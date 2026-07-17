using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

public class CommandOperationTests
{
    [Theory]
    [InlineData("")] // empty
    [InlineData("   ")] // whitespace-only (trims to empty)
    [InlineData(".")] // reserved
    [InlineData("..")] // reserved
    [InlineData("review/op-1")] // path separator
    [InlineData("a\\b")] // backslash
    [InlineData("a b")] // interior whitespace
    [InlineData("a\nb")] // control char
    [InlineData("a\0b")] // NUL
    [InlineData("café")] // non-ASCII
    public void ValidateAndCanonicalize_RejectsValuesOutsideTheGatewayGrammar(string operationId)
    {
        var act = () => CommandOperation.ValidateAndCanonicalizeOperationId(operationId, "operationId");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateAndCanonicalize_RejectsTooLong()
    {
        var act = () =>
            CommandOperation.ValidateAndCanonicalizeOperationId(
                new string('a', CommandOperation.MaxOperationIdLength + 1),
                "operationId"
            );

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateAndCanonicalize_AcceptsBoundedGrammarId()
    {
        var act = () =>
            CommandOperation.ValidateAndCanonicalizeOperationId(new string('a', CommandOperation.MaxOperationIdLength), "operationId");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateAndCanonicalize_TrimsSurroundingWhitespaceToTheCanonicalForm()
    {
        CommandOperation.ValidateAndCanonicalizeOperationId("  op-1.2_3  ", "operationId").Should().Be("op-1.2_3");
    }

    [Fact]
    public void ResolveOperationId_NullGeneratesCollisionResistantId_NonNullPassesThrough()
    {
        CommandOperation.ResolveOperationId("caller-id").Should().Be("caller-id");

        var generated = CommandOperation.ResolveOperationId(null);
        generated.Should().MatchRegex("^[0-9a-f]{32}$");
        CommandOperation.ResolveOperationId(null).Should().NotBe(generated);
    }
}
