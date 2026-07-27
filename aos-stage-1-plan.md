# AOS Stage 1-2: Planning, Risk, and Contract — Bandcamp #9

## Risk classification (per risk-taxonomy skill)

| Dimension | Factor | Class |
|---|---|---|
| Impact | Single plugin, single HTTP header behavior. No infra/secrets/hosts. | low |
| Reversibility | Single git revert; no migration. | low |
| Permission sensitivity | Local code edit on a feature branch. No secrets/credentials. | low |
| Operational exposure | None — plugin runs inside Lidarr, not our infra. | low |
| Evidence quality | Existing test suite (xUnit fixtures); deterministic. | high |
| Uncertainty | Root cause confirmed by external reporter stack trace + code inspection. | low |

**Effective risk class: LOW.** No always-ask overlay applies.

## Planning tier

Non-trivial low-risk → lightweight structured plan, same session permitted. No human gate before mutation (low-risk, local, reversible).

## Root cause analysis

**Bug:** Bandcamp plugin fails all connection tests with `User-Agent other than Lidarr not allowed` unless Tubifarry is installed first.

**Why:** Lidarr's built-in `ManagedHttpDispatcher` enforces that outbound HTTP requests carry the Lidarr User-Agent. Tubifarry registers a custom `FlexibleHttpDispatcher` via DI that relaxes this check. When Bandcamp is installed alone, no relaxed dispatcher is registered, so Lidarr's default `ManagedHttpDispatcher` rejects the browser User-Agent set by `BandcampRequestGenerator` and `BandcampHttpClient`.

**Fix approach:** The plugin already sets browser-like User-Agent headers on its requests. The problem is the *dispatcher* enforcing Lidarr's UA policy before those headers reach the wire. The correct fix is to register a custom `IHttpDispatcher` (or equivalent Lidarr extension point) that permits the Bandcamp plugin's browser UA, removing the Tubifarry dependency.

However — the deeper question is whether the plugin *needs* a browser UA at all. Bandcamp serves full search pages to browser UAs and blocks/rate-limits non-browser clients. The plugin sets browser headers deliberately. So the fix must allow the browser UA through, not remove it.

## Lane selection

- Executor: this profile (fieldbook-dogfood), uncalibrated lane → low-risk only ✓
- Reviewer: fresh context delegate (independence requirement per planning-routing skill)
- No parallelism needed.

## Contract: BANDCAMP-DF-001

See: `contract-bandcamp-df-001.yaml`
