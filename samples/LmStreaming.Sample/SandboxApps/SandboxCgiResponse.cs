using System.Text;

namespace LmStreaming.Sample.SandboxApps;

/// <summary>Accepts a small CGI header block, then writes stdout body bytes as they arrive.</summary>
public sealed class SandboxCgiResponse(HttpResponse response)
{
    private const int MaxHeaderBytes = 8192;
    private readonly MemoryStream _header = new();
    private bool _headersDone;

    public bool HasWrittenBody { get; private set; }

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (_headersDone)
        {
            if (!bytes.IsEmpty)
                HasWrittenBody = true;
            await response.Body.WriteAsync(bytes, ct);
            await response.Body.FlushAsync(ct);
            return;
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            _header.WriteByte(bytes.Span[i]);
            if (_header.Length > MaxHeaderBytes)
                throw new InvalidDataException("CGI headers exceed 8 KiB.");
            var buffer = _header.GetBuffer();
            var length = (int)_header.Length;
            if (
                (
                    length >= 4
                    && buffer[length - 4] == 13
                    && buffer[length - 3] == 10
                    && buffer[length - 2] == 13
                    && buffer[length - 1] == 10
                ) || (length >= 2 && buffer[length - 2] == 10 && buffer[length - 1] == 10)
            )
            {
                ParseHeaders();
                _headersDone = true;
                var remaining = bytes[(i + 1)..];
                if (!remaining.IsEmpty)
                {
                    HasWrittenBody = true;
                    await response.Body.WriteAsync(remaining, ct);
                    await response.Body.FlushAsync(ct);
                }
                break;
            }
        }
    }

    public Task CompleteAsync(CancellationToken ct)
    {
        if (!_headersDone)
            throw new InvalidDataException("CGI response ended before its headers.");
        return Task.CompletedTask;
    }

    private void ParseHeaders()
    {
        var raw = _header.ToArray();
        if (raw.Any(b => b > 127 || b == 0))
            throw new InvalidDataException("CGI headers must be ASCII.");
        var lines = Encoding
            .ASCII.GetString(raw)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int status = 200;
        string? contentType = null;
        string? location = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                throw new InvalidDataException("Malformed CGI header.");
            var name = line[..colon];
            var value = line[(colon + 1)..].Trim();
            if (!seen.Add(name) || value.Contains('\r') || value.Contains('\n'))
                throw new InvalidDataException("Duplicate or multiline CGI header.");
            switch (name.ToLowerInvariant())
            {
                case "status":
                    if (
                        value.Length < 3
                        || !int.TryParse(value[..3], out status)
                        || status is < 200 or > 599
                        || (value.Length > 3 && value[3] != ' ')
                    )
                        throw new InvalidDataException("Invalid CGI status.");
                    break;
                case "content-type":
                    if (value.Length is < 3 or > 128 || value.Any(char.IsControl))
                        throw new InvalidDataException("Invalid CGI content type.");
                    contentType = value;
                    break;
                case "location":
                    if (
                        !value.StartsWith("/", StringComparison.Ordinal)
                        || value.StartsWith("//", StringComparison.Ordinal)
                        || value.Contains('\\')
                        || value.Any(char.IsControl)
                    )
                        throw new InvalidDataException("CGI redirect must stay on the app origin.");
                    location = value;
                    break;
                default:
                    throw new InvalidDataException("CGI response header is not permitted.");
            }
        }
        if (contentType is null)
            throw new InvalidDataException("CGI response is missing Content-Type.");
        response.StatusCode = status;
        response.ContentType = contentType;
        if (location is not null)
            response.Headers.Location = location;
    }
}
