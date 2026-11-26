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

                def pool(name):
                    seed, store = work / name / 'seed', work / name / 'store'
                    seed.mkdir(parents=True)
                    git(seed, 'init', '-q')
                    (seed / 'tracked.txt').write_text('one\n')
                    git(seed, 'add', 'tracked.txt')
                    git(seed, 'commit', '-q', '-m', 'one')
                    first = git(seed, 'rev-parse', 'HEAD')
                    (seed / 'tracked.txt').write_text('two\n')
                    git(seed, 'commit', '-q', '-am', 'two')
                    git(work / name, 'clone', '-q', '--bare', str(seed), str(store))
                    git(store, 'update-ref', 'refs/review-setup/prepare/nova-1', first)
                    a, b = work / name / 'a', work / name / 'b'
                    git(store, 'worktree', 'add', '-q', '--detach', str(a), 'HEAD')
                    git(store, 'worktree', 'add', '-q', '--detach', str(b), 'HEAD')
                    return a, b, first

                def other_lane_fetches(a, b, first):
                    # What a fetch and a prepare in lane B do to the shared store: new commits, new and moved
                    # refs, a pruned ref. B's own detached HEAD moves too.
                    (b / 'tracked.txt').write_text('fetched\n')
                    git(b, 'commit', '-q', '-am', 'fetched')
                    fetched = git(b, 'rev-parse', 'HEAD')
                    git(b, 'update-ref', 'refs/remotes/origin/pr/2', fetched)
                    git(b, 'update-ref', 'refs/review-setup/prepare/nova-2', fetched)
                    git(b, 'update-ref', 'refs/heads/main', fetched)
                    git(b, 'update-ref', '-d', 'refs/review-setup/prepare/nova-1')

                cases = {
                    'otherLaneFetches': other_lane_fetches,
                    'headMoved': lambda a, b, first: git(a, 'checkout', '-q', '--detach', first),
                    'switchedOntoBranch': lambda a, b, first: git(a, 'switch', '-q', '-c', 'hook-branch'),
                    'indexChanged': lambda a, b, first: git(a, 'update-index', '--chmod=+x', 'tracked.txt'),
                    'trackedFileEdited': lambda a, b, first: (a / 'tracked.txt').write_text('edited\n'),
                }
                result = {}
                for name, change in cases.items():
                    a, b, first = pool(name)
                    before = commit.source_fingerprint(a)
                    refs = git(a, 'for-each-ref')
                    change(a, b, first)
                    try:
                        commit.assert_source_unchanged(a, before)
                        outcome = 'unchanged'
                    except ReviewError as error:
                        outcome = 'changed: ' + str(error)
                    result[name] = {'outcome': outcome, 'storeRefsMoved': git(a, 'for-each-ref') != refs}
                print(json.dumps(result))
                """;

            var (exitCode, stdout, stderr) = await ReviewdPreparedContextTests.RunKitAsync(harness, work);

            exitCode.Should().Be(0, stderr);
            var result = JsonNode.Parse(stdout)!.AsObject();
            var fetch = result["otherLaneFetches"]!;
            fetch["storeRefsMoved"]!
                .GetValue<bool>()
                .Should()
                .BeTrue("checkout A sees the shared store's refs, so lane B's fetch is visible from A");
            fetch["outcome"]!
                .GetValue<string>()
                .Should()
                .Be("unchanged", "another lane's fetch into the shared store is not a change to this checkout");
            foreach (var name in new[] { "headMoved", "switchedOntoBranch", "indexChanged", "trackedFileEdited" })
            {
                result[name]!["outcome"]!
                    .GetValue<string>()
                    .Should()
                    .Be(
                        "changed: Source changed during artifact capture or commit hook; side effects preserved for inspection.",
                        $"a hook that leaves this checkout {name} must still fail the commit"
                    );
            }
        }
        finally
        {
            RealGitFixture.ForceDelete(work);
        }
    }
}
