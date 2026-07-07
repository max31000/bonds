# BondAnalytics — agent instructions

## MANDATORY: pre-push gate

**Before `git push` to any branch on `origin` (or opening/updating a PR) you MUST get a clean pass of:**

```bash
scripts/pre-push-check.sh --all
```

It mirrors both CI pipelines (.NET restore/build/unit/integration tests + frontend
`yarn install --frozen-lockfile`/`typecheck`/`lint`/`test:run`/`build`) plus secret/lockfile
guards, runs everything without stopping at the first failure, and prints a summary. Pushing to
`main` auto-deploys to the VDS — a red pipeline blocks the deploy. The frontend **typecheck** is
the step that has reddened CI before (`vite dev` does not catch type errors). Integration tests
need Docker. Do **not** push on any failure or missing tool; if the user insists on pushing over
a red run, name the failing checks explicitly — never push silently.

Details/checklist rationale: [.claude/skills/pre-push-check/SKILL.md](.claude/skills/pre-push-check/SKILL.md).
Optional hard enforcement: `git config core.hooksPath scripts/git-hooks` (opt-in — runs the full
suite on every push).

## Read before implementing anything

**[docs/CODEBASE-GUIDE.md](docs/CODEBASE-GUIDE.md)** — repo patterns (Dapper repositories,
endpoints, DI, migrations, Testcontainers/MSW testing, frontend) and the **units-of-measure
contract** (fractions vs percents vs points vs rubles at every boundary — the repo's most
recurrent bug class, caught three times). Don't rediscover patterns by exploration; read the
guide, and fix it if code disagrees.

## Orchestration playbook (multi-task feature cycles)

Proven across stages 13–30; follow unless the user says otherwise:

1. **Plan docs first**: one self-contained `plan/NN-*.md` per subagent-sized task (template:
   [docs/templates/plan-task-template.md](docs/templates/plan-task-template.md)); commit them on
   a feature branch off `main` before dispatching.
2. **Dispatch** one task at a time to the **`bond-implementer`** agent (model sonnet) on a
   `task-NN-*` branch cut from the feature branch HEAD. Run tasks **sequentially** when they
   touch shared files (AppLayout, Recommendations, types.ts — they usually do); `--ff-only`
   merge each back. Worktree isolation only for fully independent tasks — **worktrees branch
   off `main`, not the feature branch** (learned the hard way).
3. **Review per wave** with the **`bond-reviewer`** agent (git range + plan files + specific
   attention points). Triage: BLOCKER/MAJOR fixed before deploy (small fixes inline by the
   orchestrator, larger ones via bond-implementer), MINOR/NIT → [BACKLOG.md](BACKLOG.md).
4. **Deploy**: pre-push gate → merge feature → `main` → push (auto-deploy) → watch both GitHub
   Actions runs to completion.
5. Escalation contract with agents: they report `NEEDS DECISION` (forks for the owner) and
   `FOLLOWUPS` (out-of-scope findings) — the orchestrator triages both, urgent items go to the
   user immediately, the rest to BACKLOG.md.

## Project conventions

- Business backlog: [BACKLOG.md](BACKLOG.md) (lean, actionable only). Completed staged plans:
  [`plan/`](plan/) (see `plan/README.md` index). Business spec:
  [`bond-portfolio-analytics-spec.md`](bond-portfolio-analytics-spec.md). Historical
  audits/decision docs: [`docs/history/`](docs/history/).
- Frontend uses **yarn** (not npm) — match CI: `cd bonds-web && yarn <script>`.
- Broker tokens / JWT secrets are read-only secrets: never commit a real `.env`, never log or
  send the token to the frontend.
- All analytics are **estimates, not investment advice**: disclaimers are mandatory in UI and
  API responses; wording is "кандидат/оценка", never "купите/продайте".
- New migration = next number in `src/Bonds.Infrastructure/Migrations/` + update
  `MigrationIdempotencyTests` (count + file list).
