using AchieveAi.LmDotnetTools.LmCore.Core;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

/// <summary>
/// Pins the factory-delegate contract of <see cref="ToolCallInjectionMiddleware"/>: the delegate
/// must be invoked once per Invoke/InvokeStreaming call so a mutating upstream catalog (e.g.
/// <c>MutableSubAgentTemplateSource</c>) surfaces on the next turn without rebuilding the
/// middleware stack — this is the mechanism that enables mid-session sub-agent activation.
/// </summary>
public class ToolCallInjectionMiddlewareTests
{
    private static FunctionContract Contract(string name) => new()
    {
        Name = name,
        Description = $"Test function {name}",
        Parameters = [],
    };

    [Fact]
    public async Task InvokeAsync_InvokesFactoryEachCall_ReflectsMutationOnNextCall()
    {
        // The whole point of the factory delegate is that a later mutation of the underlying
        // function set surfaces on the next call. If the middleware cached the first snapshot
        // it would defeat mid-session sub-agent activation.
        var functions = new List<FunctionContract> { Contract("first") };
        var middleware = new ToolCallInjectionMiddleware(() => functions);

        var capturedOptions = new List<GenerateReplyOptions?>();
        var mockAgent = new Mock<IAgent>();
        mockAgent
            .Setup(a => a.GenerateReplyAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, opts, _) => capturedOptions.Add(opts))
            .ReturnsAsync([new TextMessage { Text = "ok", Role = Role.Assistant }]);

        var context = new MiddlewareContext([new TextMessage { Text = "hi", Role = Role.User }]);
        _ = await middleware.InvokeAsync(context, mockAgent.Object);

        functions.Add(Contract("second"));
        _ = await middleware.InvokeAsync(context, mockAgent.Object);

        capturedOptions.Should().HaveCount(2);
        capturedOptions[0]!.Functions!.Select(f => f.Name).Should().BeEquivalentTo(["first"]);
        capturedOptions[1]!.Functions!.Select(f => f.Name).Should().BeEquivalentTo(["first", "second"]);
    }

    [Fact]
    public async Task InvokeStreamingAsync_InvokesFactoryEachCall_ReflectsMutationOnNextCall()
    {
        // Parity with the non-streaming path — the streaming surface is what the multi-turn loop
        // actually uses, so the mid-session activation path must work here too.
        var functions = new List<FunctionContract> { Contract("first") };
        var middleware = new ToolCallInjectionMiddleware(() => functions);

        var capturedOptions = new List<GenerateReplyOptions?>();
        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, opts, _) => capturedOptions.Add(opts))
            .ReturnsAsync(EmptyStream());

        var context = new MiddlewareContext([new TextMessage { Text = "hi", Role = Role.User }]);
        _ = await middleware.InvokeStreamingAsync(context, mockAgent.Object);

        functions.Add(Contract("second"));
        _ = await middleware.InvokeStreamingAsync(context, mockAgent.Object);

        capturedOptions.Should().HaveCount(2);
        capturedOptions[0]!.Functions!.Select(f => f.Name).Should().BeEquivalentTo(["first"]);
        capturedOptions[1]!.Functions!.Select(f => f.Name).Should().BeEquivalentTo(["first", "second"]);
    }

    [Fact]
    public async Task InvokeAsync_MergesFactoryFunctions_WithExistingOptionsFunctions()
    {
        // Caller-supplied request functions (e.g. set by a higher middleware) must not be
        // dropped just because the injection middleware also has its own catalog.
        FunctionContract[] mwFns = [Contract("middleware")];
        var middleware = new ToolCallInjectionMiddleware(() => mwFns);
        var existingOptions = new GenerateReplyOptions { Functions = [Contract("existing")] };
        var context = new MiddlewareContext([new TextMessage { Text = "hi", Role = Role.User }], existingOptions);

        GenerateReplyOptions? captured = null;
        var mockAgent = new Mock<IAgent>();
        mockAgent
            .Setup(a => a.GenerateReplyAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, opts, _) => captured = opts)
            .ReturnsAsync([new TextMessage { Text = "ok", Role = Role.Assistant }]);

        _ = await middleware.InvokeAsync(context, mockAgent.Object);

        captured!.Functions!.Select(f => f.Name)
            .Should().BeEquivalentTo(["middleware", "existing"]);
    }

    [Fact]
    public void Constructor_NullFactory_Throws()
    {
        var act = () => new ToolCallInjectionMiddleware((Func<IEnumerable<FunctionContract>>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_FixedFunctionsOverload_NullFunctions_Throws()
    {
        var act = () => new ToolCallInjectionMiddleware((IEnumerable<FunctionContract>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static async IAsyncEnumerable<IMessage> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
