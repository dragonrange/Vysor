"""
Conexão DIRETA (P2P) entre este PC e os outros clientes Vysor (Windows,
Android, ou outro Linux) na mesma sala: furo de NAT por UDP, vídeo/áudio
cifrados (AES-GCM) e fragmentados em pacotes pequenos.

POR QUE ISTO EXISTE
O servidor não repassa mais vídeo nem áudio de jeito nenhum (ver RoomHub.cs no
servidor) — foi repassar mídia que estourou o plano grátis do Render duas
vezes. Este cliente Linux, diferente do Windows, nunca teve furo de NAT: só
mandava tudo por "SendScreenFrame"/"SendAudioChunk" (relay). Sem este módulo,
o Linux simplesmente pararia de conseguir transmitir ou assistir qualquer
coisa.

WIRE-COMPATÍVEL COM (mesmo formato de pacote, mesma derivação de chave):
  - VysorClient/Services/{PeerPacket,PeerTransport,FrameReassembler,StunClient}.cs
  - VysorAndroid .../{PeerPacket,PeerTransport,StunClient}.java
Os três precisam concordar byte a byte, porque um PC Windows, um Android e
este Linux podem estar na mesma sala ao mesmo tempo.

DEPENDÊNCIA NOVA: o pacote de sistema "python3-cryptography" (Debian/Ubuntu),
"python3-cryptography" (Fedora) ou "python-cryptography" (Arch) — é a única
peça que faz AES-GCM de verdade que a biblioteca padrão do Python não tem.
Continua sem `pip`: é pacote de sistema, igual o GStreamer. Ver CRYPTO_AVAILABLE.

Threads: os callbacks (on_frame/on_state/on_same_network_stuck) vêm de
threads de rede próprias deste módulo — nunca da thread da interface. Quem
usa precisa levar pra tela via GLib.idle_add antes de tocar em qualquer
widget, exatamente como já é feito com o SignalRClient.
"""

import hashlib
import hmac as _hmac
import secrets
import socket
import struct
import threading
import time

try:
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    CRYPTO_AVAILABLE = True
except ImportError:
    AESGCM = None
    CRYPTO_AVAILABLE = False


# ---------------------------------------------------------------------------
# PeerPacket: formato do quadro na rede
# ---------------------------------------------------------------------------

KIND_VIDEO = 0
KIND_AUDIO = 1
TYPE_MEDIA = 2
HEADER_SIZE = 10                 # type(1) + kind(1) + frameId(4) + fragIndex(2) + fragCount(2)
MAX_PAYLOAD = 1200                # abaixo de ~1400 pra não ser refragmentado pelos roteadores
_NONCE_SIZE = 12
_TAG_SIZE = 16
_HEADER_FMT = "!BBIHH"


def derive_key(room_code: str) -> bytes:
    """A chave nasce do código da sala — quem não foi convidado não lê nada."""
    password = room_code.strip().upper().encode("utf-8")
    return hashlib.pbkdf2_hmac("sha256", password, b"vysor-sala-v1", 100_000, dklen=32)


def _aad(frame_id: int, kind: int) -> bytes:
    return struct.pack("!IB", frame_id, kind)


def _encrypt(plain: bytes, key: bytes, frame_id: int, kind: int) -> bytes:
    nonce = secrets.token_bytes(_NONCE_SIZE)
    # AESGCM.encrypt já devolve cifrado+tag grudados no final — mesmo formato
    # que o lado C#/Java monta na mão.
    sealed = AESGCM(key).encrypt(nonce, plain, _aad(frame_id, kind))
    return nonce + sealed


def _decrypt(sealed: bytes, key: bytes, frame_id: int, kind: int):
    if len(sealed) < _NONCE_SIZE + _TAG_SIZE:
        return None
    nonce, rest = sealed[:_NONCE_SIZE], sealed[_NONCE_SIZE:]
    try:
        return AESGCM(key).decrypt(nonce, rest, _aad(frame_id, kind))
    except Exception:
        return None   # pacote adulterado, ou de alguém com outro código de sala


