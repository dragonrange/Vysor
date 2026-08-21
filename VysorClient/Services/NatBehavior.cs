using System.Net;
using System.Net.Sockets;

namespace VysorClient.Services;

// Descobre se a rede desta pessoa permite conexão DIRETA com os amigos, sem
// ninguém abrir porta em roteador nenhum.
//
// A PERGUNTA QUE ISTO RESPONDE
// Pra dois PCs se acharem sozinhos, cada um manda pacotes pro endereço
// externo do outro ao mesmo tempo. Os roteadores, vendo que "já tinha
// conversa saindo pra lá", deixam a resposta entrar. É o tal do furo de NAT,
// e é assim que jogos e chamadas de vídeo funcionam sem configuração.
//
// Só que isso depende de um detalhe do roteador: ele mantém a MESMA porta
// externa quando você fala com destinos diferentes, ou inventa uma porta nova
// pra cada destino?
//
//   - Mantém a mesma  -> o endereço que descobrimos serve pro amigo também.
//                        O furo funciona. (A maioria dos roteadores.)
//   - Inventa outra   -> o endereço que descobrimos NÃO serve pro amigo: na
//                        hora que ele tentar, a porta já é outra. Chamam isso
//                        de NAT simétrico, e nele o furo não passa.
//
// COMO DESCOBRIMOS
// Perguntamos "qual é o meu endereço?" a DOIS servidores diferentes, pelo
// MESMO socket. Se os dois responderem a mesma porta, o roteador mantém.
// Se responderem portas diferentes, ele inventa. É um teste de 2 segundos
// que não depende de combinar nada com ninguém — cada pessoa roda sozinha.
public static class NatBehavior
{
    public enum Kind
    {
        /// A rede mantém a porta: dá pra conectar direto com os amigos.
        DirectConnectionPossible,
        /// A rede troca a porta a cada destino: precisa de alguém como ponte.
        NeedsBridge,
        /// Não foi possível descobrir (sem internet, ou algo bloqueando UDP).
        Unknown,
        /// Nem os servidores públicos responderam — normalmente firewall
        /// corporativo ou rede que bloqueia esse tipo de tráfego.
        Blocked
    }

    public sealed record Result(
        Kind Kind,
        IPEndPoint? FirstMapping,
        IPEndPoint? SecondMapping,
        string Title,
        string Explanation);

    public static Result Detect(TimeSpan? perServerTimeout = null)
    {
        TimeSpan timeout = perServerTimeout ?? TimeSpan.FromSeconds(3);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveTimeout = 800;
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
        catch
        {
            return Unknown("Não consegui abrir uma porta de rede neste computador.");
        }

        var mappings = new List<StunClient.Mapping>();

        foreach (var (host, port) in StunClient.PublicServers)
        {
            var server = StunClient.Resolve(host, port);
            if (server == null) continue;

            // MESMO socket pra todos, de propósito: é justamente a comparação
            // entre as respostas que revela o comportamento do roteador.
            var mapping = StunClient.Query(socket, server, host, timeout);
            if (mapping != null) mappings.Add(mapping);

            if (mappings.Count >= 2) break;
        }

        if (mappings.Count == 0)
        {
            return new Result(Kind.Blocked, null, null,
                "Sua rede bloqueia o tipo de conexão que usamos",
                "Nenhum dos servidores públicos respondeu. Isso costuma acontecer "
                + "em rede de empresa, faculdade ou com antivírus muito restritivo. "
                + "Em internet de casa é raro.");
        }

        if (mappings.Count == 1)
        {
            return new Result(Kind.Unknown, mappings[0].External, null,
                "Não deu pra ter certeza sobre a sua rede",
                $"Só um servidor respondeu (o {mappings[0].Server}), e preciso de dois "
                + "pra comparar. Tente de novo daqui a pouco.");
        }

        var a = mappings[0].External;
        var b = mappings[1].External;

        // A porta é o que importa. O endereço pode até variar em provedores
        // grandes, mas é a porta que decide se o furo passa.
        if (a.Port == b.Port)
        {
            return new Result(Kind.DirectConnectionPossible, a, b,
                "Sua rede permite conexão direta",
                $"Você aparece como {a} para os dois servidores que consultei. "
                + "Isso quer dizer que o seu roteador mantém a mesma porta pra "
                + "destinos diferentes — então seus amigos conseguem falar direto "
                + "com você, sem ninguém abrir porta em roteador nenhum.");
        }

        return new Result(Kind.NeedsBridge, a, b,
            "Sua rede troca a porta a cada conexão",
            $"Você apareceu como {a} para um servidor e {b} para outro. Seu roteador "
            + "inventa uma porta nova pra cada destino (chamam de NAT simétrico), "
            + "então o endereço que a gente descobre não serve pro seu amigo — "
            + "quando ele tentar, a porta já é outra. Nesse caso a conexão precisa "
            + "passar por alguém do grupo que alcance os dois lados.");
    }

    private static Result Unknown(string explanation) =>
        new(Kind.Unknown, null, null, "Não deu pra descobrir o tipo da sua rede", explanation);
}
