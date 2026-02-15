using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

[JsonConverter(typeof(ImageMessageJsonConverter))]
public class ImageMessage : IMessage, ICanGetBinary, ICanGetText
{
    [JsonPropertyName("image_data")]
    public required BinaryData ImageData { get; init; }

    /// <summary>
    ///     Gets the media type of the image (e.g., "image/jpeg", "image/png").
    ///     This property is used for JSON serialization since BinaryData.MediaType is not serialized.
    /// </summary>
    [JsonPropertyName("media_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaType
    {
        get => ImageData.MediaType;
        init
        {
            // This setter is used during deserialization to reconstruct BinaryData with MediaType
            // The actual setting happens in the JsonConverter
        }
    }

    [JsonPropertyName("parent_run_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; set; }

    public BinaryData? GetBinary()
    {
        return ImageData;
    }

    public string? GetText()
    {
        return ImageData.ToDataUrl();
    }

    [JsonPropertyName("from_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; set; }

    [JsonPropertyName("role")]
    public Role Role { get; set; }

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; set; }

    [JsonPropertyName("generation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; set; }

    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; set; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; set; }

    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; set; }

    public static ToolCall? GetToolCalls()
    {
        return null;
    }

    public static IEnumerable<IMessage>? GetMessages()
    {
        return null;
    }
}

public class ImageMessageJsonConverter : ShadowPropertiesJsonConverter<ImageMessage>
{
    private string? _pendingMediaType;
    private byte[]? _pendingImageData;

    protected override ImageMessage CreateInstance()
    {
        // Create with empty data; will be replaced if image_data is read
        return new ImageMessage { ImageData = BinaryData.FromString("") };
    }

    protected override (bool handled, ImageMessage instance) ReadProperty(
        ref Utf8JsonReader reader,
        ImageMessage instance,
        string propertyName,
        JsonSerializerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(instance);

        switch (propertyName)
        {
            case "image_data":
                // Read base64 bytes
                _pendingImageData = reader.GetBytesFromBase64();
                // If we already have a media type, create the BinaryData now
                if (_pendingMediaType != null)
                {
                    instance = CreateNewInstance(instance, BinaryData.FromBytes(_pendingImageData, _pendingMediaType));
                    _pendingImageData = null;
                    _pendingMediaType = null;
                }
                else
                {
                    // Create with default media type, may be updated when media_type is read
                    instance = CreateNewInstance(instance, BinaryData.FromBytes(_pendingImageData));
                }

                return (true, instance);

            case "media_type":
                _pendingMediaType = reader.GetString();
                // If we already have image data, recreate BinaryData with the media type
                if (_pendingImageData != null && _pendingMediaType != null)
                {
                    instance = CreateNewInstance(instance, BinaryData.FromBytes(_pendingImageData, _pendingMediaType));
                    _pendingImageData = null;
                    _pendingMediaType = null;
                }
                else if (instance.ImageData.Length > 0 && _pendingMediaType != null)
                {
                    // Recreate with media type if image_data was already processed
                    instance = CreateNewInstance(instance, BinaryData.FromBytes(instance.ImageData.ToArray(), _pendingMediaType));
                    _pendingMediaType = null;
                }

                return (true, instance);

            default:
                return base.ReadProperty(ref reader, instance, propertyName, options);
        }
    }

    protected override void WriteProperties(Utf8JsonWriter writer, ImageMessage value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        // Write image_data as base64
        writer.WriteBase64String("image_data", value.ImageData.ToMemory().Span);

        // Write media_type if available
        if (value.ImageData.MediaType != null)
        {
            writer.WriteString("media_type", value.ImageData.MediaType);
        }
    }

    private static ImageMessage CreateNewInstance(ImageMessage source, BinaryData newImageData)
    {
        return new ImageMessage
        {
            ImageData = newImageData,
            FromAgent = source.FromAgent,
            Role = source.Role,
            Metadata = source.Metadata,
            GenerationId = source.GenerationId,
            ThreadId = source.ThreadId,
            RunId = source.RunId,
            ParentRunId = source.ParentRunId,
            MessageOrderIdx = source.MessageOrderIdx,
        };
    }
}

public class ImageMessageBuilder : IMessageBuilder<ImageMessage, ImageMessage>
{
    public ImmutableDictionary<string, object>? Metadata { get; private set; }

    public string? GenerationId { get; init; }

    public List<BinaryData> ImageData { get; init; } = [];

    public string? ThreadId { get; init; }

    public string? RunId { get; init; }

    public int? MessageOrderIdx { get; init; }
    public string? FromAgent { get; init; }

    public Role Role { get; init; }

    IMessage IMessageBuilder.Build()
    {
        return Build();
    }

    public void Add(ImageMessage streamingMessageUpdate)
    {
        ArgumentNullException.ThrowIfNull(streamingMessageUpdate);
        ImageData.Add(streamingMessageUpdate.ImageData);

        // Merge metadata from the update
        if (streamingMessageUpdate.Metadata != null)
        {
            if (Metadata == null)
            {
                Metadata = streamingMessageUpdate.Metadata;
            }
            else
            {
                // Merge metadata, with update's metadata taking precedence
                foreach (var prop in streamingMessageUpdate.Metadata)
                {
                    Metadata = Metadata.Add(prop.Key, prop.Value);
                }
            }
        }
    }

    public ImageMessage Build()
    {
        var mimeType = ImageData[0].MediaType;
        var totalLength = ImageData.Sum(b => b.Length);
        var combinedBytes = new byte[totalLength];
        var offset = 0;
        foreach (var data in ImageData)
        {
            Buffer.BlockCopy(data.ToArray(), 0, combinedBytes, offset, data.Length);
            offset += data.Length;
        }

        return new ImageMessage
        {
            FromAgent = FromAgent,
            Role = Role,
            Metadata = Metadata,
            GenerationId = GenerationId,
            ImageData = BinaryData.FromBytes(combinedBytes, mimeType),
            ThreadId = ThreadId,
            RunId = RunId,
            MessageOrderIdx = MessageOrderIdx,
        };
    }
}

public static partial class ImageMessageExtensions
{
    private static readonly Regex DataUriPattern = MyRegex();

    // Parse base64 data URI and convert to BinaryData with mime type
    public static BinaryData? ToBinaryDataWithMimeType(this string? dataUrl)
    {
        if (dataUrl == null)
        {
            return null;
        }

        var match = DataUriPattern.Match(dataUrl);
        if (!match.Success)
        {
            return null;
        }

        var mimeType = match.Groups["mimeType"].Value;
        var base64Data = match.Groups["data"].Value;

        try
        {
            var bytes = Convert.FromBase64String(base64Data);
            return BinaryData.FromBytes(bytes, mimeType);
        }
        catch
        {
            return null;
        }
    }

    // Create BinaryData with explicit mime type
    public static BinaryData CreateBinaryData(byte[] imageData, string mimeType)
    {
        return BinaryData.FromBytes(imageData, mimeType);
    }

    // Create a data URL from BinaryData
    public static string? ToDataUrl(this BinaryData? data)
    {
        if (data == null)
        {
            return null;
        }

        var actualMimeType = data.MediaType ?? "application/octet-stream";
        var base64Data = Convert.ToBase64String(data.ToArray());
        return $"data:{actualMimeType};base64,{base64Data}";
    }

    [GeneratedRegex(@"^data:(?<mimeType>[a-zA-Z0-9/]+);base64,(?<data>.+)$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
