# TajsTokens: accounting, forecasting and TT architecture
Research memo • 27 October 2026 • Updated for e609aeb, performance findings and supplied modelling research • Research and design guidance, not an implementation mandate

## Executive decision

**Keep the existing .NET application, SQLite store and substantive accounting infrastructure. Repair source coverage and identity contracts, then simplify the product-facing model into measured work, provider usage, conditional runway, and planning. Make TT a stable, versioned reference-consumption index alongside the original token vector.**

The project does not need another collection framework or a universal neural predictor. It needs a dependable chain from evidence to an answer, with each answer explaining its assumptions.

Five findings change the priorities:
1. Current upstream Codex physically compresses old rollouts. The reviewed TajsTokens discovery/reader/checkpoint path does not support that representation.
2. Richer provider usage and per-response evidence already exist upstream **and are already partly integrated into TajsTokens**. Improve their coverage and use; do not duplicate the collectors.
3. PACE-001 now has a reproduced cause: fixed weight per poll amplifies a rounded jump and decays on flat polls. Separately, the affected installed binary had a schema-8/schema-9 compatibility mismatch blocking native ingestion; the merge integrates compatibility, while the pace policy remains unchanged.
4. Scenario eligibility is narrower than token accounting. “Unsupported scenario” does not mean “the tokens cannot be read.” The report's 67 missing-workload cases require individual reason classification.
5. Performance fixes introduced an interactive-process throttling/GC trap while repeated discovery, projection and calibration work remains. Section 11 separates verified code mechanisms from unmeasured runtime impact and prioritizes bounded repairs.

### Agreed direction, October 27 follow-up

The agreed starting direction is rich reconciled evidence, requirements specific to each question rather than one global eligibility gate, and a simple frozen, versioned TT basis. Establish dependable accounting before adding forecasting complexity. Missing evidence must not become zero consumption.

The forecasting design separates continuation of current work from prediction of usual future activity. The detailed estimator, context-window selection, exact TT weights/reference basket and pricing feed remain to be specified and validated. Agreement on direction does not establish those details or authorize deployment.

### What was and was not checked

- Initial TajsTokens review: `3d8acba00757999e776d6ddc5f2bf1c0027b152c`, October 13. Follow-up review: merged GitHub main `e609aebee78712dd26c2eae040005e6dcf2377f3`, October 27. The relevant merge diff, new study/investigation and regression-test source were inspected. Older pinned links remain historical evidence where indicated; this is not a full independent rerun of the merged application.
- Codex upstream: `3b600d056650850c1d2cfca7a86c4079cc538e7a`, October 27, 15:39 UTC.
- Current primary sources for comparable tools; pinned revisions where specified below.
- Your supplied plan, threshold-study report and two screenshots.
- Earlier ChatGPT discussions, including September 8–21 and October 6. The October 6 assistant proposal was subsequently supplied in full, followed by the October 27 modelling research integrated in section 12. Other unavailable replies were not treated as verified evidence.
- **Not checked:** your currently running local build, unpublished changes, native rollout files, live provider responses or telemetry database. No software was installed, no tests run, and no Codex/Work task or device operation launched.

## 1. The intended product, recovered from your conversations

You explicitly made TT a required product concept on September 20. You had already described a workload basket becoming a stable TT amount, with different conversions to Plus/Pro and short/weekly allowances. You also understood that those conversions could change. This is a coherent product goal, not something to dismiss because a universal physical unit is unavailable.

You found the psychometrics analogy helpful: observed indicators, a defined score, and a separately tested relationship to an outcome. The important correction is that an initial TT index is closer to a **defined composite scale** than a discovered universal latent trait. Its validity must be tested; naming it does not establish what it measures.

Your present goal includes conditional continuation: “If this work keeps running, when do I exhaust the allowance?” That is useful even when the app does not predict when you will take a break.

