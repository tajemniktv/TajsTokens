# Historical PROJECT.md current-work record — 2026-09-19

> Preserved before consolidation. Milestones, measurements and next-step wording below are dated
> historical context, not current instructions. Use [PROJECT.md](../../PROJECT.md#current-work)
> for the active roadmap and source contracts.

## Current work

**Layered statistical direction and TT proposal (2026-09-19):** preserve separate cost,
workload/activity, composition and horizon-specific quota models. The user's proposed TT is a
frozen, interpretable, versioned normalized workload score with a documented reference basket
and content-addressed basis identity—not credits, quota, money or an independently forecast unit.
Prototype it in Model Lab, with explicit unsupported coverage and scalar-versus-full-vector
comparisons. Account/window/regime quota conversion must remain separate and revisable; changing
provider policy must not silently change historical TT or original forecasts. Local bases do not
imply cross-user comparability. This is a research direction, not a released TT metric, fitted
universal weights or a claim that challengers beat the incumbent. Existing source/retention and
promotion gates remain in force. The rationale, formulas, caveats and proposed candidates are
preserved in [Layered statistical design](../source-notes/TT_LAYERED_STATISTICAL_DESIGN.md).
The older local `.codex/roadmap.md` is historical, not an additional delivery plan. Its retained
principles and superseded restrictions are mapped in the
[roadmap consolidation](../source-notes/TT_LAYERED_STATISTICAL_DESIGN.md#7-consolidation-of-the-earlier-quota-cost-roadmap).

**TT Model Lab prototype:** `tt-lab/v3` freezes category weights on the first 20 compatible
known-account intervals, normalizes to one million tokens in the first positive interval's
category mix, then fits a separate quota/TT scale on the next 20 intervals. The immutable
content-addressed basis includes exact weights/reference, category support, observed pooled
model/effort support and input semantics. Missing/mismatched categories or unsupported mix
are withheld, never silently scored as zero. Later outcomes compare scalar TT against locally
fitted raw-token and full-vector cost models on the same supported rows with reset-balanced
envelope loss, displayed-delta MAE and a zero-use reference. Basis and calibration remain separate.
The initial 2026-09-19 30-day weekly/half-hour evaluation supported only Astra/low: 27 held-out
intervals in one reset, MAE 0.487pp scalar versus 0.507pp full-vector and 0.791pp raw tokens.
These are dated results, not live counters; later evaluations are recorded in the evidence review.
This is completed-work cost research, not future-work forecasting or cross-regime validation.
Model Lab/`--tt` expose the
basis, coverage and calibration; no live promotion or per-task original scoring history exists.
Each run is an explicitly reconstructed/restated scoring result. Cross-context research now
reuses an earlier cohort's exact basis, with the destination's first 20 intervals fitting only
its conversion and local competitors. Pairing requires the same recorded account, provider,
profile, source, session lineage and horizon, with the source cohort ending before the destination
begins. Basis/calibration end times and both contexts remain visible. There are currently no
compatible chronological pairs in the retained 30-day sample; no empirical transfer claim follows.
**TT research-output retention:** explicit Model Lab evaluation now saves its original typed TT
report, exact basis, coefficients, reference/support, calibration, errors, coverage, requested
range, dataset cutoff and actual save time in owned SQLite intelligence schema 6. Each run has
an immutable ID and payload hash; retries are idempotent, conflicting content is rejected and
later reconstructions create new rows rather than replacing originals. Payloads are bounded
to 512 KiB/512 score rows; reads return at most 20 snapshots. No automatic deletion or background
collection is added; snapshots share existing whole-database backup/recovery. The UI loads ten
saved results without rescoring and exposes corrupt/unsupported rows rather than silently using
older success. Stored fields are selected aggregate research evidence and existing pseudonymous
cohort labels, not native prompts, titles, auth or raw payloads. This expands owned derived-result
retention only; there is still no per-task original TT scoring history or live forecast promotion.
Existing forecast snapshots are untouched. TT results remain explicitly experimental.
TT evaluation also reports reset-balanced signed bias (prediction minus reported cost) and
cumulative error over eligible held-out intervals with summed meter-envelope bounds. These
bounds are not confidence intervals or complete account totals. Saved older reports retain
missing diagnostics as unavailable; they are not silently recomputed.

**Evidence-review continuation gate (2026-09-19):** the implemented acquisition, reconciliation
experiments, semantic-change diagnostics and layered Model Lab candidates are mapped in the
[review evidence gates](../source-notes/CODEX_EVIDENCE_REPO_REVIEW.md#remaining-evidence-gates).
The next empirical step is evaluation of naturally arriving post-reset observations with the
same frozen basis and compatible account/source semantics. Do not generate synthetic Codex
work or lower independent-cycle requirements to manufacture validation. Nonzero native-credit
calibration, populated plan/task semantics and empirical cross-context transfer remain unproven.
Per-task TT persistence and endpoint/continuous-activity models are distinct future product
extensions, not requirements to replace already working aggregate research or any-work targets.
The review goal is not declared fully validated while those evidence-dependent claims are open.

**Forecast horizon separation (2026-09-19; partially implemented):**
separate quota cost conditional on workload from whether future activity occurs. Nowcast targets
5/15 minutes (30 minutes only with supporting validation); session outlook describes 30/60 minutes
conditional on continued work; quota outlook uses explicitly conditional pace/history scenarios
through reset; workload planner consumes user-supplied hypothetical work. Existing 30-minute and
two-hour token forecasts are not automatically relabelled as these new products. Short horizons
are hypotheses to evaluate, not inherently predictable. Live recorded-token output now uses
5/15-minute `rollout-token/v2` nowcasts, with the same ten-minute activity eligibility gate in
chronological short-horizon replay. Positive token increments, starts and tool activity qualify;
settings alone do not. Quiet open turns, no recent activity and unknown evidence pause output
with distinct explanations; none proves no session exists. Existing stale-refresh handling remains
separate. Historical 30-minute/two-hour comparisons remain research baselines, not live nowcasts.
Activity likelihood and active-period
workload require separate chronological evaluation before composing expected burn. Do not multiply
interval endpoints and call the result a calibrated uncertainty interval. Until activity probability
is calibrated, show observed activity/idle/unknown rather than a numerical probability. Ten minutes
without activity is an initial configurable-policy hypothesis for pausing workload extrapolation,
not proof that no Codex session exists; lifecycle/turn/subagent evidence and collector freshness
must distinguish quiet work from missing collection. Quota pace remains independently available.
The first recorded-activity/conditional-workload decomposition is now implemented as
`recorded-session-outlook/v2` for 30/60-minute local-token scenarios. The target is **any positive
recorded token usage in the interval**, not human presence, continuous activity or remaining active
at its endpoint. It estimates earlier activity frequency by token-recency group and conditional
mean/10–90% historical positive-work range, with explicit global fallback when groups are sparse.
Live activity probability and unconditional mean are withheld until 64 earlier same-group held-out
outcomes pass an empirical calibration/Brier baseline gate. This is evidence of past reliability,
not a guarantee of future calibration. Conditional ranges are descriptive, not calibrated bands;
live joint quota-burn uncertainty and endpoint/continuous-activity models remain unimplemented.
Model Lab now evaluates the session-to-quota chain retrospectively (`session-quota-evaluation/v2`):
origin-only conditional tokens and activity frequency through the existing frozen, account-cohort
total-token cost model. Thirty-/sixty-minute meter targets use their actual elapsed horizons,
including the existing five-minute polling tolerance; no proportional duration shortcut is used.
Conditional and unconditional errors are separate, with matched pace baselines and joint-error
bands only after eight earlier completed resets. The current 30-day replay has 32/9 outcomes
across 2/1 resets: expected MAE 1.85/4.32pp versus pace 2.58/4.09pp. Against the existing
quota policy on identical origin/outcome pairs, 30 minutes has 32 pairs (1.85 versus 2.06pp MAE);
60 minutes has only four pairs (3.11 versus 3.09pp). Unmatched outcomes do not enter that comparison.
No bands qualify.
This is a research comparison, not a promoted quota forecast: longer-horizon performance is mixed,
local work does not establish complete account attribution, and strict deployment evidence remains
required. It adds no native fields, retention or durable schema.
Completed quiet targets are included through the earlier of evaluation time and dataset capture,
not censored at the final token event. Snapshot age alone cannot supply additional negative labels;
collection gaps can still resemble inactivity. Nowcasts predict interval tokens after recent
activity, not continuous-session workload.

This slice reuses only the already-retained token `ObservedAtUtc`, `CapturedAtUtc` and
`ReportedTotalTokens` fields. It introduces no native metadata collection, new retention, source
schema or raw-content copying. Outputs are rebuildable in-memory forecast/evaluation projections.
Any later use of additional tool-call/completion or client/generation fields needs an explicit
per-field source/content/privacy/retention decision here before implementing collection.

**Native daily pairing (2026-09-19):** Analytics and `--daily-pairing <telemetry.db>` now evaluate
latest stored daily snapshots, never add polls or replace the latest failure with an old success.
Same account/bracket, source versions, plan/current policy context, requested range, units, grain
and freshness are required. Missing/zero credits, balance spill, tiny relative amounts, incomplete
days and ambiguous end dates cannot establish a scale. Ratios remain explicitly provisional:
historical denominator era and seat/product scope are not independently established. The owned
database initially had no readable latest daily pair because experimental collection was disabled.
An explicit one-shot collection now retains 27 count-report dates and 31 relative-report dates;
all 27 native credit values are zero and on-demand credits are absent. No ratio can be learned.
The CLI `--collect-daily-evidence <telemetry.db>` explicitly opts into one bounded collection,
without enabling background polling or modifying auth/settings. Credential-selected account is
rechecked after the backend bracket; a mid-fetch local account switch discards both reports.
Native credits are therefore not a demonstrated intermediate scale on this account; a TT candidate
still needs evidence of value over existing token/category/price baselines rather than a new name.

**Review implementation continuation (2026-09-19):** `composed-quota/v4` separates frozen
token-cost availability from unrelated activity/context/tier metadata. Strict replay still requires
timely quota labels, token amounts/model/effort and ownership assertions; unknown collection time
never becomes event time. Model evaluation and its CLI now show overlapping withholding reasons.
The previous zero-outcome strict result was caused by the broad training dependency: corrected
replay has 17 outcomes in one reset, 25 late-training exclusions and two missing compositions.
The incumbent remains better (0.2189pp envelope-distance loss versus 0.4559pp total-token,
0.5239pp categories and 0.5368pp model/effort). These are a pre-Desktop-recovery snapshot,
not current qualification: after historical token recovery, strict replay has zero held-out
outcomes in the native cohort, with 46 late-training exclusions and one missing composition.
Recovered historical evidence cannot retroactively become available at an earlier origin.
No model was promoted or gate weakened.

Live composed quota and chronological evaluation now share uncertainty calibration: at least
eight earlier completed reset generations with available outcome labels, generation-maximum
absolute errors, an empirical 80% target and a one-percentage-point radius floor. Model Lab
reports range sample counts, reported-value coverage and mean width; insufficient calibration
stays absent. These combined workload/cost error bands are not an activity-conditioned session
quota distribution, latent-usage coverage or an exhaustion probability.

Cost evaluation also includes the versioned `openai-api-standard-short-2026-09-19/v1` baseline.
It uses fixed public rates as retrospective workload weights, not historical bills, actual execution
tier, native credits or TT. Unknown model/rate coverage remains explicit; incomplete training blocks
fitting and incomplete held-out intervals are withheld, with matched pace/total comparisons.
It is diagnostic-only and cannot earn a production promotion label. On 44 compatible half-hour
targets its 0.3644pp loss trails total tokens (0.3191pp), although it beats pace (1.3741pp).

Diagnostics now derives versioned evidence-change signals from the existing immutable quota and
daily-report ledger. Signals retain before/after observation IDs, policy, account, first-observed
time and unknown effective time; configuration, missingness, capability, blocking state and
historical revision remain separate. Consumption, balance depletion and reset timestamps do not
produce policy-change signals. Account switches, failed attempts, overlapping fetches and contract/
client changes break semantic comparisons. Each surface has an independent 512-fetch/5 MiB read
budget; no signals found in this bounded history is not proof of stability. Raw source observations
remain the durable owner; derived signals rebuild without a second ledger or new acquisition.

Remaining forecast work includes joint conditional quota-burn uncertainty and distinguishing
endpoint/continuous activity from the implemented any-recorded-work target. Any future native-scale
or reconciliation promotion requires new supporting evidence;
the observed zero-credit reports and corpus comparisons do not justify either promotion.
Unsupported plan/task/enterprise routes
remain capability findings, not fabricated data or scheduled retries. A public TT unit remains
optional pending demonstrated value over the now-measured native/token/price baselines.

**Real-corpus reconciliation evaluation (2026-09-19):** the original scalar competitors now
share a restart-tested research implementation with a read-only `--reconciliation <codex-home>`
CLI. It reuses the existing file reader and ownership-aware rollout parser, excludes changed or
failed files, and emits only aggregate counts. Cross-file fingerprints require matching session,
timestamp, model/effort and both counter snapshots; they are repeat candidates, not canonical
request IDs. No persisted observations or production accounting are changed. In the initial
367-file sample, 66,263 token observations produced disagreements in 28 files: incumbent
9,161,300,274, high-watermark 8,855,223,082, bounded lineage 9,162,150,860. No strict cross-file
fingerprints repeated; this does not rule out inherited prefixes or cross-session copies.
These are physical-file experiment totals, not account billing or independent ground truth.
The v2 audit additionally compares ordered owned-token fingerprints with owner omitted, separating
equal sequences, strict prefixes, divergent prefixes and non-prefix overlap. It keeps occurrence
order/multiplicity and full category counters; it never merges files. The same 367-file sample had
zero candidate pairs even without owner matching, but the final scan excluded 3,996 pre-ownership records.
That initial exclusion was investigated below rather than treated as proof of inherited copies. Equal scalar totals with
changed category vectors are separately counted, including across research-state restarts.

**Desktop rollout ownership correction (2026-09-19):** the v3 audit separated pre-ownership token
records from other records and completely unowned files from inherited prefixes. All 553 excluded
token records in that snapshot belonged to five completely unowned files, not prefix segments.
Installed Desktop files use `rollout-<timestamp>-<thread UUID>_<suffix UUID>.jsonl`; metadata matched
the first UUID in all five, while the parser incorrectly selected the last UUID. The exact anchored
Desktop form now uses the first UUID, still requiring matching session metadata. Other filename
forms keep their prior behavior. Three of these paths matched the native catalog, two were
alternate paths for known threads. Catalog-selected acquisition remains authoritative; alternates
are not silently imported. Only suffixed paths get a changed typed-parser version and replay;
state-index schema v5 invalidates disposable selection hints so unchanged indexed files are
revisited. No observatory payload/schema change or general accounting-rule change is made.
See the [archived review continuation](../archive/CODEX_EVIDENCE_REPO_REVIEW_2026-09-19.md#14-real-corpus-reconciliation-and-model-continuation--2026-09-19).

**Descriptive even-burn comparison (2026-09-19):** Overview quota cards compare reported
consumption with elapsed window percentage at the same reading's capture time, separately from
Forecast. Start is explicitly inferred as that reading's reset minus its reported duration; no
other lane, account or historical duration is borrowed. Invalid/missing percentages, durations,
resets and captures outside the inferred window produce unavailable rather than clamped pace.
Stale cards label the comparison as last-known; the passage of wall-clock time alone never
improves the displayed comparison. This is an explanatory baseline, not a forecast, quota-policy
claim or TT conversion. Model Lab price weighting and the initial real-corpus audit are now
implemented above; broader reconciliation classification remains pending.

**Quota/tier evidence and reconciliation experiments (2026-09-18):** app-server quota responses
now retain bounded typed metadata for named and legacy buckets independently of supported quota
windows. The existing server-evidence ledger preserves account pseudonym, collection time,
limit names/IDs, plan, reached reason, nullable spend-control state, normal model alias, credit
balance/flags, individual spend-control values and native window/reset fields. Missing is not
false/zero, alternative buckets are not summed, and malformed optional metadata does not hide
usable current quota. High-frequency metadata is excluded from the activity comparison's
bounded history budget. No private route or credential handling was added for this evidence.

Local `thread_settings_applied.thread_settings.service_tier` is retained as nullable, spelling-
preserving setting evidence with existing source identity, offsets, timestamps and any explicit
turn ID. It is not silently attributed to subsequent token events or presented as confirmed
billing tier. Observatory schema 7 adds the nullable column; parser `typed-v6-service-tier-evidence`
replays existing sources once through the existing generation/recovery path. State-index schema 4
atomically invalidates only disposable selection fingerprints/watermark, so unchanged catalog files
are revisited too; native evidence and ingestion checkpoints are preserved. Diagnostics' retained
server-evidence read also displays latest quota metadata and physical tier-setting record counts.

Original test-only high-watermark and bounded `total-last` lineage experiments now compare
against the incumbent on ambiguous totals, interleaving, repeated snapshots, resets, partial
history, physical copies, every restart boundary and lineage eviction. They are simplified
competing assumptions, not copies of third-party implementations or canonical accounting rules.
The incumbent remains unchanged. Real-corpus reconciliation and chronological workload/TT model
evaluation remain next work; a synthetic winner is not a production promotion.

Plausible explicit assumptions are acceptable for useful experimental projections; retain their
basis and distinguish them from observed source fields. Lack of perfect evidence alone is not a
reason to block an experiment. User visual acceptance remains separate from builds/runtime checks.

**Experimental daily backend analytics implemented (2026-09-18):** Quota history now offers an
explicit opt-in for bounded read-only daily count and relative-usage reports, plus manual collection.
The existing server-evidence service runs these at its 30-minute cadence. The adapter reads the
selected Codex home's existing access token/account in memory, uses a fixed HTTPS origin with
redirects/cookies disabled, never refreshes authentication, and verifies backend account identity
before/after collection. No raw responses, credentials, account IDs or content are retained.
Typed immutable snapshots retain requested UTC dates, report units/freshness, parser contract,
account pseudonym, plan and before/after primary/secondary window duration/reset signatures.
Daily snapshots are not increments or live quota: repeated collection is never summed. Missing
amounts remain unknown, zero credits remain zero, and a latest failure is not replaced by an old
successful report. This intentionally revises the earlier app-server-only acquisition boundary
for this opt-in feature; normal quota acquisition remains app-server-owned.

The first production-adapter live check returned 27 count rows (`credit`) and 31 relative rows
(`percent`) with a matching account bracket. This slice exposes reported daily surface percentages
and token categories without inventing a credit denominator, TT conversion or rate-card pricing.
Entitlements/renewal, skill/plugin metrics, native-credit calibration and automated historical
policy-change interpretation remain follow-up work; available private routes are not all enabled
collectors. Retention uses the existing server-evidence store and policy; additive optional JSON
fields preserve v1 history and do not require a physical schema migration.

**Expanded backend survey (2026-09-18):** additional authorized read-only probes returned
skill/plugin daily history, reset inventory, account renewal metadata and a decimal-string
zero credit balance. Duplicate entitlement-map entries resolve to one identical native account,
not two subscriptions. Code-review metrics returned 200 with empty data: route availability is
now observed, but populated metric semantics remain unverified. Direct thread v1 returned 403,
task v2 returned 404; workspace credit/token reports remain unavailable for the tested context.
These results support an experimental daily-allowance dashboard without waiting for per-thread
costs. Preserve per-report freshness and observed-versus-estimated amounts. Details:
[expanded survey](../archive/CODEX_EVIDENCE_REPO_REVIEW_2026-09-19.md#10-expanded-endpoint-survey--2026-09-18).

**Direct-backend experiment authorized (2026-09-18):** the user selected
`how-much-i-get-from-codex` as the closest functional reference and explicitly relaxed the
previous research acquisition boundary to allow private read-only backend probes. An isolated
probe using existing Codex authentication in memory obtained daily counts and a daily breakdown
explicitly labelled `percent` (HTTP 200). This account's daily count credits were all zero
despite nonzero tokens; model-credit fallback fields did not supply native credit amounts.
Credit events were empty (200), workspace breakdown returned 400 and plan history returned 404.
These are actual bounded observations, not inferred capability outcomes. Prior statements that
no direct requests were made describe the earlier review, not this subsequent experiment.
Released acquisition remains unchanged; experimental direct HTTP is now permitted research,
not categorically excluded. Prioritize daily usage/allowance history and useful explicitly
estimated capacity over proving a universal normalization scale first. Keep observed amounts
separate from rate-card estimates. See the [experiment addendum](../archive/CODEX_EVIDENCE_REPO_REVIEW_2026-09-19.md#9-authorized-direct-backend-experiment--2026-09-18).

**External Codex evidence review (2026-09-18, proposals only):** seven pinned implementations
were compared with current acquisition and accounting. Native daily credits/relative usage
are a promising intermediate scale, not a verified universal allowance conversion; synthetic
ratio fixtures do not independently validate the author's runtime observations. CodexBar's
copy/interleave safeguards inform proposed reconciliation tests, not an approved normalization
change. The installed CLI schema still exposes no structured daily/plan-history/task-v2 report
seam. Keep Codex-owned authentication and TT's existing research status. Proposed evidence
fields, compatibility gates, drift signals and tests are in
[the source review](../source-notes/CODEX_EVIDENCE_REPO_REVIEW.md); no new acquisition,
schema migration or model was implemented by that review.

**Desktop analytics acquisition trace (2026-09-18):** installed Windows package
26.915.3509.0 implements analytics via renderer queries → private Desktop HTTP host service
→ authenticated backend requests. App-server supplies Desktop authentication, but the reports
do not flow through the public `account/usage/read` contract. Desktop additionally uses
`thread_usage/query_v2`, with grouped descendants and allowance/credit metrics distinct from
CLI credit estimates. No supported external report/export seam was established. Existing
`NoSupportedSeam` states and the Codex-owned authentication boundary remain correct for
TajsTokens. This was static installed-code inspection plus user screenshots, not a live backend
probe; unavailable UI text does not establish an HTTP status. Details and version anchors:
[Desktop analytics acquisition](../source-notes/CODEX_DESKTOP_ANALYTICS.md).

**Server-evidence review hardening (2026-09-18):** latest attempts now use surface/thread
identity regardless of account-correlation outcome; historical successes cannot survive a newer
failed/null attempt as current thread estimates. Account attribution conflict no longer replaces
the provider's usage outcome. Schema 14 losslessly shares daily values and ordered bucket sets
across small immutable fetch observations, retaining revisions, missing/empty distinctions and
all original provenance. No history expiry, rollout normalization or forecasting change is added.
App-server transport uses bounded chunk buffering and rejects unsupported server requests.
CLI modes explicitly reject unknown input before side effects. Main-window restored size and
maximized preference are saved separately in `data/window-state.json`; first use defaults to
1200×850 and restored sizes are clamped to the current display work area.

Validation: 382 Core tests and a zero-warning/error Windows build passed; daily build
`20260918T115908848Z-c94e7474` was deployed/restarted. A content-hash comparison verified all
18 pre-migration logical server observations unchanged. The next live collection reused the
same 159 daily values/one bucket set; account fetch JSON fell from about 8,157 to 1,052 characters.
Quota history was inspected at 700×650, 1000×700, 1200×850 and maximized; narrow details remain
reachable by outer scrolling. The fixed-height Pivot was left unchanged. A graceful restart
verified both maximized preference and the prior restored size. This is not exhaustive DPI testing.

**Provider-native server evidence (2026-09-18):** separate app-server acquisition now retains
backend account token activity and optional per-thread estimated credits/model-effort-speed
token groups. Current quota remains the provider-fresh anchor; local rollout accounting is
unchanged. Owned schema 13 adds immutable content-free fetch observations, typed capability
states, source contract/client versions and before/after account pseudonym brackets. Usage
responses have no account ID: stable brackets are explicitly **server-correlated**, not native
provider verification or permission to rewrite rollout AccountKeys.

The existing collector schedules bounded reads at most every 30 minutes per process, after
publishing normal telemetry. Diagnostics exposes on-demand fetch, retained capability states,
lifetime/day/thread comparisons and repeated local event-fingerprint candidates. The local
probe observed account activity (159 daily buckets), but all six sampled thread estimates were
unavailable. Plan history and grouped analytics exist in current upstream's authenticated TUI
backend client, not the installed/upstream app-server protocol. The user explicitly chose to
keep authentication owned by Codex: those surfaces are **NoSupportedSeam**, not zero or a
claimed backend 404. No browser, auth-file, credential-export or direct-HTTP path was added.

The explicit single-account assertion has now been saved against a fresh bracketed current
account for 356 bounded retained source/session ranges, through the existing revocable Settings
association owner. Native account IDs and collection times remain untouched. No inference from
numeric similarity creates an association. Native, server-correlated, user-declared and
unattributed/conflicting evidence remain distinguishable; only the existing assertion training
path is eligible for research, under its existing stronger promotion gates.

New comparisons found concrete local/server mismatches: on 2026-09-09 two physical local sources
retain identical event fingerprints and each equals the backend day total. These are exposed
as duplicate candidates, not automatically removed. A separate ownership-aware reconciliation
decision is needed before changing historical accounting. No provider credits→allowance mapping
or new historical quota labels are available yet; TT and forecasting policy remain unchanged.
Exact contracts, limits, observed results and unavailable work are in
[Codex server usage evidence](../source-notes/CODEX_SERVER_USAGE.md).

Validation: 373 tests passed; Windows Debug build had zero warnings/errors and dogfooded
`20260918T112251487Z-45eb7aad`. Automatic and explicit persisted acquisition worked against the
existing database. Re-evaluation retained 262 user-associated historical cost observations;
native cost validation still spans only two resets, strict composed validation only one.
Five compatible asserted training intervals have no strict held-out outcomes yet. No model
earned promotion; no server evidence was inserted into quota labels or model features.

**Product presentation pass (2026-09-18):** Overview puts remaining-quota gauges, status,
local reset timestamps and conditional outlooks above evidence. Healthy cards no longer carry
permanent InfoBars; omitted windows become a secondary strip. Startup explicitly says it is
reading local history, and does not fabricate detection/progress milestones. Recent token
activity uses a 24-clock-hour axis when offset-aware timestamps exist; absent records remain
gaps rather than proof of inactivity.

The shell owns navigation, selection synchronization and global Back. Diagnostics, Model lab,
rollout coverage, native sources, Data explorer and CLI harness live under Advanced. Forecasts
opens with current outlooks, followed by planning, token prediction and history; model research
has its own Advanced destination. Planning retains the honest default of one active chat and
no subagents; it does not claim to infer a recent work pattern that the planner cannot yet supply.

Quota history adds an endpoint timeline per account/source/window/reset series, with optional
local-token bars and point selection into existing details. It does not interpolate gaps or
sum overlapping rollout readings into account consumption. Usage adds a proportional-time
summary, removable scope filters and a compact default table with opt-in detailed columns.
Human-facing times are local; UTC aggregate boundaries are retained and disclosed rather than
relabelling UTC daily buckets as local calendar days. Conversation provenance is collapsed.
Shared typography, card/status resources and a content-width breakpoint replace repeated
presentation decisions on these surfaces. Main-window default is 1200×850, minimum 700×650.
Session-local bounded page caching retains filters, tab and thread selection; queries refresh
on return. This is not disk-persisted UI history or a new telemetry contract.

Validation: 360 Core tests passed; the Windows Debug solution build finished with no warnings
or errors and dogfooded build `20260918T104600975Z-d4b62a9f`. Runtime inspection covered the
1200-wide shell and 700-wide Codex/Usage/Forecasts/Quota history layouts, quota-point selection,
Usage drill-down and filter removal, Back restoring Usage scope with fresh data, and Model lab
sidebar synchronization. First-launch/no-data behavior was inspected in code, not by clearing
real data. This is a focused runtime pass, not exhaustive DPI, accessibility or user visual acceptance.

### Quota-cost calibration experiment (2026-09-18)

Implemented the evaluation-first scope from the now-historical local roadmap; its rationale is
consolidated in [the layered design](../source-notes/TT_LAYERED_STATISTICAL_DESIGN.md#7-consolidation-of-the-earlier-quota-cost-roadmap).
This section remains the product authority. `QuotaCostObservationBuilder` derives non-overlapping 30-minute and
two-hour targets over the existing read-only `SqliteForecastDatasetReader`. Targets use
actual `(start, end]` workload, retain full quota cohorts and reset boundaries, and exclude
cached rollout repeats, invalid/conflicting crossings and saturation. Intervening incompatible
cohort observations prevent A/B/A metadata from being bridged. Sources/horizons can overlap
and their counts must not be summed as independent account evidence.

`QuotaCostEvaluation` compares persistence, incumbent pace, total tokens, nonnegative
category weights, category/model/effort weights, and separate context/activity/TTFT/UTC-week
ablations. The first 20 matured disjoint intervals freeze fitting, vocabulary and scaling;
later targets cannot retrain away a residual shift. Regularization is fixed in the versioned
policy, not tuned on held-out outcomes. No intercept assigns account movement to elapsed time;
optional covariates interact with recorded workload. Missing context/runtime and account
attribution remain explicit. Native generation throughput is not inferred from unmatched
aggregate output and wall-time observations; that covariate remains unsupported.

Training minimizes squared distance to a meter envelope plus ridge penalty. App-server integer
readings use corroborated half-point endpoint rounding envelopes; this is not a guarantee of
backend precision or accounting lag. Fractional/embedded readings retain their numeric values
and use an explicitly unverified one-point-per-endpoint sensitivity allowance, not an inferred
provider step. Those sensitivity cohorts cannot earn a material-win label. Equal readings
never become exact-zero targets. Saturation needs one-sided censoring and is withheld for now.

Forecasts → Model evaluation now includes separately labelled **Cost calibration (actual work,
not forecast)** groups, frozen coefficients, interval loss, displayed-delta MAE, generation
counts, residual quantiles and unexplained positive movement. Read-only CLI `--cost` provides
aggregate comparisons without raw account/session identifiers. Derived reports are versioned
and rebuilt, not new durable source tables. Optional bands need eight earlier completed
held-out generations; reported intersection with target envelopes is not latent coverage or a
probability. Candidate shifts require two same-direction shifted generations against a frozen
three-generation reference; they generate no notifications and do not rewrite regime history.

A cost-only win needs at least 16 held-out targets in three reset generations, known account,
supported precision, and a material generation-average advantage over pace/total and the
candidate's simpler parent. It must also win on most generations. That is a diagnostic gate,
not production selection. Actual workload inputs make these errors fundamentally different
from end-to-end forecast errors; existing live quota prediction is unchanged.

The user confirmed on 2026-09-18 that all retained JSONL rollouts belong to their account.
`--cost-owned-rollouts` records that explicit user assertion in evaluation provenance without
inventing native account IDs or pooling repeated session/source readings. Default diagnostics
say "native account ID absent", not that the rollouts belong to another account.

**Ownership and promotion hardening:** runtime settings schema 4 separately persists explicit,
revocable account associations, bounded by source identity, session and event-time range.
Settings → Historical rollout ownership asks the user to select a recorded account and confirm;
it never rewrites native `AccountKey`. Overlapping contradictory assertions fail closed.
Compatible asserted intervals may supplement the frozen native training set (at most 120,
deduplicated by interval overlap, strictly preceding native training). Plan, bucket, window,
provider, profile and asserted account must match; unknown plan/bucket cannot bridge cohorts.
Assertions never supply validation targets or provider-verified attribution. The annotation-only
`--cost-owned-rollouts` flag does not create an association.

Live composed promotion now requires both reconstructed-event-time and strict `CollectedByOrigin`
paired wins. Strict evaluation requires all frozen training inputs, including any assertion,
available by each origin, a collected origin meter and an outcome collected within five minutes
of its event. Missing collection time fails closed. The last two validation generations must
not contradict the overall advantage over incumbent and pace. Native candidates require at
least 16 strict targets across eight reset generations; asserted-history candidates require 32
across twelve, plus a native-target cost advantage over native-only total-token training.
Empirical ranges use completed strict-validation generations. A later historical import or
ownership assertion cannot retroactively qualify earlier origins. Live native reads retain a
bounded 120-day lookback so these weekly-generation gates are achievable; existing row limits
and incumbent fallback remain in effect.

Review validation (2026-09-18): 360 Core tests passed; Windows Debug build passed without
warnings/errors and installed/restarted daily build `20260918T101858499Z-3b94f55e`.
Read-only retained-data backtests yielded 26 reconstructed half-hour outcomes across two
resets, versus five strict outcomes across one reset. No candidate can promote. Ownership
associations were not auto-created; the user selects the recorded target explicitly in Settings.

Initial read-only retained-data evaluation found 316 usable targets, mostly session-separated
rollout cohorts without native account IDs. The app-server weekly half-hour cohort supplied 20 training
and 28 held-out targets spanning only two held-out reset generations. Interval loss was
1.308pp for pace, 0.310pp for total tokens, 0.258pp for model/effort and 0.120pp for the context
ablation. No candidate earned promotion or calibrated bands. This is a dated local snapshot,
not universal cost weights or a provider-load finding.

Validation: all 349 Core tests passed, including eight targeted calibration regressions for
alignment, censoring, reset/cohort isolation, ownership provenance, leakage, sparse data and
replicated shifts. Final read-only snapshot at 2026-09-18 09:17 UTC contained 318 targets;
growth reflects ongoing normal collection. Windows Debug build passed with zero warnings/errors
and dogfooded/restarted build `20260918T091810528Z-85fd9411`. App startup was acknowledged;
the new evaluation view has build proof, not a completed native visual acceptance pass.

**Composition and transfer implementation (2026-09-18):** `WorkloadCompositionPrediction` projects the
existing scalar token forecast into five disjoint token categories plus model/effort shares from
the preceding two hours, retaining missing composition rather than inventing zeros. Current token
forecasts and retrospective token replay carry the vector. The Forecasts evidence view displays it.
`ComposedQuotaEvaluator` applies frozen cost weights to origin-only predictions and compares the
result with actual-workload cost, legacy pace and the actual incumbent quota policy on matched
outcomes. Event-time reconstruction and collection-time availability remain separate.

`QuotaTransferEvaluator` explicitly compares earlier source-regime weights, destination-scaled
source weights and destination-local weights. Scale fitting uses only the first 20 destination
intervals; held-out targets cannot change it. Account/source/session compatibility is required;
absence of native account linkage is not silently converted to transfer evidence. Evaluation
groups and read-only CLI `--composed` / `--transfer` expose these distinct experiments.

`ComposedQuotaPolicy` now supplies evidence-gated live composition-to-quota selection. It keeps
the incumbent unless a supported cost candidate wins, paired end-to-end errors materially beat
both incumbent and pace, at least eight independent reset generations support the comparison,
and current composition and recent outcomes are available. Unseen model/effort, missing native
account identity, detected residual shifts and insufficient calibration all preserve fallback.
Only total/category/model-effort cost models are eligible: future context/activity/runtime
features from oracle cost ablations never leak into a live forecast. Empirical ranges use
completed-generation maximum errors, not an exhaustion probability. Saved horizon evidence
continues through the existing forecast persistence owner.

Retained-data end-to-end replay had 24 paired half-hour targets across two reset generations
(four targets lacked recent composition). Interval loss was 1.577pp total, 1.477pp categories,
and 1.647pp model/effort, versus 1.276pp legacy pace and 1.579pp incumbent policy. This does not
pass promotion: explanatory cost gains are not equivalent to future-work forecasting gains.
The transfer evaluator found no compatible native-account-linked regimes in this snapshot.
Its chronological scale/local-only comparisons are implemented and synthetically verified,
but empirical transfer and TT are unsupported, not invented successes.

Validation of that slice: 356 Core tests passed, including origin-only composition, backfill
availability, paired cost/forecast leakage, transfer chronology/account isolation and live
selection fallback gates. Windows Debug build passed with zero warnings/errors and installed
and restarted `20260918T093836657Z-92e92959`. The completion audit is in
`.codex/docs/full-roadmap-completion-audit.md`; native visual acceptance remains separate.

TT remains evidence-gated research, not a shipped unit. No active prompts,
automatic messages, external uploads, crowdsourcing, pricing or hidden-runtime probes were added.

### Reset identity and review hardening (2026-09-18)

Quota burn's reset history defaults to account-meter quota drops. Reset-time shifts have a
separate filter and explicitly do not establish replenishment. Headers use detection time
labelled “Observed”, not inferred effective reset time; expanded deadlines include seconds and
the signed shift. Rollout diagnostics warn that sessions can observe an earlier change later.
No unverified cross-account/source grouping or forecasting change is implied. This presentation
also applies to already saved events; raw classifications remain unchanged.

`QuotaResetGenerationPolicy` owns the one-second bounded reset-time semantics shared by
forecast segmentation, reset detection and calibration (including scenarios). A sequence of
adjacent one-second shifts cannot chain into an arbitrarily wide generation. Scenario history
also retains the full quota cohort and does not borrow another bucket/plan/duration's samples.
Raw timestamps and exact current-anchor matching remain unchanged.

Reset event identity v2 hashes the full `QuotaHistoryPolicy.Cohort` plus the adjacent observation
times using unambiguous structured encoding. Intelligence component schema 4 atomically rebuilds
the derived reset cache from all retained eligible quota history, removing old jitter signals
and incomplete identities without changing source observations. If rebuilding fails, both the
old cache and version remain retryable. The rebuild reads retained quota history once at upgrade;
it is not restricted to the ordinary recent-refresh limit. Old derived events whose inputs are
no longer retained cannot be reconstructed; deployment backups retain the previous database.

Intelligence component schema 5 extends this to ongoing retirement: deleting retained quota
evidence invalidates the entire derived reset cache in the same SQLite transaction and persists
a rebuild marker. Until the next successful refresh the cache is empty, not stale. Refresh
rebuilds all retained eligible history when marked, restoring unrelated historical events as
well; ordinary refreshes retain the existing 512-observations-per-stream bound. Evidence reads,
event writes and clearing the marker share one writer transaction so ingestion cannot race a
stale refresh into the cache. Failed retirement rolls back invalidation; failed refresh retains
the marker for retry, including after restart. This deliberately coarse invalidation avoids
inventing source-generation ownership for events derived from adjacent observations.
`QuotaHistoryCohort` is a shared model contract under `Core.Models`; policy remains in Services.

Disposable staging deletion failures warn rather than overriding a successful deployment or
its original failure; cleanup still refuses unsafe paths/reparse points. First-run process
inspection tolerates processes exiting during enumeration. Read-only rollout comparison rejects
records carrying both owner aliases rather than selecting one and hiding ambiguity.

### Historical analytics first (2026-09-17)

Quota burn's normal timeline shows account-meter transitions with explicit local start/end
times and before/after percentages. Source details and local activity are collapsed by default.
Rollout/all-source modes live under diagnostics and explicitly describe observations, not
unique changes: different sessions can repeat one account transition and must not be summed.
This is a presentation change, not a source merger or forecast training change. Current forecasts
remain authoritative-source/account/cohort scoped; historical evaluation remains cohort separated.

Usage is integrated under Codex → Usage breakdown alongside Work & threads, over the existing intelligence/accounting
owners, not a new acquisition or storage system. Models, session repositories (projects),
sessions and UTC hourly/daily/monthly groups use the same disjoint input, output, cache read,
cache write and reasoning columns. Selected rows filter all breakdowns; exact counts and
reported-versus-component differences are inspectable. Search and sorting operate on displayed
groups. All-retained history is available; long time ranges explicitly disclose coarsened buckets.
The usage-only read path skips quota forecasts/burn queries and removes the old top-20 dimension
cap. A partial first time bucket is retained instead of silently dropping in-range events.
Overview keeps measured quota/reset values prominent and places conditional outlook details
behind an expander; missing/stale warnings remain visible.

The standalone Usage sidebar entry was removed after UX feedback. Codex keeps its work browser
as the default, with an explicit switch to usage reports. A thread offers View token usage;
session report rows offer Open thread. Switching views preserves report filters and work context,
and detaching reports uses their existing cancellation lifecycle. Browser-only controls are
hidden in usage mode so there are no competing range selectors or duplicated page headings.

Reconciliation used installed Tokscale 4.17.0, Codex-only local CLI JSON for local date
2026-09-08 (UTC interval September 7 22:00 to September 8 22:00), not the screenshots'
multi-provider/two-device totals. TajsTokens recorded 122,888,366 tokens; Tokscale's disjoint
buckets total 123,088,748. Session-level comparison isolated the entire 200,382 difference
to one event at September 8 22:00:20.419 UTC, just outside our interval, which Tokscale
included in the preceding local day. Its components are 4,981 uncached input, 194,816 cache
read, 347 non-reasoning output and 238 reasoning tokens. Other session aggregates match.
This is a time-attribution boundary difference, not evidence to overwrite native event time.
The earlier inspected upstream reference dcf8d3656bbcecf05d112c6f35f9be328e4f66b7 is not
claimed to be an exact version match for the installed binary. No raw source content or
credentials were exported; comparison artifacts remain private under `.codex/temp`.

This slice does not add remote-device ingestion, additional providers, credential management,
reset-credit actions or API-price estimates. Those are separate source/product contracts;
the new tables intentionally do not fabricate those capabilities.

Validation: 317 Core tests passed, including partial bucket conservation, all-retained
range preservation, more than 20 sessions, disjoint totals and intersecting project/model/session
filters. The final Windows Debug build passed with zero warnings/errors and dogfooded build
`20260917T203858538Z-52dcf1c3`. Live native inspection verified Overview, Usage model/project
tables, exact-count detail and project-scoped session navigation. User visual acceptance remains
separate; no remote totals or pricing accuracy is claimed.

This section owns delivery sequencing and acceptance. It replaces the separate delivery plan;
dated audits elsewhere in this document are evidence, not a queue to rerun.

### Implemented baseline (2026-09-09)

| Area | Implemented behavior / owners |
| --- | --- |
| Current quota | `CodexAppServerQuotaProvider`, `TelemetryCoordinator`, `TelemetrySnapshot`, `QuotaAlertEngine`: current lanes, reported omissions, freshness, account-scoped anchors and alerts. |
| Collection and coverage | `CodexSessionIngestionService`, `CodexRolloutParser`, observatory/semantic stores: incremental content-free evidence, native identity, replay/checkpoints and historical effort repair. `CodexStateCatalog` accelerates acquisition; `CodexRolloutComparisonReader` inspects alternate files without importing or deleting them. |
| Native work and accounting | `ICodexThreadReadModel`, `CodexThreadObservabilityService`, `CodexThreadReadModelPolicy`: workspace/root/subagent navigation, source alternatives and bounded local detail. Native rollout accounting is the default; optional Tokscale comparison/fallback remains explicit. |
| Usage prediction | `TokenWorkloadPredictionService`: retrospective rollout-trained token predictions, no quota-row/account dependency; current Overview/Forecasts display and evaluation. |
| Quota intelligence | `QuotaHistoryPolicy`, `SqliteIntelligenceService`, `SqliteForecastDatasetReader`, Core quota/scenario services: provenance-aware rollout/app-server history, explainable eligibility, burn history, saved/current outlooks and source-isolated backtests. Current anchors remain fresh/account-scoped. Cost/residual-shift diagnostics were added on 2026-09-18; production regime adaptation and cross-cohort calibration transfer are not implemented. |
| Diagnostics and operation | `TelemetryDiagnosticsPresenter` / `DiagnosticsPage` reuse shared collection health and bounded events. `SqliteTelemetryRepository`, `AppDataLocation`, `RuntimeSettingsStore` and `tools/dogfood` own data, settings, upgrades and recovery. |

Last implementation milestone (2026-09-09): **313 Core tests passed** in 48 seconds; Windows Debug
build passed with zero warnings/errors and normal dogfood installation/restart was verified.
Live checks confirmed owned schema 10, state-index component 3 and new provenance-bearing rows
arriving during the one-time replay. The full private-copy replay/evaluation is recorded below;
this is not a claim that the live replay or user visual acceptance has completed. SQLite test
collections now run serially because process-wide pool cleanup raced other fixtures' native handles.

### Delivered slice: canonical historical quota evidence

- Owned schema 10 retains optional native bucket/plan/lane, session/source-generation/record identity,
  actual collection time and missing-event-time status. Legacy rows survive without invented metadata.
  `typed-v5-quota-provenance` and state-index component 3 safely revisit unchanged historical files;
  exact retries preserve first collection time and same-time alternatives are not overwritten.
- `quota-history/v1` supplies historical eligibility to burn history, reset refresh and evaluation.
  Rollout sessions with unknown account scope remain separate from each other and from app-server
  accounts; reported bucket/plan/duration changes do not share calibration. Possibly cached repeats,
  conflicts, invalid windows and legacy missing provenance have explicit reasons. Unknown rollout durations
  are retained but not forecast. No cross-source synchronized stream is manufactured.
- Current `quota-walk-forward/v3` and `quota-workload/v2` retain fresh app-server/account anchors,
  isolate reported bucket/plan history, and record those anchor labels in saved evidence. Unknown
  historical scope is not transferred to the current account. Workload corrections still have to earn
  selection; this slice does not claim a new universal conversion or calibrated risk probability.
- Forecasts exposes historical eligibility counts without a backtest; evaluation reports source/cohort,
  event-time versus collection-time mode and within-cohort non-overlapping origin counts.

Private-copy replay on 2026-09-09 read **355 files / 387,517 records, zero ingestion errors**.
The resulting 195,856 quota rows contained **10,297 eligible readings** (2,199 app-server and
8,098 rollout), 88,138 possibly cached repeats, 95,986 legacy rows without provenance, nine
conflicting readings, 142 same-time duplicates, 750 invalid/missing windows and 534 unsupported
durations. Retained legacy and replay rows are not summed as independent evidence.

The actual short-horizon policy had **153 half-hour, 59 two-hour and eight 24-hour origins** across
separate cohorts. For rollout primary five-hour targets, half-hour MAE was **5.41pp** versus
**7.18pp** for legacy EWMA (51 origins); two-hour MAE was **21.16pp** versus **23.03pp** (19 origins).
These are retrospective observed-meter errors, not live-account performance or globally independent
samples across sessions. No workload correction earned selection and no cohort earned a horizon
uncertainty band. Backfilled rollout rows produced no historical collection-time origins. Therefore
the existing pace-based fallback remains; broader learned calibration is still next work.

**Acceptance coverage:** representative paired and non-paired source fixtures cover rounding, cached repeats,
bucket/window changes, resets, missing account scope and disagreement. Replay/collection does not
double count, rewrite raw evidence or leak later inputs. Evaluation reports coverage, independent
targets and errors for the actual policy; current-anchor safety is unchanged. Automated, runtime
and visual evidence remain separately reported. Do not demand exact synchronized equality or
complete historical account identity before separately scoped historical modelling can be useful.

### Delivered UI pass: readable answers and progressive detail

The native Overview, Forecasts and Quota burn views now use clearer hierarchy without changing
prediction or collection semantics:

- Responsive labelled navigation keeps everyday views together and moves the explicit CLI harness
  beside the raw explorer. The full build identifier is selectable in Settings rather than filling the title bar.
- Overview emphasizes remaining quota and reset outlooks. Cards stop stretching to match an expanded
  neighbor; narrower layouts stack. Hourly bars show labelled amounts with a proportional zero baseline,
  and explicitly disclose omitted gaps. Local-history totals and token prediction are separately labelled.
  Detailed source logs remain available under a collection-health summary; stale/missing quota stays visible.
- Forecast tabs use **Quota outlook history**, **Usage prediction**, **Plan a workload** and **Model evaluation**.
  Latest saved outlooks and quota evidence no longer occupy unrelated tabs. History separates the outcome,
  account, horizon summary and optional method/provenance. Hourly sampling retains source/bucket/plan scope.
- Usage predictions use compact amounts and visible uncertainty ranges. The scenario form explains its
  initial state. Evaluation selects one cohort/target before comparing model errors; no cross-cohort ranking
  or aggregation is implied. Detailed methods and metrics remain expandable, and range changes clear old results.
- Burn history separates activity from source/account labels. Reset signals expose their source without
  expansion and support a separate source filter; detections are never silently merged into account resets.

Validation: 313 Core tests passed; the Windows build passed with zero warnings/errors and dogfooded
the initial UI pass. Native accessibility inspection confirmed the rebuilt Overview and live values load,
but the user's screenshot exposed an Overview width/reflow regression. The correction removes its
max-width/scroll-content interaction and nested adaptive states: actual viewport width now constrains
content and selects the column layout, following the Codex browser's size-change pattern. After the user
authorized restart, the corrected Windows build passed with zero warnings/errors and was dogfooded.
Live visual inspection confirmed the Overview's bounded, two-column layout both windowed and maximized,
and its stacked layout at roughly 850 pixels wide. Forecast-history and usage-prediction tabs rendered
correctly; the local evaluation completed and displayed cohort-scoped model comparisons. User design acceptance remains separate
from these smoke checks. No forecast algorithm or durable data contract changed.

### Next: regime-aware quota calibration

- Segment reported account/bucket/window/plan changes and investigate sustained unexplained model
  error as a possible regime change. Version the inferred policy and validate transfer/fallback;
  do not declare undocumented provider changes as observed facts.
- Compare direct quota pace, workload-informed corrections and the usage-to-quota composition.
  Forecast remaining quota/reset survival and useful ranges across supported observed windows,
  without treating any plan as unlimited or assuming a fixed allowance.
- Only then consider a shared normalized workload score/TT experiment if there is a concrete
  measured benefit over direct features. This is not a required prerequisite or promised release.

Other known limitations stay scoped: alternate-file inspection is not automatic alternate ingestion;
coverage counts are not exhaustive work/accounting proof; broader auxiliary-source pagination and
snapshot-consistent multi-page log reads are not complete. Address these when a concrete workflow
requires them, rather than restarting the completed source-foundation audit.

### Acceptance and upkeep

Be correct under observed conditions, conservative when uncertain and diagnosable when wrong.
Choose coherent vertical slices using the existing owners; preserve unrelated work and native data.
Give accounting overlap, data loss and privacy stronger scrutiny than cosmetic polish. Follow
AGENTS.md for proportional validation and deployment. A pending user visual pass is not hidden
implementation scope. Update this section when a meaningful slice, decision or verified limitation
changes—not after every routine command.
