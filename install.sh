#!/usr/bin/env bash
set -euo pipefail

# Installs the svai CLI (macOS/Linux) by publishing and symlinking into a PATH directory.
#
# Behavior:
# - If run without env vars, attempts system install to /usr/local/bin.
# - If that directory isn't writable, automatically falls back to ~/.local/bin.
#
# Usage:
#   ./install.sh
#   PREFIX=~/.local ./install.sh
#   BINDIR=~/.local/bin ./install.sh
#
# Notes:
# - This script does NOT modify shell rc files.

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT_DIR/src/StockScreener.Cli/StockScreener.Cli.csproj"
PUBLISH_DIR="$ROOT_DIR/out/svai"
BIN_NAME="svai"

# Defaults
PREFIX_DEFAULT="/usr/local"
USER_PREFIX_DEFAULT="$HOME/.local"

PREFIX="${PREFIX:-$PREFIX_DEFAULT}"
BINDIR="${BINDIR:-$PREFIX/bin}"

echo "Publishing svai..."
dotnet publish -c Release -o "$PUBLISH_DIR" "$PROJECT" >/dev/null

if [[ ! -x "$PUBLISH_DIR/$BIN_NAME" ]]; then
  echo "Error: expected executable not found at $PUBLISH_DIR/$BIN_NAME" >&2
  exit 1
fi

TARGET="$PUBLISH_DIR/$BIN_NAME"

# If user didn't explicitly set PREFIX or BINDIR and we can't write, fall back.
if [[ -z "${PREFIX+x}" && -z "${BINDIR+x}" ]]; then
  : # (unreachable due to set -u), kept for clarity
fi

if [[ "${PREFIX:-$PREFIX_DEFAULT}" == "$PREFIX_DEFAULT" && "${BINDIR:-$PREFIX_DEFAULT/bin}" == "$PREFIX_DEFAULT/bin" ]]; then
  if [[ ! -d "$BINDIR" ]]; then
    # If /usr/local/bin doesn't exist, creating it typically requires sudo. Treat as non-writable.
    can_write=false
  else
    can_write=true
    [[ -w "$BINDIR" ]] || can_write=false
  fi

  if [[ "$can_write" == "false" ]]; then
    PREFIX="$USER_PREFIX_DEFAULT"
    BINDIR="$PREFIX/bin"
    echo "Note: '$PREFIX_DEFAULT/bin' is not writable. Falling back to user install: $BINDIR"
  fi
fi

echo "Installing to: $BINDIR/$BIN_NAME"
mkdir -p "$BINDIR"

LINK="$BINDIR/$BIN_NAME"

# Use a symlink so upgrades are just re-running the script.
ln -sf "$TARGET" "$LINK"

# Help the user ensure they can invoke it.
if ! echo ":$PATH:" | grep -q ":$BINDIR:"; then
  cat <<EOF

Note: '$BINDIR' is not on your PATH.
Add this to your shell profile (e.g. ~/.zshrc):
  export PATH="$BINDIR:\$PATH"

Then open a new terminal.
EOF
fi

echo "Done. Try: svai --help"
