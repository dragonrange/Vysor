"""
Interface do Vysor para Linux (GTK4).

Espelha o cliente Windows: tela inicial com nome + criar/entrar em sala, e a
tela da sala com as transmissões à esquerda e a lista de participantes à
direita.

Regra de thread que atravessa o arquivo inteiro: a conexão de rede roda em
threads separadas e o GStreamer tem as threads dele. NADA disso pode tocar na
tela diretamente — tudo passa por GLib.idle_add, que devolve a execução pra
thread da interface. Ignorar isso trava ou quebra a janela de formas difíceis
de diagnosticar.
"""

import os
import sys
import threading
import traceback

import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Gdk", "4.0")
from gi.repository import Gtk, Gdk, GLib, GObject, Gio   # noqa: E402

from . import media, portal, peer                           # noqa: E402
from .protocol import (TAG_H264, TAG_JPEG, tag_frame, split_frame,   # noqa: E402
                       is_keyframe)
from .signalr import SignalRClient                          # noqa: E402

DEFAULT_SERVER = os.environ.get(
    "VYSOR_SERVER",
    "https://vysorserver-cjxi.onrender.com/roomhub")

# Tamanho em que cada transmissão é decodificada pra caber na telinha. Manter
# a largura múltipla de 4 evita problemas de alinhamento de linha ao desenhar.
TILE_W, TILE_H = 640, 360

# Identificador da telinha de prévia (a sua própria transmissão). Começa com
# "__" pra nunca colidir com um identificador de verdade, que é hexadecimal.
SELF_TILE = "__eu__"

