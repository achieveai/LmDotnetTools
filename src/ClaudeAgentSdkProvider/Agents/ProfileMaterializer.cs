using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;

/// <summary>
///     Result of materializing an <see cref="AgentRuntimeProfile"/> into a staging
///     directory the Claude CLI can discover.
/// </summary>
public sealed record MaterializedProfile : IDisposable
{
    /// <summary>
    ///     Absolute path to the staging directory. Pass as <c>CLAUDE_CONFIG_DIR</c>
    ///     on the child process so the CLI sees the staged skills/agents.
    ///     <c>null</c> means the profile was empty and no staging dir was created.
    /// </summary>
    public string? StagingDirectory { get; init; }

    /// <summary>
    ///     MCP servers contributed by the profile. The caller merges these into the
    ///     final MCP dictionary, with profile entries winning on key collision.
    /// </summary>
    public IReadOnlyDictionary<string, McpServerConfig> McpServers { get; init; }
        = new Dictionary<string, McpServerConfig>();

    /// <summary>
    ///     System prompt supplied by the profile, or <c>null</c> if absent. The caller
    ///     overlays this on top of any provider-level system prompt.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    ///     Deletes the staging directory on disposal.
    /// </summary>
    public void Dispose()
    {
        if (StagingDirectory is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(StagingDirectory))
            {
                Directory.Delete(StagingDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; another process may still hold a file.
        }
        catch (UnauthorizedAccessException)
        {
            // ditto
        }
    }
}

/// <summary>
///     Translates a provider-neutral <see cref="AgentRuntimeProfile"/> into the
///     filesystem layout the Claude Agent SDK CLI expects under <c>CLAUDE_CONFIG_DIR</c>.
/// </summary>
/// <remarks>
///     <para>Layout produced:</para>
///     <list type="bullet">
///         <item><description><c>&lt;staging&gt;/skills/&lt;Name&gt;/SKILL.md</c> for inline skills.</description></item>
///         <item><description><c>&lt;staging&gt;/skills/&lt;Name&gt;/</c> for directory-sourced skills (recursive copy).</description></item>
///         <item><description><c>&lt;staging&gt;/agents/&lt;Name&gt;.md</c> for both inline and file-sourced sub-agents.</description></item>
///     </list>
///     <para>
///         Note: <c>CLAUDE_CONFIG_DIR</c> replaces the <c>~/.claude/</c> root entirely.
///         The CLI's documented skill/agent directory layout (<c>~/.claude/skills/</c>,
///         <c>~/.claude/agents/</c>) becomes <c>&lt;staging&gt;/skills/</c> and
///         <c>&lt;staging&gt;/agents/</c> — no extra <c>.claude/</c> nesting.
///     </para>
/// </remarks>
public static class ProfileMaterializer
{
    /// <summary>
    ///     Materialize the profile to a fresh temp directory. Returns a disposable
    ///     handle that includes the staging directory path, the MCP dictionary the
    ///     caller should merge with host-loaded MCP, and the system prompt to overlay.
    /// </summary>
    /// <remarks>
    ///     If the profile is <c>null</c> or contributes no Skills/SubAgents/McpServers/SystemPrompt,
    ///     no staging directory is created and <see cref="MaterializedProfile.StagingDirectory"/>
    ///     is <c>null</c>. The handle is still safe to dispose.
    /// </remarks>
    public static MaterializedProfile Materialize(AgentRuntimeProfile? profile)
    {
        if (profile is null
            || (profile.Skills.Count == 0
                && profile.SubAgents.Count == 0
                && profile.McpServers.Count == 0
                && string.IsNullOrEmpty(profile.SystemPrompt)))
        {
            return new MaterializedProfile();
        }

        string? stagingDir = null;
        if (profile.Skills.Count > 0 || profile.SubAgents.Count > 0)
        {
            stagingDir = Path.Combine(Path.GetTempPath(), $"lm-claude-{Guid.NewGuid():N}");
            _ = Directory.CreateDirectory(stagingDir);
            try
            {
                MaterializeSkills(stagingDir, profile.Skills);
                MaterializeSubAgents(stagingDir, profile.SubAgents);
            }
            catch
            {
                TryDeleteDirectory(stagingDir);
                throw;
            }
        }

        var mcpCopy = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var (name, config) in profile.McpServers)
        {
            mcpCopy[name] = config;
        }

