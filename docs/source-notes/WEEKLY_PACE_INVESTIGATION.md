# PACE-001: weekly pace depends on rounded-meter poll count

Status: reproduced; live estimator policy unchanged. This is an open fallback limitation, not a validated change in workload.

## Retained evidence

The two readings reported by the user are on 27 October 2026, Europe/Warsaw (UTC+1). Read-only queries of the app-owned quota and forecast tables identify one source/account/plan/limit/window cohort throughout. The source is `codex-app-server:codex`, the weekly window is 10,080 minutes and its reset is 2 November at 14:52:34 local time. No reset, account, source or plan transition occurred between these anchors.

| Local quota reading | Used / remaining | Active-segment samples | Segment hours | Persisted estimator | Burn pp/h | Sustainable pp/h | Reset projection |
|---|---|---:|---:|---|---:|---:|---|
| 16:06:25.0918163 | 45 / 55 | 141 | 16.301900 | legacy-ewma | 13.971596243 | 0.385233707 | Exhaustion at 20:02:36 local; 0% at reset |
| 16:23:07.1680435 | 45 / 55 | 157 | 16.580254 | legacy-ewma | 0.140031661 | 0.385989945 | SafeUntilReset; about 35.05% left |
| 16:23:26.9895784 | 45 / 55 | 158 | 16.585760 | legacy-ewma | 0.105023746 | 0.386005185 | SafeUntilReset; about 40.04% left |

Both 16:23 rates round to the displayed 0.1 pp/h. The sustainable rate rounds to 0.4 throughout. It is derived from estimated remaining allowance divided by time until reset, so it changes only slightly while the meter stays flat.

The sanitized fixture [weekly-pace-flat-rounded-2026-10-27.json](../../tests/TajsTokens.Core.Tests/Fixtures/weekly-pace-flat-rounded-2026-10-27.json) retains the 158 active-segment timestamps/meter values, their native reset timestamps (including one-second jitter) and three original forecast anchors. Account identity and plan labels are replaced; no raw source payloads, paths or chat content are included. Replay with the production code reproduces the rates, reset margins, observation counts, observation spans and estimator selection.

## Calculation and fallback path

The runtime path is `TelemetryCoordinator.RefreshBackgroundForecastsAsync` → `SqliteIntelligenceService.BuildBackgroundForecastsAsync` → `QuotaPredictionService.BuildResetOutlook` → `QuotaForecastCalibration.SelectModel` → `QuotaPaceModels.Estimate`.

The service reads at most the last 10,000 observations from the same provider/profile/source/account at or before the live anchor, filters the full cohort and preserves segmentation boundaries. The active segment starts at **26 October 23:48:18.2529502 local**, at 14% used. It spans an overnight collection gap; its full duration is not a continuously observed active-work duration.

At these anchors, the selected policy is `quota-walk-forward/v4`, model `legacy-ewma`. The persisted evidence has zero comparable completed-reset calibration epochs and no reset uncertainty band. Reset-horizon model selection uses earlier completed compatible cycles at comparable lead, with the current eight-group floor. Fixed-horizon chronological blocks cannot qualify a reset band. With no qualifying reset comparison, the existing default is `legacy-ewma`.

The retained workload status identifies the compact quota-pace path, and there are no short-horizon workload predictions attached. It is not a workload ridge, composed model, learned TT estimate or token-to-quota conversion. A detailed calibration worker failure is not established by these records; compact quota-only forecasts are an independent normal path.

For weekly quota, the legacy estimator processes each adjacent observation as:

```text
interval_rate = max(0, displayed_used_change / elapsed_hours)
rate = 0.25 * interval_rate + 0.75 * previous_rate
```

The last reading before the jump was **16:05:20.3082135**, at 44% used. At **16:06:25.0918163**, it became 45%. The 64.7836028-second interval is treated as a full 1 pp / interval, approximately **55.57 pp/h**, and gets a 25% EWMA vote. The prior rate was 0.105589301 pp/h, producing 13.971596243 pp/h. This does not establish that the whole one-point consumption occurred within that short interval: the meter is visibly quantized.

There were then **17 unchanged polls** over 17 minutes 1.8977621 seconds:

