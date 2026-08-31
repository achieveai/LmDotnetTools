using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.McpMiddleware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

/// <summary>
///     Regression tests for image extraction from MCP tool results.
///     <para>
///         <see cref="ImageContentBlock.Data" /> holds base64 <em>text</em> (as UTF-8 bytes), not raw
///         image bytes. Constructing a block by assigning raw bytes to <c>Data</c> puts non-UTF-8 bytes
///         on the wire, where they are lossily replaced with U+FFFD and the image cannot be recovered.
///         These tests pin the correct producer (<see cref="ImageContentBlock.FromBytes" />) and consumer
///         (<c>DecodedData</c>) sides across a JSON round-trip.
///     </para>
/// </summary>
public class McpImageBlockExtractionTests
{
    /// <summary>PNG magic number followed by bytes that are not valid UTF-8 on their own.</summary>
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0x10, 0x42];

    private static readonly ILogger<McpClientFunctionProvider> Logger = NullLogger<McpClientFunctionProvider>.Instance;

    [Fact]
    public void FromBytes_SerializesAsBase64_WithoutLossyReplacement()
    {
        // Arrange
        var result = new CallToolResult { Content = [ImageContentBlock.FromBytes(PngBytes, "image/png")] };

        // Act
        var json = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
        var data = JsonDocument.Parse(json).RootElement.GetProperty("content")[0].GetProperty("data").GetString();

        // Assert - the wire form is base64 text, not raw bytes mangled into U+FFFD
        Assert.NotNull(data);
        Assert.DoesNotContain('�', data);
        Assert.Equal(Convert.ToBase64String(PngBytes), data);
    }

    [Fact]
    public void ExtractImageBlocks_AfterJsonRoundTrip_ProducesExactlyOneBase64Encoding()
    {
        // Arrange - a result that has crossed the wire, as it would from a real MCP server
        var result = RoundTrip(new CallToolResult { Content = [ImageContentBlock.FromBytes(PngBytes, "image/png")] });

        // Act
        var blocks = McpClientFunctionProvider.ExtractImageBlocks(result, "TestTool", Logger);

        // Assert - single encoding: double-encoding would yield base64 of the base64 text
        var image = Assert.IsType<ImageToolResultBlock>(Assert.Single(blocks));
        Assert.Equal(Convert.ToBase64String(PngBytes), image.Data);
        Assert.Equal(PngBytes, Convert.FromBase64String(image.Data));
        Assert.Equal("image/png", image.MimeType);
    }

    [Fact]
    public void ExtractImageBlocks_EmptyData_SkipsBlock()
    {
        // Arrange
        var result = RoundTrip(
            new CallToolResult { Content = [ImageContentBlock.FromBytes(ReadOnlyMemory<byte>.Empty, "image/png")] }
        );

        // Act
        var blocks = McpClientFunctionProvider.ExtractImageBlocks(result, "TestTool", Logger);

        // Assert
        Assert.Empty(blocks);
    }

    [Fact]
    public void ExtractImageBlocks_RawBytesInDataField_SkipsBlockWithoutThrowing()
    {
        // Arrange - the version-skew case: an old server that assigned raw bytes to Data.
        // The bytes are not valid base64, so decoding must fail softly rather than tear down
        // the whole tool result.
        var result = new CallToolResult
        {
            Content = [new ImageContentBlock { Data = PngBytes, MimeType = "image/png" }],
        };

        // Act
        var blocks = McpClientFunctionProvider.ExtractImageBlocks(result, "TestTool", Logger);

        // Assert
        Assert.Empty(blocks);
    }

    [Fact]
    public void BareImageContentBlock_WithSdkOptions_DoubleEncodesData()
    {
        // Arrange - pins the SDK trap this class guards against. ContentBlock.Converter is attached
        // to the base type, so naming the derived type as the declared type misses it and Data
        // (already base64 text) is base64-encoded a second time.
        var block = ImageContentBlock.FromBytes(PngBytes, "image/png");

        // Act
        var json = JsonSerializer.Serialize(block, McpJsonUtilities.DefaultOptions);
        var data = JsonDocument.Parse(json).RootElement.GetProperty("data").GetString();

        // Assert
        Assert.NotEqual(Convert.ToBase64String(PngBytes), data);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToBase64String(PngBytes))), data);
    }

    [Fact]
    public void BareImageContentBlock_WithMcpContentJson_MatchesTheEnvelopeWireForm()
    {
        // Arrange
        var block = ImageContentBlock.FromBytes(PngBytes, "image/png");

        // Act
        var bare = JsonSerializer.Serialize(block, McpContentJson.DefaultOptions);
        var envelope = JsonSerializer.Serialize(
            new CallToolResult { Content = [block] },
            McpContentJson.DefaultOptions
        );

        // Assert - the derived declared type now produces exactly what the base declared type does
        var bareData = JsonDocument.Parse(bare).RootElement.GetProperty("data").GetString();
        var envelopeData = JsonDocument
            .Parse(envelope)
            .RootElement.GetProperty("content")[0]
            .GetProperty("data")
            .GetString();

        Assert.Equal(Convert.ToBase64String(PngBytes), bareData);
        Assert.Equal(bareData, envelopeData);
    }

    [Fact]
    public void BareImageContentBlock_WithMcpContentJson_RoundTripsSpecForm()
    {
        // Arrange - spec-form JSON as a non-.NET peer would send it
        var json = $$"""
            {"type":"image","data":"{{Convert.ToBase64String(PngBytes)}}","mimeType":"image/png"}
            """;

        // Act
        var block = JsonSerializer.Deserialize<ImageContentBlock>(json, McpContentJson.DefaultOptions)!;

        // Assert - DecodedData would throw FormatException under the SDK options
        Assert.Equal(PngBytes, block.DecodedData.ToArray());
        Assert.Equal("image/png", block.MimeType);
    }

    private static CallToolResult RoundTrip(CallToolResult result)
    {
        var json = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);

        return JsonSerializer.Deserialize<CallToolResult>(json, McpJsonUtilities.DefaultOptions)!;
    }
}
