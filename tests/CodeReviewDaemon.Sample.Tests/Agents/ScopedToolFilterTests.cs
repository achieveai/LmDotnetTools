using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Agents;

public class ScopedToolFilterTests
{
    private const string NotesDir = "/store/PRs/github/acme-repo/123";
    private const string ScratchDir = "/store/scratch";
    private static readonly string[] ReadOnlyAllow = ["Read", "Grep", "Glob", "Skill"];
    private static readonly string[] WritableAllow = ["Write", "Edit", "Bash"];

    [Fact]
    public void Apply_WritableAllowEmpty_IsByteIdenticalToReadOnlyToolFilter()
    {
        var source = new FunctionRegistry();
        source.AddFunctionsFromObject(new FakeToolset(), providerName: "sandbox");

        var expected = new FunctionRegistry();
        ReadOnlyToolFilter.Apply(source, expected, ReadOnlyAllow);
        var (expectedContracts, expectedHandlers) = expected.Build();

        var actual = new FunctionRegistry();
        ScopedToolFilter.Apply(source, actual, ReadOnlyAllow, [], NotesDir, ScratchDir);
        var (actualContracts, actualHandlers) = actual.Build();

        actualContracts.Select(c => c.Name).Should().BeEquivalentTo(expectedContracts.Select(c => c.Name));
        actualHandlers.Keys.Should().BeEquivalentTo(expectedHandlers.Keys);
        actualHandlers.Keys.Should().NotContain("Write");
        actualHandlers.Keys.Should().NotContain("Edit");
        actualHandlers.Keys.Should().NotContain("Bash");
    }

    [Fact]
    public void Apply_WritableAllowNonEmpty_AddsReadOnlyPlusWriteEditBash()
    {
        var source = new FunctionRegistry();
        source.AddFunctionsFromObject(new FakeToolset(), providerName: "sandbox");
        var target = new FunctionRegistry();

        ScopedToolFilter.Apply(source, target, ReadOnlyAllow, WritableAllow, NotesDir, ScratchDir);

        var (contracts, _) = target.Build();
        var names = contracts.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        names.Should().BeEquivalentTo(["Read", "Grep", "Glob", "Skill", "Write", "Edit", "Bash"]);
    }

