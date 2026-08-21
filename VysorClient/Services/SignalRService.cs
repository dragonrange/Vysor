using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace VysorClient.Services;

public class SignalRService
{
    // O ponto de encontro da sala.
    //
    // ATENÇÃO ao que este servidor faz AGORA, porque mudou: ele não carrega
    // mais o vídeo de ninguém. O vídeo vai direto de um PC pro outro (ver
    // PeerTransport). O que passa por aqui é só "quem está na sala" e "em que
    // endereço cada um pode ser encontrado" — uns poucos KB por pessoa, uma
    // vez por sessão. Era o vídeo que estourava o limite do plano grátis;
    // sem ele, este servidor cabe de sobra em qualquer plano gratuito.
    //
    // Dá pra trocar o endereço sem recompilar: basta um arquivo "server.txt"
    // do lado do Vysor.exe com a URL nova.
    private const string DefaultServerUrl =
        "https://vysorserver.onrender.com/roomhub";

    private HubConnection? _connection;

    // Endereço do servidor a que estamos ligados AGORA. Guardado porque, com a
    // sala podendo trocar de dono, "o servidor" deixou de ser um lugar fixo.
    public string? CurrentUrl { get; private set; }

    // Verdadeiro quando é ESTE PC que está hospedando a sala.
    public bool IsHostingHere =>
        CurrentUrl != null &&
        CurrentUrl.Equals(LocalServer.LoopbackUrl, StringComparison.OrdinalIgnoreCase);

    // Identidade estável deste app, sorteada uma vez quando ele abre e mantida
    // até fechar.
    //
    // É ela — e NÃO o identificador da conexão — que diz "quem é você" na
    // sala. A diferença importa muito: o identificador da conexão muda toda
    // vez que a conexão cai e volta (o que acontece sozinho de vez em quando,
    // inclusive quando o servidor reinicia, sem a sua internet ter caído). Como
    // antes a identidade era a da conexão, cada reconexão fazia você virar uma
    // "pessoa nova" pra sala: quem estava te assistindo perdia a imagem e
    // precisava clicar em assistir de novo. Com esta identidade fixa, a
    // reconexão passa despercebida.
    public string UserId { get; }

    // O parâmetro existe só pra dar pra fixar a identidade em teste (e, um
    // dia, pra guardar a mesma identidade entre execuções). No uso normal
    // ninguém passa nada e cada execução sorteia a sua.
    public SignalRService(string? userId = null)
    {
        UserId = string.IsNullOrWhiteSpace(userId) ? Guid.NewGuid().ToString("N") : userId!;
    }

    // Em todos os eventos abaixo, o "id" é a identidade ESTÁVEL do
    // participante (o UserId acima), não o identificador da conexão. O nome de
    // exibição é só pra mostrar na tela; duas pessoas podem escolher o mesmo
    // nome sem problema, porque nada é combinado usando o nome.
    public event Action<string, string>? OnRoomCreated; // code, myId
    public event Action<string, string, List<string>, List<string>>? OnRoomJoined; // code, myId, ids, displayNames
    public event Action<string, string>? OnUserJoined; // id, displayName
    public event Action<string>? OnUserLeft; // id
    public event Action<string>? OnUserStoppedSharing; // id
    public event Action<string, byte[]>? OnFrameReceived; // id, frameBytes
    public event Action<string, byte[]>? OnAudioChunkReceived; // id, audioBytes

    // Mensagem de erro vinda do servidor (ex: "Sala não encontrada").
    public event Action<string>? OnServerError;

    // Disparado quando a conexão caiu e voltou sozinha. Quem escuta precisa
    // entrar na sala de novo (com RejoinRoomAsync): o servidor mantém a
    // pessoa na sala por alguns segundos esperando ela voltar, mas é a
    // reentrada que reassocia a conexão nova à identidade de sempre. Sem
    // isso, o app continuaria mostrando a sala normalmente sem mandar nem
    // receber mais nada.
    public event Action? OnReconnected;

    // A conexão caiu de vez (o SignalR já tentou voltar sozinho e desistiu).
    // Quem escuta deve procurar o próximo host na fila de sucessão — é isto
    // que faz a sala sobreviver a quem estava hospedando fechar o app.
    public event Action? OnConnectionLost;

