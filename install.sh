#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="$HOME/.skillhub/bin"
mkdir -p "$INSTALL_DIR"

echo "Publishing SkillHub binary to $INSTALL_DIR..."
dotnet publish "$SCRIPT_DIR/src/SkillSync.Daemon/SkillSync.Daemon.csproj" \
  -c Release \
  -o "$INSTALL_DIR" \
  -p:PublishSingleFile=false \
  --nologo

EXE_PATH="$INSTALL_DIR/SkillHub"
chmod +x "$EXE_PATH"

echo "Running 'SkillHub install'..."
"$EXE_PATH" install --bin-path "$EXE_PATH"
