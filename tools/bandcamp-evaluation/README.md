# #47 opt-in evaluation preparation

Decision date: 2026-11-14. Parent: #46. Base: `5692f3c24cbd6b4541941a421268ace957616e37`.
**INCONCLUSIVE — web authentication remains primary. No live acceptance criterion is complete.**
No user account, credentials, login, generation, revocation, replacement, download or purchase was used in this preparation. No provider or existing login behavior is changed.

## Bounded official-source research

Research was limited to one pass over the announcement, developer API and relevant protocol reference pages, with no endpoint experimentation. Retrieved 2026-11-14:

- [Bandcamp announcement, 2026-07-16](https://blog.bandcamp.com/2026/07/16/discover-improvements-and-subsonic-implementation/): open beta; credentials generated in Fan Settings; server `https://bandcamp.com/api/subsonic`; collection streaming/download claims; supported clients Amperfy, Feishin and Submariner. Large collections may be slow. This establishes a supported candidate, not full inventory, lossless archive or lifetime parity.
- [Bandcamp developer API](https://bandcamp.com/developer): label/merch partner OAuth is a separate product. Its expiry, refresh and regeneration rules cannot be applied to fan Subsonic.
- [OpenSubsonic download](https://opensubsonic.github.io/docs/endpoints/download/) and [getAlbumList2](https://opensubsonic.github.io/docs/endpoints/getalbumlist2/): protocol references for a future collector, not proof Bandcamp implements each method or original-format semantics. Authentication reference fetch failed; no fan authentication mechanism or expiry contract was inferred.

Stop research here: no private mobile OAuth, speculative client, automatic renewal or production rollout. The accepted credential-login ADR remains in force subject to #46's newer evidence qualifications.

## Current evidence record

| Criterion | Result | Evidence missing |
| --- | --- | --- |
| Full accessible collection | INCONCLUSIVE | Same-account exhaustive web and Subsonic inventories, pagination and reconciliation |
| Actual bytes, codecs, bitrate, containers, formats | INCONCLUSIVE | Paired authenticated downloads and format inventory |
| Required metadata | INCONCLUSIVE | Artist, album, track, release date, duration and ownership comparison |
| Supported authentication | INCONCLUSIVE | Bounded authorized successful and failed auth observations |
| Revocation and replacement | INCONCLUSIVE | Separate authorized destructive lifecycle sequence |
| Lifetime | INCONCLUSIVE | Dated observations over an agreed interval; future expiry always unknown |

## Future execution protocol — requires separate authorization

This is a manual opt-in canary procedure, never routine CI. Agree the account, duration, request/file/byte budget, representative items and allowed lifecycle changes before starting. Suggested bounded session: 60 minutes, stop on challenge, 429, unexpected mutation or budget exhaustion; honor Retry-After and retain the partial evidence. Do not turn a time limit into a passing result.

1. The account owner uses the official supported UI/client personally. Never put credentials, cookies, tokens, credential hashes/prefixes, signed URLs or HTTP dumps in this repo, command arguments, logs or evidence. No purchases or playlist writes. Generated secrets are password-equivalent. Record the supported auth method and client version, not its parameters. No assumption that ping success authorizes downloads.
2. Capture the complete web collection at a fixed UTC observation window, including every pagination cursor/page count, terminal condition and accessible track membership. Capture Subsonic's complete album/track enumeration via supported client behavior. Record failures, duplicates and concurrent collection changes. Reconcile albums AND standalone tracks; counts alone are insufficient. A repeated page or unstable inventory is INCONCLUSIVE. Use a private mapping from original identifiers to the same arbitrary `item-N` alias on both sides; do not match solely by title.
3. Normalize one row per owned accessible track into `web` and `subsonic`. Fields: `key`, `artist`, `album`, `track`, `release_date` (ISO date or null), `duration` (seconds or null), `ownership` (evidenced ownership, or null). Record provenance and transformations privately. Do not fabricate ownership from catalogue availability. Mark `web_complete`/`subsonic_complete` true only after verified traversal. The CLI compares exact values; manually review date precision, artist credits and duration differences against an agreed tolerance (suggested one second), retaining originals.
4. Choose representative albums, single tracks, compilations, multi-disc releases, Unicode names, gifts, private/deleted-but-owned releases, large releases and missing dates where actually present. Record absent edge cases as untested, not passed. Inventory all web-offered archive formats and all actually offered Subsonic formats; never infer availability from a filename or stream response field.
5. Download paired files through the authorized web flow and supported Subsonic download action (not merely stream). Keep local originals and a secret-free request receipt: UTC, alias, client/version, action, status, content type, byte count, format selection and local artifact checksum (media only, never credentials). Unpack archives locally and inspect actual audio. Compare sample rate, bit depth, codec, bitrate, container, duration, track count and tags, plus cover art/archive extras required by the compatibility path. Decode fully with `ffmpeg -v error -i LOCAL_FILE -f null -` to detect truncation. An ffprobe success alone is not integrity or download provenance proof.
6. Only if separately authorized, record initial credential success, owner-performed revocation, failure of the old credential with a fresh client (not cached playback), owner-performed replacement, new-credential success and continued old-credential rejection. Do not change the account password or invent a revoke API. If the official UI lacks a supported action, record that limitation and stop that phase.
7. For lifetime, record credential generation time if known, first/last successful fresh authenticated operations, client restarts and observation timestamps. Observe only during an explicitly authorized schedule. Report the measured lower bound between successes, failures and censoring; network failures are not expiry. Never infer permanence or a refresh interval. No scheduler is installed here.

## Offline tooling

Requires Python 3 standard library; local media inspection additionally requires ffprobe. It has no networking or credential interface. Keep private evidence outside the checkout with owner-only permissions. Only publish a manually reviewed, secret-free summary. Do not feed raw account exports into this tool.

```sh
python3 tools/bandcamp-evaluation/evaluate.py /private/local/inventory.json
python3 tools/bandcamp-evaluation/evaluate.py --probe /private/local/audio.flac
python3 -m unittest discover -s tools/bandcamp-evaluation
```

Start inventory JSON with `{"web_complete": false, "subsonic_complete": false, "web": [], "subsonic": []}` and add normalized rows as specified above. Missing/extra counts are failures of recorded coverage; MATCH_RECORDED only means the supplied inventories match. Missing/blank metadata stays unknown even when both sides agree. Output never contains row identifiers or metadata values. Media output contains only technical fields, not tags or paths. Errors are generic to avoid echoing inputs. Exit 0 means analysis completed, **not acceptance passed**; exit 2 means unusable evidence/tool failure. The CLI deliberately cannot issue GO: it cannot verify provenance, traversal, ownership, lifecycle or suitability.

## Decision rule and handoff record

For each future run record: UTC start/end; authorization scope and stop conditions; client/probe/tool versions and git SHA; account alias; collection snapshot completeness; paired counts/omissions; edge-case table; per-format receipts and media observations; per-field differences and unknowns; auth/revocation/replacement event timeline; lifetime observation bounds; failures and untested cases; reviewer and dated decision. Keep raw private evidence separate and reference sanitized local artifact aliases only.

GO requires independently reviewed complete collection reconciliation, acceptable original downloads and format inventory, sufficient metadata/ownership, observed supported authentication and revocation/replacement, and explicitly accepted bounded lifetime uncertainty. Any demonstrated unacceptable gap is NO-GO; absent evidence is INCONCLUSIVE. One account, ping, stream, one codec, fixtures or synthetic media cannot establish general parity. Both NO-GO and INCONCLUSIVE retain web as primary. Only after a reviewed passing live gate should a preferred-Subsonic ticket be created under #46, blocked by #47 and the shared-session issue. No such ticket is created by this preparation.

Tests exercise the opt-in evaluation's external CLI boundary authorized by #46's canary/evidence decisions; they do not introduce session-provider seams or claim live proof. Production .NET code is untouched.
