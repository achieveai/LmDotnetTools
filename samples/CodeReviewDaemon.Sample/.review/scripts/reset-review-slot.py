"""Exact-assignment operator reset. No polling, network fetch, or history deletion."""
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys

sys.path.insert(0, str(Path.cwd() / '.claude/skills/review-setup/scripts'))
from review_common import (ReviewState, SourcePool, lock_session, run_git_read,
                           validate_relative_worktree, read_submodules, worktree_rows,
                           assert_no_git_operation, slot_writer_scope,
                           _slot_writer_assert_quiescent)
from review_git_prepare import _find_worktree_row, _worktree_functional
from review_pool_admin import select_module_by_name, _pool_status
from review_reset import _discard_prepared_slot

request = json.loads(sys.argv[1])
apply = request.pop('apply')
identity = request
root = Path.cwd().resolve()
state = ReviewState(root, read_only=not apply)
pool = SourcePool(state, select_module_by_name(root, request['repository']))
number = request['slot']


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


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
    fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def create_file(path, data):
    plain_path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    plain_path(path)
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600), 'wb') as handle:
        handle.write(data)
        handle.flush()
        os.fsync(handle.fileno())
    for directory in [path.parent, *path.parent.parents]:
        sync_directory(directory)
        if directory == state.directory:
            break


def create_json(path, value):
    create_file(path, json.dumps(value, sort_keys=True).encode())


require(type(apply) is bool and re.fullmatch(r'[a-f0-9]{32}', request['assignmentId']), 'Invalid reset request')
require(str(request['pr']).isdigit() and int(request['pr']) > 0, 'Invalid PR')
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
        require(not git(path, 'ls-files', '--others', '--ignored', '--exclude-standard', '-z'), 'Ignored files refused')
    for module in read_submodules(root, read_only=True):
        if module['path'] != pool.module['path']:
            other = outer / module['path']
            plain_path(other)
            require(not other.exists() or (other.is_dir() and not any(other.iterdir())), 'Other source material refused')
    require(not git(outer, 'diff', '--name-only', '--ignore-submodules=all')
            and not git(outer, 'diff', '--cached', '--name-only', '--ignore-submodules=all'), 'Tracked outer changes refused')
    require(not git(source, 'status', '--porcelain'), 'Source changes refused')
    require(git(outer, 'rev-parse', 'HEAD').strip() == git(root, 'rev-parse', 'HEAD').strip(), 'Outer baseline changed')
    baseline = pool.cached_baseline_sha_read_only()
    require(baseline, 'Cached source baseline is missing')
    return baseline


def inventory():
    paths = sorted(filter(None, git(outer, 'ls-files', '--others', '--exclude-standard', '-z').split('\0')))
    require(len(paths) <= 256, 'Too many residual files')
    rows, total = [], 0
    for relative in paths:
        path = outer / relative
        require(relative.startswith('PRs/' + str(request['pr']) + '/')
                and '..' not in Path(relative).parts, 'Foreign residual file refused')
        data = read_regular(path)
        total += len(data)
        require(total <= 16 * 1024 * 1024, 'Recovery size limit exceeded')
        rows.append({'path': relative, 'sha256': hashlib.sha256(data).hexdigest(), 'size': len(data)})
    # Git can collapse an embedded repository into one entry; read_regular refuses it.
    # Also reject links/special files in directories that clean would traverse.
    for directory, dirs, files in os.walk(outer, followlinks=False):
        if Path(directory) == outer:
            dirs[:] = [name for name in dirs if name != 'repos']
        for name in dirs + files:
            path = Path(directory) / name
            require(not path.is_symlink(), 'Outer symlink refused')
            require(path.is_dir() or path.is_file(), 'Special outer file refused')
    return rows


def verify_copy(rows):
    for row in rows:
        data = read_regular(recovery / 'files' / row['path'])
        require(len(data) == row['size'] and hashlib.sha256(data).hexdigest() == row['sha256'], 'Recovery hash mismatch')


def warm():
    baseline = topology()
    require(not manifest_path.exists() and git(source, 'rev-parse', 'HEAD').strip() == baseline,
            'Slot still prepared or not at baseline')
    require(not inventory() and _pool_status(root, pool.module, number)['status'] == 'warm', 'Slot is not clean/warm')


with lock_session(1200), state.lock(shared=True, phase='daemon-reset'), state.slot_lock(pool.repo_key, number, phase='daemon-reset'):
    _slot_writer_assert_quiescent(state, pool.repo_key, number, reap_stale=False, dry_run=True)
    if receipt_path.exists():
        receipt = json.loads(read_regular(receipt_path))
        require(receipt.get('schema') == 1 and receipt.get('status') == 'reset'
                and receipt.get('identity') == identity, 'Foreign reset receipt')
        verify_copy(receipt['inventory'])
        warm()
        print(json.dumps(receipt))
    else:
        require(not intent_path.exists(), 'Prior reset outcome is uncertain; preserve ownership for reconciliation')
        with pool.store_lock(shared=True, phase='daemon-reset-inspect'):
            topology()
            manifest = json.loads(read_regular(manifest_path))
            require(manifest.get('schema') == 1 and manifest.get('repo') == request['repository']
                    and manifest.get('slot') == number and manifest.get('pr') == int(request['pr'])
                    and manifest.get('merge_sha') == request['merge'], 'Prepared identity mismatch')
            require(git(source, 'rev-parse', 'HEAD').strip() == request['merge'], 'Prepared merge changed')
            require(git(source, 'show', '-s', '--format=%P', request['merge']).split()
                    == [request['base'], request['head']], 'Merge parent identity mismatch')
            rows = inventory()
        result = {'schema': 1, 'status': 'inspected', 'identity': identity, 'inventory': rows,
                  'recoveryPath': recovery.relative_to(root).as_posix()}
        if apply:
            require(not recovery.exists(), 'Existing incomplete recovery directory requires inspection')
            with slot_writer_scope(state, pool.repo_key, number, phase='daemon-reset', reap_stale=False,
                                   requested_pr=int(request['pr'])):
                for row in rows:
                    create_file(recovery / 'files' / row['path'], read_regular(outer / row['path']))
                verify_copy(rows)
                require(inventory() == rows, 'Residual inventory changed during preservation')
                create_json(intent_path, result)
                _discard_prepared_slot(state, pool, number, confirm='YES')
                warm()
                result['status'] = 'reset'
                create_json(receipt_path, result)
        print(json.dumps(result))