# Onde guardamos a permissão de captura que o portal do desktop devolve, pra
# não precisar escolher a tela toda vez que abrir o app.
CONFIG_DIR = os.path.join(
    os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config"), "vysor")
RESTORE_FILE = os.path.join(CONFIG_DIR, "captura.token")

# O texto tem acento, então nasce como texto e vira bytes no fim (um literal
# de bytes em Python só aceita ASCII).
CSS = """
/* A COR DO TEXTO É DEFINIDA AQUI EM CIMA, DE PROPÓSITO.
   Sem esta regra, cada elemento herdava a cor do tema do sistema — e num
   Linux com tema claro isso vira texto escuro sobre o nosso fundo escuro:
   o app inteiro fica ilegível, sem nada de errado aparecendo. Foi assim que
   os nomes dos participantes sumiram no primeiro teste. */
window, label, button, entry { color: #ffffff; }

window, .root { background-color: #121214; }

.title    { font-size: 22px; font-weight: bold; }
.subtle   { color: #a1a1aa; font-size: 12px; }
.hint     { color: #8a8a93; font-size: 11px; }
.notice   { color: #faa61a; font-size: 12px; }
.code     { color: #5865f2; font-weight: bold; font-size: 18px; }

entry {
  background: #27272a; border: 1px solid #3f3f46; border-radius: 8px;
  padding: 10px 12px; caret-color: #ffffff; font-size: 14px;
}
entry:focus { border-color: #5865f2; }

button {
  background-image: none; border: none; border-radius: 8px;
  padding: 10px 14px; font-size: 14px;
}
button:hover  { opacity: 0.88; }
button:disabled { opacity: 0.40; }

.primary { background-color: #5865f2; font-weight: bold; }
.success { background-color: #23a55a; font-weight: bold; }
.danger  { background-color: #f23f42; font-weight: bold; }
.neutral { background-color: #2d2d36; }

.panel { background-color: #18181b; border-radius: 12px; }
.tile  { background-color: #0d0d10; border-radius: 12px; }

.tilebar {
  background-color: rgba(0,0,0,0.65); border-radius: 6px;
  padding: 3px 10px; font-weight: bold;
}

/* Sem a cor explícita, o nome do participante ficava escuro sobre escuro. */
.participant {
  background-color: #2d2d36; color: #ffffff;
  border-radius: 8px; padding: 8px 10px;
}
.participant:hover { background-color: #35353f; }

.iconbtn { padding: 4px 8px; min-width: 0; min-height: 0; font-size: 13px; }

scale { min-height: 20px; }
scale trough    { background-color: #3f3f46; border-radius: 3px; min-height: 6px; }
scale highlight { background-color: #5865f2; border-radius: 3px; min-height: 6px; }
scale slider {
  background-color: #ffffff; border-radius: 50%;
  min-width: 14px; min-height: 14px; margin: -5px;
}
""".encode("utf-8")


def _parse_candidate(text):
    """"192.168.1.42:51820" -> ("192.168.1.42", 51820), ou None se malformado."""
    if not text or ":" not in text:
        return None
    host, _, port_text = text.rpartition(":")
    try:
        port = int(port_text)
    except ValueError:
        return None
    if not host or port <= 0 or port > 65535:
        return None
    return (host, port)


def _log(what, exc=None):
    """
    Erro que não deve derrubar o app, mas TAMBÉM não deve sumir. Engolir
    exceção em silêncio é o que transforma um bug de 5 minutos numa tarde
    perdida: aqui pelo menos sobra um rastro no terminal.
    """
    print(f"[vysor] {what}", file=sys.stderr)
    if exc is not None:
        traceback.print_exception(type(exc), exc, exc.__traceback__)


class Tile(GObject.Object):
    """Uma telinha: a transmissão de uma pessoa (ou a sua própria prévia)."""

    def __init__(self, user_id, name, window, local=False):
        super().__init__()
        self.user_id = user_id
        self.name = name
        self.window = window
        self.local = local
        self.disposed = False

        self.decoder = None      # criado quando chegar o primeiro quadro H.264
        self.player = None       # áudio dessa pessoa
        self.texture = None
        self._got_keyframe = False

        self.picture = Gtk.Picture(content_fit=Gtk.ContentFit.CONTAIN)
        self.picture.set_size_request(320, 180)

        self.placeholder = Gtk.Label(label="Aguardando o primeiro quadro…")
        self.placeholder.add_css_class("subtle")

        self.stack = Gtk.Stack()
        self.stack.add_named(self.placeholder, "wait")
        self.stack.add_named(self.picture, "video")
        self.stack.set_visible_child_name("wait")
        self.stack.set_vexpand(True)

        header = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6)
        label = Gtk.Label(label=(f"{name} (você)" if local else name))
        label.add_css_class("tilebar")
        header.append(label)
        header.append(Gtk.Box(hexpand=True))

        if not local:
            close = Gtk.Button(label="✕")
            close.add_css_class("neutral")
            close.add_css_class("iconbtn")
            close.set_valign(Gtk.Align.CENTER)
            close.connect("clicked", lambda *_: window.stop_watching(user_id))
            header.append(close)

        self.widget = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        self.widget.add_css_class("tile")
        self.widget.set_margin_start(4); self.widget.set_margin_end(4)
        self.widget.set_margin_top(4); self.widget.set_margin_bottom(4)
        self.widget.append(header)
        self.widget.append(self.stack)

        if local:
            # A prévia da própria transmissão não tem áudio (você não se escuta).
            self.volume = None
            self.mute = None
        else:
            # Volume + mudo, como no cliente Windows (0 a 150%).
            self.volume = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 0, 150, 1)
            self.volume.set_value(100)
            self.volume.set_digits(0)
            self.volume.set_size_request(140, -1)
            self.volume.set_draw_value(False)
            self.volume.connect("value-changed", lambda *_: self._apply_audio())

            self.mute = Gtk.ToggleButton(label="Som")
            self.mute.add_css_class("neutral")
            self.mute.add_css_class("iconbtn")
            self.mute.set_valign(Gtk.Align.CENTER)
            self.mute.connect("toggled", self._on_mute)

            controls = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=10)
            controls.set_margin_start(10); controls.set_margin_end(10)
            controls.set_margin_bottom(8)
            controls.append(self.volume)
            controls.append(Gtk.Box(hexpand=True))
            controls.append(self.mute)
            self.widget.append(controls)

    # --- vídeo ---

    def feed_h264(self, au: bytes):
        """Só pode ser chamado da thread da interface (ver dispose_all)."""
        if self.disposed:
            return

        # Começar a assistir no meio de uma transmissão é o caso NORMAL: você
        # clica no ▶ quando quiser. Só que um quadro comum sozinho não
        # significa nada — ele descreve as DIFERENÇAS em relação ao anterior,
        # que você não tem. Jogar isso no decodificador produz aquele
        # borrão verde/cinza cheio de rastros até o próximo quadro-chave.
        # Então esperamos, em silêncio, pelo primeiro quadro-chave (vem no
        # máximo 1 segundo depois) e só aí começamos a desenhar.
        if not self._got_keyframe:
            if not is_keyframe(au):
                return
            self._got_keyframe = True

        if self.decoder is None:
            self.decoder = media.VideoDecoder(self._on_decoded, TILE_W, TILE_H)
            if not self.decoder.start():
                self.decoder = None
                _log(f"não consegui iniciar o decodificador de vídeo de {self.name}")
                return
        self.decoder.feed(au)

    def feed_jpeg(self, data: bytes):
        if self.disposed:
            return
        try:
            self._show(Gdk.Texture.new_from_bytes(GLib.Bytes.new(data)))
        except Exception as e:
            _log("quadro JPEG inválido", e)

    def _on_decoded(self, rgb: bytes, w: int, h: int, stride: int):
        # Chega pela thread da interface (o VideoDecoder já usa idle_add).
        if self.disposed:
            return False
        try:
            texture = Gdk.MemoryTexture.new(
                w, h, Gdk.MemoryFormat.R8G8B8, GLib.Bytes.new(rgb), stride)
            self._show(texture)
        except Exception as e:
            _log("falha ao desenhar o quadro", e)
        return False   # idle_add: roda uma vez só

    def _show(self, texture):
        self.texture = texture
        self.picture.set_paintable(texture)
        if self.stack.get_visible_child_name() != "video":
            self.stack.set_visible_child_name("video")

    # --- áudio ---

    def feed_audio(self, mulaw: bytes):
        if self.disposed or self.local:
            return
        if self.player is None:
            self.player = media.AudioPlayer()
            if not self.player.start():
                self.player = None
                _log(f"não consegui abrir a saída de áudio de {self.name}")
                return
            self._apply_audio()
        self.player.feed(mulaw)

    def _on_mute(self, button):
        button.set_label("Mudo" if button.get_active() else "Som")
        self._apply_audio()

    def _apply_audio(self):
        if self.player and self.volume is not None:
            self.player.set_volume_percent(self.volume.get_value())
            self.player.set_muted(self.mute.get_active())

    def dispose_all(self):
        self.disposed = True
        if self.decoder:
            self.decoder.stop(); self.decoder = None
        if self.player:
            self.player.stop(); self.player = None


