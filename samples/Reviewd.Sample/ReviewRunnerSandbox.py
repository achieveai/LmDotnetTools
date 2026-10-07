"""Executed only through Gateway; uses the installed review-setup helpers."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
from urllib.parse import quote

phase, encoded = sys.argv[1:]
args = json.loads(encoded)
root = Path.cwd()
scripts = root / '.claude/skills/review-setup/scripts'
sys.path.insert(0, str(scripts))
import review_ado
from review_common import ReviewError, ReviewState, SourcePool, repo_slug, safe_path, run_git_read
from review_pool_admin import select_module_by_name

repo, pr, slot = args['repo'], int(args['pr']), int(args['slot'])
state = ReviewState(root, read_only=True)
module = select_module_by_name(root, repo)
repo = module['path'].split('/')[-1]
pool = SourcePool(state, module)
outer = safe_path(root, pool.slot_relative_path(slot))
source = safe_path(outer, module['path'])
directory = f'PRs/{repo_slug(repo)}-{pr}'
transport = review_ado.select_transport('https://dev.azure.com/' + args['org'], transport='gateway')


def eligibility():
    number, payload = review_ado.fetch_pr(transport, pr)
    repository = payload.get('repository') or {}
    project = repository.get('project') or {}
    if (number != pr or repository.get('name', '').casefold() != repo.casefold()
            or project.get('name', '').casefold() != args['project'].casefold()):
        raise ReviewError('PR identity does not match the requested repository.')
    return payload


def live():
    payload = eligibility()
    if payload.get('status') != 'active' or payload.get('isDraft') is not False:
        raise ReviewError('PR is not active/non-draft in the requested repository.')
    head = (payload.get('lastMergeSourceCommit') or {}).get('commitId')
    merge = (payload.get('lastMergeCommit') or {}).get('commitId')
    if head != args['head'] or not isinstance(merge, str) or len(merge) != 40:
        raise ReviewError('PR source changed or merge is unavailable; no submission allowed.')
    if args.get('merge') and merge != args['merge']:
        raise ReviewError('Prepared PR merge changed; inspect before continuing.')
    return payload, merge


def inspect_prepared():
    manifest = json.loads(state.prepared_slot_path(pool.repo_key, slot).read_text())
    _, merge = live()
    if (manifest.get('schema') != 1 or manifest.get('repo') != repo
            or manifest.get('slot') != slot or manifest.get('pr') != pr
            or manifest.get('merge_sha') != merge):
        raise ReviewError('Prepared manifest does not match this PR/slot/revision.')
    head = run_git_read(source, ['rev-parse', 'HEAD']).stdout.strip()
    if head != merge:
        raise ReviewError('Source checkout moved from prepared merge.')
    return merge


if phase == 'eligibility':
    payload = eligibility()
    print(json.dumps({'prId': str(pr), 'status': payload.get('status'),
                      'isDraft': payload.get('isDraft'),
                      'headSha': (payload.get('lastMergeSourceCommit') or {}).get('commitId'),
                      'mergeSha': (payload.get('lastMergeCommit') or {}).get('commitId')}))
elif phase == 'inspect':
    _, merge = live()
    print(json.dumps({'headSha': args['head'], 'mergeSha': merge}))
elif phase == 'inspect-prepared':
    merge = inspect_prepared()
    if not safe_path(outer, directory + '/setup-context.json').is_file():
        raise ReviewError('Retained prepared context is unavailable.')
    print(json.dumps({'headSha': args['head'], 'mergeSha': merge}))
elif phase == 'prepare':
    live()
    from review_common import atomic_json, lock_session
    from review_git_prepare import _verify_postconditions
    writable = ReviewState(root)
    pool = SourcePool(writable, module)
    marker = writable.directory / 'reviewd-owners' / f'{pool.repo_key}-{slot}.json'
    # Native prepare can clean destructively. Admit only an already idle slot; never repair a predecessor.
    with lock_session(120), writable.lock(shared=True, phase='reviewd-admit'), writable.slot_lock(pool.repo_key, slot, phase='reviewd-admit'):
        previous = json.loads(marker.read_text()) if marker.exists() else None
        if previous and previous.get('state') != 'released':
            raise ReviewError('Native slot has an unreleased reviewd owner.')
        if state.prepared_slot_path(pool.repo_key, slot).exists() or writable.load_pool_run(pool.repo_key, slot, dry_run=True) is not None:
            raise ReviewError('Native slot retains an owner; explicit owner cleanup is required.')
        if outer.exists():
            _verify_postconditions(writable, pool, outer, source,
                                   run_git_read(root, ['rev-parse', 'HEAD']).stdout.strip(),
                                   pool._verified_baseline())
        atomic_json(marker, {'claimToken': args['claimToken'], 'pr': pr, 'state': 'owned'})
    # The CLI does not expose prepare's lock_timeout. Use its existing route with a bounded
    # native-lock wait so overlapping source fetches contend locally, not at a batch gate.
    driver = """import sys
