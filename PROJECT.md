# TajsTokens - Project Design Document

## What is TajsTokens?

TajsTokens is a Codex companion app, that allows for quick overview on how they use Codex, where most of the quota usage went and predict future quota usage.

It's mostly a companion tool, with notifications, tray widget that shows current quota and quick prediction graphs. It's also an app that allows for exploring in more detail how Codex is used.

If TajsTokens could only do three things extremely well, it would be showing the current quota usage, showing the quota usage history and predicting future quota usage.

A person would prefer TajsTokens over Codex's dashboard because it shows more details, allows for quick overview of everything cleanly and has a prediction model.

It's mostly a product for myself, which is also shared on GitHub and might be used by others. But still, it's mostly my personal tool, and my workflow is serving as the first target profile.

## User Experience

Three most important things on launch are: current quota usage, whether the quota is gonna last till next reset and how much quota has been used in the last reset cycle.

Quota is the main visually dominant over token usage, although both are important. If we introduce TT (TajsToken) concept, it should also be as important.

The dashboard might answer the question "Am I okay?" with three markers:
You're good. You're in dangerous area. You're doomed.
The details can be explored elsewhere, but they have to be clearly accessible and fairly explainatory, without requiring a lot of reading and without exposing underlying models and concepts too much.

The "Did OAI nerf usage?" is fairly important and deserves a marker of it's own on the dashboard. It can be a yes, no or "looks weird, but we're not sure, check the details here".

For important numbers, the normal UI should show a small source/freshness label like “OpenAI reported · provisional” or “Locally observed · fresh”, while keeping technical source details hidden unless opened.

### Product Personality

The app should feel professional, dense but discoverable but also allow for "nerdy" exploration.

The important color, is as always purple. It could probably be used with something, but generally the design should follow my Taj's style.

Data density is going to be a problem. Hence we'll mostly try to go for cleaner UI with details on hover and/or click.

Technical terms might have a "why" or "what is this" tooltip.

The app should be as important or slightly less than the tray widget.

Uncertainty language should be somewhat between "still learning" and "insufficient  calibration epochs".

## Source of Truth

The source of truth is TajsTokens database, which collects data from multiple sources. These sources include in order of importance:

1. Json Rollouts
2. Codex live quota meter
3. Codex database
4. Provider analytics
5. Derived models

### Semantics

**Reported** = provider said it
**Observed** = TajsTokens/local source saw it
**Derived** = deterministic transformation
**Estimated** = model inference
**Inferred change** = statistical interpretation

### Conflict handling

The sources may and probably will conflict from time to time. WE (TajsTokens Database) will have to reconcile the data in our own yard. For example, for example the Codex live quota meter reports quota usage is at 50%, but from json rollout we observe that it's at 49%. In this specific case, codex live quota meter is most likely correct.

Provider analytics are probably gonna be the most delayed ones, but also they allow for some more details. They could be used as a supplementary mostly.

If later provider reports are available, we will preserve the original observation, but let the later provider report become the best current interpretation. Never rewrite or delete what we actually observed. But if OpenAI later supplies a more authoritative finalized historical account for the same quantity, TajsTokens can use that as the best retrospective interpretation.
And if they do get updated, we show latest/best value by default, with revisions available on demand.

The principle "Raw evidence is preserved carefully; **product conclusions are allowed to be imperfect and revisable**." is surely correct.

We can accept errors like forecast being off by 5pp, or temporarily misreporting quota usage by 1pp.

### Provider Analytics

New Provider analytics might be useful as supplementary data but shouldn't be taken at face value. Especially for live data - We might believe them after +6h, but still if they differ from our own observations, we should reconcile.
We should use all the ones, that are available and reconcile them with the other sources.

## Quota Intelligence

We can show weak evidence estimates, but they should be marked as such.
I'd rather see "Expected remaining at reset: ~37%, limited evidence" instead of "forecast unavailable".
No estimate should require a formally trained model, but we should aim for at some uncertainty or confidence level.
When evidence is sparse, we should show a range of possible outcomes, rather than a single point estimate. This is especially important for predictions that have high uncertainty.
Learning shouldn't really be a thing permanently. It's okay if it doesn't have any data, but if it has anything at all, we just use it.

