# Layered statistical models and a frozen TT basis

User-supplied design direction, recorded 2026-09-19. This is a structured consolidation
of the recommendation supplied in conversation, not a verbatim transcript or a claim that
the proposed candidates have won evaluation. [PROJECT.md](../../PROJECT.md) remains the product authority;
this note preserves the rationale and candidate contracts. No collection or release change
is authorized merely by a field appearing in this proposal.

## Direction

Build a small, layered statistical system rather than one enormous quota predictor.
Keep TT as an experimental, versioned normalized workload score, separate from both
quota calibration and prediction of future activity.

| Job | Recommended candidate |
|---|---|
| Explain quota cost of completed work | Nonnegative regularized regression, beginning with raw tokens and disjoint token categories |
| Predict upcoming workload | Activity-aware recent-rate baselines, then separate any-work and positive-amount models |
| Predict composition | Recent model/effort/tier and category mix, falling back toward longer history |
| Forecast quota | Horizon-specific competition between direct quota forecasts and workload-to-cost composition |
| Detect changed accounting | Errors from a frozen reference model, separate from explicit account/plan/report changes |
| Calculate TT | Frozen interpretable additive scoring function and documented reference unit |
| Convert TT to quota | Separate account/window/regime calibration, only where scalar conversion is adequate |

These are candidates, not a universal winner. The user's report of mostly Astra Light on Pro
is context, not a newly verified corpus measurement. A result from 44 half-hour outcomes
favoring raw tokens over API-price weighting does not establish cross-model weights.
Older plan and model mix changes are confounded: more rows alone do not separate plan,
model, policy and composition effects. Seek overlapping conditions or label assumptions.
Five-minute quota targets can be difficult because of bursty reporting and coarse meters;
short-horizon predictability is a hypothesis to test at that horizon.

## 1. Explain completed-work cost

For a compatible account/window/regime, start with:

```text
predicted quota movement = sum(beta[k] * observed workload[k]), beta[k] >= 0
```

Keep three nested candidates:

1. One raw-total-token coefficient, permanently retained as a competitor.
2. Separate uncached input, cached input, supported cache-write and output-category weights.
3. Model-aware category weights; effort/tier departures only with adequate compatible support.

Strong regularization should keep sparse groups near the simpler pooled model. Partial pooling
is a possible later approach, not a mandatory immediate framework. Two observations cannot
justify a precise model-specific coefficient.

Categories must be disjoint. If reasoning is a subset of output, use either total output or
reasoning plus non-reasoning output, not both total and subset. Higher effort may already act
through measured reasoning tokens; an extra multiplier must explain residual variation.
Context, compaction and subagents may affect workload volume rather than independent token cost.

Keep separate target contracts for current-window movement, daily provider-relative usage,
historical-period usage and genuine credit quantities. Daily `percent` is not automatically
weekly percentage points. Historical-period and current-full-allowance labels are not equivalent.

Preserve meter uncertainty: unchanged displayed percentages are not exact zero usage.
Interpret observed movement as locally explained work plus unobserved account activity,
accounting mismatch and reporting effects. Flag or exclude inadequate-coverage intervals from
strict calibration rather than assigning a large account jump to a tiny local workload.

## 2. Predict activity and amount separately

Workload modelling can use rollout history without requiring quota labels for those same rows.
First compare activity-aware recent-rate windows and exponential averages chronologically.
Running work, recent token events and observed descendants support continuation; completion and
idle gaps should reduce it. Stale telemetry means unknown activity, not idle. No local activity
proves nothing about another device or surface.

Then test a hurdle model:

```text
E[X_h | evidence] = P(X_h > 0 | evidence) * E[X_h | X_h > 0, evidence]
```

The probability concerns any recorded work during the interval, not activity exactly at its
endpoint. Candidate activity model: regularized logistic regression using supported running
state, last-event age, idle gaps, descendant activity and session duration. Candidate positive
amount model: recent rate first, then a small log-link positive regression (such as Gamma) if
held-out results improve. These suggestions do not override existing source/retention decisions.

Retain predicted volume and composition: model, effort/tier where observed, cached/uncached
input and output. Begin with current/recent configuration and shrink toward longer local history.
Unknown tier stays unknown. Preserve joint relationships when estimating ranges; independently
combining high volume, expensive model and high output-share bounds can fabricate a worst case.

## 3. Let quota routes compete

Both routes are legitimate:

```text
recent quota -> direct quota forecast
activity -> future workload distribution -> cost model -> quota forecast
```

