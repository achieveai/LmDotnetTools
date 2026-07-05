using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Agents;

public class ReadOnlyToolFilterTests
{
    [Fact]
    public void Apply_CopiesOnlyAllowListedTools_DropsWriteAndEdit()
    {
        var source = new FunctionRegistry();
        source.AddFunctionsFromObject(new FakeToolset(), providerName: "sandbox");
        var target = new FunctionRegistry();

        ReadOnlyToolFilter.Apply(source, target, ["Read", "Grep", "Glob", "Skill"]);

        var (contracts, _) = target.Build();
        var names = contracts.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        names.Should().Contain("Read");
        names.Should().NotContain("Write");
        names.Should().NotContain("Edit");
    }

    private sealed class FakeToolset
    {
        [Function("Read")]
        public string Read(string path) => path;

        [Function("Write")]
        public string Write(string path, string content) => path;

        [Function("Edit")]
        public string Edit(string path) => path;

        [Function("Grep")]
        public string Grep(string q) => q;
    }
}