class VysorWindow(Gtk.ApplicationWindow):
    def __init__(self, app):
        super().__init__(application=app, title="Vysor")
        self.set_default_size(1040, 680)

        self.signalr = None
        self.my_id = None
        self.room_code = None
        self.display_name = ""
        self.connection_state = ""

        # Conexão DIRETA (P2P) com os amigos da sala — ver vysor/peer.py.
        # Vídeo e áudio SÓ vão por aqui: o servidor não tem mais nenhum
        # método capaz de repassar mídia.
        self.peer = None
        self._same_network_stuck = set()   # user_ids com indício de mesma rede presos

        # O que fazer assim que a conexão ficar de pé. Sem isso o clique em
        # "Criar Sala" era mandado às cegas alguns milissegundos depois e, se
        # a conexão demorasse um pouco mais (o normal numa internet comum),
        # a mensagem sumia e o botão parecia simplesmente não funcionar.
        self._pending_action = None

        self.participants = {}   # user_id -> dict(name, sharing, row, play, label)
        self.tiles = {}          # user_id -> Tile

        self.encoder = None
        self.audio_capture = None
        self.portal = None
        self.portal_fd = None
        self.sharing = False
        self.self_tile = None

        # "O usuário QUER transmitir" — diferente de self.sharing, que é "está
        # transmitindo agora". Entre clicar em TRANSMITIR e o seletor do
        # sistema responder podem passar minutos, e nesse meio-tempo a pessoa
        # pode ter saído da sala ou desistido. Sem esta marca, a resposta
        # atrasada do portal religava a captura sozinha: a pessoa achava que
        # tinha saído, mas o aviso de "sua tela está sendo compartilhada"
        # continuava aceso e os quadros continuavam saindo.
        self._share_requested = False
        self._frames_sent = 0
        self._share_watchdog = None

        # Vira False quando a janela é fechada. Threads de rede podem demorar
        # um instante a mais pra morrer, e sem esta marca elas tentariam mexer
        # em widgets que já não existem.
        self._alive = True

        self._build()

    # ---------------- construção da tela ----------------

    def _build(self):
        self.stack = Gtk.Stack()
        self.set_child(self.stack)
        self.stack.add_named(self._build_lobby(), "lobby")
        self.stack.add_named(self._build_room(), "room")
        self.stack.set_visible_child_name("lobby")

    def _build_lobby(self):
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10,
                      halign=Gtk.Align.CENTER, valign=Gtk.Align.CENTER)
        box.set_size_request(380, -1)

        title = Gtk.Label(label="Vysor", xalign=0)
        title.add_css_class("title")
        box.append(title)

        lbl = Gtk.Label(label="Nome", xalign=0)
        lbl.add_css_class("subtle")
        box.append(lbl)

        self.name_entry = Gtk.Entry(placeholder_text="Digite seu nome")
        self.name_entry.connect("changed", self._update_lobby_buttons)
        box.append(self.name_entry)

        self.name_hint = Gtk.Label(
            label="Escolha um nome para seus amigos te reconhecerem na sala.", xalign=0)
        self.name_hint.add_css_class("hint")
        box.append(self.name_hint)

        self.btn_create = Gtk.Button(label="Criar Sala")
        self.btn_create.add_css_class("primary")
        self.btn_create.set_sensitive(False)
        self.btn_create.connect("clicked", self._on_create)
        box.append(self.btn_create)

        lbl2 = Gtk.Label(label="Código da Sala", xalign=0)
        lbl2.add_css_class("subtle")
        box.append(lbl2)

        row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        self.code_entry = Gtk.Entry(placeholder_text="Ex: XJ4K9P", hexpand=True)
        self.code_entry.connect("changed", self._update_lobby_buttons)
        row.append(self.code_entry)

        self.btn_join = Gtk.Button(label="Entrar com Código")
        self.btn_join.add_css_class("success")
        self.btn_join.set_sensitive(False)
        self.btn_join.connect("clicked", self._on_join)
        row.append(self.btn_join)
        box.append(row)

        self.lobby_status = Gtk.Label(label="", xalign=0, wrap=True)
        self.lobby_status.add_css_class("notice")
        box.append(self.lobby_status)
        return box

    def _build_room(self):
        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        outer.set_margin_start(14); outer.set_margin_end(14)
        outer.set_margin_top(14); outer.set_margin_bottom(14)

        top = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        top.append(Gtk.Label(label="CÓDIGO DA SALA:"))
        self.code_label = Gtk.Label(label="------")
        self.code_label.add_css_class("code")
        top.append(self.code_label)

        copy = Gtk.Button(label="Copiar convite")
        copy.add_css_class("neutral")
        copy.connect("clicked", self._on_copy)
        top.append(copy)
        top.append(Gtk.Box(hexpand=True))

        self.conn_label = Gtk.Label(label="")
        self.conn_label.add_css_class("notice")
        top.append(self.conn_label)

        leave = Gtk.Button(label="Sair da Sala")
        leave.add_css_class("danger")
        leave.connect("clicked", self._on_leave)
        top.append(leave)
        outer.append(top)

        body = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=10, vexpand=True)

        left = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8, hexpand=True)
        left.add_css_class("panel")

        self.tiles_grid = Gtk.FlowBox(
            selection_mode=Gtk.SelectionMode.NONE, homogeneous=True,
            min_children_per_line=1, max_children_per_line=3,
            row_spacing=6, column_spacing=6, vexpand=True)
        scroller = Gtk.ScrolledWindow(vexpand=True)
        scroller.set_child(self.tiles_grid)
        left.append(scroller)

        self.empty_label = Gtk.Label(
            label="Aguardando transmissão…\n"
                  "Clique no ▶ ao lado de um participante para assistir.")
        self.empty_label.add_css_class("subtle")
        left.append(self.empty_label)

        self.room_notice = Gtk.Label(label="", visible=False, wrap=True)
        self.room_notice.add_css_class("notice")
        left.append(self.room_notice)

        self.btn_share = Gtk.Button(label="TRANSMITIR")
        self.btn_share.add_css_class("primary")
        self.btn_share.set_margin_bottom(10)
        self.btn_share.connect("clicked", self._on_share_clicked)
        left.append(self.btn_share)
        body.append(left)

        right = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        right.add_css_class("panel")
        right.set_size_request(210, -1)
        lbl = Gtk.Label(label="PARTICIPANTES", xalign=0)
        lbl.add_css_class("subtle")
        lbl.set_margin_start(10); lbl.set_margin_top(10)
        right.append(lbl)

        self.participants_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        self.participants_box.set_margin_start(8); self.participants_box.set_margin_end(8)
        psc = Gtk.ScrolledWindow(vexpand=True)
        psc.set_child(self.participants_box)
        right.append(psc)
        body.append(right)

        outer.append(body)
        return outer

    def _update_lobby_buttons(self, *_):
        waiting = self._pending_action is not None
        has_name = bool(self.name_entry.get_text().strip())
        has_code = bool(self.code_entry.get_text().strip())
        self.btn_create.set_sensitive(has_name and not waiting)
        self.btn_join.set_sensitive(has_name and has_code and not waiting)
        self.name_hint.set_visible(not has_name)

    # ---------------- conexão ----------------

    def _ensure_connection(self):
        if self.signalr:
            return
        self.signalr = SignalRClient(DEFAULT_SERVER, self._on_event, self._on_state)
        self.signalr.start()

    def _on_state(self, state):
        """Vem da thread de rede."""
        GLib.idle_add(self._apply_state, state)

    def _apply_state(self, state):
        if not self._alive:
            return False
        self.connection_state = state
        in_room = self.stack.get_visible_child_name() == "room"

        if state == "conectado" and self.signalr:
            if self.room_code:
                # Voltou depois de uma queda: reentra na sala sozinho, sem o
                # amigo precisar fazer nada. O servidor recria a sala se
                # precisar, então ninguém fica preso no "Sala não encontrada".
                self.signalr.invoke("RejoinRoom", self.room_code,
                                    self._user_id(), self.display_name)
            elif self._pending_action:
                method, args = self._pending_action
                self._pending_action = None
                self.signalr.invoke(method, *args)
                self._update_lobby_buttons()

        if in_room:
            self.conn_label.set_text("" if state == "conectado" else f"⚠ {state}")
        elif state == "conectado":
            self.lobby_status.set_text("")
        elif state == "conectando":
            self.lobby_status.set_text("Conectando ao servidor…")
        else:
            self.lobby_status.set_text(f"Conexão: {state}")
        return False

    def _on_event(self, target, args):
        """Chamado da thread da REDE — nada de tocar na tela aqui."""
        GLib.idle_add(self._handle_event, target, args)

    def _handle_event(self, target, args):
        if not self._alive:
            return False
        try:
            if target == "RoomCreated":
                self._entered(args[0], args[1], [args[1]], [self.display_name])
            elif target == "RoomJoined":
                self._entered(args[0], args[1], args[2], args[3])
            elif target == "UserJoined":
                self._add_participant(args[0], args[1])
            elif target == "UserLeft":
                self._remove_participant(args[0])
            elif target == "UserStoppedSharing":
                self._set_sharing(args[0], False)
                self.stop_watching(args[0])
            elif target == "PeerCandidates":
                self._on_peer_candidates(args[0], args[1] or [])
            elif target == "Error":
                self._show_error(args[0])
        except Exception as e:
            _log(f"falha ao tratar o aviso '{target}'", e)
        return False

    def _show_error(self, message):
        self._pending_action = None
        if self.stack.get_visible_child_name() == "lobby":
            self.lobby_status.set_text(message)
            self._update_lobby_buttons()
        else:
            self._notice(message)

    def _notice(self, text):
        self.room_notice.set_text(text)
        self.room_notice.set_visible(bool(text))

    # ---------------- sala ----------------

    def _on_create(self, *_):
        self.display_name = self.name_entry.get_text().strip()
        self._request("CreateRoom", self._user_id(), self.display_name)

    def _on_join(self, *_):
        self.display_name = self.name_entry.get_text().strip()
        code = self.code_entry.get_text().strip().upper()
        self._request("JoinRoom", code, self._user_id(), self.display_name)

    def _request(self, method, *args):
        """Manda agora se já estiver conectado; senão, assim que conectar."""
        self._ensure_connection()
        if self.signalr.connected:
            self._pending_action = None
            self.signalr.invoke(method, *args)
        else:
            self.lobby_status.set_text("Conectando ao servidor…")
            self._pending_action = (method, args)
        self._update_lobby_buttons()

    def _user_id(self):
        # Identidade estável enquanto o app estiver aberto — mesma ideia do
        # cliente Windows: sobreviver a uma reconexão sem virar "outra pessoa".
        if not getattr(self, "_uid", None):
            import uuid
            self._uid = uuid.uuid4().hex
        return self._uid

    def _entered(self, code, my_id, ids, names):
        self._pending_action = None
        self.room_code = code
        self.my_id = my_id
        self.code_label.set_text(code)
        self.conn_label.set_text("")
        self._notice("")
        self.stack.set_visible_child_name("room")

        # Reentrada depois de uma queda: quem não está mais na sala perde a
        # telinha; quem continua mantém a dele viva, sem piscar.
        present = set(ids)
        for uid in list(self.tiles):
            if uid != SELF_TILE and uid not in present:
                self.stop_watching(uid)

        child = self.participants_box.get_first_child()
        while child is not None:
            nxt = child.get_next_sibling()
            self.participants_box.remove(child)
            child = nxt
        self.participants.clear()

        for uid, name in zip(ids, names):
            self._add_participant(uid, name)

        # Quem eu já estava assistindo continua com o ▶ marcado.
        for uid in self.tiles:
            info = self.participants.get(uid)
            if info:
                info["sharing"] = True
                info["play"].set_sensitive(True)
                info["play"].set_label("■")

        self._start_peer_transport()

    # ---------------- conexão direta (P2P) ----------------
    #
    # Sem isto, este cliente Linux só sabia falar com o servidor — e o
    # servidor não tem mais nenhum jeito de repassar vídeo/áudio. Espelha
    # PeerMedia.Start (Windows) e MainActivity.startPeers (Android): abre o
    # socket UDP, manda o endereço de casa NA HORA (conecta instantâneo com
    # quem estiver no mesmo Wi-Fi) e, em paralelo, descobre o endereço
    # externo via STUN pra fechar com quem estiver longe.
    def _start_peer_transport(self):
        if self.peer is not None:
            return
        if not peer.CRYPTO_AVAILABLE:
            self._notice("Sem o pacote python3-cryptography, vídeo/áudio não "
                         "conseguem ir direto entre os aparelhos (veja o LEIAME).")
            return

        room_key = peer.derive_key(self.room_code)
        transport = peer.PeerTransport(
            self.my_id, room_key,
            on_frame=self._on_peer_frame_threadsafe,
            on_state=self._on_peer_state_threadsafe,
            on_same_network_stuck=self._on_same_network_stuck_threadsafe)

        if not transport.start():
            _log("não consegui abrir o socket UDP do caminho direto")
            return
        self.peer = transport

        for uid in self.participants:
            if uid != self.my_id:
                self.peer.add_peer(uid, [])
                self._update_link_status(uid)

        threading.Thread(target=self._announce_myself, daemon=True,
                         name="VysorPeerAnnounce").start()

    def _announce_myself(self):
        """Roda fora da thread da interface: descobrir o endereço externo
        fala com servidores na internet e pode levar segundos."""
        transport = self.peer
        if transport is None:
            return
        local_ip = peer.local_ip_guess()
        local_candidates = []
        if local_ip:
            local_candidates.append(f"{local_ip}:{transport.local_port()}")
            prefix = peer.subnet_prefix(local_ip)
            if prefix:
                transport.set_local_subnet_prefixes({prefix})

        # Manda o endereço de casa JÁ: quem está no mesmo Wi-Fi conecta na
        # hora, sem esperar o STUN (que pode levar segundos, ou nunca
        # responder numa rede que bloqueia esse tráfego).
        if local_candidates and self.signalr:
            try:
                self.signalr.invoke("AnnounceCandidates", local_candidates)
            except Exception:
                pass

        external = None
        for host, port in peer.PUBLIC_STUN_SERVERS:
            server = peer.resolve_stun_server(host, port)
            if server is None:
                continue
            mapped = transport.query_stun(server, timeout=3.0)
            if mapped:
                external = f"{mapped[0]}:{mapped[1]}"
                break

        if external and self.signalr:
            all_candidates = [external] + local_candidates
            try:
                self.signalr.invoke("AnnounceCandidates", all_candidates)
            except Exception:
                pass

    def _stop_peer_transport(self):
        if self.peer is not None:
            self.peer.stop()
            self.peer = None
        self._same_network_stuck.clear()

    def _on_peer_candidates(self, uid, raw_candidates):
        if self.peer is None or uid == self.my_id:
            return
        parsed = []
        for text in raw_candidates:
            addr = _parse_candidate(text)
            if addr:
                parsed.append(addr)
        if parsed:
            self.peer.add_peer(uid, parsed)

    # Os três callbacks abaixo vêm de threads de rede do peer.PeerTransport —
    # nunca tocam a tela diretamente, só agendam via GLib.idle_add (mesma
    # regra do SignalRClient).
    def _on_peer_frame_threadsafe(self, sender, kind, data):
        GLib.idle_add(self._handle_peer_frame, sender, kind, data)

    def _handle_peer_frame(self, sender, kind, data):
        if not self._alive:
            return False
        if kind == peer.KIND_VIDEO:
            self._on_frame(sender, data)
        else:
            self._on_audio(sender, data)
        return False

    def _on_peer_state_threadsafe(self, uid, connected):
        GLib.idle_add(self._handle_peer_state, uid, connected)

    def _handle_peer_state(self, uid, connected):
        if not self._alive:
            return False
        if connected:
            self._same_network_stuck.discard(uid)
        self._update_link_status(uid)
        return False

    def _on_same_network_stuck_threadsafe(self, uid):
        GLib.idle_add(self._handle_same_network_stuck, uid)

    def _handle_same_network_stuck(self, uid):
        if not self._alive:
            return False
        self._same_network_stuck.add(uid)
        self._update_link_status(uid)
        self._notice("Mesma rede, mas sem conexão direta — veja o isolamento "
                     "de cliente/AP no roteador.")
        return False

    def _update_link_status(self, uid):
        """Sem servidor de repasse, se o caminho direto não fechar essa
        pessoa nunca chega — este texto embaixo do nome existe pra explicar
        por quê, em vez de deixar a telinha preta sem nenhuma pista."""
        info = self.participants.get(uid)
        if not info or not info.get("link_label"):
            return
        label = info["link_label"]
        if self.peer is not None and self.peer.is_connected(uid):
            label.set_visible(False)
            return
        if uid in self._same_network_stuck:
            label.set_text("Mesma rede, sem conexão direta")
            label.add_css_class("notice")
        else:
            label.set_text("Conectando direto…")
            label.remove_css_class("notice")
        label.set_visible(True)

    def _add_participant(self, uid, name):
        if uid in self.participants:
            return

        row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6)
        row.add_css_class("participant")

        name_col = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=0)
        name_col.set_hexpand(True)
        label = Gtk.Label(label=name + (" (você)" if uid == self.my_id else ""),
                          xalign=0, hexpand=True)
        label.set_ellipsize(3)   # Pango.EllipsizeMode.END
        name_col.append(label)

        # Sem servidor de repasse, se o caminho direto não fechar essa pessoa
        # nunca chega — este texto existe pra explicar por quê. Some sozinho
        # assim que o furo de NAT confirma (ver _update_link_status).
        link_label = Gtk.Label(xalign=0)
        link_label.add_css_class("hint")
        link_label.set_visible(False)
        name_col.append(link_label)

        row.append(name_col)

        play = Gtk.Button(label="▶")
        play.add_css_class("success")
        play.add_css_class("iconbtn")
        play.set_valign(Gtk.Align.CENTER)
        play.set_sensitive(False)
        if uid != self.my_id:
            play.connect("clicked", lambda *_, u=uid: self.toggle_watch(u))
        else:
            play.set_visible(False)
        row.append(play)

        self.participants_box.append(row)
        self.participants[uid] = {"name": name, "row": row, "play": play,
                                  "sharing": False, "label": label,
                                  "link_label": link_label}

        if uid != self.my_id and self.peer is not None:
            self.peer.add_peer(uid, [])
            self._update_link_status(uid)

    def _remove_participant(self, uid):
        self.stop_watching(uid)
        if self.peer is not None and uid != self.my_id:
            self.peer.remove_peer(uid)
        self._same_network_stuck.discard(uid)
        info = self.participants.pop(uid, None)
        if info:
            self.participants_box.remove(info["row"])

    def _set_sharing(self, uid, sharing):
        info = self.participants.get(uid)
        if not info or info["sharing"] == sharing:
            return
        info["sharing"] = sharing
        info["play"].set_sensitive(sharing)
        if not sharing:
            info["play"].set_label("▶")

    # ---------------- assistir ----------------

    def toggle_watch(self, uid):
        if uid in self.tiles:
            self.stop_watching(uid)
        else:
            self.start_watching(uid)

    def start_watching(self, uid):
        if uid in self.tiles or uid == self.my_id:
            return
        info = self.participants.get(uid)
        tile = Tile(uid, info["name"] if info else uid, self)
        self.tiles[uid] = tile
        self.tiles_grid.append(tile.widget)
        self.empty_label.set_visible(False)
        if info:
            info["play"].set_label("■")

    def stop_watching(self, uid):
        tile = self.tiles.pop(uid, None)
        if tile:
            self._detach_tile(tile)
        info = self.participants.get(uid)
        if info:
            info["play"].set_label("▶")
        self.empty_label.set_visible(not self.tiles)

    def _detach_tile(self, tile):
        parent = tile.widget.get_parent()
        if parent is not None:
            self.tiles_grid.remove(parent)
        tile.dispose_all()

    def _on_frame(self, sender, data):
        self._set_sharing(sender, True)
        tile = self.tiles.get(sender)
        if not tile or not data:
            return
        tag, payload = split_frame(data)
        if not payload:
            return
        if tag == TAG_H264:
            tile.feed_h264(payload)
        else:
            tile.feed_jpeg(payload)

    def _on_audio(self, sender, data):
        tile = self.tiles.get(sender)
        if tile and data:
            tile.feed_audio(data)

    # ---------------- transmitir ----------------

    def _on_share_clicked(self, *_):
        if self.sharing:
            self._stop_sharing()
        else:
            self._start_sharing()

    def _start_sharing(self):
        self._notice("")
        if not self.signalr or not self.signalr.connected:
            self._notice("Espere a conexão voltar antes de transmitir.")
            return

        self._share_requested = True
        kind = portal.session_type()

        if kind == "x11":
            if media.missing_elements(["ximagesrc"]):
                self._share_requested = False
                self._notice("Falta o pacote gstreamer1.0-plugins-good "
                             "(elemento ximagesrc) pra capturar a tela no X11.")
                return
            # No X11 capturamos a tela inteira, então o som do sistema pode ir.
            self._begin_encoder(x11=True, with_audio=True)
            return

        if media.missing_elements(["pipewiresrc"]):
            self._share_requested = False
            self._notice("Falta o pacote gstreamer1.0-pipewire (elemento "
                         "pipewiresrc) pra capturar a tela no Wayland.")
            return

        p = portal.ScreenCastPortal()
        if not p.is_available():
            self._share_requested = False
            self._notice("Não encontrei o portal de compartilhamento do seu desktop. "
                         "Instale o pacote xdg-desktop-portal correspondente ao seu "
                         "ambiente (GNOME, KDE, wlroots…) e tente de novo.")
            return

        self.portal = p
        self.btn_share.set_sensitive(False)
        self._notice("Escolha o que compartilhar na janela do sistema…")
        p.start(on_ready=lambda fd, node, kind_: GLib.idle_add(
                    self._portal_ready, fd, node, kind_),
                on_error=lambda msg: GLib.idle_add(self._portal_failed, msg),
                restore_token=self._load_restore_token())

    def _portal_failed(self, message):
        self._share_requested = False
        self.btn_share.set_sensitive(True)
        self._notice(message)
        self._close_portal()
        return False

    def _portal_ready(self, fd, node_id, source_type):
        self.btn_share.set_sensitive(True)

        # A resposta do portal pode chegar depois de a pessoa ter desistido ou
        # saído da sala. Nesse caso, fechamos tudo em silêncio: religar a
        # captura aqui deixaria a tela sendo compartilhada sem ela saber.
        if not self._share_requested or not self._alive:
            try:
                os.close(fd)
            except Exception:
                pass
            self._close_portal()
            return False

        self._notice("")
        self.portal_fd = fd
        self._save_restore_token(getattr(self.portal, "restore_token", None))

        # Compartilhando UMA JANELA: o som vai junto? Não. O Linux só sabe
        # entregar o som do computador INTEIRO — outras abas, notificações,
        # a conversa que estiver rolando. Quem escolhe uma janela específica
        # está dizendo "só isto"; mandar o áudio geral seria justamente o
        # contrário. O cliente Windows faz a mesma coisa nesse caso.
        is_window = source_type == portal.SOURCE_WINDOW
        self._begin_encoder(x11=False, fd=fd, node_id=node_id,
                            with_audio=not is_window)
        if is_window and self.sharing:
            self._notice("Compartilhando só esta janela, sem som: o Linux não "
                         "separa o áudio por janela, e mandar o som do "
                         "computador inteiro vazaria o resto. Pra transmitir "
                         "com som, escolha a tela inteira.")
        return False

    def _begin_encoder(self, x11: bool, fd=None, node_id=None, with_audio=True):
        # ATENÇÃO à ordem: a marca "estou transmitindo" tem que ser ligada
        # ANTES de o pipeline começar a rodar. O primeiro quadro que sai do
        # codificador é justamente o quadro-chave — o único que se explica
        # sozinho. Antes, ele era produzido enquanto ainda estávamos ligando o
        # áudio, caía no "if not self.sharing: return" e ia pro lixo; quem
        # assistia recebia quase um segundo de quadros indecifráveis (aquele
        # borrão) até o quadro-chave seguinte.
        self.sharing = True
        self._frames_sent = 0
        self.encoder = media.ScreenEncoder(self._on_encoded, width=1280, height=720,
                                           fps=30, bitrate_kbps=2500)
        ok = self.encoder.start_x11() if x11 else self.encoder.start_pipewire(fd, node_id)
        if not ok:
            self.sharing = False
            self._share_requested = False
            self.encoder = None
            # Sem isto o "cano" do portal e a sessão dele ficavam abertos pra
            # sempre a cada tentativa que falhasse.
            self._release_capture()
            self._notice("Não consegui iniciar a captura de tela.")
            return

        self.btn_share.set_label("PARAR TRANSMISSÃO")
        self._show_self_tile()

        # Se o pipeline subir mas a fonte nunca entregar imagem (monitor
        # desconectado, nó do PipeWire que morreu), sem isto o botão ficaria
        # dizendo "TRANSMITINDO" pra sempre com a prévia parada em "aguardando
        # o primeiro quadro" e nenhuma explicação.
        self._share_watchdog = GLib.timeout_add_seconds(8, self._check_frames_flowing)

        if with_audio:
            # Ligar o áudio mexe com dispositivos e pode demorar (o sistema de
            # som às vezes leva um tempo pra responder). Fora da thread da
            # interface, senão a janela congela bem na hora em que a pessoa
            # acabou de clicar em transmitir.
            threading.Thread(target=self._start_audio_capture, daemon=True,
                             name="VysorAudioStart").start()

    def _start_audio_capture(self):
        capture = media.AudioCapture(self._on_audio_chunk)
        ok = capture.start()
        GLib.idle_add(self._audio_capture_ready, capture, ok)

    def _audio_capture_ready(self, capture, ok):
        if not self.sharing or not self._alive:
            capture.stop()
            return False
        if ok:
            self.audio_capture = capture
        else:
            self._notice("Transmitindo sem áudio: não achei o \"monitor\" da sua "
                         "saída de som. Instale o pacote pulseaudio-utils (ou "
                         "pipewire-pulse) se quiser que seus amigos ouçam o "
                         "som do seu computador.")
        return False

    def _check_frames_flowing(self):
        self._share_watchdog = None
        if self.sharing and self._frames_sent == 0:
            self._notice("A captura começou mas nenhuma imagem está chegando. "
                         "Pare e tente de novo escolhendo outra tela ou janela.")
        return False

    def _show_self_tile(self):
        """Prévia da própria transmissão — é o que mostra que está no ar."""
        if self.self_tile:
            return
        self.self_tile = Tile(SELF_TILE, self.display_name or "Minha tela",
                              self, local=True)
        self.tiles[SELF_TILE] = self.self_tile
        self.tiles_grid.append(self.self_tile.widget)
        self.empty_label.set_visible(False)

    def _hide_self_tile(self):
        if not self.self_tile:
            return
        self.tiles.pop(SELF_TILE, None)
        self._detach_tile(self.self_tile)
        self.self_tile = None
        self.empty_label.set_visible(not self.tiles)

    def _stop_sharing(self):
        was_sharing = self.sharing
        self.sharing = False
        self._share_requested = False
        if self._share_watchdog is not None:
            try:
                GLib.source_remove(self._share_watchdog)
            except Exception:
                pass
            self._share_watchdog = None
        if self.encoder:
            self.encoder.stop(); self.encoder = None
        if self.audio_capture:
            self.audio_capture.stop(); self.audio_capture = None
        self._release_capture()
        self._hide_self_tile()
        if was_sharing and self.signalr:
            self.signalr.invoke("StopSharing")
        self.btn_share.set_label("TRANSMITIR")
        self.btn_share.set_sensitive(True)

    def _release_capture(self):
        """Fecha a sessão do portal e o descritor do PipeWire (nessa ordem)."""
        self._close_portal()
        if self.portal_fd is not None:
            try:
                os.close(self.portal_fd)
            except Exception:
                pass
            self.portal_fd = None

    def _close_portal(self):
        if self.portal:
            try:
                self.portal.close()
            except Exception as e:
                _log("falha ao fechar a sessão do portal", e)
            self.portal = None

    def _on_encoded(self, au: bytes):
        """Vem de uma thread do GStreamer — só manda pra rede, não toca na tela.

        Sem servidor de repasse: manda direto (UDP) pra cada amigo cujo furo
        de NAT já fechou. Quem ainda não conectou perde o quadro — não existe
        mais nenhum caminho de reserva pelo servidor (ver peer.py)."""
        if not self.sharing or not self.peer:
            return
        self._frames_sent += 1
        self.peer.broadcast(peer.KIND_VIDEO, tag_frame(TAG_H264, au))
        # A prévia é desenhada na thread da interface, nunca aqui.
        GLib.idle_add(self._feed_self_preview, au)

    def _feed_self_preview(self, au):
        if self.self_tile and self.sharing:
            self.self_tile.feed_h264(au)
        return False

    def _on_audio_chunk(self, chunk: bytes):
        if self.sharing and self.peer:
            self.peer.broadcast(peer.KIND_AUDIO, chunk)

    # ---------------- permissão de captura guardada ----------------

    def _load_restore_token(self):
        try:
            with open(RESTORE_FILE, "r", encoding="utf-8") as fh:
                return fh.read().strip() or None
        except Exception:
            return None

    def _save_restore_token(self, token):
        if not token:
            return
        try:
            os.makedirs(CONFIG_DIR, exist_ok=True)
            with open(RESTORE_FILE, "w", encoding="utf-8") as fh:
                fh.write(token)
            os.chmod(RESTORE_FILE, 0o600)
        except Exception as e:
            _log("não consegui guardar a permissão de captura", e)

    # ---------------- outros ----------------

    def _on_copy(self, *_):
        if self.room_code:
            self.get_clipboard().set(self.room_code)

    def _on_leave(self, *_):
        self._stop_sharing()
        self._stop_peer_transport()
        for uid in list(self.tiles):
            self.stop_watching(uid)
        if self.signalr and self.room_code:
            self.signalr.invoke("LeaveRoom", self.room_code)
        self.room_code = None
        self.my_id = None
        self._pending_action = None
        self.code_label.set_text("------")
        self.lobby_status.set_text("")
        self.stack.set_visible_child_name("lobby")
        self._update_lobby_buttons()

    def shutdown(self):
        self._alive = False
        self._stop_sharing()
        self._stop_peer_transport()
        for uid in list(self.tiles):
            self.stop_watching(uid)
        if self.signalr:
            if self.room_code:
                # Avisa que saiu, pra não virar fantasma na lista dos amigos
                # durante o prazo de tolerância de reconexão. O stop() espera
                # um instante curto pra essa mensagem sair de verdade.
                self.signalr.invoke("LeaveRoom", self.room_code)
            self.signalr.stop()
            self.signalr = None


