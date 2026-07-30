# Repository instructions

## Goal

Build the smallest fail-closed Slay the Spire 2 multiplayer guard that prevents
a run from starting when peers are using different effective Mod packages.

## Safety rules

- Never mutate combat, run, player, RNG, or save state to repair divergence.
- Reuse the game's version, ModelDb, dependency, and runtime checksum checks.
- Treat every enabled Mod as relevant, even when its manifest says
  `affects_gameplay: false`.
- Exchange hashes and normalized file descriptions only; never transmit file
  contents, absolute paths, settings, saves, or account data.
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
