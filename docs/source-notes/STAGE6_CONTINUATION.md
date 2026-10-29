# Recent joint-work continuation inputs (FORE-001)

`recent-joint-continuation/v1` initially extended the existing workload-composition owner;
the Planning integration below advances it to `v2-finite-decimals` for reference scoring.
It preserves observed model/effort/category cells and scales their amounts by
`forecast horizon / elapsed context`. It does not multiply independently averaged
volume and composition, fit a tariff or require quota conversion/TT availability.
The existing scalar-forecast/two-hour composition route is unchanged.

## Input and clock contract

- Observation interval: `(origin-context, origin]`, with its full elapsed denominator.
  Quiet periods and tool waits are included; no future sleep is assumed.
- Recent context: explicit duration, separate from the prediction horizon. The
  first comparison uses the existing 30-minute and two-hour choices.
- Training span: absent for this deterministic continuation. Any later quota-cost
  calibration must supply its own preceding training interval; context is not training.
- Horizon: `(origin, origin+horizon]`; evaluated separately at 5/15/30/120 minutes.
- Availability: reconstructed event time or collection-qualified origin, separately.
  Unknown/late collection and future events cannot enter the qualified context.
- Receipt: context bounds, horizon, nullable training bounds, last evidence/positive
  work, source observation count, reported/projected native totals and status.
  No fitted confidence or account-ownership claim is manufactured.

Missing context returns unavailable, with nullable amount. A nonempty set of explicit
zero observations can remain a zero conditional continuation; that does not establish
idleness or complete collection. Invalid/disjoint-incomplete categories withhold the
joint projection while preserving a usable reported-total receipt. Unknown model/
effort and vocabulary overflow remain in the joint vector. No effort multiplier is
added. The projection is installation-local, not account-wide attributed work or a
prediction that a different model performs the same task with the same tokens.

The evaluation reuses the existing per-evaluation time index; no persistent cache,
new collector or always-on work is introduced. The index owns a fixed input snapshot,
and a replacement dataset receives a new index. Tests compare indexed/flat results
and changed-input regeneration. Boundary, future-data, collection-time, unknown-label,
incomplete-category, zero/missing and constant-work examples exercise the shared path.

## Fixed context comparison

`continuation-context-evaluation/v1` reserves the first 20 compatible intervals for
the later conversion comparison and evaluates later disjoint targets. The continuation
itself fits no parameters. Context availability is reported separately; errors compare
only targets with **both** contexts available. Recorded zero targets mean no recorded
local work, not verified zero account usage. No account/horizon pooling is allowed.

Collection-qualified replay also requires the origin meter/prefix available and
token-cost/outcome evidence available within five minutes of the target end. This
explicit label-availability gate and matched-context restriction produce different
target sets from Stage 5's composed forecast replay; their mean errors are not directly
comparable. Reconstructed replay remains a separate output.

Metrics are native-token MAE, WAPE (total absolute error / total recorded target tokens),
signed accumulated token error and full-coordinate joint-share L1. Composition is
scored only when actual/projected work is positive; its sample count is separate.
Source rounding/account/lag assumptions do not turn these local-token errors into
quota error or calibrated predictive coverage.

Ranges: September 27 00:00–October 27 14:58 UTC and October 22 00:00–October 28 16:40 UTC,
matching Stage 5's fixed inspections. Recent collection-qualified results:

| Horizon | Available 30m / 2h contexts | Matched targets | Provisional blocks | MAE, million native tokens: 30m / 2h | WAPE: 30m / 2h | Joint-share L1: 30m / 2h |
|---|---:|---:|---:|---:|---:|---:|
| 5 min | 143 / 160 | 143 | 4 | 1.162 / 1.503 | 41.3% / 53.5% | 0.0307 / 0.0314 |
| 15 min | 64 / 73 | 64 | 7 | 3.138 / 3.944 | 41.1% / 51.6% | 0.0221 / 0.0346 |
| 30 min | 27 / 30 | 27 | 6 | 4.997 / 6.018 | 33.6% / 40.5% | 0.0196 / 0.0346 |
| 120 min | 2 / 3 | 2 | 1 | 11.197 / 8.549 | 39.2% / 29.9% | 0.0088 / 0.0037 |

There are 133/61/26/2 positive-work composition targets respectively. The recent
5/15/30-minute signed errors are +9.14/−12.43/−10.62 million tokens for 30 minutes,
versus −1.98/−1.02/+11.29 million for two hours. Improved absolute error does not
automatically improve accumulated bias. Five/15/30-minute latest-positive context
age exceeds ten minutes at 5/5/2 matched origins; they remain explicitly conditional
on the stated elapsed context, not inferred current active work.

Older largest-weekly-cohort collection-qualified MAE is mixed: 30 minutes versus
two hours gives 1.062/1.119 million tokens at five minutes (117 matched),
3.047/3.053 at 15 minutes (123), 5.723/5.588 at 30 minutes (83), and 20.031/23.369
at two hours (23). Other cohorts stay separate. The older reconstructed comparison
also varies by horizon. Overlapping horizons/cohorts are not independent replications;
provisional blocks and effective-sample estimates do not establish independence.

**Decision:** retain both contexts for the bounded path/conversion comparison under
FORE-002/005. The recent shorter-context signal is useful, but availability, sparse
two-hour outcomes, older mixed results and separate native-token/quota targets do
not justify globally changing the released two-hour default. DEC-003 remains open
before any changed default or horizon-specific live selection. No automatic switch
or future-work distribution is selected by FORE-001.

Private report SHA-256 values (including their actual read-capture time):

- Month reconstructed: `53a8f224f1cfea42e85b875d901e52ab2b77c0e27bcd960d26824cc0f10d63f9`
- Month collection-qualified: `87f1fde217102e54ae6b5c82ee6b01fe7e366f8ca3efa40d77fff907b3ca98bd`
- Recent reconstructed: `d102c316d60f680690df1abcd720af61498d4d142b35f01235a0d68c89e014a6`
- Recent collection-qualified: `65e3b95c8432f7e91948a3ae071d334abb8de4a6cdfc3086ade181b116ecbd4e`

