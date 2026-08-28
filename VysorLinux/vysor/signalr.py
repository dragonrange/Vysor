"""
Cliente do SignalR falado direto no protocolo, usando SÓ a biblioteca padrão
do Python.

Por que não usar uma biblioteca pronta: seu amigo teria que instalar pacotes
com pip antes de abrir o app, e cada pacote extra é mais uma chance de dar
errado num Linux diferente do meu. Tudo que o Vysor precisa cabe no que já
vem junto com o Python:

  1. um POST em /roomhub/negotiate pra pegar um bilhete de conexão
     (urllib, que já cuida do HTTPS);
  2. um WebSocket aberto na mão (socket + ssl) — o protocolo é público e a
     parte que usamos é pequena;
  3. mensagens JSON separadas pelo byte 0x1E, que é o formato do SignalR.

Detalhe importante de compatibilidade: no protocolo JSON do SignalR, um vetor
de bytes (um quadro de vídeo, um pedaço de áudio) viaja como texto em base64.
Isso vale nos dois sentidos. O cliente Windows faz isso sem saber, porque a
biblioteca dele converte sozinha; aqui a conversão é explícita.

Threads: tudo que é rede acontece em duas threads próprias (uma que lê, uma
que escreve). Os avisos chegam pelo callback on_event, chamado DESSAS
threads — quem usa precisa levar pra thread da interface antes de mexer na
tela.
"""

import base64
import json
import os
import queue
import socket
import ssl
import struct
import threading
import time
import urllib.parse
import urllib.request

SEP = b"\x1e"   # separador de mensagens do SignalR

MSG_INVOCATION = 1
MSG_PING = 6
MSG_CLOSE = 7

# Opcodes do WebSocket (RFC 6455)
OP_CONT, OP_TEXT, OP_BINARY = 0x0, 0x1, 0x2
OP_CLOSE, OP_PING, OP_PONG = 0x8, 0x9, 0xA

# Uma fila só, de propósito: vídeo e áudio NUNCA passam por aqui (ver
# peer.py — vão direto, UDP, entre os clientes). O que sobra pra esta conexão
# é só sinalização (códigos de sala, endereços, listas de participantes),
# sempre uns poucos bytes, então uma fila pequena já sobra.
SEND_QUEUE_MAX = 32

# Segundos sem NADA chegando = conexão morta. O servidor manda um "ping" a
# cada 10s, então 45s de silêncio significa que ela caiu de verdade.
READ_TIMEOUT = 45


class _Reader:
    """Leitura com buffer em cima de um socket — evita ler byte a byte."""

    def __init__(self, sock):
        self.sock = sock
        self.buf = b""

    def _fill(self):
        chunk = self.sock.recv(65536)
        if not chunk:
            raise ConnectionError("conexão fechada pelo servidor")
        self.buf += chunk

    def read_exact(self, n):
        while len(self.buf) < n:
            self._fill()
        out, self.buf = self.buf[:n], self.buf[n:]
        return out

    def read_until(self, marker):
        while marker not in self.buf:
            self._fill()
        idx = self.buf.index(marker) + len(marker)
        out, self.buf = self.buf[:idx], self.buf[idx:]
        return out


def _mask(data: bytes, key: bytes) -> bytes:
    """
    Aplica a máscara do WebSocket (um XOR com uma chave de 4 bytes que se
    repete). Um quadro de vídeo tem dezenas de KB, então isso precisa ser
    rápido: usamos int.from_bytes pra fazer o XOR do bloco inteiro de uma vez,
    em vez de um laço byte a byte em Python.
    """
    n = len(data)
    if n == 0:
        return b""
    repeats = (n + 3) // 4
    long_key = (key * repeats)[:n]
    return (int.from_bytes(data, "big") ^ int.from_bytes(long_key, "big")).to_bytes(n, "big")


