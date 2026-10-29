# Coherent allowance-threshold paths (NEXT-002)

## Decision

Keep coherent threshold calculations in explicit research. The arithmetic now
propagates each shared workload path through both windows, preserves no-crossing
and unresolved mass, and reports timing conditional on a known crossing. It does
not earn a probability label or change live forecasts/alerts.

The month study supplies 18 conditional cases per replay mode. Every fitted window
map fails the existing frozen-fit training-envelope check, so these are sensitivity
calculations under declared maps, not identified quota costs. The recent range has
no aligned two-window pairs. Supported future reset capacities and chronological
threshold-probability validation are also absent. These are evidence limits, not
reasons to infer another window, invent a refill, or add a more complex predictor.

This closes the current mechanical evaluation/deferral decision. A product
probability needs supported paired inputs/cost calibration, appropriate outcome
and censoring semantics, earlier-only predictive validation and a reset-capacity
contract before extending across resets. NEXT-003/004 examine related evidence
limits without changing the frozen TT ruler.

## Shared-path and reset contract

`coherent-quota-paths/v1` consumes one existing joint workload ensemble and two
nonnegative additive frozen cost maps. Each immutable five-minute joint step feeds
**both** maps. For a native case, one two-hour-context/two-hour ensemble supplies
all 5/15/30/120-minute prefixes; neither window nor horizon draws another future.
The existing sampler owns 64 paths, source bins, up-to-three-bin contiguous blocks,
seed and revision. Empty recorded bins are local pauses, not verified idleness.

For each path, find the first crossing bin jointly, then summarize that path-level
result. Do not take minima of marginal window quantiles. Two windows can each have
large non-crossing mass while every shared path crosses one of them; the synthetic
anti-correlated case tests this explicitly.

Outputs partition the original ensemble into crossing, no crossing through the
target, and unresolved paths. These shares always retain the original denominator;
unresolved paths are not silently discarded. P10/P50/P90 timing uses only known
crossing paths and is explicitly the **crossing-conditional bin-end time**. Per-path
lower/upper bin endpoints and candidate windows retain coarse timing and within-bin
ordering uncertainty. This is not a time quantile over paths that never cross.

Cost is allocated uniformly when a reset or requested target splits a step. This is
a declared within-bin assumption, not observed sub-bin timing. A zero current
budget crosses at origin. Negative/nonfinite cost ends that path's support rather
than becoming zero consumption. Requested path prefixes must be contiguous and
quantity-complete; missing steps are not pauses.

A reported reset is a support boundary. Native cases provide **no** future refill.
If a crossing has not been established before an unsupported boundary, the result
is unresolved, including a boundary exactly at the requested target. Exact old-budget
exhaustion at reset/support termination is an unresolved ordering tie. A known
earlier crossing remains known; future uncertainty cannot erase it.

The pure arithmetic also accepts explicit numeric reset scenarios: ordered reset
times, next support boundaries, budgets and declaration times no later than origin.
The caller supplies the budget; it never defaults to 100%. Such declarations are
scenario assumptions, not evidence that the provider will supply that capacity.
Synthetic tests split a shared step at reset, replace rather than carry the old
budget, preserve known earlier crossings and reject future-declared schedules.

Allowance threshold is separate from execution stop, purchases, grace and whether
the assumed future work happens. Workload-path spread omits cost-map and activity
uncertainty; its empirical path fraction is not a calibrated exhaustion probability.

## Native pairing and fixed studies

`coherent-quota-path-study/v1` pairs exactly aligned eligible provider-authoritative
windows within the same reported account/source/profile/plan/bucket. Account identity
must be known. Incompatible/absent windows remain absent. No temporal matching or
other account substitutes a missing anchor.

`Lane` is the provider response slot, **not** a cross-window meter identity. The
[provider parser](../../src/TajsTokens.Infrastructure/Providers/CodexAppServerQuotaProvider.cs)
sets it to `primary`/`secondary` and resolves window kind by reported duration.
Installed retained data corroborates FiveHour/primary with Weekly/secondary, and
later Weekly/primary without the short window. Pairing uses semantic kinds and
reported bucket scope; each fitted window retains its slot and actual duration.
A regression prevents requiring equal slots or breaking when Weekly changes slot.

Each window map reuses the existing first 20 earlier matured 30-minute same-cohort
joint/category cost intervals. Vocabulary/scaling are training-only. No actual
future workload or threshold label tunes the map. Collection-qualified replay
uses known collection clocks and timely training evidence; reconstruction remains
separate. Unknown/negative/over-five-minute quota collection lag creates a boundary.
These are current-code replays, not issued forecasts or account-attributed local work.

At most 32 evenly spaced training-ready origins are examined, with sampling and
path-context refusals reported separately. Both replay modes give these counts:

| Fixed target range (UTC) | Reported scope/clocks | Aligned pairs | Training-ready | Examined | Usable path cases |
|---|---:|---:|---:|---:|---:|
| October 22 00:00–October 28 16:40 | 1,713 | 0 | 0 | 0 | 0 |
| September 27 00:00–October 27 14:58 | 7,951 | 1,430 | 473 | 32 | 18 |

The month range has 6,521 unmatched/ambiguous scope/clocks, 957 aligned origins
without enough mature paired training, and 14 sampled origins without a usable
joint path context. The remaining 441 training-ready origins are outside the
explicit 32-origin sample; they are not counted as model abstentions.

The same 18 month cases produce the following mechanical counts in either mode:

| Horizon | Conditional paths | Crossing paths | No-crossing paths | Unresolved |
|---|---:|---:|---:|---:|
| 5 minutes | 1,152 | 0 | 1,152 | 0 |
| 15 minutes | 1,152 | 5 | 1,147 | 0 |
| 30 minutes | 1,152 | 19 | 1,133 | 0 |
| 120 minutes | 1,152 | 355 | 797 | 0 |

These are repeated simulated paths under conditional cases, **not measured
independent threshold outcomes**. All 36 window fits per mode have an
outside-training-envelope flag; maximum violations range from 3pp to 26pp. A
regularized optimum outside an envelope does not prove the envelope constraints
are infeasible. Neither these point maps nor the path counts establish identified
account costs or predictive calibration.

The nearest sampled reported reset is about 137.13 minutes after origin, beyond
the two-hour horizon. Thus native data here does not validate post-reset propagation
or reset-order handling; those have synthetic arithmetic coverage only. No future
capacity is borrowed from later observations.

## Validation and reproduction

Nine regressions cover shared joint inputs, the marginal-quantile counterexample,
retained no-crossing mass, conditional timing, unsupported/explicit resets,
boundary ties, missing paths, invalid costs, future assumptions/prefix changes,
cancellation, source-slot pairing and mature/available/same-account training.
The full Core suite passed **956 tests** (2m).

Independent Python arithmetic reproduces all 9,216 path/horizon outcomes, 72,334
step calculations and 22,988 shared-prefix checks across the two month modes, plus
availability partitions for all four studies. This independently checks propagation
given exported numerical costs, not the validity/identification of those fitted maps
or the original stochastic sampler. Those remain separate owners/evidence.

Use the [read-only commands](../DEVELOPMENT.md#coherent-threshold-path-research).
Audit output contains aliased numeric/time/cost evidence, with no raw account,
session or source IDs, paths, credentials or content. Review usage-pattern data
before sharing. Frozen local evidence is in `.codex/temp/stage7/thresholds/`.

| Study | Native audit SHA-256 | Independent proof SHA-256 |
|---|---|---|
| Recent reconstructed | `bbe9f5385d444614717be29a7ce38297495aa1d38fbf332d19f46e940d9e4200` | `95f3cb3e9686e6e135aecbd0557cd949927278ea2babcec2ed0f6889d4ef1641` |
| Recent collection-qualified | `3709b3adbfb8efea72539a64e65acbb075c0837f9b4bf0ac0a8db303ea338a2c` | `4f360649568d30aac4b62de783bd749a27e92a02da34eed181c2e9401bab07c4` |
| Month reconstructed | `2056f8ee136c3710703f65a034e8b6595f11f9e47a4996ad5b942a88bbce483a` | `79bcf59c5b6d4fbb5d5c961971f6df0592d481502537c1d82a23e7d3f4f80d01` |
| Month collection-qualified | `c70e381a2d8eba431a0a325cf05c4da5dad543f21a8c4a7a7a349fb279ee1c92` | `e8cc7671fb84d39c628b688a658173897ac0c064d3f23c06b01b72ac08aac0cb` |

The Windows build passed with zero warnings/errors (54s), installed normally and
restarted PID **22800**. Backup: `20261029T083010926Z-cf899d6e`. Pre-commit build
identity: `1.0.0+bb83b5ac3a3e42bf5cf3e7d6c60339ca3d3ff58b.dirty`.
The installed month-qualified and recent-reconstructed audits match byte-for-byte;
installed assemblies match the normal publish output:

- Core: `908214edc9a699faa8913df53cddbf38656a9f41b713d7ad5a932fdf01c14fa1`
- Infrastructure: `b0d9ac3c6802897e1bdc669e38191df97c9321a7d5414f50f6a4fed32ec60903`
- App: `4369e17ea4508fc0172d00101c50dfb893417c1f91f0b066df27a452ee20dd1d`
- ForecastEvaluation: `49b4d46f3d0709711ff680deab13769c9c7534804a625fa230dcf4dd43adf2a1`

No durable schema, acquisition, TT weights, live model, alert or UI behavior changed.
After restart, quota collection advanced to 08:35:00 UTC and forecast confirmation
to 08:35:02 UTC.
Host presentation/resource checks remain open in PROJECT.md.
