# Architecture and data-flow audit — 2026-09-01

Tracked by #70. This audit describes the merged application on `main` after the native-Codex accounting cutover and before Phase 5 feature work.

The purpose is not to rename layers. It is to make every displayed number answer five questions without reading implementation folklore:

1. What raw source is authoritative for this fact?
2. What normalization semantics turn that source into a stored fact?
3. Which read model owns the displayed aggregate?
4. What exact scope/coverage and generation does the number represent?
5. What makes the number live, stale, degraded, unavailable, or uncertain?

## Executive conclusion

The raw-source and ingestion architecture is broadly sound:

- Codex app-server is the authority for current subscription quota;
- Codex private state SQLite is an optional read-only index for discovery/metadata/topology;
- rollout JSONL is authoritative for local event-level token/context/activity evidence;
- TajsTokens owns normalized durable SQLite facts and exact byte checkpoints;
- Tokscale is now optional reconciliation/fallback rather than the default collector.

The main architectural debt is **above the normalized-fact layer**. Overview, Usage, Observatory, Forecasts, and Analytics increasingly derive related numbers through independent SQL/service paths with different freshness, attribution, and snapshot semantics. The result is not one broken subsystem; it is several individually reasonable subsystems that can disagree while all believe they are correct.

Before Phase 5, correctness work should therefore converge read semantics around canonical accounting/quota generations rather than add another analytics surface.

## Current data flow

```text
RAW SOURCES

Codex app-server ─────────────────────────────── current provider-authoritative quota

Codex state_*.sqlite ── changed-thread index / topology / metadata
         │
         v
Codex rollout JSONL ─── event-level local telemetry
         │
         v
CodexRolloutParser
         │
         ├── cumulative token observations
         ├── embedded quota observations
         ├── session / agent / topology metadata
         ├── context / compaction observations
         └── activity / rollout-storage metadata
         │
         v
TajsTokens SQLite normalized facts
         │
         ├── SqliteNativeCodexAccountingProvider ──> TelemetryCoordinator ──> Overview
         │
         ├── SqliteCodexObservatoryReadModel ───────────────────────────────> Observatory
         │
         └── SqliteIntelligenceService
                ├── usage history / dimensions / heatmap ───────────────────> Usage
                ├── quota burn / attribution ───────────────────────────────> Analytics
                ├── forecast history ────────────────────────────────────────> Forecasts
                └── scenario history ────────────────────────────────────────> Forecasts

Tokscale CLI ── optional reconciliation / fallback ──> NativeFirstCodexAccountingProvider
```

`TelemetryCoordinator` serializes source refreshes. It first gets app-server quota, persists it, performs incremental Observatory ingestion, then obtains the native accounting projection so a newly committed rollout turn can appear in the same final snapshot. Historical intelligence is queued after the final telemetry snapshot and runs independently.

## Metric registry / source-of-truth matrix

