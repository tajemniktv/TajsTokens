# Codex rollout representation contract

Design accepted for **ING-001** on 2026-10-28. [PROJECT.md](../../PROJECT.md)
owns the product contract and remaining work. This note specifies the extension to
the existing discovery, ingestion, checkpoint and SQLite owners; it does not claim
compressed ingestion or the complete immutable-retention contract is implemented.

## Evidence and compatibility

Inspected the read-only Codex clone at
`7498521d288b9b3b96ffba4eedf089d8d6e06a84`:

- `codex-rs/rollout/src/compression.rs`: `RolloutFile::from_path` suppresses a
  `.jsonl.zst` sibling when plain `.jsonl` exists. Materialization restores plain
  bytes before append. Compression uses Zstandard level 3 with pledged source size.
- `codex-rs/rollout/src/seekable_reader.rs`: offsets address original JSONL bytes;
  reads prefer plain, retry resolution races and retain an opened representation.
  Its seekable compressed reader uses an anonymous decoded temporary file.
- TajsTokens currently discovers plain files, reads newline-delimited physical
  bytes and independently hashes physical prefixes in `CodexSessionIngestionService`.
  Its checkpoint and parser resume state must change together. Adding a decoder
  only to the line reader would invalidate checkpoint verification.

This is corroborating source evidence, not proof that the installed Desktop build
matches that revision. TajsTokens will stream decoded bytes without creating a
decoded rollout copy: source text must not enter its durable store or temp files.

## Identity and selection

Keep four distinct identities:

| Identity | Meaning and permitted use |
|---|---|
| Source namespace | Local installation/store root under the existing private locator owner. Equal native IDs in another root/host are not automatically equal evidence. |
| Logical rollout occurrence | Stable occurrence and selected generation, with aliases for proven equivalent representations/locations. Existing occurrence IDs remain valid. |
| Physical representation | Plain or Zstandard path, OS file identity, physical size and write time. Used for opening, races and an unchanged-file fast path, not accounting identity. |
| Record occurrence | Logical generation plus decoded start/end byte offsets. Existing record IDs survive verified adoption, preserving deduplication and first capture times. |

Canonicalizing `.jsonl.zst` to `.jsonl` proposes a sibling relationship; it does
not establish content equivalence. Discovery offers one preferred representation
per sibling slot, plain first. Do not silently fall back from an unreadable/corrupt
plain file to a compressed sibling: report the preferred-source failure, retain
the last-good selected generation, and keep any alternative explicitly qualified.

A representation can adopt an existing occurrence only in the same source
namespace and logical rollout slot, with a verified decoded prefix through the
durable complete-record checkpoint. Verify at least the previously consumed bound;
a shorter source is a truncation/rewrite candidate. Reuse the exact prior occurrence
ID, record coordinates and capture times; append only the new suffix. Physical OS
identity changing during compression/restoration does not itself create new work.

Path migration requires a constrained relationship: the same opened physical file
moving into an archive, or an active/archive alias with the same canonical rollout
filename and verified bytes in the same root. A changed filename needs explicit
source mapping/lineage evidence, not just an equal session ID. Ambiguous matches
remain separate candidates and cannot be silently counted together or merged.

Forks and inherited parent prefixes remain separate rollouts. Neither common bytes,
equal header/session IDs nor an equal token vector establishes one occurrence.
Preserve the parser's established ownership and reducer rules; any inherited-prefix
accounting exclusion must have lineage evidence and a recorded interpretation.
Thread resume/revert can change a rollout filename; record the observed relationship
without using thread identity alone to erase a genuine replacement.

## Coordinates, checkpoints and replay

The new coordinate kind is **decoded-utf8-bytes-v1**. Offsets and SHA-256 include
the original decoded UTF-8 bytes, including CR/LF and any BOM; do not normalize JSON,
newlines, whitespace or text encoding before hashing. End offsets stop after a
complete newline. An unterminated tail is never a committed record.

Persist coordinate kind separately from physical representation metadata. Keep
physical encoded size distinct from decoded position/size, and label both in
diagnostics. The existing `LastByteOffset` is not silently relabelled when opening a
compressed file. Decoded size may remain unknown until EOF; frame content size is
not proof of total size across concatenated frames.

Upgrade existing checkpoints lazily and transactionally:

1. Treat absent coordinate kind as the existing physical-plain-byte contract.
2. On a verified plain source, physical bytes equal decoded bytes. Verify its full
   consumed prefix and preserve occurrence ID, amounts and first collection times.
   Only then persist the new coordinate kind and representation metadata.
3. If only a compressed sibling is available, decode and verify that same consumed
   prefix before adopting the old occurrence. Never seek to the compressed physical
   byte offset or infer decoded length from encoded length.
4. Parser resume state must have the same logical generation, decoded bound,
   parser contract and session ownership. Missing/incompatible state triggers an
   idempotent semantic replay under the verified occurrence, not fabricated state
   or restamped evidence. A parser/normalization change is a new interpretation;
   a genuinely changed consumed prefix is a candidate source generation.
