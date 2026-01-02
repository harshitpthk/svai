#!/usr/bin/env bash
set -euo pipefail

# Installs the svai CLI (macOS/Linux) by publishing and symlinking into a PATH directory.
#
# Usage:
#   ./install.sh            # installs to /usr/local/bin (default)
#   PREFIX=~/.local ./install.sh
#   BINDIR=~/.local/bin ./install.sh
#
# Notes:
# - This script does NOT modify shell rc files; ensure your chosen BINDIR is on PATH.

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT_DIR/src/StockScreener.Cli/StockScreener.Cli.csproj"
PUBLISH_DIR="$ROOT_DIR/out/svai"
BIN_NAME="svai"

PREFIX="${PREFIX:-/usr/local}"
BINDIR="${BINDIR:-$PREFIX/bin}"

echo "Publishing svai..."
dotnet publish -c Release -o "$PUBLISH_DIR" "$PROJECT" >/dev/null

if [[ ! -x "$PUBLISH_DIR/$BIN_NAME" ]]; then
  echo "Error: expected executable not found at $PUBLISH_DIR/$BIN_NAME" >&2
  exit 1
fi

echo "Installing to: $BINDIR/$BIN_NAME"
mkdir -p "$BINDIR"

TARGET="$PUBLISH_DIR/$BIN_NAME"
LINK="$BINDIR/$BIN_NAME"

# Use a symlink so upgrades are just re-running the script.
ln -sf "$TARGET" "$LINK"

# If installing into a system directory without permissions, guide user.
if [[ ! -w "$BINDIR" ]]; then
  cat <<EOF

Note: '$BINDIR' is not writable.
Re-run with sudo, e.g.:
  sudo -E ./install.sh

Or install to a user-writable prefix, e.g.:
  PREFIX=~/.local ./install.sh
EOF
fi

echo "Done. Try: svai --help"
