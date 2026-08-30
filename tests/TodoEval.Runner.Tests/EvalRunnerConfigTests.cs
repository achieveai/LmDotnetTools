namespace TodoEval.Runner.Tests;

public class EvalRunnerConfigTests
{
    [Fact]
    public void Load_NoFile_YieldsTheDocumentedDefaults()
    {
        var config = EvalRunnerConfig.Load(configPath: null);

        config.Models.Should().Equal("deepseek-v4-flash", "gpt-5.6-luna");
        config.Seeds.Should().Be(5);
        config.PerRunTimeoutMinutes.Should().Be(20);
        config.MaxParallelRuns.Should().Be(1, "runs are sequential unless explicitly parallelized");
        config.ModeName.Should().Be("todo-eval");
        config.Topics.Should().HaveCount(5);
    }

    [Fact]
    public void Load_File_OverridesDefaultsAndKeepsTheRest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"todo-eval-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            """
            {
              // comments are allowed
              "models": ["m-alpha"],
              "seeds": 2,
              "host": { "publishDir": "C:/published/host", "port": 5999 }
            }
            """
        );
        try
        {
            var config = EvalRunnerConfig.Load(path);

            config.Models.Should().Equal("m-alpha");
            config.Seeds.Should().Be(2);
            config.Host.PublishDir.Should().Be("C:/published/host");
            config.Host.Port.Should().Be(5999);
            config.PerRunTimeoutMinutes.Should().Be(20, "unset knobs keep their defaults");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        var act = () => EvalRunnerConfig.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-419.json"));

        act.Should().Throw<FileNotFoundException>();
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(5, 0, 1)]
    [InlineData(5, 20, 0)]
    public void Validate_RejectsOutOfRangeCounts(int seeds, int timeoutMinutes, int parallel)
    {
        var config = new EvalRunnerConfig
        {
            Seeds = seeds,
            PerRunTimeoutMinutes = timeoutMinutes,
            MaxParallelRuns = parallel,
        };

        var act = config.Validate;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_RejectsDuplicateModels()
    {
        var config = new EvalRunnerConfig { Models = ["a", "a"] };

        var act = config.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*duplicates*");
    }

    [Fact]
    public void TopicForSeed_CyclesWhenSeedsExceedTopics()
    {
        var config = new EvalRunnerConfig { Topics = ["t0", "t1"], Seeds = 5 };

        config.TopicForSeed(0).Should().Be("t0");
        config.TopicForSeed(1).Should().Be("t1");
        config.TopicForSeed(2).Should().Be("t0");
    }
}

public class CliOptionsTests
{
    [Fact]
    public void Parse_ReadsAllSwitches()
    {
        var options = CliOptions.Parse([
            "--eval-dir",
            "evals/x",
            "--models",
            "m1, m2",
            "--seeds",
            "3",
            "--parallel",
            "2",
            "--timeout-min",
            "7",
            "--host-publish-dir",
            "C:/pub",
            "--env-file",
            ".env.eval",
            "--allow-missing-models",
        ]);

        options.EvalDir.Should().Be("evals/x");
        options.Models.Should().Equal("m1", "m2");
        options.Seeds.Should().Be(3);
        options.MaxParallelRuns.Should().Be(2);
        options.PerRunTimeoutMinutes.Should().Be(7);
        options.HostPublishDir.Should().Be("C:/pub");
        options.EnvFile.Should().Be(".env.eval");
        options.AllowMissingModels.Should().BeTrue();
    }

    [Fact]
    public void Parse_UnknownSwitch_Throws()
    {
        var act = () => CliOptions.Parse(["--frobnicate"]);

        act.Should().Throw<ArgumentException>().WithMessage("*--frobnicate*");
    }

    [Fact]
    public void Parse_SwitchMissingItsValue_Throws()
    {
        var act = () => CliOptions.Parse(["--seeds"]);

        act.Should().Throw<ArgumentException>().WithMessage("*expects a value*");
    }

    [Fact]
    public void ApplyTo_OverlaysOnlyTheGivenSwitches()
    {
        var options = CliOptions.Parse(["--seeds", "2", "--host-publish-dir", "C:/pub"]);

        var merged = options.ApplyTo(new EvalRunnerConfig());

        merged.Seeds.Should().Be(2);
        merged.Host.PublishDir.Should().Be("C:/pub");
        merged.Models.Should().Equal("deepseek-v4-flash", "gpt-5.6-luna");
        merged.MaxParallelRuns.Should().Be(1);
    }

    [Fact]
    public void ApplyTo_ValidatesTheMergedResult()
    {
        var options = CliOptions.Parse(["--seeds", "0"]);

        var act = () => options.ApplyTo(new EvalRunnerConfig());

        act.Should().Throw<InvalidOperationException>();
    }
}
