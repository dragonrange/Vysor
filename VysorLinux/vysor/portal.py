"""
Pedido de captura de tela ao "portal" do desktop (padrão freedesktop).

No Wayland um programa não pode simplesmente ler a tela — isso é proposital,
por segurança. Quem faz a captura é o próprio ambiente de trabalho, depois de
o usuário autorizar naquela janelinha do sistema ("compartilhar qual tela?").
É exatamente o mecanismo que o Discord, o OBS e os navegadores usam.

A conversa acontece por D-Bus e tem quatro passos:
    CreateSession       -> abre uma sessão
    SelectSources       -> diz se queremos tela, janela, e se mostra o cursor
    Start               -> ABRE O SELETOR pro usuário e devolve o que ele escolheu
    OpenPipeWireRemote  -> devolve o "cano" por onde o vídeo vai chegar

Duas regras deste arquivo, as duas aprendidas do jeito difícil:

1. ESCUTAR ANTES DE PERGUNTAR. Cada passo devolve na hora o endereço de um
   "pedido", e a resposta de verdade chega depois, por sinal. Só que o portal
   pode responder rápido demais — antes mesmo de a nossa chamada retornar. Se
   a gente só começar a escutar depois disso, a resposta já passou e o app
   fica esperando pra sempre (botão de transmitir apagado, nenhum aviso, nada
   a fazer além de reabrir o app). Por isso o endereço do pedido é CALCULADO
   antes, e a escuta é ligada antes da pergunta.

2. NADA DE ESPERAR NA THREAD DA INTERFACE. Todas as chamadas são assíncronas.
   O seletor de tela é uma janela do sistema que fica aberta o tempo que o
   usuário quiser; esperar por ela travaria a janela do Vysor.
"""

import os
import random
import string

import gi
from gi.repository import Gio, GLib   # noqa: E402

BUS_NAME = "org.freedesktop.portal.Desktop"
OBJ_PATH = "/org/freedesktop/portal/desktop"
IFACE = "org.freedesktop.portal.ScreenCast"
REQUEST_IFACE = "org.freedesktop.portal.Request"

# Tipos de fonte que podemos pedir (é um conjunto de bits)
SOURCE_MONITOR = 1
SOURCE_WINDOW = 2
SOURCE_VIRTUAL = 4

CURSOR_HIDDEN = 1
CURSOR_EMBEDDED = 2   # desenha o cursor dentro da imagem
CURSOR_METADATA = 4

# Tempo máximo esperando o desktop responder a um passo. O passo "Start" é o
# que abre o seletor pro usuário, então precisa de bastante folga — mas não
# infinita, senão um portal com defeito deixa o app travado sem explicação.
STEP_TIMEOUT_MS = 120_000
CALL_TIMEOUT_MS = 15_000


def _token():
    return "vysor" + "".join(random.choices(string.ascii_lowercase + string.digits, k=10))


