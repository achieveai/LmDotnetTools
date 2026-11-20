using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// Live contract for the working-directory jail (gateway ADR 0044, the pinned image): a sandbox created with a
/// working directory lets its agent write there and nowhere else in the workspace. LmStreaming's dedicated
/// sandboxes rely on it to keep one review's agent out of another review's directory on the same workspace.
/// </summary>
/// <remarks>
/// Only the AGENT is jailed. The harness side (operations through <see cref="SandboxClient.ExecuteAsync"/>, the
/// files API) sees the whole workspace by the gateway's design, so it is what seeds the directories and reads the
/// outcome here. No credentials beyond the suite's own <c>SANDBOX_*</c> variables are needed. A link the agent makes
/// in its own directory must not carry a write out of it, to the workspace or to a read-only mount.
/// <para>
/// Logs (DEBUG) each workspace and session id, every tool result and the teardown, so a CI failure shows what the
/// gateway answered. Never the app key: no log reads the client's credentials.
/// </para>
/// </remarks>
[Collection(LiveGatewayCollection.Name)]
public sealed class SandboxWorkingDirJailLiveContractTests(LiveGatewayFixture fixture, ITestOutputHelper output)
    : LoggingTestBase(output)
{
    private const string Jail = "jail/a";

    [SkippableFact]
    public async Task An_agent_jailed_to_a_working_dir_writes_inside_it_and_nowhere_else_in_the_workspace()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var client = fixture.Client;
        var workspace = "jail-" + Guid.NewGuid().ToString("N");

        // Both directories exist before the jailed sandbox starts, so a refusal below is the jail, not a missing
        // parent directory.
        var seeder = await client.CreateAsync(new SandboxCreateRequest(workspace));
        Logger.LogDebug("Setup: seeding workspace {Workspace} from session {SessionId}", workspace, seeder.SessionId);
        try
        {
            await client.WriteTextFileAsync(seeder.SessionId, $"{Jail}/.keep", "");
            await client.WriteTextFileAsync(seeder.SessionId, "jail/b/.keep", "");
        }
        finally
        {
            await DeleteSessionAsync(workspace, seeder.SessionId);
        }

        var jailed = await client.CreateAsync(new SandboxCreateRequest(workspace, workingDir: Jail));
        var sessionId = jailed.SessionId;
        Logger.LogDebug(
            "Setup: jailed session {SessionId} on workspace {Workspace}, working directory {WorkingDir}",
            sessionId,
            workspace,
            jailed.WorkspaceWorkingDir
        );
        try
        {
            jailed.WorkspaceWorkingDir.Should().Be(Jail, "the gateway acknowledges the jail it applied");

            var (insideRefused, insideText) = await CallToolAsync(
                sessionId,
                "Write",
                new { file_path = $"/workspace/{Jail}/inside.txt", content = "in\n" }
            );
            insideRefused.Should().BeFalse($"the working directory is writable: {insideText}");

            var (outsideRefused, outsideText) = await CallToolAsync(
                sessionId,
                "Write",
                new { file_path = "/workspace/jail/b/outside.txt", content = "out\n" }
            );
            outsideRefused.Should().BeTrue("a sibling directory is outside the jail");
            outsideText.Should().Contain("only the sandbox working directory is writable");

            // A shell cannot get round it either: outside the jail the workspace is mounted read-only.
            _ = await CallToolAsync(
                sessionId,
                "Bash",
                new { command = "echo out > /workspace/jail/b/bash.txt; echo out > /workspace/root.txt" }
            );
            var (bashRefused, bashText) = await CallToolAsync(
                sessionId,
                "Bash",
                new { command = $"echo in > /workspace/{Jail}/bash.txt && echo wrote" }
            );
            bashRefused.Should().BeFalse($"a shell writes inside the jail: {bashText}");

            var seen = await client.ExecuteAsync(
                sessionId,
                new SandboxCommand([
                    "sh",
                    "-c",
                    "cd /workspace; for f in "
                        + $"{Jail}/inside.txt {Jail}/bash.txt jail/b/outside.txt jail/b/bash.txt root.txt; "
                        + "do if [ -e \"$f\" ]; then echo \"present $f\"; else echo \"absent $f\"; fi; done",
                ])
            );
            seen.ExitCode.Should().Be(0, seen.StandardError);
            seen.StandardOutput.Should()
                .Contain($"present {Jail}/inside.txt")
                .And.Contain($"present {Jail}/bash.txt")
                .And.Contain("absent jail/b/outside.txt")
                .And.Contain("absent jail/b/bash.txt")
                .And.Contain("absent root.txt");
        }
        finally
        {
            await DeleteSessionAsync(workspace, sessionId);
        }
    }

    [SkippableFact]
    public async Task A_link_in_the_working_dir_carries_no_write_outside_it_nor_into_a_read_only_mount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var client = fixture.Client;
        var workspace = "jail-" + Guid.NewGuid().ToString("N");

        var seeder = await client.CreateAsync(new SandboxCreateRequest(workspace));
        Logger.LogDebug("Setup: seeding workspace {Workspace} from session {SessionId}", workspace, seeder.SessionId);
        try
        {
            await client.WriteTextFileAsync(seeder.SessionId, $"{Jail}/.keep", "");
            await client.WriteTextFileAsync(seeder.SessionId, "jail/b/victim.txt", "orig\n");
        }
        finally
        {
            await DeleteSessionAsync(workspace, seeder.SessionId);
        }

        var jailed = await client.CreateAsync(new SandboxCreateRequest(workspace, workingDir: Jail));
        var sessionId = jailed.SessionId;
        Logger.LogDebug(
            "Setup: jailed session {SessionId} on workspace {Workspace}, working directory {WorkingDir}",
            sessionId,
            workspace,
            jailed.WorkspaceWorkingDir
        );
        try
        {
            // A read-only mount outside the workspace: a marketplace (PLUGINS_DIRS), as the agent itself sees it.
            var (_, mountsText) = await CallToolAsync(
                sessionId,
                "Bash",
                new
                {
                    command = "awk '$2 ~ \"^/marketplaces/\" && $4 ~ /(^|,)ro(,|$)/ { print \"romount=\" $2 }' "
                        + "/proc/self/mounts",
                }
            );
            var readOnlyMount = Regex.Match(mountsText, @"romount=(/\S+)") is { Success: true } found
                ? found.Groups[1].Value
                : null;
            if (readOnlyMount is null)
            {
                var reason = $"The sandbox has no read-only marketplace mount to aim a link at: {mountsText}";
                Skip.IfNot(fixture.ContractRequired, reason);
                throw new InvalidOperationException(
                    reason + " SANDBOX_CONTRACT_REQUIRED forbids skipping: mount one (PLUGINS_DIRS)."
                );
            }

            Logger.LogDebug("Setup: read-only mount {ReadOnlyMount} in session {SessionId}", readOnlyMount, sessionId);

            // The agent links out of its own directory, which it may write: to a sibling directory, to a file in
            // it (twice: Write may replace its link, so Bash gets one of its own), and to the read-only mount.
            var (linkRefused, linkText) = await CallToolAsync(
                sessionId,
                "Bash",
                new
                {
                    command = $"cd /workspace/{Jail} && ln -s /workspace/jail/b out && "
                        + "ln -s /workspace/jail/b/victim.txt victim && "
                        + "ln -s /workspace/jail/b/victim.txt victim_bash && "
                        + $"ln -s {readOnlyMount} ro && echo linked",
                }
            );
            linkRefused.Should().BeFalse($"the agent may create links in its own directory: {linkText}");
            linkText.Should().Contain("linked");

            // Every path below is inside the jail as written; only the link takes it out. A write through a
            // directory link must be refused. Write to the file link may instead replace the link itself (a write
            // through a temporary file and a rename), which leaves the file outside alone: the outcome check decides.
            // Bash writes through its own link, checked to be one just before the redirect, so the outcome check
            // shows that a shell following a link is held too.
            var writes = new (string Tool, object Arguments, bool MustRefuse)[]
            {
                ("Write", new { file_path = $"/workspace/{Jail}/out/write.txt", content = "out\n" }, true),
                ("Write", new { file_path = $"/workspace/{Jail}/victim", content = "overwritten\n" }, false),
                ("Write", new { file_path = $"/workspace/{Jail}/ro/write.txt", content = "out\n" }, true),
                (
                    "Bash",
                    new
                    {
                        command = $"cd /workspace/{Jail}; echo out > out/bash.txt; "
                            + "if [ -L victim_bash ]; then echo link-before-write; echo bash-overwritten > victim_bash; "
                            + "fi; echo out > ro/bash.txt; echo bash-done",
                    },
                    false
                ),
            };
            var bashWriteText = string.Empty;
            foreach (var (tool, arguments, mustRefuse) in writes)
            {
                var (refused, text) = await CallToolAsync(sessionId, tool, arguments);
                if (mustRefuse)
                {
                    refused.Should().BeTrue($"a write through a link out of the jail is refused: {text}");
                }

                if (tool == "Bash")
                {
                    bashWriteText = text;
                }
            }

            bashWriteText.Should().Contain("link-before-write", "Bash must write through a link, not a plain file");

            var outside = await client.ExecuteAsync(
                sessionId,
                new SandboxCommand([
                    "sh",
                    "-c",
                    "cd /workspace/jail/b; for f in write.txt bash.txt; "
                        + "do if [ -e \"$f\" ]; then echo \"present $f\"; else echo \"absent $f\"; fi; done; "
                        + "echo \"victim $(cat victim.txt)\"; "
                        + $"if [ -L /workspace/{Jail}/victim_bash ]; then echo \"link victim_bash\"; fi",
                ])
            );
            Logger.LogDebug(
                "Outcome outside the jail of session {SessionId}: exit {ExitCode}, {StandardOutput}, {StandardError}",
                sessionId,
                outside.ExitCode,
                outside.StandardOutput,
                outside.StandardError
            );
            outside.ExitCode.Should().Be(0, outside.StandardError);
            outside
                .StandardOutput.Should()
                .Contain("absent write.txt")
                .And.Contain("absent bash.txt")
                .And.Contain("victim orig", "the file outside the jail is untouched by Write and by Bash")
                .And.Contain("link victim_bash", "Bash's redirect went through the link and left it in place");

            var (_, mountText) = await CallToolAsync(
                sessionId,
                "Bash",
                new
                {
                    command = $"cd {readOnlyMount}; for f in write.txt bash.txt; "
                        + "do if [ -e \"$f\" ]; then echo \"present-ro $f\"; else echo \"absent-ro $f\"; fi; done",
                }
            );
            mountText.Should().Contain("absent-ro write.txt").And.Contain("absent-ro bash.txt");
        }
        finally
        {
            await DeleteSessionAsync(workspace, sessionId);
        }
    }

    /// <summary>One MCP tool call as the default agent. A JSON-RPC error counts as a refusal, with its message.</summary>
    private async Task<(bool IsError, string Text)> CallToolAsync(string sessionId, string tool, object arguments)
    {
        (bool IsError, string Text) result;
        try
        {
            result = await SandboxMcpRpc.CallToolAsync(sessionId, agentId: null, tool, arguments);
        }
        catch (InvalidOperationException ex)
        {
            result = (true, ex.Message);
        }

        Logger.LogDebug(
            "Tool {Tool} in session {SessionId} with {@Arguments}: refused {Refused}, answered {Text}",
            tool,
            sessionId,
            arguments,
            result.IsError,
            result.Text
        );
        return result;
    }

    /// <summary>Deletes a session the test created, and logs it, so a failure shows what was torn down.</summary>
    private async Task DeleteSessionAsync(string workspace, string sessionId)
    {
        Logger.LogDebug("Teardown: deleting session {SessionId} of workspace {Workspace}", sessionId, workspace);
        await fixture.Client.DeleteAsync(sessionId);
    }
}
