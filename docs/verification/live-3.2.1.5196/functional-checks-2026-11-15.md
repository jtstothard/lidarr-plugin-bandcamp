# Authorized live checks — 2026-11-15

User authorized as many checks as feasible on the deployed media-vm candidate. No provider settings changes, deliberate credential invalidation, credential clearing, archive grab, purchase or revocation was performed.

Authenticated host API POST downloadclient/test using saved definition 12 returned HTTP 200 with no validation failures. This exercises plugin session validation and configured output-directory writability; the test may create the configured directory if absent. Saved downloader had identity and complete credential fallback configured, no shared auth selector. Passwords/cookies stayed in process memory and HTTP bodies to the local host endpoint, never command arguments or output.

POST indexer/test using saved definition 26 returned HTTP 200 with no validation failures. The deployed TestConnection path resolves session and loads at most one page of downloadable collection purchases; success requires nonempty downloadable purchases. This proves native auth/collection test behavior, not artist/album matching, full collection parity or downloaded archive quality. Indexer retained its legacy identity and no shared auth selector.

Native host actions beginIdentityRenewal, cancelIdentityRenewal and identityRenewalStatus returned Pending, Cancelled and Cancelled. No identity candidate was submitted. Complete saved API definition payloads before and after were equal in memory; no payload or credential values were exported. This proves pre-validation cancellation routing and nonpersistence, not the validating/commit race or restart receipt behavior.

Two additional direct HTTPS homepage probes with minimal identity, no redirects and no password use did not resolve a numeric fan ID using the probe parser. No body/secret was exported. The reason was not established (transport/markup/challenge/parser differences remain possible); this does not negate passing native tests or establish signed-out state. Same-account identity remains unconfirmed; no repeat login or retry was attempted.

No targeted artist/album search or download was initiated: the host manual-release route searches enabled indexers collectively, while no specific owned album was selected. Shared-session mapping remains unchanged and therefore its live cross-consumer renewal is unqualified. Browser UI, storage-failure injection, real rotation, Subsonic parity/revocation and full restart persistence remain unqualified.
