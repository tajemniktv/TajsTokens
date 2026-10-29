# Ceiling selection and censored targets (NEXT-003)

## Decision

Retain ceiling observations and the existing ordinary-loss exclusions. The audit
shows that excluded attempts have a different headroom/workload distribution and
can reuse one report or overlap heavily. They are not interchangeable with ordinary
training intervals, or independent additional targets.

Do not introduce a latent lower-bound target or censored likelihood yet. A reported
100%, known first collection time and timely local token labels do not establish
an unbounded latent-cost quantity, its clipping operator, backend reporting lag,
physical crossing time or complete account attribution. This closes the current
selection-measurement/deferral decision; it does not declare censoring irrelevant.

To revisit fitting, specify the target and its reporting/clipping/lag assumptions,
obtain enough distinct compatible ceiling episodes, and validate a proposed loss
chronologically against unchanged ordinary and simpler baselines. Merely assigning
every overlapping attempt the displayed remaining budget would overweight one
episode and manufacture exactness that the observations do not supply.

## Construction audit contract

`quota-censoring-selection/v1` observes the existing
`QuotaCostObservationBuilder` immediately before its saturated-outcome rejection.
It does not create `QuotaCostObservation` rows for those attempts. With no observer,
normal production calls perform no extra feature/clock audit work. Enabling the
observer preserves ordinary rows and construction coverage exactly in regressions.

Earlier checks still control the candidate frame: overlapping a selected ordinary
interval, saturated origin, epoch warmup, missing/late outcome, reset/plan/source
boundaries and incompatible cohorts can reject a start before it reaches the
observer. First-rejection counts partition the original candidate starts.

An ordinary accepted interval advances the sampler's previous-end clock. A ceiling
outcome does not. Thus ceiling **attempts**, distinct endpoint reports and
non-overlapping intervals are different denominators. Six-hour blocks describe
spacing, not independence. Separate horizons and sources can overlap and are not
pooled as independent account work.

The audit retains effective and original reported clocks, fixed nominal targets,
actual endpoint times, displayed movement/headroom, recorded local token amounts,
category integrity and collection availability. Actual interval workload uses
`(origin, endpoint]`, including permitted polling delay; it is an observed cost input,
not a forecast stretched after seeing a label. Missing local events remain distinct
from account idleness or zero account cost.

Reconstruction and a collection-qualified quota frame are separate. Unknown,
negative or over-five-minute quota collection lag is a boundary in the latter;
original reported times remain available. Known token-cost availability and a
five-minute endpoint-label budget are separate diagnostics, not proof of complete
coverage or provider lag. Late retained token labels are not silently admitted as
timely labels. No observation is deleted or rewritten by this read-only study.

The [provider adapter](../../src/TajsTokens.Infrastructure/Providers/CodexAppServerQuotaProvider.cs)
stores the `usedPercent` number it reads; it does not define a latent clipping
model. The shared history policy excludes out-of-range values from eligible
intervals. A reading at/above 100 in the input frame and an eligible 100% endpoint
therefore retain different counts. Derived display remaining is not a new billing
or latent-cost observation. Reporting/collection times do not bracket physical
crossing under unknown backend lag.

The study refuses more than 100,000 audited attempts rather than silently truncating
the summaries. The existing dataset bounds and cancellation also apply. No new
fit, source acquisition, durable schema, TT weights or live forecast behavior is
introduced.

## Fixed native evidence

| Target range / mode | Input quota rows | Frame readings at/above 100 | Eligible ceiling readings | Ordinary intervals | Audited ordinary + ceiling attempts |
|---|---:|---:|---:|---:|---:|
| October 22 00:00–October 28 16:40 UTC / reconstructed | 15,950 | 38 | 27 | 488 | 488 |
| Same recent range / collection-qualified | 15,950 | 26 | 26 | 488 | 488 |
| September 27 00:00–October 27 14:58 UTC / reconstructed | 29,663 | 184 | 173 | 2,981 | 3,031 |
| Same month range / collection-qualified | 29,663 | 172 | 172 | 2,981 | 3,031 |

The recent range has ceiling readings but no ceiling-outcome attempt after the
earlier construction checks. Its 100 saturated-origin rejections across horizons
are not 100 censored interval targets. The month has 668 such origin rejections,
separate from the 50 endpoint-ceiling attempts. These summed horizon counts do not
represent independent saturation events.

Both month modes have the same affected known-account FiveHour frame; aliases
differ by report and are not cross-report account identities:

