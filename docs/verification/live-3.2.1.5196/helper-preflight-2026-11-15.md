# Visible helper preflight — 2026-11-15

The working deployment was preserved. Read-only SSH inspection confirmed DLL SHA256
`9e109bdd7d8b0c8ff417a8e5e4af36b393d1b2c6635ea58ae126f80bfbbd447c`, container running,
and unchanged StartedAt `2026-11-15T11:17:47.702317021Z`. Masked API presence checks
confirmed owner download client 12 (`Bandcamp`) retains cookie and credential pair;
indexer 26 retains its cookie. Both shared selectors remain empty. No secrets were printed.

Helper Release build passed with zero warnings/errors; all 30 helper tests passed.
The installed Playwright Chromium revision 1248 matches the helper's 1.64.1 dependency.
PowerShell is absent. The bundled Node driver can inspect/install browser prerequisites.
Only .NET 10.0.3 is installed: the default net8.0 executable invocation fails before
startup. The installation documentation now states the actual .NET 8 runtime requirement.

A temporary standalone harness called the actual `PlaywrightCookieCapture`, using
explicit `DOTNET_ROLL_FORWARD=Major`. Instrumentation delegated to the real internal
browser factory and recorded `HEADED_BROWSER_LAUNCHED`, `LOGIN_DOMCONTENTLOADED`,
`CANCELLED`, and `NEW_PROFILES_REMAINING=0` after a 20-second deadline.
No login, candidate submission or Lidarr persistence occurred. This proves native
headed browser launch, login DOM load and cooperative cancellation/profile cleanup on
this Mac under .NET 10 roll-forward. It does not prove visual correctness, completed
login, CAPTCHA/2FA, server cancellation races or successful renewal.
Harness retained in temporary directory `bandcamp-helper-smoke-k4eazeqx`.

Read-only access to the existing owner's real saved identity, held only in process
memory, resolved expected fan ID 67857 from Bandcamp HomepageApp. This precedes and
is independent of the proposed new browser candidate. A bounded collection request
returned 20 entries. The user permits choosing any available owned album; shortlisted
`Pocket` by Pocket, https://pocketpunk.bandcamp.com/album/pocket . Archive availability,
FLAC bytes, actual download and import remain unverified.

The user supplied `https://lidarr.trshpotato.win/`. Normal certificate verification
passes. Root navigation redirects to Authelia; requests to the status API with the
real Lidarr key return HTTP 403 from both media-vm and this Mac without redirects.
The helper disables cookies and redirects; browser Authelia authentication alone is
not yet evidence of a usable helper transport. The user instructed use of Authelia
login. A visible isolated agent-browser session was opened at that login after two
T3 preview snapshot timeouts. Authentication and transport resolution remain pending.
No proxy policy was changed and no browser session was exported.

Follow-up: the user identified existing 1Password credentials. Desktop-integrated
CLI access succeeded. The saved Authelia login was passed in memory through stdin
to the isolated browser; Authelia accepted it and redirected to Lidarr's login.
The saved Lidarr login was then used the same way. No password, OTP, API key or
session cookie was printed or placed in command arguments/source files. From the
authenticated browser, a same-origin status request without an API key returned
401; the same request with the real key supplied in memory returned HTTP 200 and
version 3.2.1.5196. Thus browser HTTPS/API access is proven. The production helper's
cookie-disabled HttpClient still cannot inherit that Authelia session; the plain
helper transport remains blocked. Browser API success is not CLI qualification.

Fresh origin fetch confirms #59 merged as `e51a1d5`; #55 remains draft at
`72c2776`, nine commits behind local integration HEAD. Read-only `git merge-tree`
predicts conflicts in README and BandcampSessionHealthCheck. Any eventual merge
must retain #59's BandcampHealthResult adapter and packaged release gates while
preserving shared-session health behavior. No merge, push or deployment was done.

Next concrete sequence: resolve authenticated HTTPS transport; finish local runtime
prerequisites; user completes isolated Bandcamp login for owner 12 with expected fan
67857; reconcile the single operation receipt; explicitly map indexer 26 to owner
`Bandcamp` while owner selector stays empty; test shared search and selected FLAC
download/import. New session replacement and shared-settings edits still require
the handoff's specific live-action approval. Never invalidate the working identity
to manufacture a recovery test or replay an ambiguous operation.

## Authelia helper implementation and native pre-login check

