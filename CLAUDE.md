# BondAnalytics — agent instructions

## MANDATORY: pre-push gate

**Before running `git push` (to any branch on `origin`) or opening/updating a PR, you MUST run the
`pre-push-check` skill and get a clean pass.** This is non-negotiable for every agent working in
this repo — pushing to `main` auto-deploys to the VDS, and a red pipeline blocks the deploy.

- Run it via the `pre-push-check` skill — the skill lists the exact commands to run.
- It mirrors both CI pipelines locally (.NET build + unit/integration tests, frontend
  `yarn install --frozen-lockfile` / `typecheck` / `lint` / `test` / `build`) plus secret and
  lockfile guards. The frontend **typecheck** is the step that has reddened CI before — `vite dev`
  does not catch type errors.
- Do **not** push while any required check fails or a required tool is missing. If a check fails,
  fix the root cause and re-run. If the user explicitly insists on pushing anyway, tell them
  exactly which checks failed and that CI will likely go red — never push silently over a red run.

The source of truth is [.claude/skills/pre-push-check/SKILL.md](.claude/skills/pre-push-check/SKILL.md) —
it contains the runnable checklist. Add new checks there.

## Optional hard enforcement (git hook)

To make the gate fire automatically on `git push`, add a `pre-push` git hook that runs the same
checklist (e.g. under `scripts/git-hooks/` with `git config core.hooksPath scripts/git-hooks`).
Not committed by default — it runs the full suite on every push, so it's left opt-in.

## Project conventions

- Staged implementation plans live in [`plan/`](plan/) (numbered; read in order). The business
  spec is [`bond-portfolio-analytics-spec.md`](bond-portfolio-analytics-spec.md).
- Frontend uses **yarn** (not npm) — match CI: `cd bonds-web && yarn <script>`.
- Broker tokens / JWT secrets are read-only secrets: never commit a real `.env`, never log or
  send the token to the frontend.
