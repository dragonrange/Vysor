"""
Tudo que envolve vídeo e áudio, delegado ao GStreamer.

Divisão de trabalho deste app: o Python só encosta em dados JÁ COMPRIMIDOS
(alguns KB por quadro) e o GStreamer cuida dos pixels. Isso não é preferência
de estilo — é necessidade. Um quadro 1080p cru tem ~8 MB; passar isso pelo
Python 30 vezes por segundo, vezes o número de pessoas na sala, não fecha a
conta. Comprimido, é trivial.
"""

import threading

import gi
gi.require_version("Gst", "1.0")
from gi.repository import Gst, GLib   # noqa: E402

from .protocol import AUDIO_RATE, AUDIO_CHANNELS   # noqa: E402

_initialized = False


def _shutdown_async(pipeline):
    """
    Desliga um pipeline numa thread de fundo.

    Desligar espera as threads internas do GStreamer terminarem. Feito na
    thread da interface — que é de onde vem todo fechamento de telinha — isso
    congela a janela por um instante, e fechar várias telinhas de uma vez (ao
    sair da sala) empilha uma espera atrás da outra. Como desligar é seguro de
    qualquer thread, jogamos pra fora do caminho.
    """
    if pipeline is None:
        return

    def work():
        try:
            pipeline.set_state(Gst.State.NULL)
        except Exception:
            pass

    threading.Thread(target=work, daemon=True, name="VysorGstStop").start()


def init():
    global _initialized
    if not _initialized:
        Gst.init(None)
        _initialized = True


def missing_elements(names):
    """Devolve os elementos do GStreamer que faltam instalar nesta máquina."""
    init()
    return [n for n in names if Gst.ElementFactory.find(n) is None]


def _try_set(element, **props):
    """
    Define propriedades que podem não existir na versão de GStreamer da
    máquina do usuário. Fazer isso pela descrição do pipeline seria pior: uma
    propriedade desconhecida derruba o pipeline INTEIRO, e o app perderia
    vídeo ou áudio por causa de um ajuste fino que era só desejável.
    """
    if element is None:
        return
    for name, value in props.items():
        try:
            element.set_property(name.replace("_", "-"), value)
        except Exception:
            pass


