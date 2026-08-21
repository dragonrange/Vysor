namespace VysorClient.Services;

// Faz a sala sobreviver a quem está hospedando fechar o app.
//
// COMO ISSO ACONTECE NA PRÁTICA
// A sala mora no PC de alguém. Essa pessoa fecha o Vysor, o notebook dela
// dorme, a internet dela cai. Antes, isso acabava com a sala pra todo mundo —
// mesmo com os outros três continuando online, na mesma internet de sempre.
//
// A saída tem duas metades. A primeira já está no servidor: enquanto o host
// está de pé, ele distribui a fila de quem mais pode hospedar (ver
// RoomHub.BroadcastSuccessionAsync). A segunda é esta classe: quando a
// conexão cai de vez, ela percorre essa fila e recoloca todo mundo junto.
//
// A REGRA QUE IMPEDE A SALA DE RACHAR EM DUAS
// O passo mais delicado não é achar UM lugar — é todo mundo achar O MESMO.
// Se cada app escolhesse por conta própria, existiriam duas salas com o mesmo
// código e as pessoas divididas, sem entender por que o amigo sumiu. Então a
// escolha segue sempre a mesma ordem, em dois turnos:
//
//   1º  Alguém já tem a sala viva? Quem já tem sempre ganha. É isso que faz o
//       host antigo, ao voltar, entrar na sala que continuou em vez de abrir
//       outra por cima.
//   2º  Ninguém tem? Então assume o primeiro da fila que estiver de pé. Como
//       a fila é a mesma pra todos, todos chegam sozinhos na mesma resposta,
//       sem precisar combinar nada — o que é essencial, porque neste momento
//       não existe mais ninguém no meio pra coordenar.
public class HostFailover
{
    private readonly SignalRService _signalR;

    private CancellationTokenSource? _cts;
    private string? _roomCode;
    private string? _displayName;

    // Avisos pra tela poder contar o que está acontecendo em vez de só
    // congelar. O texto é curto de propósito: vai num rodapé, não num alerta.
    public event Action<string>? OnStatus;
    public event Action? OnRecovered;
    public event Action? OnGaveUp;

    // Quanto tempo insistir antes de desistir. Generoso porque a causa mais
    // comum é o host tendo fechado o app — e o resto do grupo continua lá,
    // esperando, sem nada de errado com eles.
    private static readonly TimeSpan TotalBudget = TimeSpan.FromMinutes(3);

    public HostFailover(SignalRService signalR)
    {
        _signalR = signalR;
    }

    public bool IsRecovering => _cts != null;

    // Chamado quando a conexão com o host cai de vez. Só age se estivermos
    // realmente numa sala.
    public void Begin(string roomCode, string displayName)
    {
        if (string.IsNullOrWhiteSpace(roomCode)) return;
        if (_cts != null) return;   // já estamos procurando

        _roomCode = roomCode.Trim().ToUpperInvariant();
        _displayName = displayName;
        _cts = new CancellationTokenSource();

        _ = RecoverLoopAsync(_cts.Token);
    }

    public void Cancel()
    {
        var cts = _cts;
        _cts = null;
        if (cts == null) return;
        try { cts.Cancel(); cts.Dispose(); } catch { }
    }

    private async Task RecoverLoopAsync(CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow + TotalBudget;
        int attempt = 0;

        try
        {
            while (!token.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                attempt++;
                OnStatus?.Invoke(attempt == 1
                    ? "A conexão caiu. Procurando a sala…"
                    : $"Ainda procurando a sala… (tentativa {attempt})");

                var found = await RoomLocator.FindAsync(_roomCode!, _signalR.UserId, null, token);

                if (found.HubUrl != null && await TryTakeOverAsync(found.HubUrl, token))
                {
                    _cts = null;
                    OnStatus?.Invoke("");
                    OnRecovered?.Invoke();
                    return;
                }

                // Espera crescente, com teto baixo: quem está esperando quer
                // voltar rápido, e as tentativas são baratas.
                int waitMs = Math.Min(1000 * attempt, 5000);
                await Task.Delay(waitMs, token);
            }
        }
        catch (OperationCanceledException)
        {
            return;   // alguém saiu da sala no meio: tudo certo
        }
        catch
        {
            // Nenhuma falha aqui pode derrubar o app.
        }

        _cts = null;
        OnStatus?.Invoke("Não consegui reencontrar a sala. "
                         + "Peça o convite de novo para quem está hospedando.");
        OnGaveUp?.Invoke();
    }

    private async Task<bool> TryTakeOverAsync(string hubUrl, CancellationToken token)
    {
        try
        {
            await _signalR.DisconnectAsync();
            if (token.IsCancellationRequested) return false;

            await _signalR.ConnectAsync(hubUrl);
            if (!_signalR.IsConnected) return false;

            // RejoinRoom (e não JoinRoom) porque ele RECRIA a sala se ela não
            // existir mais. É exatamente o que precisa acontecer quando quem
            // assume acabou de subir a sala do zero: os outros chegam em
            // seguida com o mesmo código e o grupo se remonta sozinho.
            await _signalR.RejoinRoomAsync(_roomCode!, _displayName ?? "Você");
            await _signalR.AnnounceAddressAsync(LocalAddresses.Best());

            HostDirectory.NoteWorking(hubUrl);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
