using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;

/// <summary>
/// The planner's (layer 1) tools. It mirrors every executor (layer 2) tool, keeping the same name
/// and parameters and adding a required <see cref="RationaleParameterName"/>. Each handler checks the
/// rationale, then hands the call to an <see cref="IDelegatedToolExecutor"/>. No handler here
/// runs a real tool, so a planner registry built from this provider cannot bypass the executor.
/// </summary>
/// <remarks>
/// The executor's contracts are read on every <see cref="GetFunctions"/> call, not once. So a
/// catalog that changes mid-conversation (sub-agent templates, a reconnected MCP server) reaches
/// the planner on its next turn.
/// </remarks>
public sealed class DelegatingToolProvider : IFunctionProvider
{
    /// <summary>The parameter every mirrored tool gains.</summary>
    public const string RationaleParameterName = "rationale";

    /// <summary>Error code on a result rejected for a missing or vague rationale.</summary>
    public const string RationaleRejectedErrorCode = "rationale_rejected";

    /// <summary>Error code on a result the executor failed to produce.</summary>
    public const string ExecutorFailedErrorCode = "executor_failed";

    /// <summary>Fewest words a rationale may have. Below this it cannot say why and what is needed.</summary>
    public const int DefaultMinRationaleWords = 4;

    /// <summary>
    /// Tools the executor's own loop keeps to itself and that are never mirrored. Recall reads the
    /// EXECUTOR's own compacted history, and the planner has its own recall for its own history.
    /// Ask-user and notify belong to the human channel, which only the planner has. Mirroring any of
    /// them would give the planner a second tool with the same name as its own.
    /// </summary>
    public static readonly IReadOnlySet<string> ExecutorPrivateToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        RecallConversationToolProvider.ToolName,
        AskUserQuestionToolProvider.ToolName,
        NotifyClientToolProvider.ToolName,
    };

    private const string RationaleDescription =
        "Required. Tell the executor why you are making this call and what you need back. "
        + "For example: 'Find where retries are configured; return only the retry settings verbatim.'";

    private readonly Func<IEnumerable<FunctionContract>> _executorContracts;
    private readonly IDelegatedToolExecutor _executor;
    private readonly int _minRationaleWords;
    private readonly ILogger _logger;

    /// <summary>Mirrors the tools of the executor's registry.</summary>
    public DelegatingToolProvider(
        FunctionRegistry executorRegistry,
        IDelegatedToolExecutor executor,
        int minRationaleWords = DefaultMinRationaleWords,
        ILogger<DelegatingToolProvider>? logger = null
    )
        : this(
            (executorRegistry ?? throw new ArgumentNullException(nameof(executorRegistry))).BuildContracts,
            executor,
            minRationaleWords,
            logger
        ) { }

    /// <summary>
    /// Mirrors the contracts <paramref name="executorContracts"/> returns. Use this when the executor
    /// is not a <see cref="MultiTurnAgentLoop"/> built from a <see cref="FunctionRegistry"/>, such as a
    /// CLI-backed agent whose tools are described separately.
    /// </summary>
    public DelegatingToolProvider(
        Func<IEnumerable<FunctionContract>> executorContracts,
        IDelegatedToolExecutor executor,
        int minRationaleWords = DefaultMinRationaleWords,
        ILogger<DelegatingToolProvider>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(executorContracts);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentOutOfRangeException.ThrowIfNegative(minRationaleWords);
        _executorContracts = executorContracts;
        _executor = executor;
        _minRationaleWords = minRationaleWords;
        _logger = logger ?? NullLogger<DelegatingToolProvider>.Instance;
    }

    /// <summary>
    /// The planner's whole tool registry: mirrors of the executor's tools and nothing else. Build
    /// the planner from this rather than adding the provider to a registry that already holds real
    /// tools. A real tool next to its mirror would let the planner skip the executor.
    /// </summary>
    public static FunctionRegistry CreatePlannerRegistry(
        FunctionRegistry executorRegistry,
        IDelegatedToolExecutor executor,
        int minRationaleWords = DefaultMinRationaleWords,
        ILoggerFactory? loggerFactory = null
    ) =>
        new FunctionRegistry().AddProvider(
            new DelegatingToolProvider(
                executorRegistry,
                executor,
                minRationaleWords,
                loggerFactory?.CreateLogger<DelegatingToolProvider>()
            )
        );

    /// <inheritdoc />
    public string ProviderName => "DualLayerPlanner";

    /// <inheritdoc />
    public int Priority => 0;

    /// <inheritdoc />
    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        foreach (var contract in _executorContracts())
        {
            if (ExecutorPrivateToolNames.Contains(contract.Name))
            {
                continue;
            }

            // The name is what both models see on the wire, so it is also how the executor is told
            // which of its own tools to run.
            var toolName = contract.Name;
            yield return new FunctionDescriptor
            {
                Contract = Mirror(contract),
                Handler = (argsJson, context, ct) => DelegateAsync(toolName, argsJson, context, ct),
                ProviderName = ProviderName,
            };
        }
    }

    private static FunctionContract Mirror(FunctionContract contract)
    {
        var parameters = contract.Parameters?.ToList() ?? [];
        if (parameters.Any(p => string.Equals(p.Name, RationaleParameterName, StringComparison.Ordinal)))
        {
            // Renaming the planner's parameter would make the executor receive a call shaped
            // differently from the tool it owns. Dropping the tool would quietly break the
            // same-tools promise. Both are worse than failing the build loudly.
            throw new InvalidOperationException(
                $"Tool '{contract.Name}' already has a '{RationaleParameterName}' parameter, so it cannot be mirrored "
                    + "for the dual-layer planner without ambiguity."
            );
        }

        parameters.Add(
            new FunctionParameterContract
            {
                Name = RationaleParameterName,
                Description = RationaleDescription,
                ParameterType = new JsonSchemaObject { Type = new("string") },
                IsRequired = true,
            }
        );

        return new FunctionContract
        {
            Namespace = contract.Namespace,
            ClassName = contract.ClassName,
            Name = contract.Name,
            Description = contract.Description,
            Parameters = parameters,
            ReturnType = typeof(string),
            ReturnDescription = "The executor's report on the call, written for you.",
        };
    }

    private async Task<ToolHandlerResult> DelegateAsync(
        string toolName,
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        if (!TrySplitRationale(argsJson, out var rationale, out var forwardedArgs, out var parseError))
        {
            _logger.LogWarning("Planner call to {ToolName} rejected: {Reason}", toolName, parseError);
            return ToolHandlerResult.FromError(parseError, RationaleRejectedErrorCode);
        }

        var wordCount = rationale.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < _minRationaleWords)
        {
            var message =
                $"Rejected: '{RationaleParameterName}' must explain in clear words why you are calling {toolName} "
                + $"and what you need back (at least {_minRationaleWords} words). The call did not run.";
            _logger.LogWarning(
                "Planner call to {ToolName} rejected: rationale has {WordCount} words, needs {MinWords}",
                toolName,
                wordCount,
                _minRationaleWords
            );
            return ToolHandlerResult.FromError(message, RationaleRejectedErrorCode);
        }

        var call = new DelegatedToolCall(toolName, forwardedArgs, rationale, context.ToolCallId);
        _logger.LogInformation(
            "Delegating {ToolName} (call {ToolCallId}) to executor. Rationale: {Rationale}",
            toolName,
            context.ToolCallId,
            rationale
        );

        try
        {
            var report = await _executor.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Executor finished {ToolName} (call {ToolCallId}); report length {ReportLength}",
                toolName,
                context.ToolCallId,
                report.Length
            );
            return ToolHandlerResult.FromText(report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The planner must see the failure as this call's result: rethrowing would fail the
            // planner's whole run over one tool, which a direct tool failure never does.
            _logger.LogError(ex, "Executor failed {ToolName} (call {ToolCallId})", toolName, context.ToolCallId);
            return ToolHandlerResult.FromError(
                $"The executor could not carry out {toolName}: {ex.Message}",
                ExecutorFailedErrorCode
            );
        }
    }

    /// <summary>
    /// Separates the rationale from the arguments the executor's tool expects. Fails with a message
    /// written for the planner when the arguments are not a JSON object or the rationale is absent.
    /// </summary>
    internal static bool TrySplitRationale(
        string argsJson,
        out string rationale,
        out string forwardedArgsJson,
        out string error
    )
    {
        rationale = string.Empty;
        forwardedArgsJson = "{}";
        error = string.Empty;

        JsonObject? args;
        try
        {
            args = string.IsNullOrWhiteSpace(argsJson) ? [] : JsonNode.Parse(argsJson) as JsonObject;
        }
        catch (JsonException ex)
        {
            error = $"Rejected: the arguments are not valid JSON ({ex.Message}). The call did not run.";
            return false;
        }

        if (args is null)
        {
            error = "Rejected: the arguments must be a JSON object. The call did not run.";
            return false;
        }

        var node = args[RationaleParameterName];
        var text = node is JsonValue value && value.TryGetValue<string>(out var s) ? s.Trim() : string.Empty;
        if (text.Length == 0)
        {
            error =
                $"Rejected: '{RationaleParameterName}' is required. Say why you are making this call and what you "
                + "need back. The call did not run.";
            return false;
        }

        _ = args.Remove(RationaleParameterName);
        rationale = text;
        forwardedArgsJson = args.ToJsonString();
        return true;
    }
}
