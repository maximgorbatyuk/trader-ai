#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_URL="${BACKEND_URL:-http://127.0.0.1:5100}"
FRONTEND_HOST="${FRONTEND_HOST:-127.0.0.1}"
FRONTEND_PORT="${FRONTEND_PORT:-5173}"
FRONTEND_URL="http://${FRONTEND_HOST}:${FRONTEND_PORT}"

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

echo "Starting backend at ${BACKEND_URL}"
ASPNETCORE_URLS="${BACKEND_URL}" dotnet run --project "${ROOT_DIR}/TraderAi/TraderAi/TraderAi.csproj" &
child_pids+=("$!")

echo "Starting frontend at ${FRONTEND_URL}"
VITE_API_BASE_URL="${BACKEND_URL}" npm --prefix "${ROOT_DIR}/frontend" run dev -- --host "${FRONTEND_HOST}" --port "${FRONTEND_PORT}" --strictPort &
child_pids+=("$!")

echo "Frontend: ${FRONTEND_URL}"
echo "Backend: ${BACKEND_URL}"

wait_for_exit
