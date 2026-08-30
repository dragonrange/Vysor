using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace VysorClient.Services;

// Codifica vídeo em H.264 usando o encoder de hardware da GPU (NVENC, Quick
// Sync ou AMF, nessa ordem de tentativa) por meio de um processo externo do
// ffmpeg.exe empacotado do lado do Vysor.exe. Qualquer falha (sem
// ffmpeg.exe, sem GPU compatível, processo que não sobe) faz os métodos
// Start* retornarem false — este serviço nunca lança exceção pra fora, e
// quem chama (MainWindow) cai de volta pro pipeline JPEG/GDI de sempre.
//
// Dois modos, escolhidos por quem chama:
//  - StartFullScreenHardware: só tela cheia, usa o filtro "ddagrab" (Desktop
//    Duplication API) pra captura 100% na GPU, sem nunca copiar pixel pra
//    memória do processo.
//  - StartRawPipeHardware: tela cheia OU janela, mas reaproveita a captura
//    GDI que o app já usa hoje (CopyFromScreen/PrintWindow) — só a
//    compressão em H.264 roda na GPU. Frames crus BGRA entram via Feed().
//
// REGRA IMPORTANTE deste arquivo: Feed() NUNCA bloqueia quem chama. Escrever
// direto no stdin do ffmpeg bloqueia quando o processo para de consumir, e
// como Feed() é chamado do laço de captura (e, do lado do decoder, já foi
// chamado até da thread da interface), isso travava o app inteiro. Por isso
// todo o tráfego passa por uma fila limitada consumida por uma thread
// dedicada: se o encoder não está dando conta, a gente DESCARTA frames
// antigos em vez de segurar quem produz.
public class VideoEncodeService
{
    // Access units H.264 já remontadas (SPS+PPS+IDR juntos quando aparecem,
    // ou só um NAL de slice) prontas pra mandar pela rede via
    // PeerMedia.SendVideo (com o prefixo 0x01 adicionado por quem chama,
    // não por este serviço).
    //
    // ATENÇÃO pra quem for mexer: este evento é disparado da thread que lê o
    // stdout do ffmpeg. O que estiver inscrito aqui NÃO pode bloquear (nem
    // esperar a thread da interface), senão o stdout deixa de ser drenado, o
    // ffmpeg trava tentando escrever, e a transmissão inteira congela.
    public event Action<byte[]>? OnEncodedFrame;

    // Descrição de qual encoder conseguiu subir, só pra diagnóstico/log —
    // por exemplo "NVENC (captura por GPU - ddagrab, tela cheia)".
    public string? ActiveEncoderDescription { get; private set; }

    // true quando o próprio ffmpeg está capturando a tela sozinho (Nível 1,
    // ddagrab) — nesse caso quem chama NÃO deve rodar um laço de captura
    // próprio nem chamar Feed(). false no Nível 2 (StartRawPipeHardware),
    // onde quem chama precisa continuar capturando frame a frame e
    // alimentando via Feed().
    public bool IsSelfDriving { get; private set; }

    private readonly object _lifecycleLock = new();
    private Process? _process;
    private Thread? _stdoutThread;
    private Thread? _stderrThread;
    private Thread? _writerThread;
    private volatile bool _running;

    // Marcado quando Stop() é chamado. Sem isso havia uma corrida real: subir
    // o ffmpeg leva algumas centenas de milissegundos e roda numa thread de
    // fundo; se o usuário parasse a transmissão nesse meio-tempo, o Stop()
    // não encontrava processo nenhum pra matar e, logo depois, o processo
    // subia e ficava rodando pra sempre, sem ninguém segurando referência
    // pra encerrá-lo.
    private volatile bool _stopRequested;

