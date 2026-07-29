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

### 16:57 - Private Workshop upload prepared

- Confirmed the official STS2 publishing flow uses Mega Crit's separate
  `sts2-mod-uploader`, which connects to the signed-in Steam client.
- Confirmed Steam is running and STS2 is on `public-beta`.
- Downloaded official ModUploader `v0.2.0` for Windows x64 and verified its
  published SHA-256:
  `2b55c19cc5932235ca9dbd663ca07d04e5d5a0018402303199f9b4ec8ca06578`.
- Prepared an external, reusable upload workspace containing only the release
  DLL, manifest, Workshop metadata, and a required 768 x 768 preview image.
- Selected `private` visibility and constrained the first upload to
  `public-beta`; the Mod has not yet received a live multiplayer test.
- No files were installed into the live game directory.

### 17:20 - Version 0.1.0 uploaded privately

- Ran the verified official uploader after explicit user confirmation.
- Steam authenticated the upload and created Workshop item `3772631781`:
  `https://steamcommunity.com/sharedfiles/filedetails/?id=3772631781`.
- Uploaded exactly 22,852 bytes of Mod content plus the preview image.
- The uploader reported success and persisted `mod_id.txt` in the external
  workspace for future in-place updates.
- Opened the item in Steam and confirmed the title, preview, file size, and
  current hidden/private visibility.
- Steam reported that the new item is temporarily awaiting automated content
  analysis. It remains visible only to the creator and Steam administrators.
- The live game directory remains unchanged.

### 18:02 - Version 2 safety redesign implemented

- Audited the custom message against the current `0.109.1` networking code and
  confirmed:
  - a three-plus-player join could permanently lose an existing client's
    one-time fingerprint broadcast;
  - `PacketReader.ReadString()` allocated remote-declared lengths before the
    Mod's handler limit, and hosts broadcast before handler validation;
  - only fresh `StartRunLobby` sessions were guarded.
- Replaced the entire custom `INetMessage`/peer-session protocol with one
  synthetic package digest appended to
  `ModManager.GetGameplayRelevantModNameList()`.
- Verified from the local game API that the native list is compared before
  `InLobby`, `InLoadedLobby` and `Running` join branches.
- Moved capture to an `OneTimeInitialization.ExecuteEssential()` postfix so
  hashing completes before menu and command-line joins rather than inside the
  ten-second client handshake.
- Added sticky restart-required handling for runtime Mod detection, late
  assembly association and changed package file metadata.
- Expanded the package fingerprint from Manifest/DLL/PCK only to every regular
  file recursively below each loaded Mod root, with deterministic relative
  paths, SHA-256, file/byte limits and fail-closed reparse-point rejection.
- Removed remote details/error transmission, eliminating the absolute-path
  disclosure and custom-message input surface.
- Added local fail-closed ready/final gates to both fresh and loaded-run
  lobbies.
- Removed the unused `unsafe` build permission, disabled source-revision
  injection, pinned SDK `10.0.301`, and made release packaging opt-in to a new
  non-existing staging directory.
- Bumped the manifest and assembly version to `v0.2.0` / `0.2.0`.
- Expanded the dependency-free self-check to cover recursive files,
  deterministic ordering, nested-byte changes, renames, empty/added files,
  canonical path privacy, snapshot invalidation and reparse-point rejection.
- First validation:
  - Release build: 0 warnings, 0 errors.
  - `FingerprintSelfCheck`: passed.
  - `dotnet format --verify-no-changes`: passed.
- No build was installed into the live game and the private Workshop item was
  not updated.

### 18:44 - Version 2 professional safety audit closed

- Removed all disk enumeration and hashing from the native Mod-list getter.
  It now returns only a frozen digest or one process-stable random failure
  token.
- Split validation by execution context:
  - full SHA-256 at startup, before client connect, local ready, host final
    start, and after a loaded-run host confirmation;
  - bounded path/length/mtime checks inside synchronous host-connect and
    client-begin message callbacks.
- Added client-side begin-message gates for fresh and loaded runs. A failed
  quick check suppresses the handler and disconnects with STS2's native
  `ModMismatch` reason.
- Wrapped all three `ILoadRunLobbyListener.ShouldAllowRunToBegin()` concrete
  implementations. After the native confirmation task returns true, the host
  performs one final full validation before STS2 can send the loaded-run begin
  message; false returns through the game's native unready/cancel path.
- Added a pre-connect client gate and a pre-initial-message host quick gate, so
  the ten-second native handshake never performs full package hashing.
- Limited runtime Mod detection to replacements that touch an already-loaded
  ID/path. A real late assembly-count change remains sticky and requires a
  restart; unrelated runtime discoveries that are not loaded wait for the next
  restart.