## Reproduction

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation -c Debug --no-build --no-restore -- --continuation-contexts-strict "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db" 2026-10-22T00:00:00Z 2026-10-28T16:40:00Z
```

Use `--continuation-contexts` for reconstructed event-time replay. Both commands
open the app-owned database read-only, accept ordered past ranges up to 366 days
and omit account/session/path identifiers from their report. Meter cohort aliases
qualify comparisons; they do not attribute local tokens to those accounts.

The full Core suite passed **866 tests** (2m); the Windows build passed with zero
warnings/errors (51.82s), installed/restarted PID 11172 and made deployment backup
`20261028T173009687Z-fdc9f7eb`. The installed collection-qualified recent command
reproduced every numeric score exactly. Installed Core SHA-256 is
`377c8e52f8241f31dd28fa70b1f821d46c5778aac63e07e60679c72da848f001`;
app assembly SHA-256 is
`a616414a557fbc22f8627a8987db60fd7535ac74e0c019e139cdd075eea95d8a`.
This slice adds a shared contract and explicit research command; no new dashboard
control or released forecast default was introduced. Stage 5's user host checks
remain recorded separately.

## Bounded joint paths and quota comparison (FORE-002)

`recent-joint-block-paths/v1` adds one finite-window resampling candidate beside
the deterministic recent-context continuation. Both use the existing composition,
joint-workload and frozen quota-cost owners. Neither is registered for live model
selection. The explicit composed-research switch adds 30-minute/two-hour contexts
for deterministic continuation and block paths; the old scalar-volume/composition
candidate and corrected direct pace remain comparators.

The sampler creates **64** paths using whole joint five-minute bins and contiguous
runs of up to three bins. Starts are uniform over source bins; a run stops at the
source end instead of wrapping to an adjacency not present in the record. The
final fractional future bin scales every native category together by elapsed
duration. Empty recorded bins preserve pauses in this local record, not proof of
idleness or complete collection. Volume, mixture and bursts within a run remain
paired; dependence across run boundaries or beyond the context is not preserved.
Source coordinates/overflow remain explicit, and projected draws never become new
source-observation counts. The context is at most two hours; nominal horizons up
to two hours permit the existing five-minute outcome polling tolerance.

This is an intentionally small **model-conditional** finite-window experiment,
not an application of a bootstrap confidence theorem. It cannot create unseen
support, establish stationarity, infer future sleep or supply a calibrated path/
exhaustion probability. Deterministic xorshift32 draws use recorded seed/origin;
the base seed is `0x544F4B4E`. The semantic input revision includes exact available
event/collection clocks, model/effort labels, six native amounts, origin, context,
horizon, availability and algorithm version. Session IDs, paths and credentials
are unnecessary to that cache identity and are not exported.

An owned cache holds at most eight immutable ensembles. It stores workload paths,
not quota fits or TT/price calculations, so changing those calculations cannot
reuse an old converted quota result. Tests prove warm reuse, independent regeneration
with an empty cache, inside-window amount/collection-clock invalidation, safe reuse
for irrelevant outside-window/quota-only edits, bounded eviction and deterministic
regeneration. The cold recent strict replay recorded zero hits / 778 misses, with
eight retained entries; no replay speedup is claimed from those cache statistics.

Each quota comparison uses the same frozen nonnegative joint fit shrunk toward the
pooled-category prior. The mean prediction converts the **mean joint vector**, not
separate means of volume and shares. Raw path P10/P90 are tagged
`ConditionalResampledPathRange`; earlier-held-out residual bands remain separate.
Conversion diagnostics expose training-envelope violations, unknown/novel joint
coordinates, training bounds and amount extrapolation. A failed fit-compatibility
check is not an infeasibility proof: regularization can choose an outside-envelope
optimum even when other feasible coefficients exist. Conversely, compatibility
does not establish precise coefficients, ownership, complete coverage or predictive
calibration. Exploratory errors remain reported rather than being silently discarded
when these qualifications fail. No tolerances are enlarged to qualify a conversion.

### Matched retained results and decision

The four alternatives are compared on the intersection of exact `(origin,outcome)`
targets for that cohort/horizon. Direct pace is scored on those same targets;
each candidate's larger availability count is reported separately. Meter account
cohorts remain distinct and do not attribute local native work to an account.

Recent collection-qualified mean measurement-envelope distance, in quota pp:

| Horizon | Matched targets | Corrected selected direct pace | Recent 30m | Recent 2h | Block 30m | Block 2h |
|---|---:|---:|---:|---:|---:|---:|
| 5 min | 181 | 0 | 0 | 0 | 0 | 0 |
| 15 min | 80 | 0.0233 | 0.1184 | 0.0864 | 0.1156 | 0.0888 |
| 30 min | 36 | 0.0636 | 0.2631 | 0.2401 | 0.2517 | 0.2330 |
| 120 min | 0 | unavailable | unavailable | unavailable | unavailable | unavailable |

At 15 minutes the two-hour alternatives have 92 available targets versus 80 for
30 minutes; at 30 minutes they have 42 versus 36. All paired 15/30-minute path
cases fail the literal frozen-fit measurement-envelope check, without hiding their
exploratory predictions. The first-20 fit's maximum violations are about 0.160 and
0.146 pp respectively. Some two-hour projections also introduce coordinates absent
from training. These are additional qualifications, not an excuse to omit losses.

At five minutes the frozen cost fit is zero after broad flat-meter envelopes.
Although envelope distance is zero, displayed-delta MAE is 0.232 pp and signed
accumulated error is −42 pp over the 181 disjoint targets. Zero-width raw path
ranges intersect every measurement envelope but contain none completely. This is
not proof of free work or calibrated certainty. At 15 minutes, paired raw path
width is 0.710/1.294 pp for 30m/2h contexts; intersection is 91.3%/93.8%, while
containment is 0%/2.5%. At 30 minutes widths are 0.860/1.464 pp, intersection
80.6%/88.9% and containment 0%/2.8%. The gap illustrates why intersection must not
be called latent coverage.

Older largest-weekly-cohort strict replay also favors direct pace: direct versus
best path-form error is 0.0408 versus 0.2061 at 15 minutes (127 matched), 0.1364
versus 0.4312 at 30 minutes (86), and 1.3793 versus 4.1924 at two hours (24).
One separate five-hour cohort favors a two-hour-context challenger, but on only
three matched targets; it does not establish a release win. Reconstructed replay
is separately retained and does not override collection-qualified evidence.

**Decision:** keep the joint-path challenger in explicit research and retain the
corrected direct baseline. Do not change the released context/default or promote
raw resampled quantiles. These fixed-prefix results do not reject every future
joint approach: FORE-005 still owns horizon-specific/rolling-origin selection and
prospective shadow evidence, including whether a different justified training
policy is useful. Planning can show useful conditional native work/TT while live
quota answers continue through the supported direct route.

### Independent path proof and reproducibility

An independent Python decimal implementation reconstructed one private native
frame with 187 source rows, 24 source bins, 64 paths and 128 future steps (a seven-
minute horizon, including fractional final bins). It reproduced the BinaryWriter-
compatible input hash, resolved seed, every xorshift32 draw, contiguous run, joint
amount, unknown mass and path/ensemble conservation, with zero mismatches.
No supplied research script was executed to obtain that proof.

- Frame: origin `2026-10-24T18:08:30.0669341Z`, context 2h, horizon 7min.
- Resolved seed: `2474844970`.
- Input revision: `9996f2a9f24f2e39a01c56eb8d771217250cff9ad2bf29ec93280c55acd60ad3`.
- Private audit SHA-256: `d523df1e5bb480b08a7e56528236eb8fa2f36496bac4f9149fce4e5243e5ef72`.

Full private report SHA-256 values, including read-capture time:

- Month reconstructed: `a74bbec68077632b792a75f7fc9b232f1eaaf21d44bc8cfcb8306437ac8af5f5`
- Month qualified: `caf063e56683967ed1b471c932a9b9d5b0e447a00b10f4ae1cd02cec8512daed`
- Recent reconstructed: `cca6cf251549d24820b6da27e528b93569f3b5c77fdc7a21a5f5e1de388fe485`
- Recent qualified: `1fb13d616c38ed7f27207ca66bd644fe55e2f2919aa7c409cd3d27b70bca8e77`

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation -c Debug --no-build --no-restore -- --joint-path-strict "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db" 2026-10-22T00:00:00Z 2026-10-28T16:40:00Z
dotnet run --project tools/TajsTokens.ForecastEvaluation -c Debug --no-build --no-restore -- --joint-path-audit "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db" 2026-10-24T18:08:30.0669341Z 2 0.11666666666666667
```

