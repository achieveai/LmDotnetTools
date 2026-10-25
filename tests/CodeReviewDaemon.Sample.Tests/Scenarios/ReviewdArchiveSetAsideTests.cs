extern alias Reviewd;

using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     The review archive keeps what the review wrote (Kay9). A 174 MB data dump failed the archive push at GitHub's
///     100 MB limit (Polymer #5807049) and a link failed the commit outright (ideas-exp #5804611). The commit step sets
///     such files aside first. The sandbox's own rule runs here against real files.
/// </summary>
public sealed class ReviewdArchiveSetAsideTests
{
    [SkippableFact]
    public async Task The_commit_keeps_the_review_record_and_sets_aside_data_links_and_special_files()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the rule is checked against Linux links and FIFOs, as in the sandbox");
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox's archive rule cannot be run");
        var work = Path.Combine(Path.GetTempPath(), $"reviewd-set-aside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var script = Path.Combine(work, "sandbox.py");
        using (
            var resource = typeof(ReviewRunner).Assembly.GetManifestResourceStream(
                "Reviewd.Sample.ReviewRunnerSandbox.py"
            )!
        )
        using (var file = File.Create(script))
            await resource.CopyToAsync(file);
        try
        {
            // The folder holds a review's own text, the two files a resumed commit reads again (oversized here), and
            // what the archive does not keep. The rule runs twice, as a retried commit runs it again.
            const string harness = """
                import ast, json, os, sys
                from pathlib import Path
                script, work = sys.argv[1], Path(sys.argv[2])
                names = {'set_aside', 'set_aside_unarchivable', 'ARCHIVE_FILE_LIMIT', 'ARCHIVE_ALWAYS_KEPT'}
                body = [n for n in ast.parse(Path(script).read_text(encoding='utf-8')).body
                        if (isinstance(n, ast.FunctionDef) and n.name in names)
                        or (isinstance(n, ast.Assign) and any(getattr(t, 'id', None) in names for t in n.targets))]
                scope = {}
                exec('import hashlib, json, os, shutil\nfrom pathlib import Path\n', scope)
                exec(compile(ast.Module(body, []), 'sandbox', 'exec'), scope)
                folder, quarantine = work / 'PRs' / 'x-1', work / 'quarantine'
                (folder / 'sub').mkdir(parents=True)
                mib = 1024 * 1024
                (folder / 'setup-context.json').write_text('c' * (2 * mib))
                (folder / 'publication_receipts.json').write_text('r' * (2 * mib))
                (folder / 'findings.md').write_text('# findings\n')
                (folder / 'sub' / 'at-limit.txt').write_text('z' * mib)
                (folder / 'dump.json').write_text('y' * (mib + 1))
                (folder / 'sub' / 'setup-context.json').write_text('w' * (mib + 1))
                (folder / 'sub' / 'chart.png').write_bytes(b'\x89PNG\x00small')
                (folder / 'link').symlink_to('findings.md')
                (folder / 'tree').symlink_to('sub')
                os.mkfifo(folder / 'pipe')
                first = scope['set_aside_unarchivable'](folder, quarantine)
                second = scope['set_aside_unarchivable'](folder, quarantine)
                notes = {p.relative_to(folder).as_posix(): json.loads(p.read_text())
                         for p in folder.rglob('*.omitted.json')}
                print(json.dumps({
                    'setAside': sorted(n['omitted'] + ':' + n['kind'] for n in first),
                    'again': len(second),
                    'left': sorted(p.relative_to(folder).as_posix() for p in folder.rglob('*')
                                   if not p.name.endswith('.omitted.json') and not p.is_dir()),
                    'quarantined': sorted(p.name for p in quarantine.iterdir()),
                    'dumpNote': notes.get('dump.json.omitted.json'),
                }))
                """;
            var start = new ProcessStartInfo(python!) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-c", harness, script, work })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(0, await stderr);
            var result = JsonNode.Parse(await stdout)!.AsObject();

            result["setAside"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal(
                    "dump.json:large",
                    "link:symlink",
                    "pipe:special",
                    "sub/chart.png:binary",
                    "sub/setup-context.json:large",
                    "tree:symlink"
                );
            result["left"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal(
                    ["findings.md", "publication_receipts.json", "setup-context.json", "sub/at-limit.txt"],
                    "the review's text and the files a resumed commit reads again stay"
                );
            result["again"]!.GetValue<int>().Should().Be(0, "a retried commit finds nothing more to set aside");
            result["quarantined"]!.AsArray().Should().HaveCount(6, "nothing is deleted, only moved out");
            var note = result["dumpNote"]!;
            note["bytes"]!.GetValue<long>().Should().Be((1024 * 1024) + 1);
            note["sha256"]!.GetValue<string>().Should().MatchRegex("^[0-9a-f]{64}$");
        }
        finally
        {
            RealGitFixture.ForceDelete(work);
        }
    }
}
