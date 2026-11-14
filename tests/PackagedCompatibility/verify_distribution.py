#!/usr/bin/env python3
"""Check transported distribution against its successful gate receipt; never rebuild."""
import hashlib
import json
from pathlib import Path
import sys
import zipfile

from run import verify, HERE

def require(condition, message):
    if not condition:
        raise RuntimeError(message)


package, receipt_path = map(Path, sys.argv[1:])
receipt = json.loads(receipt_path.read_text())
require(receipt['passed'] is True, 'Unsuccessful gate receipt')
pins = json.loads((HERE / 'matrix.json').read_text())
require(receipt['inputs'] == pins, 'Runtime pins differ')
expected = {(p['version'], s) for p in pins.values()
            for s in ('healthy', 'warning', 'restored', 'disabled', 'multiple')}
require({(r['runtime'], r['scenario']) for r in receipt['results'] if r['behavioralPassed']} == expected, 'Incomplete behavior coverage')
require(len(receipt['results']) == len(expected), 'Unexpected behavior result count')
scans = receipt['scans']
require({s['runtime'] for s in scans} == {p['version'] for p in pins.values()}, 'Incomplete scan coverage')
require(len(scans) == len(pins), 'Unexpected scan count')
require(all(s['types'] > 0 and s['members'] > 0 and not (s['missingTypes'] or s['missingMembers'] or s['errors']) for s in scans), 'ABI scan did not pass')
verify(package, receipt['packageSha256'])
with zipfile.ZipFile(package) as archive:
    require(hashlib.sha256(archive.read('Lidarr.Plugin.Bandcamp.dll')).hexdigest() == receipt['pluginSha256'], 'DLL hash differs')
print('Verified distribution ZIP and DLL:', receipt['packageSha256'], receipt['pluginSha256'])
