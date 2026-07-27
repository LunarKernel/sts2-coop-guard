# Work log

All timestamps use Asia/Taipei (UTC+08:00).

## 2026-07-27

### 16:38 - Repository created

- Confirmed GitHub CLI `2.96.0` and authenticated account `LunarKernel`.
- Confirmed neither the local target nor remote repository existed.
- Created private repository
  `https://github.com/LunarKernel/sts2-coop-guard`.
- Cloned it to `C:\SteamLibrary\steamapps\sts2-coop-guard`.

### 16:39 - Repository rules selected

- Added canonical `AGENTS.md` plus the explicitly requested `agent.md` pointer.
- Chose private visibility because the Mod is not runtime-tested yet.
- Required fail-closed behavior, no state repair, no live-game installation,
  and one runnable self-check.

### 16:40 - Current API inspected

- Confirmed local game build `v0.109.1`.
- Read the current `sts2.xml` documentation and decompiled only the relevant
  local types for implementation evidence.
- Confirmed:
  - `MessageTypes` automatically includes loaded Mod implementations of
    `INetMessage`.
  - `INetGameService` supports reliable send, broadcast and typed handlers.
  - `StartRunLobby` exposes its network service and player connection events.
  - `SetReady` and `IsAboutToBeginGame` are the shared ready/start gates.
  - The game already checks version, gameplay Mod names/versions, ModelDb and
    runtime state; v1 must not duplicate those systems.

### 16:43 - Version 1 scope fixed

- Added ADR 0001.
- Selected SHA-256 from the .NET standard library.
- Selected one canonical, human-readable hash-line format for both digesting
  and difference reporting.
- Excluded external configuration and save-state mutation from v1.

### 16:47 - First implementation built

- Added a dependency-free `net9.0` Mod project using only STS2, Harmony and
  Godot assemblies shipped with the game.
- Added SHA-256 capture for matching manifests, loaded assemblies and declared
  PCK files for every loaded Mod, preserving effective load order.
- Added one reliable fingerprint message, peer comparison, exact line-level
  differences, a local ready gate and a host final begin-run gate.
- Added hard failure for local Mod load errors and unhashable assemblies.
- Added message-size validation and ensured popup failures cannot bypass the
  gate.
- Made a missing lobby-guard session fail closed in the shared start check and
  contained network-send exceptions so they leave readiness blocked.

### 16:48 - First validation cycle

- Initial build found one `out` parameter path that was not assigned.
- Fixed the shared `LobbyGuards.CanStart` method rather than adding caller
  workarounds.
- `dotnet build` then completed with 0 warnings and 0 errors.
- `FingerprintSelfCheck` passed equal-input, changed-hash and difference-report
  assertions.
- `dotnet format --verify-no-changes` completed successfully.
- Verified the artifact contains exactly `CoopGuard.dll` and
  `CoopGuard.json`; no files were installed into the live game.
- A first reflection smoke-test invocation incorrectly expanded a `byte[]` as
  multiple PowerShell arguments; the test harness was corrected without
  changing Mod code.
- The corrected smoke test loaded the release artifact against the current
  game assemblies, confirmed `ModInitializerAttribute`, and passed a real
  `PacketWriter` to `PacketReader` message round trip.
- Release artifact hashes:
  - `CoopGuard.dll`:
    `a125f5886e30477daf5e99f07475689d4bb0ebea285c925f3340cb8ac1492070`
  - `CoopGuard.json`:
    `0b4d703796741f7a6495d9a33d1067d8b78ec835e1e7934f62963a99009f09c2`
- Confirmed the Mod project has no NuGet package dependencies.
