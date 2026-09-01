# Phase 4 intelligence semantics

Phase 4 turns normalized TajsTokens telemetry into historical analysis, quota explanations, reset history and workload scenarios. The implementation is intentionally conservative about what the data can prove.

## Evidence levels

TajsTokens keeps three categories distinct:

1. **Observed provider facts**
   - quota `used_percent`;
   - provider/profile/window kind;
   - observation timestamp/source;
   - provider `resets_at` / window identity.
2. **Observed local activity**
   - native-shadow token deltas from normalized Codex cumulative counters;
   - root/subagent relationships;
   - model/reasoning metadata;
   - normalized context/compaction activity;
   - session/repository metadata.
3. **Inference**
   - likely contributors to a quota-change interval;
   - reset classification when the provider does not explicitly name the event;
   - historical scenario estimates.

UI copy and models should not collapse category 3 into category 1 merely because the estimate is numerically convenient.

## Quota-burn intervals

A burn interval is created from two adjacent quota observations only when:

- provider/profile/window kind match;
- `window_minutes` and `resets_at` identify the same reset epoch;
- both observations contain a quota percentage;
- used quota rises between the observations.

A reset/re-anchor boundary is therefore never converted into negative burn or attributed to activity from the previous epoch.

The observation interval is `(previous captured_at, current captured_at]`. Native Codex activity inside that range is synchronized to the meter change. This does **not** establish which event caused the provider's quota update because the meter may be rounded, delayed or batched.

### Contributor score

The first transparent heuristic is:

- 55% native token share;
- 25% uncached-input share;
- 15% cache-read share;
- 5% compaction share.

Each component is normalized against the selected interval. The score ranks local workloads for investigation; it is not a probability and does not claim provider-side billing semantics.

Root and subagent contributions remain separately inspectable. Parallelism can explain some burn without being treated as the only cause.

## Historical aggregation

The intelligence read model supports minute/hour/day buckets. Requested resolution is automatically coarsened when the range would exceed the bounded result budget.

History includes:

- disjoint native-shadow token classes;
- root/subagent token totals;
- active session count;
- compaction count;
- positive five-hour and weekly meter movement;
- repository/model/agent-role dimensions;
- day-of-week/hour heatmap cells.

Heatmap grouping uses UTC at the persistence/query layer so timezone changes cannot silently rewrite historical bucket identity. Presentation may convert ordinary timeline timestamps to local time, but labels must make UTC grouping clear where relevant.

Native-shadow totals are useful for local correlation now, but Tokscale remains the broad/default accounting backend until Phase 6 reconciliation proves native parity.

## Reset and re-anchor classification

Provider `resets_at` remains authoritative.

The detector can emit:

- `ExpectedReset`: quota decreases while the provider advances window identity near the previous authoritative boundary;
- `ReanchoredWindow`: the provider advances a rolling window after expiry without a material visible meter drop;
- `FullReset`: a very large observed meter decrease with supporting window transition evidence, or lower-confidence same-identity evidence;
- `UnusualReset`: a material decrease that does not cleanly fit the expected-reset pattern.

Each event stores before/after percentage, previous/current reset timestamps, source, explanation and confidence. Stable event IDs make repeated scans idempotent.

A changed backend reset time is never forced onto `old reset + nominal duration`. Rolling windows are allowed to re-anchor after idle gaps.

## Persisted forecasts

Phase 3.5 defined the forecast semantics. Phase 4 adds history by persisting the current reset-aware forecast after normalized quota/rollout refresh.

Forecast history preserves:

- state;
- burn rate;
- sustainable pace;
- burn pressure;
- reset survival;
- exhaustion ETA only when it occurs before reset;
- projected margin at reset;
- confidence;
- trend;
- quantized-flat state.

Provider/profile/window scopes remain isolated.

## Scenario planner

The scenario planner does not learn a universal token-to-quota exchange rate.

Its first model uses only the account's observed positive quota-change intervals. For each quota window independently it fits a small ridge regression over:

- elapsed hours;
- root-agent-hours;
- subagent-hours.

The user-provided intensity multiplier scales workload features. Optional model/reasoning cohorts are used only when enough matching samples exist; otherwise the broader account cohort is used and confidence is reduced.

At least six usable intervals per quota window are required. Below that threshold the correct answer is `not enough data`.

The result includes:

- expected quota movement;
- lower/upper empirical range from historical residual error;
- sample count;
- confidence;
- explanation of cohort/fallback behavior.

Five-hour and weekly estimates are fitted independently and shown together.

## Threading and query boundaries

- `TajsTokens.Core` owns intelligence models and inference semantics.
- `TajsTokens.Infrastructure` owns SQLite aggregation, reset persistence and the intelligence service.
- `TajsTokens.App` receives bounded normalized results only.
- Historical queries and scenario fitting run away from the WinUI dispatcher.
- No analytics page parses rollout JSONL or executes raw SQLite directly.

## Privacy

Phase 4 consumes the existing normalized privacy-safe telemetry store. It does not persist prompt text, reasoning text, source-code bodies, shell commands/output, raw tool results, credentials or raw rollout JSON.

Contributor names/repositories are the already-normalized content-free identities used elsewhere by Observatory. The intelligence layer must not introduce a parallel raw-content cache merely to make correlations easier.

## Tests that matter

Regression coverage should include:

- expected resets and rolling-window re-anchors;
- unusual same-window meter decreases;
- ordinary rising usage producing no reset event;
- stable reset event identity;
- sparse scenario history returning insufficient data;
- dual-window scenario estimates and confidence fallback;
- SQLite forecast/reset persistence;
- root/subagent quota-interval correlation;
- bounded historical aggregation and dimensions;
- meter intervals never crossing reset identities.
