# ADR 0002: Reuse STS2's native Mod-list handshake

- Status: accepted
- Date: 2026-07-27
- Supersedes: ADR 0001 decisions 1 and 3 through 5

## Context

The version 1 all-peer fingerprint message had three avoidable failure modes:

1. In a three-player join, an existing client's one-time broadcast could arrive
   before the new client's handler existed and be lost permanently.
2. Remote strings were allocated before the Mod could enforce its size limits,
   and the host broadcast them before the handler validated them.
3. The custom lobby session covered only fresh `StartRunLobby` joins.

STS2 already compares `ModManager.GetGameplayRelevantModNameList()` before it
branches into fresh-lobby, loaded-lobby or running-game rejoin flows.

## Decision

Version 2 removes the custom `INetMessage` protocol.

After Mod initialization, BetterCoop recursively hashes the regular files in
every loaded Mod root and appends one synthetic package-digest entry to the
gameplay Mod list. STS2's existing `ModMismatch` path performs the peer check.
The only package-specific exclusion is OnlineExchange `1.2.0`'s known generated
cosmetic `user_data` tree and top-level `log.oejson`, `log.oejson.tmp` and
`log.oejson.backup`.

The fingerprint is precomputed after
`OneTimeInitialization.ExecuteEssential()`, before menu or command-line joins.
Clients fully revalidate before `JoinFlow.Begin`. Host connection callbacks
perform only a bounded file-list/length/mtime freshness scan before constructing
`InitialGameInfoMessage`; client begin-message handlers use the same quick scan.
The native Mod-list getter itself only reads a frozen in-memory entry, so
content hashing cannot consume the native handshake timeout.

Runtime replacement-Mod detection, a real late assembly-count change or a
changed package digest sets a sticky restart-required state. Local ready and
host final-start gates remain for fresh and loaded-run lobbies. Clients also
check freshness when receiving either begin-run message and disconnect with the
native `ModMismatch` reason on failure. All three loaded-run confirmation
implementations wrap their native `Task<bool>` so the host performs one final
full validation after any user prompt and before sending the begin message.

Package hashing is deliberately strict:

- all regular files below the Mod root, apart from the explicit OnlineExchange
  generated-data exception;
- active external PCKs exposed by the exact audited
  `Sts2SkinManager 0.27.1` registry on STS2 build `v0.109.1`
  (`c8c577f6`) and its bundled .NET `9.0.7`, including their observed mount
  order; unknown game, adapter or runtime shapes fail closed;
- raw relative paths, lengths and SHA-256, with normalized-path collisions
  rejected;
- actual loaded assembly relative paths, identities and module IDs;
- deterministic load/path ordering;
- no reparse-point traversal;
- aggregate limits of 4,096 files, 16,384 entries, 1 GiB and 8 MiB canonical
  text, enforced before further hashing/allocation;
- no absolute path or exception text in peer-visible data.

## Consequences

- Three-plus-player join ordering no longer needs a Mod protocol.
- Fresh, loaded and running-rejoin entries share the same native check.
- The Mod adds no new remote deserializer, broadcast or sender-identity trust.
- Native Mod-mismatch UI replaces exact remote file-line differences.
- Extra non-runtime files inside a package can conservatively cause a mismatch.
- The OnlineExchange exception avoids per-user cosmetic caches making every
  join disagree. It is version-bound and must be reviewed for later releases.
- External configuration and the package that originally created an old save
  remain outside the safe, non-mutating scope.
- The quick network-callback scan detects ordinary edits but not an adversarial
  same-length rewrite that restores its timestamp; this is accepted under the
  trusted-peer, non-anti-cheat threat model.
- A guarded two-frame settlement pass can refresh startup-only PCK mounts
  before the compatibility entry is exposed. A later mount invalidates the
  frozen fingerprint and requires a restart at the next validation gate.
- Already-running state cannot be rolled back; arbitrary mid-run disk edits are
  observed only at the next explicit validation gate.
- A hostile client can still modify or bypass its local checks; this is not
  anti-cheat.