def pack(frame: bytes, kind: int, frame_id: int, key: bytes):
    """Cifra o quadro inteiro (um selo de segurança por quadro, não por pedaço)
    e recorta em pacotes prontos pra sair na rede."""
    sealed = _encrypt(frame, key, frame_id, kind)
    frag_count = max(1, (len(sealed) + MAX_PAYLOAD - 1) // MAX_PAYLOAD)
    if frag_count > 0xFFFF:
        return []   # quadro absurdo: descarta em vez de estourar

    packets = []
    for i in range(frag_count):
        offset = i * MAX_PAYLOAD
        chunk = sealed[offset:offset + MAX_PAYLOAD]
        header = struct.pack(_HEADER_FMT, TYPE_MEDIA, kind, frame_id, i, frag_count)
        packets.append(header + chunk)
    return packets


def read_header(packet: bytes):
    if len(packet) < HEADER_SIZE or packet[0] != TYPE_MEDIA:
        return None
    _type, kind, frame_id, frag_index, frag_count = struct.unpack(_HEADER_FMT, packet[:HEADER_SIZE])
    if frag_count == 0 or frag_index >= frag_count:
        return None
    return kind, frame_id, frag_index, frag_count


# ---------------------------------------------------------------------------
# FrameReassembler: junta os pedaços, e sabe desistir
# ---------------------------------------------------------------------------

_MAX_PENDING = 16          # quadros incompletos em montagem ao mesmo tempo
_MAX_AGE = 2.0              # segundos: mais que isso, o quadro já não serve pra tela ao vivo


def _is_older_or_equal(candidate: int, reference: int) -> bool:
    """Comparação que aguenta o contador de 32 bits dar a volta."""
    diff = (candidate - reference) & 0xFFFFFFFF
    if diff >= 0x80000000:
        diff -= 0x100000000
    return diff <= 0


class FrameReassembler:
    def __init__(self, key: bytes):
        self._key = key
        self._pending = {}          # frame_id -> {fragments, received, total, first_seen, kind}
        self._last_delivered = None

    def accept(self, packet: bytes):
        """Devolve (kind, frame_bytes) quando o quadro completa e decifra
        certo, ou None (pedaço faltando, ou pacote não presta)."""
        header = read_header(packet)
        if header is None:
            return None
        kind, frame_id, frag_index, frag_count = header

        if self._last_delivered is not None and _is_older_or_equal(frame_id, self._last_delivered):
            return None

        self._drop_expired()

        entry = self._pending.get(frame_id)
        if entry is None:
            if len(self._pending) >= _MAX_PENDING:
                self._drop_oldest()
            entry = {"fragments": [None] * frag_count, "received": 0, "total": 0,
                      "first_seen": time.monotonic(), "kind": kind}
            self._pending[frame_id] = entry

        if len(entry["fragments"]) != frag_count:
            # Pacote corrompido, ou de outra transmissão: descarta o quadro
            # todo em vez de montar uma imagem misturada.
            del self._pending[frame_id]
            return None

        if entry["fragments"][frag_index] is not None:
            return None   # pedaço repetido

        payload = packet[HEADER_SIZE:]
        entry["fragments"][frag_index] = payload
        entry["received"] += 1
        entry["total"] += len(payload)

        if entry["received"] < frag_count:
            return None

        sealed = b"".join(entry["fragments"])
        del self._pending[frame_id]
        self._last_delivered = frame_id

        frame = _decrypt(sealed, self._key, frame_id, entry["kind"])
        return (entry["kind"], frame) if frame is not None else None

    def _drop_expired(self):
        if not self._pending:
            return
        cutoff = time.monotonic() - _MAX_AGE
        for fid in [fid for fid, e in self._pending.items() if e["first_seen"] < cutoff]:
            del self._pending[fid]

    def _drop_oldest(self):
        oldest = min(self._pending.items(), key=lambda kv: kv[1]["first_seen"])[0]
        del self._pending[oldest]


# ---------------------------------------------------------------------------
# STUN: "de fora, qual é o meu endereço?"
# ---------------------------------------------------------------------------

_MAGIC_COOKIE = 0x2112A442
_BINDING_REQUEST = 0x0001
_BINDING_RESPONSE = 0x0101
_ATTR_MAPPED_ADDRESS = 0x0001
_ATTR_XOR_MAPPED_ADDRESS = 0x0020

PUBLIC_STUN_SERVERS = [
    ("stun.l.google.com", 19302),
    ("stun1.l.google.com", 19302),
    ("stun.cloudflare.com", 3478),
]


def stun_build_request(transaction_id: bytes) -> bytes:
    return struct.pack("!HHI", _BINDING_REQUEST, 0, _MAGIC_COOKIE) + transaction_id


def stun_parse_response(data: bytes, expected_transaction_id: bytes):
    """Desembaralha o XOR (não é segurança — é só pra roteadores antigos não
    reescreverem o que parece um endereço IP no meio do pacote)."""
    if len(data) < 20:
        return None
    msg_type, msg_len, cookie = struct.unpack("!HHI", data[:8])
    if msg_type != _BINDING_RESPONSE or cookie != _MAGIC_COOKIE:
        return None
    if data[8:20] != expected_transaction_id:
        return None

    pos, end = 20, min(len(data), 20 + msg_len)
    fallback = None
    while pos + 4 <= end:
        attr_type, attr_len = struct.unpack("!HH", data[pos:pos + 4])
        value_pos = pos + 4
        if value_pos + attr_len > end:
            break
        if attr_type in (_ATTR_XOR_MAPPED_ADDRESS, _ATTR_MAPPED_ADDRESS) and attr_len >= 8:
            family = data[value_pos + 1]
            if family == 0x01:
                port = struct.unpack("!H", data[value_pos + 2:value_pos + 4])[0]
                addr = bytearray(data[value_pos + 4:value_pos + 8])
                if attr_type == _ATTR_XOR_MAPPED_ADDRESS:
                    port ^= (_MAGIC_COOKIE >> 16)
                    cookie_bytes = struct.pack("!I", _MAGIC_COOKIE)
                    for i in range(4):
                        addr[i] ^= cookie_bytes[i]
                    return (socket.inet_ntoa(bytes(addr)), port)
                if fallback is None:
                    fallback = (socket.inet_ntoa(bytes(addr)), port)
        pos = value_pos + attr_len
        pos += (4 - (attr_len % 4)) % 4
    return fallback


def resolve_stun_server(host: str, port: int):
    try:
        return (socket.gethostbyname(host), port)
    except OSError:
        return None


def local_ip_guess():
    """
    'Se eu mandasse um pacote pra internet, por qual endereço local ele
    sairia?' Não manda nada de verdade — connect() em UDP só escolhe a rota
    —, mas é o jeito padrão de achar o IP local sem biblioteca extra (a
    enumeração completa de interfaces precisaria de um pacote fora da
    biblioteca padrão, o que este app evita de propósito). Cobre o caso
    comum (uma interface de rede ativa); quem tiver várias e precisar de uma
    específica pode usar Tailscale/IP fixo, como já é sugerido no cliente
    Windows.
    """
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("8.8.8.8", 80))
        return s.getsockname()[0]
    except OSError:
        return None
    finally:
        s.close()


