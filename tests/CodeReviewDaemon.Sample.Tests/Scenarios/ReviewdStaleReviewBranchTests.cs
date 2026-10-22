extern alias Reviewd;

using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     The retired CodeReviewDaemon left <c>review/{repo}-{pr}</c> branches on the store remote, on older store history.
///     A Reviewd commit cannot fast-forward such a branch, so the commit step's non-force push was refused (MCQdbDEV
///     #11267). The sandbox's own branch rule runs here against real Git repositories.
/// </summary>
public sealed class ReviewdStaleReviewBranchTests
{
    [SkippableFact]
    public async Task The_review_record_publishes_without_force_beside_an_old_daemon_review_branch()
    {
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the sandbox's branch rule cannot be run");
        var work = Path.Combine(Path.GetTempPath(), $"reviewd-stale-branch-{Guid.NewGuid():N}");
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
            // origin.git stands in for the store remote and slot for a slot detached at the store's main. The old
            // daemon's record is a root commit main never had: PR 1's is not in the store, PR 2's was fetched into
            // it. PR 3's remote branch is main itself (a fast-forward) and PR 4 has none. The kit's
            // read_parent_remote_ref is stubbed to its contract (the remote tip, or None) because the kit needs POSIX
            // locks. The record is then pushed the way the commit step pushes it: without force.
            const string harness = """
                import ast, json, subprocess, sys, types
                from pathlib import Path
                script, work = sys.argv[1], Path(sys.argv[2])
                identity = ('-c', 'user.name=t', '-c', 'user.email=t@example.invalid')
                def git(cwd, *args, check=True, stdin=''):
                    result = subprocess.run(['git', '-C', str(cwd), *args], input=stdin, capture_output=True, text=True)
                    if check and result.returncode:
                        raise SystemExit(f'git {args} failed: {result.stderr}')
                    return result
                def read_parent_remote_ref(root, ref):
                    listing = git(root, 'ls-remote', '--exit-code', 'origin', ref, check=False)
                    return None if listing.returncode == 2 else listing.stdout.split('\t')[0]
                common = types.ModuleType('review_common')
                common.read_parent_remote_ref = read_parent_remote_ref
                sys.modules['review_common'] = common
                origin, slot = work / 'origin.git', work / 'slot'
                git(work, 'init', '-q', '--bare', '-b', 'main', str(origin))
                tree = git(origin, 'mktree').stdout.strip()
                main = git(origin, *identity, 'commit-tree', tree, '-m', 'store main').stdout.strip()
                old = git(origin, *identity, 'commit-tree', tree, '-m', 'old daemon record').stdout.strip()
                refs = {'main': main, 'review/x-1': old, 'review/x-2': old, 'review/x-3': main}
                git(origin, 'update-ref', '--stdin', '-z',
                    stdin=''.join(f'update refs/heads/{ref}\0{sha}\0\0' for ref, sha in refs.items()))
                git(work, 'clone', '-q', '--single-branch', '--branch', 'main', '--no-checkout', str(origin), str(slot))
                git(slot, 'fetch', '-q', 'origin', 'refs/heads/review/x-2:refs/remotes/origin/review/x-2')
                git(slot, 'switch', '-q', '--detach', 'main')
                rule = [n for n in ast.parse(Path(script).read_text(encoding='utf-8')).body
                        if isinstance(n, ast.FunctionDef) and n.name == 'expected_review_branch']
                chosen = {}
                for pr in (1, 2, 3, 4):
                    scope = {'repo': 'x', 'pr': pr, 'outer': slot, 'args': {'head': 'abcdef12' + '0' * 32},
                             'repo_slug': str.lower,
                             'run_git_read': lambda repo, args, check=True: git(repo, *args, check=check)}
                    exec(compile(ast.Module(rule, []), 'sandbox', 'exec'), scope)
                    chosen[pr] = scope['expected_review_branch']()
                record = git(slot, *identity, 'commit-tree', tree, '-p', main, '-m', 'review record').stdout.strip()
                targets = {pr: chosen[pr] or f'review/x-{pr}' for pr in chosen}
                probes = {pr: f'review/x-{pr}' for pr in chosen if chosen[pr]}
                specs = [f'{record}:refs/heads/{branch}' for branch in [*targets.values(), *probes.values()]]
                pushed = git(slot, 'push', '--porcelain', 'origin', *specs, check=False).stdout
                flags = {line.split('\t')[1].split(':refs/heads/')[1]: line[0]
                         for line in pushed.splitlines() if '\t' in line}
                remote = dict(reversed(line.split('\t')) for line in git(origin, 'show-ref').stdout.replace(' ', '\t').splitlines())
                print(json.dumps({pr: {'expected': chosen[pr], 'published': flags[targets[pr]] in ('*', ' '),
                                       'canonicalRefused': pr in probes and flags[probes[pr]] == '!',
                                       'canonicalKept': remote[f'refs/heads/review/x-{pr}'] == old if pr in probes else None}
                                  for pr in chosen}))
                """;
            var start = new ProcessStartInfo(python!) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-c", harness, script, work })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(0, await stderr);
            var results = JsonNode.Parse(await stdout)!.AsObject();

            results
                .Select(pair => $"{pair.Key}:{pair.Value!["expected"]?.GetValue<string>() ?? "-"}")
                .Should()
                .Equal("1:review/x-1-abcdef12", "2:review/x-2-abcdef12", "3:-", "4:-");
            results
                .Should()
                .AllSatisfy(pair =>
                    pair.Value!["published"]!.GetValue<bool>().Should().BeTrue("the record must publish without force")
                );
            // The old daemon's record refuses a non-force push of the canonical branch, and stays exactly as it was.
            foreach (var pr in new[] { "1", "2" })
            {
                results[pr]!["canonicalRefused"]!.GetValue<bool>().Should().BeTrue();
                results[pr]!["canonicalKept"]!.GetValue<bool>().Should().BeTrue();
            }
        }
        finally
        {
            RealGitFixture.ForceDelete(work);
        }
    }
}
