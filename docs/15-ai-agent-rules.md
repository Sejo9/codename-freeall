# FreeAll --- AI Agent Rules

## 1. Purpose

These rules govern AI coding agents and automated contributors working
on FreeAll. They are mandatory.

## 2. Source of truth

Read the relevant project documents before changing code. Approved
decisions in `16-developer-decisions.md` and requirements in the PRD,
functional specification, security and cleanup documents must be
preserved.

Do not infer that a proposed design has been implemented.

## 3. Required working documents

Maintain: - `FEATURE_CHECKLIST.md` - `AI_AGENT_RULES.md` -
`DEVELOPER_DECISIONS.md` - `DEVELOPMENT_LOG.md` - `BLOCKED_FEATURES.md`

The canonical copies are in `/docs`. If the repository uses uppercase
root-level copies for agent tooling, keep them synchronized and state
which copy is authoritative.

## 4. Feature status values

-   `NOT STARTED`
-   `IN PROGRESS`
-   `BLOCKED`
-   `IMPLEMENTED`
-   `TESTING`
-   `COMPLETED`
-   `DEFERRED`

`COMPLETED` requires: 1. Implementation. 2. Relevant automated tests. 3.
Manual/operational validation where needed. 4. Security review where
relevant. 5. Documentation update. 6. Acceptance criteria satisfied.

Code that compiles is not automatically completed.

## 5. Clarification rules

Ask the product owner before making a decision that materially
changes: - Product behaviour. - Data handling or privacy. -
Destructive-operation semantics. - Privilege boundaries. - Supported
Windows releases or CPU architectures. - User-facing workflows. -
Approved feature scope. - Recovery or retention behaviour.

Do not ask repetitive questions about routine implementation details
already covered by approved standards. Make a reasonable engineering
choice, document it, and continue when it does not alter product intent.

## 6. Prohibited shortcuts

-   No automatic deletion.
-   No silent fallback from Recycle Bin/quarantine to permanent
    deletion.
-   No arbitrary privileged helper command.
-   No bypassing ACLs or taking ownership to force access.
-   No unbounded scan queues or loading entire file contents into
    memory.
-   No traversal through reparse points by default.
-   No unverified hash-only duplicate assertion; byte verification is
    required.
-   No cross-volume duplicate grouping in v1.
-   No telemetry or metadata upload without explicit approval and
    consent.
-   No claiming support for ARM64 before validation.
-   No claiming a scan is complete when errors materially affect
    coverage.
-   No marking a feature complete without tests and acceptance evidence.

## 7. Security-sensitive changes

Require focused review for: - Helper IPC. - Path validation. -
Reparse-point handling. - File identity/revalidation. - Deletion and
quarantine. - Update verification. - Database access and backup. - Any
code that runs elevated.

## 8. Session procedure

At start: 1. Read these rules and the relevant documents. 2. Inspect
repository state and existing tests. 3. Read the feature checklist and
blockers. 4. Identify the exact scope for the session.

During work: - Make small, reviewable changes. - Add tests with
implementation. - Update checklist status. - Record decisions and
blockers. - Do not overwrite user work or silently change approved
architecture.

At end, report: - Completed. - In progress. - Pending. - Blocked and
why. - Tests run and results. - Files changed. - Decisions made. - Risks
and next recommended task.

## 9. Conflict handling

If instructions or documents conflict, stop the affected work, record
the conflict in `BLOCKED_FEATURES.md`, and ask for clarification if it
changes approved behaviour. Do not invent a resolution.
