# Joint quota-cost identification and restrained conversion (CAL-001/002)

`quota-cost-identification/v1` inspects the existing reconciled cost observations;
it does not fit prices, change the conversion selector or require a TT scalar.
The product's explicit accounting analysis and the read-only evaluation command
use the same diagnostic. Production calibration/forecast refresh does not run it.

## Scope and numerical meaning

Keep each account/source/plan/limit/window/horizon cohort separate. Matrix inputs
come directly from sparse model × disjoint-category cells. Missing model labels
and vocabulary overflow occupy separate category bins. The training vocabulary is
bounded to 16 models. At most 2,048 chronological rows per cohort are examined;
examined/available counts expose truncation. Zero-work, absent/inconsistent joint
data and later/unknown collection availability have separate counts.

The existing first-20 prefix remains a **frozen comparator**. It is not replaced by
the first 20 positive-work or complete rows, and more observations of the same
mixture cannot identify its separate coordinate weights. The retained-design
comparison uses the available retrospective cohort; it does not claim those later
features or labels were available at an earlier forecast origin.

Diagnostics use noncentered column RMS scaling, matching zero-intercept cost
geometry. Pivoted twice-reorthogonalized row Gram-Schmidt supplies a numerical
rank estimate at relative residual tolerance `1e-8`. This tolerance is a diagnostic
engineering choice, not a theorem about useful statistical identification. The
participation dimension is `trace(G)^2 / trace(G^2)` for the column-normalized Gram
matrix. Maximum noncentered column cosine reports co-occurrence/collinearity.
Participation dimension is neither entropy effective rank nor independent sample
size. Exact numerical rank does not establish precise or causal cost coefficients.

Later positive-work vectors are projected against the frozen training row span;
relative residual at most `1e-6` means "within span". Novel coordinates, token
range and duration range are counted separately, with overlapping reasons. A
familiar mixture can lie within this span even if its individual coordinate prices
are unidentified. Span membership does not establish nonnegative feasible prices,
interpolation, account completeness or predictive calibration; CAL-003 owns those
stronger questions. Zero local work never establishes zero account consumption.

The existing six-hour chronological blocks and reset grouping are reused.
Block spacing and the effective-sample estimate do not prove independence;
observed reset groups do not automatically establish completed accounting cycles.
Meter account identity is not local-work attribution.

