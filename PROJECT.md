# TajsTokens - Project Design Document

## What is TajsTokens?

TajsTokens is a Codex companion app, that allows for quick overview on how they use Codex, where most of the quota usage went and predict future quota usage.

It's mostly a companion tool, with notifications, tray widget that shows current quota and quick prediction graphs. It's also an app that allows for exploring in more detail how Codex is used.

If TajsTokens could only do three things extremely well, it would be showing the current quota usage, showing the quota usage history and predicting future quota usage.

A person would prefer TajsTokens over Codex's dashboard because it shows more details, allows for quick overview of everything cleanly and has a prediction model.

It's mostly a product for myself, which is also shared on GitHub and might be used by others. But still, it's mostly my personal tool, and my workflow is serving as the first target profile.

## User Experience

Three most important things on launch are: current quota usage, whether the quota is gonna last till next reset and how much quota has been used in the last reset cycle.

Quota is the main visually dominant over token usage, although both are important. If we introduce TT (TajsToken) concept, it should also be as important.

The dashboard might answer the question "Am I okay?" with three markers:
You're good. You're in dangerous area. You're doomed.
The details can be explored elsewhere, but they have to be clearly accessible and fairly explainatory, without requiring a lot of reading and without exposing underlying models and concepts too much.

The "Did OAI nerf usage?" is fairly important and deserves a marker of it's own on the dashboard. It can be a yes, no or "looks weird, but we're not sure, check the details here".

For important numbers, the normal UI should show a small source/freshness label like “OpenAI reported · provisional” or “Locally observed · fresh”, while keeping technical source details hidden unless opened.

### Product Personality

The app should feel professional, dense but discoverable but also allow for "nerdy" exploration.

The important color, is as always purple. It could probably be used with something, but generally the design should follow my Taj's style.

Data density is going to be a problem. Hence we'll mostly try to go for cleaner UI with details on hover and/or click.

Technical terms might have a "why" or "what is this" tooltip.

The app should be as important or slightly less than the tray widget.

Uncertainty language should be somewhat between "still learning" and "insufficient  calibration epochs".

## Source of Truth

The app-owned TajsTokens database holds observations and their selected
interpretations. Native Codex files, databases and credentials remain read-only.
Authority belongs to a **quantity and scope**, rather than a global source ranking.
An authoritative allowance percentage does not establish a local token count,
and a correct local token ledger does not establish account-wide quota cost.

The following is the required product contract. Mentioning an endpoint or DTO does
not verify its availability or semantics in an installed client; EVID-002 owns that
acceptance. Existing implementation and retention gaps are stated below.

| Quantity | Basis for the displayed interpretation | Required scope and qualifications |
|---|---|---|
| Current allowance, reset and window availability | Compatible provider-authoritative `account/rateLimits/read` response; embedded rollout readings remain historical observations/fallback evidence | Provider/profile/account, named limit/lane, plan, window duration and reset. Absent, unsupported and stale windows stay distinct from zero. Named buckets are not additive. |
| Historical quota readings | Retained app-server and rollout observations, qualified by the shared quota-history policy | Preserve source/event/collection times and stream boundaries. Repeated embedded readings can be cached; a later JSONL record is not proof of a fresh measurement. Rounded readings do not establish exact burn. |
| Installation-local native token work | The selected native reducer ledger reconstructed from rollout counters and proven occurrence equivalence | Preserve reported counters, disjoint increments, counter epochs, lineage and integrity diagnostics. Local coverage does not imply account-wide completeness or current-login ownership. |
| Completed-response native usage | Source response, turn and thread counters with their native IDs and semantics | Response amounts, turn cumulative and thread cumulative snapshots overlap. Reconcile verified identities/coverage before selection; their simultaneous presence does not authorize adding them or discarding all legacy increments. Currently supplemental evidence. |
| Provider-estimated thread credits and dollars | The supported backend thread-usage report for its own named quantities | Preserve credit micros, estimated USD micros, nullable token groups, model/effort/speed and report scope independently. Native billed dollars, if later supported, need a separate field and contract. |
| Provider account activity and daily token/credit reports | The supported report snapshot and its native period, units and freshness | Lifetime, daily and grouped totals can overlap. Repeated fetches are revisions/snapshots, not new increments. Provider-wide coverage can differ from locally available rollouts. Native dates do not invent per-event UTC times. |
| Historical plan usage and task-attributed usage | Their separate supported provider contracts | Historical-period basis points retain period/plan, approximation and accounting-complete fields. Task percentages use their stated current-full-allowance basis; balance-usage credits remain a separate amount. Preserve requested-thread coverage and overlapping breakdown dimensions. |
| Purchased-credit balances and spend controls | The provider's named limit/credit metadata snapshot | Preserve reported balance text/unit, `hasCredits`, unlimited state and control limits/used amounts. A balance is not estimated thread consumption or a token/quota conversion. |
| API-equivalent valuation | A deterministic calculation under the stated price schedule and assumptions | Keep schedule/date/coverage separate from subscription usage and invoiced dollars. Current fixed prices remain the implementation until CAT-002 selects a versioned catalog. |
| TT | A frozen, explicitly selected reference-consumption basis | T-01 must select/version the public convention. Current TT is research-only. Token evidence remains usable when TT coordinates or conversion are unavailable. |
| Forecasts, quota conversion and inferred changes | Versioned derived calculations using compatible evidence and stated assumptions | Retain cutoff, horizon, training/selection support and uncertainty type. Results and selected interpretations can change without changing original observations. |

### Semantics

**Reported** = provider said it
**Observed** = TajsTokens/local source saw it
**Derived** = deterministic transformation
**Estimated** = model inference
**Inferred change** = statistical interpretation

### Conflict handling

Compare only compatible quantities, accounts, periods, units and definitions. For
example, a fresh provider allowance of 50% takes precedence for current allowance
over a cached embedded 49% reading; both observations remain evidence. Small
transient differences, such as 1pp of quota or 5pp of forecast error, do not justify
erasing contradictions or inventing precision.

A later provider report may become the best retrospective interpretation when it
actually accounts for the same quantity/period with stronger completeness or
finalization evidence. Retain its original and prior observations, revision lineage
and selection reason; expose the selected interpretation by default and revisions
on inspection. Reusing an observation ID with conflicting numeric content must
produce a conflict/variant, not an unnoticed last-writer-wins replacement.

**Content-free observations are preserved; product conclusions are revisable.**

### Provider Analytics

Provider analytics remain useful supplementary evidence. Delay can explain a
difference, but elapsed time alone does not make a report finalized or compatible.
Retain provider data-as-of/period, freshness and completeness flags where supplied.
Successful parsing, a field name or a public upstream declaration does not verify
the installed contract. Capability, account bracketing, pagination and scope limits
remain visible; unavailable or partial data must not become a plausible zero.

### Observation envelope and privacy

Extend existing typed observations and SQLite owners. These provenance groups are
required where supported; missing fields remain explicitly unknown, and existing
rows must not be presented as already carrying the complete envelope:

- **Source namespace:** provider, local installation/host and source store/root,
  adapter and declared/observed client/schema versions. Namespace logical IDs before
  comparing stores or hosts; a response ID alone is not a global billing identity.
- **Occurrence and relationships:** source occurrence/generation, safe physical
  label/representation, record identity and coordinate system, native thread/turn/
  response/session IDs, and observed or inferred lineage. Missing parent/role does
  not erase otherwise usable quantities. Encoding-neutral coordinates are ING-001;
  current physical checkpoints must not be relabelled as decoded offsets.