class WebSocket:
    """
    O mínimo de WebSocket que o Vysor precisa: abrir, mandar, receber,
    responder ping, fechar. Só o lado cliente.
    """

    def __init__(self, sock):
        self.sock = sock
        self.reader = _Reader(sock)
        self._send_lock = threading.Lock()
        self.closed = False

    # ---------- abertura ----------

    @classmethod
    def connect(cls, url, timeout=20):
        parts = urllib.parse.urlsplit(url)
        secure = parts.scheme == "wss"
        host = parts.hostname
        port = parts.port or (443 if secure else 80)
        path = parts.path or "/"
        if parts.query:
            path += "?" + parts.query

        sock = socket.create_connection((host, port), timeout=timeout)
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        if secure:
            ctx = ssl.create_default_context()
            sock = ctx.wrap_socket(sock, server_hostname=host)

        key = base64.b64encode(os.urandom(16)).decode("ascii")
        host_header = f"{host}:{port}" if parts.port else host
        headers = [
            f"GET {path} HTTP/1.1",
            f"Host: {host_header}",
            "Upgrade: websocket",
            "Connection: Upgrade",
            f"Sec-WebSocket-Key: {key}",
            "Sec-WebSocket-Version: 13",
            "User-Agent: Vysor-Linux/1.0",
        ]
        sock.sendall(("\r\n".join(headers) + "\r\n\r\n").encode())

        ws = cls(sock)
        response = ws.reader.read_until(b"\r\n\r\n").decode("latin-1")
        status = response.split("\r\n", 1)[0]
        if " 101" not in status:
            ws.abort()
            raise ConnectionError(f"o servidor recusou o WebSocket ({status.strip()})")
        sock.settimeout(READ_TIMEOUT)
        return ws

    # ---------- envio ----------

    def send(self, payload: bytes, opcode=OP_TEXT):
        """Manda um quadro. Todo quadro do cliente vai mascarado (é a regra)."""
        n = len(payload)
        header = bytearray([0x80 | opcode])
        if n < 126:
            header.append(0x80 | n)
        elif n < 65536:
            header.append(0x80 | 126)
            header += struct.pack("!H", n)
        else:
            header.append(0x80 | 127)
            header += struct.pack("!Q", n)

        key = os.urandom(4)
        header += key
        frame = bytes(header) + _mask(payload, key)

        with self._send_lock:
            if self.closed:
                return
            self.sock.sendall(frame)

    # ---------- recebimento ----------

    def recv(self):
        """
        Devolve (opcode, dados) de uma mensagem COMPLETA, já juntando os
        pedaços quando o servidor parte a mensagem em vários quadros.
        Responde ping automaticamente.
        """
        message = b""
        first_op = None

        while True:
            b0, b1 = self.reader.read_exact(2)
            fin = b0 & 0x80
            opcode = b0 & 0x0F
            masked = b1 & 0x80
            length = b1 & 0x7F

            if length == 126:
                length = struct.unpack("!H", self.reader.read_exact(2))[0]
            elif length == 127:
                length = struct.unpack("!Q", self.reader.read_exact(8))[0]

            key = self.reader.read_exact(4) if masked else None
            data = self.reader.read_exact(length) if length else b""
            if key:
                data = _mask(data, key)

            if opcode == OP_PING:
                self.send(data, OP_PONG)
                continue
            if opcode == OP_PONG:
                continue
            if opcode == OP_CLOSE:
                self.abort()
                raise ConnectionError("o servidor encerrou a conexão")

            if opcode != OP_CONT and first_op is None:
                first_op = opcode
            message += data
            if fin:
                return first_op or OP_TEXT, message

    def close(self):
        """Fechamento educado: avisa o servidor e derruba o socket."""
        if not self.closed:
            try:
                self.send(b"", OP_CLOSE)
            except Exception:
                pass
        self.abort()

    def abort(self):
        """Derruba a conexão de fora, pra soltar quem está esperando leitura."""
        self.closed = True
        try:
            self.sock.shutdown(socket.SHUT_RDWR)
        except Exception:
            pass
        try:
            self.sock.close()
        except Exception:
            pass