`--joint-path-study` produces reconstructed replay with the same database/range
arguments. Audit output contains private numeric clocks/model labels but omits
account/session/path identifiers and content; review before sharing. All commands
are read-only. Model/version, source support, raw path spread and residual calibration
remain separate, as do installed proof and user presentation acceptance.

The final full Core suite passed **877 tests** (2m 16s). Windows build passed with
zero warnings/errors (1m 23.04s), installed/restarted PID 17484 and created backup
`20261028T182013742Z-520f4556`. The installed recent strict replay reproduced all
32 score records and cache statistics exactly; its seven-minute frame reproduced
the audited ensemble exactly. Installed Core SHA-256 is
`3ed053416dd81e54fc76173460cc666eebf6ce4e4e4a0fa4e22ac983f082876d`;
app assembly SHA-256 is
`a5e9352af3fafec041d87ba6a1a9ac2da65d50691b7c0f1d36582700405db80f`.
Installed evaluation assembly SHA-256 is
`7548f2e0c06bcb158765bf53384425672d337191def517b04803fd1a6d46439a`.

An additional independent check matched an actual 30-minute nominal replay target's
full-precision origin and actual elapsed horizon (`0.5769122712222222` hours).
Its input revision is
`61d02b6fdf0be6216f2576822125de28b942079c472b21ebc2d98d86b24df573`.
Using the separately retained frozen numeric coefficients reproduced point cost
`1.550675395548618` pp, raw P10 `0.8693119035169811` and P90
`2.2361941708603874`, all within `1e-8` pp. Converting the mean joint vector agreed
with the mean of converted coherent paths. No source measurements were rewritten;
PowerShell automation must preserve round-trip timestamps when passing parsed
JSON dates to a native executable, since default string conversion loses fractions.

This slice adds explicit research commands and an immutable workload-path helper.
It changes no dashboard flow or selected live algorithm; Stage 5's presentation
acceptance remains separate from the forthcoming Planning/tray integration.

## Recent-work Planning integration (FORE-003)

The existing Planning tab now starts with **Recent work** and retains **Session
counts · comparator** as a second mode. Duration, model/effort suggestions and exact
new names remain editable. Recent mode uses an explicit 30-minute/two-hour context;
two hours remains the initial choice. Counts and the independent 1–30 day lookback
continue to control the existing count scenario, including its chronological
selection and support checks. The recent-pattern button restores the native mixture
in recent mode and fills classified active-session counts in comparator mode.

`ICodexIntelligence.PlanRecentWorkAsync` reads only the bounded local ledger context,
without account or work-history filters, and uses the existing continuation and
reference owners. The result includes the origin/context receipt, original frozen
TT assessment, optional same-token revaluation and two independently scoped quota
answers. No new collector, persistence schema, observations, forecast model or
automatic research promotion is introduced.

Model/effort edits preserve all five category amounts for reference revaluation.
They neither predict how many tokens a different model needs for the same task nor
assign an effort multiplier. Unknown tariff coordinates remain partial; positive
coordinate overflow prevents relabelling the aggregate as a chosen model. Original
observations and saved TT calculations remain unchanged. Future conditional work
has only a current snapshot-reference valuation, never a fabricated date-effective
price or billed amount.

The quota answer is explicitly separate: continue a matching fresh provider
account's supported current pace at a constant rate for the requested duration.
Its calculation receipt must match the current account/source/plan/window anchor.
Flat, stale, missing, ambiguous-account and incompatible inputs remain unavailable;
local work/TT can still be useful. Conversely, missing local work does not suppress
an independent supported quota answer. A model/effort edit withholds edited-task
quota fit. No uncertainty band is transferred from a different horizon/reset
target. Reset-crossing sessions retain conditional consumption but make no current-
window fit or post-reset capacity claim.