    // Fila de frames crus esperando pra entrar no ffmpeg. Limitada de
    // propósito: vídeo é descartável, e é melhor perder um frame do que
    // travar a captura.
    //
    // Capacidade 3 (~50ms a 60fps) era curta demais: qualquer engasgo
    // momentâneo do encoder — a GPU ocupada com o próprio jogo sendo
    // compartilhado, por exemplo — já estourava a fila e derrubava frame na
    // hora. 10 frames (~166ms a 60fps) absorve esses picos sem acumular
    // atraso perceptível numa transmissão ao vivo.
    private readonly BlockingCollection<(byte[] Buffer, int Length)> _pendingFrames =
        new(new ConcurrentQueue<(byte[], int)>(), boundedCapacity: 10);

    // --- Nível 1: tela cheia, 100% GPU (ddagrab + NVENC) --------------------

    // monitorIndex é a posição do monitor em Screen.AllScreens. O índice
    // interno do filtro ddagrab não é documentado como garantidamente igual
    // a essa ordem — se a captura vier do monitor errado, é o primeiro
    // lugar a ajustar (ver VIDEO_GPU_NOTES.md).
    public bool StartFullScreenHardware(int monitorIndex, int fps, int outputWidth, int outputHeight)
    {
        if (!FfmpegLocator.IsAvailable) return false;

        string args =
            "-hide_banner -loglevel warning " +
            "-init_hw_device d3d11va " +
            $"-filter_complex \"ddagrab=output_idx={monitorIndex}:framerate={fps}\" " +
            $"-c:v h264_nvenc -preset {NvencPreset} -tune ull -rc cbr -zerolatency 1 {BitrateFlags(outputWidth, outputHeight, fps)} " +
            $"-bf 0 -g {KeyframeInterval(fps)} -f h264 pipe:1";

        var psi = BuildPsi(args, redirectStdin: false);
        if (!TryLaunch(psi, out var proc) || proc == null) return false;

        lock (_lifecycleLock)
        {
            if (_stopRequested)
            {
                KillQuietly(proc);
                return false;
            }

            _process = proc;
            _running = true;
            IsSelfDriving = true;
            ActiveEncoderDescription = "NVENC (captura por GPU - ddagrab, tela cheia)";
            StartReaderThreads(proc, withWriter: false);
        }
        return true;
    }

    // --- Nível 2: tela cheia OU janela, captura de hoje + encode por GPU ---

    // Espera receber frames BGRA já capturados pelo código de captura
    // existente (mesmo layout de bytes de um Bitmap Format32bppArgb) via
    // Feed(). Tenta NVENC, depois Quick Sync, depois AMF.
    //
    // outputWidth/outputHeight permitem transmitir menor que a tela
    // capturada (é assim que a escolha 720p/1080p do modal passa a valer de
    // verdade também no caminho por hardware — antes ela era ignorada e a
    // gente mandava sempre na resolução nativa do monitor).
    public bool StartRawPipeHardware(int captureWidth, int captureHeight, int outputWidth, int outputHeight, int fps)
    {
        if (!FfmpegLocator.IsAvailable) return false;

        string scaleFilter = (outputWidth != captureWidth || outputHeight != captureHeight)
            ? $"-vf scale={outputWidth}:{outputHeight} "
            : string.Empty;

        string rate = BitrateFlags(outputWidth, outputHeight, fps);

        (string encoder, string flags, string label)[] attempts =
        {
            ("h264_nvenc", $"-preset {NvencPreset} -tune ull -rc cbr -zerolatency 1 {rate}", "NVENC"),
            ("h264_qsv", $"-preset faster -low_delay_brc 1 -look_ahead 0 {rate}", "Quick Sync"),
            ("h264_amf", $"-usage ultralowlatency -rc cbr -quality balanced {rate}", "AMF"),
        };

        foreach (var (encoder, flags, label) in attempts)
        {
            if (_stopRequested) return false;

            string args =
                "-hide_banner -loglevel warning " +
                $"-f rawvideo -pix_fmt bgra -s {captureWidth}x{captureHeight} -framerate {fps} -i pipe:0 " +
                scaleFilter +
                $"-c:v {encoder} {flags} -bf 0 -g {KeyframeInterval(fps)} -pix_fmt yuv420p " +
                "-f h264 pipe:1";

            var psi = BuildPsi(args, redirectStdin: true);
            if (TryLaunch(psi, out var proc) && proc != null)
            {
                lock (_lifecycleLock)
                {
                    if (_stopRequested)
                    {
                        KillQuietly(proc);
                        return false;
                    }

                    _process = proc;
                    _running = true;
                    IsSelfDriving = false;
                    ActiveEncoderDescription = $"{label} (encode por GPU, captura normal)";
                    StartReaderThreads(proc, withWriter: true);
                }
                return true;
            }
        }

        return false;
    }

