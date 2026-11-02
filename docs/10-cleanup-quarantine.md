# FreeAll --- Cleanup, Quarantine and Recovery

## 1. Safety-first model

Cleanup is a reviewed transaction, not a side effect of scanning. No
scan, classification, residue detection or duplicate detection feature
may delete files automatically.

## 2. Eligible targets

Every target must have: - Exact path. - Volume identity. - File identity
where available. - Expected size and change metadata. - Source
feature/rule. - Classification and evidence. - Risk and confidence. -
Proposed operation and destination.

## 3. Destinations

### Recycle Bin --- default

Use supported Windows shell deletion behaviour where applicable. Report
when an item cannot be placed in the Recycle Bin. Do not silently fall
back to permanent deletion.

### Quarantine --- optional

User chooses: - Same-volume quarantine. - Central quarantine location
selected by user.

Quarantine must preserve enough information to restore: - Original path
and volume identity. - Original file name. - Timestamps and attributes
where restorable. - Security metadata where supported and appropriate. -
Content hash. - Operation ID and timestamp. - Original file identity
where available.

Same-volume move may be atomic when supported. Cross-volume workflow: 1.
Copy to quarantine. 2. Verify copied content. 3. Preserve required
metadata. 4. Revalidate original. 5. Remove original only after
successful verification. 6. If source removal fails, retain the verified
copy and report that original storage remains occupied.

### Permanent deletion

Separate mode, explicit warning and confirmation. Never use as an
automatic fallback.

## 4. Review sequence

1.  Select items.
2.  Preview exact targets and total estimates.
3.  Display risk, ownership, confidence and consequences.
4.  Choose destination.
5.  Revalidate target identity and policy.
6.  Show final confirmation with item count and destination.
7.  Execute.
8.  Record per-item outcomes.
9.  Reconcile actual volume usage where possible.

## 5. Protected target policy

Block or require special handling for: - Windows system directories and
protected OS files. - Boot and recovery files. - Active application
installation files. - Shared application data. - User profile documents
and libraries. - Files currently in use. - Unknown ownership or
high-impact data. - Reparse-point targets and paths outside approved
scope.

The policy should be allowlist/denylist plus contextual review, not a
simplistic extension-based rule.

## 6. Quarantine integrity

-   Quarantine is excluded from cleanup candidates.
-   No automatic purge or retention deletion.
-   Validate content hash before restore.
-   Detect missing/corrupt quarantine content.
-   Protect metadata and quarantine folder ACLs.
-   Do not overwrite an existing restore target without explicit user
    choice.

## 7. Restore

-   Verify quarantine item integrity.
-   Resolve original volume and path.
-   If volume is missing, offer restore to a user-selected destination.
-   If destination exists, offer safe conflict choices: skip, restore
    under alternate name, or user-approved replacement.
-   Revalidate permissions and metadata.
-   Record restore result.

## 8. Audit record

Each operation records: - Operation ID. - User/session. - Start/end. -
Requested mode. - Selected item identities and paths. - Policy decision
and confirmation. - Helper involvement. - Per-item result and error. -
Quarantine destination or Recycle Bin result. - Estimated and measured
reclaim information.

## 9. Failure rules

-   Database unavailable: block cleanup.
-   Revalidation mismatch: skip target and require review.
-   Helper disconnect: fail closed.
-   Cross-volume verification failure: preserve source.
-   Source deletion failure after verified copy: preserve both and
    report.
-   Partial batch: report exact successes and failures; never claim
    total success.