Results use short native/TT and window rows, with clocks, price references, pace
receipts and assumptions under **Plan details and assumptions**. Edits, account/
quota-anchor changes and reference actions invalidate asynchronous results. Mode,
context, comparator inputs and disclosure state join the existing lightweight
hidden-dashboard restoration; no result graph/page is retained after unloading.
Actual layout and hide/restore behavior still require the user's manual check.

### Fractional conditional amounts

The native-frame check exposed an arithmetic refusal at five minutes: independently
scaled finite decimals could need more precision when the exact reference scorer
summed them. `v2-finite-decimals` therefore represents conditional category amounts
with at most nine fractional token digits, reduced at extreme magnitudes to retain
decimal summation headroom. Reported projected totals are the sum of these finite
categories. This changes only the new explicit continuation representation; native
observations, exact reference arithmetic, the existing scalar projection and the
contiguous-block sampler remain unchanged. It is representation rounding, not
predictive uncertainty or a change in tariff weights.

An independent Python decimal check reconstructed 16 native-frame cases: both
contexts, 5/15/30/120-minute horizons and unchanged/changed-model reference cases.
It matched recorded/projected amounts within `1e-6` token, original/revalued TT
within `1e-10` TT and the declared synthetic constant-pace fixture within `1e-9` pp,
with zero mismatches. The fixture quota lane is deliberately synthetic and is never
assigned to the native workload. The source frame is the same private 187-row
frame documented above; the approved frozen basis is unchanged. Targeted tests also
cover extreme native magnitudes, late/unknown collection, missing/zero/incomplete
work, partial tariffs, overflow, account isolation, actual production pace receipts,
stale/absent windows, cancellation and reset crossings.

The final full Core suite passed **893 tests** (2m 23s). The Windows build passed
with zero warnings/errors (1m 14.72s), installed/restarted PID 25524 and created
backup `20261028T190010076Z-5b694bcd`. All 16 installed native-frame/synthetic-meter
cases match the build exactly. Installed Core SHA-256 is
`2794a69be93d5a4c1647ff07fca0598072a8d66031a69ced388e5398457a5753`;
app assembly SHA-256 is
`40f38ba54b2f5b17370663dacbe134d1705dde44c917bb42cccce993b9fe45e5`.

Rerunning the two fixed collection-qualified context comparisons preserves all
availability, target and block/reset support counts (24 older / 8 recent score
rows). Relative to the retained v1 reports, maximum native-token MAE differences
are below `1.4e-8` token and signed accumulated-error differences below `2.1e-7`
token; WAPE/share and effective-size differences are numerical rounding only.
The context evidence and research-only challenger decision remain unchanged.
Manual Planning rendering, edited-model results and hidden-input restoration have
been requested and remain separate acceptance checks.

The user subsequently passed Planning rendering and the changed-model reference
check. The separate hidden-input restoration check remains pending.

## Shared product status (FORE-004)

`codex-product-status/v1` extends the existing intelligence snapshot with compact
retained measured work and bounded current change reasons. One pure projection
supplies Overview, Planning and tray: reported allowance, conditional reset/runway
outlook, native tokens, frozen TT support and source freshness. Existing forecast
selection and numeric estimates remain unchanged. Details retain calculation
receipts, fixed-horizon diagnostics and local-nowcast explanations; no generic
confidence number is added.

Overview/Planning preserve the selected history range and its exact/bounded/partial/
unavailable TT assessment. The tray is explicitly **All retained local work**,
including undated amounts. Its quantities come from the existing native model/
category aggregates; model/category pairings remain intact, while effort is unknown.
An effort-dependent basis therefore can remain partial. No hourly marginal is used
to invent joint model/category work, and no account is assigned to local totals.
Fallback data does not masquerade as native measured work. Missing native evidence
and an explicitly empty native generation remain distinct.

The intelligence engine caches the retained TT assessment by native array identity,
source/quality and frozen basis ID. Context-only updates reuse the assessment;
changed amounts/labels, restored source arrays or a changed basis regenerate it.
Current reads use in-memory telemetry and the selected reference cache, without a
ledger/training read. The existing reference owner publishes its cache on startup
and explicit/read actions, not a new polling loop. Price-only imports do not change
frozen TT. The current manifest also names the selected basis. There is no new
durable schema, collector, service process or history retained for status comparison.

Source freshness uses the actual successful source-scan clock, separately from
last token collection and selected-query read time. Age can withhold a current
outlook without deleting retained amounts. Stale/flat/invalid/unreported windows
remain explicit; future or mismatched forecast anchors are withheld. Multiple
accounts cannot supply a mixed-window conclusion. Account filters preserve current
scope while work-history filters do not allocate account quota.

The engine retains only the latest compact comparison frame and its last change
receipt. Reasons distinguish a changed provider reading, a new account/source/plan/
window or reset, recalculated pace/runway, native retained revisions, source health
and TT basis/coverage changes. A local total change is not labelled account burn;
a reset difference is not consumption; a TT revaluation is not a provider policy
change. Account-specific reasons follow account selection. No automatic regime
alert or causal provider-change claim is introduced.

Normal pages show compact pace/range and change rows; methods stay behind **Current
status and reasons**. The tray's existing menu adds at most six read-only summary
rows, **Plan a session** and **Why this status?**, both routing to the existing page.
Tooltip length still follows the native shell limit; the full menu/details preserve
information that cannot fit in a hover label. Native icon ownership/recovery remains
unchanged. Physical interaction and visual acceptance require the user's host check.

### Native numerical check and limitations

A read-only transaction over the app-owned active native table compared the compact
13-model projection with independent row-wise arithmetic over **98,062 retained
increments**. Both produced **14,554,192,603 reported native tokens**, including
undated amounts. Five source rows had category/total differences, represented by
four inconsistent model aggregates. The strict TT scorer consequently withheld a
whole score; the native total remained available. Older unsupported model weights
are also present. This is an observed scope/quantity limitation, not permission to
replace reported totals with category sums, discard legacy work or invent prices.
It does not invalidate a separately qualified selected-history TT score.

