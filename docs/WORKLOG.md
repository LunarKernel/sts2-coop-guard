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

### 23:29 - Toolkit P0/P1 implementation and isolated runtime evidence

- Split Guard, diagnostics, runtime HUD, lobby and join observations into six
  explicit Harmony owners. Optional-owner rollback cannot remove Guard
  patches; assembly-wide `PatchAll` is no longer used by the production Mod.
- Added bounded local primitives: 1,024-entry/15-minute flight recorder,
  60-sample network histories, monotonic freshness, progress leases, stable
  session-only P#/shape/color identities, 50-entry alert center and
  first-failure optional-module fuse.
- Added the local multiplayer cockpit using only native STS2 observations:
  connection state, RTT/loss/heartbeat/loading, join stage, conservative wait
  reason/confidence, 30/60/90-second stall suspicion, timeline, 60-second
  network summary, alerts and pre-run native lobby re-entry.
- Re-entry is manual, memory-only, one attempt, ten-minute TTL and repeats the
  full Guard verification before calling STS2's native Steam lobby flow.
  Running-run rejoin remains explicitly Unsupported on game build `0.109.1`.
- Added a responsive top-right scroll container, keyboard-focusable controls,
  non-color P#/shape labels plus a fixed color token, alert acknowledgement,
  manual report saving and bilingual labels. The panel does not take focus
  when an alert arrives.
- Added opt-in-only persistence foundations. Reports stay in memory until the
  user presses Save, are redacted again before atomic write, and are bounded to
  20 files, 1 MiB each, 25 MiB total and 30 days. A bounded own-marker/log-tail
  crash review distinguishes native signatures from insufficient evidence.
- Corrected the alert-capacity edge case: when all 50 retained alerts are
  fatal, a new current fatal replaces the oldest fatal instead of being
  dropped.
- Validation against STS2 `0.109.1`:
  - fingerprint/persistence/alert self-check passed;
  - Release build with warnings as errors passed with zero warnings/errors;
  - Harmony owner/isolation smoke passed all 39 owner-target bindings and
    contained optional rollback;
  - isolated Godot component startup logged
    `TOOLKIT_COCKPIT_OK hudBytes=44 overviewBytes=386` and exited 0;
  - isolated ENet host/client profiles both loaded the same 225,034-byte Mod
    set, reported identical digest
    `c229363f66e8a5bfdf1f94e6259f8fad65c093ebc4dd333535f4e5f14e009468`,
    auto-readied at two players, embarked, and the host requested the native
    multiplayer save;
  - neither side logged `StateDivergence`, `4001 / Timeout`, fingerprint
    failure, initializer exception or Toolkit disablement.
- Runtime evidence is under
  `artifacts/runtime-toolkit-p1-20260729/_profiles`. The live game and
  Workshop were not modified.
- Added C7 native FMOD cues for all-ready, disconnect, rejoin and manual
  reconnect result. The local sound toggle is keyboard-focusable, every cue
  has an existing visual/timeline equivalent, identical cues are limited to
  one per two seconds, and all Toolkit cues are limited to three per ten
  seconds. No audio asset, microphone access or new dependency was added.
- Extended D5's bounded backend with newest-first listing, redacted bounded
  reads, exact generated-name validation, single-item deletion and clear-all.
  Self-checks reject path traversal and verify delete/clear without touching
  non-CoopGuard files. The richer selectable history UI remains part of the
  unfinished P1 cockpit work.

### 22:42 - Multiplayer Toolkit P0 foundation implemented

- Replaced the assembly-wide `PatchAll` initialization with explicit install
  transactions and independent owners:
  - `CoopGuard.guard.sentinel` remains installed first;
  - `CoopGuard.guard` contains only required compatibility gates;
  - `CoopGuard.diagnostics` and `CoopGuard.toolkit` fail open and roll back
    only their own patches.
