using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace VysorClient.Services;

// Captura o áudio que deve ser transmitido junto com a tela.
//
// Dois modos, escolhidos por quem chama Start(...):
//   - Compartilhando uma JANELA: passamos o PID do processo dono da janela, e
//     tentamos capturar SÓ o áudio daquele processo (modo "incluir"). Assim o
//     áudio transmitido é exatamente o do que está sendo compartilhado.
//   - Compartilhando a TELA INTEIRA: não há um processo único, então
//     capturamos o áudio do sistema inteiro EXCLUINDO o Discord (se ele
//     estiver rodando) — pra nunca vazar a chamada de voz dos seus amigos.
//
// O que acontece quando a API avançada de "captura por processo" falha
// (Windows mais antigo que a build 20348, permissão, driver problemático):
//
//   - Compartilhando uma JANELA: NÃO transmitimos áudio nenhum. Antes,
//     caíamos na captura do sistema inteiro — o que é exatamente o oposto do
//     que a pessoa pediu e vaza tudo que estiver tocando no PC (chamada do
//     Discord, mensagens, música, outra aba). Silêncio é uma falha visível e
//     inofensiva; vazar áudio é uma falha invisível e grave.
//   - Compartilhando a TELA INTEIRA: aí sim usamos o áudio do sistema, porque
//     é isso que a pessoa pediu ao compartilhar a tela toda. Só perdemos a
//     exclusão do Discord.
//
// Em ambos os casos, quem chama pode consultar ActiveMode pra avisar o
// usuário do que realmente está acontecendo.
//
// A saída é sempre PCM 16 bits mono a 48kHz, já comprimida em μ-law (ver
// MuLawCodec), pronta para ir pela rede.
public sealed class AudioCaptureService : IDisposable
{
    private const int WireSampleRate = 48000;

    public event Action<byte[]>? OnAudioChunk;

    private readonly object _stateLock = new();
    private bool _running;

    private CancellationTokenSource? _cts;
    private Thread? _rawCaptureThread;
    private WasapiLoopbackCapture? _fallbackCapture;

    private readonly object _fallbackLock = new();
    private readonly List<float> _monoAccum = new();

    // Posição de leitura (com casas decimais) do reamostrador da via de
    // reserva. Precisa sobreviver entre uma chamada e outra, senão o áudio
    // sai com estalos e vai acumulando atraso — ver ProcessCapturedPcm.
    private double _resamplePos;

    public bool IsRunning => _running;

    /// <summary>Verdadeiro se a via avançada (isolada por processo) conseguiu iniciar.</summary>
    public bool IsUsingProcessFilter { get; private set; }

    // O que a captura de áudio está realmente fazendo agora. Serve pra
    // interface poder avisar o usuário quando o resultado não é o esperado.
    public enum AudioMode
    {
        /// <summary>Sem áudio (o usuário desligou, ou o isolamento falhou ao compartilhar uma janela).</summary>
        None,
        /// <summary>Só o áudio do app compartilhado — o cenário ideal.</summary>
        ProcessIsolated,
        /// <summary>Áudio do sistema inteiro, com o Discord excluído.</summary>
        SystemWithoutDiscord,
        /// <summary>Áudio do sistema inteiro, SEM conseguir excluir o Discord.</summary>
        SystemUnfiltered,
    }

    public AudioMode ActiveMode { get; private set; } = AudioMode.None;

