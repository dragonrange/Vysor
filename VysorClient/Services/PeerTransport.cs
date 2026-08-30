using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VysorClient.Services;

// A conversa direta entre dois computadores, sem servidor no meio.
//
// COMO DOIS PCs ATRÁS DE ROTEADORES SE ACHAM
// Nenhum dos dois aceita conexão de fora — essa é a regra do roteador
// doméstico. Mas os dois aceitam RESPOSTA de uma conversa que eles mesmos
// começaram. Então os dois começam ao mesmo tempo: cada um manda pacotes pro
// endereço externo do outro. O primeiro pacote de cada lado é descartado
// (o roteador do outro ainda não conhece aquela conversa), mas ele deixa
// aberta a passagem de volta. Quando os dois já mandaram, os pacotes seguintes
// passam nos dois sentidos. É isso o "furo de NAT" — e é o mesmo mecanismo
// que faz chamada de vídeo funcionar sem ninguém configurar nada.
//
// Pra isso funcionar cada lado precisa saber o endereço externo do outro, e
// quem descobre isso é o StunClient. A troca desses endereços acontece pelo
// servidor da sala (que carrega só isso: uns poucos bytes por pessoa).
//
// UM SOCKET SÓ PRA TODO MUNDO, DE PROPÓSITO
// O endereço externo que o STUN descobre pertence ao SOCKET, não ao
// computador. Se cada amigo usasse um socket diferente, cada um teria um
// endereço externo diferente e seria preciso descobrir tudo de novo pra cada
// pessoa. Com um socket só, uma descoberta serve pra sala inteira.
public class PeerTransport : IDisposable
{
    private const byte TypePunch = 0;
    private const byte TypePunchAck = 1;
    private const byte TypeKeepalive = 3;

    // Tamanho da assinatura que acompanha os pacotes de contato. Não é
    // criptografia — é só o suficiente pra que um pacote perdido da internet
    // (ou um curioso varrendo portas) não seja confundido com um amigo.
    private const int SignatureSize = 16;

    // Enquanto não conectou, insiste bastante: é uma janela curta em que os
    // dois lados precisam estar batendo na porta um do outro.
    private static readonly TimeSpan PunchInterval = TimeSpan.FromMilliseconds(250);

    // Depois de conectado, um sinal de vida de vez em quando. Sem isso o
    // roteador fecha a passagem por falta de uso (costuma ser 30s a 2min) e a
    // transmissão morre sozinha no meio.
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(20);

    private sealed class Peer
    {
        public required string UserId { get; init; }

        // DOIS remontadores, um pra cada tipo — e isto NÃO é detalhe de
        // organização, é a correção de um bug que destruía a imagem.
        //
        // O QUE ACONTECIA COM UM SÓ
        // Cada quadro leva um número de série, e o remontador ignora tudo que
        // chega com número MENOR que o último quadro que ele entregou (é o que
        // impede pedaços atrasados de reabrirem a montagem de um quadro velho).
        // Só que o número de série é um contador único, compartilhado por vídeo
        // E áudio — e áudio sai na frente do vídeo de propósito (fila separada,
        // sem espera; ver SendLoop), justamente pra voz não engasgar.
        //
        // Resultado: o áudio, saindo depois mas chegando antes, "carimbava" um
        // número mais alto no remontador. Os pedaços do quadro de vídeo que
        // ainda estavam a caminho chegavam com número menor e eram jogados
        // fora como se fossem atrasados. O quadro de vídeo nunca completava.
        //
        // E isso acontecia com quase TODO quadro grande: um quadro-chave vira
        // ~100 pacotes espaçados por dezenas de milissegundos, tempo de sobra
        // pra vários pedaços de áudio passarem na frente. Daí os três sintomas
        // relatados juntos e sempre nessa combinação: imagem engasgando,
        // quadro anterior "mesclando" com os seguintes (corrente H.264 quebrada
        // por quadro faltando) — e o ÁUDIO PERFEITO, porque o áudio era
        // justamente quem ganhava a corrida e nunca era descartado.
        //
        // Com um remontador por tipo, cada um tem a sua própria contagem e um
        // não interfere no outro. O formato dos pacotes não muda em nada.
        public required FrameReassembler VideoReassembler { get; init; }
        public required FrameReassembler AudioReassembler { get; init; }