- Added the dependency-free P0 primitives used by later Toolkit stages:
  immutable monotonic observations, a single 1,024-entry/512-byte bounded
  flight-recorder ring, per-session identity/membership epoch and a
  first-failure optional-module fuse.
- Added a main-thread `ToolkitNode` through an isolated `NGame._Ready` patch.
  It observes only native connection/loading state at 1 Hz, clears session
  state on disconnect/detach and contains every Godot callback exception.
- Extended the existing `Ctrl+F8` health popup with the first local cockpit
  overview. No custom network message, persistent file, game-state mutation or
  live-game installation was added.
- Extended the pure self-check with monotonic freshness boundaries, bounded
  UTF-8/control-character handling, recorder wrap/order, fuse behavior and
  session rollover.
- Added a tracked Harmony smoke project. Under the copied STS2 .NET `9.0.7`
  runtime it verified all 22 targets under their exact four owners and injected
  a missing optional target; only the failed owner rolled back.
- Validation:
  - `FingerprintSelfCheck` passed;
  - CoopGuard Release build with warnings as errors passed with zero warnings
    and errors;
  - Harmony owner/isolation smoke passed;
  - no build was installed into the live game.

### 2026-07-29 - Multiplayer toolkit design and acceptance specification

- Accepted the complete H1-H12, C1-C10, G1-G12, D1-D10 and F1-F8 feature
  catalog as the long-term product scope; this action produced specifications
  only and did not change, build, install or publish the Mod.
- Added `MULTIPLAYER_TOOLKIT_TECHNICAL_DESIGN.md` with 12 mandatory safety
  invariants, native-API reuse, privacy tiers, UI/accessibility rules, resource
  budgets, five implementation phases and release gates.
- Preserved the existing Guard as the only authoritative fail-closed layer.
  HUD, coordination, diagnostics and forensics are independently fail-open and
  cannot participate in ready/start decisions.
- Proposed ADR 0003 for a bounded optional diagnostics plane. It cannot be
  accepted until pre-allocation payload limits, sender membership validation,
  parser fuzzing and 2/3/4-client lifecycle tests are proven. Protocol failure
  disables only optional peer displays.
- Kept G9/G10 compatibility truth on the existing native Mod-list path rather
  than the optional diagnostics plane. A future wire change must bump the Guard
  protocol and pass the native ten-second handshake size matrix.
- Defined C9 as default-off and unanimous per-session consent, C6 as exactly
  user-initiated native rejoin, F1/F2 as allowlisted hierarchical digests at
  natural checkpoints, F4 as count-only observation with no RNG/checksum call,
  and F6 as push-only with no stored callback. Declared deterministic-setting
  digests freeze with the Guard entry; a different late publish becomes sticky
  restart-required rather than silently changing the handshake value.
- Added split per-feature acceptance criteria and a 52-row master traceability
  matrix. Every H/C/G/D feature has a normal and abnormal `AT-*` case; every F
  feature has normal, abnormal and degraded `CG-TST-*` cases.
- Added the layered test plan covering unit/component/contract, isolated
  2/3/4-client, E2E, soak, upgrade, security/privacy, performance and
  accessibility validation plus a developer-only fault catalog.
- Engineering references were limited to official Google SRE/engineering
  practices, Google test-size guidance and Apple HIG feedback, alerts,
  accessibility and privacy guidance.
- Independent API/reliability/security reviews found that STS2 v0.109.1 has no
  production consumer for running-run rejoin: production returns
  `RunInProgress` and the debug path marks it unimplemented. C6 is therefore
  limited to audited pre-run lobby re-entry; running recovery remains in scope
  but is explicitly Unsupported until a future build passes a real recovery
  contract and two-client test.
- Review hardening split F6's Guard settings endpoint from its optional
  diagnostics endpoint, required independent Harmony owners/install
  transactions, added native and custom deserializer pre-allocation gates,
  one shared peer scheduler, C9 membership-epoch commit barriers, a concrete
  public-only `F1SchemaV1`, exact save-byte sidecar binding and opt-in persistent
  report history.
