# ADR 0001 — Sync writes each record separately, inside one transaction per batch

- **Status:** Accepted
- **Date:** 2026-09-25
- **Plan item:** `NCBRS-Business-and-Delivery-Plan.md` §17, item 11
- **Code:** `SyncController.SubmitBatch` / `ProcessRecordAsync`, `IdempotencyFilter`

## Context

`POST /api/Sync/batches` takes a device's offline outbox: up to 500 records
(`SyncBatchRequestValidator.MaxRecordsPerBatch`), from a post that may have
been offline for weeks. The question is how much of that batch one
`SaveChanges` should cover.

The plan (§A7) framed this as "per-record commits" and blamed a ~120 ms
per-record floor partly on a WAL fsync per record. **That is not what the code
does,** and this ADR records the real shape before deciding anything about it:

- **A device always sends `X-Transaction-Id`** (the load driver and the District
  tier do too). With a caller-supplied id, `IdempotencyFilter` opens **one
  database transaction around the whole request**. It holds the idempotency
  claim, the batch's work and the completed key, and it commits once at the
  end. So there is **one commit, and one fsync, per batch**.
- Inside that transaction, `ProcessRecordAsync` puts **each record in its own
  savepoint** and calls `SaveChanges` once per record (through
  `BirthRegistrationService`, the duplicate scan and the provisional
  reconciler). A record that fails rolls back to its savepoint, and only that
  record's writes are discarded.
- Without a transaction id (a caller that sends none), each `SaveChanges` is
  its own transaction. No device path takes that route.

So the real choice is not "commit per record vs per batch". It is **flush per
record, with a savepoint, vs flush once per batch**, and separately **whether
the batch should be one transaction at all**.

What was measured (A7, dev Postgres with WAL archiving on, one facility, 25
records per batch): ~6.6 records/s at concurrency 1, which is the ~120 ms floor,
and ~32 records/s at concurrency 16, with no failures. Throughput scales about 5×,
so the path is not globally serialised. The duplicate scan is ~5 ms
(`EXPLAIN ANALYZE`). The rest of the floor is the per-record round trips: the
BRN existence check, the author lookup, the duplicate scan, the inserts with
their audit triggers and outbox rows, the savepoint create and release, and
the reconciler. None of it has been broken down further, so **nobody knows yet
what share of the floor per-batch flushing would remove.**

*Update, 2026-09-25:* the ~120 ms figure is inflated. The load driver's
synthetic names are close enough for the matcher to flag each record against
dozens of others (plan §17 11d), and the resulting bulk `DuplicateCandidates`
inserts dominated the time. A clean 500-record batch on an idle register ran
at ~20 ms per record. The decision below does not depend on the figure.

## Options

### A. Flush per record in a savepoint, one transaction per batch (current)

- **One bad record costs only itself.** This is the property the sync design
  rests on (draft 6.3): a post back from three weeks offline must not lose 49
  good registrations because the 50th has a problem. Validation rejections
  happen before any write. The savepoint covers the harder case of a
  database-level failure partway through one record.
- **Each record sees the ones before it.** The BRN existence check and the
  duplicate scan query the database. Because each record is flushed before the
  next one starts, an outbox holding the same BRN twice, or the same child
  entered twice, is caught **within the batch**.
- **The provisional reconciler works as designed.** Assigning a real BRN to a
  `PROV-` record uses the `[ConcurrencyCheck]` counter and a retry loop that
  needs its own `SaveChanges`. Its retry scope is one record.
- **The replay is exact.** Because the whole batch is one transaction with its
  idempotency key, a device that lost the response gets the original
  per-record answer back, including any `AssignedBrn`. That is the only way it
  learns the number its provisional slip became.
- Cost: several round trips per record, and **one transaction held for the
  whole batch** (see Consequences).

### B. Stage the whole batch, flush once

- Saves the per-record savepoint pair and some statement round trips. EF
  already batches the statements inside one `SaveChanges`, and the audit
  triggers still fire per row. So the saving is **bounded and unmeasured**, and
  on the evidence above it is a fraction of the floor, not most of it.
