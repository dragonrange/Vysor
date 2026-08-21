using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VysorClient.Services;

// Descobre em quais endereços ESTE computador pode ser encontrado pelos
// amigos.
//
// Por que o app precisa saber disso: agora quem cria a sala hospeda ela no
// próprio PC, então precisa passar um endereço pros outros. Adivinhar qual é
// o "certo" é justamente a parte que dá confusão — uma máquina normalmente
// tem vários (rede local, Wi-Fi, Tailscale, máquinas virtuais, adaptadores do
// Docker…) e só alguns funcionam pra quem está fora de casa.
//
// Então o app não escolhe sozinho e em silêncio: ele lista os candidatos em
// ordem de "chance de funcionar" e mostra pra você escolher qual mandar.
public static class LocalAddresses
{
    public sealed record Candidate(string Address, string Label, string Explanation, int Rank);

    // Faixa 100.64.0.0/10. É a faixa que o Tailscale usa pros endereços dele.
    private static bool IsTailscale(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    private static bool IsPrivateLan(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        if (b[0] == 10) return true;                              // 10.0.0.0/8
        if (b[0] == 192 && b[1] == 168) return true;              // 192.168.0.0/16
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
        return false;
    }

    // Adaptadores virtuais que quase nunca são o caminho certo pros amigos
    // (VirtualBox, VMware, Docker, WSL, Hyper-V). Sem filtrar, o endereço
    // sugerido acabava sendo o de uma máquina virtual que só existe dentro
    // deste PC — e nada funcionava, sem nenhuma pista do motivo.
    private static readonly string[] VirtualHints =
    {
        "virtualbox", "vmware", "docker", "wsl", "hyper-v", "vethernet",
        "loopback", "bluetooth", "vpn tunnel"
    };

    private static bool LooksVirtual(NetworkInterface nic)
    {
        string text = (nic.Name + " " + nic.Description).ToLowerInvariant();
        return VirtualHints.Any(h => text.Contains(h));
    }

    private static bool LooksTailscaleNic(NetworkInterface nic)
    {
        string text = (nic.Name + " " + nic.Description).ToLowerInvariant();
        return text.Contains("tailscale");
    }

    // Lista os endereços deste PC, do mais provável de funcionar pro menos.
    public static List<Candidate> List()
    {
        var found = new List<Candidate>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                bool tailscaleNic = LooksTailscaleNic(nic);
                if (!tailscaleNic && LooksVirtual(nic)) continue;

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(info.Address)) continue;

                    string ip = info.Address.ToString();

                    if (tailscaleNic || IsTailscale(info.Address))
                    {
                        found.Add(new Candidate(ip, "Tailscale",
                            "Funciona de qualquer lugar, inclusive fora de casa. " +
                            "Seus amigos precisam ter o Tailscale instalado e estar na sua rede.",
                            0));
                    }
                    else if (IsPrivateLan(info.Address))
                    {
                        found.Add(new Candidate(ip, "Rede local",
                            "Funciona só pra quem estiver no MESMO Wi-Fi/roteador que você. " +
                            "Pra quem está em outra casa, use o Tailscale ou libere a porta no roteador.",
                            1));
                    }
                    else
                    {
                        found.Add(new Candidate(ip, "Internet",
                            "Este PC parece ter endereço próprio na internet. Se você liberar a " +
                            "porta no roteador, seus amigos entram sem instalar nada.",
                            2));
                    }
                }
            }
        }
        catch
        {
            // Sem rede nenhuma: devolve lista vazia, e a tela explica.
        }

        return found
            .GroupBy(c => c.Address)
            .Select(g => g.First())
            .OrderBy(c => c.Rank)
            .ToList();
    }

    // Guardado quando o roteador aceita abrir a porta sozinho (ver
    // PortForwarding). É o único endereço que funciona pra um amigo de outra
    // casa sem ele instalar nada, então quando existe, ele ganha de todos.
    public static string? PublicAddress { get; set; }

    // O endereço que deve ir no convite pros amigos.
    //
    // A ORDEM AQUI IMPORTA MUITO, e foi ela que causou o primeiro teste
    // fracassado com um amigo: o app escolhia o endereço da rede local
    // (192.168.x.x) e mandava como se fosse servir. Esse número só existe
    // dentro da sua casa — pra quem está em outra, ele não leva a lugar
    // nenhum. Agora o de casa é o ÚLTIMO recurso, e quem chama recebe junto
    // o aviso de que ele só vale no mesmo Wi-Fi.
    public static string? BestForFriends()
        => PublicAddress
           ?? List().FirstOrDefault(c => c.Label == "Tailscale")?.Address
           ?? List().FirstOrDefault()?.Address;

    // Verdadeiro quando o endereço que temos pra oferecer SÓ funciona dentro
    // da própria casa. Quem mostra o convite precisa avisar nesse caso.
    public static bool OnlyWorksOnSameWifi()
    {
        if (PublicAddress != null) return false;
        var best = List().FirstOrDefault();
        return best != null && best.Label == "Rede local";
    }

    // Mantido pelo mesmo nome de antes pra não mexer em quem já chamava.
    public static string? Best() => BestForFriends();
}
