#!/usr/bin/env python3
"""Archive and clean one completed review. Inspect by default; never starts a review."""
import argparse
import base64
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import uuid

SAMPLE = Path(__file__).resolve().parents[1]


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def save(path, data):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    with path.open('xb') as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())


def save_json(path, value):
    save(path, json.dumps(value, indent=2).encode())


def sync_tree(root):
    for p in root.rglob('*'):
        require(not p.is_symlink(), 'Archive symlink refused')
        if p.is_file():
            with p.open('rb') as f:
                os.fsync(f.fileno())
    dirs = sorted((p for p in root.rglob('*') if p.is_dir()), key=lambda p: len(p.parts), reverse=True)
    for p in [*dirs, root, root.parent]:
        fd = os.open(p, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class Api:
    def __init__(self, base, headers):
        self.base, self.headers = base.rstrip('/'), headers
        self.opener = urllib.request.build_opener(NoRedirect)

    def call(self, method, path, data=None, raw=False):
        req = urllib.request.Request(self.base + path, method=method, headers=self.headers,
                                     data=None if data is None else json.dumps(data).encode())
        with self.opener.open(req, timeout=120) as response:
            data = response.read(32 * 1024 * 1024 + 1)
        require(len(data) <= 32 * 1024 * 1024, 'HTTP response exceeds bound')
        return data if raw else json.loads(data)


class Lease:
    """Use the same .NET file-sharing lock as the daemon, not a second lock protocol."""
    def __init__(self, paths):
        script = '''$ErrorActionPreference='Stop'; $handles=@(); try {
foreach ($p in ($env:REVIEW_CLEANUP_LOCKS | ConvertFrom-Json)) {
$handles += [IO.FileStream]::new($p,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
}; [Console]::WriteLine('LOCKED'); [Console]::Out.Flush(); [Console]::ReadLine() | Out-Null
} finally { foreach ($h in $handles) { $h.Dispose() } }'''
        env = os.environ.copy(); env['REVIEW_CLEANUP_LOCKS'] = json.dumps([str(p) for p in paths])
        self.process = subprocess.Popen(['pwsh', '-NoProfile', '-Command', script], env=env,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        require(self.process.stdout.readline().strip() == 'LOCKED', 'Coordinator or retention lease unavailable')

    def release(self):
        if self.process.poll() is None:
            self.process.stdin.write('release\n'); self.process.stdin.flush()
            require(self.process.wait(timeout=10) == 0, 'Lease holder failed')


def git(root, *args, binary=False):
    result = subprocess.run(['git', '--no-optional-locks', '-C', str(root), '-c', 'credential.helper=',
                             '-c', 'credential.helper=!gh auth git-credential', *args], capture_output=True)
    require(result.returncode == 0, 'Git operation failed: ' + args[0] + ' (inspect repository manually)')
    return result.stdout if binary else result.stdout.decode().strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', required=True, help='Configured repository name or normalized key')
    parser.add_argument('--pr', required=True)
    parser.add_argument('--profile', default='nova', help='appsettings.<profile>.json (default: nova)')
    parser.add_argument('--archive-root', type=Path, default=Path.home() / 'review-comparison-archives')
    parser.add_argument('--key-file', type=Path, default=Path.home() / 'Gateway/secrets/app-secrets')
    parser.add_argument('--session-file', type=Path, help='Optional existing Gateway operator session')
    parser.add_argument('--binary', type=Path, default=SAMPLE / 'bin/Debug/net9.0/CodeReviewDaemon.Sample')
    parser.add_argument('--apply', action='store_true', help='Archive, verify, then clean this exact review')
    args = parser.parse_args()
    require(re.fullmatch(r'[A-Za-z0-9_-]+', args.profile), 'Invalid profile name')
    require(re.fullmatch(r'[1-9][0-9]*', args.pr), 'PR must be a positive number')
    os.umask(0o077)
    config = json.loads((SAMPLE / f'appsettings.{args.profile}.json').read_text())
    options = config['CodeReviewDaemon']; gateway_config = config['SandboxGateway']
    require(all(options.get(k) is False for k in ('EnablePrPolling', 'EnableCommentPosting', 'EnableGitPush')),
            'Cleanup requires explicit disabled polling and broad publication gates')
    database = Path(options['DatabasePath']).resolve()
    state = Path(options.get('WorkflowStateDirectory') or str(database) + '.workflow').resolve()
    store_url = options.get('CrossRepoStoreUrl') or options.get('ReviewBotRepoUrl')
    require(store_url and urllib.parse.urlsplit(store_url).scheme == 'https' and
            not urllib.parse.urlsplit(store_url).username, 'Expected HTTPS artifact store without embedded credentials')
    binary = args.binary.resolve(); require(binary.is_file(), 'Build the sample first; binary missing')
    host_root = Path(options.get('WorkspaceHostRoot') or binary.parent / 'workspaces')
    if not host_root.is_absolute(): host_root = binary.parent / host_root
    retention = host_root / ('retention-' + digest(store_url.encode()).upper())
    require((retention / '.git').exists(), 'Host retention checkout missing')
    require(git(retention, 'remote', 'get-url', 'origin') == store_url, 'Retention origin differs from profile')
    archive_root = args.archive_root.expanduser().resolve()
    require(not any(archive_root.is_relative_to(p) for p in (SAMPLE.parents[1], database.parent, state, host_root.resolve())),
            'Archive must be outside source checkout and daemon storage')
    db = sqlite3.connect('file:' + str(database) + '?mode=ro', uri=True); db.row_factory = sqlite3.Row
    lease = Lease([str(database) + '.coordinator.lock', str(retention) + '.retention.lock'])
    mutation = False; archive = None
    try:
        repos = db.execute('select * from repo where lower(repo_name)=lower(?) or lower(display_name)=lower(?) or lower(normalized_key)=lower(?)',
                           (args.repo, args.repo, args.repo)).fetchall()
        require(len(repos) == 1, 'Repository missing or ambiguous')
        repo = dict(repos[0])
        row = db.execute('select * from review_run where repo_id=? and pr_id=? order by id desc limit 1',
                         (repo['id'], args.pr)).fetchone()
        require(row is not None, 'No recorded review for repository and PR')
        run = dict(row); run_id = run['id']
        require(run['workflow_status'] == 'Completed' and run['mode'] == 'collect-only', 'Latest run is not completed collect-only')
        require(not db.execute('select * from active_workflow_workspace').fetchall(), 'Active workspace assignments remain')

        def latest(kind):
            row = db.execute('select payload from review_artifact where review_run_id=? and artifact_kind=? order by id desc limit 1',
                             (run_id, kind)).fetchone()
            require(row is not None, 'Required evidence missing: ' + kind)
            return json.loads(row[0])

        branch = latest('review-artifact-branch'); assignment = latest('workflow-workspace-assignment')
        prepared = latest('workflow-workspace-prepared'); slot = assignment['Slot']
        instance = f'review-run-{run_id}'
        require(branch['RepoKey'] == repo['normalized_key'] and branch['PrId'] == args.pr and
                branch['HeadSha'] == run['head_sha'] and branch['WorkflowInstanceId'] == instance, 'Branch ownership mismatch')
        slug = ''.join(c if c.isalnum() or c in '._' else '-' for c in repo['repo_name'].lower()).strip('-')
        require(branch['Branch'] == f'review/{slug}-{args.pr}' and
                re.fullmatch(r'[0-9a-f]{40}', branch['PushedSha']), 'Invalid branch receipt')
        require(not assignment['Active'] and assignment['WorkflowInstanceId'] == instance and
                prepared['AssignmentId'] == assignment['AssignmentId'] and prepared['Slot'] == slot and
                slot['RepositoryName'].lower() == repo['repo_name'].lower() and
                prepared['Checkout']['SourceHeadSha'] == run['head_sha'] and
                prepared['Checkout']['TargetBaseSha'] == run['base_sha'], 'Assignment ownership mismatch')
        identity = dict(runId=run_id, assignmentId=assignment['AssignmentId'], workflowInstanceId=instance,
                        repository=slot['RepositoryName'], slot=slot['Index'], pr=args.pr, head=run['head_sha'],
                        base=run['base_sha'], merge=prepared['Checkout']['CheckoutSha'], sourcePath=slot['SourceRelativePath'])
        run_dir = state / 'runs' / digest(instance.encode()).upper()
        snapshot_path = state / 'snapshots' / (digest(instance.encode()).upper() + '.json')
        snapshot = json.loads(snapshot_path.read_text())
        require(snapshot['isComplete'] and all(t['status'] == 'validated' for t in snapshot['tasks']), 'Workflow not settled')
        history_hashes = {str(p): digest(p.read_bytes()) for p in [snapshot_path, *run_dir.rglob('*')] if p.is_file()}
        outbox = [dict(r) for r in db.execute('select * from review_outbox where review_run_id=?', (run_id,))]
        require(all(r['status'] in ('Posted', 'Sent', 'Collected') for r in outbox), 'Unresolved or already-invalidated outbox; reconcile first')
        retention_rows = [r for r in outbox if r['operation'] == 'workflow_retain_artifacts']
        owner_suffix = f":workflow-artifacts:{instance}:{run['head_sha']}:{run['variant_id']}"
        require(len([r for r in retention_rows if r['idempotency_key'].endswith(owner_suffix)]) == 1 and
                all(r['status'] == 'Posted' and r['provider_response_id'] == branch['PushedSha'] for r in retention_rows),
                'Retention receipt missing, ambiguous, or at another SHA')
        require(not db.execute("select 1 from review_artifact where review_run_id=? and artifact_kind in ('review-artifact-branch-quarantine','review-artifact-branch-redo')", (run_id,)).fetchone(), 'Prior deletion or quarantine; reconcile first')
        ref = 'refs/heads/' + branch['Branch']
        require(git(retention, 'ls-remote', '--refs', 'origin', ref) == branch['PushedSha'] + '\t' + ref, 'Remote branch moved or absent')
        require(git(retention, 'status', '--porcelain', '--untracked-files=all') == '', 'Retention checkout has unrelated changes')
        local_ref = git(retention, 'for-each-ref', '--format=%(objectname)', ref)
        require(not local_ref or local_ref == branch['PushedSha'], 'Local review branch diverged')
        require(len(git(retention, 'worktree', 'list', '--porcelain').split('worktree ')) == 2, 'Additional retention worktrees need manual inspection')
        app_id = gateway_config['AppId']
        key = os.environ.get('CRD_SANDBOX_APP_KEY')
        if not key:
            with os.fdopen(os.open(args.key_file, os.O_RDONLY | os.O_NOFOLLOW)) as f:
                key = json.load(f)[app_id]
        headers = {'X-Sbx-App-Id': app_id, 'X-Sbx-App-Key': key, 'Content-Type': 'application/json'}
        host_env = {}
        for proc in Path('/proc').iterdir():
            try:
                if proc.name.isdigit() and os.readlink(proc / 'exe').endswith('/LmStreaming.Sample'):
                    require(not host_env, 'Multiple review hosts; provide an unambiguous environment')
                    host_env = dict(x.split(b'=', 1) for x in (proc / 'environ').read_bytes().split(b'\0') if b'=' in x)
            except OSError:
                pass
        if host_env.get(b'LMSTREAMING_S2S_INBOUND_SECRET'):
            headers['X-S2S-Auth'] = host_env[b'LMSTREAMING_S2S_INBOUND_SECRET'].decode()
        host = Api(options['LmStreamingBaseUrl'], headers)
        gateway = Api(gateway_config['BaseUrl'].rstrip('/') + '/api/v1', headers)
        threads = set()
        for sid in snapshot['sessions'].values():
            binding = latest('workflow-session:' + digest(sid.encode()).upper())
            require(binding['SessionId'] == sid and binding['WorkspaceId'] == prepared['Workspace']['WorkspaceId'] and
                    binding['WorkingDirectoryRelPath'] == slot['WorktreeRelativePath'], 'Session binding mismatch')
            threads.add(binding['ThreadId'])

        def idle():
            for thread in threads:
                path = '/api/conversations/' + urllib.parse.quote(thread, safe='')
                require(host.call('GET', path + '/run-state')['isInProgress'] is False, 'Reviewer active')
                graph = host.call('GET', path + '/subagents?recursive=true')
                require(graph['schemaVersion'] == 1, 'Unknown subagent schema')
                for node in graph['nodes']:
                    require(node['status'].lower() == 'completed' and host.call('GET', '/api/conversations/' +
                            urllib.parse.quote(node['threadId'], safe='') + '/run-state')['isInProgress'] is False, 'Child reviewer active')
                require(host.call('GET', path + '/run-state')['isInProgress'] is False, 'Reviewer restarted')
        idle()
        session = json.loads(args.session_file.read_text()) if args.session_file else gateway.call('POST', '/sandboxes',
                    {'app': {'id': app_id}, 'workspace': prepared['Workspace']['Leaf'], 'pluginSelection': [], 'home': slot['WorktreeRelativePath']})
        sid = session['session_id']; mount = session['volumes']['workspace']['id']

        def remote(script, request):
            op = 'cleanup-' + uuid.uuid4().hex
            status = gateway.call('POST', f'/sandboxes/{sid}/operations', {'operation_id': op, 'executable': 'python3',
                    'args': ['-c', script, json.dumps(request)], 'cwd': {'mount_id': mount, 'path': '.'},
                    'timeout_secs': 1200, 'max_output_bytes': 4 * 1024 * 1024})
            deadline = time.monotonic() + 1230
            while status['status'] == 'running':
                require(time.monotonic() < deadline, 'Remote outcome unknown: ' + op)
                time.sleep(2); status = gateway.call('GET', f'/sandboxes/{sid}/operations/{op}')
            require(status.get('exit_code') == 0, 'Remote operation failed; inspect operation ' + op)
            artifacts = status['artifacts']
            data = gateway.call('GET', f"/sandboxes/{sid}/files/{artifacts['mount_id']}?path=" +
                                urllib.parse.quote(artifacts['stdout_path'], safe=''), raw=True)
            return json.loads(data)

        reset = (SAMPLE / '.review/scripts/reset-review-slot.py').read_text()
        inspection = remote(reset, {**identity, 'apply': False})
        require(inspection['status'] == 'inspected' and inspection['identity'] == identity, 'Unexpected reset state')
        print(json.dumps({'repo': repo['display_name'], 'pr': args.pr, 'run': run_id, 'branch': branch['Branch'],
                          'sha': branch['PushedSha'], 'slot': slot['Name'], 'workingFiles': len(inspection['inventory']), 'apply': args.apply}), flush=True)
        if not args.apply:
            return
        archive = archive_root / f"{re.sub(r'[^A-Za-z0-9._-]', '-', repo['normalized_key'])}-pr-{args.pr}-run-{run_id}"
        require(not archive.exists(), 'Archive already exists; inspect prior outcome, do not replay')
        archive.mkdir(parents=True, mode=0o700)
        save_json(archive / 'identity.json', {'repo': repo, 'run': run, 'branch': branch, 'inspection': inspection, 'outbox': outbox})
        bare = archive / 'branch.git'
        git(archive, 'init', '--bare', str(bare))
        git(bare, 'fetch', '--depth=1', '--no-tags', store_url, ref + ':' + ref)
        require(git(bare, 'rev-parse', ref) == branch['PushedSha'], 'Archive branch changed')
        git(bare, 'fsck', '--full')
        bundle = json.loads((run_dir / 'retention-bundle.json').read_text())
        require(bundle['ReviewRunId'] == run_id and bundle['WorkflowInstanceId'] == instance, 'Bundle identity mismatch')
        for item in bundle['Files']:
            name = item['RelativePath']; require(name.startswith('PRs/' + branch['Branch'][7:] + '/') and '..' not in Path(name).parts, 'Foreign bundle path')
            data = git(bare, 'show', branch['PushedSha'] + ':' + name, binary=True)
            require(data == item['Content'].encode(), 'Published artifact differs from bundle')
            save(archive / 'published' / name, data)
        export = '''import sys,json,base64,hashlib;from pathlib import Path
r=json.loads(sys.argv[1]);out={}
for f in r['files']:
 p=Path(r['root'])/f['path']
 if p.is_symlink(): raise RuntimeError('Symlink refused')
 b=p.read_bytes()
 if len(b)!=f['size'] or hashlib.sha256(b).hexdigest()!=f['sha256']: raise RuntimeError('File changed')
 out[f['path']]=base64.b64encode(b).decode()
print(json.dumps(out))'''
        contents = remote(export, {'root': slot['WorktreeRoot'], 'files': inspection['inventory']})
        for row in inspection['inventory']:
            data = base64.b64decode(contents[row['path']]); require(digest(data) == row['sha256'], 'Working file archive mismatch')
            save(archive / 'slot-files' / row['path'], data)
        shutil.copytree(run_dir, archive / 'workflow-run'); shutil.copy2(snapshot_path, archive / 'workflow-snapshot.json')
        save_json(archive / 'archive-hashes.json', {str(p.relative_to(archive)): digest(p.read_bytes()) for p in archive.rglob('*') if p.is_file()})
        sync_tree(archive); idle()
        require(remote(reset, {**identity, 'apply': False}) == inspection, 'Slot inventory changed')
        save_json(archive / 'cleanup-intent.json', {'identity': identity, 'sha': branch['PushedSha']}); sync_tree(archive)
        mutation = True
        result = remote(reset, {**identity, 'apply': True})
        require(result['status'] == 'reset' and result['identity'] == identity and result['inventory'] == inspection['inventory'], 'Reset outcome mismatch')
        save_json(archive / 'slot-reset.json', result)
        require(remote(reset, {**identity, 'apply': False}) == result, 'Slot not warm after reset')
        prefix = reset.split('with lock_session(1200),')[0]
        purge = prefix + '''
with lock_session(1200), state.lock(shared=True, phase='operator-archive'), state.slot_lock(pool.repo_key, number, phase='operator-archive'):
 _slot_writer_assert_quiescent(state,pool.repo_key,number,reap_stale=False,dry_run=True)
 receipt=json.loads(read_regular(receipt_path))
 require(receipt['status']=='reset' and receipt['identity']==identity,'Receipt mismatch')
 verify_copy(receipt['inventory']);warm()
 allowed={'receipt.json','intent.json'}|{'files/'+r['path'] for r in receipt['inventory']}
 actual=set()
 for p in recovery.rglob('*'):
  plain_path(p);require(p.is_file() or p.is_dir(),'Special recovery file')
  if p.is_file():actual.add(p.relative_to(recovery).as_posix())
 require(actual==allowed,'Foreign recovery files')
 import base64
 data={n:base64.b64encode(read_regular(recovery/n)).decode() for n in sorted(actual)}
 print(json.dumps(data))
'''
        recovery_files = remote(purge, {**identity, 'apply': False})
        for name, text in recovery_files.items(): save(archive / 'reset-recovery' / name, base64.b64decode(text))
        recovery_hashes = {name: digest(base64.b64decode(text)) for name, text in recovery_files.items()}
        save_json(archive / 'recovery-hashes.json', recovery_hashes); sync_tree(archive)
        purge = prefix + '\nexpected=' + repr(recovery_hashes) + '''
with lock_session(1200), state.lock(shared=True,phase='operator-purge'), state.slot_lock(pool.repo_key,number,phase='operator-purge'):
 _slot_writer_assert_quiescent(state,pool.repo_key,number,reap_stale=False,dry_run=True);warm()
 require({p.relative_to(recovery).as_posix() for p in recovery.rglob('*') if p.is_file()}==set(expected),'Recovery inventory changed')
 for n,h in expected.items():require(hashlib.sha256(read_regular(recovery/n)).hexdigest()==h,'Recovery changed')
 for n in expected:(recovery/n).unlink()
 for p in sorted(recovery.rglob('*'),key=lambda p:len(p.parts),reverse=True):p.rmdir()
 recovery.rmdir();sync_directory(recovery.parent);warm()
 print(json.dumps({'warm':True,'recoveryAbsent':not recovery.exists(),'notesAbsent':not (outer/'PRs'/str(request['pr'])).exists()}))
'''
        purged = remote(purge, {**identity, 'apply': True})
        require(all(purged.values()), 'Residual review notes or recovery files remain')
        save_json(archive / 'slot-cleaned.json', {'warm': True, 'recoveryPurged': inspection['recoveryPath']}); sync_tree(archive)
        require(git(retention, 'status', '--porcelain', '--untracked-files=all') == '', 'Retention checkout changed')
        if git(retention, 'rev-parse', 'HEAD') == branch['PushedSha']:
            default_ref = git(retention, 'symbolic-ref', 'refs/remotes/origin/HEAD')
            require(default_ref.startswith('refs/remotes/origin/') and not default_ref.startswith('refs/remotes/origin/review/'), 'Invalid retention default ref')
            git(retention, 'checkout', '--detach', default_ref)
        idle()
        lease.release()
        env = os.environ.copy()
        env.update({'CRD_SANDBOX_APP_ID': app_id, 'CRD_SANDBOX_APP_KEY': key,
                    'CodeReviewDaemon__EnablePrPolling': 'false', 'CodeReviewDaemon__EnableCommentPosting': 'false',
                    'CodeReviewDaemon__EnableGitPush': 'false'})
        callback = host_env.get(('WorkflowPublication__Callbacks__' + app_id + '__SharedSecret').encode())
        if callback: env['WorkflowPublication__SharedSecret'] = callback.decode()
        with (archive / 'redo-command.log').open('xb') as log:
            command = subprocess.run([str(binary), '--review', args.profile, '--redo-artifact-branch', repo['normalized_key'], args.pr],
                                     cwd=SAMPLE, env=env, stdout=log, stderr=log)
        lease = Lease([str(database) + '.coordinator.lock', str(retention) + '.retention.lock'])
        require(command.returncode == 0, 'Daemon redo failed; inspect local command log and quarantine state')
        require(git(retention, 'ls-remote', '--refs', 'origin', ref) == '', 'Remote branch still present')
        require(git(retention, 'for-each-ref', '--format=%(objectname)', ref) == '', 'Local retention branch still present')
        tracking = 'refs/remotes/origin/' + branch['Branch']
        old = git(retention, 'for-each-ref', '--format=%(objectname)', tracking)
        if old:
            require(old == branch['PushedSha'], 'Remote-tracking ref changed')
            git(retention, 'update-ref', '-d', tracking, old)
        redo = latest('review-artifact-branch-redo')
        require(redo['DeletedSha'] == branch['PushedSha'] and redo['Branch'] == branch['Branch'], 'Redo receipt mismatch')
        require(all(digest(Path(p).read_bytes()) == h for p,h in history_hashes.items()), 'Frozen workflow history changed')
        require(dict(db.execute('select * from review_run where id=?',(run_id,)).fetchone()) == run, 'Completed review row changed')
        save_json(archive / 'cleanup-completed.json', {'completedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  'branchAbsent': branch['Branch'], 'slot': slot['Name'], 'redo': redo, 'reviewHistoryPreserved': True})
        sync_tree(archive)
        mutation = False
        print(json.dumps({'completed': True, 'archive': str(archive), 'repo': args.repo, 'pr': args.pr}), flush=True)
    except BaseException:
        if mutation:
            print('BLOCKED: cleanup outcome requires reconciliation. Archive: ' + str(archive) + '; holding coordinator lease if still held.', file=sys.stderr, flush=True)
            while lease.process.poll() is None:
                time.sleep(60)
        raise
    finally:
        lease.release()


if __name__ == '__main__':
    main()