| User-visible metric | Raw authority | Normalized/stored fact | Current read path | Coverage / caveat |
|---|---|---|---|---|
| Current 5h remaining/reset | Codex app-server | current `QuotaSnapshot`; also persisted to `quota_snapshots` | `TelemetryCoordinator` → `OverviewViewModel` | provider-authoritative current response; current freshness is incorrectly represented by one global quota boolean |
| Current weekly remaining/reset | Codex app-server | same | same | same |
| Historical quota movement | app-server observations plus rollout-embedded rate limits | `quota_snapshots` | `SqliteIntelligenceService` | mixed-source observations currently share provider/profile/kind with no structured authority tier |
| Overview lifetime/local token total | rollout token events | `codex_native_token_events` disjoint deltas + `reported_total_tokens` delta | `SqliteNativeCodexAccountingProvider` → `TelemetrySnapshot` | local normalized history only; native/Tokscale semantic parity is not yet certified |
| Overview token classes | rollout token events | same | same | reported and disjoint totals can diverge without a first-class integrity state |
| Overview model totals | rollout token event model, with mutable `agents.model` fallback for null event model | same | native provider | fallback can retroactively attribute an old unknown-model event to the session's later/current model |
| Overview hourly totals | rollout token events | same | native provider | same native generation as Overview model totals because both queries share one read transaction |
| Usage range total/classes | rollout token events | same | `SqliteIntelligenceService.LoadUsageHistoryAsync` | independent read implementation from Overview; direct SQLite generation |
| Usage model breakdown | rollout token events | same | `SqliteIntelligenceService.LoadDimensionsAsync` | null event model becomes `unknown`; this disagrees with Overview's agent-model fallback |
| Usage repository breakdown | rollout session metadata (`session_meta.cwd`/repository), sanitized to a label | `sessions.repository` + native token events | intelligence dimension query | **session-scoped repository label**, not per-token-event cwd; should be named/documented accordingly |
| Usage root/subagent split | current normalized topology | `agent_relationships` + token events | intelligence history/dimension query | historical event role is classified by current session topology, which is appropriate for stable session role but can change after topology repair |
| Usage heatmap | rollout token timestamps | native token events | intelligence heatmap query | UTC weekday/hour; direct SQLite generation |
| Observatory session token total/classes | rollout token events | native token events | `SqliteCodexObservatoryReadModel` | session-scoped; exposes both reported and disjoint totals in token detail |
| Observatory current/session model label | rollout/session state | `agents.model` | Observatory read model | effectively latest known session/agent model, not necessarily the model for every historical token |
| Context peak / compactions | rollout token/context events | `context_observations` | Observatory + Intelligence | local only |
| Current Overview forecast | app-server current card + mixed-source persisted quota history | `quota_snapshots`; forecast calculated in `OverviewViewModel` | repository → `ForecastingService` | separate calculation owner from persisted intelligence forecast; card's current sample and forecast's latest history row can differ |
| Forecast history | historical quota observations | `forecast_snapshots` | `SqliteIntelligenceService` → Forecasts | generated asynchronously after telemetry refresh by a second forecast invocation |
| Quota-burn intervals | historical quota observations | derived from `quota_snapshots` | intelligence query | only positive adjacent changes within same provider/profile/window/reset identity |
| Contributor attribution | provider burn interval + local native activity | derived on query | Analytics | explicitly estimated, not provider billing truth |
| Scenario estimates | observed quota-burn intervals / concurrency cohorts | derived on query | Forecasts | account-local inference; not token→quota conversion |
| Tokscale totals | Tokscale's independent local parser | not canonical persisted facts | optional reconciliation/fallback | local corpus oracle only; not remote-inclusive |

## Findings

### A1 — P1: accounting has multiple independent read models with different semantics

Overview uses `ICodexTokenAccountingProvider` and currently receives a `CodexTokenAccountingSnapshot` from `SqliteNativeCodexAccountingProvider`. Usage and Analytics bypass that contract and aggregate `codex_native_token_events` independently in `SqliteIntelligenceService`. Observatory has a third read model over the same facts.

This is not inherently wrong for query shape/performance, but there is no shared semantic projection contract. Existing differences prove the risk is already real.

#### Concrete mismatch: model attribution

Native Overview model totals group by:

```sql
COALESCE(NULLIF(e.model, ''), NULLIF(a.model, ''), '(unknown)')
```

Usage model dimensions group by:

```sql
COALESCE(NULLIF(e.model, ''), 'unknown')
```

The Overview fallback is also semantically unsafe: `agents.model` is mutable/latest session state. A historical event whose immutable event model is unknown can therefore be retroactively assigned to a later model when the session's agent row changes.

**Decision:** immutable event attribution wins. A token event should use the model/reasoning captured for that event. Missing event attribution stays unknown unless it can be deterministically reconstructed/backfilled from rollout ordering. Do not infer an immutable historical event's model from mutable latest-agent state.

### A2 — P1: `TokenBreakdown.Total` hides reported-vs-disjoint integrity

`TokenBreakdown` owns disjoint classes, but `Total` is:

```text
ReportedTotal ?? ComputedTotal
```

