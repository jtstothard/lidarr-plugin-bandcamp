"""Public release command regression: incompatible distribution must be rejected."""
import subprocess
import sys
import unittest
from pathlib import Path
from run import fetch, CACHE

class ReleaseGate(unittest.TestCase):
    def test_released_incompatible_distribution_is_rejected(self):
        CACHE.mkdir(exist_ok=True)
        package = fetch('plugin', 'plugin.zip')
        result = subprocess.run([sys.executable, str(Path(__file__).with_name('gate.py')),
                                 str(package), '--receipt', str(CACHE / 'bad-gate.json')],
                                capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('MissingMethodException', result.stdout + result.stderr)

    def test_distribution_with_unchecked_extra_payload_is_rejected(self):
        import tempfile
        import zipfile
        with tempfile.TemporaryDirectory() as tmp:
            package = Path(tmp) / 'extra.zip'
            with zipfile.ZipFile(package, 'w') as archive:
                archive.writestr('Lidarr.Plugin.Bandcamp.dll', b'not an assembly')
                archive.writestr('unchecked.dll', b'extra payload')
            result = subprocess.run([sys.executable, str(Path(__file__).with_name('gate.py')),
                                     str(package), '--receipt', str(Path(tmp) / 'receipt.json')],
                                    capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('Distribution must contain exactly', result.stderr)

if __name__ == '__main__':
    unittest.main()