    // Calcula a taxa de bits (a "qualidade" do vídeo) a partir da resolução e
    // dos quadros por segundo.
    //
    // ISSO ESTAVA FALTANDO e era um bug sério: as opções pediam "-rc cbr"
    // (taxa constante) sem NUNCA dizer qual taxa. Sem esse valor, o ffmpeg usa
    // o padrão dele, que é ridiculamente baixo (200 kbps) pra vídeo de tela —
    // e, pior, esse mesmo orçamento minúsculo era dividido entre o dobro de
    // quadros a 60fps, deixando a imagem visivelmente pior do que a 30fps.
    //
    // A conta é ~0,07 bit por pixel por segundo, que é uma faixa confortável
    // pra conteúdo de tela (que tem muita área parada e compacta bem, mas
    // precisa de nitidez pra texto ficar legível). Fica limitada entre 1,5 e
    // 10 Mbps pra não sufocar quem tem upload modesto.
    private static string BitrateFlags(int width, int height, int fps)
    {
        double pixelsPerSecond = (double)width * height * Math.Max(1, fps);
        int kbps = (int)(pixelsPerSecond * BitsPerPixel / 1000.0);
        kbps = Math.Clamp(kbps, 2500, 20000);

        // O "bufsize" controla quanto a taxa pode oscilar antes de ser
        // corrigida.
        //
        // Era um quarto de segundo, e isso é apertado demais pra jogo. Numa
        // cena parada o codificador não usa o orçamento inteiro; numa explosão
        // ele precisaria de bem mais que a média por um instante. Com a janela
        // curta ele é obrigado a devolver a conta quase quadro a quadro, então
        // o pico é cortado na hora e é justamente aí que a imagem some em
        // borrão. Meio segundo deixa ele guardar o que sobrou da cena parada e
        // gastar no momento difícil, que é exatamente o comportamento que se
        // quer. Continua curto o bastante pra não criar atraso perceptível.
        int bufsize = Math.Max(1000, kbps / 2);

        return $"-b:v {kbps}k -maxrate {kbps}k -bufsize {bufsize}k";
    }

    // Bits por pixel por segundo — o "quanto de detalhe cabe" da transmissão.
    //
    // Era 0,07, valor pensado pra tela de trabalho: janela parada, texto, muita
    // área que não muda de um quadro pro outro. Isso comprime muito bem e 0,07
    // basta com folga.
    //
    // JOGO É OUTRO CONTEÚDO. Em Warframe (ou qualquer coisa frenética) a tela
    // inteira muda todo quadro, com partículas, fogo e brilho — que é o pior
    // caso possível pra compressão, porque quase nada pode ser reaproveitado do
    // quadro anterior. Com 0,07 o resultado a 1080p60 eram ~8,7 Mbps, que dá
    // conta de uma planilha e não dá conta de uma explosão: o codificador
    // cumpre a meta do jeito que ele sabe, borrando o detalhe.
    //
    // 0,12 põe 1080p60 em ~15 Mbps, que é a faixa que as plataformas de
    // transmissão de jogo recomendam pra esse formato. Como é P2P, essa banda
    // sai direto de uma casa pra outra, sem custo de servidor.
    private const double BitsPerPixel = 0.12;

