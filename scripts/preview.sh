#!/usr/bin/env bash
set -euo pipefail

PORT="${1:-8000}"

if [ ! -f "index.html" ]; then
  echo "Optional preview server cannot start: index.html was not found in this folder."
  echo "Build the app with the Build Coach first."
  exit 1
fi

echo "Starting optional static app preview server..."
echo "Direct browser opening is the default: open index.html directly when possible."
echo "Open: http://localhost:${PORT}"
echo "Press Ctrl+C to stop the preview server."

python3 -m http.server "${PORT}"
