using System.Net;
using System.Net.Sockets;

namespace VysorClient.Services;

// Pergunta a um servidor na internet: "de fora, qual é o meu endereço?"
//
// POR QUE ISSO É A PEÇA MAIS IMPORTANTE AGORA
// Pra dois computadores em casas diferentes se acharem sem ninguém abrir
// porta no roteador, cada um precisa saber com que endereço ele APARECE lá
// fora — que não é o endereço que ele tem em casa. Quem responde isso é um
// servidor STUN, e existem vários públicos e gratuitos (o do Google é usado
// pelo mundo inteiro há mais de uma década).
//
// O protocolo é minúsculo: manda 20 bytes, recebe o endereço de volta. Está
// escrito aqui na mão em vez de vir de biblioteca pelo mesmo motivo de
// sempre neste projeto — assim dá pra testar antes de chegar no seu PC.
public static class StunClient
{
    // Número fixo que todo servidor STUN devolve junto, pra provar que a
    // resposta é mesmo de um servidor STUN e não lixo que chegou por acaso.
    private const uint MagicCookie = 0x2112A442;

    private const ushort BindingRequest = 0x0001;
    private const ushort BindingResponse = 0x0101;
    private const ushort AttrMappedAddress = 0x0001;
    private const ushort AttrXorMappedAddress = 0x0020;

    // Servidores públicos e gratuitos. Vários de propósito: se um estiver
    // fora do ar, o teste não pode dar "falhou" por causa disso.
    public static readonly (string Host, int Port)[] PublicServers =
    {
        ("stun.l.google.com", 19302),
        ("stun1.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
    };

    public sealed record Mapping(IPEndPoint External, string Server);

    // Monta o pedido. O "número da conversa" (transaction id) serve pra
    // reconhecer a resposta certa quando várias estão no ar.
    internal static byte[] BuildRequest(byte[] transactionId)
    {
        var msg = new byte[20];
        msg[0] = (byte)(BindingRequest >> 8);
        msg[1] = (byte)(BindingRequest & 0xFF);
        msg[2] = 0; msg[3] = 0;                      // sem atributos
        msg[4] = (byte)((MagicCookie >> 24) & 0xFF);
        msg[5] = (byte)((MagicCookie >> 16) & 0xFF);
        msg[6] = (byte)((MagicCookie >> 8) & 0xFF);
        msg[7] = (byte)(MagicCookie & 0xFF);
        Buffer.BlockCopy(transactionId, 0, msg, 8, 12);
        return msg;
    }

    // Lê a resposta e tira dela o endereço externo.
    //
    // O endereço vem "embaralhado" (XOR) com o número fixo — não por
    // segurança, mas porque alguns roteadores antigos reescreviam qualquer
    // coisa que parecesse um endereço dentro do pacote, e estragavam a
    // resposta no meio do caminho. Embaralhado, eles não reconhecem e não
    // mexem.
    internal static IPEndPoint? ParseResponse(byte[] data, int length, byte[] expectedTransactionId)
    {
        if (length < 20) return null;

        int type = (data[0] << 8) | data[1];
        if (type != BindingResponse) return null;

        uint cookie = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);
        if (cookie != MagicCookie) return null;

        for (int i = 0; i < 12; i++)
            if (data[8 + i] != expectedTransactionId[i]) return null;

        int messageLength = (data[2] << 8) | data[3];
        int pos = 20;
        int end = Math.Min(length, 20 + messageLength);

        IPEndPoint? fallback = null;

        while (pos + 4 <= end)
        {
            int attrType = (data[pos] << 8) | data[pos + 1];
            int attrLen = (data[pos + 2] << 8) | data[pos + 3];
            int valuePos = pos + 4;
            if (valuePos + attrLen > end) break;

            if ((attrType == AttrXorMappedAddress || attrType == AttrMappedAddress) && attrLen >= 8)
            {
                int family = data[valuePos + 1];
                if (family == 0x01)   // IPv4
                {
                    int port = (data[valuePos + 2] << 8) | data[valuePos + 3];
                    var addrBytes = new byte[4];
                    Buffer.BlockCopy(data, valuePos + 4, addrBytes, 0, 4);

                    if (attrType == AttrXorMappedAddress)
                    {
                        port ^= (int)(MagicCookie >> 16);
                        addrBytes[0] ^= (byte)((MagicCookie >> 24) & 0xFF);
                        addrBytes[1] ^= (byte)((MagicCookie >> 16) & 0xFF);
                        addrBytes[2] ^= (byte)((MagicCookie >> 8) & 0xFF);
                        addrBytes[3] ^= (byte)(MagicCookie & 0xFF);
                    }

                    var endpoint = new IPEndPoint(new IPAddress(addrBytes), port);

                    // A versão embaralhada é a boa; a antiga só entra se não
                    // vier a outra.
                    if (attrType == AttrXorMappedAddress) return endpoint;
                    fallback ??= endpoint;
                }
            }

            pos = valuePos + attrLen;
            pos += (4 - (attrLen % 4)) % 4;   // atributos são alinhados de 4 em 4
        }

        return fallback;
    }

    // Pergunta a UM servidor, usando um socket que quem chama controla.
    //
    // O socket vem de fora de propósito: pra descobrir o tipo de rede da
    // pessoa é preciso perguntar a DOIS servidores diferentes pelo MESMO
    // socket e comparar as respostas (ver NatBehavior).
    public static Mapping? Query(Socket socket, IPEndPoint server, string serverName,
                                 TimeSpan timeout)
    {
        var transactionId = new byte[12];
        Random.Shared.NextBytes(transactionId);
        byte[] request = BuildRequest(transactionId);

        var buffer = new byte[512];
        DateTime deadline = DateTime.UtcNow + timeout;

        // Três tentativas: é UDP, o pacote pode simplesmente sumir.
        for (int attempt = 0; attempt < 3 && DateTime.UtcNow < deadline; attempt++)
        {
            try { socket.SendTo(request, server); } catch { return null; }

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int read = socket.ReceiveFrom(buffer, ref from);
                    var mapped = ParseResponse(buffer, read, transactionId);
                    if (mapped != null) return new Mapping(mapped, serverName);
                    // Resposta de outra pergunta: continua esperando a nossa.
                }
                catch (SocketException)
                {
                    break;   // prazo desta tentativa
                }
            }
        }

        return null;
    }

    public static IPEndPoint? Resolve(string host, int port)
    {
        try
        {
            var addresses = Dns.GetHostAddresses(host);
            var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return v4 == null ? null : new IPEndPoint(v4, port);
        }
        catch
        {
            return null;
        }
    }
}
