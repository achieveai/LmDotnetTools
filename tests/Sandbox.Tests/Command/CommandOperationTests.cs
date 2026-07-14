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
}
