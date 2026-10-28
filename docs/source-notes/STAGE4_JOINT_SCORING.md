# Stage 4: question-specific joint TT scoring

## Quantity and result contracts

`TtJointScoring` implements `tt-joint-scoring/v1` for the frozen joint basis. It
accepts either the selected joint workload or explicit nonnegative quantity
constraints. `TtQuantityConstraints` has five nullable disjoint amounts, optional
input/output group totals and an optional overall reported total. Null means
unconstrained. Input is uncached + cache read + cache write; output is non-reasoning +
reasoning. This is an explicit mathematical contract, not a parser for arbitrary
raw Codex input/output fields or a license to add cumulative snapshots to increments.

The adapter uses selected native increments with their joint model/category pairing.
It carries per-event inconsistency diagnostics, including errors that cancel in an
aggregate, and retains the observed/conditional-projection distinction. It does not
modify native reduction, original counters, timestamps, attribution or eligibility.
Partial cumulative journal counters are not automatically mapped to selected deltas.

The scorer returns:

| State | Meaning |
|---|---|
| Exact | The score is identified under the named basis and constraints. Missing splits with equal weights, forced-zero amounts and ignored metadata need not prevent it. |
| FiniteBounds | Minimum and maximum feasible scores under the declared nonnegative quantities and totals. No split is imputed; these are identification bounds, not confidence intervals. |
| Partial | A coordinate/category weight is unsupported or unresolved. Retain the identified supported subtotal; the whole score has no defined bounds under this basis. Missing weights are neither zero nor infinite prices. |
| Unbounded | Weights are supported, but an unconstrained quantity with a positive coefficient leaves no finite upper score. The finite lower bound is retained. |
| Inconsistent | Negative quantities, conflicting totals or source inconsistency flags make the supplied constraints infeasible. No plausible score/subtotal is asserted. |
| ArithmeticUnavailable | Required quantity/output arithmetic exceeds the supported decimal range or precision. This does not classify source observations as inconsistent. |

`CoveredSubtotalTt` contains identified supported contributions, including identified
equal-weight groups; it is not an estimate of the uncovered contribution.
`KnownUnmappedTokens` counts definitely unmapped quantities only. A mixed supported/
unsupported unknown split can have zero **known** unmapped tokens while mapping
remains unresolved. `UnmappedRows` and the Partial state retain that distinction.
`TotalTokens` stays null when not identified; no coverage percentage is invented.
Exact scores describe the supplied selected workload, not complete account coverage.

## Bounded calculation and numeric behavior

The five leaves form two disjoint groups and one overall total. Fixed leaves are
subtracted from known group budgets. An overall total can identify the other group,
or constrain the remaining leaves together when neither group has its own budget.
For a fixed residual budget, allocating it to the minimum/maximum supported leaf
coefficient gives the extrema. Independently unconstrained leaves have zero minimum
and an unbounded maximum when any coefficient is positive. Missing active model/
tier/etc. labels do not borrow the extrema of a supposedly exhaustive known catalog.

This closed-form calculation is bounded to 257 rows (256 coordinates plus overflow).
It introduces no generic optimizer, calibration fit, remote service or paid task.
Arbitrary overlapping constraints and latent quota-coefficient sets are outside this
contract; their feasibility solver remains bounded/offline research when justified.

Finite decimal products and weighted sums use exact integer scaling. The reference
dot product is evaluated exactly from the frozen basket and weights. Final identified
values round nearest/even to representable decimal precision; finite endpoints round
outward. Positive identified values below decimal precision are unavailable rather
than rounded to a false zero. Quantity sums that cannot be represented exactly are
also explicit arithmetic limitations. The old `ScoreExact` primitive and recorded
values remain unchanged; the new assessment carries its own scorer version.

## Archive and explicit candidate tool

Joint score snapshots can optionally include the versioned assessment. An omitted
assessment is omitted on serialization too, preserving existing v1 payloads and
original scalar output. New assessments are verified against their own saved basis
and joint quantities; altered results remain unavailable even with a refreshed
payload checksum. No schema change or automatic restatement is introduced.

The evaluation executable has `--score-joint <app-owned-db> <basis.json> <from> <to>`.
It checks the supplied saved basis identity/normalizer, reads the selected ledger
without quota/turn/context data and prints aggregate JSON. The period must be a past
ordered UTC range of at most 366 days. It never activates a ruler, writes daily data,
assigns current-login ownership or reads raw Codex source files. This permits a
candidate public basis to be reviewed against real quantities before T-01 activation.

## Validation boundary

Focused tests cover equal-weight output without its split, bounded cache/input
splits, conservation-based recovery of a missing amount, partial known/unmapped
coordinates, unknown active tier, unbounded positive quantities, known zero weights,
inconsistent constraints, overflow/projection provenance, numeric range/precision
and outward rounding for nonterminating ratios. Eighty deterministic small cases
also compare the closed-form endpoints with independent exhaustive feasible
integer allocations. Archive tests preserve absent legacy assessments and verify
new partial assessments through restart and edited-checksum corruption.

No public TT coefficients, basket or automatic pricing policy is selected here.
No quota/workload inference algorithm or model promotion changes in this slice.

## Installed and retained-data verification

The full Core suite passed **809 tests** (1m51s). The Windows build passed with zero
warnings/errors (50.72s), installed and restarted the daily application at
2026-10-28 10:27:30 UTC. The installed Core/Infrastructure/evaluation assemblies were:

- Core SHA-256: `F16C8D4964206EC4A578471FB6E53026FE05EE250CDA64CA4D64CBB1D68DBED5`
- Infrastructure SHA-256: `3B306B360318D02EF7D292E080B28979DBF8A6323514AD22188EA277D563BDEA`
- Evaluation SHA-256: `80325F8A288501256A0528B87DF82A716DEC6A0124CCC229032821B2D8F5CAC0`

The installed CLI read the same app-owned frozen month (2026-09-27 00:00 through
2026-10-27 14:58 UTC): 20,078 selected events and 3,984,535,029 native tokens. A
**synthetic integration ruler**, covering only exact-name `gpt-6-astra` with category
weights 1/2/3/4/5 and a 1,000-input-token reference, exercised partial scoring. It is
not a price schedule or a proposed public basis. Its 3,196 supported event rows
produced an identified synthetic subtotal of 935,378.53. Independent direct numeric
sums matched that subtotal and all 3,514,313,270 unmapped tokens. Ten of 15 joint
coordinate rows were unmapped; whole-score endpoints stayed null with Partial state.
This is integration evidence, not index validity or valuation accuracy.

The deployment backup comparison preserved all 96,412 prior native rows, 456
counter-input journal rows and all six research snapshots, including their numeric
values/chronology and archived bytes. One normal native/journal observation arrived
after restart. Telemetry/observatory/state-index/intelligence schemas remained
16/11/8/8. The new daily joint archive remained empty. Existing fresh pace receipts
continued to reconstruct their reported calculations; no public ruler, synthetic
score or automatic historical restatement was written into daily data.
