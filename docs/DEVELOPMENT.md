# Development and recovery

Run commands from the repository root. Contributor rules and telemetry/source boundaries are in [AGENTS.md](../AGENTS.md) and [PROJECT.md](../PROJECT.md).

## Build and test

```powershell
dotnet restore TajsTokens.slnx
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj -c Debug
dotnet build TajsTokens.slnx -c Debug
```

Run tests and builds serially when they share outputs. Use `--no-restore` after a successful restore. `dotnet build TajsTokens.slnx -c Debug -p:DogfoodEnabled=false` performs an isolated app build; CI and test-only builds do not deploy. Full WinUI validation requires Windows. SDK outputs are in ignored `artifacts/`, and task temporary files belong in `.codex/temp/`.

The Windows app build also builds the existing `TajsTokens.ForecastEvaluation` project for the app's RID and packages its executable, DLL, dependency manifest and runtime configuration. The calibration child uses the app's self-contained runtime and shared libraries; keep these entry files together with the app when publishing. It reads requests through private standard input, accesses only the app-owned database and numeric cache, and is killed by its owned Windows job when the parent exits. `data/calibration-cache.db` is disposable derived data; `telemetry.db` remains the retained evidence store.

To bring an already-running installed tray instance forward through the same activation handler:

```powershell
& "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/current/TajsTokens.App.exe" --open-dashboard
```

The signal is restricted to the current user/session and the same installation directory. With no matching instance, this starts the dashboard normally. `--background` starts tray-only. Neither flag changes persistent settings.

## TT selection, prices and explicit candidate scoring

Overview's Evidence tab owns price-file imports, explicit TT-basis selection and
"Save this TT calculation". The selected October 28 basis and official price
snapshot are embedded with their reviewed identities; first use records the actual
selection/import time. Imports never refresh TT weights. Optional overrides and
fallbacks are separately labelled and can be disabled there.

The installed evaluation executable offers the same operations:

```powershell
$referenceTool = "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/current/TajsTokens.ForecastEvaluation.exe"
$referenceDb = "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db"
& $referenceTool --reference-init $referenceDb
& $referenceTool --reference-import $referenceDb official path/to/prices.json
& $referenceTool --select-tt $referenceDb path/to/basis.json
& $referenceTool --reference-view $referenceDb 2026-09-27T00:00:00Z 2026-10-27T14:58:00Z
```

`--reference-init` initializes approved defaults only when no selection exists;
it never replaces an existing or damaged selection. Imports accept `official`,
`override` or `fallback`; the latter two explicitly enable that role. Mutating
commands require an existing app-owned database. `--reference-view` is read-only
and does not initialize missing selections. Bad imports return exit 2 and retain
last good data. Current reference and date-effective amounts stay separate; unknown
effective dates stay unpriced. See the [public reference contract](source-notes/STAGE4_PUBLIC_REFERENCE.md).

Price-file preview is also explicit and read-only; its final argument is the caller's
source-age threshold, not an activated freshness policy:

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation/TajsTokens.ForecastEvaluation.csproj -c Debug --no-build --no-restore -- --price-preview "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db" docs/source-notes/price-reference-2026-10-28.candidate.json 2026-09-27T00:00:00Z 2026-10-27T14:58:00Z 30
```

`--price-import <initialized-app-db> <source-key> <file>` writes only the price archive
and attempt state, with no activation; `--price-source <app-db> <source-key>` reads it.
It rejects active source keys: use `--reference-import` for a selected source so role
validation is applied. Use isolated app-owned fixtures for experiments. Bad imports
return exit 2 and retain last good data. See the [price snapshot contract](source-notes/STAGE4_PRICE_CATALOG.md).

To inspect retained model discovery alongside selected local observations, use:

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation/TajsTokens.ForecastEvaluation.csproj -c Debug --no-build --no-restore -- --model-catalog "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db" 2026-09-27T00:00:00Z 2026-10-27T14:58:00Z > .codex/temp/model-catalog.json
```

It reads app-owned metadata and selected native history without a new source,
credential or model request. The output labels account access unverified. Collection,
versioning and recovery are documented in the [model catalog contract](source-notes/STAGE4_MODEL_CATALOG.md).

