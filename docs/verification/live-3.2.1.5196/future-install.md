# Future install and inverse — NOT EXECUTED

These are review instructions, not authorization. Production replacement/restart and restoration remain unapproved. This candidate is exact-target; reprepare if image, host assemblies, plugin, config or relevant state has changed. Refresh the backups immediately before any separately authorized deployment. Confirm server-discovery and functional qualification plan first.

Run future commands via `ssh -o BatchMode=yes -J proxmox media-vm`, after separate authorization. No credentials are needed in command arguments. Before stopping anything, validate the retained packet with `tools/native-compatibility/validate-preparation.py`, confirm current image equals the receipt, and confirm live old plugin hash. Keep a record of the execution and result. Do not execute the examples during preparation.

## Bounded plugin install + restart

Future remote shell (root), only after authorization:

```sh
set -eu
config=/srv/docker-state/stacks/media/lidarr
plugin="$config/plugins/jtstothard/lidarr-plugin-bandcamp/Lidarr.Plugin.Bandcamp.dll"
candidate=/srv/docker-state/lidarr-native-probe-20261115T0920/rootfs/host/Lidarr.Plugin.Bandcamp.dll
compose=/srv/docker-state/stacks/media/compose.yaml
test "$(docker inspect lidarr --format '{{.Image}}')" = sha256:5618a549ba6ef21318d24f869764fa58f51629b063a1a1ebf11cf8f32d1f5b8a
test "$(sha256sum "$plugin" | cut -d' ' -f1)" = dbb8637f1760756f87caf5b2dcc0b3e728fe49d2b9fd2576155e57f46e505cf9
test "$(sha256sum "$candidate" | cut -d' ' -f1)" = 4333fbc799674d80a06882d478072ce7c9a67348ee0dba4dffe2da8414dc1fd4
# Stage completely before downtime; preserve source identity and atomic replacement.
test ! -e "$plugin.prepared"
install -o 99 -g 100 -m 0644 "$candidate" "$plugin.prepared"
docker compose -f "$compose" stop lidarr
mv "$plugin.prepared" "$plugin"
docker compose -f "$compose" start lidarr
```

This is a stop/start restart, with no image recreation, provider edits or API tests that could contact Bandcamp. Afterward inspect authenticated read-only status and carefully redacted plugin loader diagnostics; never emit whole config, DB/provider payloads or logs that might contain secrets. Functional account calls need their own authority. If an effectful operational step fails, stop and preserve its result; do not blindly replay the sequence.

## Plugin-only inverse

After separately authorizing rollback, confirm the installed DLL is the candidate and the backup DLL matches the old hash, then stop, atomically replace the plugin with the backed-up old DLL (uid 99, gid 100, mode 0644), and start. Do not restore databases merely to undo a plugin replacement.

```sh
set -eu
plugin=/srv/docker-state/stacks/media/lidarr/plugins/jtstothard/lidarr-plugin-bandcamp/Lidarr.Plugin.Bandcamp.dll
backup=/srv/docker-state/rollback-preparation/lidarr-20261115T092035Z
compose=/srv/docker-state/stacks/media/compose.yaml
test "$(sha256sum "$plugin" | cut -d' ' -f1)" = 4333fbc799674d80a06882d478072ce7c9a67348ee0dba4dffe2da8414dc1fd4
test "$(sha256sum "$backup/Lidarr.Plugin.Bandcamp.dll" | cut -d' ' -f1)" = dbb8637f1760756f87caf5b2dcc0b3e728fe49d2b9fd2576155e57f46e505cf9
test ! -e "$plugin.rollback"
install -o 99 -g 100 -m 0644 "$backup/Lidarr.Plugin.Bandcamp.dll" "$plugin.rollback"
docker compose -f "$compose" stop lidarr
mv "$plugin.rollback" "$plugin"
docker compose -f "$compose" start lidarr
```

## Full DB/config recovery contingency

Requires a separate restoration decision, because it discards post-backup application state and could reintroduce old credentials/settings. Before any restoration, validate receipt hashes and integrity again and obtain a fresh online backup of current state. Stop Lidarr and confirm it is stopped. Preserve current config.xml, complete plugin directory, both DBs and their `-wal`/`-shm` sidecars in a new root-only external quarantine directory; do not delete or restore over active WAL files. With no writers, restore the backed-up DBs/config and complete plugin directory using recorded ownership/modes, with no stale sidecars. Restore compose.yaml only if independently needed and approved. Run full integrity checks on the restored offline DBs, verify hashes/permissions, then start only if restart is authorized. Do not claim recovery from a successful copy or hash: validate read-only application status afterward. This contingency was not executed or runtime-tested.
