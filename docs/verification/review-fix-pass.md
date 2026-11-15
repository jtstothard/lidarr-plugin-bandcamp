# Implementation review-fix pass, #46–53

Date: 2026-11-14. Starting integration commit: `0a23a771214a9efab38fefff4f0898601c647c6c`.
Single implementer; no delegates, live account actions, PR, push, or unrelated cleanup.
Read #46–53, CONTRIBUTING, glossary, accepted ADR, TDD and code-review skills, and secret hygiene guidance.
This pass uses the spec's existing HTTP/API, session, indexer fetch and downloader seams.

## Findings addressed

- **P1 HTTP diagnostics:** the production Bandcamp HTTP constructor now takes the concrete
  plugin dispatcher and host rate limiter. It never sends these requests through host
  `IHttpClient.ExecuteAsync`. The host's private execution method traces request strings
  (full URLs) and response strings (including non-HTML error bodies), irrespective of the
  request logging flags. Changing those flags was insufficient. The plugin boundary keeps
  proxy/TLS/timeout/streaming/rate-limit behavior through the managed dispatcher, excludes
  host cookie storage, redirects and interceptors, and returns fixed exceptions without
  upstream inner exceptions. It does not replace the global host HTTP client or change
  global logging. Normal and raw requests share the safe execution path.
- **P2 account ownership:** passive replacement now validates against the cached fan ID.
  A valid different fan is refused as WrongAccount, leaves the prior cookie/fan active for
  both consumers, performs no settings write, and retains the prior identity after reread.
- **P2 consumer 429:** collection, file and other observed consumer requests report 429 to
  the selected session owner before throwing a fixed failure. The owner applies its existing
  Retry-After parser/clock and shared suppression. Session Get/Renew/replacement and already
  issued observed scopes cannot start another request during the wait. The production path
  also rechecks after waiting for a host rate slot. Validation and login retain their explicit
  429 classification. Delta/date/malformed waits recover without password login or replacement.
- **Standards:** one validation-failure classification serves validation and replacement;
  their separate cache invalidation, login eligibility and commit behavior remain in the callers.
  A shared source file defines internal renewal states and explicit wire serialize/parse maps.
  Existing Pending/Validating/Committing/Committed/Cancelled/Refused/Unknown spellings remain;
  unknown, numeric and differently cased incoming strings cannot confirm success.

## Evidence

[Red/green output and safe host diagnostics](review-fix-red-green.txt) retains the actual
behavioral failures and later passes with commands. Synthetic sentinels in RED failures are
fixture values, never account credentials. The host test uses real DryIoc registration rules,
the real host HttpClient and real ManagedHttpDispatcher path, replacing only the final
HttpMessageHandler with a controlled response. Its positive control asserts the unmodified
host logs both URL and body sentinels despite disabled request logging flags. It then captures
Trace from container-resolved plugin execution and rejects complete values, 12-character
prefixes, SHA-256 values and their 12-character prefixes. It also checks the actual handler
receives the signed URL/cookie and the host IHttpClient registration is unchanged.

The consumer tests cover delta (120 seconds), HTTP date (120 seconds), malformed fallback
(300 seconds), blocked old session scopes, both consumers' status and recovery. Public
Indexer.Fetch and queued downloader tests additionally verify mutual suppression without
HTTP requests/settings writes, then produce actual tiny fixture FLAC bytes after the wait.
Existing session/login/rotation/storage/cancellation/health and helper workflows also pass.

Serial commands (one build/test process at a time, `-m:1`; final test collections serialized):

```sh
dotnet build Lidarr.Plugin.Bandcamp.slnx -c Release -m:1 -p:TreatWarningsAsErrors=false
dotnet test tests/Lidarr.Plugin.Bandcamp.Test -c Release -m:1 --no-build --no-restore --logger 'console;verbosity=normal' -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
dotnet test tests/Lidarr.Plugin.Bandcamp.CookiePush.Test -c Release -m:1 --no-build --no-restore --logger 'console;verbosity=normal' -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
```

- [Release build](review-fix-build-release.txt): passed, 41 warnings, 0 errors.
- [Plugin tests](review-fix-plugin-release.txt): 296 passed, 0 failed.
- [Helper tests](review-fix-helper-release.txt): 30 passed, 0 failed.
- `git diff --check`: passed.

The first test attempt stopped before tests at host dependency vulnerability warnings treated
as errors. Subsequent builds used CONTRIBUTING's documented TreatWarningsAsErrors=false;
no quality threshold, test or vulnerability suppression was added. Vulnerability warnings
remain in the build receipt: MailKit, MimeKit, ImageSharp and System.Security.Cryptography.Pkcs,
including high severity findings. They are unresolved dependencies, not clean security results.
The initial symlink to the existing host reference caused a duplicate NuGet cache path error;
it was replaced with a worktree-local source copy, without modifying external host sources.

Host source inspected: Lidarr `e42a7ca4fd633e021d69da7daa0368b870b0282e`.
SHA-256 of copied, unedited relevant files:

- HttpClient.cs: `3a399fb908503f07cfaf9789c4b4a0265fd2844d5dc701d28786e17f5e2d8f9f`
- ManagedHttpDispatcher.cs: `de8d47c9851d21fd67abb87d5f205e368749ef4f13fc67726b795c8930b03aeb`

## Integration and limits

Fetched remote integration tip `c941bacf86dca1639db7ae03528f5cfc960b50e2` is an ancestor of
local integration `0a23a771214a9efab38fefff4f0898601c647c6c`. Merge the latest local integration
before handoff; this is expected to report already up to date. The implementation branch is
not merged back into integration and nothing is pushed.

#47 remains **INCONCLUSIVE**. No live helper, browser, Bandcamp account or native Lidarr
runtime is qualified by these fixtures. The HTTP boundary is proven against the inspected
host seam with controlled transport, not arbitrary instrumentation or future host revisions.
Native container discovery, actual configured proxy/TLS behavior, host DB/API/backups and
real session lifetimes remain unqualified. Requests already on the wire cannot be recalled;
the shared wait is process-local and does not claim restart persistence for rate limiting.