After building the evaluation tool, supply a serialized `TtJointWorkloadBasis`
definition with its computed `BasisId` and `ReferenceScore`:

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation/TajsTokens.ForecastEvaluation.csproj -c Debug --no-build --no-restore -- --score-joint "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db" .codex/temp/candidate-basis.json 2026-09-27T00:00:00Z 2026-10-27T14:58:00Z > .codex/temp/candidate-joint-score.json
```

This command reads app-owned selected native increments and writes aggregate JSON
to standard output. It does not activate the candidate, save scores into daily data
or infer account-wide completeness. Explicit activation uses `--select-tt`; previews
alone never select a basis. Exact,
bounded, partial, unbounded and inconsistent results retain their distinct meanings;
see [the scoring contract](source-notes/STAGE4_JOINT_SCORING.md). A definition larger
than 512 KiB or a future/range longer than 366 days is rejected. Use Ctrl+C to cancel.

## Joint quota-cost identification

The Evidence tab's explicit accounting analysis includes bounded joint-design
diagnostics. The standalone read-only report avoids fitting the research models:

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation -c Debug --no-build --no-restore -- --cost-identification "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db" 2026-09-27T00:00:00Z 2026-10-27T14:58:00Z
```

`--cost-identification-audit` uses the same arguments and additionally emits numeric
matrices as JSONL for independent checks. Both commands restrict ranges to 366 days,
keep cohort identities aliased and make no selection/write. Do not sum horizon rows
as independent work. [Methods, retained results and limits](source-notes/STAGE5_IDENTIFICATION.md).

## Forecast threshold study

Run this explicitly against the app-owned telemetry database, with a fixed past UTC range:

```powershell
dotnet build tools/TajsTokens.ForecastEvaluation/TajsTokens.ForecastEvaluation.csproj -c Debug
dotnet run --project tools/TajsTokens.ForecastEvaluation/TajsTokens.ForecastEvaluation.csproj -c Debug --no-build --no-restore -- --threshold-study "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db" 2026-09-27T00:00:00Z 2026-10-27T14:58:00Z > .codex/temp/threshold-study.json
```

The study opens telemetry read-only, writes aggregate JSON to standard output and progress to standard error, and supports Ctrl+C. It does not collect data, write calibration results to telemetry, change installed settings or promote a model. The range must be ordered, in the past and no longer than 366 days. Results include dataset identity, explicit configurations, cohort aliases, split boundaries, error/availability metrics and unsupported reasons. They contain no raw account IDs, project paths or chat content. Keep the full local JSON with useful research artifacts; preserve conclusions and reproducibility details in source notes.

The final fifth of candidate origins is reserved for comparison after choosing configurations on earlier matured outcomes. Report fixed-horizon, near-reset, scenario and composed-selection results separately. Composed availability is a conservative replay lower bound on constructed targets, not a measured live abstention rate. No source, identity, overlap, freshness or collection-time safeguard is tuned. Default evaluation options reproduce the released policy. See [the retained-data study](source-notes/FORECAST_THRESHOLD_STUDY.md) for observed limitations and interpretation.

For a faster current-policy session-scenario eligibility report, use `--eligibility`
with the same database and UTC range. It shares the study's origin selection,
development/final split, target lookup, feature builder and production scenario
selection. It reports sequential origin denominators, overlapping input reasons,
training interval/origin pairs and bounded numeric examples. Missing acquisition
denominators are null: this retained dataset cannot reconstruct source discovery,
decoding failures or the reader's excluded untimed rows. Outcome availability is a
separate branch from point eligibility. The command opens telemetry read-only and
does not rerun the 17-configuration or composed-selection sweep.

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation/TajsTokens.ForecastEvaluation.csproj -c Debug --no-build --no-restore -- --eligibility .codex/temp/stage1/worker-fixture/telemetry.db 2026-09-27T00:00:00Z 2026-10-27T14:58:00Z > .codex/temp/stage2/eligibility-frozen.json
```

Use an existing app-owned numeric snapshot or substitute the installed telemetry
path above; the fixture path is local and is not distributed. The full study now
identifies itself as `forecast-threshold-study/v3-eligible-pace`; eligibility is
`forecast-eligibility/v2-pace-boundaries`. V2 split the formerly combined workload
reason; the current versions also preserve intervening plan/window boundaries and
use the repaired production pace eligibility. Historical v1/v2 artifacts retain
their original policies and results.

## Pace validation

Use the same explicit past UTC range with `--pace-validation`:

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation/TajsTokens.ForecastEvaluation.csproj -c Debug --no-build --no-restore -- --pace-validation .codex/temp/stage1/worker-fixture/telemetry.db 2026-09-27T00:00:00Z 2026-10-27T14:58:00Z > .codex/temp/stage3-pace-validation.json
```