- Added a quick snapshot of loaded Mod order, ID, version, root,
  `affects_gameplay` and assembly count.
- Added the actual selected assembly relative path, assembly full name and MVID
  to the full fingerprint. This distinguishes packages such as RitsuLib that
  ship multiple version-specific DLL variants in one directory.
- Hardened package hashing:
  - raw relative paths are hashed while NFC-normalized collisions are rejected;
  - reparse points are rejected before traversal/open and every file stamp is
    rechecked after the whole package is hashed;
  - same-length byte changes are detected by full capture even when the mtime is
    restored;
  - non-object JSON and non-string manifest IDs cannot escape validation;
  - aggregate limits are enforced before further reads/allocations: 4,096
    files, 16,384 entries, 1 GiB and 8 MiB canonical text;
  - a limit breach stops the whole capture instead of continuing through later
    packages.
- Added one audited exception only for OnlineExchange `1.2.0`: generated
  top-level `log.oejson`, `log.oejson.tmp`, `log.oejson.backup` and the
  cosmetic `user_data` tree are excluded. Later OnlineExchange versions do not
  inherit the rule without review.
- Expanded the self-check for invariant-culture fields, scoped exclusions,
  caller limits, quick freshness, same-length/restored-mtime changes, raw
  Unicode identity, normalized collisions and reparse rejection.
- Documented the remaining trusted-peer boundary: a synchronous quick check
  cannot detect an adversarial same-length rewrite with a restored timestamp,
  and arbitrary edits after a run has begun cannot be rolled back.

### 18:44 - Final static release gate

- Reflected the current `0.109.1` game assembly and confirmed the exact public
  and private targets for `JoinFlow.Begin`, `InitialGameInfoMessage.Basic`,
  both begin-message handlers, `AssociateAssemblyWithMod`, and all three
  loaded-run confirmation implementations.
- A standalone scan of the 38 manifest-bearing Workshop/local packages used
  the production hasher successfully:
  - 170 included files;
  - 187 scanned entries;
  - 389,283,805 bytes;
  - 19,884 canonical characters;
  - 798 ms full capture and 42 ms quick check on this machine.
- A broader raw upper-bound inventory of all 40 installed Mod directories
  contained 402 regular files, 427 entries and 494,238,798 bytes
  (about 471.3 MiB), below every aggregate limit.
- Frozen-snapshot release validation passed:
  - .NET SDK `10.0.301`;
  - Release rebuild with warnings as errors: 0 warnings, 0 errors;
  - `FingerprintSelfCheck`: passed;
  - `dotnet format`: passed (apart from the known workspace-load warning);
  - two independent staging builds and two different checkout-path lengths
    produced byte-identical output;
  - normal builds did not touch existing package directories;
  - existing-stage and Debug-package safety gates both rejected as designed;
  - assembly version `0.2.0.0`;
  - manifest `v0.2.0`, minimum game version `0.109.1`.
- Final release hashes:
  - `CoopGuard.dll`:
    `c2e87991d1f463c7c391f4e7ae39dc5962e997cf6769103afbe370b17a72d5b9`
  - `CoopGuard.json`:
    `4cf6078b313a9638d9cca5cb9c574015449f2678523aa1690490e2aae23ef16e`
- Preserved the final two-file candidate at
  `artifacts/staging/v0.2.0-20260727-1844`.
- A standalone `PatchAll` smoke test ran under STS2's bundled .NET `9.0.7`
  runtime and Harmony `2.4.2`. It installed all 14 expected patches against
  the current `0.109.1` game assemblies, then removed every smoke-test-owned
  patch successfully.
- This proves runtime patchability in an isolated process. It does not exercise
  Godot initialization, ModManager lifecycle, Steam, UI or multiplayer.
- Nothing was installed into the live game and Workshop item `3772631781`
  remains on the previous private build pending an explicit live multiplayer
  test and update decision.

### 19:12 - Isolated game and local ENet smoke tests

- Created hard-linked game copies below ignored `artifacts` directories and
  installed the candidate only in those copies. Per-process `APPDATA`,
  `LOCALAPPDATA`, `TEMP`, logs and NullPlatform client IDs were isolated;
  `--force-steam=off` and `--disable-crash-handler` prevented Steam and crash
  reporting from participating. The live game, live Mods and Steam Workshop
  content were not changed.
- A real headless STS2 startup loaded `CoopGuard, Version=0.2.0.0`, ran its
  initializer and captured one loaded Mod, two files and 50,499 bytes in
  11 ms. The process returned zero.
- A matching two-player local ENet join passed the native initial-game-info
  check. Client 1000 sent its lobby join request and received a two-player
  response.
