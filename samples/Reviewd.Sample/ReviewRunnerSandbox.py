"""Executed only through Gateway; uses the installed review-setup helpers."""
import hashlib
import json
import os
from pathlib import Path
import shutil
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


def summary_reply(thread, posted, summary_threads):
    # A re-review of an already-summarized PR posts its summary as a reply in the earlier summary thread. Only
    # a thread an earlier verified round of this PR recorded as its summary counts, so a reply in any other
    # general thread the reviewer opened (a PR-level finding, say) is not taken for a summary. It must still be
    # a general (not inline) thread opened by the same author.
    opener = min(thread.get('comments', []), key=lambda c: c.get('id', 0), default={})
    author = (opener.get('author') or {}).get('id')
    return (thread.get('id') in summary_threads and not thread.get('threadContext')
            and opener.get('id') != posted.get('id')
            and author is not None and author == (posted.get('author') or {}).get('id'))


def slot_not_idle(reason):
    # Admission was refused before anything native was written, so the caller may release its own
    # claim and try another slot. Exit 3 plus this status line is the contract the runner checks.
    print(json.dumps({'status': 'slot-not-idle', 'reason': reason}))
    sys.exit(3)


def admit_baseline(verify):
    # An idle slot off the idle baseline (a commit to the store's main strands every idle slot behind it) is not
    # usable as is, and admission never repairs it. It is refused like a held slot, so an automatic run releases
    # its claim and tries the next slot instead of failing with the claim held until its lease lapses.
    try:
        verify()
    except ReviewError as failure:
        slot_not_idle(f'Native slot is not at the idle baseline: {failure}')


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


def clear_stale_index_locks():
    # A canceled agent command can kill Git mid-refresh (a `git status` did) and leave index.lock behind. Git then
    # refuses every later commit or reset in this slot, so the slot never frees. Call only while this attempt owns
    # the slot. Remove only the slot's own per-worktree locks, and only while no process works inside the slot.
    import time
    inside = os.path.realpath(outer) + os.sep
    for entry in Path('/proc').iterdir():
        try:
            if entry.name.isdigit() and (os.readlink(entry / 'cwd') + os.sep).startswith(inside):
                return
        except OSError:
            continue
    for checkout in (outer, source):
        if not (checkout / '.git').exists():
            continue
        lock = Path(run_git_read(checkout, ['rev-parse', '--path-format=absolute', '--git-path', 'index.lock']).stdout.strip())
        try:
            age = time.time() - lock.stat().st_mtime
        except FileNotFoundError:
            continue
        if age >= 60:
            lock.unlink()
            print(f'Removed stale {lock} ({int(age)}s old; no process in the slot).', file=sys.stderr)


