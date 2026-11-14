#!/usr/bin/env python3
"""Pinned public inputs only; each probe is a fresh application process."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import urllib.request
import zipfile

from compatibility import extract_runtime_dlls

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
PINS = json.loads((HERE / 'inputs.json').read_text())
CACHE = ROOT / '.compatibility-inputs'


def verify(path, expected):
    actual = hashlib.sha256(path.read_bytes()).hexdigest()
    if actual != expected:
        raise RuntimeError(f'SHA256 mismatch: {path}: {actual} != {expected}')


def fetch(key, filename, pins=PINS):
    pin = pins[key]
    path = CACHE / filename
    if not path.exists():
        with urllib.request.urlopen(pin['url']) as response, path.open('xb') as output:
            while chunk := response.read(1024 * 1024):
                output.write(chunk)
    verify(path, pin['sha256'])  # Never trust a cache hit or a mutable release URL.
    return path


def main():
    CACHE.mkdir(exist_ok=True)
    runtime_archive = fetch('runtime', 'runtime.tar.gz')
    plugin_archive = fetch('plugin', 'plugin.zip')
    with tempfile.TemporaryDirectory(prefix='bandcamp-compatibility-') as tmp:
        root = Path(tmp)
        runtime = root / 'runtime'
        runtime.mkdir()
        extract_runtime_dlls(runtime_archive, runtime)
        plugin = root / 'Lidarr.Plugin.Bandcamp.dll'
        with zipfile.ZipFile(plugin_archive) as archive:
            plugin.write_bytes(archive.read(plugin.name))
        verify(plugin, PINS['plugin']['dllSha256'])
        verify(runtime / 'Lidarr.Core.dll', PINS['runtime']['coreSha256'])
        env = {k: v for k, v in os.environ.items() if not k.startswith(('DOTNET_', 'CORECLR_', 'COMPlus_'))}
        env['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
        output = root / 'harness'
        subprocess.run(['dotnet', 'build', str(HERE / 'PackagedCompatibility.csproj'), '-c', 'Release', '-o', str(output)], check=True, env=env)
        command = ['dotnet', str(output / 'PackagedCompatibility.dll'), str(runtime), str(plugin)]
        # Red behavioral control: do not mistake an exception for successful health processing.
        red = subprocess.run(command + ['healthy'], capture_output=True, text=True, env=env)
        red_receipt = json.loads(red.stdout)
        if red.returncode != 1 or red_receipt['behavioralPassed']:
            raise RuntimeError(f'Known-bad behavioral control unexpectedly passed: {red.stdout} {red.stderr}')
        green = subprocess.run(command + ['known-bad'], capture_output=True, text=True, env=env)
        if green.returncode != 0:
            raise RuntimeError(f'Exact diagnostic failed: {green.stdout} {green.stderr}')
        receipt = json.loads(green.stdout)
        scenarios = {}
        for scenario, calls in [('warning', 1), ('restored', 2)]:
            probe = subprocess.run(command + ['known-bad', scenario], capture_output=True, text=True, env=env)
            if probe.returncode != 0:
                raise RuntimeError(f'{scenario} invocation failed: {probe.stdout} {probe.stderr}')
            scenario_receipt = json.loads(probe.stdout)
            if probe.returncode != 0 or scenario_receipt.get('homepageCalls') != calls:
                raise RuntimeError(f'{scenario} setup/control failed: {probe.stdout} {probe.stderr}')
            scenarios[scenario] = scenario_receipt

        # Negative control: an unrelated assembly-resolution failure must never pass.
        (runtime / 'Lidarr.Core.dll').unlink()
        unrelated = subprocess.run(command + ['known-bad'], capture_output=True, text=True, env=env)
        if unrelated.returncode == 0:
            raise RuntimeError('Missing-runtime negative control unexpectedly passed')
        print(json.dumps({'inputs': PINS, 'behavioralExit': red.returncode,
                          'diagnosticExit': green.returncode,
                          'missingRuntimeRejected': True, 'result': receipt, 'scenarioControls': scenarios}, indent=2))


if __name__ == '__main__':
    main()