The probe deliberately makes no live collector-freshness claim. Its private numeric
report SHA-256 is
`85cedc90dd934af38007897f534e95b12f578d0f1e653743d59cbe51565198c0`.
It exports only aggregate numeric/status evidence, not source paths, session IDs,
content or credentials. Synthetic tests separately cover model/category pairing,
effort-dependent partial support, empty/missing/fallback generations, stale scan
clocks, filtered history, account boundaries, same-basis metadata repairs, reset/
runway reasons, cache reuse/regeneration and reference-cache notification ordering.

The final full Core suite passed **905 tests** (2m 4s); the Windows solution build
passed with zero warnings/errors (15.16s) using `-p:DogfoodEnabled=false`. Source
Core SHA-256 is
`ef322c8b7c5d9f96bc35ce70578581377f087d9287bbc916ced6535123fbf785`;
app assembly SHA-256 is
`ee50bf86e9ac770cbcfc1e07759a87dbb9f4ef97b08edf9f4fe828158cd46700`.
No installed runtime or presentation claim follows from this isolated build.
At this isolated checkpoint the daily app remained PID 25524 with Core SHA-256
`2794a69be93d5a4c1647ff07fca0598072a8d66031a69ced388e5398457a5753`.
Its Planning rendering/model-edit check passed; hide/restore is still pending.
The status/tray slice then awaited installation and a physical host check. It was
subsequently installed with the journal slice below; physical acceptance stays open.

## Prospective computation journal (FORE-005)

`quota-forecast-computation/v1` preserves closed numeric computations through the
existing compact forecast owner and detailed calibration worker. It is separate
from `forecast_snapshots`, whose upsert and historical-recalculation semantics cannot
establish what existed before a target matured. The journal is not a second forecast
selector or a replacement for current forecast history. No old history is backfilled.

Intelligence component 12 adds `quota_forecast_computations`. Each immutable payload
contains the meter cohort, reset, original/collection/anchor/generation clocks,
numeric anchor, policy, input hash, existing pace receipt and exact computed outputs.
Selected reset and fixed-horizon outputs retain point/band values, support/counts,
numeric inference and conditional exhaustion fields. Source JSON, prompts, tool text
and credentials are not retained. The input hash covers the available typed quota
prefix; it is not a claim of complete account history or a recoverable copy of inputs.

Four shadow direct comparisons at 5/15/30/120 minutes reuse
`QuotaPaceModels.ProductionFallback` on the same compatible epoch. These do not change
the released fixed-horizon selector, choose a different continuation context or
promote joint paths. Short prefixes, flat meters and reset crossings remain explicit;
a flat comparator is a persistence-like benchmark, not measured zero consumption.
Direct comparisons have no invented interval band. Existing selected horizon outputs
retain their own models and empirical intervals separately.

The repository stamps insertion after taking the SQLite writer lock. It then commits
the immutable output before sampling and recording a one-time confirmation clock.
Only that post-commit confirmation can qualify an output as before-target. A crash or
cancellation between transactions leaves an **unconfirmed** output; retry confirms it
at the actual retry time. Equal retries preserve the first payload, insertion and
confirmation; changed inputs/outputs have a new content-addressed identity. Corrupt
identity collisions preserve the original and refuse replacement. Future/reversed
clocks, late confirmation and unconfirmed records have separate classifications.

This proves a **computed output existed before its target**, not that it was shown
or a warning delivered. A slow worker can produce an output for an obsolete anchor;
existing publication guards still reject that result. Before-target recording alone
also does not prove a full nominal-horizon warning lead: actual confirmation lead and
generation delay must be used. The first live five-minute comparison had only about
**4.49 minutes** of issue lead. Overlapping outputs are not independent outcomes.

Payloads are bounded to 128 KiB and 16 points, with finite numeric validation. Reads
use forward `(recorded time, ID)` pagination, at most 1,000 entries/page; the explicit
`--forecast-journal` report caps the aggregate at 10,000 and exposes truncation.
Reads use a read-only connection, never initialize/migrate/backfill, alias cohorts and
export no raw identities or payloads. Unsupported formats/checksums stay visible.
No automatic age expiry, extra source/network polling or separate runtime is added.

### Validation and installed evidence

Ten targeted regressions cover direct-fallback parity, sparse/flat/reset-crossing
states, immutable variants, concurrent retry/reopen, actual recording/confirmation
clocks, interrupted confirmation, cancellation, pagination, scope, corrupt identity
refusal and migration without relabelling history. Both compact and detailed owners
record their outputs; the compact path needs no workload-training read. Existing
schema-refusal and original-record tests remain in the full suite.

The full Core suite passed **915 tests** (1m 56s). The Windows solution build passed
with zero warnings/errors (1m 00s), installed normally and started PID **43816**;
the app had been stopped beforehand. Backup is `20261029T051907040Z-df6aa7b1`.
Installed assembly SHA-256 values:

- Core: `ad8ca0bfbde6228830fe5c07d2071b102d4dae617acd33af6db1ab19e38cbb5d`
- Infrastructure: `05e304e7d226406b11d464439e987204d47c3556c3bd9bc1c4c25b0c15e42593`
- App: `5c1a48ec415bec3940d44a8d8017d956004941475bce15914ee21fa795e6066b`

Fresh provider ingestion advanced to October 29. An independent read-only Python
check of the first **two** journal computations verified both content identities,
ten before-target outputs and eight direct-pacing calculations with **zero numeric
difference**. It compared the original tables against the closed deployment backup:
all six research evaluations, one saved TT result, one selected reference and one
price snapshot remained byte-equivalent. This does not claim every native projection
is unchanged or close the wider migration/runtime gates.

The sanitized independent aggregate report SHA-256 is
`3841a7b9bcf80b00d3e70d6e4a4a5aa255fa2b2e6c19a8ef9a2cdfc4dfa62302`;
the installed journal availability report SHA-256 is
`38b56ec5d32d2021992fc6f41a09aee6c2f9972e2e739aa79adbcb08c3e9b2f4`.
The reports contain aggregate numeric/status evidence only. No visual acceptance is
claimed; pending host checks are listed in PROJECT.md while the user is away.

