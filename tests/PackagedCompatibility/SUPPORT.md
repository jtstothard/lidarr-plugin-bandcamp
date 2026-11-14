# Session-health runtime support (#57)

The corrected plugin supports the two exact Lidarr builds below. The oldest supported
build is **3.1.4.5029**. The pinned current nightly is **3.2.1.5196** (the newest public
release entry on 2026-11-14). No compatibility claim is made for earlier builds,
intervening builds, future nightlies, or other runtime contracts merely because their
version falls between these endpoints. Add a build to the tested matrix before claiming
support for it. Plugin v1.1.2 remains incompatible with 3.2.1.5196.

| Lidarr build | Health warning constructor | Managed packaged scenarios |
| --- | --- | --- |
| 3.1.4.5029 | legacy four arguments | healthy, warning, restored, disabled, multiple |
| 3.2.1.5196 nightly | five arguments including reason | healthy, warning, restored, disabled, multiple |

[matrix.json](matrix.json) pins official Linux x64 archives, release/asset IDs, release
commits, archive hashes and extracted Core hashes. Each scenario uses a fresh process,
the same packaged plugin DLL, and only that build's application assemblies. On macOS
arm64 this executes their managed IL under host .NET 10.0.3; it does **not** establish
native Linux startup, plugin discovery, or the archive's .NET 8 host behavior. Assembly
binding accommodation and member compatibility are separate; a pinned assembly version
alone cannot repair a removed constructor.

## Constructor policy

Keep both generations behind `BandcampHealthResult`. The adapter discovers the reason
type in the actual loaded Core assembly and selects the exact available signature. The
modern type/member is never a compile-time reference; the removed legacy constructor
is never a direct call in the plugin's IL. Missing reason values or unexpected signatures
fail explicitly rather than choosing an accidental enum default or swallowing an error.
The unchanged one-argument constructor creates healthy results.

The modern warning uses `DownloadClientStatusSingleClient`, Lidarr's reason for warnings
naming unavailable download clients. This also applies to several named Bandcamp clients:
the check cannot conclude that **all Lidarr download clients** are unavailable, so
`DownloadClientStatusAllClients` would be misleading. Both generations use the download
client failures wiki fragment. The packaged assertions verify the modern reason by name,
warning severity, client names and the independently specified expired-cookie description.
The restored scenario checks warning and OK on the same health-check instance, separated
by successful real-provider validation through the fake HTTP boundary.

Session providers, typed problems, settings and renewal ownership are unchanged. No
credentials, schema migration, Lidarr service, Bandcamp network calls or media downloads
are needed. Only pinned public source/release assets and build dependencies are fetched.

## Reproduce the build and package

Use SDK **10.0.103** (`global.json`, no roll-forward). Build references are the Lidarr
plugins commit **e42a7ca4fd633e021d69da7daa0368b870b0282e**, not a moving branch.
CI pins this same commit. In a fresh checkout, prepare the ignored `ext/Lidarr` directory:

```sh
git init ext/Lidarr
git -C ext/Lidarr remote add origin https://github.com/Lidarr/Lidarr.git
git -C ext/Lidarr fetch --depth 1 origin e42a7ca4fd633e021d69da7daa0368b870b0282e
git -C ext/Lidarr checkout --detach FETCH_HEAD
python3 - <<'PY'
from pathlib import Path
p = Path('ext/Lidarr/src/Directory.Build.props')
s = p.read_text()
assert s.count('<AssemblyVersion>10.0.0.*</AssemblyVersion>') == 1
p.write_text(s.replace('<AssemblyVersion>10.0.0.*</AssemblyVersion>',
                       '<AssemblyVersion>2.0.0.0</AssemblyVersion>'))
PY
dotnet build Lidarr.Plugin.Bandcamp.slnx -c Release -p:TreatWarningsAsErrors=false -p:Version=0.0.0-compat57
dotnet test tests/Lidarr.Plugin.Bandcamp.Test/Lidarr.Plugin.Bandcamp.Test.csproj -c Release --no-build --logger 'trx;LogFileName=issue57.trx'
mkdir -p artifacts
python3 - <<'PY'
import zipfile
with zipfile.ZipFile('artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip', 'w') as z:
    z.write('src/Lidarr.Plugin.Bandcamp/bin/Release/net8.0/Lidarr.Plugin.Bandcamp.dll',
            'Lidarr.Plugin.Bandcamp.dll')
PY
```

Do not reinitialize or overwrite someone else's existing reference checkout. Verify its
HEAD and local assembly-version edit first. This pins SDK, source references and explicit
package versions; it does not claim hermetic NuGet availability or identical ZIP timestamps
across machines. The tested ZIP and DLL hashes are recorded for each invocation.

## Commands for #58

From the repository root:

```sh
# Retained exact v1.1.2 / 3.2.1.5196 control, including missing-Core rejection:
python3 tests/PackagedCompatibility/run.py
# Supply the actual ZIP intended for distribution. This command never rebuilds it:
python3 tests/PackagedCompatibility/run_matrix.py artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip --receipt artifacts/compatibility.json
```

`run_matrix.py` exits nonzero for a hash mismatch, missing dependency, failed process or
failed behavioral assertion. Successful output records the ZIP/DLL identities, all pinned
inputs, host version, process IDs, loaded application assembly identities/paths/hashes,
and observable results. Paths under disposable directories are normalized in the receipt.
`--scenario healthy` (repeatable) supports focused diagnostics; release qualification
must omit this option so all five scenarios run. Cached archives are hash-checked every use.

#58 should package once, run this command against that ZIP, retain its receipt and hashes,
run the supplementary direct-reference scanner, and publish those same checked bytes only
if all gates pass. This implementation does not add publication gating or the scanner.
A scanner cannot verify the reflective adapter's semantic reason selection; the behavioral
matrix is authoritative. Missing scanner dependencies must still fail #58's scan.

Refresh the nightly in a reviewed change: resolve the newest official release via GitHub's
release API, record version, commit, IDs and publication time, verify the downloaded asset
against its API digest, compute the extracted Core hash, update `matrix.json`, and run the
full matrix on the distribution artifact. Keep `inputs.json` and `evidence.json` as the
historical v1.1.2 control; do not move that control to a newer nightly.

## Observed evidence (2026-11-14, macOS arm64)

- `red-evidence.json`: source at integration tip 4125c79, packaged before the fix;
  3.1.4.5029 healthy/warning passed, then 3.2.1.5196 healthy failed with the exact
  missing four-argument constructor. The receipt records that failing invocation.
- `matrix-evidence.json`: corrected package; all ten runtime/scenario invocations passed.
  Restored records `warningBeforeRestore: true`; disabled returns OK despite a recorded
  problem on an inactive client; multiple names both affected clients and their causes.
- Original `evidence.json` and `inputs.json`: retained unmodified diagnostic and hashes.
  The exact control was rerun after extending the harness and passed again, with
  `behavioralPassed: false`; this is a diagnostic success, not healthy behavior.
- Ordinary unit suite: **143 passed, 0 failed, 0 skipped**. Source build: 0 errors;
  existing upstream obsolescence/style and test analyzer warnings remain. Harness build:
  0 warnings, 0 errors. CI/Linux execution has not been observed for this change.
