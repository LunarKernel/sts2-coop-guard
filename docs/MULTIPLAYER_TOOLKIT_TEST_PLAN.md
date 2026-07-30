# Multiplayer Toolkit test plan

## 1. Purpose

This plan defines the release evidence for the multiplayer-toolkit expansion
of CoopGuard. It covers:

- **F1** — hierarchical state digests;
- **F2** — first-divergence locator;
- **F3** — action/checkpoint journal;
- **F4** — RNG-count sentinel, without reading or advancing future RNG;
- **F5** — bounded peer heartbeat;
- **F6** — Mod-author diagnostics/determinism API;
- **F7** — automated two-, three- and four-client CI;
- **F8** — developer-only fault injection.

The plan extends, rather than replaces, the existing package-fingerprint,
fail-closed lobby-gate, incident-explanation and redaction checks.

## 2. Non-negotiable safety invariants

Every test level must preserve these invariants:

1. CoopGuard never repairs, rolls back, resumes, cancels or otherwise mutates
   combat, run, player, RNG or save state.
2. A test never calls `ChecksumTracker.GenerateChecksum` or any equivalent
   checksum entry point merely to obtain a test observation. Tests observe only
   checkpoints that STS2 generates naturally.
3. F4 reads only already-materialized RNG counters or observations. It never
   peeks at a future RNG value, clones an RNG to predict it, or calls `Next*`.
4. Diagnostic failure is fail-open for UI only. Package verification and
   Guard-protocol failures remain fail-closed; Diagnostics-protocol failures
   disable only the affected optional capability.
5. A stale, missing or contradictory observation is displayed as `unknown` or
   `degraded`; it is never promoted to `healthy`.
6. Network diagnostics are observational. They do not replace STS2 timeouts,
   disconnect peers, delay gameplay messages or change transport thresholds.
7. Peer-controlled strings and Mod-author API input are untrusted, bounded and
   redacted before display, logging or copying.
8. No packet or report contains file contents, absolute paths, Steam IDs, IP
   endpoints, credentials, saves, settings or future RNG data.
9. F8 runs only in an isolated copied game/profile. Its hooks are absent from
   the distributed Release DLL and cannot be enabled in the live profile.
10. Test shutdown targets only process IDs launched by that test. A failed
    harness must not terminate unrelated STS2 or Steam processes.

Static inspection and runtime sentinels must verify invariants 1–9. Absence of
an error log is not sufficient evidence.

## 3. Feature requirements

Requirement IDs are permanent and are the source of truth for traceability.

### F1 — hierarchical state digests

- `CG-REQ-F1-001`: At an STS2 naturally generated checkpoint, compute a fixed,
  versioned hierarchy of digests over allowlisted deterministic categories;
  never request an additional checkpoint.
- `CG-REQ-F1-002`: Canonicalization is culture-independent, stably ordered,
  read-only and bounded. UI/wire/report expose category labels and equality,
  not raw state values or full digest material. `F1SchemaV1` fixes every byte
  width/code/prefix/mask/sort key and has externally reviewed, hard-coded
  golden vectors for all eight categories, availability bits and maximum-size
  inputs.
- `CG-REQ-F1-003`: Compare digests only when checkpoint ID, context and schema
  are comparable. Missing/unsupported categories remain `unknown` and cannot
  weaken the existing compatibility gate. Wire uses a session-salted 128-bit
  tag for one checkpoint; only the host compares tags and returns a category
  mismatch bitmask.

### F2 — first-divergence locator

- `CG-REQ-F2-001`: Given comparable F1 observations, report the last observed
  common checkpoint, the first observed divergent checkpoint and the lowest
  differing allowlisted category.
- `CG-REQ-F2-002`: Preserve attribution limits: `first observed` is not
  `first caused`; never infer a causal Mod, card or player from category/time
  proximity alone.
- `CG-REQ-F2-003`: Bound retained checkpoints, output and peer data. When F1
  evidence is absent, retain the native divergence result and state that
  localization is unavailable.

### F3 — action/checkpoint log

- `CG-REQ-F3-001`: Publish monotonic, allowlisted already-public action and
  natural-checkpoint events into D3's single journal; never serialize action
  arguments or arbitrary `ToString()` output. Peer/wait/network events belong
  to D3, not F3.
- `CG-REQ-F3-002`: D3 owns the only fixed-capacity buffer and bounded copied
  report; event floods evict old entries rather than grow memory. H5 only
  renders a filtered recent view.
- `CG-REQ-F3-003`: Observation must not enqueue/resume/cancel actions or cause
  additional checksum generation.

### F4 — RNG-count sentinel

- `CG-REQ-F4-001`: An audited postfix may increment a CoopGuard-owned counter
  when STS2 actually consumes from an RNG stream. Record only bounded stream
  identity and already-observed count at natural checkpoints; never record RNG
  state or values.
- `CG-REQ-F4-002`: Detect the first unequal count for comparable peers and
  distinguish unequal, missing and unsupported observations.
- `CG-REQ-F4-003`: An unavailable counter API degrades diagnostics without
  touching the stream or claiming equality.

### F5 — bounded peer heartbeat

