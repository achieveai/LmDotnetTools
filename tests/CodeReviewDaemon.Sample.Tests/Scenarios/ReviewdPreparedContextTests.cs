using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     The prepared context stays small enough for a model to read whole and for the review archive to keep (Kay9).
///     Blame was one record per changed base line and once made a 347 MB context that blocked its archive push
///     (viva-skills #5797647). The kit now sums blame up per commit in a history part of its own, and the runner
///     writes the context and that history as files of at most 64 KiB. Only the review's history specialist reads the
///     history, and the archive does not keep it.
/// </summary>
public sealed class ReviewdPreparedContextTests
{
    // The runner's rules for writing the prepared report, taken from the sandbox script as it ships.
    private const string RunnerRules = """
        import ast, json, re, sys
        from pathlib import Path
        names = {'render_json', 'split_part', 'context_files', 'write_prepared_context', 'CONTEXT_PIECE_LIMIT',
                 'CONTEXT_TEXT_PART', 'ARCHIVE_FILE_LIMIT'}
        body = [n for n in ast.parse(Path(sys.argv[1]).read_text(encoding='utf-8')).body
                if (isinstance(n, ast.FunctionDef) and n.name in names)
                or (isinstance(n, ast.Assign) and any(getattr(t, 'id', None) in names for t in n.targets))]
        class ReviewError(Exception): pass
        scope = {'json': json, 're': re, 'ReviewError': ReviewError}
        exec(compile(ast.Module(body, []), 'sandbox', 'exec'), scope)

        """;

