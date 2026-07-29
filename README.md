# STS2 Co-op Guard

`CoopGuard` makes Slay the Spire 2 reject a multiplayer join when the joining
peers have different Mod package bytes, even when the Mod IDs and versions
match. Fresh/loaded lobbies also revalidate every peer immediately before that
peer starts the run.

Public Workshop item:
https://steamcommunity.com/sharedfiles/filedetails/?id=3772631781

## How protocol 4 works

- After STS2 finishes loading Mods, CoopGuard recursively hashes regular files
  below every loaded Mod root—including Mods marked
  `affects_gameplay: false`. Relative path, byte length and SHA-256 are included
  in load order; junctions, symbolic links and ambiguous Unicode-normalized
  paths are rejected. The actual loaded assembly paths and identities are also
  included, so packages containing several DLL variants cannot compare equal
  when different variants were loaded.
- With the exact audited `Sts2SkinManager 0.27.1` integration on STS2 build
  `v0.109.1` (`c8c577f6`) and its bundled .NET `9.0.7`, CoopGuard also hashes
  the bytes and observed mount order of every PCK that Skin Manager
  successfully mounted. This covers disabled skin packages and generated
  overlays that are active despite not having `ModLoadState.Loaded`. Unknown
  game, integration or runtime versions fail closed.
- CoopGuard appends one aggregate digest plus one digest for each loaded Mod to
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
  builds fail closed until CoopGuard is updated.

There is no custom network message. Peers receive the aggregate digest and
per-Mod digest entries through STS2's native Mod-list handshake. Each per-Mod
entry contains the manifest ID and a package digest—not file contents,
absolute paths, settings, saves or account data.

## Fatal error explanations

CoopGuard replaces STS2's short popup for recognized multiplayer failures with
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

Every CoopGuard diagnosis popup has a native `Copy diagnosis` / `复制诊断`
button. It copies a bounded, redacted local report containing the error code,
game version, connection/run state, package-health counts and the latest eight
warning/error messages. It never copies a package digest, SHA-256, raw path,
Steam ID, IPv4/IPv6 endpoint or common credential form. Copy success or failure
is shown through STS2's native fullscreen status text.

## Health status and manual snapshots

- When a multiplayer player clicks ready and the full local package check
  passes, STS2 shows its native non-blocking fullscreen text for 0.5 seconds.
- Press `Ctrl+F8` at any time to run the bounded metadata freshness check and
  open a local health/soft-lock snapshot. It checks paths, file lists, sizes
  and modification times against the full startup fingerprint without
  rereading every file byte. It does not decide that a pause is a soft lock.
- Snapshot and fatal-error reports remain local until the player explicitly
  presses the copy button. CoopGuard adds no telemetry or custom network
  message and does not automatically repair, reload or mutate a run.

CoopGuard does not modify combat, RNG, run or save state, and it never attempts
to repair divergence. It is a compatibility guard for trusted co-op peers, not
anti-cheat or remote attestation.

## Build

The repository pins its .NET SDK. A normal build does not create or overwrite a
release package:

```powershell
dotnet build src/CoopGuard/CoopGuard.csproj `
  -c Release -warnaserror `
  -p:Sts2Path="C:\SteamLibrary\steamapps\common\Slay the Spire 2"
```

To create a release candidate, pass a new staging directory that does not
already exist:

```powershell
dotnet build src/CoopGuard/CoopGuard.csproj `
  -c Release -warnaserror `
  -p:Sts2Path="C:\SteamLibrary\steamapps\common\Slay the Spire 2" `
  -p:CreateModPackage=true `
  -p:PackageDir="C:\path\to\new\v0.3.3-stage"
```

The staging directory must contain exactly:

- `CoopGuard.dll`
- `CoopGuard.json`

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
  local matrix blocks modded multiplayer until CoopGuard is updated.
- One capture is capped at 4,096 files, 16,384 scanned entries, 1 GiB and 8 MiB
  of canonical text across loaded Mods and externally mounted PCKs. Limit
  failures reject multiplayer.
- Per-user configuration outside Mod roots is not hashed because STS2 has no
  safe declaration of which settings affect deterministic state.
- Loaded-run and rejoin checks compare the peers' frozen full digests. Network
  callbacks detect ordinary later edits by file list, size and timestamp; an
  adversarial same-length edit that also restores the timestamp can evade that
  quick scan until the next full gate. CoopGuard is not anti-cheat.
- Loaded-run checks cannot prove that current packages match the packages that
  originally created an old save.
- Once a run is already in progress, arbitrary on-disk edits cannot be detected
  continuously without invasive file monitoring. A detected late Mod/assembly
  change makes the next connection or rejoin fail closed, but cannot undo state
  already executed in the current run.
- Identical package bytes can still contain the same deterministic bug.
- Version `v0.3.3` is built against STS2 `0.109.1`. It has passed the Release
  build, pure self-check, 21-target Harmony smoke test, isolated native-modal
  button/clipboard checks for a manual snapshot and an internal
  `MissingMethodException`, and a matching two-client ENet run in which both
  peers showed the ready status and embarked. The unchanged
  remaining gates previously passed full-Mod-set startup, loaded lobbies,
  running-game rejoin handshake, three-player start, mismatch/TOCTOU rejection
  and generated-PCK settlement under local ENet. Real Steam transport remains
  untested. Every game update requires these checks to be repeated.

Development evidence and command results are kept in
[docs/WORKLOG.md](docs/WORKLOG.md).