from pathlib import Path
sys.path.insert(0, sys.argv[1])
import review_setup, review_ado
from review_git_prepare import prepare
review_setup.prepare = lambda **kwargs: prepare(**kwargs, lock_timeout=120)
provider = review_ado.AdoProvider(sys.argv[2], transport='gateway')
raise SystemExit(review_setup.route(provider=provider, pr=int(sys.argv[3]), repo=sys.argv[4], slot=int(sys.argv[5]), root=Path.cwd()))
"""
    setup = subprocess.run([sys.executable, '-c', driver, str(scripts),
                            'https://dev.azure.com/' + args['org'], str(pr), repo, str(slot)],
                           capture_output=True, text=True)
    if setup.returncode != 0:
        raise ReviewError(f'Setup stopped with exit {setup.returncode}; preserve the slot and inspect it.')
    report = json.loads(setup.stdout)
    snapshot = report.get('snapshot') or {}
    if report.get('complete') is not True or snapshot.get('head_sha') != args['head']:
        raise ReviewError('Prepared context is incomplete or belongs to a different revision.')
    merge = inspect_prepared()
    folder = safe_path(outer, directory)
    folder.mkdir(parents=True, exist_ok=True)
    context = safe_path(folder, 'setup-context.json')
    with context.open('x') as stream:
        stream.write(setup.stdout)
    payload, _ = live()
    repo_api = (quote(payload['repository']['project']['id'], safe='') + '/_apis/git/repositories/'
                + quote(payload['repository']['id'], safe='') + '/pullRequests/' + str(pr))
    threads = transport.request('GET', repo_api + '/threads?api-version=7.1')
    values = threads.get('value') if isinstance(threads, dict) else None
    if not isinstance(values, list) or threads.get('count') != len(values):
        raise ReviewError('Incomplete baseline discussions.')
    baseline = [[thread['id'], comment['id']] for thread in values for comment in thread.get('comments', [])]
    print(json.dumps({'mergeSha': merge, 'contextPath': str(context.relative_to(root)),
                      'reviewDirectory': str(folder.relative_to(root)), 'baselineComments': baseline}))
elif phase == 'cleanup':
    from review_common import (atomic_json, lock_session, inspect_checkout_preservation,
                               preserve_checkout_generation, verify_preservation_archive,
                               preservation_generation, file_hash)
    from review_git_prepare import _verify_postconditions
    from review_reset import _discard_prepared_slot, reset_pool_run_locked
    from review_common import slot_writer_scope, git_status, git_ignored_paths
    writable = ReviewState(root)
    pool = SourcePool(writable, module)
    marker = writable.directory / 'reviewd-owners' / f'{pool.repo_key}-{slot}.json'
    with lock_session(120), writable.lock(shared=True, phase='reviewd-cleanup'), writable.slot_lock(pool.repo_key, slot, phase='reviewd-cleanup'):
        previous = json.loads(marker.read_text()) if marker.exists() else None
        if previous is None and args.get('adopt'):
            prepared_path = state.prepared_slot_path(pool.repo_key, slot)
            if prepared_path.exists():
                prepared = json.loads(prepared_path.read_text())
                if prepared.get('pr') != pr:
                    raise ReviewError('Legacy native owner does not match the explicit adoption.')
            else:
                _verify_postconditions(writable, pool, outer, source,
                                       run_git_read(root, ['rev-parse', 'HEAD']).stdout.strip(), pool._verified_baseline())
            previous = {'claimToken': args['claimToken'], 'pr': pr, 'state': 'owned'}
            atomic_json(marker, previous)
        if previous is None and args.get('failedPreparation'):
            if state.prepared_slot_path(pool.repo_key, slot).exists() or writable.load_pool_run(pool.repo_key, slot, dry_run=True) is not None:
                raise ReviewError('Unclaimed native predecessor remains; no failed-attempt reset allowed.')
            _verify_postconditions(writable, pool, outer, source,
                                   run_git_read(root, ['rev-parse', 'HEAD']).stdout.strip(), pool._verified_baseline())
            previous = {'claimToken': args['claimToken'], 'pr': pr, 'state': 'owned'}
            atomic_json(marker, previous)
        if not previous or previous.get('claimToken') != args['claimToken'] or previous.get('pr') != pr or previous.get('state') != 'owned':
            raise ReviewError('Native claim changed; no cleanup allowed.')
        prepared_path = state.prepared_slot_path(pool.repo_key, slot)
        if prepared_path.exists() and json.loads(prepared_path.read_text()).get('pr') != pr:
            raise ReviewError('Native prepared owner changed; no reset allowed.')
        if git_status(source, read_only=True) or git_ignored_paths(source, read_only=True):
            raise ReviewError('Source checkout has local data; no automatic destructive cleanup allowed.')
        # Preserve all outside-source attempt artifacts with native hash/fsync verification before reset.
        inspection = inspect_checkout_preservation(outer, protected=source)
        preserved = preserve_checkout_generation(writable, inspection,
                                                operation_id='reviewd-' + args['claimToken'], repository=outer)
        binding = {'schema_version': 1, 'workspace_identity': writable.identity, 'outer': inspection}
        verify_preservation_archive(Path(preserved['path']), expected_manifest_sha256=preserved['manifest_sha256'],
                                    expected_generation=preservation_generation(binding), expected_binding=binding)
        # Invoke native already-locked entry points; the exact token fence stays held through reset.
        with slot_writer_scope(writable, pool.repo_key, slot, phase='pooled-reset'):
            legacy = writable.load_pool_run(pool.repo_key, slot, dry_run=True)
            if legacy is not None and legacy.get('pr') != pr and legacy.get('pr_number') != pr:
                raise ReviewError('Legacy native owner differs; no automatic reset allowed.')
            if prepared_path.exists():
                result = _discard_prepared_slot(writable, pool, slot, confirm='YES')
            elif legacy is not None:
                result = reset_pool_run_locked(writable, repo, slot, mode='discard', confirm='YES', run_maintenance=False)
            elif args.get('failedPreparation') or args.get('adopt'):
                result = {'status': 'already-idle'}
            else:
                raise ReviewError('Native owner disappeared; inspect before release.')
            _verify_postconditions(writable, pool, outer, source,
                                   run_git_read(root, ['rev-parse', 'HEAD']).stdout.strip(), pool._verified_baseline())
            previous['state'] = 'released'
            atomic_json(marker, previous)
            print(json.dumps({'claimToken': args['claimToken'], 'cleanBaselineVerified': True,
                              'evidencePreserved': True, 'preservation': preserved, 'reset': result}))
elif phase == 'verify':
    inspect_prepared()
    receipt = json.loads(safe_path(outer, directory + '/publication_receipts.json').read_text())
    if (str(receipt.get('prId')) != str(pr) or receipt.get('headSha') != args['head']
            or receipt.get('mergeSha') != args['merge']):
        raise ReviewError('Publication receipt does not identify the prepared PR revision.')
    comments = receipt.get('comments')
    if not isinstance(comments, list) or not comments or not any(c.get('kind') == 'summary' for c in comments):
        raise ReviewError('Publication receipt must include a posted summary.')
    payload, _ = live()
    api = (quote(payload['repository']['project']['id'], safe='') + '/_apis/git/repositories/'
           + quote(payload['repository']['id'], safe='') + '/pullRequests/' + str(pr))
    verified, seen = [], set()
    for comment in comments:
        thread_id, comment_id = comment.get('threadId'), comment.get('commentId')
        if (type(thread_id) is not int or thread_id <= 0 or type(comment_id) is not int or comment_id <= 0
                or (thread_id, comment_id) in seen or [thread_id, comment_id] in args['baselineComments']):
            raise ReviewError('Publication IDs invalid, duplicated, or already present before this review.')
        seen.add((thread_id, comment_id))
        thread = transport.request('GET', api + f'/threads/{thread_id}?api-version=7.1')
        posted = next((c for c in thread.get('comments', []) if c.get('id') == comment_id), None)
        if (thread.get('id') != thread_id or thread.get('isDeleted') or posted is None
                or posted.get('isDeleted') or posted.get('content') != comment.get('content')
                or not isinstance(comment.get('content'), str) or not comment['content'].strip()):
            raise ReviewError('Publication could not be independently verified.')
        verified.append({'threadId': thread_id, 'commentId': comment_id, 'kind': comment.get('kind'),
                         'contentSha256': hashlib.sha256(posted['content'].encode()).hexdigest()})
    print(json.dumps({'comments': verified}))
elif phase == 'commit':
    inspect_prepared()
    from review_commit import commit_review
    print(json.dumps(commit_review(root, repo, pr, slot, source_sha=args['head'])))
else:
    raise ReviewError('Unknown review-runner sandbox phase.')
