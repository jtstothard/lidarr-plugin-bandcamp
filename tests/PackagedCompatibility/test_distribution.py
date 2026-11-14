#!/usr/bin/env python3
"""Exercise publication's byte/coverage verifier with the actual checked package."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile

from run import HERE

package, receipt_path = map(Path, sys.argv[1:])
receipt = json.loads(receipt_path.read_text())


def probe(package, receipt_path, expected):
    result = subprocess.run([sys.executable, str(HERE / 'verify_distribution.py'), str(package), str(receipt_path)], capture_output=True, text=True)
    if (result.returncode == 0) != expected:
        raise RuntimeError(result.stdout + result.stderr)


probe(package, receipt_path, True)
with tempfile.TemporaryDirectory() as tmp:
    root = Path(tmp)
    changed = root / 'changed.zip'
    changed.write_bytes(package.read_bytes() + b'changed after compatibility gate')
    probe(changed, receipt_path, False)
    for field, value in [('passed', False), ('results', receipt['results'][:-1]), ('scans', [])]:
        bad = {**receipt, field: value}
        path = root / (field + '.json')
        path.write_text(json.dumps(bad))
        probe(package, path, False)
print('Original distribution verified; changed ZIP, failed gate, incomplete behavior and missing scans rejected.')
