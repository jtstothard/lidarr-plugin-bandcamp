# Shared Bandcamp authentication (#46 / #50)

## Storage and host extension choice

An authentication owner is an existing saved Bandcamp download-client definition: its Lidarr
Name is the configuration name and its Shared Authentication selector is empty. The cookie,
optional complete credential pair, verification code and persisted challenge metadata stay in
that definition's existing settings JSON. Indexer and downloader selectors are plain provider
fields exposed through Lidarr's existing schema/API. No plugin database schema, private file,
new API controller or login engine is introduced.

The host patterns inspected were `ProviderDefinition.Settings`, `ProviderRepository`,
`DownloadClientRepository`, `DownloadClientController`/`ProviderControllerBase`,
`FieldDefinitionAttribute`, and the existing Newznab `RequestAction` extension. An independent
auth CRUD API would require extra lifecycle/registration work. Reusing a named provider gives
host persistence and masked secret fields now. The selector is an exact-name text field rather
than a dynamic dropdown. Only saved Bandcamp download clients are eligible owners; a disabled
owner is permitted. Its download path must still satisfy the host's provider settings validator.

## Deliberate migration

Upgrade is non-destructive and does not infer accounts from cookie text or email similarity:

1. Existing download clients automatically remain separate named owners without rewriting them.
2. Existing indexers with empty selectors keep reading their original Cookies. No implicit
   consolidation or automatic cookie copy takes place.
3. To share, select the exact unique name of the intended owner in each consumer's Shared
   Authentication field. For an indexer-only legacy account, first create/save a Bandcamp download
   client using that account's existing identity, a valid download path, and an empty selector.
   It may be disabled. Then explicitly select its name in the indexer.
4. When different legacy identities or credentials exist, decide which account each consumer
   should use. The explicit selector changes the effective account; the local legacy secrets
   remain intact and inactive. They are never used as fallback if the selected owner fails.
5. Rename all selectors when renaming their owner. Missing, ambiguous, deleted, or chained
   owners fail closed with ConfigurationMissing and an actionable message. Self references and
   cycles are rejected because an owner must have an empty selector.

Enablement, priority, tags, paths, URLs and early-release options are not migration targets and
are not changed. Partial email/password pairs remain validation errors even for shared consumers.
This is deliberate mapping, not an automatic account-identity discovery/matching migration.

## Recovery and current-session behavior

The existing session provider is keyed by owner definition id. All consumers share its cache,
login coalescing, rejected-credential suppression, six-hour durable challenge suppression and
typed health. Shared references read saved owner settings at each operation, including queued
execution and retry. Search takes one session snapshot per fetch. Owner execution also rereads
saved settings rather than overwriting a renewed identity from a stale factory snapshot. The
host Test action checks proposed unsaved owner settings; shared Test checks the saved owner.

ReplaceIdentityAsync is the validated manual/helper import seam. It validates read-only, compares
saved identity/credentials, performs #49's single detached-settings write, then activates. A
concurrent switch of the owner into a consumer also rejects replacement. Shared renewal changes
only the owner: no multi-definition cookie writes or partially updated search/download copies.
Manual cookie paste remains available on the owner through normal host settings. As before,
ordinary host/API edits are outside the plugin recovery transaction; a pasted value saved directly
through the host can replace the saved cookie before its next runtime validation.

No scheduled login engine was added. Enabled download clients retain the hourly monitor;
pre-grab refusal and one mid-download renewal/one retry stay intact. If only an indexer uses a
disabled owner, validation happens on search/Test, not on a new indexer-specific hourly job.

## Downgrade and operational limits

The owner still saves legacy Cookies, Email, Password and VerificationCode fields. Older versions
can read that owner's current identity/credentials. Consumer legacy values remain readable but
are frozen while sharing; they can be stale or belong to a different account. Older plugins
ignore AuthConfiguration and cannot follow it. Before downgrade, explicitly copy the chosen
owner's current identity into every intended consumer, reconcile credential pairs, and verify
account choice. No claim is made of automatically synchronized downgrade cookies. Restoring an
empty selector similarly reactivates the retained legacy secrets; inspect them first.

Do not delete the owner while it has consumers. Renaming/recreating a configuration is an
administrative account-mapping operation: selectors resolve names, not immutable ids. A unique
replacement with the same name becomes the selected owner. Use distinct names for distinct
accounts and update selectors deliberately when changing accounts.

Settings have Lidarr's existing database/API/backup exposure; no stronger encryption is claimed.
The per-definition gate coordinates plugin writers in one process, not arbitrary host/API edits
or cross-process writers. No live account operation, production rollout, browser packaging,
Subsonic parity or cookie-lifetime guarantee is established by these regression fixtures.
