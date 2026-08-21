# Vysor para Linux

Esta é a versão do Vysor pra quem usa Linux. Ela entra nas **mesmas salas** que
o pessoal do Windows: mesmo código de sala, mesma imagem, mesmo som. Não
importa quem criou a sala nem quem está em qual sistema.

Funciona no **Wayland** (GNOME, KDE, Sway…) e no **X11**.

---

## 1. Instalar as dependências

Copie e cole a linha da sua distribuição no terminal.

### Ubuntu / Debian / Linux Mint / Pop!_OS

```bash
sudo apt update
sudo apt install python3-gi python3-gi-cairo gir1.2-gtk-4.0 \
  gir1.2-gstreamer-1.0 gir1.2-gst-plugins-base-1.0 \
  gstreamer1.0-plugins-base gstreamer1.0-plugins-good \
  gstreamer1.0-plugins-bad gstreamer1.0-plugins-ugly \
  gstreamer1.0-libav gstreamer1.0-pipewire \
  xdg-desktop-portal pulseaudio-utils
```

E mais **um** destes, conforme o seu ambiente de trabalho:

```bash
sudo apt install xdg-desktop-portal-gnome   # GNOME
sudo apt install xdg-desktop-portal-kde     # KDE Plasma
sudo apt install xdg-desktop-portal-wlr     # Sway, Hyprland e afins
```

### Fedora

```bash
sudo dnf install python3-gobject gtk4 \
  gstreamer1-plugins-base gstreamer1-plugins-good \
  gstreamer1-plugins-bad-free gstreamer1-libav \
  gstreamer1-plugin-pipewire xdg-desktop-portal pulseaudio-utils
```

O Fedora não distribui o `gstreamer1-plugins-ugly` (que tem o codificador de
vídeo) nos repositórios oficiais. Ative o RPM Fusion e instale:

```bash
sudo dnf install \
  https://mirrors.rpmfusion.org/free/fedora/rpmfusion-free-release-$(rpm -E %fedora).noarch.rpm
sudo dnf install gstreamer1-plugins-ugly
```

### Arch / Manjaro / EndeavourOS

```bash
sudo pacman -S python-gobject gtk4 \
  gst-plugins-base gst-plugins-good gst-plugins-bad gst-plugins-ugly \
  gst-libav gst-plugin-pipewire xdg-desktop-portal libpulse
```

Mais o portal do seu ambiente: `xdg-desktop-portal-gnome`,
`xdg-desktop-portal-kde` ou `xdg-desktop-portal-wlr`.

---

## 2. Abrir o Vysor

Dentro da pasta `VysorLinux`:

```bash
python3 run.py
```

É só isso. **Não precisa instalar nada com `pip`** — o app usa apenas o que já
vem com o Python e as bibliotecas do sistema instaladas acima.

Se estiver faltando alguma peça, o app não abre em silêncio: ele diz
exatamente o que falta e imprime a linha de instalação pronta pra você copiar.

---

## 3. Usar

1. Escreva seu **nome** (obrigatório — é como seus amigos te reconhecem).
2. **Criar Sala** gera um código; ou cole o código que te passaram e clique em
   **Entrar com Código**.
3. Na sala, clique no **▶** ao lado de alguém pra assistir. O ▶ só acende
   quando a pessoa está transmitindo de verdade.
4. **🖥️ TRANSMITIR** compartilha a sua tela.
   - No Wayland, o sistema abre a janelinha de permissão perguntando o que
     você quer compartilhar. É o mesmo mecanismo do Discord e do OBS.
   - A escolha fica guardada, então nas próximas vezes costuma ser mais
     rápido.
   - Enquanto transmite, aparece uma prévia da sua própria tela — é assim que
     você confere que está no ar.
5. Cada telinha tem controle de **volume** e **mudo** individual.

---

## 4. Sobre o som

O Vysor transmite **o som que está saindo do seu computador** (o jogo, o
vídeo, a música), não o microfone.

Dois pontos que valem saber:

- **Ao compartilhar apenas uma janela, o som não vai junto.** O Linux só sabe
  entregar o som do computador inteiro; mandar isso quando você escolheu "só
  esta janela" vazaria tudo que você não pediu (outras abas, notificações,
  outra conversa). O cliente Windows se comporta do mesmo jeito. **Para
  transmitir com som, escolha a tela inteira.**
- Se aparecer "Transmitindo sem áudio", falta o utilitário que identifica a
  saída de som padrão: instale `pulseaudio-utils` (Debian/Ubuntu/Fedora) ou
  `libpulse` (Arch).

---

## 5. Se algo não funcionar

**"Não encontrei o portal de compartilhamento do seu desktop"**
Falta o pacote `xdg-desktop-portal-*` do seu ambiente (veja o passo 1). Depois
de instalar, saia da sessão e entre de novo.

**"O seletor de tela do sistema não respondeu"**
O serviço de portal travou. Reinicie-o com:
```bash
systemctl --user restart xdg-desktop-portal
```

**"A captura começou mas nenhuma imagem está chegando"**
Pare a transmissão e comece de novo, escolhendo outra tela ou janela. Costuma
acontecer quando o monitor escolhido foi desconectado ou a janela foi fechada.

**A janela abre, mas ninguém aparece na sala**
Confirme o código da sala (ele diferencia nada além de letras e números — o
app já converte pra maiúsculas sozinho). No canto superior direito da sala,
qualquer problema de conexão aparece escrito.

**Quero apontar pra outro servidor**
```bash
VYSOR_SERVER="https://SEU-SERVIDOR/roomhub" python3 run.py
```

**Ver os detalhes de um erro**
O app escreve os problemas no terminal, com a marca `[vysor]`. Se algo estranho
acontecer, rode pelo terminal e copie o que aparecer lá.

---

## 6. Como está organizado (para curiosos)

| Arquivo | O que faz |
|---|---|
| `run.py` | ponto de entrada |
| `vysor/app.py` | a interface (GTK4) e a lógica da sala |
| `vysor/media.py` | captura, codificação e reprodução (GStreamer) |
| `vysor/portal.py` | a permissão de captura de tela no Wayland (D-Bus) |
| `vysor/signalr.py` | a conversa com o servidor (WebSocket, só biblioteca padrão) |
| `vysor/protocol.py` | o formato exato dos dados trocados com o cliente Windows |

O vídeo trafega como H.264 e o áudio como G.711 μ-law a 48 kHz — exatamente o
mesmo formato do cliente Windows, que é o que permite os dois conversarem.