### Forecasting

The most important forecasts are most likely the next 15 minutes, remaining at reset, and exhaustion ETA.

Conditional point estimates can learn within a reset cycle. They require compatible, fresh observations, not a trained workload model or a calibrated band. Flat rounded readings do not establish zero burn. Missing account identity, reset information or usable history must be explained specifically.

Fixed-horizon uncertainty uses earlier matured outcomes (non-overlapping errors or chronological blocks, depending on the policy). Reset-horizon uncertainty needs completed compatible cycles at comparable lead; within-cycle blocks cannot establish reset-horizon coverage. These evidence requirements do not hide a supported point estimate. Challenger promotion is a separate decision and preserves the pace baseline when the challenger has not earned selection. Block spacing and evidence/improvement floors are provisional engineering policies, not mathematical guarantees.

The explicit local threshold study varies one engineering choice at a time around immutable released defaults. It reuses existing pace, scenario and composed-selection implementations, reserves the final 20% of candidate origins for comparison, and reports paired error, point/band availability, range coverage/width and non-overlapping support separately. Collection-time availability, account/source/unit boundaries, overlap rejection and assertion-specific safeguards remain fixed. Reset-horizon bands continue to use completed cycles. The command and retained-data results are in [development](docs/DEVELOPMENT.md#forecast-threshold-study) and [source notes](docs/source-notes/FORECAST_THRESHOLD_STUDY.md); no experimental configuration is selected automatically in the app.

### Planning

User should be able to have a "planner", that allows to see whether their current usage will last for: x hours, with y model, with z effort.

The primary Planning tab owns the editable session scenario: duration, optional model/effort, recent-local-work autofill, active-session counts and an independent 1–30 day history lookback. Both windows report conditional consumption and estimated fit against matching fresh account/source/plan/limit cohorts. Unsupported windows remain explicit. A calibrated band crossing available allowance means fit is uncertain; a point-only fit remains conditional. Sessions crossing a reset retain supported consumption estimates but make no post-reset capacity or current-window fit claim. Input edits and quota-anchor changes invalidate results. Forecast/workload history and Model Lab remain separate destinations, not duplicate planners.

## Quota Accounting

The current implementation keeps reported account quota, locally observed native Codex tokens, API-equivalent price weights and inferred quota cost separate. A source account key scopes the quota observations that supplied it; it does not retroactively assign local rollouts to the current login.

### Source time and schema compatibility

Observatory component schema 9 keeps nullable source event time, first collection time and timestamp quality separate for token/context evidence. Missing, malformed or unverified source dates remain unknown. Their retained token amounts can contribute to all-time totals, but do not enter hourly history, recent-work autofill or forecasting inputs as invented event dates. Source timestamps require an explicit timezone or supported numeric Unix representation; mixed aggregates have capture time without a single source event date.

The version-8-to-9 migration preserves original observation/capture values and counters, marking legacy source times `LegacyUnverified` rather than restamping them. State-index schema 7 clears disposable fingerprints/cursor so bounded, prefix-verified ingestion can enrich only unverified metadata under the original occurrence identity. Amounts and first capture times stay immutable; changed prefixes retain the existing generation-replacement policy. Source-owned Codex data remains read-only.

Component versions are independent of the base telemetry database's `user_version`. An older binary must reject a newer component, not lower its version marker. Install compatible writers/readers together; restoring older binaries is not a database downgrade. Existing-schema planner paths and the newer-schema refusal are covered alongside migration tests.

## Current work

Overview keeps current account allowances above the selected history views. Date, model, project and chat filters do not erase live context or allocate account quota to filtered work. Explicit account selection never substitutes another account's current reading. The shared outlook prioritizes exhausted meters, then earliest exhaustion before reset, then pressure against each window's sustainable pace. Both reported windows need fresh supported assessments for an overall sustainable-pace conclusion; partial and unreported windows remain explicit. Reset estimates and fixed-horizon predictions retain separate targets and uncertainty.

Known limitation [PACE-001](docs/source-notes/WEEKLY_PACE_INVESTIGATION.md): the reset fallback `legacy-ewma` weights each adjacent poll equally. Rounded-meter jumps can briefly inflate its rate, while unchanged polls decay it toward zero even during ongoing work. Startup polls affect that decay. Retained-anchor and ongoing-work regressions reproduce the defect; its conditional survival state is not validated by flat readings. The investigation leaves the live policy unchanged. Corrective work must address observation/poll semantics and compare later reset-horizon outcomes, rather than retain a desired old rate or equate a flat meter with idle activity.

The tray companion uses Windows background resource scheduling, EcoQoS and a 0.5% total-system CPU budget while its dashboard is hidden or minimized. Opening restores normal scheduling and removes that budget before constructing the window; page loading follows window activation. Hidden dashboards release their page cache and active queries/timers, retaining navigation destinations rather than selected historical data. Quota collection and alerts continue, with a full configured polling delay after each collection pass so slow work cannot create a catch-up loop. Compact quota-pace outlooks run independently of detailed calibration and never load its training dataset.

Detailed workload calibration and token nowcasts run in one owned headless process in either mode, independently of the collector and UI. Its Windows job limits total-system CPU to 0.5% in the background or 2% with the dashboard visible, private commit to 384 MiB and its managed heap to 256 MiB. Background-started slices allow ten minutes with a five-minute cooldown; foreground-started slices allow three minutes with a one-minute cooldown. The longer background wall budget lets historical data preparation finish at the lower CPU rate. Showing the dashboard adjusts an active worker's CPU budget immediately. Closing the app terminates its worker. Timeouts or memory-limit failures retain quota-pace outlooks and cached progress rather than blocking collection. Detailed results are published as fresh only for an unchanged fresh quota anchor; token nowcasts are stale if their native accounting generation changed. Full forecast promotion checks collection-time qualification before reconstructed comparisons; model selection, source provenance and acceptance criteria remain the same.

Completed numeric ridge fits are reusable across slices and restarts in the rebuildable app-owned `calibration-cache.db`, separate from telemetry evidence. Keys include the solver version and exact binary training features, targets and penalty; payloads contain only numeric fit parameters with checksums. Cache storage is bounded to 16,384 entries and 32 MiB plus SQLite journals, with at most 256 fits in memory. Changed inputs miss the cache, and corruption or cache unavailability falls back to calculation. The existing headless evaluation executable hosts the worker and shares the installed app's self-contained runtime; no external service or automatic remote processing is introduced.

Implemented Codex-focused audit slices include the local Live fleet, explicit host-qualified SSH status checks, descriptive cache input mix, an opt-in authenticated read-only local limits API, a read-only inventory of source/account combinations retained in quota history, and aggregate daily JSON reports from the existing native event ledger. The inventory is derived from observations rather than a second mutable account registry. Unknown account keys remain unknown, and matching API windows receive the same opaque account ID only within the same provider and source. Daily local workload is explicitly unattributed to a backend account.

Successful SSH fleet checks now retain append-only host-qualified status snapshots with separate remote capture and local collection times, allowing offline status review. SSH is optional and never needed for account-wide provider quota on the PC; local Tokscale-style scanning likewise cannot supply another machine's private thread history without an additional source. No remote agent or automatic SSH polling is assumed. The status snapshots do not establish remote account ownership or provide lossless cross-machine token/quota history. Further work needs source-specific evidence before product claims: a Codex notify hook must sanitize content-bearing payloads and prove overlap with existing rollout accounting; detailed context-component token costs and per-prompt quota costs cannot be inferred from current totals; full distributed usage history needs explicit host/account provenance, source-qualified deduplication and offline recovery. Dynamic price catalogs and burn/runout baselines are research candidates, not substitutes for the existing fixed-rate counterfactual and chronological forecast evaluation. No remote quota is merged into a local account, and no process-level Stop action is provided.
