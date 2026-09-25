# Architecture decision records

A decision that was argued over, with the options that lost and the conditions
under which it should be reopened. CLAUDE.md carries the short form of the
design rules; an ADR is where the long form of one decision lives when the
reasoning would not fit there.

Numbered in order, never renumbered. A decision that is reversed gets a new ADR
that supersedes the old one, and the old one's status says so. The history of
why something changed is as much the record as the decision itself.

| # | Decision | Status |
|---|---|---|
| [0001](0001-sync-commit-granularity.md) | Sync flushes each record in its own savepoint, inside one transaction per batch | Accepted |