- `CG-REQ-F5-001`: One shared per-peer scheduler combines liveness with changed
  state, bounds payload/send rate/retained state/peer count, and applies a
  documented priority/backpressure policy.
- `CG-REQ-F5-002`: Unknown, malformed, replayed or excessive peer messages are
  rejected/rate-limited without unbounded allocation.
- `CG-REQ-F5-003`: Heartbeat status supplements STS2's native
  `ConnectionStats`; it never overrides native loading, timeout or connection
  decisions.

### F6 — Mod-author diagnostics/determinism API

- `CG-REQ-F6-001`: Expose a versioned, push-only API for bounded settings
  digests, cached state digests and fixed public diagnostic event codes. The API
  never stores a callback/delegate or synchronously invokes third-party code.
- `CG-REQ-F6-002`: Validate manifest identity, schema, thread safety, size and
  rate for every call; invalid or excessive publishers receive a bounded
  failure result without an exception crossing the Mod boundary. A changed
  settings digest after the Guard fingerprint freezes sets sticky
  restart-required rather than replacing the exposed digest.
- `CG-REQ-F6-003`: Missing/incompatible optional checkpoint diagnostics degrade
  only that category. A package-declared G10 settings provider that fails to
  publish a valid digest remains fail-closed at the Guard gate.
- `CG-REQ-F6-004`: At a natural checkpoint callback entry, snapshot the latest
  cached state revision without waiting; a later publish belongs only to the
  next natural checkpoint.

### F7 — automated 2/3/4-client CI

- `CG-REQ-F7-001`: Launch two-, three- and four-peer isolated ENet topologies
  with unique profiles, ports, client IDs, temporary directories and logs.
- `CG-REQ-F7-002`: Require positive phase sentinels and attribute a failed
  peer/scenario; timeout, crash or missing evidence is not a pass.
- `CG-REQ-F7-003`: Preserve failure artifacts, redact published artifacts and
  clean up only exact test-owned processes/directories.

### F8 — developer-only fault injection

- `CG-REQ-F8-001`: Enable only a catalogued fault ID through the isolated test
  driver or a non-shipping Debug fixture.
- `CG-REQ-F8-002`: Refuse unknown faults, Release builds and live-profile
  targets.
- `CG-REQ-F8-003`: A missing injection hook after a game update makes the test
  `inconclusive`; it never weakens production checks or falls back to state
  repair/additional checksums.

### Cross-cutting requirements

- `CG-REQ-CORE-001`: Preserve all safety invariants in section 2.
- `CG-REQ-CORE-002`: Unknown game builds and Guard protocols fail closed;
  unknown Diagnostics major versions disable the optional diagnostics plane.
- `CG-REQ-CORE-003`: Raw diagnostics and reports remain local unless the user
  explicitly copies them. Only documented bounded observations may cross the
  optional plane, and C9 separately requires unanimous per-session consent.
- `CG-REQ-CORE-004`: All Harmony targets and observed API contracts match an
  explicitly audited STS2 build tuple.
- `CG-REQ-CORE-005`: Chinese and English text is actionable, bounded and
  accessible without color alone.
- `CG-REQ-CORE-006`: Experimental Forensics is default-off and sends only after
  unanimous current-session/membership-epoch activation; member change or
  revoke synchronously disables it and clears peer cache.
- `CG-REQ-CORE-007`: Guard and each optional Harmony module install under
  independent owners/transactions; optional target drift cannot alter Guard
  health.

## 4. Stable identifiers and traceability

### 4.1 Identifier grammar

- Requirement: `CG-REQ-<F1..F8|CORE>-NNN`
- Test: `CG-TST-<F1..F8|CORE>-<LAYER>-NNN`
- Fault: `CG-FLT-<DOMAIN>-NNN`

Allowed layer tokens:

| Token | Layer |
|---|---|
| `UT` | pure unit/self-check |
| `CMP` | component/Godot/Harmony |
| `CTR` | binary, API or wire contract |
| `MP2` | two-peer isolated ENet |
| `MP3` | three-peer isolated ENet |
| `MP4` | four-peer isolated ENet |
| `E2E` | complete user-visible flow |
| `SOAK` | longevity/load |
| `UPG` | upgrade/backward compatibility |
| `SEC` | security/privacy |
| `PERF` | performance/resource bounds |
| `A11Y` | accessibility/localization |

IDs are never renumbered or reused. Deleted tests remain in the registry as
`retired` with a replacement ID if applicable. Scenario names, paths and
implementation details may change without changing the ID.

### 4.2 Required metadata

Each automated result must emit:

- test ID and requirement IDs;
- CoopGuard assembly/manifest/protocol version;
- STS2 version, commit, main-assembly hash and bundled .NET version;
- topology, peer role/client ID and deterministic seed;
- enabled fault IDs and their target peer;
- start/end monotonic timestamps and timeout;
- positive phase sentinels observed;
- `pass`, `fail`, `inconclusive` or `skipped`, with reason;
- paths to redacted logs, reports and screenshots.

### 4.3 Coverage rules

1. Every feature maps `normal`, `boundary`, `error`, `degraded` and `recovery`
   path tags to a stable test ID or an approved `not_applicable` reason. One
   test may cover several tags only when each has a separate oracle/assertion.
