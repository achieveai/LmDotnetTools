using System.Diagnostics;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using FluentAssertions;
using Xunit;

namespace LmWorkflow.Tests;

public sealed class WorkflowScriptInvokerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "workflow-script-tests",
        Guid.NewGuid().ToString("N")
    );

    public WorkflowScriptInvokerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Python_receives_typed_stdin_and_environment_and_returns_whole_stdout()
    {
        Write(
            "space ; $name.py",
            "import json, os, sys\nx=json.load(sys.stdin)\nx['Input']['Environment']=os.environ['SCRIPT_TEST_VALUE']\nprint(json.dumps(x))\n"
        );
        var invoker = new WorkflowScriptInvoker(
            environment: new Dictionary<string, string> { ["SCRIPT_TEST_VALUE"] = "space ; $ value" }
        );
        var output = await invoker.InvokeAsync(
            "space ; $name.py",
            _directory,
            Context(),
            new JsonObject { ["Decision"] = true },
            default
        );
        var json = JsonNode.Parse(output)!;
        json["Input"]!["Decision"]!.GetValue<bool>().Should().BeTrue();
        json["Input"]!["Environment"]!.GetValue<string>().Should().Be("space ; $ value");
        json["Context"]!["RunId"]!.GetValue<string>().Should().Be("run-1");
    }

    [Fact]
    public async Task Nonzero_exit_reports_stderr_and_does_not_return_partial_stdout()
    {
        Write(
            "fail.py",
            "import sys\nprint('{\"Partial\":true}')\nprint('operation refused', file=sys.stderr)\nsys.exit(7)\n"
        );
        var invoker = new WorkflowScriptInvoker();
        await invoker
            .Invoking(x =>
                x.InvokeAsync(
                    "fail.py",
                    _directory,
                    Context(),
                    new JsonObject { ["Ignored"] = new string('x', 1024 * 1024) },
                    default
                )
            )
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*7*operation refused*");
    }

    [Fact]
    public async Task Input_pipe_failure_from_a_zero_exit_is_not_reported_as_a_script_failure()
    {
        Write("close.py", "import sys, time\nsys.stdin.close()\ntime.sleep(.2)\n");
        var invoker = new WorkflowScriptInvoker();

        await invoker
            .Invoking(x =>
                x.InvokeAsync(
                    "close.py",
                    _directory,
                    Context(),
                    new JsonObject { ["Ignored"] = new string('x', 1024 * 1024) },
                    default
                )
            )
            .Should()
            .ThrowAsync<IOException>();
    }

    [Fact]
    public async Task Output_limit_terminates_the_child_instead_of_buffering_unbounded_data()
    {
        Write("large.py", "import sys\nsys.stdout.write('x'*100000)\nsys.stdout.flush()\n");
        var invoker = new WorkflowScriptInvoker(maximumOutputCharacters: 1024);
        await invoker
            .Invoking(x => x.InvokeAsync("large.py", _directory, Context(), new JsonObject(), default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*limit*");
    }

    [Fact]
    public async Task Cancellation_kills_running_process_before_returning()
    {
        Write(
            "wait.py",
            "import os, time\nwith open('pid.txt','w') as f:\n f.write(str(os.getpid()))\ntime.sleep(60)\n"
        );
        using var cancellation = new CancellationTokenSource();
        var invoker = new WorkflowScriptInvoker();
        var running = invoker.InvokeAsync("wait.py", _directory, Context(), new JsonObject(), cancellation.Token);
        await WaitForFileAsync("pid.txt");
        var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "pid.txt")));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        var isRunning = false;
        try
        {
            using var process = Process.GetProcessById(pid);
            isRunning = !process.HasExited;
        }
        catch (ArgumentException) { }
        isRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_also_terminates_spawned_children()
    {
        Write(
            "parent.py",
            "import os, subprocess, sys, time\nchild=subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)'])\nwith open('child.txt','w') as f:\n f.write(str(child.pid))\ntime.sleep(60)\n"
        );
        using var cancellation = new CancellationTokenSource();
        var invoker = new WorkflowScriptInvoker();
        var running = invoker.InvokeAsync("parent.py", _directory, Context(), new JsonObject(), cancellation.Token);
        await WaitForFileAsync("child.txt");
        var childId = int.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "child.txt")));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        try
        {
            using var child = Process.GetProcessById(childId);
            child.HasExited.Should().BeTrue();
        }
        catch (ArgumentException) { }
    }

    [Fact]
    public async Task Successful_parent_that_leaves_a_child_quarantines_the_workspace()
    {
        Write(
            "detached.py",
            "import subprocess, sys, time\nchild=subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)\nwith open('child.txt','w') as f:\n f.write(str(child.pid))\ntime.sleep(.2)\n"
        );
        Write("ok.py", "print('{}')\n");
        var invoker = new WorkflowScriptInvoker();

        await invoker
            .Invoking(x => x.InvokeAsync("detached.py", _directory, Context(), new JsonObject(), default))
            .Should()
            .ThrowAsync<WorkflowScriptTerminationException>()
            .WithMessage("*left child processes running*");

        var childId = int.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "child.txt")));
        await WaitForExitAsync(childId);
        await invoker
            .Invoking(x => x.InvokeAsync("ok.py", _directory, Context(), new JsonObject(), default))
            .Should()
            .ThrowAsync<WorkflowScriptTerminationException>()
            .WithMessage("*Workspace remains unavailable*");
    }

    [Fact]
    public async Task Unreadable_process_tree_enumeration_quarantines_the_workspace()
    {
        Write("wait.py", "import time\ntime.sleep(60)\n");
        Write("ok.py", "print('{}')\n");
        var invoker = new WorkflowScriptInvoker(_ =>
            throw new UnauthorizedAccessException("The Linux process table cannot be read.")
        );

        await invoker
            .Invoking(x => x.InvokeAsync("wait.py", _directory, Context(), new JsonObject(), default))
            .Should()
            .ThrowAsync<WorkflowScriptTerminationException>()
            .WithMessage("*Could not establish the script process tree*");

        await invoker
            .Invoking(x => x.InvokeAsync("ok.py", _directory, Context(), new JsonObject(), default))
            .Should()
            .ThrowAsync<WorkflowScriptTerminationException>()
            .WithMessage("*Workspace remains unavailable*");
    }

    [Fact]
    public async Task Concurrent_scripts_for_one_workspace_do_not_overlap()
    {
        Write(
            "lock.py",
            "import os, time\nfd=os.open('exclusive',os.O_CREAT|os.O_EXCL|os.O_WRONLY)\ntime.sleep(0.2)\nos.close(fd)\nos.unlink('exclusive')\nprint('{}')\n"
        );
        var invoker = new WorkflowScriptInvoker();
        var runs = Enumerable
            .Range(0, 3)
            .Select(_ => invoker.InvokeAsync("lock.py", _directory, Context(), new JsonObject(), default));
        var outputs = await Task.WhenAll(runs);
        outputs.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("../outside.py")]
    [InlineData("arbitrary.exe")]
    public async Task Uncontained_or_unsupported_scripts_are_rejected(string script)
    {
        var invoker = new WorkflowScriptInvoker();
        await invoker
            .Invoking(x => x.InvokeAsync(script, _directory, Context(), new JsonObject(), default))
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    private JsonObject Context() =>
        new()
        {
            ["RunId"] = "run-1",
            ["StepId"] = "test",
            ["Attempt"] = 1,
            ["RunDirectory"] = _directory,
        };

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_directory, name), text);

    private async Task WaitForFileAsync(string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(_directory, name)))
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task WaitForExitAsync(int processId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            await Task.Delay(25, timeout.Token);
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
