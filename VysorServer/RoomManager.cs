using System.Collections.Concurrent;

namespace VysorServer;

// Representa uma pessoa dentro de uma sala.
//
// IMPORTANTE — a diferença entre os dois identificadores:
//   UserId       : criado pelo app e o MESMO durante toda a execução dele.
//                  É essa a identidade da pessoa: é por ela que as telinhas,
//                  o áudio e a lista de participantes são organizados.
//   ConnectionId : identificador da conexão atual com o servidor. MUDA toda
//                  vez que a conexão cai e volta.
//
// Usar o ConnectionId como identidade (como era antes) fazia com que qualquer
// reconexão — inclusive as que acontecem sem a internet do usuário cair, como
// quando o servidor reinicia — transformasse a pessoa em um "novo"
// participante: quem estava assistindo perdia a imagem e tinha que clicar em
// assistir de novo. Com o UserId estável, a reconexão passa despercebida.
public class Participant
{
    public required string UserId { get; set; }
    public required string ConnectionId { get; set; }
    public required string DisplayName { get; set; }

    // Quando a conexão dessa pessoa caiu, ou null se ela está conectada.
    // Enquanto estiver dentro do prazo de tolerância, ela continua na sala
    // (ninguém é avisado de que ela saiu), esperando a reconexão automática.
    public DateTimeOffset? DisconnectedAt { get; set; }

    // Endereço em que o Vysor DESTA pessoa pode ser alcançado, caso ela possa
    // hospedar a sala (ex: "100.94.12.7:5799"). Vem do próprio app, porque só
    // ele sabe por qual caminho os amigos conseguem chegar nele — o servidor
    // não tem como adivinhar.
    //
    // É esta informação que faz a sala sobreviver ao host fechar o app: todo
    // mundo já sabe de antemão pra onde ir em seguida.
    public string? Address { get; set; }

    // Ordem de chegada na sala, atribuída uma única vez (reconectar NÃO muda).
    // É ela que define a fila de sucessão: se o host sair, assume o primeiro
    // da fila que ainda estiver de pé. Como todo mundo recebe a mesma ordem,
    // cada um escolhe o mesmo sucessor por conta própria, sem precisar
    // combinar nada — o que é essencial, porque nesse momento não existe mais
    // ninguém no meio pra coordenar.
    public long JoinOrder { get; set; }

    // Por onde os OUTROS conseguem falar direto com esta pessoa: o endereço
    // externo que ela descobriu (via STUN) e o dela na rede local.
    //
    // É a única coisa que o servidor precisa carregar pro vídeo passar a ir
    // direto de um PC pro outro. São dois textos curtos por pessoa, uma vez
    // por sessão — comparado com o vídeo que passava por aqui antes, é
    // aproximadamente nada. Foi essa troca que deixou de custar gigabytes.
    public string[] Candidates { get; set; } = Array.Empty<string>();
}

// Representa uma sala: tem um código e uma lista de participantes.
public class Room
{
    public required string Code { get; set; }
    public string? Pin { get; set; } // PIN opcional, igual o "Extra lock" do print

    // Indexado pelo UserId (estável), não pelo ConnectionId.
    public ConcurrentDictionary<string, Participant> Participants { get; } = new();

    // Desde quando a sala está sem ninguém dentro. null = tem gente agora.
    // A sala NÃO é apagada no instante em que esvazia: ela fica "de molho"
    // por alguns minutos (ver RoomManager.EmptyRoomGrace). Isso é o que
    // permite todo mundo voltar depois de uma queda de internet, de um
    // reinício do servidor ou de um engasgo na rede — antes, bastava a sala
    // ficar vazia por um instante pra ela ser destruída, e aí o código
    // morria de vez: todo mundo passava a receber "Sala não encontrada" e
    // ninguém conseguia mais entrar.
    public DateTimeOffset? EmptySince { get; set; }

    // Contador de chegadas, pra numerar a fila de sucessão (ver
    // Participant.JoinOrder). Só cresce.
    private long _joinCounter;

    public long NextJoinOrder() => Interlocked.Increment(ref _joinCounter);
}

