using System.Net.Http;

namespace VysorClient.Services;

// Descobre ONDE uma sala está, agora que ela pode estar no PC de qualquer um.
//
// Esta é a peça que os dois caminhos importantes compartilham:
//   - entrar numa sala com um convite colado;
//   - reencontrar a sala depois que quem hospedava fechou o app.
//
// Os dois têm exatamente o mesmo problema: existem vários lugares possíveis e
// é preciso que TODO MUNDO escolha o mesmo, senão a sala racha em duas e as
// pessoas ficam divididas sem entender por quê.
//
// A regra é sempre a mesma, em dois turnos:
//   1º  quem JÁ TEM a sala viva ganha, sempre. É isso que faz quem chega
//       atrasado (inclusive o host antigo voltando) entrar na sala que
//       continuou, em vez de abrir outra por cima.
//   2º  se ninguém tem, assume o primeiro da fila que estiver de pé. Como a
//       fila é a mesma pra todos, todos chegam sozinhos na mesma resposta.
public static class RoomLocator
{
    public enum Status { Dead, Alive, HasRoom }

    // Uma checagem de "você está aí?" não pode demorar: com quatro endereços
    // na fila, esperar muito em cada um vira meio minuto de tela parada.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly HttpClient Http = new() { Timeout = ProbeTimeout };

    public sealed record Result(string? HubUrl, bool RoomWasAlive);

    // Devolve a URL do hub onde a sala deve ser aberta, ou null se nenhum
    // endereço conhecido respondeu.
    public static async Task<Result> FindAsync(
        string code, string myUserId, string? preferAddress = null,
        CancellationToken token = default)
    {
        // O servidor entra na lista em PRIMEIRO lugar, mas não é o único
        // caminho: se ele estiver fora do ar, a procura continua pelos PCs do
        // grupo, e a sala pode viver sem ele.
        var candidates = SignalRService.BuildCandidateUrls(myUserId, preferAddress);
        if (candidates.Count == 0) return new Result(null, false);

        // Pergunta a todos ao mesmo tempo. Um de cada vez, com quatro amigos
        // na fila, seria quase dez segundos parado a cada tentativa.
        var results = await Task.WhenAll(
            candidates.Select(url => ProbeAsync(url, code, token)));

        for (int i = 0; i < candidates.Count; i++)
            if (results[i] == Status.HasRoom) return new Result(candidates[i], true);

        for (int i = 0; i < candidates.Count; i++)
            if (results[i] == Status.Alive) return new Result(candidates[i], false);

        return new Result(null, false);
    }

    public static async Task<Status> ProbeAsync(string hubUrl, string code, CancellationToken token = default)
    {
        // hubUrl termina em /roomhub; a checagem mora ao lado dele.
        string baseUrl = hubUrl.EndsWith("/roomhub", StringComparison.OrdinalIgnoreCase)
            ? hubUrl[..^"/roomhub".Length]
            : hubUrl.TrimEnd('/');

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(ProbeTimeout);

            using var response = await Http.GetAsync(
                $"{baseUrl}/room/{code.Trim().ToUpperInvariant()}", cts.Token);

            // 200 = tem a sala. 404 = está vivo, só não tem esta sala (serve
            // como candidato do 2º turno).
            return response.IsSuccessStatusCode ? Status.HasRoom : Status.Alive;
        }
        catch
        {
            return Status.Dead;
        }
    }
}
