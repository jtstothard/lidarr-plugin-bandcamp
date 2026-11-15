# Approved 2e1b47a8 deployment and guarded attempt

The user approved the pinned candidate, one restart and one fresh Dark Matter FLAC
grab with failed-download retries paused. The reviewed deploy.py passed all guards,
staging/stop/replace/start/readiness phases and exited 0. The actual
[dispatch receipt](approved-deploy-dispatch.jsonl) is retained. Installed SHA256:
`2e1b47a848abfb74d6e261b4f44c405ca1c176c368ff8d2f50f924ed3b7c2625`.

Both native provider Tests returned HTTP 200, with owner/indexer mapping intact.
Fresh native search returned one accepted FLAC release. Before dispatch, all 20 old
grabs were terminal failures, no target search command was active, the target's four
tracks had no linked files, and artist 268 had no track files. Current DLL identity
and FLAC fragment were checked; complete config/history snapshots and an exclusive
dispatch marker were saved privately. AutoRedownloadFailed=false was independently
read back equal before one POST.

The POST returned 200, new download ID `d33313c19c354582a0b6527a2aeb9439`, grabbed at
14:46:50 UTC. It failed at 14:46:55 UTC, history 2377412:

```text
Received HTML response instead of expected audio archive (HTTP 200). Inspect the resolver response; this alone does not establish an expired session.
```

No native grab or resolver request was repeated. The new attempt was terminal; all
21 total grabs had failure records and no target command was active. At 14:49:18 UTC
the complete original retry config was restored exactly (HTTP 202). See
[terminal receipt](approved-native-terminal.json). No import occurred. The new artist
and target EP remain unmonitored. The approved 2e1b47a8 DLL remains installed; no
rollback was requested or attempted.

Protected history/config/dispatch data remains under
`/srv/docker-state/rollback-preparation/lidarr-resolver-pointer-20261115T142709Z/approved-native-attempt`.
No raw response body, signed URL or cookie was exposed. The redirect-pointer correction
did not establish native download success. The previous location-only observation was
insufficient to diagnose the remaining request-path problem.
