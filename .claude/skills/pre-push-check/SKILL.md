---
name: pre-push-check
description: >
  Run BEFORE every `git push` to origin (and before opening or updating a PR) on the
  BondAnalytics repo, to make sure the GitHub Actions pipelines (backend.yml + frontend.yml)
  go green. Use this whenever you are about to push, are preparing to push, just finished a
  change you intend to push, the user says "push", "запушить", "залить", "отправить в origin",
  "make CI pass", "пайплайн", "проверь перед пушем", or asks why CI is red. It mirrors CI
  locally: .NET restore/build/unit+integration tests AND the frontend yarn install/typecheck/
  test/build (the same TypeScript type check that has broken the pipeline before), plus secret
  and lockfile guards. Always run it and get a clean pass before pushing — do not push on a
  failed or skipped-required check.
---

# Pre-push check (BondAnalytics)

## Why this exists

Pushing to `main` triggers two GitHub Actions pipelines that **build, test, AND deploy** to the
VDS ([.github/workflows/backend.yml](../../../.github/workflows/backend.yml),
[frontend.yml](../../../.github/workflows/frontend.yml)). A red pipeline means no deploy and a
Telegram failure alert. The most common breakage is the **frontend TypeScript type check**
(`yarn typecheck`), which `vite` does not catch during `yarn dev`, so it only blows up in CI.

This skill runs the exact same steps CI runs, locally, so you catch failures before pushing.

## How to use it

The whole checklist is a single script — **run it, don't reimplement it**:

```bash
./scripts/pre-push-check.sh
```

It auto-detects whether backend and/or frontend changed (mirroring CI's path filters) and runs
the matching checks. It runs **all** checks (doesn't stop at the first failure), then prints a
PASS/FAIL/SKIP summary and exits non-zero if anything required failed.

Useful variants:

| Command | When |
|---|---|
| `./scripts/pre-push-check.sh` | Default — auto-detect scope from changed files vs `origin/main`. |
| `./scripts/pre-push-check.sh --all` | Force both backend + frontend (use when unsure). |
| `./scripts/pre-push-check.sh --backend` / `--frontend` | One side only. |
| `./scripts/pre-push-check.sh --docker` | Also build the backend Docker image (CI builds & pushes it; add before a release-y push). |
| `./scripts/pre-push-check.sh --format` | Also run `dotnet format --verify-no-changes`. |

## What it checks (and why)

**Backend — mirrors `backend.yml`:**
1. `dotnet restore Bonds.sln`
2. `dotnet build Bonds.sln --no-restore -c Release` — build warnings-as-errors / compile breaks.
3. `dotnet test` unit tests (`tests/Bonds.Tests`).
4. `dotnet test` integration tests (`tests/Bonds.IntegrationTests`) with `DOTNET_ENVIRONMENT=Testing`.

**Frontend — mirrors `frontend.yml` (uses yarn + frozen lockfile, exactly like CI):**
1. `yarn install --frozen-lockfile`
2. `yarn typecheck` (`tsc -b --noEmit`) — **the check that usually reddens CI.**
3. `yarn lint` (eslint) — extra vs CI; catches real bugs, keeps the bar up.
4. `yarn test:run` (vitest)
5. `yarn build` (`tsc -b && vite build`)

**Universal guards (cheap, always run):**
- No tracked `.env` (only `.env.example` belongs in git — broker tokens / JWT secrets must never be committed).
- `yarn.lock` not drifted from `package.json` (CI's `--frozen-lockfile` fails on drift).

## Interpreting the result

- **All PASS** → safe to push.
- **Any FAIL** → fix it, then re-run. Treat the failing step's own output as the source of truth
  (e.g. a `tsc` error points at the file:line and the type mismatch — fix the types, don't loosen
  the check). Re-run the whole script after fixing; cheap checks running again is fine.
- **SKIP on a required tool** (e.g. `dotnet`/`yarn` not on PATH) → don't treat the run as green.
  Install the tool or run the missing side on a machine that has it before pushing.

## Hard rule

Do **not** run `git push` until this script exits 0 for the scope you're pushing. If the user
explicitly insists on pushing despite a failure, surface exactly which checks failed and that CI
will likely go red, and let them make the call — but never push silently over a red local check.

## Extending the checklist

If you add a real check, put it in `scripts/pre-push-check.sh` (the single source of truth so a
git hook and humans run the same thing) rather than only describing it here. Good candidates as
the project grows: a secrets/regex scan for accidentally committed tokens, `dotnet format` in CI,
adding `yarn lint` to `frontend.yml`, or an OpenAPI/contract drift check between backend DTOs and
frontend `api/types.ts`.
