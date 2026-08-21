"""
Formato dos dados que trafegam entre os clientes do Vysor.

Este arquivo é a "tradução" do que o cliente Windows faz. Se algo aqui não
bater exatamente com o lado de lá, o Linux entra na sala mas ninguém consegue
ver nem ouvir ninguém — então cada detalhe abaixo é intencional.
"""

# Primeiro byte de todo quadro de vídeo, dizendo em que formato ele vem.
TAG_JPEG = 0x00   # imagem JPEG completa e independente
TAG_H264 = 0x01   # uma "access unit" H.264 (um quadro), no formato Annex-B

# Formato do áudio na rede, igual ao do Windows: G.711 μ-law, 48 kHz, 1 canal.
AUDIO_RATE = 48000
AUDIO_CHANNELS = 1

# NALs que aparecem ANTES das fatias de imagem e pertencem ao mesmo quadro:
# delimitador (9), SPS (7), PPS (8) e informação suplementar (6).
LEADING_NALS = (6, 7, 8, 9)
SLICE_NALS = (1, 5)   # 1 = quadro comum, 5 = quadro-chave (IDR)


def tag_frame(tag: int, payload: bytes) -> bytes:
    """Prefixa o marcador de formato, como o cliente Windows espera receber."""
    return bytes([tag]) + payload


def split_frame(data: bytes):
    """Separa o marcador do conteúdo. Devolve (tag, payload)."""
    if not data:
        return None, b""
    return data[0], data[1:]


def find_start_code(buf: bytes, start: int, end: int):
    """
    Procura o próximo "start code" do H.264 (00 00 01 ou 00 00 00 01).
    Devolve (posição, tamanho_do_start_code) ou (-1, 0).
    """
    i = start
    limit = end - 3
    while i <= limit:
        if buf[i] == 0 and buf[i + 1] == 0:
            if i + 3 < end and buf[i + 2] == 0 and buf[i + 3] == 1:
                return i, 4
            if buf[i + 2] == 1:
                return i, 3
        i += 1
    return -1, 0


class AnnexBSplitter:
    """
    Recorta um fluxo H.264 contínuo em quadros individuais (access units).

    O codificador entrega um rio de bytes sem marcação de "aqui acaba um
    quadro". Precisamos cortar nos lugares certos porque cada quadro vira uma
    mensagem separada na rede.

    Detalhe que já causou bug do lado Windows: ao fechar um quadro, é preciso
    recolher também os NALs de cabeçalho que vêm ANTES da imagem (SPS, PPS,
    SEI, delimitador). Se o SPS/PPS ficar no quadro anterior, o quadro-chave
    viaja sem os parâmetros que descrevem o vídeo — e quem receber só aquele
    pacote não consegue decodificar nada.
    """

    MAX_BUFFER = 16 * 1024 * 1024   # trava de segurança

    def __init__(self):
        self._buf = bytearray()
        self._nals = []        # [(posição, tipo)]
        self._pending = None   # início do quadro ainda não fechado
        self._scan = 0

    def feed(self, chunk: bytes):
        """Recebe mais bytes e devolve a lista de quadros completos."""
        self._buf += chunk
        out = []

        while True:
            sc, code_len = find_start_code(self._buf, self._scan, len(self._buf))
            if sc < 0:
                break

            header = sc + code_len
            if header >= len(self._buf):
                break   # start code no fim do buffer: espera mais dados

            nal_type = self._buf[header] & 0x1F
            self._nals.append((sc, nal_type))
            self._scan = header

            if nal_type in SLICE_NALS:
                boundary = sc
                j = len(self._nals) - 2
                while (j >= 0 and self._nals[j][1] in LEADING_NALS
                       and (self._pending is None or self._nals[j][0] >= self._pending)):
                    boundary = self._nals[j][0]
                    j -= 1

                if self._pending is not None and boundary > self._pending:
                    shifted = self._pending
                    out.append(bytes(self._buf[self._pending:boundary]))
                    self._compact(self._pending)
                    boundary -= shifted

                self._pending = boundary

        if len(self._buf) > self.MAX_BUFFER:
            self.reset()

        return out

    def _compact(self, upto: int):
        """Descarta o que já foi entregue e reajusta as posições guardadas."""
        if upto <= 0:
            return
        del self._buf[:upto]
        self._scan = max(0, self._scan - upto)
        self._nals = [(p - upto, t) for (p, t) in self._nals if p - upto >= 0]
        if self._pending is not None:
            self._pending -= upto

    def reset(self):
        self._buf = bytearray()
        self._nals = []
        self._pending = None
        self._scan = 0


def is_keyframe(access_unit: bytes) -> bool:
    """Verdadeiro se este quadro é um quadro-chave (dá pra começar a assistir nele)."""
    i, end = 0, len(access_unit)
    while True:
        sc, cl = find_start_code(access_unit, i, end)
        if sc < 0:
            return False
        header = sc + cl
        if header >= end:
            return False
        if (access_unit[header] & 0x1F) == 5:
            return True
        i = header
