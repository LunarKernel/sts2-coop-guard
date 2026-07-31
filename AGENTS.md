# Repository instructions

## Goal

Build the smallest robust Slay the Spire 2 multiplayer toolkit that preserves
the fail-closed package guard while adding bounded collaboration, diagnostics,
high-player-count compatibility and host-authorized recovery.

## Safety rules

- Never mutate combat, run, player or RNG state to repair divergence.
- Only the audited recovery module may replace an active multiplayer save, and
  only with a checkpoint produced by a completed native save after host
  confirmation and unanimous current-peer readiness.
- Reuse the game's version, ModelDb, dependency, and runtime checksum checks.
- Treat every enabled Mod as relevant, even when its manifest says
  `affects_gameplay: false`.
- Exchange bounded hashes, normalized descriptions, capability proofs, consent
  state, plain user text, hand snapshots and RNG summaries only. Never transmit
  files, absolute paths, saves, account data, raw peer seeds or RNG state.
- Never interpret remote text as markup, a URL, a path, a format string or a
  command.
- RNG diagnostics may observe completed native calls but must never call,
  clone, advance or predict the game's RNG.
- A local hashing or protocol error must block readying rather than silently
  weaken validation.
- Do not install a build into the live game directory unless the user asks.

## Change discipline

- Keep the project dependency-free beyond assemblies shipped with STS2.
- Add no abstraction until two real callers need it.
- Comment safety boundaries and non-obvious multiplayer timing, not syntax.
- Record every material action, result, and design change in
  `docs/WORKLOG.md`.
- Add or update one runnable self-check for non-trivial pure logic.
- Before committing, run:
  - `dotnet build src/BetterCoop/BetterCoop.csproj -p:Sts2Path="<game path>"`
  - `dotnet run --project tests/FingerprintSelfCheck/FingerprintSelfCheck.csproj`

## Versioning

- `ProtocolVersion` changes when the wire format changes.
- The manifest version changes whenever distributed DLL bytes change.
- `min_game_version` must match the oldest STS2 API version actually tested.
