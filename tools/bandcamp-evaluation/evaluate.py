#!/usr/bin/env python3
"""Offline evidence comparison only. Never contacts Bandcamp or accepts credentials."""
import argparse
import json
from pathlib import Path
import subprocess
import sys

FIELDS = ('artist', 'album', 'track', 'release_date', 'duration', 'ownership')


def evaluate(record):
    web = {row['key']: row for row in record['web']}
    subsonic = {row['key']: row for row in record['subsonic']}
    if len(web) != len(record['web']) or len(subsonic) != len(record['subsonic']):
        raise ValueError('duplicate key')
    missing = len(web.keys() - subsonic.keys())
    extra = len(subsonic.keys() - web.keys())
    mismatches = {field: sum(web[key].get(field) != subsonic[key].get(field)
                             for key in web.keys() & subsonic.keys()) for field in FIELDS}
    unknown = sum(row.get(field) in (None, '') for rows in (web, subsonic)
                  for row in rows.values() for field in FIELDS)
    complete = record.get('web_complete') is True and record.get('subsonic_complete') is True
    coverage = 'FAIL' if missing or extra else ('MATCH_RECORDED' if complete and web else 'INCONCLUSIVE')
    return {'decision': 'INCONCLUSIVE', 'web_primary': True, 'coverage': coverage,
            'web_items': len(web), 'subsonic_items': len(subsonic), 'missing': missing,
            'extra': extra, 'metadata_mismatches': mismatches, 'metadata_unknown_fields': unknown,
            'live_gate': 'Requires separately authorized review of provenance, downloads, formats and lifecycle.'}


def probe(path):
    # Resolve to a local regular file; never allow ffprobe URL/protocol input.
    local = Path(path).resolve(strict=True)
    if not local.is_file():
        raise ValueError('not a regular file')
    result = subprocess.run(['ffprobe', '-v', 'error', '-protocol_whitelist', 'file',
                             '-show_entries', 'format=format_name,duration,bit_rate,size:stream=codec_type,codec_name,bit_rate,sample_rate,channels,bits_per_raw_sample',
                             '-of', 'json', str(local)], capture_output=True, timeout=30, check=True)
    data = json.loads(result.stdout)
    return {'bytes': local.stat().st_size, 'format': data.get('format', {}),
            'streams': data.get('streams', []), 'provenance': 'LOCAL_FILE_ONLY_NOT_DOWNLOAD_PROOF'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('record', nargs='?', help='local normalized JSON inventory')
    parser.add_argument('--probe', help='inspect one existing local audio file; requires ffprobe')
    args = parser.parse_args()
    try:
        if bool(args.record) == bool(args.probe):
            raise ValueError('choose one input')
        output = probe(args.probe) if args.probe else evaluate(json.loads(Path(args.record).read_text()))
        print(json.dumps(output, sort_keys=True, indent=2))
        return 0
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        # Never echo input, paths, raw server bodies or ffprobe stderr.
        print('Evaluation failed: invalid local evidence or unavailable media probe.', file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