Evaluate 5, 15, 30 and 120 minutes, and reset-oriented outlooks where supported; the existing
60-minute session experiment is additional evidence, not a substitute for those horizons.
On the same eligible outcomes compare no-additional-usage/persistence, the incumbent, and the
composed challenger. Promotion is horizon-specific. A simple blend can be a later candidate,
not a complex weighting system fitted to a tiny strict sample.

Separate expected outlooks from conditional plans. “Continue this work for two hours” assumes
the work happens; its range does not include the probability that the scenario never occurs.
Reset-horizon pace is a reference projection. Long horizons are not inherently impossible:
daily totals may be more predictable than burst timing, but that also needs evidence.

## 4. TT: a frozen ruler, not a quota wallet

**A TT is a normalized workload unit calculated by a specified TajsTokens scoring model.
It is not a raw token, purchased credit, monetary balance or quota percentage.**

For scoring basis v:

```text
S_v(x) = sum(w_v[k] * x[k]), w_v[k] >= 0
TT_v(W) = sum(S_v(x_i), i in W) / S_v(reference workload)
```

The reference workload is worth 1 TT. Define a fixed supported basket, including relevant
token mix/model/effort/tier/context conditions, then freeze it. Unit size is a convention;
relative weights are empirical. Multiplying every score by ten and dividing conversion by
ten gives identical quota predictions: data does not identify the ruler's markings.

### Basis identity and historical meaning

A content-addressed `basis_id` must cover exact coefficients, reference basket, input semantics
and supported dimensions. A materially changed scoring function is a new basis. Retain the
distinction between originally scored work and a restatement under a different basis; do not
silently restate original forecasts during evaluation.

If work scored 120 TT under basis v1, a provider allowance change must not alter that score.
Update quota calibration separately. Retraining the ruler in response to policy changes would
hide the change that historical comparisons are supposed to expose.

### Conversion and scalar limitations

Where validated:

```text
predicted quota movement[g] = alpha[g] * TT_v
g = compatible account + allowance window + policy regime
```

Illustrative only: 50 TT at 0.04 pp/TT gives 2 pp on one window; at 0.01 pp/TT it gives
0.5 pp on another. These are not OpenAI rates or proposed fitted constants. Never infer
“the subscription contains 10,000 TT.” Remaining capacity would need the qualification
“at this workload mix and current calibration.”

A scalar works as a complete intermediary only when cost policies approximately differ by
scale. If Astra/Luna cost ratios are 2:1 for one window and 8:1 for another, one frozen ratio
cannot represent both through a scalar conversion. Keep full-vector-to-quota prediction;
TT must not become an information bottleneck. Compare scalar conversion against full features.

Locally learned bases are not cross-user comparable. Start with an experimental local index
whose basis is inspectable. Cross-installation comparison requires the same basis and compatible
accounting semantics. A future shared basis need not upload telemetry but still needs validation.
Unsupported models require partial coverage or explicitly provisional output, never silent zeros.

Observed TT comes from observed workload; predicted TT comes from predicted workload. TT needs
no independent forecasting algorithm. A raw-token score divided by a constant is an acceptable
explicitly simple index, but adds no predictive information.

## 5. Drift: monitor the relationship without redefining the ruler

Retain a frozen reference cost model and its residual history, including after adaptive retraining.
Separate workload changes (model/effort/context/tier), reporting changes (lag/revision/coverage/
precision/source), and possible accounting changes under otherwise comparable conditions.
Start with smoothed residual diagnostics and conservative thresholds. Resets and reporting
changes are not automatic provider-policy changes; elaborate automated change-point machinery
is not the first requirement.

## 6. Validation and bounded next work

First-20-target frozen fits are useful reference experiments, not necessarily the permanent
production fitting policy. Test adaptive candidates with rolling origins, not shuffled rows.
September 1 work imported September 18 can train a September 19 forecast, but cannot count as
information available September 1. Outcome labels are naturally learned later: do not require
the future answer to exist at prediction time. Preserve explicit label-availability semantics.

Report errors by horizon, activity state, composition, coverage and reset groups. Separate
cost-only and end-to-end evaluation. With coarse short-horizon meters, zero predictions may
often lie inside an uncertainty envelope; also assess the zero-use baseline, accumulated bias,
missed high-consumption intervals, range coverage and width. Low envelope loss alone is not utility.

Empirical ranges need honest provisional labels. A library's two numbers do not establish
calibrated confidence. Adaptive conformal methods are a possible later layer, not a guarantee
of conditional coverage in a seventeen-outcome sample.