- Added stable cross-feature protocol/isolation tests and positive F1/F2/F4
  multiplayer tests. Mixed CoopGuard releases are no longer described as
  coexisting: strict package equality rejects them before lobby, while
  Diagnostics negotiation covers same-package local availability only.
- Final static specification validation passed:
  - technical catalog contains exactly 52 unique features with none missing;
  - H/C contains 22 sections and 45 unique AT cases; G/D contains 22 sections
    and 44 unique AT cases;
  - the test plan contains 46 unique engineering tests; all 32 F/CORE
    requirements map to at least one test and every test appears in the master
    traceability matrix;
  - Markdown table shapes, trailing whitespace and `git diff --check` passed.
- No source, project, manifest, package or live-game file changed, so no build
  or client runtime was performed for this documentation-only action.

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

### 2026-07-30 00:47 - Toolkit P1 lifecycle fix and first P2 governance slice

- Fixed the cockpit lifecycle at its shared root. A dynamically added C# Godot
  node did not reliably receive `_Ready`/`_Process`; attachment now performs an
  idempotent explicit initialization and a persistent native
  `NControllerManager._Process` patch drives the bounded refresh. The optional
  runtime remains under its own fail-open Harmony owner.
- Added the `Ctrl+F7` control panel, keyboard-focusable scrolling controls,
  selectable manual report history, native sound cues with rate limits, and
  explicit environment save/copy/compare actions. Report history remains
  opt-in and bounded; no report is written automatically.
- Added the first environment-governance implementation:
  - G1/G2 mismatch output now preserves the concise summary and adds a
    per-Mod direction/confidence/read-only repair table plus host/local/common
    repair groups, with 100 displayed and 256 parsed item bounds;
  - G3 exposes a read-only Local Mod Doctor using native `ModManager`
    states/errors, duplicate IDs, source overlap and existing package
    freshness. It does not claim undeclared dependency safety;
  - G4 reuses `ModFingerprint.ValidateQuick` at a five-second interval and
    preserves sticky restart-required behavior. Changed package IDs are
    included when the existing bounded metadata scan can attribute them;
  - G5/G9 export a deterministic schema-1 environment lockfile with strict
    package, category and size bounds and a hostile-input parser. Export is
    explicit, at most 512 KiB, contains no file content or absolute path, and
    can compare an imported lockfile by difference category;
  - G8 exposes read-only Harmony owner/type/priority/before/after metadata,
    caps processing at 5,000 patch records, and never unpatches or reorders;
  - G10 adds a push-only 32-byte settings-digest API. Publisher identity is
    authenticated after native Mod association and before fingerprint freeze;
    traversal, conflicting publishers and late changes are rejected. Digests
    enter the strict aggregate Guard fingerprint and lockfile settings
    category; actual setting values never enter CoopGuard;
  - G12 adds a schema-checked, local-only exact-match rule catalog for the
    current build. It has no network updater, executable content or ability to
    override native/Guard blocking.
- Hardened lockfile parsing against null collections/strings, unknown
  categories, duplicate Mod/provider IDs, truncated JSON, unsupported schema,
  oversized input and failed-capture snapshots. The headless clipboard test
  was corrected to test malicious data directly against the parser because the
  Godot headless clipboard does not reliably accept a second write.
- Validation against STS2 `v0.109.1`:
  - Release build with warnings as errors passed with zero warnings/errors;
  - fingerprint/incident self-check passed, including the G1 table and grouped
    repair text without digest leakage;
  - game-bundled .NET `9.0.7` Harmony smoke passed all 39 owner-target
    bindings and contained an injected optional-owner rollback;
  - isolated headless runtime contract passed
    `TOOLKIT_COCKPIT_OK hudBytes=142 overviewBytes=701
    environmentBytes=1314`, including focusable controls, deterministic
    export/re-import, traversal/oversize/truncation/unknown-category rejection,
    G10 fixture publication, read-only Doctor/Harmony reports and exact versus
    near-miss G12 matching.
