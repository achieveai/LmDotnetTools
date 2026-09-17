using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// Pins <see cref="SandboxEnvRules"/> — the app-side mirror of the gateway's <c>gateway_types::sandbox_env</c>
/// validation (SandboxedOstoolsMcpServer #183) — so a map the app sends can never be accepted locally and then
/// rejected remotely (or vice versa) because the two grammars drifted apart.
/// </summary>
public class SandboxEnvRulesTests
{
    private static Dictionary<string, string> Env(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    [Theory]
    [InlineData("FOO")]
    [InlineData("_x1")]
    [InlineData("PATH")]
    public void FindInvalidKeys_AcceptsWellFormedKeys(string key)
    {
        var invalid = SandboxEnvRules.FindInvalidKeys(Env((key, "v")));

        invalid.Should().BeEmpty();
    }

    [Theory]
    [InlineData("1ABC")]
    [InlineData("bad-key")]
    [InlineData("with space")]
    [InlineData("")]
    public void FindInvalidKeys_RejectsMalformedKeys(string key)
    {
        var invalid = SandboxEnvRules.FindInvalidKeys(Env((key, "v")));

        invalid.Should().Contain(key);
    }

    [Theory]
    [InlineData("http_proxy")]
    [InlineData("Sandbox_Home")]
    [InlineData("HTTP_PROXY")]
    [InlineData("sandbox_allowed_paths")]
    public void FindInvalidKeys_RejectsProtectedNames_CaseInsensitively(string key)
    {
        var invalid = SandboxEnvRules.FindInvalidKeys(Env((key, "v")));

        invalid.Should().Contain(key);
    }

    [Fact]
    public void FindInvalidKeys_PathIsAllowed_NotProtected()
    {
        var invalid = SandboxEnvRules.FindInvalidKeys(Env(("PATH", "/usr/bin")));

        invalid.Should().BeEmpty();
    }

    [Fact]
    public void FindInvalidKeys_NulInKey_IsRejected()
    {
        var key = "FOO\0BAR";
        var invalid = SandboxEnvRules.FindInvalidKeys(Env((key, "v")));

        invalid.Should().Contain(key);
    }

    [Fact]
    public void FindInvalidKeys_NulInValue_IsRejected()
    {
        var invalid = SandboxEnvRules.FindInvalidKeys(Env(("FOO", "bad\0value")));

        invalid.Should().Contain("FOO");
    }

    [Fact]
    public void FindInvalidKeys_CaseDuplicateKeys_NamesBothVariants()
    {
        // IReadOnlyDictionary construction is Ordinal, so "FOO" and "foo" can coexist as distinct entries
        // going INTO FindInvalidKeys even though the gateway (and this rule) treats them as a collision.
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["FOO"] = "1", ["foo"] = "2" };

        var invalid = SandboxEnvRules.FindInvalidKeys(env);

        invalid.Should().Contain("FOO");
        invalid.Should().Contain("foo");
    }

    [Fact]
    public void FindInvalidKeys_ValueOverMaxBytes_IsRejected()
    {
        var value = new string('a', SandboxEnvRules.MaxValueBytes + 1);
        var invalid = SandboxEnvRules.FindInvalidKeys(Env(("FOO", value)));

        invalid.Should().Contain("FOO");
    }

    [Fact]
    public void FindInvalidKeys_ValueAtMaxBytes_IsAccepted()
    {
        var value = new string('a', SandboxEnvRules.MaxValueBytes);
        var invalid = SandboxEnvRules.FindInvalidKeys(Env(("FOO", value)));

        invalid.Should().BeEmpty();
    }

    [Fact]
    public void FindInvalidKeys_KeyOverMaxBytes_IsRejected()
    {
        var key = "K" + new string('a', SandboxEnvRules.MaxKeyBytes);
        var invalid = SandboxEnvRules.FindInvalidKeys(Env((key, "v")));

        invalid.Should().Contain(key);
    }