class VideoDecoder:
    """
    Recebe quadros H.264 pela rede e devolve imagens prontas pra desenhar.

    Já entrega a imagem no tamanho da telinha (o redimensionamento acontece
    dentro do GStreamer). Assim o Python recebe algo pequeno em vez de um
    quadro 1080p inteiro.
    """

    def __init__(self, on_frame, width=640, height=360):
        init()
        # chamado na thread da interface com (bytes RGB, largura, altura, passo)
        self.on_frame = on_frame
        self.width, self.height = width, height
        self._pipeline = None
        self._src = None
        self._started = False

        # Só o quadro MAIS RECENTE fica esperando pra ser desenhado (ver
        # _on_sample). Guardado sob trava porque é escrito pela thread do
        # GStreamer e lido pela thread da interface.
        self._pending_lock = threading.Lock()
        self._pending_frame = None
        self._draw_scheduled = False

    def start(self):
        if self._started:
            return True
        desc = (
            "appsrc name=src is-live=true do-timestamp=true format=time "
            "caps=video/x-h264,stream-format=byte-stream,alignment=au "
            "! h264parse ! avdec_h264 output-corrupt=false "
            "! videoconvert ! videoscale name=scale "
            f"! video/x-raw,format=RGB,width={self.width},height={self.height},pixel-aspect-ratio=1/1 "
            "! appsink name=sink emit-signals=true sync=false max-buffers=2 drop=true"
        )
        try:
            self._pipeline = Gst.parse_launch(desc)
            self._src = self._pipeline.get_by_name("src")
            sink = self._pipeline.get_by_name("sink")
            sink.connect("new-sample", self._on_sample)

            # Se a rede entregar mais rápido do que a máquina decodifica, a
            # fila do appsrc cresce sem parar e o vídeo vai ficando cada vez
            # mais atrasado (efeito "ao vivo com 30 segundos de atraso").
            # Preferimos perder o quadro mais VELHO e continuar em tempo real.
            _try_set(self._src, max_bytes=4 * 1024 * 1024, block=False,
                     leaky_type=2)   # 2 = downstream: descarta o mais antigo

            # Preserva a proporção da tela de quem transmite, com tarjas pretas
            # em vez de esticar a imagem.
            _try_set(self._pipeline.get_by_name("scale"), add_borders=True)

            self._pipeline.set_state(Gst.State.PLAYING)
            self._started = True
            return True
        except Exception:
            self.stop()
            return False

    def feed(self, access_unit: bytes):
        """Entrega um quadro. Nunca bloqueia."""
        if not self._started or not self._src:
            return
        buf = Gst.Buffer.new_allocate(None, len(access_unit), None)
        buf.fill(0, access_unit)
        self._src.emit("push-buffer", buf)

    def _on_sample(self, sink):
        sample = sink.emit("pull-sample")
        if sample is None:
            return Gst.FlowReturn.OK
        buf = sample.get_buffer()
        ok, info = buf.map(Gst.MapFlags.READ)
        if ok:
            try:
                data = bytes(info.data)
                caps = sample.get_caps().get_structure(0)
                w = caps.get_value("width")
                h = caps.get_value("height")
                # O GStreamer alinha cada LINHA da imagem em múltiplos de 4
                # bytes. Com RGB (3 bytes por pixel) isso quase sempre sobra
                # alguns bytes no fim de cada linha; entregar largura*3 como
                # se fosse o passo real deixaria a imagem "escorrida" na
                # diagonal. Deduzimos o passo verdadeiro do tamanho do buffer.
                stride = len(data) // h if h else w * 3
                if stride < w * 3:
                    stride = w * 3

                # Volta pra thread da interface antes de mexer na tela — mas
                # guardando SÓ o quadro mais recente, nunca uma fila deles.
                #
                # Isto não é economia: é o que impede o app de estourar a
                # memória. Um quadro decodificado ocupa ~700 KB e chegam 30
                # por segundo por pessoa assistida. Se a interface travar por
                # alguns segundos (uma janela do sistema demorando, o disco
                # engasgando), enfileirar cada quadro acumularia mais de um
                # gigabyte em segundos — e depois a imagem passaria em
                # câmera-rápida pra "recuperar o atraso". Guardando só o
                # último, o custo é fixo e o vídeo continua no tempo certo.
                with self._pending_lock:
                    self._pending_frame = (data, w, h, stride)
                    schedule = not self._draw_scheduled
                    self._draw_scheduled = True
                if schedule:
                    GLib.idle_add(self._draw_pending)
            finally:
                buf.unmap(info)
        return Gst.FlowReturn.OK

    def _draw_pending(self):
        with self._pending_lock:
            frame = self._pending_frame
            self._pending_frame = None
            self._draw_scheduled = False
        if frame is not None and self._started:
            self.on_frame(*frame)
        return False

    def stop(self):
        self._started = False
        _shutdown_async(self._pipeline)
        self._pipeline = None
        self._src = None