    [SkippableFact]
    public async Task A_large_report_is_written_in_pieces_a_model_can_read_whole_and_nothing_is_lost()
    {
        // Many changed files, a whole work-item family, a long non-English PR description (each character escapes to
        // six bytes), one file whose text alone is too big for a piece, and one whose list of hunks is. Read back
        // through the pieces, as their notes say, the report is the one prepared.
        const string harness =
            RunnerRules
            + """
                files = [{'path': f'src/f{i:05}.cs', 'status': 'M', 'base_ranges': [[i, i + 3]]} for i in range(3000)]
                files.append({'path': 'big.cs', 'status': 'M', 'patch': 'x' * 100000})
                files.append({'path': 'gen.cs', 'hunks': ['h' * 500] * 300})
                report = {'snapshot': {'pr_number': 7, 'title': 'T', 'description': '\u8bf4\u660e' * 20000},
                          'files': files, 'complete': True,
                          'work_items': {'linked_ids': [1], 'nodes': [{'id': i, 'title': 't' * 60} for i in range(2000)]}}
                written = scope['context_files'](report, 'setup-context')
                def whole(value):
                    if isinstance(value, dict) and 'split_into' in value:
                        bodies = [next(v for k, v in json.loads(written[name]).items() if k not in ('part_of', 'piece'))
                                  for name in value['split_into']]
                        if value['note'].startswith('Text'):
                            return ''.join(part for b in bodies for part in b)
                        value = ([item for b in bodies for item in b] if isinstance(bodies[0], list)
                                 else {k: v for b in bodies for k, v in b.items()})
                    if isinstance(value, dict):
                        return {k: whole(v) for k, v in value.items()}
                    return [whole(v) for v in value] if isinstance(value, list) else value
                main = json.loads(written['setup-context.json'])
                rebuilt = whole(main)
                small = {'snapshot': {'pr_number': 7}, 'files': files[:3], 'complete': True}
                alone = scope['context_files'](small, 'setup-context')
                print(json.dumps({
                    'files': len(written),
                    'largest': max(len(text.encode()) for text in written.values()),
                    'parts': sorted({json.loads(text)['part_of'] for name, text in written.items()
                                     if name != 'setup-context.json'}),
                    'split': sorted(k for k, v in main.items() if isinstance(v, dict) and 'split_into' in v),
                    'texts': sorted({name.rsplit('-', 1)[0] for name in written if 'patch' in name or 'description' in name}),
                    'same': rebuilt == report,
                    'alone': sorted(alone),
                    'aloneSame': json.loads(alone['setup-context.json']) == small,
                }))
                """;

        var (exitCode, stdout, stderr) = await ReviewdAutoSlotTests.RunSandboxRuleAsync(harness);

        exitCode.Should().Be(0, stderr);
        var result = JsonNode.Parse(stdout)!.AsObject();
        result["files"]!.GetValue<int>().Should().BeGreaterThan(5, "about 700 KB of report cannot fit in one file");
        result["largest"]!.GetValue<int>().Should().BeLessThanOrEqualTo(64 * 1024, "every file is read whole");
        result["parts"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .Should()
            .Equal(["setup-context.json"], "each piece names the file it belongs to");
        result["split"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .Should()
            .Equal("files", "snapshot", "work_items");
        result["texts"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .Should()
            .Equal(
                ["setup-context.files.3000.patch", "setup-context.snapshot.description"],
                "a text too big for one piece is cut into parts of its own, not dropped"
            );
        result["same"]!.GetValue<bool>().Should().BeTrue("read back through its pieces, nothing is lost");
        result["alone"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .Should()
            .Equal(["setup-context.json"], "a small report stays one file");
        result["aloneSame"]!.GetValue<bool>().Should().BeTrue();
    }

    [SkippableFact]
    public async Task The_prepared_history_is_written_apart_and_the_context_only_points_to_it()
    {
        const string harness =
            RunnerRules
            + """
                work = Path(sys.argv[2])
                sha = 'c' * 40
                history = {'files': [{'path': f'src/f{i:04}.cs', 'lines': 9,
                                      'by_commit': [{'commit': sha, 'lines': 9, 'base_lines': '1-9'}]} for i in range(1500)],
                           'commits': {sha: {'author': 'Ann', 'summary': 'Add the files', 'pr_ids': [5]}},
                           'pull_requests': {'5': {'id': 5, 'title': 'Add the files'}}, 'complete': True}
                report = {'snapshot': {'pr_number': 7, 'head_sha': 'h' * 40}, 'files': [{'path': 'src/f0000.cs'}],
                          'history': history, 'complete': True}
                result = {}
                for case, prepared in (('with', report), ('without', {k: v for k, v in report.items() if k != 'history'})):
                    folder = work / case
                    folder.mkdir()
                    context = scope['write_prepared_context'](folder, prepared)
                    main = json.loads(context.read_text())
                    result[case] = sorted(p.name for p in folder.iterdir())
                    result[case + 'Largest'] = max(p.stat().st_size for p in folder.iterdir())
                    result[case + 'Pointer'] = main.get('history')
                    result[case + 'Rest'] = {k: v for k, v in main.items() if k != 'history'} == {
                        k: v for k, v in report.items() if k != 'history'}
                result['shaInContext'] = sha in (work / 'with' / 'setup-context.json').read_text()
                result['historyMain'] = json.loads((work / 'with' / 'setup-history.json').read_text())
                print(json.dumps(result))
                """;
        var work = Directory.CreateTempSubdirectory("reviewd-prepared-").FullName;
        try
        {
            var (exitCode, stdout, stderr) = await ReviewdAutoSlotTests.RunSandboxRuleAsync(harness, work);

            exitCode.Should().Be(0, stderr);
            var result = JsonNode.Parse(stdout)!.AsObject();
            result["with"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal(
                    "setup-context.json",
                    "setup-history.files-1.json",
                    "setup-history.files-2.json",
                    "setup-history.files-3.json",
                    "setup-history.files-4.json",
                    "setup-history.json"
                );
            result["withLargest"]!.GetValue<int>().Should().BeLessThanOrEqualTo(64 * 1024);
            result["withPointer"]!["file"]!.GetValue<string>().Should().Be("setup-history.json");
            result["withRest"]!.GetValue<bool>().Should().BeTrue("the rest of the context is written as prepared");
            result["shaInContext"]!
                .GetValue<bool>()
                .Should()
                .BeFalse("the context the whole review reads holds none of the history");
            var history = result["historyMain"]!;
            history["commits"]![new string('c', 40)]!["pr_ids"]!.AsArray().Should().ContainSingle();
            history["files"]!["count"]!.GetValue<int>().Should().Be(1500);
            history["files"]!["split_into"]!.AsArray().Should().HaveCount(4);
            result["without"]!
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .Equal(["setup-context.json"], "an old kit's report, with no history part, is one file");
            result["withoutPointer"].Should().BeNull();
            result["withoutRest"]!.GetValue<bool>().Should().BeTrue();
        }
        finally
        {
            RealGitFixture.ForceDelete(work);
        }
    }

    [SkippableFact]
    public async Task The_kit_sums_up_line_history_per_commit_in_a_part_of_its_own()
    {
        // A real repository: the PR changes base lines 3-6 and 11 of one file. Lines 3, 4 and 11 last changed in the
        // first commit, 5 and 6 in the second. Both providers build their report from it; their APIs are stubbed.
        const string harness = """
            import json, os, subprocess, sys, types
            from pathlib import Path
            sys.path.insert(0, sys.argv[1])
            import review_ado as rd, review_github as rg
            repo = Path(sys.argv[2])
            def git(*args, when='2026-01-02T03:04:05Z', who='Ann'):
                # Nothing from a calling Git (a hook's GIT_DIR) reaches this repository.
                env = {k: v for k, v in os.environ.items() if not k.startswith('GIT_')}
                env.update(GIT_AUTHOR_NAME=who, GIT_AUTHOR_EMAIL='a@x', GIT_COMMITTER_NAME=who, GIT_COMMITTER_EMAIL='a@x',
                           GIT_AUTHOR_DATE=when, GIT_COMMITTER_DATE=when, GIT_CONFIG_GLOBAL=os.devnull,
                           GIT_CONFIG_NOSYSTEM='1')
                return subprocess.run(['git', '-C', str(repo), *args], env=env, check=True, capture_output=True,
                                      text=True).stdout.strip()
            def commit(lines, message, **who):
                (repo / 'a.txt').write_text(''.join(f'{line}\n' for line in lines))
                git('add', 'a.txt', **who)
                git('commit', '-q', '-m', message, **who)
                return git('rev-parse', 'HEAD')
            git('init', '-q', '-b', 'main')
            lines = [f'L{n}' for n in range(1, 13)]
            first = commit(lines, 'Add a')
            lines[4:6] = ['M5', 'M6']
            second = commit(lines, 'Tune the middle', when='2026-02-03T04:05:06Z', who='Bo')
            git('checkout', '-q', '-b', 'pr')
            lines[2:6], lines[10] = ['X3', 'X4', 'X5', 'X6'], 'X11'
            head = commit(lines, 'The PR', who='Cy')
            git('checkout', '-q', 'main')
            git('merge', '-q', '--no-ff', '-m', 'Merge', 'pr')
            merge = git('rev-parse', 'HEAD')
            class Ado:
                def request(self, method, path, body=None):
                    if '/workitems?' in path:
                        return {'value': []}
                    if '/pullrequestquery?' in path:
                        return {'results': [{first: [{'pullRequestId': 5}]}, {}]}
                    assert '/pullRequests/5?' in path, path
                    return {'pullRequestId': 5, 'title': 'Add a', 'description': 'd' * 400, 'status': 'completed',
                            'createdBy': {'displayName': 'Ann Lee', 'id': 'guid', 'imageUrl': 'https://x/avatar'}}
            snapshot = {'pr_number': 7, 'head_sha': head, 'repository': {'id': 'r', 'project': {'id': 'p'}}}
            report = rd._build_context_report(Ado(), snapshot, repo, second, context_lines=0)
            markdown = rd.render_context_markdown(report)
            rg.fetch_pr = lambda client, number: {}
            rg.live_state = lambda pr: {'status': 'active', 'mergeSha': merge, 'headSha': head}
            rg._snapshot = lambda pr, owner, repository: {'pr_number': 7, 'head_sha': head}
            rg._origin_prs = lambda client, commits: {
                'commits': {c: {'pr_ids': [5] if c == first else [], 'status': 'associated'} for c in commits},
                'pull_requests': {'5': {'id': 5, 'title': 'Add a', 'description': 'body', 'author': 'ann'}},
                'warnings': [], 'complete': True}
            rg._linked_issues = lambda client, snapshot: {'issues': [], 'warnings': [], 'complete': True}
            rg._discussion = lambda client, number: []
            github = object.__new__(rg.GitHubProvider)
            github.client, github.owner, github.repository = None, 'o', 'r'
            github = github.collect_context(types.SimpleNamespace(pr=7), repo)
            rd._BLAME_COMMITS_PER_FILE = 1
            capped = rd._collect_file_changes(repo, second, head, current_pr=7, context_lines=0)
            rd._MAX_BLAME_LINES = 4
            skipped = rd._collect_file_changes(repo, second, head, current_pr=7, context_lines=0)
            print(json.dumps({'first': first, 'second': second, 'files': report['files'], 'history': report['history'],
                              'markdown': markdown, 'github': {k: github[k] for k in ('files', 'history')},
                              'capped': capped['blame'],
                              'skipped': {key: skipped[key] for key in ('files', 'blame', 'warnings', 'complete')}}))
            """;
        var repo = Directory.CreateTempSubdirectory("reviewd-kit-history-").FullName;
        try
        {
            var (exitCode, stdout, stderr) = await RunKitAsync(harness, repo);

            exitCode.Should().Be(0, stderr);
            var result = JsonNode.Parse(stdout)!.AsObject();
            var first = result["first"]!.GetValue<string>();
            var second = result["second"]!.GetValue<string>();
            var file = result["files"]!.AsArray().Should().ContainSingle().Which!.AsObject();
            file.Select(pair => pair.Key)
                .Should()
                .NotContain(["blame", "commits", "origin_pr_ids"], "the changed-file list carries no history");
            file["provenance_status"]!.GetValue<string>().Should().Be("complete");
            var history = result["history"]!;
            var blamed = history["files"]!.AsArray().Should().ContainSingle().Which!;
            blamed["path"]!.GetValue<string>().Should().Be("a.txt");
            blamed["lines"]!.GetValue<int>().Should().Be(5);
            blamed["by_commit"]!
                .AsArray()
                .Select(row => $"{row!["commit"]} {row["lines"]} {row["base_lines"]}")
                .Should()
                .Equal($"{first} 3 3-4,11", $"{second} 2 5-6");
            blamed["origin_pr_ids"]!.AsArray().Select(node => node!.GetValue<int>()).Should().Equal(5);
            var commit = history["commits"]![first]!;
            commit["author"]!.GetValue<string>().Should().Be("Ann");
            commit["summary"]!.GetValue<string>().Should().Be("Add a");
            commit["date"]!.GetValue<string>().Should().Be("2026-01-02");
            commit["pr_ids"]!.AsArray().Select(node => node!.GetValue<int>()).Should().Equal(5);
            history["commits"]![second]!["author"]!.GetValue<string>().Should().Be("Bo");
            var pr = history["pull_requests"]!["5"]!;
            pr["author"]!.GetValue<string>().Should().Be("Ann Lee", "a person is a name, not an identity record");
            pr["description"]!.GetValue<string>().Should().Be(new string('d', 300) + " ... [100 more characters]");
            result["markdown"]!.GetValue<string>().Should().Contain($"- {first}: PR \\#5");

            // GitHub reports the same way; its blame here also counts the three lines of context around each change.
            var github = result["github"]!;
            github["files"]!
                .AsArray()
                .Should()
                .ContainSingle()
                .Which!.AsObject()
                .ContainsKey("blame")
                .Should()
                .BeFalse();
            var githubBlamed = github["history"]!["files"]!.AsArray().Should().ContainSingle().Which!;
            githubBlamed["by_commit"]!
                .AsArray()
                .Select(row => $"{row!["commit"]} {row["lines"]} {row["base_lines"]}")
                .Should()
                .Equal($"{first} 10 1-4,7-12", $"{second} 2 5-6");
            githubBlamed["origin_pr_ids"]!.AsArray().Select(node => node!.GetValue<int>()).Should().Equal(5);

            var capped = result["capped"]!.AsArray().Should().ContainSingle().Which!;
            capped["by_commit"]!.AsArray().Should().ContainSingle().Which!["commit"]!
                .GetValue<string>()
                .Should()
                .Be(first);
            capped["more"]!.GetValue<string>().Should().Be("1 more commits (2 lines) not listed");

            var skipped = result["skipped"]!;
            skipped["files"]![0]!["provenance_status"]!.GetValue<string>().Should().Be("skipped-too-many-lines");
            skipped["blame"]!.AsArray().Should().BeEmpty("a file with too many changed lines is not blamed");
            skipped["warnings"]!.AsArray().Should().BeEmpty("the review goes on without that file's history");
            skipped["complete"]!.GetValue<bool>().Should().BeTrue();
        }
        finally
        {
            RealGitFixture.ForceDelete(repo);
        }
    }

    [SkippableFact]
    public async Task The_kit_keeps_the_PRs_own_work_items_in_full_and_lists_the_rest_of_the_family_by_title()
    {
        // A feature (1) over the PR's story (10) and task (20), with a sibling task (21), 107 other stories (100-206)
        // and a task (300) under the last of them.
        const string harness = """
            import json, sys
            sys.path.insert(0, sys.argv[1])
            import review_ado as rd
            url = lambda number: f'https://dev.azure.com/o/p/_apis/wit/workitems/{number}'
            link = lambda kind, number: {'rel': f'System.LinkTypes.Hierarchy-{kind}', 'url': url(number)}
            family = {1: ('Feature', None, [10, *range(100, 207)]), 10: ('User Story', 1, [20, 21]),
                      20: ('Task', 10, []), 21: ('Task', 10, []),
                      **{n: ('User Story', 1, [300] if n == 206 else []) for n in range(100, 207)},
                      300: ('Task', 206, [])}
            def item(number):
                kind, parent, children = family[number]
                return {'id': number, 'rev': 3, 'url': url(number),
                        'fields': {'System.WorkItemType': kind, 'System.Title': f'Item {number}', 'System.State': 'Active',
                                   'System.Description': '<div>Fix <b>the</b>&nbsp;thing<br>now</div><p>Second</p>',
                                   'Microsoft.VSTS.Common.AcceptanceCriteria': '<ul><li>One</li><li>Two &amp; three</li></ul>',
                                   'System.AssignedTo': {'displayName': 'Ann Lee', 'uniqueName': 'ann@x', 'id': 'guid',
                                                         'imageUrl': 'https://x/avatar'}},
                        'relations': ([link('Reverse', parent)] if parent else []) + [link('Forward', c) for c in children]
                                     + ([{'rel': 'ArtifactLink', 'url': 'vstfs:///Git/Commit/x'}] if number in (20, 100) else [])}
            class Client:
                def request(self, method, path, body=None):
                    if method == 'GET':
                        return {'value': [{'id': '20'}]}
                    return {'value': [item(number) for number in body['ids']]}
            result = rd._collect_work_items(Client(), {'pr_number': 7, 'repository': {'id': 'r', 'project': {'id': 'p'}}})
            nodes = {node['id']: node for node in result['nodes']}
            print(json.dumps({'ids': sorted(nodes), 'task': nodes[20], 'feature': nodes[1], 'other': nodes[100],
                              'notListed': result.get('not_listed'), 'edges': result['edges'],
                              'complete': result['complete']}))
            """;

        var (exitCode, stdout, stderr) = await RunKitAsync(harness);

        exitCode.Should().Be(0, stderr);
        var result = JsonNode.Parse(stdout)!.AsObject();
        var ids = result["ids"]!.AsArray().Select(node => node!.GetValue<int>()).ToList();
        ids.Should()
            .Equal(
                new[] { 1, 10, 20, 21 }.Concat(Enumerable.Range(100, 99)),
                "the PR's items, their parents and the first 100 others"
            );
        result["notListed"]!.GetValue<string>().Should().Be("9 more work items in the same family");
        foreach (var full in new[] { result["task"]!, result["feature"]! })
        {
            full["description"]!.GetValue<string>().Should().Be("Fix the thing\nnow\nSecond", "HTML is read as text");
            full["acceptance_criteria"]!.GetValue<string>().Should().Be("One\nTwo & three");
            full["assigned_to"]!.GetValue<string>().Should().Be("Ann Lee");
        }
        result["other"]!
            .AsObject()
            .Select(pair => pair.Key)
            .Should()
            .BeEquivalentTo(["id", "type", "title", "state"], "the rest of the family is listed by title");
        var edges = result["edges"]!.AsArray();
        edges.Should().HaveCount(111, "110 hierarchy links touch the PR's items or parents, and one commit link");
        edges
            .Where(edge => edge!["source"]!.GetValue<int>() == 206 || edge!["source"]!.GetValue<int>() == 100)
            .Should()
            .BeEmpty("links between unlisted or other items, and their commit links, are left out");
        result["complete"]!.GetValue<bool>().Should().BeTrue();
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunKitAsync(
        string harness,
        params string[] arguments
    )
    {
        var python = await ReviewdSummaryReplyTests.FindPythonAsync();
        Skip.If(python is null, "no Python interpreter on PATH, so the kit cannot be run");
        var scripts = Path.Combine(
            "samples",
            "CodeReviewDaemon.Sample",
            "review-host-kit",
            "skills",
            "review-setup",
            "scripts"
        );
        var kit = new DirectoryInfo(AppContext.BaseDirectory);
        while (kit is not null && !File.Exists(Path.Combine(kit.FullName, scripts, "review_ado.py")))
            kit = kit.Parent;
        Skip.If(kit is null, "the review-host kit is not in this checkout");
        var start = new System.Diagnostics.ProcessStartInfo(python!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-I", "-c", harness, Path.Combine(kit.FullName, scripts) }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, stderr);
    }
}
