#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_URL="${BACKEND_URL:-http://127.0.0.1:5100}"
FRONTEND_HOST="${FRONTEND_HOST:-127.0.0.1}"
FRONTEND_PORT="${FRONTEND_PORT:-5173}"
FRONTEND_URL="http://${FRONTEND_HOST}:${FRONTEND_PORT}"

GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[0;33m'
NC='\033[0m'

BACKEND_PROJECT="${ROOT_DIR}/TraderAi/TraderAi/TraderAi.csproj"
FRONTEND_DIR="${ROOT_DIR}/frontend"

# Open a Google search so a developer missing a prerequisite lands on install docs.
open_install_search() {
  local query="$1"
  open "https://www.google.com/search?q=${query// /+}" >/dev/null 2>&1 || true
}

check_prerequisites() {
  local missing=0

  if command -v dotnet >/dev/null 2>&1; then
    printf "${GREEN}✓${NC} .NET SDK %s\n" "$(dotnet --version)"
  else
    printf "${RED}✗${NC} .NET SDK not found — opening install help...\n"
    open_install_search ".net install"
    missing=1
  fi

  if command -v npm >/dev/null 2>&1; then
    printf "${GREEN}✓${NC} npm %s\n" "$(npm --version)"
  else
    printf "${RED}✗${NC} npm not found — opening install help...\n"
    open_install_search "npm install"
    missing=1
  fi

  if [ "$missing" -ne 0 ]; then
    printf "${RED}Missing prerequisites. Install them and re-run ./start-dev.sh${NC}\n"
    exit 1
  fi
}

# Build implicitly restores, so one command covers both a fresh checkout and a restored-but-unbuilt one.
ensure_backend_ready() {
  local restored=0 built=0
  [ -f "${ROOT_DIR}/TraderAi/TraderAi/obj/project.assets.json" ] && restored=1
  find "${ROOT_DIR}/TraderAi/TraderAi/bin" -name "TraderAi.dll" -print -quit 2>/dev/null | grep -q . && built=1

  if [ "$restored" -eq 1 ] && [ "$built" -eq 1 ]; then
    printf "${GREEN}✓${NC} backend restored and built\n"
    return
  fi

  printf "${YELLOW}…${NC} backend not ready — running dotnet build\n"
  if ! dotnet build "${BACKEND_PROJECT}"; then
    printf "${RED}✗${NC} backend build failed\n"
    exit 1
  fi
  printf "${GREEN}✓${NC} backend restored and built\n"
}

ensure_frontend_ready() {
  if [ -d "${FRONTEND_DIR}/node_modules" ]; then
    printf "${GREEN}✓${NC} frontend dependencies installed\n"
    return
  fi

  printf "${YELLOW}…${NC} frontend dependencies missing — running npm install\n"
  if ! npm --prefix "${FRONTEND_DIR}" install; then
    printf "${RED}✗${NC} npm install failed\n"
    exit 1
  fi
  printf "${GREEN}✓${NC} frontend dependencies installed\n"
}

child_pids=()

cleanup() {
  if [ "${#child_pids[@]}" -gt 0 ]; then
    for pid in "${child_pids[@]}"; do
      kill "$pid" 2>/dev/null || true
    done
  fi

  wait 2>/dev/null || true
}

wait_for_exit() {
  while true; do
    for pid in "${child_pids[@]}"; do
      if ! kill -0 "$pid" 2>/dev/null; then
        wait "$pid" 2>/dev/null || true
        return
      fi
    done

    sleep 1
  done
}

trap cleanup INT TERM EXIT

check_prerequisites
ensure_backend_ready
ensure_frontend_ready

echo "Starting backend at ${BACKEND_URL}"
ASPNETCORE_URLS="${BACKEND_URL}" dotnet run --project "${ROOT_DIR}/TraderAi/TraderAi/TraderAi.csproj" &
child_pids+=("$!")

echo "Starting frontend at ${FRONTEND_URL}"
VITE_API_BASE_URL="${BACKEND_URL}" npm --prefix "${ROOT_DIR}/frontend" run dev -- --host "${FRONTEND_HOST}" --port "${FRONTEND_PORT}" --strictPort &
child_pids+=("$!")

echo "Frontend: ${FRONTEND_URL}"
echo "Backend: ${BACKEND_URL}"

wait_for_exit
