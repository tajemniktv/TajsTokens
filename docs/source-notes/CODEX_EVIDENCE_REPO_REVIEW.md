# Codex evidence acquisition repository review

Consolidated 2026-09-19 from the pinned review and subsequent local experiments.
[PROJECT.md](../../PROJECT.md#current-work) owns product scope; this document owns source findings
and proof boundaries. The [layered statistical design](TT_LAYERED_STATISTICAL_DESIGN.md) owns
modeling rationale, including the superseded local roadmap.

## Findings at a glance

- Personal daily counts and relative reports are available through the implemented explicit
  opt-in backend adapter. Sampled native daily credits were zero despite nonzero tokens:
  no usable credit-to-allowance scale has been established on this account.
- App-server remains the normal account/quota/activity boundary. The inspected SDKs add
  transport convenience, not private billing capabilities. Direct HTTP stays separate,
  never an automatic fallback; credentials remain read-only and are not retained.
- TT is now a frozen, basis-labelled **local research index**, separately calibrated to quota.
  It is not a credit wallet or proven universal scalar. Full-vector models remain competitors.
- Rich quota metadata, nullable requested service tier, semantic-change diagnostics, even-burn
  pace and reconciliation experiments exist. No alternative counter earned canonical promotion.
- A 200/empty response corroborated the tested code-review route, not populated metrics.
  Plan/task/workspace failures are dated capability outcomes, not zero usage or permanent absence.

The [archived investigation record](../archive/CODEX_EVIDENCE_REPO_REVIEW_2026-09-19.md)
preserves all original 31 sections, source details, probe outcomes, tests and dated deployments.
It is historical evidence, not a second roadmap. This consolidation did not refresh upstream
repositories, probe endpoints or rerun models. Version-sensitive claims remain limited to the
recorded pins and installed snapshots.

## Review scope and source anchors

Seven repositories received the pinned comparative review; four additional projects received
focused checks, not equivalent whole-repository audits. No third-party code was copied or run.
The initial static review made no authenticated calls; later authorized probes and collection
are recorded separately. Synthetic tests demonstrate behavior, not independent provider semantics.

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
Read-only Codex source at review time: clean checkout at
[`7498521d288b9b3b96ffba4eedf089d8d6e06a84`](https://github.com/openai/codex/tree/7498521d288b9b3b96ffba4eedf089d8d6e06a84).
CLI checked during the original review: `0.156.0-alpha.2`; no exact source/build equivalence asserted.
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
The archived section 10 subsequently corroborated a 200/empty response; populated metric semantics remain unverified.

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
The implemented opt-in daily adapter supersedes only the daily-report part of that gap.

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

The research contenders now provide **versioned reconciliation projections**, retaining source occurrences and
selection reasons. Establish owner/lineage/overlap first; do not delete duplicate candidates,
dedup by final totals, silently take max per session, or treat a containment estimate as exact.
Relevant inspected tests include CodexBar `CostUsageScannerBreakdownTests.swift` interleave
cases (including >64 observations and checkpoint restart), `CodexSubagentAccountingIntegrationTests.swift`
and `CodexCompactSubagentAccountingTests.swift`. These are test-design references, not copied code.

The aggregate-only `reconciliation-audit/v6` CLI partitions transitions by pre-step scalar
watermark relationship and complete/nonnegative last-snapshot presence. Each bucket reports
observations, disagreements and all three contender deltas; sums recover the physical-file totals.
“Usable last” here means snapshot fields are present/nonnegative, not that categories sum correctly,
the last total fits the cumulative total, or native lineage is established. Failed/changed files
contribute no bucket totals. No identifiers, hashes, paths or payload examples are exported.

The 2026-09-19 read-only scan examined 367 files: 366 stable, one changed and excluded, no failures.
Among 66,476 observations, 2,272 transitions disagreed: 2,254 below the previous watermark,
15 above it and three first-cumulative observations. All had complete/nonnegative last snapshots.
There were 29 disagreeing files and no cross-file sequence-match candidates in this sample.
This prioritizes investigation of below-watermark histories with last counters; it does not prove
interleaving, reset lineage, corpus-wide absence of copies or a winner. Canonical accounting is unchanged.

The follow-up v5 scan on the same date included all 367 stable files and 67,182 observations.
It split the 2,291 below-watermark observations into **30 falling, 2,224 recovering and 37 repeated**
totals relative to the previous complete snapshot. All falling/recovering rows disagreed; repeated
rows did not. Thus the dominant count is recovery below an old maximum, not thousands of fresh
regressions. The scalar high-watermark contender assigns zero to those recovering increments;
that is an assumption, not evidence they are duplicates. The current next investigation is the
30 actual falls and their surrounding native identity/lifecycle evidence, not promotion of containment.
A separate read-only exploratory scan found only one fall immediately preceded by a compaction
record since the prior token observation; this adjacency does not establish causality or exclude
unrecorded lifecycle changes. Current upstream `7498521d288b9b3b96ffba4eedf089d8d6e06a84`
(`core/src/session/mod.rs`, resume/fork initialization) seeds token info from prior rollout history,
so a generic claim that every resume necessarily resets counters is not supported. This is current
upstream corroboration, not a version-matched explanation of historical Desktop rows.

#### Actual falls and newly observed response identity (2026-09-19)

A follow-up stable-file scan found 30 falls: 26 new totals equal the last reported increment,
one cumulative zero and three other positive regressions. All 30 had a `task_started` record
since the preceding token observation; 22 also had settings applied, 23 completion, two abort,
and one compaction. These overlapping counts describe adjacency, not causes. The legacy
`token_count` payload/info had no response/thread/session/turn identity fields at those falls.
This does not justify replacing the incumbent with whole-file containment.

The same stable scan found **6,024 top-level `token_usage_record` records**. Current upstream
`7498521d288b9b3b96ffba4eedf089d8d6e06a84` defines `TokenUsageRecord` in
`codex-rs/protocol/src/protocol.rs` as best-effort completed-response usage with `thread_id`,
`turn_id`, `session_id`, `root_turn_id`, `response_id`, `usage`, `turn_token_usage` and
`thread_token_usage`. `core/src/session/mod.rs::record_observed_response_completed` persists
it only when usage is present. `core/src/state/session.rs::record_token_usage` accumulates turn
and thread vectors separately. This is an observed local source with corroborating current
upstream, not proof every historical Desktop version implements the same semantics.

A subsequent exploratory scan while collection continued saw 6,026 records across 38 files,
all with those five identity fields; 5,992 single-record/next-token pairs had exact JSON usage/
last-vector equality, leaving 34 non-exact pairs. Eleven of the 30 falls had a response record
between legacy token observations, 19 did not. No repeated thread/response key within a file
was observed. These are exploratory coverage counts, not a stable snapshot, completeness claim,
cross-file identity proof or verified explanation of the 34 differences.

**Implemented read-only comparison:** the existing corpus audit now counts per-file response-key
repeats/conflicts (usage and turn/session/root lineage), missing identity, owner conflicts and invalid
vectors. Exactly one eligible pending response is compared with the next legacy last vector; multiple
candidates stay ambiguous. Task start/completion/abort, settings, metadata and compaction break pairing.
An absent cache-write field defaults to zero as upstream specifies; null/missing required counters
remain invalid. Categories are compared, not repaired or certified internally consistent. File-level
failure/change excludes its entire contribution. End-of-file pending records remain unpaired.
Tests cover privacy, copies, failed-file exclusion, boundaries, ambiguity, repeated/conflicting identity
and optional-counter semantics. Native IDs never enter reports and the streams are never summed.
This is candidate adjacency, not established native request-to-token correspondence or cross-file
deduplication. The subsequent [selected response-evidence contract](../../PROJECT.md#selected-response-evidence-contract)
defines local identifier/vector retention, occurrence identity, generation handling and required
replay/read-model tests. This is the chosen next implementation, not shipped acquisition or
canonical selection. Keep existing accounting unchanged.

The first boundary-aware v6 scan (2026-09-19) included all 367 stable files: 6,043 response records,
6,007 exact eligible vector pairs, 35 records unpaired at lifecycle boundaries and one at EOF.
No different eligible vector pair, invalid vector, missing/conflicting identity or repeated per-file
response key was observed. There were 67,366 legacy token records, including context-only rows,
of which 61,359 had no pending response. These are not additive workloads or a coverage percentage
for all historical requests. The earlier 34 naive mismatches were removed by boundary-aware pairing;
they are not demonstrated same-response inconsistencies. Cross-file identity, collection-time
provenance, version scope and canonical selection remain separate gates. Implement the selected
typed supplemental contract and its replay tests rather than changing counter heuristics.

## 5. Compatibility and evidence-model contracts

These contracts guide existing typed evidence owners. Daily reports, profile/thread projections
and quota metadata are implemented; unavailable plan/task/workspace sources remain proposals,
not successful acquisition. This documentation consolidation adds no collection.

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
alongside windows/plan. TajsTokens now preserves these through typed server evidence and
`CodexQuotaMetadataParser`, exposed in Diagnostics without new quota HTTP acquisition. Preserve absence and exact provider semantics; do not interpret
`credits.unlimited` as unlimited subscription allowance or assume a missing bucket is revoked.

## 6. Validation requirements and proof limits

This original matrix is a contract checklist, not blanket proof of implementation. Existing
parser/pairing/provider/drift/reconciliation/persistence tests cover acquired surfaces. Populated
plan/task reports and credit rebasing require actual source evidence before integration claims.

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
| Explicit opt-in adapter | Fixed HTTPS host/route/method allowlist; redirect denial; no credential logging/persistence/export; bounded payload/time/retries; cancellation; identity switch during request; expired token with Codex-owned refresh, no parallel refresh writer; no auto-fallback from production RPC to direct HTTP. |

Do not add tests that merely mirror these proposed record types. First create meaningful
contract/reducer cases under existing owners; acquire sanitized independent evidence only
through an explicitly selected boundary. Fixtures derived from a ratio cannot prove that ratio.

## 7. Acquisition boundary

At the recorded CLI/schema snapshot, available reads are `account/read`,
`account/rateLimits/read`, and `account/usage/read` (optionally `threadId`).
These expose current account/quota and projected activity or thread estimates, not full backend
analytics. Experimental schema generation did not add daily analytics, plan history,
full-profile extras or task-v2 RPCs.

The separate `CodexBackendDailyEvidenceProvider` uses explicit opt-in, fixed HTTPS routes,
bounded responses, redirect rejection and account-switch checks. It reads the selected login
in memory, without refreshing or writing credentials. One-shot CLI collection does not enable
background polling. Normal quota acquisition remains app-server-owned.

A future Codex-owned report seam remains preferable; `account/analytics/read` is a proposal,
not an existing method. Legacy `getAuthStatus` appeared in inspected native logs, which does
not prove successful token export or create an analytics RPC. SDK wrappers cannot change that.

Commands and mutation distinctions live in [DEVELOPMENT.md](../DEVELOPMENT.md#provider-native-evidence-diagnostics).
See [Desktop analytics](CODEX_DESKTOP_ANALYTICS.md) for the inspected private transport and
[the SQLite reference](CODEX_SQLITE_SCHEMA.md) for native log findings. Readable endpoint names
do not justify retaining raw logs or content.

## 8. Codex SDK follow-up — 2026-09-18

**The SDKs do not expose additional billing evidence. Python can simplify app-server
transport in a Python tool; TypeScript is primarily an agent-execution wrapper. Neither
unlocks the private analytics reports listed above.**

At the recorded review date, checked the [official SDK documentation](https://learn.chatgpt.com/docs/codex-sdk),
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

## Current implementation

| Area | Existing owner / result | Limit |
|---|---|---|
| Quota metadata and requested tier | `CodexQuotaMetadataParser`, rollout parser, observatory replay and Diagnostics | Requested tier is not execution/billing tier; missing stays unknown |
| Daily reports and pairing | Immutable server ledger, `CodexBackendDailyEvidenceProvider`, `CodexDailyPairing` | Snapshots are not additive spend; latest failures remain visible; zero credits cannot validate a ratio |
| Semantic changes | `CodexEvidenceDrift`, input references and first-observed changes | Ordinary resets/consumption/balance changes are not policy drift |
| Copies/interleaves | `CodexReconciliationAudit`, `CodexSequenceComparison`, containment/lineage contenders | Copy/prefix/restart tests and real-corpus disagreement do not prove native lineage; canonical accounting unchanged |
| Desktop filename ownership | Parser/state-index replay recognizes thread UUID before suffix UUID | Ownership fix, not cross-file deduplication; no silent alternate-path import |
| Pace/price baselines | `QuotaEvenBurn`, versioned `ApiPriceWorkload`, cost evaluation | Pace is explanation; API prices are counterfactual, not native credits |
| Cost/drift diagnostics | `QuotaCostObservationBuilder`, `QuotaCostEvaluation`, frozen-reference residual checks | Completed-work explanation is not forecasting; residual shifts are associations |
| Evaluation coverage | `QuotaEvaluationCoverageBuilder`, detailed observation construction, Model Lab and cost CLI; composition/time/tier counts, cohort flags and construction reasons with strict replay context | Stage-specific candidate counts, not whole-account attribution, all raw-reading rejection counts or canonical duplicate coverage |
| Workload/composed quota | Activity-aware 5/15-minute nowcasts, 30/60-minute any-work/conditional scenarios, composed/session quota evaluators | Local activity is not human presence or whole-account coverage; promotion and ranges remain evidence-gated |
| TT research | `tt-lab/v3`, immutable basis, separate calibration, scalar/full-vector comparisons, signed/cumulative error | Local pooled support only; empirical cross-context transfer unvalidated |
| Original research reports | `tt_evaluation_snapshots`, intelligence schema 6, Model Lab and CLI history | Immutable aggregate output, not per-task history; absent older diagnostics are not recomputed |

The archived record preserves the 479-test/build milestone and live snapshot round trip.
These are dated checks, not tests rerun during this consolidation or universal visual acceptance.

## Remaining evidence gates

Local mismatch investigation (2026-09-19) traced all five flagged retained token rows to native
`last_token_usage` snapshots with positive totals and zero category counters. This is not an
import arithmetic error, and cumulative movement is not a justified replacement for the last-turn
amount. Stored totals/categories remain unchanged. Category-dependent cost, transfer and composition
models now withhold incomplete evidence; per-record flags survive aggregation even if opposing
mismatches cancel. Raw-token cost baselines remain eligible. This establishes missing composition,
not its provider-side cause or a general solution to interleaved lineage.

| Question | Recorded evidence | What can resolve it |
|---|---|---|
| Native credit scale | 27 count rows with zero credits and 31 relative rows in the collected sample | Compatible nonzero native pairs with established units, seat, dates and independent freshness |
| Lag/date boundaries | Client inclusive range conflicts with userscript end-plus-one behavior | Completed-day observations and revisions; no silent cross-route alignment |
| Plan/task/workspace reports | Plan history 404, sampled v1 403 and v2 404; further account-scoped failures in archived survey | Real capability change or supported seam, then populated contract tests |
| Code-review metrics | Tested route returned 200/empty | Independent populated-row corroboration before retention or interpretation |
| TT transfer | Prototype/negative controls exist; evaluated sample had no compatible chronological pairs | Later compatible contexts with disjoint calibration and held-out evidence |
| Joint session quota ranges | Insufficient independent completed cycles | Ordinary history; more rows in one cycle do not substitute |
| Canonical reconciliation | Algorithms disagree on physical-file totals | Validated identity/lineage evidence, not merely a smaller total |
| Service-tier interpretation | Nullable requested settings retained and inspectable | Coverage and execution/billing semantics before using them as provider speed or price multipliers |

The user intends ordinary post-reset rollouts to supply evaluation data. The banked reset was
user-reported and corroborated by a downward meter transition, not proof of changed cost policy.
No synthetic workload, repeated denied-route probes or automatic promotion is justified.
Per-task TT history, continuous-presence prediction and extra native-log extraction remain
separate proposals, not capabilities of aggregate reports or the any-work target.

Keep implementation priorities in PROJECT.md. Update this review when a contract, conclusion
or proof boundary changes; retain lengthy dated experiments in supporting records rather than
restoring a chronological roadmap here.