    // Esforço do codificador da Nvidia. É a melhoria que NÃO custa banda: com
    // a mesma taxa de bits, um preset mais caprichoso procura melhor o que
    // reaproveitar entre um quadro e outro e devolve imagem visivelmente mais
    // limpa — de novo, ganho maior justamente em cena com muito movimento.
    //
    // Estava em "p1", que é o mais apressado dos sete. Ele existe pra caso de
    // latência extrema (jogar remotamente, por exemplo), não pra assistir
    // alguém jogar, e a diferença de tempo entre p1 e p4 num 1080p60 é de
    // poucos milissegundos numa placa moderna — muito abaixo dos 16ms que cada
    // quadro tem. "p4" é o meio-termo, o mesmo que o OBS usa por padrão.
    //
    // Se em alguma placa antiga isto ficar pesado, é a primeira coisa a voltar
    // atrás: o encoder atrasado descarta quadro (ver a fila em Feed) e o
    // sintoma seria imagem limpa porém com menos quadros por segundo.
    private const string NvencPreset = "p4";

    // Intervalo entre quadros-chave.
    //
    // Um quadro-chave não é só "o ponto em que quem chega no meio da
    // transmissão consegue entrar" — é também o ÚNICO ponto de recuperação
    // depois de perder um pacote. Com "-bf 0" (sem quadros B), a decodificação
    // é uma corrente: P depende do quadro anterior, que depende do anterior,
    // até o último quadro-chave. Perder UM pacote de UM quadro no meio da
    // corrente corrompe a imagem até o próximo quadro-chave chegar — com um
    // quadro-chave por segundo, isso podia significar até 1 segundo inteiro de
    // imagem quebrada por causa de uma perda pontual.
    //
    // A boa notícia: com "-rc cbr", o total de bits por segundo é fixo pelo
    // "-maxrate" (ver BitrateFlags) — mais quadros-chave não aumenta a banda
    // usada, só REDISTRIBUI o mesmo orçamento (um pouco menos nítido quadro a
    // quadro, pra sobrar mais pros quadros-chave). Ou seja: dá pra encurtar
    // bastante esse intervalo de graça. Um quarto de segundo entre
    // quadros-chave limita qualquer perda a, no máximo, ~250ms de imagem
    // ruim, em vez de até 1 segundo inteiro.
    private static int KeyframeInterval(int fps) => Math.Max(2, fps / 4);

    // Só válido depois de StartRawPipeHardware ter retornado true. Nunca
    // bloqueia: se a fila estiver cheia (encoder não está dando conta), o
    // frame mais antigo que ainda não entrou é descartado e o novo entra no
    // lugar.
    //
    // "buffer" TEM que vir de ArrayPool<byte>.Shared.Rent (ver
    // MainWindow.BitmapToBgraBytes) — este método assume posse dele e o
    // devolve ao pool sozinho (seja depois de mandar pro ffmpeg, seja ao
    // descartá-lo por fila cheia). Alocar um array novo a cada quadro — o
    // que fazíamos antes — vira ~500 MB/s de lixo a 1080p60, e as pausas do
    // coletor de lixo do .NET pra limpar isso apareciam na tela como
    // engasgada, sem nenhuma relação com rede.
    // Quantos quadros a CAPTURA produziu e o codificador não deu conta de
    // engolir. É o número que diz "o problema começa aqui, no remetente" —
    // sem ele, um encoder que não vence a carga (GPU quente no meio de um jogo
    // pesado, por exemplo) é indistinguível de perda de rede do outro lado.
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    private long _droppedFrames;

    // Quantos quadros saíram codificados de verdade. Comparado com o tempo,
    // dá os quadros por segundo REAIS — que é o que importa, não o que foi
    // pedido no modal.
    public long EncodedFrames => Interlocked.Read(ref _encodedFrames);
    private long _encodedFrames;

    public void Feed(byte[] buffer, int length)
    {
        if (!_running)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            return;
        }

