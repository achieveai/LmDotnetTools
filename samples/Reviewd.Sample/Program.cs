using System.Runtime.InteropServices;
using Reviewd.Sample;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(CliOptions.HelpText);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

using var termination =
    options.Command == "review batch" && !OperatingSystem.IsWindows()
        ? PosixSignalRegistration.Create(
            PosixSignal.SIGTERM,
            context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            }
        )
        : null;
try
{
    using var config = ReviewdConfig.Load(options.ConfigPath);
    if (options.Command.StartsWith("merge ", StringComparison.Ordinal))
    {
        return await MergeRunner.RunAsync(options, config, cancellation.Token);
    }
    if (options.Command == "review capacity")
    {
        var evidence = System
            .Text.Json.Nodes.JsonNode.Parse(
                await File.ReadAllTextAsync(options.Required("evidence"), cancellation.Token)
            )!
            .AsObject();
        var report = await ReviewCapacity.PreviewAsync(
            options.Required("db"),
            options.Required("workspace"),
            options.Required("session"),
            options
                .Required("repos")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            evidence,
            cancellation.Token
        );
        Console.WriteLine(report.ToJsonString());
        return 0;
    }
    if (options.Command == "review reconcile-slot")
    {
        return await SlotRelease.RunAsync(options, cancellation.Token);
    }
    if (options.Command == "review reclaim-slot")
    {
        return await ReclaimSlotCommand.RunAsync(options, config, cancellation.Token);
    }
    if (options.Command == "review reap-slots")
    {
        return await SlotReaper.RunAsync(options, config, cancellation.Token);
    }
    if (options.Command == "review batch")
    {
        return await ReviewBatch.RunAsync(options, config, cancellation.Token);
    }
    if (options.Command == "review run")
    {
        return await ReviewRunner.RunAsync(options, config, cancellation.Token);
    }
    if (options.Command.StartsWith("auth ", StringComparison.Ordinal))
    {
        return await AuthCommands.RunAsync(options, config, cancellation.Token);
    }
    if (options.Command.StartsWith("sandbox ", StringComparison.Ordinal))
    {
        return await SandboxCommands.RunAsync(options, config, cancellation.Token);
    }
    return options.Command.StartsWith("pr ", StringComparison.Ordinal)
        ? await PrCommands.RunAsync(options, config, cancellation.Token)
        : await WorkspaceCommands.RunAsync(options, config, cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled locally. Remote commands or agents may still be running.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