Substitute an existing app-owned snapshot or the installed telemetry path; no
fixture is distributed. This read-only report compares predefined pace candidates
at 5/15/30/120-minute, eligible 24-hour and near-reset targets, reserving the final
fifth of origins for later-period comparison. Fixed-horizon rows are arithmetic
candidate comparisons, rather than the complete workload/composed selector;
`production-reset` replays the actual repaired reset selector. Local active/burst/
quiet/missing-evidence states and reset-lead partitions remain separate. They do
not assign local work to the quota account or prove idleness.

Point/band availability, missing targets, signed error, MAE/RMSE, missed observed
movement and non-overlapping support have explicit denominators. Positive signed
error is optimistic remaining allowance. Use `overall` rows for cohort-wide
support; do not add their counts to partitions or sum non-overlapping support across
state/lead groups. The command changes no observations, settings or selected model.
See [the Stage 3 evidence](source-notes/STAGE3_PACE_VALIDATION.md) for assumptions,
current results, policy versions and remaining limits.

## Ingestion performance fixtures

The existing evaluation executable also provides local synthetic ingestion benchmarks:

```powershell
dotnet build tools/TajsTokens.ForecastEvaluation/TajsTokens.ForecastEvaluation.csproj -c Debug
dotnet run --project tools/TajsTokens.ForecastEvaluation -c Debug --no-build --no-restore -- --benchmark-token-writes .codex/temp/ingestion-bench 10000 > .codex/temp/token-writes.jsonl
dotnet run --project tools/TajsTokens.ForecastEvaluation -c Debug --no-build --no-restore -- --benchmark-token-writes .codex/temp/ingestion-bench 1000 --reverse > .codex/temp/token-writes-reverse.jsonl
dotnet run --project tools/TajsTokens.ForecastEvaluation -c Debug --no-build --no-restore -- --benchmark-ingestion-prefix .codex/temp/ingestion-bench > .codex/temp/ingestion-prefix.jsonl
```

The write fixture compares per-observation transactions with ordered batches of 128
through the same current reducer, SQLite policy and revision triggers. It checks token
conservation on initial ingestion and duplicate replay. `--reverse` swaps measurement
order to expose warmup effects. Counts are limited to 1–100,000. The prefix fixture
reports cold, unchanged, append and consumed-prefix-rewrite work; complete-record
bytes exclude incomplete trailing data. These logical counters are not physical disk
I/O. Neither command opens native Codex data or generates provider traffic. Each uses
a fresh child directory under the supplied output root and removes only its own
fixture after completion. Keep aggregate JSON and qualify hardware/warmup differences;
these are not timings of an old released binary. See [Stage 1 evidence](source-notes/STAGE1_RESOURCE_VALIDATION.md).

## Resource measurements

Use the existing sampler against an installed app, without UI interaction:

```powershell
./tools/performance/Measure-AppResources.ps1 -Seconds 35 -IntervalMilliseconds 1000 -PhysicalDiskCounters -OutputPath .codex/temp/stage1/resources.json
```

It qualifies the app installation and includes its observed child tree, separating
calibration workers and transient helpers. PID creation times prevent mixing
reused PIDs. Combined private peaks use one sample cycle. CPU/I/O for a child
covers its observed lifetime only. Optional PhysicalDisk counters are system-wide;
they cannot attribute physical bytes to this app. Process transfer and prefix hash
counters are logical work. Record visibility, scenario, build and sampling limits
separately. See [Stage 1 evidence](source-notes/STAGE1_RESOURCE_VALIDATION.md) for
phase tracing, the offline reporter and current measurements.

## Restrained joint-cost research

