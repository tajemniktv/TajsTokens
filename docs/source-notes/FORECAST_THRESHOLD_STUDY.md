# Forecast threshold study

## Conclusion

Keep released thresholds unchanged. This retained-data study did not establish a consistent later-period improvement that also improves useful-answer availability. It supports keeping conditional pace answers visible while treating bands and challenger promotion as separate evidence decisions.

The largest weekly cohort had complete point-answer availability on its final candidate origins. Halving the promotion margin slightly improved development MAE, then worsened final 30-minute MAE and RMSE. Doubling the margin left final two-hour point error unchanged, narrowed bands and reduced observed coverage. Smaller cohorts and reset-horizon labels remain too sparse for a policy recommendation.

## Reproduction and boundaries

- Study version: `forecast-threshold-study/v1`; 17 one-factor configurations plus persistence and simple-pace baselines.
- Requested UTC range: `2026-09-27T00:00:00Z` to `2026-10-27T14:58:00Z`.
- Dataset captured: `2026-10-27T15:13:14.2551753+00:00`.
- Content identity: `500ca52ceb7dd9507629327b8ab44e88d92e11bcc96eb63e55979329edb2f5b7`.
- Read-only app-owned telemetry; collection-time replay; no user ownership assertions supplied. Known account/source/plan/limit/window cohorts remain separate and are represented by aliases.
- The final 20% of candidate origins is reserved for comparison. Configuration choice uses earlier paired errors with outcomes matured before the split and requires no loss of point availability. Twenty non-overlapping final outcomes is a reporting convenience for identifying sparse comparisons, not a production rule or significance guarantee.
- The largest weekly cohort's final period begins at `2026-10-16T15:09:52.1742306+00:00`. Other split boundaries and all configurations are retained in local aggregate JSON at `.codex/docs/forecast-threshold-study.json` (ignored). Regenerate with the command in [development](../DEVELOPMENT.md#forecast-threshold-study).
- Simple pace means the existing `time-ewma-2h` fallback. Errors and band widths are percentage points of the corresponding allowance. No universal workload or token-to-quota currency is introduced.

## Final-period current-policy results

Point and band counts share the candidate-origin denominator. Paired outcomes require an observed matching target; non-overlapping support is reported separately. Near-reset outcomes are within-five-minutes proxies, not exact reset truth.

| Cohort/window | Target | Points / candidates | Bands | Paired / non-overlapping | MAE | Simple-pace MAE | Band coverage / mean width |
|---|---|---:|---:|---:|---:|---:|---|
| cohort-01 / 5-hour | 0.5h | 8/8 | 8 | 6/6 | 5.972 | 5.218 | 83.3% / 20.095 |
| cohort-01 / 5-hour | 2h | 6/6 | 0 | 1/1 | 50.777 | 43.761 | unavailable |
| cohort-01 / 5-hour | near reset | 8/8 | 0 | 2/1 | 3.836 | 2.470 | unavailable |
| cohort-02 / weekly | 0.5h | 116/116 | 116 | 78/78 | 0.340 | 0.338 | 88.5% / 1.386 |
| cohort-02 / weekly | 2h | 116/116 | 116 | 59/20 | 0.963 | 0.963 | 89.8% / 4.439 |
| cohort-02 / weekly | 24h | 116/116 | 0 | 7/1 | 3.601 | 31.988 | unavailable |
| cohort-02 / weekly | near reset | 115/116 | 0 | 0/0 | unavailable | unavailable | unavailable |
| cohort-03 / weekly | 0.5h | 10/10 | 10 | 8/8 | 1.000 | 1.327 | 87.5% / 3.316 |
| cohort-03 / weekly | 2h | 10/10 | 0 | 5/2 | 5.684 | 3.667 | unavailable |
| cohort-03 / weekly | 24h | 10/10 | 0 | 0/0 | unavailable | unavailable | unavailable |
| cohort-03 / weekly | near reset | 9/10 | 0 | 0/0 | unavailable | unavailable | unavailable |

## Threshold tradeoffs

| Weekly cohort-02 comparison | Current | Alternative | Interpretation |
|---|---:|---:|---|
| 30-minute final MAE, margin halved | 0.340 | 0.420 | Development winner lost on later observations. |
| 30-minute final RMSE, margin halved | 0.609 | 1.152 | Larger tail errors despite unchanged answer availability. |
| 2-hour final MAE, margin doubled | 0.963 | 0.963 | No later point-error improvement. |
| 2-hour final band coverage, margin doubled | 89.8% | 86.4% | Narrower bands: 4.439 to 3.776 points; a tradeoff. |
| 30-minute development band availability, 20-error floor halved | 440/461 | 450/461 | Ten earlier band answers; final availability and errors unchanged. |

The 30-minute final comparison has 78 non-overlapping outcomes across 11 provisional blocks; candidate origins span eight days and five reset identities. The two-hour comparison has 59 raw matched outcomes but only 20 non-overlapping outcomes. Block spacing does not prove independence. The 24-hour result has seven matched outcomes and only one non-overlapping outcome, so its apparent advantage is inconclusive.

## Scenario and composed limitations

