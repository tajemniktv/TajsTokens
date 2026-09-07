# TajsTokens agent instructions

## Authority

Read [`PROJECT.md`](PROJECT.md) before making product, telemetry, persistence, provider-model, or architecture decisions.

`PROJECT.md` is the sole authority for current product/data architecture and planning during the foundation reset. Existing code is implementation evidence, not specification. Files under `docs/archive/` are historical only and must not be used to resurrect old architecture or roadmap assumptions.

## Current product direction

TajsTokens is a **Codex-first observability and intelligence application**. The product goal is a coherent, trustworthy, production-quality local view of Codex: current quota and usage, source-native history, threads/workspaces/subagents, diagnostics, and evidence-backed predictions that help the user understand what is happening and what is likely to happen next.

The near-term priority is still to understand and expose Codex richly from trustworthy local evidence. Predictive or inferential features sit above that evidence; they must not weaken source contracts or turn guesses into stored facts.

Keep possible future providers in mind when choosing boundaries, but do not turn current Codex work into a generic-provider framework unless a concrete need requires that abstraction.

The architectural shorthand is:

> **Codex-first, not Codex-shaped.** Preserve rich Codex-native semantics. Future providers get their own native models. Shared abstractions are projections only where semantics genuinely overlap.

A useful product-level completion test is that normal Codex observability workflows should be useful without requiring raw SQLite/JSONL archaeology, while the raw explorers remain available for investigation and evidence work.

## Reset rules

- Do not introduce new semantic claims unless they are supported by an empirical source contract in `PROJECT.md` or work explicitly being added to it.
- Do not treat current UI labels, database columns, service names, tests, comments, table names, or field names as proof that a metric or relationship means what it appears to mean.
- Keep raw acquisition, source contracts, normalization, durable evidence, and read-model policy conceptually separate.
- Preserve source provenance, source-native identity, missing fields, ambiguity, and conflicting observations instead of silently resolving them during ingestion.
- Do not add new roadmap/phase/architecture documents. Update `PROJECT.md`.
- Do not delete working implementation merely because it is pre-reset. Change or remove it only when reset work establishes a reason.
- **Do not confuse local inspection with durable collection.** TajsTokens may show a user raw/content-bearing data exposed by their local Codex installation when that is useful for observability. That does not automatically authorize copying the same payload into TajsTokens' durable evidence store.
- Durable duplication of prompts, reasoning text, source-code bodies, credentials, authentication material, and similar sensitive payloads requires an explicit design decision and source-specific justification.
- Any export path is a separate privacy boundary. Make it explicit what leaves the machine; provide sanitization/redaction where appropriate; never silently convert a raw view into a lossy export or silently upload source data. If raw export is supported, it must be an explicit user choice.
- Fixtures, bug reports, documentation examples, and other artifacts intended to leave the user's machine must be sanitized or deliberately reviewed before sharing.
- Secret-bearing values require special handling even if their source is locally inspectable.
- External application data and credentials are read-only unless a future source contract explicitly establishes otherwise.
- Source explorers and raw inspection tools are acquisition aids. Their output does not become product truth merely because it came from a Codex-owned database or API.

## Provider and schema rules

- **Do not force Codex into a lowest-common-denominator provider schema.** If Codex exposes a useful, safe, contracted concept or relationship, preserve it even if a hypothetical future provider has no equivalent.
- **Do not force future providers into the Codex ontology.** A future provider may use runs, requests, generations, workers, queues, hardware metrics, or entirely different concepts. Give it a provider-native model rather than manufacturing Codex threads/turns/subagents.
- **Normalized does not mean generic.** Provider-native normalized observations are preferred when they reflect the evidence honestly.
- Shared infrastructure may unify provenance, capture/observation timestamps, source identity, contract/version metadata, persistence mechanics, and query plumbing. It must not require one universal semantic payload.
- Shared cross-provider concepts belong primarily in read-model projections and must be justified by demonstrated semantic overlap, not by similar names.
- If a read model maps a source-native value into a broader presentation category, retain the native value and treat the broad category as derived policy. For example, do not replace a provider's exact state with a generic `Running`/`Failed` classification and then persist the classification as evidence.
- Provider-specific UI and read models are first-class. Codex views may be substantially richer than future shared views.
- Do not discard a safe, understood source-native field merely because no current UI renders it. Also do not persist a field merely because it exists: its semantics and retention posture still need a contract.
- Avoid speculative provider capability matrices, plugin systems, or generic base classes whose only justification is possible future breadth. Prefer small seams that keep provider ownership explicit.

## Forecasting and intelligence rules

Prediction is active product scope. For quota forecasting, scenario planning, intelligence, or Overview prediction UX:

- Forecasts, confidence, classifications, attribution, and scenarios are **derived read-model policy**, never source evidence.
- Anchor current quota forecasts to provider-authoritative current observations; never borrow a current forecast from stale, non-authoritative, future-dated, or different reset generations.
- Keep reset epochs isolated. Historical evaluation must never use future observations or leak information across a reset/re-anchor boundary.
- Treat unchanged quantized quota-meter readings as censored/precision-limited observations, not proof of exactly zero burn.
- Prefer account-local learning from observed history. Do not invent a universal token-to-subscription-quota conversion.
- Candidate workload signals belong in production only when their semantics are supported and they improve out-of-sample prediction.
- Use walk-forward/backtesting for material algorithm changes. Compare sensible baselines and prefer the simplest approach that performs well.
- Evaluate remaining-at-reset error, exhaustion classification/calibration, ETA error, interval coverage, and sparse/stale/quantized behavior where relevant.
- Prediction intervals and probability/confidence labels must have a defined meaning. A heuristic score must not be presented as a calibrated probability.
- Degrade gracefully to simpler models or explicit learning/unknown states when evidence is insufficient. False precision is worse than an unavailable forecast.
- Persisted forecast history is derived and rebuildable; retain enough anchor, evaluation-time, model/policy-version, and provenance data to explain it.
- Use evaluation results to improve the actual product. Do not stop at an analysis harness if evidence supports a better production model.
- In Overview/intelligence UI, favor decision-useful outputs: reset survival/exhaustion risk, useful uncertainty, remaining-at-reset range, sustainable pace, regime context, freshness, and provenance.

Current EWMA, confidence heuristics, and account-local ridge scenario estimation are baselines, not architectural commitments. Replace or ensemble them when measured historical performance justifies it.

## Working style

Prefer coherent, evidence-backed changes that can be reviewed against captured evidence. Small source-focused changes are usually easiest to validate, but substantial refactors are appropriate when they remove duplicated policy, establish a cleaner ownership boundary, or materially improve a measured prediction path. New parsers or adapters should come with sanitized fixtures/probes covering observed variants and edge cases.

When TajsTokens code, observed runtime data, and upstream Codex implementation disagree, report the disagreement rather than forcing them to agree. The installed runtime is authoritative for what the user's installation actually emits; matching upstream source is corroboration and explanation of intended semantics, not permission to overwrite contradictory observation.

### Codex evidence workflow

Do not experimentally re-prove every relationship that Codex itself states explicitly in implementation, migrations, protocol types, and tests. Use the strongest practical evidence for the question:

1. observed runtime/source data from the installed Codex sources;
2. matching or near-matching `openai/codex` implementation, migrations, protocol types, and tests;
3. targeted controlled experiments for version-sensitive, desktop-only, ambiguous, or contradictory behavior;
4. inference only when explicitly labelled and harmless to the product claim.

When relying on upstream Codex source, record the commit/tag/version inspected and whether it is known to match the installed build. A source-code relationship may support a source contract when it is explicit and consistent with observed runtime data. Manual experiments are still required when the installed behavior differs, when source coverage is uncertain, or when a user-facing label would otherwise overclaim.

The public Codex repository may not contain every desktop/app subsystem. Absence from `openai/codex` is not evidence that an observed local source is invalid; keep desktop-only or otherwise unmatched sources runtime/schema-driven until matching implementation evidence is found.

When multiple Codex sources appear to describe the same concept, do not choose an authority by convenience. Preserve the observations and put any precedence/reconciliation rule in an explicit read-model policy after the relationship is sufficiently supported.

## Current repository

The present solution contains:

- `src/TajsTokens.App`
- `src/TajsTokens.Core`
- `src/TajsTokens.Infrastructure`
- `tests/TajsTokens.Core.Tests`

These are current implementation boundaries, not guaranteed target architecture.

The repository also contains a read-only Codex State DB Explorer. Treat it as acquisition/investigation tooling according to the boundary documented in `PROJECT.md`, not as permission to directly project raw private SQLite rows into product semantics. Local raw inspection, including content-bearing fields, is allowed by the product direction; durable retention and export remain separate decisions.

## Build and validation

Full WinUI validation is Windows-specific.

```powershell
dotnet restore TajsTokens.sln
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj -c Debug
dotnet build TajsTokens.sln -c Debug
```

For focused changes, use targeted tests while iterating when they answer a concrete question. Do not rerun the full suite after every small edit. At a sensible milestone and before completion, run the full Core test suite and Windows application build; algorithm work should also run its relevant backtests/evaluation.

## References

The local read-only clone of the Codex CLI source is at `E:\dev\codex`. Use it as a first-class corroborating reference for Codex semantics, together with the installed runtime evidence. Do not add it as a TajsTokens dependency or submodule.
Documented SQLite schema for Codex's CLI is at docs\source-notes\CODEX_SQLITE_SCHEMA.md