        try
        {
            if (!_pendingFrames.TryAdd((buffer, length)))
            {
                if (_pendingFrames.TryTake(out var dropped))
                    System.Buffers.ArrayPool<byte>.Shared.Return(dropped.Buffer);
                Interlocked.Increment(ref _droppedFrames);
                _pendingFrames.TryAdd((buffer, length));
            }
        }
        catch
        {
            // Fila já encerrada (Stop em andamento).
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public bool IsRunning => _running;

    public void Stop()
    {
        Process? proc;

        lock (_lifecycleLock)
        {
            _stopRequested = true;
            _running = false;
            proc = _process;
            _process = null;
        }

        try { _pendingFrames.CompleteAdding(); } catch { }

        if (proc != null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    if (proc.StartInfo.RedirectStandardInput)
                    {
                        try { proc.StandardInput.Close(); } catch { }
                    }

                    if (!proc.WaitForExit(400))
                    {
                        try { proc.Kill(true); } catch { }
                    }
                }
            }
            catch { }
        }

        // Espera as threads saírem antes de descartar o processo, pra elas
        // não ficarem mexendo em streams já liberados.
        JoinQuietly(_writerThread);
        JoinQuietly(_stdoutThread);
        JoinQuietly(_stderrThread);
        _writerThread = _stdoutThread = _stderrThread = null;

        if (proc != null)
        {
            try { proc.Dispose(); } catch { }
        }

