using System.Collections.Concurrent;
using System.Net;

namespace VysorClient.Services;

// Decide, PARA CADA AMIGO, por onde o vídeo vai: direto ou pelo servidor.
//
// O QUE MUDOU E POR QUÊ
// Antes, cada quadro que você transmitia subia pro servidor e descia pra cada
// pessoa. Isso funcionava, mas era o servidor pagando a conta — e foi o que
// esgotou o plano grátis duas vezes. Agora o vídeo vai direto do seu PC pro
// PC de cada amigo, e o servidor só apresenta um ao outro.
//
// A REGRA, POR PESSOA E NÃO PELO GRUPO
// O caminho direto não fecha em 100% dos casos: às vezes um par específico
// não consegue, mesmo com todo o resto funcionando. Por isso a decisão é
// individual — o amigo com quem o caminho direto fechou recebe direto; o
// outro continua recebendo pelo servidor, sem perceber diferença. Ninguém
// fica sem ver ninguém por causa de uma rede teimosa.
//
// Enquanto o caminho direto está sendo negociado (leva menos de um segundo),
// tudo vai pelo servidor. Assim a transmissão começa na hora e vai migrando
// sozinha, em vez de ficar esperando parada.
public class PeerMedia
{
    private readonly SignalRService _signalR;
    private PeerTransport? _transport;
    private string _roomCode = "";

    // Quem está na sala, pra saber pra quem mandar. O transporte só conhece
    // quem já anunciou endereço; esta lista conhece todo mundo.
    private readonly ConcurrentDictionary<string, byte> _participants = new();

    // Chegou vídeo/áudio de alguém — não importa se veio direto ou pelo
    // servidor. Quem escuta trata igual, e é isso que faz os dois caminhos
    // conviverem sem o resto do app precisar saber.
    public event Action<string, byte[]>? OnVideo;
    public event Action<string, byte[]>? OnAudio;

    // Mudou quantos amigos estão no caminho direto (pra tela poder mostrar).
    public event Action? OnPathsChanged;

    public PeerMedia(SignalRService signalR)
    {
        _signalR = signalR;
    }

    public bool IsRunning => _transport != null;

    public int DirectPeers => _transport?.ConnectedPeers.Count ?? 0;
    public int TotalPeers => _participants.Count;

    public bool IsDirect(string userId) => _transport?.IsConnected(userId) == true;

    // ---------- ciclo de vida ----------

    public void Start(string roomCode)
    {
        if (_transport != null && _roomCode == roomCode) return;
        Stop();

        _roomCode = roomCode;

        // A chave nasce do código da sala: quem não foi convidado não
        // consegue ler o que trafega entre vocês.
        var transport = new PeerTransport(_signalR.UserId, PeerPacket.DeriveKey(roomCode));
        transport.OnFrame += HandleDirectFrame;
        transport.OnPeerStateChanged += (_, _) => OnPathsChanged?.Invoke();

        if (!transport.Start())
        {
            // Não conseguiu abrir o canal direto: tudo continua pelo servidor,
            // exatamente como funcionava antes. Nada quebra.
            return;
        }

        _transport = transport;

        // Aplica o que chegou antes de o transporte existir (ver
        // _earlyCandidates).
        foreach (var (userId, candidates) in _earlyCandidates)
        {
            AddPeerCandidates(userId, candidates);
        }
        _earlyCandidates.Clear();

        // Descobrir o próprio endereço externo demora um pouco (fala com
        // servidores na internet), então roda fora do caminho de quem chamou.
        _ = Task.Run(AnnounceMyselfAsync);
    }

    public void Stop()
    {
        var transport = _transport;
        _transport = null;
        _participants.Clear();
        _earlyCandidates.Clear();
        _roomCode = "";

        if (transport == null) return;
        transport.OnFrame -= HandleDirectFrame;
        transport.Stop();
    }

    // ---------- quem está na sala ----------

    public void SetParticipants(IEnumerable<string> userIds)
    {
        _participants.Clear();
        foreach (string id in userIds)
        {
            if (id != _signalR.UserId) _participants[id] = 1;
        }
    }

    public void AddParticipant(string userId)
    {
        if (userId != _signalR.UserId) _participants[userId] = 1;
    }

    public void RemoveParticipant(string userId)
    {
        _participants.TryRemove(userId, out _);
        _transport?.RemovePeer(userId);
        OnPathsChanged?.Invoke();
    }

    // ---------- endereços ----------