The proposed implementation sequence, to be tracked in PROJECT.md rather than as a second roadmap:

1. Expose evidence quality: reconciliation ambiguity, nullable tier, target semantics,
   composition and collection-time provenance.
2. Evaluate the small cost ladder: raw tokens, regularized categories, restrained model/effort/
   tier challengers; keep daily-relative and quota-window targets separate.
3. Evaluate activity-aware nowcasting and two-part workload models against the incumbent by horizon.
4. Prototype TT in Model Lab with a frozen reference basket, exact basis identity, coverage and
   scalar-versus-vector accuracy checks. Do not claim a universal scale or automatic promotion.

No synthetic Codex prompts, personality prediction or deep-learning service is needed.

## 7. Consolidation of the earlier quota-cost roadmap

The local `.codex/roadmap.md` (2026-09-18, "Quota-Cost Modeling & Regime Detection") is
historical rationale, not a second active plan. Its immediate calibration task has been
implemented. PROJECT.md's **Current work** owns subsequent priorities and acceptance;
the [evidence review](CODEX_EVIDENCE_REPO_REVIEW.md) owns dated source findings and experiments.
This section preserves the useful decisions without requiring that ignored local artifact.

Retain the distinction between actual-workload cost explanation and origin-only future-workload
forecasting. Build compatible, non-overlapping interval observations with source/account/reset
provenance and precision-aware quota targets. Local unexplained movement may reflect missing
coverage, delayed accounting or model error; it is not causal attribution to another device
or provider policy. Context, compaction, subagents, runtime and time-of-week features are
separate ablations, not assumed extra prices. In particular, TTFT or time of day cannot by
themselves establish provider load. Keep frozen definitions, repeated observations, noise
baselines and residual history rather than adapting away suspected changes.

The earlier roadmap's six slices now map to existing owners:

| Original slice | Current implementation / boundary |
|---|---|
| Derived interval dataset | `QuotaCostObservationBuilder`; compatible non-overlapping targets, not raw polling-row regression |
| Baseline cost models | `QuotaCostEvaluation`; pace, total, categories and model/effort candidates, not automatic promotion |
| Feature ablations | Context/activity/runtime/time candidates in the same evaluator; inclusion is not proof of benefit |
| Residual/regime diagnostics | Frozen fit and replicated residual-shift diagnostics; explicit semantic changes remain separately owned by `CodexEvidenceDrift` |
| End-to-end outlook | `ComposedQuotaEvaluator` and `SessionQuotaEvaluator`; workload uncertainty and collection-time eligibility remain separate from oracle cost accuracy |
| Transfer / TT research | `TtEvaluator`, immutable `TtWorkloadBasis` and aggregate research snapshots; empirical transfer and per-task original scoring history are not established |

Three older restrictions need qualification, not repetition:

1. A frozen local TT score can compare workload without validated cross-regime scalar transfer.
   Transfer validation is required for the corresponding quota-conversion claim, not for the
   mathematical existence of an explicitly experimental index. Preserve the full workload vector.
2. API-price weighting is now permitted as a versioned counterfactual competitor, not a bill,
   native credit quantity or provider metering contract.
3. Interval loss alone is not "primary truth." Zero-use comparisons, signed/accumulated error,
   coverage, range width and independent-cycle evidence are necessary complementary checks.

The old Desktop analytics inspection suggestion has been investigated; see
[Desktop analytics acquisition](CODEX_DESKTOP_ANALYTICS.md). Do not repeat that investigation
without a changed runtime or a new contract question. The old active-benchmark/"nerf checker"
ideas remain methodological background only: no synthetic prompts, hidden-runtime probing,
crowdsourcing, telemetry upload or causal "nerf" claim is introduced by this consolidation.
External-project descriptions in the old artifact are dated observations, not newly verified
claims about those projects today.

## User-supplied references

Retained as supplied supporting reading; not independently reviewed while saving this proposal.

1. [Time-series cross-validation](https://otexts.com/fpp3/tscv.html)
2. [Scikit-learn linear models](https://scikit-learn.org/stable/modules/linear_model.html)
3. [Stan finite mixtures and hurdle models](https://mc-stan.org/docs/stan-users-guide/finite-mixtures.html)
4. [Forecast combinations](https://otexts.com/fpp3/combinations.html)
5. [Forecasting with regression and scenarios](https://otexts.com/fpp3/forecasting-regression.html)
6. [Adaptive Conformal Inference Under Distribution Shift](https://proceedings.neurips.cc/paper/2021/hash/0d441de75945e5acbc865406fc9a2559-Abstract.html)