The native counter writer independently computes component deltas and `total_tokens` delta. Under healthy Codex counters these should agree, but reset/regression/parser edge cases can make them differ. Overview and Usage normally display the reported total while also displaying disjoint classes, so category sums can disagree with Total without any integrity signal.

Observatory already has the better internal shape (`ReportedTotal` plus `DisjointTotal`).

**Decision:** retain both values explicitly in canonical accounting summaries and calculate an integrity delta/status. UI can still choose reported total as the headline while making non-zero discrepancies diagnosable. Never silently redefine historical totals merely to force parity.

### A3 — P1: native accounting cutover happened before semantic parity certification

The native rollout parser currently constructs accounting observations from `info.total_token_usage`. `last_token_usage` is read for context utilization, not primary accounting deltas. The counter store treats a chronological decrease in any cumulative component as a new epoch and otherwise contributes positive cumulative deltas.

Tokscale has mature semantics around `last_token_usage`, stale cumulative regressions, fork/replay boundaries, model-less rows, and pending model/turn state. The native path is now the default operational source, but #62's parity gate remains real work.

**Decision:** do not restore mandatory Tokscale. Label the current state as native-first/local with parity status `unverified`, `exact`, or `divergent`. Use measured reconciliation/fixtures to decide which Tokscale semantics need porting.

### A4 — P1: quota freshness is global instead of per lane

`TelemetrySnapshot` has one `QuotaDataFresh` boolean. `TelemetryCoordinator` considers quota globally fresh only when both supported five-hour and weekly windows are present. If the app-server returns one fresh supported lane and omits the other, the fresh lane is merged with last-known-good data but **the entire snapshot has `QuotaDataFresh = false`**.

Consequences:

- Overview passes the same global freshness value to both quota cards, so a genuinely fresh 5h lane can be shown as stale because weekly is missing;
- forecasting is paused for both cards;
- `QuotaAlertEngine` suppresses **all quota alerts** whenever the global boolean is false, including alerts for a lane that was freshly observed.

**Decision:** current quota state must be lane-scoped (`provider/profile/kind`). Each lane needs its own freshness/as-of/authority state. An overall health summary may be derived from lane states, never the other way around.

### A5 — P1: “refresh completed without errors” is confused with “source coverage is available”

For Observatory, freshness is currently essentially:

```text
observatory.Errors == 0
```

A refresh that discovers zero rollout sources and reports zero errors is therefore marked Live. Native accounting can then be promoted as fresh from the existing SQLite projection even though the source corpus is no longer discoverable.

**Decision:** distinguish at least:

- collection succeeded and source scope is present;
- collection succeeded but source scope is empty/missing;
- collection partially failed;
- collection failed;
- normalized historical data is still available.

Historical data availability is not the same property as current source freshness.

### A6 — P1: historical intelligence queries are not generation-consistent

`SqliteIntelligenceService.QueryAsync` opens one SQLite connection but does not begin a read transaction. It sequentially queries usage history, dimensions, heatmap, burn intervals, resets, and forecast history. A writer commit between statements can therefore produce a single `IntelligenceDashboard` whose sections describe different database generations.

The native accounting provider explicitly fixed this problem for model/hour projections by placing both under one read transaction. Intelligence should follow the same rule.

**Decision:** one dashboard/query response should either share one read transaction/snapshot or explicitly carry independent generation stamps if sections are intentionally eventually consistent. Prefer one read transaction for the existing bounded dashboard query.

### A7 — P1: forecasting has two calculation owners

Overview:

1. takes the current app-server `QuotaSnapshot` from `TelemetrySnapshot`;
2. separately loads up to 96 recent persisted quota observations;
3. calls `ForecastingService.BuildForecast()` in `OverviewViewModel`.

Historical intelligence separately:

1. loads up to 512 observations per provider/profile/kind;
2. calls the same forecasting service;
3. persists a `forecast_snapshots` row asynchronously after final telemetry publication.

This creates two legitimate but potentially different forecasts because sample horizon and evaluation time differ. The Overview card can also display one current app-server quota sample while its forecast selects a later rollout-embedded history observation as the `latest` sample.

