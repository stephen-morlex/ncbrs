# Contributing

The delivery workflow for this repository. It applies to every change,
including work done by an AI assistant — CLAUDE.md carries the *design*
rules, this carries the *process* ones.

The shape is deliberately heavier than a hobby project's. This is a civil
register: a defect here is a child with the wrong legal identity, a family
holding a certificate that no longer verifies, or an audit trail that cannot
answer who changed what. The approval gates below exist because the cost of
being wrong is not a rollback.

---

## The workflow

1. **Inspect** — branch, working tree, recent commits, remote, structure, docs, stack, test commands.
2. **Plan** — requirements, affected files, approach, risks, step-by-step.
3. **Branch** — from the correct base, descriptively named.
4. **Implement**.
5. **Test and validate** — actually run them.
6. **Review locally** — `git diff`, `git status`.
7. **Commit** — conventional commits, **after approval**.
8. **Push**.
9. **Open a PR** — **after approval**.
10. **Review the PR** as a senior reviewer.
11. **Fix** what the review finds.
12. **Re-run tests**.
13. **Final review**.
14. **Merge** — **after explicit approval**.
15. **Verify** the merge.

One step at a time. Announce the step, show the command, explain the result.

---

## 1. Inspect before acting

Never assume the repository's structure or conventions. Check the branch, the
working tree, recent commits, the remote, and the available test commands.

**Uncommitted work belongs to whoever left it there.** Report it, do not
overwrite it, and do not fold it into an unrelated change.

## 2. Plan before coding

State the requirement, the files it touches, the approach, and the risks.
**If the requirement is ambiguous, ask.** A wrong guess in a birth registry
is more expensive than a question.

## 3. Branches

Never commit directly to `main`. Branch from the current `origin/main`.

```
feat/record-search
fix/amendment-drift-check
refactor/device-enrolment-service
chore/web-cors
```

## 4. Implementation

Follow the conventions already in the code. Reuse what exists. Do not touch
unrelated files, and do not "tidy" things the change did not require —
unrelated edits make a diff unreviewable, and this diff needs reviewing.

Read CLAUDE.md first. Several behaviours look like candidates for
simplification and are load-bearing legal rules; each carries a note saying
so.

Summarise every file changed and why.

## 5. Testing

```bash
dotnet build
dotnet test                                    # 566 tests, SQLite
NCBRS_TEST_PROVIDER=Postgres dotnet test       # same suite, PostgreSQL
```

**Do not add a warning.** The build is not warning-free today — three are
known and listed below — but that is a debt to pay down, not a budget to
spend. A build with warnings nobody reads is a build where the next real one
goes unnoticed.

| Warning | Where | Status |
|---|---|---|
| `CS0108` | `DevicesController.Response` hides `ControllerBase.Response` | To fix — shadowing the response object is a genuine hazard |
| `CS8602` | `TransactionHeaderOperationFilter` | Pre-existing null dereference |
| `NU1510` | Redundant `Microsoft.Extensions.Hosting` reference in `NCBRS.Consumer` | Removing it is a dependency change, so it needs approval |

Once `web/` exists: `npm run lint`, `npm run typecheck`, `npm run build`,
`npm test`.

A change touching the schema runs the Postgres suite too. A change touching
the API contract regenerates the web client and builds it.

**Never report a test as passing without running it.** Report what ran, what
passed, what failed, and what is not covered. "It should work" is not a test
result, and neither is a green suite that never exercised the changed path.

When a test fails: analyse, find the root cause, fix, re-run. Do not adjust a
test so it passes unless the test was wrong — and if it was, say so plainly
and explain why.

## 6. Review before committing

Read your own diff. Check for bugs, security issues, accidental secrets,
unintended file changes, and missing tests.

**Never commit:** databases (`*.db`), signing keys (`*.pfx`, `*.pem`),
`node_modules/`, build output, or real personal data. `.gitignore` covers
these; check anyway, because the one time it does not is the one that
matters.

## 7. Commits

[Conventional Commits](https://www.conventionalcommits.org/):
`feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `chore:`, `perf:`, `build:`,
`ci:`.

Stage deliberately. `git add .` sweeps up whatever else is lying around.

The body says **why**, not what — the diff already says what. Where a
decision has a consequence someone may later want to undo, record the
reasoning, so it can be re-read rather than re-argued.

> **Approval gate.** Show the files, the proposed message and the test
> results, then wait.

## 8. Push

Verify the commit, push, set upstream. **Never force-push without explicit
authorisation** — someone else's work may be on that branch.

## 9. Pull requests

Title, summary, the problem being solved, implementation notes, testing
performed, related issue, and any risk or breaking change.

> **Approval gate.** Ask before opening.

Never merge automatically.

## 10. Reviewing a PR

Correctness, security, performance, maintainability, test coverage,
backward compatibility, migration safety, API contract changes.

Classify: **CRITICAL** (must fix) · **HIGH** (before merge) · **MEDIUM** (if
relevant) · **LOW** (optional).

Do not approve with unresolved CRITICAL findings. Check CI results where they
exist.

## 11. Acting on review

Read the comment, explain the issue, fix it, test, commit, push, re-review.
Do not dismiss a valid comment without saying why.

## 12. Merging

Verify: CI green, approvals present, no unresolved blocking comments, correct
base branch, branch current, no unexpected changes.

> **Approval gate.** Explicit human approval, every time.

**Never merge** with failing tests, missing approval, unresolved critical
security findings, or unclear scope.

Merge strategy follows the repository's convention — confirm it rather than
assuming.

## 13. After merging

Confirm the merge, verify the target branch has the commit, check CI and
deployment, watch for post-merge errors. Delete the branch when it is safe
to. Report the PR URL, the resulting commit, test status, and anything left
outstanding.

---

## If blocked, stop

Say what is blocking, what you tried, and what you need. A half-finished
change reported as done is worse than one reported as blocked.

## Current tooling gaps

| | |
|---|---|
| GitHub CLI (`gh`) | **Not installed.** Steps 9–14 cannot be executed locally: `winget install --id GitHub.cli` then `gh auth login`. |
| CI | **None.** No `.github/workflows`. Step 12's "CI green" cannot be checked until one exists. |

Both are worth closing early: the workflow above assumes them, and a process
that quietly skips its own verification steps is not the process.
