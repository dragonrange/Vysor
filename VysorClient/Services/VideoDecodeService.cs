using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace VysorClient.Services;

// Decodifica um stream H.264 recebido pela rede (gerado do outro lado por
// VideoEncodeService) de volta pra imagens, usando o ffmpeg.exe empacotado.
// Cada frame decodificado sai pelo evento OnFrameDecoded já como um BMP
// completo — que dá pra jogar direto no BytesToBitmapImage que o app já usa
// hoje pra JPEG, sem precisar mudar nada no caminho de exibição.
//
// Uma instância por tile assistido (inclusive a prévia da própria
// transmissão), criada/derrubada nos mesmos pontos centralizados que já
// existem (AddWatchTile/RemoveWatchTile em MainWindow).
//
// REGRA IMPORTANTE deste arquivo: Feed() NUNCA bloqueia quem chama, e
// OnFrameDecoded NUNCA deve ser tratado de forma bloqueante. Antes, Feed()
// escrevia direto no stdin do ffmpeg e era chamado da thread da interface,
// enquanto a thread que lia o stdout esperava a interface pra entregar cada
// quadro — as duas ficavam se esperando e o app inteiro travava de vez
// (tinha que matar pelo Gerenciador de Tarefas). Agora tudo passa por uma
// fila limitada com thread própria.
public class VideoDecodeService
{
    // Disparado da thread que lê o stdout do ffmpeg. Quem se inscrever aqui
    // não pode bloquear esperando a interface (use InvokeAsync, não Invoke).
    public event Action<byte[]>? OnFrameDecoded;

    private readonly object _lifecycleLock = new();
    private Process? _process;
    private volatile bool _running;
    private volatile bool _stopRequested;
    private Thread? _stdoutThread;
    private Thread? _stderrThread;
    private Thread? _writerThread;

    // Access units esperando pra entrar no decodificador. Limitada: se o
    // decodificador não está dando conta, é melhor descartar quadros antigos
    // (o vídeo "pula") do que segurar quem produz.
    private readonly BlockingCollection<byte[]> _pendingUnits =
        new(new ConcurrentQueue<byte[]>(), boundedCapacity: 8);

    // Se já vimos o primeiro quadro-chave desta transmissão. Enquanto não
    // vimos, os quadros que chegam não têm como ser decodificados (ver Feed).
    private volatile bool _sawKeyframe;

    // Desde quando estamos esperando um quadro-chave por causa de uma
    // ressincronia (0 = não é ressincronia, é o início normal da transmissão,
    // que espera o tempo que for). Ver a válvula de segurança em Feed().
    private long _waitingSince;
    private const long ResyncPatienceMs = 2000;

    public bool Start()
    {
        if (!FfmpegLocator.IsAvailable) return false;

        // Tenta decodificação acelerada por hardware primeiro; se não subir,
        // cai pra decodificação por software (mais compatível — quem
        // assiste não precisa necessariamente ter GPU, só quem transmite
        // precisa pra ter o ganho de performance).
        return TryStart(useHardware: true) || TryStart(useHardware: false);
    }

