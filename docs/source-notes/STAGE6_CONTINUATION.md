# Recent joint-work continuation inputs (FORE-001)

`recent-joint-continuation/v1` extends the existing workload-composition owner.
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
