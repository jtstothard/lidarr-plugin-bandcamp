"""Read-only validation on media-vm; emits metadata only."""
import hashlib, json, pathlib, sqlite3, subprocess
sandbox = pathlib.Path('/srv/docker-state/lidarr-native-probe-20261115T0920/rootfs/host')
backup = pathlib.Path('/srv/docker-state/rollback-preparation/lidarr-20261115T092035Z')
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
rows = subprocess.check_output(['docker', 'exec', 'lidarr', 'sh', '-c', 'sha256sum /app/lidarr/bin/*.dll /app/lidarr/bin/*.so'], text=True).splitlines()
for row in rows:
    expected, name = row.split(maxsplit=1)
    assert sha(sandbox / pathlib.Path(name).name) == expected, name
receipt = json.loads((backup / 'receipt.json').read_text())
assert backup.stat().st_mode & 0o777 == 0o700
for f in receipt['files']:
    target = backup / f['name']
    assert sha(target) == f['sha256']
    st = target.stat()
    assert (st.st_uid, st.st_gid, oct(st.st_mode & 0o777)) == (f['backup_uid'], f['backup_gid'], f['backup_mode'])
for name in ['lidarr.db', 'logs.db']:
    with sqlite3.connect((backup / name).as_uri() + '?mode=ro', uri=True) as db:
        assert db.execute('PRAGMA integrity_check').fetchall() == [('ok',)]
plugin = pathlib.Path('/srv/docker-state/stacks/media/lidarr/plugins/jtstothard/lidarr-plugin-bandcamp')
count = 0
for original in plugin.rglob('*'):
    if not original.is_file(): continue
    saved = backup / 'plugin-directory' / original.relative_to(plugin)
    assert sha(original) == sha(saved)
    a,b = original.stat(), saved.stat()
    assert (a.st_uid,a.st_gid,a.st_mode & 0o777) == (b.st_uid,b.st_gid,b.st_mode & 0o777)
    count += 1
assert sha(plugin / 'Lidarr.Plugin.Bandcamp.dll') == receipt['old_plugin_sha256']
print(json.dumps({'host_assemblies_and_native_libraries_identical_to_live':len(rows), 'backup_parent_mode':'0700', 'backup_file_hashes_and_modes_valid':True, 'sqlite_integrity':'ok', 'plugin_directory_files_valid':count, 'candidate_sha256':sha(sandbox / 'Lidarr.Plugin.Bandcamp.dll')}, indent=2))