if phase == 'eligibility':
    payload = eligibility()
    print(json.dumps({'prId': str(pr), 'status': payload.get('status'),
                      'isDraft': payload.get('isDraft'),
                      'createdAt': payload.get('creationDate'),
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
            slot_not_idle('Native slot has an unreleased reviewd owner.')
        if state.prepared_slot_path(pool.repo_key, slot).exists() or writable.load_pool_run(pool.repo_key, slot, dry_run=True) is not None:
            slot_not_idle('Native slot retains an owner; explicit owner cleanup is required.')
        if outer.exists():
            admit_baseline(lambda: _verify_postconditions(writable, pool, outer, source,
                                                          run_git_read(root, ['rev-parse', 'HEAD']).stdout.strip(),
                                                          pool._verified_baseline()))
        atomic_json(marker, {'claimToken': args['claimToken'], 'pr': pr, 'state': 'owned'})
        clear_stale_index_locks()
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
        # An interrupted preparation can leave an orphaned 'owned' marker whose claimToken belongs to
        # a dead attempt (e.g. the batch process was killed between claiming the slot and finishing the
        # fetch). prepare then refuses the slot ('unreleased reviewd owner') and this cleanup refused it
        # too ('Native claim changed'), so the slot stayed blocked forever -- violating the
        # slot-never-blocks invariant. Under slot_lock (serialised against any live prepare), a marker
        # with no prepared slot and no pool run is a provable orphan, so a failed-attempt reset may
        # reclaim it. The prepared_slot/pool_run guard below still refuses to steal a genuinely prepared
        # predecessor, and lock serialisation guarantees we never race a live in-progress prepare.
        orphan_owned = (previous is not None and previous.get('state') == 'owned'
                        and previous.get('claimToken') != args['claimToken'])
        # A preparation that failed before claiming leaves the marker 'released' under an older claim:
        # natively the slot is already free, so only the SQLite owner is stale. A failed-attempt reset or
        # a reaper discard may adopt it under the same no-prepared-slot/no-pool-run guard.
        released_other = (previous is not None and previous.get('state') == 'released'
                          and previous.get('claimToken') != args['claimToken'])
        if ((args.get('failedPreparation') and (previous is None or orphan_owned))
                or ((args.get('failedPreparation') or args.get('discard')) and released_other)):
            if state.prepared_slot_path(pool.repo_key, slot).exists() or writable.load_pool_run(pool.repo_key, slot, dry_run=True) is not None:
                raise ReviewError('Unclaimed native predecessor remains; no failed-attempt reset allowed.')
            # A discard resets the slot to baseline below, so only a non-discarding adoption must
            # already be clean.
            if not args.get('discard'):
                _verify_postconditions(writable, pool, outer, source,
                                       run_git_read(root, ['rev-parse', 'HEAD']).stdout.strip(), pool._verified_baseline())
            previous = {'claimToken': args['claimToken'], 'pr': pr, 'state': 'owned'}
            atomic_json(marker, previous)
        if not previous or previous.get('claimToken') != args['claimToken'] or previous.get('pr') != pr or previous.get('state') != 'owned':
            raise ReviewError('Native claim changed; no cleanup allowed.')
        clear_stale_index_locks()
        prepared_path = state.prepared_slot_path(pool.repo_key, slot)
        if prepared_path.exists() and json.loads(prepared_path.read_text()).get('pr') != pr:
            raise ReviewError('Native prepared owner changed; no reset allowed.')
        discard = bool(args.get('discard'))
        if not discard and (git_status(source, read_only=True) or git_ignored_paths(source, read_only=True)):
            raise ReviewError('Source checkout has local data; no automatic destructive cleanup allowed.')
        # Preserve all outside-source attempt artifacts with native hash/fsync verification before reset.
        # A discard reset (a dead conversation its owner has frozen, being thrown away to reclaim the slot)
        # keeps nothing: skip preservation and the clean-source guard, exactly like the post-checkout reset.
        preserved = None
        if not discard:
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
            elif discard or args.get('failedPreparation'):
                # No manifest to replay: hard-reset this slot's outer and source worktrees to the idle
                # baseline, exactly as a prepared-slot discard does, so a parked or half-prepared slot
                # can always be thrown away.
                from review_git_prepare import _prepare_outer, _prepare_nested
                pool._ensure_store()
                _prepare_outer(writable, outer, run_git_read(root, ['rev-parse', 'HEAD']).stdout.strip())
                _prepare_nested(writable, pool, source, pool._verified_baseline())
                result = {'status': 'reset-to-baseline'}
            elif args.get('adopt'):
                result = {'status': 'already-idle'}
            else:
                raise ReviewError('Native owner disappeared; inspect before release.')
            _verify_postconditions(writable, pool, outer, source,
                                   run_git_read(root, ['rev-parse', 'HEAD']).stdout.strip(), pool._verified_baseline())
            previous['state'] = 'released'
            atomic_json(marker, previous)
            print(json.dumps({'claimToken': args['claimToken'], 'cleanBaselineVerified': True,
                              'evidencePreserved': preserved is not None, 'preservation': preserved, 'reset': result}))
elif phase == 'verify':
    inspect_prepared()
    receipt = json.loads(safe_path(outer, directory + '/publication_receipts.json').read_text())
    if (str(receipt.get('prId')) != str(pr) or receipt.get('headSha') != args['head']
            or receipt.get('mergeSha') != args['merge']):
        raise ReviewError('Publication receipt does not identify the prepared PR revision.')
    comments = receipt.get('comments')
    if not isinstance(comments, list) or not comments:
        raise ReviewError('Publication receipt must include a posted summary.')
    payload, _ = live()
    api = (quote(payload['repository']['project']['id'], safe='') + '/_apis/git/repositories/'
           + quote(payload['repository']['id'], safe='') + '/pullRequests/' + str(pr))
    verified, seen, summarized = [], set(), False
    summary_threads = set(args.get('summaryThreads') or [])
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
        summarized = summarized or comment.get('kind') == 'summary' or (
            comment.get('kind') == 'reply' and summary_reply(thread, posted, summary_threads))
        verified.append({'threadId': thread_id, 'commentId': comment_id, 'kind': comment.get('kind'),
                         'contentSha256': hashlib.sha256(posted['content'].encode()).hexdigest()})
    if not summarized:
        raise ReviewError('Publication receipt must include a posted summary.')
    print(json.dumps({'comments': verified}))
elif phase == 'commit':
    inspect_prepared()
    clear_stale_index_locks()
    from review_commit import commit_review
    # A reviewer that runs git inside its evidence leaves a .git there, and review_commit refuses any .git path
    # part, so the finished review could never commit. Move the whole nested repository out, not just its .git:
    # its working tree may be a clone of the source, and commit_review adds everything left in the folder.
    folder = safe_path(outer, directory)
    quarantine = Path('/tmp/review-quarantine') / f"{repo_slug(repo)}-{pr}-{args['head'][:8]}"
    for current, dirs, files in os.walk(folder):
        if '.git' not in dirs + files:
            continue
        nested = Path(current) if Path(current) != folder else Path(current) / '.git'
        relative = nested.relative_to(folder).as_posix()
        target = quarantine / relative.replace('/', '__')
        suffix = 0
        while target.exists() or target.is_symlink():
            suffix += 1
            target = quarantine / f"{relative.replace('/', '__')}.{suffix}"
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(nested), str(target))
        print(f'Quarantined {directory}/{relative} to {target}', file=sys.stderr)
        dirs[:] = [] if nested == Path(current) else [d for d in dirs if d != '.git']
    # An earlier review of this PR, in any slot, leaves review/<repo>-<pr> in the shared review repository, and a
    # detached slot refuses to switch to or overwrite it. Retain this head's review on its own branch instead.
    expected = None
    branch = f'review/{repo_slug(repo)}-{pr}'
    if (run_git_read(outer, ['symbolic-ref', '-q', 'HEAD'], check=False).returncode
            and not run_git_read(outer, ['rev-parse', '-q', '--verify', 'refs/heads/' + branch], check=False).returncode):
        expected = f"{branch}-{args['head'][:8]}"
    print(json.dumps(commit_review(root, repo, pr, slot, source_sha=args['head'], expected_branch=expected)))
else:
    raise ReviewError('Unknown review-runner sandbox phase.')