2. Every requirement maps to at least one automated test or an explicitly
   justified manual external test.
3. Any new wire field requires round-trip, size-bound, malformed-input,
   mixed-version and redaction tests.
4. Any new Harmony target requires a reflection contract test, PatchAll smoke
   and missing-target fail-safe test.
5. Any peer-controlled or Mod-author-controlled field requires boundary,
   Unicode/control-character and event-flood tests.
6. Any visible state requires Chinese/English, keyboard/controller, scaling and
   color-independent checks.
7. Any timing rule uses a controllable monotonic clock in pure tests and must
   cover zero, threshold-minus-one, threshold, threshold-plus-one and clock
   discontinuity.
8. A test may satisfy several requirements, but its primary feature and stable
   test ID do not change.

The test implementation must keep a machine-readable trace table with
`test_id`, `requirements`, `path_tags`, `layer`, `owner`, `automated`,
`status`, `not_applicable_reason` and `last_evidence`. CI fails if a
non-retired requirement/path tag has no test or approved reason, or a test
references an unknown requirement/fault ID.

## 5. F1–F8 minimum acceptance matrix

These tests are mandatory before a feature can be called complete.

| Test ID | Path | Scenario and oracle | Requirements |
|---|---|---|---|
| `CG-TST-F1-UT-001` | Normal | Run hard-coded expected byte/digest vectors (not values generated by the implementation under test) for all eight `F1SchemaV1` categories, every mask, empty/max collections, unknown enums, duplicate keys, different order and cultures. | `CG-REQ-F1-001`, `CG-REQ-F1-002` |
| `CG-TST-F1-CMP-002` | Abnormal | One category contains an over-limit/unsupported public value while access spies protect card identities, hand/draw order, choice candidates, seeds and RNG state. The category is rejected or unknown, every forbidden accessor has zero reads, no raw/full digest reaches output, and Guard is unchanged. | `CG-REQ-F1-002`, `CG-REQ-F1-003`, `CG-REQ-CORE-001` |
| `CG-TST-F1-UPG-003` | Degraded | Remove one audited state projection from the contract fixture. Only its category becomes `unsupported`; no extra checksum is generated and all unrelated categories/gameplay continue. | `CG-REQ-F1-001`, `CG-REQ-F1-003`, `CG-REQ-CORE-004` |
| `CG-TST-F1-MP4-004` | Multiplayer | Four peers at one natural checkpoint first produce equal session-salted tags, then one fixture changes an allowlisted public field. Only the host receives tags; all peers receive the correct category bitmask, packet capture contains no full/local digest, and session rollover changes every tag. | `CG-REQ-F1-001`, `CG-REQ-F1-002`, `CG-REQ-F1-003` |
| `CG-TST-F1-CTR-005` | Privacy contract | Replace card identity/order, choice candidate, seed, RNG-state and hidden-intent members with access spies that fail on read. All read counts remain zero; wire capture contains only one-checkpoint session-salted tags to host and returned mismatch bitmasks. | `CG-REQ-F1-002`, `CG-REQ-F1-003`, `CG-REQ-CORE-003`, `CG-REQ-CORE-004` |
| `CG-TST-F2-UT-001` | Normal | Compare bounded F1 histories with checkpoints 40–43, where 40–41 match and Combat first differs at 42. Output is `last common=41`, `first observed divergent=42`, `category=Combat` across cultures/orderings. | `CG-REQ-F2-001`, `CG-REQ-F2-002` |
| `CG-TST-F2-SEC-002` | Abnormal | Supply mismatched schemas, replayed checkpoints, malformed category labels and histories above capacity. Inputs are rejected/bounded, and no Mod/card/player is blamed from temporal proximity. | `CG-REQ-F2-002`, `CG-REQ-F2-003`, `CG-REQ-CORE-003` |
| `CG-TST-F2-E2E-003` | Degraded | Receive native `StateDivergence` without comparable F1 digests. Popup confirms divergence and explicitly says hierarchical localization is unavailable; it never requests state or an extra checkpoint. | `CG-REQ-F2-003` |
| `CG-TST-F2-MP3-004` | Multiplayer | Three peers share checkpoints 40–43; one peer first differs in `CombatPublic` at 42. Every supported UI reports last-common 41/first-observed 42 without causal attribution; a late join creates a new session history instead of merging old checkpoints. | `CG-REQ-F2-001`, `CG-REQ-F2-002`, `CG-REQ-F2-003` |
| `CG-TST-F3-CMP-001` | Normal | Observe an already-public native action begin/completion and naturally generated checkpoint. Sequence/time ordering is stable; the journal contains only allowlisted category/actor/checkpoint fields. | `CG-REQ-F3-001`, `CG-REQ-F3-003` |
| `CG-TST-F3-SEC-002` | Abnormal | Inject duplicates, late observations, oversized labels, an action whose `ToString()` contains a path/secret and more events than capacity. Raw arguments are never evaluated/copied, oldest entries are evicted and memory/output remain bounded. | `CG-REQ-F3-001`, `CG-REQ-F3-002` |
| `CG-TST-F3-E2E-003` | Degraded | Make clipboard/modal/report rendering unavailable. Timeline collection remains bounded, gameplay continues, and the failure is logged without altering the compatibility gate. | `CG-REQ-F3-002`, `CG-REQ-F3-003` |
| `CG-TST-F4-CTR-001` | Normal | Run audited RNG calls through the natural game path on matching peers. Postfix counters match at a natural checkpoint; spies prove CoopGuard never invokes an RNG method or extra-checksum entry point and records no value/state. | `CG-REQ-F4-001`, `CG-REQ-F4-002`, `CG-REQ-CORE-001` |
| `CG-TST-F4-UT-002` | Abnormal | Feed unequal already-observed counts for one allowlisted stream at the same checkpoint. Identify the first count mismatch without serializing RNG state/value, predicting the next value or mapping it to a causal Mod. | `CG-REQ-F4-001`, `CG-REQ-F4-002` |
| `CG-TST-F4-UPG-003` | Degraded | An audited RNG method signature changes or the patch cannot install. Mark the entire RNG-count category unsupported, uninstall any partial hooks and preserve all other diagnostics/gates. | `CG-REQ-F4-003`, `CG-REQ-CORE-004` |
| `CG-TST-F4-MP2-004` | Multiplayer | Matching peers execute the same audited natural RNG calls and compare count tags at the next natural checkpoint; a fixture alters only its reported test counter in a second run. Equality/mismatch is correct and invocation spies prove zero extra RNG/checksum calls. | `CG-REQ-F4-001`, `CG-REQ-F4-002`, `CG-REQ-F4-003` |
| `CG-TST-F5-MP4-001` | Normal | Four peers use one scheduler while idle, loading, sending C1 status, C9 snapshots and F1 tags. Liveness/state converge; envelope rate/payload stay within budgets and H3 becomes stale only after two missed 2-second keepalives plus 1-second grace. | `CG-REQ-F5-001`, `CG-REQ-F5-003` |
| `CG-TST-F5-SEC-002` | Abnormal | Send unknown-peer, malformed, replayed and rate-excess heartbeat inputs. They are ignored/rate-limited without exception, identity confusion or unbounded allocation. | `CG-REQ-F5-001`, `CG-REQ-F5-002` |
| `CG-TST-F5-MP2-003` | Degraded | Delay/drop toolkit heartbeats while STS2 native traffic still progresses and while the remote reports loading. Toolkit status degrades but never disconnects, pauses or overrides native status. | `CG-REQ-F5-003`, `CG-REQ-CORE-001` |
| `CG-TST-F6-CMP-001` | Normal | A fixture Mod with a package-hashed settings declaration pushes one valid settings digest through the Guard API, then a cached state digest and fixed public event through the optional API. CoopGuard stores only bounded copies, routes them to independent owners and never calls back into the fixture. | `CG-REQ-F6-001` |
| `CG-TST-F6-SEC-002` | Abnormal | Fixture uses the wrong manifest identity/schema, invalid event code, oversized/high-rate/concurrent calls and malformed digests, then changes a valid settings digest after freeze. Calls return bounded failures, no exception crosses the Mod boundary, no delegate is retained, memory stays bounded and the late change sets restart-required without replacing the frozen entry. | `CG-REQ-F6-002`, `CG-REQ-CORE-003` |
| `CG-TST-F6-UPG-003` | Degraded | An optional checkpoint publisher requests an unsupported API version and degrades locally; separately, a Mod with a declared G10 provider omits its required settings digest and the existing Guard gate fails closed. | `CG-REQ-F6-003`, `CG-REQ-CORE-002` |
| `CG-TST-F6-CMP-004` | Isolation | Force the Optional Diagnostics API/module to throw and trip its circuit breaker after a valid settings digest is frozen. The optional category disables, while the frozen Guard entry and ready/start decision remain unchanged. | `CG-REQ-F6-001`, `CG-REQ-F6-003`, `CG-REQ-CORE-001` |
| `CG-TST-F6-CMP-005` | Isolation | Break the declared Guard Settings API before freeze while leaving Optional Diagnostics healthy. Guard fails closed with the provider ID, and no optional UI/API result can mark compatibility healthy or bypass the gate. | `CG-REQ-F6-001`, `CG-REQ-F6-003`, `CG-REQ-CORE-001` |
| `CG-TST-F6-CMP-006` | Cutoff | Publish revision 7 before the natural checkpoint callback and revision 8 after callback entry. F1 freezes only revision 7 for that checkpoint, uses revision 8 at the next natural checkpoint, never waits and never changes subscriber order. | `CG-REQ-F6-001`, `CG-REQ-F6-004` |
| `CG-TST-F7-MP4-001` | Normal | A matching four-peer fresh-lobby job gives every peer unique isolated paths/IDs; all four join, ready and embark with explicit per-peer sentinels. | `CG-REQ-F7-001`, `CG-REQ-F7-002` |
| `CG-TST-F7-MP3-002` | Abnormal | In three peers, give one peer a package mismatch then separately crash one peer. CI identifies the exact peer and expected phase; neither case can pass from host-only evidence. | `CG-REQ-F7-002` |
| `CG-TST-F7-E2E-003` | Degraded | Break a harness prerequisite or exceed the scenario deadline. Result is `inconclusive`, original artifacts remain, and cleanup stops only launched process IDs. | `CG-REQ-F7-002`, `CG-REQ-F7-003` |
| `CG-TST-F7-MP2-004` | Normal | A matching two-peer job covers fresh join/start, loaded lobby and pre-run lobby re-entry with explicit host/client sentinels and isolated cleanup. | `CG-REQ-F7-001`, `CG-REQ-F7-002`, `CG-REQ-F7-003` |
| `CG-TST-F7-MP3-005` | Normal | Three peers join sequentially, ready simultaneously, embark and retain evidence from every process; no one-time broadcast is lost when peer 3 joins late. | `CG-REQ-F7-001`, `CG-REQ-F7-002` |
| `CG-TST-F8-E2E-001` | Normal | Explicitly enable one catalogued fault in an ignored isolated runtime. Output records the exact fault ID/target and the intended observer behavior occurs once. | `CG-REQ-F8-001` |
| `CG-TST-F8-SEC-002` | Abnormal | Try an unknown fault, Release package and live profile path. All are refused before launch/injection; the packaged DLL has no injection handler. | `CG-REQ-F8-002`, `CG-REQ-CORE-001` |
| `CG-TST-F8-UPG-003` | Degraded | Remove/rename the audited injection hook in a contract fixture. Test becomes `inconclusive` and does not use state repair or an extra checksum as fallback. | `CG-REQ-F8-003`, `CG-REQ-CORE-004` |

