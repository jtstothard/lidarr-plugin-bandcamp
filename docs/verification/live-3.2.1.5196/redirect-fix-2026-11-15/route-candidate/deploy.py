"""Pinned media-vm packet. Plan-only by default; --apply requires separate approval.

Run on media-vm, never on the desktop. No implicit inverse or download dispatch.
An inverse is a distinct, separately authorized invocation with --rollback --apply.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import time
import urllib.request
import xml.etree.ElementTree as ET

ROOT = Path('/srv/docker-state/rollback-preparation/lidarr-resolver-route-20261115T150021Z')
CONFIG = Path('/srv/docker-state/stacks/media/lidarr')
TARGET = CONFIG / 'plugins/jtstothard/lidarr-plugin-bandcamp/Lidarr.Plugin.Bandcamp.dll'
COMPOSE = CONFIG.parent / 'compose.yaml'
OLD = '2e1b47a848abfb74d6e261b4f44c405ca1c176c368ff8d2f50f924ed3b7c2625'
NEW = 'ad312e81cbf9bcf3290aa761508fc69a9ee094b519b5c738233f54523c165f08'
IMAGE = 'sha256:5618a549ba6ef21318d24f869764fa58f51629b063a1a1ebf11cf8f32d1f5b8a'


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def inspect():
    return json.loads(subprocess.check_output(['docker', 'inspect', 'lidarr']))[0]


def require(condition, reason):
    if not condition:
        raise RuntimeError(reason)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--rollback', action='store_true')
    args = parser.parse_args()
    operation = 'inverse' if args.rollback else 'deploy'
    expected, wanted = (NEW, OLD) if args.rollback else (OLD, NEW)
    source = ROOT / 'Lidarr.Plugin.Bandcamp.dll' if args.rollback else ROOT / 'route-probe-rootfs/host/Lidarr.Plugin.Bandcamp.dll'
    if not args.apply:
        print(json.dumps({'mode': 'PLAN_ONLY', 'operation': operation, 'source': str(source),
                          'expected_installed_sha256': expected, 'candidate_sha256': wanted,
                          'backup_root': str(ROOT), 'service': 'lidarr', 'settings_restore': False}))
        return

    stage = 'preflight'
    receipt = None
    try:
        require(os.geteuid() == 0, 'root required on media-vm')
        state = inspect()
        require(state['Image'] == IMAGE and state['State']['Running'], 'host identity/state changed')
        require(sha(TARGET) == expected and sha(source) == wanted, 'DLL identity changed')
        require(sha(ROOT / 'Lidarr.Plugin.Bandcamp.dll') == OLD, 'rollback DLL changed')
        require(sha(COMPOSE) == sha(ROOT / 'compose.yaml'), 'Compose configuration changed')
        require(sha(CONFIG / 'config.xml') == sha(ROOT / 'config.xml'), 'host configuration changed')
        if not args.rollback:
            with sqlite3.connect((CONFIG / 'lidarr.db').as_uri() + '?mode=ro', uri=True) as live, \
                 sqlite3.connect((ROOT / 'lidarr.db').as_uri() + '?mode=ro', uri=True) as saved:
                for table, identity in [('DownloadClients', 12), ('Indexers', 26)]:
                    query = 'SELECT Name,Settings FROM ' + table + ' WHERE Id=?'
                    require(live.execute(query, (identity,)).fetchone() == saved.execute(query, (identity,)).fetchone(),
                            'saved authentication definition changed since backup')

        # O_EXCL preserves spent execution history and prevents a blind second dispatch.
        fd = os.open(ROOT / (operation + '-dispatch.jsonl'), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        receipt = os.fdopen(fd, 'w')

        def record(phase):
            receipt.write(json.dumps({'phase': phase, 'utc': time.time(), 'container_id': state['Id']}) + '\n')
            receipt.flush()
            os.fsync(receipt.fileno())
            print('phase=' + phase, flush=True)

        record('dispatched')
        stage = 'stage'
        staging = TARGET.with_name(TARGET.name + '.' + operation + '-auth-redirect-stage')
        fd = os.open(staging, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o644)
        with os.fdopen(fd, 'wb') as stream:
            stream.write(source.read_bytes())
            stream.flush()
            os.fsync(stream.fileno())
        os.chown(staging, 99, 100)
        os.chmod(staging, 0o644)
        require(sha(staging) == wanted, 'staging readback mismatch')
        record('staged')
        for action in ['stop', 'replace', 'start']:
            stage = action
            if action == 'replace':
                current = inspect()
                require(current['Id'] == state['Id'] and not current['State']['Running'], 'container not stopped or replaced')
                require(sha(TARGET) == expected and sha(staging) == wanted, 'concurrent DLL edit')
                os.replace(staging, TARGET)
            else:
                with (ROOT / (operation + '-' + action + '.log')).open('x') as log:
                    subprocess.run(['docker', 'compose', '-f', str(COMPOSE), action, 'lidarr'],
                                   stdout=log, stderr=subprocess.STDOUT, check=True)
            record(action)
        stage = 'verify'
        require(sha(TARGET) == wanted, 'installed hash mismatch')
        config = ET.parse(CONFIG / 'config.xml').getroot()
        url = 'http://192.168.10.252:' + config.findtext('Port') + (config.findtext('UrlBase') or '') + '/api/v1/system/status'
        ready = False
        for _ in range(15):
            try:
                request = urllib.request.Request(url, headers={'X-Api-Key': config.findtext('ApiKey')})
                with urllib.request.urlopen(request, timeout=3) as response:
                    ready = json.load(response).get('version') == '3.2.1.5196'
                if ready:
                    break
            except Exception:
                pass
            time.sleep(2)
        final = inspect()
        require(ready and final['Image'] == IMAGE and final['Id'] == state['Id'] and final['State']['Running'], 'readiness unresolved')
        record('verified')
    except Exception as error:
        # No raw HTTP, config, provider payload, upstream exception or blind inverse.
        print(json.dumps({'result': 'STOP', 'stage': stage, 'error_type': type(error).__name__,
                          'inverse_performed': False, 'reconcile_before_any_further_effect': True}))
        raise SystemExit(1)
    finally:
        if receipt is not None:
            receipt.close()


if __name__ == '__main__':
    main()