- **Times:** nullable source event time and quality, source-recording time only when
  supplied, first collection time, and fetch-start/end or report data-as-of/period.
  File modification time and later re-reading never manufacture original event or
  collection times. Retrospective reconstruction and collection-qualified replay
  retain their different availability rules.
- **Amounts:** original nullable reported fields, native unit/scale and cumulative
  versus increment/snapshot meaning; separate normalized disjoint quantities,
  integrity/defaulting diagnostics and parser/normalization version. Missing is not
  zero. Cached input and reasoning output retain their inclusion relationships.
- **Account and interpretation:** native verified identity, bracket correlation,
  user assertion with its actual assertion time/range, or unknown/conflicting
  ownership. Preserve selected/alternate/superseded interpretation and the reason
  for equivalence, conflict or exclusion. Account creation/current login never
  retroactively assigns all local work to that account.
- **Coverage:** supported surfaces/encodings, scan/request/period bounds, decode or
  schema failures, freshness and explicit absent/unsupported/invalid/stale/zero
  states. Report actual denominators and unknown coverage; avoid an invented
  account-wide completeness percentage.

Durable payloads use closed allowlisted DTOs for numeric/status/provenance fields,
not source JSON extension data, prompts, code, tool text or credentials. Keep
necessary private source locators under their existing local ownership; sanitize
paths and identifiers in diagnostic/shareable artifacts. Reading local data,
retaining it and exporting it remain separate boundaries. This contract introduces
no new source access, credential copying, remote polling or raw-content retention.

### Durable observations and rebuildable projections

Retain original content-free numeric observations and first collection identity
across replay and representation changes. Keep the original reported token counter
snapshots separately from their normalized increments and latest reducer state.
Equivalent copies/re-reads add provenance or last-seen state without adding usage
or restamping first collection. A genuine rewrite retains the superseded evidence
and selects a new generation; a representation change alone does not create one.
Historical response/account reports remain snapshots with alternatives/revisions.

Normalization, metadata enrichment, lineage/account assertions, eligibility and
generation selection are interpretations with versioned inputs/reasons. Preserve
prior values when interpreting/enriching evidence rather than rewriting its original
numeric content. Missing optional model, effort, role, time or account can restrict
an answer while its valid original amounts remain retained.

The selected ledger, timelines, current session/parser/counter state, checkpoints,
fingerprints, reset tails and model/fit caches are rebuildable projections or resume
state. Replacing them is safe only after the required observations/provenance are
durable, with selected-generation membership preventing double counting. A model's
read bound/lookback is not evidence expiry. No automatic age expiry is introduced;
explicit user data deletion and deliberate privacy/schema repair remain separate
operations. Missing legacy provenance cannot be reconstructed as invented facts.

**Current implementation boundary:** the store does not yet satisfy this entire
retention contract. The [native writer](src/TajsTokens.Infrastructure/Persistence/SqliteCodexObservatoryStore.cs)
keeps normalized increments and latest reducer state, and component 10 adds a
[closed counter-input journal](src/TajsTokens.Infrastructure/Persistence/SqliteCodexTokenCounterEvidence.cs)
for new/replayed total and last snapshots, including partial inputs producing no
delta. Equal replay preserves first capture; differing interpretations remain
variants of one occurrence, not additive ledger entries. Legacy original inputs
are unavailable until actually recovered: migration does not reconstruct them from
deltas. Recovery records its actual input-capture time separately from the known
occurrence's original capture. Supported metadata repair preserves amounts and
original observation/collection times. The shared
[generation retirement](src/TajsTokens.Infrastructure/Persistence/SqliteCodexSourceGeneration.cs)
now preserves superseded numeric interpretations through an
[allowlisted retirement snapshot](src/TajsTokens.Infrastructure/Persistence/SqliteCodexRetiredEvidence.cs)
before removing active token/workload/quota/context/timeline projections and
rollout/parser membership. Component 11 keeps replacement identity/reason, known
old generation membership, original nullable quantities and chronology; unknown
legacy generation association remains unknown. Preservation and removal share the
existing transaction, and ordinary accounting reads only active projections.
Counter resume state is retained as state, not relabelled as original input.
Retirements before this capability remain unavailable; no history is invented.
[Response observations](src/TajsTokens.Infrastructure/Persistence/SqliteCodexResponseEvidence.cs)
already survive retirement with active/retired membership, and
[server observations](src/TajsTokens.Infrastructure/Persistence/SqliteTelemetryRepository.ServerEvidence.cs)
retain typed captures with collision checks. ING-002 now verifies equivalent
representation adoption and preflights compressed candidates before replacing
selected history; failed candidates do not retire the selected generation.
Broader EVID-004 scope/answer invariants remain separate from these storage changes.
Existing-schema refusal and original times/amounts remain invariants.

## Quota Intelligence

We can show weak evidence estimates, but they should be marked as such.
I'd rather see "Expected remaining at reset: ~37%, limited evidence" instead of "forecast unavailable".
No estimate should require a formally trained model, but we should aim for at some uncertainty or confidence level.
When evidence is sparse, we should show a range of possible outcomes, rather than a single point estimate. This is especially important for predictions that have high uncertainty.
Learning shouldn't really be a thing permanently. It's okay if it doesn't have any data, but if it has anything at all, we just use it.

### Forecasting

The most important forecasts are most likely the next 15 minutes, remaining at reset, and exhaustion ETA.

Conditional point estimates can learn within a reset cycle. They require compatible, fresh observations, not a trained workload model or a calibrated band. Flat rounded readings do not establish zero burn. Missing account identity, reset information or usable history must be explained specifically.

Fixed-horizon uncertainty uses earlier matured outcomes (non-overlapping errors or chronological blocks, depending on the policy). Reset-horizon uncertainty needs completed compatible cycles at comparable lead; within-cycle blocks cannot establish reset-horizon coverage. These evidence requirements do not hide a supported point estimate. Challenger promotion is a separate decision and preserves the pace baseline when the challenger has not earned selection. Block spacing and evidence/improvement floors are provisional engineering policies, not mathematical guarantees.