        public List<IPEndPoint> Candidates { get; } = new();
        public IPEndPoint? Confirmed { get; set; }
        public DateTime LastHeard { get; set; }
        public uint NextFrameId;

        // Algum dos endereços anunciados por este amigo cai no mesmo /24 de
        // algum dos NOSSOS próprios endereços locais? Só um indício (redes
        // domésticas normalmente usam /24, mas nem sempre) — usado só pra
        // UI explicar melhor uma demora, nunca pra decidir o furo de NAT em
        // si (isso continua tentando TODOS os candidatos, sempre).
        public bool SameNetworkHint;
        public DateTime FirstSeenAt = DateTime.UtcNow;
        public bool StuckNotified;
    }

    private readonly string _myUserId;
    private readonly byte[] _roomKey;
    private readonly ConcurrentDictionary<string, Peer> _peers = new();

    private Socket? _socket;
    private Thread? _receiveThread;
    private Thread? _maintenanceThread;
    private volatile bool _running;

    // Quadro pronto, já remontado e conferido. Vem de uma thread de rede:
    // quem escuta não pode bloquear esperando a interface.
    public event Action<string, byte, byte[]>? OnFrame;

    // Um amigo passou a estar (ou deixou de estar) alcançável direto.
    public event Action<string, bool>? OnPeerStateChanged;

    // Um quadro de VÍDEO deste amigo se perdeu no caminho. Ver o comentário
    // grande em FrameReassembler.TakeLoss: seguir decodificando depois de uma
    // perda produz imagem borrada/com rastro do quadro anterior, então quem
    // ouve isto deve pedir ressincronia ao decodificador.
    public event Action<string>? OnVideoLoss;

    // Amigo com indício de mesma rede que não conseguiu furar o NAT depois de
    // um tempo razoável. Ver SameNetworkStuckAfter.
    public event Action<string>? OnSameNetworkStuck;

    private static readonly TimeSpan SameNetworkStuckAfter = TimeSpan.FromSeconds(6);

    // Prefixos /24 ("a.b.c") dos NOSSOS endereços locais, calculados uma vez
    // no Start(). Usado só pra comparar com os candidatos que os amigos
    // anunciam (ver Peer.SameNetworkHint).
    private HashSet<string> _localSubnetPrefixes = new();

    public PeerTransport(string myUserId, byte[] roomKey)
    {
        _myUserId = myUserId;
        _roomKey = roomKey;
    }

    public IPEndPoint? LocalEndPoint =>
        _socket?.LocalEndPoint as IPEndPoint;

