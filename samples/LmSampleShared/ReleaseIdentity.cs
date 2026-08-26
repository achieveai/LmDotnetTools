using System.Security.Cryptography;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmSampleShared.Release;

public sealed record ReleaseIdentity(
    string ReleaseId,
    string SourceContentSha256,
    string BaseCommit,
    bool IsDirty,
    int HostApiContractVersion,
    int DatabaseSchemaMinimum,
    int DatabaseSchemaMaximum,
    IReadOnlyList<string> Capabilities,
    bool IsDevelopment = false
)
{
    public const int CurrentHostApiContractVersion = 1;

    public static ReleaseIdentity Development(string component) =>
        new(
            $"development:{component}",
            "development",
            "development",
            true,
            CurrentHostApiContractVersion,
            0,
            int.MaxValue,
            ReleaseCapabilities.Required,
            true
        );
}

public static class ReleaseCapabilities
{
    public static readonly string[] Required =
    [
        "collaboration",
        "message-idempotency",
        "spawn-suppression",
        "recursive-subagents",
        "per-turn-model-override",
    ];
}

public sealed record ReleaseArtifact(string Path, string Sha256, string Component);

public sealed record ReleaseManifest(
    int FormatVersion,
    ReleaseIdentity Identity,
    IReadOnlyList<ReleaseArtifact> Artifacts,
    DateTimeOffset VerifiedAtUtc,
    string VerificationPolicy
);

public static class ReleaseManifestVerifier
{
    public const int CurrentManifestFormatVersion = 2;
    public const string ManifestFileName = "manifest.json";
    public const string DevelopmentIdentityFlag = "--development-identity";
    public const string VerifyReleaseFlag = "--verify-release";

