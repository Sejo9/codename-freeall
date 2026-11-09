# FreeAll --- Project Documentation

**Documentation baseline:** 1.0\
**Status:** Approved product direction; implementation details marked as
proposed or pending validation\
**Last updated:** 2 November 2026

## Purpose

This package is the authoritative documentation baseline for FreeAll, a
Windows 11 storage intelligence and safe-cleanup desktop application. It
is intended for product owners, architects, developers, UI/UX designers,
QA engineers, security reviewers, release engineers, and AI coding
agents.

## Start here

1.  [Product Overview](01-product-overview.md)
2.  [Product Requirements Document](02-product-requirements.md)
3.  [Feature Catalogue](03-feature-catalogue.md)
4.  [Functional Specification](04-functional-specification.md)
5.  [System Architecture](05-system-architecture.md)
6.  [Technical Design](06-technical-design.md)
7.  [UI/UX Design System](07-ui-ux-design.md)
8.  [Security and Privacy](08-security-privacy.md)
9.  [Scanning and Indexing](09-scanning-indexing.md)
10. [Cleanup, Quarantine and Recovery](10-cleanup-quarantine.md)
11. [Data Model and Persistence](11-data-model.md)
12. [Quality Assurance and Acceptance](12-quality-assurance.md)
13. [Deployment, Updates and Support](13-deployment-release.md)
14. [Roadmap and Delivery Plan](14-roadmap.md)
15. [AI Agent Rules](15-ai-agent-rules.md)
16. [Developer Decisions](16-developer-decisions.md)
17. [Feature Checklist](17-feature-checklist.md)
18. [Development Log](18-development-log.md)
19. [Blocked Features and Open Decisions](19-blocked-features.md)
20. [Glossary and References](20-glossary-references.md)
21. [Requirements Traceability Matrix](21-requirements-traceability.md)
22. [Operations and Troubleshooting Runbook](22-operations-runbook.md)
23. [Dependency Register](23-dependency-register.md)
24. [Windows Compatibility Matrix](24-compatibility-matrix.md)
25. [Release Notes and Known Limitations](25-release-notes.md)

## Canonical working documents

The numbered files in this folder are authoritative. The working
documents named in `15-ai-agent-rules.md` §3 are these files (TD-007):
`FEATURE_CHECKLIST.md` = `17-feature-checklist.md`, `AI_AGENT_RULES.md`
= `15-ai-agent-rules.md`, `DEVELOPER_DECISIONS.md` =
`16-developer-decisions.md`, `DEVELOPMENT_LOG.md` =
`18-development-log.md`, `BLOCKED_FEATURES.md` =
`19-blocked-features.md`.

## Authority and conflict resolution

-   `16-developer-decisions.md` records approved decisions and their
    history.
-   `02-product-requirements.md` and `04-functional-specification.md`
    define required product behaviour.
-   `05-system-architecture.md` and `06-technical-design.md` define
    implementation boundaries.
-   `07-ui-ux-design.md` governs presentation and interaction.
-   `08-security-privacy.md` and `10-cleanup-quarantine.md` take
    precedence for security-sensitive and destructive behaviour.
-   If documents conflict, do not silently choose. Record the conflict
    in `19-blocked-features.md` and request product-owner clarification
    when it changes an approved requirement.

## Status vocabulary

`NOT STARTED`, `IN PROGRESS`, `BLOCKED`, `IMPLEMENTED`, `TESTING`,
`COMPLETED`, `DEFERRED`.

A feature is not `COMPLETED` until implementation, tests, validation,
documentation, and acceptance criteria are all satisfied.

## Important distinction

This is a baseline specification, not evidence that the software has
been implemented or tested. Third-party packages, native API approaches,
performance targets, ARM64 support, and several detailed product
policies remain subject to the validation gates recorded in the relevant
documents.