Numerical methodology was checked against primary references: numerical rank
requires a tolerance appropriate to the application ([NumPy documentation](https://numpy.org/doc/stable/reference/generated/numpy.linalg.matrix_rank.html));
QR pivoting is a rank-diagnostic route, with numerical rank requiring an explicit
criterion ([LAPACK guide](https://www.netlib.org/lapack/lug/node42.html)). The bounded
implementation is not a general-purpose LAPACK replacement. Independent SVD
checks below qualify its behavior on the inspected matrices.

## Reproduction and retained-data findings

After building the evaluation project, use the app-owned database and fixed range:

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation -c Debug --no-build --no-restore -- --cost-identification "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db" 2026-09-27T00:00:00Z 2026-10-27T14:58:00Z
```

The `--cost-identification-audit` variant emits JSONL numeric matrices and a final
report for independent verification. It uses cohort aliases, not account/session/
path identifiers. Neither command writes evidence or selects a model. Matrices
remain private derived diagnostic exports unless deliberately reviewed for sharing.

The inspected range yields 12 separate cohort/horizon groups and 2,981 scoped
interval rows. Rows across horizons are overlapping evidence, not 2,981 independent
outcomes. No group hit the diagnostic row bound; none had missing/invalid joint
amounts, missing model mass or vocabulary overflow. This describes the retained
cost intervals, not complete discovery or account-wide token coverage.

| Cohort alias | Window | Horizon | Rows | First-20 joint rank / active columns | Retained rank / active columns | Retained participation dimension | Blocks / reset groups |
|---|---|---:|---:|---:|---:|---:|---:|
| 0 | Weekly | 5 min | 788 | 2 / 4 | 9 / 12 | 3.53 | 39 / 8 |
| 1 | Weekly | 15 min | 835 | 3 / 4 | 12 / 12 | 3.84 | 57 / 11 |
| 2 | Weekly | 30 min | 525 | 2 / 4 | 11 / 12 | 3.44 | 56 / 10 |
| 3 | Weekly | 120 min | 138 | 3 / 8 | 9 / 12 | 3.01 | 54 / 9 |
| 4 | Five-hour | 5 min | 194 | 0 / 0 | 4 / 4 | 1.54 | 5 / 5 |
| 5 | Five-hour | 15 min | 66 | 0 / 0 | 4 / 4 | 1.27 | 5 / 5 |
| 6 | Five-hour | 30 min | 32 | 4 / 4 | 4 / 4 | 1.17 | 5 / 5 |
| 7 | Five-hour | 120 min | 8 | 4 / 4 | 4 / 4 | 1.09 | 5 / 5 |
| 8 | Weekly | 5 min | 251 | 0 / 0 | 4 / 4 | 1.69 | 5 / 1 |
| 9 | Weekly | 15 min | 88 | 0 / 0 | 4 / 4 | 1.33 | 4 / 1 |
| 10 | Weekly | 30 min | 45 | 3 / 4 | 4 / 4 | 1.27 | 4 / 1 |
| 11 | Weekly | 120 min | 11 | 4 / 4 | 4 / 4 | 1.09 | 4 / 1 |

The largest weekly 15-minute group has 705 zero-local-work rows out of 835.
Of 127 later positive-work rows, all lie outside its first-20 training span and
122 introduce coordinates absent from that prefix. At 120 minutes, 36 later
positive-work rows lie outside the prefix span, though only one has a novel
coordinate: composition contrast matters in addition to vocabulary coverage.
The five-hour 30-minute cohort has two later positive rows inside its training
span; one nevertheless lies outside its training token range. These are geometry
and coverage findings, not automatic withholding or acceptance decisions.

NumPy 2.3.5 independently checked numerical rank via SVD and participation
dimension for all **36 matrices**, with **zero mismatches** at the declared test
tolerance. The largest matrix has 835 rows and 25 columns; the largest retained
nonzero-subspace condition ratio was about 73.80. This does not establish universal
rank-revealing behavior or statistical precision under rounded/noisy quota labels.

## Validation boundary and next work

Regressions cover replicated mixtures, equal marginals with different joint pairing,
familiar-volume versus novel-composition edits, unknown/overflow labels, invalid and
zero rows, cohort separation, row bounds, cancellation and coordinate-unit scaling.
The final Core suite passed **844 tests** (2m 2s). The Windows build passed with
zero warnings/errors (1m 3.87s), installed the daily build and restarted PID 36500;
the deployment backup is `20261028T163957872Z-e3ea3015`. Installed Core SHA-256 is
`09ef0a13b884fa55d53461674f3593daaaf30fa7683d7d46a2c027a9be759ee1`;
evaluation assembly SHA-256 is
`fc8800ee39d8be3ec3d55d39f5df3eab9a487c42164f72c0acaafa5635c07639`.
The installed read-only audit reproduced the table and all 36 independent matrix
checks. Its private JSONL SHA-256 is
`471c52096b53fbe652ceebcf3bc90d88205cbc53813fc2e6ca6ef9de17b7f1c0`.

Coverage mismatches remain explicit. In the largest weekly 15-minute cohort,
52 of 705 zero-local-work rows have positive displayed movement, and four have a
strictly positive lower measurement bound. In the five-hour 5-minute cohort,
65 of 167 have positive displayed movement and 52 have a positive lower bound.
At 120 minutes, the largest weekly cohort has 22/99 and 14/99 respectively.
These counts overlap across horizons. They reject a literal zero-lag,
complete-local-coverage explanation for those declared envelopes; they do not
identify the cause, external activity amount or lag distribution. Diagnostics
rendering was subsequently confirmed by the user's manual host check and screenshot.
They reported working accounting/regime analysis but excessive prose. Presentation
work should keep concise results visible and methods/assumptions inspectable on demand.

The paired old model implementations and their thresholds remain unchanged.
CAL-002 must compare a restrained joint challenger with pooled/raw baselines on
chronological matched outcomes. Positive quota envelopes with no recorded local
work must remain visible; they cannot be fitted away as local token prices.
CAL-003/004 retain ownership of feasible-set assumptions and uncertainty semantics.

## Restrained joint challenger (CAL-002)

`quota-cost-research/v8-joint-shrink` adds `joint-categories` to explicit research,
not the live candidate list. It reuses `QuotaAccountingModel`'s nonnegative,
zero-intercept interval-distance ridge solver. For training matrix `X`, target
envelopes `[L,U]` and column RMS scales `s`, it minimizes
`mean(distance(X beta,[L,U])^2)/2 + 0.1*||s*(beta-betaPooled)||^2/2`.
`betaPooled` is the preceding pooled-category fit on exactly the same frozen
training rows. The penalty is fixed before evaluation; no held-out tuning occurs.
Missing-model and training-vocabulary overflow category bins remain separate;
the model vocabulary is capped at 16. Effort, tier and context add no multiplier.

All final coefficients are nonnegative. Unseen coordinates inherit the pooled
prior as a regularization assumption, not an identified price. Zero local work
still predicts zero local contribution; an intercept cannot explain missing
external activity. Absent/inconsistent observed joint inputs withhold the joint
candidate while preserving simpler supported baselines. The old first-20
comparator, default live candidates, fitting results and promotion policy remain
unchanged. Its research flag cannot earn a production-promotion label.

The existing composed evaluator has an explicit `includeJointResearch` option,
default false. It uses the existing origin-only joint continuation, preserving
native pairings rather than multiplying model/category marginals. It reports
realized-work oracle cost and future-work forecast losses separately. Collection-
qualified replay checks training, origin and outcome availability; reconstructed
replay remains a distinct experiment. This does not select a new context, fit a
lag distribution or prove local-work/account attribution.

### Retained comparisons

The diagnostic range is September 27 00:00–October 27 14:58 UTC. A second,
recent-week range is October 22 00:00–October 28 16:40 UTC, following the user's
current dashboard range and its stronger observed joint contrast. This second
inspection is exploratory, not an untouched final selection holdout. Each range
fits its own frozen first-20 prefix. Empty/sparse prefixes remain visible rather
than silently choosing the first 20 positive rows. Overlapping horizons are never
pooled or treated as independent outcomes.

Mean measurement-envelope distance, in quota percentage points:

| Range / compatible meter cohort | Horizon | Oracle held-out rows | Raw total | Pooled categories | Joint |
|---|---:|---:|---:|---:|---:|
| Month / largest weekly | 15 min | 815 | 0.0743 | 0.0568 | 0.0579 |
| Month / largest weekly | 30 min | 505 | 0.1034 | 0.1556 | 0.1570 |
| Month / largest weekly | 120 min | 118 | 1.1985 | 0.9586 | 1.4403 |
| Month / five-hour | 30 min | 12 | 9.6643 | 10.0858 | 10.4468 |
| Month / other weekly | 30 min | 25 | 0.5059 | 0.4939 | 0.5242 |
| Recent week / weekly | 15 min | 121 | 0.1695 | 0.3245 | 0.1315 |
| Recent week / weekly | 30 min | 55 | 0.4267 | 0.9430 | 0.2062 |
| Recent week / weekly | 120 min | 3 | 0.1196 | 0 | 0 |

Five-minute comparisons have zero envelope distance for all three candidates;
that broad-envelope result does not identify useful conversion weights. Four
month-range cohort/horizon prefixes contain no local work; two have fewer than
20 intervals. They produce no fitted local-conversion results. Positive meter
movement without recorded local tokens and the coverage limitations above remain
part of interpretation, including in seemingly favorable mean errors.

Collection-qualified **end-to-end** recent-week comparisons, on identical targets:

| Horizon | Matched targets | Provisional blocks / reset groups | Raw total | Pooled categories | Joint |
|---|---:|---:|---:|---:|---:|
| 5 min | 176 | 5 / 2 | 0 | 0 | 0 |
| 15 min | 92 | 9 / 3 | 0.1539 | 0.2652 | 0.0936 |
| 30 min | 42 | 7 / 3 | 0.3735 | 0.7490 | 0.1702 |
| 120 min | 0 | 0 / 0 | unavailable | unavailable | unavailable |

The missing two-hour trials are withheld because frozen training was collected
after those origins. At 15/30 minutes one origin fails that availability check;
28/12 further origins lack supported composition. Counts can overlap. Six-hour
blocks/reset groups do not establish independence or completed cycles; the
existing effective-sample estimates are only about 3.33 and 5.00.

The recent reconstructed 15/30-minute forecasts improve similarly (joint
0.1549/0.2960 versus total 0.2102/0.5199 and pooled 0.3749/1.0606, 109/52 targets).
Its three two-hour forecasts regress (joint 1.1311 versus total 0.1093 and pooled
0.8110), despite zero oracle joint cost loss. This demonstrates why oracle fit
does not establish future-work prediction. In the month replay, joint forecasts
generally lose to simpler models; the largest weekly strict two-hour cohort is
an exception (34 targets, joint 2.4967 versus total 3.2378 and pooled 2.7510),
while its joint oracle cost is worse. No universal model or horizon win follows.

**Decision:** retain the challenger in explicit research. Recent 15/30-minute
improvement supports continued investigation, but sparse short-window blocks,
mixed older-period performance, two-hour availability and unresolved local/account
coverage do not justify changing live selection or presenting coefficients as
provider prices. A later context/prefix policy needs its own chronological
evaluation under FORE-001/005; first-20 is not a permanent sample-size policy.

### Numerical and product verification

The joint-pairing synthetic counterexample distinguishes costs 51 and 15 despite
identical marginals. Regressions also verify training-only vocabulary/weights,
pooled-prior behavior for unseen models, nonnegativity, zero local cost, incomplete
joint withholding, matched baselines, unchanged default composed candidates and
late-collection rejection. Independent NumPy 2.3.5 coordinate minimization solves
each scalar derivative by bisection, separately from the product's simultaneous
projected-gradient solver. All ten fitted cohort/horizon comparisons across both
ranges agree within `1e-4` pp held-out prediction difference and `1e-6` scaled
KKT residual; zero mismatches. This checks the chosen convex objective, not the
scientific validity of the local/account explanation.

Private read-only cost JSONL artifact SHA-256 values:

- Month: `69f1d249bc376fe07784a5932d589dc7aca3fc0d34e885b1140ceb59f1bf1396`
- Recent week: `cbb20b21fe4c1451b88a4ee36d4d0308ee486d172cacc587e68d879bf9fa581e`

Explicit accounting analysis presents the joint comparison alongside total and
pooled results. Following the user's host feedback, long methodology and detailed
coverage qualifications move into a collapsed **Methods and assumptions** expander;
the first view shows concise result and support rows. This is a targeted response
to the reported prose density, not completion of the app-wide presentation review.

The final full Core suite passed **849 tests** (2m 8s). Windows build passed with
zero warnings/errors (1m 14.09s) and installed/restarted PID 31884, with backup
`20261028T165630318Z-b6145ecd`. The installed command reproduced all recent-range
numeric scores exactly. Installed Core SHA-256 is
`8a5fc6892bfee734e38377a6ae19044acbc6279b6f52664ddab1dd738d1ef56b`;
app assembly SHA-256 is
`55850fce340d5f8418919270a4eef8ed61174957fb87babc96985bd4898e5dee`.
The user's prior analysis/import checks pass; the shorter presentation has its
own requested manual acceptance check. No new persistence or collection contract
was introduced by CAL-002.
