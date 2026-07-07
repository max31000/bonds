#!/usr/bin/env bash
#
# pre-push-check.sh — local mirror of the GitHub CI pipelines (backend.yml + frontend.yml)
# plus a few extra guards. Run this before pushing to origin/main so the pipeline goes green.
#
# It runs EVERY relevant check (it does not stop at the first failure), then prints a summary
# and exits non-zero if anything required failed. That way one run surfaces all problems.
#
# Usage:
#   scripts/pre-push-check.sh            # auto-detect scope from changed files vs origin/main
#   scripts/pre-push-check.sh --all      # force-run backend + frontend regardless of changes
#   scripts/pre-push-check.sh --backend  # backend checks only
#   scripts/pre-push-check.sh --frontend # frontend checks only
#   scripts/pre-push-check.sh --docker   # also run the backend Docker image build (slow)
#   scripts/pre-push-check.sh --format   # also run `dotnet format --verify-no-changes` (slow)
#
# Flags combine, e.g. `--all --docker`.

set -uo pipefail

# ── Resolve repo root so the script works from any CWD ──────────────────────────────────────
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

WEB_DIR="bonds-web"
SLN="Bonds.sln"
DOCKERFILE="src/Bonds.Api/Dockerfile"
BASE_REF="origin/main"

# ── Colors (disabled when not a TTY) ────────────────────────────────────────────────────────
if [ -t 1 ]; then
  RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; BLUE=$'\033[34m'; BOLD=$'\033[1m'; RESET=$'\033[0m'
else
  RED=""; GREEN=""; YELLOW=""; BLUE=""; BOLD=""; RESET=""
fi

# ── Flags ───────────────────────────────────────────────────────────────────────────────────
FORCE_ALL=0; ONLY_BACKEND=0; ONLY_FRONTEND=0; RUN_DOCKER=0; RUN_FORMAT=0
for arg in "$@"; do
  case "$arg" in
    --all)      FORCE_ALL=1 ;;
    --backend)  ONLY_BACKEND=1 ;;
    --frontend) ONLY_FRONTEND=1 ;;
    --docker)   RUN_DOCKER=1 ;;
    --format)   RUN_FORMAT=1 ;;
    -h|--help)  grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "${YELLOW}Unknown arg: $arg${RESET}" ;;
  esac
done

# ── Result tracking ─────────────────────────────────────────────────────────────────────────
declare -a RESULTS   # "PASS|FAIL|SKIP\tlabel"
FAILED=0

step() {
  # step "Label" command...
  local label="$1"; shift
  echo ""
  echo "${BOLD}${BLUE}▶ ${label}${RESET}"
  echo "  ${YELLOW}\$ $*${RESET}"
  if "$@"; then
    echo "  ${GREEN}✔ ${label} — OK${RESET}"
    RESULTS+=("PASS	${label}")
  else
    echo "  ${RED}✗ ${label} — FAILED${RESET}"
    RESULTS+=("FAIL	${label}")
    FAILED=1
  fi
}

skip() {
  local label="$1"; local why="${2:-}"
  echo ""
  echo "${YELLOW}↷ SKIP: ${label}${RESET}${why:+ (${why})}"
  RESULTS+=("SKIP	${label}${why:+ (${why})}")
}

have() { command -v "$1" >/dev/null 2>&1; }

# ── Decide what changed (to mirror the CI path filters) ─────────────────────────────────────
# Backend pipeline triggers on: src/**, tests/**, Bonds.sln, .github/workflows/backend.yml
# Frontend pipeline triggers on: bonds-web/**, .github/workflows/frontend.yml
detect_changes() {
  local files=""
  if git rev-parse --verify "$BASE_REF" >/dev/null 2>&1; then
    files+=$'\n'"$(git diff --name-only "${BASE_REF}...HEAD" 2>/dev/null)"
  fi
  # Also include uncommitted + staged changes so you catch work that isn't committed yet.
  files+=$'\n'"$(git diff --name-only HEAD 2>/dev/null)"
  files+=$'\n'"$(git diff --name-only --cached 2>/dev/null)"
  printf '%s' "$files"
}

CHANGED="$(detect_changes)"

backend_touched() {
  printf '%s' "$CHANGED" | grep -Eq '^(src/|tests/|Bonds\.sln|\.github/workflows/backend\.yml)'
}
frontend_touched() {
  printf '%s' "$CHANGED" | grep -Eq '^(bonds-web/|\.github/workflows/frontend\.yml)'
}

DO_BACKEND=0; DO_FRONTEND=0
if [ "$ONLY_BACKEND" = 1 ]; then DO_BACKEND=1; fi
if [ "$ONLY_FRONTEND" = 1 ]; then DO_FRONTEND=1; fi
if [ "$ONLY_BACKEND" = 0 ] && [ "$ONLY_FRONTEND" = 0 ]; then
  if [ "$FORCE_ALL" = 1 ]; then
    DO_BACKEND=1; DO_FRONTEND=1
  else
    backend_touched  && DO_BACKEND=1
    frontend_touched && DO_FRONTEND=1
    # If nothing matched (e.g. only docs changed, or no upstream), be safe and run everything.
    if [ "$DO_BACKEND" = 0 ] && [ "$DO_FRONTEND" = 0 ]; then
      echo "${YELLOW}No backend/frontend changes detected vs ${BASE_REF}; running ALL checks to be safe.${RESET}"
      DO_BACKEND=1; DO_FRONTEND=1
    fi
  fi