- **It breaks correctness, not just isolation.** Staged records are invisible
  to the BRN check and the duplicate scan that follow them, so a duplicate
  inside one outbox would pass both checks. It would then either fail the
  whole flush on the unique index, or, for a duplicate child under two BRNs,
  register twice without a flag. Keeping in-batch duplicate detection would
  mean re-implementing it in memory: a second description of the rule that can
  drift from the first, which is the thing this codebase avoids everywhere else.
- A database-level failure fails the entire flush. Recovering isolation would
  mean bisecting the batch and retrying, which is more code and more round
  trips than the flushes it saved.
- The reconciler's concurrency retry would widen from one record to the batch.

### C. A separate transaction per record (no outer transaction)

- Shortens every transaction and every lock to one record.
- **It gives up the exact replay.** The idempotency claim, the work and the
  completed key could no longer commit together. A retried batch would come
  back record by record as `Duplicate` instead of the original answer, and a
  device would never learn the BRN its provisional record was reconciled to.
  The filter exists to close exactly this window (see its header comment).

### D. Commit in chunks of N records, one idempotency key per chunk

- Bounds transaction length without giving up the replay, but it changes the
  sync contract: the device or the District tier would have to split and track
  chunks. If a shorter transaction is ever needed, splitting the **outbox**
  into smaller batches on the client does the same with no server change.

## Decision

**Keep A.** Flush each record in its own savepoint, inside one transaction per
batch. B trades correctness (in-batch duplicate detection) and failure
isolation for an unmeasured saving. C trades away the exact replay that
provisional reconciliation depends on. D is a contract change that client-side
batch sizing already provides if it is ever needed.

This is not "per-record commit". Documentation that calls it that, including
the plan's A7 paragraph, is corrected alongside this ADR.

## Consequences

- **A batch holds one transaction for as long as it takes to process.** A
  clean 500-record batch took 6–10 s on an idle register and 46.7 s during a
  concurrent sync burst. During that time the transaction holds locks on rows
  every record touches:
  - The **device row**, updated by the last-seen mark at the start of the
    batch. Concurrent batches from one device therefore serialise; A7
    observed this before the driver moved to a device fleet.
  - The **facility row**, whenever a provisional record is reconciled, since
    that updates `BrnBlockNextAvailable`. Until the batch commits, this also
    blocks a `request-brn-block` for that facility and any other batch
    reconciling there.
- **Found while writing this ADR, since reproduced:** the District tier
  forwards with a **20 s** HTTP timeout (`CentralApiClient.Timeout`). A forward
  that outlasts it is cancelled at the centre, which rolls back the whole batch,
  and the District retries it into the same timeout indefinitely, so the births
  never land. This happened to a 500-record batch during a sync burst. The
  second failure mode, a landed batch marked `Rejected` after a `409`, turned out
  to need no proxy: the District forwards every new batch twice at once, and
  that produces it on its own. The fix belongs to the timeout, batch sizing and
  the District's own forwarding, not to this decision, and all three were
  fixed there (plan §17 items 11a–11c): a per-batch timeout that grows after a
  timeout, one forward at a time per batch, and the signature carried through.
- In-batch duplicate detection, per-record isolation and exact replay stay
  intact. The tests that pin them (sync batch, idempotency, provisional
  reconciliation) should keep passing unchanged.

## Revisit when

- **The A7 exit run on production-grade Postgres, across multiple facilities,
  shows the savepoint and per-record flush round trips are most of the
  per-record time.** Instrument the savepoint pair and the `SaveChanges` calls
  separately before deciding; do not infer it from the total again.
- **Lock hold time becomes visible:** `request-brn-block` latency spikes that
  line up with sync batches at the same facility, or lock waits on `Devices` or
  `Facilities` in `pg_stat_activity`. The first answer to that is smaller
  batches from the client, not a different commit shape.