    [Fact]
    public async Task WrappedWrite_FilePathUnderNotesDir_DelegatesToRealHandler()
    {
        var toolset = new FakeToolset();
        var source = new FunctionRegistry();
        source.AddFunctionsFromObject(toolset, providerName: "sandbox");
        var target = new FunctionRegistry();
        ScopedToolFilter.Apply(source, target, ReadOnlyAllow, WritableAllow, NotesDir, ScratchDir);
        var (_, handlers) = target.Build();

        var argsJson = $$"""{"file_path":"{{NotesDir}}/note.md","content":"hello"}""";
        var result = await handlers["Write"](argsJson, new ToolCallContext(), CancellationToken.None);

        toolset.WriteInvoked.Should().BeTrue();
        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task WrappedWrite_FilePathUnderScratchDir_DelegatesToRealHandler()
    {
        var toolset = new FakeToolset();
        var source = new FunctionRegistry();
        source.AddFunctionsFromObject(toolset, providerName: "sandbox");
        var target = new FunctionRegistry();
        ScopedToolFilter.Apply(source, target, ReadOnlyAllow, WritableAllow, NotesDir, ScratchDir);
        var (_, handlers) = target.Build();

        var argsJson = $$"""{"file_path":"{{ScratchDir}}/work.tmp","content":"hello"}""";
        var result = await handlers["Write"](argsJson, new ToolCallContext(), CancellationToken.None);

        toolset.WriteInvoked.Should().BeTrue();
        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.IsError.Should().BeFalse();
    }

    [Theory]
    [InlineData("/store/repos/Repo/x.cs")]
    [InlineData("/etc/x")]
    public async Task WrappedWrite_FilePathOutsideWritableRoots_RejectsWithoutInvokingRealHandler(string filePath)
    {
        var toolset = new FakeToolset();
        var source = new FunctionRegistry();
        source.AddFunctionsFromObject(toolset, providerName: "sandbox");
        var target = new FunctionRegistry();
        ScopedToolFilter.Apply(source, target, ReadOnlyAllow, WritableAllow, NotesDir, ScratchDir);
        var (_, handlers) = target.Build();

        var argsJson = $$"""{"file_path":"{{filePath}}","content":"hello"}""";
        var result = await handlers["Write"](argsJson, new ToolCallContext(), CancellationToken.None);

        toolset.WriteInvoked.Should().BeFalse();
        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.IsError.Should().BeTrue();
        resolved.Payload.Text.Should().Contain("scoped-write");
        resolved.Payload.Text.Should().Contain(filePath);
    }

    [Fact]
    public async Task WrappedWrite_MissingFilePath_RejectsWithoutInvokingRealHandler()
    {
        var toolset = new FakeToolset();
        var source = new FunctionRegistry();
        source.AddFunctionsFromObject(toolset, providerName: "sandbox");
        var target = new FunctionRegistry();
        ScopedToolFilter.Apply(source, target, ReadOnlyAllow, WritableAllow, NotesDir, ScratchDir);
        var (_, handlers) = target.Build();

        var result = await handlers["Write"]("""{"content":"hello"}""", new ToolCallContext(), CancellationToken.None);

        toolset.WriteInvoked.Should().BeFalse();
        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.IsError.Should().BeTrue();
    }

    [Theory]
    [InlineData("PathTraversalViaNotesDir")]
    [InlineData("PathTraversalViaScratchDir")]
    public async Task WrappedWrite_FilePathWithParentTraversal_IsRejected(string variant)
    {
        var toolset = new FakeToolset();
        var source = new FunctionRegistry();
        source.AddFunctionsFromObject(toolset, providerName: "sandbox");
        var target = new FunctionRegistry();
        ScopedToolFilter.Apply(source, target, ReadOnlyAllow, WritableAllow, NotesDir, ScratchDir);
        var (_, handlers) = target.Build();

        var filePath = variant == "PathTraversalViaNotesDir"
            ? $"{NotesDir}/../../../etc/passwd"
            : $"{ScratchDir}/../../../etc/passwd";
        var argsJson = $$"""{"file_path":"{{filePath}}","content":"hello"}""";
        var result = await handlers["Write"](argsJson, new ToolCallContext(), CancellationToken.None);

        toolset.WriteInvoked.Should().BeFalse();
        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task WrappedEdit_FilePathUnderNotesDir_DelegatesToRealHandler()
    {
        var toolset = new FakeToolset();
        var source = new FunctionRegistry();
        source.AddFunctionsFromObject(toolset, providerName: "sandbox");
        var target = new FunctionRegistry();
        ScopedToolFilter.Apply(source, target, ReadOnlyAllow, WritableAllow, NotesDir, ScratchDir);
        var (_, handlers) = target.Build();

        var argsJson = $$"""{"file_path":"{{NotesDir}}/note.md"}""";
        var result = await handlers["Edit"](argsJson, new ToolCallContext(), CancellationToken.None);

        toolset.EditInvoked.Should().BeTrue();
        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task WrappedEdit_FilePathOutsideWritableRoots_RejectsWithoutInvokingRealHandler()
    {
        var toolset = new FakeToolset();
        var source = new FunctionRegistry();
        source.AddFunctionsFromObject(toolset, providerName: "sandbox");
        var target = new FunctionRegistry();
        ScopedToolFilter.Apply(source, target, ReadOnlyAllow, WritableAllow, NotesDir, ScratchDir);
        var (_, handlers) = target.Build();

        var argsJson = """{"file_path":"/store/repos/Repo/x.cs"}""";
        var result = await handlers["Edit"](argsJson, new ToolCallContext(), CancellationToken.None);

        toolset.EditInvoked.Should().BeFalse();
        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task WrappedBash_PassesThroughUnwrapped_EvenForOutsidePathLikeArguments()
    {
        var toolset = new FakeToolset();
        var source = new FunctionRegistry();
        source.AddFunctionsFromObject(toolset, providerName: "sandbox");
        var target = new FunctionRegistry();
        ScopedToolFilter.Apply(source, target, ReadOnlyAllow, WritableAllow, NotesDir, ScratchDir);
        var (_, handlers) = target.Build();

        var argsJson = """{"command":"rm -rf /etc"}""";
        var result = await handlers["Bash"](argsJson, new ToolCallContext(), CancellationToken.None);

        toolset.BashInvoked.Should().BeTrue();
        var resolved = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject;
        resolved.Payload.IsError.Should().BeFalse();
    }

    private sealed class FakeToolset
    {
        public bool WriteInvoked { get; private set; }

        public bool EditInvoked { get; private set; }

        public bool BashInvoked { get; private set; }

        [Function("Read")]
        public string Read(string path) => path;

        [Function("Grep")]
        public string Grep(string query) => query;

        [Function("Glob")]
        public string Glob(string pattern) => pattern;

        [Function("Skill")]
        public string Skill(string name) => name;

        [Function("Write")]
        public string Write(string file_path, string content)
        {
            WriteInvoked = true;
            return file_path;
        }

        [Function("Edit")]
        public string Edit(string file_path)
        {
            EditInvoked = true;
            return file_path;
        }

        [Function("Bash")]
        public string Bash(string command)
        {
            BashInvoked = true;
            return command;
        }
    }
}
