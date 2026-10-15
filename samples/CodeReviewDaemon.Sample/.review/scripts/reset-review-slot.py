"""Exact selected-slot cleanup. No polling, fetch, publication or round mutation."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import sys

sys.path.insert(0, str(Path.cwd() / '.claude/skills/review-setup/scripts'))
from review_common import (ReviewState, SourcePool, lock_session, run_git, run_git_read,
                           validate_relative_worktree, read_submodules, worktree_rows,
                           assert_no_git_operation, slot_writer_scope,
                           _slot_writer_assert_quiescent)
from review_git_prepare import _find_worktree_row, _worktree_functional
from review_pool_admin import select_module_by_name, _pool_status

request = json.loads(sys.argv[1])
apply = request.pop('apply')
compact_output = request.pop('compactOutput', False)
if not isinstance(compact_output, bool):
    raise ValueError('compactOutput must be boolean')
identity = request
root = Path.cwd().resolve()
state = ReviewState(root, read_only=not apply)
pool = SourcePool(state, select_module_by_name(root, request['repository']))
number = request['slot']
output_roots = ('PRs', 'KnowledgeBase', 'DeveloperLearnings')


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def emit_result(result):
    if compact_output:
        rows = result['inventory']
        encoded = json.dumps(rows, sort_keys=True, separators=(',', ':'), ensure_ascii=True).encode('utf-8')
        result = {key: value for key, value in result.items() if key != 'inventory'}
        result.update(compactOutput=True, inventoryCount=len(rows),
                      inventorySha256=hashlib.sha256(encoded).hexdigest())
    print(json.dumps(result))


def git(path, *args):
    return run_git_read(path, list(args)).stdout


def plain_path(path):
    require(path.is_relative_to(root), 'Path escaped workspace')
    for part in [path, *path.parents]:
        if part == root:
            break
        require(not part.is_symlink(), 'Symlink refused')


def read_regular(path, limit=2 * 1024 * 1024):
    plain_path(path)
    with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW), 'rb') as handle:
        info = os.fstat(handle.fileno())
        require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_size <= limit,
                'Non-regular, linked or oversized recovery file')
        data = handle.read(limit + 1)
        require(len(data) <= limit, 'File grew beyond recovery limit')
        return data


def sync_directory(path):
    fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def create_json(path, value):
    plain_path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    plain_path(path)
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600), 'wb') as handle:
        handle.write(json.dumps(value, sort_keys=True).encode())
        handle.flush()
        os.fsync(handle.fileno())
    for directory in [path.parent, *path.parent.parents]:
        sync_directory(directory)
        if directory == state.directory:
            break


require(type(apply) is bool and re.fullmatch(r'[a-f0-9]{32}', request['assignmentId']), 'Invalid reset request')
require(type(number) is int and number >= 0, 'Invalid slot')
require(str(request['pr']).isdigit() and int(request['pr']) > 0, 'Invalid PR')
mode = request.get('mode', 'prepared')
require(mode in ('prepared', 'unprepared-baseline'), 'Unknown reset mode')
outer = root / pool.slot_relative_path(number)
source = outer / pool.module['path']
require(source.relative_to(root).as_posix() == request['sourcePath'], 'Source path mismatch')
recovery = state.directory / 'daemon-resets' / request['assignmentId']
plain_path(recovery)
manifest_path = state.prepared_slot_path(pool.repo_key, number)
receipt_path = recovery / 'receipt.json'
intent_path = recovery / 'intent.json'


def topology():
    require(not state.pool_run_path(pool.repo_key, number).exists(), 'Legacy pool-run binding refused')
    require(git(pool.store, 'rev-parse', '--is-bare-repository').strip() == 'true', 'Source store is not bare')
    for path, store, repository in [(outer, state.common, root), (source, pool.store, pool.store)]:
        plain_path(path)
        require((path / '.git').is_file(), 'Expected linked worktree')
        require(_find_worktree_row(worktree_rows(repository, read_only=True), path) is not None
                and _worktree_functional(path, store), 'Reset would enter a repair path')
        validate_relative_worktree(root, path, store)
        assert_no_git_operation(path)
        require(git(path, 'rev-parse', '--abbrev-ref', 'HEAD').strip() == 'HEAD', 'Attached branch refused')
    for module in read_submodules(root, read_only=True):
        if module['path'] != pool.module['path']:
            other = outer / module['path']
            plain_path(other)
            require(not other.exists() or (other.is_dir() and not any(other.iterdir())), 'Other source material refused')
    root_head = git(root, 'rev-parse', 'HEAD').strip()
    require(git(outer, 'rev-parse', 'HEAD').strip() == root_head, 'Outer baseline changed')
    baseline = pool.cached_baseline_sha_read_only()
    require(baseline, 'Cached source baseline is missing')
    return root_head, baseline


def inventory():
    # Inspect all selected-slot contents, including ignored files and empty dirs.
    # Never traverse Git administration or follow a symlink into another slot.
    device = outer.stat().st_dev
    rows = []
    for directory, dirs, files in os.walk(outer, followlinks=False):
        for name in list(dirs) + files:
            path = Path(directory) / name
            relative = path.relative_to(outer).as_posix()
            info = path.lstat()
            require(not stat.S_ISLNK(info.st_mode), 'Slot symlink refused')
            require(info.st_dev == device and not os.path.ismount(path), 'Nested mount refused')
            require(stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode), 'Special slot entry refused')
            if name == '.git':
                require(path in (outer / '.git', source / '.git') and stat.S_ISREG(info.st_mode),
                        'Unregistered nested repository refused')
                continue
            if stat.S_ISDIR(info.st_mode):
                rows.append({'path': relative, 'kind': 'directory'})
            else:
                require(info.st_nlink == 1, 'Hard-linked slot file refused')
                with path.open('rb') as handle:
                    digest = hashlib.file_digest(handle, 'sha256').hexdigest()
                rows.append({'path': relative, 'kind': 'file', 'sha256': digest, 'size': info.st_size})
    return sorted(rows, key=lambda row: row['path'])


def warm(root_head, baseline):
    require(topology() == (root_head, baseline), 'Reset baseline changed')
    require(not manifest_path.exists() and git(source, 'rev-parse', 'HEAD').strip() == baseline,
            'Slot still prepared or not at baseline')
    require(all(not (outer / name).exists() for name in output_roots), 'Old review output remains')
    require(not git(source, 'status', '--porcelain')
            and not git(source, 'ls-files', '--others', '--ignored', '--exclude-standard', '-z'),
            'Source is not fresh')
    require(_pool_status(root, pool.module, number)['status'] == 'warm', 'Slot is not clean/warm')


def clean(root_head, baseline):
    # Worktree-local exclusions survive the next prepare/reset without deleting
    # historical blobs from the shared store or changing a sibling checkout.
    run_git(outer, ['sparse-checkout', 'set', '--no-cone', '--no-sparse-index', '--stdin'],
            input_data=b'/*\n!/PRs/\n!/KnowledgeBase/\n!/DeveloperLearnings/\n')
    run_git(outer, ['reset', '--hard', root_head])
    run_git(outer, ['clean', '-ffdx', '-e', '/repos'])
    run_git(outer, ['sparse-checkout', 'reapply'])
    # Sparse checkout can leave modified excluded files and empty directories.
    # The prior inventory refuses links/mounts; rmtree uses no-follow dir fds.
    require(shutil.rmtree.avoids_symlink_attacks, 'Descriptor-anchored deletion is unavailable')
    with_fd = os.open(outer, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        for name in output_roots:
            if (outer / name).exists():
                shutil.rmtree(name, dir_fd=with_fd)
        os.fsync(with_fd)
    finally:
        os.close(with_fd)
    run_git(source, ['reset', '--hard', baseline])
    run_git(source, ['clean', '-ffdx'])
    require(topology() == (root_head, baseline), 'Reset baseline changed')
    manifest_path.unlink(missing_ok=True)
    if manifest_path.parent.exists():
        sync_directory(manifest_path.parent)
    warm(root_head, baseline)


with lock_session(1200), state.lock(shared=True, phase='daemon-reset'), state.slot_lock(pool.repo_key, number, phase='daemon-reset'):
    _slot_writer_assert_quiescent(state, pool.repo_key, number, reap_stale=False, dry_run=True)
    if receipt_path.exists():
        receipt = json.loads(read_regular(receipt_path, limit=32 * 1024 * 1024))
        require(receipt.get('schema') == 1 and receipt.get('cleanupVersion') == 2
                and receipt.get('status') == 'reset' and receipt.get('identity') == identity, 'Foreign or older reset receipt')
        warm(receipt['rootHead'], receipt['sourceBaseline'])
        emit_result(receipt)
    elif intent_path.exists():
        intent = json.loads(read_regular(intent_path, limit=32 * 1024 * 1024))
        require(intent.get('schema') == 1 and intent.get('cleanupVersion') == 2
                and intent.get('status') == 'inspected' and intent.get('identity') == identity,
                'Foreign or older reset intent')
        # A lost receipt is recoverable only after independently proving the
        # exact selected slot is already fresh. Never repeat uncertain deletion.
        with pool.store_lock(shared=True, phase='daemon-reset-reconcile'):
            warm(intent['rootHead'], intent['sourceBaseline'])
            if apply:
                with slot_writer_scope(state, pool.repo_key, number, phase='daemon-reset-reconcile',
                                       reap_stale=False, requested_pr=int(request['pr'])):
                    warm(intent['rootHead'], intent['sourceBaseline'])
                    intent['status'] = 'reset'
                    create_json(receipt_path, intent)
        emit_result(intent)
    else:
        with pool.store_lock(shared=True, phase='daemon-reset-inspect'):
            root_head, baseline = topology()
            if mode == 'unprepared-baseline':
                require(not manifest_path.exists(), 'Prepared slot refused in unprepared mode')
                require((not apply and request.get('rootHead') is None and request.get('sourceBaseline') is None)
                        or (request.get('rootHead') == root_head and request.get('sourceBaseline') == baseline),
                        'Unprepared baseline identity mismatch')
                require(git(source, 'rev-parse', 'HEAD').strip() == baseline, 'Unprepared source baseline changed')
            else:
                manifest = json.loads(read_regular(manifest_path))
                require(manifest.get('schema') == 1 and manifest.get('repo') == request['repository']
                        and manifest.get('slot') == number and manifest.get('pr') == int(request['pr'])
                        and manifest.get('merge_sha') == request['merge'], 'Prepared identity mismatch')
                require(git(source, 'rev-parse', 'HEAD').strip() == request['merge'], 'Prepared merge changed')
                require(git(source, 'show', '-s', '--format=%P', request['merge']).split()
                        == [request['base'], request['head']], 'Merge parent identity mismatch')
            rows = inventory()
            result = {'schema': 1, 'cleanupVersion': 2, 'status': 'inspected', 'identity': identity,
                      'rootHead': root_head, 'sourceBaseline': baseline, 'inventory': rows,
                      'recoveryPath': recovery.relative_to(root).as_posix()}
            if apply:
                require(not recovery.exists(), 'Existing incomplete reset requires inspection')
                with slot_writer_scope(state, pool.repo_key, number, phase='daemon-reset', reap_stale=False,
                                       requested_pr=int(request['pr'])):
                    require(inventory() == rows, 'Slot changed during inspection')
                    create_json(intent_path, result)
                    clean(root_head, baseline)
                    result['status'] = 'reset'
                    create_json(receipt_path, result)
        emit_result(result)
