using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace VysorClient.Services;

// Pede ao roteador pra abrir a porta do Vysor sozinho.
//
// POR QUE ISSO EXISTE
// Pra um amigo de outra casa te alcançar, o seu roteador precisa deixar a
// conexão entrar. Fazer isso na mão significa entrar no roteador, achar uma
// tela chamada "encaminhamento de porta" e preencher quatro campos — e isso é
// pedir demais, ainda mais multiplicado por várias pessoas.
//
// Só que o roteador aceita receber esse pedido por PROGRAMA. É o mesmo
// mecanismo que jogos online e programas de torrent usam há vinte anos
// (chama-se UPnP). Então o app pede, e ninguém precisa configurar nada.
//
// Escrito na mão, sem biblioteca de terceiros, por um motivo prático: assim
// tudo aqui é código comum que dá pra compilar e testar antes de chegar no
// seu PC — inclusive contra um roteador de mentira, que foi como isto foi
// verificado.
//
// FALHA É NORMAL, E TUDO BEM
// Roteador com UPnP desligado, operadora usando CGNAT, rede corporativa: em
// todos esses casos isto não funciona. Nada aqui lança erro pra cima; quem
// chama recebe um resultado dizendo o que deu e mostra isso pra pessoa.
public static class PortForwarding
{
    public sealed record Result(
        bool Success,
        string? ExternalIp,
        int ExternalPort,
        string Detail,
        // Se o roteador chegou a RESPONDER, mesmo tendo recusado o pedido.
        // Separado de propósito: "não achei o roteador" e "achei e ele
        // recusou" são situações diferentes, com saídas diferentes — e
        // misturar as duas fazia o aviso na tela dizer uma coisa no título
        // e outra no corpo.
        bool RouterFound = false);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private const string MulticastAddress = "239.255.255.250";
    private const int SsdpPort = 1900;

    // Os dois "sobrenomes" que um roteador pode usar pro serviço que abre
    // portas. Depende do tipo de conexão (cabo/fibra x PPPoE), então
    // procuramos os dois.
    private static readonly string[] ServiceTypes =
    {
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1"
    };

    // ---------- 1. achar o roteador ----------

    // Procura o roteador por DOIS caminhos ao mesmo tempo, de propósito.
    //
    // O caminho clássico é gritar pra rede inteira ("multicast") e ver quem
    // responde. Só que esse grito falha por motivos bobos e frequentes: o
    // Firewall do Windows descarta a resposta (ela chega de um endereço
    // diferente do que a gente chamou, e isso confunde a regra automática),
    // o PC tem várias placas de rede e o grito sai pela errada, ou o pacote
    // simplesmente se perde — não existe reenvio nesse tipo de mensagem.
    //
    // Por isso também perguntamos DIRETO ao roteador, no endereço dele. Esse
    // segundo caminho não depende de multicast nem de o Firewall entender a
    // resposta, e resolve a maior parte dos casos em que o primeiro falha em
    // silêncio.
    public static async Task<List<Uri>> DiscoverAsync(TimeSpan timeout,
                                                      IEnumerable<IPAddress>? extraTargets = null)
    {
        var found = new List<Uri>();
        var tasks = new List<Task>();

        void Collect(IEnumerable<Uri> uris)
        {
            lock (found)
            {
                foreach (var uri in uris)
                    if (!found.Contains(uri)) found.Add(uri);
            }
        }

        foreach (var localIp in LocalIPv4Addresses())
        {
            var ip = localIp;
            tasks.Add(Task.Run(async () => Collect(await SearchFromAsync(ip, timeout))));
        }

        // Pergunta direta ao roteador de cada rede (o "gateway padrão").
        var direct = new List<IPAddress>(DefaultGateways());
        if (extraTargets != null) direct.AddRange(extraTargets);

        foreach (var gateway in direct)
        {
            var target = gateway;
            tasks.Add(Task.Run(async () => Collect(await AskDirectlyAsync(target, timeout))));
        }

        try { await Task.WhenAll(tasks); } catch { }
        return found;
    }

