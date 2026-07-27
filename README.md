# STS2 Co-op Guard

`CoopGuard` makes Slay the Spire 2 reject a multiplayer join when the joining
peers have different Mod package bytes, even when the Mod IDs and versions
match. Fresh/loaded lobbies also revalidate every peer immediately before that
peer starts the run.

Private Workshop test item:
https://steamcommunity.com/sharedfiles/filedetails/?id=3772631781

## How version 3 works

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
- CoopGuard appends one fixed package-digest entry to STS2's existing gameplay
  Mod list. STS2's native join check therefore covers fresh lobbies, loaded-run
  lobbies and running-game rejoins before the client enters the session.
- Full hashing runs at startup, before each client connection, at local ready
  and host-start gates, and after the loaded-run host's final confirmation.
  Synchronous host-connect and client-begin callbacks use a bounded
  path/length/mtime freshness scan instead of rereading up to 1 GiB of content,
  so content hashing cannot consume STS2's ten-second handshake timer.
- Local hashing errors, STS2-reported Mod load failures, replacement Mods,
  late assemblies and package changes fail closed with one process-stable
  mismatch token.

There is no custom network message. Peers receive only the aggregate digest
already carried by STS2's native Mod-list handshake—not file contents,
absolute paths, settings, saves or account data.

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
  -p:PackageDir="C:\path\to\new\v0.2.1-stage"
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
rule. It runs on the installed .NET 10 runtime; the Release build separately
compiles the actual Mod for STS2's .NET 9 runtime.

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
- Version `v0.2.1` is built against STS2 `0.109.1`. It passed the Release,
  self-check, formatting and 15-target Harmony smoke gates, plus isolated
  full-Mod-set startup, matching fresh/loaded lobbies, fresh-run start,
  running-game rejoin handshake, a three-player start, same-ID/version package
  mismatch rejection, initial-info TOCTOU rejection and deferred generated-PCK
  settlement. These multiplayer tests used local ENet with Steam disabled;
  real Steam transport remains untested. Every game update requires the checks
  to be repeated.

Development evidence and command results are kept in
[docs/WORKLOG.md](docs/WORKLOG.md).
