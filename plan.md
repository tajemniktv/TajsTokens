# TajsTokens delivery plan

**Updated:** 2026-09-08

**Stage:** active product integration and hardening, after the foundation reset.

**Status:** this is the delivery plan, not a claim that the work below is complete.

## Purpose and authority

Deliver the product end state in [PROJECT.md](PROJECT.md): a trustworthy, comfortable local Windows companion for understanding Codex activity, history, quota, and likely future behavior without routine SQLite/JSONL archaeology.

`PROJECT.md` owns product and architecture decisions and source contracts. [AGENTS.md](AGENTS.md) owns contributor instructions. This plan owns implementation sequence, dependencies, and acceptance gates. When implementation reveals a new semantic or retention decision, update `PROJECT.md` rather than quietly making the checklist the specification.

## Starting point

The repository already has native source readers/explorers, thread read models, rollout ingestion, token accounting, owned quota history, forecasting/backtesting, and WinUI product views. Build on those owners; this is not a greenfield rewrite.

Recent work establishes safe workload metadata collection, historical effort repair, authoritative-target evaluation, source-isolated quota-burn intervals, and clearer forecast/omitted-window UX. The dated evaluation in `PROJECT.md` still has sparse authoritative outcomes; it does not prove that advanced models or calibrated probabilities are ready. Recent UI changes have automated/build evidence, while the user has reserved visual acceptance for themselves.

The following workstreams are **open acceptance scopes**. They may reuse substantial existing implementation. First inspect what already satisfies each gate; do not recreate it or label an entire workstream complete from one passing helper test.

## Target arrangement

```text
Codex SQLite / JSONL / app-server
             |
     source-specific readers
             |
             +--> on-demand local inspection --> native detail / raw explorer
             |
             +--> selected safe observations --> TajsTokens-owned evidence
                                                        |
                                            reconciled read models
                                                        |
                                            product UI / intelligence
```

Our database preserves the history we need, not everything Codex can expose. Read models link sources through supported identities and explicit policy; ingestion does not erase disagreements. Derived caches can be rebuilt. Unique collected evidence must not be treated as disposable.

## 1. Make the integration map explicit

**Outcome:** every product-relevant data path has a known source, owner, retention decision, and meaning.

- [ ] Map the fields needed by Overview, work history, quota investigation, diagnostics, and predictions to existing source contracts and current readers/writers.
- [ ] For each candidate, classify it as direct inspection, durable evidence, or derived policy. Record decisions in `PROJECT.md`.
- [ ] Document native keys, source instance/generation, event and collection times, supported change signals, missing-field behavior, and known overlaps between SQLite and rollouts.
- [ ] Identify gaps that prevent a specific user workflow. Investigate those gaps, not every unused native table.
- [ ] Establish how installation history and authenticated quota account/profile scope are distinguished across account changes. Unknown associations remain unknown.

**Gate:** a reviewer can trace each planned user-facing claim to evidence and explain why it is retained, read directly, or derived. No blanket “SQLite wins” or “rollouts win” rule.

## 2. Complete selective, reliable collection

**Depends on:** the relevant decisions from workstream 1; unrelated established paths need not wait.

- [ ] Extend existing ingestion only for missing, justified historical observations. Do not add a raw database mirror or generic sync service.
- [ ] Verify source-qualified identities and atomic observation/checkpoint commits across normal refresh and replay.
- [ ] Cover append, truncation/replacement, interrupted batches, rescans, source disappearance/reappearance, duplicates, and schema changes with sanitized fixtures/probes.
- [ ] Define safe change detection for any selected SQLite observation; mutable rows and deletions are not automatically lifecycle events.
- [ ] Backfill eligible history without recounting tokens, inventing collection timestamps, or copying content-bearing payloads.
- [ ] Surface collection coverage, lag, unsupported sources, and recovery actions instead of silently returning empty data.

**Gate:** repeated collection is idempotent; interrupted collection resumes without loss or double counting; source files remain read-only; retained payloads obey the privacy contract. A backfill report distinguishes reconstructed history from collection-time proof.

## 3. Reconcile through provider-native read models

**Depends on:** the contracts and observations needed for each view.

- [ ] Link supported thread/turn/workspace/root/subagent identities without treating a missing parent as proof of a root agent.
- [ ] Keep current SQLite metadata distinct from historical turn metadata and observed lifecycle events.
- [ ] Verify accounting equivalence before comparing or deduplicating totals across sources; never sum overlapping counters.
- [ ] Retain source alternatives and expose selection rationale, freshness, scope, and conflicts in detail views.
- [ ] Keep source selection stable across refresh and paging; report truncation and non-snapshot-consistent reads.
- [ ] Consolidate duplicate presentation policy behind existing native read-model owners where a concrete duplicate exists.