Added explicit `--authelia` with environment-only session value, exact protected
Lidarr HTTPS origin binding, and an optional restricted cookie name. It supplies
one Cookie header only to the existing Lidarr HttpClient. It does not change the
isolated Bandcamp browser, provider settings, TLS verification, redirect refusal,
or response-cookie refusal. Authentication applies equally to status and the
existing detached cancellation reconciliation path. No new dependencies were added.

At the existing command/HTTP seam, the first Authelia status test failed before
implementation (expected exit 1 for acknowledged cancellation, actual usage exit 2).
After implementation, the complete helper suite passed: 49 tests, zero failures or
skips. Cases include missing/mismatched/malformed origins and session headers,
non-opt-in behavior, redirect refusal, real transport cookie/redirect flags, and
lost-submission reconciliation without candidate replay or secret diagnostics.
`git diff --check` passed. This was a local source review, not an independent review.

The actual Release helper CLI was invoked with `--authelia --client-name Bandcamp
--fan-id 67857` and explicit .NET 10 major roll-forward. A parent Python process
obtained only the secure HttpOnly `authelia_session` from the dedicated Authelia
browser and the current Lidarr API key through SSH; both remained in process memory
and the child environment. Neither was printed or supplied in process arguments.
No LidarrAuth cookie or complete browser profile was transferred.
After 20 seconds the parent sent SIGINT; the helper exited 1 cooperatively:

```text
Opening isolated Bandcamp login. Complete login and verification personally.
Renewal did not confirm success. Check owner selection, expected fan ID, HTTPS/API access and Lidarr status. If submitted, reconcile saved status before retrying.
```

Reaching the browser proves the actual helper passed HTTPS/API/owner selection
through Authelia. No login was entered and no renewal operation ID was emitted.
No new helper profile remained. One inactive profile predating this work
(created 2026-11-14 at 20:47 local time) was observed and preserved.
This supersedes the transport blocker above for explicitly supplied valid Authelia
sessions. It does not qualify completed Bandcamp login, candidate validation,
replacement, persistence, native post-submit cancellation or shared-consumer recovery.

An owned Pocket download page was inspected read-only: it offers FLAC, advertised
201.8 MB; no archive URL was fetched. Live Lidarr metadata identifies artist 263,
album 235472, download output `/data/downloads/bandcamp`. The album already has eight
FLAC files. Do not overwrite those to force an import qualification. A bounded
read-only attempt to select an owned missing alternative returned non-JSON and
stopped without retry; no new account state is inferred from that parser failure.
Pocket can still qualify archive/output and duplicate handling, but successful new
library import needs a suitable target or separately agreed handling of existing files.

Ready next: one approved helper renewal for saved owner 12, expected fan 67857,
then indexer 26 mapped to exact owner `Bandcamp`, retaining its legacy fields and
leaving the owner selector empty. The user must personally complete the visible
Bandcamp login/challenge. Preserve the operation ID and reconcile uncertainty; no
replay or deliberate credential invalidation. No live settings edit or deployment
was performed during this implementation.

## Approved live renewal checkpoint — 2026-11-15, 12:08 UTC

The user explicitly approved one owner renewal and shared mapping/search/download,
then instructed use of the saved Bandcamp login in 1Password. A new private snapshot
of the exact owner/indexer database rows was saved under
`/srv/docker-state/rollback-preparation/bandcamp-shared-renewal-20261115T115346Z`
(directory 0700, settings file 0600). The deployed DLL hash was unchanged.

The first ordinary CLI run was cooperatively cancelled while still waiting for
login; exit 1, no renewal operation ID. Native desktop control could not select the
helper process independently of another Chromium process with the same app identity.
A temporary .NET 10 qualification runner was therefore built around the production
CookiePushCommand, PlaywrightCookieCapture and PlaywrightBrowserSession. Its factory
uses the production headed/download-disabled/service-worker-blocked launch options
and the production session's network restrictions. Its only added behavior is
filling the saved login from process memory; no credentials are in source or arguments.
This is an instrumented native runner, not a claim that the ordinary CLI autofills.

The first autofill attempt timed out before confirming the submit action. No server
renewal operation was created. Credential-free probes reproduced an original submit
selector with zero matches. The accessible Log in button had one match, but its trial
click was blocked by the consent overlay. Accept necessary only followed by the
accessible button passed the trial click without submitting any credentials. The
original selector therefore could not click the submit button. No password retry
was used to diagnose this failure.