        // Devolve a memória dos buffers em vez de só zerar o comprimento —
        // um buffer que cresceu por causa de um pico ficava reservado pra
        // sempre.
        _buf = new byte[1 << 16];
        _bufLen = 0;
        _nals.Clear();
        _nals.TrimExcess();
        _pendingAuStart = null;
        _scanPos = 0;
    }

    private static void JoinQuietly(Thread? thread)
    {
        try { thread?.Join(300); } catch { }
    }

    private static void KillQuietly(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(true); } catch { }
        try { proc.Dispose(); } catch { }
    }

    // --- Infra comum de processo -----------------------------------------

    private static ProcessStartInfo BuildPsi(string args, bool redirectStdin)
    {
        return new ProcessStartInfo
        {
            FileName = FfmpegLocator.GetFfmpegPath(),
            Arguments = args,
            RedirectStandardInput = redirectStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    // Sobe o processo e espera um tempo curto pra ver se ele morre logo de
    // cara (sinal de que o encoder escolhido não iniciou — por exemplo, sem
    // GPU Nvidia pra NVENC). Não existe uma forma 100% confiável de saber
    // "o encoder funcionou" sem isso; é uma heurística por tempo, no mesmo
    // espírito do resto do app (áudio já usa o padrão "tenta, senão cai pro
    // próximo").
    private static bool TryLaunch(ProcessStartInfo psi, out Process? process)
    {
        process = null;
        Process? proc = null;
        try
        {
            proc = new Process { StartInfo = psi };
            if (!proc.Start())
            {
                proc.Dispose();
                return false;
            }

            // Amarra ao tempo de vida do Vysor: se o app morrer de forma
            // anormal, o Windows mata este ffmpeg junto.
            ChildProcessTracker.Track(proc);

            if (proc.WaitForExit(700))
            {
                proc.Dispose();
                return false;
            }

            process = proc;
            return true;
        }
        catch
        {
            if (proc != null) KillQuietly(proc);
            return false;
        }
    }

    private void StartReaderThreads(Process proc, bool withWriter)
    {
        _stderrThread = new Thread(() => DrainStderr(proc))
        {
            IsBackground = true,
            Name = "VysorVideoEncodeStderr"
        };
        _stderrThread.Start();

        _stdoutThread = new Thread(() => ReadStdout(proc))
        {
            IsBackground = true,
            Name = "VysorVideoEncodeStdout"
        };
        _stdoutThread.Start();

        if (withWriter)
        {
            _writerThread = new Thread(() => WriteLoop(proc))
            {
                IsBackground = true,
                Name = "VysorVideoEncodeStdin"
            };
            _writerThread.Start();
        }
    }

    // Única thread que escreve no stdin do ffmpeg. Bloquear aqui é seguro —
    // ninguém mais depende dela pra continuar.
    private void WriteLoop(Process proc)
    {
        try
        {
            var stdin = proc.StandardInput.BaseStream;
            foreach (var (buffer, length) in _pendingFrames.GetConsumingEnumerable())
            {
                try
                {
                    if (_running)
                    {
                        stdin.Write(buffer, 0, length);
                        stdin.Flush();
                    }
                }
                finally
                {
                    // Sempre devolve, escreveu ou não — é o que fecha o
                    // ciclo do buffer emprestado por BitmapToBgraBytes.
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }

                if (!_running) break;
            }
        }
        catch
        {
            _running = false;
        }
    }

    // Só drena o stderr pra não travar o pipe (o ffmpeg escreve logs lá o
    // tempo todo, e se ninguém ler, o buffer do SO enche e o processo
    // trava). Não usamos mais o conteúdo depois que TryLaunch já checou que
    // o processo sobreviveu ao período inicial.
    private void DrainStderr(Process proc)
    {
        try
        {
            var buffer = new char[4096];
            while (true)
            {
                int read = proc.StandardError.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
            }
        }
        catch { }
    }

    // --- Parsing do stream Annex-B em "access units" ----------------------
    //
    // O ffmpeg manda pelo stdout um stream H.264 cru (Annex-B), sem nenhuma
    // marcação de "aqui acaba um frame" — só uma sequência contínua de NAL
    // units, cada uma começando com um start code (00 00 01 ou 00 00 00 01)
    // seguido de um byte de cabeçalho cujos 5 bits baixos dizem o tipo do
    // NAL. Como o encoder é configurado sem B-frames e uma única slice por
    // frame (-bf 0), cada frame vira exatamente 1 NAL de slice (tipo 1 ou
    // 5), às vezes precedido de SPS(7)/PPS(8) (antes de cada IDR). A lógica
    // abaixo agrupa esse SPS/PPS com a slice que vem logo depois, formando
    // uma "access unit" por frame, e dispara OnEncodedFrame só quando a
    // unidade está completa (ou seja, um frame atrás do que está chegando).

    private byte[] _buf = new byte[1 << 16];
    private int _bufLen;
    private readonly List<(int Offset, int Type)> _nals = new();
    private int? _pendingAuStart;
    private int _scanPos;
    private const int MaxBufferBytes = 16 * 1024 * 1024; // trava de segurança

    private void ReadStdout(Process proc)
    {
        var stream = proc.StandardOutput.BaseStream;
        byte[] chunk = new byte[65536];

        try
        {
            while (_running)
            {
                int read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;

                AppendToBuffer(chunk, read);
                ScanForAccessUnits();

                if (_bufLen > MaxBufferBytes)
                {
                    // Stream sem start codes reconhecíveis por tempo demais —
                    // melhor descartar e recomeçar do que crescer sem limite.
                    _buf = new byte[1 << 16];
                    _bufLen = 0;
                    _nals.Clear();
                    _pendingAuStart = null;
                    _scanPos = 0;
                }
            }
        }
        catch { }
        finally
        {
            _running = false;
        }
    }

    private void AppendToBuffer(byte[] chunk, int count)
    {
        if (_bufLen + count > _buf.Length)
        {
            int newSize = Math.Max(_buf.Length * 2, _bufLen + count);
            Array.Resize(ref _buf, newSize);
        }
        Buffer.BlockCopy(chunk, 0, _buf, _bufLen, count);
        _bufLen += count;
    }

    private void ScanForAccessUnits()
    {
        while (true)
        {
            int startCode = FindStartCode(_buf, _scanPos, _bufLen, out int codeLen);
            if (startCode < 0) break;

            int headerPos = startCode + codeLen;
            if (headerPos >= _bufLen) break; // start code no fim do buffer — espera mais dados

            int nalType = _buf[headerPos] & 0x1F;
            _nals.Add((startCode, nalType));
            _scanPos = headerPos; // não rescaneia dentro do mesmo start code

            if (nalType == 1 || nalType == 5)
            {
                // Anda pra trás recolhendo tudo que PERTENCE a este quadro:
                // AUD(9), SPS(7), PPS(8) e SEI(6).
                //
                // Antes só recolhia SPS e PPS, e isso tinha uma consequência
                // séria: os codificadores costumam inserir um SEI entre o PPS
                // e o quadro-chave. A busca parava nesse SEI e deixava o
                // SPS/PPS no pacote ANTERIOR — ou seja, o pacote do quadro-chave
                // ia sem os parâmetros que descrevem o vídeo. Funcionava só
                // porque os pacotes chegavam em sequência e o decodificador
                // remontava tudo; mas se o pacote anterior fosse descartado
                // (o servidor descarta quadros quando alguém está lento), o
                // quadro-chave chegava indecifrável e a telinha da pessoa
                // ficava preta sem nunca se recuperar.
                int boundary = startCode;
                int j = _nals.Count - 2;
                while (j >= 0 && IsLeadingNal(_nals[j].Type) &&
                       (!_pendingAuStart.HasValue || _nals[j].Offset >= _pendingAuStart.Value))
                {
                    boundary = _nals[j].Offset;
                    j--;
                }

                if (_pendingAuStart.HasValue && boundary > _pendingAuStart.Value)
                {
                    // EmitAccessUnit chama CompactBuffer, que desloca todo o
                    // buffer (e os offsets em _nals) por _pendingAuStart.Value
                    // bytes pra esquerda. "boundary" foi calculado ANTES
                    // dessa compactação, então depois de compactar ele não
                    // aponta mais pro lugar certo — precisa subtrair a mesma
                    // quantidade pra continuar válido no buffer já compactado.
                    // (Sem esse ajuste, cada frame ficava um pouco mais
                    // "atrasado" que o anterior — foi a causa da prévia saindo
                    // corrompida/com rastro fantasma no primeiro teste real.)
                    int compactedBy = _pendingAuStart.Value;
                    EmitAccessUnit(_pendingAuStart.Value, boundary);
                    boundary -= compactedBy;
                }

                _pendingAuStart = boundary;
            }
        }
    }

    private void EmitAccessUnit(int from, int to)
    {
        int len = to - from;
        if (len <= 0) return;

        byte[] au = new byte[len];
        Buffer.BlockCopy(_buf, from, au, 0, len);

        Interlocked.Increment(ref _encodedFrames);
        try { OnEncodedFrame?.Invoke(au); } catch { }

        CompactBuffer(from);
    }

    // Descarta tudo antes de "upTo" (já emitido) e reajusta os índices
    // guardados, pra não deixar o buffer crescer pra sempre.
    private void CompactBuffer(int upTo)
    {
        if (upTo <= 0) return;

        int remaining = _bufLen - upTo;
        if (remaining > 0)
        {
            Buffer.BlockCopy(_buf, upTo, _buf, 0, remaining);
        }
        _bufLen = remaining;
        _scanPos = Math.Max(0, _scanPos - upTo);

        for (int i = 0; i < _nals.Count; i++)
        {
            _nals[i] = (_nals[i].Offset - upTo, _nals[i].Type);
        }
        _nals.RemoveAll(n => n.Offset < 0);

        if (_pendingAuStart.HasValue)
        {
            _pendingAuStart = _pendingAuStart.Value - upTo;
        }
    }

    // NALs que vêm ANTES das fatias de imagem e fazem parte do mesmo quadro:
    // delimitador (9), SPS (7), PPS (8) e informação suplementar (6).
    private static bool IsLeadingNal(int nalType)
        => nalType == 6 || nalType == 7 || nalType == 8 || nalType == 9;

    private static int FindStartCode(byte[] buf, int from, int len, out int codeLen)
    {
        codeLen = 0;
        for (int i = from; i + 3 <= len; i++)
        {
            if (buf[i] == 0 && buf[i + 1] == 0)
            {
                if (i + 3 < len && buf[i + 2] == 0 && buf[i + 3] == 1)
                {
                    codeLen = 4;
                    return i;
                }
                if (buf[i + 2] == 1)
                {
                    codeLen = 3;
                    return i;
                }
            }
        }
        return -1;
    }
}