**FORE-005 remains open.** The following scorer adds recorded-outcome evaluation;
fair rolling-origin joint/direct comparison and broader prospective evidence remain
next work. Reconstructed history, collection-qualified replay and genuinely recorded
outputs stay separate. Recording success alone does not select a model, establish
warning delivery or demonstrate predictive accuracy.

## Recorded forecast outcomes (FORE-005)

`recorded-quota-evaluation/v1` evaluates immutable journal outputs without refitting,
selecting or extending a prediction to a delayed target measurement. The recording
range's end is a fixed availability cutoff. Source collection after that cutoff cannot
improve the report; future confirmations and immature targets remain explicit.
The existing read-only quota dataset owner supplies original measurement/collection
clocks and selected source membership, without reading token/workload datasets.
Its quota-only route also avoids training-schema dependencies. A preceding reported
window is read so reporting boundaries do not restart a reset-jitter or plan history.

Labels must retain the anchor's source/account/bucket/plan/window and compatible
reset generation. Existing bounded one-second reset jitter applies to the whole
range; transitive drift cannot merge generations. Invalid readings, same-time
conflicts, decreases and returning plan regimes remain boundaries. Equal copies
add neither a boundary nor an independent interval. Revised/missing anchors and
unsupported points stay separate from measured zero. Installed observations include
sub-second reset jitter; exact reset-string equality is not the compatibility rule.

Fixed targets use the first compatible measurement at/after the original target,
within `min(5 minutes, 20% of horizon)`. A late/unknown collection cannot be replaced
by a more convenient later label: collection must be known, no earlier than the
measurement, within five minutes and by cutoff. Reset outcomes use the last
within-five-minute pre-reset proxy. These are displayed-meter targets, not exact
latent consumption or a provider lag/precision guarantee.

The report separates computed point/band availability, before-target availability,
matched targets, ordinary-loss outcomes, saturated targets and withheld reasons.
Saturation stays counted but is excluded from ordinary error and band coverage.
Signed remaining error is positive for optimistic predictions. Absolute/squared
errors use their stated matched denominators; accumulated signed error uses only
non-overlapping outcomes from the existing chronological-block owner. Six-hour block
counts describe spacing, not proof of independence. Selected/direct errors are paired
only within the same computation, target, reset-lead group and ordinary outcome.

A missed displayed burst means at least 2pp observed movement with under 1pp
predicted; these are declared reporting thresholds rather than tuned precision
assumptions. Fixed-horizon negative threshold labels require a compatible positive
remaining reading at/after target. A first saturation report after target cannot
prove crossing by target. Negative reset labels require a reading exactly at reset;
a pre-reset proxy cannot establish survival. Positive labels require an actual
timely saturation report by target. Warning lead uses the actual post-commit
confirmation and first timely saturation report; it can be negative and does not
bracket physical crossing under unknown backend lag. These are computed threshold
comparisons, not evidence of a delivered warning, execution stop or reset capacity.

### First fixed prospective outcome check

At **2026-10-29T05:35:35.1734214Z**, the journal had 15 compact computations with
75 output rows. All 15 content identities and 75 stored output values were checked
independently. Eleven original targets matched timely outcomes; independent Python
arithmetic reproduced point/signed/squared error and non-overlapping sums.

| Horizon | Computations | Ordinary matched outcomes | Non-overlapping outcomes | Signed mean remaining error | MAE |
|---|---:|---:|---:|---:|---:|
| 5 minutes | 15 | 10 | 2 | +0.252484pp | 0.446229pp |
| 15 minutes | 15 | 1 | 1 | +0.613824pp | 0.613824pp |
| 30 minutes | 15 | 0 | 0 | unavailable | unavailable |
| 120 minutes | 15 | 0 | 0 | unavailable | unavailable |

Thirty-minute, two-hour and reset targets were immature at this fixed cutoff.
There were no recorded detailed outputs there, so no paired selected/direct
comparison, interval-coverage result or reset-warning accuracy can be claimed from
this sample. The compact points have no invented band. This early sample is
prospective evidence, not a context/default selection or an accuracy guarantee.

A later installed cutoff, **2026-10-29T05:52:36.882744Z**, contains 31 compact
computations: 25/16/2 ordinary matches at 5/15/30 minutes, with only 5/2/1
non-overlapping outcomes. The thirty-minute MAE is 1.732648pp over two overlapping
outcomes. One nominally mature target per fixed horizon still lacks a compatible
reading at that cutoff; it is withheld rather than assigned zero. Two-hour/reset
targets remain immature. This later report is a progression check, not a new
independent numerical validation or a basis for changing defaults.

### Scorer validation and installation

Eighteen focused regressions cover fixed-output/delayed-label behavior, same-target
selected/direct pairing, point/band/issue availability, ordinary versus saturated
loss, threshold timing, actual confirmation lead (including negative lead), no
pre-reset survival inference, overlap-safe sums, unknown/late collection, cutoff
leakage, account/anchor mismatch and bounded reset/plan/conflict/decrease semantics.
The quota-only reader also works with token and training sources deliberately made
unavailable, retaining original clocks. A separate base-database regression exposed
and repairs its old dependency on `rollout_files`: quota-only operation now works
without an Observatory store, while unverified non-null native memberships stay
excluded. The full Core suite passed **933 tests** (2m 9s). No production model/
continuation policy or durable schema changed here.

The Windows solution build passed with zero warnings/errors (53s), installed
normally and restarted the daily app as PID **28504**. Backup is
`20261029T060159687Z-f7710311`. All **40** pre-deployment journal computations retained
their exact payload/hash, insertion and confirmation timestamps. Installed hashes:

- Core: `9cfdccc424901edc9d88df806f694cefffe093bc455c51fff7bb89ba55066cf5`
- Infrastructure: `905fce555775258fc841e19445439609c99ed1ba26df65e8d3dd1f9d43ec9fd3`
- App: `dae62e2640264c5dd024704437c118097b25e67cbbf9480e40d9fa774002d5dd`