    // Anuncia em DUAS ETAPAS, e a ordem importa.
    //
    // Descobrir o endereço externo depende de falar com servidores na
    // internet, e isso pode levar segundos — ou nunca responder, se a rede da
    // pessoa bloquear esse tipo de tráfego. Esperar por ele pra só então
    // anunciar qualquer coisa custava quase 10 segundos parados quando o
    // STUN não respondia (medido). Nesse tempo todo o vídeo ia pelo servidor.
    //
    // Então mandamos primeiro o que já se sabe na hora (o endereço de casa) e
    // depois completamos com o externo. Quem está no mesmo Wi-Fi conecta
    // imediatamente; quem está longe conecta assim que o externo chega.
    private async Task AnnounceMyselfAsync()
    {
        try
        {
            int localPort = _transport?.LocalEndPoint?.Port ?? 0;
            if (localPort == 0) return;

            var local = new List<string>();
            foreach (var address in LocalAddresses.List())
            {
                local.Add($"{address.Address}:{localPort}");
                if (local.Count >= 3) break;
            }

            if (local.Count > 0) await _signalR.AnnounceCandidatesAsync(local.ToArray());

            // Agora o demorado: como eu apareço na internet. É este endereço
            // que serve pros amigos de outra casa.
            string? external = await Task.Run(() => DiscoverExternal(localPort));
            if (external == null) return;

            var all = new List<string> { external };
            all.AddRange(local);
            await _signalR.AnnounceCandidatesAsync(all.ToArray());
        }
        catch
        {
            // Sem endereço anunciado, tudo continua indo pelo servidor.
        }
    }

    // Usa o MESMO socket do transporte pra perguntar "qual é meu endereço?".
    // Isso é essencial e não é detalhe: o endereço externo pertence ao socket,
    // não ao computador. Perguntando por outro socket, a resposta seria um
    // endereço que não leva a lugar nenhum — os amigos bateriam numa porta
    // fechada e nada funcionaria, sem erro nenhum aparecendo.
    private string? DiscoverExternal(int localPort)
    {
        var transport = _transport;
        if (transport == null) return null;

        foreach (var (host, port) in StunClient.PublicServers)
        {
            var server = StunClient.Resolve(host, port);
            if (server == null) continue;

            var mapping = transport.QueryStun(server, TimeSpan.FromSeconds(3));
            if (mapping != null) return mapping.ToString();
        }
        return null;
    }

    // Endereços que chegaram ANTES de o transporte estar de pé.
    //
    // Isto não é caso raro: ao entrar numa sala, o servidor manda na hora os
    // endereços de quem já estava lá — e essa mensagem pode chegar antes de o
    // app terminar de ligar o transporte. Sem guardar, esses endereços se
    // perdiam e a pessoa nunca conseguia falar direto com quem já estava na
    // sala (só com quem entrasse depois dela). Descoberto num teste em que a
    // troca de endereços ficou mais rápida e passou a ganhar a corrida.
    private readonly ConcurrentDictionary<string, string[]> _earlyCandidates = new();

    // Chegaram os endereços de um amigo: passa pro transporte começar a furar.
    public void AddPeerCandidates(string userId, string[] candidates)
    {
        if (userId == _signalR.UserId) return;

        if (_transport == null)
        {
            _earlyCandidates[userId] = candidates;
            return;
        }

        var endpoints = new List<IPEndPoint>();
        foreach (string text in candidates)
        {
            if (TryParse(text, out var endpoint)) endpoints.Add(endpoint!);
        }

        if (endpoints.Count == 0) return;
        AddParticipant(userId);
        _transport.AddPeer(userId, endpoints);
    }

    internal static bool TryParse(string text, out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        int colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return false;

        if (!IPAddress.TryParse(text[..colon], out var address)) return false;
        if (!int.TryParse(text[(colon + 1)..], out int port)) return false;
        if (port <= 0 || port > 65535) return false;

        endpoint = new IPEndPoint(address, port);
        return true;
    }

    // ---------- envio ----------

    public void SendVideo(byte[] taggedFrame) => Send(PeerPacket.KindVideo, taggedFrame);
    public void SendAudio(byte[] chunk) => Send(PeerPacket.KindAudio, chunk);

    private void Send(byte kind, byte[] payload)
    {
        foreach (string userId in _participants.Keys)
        {
            if (_transport != null && _transport.IsConnected(userId))
            {
                _transport.Send(userId, kind, payload);
            }
            else if (kind == PeerPacket.KindVideo)
            {
                _ = _signalR.SendFrameToAsync(userId, payload);
            }
            else
            {
                _ = _signalR.SendAudioChunkToAsync(userId, payload);
            }
        }
    }

    private void HandleDirectFrame(string userId, byte kind, byte[] payload)
    {
        if (kind == PeerPacket.KindVideo) OnVideo?.Invoke(userId, payload);
        else OnAudio?.Invoke(userId, payload);
    }
}
