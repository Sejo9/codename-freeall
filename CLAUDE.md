# FreeAll: agent guide

- Mandatory rules: `docs/15-ai-agent-rules.md`. Read the checklist (`docs/17`) and blockers (`docs/19`) at session start.
- Precedence: `docs/16-developer-decisions.md` > PRD (`02`) / functional spec (`04`); `08` security and `10` cleanup win for destructive or security-sensitive behaviour.
- Never: automatic deletion, silent fallback to permanent deletion, following reparse points by default, arbitrary helper commands, telemetry, hash-only duplicate claims, cross-volume duplicate groups.
- Tests that touch the Recycle Bin must remove their own items by the path Windows returned (`CleanupFixture`). Never run cleanup against anything a test or capture did not create.
- Layering (TD-001, enforced by `tests/FreeAll.Architecture.Tests`): feature projects and Infrastructure reference only Core + Application; Desktop is the composition root; Helper references only Core.
- New packages: add to `Directory.Packages.props` and record in `docs/23-dependency-register.md` first.
- Build and test: `./build.ps1` (CI: `./build.ps1 -Configuration Release -Locked -Publish`). Warnings are errors.
- After changing package references, run a normal `dotnet restore` so the `packages.lock.json` files update, then commit them.
- UI changes: build Debug and run `FreeAll.exe --capture-screens <scratch folder> --data-dir <scratch data folder>` (add `--capture-scan <path>` for pages with data), then review the PNGs in light and dark before reporting UI work as done. Colours only via `DynamicResource Color.*` tokens (TD-011).
- Scanner and index rules: TD-018 to TD-023; incremental updates, hard links, search, export, review queue, retention, charts: TD-025 to TD-032; residue and temporary files: TD-033 to TD-036; duplicates: TD-037 to TD-041 (the Residue and Duplicates projects only read; a guard test enforces it); cleanup, quarantine and the helper: TD-042 to TD-047; Windows storage, installer, updates, accessibility and release files: TD-048 to TD-052 (Windows Storage only opens Windows' own tools; the installer references no FreeAll project and runs as invoker). Chart colours: `Color.Chart.*` tokens, assigned by category (`ChartPalette`). Persisted enum values in Core never change; schema changes are new migration scripts, never edits.
- The installer is published with Native AOT and needs the MSVC linker; `build.ps1` puts `vswhere.exe` on PATH. Installer tests and smoke runs must redirect every location (`/root`, `/start-menu`, `/data`, `/registry-key Software\FreeAll.Tests\…`); never install into the real locations from a test.
- Release-only values (helper digest, update address and key) are embedded by `build.ps1` as assembly metadata; Debug builds have none, so they can't start the helper or check for updates.
- Accessibility: unnamed icons use `controls:Glyph`; buttons with an icon and a label need `AutomationProperties.Name` (architecture tests and the capture's `accessibility.txt` check this).
- WPF projects do not get `System.IO` as an implicit using (it conflicts with `System.Windows.Shapes.Path`); add `using System.IO;` explicitly.
- At session end, update `docs/17`, `docs/18` and `docs/19`, and report in the format in `docs/15` §8.
