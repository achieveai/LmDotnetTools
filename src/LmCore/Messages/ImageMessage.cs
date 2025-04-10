using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

public class ImageMessage : IMessage, ICanGetBinary, ICanGetText
{
    public string? FromAgent { get; set; }

    public Role Role { get; set; }
    
    public JsonObject? Metadata { get; set; }
    
    public string? GenerationId { get; set; }
    
    public required BinaryData ImageData { get; init; }
    
    public string? GetText() => ImageData.ToDataUrl();
    
    public BinaryData? GetBinary() => ImageData;
    
    public ToolCall? GetToolCalls() => null;
    
    public IEnumerable<IMessage>? GetMessages() => null;
}

public class ImageMessageBuilder : IMessageBuilder<ImageMessage, ImageMessage>
{
    public string? FromAgent { get; init; }

    public Role Role { get; init; }
    
    public JsonObject? Metadata { get; private set; }

    public string? GenerationId { get; init; }

    public List<BinaryData> ImageData { get; init; } = new List<BinaryData>();

    IMessage IMessageBuilder.Build()
    {
        return this.Build();
    }

    public void Add(ImageMessage streamingMessageUpdate)
    {
        ImageData.Add(streamingMessageUpdate.ImageData);
        
        // Merge metadata from the update
        if (streamingMessageUpdate.Metadata != null)
        {
            if (Metadata == null)
            {
                Metadata = streamingMessageUpdate.Metadata.DeepClone() as JsonObject;
            }
            else
            {
                // Merge metadata, with update's metadata taking precedence
                foreach (var prop in streamingMessageUpdate.Metadata)
                {
                    Metadata[prop.Key] = prop.Value?.DeepClone();
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
        };
    }
}

public static class ImageMessageExtensions
{
    private static readonly Regex DataUriPattern = new Regex(@"^data:(?<mimeType>[a-zA-Z0-9/]+);base64,(?<data>.+)$", RegexOptions.Compiled);
    
    // Parse base64 data URI and convert to BinaryData with mime type
    public static BinaryData? ToBinaryDataWithMimeType(this string? dataUrl)
    {
        if (dataUrl == null)
            return null;
            
        var match = DataUriPattern.Match(dataUrl);
        if (!match.Success)
            return null;
            
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
            return null;
            
        string actualMimeType = data.MediaType ?? "application/octet-stream";
        var base64Data = Convert.ToBase64String(data.ToArray());
        return $"data:{actualMimeType};base64,{base64Data}";
    }
}