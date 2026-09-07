# TajsTokens foundation

> **Status: authoritative working document.**

## Why this reset exists

TajsTokens reached a point where implementation, UI, and derived metrics moved faster than our understanding of the underlying data. A polished result is not useful if the source semantics are uncertain.
The reset therefore starts from evidence. We will study each source empirically, record what it actually exposes, preserve uncertainty, and only then define normalized data and read models.
If current code conflicts with this document or with newly established source evidence, that conflict is a finding. We do not bend the evidence to preserve an existing implementation.

## Product direction: Codex-first observability

TajsTokens is currently a **Codex-first observability and intelligence application**. The immediate goal is not broad provider support, a generic AI telemetry platform, or a lowest-common-denominator usage dashboard. The goal is to make Codex richly inspectable from trustworthy local evidence and, where that evidence is strong enough, turn it into useful account-local predictions about quota usage and near-future behavior.
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
Codex sources
    -> Codex source contracts
    -> Codex-native normalized observations
    -> durable evidence
    -> rich Codex read models
                 \
                  -> optional shared observability projections
    -> Codex-specific UI

Future provider sources
    -> provider-specific source contracts
    -> provider-native normalized observations
    -> durable evidence
    -> provider-native read models
    -> provider-specific UI
                 \
                  -> compatible shared projections where semantics genuinely overlap
```

There is deliberately no universal provider schema in the middle of this pipeline.

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
- **Existing implementation has no grandfathered semantic authority.** Useful plumbing may survive; claims must earn their way back through source evidence.
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

TajsTokens should help answer not only **what Codex has done**, but also **what the current workload implies for the rest of the quota window**. This layer is explicitly derived, account-local, replaceable, and evaluation-driven.

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

### Evidence and authority boundary

Current forecasts must be anchored to the provider-authoritative current quota observation for the exact provider/profile/window/reset generation being forecast. Stale, non-authoritative, future-dated, or differently anchored observations may remain visible as history but must not silently become the current forecast anchor.

Historical inputs must be bounded at forecast evaluation time. No walk-forward/backtest may see later quota observations, later reset outcomes, or future workload features. Reset/re-anchor generations are separate forecasting epochs; older epochs may be used only as prior training data by a deliberately evaluated model.

Provider meters are quantized/limited-precision observations. A repeated percentage reading means no movement was visible at the meter's precision; it does **not** establish exact zero consumption. Forecasting methods must model or conservatively preserve that uncertainty.

### Account-local learning

There is no assumed universal conversion from tokens to subscription quota. Any relationship between quota movement and workload must be learned from the user's own observed history and remain conditional on available coverage.

Potential predictors include only already-contracted observations such as recent quota movement, time within the reset epoch, root/subagent activity, concurrency, model/reasoning-effort mix, native token/accounting categories, cache behavior, active-session duration, and other provider-native signals established later. These are candidate predictors, not causal truths.

### Evaluation contract

Forecasting changes must be judged primarily by historical walk-forward/backtesting over completed observations/windows, preserving only information that would actually have been available at each historical prediction time.

Evaluation should support sensible baselines and metrics appropriate to the output: remaining-quota and remaining-at-reset error, exhaustion-before-reset classification quality, probability calibration/proper scoring when probabilities are emitted, exhaustion ETA error where legitimate, prediction-interval coverage and width, and explicit sample counts/coverage. When sample size permits, report 5-hour versus weekly and workload/model/reasoning-regime performance separately.

Model selection should prefer the simplest method whose out-of-sample performance is competitive. A fixed heuristic, robust local estimator, EWMA, regression, state-space model, ensemble, or another approach is acceptable if it wins on evidence. Complexity is not itself progress.

Confidence has to mean something. Heuristic confidence scores may be labelled as heuristic, but a UI percentage that looks probabilistic must be calibrated/validated as such. Prediction intervals should state intended coverage and be checked empirically.

### Current baseline and intended evolution

The current production baseline is deliberately simple:

- `ForecastingService` isolates the active reset epoch, derives observed quota-percent-per-hour intervals, uses a chronological EWMA with different fixed alphas for 5-hour and weekly windows, estimates sustainable pace/reset survival, and applies heuristic confidence.
- Flat quantized histories produce `IdleWithinMeterPrecision` rather than a confident zero-burn forecast.
- `SqliteIntelligenceService.BuildAndPersistCurrentForecastsAsync` owns current forecast generation, requires a fresh provider-authoritative anchor, excludes history newer than it, persists the exact forecast shown to the app, and prevents stale/non-authoritative lanes from borrowing a competing forecast.
- `ScenarioPlannerService` currently fits a small account-local ridge model over quota-drop intervals using duration, root-agent-hours, and subagent-hours, with optional model/reasoning cohort selection and residual-derived uncertainty.

These implementations are **baselines, not architecture**. Their formulas, coefficients, thresholds, confidence logic, and feature sets may be replaced when evaluation demonstrates a better production choice. Persisted forecast snapshots are derived historical outputs and should retain enough lineage to identify the anchor and policy/model used; they must remain rebuildable from durable evidence.

## Current work

There is deliberately no phase roadmap during the foundation reset. Work is selected by product value and evidence readiness: establish or strengthen source contracts where semantics remain uncertain, then promote trustworthy observations into useful read models and intelligence.

The current product focus is **rich Codex observability plus trustworthy account-local intelligence**. The major local Codex source families are already inspectable through raw and provider-native surfaces, and the application has working quota, token-accounting, thread/workspace/subagent, auxiliary-source, forecasting, and intelligence paths. Those existing paths are implementation evidence, not proof that every semantic or algorithm is finished.

Current work should therefore favor:

- closing concrete source-contract or compatibility gaps that still make product claims unsafe;
- making normal Codex workflows useful through provider-native views rather than requiring raw SQLite/JSONL inspection;
- preserving source selection, provenance, missing/ambiguous/conflicting evidence, and source evolution explicitly;
- improving durable evidence/read-model boundaries where current code still duplicates policy or stores derived assumptions as facts;
- developing quota forecasting and scenario intelligence through leakage-safe walk-forward evaluation, calibrated uncertainty, and account-local evidence;
- using workload signals only when their semantics are established and they improve measured out-of-sample prediction;
- improving Overview/intelligence UX so predictions are actionable without overstating certainty;
- maintaining strong tests, bounded queries, cancellation/concurrency behavior, and Windows build quality.

Possible future providers should be kept in mind through clean provider/source boundaries. We are not implementing hypothetical providers now, and we are not weakening the Codex model to make imaginary future mappings easier.

Quota remains one Codex-observability domain among many rather than the organizing principle of the architecture. It is currently a particularly valuable intelligence domain because it has an authoritative live meter, reset epochs, historical observations, and related workload evidence that make prediction measurable rather than purely speculative.

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

Do not create a competing roadmap, architecture guide, phase document, or semantic specification while this reset is active. Update this file instead.

Historical documents may be consulted for implementation archaeology, but any useful claim from them must be re-established and deliberately incorporated here before becoming current architecture again.