- A deliberate same-ID/same-version mismatch added one 37-byte marker to only
  the client package. The host digest remained
  `747fd8d47c3571086ab4f467d78564022afab145c56bcff6b5f047fc7c845b86`;
  the three-file client digest became
  `b614cb2a54ae02c6d0d246552d4c7c6e7c95e0dc399e7da5cc4eebd0060b00d6`.
  STS2 reported `Mod mismatch` immediately after initial game info, before the
  client sent a lobby join request. The host recorded a handshake-stage
  disconnect.
- A matching three-player local ENet join also passed. Client IDs 1000 and
  2000 both sent join requests; the responses contained two and then three
  players.
- Headless `--quit-after` shutdown emits Godot dummy-renderer RID/resource
  cleanup warnings. They occur after the connection evidence and contain no
  CoopGuard exception.
- Still unclaimed: clicking ready and beginning a fresh run, loaded-run
  confirmation, running-game rejoin, the complete current 32-Mod loadout, and
  real Steam transport. Those require an interactive game test.

### 19:28 - Complete current Mod-set ENet handshake

- Built two independent isolated copies from the current 32-enabled-Mod
  profile and added CoopGuard. Before startup, all 404 package files matched
  by SHA-256 with zero differences.
- The copied profile preserved an existing dependency inconsistency:
  BetterSaveSlots was enabled while its required JmcModLib was disabled. STS2
  correctly refused BetterSaveSlots with its native missing-dependency error.
  JmcModLib was enabled only in both isolated test profiles; the live settings
  were not changed.
- With that dependency satisfied, each peer loaded 34 Mods: the current 32,
  JmcModLib and CoopGuard.
- Both peers captured 34 Mods, 154 files and 262,689,475 bytes with digest
  `d4ac0504400fcc7f71f45618a88f01de84e144a3b64a8b93c73c7d87642278bf`.
  The client repeated the same full capture immediately before joining.
- The client received `ClientLobbyJoinResponseMessage Players: 2 Ascension: 0`,
  confirming that the native initial-game-info check accepted the complete
  isolated set and the lobby join completed.
- OnlineExchange emitted two `add_child()` lifecycle errors on each peer, from
  `EnsureCardArtStartupBootstrapNode()` during both `Initialize()` and
  `NGameReadyPatch.Postfix(NGame)`. OnlineExchange still initialized, its
  card-art recovery completed, both CoopGuard captures matched and the lobby
  join succeeded. This is third-party headless lifecycle noise, not a
  CoopGuard exception.
- The test retained per-process data directories, `--force-steam=off` and
  copied package roots. The live game, Mods, Workshop content and settings
  remained untouched.
- A write-path audit found that OnlineExchange `1.2.0` is the only enabled Mod
  that automatically writes inside its own package at startup. Both full-set
  peers therefore used physical package copies; hard-link inspection confirmed
  that its DLL and generated log belonged only to their respective copies.
- Other automatic writes from the enabled set use Godot user data. RouteSuggest
  can write `mods/RouteSuggestConfig.json` only when the user saves its
  settings; import, export and update actions in other Mods were not invoked.
  No broader fingerprint exclusion is justified by the observed behavior.

### 19:50 - Complete-set fresh-run start

- Added an ignored, artifacts-only test driver that calls the game's native
  lobby `SetReady()` method after both isolated peers are present. Its first
  manifest deliberately exposed a malformed dependency declaration: STS2
  reported a Mod load failure, each peer received a different process-stable
  CoopGuard error token, and the native handshake rejected the join. This
  confirms that a game-reported dependency/load error fails closed.
- Replaced the dependency with STS2's object form and repeated the test with
  the repaired complete set. Both peers loaded 35 Mods and captured 156 files,
  262,697,047 bytes and digest
  `28acbd10e80f7921669df8f0738c7278d543429b25929930510aaab26685fe96`.
- Host ID 1 and client ID 1000 both became ready through the native lobby API.
  Each performed the final full capture, received the other peer's ready
  state, and logged `Embarking multiplayer` with the same seed and characters.
  Both reached the map while the local ENet connection remained live.
- Reaching the map does not itself create `current_run_mp.save`; loaded-run
  coverage therefore remains open. The test driver source now requests the
  game's native `SaveManager.SaveRun(null, saveProgress: false)` after host
  embark, but that revision has not yet passed compilation or runtime testing.

### 20:00 - Version 0.2.1 audit fixes staged in source

- Closed a client time-of-check/time-of-use window by adding a first-priority
  quick validation immediately before
  `JoinFlow.HandleInitialGameInfoMessage()` completes the native initial-info
  task. Failure suppresses the handler and disconnects with the game's
  `ModMismatch` reason. This is especially relevant to running-game rejoins,
  which have no later lobby-ready gate.
