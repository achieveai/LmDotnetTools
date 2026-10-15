"""Read immutable completion evidence through the installed SourcePool read locks.

Probe may acquire exact commits through the installed bounded source transport.
No checkout, reset, slot movement, or source-file execution is performed.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path('/workspace')
sys.path.insert(0, str(ROOT / '.claude/skills/review-setup/scripts'))
from review_common import (ReviewState, SourcePool, lock_session, run_git,
                           canonical_repo_name, read_submodules, bounded_fetch, fetch_session)

request = json.loads(sys.argv[1])
operation = sys.argv[2] if len(sys.argv) > 2 else 'evidence'
assert operation in ('evidence', 'file', 'tree', 'probe')
repository = request['Repository']
landed, parent = request['LandedSha'], request.get('ParentSha')
assert isinstance(landed, str) and re.fullmatch('[0-9a-f]{40}', landed)
if operation != 'probe':
    assert isinstance(parent, str) and re.fullmatch('[0-9a-f]{40}', parent)
limit = request['MaximumCharacters']
assert isinstance(limit, int) and not isinstance(limit, bool) and 0 < limit <= 8 * 1024 * 1024
acquire = request.get('Acquire', False)
assert isinstance(acquire, bool) and (not acquire or operation == 'probe')
timeout = request.get('AcquisitionTimeoutSeconds', 120)
assert isinstance(timeout, int) and not isinstance(timeout, bool) and 0 < timeout <= 300
state = ReviewState(ROOT, read_only=True)

def git(*args):
    result = run_git(pool.store, list(args), read_only=True)
    assert len(result.stdout) <= limit, 'complete object evidence exceeds limit'
    return result.stdout

with lock_session(1200), state.lock(shared=True, phase='merged-evidence'):
    modules = [module for module in read_submodules(ROOT, read_only=True)
               if canonical_repo_name(module).lower() == repository.lower()]
    assert len(modules) == 1, 'source module is unavailable or ambiguous'
    pool = SourcePool(state, modules[0])
    if acquire:
        with fetch_session(timeout, log=False) as budget:
            with pool.store_lock(shared=True, phase='merged-probe'):
                assert git('rev-parse', '--is-bare-repository').strip() == 'true'
                assert git('rev-parse', '--is-shallow-repository').strip() == 'false'
            # The installed transport takes source EX for import; never hold source SH here.
            result = bounded_fetch(pool.store, pool.module['url'], [landed], depth=2)
            assert result.returncode == 0, 'exact landed acquisition failed'
            with pool.store_lock(shared=True, phase='merged-probe'):
                budget.remaining()
                assert git('rev-parse', '--verify', landed + '^{commit}').strip() == landed
                parents = git('show', '-s', '--format=%P', landed).strip().split()
                assert len(parents) in (1, 2), 'landed commit requires a bounded parent list'
                parent = parents[0]
            # Exact-commit cache hits do not prove the parent object is also present.
            result = bounded_fetch(pool.store, pool.module['url'], [parent], depth=2)
            assert result.returncode == 0, 'exact first-parent acquisition failed'
            budget.remaining()
    with pool.store_lock(shared=True, phase='merged-evidence'):
        assert git('rev-parse', '--is-bare-repository').strip() == 'true'
        assert git('rev-parse', '--is-shallow-repository').strip() == 'false'
        assert git('rev-parse', '--verify', landed + '^{commit}').strip() == landed
        parents = git('show', '-s', '--format=%P', landed).strip().split()
        assert len(parents) in (1, 2), 'landed commit requires a bounded parent list'
        if operation == 'probe':
            parent = parents[0]
        assert parents[0] == parent, 'final first parent differs from admission'
        assert git('rev-parse', '--verify', parent + '^{commit}').strip() == parent
        if operation == 'probe':
            output = {'LandedSha': landed, 'ParentSha': parent, 'Repository': repository}
        elif operation == 'evidence':
            output = {
                'Kind': 'landed-objects', 'Repository': repository,
                'LandedSha': landed, 'ParentSha': parent, 'Parents': parents,
                'Diff': git('diff', '--no-ext-diff', '--no-textconv', '--unified=3', parent, landed, '--'),
                'SourceCheckout': 'not_used',
            }
        else:
            revision, path = sys.argv[3:]
            assert revision in (landed, parent), 'navigation must use an admitted revision'
            assert path and not path.startswith('/') and not any(
                part in ('', '.', '..') or '\\' in part or ':' in part
                or any(ord(c) < 32 for c in part) for part in path.split('/'))
            if operation == 'file':
                object_name = revision + ':' + path
                assert git('cat-file', '-t', object_name).strip() == 'blob'
                assert int(git('cat-file', '-s', object_name).strip()) <= limit
                output = {'Revision': revision, 'Path': path, 'Content': git('cat-file', 'blob', object_name)}
            else:
                output = {'Revision': revision, 'Path': path,
                          'Entries': git('ls-tree', '-z', revision, '--', path).split('\0')}
        encoded = json.dumps(output, ensure_ascii=True)
        assert len(encoded) <= limit, 'serialized evidence exceeds limit'
        print(encoded)
