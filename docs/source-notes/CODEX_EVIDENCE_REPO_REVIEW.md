# Codex evidence acquisition repository review — 2026-09-18

## Result and proof boundary

**Prefer provider-native accounting quantities over inventing TT, but do not treat all
credits as one stable unit or daily relative usage as historical quota consumption.**
The reviewed implementations establish useful acquisition and reconciliation techniques,
not a universal credit-to-allowance contract. Keep released acquisition through Codex-owned
app-server. The changes and tests below are proposals, not implemented acquisition.

This review inspected pinned source, existing TajsTokens implementation and source notes,
and generated the installed CLI's protocol schema, including experimental fields. It made
no authenticated backend calls, read no credentials, ran no downloaded project code, and
did not change collection, databases, account associations, forecasts or the running app.
Third-party tests were inspected, not executed. Published runtime anecdotes are attributed
to their authors; synthetic fixtures are not independent runtime corroboration.

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

**`daily-code-review-metrics` remains unverified.** No corroborating implementation was found
in the seven reviewed snapshots or searched Codex backend-client/model sources. This is not
proof of nonexistence. A code-review quota bucket is not corroboration of that analytics route.

## 3. Comparison with TajsTokens

| Project | Useful technique | Difference / do not import |
| --- | --- | --- |
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
Current server evidence does **not** retain the daily credit/relative-usage reports, full-profile
metadata, historical-period allowance labels or v2 task percentages. See
[existing acquisition contract](CODEX_SERVER_USAGE.md).

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
third-party RPC/export seam. Backend availability remains untested in this review.

**Experimental auth adapter possibility, not released integration:** upstream legacy v1
`getAuthStatus` defines `includeToken`/`refreshToken` and an optional `authToken` response.
This was not present in the installed generated public method catalog; availability is not
established here and no token request was made. Even where callable, exporting a token then
making HTTP requests transfers credentials/request ownership to TajsTokens: it is not a
Codex-owned analytics RPC. Internal `chatgptAuthTokens` login is explicitly marked unstable,
internal-only and supplies credentials to Codex, not an analytics read alternative.

The seven projects' auth-file, OAuth/PAT and browser-session approaches belong in that
separate research category. Prefer a Codex-owned allowlisted report/export or app-server
extension using the existing backend client and credential refresh. An experimental adapter
would require an explicit change to the acquisition boundary, not silent fallback or browser
injection. No account-management, reset-redemption or unrelated UI redesign is proposed.

Recommended order:

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
Only this summary and the product-direction note are staged. No production collection/storage changes,
application builds/restarts or broad backend endpoint enumeration were performed.
