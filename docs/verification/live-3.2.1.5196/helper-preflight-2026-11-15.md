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
