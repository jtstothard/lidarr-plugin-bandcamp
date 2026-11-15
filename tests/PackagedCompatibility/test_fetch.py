"""Cache transfer and pin-refresh regression controls; no network required."""
import hashlib
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import run


class FetchTests(unittest.TestCase):
    def test_pin_refresh_and_identical_content_share_entries(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(run, 'CACHE', Path(tmp)):
            for payload in (b'old', b'new'):
                digest = hashlib.sha256(payload).hexdigest()
                pins = {'input': {'url': 'https://example.invalid/input', 'sha256': digest}}
                with patch.object(run.urllib.request, 'urlopen', return_value=io.BytesIO(payload)) as download:
                    result = run.fetch('input', 'role.zip', pins)
                    self.assertEqual(result.name, digest)
                    self.assertEqual(result.read_bytes(), payload)
                    download.assert_called_once_with(pins['input']['url'], timeout=60)
                with patch.object(run.urllib.request, 'urlopen', side_effect=AssertionError('cache miss')):
                    self.assertEqual(run.fetch('input', 'other-role.zip', pins), result)

    def test_failed_transfer_is_not_cached_and_retry_succeeds(self):
        payload = b'complete'
        pins = {'input': {'url': 'https://example.invalid/input', 'sha256': hashlib.sha256(payload).hexdigest()}}
        with tempfile.TemporaryDirectory() as tmp, patch.object(run, 'CACHE', Path(tmp)):
            with patch.object(run.urllib.request, 'urlopen', return_value=io.BytesIO(b'truncated')):
                with self.assertRaisesRegex(RuntimeError, 'SHA256 mismatch'):
                    run.fetch('input', 'role.zip', pins)
            self.assertEqual(list(Path(tmp).iterdir()), [])
            with patch.object(run.urllib.request, 'urlopen', side_effect=TimeoutError):
                with self.assertRaises(TimeoutError):
                    run.fetch('input', 'role.zip', pins)
            self.assertEqual(list(Path(tmp).iterdir()), [])
            with patch.object(run.urllib.request, 'urlopen', return_value=io.BytesIO(payload)):
                self.assertEqual(run.fetch('input', 'role.zip', pins).read_bytes(), payload)


if __name__ == '__main__':
    unittest.main()