- This is a partial P2 milestone, not completion of the 52-feature product.
  G3 manifest dependency-graph fixtures, G6/G7 save sidecars, full G9 wire
  allocation audit, G10 declaration-file enforcement, D6/D9, dual-client
  regression and later phases remain open. Nothing was installed into the
  live game, committed, pushed or uploaded.

### 2026-07-30 01:20 - Toolkit P1 preferences and accessibility contract

- Added one schema-checked, fail-safe local preference file for Toolkit-only
  presentation settings. It is capped at 4 KiB, parsed with a depth bound,
  rejects invalid/non-finite values, falls back to safe defaults, and is
  written only after an explicit user action through temp-file flush plus
  atomic replacement.
- Added immediately applied controls for master sound, native FMOD volume,
  ready/network/rejoin/result cue categories, 100-200% text/UI scale, reduced
  motion and high contrast. Every sound event retains its existing visual and
  timeline equivalent; no audio asset or dependency was added.
- Added a waiting-for-local-player cue and routed every cue through the
  existing global/per-kind rate limiter. Category preferences cannot affect
  the Guard, lobby readiness, run state, saves, RNG or networking.
- Completed the `Ctrl+F7` focus lifecycle: opening the control panel moves
  keyboard focus to its first control, closing collapses all settings and
  governance controls, and valid prior game focus is restored. Passive HUD and
  alerts never steal focus.
- Reused an inherited native Godot `Theme` for scaling and contrast. A
  dual-client regression exposed invalid `Theme.Clear*` calls on an initially
  empty theme; the shared transition logic now clears contrast keys only when
  moving from enabled to disabled.
- Validation against STS2 `v0.109.1`:
  - preference round-trip, corrupt/oversized fallback and temp-file cleanup
    checks passed in the dependency-free self-check;
  - Release build with warnings as errors passed with zero warnings/errors;
  - Harmony owner/isolation smoke passed all 39 owner-target bindings and
    contained optional rollback;
  - an isolated real Godot run passed
    `TOOLKIT_COCKPIT_OK hudBytes=142 overviewBytes=701
    environmentBytes=1314`, including immediate 200% inherited font size,
    bounded atomic preferences, settings controls and focus restoration;
  - the final isolated ENet host/client run produced the same digest
    `5c6a498c0e20f1082939b2f141d207b32689a0bfb55919cad1e8895dd8362e73`
    on both sides, both passed full verification and auto-ready, both
    embarked, and the host requested the native multiplayer save;
  - neither side logged `StateDivergence`, `4001 / Timeout`, Toolkit
    disablement, initializer/unhandled exceptions or the fixed theme error.
- P1 still lacks the release-gate eight-hour soak and captured 1280x720 visual
  QA, so it is not marked complete. Nothing was installed into the live game,
  committed, pushed or uploaded.

### 2026-07-30 05:25 - Version 0.4.0 multiplayer Toolkit implementation complete

- Implemented the requested H1-H12, C1-C10, G1-G12, D1-D10 and F1-F8
  production paths. Added a 52-row traceability record in
  `docs/MULTIPLAYER_TOOLKIT_IMPLEMENTATION_STATUS.md`; unsupported or
  qualification-pending portions are explicit instead of inferred.
- Added the optional diagnostics protocol as a bounded plane independent from
  Guard Protocol 4:
  - fixed major/minor envelope, 16-peer and payload limits, monotonic sequence
    validation, targeted Hello/ACK with bounded retry and all-peer capability
    gates;
  - optional failures, malformed packets and unsupported features fail open
    without changing native networking, ready/start or the Guard result;
  - consent, quick-status, hand, checkpoint, checkpoint-result and
    contribution control traffic uses small per-peer bounded pending queues,
    cleared synchronously on revoke, member change or peer disablement.