| Horizon | Ordinary intervals | Ceiling attempts | Distinct ceiling endpoint times | Non-overlapping ceiling attempts |
|---|---:|---:|---:|---:|
| 5 minutes | 194 | 1 | 1 | 1 |
| 15 minutes | 66 | 13 | 10 | 1 |
| 30 minutes | 32 | 17 | 14 | 1 |
| 120 minutes | 8 | 19 | 17 | 1 |

All ceiling attempts in this frame share one reported reset clock. Later fresh
100% readings and overlapping starts do not supply separate physical crossings.
Even though all 50 attempts have known/timely label-availability diagnostics, they
do not establish backend reporting-delay bounds.

Selection effects are visible in the same frame. Native workload is in millions
of recorded tokens; headroom is displayed percentage points at origin:

| Horizon | Mean ordinary / excluded headroom | Mean ordinary / excluded native workload |
|---|---:|---:|
| 5 minutes | 51.59 / 1.00pp | 0.164 / 0.383M |
| 15 minutes | 53.88 / 4.15pp | 0.445 / 1.928M |
| 30 minutes | 56.53 / 4.59pp | 0.861 / 2.134M |
| 120 minutes | 78.50 / 4.74pp | 3.443 / 2.205M |

Ceiling exclusions preferentially remove low-headroom attempts here. Workload
differences depend on horizon; they do not establish a universal direction or size
of estimator bias. Overlapping excluded attempts also weight an episode differently
from accepted disjoint intervals. This is descriptive selection evidence, not a
causal correction or proof that local work explains all account movement.

## Validation and reproduction

Six regressions verify exact ordinary-output/coverage/input parity, overlapping
attempts reusing one report, known/late/unknown/invalid collection, original-clock
retention, reset/returning-plan boundaries, empty local work, sanitized aliases and
cancellation. The full Core suite passed **962 tests** (2m 1s).

An independent read-only native-ledger calculation verifies all **7,038** attempted
workload totals, token counts and category-integrity results across the four reports,
plus 628 construction partitions and 1,256 summary rows. It recomputes headroom,
movement, workload statistics, non-overlap/blocks and availability counts within
the exported frame. It does not independently establish that collection is complete,
reconstruct every quota eligibility decision, or identify latent crossing/cost.

See the [read-only commands](../DEVELOPMENT.md#ceiling-selection-and-censoring-research).
Audit output contains aliased numerical/time data, not raw account/session/source
IDs, paths, credentials or source content. Review usage patterns before sharing.
Evidence files are in `.codex/temp/stage7/censoring/`:

| Study | Native audit SHA-256 | Independent proof SHA-256 |
|---|---|---|
| Recent reconstructed | `cfa65eb23043b4c22d88e40a82569316205ba8ebd66a5ba7f5544a25b0ba5391` | `3add54152c954269fca3975a742827ab6cf832fbe54cca337c1052cee036bd4b` |
| Recent collection-qualified | `916c5417410dbf163b7558e4975a3ae7a826b3e7010334830a5e64508b78caca` | `0e2a1d7ce2ecf26e34748ed46f8a9d55bca2dfd1ebf316d7a9db0b30c573ce1c` |
| Month reconstructed | `c3dce947fb0dfaa1c2df2fcfd5277b7a56f145d113150c1de9a798eec80e7e14` | `e8f4670b662c4beeab20dfb312ad687262156647b841529b35fcee38dfef7508` |
| Month collection-qualified | `bcf5838e361633db1b7ddba203b8bb257ca2cb2b07388a49c19b0524af7c65ab` | `1a5e80c70f56febad3ef30d702140a344cbbb2318c0f45e0c1b290306c714a46` |

Host presentation and resource acceptance remain separate checklist gates. This
explicit research slice adds no UI behavior or physical-crossing claim.

The Windows build passed with zero warnings/errors (54s), installed normally and
restarted PID **47184**. Backup: `20261029T090142442Z-193b2976`. Pre-commit build
identity: `1.0.0+bc177d1dfa855b5d703eda184c010e6ff8e4679b.dirty`.
The installed month-qualified and recent-reconstructed audits match byte-for-byte;
installed assemblies match the normal publish output:

- Core: `c431f04f092ce82179749f72d10aca862409b167bedc1397edafb1e8c561c55d`
- Infrastructure: `320465f4b069eee2f8cbe76a1ff33b296d0cdd3721b358303bb78148f80df6a7`
- App: `f5131e7314a9527de92dddcf8cfd9bfd8c1b5352b493791fb4d22a8fba4eb1af`
- ForecastEvaluation: `3a4999facae5149cbeb890130fd3581b3e8728a89367dd9e92311499c5c319c2`

After restart, quota collection advanced to 09:14:08 UTC and forecast confirmation
to 09:14:09 UTC. Presentation/resource acceptance remains separate.