### 5.1 ADR 0003 and cross-feature hard gates

| Test ID | Scenario and oracle | Requirements |
|---|---|---|
| `CG-TST-CORE-CTR-001` | Prove the Diagnostics envelope limit is enforced before payload allocation; every internal count/length is validated over `ReadOnlySpan<byte>` before arrays/strings, including `uint32.MaxValue`, truncation and cumulative overflow. | `CG-REQ-CORE-001`, `CG-REQ-CORE-004` |
| `CG-TST-CORE-SEC-002` | Parse 1,000,000 generated outer/inner payloads. There are zero crashes/leaks; per-parse additional allocation is ≤16 KiB, per-peer cache ≤64 KiB and aggregate multi-peer memory stays fixed. | `CG-REQ-CORE-001`, `CG-REQ-CORE-003` |
| `CG-TST-CORE-MP2-003` | Force the optional plane off, corrupt it and trip its circuit breaker in matching two-peer runs. Native join/ready/start and Guard decisions remain byte-for-byte equivalent to the disabled baseline. | `CG-REQ-CORE-001`, `CG-REQ-CORE-002` |
| `CG-TST-CORE-MP3-004` | Sequential third-peer join, leave, lobby re-entry and session rollover exercise bounded Hello/ACK retries; old-session messages are rejected and no one-time broadcast is required. | `CG-REQ-CORE-001`, `CG-REQ-CORE-004` |
| `CG-TST-CORE-MP4-005` | Four peers exercise idle/load/play, two degraded modules, backpressure and cleanup; one peer cannot make another peer's row healthy or allocate outside its quota. | `CG-REQ-CORE-001`, `CG-REQ-CORE-003` |
| `CG-TST-CORE-UPG-006` | In the same Release parser fixture, inject an unknown Diagnostics major; only the optional plane disables. A genuinely different CoopGuard package is separately rejected by native Guard before lobby. | `CG-REQ-CORE-002`, `CG-REQ-CORE-004` |
| `CG-TST-CORE-CTR-007` | Audit native `InitialGameInfoMessage` Mod-list count, single-string length and aggregate UTF-8 byte limits with actual allocation measurements before enabling G9/G10 vector expansion. Failure keeps protocol 4 behavior. | `CG-REQ-CORE-001`, `CG-REQ-CORE-004` |
| `CG-TST-CORE-MP4-008` | Exhaustively permute C9 OptIn/Commit/Active, member join/rejoin/revoke and old hand updates. No invalid epoch sends/decodes/displays data; local revoke is synchronous. | `CG-REQ-CORE-001`, `CG-REQ-CORE-003` |
| `CG-TST-CORE-MP4-009` | Forensics starts only after unanimous current-epoch Active; member change/revoke clears tags. A state digest published after checkpoint callback entry is used only at the next natural checkpoint without waiting. | `CG-REQ-F1-003`, `CG-REQ-F6-004`, `CG-REQ-CORE-001`, `CG-REQ-CORE-006` |
| `CG-TST-CORE-CMP-010` | Remove one optional Harmony target and throw during another module's install. Only those owners roll back/disable; `coopguard.guard` targets, health and native gate output are unchanged. | `CG-REQ-CORE-001`, `CG-REQ-CORE-004`, `CG-REQ-CORE-007` |
| `CG-TST-CORE-A11Y-011` | Render every new surface in Simplified/Traditional Chinese and English fallback at 100/150/200% scale; complete keyboard/controller traversal, contrast and color-independent/sound-alternative checks with bounded hostile names. | `CG-REQ-CORE-003`, `CG-REQ-CORE-005` |
| `CG-TST-CORE-MP3-012` | After comparable F1/F2/F4 checkpoints, disconnect one peer, attempt the current unsupported running rejoin, then form a new lobby/session with the same peers. Disconnect synchronously clears Forensics history; old epoch tags/counters are rejected before cache, and the new session never merges old last-common/divergent checkpoints. | `CG-REQ-F1-003`, `CG-REQ-F2-003`, `CG-REQ-F4-003`, `CG-REQ-CORE-006` |
| `CG-TST-CORE-SEC-013` | Enumerate every Diagnostics schema/handler and inspect optional-module call edges: only documented structured fields/IDs exist, injected free text/control/URL/path fields are rejected, and no handler can reach action/run/player/RNG/save mutation APIs. Reuse the F8 Release-package security gate to prove no fault handler/switch is present. | `CG-REQ-CORE-001`, `CG-REQ-CORE-003`, `CG-REQ-F8-002` |