**Decision:** forecast calculation belongs outside the UI. Build one canonical current forecast from an explicit current-lane observation plus a defined history policy, then persist/display that same forecast generation. Forecast history is persistence of the current-forecast result, not a second independent calculation.

### A8 — P2: historical quota mixes authority levels without a structured source policy

`quota_snapshots` contains both:

- provider-authoritative app-server observations;
- rollout-embedded rate-limit observations.

Both use the same `provider/profile/kind` identity and differ only in free-form `source`. Forecasting and most historical grouping treat them as one chronological stream. Quota-burn confidence notices a source change, but there is no first-class authority tier or dedup/reconciliation rule.

The mixed stream is useful: rollout observations provide high-frequency evidence during work. The problem is not storing both; it is making algorithms infer authority from strings.

**Decision:** define structured quota provenance/authority (`AuthoritativeCurrent`, `EmbeddedObservation`, later other supported sources). Current quota selection must prefer authoritative provider reads. Historical analytics may merge compatible observations using an explicit dedup/conflict policy.

### A9 — P2: structured accounting provenance is flattened before UI consumers receive it

`CodexTokenAccountingSnapshot` has coverage, reconciliation, fallback, source and diagnostic data. `TelemetryCoordinator` turns much of this into a `ProviderHealthSnapshot.Detail` string and publishes only token arrays plus one global `TokenDataFresh` boolean in `TelemetrySnapshot`.

Consequences:

- Overview cannot render structured coverage/parity/integrity states;
- future Diagnostics would need to parse prose or re-run providers;
- fallback quality, source freshness, local coverage and reconciliation are conflated.

**Decision:** publish a structured accounting generation/envelope in the process snapshot. Provider-health prose should be a presentation of structured state, not its only durable representation.

### A10 — P2: freshness, authority, fallback quality and coverage are overloaded into booleans

`TokenDataFresh` is false for a successful Tokscale fallback even if Tokscale just parsed current local files; that state is better described as **fresh but degraded/non-primary**, not stale. Reconciliation mismatch does not make data stale either; it makes parity divergent. Local-only coverage also does not make data stale; it makes coverage partial relative to account-global usage.

Similarly, quota global freshness currently conflates two lane states.

**Decision:** do not grow more booleans. Domain envelopes should carry independent dimensions:

```text
Freshness: Live | Stale | Unavailable
Quality: Primary | DegradedFallback
Coverage: LocalHistory | RemoteInclusive | PartialUnknown
Parity: NotChecked | Exact | Divergent | CheckFailed
Authority: ProviderAuthoritative | NativeNormalized | EmbeddedObservation | ExternalReference
AsOf / Generation
```

Names can be refined, but these concepts must remain orthogonal.

### A11 — P2: UI “Refresh” has inconsistent semantics

- Overview Refresh calls `TelemetryCoordinator.RefreshAsync`, performing source/provider refresh + ingestion + projection.
- Usage Refresh re-runs `Intelligence.QueryAsync` against local SQLite.
- Forecasts Refresh re-runs local history query.
- Analytics Refresh re-runs local history query.

A user cannot tell whether Refresh means “collect new data” or “re-query the database.”

**Decision:** distinguish the two operations. Recommended UX contract:

- page load/filter/range changes: query current local read model only;
- explicit global/source Refresh: run `TelemetryCoordinator`, then re-query the current page;
- if a page offers only local re-query, label it `Reload`/`Re-query` rather than implying provider collection.

### A12 — P2: repository breakdown is session-scoped, not event-scoped

The rollout parser establishes `Repository` from owning `session_meta` (`cwd`/repository), stores a privacy-safe repository label on the session, and token events do not carry repository identity. Usage then joins every token event in a session to `sessions.repository`.

This is a valid and privacy-friendly approximation for sessions rooted in one project, but the UI currently presents it simply as `Repository`, which can be read as exact per-event attribution.

