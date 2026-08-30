using Microsoft.AspNetCore.SignalR;

namespace VysorServer.Hubs;

public class RoomHub : Hub
{
    private readonly RoomManager _roomManager;

    // Usado só por BroadcastSuccessionAsync pra falar com a sala fora do
    // fluxo normal do hub (ex: quando alguém anuncia endereço, sem ninguém
    // ter chamado nada que espere resposta). O "Clients" normal do Hub não
    // pode ser usado depois que o método termina, porque o objeto do hub é
    // descartado; o IHubContext é global e vale a qualquer momento.
    private readonly IHubContext<RoomHub> _hubContext;

    private readonly DiscordAnnouncer _discord;

    public RoomHub(RoomManager roomManager, IHubContext<RoomHub> hubContext, DiscordAnnouncer discord)
    {
        _roomManager = roomManager;
        _hubContext = hubContext;
        _discord = discord;
    }

    // Pede pro servidor avisar o canal do Discord que esta sala existe.
    //
    // POR QUE PASSA POR AQUI E NÃO POR UMA ROTA HTTP SOLTA
    // Uma rota aberta do tipo "/anunciar?codigo=X" deixaria QUALQUER UM na
    // internet publicar no canal à vontade, só chutando endereços — o canal
    // viraria mural de spam de estranhos. Aqui não: o servidor não acredita no
    // que o cliente diz. Ele olha em qual sala esta conexão está de verdade
    // (ver TrackConnection) e só anuncia essa. Quem não está numa sala não
    // consegue anunciar nada.
    //
    // Devolve false quando o bot não está configurado — e é esse false que faz
    // o app cair sozinho no caminho antigo (webhook embutido no cliente).
    // channelId é o canal que ESTA pessoa escolheu ao instalar o Vysor no
    // servidor dela (ver /discord/instalado). Vazio = usa o canal fixo do
    // servidor, se houver — é o que atende quem nunca configurou nada.
    public async Task<bool> AnnounceRoomOnDiscord(string displayName, string? channelId)
    {
        var info = _roomManager.GetConnectionInfo(Context.ConnectionId);
        if (info == null) return false;

        var room = _roomManager.GetRoom(info.RoomCode);
        if (room == null) return false;

        return string.IsNullOrWhiteSpace(channelId)
            ? await _discord.AnnounceRoomAsync(room.Code, displayName)
            : await _discord.AnnounceRoomAsync(room.Code, displayName, channelId);
    }

    // Tira a conexão da sala em que ela está. Usado quando a pessoa entra em
    // outra sala ou sai de propósito — nunca por causa de uma queda de
    // conexão (esse caso é tratado em OnDisconnectedAsync, que dá um tempo
    // pra pessoa voltar).
    private async Task LeaveCurrentRoomAsync()
    {
        var info = _roomManager.GetConnectionInfo(Context.ConnectionId);
        if (info == null) return;

        var previousRoom = _roomManager.GetRoom(info.RoomCode);
        if (previousRoom != null && previousRoom.Participants.TryRemove(info.UserId, out _))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, info.RoomCode);
            await Clients.OthersInGroup(info.RoomCode).SendAsync("UserLeft", info.UserId);
            _roomManager.MarkRoomEmptyIfNeeded(info.RoomCode);

