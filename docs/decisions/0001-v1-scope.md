# ADR 0001: Version 1 is a package-fingerprint lobby guard

- Status: accepted
- Date: 2026-07-27

## Context

STS2 already rejects incompatible game versions and gameplay Mod lists, hashes
its ModelDb IDs, and compares deterministic run state. It does not compare the
actual bytes of same-ID/same-version Mod packages, and non-gameplay Mods are
not a hard compatibility gate.

## Decision

Version 1 will:

1. Hash every loaded Mod's matching manifest, loaded DLL assemblies and
   declared PCK with SHA-256.
2. Preserve effective Mod load order in the digest.
3. Exchange the digest and normalized hash lines through one reliable
   broadcast `INetMessage`.
4. Block local ready and the host's final begin-run gate until every connected
   player has supplied an identical, error-free fingerprint.
5. Report exact differing hash lines without transmitting file contents or
   absolute paths.

The Mod itself is marked `affects_gameplay: true`, so STS2 guarantees every
peer has the same guard version before its custom message is used.

## Consequences

- Same manifest versions with different DLL/PCK bytes are detected.
- Mislabelled `affects_gameplay: false` Mods are still covered.
- Large PCK files are hashed once on first lobby entry and may add a short
  delay.
- External configuration is not covered in v1 because arbitrary Mods have no
  shared declaration of which settings affect deterministic state.
- Loaded-run lobbies are deferred until the fresh-run handshake has been
  runtime-tested; the known-safe fallback remains starting a new run.
- No attempt is made to repair an already-diverged run.
