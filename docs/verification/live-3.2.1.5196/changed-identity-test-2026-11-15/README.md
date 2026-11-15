# Controlled changed-identity recovery and restart — PASS

A separate disabled test owner was seeded with a synthetic invalid identity, rejected by real Bandcamp validation, then repaired using the actual Lidarr begin/renew/status protocol and a real valid candidate for independently confirmed fan 67857. The changed value was saved, both named consumers recovered without their settings being edited, and the changed value plus both consumers survived a guarded service restart. A real FLAC search through the test indexer passed before and after restart. No download was dispatched.

This closes the **controlled changed-value recovery and persistence** gap. It does not establish that Bandcamp itself issued a new token or expired a previously valid one: the initial value was deliberately invalid, and the valid candidate came from the existing working owner. The separate [ordinary Mac HTTPS helper and fresh browser renewal](../helper-https-2026-11-15/README.md) already qualified the desktop client. This fault test exercises the same real server-side helper protocol through the existing authenticated SSH/local-admin route, not another desktop HTTPS run.

## Isolation and baseline

Fresh protected rollback/evidence root: `/srv/docker-state/rollback-preparation/bandcamp-changed-identity-test-20261115T180222Z`. The online SQLite snapshots passed integrity before test definitions were added. The original working downloader 12 and indexer 26 were never edited. Their complete final database rows, config.xml and Compose file compare byte-for-byte equal with the pre-test snapshot. Installed DLL remains `674d2de854890f625e84d0d3702d0a9cbc3f0860c9479f5eee6f86aa93c26cc4`.

The scoped test definitions were:

| Role | ID | Name |
| --- | --- | --- |
| Owner | 13 | Bandcamp changed-identity qualification 20261115 |
| Downloader consumer | 14 | Bandcamp changed-identity qualification 20261115 downloader |
| Indexer consumer | 29 | Bandcamp changed-identity qualification 20261115 indexer |

They were all created disabled. Password, email and verification fields were blank, so no credential-login fallback could run. Both consumers had empty local cookies and selected only the exact test-owner name. Test download paths were separate empty qualification directories; no media path was reused. [Definition IDs](test-definitions.json), [actual setup](setup.py).

Both consumer tests returned **HTTP 400** with authentication validation failures: downloader property Cookies and indexer property AuthConfiguration. The real working definitions remained unchanged. These are live negative controls, not fixture assertions. [Actual red results](red-results.json).

## One validated replacement

The attempted ordinary helper path stopped at a 1Password authorization timeout before any Authelia login or renewal began. No candidate had been submitted. Instead of requiring another desktop unlock, the test used the same production beginIdentityRenewal / renewIdentity / identityRenewalStatus actions through the already-authorized SSH administration channel. The production helper's HTTPS requirement, proxy policy and TLS settings were unchanged.

The exact test owner was preflighted, the synthetic old value verified, and an exclusive dispatch marker permitted at most one candidate submission. Only the real working owner's minimal identity was read privately into process memory. Operation **771a1739-660c-4cd8-94ff-4ac371740dba** validated expected fan **67857**, returned Committed, and a separate status read confirmed Committed. No candidate was resubmitted. The test owner's saved value differed from the synthetic old value and matched the valid candidate. Both consumer Name/Settings rows remained equal to their pre-repair snapshots; only the owner was repaired. Owner and both consumer connection tests then passed. [Begin](begin-receipt.json), [renew](renew-receipt.json), [status](status-receipt.json), [live repair result](repair-result.json), [exact repair script](repair.py).

To test actual release search, only the test indexer's **interactive/manual search** flag was temporarily enabled. RSS and automatic search remained false; both test download clients stayed disabled throughout. A manual search returned one FLAC result attributed specifically to indexer **29**, with no grab. The result was already satisfied by the existing album. A separate online `lidarr.after-repair.db` snapshot passed integrity and retained the committed test-owner value plus consumer configurations privately. [Search result](pre-restart-search.json), [search/snapshot script](search.py).

## Restart and cleanup

The guarded restart required the same running container/image/DLL, unchanged host config/Compose and real working definitions, exact test-owner/consumer rows matching the post-repair snapshot, eleven linked POSI files, no active Bandcamp queue item, and no active import command. It stopped/started the same service without replacing files or restoring data. There was no implicit retry/inverse. [Dispatch phases](restart-dispatch.jsonl), [restart script](restart.py).

After restart, the test-owner and both consumer rows still matched the committed post-repair snapshot. Owner 13, downloader consumer 14 and indexer 29 all passed live validation, and a real search again returned a FLAC result from indexer 29. This proves the new value was loaded after process memory was lost. [Actual post-restart result](restart-result.json).

As designed, the old process-scoped operation receipt returned Unknown after restart; it was not replayed. The pre-restart committed receipts and durable row comparison establish the outcome independently. [Old operation status](operation-status-after-restart.json).

The test indexer's manual-search flag was restored to false. All three test definitions remain disabled for review. The real clearLocalCredentials action removed **only test owner 13's** copied local credentials; it did not revoke the Bandcamp server session or alter the real owner. The immutable test receipts and protected snapshots remain. [Clear receipt](clear-test-local-credentials-receipt.json), [cleanup](cleanup-result.json), [exact cleanup script](cleanup.py).

Final audit: the complete original provider rows, host config and Compose are unchanged; real downloader/indexer validation still passes; all eleven imported files are linked and their SHA-256/size pairs match the earlier fully decoded native FLAC qualification; the retry flag is true. No new grab or password login was made by this fault test. [Final independent audit](final-audit.json).

No production source changed during this test. Existing 363 source/exact plugin tests and package/native compatibility evidence remain applicable. This report adds actual runtime recovery/persistence proof rather than re-running unrelated checks. PR #59 compatibility is retained; PR #55 remains draft under the recorded boundary. No merge, public release or issue closure occurred.
