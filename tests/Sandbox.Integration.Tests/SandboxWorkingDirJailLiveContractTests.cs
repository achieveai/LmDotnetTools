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
/// outcome here. No credentials beyond the suite's own <c>SANDBOX_*</c> variables are needed.
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
