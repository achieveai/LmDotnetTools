import hashlib
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/repo_setup.py'
spec = importlib.util.spec_from_file_location('repo_setup', SCRIPT)
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)


class RepoSetupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=os.environ.get('TMPDIR'))
        self.base = Path(self.temp.name)
        self.root = self.base / 'outer'
        self.root.mkdir()
        self.config = self.base / 'gitconfig'
        self.config.write_text('')
        self.environment = patch.dict(os.environ, {
            'GIT_CONFIG_GLOBAL': str(self.config), 'GIT_CONFIG_SYSTEM': '/dev/null',
            'GIT_ALLOW_PROTOCOL': 'file', 'GIT_OPTIONAL_LOCKS': '0', 'GIT_AUTHOR_NAME': 'Fixture',
            'GIT_AUTHOR_EMAIL': 'fixture@example.invalid', 'GIT_COMMITTER_NAME': 'Fixture',
            'GIT_COMMITTER_EMAIL': 'fixture@example.invalid',
        })
        self.environment.start()
        self.sources = {}
        self.git(self.root, 'init', '-b', 'outer')
        (self.root / 'PRs').mkdir()
        (self.root / 'PRs/.gitkeep').write_text('')
        text = ''
        for name, branch in [('Nova', 'dev'), ('Astra', 'trunk')]:
            source = self.base / name
            source.mkdir()
            self.git(source, 'init', '-b', branch)
            (source / 'file.txt').write_text('base\n')
            self.git(source, 'add', '.')
            self.git(source, 'commit', '-m', 'base')
            sha = self.git(source, 'rev-parse', 'HEAD')
            self.sources[name] = source
            url = f'https://dev.azure.com/fixture/project/_git/{name}'
            with self.config.open('a') as config:
                config.write(f'[url "{source.as_uri()}"]\n\tinsteadOf = {url}\n')
            text += f'[submodule "repos/{name}"]\n\tpath = repos/{name}\n\turl = {url}\n'
            if name == 'Nova':
                text += '\tbranch = dev\n'
            self.git(self.root, 'update-index', '--add', '--cacheinfo', f'160000,{sha},repos/{name}')
            (self.root / f'repos/{name}').mkdir(parents=True)
        (self.root / '.gitmodules').write_text(text)
        self.git(self.root, 'add', '.gitmodules', 'PRs')
        self.git(self.root, 'commit', '-m', 'outer')

    def tearDown(self):
        self.environment.stop()
        self.temp.cleanup()

    def git(self, path, *args):
        result = subprocess.run(['git', '-C', str(path), *args], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        return result.stdout.strip()

    def test_plan_read_only_apply_repeat_and_preserve_shared_slot(self):
        before = sorted(str(p.relative_to(self.root)) for p in self.root.rglob('*'))
        plan = setup.synchronize(self.root)
        self.assertEqual(['dev', 'trunk'], [r['branch'] for r in plan['repositories']])
        self.assertEqual(['gitmodules', 'remote-head'], [r['branchSource'] for r in plan['repositories']])
        self.assertEqual(before, sorted(str(p.relative_to(self.root)) for p in self.root.rglob('*')))
        first = setup.synchronize(self.root, apply=True)
        store = self.root / first['repositories'][0]['store']
        baseline = first['repositories'][0]['targetSha']
        state = setup.ReviewState(self.root)
        pool = setup.SourcePool(state, setup.read_submodules(self.root)[0])
        with setup.lock_session(30), state.lock(phase='fixture-warm'):
            pool.publish_baseline(baseline, depth=1000000)
            pool.warm(range(6))
        slots = [self.root / pool.slot_relative_path(i) / pool.module['path'] for i in range(6)]
        metadata = [(slot, Path(self.git(slot, 'rev-parse', '--absolute-git-dir'))) for slot in slots]
        indexes = {slot: hashlib.sha256((directory / 'index').read_bytes()).hexdigest() for slot, directory in metadata}
        marker = self.root / '.git/review-setup/prepared-slots/test.json'
        marker.parent.mkdir(parents=True)
        marker.write_text('{"preserve":true}')
        (self.sources['Nova'] / 'file.txt').write_text('advanced\n')
        self.git(self.sources['Nova'], 'commit', '-am', 'advance')
        second = setup.synchronize(self.root, apply=True)
        self.assertNotEqual(baseline, second['repositories'][0]['targetSha'])
        for slot, directory in metadata:
            self.assertEqual(baseline, self.git(slot, 'rev-parse', 'HEAD'))
            self.assertEqual(indexes[slot], hashlib.sha256((directory / 'index').read_bytes()).hexdigest())
            self.assertEqual(str(directory), self.git(slot, 'rev-parse', '--absolute-git-dir'))
        self.assertEqual(baseline, self.git(store, 'rev-parse', 'refs/review-setup/baselines/repos-nova'))
        self.assertEqual('{"preserve":true}', marker.read_text())
        self.assertEqual(str(store), self.git(self.root / 'repos/Nova', 'rev-parse', '--path-format=absolute', '--git-common-dir'))
        third = setup.synchronize(self.root, apply=True)
        self.assertTrue(third['complete'])
        self.assertEqual([r['targetSha'] for r in second['repositories']], [r['targetSha'] for r in third['repositories']])

    def test_relative_links_repair_preserves_dirty_worktrees_and_relocation(self):
        setup.synchronize(self.root, apply=True)
        state = setup.ReviewState(self.root)
        module = setup.read_submodules(self.root)[0]
        pool = setup.SourcePool(state, module)
        baseline = self.git(self.root / module['path'], 'rev-parse', 'HEAD')
        with setup.lock_session(30), state.lock(phase='relative-path-fixture'):
            pool.publish_baseline(baseline, depth=1000000)
            pool.warm([0])
        outer = self.root / pool.slot_relative_path(0)
        nested = outer / module['path']
        views = [self.root, self.root / 'repos/Nova', self.root / 'repos/Astra', outer, nested]
        commons = [self.root] + [self.root / '.git/review-setup/source-stores' / f'repos-{name}.git'
                                 for name in ('nova', 'astra')]
        marker = self.root / '.git/review-setup/prepared-slots/relative-fixture.json'
        marker.parent.mkdir(parents=True, exist_ok=True)
        marker.write_text('{"preserve":true}')

        def assert_relative_links():
            for path in views[1:]:
                directory = Path(self.git(path, 'rev-parse', '--absolute-git-dir'))
                forward = Path((path / '.git').read_text().strip().removeprefix('gitdir: '))
                backward = Path((directory / 'gitdir').read_text().strip())
                common = Path((directory / 'commondir').read_text().strip())
                self.assertFalse(forward.is_absolute())
                self.assertFalse(backward.is_absolute())
                self.assertFalse(common.is_absolute())
                self.assertEqual(directory, (path / forward).resolve())
                self.assertEqual(path / '.git', (directory / backward).resolve())
                self.assertEqual(self.git(path, 'rev-parse', '--path-format=absolute', '--git-common-dir'),
                                 str((directory / common).resolve()))

        def snapshot():
            result = []
            for path in views:
                directory = Path(self.git(path, 'rev-parse', '--absolute-git-dir'))
                result.append((self.git(path, 'rev-parse', 'HEAD'),
                               (directory / 'HEAD').read_bytes(), (directory / 'index').read_bytes(),
                               self.git(path, 'status', '--porcelain=v1', '--ignore-submodules=none')))
            content = [(path / name).read_bytes() for path in views
                       for name in ('file.txt', 'PRs/.gitkeep', 'untracked.txt') if (path / name).exists()]
            object_dirs = [Path(self.git(common, 'rev-parse', '--path-format=absolute', '--git-common-dir')) / 'objects'
                           for common in commons]
            objects = sorted((str(path.relative_to(self.root)), hashlib.sha256(path.read_bytes()).hexdigest())
                             for directory in object_dirs for path in directory.rglob('*') if path.is_file())
            return result, content, marker.read_bytes(), objects

        # Exercise fresh creation, then recreate the old absolute-link metadata only.
        assert_relative_links()
        for common in commons:
            self.git(common, 'worktree', 'repair', '--no-relative-paths')
        for path in views[1:]:
            directory = Path(self.git(path, 'rev-parse', '--absolute-git-dir'))
            (path / '.git').write_text(f'gitdir: {directory}\n')
            (directory / 'gitdir').write_text(f'{path}/.git\n')
        (nested / 'file.txt').write_text('staged user change\n')
        self.git(nested, 'add', 'file.txt')
        (nested / 'file.txt').write_text('unstaged user change\n')
        (outer / 'PRs/.gitkeep').write_text('outer user change\n')
        (nested / 'untracked.txt').write_text('untracked user content\n')
        before = snapshot()
        import review_common
        with patch.object(review_common, 'run_git', wraps=review_common.run_git) as runner:
            setup.synchronize(self.root, apply=True)
        repairs = [call for call in runner.call_args_list if call.args[1] == ['worktree', 'repair', '--relative-paths']]
        self.assertEqual(3, len(repairs))
        self.assertEqual(before, snapshot())
        with patch.object(review_common, 'run_git', wraps=review_common.run_git) as runner:
            setup.synchronize(self.root, apply=True)
        self.assertFalse(any('repair' in call.args[1] and '-h' not in call.args[1] for call in runner.call_args_list))
        assert_relative_links()
        previous = self.root
        self.root = self.base / 'relocated'
        previous.rename(self.root)
        views = [self.root / path.relative_to(previous) for path in views]
        commons = [self.root / path.relative_to(previous) for path in commons]
        marker = self.root / marker.relative_to(previous)
        self.assertEqual(before, snapshot())
        assert_relative_links()
        for common in commons:
            self.assertEqual('1', self.git(common, 'config', '--get', 'core.repositoryformatversion'))
            self.assertEqual('true', self.git(common, 'config', '--get', 'extensions.relativeWorktrees'))

    def test_each_absolute_link_is_rejected_by_strict_validation_and_apply_repairs(self):
        setup.synchronize(self.root, apply=True)
        pool = setup.SourcePool(setup.ReviewState(self.root), setup.read_submodules(self.root)[0])
        path = self.root / pool.module['path']
        directory = Path(self.git(path, 'rev-parse', '--absolute-git-dir'))
        for pointer, absolute in [(path / '.git', f'gitdir: {directory}\n'),
                                  (directory / 'gitdir', f'{path}/.git\n'),
                                  (directory / 'commondir', f'{pool.store}\n')]:
            with self.subTest(pointer=pointer.name):
                pointer.write_text(absolute)
                self.assertFalse(setup.validate_relative_worktree(self.root, path, pool.store, require_relative=False))
                with self.assertRaises(setup.ReviewError):
                    setup.validate_relative_worktree(self.root, path, pool.store)
                before = pointer.read_bytes()
                setup.synchronize(self.root)
                self.assertEqual(before, pointer.read_bytes())
                if pointer.name == 'commondir':
                    with self.assertRaisesRegex(setup.ReviewError, 'commondir'):
                        setup.synchronize(self.root, apply=True)
                    self.assertEqual(before, pointer.read_bytes())
                else:
                    setup.synchronize(self.root, apply=True)
                    self.assertTrue(setup.validate_relative_worktree(self.root, path, pool.store))

    def test_foreign_registration_prevents_all_repairs_and_fetches(self):
        setup.synchronize(self.root, apply=True)
        pools = [setup.SourcePool(setup.ReviewState(self.root), m) for m in setup.read_submodules(self.root)]
        for pool in pools:
            self.git(pool.store, 'worktree', 'repair', '--no-relative-paths')
        target = self.git(self.root / pools[-1].module['path'], 'rev-parse', 'HEAD')
        self.git(pools[-1].store, 'worktree', 'add', '--detach', str(self.base / 'foreign'), target)
        before = (self.root / 'repos/Nova/.git').read_bytes()
        import review_common
        with patch.object(review_common, 'run_git', wraps=review_common.run_git) as runner:
            with self.assertRaises(setup.ReviewError):
                setup.synchronize(self.root, apply=True)
        self.assertEqual(before, (self.root / 'repos/Nova/.git').read_bytes())
        self.assertFalse(any('fetch' in c.args[1] or ('repair' in c.args[1] and '-h' not in c.args[1])
                             for c in runner.call_args_list))

    def test_broken_backpointer_and_symlink_metadata_refused_before_repair(self):
        setup.synchronize(self.root, apply=True)
        pool = setup.SourcePool(setup.ReviewState(self.root), setup.read_submodules(self.root)[0])
        path = self.root / pool.module['path']
        directory = Path(self.git(path, 'rev-parse', '--absolute-git-dir'))
        pointer = directory / 'gitdir';before = pointer.read_bytes()
        pointer.write_text(str(self.root / 'wrong/.git'))
        with self.assertRaises(setup.ReviewError):
            setup.synchronize(self.root, apply=True)
        pointer.write_bytes(before)
        common = directory / 'commondir';content = common.read_bytes();common.unlink()
        external = self.base / 'external-pointer';external.write_bytes(content);common.symlink_to(external)
        with self.assertRaises(setup.ReviewError):
            setup.synchronize(self.root, apply=True)

    def test_missing_relative_support_fails_before_remote_query_or_store_creation(self):
        import review_common
        real = review_common.run_git_read
        def unsupported(path, args, **kwargs):
            if args[:2] == ['worktree', 'add'] and '-h' in args:
                return subprocess.CompletedProcess(args, 129, 'old Git help', '')
            return real(path, args, **kwargs)
        with patch.object(review_common, 'run_git_read', side_effect=unsupported), \
             patch.object(setup, 'selected_branch', side_effect=AssertionError('network should not start')):
            with self.assertRaisesRegex(setup.ReviewError, 'native relative'):
                setup.synchronize(self.root, apply=True)
        self.assertFalse((self.root / '.git/review-setup/source-stores').exists())

    def test_repair_cleanup_unknown_reaches_existing_cli_exit_75(self):
        import io
        import review_common
        from contextlib import redirect_stdout
        setup.synchronize(self.root, apply=True)
        pool = setup.SourcePool(setup.ReviewState(self.root), setup.read_submodules(self.root)[0])
        path = self.root / pool.module['path']
        directory = Path(self.git(path, 'rev-parse', '--absolute-git-dir'))
        (path / '.git').write_text(f'gitdir: {directory}\n')
        (directory / 'gitdir').write_text(f'{path}/.git\n')
        self.assertFalse(setup.validate_relative_worktree(self.root, path, pool.store, require_relative=False))
        real = review_common.run_git
        def unknown(path, args, **kwargs):
            if args == ['worktree', 'repair', '--relative-paths']:
                raise setup.CleanupNotProvenError('private child detail')
            return real(path, args, **kwargs)
        output = io.StringIO()
        with patch.object(review_common, 'run_git', side_effect=unknown), \
             patch.object(sys, 'argv', [str(SCRIPT), 'apply', '--root', str(self.root)]), redirect_stdout(output):
            self.assertEqual(75, setup.main())
        self.assertIn('child-cleanup-not-proven', output.getvalue())
        self.assertNotIn('private child detail', output.getvalue())

    def test_local_override_and_dot_branch(self):
        self.git(self.sources['Nova'], 'branch', 'outer')
        self.git(self.root, 'config', 'submodule.repos/Nova.branch', '.')
        row = setup.synchronize(self.root)['repositories'][0]
        self.assertEqual('outer', row['branch'])
        self.assertEqual('local-config:outer-branch', row['branchSource'])
        self.git(self.root, 'checkout', '--detach')
        with self.assertRaises(setup.ReviewError):
            setup.synchronize(self.root)

    def test_dirty_staged_untracked_ignored_and_local_commit_refused(self):
        setup.synchronize(self.root, apply=True)
        checkout = self.root / 'repos/Nova'
        for staged in (False, True):
            (checkout / 'file.txt').write_text('dirty\n')
            if staged:
                self.git(checkout, 'add', 'file.txt')
            with self.assertRaises(setup.ReviewError):
                setup.synchronize(self.root, apply=True)
            self.git(checkout, 'restore', '--staged', '--worktree', '.')
        extra = checkout / 'extra'
        extra.write_text('untracked')
        with self.assertRaises(setup.ReviewError):
            setup.synchronize(self.root, apply=True)
        extra.unlink()
        store = Path(self.git(checkout, 'rev-parse', '--path-format=absolute', '--git-common-dir'))
        (store / 'info/exclude').write_text('extra\n')
        extra.write_text('ignored')
        with self.assertRaises(setup.ReviewError):
            setup.synchronize(self.root, apply=True)
        extra.unlink()
        (checkout / 'file.txt').write_text('local commit\n')
        self.git(checkout, 'commit', '-am', 'local')
        with self.assertRaises(setup.ReviewError):
            setup.synchronize(self.root, apply=True)

    def test_all_paths_preflighted_before_any_store_created(self):
        path = self.root / 'repos/Astra'
        path.rmdir()
        path.symlink_to(self.sources['Astra'], target_is_directory=True)
        with self.assertRaises(setup.ReviewError):
            setup.synchronize(self.root, apply=True)
        self.assertFalse((self.root / '.git/review-setup/source-stores').exists())

    def test_cleanup_not_proven_has_dedicated_unknown_exit(self):
        import io
        import json
        from contextlib import redirect_stdout
        output = io.StringIO()
        with patch.object(sys, 'argv', [str(SCRIPT), 'apply', '--root', str(self.root)]), \
             patch.object(setup, 'synchronize', side_effect=setup.CleanupNotProvenError('private diagnostic')), \
             redirect_stdout(output):
            self.assertEqual(75, setup.main())
        result = json.loads(output.getvalue())
        self.assertEqual('unknown', result['outcome'])
        self.assertNotIn('private diagnostic', output.getvalue())

    def test_ignore_all_cannot_hide_staged_gitlink(self):
        setup.synchronize(self.root, apply=True)
        source = self.sources['Nova']
        (source / 'file.txt').write_text('new tip')
        self.git(source, 'commit', '-am', 'advance')
        setup.synchronize(self.root, apply=True)
        self.git(self.root, 'add', 'repos/Nova')
        self.git(self.root, 'config', 'submodule.repos/Nova.ignore', 'all')
        self.assertEqual('', self.git(self.root, 'diff', '--cached', '--name-only'))
        with self.assertRaises(setup.ReviewError):
            setup.synchronize(self.root, apply=True)

    def test_missing_branch_fails_and_cli_emits_only_json(self):
        result = subprocess.run(['python3', str(SCRIPT), 'plan', '--root', str(self.root)],
                                capture_output=True, text=True, cwd=self.base)
        self.assertEqual(0, result.returncode, result.stderr)
        import json
        self.assertEqual('plan', json.loads(result.stdout)['mode'])
        self.git(self.root, 'config', 'submodule.repos/Nova.branch', 'missing')
        with self.assertRaises(setup.ReviewError):
            setup.synchronize(self.root, apply=True)
        self.assertFalse((self.root / 'repos/Nova/.git').exists())

    def test_wrong_store_origin_refused_without_moving_root(self):
        first = setup.synchronize(self.root, apply=True)
        row = first['repositories'][0]
        store = self.root / row['store']
        self.git(store, 'config', 'remote.origin.url', 'https://dev.azure.com/fixture/project/_git/Wrong')
        with self.assertRaises(setup.ReviewError):
            setup.synchronize(self.root, apply=True)
        self.assertEqual(row['targetSha'], self.git(self.root / row['path'], 'rev-parse', 'HEAD'))

    def test_foreign_checkout_and_staged_outer_refused(self):
        path = self.root / 'repos/Astra'
        self.git(path, 'init')
        with self.assertRaises(setup.ReviewError):
            setup.synchronize(self.root, apply=True)
        (self.root / '.gitmodules').write_text((self.root / '.gitmodules').read_text() + '\n# changed\n')
        self.git(self.root, 'add', '.gitmodules')
        with self.assertRaises(setup.ReviewError):
            setup.synchronize(self.root)


if __name__ == '__main__':
    unittest.main()
