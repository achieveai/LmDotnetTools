using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.AgentRuntime;

public class ContentSourceTests
{
    [Fact]
    public void PatternMatch_HandlesBothVariants()
    {
        ContentSource fromPath = new ContentSource.FromPath("/tmp/skill.md");
        ContentSource fromInline = new ContentSource.FromInline("inline body");

        Assert.Equal("path:/tmp/skill.md", Render(fromPath));
        Assert.Equal("inline:inline body", Render(fromInline));

        static string Render(ContentSource source) => source switch
        {
            ContentSource.FromPath p => $"path:{p.Value}",
            ContentSource.FromInline i => $"inline:{i.Content}",
            _ => throw new InvalidOperationException(),
        };
    }

    [Fact]
    public void AgentSkill_InlineFactory_BuildsFromInline()
    {
        var skill = AgentSkill.Inline("name", "body");

        Assert.Equal("name", skill.Name);
        var inline = Assert.IsType<ContentSource.FromInline>(skill.Source);
        Assert.Equal("body", inline.Content);
    }

    [Fact]
    public void AgentSkill_FromPathFactory_BuildsFromPath()
    {
        var skill = AgentSkill.FromPath("name", "/some/path");

        var fromPath = Assert.IsType<ContentSource.FromPath>(skill.Source);
        Assert.Equal("/some/path", fromPath.Value);
    }

    [Fact]
    public void SubAgentDefinition_Factories_RoundTrip()
    {
        var inline = SubAgentDefinition.Inline("sa", "body");
        var path = SubAgentDefinition.FromPath("sa", "/p.md");

        Assert.IsType<ContentSource.FromInline>(inline.Source);
        Assert.IsType<ContentSource.FromPath>(path.Source);
    }
}