def subnet_prefix(ip: str):
    parts = ip.split(".")
    return ".".join(parts[:3]) if len(parts) == 4 else None


# ---------------------------------------------------------------------------
# Pacote de contato (o "furo de NAT" em si)
# ---------------------------------------------------------------------------

_PUNCH, _ACK, _KEEP = 0, 1, 3
_SIG_SIZE = 16

SAME_NETWORK_STUCK_AFTER = 6.0   # segundos
PEER_TIMEOUT = 20.0
KEEPALIVE_INTERVAL = 10.0
PUNCH_INTERVAL = 0.25


def _sign(room_key: bytes, packet_type: int, peer_id: bytes) -> bytes:
    mac = _hmac.new(room_key, bytes([packet_type]) + peer_id, hashlib.sha256)
    return mac.digest()[:_SIG_SIZE]


class _Peer:
    def __init__(self, peer_id: str, room_key: bytes):
        self.id = peer_id
        self.reassembler = FrameReassembler(room_key)
        self.candidates = []       # list[(ip, port)]
        self.confirmed = None       # (ip, port) ou None
        self.last_heard = 0.0
        self.next_frame_id = 0
        self.same_network_hint = False
        self.first_seen_at = time.monotonic()
        self.stuck_notified = False


class PeerTransport:
    """
    on_frame(user_id, kind, data): quadro remontado e decifrado.
    on_state(user_id, connected): amigo ficou (ou deixou de estar) alcançável direto.
    on_same_network_stuck(user_id): indício forte de mesma rede que não furou
        o NAT depois de alguns segundos — normalmente isolamento de
        cliente/AP no roteador, não "internet ruim".
    Todos os callbacks vêm de threads de rede: levar pra tela via GLib.idle_add.
    """

    def __init__(self, my_id: str, room_key: bytes,
                 on_frame=None, on_state=None, on_same_network_stuck=None):
        self.my_id = my_id
        self.room_key = room_key
        self.on_frame = on_frame
        self.on_state = on_state
        self.on_same_network_stuck = on_same_network_stuck

        self._peers = {}
        self._lock = threading.Lock()
        self._socket = None
        self._running = False
        self._rx_thread = None
        self._maint_thread = None
        self._local_subnet_prefixes = set()
        self._stun_waiters = {}    # transaction id (hex) -> (threading.Event, dict com "result")
        self._stun_lock = threading.Lock()

    def set_local_subnet_prefixes(self, prefixes):
        self._local_subnet_prefixes = set(prefixes)

    def start(self) -> bool:
        if self._running:
            return True
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                s.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4 * 1024 * 1024)
                s.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 2 * 1024 * 1024)
            except OSError:
                pass   # sistema não deixa pedir buffer tão grande: segue com o padrão
            s.bind(("0.0.0.0", 0))
            s.settimeout(0.5)
            self._socket = s
            self._running = True
            self._rx_thread = threading.Thread(target=self._receive_loop, daemon=True, name="VysorUdpRecv")
            self._maint_thread = threading.Thread(target=self._maintenance_loop, daemon=True, name="VysorUdpKeep")
            self._rx_thread.start()
            self._maint_thread.start()
            return True
        except OSError:
            self.stop()
            return False

    def local_port(self):
        return self._socket.getsockname()[1] if self._socket else -1

    def stop(self):
        self._running = False
        if self._socket:
            try:
                self._socket.close()
            except OSError:
                pass
        self._socket = None
        with self._lock:
            self._peers.clear()

    # ---------- amigos ----------

    def add_peer(self, peer_id: str, candidates):
        if not peer_id or peer_id == self.my_id:
            return
        with self._lock:
            peer = self._peers.get(peer_id)
            if peer is None:
                peer = _Peer(peer_id, self.room_key)
                self._peers[peer_id] = peer
            for ip, port in candidates:
                if (ip, port) not in peer.candidates:
                    peer.candidates.append((ip, port))
                    prefix = subnet_prefix(ip)
                    if prefix and prefix in self._local_subnet_prefixes:
                        peer.same_network_hint = True

    def remove_peer(self, peer_id: str):
        with self._lock:
            self._peers.pop(peer_id, None)

    def is_connected(self, peer_id: str) -> bool:
        with self._lock:
            peer = self._peers.get(peer_id)
            return peer is not None and peer.confirmed is not None

    def is_same_network_hint(self, peer_id: str) -> bool:
        with self._lock:
            peer = self._peers.get(peer_id)
            return peer is not None and peer.same_network_hint

    def connected_peers(self):
        with self._lock:
            return [p.id for p in self._peers.values() if p.confirmed is not None]

    # ---------- envio ----------
    # Sem servidor de repasse: manda só pra quem já furou o NAT. Quem ainda
    # não conectou perde o quadro (ver PeerMedia.cs / peer.broadcast nos
    # outros clientes — mesma regra, sem exceção).

    def send(self, peer_id: str, kind: int, frame: bytes):
        with self._lock:
            peer = self._peers.get(peer_id)
            if peer is None or peer.confirmed is None:
                return
            peer.next_frame_id = (peer.next_frame_id + 1) & 0xFFFFFFFF
            frame_id = peer.next_frame_id
            target = peer.confirmed
        sock = self._socket
        if sock is None:
            return
        try:
            for packet in pack(frame, kind, frame_id, self.room_key):
                sock.sendto(packet, target)
        except OSError:
            pass

    def broadcast(self, kind: int, frame: bytes):
        for peer_id in list(self._peers.keys()):
            self.send(peer_id, kind, frame)

    def _send_raw(self, data: bytes, target):
        sock = self._socket
        if sock is None:
            return
        try:
            sock.sendto(data, target)
        except OSError:
            pass

    def _send_contact(self, packet_type: int, target):
        peer_id_bytes = self.my_id.encode("utf-8")
        sig = _sign(self.room_key, packet_type, peer_id_bytes)
        self._send_raw(bytes([packet_type]) + sig + peer_id_bytes, target)

    def _read_contact(self, data: bytes, packet_type: int):
        if len(data) <= 1 + _SIG_SIZE:
            return None
        peer_id_bytes = data[1 + _SIG_SIZE:]
        if len(peer_id_bytes) > 128:
            return None
        expected = _sign(self.room_key, packet_type, peer_id_bytes)
        if not _hmac.compare_digest(data[1:1 + _SIG_SIZE], expected):
            return None
        try:
            return peer_id_bytes.decode("utf-8")
        except UnicodeDecodeError:
            return None

    # ---------- STUN ----------
    # Roda no MESMO socket que o _receive_loop já está lendo, então a
    # resposta não pode ser lida por um recv() de fora: registra um
    # "esperador" antes de mandar, e quem entrega é o próprio _receive_loop.

    def query_stun(self, server, timeout=3.0):
        transaction_id = secrets.token_bytes(12)
        key = transaction_id.hex()
        event = threading.Event()
        holder = {"result": None}
        with self._stun_lock:
            self._stun_waiters[key] = (event, holder)
        try:
            request = stun_build_request(transaction_id)
            per_try = max(0.4, timeout / 3)
            for _attempt in range(3):
                self._send_raw(request, server)
                if event.wait(per_try):
                    return holder["result"]
            return None
        finally:
            with self._stun_lock:
                self._stun_waiters.pop(key, None)

    def _try_handle_stun(self, data: bytes) -> bool:
        if len(data) < 20:
            return False
        msg_type = struct.unpack("!H", data[0:2])[0]
        if msg_type != _BINDING_RESPONSE:
            return False
        cookie = struct.unpack("!I", data[4:8])[0]
        if cookie != _MAGIC_COOKIE:
            return False

        transaction_id = data[8:20]
        key = transaction_id.hex()
        with self._stun_lock:
            entry = self._stun_waiters.get(key)
        if entry is None:
            return True   # era STUN, mas de uma pergunta que já expirou
        event, holder = entry
        holder["result"] = stun_parse_response(data, transaction_id)
        event.set()
        return True

    # ---------- laços ----------

    def _receive_loop(self):
        sock = self._socket
        while self._running:
            try:
                data, addr = sock.recvfrom(65536)
            except socket.timeout:
                continue
            except OSError:
                break
            try:
                self._handle(data, addr)
            except Exception:
                pass

    def _handle(self, data: bytes, addr):
        if self._try_handle_stun(data):
            return
        if not data:
            return
        packet_type = data[0]

        if packet_type in (_PUNCH, _ACK, _KEEP):
            peer_id = self._read_contact(data, packet_type)
            if peer_id is None:
                return
            became_connected = False
            with self._lock:
                peer = self._peers.get(peer_id)
                if peer is None:
                    return
                became_connected = peer.confirmed is None
                peer.last_heard = time.monotonic()
                peer.confirmed = addr
            if packet_type == _PUNCH:
                self._send_contact(_ACK, addr)
            if became_connected and self.on_state:
                self.on_state(peer_id, True)
            return

        if packet_type == TYPE_MEDIA:
            target_peer = None
            with self._lock:
                for peer in self._peers.values():
                    if peer.confirmed == addr:
                        target_peer = peer
                        break
            if target_peer is None:
                return
            result = target_peer.reassembler.accept(data)
            if result is not None and self.on_frame:
                kind, frame = result
                self.on_frame(target_peer.id, kind, frame)

    def _maintenance_loop(self):
        while self._running:
            now = time.monotonic()
            with self._lock:
                snapshot = [(p, p.confirmed, list(p.candidates)) for p in self._peers.values()]

            for peer, confirmed, candidates in snapshot:
                try:
                    if confirmed is None:
                        for candidate in candidates:
                            self._send_contact(_PUNCH, candidate)

                        newly_stuck = False
                        with self._lock:
                            if (peer.same_network_hint and not peer.stuck_notified
                                    and now - peer.first_seen_at > SAME_NETWORK_STUCK_AFTER):
                                peer.stuck_notified = True
                                newly_stuck = True
                        if newly_stuck and self.on_same_network_stuck:
                            self.on_same_network_stuck(peer.id)

                    elif now - peer.last_heard > PEER_TIMEOUT:
                        became_disconnected = False
                        with self._lock:
                            if peer.confirmed == confirmed:
                                peer.confirmed = None
                                peer.first_seen_at = now
                                peer.stuck_notified = False
                                became_disconnected = True
                        if became_disconnected and self.on_state:
                            self.on_state(peer.id, False)

                    elif now - peer.last_heard > KEEPALIVE_INTERVAL:
                        self._send_contact(_KEEP, confirmed)
                except Exception:
                    pass

            time.sleep(PUNCH_INTERVAL)
