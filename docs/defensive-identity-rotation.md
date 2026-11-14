# Defensive identity rotation (#46 / #52)

Passive rotation is session hygiene. It cannot revive an expired session and establishes no
fixed lifetime, sliding expiration, rotation cadence or indefinite-renewal guarantee.

The existing session provider handles rotation through #49's read-only validation and atomic
saved-settings replacement. A successfully validated homepage may supply a candidate. Search,
search Test and download execution also observe responses inside a scope bound to their selected
session owner. Cookie equality never selects another owner, even if two named owners contain
the same identity. An unmapped legacy indexer has no saved shared owner and does not persist
passive rotations; select a named owner as described in [shared authentication](shared-authentication.md).

Only successful HTTPS responses from `bandcamp.com` on its default port are eligible. The
request must carry the still-current, recently validated identity. A homepage must positively
validate before supplying a candidate. Unauthenticated requests, redirects and error responses
cannot supply candidates; signed-out, challenge and error candidate validations cannot replace
an identity. The HTTP wrapper injects
cookies only for that account origin, disables automatic redirects, and disables the host
request/response cookie jar so it cannot forward stored account credentials to artists, CDNs
or unrelated hosts. Public artist metadata and archive requests do not receive the account cookie.

Extraction is deliberately conservative: exactly one changed `identity`, a valid nonempty
cookie value, `Secure`, and an explicit `Path=/`. An absent Domain is host-only; an explicit
Domain must be `bandcamp.com` or `.bandcamp.com`. Duplicate identity cookies, duplicate attributes,
malformed or unknown attributes, invalid scopes, and deletion cookies are ignored. Recognized
optional attributes are HttpOnly, SameSite, Expires and Max-Age. Expiry attributes only reject
malformed/deletion candidates; they never determine session liveness or schedule renewal.
Unchanged identities and unrelated cookies cause no validation or persistence. A replacement
saves only `identity=<value>`; no response attributes or unrelated cookies enter saved Cookies.

The candidate must pass the normal homepage fan-ID check before persistence. Candidate
validation does not recursively process more rotation headers. A detached settings write compares
the prior identity and credentials, then activates only after saving succeeds. Failed validation,
cancellation before commit, storage failure or a competing replacement retain the previous
identity. Typed status distinguishes validation, storage and replacement-conflict failures. A
successful already-started request may finish using its original session snapshot; subsequent
operations resolve the newly saved identity. The existing single download renewal/retry contract
remains in place.

The transaction coordinates plugin writers within one process. It does not promise database
compare-and-swap against arbitrary host/API edits or other processes. Existing host settings,
API and backup secret exposure still apply. No new password-login engine or secret store is added.

Regression evidence covers HTTP parsing/origin restrictions, owner isolation, cancellation,
validation/storage/concurrency failures, serialized process re-read, and rotation observed during
indexer search or download followed by search and a real fixture-byte download through the same owner.
Fixtures are synthetic headers and recorded homepage/archive responses; they do not qualify live
Bandcamp behavior. No live account action or deployment is part of this change.