**Gate:** disagreement fixtures remain inspectable, consistent fixtures produce useful unified views, and no later observation rewrites the evidence available at an earlier time.

## 4. Finish the daily product workflows

**Can progress alongside:** collection/reconciliation work wherever contracts are already established.

- [ ] Overview answers “what is happening now?” with reported windows, freshness, usage, and concise actionable messages.
- [ ] Work navigation connects workspaces, threads, turns, and supported subagent activity with useful search/filter/detail flows.
- [ ] Quota Burn separates observed movement from local activity and keeps sources distinct; Forecasts separates current context, saved projections, scenarios, and evaluation.
- [ ] Put technical provenance and methodology behind deliberate detail actions, not repeated paragraphs in every row.
- [ ] Verify loading, empty, missing, stale, failed, narrow-window, large-history, keyboard, and accessibility states.
- [ ] Preserve raw explorers and the explicit user-invoked CLI harness without making either a prerequisite for normal observability.

**Gate:** representative everyday questions are answerable without raw SQL/JSONL; diagnostics remain reachable. Automated/build checks and user-owned visual acceptance are recorded separately. Do not automate the visual pass while the user has reserved it.

## 5. Improve intelligence as evidence permits

**Depends on:** sufficient correctly scoped observations and leakage-safe features, not simply a large row count.

- [ ] Accumulate usable authoritative quota outcomes while preserving reset/account/source boundaries and omitted-window semantics.
- [ ] Evaluate five-hour and weekly behavior separately, including flat/quantized, sparse, stale, and changing-workload cases.
- [ ] Compare simple baselines and justified token/model/effort/activity/context candidates with chronological training and out-of-sample outcomes.
- [ ] Report actual fitted origins, independent reset generations, remaining-quota error, exhaustion/ETA evidence, and interval coverage—not just aggregate fit quality.
- [ ] Promote a more complex candidate only when comparable held-out results justify it; otherwise retain the simpler production model.
- [ ] Keep scenarios inside historical support and expose unknown/learning states. Calibrate probability labels before presenting them as probabilities.

**Gate:** production uses the evaluated policy, with reproducible inputs/version/provenance and honest support limits. Insufficient historical outcomes are an explicit evidence limitation, not a reason to manufacture a success metric or loop over the same backtest indefinitely.

## 6. Harden storage and operation

- [ ] Classify owned tables/caches by retention and rebuildability; define a concrete retention policy before deleting any unique history.
- [ ] Establish recoverable migrations and backup/restore behavior for TajsTokens-owned data without modifying Codex-owned databases.
- [ ] Verify source/schema upgrades, permissions failures, partial corruption, and unavailable files fail with actionable diagnostics rather than silent substitution.
- [ ] Measure startup, refresh, query latency, memory, and storage growth using representative histories; agree explicit budgets from measurements rather than invented thresholds.
- [ ] Keep long queries cancellable and bounded, avoid native-source write locks, and prevent slow collection from freezing current quota/UI updates.
- [ ] Verify that export is explicit and reviewed/sanitized as appropriate; raw local inspection must never become an automatic upload.

**Gate:** upgrades and recovery preserve irreplaceable evidence; performance is acceptable against recorded budgets; the user can understand and recover from a failed source or collection step.

## Delivery and completion discipline

Implement vertical slices rather than one repository-wide migration: contract -> required evidence -> read model -> usable UI -> validation. Workstream 1 narrows the next slice; it is not an excuse to reopen all historical research. Keep existing project boundaries unless an observed ownership problem warrants a change.

For code milestones, follow the commands in `AGENTS.md` using `TajsTokens.slnx`, run relevant algorithm evaluations, and inspect the intended diff. For documentation-only work, check consistency, links, and referenced paths/commands without launching the app.

A slice is complete only when its acceptance gate is evidenced, existing behavior is preserved where intended, limitations are explicit, and documentation describes the delivered behavior. Overall completion means the product end-state workflows and operational guarantees in `PROJECT.md` are satisfied—not that every possible Codex table is copied or every optional advanced model is implemented.

**Next action:** produce the bounded integration map for the existing daily workflows, identify the highest-value unsupported history/reconciliation gap, and implement that slice through its acceptance gate. This document authorizes no automatic deletion, broad source mirroring, or rollout of speculative architecture.
