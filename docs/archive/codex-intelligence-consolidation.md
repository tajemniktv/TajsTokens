# Active implementation contract — 2026-09-20

User requested full implementation of ChatGPT conversation 6aafe793-1f60-83eb-927b-669000b44d86, "Define TajsTokens Features". Read all eight turns; latest proposal and telemetry/reconciliation requirements define scope. Examples/numbers are illustrative, not measurements. Current HEAD before work f36ddb2, clean.

Acceptance:
- One Codex product surface with shared time/account/model/project/thread context; preserve advanced tools and thread drilldowns.
- One typed CodexIntelligenceSnapshot / analytical ledger above existing owners, not a replacement ingestion database or one giant regression.
- Four inference responsibilities: workload, accounting, regime, forecast. TT/API equivalent/provider credits remain distinct derived/reported quantities. Reuse current algorithms and preserve conservative production gates.
- Reproducible inference manifest covering actual algorithms, scoring/rate bases, evidence policy and selection; don't claim a policy hash alone reconstructs fitted coefficients.
- Retrospective within-cycle residual/regime analysis separate from live promotion; distinguish periodic variation, structural candidates, measurement uncertainty and missingness; chronological evaluation and negative controls.
- Recheck newly reported provider capabilities with read-only fixed-origin authenticated adapter; no refresh/writes to credentials. Screenshots show weekly history and per-chat weekly allowance but do not establish endpoint/auth availability.
- Typed immutable provider period/task observations, actual capture and as-of times, completeness and revisions; shadow-only calibration eligibility.
- Reconciliation: compatible live quota vs historical periods, exact thread joins, dimensional conservation, local vs provider tokens; no cross-denominator sums or root/descendant double counts.
- Meaningful observed/reported/derived/estimated labels, unknown distinct from zero; provisional/delayed/incomplete/revised/conflicting/unavailable diagnostics, not invented confidence percentages.
- Update PROJECT/README/DEVELOPMENT, batched full tests/build, native UI checks, commit/push. No new broad research loop. Cannot claim source availability or empirical superiority absent evidence.

Execution order: consolidate derived contract/UI first; provider capability probe to settle implementation inputs; integrate usable analytics into same owner; retrospective regime analysis; validation/acceptance. Preserve existing source privacy/accounting.

Known owners: IIntelligenceService/SqliteIntelligenceService; TelemetryCoordinator latest/current forecast authority; CodexServerEvidenceService and JSON evidence storage; CodexPage existing thread browser; UsagePage local filter view; ForecastsPage planner/research; MainWindow navigation. Shared IntelligenceQuery currently filters model/repository/thread but not account. Dataset reader account param filters quota only, NOT local workload. Do not silently attribute local workload to selected account.

Implementation now present (uncommitted): shared intelligence ledger/snapshot/UI, task/plan typed opt-in adapter and shadow reconciliation, retrospective within-cycle regime detection, exact chat/account joins, unified tray/alerts entry, persisted numeric inference artifacts/manifests. Reset ForecastingService folded into QuotaPredictionService.ResetOutlook; unused interface/helpers removed; all source/test/CLI callers migrated. Active baselines retained. Provider credential bracket still failing; stage-qualified probe pending. No source credentials written.

Validation: 540 Core tests passed, app build 0 warnings/errors deployed 20260920T153121763Z-bdaffec2 PID31116. Final small review fixes (unknown-account failure visibility, stored bucket hydration, UI manifest refresh, CLI caller) require final batch now running. Native background accessibility tree confirmed new Codex page but screen capture showed foreground game: do not steal focus or automate user input while user is playing; visual/interactive acceptance remains open. Engine live read found5944 canonical events,10285 quota observations,130 accounting score rows,0 supported retrospective candidates. Latest execution handle is in conversation tools; no agents. Final steps: final batch, source outcome/docs, bounded live text check, stage/review/commit/push.

Completed milestone: committed and pushed ee7f1c4 to origin/main; clean working tree, HEAD...origin/main 0/0. Final 540 tests passed, CLI/app build 0 warnings/errors, deployed20260920T153704243Z-e90aefc2 PID7328. Final probe timed out on INITIAL /wham/usage identity bracket before plan/task calls; no report capability conclusion. Final live ledger5987 events/10336quota/130costscores/0regime candidates. No further automatic probes. Only remaining acceptance: actual provider payload access, explicit TT basis/promotion evidence, native interactive visual pass when it will not interrupt foreground gameplay. Current implementation status is PROJECT.md. No running tool sessions or agents remain.

Memory registry consulted MEMORY.md:42-44; current repo supersedes older status. Do not update memory. Existing persistent goal is blocked from prior work; do not change it or create one for this new request.