- Completed collaboration and display features:
  - C1 five-value localized quick statuses have no free text, use a
    session-wide capability gate and reliable bounded host fan-out;
  - C9 exact hand sharing is default-off, unanimous, revision-bounded,
    host-relayed, safe for unknown card IDs and revoked by roster changes;
  - C10 records only attributable public action/damage/block/healing facts,
    keeps local display separate from sharing, sends changed-only five-second
    snapshots, rejects non-monotonic updates and labels results as
    entertainment-only rather than a score;
  - H9 caches only successful environment codes, H10 retains five local load
    durations, and D2 lowers confidence when native connection evidence
    conflicts with a phase/action-based wait inference.
- Completed forensics and author APIs:
  - F1 uses audited native checkpoint constructors and separately installed
    RNG observers. A failed optional owner is rolled back as one unit and the
    category becomes `Unsupported`, never a fabricated zero;
  - F2 retains the last common and earliest observed divergent checkpoint,
    subject ordinal, comparable category mask and actual first-divergence
    mask, then shares the bounded result with all capable peers;
  - F6 authenticates the loaded publishing Mod, accepts only push-only bounded
    state/event records and enforces per-publisher rate limits. Its optional
    path is physically separate from the fail-closed G10 settings digest;
  - F8 exists only in the tracked Debug test driver, requires a marked
    isolated APPDATA root and has no trigger string in the production DLL.
- Split optional Harmony owners for checkpoint, RNG and contribution
  observers. A missing target now rolls back only its complete feature owner;
  Guard and unrelated Toolkit modules remain installed.
- Real-client regression findings and root fixes:
  - one protocol check sampled during the native lobby-to-run service rollover;
    the test now waits for the same negotiated session twice and uses the
    public all-peer capability gate;
  - C1 could partially broadcast when a periodic state message had consumed a
    peer token. A maximum-three latest-status queue now guarantees eventual
    delivery without unbounded memory;
  - a client incorrectly rejected an authenticated host status until its own
    aggregate matrix arrived. The host now owns the all-peer gate while clients
    validate the negotiated host and fixed message type;
  - the collaboration test previously let the host enter consent before a
    slower peer reached its capability barrier. Its bounded wait now covers
    the worst protocol window;
  - Godot headless has no system clipboard. The UI test now invokes the same
    lockfile parser and dependency-aware planner core directly while still
    checking that every clipboard-facing control exists and is focusable.
- Final validation against STS2 `v0.109.1` (`c8c577f6`) and bundled .NET
  `9.0.7`:
  - Release build with warnings as errors passed with zero warnings/errors;
  - fingerprint/incident self-check and all bounded codecs passed with
    `COOPGUARD_FUZZ_ITERATIONS=1000000`;
  - Harmony owner/isolation smoke passed 62 owner-target bindings and contained
    an injected partial optional-owner rollback;
  - 3-peer collaboration/API run passed at
    `artifacts/f7-matrix/summary-20260729-210421.json`;
  - final isolated ENet 2/3/4 matrix passed at
    `artifacts/f7-matrix/summary-20260729-210502.json`;
  - the final versioned v0.4.0 DLL passed an additional 2-peer handshake at
    `artifacts/f7-matrix/summary-20260729-212434.json`;
  - one-shot diagnostics-handler failure recovered in the 2-peer run at
    `artifacts/f7-matrix/summary-20260729-210604.json`;
  - isolated Godot UI/governance contract passed
    `TOOLKIT_COCKPIT_OK hudBytes=105 overviewBytes=967
    environmentBytes=1314`;
  - byte inspection found none of `cgtest-fault`, `F8_FAULT`,
    `diagnostics-handler-throw-once` or
    `DiagnosticsHandlerThrowOnceFault` in the production `CoopGuard.dll`;
    the Release test driver retained only its refusal branch and contained no
    armed ID, injected marker or fault class.
