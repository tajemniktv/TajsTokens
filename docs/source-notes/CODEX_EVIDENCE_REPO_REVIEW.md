# Codex evidence acquisition repository review — 2026-09-18

## Result and proof boundary

The subsequent user-supplied [layered statistical and TT design proposal](TT_LAYERED_STATISTICAL_DESIGN.md)
defines a frozen normalized workload basis with separate quota calibration. It is a research
direction, not evidence of implemented TT or a universal native-credit scale. PROJECT.md owns
current implementation scope; this review's dated experiments remain evidence, not competing plans.

**Prefer provider-native accounting quantities over inventing TT, but do not treat all
credits as one stable unit or daily relative usage as historical quota consumption.**
The reviewed implementations establish useful acquisition and reconciliation techniques,
not a universal credit-to-allowance contract. Prefer Codex-owned app-server acquisition where
available. **Current status:** sections 1–8 record the original static review; sections 9–10
record subsequent authorized live probes. Section 11 consolidates the broader repository map
and the now-implemented, opt-in daily backend adapter. Proposals are not all implemented.

The original static review inspected pinned source, existing TajsTokens implementation and source notes,
and generated the installed CLI's protocol schema, including experimental fields. That phase made
no authenticated backend calls, read no credentials, ran no downloaded project code, and
did not change collection, databases, account associations, forecasts or the running app.
Third-party tests were inspected, not executed. Published runtime anecdotes are attributed
to their authors; synthetic fixtures are not independent runtime corroboration.

Review depth is deliberately unequal: seven repositories received the original comparative
review below. Four additional repositories received focused source/documentation checks during
this consolidation, not equivalent whole-project audits. No third-party code was copied or run.

### Source anchors

Repositories were cloned into ignored `.codex/temp/codex-repo-review/`. Pins:

| Repository | Reviewed commit | Principal inspected evidence |
| --- | --- | --- |
| [MacSteini/Codex-Usage](https://github.com/MacSteini/Codex-Usage/tree/ed1c608ce7da180d996c0269e98e9c05461b8247) | `ed1c608ce7da180d996c0269e98e9c05461b8247` | `codex_usage.py`: endpoint allowlist, auth/header handling, online report, final-per-file token aggregation |
| [bigbobro/how-much-i-get-from-codex](https://github.com/bigbobro/how-much-i-get-from-codex/tree/9348b05c1da661f0dc2ac5ad0002ab2762f3e407) | `9348b05c1da661f0dc2ac5ad0002ab2762f3e407` | userscript `fetchAll`, `measuredAllowance`, `allowanceReading`, weekly projection; README, freshness note, smoke fixtures |
| [jordan-edai/codex-reset-watcher](https://github.com/jordan-edai/codex-reset-watcher/tree/3253269d856102273ff2ef826fbc1d8a07ccabf4) | `3253269d856102273ff2ef826fbc1d8a07ccabf4` | `CodexResetCreditsClient.swift`, `ResetCreditsStore.swift`, account snapshots and transport tests |
| [Soju06/codex-lb](https://github.com/Soju06/codex-lb/tree/9637bdee36744ca65e7cd13ffd47b7e79741f551) | `9637bdee36744ca65e7cd13ffd47b7e79741f551` | `app/core/usage/`: live snapshots, quota, capacity tables; dashboard weekly pace |
| [steipete/CodexBar](https://github.com/steipete/CodexBar/tree/5065a72b38320b137ca131e656065f99b2d1eda9) | `5065a72b38320b137ca131e656065f99b2d1eda9` | Codex OAuth/PAT/CLI sources, web dashboard, history ownership, vendored CostUsage scanner and accounting tests |
| [nesszer/Win-CodexBar](https://github.com/nesszer/Win-CodexBar/tree/10e3b0954b32770a9709197b1f4c6e530a3a5f80) | `10e3b0954b32770a9709197b1f4c6e530a3a5f80` | `rust/src/providers/codex/api.rs`, weekly-reset policy, JSONL parser and cost-scanner reconciliation |
| [Thomas97460/quota-tracker](https://github.com/Thomas97460/quota-tracker/tree/d52a8c9e40918e35c8b278a135c5e3bba206298d) | `d52a8c9e40918e35c8b278a135c5e3bba206298d` | `quota_tracker/providers/codex.py`, SQLite schema and archival policy |

TajsTokens baseline: `4992bf6988d4ddc4aa9943d4873b56c597883d1d`.
Read-only Codex source: clean checkout at
[`7498521d288b9b3b96ffba4eedf089d8d6e06a84`](https://github.com/openai/codex/tree/7498521d288b9b3b96ffba4eedf089d8d6e06a84).
Installed CLI freshly checked: `0.156.0-alpha.2`; no exact source/build equivalence asserted.
Its schema was generated locally with `codex app-server generate-json-schema --experimental`.
The installed Desktop evidence remains the separately dated static inspection in
[CODEX_DESKTOP_ANALYTICS.md](CODEX_DESKTOP_ANALYTICS.md), not a new Desktop runtime probe.

Additional focused checks, cloned on 2026-09-18:

| Repository / pin | Inspected anchors |
| --- | --- |
| [junhoyeo/tokscale](https://github.com/junhoyeo/tokscale/tree/d8fd670a46857e5290e71b10245dc522a344fc17) | `README.md` account activity section; `crates/tokscale-cli/src/commands/codex_activity.rs` |
| [merefield/codexometer](https://github.com/merefield/codexometer/tree/13405d21160c29ed6330f621d277debecf96450c) | `README.md` Consumption Pace and Observed quota API equivalent; `internal/ui/benchmark_quota.go` |
| [xiufengsun/TokenTracker](https://github.com/xiufengsun/TokenTracker/tree/5ccd325fb31fd3ca92c577adc85966b8fb8da8ab) | `src/lib/codex-service-tier.js`, `codex-token-usage.js`, `codex-token-refresh.js`; interleave fixtures in `test/rollout-parser.test.js` |
| [douglasmonsky/codex-usage-tracker](https://github.com/douglasmonsky/codex-usage-tracker/tree/43278d1408416c3262086028bdfa7a522cfc35f8) | `src/codex_usage_tracker/agent_kernel/evidence/service.py`, `kernel/allowance/service.py`; supported-question, product-direction and schema contracts under `docs/` |

## 1. Credit / daily allowance relationship

The userscript pairs each date's `daily-workspace-usage-counts.data[].totals.credits`
with the sum of `daily-token-usage-breakdown.data[].product_surface_usage_values`:

`inferred denominator in credits = 100 × reported daily credits / daily percentage`.

Its author reports 49.897 credits per percentage point on 26 active days, hence 4,989.7
credits per allowance, including a day at 100%. This is credible evidence of a shared
accounting denominator for that observed account/report era. It is not evidence that all
plans, buckets, periods or current short/weekly limits use that denominator.

Crucially, `smoketest.html:436–465` constructs percentages from `ALLOWANCE_CREDITS = 4989.7`.
Those scenarios exercise the implementation but cannot independently validate the ratio.
Current Codex TUI normalization calls non-credit usage **RelativeUsage**, not universally
percent; response `units` and account/report semantics must be retained and checked.

Implementation details worth testing, not adopting as provider facts:

- The script prefers samples inside the current inferable window, falling back to older
  history. It anchors on the newest ratio, retains samples within 2%, and takes their median.
  This can select a lagged/incomplete newest day and is not calibrated change detection.
- It keeps five-hour and weekly windows separate by duration, refuses a five-hour ceiling
  from daily buckets, detects sliding zero-use placeholder windows, and exposes disagreements.
- A weekly estimate uses cycle spend divided by current weekly percentage; differences over
  5% can override the daily ratio. A reached limit can override it with a depletion estimate.
  **TajsTokens must not call that depletion amount exact:** delayed credits, shared-pool work,
  UTC boundary spill, missing surfaces, purchased-credit use, or a different blocking bucket
  can all break the equality. `limit_reached` alone does not prove exhaustion of this denominator.
- Reported credits override token pricing. Where reported amounts are nonpositive the script
  falls back to rate-card pricing, sometimes a top-model rate, and proportionally allocates
  model/speed/surface amounts. Those are derived estimates, not newly observed native credits.
- It heuristically decides whether `on_demand_credits` overlaps `credits` from their magnitudes.
  Do not reproduce that inference. Preserve both until the contract establishes inclusion.
- `models[].credits` can carry relative values on a percent report. A field called credits
  does not override the report unit. Allocating turns/tokens by credit share is not observation.
- Its $0.04/credit conversion is an explicit display assumption, not an API-credit exchange
  rate or provider compute cost. Its dated rate table is not a stable historical rate contract.

The author documents a monthly-to-weekly transition with a stale daily denominator. That is
particularly relevant negative evidence against treating this ratio as a timeless allowance.
Daily spend can be useful on a native intermediate scale even when its allowance conversion drifts.

Official guidance independently confirms that included usage is consumed before extra credits,
that supported agentic features can share an allowance, and that these are not API credits.
It does not document these private route schemas or establish a fixed reporting-lag bound.
[OpenAI personal credits guidance](https://help.openai.com/en/articles/12642688-using-credits-for-flexible-usage-in-chatgpt-plus-pro).

## 2. Endpoint contracts and limits of interpretation

Routes below are relative to `https://chatgpt.com/backend-api/wham/`. Codex backend-client
also supports an `/api/codex/` path style. These are inspected client contracts, not a public
stability guarantee or proof that this account can call each route. Primary source files:
[analytics client](https://github.com/openai/codex/blob/7498521d288b9b3b96ffba4eedf089d8d6e06a84/codex-rs/backend-client/src/client/analytics.rs),
[generated analytics models](https://github.com/openai/codex/blob/7498521d288b9b3b96ffba4eedf089d8d6e06a84/codex-rs/codex-backend-openapi-models/src/models/analytics.rs),
[TUI route selection](https://github.com/openai/codex/blob/7498521d288b9b3b96ffba4eedf089d8d6e06a84/codex-rs/tui/src/analytics/client.rs).

| Surface | Exact inspected request/data | Caveat and TajsTokens treatment |
| --- | --- | --- |
| Daily activity/counts | GET `analytics/daily-workspace-usage-counts`; `start_date`, `end_date`, `group_by=day`, `workspace_user=true`. Rows: `date`, `totals`, `clients`, optional `models`, `groups`; response optional `balance_unit`, `active_users_summary`, grouping metadata. Totals include users/threads/turns/credits, optional USD string, on-demand credits and text-token components. | Seat view, not a local repository/worktree. Activity, credit and token fields remain separate. Models can have separate token-shaped and turn-shaped rows for the same name. No `data_freshness_ts` in this inspected response type: do not invent one or inherit another report's freshness. |
| Personal daily breakdown | GET `usage/daily-token-usage-breakdown`, date range and `group_by=day`. Response `data`, optional `units`, `data_freshness_ts`, `group_by`, `breakdown_by`. Rows: surface-value map, optional attribution, premium usage, models and dimensional groups. | Only confirmed percent semantics justify an allowance ratio. Attribution dimensions are thread source, turn trigger, model, surface. Alternative breakdowns describe the same usage; do not add them together. Missing attribution is not zero or proof of complete coverage. |
| Workspace token/credit breakdown | GET `usage/daily-workspace-user-token-usage-breakdown`; same response family. EnterpriseTokens adds `breakdown_by=model` and repeated `modes=codex&modes=work`; WorkspaceCredits does not. | TUI selects by account kind: Enterprise token view differs from Business workspace-credit view. The userscript reports personal-account 400/no-active-workspace. Treat that as endpoint/account capability, not zero usage. Preserve `units`, modes and selected plan context. |
| Premium maps in breakdown | `total_usage_credits`, `credit_usage_credits`, uncached/cached/output/text-total token maps and optional total-token map, keyed by surface. | Preserve the distinction between total usage and credit-balance usage. Neither map is automatically the daily allowance denominator or an additive extension to `totals.credits`. |
| Consumer credit events | GET `usage/credit-usage-events`; inspected backend client sends **no date-range/grouping query** here. Response `data[]`: `date`, `product_surface`, `credit_amount`, optional `usage_id`. | Separate credit ledger/report, not all subscription usage. Local date filtering is not server-side completeness or pagination proof. Preserve signed adjustments where supported; missing IDs do not justify collapsing equal amounts. |
| Enterprise credit report | GET `usage/daily-workspace-user-credit-usage`; dates plus `breakdown=product|model|speed|reasoning_effort`. Response `breakdown`, per-date value maps, `series`, optional `unit`, `data_freshness_ts`. | Different shape and units from consumer events. Series totals and daily cells are two views, not additive evidence. Unknown unit stays unknown, including dollar/token-based billing differences. |
| Profile activity | GET `profiles/me`. Existing app-server projection: summary lifetime/peak tokens, longest-running turn seconds, current/longest streak days and nullable daily `{startDate,tokens}` buckets. Full backend profile additionally has optional identity metadata, `metadata.stats_as_of`, `stats_error`, fast-mode/effort percentages, skill counts, thread count, top invocations. | Activity totals are neither credits nor quota. Full-profile names/usernames are not account/seat ownership. The public app-server projection omits full-profile freshness/extra statistics; do not infer them. No prompts/names needed in durable evidence. |
| Plan history | GET `usage/plan_limit_history?days=7`. `data_as_of`, `coverage_start`, `coverage_complete`, `approximate` (default true), `boundary_tolerance_seconds`, periods with `id`, duration, plan, start/end, `accounting_complete`, nullable `used_basis_points`, optional dimensional breakdowns. | Basis points are hundredths of a percent of that **historical period's** allowance. Much stronger quota labels than assuming a current denominator for old days. Null is unknown; partial/approximate stays such. Client maps 404 to unavailable. Feature/account gating still applies. Do not apply the report's tolerance globally to current quota resets. |
| Thread estimate v1 | POST `usage/thread_usage/query`, `{thread_ids:[...]}`; backend client permits 1–100 distinct IDs. Result credits in integer millionths, optional USD millionths, groups by model/effort/speed and nullable token categories. Public `account/usage/read` exposes one optional `threadId`. | Estimated lifetime accounting, not balance debit or a quota-window delta. App-server maps route 403/404 to null. Omitted/null is not zero; no native account ID in the usage response. Root/child totals cannot be summed without ownership coverage. |
| Task/thread v2 | POST `usage/thread_usage/query_v2`, `{threads:[{thread_id,created_at,descendant_thread_ids}]}`. Client bounds: 100 roots, 1,000 disjoint participating IDs. Response `data_as_of`, thread `data_status`, `usage_source`, nullable `five_hour_limit_percent`, `weekly_limit_percent`, decimal-string `balance_usage_credits`, dimensional groups. | Lifetime work measured against the **current full allowance**, distinct from historical-period basis points and v1 estimates. Preserve partial/unavailable, negative credit adjustments and values above 100; do not clamp lifetime usage to a gauge. No app-server method found. |

Date-boundary warning: current backend-client documents an inclusive UTC date range; the
userscript sends `end_date = requested end + 1 day`. This disagreement must be tested on
bounded fixtures/authorized observations before joining reports. Do not assume all routes
share inclusive/exclusive semantics, or prorate a partially overlapping UTC day as exact spend.

Profile/plan/task sources:
[profile](https://github.com/openai/codex/blob/7498521d288b9b3b96ffba4eedf089d8d6e06a84/codex-rs/backend-client/src/client/profile.rs),
[plan history](https://github.com/openai/codex/blob/7498521d288b9b3b96ffba4eedf089d8d6e06a84/codex-rs/backend-client/src/client/plan_history.rs),
[v1 estimates](https://github.com/openai/codex/blob/7498521d288b9b3b96ffba4eedf089d8d6e06a84/codex-rs/backend-client/src/client/thread_usage.rs),
[v2 task usage](https://github.com/openai/codex/blob/7498521d288b9b3b96ffba4eedf089d8d6e06a84/codex-rs/backend-client/src/client/task_usage.rs).

Official Enterprise/Edu guidance also distinguishes recently active chat selection from
lifetime totals and warns of missing subagent/tool/background activity and reporting delays.
Do not generalize that product's refresh timing into a personal-plan endpoint SLA.
[OpenAI Desktop analytics guidance](https://help.openai.com/en/articles/20001478-reviewing-work-and-codex-usage-and-using-personal-analytics-in-chatgpt-desktop).

**At the static-review stage, `daily-code-review-metrics` was unverified.** No corroborating implementation was found
in the seven reviewed snapshots or searched Codex backend-client/model sources. This is not
proof of nonexistence. A code-review quota bucket is not corroboration of that analytics route.
Section 10 subsequently corroborated a 200/empty response; populated metric semantics remain unverified.

## 3. Comparison with TajsTokens

| Project | Useful technique | Difference / do not import |
| --- | --- | --- |
| openai/codex | Primary upstream schema/implementation reference for app-server activity/current quota and backend analytics, plan history, thread v1 and task v2. Prefer a Codex-owned report seam. | Backend-client methods are not automatically app-server RPCs or available to every account. Installed/runtime evidence takes precedence over an unmatched upstream snapshot. |
| Tokscale | App-server `account/usage/read` with explicitly supplemental account activity; this technique is already represented in TajsTokens' account evidence/reconciliation. | Account totals never replace local token truth. The current focused check confirms the technique, not an independently reconstructed chronology of who discovered it first. |
| Codexometer | Transparent elapsed-window versus consumed-quota pace; linear exhaustion projection; observed API-price-weighted workload/quota learning. Useful evaluation and explanatory baselines. | API-equivalent USD is an estimate, not native provider credits. Published price weighting, tier assumptions and its learner require separate evaluation before adoption; pace is not calibrated forecasting. |
| TokenTracker | Local `thread_settings_applied.thread_settings.service_tier`; bounded multi-baseline cumulative reconciliation and restart/interleave fixtures. | Tier parser normalizes strings, and pricing treats unknown as Standard: retain raw nullable evidence instead. Stream inference uses `total - last` and a 32-baseline cache, not native stream IDs; it cannot prove a lineage from totals-only `100 → 20 → 110`. Direct auth refresh is outside our adapter's responsibilities. |
| codex-usage-tracker | Explicit physical occurrences versus canonical entities, bounded evidence selection, deterministic allowance intervals with local tokens/calls/turns per percentage point. | Strong conceptual reference, not a proven winner on our corpus. Its supported contracts reject allowance-exhaustion forecasting as a kernel fact; deterministic diagnostics are not predictions. Do not transplant its separate kernel/ledger. |
| Codex-Usage | Explicit read-only endpoint inventory; per-endpoint failures; redacted diagnostics; profile plus quota plus credit-event inspection. | Directly reads `auth.json` and sends bearer/account header. Local summary uses final cumulative total per physical file, assigns it to file date/final model, then sums files: not stronger copy/interleave accounting than TajsTokens. Redacting arbitrary responses is weaker than our typed content-free retention. |
| how-much-i-get-from-codex | Joins native daily credits and relative usage; identifies incompatible quota windows, changing denominator, ambiguous subscriptions and stale analytics. | Browser-session token acquisition and direct private HTTP. Price fallbacks, allocation, zero coercion, thresholds and depletion precedence are application heuristics. Not a stable provider-normalized unit or independent calibration dataset. |
| codex-reset-watcher | Endpoint allowlist, redirect rejection, ephemeral transport; before/after auth-context change checks; per-account non-secret snapshots; separates last check from successful usage capture and unknown reset inventory. | Credential/JWT parsing; snapshots are not a full historical analytics ledger. Reset credits are entitlements to reset, not workload credits. Auth-context identity preference still does not prove a response's effective seat in every route. |
| codex-lb | Account-qualified live headers/payload quota, credit-backed availability, window normalization, retained history and reset coordination. | It is a traffic-owning proxy/account pool, not a passive observer. Plan capacities are hardcoded (e.g. Plus secondary 7,560 credits, Pro 50,400 at this pin); pooled credit pace is derived, not independent evidence of our account's allowance. Proxy traffic is not all-account history; do not bring routing/auth switching into TajsTokens. |
| CodexBar | OAuth/PAT and CLI RPC/PTY acquisition options, web dashboard supplement, account-scoped history, explicit identity reconciliation, local cost/pricing history and robust scanner cases. | Broader credential/browser ownership than our released boundary. Email-only/legacy continuity policies are not native account attribution. Locally sampled plan-utilization history is not backend plan history. Local dollar estimates are not provider quota. |
| Win-CodexBar | Account-bound requests; PAT account-ID requirement rather than email identity; persistent account-scoped weekly-reset confirmation; incremental JSONL/cost scanner with interleave containment. | Direct auth/HTTP, optional account-management surfaces. Same conceptual undercount tradeoff as containment elsewhere; cache deletion/reconciliation is not our durable evidence retention policy. |
| quota-tracker | Simple passive log scan plus active quota polling; historical SQLite charts and age-based rollups. | Active probe uses bearer auth without account header in this implementation; quota records are provider/window scoped, not our explicit account cohorts. Path/timestamp/index event IDs do not deduplicate copies. It falls back from last usage to cumulative totals as if per-event usage. Thirty-day archival averages are unsuitable for exact cycle boundaries and revisions. |

TajsTokens already has the better evidence boundary for this task: source/account cohorts,
immutable fetch observations, actual collection times, null/capability states, account-bracket
correlation, revocable user associations, current-anchor equality, and schema-14 daily-value
sharing without losing revisions. Reuse those owners; do not add a competing ledger.
At the original review baseline, server evidence did **not** retain daily credit/relative-usage reports, full-profile
metadata, historical-period allowance labels or v2 task percentages. See
[existing acquisition contract](CODEX_SERVER_USAGE.md).
The opt-in daily report implementation in section 11 supersedes only the daily-report part of that gap.

## 4. Duplicate, copied and interleaved rollout evidence

**Yes: CodexBar has more explicit safeguards for these cases, not a generally proven superior
accounting truth.** Its `CostUsageScanner+CacheHelpers.swift:491` keys cross-file rows by
session (file fallback), turn ID, event index, day, model, input/cached/output tuple. This can
remove copied rows while retaining unique suffixes. It is not native request identity: changed
event ordinals, ambiguous owners and equal legitimate rows require separate treatment; the key
does not include every semantic dimension. It also classifies copied/inherited subagent shapes.

CodexBar's scanner latches an interleaved state on counter regression, retains a never-lowered
watermark, suppresses seen raw tuples, and uses contained growth capped by `last` when available.
Its source explicitly accepts Phase-1 undercount for a smaller lineage below the watermark.
Win-CodexBar likewise has `saw_interleaved_totals`, a persistent high watermark and contained
delta calculation. Neither proves which independent lineage owns all work from numeric values.

TajsTokens' reducer prefers complete `last_token_usage`, suppresses unchanged aggregate totals
and stale regressions, otherwise advances a reset epoch. With no usable last increment a reset
emits zero but lowers the subsequent baseline. It has no equivalent latched interleave watermark.
For a totals-only ordered sequence 100 → 20 → 110, its inspected rules can count 100 + 0 + 90;
that illustrates potential gap recounting if these are interleaved histories, not three proven
independent increments. A complete-last sequence has different behavior and must be tested too.

Physical source+offset dedup and prefix-verified checkpoints protect replay within a source,
not copies across sources. Session-keyed counter state is not a lineage resolver. Existing
bounded alternate inspection and duplicate-fingerprint candidates correctly stop short of merging.

Propose a **versioned reconciliation projection**, retaining original source observations and
selection reasons. Establish owner/lineage/overlap first; do not delete duplicate candidates,
dedup by final totals, silently take max per session, or treat a containment estimate as exact.
Relevant inspected tests include CodexBar `CostUsageScannerBreakdownTests.swift` interleave
cases (including >64 observations and checkpoint restart), `CodexSubagentAccountingIntegrationTests.swift`
and `CodexCompactSubagentAccountingTests.swift`. These are test-design references, not copied code.

## 5. Compatibility and proposed evidence-model additions

All additions below are proposed extensions to existing typed server evidence/quota history.
No migration or collection change is part of this review.

1. **Typed report payloads:** distinguish daily counts, personal relative usage, workspace
   token/credit usage, balance events, profile activity, plan periods, thread-v1 estimates and
   task-v2 usage. Reuse `CodexServerObservation`, immutable fetches and value-set sharing.
   Do not put them all in `QuotaSnapshot` or `token_usage`.
2. **Quantity semantics:** exact decimal/integer representation, original unit string,
   semantic quantity kind, reported-versus-estimated-versus-price-derived origin, denominator
   basis (historical period/current full allowance/unknown), inclusion relation and rate-policy
   version only when known. Preserve signed ledger adjustments and missing versus zero.
3. **Scope and time:** requested range, observed range, bucket timezone/grain, endpoint/report
   contract, modes/surfaces/groupings, source version, actual fetch timestamps, native as-of
   and completeness/approximation separately. Reports are revisable snapshots, not additive polls.
4. **Identity:** effective provider account/workspace and seat/user scope when actually supplied;
   requested account context separately; existing pseudonym/bracket evidence class; missing or
   conflicting identity remains so. Email, profile display name, local CODEX_HOME, plan name and
   requested account header are not interchangeable ownership proofs. Stable brackets remain
   correlation and cannot exclude a switch away and back. Never backfill old rollout ownership.
5. **Cycle/regime:** retain native period IDs and boundaries, bucket/limit ID, duration, plan,
   quantity semantics and source/account compatibility. Reuse current bounded reset-generation
   policy; a reset is not necessarily a new policy regime, and policy drift need not coincide
   with a reset. Keep raw boundaries unchanged and selection/pairing policies versioned.
6. **Derived mappings and reconciliation:** store references to input observations, policy
   version, eligibility/exclusion reasons, residuals/ranges and effective/first-observed time.
   Keep ratio-based denominator hypotheses distinct from provider-declared limits. Retain
   alternative interpretations and revision history; no destructive reclassification.

A valid credit/percent pair needs compatible account+seat, report coverage, product scope,
date bucket, units, revision/as-of and denominator era. A valid current-window mapping further
needs a known bucket, aligned cycle bounds and no unaccounted shared-pool work or credit-balance
spill. Do not mix v1 micros, v2 balance debits, price estimates and reset-credit counts merely
because they contain “credit”. Compatible units alone do not establish compatible sources.

### Semantic policy-drift signals worth persisting

- Provider-reported plan/bucket/duration changes; bucket appearance/disappearance; blocking
  reason and spend-control changes; reported unit, modes, surface coverage and grouping changes.
- Native period identity/completeness/approximation; accounting as-of changes and historical
  revisions. Distinguish accounting corrections from changes effective on the usage day.
- A sustained change in matched native credits per relative point; disagreement with compatible
  period-history labels; changed credits per token composition at fixed model/effort/speed.
- Changed ratio between short and weekly consumption only when observed on compatible intervals;
  changed current-allowance valuation of an unchanged old task may indicate denominator rebasing.
- Lag, truncation, missing fields, account changes, altered sampling precision and source/client
  changes are competing explanations or capability drift, not automatic quota-policy changes.

The installed `RateLimitSnapshot` schema already exposes `rateLimitReachedType`,
`spendControlReached`, `individualLimit`, `normalModelSlug`, credits and named limit metadata
alongside windows/plan. TajsTokens' current `QuotaSnapshot` projection does not preserve all of
these. A small content-free side of the existing quota observation is a useful first proposal,
without new HTTP acquisition. Preserve absence and exact provider semantics; do not interpret
`credits.unlimited` as unlimited subscription allowance or assume a missing bucket is revoked.

## 6. Proposed tests, before acquisition implementation

| Priority | Tests and required outcome |
| --- | --- |
| P0: contracts/units | Percent, relative, credits, USD, unknown/missing units; optional/empty maps; decimal credits, micros, negative adjustments; lifetime percentages >100; no cross-unit sums or silent zero substitution. Same field name under different unit must not create a mapping. |
| P0: pairing | Native-only 4,989.7-credit example recovers the constructed denominator, labelled synthetic. Independently observed sanitized samples required to validate reality. Reject price-fallback samples; exclude zero/tiny denominator, missing surface, mixed seat and mismatched as-of. Check date end inclusion, DST display versus UTC buckets, midnight/non-midnight resets and partial opening day. |
| P0: drift negatives | Constant ratio across completed days; mid-history denominator switch; latest day lag then revision; old days restated under new denominator; shared-pool activity missing from Codex; five-hour depletion during a weekly cycle; purchased-credit continuation. Preserve conflicts; do not crown depletion an exact allowance. |
| P0: identity/capability | Same email with personal and two workspace seats; requested header disagrees with response; A/B/A identity; stable/missing/failed brackets; 400/401/403/404, null, empty, partial, malformed and no-seam distinguished. Failed newest attempt must not leave prior success current. No unsupported route is probed by production. |
| P0: reconciliation | Full copies, copied prefixes plus unique suffixes, divergent tails, metadata-only/late owner changes, inherited parent prefix, missing parent and last-only copies. Two legitimate equal increments remain distinct. Physical relocation/replacement, different append order and event-index shifts must not fabricate equivalence. |
| P0: interleave/recovery | Totals-only 100→20→110 and alternating high/low lineages; complete-last variants; real counter reset versus stale replay; identical totals with component reshuffle; >64 unique observations then replay. Full scan, chunked append and restart give the same selected projection; unknown lineage remains explicit and undercount is disclosed. |
| P0: storage/privacy | Replay idempotence, same-ID conflict rollback, late corrections/removals, source retirement, value sharing without account sharing, checkpoint/migration crash recovery. Existing observations and collection times remain unchanged. No raw auth, profile names, prompts, thread titles or payload bodies retained/exported. |
| P1: plan/task semantics | Basis points use historical period, task percentages use current full allowance; partial periods withheld from exact labels; null != zero; unknown breakdown dimensions do not erase known rows. Disjoint root/descendant groups; omitted/unexpected/duplicate IDs; root+child must not double-count. |
| P1: evaluation | Chronological held-out cycles/regimes with no future revision or association leakage. Compare native-credit features against token-composition and incumbent pace baselines. Report interval errors, samples, independent cycles, uncertainty coverage and sparse/stale/reset behavior; do not promote merely because ratio-fit error is small. |
| Adapter-only, if later authorized | Fixed HTTPS host/route/method allowlist; redirect denial; no credential logging/persistence/export; bounded payload/time/retries; cancellation; identity switch during request; expired token with Codex-owned refresh, no parallel refresh writer; no auto-fallback from production RPC to direct HTTP. |

Do not add tests that merely mirror these proposed record types. First create meaningful
contract/reducer cases under existing owners; acquire sanitized independent evidence only
through an explicitly selected boundary. Fixtures derived from a ratio cannot prove that ratio.

## 7. Available seams and recommended order

**Available in the installed app-server schema now:** `account/read`,
`account/rateLimits/read`, and `account/usage/read` with optional `threadId`.
These cover account login/plan context, current quota/credit/spend-control metadata and the
existing activity/thread estimate projection. Notifications and thread token usage are local
event/state observations, not a historical billing-report API. Reset-credit consumption is
a separate mutation, not a research probe.

**Not found as installed or inspected upstream app-server analytics methods:** daily counts,
daily relative/workspace breakdowns, credit-event history, full-profile extras, plan history,
and task v2. `--experimental` schema generation did not add them. Their backend-client/TUI
implementation and Desktop private HTTP host path do not by themselves create a supported
third-party RPC/export seam. Backend availability was untested at this stage; see sections 9–10.

**Experimental auth adapter possibility, not released integration:** upstream legacy v1
`getAuthStatus` defines `includeToken`/`refreshToken` and an optional `authToken` response.
This was not present in the installed generated public method catalog; availability was not
established during the original review and no token request was made. The September 19 log
inspection subsequently observed runtime requests for this method (section 13), without proving
successful token export. Even where callable, exporting a token then
making HTTP requests transfers credentials/request ownership to TajsTokens: it is not a
Codex-owned analytics RPC. Internal `chatgptAuthTokens` login is explicitly marked unstable,
internal-only and supplies credentials to Codex, not an analytics read alternative.

The seven projects' auth-file, OAuth/PAT and browser-session approaches belong in that
separate research category. Prefer a Codex-owned allowlisted report/export or app-server
extension using the existing backend client and credential refresh. An experimental adapter
would require an explicit change to the acquisition boundary, not silent fallback or browser
injection. No account-management, reset-redemption or unrelated UI redesign is proposed.

Original recommended order (superseded by the authorized probes and section 11):

1. Review/adopt the quantity, compatibility and reconciliation test proposals.
2. Preserve already available semantic quota metadata through existing app-server acquisition.
3. Specify and evaluate source/lineage reconciliation independently of new backend reports.
4. Request/use a Codex-owned structured analytics seam; only separately authorize an experimental
   adapter if that tradeoff is wanted. Validate actual report availability/units/identity first.
5. Evaluate native credits and historical allowance labels before considering TT.

**TT conclusion:** native credits are the preferred candidate intermediate accounting scale
where genuinely reported, and may make a custom TT scalar unnecessary for that use case.
They are not yet a validated stable cross-plan/cross-policy workload scale. Strong native
period labels may also allow direct feature-to-quota prediction without any intermediate
scalar. Keep TT unshipped research; lack of a validated universal credit mapping is not a
reason to invent one. This review does not change current model-promotion gates.

## 8. Codex SDK follow-up — 2026-09-18

**The SDKs do not expose additional billing evidence. Python can simplify app-server
transport in a Python tool; TypeScript is primarily an agent-execution wrapper. Neither
unlocks the private analytics reports listed above.**

Checked the current [official SDK documentation](https://learn.chatgpt.com/docs/codex-sdk),
[app-server documentation](https://learn.chatgpt.com/docs/app-server), and the TypeScript
and Python SDK sources at the same clean Codex commit `7498521d288b9b3b96ffba4eedf089d8d6e06a84`.
No SDK was installed or executed, no agent turn was started, and no account call was made.
Source-level SDK details below are pinned-checkout findings, not verification of every
published package version. Official docs say Python has a stable release with a pinned CLI
runtime dependency; this review did not install that release to establish binary parity.

| SDK / surface | What it actually provides | Relevance to TajsTokens |
| --- | --- | --- |
| TypeScript `@openai/codex-sdk` | `Codex.startThread` / `resumeThread`, `Thread.run` / streaming. `exec.ts` launches `codex exec --experimental-json`. `turn.completed.usage` carries input, cached input, cache-write input, output and reasoning output counts in the inspected source. | Convenient for collecting usage of turns the application runs. Not passive whole-account telemetry, quota/credit history or a daily analytics client. Do not run a prompt to ask the agent to retrieve billing evidence. |
| Python `openai-codex` | Sync/async thread operations, streaming, login/account helpers, process lifecycle and typed JSON-RPC over `codex app-server --listen stdio://`. | More relevant transport machinery, but the same server authority and methods TajsTokens already uses. Existing Codex authentication can remain Codex-owned. |
| Python generated protocol models | Includes `GetAccountRateLimitsResponse`, `GetAccountTokenUsageResponse` and typed `account/rateLimits/updated` notifications. | Useful schema/reference material. Generated types do not imply new endpoints or guarantee a matching installed runtime. |
| Python lower-level client | `openai_codex.client.CodexClient.request(method, params, response_model=...)` sends arbitrary named RPC and validates its object response; async equivalent also exists. The high-level `Codex` facade has no dedicated quota/usage convenience methods in this pin, and the lower-level client is not a top-level `__all__` export. | Can wrap the existing `account/rateLimits/read` and `account/usage/read` without hand-writing a transport in a Python research tool. It cannot route a private HTTP analytics URL or make an unsupported RPC work. Do not build on the facade's private `_client` attribute as a stable API. |

The Python SDK normally selects its packaged CLI, not necessarily the executable currently
used by TajsTokens. `CodexConfig(codex_bin=...)` deliberately selects a specific binary;
schema, SDK version, runtime version and observed capabilities must be recorded separately.
The inspected configuration defaults `experimental_api` to true. That is not permission to
expand production capabilities, nor evidence of a hidden daily-history method.

**Recommendation:** keep TajsTokens' existing C# app-server integration. Adding a Python or
Node sidecar solely for these reads adds deployment/version/lifecycle boundaries without
adding evidence. Python is a reasonable option for a separate, explicitly bounded research
harness if one is needed; it is not a reason to replace the working collector. A future
Codex-owned analytics RPC would benefit both SDK and direct clients; a new SDK alone cannot
supply the absent backend contract.

If an SDK adapter is proposed later, add transport-parity checks: same selected CLI and
request must preserve optional thread estimates, nulls, native account absence, bucket maps,
as-of fields and errors; unknown methods stay unsupported; notifications cannot be mistaken
for replies; lifecycle/cancellation must not orphan processes. Preserve TajsTokens' account
brackets, bounded response handling, immutable provenance and no-credential-retention rules.
SDK convenience does not replace those evidence guarantees.

## 9. Authorized direct-backend experiment — 2026-09-18

After the review, the user explicitly relaxed the research boundary: try the private backend,
accept experimental stability, and use `how-much-i-get-from-codex` as the closest functional
reference. This supersedes the earlier recommendation against direct acquisition **for this
isolated experiment**. Released app acquisition and storage remain unchanged.

At 21:02–21:03 UTC an original bounded Python probe read the existing Codex access token/account
context into memory and issued fixed-host HTTPS GETs with redirects disabled. No credentials
were printed, copied, refreshed or modified. No reset redemption, login/account switching,
agent turn, production database write or app restart occurred. Raw bodies were not saved;
allowlisted numeric summaries are ignored local research artifacts under `.codex/temp/`.
The probe was not copied from the reviewed repositories.

| Route | Observed result on this account |
| --- | --- |
| `usage`, before/after reports | 200; returned account identity matched the requested context both times. Stable plan/window readings within the probe. Report association remains correlated because the daily reports do not supply independent account identity. |
| `analytics/daily-workspace-usage-counts` | 200; 27 rows, `balance_unit=credit`, `group_by=day`. Nonzero text-token activity, but **every totals.credits value was zero**; on-demand amounts absent. |
| `usage/daily-token-usage-breakdown` | 200; 31 rows, explicitly **`units=percent`**, daily surface values and model rows. This is usable new provider-native daily relative-usage evidence, not a guessed unit. |
| `usage/daily-workspace-user-token-usage-breakdown` | 400. Response body deliberately not retained; no claim about its exact error reason. |
| `usage/credit-usage-events` | 200 with an empty `data` array. Not evidence that subscription usage was zero. |
| `usage/plan_limit_history?days=7` | Actual 404 on this request/account; no report available through this probe. |
| `profiles/me` | 200 JSON object; no identity/profile text persisted. |

The daily query requested 2026-08-19 through 2026-09-18 with `group_by=day`;
counts additionally used `workspace_user=true`. Two focused follow-ups checked why the
credit/percentage join yielded no positive-credit pairs and whether alternative native credit
fields could supply the numerator. Count-model rows also had zero credits, no text-token amounts
and no USD amounts. The relative report's model `credits` values were nonzero **within its
percent-unit report**, not independent currency credits; premium usage maps were absent.
Thus this account did not reproduce the author's positive native credit/percent ratio.

This is a productive result, not a reason to stop: daily percentage history and token composition
can support the desired product directly. Next candidate slice is experimental daily-report
collection through existing evidence owners, then historical charts and an explicitly estimated
allowance/capacity view. This account would need a price-derived numerator (or another observed
native amount) for a credit-denominated estimate. Aggregate token pricing without per-model token
splits must expose the assumption/range rather than invent a precise model allocation.

Before promotion, the minimal practical checks are unit/zero preservation, account-switch handling,
report revisions and date alignment; use observed percent data immediately as dated daily relative
usage rather than waiting for a universal policy theory. Daily percentages still do not establish
the denominator of today's weekly meter. Keep the ratio and current-window estimates visibly
experimental, as in the reference product. No new production adapter or accounting change was
implemented in this probe, and `daily-code-review-metrics` was not probed or verified.

## 10. Expanded endpoint survey — 2026-09-18

The user then authorized trying additional endpoints. At 21:11–21:12 UTC, the isolated
probe tested the following 14 additional route/query surfaces. Paths are relative to
`https://chatgpt.com/backend-api/`; `{account}` was the existing authenticated Codex account,
not a guessed or enumerated account. Analytics used 2026-09-11 through 2026-09-18.

| Route / request | Actual result | Practical interpretation |
| --- | --- | --- |
| GET `wham/rate-limit-reset-credits` | 200; 2 available, 2 detail rows | Reset inventory and expiry/status metadata are accessible. No reset consumed. |
| GET `wham/analytics/daily-skill-usage-metrics`, day grouping, current seat, top 10 | 200; 4 daily rows and `data_freshness_ts` | Skill activity history is available. Skill names/content were not retained in the probe summary. |
| GET `wham/analytics/daily-plugin-usage-metrics`, day grouping, current seat, top 10 | 200; 4 daily rows and `data_freshness_ts` | Plugin activity history is available. Plugin identities/content were not retained. |
| GET `wham/analytics/code-attribution`, day grouping, current seat, `group=workspace` | 200; empty data | Route responds, but this sample supplies no attribution metrics. |
| GET `wham/usage/daily-workspace-user-credit-usage`, `breakdown=model` | 403 | Denied for this request/context; no attempt to change accounts or bypass permission. |
| GET `wham/usage/daily-workspace-user-token-usage-breakdown`, `breakdown_by=model&modes=codex&modes=work`, day grouping | 400; fixed error classification `NoActiveWorkspace` | Enterprise-style query does not make the workspace report usable for the current context. |
| GET `subscriptions` | 400 | This bare route/header combination did not supply subscription metadata. Do not generalize to all possible clients. |
| GET `accounts/check/v4-2023-04-27` | 200; matching active monthly personal entitlement with renewal present | Useful source for the subscription-period view; account map aliasing must be handled (below). |
| GET `accounts/{account}/remaining_balance` | 200; `balance` is decimal string `"0"` | Native zero balance, not missing; decode decimal strings as well as numeric JSON. |
| GET `accounts/{account}/spend-controls/current-user/monthly-usage` | 401 | No usable report. Stopped this batch; subsequent ordinary `wham/usage` health read returned 200, so continued only the other independent endpoints, without retrying this denied route. |
| GET `wham/workspace-messages` | 404 | No report for this request. No message bodies retained. |
| GET `wham/analytics/daily-code-review-metrics`, day grouping/current seat | **200; empty data** | Supersedes the earlier unverified-route status: a live JSON response is corroborated. Populated-row schema, metric semantics and completeness remain unverified. |
| POST `wham/usage/thread_usage/query`, three recent local thread IDs | 403 | Direct request exposes the denial that app-server can map to unavailable. No estimated credit values obtained. |
| POST `wham/usage/thread_usage/query_v2`, same three IDs with creation times and empty descendant lists | 404 | No current-allowance task values obtained. This does not test complete descendant-group accounting or all threads. |

The POSTs were documented read-only query operations, not mutations. Thread UUIDs and creation
times were selected from native state SQLite using `mode=ro`; titles, transcripts and prompts
were neither selected nor transmitted. Output retains request count and availability, not thread IDs.
All requests used the same existing Codex context and fixed HTTPS host, with redirect rejection,
response-size and timeout bounds. No alternate accounts, credential refresh, new credentials,
admin API keys, purchase/reset actions, messaging actions or guessed account identifiers were used.

### Two useful parsing discoveries

The account-entitlement map initially appeared to contain two active subscriptions. Focused
inspection established **one distinct explicit account ID**, both entries matching the requested
account, identical records and identical renewal dates. These are duplicate account-map entries,
not evidence of two subscriptions. Deduplicate by native account identity while retaining conflicting
alternatives; never count map entries as subscription seats. The captured metadata is sufficient to
select this account's renewal context without user guesswork, although actual dates were not exported.

The remaining-balance value is a string, not a JSON number. An initial numeric-only summary reported
null; the follow-up established the value as decimal zero. This adds a concrete test case: missing,
null, numeric zero and decimal-string zero must not be conflated. No production parser changed.

Skill and plugin reports supplied different freshness timestamps (19:22 UTC versus 07:39 UTC on
the same day). That is direct evidence that a single shared report-as-of would be wrong; it is not
a measured processing-delay SLA. Preserve per-surface freshness.

### Recommended practical slice after this survey

An experimental personal dashboard can now be grounded in available **daily percentages,
daily tokens/activity, subscription renewal, extra-credit balance, reset inventory and skill/plugin
history**. Keep unavailable plan-history and per-thread costs visibly unavailable rather than blocking
the whole feature. Credit-valued allowance estimates still require an explicit pricing assumption on
this account because the daily native credit numerator is zero. No model or TT change follows yet.

The original probe scripts and sanitized results remain ignored in `.codex/temp/codex-repo-review/`.
At the survey stage only this summary and the product-direction note changed. Production
collection/storage and app builds/restarts followed in section 11, not during the survey.

## 11. Consolidated findings and implementation status — 2026-09-18

This section reconciles the supplied broader repository map with pinned source, actual probes
and the current TajsTokens working tree. It is supporting evidence, not a second roadmap;
[PROJECT.md Current work](../../PROJECT.md#current-work) owns delivery status and priorities.
Superlatives such as “best” or “most mature” are omitted: review depths differ and none of these
algorithms has yet won a controlled comparison against our retained corpus. The previously used
`Finesssee/Win-CodexBar` name is not independently verified here; the reviewed pin is under `nesszer`.

### What is implemented versus proposed

- **Implemented:** optional daily counts and relative-usage acquisition, typed immutable report
  snapshots in the existing server-evidence store, account-bracket correlation, report units and
  freshness, requested date range, plan and before/after window-duration/reset signatures.
  Quota history exposes the opt-in, manual collection, latest reported values and daily details.
  The normal server-evidence cadence is 30 minutes; current quota remains app-server-owned.
- **Auth boundary:** the separate experimental adapter reads the existing selected Codex home's
  access token/account into memory and sends bounded GETs to a fixed HTTPS origin. Redirects and
  cookies are disabled. It does not refresh OAuth, write `auth.json`, switch accounts, acquire
  browser credentials or silently fall back from app-server. Direct HTTP entails responsibility
  for credential handling and request behavior, but **does not require owning refresh/persistence**.
- **Proof:** the production adapter fetched 27 daily count rows (`credit`) and 31 daily relative
  rows (`percent`) with a matching account bracket. The Core suite passed 392 tests; the final
  Windows build passed with zero warnings/errors and deployed/restarted the daily app. This is
  not user visual acceptance or proof of longitudinal stability. The research/doc move is committed
  as `86fd457`; implementation was staged after a separate signing timeout at consolidation time.
- **Still proposed:** more app-server quota metadata, nullable local service-tier evidence,
  competing copy/interleave algorithms, even-burn presentation, API-price-weighted Model Lab
  baselines, native-credit calibration, renewal/entitlement and skill/plugin collectors, and
  historical semantic drift interpretation. Persisting a policy signature is not detecting or
  explaining historical policy changes. Do not imply these features shipped with daily reports.

### Corrected availability and uncertainty map

| Question | Current evidence / remaining uncertainty |
| --- | --- |
| Have native daily reports been fetched on this account? | Yes, both isolated probes and the implemented C# adapter. “Never fetched” is obsolete. |
| Are daily native credits nonzero during included usage? | In the sampled history, all reported daily count credits were zero despite nonzero tokens. The observed plan string was `prolite`; do not replace it with an assumed plan label. |
| Is there a stable credits / relative-usage ratio? | Not established on this account: no positive native numerator/percentage pairs. The author's ~49.897 credits/pp is account/era-specific, not a universal constant. |
| Does it survive resets, policy changes or model/effort/speed differences? | Unknown. Daily reports do not establish which present 5h/weekly meter a denominator describes. |
| Are delay, revision and date-boundary semantics established? | No longitudinal revision/latency study or cross-route boundary proof. Preserve independent freshness and snapshots; do not silently align different date ranges. |
| Is plan history available? | Tested `plan_limit_history?days=7` returned 404. This is an observed request outcome, not just a TUI message or proof of permanent nonexistence. |
| Are thread/task estimates available? | Direct sampled v1 returned 403 and v2 returned 404. V2 is no longer “not probed”; no actual task percentages were obtained. |
| Does code-review metrics exist? | A live 200/empty JSON response corroborates the tested route. Populated-row contracts, metric meaning and completeness remain unverified. |
| Is service-tier coverage sufficient locally? | Not measured on TajsTokens' corpus. TokenTracker's source comments describe its own corpus, not ours. |
| Are duplicates/interleaves canonically resolved? | No. Candidate fingerprints and competing algorithms are evidence for tests, not proof of independent usage or safe deletion. |

### Follow-up proposals worth carrying forward

The following list records the original proposals, not a current outstanding-work checklist.
The implementation status below supersedes its present-tense descriptions of missing fields.

| Item | Current implementation evidence | Boundary / remaining work |
|---|---|---|
| 1. Quota metadata | `CodexQuotaMetadataParser`, typed server evidence, Diagnostics; section 12 | Presence is not proof of stable semantics. The unspecified "two review fixes" cannot be audited without identifiers. |
| 2. Service tier | `CodexRolloutParser`, observatory persistence/replay and `CodexServiceTierEvidenceTests`; section 12 | Requested settings are not confirmed execution or billing speed. |
| 3. Reconciliation | `CodexReconciliationAudit`, `CodexSequenceComparison`, competing reducers and recovery fixtures; sections 14, 18–19 | No competitor earned canonical promotion. Desktop filename ownership was corrected without importing alternate paths. |
| 4. Baselines | `QuotaEvenBurn`, `ApiPriceWorkload`, cost evaluation and product diagnostics; sections 14–15 | Versioned price weights are counterfactual, not native credits or actual bills; no promotion. |
| 5. Acquisition | Opt-in `CodexBackendDailyEvidenceProvider`, stored daily reports, account-switch tests and one-shot CLI; section 20 | Available personal reports contain zero credits. Unavailable reports remain capability findings, not fabricated contracts or reasons for repeated denied probes. |
| 6. Compatibility/drift | `CodexDailyPairing`, `CodexEvidenceDrift`, parser/provider/persistence tests; sections 15–16, 20 | Tests cover the implemented surfaces, not every future plan/task contract proposed in section 6. No policy change is inferred from ordinary consumption. |

This map does not claim that every original proposed test is implemented. Plan/task acquisition,
native-credit scale validation and hypothetical backend extensions remain unsupported research;
the current product scope and forecast work are owned by `PROJECT.md`.

1. Preserve additional installed app-server quota fields (`rateLimitReachedType`,
   `spendControlReached`, `individualLimit`, `normalModelSlug`, credits and named-limit metadata)
   with explicit null/unknown handling. Their presence is schema evidence, not proof that every
   field is populated or semantically stable. The current C# quota projection does not retain
   the first four named fields. The supplied “two review fixes” has no issue/diff identifiers;
   it is not recorded as a verified outstanding defect or completion claim.
2. Add local service-tier observations with source order/turn association and unknown coverage.
   Retain observed spelling and null when absent; separate requested settings from confirmed
   execution/billing tier. Test first-turn absence, settings changes, restart/checkpoint replay,
   conflicting sources and copy attribution before equating local tier with backend `speed`.
3. Compare current accounting, TokenTracker-style lineage inference and CodexBar/Win-CodexBar
   containment using identical copy, inherited-prefix, interleaved, truncation and restart fixtures.
   Include totals-only `100 → 20 → 110`, total-plus-last counters, equal counters from distinct
   requests, bounded-cache eviction, file-order independence and checkpoint/full-replay parity.
   Keep physical occurrences even if a separately versioned projection identifies canonical usage.
4. Evaluate simple pace independently of Forecast: consumed 62% minus elapsed 40% gives a
   **22-percentage-point pace deficit**. Require a known compatible window/start/duration;
   resets, missing windows and changing duration must not generate a fictitious elapsed fraction.
   API-price-weighted workload belongs beside token/category/model-effort baselines in Model Lab,
   not automatically in production quota policy. Version price tables and retain unpriced coverage.
5. Continue the already-authorized experimental acquisition rather than reinstate “do not poll”.
   Keep daily counts, relative usage, workspace credit/token reports, credit events, profile activity,
   plan history, v1 estimates and v2 task percentages as distinct typed contracts when added.
   Prioritize actually available personal-account reports; do not repeatedly probe denied routes.
   A Codex-owned analytics/plan/task app-server seam remains preferable long term. Names such as
   `account/analytics/read` are design suggestions, **not existing methods**; the SDK adds no
   hidden billing capability (section 8).
6. Extend the section 6 tests for account/seat/date/cycle compatibility, revisions, negative drift
   cases, parser-unit changes, missing versus zero, latest-failure visibility and privacy. Track
   unit/plan/window changes separately from ordinary resets, reporting lag and source-coverage changes.
   Before native-credit calibration, distinguish included workload credits, balance debits,
   reset entitlements and percentages; do not fill missing native credits with a price estimate
   and subsequently call the resulting ratio provider-native.

**TT remains deferred, not disproved.** Prefer observed native quantities or direct
feature-to-quota models where supported. The current zero-credit history does not supply a stable
native intermediate scale, but neither does it justify inventing TT. Any learned scalar or new
weighting still needs chronological evaluation and existing promotion gates. Preserve selected
typed evidence and provenance—not raw payloads “aggressively”; retention/privacy boundaries apply.

## 12. Evidence slice and reconciliation experiment — 2026-09-18

The existing app-server quota reader now retains a separate typed `QuotaMetadata` observation
in the existing server-evidence ledger. This includes named/legacy buckets even without a
supported quota window, nullable policy/spend-control metadata, balance strings and window/reset
values. It is not a new polling source or a native workload-credit measurement. Current quota
gauges continue to use their existing selection rules. Optional-metadata validation failure does
not discard otherwise usable window values. Frequent metadata does not consume the bounded
account-activity comparison history budget.

Rollout workload evidence now retains raw nullable service-tier settings, preserving source
identity, byte order and any native turn ID. Schema migration and parser replay recover these
records from existing sources. There is no new inferred token-to-tier attribution, pricing default
or accounting change. Diagnostics exposes physical setting-record counts, not canonical requests.
Live deployment verified schema 7, account-scoped quota metadata, and initial `flex` setting records.
That check exposed the state-index fast path skipping unchanged files; schema 4 now invalidates
only the disposable index hints atomically, with rollback/retry coverage. Initial counts are not
complete-corpus coverage and should not be presented as such.

Original test-only scalar competitors make the assumptions testable without importing code:

| Synthetic sequence | Incumbent | High-watermark-only | Bounded total-minus-last lineage |
| --- | ---: | ---: | ---: |
| Totals 100 → 20 → 110, no last counters | 190 | 110 | 110, marked ambiguous |
| Same totals, last counters 100 → 20 → 10 | 130 | 110 | 130 |
| Totals 100 → 20 → 100, last 100 → 20 → 100 | 220 | 100 | 120 |
| Reset 100 → 20 → 30, last 100 → 20 → 10 | 130 | 100 | 130 |
| Partial history totals 1000 → 1100, last 10 → 20 | 30 | 1100 | 30 |

These numbers are algorithm outputs, not independent workload truth. The high-watermark-only
baseline deliberately ignores last counters; it is not a full CodexBar reproduction. The lineage
baseline uses a 32-head cache and a `total-last` predecessor assumption, not native stream IDs
or a full TokenTracker port. Tests also show equal counters can hide independent requests,
eviction can make old replay look new, copies remain independently counted, and serialization at
every restart boundary preserves each candidate's result. Existing inherited-prefix and source-
rewrite tests remain part of the full suite. Real-corpus comparison is still needed before choosing
a canonical projection. Plausible assumptions can support experiments without relabelling inferred
results as provider facts or waiting indefinitely for perfect identifiers.

## 13. Native log evidence — 2026-09-19

Read-only inspection of the live `logs_2.sqlite` used a short SQLite read transaction and
returned allowlisted aggregates, not raw bodies, identities or credentials. The existing
[SQLite reference](CODEX_SQLITE_SCHEMA.md#installed-logs-corroboration--2026-09-19) correctly
describes its schema. Existing [server-usage contracts](CODEX_SERVER_USAGE.md) remain useful;
logs supplement runtime evidence rather than replace those contracts.

| Observed evidence | Useful interpretation / boundary |
| --- | --- |
| About 778 `post sampling token usage` rows, with `total_usage_tokens`, compaction scope/limit and full-context limit fields | Context occupancy and compaction diagnostics. Upstream explicitly assigns `total_usage_tokens = token_status.active_context_tokens`; never sum these as consumed tokens or TT training workload. Observed scope/full-context limits were 244,800/258,400, not universal constants. |
| About 1,175 `getAuthStatus` request-name rows | Runtime capability discovery beyond the generated public catalog. Invocation is not successful response or credential export. Upstream defaults `includeToken` and `refreshToken` to false and withholds host-owned credentials. No token extraction was attempted. |
| About 71 `thread/tokenUsage/updated`, 68 `account/rateLimits/updated`, and two `thread/settings/updated` rows | These sampled notifications contain names, not quantitative payloads. Useful for lifecycle/coverage timing, not reconstructing historical allowance percentages. |
| Transport completion rows for GET `/backend-api/codex/models`, POST `/backend-api/codex/analytics-events/events`, POST `/backend-api/ps/apps/batch`, and GET `/backend-api/accounts/{id}` | All selected route groups had observed HTTP 200 responses. Analytics-events is telemetry ingestion, not a report endpoint. Routes were extracted only from the actual HTTP logger, with queries and identifiers removed. |
| Three observed `service_tier` values parsed as `flex` in selected turn logs | Additional local tier corroboration, not proof of execution/billing tier or equivalence with backend `speed`. |
| Output-item identifiers and optional thread/process identities | Potential correlation evidence for replay/interleaving experiments. Repeated item observations are not proof of repeated billing or a validated canonical token identity. |

The selected HTTP logger slice exposed no numeric quota/credit headers and no daily WHAM report
calls. This is **not** evidence that those routes are unavailable: retention, logger selection and
different transport owners limit coverage. Endpoint strings inside logged tool commands are not
transport observations and were excluded. No new provider-credit scale was discovered.

Source corroboration at `7498521d288b9b3b96ffba4eedf089d8d6e06a84` (not exact-build proof):

- `codex-rs/core/src/session/turn.rs:568`: active-context assignment and compaction fields.
- `codex-rs/app-server/src/message_processor.rs:624`: request-name logging;
  `app-server/src/outgoing_message.rs:758`: notification-name logging.
- `codex-rs/app-server/src/request_processors/account_processor.rs:1035`: legacy auth-status
  options and credential-export exclusions.
- `codex-rs/http-client/src/client.rs:118`: completed-request method/URL/status/header logging.
- `codex-rs/core/src/stream_events_utils.rs:326`: tool payload previews demonstrate why raw logs
  remain content-bearing; line 448 records output-item identity.

**Practical next slice, not implemented:** extend the existing bounded native-log inspector with
allowlisted context/compaction and transport/capability diagnostics. Before persisting derived
records, test logger/version-specific extraction, missing fields, content-string false positives,
query/header redaction, repeated observations, pruning and database recreation. Context limits
may indicate a model/configuration change, not quota-policy drift; preserve that distinction.
Any item-ID reconciliation experiment should compare same-thread/process and cross-process
observations against rollout evidence before changing canonical accounting. No new acquisition,
raw-log retention, endpoint probes or TT normalization were introduced by this investigation.

## 14. Real-corpus reconciliation and model continuation — 2026-09-19

The scalar experiments from section 12 now share their implementation with a read-only corpus
audit, rather than maintain a separate CLI approximation. Existing restart, repeat, interleave
and eviction fixtures exercise that implementation. Additional fixtures check inherited-prefix
exclusion, physical copies, malformed-file exclusion, last-only counters and aggregate-output
privacy. The native parser/file reader own record and session semantics; no replacement parser,
new durable schema, native mutation or production deduplication was introduced.

Run locally from the repository root:

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation -c Debug -- --reconciliation "$env:USERPROFILE\.codex"
```

Pass the actual selected Codex home if it differs. The command scans `sessions`,
`archived_sessions` and `archive`; other roots are not implicitly included. Reports exclude
files whose size/write time changed during reading and files with read/JSON failures. This is
not an atomic whole-corpus snapshot or protection against an undetectable same-metadata rewrite.
It keeps hashes only in memory and prints aggregate numbers, never native identities or payloads.

Initial sample: 367 stable files, zero excluded files, 66,263 owned token observations, no
incomplete cumulative snapshots, one totals-only drop, and 28 files where candidate totals differ.

| Counter projection | Physical-file total | Difference from incumbent |
| --- | ---: | ---: |
| Incumbent reducer | 9,161,300,274 | — |
| High-watermark-only | 8,855,223,082 | −306,077,192 |
| Bounded total-minus-last lineage | 9,162,150,860 | +850,586 |

No cross-file fingerprint repeated under the strict session/time/model/effort/both-snapshot key.
This is narrower than all possible copy detection: inherited prefixes are withheld by the parser,
cross-session similarities do not match, and repeated rows within one file are not this metric.
Earlier historical repeat-fingerprint results used different predicates/data and are not refuted
by this result. Scalar containment still discards possible legitimate reset work; lineage still
assumes predecessor identity and cannot recover evicted repeats. Neither result establishes truth
or warrants changing the canonical reducer. Candidate counters lacking complete valid cumulative
evidence are explicitly counted as incomplete, not treated as a zero watermark.

The separately delivered even-burn Overview comparison is descriptive only and does not address
TT or change these model evaluations. Native credits remain zero in the observed daily report;
local learned workload weights remain a plausible research path, not newly discovered credits.

### Chronological evaluation refresh

The existing `--cost`, `--composed`, `--composed-strict` and `--transfer` commands were run
read-only against the owned database. Each command reads its own consistent transaction; this
was not one frozen snapshot across commands. The cost snapshot at 2026-09-18 22:37:38 UTC had
341 usable intervals across separate cohorts. The authoritative weekly half-hour cohort had
20 training and 44 held-out intervals across only two held-out reset generations.

| Actual-workload cost candidate | Held-out mean envelope-distance loss (pp) |
| --- | ---: |
| Pace | 1.3741 |
| Total tokens | 0.3191 |
| Token categories | 0.3831 |
| Model/effort | 0.2786 |
| Context ablation (oracle explanatory input) | 0.1906 |

These are explanatory cost losses, not forecasting performance. No candidate earned the
material-win gate. Reconstructed end-to-end replay had 39 paired outcomes across two resets
(five additional targets lacked composition): total-token loss 1.1507pp, categories 1.0774pp,
model/effort 1.1901pp, pace 1.3592pp, **incumbent 1.0586pp**. Better explanatory weights still
do not imply a better product forecast. The context ablation is not eligible for live promotion.

Strict collection-time replay withheld all 44 potential outcomes under its combined availability/
composition gate; the current report does not distinguish those reasons. The older five-outcome
strict result is not current proof. The next model follow-up should diagnose this loss of strict
coverage, including the builder's broad workload-availability dependency and newly backfilled
metadata, before changing any gate or treating the exclusion as ordinary model failure. Unknown
capture times and genuinely late evidence must remain excluded. Transfer evaluation still found
no compatible recorded-account regimes, so it cannot establish a transferable TT scale.

Validation: 417 Core tests passed; Windows Debug build succeeded with zero warnings/errors and
installed/restarted daily build `20260918T223931937Z-d18f5867`. Native corpus audit and the four
retained-data evaluations completed. No model promotion, accounting rewrite, signing or push.

## 15. Strict replay, drift diagnostics and API-price baseline — 2026-09-19

### Strict coverage diagnosis and correction

The new withholding diagnostics reproduced the previous zero-outcome result: all 44 targets
failed `training-collected-after-origin`. `QuotaCostObservationBuilder` had included every
historical workload observation's collection time in the frozen training dependency. That made
newly backfilled metadata invalidate token-only models which never used those fields.

`quota-cost-observations/v2` preserves the broad full-feature timestamp and separately derives
token-cost availability from quota labels/prefix, token amounts/model/effort and any ownership
assertion. `composed-quota/v3` uses the latter for its existing three token-based candidates.
Activity/context/runtime oracle ablations remain outside composed promotion. Late or unknown
token collection, late meters and retrospective ownership declarations still fail closed.
Tests cover that boundary and prove that a late unused tier setting cannot change eligible
predictions. The UI and CLI show overlapping exclusion reasons rather than one opaque total.

Corrected strict replay: 17 held-out targets, one reset generation; 25 targets still fail late
training, two lack composition. Interval losses: incumbent 0.2189pp, total 0.4559pp, categories
0.5239pp, model/effort 0.5368pp, pace 1.3173pp. This fixes dependency provenance, not the model's
relative performance, and supplies neither sufficient resets nor an earned promotion.

### Rebuildable semantic change records

`codex-evidence-drift/v1` compares adjacent compatible immutable observations. Each derived
signal carries stable ID, policy, before/after observation IDs, surface/account, field values,
first-observed collection time and **unknown** policy-effective time. The persisted observation
ledger remains the durable source; no redundant derived table or raw response retention is added.

- Reported plan, limit identity, model alias, duration, individual limit and unlimited-credit
  flag changes are configuration evidence, not proof of a changed quota denominator.
- Bucket/field disappearance is missingness, never automatic revocation. Blocking reason and
  spend-control state are separate operational signals.
- Failures, client/contract changes, account switches and overlapping fetches prevent bridging
  semantic comparisons. A/B/A account history is not silently joined.
- Historical daily revisions require overlapping completed UTC days and compatible units and
  grouping. Current-day growth, moving range edges and freshness advancement alone are not drift.
- Quota consumption, ordinary reset movement, balance depletion, and decimal formatting alone
  generate no policy signal. Null remains distinct from zero and false.

Diagnostics displays the latest 24 signals from independently bounded per-surface histories
(512 fetches / 5 MiB each). These limits are disclosed; zero signals does not establish stability.
Tests cover resets/depletion negatives, account/version/failure barriers, missingness, stable
provenance, reordered named/legacy views, unit changes and revised versus in-progress days.

### API-price weighting, not native credits

`openai-api-standard-short-2026-09-19/v1` uses the inspected standard short-context rates from
[official API pricing](https://developers.openai.com/api/docs/pricing) for GPT-6 Astra and the
three GPT-5.6 models, plus the published model-page rates for
[GPT-5.5](https://developers.openai.com/api/docs/models/gpt-5.5),
[GPT-5.4](https://developers.openai.com/api/docs/models/gpt-5.4),
[GPT-5.3-Codex](https://developers.openai.com/api/docs/models/gpt-5.3-codex) and
[GPT-5.2-Codex](https://developers.openai.com/api/docs/models/gpt-5.2-codex).
The source snapshot is a fixed retrospective weighting choice, not historical price evidence.
The implementation matches exact supported names; it does not guess undocumented aliases.

The weighted categories are disjoint input, cached input, cache writes (only where a rate is
documented), non-reasoning output and reasoning output. Unknown model/cache-write rates retain
unpriced event/token coverage, never a zero-cost substitute. Standard/short-context weighting
does not assert actual service tier or request context length and omits regional/tool modifiers.
It is neither estimated subscription billing nor a provider-credit fallback.

The existing frozen-prefix interval model fits one scale coefficient on the first 20 targets.
Unpriced training prevents fitting; unpriced held-out targets are excluded and matched-outcome
pace/total losses are reported. The candidate cannot earn a material-win/promotion label.
Chronological native weekly replay priced all 20 training and 44 held-out half-hour targets:
API-weight loss 0.3644pp, matched total-token 0.3191pp, matched pace 1.3741pp (two held-out resets).
This sample does not support preferring published-price weighting to raw tokens.

Validation: 425 Core tests passed; Windows Debug build succeeded with zero warnings/errors and
installed/restarted daily build `20260918T230426495Z-59db2cb3`. Strict replay was reproduced
before and after the dependency correction; the API-price baseline ran on retained chronological
data. Drift extraction and persisted-ledger read integration have automated proof; native visual
acceptance is not claimed. Changes are staged, without signing or push. Remaining review work
is tracked in PROJECT.md, including daily native pairing diagnostics and broader reconciliation.

## 16. Native pairing and forecast horizons — 2026-09-19

The native daily pairing diagnostic now consumes the existing immutable report snapshots in
Analytics and the evaluation CLI (`--daily-pairing <telemetry.db>`). It does not acquire evidence,
change canonical accounting, fit TT or convert a daily ratio into current five-hour/weekly quota.
Latest failures supersede earlier successes. Same account bracket, versions, range, units, grain,
plan, collection policy and freshness are checked. Completed UTC days only are compared, excluding
the requested end date because endpoint inclusion semantics remain uncertain. Zero/missing native
credits, on-demand spill (including unknown spill), tiny usage and incompatible reports are exposed
as separate exclusions. Revisions replace a snapshot; they are never summed across polls.

Any resulting credits/percentage-point ratio is a hypothesis. Common product/seat scope and
historical denominator era remain unverified. Tests can construct 49.897, but that is arithmetic
verification, not independent corroboration of the repository's observed scale. The current owned
database inspection returned no readable latest daily pair; earlier transient probes do not provide
a stored paired series. No authenticated request was made for this diagnostic run.

The accepted forecast direction is now **Nowcast / Session outlook / Quota outlook / Workload
planner**, with implementation status owned by PROJECT.md. Separate activity probability from
workload conditional on activity, and separately estimate quota cost given workload. Short-horizon
predictability must still be measured. Do not publish illustrative probabilities as model output,
multiply conditional interval endpoints into purported calibrated bounds, or rename the existing
two-hour extrapolation as a nowcast. Idle handling must distinguish missing collection, quiet
running work and observed inactivity. Long-horizon output should describe pace/history scenarios,
not claim knowledge of future human decisions. This direction changes the next forecast slice,
not native quota semantics or the already-completed cost-model evaluation.

Validation: 438 Core tests passed, including 13 pairing cases. Windows Debug build passed with
zero warnings/errors and installed/restarted `20260918T231501762Z-3182d9d4`. No visual acceptance
or new forecast-model validation is claimed. Changes are staged, not signed or pushed.

## 17. Activity-aware local nowcasts — 2026-09-19

Live recorded-token forecasts now target 5/15 minutes, not 30 minutes/two hours. A versioned
ten-minute observed-activity gate pauses extrapolation after quiet periods. Quiet open turns are
not considered guaranteed running work; missing terminals cannot extend predictions forever.
Recent settings/context metadata alone cannot manufacture activity. The UI distinguishes quiet
open turns, no recent observed activity, unknown evidence and failed/stale refreshes. No probability
of human activity is reported. Quota outlook remains independent and unchanged.

Short-horizon chronological replay uses the same eligibility gate and retains earlier-only fitting,
selection and empirical intervals. On the retained corpus, 4,688 five-minute origins yielded
selected-policy MAE 1,385,011 tokens versus recent-30m pace 1,417,181; 1,545 fifteen-minute origins
yielded MAE 3,777,833 versus 3,812,848. Empirical 80%-target ranges covered 81.1% and 81.0% of
4,668 and 1,525 eligible outcomes. These are reconstructed event-time corpus results, not strict
historical deployment proof or a promise of useful precision. Fifteen-minute recent-median error
was lower (3,598,647), so the selected policy is not uniformly best. Model Lab retains all competing
baselines and adds zero workload as an explicit evaluation-only inactivity baseline.
Zero-workload MAE was 1,902,593 and 5,353,439 tokens respectively, worse than the selected policy.

This is not yet the proposed hurdle model. Predicting active probability, future workload
conditional on activity, conditional session outlook and calibrated joint burn uncertainty remain
separate next work. Ten minutes is an explicit initial assumption, not an inferred universal rule.

Validation: 441 Core tests passed. Windows Debug build passed with zero warnings/errors and
installed/restarted `20260918T232412503Z-08000f0d`. Retained-corpus replay includes pace, median,
regression and zero-workload comparisons; live query reported quiet open turns and paused output.
Native UI visual acceptance is not claimed. Work is staged, without signing or push.

## 18. Ordered reconciliation candidates — 2026-09-19

`reconciliation-audit/v2` extends the read-only corpus CLI with ordered token-sequence comparisons.
It retains the existing same-owner fingerprint count and separately omits owner when looking for
cross-file candidates. Timestamp, model/effort, full cumulative category vector and last-usage
vector still participate. Equal sequences, strict prefixes, divergent prefixes and non-prefix
overlap are mutually exclusive pair classes; shared-prefix occurrence counts are pairwise, not a
deduplicated workload total. Repeated values inside one sequence retain multiplicity/order. An
inverted index limits comparison to files sharing at least one fingerprint. Hashes, owners and
paths never leave the invocation. These are token evidence relationships, not byte-copy proof or
native request identity. Account compatibility is not established, so no automatic merge follows.

The 367 stable files / 66,263 owned observations produced no candidate pairs, even without owner
matching. The first scan excluded 3,949 pre-ownership records; the final scan excluded 3,996 as
the source corpus continued growing, with owned-token totals unchanged and zero equal-total
category-change observations. Some excluded records may contain
inherited parent history; the production parser's exclusion is preserved rather than bypassed to
manufacture accounting evidence. That blind spot remains explicit next research. Counter-category
reshuffles at unchanged scalar totals now have a separate diagnostic and restart/retry fixture.

Tests cover exact sequences, prefix plus suffix, divergent tails, reordered overlap, repeated
occurrences, empty/unrelated streams, processing-order invariance, copied files and aggregate-only
privacy. Canonical accounting and durable evidence remain unchanged; no competitor was promoted.

Validation: 449 Core tests passed; final corpus audit completed. Windows Debug build passed with
zero warnings/errors and installed/restarted `20260918T233022949Z-5068df91`. Changes staged;
no signing or push. This diagnostic adds no new native acquisition or retention.

## 19. Pre-ownership investigation found a Desktop filename bug — 2026-09-19

The v3 audit splits excluded token-count records from other records, unowned whole files from
prefixes, and missing filename identity from missing matching metadata. Before the fix, 4,068
records / 553 token-count records belonged to five wholly unowned files. No successfully owned
file contained an excluded prefix. All five filenames had the observed Desktop form
`rollout-<timestamp>-<thread UUID>_<suffix UUID>.jsonl`. The existing last-UUID rule incorrectly
selected the suffix. Both metadata identity fields matched the first UUID, and the native catalog
confirmed all five thread identities: three exact selected paths, two alternate paths.

This is installed-runtime evidence, not a claim about an independently verified public upstream
contract. These files report CLI versions `0.150.0-alpha.8`, `0.155.0-alpha.2.6`, and
`0.156.0-alpha.2`, with Desktop originator. No native identity, path or content was exported.

The parser now recognizes only the exact timestamp-plus-two-UUID Desktop filename shape and uses
its thread UUID. Matching metadata is still mandatory; a copied prefix or metadata matching only
the suffix cannot establish ownership. Arbitrary renamed multi-UUID files retain the old rule.
The fix is shared by ingestion, coverage and read-only alternate comparisons. Only affected paths
change typed-parser version; state-index v5 clears disposable selection hints transactionally.
Unchanged non-suffixed files retain checkpoints. Alternate paths remain inspection-only while the
native catalog is available. This corrects lost selected-source evidence, not cross-file deduping.

Tests cover wrong suffix-owner metadata, parent prefixes, native token ownership, restart state,
old-parser replay, append idempotence and cache-migration failure/rollback/retry. No provider quota
semantics or normalization unit changes follow from this filename correction.

Post-fix corpus audit: all 367 files stable and owned, 66,839 token observations, zero pre-ownership
exclusions and zero cross-file candidate pairs. There were 29 scalar-competitor disagreement files,
one totals-only drop and zero equal-total category changes. Physical-file experiment totals were
9,239,354,220 incumbent, 8,945,479,838 containment and 9,240,204,806 lineage; these include alternates
for research and are not canonical account usage. The corpus grew between scans, so the increase
is not attributed solely to recovered historical rows.

Validation: 453 Core tests passed. One initial run hit an unrelated transient directory-move
access denial in AppDataLocation; its targeted retry and the full suite passed. Windows Debug
build passed with zero warnings/errors and deployed/restarted `20260918T234102623Z-5f987e56`.
After refresh, owned storage had four Desktop-version checkpoints, all native-catalog-selected,
and 511 native token events for their privacy-safe source labels; state-index version was 5.
The wider catalog can select paths outside the corpus scanner's roots. No alternate import or
UI visual acceptance is claimed. Changes staged, without signing or push.

## 20. Retained daily reports and account-switch protection — 2026-09-19

The missing retained daily pair was explained by `ExperimentalCodexBackendEnabled=false`, not
endpoint failure. An explicitly authorized one-shot collection using the existing adapter returned
Available for both routes: 27 count-report dates and 31 relative-usage dates. All 27 reported native
credit values were zero; all on-demand-credit fields were absent. Four relative dates had no count
row, six had zero/tiny relative usage, and one was the incomplete/end-boundary date. These exclusions
overlap. Pairing found zero conversion hypotheses. Typed observations are now retained in the
existing ledger; no raw payload, credentials or identifiers were exported. Background polling
remains disabled. Zero reported credits do not mean zero work or prove credits can never exist.

`--collect-daily-evidence <telemetry.db>` is now an explicit one-shot research command using the
same provider/storage owners; it does not change saved preferences or add a silent fallback.
`--daily-pairing` remains the retained-evidence path. The negative native-scale result leaves TT
eligible for evaluation, not automatically justified or calibrated.

Transport tests exposed and addressed a separate identity hazard: before/after backend identity
requests use the captured bearer, so both can succeed for the old account after the local selected
account changes. The adapter now rereads selected account identity at the end and discards reports
on a switch. Same-account token rotation alone does not discard evidence. A 401 on either identity
bracket now reports AuthenticationRequired rather than generic Error; no refresh is attempted.
Tests use synthetic in-memory auth and a fixed-route handler, with assertions that credentials/raw
account IDs never enter retained observations. Disabled collection reads neither auth nor network.

Validation: 458 Core tests passed. Retained-evidence CLI reproduced the live collection's zero-ratio
result without another backend call, and saved background opt-in remained false. Windows Debug
build passed with zero warnings/errors and installed/restarted `20260918T234940099Z-df4880f9`.
Changes staged, not signed or pushed. No native UI visual acceptance is claimed.

## 21. Recorded-activity and conditional-session decomposition — 2026-09-19

`recorded-session-outlook/v1` separates the probability of **any positive local token recording**
in a 30/60-minute interval from the workload conditional on that event. It does not claim human
presence, continuous activity, activity at the horizon endpoint, or complete account coverage.
The only inputs are already-retained token observation time, collection time and amount. No new
tool-call/completion, client/generation, prompt, result or other native metadata retention was
introduced. A future expansion requires an explicit per-field retention/privacy contract first.

Training uses the preceding 120 matured, disjoint origins, stratified by recent-token age when
at least 20 matching origins exist, otherwise a labelled global fallback. Activity frequency uses
Beta(1,1) smoothing. Positive-target mean and empirical 10–90% range need eight positive examples.
Expected work is the product of probability and conditional mean, never multiplied range endpoints.
UI probability/expected mean need 64 earlier same-group held-out outcomes, binned calibration error
<=0.1 and Brier no worse than earlier-only global frequency. The gate is an empirical assumption,
not proof of permanent calibration. Quiet/stale/sparse states remain explicit. Model Lab and the
`--session-outlook` CLI expose the methodology and matched outcome diagnostics.

Retained-corpus reconstructed replay produced:

| Horizon | Activity outcomes / positive | Brier / frequency baseline | Binned calibration error | Conditional MAE / matched pace | Expected MAE / matched pace | Historical-range coverage |
|---|---:|---:|---:|---:|---:|---:|
| 30 min | 1,701 / 919 | 0.146 / 0.234 | 0.0145 | 6.13M / 6.66M tokens (851 outcomes) | 4.97M / 5.20M tokens | 74.5% |
| 60 min | 839 / 506 | 0.153 / 0.234 | 0.0235 | 10.51M / 12.03M tokens (499 outcomes) | 7.73M / 8.46M tokens | 70.9% |

These results support an explicitly conditional local-work scenario, not a reliable quota-burn
distribution. The ranges are deliberately labelled historical rather than calibrated 80% bands.
No cost-model promotion, TT scale, native credit conversion or new quota-policy inference follows.
Tests cover chronological maturity, non-overlap, future/late evidence exclusion, sparse/idle cases,
positive-target conditioning, decomposition arithmetic and same-group probability withholding.

The CLI was then aligned with the actual live reader's **30-day lookback** rather than the initial
January-to-date research run above. In that live-path-sized replay, 30/60-minute activity outcomes
were 421/201 (263/137 positive), Brier 0.137/0.169 versus 0.241/0.222, and calibration error
0.038/0.030. Conditional MAE was 8.98M/16.38M versus matched pace 10.00M/18.54M over 250/137
positive outcomes. Expected-token MAE was 7.61M/13.47M versus 8.28M/14.28M over 345/201 matched
outcomes. Historical-range coverage was 74.0%/73.7%. This confirms direction but also substantial
error; the longer-history table must not be presented as the live window's validation result.

Validation: 462 Core tests passed, including the final matched-outcome count checks. Windows Debug
build passed with zero warnings/errors and deployed/restarted `20260919T000133149Z-3bd7153f`.
Both research and live-lookback CLI evaluations ran against owned retained evidence only.
Changes staged; no signing, push or native UI visual acceptance claimed.

## 22. Joint quota-range replay (2026-09-19)

`composed-quota/v4` shares the live range calibration with chronological replay. Each range
requires eight earlier completed reset generations; labels must be available at the origin.
Strict replay uses actual collection time, reconstruction uses event time. Repeated polls do not
create independent reset generations. The radius uses generation-maximum absolute composed
errors, an empirical 80% target and a one-percentage-point floor. Model Lab and CLI report range
origins, coverage of reported remaining quota and mean width, without claiming latent coverage.

After Desktop historical recovery, the native 30-minute cohort currently has no strict held-out
outcomes: 46 origins are excluded for training collected after the origin and one lacks usable
composition. The earlier 17-outcome result is a dated pre-recovery snapshot. Backfilled tokens
must not qualify historical deployment evidence. No ranges or models were promoted.

This closes the shared-calibration/coverage diagnostic gap, not the separate conditional-session
quota distribution. Endpoint activity prediction and any new metadata retention remain separate
future decisions; the implemented session target is any positive recorded work in the interval.

Validation: 463 Core tests passed; Windows Debug build passed with zero warnings/errors and
deployed/restarted `20260919T001245160Z-957c09b1`. Strict replay ran against owned evidence.
Changes staged without signing or pushing; native UI visual acceptance remains unclaimed.

## 23. Include completed quiet session outcomes (2026-09-19)

Audit found that session replay stopped at the last token timestamp. This censored completed
quiet targets in the retained snapshot's tail until another token arrived, biasing the activity
sample toward continuing work. `recorded-session-outlook/v2` now matures targets through the
earlier of requested evaluation time and dataset capture. The existing two-hour origin-recency
bound still prevents extrapolating multi-day abandoned sessions. Incomplete targets stay absent;
an aging snapshot cannot create new negative labels. The target remains recorded local work,
not proof that collection was complete or that a human stopped working.

A regression fixture verifies quiet outcomes without a subsequent token, stale-snapshot stability,
capture-boundary exclusion and bounded origin eligibility. The owned 30-day replay currently
remains 421/201 outcomes for 30/60 minutes with Brier 0.137/0.169 and historical-range coverage
74.0%/73.7%; its actively updating tail did not expose the synthetic stopped-session case.
No quota promotion, collection, retention or durable schema change accompanies this correction.

Validation: 464 Core tests passed; Windows Debug build passed with zero warnings/errors and
deployed/restarted `20260919T001602113Z-5fd658c7`. Work staged; no signing or push attempted.

## 24. Session-workload to quota evaluation (2026-09-19)

Added `session-quota-evaluation/v1` in Model Lab and the read-only `--session-quota` CLI.
The existing duration/session-count planner is not a token-cost estimator and is not reused under
a misleading name. This comparison instead uses the existing first-20-interval frozen total-token
cost model, separately for each known account/source/plan/bucket and 30/60-minute target.
Session predictions are reconstructed at each quota origin using preceding events and matured
targets; cost fitting never consumes held-out workload. Local work remains co-observed evidence,
not proven exhaustive account attribution. This is retrospective, not collection-time validation.

Real polls rarely land exactly 30/60 minutes apart. An initial exact-duration restriction withheld
all real outcomes. The corrected evaluator trains the workload model for the actual elapsed
horizon accepted by the existing five-minute meter tolerance, rather than scaling a 30-minute
prediction or silently treating different durations as identical. Quiet/sparse origins stay withheld;
quiet outcomes after eligible origins remain in unconditional scores, not conditional-positive MAE.

| Target | Outcomes / positive / resets | Conditional MAE / matched pace | Expected MAE / matched pace | Expected envelope loss / pace |
|---|---:|---:|---:|---:|
| 30 min | 32 / 31 / 2 | 1.865 / 2.657pp | 1.847 / 2.576pp | 1.013 / 1.723pp |
| 60 min | 9 / 8 / 1 | 4.536 / 4.584pp | 4.317 / 4.089pp | 3.317 / 3.223pp |

The 30-day retained-data run withheld 15/3 origins. Joint expected-quota bands use prior completed
generation-maximum absolute end-to-end errors and the existing eight-generation calibration rule
(80% empirical target and one-point floor), never multiplied component endpoints. Neither horizon
has qualifying bands. Conditional quota ranges and live promotion remain unsupported; the mixed
result is not evidence for a TT scale. The live activity-probability gate is not a selection filter
for this research candidate, which is explicit in the methodology.

Tests cover future-work exclusion, cohort isolation, absent account identity, horizon-specific
training, polling jitter and conditional versus unconditional outcome selection. No native
acquisition, retention, auth, canonical accounting or persistence schema changes were made.

Validation: 468 Core tests passed; Windows Debug build passed with zero warnings/errors and
deployed/restarted `20260919T002408180Z-e57eb8c3`. Work staged without signing or pushing.
The Model Lab code path builds successfully; native UI visual acceptance is not claimed.

## 25. Compare session quota against the incumbent (2026-09-19)

`session-quota-evaluation/v2` adds the existing quota policy as a stronger baseline, replayed
within the same source/account/plan/window cohort. Matching requires both identical origin and
outcome timestamps. Missing predictions remain absent; candidate and incumbent errors are both
computed on the resulting paired subset, whose count is shown in Model Lab and CLI.

The owned-data replay has 32 paired 30-minute outcomes: candidate/incumbent MAE **1.847/2.062pp**,
with measurement-envelope loss **1.013/1.234pp**. At 60 minutes only four of nine outcomes match:
paired MAE **3.110/3.087pp**, envelope loss **2.110/2.281pp**. The earlier 4.317pp full-sample
hourly candidate MAE must not be compared with this four-outcome incumbent value.

This strengthens the baseline comparison, not the promotion case: two/one reset generations and
retrospective reconstruction remain insufficient for live selection or uncertainty claims. Tests
check replay-derived errors, exact pair counts, same-account isolation and absent comparisons.

Validation: 469 Core tests passed; Windows Debug build passed with zero warnings/errors and
deployed/restarted `20260919T002751704Z-b6f01560`. Changes staged; no signing or push attempted.

## 26. Frozen TT basis prototype (2026-09-19)

Implemented `tt-lab/v1` in Model Lab and read-only `--tt`. This is an experimental local
workload index, not a provider quantity or subscription wallet. The first 20 compatible
known-account cost intervals fit the existing nonnegative category model. A reference basket
of one million tokens in the first positive training interval's mix is worth 1 TT. Model/effort
support is pooled; it does not claim model-specific premiums. Tier and context are unmodelled.

`TtWorkloadBasis` defensively copies its values and hashes exact coefficients, reference basket,
category support, sorted model/effort support and versioned input semantics. Unsupported categories,
incomplete model/effort mix, and category/total disagreement cannot produce a complete score.
The next 20 disjoint intervals fit a separate nonnegative quota/TT conversion and local raw-token/
full-vector competitors. Later supported intervals are held out. Changing calibration/outcome
labels does not alter the frozen scoring basis or TT assigned to unchanged workload.

The current weekly/half-hour basis is
`tt:cfc1cbecfe08ff053f7de21787d959f79a3b4a9435fc3be87737a9aaf7ca6ca7`.
It supports observed `gpt-6-astra` / `low`, with no cache-write support. Category order is
uncached, cache-read, cache-write, non-reasoning output, reasoning output; reference counts are
approximately 7,144.24 / 990,739.37 / 0 / 1,168.58 / 947.81. Exact values and coefficients are
available in CLI output; rounded documentation values are not a basis serialization.

On 27 later intervals from **one reset**, all supported, scalar/full-vector/raw-token
measurement-envelope losses were **0.0085 / 0.0810 / 0.2419pp**. Displayed-delta MAE was
**0.4868 / 0.5067 / 0.7908pp**; zero-use envelope loss was **1.7778pp**. The separate fitted
conversion was 0.274907pp/TT and supported held-out work summed to 253.832 TT under this basis.
Neither number is a universal rate or allowance. The two-hour cohort has only 14 basis intervals
and remains unavailable. These results do not prove a scalar works across plans, models or windows.

The comparison has an explicit tradeoff: TT weights use an earlier 20-interval reference, while
the local competitors use the subsequent 20 calibration intervals. It tests whether a frozen
older shape plus a new scale is useful, not equal training-information budgets or a universal win.
Cross-regime tests remain separate; no strict future-work forecast or promotion follows.

The prototype produces reproducible reconstructed results, not durable as-original TT history.
Revisiting a dataset can create a different basis; it must be described as restatement, never
as silent mutation of an earlier unit. No native acquisition, auth, retention or canonical
accounting changed. Tests cover reference anchoring, additive scoring, immutable basis data,
content identity, unsupported coverage, separate calibration and future-label noninterference.

Validation: 471 Core tests passed. The initial app build caught a missing formatting namespace;
after correcting it, Windows Debug build passed with zero warnings/errors and deployed/restarted
`20260919T003901513Z-647d344f`. CLI replay used owned retained evidence only. UI visual acceptance
and cross-regime/as-original TT-history validation remain unclaimed.

## 27. Requirement audit and TT negative controls (2026-09-19)

This audit distinguishes delivered implementation from unavailable evidence and remaining
product work. It does not mark the overall implementation goal complete or make every original
hypothetical test a demonstrated capability. PROJECT.md remains the current-work authority.

| Original investigation requirement | Evidence / implementation now present | Proof boundary |
|---|---|---|
| Provider credits versus daily allowance | Sections 1, 9–10, 16, 20; `CodexDailyPairing`; stored personal count/relative reports | Zero credit quantities on the observed account cannot validate a ratio. Constructed ratio fixtures test arithmetic only. |
| Exact report contracts | Section 2 source-pinned table, SDK review in section 8, live probe outcomes in sections 9–10 | Availability, units and meaning are separate. Unsupported plan/task/workspace reports are not implemented successful acquisition. |
| Native scale versus TT | Pairing negatives, token/category/API-price evaluation, section 26 TT prototype | No universal native scale or TT conversion established. TT is a local, basis-labelled experiment. |
| Semantic drift | `CodexEvidenceDrift`, immutable server ledger and negative/revision tests | Derived versioned change records preserve input references; they do not prove provider-policy causality. |
| Source/account/cycle compatibility | Server bracket/account-switch, account-scope, daily-pairing and quota-history tests | Correlation remains correlation; no historical ownership is invented. Unknown seats are not presumed compatible. |
| Copy/interleave evidence | Competing reducers, ordered-sequence tests, real-corpus audit and Desktop ownership fix | Physical observations remain distinct. Neither containment nor lineage inference earned canonical promotion. |
| Acquisition boundary and SDK | Installed RPC/source findings, SDK comparison, explicit opt-in backend adapter | Direct HTTP is never an automatic app-server fallback. No credential refresh writer, account switching or synthetic agent traffic added. |

The proposed P0 checks are covered by the named parser/pairing/provider/drift/reconciliation/
persistence suites for the implemented sources. They are **not** blanket proof of future endpoint
contracts: populated plan/task reports, signed credit-event adjustments, shared-pool coverage and
historical denominator rebasing remain source-dependent work before those sources can be used.
In particular, section 10 corroborated an empty HTTP 200 response for `daily-code-review-metrics`;
that confirms the observed route response, not populated-row semantics or a useful retained metric.

The TT negative-control fixture now models a relative category-price change: two categories have
equal cost during basis fitting, then one costs four times the other during calibration and
validation. The frozen basis correctly assigns equal TT to equal-score work, while a scalar
conversion loses to the full-vector challenger. This verifies that the evaluator can reject the
information bottleneck rather than only succeeding on proportional synthetic examples.

A second fixture establishes that unsupported calibration effort does not become a zero-cost
conversion. Supported workload can still have a TT score without a quota conversion. Missing
model support during basis fitting prevents creation of a usable basis altogether.

Remaining product boundaries are explicit: no durable as-original TT scoring history, no
validated cross-regime scalar conversion, no live session quota uncertainty with adequate
independent cycles, and no endpoint/continuous-activity model. The latter is a different target
from any recorded work, not a defect to hide by renaming the existing probability. New source
retention and unavailable backend capabilities require their own evidence and contracts.

Validation: 473 Core tests passed; isolated Windows Debug build passed with zero warnings/errors
(`DogfoodEnabled=false`). This test/documentation-only slice did not deploy or restart the app.

## 28. Reuse a frozen TT basis across compatible contexts (2026-09-19)

`tt-lab/v2` adds explicit source/destination comparisons, without a second scoring implementation.
The source's first 20 observations create the same content-addressed basis as its local experiment.
The destination's first 20 observations fit its independent quota/TT scale and local competitors;
later supported observations are held out. The basis is not refitted to destination labels.
Rows expose source context, destination context, basis end time and calibration end time.

Compatibility requires the same recorded account/provider/profile/source/session lineage and
horizon, different cohort labels, and the entire observed source cohort ending before the
destination begins. Overlapping/interleaved contexts are not treated as successive regimes.
These are reported evidence contexts, not a causal assertion about provider-policy changes.
Model/effort/category support checks still apply; no new source or account ownership is inferred.

The retained 30-day replay currently has **zero transfer pairs**. Model Lab and CLI state this
explicitly rather than treating absence as successful validation. The local weekly/half-hour
basis ID and calibration remain unchanged with a newly matured 28th held-out interval; local
MAE is now 0.484pp versus 0.495pp full-vector and 0.795pp raw tokens, still only one reset.
Those local results are not transfer evidence.

A synthetic plan-context fixture verifies exact basis identity reuse, separate destination
calibration and chronological boundaries. Account, source, profile, session, horizon mismatch
and overlapping eras produce no transfer comparisons. Existing relative-cost negative controls
continue to establish that scalar TT can lose to the richer workload model. Durable as-original
scoring and empirical cross-regime validation remain separate, unfinished boundaries.

Validation: 474 Core tests passed; Windows Debug build passed with zero warnings/errors and
deployed/restarted `20260919T004833138Z-90a9131e`. No live transfer model was promoted.

## 29. Preserve original TT research output (2026-09-19)

Intelligence component schema 6 adds `tt_evaluation_snapshots` to the existing owned database.
It stores selected typed TT reports: exact scoring basis, support/reference, scalar calibration,
cohort labels, error/coverage summaries, requested range, dataset cutoff and actual save time.
There is no native source-content or credential collection. Explicit Model Lab evaluations save
one original research result; `--save-tt` is the equivalent opt-in CLI operation. Plain `--tt`
still only reconstructs. The app's saved-results expander and `--tt-history` read old results
without rerunning the model or rewriting their values.

An immutable run ID plus SHA-256 payload checksum separates retries from conflicting writes.
Exact retries are idempotent; changed content with the same ID is rejected. The reader verifies
payload checksum, envelope metadata, basis content ID and input-semantics version. Corrupt,
oversized or unsupported rows remain visible as unavailable and are never replaced with a fresh
reconstruction. Payloads are bounded to 512 KiB/512 score rows; query batches to 20 snapshots.
No automatic deletion is introduced; existing whole-database backups retain the new table.
These are original **aggregate research outputs**, not originally scored per-task TT history.

Migration tests exposed older fixtures that deliberately rewind the intelligence version while
retaining newer tables. The additive migration is idempotent and preserves existing data; an
incompatible view/table still fails transactionally, and retry after resolving it succeeds.
Persistence tests cover exact round trips, immutable basis identity, conflict rollback, later
restatements, profile scoping, read/write bounds, corruption, future formats and retry recovery.

During this work the user reported consuming a banked reset. Owned app-server history independently
showed weekly usage drop from 100% to 0% at `2026-09-19T00:50:42Z`; the reset boundary then settled
near `2026-09-26T00:51:34Z`. Later readings showed 1% used. Banked-reset cause is user-reported,
not an inferred backend field. The existing epoch splitter separates downward usage movement;
this is not a cost-policy change or a reason to redefine TT weights.

Validation: 478 Core tests passed; Windows Debug build passed with zero warnings/errors and
deployed/restarted `20260919T010110025Z-dcc98ab4`. A live owned-database save/read round trip
preserved snapshot `0b36f741f5ef44809952b7fda61973a7`, its two score rows and exact basis ID;
the history reader reported no problem. Native UI visual acceptance remains unverified.
Incoming ordinary rollouts can supply post-reset evaluation evidence; no synthetic workload
or assumption that a reset changed the accounting policy is required.
