using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     The commit step checks that the source checkout did not change while it captured and committed the review
///     (review_commit.py). Each slot's source is a worktree of one bare store that every lane shares. The check
///     recorded every ref in that store, so another lane's fetch failed a finished review's save (#999; MCQdbDEV PR
///     11550, LmDotnetTools #1008 and #1011).
/// </summary>
public sealed class ReviewdSourceFingerprintTests
{
    [SkippableFact]
    public async Task Another_lanes_fetch_into_the_shared_store_passes_but_a_change_to_this_checkout_fails()
    {
        var work = Directory.CreateTempSubdirectory("reviewd-source-fingerprint-").FullName;
        try
        {
            // Each case gets its own store with two detached source checkouts, A (this slot) and B (another lane),
            // as the pool makes them. A's fingerprint is taken, the case changes something, and the check runs on A.
            // Each rejected case changes only what one fingerprint field sees, so dropping that field fails it.
            const string harness = """
                import json, subprocess, sys
                from pathlib import Path
                sys.path.insert(0, sys.argv[1])
                import review_commit as commit
                from review_common import ReviewError
                work = Path(sys.argv[2])

                def git(cwd, *args):
                    return subprocess.run(['git', '-c', 'user.name=t', '-c', 'user.email=t@example.com',
                                           '-c', 'commit.gpgsign=false', '-C', str(cwd), *args],
                                          check=True, capture_output=True, text=True).stdout.strip()

                def has_ref(cwd, ref):
                    return subprocess.run(['git', '-C', str(cwd), 'rev-parse', '-q', '--verify', ref],
                                          capture_output=True).returncode == 0

                def pool(name):
                    seed, store = work / name / 'seed', work / name / 'store'
                    seed.mkdir(parents=True)
                    git(seed, 'init', '-q', '--initial-branch=main')
                    (seed / 'tracked.txt').write_text('one\n')
                    git(seed, 'add', 'tracked.txt')
                    git(seed, 'commit', '-q', '-m', 'one')
                    first = git(seed, 'rev-parse', 'HEAD')
                    (seed / 'tracked.txt').write_text('two\n')
                    git(seed, 'commit', '-q', '-am', 'two')
                    git(work / name, 'clone', '-q', '--bare', str(seed), str(store))
                    for ref in ('refs/review-setup/prepare/nova-1', 'refs/review-setup/prepare/nova-2',
                                'refs/remotes/origin/pr/9'):
                        git(store, 'update-ref', ref, first)
                    a, b = work / name / 'a', work / name / 'b'
                    git(store, 'worktree', 'add', '-q', '--detach', str(a), 'HEAD')
                    git(store, 'worktree', 'add', '-q', '--detach', str(b), 'HEAD')
                    return a, b, first

                def other_lane_fetches(a, b, first):
                    # What a fetch and a prepare in lane B do to the shared store: new commits, new and moved
                    # refs, an unrelated pruned ref. A's own prepare ref stays. B's detached HEAD moves, and B
                    # starts a bisect and writes a worktree ref, which are B's alone.
                    main_before = has_ref(b, 'refs/heads/main')
                    (b / 'tracked.txt').write_text('fetched\n')
                    git(b, 'commit', '-q', '-am', 'fetched')
                    fetched = git(b, 'rev-parse', 'HEAD')
                    git(b, 'update-ref', 'refs/remotes/origin/pr/2', fetched)
                    git(b, 'update-ref', 'refs/review-setup/prepare/nova-2', fetched)
                    git(b, 'update-ref', 'refs/heads/main', fetched)
                    git(b, 'update-ref', '-d', 'refs/remotes/origin/pr/9')
                    git(b, 'bisect', 'start', '--no-checkout', fetched, first)
                    git(b, 'update-ref', 'refs/worktree/lane-b', fetched)
                    return {'mainExistedBefore': main_before,
                            'mainMoved': git(a, 'rev-parse', 'refs/heads/main') == fetched,
                            'ownPrepareKept': git(a, 'rev-parse', 'refs/review-setup/prepare/nova-1') == first,
                            'laneBBisects': has_ref(b, 'refs/bisect/bad')}

                def same_tree_head(a, b, first):
                    # A new commit with HEAD's tree: no file, index, diff or status change.
                    same = git(a, 'commit-tree', 'HEAD^{tree}', '-p', 'HEAD', '-m', 'same tree')
                    git(a, 'checkout', '-q', '--detach', same)

                def replaced(a, b, first):
                    # Same tree, no parent: the SHA stays, but the diff it shows changes.
                    root = git(a, 'commit-tree', 'HEAD^{tree}', '-m', 'replacement')
                    git(a, 'replace', 'HEAD', root)
                    return {'replaceRef': has_ref(a, 'refs/replace/' + git(a, 'rev-parse', 'HEAD'))}

                def bisected(a, b, first):
                    git(a, 'bisect', 'start', '--no-checkout', 'HEAD', first)
                    return {'bisectRef': has_ref(a, 'refs/bisect/bad')}

                def untracked(a):
                    (a / 'notes.txt').write_text('notes\n')

                cases = {
                    'otherLaneFetches': (None, other_lane_fetches),
                    'headMovedToSameTree': (None, same_tree_head),
                    'switchedOntoBranch': (None, lambda a, b, first: git(a, 'switch', '-q', '-c', 'hook-branch')),
                    'assumeUnchanged': (None, lambda a, b, first: git(a, 'update-index', '--assume-unchanged',
                                                                      'tracked.txt')),
                    'trackedFileEdited': (None, lambda a, b, first: (a / 'tracked.txt').write_text('edited\n')),
                    'untrackedFileRewritten': (untracked, lambda a, b, first: (a / 'notes.txt').write_text('x\n')),
                    'replaceRefAdded': (None, replaced),
                    'bisectStarted': (None, bisected),
                }
                result = {}
                for name, (prepare, change) in cases.items():
                    a, b, first = pool(name)
                    if prepare:
                        prepare(a)
                    before = commit.source_fingerprint(a)
                    refs = git(a, 'for-each-ref')
                    facts = change(a, b, first)
                    facts = facts if isinstance(facts, dict) else {}
                    try:
                        commit.assert_source_unchanged(a, before)
                        outcome, error = 'unchanged', None
                    except ReviewError as caught:
                        outcome, error = 'changed', str(caught)
                    result[name] = {'outcome': outcome, 'error': error,
                                    'storeRefsMoved': git(a, 'for-each-ref') != refs, **facts}
                print(json.dumps(result))
                """;

            var (exitCode, stdout, stderr) = await ReviewdPreparedContextTests.RunKitAsync(harness, work);

            exitCode.Should().Be(0, stderr);
            var result = JsonNode.Parse(stdout)!.AsObject();
            var fetch = result["otherLaneFetches"]!;
            foreach (var fact in new[] { "storeRefsMoved", "mainExistedBefore", "mainMoved", "ownPrepareKept" })
                fetch[fact]!.GetValue<bool>().Should().BeTrue($"lane B's fetch is visible from A ({fact})");
            fetch["laneBBisects"]!.GetValue<bool>().Should().BeTrue("lane B has per-worktree refs of its own");
            fetch["outcome"]!
                .GetValue<string>()
                .Should()
                .Be("unchanged", "another lane's fetch into the shared store is not a change to this checkout");
            result["replaceRefAdded"]!["replaceRef"]!.GetValue<bool>().Should().BeTrue();
            result["bisectStarted"]!["bisectRef"]!.GetValue<bool>().Should().BeTrue();
            foreach (
                var name in new[]
                {
                    "headMovedToSameTree",
                    "switchedOntoBranch",
                    "assumeUnchanged",
                    "trackedFileEdited",
                    "untrackedFileRewritten",
                    "replaceRefAdded",
                    "bisectStarted",
                }
            )
            {
                var rejected = result[name]!;
                rejected["outcome"]!
                    .GetValue<string>()
                    .Should()
                    .Be("changed", $"a hook that leaves this checkout {name} must still fail the commit");
                // The retry classifier matches only this phrase.
                rejected["error"]!
                    .GetValue<string>()
                    .Should()
                    .Contain("Source changed during artifact capture or commit hook", name);
            }
        }
        finally
        {
            RealGitFixture.ForceDelete(work);
        }
    }
}
