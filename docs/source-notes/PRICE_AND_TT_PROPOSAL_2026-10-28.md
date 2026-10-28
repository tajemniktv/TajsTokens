# Price-catalog and public TT choices — proposed, not active

This makes PROJECT.md decisions DEC-001/T-01, DEC-002 and DEC-005 concrete. No daily
setting, active price source, TT basis or historical score has been changed.

## Price data and refresh policy (DEC-002)

Recommend an **official-snapshot import first**: independently versioned numeric
catalog files, curated from official pricing/model pages, accepted by an explicit
import rather than a new app build. No official structured feed has been selected
or verified for unattended refresh in this work. This trades automatic network
refresh for a traceable initial source and does not prevent adding a selected feed.

Proposed policy:

- Explicit imports validate schema, USD/per-million-token units, exact model,
  product, speed tier, context bracket and cache-write lifetime. Unknown coordinates
  and missing categories remain unpriced; no model-name guessing or silent zeros.
- Official snapshots, user overrides and fallbacks have separate identities and
  labels. A selected versioned override takes precedence only for its explicit
  coordinates/dates. Community and legacy fallbacks are disabled unless explicitly
  selected; the existing pinned card remains available as a labelled legacy source.
- New valid imports replace the selected current catalog and retain prior snapshots.
  Bad/missing imports retain last-known-good data with failure/stale state. A proposed
  **30-day age label** requests refresh without pretending old data is freshly read.
- Source effective dates are unknown unless explicitly supplied. Retrieval/curation
  time does not establish historical applicability. “Current reference” revalues
  the same native counts under the named snapshot; date-effective valuation needs
  actual applicable dates and supported observation times. Neither is an invoice.
- Price changes never mutate frozen TT weights, select a new ruler or restate old
  TT scores. There is no automatic community feed or network polling in this first
  policy; an automatic feed remains a separate source choice.

The [candidate numeric snapshot](price-reference-2026-10-28.candidate.json) contains
only the five exact model names in the retained month's selected ledger. These are
standard/short-context, 30-minute-cache-write **reference** rates, in USD per million
tokens; they do not infer the actual service tier, request length, cache lifetime,
region, modality or tools. The output rate weights both disjoint output categories.

| Model | Uncached input | Cache read | Cache write (30m) | Each output category | Source |
|---|---:|---:|---:|---:|---|
| gpt-6-astra | 10 | 1 | 12.5 | 50 | [Official model](https://developers.openai.com/api/docs/models/gpt-6-astra) |
| gpt-6.1-sol | 2 | 0.10 | 2.5 | 10 | [Official model](https://developers.openai.com/api/docs/models/gpt-6.1-sol) |
| gpt-6.1-terra | 1 | 0.05 | 1.25 | 5 | [Official model](https://developers.openai.com/api/docs/models/gpt-6.1-terra) |
| gpt-6-sol | 2 | 0.20 | 2.5 | 10 | [Official model](https://developers.openai.com/api/docs/models/gpt-6-sol) |
| gpt-6-luna | 0.10 | 0.01 | 0.125 | 0.50 | [Official model](https://developers.openai.com/api/docs/models/gpt-6-luna) |

The [pricing page](https://developers.openai.com/api/docs/pricing) and
[cache policy](https://developers.openai.com/api/docs/guides/prompt-caching) distinguish
30-minute and 12-hour writes. The latter has a different rate; writes are a separate
input category rather than an added ordinary-input charge. Prices and policy were
inspected October 28, 2026. Effective dates remain null in this snapshot.

## Public TT convention and activation (DEC-001/T-01, DEC-005)

Recommend **TT-2026-10-28 standard reference**:

- **One TT is one million uncached GPT-6.1 Sol input tokens** under the frozen
  candidate schedule. Defining the basket requires no paid synthetic task.
- Weights are the table above, with no extra effort multiplier. Model/category
  pairing stays joint; reasoning and non-reasoning output are disjoint and receive
  the same published output weight.
- Apply standard speed, short context and 30-minute cache writes as the ruler's
  declared reference convention, irrespective of the workload's actual execution
  settings. Those settings are not inferred from model capacity or requested tier.
- For weights expressed per million tokens,
  `TT = sum(native_tokens[model,category] * frozen_weight) / 2,000,000`.
  The common price scale cancels. TT is a reference-consumption index, with no claim
  to remaining allowance, billed dollars, physical compute or successful work.
- Unknown models/categories yield supported subtotals/bounds as available, preserving
  native counts. They never receive an implicit zero or universal model multiplier.
- Activation selects this exact basis ID explicitly. Additions require a new,
  explicitly selected compatible extension preserving old weights and basket.
  Material weight/basket/convention changes create a new ruler. Original scores
  stay intact; a historical restatement is separately requested and linked.

The [serialized candidate basis](tt-reference-2026-10-28.candidate.json) has ID
`tt-joint:00dac6d4779ceff3507bce2570ac4a7fe30876e53e0fc033d1ae41540a46f6fa`.
Its source is the exact candidate price artifact, SHA-256
`d2a13f4e571b6d72557b08191ee13802e67089f8b676a171eaecfd7cb2914026`.

## Read-only preview and limits

The installed `--score-joint` tool validated the saved identity/normalizer and scored
the selected local ledger from 2026-09-27 00:00 through 2026-10-27 14:58 UTC:

- 20,078 selected events, 15 model/effort coordinate rows and 3,984,535,029 native
  tokens.
- **608.26452665 proposed TT**, exact under the candidate and supplied constraints.
  No unmapped or category-inconsistent rows occurred in this selection.
- All five observed model names are supported by this candidate. This does not
  establish account-wide collection completeness or support for other models.
- The old pinned API card covers Astra among these names; the new snapshot would
  improve reference-price coverage without rewriting the original token ledger.

Reproduce with the installed evaluation executable or the development command:

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation/TajsTokens.ForecastEvaluation.csproj -c Debug --no-build --no-restore -- --score-joint "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db" docs/source-notes/tt-reference-2026-10-28.candidate.json 2026-09-27T00:00:00Z 2026-10-27T14:58:00Z
```

This preview is a deterministic convention check. It does not validate TT as a
predictor of quota, compare model task quality, establish current entitlement or
recover actual billing conditions. Current quota conversion keeps the full native
vector and still needs its separate calibration evidence.