- Created the exact two-file candidate at
  `artifacts/staging/v0.4.0-20260730-final2`:
  - `CoopGuard.dll`, 481,792 bytes, SHA-256
    `d4c70d458bb14d0edea919f0a9cf7a7f45b01371b743d578f9fa74d31af93a11`;
  - `CoopGuard.json`, 359 bytes, SHA-256
    `f41c4436d54ce14ece4e4b38f1b061ca47295843cfa303373317542ab497fe5c`.
- Deliberate release boundaries remain:
  - running-game one-click recovery is `Unsupported` on this game build;
  - F1 monster/public-effect categories lack audited stable public identities;
  - G9 local category comparison is active, but wire expansion is withheld
    pending native receiver allocation audit;
  - Steam transport and the eight-hour/2,000-client-minute soak remain
    controlled release gates and were not represented as completed.
- Nothing was installed into the live game, committed, pushed or uploaded.

### 09:33 - Version 0.4.0 published

- Created the recoverable pre-upload backup
  `C:\SteamLibrary\steamapps\sts2-coop-guard-workshop.backup.pre-v0.4.0-20260730-092347`.
- Updated existing Workshop item `3772631781` in place through Mega Crit's
  official ModUploader and the signed-in Steam client; no duplicate item was
  created.
- Uploaded exactly the validated two-file v0.4.0 candidate: 481,792-byte
  `CoopGuard.dll` and 359-byte `CoopGuard.json` (482,151 bytes total).
- Steam displayed the updated 482.151 KB size, 30 Jul @ 9:33am timestamp and
  v0.4.0 description. The owner visibility menu showed `Public` selected.
- Steam temporarily hid the public item while its automated content-analysis
  check runs. This is a platform review state, not a private visibility
  setting.
- The live game directory remained unchanged.

### 13:10 - BetterCoop v0.5.0 identity migration validated

- Per the requested breaking rename, replaced the current product identity
  throughout production code and tracked test tooling:
  - manifest ID/name and release filenames are now `BetterCoop`;
  - assembly, namespace, public diagnostics API, Harmony owners, report/data
    names and compatibility-family prefixes now use `BetterCoop`;
  - the test driver, settings declaration and all reflection fixtures were
    migrated with the production code.
- Raised the release to `v0.5.0` and Guard Protocol 5. The existing Workshop
  item will remain the distribution path, but every peer must update together,
  fully restart and create a new lobby.
- Updated the README, current technical/acceptance documents and Workshop
  metadata to describe the complete Guard, cockpit, coordination, diagnosis,
  environment, forensics, privacy and migration behavior.
- Release validation against STS2 `v0.109.1` (`c8c577f6`) passed:
  - Release build with warnings as errors: zero warnings/errors;
  - fingerprint, incident, persistence, collaboration and bounded-codec
    self-check: `BETTERCOOP_FUZZ_ITERATIONS=1000000`;
  - Harmony owner/isolation smoke: 62 owner-target bindings, optional rollback
    contained;
  - isolated ENet 2/3/4-client matrix:
    `artifacts/f7-matrix/summary-20260730-050103.json`.
- The first matrix invocation timed out before Mod initialization because its
  `SeedSettingsPath` incorrectly pointed to the test driver's declaration JSON
  instead of a game `settings.save`; both logs explicitly showed that the Mod
  warning had not been accepted. A new isolated seed enabled `BetterCoop` and
  `BetterCoopTestDriver`; the unchanged production binary then passed 2/3/4
  peers.
