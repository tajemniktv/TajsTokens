# Full-roadmap requirement audit — 2026-09-18

Reference: user-requested .codex/ROADMAP.md; original objective preserved. This is an evidence
audit, not an alternate product spec. PROJECT.md owns implementation status.

| Roadmap requirement | Current implementation / proof |
| --- | --- |
| §§1–2 local retained-data learning, native source contracts | Existing SqliteForecastDatasetReader read-only transaction reused; no acquisition/schema/retention changes. Both CLI runs completed on retained telemetry.db. |
| §3 official analytics complementary; optional asar inspection | Cost and end-to-end diagnostics complement analytics. Asar investigation is optional research, not required acquisition. No new source dependency. |
| §§4–5 interval workload/categories/model/effort/activity/context/runtime | QuotaCostObservationBuilder; five categories and existing source-native feature builder; TTFT supplied only from supported completed observations. Native generation throughput remains unavailable without a source-backed token-to-turn join and is not fabricated. |
| §5 horizons/alignment/cohorts/resets | Disjoint 30m/2h windows, (start,end] alignment, conflicts/reset drops/jitter and incompatible transitions excluded; QuotaCostEvaluationTests. |
| §6 censored targets | Nonnegative interval-distance ridge; exact app-server conversion envelope versus explicit unverified rollout sensitivity; equal meters retain uncertainty; saturation excluded. Tests cover flat/cached/saturated values. |
| §7 ladder/regularization/ablations | Persistence, pace, total, categories, model/effort, separate context/activity/TTFT/time candidates. Frozen training vocabulary/scales and nonnegative weights; no future oracle features used by production. |
| §8 unexplained movement | Per-trial signed residual + lower-envelope unexplained positive component and UI residual quantiles; association, not causality. |
| §9 distinct cost and end-to-end errors | ComposedQuotaEvaluator evaluates actual cost and predicted-composition cost on same outcomes; incumbent and legacy pace paired. Future-work injection regression changes oracle result but not forecast. |
| §§10–11 frozen residual regimes/versioned evidence | v1 report/trial series, three-generation frozen reference and two-generation replicated-shift threshold; no automatic notification/retraining; shifts inhibit live promotion. |
| §§12–13 TT and prohibited operations | No TT unit, prices, prompts, probes, external telemetry, crowdsourcing or provider-change notifications. Ownership assertion recorded separately from native account IDs. |
| §14 slice 1 | Dataset implemented and tested. |
| §14 slices 2–3 | Baselines/ablations implemented; historical read-only evaluation completed and source/cohort-separated. |
| §14 slice 4 | Derived versioned residual series and conservative shift evidence implemented. |
| §14 slice 5 | Scalar token predictor now carries projected composition; exact-origin evaluator and live ComposedQuotaPolicy preserve incumbent unless cost/e2e/independent-reset/freshness/support gates pass; ranges via previous completed generations. |
| §14 slice 6 | Explicit chronological old-weights, scale-only transfer, local-only comparison implemented, CLI/UI exposed, synthetic chronology/account/heldout independence test passed. Current data has no compatible native-account-linked regimes; no empirical transfer or TT claim. |
| §15 reliability | No future inputs; disjoint outcomes; shared bounded reset grouping; material paired improvement; native account/cohort isolation; stale/missing/unseen fallback; no calibrated probabilities. Gate tests reject repeated-polling pseudo-generations, stale evidence, missing comparator and immaterial gain; source also rejects future outcomes. |
| §16 immediate experiment | Completed in 4e9889f, extended rather than substituted for the full objective. |
| §17 references | Existing source and policy owners retained; no claims of newly verified external nerf-checker implementation. |

Empirical end-to-end: 24 targets /2 reset generations; total 1.5773pp, categories 1.4774pp,
model/effort 1.6465pp interval loss; legacy pace 1.2757pp, incumbent 1.5793pp. Four targets
missing composition. No model qualifies for live promotion. Transfer: no linked regimes.

Validation run: exec session 9498 exited 0. All 356 Core tests passed; Windows Debug build passed
with zero warnings/errors. Dogfood build 20260918T093836657Z-92e92959 installed/restarted and
acknowledged readiness (PID 35856). Native visual acceptance is separate and not claimed.
Final staged diff excludes user's schema-document move and pre-existing untracked .codex material.
Data gates correctly leave live forecasts on the incumbent: empirical promotion/TT are conditional,
not unimplemented paths or invented successes. No required unconditional roadmap code remains.
