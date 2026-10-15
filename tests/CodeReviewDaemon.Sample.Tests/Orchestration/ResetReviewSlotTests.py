"""Reset intent reconciliation contracts; no real daemon, provider or Git writes."""
from contextlib import nullcontext, redirect_stdout
import io
import json
import os
from pathlib import Path
import runpy
import sys
import tempfile
from types import ModuleType, SimpleNamespace
import unittest
from unittest.mock import Mock, patch

SCRIPT = Path(__file__).resolve().parents[3] / "samples/CodeReviewDaemon.Sample/.review/scripts/reset-review-slot.py"


class ResetReviewSlotTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.outer = self.root / '.worktrees/Nova-0'
        self.source = self.outer / 'repos/Nova'
        self.source.mkdir(parents=True)
        for path in (self.outer, self.source):
            (path / '.git').write_text('gitdir: fixture')
        self.identity = {'runId': 203, 'assignmentId': 'a' * 32,
                         'workflowInstanceId': 'review-run-203', 'repository': 'Nova',
                         'slot': 0, 'pr': '123', 'head': 'b' * 40, 'base': 'c' * 40,
                         'merge': 'd' * 40, 'sourcePath': '.worktrees/Nova-0/repos/Nova'}
        self.state = SimpleNamespace(
            directory=self.root / '.state', common=self.root / '.git',
            prepared_slot_path=lambda *args: self.root / '.state/prepared.json',
            pool_run_path=lambda *args: self.root / '.state/pool-run.json',
            lock=lambda **kwargs: nullcontext(), slot_lock=lambda *args, **kwargs: nullcontext())
        self.pool = SimpleNamespace(
            module={'path': 'repos/Nova'}, repo_key='nova', store=self.root / '.store',
            slot_relative_path=lambda number: '.worktrees/Nova-0',
            cached_baseline_sha_read_only=lambda: 'c' * 40,
            store_lock=lambda **kwargs: nullcontext())
        self.recovery = self.state.directory / 'daemon-resets' / self.identity['assignmentId']
        self.recovery.mkdir(parents=True)
        self.intent = {'schema': 1, 'cleanupVersion': 2, 'status': 'inspected',
                       'identity': self.identity, 'rootHead': 'e' * 40,
                       'sourceBaseline': 'c' * 40, 'inventory': [],
                       'recoveryPath': self.recovery.relative_to(self.root).as_posix()}
        (self.recovery / 'intent.json').write_text(json.dumps(self.intent))
        self.write_git = Mock(side_effect=AssertionError('Reconciliation must not repeat Git deletion'))
        common = ModuleType('review_common')
        common.ReviewState = lambda *args, **kwargs: self.state
        common.SourcePool = lambda *args: self.pool
        common.lock_session = lambda *args: nullcontext()
        common.run_git = self.write_git
        common.run_git_read = self.git_read
        common.validate_relative_worktree = lambda *args: None
        common.read_submodules = lambda *args, **kwargs: []
        common.worktree_rows = lambda *args, **kwargs: []
        common.assert_no_git_operation = lambda *args: None
        common.slot_writer_scope = lambda *args, **kwargs: nullcontext()
        common._slot_writer_assert_quiescent = Mock()
        prepare = ModuleType('review_git_prepare')
        prepare._find_worktree_row = lambda *args: {}
        prepare._worktree_functional = lambda *args: True
        admin = ModuleType('review_pool_admin')
        admin.select_module_by_name = lambda *args: self.pool.module
        admin._pool_status = lambda *args: {'status': 'warm'}
        self.modules = {'review_common': common, 'review_git_prepare': prepare,
                        'review_pool_admin': admin}

    def git_read(self, path, args):
        values = {('rev-parse', '--is-bare-repository'): 'true',
                  ('rev-parse', '--abbrev-ref', 'HEAD'): 'HEAD',
                  ('rev-parse', 'HEAD'): 'c' * 40 if path == self.source else 'e' * 40,
                  ('status', '--porcelain'): '',
                  ('ls-files', '--others', '--ignored', '--exclude-standard', '-z'): ''}
        return SimpleNamespace(stdout=values[tuple(args)])

    def invoke(self, apply=True, **changes):
        output = io.StringIO()
        cwd, paths = Path.cwd(), list(sys.path)
        try:
            os.chdir(self.root)
            with patch.dict(sys.modules, self.modules), patch.object(sys, 'argv',
                    [str(SCRIPT), json.dumps({**self.identity, **changes,
                                             'apply': apply, 'compactOutput': True})]), redirect_stdout(output):
                runpy.run_path(str(SCRIPT), run_name='__main__')
        finally:
            os.chdir(cwd)
            sys.path[:] = paths
        self.write_git.assert_not_called()
        return json.loads(output.getvalue())

    def test_exact_fresh_intent_recovers_missing_receipt_without_repeating_cleanup(self):
        result = self.invoke()
        self.assertEqual('reset', result['status'])
        self.assertEqual(self.identity, result['identity'])
        self.assertEqual(self.intent['inventory'], json.loads((self.recovery / 'receipt.json').read_text())['inventory'])
        self.assertEqual(result, self.invoke())

    def test_readonly_reconciliation_does_not_create_receipt(self):
        self.assertEqual('inspected', self.invoke(apply=False)['status'])
        self.assertFalse((self.recovery / 'receipt.json').exists())

    def test_partial_cleanup_and_invalid_intent_are_not_accepted(self):
        for field, value in (('schema', 2), ('cleanupVersion', 1), ('status', 'reset')):
            with self.subTest(field=field):
                (self.recovery / 'intent.json').write_text(json.dumps({**self.intent, field: value}))
                with self.assertRaisesRegex(RuntimeError, 'Foreign or older reset intent'):
                    self.invoke()
        (self.recovery / 'intent.json').write_text(json.dumps(self.intent))
        original = self.modules['review_common'].run_git_read
        self.modules['review_common'].run_git_read = lambda path, args: (
            SimpleNamespace(stdout=' M file.txt') if args == ['status', '--porcelain'] else original(path, args))
        with self.assertRaisesRegex(RuntimeError, 'Source is not fresh'):
            self.invoke()
        self.assertFalse((self.recovery / 'receipt.json').exists())
        self.write_git.assert_not_called()

    def test_observed_writer_blocks_reconciliation(self):
        self.modules['review_common']._slot_writer_assert_quiescent.side_effect = RuntimeError('Writer still active')
        with self.assertRaisesRegex(RuntimeError, 'Writer still active'):
            self.invoke()
        self.assertFalse((self.recovery / 'receipt.json').exists())
        self.write_git.assert_not_called()

    def test_foreign_identity_and_intervening_output_keep_receipt_missing(self):
        with self.assertRaisesRegex(RuntimeError, 'Foreign or older reset intent'):
            self.invoke(pr='456')
        output = self.outer / 'PRs/new/review.md'
        output.parent.mkdir(parents=True)
        output.write_text('new writer output')
        with self.assertRaisesRegex(RuntimeError, 'Old review output remains'):
            self.invoke()
        self.assertEqual('new writer output', output.read_text())
        self.assertFalse((self.recovery / 'receipt.json').exists())
        self.write_git.assert_not_called()


if __name__ == '__main__':
    unittest.main()