- Created the exact two-file candidate at
  `artifacts/staging/v0.5.0-bettercoop-20260730-final`:
  - `BetterCoop.dll`, 481,792 bytes, SHA-256
    `f1867f1c186510cac39981fc947981b2cfa22c82eb79143b6f5845aa5da37f90`;
  - `BetterCoop.json`, 401 bytes, SHA-256
    `dcec32c27817cad442ecf3fe835f4a5b6618a29c5b02f1efcd1d5623f84dea40`.
  The candidate contains no old-brand bytes.
- Prepared the Public Workshop workspace with the same two files and a
  complete feature description. The recoverable pre-v0.5.0 backup is
  `C:\SteamLibrary\steamapps\sts2-coop-guard-workshop.backup.pre-v0.5.0-bettercoop-20260730-131000`.
- Nothing was installed into the live game, uploaded to Workshop, committed or
  pushed in this step.

### 13:12 - BetterCoop v0.5.0 published

- Committed the complete identity migration as `2978283`
  (`Rename to BetterCoop v0.5.0`) and pushed the existing
  `agent/coopguard-v0.2.1` branch. Draft PR #1 remains the review target.
- Updated existing Workshop item `3772631781` in place through Mega Crit's
  official ModUploader and the signed-in Steam client; no duplicate item was
  created and existing subscriptions remain on the same item.
- Uploaded exactly the validated two-file candidate: 481,792-byte
  `BetterCoop.dll` and 401-byte `BetterCoop.json` (482,193 bytes total).
- Steam rendered the new `BetterCoop - Multiplayer Toolkit & Guard` title,
  full categorized feature description, 482.193 KB size, 30 Jul @ 1:12pm
  timestamp and eighth change note. The owner visibility menu showed `Public`
  selected.
- Steam temporarily hid the public item while its automated content-analysis
  check runs. This is a platform review state, not a private visibility
  setting.
- The live game directory remained unchanged.

### 14:58 - Bilingual Workshop description published

- Created the recoverable pre-edit backup
  `C:\SteamLibrary\steamapps\sts2-coop-guard-workshop.backup.pre-bilingual-description-20260730-145035`.
- Expanded the existing Workshop description in English and Simplified
  Chinese, with the English section shown first.
- The description is 6,957 UTF-8 bytes and the workspace remains `public`.
- Updated existing Workshop item `3772631781` in place; no duplicate item was
  created and Mod dependencies were unchanged.
- Steam rendered the English section before the Chinese section, retained the
  482.193 KB package, and showed `Public` selected in the owner visibility
  menu.
- Steam again showed its temporary automated content-analysis notice. This is
  a platform review state, not a private visibility setting.
- `BetterCoop.dll` and `BetterCoop.json` remained byte-for-byte unchanged; no
  gameplay, protocol, dependency or live-game files were modified.

### 15:32 - BetterCoop v0.6.0 next-version design drafted

- Added separate proposed technical design, acceptance and test-plan documents
  for C11 bounded peer text, H13 one-action teammate hand viewing, R1
  host-authoritative room-node rollback, G13 Multiplayer Limit Break
  compatibility and F9 seed/RNG analysis.
- Defined the required safety-contract ADR before implementation because peer
  text, save restoration and seed display intentionally change three existing
  repository invariants.
- Reused the existing bounded diagnostics envelope, C9 consent/hand snapshot,
  forensics hooks and atomic persistence pattern; proposed a separate
  fail-closed Run Control transaction instead of treating rollback as optional
  diagnostics.
- Bound the initial >4-player profile to Workshop manifest ID
  `STS2-MultiplayerLimitBreak` v0.1.3, its exact DLL hash and
  `STS2-RitsuLib >=0.4.13`. Added per-peer runtime capability proof and hard
  5/8-player loaded-run gates before R1 can be called supported.
- Documented the repository-wide Google C# migration and executable style,
  analyzer, warning, traceability and release-evidence gates as a separate
  behavior-neutral change before feature work.
- No runtime source, live game, Workshop item, GitHub state or published
  package was changed. Build and multiplayer tests were not run because this
  step produced design documents only.

