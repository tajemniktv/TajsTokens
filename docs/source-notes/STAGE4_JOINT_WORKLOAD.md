# Stage 4: observed joint workload

## Contract and scope

`SCORE-001` adds the immutable, bounded `codex-joint-workload/v1` read projection.
It aggregates selected native increments directly into model/effort coordinate rows
containing the five disjoint categories and the independent reported total. It never
multiplies model shares by category shares. Decimal amounts preserve sums above the
range of one `long`. Per-observation inconsistency and invalidity counts prevent
opposing errors from disappearing when aggregates happen to balance.

The native event owner establishes model and effort at each increment. Requested
service tier and model context capacity do not establish executed billing tier or
charged context band. Execution tier, billing product and charged context band
therefore remain unknown in this projection. The coordinate contract can distinguish
explicit labels without claiming that the current native writer supplies them.
Source retention, counter reduction, occurrence selection and account ownership do
not change. No new native source access or telemetry schema is introduced.

At most 256 distinct coordinates are retained, selected deterministically by ordinal
model/effort order. Amounts outside that vocabulary go into a separate aggregate
overflow row. Missing-model and missing-effort mass is counted independently across
all rows, including overflow; these missingness counts overlap. Zero-token observed
coordinates remain present. Overflow is loss of coordinate detail, not missing
source metadata or discarded usage.

The product snapshot uses its already selected ledger. Advanced Evidence shows
the scope, totals, diagnostics and up to 16 coordinate rows. Feature-index caches
use the same origin/lookback/collection qualification as existing features. Forecast
composition scales the preceding two hours' actual joint mixture and marks it as
`ConditionalProjection`; source observations remain `ObservedIncrements`. Composed
validation carries that predicted mixture instead of leaking the realized target.
Existing JSON without the optional field remains readable. Unsupported or malformed
joint contracts fail JSON decoding rather than silently becoming a supported basis.

## Existing accounting and versions

`model-effort` remains the existing marginal comparator, not a joint cost model.
It now distinguishes known labels outside its frozen training vocabulary from
missing source labels. The versions are `quota-cost-observations/v5-joint-evidence`,
`quota-cost-evaluation/v7-joint-evidence`, `recent-workload-composition/v3-joint`,
`rollout-token/v3-joint-composition` and `codex-intelligence/v2-joint`.

The old five-category TT research basis and its artifacts are unchanged. This slice
does not select a public TT basket, price schedule, joint weights or billing-tier
convention. It does not promote a joint quota model or claim better prediction.

## Validation

The fixed app-owned numeric fixture used for the Stage 3 comparison was read-only:
2026-09-27 00:00 UTC through 2026-10-27 14:58 UTC. It supplied 20,078 native events,
29,663 quota readings and 759 constructed cost intervals. Its 15 joint coordinates
conserved **3,984,535,029** reported/category tokens exactly. Missing model, missing
effort, vocabulary overflow, invalidity and category inconsistency were all zero.
JSON round-trip equality passed; the aggregate payload was 5,146 characters.
All 759 interval features carried joint amounts with zero conservation failures.
All extra billing dimensions remained unknown.

A temporary probe compared the installed Stage 3 assemblies with this source on
the same fixed range, using frozen first-20 training and subsequent chronological
held-out intervals. All **24** score sets and individual trials for pace, total,
categories and model-effort matched exactly. No new model was selected. The main
cohort's model-effort comparator retained 505 half-hour held-out intervals (interval
loss 0.082521 pp, displayed MAE 0.299287 pp; 430 bands with 0.923256 intersection)
and 118 two-hour held-out intervals (loss 0.712406 pp, MAE 1.090563 pp; 98 bands with
0.836735 intersection). These are cost-only oracle-workload results, not end-to-end
forecast accuracy or calibrated account-wide coverage. Other cohorts remain sparse.

Single probe runs allocated 233.63 MiB before and 234.30 MiB after; peak working set
was 112.64 / 115.55 MiB. This bounds the observed increment for that evaluation and
does not close the app's aggregate background-memory acceptance.

Targeted tests cover equal marginals with different joint costs, conditional mixture
continuation, deterministic overflow with missing labels, per-event inconsistency
cancellation, negative values, sums above `long`, zero observations, collection-time
qualification, cache reuse, immutability, versioned JSON, distinct extra dimension
labels, and separation of missing/overflow in the frozen marginal comparator.
The existing SQLite product-engine test also checks selected/account-filtered joint
amounts without assigning unattributed work to the current account.

The full Core suite passed **781 tests** (1m50s). The Windows solution build passed
with zero warnings/errors (50.50s) and installed/restarted the daily app at
2026-10-28 09:42:08 UTC. The installed and published Core assemblies matched SHA-256
`D80944ECF74531FF8455331143DCCE17354492CBFAD1D89AEA8DDC3ABF7237F9`.
A read-only product query using the installed assemblies returned the same 20,078
selected rows, 15 coordinates and exact totals; the matrix equalled aggregation of
that snapshot's selected ledger. Its manifest included the new contract versions.

Against the deployment backup, all 96,354 prior native rows and 398 counter-input
journal rows retained their numeric values and chronology; none were removed.
One normal ingestion added one native and journal row after restart. Schema versions
remained telemetry 16 / observatory 11 / state index 8. Fresh reset forecasts still
used the Stage 3 receipt policy; these checks do not constitute a visual pass of
Advanced Evidence or close the pending aggregate-memory budget.
