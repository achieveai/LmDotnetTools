using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Capabilities;

/// <summary>
///     Represents the multimodal capabilities of a model.
/// </summary>
public record MultimodalCapability
{
    /// <summary>
    ///     Whether the model supports image inputs.
    /// </summary>
    [JsonPropertyName("supports_images")]
    public bool SupportsImages { get; init; } = false;

    /// <summary>
    ///     Whether the model supports audio inputs.
    /// </summary>
    [JsonPropertyName("supports_audio")]
    public bool SupportsAudio { get; init; } = false;

    /// <summary>
    ///     Whether the model supports video inputs.
    /// </summary>
    [JsonPropertyName("supports_video")]
    public bool SupportsVideo { get; init; } = false;

    /// <summary>
    ///     Supported image formats (e.g., "jpeg", "png", "webp", "gif").
    /// </summary>
    [JsonPropertyName("supported_image_formats")]
    public IReadOnlyList<string> SupportedImageFormats { get; init; } = [];

    /// <summary>
    ///     Supported audio formats (e.g., "mp3", "wav", "m4a").
    /// </summary>
    [JsonPropertyName("supported_audio_formats")]
    public IReadOnlyList<string> SupportedAudioFormats { get; init; } = [];

    /// <summary>
    ///     Supported video formats (e.g., "mp4", "avi", "mov").
    /// </summary>
    [JsonPropertyName("supported_video_formats")]
    public IReadOnlyList<string> SupportedVideoFormats { get; init; } = [];

    /// <summary>
    ///     Maximum size in bytes for image files.
    /// </summary>
    [JsonPropertyName("max_image_size")]
    public long? MaxImageSize { get; init; }

    /// <summary>
    ///     Maximum size in bytes for audio files.
    /// </summary>
    [JsonPropertyName("max_audio_size")]
    public long? MaxAudioSize { get; init; }

    /// <summary>
    ///     Maximum size in bytes for video files.
    /// </summary>
    [JsonPropertyName("max_video_size")]
    public long? MaxVideoSize { get; init; }

    /// <summary>
    ///     Maximum number of images that can be included in a single message.
    /// </summary>
    [JsonPropertyName("max_images_per_message")]
    public int? MaxImagesPerMessage { get; init; }

    /// <summary>
    ///     Maximum number of audio files that can be included in a single message.
    /// </summary>
    [JsonPropertyName("max_audio_per_message")]
    public int? MaxAudioPerMessage { get; init; }

    /// <summary>
    ///     Maximum number of video files that can be included in a single message.
    /// </summary>
    [JsonPropertyName("max_video_per_message")]
    public int? MaxVideoPerMessage { get; init; }

    /// <summary>
    ///     Maximum total size in bytes for all media files in a single message.
    /// </summary>
    [JsonPropertyName("max_total_media_size")]
    public long? MaxTotalMediaSize { get; init; }

    /// <summary>
    ///     Whether the model supports generating images.
    /// </summary>
    [JsonPropertyName("supports_image_generation")]
    public bool SupportsImageGeneration { get; init; } = false;

    /// <summary>
    ///     Whether the model supports generating audio.
    /// </summary>
    [JsonPropertyName("supports_audio_generation")]
    public bool SupportsAudioGeneration { get; init; } = false;

    /// <summary>
    ///     Whether the model supports generating video.
    /// </summary>
    [JsonPropertyName("supports_video_generation")]
    public bool SupportsVideoGeneration { get; init; } = false;
}