- Audited `Sts2SkinManager 0.27.1` and confirmed that its public
  `ManagedPckRegistry` can contain active PCKs from Mods that ModManager marks
  disabled. The current profile includes such a mounted `Mesugaki.pck`, so
  loaded-Mod-root hashing alone was incomplete.
- Added an exact-version adapter for the audited Skin Manager registry and
  STS2 build `v0.109.1` (`c8c577f6`, main assembly hash `195020890`) and its
  bundled .NET `9.0.7`. It snapshots the observed mount sequence and hashes
  each mounted PCK's length and bytes without placing its absolute path in
  canonical/network data. Game-build, registry-shape, runtime, limit,
  reparse-point, ordering or freshness failures reject multiplayer.
- The first audit revision would have made Skin Manager's zero-delay
  vanilla-body overlay look like a post-startup mutation on every restart.
  Added one guarded two-frame settlement pass: it refreshes only startup PCK
  mounts and only before the compatibility entry has been exposed. Mounts
  after that point remain sticky and require a restart.
- Bumped the compatibility prefix, assembly and manifest to version `0.2.1`;
  added mounted-PCK self-check cases and raised the PatchAll expectation from
  14 to 15 methods.
- These `0.2.1` changes are source-staged only. Build, self-check, PatchAll,
  complete-set handshake, mismatch, fresh-run and loaded-run gates must be
  repeated before installation or Workshop update.

### 20:10 - Workshop state rechecked

- Opened Steam's authenticated Workshop UI read-only and confirmed the only
  posted STS2 item is `3772631781`, **CoopGuard - Multiplayer Mod Check**.
- The item remains hidden, contains the 22.852 KB `v0.1.0` upload, and still
  describes the original fresh-lobby-only behavior.
- No title, description, visibility, content or subscription state was changed.
  The item must remain untouched until the `v0.2.1` runtime gates pass.

### 00:05 - Version 0.2.1 static release gates

- Release build with warnings as errors passed with zero warnings and errors.
- `FingerprintSelfCheck`, production/test formatting and `git diff --check`
  passed. The only diff-check output was Git's existing LF-to-CRLF warning.
- The Harmony smoke ran under STS2's bundled .NET `9.0.7`, patched all 15
  expected methods against the `0.109.1` assemblies and removed its patches.
- Preserved the immutable two-file candidate at
  `artifacts/staging/v0.2.1-20260728-0005`:
  - `CoopGuard.dll`, 61,952 bytes,
    SHA-256 `dcfdae30cb4831a020ad7ecfa3f7905db73af6815732aeeca4556afcd80e674c`;
  - `CoopGuard.json`, 323 bytes,
    SHA-256 `33553f9040ec1c029bc4b5eaa2a005e30c0518255b7becf66d58ed0f21d37c36`.
- The manifest is `v0.2.1`, the assembly is `0.2.1.0`, and the stage contains
  exactly those two independent regular files.

### 00:31 - Version 0.2.1 complete-set runtime matrix

- Every process used a physical isolated Mod-package copy, isolated
  `APPDATA`/`LOCALAPPDATA`/`TEMP`, NullPlatform IDs and
  `--force-steam=off`. The live game and Steam transport were not involved.
- Matching fresh-run peers loaded 35 Mods and captured 161 files. They received
  the native two-player lobby response, both became ready, embarked with the
  same seed and wrote a native multiplayer save for the loaded-run test.
- Adding one marker only to the client's CoopGuard package changed its digest
  while keeping the same Mod ID and version. STS2 rejected it with native
  `Mod mismatch` before `ClientLobbyJoinRequestMessage`.
- The saved-run test completed native load-join request/response, both ready
  confirmations and `LobbyBeginLoadedRunMessage`; both peers loaded the same
  multiplayer run with matching final digests.
- An artifacts-only Harmony test hook changed the client package immediately
  before CoopGuard's initial-info prefix. CoopGuard rejected the initial game
  info with `ModMismatch`, and the client never sent a lobby join request.
- With Skin Manager's vanilla-body overlay enabled, both peers first captured
  162 files and digest
  `3d1ec4ca33782fce45da6857319a857d5ad51d4924b30a56e4b215fc9f06d17b`.
  After `ATA_IronClad_vanillabody.pck` was generated and mounted, the guarded
  two-frame settlement recaptured 163 files and digest
  `d86ba695a30e0ef8f2d39f828f823c5b9f5ded23828facc116be9cc52dcc8a82`.
  Both peers retained that settled digest through ready and embark.
- No accepted scenario logged a CoopGuard exception, rejection or restart
  requirement. The negative scenarios failed at their intended gate.

### 00:49 - Running rejoin and three-player regression

