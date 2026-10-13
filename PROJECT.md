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

No forecast should require multiple reset cycles.

### Planning

User should be able to have a "planner", that allows to see whether their current usage will last for: x hours, with y model, with z effort.

## Quota Accounting

The current implementation keeps reported account quota, locally observed native Codex tokens, API-equivalent price weights and inferred quota cost separate. A source account key scopes the quota observations that supplied it; it does not retroactively assign local rollouts to the current login.

## Current work

Implemented Codex-focused audit slices include the local Live fleet, explicit host-qualified SSH status checks, descriptive cache input mix, an opt-in authenticated read-only local limits API, a read-only inventory of source/account combinations retained in quota history, and aggregate daily JSON reports from the existing native event ledger. The inventory is derived from observations rather than a second mutable account registry. Unknown account keys remain unknown, and matching API windows receive the same opaque account ID only within the same provider and source. Daily local workload is explicitly unattributed to a backend account.

Successful SSH fleet checks now retain append-only host-qualified status snapshots with separate remote capture and local collection times, allowing offline status review. SSH is optional and never needed for account-wide provider quota on the PC; local Tokscale-style scanning likewise cannot supply another machine's private thread history without an additional source. No remote agent or automatic SSH polling is assumed. The status snapshots do not establish remote account ownership or provide lossless cross-machine token/quota history. Further work needs source-specific evidence before product claims: a Codex notify hook must sanitize content-bearing payloads and prove overlap with existing rollout accounting; detailed context-component token costs and per-prompt quota costs cannot be inferred from current totals; full distributed usage history needs explicit host/account provenance, source-qualified deduplication and offline recovery. Dynamic price catalogs and burn/runout baselines are research candidates, not substitutes for the existing fixed-rate counterfactual and chronological forecast evaluation. No remote quota is merged into a local account, and no process-level Stop action is provided.