        return new MaterializedProfile
        {
            StagingDirectory = stagingDir,
            McpServers = mcpCopy,
            SystemPrompt = string.IsNullOrEmpty(profile.SystemPrompt) ? null : profile.SystemPrompt,
        };
    }

    private static void MaterializeSkills(string stagingDir, IReadOnlyList<AgentSkill> skills)
    {
        var skillsRoot = Path.Combine(stagingDir, "skills");
        _ = Directory.CreateDirectory(skillsRoot);
        foreach (var skill in skills)
        {
            ValidateName(skill.Name, "skill");
            var dir = EnsureWithinRoot(skillsRoot, Path.Combine(skillsRoot, skill.Name));
            _ = Directory.CreateDirectory(dir);
            switch (skill.Source)
            {
                case ContentSource.FromInline inline:
                    File.WriteAllText(Path.Combine(dir, "SKILL.md"), inline.Content);
                    break;
                case ContentSource.FromPath fromPath:
                    CopySkillFromPath(skill.Name, fromPath.Value, dir);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unhandled ContentSource variant for skill '{skill.Name}': {skill.Source.GetType().Name}");
            }
        }
    }

    private static void CopySkillFromPath(string skillName, string sourcePath, string destDir)
    {
        if (Directory.Exists(sourcePath))
        {
            CopyDirectory(sourcePath, destDir);
        }
        else if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, Path.Combine(destDir, "SKILL.md"), overwrite: true);
        }
        else
        {
            throw new FileNotFoundException(
                $"Skill source path not found for '{skillName}': {sourcePath}",
                sourcePath);
        }
    }

    private static void MaterializeSubAgents(string stagingDir, IReadOnlyList<SubAgentDefinition> subAgents)
    {
        var agentsRoot = Path.Combine(stagingDir, "agents");
        _ = Directory.CreateDirectory(agentsRoot);
        foreach (var subAgent in subAgents)
        {
            ValidateName(subAgent.Name, "sub-agent");
            var target = EnsureWithinRoot(agentsRoot, Path.Combine(agentsRoot, $"{subAgent.Name}.md"));
            switch (subAgent.Source)
            {
                case ContentSource.FromInline inline:
                    File.WriteAllText(target, inline.Content);
                    break;
                case ContentSource.FromPath fromPath:
                    CopySubAgentFromPath(subAgent.Name, fromPath.Value, target);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unhandled ContentSource variant for sub-agent '{subAgent.Name}': {subAgent.Source.GetType().Name}");
            }
        }
    }

    private static void CopySubAgentFromPath(string subAgentName, string sourcePath, string target)
    {
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, target, overwrite: true);
            return;
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"Sub-agent source path not found for '{subAgentName}': {sourcePath}",
                sourcePath);
        }

        var firstMd = Directory.EnumerateFiles(sourcePath, "*.md").FirstOrDefault()
            ?? throw new FileNotFoundException(
                $"Sub-agent source dir contains no .md file for '{subAgentName}': {sourcePath}");
        File.Copy(firstMd, target, overwrite: true);
    }

    private static void ValidateName(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException($"Profile {kind} name cannot be empty.", nameof(name));
        }

        if (name.Contains("..", StringComparison.Ordinal)
            || name.IndexOfAny(['/', '\\']) >= 0
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                $"Profile {kind} name '{name}' contains invalid characters or path separators.",
                nameof(name));
        }
    }

    private static string EnsureWithinRoot(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Resolved path '{fullCandidate}' escapes staging root '{fullRoot}'.");
        }
        return fullCandidate;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void CopyDirectory(string source, string dest)
    {
        _ = Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var subDir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(subDir, Path.Combine(dest, Path.GetFileName(subDir)));
        }
    }
}
