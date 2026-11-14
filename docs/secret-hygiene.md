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

Known host exposure: inspected Lidarr source e42a7ca4f logs HttpRequest/HttpResponse at
Trace in HttpClient.ExecuteRequestAsync. Request strings include full signed URLs;
HTTP error response strings can include response bodies even with LogResponseContent=false.
LogHttpError=false disables the host warning, not these unconditional trace events.
The plugin disables response-content/error-warning logging on its requests but cannot
claim host trace safety. Avoid host HTTP trace collection around credentials; this remains
a release secret-hygiene gap requiring host-level remediation and native verification.
DB/API/backup protection and runtime container/HTTP behavior are likewise unqualified.

Regression evidence covers public collection search (not catalogue search), real fixture
archive/file output, pre-grab refusal, renewal failure, retry exhaustion, storage errors,
restart persistence and cancellation. Synthetic tiny FLAC/ZIP bytes prove file flow, not
media quality or current Bandcamp interoperability. No live account was accessed here.
See the [opt-in canary protocol](web-canary.md); #47 stays INCONCLUSIVE.
