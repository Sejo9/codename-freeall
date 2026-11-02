# FreeAll --- Security and Privacy Requirements

## 1. Security objectives

-   Protect user data from accidental or malicious deletion.
-   Keep privileged functionality narrowly scoped.
-   Prevent path confusion, reparse-point escape and
    time-of-check/time-of-use errors.
-   Protect local scan history and quarantine metadata.
-   Avoid collecting or transmitting user file metadata.
-   Make destructive actions explainable and auditable.

## 2. Threat model

Assets: - User files and application data. - Windows system files and
application installations. - Scan database and hash cache. - Quarantine
content and restore metadata. - Elevated helper command channel. -
Update packages and signing keys.

Threats: - Incorrect candidate classification. - Path substitution
between review and execution. - Malicious or compromised helper
client. - Named-pipe impersonation or unauthorized access. -
Reparse-point traversal outside scan scope. - Malformed filenames or
metadata in exports/UI. - Tampered update package. - Database corruption
or rollback. - Partial cross-volume quarantine. - Concurrent file
modification.

## 3. Privilege model

-   Main app runs as standard user.
-   No elevation at launch.
-   Helper starts with UAC only when an approved operation requires it.
-   Helper is not a service and does not remain privileged.
-   If elevation is unavailable, explain which operations cannot be
    performed; do not pretend the scan was complete.

## 4. Helper IPC

-   Use a local Windows named pipe with restrictive ACLs.
-   Authenticate the client and verify expected user/session.
-   Use a versioned, typed request protocol.
-   Allowlist commands such as `InspectPath`, `EnumerateDirectory`,
    `ReadMetadata`, `InspectVolume`, `ExecuteApprovedOperation`.
-   Never expose arbitrary command execution, arbitrary shell, arbitrary
    path deletion or generic privileged filesystem RPC.
-   Validate every argument and policy independently in the helper.
-   Apply request size limits, timeouts and cancellation.
-   Audit privileged operations.
-   Verify helper publisher signature before launch where practical.
-   Fail closed on protocol mismatch or authentication failure.

## 5. Path and file safety

-   Normalize and validate paths.
-   Resolve volume identity and file identity.
-   Detect reparse points and refuse unsafe traversal.
-   Do not rely on string-prefix checks alone for path containment.
-   Revalidate file identity, size and change metadata immediately
    before destructive action.
-   Where supported, use handle-based operations to reduce race
    conditions.
-   Never bypass ACLs, take ownership, or change permissions to force
    cleanup.
-   Never delete protected Windows servicing content manually.

## 6. Deletion protections

-   No automatic deletion.
-   Protected/system policy blocks known critical targets.
-   Uncertain targets require review and may be blocked.
-   File size, age or extension alone cannot establish safety.
-   Application residue cannot be automatically removed.
-   Permanent deletion requires separate explicit mode and confirmation.
-   The app must show exact paths and destination before execution.

## 7. Privacy

-   Local processing by default.
-   No telemetry by default.
-   No file content upload.
-   No path, filename, hash, app inventory or usage metadata upload
    without separate explicit consent.
-   Update checks are opt-in at initial setup.
-   Any future diagnostics upload must be previewable, minimal,
    consent-based and redactable.
-   Logs should avoid file names and full paths unless needed for local
    troubleshooting; protect logs with user ACLs.

## 8. Data protection

-   Store data under the current user's profile.
-   Apply restrictive ACLs to database, logs and quarantine metadata.
-   Do not store credentials.
-   Avoid storing file content.
-   Protect quarantine metadata from tampering as far as practical.
-   Back up database consistently; do not copy a live SQLite database
    without a supported backup/checkpoint strategy.

## 9. Updates and supply chain

-   HTTPS to official endpoints.
-   Verify signed manifests and packages.
-   Verify publisher identity and signature chain.
-   Prevent downgrade or replay where feasible.
-   Require user approval before install.
-   Pin and review dependencies.
-   Maintain SBOM and dependency vulnerability checks.
-   Sign release binaries and installer.

## 10. Security testing

-   Path traversal and reparse-point tests.
-   Symlink/junction/mount-point loop tests.
-   TOCTOU replacement tests.
-   Named-pipe ACL and impersonation tests.
-   Malformed protocol and oversized request tests.
-   Helper elevation and signature tests.
-   Protected path policy tests.
-   Quarantine interruption and tampering tests.
-   Export injection tests.
-   Update signature and rollback tests.
