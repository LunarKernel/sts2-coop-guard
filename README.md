# STS2 Co-op Guard

`CoopGuard` blocks multiplayer ready/start when connected players have
different effective Mod package files.

Private Workshop test item:
https://steamcommunity.com/sharedfiles/filedetails/?id=3772631781

Version 1 deliberately complements STS2's built-in checks:

- STS2 continues to validate the game version, gameplay Mod list, ModelDb and
  deterministic run state.
- CoopGuard hashes every loaded Mod's matching manifest, loaded DLL assemblies
  and declared PCK with SHA-256.
- Peers exchange only a digest and normalized hash lines.
- `StartRunLobby.SetReady(true)` and the host's final begin-run check fail
  closed until all connected peers match.

It does not repair or overwrite game state. Configuration hashing and saved-run
fingerprints are intentionally deferred until a concrete, safe configuration
contract exists.

## Build

The Mod targets the same .NET version as STS2 and references only assemblies
shipped with the game:

```powershell
dotnet build src/CoopGuard/CoopGuard.csproj `
  -p:Sts2Path="C:\SteamLibrary\steamapps\common\Slay the Spire 2"
```

The package is written to `artifacts/CoopGuard/` and contains:

- `CoopGuard.dll`
- `CoopGuard.json`

Building does not install or modify the live game.

## Self-check

```powershell
dotnet run --project tests/FingerprintSelfCheck/FingerprintSelfCheck.csproj
```

## Current limitations

- v1 reports differences in the game log and in a popup only when the local
  player attempts to ready.
- v1 hashes package files, not external per-user configuration files.
- v1 guards fresh-run `StartRunLobby`; loaded-run lobbies remain native-only.
- It cannot prevent a deterministic bug shared by identical Mod binaries.

Development evidence and command results are kept in
[docs/WORKLOG.md](docs/WORKLOG.md).