```text
13.971596243 * 0.75^17 = 0.105023746 pp/h
```

Every unchanged poll contributes a zero displayed interval rate, independently of its duration or whether work continues. The all-flat guard does not apply because the whole segment previously moved from 14% to 45%. That allows the arithmetic classification to change to `SafeUntilReset` even though the flat suffix alone does not establish an active-work burn rate or survival.

## Build/restart contribution

The installed `previous` and `current` build manifests record builds at **16:20:46.4206526** and **16:23:17.0678852** local. Both identify `1.0.0+d7347faec364b34d7b1a81da5101359465fd307c.dirty`; the dirty suffix correctly includes the then-uncommitted work. The existing `QuotaPaceModels.cs` bytes are identical at `a64f3b0`, `d7347fa` and `f09cafe` (SHA-256 `f0b853c5214d1a0162e74db016e3a107dfa5b6a905b3016227d4728347cf87c3`). Retained policy/model fields also stay unchanged.

New startup observations occur at **16:20:56.7782251** and **16:23:26.9895784**. They follow earlier polls by only **25.9668706** and **19.8215349 seconds**. Each flat reading still applies a full 0.75 multiplier. Thus restarts contributed extra sample-count decay, but did not reset an in-memory slope or select a different estimator: the rate was reconstructed from persisted observations. The decline had already crossed to `NearSustainablePace` at **16:20:30**, before the first of these startup observations.

The earlier Overview change made the existing reset outlook prominent instead of the first short-horizon forecast. It did not introduce the per-poll EWMA calculation.

## What the observations support

At 16:23:26, the existing two-hour bracketing interval is **14:22:58.1659316 → 16:23:26.9895784**, from 38% to 45% used. That is **7 displayed pp over 2.008006569 hours**, or **3.486044373 pp/h**. At 16:06:25, its corresponding bracket spans 2.053900519 hours and 7 pp, or 3.408149486 pp/h. These are observed interval averages, not instantaneous or predicted future rates.

The already-implemented elapsed-time estimators provide comparisons:

| Anchor | time-ewma-2h | time-ewma-6h | Observed recent-2h average |
|---|---:|---:|---:|
| 16:06:25 | 3.011880451 | 2.268002578 | 3.408149486 |
| 16:23:26 | 2.613254017 | 2.156152974 | 3.486044373 |

Keeping only the endpoints of the flat suffix leaves both time-weighted estimates unchanged, while it leaves the legacy estimate over 50 times larger than with dense flat polling. That isolates the poll-count artifact without fitting another model or hardcoding a desired rate.

The data therefore does **not** establish either 14 pp/h or 0.1 pp/h as the current active-work burn rate. The observed two-hour account-wide average is well above the approximately 0.386 pp/h sustainable rate. If that observed average continued unchanged, 55% would last about **15.8 hours**; that is a conditional arithmetic scenario, not a calibrated exhaustion ETA. Future workload may change. These account-wide meter rates cannot assign the consumption to particular local work.

The unchanged integer meter does not establish exact zero consumption, and ongoing local work does not supply a quota conversion. In this capture, the retained native token ledger ended at 12:12:22 local, and retained workload observations ended at 12:00:53 local. Their absence during the reported interval is not evidence that the user was idle. The compact outlook does not read either training dataset. A synthetic regression with positively recorded ongoing token work reproduces the same legacy decay, showing that ingestion lag is not required to cause this artifact.

## Regression and remaining decision

`WeeklyPaceRegressionTests` covers exact retained-anchor replay; identical endpoint/elapsed-time comparisons with sparse and dense flat polling; and the production background service with ongoing recorded local work. It also verifies that an entirely flat segment remains unknown rather than becoming a measured zero burn or calibrated survival claim.

These tests characterize the existing defect; they do not approve its survival classification. The live estimator, model-selection thresholds and smoothing policy remain unchanged in this investigation. A corrective policy should remove sensitivity to poll count and preserve rounded-meter uncertainty, then compare later unseen reset-horizon outcomes. Replacing the fallback or presenting a different reset conclusion needs that explicit evidence; a nicer-looking number is not validation.
