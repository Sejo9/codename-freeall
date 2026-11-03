# FreeAll: agent guide

- Mandatory rules: `docs/15-ai-agent-rules.md`. Read the checklist (`docs/17`) and blockers (`docs/19`) at session start.
- Precedence: `docs/16-developer-decisions.md` > PRD (`02`) / functional spec (`04`); `08` security and `10` cleanup win for destructive or security-sensitive behaviour.
- Never: automatic deletion, silent fallback to permanent deletion, following reparse points by default, arbitrary helper commands, telemetry, hash-only duplicate claims, cross-volume duplicate groups.
- Layering (TD-001, enforced by `tests/FreeAll.Architecture.Tests`): feature projects and Infrastructure reference only Core + Application; Desktop is the composition root; Helper references only Core.
- New packages: add to `Directory.Packages.props` and record in `docs/23-dependency-register.md` first.
- Build and test: `./build.ps1` (CI: `./build.ps1 -Configuration Release -Locked -Publish`). Warnings are errors.
- After changing package references, run a normal `dotnet restore` so the `packages.lock.json` files update, then commit them.
- UI changes: build Debug and run `FreeAll.exe --capture-screens <scratch folder> --data-dir <scratch data folder>` (add `--capture-scan <path>` for pages with data), then review the PNGs in light and dark before reporting UI work as done. Colours only via `DynamicResource Color.*` tokens (TD-011).
- Scanner and index rules: TD-018 to TD-023. Persisted enum values in Core never change; schema changes are new migration scripts, never edits.
- WPF projects do not get `System.IO` as an implicit using (it conflicts with `System.Windows.Shapes.Path`); add `using System.IO;` explicitly.
- At session end, update `docs/17`, `docs/18` and `docs/19`, and report in the format in `docs/15` §8.