class ScreenEncoder:
    """
    Captura a tela (ou uma janela) e devolve quadros H.264 prontos pra rede.

    A entrada muda conforme o sistema:
      - Wayland: pipewiresrc, alimentado pelo "portal" do desktop (é o
        mecanismo que o Discord e o OBS usam; exige a permissão do usuário
        naquela janelinha do sistema).
      - X11: ximagesrc, que captura direto.

    A saída é sempre igual à do cliente Windows: H.264 Annex-B, sem B-frames,
    uma imagem por quadro e um quadro-chave por segundo. As três opções abaixo
    marcadas como "compatibilidade" existem justamente pra isso e não devem
    ser mexidas sem testar do outro lado.
    """

    def __init__(self, on_encoded, width=1280, height=720, fps=30, bitrate_kbps=2500):
        init()
        self.on_encoded = on_encoded      # chamado com bytes de um quadro H.264
        self.width, self.height, self.fps = width, height, fps
        self.bitrate = bitrate_kbps
        self._pipeline = None
        self._started = False
        from .protocol import AnnexBSplitter
        self._splitter = AnnexBSplitter()

    def _encoder_chain(self):
        return (
            "videoconvert ! videoscale name=scale ! videorate "
            f"! video/x-raw,width={self.width},height={self.height},framerate={self.fps}/1"
            # ---- pixel quadrado: NÃO tire este pedaço ----
            # Sem fixar isto, o videoscale preserva a proporção da tela de um
            # jeito traiçoeiro: em vez de colocar tarjas pretas, ele declara
            # que cada pixel é RETANGULAR e escreve isso dentro do H.264. O
            # cliente Windows decodifica com o ffmpeg pra BMP, e BMP não
            # guarda essa informação — então ela se perde e a imagem aparece
            # esticada do outro lado. Medido: uma tela 4:3 chegava 33% mais
            # larga no Windows. Com o pixel fixado em quadrado, o add-borders
            # abaixo faz o certo (tarjas) e todo mundo vê a mesma coisa.
            ",pixel-aspect-ratio=1/1 "
            "! x264enc tune=zerolatency speed-preset=veryfast "
            f"bitrate={self.bitrate} key-int-max={self.fps} "
            # --- compatibilidade com o cliente Windows ---
            "bframes=0 "            # sem quadros bidirecionais
            "sliced-threads=false " # UMA imagem por quadro (senão o Windows corta errado)
            "! h264parse config-interval=-1 "   # repete os parâmetros a cada quadro-chave
            "! video/x-h264,stream-format=byte-stream,alignment=au "
            "! appsink name=sink emit-signals=true sync=false max-buffers=4 drop=true"
        )

    def start_pipewire(self, fd: int, node_id: int):
        """Wayland: recebe a captura já autorizada pelo portal do desktop."""
        src = f"pipewiresrc fd={fd} path={node_id} do-timestamp=true keepalive-time=1000"
        return self._start(f"{src} ! {self._encoder_chain()}")

    def start_x11(self, xid: int = None):
        """X11: captura direto (a tela toda, ou uma janela pelo identificador)."""
        src = "ximagesrc use-damage=false show-pointer=true"
        if xid:
            src += f" xid={xid}"
        return self._start(f"{src} ! {self._encoder_chain()}")

    def _start(self, desc):
        try:
            self._pipeline = Gst.parse_launch(desc)
            sink = self._pipeline.get_by_name("sink")
            sink.connect("new-sample", self._on_sample)

            # Tela de 21:9, monitor girado, janela estreita: sem isto a
            # imagem chegaria esticada do outro lado. Com tarjas, a proporção
            # é preservada.
            _try_set(self._pipeline.get_by_name("scale"), add_borders=True)

            # Se o pipeline não conseguir sair do PAUSED (fonte inválida,
            # permissão negada, monitor que sumiu), é melhor descobrir aqui do
            # que ficar com um botão "TRANSMITINDO" que nunca manda nada.
            self._pipeline.set_state(Gst.State.PLAYING)
            state = self._pipeline.get_state(3 * Gst.SECOND)
            if state[0] == Gst.StateChangeReturn.FAILURE:
                self.stop()
                return False

            self._started = True
            return True
        except Exception:
            self.stop()
            return False

    def _on_sample(self, sink):
        sample = sink.emit("pull-sample")
        if sample is None:
            return Gst.FlowReturn.OK
        buf = sample.get_buffer()
        ok, info = buf.map(Gst.MapFlags.READ)
        if ok:
            try:
                # O GStreamer já entrega alinhado por quadro, mas passamos pelo
                # mesmo recortador do cliente Windows pra garantir que cada
                # mensagem carregue exatamente um quadro autossuficiente.
                for au in self._splitter.feed(bytes(info.data)):
                    self.on_encoded(au)
            finally:
                buf.unmap(info)
        return Gst.FlowReturn.OK

    def stop(self):
        self._started = False
        _shutdown_async(self._pipeline)
        self._pipeline = None
        self._splitter.reset()

    @property
    def running(self):
        return self._started


def find_monitor_source():
    """
    Descobre o "monitor" da saída de áudio — a fonte que entrega o som que
    está SAINDO pelas caixas/fone.

    Isso não é um detalhe: se a gente simplesmente pedir "a fonte padrão", o
    PulseAudio/PipeWire entrega o MICROFONE. Seus amigos ouviriam você
    respirando em vez do jogo — e, pior, você estaria transmitindo o microfone
    sem ter pedido isso em nenhum momento. Então: ou achamos o monitor certo,
    ou o app transmite sem áudio e avisa.
    """
    init()

    # 1) Pelo próprio GStreamer, que já enxerga os dispositivos do sistema.
    try:
        monitor = Gst.DeviceMonitor.new()
        monitor.add_filter("Audio/Source", None)
        monitor.start()
        try:
            devices = monitor.get_devices() or []
        finally:
            monitor.stop()

        for device in devices:
            props = device.get_properties()
            if props is None:
                continue
            name = (props.get_string("device.name")
                    or props.get_string("node.name") or "")
            klass = props.get_string("device.class") or ""
            if klass == "monitor" or name.endswith(".monitor"):
                return name or None
    except Exception:
        pass

    # 2) Reserva: pergunta ao PulseAudio/PipeWire qual é a saída padrão e usa
    #    o monitor dela.
    try:
        import subprocess
        sink = subprocess.run(["pactl", "get-default-sink"], capture_output=True,
                              text=True, timeout=3).stdout.strip()
        if sink:
            return sink + ".monitor"
    except Exception:
        pass

    return None


