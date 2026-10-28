# Joint quota-cost identification diagnostics (CAL-001)

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
rendering and native file selection are pending the user's manual host check.

The paired old model implementations and their thresholds remain unchanged.
CAL-002 must compare a restrained joint challenger with pooled/raw baselines on
chronological matched outcomes. Positive quota envelopes with no recorded local
work must remain visible; they cannot be fitted away as local token prices.
CAL-003/004 retain ownership of feasible-set assumptions and uncertainty semantics.
