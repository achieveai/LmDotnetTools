using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

public class ConversationWorkingDirectoryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(120)]
    [InlineData(600)]
    [InlineData(3600)]
    public void Bash_sends_its_validated_timeout_to_the_gateway(int seconds)
    {
        // Without it the SDK sends its 5-minute client default: 600 seconds would be cut short, and a
        // local cancellation alone would leave the remote process running.
        var command = ConversationWorkingDirectory.BashCommand("make test", "repo/src", seconds);

        command.ExecutionTimeout.Should().Be(TimeSpan.FromSeconds(seconds));
        command.Arguments.Should().Equal("bash", "-c", "make test");
        command.WorkingDirectory.Should().Be("repo/src");
    }
}
