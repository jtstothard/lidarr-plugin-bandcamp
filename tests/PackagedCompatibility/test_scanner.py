"""CLI controls prove type, member and dependency failures cannot pass the ABI guard."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile

from compatibility import extract_runtime_dlls
from run import HERE, CACHE, fetch

class ScannerControls(unittest.TestCase):
    def test_scanner_rejects_missing_type_member_and_dependency(self):
        CACHE.mkdir(exist_ok=True)
        pins = json.loads((HERE / 'matrix.json').read_text())
        key = 'nightly' if 'nightly' in pins else list(pins)[-1]
        archive = fetch(key, key + '.tar.gz', pins)
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            runtime = root / 'runtime'
            runtime.mkdir()
            extract_runtime_dlls(archive, runtime)
            scanner = root / 'scanner'
            subprocess.run(['dotnet', 'build', str(HERE / 'Scanner/Scanner.csproj'), '-o', str(scanner)], check=True, capture_output=True)
            subprocess.run(['dotnet', 'run', '--project', str(HERE / 'ScannerControls/ScannerControls.csproj'), '--', str(root)], check=True, capture_output=True)
            receipts = {}
            for name, field in [('missing-type', 'missingTypes'), ('missing-member', 'missingMembers'), ('valid', None)]:
                probe = subprocess.run(['dotnet', str(scanner / 'Scanner.dll'), str(runtime), str(root / (name + '.dll'))], capture_output=True, text=True)
                result = json.loads(probe.stdout)
                self.assertEqual(probe.returncode, 0 if field is None else 1, probe.stdout + probe.stderr)
                if field:
                    self.assertTrue(result[field], result)
                receipts[name] = result
            bad = fetch('plugin', 'plugin.zip')
            with zipfile.ZipFile(bad) as archive:
                (root / 'known-bad.dll').write_bytes(archive.read('Lidarr.Plugin.Bandcamp.dll'))
            probe = subprocess.run(['dotnet', str(scanner / 'Scanner.dll'), str(runtime), str(root / 'known-bad.dll')], capture_output=True, text=True)
            result = json.loads(probe.stdout)
            self.assertEqual(probe.returncode, 1, probe.stdout)
            self.assertTrue(any('HealthCheck::.ctor' in member for member in result['missingMembers']), result)
            self.assertFalse(result['errors'], result)
            receipts['known-bad-release'] = result
            # Missing transitive metadata dependency while resolving a base member:
            # this must be an explicit resolution error, never a clean missing-member result.
            dependency = runtime / 'System.Runtime.dll'
            dependency_bytes = dependency.read_bytes()
            dependency.unlink()
            probe = subprocess.run(['dotnet', str(scanner / 'Scanner.dll'), str(runtime), str(root / 'missing-member.dll')], capture_output=True, text=True)
            result = json.loads(probe.stdout)
            self.assertEqual(probe.returncode, 1, probe.stdout)
            self.assertTrue(result['errors'], result)
            receipts['missing-transitive-dependency'] = result
            dependency.write_bytes(dependency_bytes)
            (runtime / 'Lidarr.Core.dll').unlink()
            probe = subprocess.run(['dotnet', str(scanner / 'Scanner.dll'), str(runtime), str(root / 'valid.dll')], capture_output=True, text=True)
            result = json.loads(probe.stdout)
            self.assertEqual(probe.returncode, 1, probe.stdout)
            self.assertTrue(result['errors'], result)
            receipts['missing-dependency'] = result
            print(json.dumps(receipts, indent=2).replace(str(root), '<control-root>'))

if __name__ == '__main__':
    unittest.main()
