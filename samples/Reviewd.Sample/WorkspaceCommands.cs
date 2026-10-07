using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Agents;

namespace Reviewd.Sample;

internal static class WorkspaceCommands
{
    public static async Task<int> RunAsync(CliOptions options, ReviewdConfig config, CancellationToken ct)
    {
        var client = config.CreateS2SClient();
        if (options.Command is "workspace create" or "workspace edit" or "workspace plugins")
        {
            return await ManageWorkspaceAsync(options, client, ct);
        }
        var workspaces = await client.ListWorkspacesAsync(ct);
        if (options.Command == "workspace list")
        {
            Console.WriteLine("Registered host workspace catalog (not live container inventory).");
            foreach (var workspace in workspaces)
            {
                Console.WriteLine($"{workspace.Id}\t{workspace.Name}\t{workspace.DirectoryRelPath}");
            }
            return 0;
        }

        var workspaceId = options.Required("workspace");
        var selected =
            workspaces.SingleOrDefault(w => string.Equals(w.Id, workspaceId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Workspace '{workspaceId}' is not in the host catalog.");
        var directory = options.Get("directory") ?? ".";
        var timeoutText = options.Get("timeout") ?? "120";
        if (!int.TryParse(timeoutText, out var timeout) || timeout <= 0 || timeout > 86400)
        {
            throw new ArgumentException("--timeout must be between 1 and 86400 seconds.");
        }
        if (options.Command == "agent start")
        {
            return await StartAgentAsync(
                options,
                config,
                client,
                selected,
                options.Has("timeout") ? timeout : null,
                ct
            );
        }
        if (options.Command != "workspace exec")
        {
            throw new ArgumentException($"Unsupported workspace command '{options.Command}'.");
        }

        // Construct before creating a remote session: argv and cwd validation must fail without effects.
        var command = new SandboxCommand(options.Argv, directory);
        using var sandbox = config.CreateSandboxClient();
        var session = await sandbox.CreateAsync(
            new SandboxCreateRequest(
                selected.DirectoryRelPath,
                selected.Marketplaces,
                pluginSelection: selected.PluginSelection
            ),
            ct
        );
        Console.Error.WriteLine(
            "Local cancellation may leave the remote command running; the workspace/session is not deleted."
        );
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(timeout));
        SandboxCommandResult result;
        try
        {
            result = await sandbox.ExecuteAsync(session.SessionId, command, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
        {
            Console.Error.WriteLine("Command wait timed out. The remote command may still be running.");
            return 1;
        }
        Console.Out.Write(result.StandardOutput);
        Console.Error.Write(result.StandardError);
        return result.ExitCode;
    }

    private static async Task<int> ManageWorkspaceAsync(
        CliOptions options,
        LmStreamingS2SClient client,
        CancellationToken ct
    )
    {
        S2SWorkspace? workspace = options.Get("workspace") is { } id ? await client.GetWorkspaceAsync(id, ct) : null;
        var marketplaces = options.Get("marketplaces") is { } csv
            ? csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : workspace?.Marketplaces ?? [];
        if (options.Command == "workspace plugins")
        {
            var catalog = await client.GetMarketplaceCatalogAsync(workspace?.Marketplaces, ct);
            if (workspace is not null)
            {
                PrintWorkspace(workspace);
            }
            Console.WriteLine(catalog.GetRawText());
            Console.WriteLine(
                "Catalog lists available plugins; workspace selection is configured enablement, not proof of loaded plugins."
            );
            return 0;
        }

        var replaceSelection = options.Has("enabled-plugins");
        IReadOnlyList<SandboxPluginRef>? selection = null;
        if (replaceSelection)
        {
            using var document = JsonDocument.Parse(options.Required("enabled-plugins"));
            if (document.RootElement.ValueKind != JsonValueKind.Null)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new ArgumentException(
                        "--enabled-plugins must be null or an array of marketplace/plugin pairs."
                    );
                }
                selection = document
                    .RootElement.EnumerateArray()
                    .Select(item => new SandboxPluginRef(
                        item.GetProperty("marketplace").GetString()!,
                        item.GetProperty("plugin").GetString()!
                    ))
                    .ToArray();
            }
            var catalog = await client.GetMarketplaceCatalogAsync(marketplaces, ct);
            if (
                !catalog.TryGetProperty("capabilities", out var capabilities)
                || !capabilities.TryGetProperty("pluginFiltering", out var filtering)
                || filtering.ValueKind != JsonValueKind.True
            )
            {
                throw new InvalidOperationException(
                    "Gateway plugin filtering is unavailable; no workspace change was sent."
                );
            }
        }

        if (options.Command == "workspace create")
        {
            var created = await client.CreateWorkspaceAsync(
                options.Required("name"),
                options.Get("directory") ?? "",
                marketplaces,
                ct,
                selection
            );
            PrintWorkspace(created);
            return 0;
        }

        if (workspace is null)
        {
            throw new ArgumentException("--workspace is required.");
        }
        if (workspace.IsSystemDefined)
        {
            throw new ArgumentException("System workspaces cannot be edited.");
        }
        var updated = await client.UpdateWorkspaceAsync(workspace, marketplaces, replaceSelection, selection, ct);
        PrintWorkspace(updated);
        Console.Error.WriteLine(
            "Enabled-plugin changes can replace active sandbox sessions. Revision conflicts are not retried."
        );
        return 0;
    }

    private static void PrintWorkspace(S2SWorkspace workspace) =>
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    workspace.Id,
                    workspace.Name,
                    workspace.DirectoryRelPath,
                    workspace.Marketplaces,
                    enabledPlugins = workspace.PluginSelection,
                    workspace.PluginsRevision,
                    workspace.IsSystemDefined,
                }
            )
        );

    internal static async Task<int> StartAgentAsync(
        CliOptions options,
        ReviewdConfig config,
        LmStreamingS2SClient client,
        S2SWorkspace workspace,
        int? timeout,
        CancellationToken ct,
        Func<CancellationToken, Task>? pollDelay = null
    )
    {
        var provider = options.Required("provider");
        var prompt = options.Required("prompt");
        var mode = options.Get("mode") ?? config.ModeId;
        var directory = options.Get("directory");
        if (directory is not null)
        {
            _ = new SandboxCommand(["pwd"], directory);
        }
        var repo = options.Get("repo") is { } repoText ? PrCommands.ParseRepo(repoText) : null;
        var database = options.Get("db") ?? "reviewd.db";
        if (
            repo is not null
            && await PrThreadBindings.GetAsync(database, repo, options.Required("pr"), ct) is { } existing
        )
        {
            throw new InvalidOperationException(
                $"PR {options.Required("pr")} already has conversation '{existing}'; no new agent was submitted."
            );
        }
        var threadId = await client.ProvisionAsync(workspace.Id, provider, mode, null, null, null, directory, ct);
        Console.WriteLine($"threadId\t{threadId}");
        if (repo is not null)
        {
            await PrThreadBindings.SetAsync(database, repo, options.Required("pr"), threadId, ct);
            Console.WriteLine($"prThread\t{repo.DisplayName}\t{options.Required("pr")}\t{threadId}");
        }
        Console.Error.WriteLine(
            "Local cancellation may leave the remote agent running; the conversation/workspace is not deleted."
        );
        var inputId = await client.SendMessageAsync(threadId, prompt, false, null, ct);
        Console.WriteLine($"inputId\t{inputId}");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } seconds)
        {
            budget.CancelAfter(TimeSpan.FromSeconds(seconds));
        }
        pollDelay ??= token => Task.Delay(TimeSpan.FromSeconds(1), token);
        ReviewSubAgentTreeSnapshot? candidate = null;
        try
        {
            while (true)
            {
                var status = await client.GetStatusByInputIdAsync(threadId, inputId, budget.Token);
                switch (status.Status.ToLowerInvariant())
                {
                    case "completed":
                    case "errored":
                    case "interrupted":
                        var tree = await client.GetSubAgentTreeAsync(threadId, budget.Token);
                        var nodes = tree
                            .Nodes.OrderBy(node => node.Depth)
                            .ThenBy(node => node.ParentThreadId, StringComparer.Ordinal)
                            .ThenBy(node => node.AgentId, StringComparer.Ordinal)
                            .ToArray();
                        if (
                            nodes.Any(node =>
                                node.Status is ReviewSubAgentStatus.Running or ReviewSubAgentStatus.Unknown
                            )
                        )
                        {
                            candidate = null;
                            break;
                        }
                        if (
                            candidate is null
                            || !candidate
                                .Nodes.Select(node =>
                                    (
                                        node.Depth,
                                        node.ParentThreadId,
                                        node.AgentId,
                                        node.ThreadId,
                                        node.Status,
                                        node.FailureCode
                                    )
                                )
                                .SequenceEqual(
                                    nodes.Select(node =>
                                        (
                                            node.Depth,
                                            node.ParentThreadId,
                                            node.AgentId,
                                            node.ThreadId,
                                            node.Status,
                                            node.FailureCode
                                        )
                                    )
                                )
                        )
                        {
                            candidate = new ReviewSubAgentTreeSnapshot(nodes);
                            break;
                        }
                        var succeeded =
                            status.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                            && nodes.All(node =>
                                node.Status == ReviewSubAgentStatus.Completed && node.FailureCode is null
                            );
                        var output = succeeded ? Console.Out : Console.Error;
                        output.WriteLine($"status\t{status.Status}");
                        if (status.ResponseText is not null)
                        {
                            output.WriteLine(status.ResponseText);
                        }
                        if (!succeeded && nodes.Length > 0)
                        {
                            Console.Error.WriteLine(tree.ToSafeInventory());
                        }
                        return succeeded ? 0 : 1;
                    case "notstarted":
                    case "inprogress":
                        candidate = null;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown agent status '{status.Status}'; not reported as success."
                        );
                }
                await pollDelay(budget.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
        {
            Console.Error.WriteLine("Agent wait timed out. The remote agent may still be running.");
            return 1;
        }
    }
}
