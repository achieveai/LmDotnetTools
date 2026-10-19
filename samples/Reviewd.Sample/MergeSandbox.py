"""Gateway-only merge helper. JSON facts are not independent-review approval."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
from urllib.parse import quote, urlsplit


class Blocked(Exception):
    def __init__(self, code, message):
        self.code, self.message = code, message
        super().__init__(message)


def require(condition, code, message):
    if not condition:
        raise Blocked(code, message)


def safe_output(value):
    value = re.sub(r'(https?://)[^/\s]+@', r'\1[redacted]@', value)
    value = re.sub(r'(?i)(authorization\s*[:=]\s*|bearer\s+|token\s*[:=]\s*)\S+',
                   r'\1[redacted]', value)
    return value[-12000:]


def run(argv, directory, timeout=120):
    started = time.monotonic()
    # Do not inherit caller Git plumbing overrides that redirect the index/repository.
    env = {k: v for k, v in os.environ.items()
           if k not in ('GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE',
                        'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES')}
    env['GIT_TERMINAL_PROMPT'] = '0'
    env['GIT_OPTIONAL_LOCKS'] = '0'
    try:
        completed = subprocess.run(argv, cwd=directory, env=env, capture_output=True,
                                   text=True, errors='replace', timeout=timeout)
        return {'exitCode': completed.returncode, 'elapsedSeconds': round(time.monotonic() - started, 3),
                'stdout': safe_output(completed.stdout), 'stderr': safe_output(completed.stderr),
                'timedOut': False, 'missingTool': False}, completed.stdout
    except FileNotFoundError:
        return {'exitCode': None, 'elapsedSeconds': round(time.monotonic() - started, 3),
                'stdout': '', 'stderr': 'Executable unavailable.', 'timedOut': False,
                'missingTool': True}, ''
    except subprocess.TimeoutExpired:
        return {'exitCode': None, 'elapsedSeconds': round(time.monotonic() - started, 3),
                'stdout': '', 'stderr': 'Command timed out; inspect before retrying.',
                'timedOut': True, 'missingTool': False}, ''


def git(directory, *arguments, allow_failure=False):
    result, raw = run(['git', *arguments], directory)
    require(allow_failure or result['exitCode'] == 0, 'GIT_FAILED',
            'Git command failed: ' + ' '.join(arguments[:2]) + '. ' + result['stderr'])
    return raw.strip(), result


def sha(value, name):
    require(isinstance(value, str) and re.fullmatch(r'[0-9a-f]{40}', value) is not None,
            'INVALID_PIN', name + ' must be a full lowercase SHA-1 commit ID.')
    return value


def inside(root, relative):
    require(isinstance(relative, str) and relative and not Path(relative).is_absolute(),
            'INVALID_DIRECTORY', 'Directory must be workspace-relative.')
    path = (root / relative).resolve()
    require(path.is_relative_to(root.resolve()) and path != root.resolve(),
            'INVALID_DIRECTORY', 'Directory escapes the workspace or selects its root.')
    return path


def identity(args):
    require(args.get('kind') in ('source', 'artifact'), 'INVALID_KIND', 'Unknown merge kind.')
    operation = args.get('operationId')
    require(isinstance(operation, str) and re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,79}', operation),
            'INVALID_OPERATION', 'Operation ID must be a safe, nonempty identifier.')
    for key in ('sourceSha', 'targetSha'):
        sha(args.get(key), key)
    for key in ('sourceRef', 'targetRef'):
        ref = args.get(key)
        require(isinstance(ref, str) and ref.startswith('refs/heads/'),
                'INVALID_REF', 'Source and target must be explicit branch refs.')
        git(Path.cwd(), 'check-ref-format', ref)
    require(args['sourceRef'] != args['targetRef'], 'INVALID_REF', 'Source and target refs must differ.')
    url = args.get('repoUrl')
    require(isinstance(url, str) and url and not url.startswith('-') and '\n' not in url,
            'INVALID_REPOSITORY', 'An exact repository URL is required.')
    require(args.get('sourceRepoUrl', url) == url and args.get('targetRepoUrl', url) == url,
            'FORK_UNSUPPORTED', 'Cross-repository PRs require verified fork destination support.')
    return {k: args[k] for k in ('kind', 'repoUrl', 'sourceRef', 'targetRef',
                                 'sourceSha', 'targetSha', 'operationId')}


def provider_metadata(root, args):
    number = args.get('prId')
    require(isinstance(number, str) and number.isdecimal() and int(number) > 0,
            'INVALID_PR', 'Source recovery requires a positive PR identity.')
    provider = args.get('provider')
    if provider == 'azure-devops':
        scripts = root / '.claude/skills/review-setup/scripts'
        require(scripts.is_dir(), 'PROVIDER_UNAVAILABLE', 'Installed provider transport is unavailable.')
        sys.path.insert(0, str(scripts))
        import review_ado
        org, project, repository = args.get('org'), args.get('project'), args.get('repo')
        require(all(isinstance(v, str) and v for v in (org, project, repository)),
                'INVALID_PR', 'ADO organization/project/repository must be explicit.')
        transport = review_ado.select_transport('https://dev.azure.com/' + org, transport='gateway')
        actual_number, payload = review_ado.fetch_pr(transport, int(number))
        remote_repo = payload.get('repository') or {}
        require(payload.get('forkSource') is None, 'FORK_UNSUPPORTED',
                'Fork source destinations are not yet certified.')
        require(actual_number == int(number) and remote_repo.get('name', '').casefold() == repository.casefold()
                and (remote_repo.get('project') or {}).get('name', '').casefold() == project.casefold(),
                'PR_IDENTITY_CHANGED', 'Provider returned another repository or PR.')
        require(payload.get('status') == 'active' and payload.get('isDraft') is False,
                'PR_CLOSED_OR_DRAFT', 'PR must remain active and non-draft.')
        require(payload.get('sourceRefName') == args['sourceRef'] and payload.get('targetRefName') == args['targetRef']
                and (payload.get('lastMergeSourceCommit') or {}).get('commitId') == args['sourceSha'],
                'PR_PIN_CHANGED', 'Live PR source or branch identity changed.')
        expected = 'https://dev.azure.com/' + org + '/' + quote(project, safe='') + '/_git/' + quote(repository, safe='')
        require(args['repoUrl'].rstrip('/') == expected, 'PUSH_DESTINATION_CHANGED', 'Remote does not identify this PR repository.')
        merge_status = payload.get('mergeStatus')
    elif provider == 'github':
        owner, repository = args.get('owner'), args.get('repo')
        require(all(isinstance(v, str) and v for v in (owner, repository)),
                'INVALID_PR', 'GitHub owner/repository must be explicit.')
        endpoint = 'https://api.github.com/repos/' + quote(owner, safe='') + '/' + quote(repository, safe='') + '/pulls/' + number
        response, raw = run(['curl', '--fail', '--silent', '--show-error', '--max-time', '30',
                             '--proto', '=https', '-H', 'Accept: application/vnd.github+json', endpoint], root)
        require(response['exitCode'] == 0, 'PROVIDER_READ_FAILED', 'GitHub factual metadata read failed; do not infer conflicts.')
        payload = json.loads(raw)
        head, target = payload.get('head') or {}, payload.get('base') or {}
        full_name = owner + '/' + repository
        require(payload.get('number') == int(number) and payload.get('state') == 'open' and payload.get('draft') is False,
                'PR_CLOSED_OR_DRAFT', 'GitHub PR must remain open and non-draft.')
        require((head.get('repo') or {}).get('full_name') == full_name
                and (target.get('repo') or {}).get('full_name') == full_name,
                'FORK_UNSUPPORTED', 'Fork source destinations are not yet certified.')
        require(head.get('sha') == args['sourceSha'] and target.get('sha') == args['targetSha']
                and 'refs/heads/' + str(head.get('ref')) == args['sourceRef']
                and 'refs/heads/' + str(target.get('ref')) == args['targetRef'],
                'PR_PIN_CHANGED', 'Live GitHub PR refs or revisions changed.')
        require(args['repoUrl'].rstrip('/') in ('https://github.com/' + full_name, 'https://github.com/' + full_name + '.git'),
                'PUSH_DESTINATION_CHANGED', 'Remote does not identify this GitHub PR repository.')
        merge_status = 'conflicts' if payload.get('mergeable') is False and payload.get('mergeable_state') == 'dirty' else payload.get('mergeable_state')
    else:
        raise Blocked('PROVIDER_UNSUPPORTED', 'Source recovery requires an explicit supported provider.')
    return {'provider': provider, 'prId': number, 'mergeStatus': merge_status}


def source_directory(root, args, create=False):
    expected = 'operator-scratch/merge-' + args['operationId'] + '/repo'
    require(args.get('directory', expected) == expected, 'OWNERSHIP_MISMATCH',
            'Source merges must use their exclusive operation checkout.')
    directory = inside(root, expected)
    # Reject even in-workspace symlink redirection of the operation namespace.
    require(directory == root.resolve() / expected, 'OWNERSHIP_MISMATCH', 'Scratch path is redirected.')
    owner = directory.parent / 'operation.json'
    expected_identity = identity(args)
    if create:
        directory.parent.mkdir(parents=True, exist_ok=False)
        with owner.open('x') as stream:
            json.dump(expected_identity, stream, sort_keys=True)
    else:
        require(owner.is_file(), 'OWNERSHIP_MISSING', 'Operation ownership record is unavailable.')
        require(json.loads(owner.read_text()) == expected_identity, 'OWNERSHIP_MISMATCH',
                'Existing operation belongs to different immutable merge inputs.')
    return directory


def checkout(directory, exclusive=False):
    require(directory.is_dir(), 'CHECKOUT_MISSING', 'Merge checkout is unavailable.')
    top, _ = git(directory, 'rev-parse', '--show-toplevel')
    require(Path(top).resolve() == directory.resolve(), 'CHECKOUT_MISMATCH',
            'Directory is not the exact repository root.')
    if exclusive:
        common, _ = git(directory, 'rev-parse', '--git-common-dir')
        require(not (directory / '.git').is_symlink() and
                (directory / common).resolve() == directory / '.git', 'SHARED_REPOSITORY',
                'Source merge checkout must not share Git metadata/worktrees.')
        require(not (directory / '.git/objects/info/alternates').exists(), 'SHARED_REPOSITORY',
                'Git object alternates are unsupported.')


def remote_pins(directory, args):
    output, _ = git(directory, 'ls-remote', '--refs', args['repoUrl'],
                    args['sourceRef'], args['targetRef'])
    refs = dict((line.split('\t')[1], line.split('\t')[0])
                for line in output.splitlines() if '\t' in line)
    require(refs.get(args['sourceRef']) == args['sourceSha'] and
            refs.get(args['targetRef']) == args['targetSha'], 'REMOTE_PIN_CHANGED',
            'Remote source/target no longer match both approved pins.')


def artifact_directory(root, args):
    identity(args)
    directory = root if args.get('directory') == '.' else inside(root, args.get('directory'))
    checkout(directory)
    for name in ('rootStartSha', 'reviewTipSha', 'baselineSha'):
        sha(args.get(name), name)
    require(args['sourceSha'] == args['rootStartSha'] and args['targetSha'] == args['reviewTipSha'],
            'ARTIFACT_PIN_MISMATCH', 'Artifact merge pins must identify root start and review tip.')
    module = args.get('modulePath')
    require(isinstance(module, str) and module and not Path(module).is_absolute() and
            '..' not in Path(module).parts, 'INVALID_MODULE', 'Exact relative module path is required.')
    require(inside(directory, module).is_relative_to(directory), 'INVALID_MODULE', 'Module escapes checkout.')
    return directory


def conflict_paths(directory):
    output, _ = git(directory, 'ls-files', '-u', '-z')
    return sorted({entry.split('\t', 1)[1] for entry in output.split('\0') if '\t' in entry})


def state(directory, args):
    checkout(directory, exclusive=args['kind'] == 'source')
    head, _ = git(directory, 'rev-parse', 'HEAD')
    merge_path, _ = git(directory, 'rev-parse', '--git-path', 'MERGE_HEAD')
    merge_file = directory / merge_path
    merge = merge_file.read_text().strip() if merge_file.is_file() else None
    return {'directory': str(directory.relative_to(Path.cwd().resolve())), 'headSha': head,
            'mergeSha': merge, 'conflictPaths': conflict_paths(directory), 'mergeResolved': False,
            'checksPassed': False, 'treeSha': None, 'checks': [], 'blockers': []}


def artifact_baseline(directory, args):
    # A review merge must never silently change the containing repository's module pointer.
    # The existing artifact workflow may normalize a stale branch gitlink to a
    # freshly fetched baseline. Validate the candidate index, not historical tips.
    output, _ = git(directory, 'ls-files', '--stage', '--', args['modulePath'])
    require(output == '160000 ' + args['baselineSha'] + ' 0\t' + args['modulePath'],
            'GITLINK_CHANGED', 'Index does not preserve the exact baseline module gitlink.')


def check_command(spec, directory):
    require(isinstance(spec, dict), 'INVALID_CHECK', 'Check must be an argv specification.')
    argv = spec.get('argv')
    require(isinstance(argv, list) and len(argv) >= 2 and
            all(isinstance(item, str) and item and '\0' not in item for item in argv),
            'INVALID_CHECK', 'Check argv must contain nonempty strings.')
    require((argv[0] == 'dotnet' and argv[1] in ('test', 'build')) or
            (argv[0] == 'git' and argv[1] == 'diff'), 'CHECK_UNSUPPORTED',
            'Only explicit dotnet build/test and git diff checks are supported.')
    relative = spec.get('directory', '.')
    work = directory if relative == '.' else inside(directory, relative)
    require(work.is_dir(), 'INVALID_CHECK_DIRECTORY', 'Check directory is unavailable.')
    timeout = spec.get('timeoutSeconds', 600)
    require(type(timeout) is int and 1 <= timeout <= 1800, 'INVALID_CHECK', 'Invalid check timeout.')
    actual = argv if argv[0] != 'git' else ['git', 'diff', '--no-ext-diff', '--no-textconv', *argv[2:]]
    require(not any(item in ('--ext-diff', '--textconv', '--output') or item.startswith('--output=')
                    for item in argv[2:]) if argv[0] == 'git' else True,
            'CHECK_UNSUPPORTED', 'External diff/output-writing checks are unsupported.')
    return actual, work, timeout


def verify(root, args, run_checks=True):
    directory = source_directory(root, args) if args['kind'] == 'source' else artifact_directory(root, args)
    result = state(directory, args)
    require(result['headSha'] == args['sourceSha'] and result['mergeSha'] == args['targetSha'],
            'MERGE_TOPOLOGY_CHANGED', 'HEAD/MERGE_HEAD must equal the exact source/target pins.')
    if args['kind'] == 'artifact':
        artifact_baseline(directory, args)
    if result['conflictPaths']:
        result['blockers'].append({'code': 'UNRESOLVED_CONFLICTS', 'message': 'Unmerged index entries remain.'})
        return result
    for options in (('--cached', '--check'), ('--check',)):
        _, check = git(directory, 'diff', '--no-ext-diff', '--no-textconv', *options, allow_failure=True)
        require(check['exitCode'] == 0, 'WHITESPACE_ERRORS', 'Git diff --check failed. ' + check['stdout'])
    unstaged, _ = git(directory, 'diff', '--no-ext-diff', '--no-textconv', '--name-only', '--ignore-submodules=none')
    untracked, _ = git(directory, 'ls-files', '--others', '--exclude-standard', '-z')
    require(not unstaged and not untracked, 'UNEXPECTED_WORKTREE_CHANGES',
            'Stage intended merge resolutions; unexpected unstaged/untracked files remain.')
    result['treeSha'], _ = git(directory, 'write-tree')
    result['mergeResolved'] = True
    if not run_checks:
        return result
    checks = args.get('checks')
    require(isinstance(checks, list) and checks, 'CHECKS_REQUIRED', 'At least one explicit validation check is required.')
    for spec in checks:
        argv, work, timeout = check_command(spec, directory)
        check, _ = run(argv, work, timeout)
        check.update({'argv': spec['argv'], 'directory': spec.get('directory', '.')})
        result['checks'].append(check)
    result['checksPassed'] = all(check['exitCode'] == 0 for check in result['checks'])
    if not result['checksPassed']:
        result['blockers'].append({'code': 'VALIDATION_FAILED', 'message': 'A required check failed, timed out, or lacks its tool.'})
    # Build/test commands can alter tracked files/index. Re-check after the commands.
    after = verify(root, args, run_checks=False)
    require(after['mergeResolved'] and after['treeSha'] == result['treeSha'], 'VALIDATION_CHANGED_TREE',
            'Validation changed the merge tree or introduced unresolved entries.')
    return result


def prepare(root, args):
    require(args['kind'] == 'source', 'ARTIFACT_PREPARE_UNSUPPORTED',
            'Artifact merges must adopt an explicitly identified existing merge context.')
    existing = root / ('operator-scratch/merge-' + args['operationId'])
    if existing.exists():
        directory = source_directory(root, args)
        result = state(directory, args)
        require(result['headSha'] == args['sourceSha'] and result['mergeSha'] == args['targetSha'],
                'PREPARE_INCOMPLETE', 'Existing operation is not the expected pending merge; inspect without resetting.')
        result['resumed'] = True
        if result['conflictPaths']:
            result['blockers'].append({'code': 'UNRESOLVED_CONFLICTS', 'message': 'Existing merge still has actual Git conflicts.'})
        return result
    provider_metadata(root, args)
    directory = source_directory(root, args, create=True)
    # --no-local prevents local hardlinks/object sharing, including local manual fixtures.
    cloned, _ = run(['git', 'clone', '--no-local', '--no-hardlinks', '--no-checkout',
                     '--', args['repoUrl'], str(directory)], root)
    require(cloned['exitCode'] == 0, 'CLONE_FAILED', 'Clone failed; preserve the operation for inspection.')
    checkout(directory)
    git(directory, 'fetch', '--no-tags', 'origin',
        args['sourceRef'] + ':refs/remotes/operator/source',
        args['targetRef'] + ':refs/remotes/operator/target')
    source, _ = git(directory, 'rev-parse', 'refs/remotes/operator/source')
    target, _ = git(directory, 'rev-parse', 'refs/remotes/operator/target')
    require(source == args['sourceSha'] and target == args['targetSha'], 'REMOTE_PIN_CHANGED',
            'Fetched branches no longer match the approved pins.')
    git(directory, 'checkout', '--detach', args['sourceSha'])
    _, merge_result = git(directory, 'merge', '--no-commit', '--no-ff', args['targetSha'], allow_failure=True)
    result = state(directory, args)
    result['mergeCommand'] = merge_result
    if result['conflictPaths']:
        result['blockers'].append({'code': 'UNRESOLVED_CONFLICTS', 'message': 'Resolve and stage only these actual Git conflicts.'})
    elif merge_result['exitCode'] != 0:
        result['blockers'].append({'code': 'MERGE_FAILED', 'message': 'Merge failed without unmerged index entries; this is not a content conflict.'})
    elif result['mergeSha'] != args['targetSha']:
        result['blockers'].append({'code': 'NO_MERGE_NEEDED', 'message': 'No two-parent merge is pending (target may already be an ancestor).'})
    return result


def publish(root, args):
    require(args['kind'] == 'source', 'ARTIFACT_PUBLISH_UNSUPPORTED',
            'Artifact publication must use the separately guarded existing merge_review workflow.')
    tree = sha(args.get('verifiedTreeSha'), 'verifiedTreeSha')
    require(args.get('approvedTreeSha') == tree and isinstance(args.get('resultDigest'), str) and
            re.fullmatch('[0-9a-fA-F]{64}', args['resultDigest']), 'VERIFIED_REVIEW_REQUIRED',
            'Caller must supply persisted verification digest and independently approved exact tree.')
    # Python validates facts; only the coordinating caller can authenticate its independent reviewer.
    verified = verify(root, args)
    require(verified['mergeResolved'] and verified['checksPassed'] and verified['treeSha'] == tree,
            'VERIFICATION_CHANGED', 'Current merge no longer matches the reviewed passing tree.')
    directory = source_directory(root, args)
    provider_metadata(root, args)
    remote_pins(directory, args)
    journal = directory.parent / 'publication.json'
    require(not journal.exists(), 'PUBLICATION_ALREADY_ATTEMPTED',
            'Publication was already attempted. Inspect commit/remote/journal; do not automatically retry.')
    record = {'operationId': args['operationId'], 'resultDigest': args['resultDigest'],
              'treeSha': tree, 'sourceSha': args['sourceSha'], 'targetSha': args['targetSha'],
              'state': 'commit-started', 'commitSha': None}
    with journal.open('x') as stream:
        json.dump(record, stream, sort_keys=True)
    git(directory, 'commit', '-m', 'Merge ' + args['targetRef'] + ' into ' + args['sourceRef'])
    commit, _ = git(directory, 'rev-parse', 'HEAD')
    actual_tree, _ = git(directory, 'rev-parse', 'HEAD^{tree}')
    parents, _ = git(directory, 'rev-list', '--parents', '-n', '1', 'HEAD')
    require(actual_tree == tree and parents.split() == [commit, args['sourceSha'], args['targetSha']],
            'COMMIT_CHANGED', 'Commit hooks changed the reviewed tree or merge parents; do not push.')
    status, _ = git(directory, 'status', '--porcelain', '--untracked-files=normal')
    require(not status, 'COMMIT_CHANGED', 'Commit hooks left unexpected changes; do not push.')
    record.update({'state': 'commit-created', 'commitSha': commit})
    journal.write_text(json.dumps(record, sort_keys=True))
    provider_metadata(root, args)
    remote_pins(directory, args)
    record['state'] = 'push-started'
    journal.write_text(json.dumps(record, sort_keys=True))
    # Lease the push to the exact pinned source tip: remote_pins only proved sourceRef==sourceSha at
    # pin time, and a concurrent rewind before this push could otherwise fast-forward underneath us
    # (the merge's first parent IS sourceSha, so a rewind-then-readvance still fast-forwards). The
    # lease makes the push publish only while the remote source is still the reviewed base.
    _, pushed = git(directory, 'push', '--porcelain',
                    '--force-with-lease=' + args['sourceRef'] + ':' + args['sourceSha'],
                    '--', args['repoUrl'],
                    commit + ':' + args['sourceRef'], allow_failure=True)
    if pushed['exitCode'] != 0:
        raise Blocked('PUSH_OUTCOME_UNKNOWN', 'Push did not report success; independently inspect remote before any retry.')
    remote, _ = git(directory, 'ls-remote', '--refs', args['repoUrl'], args['sourceRef'])
    require(remote == commit + '\t' + args['sourceRef'], 'PUSH_OUTCOME_UNKNOWN',
            'Remote source did not independently confirm the published commit.')
    record['state'] = 'published'
    journal.write_text(json.dumps(record, sort_keys=True))
    verified.update({'commitSha': commit, 'publicationState': 'published',
                     'journalPath': str(journal.relative_to(root))})
    return verified


def dispatch(phase, root, args):
    identity(args)
    if phase == 'prepare':
        return prepare(root, args)
    if phase == 'verify':
        return verify(root, args)
    if phase == 'inspect-artifact':
        require(args['kind'] == 'artifact', 'INVALID_KIND', 'Artifact inspection requires artifact kind.')
        directory = artifact_directory(root, args)
        result = state(directory, args)
        require(result['headSha'] == args['rootStartSha'] and result['mergeSha'] == args['reviewTipSha'],
                'MERGE_TOPOLOGY_CHANGED', 'Existing artifact transaction does not match root start/review tip.')
        artifact_baseline(directory, args)
        return result
    if phase == 'inspect':
        facts = provider_metadata(root, args)
        remote_pins(root, args)
        return {'headSha': args['sourceSha'], 'mergeSha': args['targetSha'], 'provider': facts, 'blockers': []}
    if phase == 'publish':
        return publish(root, args)
    raise Blocked('UNKNOWN_PHASE', 'Unsupported merge helper phase.')


def main():
    result = {'directory': None, 'headSha': None, 'mergeSha': None, 'conflictPaths': [],
              'mergeResolved': False, 'checksPassed': False, 'treeSha': None, 'checks': [], 'blockers': []}
    try:
        require(len(sys.argv) == 3, 'INVALID_ARGUMENTS', 'Expected phase and one JSON argument.')
        args = json.loads(sys.argv[2])
        require(isinstance(args, dict), 'INVALID_ARGUMENTS', 'Arguments must be a JSON object.')
        result.update(dispatch(sys.argv[1], Path.cwd().resolve(), args))
    except Blocked as error:
        if len(sys.argv) == 3 and isinstance(locals().get('args'), dict) and sys.argv[1] == 'verify':
            try:
                root = Path.cwd().resolve()
                directory = source_directory(root, args) if args.get('kind') == 'source' else artifact_directory(root, args)
                facts = state(directory, args)
                facts['mergeResolved'] = (facts['headSha'] == args.get('sourceSha')
                                          and facts['mergeSha'] == args.get('targetSha')
                                          and not facts['conflictPaths'])
                result.update(facts)
            except Exception:
                pass
        result['blockers'].append({'code': error.code, 'message': safe_output(error.message)})
    except Exception as error:
        # One parseable JSON result, including malformed inputs and filesystem failures.
        result['blockers'].append({'code': 'HELPER_ERROR', 'message': safe_output(str(error))})
    print(json.dumps(result, sort_keys=True))


if __name__ == '__main__':
    main()
