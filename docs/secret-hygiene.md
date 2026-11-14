# Local credentials and verification limits (#53)

The host-authenticated provider POST action `clearLocalCredentials` clears the selected
saved definition only. For download clients it clears Email, Password, VerificationCode,
Cookies and challenge metadata. For indexers it clears the legacy Cookies field. It
preserves the Shared Authentication selector and unrelated settings. Clearing a consumer
does not follow its selector or clear the selected owner. To clear the owner and inactive
legacy copies, invoke the action on each intended definition deliberately.

Use Lidarr's existing provider action API: POST
`/api/v1/downloadclient/action/clearLocalCredentials` or
`/api/v1/indexer/action/clearLocalCredentials`, with the selected saved provider's `id`,
`implementation`, `configContract` and nonsecret required provider metadata in the JSON
body and the host API key supplied privately in its normal header. No secret field values
are needed. Check `success` and reread saved settings; storage failure does not confirm
clearing. This is an API action, not a verified native UI button. Host authentication,
API request binding and container discovery still need native Lidarr qualification.

Clearing does not revoke Bandcamp server sessions, log out browsers, erase backups or
remove copies in other definitions, in-flight request/session snapshots, existing logs,
OS crash dumps or host/API snapshots. Stop active operations before deliberate clearing
if those snapshots matter. A cleared shared consumer can still use its selected owner's
session. A cleared owner cannot supply its old cached cookie on subsequent resolution;
comparison against saved credentials prevents stale replacement from restoring it.

Secrets retain Lidarr's provider-settings threat model: DB, API-key and backup access
can expose them. PrivacyLevel.Password is presentation masking, not proven encryption.
This change makes no stronger at-rest, memory-erasure or server-revocation claim.

Plugin HTTP logs exclude URLs and raw upstream exceptions; failure exceptions have fixed
messages and no secret-bearing inner exception. API parse diagnostics exclude raw parser
exceptions and signed URLs. Download purchase-resolution failures exclude signed URLs.
Fixture tests capture actual plugin NLog output, exceptions, state diagnostics and helper
stdout/stderr and reject sentinel secrets, stable prefixes, SHA-256 values and their prefixes.
These are controlled-boundary tests, not proof that arbitrary host instrumentation is safe.

The plugin's production HTTP constructor executes through its concrete
`BandcampHttpDispatcher` and the host rate limiter, bypassing host `IHttpClient`.
Inspected Lidarr source e42a7ca4f unconditionally traces request and response strings
inside private `HttpClient.ExecuteRequestAsync`; request flags cannot prevent full signed
URLs or non-HTML error bodies appearing there. Container-resolved regression tests retain
that original-host exposure as a positive control, then capture Trace diagnostics from the
plugin path through the actual managed dispatcher with sentinel signed URLs and error bodies.
Both normal and raw execution exclude the values, tested stable prefixes, SHA-256 values and
hash prefixes. Global host logging and other HTTP clients are unchanged.

The boundary retains host proxy/TLS configuration, timeout, streaming and rate limiting,
but deliberately does not run host request interceptors, cookie storage or redirect processing.
Errors cross the plugin boundary with fixed messages and no upstream inner exceptions.
Collection/file 429 responses update the observed owner's shared wait; both consumers and
already-issued session scopes stop new requests until due. Valid date/delta Retry-After values
are honored; malformed/missing values retain the five-minute fallback. Requests already on
the wire cannot be recalled. The host rate-slot wait rechecks the shared gate before dispatch.

This proves the controlled plugin/host seam against the inspected sources, not arbitrary host
instrumentation, native container discovery or current live Bandcamp behavior. DB/API/backup
protection and native runtime behavior remain unqualified. See
[review-fix evidence](verification/review-fix-pass.md).

Regression evidence covers public collection search (not catalogue search), real fixture
archive/file output, pre-grab refusal, renewal failure, retry exhaustion, storage errors,
restart persistence and cancellation. Synthetic tiny FLAC/ZIP bytes prove file flow, not
media quality or current Bandcamp interoperability. No live account was accessed here.
See the [opt-in canary protocol](web-canary.md); #47 stays INCONCLUSIVE.