    // Start e Stop seguram o _stateLock do começo ao fim de propósito.
    // Antes, o lock só protegia a flag _running e todo o resto (subir a
    // thread de captura, guardar o CancellationTokenSource) ficava de fora —
    // então parar e começar a transmitir rapidinho podia fazer o Stop()
    // cancelar algo que ainda não existia e, logo depois, o Start()
    // instalar uma captura que ninguém mais iria parar. Na prática: o
    // microfone/áudio do sistema CONTINUAVA sendo transmitido depois de
    // você mandar parar. Como as duas operações são rápidas e só acontecem
    // quando o usuário clica, segurar o lock aqui não custa nada.
    public void Start(uint? targetProcessId)
    {
        lock (_stateLock)
        {
            if (_running) return;
            _running = true;

            lock (_fallbackLock) { _monoAccum.Clear(); _resamplePos = 0; }

            bool startedRaw = false;
            bool triedDiscordExclusion = false;

            try
            {
                if (targetProcessId.HasValue)
                {
                    // Modo JANELA: queremos só o áudio daquele app.
                    startedRaw = TryStartRawCapture(targetProcessId.Value, exclude: false);
                }
                else
                {
                    // Modo TELA INTEIRA: áudio do sistema, tirando o Discord.
                    uint? discordPid = FindDiscordProcessId();
                    if (discordPid.HasValue)
                    {
                        triedDiscordExclusion = true;
                        startedRaw = TryStartRawCapture(discordPid.Value, exclude: true);
                    }
                }
            }
            catch
            {
                startedRaw = false;
            }

            IsUsingProcessFilter = startedRaw;

            if (startedRaw)
            {
                ActiveMode = targetProcessId.HasValue
                    ? AudioMode.ProcessIsolated
                    : AudioMode.SystemWithoutDiscord;
                return;
            }

            // Não conseguiu isolar. Se a pessoa está compartilhando uma JANELA,
            // é melhor ficar sem áudio do que mandar o áudio do PC inteiro —
            // ela pediu uma janela, não o computador. Este era o bug: aqui
            // caía-se na captura total, e tudo que estivesse tocando no PC ia
            // junto sem ninguém perceber.
            if (targetProcessId.HasValue)
            {
                ActiveMode = AudioMode.None;
                return;
            }

            // Tela inteira: áudio do sistema é o esperado mesmo.
            StartFallbackCapture();
            ActiveMode = _fallbackCapture != null
                ? (triedDiscordExclusion ? AudioMode.SystemUnfiltered : AudioMode.SystemWithoutDiscord)
                : AudioMode.None;
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_running) return;
            _running = false;

            StopInternalLocked();
        }
    }

    private void StopInternalLocked()
    {
        ActiveMode = AudioMode.None;
        IsUsingProcessFilter = false;

        try { _cts?.Cancel(); } catch { /* ignora */ }
        try { _rawCaptureThread?.Join(500); } catch { /* ignora */ }
        try { _cts?.Dispose(); } catch { /* ignora */ }
        _cts = null;
        _rawCaptureThread = null;

        try
        {
            _fallbackCapture?.StopRecording();
            _fallbackCapture?.Dispose();
        }
        catch { /* ignora */ }
        _fallbackCapture = null;

        lock (_fallbackLock) { _monoAccum.Clear(); _resamplePos = 0; }
    }

    public void Dispose() => Stop();

    // --- Via avançada: loopback isolado por processo -----------------------------------

    // Formatos tentados, em ordem, ao capturar o áudio de um processo.
    //
    // Por que uma LISTA e não um formato só: no modo "captura por processo" não
    // existe um dispositivo de áudio de verdade pra consultar — as funções que
    // normalmente diriam "qual formato você aceita?" (GetMixFormat e
    // IsFormatSupported) simplesmente não são implementadas nesse modo. Ou
    // seja: não dá pra perguntar, só dá pra tentar. Então tentamos do melhor
    // pro mais seguro.
    //
    // O primeiro é o ideal (bate com o formato que mandamos pela rede, sem
    // conversão). O segundo é EXATAMENTE o que o exemplo oficial da Microsoft
    // usa — o mais provável de funcionar em qualquer máquina. O terceiro é o
    // que este código usava antes, mantido só por garantia.
    //
    // Era justamente aqui que estava o bug: a versão anterior tentava só MONO,
    // e nenhuma implementação de referência pede mono. Como não há como
    // consultar os formatos aceitos, a recusa aparecia apenas como uma falha
    // genérica — e o código caía silenciosamente na captura do sistema INTEIRO.
    // Resultado: quem compartilhava uma janela transmitia todo o áudio do PC.
    private static readonly (int SampleRate, int Channels)[] ProcessLoopbackFormats =
    {
        (48000, 2),
        (44100, 2),
        (48000, 1),
    };

    // Detalhe técnico da última falha de isolamento, pra podermos mostrar na
    // tela em vez de só "não deu". Sem isso, diagnosticar exigia adivinhação.
    public string? LastFailureDetail { get; private set; }

    private sealed class RawStartOutcome
    {
        public bool Started;
        public string? Detail;
    }

    // Sobe a captura isolada por processo numa thread MTA dedicada.
    //
    // POR QUE UMA THREAD PRÓPRIA, E POR QUE MTA — este era um problema real:
    // a montagem acontecia na thread da interface, que no WPF é STA. Só que o
    // Windows entrega o resultado da ativação chamando um objeto nosso a
    // partir de uma thread MTA. Quando o objeto pertence a uma STA, o COM
    // precisa "transferir" essa chamada para a STA — e nós estávamos
    // justamente bloqueando a STA esperando o resultado. Além disso, o objeto
    // de áudio obtido numa apartment e usado a partir de outra é uma fonte
    // clássica de falha silenciosa.
    //
    // Fazendo tudo (ativar, configurar, capturar) numa única thread MTA, o
    // Windows chama a gente diretamente, sem transferência entre apartments.
    private bool TryStartRawCapture(uint pid, bool exclude)
    {
        var ready = new ManualResetEventSlim(false);
        var outcome = new RawStartOutcome();
        var cts = new CancellationTokenSource();

        var thread = new Thread(() => RawCaptureThreadMain(pid, exclude, cts.Token, outcome, ready))
        {
            IsBackground = true,
            Name = "VysorAudioCapture"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        // Espera só a MONTAGEM terminar (não a captura). O limite é generoso
        // porque são até três tentativas de formato.
        if (!ready.Wait(TimeSpan.FromSeconds(15)))
        {
            outcome.Detail = "a preparação do áudio isolado não respondeu a tempo";
        }

        LastFailureDetail = outcome.Started ? null : outcome.Detail;

        if (!outcome.Started)
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
            return false;
        }

        _cts = cts;
        _rawCaptureThread = thread;
        return true;
    }

    private void RawCaptureThreadMain(uint pid, bool exclude, CancellationToken token,
                                      RawStartOutcome outcome, ManualResetEventSlim ready)
    {
        IRawAudioClient? audioClient = null;
        IRawAudioCaptureClient? captureClient = null;
        EventWaitHandle? readyEvent = null;
        WaveFormat? chosenFormat = null;
        string? lastDetail = null;

        try
        {
            foreach (var (sampleRate, channels) in ProcessLoopbackFormats)
            {
                if (token.IsCancellationRequested) break;

                if (TrySetupRawCapture(pid, exclude, sampleRate, channels,
                                       out audioClient, out captureClient, out readyEvent, out lastDetail))
                {
                    chosenFormat = new WaveFormat(sampleRate, 16, channels);
                    break;
                }
            }

            if (chosenFormat == null || audioClient == null || captureClient == null || readyEvent == null)
            {
                outcome.Started = false;
                outcome.Detail = lastDetail ?? "falha desconhecida ao preparar o áudio isolado";
                return;
            }

            outcome.Started = true;
            outcome.Detail = null;
            ready.Set(); // libera quem está esperando ANTES de entrar no laço

            RawCaptureLoop(audioClient, captureClient, readyEvent, chosenFormat, token);
        }
        catch (Exception ex)
        {
            outcome.Started = false;
            outcome.Detail = lastDetail ?? ex.Message;
        }
        finally
        {
            ready.Set(); // garante que ninguém fique esperando pra sempre
        }
    }

    // Uma tentativa completa com um formato específico. Devolve false e
    // preenche "detail" com o motivo exato (etapa + código de erro do
    // Windows), pra sabermos o que aconteceu em vez de supor.
    private static bool TrySetupRawCapture(
        uint pid, bool exclude, int sampleRate, int channels,
        out IRawAudioClient? audioClient, out IRawAudioCaptureClient? captureClient,
        out EventWaitHandle? readyEvent, out string? detail)
    {
        audioClient = null;
        captureClient = null;
        readyEvent = null;
        detail = null;

        string fmt = $"{sampleRate}Hz/{channels}ch";

        try
        {
            object rawObj = ProcessLoopbackInterop.ActivateProcessLoopbackAudioClient(pid, exclude);
            var client = (IRawAudioClient)rawObj;

            var format = new WaveFormat(sampleRate, 16, channels);
            Guid sessionGuid = Guid.Empty;

            // Os parâmetros abaixo seguem o exemplo oficial da Microsoft
            // (ApplicationLoopback):
            //  - EVENTCALLBACK: o Windows nos AVISA quando há áudio novo, em
            //    vez de a gente ficar perguntando num laço. É assim que todas
            //    as implementações conhecidas fazem.
            //  - duração e periodicidade ZERO: exigência documentada do modo
            //    orientado a evento.
            int hr = client.Initialize(
                WasapiConstants.AUDCLNT_SHAREMODE_SHARED,
                WasapiConstants.AUDCLNT_STREAMFLAGS_LOOPBACK
                    | WasapiConstants.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                    | WasapiConstants.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
                0,
                0,
                format,
                ref sessionGuid);
            if (hr < 0) { detail = $"Initialize {fmt} -> {DescribeHResult(hr)}"; return false; }

            var handle = new EventWaitHandle(false, EventResetMode.AutoReset);
            hr = client.SetEventHandle(handle.SafeWaitHandle.DangerousGetHandle());
            if (hr < 0) { handle.Dispose(); detail = $"SetEventHandle {fmt} -> {DescribeHResult(hr)}"; return false; }

            hr = client.GetService(WasapiConstants.IID_IAudioCaptureClient, out IntPtr captureClientPtr);
            if (hr < 0 || captureClientPtr == IntPtr.Zero)
            {
                handle.Dispose();
                detail = $"GetService {fmt} -> {DescribeHResult(hr)}";
                return false;
            }

            object captureObj = Marshal.GetTypedObjectForIUnknown(captureClientPtr, typeof(IRawAudioCaptureClient));
            Marshal.Release(captureClientPtr);

            hr = client.Start();
            if (hr < 0) { handle.Dispose(); detail = $"Start {fmt} -> {DescribeHResult(hr)}"; return false; }

            audioClient = client;
            captureClient = (IRawAudioCaptureClient)captureObj;
            readyEvent = handle;
            return true;
        }
        catch (Exception ex)
        {
            detail = $"ativação {fmt} -> {ex.Message}";
            return false;
        }
    }

    // Traduz os códigos de erro mais prováveis pra algo interpretável.
    private static string DescribeHResult(int hr)
    {
        string hex = $"0x{hr:X8}";
        return (uint)hr switch
        {
            0x88890008 => $"{hex} (formato não suportado)",
            0x88890004 => $"{hex} (dispositivo de áudio invalidado)",
            0x88890013 => $"{hex} (duração/periodicidade inválidas)",
            0x8889000A => $"{hex} (falha ao criar o ponto de captura)",
            0x88890001 => $"{hex} (cliente de áudio não inicializado)",
            0x80004001 => $"{hex} (não implementado neste Windows)",
            0x80070057 => $"{hex} (parâmetro inválido)",
            0x80070490 => $"{hex} (recurso não encontrado — Windows sem suporte a captura por processo)",
            0x80070005 => $"{hex} (acesso negado)",
            _ => hex,
        };
    }

    private void RawCaptureLoop(IRawAudioClient audioClient, IRawAudioCaptureClient captureClient,
                                EventWaitHandle readyEvent, WaveFormat capturedFormat, CancellationToken token)
    {
        // Quantos bytes cada "quadro" de áudio ocupa: 2 bytes por amostra
        // (16 bits) vezes o número de canais que negociamos.
        int blockAlign = 2 * Math.Max(1, capturedFormat.Channels);

        try
        {
            while (!token.IsCancellationRequested)
            {
                // Espera o Windows avisar que há áudio novo. O tempo limite
                // serve só pra reavaliar o cancelamento de vez em quando —
                // não é uma consulta em laço.
                if (!readyEvent.WaitOne(200)) continue;

                while (!token.IsCancellationRequested)
                {
                    int hr = captureClient.GetNextPacketSize(out int framesInPacket);
                    if (hr < 0 || framesInPacket == 0) break;

                    hr = captureClient.GetBuffer(out IntPtr buffer, out int numFrames, out int flags, out _, out _);
                    if (hr < 0) break;

                    int byteCount = numFrames * blockAlign;
                    if (byteCount > 0)
                    {
                        var pcm = new byte[byteCount];
                        bool silent = (flags & WasapiConstants.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                        if (!silent && buffer != IntPtr.Zero)
                        {
                            Marshal.Copy(buffer, pcm, 0, byteCount);
                        }
                        captureClient.ReleaseBuffer(numFrames);

                        // Passa pela MESMA conversão da outra via de captura
                        // (mistura os canais em um só e ajusta a frequência
                        // para a que trafega na rede). Antes, este caminho
                        // assumia que o áudio já vinha pronto — o que só era
                        // verdade no formato mono que, na prática, nunca era
                        // aceito.
                        ProcessCapturedPcm(pcm, byteCount, capturedFormat);
                    }
                    else
                    {
                        captureClient.ReleaseBuffer(numFrames);
                    }
                }
            }
        }
        catch
        {
            // Se o loop cair no meio (dispositivo removido, processo alvo fechou, etc.),
            // simplesmente para — o usuário pode tentar iniciar a transmissão de novo.
        }
        finally
        {
            try { audioClient.Stop(); } catch { /* ignora */ }
            try { readyEvent.Dispose(); } catch { /* ignora */ }
        }
    }

    // --- Fallback: loopback do sistema inteiro, via NAudio (caminho testado/estável) ---

    private void StartFallbackCapture()
    {
        try
        {
            var capture = new WasapiLoopbackCapture();
            var srcFormat = capture.WaveFormat;
            capture.DataAvailable += (_, e) => ProcessCapturedPcm(e.Buffer, e.BytesRecorded, srcFormat);
            _fallbackCapture = capture;
            capture.StartRecording();
        }
        catch
        {
            _fallbackCapture = null;
            // Se nem isso funcionar (por exemplo, sem dispositivo de saída padrão
            // configurado no Windows), a transmissão simplesmente segue sem áudio.
        }
    }

    private void ProcessCapturedPcm(byte[] buffer, int bytesRecorded, WaveFormat srcFormat)
    {
        int channels = Math.Max(1, srcFormat.Channels);
        bool isFloat = srcFormat.Encoding == WaveFormatEncoding.IeeeFloat;
        int bytesPerSample = srcFormat.BitsPerSample / 8;
        int frameSize = bytesPerSample * channels;
        if (frameSize <= 0 || bytesRecorded < frameSize) return;

        lock (_fallbackLock)
        {
            for (int i = 0; i + frameSize <= bytesRecorded; i += frameSize)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int off = i + c * bytesPerSample;
                    float sampleValue = isFloat
                        ? BitConverter.ToSingle(buffer, off)
                        : BitConverter.ToInt16(buffer, off) / 32768f;
                    sum += sampleValue;
                }
                _monoAccum.Add(sum / channels);
            }

            int srcRate = srcFormat.SampleRate;
            if (srcRate <= 0) { _monoAccum.Clear(); _resamplePos = 0; return; }

            // Reamostragem com posição FRACIONÁRIA contínua entre chamadas.
            //
            // Antes, a leitura recomeçava da posição 0 a cada bloco de áudio
            // que chegava e o "quanto foi consumido" era arredondado — dois
            // erros que se somavam. Numa placa de 44.1kHz isso gerava um
            // salto de fase ~100 vezes por segundo (o chiado/estalinho que
            // dá pra ouvir) e produzia áudio 0,2% mais rápido do que devia,
            // enchendo aos poucos o buffer de quem escuta até começar a
            // descartar pedaços (as falhas periódicas no som). Guardando a
            // sobra fracionária (_resamplePos) o fluxo fica contínuo.
            double step = srcRate / (double)WireSampleRate;

            var outSamples = new List<short>(_monoAccum.Count + 8);
            double pos = _resamplePos;
            while (pos + 1 < _monoAccum.Count)
            {
                int idx = (int)pos;
                double frac = pos - idx;
                float s0 = _monoAccum[idx];
                float s1 = _monoAccum[idx + 1];
                float interp = (float)(s0 + (s1 - s0) * frac);
                outSamples.Add((short)Math.Clamp(interp * short.MaxValue, short.MinValue, short.MaxValue));
                pos += step;
            }

            if (outSamples.Count == 0)
            {
                _resamplePos = pos;
                return;
            }

            var pcm = new byte[outSamples.Count * 2];
            for (int o = 0; o < outSamples.Count; o++)
            {
                short pcm16 = outSamples[o];
                pcm[o * 2] = (byte)(pcm16 & 0xFF);
                pcm[o * 2 + 1] = (byte)((pcm16 >> 8) & 0xFF);
            }

            // Descarta as amostras já usadas, mantendo a fração restante pra
            // próxima rodada continuar exatamente de onde parou.
            int consumed = (int)pos;
            if (consumed > 0)
            {
                consumed = Math.Min(consumed, _monoAccum.Count);
                _monoAccum.RemoveRange(0, consumed);
                _resamplePos = pos - consumed;
            }
            else
            {
                _resamplePos = pos;
            }

            EmitPcm16Mono(pcm);
        }
    }

    private void EmitPcm16Mono(byte[] pcmBytes)
    {
        int sampleCount = pcmBytes.Length / 2;
        if (sampleCount == 0) return;
        var samples = new short[sampleCount];
        Buffer.BlockCopy(pcmBytes, 0, samples, 0, sampleCount * 2);
        byte[] encoded = MuLawCodec.EncodeBuffer(samples, sampleCount);
        OnAudioChunk?.Invoke(encoded);
    }

    // --- Utilitário: achar o processo do Discord, se estiver rodando -------------------

    // Acha o processo PRINCIPAL do Discord (o "pai" de todos os outros).
    //
    // Isso importa muito: o Discord roda vários processos com o mesmo nome
    // (janela, GPU, renderizadores...), e a exclusão de áudio do Windows
    // exclui o processo indicado E os filhos dele. Se a gente apontar pra um
    // processo-filho qualquer, o pai continua de fora da exclusão e o áudio
    // da chamada do Discord VAI JUNTO na transmissão — exatamente o que essa
    // funcionalidade existe pra evitar. A versão anterior escolhia o
    // primeiro processo com janela visível e, se o Discord estivesse
    // minimizado na bandeja (nenhum tem janela), caía no primeiro da lista,
    // que quase nunca é o principal.
    private static uint? FindDiscordProcessId()
    {
        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName("Discord");
        }
        catch
        {
            return null;
        }

        try
        {
            if (candidates.Length == 0) return null;

            var byId = new Dictionary<int, Process>();
            foreach (var p in candidates)
            {
                try { byId[p.Id] = p; } catch { }
            }

            // O processo raiz é aquele cujo "pai" não é também um Discord.
            Process? root = null;
            foreach (var p in candidates)
            {
                try
                {
                    int parentId = GetParentProcessId(p.Id);
                    if (parentId <= 0 || !byId.ContainsKey(parentId))
                    {
                        // Entre vários candidatos a raiz, prefere o que tem
                        // janela (é o processo principal do app).
                        if (root == null || p.MainWindowHandle != IntPtr.Zero) root = p;
                    }
                }
                catch { }
            }

            root ??= candidates[0];
            return (uint)root.Id;
        }
        catch
        {
            return null;
        }
        finally
        {
            // Process.GetProcessesByName abre um handle por processo — sem
            // liberar, cada início de transmissão vazava alguns handles.
            foreach (var p in candidates)
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    private static int GetParentProcessId(int processId)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return -1;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return -1;

            do
            {
                if (entry.th32ProcessID == (uint)processId) return (int)entry.th32ParentProcessID;
            }
            while (Process32Next(snapshot, ref entry));
        }
        catch { }
        finally
        {
            CloseHandle(snapshot);
        }

        return -1;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