class VysorApp(Gtk.Application):
    def __init__(self):
        super().__init__(application_id="dev.vysor.linux",
                         flags=Gio.ApplicationFlags.FLAGS_NONE)
        self.win = None

    def do_activate(self):
        provider = Gtk.CssProvider()
        try:
            provider.load_from_data(CSS)
        except TypeError:
            # Versões diferentes do GTK4 querem texto ou bytes — aceitamos as duas.
            provider.load_from_data(CSS.decode("utf-8"), -1)
        Gtk.StyleContext.add_provider_for_display(
            Gdk.Display.get_default(), provider,
            Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)

        if not self.win:
            self.win = VysorWindow(self)
            self.win.connect("close-request", self._on_close)
        self.win.present()

    def _on_close(self, *_):
        if self.win:
            self.win.shutdown()
        return False


# Qual peça do GStreamer vem em qual pacote, por família de distribuição.
# Serve pra transformar "falta o elemento x264enc" (que não diz nada pra quem
# não é programador) numa linha de comando pronta pra copiar e colar.
_PACKAGES = {
    "base": {
        "elements": ["appsrc", "appsink", "videoconvert", "videoscale",
                     "videorate", "audioconvert", "audioresample", "volume"],
        "apt": "gstreamer1.0-plugins-base",
        "dnf": "gstreamer1-plugins-base",
        "pacman": "gst-plugins-base",
    },
    "good": {
        "elements": ["pulsesrc", "autoaudiosink", "mulawenc", "mulawdec", "ximagesrc"],
        "apt": "gstreamer1.0-plugins-good",
        "dnf": "gstreamer1-plugins-good",
        "pacman": "gst-plugins-good",
    },
    "bad": {
        "elements": ["h264parse"],
        "apt": "gstreamer1.0-plugins-bad",
        "dnf": "gstreamer1-plugins-bad-free",
        "pacman": "gst-plugins-bad",
    },
    "ugly": {
        "elements": ["x264enc"],
        "apt": "gstreamer1.0-plugins-ugly",
        "dnf": "gstreamer1-plugins-ugly",
        "pacman": "gst-plugins-ugly",
    },
    "libav": {
        "elements": ["avdec_h264"],
        "apt": "gstreamer1.0-libav",
        "dnf": "gstreamer1-libav",
        "pacman": "gst-libav",
    },
    "pipewire": {
        "elements": ["pipewiresrc"],
        "apt": "gstreamer1.0-pipewire",
        "dnf": "gstreamer1-plugin-pipewire",
        "pacman": "gst-plugin-pipewire",
    },
}


