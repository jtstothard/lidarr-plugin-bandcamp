#!/usr/bin/env python3
"""Check the supplied distribution ZIP, without rebuilding the plugin."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import zipfile

from run import HERE, CACHE, fetch, verify


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('package', type=Path)
    parser.add_argument('--receipt', type=Path, required=True)
    parser.add_argument('--scenario', action='append', choices=('healthy', 'warning', 'restored', 'disabled', 'multiple'))
    args = parser.parse_args()
    package = args.package.resolve()
    pins = json.loads((HERE / 'matrix.json').read_text())
    CACHE.mkdir(exist_ok=True)
    env = {k: v for k, v in os.environ.items() if not k.startswith(('DOTNET_', 'CORECLR_', 'COMPlus_'))}
    env['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    receipt = {'package': str(package), 'packageSha256': hashlib.sha256(package.read_bytes()).hexdigest(),
               'inputs': pins, 'results': []}
    with tempfile.TemporaryDirectory(prefix='bandcamp-matrix-') as tmp:
        root = Path(tmp)
        plugin = root / 'Lidarr.Plugin.Bandcamp.dll'
        with zipfile.ZipFile(package) as archive:
            plugin.write_bytes(archive.read(plugin.name))
        receipt['pluginSha256'] = hashlib.sha256(plugin.read_bytes()).hexdigest()
        output = root / 'harness'
        subprocess.run(['dotnet', 'build', str(HERE / 'PackagedCompatibility.csproj'), '-c', 'Release', '-o', str(output)], check=True, env=env)
        for key, pin in pins.items():
            archive_path = fetch(key, key + '.tar.gz', pins)
            runtime = root / key
            runtime.mkdir()
            with tarfile.open(archive_path) as archive:
                for member in archive:
                    if member.isfile() and member.name.startswith('Lidarr/') and member.name.count('/') == 1 and member.name.endswith('.dll'):
                        (runtime / Path(member.name).name).write_bytes(archive.extractfile(member).read())
            verify(runtime / 'Lidarr.Core.dll', pin['coreSha256'])
            for scenario in args.scenario or ('healthy', 'warning', 'restored', 'disabled', 'multiple'):
                command = ['dotnet', str(output / 'PackagedCompatibility.dll'), str(runtime), str(plugin), 'behavior', scenario]
                probe = subprocess.run(command, capture_output=True, text=True, env=env)
                print(f"{pin['version']} {scenario}: exit {probe.returncode}")
                if probe.returncode != 0:
                    raise RuntimeError(f'{command}: {probe.stdout} {probe.stderr}')
                result = json.loads(probe.stdout)
                if not result['behavioralPassed']:
                    raise RuntimeError('Behavior assertion did not pass')
                receipt['results'].append({'runtime': pin['version'], **result})
        normalized = json.loads(json.dumps(receipt).replace(str(root), '<isolated-root>'))
        args.receipt.write_text(json.dumps(normalized, indent=2) + '\n')


if __name__ == '__main__':
    main()
