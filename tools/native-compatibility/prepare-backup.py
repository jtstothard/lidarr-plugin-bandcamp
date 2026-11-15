"""Run on media-vm as root. No secrets emitted; online SQLite backup, never raw DB copy."""
import datetime, hashlib, json, os, pathlib, shutil, sqlite3, xml.etree.ElementTree as ET, urllib.request
os.umask(0o077)
source = pathlib.Path('/srv/docker-state/stacks/media/lidarr')
plugin = source / 'plugins/jtstothard/lidarr-plugin-bandcamp/Lidarr.Plugin.Bandcamp.dll'
expected = 'dbb8637f1760756f87caf5b2dcc0b3e728fe49d2b9fd2576155e57f46e505cf9'
def digest(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()
assert digest(plugin) == expected, 'Old plugin identity changed'
# Key remains only in process memory/header. Do not print status payload wholesale.
key = ET.parse(source / 'config.xml').findtext('ApiKey')
request = urllib.request.Request('http://192.168.10.252:8686/api/v1/system/status', headers={'X-Api-Key': key})
with urllib.request.urlopen(request, timeout=15) as response:
    status = json.load(response)
assert status['version'] == '3.2.1.5196', 'Live version changed'
root = pathlib.Path('/srv/docker-state/rollback-preparation') / ('lidarr-' + datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ'))
root.mkdir(parents=True, mode=0o700, exist_ok=False)
files = []
for name in ('lidarr.db', 'logs.db'):
    original = source / name
    if not original.exists():
        continue
    with sqlite3.connect(original.as_uri() + '?mode=ro', uri=True) as src, sqlite3.connect(root / name) as dst:
        src.backup(dst, pages=256, sleep=0.1)
        assert dst.execute('PRAGMA integrity_check').fetchall() == [('ok',)]
    files.append((original, root / name))
for original, target in [(source / 'config.xml', root / 'config.xml'), (plugin, root / 'Lidarr.Plugin.Bandcamp.dll'), (source.parent / 'compose.yaml', root / 'compose.yaml')]:
    shutil.copyfile(original, target)
    assert digest(original) == digest(target)
    files.append((original, target))
# Retain complete installed plugin directory including metadata, protected by private parent.
shutil.copytree(plugin.parent, root / 'plugin-directory', copy_function=shutil.copy2)
for original in [plugin.parent, *plugin.parent.rglob('*')]:
    target = root / 'plugin-directory' / original.relative_to(plugin.parent)
    st = original.stat()
    os.chown(target, st.st_uid, st.st_gid)
    os.chmod(target, st.st_mode & 0o777)
receipt = {'backup_root': str(root), 'version': status['version'], 'old_plugin_sha256': expected, 'sqlite_integrity': 'ok', 'files': []}
for original, target in files:
    st = original.stat()
    os.chown(target, st.st_uid, st.st_gid)
    os.chmod(target, st.st_mode & 0o777)
    bt = target.stat()
    receipt['files'].append({'name': target.name, 'sha256': digest(target), 'source_uid': st.st_uid, 'source_gid': st.st_gid, 'source_mode': oct(st.st_mode & 0o777), 'backup_uid': bt.st_uid, 'backup_gid': bt.st_gid, 'backup_mode': oct(bt.st_mode & 0o777)})
assert digest(plugin) == expected
(root / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
print(json.dumps(receipt, indent=2))
