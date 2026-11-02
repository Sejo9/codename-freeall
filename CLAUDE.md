# FreeAll: agent guide

- Mandatory rules: `docs/15-ai-agent-rules.md`. Read the checklist (`docs/17`) and blockers (`docs/19`) at session start.
- Precedence: `docs/16-developer-decisions.md` > PRD (`02`) / functional spec (`04`); `08` security and `10` cleanup win for destructive or security-sensitive behaviour.
- Never: automatic deletion, silent fallback to permanent deletion, following reparse points by default, arbitrary helper commands, telemetry, hash-only duplicate claims, cross-volume duplicate groups.
- Layering (TD-001, enforced by `tests/FreeAll.Architecture.Tests`): feature projects and Infrastructure reference only Core + Application; Desktop is the composition root; Helper references only Core.
- New packages: add to `Directory.Packages.props` and record in `docs/23-dependency-register.md` first.
- Build and test: `./build.ps1` (CI: `./build.ps1 -Configuration Release -Locked -Publish`). Warnings are errors.
- After changing package references, run a normal `dotnet restore` so the `packages.lock.json` files update, then commit them.
- At session end, update `docs/17`, `docs/18` and `docs/19`, and report in the format in `docs/15` §8.
