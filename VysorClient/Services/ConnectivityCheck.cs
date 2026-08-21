using System.Net.Http;

namespace VysorClient.Services;

// Responde, em português claro, a pergunta que estava travando tudo:
// "meus amigos conseguem me alcançar?"
//
// POR QUE ISSO EXISTE
// Antes, o app mostrava um endereço e deixava você mandar pros amigos sem
// dizer se aquilo tinha alguma chance de funcionar. Quando não funcionava,
// sobrava um "servidor não encontrado" que não explica nada e não sugere
// nada. Era uma falha silenciosa — o mesmo tipo de erro do vazamento de
// áudio: o app sabia que estava errado e não falou.
//
// COMO A CONCLUSÃO É TIRADA
// O truque está em comparar dois números:
//   - o endereço que o SEU ROTEADOR acha que tem na internet;
//   - o endereço que um site de fora enxerga quando você acessa.
//
// Se os dois batem, o seu roteador é realmente a sua porta de entrada e dá
// pra abrir caminho até você. Se são diferentes, existe mais um roteador da
// sua operadora no meio (chamam isso de CGNAT) — e aí não tem configuração
// no seu roteador que resolva. Descobrir isso em 10 segundos evita horas
// tentando algo que nunca ia dar certo.
public static class ConnectivityCheck
{
    public enum Verdict
    {
        /// Amigos de fora conseguem entrar. Melhor caso.
        ReachableFromInternet,
        /// A operadora está no meio (CGNAT). Não adianta mexer no roteador.
        BlockedByCarrier,
        /// O roteador poderia, mas está com a abertura automática desligada.
        RouterRefused,
        /// Nem achamos o roteador (UPnP desligado ou rede incomum).
        RouterNotFound,
        /// Sem rede utilizável.
        NoNetwork
    }

    public sealed record Report(
        Verdict Verdict,
        string? PublicIp,
        string? RouterExternalIp,
        string? LanIp,
        string? TailscaleIp,
        string Title,
        string Explanation,
        string WhatToDo,
        string? SuggestedAddress,
        // Resultado da sonda de NAT: diz se ESTA pessoa consegue conexão
        // direta com os amigos, sem ninguém abrir porta em roteador. É o
        // dado que decide se o Vysor pode passar a funcionar sem host fixo.
        NatBehavior.Result? Nat = null);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    // Dois serviços, porque um deles pode estar fora do ar. Os dois devolvem
    // só o endereço em texto puro, nada mais.
    private static readonly string[] IpEchoServices =
    {
        "https://api.ipify.org",
        "https://checkip.amazonaws.com"
    };

    public static async Task<string?> GetPublicIpAsync()
    {
        foreach (string url in IpEchoServices)
        {
            try
            {
                string text = (await Http.GetStringAsync(url)).Trim();
                if (System.Net.IPAddress.TryParse(text, out _)) return text;
            }
            catch
            {
                // Tenta o próximo.
            }
        }
        return null;
    }

    public static async Task<Report> RunAsync(int port = LocalServer.DefaultPort)
    {
        var addresses = LocalAddresses.List();
        string? lanIp = addresses.FirstOrDefault(a => a.Label == "Rede local")?.Address;
        string? tailscaleIp = addresses.FirstOrDefault(a => a.Label == "Tailscale")?.Address;

        if (addresses.Count == 0)
        {
            return new Report(Verdict.NoNetwork, null, null, null, null,
                "Este computador está sem rede",
                "Não encontrei nenhuma placa de rede ativa com endereço.",
                "Confira se o Wi-Fi está conectado ou se o cabo está na tomada.",
                null);
        }

        // As duas perguntas ao mesmo tempo: são independentes e cada uma pode
        // levar alguns segundos.
        var publicIpTask = GetPublicIpAsync();
        var forwardTask = PortForwarding.TryOpenAsync(port, lanIp);

        // A sonda de NAT roda junto: ela é UDP puro e não atrapalha as outras.
        var natTask = Task.Run(() => NatBehavior.Detect());

        string? publicIp = await publicIpTask;
        var forward = await forwardTask;
        NatBehavior.Result? nat = null;
        try { nat = await natTask; } catch { }

        return Decide(publicIp, forward, lanIp, tailscaleIp, port) with { Nat = nat };
    }