## 6. Test layers

### 6.1 Unit/self-check

Pure tests run without STS2, Godot, network or filesystem state except bounded
temporary inputs. They cover:

- hierarchical digest canonicalization, category schemas and checkpoint
  comparability;
- last-common/first-observed-divergent checkpoint reduction and history limits;
- timeline ordering, deduplication and ring-buffer eviction;
- observed RNG-count comparison with an API that exposes no RNG value;
- heartbeat encode/decode, replay window, rate limit and expiry;
- F6 push-only validation, version negotiation and per-publisher quotas;
- redaction and bilingual text;
- trace-table and fault-catalog schema validation.

Keep one runnable self-check per cohesive pure-logic area. Do not introduce a
test framework merely for parameterization.

### 6.2 Component

Component tests load the Release DLL against copied STS2/Harmony/Godot
assemblies and verify:

- all expected patches install and unpatch;
- Guard and every optional module use distinct Harmony owners/install
  transactions; an optional missing target rolls back only that owner and a
  spy proves core `PatchAll` never scans optional patches;
- observers subscribe/unsubscribe exactly once across scene/run lifecycles;
- a post-sentinel diagnostic patch failure leaves the compatibility sentinel
  fail-closed;
- real native modal, label, button and clipboard behavior under normal
  rendering;
