#!/usr/bin/env sh
set -eu

if ! command -v lp >/dev/null 2>&1 || ! command -v lpstat >/dev/null 2>&1; then
  echo "CUPS não encontrado. Instale cups-client antes de continuar." >&2
  echo "Debian/Ubuntu: sudo apt install cups cups-client" >&2
  exit 1
fi

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)"
SOURCE="$ROOT/dist/linux-x64"
if [ ! -x "$SOURCE/softprint" ]; then
  echo "Pacote linux-x64 não encontrado em $SOURCE. Execute scripts/publish-all.ps1." >&2
  exit 1
fi

TARGET="$HOME/.local/lib/softprint"
UNIT="$HOME/.config/systemd/user"
mkdir -p "$TARGET" "$UNIT"
cp -R "$SOURCE/." "$TARGET/"
chmod +x "$TARGET/softprint"
cp "$ROOT/packaging/linux/softprint.service" "$UNIT/softprint.service"
systemctl --user daemon-reload
systemctl --user enable --now softprint.service
echo "SoftPrint instalado."
echo "Painel: http://127.0.0.1:5178/dashboard"
echo "Diagnóstico: $TARGET/softprint --diagnose"
