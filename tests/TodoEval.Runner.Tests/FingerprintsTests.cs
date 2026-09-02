using System.Text;
using TodoEval.Runner.Metrics;

namespace TodoEval.Runner.Tests;

/// <summary>
/// Pins for the three fingerprints. What matters is not the digest values but the two properties
/// #677 will refuse on: the corpus hash MOVES when any corpus file moves, and the evaluator hash
/// does NOT move when something that cannot change a measured number moves.
/// </summary>
public class FingerprintsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"todo-eval-fp-{Guid.NewGuid():N}");

    public FingerprintsTests()
    {
        Directory.CreateDirectory(_dir);
        foreach (var name in Fingerprints.CorpusFileNames)
        {
            File.WriteAllText(Path.Combine(_dir, name), $"contents of {name}\n");
        }
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CorpusHash_IsStableAcrossCalls()
    {
        Fingerprints.CorpusHash(_dir).Should().Be(Fingerprints.CorpusHash(_dir));
        Fingerprints.CorpusHash(_dir).Should().HaveLength(64);
    }

    [Theory]
    [InlineData("task.md")]
    [InlineData("mode.json")]
    [InlineData("expected-board.json")]
    public void CorpusHash_ChangesWhenAnyOneCorpusFileChanges(string fileName)
    {
        var before = Fingerprints.CorpusHash(_dir);

        File.AppendAllText(Path.Combine(_dir, fileName), "x");

        Fingerprints.CorpusHash(_dir).Should().NotBe(before);
    }

    [Fact]
    public void CorpusHash_IgnoresLineEndings_SoACrlfCheckoutHashesLikeAnLfOne()
    {
        // Without this the same commit fingerprints differently on two machines, and #677 would
        // refuse to compare a Windows sweep with a Linux one for no real reason.
        var lf = Fingerprints.CorpusHash(_dir);
        foreach (var name in Fingerprints.CorpusFileNames)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n"));
        }

        Fingerprints.CorpusHash(_dir).Should().Be(lf);
    }

    [Fact]
    public void CorpusHash_SeparatesFiles_SoContentCannotSlideBetweenThem()
    {
        // Length-prefixing each contribution is what stops "ab" + "" from hashing like "a" + "b".
        var before = Fingerprints.CorpusHash(_dir);
        File.WriteAllText(Path.Combine(_dir, "task.md"), "contents of task.md\ncontents of ");
        File.WriteAllText(Path.Combine(_dir, "mode.json"), "mode.json\n");

        Fingerprints.CorpusHash(_dir).Should().NotBe(before);
    }

    [Fact]
    public void MissingCorpusFile_IsDistinguishableFromAnEmptyOne()
    {
        File.WriteAllText(Path.Combine(_dir, "mode.json"), "");
        var empty = Fingerprints.CorpusHash(_dir);

        File.Delete(Path.Combine(_dir, "mode.json"));

        Fingerprints.CorpusHash(_dir).Should().NotBe(empty);
    }

    [Fact]
    public void EvaluatorHash_CoversBothVocabulariesAndTheStormThreshold()
    {
        // The hash is over constants, so it is asserted by recomputing the documented recipe: any
        // change to the recipe that this test does not mirror shows up here as an inequality.
        var expected = Hex(
            $"{Fingerprints.SpecHash()}\n"
                + $"{string.Join(",", TaskTools.All.Order(StringComparer.Ordinal))}\n"
                + $"{string.Join(",", CoordinationTools.All.Order(StringComparer.Ordinal))}\n"
                + $"{RetryStormDetector.StormThreshold}\n"
                + Fingerprints.RedactedArgsKey
        );

        Fingerprints.EvaluatorHash().Should().Be(expected);
    }

    [Fact]
    public void SpecHash_IsThePairOfContractStrings()
    {
        Fingerprints.SpecHash().Should().Be(Hex($"{Fingerprints.SpecVersion}\n{Fingerprints.Schema}"));
    }

    [Fact]
    public void ComputedSet_CarriesTheSpecVersionAlongsideTheHashes()
    {
        var set = FingerprintSet.Compute(RepoPaths.EvalDir);

        set.SpecVersion.Should().Be(Fingerprints.SpecVersion);
        set.TaskCorpusHash.Should().Be(Fingerprints.CorpusHash(RepoPaths.EvalDir));
        set.EvaluatorHash.Should().Be(Fingerprints.EvaluatorHash());
    }

    private static string Hex(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
