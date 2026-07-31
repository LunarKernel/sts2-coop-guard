# BetterCoop

`BetterCoop` is a safety-first Slay the Spire 2 multiplayer toolkit. Its
compatibility guard rejects joins when peers have different effective Mod
package bytes, even when Mod IDs and versions match. Its optional tools add a
co-op cockpit, health and progress displays, fixed coordination messages,
actionable local diagnosis, bounded peer text, one-click teammate hand
watching, environment management, seed/RNG analysis and consent-gated
forensics. An experimental, default-off host rollback tool can restore a
verified native multiplayer room checkpoint through STS2's own loaded-run
lobby flow.

Public Workshop item:
https://steamcommunity.com/sharedfiles/filedetails/?id=3772631781

## How protocol 6 works

- After STS2 finishes loading Mods, BetterCoop recursively hashes regular files
  below every loaded Mod root—including Mods marked
  `affects_gameplay: false`. Relative path, byte length and SHA-256 are included
  in load order; junctions, symbolic links and ambiguous Unicode-normalized
  paths are rejected. The actual loaded assembly paths and identities are also
  included, so packages containing several DLL variants cannot compare equal
  when different variants were loaded.
- With the exact audited `Sts2SkinManager 0.27.1` integration on STS2 build
  `v0.109.1` (`c8c577f6`) and its bundled .NET `9.0.7`, BetterCoop also hashes
  the bytes and observed mount order of every PCK that Skin Manager
  successfully mounted. This covers disabled skin packages and generated
  overlays that are active despite not having `ModLoadState.Loaded`. Unknown
  game, integration or runtime versions fail closed.
- BetterCoop appends one aggregate digest plus one digest for each loaded Mod to
  STS2's existing gameplay Mod list. STS2's native set difference can therefore
  name Mods whose package bytes or versions differ, including Mods marked
  `affects_gameplay: false`. A separate grouped entry identifies mounted-PCK
  differences managed by Sts2SkinManager. The aggregate remains the fail-closed
  backstop for load-order and other global differences.
- The native-list sentinel is installed before every other Harmony patch. If a
  later gameplay or diagnostic patch fails after a game update, the sentinel
  remains active and emits a process-unique unsafe entry instead of silently
  claiming that protection is active.
- Full hashing runs at startup, before each client connection, at local ready
  and host-start gates, and after the loaded-run host's final confirmation.
  Synchronous host-connect and client-begin callbacks use a bounded
  path/length/mtime freshness scan instead of rereading up to 1 GiB of content,
  so content hashing cannot consume STS2's ten-second handshake timer.
- Local hashing errors, STS2-reported Mod load failures, replacement Mods,
  late assemblies and package changes fail closed with one process-stable
  mismatch token.
- The installed STS2 version, commit and main-assembly hash must match an
  explicitly tested build even when Skin Manager is not installed. Unknown
  builds fail closed until BetterCoop is updated.

Guard Protocol 6 uses no custom network message. Peers receive the aggregate
digest and per-Mod digest entries through STS2's native Mod-list handshake.
Each per-Mod entry contains the manifest ID and a package digest—not file
contents, absolute paths, settings, saves or account data.

The optional Toolkit diagnostics plane is physically separate from Guard. It
uses a bounded, versioned message envelope only after per-peer capability
negotiation. Unsupported, stale or malformed diagnostics degrade or disable
that optional peer channel; they cannot approve ready/start, alter Guard
results or replace STS2 networking.

## Fatal error explanations

BetterCoop replaces STS2's short popup for recognized multiplayer failures with
an actionable diagnosis. It uses the game's structured failure reason first.
Only `Error` entries captured during the preceding 30 seconds can refine an
ambiguous network root cause; older warnings/errors remain report context but
cannot change the classification. A normal log `Error` does not trigger a
popup by itself.

Built-in explanations cover state divergence, Mod/package mismatch, transport
and handshake timeouts, game/data-model mismatch, offline and secure-connection
failures, hosting/platform errors, missing or unloadable dependencies,
incompatible Mod APIs, Harmony patch failures, soft-lock exceptions and
otherwise unknown internal errors. Missing methods, types and dependencies are
shown after redaction. A Mod is named only when its loaded assembly appears in
the exception chain; otherwise the popup explicitly says the source is
unknown. Each popup separates the root cause, evidence, confidence and next
action.

The popup follows STS2's current language. Simplified or Traditional Chinese
uses the Chinese text; every other language falls back to English. No log,
path, Steam ID, IP address, credential, save or fingerprint is uploaded or
sent to peers. Native crashes that terminate Godot before it can create UI
cannot display an in-game popup.

