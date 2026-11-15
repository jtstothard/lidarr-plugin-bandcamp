# Approved resolver observation and prepared pointer candidate

This is the preparation record. The pointer candidate was subsequently deployed;
its single guarded grab still failed with HTML. See [approved-run.md](approved-run.md)
and the [request-route correction](../route-candidate/README.md) for the later state.

The user approved the two-request diagnostic. The exact compiled resolver-probe.py
was dispatched once with `--live`, after confirming no prior dispatch/result marker
and the installed e91694e0 DLL hash. It made **exactly two requests**:

- Owned download page: HTTP 200.
- FLAC resolver: HTTP 302, exactly one Location classified as default-port account
  HTTPS (`bandcamp.com`). The bounded body had no recorded login/download/challenge
  markers. This does not rule out what a redirect target might display.

The script did not follow that Location, request another archive URL, save an archive,
update provider settings or dispatch another native grab. Raw URLs, cookies and bodies
were not retained. Actual nonsecret receipt:
[resolver-probe-result.json](../resolver-probe-result.json). The private dispatch/result
remain under the earlier approved-native-retry packet. No probe was replayed.

This is a **Python urllib** observation with production browser headers, not a native
managed-dispatcher receipt. It demonstrates a real resolver response shape matching
the locally reproduced omitted redirect branch; it does not establish the cause of
the previous native HTML response, or prove that this Location leads to archive bytes.

## Prepared candidate, not deployed

Current installed DLL:
`e91694e0fe1304cab7fb87e61243e5bba050fe8682ac0cd1b994dc42d00d7f9d`.
Prepared exact-target DLL:
`2e1b47a848abfb74d6e261b4f44c405ca1c176c368ff8d2f50f924ed3b7c2625`.

The candidate preserves a validated statdownload redirect Location as the archive
pointer, rather than dropping it and falling back to the original endpoint without
resolution parameters. The resolver does not follow the Location itself. The final
file-transfer boundary retains its three-hop HTTPS/domain limit and drops cookies
on origin changes. HTML diagnostics no longer imply that credential expiry is proved.

All **322 exact-target tests pass** ([output](exact-target-tests.txt)). The source
release package passed its full ten-case managed compatibility matrix and ABI scans;
those distinct source-package hashes remain in
[resolver-source-gate.json](../resolver-source-gate.json). CI build and known-bad
control were green at source head 0f4a3ec. Helper/evaluation source is unchanged.

A fresh protected backup of the current e91694e0 deployment and online DB snapshots
passed SQLite integrity checks:
`/srv/docker-state/rollback-preparation/lidarr-resolver-pointer-20261115T142709Z`.
A fresh copied rootfs under that packet contains the staged 2e1b47a8 candidate. All
388 host library hashes match the live container ([comparison](host-comparison.json)).
No old packet/root or production DLL was overwritten.

The native .NET 8.0.27 probe passed: 149 types loaded, 803 method/constructor bodies
prepared without invocation, native DI selected BandcampHttpDispatcher, default/
production config paths unavailable, and AF_INET socket creation denied. It used
the same uid65534, private-root/network and AF_UNIX-only confinement as the preceding
probe, targeting Probe.dll through the small C host, never the Lidarr apphost.
See [stdout](native-stdout.txt), [stderr](native-stderr.txt) and
[preparation.json](preparation.json). This proves native type/JIT/DI compatibility,
not live HTTP response behavior or successful import.

[deploy.py](deploy.py) is pinned to this new packet and the e91694e0→2e1b47a8 hashes.
It compiles; both deployment and inverse were exercised only in PLAN_ONLY mode.
Its identity/config/provider guards, exclusive dispatch receipt, stopped-container
check, atomic replacement, readiness verification and no-blind-inverse behavior are
unchanged. Its inverse would restore e91694e0 only, without restoring DB/config.

No deployment/restart or further Bandcamp request was performed while preparing this
candidate. Any next live run requires fresh approval: one guarded deployment/restart,
then at most one fresh Dark Matter FLAC grab with failed-download retries paused and
their exact inverse prepared. Preserve all 20 prior failed grabs; first prove closure
and the still-missing four-track target, and use a fresh interactive-search cache.
Stop at the first effectful failure, reconcile the new ID and restore the prior retry
setting after terminal closure. The preceding single-grab authority is spent.
