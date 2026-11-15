# Native download failure and prepared redirect correction

## Live outcome and containment

The user authorized continued native download/import qualification while preserving
existing files and the working deployment. Earlier target selection was too strict:
the collection's absent `num_tracks` values were treated as mismatches. Null counts
are unknown, not evidence that no usable owned target exists. A further read-only
check found owned artists absent from Lidarr's catalogue.

Chloe Glover was added as artist 268, MusicBrainz
`88dfac59-76c9-47c8-93c8-fe15be39aa24`, at the previously absent path
`/data/media/Music/Chloe Glover`, using the existing HQ/Standard profiles. The artist
was unmonitored and no automatic missing-album search was requested. Metadata loaded
Dark Matter as EP 235830, selected release 78874, four tracks, initially no linked files.
Native shared-indexer search returned an accepted FLAC release of 175531622 bytes.

One agent-issued `POST /api/v1/release` selected that exact cached FLAC release,
indexer 26 and downloader 12. It returned HTTP 200. First download ID:
`846935182f7949e4aa4196d6ea5309c2`, grabbed at 12:54:31 UTC. The first failure at
12:54:37 UTC, history 2377372, was:

```text
Bandcamp file download failed with HTTP 302.
```

Lidarr automatically re-searched/re-grabbed after failures. The agent did not issue
another grab. This was not preflighted correctly: the existing host retry setting
allowed the bounded manual test to produce an autonomous retry chain. Unmonitoring
the new EP did not stop explicit failed-download AlbumSearch commands. Consequently,
`autoRedownloadFailed` was temporarily set false after privately retaining its exact
prior resource. Only target search commands were considered for cancellation.

Closure was independently read back twice: **19 grabs, 19 terminal failures, no
unpaired grab and no active target search command**. All 19 failures were HTTP 302;
all grab/failure records name Bandcamp as client/indexer. At 13:00:37 UTC the complete
download-client configuration was restored exactly to its prior value (HTTP 202,
readback equality true). `autoRedownloadFailed` is again true. Artist 268 and EP
235830 remain unmonitored; their preparation and the spent history are retained.
The target still has zero linked files. No import or existing-media overwrite occurred.
No further grab or credential retry was dispatched after the first native failure.

Protected server evidence remains under
`/srv/docker-state/rollback-preparation/bandcamp-shared-renewal-20261115T115346Z`:
`chloe-native-preparation.json`, `chloe-search-results.json`,
`chloe-grab-dispatched.json`, the original/final failure history,
`downloadclient-config-before-containment.json` and `native-failure-containment.json`.
Raw saved resources and signed URLs are not copied into this repository.

## Local correction and verification

The public DownloadFileAsync regression first reproduced the exact HTTP 302
exception ([red output](red.txt)). The final archive transfer now manually follows
at most three redirects to default-port HTTPS Bandcamp/bcbits hosts. It refuses
cycles, unsafe hosts/schemes/ports, userinfo, fragments and invalid Location headers.
Automatic redirects stay disabled. Cookies are dropped on origin change and never
restored later in that chain. The dispatcher streams only HTTP 200, keeping redirect
bodies out of the archive. Account and helper API redirects remain refused.

Source plugin suite: **315 passed, zero failed/skipped**. Helper suite: **49 passed**.
Offline evaluation suite: **6 passed**. The exact-deployed-library plugin suite also
passed all 315 tests under the local .NET 10 runtime; its actual output is retained
in [exact-target-tests.txt](exact-target-tests.txt). Initial custom output-directory
attempts failed from shared output collisions and two existing source-relative
fixture paths; the normal per-project output layout passed without changing,
skipping or suppressing either test.

Main/#59 is merged locally as `9f1b53b`. The health check retains both the portable
BandcampHealthResult constructor adapter and #46's shared-session state description.
The packaged fixture needed adaptation to the deliberately internal fake-HTTP seam
and optional repository dependency. Its independent message contract now distinguishes
the historical v1.1.2 control from #46's typed signed-out diagnosis. The gate's exact
modern reason, named-client warnings, restoration and disabled/multiple cases remain.

The source build uses SDK 10.0.103, pinned Lidarr source e42a7ca4f and explicit global
AssemblyVersion 2.0.0.0, without editing the existing reference checkout. The full
distribution gate passed ten behavior scenarios across pinned 3.1.4.5029 and
3.2.1.5196 runtimes plus both ABI scans. The historical incompatible-package,
missing-dependency, scanner and transported-distribution negative controls passed.
These managed matrix processes run under local .NET 10, not native Linux hosting.
Source build retains existing warnings; it is not a zero-warning build.
See [release-gate.json](release-gate.json) for the exact distribution identities.

## Exact-target prepared candidate; not deployed

Exact-target DLL SHA256:
`e91694e0fe1304cab7fb87e61243e5bba050fe8682ac0cd1b994dc42d00d7f9d`.
This is a separate build against copied, independently verified deployed Core/Common
3.2.1.5196, not the source-reference distribution in release-gate.json. No distribution
or deployment identity is inferred from another build's hash.

A fresh private backup of the working deployment, current settings and online SQLite
snapshots passed integrity checks:
`/srv/docker-state/rollback-preparation/lidarr-auth-redirect-20261115T130557Z`.
It contains the working DLL SHA256
`9e109bdd7d8b0c8ff417a8e5e4af36b393d1b2c6635ea58ae126f80bfbbd447c`, not an original
pre-auth plugin. A fresh copied rootfs underneath this packet holds the prepared
candidate; no historical root or live plugin file was overwritten. All 388 copied
host DLL/native-library hashes matched the current live container; its image remained
`sha256:5618a549ba6ef21318d24f869764fa58f51629b063a1a1ebf11cf8f32d1f5b8a`.

The exact candidate passed a native .NET 8.0.27 confinement probe: 149 types loaded,
803 method/constructor bodies JIT-prepared without invocation, and native host DI
selected BandcampHttpDispatcher. Production/default config paths were unavailable
and AF_INET socket creation was denied. The copied probe, never Lidarr's apphost,
ran as uid 65534 with RootDirectory, PrivateNetwork, AF_UNIX-only sockets, ProtectHome,
ProtectSystem=strict, PrivateTmp/Devices and NoNewPrivileges. Actual
[stdout](native-stdout.txt), [stderr](native-stderr.txt) and
[deployment packet](deployment-packet.json) are retained. This qualifies native
type/JIT/DI compatibility, not redirect behavior against Bandcamp or live import.

The working DLL remains installed; no deployment/restart or retry of the failed
native operation has occurred. The next effectful run requires specific approval:
refresh identity/settings and backup checks; stage only the exact candidate with
uid99/gid100 mode0644; one guarded Compose stop/atomic DLL replacement/start; verify
health, shared owner and indexer without password experiments. Then, with host
failed-download retries temporarily disabled and their exact inverse prepared,
perform at most one fresh Dark Matter FLAC grab. Stop at the first effectful failure,
retain partial output/history, reconcile terminal state, and restore the prior retry
setting only after closure. Verify actual FLAC output and native import before claiming
qualification. Plugin-only inverse restores the backed-up DLL after identity checks;
do not restore DB/config merely to undo a DLL. #55 stays draft pending native success.