    // O endereço do roteador em cada rede em que este PC está.
    internal static List<IPAddress> DefaultGateways()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var gw in nic.GetIPProperties().GatewayAddresses)
                {
                    if (gw?.Address == null) continue;
                    if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (gw.Address.Equals(IPAddress.Any)) continue;
                    if (!list.Contains(gw.Address)) list.Add(gw.Address);
                }
            }
        }
        catch { }
        return list;
    }

    // Pergunta ao roteador no endereço dele, sem multicast.
    private static Task<List<Uri>> AskDirectlyAsync(IPAddress gateway, TimeSpan timeout)
        => Task.Run(() =>
        {
            var results = new List<Uri>();
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.ReceiveTimeout = 700;
                var target = new IPEndPoint(gateway, SsdpPort);

                var buffer = new byte[8192];
                DateTime deadline = DateTime.UtcNow + timeout;

                // Repete o pedido: mensagem UDP não tem reenvio automático, e
                // uma única tentativa perdida vira um "seu roteador não
                // respondeu" que não é verdade.
                for (int round = 0; round < 3 && DateTime.UtcNow < deadline; round++)
                {
                    foreach (string st in SearchTargets)
                    {
                        try { socket.SendTo(Encoding.ASCII.GetBytes(BuildSearch(st, gateway.ToString())), target); }
                        catch { }
                    }

                    while (DateTime.UtcNow < deadline)
                    {
                        try
                        {
                            EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                            int read = socket.ReceiveFrom(buffer, ref sender);
                            if (read <= 0) break;
                            var location = ExtractLocation(Encoding.ASCII.GetString(buffer, 0, read));
                            if (location != null && !results.Contains(location)) results.Add(location);
                        }
                        catch (SocketException)
                        {
                            break;   // só o prazo de espera desta rodada
                        }
                    }

                    if (results.Count > 0) break;
                }
            }
            catch { }
            return results;
        });

    private static readonly string[] SearchTargets =
    {
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
        "ssdp:all"
    };

    private static string BuildSearch(string searchTarget, string host) =>
        "M-SEARCH * HTTP/1.1\r\n" +
        $"HOST: {host}:{SsdpPort}\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 2\r\n" +
        $"ST: {searchTarget}\r\n\r\n";

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        List<IPAddress> list = new();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(info.Address))
                    {
                        list.Add(info.Address);
                    }
                }
            }
        }
        catch { }
        return list;
    }

    private static Task<List<Uri>> SearchFromAsync(IPAddress localIp, TimeSpan timeout)
        => Task.Run(() => SearchFrom(localIp, timeout));

    // Sincrono de propósito: usa socket bloqueante com prazo, e é chamado
    // dentro de um Task.Run pra não segurar a thread de quem pediu.
    private static List<Uri> SearchFrom(IPAddress localIp, TimeSpan timeout)
    {
        var results = new List<Uri>();

        // Uma placa de rede por vez, de propósito: num PC com Wi-Fi, cabo e
        // adaptadores virtuais, mandar por "qualquer uma" costuma sair pela
        // errada e o roteador nunca responde.
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(localIp, 0));

            // Diz explicitamente por qual placa de rede o grito deve sair. Sem
            // isto, num PC com Wi-Fi + cabo + adaptadores virtuais, o sistema
            // escolhe sozinho — e escolhe errado com frequência.
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    localIp.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
            }
            catch { }

            socket.ReceiveTimeout = 700;

            var target = new IPEndPoint(IPAddress.Parse(MulticastAddress), SsdpPort);
            var buffer = new byte[8192];
            DateTime deadline = DateTime.UtcNow + timeout;

            // Três rodadas: mensagem UDP pode simplesmente se perder, e não
            // existe reenvio automático. Uma tentativa só transformava um
            // pacote perdido em "seu roteador não respondeu".
            for (int round = 0; round < 3 && DateTime.UtcNow < deadline; round++)
            {
                foreach (string st in SearchTargets)
                {
                    try { socket.SendTo(Encoding.ASCII.GetBytes(BuildSearch(st, MulticastAddress)), target); }
                    catch { }
                }

                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                        int read = socket.ReceiveFrom(buffer, ref sender);
                        if (read <= 0) break;

                        Uri? location = ExtractLocation(Encoding.ASCII.GetString(buffer, 0, read));
                        if (location != null && !results.Contains(location)) results.Add(location);
                    }
                    catch (SocketException)
                    {
                        break;   // prazo desta rodada esgotado
                    }
                }

                if (results.Count > 0) break;
            }
        }
        catch
        {
            // Placa de rede que não aceita multicast: ignora e segue.
        }

        return results;
    }

    // Cada resposta traz uma linha "LOCATION: http://...": é o endereço da
    // ficha técnica do roteador.
    internal static Uri? ExtractLocation(string ssdpResponse)
    {
        foreach (string line in ssdpResponse.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase)) continue;

            string value = trimmed["LOCATION:".Length..].Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) return uri;
        }
        return null;
    }

    // ---------- 2. ler a ficha técnica e achar onde mandar os pedidos ----------

    public sealed record ControlPoint(Uri ControlUrl, string ServiceType);

    public static async Task<ControlPoint?> ReadControlPointAsync(Uri descriptionUrl)
    {
        try
        {
            string xml = await Http.GetStringAsync(descriptionUrl);
            return ParseControlPoint(xml, descriptionUrl);
        }
        catch
        {
            return null;
        }
    }

    // Separado da parte de rede pra poder ser testado com um XML de verdade.
    internal static ControlPoint? ParseControlPoint(string xml, Uri baseUrl)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return null; }

        // Comparação pelo nome local (ignorando o "namespace") porque cada
        // fabricante declara o XML de um jeito, e exigir o namespace exato
        // faria o app não achar o serviço em metade dos roteadores.
        foreach (var service in doc.Descendants().Where(e => e.Name.LocalName == "service"))
        {
            string? type = service.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value?.Trim();
            string? control = service.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value?.Trim();

            if (type == null || control == null) continue;
            if (!ServiceTypes.Contains(type)) continue;

            // O endereço de controle quase sempre vem relativo ("/ctl/IPConn"),
            // e às vezes completo. Decidimos olhando se ele COMEÇA com http —
            // e não perguntando "isto é um endereço absoluto?", que era o que
            // estava aqui antes e dava errado: "/ctl/IPConn" é aceito como
            // endereço absoluto de ARQUIVO, virava "file:///ctl/IPConn", e daí
            // nenhum comando chegava no roteador.
            Uri? absolute;
            bool jaCompleto =
                control.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                control.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            if (jaCompleto)
            {
                if (!Uri.TryCreate(control, UriKind.Absolute, out absolute)) continue;
            }
            else
            {
                if (!Uri.TryCreate(baseUrl, control, out absolute)) continue;
            }

            return new ControlPoint(absolute, type);
        }

        return null;
    }

    // ---------- 3. conversar com o roteador ----------

    internal static string BuildSoap(string serviceType, string action, string innerXml) =>
        "<?xml version=\"1.0\"?>" +
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
        "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
        "<s:Body>" +
        $"<u:{action} xmlns:u=\"{serviceType}\">{innerXml}</u:{action}>" +
        "</s:Body></s:Envelope>";

    private static async Task<XDocument?> CallAsync(ControlPoint point, string action, string innerXml)
    {
        try
        {
            string body = BuildSoap(point.ServiceType, action, innerXml);

            using var request = new HttpRequestMessage(HttpMethod.Post, point.ControlUrl);
            request.Content = new StringContent(body, Encoding.UTF8, "text/xml");
            request.Headers.Add("SOAPACTION", $"\"{point.ServiceType}#{action}\"");

            using var response = await Http.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) return null;

            return XDocument.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    // O endereço que o ROTEADOR acha que é o dele na internet. Comparar este
    // número com o que um site de fora enxerga é o que revela o CGNAT (ver
    // ConnectivityCheck).
    public static async Task<string?> GetExternalIpAsync(ControlPoint point)
    {
        var doc = await CallAsync(point, "GetExternalIPAddress", "");
        string? ip = doc?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value?.Trim();
        return string.IsNullOrWhiteSpace(ip) ? null : ip;
    }

    public static async Task<bool> AddPortMappingAsync(
        ControlPoint point, string internalIp, int port, string description)
    {
        // NewLeaseDuration=0 pede um encaminhamento SEM prazo de validade.
        // Alguns roteadores recusam prazo zero, então, se falhar, tentamos de
        // novo com prazo — e o app renova enquanto estiver aberto.
        string Inner(int lease) =>
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            "<NewProtocol>TCP</NewProtocol>" +
            $"<NewInternalPort>{port}</NewInternalPort>" +
            $"<NewInternalClient>{internalIp}</NewInternalClient>" +
            "<NewEnabled>1</NewEnabled>" +
            $"<NewPortMappingDescription>{description}</NewPortMappingDescription>" +
            $"<NewLeaseDuration>{lease}</NewLeaseDuration>";

        if (await CallAsync(point, "AddPortMapping", Inner(0)) != null) return true;
        return await CallAsync(point, "AddPortMapping", Inner(604800)) != null;   // 7 dias
    }

    public static async Task<bool> DeletePortMappingAsync(ControlPoint point, int port)
    {
        string inner =
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            "<NewProtocol>TCP</NewProtocol>";
        return await CallAsync(point, "DeletePortMapping", inner) != null;
    }

    // ---------- tudo junto ----------

    private static ControlPoint? _openedOn;
    private static int _openedPort;

    // Tenta abrir a porta. Nunca lança: devolve o que aconteceu, em português.
    public static async Task<Result> TryOpenAsync(int port, string? internalIp = null)
    {
        internalIp ??= LocalAddresses.List()
            .FirstOrDefault(c => c.Label == "Rede local")?.Address;

        if (internalIp == null)
            return new Result(false, null, port,
                "Não achei o endereço deste computador na rede local.");

        var locations = await DiscoverAsync(TimeSpan.FromSeconds(4));
        if (locations.Count == 0)
        {
            string gateway = DefaultGateways().FirstOrDefault()?.ToString() ?? "";
            return new Result(false, null, port,
                gateway.Length > 0
                    ? $"Procurei o seu roteador ({gateway}) de duas formas diferentes e ele "
                      + "não respondeu ao pedido automático. Isso quase sempre quer dizer que "
                      + "a opção UPnP está desligada nele."
                    : "Não achei o roteador desta rede.");
        }

        foreach (var location in locations)
        {
            var point = await ReadControlPointAsync(location);
            if (point == null) continue;

            string? externalIp = await GetExternalIpAsync(point);
            bool opened = await AddPortMappingAsync(point, internalIp, port, "Vysor");

            if (opened)
            {
                _openedOn = point;
                _openedPort = port;
                return new Result(true, externalIp, port, "O roteador abriu a porta.", true);
            }

            if (externalIp != null)
                return new Result(false, externalIp, port,
                    "Seu roteador respondeu, mas recusou abrir a porta. "
                    + "Provavelmente o UPnP está desligado nas configurações dele.", true);
        }

        // Chegamos aqui quando o roteador APARECEU na busca mas não deixou
        // nem perguntar o endereço dele. Na prática é UPnP desligado.
        return new Result(false, null, port,
            "Encontrei o seu roteador, mas ele não aceita abrir portas por programa.", true);
    }

    // Fecha o que abrimos, ao sair. Deixar aberto não é o fim do mundo (o
    // Vysor não estaria mais ouvindo), mas é falta de educação com a rede da
    // pessoa deixar um buraco pra trás.
    public static async Task CloseAsync()
    {
        var point = _openedOn;
        _openedOn = null;
        if (point == null) return;
        try { await DeletePortMappingAsync(point, _openedPort); } catch { }
    }
}
