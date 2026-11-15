# Preparation for media-vm Lidarr 3.2.1.5196

Prepared 2026-11-15 from integration `72c2776`. Preparation only: no production plugin replacement, restart, provider edits, Bandcamp calls, or restoration. SSH used `ssh -o BatchMode=yes -J proxmox media-vm`; no 1Password or apphost invocation.

## Candidate and proof

Candidate SHA-256: `4333fbc799674d80a06882d478072ce7c9a67348ee0dba4dffe2da8414dc1fd4`.

Local candidate: `src/Lidarr.Plugin.Bandcamp/bin/Release/net8.0/Lidarr.Plugin.Bandcamp.dll` in this worktree. Retained remote candidate: `/srv/docker-state/lidarr-native-probe-20261115T0920/rootfs/host/Lidarr.Plugin.Bandcamp.dll`.

Exact image: `sha256:5618a549ba6ef21318d24f869764fa58f51629b063a1a1ebf11cf8f32d1f5b8a`. Read-only authenticated `/api/v1/system/status` confirmed 3.2.1.5196. API key was read from config.xml into Python memory and used only in an HTTP header; no secret content was emitted or stored in receipts. Runtime configuration confirms bundled .NET/ASP.NET 8.0.27. Remote free space was 94G; local free space 53Gi before copying the 150M host.

The opt-in `LidarrBinaryPath` build uses copied deployed Core/Common and dependency DLLs, bypassing source project references. It leaves the existing source/CI mode intact. The candidate now references Core/Common **3.2.1.5196**, not the historical nondeterministic 10.* references. CI's major-2 source pin is not evidence of ABI compatibility and was not used for this qualification.

The first exact-target compile failed CS7036: the deployed health-warning constructor requires `HealthCheckReason`. The fix uses the shared source constructor plus warning/message properties. The regression asserts the warning source as well as its type/message and recovery to OK. Final Release build: zero warnings/errors. Plugin tests: **296/296**; helper tests: **30/30**; Python: **6/6**. Plugin/helper tests execute on installed .NET 10; the separate native proof below executes on exact bundled .NET 8.0.27. One initial test failed solely because the FLAC source fixture was absent; a copy of the existing fixture was added under this worktree's ignored ext directory, without replacing any existing references.

`native-exact-final.txt` is the authoritative native result for the final candidate hash. A small C host loads hostfxr and targets **Probe.dll**, never Lidarr.dll or its apphost. The probe loads every plugin type (148), JIT-prepares 801 non-generic method/constructor bodies, instantiates the plugin descriptor and uses deployed `WithNzbDroneRules`/`AutoAddServices` to resolve BandcampHttpDispatcher with mocked constructor dependencies. It does not execute business methods, tasks, providers, database migrations or the server entry point.

The probe ran as UID 65534 in a systemd RootDirectory containing copied host/runtime files, copied Alpine libraries and ICU data. RootDirectory excludes all production/home appdata. Properties: `PrivateNetwork=yes`, `RestrictAddressFamilies=AF_UNIX`, `ProtectHome=yes`, `ProtectSystem=strict`, `PrivateTmp=yes`, `PrivateDevices=yes`, `NoNewPrivileges=yes`, `MountAPIVFS=yes`. The probe independently confirmed production/default config paths unavailable and AF_INET socket creation denied. All deployed managed/native libraries in the sandbox were subsequently compared to the live container (`validation.json`). Moq and Castle are probe-only dependencies, never plugin payloads.

Preserved earlier attempts show missing musl dependencies, SDK muxer argument interpretation, missing ICU, and initial Moq resolution before resolver registration. No Lidarr server entry point was invoked. An intermediate probe temporarily used Moq's EventLog dependency; authoritative final proof restores and verifies the exact host dependency. Probe compilation retains an EventLog 6-versus-8 reference warning from Moq; native execution uses live EventLog 8 and passes. The final candidate changed after narrowing binary reference inputs; both old candidate identity and final rerun are retained.

This proves native type/JIT/DI compatibility against this exact deployment. It does **not** prove server PluginLoadContext discovery, settings/schema API flows, persistence/restart behavior, scheduled runtime behavior, authentication, search, download, or live account operation. Those remain separate gates requiring appropriate authorization. No claim of full application qualification is made.

## Rollback material

Protected external backup: `/srv/docker-state/rollback-preparation/lidarr-20261115T092035Z` (root-owned parent mode 0700). `backup.json` contains metadata only. SQLite's online backup API opened each live DB with `mode=ro`; both lidarr.db and logs.db passed full `PRAGMA integrity_check` on backup and readback. No raw live DB copy was used. The two databases are independently consistent snapshots, not an atomic cross-database/config snapshot; config and plugin hashes were checked at copy time. No providers were edited during preparation.

Includes config.xml, complete plugin directory, an individually verified old plugin DLL, compose.yaml, and both SQLite databases. File hashes, uid/gid and modes are recorded. Old DLL: `dbb8637f1760756f87caf5b2dcc0b3e728fe49d2b9fd2576155e57f46e505cf9`. Config and databases retain source modes/owners behind the root-only parent. Initial plugin-directory copy preserved modes but not owners; readback caught this. Ownership was corrected only in the external backup; final readback verifies every plugin file's bytes, mode and owner. No restore was performed.

Production DLL still matches the old identity. The container reports StartedAt `2026-11-14T18:42:37.252678008Z`, RestartCount 0. All sandbox and intermediate artifacts remain; no cleanup, PR or push.

## Reproduction

Use a fresh external copy of the exact host and verify its identity first:

```sh
dotnet build src/Lidarr.Plugin.Bandcamp -c Release -p:LidarrBinaryPath=/absolute/path/to/copied/host
dotnet test tests/Lidarr.Plugin.Bandcamp.Test -c Release -p:LidarrBinaryPath=/absolute/path/to/copied/host
dotnet test tests/Lidarr.Plugin.Bandcamp.CookiePush.Test -c Release
python3 -m unittest discover -s tools/bandcamp-evaluation
```

The probe source is `tools/native-compatibility/Program.cs`; `native-host.c` intentionally targets `/host/Probe.dll`. Build the probe with the same LidarrBinaryPath. In a new external rootfs, supply exact host DLLs/.so, musl /lib, /usr/lib, ICU data, Probe.dll, Moq.dll and Castle.Core.dll. Copy Lidarr.runtimeconfig.json/deps.json to Probe.runtimeconfig.json/deps.json. Compile the C host on media-vm using `cc native-host.c -ldl -o probe-host`. Run only inside the confinement described above:

```sh
systemd-run --unit=UNIQUE-PROBE-UNIT --wait --pipe --collect \
  -p User=65534 -p RootDirectory=/absolute/external/rootfs -p WorkingDirectory=/host \
  -p MountAPIVFS=yes -p PrivateNetwork=yes -p RestrictAddressFamilies=AF_UNIX \
  -p ProtectHome=yes -p ProtectSystem=strict -p PrivateTmp=yes -p PrivateDevices=yes \
  -p NoNewPrivileges=yes \
  /lib/ld-musl-x86_64.so.1 /host/probe-host . ./Lidarr.Plugin.Bandcamp.dll
```

`prepare-backup.py` is effectful only in its external backup directory and must not be rerun as a read-only check. `validate-preparation.py` is the read-only metadata validator for this retained packet. See `future-install.md` for unexecuted future actions.
