# Bounded web observation protocol (#53)

No live run is authorized by this ticket or this document. #47 remains INCONCLUSIVE;
no Subsonic provider is published in this integration. Its parity, generated-secret,
replacement and revocation gates remain prerequisites for any future provider.

`python3 tools/bandcamp-evaluation/web_canary.py` prints a plan and performs zero requests.
A future operator must first obtain separate explicit authorization naming the account,
read-only purpose, time window and request bound. Run outside CI on a trusted machine.
Record UTC date, plugin/host version, network context and the authorized bound without
identifying the account or credential. Do not put secret arguments in shell history.

Only after that authorization, supply a private permission-restricted file containing
only the minimal identity via `--identity-file <private-path>`, plus `--live`,
`--authorized-read-only I_HAVE_SEPARATE_AUTHORIZATION` and `--max-requests 1` (maximum 3).
The acknowledgement is an operator assertion, not a grant of authorization. With no
identity file the tool observes a signed-out request; never label it an authenticated run.
The tool makes GET requests only to `https://bandcamp.com/`, refuses redirects, uses no
cookie jar, does not save/activate response cookies, limits each response to 1 MiB and
one request to 10 seconds, and checks a 30-second budget before each request. A socket
read timeout is not a strict wall-clock deadline against a trickling server: terminate
the process at the authorized external deadline. Stop at the first non-valid outcome.
No automatic login, retry after refusal, challenge solving, replacement or revocation occurs.

Review only the fixed-schema JSON result. It excludes headers, body, fan ID, URL tokens,
identity values, prefixes and hashes. `different_identity_seen` is only a comparison with
the sent value: it does not establish candidate scope, validation, persistence, renewal,
sliding expiration or lifetime. A single unchanged cookie is not evidence against rotation.
A signed-out/challenge observation must arise through ordinary authorized use; do not
revoke a session or modify an account to manufacture it. The homepage classifier is a
bounded diagnostic approximation, not the plugin's full validation contract.

Never attach private input files, browser profiles, HARs, screenshots of secrets, raw
HTTP headers/bodies or exception dumps. Host/OS/network instrumentation is outside the
tool's output guarantee. Any account-changing or Subsonic revocation experiment needs
its own authorization and the #47 parity protocol. Record unobserved cases as UNKNOWN.