    // A fila de sucessão mudou (alguém entrou, saiu, ou anunciou endereço).
    public event Action? OnSuccessionChanged;

    // Chegaram os endereços por onde dá pra falar DIRETO com um amigo. É com
    // isso que o vídeo deixa de passar pelo servidor.
    public event Action<string, string[]>? OnPeerCandidates;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    // Servidor fixo, se você tiver um.
    //
    // Lê de um arquivo "server.txt" na mesma pasta do Vysor.exe (uma linha só,
    // com a URL terminando em /roomhub). Existindo esse arquivo, ele manda em
    // tudo: o app usa esse endereço e ignora a hospedagem local. É a saída
    // pra quem um dia voltar a ter um servidor na internet — e também um jeito
    // de apontar todo mundo pro PC de um amigo específico, sem depender da
    // fila de sucessão.
    public static string? GetFixedServerUrl()
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server.txt");
            if (File.Exists(path))
            {
                string url = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
        }
        catch { /* sem arquivo: usa o endereço embutido */ }

        return DefaultServerUrl;
    }

    // A ordem em que vamos tentar entrar numa sala.
    //
    // O servidor vem primeiro: é onde a sala mora no caso normal. Mas ele NÃO
    // é o único caminho, de propósito — se ele estiver fora do ar (foi o que
    // já aconteceu duas vezes com o plano grátis), o app cai de volta pro modo
    // em que a sala mora no PC de alguém do grupo. Vocês não ficam reféns de
    // um serviço que pode sumir.
    public static List<string> BuildCandidateUrls(string userId, string? preferAddress)
    {
        var list = new List<string>();

        // Endereço pedido explicitamente vem PRIMEIRO. Isto estava errado
        // antes: o servidor era sempre o primeiro da fila, então quem pedia
        // "conecte NESTE endereço" (ao entrar por um convite, ou ao assumir a
        // sala depois que o host caiu) acabava conectando no servidor mesmo
        // assim — no lugar errado, em silêncio.
        if (!string.IsNullOrWhiteSpace(preferAddress))
            list.Add(HostDirectory.BuildHubUrl(preferAddress!));

        string? server = GetFixedServerUrl();
        if (server != null && !list.Contains(server)) list.Add(server);

        foreach (string url in HostDirectory.CandidateUrls(userId, null))
            if (!list.Contains(url)) list.Add(url);

        return list;
    }

    private List<string> BuildCandidates(string? preferAddress)
        => BuildCandidateUrls(UserId, preferAddress);

    // Um "semáforo" pra impedir duas tentativas de conexão ao mesmo tempo
    // (o app chama ConnectAsync de vários lugares: na abertura, ao criar
    // sala, ao entrar, ao transmitir).
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    // Conecta na sala.
    //
    // preferAddress é o endereço que veio no convite que a pessoa colou. Se
    // ele não atender (o amigo fechou o app, a rede dele caiu), seguimos pela
    // fila de sucessão até achar alguém de pé — e, se ninguém estiver, a sala
    // sobe aqui mesmo. É este percurso que faz a sala não morrer junto com o
    // host.
    public async Task ConnectAsync(string? preferAddress = null)
    {
        if (IsConnected) return;

        await _connectLock.WaitAsync();
        try
        {
            if (IsConnected) return;

            // Se sobrou uma conexão antiga que não está mais utilizável
            // (tentativa que falhou, ou queda definitiva), descarta antes de
            // montar outra. ESTE ERA UM BUG SÉRIO: a conexão era guardada no
            // campo ANTES de conectar de fato, então se a primeira tentativa
            // falhasse (servidor dormindo, internet fora do ar), todas as
            // tentativas seguintes saíam na hora achando que já havia
            // conexão — e os botões "Criar Sala"/"Entrar" simplesmente
            // paravam de funcionar até fechar e abrir o app de novo.
            // Qualquer conexão que não esteja de pé AGORA é descartada e
            // refeita. Antes, um estado intermediário ("conectando",
            // "reconectando") fazia este método sair calado, e o clique em
            // Criar Sala virava um nada absoluto: sem sala, sem erro, sem
            // explicação. Era preciso clicar de novo até dar sorte de pegar a
            // conexão num estado bom.
            if (_connection != null && _connection.State != HubConnectionState.Connected)
            {
                var stale = _connection;
                _connection = null;
                CurrentUrl = null;
                try { await stale.StopAsync(); } catch { }
                try { await stale.DisposeAsync(); } catch { }
            }

            if (_connection != null) return;   // já conectado: nada a fazer

            var candidates = BuildCandidates(preferAddress);
            Exception? lastError = null;

            foreach (string url in candidates)
            {
                HubConnection connection = BuildConnection(url);

                try
                {
                    await connection.StartAsync();
                }
                catch (Exception ex)
                {
                    // Este endereço não atendeu. Descarta e tenta o próximo —
                    // publicar no campo só depois de conectar de verdade é o
                    // que impede uma tentativa falha de "envenenar" as
                    // seguintes (bug real que travava os botões da tela
                    // inicial até fechar e abrir o app).
                    lastError = ex;
                    try { await connection.DisposeAsync(); } catch { }
                    continue;
                }

                _connection = connection;
                CurrentUrl = url;
                return;
            }

            throw lastError ?? new InvalidOperationException(
                "Não consegui falar com nenhum endereço da sala.");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private HubConnection BuildConnection(string url)
    {
            var connection = new HubConnectionBuilder()
                .WithUrl(url, options =>
                {
                    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                    options.ApplicationMaxBufferSize = 10 * 1024 * 1024;
                    options.TransportMaxBufferSize = 10 * 1024 * 1024;
                })
                // Tentativas de reconexão CURTAS de propósito.
                //
                // O padrão do SignalR é 0s, 2s, 10s e 30s antes de desistir —
                // uns 42 segundos. Fazia sentido quando a sala morava num
                // servidor alugado que sempre voltava. Agora a sala mora no PC
                // de um amigo, e a causa mais comum de queda é ele ter fechado
                // o app: insistir 42 segundos com quem não vai voltar é só
                // tempo de tela parada. Desistindo em ~6s, a busca pelo novo
                // host começa antes — e ela é mais esperta que a insistência,
                // porque sabe procurar em OUTROS endereços (inclusive de volta
                // neste mesmo, se ele só tiver engasgado).
                .WithAutomaticReconnect(new[]
                {
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(4)
                })
                .Build();

            connection.On<string, string>("RoomCreated", (code, myId) => OnRoomCreated?.Invoke(code, myId));
            connection.On<string, string, List<string>, List<string>>("RoomJoined", (code, myId, ids, names) => OnRoomJoined?.Invoke(code, myId, ids, names));
            connection.On<string, string>("UserJoined", (id, displayName) => OnUserJoined?.Invoke(id, displayName));
            connection.On<string>("UserLeft", (id) => OnUserLeft?.Invoke(id));
            connection.On<string>("UserStoppedSharing", (id) => OnUserStoppedSharing?.Invoke(id));
            connection.On<string>("Error", (message) => OnServerError?.Invoke(message));
            connection.On<string, byte[]>("ReceiveScreenFrame", (userId, frameBytes) =>
            {
                OnFrameReceived?.Invoke(userId, frameBytes);
            });
            connection.On<string, byte[]>("ReceiveAudioChunk", (userId, audioBytes) =>
            {
                OnAudioChunkReceived?.Invoke(userId, audioBytes);
            });

            // A fila de sucessão distribuída pelo host: quem mais pode receber
            // a sala, e em que ordem. Guardamos assim que chega, ANTES de
            // precisar — quando o host cair, não vai ter mais ninguém pra
            // perguntar.
            connection.On<string, string[]>("PeerCandidates", (userId, candidates) =>
            {
                OnPeerCandidates?.Invoke(userId, candidates ?? Array.Empty<string>());
            });

            connection.On<List<string>, List<string>, List<string>>(
                "RoomSuccession", (ids, names, addresses) =>
                {
                    HostDirectory.Update(ids, names, addresses);
                    OnSuccessionChanged?.Invoke();
                });

            connection.Reconnected += _ =>
            {
                OnReconnected?.Invoke();
                return Task.CompletedTask;
            };

            // A reconexão automática do SignalR só sabe voltar pro MESMO
            // endereço. Quando ela desiste de vez, é porque aquele host não
            // volta mais — e aí quem assume é a fila de sucessão.
            connection.Closed += _ =>
            {
                OnConnectionLost?.Invoke();
                return Task.CompletedTask;
            };

            return connection;
    }

    public async Task CreateRoomAsync(string displayName)
    {
        // Lança em vez de sair calado. Sem conexão não existe sala, e quem
        // clicou precisa ver um aviso — não uma tela que não reage.
        RequireConnection();
        await _connection!.InvokeAsync("CreateRoom", UserId, displayName);
    }

    private void RequireConnection()
    {
        if (_connection == null || !IsConnected)
            throw new InvalidOperationException("Sem conexão com a sala.");
    }

    public async Task JoinRoomAsync(string code, string displayName)
    {
        RequireConnection();
        await _connection!.InvokeAsync("JoinRoom", code, UserId, displayName);
    }

    // Usado só depois de uma reconexão automática. Duas diferenças em relação
    // ao JoinRoom: o servidor recria a sala se ela tiver deixado de existir
    // enquanto a conexão estava caída (senão o grupo inteiro ficava preso no
    // "Sala não encontrada", com o código morto pra sempre), e, como a
    // identidade enviada é a mesma de antes, o servidor reconhece que é você
    // voltando — ninguém na sala percebe interrupção nenhuma.
    public async Task RejoinRoomAsync(string code, string displayName)
    {
        // try/catch porque quem chama não espera o resultado (é disparado do
        // tratamento de reconexão). Sem isso, uma segunda queda no meio da
        // chamada viraria um erro que ninguém observa.
        try
        {
            if (_connection != null && IsConnected)
                await _connection.InvokeAsync("RejoinRoom", code, UserId, displayName);
        }
        catch { }
    }

    public async Task LeaveRoomAsync(string code)
    {
        if (_connection != null && IsConnected)
            await _connection.InvokeAsync("LeaveRoom", code);
    }

    // Entra numa sala a partir do convite colado ("100.94.12.7/AB12CD").
    //
    // Não vai direto no endereço do convite: primeiro pergunta quem está com
    // a sala viva. Isso importa porque o convite envelhece — quem hospedava
    // pode ter fechado o app e a sala ter passado pra outra pessoa. Sem esta
    // procura, o convite que o amigo mandou de manhã simplesmente não
    // funcionaria mais à tarde, mesmo com a sala aberta e todo mundo lá.
    public async Task EnterRoomAsync(string invite, string displayName)
    {
        var (address, code) = HostDirectory.ParseInvite(invite);
        if (code.Length == 0) throw new ArgumentException("Convite vazio.");

        var found = await RoomLocator.FindAsync(code, UserId, address);

        // Ninguém respondeu: tenta o endereço do convite mesmo assim, pra que
        // o erro que aparecer seja o de verdade (ex: "não consegui alcançar
        // esse computador") em vez de um silêncio.
        string target = found.HubUrl
                        ?? (address != null ? HostDirectory.BuildHubUrl(address) : LocalServer.LoopbackUrl);

        await ConnectToAsync(target);
        await JoinRoomAsync(code, displayName);
        HostDirectory.NoteWorking(target);
    }

    // Cria uma sala nova, no melhor lugar disponível.
    //
    // Normalmente é no servidor (que agora só organiza a sala — o vídeo vai
    // direto entre os PCs). Se ele estiver fora do ar, a sala nasce neste
    // computador mesmo, e o grupo continua funcionando sem depender dele.
    public async Task CreateRoomAnywhereAsync(string displayName)
    {
        // NÃO sondamos antes de conectar, de propósito.
        //
        // A sondagem tinha 2 segundos de paciência — o bastante pra um
        // servidor acordado, mas NÃO pra um que estava dormindo (o Fly
        // desliga a máquina quando ninguém usa, e ela leva alguns segundos
        // pra levantar). Resultado: o primeiro clique dava a sondagem como
        // fracassada e a sala nascia no lugar errado, ou não nascia. Só depois
        // de alguns cliques — quando o servidor já tinha acordado por causa
        // das próprias tentativas — é que funcionava.
        //
        // A tentativa de conexão já é o teste, e ela tem paciência de sobra.
        await ConnectAsync();
        await CreateRoomAsync(displayName);

        if (CurrentUrl != null) HostDirectory.NoteWorking(CurrentUrl);
    }

    // Garante que estamos ligados EXATAMENTE neste endereço.
    //
    // O detalhe que importa: ConnectAsync sai na hora se já houver conexão —
    // o que estava certo quando existia um servidor só. Agora não: dá pra
    // estar conectado no lugar errado (no próprio PC, por exemplo, depois de
    // ter criado uma sala) e ir "entrar" na sala de um amigo. Sem sair da
    // conexão antiga primeiro, o app ficaria parado no lugar de antes e o
    // botão pareceria não fazer nada.
    private async Task ConnectToAsync(string hubUrl)
    {
        if (IsConnected && !string.Equals(CurrentUrl, hubUrl, StringComparison.OrdinalIgnoreCase))
        {
            await DisconnectAsync();
        }
        await ConnectAsync(hubUrl);
    }

    // Diz à sala em que endereço ESTE app pode receber os outros, caso
    // precise assumir a hospedagem. Chamado logo depois de entrar na sala.
    public async Task AnnounceAddressAsync(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        try
        {
            if (_connection != null && IsConnected)
                await _connection.InvokeAsync("AnnounceAddress", address);
        }
        catch { }
    }

    // Derruba a conexão atual de propósito, pra poder tentar outro endereço.
    // Usado quando o host sai e a sala precisa mudar de dono.
    public async Task DisconnectAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            var connection = _connection;
            _connection = null;
            CurrentUrl = null;
            if (connection == null) return;
            try { await connection.StopAsync(); } catch { }
            try { await connection.DisposeAsync(); } catch { }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task StopSharingAsync()
    {
        // try/catch pelo mesmo motivo do RejoinRoomAsync: quem chama dispara
        // e segue em frente, então um erro aqui não teria quem o observasse.
        try
        {
            if (_connection != null && IsConnected)
                await _connection.InvokeAsync("StopSharing");
        }
        catch { }
    }

    // Os dois envios abaixo são chamados em "fire and forget" (sem ninguém
    // esperando o resultado) dezenas de vezes por segundo. O try/catch é
    // essencial: entre checar IsConnected e enviar de fato, a conexão pode
    // cair, e uma exceção numa tarefa que ninguém observa fica invisível —
    // o app parecia continuar transmitindo enquanto nada saía.
    // Anuncia por onde os amigos conseguem falar direto com este PC.
    public async Task AnnounceCandidatesAsync(string[] candidates)
    {
        try
        {
            if (_connection != null && IsConnected)
                await _connection.InvokeAsync("AnnounceCandidates", candidates);
        }
        catch { }
    }

    // Caminho reserva: manda para UMA pessoa através do servidor, quando o
    // caminho direto com ela não fechou. Usado só nesse caso.
    public async Task SendFrameToAsync(string targetUserId, byte[] frameBytes)
    {
        try
        {
            if (_connection != null && IsConnected)
                await _connection.SendAsync("SendScreenFrameTo", targetUserId, frameBytes);
        }
        catch { }
    }

    public async Task SendAudioChunkToAsync(string targetUserId, byte[] audioBytes)
    {
        try
        {
            if (_connection != null && IsConnected)
                await _connection.SendAsync("SendAudioChunkTo", targetUserId, audioBytes);
        }
        catch { }
    }

    public async Task SendFrameAsync(byte[] frameBytes)
    {
        try
        {
            if (_connection != null && IsConnected)
            {
                await _connection.SendAsync("SendScreenFrame", frameBytes);
            }
        }
        catch { }
    }

    public async Task SendAudioChunkAsync(byte[] audioBytes)
    {
        try
        {
            if (_connection != null && IsConnected)
            {
                await _connection.SendAsync("SendAudioChunk", audioBytes);
            }
        }
        catch { }
    }
}
