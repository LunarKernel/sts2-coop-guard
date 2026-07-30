# Multiplayer Toolkit implementation status

Target: BetterCoop `v0.5.0`, STS2 `v0.109.1` (`c8c577f6`), .NET `9.0.7`.

Status meanings:

- **Implemented**: production path exists and has an automated or isolated
  runnable check.
- **Implemented (degraded)**: the safe subset exists and the unsupported part
  is shown explicitly instead of guessed.
- **Release gate pending**: implementation exists, but the named long-running
  or platform-specific qualification remains outstanding.

| ID | Capability | Status | Current evidence or boundary |
|---|---|---|---|
| H1 | Network HUD | Implemented | Native peer stats, local-view label, bounded 60-second samples. |
| H2 | Current wait reason | Implemented | Native phase/action/choice observations with confidence and age. |
| H3 | Lobby health matrix | Implemented | Native state plus negotiated Toolkit rows; unknown peers degrade visibly. |
| H4 | Join progress | Implemented | JoinFlow observers hide when unavailable. |
| H5 | Multiplayer timeline | Implemented | Bounded whitelist event recorder. |
| H6 | Choice progress | Implemented | Completion/waiter state only; no choice contents. |
| H7 | Public teammate state | Implemented | Only state already public through native multiplayer models. |
| H8 | 60-second network graph | Implemented | Bounded native samples with unknown-value handling. |
| H9 | Shared environment code | Implemented | Session nonce plus build/protocol/package inputs; successful value cached. |
| H10 | Load phase/history | Implemented | Phase/duration plus the five most recent local loads; no fake percentage. |
| H11 | Player identity colors | Implemented | Stable session ordinal plus textual/symbol identity. |
| H12 | Alert center | Implemented | Deduplicated severity/action alerts; nonfatal events stay nonmodal. |
| C1 | Fixed quick status | Implemented | Five localized enums, all-peer capability gate, bounded reliable fan-out. |
| C2 | Map progress | Implemented | Submitted/pending state only; no route or node disclosure. |
| C3 | Public action feed | Implemented | Completed public action types only. |
| C4 | Wait timer | Implemented | Monotonic clock reset on state transition. |
| C5 | Reconnect card | Implemented | Reason, heartbeat age and native eligibility. |
| C6 | One-click reconnect | Implemented (degraded) | Audited pre-run re-entry only; running recovery is `Unsupported`. |
| C7 | Sound cues | Implemented | Native FMOD, global/per-type limits, mute and visual equivalents. |
| C8 | Accessibility | Implemented (degraded) | Keyboard focus, 200% scale, contrast, reduced motion and non-color text; screen-reader narration is not guaranteed. |
| C9 | Exact hand sharing | Implemented | Default off, unanimous session consent, bounded snapshots, roster-change revoke. |
| C10 | Contribution counters | Implemented | Local factual counters; separate opt-in sharing, monotonic merge, no score. |
| G1 | Mismatch repair table | Implemented | Per-Mod direction/confidence/read-only repair grouping. |
| G2 | Copy repair checklist | Implemented | Redacted output without digests or absolute paths. |
| G3 | Local Mod Doctor | Implemented | Read-only duplicate/source/dependency/version/cycle analysis. |
| G4 | Workshop update observer | Implemented | Read-only local package freshness; never triggers an update. |
| G5 | Environment lockfile | Implemented | Deterministic schema 1, hostile-input bounds, explicit export/compare. |
| G6 | Save environment sidecar | Implemented | Atomic independent sidecar after native save success. |
| G7 | Old-save warning | Implemented | Exact/different/unprovable outcomes; never edits the STS2 save. |
| G8 | Harmony conflict graph | Implemented | Read-only owner/target/type/order report; never unpatches. |
| G9 | Category fingerprints | Implemented (degraded) | Local category comparison active; wire expansion withheld pending native allocation audit. |
| G10 | Deterministic settings declaration | Implemented | Authenticated push-only digest, frozen into Guard and lockfile. |
| G11 | Capability negotiation | Implemented | Bounded Hello/ACK and optional diagnostics session; Guard 4 is independent. |
| G12 | Known-issue catalog | Implemented | Exact local build rules only; no remote executable rules. |
| D1 | Soft-lock observer | Implemented | Alert/evidence only; never cancels actions or ends turns. |
| D2 | Wait confidence | Implemented | Confidence, evidence and age; conflicting network evidence lowers confidence. |
| D3 | Flight recorder | Implemented | 1,024 events, 512-byte entries, 15-minute retention and drop count. |
| D4 | Pre-disconnect snapshot | Implemented | Last 60 seconds summarized without endpoints. |
| D5 | Report history | Implemented | Explicit save, bounded count/bytes, atomic files and clear/delete controls. |
| D6 | Offline report comparison | Implemented | Versioned bounded parser and text-only differences. |
| D7 | Shared session ID | Implemented | Random 128-bit ID with short display; not account-derived. |
| D8 | Next-start crash review | Implemented | Unclean marker plus local logs; explicitly includes power-loss/forced-exit uncertainty. |
| D9 | Dependency-aware A/B plan | Implemented | Read-only plan over selected input; cockpit default is all active Mods. |
| D10 | Host diagnostics | Implemented | Host-visible aggregate only; no kick, blame or automatic action. |
| F1 | Hierarchical state digests | Implemented (degraded) | Audited categories only; monster/public-effect categories are `Unsupported` on this build. |
| F2 | Earliest divergence locator | Implemented | Reports earliest observed checkpoint/category and last common point, not a culprit. |
| F3 | Action/checkpoint log | Implemented | Bounded public type/actor/checkpoint metadata without hidden parameters. |
| F4 | RNG call sentinel | Implemented | Counts audited consumers only; never reads value/seed or calls RNG. |
| F5 | Peer heartbeat | Implemented | Fixed fields, rate/size limits and expiry. |
| F6 | Mod diagnostics API | Implemented | Authenticated push-only state/event APIs, bounded and rate-limited; Guard API separate. |
| F7 | Automated client matrix | Release gate pending | Isolated ENet 2/3/4 matrix passes; Steam transport remains controlled/manual. |
| F8 | Developer fault injection | Implemented | Debug test-driver only, isolated-profile gate, one-shot recovery verified; production DLL has no trigger. |

Release qualification still pending: the eight-hour soak/2,000 eligible
client-minutes performance gate and controlled Steam-transport pass. These are
not represented as completed by shorter local smoke tests.
