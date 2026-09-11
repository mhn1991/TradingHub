"""Publication safety and runner integration, without executing a market replay."""
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from alfonso_publish_completed import build_run, publish


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.output = self.root / 'public/latest.json'

    def run_record(self, simulation='one', at='2026-09-08T12:00:00+00:00'):
        return {'simulationId': simulation, 'generatedAt': at, 'records': []}

    def test_upsert_preserves_older_runs_and_newest_is_first(self):
        newest = self.run_record('new')
        oldest = self.run_record('old', '2026-09-07T12:00:00+00:00')
        publish(newest, self.output)
        publish(oldest, self.output)
        newest['label'] = 'Updated label'
        publish(newest, self.output)
        payload = json.loads(self.output.read_text())
        self.assertEqual(payload['runs'], [newest, oldest])
        self.assertEqual(payload['generatedAt'], newest['generatedAt'])

    def test_invalid_archive_is_not_overwritten(self):
        self.output.parent.mkdir()
        self.output.write_text('{"schemaVersion":9,"runs":[]}')
        before = self.output.read_bytes()
        with self.assertRaises(ValueError):
            publish(self.run_record(), self.output)
        self.assertEqual(self.output.read_bytes(), before)

    def test_serialization_failure_keeps_previous_archive(self):
        publish(self.run_record(), self.output)
        before = self.output.read_bytes()
        with self.assertRaises(ValueError):
            publish({**self.run_record('bad'), 'value': float('nan')}, self.output)
        self.assertEqual(self.output.read_bytes(), before)
        self.assertEqual(list(self.output.parent.glob('*.tmp')), [])

    def test_incomplete_replay_never_loads_candles(self):
        result = {'simulationId': 'test', 'strategies': [{'strategyId': 'alfonso', 'isComplete': False}]}
        with patch('alfonso_publish_completed.read', side_effect=[result, {}]), \
                patch('alfonso_publish_completed.historical_context') as history:
            with self.assertRaisesRegex(ValueError, 'incomplete'):
                build_run(self.root, self.root)
            history.assert_not_called()

    def test_missing_completion_marker_is_rejected(self):
        result = {'simulationId': 'test', 'strategies': [{'strategyId': 'alfonso', 'isComplete': True}]}
        with patch('alfonso_publish_completed.read', side_effect=[result, {}]):
            with self.assertRaisesRegex(ValueError, 'incomplete'):
                build_run(self.root, self.root)


class RunnerTests(unittest.TestCase):
    def exercise(self, replay_exit=0, publish_exit=0):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'tools').mkdir()
            original = Path(__file__).with_name('alfonso_lower_alignment_test.sh')
            runner = root / 'tools/runner.sh'
            runner.write_text(original.read_text())
            binaries = root / 'bin'
            binaries.mkdir()
            scripts = {
                'git': '#!/bin/sh\nexit 0\n',
                'dotnet': f'#!/bin/sh\nexit {replay_exit}\n',
                'python3': f'#!/bin/sh\nprintf "%s\\n" "$@" > "$PUBLISH_MARKER"\nexit {publish_exit}\n',
            }
            for name, content in scripts.items():
                binary = binaries / name
                binary.write_text(content)
                binary.chmod(0o755)
            marker = root / 'published.txt'
            completed = subprocess.run(['bash', str(runner), str(root / 'results with spaces')],
                                       env={**os.environ, 'PATH': f'{binaries}:{os.environ["PATH"]}',
                                            'PUBLISH_MARKER': str(marker)}, capture_output=True, text=True)
            return completed, marker.read_text().splitlines() if marker.exists() else None

    def test_success_automatically_publishes(self):
        result, invocation = self.exercise()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(invocation[0], 'tools/alfonso_publish_completed.py')
        self.assertTrue(invocation[1].endswith('/results with spaces'))

    def test_failed_replay_does_not_publish(self):
        result, invocation = self.exercise(replay_exit=7)
        self.assertEqual(result.returncode, 7)
        self.assertIsNone(invocation)

    def test_publication_failure_reports_retry_without_replay(self):
        result, invocation = self.exercise(publish_exit=1)
        self.assertEqual(result.returncode, 1)
        self.assertIsNotNone(invocation)
        self.assertIn('Results are safe', result.stderr)
        self.assertIn('Retry publication without rerunning', result.stderr)


if __name__ == '__main__':
    unittest.main()
