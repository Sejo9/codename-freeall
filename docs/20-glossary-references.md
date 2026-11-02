# FreeAll --- Glossary and References

## Glossary

-   **Allocated size:** Storage allocation attributed to a file by the
    filesystem; may differ from logical size.
-   **Application residue:** Files or directories that may remain after
    an application is removed.
-   **Byte verification:** Direct comparison of file contents after hash
    matching.
-   **Confidence:** Strength of evidence linking a candidate path to an
    application or classification.
-   **File identity:** Filesystem-provided identity, generally
    interpreted with volume identity.
-   **Hard link:** Multiple directory entries referring to the same
    underlying file record/content allocation.
-   **Logical size:** File length reported by the filesystem.
-   **Quarantine:** Managed storage for items moved aside with metadata
    intended to support restoration.
-   **Reparse point:** Windows filesystem mechanism used by links,
    junctions, mount points and other redirections.
-   **Reclaimable estimate:** Estimate of storage that may become
    available after an operation; not guaranteed.
-   **USN Change Journal:** NTFS change record mechanism that can help
    identify filesystem changes.
-   **Volume GUID:** Stable volume naming mechanism preferable to
    relying only on drive letters.

## Official references

1.  [.NET support
    policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
    --- verify .NET 10 servicing and end-of-support before release.
2.  [What's new in
    WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/)
    --- WPF changes by .NET release.
3.  [WPF control styles and
    templates](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/control-styles-and-templates)
    --- theming and styling guidance, including modern Fluent theme
    information.
4.  [Install .NET on
    Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
    --- runtime/OS/architecture support matrix.
5.  [Windows lifecycle fact
    sheet](https://learn.microsoft.com/en-us/windows/release-health/)
    --- Windows release health and servicing information.
6.  [Windows App SDK
    documentation](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/)
    --- optional Windows API capabilities usable by desktop apps where
    applicable.
7.  [SQLite documentation](https://www.sqlite.org/docs.html) ---
    database behaviour, backup and operational guidance.

## Reference maintenance

-   Verify official links and policy before each release.
-   Do not freeze Windows servicing dates in code.
-   Third-party package references must be added with version, licence,
    maintenance and security review.
-   This document is informational and does not replace API-specific
    implementation review.