- The first rejoin harness revision used ENet's immediate-disconnect option,
  which does not send STS2's application-layer disconnection packet. The
  artifacts-only driver was corrected to call the game's normal graceful
  `Disconnect(NetError.Quit)` path; production CoopGuard code was unchanged.
- The host then logged peer 1000 disconnected with `Quit`. The same isolated
  client ID restarted with the same package digest, received
  `InitialGameInfoMessage State: Running`, sent `ClientRejoinRequestMessage`,
  and received `ClientRejoinResponseMessage`. CoopGuard did not reject or
  throw.
- STS2 `0.109.1` then disconnected that peer with its native
  `RunInProgress` reason. The current game UI does not restore the client to
  the map; the completed request/response handshake is the available rejoin
  boundary.
- A final three-player run used IDs 1, 1000 and 2000. All peers captured 35
  Mods, 161 files and 298,987,383 bytes with digest
  `6b3628da634068072abb72ada6d7da8522efa3996c4672d72396894bef8c267f`,
  became ready with three peers and embarked with seed `320PBPEAZ0TA`.
- All exact isolated STS2 and crashpad processes were stopped after each
  scenario.

### 00:59 - Live dependency setting repaired

- Re-read the active Steam profile only after confirming that no STS2 process
  was running. The live settings still had global Mods enabled and
  BetterSaveSlots enabled, but its installed dependency JmcModLib disabled.
- Atomically changed only `JmcModLib.is_enabled` from `false` to `true`.
  Independent text comparison proved that this was the sole change; JSON
  parsing and CRLF counts also matched.
- Current settings SHA-256:
  `87d1a0d1ff2afd6cc7da4484ec2182bac98864c041440718be840a5ec42958a9`.
- Recoverable pre-change backup:
  `settings.save.coopguard-pre-v0.2.1-20260728-005913.bak`, SHA-256
  `653d502556dae9e41e6e817e170299a0b482ec00a85d3ce84c33ec1889f335661`.
- The isolated repaired profile had already confirmed JmcModLib `1.7.0` and
  BetterSaveSlots `1.2.0` both initialize without the native missing-dependency
  failure. The live game was not launched after the setting edit.

### 01:46 - Version 0.3.0 bilingual fatal diagnosis

- Added a dependency-free incident explainer and integrated it at STS2's two
  authoritative error UI paths:
  - `NErrorPopup.Create(NetErrorInfo)` for terminal multiplayer/network
    failures;
  - `NGame.ReturnToMainMenuWithInternalError(Exception)` and its native
    internal-error popup for managed errors that force the run back to menu.
- Replaced the old local fingerprint-failure popup body with the same
  structured diagnosis format.
- Added built-in Chinese and English explanations for state divergence,
  effective-package and Mod-list mismatch, transport and handshake timeouts,
  game/data-model mismatch, offline/secure-connection/hosting/platform
  failures, missing dependencies, incompatible APIs, Harmony patch failures,
  explicit soft-lock exceptions and unknown internal errors.
- Every diagnosis now separates error code, confidence, root cause, evidence
  and next action. State divergence does not blame the last card/action or one
  Mod without stronger evidence; a third-party stack frame is described as the
  failure location rather than automatic proof of the original cause.
- Language follows `LocManager.Instance.Language`: `zhs`, `zht` and other
  `zh*` values use Chinese; all other languages fall back to English.
- Subscribed to STS2's global log callback but retained only the latest 80
  emitted warning/error messages in memory. Logs are supporting context for an
  already-authoritative failure; an ordinary `Error` never triggers a popup.
- Diagnostic text redacts Steam IDs, IP endpoints, credentials and absolute
  paths. Raw logs, fingerprints, settings, saves and account data are neither
  persisted by this feature nor transmitted to peers.
- Bumped the assembly and manifest to `v0.3.0`; the native compatibility wire
  entry remains version 3 because its format did not change.
- Validation completed against STS2 `0.109.1`:
  - Release build with warnings as errors: zero warnings and errors;
  - fingerprint/incident self-check passed, including Chinese/English text,
    common classification, normal-quit exclusion and privacy redaction;
  - formatting verification passed;
  - Harmony smoke patched and removed all 18 expected targets under the
    bundled .NET `9.0.7` runtime;
  - isolated real-game English and Chinese `StateDivergence` popups were
    created and installed into the native modal container;
  - an isolated English `MissingMethodException` internal-error popup resolved
    to `CG-MOD-API-INCOMPATIBLE`;
  - an isolated matching two-client ENet run joined, readied and embarked with
    identical fingerprints on both peers.
- The first two-client orchestration attempt used PowerShell's reserved
  `$Host` variable, so it started only the client and produced no multiplayer
  result. The corrected attempt used explicit process variables; both exact
  isolated processes exited after the successful run.