**Decision:** document it as **session repository/workspace attribution**. Only introduce per-event repository attribution if Codex exposes a safe stable event-level identity and there is a product need.

### A13 — P2: legacy generic telemetry persistence is now dead/ambiguous ownership

The base telemetry schema and `ITelemetryRepository` still expose:

- `token_usage` / `AddTokenUsageAsync`;
- generic session/agent/activity mutation methods duplicating Observatory-owned mutation paths.

Code search finds `AddTokenUsageAsync` and several generic mutations only in the interface/repository implementation, not the current production ingestion path. The native event ledger has become the canonical Codex token fact store.

Leaving unused alternate writers makes future agents reasonably assume both stores are valid authorities.

**Decision:** after compatibility confirmation, remove/deprecate dead generic mutation contracts and eventually migrate/drop truly unused tables through an explicit schema migration. Do not delete historical rows blindly until real DB contents and upgrade paths are checked.

### A14 — P2: native accounting revision schema has split ownership

`SqliteNativeCodexAccountingProvider`, a read projection, creates `codex_native_accounting_revision` on demand. The semantic batch writer also knows the table and increments it.

**Decision:** the writer/schema owner should create and advance projection revisions. The read provider should validate/read the revision, not bootstrap writer-support schema. Component migration ownership should remain explicit.

### A15 — P2: architecture documentation is stale after the cutover

Current `AGENTS.md` still describes Tokscale as the default broad accounting source and native accounting as shadow/reconciliation mode, despite current composition making native Codex accounting the default and Tokscale opt-in. Parts of Phase 3 documentation likewise describe pre-cutover runtime composition.

**Decision:** update repository architecture guidance as part of this audit PR. Historical phase docs can remain historical if clearly labelled; the root architecture source of truth must describe current behavior.

## Canonical semantics proposed by the audit

### Token event fact

The canonical durable local Codex token fact remains one normalized event derived from rollout evidence:

```text
source event identity
session/agent identity
observed UTC timestamp
immutable event model/reasoning attribution when known
counter epoch
uncached input
cache read
cache write
non-reasoning output
reasoning output
reported total delta
computed disjoint total
```

Rules:

- local rollout event evidence is authoritative for these local event facts;
- missing event model/reasoning remains unknown unless deterministic replay context reconstructs it;
- mutable session/agent “latest model” is a presentation property, not historical event attribution;
- reported and computed totals are both retained;
- inherited child history must never become newly generated child usage;
- reset/regression/fork semantics remain subject to #62 reconciliation evidence.

### Accounting generation

Introduce one domain read-model envelope conceptually equivalent to:

```text
AccountingGeneration
  revision/generation
  asOfUtc
  source
  authority
  freshness
  quality
  coverage
  parity
  totals (reported + computed + integrity delta)
  model summaries
  hourly summaries
```

Range/repository/role queries may remain separate SQL shapes for efficiency, but they must use the **same attribution/total rules** and expose the database revision/generation they were read from.

### Quota lane state

Replace one global quota-freshness concept with lane state:

```text
QuotaLaneState
  provider/profile/kind
  current observation
  authority
  freshness
  asOfUtc
  reset identity
  current forecast
```

A combined UI status can summarize multiple lanes, but alerts and card forecasting operate on the selected lane's state.

### Quota history provenance

Retain all useful supported observations but classify source authority structurally. Current-lane selection prefers app-server authoritative reads. Historical algorithms define explicitly whether/how embedded observations participate.

### Forecast ownership

One current-forecast service owns calculation policy. Its result is:

1. displayed on Overview;
2. persisted as forecast history;
3. later queried by Forecasts.

The UI never independently reimplements current forecast assembly.

### Read consistency

A logical read model returned as one object should describe one committed SQLite generation. Use one read transaction for multi-query dashboards unless there is a deliberate reason to expose eventually-consistent sections.

## Target architecture

