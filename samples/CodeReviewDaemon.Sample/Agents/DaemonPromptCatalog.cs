using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Prompts;
using YamlDotNet.Serialization;

namespace CodeReviewDaemon.Sample.Agents;

internal sealed record DaemonPromptDefinition(string Id, string Version, string Text, string ContentHash);

internal sealed class DaemonPromptSnapshot
{
    private readonly IReadOnlyDictionary<string, DaemonPromptDefinition> _prompts;

    internal DaemonPromptSnapshot(IReadOnlyDictionary<string, DaemonPromptDefinition> prompts)
    {
        _prompts = prompts;
    }

    public DaemonPromptDefinition GetPrompt(string id) =>
        _prompts.TryGetValue(id, out var prompt)
            ? prompt
            : throw new KeyNotFoundException($"Prompt '{id}' is not active in the daemon prompt catalog.");
}

internal sealed class DaemonPromptCatalog
{
    private readonly string _path;
    private readonly ILogger<DaemonPromptCatalog> _logger;
    private readonly object _gate = new();
    private DaemonPromptSnapshot? _lastValid;
    private string? _lastContentHash;

    public DaemonPromptCatalog(string path, ILogger<DaemonPromptCatalog> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public DaemonPromptSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            try
            {
                var yaml = File.ReadAllText(_path);
                var contentHash = Hash(yaml);
                if (_lastValid is not null && string.Equals(contentHash, _lastContentHash, StringComparison.Ordinal))
                {
                    return _lastValid;
                }

                var next = Parse(yaml);
                _lastValid = next;
                _lastContentHash = contentHash;
                _logger.LogInformation(
                    "Loaded daemon prompt catalog {PromptPath} ({ContentHash}).",
                    _path,
                    contentHash
                );
                return next;
            }
            catch (Exception ex) when (_lastValid is not null)
            {
                _logger.LogError(
                    ex,
                    "Daemon prompt catalog update {PromptPath} is invalid; retaining the last valid snapshot {ContentHash}.",
                    _path,
                    _lastContentHash
                );
                return _lastValid;
            }
        }
    }

    private static DaemonPromptSnapshot Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder().Build();
        var root =
            deserializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(yaml)
            ?? throw new InvalidOperationException("The daemon prompt catalog is empty.");
        if (!root.TryGetValue("_active", out var active) || active.Count == 0)
        {
            throw new InvalidOperationException("The daemon prompt catalog must define at least one active prompt.");
        }

        var reader = new PromptReader(new MemoryStream(Encoding.UTF8.GetBytes(yaml)));
        var prompts = new Dictionary<string, DaemonPromptDefinition>(StringComparer.Ordinal);
        foreach (var (id, version) in active)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
            {
                throw new InvalidOperationException("Every active daemon prompt must name a non-empty id and version.");
            }

            var prompt = reader.GetPrompt(id, version);
            if (string.IsNullOrWhiteSpace(prompt.Value))
            {
                throw new InvalidOperationException($"Active prompt '{id}' version '{version}' is blank.");
            }

            prompts.Add(id, new DaemonPromptDefinition(id, version, prompt.Value, Hash(prompt.Value)));
        }

        return new DaemonPromptSnapshot(prompts);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