Every BetterCoop diagnosis popup has a native `Copy diagnosis` / `复制诊断`
button. It copies a bounded, redacted local report containing the error code,
game version, connection/run state, package-health counts and the latest eight
warning/error messages. It never copies a package digest, SHA-256, raw path,
Steam ID, IPv4/IPv6 endpoint or common credential form. Copy success or failure
is shown through STS2's native fullscreen status text.

## Multiplayer Toolkit

- A passive HUD shows connection health, the current wait reason and
  confidence, ready/choice/map progress, public teammate state, recent
  network samples, loading history, session identity and actionable alerts.
- `Ctrl+F7` opens the keyboard-focusable cockpit. It includes the lobby health
  matrix, event timeline, report history/comparison, environment lockfiles,
  Local Mod Doctor, read-only Harmony report, dependency-aware A/B plans,
  known-issue rules, accessibility settings and reconnect eligibility.
- `Ctrl+F8` opens a bounded local health/soft-lock snapshot. It checks current
  metadata freshness without rereading every file byte and never cancels an
  action or declares a player responsible.
- Coordination includes five fixed localized statuses plus plain peer text.
  Text is normalized Unicode, limited to 384 UTF-8 bytes/four lines, rendered
  without markup, host-relayed with per-sender and room rate limits, kept only
  in bounded session memory and never written to logs or reports.
- Exact hand sharing remains off by default and requires unanimous
  per-session consent. Each viewer selects one teammate; change-driven
  snapshots are routed only to current watchers, marked stale after one second
  and hidden after three seconds. Membership/session changes or revocation
  clear consent and cached cards immediately.
- Seed/RNG analysis observes the existing 12 deterministic run streams without
  making an RNG call. Peers exchange session-salted seed tags plus bounded
  call-count/rolling-digest summaries at natural checkpoints. Raw seed and
  recent arguments/results remain local and hidden until explicitly revealed.
- For five or more players, ready/start is fail-closed until every peer proves
  the exact supported `STS2-MultiplayerLimitBreak` v0.1.3/RitsuLib contract,
  runtime patch groups, capacity/bit widths and synchronized scaling settings.
  BetterCoop never changes Limit Break settings, layout or scaling.
- Experimental room-node rollback is off by default and its enabled setting
  must match every peer. The host can select only complete native multiplayer
  saves archived at visited stable nodes. Prepare requires the complete
  original roster at a quiescent map boundary; activation uses an atomic
  replace with an independently hashed emergency backup; STS2 then recreates
  its native loaded-run lobby and all original peers must rejoin and verify the
  canonical run/node identity before Ready or Commit is allowed. Failure after
  activation closes the lobby and leaves explicit host recovery available.
- Local forensics records bounded public action/checkpoint metadata, heartbeat
  age and the earliest observed divergent category. A push-only API lets
  another loaded Mod publish bounded diagnostic state/events; publisher
  identity and rate/size limits are enforced.
- Environment export, save sidecars, history and settings use schema/size
  bounds and atomic writes. Outside an explicitly confirmed rollback,
  BetterCoop does not edit the STS2 save, Mod list, patch order or Workshop
  state. Rollback never writes the in-memory run object, action queue or RNG.

The implementation status and deliberate degraded modes for all 52 requested
features are tracked in
[docs/MULTIPLAYER_TOOLKIT_IMPLEMENTATION_STATUS.md](docs/MULTIPLAYER_TOOLKIT_IMPLEMENTATION_STATUS.md).

When a multiplayer player clicks ready and the full local package check passes,
STS2 also shows its native non-blocking confirmation. Snapshot, history and
fatal-error reports remain local until the player explicitly copies or saves
them; BetterCoop has no telemetry.

BetterCoop does not modify combat or RNG and never attempts to repair a live
divergence. Its only run/save mutation is the default-off, host-confirmed
rollback transaction described above. It is a compatibility tool for trusted
co-op peers, not anti-cheat or remote attestation.

## Build

The repository pins its .NET SDK. A normal build does not create or overwrite a
release package:

```powershell
dotnet build src/BetterCoop/BetterCoop.csproj `
  -c Release -warnaserror `
  -p:Sts2Path="C:\SteamLibrary\steamapps\common\Slay the Spire 2"
```

To create a release candidate, pass a new staging directory that does not
already exist:

```powershell
dotnet build src/BetterCoop/BetterCoop.csproj `
  -c Release -warnaserror `
  -p:Sts2Path="C:\SteamLibrary\steamapps\common\Slay the Spire 2" `
  -p:CreateModPackage=true `
  -p:PackageDir="C:\path\to\new\v0.6.0-stage"
```

The staging directory must contain exactly:

- `BetterCoop.dll`
- `BetterCoop.json`

Building does not install or modify the live game.

## Self-check

```powershell
dotnet run --project tests/FingerprintSelfCheck/FingerprintSelfCheck.csproj `
  -c Release
