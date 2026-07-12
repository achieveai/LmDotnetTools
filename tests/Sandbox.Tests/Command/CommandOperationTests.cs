using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

public class CommandOperationTests
{
    [Fact]
    public void ValidateOperationId_RejectsEmptyTooLongAndControlCharacters()
    {
        var empty = () => CommandOperation.ValidateOperationId("", "operationId");
        var tooLong = () =>
            CommandOperation.ValidateOperationId(
                new string('a', CommandOperation.MaxOperationIdLength + 1),
                "operationId"
            );
        var control = () => CommandOperation.ValidateOperationId("a\nb", "operationId");
        var nul = () => CommandOperation.ValidateOperationId("a\0b", "operationId");

        empty.Should().Throw<ArgumentException>();
        tooLong.Should().Throw<ArgumentException>();
        control.Should().Throw<ArgumentException>();
        nul.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateOperationId_AcceptsBoundedPrintableId()
    {
        var act = () =>
            CommandOperation.ValidateOperationId(new string('a', CommandOperation.MaxOperationIdLength), "operationId");

        act.Should().NotThrow();
    }

    [Fact]
    public void ResolveOperationId_NullGeneratesCollisionResistantId_NonNullPassesThrough()
    {
        CommandOperation.ResolveOperationId("caller-id").Should().Be("caller-id");

        var generated = CommandOperation.ResolveOperationId(null);
        generated.Should().MatchRegex("^[0-9a-f]{32}$");
        CommandOperation.ResolveOperationId(null).Should().NotBe(generated);
    }

    [Fact]
    public void OperationDirectoryName_IsDeterministicFixedLengthHex_NeverTheRawId()
    {
        const string hostileId = "../../etc/passwd";

        var name = CommandOperation.OperationDirectoryName("sess-1", hostileId);

        name.Should().MatchRegex("^[0-9a-f]{32}$");
        name.Should().NotContain("/").And.NotContain("..");
        name.Should().NotContain(hostileId);
        CommandOperation.OperationDirectoryName("sess-1", hostileId).Should().Be(name);
    }

    [Fact]
    public void OperationDirectoryName_DiffersBySessionAndOperationId()
    {
        var baseline = CommandOperation.OperationDirectoryName("sess-1", "op-1");

        CommandOperation.OperationDirectoryName("sess-2", "op-1").Should().NotBe(baseline);
        CommandOperation.OperationDirectoryName("sess-1", "op-2").Should().NotBe(baseline);
    }

    [Fact]
    public void CanonicalDigest_IsDeterministic64Hex()
    {
        var digest = CommandOperation.CanonicalDigest("sess-1", ["ls", "-la"], "work", 120);

        digest.Should().MatchRegex("^[0-9a-f]{64}$");
        CommandOperation.CanonicalDigest("sess-1", ["ls", "-la"], "work", 120).Should().Be(digest);
    }

    [Fact]
    public void CanonicalDigest_ChangesWithEveryComponent()
    {
        var baseline = CommandOperation.CanonicalDigest("sess-1", ["ls", "-la"], "work", 120);

        CommandOperation.CanonicalDigest("sess-2", ["ls", "-la"], "work", 120).Should().NotBe(baseline);
        CommandOperation.CanonicalDigest("sess-1", ["ls", "-l"], "work", 120).Should().NotBe(baseline);
        CommandOperation.CanonicalDigest("sess-1", ["ls", "-la"], "other", 120).Should().NotBe(baseline);
        CommandOperation.CanonicalDigest("sess-1", ["ls", "-la"], "work", 121).Should().NotBe(baseline);
    }

    [Fact]
    public void CanonicalDigest_LengthPrefixing_DisambiguatesArgvBoundaries()
    {
        // Without length-prefixing, ["a","bc"] and ["ab","c"] would concatenate to the same bytes.
        var left = CommandOperation.CanonicalDigest("s", ["a", "bc"], "", 1);
        var right = CommandOperation.CanonicalDigest("s", ["ab", "c"], "", 1);

        left.Should().NotBe(right);
    }
}