```text
RAW / PROVIDER SOURCES
────────────────────────────────────────────────────────────
Codex app-server        current provider-authoritative quota
Codex state SQLite      discovery / metadata / topology index
Codex rollout JSONL     local event-level telemetry
Tokscale                optional audit oracle / degraded fallback

                         ↓

NORMALIZED FACT STORE
────────────────────────────────────────────────────────────
quota observations + structured provenance
native token events + integrity data
sessions / agents / topology
context / compactions
activity
rollout/checkpoint metadata

                         ↓

CANONICAL DOMAIN READ MODELS
────────────────────────────────────────────────────────────
QuotaReadModel
  lane-scoped current quota + source/freshness
  history
  canonical current forecast

AccountingReadModel
  current/lifetime generation
  range queries
  token classes
  model/repository/session-role/hour summaries
  coverage/parity/integrity/generation

ObservatoryReadModel
  session explorer / topology / context / storage

IntelligenceReadModel
  burn intervals / attribution
  persisted forecast history
  scenarios / reset events

                         ↓

UI
────────────────────────────────────────────────────────────
Overview      summary of canonical current generations
Usage         bounded AccountingReadModel queries
Observatory   ObservatoryReadModel
Forecasts     current/persisted canonical forecast semantics
Analytics     IntelligenceReadModel over canonical facts
Diagnostics   structured source/generation/parity/coverage state
```

This does **not** require one giant service or one giant SQL query. The goal is shared semantic ownership, not a god object.

## Ordered implementation plan

### Slice 1 — correctness: structured generations and lane freshness

Highest priority because current behavior can mislabel genuinely fresh/stale data.

- replace global quota freshness use with lane-scoped freshness/as-of state;
- make alerts evaluate only fresh individual lanes rather than requiring the entire quota set to be fresh;
- distinguish Observatory source-empty from healthy source-present refresh;
- carry structured accounting source/coverage/fallback/parity/freshness through `TelemetrySnapshot` rather than flattening it into prose;
- add regression tests for partial app-server lane response and zero-discovered-rollout behavior.

### Slice 2 — correctness: unify accounting semantics/read consistency

- define shared SQL/semantic helpers for event model attribution and total semantics;
- remove mutable `agents.model` fallback from historical event model attribution;
- add reported-vs-disjoint integrity status/tests;
- make Overview and Usage use the same canonical accounting semantics even if query shapes differ;
- execute `SqliteIntelligenceService.QueryAsync` under one read transaction/generation;
- label repository breakdown as session-scoped attribution;
- expose accounting revision/as-of to read-model consumers.

### Slice 3 — correctness: one current forecast owner

- move Overview forecast assembly out of the ViewModel;
- define current-lane authoritative observation selection;
- define how rollout-embedded quota participates in forecast history;
- calculate one canonical current forecast and persist that same result;
- keep Forecasts page as history/scenario presentation rather than a second calculation owner.

### Slice 4 — #62 native/Tokscale semantic certification

- run real-history reconciliation with structured parity diagnostics;
- add/port fixtures for stale cumulative regressions, `last_token_usage`, forks/replay boundaries, model-less events, compaction/reset behavior;
- change native parser/counter semantics only where evidence demonstrates a mismatch;
- record parity status without making Tokscale mandatory again.

### Slice 5 — cleanup after semantic convergence

- remove/deprecate dead generic `token_usage` and duplicate mutation contracts after migration/upgrade validation;
- move accounting-revision schema ownership fully to the writer/component migration;
- update historical docs where current-runtime wording is misleading;
- re-profile read paths after correctness refactors.

## Deliberately deferred

- remote/cloud-only Codex session collection (#63);
- Phase 5 CLI/API/HUD/update features;
- speculative multi-provider abstraction beyond what current read-model contracts genuinely need;
- WAL/storage tuning unless post-refactor profiling still justifies it.

## Acceptance criteria for closing #70

- every major visible metric is represented in the source-of-truth matrix;
- the repository architecture guide reflects native-first current reality;
- concrete contradictions are tracked by implementation issues/slices;
- no Phase 5 feature depends on the current global freshness/duplicated accounting semantics by accident;
- future UI can obtain source, coverage, freshness, parity and generation without parsing provider-health prose.
