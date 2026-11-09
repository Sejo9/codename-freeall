# FreeAll --- Release Notes and Known Limitations

Required by `02-product-requirements.md` §6 and `13-deployment-release.md` §7
step 11. Each version's section is published as its GitHub release page by
`.github/workflows/release.yml`, so it is written for people downloading
FreeAll: no internal IDs, no links into the repository, no tables. The app's
update notes link to that page.

## 1.0.2 (2026-11-09)

FreeAll now has its own icon: on the program, the Start menu shortcut, the taskbar, the window, Settings › Apps, the installer, and the Windows prompt shown when FreeAll asks for administrator rights. Nothing else changed.

For what FreeAll does, its known limitations and what has been tested, see the 1.0.1 notes: https://github.com/Sejo9/codename-freeall/releases/tag/v1.0.1

### Install or update

- **New install:** download `FreeAll-Setup-1.0.2-x64.exe` below and run it. It installs for your Windows account only and needs no administrator rights. FreeAll isn't code-signed yet, so if Windows SmartScreen says it "protected your PC", choose **More info**, then **Run anyway**.
- **Already have 1.0.1:** run the new setup file, or, if you turned on update checks, use **Check now** in Settings. Your data and settings are kept.
- **Check the download (optional):** in PowerShell, run `Get-FileHash .\FreeAll-Setup-1.0.2-x64.exe` and compare the result with the line for that file in `SHA256SUMS.txt`.

### Files

- `FreeAll-Setup-1.0.2-x64.exe`: the installer.
- `SHA256SUMS.txt`: checksums of the files.
- `FreeAll-1.0.2-x64.spdx.json`: the list of components FreeAll includes (SPDX software bill of materials).
- `manifest.json` and `manifest.json.sig`: the signed update information FreeAll checks.
## 1.0.1 (2026-11-09)

The first release of FreeAll: a free tool that shows what is using the space on your Windows 11 PC and helps you decide what to remove, without removing anything on its own.

### What it does

- **Overview:** each drive's free space, how complete and how recent its last scan is, storage by type of file, what grew since the previous scan, and what is waiting for your decision.
- **Scanning:** scans local drives without changing anything, keeps a searchable index, and can update a scan quickly from the drive's change journal. Network drives aren't scanned. Links and junctions aren't followed.
- **Storage Explorer, File Categories, Large Files and Search,** with a treemap, and exports to CSV, JSON, HTML and PDF.
- **Application Residue and Temporary Files:** possible leftovers of uninstalled programs and temporary data, each with its evidence, confidence and risk. Nothing is selected for you.
- **Duplicate Files:** identical files on the same drive, confirmed byte for byte.
- **Windows Storage:** what Windows itself uses (component store, update files, page and hibernation files, restore points, Recycle Bin and more), with a button that opens the Windows tool that manages each. FreeAll never removes anything there itself.
- **Cleanup you control:** items you choose go to the Recycle Bin (the default), to quarantine, or, only if you confirm it separately, are deleted permanently. Each item is checked again just before it is touched. Quarantined items can be restored, and every action is recorded in Activity.
- **Updates, off until you agree:** FreeAll asks once. If you agree, it checks GitHub for a signed update at most once a week and never downloads or installs without asking.
- **Privacy:** no telemetry and no account. Everything FreeAll records stays in `%LocalAppData%\FreeAll` on your PC.

### Install

1. Download `FreeAll-Setup-1.0.1-x64.exe` below.
2. Optional: check it. In PowerShell, run `Get-FileHash .\FreeAll-Setup-1.0.1-x64.exe` and compare the result with the line for that file in `SHA256SUMS.txt`.
3. Run it. It installs for your Windows account only and needs no administrator rights. Because FreeAll isn't code-signed yet, Windows SmartScreen may say it "protected your PC": choose **More info**, then **Run anyway**.

To uninstall, use Settings › Apps › Installed apps. It asks whether to keep your FreeAll data; keeping it is the default.

### Requirements

Windows 11 on a 64-bit Intel or AMD PC (x64). Windows 10 and ARM PCs aren't supported.

### Known limitations

- **Not code-signed yet:** SmartScreen warns on first run. When FreeAll needs administrator rights to remove an item, the Windows prompt shows "Unknown publisher".
- **Scans run with your own permissions:** some Windows folders can't be read, so a scan of the Windows drive is usually marked partial. FreeAll lists what it couldn't read, and Windows Storage shows those areas as "Not readable". Scanning as administrator isn't available yet.
- **Large text sizes:** at 150 % text size or more, in a window that isn't maximised, pages with a list beside a details pane can cut off the details pane. Maximise the window.
- **English only.** Dates and numbers follow your Windows regional format.
- **Uninstaller copy left behind:** the uninstaller leaves a copy of itself (about 2 MB) in your temporary folder, because a running program can't delete itself. Windows' Storage Sense clears it.
- **Folder changes before removal:** when you remove a folder, anything added to it after FreeAll last checked it goes too.
- **How changes are detected:** before removing a file, FreeAll checks that it's the same file, with the same size and modified time as when it was scanned. It doesn't compare the contents, so a program that rewrites a file with the same size and puts the old modified time back isn't noticed. The Recycle Bin, the default, keeps such a file recoverable.

### Testing so far

Tested by its developers on Windows 11 Pro 26H2 with more than 1,000 automated tests, including install, upgrade and uninstall, and cleanup and restore. Not yet done:

- an independent security review;
- testing on other Windows 11 versions;
- a full review with Narrator and high-contrast themes.

Please report problems at https://github.com/Sejo9/codename-freeall/issues.

### Files

- `FreeAll-Setup-1.0.1-x64.exe`: the installer.
- `SHA256SUMS.txt`: checksums of the files.
- `FreeAll-1.0.1-x64.spdx.json`: the list of components FreeAll includes (SPDX software bill of materials).
- `manifest.json` and `manifest.json.sig`: the signed update information FreeAll checks.
## Release acceptance record (maintainers)

Not part of the published notes; the release workflow publishes only the
version's own section.
### 1.0.1 (release acceptance, `02-product-requirements.md` §6)

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

The product owner chose to release 1.0.1 (and 1.0.2, which adds only the icon) on 2026-11-09 with the gates above
still open; they remain the next work (`19-blocked-features.md`).
### Release files

`build.ps1 -Configuration Release -Locked -Publish` writes to
`artifacts/release`:

-   `FreeAll-Setup-<version>-x64.exe`: the installer with the app inside.
-   `FreeAll-<version>-x64.spdx.json`: SPDX 2.3 software bill of materials,
    from the lock files.
-   `SHA256SUMS.txt`: checksums of the files above.

Pushing a version tag runs `.github/workflows/release.yml`, which builds these
files, signs the update manifest and publishes them all as a GitHub release
(`22-operations-runbook.md` §7).