5. Advance the durable checkpoint only after original observations, projections,
   ordered reducer decisions and matching parser state succeed. Cancellation or a
   crash before that point replays safely. Keep newer-schema refusal intact.

Strong prefix verification remains mandatory after a changed physical identity,
length, write time or checkpoint. The unchanged fast path is valid only for the
exact verified tuple and coordinate/parser contract. A suffix hash, mtime or equal
length cannot replace prefix verification. Compressed resume initially decodes from
the beginning and hashes/discards through the checkpoint; it does not allocate the
whole prefix or persist source text. Measure that cost before adding a seek index.

## Replacement, corruption and bounded reading

Original content-free observations and superseded generation membership must be
durable **before** ING-002 can change representation/replacement behavior.
Component 10 now journals new/recovered original counter inputs, and component 11
preserves superseded numeric interpretations before active projection removal;
[validation and limits](STAGE2_EVIDENCE_VALIDATION.md) remain separate from this design.
Keep
original nullable total/last snapshots, original times and capture identity apart
from normalized deltas/latest counter state. Legacy deltas cannot reconstruct
missing original snapshots; mark their original-input coverage unavailable.

Keep the last-good selected generation while validating a replacement. Journal
candidate observations with candidate/failed status without making them active
accounting. Select a new generation and retire its old projections atomically only
after the candidate's required decode/integrity verification succeeds. A corrupt
frame may emit valid-looking lines before its checksum fails: those lines must not
silently replace good history. Existing response observations already have a
surviving evidence path; extend the current owners for the missing legacy inputs
and superseded numeric evidence rather than creating a second ingestion framework.

The reader must hold one opened physical handle through decoding and verification,
with read/write/delete sharing where supported. Retry bounded discovery/open races
by resolving the sibling slot again; do not hash one file and parse a different
replacement. Before publication, recheck source/selection consistency. Changed
inputs require retry or candidate status, never a successful unchanged checkpoint.

Use streaming decoding, a **32 MiB maximum frame window** (`windowLogMax = 25`),
64 KiB I/O buffers, the existing 64 MiB maximum JSONL record and 128-record batch.
No allocation may scale with advertised full decoded size. Reuse existing scan/time
budgets and cancellation between reads/records; expose window/record/budget refusal
as coverage limits. The window cap is a proposed compatibility bound to verify
against fixtures/observed sources, not a claim about every possible Codex frame.
Serialized ingestion and resource sampling must include decoder and record buffers.

Retain distinct outcomes: complete/unchanged, missing during race, inaccessible,
cancelled, budget deferred, incomplete JSONL tail, truncated frame, invalid frame or
checksum, unsupported dictionary/window/encoding, and rewritten prefix. Concatenated
frames must be read through actual EOF with end-of-stream checking enabled. A plain
incomplete tail can retain its committed prefix; a compressed frame failure cannot
be reported as complete or silently consume the failed suffix. All outcomes need
sanitized counts/locators rather than source text or decoder exception payloads.

## Selected dependency and packaging review

Select **ZstdSharp.Port 0.8.8**, pinned when ING-002 adds the reference. This is a
managed port, compatible with the project's .NET 10 target; the reviewed package's
nearest framework asset is `lib/net9.0/ZstdSharp.dll` (396,800 bytes), with an empty
dependency group and no `runtimes/` native assets. The package download SHA-256 is
`F6797192DEEBF4535F0601C2273E750DA0C02062E30A3E4A3C4011B9AC22403A`.

The NuGet metadata specifies MIT and source revision
`2cd0c019693bc786a5fe5c3be94e107b24e7267e`. Its
[`DecompressionStream`](https://github.com/oleg-st/ZstdSharp/blob/2cd0c019693bc786a5fe5c3be94e107b24e7267e/src/ZstdSharp/DecompressionStream.cs)
accepts a fixed input buffer, exposes `SetParameter`, supports async cancellation,
and throws on premature EOF when `checkEndOfStream` is enabled. Set the window cap
before the first read and dispose both decoder and source deterministically.
Frame concatenation, checksum failure, skippable frames, dictionaries and resource
bounds remain executable acceptance cases in ING-002, not assumptions from API names.

The upstream [MIT notice](https://github.com/oleg-st/ZstdSharp/blob/2cd0c019693bc786a5fe5c3be94e107b24e7267e/LICENSE)
requires retaining copyright/permission text. Ship that notice with the dependency
in portable and daily installations. The package declares a license expression but
does not contain a license text file; relying on copying its DLL alone is insufficient.
Verify restore asset selection, dependency manifests, published DLL/notice and normal
install/recovery with the actual app build when the reference is introduced.

Sources: [NuGet package](https://www.nuget.org/packages/ZstdSharp.Port/0.8.8),
[package repository](https://github.com/oleg-st/ZstdSharp), and the pinned source
files above. This review downloads/inspects the package only; it adds no runtime
dependency and supplies no performance or production decode proof.