// Para onde uma conexão aponta. Guardado à parte porque o ConnectionId muda
// a cada reconexão, enquanto o UserId permanece.
public record ConnectionInfo(string RoomCode, string UserId);

// Classe central que guarda TODAS as salas ativas, em memória (na RAM do servidor).
// Como é "em memória", se o servidor reiniciar, as salas somem. Isso é normal
// e suficiente pro nosso caso (não precisamos de banco de dados aqui).
public class RoomManager
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();

    // Quantos envios simultâneos ainda não concluídos cada remetente pode ter.
    private readonly ConcurrentDictionary<string, int> _inFlightRelays = new();

    // Por quanto tempo uma sala vazia continua existindo antes de ser
    // descartada de vez. Generoso de propósito: o custo de manter uma sala
    // vazia na memória é irrisório (um punhado de bytes), enquanto apagá-la
    // cedo demais quebra a sala pra todo mundo de forma irreversível.
    public static readonly TimeSpan EmptyRoomGrace = TimeSpan.FromMinutes(10);

    // Por quanto tempo uma pessoa que caiu continua "na sala" esperando
    // reconectar. O SignalR tenta reconectar sozinho aos 0s, 2s, 10s e 30s por
    // padrão, então 45s cobre todas as tentativas dele com folga.
    public static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(45);

    // Quantas pessoas cabem numa sala. Existe pra que um cliente com defeito
    // (ou alguém mal-intencionado) não consiga encher o servidor sozinho — ele
    // guarda tudo em memória e não tem autenticação nenhuma.
    public const int MaxParticipantsPerRoom = 12;

    // --- Conexões ------------------------------------------------------------

    public void TrackConnection(string connectionId, string roomCode, string userId)
        => _connections[connectionId] = new ConnectionInfo(roomCode, userId);

    public ConnectionInfo? GetConnectionInfo(string connectionId)
        => _connections.TryGetValue(connectionId, out var info) ? info : null;

    public string? GetRoomCodeForConnection(string connectionId)
        => _connections.TryGetValue(connectionId, out var info) ? info.RoomCode : null;

    public void UntrackConnection(string connectionId)
    {
        _connections.TryRemove(connectionId, out _);
        _inFlightRelays.TryRemove("v:" + connectionId, out _);
        _inFlightRelays.TryRemove("a:" + connectionId, out _);
    }

    // --- Salas ---------------------------------------------------------------

    public Room CreateRoom(string? pin)
    {
        // TryAdd em vez de "checar e depois gravar": entre a checagem e a
        // gravação, outra pessoa criando sala ao mesmo tempo poderia sortear
        // o mesmo código, e uma sobrescreveria a outra — quem estivesse na
        // primeira sala ficaria num limbo, "dentro" de uma sala que o
        // servidor não conhece mais.
        while (true)
        {
            string code = GenerateCode();
            var room = new Room { Code = code, Pin = pin, EmptySince = DateTimeOffset.UtcNow };
            if (_rooms.TryAdd(code, room)) return room;
        }
    }

    // Devolve a sala com esse código, criando-a se ela não existir mais.
    // Usado só pela reentrada automática depois de uma queda de conexão: se o
    // servidor reiniciou (ou a sala expirou), o grupo consegue se reencontrar
    // no mesmo código em vez de ficar preso pra sempre no "Sala não
    // encontrada". Quem digita um código errado NÃO passa por aqui — continua
    // recebendo o erro normalmente.
    public Room GetOrCreateRoom(string code)
        => _rooms.GetOrAdd(code, c => new Room { Code = c, EmptySince = DateTimeOffset.UtcNow });

    public Room? GetRoom(string code)
    {
        _rooms.TryGetValue(code, out var room);
        return room;
    }

    // Marca a sala como vazia (começando a contar o tempo de tolerância) em
    // vez de apagá-la na hora.
    public void MarkRoomEmptyIfNeeded(string code)
    {
        if (_rooms.TryGetValue(code, out var room) && room.Participants.IsEmpty)
        {
            room.EmptySince ??= DateTimeOffset.UtcNow;
        }
    }

    // Chamado quando alguém entra: a sala deixa de estar em contagem
    // regressiva pra ser apagada.
    public void MarkRoomActive(Room room) => room.EmptySince = null;

    // --- Limpeza periódica ----------------------------------------------------

    // Remove quem caiu e não voltou dentro do prazo, e apaga salas vazias
    // velhas. Devolve a lista de "fulano saiu da sala tal" pra quem chamou
    // avisar os outros participantes.
    public List<(string RoomCode, string UserId)> SweepExpired()
    {
        var departures = new List<(string, string)>();
        var now = DateTimeOffset.UtcNow;

        try
        {
            foreach (var pair in _rooms)
            {
                var room = pair.Value;

                foreach (var participantPair in room.Participants)
                {
                    var participant = participantPair.Value;
                    if (participant.DisconnectedAt.HasValue
                        && now - participant.DisconnectedAt.Value > ReconnectGrace
                        && room.Participants.TryRemove(participantPair.Key, out _))
                    {
                        // A pessoa pode ter reconectado no intervalo entre a
                        // checagem acima e a remoção (a reconexão limpa o
                        // DisconnectedAt no MESMO objeto). Nesse caso ela
                        // volta pra lista e ninguém é avisado de nada — senão
                        // expulsaríamos alguém que acabou de voltar, e ela
                        // continuaria transmitindo enquanto todos achavam que
                        // tinha saído.
                        if (participant.DisconnectedAt is null)
                        {
                            room.Participants.TryAdd(participantPair.Key, participant);
                            continue;
                        }

                        departures.Add((room.Code, participant.UserId));
                    }
                }

                if (room.Participants.IsEmpty)
                {
                    room.EmptySince ??= now;
                    if (now - room.EmptySince.Value > EmptyRoomGrace)
                    {
                        _rooms.TryRemove(pair.Key, out _);
                    }
                }
            }
        }
        catch
        {
            // Um erro na limpeza nunca pode derrubar o servidor.
        }

        return departures;
    }

    // --- Controle de fluxo do repasse de vídeo/áudio -------------------------
    //
    // Impede que um espectador com internet ruim segure a transmissão de todo
    // mundo. Antes, o servidor esperava a entrega para CADA destinatário antes
    // de aceitar o próximo quadro do remetente; um único participante lento
    // travava a sala inteira e, por tabela, fazia o servidor parar de responder
    // aos "pings" de conexão — o que derrubava todo mundo e (aí sim) matava a
    // sala. Agora o repasse é solto, e quando o remetente acumula envios
    // demais ainda pendentes, o quadro novo é simplesmente descartado.
    //
    // Vídeo e áudio têm cotas SEPARADAS de propósito. Um quadro de vídeo é
    // milhares de vezes maior que um pedacinho de áudio; se dividissem a
    // mesma cota, um quadro pesado em trânsito faria o áudio ser descartado
    // junto — e falha no som incomoda muito mais do que um quadro perdido.
    public const int MaxInFlightVideoPerSender = 3;
    public const int MaxInFlightAudioPerSender = 12;

    public bool TryBeginRelay(string connectionId, bool isVideo)
    {
        string key = (isVideo ? "v:" : "a:") + connectionId;
        int max = isVideo ? MaxInFlightVideoPerSender : MaxInFlightAudioPerSender;

        while (true)
        {
            int current = _inFlightRelays.GetOrAdd(key, 0);
            if (current >= max) return false;
            if (_inFlightRelays.TryUpdate(key, current + 1, current)) return true;
        }
    }

    public void EndRelay(string connectionId, bool isVideo)
    {
        string key = (isVideo ? "v:" : "a:") + connectionId;

        while (true)
        {
            if (!_inFlightRelays.TryGetValue(key, out int current)) return;
            if (current <= 0) return;
            if (_inFlightRelays.TryUpdate(key, current - 1, current)) return;
        }
    }

    // Gera um código curto tipo "XJ4K9P", parecido com o do Beacon.
    private static string GenerateCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // sem letras/números parecidos (O/0, I/1)
        var random = Random.Shared;
        return new string(Enumerable.Range(0, 6).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }
}
