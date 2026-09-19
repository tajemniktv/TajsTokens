# TajsTokens product and architecture

> **Status: active product development.** This is the single authority for product goals,
> architecture, source contracts, retention, implementation status and next work.
> [Current work](#current-work) distinguishes delivered behavior from planned changes.
> [AGENTS.md](AGENTS.md) owns stable contributor rules; [README.md](README.md) describes shipped
> user-facing features; [the contributor guide](docs/DEVELOPMENT.md) owns build/recovery procedures.

## Development stage

The foundation reset established the evidence, provenance, privacy, and provider-native boundaries below. It is no longer the organizing phase of the project. TajsTokens has working acquisition, normalized history, native source inspection, forecasting, and product surfaces; the task now is to integrate and harden them into a coherent application.

Extend the existing implementation where it serves the product. Do not restart discovery or rebuild working systems merely because they originated before the reset. Investigate concrete semantic gaps and runtime disagreements, then deliver and validate the corresponding product behavior. The evidence rules remain permanent engineering constraints, not a reason to postpone useful features indefinitely.

## Product direction: Codex-first observability

TajsTokens is a **Codex-first observability and intelligence application**. The goal is a dependable daily companion for understanding Codex activity, usage, quota, history, and likely future behavior. It is not primarily a database explorer, a generic AI telemetry platform, or a replacement Codex client.
Quota planning is a primary product outcome: what remains, whether current or planned work will
exhaust it, and what changed. Rich native history and usage understanding support that outcome
and remain useful independently. A token-count prediction alone does not complete quota forecasting.
Codex exposes a large and evolving set of source-native concepts. Where source contracts establish their semantics, TajsTokens should preserve and use that richness rather than flattening it into generic `session`, `event`, or `usage` records merely because those names might also fit another provider later.
At the same time, TajsTokens should not become permanently shaped like Codex. Possible future providers influence the architecture through clean boundaries, not through premature genericization.

The guiding rule is:

> **TajsTokens is Codex-first, not Codex-shaped. Provider-native semantics are preserved. Shared abstractions are projections, not storage requirements. A provider is never forced into another provider's ontology merely to obtain a unified UI.**

This means:

- Codex may have a rich first-class model for concepts that Codex actually exposes, such as threads, turns, items, relationships, tools, jobs, logs, catalog state, or other contracted source-native structures.
- A future provider may have a completely different first-class model, such as runs, requests, generations, workers, queues, hardware observations, or concepts we have not encountered yet.
- Shared infrastructure may unify provenance, capture time, observation identity, contract/version metadata, persistence mechanics, and query plumbing without requiring a universal semantic payload.
- Shared product views may project different provider-native models into a common answer only where the relevant semantics have been demonstrated to overlap.
- Provider-specific views are allowed, and often preferable, when a provider exposes useful information that has no honest cross-provider equivalent.
- Future-provider support is a design constraint on seams and ownership, not current implementation scope.

A useful mental model is:

```text
Rollout JSONL + app-server quota + selected Codex SQLite observations
    -> source-specific acquisition and normalization
    -> TajsTokens telemetry.db (provenance-aware durable evidence)
    -> reconciled read models
    -> history / analytics / usage prediction / quota outlook / UI

Content-heavy or unnecessary-to-duplicate native data
    -> direct read-only inspection (not automatically copied into telemetry.db)
```

There is deliberately no universal provider schema in the middle of this pipeline.
One physical owned database can hold workload facts, quota observations and selected native
metadata, alongside separately identified derived tables and checkpoints. Sources are observation
mechanisms feeding that store, not competing forecasting architectures. Combining evidence does
not mean copying every native table, losing provenance or adding overlapping counters.

## Product end state

A production-quality local Windows application should let the user:

- **Understand now:** open Overview and immediately see reported quota windows, usage, recent activity, freshness, and actionable problems. Missing windows, source failures, and stale history must look different.
- **Understand work:** navigate workspaces, threads, turns, and supported root/subagent relationships; inspect relevant history and token/context behavior without manually joining SQLite rows and JSONL files.
- **Understand changes:** investigate quota movement, lifecycle events, source disagreements, and collection gaps through concise summaries with evidence available on demand. Correlation must not masquerade as causality.
- **Plan ahead:** see conditional quota outlooks, sustainable pace, supported workload scenarios, and useful uncertainty. Calibrated probabilities are an earned capability, not a release requirement that can be satisfied by inventing confidence.
- **Trust the record:** understand what was observed, where it came from, when it was observed/collected, and what remains unknown. Repeated collection must not double-count work or rewrite historical meaning.
- **Operate comfortably:** use responsive, accessible native views, reliable background collection, safe upgrades and recovery, and explicit privacy/export controls. Raw explorers remain useful diagnostic tools, not prerequisites for everyday workflows.

The architectural end state is **selective durable collection + direct local inspection + rebuildable reconciled views**. Codex owns its operational data; TajsTokens owns its selected observation history and derived intelligence. Future providers may add their own native models when there is concrete demand, without weakening the Codex experience.

This is the target, not a declaration that every workflow is complete. Delivery gates and outstanding
work are in [Current work](#current-work); dated audits below describe captured evidence, not timeless guarantees.

## Current work

This is the single active roadmap, consolidated 2026-09-19. The architecture and source contracts
below remain authoritative. [Historical milestones](docs/archive/PROJECT_CURRENT_WORK_2026-09-19.md)
preserve the previous chronological log, metrics and validation results; they are not current tasks.
The [evidence review](docs/source-notes/CODEX_EVIDENCE_REPO_REVIEW.md) maps source findings and
remaining proof boundaries. The [layered design](docs/source-notes/TT_LAYERED_STATISTICAL_DESIGN.md)
defines model/TT rationale. Neither is a competing delivery plan.

**Evidence-visibility progress:** Model Lab and cost CLI now expose dataset-local token-weighted
model/effort coverage, category mismatch/invalid counts, unknown/after-event collection times,
physical requested-tier setting counts, and per-cohort/horizon quality flags and quota-account/
assertion counts. Counts do not attribute local tokens to an account, infer billed tier or establish
copy/interleave coverage. They are rebuildable diagnostics with no new acquisition or retention.
Construction diagnostics now partition eligible-epoch interior candidate starts into built targets
and first-rejection reasons (overlap, warmup, saturation, absent outcome or polling gap, incompatible
cohort). Model Lab places matching strict-replay candidate counts/reasons beside that stage, without
adding their overlapping exclusions or treating absent comparisons as zero. These counts are not
all raw readings, independent outcomes or proof of collection failure.
**Next implementable slice:** investigate representative composition mismatches and reconciliation
disagreements using these diagnostics; distinguish source/reporting gaps from absent recorded work
without inventing completeness. Use existing Model Lab/read-model owners, not a new framework.
New observations block some validation claims, **not all development**.

### Ordered roadmap and acceptance

| Priority | Remaining work | Acceptance / dependency |
|---|---|---|
| 1. Evidence-quality visibility | Consolidate model/effort/tier coverage, unknown quantities, account attribution, reporting gaps/revisions and reconciliation exclusions in evaluation output | A result explains included/excluded/unobserved evidence and competing explanations; requested settings never become billed tier |
| 2. Reconciliation investigation | Establish lineage for representative real copy/interleave/reset disagreements; compare existing contenders under identical inputs | Promote only supported identity/overlap rules with replay/restart/replacement parity; ambiguous cases stay explicit. Some cases need better native identity evidence |
| 3. Layered evaluation | Evaluate cost and future-workload models separately by horizon, composition, coverage and reset; test frozen models on ordinary incoming work | Identical eligible targets for baseline comparisons, no future collection/revision/association leakage; retain raw-token/pace/incumbent competitors |
| 4. Supported product improvements | Promote by horizon where evidence warrants it; distinguish nowcasts, conditional session scenarios, reset outlooks and user-planned work | Existing promotion gates pass; stale/idle/unknown/sparse cases retain truthful fallback. Joint calibrated ranges need independent completed cycles |
| 5. TT product decision | Evaluate empirical transfer and choose whether a supported local workload index earns a user-facing role | Freeze identifiable basis/reference, expose partial support, compare scalar conversion with full-vector cost. If task-level TT is chosen, implement original scoring/provenance and explicit restatement first |
| 6. Useful backend extensions | Establish daily date-boundary/revision behavior; add plan/task/workspace or other reports only for a defined product question and usable capability | Populated source contracts, compatible units/identity/time, typed retention decision and tests; no repeated denied probes or silent auth-boundary expansion |
| 7. Product acceptance | Inspect new diagnostics, saved research results and outlook explanations in native UI, including empty/stale/partial/corrupt states | Build/test/runtime evidence is separate from user visual acceptance; evidence should be understandable without reading source notes |

Priorities 1 and 3 are the immediate development path. Reconciliation can proceed when concrete
disagreements are available; backend expansion is opportunistic, not a prerequisite for local models.
UI acceptance accompanies each shipped slice, not merely the end of the roadmap.

**Evidence-dependent, not claimed complete:** native-credit normalization (sampled credits were
zero), populated plan/task/workspace semantics, empirical cross-context TT conversion and adequately
supported joint quota ranges. Ordinary post-reset usage supplies future evidence; a banked reset is
not itself a policy change. Do not lower cycle requirements or generate synthetic prompts to force
a favorable result.

**Optional research, not mandatory scope:** logistic/Gamma or hierarchical challengers, adaptive
fits, continuous/endpoint presence models, per-task TT history before a product decision, additional
native-log extraction, renewal/skill/plugin reports without a use case, or a shared cross-user basis.
No synthetic benchmark traffic, crowdsourcing, OAuth refresh writer, account switching, universal
TT claim or replacement forecasting framework is planned.

### Implemented baseline — do not restart

| Area | Existing behavior / owner |
|---|---|
| Daily product | Overview quota/freshness/nowcasts, work/thread navigation, usage breakdown, quota history, workload planner, progressive diagnostics and native-source inspection |
| Evidence | Source/account cohorts, immutable server observations and shared daily value sets, nullable quota metadata and requested service tier, revocable bounded ownership assertions |
| Experimental analytics | Opt-in daily counts/relative reports, immutable snapshots, pairing exclusions and semantic-change diagnostics |
| Reconciliation research | Shared corpus audit and ordered sequence comparison; containment/lineage contenders, restart/copy fixtures; no canonical promotion |
| Completed-work cost | Non-overlapping observations; pace/total/category/model-effort candidates and context/activity/runtime/time ablations; frozen-reference residual diagnostics |
| Workload and quota | 5/15-minute activity-aware nowcasts, 30/60-minute any-work/positive-amount scenarios; origin-only composition, strict and retrospective composed/session quota evaluation |
| TT research | Frozen content-addressed basis, separate calibration, scalar/full-vector/raw comparisons, transfer experiments, signed/cumulative error, original aggregate research snapshots |
| Recovery | Source-generation replay/checkpoints, owned schema migrations, source/account-scoped reset identity, whole-data deployment backups and explicit rollback |

Implementation is not empirical validation or promotion. Historical test counts and benchmark
results belong in the archived milestones and evidence records, not live counters in this roadmap.

### Current acquisition and evidence constraints

Normal quota/account/activity reads remain Codex app-server-owned. Account activity is supplemental,
not replacement local-token truth. Usage replies lack native account identity: stable before/after
pseudonym brackets provide server correlation only. Native, server-correlated, user-declared and
unknown/conflicting ownership remain distinct. Associations never rewrite native IDs or actual
collection times; asserted history cannot become native held-out validation.

The existing collector schedules bounded server-evidence reads at most every 30 minutes per process.
Latest attempts are selected per surface/thread regardless of correlation outcome; a newer failure
or null reply cannot leave an older successful estimate current. Owned schema 14 shares daily values
and ordered bucket sets without losing fetch identity, revisions, nulls, collection times or history.

The explicitly enabled daily adapter uses the selected Codex login in memory, fixed HTTPS origin/routes,
bounded responses, disabled redirects/cookies and backend brackets plus a final selected-account check.
It never refreshes/writes credentials. A switch rejects both reports. One-shot collection does not
enable polling or modify auth/settings. No raw responses, native account IDs, credentials or content
are retained. Typed snapshots retain requested UTC range, units/freshness, parser/source context,
account pseudonym, plan and before/after window signatures. Polls are snapshots, not additive spend.

Daily pairing requires compatible account/brackets, versions, plan/policy context, ranges, units,
grain and freshness. Missing/zero credits, balance spill, tiny denominators, incomplete days and
ambiguous end dates cannot establish a native scale. Seat/product scope and historical denominator
era remain unproven. Activity tokens, reset entitlements, balance debits, relative percentages,
estimated thread credits and API-price weights are different quantities.

Quota metadata retains named/legacy buckets independently of supported windows, reached reason,
nullable spend controls, model alias, credit flags/balance and reset fields. Malformed optional metadata
does not hide usable quota; alternative buckets are not summed. Requested service tier preserves
spelling, source/offset/time and explicit turn IDs; it is not silently attached to later token events.
Observatory schema 7 retains it. Desktop suffixed filenames use the first thread UUID with matching
metadata; state-index schema 5 invalidates disposable discovery hints for replay. Alternate paths
remain inspection evidence, not automatic import or inferred independent workload.

Semantic diagnostics derive from immutable evidence with input IDs, version, account, first-observed
time and unknown effective time. Configuration, missingness, capability, blocking and revisions stay
separate. Ordinary consumption, balance depletion and resets do not imply policy drift. Failed or
overlapping fetches, account switches and source/contract changes break comparisons. The independent
512-fetch/5 MiB per-surface budget means no detected change is not proof of stability.

### Current model and presentation constraints

Use separate cost, activity/amount, composition and horizon-specific quota models. Retain disjoint
token categories; effort/context/activity may explain workload rather than an extra per-token price.
Plausible explicit assumptions are allowed, but must remain distinguishable from observed facts.

- **Nowcast:** `rollout-token/v2` predicts recorded local tokens at 5/15 minutes. Recent positive
  tokens, starts or tool activity qualify under the ten-minute policy; settings alone do not.
  Quiet open turns, inactivity and unknown/stale evidence have distinct explanations.
- **Session outlook:** `recorded-session-outlook/v2` targets any positive recorded work within
  30/60 minutes, not continuous presence or endpoint activity. Recency-group frequency and
  conditional mean/10–90% historical positive range have explicit sparse fallback. Live probability
  and unconditional mean need 64 earlier same-group held-out outcomes passing calibration/Brier
  gates. Descriptive conditional ranges are not calibrated joint quota bands.
- **Completed quiet outcomes:** evaluation ends at the earlier of requested time and dataset capture,
  not the last token. Snapshot age cannot manufacture negative targets; collector gaps remain a
  competing explanation. The session decomposition reuses retained token time/amount fields.
- **Composed quota:** `composed-quota/v5` preserves separate retrospective and strict collection-time
  evaluations. Only required cost inputs govern availability; unknown capture time never becomes
  event time. Recovered old work cannot qualify a historical prediction retroactively.
- **Promotion:** retain the incumbent unless a supported total/category/model-effort candidate wins
  cost and paired end-to-end comparisons against incumbent and pace, with at least eight independent
  reset generations and current composition/outcomes. Unknown support, identity, shifts or calibration
  preserve fallback. Oracle context/activity/runtime features cannot leak into live forecasts.
- **Ranges:** shared calibration uses at least eight earlier completed resets with available labels,
  generation-maximum absolute errors, empirical 80% target and 1pp floor. Report count, coverage and
  width; these are not latent-usage coverage, survival probabilities or activity-conditioned bands.
  `session-quota-evaluation/v2` remains a separate research chain using actual target elapsed times
  and matched incumbent subsets.
- **Pace:** even-burn compares consumption and elapsed window at the reading, using explicitly
  inferred reset-minus-duration start. Missing/invalid windows are unavailable; stale cards remain
  last-known and wall-clock passage alone does not improve their comparison.
- **API-price baseline:** versioned counterfactual workload weights, not native credits, actual bills
  or execution tier. Unknown rates withhold fitting/evaluation coverage; not a production candidate.

Additional native fields require per-field source/content/privacy/retention decisions before collection.
Native rollouts can report positive total tokens with zero category counters. Preserve both;
do not substitute cumulative movement or fabricate a category allocation. Per-record mismatch flags
prevent offsetting errors from disappearing during interval aggregation. Category-dependent cost
training/evaluation, transfer and recent composition now withhold incomplete evidence; raw-token
cost baselines retain reported totals. Comparison gates require identical target sets rather than
zipping unequal trial lists. This changes derived model eligibility, not stored token accounting.
Supported improvements should reach the product after validation, not remain permanently in a harness.

### TT basis and original research-output contract

`tt-lab/v3` freezes nonnegative category weights on the first 20 compatible known-account intervals.
One million tokens in the first positive interval's category mix defines its reference basket; the next
20 intervals calibrate quota/TT and competitors. Exact coefficients, reference, support, pooled observed
model/effort dimensions and input semantics define the immutable basis ID. Unknown/incomplete categories
or unsupported mixtures are withheld, never zero. Tier/context are unmodelled. TT may summarize local
work without universal transfer; quota prediction retains the full vector when a scalar loses accuracy.

Transfer uses the exact earlier basis and destination-local calibration only, requiring the same
account/provider/profile/source/session lineage and horizon, with source context ending before
destination begins. No compatible pair is not successful transfer. Frozen weights and revisable quota
calibration remain separate; historical restatement must not rewrite original forecasts or scores.
Signed bias is prediction minus reported cost, reset-balanced; cumulative eligible-interval error
has summed meter-envelope bounds, not confidence intervals or complete account totals.

Explicit Model Lab evaluation saves original aggregate reports in intelligence schema 6:
basis/reference/support, calibration, cohort labels, errors/coverage, requested range, dataset cutoff
and actual save time. Immutable IDs and payload hashes make retries idempotent and conflicts errors.
Payloads are bounded to 512 KiB/512 score rows; reads to 20 snapshots (UI ten). Corrupt/unsupported
rows remain visible; older absent diagnostics are unavailable, never silently recomputed. No automatic
pruning/background scoring is added. Whole-database backups retain reports. No prompts, titles, auth
or native raw payloads are stored. These are aggregate research records, not original per-task TT
history, subscription balances or live forecast promotion.

### Other scoped product gaps

Broader auxiliary pagination/thread-scoped queries and snapshot-consistent multi-page log reads remain
unfinished. Alternate-file coverage is best-effort and does not prove full account accounting. Address
these through concrete navigation/inspection workflows, not by restarting the foundation. The workload
planner currently consumes explicit hypothetical inputs, not an inferred recent work pattern.
Window size/maximized preference is retained separately in `data/window-state.json`.

### Acceptance and upkeep

Choose coherent vertical slices with existing owners. Keep automated tests, runtime checks and native
visual acceptance distinct. Use AGENTS.md for proportional validation/deployment and docs/DEVELOPMENT.md
for commands/recovery. Preserve unrelated work and native data; scrutinize overlap, data loss and privacy.
Update this roadmap when priorities, behavior or verified limitations change—not after every command.

## Local installation and owned data

Successful local Windows app builds dogfood a verified self-contained publish into
`%LOCALAPPDATA%/Programs/TajemnikTV/TajsTokens/current`, with a stable Start-menu launcher,
graceful lifecycle handshake, prior binaries, stopped-app database/settings backups, and an
explicit build opt-out. CI and test-only builds must not alter the daily installation.
Git commit/dirty identity is build provenance, not Codex source evidence.

Owned application data lives in the sibling `data` directory, not in a replaceable binary
generation. A first-run copy-and-promote migration retains the old `%LOCALAPPDATA%/TajsTokens`
database, settings, WAL/SHM and local inspection exports unchanged as a recovery copy. Existing
destination data is never merged or overwritten. Legacy build-validation outputs are excluded.
Failed migrations remain separate and retryable. The migration requires no other legacy app
instance to be running. This is a local storage relocation, not new collection or export.

Deployment never writes Codex-owned sources. Binary rollback does not imply that a newer
database schema can be downgraded; live data is never automatically overwritten on rollback.
Matching backups and failed binary generations remain available for deliberate recovery.
No automatic deletion/retention policy is introduced. Startup acknowledgement proves shell
initialization, not source availability, full workflow correctness, or user visual acceptance.

## Working principles

- **Evidence before interpretation.** A field existing does not prove what it means beyond what the source supports.
- **Unknown is a valid result.** Missing, ambiguous, delayed, or unexplained data must remain explicit.
- **Provenance is mandatory.** Normalized observations must retain where and when they came from and what source identity produced them.
- **Conflicting observations are data.** One source must not silently overwrite another merely because we prefer it.
- **Normalization must not invent semantics.** Conversion into a stable shape may rename or type known fields, but may not manufacture relationships that have not been demonstrated.
- **Preserve provider-native richness.** Do not discard a safe, understood source-native field or relationship merely because the current UI does not render it or a hypothetical future provider may not have it.
- **Shared abstractions must be earned.** Similar names or shapes are not enough. A shared concept is introduced only when its observable semantics are sufficiently equivalent for the read-model question being answered.
- **Derived views are rebuildable.** Selection, aggregation, classification, and policy belong above durable evidence and must be replaceable without rewriting history.
- **Native state and derived presentation state are distinct.** If a read model later classifies source-native states into broader categories, the source-native value remains evidence and the broader category remains explicit policy.
- **Local visibility is not durable collection.** TajsTokens is local-first and should let a user inspect the data their local Codex installation exposes, including content-bearing values where appropriate. Showing a source value on-device does not by itself justify copying it into TajsTokens' durable evidence store.
- **Export is a separate privacy boundary.** If TajsTokens exports source data, the export path must make the data leaving the local inspection boundary explicit and should support sanitization/redaction where appropriate. Raw and sanitized export modes, if both exist, must be distinguishable rather than silently changing the evidence.
- **Privacy stays conservative for duplication and secrets.** Do not expand durable duplication of prompts, reasoning text, source bodies, credentials, authentication material, or other sensitive payloads merely to make analysis easier. Secret-bearing data requires explicit handling even when the source can be inspected locally.
- **Predictions are policy, not evidence.** Forecasts, confidence scores, scenarios, inferred contributors, and classifications must remain rebuildable outputs with explicit inputs and policy/model versions. They never become source facts merely because they were persisted for history.
- **Evaluation before sophistication.** A more complicated forecasting model earns its place only through leakage-safe historical evaluation against simpler baselines. False precision and uncalibrated confidence are product defects.
- **Implementation is evidence, not semantic authority.** Preserve working behavior unless a supported correction or deliberate product change requires otherwise; names and existing formulas alone do not establish meaning.
- **Do not design for hypothetical provider symmetry.** A clean provider boundary is valuable; forcing Codex into a lowest-common-denominator model is not.

## 1. Raw source acquisition

Work one source at a time. For each source, first establish how TajsTokens can read it and collect representative examples across normal and awkward states.
Local inspection may expose raw source values to the user. Fixtures, bug reports, documentation examples, and other artifacts intended to leave the user's machine must be sanitized or deliberately reviewed before sharing.
At this layer we record what the source emitted. We do not calculate product metrics, reconcile it with other sources, or decide which source is "right".

Source acquisition must document at least:

- access mechanism and lifecycle;
- raw response/record shapes;
- timestamps and identifiers present in the source;
- optional/missing fields;
- observed variants across versions or runtime states;
- error, unavailable, partial, and stale-looking responses;
- whether the source is read-only from TajsTokens' point of view.

Raw content does not automatically belong in the durable database. Retention is a separate decision made per source after privacy, product value, and reprocessing needs are understood.

## 2. Source contracts

A source becomes usable by the architecture only after it has an empirical source contract.

A source contract records:

- what has been directly observed;
- field names, types, units, and value ranges where known;
- identity semantics: what makes two observations the same or different;
- time semantics: event time, capture time, reset time, update time, or unknown;
- update/mutation behavior observed over repeated reads;
- relationships between fields that are demonstrated by evidence;
- known limitations and coverage boundaries;
- unresolved questions and hypotheses, explicitly labelled as such;
- sanitized fixtures, reproducible probes, or matching implementation evidence supporting the contract.

For Codex sources, evidence should be **evidence-first, not research-first**. Installed runtime/source data is authoritative for what the selected build actually emits. Matching or near-matching `openai/codex` implementation, migrations, protocol types, and tests are first-class corroborating evidence for intended semantics and may support a source-contract relationship when they are explicit and consistent with observed runtime data. Record the upstream commit/tag/version used and whether it is known to match the installed build.
Targeted experiments remain required when behavior is version-sensitive, Desktop-only, ambiguous, contradictory, or when source coverage is uncertain enough that a user-facing label would otherwise overclaim. Absence of a Desktop/app-local subsystem from the public Codex repository is not evidence that an observed local source is invalid.
A source contract must distinguish **known**, **observed but unexplained**, and **hypothesized**. Hypotheses do not become normalization rules merely because they are convenient.

### Source-contract template

```text
Source:
Acquisition method:
Read/write posture:
Representative samples:

Observed fields:
Observed variants:
Identity semantics:
Time semantics:
Update/mutation behavior:
Coverage:

Known:
Observed but unexplained:
Hypotheses to test:
Unknown:

Fixtures/probes:
Open questions:
```

## 3. Normalized observations

Normalization converts source-specific evidence into stable TajsTokens observations only where the source contract justifies the conversion.
**Normalized does not mean generic.** The preferred normalized form is usually provider-native and source-aware. A Codex state-thread observation, Codex turn observation, rollout observation, or app-server observation may remain explicitly Codex-specific if that is the honest semantic model.
Every normalized observation must retain enough provenance to answer:

- which provider/source produced it;
- which source record/object/window/thread/turn or other native identity it came from, when such identity exists;
- when the source says it happened, if known;
- when TajsTokens captured it;
- which fields were actually present;
- which source contract and normalization version produced the normalized form.

Shared observation infrastructure may provide a common envelope for provenance, timing, identity, and versioning. It must not require unrelated providers to share the same payload schema.
Normalization must preserve meaningful distinctions between sources and between provider-native concepts. We will not force unrelated concepts into a universal schema merely because they look similar in a dashboard.
Two conflicting source observations may normalize into two conflicting observations. Resolving that conflict is not the normalizer's job.

## 4. Durable evidence store

### Decision: keep a separate TajsTokens database

Keep the owned SQLite database separate from Codex's databases. Do not mirror their schemas, merge their files into ours, or introduce a general-purpose bidirectional synchronizer. There are three complementary paths:

| Path | Responsibility | Retention |
| --- | --- | --- |
| Direct source inspection | Read current native details and user-requested raw/content-bearing records through source-specific readers | On demand; not automatically duplicated |
| Durable acquisition | Collect contracted, safe observations whose history the product needs | TajsTokens-owned evidence with native identity and provenance |
| Read models and intelligence | Link observations, apply explicit presentation policy, aggregate, forecast, and evaluate | Rebuildable projections/caches, distinguishable from source evidence |

The existing owned database can contain evidence, ingestion checkpoints, and derived tables with explicit ownership. These are logical boundaries, not a requirement for three databases or a generic JSON payload bus. Runtime settings need not move from their existing storage merely to fit this model.

For every source-specific collection decision, specify the fields, historical need, native identity, change-detection method, retention/privacy posture, and source/schema/parser version. SQLite metadata should be collected only where a concrete historical need warrants it; a field being visible in a native explorer is not sufficient justification.

### Verified daily-workflow integration map (2026-09-08, afternoon)

This is a bounded implementation map, not a field-by-field contract for every native table.
Read-only SQLite transactions against the current installation found 352 state threads,
1,845 history turns, and an owned database with 351 sessions, 7,763 workload observations,
97,993 quota observations, and 351 state-index fingerprints. Native SQLite uses SQLx migrations
(`user_version` is 0); owned `user_version` is 8 plus component schema versions. The numbers
are non-atomic **across databases**, can change during collection, and must not be reconciled
by making table counts equal. The earlier dated inventories below remain historical evidence.

| Daily question | Current source and reader | Owned storage / read policy | Boundary and remaining gap |
| --- | --- | --- | --- |
| What quota is reported now? | App-server quota response through `TelemetryCoordinator` | `SqliteTelemetryRepository` stores source/account-qualified `quota_snapshots`; current lanes keep freshness/omission separate from history | The response's backend-account pseudonym separates quota streams; unknown historical scope stays unknown. It does not identify a person or attribute local thread work. |
| What work exists and how is it organized? | State/project/spawn rows through `CodexThreadObservabilityService` and `ICodexThreadReadModel` | Direct on-demand inspection; explicit `CodexThreadReadModelPolicy` preserves alternatives | Native thread IDs link supported records. Current model/project metadata is not historical turn context. Owned `sessions`/`agents` are rollout-derived projections, not a mirror of every native thread. |
| What happened in a thread? | History SQLite turns/items and separate realtime lane through the native thread reader | On-demand local content; selected source and provenance retained in the view, no durable transcript copy | Projection coverage may differ from retained JSONL. Missing history is not proof that a turn never happened; alternative sources are not silently concatenated. |
| How much local token/context activity was observed? | Owned rollout records through `CodexRolloutParser` / `CodexSessionIngestionService` | `codex_native_token_events`, `context_observations`, `codex_workload_observations` retain selected safe observations; counter/parser/checkpoint tables support replay | Native record/turn/session identity and source offsets qualify observations. Collection time is separate from event time; legacy missing collection times remain missing. Native state totals are not added to rollout totals. |
| Which files need collection? | `CodexStateCatalog` reads compact mutable state rows; filesystem discovery remains the compatibility fallback | `codex_state_thread_fingerprints` / `codex_state_sync` are replaceable acceleration state; ingestion batch writer owns transactional rollout progress | `updated_at_ms` is not a complete change feed. Full fingerprint reconciliation occurs at startup and on the first refresh after five minutes; unchanged bodies stay unopened. No source deletion propagates into historical deletion. |
| Why did quota move; what may happen next? | Source-isolated owned quota and bounded local workload via `SqliteIntelligenceService` / `SqliteForecastDatasetReader` | Burn intervals, reset classification, forecast/scenario policy, and `forecast_snapshots` are derived, not provider facts | Never sum overlapping sources or train on future observations. Rebuildability depends on retaining the relevant evidence, not just forecast outputs. |
| Why is data missing or different? | Source reader diagnostics, telemetry health, raw explorers | Direct inspection plus bounded product diagnostics | Acquisition/index progress is not yet a complete per-source coverage report; count differences alone are not errors or proof of loss. |

The timestamp gap is corroborated by `codex-rs/state/src/runtime/threads.rs::replace_rollout_path_if_current`
at local upstream commit `a51608398d53b6d23ed98b8287de415b35f1eea5`: it updates only
`rollout_path`, without advancing `updated_at_ms`. That clone is not asserted to match the
installed desktop exactly; the installed schema supports the queried fields, and sanitized
regression fixtures cover an old thread changing path outside the warm timestamp overlap.
Five minutes is explicit collection policy, not a native delivery guarantee. Failed full
passes retry on the next refresh; successful scans do not copy current model/effort onto old events.

Outstanding integration decisions: prove account/installation scoping before attributing local
work to a quota account; audit source-generation replacement and rollout-only coverage before
claiming exhaustive discovery; expose retained-versus-native coverage without inventing equality.
This integration-map research alone does not authorize additional durable fields, data deletion, or a schema migration. The separately implemented account-scope migration below is not part of that research-only restriction.

#### Native index versus rollout path coverage follow-up

A subsequent read-only path audit found 352 distinct state-index paths and 354 JSONL files in
the configured discovery roots: 31 discovered paths were not indexed, while 29 indexed paths
were outside that discovered set. All 352 indexed paths existed. A bounded `session_meta`
inspection of those 31 unindexed files found thread IDs already present in state. This does
**not** prove identical file contents or missing work; it establishes that path-count differences
cannot be treated as counts of additional threads. No alternate files were imported by the audit.

The collector now attaches ephemeral `CodexCollectionCoverage` to full catalog reconciliations.
Overview's existing source diagnostics show its observation time, accessible indexed paths,
configured-root discovery count, unindexed paths, and indexed paths outside that set. Warm
refreshes retain the original coverage timestamp rather than pretending the enumeration ran again.
Discovery remains best-effort (inaccessible/reparse-point paths can be excluded), not proof of
complete filesystem or durable-history coverage. Unindexed files are not automatically ingested
while the state catalog is usable. The inspection-only alternate-rollout policy below does not
authorize promotion of additional records; same thread ID is not sufficient.

When the selected state database path changes, the collector immediately rereads the new source
without the old timestamp cursor. A successful full reconciliation re-anchors that disposable
cursor to the selected catalog; existing fingerprints/history are not deleted. Same-path source
replacement is covered by the periodic full comparison, not claimed as immediately detectable.
Sanitized generation-switch tests cover a new catalog whose timestamps are lower than the old
cursor and verify retained fingerprints. Path-coverage tests cover indexed files outside discovery
roots, unindexed alternatives, unchanged warm refreshes, and diagnostics reaching the product.

#### Alternate-rollout inspection policy (2026-09-08)

`CodexObservatoryService` exposes a separate, user-invoked `ICodexRolloutInspection` capability
through **Codex > Rollout coverage**. It reuses the collector's selected state catalog and
configured-root discovery; it does not use or mutate ingestion checkpoints, fingerprints,
counter state, cached coverage, or owned observations. Routine refreshes do not read alternate
file bodies. Existing source diagnostics point to this inspection rather than treating path
counts as evidence completeness.

Each page inspects at most eight unindexed paths, in stable path order, with a 2 MiB and
20,000-record limit per file and a 30-second cancellable UI operation. An indexed counterpart
is selected only from a unique state-thread/filename ID match, including indexed paths outside
discovery roots. That is candidate selection, not proof of ownership. Both filenames and an
observed `session_meta.payload.id` must corroborate the indexed thread before comparing owned
records. A fresh no-UUID file does not acquire ownership from its first metadata record.
Non-owning prefixes are kept distinct; they are not counted as the child's records and are not
asserted to be a parent relationship merely because they precede the owner. A later conflicting
session owner, invalid JSON, incomplete final line, unreadable/replaced/changing input, or an
inspection limit produces an explicit unresolved result.

The comparison is of captured bytes, not normalized token totals or semantic equivalence:

- **Identical bytes:** complete captured file bytes match; ownership is a separate reported fact.
- **Identical owned records:** owned record bytes match while non-owning prefixes differ; the
  whole files are not identical.
- **Prefix overlap:** one complete owned record stream is an exact byte-for-byte prefix of the
  other. Additional records are not automatically missing collected work.
- **Different records:** owned streams differ after their common prefix. Formatting changes
  alone can cause this result; it is not proof of conflicting semantics or additional usage.
- **Unresolved:** no safe comparison at these bounds. Unknown is not a zero-overlap conclusion.

Physical identity, length, creation time, and write time are checked around each read pair to
reject ordinary concurrent replacement/change. Native files and catalog rows do not form an
atomic snapshot; same-size in-place edits with deliberately preserved metadata are not promised
to be detectable. Results are dated, local, ephemeral inspection output. Transcripts, reasoning,
tool bodies, byte buffers, and comparison hashes are not retained in the owned database or exported.
No comparison result permits automatic import, deduplication, deletion, or accounting changes.

The bounded native inventory selected `state_5.sqlite`: 353 indexed paths, 355 discovered paths,
31 unindexed paths, and 29 indexed paths outside discovery roots. Of the eight smallest alternate
pairs sampled, five were byte-identical with corroborated owners and three exceeded 2 MiB. The
other pairs were not compared; these counts are dated observations, not a corpus-wide equality claim.
The implemented C# capability was also exercised read-only against the installed source: its
first path-ordered page returned two identical pairs and six unresolved comparisons, with no
owned database created. This is a different sample ordering from the smallest-file inventory.
At local upstream commit `a51608398d53b6d23ed98b8287de415b35f1eea5`,
`codex-rs/thread-store/src/local/rollout_migration.rs` stages/reprojects and renames replacement
rollouts while checking source length/mtime. This corroborates the need to handle replacements;
it does not establish that migration caused these particular copies, or that the installed
desktop exactly matches that commit.

Existing ingestion retains physical file identity and offset-qualified record identity, restores
parser ownership at the matching checkpoint, and writes projection batches before ordered token
reduction and checkpoint advancement. `codex_counter_state` is keyed by session while source
event deduplication is physical-record-qualified. Therefore replay safety within a known source
does not establish safe accounting across divergent/copy files (especially last-only counters).
Any future alternate collection must first specify source/record equivalence, overlapping counter
epochs, active/retired generation selection, replay/recovery, migration and retention tests. This
inspection slice deliberately changes none of those writers.

### Linking sources without erasing their meaning

- Preserve each source-qualified observation before reconciliation. Join through supported native identifiers and relationships; do not infer identity solely from similar names, timestamps, totals, or paths.
- A mutable SQLite thread row can describe current metadata; a rollout turn context can describe historical metadata. A later current-model value must not overwrite the model recorded for an earlier turn.
- SQLite totals and rollout increments may overlap. Establish their accounting relationship before choosing or comparing them; never add both merely because both are available.
- Keep authority specific to the question and field. Current quota uses the supported authoritative app-server bucket; neither every SQLite row nor every JSONL field has universal precedence.
- Keep provider/profile, installation/source instance, and proven account scope distinct. A local rollout directory is not proof that all its work belongs to the currently authenticated quota account.
- Preserve disagreements and expose the selected view's rationale. Reconciliation rules are versioned policy, not destructive ingestion updates.

### Collection and lifecycle guarantees

Use read-only native access and consistent bounded reads where the source supports them. Incremental collection must have source-specific identities/checkpoints, idempotent replay, and atomic evidence/checkpoint commits. File offsets require source-generation/truncation handling; SQLite polling must use a supported change signal rather than assuming row IDs are a universal change feed.

Preserve source event time separately from actual collection time. A backfill reconstructs available history; it cannot claim those records were collected at their original event times. A disappearing native row/file must not silently delete retained historical observations. Conversely, direct-inspection content may become unavailable when Codex removes it: the product must not promise transcript recovery it never retained.

Migrations, retention, backups, and rebuild operations must distinguish irreplaceable collected evidence from disposable projections. Never silently delete unique quota observations or other evidence that cannot be reacquired. Changes to these lifecycle policies require explicit implementation and validation; this section defines the target guarantees, not proof that every maintenance workflow already exists.

### Current owned-data retention and recovery policy

The current local policy is **no automatic age-based history deletion**. There is no reset-history
button. This is intentional for a small dogfood application; add retention controls only when measured
growth or a user request warrants them. This does not prohibit existing ingestion corrections or
replacement of active accounting projections when a rollout generation changes.

| Owned data | Retention / rebuild posture |
| --- | --- |
| `quota_snapshots`, context/workload observations, legacy `token_usage` / `usage_events` / `reset_events` / `announcements` | No age-based expiry. Native sources may disappear and live quota cannot be reacquired retrospectively. Do not assume legacy rows are disposable merely because a newer pipeline exists. |
| `codex_native_token_events` | Active normalized accounting, not an immutable archive of every physical generation. The existing batch writer retires the replaced path's token generation while activating its new identity, avoiding double counting. No separate historical-generation archive is promised. |
| `sessions`, `agents`, relationships, repositories/workspaces | Retain existing rollout-derived metadata. Some projections can be recalculated while source evidence remains; there is no blanket safe-delete promise after native history is removed. |
| Rollout file/record identities, parser/counter state, ingestion checkpoints, schema/revision tables | Managed with the associated active replay/checkpoint generation. The batch writer retires obsolete path-generation bookkeeping transactionally. These are not independent user-cleanable caches; deleting a subset can cause re-ingestion or lost continuity. |
| State-index fingerprints and sync cursor | Rebuildable acquisition acceleration. Existing reconciliation re-anchors on selected-source changes; it does not delete collected history. |
| `forecast_snapshots`, `quota_reset_events` and in-memory burn/scenario/evaluation results | Derived, not source truth. Recalculation requires the retained inputs and policy; saved results are currently retained, not periodically purged. |
| `tt_evaluation_snapshots` | Immutable original aggregate research outputs, not native facts or per-task scoring history. Explicit evaluation saves exact basis/calibration and results; later reconstruction is a separate run. No automatic pruning. Existing whole-database backups include these rows; read failures never replace originals with recomputed results. |
| Settings, local exports, deployment backups and retained binaries | User-owned/local recovery material, retained until explicit removal. Exports are separately user-invoked; this policy does not authorize sharing them. |

Normal local deployment takes stopped-app data/settings backups and supports binary rollback.
Restoring data remains an explicit stopped-app operation described in [the contributor guide](docs/DEVELOPMENT.md); preserve the
entire current data directory before restoring a matching snapshot. Binary rollback does not migrate
a newer database backward. No new automatic restore, deletion, source write, or upload is introduced.

The Diagnostics page consumes the existing in-memory collector snapshot, shows per-source health,
quota/forecast explanations and bounded recent events, and links to source inspection and Settings.
Opening it performs no acquisition; its Refresh action uses the existing shared coordinator. It does
not infer root causes from error text or claim durable incident history. Overview keeps detailed
forecast evidence/methodology behind an expander rather than repeating it in the main message.

The durable store is history of normalized evidence, not a warehouse of product conclusions and not an automatic mirror of every readable source payload.

Its design must support:

- deterministic observation identity and replay/idempotence where possible;
- preservation of distinct source observations;
- provider/source-native identities and relationships where established by contract;
- append/rebuild-friendly history;
- explicit source and normalization provenance;
- schema evolution without silently changing the historical meaning of a stored row;
- rebuilding read models after policies change;
- conservative handling of content-bearing or credential-bearing source material.

The evidence store may contain provider-specific tables or structures when that preserves contracted semantics cleanly. A single generic table is not inherently more architectural than several honest provider-native structures.
Fact/evidence tables must not contain values whose only justification is a current UI formula, cross-source selection policy, inferred classification, or other derived interpretation.
A source field being useful for local inspection does not automatically make it appropriate for durable duplication. Durable retention of content-bearing values must be justified independently from the ability to display those values from the source on demand.
If an existing database table cannot satisfy these constraints cleanly, compatibility with that table is not more important than getting the evidence model right. Migration, rebuild, or replacement remain valid options.

## 5. Read models

Read models turn durable evidence into answers the application can use now. They are derived, policy-driven, and replaceable.

TajsTokens may have two kinds of read models:

1. **Provider-native read models.** These may be rich and provider-specific. For Codex, they can join contracted evidence into useful Codex concepts and relationships without pretending those concepts are universal.
2. **Shared observability projections.** These may project multiple provider-native models into a common answer such as current activity or recent failures, but only where the required semantics genuinely overlap.

A provider-native read model may combine multiple contracted sources for the same provider. Any source selection, reconciliation, grouping, or field precedence is read-model policy and must be explicit and inspectable. Conflicting evidence remains preserved underneath the selected view.
A shared projection is never a storage requirement. For example, a future Codex thread and a future local inference run might both project into a broad activity summary while remaining completely different first-class models in evidence and provider-specific views.
Where relevant, a read-model result should carry or expose:

- evidence/source provenance;
- capture/observation time;
- provider-native identity;
- coverage or scope;
- freshness;
- missing/ambiguous/conflicting evidence;
- the policy/version used to choose among, classify, or combine observations.

A read model must never make a derived selection or broad presentation classification look like a raw provider fact.
Provider-specific UI is allowed to be substantially richer than shared UI. TajsTokens should exploit Codex data where it is trustworthy instead of hiding useful information because another provider may not expose an equivalent.
Trustworthy current facts and inspectable history remain the foundation. Predictive, inferential, cross-signal, and presentation work is now active product scope, but it lives **above** the evidence/read-model boundary. Intelligence may consume contracted observations and read models; it must not reach backward and redefine what the sources mean.

## 6. Intelligence, forecasting, and prediction

TajsTokens should help answer not only **what Codex has done**, but also **what the current workload implies for the rest of the quota window**. Intelligence is derived, replaceable and evaluation-driven. Workload history is installation-scoped; quota calibration retains known account and regime scope rather than assigning all local work to an account.

### Product language and modelling layers

In the primary product language, **forecast/outlook means quota outlook**. Use **Usage prediction**
or **Workload prediction** for expected token activity and **Plan a workload** for user-supplied
scenarios. The delivered Forecasts tabs use these labels; the historical terminology audit
is no longer pending UI work.

Separate three questions internally, without creating separate source-specific products:

1. **Expected usage:** what local workload is likely next? Learn from native token composition,
   model, reasoning effort, cache, context, lifecycle and root/subagent activity. Historical rollouts
   can train this directly without quota readings.
2. **Quota calibration:** how did observed workload relate to observed quota movement in a
   particular account/bucket/window/regime? Learn this; do not invent a universal conversion.
3. **Quota outlook:** given the current meter/reset and expected or requested workload, what may
   remain, when might it run out, and what uncertainty matters to the user?

This decomposition does not require a mandatory three-service pipeline or a new framework.
Direct quota-pace models remain valid baselines; adding a workload predictor, residual correction
or shared latent model must improve held-out results. Expected tokens alone do not complete quota
forecasting, and quota calibration gaps must not prevent useful usage prediction.

Use the windows actually reported, not hardcoded Plus/Pro assumptions. Plan labels are optional
observed metadata. No plan is treated as effectively unlimited; an omitted bucket/window is
neither zero usage nor an unlimited entitlement.

### Quota regimes and optional normalized workload research

Historical percentages are not assumed comparable across changing accounts, buckets, window
durations, plans or provider policy eras. A reset generation is a time boundary; a **quota regime**
is a modelling cohort describing the applicable accounting conditions, not one reset. Record reported
changes; label inferred change points as derived policy, not provider facts. Missing plan/version/
identity remains missing. Never rewrite observations when a regime interpretation changes.

Regime-aware calibration is planned work. Define compatible history, boundaries, transfer restrictions
and fallback before mixing cohorts. Unknown-account history can support separately labelled historical
modelling without becoming evidence about today's account. Transfer into live account forecasts
requires an explicit, evaluated compatibility policy.

**TT is an implemented experimental Model Lab index, not a released quota unit or balance.**
A frozen local workload score can support comparisons under the same basis without proven
cross-regime conversion; adding a scalar does not solve the unknown quota conversion. Retain
original token composition/model/effort, define a reference scale, and version weights and
calibration separately. Keep full-vector quota prediction when scalar conversion loses information. Do not claim provider
compute cost, billing value, a fixed quota percentage or automatic comparability across score versions.
Weights fitted to quota are not independently validated workload costs. Compare with direct-feature
baselines before introducing a public TT label.

### Product goals

For the 5-hour and weekly quota windows, the prediction system should aim to provide:

- current burn regime and sustainable pace for the authoritative reset window;
- probability/risk of exhausting the window before reset when the data supports a calibrated probability;
- estimated exhaustion time when exhaustion before reset is plausible;
- predicted remaining quota at reset;
- meaningful uncertainty/ranges rather than a single falsely precise number;
- clear learning/stale/insufficient-evidence states;
- explanations tied to observed regime changes or workload signals without claiming causality that has not been established.

The Overview should surface the compact decision-useful version. Deeper intelligence views may expose backtest performance, model/policy versions, uncertainty diagnostics, and historical forecast-versus-outcome comparisons.

### Quota source contract and current implementation boundary

**Decision (2026-09-09): rollout `rate_limits` are legitimate provider-derived historical quota
evidence.** They do not have categorically weaker meter semantics merely because they are embedded
in JSONL. The fresh app-server **read** remains the current anchor; source origin, freshness,
account scope, bucket identity, window/reset identity and precision are separate dimensions.

At inspected upstream commit `a51608398d53b6d23ed98b8287de415b35f1eea5`:

- `codex-rs/rollout/src/policy.rs` retains `EventMsg::TokenCount` in rollout history.
- `codex-rs/app-server/src/bespoke_event_handling.rs::handle_token_count_event` directly forwards
  the event's `rate_limits` into `account/rateLimits/updated` through the protocol conversion.
- `codex-rs/core/src/state/session.rs::token_info_and_rate_limits` clones cached session quota;
  a token event timestamp is not proof of a new backend meter measurement.
- `codex-rs/app-server-protocol/src/protocol/v2/account.rs` rounds core `used_percent` from a
  float to an integer, maps `window_minutes` to `window_duration_mins`, and preserves reset time.
- `codex-rs/app-server/src/request_processors/account_processor.rs::get_account_rate_limits_response`
  fetches backend usage and returns account identity when available and a limit-ID map. A fresh
  request is not a guarantee of an instantaneous backend measurement.

This source snapshot is corroboration, not an asserted exact installed-build match. A bounded
read-only installed-data check sampled seven recent rollout files: 836 window observations and
663 consecutive repeated quota snapshots. Of 90 nearby (within 120 seconds) same-duration/reset
comparisons for the main Codex bucket, 82 percentages matched exactly; the maximum difference
was one percentage point. These were not synchronized samples and do not prove account identity,
every historical bucket's equivalence or a universal lag bound. No credentials were inspected.

Historical reconciliation must:

- Preserve original observations, source/native identity and supported precision; compare like
  buckets, windows and reset generations. Primary/secondary position is not a fixed duration,
  and similar percentages alone do not establish identity.
- Keep source event time and actual collection time distinct. Mark possibly cached repetitions;
  do not count repeated embeddings as independent meter measurements or proof of exact zero burn.
- Preserve disagreements and account ambiguity. Equivalent meter semantics do not identify an
  account, prove a rollout's causal cost, or authorize destructive merging/double counting.
- Make selection, deduplication and training eligibility explainable, versioned read-model policy.
  Allow separately labelled historical cohorts without claiming live-account attribution.
- Validate against later compatible observations from both supported paths, reporting freshness,
  precision, source coverage and account/regime limitations.

**Implemented historical contract (2026-09-09):** owned schema 10 extends `quota_snapshots`, not a
parallel source store. Retained optional fields are `observation_id`, `source_identity`, `session_id`,
`limit_id`, `plan_type`, `lane`, `collected_at_utc` and `has_source_timestamp`. They justify native
identity, replay safety, cohort separation and availability; no prompts, auth material or raw payloads
are added. `captured_at_utc` keeps its existing source-event meaning for rollouts and acquisition-time
meaning for app-server reads; the new collection field removes that ambiguity for new collection.
Source precision is retained numerically (including fractional rollout values), not normalized to an
invented meter step. Missing rollout `limit_id` uses Codex's documented main-bucket default only in
read policy; absence stays absent in storage. Plan/account absence is never backfilled from login.

The quota key now also includes observation identity. Rollouts use native source-record identity plus
lane; reads without a record ID use a content fingerprint within the source/account/time key, retaining
conflicting same-time alternatives. Exact retries do not update values or first collection time.
Migration preserves legacy rows with missing provenance; a versioned native replay adds qualified
observations rather than fabricating provenance onto old rows. Old source generations remain retained;
the forecast dataset reads only currently selected native generations. The state-index upgrade clears
only owned acceleration fingerprints/watermark so unchanged files are revisited. It does not clear
native evidence or write to Codex. Initial replay can take minutes on multi-gigabyte history.

`QuotaObservationAuthority` remains the compatibility classification for current-read safety;
historical eligibility is independently owned by `quota-history/v1`. Evaluation and burn history use
separate source/account/bucket/plan/duration cohorts and separate unknown-account rollout sessions.
Repeated embeddings are marked potentially cached and withheld as independent targets; conflicts and
invalid points break slope segments. Strict replay dates availability no earlier than actual collection,
so backfill cannot create past deployable predictions. Historical source inclusion does not relax
current-anchor safety or establish account attribution. The bounded dataset read allows up to 500,000
quota observations and fails explicitly above that bound; the ordinary history view remains bounded.

#### Current quota presentation

The 2026-09-08 installed account response after a plan change reports only the weekly window in
the main `codex` bucket (`primary.windowDurationMins=10080`, `secondary=null`). A separate Spark
bucket still has its own five-hour/weekly windows; those must not fill a missing main Codex window.
A successful response containing supported windows now marks other supported lanes as **not
reported**, independently of retained last-known-good history. This is neither a request failure
nor proof of an unlimited/disabled entitlement. Response health may be live while an omitted lane
has no current forecast. Request failures clear the omission marker and retain ordinary stale or
unavailable states; a subsequently reported window resumes automatically. No plan-specific quota
assumptions are persisted.

The quota-burn read model calculates differences only within an exact provider/profile/window/source
stream. The product view defaults to authoritative account readings, with explicit source/window
filters applied before interval limits; rollout readings remain separately inspectable. Local token
shares describe activity during an interval, not causal quota attribution or confidence probabilities.
Forecast history defaults to the latest saved sample per hour/window, with every loaded sample and
per-sample provenance still accessible. Overview is the application's startup destination.

Current forecasts must be anchored to the provider-authoritative current quota observation for the exact provider/profile/window/reset generation being forecast. Stale, non-authoritative, future-dated, or differently anchored observations may remain visible as history but must not silently become the current forecast anchor.

Historical inputs must be bounded at forecast evaluation time. No walk-forward/backtest may see later quota observations, later reset outcomes, or future workload features. Reset/re-anchor generations are separate forecasting epochs; older epochs may be used only as prior training data by a deliberately evaluated model.

**Reset timestamp jitter (2026-09-17):** forecast policies `quota-walk-forward/v4` and
`quota-workload/v3` tolerate a maximum one-second reset timestamp range within a derived
epoch, matching observed alternating app-server timestamps. The range is bounded across
the entire segment, not chained between adjacent readings. Meter drops, cohort changes,
invalid observations and crossing the earliest reset boundary still terminate the segment.
Raw provider timestamps and exact current-anchor matching are unchanged. Calibration and
evaluation group one-second reset variants so they do not count as independent generations.
On a frozen 512-reading current-account weekly sample, this recovers 4.26 hours rather than
one reading. Chronological 30-minute replay yields seven targets (previously zero): MAE
0.964 percentage points for the incumbent, 1.000 for persistence, 0.865 for epoch pace.
This small single-window sample does not justify changing models or claiming calibrated
coverage; no uncertainty bands are available. Larger timestamp changes remain boundaries.

Provider meters are quantized/limited-precision observations. A repeated percentage reading means no movement was visible at the meter's precision; it does **not** establish exact zero consumption. Forecasting methods must model or conservatively preserve that uncertainty.

### Account-local learning

**2026-09-08 scope audit:** the installed npm CLI (`codex-cli 0.153.4`), reached through the
same CLI installation used by the provider's PATH fallback, returns nonempty `accountId` on
`account/rateLimits/read`. Only field presence/names were recorded by the read-only probe, not
the identifier value or authentication material. Its response has no `userId` field. At local
upstream commit `a51608398d53b6d23ed98b8287de415b35f1eea5`,
`app-server-protocol/src/protocol/v2/account.rs::GetAccountRateLimitsResponse.account_id`
is explicitly the backend account associated with that usage snapshot. This does not prove
which account produced historical rollout work, identify a person within a shared account,
or establish that every historical `default` profile row has the same account. The upstream
checkout is corroboration, not an exact-build match for the installed CLI.

**Backend-account retention and read policy (2026-09-08):** retain a versioned SHA-256 pseudonym
of the exact nonempty response `accountId`, never the raw ID or authentication material. This
is a linkable local account key, not anonymization or a user identity. Missing, malformed,
oversized, or duplicate account fields remain unknown. The response envelope preserves scope
even when no supported quota windows are returned. No auth-file inspection is needed.

Owned schema 9 adds this scope to quota and forecast keys; intelligence component 2 adds it to
reset observations. Existing rows are preserved with unknown scope, without backfilling from
the current login. This field follows the retained quota/derived-history lifetime; no new
export or automatic deletion is introduced. The quota history reader's omitted/null account
parameter selects unknown scope, not every account. Historical explorers can still show all
streams with scope labels; read-only evaluation also supports schema-8 databases as unknown.

The current implementation uses only a fresh authoritative anchor and same-source, same-account history
no newer than that anchor. Unknown current scope has usable meter readings but a learning
forecast. Successful account changes clear omitted lanes and forecasts from another account;
failed reads retain the last successful observations as stale. Resets, burn intervals, alerts,
scenario cohorts, and chronological evaluation stay account-separated. Broad quota overlays
are omitted when independent streams cannot be represented honestly in one series.

Local rollout/token activity remains installation-scoped. Showing it beside quota movement
means co-observation, not account membership or causation. Production scenarios require a
known current quota scope and recent matching evidence; no legacy/other-account history is
borrowed to fill sparse training data. The delivered historical eligibility policy preserves
these live-anchor/account protections while allowing separately scoped supported rollout history.

There is no assumed universal conversion from tokens to subscription quota. Any relationship between quota movement and workload must be learned from the user's own observed history and remain conditional on available coverage.

Potential predictors include only already-contracted observations such as recent quota movement, time within the reset epoch, root/subagent activity, concurrency, model/reasoning-effort mix, native token/accounting categories, cache behavior, active-session duration, and other provider-native signals established later. These are candidate predictors, not causal truths.

### Predictive workload evidence audit (2026-09-08)

The live `CODEX_HOME` inventory, not just TajsTokens' database, is the acquisition baseline.
A read-only scan of 348 active/archived rollout JSONL files (about 2.26 GB) found 2,368 owned
`turn_context` records, 62,570 owned `token_count` records, 1,864 `task_started`, 1,751
`task_complete`, and 81 `turn_aborted` records spanning March through September 2026. Every
observed turn context contained string `model`, string `effort`, and `turn_id`; 40 also contained
`root_turn_id`. All owned token observations followed an effort-bearing context. Counts are a
live inventory, not stable source guarantees. Inherited prefixes (32 non-owning session metadata
records) must not be counted as the child's work. The reproducible read-only inventory is
`tools/TajsTokens.ForecastEvaluation --inventory <CODEX_HOME> <telemetry.db>`; it prints aggregate
counts and metadata coverage only, never transcript contents or source identifiers.

In contrast, the inspected TajsTokens database had 62,246 normalized token events across 342
sessions and **all reasoning-effort values were NULL**. The parser recognized `reasoning_effort`
and nested `reasoning.effort`, but not the observed native `effort` field. Its existing usage-event
projection retained lifecycle event names/timestamps but discarded native turn identities.
This is an ingestion gap, not absence of predictive history. Current state SQLite has model and
reasoning metadata for 345 of 346 threads, but these mutable current values must not be attached
retroactively to past token events. The live thread-history SQLite has 323 turns across 127
threads (319 completed/duration-bearing), beginning in August; JSONL has substantially longer
turn lifecycle coverage. SQLite is useful corroboration, not a reason to replace the longer
source-native event history with current thread snapshots.

Corroboration: local `openai/codex` commit `a51608398d53b6d23ed98b8287de415b35f1eea5`,
`codex-rs/protocol/src/protocol.rs::TurnContextItem`, explicitly persists turn context once per
real user turn and again after mid-turn compaction. It declares optional `turn_id`, optional
`root_turn_id` (root-turn attribution for subagents), `model`, and optional `effort`.
The commit is not asserted to exactly match the installed desktop. Repeated turn contexts are
observations, not additional user turns. The same protocol defines `RateLimitWindow.used_percent`
as consumed percentage, `window_minutes` as duration, and `resets_at` as Unix seconds.

Retention decision: extend the existing owned-rollout normalization path with typed, content-free
workload observations for native context/lifecycle metadata. Retain source record/file identity,
byte provenance, native session/turn/root-turn identity where present, event type, nullable source
event time, actual collection time, contract/parser version, model/effort context, and supported
context-window metadata. Native token categories continue through the existing accounting reducer.
Do not duplicate prompts, reasoning bodies, tool arguments/results, instructions, code, or auth
material. Parser replay may repair missing metadata without recounting token increments. Missing
or conflicting native values must remain inspectable rather than being filled from current state.

Features such as completed-turn duration, active-turn overlap, root/subagent mix, model/effort mix,
and recent token/context behavior are derived from these observations at a bounded evaluation
time. They are not stored as native facts. A task-start/completion interval measures observed turn
wall time, not model-compute time; open or unmatched lifecycles remain incomplete. No future
completion may retrospectively mark a historical turn inactive. Backfilled event-time replay must
be labelled reconstructed history, distinct from a strict replay using collection-time availability.
Current mutable SQLite metadata does not establish historical availability or historical effort.
The initial audit restricted quota targets to app-server observations. The 2026-09-09 quota source
contract above superseded that blanket historical exclusion. The canonical historical quota
evidence slice delivered the reconciliation/eligibility changes described under Current work.

The follow-up live scan also observed numeric `started_at` on 1,244 task starts; 1,158 completions
with `completed_at`/`duration_ms`; and 956 completions with `time_to_first_token_ms`. The inspected
protocol explicitly defines native start/end as Unix seconds and durations as milliseconds. These
optional scalars and the native session-source discriminator are retained without retaining the
terminal message/error payload. Root/subagent classification is derived from the observed native
source discriminator or explicit parent/root-turn relationship, not absence of a relationship in
today's state graph. Unknown origin stays unknown. Notification-name-only app-server logs were
also inspected: the outgoing quota notifications had no numeric quota payload and do not create
additional authoritative training labels.

Observatory schema 5 adds workload observations; schema 6 records collection time on new token
and context rows. Existing rows retain NULL collection time rather than a fabricated original
availability date. Replays preserve first collection time. `typed-v4-workload` forces safe parser
replay, and state-index schema 2 invalidates the derived fast-path fingerprints once so unchanged
historical rollouts are not skipped. The normalized store does not replace token increments during
that metadata repair. Retired source generations remain provenance but are not mixed into the
active-source forecasting feature query. No source-owned SQLite database is modified or mirrored.

### Evaluation contract

The 2026-09-08 durable backfill/evaluation snapshot contains 1,772 authoritative quota observations,
6,738 content-free workload observations, and 62,544 native token increments, all with historical
effort restored (96 have actual collection timestamps; legacy availability stays unknown).
Authoritative half-hour targets provide only 3 eligible five-hour origins across 2 reset generations
and 14 weekly origins in 1 generation. Weekly ridge candidates can fit only 2 of those origins;
the elapsed/pace ridge MAE is 0.168 points versus the incumbent's 0.163. This does not support
promoting workload sophistication. There are no weekly near-reset targets and no supported
eight-generation uncertainty calibration. The production incumbent is therefore retained with
explicit learning/conditional states, while chronological model selection and empirical bands
activate only with sufficient comparable completed generations. This dated evaluation excluded
rollout quota targets by policy, so it does not establish scarcity of quota evidence in the full
corpus. The evaluation UI and local CLI expose sample,
generation, actual-fit, and coverage counts so fallback predictions cannot masquerade as model proof.

Forecasting changes must be judged primarily by historical walk-forward/backtesting over completed observations/windows, preserving only information that would actually have been available at each historical prediction time.

Evaluation should support sensible baselines and metrics appropriate to the output: remaining-quota and remaining-at-reset error, exhaustion-before-reset classification quality, probability calibration/proper scoring when probabilities are emitted, exhaustion ETA error where legitimate, prediction-interval coverage and width, and explicit sample counts/coverage. When sample size permits, report 5-hour versus weekly and workload/model/reasoning-regime performance separately.

Model selection should prefer the simplest method whose out-of-sample performance is competitive. A fixed heuristic, robust local estimator, EWMA, regression, state-space model, ensemble, or another approach is acceptable if it wins on evidence. Complexity is not itself progress.

Confidence has to mean something. Heuristic confidence scores may be labelled as heuristic, but a UI percentage that looks probabilistic must be calibrated/validated as such. Prediction intervals should state intended coverage and be checked empirically.

### Current baseline and intended evolution

#### Live short-horizon prediction (2026-09-09)

**Native token prediction does not need quota labels.** `TokenWorkloadPredictionService` trains
directly on the rollout corpus and predicts recorded token consumption over the next 30 minutes
and 2 hours. This is installation-local, includes cached input, and is not an account-wide usage,
billing or subscription-quota conversion. A missing interval contributes zero *recorded* tokens,
not proof that no work happened elsewhere. Only origins with recent recorded activity are used;
targets must end within the observed corpus. Current forecasts pause after two hours without a
token observation. Current training reads 30 days; evaluation can inspect a wider selected range.

The `rollout-token/v1` policy compares recent 30-minute pace, two-hour pace, recent target median,
log-target ridge (penalty 10), and strongly regularized linear pace-residual ridge (penalty 100).
The workload models use prior token volume/cache/reasoning mix, native model/effort shares,
root/subagent/unknown activity, observed turns/overlap, compactions and context pressure. Fits use
the latest 120 matured intervals, minimum 20, prefix-only scaling/vocabulary, and bounded
extrapolation. Missing/unseen current model/effort falls back. Selection requires 16 held-out
comparisons and 20% lower MAE over up to 48 previous non-overlapping targets. The half-hour
fallback is recent half-hour pace; the two-hour fallback is the recent target median once eight
training targets exist. Empirical 80%-target bands require 20 earlier selected-policy errors.

Historical token learning intentionally reconstructs features at their source event times from
evidence available **now**, allowing backfilled rollouts to be useful immediately. Future source
events are never input features; outcomes must mature before subsequent training/selection.
The retrospective scores are not claimed as historically deployed app performance. This is
separate from the strict collection-time quota-policy experiment and introduces no source writes
or durable raw-content retention. Current results are rebuildable in-memory read models exposed
by the telemetry coordinator, Overview and the Forecasts token-workload tab, even without quota
rows or a known quota account.

The same private snapshot contains **350 sessions**, supporting **1,643 half-hour** and **413
two-hour** token targets. Selected-policy MAE is 4.010 million / 15.182 million recorded tokens,
versus recent-half-hour pace 4.045 million / 15.980 million; the simple two-hour target median
achieves 14.745 million. Workload regressions are not consistently superior, so they are not
forced on. Empirical band coverage is 80.7% / 77.9% (1,623 / 393 scored intervals). These errors
show substantial workload variability, not a precise forecast guarantee. This dataset informed
the model-policy choice; it is not an untouched external test set. Session/time indexing reduced
the measured full-corpus live replay from 110 seconds to 2.45 seconds without changing candidate
predictions; the normal live range is shorter. Synthetic tests cover learned workload selection,
future-event isolation, backfill usability and native-token predictions with no quota data.

`QuotaPredictionService` now supplies +30-minute, +2-hour and (weekly only) +24-hour
remaining-quota predictions alongside the existing conditional reset outlook. The live owner
persists exactly these outputs in optional `ForecastEvidence` JSON fields and Overview/history
render them. No new durable source payload, token-to-quota conversion, or account assignment is
introduced. The feature reader restricts quota by account before its row limit; live learning uses
up to 14 days of same-account/source/window observations. Missing or over-limit workload reads
retain quota-only predictions with an explicit diagnostic.

The `quota-workload/v1` policy starts with a two-hour elapsed-time-weighted rate, avoiding fixed
per-poll EWMA decay. For each horizon it compares pace candidates on matured, non-overlapping
outcomes (minimum 6, latest 48); a replacement must improve MAE by 10% and 0.05 quota points.
An account-local ridge residual correction uses token volume, cache/reasoning shares, model and
effort shares, root/subagent/unknown activity, observed turns/overlap, compactions and context
pressure. It fits the latest 120 compatible matured targets (minimum 12), with prefix-only scaling
and vocabulary and penalty 10. Missing or unseen model/effort evidence falls back. The correction
is used only after 6 non-overlapping held-out fitted predictions beat the actual adaptive pace
baseline by the same margin. These are co-observed local workload signals, not verified account
attribution or model-compute time. Live fitting uses actual collection-time availability; offline
event-time reconstruction remains separately labelled. Backfilled rollouts can support retrospective
experiments but cannot pretend to have been available to an earlier live prediction.

Short-horizon bands use absolute errors of earlier selected-policy predictions, with at least 20
non-overlapping outcomes, an empirical 80% target and a minimum 1-point resolution allowance.
This is not a calibrated exhaustion probability or a guarantee under regime change. Long reset
outlooks retain their separate completed-generation calibration; short-horizon training is not
blocked on eight completed weekly resets. Flat meters remain precision-limited, even when a
conditional point prediction equals the current reading.

The private September 8 22:00 UTC snapshot has 2,067 quota observations and 63,236 token
increments. In the historical unknown-account cohort, new-policy weekly MAE is 0.415 points at
30 minutes (19 origins, 2 generations), 1.703 at 2 hours (6 origins, 1 generation), and 4.049 at
24 hours (8 origins, 1 generation), versus legacy 0.608 / 1.194 / 11.851. Five-hour MAE is
2.205 at 30 minutes (3 origins) and 17.411 at 2 hours (1 origin), versus 1.466 / 20.284.
These sparse, mixed results do not establish universal superiority. No workload correction earns
selection on that snapshot; the newly known account has no eligible matured targets. Synthetic
known-relationship tests demonstrate earned workload selection and live/replay agreement, not
real-world accuracy. The historical unknown cohort is never attached to the current account.
Measured combined live projection time on this snapshot was 27 ms for the known weekly cohort
and 639 ms for the larger historical weekly cohort, excluding database loading.

The current production baseline is deliberately simple:

- `ForecastingService` isolates the active source/reset epoch and retains EWMA as the incumbent until comparable matured outcomes support another candidate. Model selection is chronological; empirical uncertainty requires sufficient prior reset generations. Outputs are conditional projections or explicit learning states, not heuristic confidence probabilities.
- Flat quantized histories produce `IdleWithinMeterPrecision` rather than a confident zero-burn forecast.
- `SqliteIntelligenceService.BuildAndPersistCurrentForecastsAsync` owns current forecast generation, requires a fresh provider-authoritative anchor, excludes history newer than it, persists the exact forecast shown to the app, and prevents stale/non-authoritative lanes from borrowing a competing forecast.
- `ScenarioPlannerService` uses non-overlapping, source-isolated authoritative intervals (including unchanged meters), with reconstructed workload evidence bounded at interval start. It compares an elapsed-time baseline with account-local ridge on matured chronological outcomes. Recent token-active session counts are not simultaneous compute or agent-hours. Exact requested model/effort cohorts require support; unsupported intensity scaling and extrapolation return unavailable. Uncertainty requires eight comparable held-out reset generations, never training residuals or heuristic confidence percentages. Reconstruction is not proof the app had collected those inputs at the historical origin.

These implementations are **baselines, not architecture**. Their formulas, coefficients, thresholds, confidence logic, and feature sets may be replaced when evaluation demonstrates a better production choice. Persisted forecast snapshots are derived historical outputs and should retain enough lineage to identify the anchor and policy/model used; they must remain rebuildable from durable evidence.


## Codex state SQLite inspection boundary

TajsTokens includes a read-only **Codex State DB Explorer** as an acquisition and investigation surface. It is deliberately separate from product read models: the explorer returns the selected source path, SQLite object definitions, `PRAGMA table_xinfo`/index metadata, row counts, and paged raw values without assigning domain meaning to any field.

Source: Codex private SQLite files discovered as `*.db`/`*.sqlite` under `CODEX_HOME`, `.db`/`.sqlite` files under `CODEX_HOME/sqlite`, and explicitly configured or development-local snapshot folders (the checked-out `private` folder is one such snapshot folder). Filename and generation-looking portions are discovery hints only.

Acquisition method: enumerate candidates, explicitly select one file, open with SQLite `Mode=ReadOnly` and `PRAGMA query_only=ON`, then query `sqlite_master`, `table_xinfo`/index metadata, index definitions, counts, and raw rows. A batch inspection can retain unavailable/invalid sources alongside successful inspections. The workbench can also inspect a selected pair of source instances/snapshots, compare their schema and bounded row observations, and export a schema-only view that keeps each source path and schema fingerprint separate. A source-native key trace accepts selected source paths and performs independent exact literal lookups.

Read/write posture: TajsTokens must not mutate the selected Codex file. Baseline snapshots, row fingerprints, and bounded raw row observations are held in memory only. Local inspection may display raw values from the selected source, including content-bearing fields where the user chooses to inspect them. If an export surface is provided, it must clearly distinguish what is being exported and should provide sanitization/redaction before data leaves the local boundary where appropriate; raw export, if supported, must be an explicit user choice rather than an accidental side effect. SQLite may create or refresh transient WAL shared-memory sidecars while reading a live WAL database; those are an SQLite read-path effect, not content writes by TajsTokens.

Known: schema shape, table names, column declarations, indexes, row counts, and the values returned by the read-only queries are source observations.

Observed but unexplained: table names and fields may vary with Codex versions, migrations, or runtime state. Every `sqlite_master` object (including tables, views, indexes, and triggers) is retained as raw schema metadata; table inspection also retains hidden/generated-column flags and index definitions where SQLite exposes them. Each object receives a deterministic schema fingerprint from its raw SQLite definition/columns/indexes, and each inspection receives a database-level fingerprint composed from those object fingerprints. Compact databases use in-memory row hashes and bounded raw row observations; when a source primary key is exposed, it is used only as an inspection row identity, otherwise the row hash is used and changes remain add/remove candidates. Large databases (over 64 MiB) or tables over 100,000 rows intentionally use a bounded/count-only row fingerprint so inspecting content-heavy snapshots remains responsive; comparisons explicitly report incomplete coverage and never promote a changed row into a domain event. All fingerprints and row candidates are inspection aids rather than semantic change classifications. The source-native key trace performs exact, parameterized `column = value` lookups independently in each selected matching table/view (for example, `thread_id`) and reports raw matches with source path, source description, object, and inspection capture time; it does not join tables or infer relationships.

Unknown: field meaning, lifecycle guarantees, identity semantics, and update ordering remain unknown until separately supported by captured source evidence. Discoveries in this explorer do not become normalized TajsTokens observations automatically.

## Codex source inventory and evidence baseline

The following compact inventory records the source instances observed in the checked-in `private`
snapshot set on 2026-09-02. Counts are observations from those snapshots, not guarantees about a
future Codex installation. The upstream corroboration below was inspected at Codex commit
`8e3b180d49` (`E:\dev\codex`); the installed desktop build is not asserted to be byte-for-byte
identical, so a runtime disagreement remains authoritative.

| Source family | Discovery/location observed | Snapshot schema/capabilities | Observed coverage and posture |
| --- | --- | --- | --- |
| Core/state SQLite | `state_*.sqlite` under `CODEX_HOME` and checked-in snapshot folders | `threads`, `projects`, `project_roots`, `thread_sections`, `thread_spawn_edges`, `thread_dynamic_tools`, `thread_artifacts` | 323 thread rows, 18 projects, 24 roots, 42 spawn edges, 285 dynamic-tool rows, 0 artifacts. Read-only source catalog; fields remain source-native. |
| Thread-history SQLite | `thread_history_*.sqlite` under the same roots | `thread_turns`, `thread_items`, `thread_realtime_items`, `thread_history_projection_state` | 270 turns, 41,591 normal items, 0 realtime items, 104 projection-state rows. The snapshot exposes rollout ordinals/offsets and item JSON; local inspection may display it, but it is not durably copied by this app. |
| Catalog/app SQLite | `*.db`/`*.sqlite` candidates, observed as `codex-dev.db` | `local_thread_catalog`, host/sync/checkpoint tables, `thread_timeline_ledger`, automation/inbox tables | 1,520 catalog rows, 3 hosts, 2 ledger rows, and empty automation/inbox tables. This is a catalog/index-shaped observation; no precedence over state/history is established. |
| Thread-summary SQLite | `codex-thread-summaries-dev.db` | `thread_turn_summaries` keyed by principal/host/thread | 3 summary rows. Summary meaning and authority relative to history items remain unknown. |
| Logs SQLite | `logs_*.sqlite` | `logs` with timestamp, level, target, optional thread/process IDs and indexes | 133,654 rows in the captured snapshot. Content-bearing log bodies are locally inspectable only; no durable import is implied. |
| Memory SQLite | `memories_*.sqlite` | `jobs`, `stage1_outputs` | 126 jobs and 80 stage-one output rows. The snapshot is source evidence for availability and shape; memory selection/quality semantics are not inferred. |
| Goals SQLite | `goals_*.sqlite` | `thread_goals`, `thread_goal_continuation_deferrals` | 3 goal rows and no continuation-deferral rows. Goal status values are source strings; they are not mapped to a generic task state here. |
| Queue SQLite | `queue_*.sqlite` | `queued_items`, `queued_thread_revisions` | Empty in the captured snapshot. Absence of rows is not evidence that the feature is unsupported. |
| Rollout JSONL | Files discovered under the Codex rollout/history locations | JSONL records with source-native event types and optional payloads | TajsTokens currently parses content-free session/context/token/storage metadata and keeps ownership/provenance; transcript payloads are not persisted. |
| App-server surfaces | Protocol and local app-server seams in the observed installation | Source-specific responses/events rather than a SQLite table contract | Used only where a matching source contract exists (for example quota reads). No universal app-server semantic model is claimed. |

Upstream corroboration at the recorded commit (notably `codex-rs/state/thread_history_migrations/0001_thread_history.sql`, `0005_thread_realtime_items.sql`, `state/migrations/0021_thread_spawn_edges.sql`, `0049_projects.sql`, and `app-server/src/request_processors/thread_processor.rs`) supports the following narrow interpretations: thread-history SQLite is a materialized projection of rollout JSONL with checkpoint advancement and row writes committed together; paginated turns retain first and terminal rollout ordinals/offsets; completed normal items are intended immutable; `thread_realtime_items` is a separate realtime lane; `thread_spawn_edges` are directional parent/child edges with `open`/`closed` status; projects own ordered roots and thread assignment uses `project_id`; and `history_mode` is a native metadata field whose legacy/paginated display behavior differs. These statements are corroboration, not a license to override a conflicting runtime observation. Desktop-only catalog, queue, memory, goals, and summary semantics remain runtime/schema-driven where matching upstream implementation was not found.

The first thread observability slice therefore treats state metadata, project/root rows, directional
spawn edges, dynamic-tool registrations, and history turns/items as provider-native read data. It
retains source paths and capability absence, keeps realtime rows in a separate lane, and leaves
unresolved field meaning visible rather than manufacturing cross-source relationships.
Presentation assumptions are limited to bounded catalog ordering, source-selection rationale, and
the explicit history-mode display-name policy; they are read-model policy and are not persisted as
Codex facts. Missing or older columns remain unavailable rather than being promoted to zero/false
values.

The production UI exposes this slice through the `ICodexThreadReadModel` provider-native query
boundary. Its bounded catalog can select a thread from state-backed rows (so a thread does not
need a normalized rollout session to be selectable), while SQLite discovery and source acquisition
remain behind Infrastructure. The on-demand detail view renders source-native metadata, project
roots, section and pin state, directional spawn edges, dynamic tools, turn/item lanes, realtime
items, source paths, reconciliation policy, and capability/truncation diagnostics. Every readable
state source is retained as a source-qualified observation; `CodexThreadReadModelPolicy` chooses
the presentation value and exposes alternatives and conflicts with its rationale. Content-bearing
item JSON is not rendered into the summary and is never written to TajsTokens durable storage.

The consolidated Codex browser adds a separate presentation layer over this boundary. It groups
root thread families by explicit `project_id`, falling back to the observed root `cwd` as a
clearly-labelled Workspace group and retaining an Unassigned group only when both are absent.
Directional spawn edges are rendered recursively as nested subagent thread nodes; missing parents,
cycles, conflicting statuses, and bounded results remain visible as navigation warnings. A selected
thread may render a local, on-demand conversation projection from the named preferred history
source. The projection recognizes observed Codex `ThreadItem` variants, keeps reasoning/tool/raw
JSON behind explicit expansion, and retains the complete source item and source identity beneath
every presentation row. Rotated or alternate history sources remain available as evidence rather
than being silently merged into the readable narrative. This local presentation does not add
durable transcript, reasoning, command output, or tool payload storage, and it does not change the
explicit export/sanitization boundary.

## Auxiliary Codex source observability

The auxiliary Codex source read model is intentionally provider-native and read-only. It discovers
the current `logs_*`, `memories_*`, `goals_*`, `queue_*`, `state_*`, `codex-dev.db`, and
`codex-thread-summaries-dev.db` instances through the same explorer boundary used for raw SQLite
inspection. Each source result retains its selected path and discovery provenance, capture time,
schema fingerprint, supported table names, the latest `_sqlx_migrations.version` when exposed, and
the corroborating upstream reference commit (`8e3b180d49`). The installed source
remains authoritative; the commit is not treated as a version guarantee.

The model currently exposes bounded (250-row) source-native log reads from the observed `logs`
table, plus bounded metadata reads for the observed `jobs` and
`stage1_outputs` memory tables, `thread_goals` and continuation-deferral rows, `queued_items` and
`queued_thread_revisions`, `thread_artifacts`, `local_thread_catalog`, and
`thread_turn_summaries`. Native status, revision, ordering, nullable fields, and source identities
remain source-shaped. Missing expected tables are reported as unsupported; a present table with no
rows is reported as empty; a missing file is unavailable; and open/query failures are errors.
Content-bearing memory, goal, queue, artifact, catalog, and summary values are never written to the
TajsTokens database by this surface. The UI shows metadata and content-presence flags while raw
values remain available only through deliberate local inspection.

The logs view preserves the source-native `id`, `ts`, `ts_nanos`, `level`, `target`, optional
`module_path`, `file`, `line`, `thread_id`, `process_uuid`, and `estimated_bytes` fields. It applies level, target,
time-range, and available optional-column filters in SQLite before returning a bounded page ordered
by timestamp and ID. Log body text (`feedback_log_body` in the current schema, or legacy `message`)
is not selected by default; the user must explicitly enable local body inspection, and the body is
never written to TajsTokens durable storage. A missing optional column remains unavailable rather
than being inferred from another field; in particular, `target` is not relabelled as a category.

Artifacts remain an explicit empty/unsupported capability when no rows are observed. No rows are
manufactured and no artifact lifecycle or relationship is inferred from the table name alone.
Desktop catalog and thread-summary rows are displayed as observations from their own source; they
are not reconciled with core state or rollout history merely because thread IDs happen to match.

The inspected upstream `state` model corroborates only the narrow artifact shape: a server-assigned
UUIDv7 `id`, owning `thread_id`, client-defined `artifact_type` and `identity_key`, bounded JSON
`payload`, and integer Unix-seconds `created_at`, with uniqueness per thread/type/key and an
attach-existing outcome that leaves payload and creation time unchanged. The current checked-in
runtime snapshot has zero `thread_artifacts` rows, so no local lifecycle transition is claimed.
The inspected app-server protocol at that commit exposes no supported artifact attach/list workflow
for reproducing a row; this implementation therefore performs no database writes or synthetic
fixture insertion and reports the runtime capability as empty/unsupported until a normal product
workflow produces evidence.

Thread catalog search exposes a bounded presentation list plus every matching source observation.
Its named reconciliation policy prefers newest recency, then updated/created time, source
generation, source write time, and source path; the selected entry retains its alternatives and
rationale. Thread-history reads use a separate named union policy: every readable history store is
retained in a source bundle and in source-qualified flat lanes, ordered by generation, write time,
description, and path without one store overriding another. Candidate generation parsing covers
both `state_*` and `thread_history_*` filenames, but generation remains discovery provenance only.
All bounded source reads request one extra row and expose per-category truncation flags/warnings;
absence of a capability remains distinct from an empty result and from an unreadable source.

### Auxiliary source selection and compatibility policy

The auxiliary product UI now consumes `ICodexNativeSourcesReadModel`, not the SQLite explorer
implementation. Requests may explicitly select a discovered database independently for each
Codex source family. Every snapshot exposes all discovered alternatives and the named
`codex-native-source-selection/v1` policy: Codex-home before sqlite-folder before snapshots,
then descending filename generation and source write time, with ordinal path as a deterministic
tie-breaker. This is discovery preference, not semantic authority or a version guarantee.
Sources are not merged. An explicit choice that disappears is unavailable; an unreadable choice
is an error, never a silent fallback to a different database. Selection is inspection-session
state only. Returned log queries retain the actual database path so paging does not switch
instances when another generation appears; offset paging is still a live read, not a stable
transactional snapshot across pages.

Auxiliary scalar metadata now retains unknown instead of substituting zero, false or Unix epoch
for absent, null or undecodable values. Presence flags distinguish an absent column (unknown)
from a present column containing NULL (false). Only source boolean encodings 0 and 1 decode as
false and true; other values remain unknown. These are conservative decoding rules, not claims
about new native state semantics. Missing table/column capabilities and omitted undecodable
identity rows are reported with source-qualified warnings. A source containing only rejected
rows is not relabelled empty. Raw values remain available for deliberate local inspection.
Queue revision observations for the loaded item threads are retained rather than reduced to
the maximum revision; a conflicting set leaves the item's revision unknown. A missing goal
deferral capability is unknown, not evidence of no deferral. This changes only the on-demand
read model: no schema migration, durable content collection or export is introduced.

The source UI preserves the chosen family and applied log filters through refresh. Its general
text filter is explicitly limited to loaded rows; it is not a complete database search. Broader
auxiliary pagination/thread-scoped querying and snapshot-consistent log paging remain unfinished.

## Codex CLI harness boundary

TajsTokens also includes a user-invoked **Codex CLI Harness** for running a custom local Codex
CLI alongside the observability surfaces. The command/executable, working directory, one-argument-
per-line argument list, prompt transport, and timeout are explicit runtime settings. The harness
passes arguments as process values rather than accepting a shell script, and it does not run during
ordinary telemetry collection. A prompt may be sent on standard input or as one final argument;
TajsTokens makes no claim that custom CLIs share a universal protocol.

Harness stdout, stderr, prompt text, and exit metadata are local session inspection only. They are
not automatically normalized or copied into the TajsTokens durable evidence store, and there is no
automatic export or upload path. The harness is therefore an execution aid, not a new source
contract or a replacement for the read-only Codex source boundaries above.

## Documentation rule

Keep this document authoritative for current product goals, architecture, source semantics,
retention, implementation status, next work and acceptance. Keep AGENTS.md stable and focused on
contributor workflow; move feature-specific policy and changing implementation detail here instead.
The contributor guide owns operational procedures. Update README.md only for actual user-facing
feature/behavior, requirements or setup changes; exclude roadmaps, internal thresholds, audit
counters and unimplemented concepts such as TT. A checklist or chat proposal is not source evidence.

### Recorded-token prediction contract

Owned schema 12 adds a SHA-256 fingerprint of consumed rollout bytes to ingestion checkpoints.
Before resuming a changed file, ingestion verifies its consumed prefix, including in-place workspace
relinks that preserve the filesystem identity. A mismatch (or legacy unverified checkpoint) starts
a replacement generation; the existing batch writer retires old token/counter, workload, quota,
context and usage projections for that file before replay. Source files remain read-only. Verification
reads the consumed prefix once per changed-file ingestion pass (linear I/O); checkpoint hashing is
incremental within the pass. A process-local cache skips rehashing when identity, checkpoint, size
and write time are unchanged. This is not an atomic snapshot of a concurrently rewritten external file. Empty
replacement files cannot activate a new parsed generation until a complete record arrives.

Token forecasts target the sum of recorded native token increments (including cached input) in
the next interval, conditional on recent installation-local activity. Zero means no tokens were
recorded, not proof of no account activity. This is neither whole-account accounting nor a
subscription-quota or price conversion. Chronological held-out targets exclude future tokens,
completions, and model changes from inputs. Backfilled history supports retrospective learning,
not claims that a past app had collected it. A retained result after refresh failure is explicitly
stale and retains its original generation time.

Owned schema 11 corrects the timestamp-provenance flag on legacy rows migrated by schema 10;
legacy evidence is retained but cannot silently become eligible account-local training history.
Unknown/malformed/conflicting observations terminate usable pace segments. Overlapping workload
outcomes cannot satisfy the independent training threshold. Flat-meter point estimates are not
exact zero consumption; all-flat production epochs remain precision-limited with no survival claim.
The statistical replacement of point-estimate baselines with a censored-likelihood model remains
research, not an implemented or calibrated model.


Do not introduce additional competing roadmaps or architecture specifications without a concrete need. Mark dated observations and superseded decisions explicitly; do not silently reinterpret older audit counts as current state.

Historical documents may be consulted for implementation archaeology, but any useful claim from them must be re-established and deliberately incorporated here before becoming current architecture again.
