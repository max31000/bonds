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
  failed check.
---

# Pre-push check (BondAnalytics)

## Why this exists

Pushing to `main` triggers two GitHub Actions pipelines that **build, test, AND deploy** to the
VDS ([.github/workflows/backend.yml](../../../.github/workflows/backend.yml),
[frontend.yml](../../../.github/workflows/frontend.yml)). A red pipeline means no deploy and a
Telegram failure alert. The most common breakage is the **frontend TypeScript type check**
(`yarn typecheck`), which `vite` does not catch during `yarn dev`, so it only blows up in CI.

This skill runs the same steps CI runs, locally, so you catch failures before pushing.

## How to use it

Run the checks **relevant to what changed** (mirror CI's path filters), from the repo root.
Run every relevant check (don't stop at the first failure) so one pass surfaces all problems.
If unsure what changed, run both backend and frontend.

> The commands below ARE the checklist — run them directly. (There is no `scripts/pre-push-check.sh`
> in the repo; if one is later added as a convenience wrapper, it must run exactly these steps.)

### 0. Universal guards (cheap, always run)

```bash
# No real .env may be committed (only .env.example). Broker tokens / JWT secrets live there.
git ls-files --error-unmatch .env 2>/dev/null && echo "FAIL: .env is tracked — must not be committed" || echo "OK: no tracked .env"
# yarn.lock must not drift from package.json (CI uses --frozen-lockfile and fails on drift).
git diff --name-only origin/main...HEAD HEAD | grep -q '^bonds-web/package\.json$' \
  && ! git diff --name-only origin/main...HEAD HEAD | grep -q '^bonds-web/yarn\.lock$' \
  && echo "WARN: package.json changed but yarn.lock did not — run 'yarn install' and commit yarn.lock" \
  || echo "OK: lockfile in sync"
```

### 1. Backend — run if `src/**`, `tests/**`, or `Bonds.sln` changed (mirrors backend.yml)

```bash
dotnet restore Bonds.sln
dotnet build Bonds.sln --no-restore -c Release
dotnet test tests/Bonds.Tests/Bonds.Tests.csproj --no-build -c Release --verbosity normal
DOTNET_ENVIRONMENT=Testing dotnet test tests/Bonds.IntegrationTests/Bonds.IntegrationTests.csproj --no-build -c Release --verbosity normal
```

### 2. Frontend — run if `bonds-web/**` changed (mirrors frontend.yml; uses yarn + frozen lockfile)

```bash
cd bonds-web
yarn install --frozen-lockfile
yarn typecheck     # tsc -b --noEmit — THE step that usually reddens CI
yarn lint          # eslint — extra vs CI; catches real bugs, keeps the bar up
yarn test:run      # vitest
yarn build         # tsc -b && vite build
```

### 3. Optional (before a release-y push)

```bash
# CI builds & pushes this image; a build failure breaks the pipeline.
docker build -f src/Bonds.Api/Dockerfile -t bonds-api:prepush-check .
# Formatting (not in CI today, but keeps the tree clean):
dotnet format Bonds.sln --verify-no-changes
```

## What it checks (and why)

- **Backend** — `dotnet restore` → `build -c Release` (compile/warnings-as-errors) → unit tests
  (`tests/Bonds.Tests`) → integration tests (`tests/Bonds.IntegrationTests`, needs
  `DOTNET_ENVIRONMENT=Testing`).
- **Frontend** — `yarn install --frozen-lockfile` → `yarn typecheck` (**the usual CI breaker**) →
  `yarn lint` → `yarn test:run` (vitest) → `yarn build`.
- **Guards** — no tracked `.env`; `yarn.lock` not drifted from `package.json`.

## Interpreting the result

- **All steps pass** → safe to push.
- **Any failure** → fix the root cause, then re-run that step (and the suite). Treat the failing
  command's own output as the source of truth (e.g. a `tsc` error points at the file:line and the
  type mismatch — fix the types, don't loosen the check).
- **A required tool missing** (`dotnet`/`yarn` not on PATH) → don't treat the run as green; install
  it or run the missing side on a machine that has it before pushing.

## Hard rule

Do **not** run `git push` until the checks for the scope you're pushing pass. If the user
explicitly insists on pushing despite a failure, surface exactly which checks failed and that CI
will likely go red, and let them make the call — but never push silently over a red local check.