    // Um resumo de uma linha, feito pra caber num print de tela e ser
    // comparado entre várias pessoas do grupo.
    public static string ShortSummary(Report report)
    {
        string hospedar = report.Verdict switch
        {
            Verdict.ReachableFromInternet => "posso hospedar: SIM",
            Verdict.BlockedByCarrier => "posso hospedar: NÃO (operadora)",
            Verdict.RouterRefused => "posso hospedar: NÃO (roteador recusa)",
            Verdict.RouterNotFound => "posso hospedar: NÃO (roteador mudo)",
            _ => "posso hospedar: ?"
        };

        string direta = report.Nat?.Kind switch
        {
            NatBehavior.Kind.DirectConnectionPossible => "conexão direta: SIM",
            NatBehavior.Kind.NeedsBridge => "conexão direta: NÃO (precisa de ponte)",
            NatBehavior.Kind.Blocked => "conexão direta: bloqueada",
            _ => "conexão direta: ?"
        };

        return hospedar + "  |  " + direta;
    }

    // A decisão em si, separada da parte de rede de propósito: assim ela pode
    // ser testada com todos os cenários (inclusive os que eu não consigo
    // reproduzir aqui, como uma operadora com CGNAT) antes de chegar no seu PC.
    internal static Report Decide(
        string? publicIp, PortForwarding.Result forward,
        string? lanIp, string? tailscaleIp, int port)
    {
        string? routerIp = forward.ExternalIp;

        // --- o roteador nem apareceu ---
        if (routerIp == null && !forward.Success && !forward.RouterFound)
        {
            string gateway = PortForwarding.DefaultGateways().FirstOrDefault()?.ToString() ?? "";
            string comoEntrar = gateway.Length > 0
                ? $"Abra http://{gateway} no navegador (é a tela do seu roteador) e procure "
                  + "por \"UPnP\" — costuma ficar em Avançado, NAT ou Rede. "
                : "Abra a tela do seu roteador no navegador e procure por \"UPnP\". ";

            return new Report(Verdict.RouterNotFound, publicIp, null, lanIp, tailscaleIp,
                "Não consegui falar com o seu roteador",
                forward.Detail + "\n\nIsso NÃO quer dizer que sua internet é ruim — quer dizer "
                + "só que o roteador não aceita pedidos automáticos. É uma opção que muitos "
                + "vêm com a fábrica desligada.",
                comoEntrar + "É uma chavinha só: ligue, salve, e clique em \"Testar minha "
                + "conexão\" de novo. Se der certo, acabou — e nenhum dos seus amigos "
                + "precisa fazer nada. Se você não achar a opção, ou não quiser mexer, me "
                + "avise: existe um caminho que não depende do roteador.",
                tailscaleIp);
        }

        // --- a operadora está no meio ---
        bool carrierInTheMiddle =
            routerIp != null && publicIp != null &&
            !string.Equals(routerIp, publicIp, StringComparison.Ordinal);

        if (carrierInTheMiddle)
        {
            return new Report(Verdict.BlockedByCarrier, publicIp, routerIp, lanIp, tailscaleIp,
                "Sua operadora não deixa seus amigos chegarem até você",
                $"Seu roteador acha que o endereço dele é {routerIp}, mas a internet "
                + $"te enxerga como {publicIp}. Isso quer dizer que existe mais um "
                + "roteador da sua operadora no meio do caminho (chamam de CGNAT). "
                + "Nenhuma configuração no SEU roteador resolve isso — o caminho "
                + "termina antes de chegar em você.",
                "Duas saídas: pedir um \"IP público\" à sua operadora (algumas dão de "
                + "graça, outras cobram), ou usar um caminho que não dependa disso. "
                + "Me avise que essa foi a resposta — é o sinal de que precisamos "
                + "partir pro caminho em que os computadores se acham sozinhos.",
                null);
        }

        // --- achamos o roteador, mas ele não deixa abrir ---
        if (!forward.Success)
        {
            return new Report(Verdict.RouterRefused, publicIp, routerIp, lanIp, tailscaleIp,
                "Seu roteador está com a abertura automática desligada",
                forward.Detail + " A boa notícia é que a sua internet PERMITE — só "
                + "falta liberar a passagem.",
                "Entre no seu roteador, procure \"UPnP\" e ligue. Ou crie a regra na "
                + "mão: porta " + port + ", protocolo TCP, apontando pro endereço "
                + (lanIp ?? "deste computador") + ". Depois rode este teste de novo.",
                publicIp);
        }

        // --- deu tudo certo ---
        return new Report(Verdict.ReachableFromInternet, publicIp, routerIp, lanIp, tailscaleIp,
            "Pronto — seus amigos conseguem entrar",
            $"O roteador abriu a porta {port} sozinho, e sua internet te dá um "
            + "endereço próprio. Ninguém precisa instalar nada.",
            "Crie a sala e mande o convite pelo botão Copiar. O endereço já vai "
            + "certo, com o seu IP da internet.",
            publicIp);
    }
}