class SignalRClient:
    """
    Conexão com o servidor do Vysor, com reconexão automática.

    on_state recebe: "conectando", "conectado", "desconectado: <motivo>".
    Quando o estado vira "conectado" DEPOIS de já ter estado numa sala, quem
    usa esta classe deve voltar pra sala (RejoinRoom) — quem sabe o código da
    sala é a interface, não esta classe.
    """

    def __init__(self, base_url, on_event, on_state=None):
        self.base_url = base_url.rstrip("/")
        self.on_event = on_event
        self.on_state = on_state or (lambda s: None)

        self._ws = None
        self._running = False
        self._reader_thread = None
        self._writer_thread = None
        self._outbox = queue.Queue(maxsize=SEND_QUEUE_MAX)
        self._wakeup = threading.Event()
        self._connected = threading.Event()
        self._stop_event = threading.Event()
        self.dropped_messages = 0

    # ---------- ciclo de vida ----------

    def start(self):
        if self._reader_thread:
            return
        self._running = True
        self._stop_event.clear()
        self._reader_thread = threading.Thread(target=self._run, daemon=True,
                                               name="VysorNetRead")
        self._writer_thread = threading.Thread(target=self._write_loop, daemon=True,
                                               name="VysorNetWrite")
        self._reader_thread.start()
        self._writer_thread.start()

    def flush(self, timeout=0.4):
        """
        Espera (pouco) as mensagens de controle pendentes saírem de verdade.

        Serve pro caso de fechar o app: o "avise que eu saí da sala" precisa
        chegar ao servidor antes de a conexão morrer, senão você fica de
        fantasma na lista dos seus amigos pelo prazo de tolerância. Limitado
        no tempo de propósito — fechar a janela nunca deve parecer travado.
        """
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            if self._outbox.empty() or not self._connected.is_set():
                return
            time.sleep(0.02)

    def stop(self, drain=0.4):
        self.flush(drain)
        self._running = False
        self._stop_event.set()
        self._connected.clear()
        self._wakeup.set()
        ws = self._ws
        if ws:
            ws.abort()
        # A thread de leitura pode estar presa no "negotiate" (uma espera de
        # rede que não dá pra interromper de fora). Ela é daemon e já sabe que
        # deve morrer, então não seguramos o fechamento da janela por causa
        # dela — esperamos pouco e seguimos.
        for t in (self._reader_thread, self._writer_thread):
            if t and t is not threading.current_thread():
                t.join(timeout=1)
        self._reader_thread = self._writer_thread = None
        self._ws = None

    @property
    def connected(self):
        return self._connected.is_set()

    # ---------- laço de conexão ----------

    def _run(self):
        backoff = 1
        while self._running:
            try:
                self.on_state("conectando")
                self._connect_once()          # só volta quando a conexão cai
                backoff = 1
            except Exception as e:
                if not self._running:
                    break
                self.on_state(f"desconectado: {e}")
            finally:
                self._connected.clear()
                self._ws = None

            if not self._running:
                break
            # Espera crescente antes de tentar de novo, pra não martelar o
            # servidor quando ele estiver fora do ar. Acorda na hora se
            # alguém pedir pra parar.
            if self._stop_event.wait(backoff):
                break
            backoff = min(backoff * 2, 15)

    def _connect_once(self):
        # 1) negotiate — o servidor devolve o bilhete que identifica a conexão
        token = self._negotiate()

        # 2) WebSocket levando o bilhete
        ws_url = self.base_url
        if ws_url.startswith("https://"):
            ws_url = "wss://" + ws_url[len("https://"):]
        elif ws_url.startswith("http://"):
            ws_url = "ws://" + ws_url[len("http://"):]
        ws_url += "?" + urllib.parse.urlencode({"id": token})

        ws = WebSocket.connect(ws_url)
        self._ws = ws
        try:
            # 3) aperto de mão: "eu falo JSON, versão 1"
            ws.send(json.dumps({"protocol": "json", "version": 1}).encode() + SEP)
            _, raw = ws.recv()
            for obj in self._parse(raw):
                if obj.get("error"):
                    raise ConnectionError(f"servidor recusou: {obj['error']}")

            # Joga fora o que sobrou da conexão anterior: são quadros velhos e
            # chamadas que não fazem mais sentido nesta conexão nova.
            self._drain_outbox()
            self._connected.set()
            self.on_state("conectado")

            while self._running:
                _, raw = ws.recv()
                for obj in self._parse(raw):
                    self._dispatch(obj)
        finally:
            self._connected.clear()
            ws.abort()

    def _negotiate(self):
        url = f"{self.base_url}/negotiate?negotiateVersion=1"
        req = urllib.request.Request(url, data=b"", method="POST")
        req.add_header("Content-Type", "text/plain;charset=UTF-8")
        with urllib.request.urlopen(req, timeout=20) as resp:
            info = json.loads(resp.read().decode("utf-8"))
        token = info.get("connectionToken") or info.get("connectionId")
        if not token:
            raise ConnectionError("o servidor não devolveu identificador de conexão")
        return token

    def _drain_outbox(self):
        while True:
            try:
                self._outbox.get_nowait()
            except queue.Empty:
                break

    # ---------- entrada ----------

    @staticmethod
    def _parse(raw):
        """Uma mensagem pode trazer vários objetos JSON grudados, separados por 0x1E."""
        if isinstance(raw, str):
            raw = raw.encode()
        if not raw:
            return []
        out = []
        for piece in raw.split(SEP):
            if not piece:
                continue
            try:
                obj = json.loads(piece)
            except Exception:
                continue
            if isinstance(obj, dict):
                out.append(obj)
        return out

    def _dispatch(self, obj):
        t = obj.get("type")
        if t == MSG_INVOCATION:
            try:
                self.on_event(obj.get("target", ""), obj.get("arguments") or [])
            except Exception:
                pass
        elif t == MSG_PING:
            # Responder ao ping é o que prova pro servidor que ainda estamos
            # vivos. Sem isso ele nos desconecta por tempo esgotado.
            self._enqueue({"type": MSG_PING})
        elif t == MSG_CLOSE:
            self.on_state("servidor encerrou a conexão")

    # ---------- saída ----------

    def _next_payload(self):
        try:
            return self._outbox.get_nowait()
        except queue.Empty:
            return None

    def _write_loop(self):
        while self._running:
            payload = self._next_payload()
            if payload is None:
                self._wakeup.wait(0.2)
                self._wakeup.clear()
                continue
            ws = self._ws
            if ws is None or ws.closed or not self._connected.is_set():
                continue
            try:
                ws.send(payload)
            except Exception:
                # A thread de leitura percebe a queda e reconecta sozinha.
                pass

    def _enqueue(self, obj):
        if not self._connected.is_set():
            return
        payload = json.dumps(obj, separators=(",", ":")).encode() + SEP

        try:
            self._outbox.put_nowait(payload)
        except queue.Full:
            self.dropped_messages += 1
            # Mensagem de sinalização não pode sumir: abre espaço jogando
            # fora a mais antiga.
            try:
                self._outbox.get_nowait()
                self._outbox.put_nowait(payload)
            except Exception:
                pass
        self._wakeup.set()

    def invoke(self, method, *args):
        """
        Chama um método no servidor. Vetores de bytes viram base64, porque é
        assim que o protocolo JSON do SignalR os representa — mandar a lista
        de números crua faz o servidor recusar a mensagem.

        NÃO existe (e não deve voltar a existir) um invoke_media() aqui.
        Vídeo e áudio vão só pelo caminho direto (ver vysor/peer.py) — o
        servidor não tem mais nenhum método capaz de repassar mídia.
        """
        self._enqueue(self._message(method, args))

    @staticmethod
    def _message(method, args):
        converted = [
            base64.b64encode(a).decode("ascii") if isinstance(a, (bytes, bytearray)) else a
            for a in args
        ]
        return {"type": MSG_INVOCATION, "target": method, "arguments": converted}
