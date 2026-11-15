# Format-link request-path correction

## Evidence and regression

After the 2e1b47a8 attempt failed, no native grab or resolver/archive request was
repeated. One read-only GET of the already-owned HTML download page classified the
FLAC link as **DownloadEndpoint** (`/download/…`), HTTPS, with a query. Neither the
format link nor a resolver/archive URL was requested. Raw HTML, cookies and signed
URL values were not saved. See [actual classification](owned-page-path-observation.json).

Inspection found that ResolveStatdownloadUrlAsync appended resolution parameters but
never converted that browser download path to the resolver path. The existing complete
pipeline fixture hid the transition by supplying an already-converted statdownload URL.
As an independent primary implementation reference, pinned
[yt-dlp ee2559128c7d, lines 257–266](https://github.com/yt-dlp/yt-dlp/blob/ee2559128c7ddc471de5aeaa584a9a4ae6836ec3/yt_dlp/extractor/bandcamp.py#L257-L266)
explicitly performs the download-to-statdownload path conversion; its comment identifies
the download bundle JavaScript as its protocol source. Provenance is retained in
[primary-source.json](primary-source.json). This is an implementation reference,
not an official Bandcamp API support guarantee.

The complete ExecuteDownloadAsync fixture now supplies a download format link,
requires a statdownload request to obtain the file pointer, and returns an HTML
landing page for incorrect direct requests. With that stronger fixture and unchanged
production code, it reproduced the **same HTTP 200 HTML DownloadException** as the
native run ([red output](red.txt)). The minimal path conversion made that pipeline
test pass, including actual synthetic file output and no collection-query fallback.
This identifies a concrete request construction defect matching the observed input;
live success still requires qualification.

BuildStatdownloadUrl now converts only the leading `/download/` path segment to
`/statdownload/`. It slices the original string to preserve signed query bytes,
uses the correct query separator, escapes the format value, and refuses untrusted
scheme/host/port/userinfo/fragment inputs before any request. Already-correct resolver
links remain unchanged. Tests cover encoded query preservation, download text inside
the query, a queryless link and initial-origin refusals. Existing cookie, redirect,
TLS, rate-limit and secret-diagnostic boundaries remain.

## Prepared candidate, not deployed

All **330 source-build and exact-target plugin tests pass**; see
[source tests](source-tests.txt) and [exact tests](exact-target-tests.txt).
The source package passes the full ten-case two-runtime behavior matrix and both
ABI scans ([source gate](source-gate.json)), with transported ZIP/DLL verification.
The helper and offline evaluation code is unchanged from its 49/6 passing checks.

Current installed DLL:
`2e1b47a848abfb74d6e261b4f44c405ca1c176c368ff8d2f50f924ed3b7c2625`.
Prepared exact-target DLL:
`ad312e81cbf9bcf3290aa761508fc69a9ee094b519b5c738233f54523c165f08`.
The source-package hash in source-gate.json is a distinct build, not this native packet.

Fresh protected backup:
`/srv/docker-state/rollback-preparation/lidarr-resolver-route-20261115T150021Z`.
Online SQLite snapshots passed integrity. The backed-up working DLL is 2e1b47a8,
not a pre-auth version. A fresh copied rootfs contains ad312e81; old roots/packets
and the installed DLL were not overwritten. All 388 host library hashes matched
the live container ([comparison](host-comparison.json)). The native .NET 8.0.27
network-denied probe passed: 149 types loaded, 804 method/constructor bodies prepared
without invocation, native DI selected BandcampHttpDispatcher, production/default
config paths unavailable, and AF_INET socket creation denied. Actual
[stdout](native-stdout.txt), [stderr](native-stderr.txt) and
[preparation](preparation.json) retain that scope. No Lidarr apphost was invoked.

[deploy.py](deploy.py) is the pinned deployment/inverse payload for this packet.
It compiles and was run only in PLAN_ONLY mode for both directions. The guarded
stop/atomic replacement/start and no-blind-inverse protocol is unchanged. Its inverse
restores 2e1b47a8 only; it never restores DB/config.

No further resolver/format/archive request or native grab was made while preparing
the candidate. A new approved live run must preserve all 21 failed histories, first
prove terminal closure and the missing four-track target, then perform one guarded
deployment/restart and at most one fresh Dark Matter FLAC grab with failed-download
retries paused. Reconcile that ID and restore the prior retry config after closure;
stop at the first effectful failure. Native archive/import qualification remains open.
