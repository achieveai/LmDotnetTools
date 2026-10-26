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
                names = {'set_aside', 'set_aside_unarchivable', 'fitting_name', 'ARCHIVE_FILE_LIMIT',
                         'ARCHIVE_ALWAYS_KEPT'}
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
                # Both flatten to one quarantine name: the second takes a suffix, never moving over the first.
                (folder / 'a').mkdir()
                (folder / 'a' / 'b.bin').write_bytes(b'\x00b')
                (folder / 'a__b.bin').write_bytes(b'\x00ab')
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
                    "a/b.bin:binary",
                    "a__b.bin:binary",
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
            result["quarantined"]!.AsArray().Should().HaveCount(8, "nothing is deleted, only moved out");
            var note = result["dumpNote"]!;
            note["bytes"]!.GetValue<long>().Should().Be((1024 * 1024) + 1);
            note["sha256"]!.GetValue<string>().Should().MatchRegex("^[0-9a-f]{64}$");
        }
        finally
        {
            RealGitFixture.ForceDelete(work);
        }
    }

    [SkippableTheory]
    // One file name holds at most 255 bytes. A deep evidence path flattens to a longer quarantine name, and a file
    // named near the limit has no room for its note's ending. Either one failed every commit of the review.
    [InlineData("deep")]
    [InlineData("long-name")]
    public async Task A_path_or_name_too_long_for_one_file_name_is_still_set_aside_and_noted(string shape)
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the rule is checked against Linux file name limits, as in the sandbox");
        var work = Directory.CreateTempSubdirectory("reviewd-set-aside-").FullName;
        try
        {
            // 'deep' flattens to 264 bytes. 'long-name' is 250 bytes in 127 characters, so only a byte count sees it.
            const string harness = """
                import ast, json, sys
                from pathlib import Path
                script, work, shape = sys.argv[1], Path(sys.argv[2]), sys.argv[3]
                names = {'set_aside', 'set_aside_unarchivable', 'fitting_name', 'ARCHIVE_FILE_LIMIT',
                         'ARCHIVE_ALWAYS_KEPT'}
                body = [n for n in ast.parse(Path(script).read_text(encoding='utf-8')).body
                        if (isinstance(n, ast.FunctionDef) and n.name in names)
                        or (isinstance(n, ast.Assign) and any(getattr(t, 'id', None) in names for t in n.targets))]
                scope = {}
                exec('import hashlib, json, os, shutil\nfrom pathlib import Path\n', scope)
                exec(compile(ast.Module(body, []), 'sandbox', 'exec'), scope)
                folder, quarantine = work / 'PRs' / 'x-1', work / 'quarantine'
                parts = (['evidence', 'a' * 76, 'b' * 60, 'c' * 38, 'd' * 70 + '.png'] if shape == 'deep'
                         else ['\u00e9' * 123 + '.png'])
                binary = folder.joinpath(*parts)
                binary.parent.mkdir(parents=True)
                binary.write_bytes(b'\x89PNG\x00data')
                first = scope['set_aside_unarchivable'](folder, quarantine)
                second = scope['set_aside_unarchivable'](folder, quarantine)
                print(json.dumps({
                    'path': '/'.join(parts),
                    'setAside': [n['omitted'] for n in first],
                    'again': len(second),
                    'noted': [json.loads(p.read_text())['omitted'] for p in folder.rglob('*.omitted.json')],
                    'left': [p.name for p in folder.rglob('*') if p.is_file() and not p.name.endswith('.omitted.json')],
                    'quarantined': [p.name for p in quarantine.iterdir() if p.read_bytes() == b'\x89PNG\x00data'],
                }))
                """;
            var (exitCode, stdout, stderr) = await ReviewdAutoSlotTests.RunSandboxRuleAsync(harness, work, shape);

            exitCode.Should().Be(0, stderr);
            var result = JsonNode.Parse(stdout)!.AsObject();
            var path = result["path"]!.GetValue<string>();
            result["setAside"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Equal(path);
            result["noted"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal([path], "the note beside where the file was names its whole path");
            result["left"]!.AsArray().Should().BeEmpty();
            result["again"]!.GetValue<int>().Should().Be(0, "a retried commit finds nothing more to set aside");
            result["quarantined"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .ContainSingle("the file is moved out whole, not deleted")
                .Which.Should()
                .EndWith(".png", "the quarantined name keeps the end of the file's name");
        }
        finally
        {
            RealGitFixture.ForceDelete(work);
        }
    }

    [SkippableFact]
    public async Task The_commit_phase_sets_data_and_nested_repositories_aside_before_it_commits_the_folder()
    {
        // The real commit phase runs, with the kit's commit_review replaced by one that reports what it would commit.
        Skip.IfNot(OperatingSystem.IsLinux(), "the sandbox script scans /proc, so it runs only on Linux");
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox script cannot be run");
        var root = await ReviewdAutoSlotTests.NativeRootAsync();
        // The phase quarantines under /tmp/review-quarantine/<repo>-<pr>-<head8>; a PR number of this run's own keeps
        // concurrent runs apart.
        var pr = Random.Shared.Next(1_000_000, int.MaxValue);
        var quarantine = $"/tmp/review-quarantine/nova-{pr}-hhhhhhhh";
        try
        {
            var scripts = Path.Combine(root, ".claude/skills/review-setup/scripts");
            // The commit phase also checks the source checkout is at the prepared merge and looks up the review branch.
            await File.AppendAllTextAsync(
                Path.Combine(scripts, "review_common.py"),
                """


                def run_git_read(path, args, check=True):
                    return SimpleNamespace(stdout='m' * 40 + '\n', returncode=0)


                read_parent_remote_ref = None
                """
            );
            await File.WriteAllTextAsync(
                Path.Combine(scripts, "review_commit.py"),
                """
                from pathlib import Path


                def commit_review(root, repo, pr, slot, source_sha, expected_branch):
                    folder = Path(root) / f'slots/{slot}/PRs/{repo.lower()}-{pr}'
                    return sorted(p.relative_to(folder).as_posix() for p in folder.rglob('*') if p.is_file())
                """
            );
            await File.WriteAllTextAsync(
                Path.Combine(root, "state", "prepared-nova-2.json"),
                new JsonObject
                {
                    ["schema"] = 1,
                    ["repo"] = "Nova",
                    ["slot"] = 2,
                    ["pr"] = pr,
                    ["merge_sha"] = new string('m', 40),
                }.ToJsonString()
            );
            var folder = Directory.CreateDirectory(Path.Combine(root, "slots", "2", "PRs", $"nova-{pr}")).FullName;
            await File.WriteAllTextAsync(Path.Combine(folder, "findings.md"), "# findings\n");
            await File.WriteAllBytesAsync(Path.Combine(folder, "chart.png"), [0x89, 0x50, 0x4E, 0x47, 0x00]);
            var clone = Directory.CreateDirectory(Path.Combine(folder, "evidence", "clone", ".git")).Parent!.FullName;
            await File.WriteAllTextAsync(Path.Combine(clone, ".git", "HEAD"), "ref: refs/heads/main\n");
            await File.WriteAllTextAsync(Path.Combine(clone, "README.md"), "# a clone of the source\n");

            var (exitCode, stdout, stderr) = await ReviewdAutoSlotTests.NativePhaseAsync(
                python!,
                root,
                "commit",
                pr,
                "A"
            );

            exitCode.Should().Be(0, stderr);
            JsonNode
                .Parse(stdout)!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal(
                    ["chart.png.omitted.json", "findings.md"],
                    "the commit sees the review's text and the note, never the data or the nested repository"
                );
            File.Exists(Path.Combine(quarantine, "chart.png")).Should().BeTrue("the data is moved out, not deleted");
            Directory
                .Exists(Path.Combine(quarantine, "evidence__clone"))
                .Should()
                .BeTrue("a nested repository is moved out whole");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(quarantine))
                Directory.Delete(quarantine, recursive: true);
        }
    }
}
