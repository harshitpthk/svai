#!/usr/bin/env bash
set -euo pipefail

# Uninstalls the svai CLI (macOS/Linux) by removing the installed symlink.
#
# Usage:
#   ./uninstall.sh
#   PREFIX=~/.local ./uninstall.sh
#   BINDIR=~/.local/bin ./uninstall.sh

BIN_NAME="svai"
PREFIX="${PREFIX:-/usr/local}"
BINDIR="${BINDIR:-$PREFIX/bin}"
LINK="$BINDIR/$BIN_NAME"

if [[ -L "$LINK" || -f "$LINK" ]]; then
  echo "Removing: $LINK"
  rm -f "$LINK"
else
  echo "Nothing to remove at: $LINK"
fi

echo "Done."
