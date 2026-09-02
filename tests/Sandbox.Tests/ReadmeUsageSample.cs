namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// A COMPILED transcription of the usage walkthrough in <c>src/Sandbox/README.md</c>, so a rename or
/// signature change to the public surface breaks the build here instead of silently rotting the
/// README. It is deliberately never invoked: the point is that it <i>compiles</i>, and running it
/// would require a live gateway (that job belongs to
/// <c>tests/Sandbox.Integration.Tests/SandboxLiveContractTests.cs</c>, which exercises the same flow
/// end-to-end against the real thing).
/// </summary>
/// <remarks>
/// Keep this method and the README's fenced <c>csharp</c> block in step. If you change one, change the
/// other in the same commit — a compiled sample only protects the README while the two say the same
/// thing. Every call in the documented catalog -&gt; create -&gt; command -&gt; file -&gt; delete flow appears
/// here exactly once.
/// <para>
/// What this pins is SIGNATURES, not prose. The compiler catches a renamed method or a changed parameter
/// list; it cannot catch the README describing behaviour the SDK no longer has. Claims about what a call
/// DOES still need a test — or a reader who checks.
/// </para>
/// </remarks>
internal static class ReadmeUsageSample
{
    /// <summary>The README's "Usage" block, verbatim in behaviour and call order.</summary>
    public static async Task DocumentedFlowAsync(string myBase64Secret)
    {
        var options = new SandboxClientOptions(
            serverAddress: new Uri("https://sandbox.internal:3443"),
            appId: "my-app",
            clientSecret: myBase64Secret,
            executionTimeout: TimeSpan.FromMinutes(10),
            transportTimeout: TimeSpan.FromSeconds(30)
        );

        using var client = new SandboxClient(options); // owns its HttpClient

        var sandbox = await client.CreateAsync(new SandboxCreateRequest(workspace: "my-workspace"));
        try
        {
            var catalog = await client.PreviewMarketplacesAsync();
            var discovered = await client.ListDiscoveredAsync(sandbox.SessionId);

            var clone = await client.ExecuteAsync(
                sandbox.SessionId,
                new SandboxCommand(["git", "clone", "https://example.com/repo.git", "repo"])
            );
            var build = await client.ExecuteAsync(
                sandbox.SessionId,
                new SandboxCommand(["dotnet", "build"], workingDirectory: "repo")
            );
            Console.WriteLine($"exit={build.ExitCode}\n{build.CombinedOutput}");

            await client.WriteTextFileAsync(sandbox.SessionId, "repo/notes.md", "# build passed\n");
            var notes = await client.ReadTextFileAsync(sandbox.SessionId, "repo/notes.md");
            var entries = await client.ListDirectoryAsync(sandbox.SessionId, "repo");

            // Referenced so the compiler proves each documented call's result type is still usable as
            // written, rather than warning them away as unused locals.
            _ = (catalog.Marketplaces.Count, discovered.Count, clone.ExitCode, notes.Length, entries.Count);
        }
        finally
        {
            await client.DeleteAsync(sandbox.SessionId); // explicit teardown — never implicit on dispose
        }
    }

    /// <summary>
    /// The README's "Recovering a command whose response was lost" guidance, compiled: the recovery
    /// handle is read off the exception and fed back as the operation id of the SAME command.
    /// </summary>
    public static async Task<SandboxCommandResult?> RecoverLostCommandAsync(SandboxClient client, string sessionId)
    {
        string[] argv = ["git", "push"];
        try
        {
            return await client.ExecuteAsync(sessionId, new SandboxCommand(argv));
        }
        catch (SandboxException ex)
            when (ex.OperationId is { } operationId
                && ex.Kind is SandboxErrorKind.TransportTimeout or SandboxErrorKind.Unavailable
            )
        {
            // Gate on Kind, not merely on the id being present. The id is stamped on DETERMINISTIC failures
            // too (a 403, a refused redirect) — re-issuing those just fails again, and for a side-effecting
            // command like `git push` a blind retry loop is exactly the wrong reflex. Only an AMBIGUOUS
            // failure, where the response was lost and the command may or may not have run, is worth
            // re-issuing: passing the same operation id makes the gateway replay the existing operation
            // rather than run the push a second time.
            return await client.ExecuteAsync(sessionId, new SandboxCommand(argv, operationId: operationId));
        }
    }

    /// <summary>
    /// The README's "Artifact retention and cleanup" block, compiled: a succeeding command needs no
    /// cleanup call because <see cref="SandboxClient.ExecuteAsync"/> already released its record, while a
    /// FAILING one keeps its record as a replay handle and is reclaimed explicitly.
    /// </summary>
    public static async Task ReclaimCommandArtifactsAsync(SandboxClient client, string sessionId, string id)
    {
        try
        {
            var result = await client.ExecuteAsync(sessionId, new SandboxCommand(["git", "status"], operationId: id));
            Console.WriteLine(result.StandardOutput);
            // Nothing to clean up here: ExecuteAsync already released the record and its artifacts, and
            // result.OperationRecordReleased says whether that succeeded.
        }
        catch (SandboxException ex) when (ex.OperationId is { } failed)
        {
            // A failure keeps its record on purpose, so the id stays a replay handle. Reclaim it explicitly
            // once you have given up on re-reading that operation's output.
            await client.DeleteOperationAsync(sessionId, failed);
        }
    }
}