    public static bool TryRunSelfCheck(
        string component,
        string baseDirectory,
        IReadOnlyList<string> arguments,
        out int exitCode
    )
    {
        ArgumentNullException.ThrowIfNull(arguments);
        exitCode = 0;
        var index = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (string.Equals(arguments[i], VerifyReleaseFlag, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            return false;
        }

        if (index + 2 != arguments.Count)
        {
            Console.Error.WriteLine($"Usage: {VerifyReleaseFlag} <manifest-path>");
            exitCode = 2;
            return true;
        }

        try
        {
            var identity = LoadAndVerify(component, baseDirectory, developmentMode: false, arguments[index + 1]);
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        status = "verified",
                        component,
                        releaseId = identity.ReleaseId,
                        sourceContentSha256 = identity.SourceContentSha256,
                        manifestFormatVersion = CurrentManifestFormatVersion,
                    }
                )
            );
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Release self-check failed: {ex.Message}");
            exitCode = 1;
            return true;
        }
    }

    public static bool IsDevelopmentIdentityAllowed(
        string baseDirectory,
        IEnumerable<string> arguments,
        bool allowTestHostSwitch = false
    )
    {
        var explicitlyRequested = arguments.Contains(DevelopmentIdentityFlag, StringComparer.Ordinal);
        var testHostRequested =
            allowTestHostSwitch
            && AppContext.TryGetSwitch(
                "LmDotnetTools.ReleaseIdentity.TestHostDevelopmentIdentity",
                out var testHostEnabled
            )
            && testHostEnabled;
        if (!explicitlyRequested && !testHostRequested)
        {
            return false;
        }

        var fullBase = Path.GetFullPath(baseDirectory);
        var segments = fullBase.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase))
            && segments.Any(segment =>
                string.Equals(segment, "Debug", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "Release", StringComparison.OrdinalIgnoreCase)
            );
    }

    public static ReleaseIdentity LoadAndVerify(
        string component,
        string baseDirectory,
        bool developmentMode,
        string? manifestPath = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        if (developmentMode)
        {
            return ReleaseIdentity.Development(component);
        }

        manifestPath ??= Environment.GetEnvironmentVariable("CODEREVIEW_RELEASE_MANIFEST");
        manifestPath = string.IsNullOrWhiteSpace(manifestPath)
            ? Path.Combine(Directory.GetParent(baseDirectory)?.FullName ?? baseDirectory, ManifestFileName)
            : Path.GetFullPath(manifestPath);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"Immutable release mode requires an adjacent {ManifestFileName}; not found at '{manifestPath}'."
            );
        }

        ReleaseManifest manifest;
        try
        {
            var manifestJson = File.ReadAllText(manifestPath);
            ValidateContract(manifestJson);
            manifest =
                JsonSerializer.Deserialize<ReleaseManifest>(
                    manifestJson,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
                ) ?? throw new InvalidOperationException("Release manifest was empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Release manifest is malformed.", ex);
        }

        if (
            manifest.FormatVersion != CurrentManifestFormatVersion
            || string.IsNullOrWhiteSpace(manifest.Identity.ReleaseId)
            || string.IsNullOrWhiteSpace(manifest.Identity.SourceContentSha256)
        )
        {
            throw new InvalidOperationException("Release manifest identity or format is invalid.");
        }

        var releaseRoot = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        RejectReparsePoints(releaseRoot, Path.GetFullPath(manifestPath));
        var artifacts = manifest
            .Artifacts.Where(a => string.Equals(a.Component, component, StringComparison.Ordinal))
            .ToArray();
        if (artifacts.Length == 0)
        {
            throw new InvalidOperationException($"Release manifest contains no artifacts for component '{component}'.");
        }

        foreach (var artifact in artifacts)
        {
            if (
                Path.IsPathRooted(artifact.Path)
                || artifact.Path.Contains('\\', StringComparison.Ordinal)
                || artifact.Path.Split('/').Any(segment => segment is "" or "." or "..")
            )
            {
                throw new InvalidOperationException($"Release artifact path '{artifact.Path}' is not canonical.");
            }

            var fullPath = Path.GetFullPath(Path.Combine(releaseRoot, artifact.Path));
            if (
                !fullPath.StartsWith(releaseRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(fullPath)
            )
            {
                throw new InvalidOperationException(
                    $"Release artifact '{artifact.Path}' is missing or outside the release."
                );
            }

            RejectReparsePoints(releaseRoot, fullPath);
            if ((File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
            {
                throw new InvalidOperationException($"Release artifact '{artifact.Path}' is not a regular file.");
            }

            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan
            );
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actual),
                    Convert.FromHexString(artifact.Sha256)
                )
            )
            {
                throw new InvalidOperationException($"Release artifact hash mismatch for '{artifact.Path}'.");
            }
        }

        return manifest.Identity;
    }

    private static void ValidateContract(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireFields(
            root,
            ["formatVersion", "identity", "artifacts", "verifiedAtUtc", "verificationPolicy"],
            [
                "formatVersion",
                "identity",
                "artifacts",
                "verifiedAtUtc",
                "verificationPolicy",
                "verificationPolicySha256",
                "verificationSha256",
                "sourceInventorySha256",
            ]
        );
        var identity = root.GetProperty("identity");
        RequireFields(
            identity,
            [
                "releaseId",
                "sourceContentSha256",
                "baseCommit",
                "isDirty",
                "hostApiContractVersion",
                "databaseSchemaMinimum",
                "databaseSchemaMaximum",
                "capabilities",
            ],
            [
                "releaseId",
                "sourceContentSha256",
                "baseCommit",
                "isDirty",
                "hostApiContractVersion",
                "databaseSchemaMinimum",
                "databaseSchemaMaximum",
                "capabilities",
            ]
        );
        foreach (var artifact in root.GetProperty("artifacts").EnumerateArray())
        {
            RequireFields(artifact, ["path", "sha256", "component"], ["path", "sha256", "component"]);
            var hash = artifact.GetProperty("sha256").GetString();
            if (hash is null || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
            {
                throw new InvalidOperationException(
                    "Release artifact hash must be 64 lowercase hexadecimal characters."
                );
            }
        }
    }

    private static void RequireFields(JsonElement element, string[] required, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Release manifest contract expected an object.");
        }

        var expected = required.ToHashSet(StringComparer.Ordinal);
        var accepted = allowed.ToHashSet(StringComparer.Ordinal);
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        var missing = expected.Except(actual, StringComparer.Ordinal).ToArray();
        var unknown = actual.Except(accepted, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"Release manifest fields are invalid (missing: {string.Join(',', missing)}; unknown/cased: {string.Join(',', unknown)})."
            );
        }
    }

    private static void RejectReparsePoints(string root, string path)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var pathFull = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(rootFull, pathFull);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(segment => segment == ".."))
        {
            throw new InvalidOperationException($"Release path '{path}' is outside the release.");
        }

        var current = rootFull;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Release path '{path}' contains a symbolic link.");
            }
        }
    }
}