fi

echo "${BOLD}Pre-push check — repo: ${REPO_ROOT}${RESET}"
echo "Scope: backend=$([ $DO_BACKEND = 1 ] && echo yes || echo no), frontend=$([ $DO_FRONTEND = 1 ] && echo yes || echo no), docker=$([ $RUN_DOCKER = 1 ] && echo yes || echo no)"

# ── Universal guards (cheap, always run) ────────────────────────────────────────────────────
echo ""
echo "${BOLD}${BLUE}▶ Repository guards${RESET}"

# 1. Never commit a real .env (only .env.example is allowed). Broker tokens / JWT secrets live here.
if git ls-files --error-unmatch .env >/dev/null 2>&1; then
  echo "  ${RED}✗ .env is tracked by git — it must NOT be committed (secrets leak).${RESET}"
  RESULTS+=("FAIL	No tracked .env secret file"); FAILED=1
else
  echo "  ${GREEN}✔ No tracked .env file${RESET}"; RESULTS+=("PASS	No tracked .env secret file")
fi

# 2. Lockfile drift: if bonds-web/package.json changed but yarn.lock didn't, frozen-lockfile will fail in CI.
if printf '%s' "$CHANGED" | grep -q '^bonds-web/package\.json$' && ! printf '%s' "$CHANGED" | grep -q '^bonds-web/yarn\.lock$'; then
  echo "  ${YELLOW}⚠ package.json changed but yarn.lock did not — run 'yarn install' in bonds-web and commit yarn.lock.${RESET}"
  RESULTS+=("FAIL	yarn.lock in sync with package.json"); FAILED=1
else
  echo "  ${GREEN}✔ yarn.lock / package.json in sync (no obvious drift)${RESET}"; RESULTS+=("PASS	yarn.lock in sync with package.json")
fi

# ── Backend (mirror backend.yml) ────────────────────────────────────────────────────────────
if [ "$DO_BACKEND" = 1 ]; then
  if ! have dotnet; then
    skip "Backend checks" "dotnet SDK not on PATH"
  else
    step "Backend: restore"           dotnet restore "$SLN"
    step "Backend: build (Release)"   dotnet build "$SLN" --no-restore -c Release
    step "Backend: unit tests"        dotnet test tests/Bonds.Tests/Bonds.Tests.csproj --no-build -c Release --verbosity normal
    step "Backend: integration tests" bash -c "DOTNET_ENVIRONMENT=Testing dotnet test tests/Bonds.IntegrationTests/Bonds.IntegrationTests.csproj --no-build -c Release --verbosity normal"
    if [ "$RUN_FORMAT" = 1 ]; then
      step "Backend: dotnet format --verify-no-changes" dotnet format "$SLN" --verify-no-changes
    else
      skip "Backend: dotnet format" "pass --format to enable"
    fi
  fi
fi

# ── Frontend (mirror frontend.yml) ──────────────────────────────────────────────────────────
if [ "$DO_FRONTEND" = 1 ]; then
  if ! have yarn; then
    skip "Frontend checks" "yarn not on PATH"
  else
    step "Frontend: install (--frozen-lockfile)" bash -c "cd '$WEB_DIR' && yarn install --frozen-lockfile"
    step "Frontend: typecheck"                   bash -c "cd '$WEB_DIR' && yarn typecheck"
    # eslint is NOT in CI today, but a clean lint catches real bugs and keeps the bar high.
    step "Frontend: lint (eslint)"               bash -c "cd '$WEB_DIR' && yarn lint"
    step "Frontend: tests (vitest run)"          bash -c "cd '$WEB_DIR' && yarn test:run"
    step "Frontend: build (tsc -b && vite build)" bash -c "cd '$WEB_DIR' && yarn build"
  fi
fi

# ── Optional: backend Docker image build (CI builds & pushes it; build failure breaks the pipeline) ──
if [ "$RUN_DOCKER" = 1 ]; then
  if have docker && docker info >/dev/null 2>&1; then
    step "Backend: docker build" docker build -f "$DOCKERFILE" -t bonds-api:prepush-check .
  else
    skip "Backend: docker build" "docker not available / daemon not running"
  fi
fi

# ── Summary ─────────────────────────────────────────────────────────────────────────────────
echo ""
echo "${BOLD}──────────────── Summary ────────────────${RESET}"
for r in "${RESULTS[@]}"; do
  status="${r%%	*}"; label="${r#*	}"
  case "$status" in
    PASS) echo "  ${GREEN}PASS${RESET}  $label" ;;
    FAIL) echo "  ${RED}FAIL${RESET}  $label" ;;
    SKIP) echo "  ${YELLOW}SKIP${RESET}  $label" ;;
  esac
done
echo "${BOLD}─────────────────────────────────────────${RESET}"

if [ "$FAILED" = 1 ]; then
  echo "${RED}${BOLD}✗ Pre-push check FAILED — fix the items above before pushing.${RESET}"
  exit 1
fi
echo "${GREEN}${BOLD}✔ All pre-push checks passed — safe to push.${RESET}"
exit 0