    public bool Start(int port = 0)
    {
        if (_running) return true;
        try
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Buffers grandes, e isto NÃO é exagero de precaução.
            //
            // Um quadro-chave de tela cheia vira ~100 pacotes que saem quase
            // ao mesmo tempo. Com o buffer padrão (algumas dezenas de KB), a
            // fila enche no meio da rajada e o sistema joga o resto fora em
            // silêncio. Como o quadro-chave é justamente o que "abre" a
            // imagem, a tela do amigo ficaria preta esperando pra sempre.
            // Medido: com o padrão, o quadro de 120 KB não chegava; com
            // estes valores, chega.
            _socket.ReceiveBufferSize = 4 * 1024 * 1024;
            _socket.SendBufferSize = 2 * 1024 * 1024;

            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            _socket.ReceiveTimeout = 500;

            _localSubnetPrefixes = LocalAddresses.List()
                .Select(c => SubnetPrefix(c.Address))
                .Where(p => p != null)
                .Select(p => p!)
                .ToHashSet();

            // Sem isto, no Windows, um pacote que "voltou" de um endereço
            // fechado derruba o socket inteiro com uma exceção — e a conexão
            // com TODOS os amigos morreria por causa de um só. Durante o furo
            // de NAT isso acontece o tempo todo, por definição.
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                _socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch { /* só existe no Windows */ }

            _running = true;

            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "VysorP2PRecv" };
            _maintenanceThread = new Thread(MaintenanceLoop) { IsBackground = true, Name = "VysorP2PKeep" };
            _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "VysorP2PSend" };
            _receiveThread.Start();
            _maintenanceThread.Start();
            _sendThread.Start();
            return true;
        }
        catch
        {
            Stop();
            return false;
        }
    }

    // Diz por onde tentar alcançar um amigo. Normalmente vêm dois endereços:
    // o externo (descoberto pelo STUN) e o da rede local — este último faz a
    // conexão ser instantânea e sem sair de casa quando vocês estão no mesmo
    // Wi-Fi, caso que o endereço externo resolveria mal ou não resolveria.
    public void AddPeer(string userId, IEnumerable<IPEndPoint> candidates)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId == _myUserId) return;

        var peer = _peers.GetOrAdd(userId, id => new Peer
        {
            UserId = id,
            VideoReassembler = new FrameReassembler(_roomKey),
            AudioReassembler = new FrameReassembler(_roomKey),
            LastHeard = DateTime.UtcNow
        });

        lock (peer.Candidates)
        {
            foreach (var candidate in candidates)
            {
                if (candidate == null || peer.Candidates.Contains(candidate)) continue;
                peer.Candidates.Add(candidate);

                string? prefix = SubnetPrefix(candidate.Address.ToString());
                if (prefix != null && _localSubnetPrefixes.Contains(prefix))
                    peer.SameNetworkHint = true;
            }
        }
    }

    // "192.168.1.42" -> "192.168.1". Comparação /24 simples, de propósito:
    // cobre a esmagadora maioria das redes domésticas sem precisar entender
    // máscara de sub-rede de verdade (que o app nem tem como saber, só olhando
    // um IP isolado).
    private static string? SubnetPrefix(string ipv4)
    {
        int last = ipv4.LastIndexOf('.');
        return last <= 0 ? null : ipv4[..last];
    }

    public bool IsSameNetworkHint(string userId) =>
        _peers.TryGetValue(userId, out var peer) && peer.SameNetworkHint;

    public void RemovePeer(string userId) => _peers.TryRemove(userId, out _);

    public bool IsConnected(string userId) =>
        _peers.TryGetValue(userId, out var peer) && peer.Confirmed != null;

    public IReadOnlyCollection<string> ConnectedPeers =>
        _peers.Where(p => p.Value.Confirmed != null).Select(p => p.Key).ToList();

    // ---------- envio ----------

    public void Send(string userId, byte kind, byte[] frame)
    {
        if (!_peers.TryGetValue(userId, out var peer)) return;
        var target = peer.Confirmed;
        if (target == null || _socket == null) return;

        uint frameId = Interlocked.Increment(ref peer.NextFrameId);
        var packets = PeerPacket.Pack(frame, kind, frameId, _roomKey);

        // Um quadro entra INTEIRO na fila ou não entra. Enfiar metade seria o
        // pior dos mundos: os pedaços enviados são inúteis sem os que ficaram
        // de fora (o quadro é descartado do outro lado de qualquer jeito) e
        // ainda teriam gasto banda à toa.
        //
        // Vídeo e áudio têm filas SEPARADAS de propósito — mesmo raciocínio
        // que já valia quando o servidor ainda repassava mídia (ver
        // RoomManager.cs no histórico): um quadro-chave de vídeo a 60fps
        // pode virar mais de cem pacotes de uma vez, e numa fila única eles
        // enfileiravam NA FRENTE de qualquer pedaço de áudio que chegasse
        // logo depois. Resultado observado: o amigo via a imagem engasgar E
        // ficava sem áudio nenhum, porque o áudio nunca conseguia furar a
        // fila de vídeo (ou nem entrava mais, com a fila cheia). Áudio é uma
        // fração do tamanho do vídeo (bem menos de 1 Mbps mesmo em qualidade
        // alta) — separar as filas custa quase nada e evita esse
        // atropelamento por completo.
        bool isAudio = kind == PeerPacket.KindAudio;
        var queue = isAudio ? _audioOutbound : _videoOutbound;
        int max = isAudio ? MaxQueuedAudioPackets : MaxQueuedVideoPackets;

        lock (_outboundLock)
        {
            if (queue.Count + packets.Count > max)
            {
                DroppedFrames++;
                return;
            }

            foreach (var packet in packets) queue.Enqueue(new Outgoing(target, packet));
        }

        _hasWork.Set();
    }

    // ---------- a saída, numa thread só dela ----------
    //
    // POR QUE ISTO EXISTE (e por que a versão anterior engasgava no Windows)
    //
    // Um quadro-chave vira ~100 pacotes. Despejar os 100 de uma vez entope a
    // fila do sistema e faz perder pedaços do próprio quadro que estamos
    // mandando. A primeira solução foi dar uma pausa de 1 milissegundo a cada
    // 16 pacotes — e ela funcionou nos testes, porque os testes rodam em
    // Linux, onde "dormir 1ms" dorme 1ms.
    //
    // No WINDOWS não é assim: o relógio que o sistema usa pra acordar threads
    // tica de 15 em 15 milissegundos. "Dormir 1ms" vira dormir até 15ms. Num
    // quadro-chave isso somava quase 100 milissegundos de thread PARADA — e,
    // pior, parada dentro da thread que produz o vídeo. O codificador
    // engasgava atrás, a taxa de dados oscilava tentando compensar, e o
    // resultado era exatamente o que se via na tela: travadinhas e qualidade
    // pulando.
    //
    // Agora quem envia é uma thread própria: quem produz o vídeo só larga os
    // pacotes numa fila e volta a trabalhar na hora, sem NUNCA esperar. E o
    // espaçamento entre pacotes é medido por cronômetro de verdade, não por
    // "dormir", então não depende do relógio grosso do sistema.
    private readonly record struct Outgoing(IPEndPoint Target, byte[] Packet);

    // Duas filas, de propósito (ver o comentário grande em Send()): áudio
    // sempre passa na frente, e nunca espera o ritmo do vídeo.
    private readonly Queue<Outgoing> _videoOutbound = new();
    private readonly Queue<Outgoing> _audioOutbound = new();
    private readonly object _outboundLock = new();
    private readonly ManualResetEventSlim _hasWork = new(false);
    private Thread? _sendThread;

    // Espaço pra uns 15 quadros-chave de vídeo. Passou disso, a rede não
    // está dando conta e guardar mais só aumentaria o atraso.
    private const int MaxQueuedVideoPackets = 1500;

    // Áudio é minúsculo perto de vídeo (bem menos de 1 Mbps mesmo em
    // qualidade alta) — 200 pacotes já é generoso, e mantém a fila de áudio
    // sempre curta o bastante pra nunca acumular atraso perceptível.
    private const int MaxQueuedAudioPackets = 200;

    // Ritmo de saída do VÍDEO: rápido o bastante pra não atrasar nada, mas
    // espaçado o bastante pra não chegar como rajada capaz de estourar o
    // link de subida real de quem está em casa (upload doméstico raramente
    // chega perto disso — o valor antigo, 8 MB/s = 64 Mbps, deixava um
    // quadro-chave sair todo de uma vez achando que a rede aguentava, e uma
    // rajada assim é exatamente o que os roteadores no meio do caminho
    // descartam primeiro). Áudio NÃO usa este ritmo — sai assim que chega,
    // sem fila de espera na frente.
    // ATUALIZADO junto com o aumento da taxa de bits do vídeo (ver
    // VideoEncodeService.BitsPerPixel): este número precisa ficar bem ACIMA do
    // que o codificador produz, senão ele deixa de ser "espaçar pacotes" e vira
    // um funil — a fila cresce, o atraso sobe e o vídeo volta a engasgar, mas
    // desta vez por culpa nossa. Com o codificador indo até 20 Mbps, 5 MB/s
    // (~42 Mbps) deixa mais que o dobro de folga, e continua muito abaixo de
    // qualquer link doméstico de subida que aguente transmitir 1080p60.
    private const double TargetBytesPerSecond = 5 * 1024 * 1024;

    public long DroppedFrames { get; private set; }

    // --- Números pro painel de diagnóstico ---------------------------------
    //
    // Existem pra responder UMA pergunta objetiva quando a imagem engasga:
    // o problema nasceu aqui (nós não damos conta de enviar), chegou pela rede
    // (pacote perdido no caminho), ou é da tela do outro lado? Sem isso, os
    // três casos parecem idênticos pra quem está assistindo — e cada um pede
    // uma correção diferente.

    // Quadros que a rede perdeu no caminho até nós: chegou pedaço faltando e
    // o quadro inteiro teve que ser descartado. É a medida de perda REAL.
    public long ReassemblyLosses
    {
        get
        {
            long total = 0;
            // Só o vídeo: é ele que engasga visivelmente. O áudio tem fila e
            // remontagem próprias desde a v1.1.9 (ver Peer), e misturar os
            // dois números aqui esconderia justamente o que se quer enxergar.
            foreach (var peer in _peers.Values) total += peer.VideoReassembler.DroppedFrames;
            return total;
        }
    }

    // Quanto está represado esperando pra sair. Em rede saudável isso fica
    // perto de zero o tempo todo; um número que cresce e não volta significa
    // que estamos produzindo vídeo mais rápido do que a subida aguenta.
    public int QueuedVideoPackets
    {
        get { lock (_outboundLock) return _videoOutbound.Count; }
    }

    public int QueuedAudioPackets
    {
        get { lock (_outboundLock) return _audioOutbound.Count; }
    }

    private void SendLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double nextSendAt = 0;

        while (_running)
        {
            Outgoing audioItem = default;
            Outgoing videoItem = default;
            bool hasAudio = false, hasVideo = false;

            lock (_outboundLock)
            {
                if (_audioOutbound.Count > 0)
                {
                    audioItem = _audioOutbound.Dequeue();
                    hasAudio = true;
                }
                else if (_videoOutbound.Count > 0)
                {
                    videoItem = _videoOutbound.Dequeue();
                    hasVideo = true;
                }
                else
                {
                    _hasWork.Reset();
                }
            }

            if (hasAudio)
            {
                // Sem pacing nenhum: a fila de áudio é sempre curta, e atraso
                // na voz incomoda muito mais do que uma rajada pequena.
                try { _socket?.SendTo(audioItem.Packet, audioItem.Target); }
                catch { /* destino sumiu: a manutenção percebe */ }
                continue;
            }

            if (!hasVideo)
            {
                _hasWork.Wait(100);
                continue;
            }

            // Espera até a hora deste pacote. Cronômetro, não "dormir": é o
            // que torna o espaçamento igual no Windows e no Linux.
            double now = clock.Elapsed.TotalMilliseconds;
            if (nextSendAt > now)
            {
                double waitMs = nextSendAt - now;
                if (waitMs > 4) Thread.Sleep((int)(waitMs - 2));   // sono grosso só pra esperas longas
                var spin = new SpinWait();
                while (clock.Elapsed.TotalMilliseconds < nextSendAt) spin.SpinOnce();
            }
            else if (now - nextSendAt > 100)
            {
                nextSendAt = now;      // ficamos parados um tempo: recomeça o ritmo
            }

            try { _socket?.SendTo(videoItem.Packet, videoItem.Target); }
            catch { /* destino sumiu: a manutenção percebe */ }

            nextSendAt += videoItem.Packet.Length * 1000.0 / TargetBytesPerSecond;
        }
    }

    public void Broadcast(byte kind, byte[] frame)
    {
        foreach (var peer in _peers.Values)
        {
            if (peer.Confirmed != null) Send(peer.UserId, kind, frame);
        }
    }

    // ---------- recebimento ----------

    private void ReceiveLoop()
    {
        var buffer = new byte[65536];

        while (_running)
        {
            int read;
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                read = _socket!.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException)
            {
                continue;   // prazo de espera, ou pacote rejeitado: segue
            }
            catch
            {
                break;      // socket fechado
            }

            if (read <= 0) continue;

            try { Handle(buffer, read, (IPEndPoint)from); }
            catch { /* pacote estranho não pode derrubar a thread */ }
        }
    }

    // ---------- descobrir o próprio endereço externo ----------

    // Pergunta "qual é o meu endereço aqui fora?" USANDO ESTE MESMO SOCKET.
    //
    // Isto não é conveniência, é obrigatório: o endereço externo pertence ao
    // socket, não ao computador. Se a pergunta saísse por outro socket, a
    // resposta seria um endereço diferente do que os amigos precisam — eles
    // bateriam numa porta que não é a nossa e nada funcionaria, sem erro
    // nenhum aparecendo em lugar algum.
    public IPEndPoint? QueryStun(IPEndPoint server, TimeSpan timeout)
    {
        var socket = _socket;
        if (socket == null) return null;

        var transactionId = new byte[12];
        Random.Shared.NextBytes(transactionId);
        string key = Convert.ToHexString(transactionId);

        var waiter = new TaskCompletionSource<IPEndPoint?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _stunWaiters[key] = waiter;

        try
        {
            byte[] request = StunClient.BuildRequest(transactionId);
            var perTry = TimeSpan.FromMilliseconds(Math.Max(400, timeout.TotalMilliseconds / 3));

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { socket.SendTo(request, server); } catch { return null; }
                if (waiter.Task.Wait(perTry)) return waiter.Task.Result;
            }
            return null;
        }
        finally
        {
            _stunWaiters.TryRemove(key, out _);
        }
    }

    private readonly ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint?>> _stunWaiters = new();

    // Resposta de servidor STUN? Ela começa igual a um dos nossos pacotes de
    // contato, então o que separa de verdade é o número fixo do protocolo nos
    // bytes 4 a 7 — a chance de um pacote nosso cair nele por acaso é de uma
    // em quatro bilhões.
    private bool TryHandleStun(byte[] buffer, int length)
    {
        if (length < 20) return false;
        if (buffer[0] != 0x01 || buffer[1] != 0x01) return false;
        if (buffer[4] != 0x21 || buffer[5] != 0x12 || buffer[6] != 0xA4 || buffer[7] != 0x42)
            return false;

        var transactionId = new byte[12];
        Buffer.BlockCopy(buffer, 8, transactionId, 0, 12);

        if (!_stunWaiters.TryRemove(Convert.ToHexString(transactionId), out var waiter))
            return true;   // era STUN, mas de uma pergunta que já expirou

        waiter.TrySetResult(StunClient.ParseResponse(buffer, length, transactionId));
        return true;
    }

    private void Handle(byte[] buffer, int length, IPEndPoint from)
    {
        if (TryHandleStun(buffer, length)) return;

        byte type = buffer[0];

        if (type == TypePunch || type == TypePunchAck || type == TypeKeepalive)
        {
            string? userId = ReadContactPacket(buffer, length, type);
            if (userId == null) return;
            if (!_peers.TryGetValue(userId, out var peer)) return;

            bool wasConnected = peer.Confirmed != null;
            peer.LastHeard = DateTime.UtcNow;

            // Chegou pacote DESTE endereço: então este é o caminho que
            // funciona. Pode ser diferente do que o STUN previu (roteador que
            // remapeia), e é por isso que confiamos no que chegou, não no que
            // foi anunciado.
            if (!from.Equals(peer.Confirmed))
            {
                peer.Confirmed = from;
            }

            // Responder ao "alô" é o que abre a passagem do nosso lado.
            if (type == TypePunch) SendContact(TypePunchAck, from);

            if (!wasConnected) OnPeerStateChanged?.Invoke(userId, true);
            return;
        }

        if (type == PeerPacket.TypeMedia)
        {
            // Descobre de quem é pelo endereço: mídia chega o tempo todo e
            // carimbar o remetente em cada pacote seria desperdício de banda.
            foreach (var peer in _peers.Values)
            {
                if (!from.Equals(peer.Confirmed)) continue;

                peer.LastHeard = DateTime.UtcNow;
                var header = PeerPacket.ReadHeader(buffer, length);
                if (header == null) return;

                var copy = new byte[length];
                Buffer.BlockCopy(buffer, 0, copy, 0, length);

                bool isVideo = header.Kind == PeerPacket.KindVideo;
                var reassembler = isVideo ? peer.VideoReassembler : peer.AudioReassembler;

                byte[]? frame = reassembler.Accept(copy, length);
                if (frame != null) OnFrame?.Invoke(peer.UserId, header.Kind, frame);

                // Perdeu quadro de vídeo? Avisa, pra quem decodifica poder
                // esperar o próximo quadro-chave em vez de seguir com a
                // corrente quebrada (ver FrameReassembler.TakeLoss).
                if (isVideo && reassembler.TakeLoss()) OnVideoLoss?.Invoke(peer.UserId);
                return;
            }
        }
    }

    // ---------- manutenção: furar, manter aberto, perceber queda ----------

    private void MaintenanceLoop()
    {
        DateTime lastKeepalive = DateTime.MinValue;

        while (_running)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                bool keepaliveDue = now - lastKeepalive >= KeepaliveInterval;
                if (keepaliveDue) lastKeepalive = now;

                foreach (var peer in _peers.Values)
                {
                    if (peer.Confirmed == null)
                    {
                        // Ainda procurando: bate em TODOS os endereços
                        // possíveis dele ao mesmo tempo. Quem responder
                        // primeiro vira o caminho.
                        List<IPEndPoint> candidates;
                        lock (peer.Candidates) candidates = peer.Candidates.ToList();
                        foreach (var candidate in candidates) SendContact(TypePunch, candidate);

                        // Indício de mesma rede e mesmo assim não furou depois
                        // de um tempo razoável: a causa mais provável deixa de
                        // ser "internet ruim" e passa a ser o roteador
                        // separando os dois aparelhos (isolamento de
                        // cliente/AP, comum em redes de convidado e em alguns
                        // roteadores de operadora). Avisa uma vez só.
                        if (peer.SameNetworkHint && !peer.StuckNotified
                            && now - peer.FirstSeenAt > SameNetworkStuckAfter)
                        {
                            peer.StuckNotified = true;
                            OnSameNetworkStuck?.Invoke(peer.UserId);
                        }
                        continue;
                    }

                    if (now - peer.LastHeard > PeerTimeout)
                    {
                        // Sumiu: volta a procurar do zero, inclusive nos
                        // outros endereços (a rede dele pode ter mudado).
                        // Reinicia também a contagem do aviso de "mesma rede
                        // travada": se voltar a ficar preso, avisa de novo.
                        peer.Confirmed = null;
                        peer.FirstSeenAt = now;
                        peer.StuckNotified = false;
                        OnPeerStateChanged?.Invoke(peer.UserId, false);
                        continue;
                    }

                    if (keepaliveDue) SendContact(TypeKeepalive, peer.Confirmed);
                }
            }
            catch { /* nada aqui pode derrubar a thread */ }

            Thread.Sleep(PunchInterval);
        }
    }

    // ---------- pacotes de contato ----------

    // [tipo][assinatura 16][identidade]
    //
    // A assinatura prova que quem mandou conhece o código da sala. Sem ela,
    // qualquer pacote que chegasse por acaso na porta poderia ser tomado por
    // um amigo — e a partir daí o app aceitaria vídeo daquele endereço.
    private void SendContact(byte type, IPEndPoint target)
    {
        if (_socket == null) return;

        byte[] id = Encoding.UTF8.GetBytes(_myUserId);
        var packet = new byte[1 + SignatureSize + id.Length];
        packet[0] = type;
        Buffer.BlockCopy(Sign(type, id), 0, packet, 1, SignatureSize);
        Buffer.BlockCopy(id, 0, packet, 1 + SignatureSize, id.Length);

        try { _socket.SendTo(packet, target); } catch { }
    }

    private string? ReadContactPacket(byte[] buffer, int length, byte type)
    {
        if (length <= 1 + SignatureSize) return null;

        int idLength = length - 1 - SignatureSize;
        if (idLength > 128) return null;

        var id = new byte[idLength];
        Buffer.BlockCopy(buffer, 1 + SignatureSize, id, 0, idLength);

        byte[] expected = Sign(type, id);
        // Comparação de tempo fixo: não é crítico aqui, mas comparar
        // assinatura com "==" é um hábito ruim que uma hora cobra caro.
        if (!CryptographicOperations.FixedTimeEquals(
                new ReadOnlySpan<byte>(buffer, 1, SignatureSize), expected))
        {
            return null;
        }

        return Encoding.UTF8.GetString(id);
    }

    private byte[] Sign(byte type, byte[] id)
    {
        var data = new byte[1 + id.Length];
        data[0] = type;
        Buffer.BlockCopy(id, 0, data, 1, id.Length);

        using var hmac = new HMACSHA256(_roomKey);
        byte[] full = hmac.ComputeHash(data);

        var truncated = new byte[SignatureSize];
        Buffer.BlockCopy(full, 0, truncated, 0, SignatureSize);
        return truncated;
    }

    // ---------- encerramento ----------

    public void Stop()
    {
        _running = false;
        try { _socket?.Close(); } catch { }
        _socket = null;

        _hasWork.Set();

        foreach (var t in new[] { _receiveThread, _maintenanceThread, _sendThread })
        {
            if (t != null && t != Thread.CurrentThread) t.Join(1000);
        }
        _receiveThread = null;
        _maintenanceThread = null;
        _sendThread = null;

        lock (_outboundLock) { _videoOutbound.Clear(); _audioOutbound.Clear(); }
        _peers.Clear();
    }

    public void Dispose() => Stop();
}
