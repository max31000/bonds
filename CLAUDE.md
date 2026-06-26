# BondAnalytics — agent instructions

## MANDATORY: pre-push gate

**Before running `git push` (to any branch on `origin`) or opening/updating a PR, you MUST run the
`pre-push-check` skill and get a clean pass.** This is non-negotiable for every agent working in
this repo — pushing to `main` auto-deploys to the VDS, and a red pipeline blocks the deploy.

- Run it via the skill, or directly: `./scripts/pre-push-check.sh`
- It mirrors both CI pipelines locally (.NET build + unit/integration tests, frontend
  `yarn install --frozen-lockfile` / `typecheck` / `lint` / `test` / `build`) plus secret and
  lockfile guards. The frontend **typecheck** is the step that has reddened CI before — `vite dev`
  does not catch type errors.
- Do **not** push while any required check is FAIL or a required tool is SKIP. If a check fails,
  fix the root cause and re-run. If the user explicitly insists on pushing anyway, tell them
  exactly which checks failed and that CI will likely go red — never push silently over a red run.

See [.claude/skills/pre-push-check/SKILL.md](.claude/skills/pre-push-check/SKILL.md) for details.
The single source of truth is [scripts/pre-push-check.sh](scripts/pre-push-check.sh) — add new
checks there, not just in prose.

## Optional hard enforcement (git hook)

To make the gate fire automatically on `git push` regardless of which agent (or human) pushes:

```bash
git config core.hooksPath scripts/git-hooks
```

This activates [scripts/git-hooks/pre-push](scripts/git-hooks/pre-push), which runs the same
script and aborts the push if it fails. Left opt-in because it runs the full suite on every push.

## Project conventions

- Staged implementation plans live in [`plan/`](plan/) (numbered; read in order). The business
  spec is [`bond-portfolio-analytics-spec.md`](bond-portfolio-analytics-spec.md).
- Frontend uses **yarn** (not npm) — match CI: `cd bonds-web && yarn <script>`.
- Broker tokens / JWT secrets are read-only secrets: never commit a real `.env`, never log or
  send the token to the frontend.