The corrected native runner accepted necessary cookies, filled the saved credentials
from 1Password and emitted `LOGIN_SUBMITTED_ONCE`, followed by
`VERIFICATION_FIELD_VISIBLE=False`. It is still waiting for a usable identity; no
renewal operation ID or success receipt has been emitted. No CAPTCHA/verification
was solved and no password submission was repeated after this corrected submission.
At 12:08 UTC both complete saved database rows still matched the before snapshot.
An exact identity-cookie metadata-only query of this run's own temporary profile
found no persisted identity row. That does not prove absence from browser memory.
The page's actual refusal/challenge state remains UNKNOWN; do not attribute it to
CAPTCHA, a wrong password or expiry without observing the page.

Temporary runner/probes and nonsecret sources are retained in
`/var/folders/kc/hjd9zkys3ts402ygmnrh89j40000gn/T/bandcamp-native-autofill-kysnl545`.
The corrected runner uses the existing ten-minute helper deadline. No indexer mapping,
provider Test/search, archive grab, library import, deploy or restart has followed
this pending login. Preserve the working session. Reconcile any subsequently emitted
operation ID; do not launch another password attempt blindly.

## Completed handoff, shared mapping and lossless archive

The user observed CAPTCHA and completed it in the visible isolated browser. The
instrumented native runner then emitted operation
`7d085c34-22e1-4512-a12d-c1e6e8c4a309`, the production helper's validated-and-saved
message and `NATIVE_HELPER_EXIT 0`. No CAPTCHA was automated or bypassed.
An independent native host status action for that exact owner/operation returned
`Committed`, `success: true`, no problem. No candidate was replayed.

The approved one-field PUT of indexer 26 returned 202. Database readback proved the
only changed setting was `authConfiguration: Bandcamp`, with every other column
and retained legacy setting unchanged. Owner 12 retains its empty selector.
Native owner and shared indexer Test actions both returned HTTP 200 with no failures.
The native album search for Pocket (album 235472) returned eight Bandcamp format
results, including a FLAC release of advertised size 211602636 bytes. This search
uses the host's collective interactive route, so other enabled indexers also ran;
the Bandcamp result was identified by indexer id 26. The FLAC release was rejected
by host decisions; no rejected release was force-grabbed.

Final database comparison found the entire owner row still equal to its before
snapshot. The committed handoff therefore does **not** establish cookie rotation,
a different persisted cookie or recovery from an expired session. It establishes
the real browser capture, expected-account validation and successful server receipt
for the instrumented workflow. No working identity was deliberately invalidated.
Saved mapping persistence is proven by database readback, not by a host restart.

To find a safe native import target, a bounded read of 95 collection entries was
compared with existing album metadata and library directory names. No straightforward
candidate both matched selected track counts and avoided existing files/directories.
ME REX album 1724 had no linked track files, but existing physical Triceratops and
Stegosaurus directories were present; its native Bandcamp search also returned no
results. No album monitoring/release choice or existing library file was changed.
A protected before manifest of 169 existing ME REX artist files was retained beside
the settings snapshot; it is evidence preparation, not evidence of an import.

One authorized owned FLAC archive was instead downloaded to a new isolated directory
on media-vm, outside both the library and the configured native download-client root:
`/srv/docker-state/bandcamp-lossless-qualification-20261115T122307Z/pocket-flac.zip`.
The request used the current saved owner identity, held in process memory. The flow
read the owned download page, selected only FLAC, followed bounded HTTPS Bandcamp/CDN
handoffs, and sent the identity only to exact bandcamp.com. No signed URL or cookie
was printed. The archive streamed to an exclusive new file; no native client grab,
queue entry, extraction, library import or existing-file overwrite occurred.

The archive is 211636371 bytes, SHA256
`9b4a6b37b9ae3a01a041d1c8839f8e49ae036c70583c9fc15e9ee08400084641`.
ZIP CRC validation passed. All eight FLAC members have native `fLaC` headers and
STREAMINFO indicating 44100 Hz, stereo, 24 bits/sample. Full receipt:
[pocket-flac-2026-11-15.json](pocket-flac-2026-11-15.json).
This proves original lossless archive retrieval using the current saved owner,
not execution of Lidarr's native download client or successful library import.
The unchanged deployed plugin hash was independently rechecked after these steps.

Remaining gates: ordinary CLI autofill is not implemented (1Password autofill used
a temporary instrumented runner); supported-platform packaging, forced-expiry or
real cookie rotation, native post-submit cancellation races, restart persistence,
and actual plugin download/import remain unqualified. Integration with main/#59
and pushing the unpublished integration commits remain outstanding; keep #55 draft.