class ScreenCastPortal:
    """
    Uso:
        p = ScreenCastPortal()
        p.start(on_ready=..., on_error=...)

    on_ready recebe (fd, node_id, source_type):
      - fd e node_id vão direto pro ScreenEncoder.start_pipewire();
      - source_type diz se a pessoa escolheu um MONITOR (1) ou uma JANELA (2),
        que é o que decide se o áudio do sistema pode ir junto.
    """

    def __init__(self):
        self._bus = None
        self._session = None
        self._on_ready = None
        self._on_error = None
        self._subs = []
        self._timeout_id = None
        self._finished = False
        self.restore_token = None

    # ---------- utilidades ----------

    def _connect(self):
        if self._bus is None:
            self._bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        return self._bus

    def available_source_types(self):
        """
        Diz o que ESTE desktop aceita capturar. Importante: em ambientes
        baseados em wlroots (Sway, por exemplo) não existe captura de janela
        individual — só a tela inteira. Consultar evita oferecer uma opção
        que não funciona.
        """
        try:
            bus = self._connect()
            val = bus.call_sync(
                BUS_NAME, OBJ_PATH, "org.freedesktop.DBus.Properties", "Get",
                GLib.Variant("(ss)", (IFACE, "AvailableSourceTypes")),
                None, Gio.DBusCallFlags.NONE, 3000, None)
            return val.unpack()[0]
        except Exception:
            return 0

    def is_available(self):
        """O portal está instalado e respondendo?"""
        try:
            self._connect()
            return self.available_source_types() != 0
        except Exception:
            return False

    def _request_path(self, token):
        """
        Calcula o endereço do "pedido" ANTES de fazer a chamada.

        O portal monta esse endereço de forma previsível a partir de quem
        chamou e do token que a gente escolheu — é justamente pra permitir
        começar a escutar antes de perguntar (regra 1 lá em cima).
        """
        sender = self._connect().get_unique_name()      # ex: ":1.42"
        sender = sender[1:].replace(".", "_")           # vira "1_42"
        return f"/org/freedesktop/portal/desktop/request/{sender}/{token}"

    def _call(self, method, args, on_response, token):
        """Escuta a resposta, depois faz a pergunta — nessa ordem, sem esperar."""
        bus = self._connect()
        path = self._request_path(token)

        def on_signal(conn, sender, obj_path, iface, signal, params):
            self._unsubscribe(sub_id)
            if self._finished:
                return
            try:
                code, results = params.unpack()
            except Exception as e:
                return self._fail(f"resposta incompreensível do portal: {e}")
            try:
                on_response(code, results)
            except Exception as e:
                # Sem isto, um erro aqui dentro sumia dentro do GDBus e o app
                # ficava esperando pra sempre por uma resposta que já tinha
                # chegado.
                self._fail(f"falha ao tratar a resposta do portal: {e}")

        sub_id = bus.signal_subscribe(
            BUS_NAME, REQUEST_IFACE, "Response", path, None,
            Gio.DBusSignalFlags.NONE, on_signal)
        self._subs.append(sub_id)

        def on_reply(conn, res):
            try:
                conn.call_finish(res)
            except Exception as e:
                self._unsubscribe(sub_id)
                self._fail(f"o portal recusou o pedido: {e}")

        bus.call(BUS_NAME, OBJ_PATH, IFACE, method, args,
                 GLib.VariantType("(o)"), Gio.DBusCallFlags.NONE,
                 CALL_TIMEOUT_MS, None, on_reply)

    def _unsubscribe(self, sub_id):
        if sub_id in self._subs:
            self._subs.remove(sub_id)
        try:
            if self._bus:
                self._bus.signal_unsubscribe(sub_id)
        except Exception:
            pass

    # ---------- fluxo ----------

    def start(self, on_ready, on_error, include_windows=True, show_cursor=True,
              restore_token=None):
        self._on_ready = on_ready
        self._on_error = on_error
        self._restore_token = restore_token
        self._finished = False

        # Rede de segurança: se o desktop nunca responder, avisamos em vez de
        # deixar o botão de transmitir apagado pra sempre.
        self._timeout_id = GLib.timeout_add(
            STEP_TIMEOUT_MS,
            lambda: self._fail("o seletor de tela do sistema não respondeu.") or False)

        token = _token()
        try:
            self._call("CreateSession",
                       GLib.Variant("(a{sv})", ({
                           "handle_token": GLib.Variant("s", token),
                           "session_handle_token": GLib.Variant("s", _token()),
                       },)),
                       lambda code, res: self._on_session(code, res, include_windows,
                                                          show_cursor),
                       token)
        except Exception as e:
            self._fail(f"não consegui falar com o portal do desktop: {e}")

    def _on_session(self, code, results, include_windows, show_cursor):
        if code != 0:
            return self._fail("o pedido de captura foi cancelado")
        self._session = results.get("session_handle")
        if not self._session:
            return self._fail("o portal não devolveu uma sessão")

        types = SOURCE_MONITOR
        available = self.available_source_types()
        if include_windows and (available & SOURCE_WINDOW):
            types |= SOURCE_WINDOW

        token = _token()
        options = {
            "handle_token": GLib.Variant("s", token),
            "types": GLib.Variant("u", types),
            "multiple": GLib.Variant("b", False),
            "cursor_mode": GLib.Variant("u", CURSOR_EMBEDDED if show_cursor else CURSOR_HIDDEN),
            # Guarda a permissão pra não perguntar toda vez que abrir o app.
            "persist_mode": GLib.Variant("u", 2),
        }
        if self._restore_token:
            options["restore_token"] = GLib.Variant("s", self._restore_token)

        try:
            self._call("SelectSources",
                       GLib.Variant("(oa{sv})", (self._session, options)),
                       self._on_sources, token)
        except Exception as e:
            self._fail(f"falha ao escolher a fonte: {e}")

    def _on_sources(self, code, results):
        if code != 0:
            return self._fail("o pedido de captura foi cancelado")
        token = _token()
        try:
            self._call("Start",
                       GLib.Variant("(osa{sv})",
                                    (self._session, "",
                                     {"handle_token": GLib.Variant("s", token)})),
                       self._on_started, token)
        except Exception as e:
            self._fail(f"falha ao abrir o seletor: {e}")

    def _on_started(self, code, results):
        if code != 0:
            return self._fail("você cancelou o compartilhamento")

        streams = results.get("streams") or []
        if not streams:
            return self._fail("nenhuma tela ou janela foi escolhida")

        node_id = streams[0][0]
        props = streams[0][1] if len(streams[0]) > 1 else {}
        # Qual TIPO de fonte a pessoa escolheu. Isso decide se o som do
        # sistema pode ir junto: ao compartilhar uma janela só, mandar o áudio
        # inteiro do computador seria vazar tudo que ela não pediu.
        source_type = props.get("source_type", SOURCE_MONITOR)
        self.restore_token = results.get("restore_token")

        bus = self._connect()

        def on_reply(conn, res):
            if self._finished:
                return
            try:
                reply, fds = conn.call_with_unix_fd_list_finish(res)
                fd = fds.get(reply.unpack()[0])
            except Exception as e:
                return self._fail(f"falha ao abrir o canal de vídeo: {e}")

            if fd is None or fd < 0:
                return self._fail("o portal não devolveu o canal de vídeo")

            self._done()
            if self._on_ready:
                self._on_ready(fd, node_id, source_type)

        try:
            bus.call_with_unix_fd_list(
                BUS_NAME, OBJ_PATH, IFACE, "OpenPipeWireRemote",
                GLib.Variant("(oa{sv})", (self._session, {})),
                GLib.VariantType("(h)"), Gio.DBusCallFlags.NONE,
                CALL_TIMEOUT_MS, None, None, on_reply)
        except Exception as e:
            self._fail(f"falha ao abrir o canal de vídeo: {e}")

    # ---------- encerramento ----------

    def _done(self):
        """Marca o fluxo como concluído e desarma o cronômetro de segurança."""
        self._finished = True
        if self._timeout_id is not None:
            try:
                GLib.source_remove(self._timeout_id)
            except Exception:
                pass
            self._timeout_id = None

    def _fail(self, message):
        if self._finished:
            return
        self._done()
        if self._on_error:
            self._on_error(message)

    def close(self):
        self._done()
        for sid in list(self._subs):
            self._unsubscribe(sid)
        self._subs.clear()

        bus, session = self._bus, self._session
        self._session = None
        if session and bus:
            try:
                bus.call(BUS_NAME, session, "org.freedesktop.portal.Session",
                         "Close", None, None, Gio.DBusCallFlags.NONE,
                         2000, None, None)
            except Exception:
                pass


def session_type():
    """'wayland', 'x11' ou 'desconhecido'."""
    t = (os.environ.get("XDG_SESSION_TYPE") or "").lower()
    if t in ("wayland", "x11"):
        return t
    if os.environ.get("WAYLAND_DISPLAY"):
        return "wayland"
    if os.environ.get("DISPLAY"):
        return "x11"
    return "desconhecido"
