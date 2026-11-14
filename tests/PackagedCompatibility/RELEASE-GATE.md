# Distribution compatibility release gate (#58)

The release job depends on successful `build` **and** `known-bad-compatibility-control`
jobs. Unit tests remain in `build`. That job packages once, executes the complete
behavior matrix and supplementary scanner on the ZIP, and uploads that ZIP with its
receipt. `publish` downloads those bytes, verifies ZIP/DLL SHA-256 and complete passing
coverage against the checked-in matrix, then publishes that ZIP. There is no rebuild in
`publish`, no failure waiver, and no publication step in another job. Failure, cancellation
or skipping of a required job prevents publication through GitHub Actions' default
successful-needs condition. Upload and release reject missing files.

`ruby tests/PackagedCompatibility/verify_workflow.rb` checks that topology and rejects
four mutated workflows (either dependency removed, build failure waived, gate failure
waived). This is configuration verification, not an observed GitHub release run.

## Reproduce without publishing

Prepare the pinned source references exactly as in [SUPPORT.md](SUPPORT.md), then:

```sh
dotnet build Lidarr.Plugin.Bandcamp.slnx -c Release -p:TreatWarningsAsErrors=false -p:Version=0.0.0-compat58
dotnet test tests/Lidarr.Plugin.Bandcamp.Test/Lidarr.Plugin.Bandcamp.Test.csproj -c Release --no-build --logger 'trx;LogFileName=issue58.trx'
mkdir -p artifacts
python3 - <<'PY'
import zipfile
with zipfile.ZipFile('artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip', 'w') as z:
    z.write('src/Lidarr.Plugin.Bandcamp/bin/Release/net8.0/Lidarr.Plugin.Bandcamp.dll',
            'Lidarr.Plugin.Bandcamp.dll')
PY
ruby tests/PackagedCompatibility/verify_workflow.rb
python3 tests/PackagedCompatibility/test_gate.py
python3 tests/PackagedCompatibility/test_scanner.py
python3 tests/PackagedCompatibility/gate.py artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip --receipt artifacts/release-gate.json
python3 tests/PackagedCompatibility/verify_distribution.py artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip artifacts/release-gate.json
python3 tests/PackagedCompatibility/test_distribution.py artifacts/Lidarr.Plugin.Bandcamp.net8.0.zip artifacts/release-gate.json
python3 tests/PackagedCompatibility/run.py
```

`gate.py` has no scenario-selection option: release checks always execute healthy,
warning, restored, disabled and multiple for **both** pinned runtimes in fresh processes.
It checks exactly the one-DLL distribution format currently published by this repository;
extra/duplicate entries fail rather than leave distributed payloads unchecked. Neither
command builds a replacement plugin. The harness and scanner are separately built tools.
Failed matrix/scan receipts retain `passed: false` and the error. A receipt from a focused
`run_matrix.py --scenario` invocation cannot authorize publication.

## Scanner boundary

`Scanner` uses Mono.Cecil **0.11.6**, exact-version constrained and content-hash locked in
`packages.lock.json`; restore uses locked mode. It reads metadata without executing the
plugin. It resolves all direct TypeRef and MemberRef entries whose declaring assembly
scope starts `Lidarr.` or `NzbDrone.`, including field and method references. Cecil follows
base types/type forwarding as needed for resolution. Every attempted resolution exception
is an error, not a missing-member success or skipped result. Empty coverage also fails.

Resolution searches **only** the selected extracted runtime directory, by simple assembly
name (assembly binding policy and API compatibility are separate). There is no default
Cecil resolver, GAC, current-directory, NuGet or compile-side fallback. Output states scan
coverage, scanned type/member counts, missing types, missing members, resolution errors,
and the identities, paths and SHA-256s of every assembly resolved. Counts include all
metadata MemberRef entries, including generic instantiations; they need not match a
scanner that deduplicates or filters differently.

This does not validate every inherited/interface contract, reflection target, unused
AssemblyRef, native library or code path. Unreferenced dependencies are not a full closure
scan. The behavior matrix is authoritative for session health, including the reflective
constructor adapter's reason semantics. Scanner success is supplementary evidence only.

`test_scanner.py` creates disposable real metadata controls: a valid constructor, a
missing type and a missing member. It also scans the unmodified pinned v1.1.2 DLL, then
removes a transitive System.Runtime dependency and then Core to prove unresolved inputs
fail explicitly. Each outcome is asserted through the CLI.
`test_distribution.py` verifies the real passing distribution, then rejects changed ZIP
bytes, a failed receipt, incomplete behavior coverage and absent scans. These controls do
not publish anything.

## Pins and refresh

SDK **10.0.103**, no roll-forward; build/reference source
**e42a7ca4fd633e021d69da7daa0368b870b0282e**, with the documented single assembly-version
edit to **2.0.0.0**. Source projects retain their explicit package versions; the new Cecil
tools additionally lock their NuGet content hash. This is not a hermetic build or a promise
of byte-identical ZIP metadata across hosts. The actual checked artifact hashes are the
publication identity. The added download-artifact action is pinned to the v8 commit.

Runtime archives/Core hashes and release provenance remain pinned in `matrix.json`;
known-bad ZIP/DLL/runtime pins remain historical in `inputs.json`. Refresh nightly only in
a reviewed change using the procedure in SUPPORT.md: official API release metadata and
asset digest, downloaded archive SHA-256 and extracted Core SHA-256, full gate and controls.
Never retarget the historical bad control. For a Cecil update, change both exact package
versions, run `dotnet restore <project> -p:RestoreLockedMode=false` for each tool, review the
lock diffs and rerun controls plus full matrix. Build/reference source or SDK updates also
require full unit checks, controls and gate; update SUPPORT.md and workflow pins together.

## Observed local evidence, 2026-11-14

Base/confirmed ancestor: integration tip `f31b5860e410247b8674ced02adcd7cbe1ca4bdc`.
Fresh local source build on macOS arm64: **0 errors, 40 existing warnings**. Unit suite:
**143 passed, 0 failed, 0 skipped**. Host SDK/runtime: **10.0.103 / 10.0.3**.

The exact local ZIP checked by the gate:

- ZIP SHA-256: `2088aad04b9c5dc2ca04fd19c23ad7244378ffacec8f3706e5ea6acb79b7349a`
- DLL SHA-256: `3efe0fed243bb74df58978f8e8bc32a52005871301f56f08c14ac1382d6e16de`

[release-gate-evidence.json](release-gate-evidence.json) records ten successful behavioral
processes and two successful scans, each covering **88 types / 204 members**, zero missing
references and zero resolution errors. Runtime archive and resolved assembly hashes,
commands, PIDs and host information are retained; disposable paths are normalized.
[scanner-control-evidence.json](scanner-control-evidence.json) records rejected controls,
including v1.1.2's **one missing four-argument constructor**, zero resolution errors.

TDD began with the public gate rejection test failing because the command was absent;
it passed once the command rejected v1.1.2 with the exact `MissingMethodException`. The
scanner-control driver initially failed because its fixture generator was absent, then
passed with generated metadata and the real bad release. The actual known-bad distribution
is rejected by the release gate ([bad-gate-evidence.json](bad-gate-evidence.json)), while `run.py` succeeds only as an expected-failure
diagnostic (`behavioralPassed: false`). Its identities remain unchanged in `inputs.json`.
Extra-payload, scanner, transported-byte/receipt and four topology controls all passed.

No GitHub Actions run or publication was performed here. Managed IL on host .NET 10 does
not qualify the Linux native executable, .NET 8 hosting, plugin discovery or every runtime
contract. No production instance, credentials or Bandcamp/media downloads were used.