- UI/report failure cannot block or mutate the run;
- fixture Mod pushes bounded data through the public F6 assembly boundary, and
  an invocation spy proves no callback/delegate is retained or invoked;
- counters/loggers dispose cleanly and retain no dead scene/player objects.

### 6.3 Contract

For every supported STS2 build tuple, reflect and pin only the APIs actually
used:

- `INetGameService`, peer identity and `ConnectionStats`;
- native lobby/join transitions, pre-run lobby re-entry, and the current
  `RunInProgress` rejection for running sessions;
- action-queue observable events and natural checksum events;
- the optional `PlayerChoiceSynchronizer.WaitForRemoteChoice(...)`
  prefix/finalizer signature, with an invocation spy proving CoopGuard never
  calls it;
- native divergence message and the read-only projection used by F1;
- every F1 forbidden member (card identity/order, choice candidates, seed,
  RNG state and hidden intent) is replaced by an access spy and covered by
  `CG-TST-F1-CTR-005`; a single forbidden read fails the contract;
- RNG consumption methods observed by F4 and their patch signatures;
- UI node/method signatures;
- serialization registration and maximum encoded heartbeat/API field sizes.

Contract tests must prove that observers do not call action mutation,
RNG-generation or checksum-generation methods. An unknown tuple fails closed
for safety-critical compatibility and reports optional diagnostic layers as
unsupported.

### 6.4 Isolated multiplayer

All peers use copied game directories and profiles with:

- Steam forced off;
- unique `APPDATA`, `LOCALAPPDATA`, temp, log and Mod roots;
- deterministic client IDs, ports and seeds;
- accepted Mod settings copied into each isolated profile;
- a per-process start/exit sentinel and exact process handle.

Minimum topology matrix:

| Topology | Mandatory scenarios |
|---|---|
| 2 peers | fresh join/start; loaded lobby/start; pre-run lobby re-entry; v0.109.1 running `RunInProgress`/Unsupported; package mismatch; native divergence; heartbeat loss; client exit |
| 3 peers | sequential joins; simultaneous ready; one differing package; one slow/loading peer; one pre-run disconnect/re-entry; attribution from affected peer logs |
| 4 peers | staggered join/load; all ready/start; bounded heartbeat/event load; two degraded peers; one crash; long-run resource bounds |

Clients connect to the host; a client must not claim direct quality information
for another client unless the host explicitly supplied bounded peer status.

### 6.5 End to end

User-visible E2E checks include:

1. healthy lobby summary through embark;
2. exact package mismatch and actionable peer/Mod direction;
3. native divergence with and without comparable hierarchical digests;
4. wait-state transition from player choice/loading to progress;
5. degraded toolkit heartbeat while native connection remains healthy;
6. pre-run disconnect/native lobby re-entry, plus an in-run disconnect that
   displays `RunInProgress`/Unsupported without a recovery button;