```

The self-check covers deterministic recursive hashing, nested and empty files,
renames, added files, same-length byte changes with restored timestamps,
canonical path privacy, raw Unicode path identity, Unicode-normalization
collisions, caller-supplied limits, mounted-PCK byte capture, quick metadata
checks, reparse-point rejection and the scoped OnlineExchange runtime-data
rule. It also covers Chinese and English incident text, error classification,
normal-quit exclusion and diagnostic redaction. It runs on the installed .NET
10 runtime; the Release build separately compiles the actual Mod for STS2's
.NET 9 runtime. The copyable-report check also verifies the manual health
snapshot and rejects Steam IDs, IPv4/IPv6 endpoints, credentials, package
fingerprints, SHA-256 values, UNC paths and other absolute paths.
Per-Mod checks cover Unicode IDs, malformed entries, peer-controlled control
characters, same-ID byte differences and Mods present on only one peer.
Toolkit checks cover bounded codecs, consent/session rollover, contribution
monotonicity, lockfiles, sidecars, report comparison, dependency graphs,
preferences, Run Control roster/codec invariants, rollback archive/activation/
recovery/corruption paths and deterministic parser fuzzing. Set
`BETTERCOOP_FUZZ_ITERATIONS=1000000` for the release-gate fuzz run.

## Current boundaries

- Strict package equality includes documentation, PDBs and other regular files
  inside a Mod root. This is intentionally conservative.
- One evidence-based exception exists only for `OnlineExchange` `1.2.0`: its
  top-level `log.oejson`, `log.oejson.tmp`, `log.oejson.backup` and generated
  cosmetic `user_data` tree are excluded. Its DLL, manifest, README and all
  other files remain covered. A future version does not inherit this exception
  until its behavior is reviewed.
- Mounted-PCK discovery is intentionally limited to the public registry shape
  audited in `Sts2SkinManager 0.27.1` on STS2 build `v0.109.1`
  (`c8c577f6`) and .NET `9.0.7`. A different game, integration, runtime or
  registry shape is rejected until reviewed. One guarded settlement pass
  absorbs Skin Manager's zero-delay startup overlays before the fingerprint is
  exposed; a later PCK mount makes the next gate require a restart.
- Game-build verification is global rather than conditional on Skin Manager.
  Any version, commit or main-assembly hash that has not passed the complete
  local matrix blocks modded multiplayer until BetterCoop is updated.
- One capture is capped at 4,096 files, 16,384 scanned entries, 1 GiB and 8 MiB
  of canonical text across loaded Mods and externally mounted PCKs. Limit
  failures reject multiplayer.
- Per-user configuration outside Mod roots is not hashed because STS2 has no
  safe declaration of which settings affect deterministic state.
- Loaded-run and rejoin checks compare the peers' frozen full digests. Network
  callbacks detect ordinary later edits by file list, size and timestamp; an
  adversarial same-length edit that also restores the timestamp can evade that
  quick scan until the next full gate. BetterCoop is not anti-cheat.
- Loaded-run checks cannot prove that current packages match the packages that
  originally created an old save.
- Once a run is already in progress, arbitrary on-disk edits cannot be detected
  continuously without invasive file monitoring. A detected late Mod/assembly
  change makes the next connection or rejoin fail closed, but cannot undo state
  already executed in the current run.
- Identical package bytes can still contain the same deterministic bug.
- Room rollback is `Experimental` and Steam-only on STS2 `v0.109.1`. It is
  available only when all peers enable Run Control and, above four players,
  the exact Limit Break contract is proven. A failed post-activation
  transaction closes the lobby; it never guesses between the target and the
  preserved pre-rollback save.
- F1 monster/public-effect categories are `Unsupported` on this build because
  STS2 exposes no audited stable public entity/owner identity for them. The
  remaining checkpoint categories report their availability explicitly.
- G9 wire expansion remains disabled until the native receiver's allocation
  bounds are audited. Local category comparison is active and Guard Protocol 6
  remains the fail-closed compatibility path.
- Screen-reader narration is not guaranteed by the current Godot UI. Controls
  remain keyboard focusable, labeled, scalable to 200%, high-contrast capable
  and backed by copyable text.
- Version `v0.6.0` targets STS2 `v0.109.1` (`c8c577f6`) and bundled .NET
  `9.0.7`. Release build, bounded parser fuzz, persistence/rollback self-check
  and compile-time Harmony/native API contracts are automated. Real Steam
  five-player rollback, 5/8-player loaded-run continuation and the eight-hour
  soak remain controlled release gates; until their evidence is recorded,
  those paths remain Experimental rather than release-proven. Every game or
  Limit Break update requires the complete matrix to be repeated.

Development evidence and command results are kept in
[docs/WORKLOG.md](docs/WORKLOG.md).