def _install_hint(missing):
    """Monta a linha de instalação para a distribuição desta máquina."""
    import shutil

    if shutil.which("apt"):
        manager, key = "sudo apt install", "apt"
    elif shutil.which("dnf"):
        manager, key = "sudo dnf install", "dnf"
    elif shutil.which("pacman"):
        manager, key = "sudo pacman -S", "pacman"
    else:
        return None

    needed = []
    for group in _PACKAGES.values():
        if any(e in missing for e in group["elements"]) and group[key] not in needed:
            needed.append(group[key])
    return f"{manager} {' '.join(needed)}" if needed else None


REQUIRED_ELEMENTS = [
    "appsrc", "appsink", "x264enc", "avdec_h264", "h264parse",
    "mulawenc", "mulawdec", "pulsesrc", "videoconvert", "videoscale",
    "videorate", "audioconvert", "audioresample", "volume", "autoaudiosink",
]


def _crypto_install_hint():
    import shutil
    if shutil.which("apt"):
        return "sudo apt install python3-cryptography"
    if shutil.which("dnf"):
        return "sudo dnf install python3-cryptography"
    if shutil.which("pacman"):
        return "sudo pacman -S python-cryptography"
    return None


def main():
    # Sem isto, o vídeo/áudio não têm como ir direto entre os aparelhos (é o
    # ÚNICO caminho que existe — o servidor não repassa mídia de jeito
    # nenhum, ver peer.py) e o app abriria "funcionando" só pra ninguém
    # nunca se ver. Melhor recusar a abrir e dizer exatamente o que falta,
    # do mesmo jeito que já é feito pras peças do GStreamer abaixo.
    if not peer.CRYPTO_AVAILABLE:
        print("O Vysor precisa do pacote python3-cryptography (é ele que faz "
              "AES-GCM de verdade — a biblioteca padrão do Python não tem).",
              file=sys.stderr)
        hint = _crypto_install_hint()
        if hint:
            print("\nPra instalar, copie e cole no terminal:\n", file=sys.stderr)
            print("  " + hint + "\n", file=sys.stderr)
        else:
            print("\nProcure o pacote 'python3-cryptography' (ou "
                  "'python-cryptography') da sua distribuição.\n", file=sys.stderr)
        return 1

    media.init()
    missing = media.missing_elements(REQUIRED_ELEMENTS)
    if missing:
        print("O Vysor precisa de algumas peças do GStreamer que não estão "
              "instaladas aqui:", file=sys.stderr)
        print("  " + ", ".join(missing), file=sys.stderr)
        hint = _install_hint(missing)
        if hint:
            print("\nPra instalar, copie e cole no terminal:\n", file=sys.stderr)
            print("  " + hint + "\n", file=sys.stderr)
        else:
            print("\nProcure pelos pacotes do GStreamer da sua distribuição: "
                  "plugins-base, plugins-good, plugins-bad, plugins-ugly e "
                  "libav. Veja o LEIAME_LINUX.md.\n", file=sys.stderr)
        return 1

    # Só um aviso: dá pra usar o app pra assistir mesmo sem isto, só não dá
    # pra transmitir a própria tela no Wayland.
    if portal.session_type() == "wayland" and media.missing_elements(["pipewiresrc"]):
        hint = _install_hint(["pipewiresrc"])
        print("Aviso: falta o pipewiresrc, então não vai dar pra TRANSMITIR "
              "sua tela (assistir os outros funciona normalmente).", file=sys.stderr)
        if hint:
            print("Pra resolver:  " + hint, file=sys.stderr)

    return VysorApp().run(sys.argv)