            // Quem saiu de propósito sai também da fila de sucessão, na hora.
            await BroadcastSuccessionAsync(previousRoom);
        }

        _roomManager.UntrackConnection(Context.ConnectionId);
    }

    public async Task CreateRoom(string userId, string displayName)
    {
        userId = NormalizeUserId(userId, Context.ConnectionId);
        await LeaveCurrentRoomAsync();

        var room = _roomManager.CreateRoom(null);
        await AddCallerToRoomAsync(room, userId, displayName);

        // Manda de volta a identidade que o app deve usar pra si mesmo. É
        // esse identificador (e não o nome de exibição) que organiza tudo:
        // duas pessoas podem ter o mesmo nome sem se confundirem.
        await Clients.Caller.SendAsync("RoomCreated", room.Code, userId);
    }

    public async Task JoinRoom(string code, string userId, string displayName)
    {
        code = NormalizeCode(code);
        userId = NormalizeUserId(userId, Context.ConnectionId);

        var room = _roomManager.GetRoom(code);
        if (room == null)
        {
            await Clients.Caller.SendAsync("Error", "Sala não encontrada.");
            return;
        }

        if (IsFullFor(room, userId))
        {
            await Clients.Caller.SendAsync("Error", "Essa sala já está cheia.");
            return;
        }

        await JoinInternalAsync(room, userId, displayName);
    }

    // Reentrada automática depois de uma queda de conexão.
    //
    // Diferente do JoinRoom, aqui a sala é RECRIADA se não existir mais. Isso
    // é o que impede o pior problema que apareceu em uso real: bastava a
    // conexão de todo mundo cair junto (um engasgo de rede, o servidor
    // reiniciando) pra sala ficar vazia e ser destruída — e aí, quando os
    // apps voltavam sozinhos, todos recebiam "Sala não encontrada" ao mesmo
    // tempo e aquele código ficava morto pra sempre. Como só o app chama este
    // método (e só com um código em que ele já estava), não há risco de um
    // erro de digitação criar salas fantasmas.
    public async Task RejoinRoom(string code, string userId, string displayName)
    {
        code = NormalizeCode(code);
        userId = NormalizeUserId(userId, Context.ConnectionId);

        if (code.Length == 0)
        {
            await Clients.Caller.SendAsync("Error", "Sala não encontrada.");
            return;
        }

        var room = _roomManager.GetOrCreateRoom(code);

        if (IsFullFor(room, userId))
        {
            await Clients.Caller.SendAsync("Error", "Essa sala já está cheia.");
            return;
        }

        await JoinInternalAsync(room, userId, displayName);
    }

    private async Task JoinInternalAsync(Room room, string userId, string displayName)
    {
        // Já estava nesta sala? Então isto é uma reconexão: ninguém precisa
        // ser avisado de nada, e as telinhas de quem está assistindo essa
        // pessoa continuam funcionando sem interrupção.
        bool isReturning = room.Participants.ContainsKey(userId);

        // Se a conexão anterior desta mesma pessoa ainda estiver registrada
        // (em outra sala), desfaz o vínculo antigo primeiro.
        var previous = _roomManager.GetConnectionInfo(Context.ConnectionId);
        if (previous != null && previous.RoomCode != room.Code)
        {
            await LeaveCurrentRoomAsync();
        }

        await AddCallerToRoomAsync(room, userId, displayName);
        await SendRoomJoinedAsync(room, userId, displayName, isReturning);
    }

    private static string NormalizeCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    // Se um app antigo (ou com defeito) não mandar identidade, usamos o
    // identificador da conexão como reserva — funciona, só perde o benefício
    // de sobreviver a uma reconexão.
    private static string NormalizeUserId(string? userId, string fallback)
    {
        userId = (userId ?? string.Empty).Trim();
        return userId.Length == 0 ? fallback : userId;
    }

    // Não conta como "cheia" se quem está entrando já está na sala (é o caso
    // de uma reconexão automática logo depois de uma queda).
    private static bool IsFullFor(Room room, string userId)
        => room.Participants.Count >= RoomManager.MaxParticipantsPerRoom
           && !room.Participants.ContainsKey(userId);

    private async Task AddCallerToRoomAsync(Room room, string userId, string displayName)
    {
        // Se a pessoa já existia (reconexão), o registro dela é REAPROVEITADO,
        // só trocando qual conexão está ativa e limpando a marca de "caiu".
        room.Participants.AddOrUpdate(
            userId,
            _ => new Participant
            {
                UserId = userId,
                ConnectionId = Context.ConnectionId,
                DisplayName = displayName,
                // Numera a chegada. É isto que define a fila de sucessão —
                // quem assume a sala se o host fechar o app.
                JoinOrder = room.NextJoinOrder()
            },
            (_, existing) =>
            {
                existing.ConnectionId = Context.ConnectionId;
                existing.DisplayName = displayName;
                existing.DisconnectedAt = null;
                // JoinOrder e Address NÃO são mexidos de propósito: quem volta
                // de uma queda mantém o lugar que já tinha na fila.
                return existing;
            });

        _roomManager.MarkRoomActive(room);

        await Groups.AddToGroupAsync(Context.ConnectionId, room.Code);
        _roomManager.TrackConnection(Context.ConnectionId, room.Code, userId);
    }

    private async Task SendRoomJoinedAsync(Room room, string userId, string displayName, bool isReturning)
    {
        // Duas listas paralelas (identidades e nomes): é a identidade que
        // organiza tudo no app, o nome é só pra mostrar na tela.
        //
        // As duas SAEM DA MESMA fotografia da lista de propósito. Ler
        // "Participants.Values" duas vezes tirava duas fotos em momentos
        // diferentes; se alguém entrasse ou saísse no meio, as listas ficavam
        // com tamanhos ou ordens diferentes — e como o app junta as duas por
        // posição, o nome de uma pessoa acabava colado na identidade de
        // outra (você clicaria em "Bruno" e assistiria a Ana).
        var snapshot = room.Participants.Values.ToList();
        var ids = snapshot.Select(p => p.UserId).ToList();
        var names = snapshot.Select(p => p.DisplayName).ToList();

        await Clients.Caller.SendAsync("RoomJoined", room.Code, userId, ids, names);

        // Só avisa os outros quando é alguém realmente novo. Numa reconexão,
        // os outros nunca souberam que a pessoa saiu (ela ficou na sala
        // durante o prazo de tolerância), então avisar de novo criaria um
        // participante duplicado na lista deles.
        if (!isReturning)
        {
            await Clients.OthersInGroup(room.Code).SendAsync("UserJoined", userId, displayName);
        }

        await BroadcastSuccessionAsync(room);

        // Quem chega precisa saber onde os que já estavam podem ser
        // alcançados — senão só conseguiria falar com quem entrar depois dele.
        await SendExistingCandidatesAsync(room, userId);
    }

    // Diz quem informou um endereço em que pode ser alcançado, e em que ordem.
    //
    // ESTA É A PEÇA QUE MANTÉM A SALA VIVA QUANDO O HOST SAI. Enquanto o host
    // está de pé, ele é o único ponto de encontro — e se ele fechar o app sem
    // ninguém ter guardado pra onde ir, a sala simplesmente acaba, mesmo com
    // todo mundo ainda online. Mandando a fila ANTES de precisar dela, cada
    // app já sabe sozinho qual é o próximo endereço a tentar. Ninguém precisa
    // combinar nada na hora (o que seria impossível: quem coordenava sumiu).
    //
    // Só entra quem realmente pode receber os outros. Quem não anunciou
    // endereço — porque está atrás de uma rede que não deixa, ou porque usa
    // uma versão antiga do app — participa da sala normalmente, apenas nunca
    // é escolhido como sucessor.
    private async Task BroadcastSuccessionAsync(Room room)
    {
        var candidates = room.Participants.Values
            .Where(p => !string.IsNullOrWhiteSpace(p.Address))
            .OrderBy(p => p.JoinOrder)
            .ToList();

        await _hubContext.Clients.Group(room.Code).SendAsync(
            "RoomSuccession",
            candidates.Select(p => p.UserId).ToList(),
            candidates.Select(p => p.DisplayName).ToList(),
            candidates.Select(p => p.Address!).ToList());
    }

    // ---- troca de endereços para conexão DIRETA entre os PCs ----
    //
    // Este é o serviço mais importante que o servidor presta agora, e também
    // o mais barato. O vídeo não passa mais por aqui: ele vai direto de um
    // computador pro outro. Mas pra isso os dois precisam saber em que
    // endereço o outro aparece na internet — e é impossível combinarem isso
    // sozinhos antes de conseguirem se falar (o ovo e a galinha). Alguém
    // precisa apresentar um ao outro. É só isso que acontece aqui.
    //
    // Tamanho do que trafega: dois textos curtos por pessoa, uma vez por
    // sessão. O vídeo que passava por aqui antes era medido em gigabytes.
    public async Task AnnounceCandidates(string[] candidates)
    {
        var info = _roomManager.GetConnectionInfo(Context.ConnectionId);
        if (info == null) return;

        var room = _roomManager.GetRoom(info.RoomCode);
        if (room == null) return;
        if (!room.Participants.TryGetValue(info.UserId, out var participant)) return;

        // Limites pra um cliente com defeito (ou mal-intencionado) não usar
        // isto como mural de recados: o servidor guarda tudo em memória.
        var cleaned = (candidates ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c) && c.Length <= 64)
            .Select(c => c.Trim())
            .Distinct()
            .Take(8)
            .ToArray();

        participant.Candidates = cleaned;

        await Clients.OthersInGroup(room.Code)
            .SendAsync("PeerCandidates", info.UserId, cleaned);
    }

    // Manda pra quem acabou de entrar os endereços de quem JÁ estava na sala.
    //
    // Sem isto, só quem chega depois seria alcançável: os que já estavam
    // anunciaram antes de você existir, e aquele aviso passou. Você entraria
    // numa sala cheia sem conseguir falar direto com ninguém.
    private async Task SendExistingCandidatesAsync(Room room, string newcomerId)
    {
        foreach (var other in room.Participants.Values)
        {
            if (other.UserId == newcomerId) continue;
            if (other.Candidates.Length == 0) continue;

            await Clients.Caller.SendAsync("PeerCandidates", other.UserId, other.Candidates);
        }
    }

    // O app avisa em que endereço ele pode receber os amigos, caso precise
    // assumir a sala. Chamado logo depois de entrar.
    //
    // É um método separado (e não um parâmetro a mais no JoinRoom) de
    // propósito: assim um app mais antigo, que não conhece esta parte,
    // continua entrando na sala e funcionando normalmente. Ele só não entra
    // na fila de sucessão.
    public async Task AnnounceAddress(string address)
    {
        var info = _roomManager.GetConnectionInfo(Context.ConnectionId);
        if (info == null) return;

        var room = _roomManager.GetRoom(info.RoomCode);
        if (room == null) return;
        if (!room.Participants.TryGetValue(info.UserId, out var participant)) return;

        address = (address ?? string.Empty).Trim();
        if (address.Length > 200) return;   // entrada absurda: ignora

        if (participant.Address == address) return;   // nada mudou

        participant.Address = address.Length == 0 ? null : address;
        await BroadcastSuccessionAsync(room);
    }

    // Sai da sala em que a conexão realmente está, ignorando o código que o
    // cliente mandou. Antes, se viesse um código errado/desatualizado, o
    // vínculo era esquecido mesmo assim: a pessoa continuava na lista de
    // participantes da sala de verdade, mas o servidor esquecia onde ela
    // estava — e ela virava um fantasma permanente na lista de todo mundo.
    public async Task LeaveRoom(string code)
    {
        await LeaveCurrentRoomAsync();
    }

    // Avisa os outros da sala que essa pessoa parou de compartilhar — sem
    // isso, quem estava assistindo ficava com o último quadro congelado na
    // tela sem nenhum aviso, e o "play" ao lado do nome continuava marcado
    // como se a transmissão ainda estivesse rolando.
    public async Task StopSharing()
    {
        var info = _roomManager.GetConnectionInfo(Context.ConnectionId);
        if (info == null) return;

        await Clients.OthersInGroup(info.RoomCode).SendAsync("UserStoppedSharing", info.UserId);
    }

    // ---- NENHUM vídeo ou áudio passa por aqui, de propósito ----
    //
    // Este servidor NÃO tem (e não deve voltar a ter) um método que receba
    // frame_bytes/audio_bytes e os repasse pra outro cliente. Já existiu um
    // caminho de reserva assim (SendScreenFrameTo/SendAudioChunkTo, repassando
    // só o PAR que não conseguisse furar o NAT) e foi ele — mais o caminho
    // amplo que existia antes dele — que estourou o plano grátis do Render
    // duas vezes: o Android nunca soube descobrir o próprio endereço externo
    // (sem STUN), então qualquer sessão em que o celular não estivesse na
    // MESMA rede local do PC caía neste "reserva" e ficava nele a sessão
    // inteira, sem nunca migrar pro caminho direto.
    //
    // A decisão agora é: se o caminho direto (PeerTransport, furo de NAT) não
    // fechar, o quadro é DESCARTADO no cliente — nunca mais vem parar aqui.
    // Structurally, este servidor só processa texto curto (códigos de sala,
    // endereços, listas de participantes); ele é incapaz de mover gigabytes
    // mesmo que um cliente antigo ou malicioso tente chamar um método de
    // repasse, porque esse método simplesmente não existe mais no Hub.
    //
    // Queda de conexão. A pessoa NÃO é removida da sala na hora: ela fica
    // marcada como "caiu" e tem alguns segundos pra voltar (o app tenta
    // reconectar sozinho). Se voltar, ninguém percebe que houve interrupção —
    // é isso que evita a transmissão dela "sumir" e os amigos terem que
    // clicar em assistir de novo. Quem não voltar dentro do prazo é removido
    // pela limpeza periódica (RoomSweeper), que aí sim avisa os outros.
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var info = _roomManager.GetConnectionInfo(Context.ConnectionId);
        if (info != null)
        {
            var room = _roomManager.GetRoom(info.RoomCode);
            if (room != null && room.Participants.TryGetValue(info.UserId, out var participant))
            {
                // Só marca como caída se a conexão que caiu ainda é a atual
                // dessa pessoa. Se ela já reconectou por outra conexão, a
                // queda da conexão velha não significa nada.
                if (participant.ConnectionId == Context.ConnectionId)
                {
                    participant.DisconnectedAt = DateTimeOffset.UtcNow;
                }
            }

            _roomManager.UntrackConnection(Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
