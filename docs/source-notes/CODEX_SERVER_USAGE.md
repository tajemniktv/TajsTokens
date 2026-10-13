# Codex server usage evidence — 2026-09-18

## Versions and observation boundary

**Scope update, 2026-09-19:** the acquisition boundary and observed results below describe the
original app-server collector and its investigation. The user subsequently authorized a separate,
opt-in direct-backend experiment and daily-report adapter; see the
[consolidated review](CODEX_EVIDENCE_REPO_REVIEW.md#current-implementation).
Statements below excluding direct HTTP are not a current prohibition on that separate adapter.
Live logs additionally corroborate runtime `getAuthStatus` requests and quota/token notification
names, but do not establish successful token export or expose quantitative usage in those
name-only notification rows; see [archived section 13](../archive/CODEX_EVIDENCE_REPO_REVIEW_2026-09-19.md#13-native-log-evidence--2026-09-19).

The clean reference checkout at `E:\dev\codex` was fast-forwarded from
`a51608398d53b6d23ed98b8287de415b35f1eea5` to current `origin/main`
`7498521d288b9b3b96ffba4eedf089d8d6e06a84` (2026-09-18).
The installed command selected by the existing TajsTokens resolver
initially reported `codex-cli 0.154.0`; initialize also reported `0.154.0`. Upstream HEAD is corroboration,
**not an exact-build match**. Installed experimental JSON schema was generated locally
under `.codex/temp/installed-app-server-schema`; it includes `account/usage/read`
and no plan-history or grouped-analytics app-server method.

During the task the installed CLI changed independently to `0.156.0-alpha.2` (TajsTokens
did not update it). Its newly generated schema under `.codex/temp/installed-app-server-schema-0156`
still has no plan-history/grouped-analytics method. The matching upstream release tag resolves
to `235e3ec827d58949d30bb95ba8f47d3560ab305e`; its app-server request list corroborates this.
Durable observations retain the per-fetch client version, rather than relabelling older probes.

TajsTokens launches `codex app-server --listen stdio://`, initializes, reads only
`account/rateLimits/read` and `account/usage/read`, then terminates its own child.
No model turn, thread resume, browser operation, auth-file read, bearer-token export,
direct HTTP request or configuration change is involved. Codex owns authentication.
The user explicitly selected this boundary over implementing a separate backend auth adapter.

## Exact available app-server contracts

Sources at the pinned upstream commit:

- `codex-rs/app-server-protocol/src/protocol/v2/account.rs`
- `codex-rs/app-server-protocol/src/protocol/v2/thread_usage.rs`
- `codex-rs/app-server/src/request_processors/account_processor.rs`
- `codex-rs/backend-client/src/client.rs` and `client/thread_usage.rs`

`account/usage/read` without params returns `summary` with nullable integer
`lifetimeTokens`, `peakDailyTokens`, `longestRunningTurnSec`, `currentStreakDays`,
`longestStreakDays`, and nullable `dailyUsageBuckets[{startDate,tokens}]`.
The implementation reads the backend token profile (`profiles/me`); it is not a sum
of local rollouts. Date strings are retained verbatim after validating `yyyy-MM-dd`.
There is **no backend as-of timestamp, complete-coverage flag or bucket timezone** in
this response. Last returned date is not invented into a completeness watermark.

With `{ "threadId": "<native UUID>" }`, the same method returns nullable `threadUsage`:
`threadId`, integer `estimatedUsageCreditsMicros`, nullable `estimatedUsageUsdMicros`,
and `groups[]` containing nullable `model`, `reasoningEffort`, `speed`, integer
`estimatedUsageCreditsMicros`, and nullable `netNewInputTokens`, `cachedInputTokens`,
`inputTokens`, `outputTokens`, `totalTokens`. Upstream returns null summary fields and
no daily buckets in this branch; **never use summary as a fallback for a thread**.
The underlying batch route is `usage/thread_usage/query`; TajsTokens does not call it.
Upstream converts thread-route 403/404 to `threadUsage: null`. This means unavailable,
not zero credits/tokens and not proof that the local thread was never billed.

Neither usage response carries native `accountId`. Before/after rate-limit reads
provide versioned account pseudonyms and fetch times. Stable matching brackets yield
**server-correlated**, not provider-verified, usage account scope. Changed brackets
yield conflicting/unattributed evidence. Failed/missing brackets retain the report
without an account binding. An undetected switch away and back is not ruled out.

## Real backend contracts not available through this seam

Desktop also contains these analytics, through its own authenticated HTTP host service rather
than these public app-server methods. Exact installed-package tracing and the distinct v2
top-chat contract are recorded in [Desktop analytics acquisition](CODEX_DESKTOP_ANALYTICS.md).
"TUI-only" describes where the initial public-source client was found, not exclusive product availability.

`backend-client/src/client/plan_history.rs` exposes
`usage/plan_limit_history?days=7` through Codex's backend client. Its report includes
`data_as_of`, `coverage_start`, `coverage_complete`, `approximate` (defaults true),
`boundary_tolerance_seconds`, and periods with `id`, `window_minutes`, `plan_type`,
`starts_at`, `ends_at`, `accounting_complete`, nullable `used_basis_points`, and optional
breakdowns (`thread_source`, `turn_trigger`, `model`, `surface`; unknown dimensions
do not discard recognized ones). Basis points are hundredths of a percent of that
period's historical allowance. Null usage is unknown. 404 is an unavailable report.
TUI `analytics/client.rs` uses Codex's `AnalyticsSession`, checks account identity,
and restricts this view to consumer accounts. `analytics_plan_history` is experimental,
default off (`features/src/lib.rs`).

`backend-client/src/client/analytics.rs` has plan-specific routes including
`usage/daily-token-usage-breakdown`, `usage/daily-workspace-user-token-usage-breakdown`,
`usage/credit-usage-events`, and workspace credit reports. TUI `analytics/client.rs`
selects routes/groupings by account type and falls back when attribution is incomplete.
Surface/feature/model/turn-trigger/speed/reasoning/token-type display choices do not imply
every plan has all of those backend labels.

These sources are **NoSupportedSeam**, not “backend returned 404”, “feature disabled on
this account”, or zero usage. No request was made. Their completeness, approximation,
coverage, historical allowance and breakdown values therefore remain unavailable.
Future support must preserve those fields and actual fetch times, not import periods
as exact current quota anchors. Adding a new authenticated backend client is out of
scope under the user's selected boundary.

## Collection, persistence and reconciliation

- Account report plus at most six recent native thread IDs per batch; oldest attempted
  threads rotate first within the latest 30 days. One in-process gate, 30-minute automatic
  backoff including failures, explicit diagnostic retry. At most 100 seconds per batch,
  15 seconds per request, 2 MiB per response and 1,000 intervening protocol messages.
- Allowlist parsers accept at most 4,000 daily buckets and 512 thread groups. Missing,
  empty, unsupported, authentication-required, invalid, conflicting and failed reads
  remain distinct. Negative/malformed counters and duplicate daily dates fail closed.
- Owned schema 14 retains `codex_server_evidence`, with immutable observation identity,
  surface/thread scope, fetch start/end, contract/client version, account correlation and
  typed content-free JSON. Raw response bodies, stderr, errors, credentials, prompts,
  titles and email addresses are never persisted. Same-ID exact retries are idempotent;
  changed same-ID content rolls back the whole batch. Existing native tables are unchanged.
- Account daily values are stored once per `(start_date, tokens)` in `codex_account_day_values`.
  Content-addressed bucket sets retain ordered membership via `codex_account_bucket_members`;
  each small fetch references a set. Identical polls add no day values or memberships. A revised
  day adds a value and a new membership snapshot; removals, ordering, null and empty remain
  distinguishable. Sharing is physical only: account association and fetch provenance remain on
  each observation, never inferred from a shared value/set. Schema 13 fetches are converted in
  one rollback-safe transaction without deleting historical observations or changing timestamps.
  Storage still grows with fetches and distinct revisions; this is not age-based retention or a
  fixed database-size cap. Unavailable thread polling retains the existing rotation/cadence.
- Reports are point-in-time snapshots, not additive events. Prior snapshots survive nulls,
  failures and revisions. No retention/delete/export policy was introduced.
- Comparisons read one SQLite snapshot, bounded to 2,000 retained fetch results / 16 MiB
  of JSON characters, 60 threads and 30 daily labels per account. Daily comparisons use
  UTC local-event dates as an explicitly unverified alignment. Lifetime and fetch-to-fetch
  differences have different coverage and accounting-lag limitations.
- Latest attempt is keyed by surface/thread, not account correlation. Losing correlation or
  switching accounts cannot leave an older successful thread estimate current. Account-specific
  historical comparisons remain dated history. Provider outcome and account conflict are separate:
  a bracket conflict preserves Available/Unavailable/Error and appends attribution diagnostics.
- The transport scans reusable 4 Ki-character chunks, retaining subsequent lines. Unknown
  server-to-client requests receive bounded `-32601` replies; notifications and reply IDs remain
  separate. Existing time/message bounds still apply; no server capability is enabled.
- Thread joins use exact IDs, then model/effort. Speed has no aligned local field; repeated
  model/effort comparisons across speed groups cannot be summed. Nullable counters remain
  nullable, and each side's categories remain separately named.
- Matching account numbers do not manufacture ownership. Existing source/session/time-bounded
  **user-declared single-account** associations remain separate from native AccountKey.
  They are revocable in Settings and may inform existing research/calibration under the
  already stronger collection-time promotion policy. No new server evidence enters live
  forecast selection.

## Observed local results

Review follow-up on 2026-09-18: schema 14 migration preserved all 18 existing observations,
verified by hashes of rehydrated content. After the next nine-result live collection, the store
contained 27 fetches but only 159 day values, one bucket set and 159 membership references.
Account-fetch JSON averaged 1,052 characters versus 8,157 previously (about 87% smaller);
this is a per-fetch JSON measurement, not a claim that total database file size shrank.
The read-only CLI rehydrated all 159 buckets; an unknown CLI option was rejected rather than
collecting. All 382 Core tests and the Windows build passed; the daily app was restarted.

Read-only probes on 2026-09-18 returned account activity with **159 daily buckets**, lifetime
**10,935,115,385** tokens, peak day **811,292,125**, longest turn **9,468 seconds**, and
current/longest streak **6/19 days**. Six requested recent threads each returned unavailable
estimates. These outcomes do not establish availability for every thread or plan.
The deployed automatic collection and an explicit persisted collection subsequently tested
12 distinct recent threads, all unavailable, on `0.156.0-alpha.2`; both batches persisted.

The first probe compared backend lifetime with **9,179,894,606** retained local tokens
(ratio **0.839**). Local totals continue changing; neither this ratio nor daily ratios are
validated account-coverage fractions. On 2026-09-09, local history contained 306 events in
one session across two physical sources: 153 matching session/time/model/effort/counter
fingerprints, each source totaling **21,250,438** tokens. The backend day total was also
**21,250,438**. This is a concrete duplicate-source discrepancy, not permission to delete
or silently normalize retained evidence. Diagnostics expose repeated-fingerprint candidates.
Across the retained native table the comparison found 544 cross-source repeated fingerprints,
representing 71,700,836 extra retained tokens under that candidate fingerprint rule. These are
not automatically merged: identity/overlap reconciliation remains a separate evidence decision.

The user's current-account assertion was explicitly bound to the fresh bracketed account
and saved for **356** retained source/session ranges, preserving all native AccountKeys.
Its assertion time is now, not the historical event time. It cannot retroactively qualify
past origins for collection-time promotion.

Without available thread credits and historical allowance labels, no held-out credit→allowance
mapping, plan/regime stability claim, improved cost weights or TT quantity is supported.

Re-evaluation with the saved assertions found 323 cost observations, including 262 explicitly
user-associated historical observations. The native half-hour cohort had 31 held-out targets
across two reset generations; there were **zero diagnostic material wins**. Strict composed
evaluation had only six native outcomes across one generation (total-token composed interval
loss 0.5821pp versus incumbent 0.2590pp). Five compatible asserted intervals supplemented native
training, but asserted variants had **zero strict held-out outcomes**, because the assertion was
not available at those historical origins. No model was promoted. These are dated research
results over the unchanged existing evaluator, not evidence that server credits improved cost.

Validation: 373 Core tests passed, including parsing/null/capability, account-bracket, additive
migration, idempotence/collision rollback and mismatch-retention regressions. Windows Debug
solution build completed with zero warnings/errors and installed/restarted
`20260918T112251487Z-45eb7aad`. Automatic and explicit app-server collection persisted successfully
in the existing owned database. Diagnostics has build/service-level proof; no new native visual
acceptance pass is claimed for this evidence panel.