- Neither the default nor tested alternatives produced supported final session scenarios in these cohorts. In the largest weekly cohort, 67/116 candidate origins lacked usable origin workload and 49/116 requested session shapes were outside observed support. Lower count floors do not establish a response curve outside observed support.
- Composed selection produced no supported final points across six separate cohort/horizon groups. This is a conservative replay lower bound on constructed targets, not a measured live abstention rate or proof that another threshold would make a safe forecast.
- Reset outlooks still produced conditional point answers while reset bands remained absent. The largest weekly cohort has no matching final near-reset labels; within-cycle fixed-horizon evidence cannot validate those bands.
- Source/identity/unit compatibility, freshness, overlap rejection, collection-time availability and assertion-specific safeguards were not tuned. No experimental configuration was promoted.

A later policy decision needs confirmation on additional compatible later observations and should inspect error, answer availability and interval behavior together. Current data does not justify automatically lowering every barrier.

## Eligibility follow-up (EVID-003)

The `forecast-eligibility/v1` diagnostic reuses the threshold study's origin
selection, split and target lookup, the existing feature builder and the production
scenario history selector. The full study's diagnostic version is now
`forecast-threshold-study/v2-eligibility`; the forecast rules and thresholds are
unchanged. Scenario failures in the product identify unsupported duration/root/
subagent coordinates and show compatible-history counts. Structured report reasons
remain in the explicit local diagnostic, rather than exposing machine labels in
the normal Planning flow.

The follow-up reads the frozen app-owned numeric database at
`.codex/temp/stage1/worker-fixture/telemetry.db`, using the original requested UTC
range above. Its dataset identity is
`d1ae955775835120e6470aee564962dd1deed3afc027fad5fc50d2d284365972`.
This is a later retained snapshot, **not the original v1 dataset identity**. It
reproduces the largest weekly cohort's 116 final candidate origins, 67 workload
exclusions and 49 support exclusions for each of the two scenario horizons. This
classification belongs to that explicitly identified follow-up, not a claim of
byte-identical historical replay.

The installed executable reproduced this report at `2026-10-28T08:06:54.9970127Z`;
the final period begins at `2026-10-16T15:09:52.1742306Z`, matching the original
study's reported split. The new fixture identity is still stated separately.

| Largest weekly final cohort, each scenario horizon | Origins | Meaning |
|---|---:|---|
| Candidate origins | 116 | Denominator for 0.5h and 1h separately; do not add them. |
| No retained dated token events in the two-hour origin lookback | 63 | Does not prove idleness, complete source discovery or absence of untimed evidence. |
| Retained events exist, but all were collected after the origin | 4 | Valid retained amounts, unavailable for that historical prediction. |
| Unknown collection time among those rejected origins | 0 | Explicitly separate from later collection. |
| Unknown-role-only exclusions | 0 | The old combined label did not identify a role problem in these 67 cases. |
| Timely inputs and required roles pass | 49 | All also pass compatible-training count/freshness selection. |
| Duration outside observed training support | 49 | No root/subagent count rejection was emitted at this stage. |
| Supported scenario point answers | 0 | Unchanged from the retained study. |

`no-timely-token-events` and `no-classified-token-sessions` each occur 67 times;
they describe the same origins and must not be added. One sanitized late-collection
example contains 336 retained events and zero timely events. The first 30-minute
support example has 92 matching intervals ranging from 0.5003742483 to
0.6329527891 hours; its shortest interval exceeds the request by about 1.35 seconds.
That demonstrates sensitivity to the strict observed-duration boundary, not proof
that extrapolation or a new tolerance is valid. The corresponding one-hour request
exceeds the upper bound. These findings inform FORE-003/005; no support boundary
was relaxed to manufacture answers.

Outcome availability is a parallel branch: 78/116 half-hour and 91/116 one-hour
candidate origins have matching final outcomes, even though scenario points remain
unsupported. Missing labels do not become workload-input failures. Bands and paired
accuracy retain their own denominators. Training-selection counts use interval/origin
pairs because one earlier interval may be assessed at many origins; these are not
independent observations.

The report also exposes quota collection/history/account stages, model/effort
matching when explicitly requested, overlapping training exclusions and bounded
numeric examples without source/account/session identifiers. Historical discovery
and decoding denominators are **unknown/null**, rather than inferred from retained
rows. Their independent implementation/runtime evidence remains under ING-002 in
[Stage 2 validation](STAGE2_EVIDENCE_VALIDATION.md#compressed-rollout-ingestion-ing-002).
The retained-reader boundary also excludes untimed rows from this forecast report;
their amounts and all-time presentation remain EVID-004. Model/effort are unconstrained
in these two study requests, so missing labels do not veto them. No current login
is used to assign local work to a quota account.

Reproduce with `--eligibility` in [development](../DEVELOPMENT.md#forecast-threshold-study).
Local aggregate JSON and validation logs are under `.codex/temp/stage2/eligibility-*`.

Validation passed: **762 Core tests** (1m49s), including direct original-selector
parity, study/report denominator and point/band/paired-outcome agreement, late versus
unknown collection, overlapping reasons, privacy and unconstrained versus explicit
model/effort requests. The Windows build passed with zero warnings/errors (59.63s),
installed the daily build and restarted PID 31304. Installed Core SHA-256:
`CD1E3ACFC8829E06E77831D2CC007EFA8E2912DBA9CD37A9DEC56A06395CDC56`.
The installed `--eligibility` command completed successfully against the frozen
snapshot, with an empty error stream. This did not perform a new native-source or
provider collection, change durable schemas, or establish visual acceptance of the
updated Planning explanation. That UI pass remains separate under SHIP-004.