The installed fixed-cutoff audit reproduced byte-for-byte on a second fresh read.
Its SHA-256 is `68b4a7ec9611ec7b8de9802dadb1b69ec5f8b207000e5b1c558774dd2c2cf699`;
the independent aggregate proof is
`61a725824f70c1df2c7caec0b5c5d596327996374b6584c4b074d7b2b555442d`.
The later-cutoff report is
`c2a354eb2a7fb48720c8a52ffe6ab8bb571bd02a2756cdf1d3c3de3607dd95e8`.
These reports contain aliased numeric/status evidence, no raw account/source IDs,
paths, source content or credentials. Future reports can legitimately differ when
the cutoff or selected retained evidence changes; a fixed-cutoff check is not a
claim that later interpretation repairs cannot revise an outcome assessment.

The following comparison supplies rolling-origin joint/direct evidence; broader
qualified prospective evidence remains open. This scorer supplies descriptive issued-output
metrics; it does not choose a winner, supply missing long-horizon/reset truth or
close physical alert/visual acceptance. Host checks remain listed in PROJECT.md.

## Rolling joint comparison (FORE-005)

`rolling-joint-comparison/v1` reuses the existing cost-interval, nonnegative ridge,
joint projection, block-path, pace and calibration owners. Research orchestration
lives under `Core.Research`; product services do not depend on it. The original
frozen-fit comparison and its artifacts remain separate.

### Targets, availability and selection

Each prediction uses its **nominal** 5/15/30/120-minute duration. A tolerated later
quota poll supplies the observed label without extending projected work. These are
ordinary displayed-meter outcomes, not exact latent costs. The existing builder
excludes saturated/invalid/cross-boundary intervals; report availability is
conditional on those constructed targets, rather than measured live abstention.
The prospective journal measures actual retained issuance separately.

Reconstruction uses event clocks. Collection-qualified replay uses known collection
times as effective quota clocks, retaining original measured times. Unknown,
negative or over-five-minute lag creates a boundary. Training must have ended and
its token evidence must have been available by the origin. Unknown collection
never turns late workload into timely free work. Same-meter plan/reset/conflict/
decrease boundaries remain owned by the existing quota policy.

Eight fixed candidates are compared: direct fallback, released head, frozen
first-20 joint/two-hour context, expanding category and joint fits under 30-minute
and two-hour contexts, and a two-hour contiguous-block joint-path challenger.
Vocabulary/scaling use training data only; expanding fits use earlier matured
same-cohort/horizon rows. The existing 20-row fit floor remains an engineering
comparator, not a universal sample-size guarantee. At most 2,048 intervals per
cohort/horizon are examined, with source/examined counts reporting truncation.

The last fifth of each cohort's retained **timestamp span** is final holdout;
quota values and target eligibility cannot move that split. Development labels
must mature by the split. Boundary-overlapping labels cannot choose a policy.
The development choice is then fixed. Expanding fits may learn from earlier
matured final outcomes under that fixed rule; final results cannot tune the choice.

Raw candidate availability and full policies are reported separately. A candidate
policy keeps the released head when composition is unavailable, and is scored on
the full head-supported target set. It cannot improve by dropping difficult targets.
Selection requires at least 16 actual composed, non-overlapping outcomes in eight
six-hour blocks, more than 0.1pp and 10% block-balanced envelope-loss improvement
over both direct and released controls, and a majority of winning blocks. Richer
joint/path candidates must also beat the matched simpler parent policy. These
inherited support/margin floors are engineering gates, not proof of independence
or a complete production-promotion policy. No automatic live promotion occurs.

Signed remaining-error sums use non-overlapping outcomes. Point availability,
paired MAE/envelope loss, band availability, coverage and width retain separate
denominators. Residual calibration uses earlier emitted clipped predictions and
the existing completed-block owner. Raw block-path P10/P90 remains conditional
spread; it is not a calibrated predictive band or an identification range.

### Fixed native studies

Both ranges were run in reconstructed and collection-qualified modes:

| Range (UTC) | Input quota rows | Qualified-clock rows | Constructed intervals | Cohort/horizon groups | Truncated groups |
|---|---:|---:|---:|---:|---:|
| October 22 00:00–October 28 16:40 | 15,950 | 6,110 | 488 | 4 | 0 |
| September 27 00:00–October 27 14:58 | 29,663 | 14,794 | 2,981 | 12 | 0 |

**Every development choice retained the released head in all four studies.**
Recent development support was sparse; the month study also had sufficiently
spaced Weekly evidence, without a candidate earning the required full-policy
improvement. This supports retaining the incumbent for these fixed comparisons,
not a universal claim that composition cannot help.

Recent collection-qualified final outcomes illustrate why raw availability and
full-policy comparisons must stay separate:

| Horizon | Head-supported outcomes | Raw expanding joint/two-hour outcomes | Head MAE | Joint policy with head fallback MAE |
|---|---:|---:|---:|---:|
| 5 minutes | 229 | 198 | 0.206350pp | 0.196510pp |
| 15 minutes | 90 | 69 | 0.442804pp | 0.456891pp |
| 30 minutes | 45 | 34 | 0.678261pp | 0.652988pp |
| 120 minutes | 3 | 0 | 1.306472pp | 1.306472pp |

Final improvements cannot retroactively choose a model. In the month study's first
four Weekly horizon groups, the same full joint policy has MAE
0.250000/0.607599/0.825447/1.691870pp versus head
0.250000/0.543469/0.550119/1.903222pp over 12/59/34/14 outcomes at
5/15/30/120 minutes. Five-hour and other Weekly cohorts stay separate; they must
not be pooled into an apparent account-wide accuracy or independent sample count.

Eight regressions cover growing/frozen fits, nominal horizons despite delayed polls,
future-data invariance, collection-clock qualification, unavailable token training,
honest head fallback, parent/support gates, mature residual bands, raw-path spread,
zero work and sparse history. Independent NumPy arithmetic reproduced 257 sampled
category/joint fits (maximum absolute difference below 7.2e-15), 50,720 nominal-target/
training checks, raw/policy scores and development/final-policy decisions across
the four studies. The full Core suite passed **941 tests** (2m 8s).

### Prospective maturation