The explicit local threshold study varies one engineering choice at a time around immutable released defaults. It reuses existing pace, scenario and composed-selection implementations, reserves the final 20% of candidate origins for comparison, and reports paired error, point/band availability, range coverage/width and non-overlapping support separately. Collection-time availability, account/source/unit boundaries, overlap rejection and assertion-specific safeguards remain fixed. Reset-horizon bands continue to use completed cycles. The command and retained-data results are in [development](docs/DEVELOPMENT.md#forecast-threshold-study) and [source notes](docs/source-notes/FORECAST_THRESHOLD_STUDY.md); no experimental configuration is selected automatically in the app.

### Planning

User should be able to have a "planner", that allows to see whether their current usage will last for: x hours, with y model, with z effort.

The primary Planning tab owns the editable session scenario: duration, optional model/effort, recent-local-work autofill, active-session counts and an independent 1–30 day history lookback. Both windows report conditional consumption and estimated fit against matching fresh account/source/plan/limit cohorts. Unsupported windows remain explicit. A calibrated band crossing available allowance means fit is uncertain; a point-only fit remains conditional. Sessions crossing a reset retain supported consumption estimates but make no post-reset capacity or current-window fit claim. Input edits and quota-anchor changes invalidate results. Forecast/workload history and Model Lab remain separate destinations, not duplicate planners.

## Quota Accounting

The current implementation keeps reported account quota, locally observed native Codex tokens, API-equivalent price weights and inferred quota cost separate. A source account key scopes the quota observations that supplied it; it does not retroactively assign local rollouts to the current login.

### Source time and schema compatibility

Since observatory component schema 9, nullable source event time, first collection time and timestamp quality are separate for token/context evidence. Missing, malformed or unverified source dates remain unknown. Their retained token amounts can contribute to all-time totals, but do not enter hourly history, recent-work autofill or forecasting inputs as invented event dates. Source timestamps require an explicit timezone or supported numeric Unix representation; mixed aggregates have capture time without a single source event date. Component 10 adds the original counter-input journal described above without reconstructing missing legacy inputs.

The version-8-to-9 migration preserves original observation/capture values and counters, marking legacy source times `LegacyUnverified` rather than restamping them. State-index schema 7 clears disposable fingerprints/cursor so bounded, prefix-verified ingestion can enrich only unverified metadata under the original occurrence identity. Amounts and first capture times stay immutable; changed prefixes retain the existing generation-replacement policy. Source-owned Codex data remains read-only.

Component versions are independent of the base telemetry database's `user_version`. An older binary must reject a newer component, not lower its version marker. Install compatible writers/readers together; restoring older binaries is not a database downgrade. Existing-schema planner paths and the newer-schema refusal are covered alongside migration tests.

## Current work

### Accounting, forecasting and TT implementation checklist

Planned 27 October 2026 against clean local `main` at
`e609aebee78712dd26c2eae040005e6dcf2377f3`. The original planning pass changed
documentation only; the stage statuses below record subsequent implementation. The
[research evidence index](.codex/docs/research/2026-10-27/SOURCE_INDEX.md) preserves
the supplied documents and maps recommendations to existing owners. Their contents
are research inputs, not instructions to execute. Pending decisions and candidates
below are not selected production behavior.

Keep the .NET application, SQLite evidence store, native reducer, ingestion batch
writer, intelligence boundary and bounded calibration worker. Extend those owners.
Deliver **responsive operation and trustworthy evidence → corrected pace → joint
accounting and TT → validated continuation → later activity/threshold models**.
Publish useful supported answers at each stage; public TT scoring does not require
a learned quota conversion.

#### Tracking rules

- `[ ]` means unfinished; `[x]` means the item's acceptance has evidence. Keep IDs
  stable across reordering and priority changes. Add a short evidence link and
  disposition when closing an item. Decisions close with a recorded choice;
  experiments can close with an evidence-backed rejection or deferral.
- Check implementation items after focused verification and the required Core
  suite/Windows build. Record automated, retained-data replay, installed runtime and
  visual acceptance separately. A successful build cannot close a runtime gate.
- Preserve existing finding IDs **PACE-001**, **A-01**, **T-01**, **D-01** and **P-01**.
  Link the [audit register](.codex/docs/AUDIT.md) to this checklist rather than
  creating another backlog. The existing baseline below is context, not completed
  new checklist work.
- Commit coherent validated slices. Temporary outputs belong in `.codex/temp/`,
  enduring evidence in source notes or `.codex/docs/`, SDK outputs in `artifacts/`.
  Follow [development procedures](docs/DEVELOPMENT.md) for validation/installation.
  This plan does not start paid experiments, remote operations or a push.

#### Sequence and dependencies

| Stage | Deliverable | Gate |
|---|---|---|
| 1 | Responsive UI/tray; less repeated work | Comparable baseline and recovery during collection/calibration |
| 2 | Representation-safe evidence; explain missing answers | Identity/checkpoint/retention contract before compression; replay/migration invariance |
| 3 | Correct conditional quota pace | Compatible fresh quota; independent of token availability and TT choice |
| 4 | Joint workload projection, catalogs and public TT | Reconciled evidence; T-01 before public basis activation |
| 5 | Support-aware quota conversion | Joint projection and chronological labels; no dependency on a TT scalar |
| 6 | One improved continuation/planning flow | Correct direct baseline; composed route needs supported conversion/mixture |
| 7 | Usual activity, threshold paths and drift | Earlier value and adequate compatible outcomes; conditional research |
| 8 | Acceptance and delivery | Apply to every shipped slice, not just the end of the programme |

Stage 3 can proceed while compressed history is being recovered once its quota
contract is established. Stage 4's joint projection can proceed while deciding the
public TT convention. Do not delay independent correctness fixes for later research.

#### Stage 1 — responsiveness and work reduction

Owners: `WindowsResourcePolicy`, `App`, `MainWindow`, `TelemetryCoordinator`,
`CodexObservatoryService`, SQLite stores/providers and `CalibrationProcessService`.
Memo §11 supplies the mechanisms; their relative runtime cost remains to be measured.

Stage 1 is in progress: PERF-002–008 and P-01 are implemented and verified within
their stated scope; PERF-001 resource acceptance and numerical budgets remain open.
Normal interactive scheduling, retryable/visible EcoQoS, coalesced
fleet reads, independent discovery/catch-up, precise projection invalidation, no-op
write suppression, guarded initialization, indexed reset tails and ordered token
batching are installed. Retained presentation inputs, qualified short history reuse
and hidden-history eviction are also installed. Opt-in aggregate phase diagnostics
and an offline EventPipe reporter are validated; the installed sample identifies
workload-prefix preparation and worker allocation pressure. Evidence-qualified
calibration scheduling and worker-local overlapping dataset reads are installed.
The overview now requests ledger/quota evidence without predictive metadata or
context; forecast/calibration reads retain their existing inputs. Hidden eviction
also releases the navigation shell and preserves pane/expansion state. The Windows
segment heap is enabled within the existing supported OS range; measured savings
are workload-qualified, not a new memory guarantee. Current token predictions now
evaluate their exact training/selection/band dependency closure; full replay reports
remain exhaustive. A frozen worker comparison retained its complete output hash
while reducing wall time from 455.8 to 94.3 seconds. All 707 Core tests and the
Windows build/install pass. Resource acceptance and
numerical budgets remain open under PERF-001. See
[current validation evidence](docs/source-notes/STAGE1_RESOURCE_VALIDATION.md).

- [ ] **PERF-001 — Establish measurements and targets.** Compare startup, warm idle,
  active append, backlog, hide/restore during work and reset boundary. Separate
  parent/worker CPU, private commit, working set, live heap, GC pauses and physical
  versus logical I/O. Capture phase timings, files, hashed/decoded bytes, rows,
  transactions, invalidations, fleet queries, preparation and cache hits. Choose
  numerical budgets from that hardware/data baseline; `BytesObserved` is not a
  physical-I/O counter.
  Aggregate phase capture and separate parent/worker allocation/GC samples are
  recorded. Manual hide/restore was normal for the user; two captured first frames
  took 30.3/96.3 ms; a corrected unload/rebuild restore took 103.3 ms. A confirmed
  hidden sample averaged 415.1 MiB after the unload delay and peaked at 595.1 MiB
  when a 126.9 MiB quota helper overlapped calibration. A fresh background-only
  process previously peaked at 388.2 MiB across the tree. Controlled scenario
  comparisons, attributed physical I/O and numerical budgets
  remain open. The latest five-minute hidden sample averaged 351.8 MiB overall,
  but 496.9 MiB while calibration was active and peaked at 556.9 MiB; it does not
  meet the provisional 400 MiB sustained target. Its restore first frame took
  120.3 ms; [measurement details](docs/source-notes/STAGE1_RESOURCE_VALIDATION.md).
- [x] **PERF-002 — Repair interactive scheduling.** Remove the UI/tray process hard
  CPU cap and routine forced compacting GC, retaining worker containment and
  cooperative bounded collection. Track desired/applied native policy separately;
  report failures and retry failed transitions. Verify restore/shutdown during work
  and injected policy failures.
  Verified by failure/retry/disposal regressions, installed minimize/restore,
  visible Diagnostics state and graceful app replacement during the next build;
  [evidence](docs/source-notes/STAGE1_RESOURCE_VALIDATION.md).
- [x] **PERF-003 — Make completion side effects generation-specific.** Replace scans
  of historical `Refresh completed` display text with typed completion identity.
  Allow one fleet read in flight, coalesce superseded requests before expensive
  reads where possible and preserve freshness expiry. Derived snapshots must not
  repeat a completed collection's fleet scan or reclamation.
  Implemented with typed completion identity and the coalesced fleet reader;
  [652-test/build and installed evidence](docs/source-notes/STAGE1_RESOURCE_VALIDATION.md).
- [x] **PERF-004 — Preserve presentation state and reduce rendering.** Pause hidden
  subscriptions/timers, retain lightweight navigation/filter state and selectively
  evict heavy history. Cache summaries by selection/data generation, coalesce visual
  snapshots and update changed properties. Keep alert transitions lossless and
  independent of visual coalescing.
  Detaches immediately and releases the dashboard frame and navigation shell after 30 seconds hidden,
  following the user's approved tradeoff. It captures page-owned
  scalar inputs/selection identities and Back/Forward entries, releases the frame
  and its page cache, and rebuilds on restore. Brief hides retain the same view.
  Unsaved settings retain their original conflict-detection baseline. History and
  generated visuals reload; CLI output and raw comparison baselines are released
  and require another run or a fresh baseline capture. They are not retained as
  presentation inputs. Installed state/restore acceptance
  is verified; combined resource acceptance remains under PERF-001. A navigation-order correction preserves
  the Planning tab, typed values and unsaved settings through full unloading;
  installed Back navigation passed. Diagnostic text rows now have distinct object
  identities even when labels match, avoiding a reproduced WinUI accessibility
  hang. Full suite: 687 passed; Windows build/install passed; repeated installed
  chat/usage accessibility walks passed. Nested usage range/filter and explorer
  child-filter restoration, Back navigation and history-to-model-lab switching passed.
  The shell extension also preserved Planning duration and Advanced expansion;
  [evidence](docs/source-notes/STAGE1_RESOURCE_VALIDATION.md).
- [x] **PERF-005 — Separate discovery from catch-up.** Deferred/missing/mismatched
  rollouts must not force full discovery every poll. Use bounded fair retries and
  filesystem-fallback budgets, independent relationship reconciliation and correct
  watermarks; prove eventual coverage after recovery.
  Pending work is independent of discovery; conservative durable cursors recover
  after restart. The installed app read one catalog row and reused full discovery
  while the existing unresolved rollout remained explicit;
  [653-test/build and runtime evidence](docs/source-notes/STAGE1_RESOURCE_VALIDATION.md).
- [x] **PERF-006 — Narrow invalidation and stop no-op writes.** Introduce token-relevant
  revisions covering retirement/deletion/time enrichment, skip unchanged durable
  checkpoints and define coalesced last-seen freshness. Reuse bounded fingerprint/
  edge indexes with independent edge invalidation. Measure hot-file prefix work
  while preserving detection of rewritten consumed prefixes.
  Verified by mutation/rollback/replacement/no-op regressions, synthetic prefix
  measurements and installed logical-work diagnostics. Full suite: 668 passed;
  Windows build/install passed; [evidence](docs/source-notes/STAGE1_RESOURCE_VALIDATION.md).
- [x] **PERF-007 — Reuse qualified preparation/evaluation.** Gate expensive calibration
  on evidence revisions and newly matured outcomes, not cooldown alone. Reuse
  overlapping reads/prepared features and useful interrupted-slice progress. Keep
  cheap anchor/time-sensitive projection separate. Invalidate for account/source/
  cohort, parser/schema/settings, backfill/replacement, resets, moving windows and
  expiry; unchanged token counts alone are not a valid cache key.
  Evaluation-local sorted workload prefixes and bounded feature reuse are
  implemented, preserving enclosing-origin collection cutoffs and invalidating on
  late evidence or replacement inputs. Persistent numeric fit recovery remains
  in the existing cache. Its confirmed-WAL connection uses NORMAL synchronization
  for rebuildable fits, following matching-row benchmarks; source/checkpoint
  durability is unchanged. Separate input revisions exclude derived writes and no-op
  maintenance, while expiry/future collection/rolling windows invalidate reuse.
  Current token predictions build only the features and candidate estimates needed
  by the existing 120-row training and two 48-point selection/band windows. Complete
  replay remains the numerical oracle; exact elapsed horizons and evidence remain
  unchanged. Compact candidate-estimate reuse excludes incomplete warmup selections.
  Shared worker feature reads preserve the original account/window/source rules
  and fall back at read bounds or concurrent evidence changes. Full suite: 707
  passed; Windows build/install passed;
  [matching-output and resource evidence](docs/source-notes/STAGE1_RESOURCE_VALIDATION.md).
- [x] **PERF-008 — Bound maintenance and queries.** Put legacy privacy scrubs under
  durable migration ownership and enforce privacy in writers. Guard initialization
  with database-replacement recovery. Inspect fleet/reset/cache-stamp query plans;
  use bounded candidates/incremental tails where measured, rebuilding after
  backfill/deletion. Codex-owned schemas stay read-only.
  Verified by migration/failure/replacement/reset regressions and installed query
  plans. Unchanged reset refresh writes nothing; indexed tails retain raw stream
  partitions and rebuild on late/changed evidence/time qualification. Full suite:
  668 passed; Windows build/install passed; [evidence](docs/source-notes/STAGE1_RESOURCE_VALIDATION.md).
- [x] **P-01 — Measure and conditionally batch token writes.** Benchmark the existing
  per-observation lane against batching within the reducer owner. Adopt only with
  material benefit and parity for order, identity, deduplication, atomic state/event
  writes and checkpoint-after-success recovery. Inject partial failures/restart.
  A measured decision to retain the existing lane also resolves this investigation.
  Adopted ordered batches of 128 after the synthetic 10,000-observation comparison
  (21.73 s → 2.20 s), reversed-order measurement and parity/failure/restart tests.
  Full suite: 668 passed; Windows build/install passed;
  [evidence and reproducible commands](docs/source-notes/STAGE1_RESOURCE_VALIDATION.md).

**Exit:** measured restore responsiveness and lower repeated work without losing
quota collection, alerts, freshness, cancellation or evidence. Warm unchanged sources
avoid unnecessary body reads, writes and retraining.

#### Stage 2 — evidence, reconciliation and availability

Owners: existing discovery/reader/ingestion/checkpoint path, SQLite writer/reducer,
app-server evidence provider/parser, feature and coverage/study builders.

Stage 2 now has the quantity/retention contract, superseded numeric preservation,
compressed-ingestion recovery and scoped provider evidence below. These do not
establish complete source inventory or cross-source billing equivalence. Stage 1 resource
acceptance stays open under PERF-001 while this prerequisite evidence work proceeds.

- [x] **A-01 — Close installed chronology/schema acceptance.** Verify the installed
  binary supports the retained component and ingestion advances. Preserve existing
  v8→v9 migration, newer-version refusal, nullable source time, original amounts and
  first capture times. Keep source/test, installed recovery and presentation
  evidence separate.
  Installed component 9 and advancing ingestion passed: two checkpoints advanced,
  with original amounts/times unchanged. The installed headless worker refused a
  newer marker, preserved rows, then recovered at the supported version while
  withholding untimed evidence from forecasts. Existing migration/query regressions
  passed in the 707-test suite. Recovery coverage and broader presentation remain
  under ING-002/EVID-004/SHIP-004; [scope and evidence](docs/source-notes/STAGE2_EVIDENCE_VALIDATION.md).
- [x] **EVID-001 — Specify authority and retention per quantity.** Refine Source of
  Truth above for current allowance, native tokens and each provider unit separately.
  Define content-free source fields: host/store/occurrence, logical IDs/lineage,
  event/recording/collection times, numeric semantics/normalization, account certainty,
  parser version, selected/alternate generations and coverage/conflict reasons.
  Specify immutable observations versus rebuildable/superseded projections before
  changing replacement/retention behavior.
  Defined in [Source of Truth](#source-of-truth), including quantity-specific
  authority, the allowlisted provenance envelope, original versus normalized
  counters, first collection time, alternatives and selected-generation membership.
  The original destructive-retirement/latest-only gaps were recorded explicitly;
  subsequent counter journaling and retirement preservation are tracked under
  EVID-004. Closing this specification item itself claimed documentation only.
- [x] **ING-001 — Define encoding-neutral identity/checkpoints.** Specify logical
  equivalence, physical representation preference, decoded-byte offsets/hashes,
  resume state and migration of physical checkpoints. Cover siblings, archives,
  restore/resume, forks, inherited prefixes and genuine rewrites. Select a compatible
  Zstandard dependency with license/packaging review; keep strong prefix verification.
  Defined in the [representation contract](docs/source-notes/CODEX_ROLLOUT_REPRESENTATIONS.md):
  scoped, prefix-proven equivalence; decoded UTF-8 coordinates; verified legacy
  adoption; separate physical identity; candidate generations and bounded streaming.
  Selected ZstdSharp.Port 0.8.8 after pinned package/source/license inspection.
  This closes design and dependency review only; migration, shipped notices,
  decoding/resource proof and recovery remain ING-002/EVID-004 implementation gates.
- [x] **ING-002 — Implement compressed discovery/read/recovery together.** Extend
  the existing owners as one slice, with bounded decoding/memory and explicit
  corrupt/truncated/unsupported states. Test plain, compressed, both-present,
  archived, restored/resumed and representation races, interrupted final records,
  cancellation/restart. Equivalent evidence must preserve totals and capture times.
  Implemented through the existing filesystem provider, collector, ingestion and
  checkpoint owners: plain-preferred discovery, retained-handle verification,
  bounded streaming Zstandard validation before writes, decoded-byte checkpoints,
  verified archive aliases and explicit read-failure reasons. Telemetry schema 16
  adds coordinate/representation metadata without relabelling old checkpoints;
  state-index schema 8 revisits disposable skip hints. Original IDs/times survive
  verified adoption; new occurrence IDs namespace the local host/store. Blank
  complete lines advance checkpoints; valid empty replacements retire their old
  projections through the evidence-preserving path. Validation details are in the
  [Stage 2 evidence record](docs/source-notes/STAGE2_EVIDENCE_VALIDATION.md#compressed-rollout-ingestion-ing-002).
  The 747-test Core suite and Windows build passed. Installed adoption reached
  403 compressed checkpoints while preserving all 96,120 pre-deployment native
  rows and 164 journal rows; no adoption retired a generation. Installed preflight
  memory stayed bounded for 16/256 MiB synthetic decoded sources, and timed
  cancellation/retry passed. This does not claim complete corpus/lineage coverage
  or close the aggregate app/worker resource budget.
- [x] **EVID-002 — Reconcile already-collected response/provider evidence.** Check
  installed app-server capabilities/schema, pagination and account bracketing.
  Never sum response/turn/thread cumulative representations. Preserve IDs, scope,
  periods and distinct estimated credits/dollars, nullable native amounts and
  purchased balances. Expose bounded thread selection/cadence rather than claiming
  complete live coverage; unknown capability is not zero usage.
  Installed CLI 0.164.0 schema/captures verify the usage contract, absent pagination
  and account bracketing. A read-only 439-rollout audit found 28,993 exact vector
  pair candidates and 194 unpaired responses; response/turn/thread snapshots remain
  supplemental and overlapping, not additive ledger inputs. New provider batches
  retain exact requested threads, examined/eligible counts, truncation and the
  six-thread/30-day/30-minute policy; installed collection retained all nine scoped
  results. All-null activity is unavailable, while non-token metrics remain useful.
  The 757-test suite and Windows build passed. [Evidence and limits](docs/source-notes/STAGE2_EVIDENCE_VALIDATION.md#responseprovider-reconciliation-evid-002).
  Sampling text is wired into evidence views; the visual pass remains unconfirmed
  because a Windows Security passkey prompt reappeared on focus. No authentication
  action was taken. Cross-source billing equivalence and complete inventory remain
  unproven; these results do not promote supplemental counters or quota fits.
- [x] **EVID-003 — Explain every eligibility exclusion.** Extend structured support
  reasons into a waterfall with stage denominators, reason overlaps and sanitized
  examples: discovery/encoding, decoding, timely inputs, required role/model/effort/
  account, compatible training, mixture support and outcome availability. Split
  `origin-workload-unavailable` and classify the reported 67 cases on retained data;
  do not infer their distribution or lower all thresholds to make them disappear.
  **Implemented:** the read-only `--eligibility` report shares study origins/splits,
  feature calculation and production scenario selection. It reports shrinking stage
  denominators, overlapping reasons, interval/origin training counts and sanitized
  numeric examples. Discovery/decode denominators unavailable in the retained
  dataset remain explicitly null; the ING-002 acquisition checks are separate.
  A named frozen follow-up snapshot reproduces 67/116 final weekly exclusions:
  63 have no retained dated events in the lookback and four contain only later-
  collected events, with no unknown-role-only exclusions. All remaining 49 pass
  training but fail duration support. The first half-hour example misses the
  shortest training duration by about 1.35 seconds; FORE-003/005 owns any subsequent
  support-policy change. Product explanations identify the unsupported coordinate
  and compatible-history counts. No threshold or model-selection change was made.
  The 762-test Core suite, Windows build and installed diagnostic passed;
  [reproduction and limits](docs/source-notes/FORECAST_THRESHOLD_STUDY.md#eligibility-follow-up-evid-003).
- [x] **EVID-004 — Separate retention from answer requirements.** Keep valid amounts
  with unknown role/model/time/account, preserving missing versus zero. Prove unknown
  role does not disable token accounting/direct pace, untimed data stays out of dated
  forecasts, unknown model retains quantities and unassigned work never inherits
  current login. Include deduplicated all-time amounts only where scope permits.
  Add durable allowlisted original counter inputs and superseded-generation
  evidence before replacing destructive retirement. Preserve first collection and
  distinguish unavailable legacy history from new observations; active projections
  must select generations without counting alternatives twice.
  Original counter-input journaling is implemented in component 10: closed nullable
  snapshots, immutable replay, interpretation variants and atomic input/reducer
  writes. Both writer paths retain those inputs through a genuine source rewrite.
  The 714-test Core suite and Windows build passed; installed ingestion produced
  three associated journal rows. All 95,956 existing native rows retained their
  amounts and chronology across migration; [validation](docs/source-notes/STAGE2_EVIDENCE_VALIDATION.md#original-counter-input-journal-component-10).
  Component 11 additionally preserves superseded native increments, counter state,
  quota, context, workload and numeric timeline interpretations before projection
  retirement, with allowlisted payloads, replacement provenance, original missingness
  and rollback protection. Active totals exclude these snapshots. The 720-test full
  suite plus a precision regression and Windows build passed. Installed migration
  preserved 95,995 existing native rows and all 39 prior journal rows;
  [validation](docs/source-notes/STAGE2_EVIDENCE_VALIDATION.md#superseded-numeric-retention-component-11).
  ING-002 subsequently enabled verified compressed replacement. **Answer acceptance:**
  all-time native totals keep amounts with unverified time/model and expose their
  overlapping coverage. Dated daily/Fleet readers now require verified source time;
  neither collection time nor legacy observed time supplies a date. Daily JSON v2
  separates its date range from all-time coverage and refuses unsupported schemas.
  Missing role/model/effort does not veto direct token or quota pace; current login
  does not assign native ownership. Unpriced quantities remain visible without an
  invented zero API valuation. Replay, compressed/archive adoption and genuine
  retirement preserve one selected amount while retaining alternatives separately.
  The 767-test Core suite, final Windows build and installed v2 report passed.
  All 96,259 pre-deployment native rows and 303 journal rows retained their original
  amounts and chronology. Cross-source/global equivalence remains unresolved;
  [scope and evidence](docs/source-notes/STAGE2_EVIDENCE_VALIDATION.md#answer-specific-support-evid-004).

**Exit:** trace a displayed amount to observations and reproduce it after replay,
duplicate discovery, compression and supported migration. Late imports improve
reconstruction without inventing past online availability.

#### Stage 3 — correct conditional pace

Owners: `QuotaPaceModels`, `QuotaForecastCalibration`, forecast/reset-outlook owner
and shared intelligence presentation. Keep prediction accuracy separate from repair
of the known observation/polling defect.

- [x] **PACE-001 — Replace the defective production fallback.** Start from existing
  recent-bracket/time-weighted candidates with elapsed-time, precision and freshness
  semantics. Retain legacy EWMA for reproducible research and exclude it from
  production eligibility after repair. Correctness and challenger promotion are
  separate gates: sparse calibration must not reactivate the invalid fallback.
  Do not hardcode the incident's rate, add an arbitrary floor or hide it with smoothing.
  **Implemented:** reset v5 and fixed-horizon v6 use the existing elapsed-time
  `time-ewma-2h` fallback; production candidate/selector paths exclude legacy EWMA
  even when explicitly supplied as a fallback. Explicit legacy research replay and
  old forecast evidence remain available. Cost baseline/version tags advance with
  the correction. Two hours remains provisional, not a universal context decision.
- [x] **PACE-002 — Verify observation mechanics.** Cover the retained incident,
  identical endpoints/elapsed time with sparse/dense cached polls, startup, rounded
  jumps, all-flat periods, resets, stale collection and ongoing/missing local work.
  Distinguish cached duplicates from genuinely fresh later flat readings; neither
  establishes exact zero burn. State rounding/lag assumptions.
  **Verified:** retained incident, sparse/dense fresh flat polls, same-clock cached
  copies, startup, all-flat/invalid/reset cases, stale measurements imported later,
  ongoing/missing local work and source/account/bucket boundaries. Returning plan
  regimes retain the intervening boundary rather than resurrecting an old slope;
  independent buckets stay independent. Rounded movement is spread across observed
  intervals; backend lag and exact physical consumption remain unknown.
- [x] **PACE-003 — Carry a calculation receipt.** Include anchor/cohort, measurement/
  collection times, interval/counts, estimator/version, numerator/denominator,
  fallback and coverage. Explain changed answers. Sustainable budget remains
  remaining allowance divided by clock time to reset, separate from consumption.
  **Implemented:** closed v1 receipts persist in forecast diagnostics, round-trip
  through the repository and appear in history/telemetry details. They reconstruct
  rate numerator/exposure and the independent reported-remaining/clock-time budget.
  Unsupported/invalid measurements and old captures retain explicit missingness.
- [x] **PACE-004 — Evaluate corrected defaults.** Compare later outcomes by horizon,
  reset lead and active/burst/idle/missing-source state with availability, point and
  signed error. Preserve completed-compatible-cycle requirements for reset bands.
  Supported conditional points remain visible without invented calibrated probability.
  **Evaluated:** read-only fixed-candidate and actual reset-selector replay at
  5/15/30/120-minute, eligible 24-hour and near-reset targets, with later final fifth,
  state/reset-lead partitions, availability, signed/absolute/squared error, missed
  movement and non-overlapping support. The largest weekly cohort improves at
  15/30/120 minutes versus legacy but worsens at 24 hours (seven matched/one
  non-overlapping outcome); five minutes has one match and final reset truth is
  absent there. This repairs mechanics without claiming universal accuracy or
  selecting a new window. Completed-compatible-cycle reset bands remain unchanged.

**Exit:** polling frequency cannot manufacture the reproduced decay. Report future
accuracy separately; the incident's old high rate is not an acceptance target.
The 774-test Core suite, Windows build, installed score reproduction and persisted
receipt reconstruction passed. Native amounts/original chronology were preserved;
[policy, results and limits](docs/source-notes/STAGE3_PACE_VALIDATION.md). SHIP-004
receipt visual acceptance and PERF-001 resource acceptance remain separate.

#### Stage 4 — joint workload, catalogs and a public TT basis

Owners: event/dataset projections, accounting feature builder, `TtWorkloadBasis`,
TT evaluator, existing model metadata, `ApiPriceWorkload` and product snapshot.

- [x] **SCORE-001 — Preserve sparse model × category quantities.** Build joint cells
  from reconciled events, with supported tier/product/context distinctions and
  explicit unknown mass. Keep disjoint conservation and reported totals; never
  multiply marginal shares to invent joint cells. Distinguish vocabulary overflow
  from missing metadata. Carry joint information into recent-work/forecast caches;
  test equal marginal totals with different joint costs.
  **Implemented/installed:** `codex-joint-workload/v1` aggregates selected increments
  directly, preserves exact category/reported amounts and per-event diagnostics,
  and separates bounded vocabulary overflow from overlapping missing-label mass.
  Execution tier/product/charged context remain explicitly unknown in native rows.
  Product Evidence, origin-qualified feature caches and conditional forecast
  composition carry the joint mixture. The fixed-month check conserved 3,984,535,029
  tokens across 20,078 events and all 759 cost intervals; 24 chronological comparator
  score/trial sets matched the prior installed baseline. **781 Core tests** and the
  Windows build passed; installed read-model and numeric-retention checks passed.
  [Contract and limits](docs/source-notes/STAGE4_JOINT_WORKLOAD.md). Joint-model
  calibration and public TT activation remain later work; visual acceptance stays
  separate.
- [ ] **T-01 — Select the public TT convention.** Resolve DEC-001 before activation:
  exact schedule/weights, reference basket, units/vintage, support/equivalence,
  speed normalization and extension rules. TT is a frozen reference-consumption
  index. Defining a basket does not require running paid synthetic tasks.
- [ ] **SCORE-002 — Version the joint-coordinate basis.** Reuse immutability,
  normalization and hashing while preserving the old five-category research basis
  and artifacts. Identity covers dimensions, weights, basket and semantics. Preserve
  original scores and explicitly chosen restatements. New prices/models never
  silently alter old weights or activate a new ruler.
- [ ] **SCORE-003 — Support exact, bounded and partial scoring.** Derive missing-field
  requirements from the requested score: equal-weight output splits can be exact;
  unknown cache splits can give finite bounds; unknown models retain covered
  subtotals without guessed weights. Distinguish exact-under-basis, finite bounds,
  partial/unbounded and inconsistent constraints. Implement cheap common cases;
  generic feasibility solving belongs in bounded/offline work when justified.
- [ ] **CAT-001 — Separate discovery and entitlement.** Version model metadata with
  source/retrieval time, aliases, retired entries and capability/effort support.
  Listed, observed and account-access-verified are distinct. Unknown observed models
  stay ingestible without an update or paid probe; no extra API key just for a picker.
- [ ] **CAT-002 — Version prices independently.** After DEC-002, validate feed schema,
  units/currency, model/category/tier/context and applicable effective dates. Preserve
  snapshots, separate overrides/labelled fallbacks, last-known-good recovery and
  stale/missing states. Current/date-effective API-equivalent valuation remains
  separate from subscription usage and frozen TT.
- [ ] **D-01 — Clarify versions and expose measured TT.** Distinguish evaluator
  `tt-lab/v4-blocks`, research `tt-category-basis/v1` and the new public basis.
  Show observed TT with basis/coverage beside native tokens even when conversion is
  unavailable. Update public feature documentation when this actually ships.

**Exit:** the selected basket scores one TT; a price refresh cannot move the frozen
ruler. Unsupported TT coordinates retain usable token accounting. Current quota is
not presented as a TT wallet.

#### Stage 5 — support-aware quota conversion

Owners: existing observation builder, accounting model, training/evaluation/coverage
policies and bounded calibration worker. A TT scalar is not required for this route.

- [ ] **CAL-001 — Diagnose identification and contrast.** Report joint coverage,
  effective rank/collinearity, independent blocks/cycles and scenario support. More
  copies of one mixture do not identify individual prices. Keep the fixed-first-20
  experiment as a frozen comparator, not a sample-size theorem or permanent policy.
- [ ] **CAL-002 — Compare a restrained joint model.** Retain total-token and pooled
  category baselines; test nonnegative model/category coefficients shrunk toward
  pooled weights using the existing solver family. Add effort/tier departures only
  with evidence beyond observed tokens. Evaluate realized-work cost explanation
  separately from end-to-end future-work prediction.
- [ ] **CAL-003 — Make support scenario-specific.** Permit justified familiar-mixture
  answers despite unidentified individual coefficients; bound/withhold unsupported
  edits. Prototype feasible-set diagnostics under explicit rounding/lag/attribution/
  mismatch assumptions. Report infeasible/unbounded results, never silently widen
  tolerances or fit missing external account activity as local prices.
- [ ] **CAL-004 — Type and label uncertainty.** Keep measurement, identification and
  predictive ranges distinct. Report envelope containment/intersection separately,
  assumptions, width and point-error bounds. Preserve the existing intersection
  disclaimer; it does not establish calibrated latent coverage or survival probability.

**Exit:** matched chronological outcomes justify richer conversion or it stays
research-only. Supported direct quota answers remain available without local attribution.

#### Stage 6 — continuation and one integrated planning flow

Owners: workload/composition helpers, forecast/scenario owner, `ICodexIntelligence`,
shared projection and the existing Planning page; no duplicate planner/estimator.

- [ ] **FORE-001 — Define continuation inputs.** Separate observation interval, recent
  context, training span and horizon. Specify joint mixture/intensity and included
  tool waits/idle time. Compare a small fixed set of existing recent/time-weighted
  contexts; do not globally change two hours to twelve or assume future sleep.
- [ ] **FORE-002 — Evaluate one joint-path challenger.** Compare corrected direct
  pace, recent joint-workload continuation through supported conversion and a small
  contiguous-block resampling candidate. Preserve pairings, bursts/pauses and volume/
  composition dependence. Use bounded revision-qualified caches and deterministic
  seeds; resampling cannot create unseen support.
- [ ] **FORE-003 — Integrate supported planning edits.** Start from recent work and
  preserve useful existing duration/model/effort/count/lookback controls. Distinguish
  same-token revaluation from changed-model task prediction. Keep count scenarios as
  comparators, mark unsupported edits and matching account/window scope, and retain
  restrictions on post-reset capacity/current-window fit for reset-crossing sessions.
- [ ] **FORE-004 — Extend one product snapshot.** Overview/Planning and then tray show
  allowance, measured tokens/TT, conditional runway and inspectable change reasons.
  Carry exact/bounded/partial/unsupported/stale/uncalibrated states with compact
  source/freshness labels. Preserve account selection, filters and anchor invalidation;
  no page-specific math or invented generic confidence number.
- [ ] **FORE-005 — Earn horizon-specific selection.** Evaluate 5/15/30/120-minute
  targets and supported reset leads with rolling origins, timely inputs/labels and
  overlap-safe tuning/holdout boundaries. Compare direct/composed on matched outcomes
  and show separate availability. Report point/signed accumulated error, width/
  coverage, missed bursts, false exhaustion warnings and warning lead time. Retain
  timestamped shadow predictions before labels mature; keep reconstructed history
  and operational replay separate.

**Exit:** a recent-work plan is useful end to end with explicit assumptions and
reproducible evidence. A challenger that fails evaluation leaves the corrected baseline
selected; uncalibrated ranges do not hide otherwise supported point estimates.

#### Stage 7 — conditional later research and extensions

Begin only with a supported question and earlier evidence; completion may mean
rejecting/deferring a candidate. Not every research proposal must become a feature.

- [ ] **NEXT-001 — Evaluate usual activity separately.** Start with supported historical
  activity and recent positive-work amounts; compare a small activity/amount model
  only if needed. A hurdle mean is not a path distribution or exhaustion probability.
  Label habitual activity separately from current-work continuation.
- [ ] **NEXT-002 — Evaluate coherent threshold paths.** Propagate the same workload
  paths through both windows with explicit supported resets. Preserve no-crossing
  mass and label crossing-conditional timing. Do not take minima of marginal quantiles
  as first-exhaustion quantiles. Allowance threshold differs from execution stop,
  purchases and grace; probability labels require predictive validation.
- [ ] **NEXT-003 — Evaluate censoring.** Preserve saturated evidence and current
  ordinary-loss exclusions. Measure selection effects before introducing justified
  lower-bound/censored targets. Unknown reporting lag prevents claiming collection
  times bracket true crossing; clipped usage is not exact latent cost.
- [ ] **NEXT-004 — Test shared-window and drift hypotheses.** Compare proportional
  shared-cost against separate conversions on aligned compatible evidence. Reuse
  the regime owner/frozen reference for the dashboard marker. Separate known plan/
  tier changes, mixture, coverage, attribution and lag before inferring policy drift;
  adaptation must not move the TT ruler.
- [ ] **NEXT-005 — Decide whether more machinery is warranted.** Test bounded local
  slope, adaptive state/lag or conformal layers only against a demonstrated remaining
  failure and simpler comparators. Record selection/deferral. No universal latent TT,
  new always-on Python/Wolfram service, neural predictor or collection framework is
  implied by the research.

#### Decision checkpoints

Resolve consequential choices when a slice is concrete; routine implementation
choices stay with its owner. Recommendations below are not selected numeric policies.

| ID | Choice and recommendation | Needed before |
|---|---|---|
| DEC-001 / T-01 | Recommend frozen dated tariff-reference TT first, empirical TT separately. Choose exact schedule, joint weights, basket and standard-speed versus tier-specific semantics together. | Public basis activation, not joint projection |
| DEC-002 | Choose pricing source, override/fallback policy, refresh cadence and effective-date treatment; unknown effective dates stay unknown. | Dynamic price refresh; a pinned TT schedule can be specified independently |
| DEC-003 | Choose interval semantics/context candidates using the incident and later outcomes. Recommend a small existing-candidate comparison with horizon-specific selection. | Releasing changed pace/default or continuation policy |
| DEC-004 | Set CPU/disk/memory/latency budgets from PERF-001. Consider collector isolation only if measured cooperative work is insufficient. | Resource acceptance; no new service by assumption |
| DEC-005 | Define explicit basis activation, extension and historical-restatement policy. Compatible new-model extensions preserve old weights/normalization with a new identity. | Extending/replacing a public basis |

#### Stage 8 — acceptance and delivery for each shipped slice

Apply these gates to each slice and record its results when closing the related
items. Keep the programme-wide SHIP boxes open until all selected delivery is
complete; one successful early slice does not validate later changes.

- [ ] **SHIP-001 — Preserve reproducible evidence.** Record source/binary versions,
  sanitized fixture hashes/ranges, configurations/seeds, chronological splits,
  support counts and limits. Check cold/warm/reopened/missing-cache/narrow-invalidated
  parity. Translate relevant synthetic counterexamples into product regressions;
  supplied Python/Wolfram outputs establish algebra, not production performance.
- [ ] **SHIP-002 — Complete automated validation.** Run focused tests, then full Core
  suite and Windows build serially at code milestones/completion. Algorithm changes
  need relevant backtests; durable changes need migration/replay/idempotence/recovery
  checks. Run deployment checks for deployment changes. Do not repeat unchanged
  passing checks without a new reason.
- [ ] **SHIP-003 — Verify installed behavior separately.** Use existing backup and
  schema-compatible installation/recovery procedures. Record installed revision,
  whether the running version changed, fresh ingestion, quota/alerts, cancellation
  and measured responsiveness. Isolated builds leave this open; honor user-reserved
  runtime testing.
- [ ] **SHIP-004 — Complete presentation acceptance and docs.** Check actual Overview,
  Planning and tray, physical tray interaction, partial/stale/unsupported states,
  reset crossings and readable labels. Record user visual acceptance separately.
  Update implemented behavior here, public README only for shipped features,
  procedures in development docs and evidence in source notes. Commit validated
  slices; push only when requested or previously authorized.

#### Current implementation handoff

The Stage 1 mechanisms are installed with their stated evidence; PERF-001 resource
acceptance remains open. EVID-001 now defines the quantity/retention contract.
Installed **A-01** chronology acceptance has the stated runtime evidence. Continue
Stage 2 with the accepted **ING-001** identity/checkpoint contract, component-10
original counter-input journal, component-11 superseded numeric retention and the
**ING-002** compressed ingestion and **EVID-002** scoped reconciliation slices.
**EVID-003** supplies eligibility explanations and the retained 67-case breakdown;
**EVID-004** supplies the stated retention/answer and selected-generation acceptance.
**PACE-001–004** now implement and evaluate the elapsed-time correction under the
established quota contract. **SCORE-001** now carries selected joint evidence through
the installed product and conditional forecast projections. Continue Stage 4 with
the explicit public-basis/catalog decisions and versioned joint scoring. No public
TT ruler is activated yet.

### Existing implementation baseline and known gaps

Overview keeps current account allowances above the selected history views. Date, model, project and chat filters do not erase live context or allocate account quota to filtered work. Explicit account selection never substitutes another account's current reading. The shared outlook prioritizes exhausted meters, then earliest exhaustion before reset, then pressure against each window's sustainable pace. Both reported windows need fresh supported assessments for an overall sustainable-pace conclusion; partial and unreported windows remain explicit. Reset estimates and fixed-horizon predictions retain separate targets and uncertainty.

[PACE-001](docs/source-notes/WEEKLY_PACE_INVESTIGATION.md) is repaired by the
[Stage 3 elapsed-time policy](docs/source-notes/STAGE3_PACE_VALIDATION.md).
Production selection excludes legacy per-poll EWMA; its old rates remain explicit
research evidence. Current forecasts carry calculation receipts and keep reported
sustainable budget separate from consumption estimates. Later-period accuracy is
still horizon-dependent: sparse five-minute/reset labels and poor long-horizon
constant-pace extrapolation do not establish a universal window or calibrated
survival probability. Flat meters and missing local activity never prove idleness.

The tray/UI process keeps normal scheduling and has no hard CPU cap or routine forced compacting GC. EcoQoS remains a background hint with retryable native transitions exposed in Diagnostics; detailed calibration retains its separate worker budget. Typed collector completion generations coalesce supplementary fleet reads, and cached fleet status ages by clock time independently of database changes. Hidden dashboards still release their page cache and active queries/timers, retaining navigation destinations rather than selected history; PERF-004 owns preserving useful lightweight presentation state. Quota collection and alerts continue, with a full configured polling delay after each pass. Compact quota-pace outlooks remain independent of detailed calibration and never load its training dataset. Full source discovery now follows its reconciliation cadence independently of bounded pending rollout retries; a persistent failure remains explicit without forcing full discovery every poll.

Detailed workload calibration and token nowcasts run in one owned headless process in either mode, independently of the collector and UI. Its Windows job limits total-system CPU to 0.5% in the background or 2% with the dashboard visible, private commit to 384 MiB and its managed heap to 256 MiB. Background-started slices allow ten minutes with a five-minute cooldown; foreground-started slices allow three minutes with a one-minute cooldown. The longer background wall budget lets historical data preparation finish at the lower CPU rate. Showing the dashboard adjusts an active worker's CPU budget immediately. Closing the app terminates its worker. Timeouts or memory-limit failures retain quota-pace outlooks and cached progress rather than blocking collection. Detailed results are published as fresh only for an unchanged fresh quota anchor; token nowcasts are stale if their native accounting generation changed. Full forecast promotion checks collection-time qualification before reconstructed comparisons; model selection, source provenance and acceptance criteria remain the same.

Completed numeric ridge fits are reusable across slices and restarts in the rebuildable app-owned `calibration-cache.db`, separate from telemetry evidence. Keys include the solver version and exact binary training features, targets and penalty; payloads contain only numeric fit parameters with checksums. Cache storage is bounded to 16,384 entries and 32 MiB plus SQLite journals, with at most 256 fits in memory. Changed inputs miss the cache, and corruption or cache unavailability falls back to calculation. The existing headless evaluation executable hosts the worker and shares the installed app's self-contained runtime; no external service or automatic remote processing is introduced.

Implemented Codex-focused audit slices include the local Live fleet, explicit host-qualified SSH status checks, descriptive cache input mix, an opt-in authenticated read-only local limits API, a read-only inventory of source/account combinations retained in quota history, and aggregate daily JSON reports from the existing native event ledger. The inventory is derived from observations rather than a second mutable account registry. Unknown account keys remain unknown, and matching API windows receive the same opaque account ID only within the same provider and source. Daily local workload is explicitly unattributed to a backend account.

Successful SSH fleet checks now retain append-only host-qualified status snapshots with separate remote capture and local collection times, allowing offline status review. SSH is optional and never needed for account-wide provider quota on the PC; local Tokscale-style scanning likewise cannot supply another machine's private thread history without an additional source. No remote agent or automatic SSH polling is assumed. The status snapshots do not establish remote account ownership or provide lossless cross-machine token/quota history. Further work needs source-specific evidence before product claims: a Codex notify hook must sanitize content-bearing payloads and prove overlap with existing rollout accounting; detailed context-component token costs and per-prompt quota costs cannot be inferred from current totals; full distributed usage history needs explicit host/account provenance, source-qualified deduplication and offline recovery. Dynamic price catalogs and burn/runout baselines are research candidates, not substitutes for the existing fixed-rate counterfactual and chronological forecast evaluation. No remote quota is merged into a local account, and no process-level Stop action is provided.
