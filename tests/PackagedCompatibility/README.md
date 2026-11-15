# Packaged session-health compatibility seam (#56)

For the implemented release-publication gate and scanner, see
[RELEASE-GATE.md](RELEASE-GATE.md). The #56 notes below describe the retained diagnostic.

For the corrected package, support matrix, reproducible build commands and #58 handoff,
see [SUPPORT.md](SUPPORT.md). The diagnostic control below remains historical #56 evidence.

From the repository root, with Python 3 and .NET SDK 10.0.103:

```sh
python3 tests/PackagedCompatibility/run.py
```

The command downloads two **public release assets**, verifies pinned SHA-256s on every
use (including cache hits), extracts only flat DLLs into a temporary directory, builds
a dependency-free harness, and starts a fresh `dotnet` process for each invocation.
No Lidarr service, production config, credentials, network session validation or downloads
are involved. The fake HTTP boundary handles only the Bandcamp homepage; other calls fail.
The only network activity is fetching the two pinned public archives.

The diagnostic exits 0 only when the unmodified v1.1.2 packaged DLL throws exactly:

```text
System.MissingMethodException
Method not found: 'Void NzbDrone.Core.HealthCheck.HealthCheck..ctor(System.Type, NzbDrone.Core.HealthCheck.HealthCheckResult, System.String, System.String)'.
```

The output retains `behavioralPassed: false`. The expected exception is a **passing
diagnostic control**, not a passing health check or compatibility qualification.
The runner also requires a healthy behavioral assertion to exit 1, exercises the same
exact diagnostic after warning/restored fixture setup, and removes Core from the disposable
runtime to prove an unrelated resolution failure cannot pass. Wrong exception types,
wrong messages, missing inputs and hash mismatches fail rather than bless the artifact.
CI runs this control independently of the source build. Release gating for the fixed
artifact and a support matrix belong to later parent tickets.

## Resolution and provenance

[inputs.json](inputs.json) records the release API URLs, release/asset IDs, publication
times, runtime release commit, immutable expected archive hashes and extracted DLL hashes.
Resolved on 2026-11-14 from the GitHub release APIs named there. GitHub's asset `digest`
matched the locally computed archive hashes. The plugin DLL hash exactly matches #54:
`dbb8637f1760756f87caf5b2dcc0b3e728fe49d2b9fd2576155e57f46e505cf9`.
No live-machine files were obtained for this ticket.

The selected **official Linux x64** 3.2.1.5196 archive has Core SHA-256
`7ca8d80b4ea680546f81b4c48787ed5a44550654e97684e3fe2465b7e7862294`.
This differs from the copied production Core hash `8d00f110...` in #54; the release
archive is a separately identified input reproducing the same ABI failure. We do not
claim those DLLs are byte-identical.

The harness has no project/package references to Lidarr, the plugin or a mocking library.
Its application load context resolves application dependencies only from the selected
runtime directory and fails closed when absent. Only assemblies from the host's shared
framework may fall back. Cross-platform framework/ReadyToRun DLLs bundled with the
Linux archive are excluded from loading; this managed seam uses the host .NET 10 runtime,
not the archive's native Linux host. It does not qualify native startup or .NET 8 hosting.
Every loaded application assembly's full identity, absolute path and SHA-256 is emitted;
application paths outside the runtime (apart from the selected plugin itself) fail.
Compile-side `ext/Lidarr` and source plugin output are never consulted.

## Reuse for #57

`SessionFixture` follows `BandcampSessionHealthCheckFixture`: real packaged registry,
provider, API client and HTTP wrapper, with only the external HTTP/client-factory and
localization boundaries substituted. `AddClient(id, name, enabled)` provides definitions
and a real provider. `ValidateAsync(provider, false)` supplies the dead homepage;
`ValidateAsync(provider, true)` supplies the live homepage through `RenewAsync` (forced
validation avoids waiting for the cache). `CreateCheck` injects those same definitions
and registry into the packaged health check. No private state or credential store is used.

`Program.cs` accepts `<runtime-directory> <packaged-dll> <healthy|known-bad>
[healthy|warning|restored]`. The healthy scenario has no problems; warning validates a
named enabled client against the fake dead homepage; restored then validates it against
the fake live homepage. All three still fail at Check() in v1.1.2. #57 can extend result
assertions and client scenarios using this fixture and loader, retaining the known-bad
control unchanged. This ticket does not implement the constructor adapter, support policy,
fixed-artifact warning assertions, disabled/multiple-client assertions, or ABI scanner.

## Local TDD evidence

On macOS arm64 with SDK 10.0.103 / host runtime 10.0.3:

- First real healthy assertion against the pinned release: exit 1, exact MissingMethodException.
- Exact known-bad diagnostic: exit 0, `behavioralPassed: false`.
- Added warning setup assertion before wiring scenarios: exit 1 (no fake homepage call).
- Wired real fixture preparation: warning has 1 fake homepage call; restored has 2;
  each fresh process reproduces the same exact Check() failure.
- Missing-Core negative control: nonzero, rejected by the runner.
- Final single command: exit 0; harness build: 0 warnings, 0 errors.

[evidence.json](evidence.json) is the actual final receipt, with ephemeral directory prefixes
normalized to `<isolated-root>`; process IDs, assembly identities and hashes are retained.
CI/Linux execution is configured but was not observed locally. Ordinary source unit tests
are unchanged and were not run: this independent project needs no source Lidarr checkout.