For explicit restrained-joint research on an ordered past range up to 366 days:

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation -c Debug --no-build --no-restore -- --joint-cost-study "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db" 2026-10-22T00:00:00Z 2026-10-28T16:40:00Z
```

`--joint-composed-study` and `--joint-composed-strict` use the same arguments for
reconstructed and collection-qualified future-work replay. These read-only commands
do not select a live model. Cost JSONL includes private derived numeric observations
and model labels for reproduction, with account/session/path identifiers omitted;
review it before sharing. Compare matched targets, availability and provisional
blocks, not just mean losses. See [Stage 5 evidence](source-notes/STAGE5_IDENTIFICATION.md).

`--quota-support-study` uses the same database/range arguments for bounded
conditional scenario identification. It inspects the frozen first-20 prefix's
first positive mixture and the latest retained mixture under explicitly labelled
external-activity sensitivity cases. It does not choose those assumptions for live
Planning. More than four active joint coordinates remain unsupported; infeasible,
unbounded and numerically uncertain results are preserved. Source notes document
the declared numerical tolerance and independent exact-rational checks.

## Recent-work planner checks

Open Overview → Planning. Recent work continues the selected elapsed context into
conditional native tokens/TT, alongside an independent account-pace fit check.
Changing model/effort offers same-token reference revaluation; it must not claim
changed-task quota fit. Session counts · comparator retains counts, model/effort
history matching and the separate history lookback. A reset-crossing plan must not
claim fit against post-reset capacity. Details belong under the plan disclosure.
Check mode/context/input restoration after hiding the dashboard for over 30 seconds;
the user performs host UI checks. See [contracts and numerical evidence](source-notes/STAGE6_CONTINUATION.md#recent-work-planning-integration-fore-003).

## Recent joint continuation research

To inspect the fixed 30-minute/two-hour joint continuation context comparison:

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation -c Debug --no-build --no-restore -- --continuation-contexts-strict "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db" 2026-10-22T00:00:00Z 2026-10-28T16:40:00Z
```

`--continuation-contexts` gives reconstructed replay instead. These read-only
reports keep availability, matched targets, local-token errors and composition
errors separate. Neither chooses a released context default. See the
[continuation contract and fixed comparisons](source-notes/STAGE6_CONTINUATION.md).

`--joint-path-study` / `--joint-path-strict` use the same database/range arguments
for explicit joint-path quota research. They retain the direct baseline and show
input/fit compatibility separately from experimental errors. Match exact target
keys across alternatives before comparing means. `--joint-path-audit` accepts a
database, past UTC origin, context hours and horizon hours to reproduce one finite
64-path ensemble. These commands neither select a live model nor turn raw path
quantiles into calibrated intervals; audit files stay private until reviewed.

## Daily installation

A successful default local app build publishes, installs and restarts `%LOCALAPPDATA%\Programs\TajemnikTV\TajsTokens\current`. Persistent database/settings live in sibling `data`; the prior binary is kept in `previous`, and deployment backups are in `backups`. The build does not delete `data`.

```powershell
# Explicitly deploy the current publish, or roll back the binary without downgrading data:
powershell -NoProfile -ExecutionPolicy Bypass -File tools/dogfood/Deploy.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/dogfood/Deploy.ps1 -Rollback

# Validate deployment behavior after changing it:
powershell -NoProfile -ExecutionPolicy Bypass -File tools/dogfood/Test-Deployment.ps1
```

Binary rollback does **not** restore an older database schema. Stop the app and preserve the current `data` folder before restoring a matching data backup. Consult [the deployment scripts](../tools/dogfood/README.md) for the exact staging, hash-verification and recovery contract.

## Source and integration boundaries

Codex native files and credentials are read-only. The app's local database owns retained evidence; no external collector should become a second authoritative Codex token ledger. The [local limits API](LOCAL_LIMITS_API.md) serves already-collected current quota on authenticated loopback only. [Remote SSH fleet probes](REMOTE_FLEET.md) are explicit, read-only, content-free status checks. They retain local status snapshots but do not synchronize token/quotas or transcripts. Avoid writing prompts, command bodies, account credentials or API tokens to shared reports.

For machine-readable local workload totals, run:

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation -- --daily-json "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db" 30
```

The optional final number is 1–366 UTC days. Output is `tajstokens.daily-tokens.v2`
JSON with selected local native increment counts and disjoint token categories by
verified source UTC day. Legacy observed time and collection time do not substitute
for a missing/malformed/unverified source date. Explicit zero observations remain
distinct from unavailable dates. Missing days mean no retained observations,
**not** verified zero use.

The separate `allTimeCoverage` object covers the entire selected local native
ledger, outside the requested date range. It reports increment counts and amounts
with verified/unverified time and missing model metadata; the latter groups can
overlap. It excludes retired snapshots and original-input interpretation variants,
which are evidence rather than additive increments. Known occurrence replay and
representation equivalence prevent duplicates within their proven scope;
unverified cross-source/global equivalence remains unresolved. Observatory's
all-time token card exposes the same coverage. Fleet's recent token count uses
verified source dates within its explicit snapshot bounds.

The report opens the app-owned database read-only, refuses unsupported observatory
schemas instead of inventing legacy dates, and requires schema 9 through the current
supported component version. It excludes paths, prompts, session IDs and account
keys; it does not attribute local work to the current Codex account or include
remote hosts. V1 exports remain historical artifacts with their former date basis.
