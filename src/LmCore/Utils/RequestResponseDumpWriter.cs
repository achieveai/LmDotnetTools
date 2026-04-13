using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
///     Writes optional request/response dumps to files for diagnostics.
/// </summary>
public sealed class RequestResponseDumpWriter
{
    private const int MaxRotationAttempts = 10_000;

    private readonly string _baseFileName;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger _logger;
    private bool _disableFurtherChunkWrites;
    private bool _requestPathPrepared;
    private bool _responsePathPrepared;

    private RequestResponseDumpWriter(string baseFileName, JsonSerializerOptions jsonOptions, ILogger logger)
    {
        _baseFileName = baseFileName;
        _jsonOptions = jsonOptions;
        _logger = logger;
    }

    public static RequestResponseDumpWriter? Create(
        string? baseFileName,
        JsonSerializerOptions jsonOptions,
        ILogger logger
    )
    {
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentNullException.ThrowIfNull(logger);
        return string.IsNullOrWhiteSpace(baseFileName)
            ? null
            : new RequestResponseDumpWriter(baseFileName, jsonOptions, logger);
    }

    public void WriteRequest<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        ExecuteIoBestEffort(
            () =>
            {
                PrepareRequestPathIfNeeded();
                var requestPath = GetRequestPath();
                File.WriteAllText(requestPath, json);
            },
            "request"
        );
    }

    public void WriteResponse<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        ExecuteIoBestEffort(
            () =>
            {
                PrepareResponsePathIfNeeded();
                var responsePath = GetResponsePath();
                File.WriteAllText(responsePath, json);
            },
            "response"
        );
    }

    public void AppendResponseChunk<T>(T payload)
    {
        if (_disableFurtherChunkWrites)
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        ExecuteIoBestEffort(
            () =>
            {
                PrepareResponsePathIfNeeded();
                var responsePath = GetResponsePath();
                File.AppendAllText(responsePath, json + Environment.NewLine);
            },
            "streaming response chunk",
            onFailure: ex =>
            {
                _disableFurtherChunkWrites = true;
                _logger.LogError(
                    ex,
                    "Disabling further streaming dump writes for base file name {BaseFileName} after first failure",
                    _baseFileName
                );
            }
        );
    }

    private void PrepareRequestPathIfNeeded()
    {
        if (_requestPathPrepared)
        {
            return;
        }

        PrepareTargetPath(GetRequestPath(), "request");
        _requestPathPrepared = true;
    }

    private void PrepareResponsePathIfNeeded()
    {
        if (_responsePathPrepared)
        {
            return;
        }

        PrepareTargetPath(GetResponsePath(), "response");
        _responsePathPrepared = true;
    }

    private void PrepareTargetPath(string targetPath, string suffix)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        if (!File.Exists(targetPath))
        {
            return;
        }

        for (var nextIndex = 1; nextIndex <= MaxRotationAttempts; nextIndex++)
        {
            var rotatedPath = $"{_baseFileName}.{nextIndex}.{suffix}.txt";
            if (File.Exists(rotatedPath))
            {
                continue;
            }

            File.Move(targetPath, rotatedPath);
            return;
        }

        throw new IOException(
            $"Failed to rotate dump file '{targetPath}' after {MaxRotationAttempts} attempts."
        );
    }

    private string GetRequestPath()
    {
        return $"{_baseFileName}.request.txt";
    }

    private string GetResponsePath()
    {
        return $"{_baseFileName}.response.txt";
    }

    private void ExecuteIoBestEffort(
        Action operation,
        string operationName,
        Action<Exception>? onFailure = null
    )
    {
        try
        {
            operation();
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or PathTooLongException
                or DirectoryNotFoundException
        )
        {
            onFailure?.Invoke(ex);
            _logger.LogWarning(
                ex,
                "Failed to write {OperationName} dump for base file name {BaseFileName}",
                operationName,
                _baseFileName
            );
        }
    }
}