class AudioCapture:
    """
    Captura o som que está saindo do computador, já comprimido em μ-law —
    exatamente o formato que trafega entre os clientes.

    No Linux isso é mais simples que no Windows: o "monitor" da saída de áudio
    é uma fonte de gravação comum, tanto no PulseAudio quanto no PipeWire.
    """

    def __init__(self, on_chunk, device=None):
        init()
        self.on_chunk = on_chunk
        self.device = device
        self.device_used = None
        self._pipeline = None
        self._started = False

    def start(self):
        device = self.device or find_monitor_source()
        if not device:
            # Sem monitor identificado, preferimos ficar mudos a transmitir o
            # microfone por engano.
            return False
        self.device_used = device

        desc = (
            "pulsesrc name=src ! audioconvert ! audioresample "
            f"! audio/x-raw,rate={AUDIO_RATE},channels={AUDIO_CHANNELS},format=S16LE "
            "! mulawenc ! appsink name=sink emit-signals=true sync=false "
            "max-buffers=8 drop=true"
        )
        try:
            self._pipeline = Gst.parse_launch(desc)
            src = self._pipeline.get_by_name("src")
            # O nome do dispositivo vai por propriedade, não pela descrição do
            # pipeline: nomes de monitor têm pontos e podem ter caracteres que
            # confundem o interpretador de texto do GStreamer.
            src.set_property("device", device)
            # Pedaços de ~20ms: latência baixa o suficiente pra conversa e
            # ainda longe de virar uma enxurrada de mensagens. O padrão do
            # pulsesrc (200ms) deixaria o áudio nitidamente atrasado do vídeo.
            _try_set(src, latency_time=20000, buffer_time=200000)

            sink = self._pipeline.get_by_name("sink")
            sink.connect("new-sample", self._on_sample)

            self._pipeline.set_state(Gst.State.PLAYING)
            state = self._pipeline.get_state(3 * Gst.SECOND)
            if state[0] == Gst.StateChangeReturn.FAILURE:
                self.stop()
                return False

            self._started = True
            return True
        except Exception:
            self.stop()
            return False

    def _on_sample(self, sink):
        sample = sink.emit("pull-sample")
        if sample is None:
            return Gst.FlowReturn.OK
        buf = sample.get_buffer()
        ok, info = buf.map(Gst.MapFlags.READ)
        if ok:
            try:
                self.on_chunk(bytes(info.data))
            finally:
                buf.unmap(info)
        return Gst.FlowReturn.OK

    def stop(self):
        self._started = False
        _shutdown_async(self._pipeline)
        self._pipeline = None


class AudioPlayer:
    """
    Toca o áudio de UM participante, com volume próprio (0 a 150%, igual ao
    cliente Windows). Uma instância por pessoa que você está assistindo.
    """

    def __init__(self):
        init()
        self._pipeline = None
        self._src = None
        self._volume = None
        self._started = False
        self._muted = False
        self._percent = 100.0

    def start(self):
        if self._started:
            return True
        desc = (
            "appsrc name=src is-live=true do-timestamp=true format=time "
            f"caps=audio/x-mulaw,rate={AUDIO_RATE},channels={AUDIO_CHANNELS} "
            "! mulawdec ! audioconvert ! audioresample "
            "! volume name=vol ! autoaudiosink sync=false"
        )
        try:
            self._pipeline = Gst.parse_launch(desc)
            self._src = self._pipeline.get_by_name("src")
            self._volume = self._pipeline.get_by_name("vol")

            # Mesmo cuidado do vídeo: se chegar mais áudio do que a placa de
            # som consome, a fila cresce e a pessoa passa a ser ouvida cada
            # vez mais atrasada. Melhor perder um pedacinho e continuar em
            # tempo real do que acumular meio minuto de atraso.
            _try_set(self._src, max_bytes=96 * 1024, block=False, leaky_type=2)

            self._pipeline.set_state(Gst.State.PLAYING)
            self._started = True
            self._apply()
            return True
        except Exception:
            self.stop()
            return False

    def feed(self, mulaw: bytes):
        if not self._started or not self._src or not mulaw:
            return
        buf = Gst.Buffer.new_allocate(None, len(mulaw), None)
        buf.fill(0, mulaw)
        self._src.emit("push-buffer", buf)

    def set_volume_percent(self, percent: float):
        self._percent = max(0.0, min(150.0, percent))
        self._apply()

    def set_muted(self, muted: bool):
        self._muted = muted
        self._apply()

    def _apply(self):
        if self._volume:
            try:
                self._volume.set_property("volume", 0.0 if self._muted else self._percent / 100.0)
            except Exception:
                pass

    def stop(self):
        self._started = False
        _shutdown_async(self._pipeline)
        self._pipeline = None
        self._src = None
        self._volume = None
