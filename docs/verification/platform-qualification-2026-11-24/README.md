# Windows and Linux helper qualification, web canary and offline hygiene (2026-11-24)

Jay authorised these runs on 2026-11-24: helper renewal on the Windows PC and on the
`t3-dev-linux` VM against the live Lidarr owner (with a backup of its setting first),
the bounded #53 web canary on his own account, and the offline secret-hygiene checks.
No receipt here contains an API key, login, email address, cookie, identity, prefix or hash.

## #51: renewal helper on Windows and Linux

Server: live Lidarr 4.0.0.5236 over `https://lidarr.trshpotato.win` (normal certificate
validation; the `/api` path no longer needs `--authelia`). Installed plugin DLL SHA-256
`63d53c3b6bce04c6b22ba76434da173a34fa846a5a27437cf421f466d3b55a27`. Owner download client 12
(`Bandcamp`), expected fan ID 67857. Helper built from `21172c7` (main `b86122c` plus the
harness below).

| Step | Windows 10.0.26200 x64, .NET 8.0.25 | Ubuntu 24.04.5 x64 (VM 107, Xvfb), .NET 8.0.34 |
| --- | --- | --- |
| Documented build (`dotnet build src/Lidarr.Plugin.Bandcamp.CookiePush`) | Fails with the system SDK 8.0.419: `global.json` pins 10.0.103 | No SDK installed; same pin applies |
| Build with user-local SDK 10.0.103 | 0 warnings, 0 errors | 0 errors |
| Chromium install | `playwright.ps1 install chromium`, exit 0 | bundled Node CLI `install chromium` + `install-deps` (no PowerShell) |
| `--help` on the .NET 8 runtime | exit 0 | exit 0 |
| `--session-status` (read-only) | `Valid`, exit 0 | `Valid`, exit 0 |
| Isolated login opened, then cancelled before login | exit 1, profile created then removed, no window left | exit 1, profile created then removed, no browser or Xvfb left |
| Fresh login: credentials submitted once | reCAPTCHA image challenge; no person solved it in 180 s; cancelled | reCAPTCHA image challenge (headless Xvfb, nobody can see it); cancelled after 60 s |
| Temporary `bandcamp-cookie-push-*` profiles left | 0 | 0 |
| Owner setting after the runs | unchanged | unchanged |

Receipts: [windows/](windows/), [linux/](linux/). The owner row hash was compared before and
after each run on media-vm; both login attempts stopped before any identity existed, so the
helper never began a renewal operation and Lidarr was not written. Root-only backups of the
Bandcamp provider rows were taken first at
`/srv/docker-state/rollback-preparation/bandcamp-platform-qual-20261124T172454Z-pre-linux-login/`
and `.../bandcamp-platform-qual-20261124T172620Z-pre-windows-login/`.

**Stop point.** On both platforms Bandcamp answered the single credential submission with a
reCAPTCHA image challenge. The helper is designed for the user to complete that personally,
and #51 forbids CAPTCHA solving, so the runs stopped there. A completed login-to-commit renewal
on Windows and Linux still needs Jay at the browser.

What this run does qualify on Windows and Linux: building and running the helper on the
.NET 8 runtime, HTTPS session status against the live owner, headed isolated-profile launch with
navigation to the trusted login page, cooperative cancellation before login, cancellation after
credential submission while a challenge is open, and temporary profile cleanup in both cases.

To finish on Windows (PC, everything is already built under `C:\Users\Jay\bc-qual`):

```powershell
pwsh -NoProfile -File C:\Users\Jay\bc-qual\run-qual.ps1 -Step login
```

It reads the API key and the saved Bandcamp login with Jay's own `op` (1Password prompt), fills
the login once, then waits up to nine minutes for him to complete the CAPTCHA. Keep the printed
operation ID; `-Step renewal-status -OperationId <id>` reconciles it. Linux needs a display Jay
can see (a Linux desktop, or a VNC view of the Xvfb session on VM 107) and `run-qual.sh login`
with a private `secrets.env`; the secrets file used today was deleted after the runs.

### Harness

[`QualCheck/`](QualCheck/) runs the production `CookiePushCommand` and `PlaywrightCookieCapture`
with the production browser factory. It adds only observation, a single credential fill on the
trusted login page, and cancellation through the same token Ctrl-C cancels. `cancel` mode cancels
eight seconds after the login page loads. `login` mode fills and submits once, watches for 20 s,
and if no identity was issued leaves the window for `QUAL_HUMAN_WAIT_SECONDS` before cancelling.
It passes `TextReader.Null` as stdin, so a redirected agent shell cannot turn into manual import.
[`run-qual.ps1`](run-qual.ps1) and [`run-qual.sh`](run-qual.sh) are the wrappers used on each host.
For today's unattended Windows run the secrets reached the PC as RSA-OAEP ciphertext for a key
generated there, and both files were deleted afterwards; the committed script uses `op` instead.

## #53: bounded web canary

Authorised by Jay on 2026-11-24 for his own Bandcamp account, read-only. Runs on VM 107
(`t3-dev-linux`) as a systemd timer, outside CI:

- Window 2026-11-24T17:27Z to 2026-12-01T18:00Z. Hourly until 2026-11-25T06:00Z, then every
  six hours (00/06/12/18 UTC). One GET to `https://bandcamp.com/` per run (`--max-requests 1`),
  at most 40 runs in all.
- The series stops itself, disables the timer and deletes the identity file at the end date, at
  the 40-run cap, or after the first outcome that is not `Valid`.
- The identity is the live owner's current minimal identity, copied once from media-vm straight
  into a mode-600 file on the VM. The canary never saves response cookies.
- Files on the VM: `/usr/local/sbin/bandcamp-web-canary-run` ([source](canary-run.sh)),
  `/etc/systemd/system/bandcamp-web-canary.{service,timer}` ([service](bandcamp-web-canary.service),
  [timer](bandcamp-web-canary.timer)), results in `/home/t3dev/bc-qual/canary/results.jsonl`.
  Stop early with `systemctl disable --now bandcamp-web-canary.timer` and delete
  `/home/t3dev/bc-qual/canary/identity`.

First result ([canary/results-2026-11-24.jsonl](canary/results-2026-11-24.jsonl)):
2026-11-24T17:27:15Z, HTTP 200, `Valid`, no `identity` Set-Cookie. A single unchanged cookie is
not evidence against rotation. Rotation, signed-out and challenge states stay UNKNOWN until the
series observes them; the results file is to be committed when the series ends.

## #53: offline secret hygiene

[offline/secret-hygiene-2026-11-24.txt](offline/secret-hygiene-2026-11-24.txt), Mac, SDK
10.0.103, Lidarr source `e42a7ca4f` with the CI assembly-version pin:

- Secret-hygiene, host-logging and credential-login fixtures: 103 passed, 0 failed.
- Helper tests: 52 passed. Full `Bandcamp` suite: 367 + 52 passed.
- `tools/bandcamp-evaluation` Python tests: 6 passed. `web_canary.py` without `--live` prints
  the plan and makes no request.