Sources: [September 8 workload/TT example](https://chatgpt.com/c/6aa06fa2-c6c4-83eb-9ce9-9e28b5547520?messageId=b44856d7-81f2-4fdf-8259-b397d408c658), [TT is required](https://chatgpt.com/c/6aafe793-1f60-83eb-927b-669000b44d86?messageId=bbb21e66-acd7-4729-9f55-f6121cc2e8f0), [psychometrics helped](https://chatgpt.com/c/6aafe793-1f60-83eb-927b-669000b44d86?messageId=bbb21318-2b17-448e-bf18-6fd1275a3331), [October 6 proposal](https://chatgpt.com/c/6ac4e1a7-d3cc-83ed-ab29-d4a6459f2686?messageId=f5f7c75b-f308-4411-aa6f-8d3f05009fd1).

## 2. Upstream reality: what we can actually observe

### Physical compression is a correctness issue

Codex supports plain `.jsonl` and Zstandard `.jsonl.zst` representations. Its worker scans active and archived session directories and targets files at least seven days old by modification time. Plain wins if both siblings exist. Readers tolerate representation changes; append/resume can restore a compressed rollout to plain JSONL.

Therefore:
- An encoding change is not new usage, a new logical thread, or deleted history.
- Compressed bytes and decoded JSONL bytes have different offset coordinates.
- “Add a decompression stream” is insufficient if checkpoint hashes/lengths still use physical-file offsets.
- Discovering every copy and summing them is also wrong: archive copies, inherited prefixes, forks and alternate generations can repeat work.
- Compression of inactive history could harm reconstruction and training. It does **not by itself explain** a live 17-minute pace collapse.

TajsTokens currently enumerates `*.jsonl` in its filesystem discovery path and reads raw newline-delimited UTF-8 bytes. Its checkpoint verifier separately hashes physical prefixes. Extend those existing owners together.

Sources: [Codex compression](https://github.com/openai/codex/blob/3b600d056650850c1d2cfca7a86c4079cc538e7a/codex-rs/rollout/src/compression.rs), [seekable decoding](https://github.com/openai/codex/blob/3b600d056650850c1d2cfca7a86c4079cc538e7a/codex-rs/rollout/src/seekable_reader.rs), [TajsTokens discovery](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/src/TajsTokens.Infrastructure/Services/CodexObservatoryService.cs#L460-L485), [reader](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/src/TajsTokens.Infrastructure/Ingestion/FileSystemCodexSessionEventProvider.cs).

### Three evidence sources, not three interchangeable totals

**Response/local work:** current Codex has per-completed-response `token_usage_record` with response/thread/turn/session identifiers and response, turn-cumulative and thread-cumulative usage. Do not sum all three. Legacy token-count snapshots remain relevant. TajsTokens already parses response records into supplemental evidence; this is an integration/reconciliation opportunity, not a missing parser to reinvent.

**Backend-reported usage:** app-server `account/usage/read` provides account activity summaries and, with a thread ID, estimated thread credits/cost and token breakdown when supported. Estimated credits, estimated dollars, nullable native dollars and purchased-credit balances must remain distinct. Availability depends on installed protocol, authentication and backend route; unknown is not zero. TajsTokens already calls this interface and retains server evidence.

**Account allowance:** `account/rateLimits/read` supplies quota windows/buckets and related availability information. These percentages are neither tokens nor an invoice. Embedded rollout snapshots may be cached; a newly written event is not necessarily a newly measured quota.

Prefer the app-server contract to inventing another private HTTP client or copying credentials. Existing private-route integrations need capability and schema checks, not automatic expansion. Public protocol support in upstream main is not proof your installed version exposes it.

Sources: [Codex usage records](https://github.com/openai/codex/blob/3b600d056650850c1d2cfca7a86c4079cc538e7a/codex-rs/protocol/src/protocol.rs#L2463), [app-server usage contract](https://github.com/openai/codex/blob/3b600d056650850c1d2cfca7a86c4079cc538e7a/codex-rs/app-server-protocol/src/protocol/v2/account.rs#L449), [backend availability handling](https://github.com/openai/codex/blob/3b600d056650850c1d2cfca7a86c4079cc538e7a/codex-rs/app-server/src/request_processors/account_processor.rs#L1271), [quota contract](https://github.com/openai/codex/blob/3b600d056650850c1d2cfca7a86c4079cc538e7a/codex-rs/app-server-protocol/src/protocol/v2/account.rs#L323).

Existing TajsTokens integrations: [app-server requests](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/src/TajsTokens.Infrastructure/Providers/CodexAppServerEvidenceProvider.cs#L88-L112), [server-evidence parser](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/src/TajsTokens.Infrastructure/Providers/CodexServerEvidenceParser.cs#L20-L108), [response comparison already implemented](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/src/TajsTokens.Infrastructure/Ingestion/CodexResponseUsageComparison.cs).

The existing server collection is bounded: account usage plus up to six selected thread IDs and a separate approximately 30-minute collection cadence. This is not complete per-response or real-time coverage. Identity is bracket-correlated rather than necessarily embedded in each usage response. Verify compatibility and availability before treating this evidence as training labels. See also [physical checkpoint verification](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/src/TajsTokens.Infrastructure/Ingestion/CodexSessionIngestionService.cs#L62-L167).

### Storage is not permanently “one thread, one file”

Upstream now has a ThreadStore abstraction. Local canonical history and SQLite metadata are one implementation, not a promise that every backend exposes local files. Session name indexes are not complete usage inventories. A revert can retain thread identity while changing rollout filename. Creation-account metadata does not establish the billing account for every later resumed response.

Keep capability-based adapters and explicit coverage. Do not build a replacement Codex client just to observe usage.

Source: [ThreadStore architecture](https://github.com/openai/codex/blob/3b600d056650850c1d2cfca7a86c4079cc538e7a/codex-rs/thread-store/README.md).

## 3. Recommended architecture: six responsibilities, one application

These are conceptual owners, not six services, databases, frameworks or background processes.

| Owner | Responsibility | Key boundary |
|---|---|---|
| Source adapters | Discover, decode and observe supported sources | No pricing or prediction |
| Durable evidence and reconciliation | Preserve provenance; select/deduplicate logical events | Physical copies are not additional work |
| Accounting views | Tokens, TT, provider estimates, quota and API-equivalent cost | Separate meanings and coverage |
| Quota calibration | Explain compatible observed workload versus quota movement | Not a forecast of future activity |
| Forecast/scenario engine | Direct pace and workload-based conditional predictions | Assumptions and availability explicit |
| Product snapshot | One coherent answer for Overview, Planning, later tray | No duplicated math in each UI |

Keep existing SQLite and interfaces where they already implement these jobs. In particular, retain the token reducer, transactional batch writer, forecast dataset reader, quota interval builder and separate workload/accounting/forecast owners.

### Evidence contract

Preserve original content-free numeric observations, not full prompts. Maintain:
- Source host/store and physical occurrence.
- Logical thread/turn/response and supported lineage.
- Source event time, source-recording time where distinct, first collection time.
- Reported token fields plus a normalized disjoint vector.
- Model/effort/tier provenance; unknown stays unknown.
- Account/workspace/window identity and confidence of association.
- Parser/normalization version, selected generation and supersession/equivalence reason.
- Coverage state, not an invented completeness percentage.

Use the backend's response identity where available, scoped to its documented namespace. Legacy observations still need proven equivalence and counter rules. Do not delete every legacy event simply because some response records exist; those records are best-effort and may have different completeness.

**Important existing mismatch:** replacement of the same active logical path/source generation currently deletes old-generation token/workload/quota projections, while documentation describes stronger historical retention. This is not routine age expiry. Supplemental response observations already survive retirement and preserve first capture, a useful existing pattern to extend. Resolve what is durable evidence and what is a rebuildable projection before compression migration. This is not proof that your current data was deleted, but it prevents calling the entire present store immutable.

Sources: [existing reducer](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/src/TajsTokens.Core/Models/CodexTokenAccounting.cs#L106-L260), [generation retirement](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/src/TajsTokens.Infrastructure/Persistence/SqliteCodexSourceGeneration.cs#L24-L55), [retention wording](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/PROJECT.md#L986-L1003).

### Source-adapter acceptance: rich evidence, not the lowest common denominator

The required end product is a detailed, reconciled evidence view with known coverage, not merely a handful of fields that today's model happens to consume. Preserve supported source-specific numeric/detail fields alongside a common typed envelope; do not flatten distinct meanings to fit one generic record. This does not require retaining prompts, secrets or entire content-bearing payloads.

Every adapter should declare: capabilities and schema version; scope/account certainty; supported encodings and inventories; event/recording/collection times; units and field semantics; stable source identity; selected versus alternate representations; cursor/revision behavior; freshness, collection errors and coverage limits. Preserve absent, unsupported, malformed, stale and zero as distinct states. Unknown models and unknown session parentage must not discard otherwise valid token evidence.

Adapters emit observations. A shared reconciliation owner chooses equivalent/authoritative interpretations without erasing original evidence. Product views compose those observations into the richest justified answer, with conflicts and missing coverage available for inspection. Partial backend pagination, decode failures and component-version refusal must be visible instead of silently yielding a plausible incomplete total. There is no guarantee of account-wide completeness when remote/cloud sources are inaccessible; state that boundary rather than invent a completeness percentage.

Acceptance must trace one displayed value back through normalization and reconciliation to source observations, then reproduce it after replay, compression, a supported schema upgrade and duplicate discovery. Missing optional relationships may disable lineage-specific analysis, but not unrelated accounting.

### Token conservation

Cached input is generally part of input; reasoning is generally part of output. Preserve original semantics before deriving uncached input, cache-read/write where supported, non-reasoning output and reasoning output. Never add total output and reasoning as independent charges. Not every provider exposes every subdivision; absence is not zero.

Account-wide quota movement can be paired with aggregate compatible work. It cannot be assigned wholly to each chat that recorded the same snapshot. Per-thread backend estimates can provide stronger attribution, but retain their estimated status and aggregation period.

## 4. Why “67 missing workloads” is not a conclusion about parsing

The current source's feature builder looks back two hours. Its scenario-history builder excludes intervals with no token events **or any token-active session whose root/subagent role is unknown**. Its predictor representation is duration and root/subagent counts, with exclusive model/effort filtering; samples also retain quota/account/cohort metadata. The regression does not use the full token vector.

An unknown role can make a role-count scenario unsupported while leaving token accounting and direct quota pace usable. A mixed-model interval can still be valuable for category accounting even when unsuitable for an exclusive-model scenario.

The merged study source now confirms that `origin-workload-unavailable` combines three conditions: no observed token events, zero recognized root-plus-subagent sessions, or any unknown-role token-active session. It uses collection-time eligibility. The aggregate count of 67 still does not say how many failed each condition, and no local dataset was independently replayed here. Demand a reason waterfall:
1. Candidate historical prediction checkpoints, with counts and reasons for eligibility at each subsequent stage.
2. Selected source coverage and supported encodings.
3. Decoded and normalized observations.
4. Observations available by the origin's collection-time cutoff.
5. Missing/unknown roles, model, effort and account association.
6. Matching training intervals.
7. Requested scenario within observed support.

Report counts at each exclusion, examples using sanitized IDs, and overlap between reasons. Do not solve this by globally dropping provenance gates or relabelling every unknown session as a root.

Sources: [feature builder](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/src/TajsTokens.Core/Services/Forecast/CodexForecastFeatureBuilder.cs), [scenario builder](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/src/TajsTokens.Core/Services/Forecast/CodexScenarioHistoryBuilder.cs), [scenario regression](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/src/TajsTokens.Core/Services/Forecast/QuotaPredictionService.Scenarios.cs).

### Optional evidence, question-specific requirements

Distinguish **required to retain an observation** from **required to answer a particular question**. Preserve every valid supported quantity with provenance and explicit missingness; do not discard the whole record because one downstream model needs an absent field. Retaining an observation does not mean adding every overlapping representation to a canonical total: reconciliation still establishes identity and prevents double counting.

| Evidence available | Valid use | Restriction |
|---|---|---|
| Known tokens, unknown parent/root/subagent role | Token accounting and compatible token-based features | Role-count scenarios unavailable or explicitly partial |
| Known token quantities, unknown model | Retain quantities and coverage | No fabricated price or TT weight; score only covered portions and label them |
| Known amount, unknown event time | Retain untimed evidence; include in a deduplicated all-time total only where scope/identity permit | Do not assign to an hourly bucket or invent an event time |
| Fresh quota, unavailable local workload | Compatible direct conditional quota pace | No assertion that local work explains the account-wide change |
| Unknown account association | Retain unassigned evidence | Do not attribute it to the currently logged-in account |

Optional fields require an explicitly missingness-aware model or a simpler valid fallback. Making a field nullable without changing the consuming estimator is not a fix. Never substitute zero for missing tokens, roles, price or activity. Preserve identity, unit, scope and chronology safeguards wherever the requested answer depends on them.

Rich adapters should yield more usable evidence, but many token records are not necessarily many matched, independent quota/workload examples. Account-wide usage from other devices, delayed meter updates and unknown attribution can still prevent a particular conversion from being identified. Availability and coverage are properties of the answer, not a blanket judgement that the entire dataset is unusable.

### “Eligible historical origins,” in plain language

An **origin** is simply a historical moment at which a forecast would have been made. For example, at Monday 14:00, predict usage by 14:30 using only evidence available at 14:00; compare with the later observed outcome once it is available. “Historical prediction checkpoint” is the clearer product-facing name.

Eligibility means that checkpoint satisfies the stated requirements of that particular evaluation. Input availability at 14:00 and the later availability of a valid outcome are separate checks. Explain exclusions individually: missing timely inputs, incompatible account/window, unavailable outcome, unknown required feature, or unsupported scenario. Do not turn the existing role-count scenario's requirements into a universal definition of usable history.

## 5. Two hours versus twelve: four different periods

Do not replace every “2h” with “12h.” Separate:
- **Observation interval:** over what period did the meter/token count change?
- **Recent-context window:** what recent work describes the current situation?
- **Training span:** which historical examples teach the mapping?
- **Forecast horizon:** the next half-hour, session, or time until reset.

A twelve-hour context may smooth noise but mix yesterday's tasks, sleep and today's work. A short context reacts quickly but can jump when a burst leaves the window. Neither should determine the training span automatically.

Recommended starting candidates:
- Current compatible quota pace.
- Exponentially weighted recent pace with explicit elapsed-time semantics.
- Recent observed workload continuation with preserved model/category mixture.
- A longer-history activity-pattern forecast, separately labelled, only when validated.

Choose by later, unseen performance at the actual horizon and activity state. Use a small fixed comparison set, not dozens of knobs. Activity/session count is context, not proof of simultaneous generation or a direct cost multiplier.

### Two questions, not one ambiguous forecast

1. **“If this work continues, when do I run out?”** Preserve the recent observed workload intensity and model/category mixture as a conditional assumption. Do not silently dilute active-work intensity with assumed future sleep. Explain the selected observation interval and whether its rate includes idle/tool-waiting time.
2. **“Given my usual activity, will my allowance last until reset?”** Include expected active and idle periods, using a sufficiently supported historical pattern. This may disagree legitimately with continuous-work runway. Label it separately; do not silently substitute it for the first answer.

A practical candidate design uses a responsive recent estimate alongside a slower reference estimate. Treat token flow, quota movement, observed workload mix and activity state as related but distinct evidence. A stale collector is not proof of idleness; an open session is not proof of generation. Genuine pauses can change an observed average, but unchanged rounded quota polls alone do not establish that ongoing work has stopped.

Start with existing recent-interval and elapsed-time-weighted candidates rather than adding another forecasting framework. Compare a small predefined set of context windows on chronological held-out outcomes, separately by horizon and active/burst/idle/missing-source state. Report missed bursts and availability alongside average error so a nearly-zero forecast cannot look good merely because most time was idle. Select or combine candidates only after those comparisons; neither two nor twelve hours is an agreed universal constant. Correct PACE-001 before attributing gains to a more elaborate switching policy.

### PACE-001: the 14 → 0.1 pp/hour incident is now reproduced

The pushed investigation and regression fixture establish the runtime path as the same `legacy-ewma` reset fallback, not an estimator switch, TT conversion or workload regression. The source code corroborates the fixed weekly update:

`rate = 0.25 × adjacent displayed-meter rate + 0.75 × previous rate`

- At 16:06:25 local, the meter moved from 44% to 45% used over 64.7836 seconds. Treating that rounded one-point jump as consumption wholly inside the interval produced approximately 55.57 pp/hour; its 25% vote raised the estimate to 13.9716 pp/hour.
- Seventeen unchanged polls then reduced it to `13.971596243 × 0.75^17 = 0.105023746` pp/hour, despite the same 55% remaining. Poll duration did not control the multiplier.
- Extra startup polls 26 and 20 seconds after preceding observations accelerated the decay. Persisted history was reconstructed; the problem was not lost in-memory state.
- The all-flat safeguard did not apply because the full segment had previously moved. Neither 14 nor 0.1 is thereby established as the current active-work rate.

The two-hour bracket at 16:23 was 7 displayed points over 2.008006569 hours: 3.486044373 pp/hour. Holding that account-wide average constant would exhaust 55% in about 15.8 hours. This remains a conditional arithmetic comparison, not a calibrated ETA. There were zero comparable reset-calibration epochs and no reset band.

Two already-implemented comparisons at the final anchor were time-ewma-2h ≈2.6133 and time-ewma-6h ≈2.1562 pp/hour. The new test shows these unchanged when intermediate flat-suffix polls are removed while preserving endpoints and elapsed time; legacy EWMA changes by more than 50-fold. This isolates the specific poll-count defect without declaring either alternative universally best.

**Status:** reproduced and documented, not fixed. The new tests deliberately characterize defective behavior. The quoted 613 passing tests and isolated Windows build were Codex's pre-merge report, not independently run here and not proof the defect was corrected. The merged source still retains the legacy default.

**Recommended correction:** make elapsed time and meter precision part of the fallback's contract, reusing existing candidates where suitable. Keep the old estimator for reproducible research, not automatically as an eligible production fallback merely because a challenger lacks eight completed cycles. Correctness eligibility and statistical model promotion are separate gates. A corrected conditional baseline need not claim a calibrated reset band; quantify its later predictive performance separately. Do not hardcode 14, impose an arbitrary rate floor, or hide the issue through UI smoothing.

Acceptance should cover the retained incident, sparse/dense unchanged polling with identical endpoints, startup polls, all-flat and quantized-jump periods, stale versus freshly observed quota, reset boundaries and missing local activity. No synthetic passing test alone establishes future forecast accuracy.

Each forecast should retain an inspectable calculation receipt: anchor, input interval/counts, estimator/version, numerator/denominator, fallback reason and coverage. The sustainable budget remains a separate quantity: remaining allowance divided by remaining clock time, including idle hours.

Sources: [pushed investigation](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/docs/source-notes/WEEKLY_PACE_INVESTIGATION.md), [retained replay tests](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/tests/TajsTokens.Core.Tests/WeeklyPaceRegressionTests.cs), [pace implementations](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Core/Services/Forecast/QuotaPaceModels.cs), [default/selection policy](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Core/Services/Forecast/QuotaForecastCalibration.cs).

### A separate live-ingestion compatibility failure

The investigation records native token evidence ending at 12:12:22 local and workload observations at 12:00:53, before the reported work. It identifies an affected binary supporting observatory component 8 against a retained component-9 database, blocking observatory initialization. This is a specific explanation for those live collection gaps, not proof of why all 67 historical replay origins were unavailable.

The merge integrates schema-9 reader/writer compatibility and tests both v8 migration and existing-v9 planner paths. Tests also preserve rejection of a genuinely newer unsupported schema instead of lowering its marker. Verify actual installed build/component compatibility and fresh ingestion after deployment; a source fix does not prove the running app recovered. Preserve first collection times and nullable/verified source timestamps. Do not backdate a late import to manufacture historical online availability.

Compression remains an independently confirmed reader gap: the merged reader change optimizes buffering, not decompression. The pace regression also reproduces with synthetic ongoing token work, so ingestion lag is not required to cause PACE-001.

Sources: [schema compatibility tests](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/tests/TajsTokens.Core.Tests/ObservatorySchemaCompatibilityTests.cs), [current source-time contract](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/PROJECT.md#source-time-and-schema-compatibility), [current plain reader](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Ingestion/FileSystemCodexSessionEventProvider.cs).

## 6. TT: make it real without making it magical

### Proposed meaning

**TT-v1 is a fixed reference-consumption index for supported observable AI workloads.** It is not money, purchased credit, remaining allowance, physical FLOPs, intelligence or successful work.

For version v:
`TT_v = sum(weight_v[model, category] × observed_tokens[model, category]) / reference_score_v`

The reference basket equals one TT. Its size is a convention, not a discovered constant. Do not execute synthetic tasks just to define the basket.

My recommended **shippable TT-v1** is a frozen reference-tariff index: choose a dated published model/category pricing schedule and a named reference basket, then freeze both. This is a reproducible convention for weighting consumption, not a claim that prices measure physical compute or current subscription cost. No live prices or exact weights are selected in this memo. Effort affects the observed token vector; do not invent an additional effort multiplier without evidence.

Reuse the existing frozen-basis immutability, normalization and identity machinery, but not its five-pooled-weight shape unchanged: model-specific reference-tariff TT requires a versioned joint model/category basis (see section 12 and Appendix A, Research §7). Keep API-equivalent cost under current/date-effective prices as a separate view; a fixed-vintage TT ruler need not move whenever prices move. Derive TT directly from the workload vector under its fixed schedule, not through a chain of estimated credits and quota conversions.

If your intended meaning excludes a price-based reference, the alternative is a frozen empirical reference-metering index: select a representative covered cohort with varied models/category mixes, fit restrained nonnegative weights, verify coefficient stability and held-out behavior, then freeze the result. Highly correlated categories may need pooling. The existing first-20 experiment is not automatically a representative shared standard; retain it as provisional/local until justified.

Either route must publish coefficients, reference basket, semantics, supported dimensions and basis ID. Expose observed TT with coverage even when quota conversion is unavailable. Material changes create a new basis; original and explicitly restated histories remain distinct. The simple frozen, versioned starting approach is agreed in principle. Exact weights, reference basket and tariff-reference versus empirical-reference semantics remain product decisions; no specific numeric basis has been approved.

For cross-user comparisons, use the same published basis. Independently learned local TT rulers are not comparable merely because both are named TT. Unsupported models receive explicit provisional/partial treatment, never silent zero weight.

### Future-proof catalogs without moving the TT ruler

Use three separately versioned contracts:

1. **Model catalog:** discover provider/model IDs and supported capabilities/efforts through available provider interfaces, cache source and retrieval time, and preserve observed aliases and retired historical entries. Catalog-listed, locally observed and account-access-verified are different states. Unknown newly observed models remain ingestible without an application update. Never guess that an alias always resolves to the same model over time.
2. **Price catalog:** refresh independently from a supported pricing source. Store model/category/tier and applicable context brackets, units/currency, retrieval time, source, effective dates when supplied, and uncertainty. Support local overrides as separate versioned entries. Validate incoming schemas/units and preserve the last known good snapshot on failure; record stale or missing prices explicitly. An update is data, never executable code. A community catalog can be a labelled fallback, not silently promoted to provider truth. No particular third-party feed is selected by this memo.
3. **TT basis:** pin exact weights, reference basket, semantics and source price snapshot (if tariff-derived). New prices may automatically update the current cost view, and may prepare a candidate new TT basis. Activating a new basis must follow an explicit product policy, show the change, and preserve original scores. Historical restatement uses a chosen common basis and remains distinct from originally recorded values. Automatically discovered models need a documented basis-extension/mapping rule or an explicitly unsupported/provisional TT score, not a zero weight.

OpenAI's documented Models API exposes model metadata, not pricing. Codex model/list is a catalog interface and can return bundled entries in some provider configurations; it is not universal proof of entitlement. Prefer appropriate existing authenticated interfaces without adding an API credential requirement just to populate a Codex model picker. No test inference or paid request is necessary merely to discover catalog entries.

Sources: [Models API response schema](https://developers.openai.com/api/reference/resources/models/methods/list), [Codex model catalog caveat](https://developers.openai.com/siwc/token-sharing-open-source/codex-app-server).

### Keep conversion separate

Observed work → TT is scoring.
Observed work → provider-reported estimated credits is a separate source.
Observed work → predicted quota is calibration.
Future work → predicted TT/quota is forecasting.

A single quota-per-TT rate only works across arbitrary mixtures when the quota weights are approximately proportional to the TT weights. Otherwise equal TT can cost different quota. Preserve full token vectors and mixture information for conversion; do not route every prediction through a lossy scalar.

This also makes policy-change detection possible: the ruler stays fixed while the relationship to quota may change. If the ruler continuously retrains to match quota, it can hide exactly the change you wanted to detect.

### Where state-space models belong

A small state-space model is a reasonable later candidate for time-varying quota conversion or reporting lag. It is not a way to recover unidentified missing usage with certainty. First establish that observable work explains the meter under reasonably complete coverage. Distinguish explicit plan/source changes, mixture shifts, incomplete collection and unexplained residual changes before saying “provider policy changed.”

In psychometric terms: validate the scale and test stability across groups/time; do not let a model's latent label substitute for construct validity. The supplied October 27 research explicitly withdraws a universal learned TT factor as the default destination; such a factor is an optional measurement hypothesis, not an assumed upgrade path (section 12).

Source: [existing TT design and scalar limitation](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/docs/source-notes/TT_LAYERED_STATISTICAL_DESIGN.md).

## 7. Prediction without one giant model

Keep two legitimate routes:
1. **Direct quota route:** recent quota → conditional future quota.
2. **Composed route:** future workload vector → quota conversion → future quota.

For “continue current work,” keep the activity assumption explicit and estimate token intensity/composition from comparable recent work. For an ordinary future-behavior forecast, model activity/idle time separately. A session may be waiting on tools while technically alive.

For custom scenarios, agent counts and reasoning effort are not enough to determine tokens. Use a recent-work pattern or supported historical neighborhood, let the user edit assumptions, and expose unsupported extrapolation. Avoid presenting a numerical regression output as a validated answer just because the equation can evaluate it.

Reuse existing nonnegative interval-aware accounting before introducing a new model family. Rounded meters are intervals, not exact values; delayed/cached data need separate treatment. Under nearest-integer rounding, a displayed level lies roughly within half a point of its label; other rounding rules differ. Adjacent differences share endpoint errors and are not independent.

Sources: [existing cost model](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/src/TajsTokens.Core/Services/Accounting/QuotaAccountingModel.cs), [quota interval construction](https://github.com/tajemniktv/TajsTokens/blob/3d8acba00757999e776d6ddc5f2bf1c0027b152c/src/TajsTokens.Core/Services/Accounting/QuotaCostObservationBuilder.cs), [Stan rounded-data treatment](https://mc-stan.org/docs/stan-users-guide/measurement-error.html#rounding).

## 8. What other apps actually teach us

| Project | Useful mechanism | Do not mistake it for |
|---|---|---|
| CodexBar | Separate remote quota and local token/cost sources; source failures and partial pagination explicit | Complete account-wide local usage or quota-to-dollar conversion |
| ccusage | Compressed/archive discovery, encoding-race recovery, cumulative/last-counter reconciliation | Subscription allowance measurement |
| Tokscale | Logical session accounting, inherited-history handling, category-conservation tests | Perfect cloud coverage |
| Win-CodexBar | Shared accounting core, thin Windows UI; simple quota pacing | A learned future-workload model |
| Claude-Code-Usage-Monitor | Provenance-labelled snapshots and retained warehouse | A percentile-derived token limit being an official quota |
| Peras | Sampled-quota trend, guard hysteresis, fresh-check bookkeeping | Exact per-project attribution or a guaranteed hard spending cap |

Peras uses a one-hour recent lookback for the five-hour window and 24 hours for weekly, with minimum spans and fallbacks. Those are sensible product heuristics to compare, not evidence that TajsTokens should adopt those constants. Its guard can prevent subsequent covered actions; parallel activity and failures limit enforcement. Its proportional chat attribution and “project recently active” accounting remain approximations.

Tokscale has an actual issue reproducing 160 tokens becoming zero after compression in an older version. This supports treating encoding compatibility as a first-order accounting test.

Borrow source separation, conservation tests, explicit provenance and deterministic guard logic. Do not copy an attractive dashboard's hidden assumptions. Any actual code reuse needs license review.

Sources:
- [CodexBar source design](https://github.com/steipete/CodexBar/blob/main/docs/codex.md)
- [ccusage parser, pinned](https://github.com/ccusage/ccusage/blob/0c76c3e72b3a553be46f02feb9a9dbfab06e9fe9/rust/adapters/codex/src/parser.rs)
- [Tokscale parser/tests, pinned](https://github.com/junhoyeo/tokscale/blob/39ecb83ab045a300c6f502f9c9319acf5c80e764/crates/tokscale-core/src/sessions/codex.rs)
- [Tokscale compression regression](https://github.com/junhoyeo/tokscale/issues/1421)
- [Win-CodexBar pacing code](https://github.com/nesszer/Win-CodexBar/blob/main/rust/src/core/usage_pace.rs)
- [Claude monitor calculations](https://github.com/Maciek-roboblog/Claude-Code-Usage-Monitor/blob/main/src/claude_monitor/core/calculations.py)
- [Peras forecast, pinned](https://github.com/Vertias3u/Peras/blob/13b523ff8bb244fcf061234617221dc1d90693eb/src/forecast.mjs)
- [Peras failure policy](https://github.com/Vertias3u/Peras/blob/13b523ff8bb244fcf061234617221dc1d90693eb/src/failure-policy.mjs)
- [Peras attribution approximation](https://github.com/Vertias3u/Peras/blob/13b523ff8bb244fcf061234617221dc1d90693eb/src/blame.mjs)

No Android-specific implementation was inspected deeply enough to recommend. The cross-platform architectural lesson is one accounting core and thin consumers, not separate phone-specific estimators.

## 9. Evaluation: answer the right question with the right data

Keep two reports:
- **Reconstructed history:** what can the best evidence available today tell us about past work?
- **Operational replay:** what could the app actually have predicted using evidence collected by that historical moment?

Recovered older rollouts can improve present training and historical accounting. They cannot retroactively improve a past live forecast's information set. Preserve original first collection times through re-decoding and normalization upgrades where evidence establishes identity. Do not set first collection to event time.

The now-pushed threshold study's zero supported scenarios is an availability result, not a measured accuracy result for those scenarios. Its sparse long-horizon outcomes cannot justify precise reset-horizon bands. Do not indiscriminately lower production thresholds before explaining coverage and eligibility. This does not require preserving a known invalid poll-count fallback: measurement correctness and challenger-performance thresholds are separate decisions.

Evaluate:
- Token conservation, identity and reimport invariance.
- Point accuracy alongside answer availability.
- Range coverage **and** width.
- Accumulated signed error and missed high-consumption periods, not just average short-horizon error.
- Active, idle, tool-waiting, stale and missing-source states.
- Account/plan/window/model cohorts separately.
- Direct quota versus composed forecasts on matched origins.
- Cost explanation using realized tokens separately from end-to-end future-work prediction.

Use chronological evaluation and timestamped shadow predictions before outcomes mature. No paid synthetic traffic, benchmark runs or live policy promotion is authorized by this memo.

Sources: [event-time/processing-time model](https://research.google.com/pubs/archive/43864.pdf), [forecast evaluation](https://otexts.com/fpp3/toolbox.html), [scenario forecasting](https://otexts.com/fpp3/regression.html#scenario-based-forecasting).

### Current merged product progress

The merge already implements persistent live allowance context, shared outlook projection, a unified Planning flow, structured support reasons, a read-only threshold study, and bounded background-calibration infrastructure. These are not future greenfield tasks. Preserve that work and extend its existing owners. The separate owned calibration process/cache is already present; this memo's logical responsibilities are not a proposal to add more processes.

The much shorter current PROJECT.md clarifies product intent. One detail worth refining is its global source-priority list: authority should be quantity-specific (fresh quota for current allowance; response/ledger evidence for local tokens; provider reports for their own reported units), as its own conflict example acknowledges. A blanket “rollouts always outrank provider” rule would be wrong. This is a contract clarification, not a reason to restore the deleted documentation bulk.

Source: [current project contract](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/PROJECT.md), [study implementation and exact origin gate](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Core/Research/ForecastThresholdStudy.cs#L135-L168).

## 10. Concrete implementation outcomes

### A. Recover all supported history without changing its meaning
First verify the compatible merged binary resumes native ingestion without changing retained evidence. Then extend existing discovery, reader and checkpoint semantics for compressed/plain transitions. Preserve logical identity and prior capture times. Prove equal totals for plain, compressed, both-present, archived, restored and interrupted-read fixtures. Resolve retention semantics first where replacement can discard evidence.

### B. Explain every missing answer and every pace change
Extend the structured support reasons now implemented into a full coverage waterfall. PACE-001 is diagnosed; the next change is a correctness-tested, time-aware conditional fallback with explicit uncertainty, not another open-ended investigation. Separate unknown role from unknown tokens; keep weaker valid answers available.

### C. Use already-collected provider evidence deliberately
Verify installed app-server capabilities and the existing stored contracts. Reconcile local response events, cumulative totals and provider thread estimates over compatible scopes. Do not add totals from these parallel representations. Preserve provider estimates versus actual billed quantities.

### D. Release a defined TT basis
Following the agreed simple frozen starting approach, specify tariff-reference versus empirical-reference consumption, supported model/category space and reference basket. Freeze and version a defensible initial basis; tariff-reference scoring need not wait for quota-regression promotion. Expose observed TT and coverage without requiring every quota conversion to succeed. Keep conversion and future-work uncertainty separate.

### E. Improve one planning pathway
Use recent-work continuation first, then supported edits. Reuse the workload/cost engines; preserve the old count-based scenario as a comparator rather than treating it as the whole architecture. Select improvements from later outcomes, not training fit.

### F. Build the UI and later tray from one snapshot
Overview should show allowance, observed current work, conditional runway, and explanation of a changed answer. Planning owns assumptions. Technical evidence remains inspectable but secondary. The tray later consumes the same product snapshot; no separate forecasting implementation.

These outcomes are bounded slices, not a reason to add six new roadmaps. Update the existing authoritative project document once decisions are accepted. Keep the research memo as supporting evidence, not a second competing specification.

## 11. Performance and responsiveness: reduce work, not the user's ability to interact

### Scope and diagnosis strength

Follow-up source review on October 27 reconfirmed main at `e609aebee78712dd26c2eae040005e6dcf2377f3`. The user reports constant disk activity, roughly 6–10% CPU and 500 MB RAM before the performance caps, followed by unresponsive animation, delayed appearance and unreliable tray restoration. These are user-observed symptoms, not independently captured profiling measurements. Source inspection identifies recurring work and concrete scheduling hazards, but cannot assign percentages, prove a memory leak, or establish the cause of every live stall. No local process was inspected or manipulated and no tests/builds were run.

The objective is **low steady-state resource use and responsive interaction simultaneously**. Removing all limits and restoring unlimited background work is not the recommendation. Preserve bounded calibration and the existing incremental infrastructure; fix excess work and keep UI/tray control responsive.

### A. The performance policy creates a responsiveness trap

The `ea4334f` change applies a 0.5% total-system hard CPU cap, process background scheduling and EcoQoS to the interactive process while hidden/minimized. `UpdateDashboardVisibility` applies that policy **before** synchronously destroying the page/frame. Consequently, page unloading and cache release execute under the cap. The tray HWND and the callback which lifts the cap share that process: lifting restrictions before window construction does not help until the callback itself gets to execute.

After a three-second delay, when the managed heap is at least 64 MiB, the policy can force a blocking, compacting Gen2/large-object-heap collection. `Task.Run` does not make this collection non-blocking for managed UI callbacks. Cancellation is checked before the collection, not a mechanism for aborting a collection already running. The busy predicate covers forecast refresh, not every ingestion/query activity. These are verified mechanisms and strong latency suspects, not measured pause durations.

Foreground recovery also has a failure-path defect: requested visibility is recorded before native policy calls succeed. A failed background-mode exit or CPU-cap removal is only debug-logged; another request for the same visibility returns early, without retrying the failed transition. Track desired and successfully applied policy separately, report failures and retry appropriately. Do not claim a native failure occurred on this machine without evidence.

Every hide/minimize additionally replaces the navigation frame and discards cached pages. Restore reconstructs the selected page at low dispatcher priority; the default dashboard then reloads its selected history. Deferral in `a64f3b0` avoids doing construction directly inside the tray callback, but does not eliminate reconstruction. Preserve lightweight active-page/filter/navigation state, pause subscriptions/timers while hidden, and evict expensive history selectively rather than treating every minimize as a cold start.

**Recommended direction:** keep the UI/tray process outside the hard CPU budget and remove routine forced compacting GC. Keep the existing isolated calibration worker bounded; make collection cooperative, incremental and bounded. If collection still needs strict process isolation after measurement, evaluate that specific boundary rather than adding a new service framework by default. Stage changes with resource and latency comparisons, not an unmeasured blanket rollback.

Sources: [resource policy](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.App/Services/WindowsResourcePolicy.cs#L27-L101), [visibility ordering](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.App/App.xaml.cs#L376-L426), [page teardown/restore](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.App/MainWindow.xaml.cs#L101-L139), [Windows hard-cap semantics](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information), [blocking GC](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals).

### B. A concrete repeated-work amplifier: completion events are reused as triggers

`App.OnSnapshotUpdated` searches the snapshot's event history for any `Refresh completed` entry, then schedules memory reclamation and calls `UpdateRecentFleetTrayAsync`. Compact-forecast and calibration completion snapshots append to the prior event list, retaining that entry. Whenever that marker is retained, these derived updates can trigger the same expensive side effects again, even though another collection pass did not just finish.

The fleet refresh is not just updating an icon: it searches the thread catalog, iterates source candidates, queries latest turns using `ROW_NUMBER() OVER (PARTITION BY thread_id ...)`, and performs additional item queries for in-progress turns. Output limits bound returned rows, not necessarily the database work needed by the window query. The caller checks whether a result was superseded only **after** the read and has no dedicated coalescing gate. It can perform work whose result is immediately discarded. This is a source-proven repetition mechanism; actual database query plans and cost remain unmeasured.

Use a typed refresh generation/completion transition rather than scanning accumulated display events. Run at most one fleet refresh for the relevant generation, coalesce newer requests and cache results within a stated freshness policy. Narrow recent-thread queries where supported and inspect query plans without modifying Codex-owned schemas. Ensure time-based freshness can still expire even when source data does not change. Reclamation should not be retriggered by historical event text.

Sources: [snapshot consumer](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.App/App.xaml.cs#L177-L223), [derived snapshots preserve events](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Services/TelemetryCoordinator.cs#L630-L689), [fleet query](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Services/CodexThreadObservabilityService.cs#L48-L136).

### C. Visible-page work should follow changes, not every snapshot

The intelligence page queues a dispatcher callback for every telemetry snapshot, rebuilds account choices and rerenders current content. Dashboard rendering re-sums the selected ledger, re-counts distinct chats and replaces quota tile items; width changes can also rerender it. A new quota or forecast snapshot does not necessarily change historical totals. Cache history summaries by selection/data generation, update changed card properties, and coalesce presentation to the newest snapshot. Keep alert transition processing separate so coalescing visual updates does not silently drop alerts.

This is not a claim that history is re-read from the database on every telemetry event: the ordinary event handler uses the already-loaded history snapshot. History queries run on load/filter changes and after destructive restore. The current collector and intelligence query entry points already use `Task.Run`; do not diagnose them as inherently executing their database work on the dispatcher merely because callers use `await`.

Sources: [telemetry-driven rendering](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.App/Pages/CodexIntelligencePage.xaml.cs#L28-L98), [dashboard aggregation](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.App/Pages/CodexIntelligencePage.Dashboard.cs#L34-L88).

### D. Repeated collection and forecasting work

The following are specific optimization candidates, not a claim that every path runs on every machine or that all logical reads hit physical disk.

| Path | Source-supported behavior | Targeted improvement |
|---|---|---|
| Calibration scheduling | A fresh quota lane, expired cooldown and free gate allow a new worker without checking whether training evidence changed. Cooldown is five minutes hidden / one minute visible after an attempt; timeouts are ten / three minutes. Showing the dashboard clears the prior background cooldown. | Gate expensive fitting/replay on qualified evidence revisions and newly matured outcomes. Keep cheap current-anchor/time-sensitive projection separate. |
| Calibration preparation | Each worker creates a new intelligence service, loads 120-day quota/workload/token/context data, then separately loads 30-day token history. Invocation-local reuse shares matching quota lanes; persistent numeric caching skips fits only after training features have been built and hashed. | Reuse revision-qualified preparation/features/evaluation results and share overlapping reads where snapshot semantics permit. A slice killed before completing a fit currently retains no preparation progress. |
| Repeated privacy cleanup | Token nowcasting constructs a new observatory store. Its initialization runs four legacy privacy-scrub UPDATEs even at schema 9; the initialization guard is per instance. | Preserve privacy enforcement but put legacy cleanup under durable versioned migration ownership; enforce current privacy contracts in ordinary writers. Reuse initialized owners where appropriate. |
| Historical reset detection | Normal reset refresh ranks eligible retained quota history with ROW_NUMBER before taking 512 rows per stream, in a writer transaction. The result bound does not necessarily bound ranking work. | Maintain per-stream incremental tails/watermarks with existing rebuild invalidation for deletion/replacement/backfill; confirm plans and rows visited. |

The dataset reader's large row limits are safety bounds, not measured retained sizes. Historical session-prefix metadata is intentionally retained for correct interpretation, so blindly applying lower date filters or truncating lists is not a safe memory optimization. Likewise, unchanged tokens do not imply no time-sensitive work: moving windows, fresh quota anchors, reset boundaries, stale evidence and newly matured validation outcomes can require updates. Cache keys/invalidation must include these semantics rather than using only rounded percentage or token count.

Sources: [calibration schedule](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Services/CalibrationProcessService.cs#L33-L125), [worker stages](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Services/CalibrationWorkerHost.cs#L27-L35), [quota preparation](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Services/SqliteIntelligenceService.cs#L87-L185), [token read/store initialization](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Services/SqliteIntelligenceService.cs#L539-L551), [privacy scrub](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Persistence/SqliteCodexObservatoryStore.cs#L394-L416), [reset refresh](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Persistence/SqliteTelemetryRepository.cs#L1215-L1283).

Collection has additional conditional costs worth addressing:

- **Backlog defeats the reconciliation interval.** The indexed collector schedules full reconciliation nominally every five minutes, but marks it complete only when no deferred/unapplied thread remains. Its 8-recent + 8-older batch limit means backlog, a missing file or a persistent state/rollout mismatch can leave full catalog reads and recursive coverage enumeration due every pass. Separate successful discovery/reconciliation from rollout catch-up; keep bounded pending retries and correct watermarks. [Scheduling and completion](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Services/CodexObservatoryService.cs#L152-L358).
- **A small append can require a large prefix read.** Prefix verification is keyed by identity, checkpoint/hash, length and write time. On append, it rehashes from byte zero through the old checkpoint before continuing. Truly unchanged warm stable-identity files already skip this verification; not every file is rehashed every poll. Measure prefix bytes separately and coalesce hot-file processing. Do not replace this safeguard with a suffix-only hash or length/mtime shortcut that misses rewritten prefixes. [Verification path](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Ingestion/CodexSessionIngestionService.cs#L77-L150).
- **No new records does not always mean no writes.** Reached ingestion paths still save the final checkpoint/resume state and update last-seen metadata. Normal matching indexed fingerprints avoid this, but unresolved retries and filesystem fallback do not. Fallback discovers and attempts every file rather than using the indexed 16-file budget. Skip truly unchanged durable state writes, coalesce freshness metadata under an explicit policy, and bound fallback fairly without losing eventual coverage. [Final checkpoint](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Ingestion/CodexSessionIngestionService.cs#L239-L253), [fallback loop](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Services/CodexObservatoryService.cs#L394-L437).
- **Token-cache invalidation is too broad.** Every nonempty semantic batch bumps the accounting revision, including token-free records. That can invalidate historical model/hourly aggregates unnecessarily. The cache-stamp query also includes COUNT and MAX over native token events; check its query plan rather than assuming it is free metadata. Introduce a token-relevant revision covering all mutations that actually affect the projection, including retirement, deletion and timestamp enrichment. [Provider cache and aggregates](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Providers/SqliteNativeCodexAccountingProvider.cs#L41-L214), [batch revision bump](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Persistence/SqliteCodexSemanticBatchWriter.cs#L49-L95).
- **Incremental collection still rebuilds metadata collections.** Fingerprints and spawn-edge/reconciliation sets are loaded into fresh collections. Query candidate fingerprints or retain a bounded invalidatable index; give relationship reconciliation its own cadence. Edges can arrive independently of thread timestamp changes, so a thread-only dirty flag is insufficient. [Fingerprint load](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Persistence/SqliteCodexStateIndexStore.cs#L265-L292), [edge processing](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Services/CodexStateCatalog.cs#L282-L353).
- **Actual ingestion still has per-token transactions.** Semantic records are batched, but token observations are subsequently applied serially through separate connection/transaction operations. Benchmark batching within the existing reducer owner while preserving ordering, deduplication, atomic state/event updates and checkpoint-after-success recovery. [Batch writer](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Persistence/SqliteCodexSemanticBatchWriter.cs#L57-L95), [token persistence](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Infrastructure/Persistence/SqliteCodexObservatoryStore.cs#L571-L610).

Keep existing mitigations: matching-file skips, indexed batch bounds, prefix verification reuse, token projection cache, WAL, guarded component initialization, one calibration worker, persistent numeric-fit reuse and a full polling delay after each completed pass. Base repository initialization still reopens/checks WAL and schema each time; guard successful initialization with explicit database-replacement recovery, but do not misdescribe this as a full migration on every poll.

**Suggested repair order:** remove the UI/tray scheduling trap while preserving worker containment; fix repeated completion-trigger side effects and reconciliation retry amplification; narrow accounting invalidation and stop no-op writes; then profile and improve calibration preparation/replay and transaction batching. This order combines concrete correctness/latency defects with avoidable repeated work, without pretending the largest CPU contributor has already been measured.

### E. What to measure before declaring the resource problem fixed

Use one representative retained-data fixture and real-host timing, without generating paid model traffic. Capture a startup, unchanged idle period, active append period, backlog, hide/restore during work, and reset boundary. Separate cold foreground launch from waking a hidden process; the cap cannot automatically explain a slow first-ever foreground launch.

For each collector generation record elapsed time and process CPU time by phase, files enumerated/changed/deferred, bytes hashed/decoded, rows read/written, transactions, projection invalidations, fleet-query count, calibration start/skip reason, dataset-preparation time, fit-cache hits/misses, allocation volume and GC pause duration. Record requested/applied resource state and native failures. Use content-free metrics, not prompts or raw event bodies. The existing collection `BytesObserved` sums selected file lengths; it is not a physical-I/O counter or an exact count of hashed/decoded bytes. Do not use it alone to claim disk savings.

For interaction, record tray-input/callback/activation/first-content timestamps and dispatcher stalls. For memory, distinguish private commit, resident working set and managed live heap; compare settled retention after equivalent cycles rather than treating a 500 MB snapshot alone as proof of a leak. Separate actual storage I/O from logical/cached reads: source-level repeated queries are not proof of continuous physical disk access.

Acceptance should demonstrate:
- Warm unchanged inputs avoid rollout-body rereads and unnecessary accounting/retraining, while quota collection and freshness expiry still work.
- Failed or deferred sources remain recoverable without forcing whole-source enumeration on every poll.
- A derived forecast update does not repeat a completed generation's fleet scan or memory-reclamation side effects.
- Short minimize/restore cycles preserve useful state and remain responsive during collection/calibration.
- Cancellation, reset, source replacement, model/settings changes and new data invalidate exactly the derived results they should.
- Resource use falls without stale results being disguised as fresh or correctness/retention safeguards being removed.

Choose numerical CPU, disk, memory and latency targets against the measured hardware/data baseline; this review has not established defensible universal thresholds. The existing worker limits can remain containment while the causes are corrected. Do not optimize by discarding evidence, weakening identity checks, rewriting first-collection times or making missing usage zero.
## 12. Integrated modelling addendum: what the supplied research changes

### Provenance, authority and verification

On October 27 the user supplied an explanatory ChatGPT message and `TajsTokens-model-research-2026-10-27.zip`. The package contains `RESEARCH.md`, `README.md`, `model_checks.py`, `wolfram_checks.wl` and `check_results.json`. **Appendix A preserves the supplied RESEARCH.md verbatim**, including its derivations, source list and original author's verification claims. This section is Echo's integration and qualification of that source. References to “Research §…” below mean sections of that appendix, not sections of this memo.

The two documents do not become competing specifications. The main memo owns the consolidated recommendation; the appendix is supporting research. Neither authorizes implementation, paid experiments, a promoted TT basis or a production forecast. Existing performance constraints in section 11 remain in force: no always-on Python/Wolfram/LLM service or repeated fitting on unchanged polls is implied.

For this integration, the model/category feature loss, five-weight TT contract, saturation exclusions and envelope-intersection semantics were independently checked against pinned `e609aeb` source. Simple counterexample and incident arithmetic were independently recalculated. The package's scripts and retained results were read, **not executed**; its Wolfram/Python execution claims remain attributed to its author. No telemetry fit, C# test, host profiling or production validation was performed. Selected current official provider statements were checked, not the entire cited literature or the user's billing entitlement.

### 12.1 Corrections to the earlier memo

**Preserve the joint model/category vector, not merely separate totals.**

The supplied explanatory message states:

> No regression, neural network, or Bayesian wizardry can recover a pairing that its inputs discarded.

Research §4 supplies the concrete example: invented weights yield costs of 51 versus 15 for two workloads with identical model totals and category totals. The current `CostVector` uses five pooled category totals and separate model/effort masses, so that ambiguity is real. Marginals can still predict well over restricted patterns or under extra assumptions; they cannot uniquely identify arbitrary missing pairings.

Reconcile source events into sparse `x[model, category]` quantities, retaining unknown mass. Do not reconstruct the joint table by multiplying marginal shares, which would silently impose an independence assumption. Fit only supported distinctions, comparing pooled baselines against restrained model/category coefficients. Model vocabulary overflow and truly missing model metadata must remain distinguishable even if a fitting representation groups them together.

**The existing TT machinery is reusable, but its five-weight shape is insufficient for model-specific tariff TT.** Research §7 corrects an overly broad reading of section 6's “reuse” recommendation. Preserve immutable-basis IDs, reference normalization and history, but version a joint-coordinate basis contract. Adding model names to the old support list does not add model-specific prices. The observation-scoring overload gates complete categories/model/effort support; the lower-level vector overload has narrower checks. Avoid claiming all scoring paths use identical gates.

**A universal learned TT factor is not the default long-term destination.** Research §2 explicitly withdraws that stronger October 6 suggestion. A frozen reference index is the recommended starting convention, not a temporary pretence awaiting discovery of physical compute. A later latent model remains a separately justified research hypothesis. Preserve the full vector regardless.

Sources: [joint-feature limitation](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Core/Services/Accounting/QuotaAccountingModel.cs#L320-L354), [marginal shares](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Core/Services/Forecast/CodexForecastFeatureBuilder.cs#L91-L96), [TT basis and scoring](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Core/Models/TtWorkloadBasis.cs#L40-L99).

### 12.2 Answer-specific support: exact, bounded, conditional or unavailable

Two particularly useful statements are preserved from the supplied research:

> Availability is a property of the requested answer, not just a property of the fitted model.

> A deterministic feasible range is not a confidence interval.

Research §§6–7 sharpen the optional-data policy:

- Known total output with identical reasoning/non-reasoning weights gives an exact score despite an unknown split. “Exact” is conditional on trusted totals, correct reconciliation and the named basis, not proof of complete account coverage.
- Known input with unknown cache split can give a finite score range. Missing mapping does not automatically mean zero, and an unsupported mapping is not synonymous with infinite cost.
- Compatible coefficient sets can tightly constrain a familiar workload mix even when individual prices remain unidentified. In the synthetic example, `9 <= a+b <= 11` supports 9–11 for the same mix but 0–22 for two input units; adding `14 <= 2a+b <= 16` narrows that latter range to 6–14.
- Distinguish **inconsistent constraints**, **unbounded result**, **finite bounds**, and **exact under assumptions**. Do not hide infeasibility by silently widening tolerances or adding priors.

The row-space condition in Research §7 is sufficient for an exact aggregate score, not necessary for every particular nonnegative feasible set. Common equal-weight rules should be deterministic and cheap; a generic optimization solver need not run in the tray. With unrestricted external account activity, local zero cost can remain compatible with all nonnegative meter movement. More sophisticated latent modelling does not manufacture attribution.

### 12.3 Observation mechanics and uncertainty contracts

Research §5 complements, rather than replaces, the PACE-001 repair. Use compatible cumulative meter levels with effective measurement time, rounding and lag assumptions. Adjacent differences share endpoint errors. A duplicate cached observation adds no new measurement, but a genuinely fresh later flat reading can constrain a recent slope. Neither per-poll decay nor “ignore every flat reading” is a universal rule.

A bounded constant-slope challenger is a compatibility test, not a calibrated future-work forecast. The research's approximately 13.680–18.574-hour rounding-only runway follows under fresh nearest-integer endpoints, no reset/lag and continuation of the historical average. Treat endpoints as conservative closure bounds: half-open rounding cells need not attain them. This is not a new live ETA or confidence interval.

Research §12 correctly distinguishes measurement-envelope intersection from coverage of latent truth. The current code explicitly labels its 80% target as the former. Containment and intersection can bound **realized empirical** latent coverage if each true value belongs to its declared measurement set; they do not establish future probabilistic coverage. The UI must keep measurement bounds, identification bounds and predictive intervals distinct.

Source: [current uncertainty disclaimer](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Core/Services/Accounting/QuotaAccountingModel.cs#L18-L32).

### 12.4 Joint paths and threshold outcomes

Research §§9–11 extend the two-question forecast design without making every proposal a required implementation:

- Test joint contiguous-block workload resampling as a challenger, preserving category/model pairing, bursts and pauses. It needs compatible history and chronological evaluation; resampling does not create unseen behavior.
- A hurdle expression `P(work) × E(amount | work)` gives an **expected amount**, not the full amount/path distribution or the probability of lasting until reset. Those need timing, dependence and conditional distributions.
- Propagate the same workload paths through short and weekly policies. Separate targets need not mean statistical independence; a shared-cost relationship is a testable hypothesis, not provider truth.
- Retain the no-crossing-before-reset outcome. Label a timing summary conditional on crossing as such; it is not the unconditional median. Do not take the minimum of marginal quantiles as the quantile of first exhaustion.
- Distinguish same-token **revaluation** under another schedule from predicting how another model would perform the task. The latter can change volume, retries, tool use and quality.
- Preserve saturated observations for a later censoring-aware evaluation only under a justified clipping/timing contract. Last-below and first-saturated **collection times** do not necessarily bracket actual crossing when reporting lag is unknown. Existing saturation exclusion is appropriate for the current ordinary bounded-delta loss, not evidence already lost from storage.

Source: [current saturation gate](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Core/Services/Accounting/QuotaCostObservationBuilder.cs#L67-L105).

### 12.5 Provider distinctions checked during integration

Current official documentation distinguishes Fast's credit multiplier (2×) from its included-allowance multiplier (2.5×), and applicable Ultrafast cases (6× versus 8×). It also describes shared agentic usage across eligible surfaces and possible continuation of an active turn after a limit. These are scoped October 27 documentation facts, not fitted user coefficients, proof of historical rates, or personal plan entitlements. They support separate tariff/quota contracts and separate **allowance threshold** versus **execution stop** targets. [Rate-card notes](https://help.openai.com/en/articles/11481834-chatgpt-rate-card-business-enterpriseedu-credit-based-pricing), [Codex plan guidance](https://help.openai.com/en/articles/11369540-using-codex-with-your-chatgpt-plan).

### 12.6 Implementation implications and reading map

| Existing memo outcome | Refinement from supplied research | Research sections |
|---|---|---|
| A–C: dependable evidence | Preserve sparse joint quantities; retain observation timing and scope; do not infer missing pairings | §§3–5, 7 |
| D: defined TT basis | New explicit joint-coordinate basis, exact equivalent-category scoring, bounded partial scoring; freeze semantics and reference | §§2, 7–8 |
| B/E: valid answers | Separate mixture support from coefficient identification; compare restrained joint model and partial-identification diagnostics | §§4, 6, 12 |
| E: planning | Revaluation versus changed-task prediction; direct and composed routes; joint paths, no-crossing mass, censoring-aware evaluation | §§9–12 |
| F: product snapshot | Exact-under-basis, bounded, partial, unsupported, stale and uncalibrated states instead of one generic confidence number | §13 |
| Section 11: performance | Offline/bounded research and revision-qualified reuse; no new continuous fitting service | §13 |

First restore trustworthy collection and responsive operation and repair PACE-001. Next add joint evidence/basis semantics and cheap exact/bounded scoring. Then evaluate support-aware conversion and one short-horizon continuation challenger before extending to threshold probabilities and habitual activity. Exact basis weights, speed normalization, solver choice and production thresholds remain undecided. This is a refinement of the existing outcomes, not a second roadmap or approval to launch all proposed models.

## Bottom line

**The architecture is feasible, and much of it already exists.** The most valuable change is not “2h becomes 12h” or “replace ridge with a neural net.” It is an encoding-neutral, provenance-preserving evidence path; explicit separation of observed tokens, provider estimates and quota; a stable TT ruler; and forecasts whose inputs, assumptions and limitations are visible.

TT can be real. The app can be useful before every conversion is perfectly calibrated. Neither goal requires pretending that missing evidence is zero or that a scalar can erase genuine differences between workloads.


## Appendix A. Supplied modelling research, preserved verbatim

Source: RESEARCH.md inside the user-supplied TajsTokens-model-research-2026-10-27.zip (October 27). The original author is the assistant in the separate ChatGPT research conversation. First-person claims and “What was run” below belong to that source, not to Echo’s integration. See section 12 for independent checks, qualifications and how this affects the consolidated recommendation. The source bibliography is preserved; relative companion-file references refer to the supplied ZIP.

The source text between the markers is unchanged, with SHA-256 d8239a85375da366bf0a0e6531e4a542dd45add8e902d7fae7105eb029c62a4d.

<!-- BEGIN VERBATIM SUPPLIED RESEARCH.md -->
# TajsTokens models: research addendum

**27 October 2026 · Research and design proposals, not an implementation mandate**

## Decision

Keep the supplied memo's architecture. Strengthen it with a measurement-aware, support-aware modelling contract:

> Preserve the joint workload information; establish which quantities are identifiable; return exact values, defensible bounds, conditional forecasts, or an explicit unavailable result according to the evidence.

This is not a proposal to replace the application, introduce a neural predictor, install a statistical service, or expand background collection. The existing accounting, forecasting, SQLite and immutable-basis owners are the appropriate starting points. The original memo and `PROJECT.md` retain their existing roles. These notes are supporting research, not another competing roadmap.

The principal additions are:

1. Preserve **model × token-category** combinations. The current richer quota feature vector loses that pairing.
2. Treat **identification ranges**, **meter precision**, and **predictive uncertainty** as different quantities.
3. Make answer requirements depend on the requested calculation. Missing subdivisions sometimes permit an exact score, not just a partial one.
4. Model quota readings as observations of an accumulated process, not as independent exact per-poll consumption.
5. Forecast the distribution of **crossing an allowance threshold**, including the possibility of no crossing before reset.
6. Keep TT as a frozen reference-consumption convention. Do not infer a universal latent resource from billing-derived indicators.

## 1. Evidence, scope and limitations

The supplied source was **TajsTokens-accounting-forecasting-TT-research-2026-10-27.md**, including its October 27 architecture, PACE-001 and performance updates. Its distinction between agreed direction and undecided implementation details is preserved here.

A read-only GitHub check confirmed `main` at **e609aebee78712dd26c2eae040005e6dcf2377f3**, the same revision discussed in that memo. Targeted source inspection covered:

- `QuotaAccountingModel.cs`, including feature construction, interval ridge fitting, first-20 training and evaluation-band semantics.
- `QuotaCostObservationBuilder.cs`, including interval construction, chronology, rounded targets and saturated-outcome exclusion.
- `QuotaPaceModels.cs`, including fixed-per-poll and elapsed-time-weighted estimators.
- `CodexForecastFeatureBuilder.cs`, including model/effort marginal shares and collection-time availability.
- `TtWorkloadBasis.cs`, including its five pooled category weights and support requirements.
- `TT_LAYERED_STATISTICAL_DESIGN.md` and the consolidated audit register.

Primary external sources were consulted for rounded measurements, bounded-error identification, composite-index construction, forecasting evaluation, scenario interpretation, censoring and conformal prediction. Current official OpenAI documentation was checked for pricing-versus-allowance semantics. Sources are listed at the end.

**What was run:** two Wolfram evaluations containing symbolic checks and synthetic constrained optimizations; independent Python/NumPy/SciPy checks of the mathematical examples. `model_checks.py` and `check_results.json` are included.

**What was not run:** the C# test suite, the application, a local telemetry database replay, a fit to the user's actual usage history, paid inference, benchmark prompts, remote-device operations, or a production-model change. No accuracy, learned coefficient, personal quota capacity or numerical production threshold is established by this work. The supplied memo's incident numbers were used for arithmetic, not independently recovered from logs.

The literature review is targeted, not an exhaustive systematic review. For the bounded-error identification and intermittent-demand papers, publicly available abstracts support the limited methodological descriptions below; their complete implementations were not reproduced. The conformal paper's introduction/figure and the relevant OECD weighting/sensitivity pages were inspected in rendered PDF form.

## 2. What changes from the earlier proposal

### 2.1 A universal latent TT is not the default destination

The earlier idea of learning one latent quantity from quota, prices, credits and resource signals was too ambitious as a default. Prices and credits may be transformations of the same tariff, not independent measurements of a common physical resource. A factor fitted to them can reproduce those conventions without acquiring provider-independent meaning.

The OECD/JRC treatment of composite indicators supports specifying the construct, weighting choices and sensitivity checks explicitly. A statistical weighting procedure does not eliminate the need to define what the index means. [S1]

For TajsTokens, a **frozen reference-tariff consumption index** is a practical first convention. It measures reference-weighted consumption, not intelligence, useful completed work, FLOPs, current money or an account's remaining quota. A later latent model would need an additional, explicitly justified measurement claim.

### 2.2 Nonnegative regression already exists

The source already has an interval-distance, nonnegative ridge cost model, several comparators, chronological held-out trials, and conservative evaluation labels. Proposing that family as though it were missing would be redundant. Its useful next changes concern representation, support and interpretation. [R1]

### 2.3 Better mathematics does not repair absent evidence

The memo's source-coverage and ingestion work remains upstream of empirical calibration. More sophisticated estimators cannot establish which account paid for an unassigned response, reconstruct inaccessible cloud work with certainty, or turn a late import into a timely historical observation.

However, evidence insufficiency is question-specific. A dataset can support workload scoring or a usual-mix forecast while not supporting a model-specific price decomposition. This is the central distinction to make operational.

### 2.4 Experimental design should begin passively

Instead of automatically spending quota on synthetic experiments, measure which contrasts ordinary work already supplies: different cache mixes, output mixes, models, tiers and compatible account periods. Report missing contrasts. A deliberate paid experiment is a separate, explicitly authorized decision.

## 3. Current provider facts that materially constrain the model

The current official rate card distinguishes purchased-usage pricing from included allowance consumption. It lists Fast mode at **2×** the Standard credit rate but **2.5×** the included allowance rate; supported Ultrafast examples use **6×** versus **8×**. These are documented policy distinctions, not inferred user-specific coefficients. [S2]

Official documentation also describes shared allowances across Codex and other eligible agentic surfaces. Consequently, unexplained account-wide movement need not originate in another local Codex thread or even another Codex device. [S2, S3]

OpenAI says an active turn may continue after an included limit is reached, subject to fair-use limits. Therefore an allowance threshold and the exact time execution stops are different targets. [S3]

These observations reinforce the memo rather than overturn it:

- Separate source-reported quantities, counterfactual reference valuation and learned quota conversion.
- Key applicable policy by product, account/plan, billing route, model, service tier and effective time where supported.
- Do not turn current documentation into a claim about historical October 6 pricing or the exact behavior of this user's account.
- Do not assume an API-key tariff applies to a ChatGPT-authenticated session.

The reviewed documentation also differentiates cache-write charges and route-specific context treatment. This is a reason to preserve those dimensions and policy provenance, not to introduce every conceivable feature into a small regression. [S2, S4]

## 4. New source finding: marginal features lose the joint workload

`QuotaAccountingModel.CostVector` takes pooled token-category totals and, in its richer candidate, appends token mass by model and effort. `CodexForecastFeatureBuilder` supplies those model and effort shares. The feature vector does **not** preserve the model/category cross-tabulation. [R1, R4]

That distinction is structural. Consider two synthetic models with invented per-unit weights:

| Model | Input weight | Output weight |
|---|---:|---:|
| A | 1 | 5 |
| B | 10 | 50 |

Two workloads each contain one input unit, one output unit, one unit from A and one unit from B:

- Workload 1: A supplies the input; B supplies the output. Cost = 1 + 50 = **51**.
- Workload 2: B supplies the input; A supplies the output. Cost = 10 + 5 = **15**.

Their marginal features are identical, but their true costs under this illustrative schedule differ by a factor of 3.4. No nonlinear model can recover a pairing that its input representation discarded. These numbers are not OpenAI rates. Wolfram and the independent Python check reproduce the example.

### Recommended representation

Preserve a sparse additive vector `x[model, category]`, with supported tier/product distinctions where necessary. Construct it from reconciled token events, not by multiplying separately aggregated shares. Existing events already supply a starting point for this work. Unknown model mass should remain explicitly unassigned.

Fit a small ladder:

1. Raw-total baseline.
2. Pooled-category baseline.
3. Model-specific category coefficients with shrinkage toward the pooled-category model.
4. Tier or effort departures only when supported by additional compatible evidence.

The third step need not mean a new Bayesian runtime. A constrained ridge objective can penalize departures from pooled weights while keeping final model/category coefficients nonnegative. Effort should not receive an automatic extra charge when its effect is already represented by observed token consumption.

### A related identification issue

When categories are complete and conserved, their sum equals total tokens. The model totals, including unknown-model mass, also sum to total tokens; effort totals do likewise. Those feature groups therefore contain exact linear dependencies. The richer implementation can construct as many as 39 base features before extensions while the research fit uses 20 training intervals, although the actual active feature count in the user's data was not measured. [R1]

Regularization can select a stable numerical solution. It does not prove that the selected individual coefficients are independently determined by the observations. This is the ordinary collinearity distinction between a useful prediction and an identified explanation. [S5]

## 5. A better quota observation model

### 5.1 Start with cumulative levels

For one compatible account/window/regime, let `U(t)` be latent accumulated allowance consumption in percentage points. A reading `y_j` describes `U(s_j)` at its effective provider measurement time `s_j`, which need not equal the collection time.

If nearest-integer rounding is actually justified, a non-boundary observation has the constraint:

`y_j - 0.5 <= U(s_j) < y_j + 0.5`.

Other rounding rules, clipping, cached values and reporting delay require different treatment. An integer JSON field alone does not establish the complete backend precision or timing contract. The repository already acknowledges this limitation. [R2, S6]

The statistical principle is to model the observation mechanism rather than declare the displayed value exact. A probabilistic rounded-data model assigns probability to the entire observation interval. A deterministic bounded-error model retains the interval as a constraint. These are different interpretations and should not share an unlabeled “confidence” badge. [S6, S7]

### 5.2 Adjacent deltas share errors

Writing `y_j = U_j + e_j` gives:

`Delta y_j = Delta U_j + e_j - e_(j-1)`.

Even in the simplifying toy case of independent endpoint errors with variance `sigma²`, adjacent delta errors have covariance `-sigma²`. Real rounding errors need not be independent or uniformly distributed; the example merely demonstrates why independent-delta likelihoods require justification.

Likewise, many overlapping intervals do not create many independent outcomes. Preserve endpoint identity and either work with cumulative-level constraints, use well-separated evaluation blocks, or explicitly model dependence.

### 5.3 What to ship first, and what to test next

The immediate correction remains the memo's elapsed-time-aware fallback with explicit precision and freshness. Existing recent-bracket and elapsed-time-weighted implementations should remain the first competitors. [R3]

A useful next research challenger is a **bounded local slope**: over a declared recent segment, find nonnegative slopes `r` and intercepts `a` satisfying all justified level intervals:

`lower_j <= a + r * (s_j - s_0) <= upper_j`.

This is a small linear feasibility/optimization problem. Its output is a range of slopes compatible with a constant-rate approximation, not a probability distribution for future activity. If the set is empty, do not silently erase the conflict: the chosen constant-rate approximation, timing, measurement bounds or regime assumptions failed. A robust slack version can be tested, but its tolerance needs an explicit policy.

Later, a piecewise-rate/state-space candidate can allow rates or reporting lag to vary. That complexity is justified only if it beats the simpler candidates on timely held-out evidence.

**Polling nuance:** duplicates of the same cached observation add no new measurement. A genuinely fresh unchanged reading at a later time can constrain the slope. “Every unchanged poll is redundant” is therefore also wrong. Clock-time smoothing removes a particular per-poll decay defect; it does not solve all quantization and lag problems.

### 5.4 Recalculation of the memo's example

The memo's seven displayed points over 2.008006569 hours give **3.486044373 pp/hour**. At 55 points remaining, holding that historical average constant gives **15.77719447 hours**.

Assuming fresh nearest-integer endpoint readings of 38 and 45, with no reporting lag or reset, the rounding-compatible average rate is approximately **2.988–3.984 pp/hour**. Accounting jointly for the uncertain final level and uncertain rate gives a constant-rate runway range of **13.680–18.574 hours**.

These are **rounding-only arithmetic bounds under an unchanged historical-average assumption**, not an 80% or 90% predictive interval, not a current active-work rate, and not a live exhaustion forecast. Future workload variation could dominate this uncertainty.

## 6. Partial identification: learn what the data actually constrain

For well-matched work/usage intervals, a bounded-error linear model can define:

`Theta = { beta >= 0 : lower_i <= x_i' beta <= upper_i for every retained constraint i }`.

For a particular proposed workload `x*`, solve:

`lower_prediction = min(beta in Theta) x*' beta`

`upper_prediction = max(beta in Theta) x*' beta`.

This is a set-membership identification approach. The literature provides linear-programming treatments for bounded-error regression; the proposed application here is to TajsTokens' explicitly qualified quota intervals. [S7]

This formulation is not a claim that rounding is the only source of error. Bounds for reporting lag, normalization uncertainty or model mismatch require evidence or sensitivity assumptions. If a coefficient is unobserved and unbounded, say so rather than adding an invisible prior to make a tidy number.

### Synthetic example

Suppose all informative workloads have a 1:1 input/output mix and reveal only `9 <= a+b <= 11`, with nonnegative category weights.

| Requested prediction | Feasible cost range |
|---|---:|
| Another 1:1 workload | 9–11 |
| Two input units, no output | 0–22 |
| The same pure-input workload after additionally observing `14 <= 2a+b <= 16` | 6–14 |

Wolfram and SciPy produce these bounds independently. More copies of the first mix cannot separate the category weights. A different mix contributes a genuinely new contrast.

The product consequence is important: **a conversion can be supported for the current mixture without supporting arbitrary scenario edits or a universal tariff explanation.** Availability is a property of the requested answer, not just a property of the fitted model.

### External activity is not a free latent explanation

If account movement contains unobserved external work `z_i >= 0`, constraints become:

`lower_i <= x_i' beta + z_i <= upper_i`.

With no useful restrictions on `z_i`, the data may provide upper bounds on local coefficients but no positive lower bounds. Setting local cost to zero and attributing all movement to `z_i` can satisfy the observations. A latent-state model does not remove this ambiguity.

Use provider/thread reports where scope and semantics match, bounded attribution assumptions where defensible, and sensitivity results elsewhere. An account-wide direct quota forecast can remain available even when local causal attribution is unavailable. Reporting lag or corrections must be resolved separately before interpreting interval residuals as external usage.

## 7. Missing information can still produce exact answers

Let the supported observed aggregates be `A x = b`, with nonnegative unknown detailed quantities `x`. For a frozen TT basis with weights `w`, consider the range of `w' x` over that feasible set.

Three outcomes are possible:

- The minimum and maximum coincide: the requested score is determined even though some details are missing.
- Both are finite but different: a defensible score range is available under the specified constraints.
- A useful finite bound cannot be established: expose the covered subtotal and missing dimensions, or withhold the total.

A simple sufficient condition for an exact score is `w = A' v` for some vector `v`; then `w' x = v' b`.

### Examples, with invented score weights

If total output is 1,000 and both reasoning and non-reasoning output have weight 5, the score is exactly **5,000** regardless of the unknown subdivision.

If total input is 1,000 but the cache split is unknown, with uncached weight 1 and cached weight 0.1, the score is bounded by **100–1,000**. It is not legitimate to substitute either endpoint, or a midpoint, as an observed score.

An unknown model with no supported mapping does not acquire a plausible weight just because other models have one. A truly unknown model may prevent a finite upper TT bound for the missing portion.

### Implication for existing code

`TtWorkloadBasis` currently stores five pooled category weights and checks complete categories plus supported model/effort shares. Model and effort lists act as support gates, not model-specific numerical coefficients. [R5]

A model-specific tariff TT cannot be implemented by merely adding model names to those lists. Reuse its immutability, reference normalization and hashing pattern, but introduce explicit joint model/category semantics under a new basis contract. Preserve the old research basis and scores.

For the new basis, derive requirements from the score being requested. Unknown effort need not block a tariff score whose coefficients are identical across effort. Unknown reasoning split need not block a pooled-output score. This is a targeted extension of the memo's question-specific eligibility rule, not permission to ignore provenance.

There is no requirement to run a generic optimization solver on every UI refresh. Common aggregation and equal-weight cases can have small, tested deterministic implementations; the optimization formulation supplies the research specification.

## 8. TT's recommended role and its mathematical limits

Use a frozen, dated reference schedule and a named basket:

`TT_v(x) = sum_(m,k) p_v[m,k] * x[m,k] / score_v(reference_basket)`.

Exact weights and the reference basket remain explicit product choices. This research does not activate a basis. My recommendation is tariff-reference semantics for a first public basis, with local empirical indices retained as research alternatives.

A tariff-derived TT is mathematically proportional to reference-vintage tariff value. That is not a defect, but it means TT adds **stable units and interpretability**, not new predictive information. Changing current prices need not change that ruler.

Decide whether the reference schedule normalizes away service speed. With a standard-speed reference, identical model/category work has identical TT even if Fast mode consumes more live allowance. The live conversion must then carry the speed distinction. This is coherent; silently changing the meaning between views is not.

A useful basis contract specifies normalization, joint dimensions, unsupported mappings, equivalent-category rules, exact coefficients, source vintage and historical-restatement behavior. A compatible new-model extension can retain existing weights and reference normalization while receiving a new snapshot identity. Compatibility on old coordinates must be explicit; new-model weights must not appear silently.

### Why one TT-to-quota exchange rate is conditional

Absorb normalization into `w`, so `TT = w' x`, and write quota cost as `q = beta' x`. For positive supported TT weights:

`q / TT = sum_k s_k * (beta_k / w_k)`, where `s_k = w_k*x_k/TT` and `sum_k s_k = 1`.

Therefore the conversion is a weighted average of category-specific ratios. A universal exact scalar conversion for arbitrary supported mixtures requires `beta = c*w`. Wolfram verified that condition in a two-category symbolic case.

A scalar conversion may still work over a restricted observed mixture. The product should then say that it applies **at that mixture**, retain the vector route, and expand or withhold the result when the mixture changes beyond validated support.

For fixed known `beta` and an otherwise arbitrary nonnegative supported mixture:

`min_k(beta_k/w_k) <= q/TT <= max_k(beta_k/w_k)`.

Those are deterministic mixture bounds, not calibrated predictive probabilities. Zero-weight dimensions with possible positive quota cost invalidate a finite upper conversion bound and must be handled separately.

## 9. Forecast workload paths, not just independent category totals

### 9.1 Current-work continuation

The first forecast should condition on continuation of a declared recent operating pattern. The pattern includes natural tool waits and burst spacing if those are part of the selected observation interval. It must not assume the user will sleep unless that is part of the chosen scenario.

Begin with the existing recent-rate/elapsed-time baselines. A useful challenger is **joint block resampling** of comparable recent work: resample contiguous workload-vector/time blocks rather than independent category amounts or independently forecasted agents. This can preserve burstiness, cache/output correlation, model pairing and simultaneous activity.

A bootstrap does not invent unseen behavior or prove stationarity. Compatible history, source quality, block selection and held-out performance still determine whether this is useful. Intermittent-demand research provides precedent for forecasting lead-time cumulative demand distributions rather than only point demand; transferring that idea to AI work is a proposal, not an established TajsTokens result. [S8]

### 9.2 Usual future activity

For the separate “will it last until reset?” question, a hurdle decomposition is sensible:

`P(any work during horizon) × E(work amount | some work during horizon)`.

The existing design note already proposes this, so it is not a new missing feature specification. [R6] Start from a simple supported activity pattern and recent positive-work amounts; introduce a small activity classifier and positive-amount model only if they improve future outcomes.

Do not multiply independently fitted mean volume by mean category shares and assume their correlation disappeared. Preserve the joint distribution, or model category amounts conditionally on the volume/activity state.

### 9.3 Counterfactual revaluation is not task prediction

“Replay these same observed tokens under another model's reference weights” is an exact conditional valuation when the mapping is supported.

“Run the same task with another model” changes unknown future token volume, tool use, retries, completion probability and possibly quality. That requires workload/outcome evidence. A model-picker scenario should not imply that holding token volume constant predicts the real changed task. Scenario forecasts are conditional on their supplied predictors; their intervals do not automatically include uncertainty in those assumptions. [S9]

## 10. Runway is a threshold-crossing target

For a future cumulative-consumption path `B(t)` and remaining allowance `R`, define:

`T = inf{t >= 0 : B(t) >= R}`.

There can be substantial probability of **no crossing before reset**. That outcome should not be forced into an arbitrary large finite ETA. A median crossing time may not exist within the relevant horizon.

A path-based model can derive the probability of crossing before the known reset, the crossing-time distribution among paths that cross, and the remaining allowance distribution at a chosen deadline. Each answer must retain the scenario and uncertainty qualification that produced its paths.

For simultaneous short and weekly limits, propagate the **same workload path** through the two window policies. Resets change the respective states at their known times. Do not assume independence between limits driven by the same work, and do not take the minimum of marginal quantiles as though it were the quantile of the first crossing.

The threshold is not automatically a hard interruption time: provider grace, purchased usage and explicitly chosen resets can affect continuation. Treat those as separate policy/scenario inputs. Do not assume an automatic purchase or banked-reset action. [S3]

### Preserve censored outcomes

`QuotaCostObservationBuilder` currently excludes saturated outcomes because the hidden overshoot is not an ordinary bounded delta. That is a defensible boundary for its existing loss. [R2]

A later censoring-aware evaluator could retain them as lower-bound cost evidence, or retain an interval between the last below-threshold and first at-threshold observations. It must not train against the clipped number as the exact latent amount. Censored-data likelihoods are a standard way to retain this kind of information without fabricating an unseen value. [S10]

This matters because excluding all threshold-reaching intervals can remove precisely the high-consumption cases that planning needs. Whether that selection materially affects this dataset remains to be measured.

## 11. Two quota windows may share information, but this is a hypothesis

Separate window targets remain essential. However, independent fitting need not be a permanent restriction.

A testable shared-cost hypothesis is:

`Delta q_short = a_short * g(x)`

`Delta q_weekly = a_weekly * g(x)`.

For fresh aligned intervals wholly inside compatible reset periods, the ratio of latent increments would be constant under that hypothesis. Multiple mixtures are needed to test whether it survives changes in model/category/tier composition. Rounding, lag and shared external work must be handled, and the two readings are not independent replicates of the work.

In matrix terms, a common scalar requires proportional cost vectors. A joint model can therefore test a constrained shared-cost structure against separate per-window vectors. If supported, partial pooling may reduce variance; if contradicted, retain separate conversions. No private provider formula is assumed.

Keep a frozen reference calibration for change detection alongside any adaptive predictor. A provider policy change is only one possible explanation for residual drift. First separate known plan/tier changes, workload composition, source coverage, attribution and reporting behavior.

## 12. Evaluation must distinguish compatibility from predictive coverage

### 12.1 An existing good disclaimer needs to reach the UI

`QuotaAccountingModel` explicitly says its research bands target **80% measurement-envelope intersection**, not calibrated latent coverage or exhaustion probability. [R1] Preserve that qualification. An interval overlapping the set of possible measured outcomes has not necessarily covered the unknown true outcome.

For example, a measurement interval `[9,11]` and prediction interval `[9.9,10.1]` intersect. A compatible true value of `9.2` is nevertheless outside the prediction interval. The included synthetic check verifies this elementary but important distinction.

For trusted measurement sets `A_i` and forecast intervals `P_i`, report two empirical quantities:

- **Certain coverage:** fraction for which `A_i` is wholly contained in `P_i`.
- **Possible coverage:** fraction for which `A_i` intersects `P_i`.

The actual latent coverage is bounded by those two fractions when every true value lies in its declared measurement set. These bounds do not account for a wrong observation contract or unbounded lag. Likewise, point absolute error can be bounded below by distance to the observation interval and above by the farther endpoint when the interval is finite.

### 12.2 Keep probability labels honest

A deterministic feasible range is not a confidence interval. A range of future simulated outcomes is model-conditional until predictive calibration is demonstrated. A coefficient interval is not a prediction interval for a future task. Every range needs its target and assumptions.

Ordinary conformal coverage relies on exchangeability. Research extends it to distribution shift, and adaptive conformal inference can target long-run coverage frequency, but neither provides an automatic reliable guarantee for every particular rare workload or sudden regime change. [S11, S12]

Do not add conformal calibration as a cosmetic wrapper around sparse, biased or incorrectly timed targets. It becomes useful after the target, source coverage and chronological evaluation are sound.

### 12.3 Evaluate the actual operational question

Retain the memo's distinction between reconstructed history and operational replay. Each forecast origin must use only inputs collected by that time; model fitting at the origin must use only outcome labels already available then. Preserve predictions before outcomes mature.

Use chronological rolling-origin evaluation at the requested horizons. Avoid overlap leakage across tuning/evaluation boundaries and block uncertainty summaries by compatible sessions/reset periods where appropriate. The forecasting literature explicitly distinguishes genuine future forecasts from fits that use realized future predictors. [S9, S13]

Compare direct quota and composed forecasts on matched origins, but also show their separate answer availability. Report interval width, signed accumulated error, missed bursts, false exhaustion warnings and warning lead time, not just mean error during mostly idle periods.

The fixed first-20 fit is valuable as a frozen research reference. It is not a universal sample-size theorem or the permanent best production training policy. “Enough rows” should not substitute for workload contrast, support and held-out performance.

## 13. Product contract and bounded implementation sequence

This is a model-semantics recommendation, not a screenshot-based UI audit or a new visual design.

The product snapshot should distinguish:

| Answer | Meaning | Material qualifier |
|---|---|---|
| Measured work | Reconciled tokens and TT under a named basis | Covered sources and dimensions |
| Reported allowance | Provider observation for a specific window | Measurement/collection time and freshness |
| Continue this work | Conditional continuation of a selected operating pattern | Pattern, scope and supported mixture |
| Usual activity outlook | Forecast including expected active and idle time | Historical support and calibration |
| Counterfactual revaluation | Same fixed workload under a different schedule | Not a prediction of changed task behavior |

Useful explanatory states include **exact under basis**, **bounded under stated assumptions**, **partial**, **unsupported mixture**, **stale source**, and **forecast not yet calibrated**. These are not an invented numeric confidence score.

### Recommended order inside the existing owners

**First, evidence and pace correctness.** Complete the memo's ingestion/representation work, repair PACE-001 and retain source-time, account and coverage safeguards. This is still the highest-value prerequisite.

**Second, representation and score semantics.** Add joint model/category projections; give the public TT basis an explicitly versioned representation; support exact equivalent-category aggregation and bounded partial answers. Keep old basis/history semantics intact.

**Third, support-aware calibration.** Add rank/contrast diagnostics and scenario-specific prediction ranges to the existing accounting research. Compare the existing pooled model with a restrained joint model on matched future outcomes. Known external coverage gaps must not be fitted away as local prices.

**Fourth, short-horizon continuation.** Compare the corrected direct baseline, recent joint-workload continuation and a small block-resampling challenger. Keep the feature/fit caches revision-qualified; do not turn each unchanged poll into a fitting job.

**Fifth, threshold outcomes and usual activity.** Add censoring-aware evaluation, coherent short/weekly path propagation and separately validated activity forecasts. Introduce adaptive state estimation or conformal layers only when evidence shows their benefit.

The research helpers can run offline or in the existing bounded calibration process. Wolfram is useful for checking derivations and prototypes; the installed application does not need Wolfram, an LLM call, an always-on Python service or a new database merely to implement these contracts.

## 14. What would change these recommendations?

A complete, fresh provider ledger with authoritative per-response allowance debits could substantially reduce the need to infer cost coefficients. A shared provider unit across both windows could simplify their relationship if its contract were explicit. Conversely, substantial missing cross-surface activity or rapidly changing policies could make a direct account-level forecast more useful than local cost attribution for longer.

A genuinely broad, well-covered dataset might show a simple scalar conversion is adequate over the product's intended workload support. That would justify using it there; it would not prove arbitrary cross-provider universality.

The most important unresolved empirical questions are the actual retained joint-feature coverage, effective sample diversity, reporting lag, external account activity, horizon-specific forecasting performance and the desired TT reference convention. No mathematical demonstration in this package substitutes for those measurements.

## Reproducibility

- `model_checks.py`: independently checks the counterexamples, linear feasibility bounds, endpoint-error covariance, incident arithmetic, elapsed-time decay identity, mixture-conversion bounds and interval-coverage counterexample.
- `check_results.json`: generated output from a successful run.
- `wolfram_checks.wl`: the two evaluated Wolfram expressions, retained for inspection and rerun.

All synthetic weights and observations are explicitly illustrative. The computations establish algebraic properties and failure modes, not fitted production performance.

## Sources

### Repository, pinned at e609aebee78712dd26c2eae040005e6dcf2377f3

[R1] [QuotaAccountingModel.cs](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Core/Services/Accounting/QuotaAccountingModel.cs). Interval ridge, marginal features, first-20 fit and envelope-intersection evaluation.

[R2] [QuotaCostObservationBuilder.cs](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Core/Services/Accounting/QuotaCostObservationBuilder.cs). Compatible intervals, measurement assumptions, chronology and saturation exclusion.

[R3] [QuotaPaceModels.cs](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Core/Services/Forecast/QuotaPaceModels.cs). Existing pace candidates and elapsed-time weighting.

[R4] [CodexForecastFeatureBuilder.cs](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Core/Services/Forecast/CodexForecastFeatureBuilder.cs). Content-free feature projection and model/effort marginal shares.

[R5] [TtWorkloadBasis.cs](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/src/TajsTokens.Core/Models/TtWorkloadBasis.cs). Five pooled category weights, hashing and support gates.

[R6] [Layered statistical design](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/docs/source-notes/TT_LAYERED_STATISTICAL_DESIGN.md). Existing hurdle, frozen-basis and vector-versus-scalar proposals.

[R7] [Consolidated audit register](https://github.com/tajemniktv/TajsTokens/blob/e609aebee78712dd26c2eae040005e6dcf2377f3/.codex/docs/AUDIT.md). Dated implementation boundaries, not newly rerun runtime results.

### Primary documentation and research

[S1] OECD/European Union/EC-JRC (2008), [Handbook on Constructing Composite Indicators](https://www.oecd.org/en/publications/handbook-on-constructing-composite-indicators-methodology-and-user-guide_9789264043466-en.html), particularly weighting and sensitivity, printed pp. 31–34. Methodological analogy; the handbook does not validate TT.

[S2] OpenAI, [ChatGPT Rate Card: credit-based pricing](https://help.openai.com/en/articles/11481834-chatgpt-rate-card-business-enterpriseedu-credit-based-pricing), consulted 27 October 2026. Scope, speed multipliers, shared usage and pricing notes.

[S3] OpenAI, [Using Codex with your ChatGPT plan](https://help.openai.com/en/articles/11369540-using-codex-with-your-chatgpt-plan), consulted 27 October 2026. Shared allowance, reset and active-turn continuation semantics.

[S4] OpenAI, [Enterprise token-based rate card](https://help.openai.com/en/articles/20001415-chatgpt-rate-card-enterprise-token-based-pricing), consulted 27 October 2026. Billing-route and context/speed distinctions. Not a statement of the user's personal billing contract.

[S5] Stan Development Team, [Problematic Posteriors](https://mc-stan.org/docs/stan-users-guide/problematic-posteriors.html), collinear regression predictors and mitigation. Used for the general identifiability distinction; the numerical examples here are independently derived.

[S6] Stan Development Team, [Measurement Error and Meta-Analysis](https://mc-stan.org/docs/stan-users-guide/measurement-error.html), rounding section. Interval likelihood and latent-measurement formulations.

[S7] Casini, Garulli and Vicino (2017), [A linear programming approach to online set membership parameter estimation for linear regression models](https://doi.org/10.1002/acs.2701), International Journal of Adaptive Control and Signal Processing 31(3), 360–378. Public abstract consulted; the package implements its own elementary illustrative LPs, not the paper's online algorithm.

[S8] Willemain, Smart and Schwarz (2004), [A new approach to forecasting intermittent demand for service parts inventories](https://doi.org/10.1016/S0169-2070(03)00013-X), International Journal of Forecasting 20(3), 375–387. Public abstract consulted; used as methodological precedent, not as evidence that its procedure works for Codex.

[S9] Hyndman and Athanasopoulos, [Forecasting: Principles and Practice, regression forecasting](https://otexts.com/fpp3/regression.html), ex-ante/ex-post and scenario sections.

[S10] Stan Development Team, [Truncated or Censored Data](https://mc-stan.org/docs/stan-users-guide/truncation-censoring.html). Retaining censored observations without fabricating their exact values.

[S11] Barber, Candès, Ramdas and Tibshirani (2023), [Conformal Prediction Beyond Exchangeability](https://www.stat.cmu.edu/~ryantibs/papers/nexcp.pdf). Exchangeability violations, dependence/drift and qualified coverage extensions.

[S12] Gibbs and Candès (2021), [Adaptive Conformal Inference Under Distribution Shift](https://papers.neurips.cc/paper/2021/hash/0d441de75945e5acbc865406fc9a2559-Abstract.html), NeurIPS 34. Long-run coverage frequency is distinct from pointwise conditional guarantees.

[S13] Hyndman and Athanasopoulos, [Forecasting: Principles and Practice, forecaster's toolbox](https://otexts.com/fpp3/toolbox.html), rolling-origin evaluation and predictive assessment.
<!-- END VERBATIM SUPPLIED RESEARCH.md -->
