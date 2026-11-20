using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

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
/// </remarks>
[Collection(LiveGatewayCollection.Name)]
public sealed class SandboxWorkingDirJailLiveContractTests(LiveGatewayFixture fixture)
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
        try
        {
            await client.WriteTextFileAsync(seeder.SessionId, $"{Jail}/.keep", "");
            await client.WriteTextFileAsync(seeder.SessionId, "jail/b/.keep", "");
        }
        finally
        {
            await client.DeleteAsync(seeder.SessionId);
        }

        var jailed = await client.CreateAsync(new SandboxCreateRequest(workspace, workingDir: Jail));
        var sessionId = jailed.SessionId;
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
            await client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task A_link_in_the_working_dir_carries_no_write_outside_it_nor_into_a_read_only_mount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var client = fixture.Client;
        var workspace = "jail-" + Guid.NewGuid().ToString("N");

        var seeder = await client.CreateAsync(new SandboxCreateRequest(workspace));
        try
        {
            await client.WriteTextFileAsync(seeder.SessionId, $"{Jail}/.keep", "");
            await client.WriteTextFileAsync(seeder.SessionId, "jail/b/victim.txt", "orig\n");
        }
        finally
        {
            await client.DeleteAsync(seeder.SessionId);
        }

        var jailed = await client.CreateAsync(new SandboxCreateRequest(workspace, workingDir: Jail));
        var sessionId = jailed.SessionId;
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

            // The agent links out of its own directory, which it may write: to a sibling directory, to a file in
            // it, and to the read-only mount.
            var (linkRefused, linkText) = await CallToolAsync(
                sessionId,
                "Bash",
                new
                {
                    command = $"cd /workspace/{Jail} && ln -s /workspace/jail/b out && "
                        + $"ln -s /workspace/jail/b/victim.txt victim && ln -s {readOnlyMount} ro && echo linked",
                }
            );
            linkRefused.Should().BeFalse($"the agent may create links in its own directory: {linkText}");
            linkText.Should().Contain("linked");

            // Every path below is inside the jail as written; only the link takes it out. A write through a
            // directory link must be refused. A write to the file link may instead replace the link itself (a write
            // through a temporary file and a rename), which leaves the file outside alone: the outcome check decides.
            var writes = new (string Tool, object Arguments, bool MustRefuse)[]
            {
                ("Write", new { file_path = $"/workspace/{Jail}/out/write.txt", content = "out\n" }, true),
                ("Write", new { file_path = $"/workspace/{Jail}/victim", content = "overwritten\n" }, false),
                ("Write", new { file_path = $"/workspace/{Jail}/ro/write.txt", content = "out\n" }, true),
                (
                    "Bash",
                    new
                    {
                        command = $"cd /workspace/{Jail}; echo out > out/bash.txt; echo overwritten > victim; "
                            + "echo out > ro/bash.txt",
                    },
                    false
                ),
            };
            foreach (var (tool, arguments, mustRefuse) in writes)
            {
                var (refused, text) = await CallToolAsync(sessionId, tool, arguments);
                if (mustRefuse)
                {
                    refused.Should().BeTrue($"a write through a link out of the jail is refused: {text}");
                }
            }

            var outside = await client.ExecuteAsync(
                sessionId,
                new SandboxCommand([
                    "sh",
                    "-c",
                    "cd /workspace/jail/b; for f in write.txt bash.txt; "
                        + "do if [ -e \"$f\" ]; then echo \"present $f\"; else echo \"absent $f\"; fi; done; "
                        + "echo \"victim $(cat victim.txt)\"",
                ])
            );
            outside.ExitCode.Should().Be(0, outside.StandardError);
            outside
                .StandardOutput.Should()
                .Contain("absent write.txt")
                .And.Contain("absent bash.txt")
                .And.Contain("victim orig", "the file outside the jail is untouched");

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
            await client.DeleteAsync(sessionId);
        }
    }

    /// <summary>One MCP tool call as the default agent. A JSON-RPC error counts as a refusal, with its message.</summary>
    private static async Task<(bool IsError, string Text)> CallToolAsync(
        string sessionId,
        string tool,
        object arguments
    )
    {
        try
        {
            return await SandboxMcpRpc.CallToolAsync(sessionId, agentId: null, tool, arguments);
        }
        catch (InvalidOperationException ex)
        {
            return (true, ex.Message);
        }
    }
}
