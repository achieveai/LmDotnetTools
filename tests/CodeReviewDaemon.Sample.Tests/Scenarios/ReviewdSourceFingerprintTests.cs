using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using FluentAssertions.Execution;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     The commit step checks that the source checkout did not change while it captured and committed the review
///     (review_commit.py). Each slot's source is a worktree of one bare store that every lane shares. The check
///     recorded every ref in that store, so another lane's fetch failed a finished review's save (#999; MCQdbDEV PR
///     11550, LmDotnetTools #1008 and #1011).
/// </summary>
public sealed class ReviewdSourceFingerprintTests
{
    private const string GitHarness = """
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

        """;

    [SkippableFact]
    public async Task Another_lanes_fetch_into_the_shared_store_passes_but_a_change_to_this_checkout_fails()
    {
        var work = Directory.CreateTempSubdirectory("reviewd-source-fingerprint-").FullName;
        try
        {
            // Each case gets its own store with two detached source checkouts, A (this slot) and B (another lane),
            // as the pool makes them. A's fingerprint is taken, the case changes something, and the check runs on A.
            // Most rejected cases change only what one fingerprint field sees, so dropping that field fails them. The
            // branch switch and the tracked edit are each seen by several fields, so they check overlapping guards.
            const string harness = """
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

                def own_ref(namespace):
                    def write(a, b, first):
                        git(a, 'update-ref', namespace + '/x', 'HEAD')
                        return {'ownRef': has_ref(a, namespace + '/x') and not has_ref(b, namespace + '/x')}
                    return write

                def seeds_own_ref(namespace):
                    return lambda a: git(a, 'update-ref', namespace + '/x', 'HEAD^')

                def move_own_ref(namespace):
                    def move(a, b, first):
                        ref = namespace + '/x'
                        earlier = git(a, 'rev-parse', ref)
                        git(a, 'update-ref', ref, 'HEAD')
                        return {'sameNameNewTarget': git(a, 'rev-parse', ref) != earlier and not has_ref(b, ref)}
                    return move

                def symbolic_ref(a):
                    git(a, 'symbolic-ref', 'refs/worktree/shared-tip', 'refs/heads/main')
                    git(a, 'update-ref', 'refs/remotes/origin/main', 'HEAD')

                def shared_symbolic_target_moves(a, b, first):
                    ref = 'refs/worktree/shared-tip'
                    before = git(a, 'rev-parse', ref)
                    facts = other_lane_fetches(a, b, first)
                    return {**facts, 'sameNameNewTarget': git(a, 'rev-parse', ref) != before,
                            'sameSymbolicTarget': git(a, 'symbolic-ref', ref) == 'refs/heads/main'}

                def symbolic_ref_retargeted(a, b, first):
                    ref = 'refs/worktree/shared-tip'
                    before = git(a, 'rev-parse', ref)
                    git(a, 'symbolic-ref', ref, 'refs/remotes/origin/main')
                    return {'sameResolvedTarget': git(a, 'rev-parse', ref) == before,
                            'newSymbolicTarget': git(a, 'symbolic-ref', ref) == 'refs/remotes/origin/main'}

                def chained_symbolic_ref(a):
                    symbolic_ref(a)
                    for alias in ('HEAD', 'alias'):
                        git(a, 'symbolic-ref', 'refs/remotes/origin/' + alias, 'refs/heads/main')
                    git(a, 'symbolic-ref', 'refs/worktree/shared-tip', 'refs/remotes/origin/HEAD')

                def alias_targets(a):
                    ref = 'refs/worktree/shared-tip'
                    return (git(a, 'symbolic-ref', '--no-recurse', ref), git(a, 'symbolic-ref', ref),
                            git(a, 'rev-parse', ref))

                def shared_alias_retargeted(a, b, first):
                    local, leaf, oid = alias_targets(a)
                    git(b, 'symbolic-ref', 'refs/remotes/origin/HEAD', 'refs/remotes/origin/main')
                    next_local, next_leaf, next_oid = alias_targets(a)
                    return {'sameLocalTarget': local == next_local, 'differentLeaf': leaf != next_leaf,
                            'sameResolvedTarget': oid == next_oid}

                def local_alias_retargeted(a, b, first):
                    local, leaf, oid = alias_targets(a)
                    git(a, 'symbolic-ref', 'refs/worktree/shared-tip', 'refs/remotes/origin/alias')
                    next_local, next_leaf, next_oid = alias_targets(a)
                    return {'differentLocalTarget': local != next_local, 'sameLeaf': leaf == next_leaf,
                            'sameResolvedTarget': oid == next_oid}

                def replacement(a, message):
                    return git(a, 'commit-tree', 'HEAD^{tree}', '-m', message)

                def replace_installed(a):
                    git(a, 'replace', 'HEAD', replacement(a, 'first replacement'))

                def replace_retargeted(a, b, first):
                    # The same refs/replace/<HEAD> name, now naming a different same-tree root commit.
                    ref = 'refs/replace/' + git(a, 'rev-parse', 'HEAD')
                    before = git(a, 'rev-parse', ref)
                    git(a, 'replace', '-f', 'HEAD', replacement(a, 'second replacement'))
                    return {'sameNameNewTarget': git(a, 'rev-parse', ref) != before}

                def untracked(a):
                    (a / 'notes.txt').write_text('notes\n')

                cases = {
                    'otherLaneFetches': (None, other_lane_fetches),
                    'sharedSymbolicTargetMoves': (symbolic_ref, shared_symbolic_target_moves),
                    'ownSymbolicRefRetargeted': (symbolic_ref, symbolic_ref_retargeted),
                    'sharedAliasRetargeted': (chained_symbolic_ref, shared_alias_retargeted),
                    'localAliasRetargeted': (chained_symbolic_ref, local_alias_retargeted),
                    'headMovedToSameTree': (None, same_tree_head),
                    'switchedOntoBranch': (None, lambda a, b, first: git(a, 'switch', '-q', '-c', 'hook-branch')),
                    'assumeUnchanged': (None, lambda a, b, first: git(a, 'update-index', '--assume-unchanged',
                                                                      'tracked.txt')),
                    'trackedFileEdited': (None, lambda a, b, first: (a / 'tracked.txt').write_text('edited\n')),
                    'untrackedFileRewritten': (untracked, lambda a, b, first: (a / 'notes.txt').write_text('x\n')),
                    'replaceRefAdded': (None, replaced),
                    'replaceRefRetargeted': (replace_installed, replace_retargeted),
                    'bisectStarted': (None, bisected),
                    'worktreeRefAdded': (None, own_ref('refs/worktree')),
                    'rewrittenRefAdded': (None, own_ref('refs/rewritten')),
                    'worktreeRefMoved': (seeds_own_ref('refs/worktree'), move_own_ref('refs/worktree')),
                    'rewrittenRefMoved': (seeds_own_ref('refs/rewritten'), move_own_ref('refs/rewritten')),
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

            var (exitCode, stdout, stderr) = await ReviewdPreparedContextTests.RunKitAsync(GitHarness + harness, work);

            exitCode.Should().Be(0, stderr);
            var result = JsonNode.Parse(stdout)!.AsObject();
            using var assertions = new AssertionScope();
            var fetch = result["otherLaneFetches"]!;
            foreach (var fact in new[] { "storeRefsMoved", "mainExistedBefore", "mainMoved", "ownPrepareKept" })
                fetch[fact]!.GetValue<bool>().Should().BeTrue($"lane B's fetch is visible from A ({fact})");
            fetch["laneBBisects"]!.GetValue<bool>().Should().BeTrue("lane B has per-worktree refs of its own");
            fetch["outcome"]!
                .GetValue<string>()
                .Should()
                .Be("unchanged", "another lane's fetch into the shared store is not a change to this checkout");
            var sharedTarget = result["sharedSymbolicTargetMoves"]!;
            sharedTarget["mainMoved"]!.GetValue<bool>().Should().BeTrue();
            sharedTarget["sameNameNewTarget"]!.GetValue<bool>().Should().BeTrue();
            sharedTarget["sameSymbolicTarget"]!.GetValue<bool>().Should().BeTrue();
            sharedTarget["outcome"]!.GetValue<string>().Should().Be("unchanged");
            var ownSymbolicRef = result["ownSymbolicRefRetargeted"]!;
            ownSymbolicRef["sameResolvedTarget"]!.GetValue<bool>().Should().BeTrue();
            ownSymbolicRef["newSymbolicTarget"]!.GetValue<bool>().Should().BeTrue();
            var sharedAlias = result["sharedAliasRetargeted"]!;
            sharedAlias["sameLocalTarget"]!.GetValue<bool>().Should().BeTrue();
            sharedAlias["differentLeaf"]!.GetValue<bool>().Should().BeTrue();
            sharedAlias["sameResolvedTarget"]!.GetValue<bool>().Should().BeTrue();
            sharedAlias["outcome"]!.GetValue<string>().Should().Be("unchanged", "only the shared alias changed");
            var localAlias = result["localAliasRetargeted"]!;
            localAlias["differentLocalTarget"]!.GetValue<bool>().Should().BeTrue();
            localAlias["sameLeaf"]!.GetValue<bool>().Should().BeTrue();
            localAlias["sameResolvedTarget"]!.GetValue<bool>().Should().BeTrue();
            result["replaceRefAdded"]!["replaceRef"]!.GetValue<bool>().Should().BeTrue();
            result["bisectStarted"]!["bisectRef"]!.GetValue<bool>().Should().BeTrue();
            result["replaceRefRetargeted"]!["sameNameNewTarget"]!
                .GetValue<bool>()
                .Should()
                .BeTrue("only the replace ref's target changes, not its name");
            foreach (var name in new[] { "worktreeRefAdded", "rewrittenRefAdded" })
                result[name]!["ownRef"]!.GetValue<bool>().Should().BeTrue($"{name} is A's ref alone");
            foreach (var name in new[] { "worktreeRefMoved", "rewrittenRefMoved" })
            {
                result[name]!["sameNameNewTarget"]!
                    .GetValue<bool>()
                    .Should()
                    .BeTrue($"{name} changes an existing local ref");
            }
            foreach (
                var name in new[]
                {
                    "headMovedToSameTree",
                    "switchedOntoBranch",
                    "assumeUnchanged",
                    "trackedFileEdited",
                    "untrackedFileRewritten",
                    "replaceRefAdded",
                    "replaceRefRetargeted",
                    "bisectStarted",
                    "worktreeRefAdded",
                    "rewrittenRefAdded",
                    "worktreeRefMoved",
                    "rewrittenRefMoved",
                    "ownSymbolicRefRetargeted",
                    "localAliasRetargeted",
                }
            )
            {
                var rejected = result[name]!;
                rejected["outcome"]!
                    .GetValue<string>()
                    .Should()
                    .Be("changed", $"a hook that leaves this checkout {name} must still fail the commit");
                // The retry classifier matches only this phrase.
                var error = rejected["error"]?.GetValue<string>();
                error.Should().Contain("Source changed during artifact capture or commit hook", name);
            }
        }
        finally
        {
            RealGitFixture.ForceDelete(work);
        }
    }

    [SkippableTheory]
    [InlineData("clean")]
    [InlineData("bisect")]
    [InlineData("replace")]
    [InlineData("rewritten")]
    public async Task Commit_refuses_stale_source_operation_refs_before_capturing_artifacts(string kind)
    {
        var work = Directory.CreateTempSubdirectory("reviewd-source-retry-").FullName;
        try
        {
            const string harness = """
                from unittest.mock import patch

                kind = sys.argv[3]
                seed = work / 'seed'
                seed.mkdir()
                git(seed, 'init', '-q', '--initial-branch=main')
                git(seed, 'commit', '-qm', 'Prepared source', '--allow-empty')
                sha = git(seed, 'rev-parse', 'HEAD')

                root = work / 'review-store'
                root.mkdir()
                git(root, 'init', '-q', '--initial-branch=main')
                (root / '.gitmodules').write_text('[submodule "nova"]\n\tpath = repos/nova\n'
                                                   '\turl = https://github.com/example/nova.git\n')
                git(root, 'add', '.gitmodules')
                git(root, 'update-index', '--add', '--cacheinfo', f'160000,{sha},repos/nova')
                git(root, 'commit', '-qm', 'Configure source pool')
                state = commit.ReviewState(root, read_only=True)
                pool = commit.SourcePool(state, commit.select_module_by_name(root, 'nova'))
                outer = root / pool.slot_relative_path(0)
                git(root, 'worktree', 'add', '-q', '--detach', str(outer), 'main')

                pool.store.parent.mkdir(parents=True)
                git(seed, 'clone', '-q', '--bare', str(seed), str(pool.store))
                source = outer / pool.module['path']
                git(pool.store, 'worktree', 'add', '-q', '--detach', str(source), 'main')
                manifest = state.prepared_slot_path(pool.repo_key, 0)
                manifest.parent.mkdir()
                manifest.write_text(json.dumps({'schema': 1, 'repo': 'nova', 'slot': 0, 'pr': 9,
                                                'merge_sha': sha, 'source_sha': sha}))
                (source / 'review-note.txt').write_text('Keep my source evidence\n')
                git(source, 'update-ref', 'refs/worktree/prepare-marker', sha)

                ref = {'bisect': 'refs/bisect/bad', 'replace': f'refs/replace/{sha}',
                       'rewritten': 'refs/rewritten/onto'}.get(kind)
                if ref:
                    target = git(source, 'commit-tree', 'HEAD^{tree}', '-m', 'Earlier hook evidence')
                    git(source, 'update-ref', ref, target)
                else:
                    other = work / 'another-lane'
                    git(pool.store, 'worktree', 'add', '-q', '--detach', str(other), 'main')
                    git(other, 'update-ref', 'refs/bisect/bad', sha)
                    git(other, 'update-ref', 'refs/rewritten/onto', sha)
                with patch.object(commit, 'source_fingerprint', wraps=commit.source_fingerprint) as fingerprint:
                    try:
                        plan = commit.commit_review(root, 'nova', 9, 0, source_sha=sha, dry_run=True, push=False)
                        error = None
                    except ReviewError as refusal:
                        plan, error = None, str(refusal)
                print(json.dumps({'error': error, 'plan': plan, 'ref': ref, 'fingerprints': fingerprint.call_count,
                                  'sourceKept': git(source, 'rev-parse', 'HEAD') == sha and
                                      (source / 'review-note.txt').read_text() == 'Keep my source evidence\n' and
                                      (not ref or git(source, 'rev-parse', ref) == target),
                                  'captureWritten': (outer / 'PRs').exists()}))
                """;
            var (exitCode, stdout, stderr) = await ReviewdPreparedContextTests.RunKitAsync(
                GitHarness + harness,
                work,
                kind
            );

            exitCode.Should().Be(0, stderr);
            var result = JsonNode.Parse(stdout)!.AsObject();
            result["sourceKept"]!.GetValue<bool>().Should().BeTrue();
            result["captureWritten"]!.GetValue<bool>().Should().BeFalse();
            if (kind == "clean")
            {
                result["error"].Should().BeNull();
                result["plan"]!["copiedFiles"]!
                    .AsArray()
                    .Select(path => path!.GetValue<string>())
                    .Should()
                    .Contain("PRs/nova-9/outside/source/review-note.txt");
                result["fingerprints"]!.GetValue<int>().Should().BePositive();
            }
            else
            {
                result["error"].Should().NotBeNull("an existing {0} ref must prevent taking the retry baseline", kind);
                result["error"]!
                    .GetValue<string>()
                    .Should()
                    .Contain("An existing Git operation or index lock must be resolved first.")
                    .And.Contain(result["ref"]!.GetValue<string>());
                result["fingerprints"]!.GetValue<int>().Should().Be(0);
                result["plan"].Should().BeNull();
            }
        }
        finally
        {
            RealGitFixture.ForceDelete(work);
        }
    }
}