7. manual snapshot and copy report;
8. English and Chinese UI under keyboard/controller input.

A state-divergence fixture may create a one-sided difference only in an
isolated test Mod through a normal game-action path. The test then waits for
STS2's next natural checkpoint. CoopGuard and the harness must not mutate the
state, repair it or request an extra checksum.

### 6.6 Soak

- Two-peer active soak: at least 2 hours across combats, choices, rewards, map
  transitions, one pre-run lobby re-entry and one in-run unsupported recovery
  diagnosis.
- Four-peer idle/loading soak: at least 8 hours with staggered loading and
  bounded heartbeat/status updates.
- Pure event-pressure soak: at least 100,000 synthetic observations into the
  bounded F3/F6 pipeline.
- Network-degradation soak: at least 30 minutes of alternating latency/loss
  profiles without toolkit-driven disconnects.

Pass requires stable buffer counts, no unbounded task/subscription growth, no
new unhandled exception, and section 10 resource budgets after warm-up.

### 6.7 Upgrade compatibility

Test:

- current/current versions;
- current/previous Guard 发布包（必须由原生包校验拒绝）；
- 同一发布包内 Diagnostics 本地关闭/API 不可用，以及注入的未知 major
  parser 防御；不把它描述成 mixed Release lobby；
- current/missing CoopGuard（必须由原生包校验拒绝）；
- equal protocol with different package bytes;
- supported and unknown STS2 build tuples;
- current/previous F6 Mod-author API versions;
- old copied diagnostic reports opened by the current parser, if report import
  is supported.

Mixed Guard safety protocols must reject before the run starts with a specific
diagnosis. In a same-Release parser fixture, an injected unknown Diagnostics
major disables only the optional plane; this is not a mixed-Release promise.
Optional display/API incompatibility may degrade only when it does not weaken
any Guard compatibility assertion.

### 6.8 Security and privacy

Fuzz/bound:

- Mod IDs, peer labels, diagnostic markers and component names;
- empty, malformed and maximum-length heartbeat messages;
- Unicode normalization collisions, bidi/control characters and invalid UTF-8;
- unknown/replayed peer identity;
- absolute/UNC paths, Steam IDs, IPv4/IPv6, bearer/basic credentials and
  SHA-256-like material in every copied report field;
- rate, peer-count, event-count, nesting and output-size limits;
- wrong-identity/schema and oversized/high-rate/concurrent F6 publishers.

Capture actual packets in isolated tests and assert that payloads contain only
documented bounded fields. Clipboard remains user-initiated. No test artifact
with raw identifiers or paths may be uploaded by CI.

Release-package inspection must confirm that F8 command-line switches, fault
handlers and fault payloads are absent from `CoopGuard.dll`.

### 6.9 Accessibility

For every new visible surface:

- Simplified/Traditional Chinese use Chinese; other locales have a complete
  English fallback;
- status is conveyed by text/icon as well as color;
- 100%, 150% and 200% UI scaling do not clip the error code, peer name,
  primary status or next action;
- long Unicode peer/Mod names are bounded with the full redacted value
  available in the copied report;
- keyboard and controller can focus, open, copy and close without traps;
- rapid state changes do not flash faster than three times per second;
- HUD can be hidden and does not cover ready/end-turn, cards, rewards or native
  timeout UI;
- loading/unknown/degraded wording does not imply a player is at fault.

## 7. Fault-injection catalog

F8 should reuse a separate test-driver Mod/process where possible. Production
CoopGuard should not own fault branches.

| Fault ID | Injection | Allowed scope | Expected observation |
|---|---|---|---|
| `CG-FLT-NET-001` | Drop toolkit heartbeats for one peer | isolated driver only | F5 degrades; native connection remains authoritative |
| `CG-FLT-NET-002` | Delay toolkit heartbeats across expiry thresholds | isolated driver only | monotonic age/status transitions occur once per threshold |
| `CG-FLT-NET-003` | Replay/duplicate/malformed heartbeat | serializer/isolated driver | input rejected or ignored within bounds |
| `CG-FLT-PEER-001` | Gracefully exit one launched client | isolated process | exact peer/phase logged; remaining peers stay bounded |
| `CG-FLT-PEER-002` | Terminate one launched client process | isolated process | crash/disconnect distinguished from Mod divergence |
| `CG-FLT-OBS-001` | Feed two synthetic allowlisted category histories with a known first differing checkpoint | pure/component | F2 returns last-common, first-observed-divergent and lowest differing category |
| `CG-FLT-OBS-002` | Remove comparable hierarchical digests | component/E2E fixture | confirmed native divergence, localization unavailable |
| `CG-FLT-RNG-001` | Alter the reported test observation count, not RNG | pure/component | F4 count mismatch, zero RNG calls |
| `CG-FLT-LOG-001` | Flood bounded observation/API events | pure/component | eviction/rate limit; fixed memory/output |
| `CG-FLT-MOD-001` | Change one copied package file | isolated copied Mod | native Mod mismatch before lobby/run |
| `CG-FLT-MOD-002` | Fixture Mod creates a one-sided state difference through a normal action | isolated test Mod | native natural checkpoint reports divergence |
| `CG-FLT-API-001` | Wrong-identity/schema, oversized, high-rate and concurrent F6 pushes | fixture Mod | publisher rejected/limited and named safely; no callback exists |
| `CG-FLT-UI-001` | Modal/clipboard unavailable | component | diagnostics fail open; guard remains fail-closed |
| `CG-FLT-BUILD-001` | Unsupported reflected build tuple | contract fake layout | production compatibility fails closed |
| `CG-FLT-HARNESS-001` | Missing port/profile/process sentinel | CI harness | `inconclusive`, artifacts retained, exact cleanup |

