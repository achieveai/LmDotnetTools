using TodoEval.Runner.Metrics;

namespace TodoEval.Runner.Tests;

public class JsonCanonicalizerTests
{
    [Fact]
    public void KeyOrder_DoesNotMatter()
    {
        var a = JsonCanonicalizer.CanonicalizeArgs("""{"taskId":"2.1","noteText":"x","subtaskId":0}""");
        var b = JsonCanonicalizer.CanonicalizeArgs("""{"noteText":"x","subtaskId":0,"taskId":"2.1"}""");

        a.Should().Be(b).And.Be("""{"noteText":"x","subtaskId":0,"taskId":"2.1"}""");
    }

    [Fact]
    public void NestedObjectKeys_AreSortedAtEveryLevel()
    {
        JsonCanonicalizer
            .CanonicalizeArgs("""{"b":{"z":1,"a":2},"a":[{"y":1,"x":2}]}""")
            .Should()
            .Be("""{"a":[{"x":2,"y":1}],"b":{"a":2,"z":1}}""");
    }

    [Fact]
    public void Whitespace_IsInsignificant()
    {
        JsonCanonicalizer.CanonicalizeArgs("{ \"a\" : 1 }").Should().Be("""{"a":1}""");
    }

    [Fact]
    public void ArrayOrder_IsPreserved()
    {
        JsonCanonicalizer.CanonicalizeArgs("[3,1,2]").Should().Be("[3,1,2]");
    }

    [Fact]
    public void AbsentOrEmptyArgs_CanonicalizeToEmptyString()
    {
        JsonCanonicalizer.CanonicalizeArgs(null).Should().Be("");
        JsonCanonicalizer.CanonicalizeArgs("").Should().Be("");
    }

    [Fact]
    public void ParseFailure_FallsBackToTheRawStringVerbatim()
    {
        // Per metrics-spec.md the raw string is kept exactly as recorded — not trimmed — so a
        // malformed args payload compares equal only to byte-identical retries.
        JsonCanonicalizer.CanonicalizeArgs(" not json { ").Should().Be(" not json { ");
    }
}
