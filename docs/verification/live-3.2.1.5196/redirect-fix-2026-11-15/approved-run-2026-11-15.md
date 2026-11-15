# Approved redirect deployment and one guarded native grab

The user approved candidate `e91694e0…`, one restart and one fresh Dark Matter FLAC
grab with failed-download retries temporarily disabled. PR #55 head 0330b7f had green
build and known-bad compatibility control; publication and auto-merge were skipped.

The exact reviewed deploy.py was compiled locally and sent once to media-vm with
`--apply`. Its identity/config/provider guards passed. The exclusive dispatch receipt
records staged, stop, atomic replacement, start and verified phases; payload exit 0.
See [actual deployment receipt](approved-deploy-dispatch.jsonl).

Installed DLL SHA256:
`e91694e0fe1304cab7fb87e61243e5bba050fe8682ac0cd1b994dc42d00d7f9d`.
Container start: `2026-11-15T13:33:07.182584893Z`. The server was ready at 3.2.1.5196.
Post-restart native owner 12 and shared indexer 26 Tests each returned HTTP 200 with
no validation failures. Their saved name/selector guards passed. No credential
clearing, new login, session replacement or inverse was performed.

Fresh native search returned one accepted Dark Matter FLAC release, 175531622 bytes.
Before dispatch, all 19 old grabs had terminal failures, no target command was active,
all four target tracks had no linked file, and artist 268 had no track files. The
installed DLL and format fragment were independently checked. The complete current
download-client config and prior history were retained privately. AutoRedownloadFailed
was set false and the complete paused config read back equal before one POST.

That one agent-issued POST returned HTTP 200 and new download ID
`5df714ce9d454286ae48b72c5ead338e`, grabbed at 13:35:36 UTC. It failed at 13:35:41 UTC,
history 2377410, with:

```text
Received HTML response instead of expected audio archive. The download URL may have expired or the session cookies may be invalid.
```

This is the deployed diagnostic, not proof that the session expired or that CAPTCHA
caused the response. No raw HTML, signed URL or response cookie was captured. No
further native grab was sent. Closure readback showed exactly **20 total grabs,
20 terminal failures, one new grab and no active target command**. At 13:37:11 UTC
the complete retry config was restored exactly to its prior resource (HTTP 202),
including AutoRedownloadFailed=true. An independent later read confirmed 20/20,
zero linked target tracks, container running, and the approved DLL still installed.
See [terminal receipt](approved-native-terminal.json).

The guard prevented the previous re-grab chain. No library import occurred. The
new artist and target EP remain unmonitored. All protected operation data stays in
`/srv/docker-state/rollback-preparation/lidarr-auth-redirect-20261115T130557Z/approved-native-retry`.
The working approved DLL is left installed; no rollback was authorized or attempted.

## Local follow-up; not established as the live cause

Read-only inspection of existing logs found host logging at Info and no retained
target phase/fallback markers. The failed download's cleanup path removes its temp
file, so there is no retained HTML body supporting a specific cause. No Bandcamp
request or failed grab was replayed to investigate it.

A separate controlled regression demonstrates a resolver defect: an HTTP 302
statdownload response with a trusted archive Location produced null, causing the
proxy to fall back to the original endpoint without its resolution parameters.
The new source preserves that validated Location as the archive pointer and refuses
unsafe/missing Locations. It does not follow the Location in the resolver itself.
The file-transfer boundary retains the existing redirect limit/cookie restrictions.
The HTML error now reports the status neutrally rather than implying credential expiry.
The regression went red on null and green on the exact pointer. This proves the
local defect, **not that this particular native HTML failure traversed that path**.

All 322 exact-target plugin tests pass, including unsafe resolver-redirect cases.
The updated source is not deployed. Source-build/package verification receipts are
retained separately from the earlier e91694e0 deployment packet.

## Prepared next observation, not executed

[resolver-probe.py](resolver-probe.py) compiles and has been run only in PLAN_ONLY
mode. With separate approval, it performs at most two bounded HTTPS GETs: the saved
owned download page and one FLAC resolver request. It never follows redirect/returned
archive URLs, retries a refused request, or saves an archive. If archive bytes arrive
directly it reads only the ZIP header and closes. The resolver request may still
affect Bandcamp's download accounting/conversion, so it is a new live operation,
not covered by the now-spent one-grab authorization.

The diagnostic emits/persists only statuses, fixed content/origin classes and marker
booleans. Cookies, signed URLs, HTML and JSON pointer values stay in process memory.
An exclusive dispatch marker prevents accidental replay. It uses Python urllib with
production browser headers, **not the native managed dispatcher**: an observation
can characterize that response but must not be presented as proof of the native
transport or live root cause. A matched sanitized response can inform a subsequent
native fixture/instrumentation step. No further deployment or grab is proposed until
that uncertainty is resolved.
