import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name('evaluate.py')

class EvaluationCliTests(unittest.TestCase):
    def run_cli(self, record):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'record.json'
            path.write_text(json.dumps(record))
            return subprocess.run([sys.executable, str(SCRIPT), str(path)], capture_output=True, text=True)

    def test_empty_evidence_cannot_pass_live_gate(self):
        result = self.run_cli({'web': [], 'subsonic': []})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)['decision'], 'INCONCLUSIVE')
        self.assertEqual(json.loads(result.stdout)['coverage'], 'INCONCLUSIVE')

    def test_missing_metadata_is_unknown_even_when_both_sides_match(self):
        row = {'key': 'item-1', 'artist': '', 'album': 'Album', 'track': 'Track',
               'release_date': '2020-01-01', 'duration': 120, 'ownership': 'owned'}
        result = self.run_cli({'web': [row], 'subsonic': [row],
                               'web_complete': True, 'subsonic_complete': True})
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual(report['metadata_unknown_fields'], 2)
        self.assertEqual(report['decision'], 'INCONCLUSIVE')

    def test_malformed_rows_fail_without_echoing_evidence(self):
        result = self.run_cli({'web': [None], 'subsonic': []})
        self.assertEqual(result.returncode, 2)
        self.assertNotIn('Traceback', result.stderr)
        self.assertEqual(result.stdout, '')

if __name__ == '__main__':
    unittest.main()
