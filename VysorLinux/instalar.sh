#!/usr/bin/env bash
#
# Cria o atalho do Vysor no menu de aplicativos.
#
# Depois de rodar isto UMA vez, o Vysor aparece no menu do sistema junto com
# os outros programas: é só clicar no ícone, como qualquer app. Nunca mais
# precisa abrir terminal.
#
#   bash instalar.sh
#
set -e

PASTA="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ATALHOS="$HOME/.local/share/applications"
ICONES="$HOME/.local/share/icons/hicolor/scalable/apps"

mkdir -p "$ATALHOS" "$ICONES"

# Ícone simples, desenhado aqui mesmo pra não depender de arquivo externo.
cat > "$ICONES/vysor.svg" <<'SVG'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <rect width="64" height="64" rx="14" fill="#5865F2"/>
  <path d="M36 12 L22 34 h9 l-4 18 14-22 h-9 z" fill="#FFFFFF"/>
</svg>
SVG

cat > "$ATALHOS/vysor.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Vysor
Comment=Assistir a tela dos amigos
Exec=python3 "$PASTA/run.py"
Path=$PASTA
Icon=vysor
Terminal=false
Categories=Network;AudioVideo;
StartupNotify=true
DESKTOP

chmod +x "$ATALHOS/vysor.desktop"

# Faz o sistema notar o atalho novo na hora, sem precisar reiniciar a sessão.
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$ATALHOS" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f -t "$HOME/.local/share/icons/hicolor" >/dev/null 2>&1 || true
fi

echo
echo "Pronto! O Vysor já aparece no menu de aplicativos."
echo "Procure por \"Vysor\" no menu do sistema e clique nele."
echo
echo "Se não aparecer na hora, saia da sessão e entre de novo."
