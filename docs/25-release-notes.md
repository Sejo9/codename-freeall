# FreeAll --- Release Notes and Known Limitations

Required by `02-product-requirements.md` §6 and `13-deployment-release.md` §7
step 11. The text of each release's notes also goes into the signed update
manifest (`notes`), so keep it plain and short.

## 0.0.1 --- release candidate (2026-11-08, not yet released)

**Status: not releasable yet.** The release gates still open are listed in
"Before this can ship" below.

### What it does

-   Finds local drives, scans them with streaming, bounded-memory
    enumeration, and keeps a searchable index with full and incremental
    (change journal) scans and scan history.
-   Storage Explorer with a treemap, File Categories, Large Files, Search and
    exports to CSV, JSON, HTML and PDF.
-   Application Residue and Temporary Files, with evidence, confidence and
    risk for every suggestion; nothing is preselected.
-   Duplicate Files within each drive, confirmed byte for byte.
-   **Windows Storage** (new): what Windows itself uses on its drive (component
    store, update downloads, previous installation, page and hibernation
    files, restore points, Recycle Bin and others), measured from the scan,
    with the Windows tool that manages each. FreeAll never removes anything
    there; buttons open Windows' own screens.
-   Cleanup from a review queue to the Recycle Bin, quarantine (same drive or
    a folder you choose) or permanent deletion, each item checked again just
    before it is touched, with restore and a full activity record. An elevated
    helper retries items that need administrator approval.
-   **Installer** (new): a per-user setup (`FreeAll-Setup-0.0.1-x64.exe`) that
    needs no administrator rights, installs to
    `%LocalAppData%\Programs\FreeAll`, adds a Start menu shortcut and an entry
    in Settings › Apps, upgrades in place, refuses to install an older
    version over a newer one, and asks before deleting your data when you
    uninstall (keeping it is the default).
-   **Update checks** (new, off until you say yes): asked once at first start.
    Checks fetch a signed manifest over HTTPS at most once a week; nothing is
    downloaded or installed without your approval, and the installer is
    checked against the signed manifest before it starts.
-   Follows Windows' **Text size** setting and formats dates and numbers in
    your regional format.

### Compatibility

Windows 11, x64 only. See `24-compatibility-matrix.md`: on 2026-11-08 only
Windows 11 Pro 26H2 has been tested. ARM64 and Windows 10 are not supported.

### Known limitations

-   **Not code-signed** (DEC-025): Windows SmartScreen warns when the setup
    file is first run, and the helper's UAC prompt shows "Unknown publisher".
    The helper is checked against a digest built into the app instead.
-   **Overview** is still a placeholder page (F-006).
-   **Update checks can't run in this build** until the project publishes an
    update address and key (BLK-021). The question isn't asked until then.
-   A standard-user scan of the Windows drive is normally **partial**: some
    Windows folders can't be read without administrator rights, and the app
    says so. Elevated scanning (DEC-023) is not built.
-   Windows Storage measures only what the scan could read; restore points,
    Windows temporary files and Store apps usually show "Not readable". The
    component store size counts hard-linked files once and can differ from
    Windows' own figure.
-   Text scaling enlarges the whole window content, not only the text. At
    150 % and more on a window narrower than about 1,400 pixels, pages with
    a list and a details pane side by side (Windows Storage, Temporary Files,
    Application Residue, Duplicate Files) crop the details pane, and long
    page titles are cut; maximise the window. Stacking the panes on narrow
    windows is planned (F-038).
-   English only; dates and numbers follow the Windows regional format.
-   The uninstaller leaves a copy of itself (about 2 MB) in your temporary
    folder, because a running program can't delete itself; Windows' Storage
    Sense removes it.
-   Files that a folder gains between its last check and its removal go with
    it (TD-043).
-   See `19-blocked-features.md` for validation still outstanding (Narrator,
    high contrast, removable media, elevated helper run, independent security
    review).

### Before this can ship (release acceptance, `02-product-requirements.md` §6)

  ----------------------------------------------------------------------------------------
  Gate                                                    State
  ------------------------------------------------------- --------------------------------
  Release-blocking security and data-loss defects         None known; independent security
  resolved                                                review not done (BLK-020)

  Supported Windows 11 matrix passed                      One release tested (26H2 Pro);
                                                          25H2 and 24H2 Enterprise not
                                                          tested

  x64 packaging, upgrade and uninstall pass               Automated: yes. Interactive run
                                                          by a person: not yet

  ARM64 validated or listed as unsupported                Listed as unsupported

  Cleanup and quarantine recovery tests pass              Automated: yes. Manual §3.2:
                                                          not yet (BLK-019)

  Documentation, release notes, known limitations         This document
  ----------------------------------------------------------------------------------------

### Release files

`build.ps1 -Configuration Release -Locked -Publish` writes to
`artifacts/release`:

-   `FreeAll-Setup-<version>-x64.exe`: the installer with the app inside.
-   `FreeAll-<version>-x64.spdx.json`: SPDX 2.3 software bill of materials,
    from the lock files.
-   `SHA256SUMS.txt`: checksums of the files above.

To offer the release as an update, sign a manifest with
`tools/updates/New-UpdateManifest.ps1` and publish it beside the setup file
(`22-operations-runbook.md` §7).