- Headless shutdown again emitted the known dummy-renderer RID/resource and
  occasional worker task cleanup warnings after the relevant evidence.
- Preserved the final two-file candidate at
  `artifacts/staging/v0.3.0-20260728-0148`:
  - `CoopGuard.dll`, 99,328 bytes, SHA-256
    `4326b6596ff341c6b650d840fd6bace89d063efe0b70b0dc14a34c3b4bc406e9`;
  - `CoopGuard.json`, 352 bytes, SHA-256
    `b728799633610c62dc0f66c794fbaada48d2de685ab34e95f0dbb28077839952`.
  The assembly version is `0.3.0.0` and the manifest version is `v0.3.0`.
- No build was installed into the live game.

### 02:53 - Version 0.3.0 uploaded privately

- Updated existing Workshop item `3772631781` in place through Mega Crit's
  official ModUploader and the signed-in Steam client; no duplicate item was
  created.
- Uploaded exactly the validated two-file candidate: 99,328-byte
  `CoopGuard.dll` and 352-byte `CoopGuard.json`.
- Added the v0.3.0 fatal-diagnosis description and change note.
- Reopened the item in Steam and confirmed the 99.680 KB content size,
  v0.3.0 change note, updated timestamp and `Hidden` visibility.

### 02:56 - Version 0.3.0 pushed to GitHub

- Committed the v0.3.0 implementation and publication evidence as `06ac02e`
  (`Add fatal multiplayer diagnostics`) and pushed the existing
  `agent/coopguard-v0.2.1` branch.
- Updated draft PR #1 against `main` to describe the full validation hardening
  and fatal-diagnosis scope:
  `https://github.com/LunarKernel/sts2-coop-guard/pull/1`.

### 15:44 - Version 0.3.1 local health and copyable diagnostics

- Reused STS2's native UI instead of adding a PCK or custom scene:
  - a successful multiplayer ready check now shows
    `NFullscreenTextVfx` for 0.5 seconds without blocking the lobby;
  - `Ctrl+F8` runs the existing bounded freshness check and opens a local
    health/soft-lock evidence snapshot through `NErrorPopup`;
  - CoopGuard-owned diagnostic popups relabel the native report button to
    `Copy diagnosis` / `复制诊断` and write the report through Godot's system
    clipboard API.
- Associated reports with their owning popups through a standard-library
  `ConditionalWeakTable`; ordinary STS2 error popups remain unchanged.
- Copyable reports include the incident code/body, UTC time, game version,
  connection/run status, package counts/status and the latest eight
  warning/error messages. Every field is passed through the existing
  redactor; raw package digests, paths, Steam IDs, IP endpoints and
  credentials are excluded.
- Manual snapshots do not declare every pause a soft lock, upload telemetry,
  add a network message, repair divergence or mutate combat/run/save/RNG
  state.
- Bumped the assembly and manifest to `v0.3.1`; the native compatibility wire
  entry remains version 3 because its format did not change.
- Validation against STS2 `0.109.1` completed:
  - final Release build with warnings as errors: zero warnings and errors;
  - fingerprint/incident self-check passed, including the health report,
    `Ctrl+F8` instructions and copied-report privacy;
  - formatting and `git diff --check` passed;
  - bundled .NET `9.0.7` Harmony smoke patched and removed all 21 expected
    targets;
  - a hidden, normally rendered isolated STS2 instance verified the manual
    health popup, exact `Copy diagnosis` label and a 926-byte clipboard
    report;
  - a second isolated rendered run verified the fatal
    `StateDivergence` popup and a 1,206-byte clipboard report;
  - matching isolated ENet host/client instances produced the same effective
    package digest, both displayed the successful ready status, and both
    embarked on the same multiplayer run.
- Initial isolated launches intentionally exposed two harness constraints:
  Steam initialization had to be disabled with the game's own
  `--force-steam=off`, and a fresh profile had to reuse its isolated
  already-accepted Mod settings. The headless display server executed the
  copy handler but cannot read a system clipboard, so clipboard assertions
  were repeated successfully with hidden normal rendering.
- Preserved the final two-file candidate at
  `artifacts/staging/v0.3.1-20260728-1544`:
  - `CoopGuard.dll`, 107,008 bytes, SHA-256
    `0ee8ef25395e58d6d5b337ac8967034df799eb87f66da0d75fb2abaf270e791b`;
  - `CoopGuard.json`, 374 bytes, SHA-256
    `3dd6ea0f6317b11f7f04c858465a2371f89bb97f6c1cac0f5c3209a82ce8071a`.
  The assembly version is `0.3.1.0` and the manifest version is `v0.3.1`.