At **2026-10-29T07:37:22.3758837Z**, the installed journal scorer read 128 compact
computations. Ordinary matched targets at 5/15/30/120 minutes were
123/114/100/15, with only 25/8/4/1 non-overlapping outcomes, all in one six-hour
block per horizon. MAE was 0.350486/0.554487/0.547012/1.677254pp. The two-hour
signed mean error was +1.677254pp, an optimistic remaining-quota error. One mature
target per horizon lacked a compatible label; it was withheld. Reset targets were
still immature. There were no detailed selected/composed pairs or banded outputs
at that cutoff. More overlapping readings do not establish an independent win,
calibrated warning probability or delivered-alert behavior.

Independent arithmetic verified all 128 journal content identities, 640 frozen
output rows and 352 matched targets/errors at this cutoff. The report SHA-256 is
`8acb76ea036cf83b07cc5ddd119c57633270c963e7e78178ebfe59d8293bc3d2`;
the independent aggregate proof is
`0e5f52159cbfecefdb335f927ba088e91af63c8bbe6abc4aac39632634061732`.

### Installation and reproducibility

The Windows solution build passed with zero warnings/errors (53s), installed
normally and restarted the app as PID **46768**. Backup is
`20261029T074148631Z-9da5fc40`. Its pre-commit build identity is
`1.0.0+8fb9dd588622113019d1cf7716047ada5fc070ad.dirty` at 07:42:09 UTC;
the assembly hashes identify the validated source patch. Fresh quota collection
advanced to 07:44:29 UTC and forecast confirmation to 07:44:31 UTC after restart.
Installed Core/Infrastructure/App/tool assemblies
match the normal publish output:

- Core: `6984f81349ce4b36e7fa932b5500ae75b3d5f91ffa46fe663a2615cdbca22f5c`
- Infrastructure: `116938f4cc3493f27586c09e4101fc9de581fbf098875efcb49df0cb3278e73d`
- App: `707be11a8a688b7d0fc825c2bf63199e5904bfdc790f0ac88e9b6ad36262af87`
- ForecastEvaluation: `0820bfdc1536934ecbe486aa1e7bc2e27cf078ef6cae761cafc70d57d9c364b7`

The installed tool reproduced both the recent collection-qualified audit and the
fixed prospective report byte-for-byte. Frozen study artifacts under
`.codex/temp/stage6/` have these SHA-256 identities:

| Study | Native audit | Independent aggregate proof |
|---|---|---|
| Recent reconstructed | `281a89c480313f2be4d552c650f13596da86beeb036763bfb4177a3eb56f35a2` | `a4c314161cc862e5c5f7a87822c540e2be88175c77bacb5015ec172390fc5730` |
| Recent collection-qualified | `d828b74d74aa39a9031e71a682543720ac2afbdbbb9b277579b4258993daf035` | `cd43563a835825bc540fe770b787d65ce66e02cee6a34e5295057a0916e56936` |
| Month reconstructed | `545c9ef138469c8747cdb4f476c17b652b0d2c0ec267ca69fcaf250a6e26474f` | `b8c90970a0bc0e6972c066a55f5f093f2a38c1c843598f488e97a20ba86d4013` |
| Month collection-qualified | `8ad7a3f6fcc85f0a0010284753dc5d313af9afc43d22dedfafd4c4fead43275a` | `db66de5d163beacd3d81de33b7b67f528c7e80e88d78f013d7f87257c44621d6` |

No source acquisition, durable schema, TT weights or released continuation/model
policy changed. FORE-005 remains open for broader prospective support, supported
reset labels and selected/composed prospective comparisons. Physical tray, input
restoration, alerts and resource acceptance remain separate host checks in
PROJECT.md; this explicit research slice adds no UI acceptance claim.

### Detailed recorded outcomes at the October 29 noon cutoff

The frozen live-journal cutoff **2026-10-29T12:00:55.5050559Z** contains 389
computations: 376 compact and 13 detailed, with no invalid entries or truncation.
The Debug-compute and worker-cache fixes allow detailed before-target recordings
to accumulate. These are actual retained outputs, not a reconstructed refit.

| Compact horizon | Ordinary matches | Non-overlapping | Blocks | MAE / signed mean, pp remaining |
|---|---:|---:|---:|---:|
| 5 minutes | 366 | 73 | 2 | 0.32858 / +0.03616 |
| 15 minutes | 362 | 26 | 2 | 0.53220 / +0.06421 |
| 30 minutes | 348 | 13 | 2 | 0.33800 / +0.08648 |
| 120 minutes | 263 | 3 | 1 | 0.88425 / +0.02228 |

Detailed selected/direct 30-minute outputs have **seven matched ordinary pairs**,
only **two non-overlapping outcomes in one block**. Both use `time-ewma-2h` and
have MAE **0.532195pp**. The selected outputs' signed mean is +0.299603pp remaining;
positive means optimistic remaining quota. All seven selected bands contain their
reported outcomes, at mean width 2.002521pp; seven overlapping results in one block
do not validate nominal 80% predictive coverage. Detailed two-hour/24-hour/reset
targets are still immature at this cutoff. No composed challenger is selected in
these pairs and no delivered-alert or policy-improvement claim follows.

Independent read-only reconstruction verifies all 389 stored content identities,
1,984 original output rows, 1,375 compatible target matches and error/non-overlap
summaries. A separate arithmetic check reproduces 13 score groups' block/band
metrics and the seven selected/direct pairs. It does not prove complete account
coverage, backend precision/lag, timely nominal-origin issuance at every horizon
or population calibration. Before-target recording remains distinct from each of
those claims.

Local evidence is in `.codex/temp/stage7/worker-completion/`:

- Native report: `recorded-after-admission.json`, SHA-256
  `b878df7529f83b490f32169cd126a7a57ef283788d4ec6e4033fd74bc2c737ae`.
- Target/identity proof: `recorded-after-admission-independent.json`, SHA-256
  `e0568c367697f46249a3367bb1c27a4414e9df341457a7c4d1e8fff6a5fe70d6`.
- Summary/pair proof: `recorded-after-admission-summary-independent.json`, SHA-256
  `dd0a83d70c91af354799c01e75885973d45d05aa44df8430641dec6d9693ddd6`.

FORE-005 remains open for broader chronological support, mature reset/long-horizon
labels and supported selected/composed comparisons. The released selection policy
and continuation defaults remain unchanged.