### 2026-07-31 02:49 - BetterCoop v0.6.0 implementation and high-player ENet smoke

- Implemented C11 peer text, H13 one-action hand watch, F9 seed/RNG summaries,
  G13 Limit Break capability proofs and experimental default-off R1 room-node
  rollback under Guard Protocol 6 / diagnostics protocol 2.
- Added bounded atomic rollback checkpoints, SHA-256 activation/recovery,
  exact-roster two-phase Run Control, native loaded-lobby re-entry and
  explicit emergency recovery. Corrupt checkpoint metadata is isolated per
  entry and journal read/commit failures remain recoverable.
- Added repository-wide `.editorconfig`, build analyzers, warnings-as-errors,
  deterministic builds and native/Harmony API contract checks.
- Found and fixed three integration defects with Workshop `3747606832`
  v0.1.3 and RitsuLib v0.4.66:
  - ready was waiting for a sidecar that upstream sends only after ready;
  - BetterCoop consumed RitsuLib's 36-byte native trailer instead of advancing
    only its own length-prefixed envelope;
  - clients incorrectly required direct handshakes with every peer instead of
    accepting the host-relayed capability proofs of the star topology.
- The upstream source confirms `_remoteHostSettings` is populated only after
  `RunManager` exists. Lobby gates now require the full pre-run contract and
  matching local setting digest; strict post-run G13 (and R1) still requires
  `SettingsSynchronized`.
- Automated evidence against isolated STS2 v0.109.1:
  - Release build: 0 warnings / 0 errors;
  - persistence/protocol self-check: passed at 10,000 parser fuzz iterations;
  - ENet 2/4 baseline:
    `artifacts/f7-matrix/summary-20260730-181800.json`;
  - ENet 5 with Limit Break/RitsuLib:
    `artifacts/f7-matrix/summary-20260730-184815.json`;
  - ENet 8 with Limit Break/RitsuLib:
    `artifacts/f7-matrix/summary-20260730-184913.json`.
- The 5/8 results prove join, ready, embark, every-peer protocol negotiation
  and collaboration sentinels. They do not yet prove two combat turns,
  room/save/load coverage, R1 multi-client rollback, real Steam five-player,
  MP16 or the eight-hour soak.
- Nothing was installed into the live game, committed, pushed or uploaded.

### 03:19 - BetterCoop v0.6.0 RC1 engineering gates

- All four C# projects passed `dotnet format whitespace/style
  --verify-no-changes`; the naming rules distinguish private constants and
  static-readonly fields from mutable private fields.
- BetterCoop, HarmonySmoke and BetterCoopTestDriver Release builds completed
  with zero warnings and zero errors against the isolated STS2 v0.109.1
  fixture.
- FingerprintSelfCheck passed with 1,000,000 parser-fuzz iterations, including
  rollback archive/activate/recover/commit and Run Control protocol checks.
- The retained 5-player and 8-player Godot logs contain 5/5 and 8/8 protocol
  plus collaboration success sentinels respectively, with no protocol
  failure, collaboration failure, unhandled-exception or fatal marker.
- Tracked JSON/XML parsing and `git diff --check` passed. PSScriptAnalyzer was
  not run because the module is not installed; no dependency was added only
  for this check.
- Created the non-overwriting two-file candidate
  `artifacts/staging/v0.6.0-bettercoop-20260731-rc1`:
  - `BetterCoop.dll`: 635,904 bytes,
    SHA-256 `8080A4FF5CA739AB673FD97A629C5E3C35F9C5C17953052BC2989B18F296D5D2`;
  - `BetterCoop.json`: 431 bytes,
    SHA-256 `C334040720EF0E26B37A6DE6555BC3FB7D4D3893953EE69A8E93507E9862C601`.
- Binary scans found neither test fault IDs (`CG-FLT-`) nor the old
  `CoopGuard` brand. The live game, GitHub and Workshop remain unchanged.