- No file was installed into the live game, and this version was not uploaded
  to Workshop or GitHub.

### 16:05 - Version 0.3.1 uploaded to Steam Workshop

- Updated existing private Workshop item `3772631781` in place with Mega
  Crit's official ModUploader; no duplicate item was created.
- Uploaded exactly the validated v0.3.1 candidate: 107,008-byte
  `CoopGuard.dll` and 374-byte `CoopGuard.json` (107,382 bytes total).
- Published the v0.3.1 health-status, manual snapshot and copyable-diagnostics
  description/change note while preserving `Hidden` visibility.
- Preserved the pre-upload workspace at
  `C:\SteamLibrary\steamapps\sts2-coop-guard-workshop.backup.pre-v0.3.1-20260728-1600`.

### 17:53 - Version 0.3.2 fail-closed and diagnosis hardening

- Installed the native gameplay-Mod-list sentinel before every other Harmony
  patch. If any later required patch fails, the sentinel remains active and
  emits a process-unique unsafe entry so multiplayer rejects the session
  instead of silently running without complete protection.
- Applied the exact tested STS2 build tuple check to every full fingerprint,
  not only the optional SkinManager path. This build supports STS2 `v0.109.1`,
  commit `c8c577f6`, main assembly hash `195020890`; an unknown build fails
  closed with a specific local explanation.
- Kept wire protocol 3 and added a shared protocol-family prefix so a missing
  CoopGuard peer, a different CoopGuard protocol and a peer-local verification
  failure are distinguished from a confirmed effective-package byte mismatch.
- Limited log-based refinement of ambiguous network failures to error-level
  entries captured in the previous 30 seconds. Pending managed exceptions
  expire after five seconds, and Mod/dependency attribution now examines inner,
  aggregate and reflection loader exceptions.
- Added exact but redacted exception details, source-unknown wording where no
  third-party assembly can be attributed, dynamic CoopGuard report versioning
  and explicit wording that `Ctrl+F8` performs a bounded metadata freshness
  check rather than rereading every byte.
- Expanded report redaction to current/future CoopGuard fingerprints, generic
  SHA-256 values, IPv6 endpoints, bearer/common credentials and UNC paths.
  No telemetry, custom network message, dependency or new configuration was
  added.
- Bumped the assembly and manifest to `v0.3.2`; protocol 3 is unchanged.
- Validation against STS2 `0.109.1` completed:
  - final Release build with warnings as errors: zero warnings and errors;
  - fingerprint/incident self-check passed, including missing/different guard
    protocols, peer verification failures, unsupported builds, unknown API
    source wording, report versioning and expanded privacy probes;
  - formatting verification and `git diff --check` passed;
  - bundled .NET `9.0.7` Harmony smoke patched and removed all 21 expected
    targets, proved a post-sentinel patch failure remains fail-closed and
    verified the exact game build tuple;
  - hidden normally rendered isolated runs verified the manual-health popup,
    clipboard report and an internal `MissingMethodException` report with the
    exact missing API but no unsupported Mod attribution;
  - matching isolated ENet host/client instances produced digest
    `ea1e57d38ae0e76b62c95c1e4788c668a87b06b56db5a54fc913b36e32705152`,
    both passed local verification and embarked with seed `KA35NBWCYTV4`.
- Test harness corrections were isolated from production code: arguments after
  Godot's `--` delimiter were not parsed as game options; a newly generated
  framework-dependent runtime config could not use the copied runtime; and a
  flat smoke-runtime directory lacked the parent `release_info.json`. Reusing
  the accepted isolated Mod profile and the fake game directory layout fixed
  those harness-only failures. The final smoke was rerun against the packaged
  DLL from the fake game layout and passed.
- Preserved the final two-file candidate at
  `artifacts/staging/v0.3.2-20260728-1753`:
  - `CoopGuard.dll`, 123,904 bytes, SHA-256
    `c337bde2ce8d9c915e8e63866c6dfd71aac6e05827723f08776d74bc0d158087`;
  - `CoopGuard.json`, 374 bytes, SHA-256
    `e989fc33736ce6a4948967ff888b5ab3ed2d65434a436c9ff490e47151cf73d6`.
  The assembly version is `0.3.2.0`, the manifest version is `v0.3.2`, and the
  package contains exactly those two files.
- No file was installed into the live game, and v0.3.2 was not uploaded to
  Workshop or GitHub. A real Steam two-account/two-machine session remains the
  final external validation step.

### 20:24 - Version 0.3.2 published

- Committed the v0.3.2 implementation as `482e4cb`
  (`Harden fail-closed diagnostics`) and pushed the existing
  `agent/coopguard-v0.2.1` branch to GitHub. Draft PR #1 remains the review
  target.
