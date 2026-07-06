using ConversationDaemon.Sample;

// ConversationDaemon.Sample drives an ALREADY-RUNNING LmStreaming.Sample server purely over its
// headless REST API. It provisions a conversation, runs a few scripted mock-provider prompts
// (including sub-agent delegation and a Wait/timer park-and-wake), then prints a browser deep-link so
// a human can open the SAME conversation live and take over. No project reference to
// LmStreaming.Sample — this is a pure HttpClient + System.Text.Json client.

var baseUrl = ResolveBaseUrl(args).TrimEnd('/');
Console.WriteLine($"ConversationDaemon: driving LmStreaming.Sample at {baseUrl}");

using var httpClient = new HttpClient
{
    BaseAddress = new Uri(baseUrl + "/", UriKind.Absolute),
};
var client = new DaemonRestClient(httpClient);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
var ct = cts.Token;

try
{
    // Step 1 — provision a conversation (server mints the thread id).
    Console.WriteLine();
    Console.WriteLine(
        "Provisioning a conversation (workspace=default, mode=default, provider=test-anthropic)...");
    var threadId = await client.ProvisionAsync("default", "test-anthropic", "default", ct);
    Console.WriteLine($"  thread id: {threadId}");

    // Step 2 — set a descriptive title/preview so the conversation is identifiable in the sidebar.
    await client.UpdateMetadataAsync(
        threadId,
        title: "Headless daemon demo",
        preview: "Provisioned and driven over REST by ConversationDaemon.Sample.",
        ct);
    Console.WriteLine("  metadata set (title + preview).");

    // Step 3 — print the browser deep-link (the web client reads ?threadId= at the site root).
    var deepLink = $"{baseUrl}/?threadId={threadId}";
    Console.WriteLine();
    Console.WriteLine("Open this link in a browser to watch or take over the conversation live:");
    Console.WriteLine($"  {deepLink}");

    // Step 4 — warm-up: a single calculate tool call.
    await RunStepAsync(client, threadId, "Warm-up (calculate tool call)", DaemonPrompts.WarmUp, ct);

    // Step 5 — sub-agent delegation via a nested instruction chain (AC3).
    await RunStepAsync(
        client,
        threadId,
        "Sub-agent delegation (nested instruction chain)",
        DaemonPrompts.SubAgentDelegation,
        ct);

    // Step 6 — Wait / park-and-wake: assert the run parks, then that it auto-resumes (AC5).
    Console.WriteLine();
    Console.WriteLine("Step: Wait / park-and-wake (3s timer)");
    var waitInputId = await client.SendMessageAsync(threadId, DaemonPrompts.WaitParkAndWake, ct);
    Console.WriteLine($"  sent (inputId={waitInputId}); waiting for the run to PARK on the timer...");
    await ConversationScript.WaitForDeferredWaitAsync(
        client,
        threadId,
        TimeSpan.FromSeconds(40),
        ct);
    Console.WriteLine("  confirmed: the run parked (a deferred Wait is persisted, is_deferred=true).");
    Console.WriteLine("  waiting for the timer to fire and the run to auto-resume...");
    await ConversationScript.WaitForRunToCompleteAsync(
        client,
        threadId,
        TimeSpan.FromSeconds(60),
        ct);
    var waitStatus = await client.GetStatusByInputIdAsync(threadId, waitInputId, ct);
    Console.WriteLine($"  resumed: run status = {waitStatus.Status}.");

    Console.WriteLine();
    Console.WriteLine(
        "Done. The conversation is now idle — open the link above to continue it in the web UI:");
    Console.WriteLine($"  {deepLink}");
    return 0;
}
catch (DaemonConnectionException ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}

// Sends one scripted prompt, waits for the run to go idle, then prints the resolved status.
static async Task RunStepAsync(
    DaemonRestClient client,
    string threadId,
    string label,
    string prompt,
    CancellationToken ct)
{
    Console.WriteLine();
    Console.WriteLine($"Step: {label}");
    var inputId = await client.SendMessageAsync(threadId, prompt, ct);
    Console.WriteLine($"  sent (inputId={inputId}); waiting for the run to complete...");
    await ConversationScript.WaitForRunToCompleteAsync(client, threadId, TimeSpan.FromSeconds(60), ct);
    var status = await client.GetStatusByInputIdAsync(threadId, inputId, ct);
    Console.WriteLine($"  run status = {status.Status}.");
}

// Resolves the server base URL from `--base-url <url>`, then a positional arg, then the
// LMSTREAMING_BASE_URL env var, else the default local address.
static string ResolveBaseUrl(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--base-url", StringComparison.Ordinal) && i + 1 < args.Length)
        {
            return args[i + 1];
        }
    }

    foreach (var arg in args)
    {
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            return arg;
        }
    }

    var env = Environment.GetEnvironmentVariable("LMSTREAMING_BASE_URL");
    if (!string.IsNullOrWhiteSpace(env))
    {
        return env;
    }

    return "http://localhost:5000";
}