    private bool TryStart(bool useHardware)
    {
        if (_stopRequested) return false;

        string hwFlag = useHardware ? "-hwaccel d3d11va " : "";
        string args =
            $"-hide_banner -loglevel warning {hwFlag}" +
            "-f h264 -i pipe:0 -f image2pipe -c:v bmp -pix_fmt bgr24 pipe:1";

        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.GetFfmpegPath(),
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

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
            // anormal, o Windows mata este ffmpeg junto (senão ele ficaria
            // rodando pra sempre em segundo plano).
            ChildProcessTracker.Track(proc);

            // Decodificadores não costumam morrer sozinhos só por falta de
            // hardware do jeito que o encoder morre — aqui só checamos que
            // não caiu de cara (erro de linha de comando, por exemplo).
            Thread.Sleep(150);
            if (proc.HasExited)
            {
                proc.Dispose();
                return false;
            }

            lock (_lifecycleLock)
            {
                // Stop() pode ter sido chamado enquanto o processo subia (o
                // Start roda numa thread de fundo). Sem esta checagem, o
                // processo ficava rodando órfão: o Stop() não achou nada pra
                // matar e ninguém mais tinha referência pra ele.
                if (_stopRequested)
                {
                    try { if (!proc.HasExited) proc.Kill(true); } catch { }
                    try { proc.Dispose(); } catch { }
                    return false;
                }

                _process = proc;
                _running = true;

                _stderrThread = new Thread(() => DrainStderr(proc))
                {
                    IsBackground = true,
                    Name = "VysorVideoDecodeStderr"
                };
                _stderrThread.Start();

                _stdoutThread = new Thread(() => ReadStdout(proc))
                {
                    IsBackground = true,
                    Name = "VysorVideoDecodeStdout"
                };
                _stdoutThread.Start();

                _writerThread = new Thread(() => WriteLoop(proc))
                {
                    IsBackground = true,
                    Name = "VysorVideoDecodeStdin"
                };
                _writerThread.Start();
            }

            return true;
        }
        catch
        {
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                try { proc.Dispose(); } catch { }
            }
            return false;
        }
    }

    // Recebe uma access unit H.264 (a mesma que VideoEncodeService produziu
    // do outro lado da rede) e enfileira pro decodificador. Nunca bloqueia.
    public void Feed(byte[] accessUnit)
    {
        if (!_running || accessUnit.Length == 0) return;

        // Começar a assistir no MEIO de uma transmissão é o caso normal: você
        // clica em "assistir" quando quiser. Só que um quadro comum sozinho
        // não descreve uma imagem — ele descreve as DIFERENÇAS em relação ao
        // quadro anterior, que quem acabou de chegar não tem. Jogar isso no
        // decodificador só gera erro e, dependendo do decodificador, um borrão
        // com rastros. Esperamos em silêncio pelo primeiro quadro-chave, que
        // vem no máximo 1 segundo depois.
        if (!_sawKeyframe)
        {
            // VÁLVULA DE SEGURANÇA. Um quadro-chave é o maior de todos (vira
            // ~100 pacotes), então é justamente o mais provável de perder um
            // pedaço no caminho. Numa rede muito ruim dá pra imaginar o caso
            // em que quadro-chave nenhum chega inteiro — e aí, esperando em
            // silêncio, a imagem ficaria congelada pra sempre sem nenhum aviso.
            //
            // Passado esse tempo, aceitamos o que vier. A imagem pode sair
            // suja por um instante, mas volta a se mexer e se recompõe sozinha
            // no primeiro quadro-chave que chegar inteiro. Imagem imperfeita é
            // ruim; imagem parada sem explicação é pior.
            if (!IsKeyframe(accessUnit))
            {
                if (_waitingSince == 0 ||
                    Environment.TickCount64 - _waitingSince < ResyncPatienceMs) return;
            }
            _sawKeyframe = true;
            _waitingSince = 0;
        }

        try
        {
            if (!_pendingUnits.TryAdd(accessUnit))
            {
                // Fila cheia: o decodificador não está dando conta.
                //
                // Antes a gente descartava o quadro mais antigo e enfiava o
                // novo no lugar. Parecia razoável ("perde um quadro, segue a
                // vida") e era justamente o contrário: tirar um quadro do MEIO
                // da fila quebra a corrente do H.264, e todos os quadros
                // seguintes passam a ser decodificados a partir de uma imagem
                // que não existe — saem com rastro do quadro anterior grudado.
                //
                // O certo é assumir a perda de vez: joga fora o que estava na
                // fila e volta a esperar um quadro-chave. Custa uma pausa de
                // no máximo ~250ms (é o intervalo entre quadros-chave) em
                // troca de imagem limpa quando volta.
                RequestResync();
            }
        }
        catch
        {
            // Fila encerrada (Stop em andamento) — ignora.
        }
    }

    // Descarta o que está na fila e volta a ignorar tudo até o próximo
    // quadro-chave.
    //
    // Chamado quando se sabe que a corrente de quadros foi quebrada — seja
    // por perda na rede (ver PeerTransport.OnVideoLoss) ou por não termos dado
    // conta de decodificar tudo. Continuar decodificando depois de um buraco
    // não devolve "um quadro a menos": devolve imagem ERRADA, com pedaços do
    // quadro velho misturados nos novos, até o próximo quadro-chave chegar
    // sozinho. Melhor a imagem congelar por um instante do que borrar.
    //
    // Chamar isto várias vezes seguidas é inofensivo: depois da primeira, a
    // fila já está vazia e só o que muda é continuar esperando o quadro-chave.
    // Quantas vezes esta telinha teve que jogar a fila fora e esperar um novo
    // quadro-chave. É o melhor indicador de "chegou lixo": cada uma dessas é
    // uma pausa visível de até ~250ms na imagem. Se este número sobe junto com
    // a engasgada, o problema chegou pela rede ou o decodificador não venceu;
    // se fica parado, a engasgada é da tela (interface), não do vídeo.
    public long Resyncs => Interlocked.Read(ref _resyncs);
    private long _resyncs;

    // Quadros que saíram decodificados de verdade, pra medir os fps REAIS
    // chegando nesta telinha.
    public long DecodedFrames => Interlocked.Read(ref _decodedFrames);
    private long _decodedFrames;

    public void RequestResync()
    {
        if (!_sawKeyframe) return;   // já estamos esperando: não reinicia a espera

        Interlocked.Increment(ref _resyncs);
        _sawKeyframe = false;
        _waitingSince = Math.Max(1, Environment.TickCount64);
        try
        {
            while (_pendingUnits.TryTake(out _)) { }
        }
        catch { }
    }

    // Um quadro-chave (IDR) é o único que se explica sozinho — é por ele que
    // dá pra começar a assistir. Procuramos uma NAL do tipo 5 dentro da
    // access unit, percorrendo os "start codes" do formato Annex-B
    // (00 00 01 ou 00 00 00 01).
    private static bool IsKeyframe(byte[] au)
    {
        for (int i = 0; i + 3 < au.Length; i++)
        {
            if (au[i] != 0 || au[i + 1] != 0) continue;

            int headerIndex;
            if (au[i + 2] == 1) headerIndex = i + 3;
            else if (au[i + 2] == 0 && au[i + 3] == 1) headerIndex = i + 4;
            else continue;

            if (headerIndex >= au.Length) return false;
            if ((au[headerIndex] & 0x1F) == 5) return true;
        }

        return false;
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

        try { _pendingUnits.CompleteAdding(); } catch { }

        if (proc != null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    try { proc.StandardInput.Close(); } catch { }
                    if (!proc.WaitForExit(400))
                    {
                        try { proc.Kill(true); } catch { }
                    }
                }
            }
            catch { }
        }

        JoinQuietly(_writerThread);
        JoinQuietly(_stdoutThread);
        JoinQuietly(_stderrThread);
        _writerThread = _stdoutThread = _stderrThread = null;

        if (proc != null)
        {
            try { proc.Dispose(); } catch { }
        }

        _buf = new byte[1 << 16];
        _bufLen = 0;
        _waitingSince = 0;
    }

    private static void JoinQuietly(Thread? thread)
    {
        try { thread?.Join(300); } catch { }
    }

    // Única thread que escreve no stdin do ffmpeg — bloquear aqui é seguro.
    private void WriteLoop(Process proc)
    {
        try
        {
            var stdin = proc.StandardInput.BaseStream;
            foreach (var unit in _pendingUnits.GetConsumingEnumerable())
            {
                if (!_running) break;
                stdin.Write(unit, 0, unit.Length);
                stdin.Flush();
            }
        }
        catch
        {
            _running = false;
        }
    }

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

    // --- Separação dos frames BMP no stdout --------------------------------
    //
    // Um arquivo BMP começa com 'B' 'M' (0x42 0x4D) seguido, no offset 2, por
    // um inteiro de 4 bytes little-endian com o tamanho TOTAL do arquivo
    // (inclusive esses 14 bytes de cabeçalho). Isso dá exatamente o tamanho
    // de um frame completo, sem precisar entender mais nada do formato BMP —
    // só ler esse campo e cortar o buffer nesse ponto.

    private byte[] _buf = new byte[1 << 16];
    private int _bufLen;
    private const int MaxBufferBytes = 64 * 1024 * 1024;

    // Tem que ser MENOR que MaxBufferBytes: um cabeçalho corrompido
    // declarando um tamanho entre os dois valores nunca poderia ser
    // satisfeito, e o buffer ficaria crescendo até o limite a cada vez.
    private const long MaxPlausibleFrameSize = 48L * 1024 * 1024;

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
                ExtractBmpFrames();

                if (_bufLen > MaxBufferBytes)
                {
                    _buf = new byte[1 << 16]; // stream corrompido — descarta e recomeça
                    _bufLen = 0;
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

    private void ExtractBmpFrames()
    {
        int offset = 0;
        while (_bufLen - offset >= 14)
        {
            if (_buf[offset] != (byte)'B' || _buf[offset + 1] != (byte)'M')
            {
                int nextBm = FindNextBm(offset + 1);
                if (nextBm < 0)
                {
                    CompactBuffer(Math.Max(offset, _bufLen - 1));
                    return;
                }
                offset = nextBm;
                continue;
            }

            uint fileSize = BitConverter.ToUInt32(_buf, offset + 2);
            if (fileSize < 14 || fileSize > MaxPlausibleFrameSize)
            {
                int nextBm = FindNextBm(offset + 1);
                if (nextBm < 0) { CompactBuffer(offset); return; }
                offset = nextBm;
                continue;
            }

            if (_bufLen - offset < fileSize) break; // frame ainda incompleto — espera mais bytes

            byte[] frame = new byte[fileSize];
            Buffer.BlockCopy(_buf, offset, frame, 0, (int)fileSize);
            Interlocked.Increment(ref _decodedFrames);
            try { OnFrameDecoded?.Invoke(frame); } catch { }

            offset += (int)fileSize;
        }

        CompactBuffer(offset);
    }

    private int FindNextBm(int from)
    {
        for (int i = from; i + 1 < _bufLen; i++)
        {
            if (_buf[i] == (byte)'B' && _buf[i + 1] == (byte)'M') return i;
        }
        return -1;
    }

    private void CompactBuffer(int upTo)
    {
        if (upTo <= 0) return;
        int remaining = _bufLen - upTo;
        if (remaining > 0)
        {
            Buffer.BlockCopy(_buf, upTo, _buf, 0, remaining);
        }
        _bufLen = remaining;
    }
}
