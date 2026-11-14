#!/usr/bin/env python3
"""Check the supplied distribution ZIP, without rebuilding the plugin."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import zipfile

from compatibility import extract_runtime_dlls, SCENARIOS

from run import HERE, CACHE, fetch, verify


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('package', type=Path)
    parser.add_argument('--receipt', type=Path, required=True)
    parser.add_argument('--scan', action='store_true', help='Also require supplementary direct ABI scanning')
    parser.add_argument('--scenario', action='append', choices=SCENARIOS)
    args = parser.parse_args()
    package = args.package.resolve()
    pins = json.loads((HERE / 'matrix.json').read_text())
    CACHE.mkdir(exist_ok=True)
    env = {k: v for k, v in os.environ.items() if not k.startswith(('DOTNET_', 'CORECLR_', 'COMPlus_'))}
    env['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    receipt = {'package': str(package), 'packageSha256': hashlib.sha256(package.read_bytes()).hexdigest(),
               'inputs': pins, 'sdk': subprocess.check_output(['dotnet', '--version'], text=True, env=env).strip(),
               'scannerLockSha256': hashlib.sha256((HERE / 'Scanner/packages.lock.json').read_bytes()).hexdigest() if args.scan else None,
               'results': []}
    root = None
    try:
        with tempfile.TemporaryDirectory(prefix='bandcamp-matrix-') as tmp:
            root = Path(tmp)
            plugin = root / 'Lidarr.Plugin.Bandcamp.dll'
            with zipfile.ZipFile(package) as archive:
                if archive.namelist() != [plugin.name]:
                    raise RuntimeError('Distribution must contain exactly Lidarr.Plugin.Bandcamp.dll')
                plugin.write_bytes(archive.read(plugin.name))
            receipt['pluginSha256'] = hashlib.sha256(plugin.read_bytes()).hexdigest()
            output = root / 'harness'
            subprocess.run(['dotnet', 'build', str(HERE / 'PackagedCompatibility.csproj'), '-c', 'Release', '-o', str(output)], check=True, env=env)
            if args.scan:
                scanner = root / 'scanner'
                subprocess.run(['dotnet', 'build', str(HERE / 'Scanner/Scanner.csproj'), '-c', 'Release', '-o', str(scanner)], check=True, env=env)
            for key, pin in pins.items():
                archive_path = fetch(key, key + '.tar.gz', pins)
                runtime = root / key
                runtime.mkdir()
                extract_runtime_dlls(archive_path, runtime)
                verify(runtime / 'Lidarr.Core.dll', pin['coreSha256'])
                for scenario in args.scenario or SCENARIOS:
                    command = ['dotnet', str(output / 'PackagedCompatibility.dll'), str(runtime), str(plugin), 'behavior', scenario]
                    probe = subprocess.run(command, capture_output=True, text=True, env=env)
                    print(f"{pin['version']} {scenario}: exit {probe.returncode}")
                    receipt.setdefault('invocations', []).append({'command': command, 'exit': probe.returncode, 'stderr': probe.stderr})
                    if probe.returncode != 0:
                        raise RuntimeError(f'{command}: {probe.stdout} {probe.stderr}')
                    result = json.loads(probe.stdout)
                    if not result['behavioralPassed']:
                        raise RuntimeError('Behavior assertion did not pass')
                    receipt['results'].append({'runtime': pin['version'], **result})
                if args.scan:
                    scan_command = ['dotnet', str(scanner / 'Scanner.dll'), str(runtime), str(plugin)]
                    scan = subprocess.run(scan_command, capture_output=True, text=True, env=env)
                    receipt.setdefault('invocations', []).append({'command': scan_command, 'exit': scan.returncode, 'stderr': scan.stderr})
                    print(f"{pin['version']} scan: exit {scan.returncode}")
                    result = json.loads(scan.stdout)
                    receipt.setdefault('scans', []).append({'runtime': pin['version'], **result})
                    if scan.returncode != 0:
                        raise RuntimeError(f'ABI scan failed: {scan.stdout} {scan.stderr}')
        receipt['passed'] = True
    except Exception as error:
        receipt['passed'] = False
        receipt['error'] = str(error)
        raise
    finally:
        normalized = json.loads(json.dumps(receipt).replace(str(root), '<isolated-root>'))
        args.receipt.write_text(json.dumps(normalized, indent=2) + '\n')


if __name__ == '__main__':
    main()