Faults must be deterministic and single-purpose. A test result records the
fault ID, target peer, activation time and proof that it activated exactly
once. Network delay/loss should be applied at the toolkit message/test-proxy
boundary, not by holding game actions or modifying STS2 timers.

## 8. Multiplayer and E2E oracles

Use positive sentinels, not log silence:

- process initialized the expected CoopGuard assembly/manifest;
- full local fingerprint completed;
- expected peer IDs joined and entered the intended lobby/run state;
- every ready gate completed or was blocked for the expected reason;
- all healthy peers embarked with the same seed;
- expected natural action/checkpoint IDs progressed;
- expected F1–F6 report code and peer/component appeared;
- raw hashes, wire entries and private data did not appear;
- expected disconnect/lobby-re-entry/`RunInProgress` native-error reason appeared;
- every launched process exited or was stopped by exact process handle.

Host-only evidence cannot prove a client-side UI/action stall. A scenario that
attributes a client must retain that client's timestamped log and phase
sentinel.

## 9. CI execution policy

Recommended gates:

### Pull request

1. format/diff check;
2. Release build with warnings as errors;
3. existing fingerprint/incident self-check;
4. F1–F6 pure self-checks;
5. contract and PatchAll smoke;
6. two-peer happy path plus one rotating negative scenario.

### Nightly

1. full 2/3/4-peer matrix;
2. all catalogued non-Steam faults;
3. rendered UI and accessibility checks;
4. packet/privacy capture;
5. short performance and 2-hour soak.

### Release candidate

1. clean immutable two-file staging package;
2. complete unit/component/contract/multiplayer/E2E matrix;
3. eight-hour four-peer soak;
4. upgrade matrix against the currently published version;
5. static proof that F8 is absent from Release;
6. one real Steam two-account/two-machine session, recorded as manual external
   evidence until it can be safely automated.

One automatic rerun is allowed only for classified infrastructure
`inconclusive` results. The original attempt remains attached. Product
failures, assertion failures, crashes, privacy failures and timeouts with a
healthy harness are never erased by a retry.

## 10. Performance and resource budgets

Initial release budgets for the toolkit additions:

| Resource | Budget |
|---|---|
| F1/HUD refresh | at most 4 Hz; p95 main-thread observer/update work ≤0.25 ms/frame and p99 ≤0.75 ms/frame on the reference machine |
| F1/F2 history | at most 64 natural checkpoints/peer and 8 fixed categories/checkpoint; report at most 16 KiB and no raw/unbounded state copy |
| D3/F3 journal | one shared ring, at most 1024 retained entries and 2 MiB total retained memory including object/index overhead; copied toolkit section at most 64 KiB |
| F5/scheduler | one shared scheduler; steady ≤1 envelope/peer/second, burst 4, idle keepalive every 2 s; liveness header ≤256 B and total payload ≤4096 B |
| F6 publisher | per-field and per-publisher quotas; aggregate event rate cannot exceed the F3 buffer/input limiter; no stored callback/delegate |
| Steady memory | optional modules ≤32 MiB total; after warm-up an 8-hour soak has no monotonic retained-object growth |
| Four-peer network | average ≤1 KiB/s/peer and hard burst ≤4 KiB/s/peer at default settings |
| Frame impact | no toolkit-caused frame hitch above 5 ms; p99 added frame work ≤0.75 ms |
| Connect/begin callbacks | no full disk hashing, blocking wait or unbounded serialization |

Measure a matching build with toolkit displays disabled and enabled using the
same seed/topology. Record CPU/GPU/RAM/OS and exact builds; warm up 60 seconds,
capture at least 10 minutes and 10,000 frames, repeat three times, and publish
each run plus the median. The Release SLO corpus must contain at least 2,000
eligible client-minutes as defined by the technical design. A budget change
requires a documented reason and new baseline; it must not be hidden by
increasing a timeout or excluding an erroneous downgrade from the denominator.

## 11. Exit criteria

A feature is releasable only when:

- its requirements are approved and mapped to stable tests;
- normal, abnormal and degraded tests in section 5 pass;
- all applicable unit/component/contract/topology/security/performance/A11Y
  tests pass;
- no safety invariant or privacy test fails;
- unknown APIs/protocols take the documented fail-closed/degraded path;
- the exact Release package passes the final smoke and contains no F8
  injection surface;
- `docs/WORKLOG.md` records commands, build tuple, results, known gaps and
  immutable artifact hashes.

Real Steam transport, unavailable in ordinary CI, remains an explicit manual
release gate rather than being inferred from local ENet success.
