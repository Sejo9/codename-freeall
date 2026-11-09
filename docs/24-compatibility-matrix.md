# FreeAll --- Windows Compatibility Matrix

Required by `13-deployment-release.md` §6 and DEC-003/DEC-020. A Windows 11
release is **supported** only while Microsoft services it for the edition in
question **and** a FreeAll build has passed the checks below on it. Releases
enter the matrix after testing and leave it when servicing ends.

## 1. Servicing basis

Source: Microsoft, *Windows 11 release information*
(<https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information>),
page dated 2026-10-28, read 2026-11-08. Re-check before every release; dates
differ by edition.

  -------------------------------------------------------------------------------------------------
  Version   OS build   Available     End of updates:          End of updates:        FreeAll status
                                     Home, Pro, Pro           Enterprise, Education  (2026-11-08)
                                     Education, Pro for       IoT Enterprise
                                     Workstations
  --------- ---------- ------------- ------------------------ ---------------------- --------------
  26H2      26300      2026-09-29    2028-10-10               2029-10-09             Tested (Pro)

  26H1      28000      2026-02-10    2028-03-14               2029-03-13             Not tested; new
                                                                                     devices only,
                                                                                     mostly ARM64

  25H2      26200      2025-09-30    2027-10-12               2028-10-10             Not tested

  24H2      26100      2024-10-01    Ended                    2027-10-12             Not tested
                                                                                     (Enterprise,
                                                                                     Education only)

  24H2 LTSC 26100      2024-10-01    ---                      2029-10-09             Not tested
                                                              (IoT LTSC 2034-10-10)

  23H2      22631      2023-10-31    Ended                    2026-11-10             Leaves servicing
                                                                                     in two days; not
                                                                                     added
  -------------------------------------------------------------------------------------------------

22H2 and 21H2 are out of servicing for every edition and are not supported.
Windows 10 is not supported (DEC-001); the app refuses to start below build
22000 (F-041).

## 2. What "tested" means

A row moves to **Supported** when, on that release and edition, with a
published (`build.ps1 -Configuration Release -Publish`) x64 build:

1.  `build.ps1 -Configuration Release` passes (unit, integration, security,
    installer and short soak tests).
2.  The setup file installs as a standard user, FreeAll starts, and the
    uninstaller removes it (`12-quality-assurance.md` §3.3 A).
3.  A full scan and an update of the Windows drive finish; Windows Storage,
    Temporary Files and Duplicate Files show results.
4.  Cleanup to the Recycle Bin and to quarantine, and a restore, work on
    disposable files (`12-quality-assurance.md` §3.2).
5.  The elevated helper runs through UAC (`12-quality-assurance.md` §3.2 A
    and B).
6.  Every Windows tool button on the Windows Storage page opens its screen.

`tools/Test-Compatibility.ps1` runs steps 1 and 2 (test locations only) and
writes the Windows release, edition and build into a dated record. Steps 3 to
6 need a person.

## 3. Results

  ------------------------------------------------------------------------------------------------------------------
  Date         Windows                   Arch  FreeAll   1 Tests    2 Install  3 Scan     4 Cleanup  5 Helper  6 Tools
  ------------ ------------------------- ----- --------- ---------- ---------- ---------- ---------- --------- -------
  2026-11-08   11 Pro 26H2 26300.9711    x64   0.0.1     Pass       Pass ¹     Pass       Pass ²     Not run ³ Pass ⁴
  ------------------------------------------------------------------------------------------------------------------

1.  Installed and uninstalled silently into test locations and a test
    registry key, through the real setup and the installed uninstaller. The
    interactive dialogs and the real `%LocalAppData%\Programs\FreeAll` location
    have not been exercised on this PC: a release build started from there
    would open and migrate the developer's real FreeAll database.
2.  Automated cleanup and restore suites (`FreeAll.Cleanup.Tests`) on real
    files, and the capture run's quarantine of files it created.
3.  BLK-019: the development agent can't answer a UAC prompt.
4.  The launch targets exist on this PC and the allowlist test passes; a
    person still has to press each button once.

## 4. Architectures

  ----------------------------------------------------------------------------
  Architecture   Status
  -------------- -------------------------------------------------------------
  x64            Primary; the only build produced (DEC-004).

  ARM64          **Not supported in this release** (DEC-005, BLK-001). x64
                 builds may run under emulation on ARM64 Windows 11, but that
                 is untested and unsupported. Windows 11 26H1 ships mainly on
                 ARM64 devices, so it stays out of the matrix until BLK-001.

  x86            Never (DEC-006).
  ----------------------------------------------------------------------------

## 5. Known issues by release

None recorded. Add an entry here, with the FreeAll version and a workaround,
when a release-specific defect is found.

## 6. .NET servicing

FreeAll is self-contained on .NET 10 (LTS, supported until November 2028 per
`02-product-requirements.md` §2). Each release ships the .NET runtime it was
built with, so the .NET patch level is that of the build machine's SDK at
release time (10.0.14 runtime packages on 2026-11-08). Re-check the .NET
support policy before each release.