    [Fact]
    public void FindInvalidKeys_TooManyKeys_NamesEveryKey()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < SandboxEnvRules.MaxKeys + 1; i++)
        {
            env[$"K{i}"] = "v";
        }

        var invalid = SandboxEnvRules.FindInvalidKeys(env);

        invalid.Should().HaveCount(env.Count);
        invalid.Should().BeEquivalentTo(env.Keys);
    }

    [Fact]
    public void FindInvalidKeys_AtMaxKeys_IsAccepted()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < SandboxEnvRules.MaxKeys; i++)
        {
            env[$"K{i}"] = "v";
        }

        var invalid = SandboxEnvRules.FindInvalidKeys(env);

        invalid.Should().BeEmpty();
    }

    [Fact]
    public void FindInvalidKeys_TotalBytesOverMax_NamesEveryKey()
    {
        // Two keys whose combined key+value bytes exceed MaxTotalBytes but each individually stays under
        // MaxValueBytes, so only the map-level total check can catch it.
        var half = new string('a', (SandboxEnvRules.MaxTotalBytes / 2) + 1);
        var env = Env(("A", half), ("B", half));

        var invalid = SandboxEnvRules.FindInvalidKeys(env);

        invalid.Should().HaveCount(2);
        invalid.Should().BeEquivalentTo(["A", "B"]);
    }

    [Fact]
    public void FindInvalidKeys_NullOrEmpty_IsValid()
    {
        SandboxEnvRules.FindInvalidKeys(new Dictionary<string, string>()).Should().BeEmpty();
    }

    [Fact]
    public void Validate_NullEnv_DoesNotThrow()
    {
        var act = () => SandboxEnvRules.Validate(null, "workspace");

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_EmptyEnv_DoesNotThrow()
    {
        var act = () => SandboxEnvRules.Validate(new Dictionary<string, string>(), "workspace");

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_InvalidEnv_ThrowsWithLayerAndKeys_AndNeverIncludesTheValue()
    {
        const string secretValue = "super-secret-value";
        var env = Env(("bad-key", secretValue));

        var act = () => SandboxEnvRules.Validate(env, "workspace");

        var exception = act.Should().Throw<SandboxEnvValidationException>().Which;
        exception.Layer.Should().Be("workspace");
        exception.Keys.Should().Contain("bad-key");
        exception.Message.Should().Be("Invalid sandbox environment in layer 'workspace': bad-key");
        exception.Message.Should().NotContain(secretValue);
    }

    [Fact]
    public void Validate_MultipleInvalidKeys_MessageJoinsThemWithCommaSpace()
    {
        var env = Env(("bad-key", "v"), ("1ABC", "v"));

        var act = () => SandboxEnvRules.Validate(env, "app");

        var exception = act.Should().Throw<SandboxEnvValidationException>().Which;
        exception.Message.Should().StartWith("Invalid sandbox environment in layer 'app': ");
        exception.Message.Should().Contain("bad-key");
        exception.Message.Should().Contain("1ABC");
    }

    [Fact]
    public void Merge_LaterLayerWins()
    {
        var first = Env(("FOO", "1"), ("BAR", "b"));
        var second = Env(("FOO", "2"));

        var merged = SandboxEnvRules.Merge(first, second);

        merged.Should().Equal(new Dictionary<string, string> { ["FOO"] = "2", ["BAR"] = "b" });
    }

    [Fact]
    public void Merge_SkipsNullLayers()
    {
        var first = Env(("FOO", "1"));

        var merged = SandboxEnvRules.Merge(first, null, Env(("BAR", "2")));

        merged.Should().Equal(new Dictionary<string, string> { ["FOO"] = "1", ["BAR"] = "2" });
    }

    [Fact]
    public void Merge_NoLayers_ReturnsEmpty()
    {
        var merged = SandboxEnvRules.Merge();

        merged.Should().BeEmpty();
    }

    [Fact]
    public void Diff_ChangedAndNewKeys_MapToTheirValues()
    {
        var lastApplied = Env(("FOO", "1"), ("OLD", "o"));
        var desired = Env(("FOO", "2"), ("OLD", "o"), ("NEW", "n"));

        var diff = SandboxEnvRules.Diff(lastApplied, desired);

        diff.Should().Equal(new Dictionary<string, string?> { ["FOO"] = "2", ["NEW"] = "n" });
    }

    [Fact]
    public void Diff_KeysRemovedFromDesired_MapToNull()
    {
        var lastApplied = Env(("FOO", "1"), ("OLD", "o"));
        var desired = Env(("FOO", "1"));

        var diff = SandboxEnvRules.Diff(lastApplied, desired);

        diff.Should().Equal(new Dictionary<string, string?> { ["OLD"] = null });
    }

    [Fact]
    public void Diff_IdenticalMaps_IsEmpty()
    {
        var lastApplied = Env(("FOO", "1"), ("BAR", "b"));
        var desired = Env(("FOO", "1"), ("BAR", "b"));

        var diff = SandboxEnvRules.Diff(lastApplied, desired);

        diff.Should().BeEmpty();
    }

    /// <summary>
    /// A null VALUE is reachable from the wire even though the map's value type is non-nullable:
    /// <c>{"env":{"K":null}}</c> deserializes to exactly this, on workspace create/update, mode
    /// create/update and S2S provision alike. It must be reported as an invalid key like any other
    /// rule violation. Before the null guard, <c>Encoding.UTF8.GetByteCount(null)</c> threw
    /// <see cref="ArgumentNullException"/> from inside the validator — past every controller's
    /// <c>invalid_env</c> handling, so the caller got a 500 with no indication of which key was bad.
    /// </summary>
    [Fact]
    public void FindInvalidKeys_NullValue_IsReportedAsInvalidRatherThanThrowing()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["GOOD"] = "v", ["BAD"] = null! };

        var invalid = SandboxEnvRules.FindInvalidKeys(env);

        invalid.Should().Contain("BAD");
        invalid.Should().NotContain("GOOD", "a null value condemns only its own key");
    }

    /// <summary>The same condition through the throwing entry point the controllers actually call.</summary>
    [Fact]
    public void Validate_NullValue_ThrowsSandboxEnvValidationException_NotArgumentNullException()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["BAD"] = null! };

        var act = () => SandboxEnvRules.Validate(env, "workspace");

        act.Should().Throw<SandboxEnvValidationException>().Which.Keys.Should().Contain("BAD");
    }
}