- Created the recoverable pre-upload backup
  `C:\SteamLibrary\steamapps\sts2-coop-guard-workshop.backup.pre-v0.3.2-20260728-202014`.
- Updated existing Workshop item `3772631781` in place through Mega Crit's
  official ModUploader; no duplicate item was created.
- Uploaded exactly the validated two-file v0.3.2 candidate: 123,904-byte
  `CoopGuard.dll` and 374-byte `CoopGuard.json` (124,278 bytes total).
- Steam displayed the updated 124.278 KB size, 28 Jul @ 8:24pm timestamp,
  v0.3.2 description and change note. The owner visibility menu showed
  `Public` selected.
- Steam temporarily hid the public item while its automated content-analysis
  check runs. This is a platform review state, not a private visibility
  setting.

### 11:03 - Version 0.3.3 identifies the differing Mod

- Fixed the root cause of generic Mod-mismatch reports: protocol 3 exchanged
  only one aggregate package fingerprint, so STS2 could prove that the peers
  differed but could not identify one package.
- Bumped the compatibility protocol to 4. A healthy peer now appends one
  native gameplay-Mod-list entry per loaded Mod, containing a base64url Mod ID
  and that package's SHA-256 digest, while retaining the aggregate fingerprint
  for load-order and whole-composition checks.
- Made each package digest independent of Mod load order; the aggregate digest
  still includes order. Mounted SkinManager PCKs are represented as one named
  component because they are not owned by individual native Mod manifests.
- The native mismatch popup now distinguishes:
  - the same Mod with different content or version;
  - a Mod present only locally;
  - a Mod present only on the host;
  - an aggregate-only difference such as load order, where no individual Mod
    can be named reliably.
- Kept the trust boundary narrow: peer-supplied Mod IDs are length-bounded,
  strict UTF-8 decoded, redacted and stripped of control characters before
  display. Raw component entries and hashes are removed from user-facing
  reports. No file contents, paths, Steam IDs or telemetry are exchanged.
- Validation against STS2 `0.109.1` completed:
  - Release build with warnings as errors: zero warnings and errors;
  - fingerprint/incident self-check passed, including different-content,
    one-sided, Unicode, malformed and control-character component cases;
  - bundled .NET `9.0.7` Harmony smoke patched and removed all 21 expected
    targets, preserved fail-closed behavior and verified the current game
    build tuple;
  - isolated ENet host/client instances with the same `MismatchFixture` ID but
    different package bytes produced `NetError.ModMismatch`; the rendered
    client popup passed `DIAGNOSIS_MISMATCH_OK mod=MismatchFixture` and did not
    expose the wire fingerprint;
  - a second isolated ENet run with identical package bytes gave both peers
    digest `a46233cd3ef3365ed242c95a2a8e453a26ba85633ace5be5b116c410ad4302fc`,
    both auto-readied, embarked, and the host wrote the native multiplayer
    save without `ModMismatch` or `StateDivergence`.
- Preserved the final two-file candidate at
  `artifacts/staging/v0.3.3-20260729-1103`:
  - `CoopGuard.dll`, 127,488 bytes, SHA-256
    `3d7efed0aba5d363b9b5cf21b72c0e0170b8cf31300c3b22a3ba6b366d74f90d`;
  - `CoopGuard.json`, 374 bytes, SHA-256
    `8593f5e5fdd000df9ffedda491fdeb24b509b7f166cb8c9e9b0a21989a2ed9da`.
  The assembly version is `0.3.3.0`, the manifest version is `v0.3.3`, and the
  package contains exactly those two files.
- No file was installed into the live game, and v0.3.3 was not uploaded to
  Workshop or GitHub.

### 11:16 - Version 0.3.3 published

- Committed the v0.3.3 implementation as `d1012cb`
  (`Identify mismatched Mods`) and pushed the existing
  `agent/coopguard-v0.2.1` branch to GitHub. Draft PR #1 remains the review
  target.
- Created the recoverable pre-upload backup
  `C:\SteamLibrary\steamapps\sts2-coop-guard-workshop.backup.pre-v0.3.3-20260729-111350`.
- Updated existing Workshop item `3772631781` in place through Mega Crit's
  official ModUploader and the signed-in Steam client; no duplicate item was
  created.
- Uploaded exactly the validated two-file v0.3.3 candidate: 127,488-byte
  `CoopGuard.dll` and 374-byte `CoopGuard.json` (127,862 bytes total).
- Steam displayed the updated 127.862 KB size, 29 Jul @ 11:16am timestamp,
  v0.3.3 description and sixth change note. The owner visibility menu showed
  `Public` selected.
- Steam temporarily hid the public item while its automated content-analysis
  check runs. This is a platform review state, not a private visibility
  setting.
