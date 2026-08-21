using System.Security.Cryptography;

namespace VysorClient.Services;

// Como um quadro de vídeo viaja direto de um PC pro outro.
//
// O PROBLEMA
// Até agora o vídeo ia por uma conexão parecida com a de um site: você
// entrega um bloco de 100 KB e ela se vira. Indo direto entre dois
// computadores, o transporte é outro — cada pacote é pequeno e independente,
// como um cartão-postal. Um quadro-chave de tela cheia não cabe num pacote
// só: precisa ser recortado, numerado, e remontado do outro lado.
//
// AS TRÊS DECISÕES QUE IMPORTAM AQUI
//
// 1. Pedaços de 1200 bytes. Poderia ser maior, mas acima de ~1400 os pacotes
//    começam a ser quebrados de novo pelos roteadores do caminho — e quando
//    isso acontece, perder um fragmento invisível derruba o pacote inteiro.
//    Ficando abaixo do limite, cada pedaço viaja sozinho de verdade.
//
// 2. Perdeu um pedaço, joga o quadro fora. Não pedimos reenvio. Parece
//    desperdício, mas é o certo pra tela ao vivo: um quadro reenviado chega
//    atrasado demais pra servir, e a espera por ele atrasaria todos os
//    seguintes. Melhor pular e seguir — a imagem se recompõe no próximo
//    quadro-chave, que vem a cada segundo.
//
// 3. Criptografado. Até agora a sua tela ia protegida até o servidor. Indo
//    direto entre casas, ela atravessaria a internet aberta — e trocar
//    privacidade por velocidade sem ninguém pedir seria uma escolha ruim de
//    se fazer em silêncio. A chave nasce do código da sala, que só quem foi
//    convidado tem.
public static class PeerPacket
{
    // Cabe com folga dentro do limite típico de 1500 bytes da internet
    // doméstica, deixando espaço pros cabeçalhos de rede.
    public const int MaxPayloadPerDatagram = 1200;

    public const byte KindVideo = 0;
    public const byte KindAudio = 1;

    // type(1) + kind(1) + frameId(4) + fragIndex(2) + fragCount(2)
    public const int HeaderSize = 10;

    public const byte TypeMedia = 2;

    private const int NonceSize = 12;   // AES-GCM
    private const int TagSize = 16;

    // ---------- chave da sala ----------

    // A chave vem do código da sala. Não é segredo militar — é o que impede
    // que alguém que capture o tráfego no meio do caminho monte a sua tela.
    // Quem não foi convidado não tem o código, e sem ele os pacotes são ruído.
    public static byte[] DeriveKey(string roomCode)
    {
        // O "sal" fixo existe pra que a chave dependa deste app, e não seja o
        // mesmo hash que qualquer outro programa faria do mesmo texto.
        byte[] salt = "vysor-sala-v1"u8.ToArray();
        return Rfc2898DeriveBytes.Pbkdf2(
            password: System.Text.Encoding.UTF8.GetBytes(roomCode.Trim().ToUpperInvariant()),
            salt: salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);
    }

    // ---------- envio ----------

    // Recebe um quadro inteiro e devolve os pacotes prontos pra sair na rede.
    //
    // Criptografa ANTES de recortar, de propósito: assim gasta um selo de
    // segurança por quadro em vez de um por pedaço. Como um pedaço perdido já
    // condena o quadro todo de qualquer forma, não se perde nada com isso.
    public static List<byte[]> Pack(byte[] frame, byte kind, uint frameId, byte[] key)
    {
        byte[] sealed_ = Encrypt(frame, key, frameId, kind);

        int fragCount = Math.Max(1, (sealed_.Length + MaxPayloadPerDatagram - 1) / MaxPayloadPerDatagram);
        if (fragCount > ushort.MaxValue)
            return new List<byte[]>();   // quadro absurdo: descarta em vez de estourar

        var packets = new List<byte[]>(fragCount);

        for (int i = 0; i < fragCount; i++)
        {
            int offset = i * MaxPayloadPerDatagram;
            int size = Math.Min(MaxPayloadPerDatagram, sealed_.Length - offset);

            var packet = new byte[HeaderSize + size];
            packet[0] = TypeMedia;
            packet[1] = kind;
            WriteUInt32(packet, 2, frameId);
            WriteUInt16(packet, 6, (ushort)i);
            WriteUInt16(packet, 8, (ushort)fragCount);
            Buffer.BlockCopy(sealed_, offset, packet, HeaderSize, size);

            packets.Add(packet);
        }

        return packets;
    }

    // ---------- recebimento ----------

    public sealed record Header(byte Kind, uint FrameId, ushort FragIndex, ushort FragCount);

    public static Header? ReadHeader(byte[] packet, int length)
    {
        if (length < HeaderSize) return null;
        if (packet[0] != TypeMedia) return null;

        ushort fragCount = ReadUInt16(packet, 8);
        ushort fragIndex = ReadUInt16(packet, 6);
        if (fragCount == 0 || fragIndex >= fragCount) return null;

        return new Header(packet[1], ReadUInt32(packet, 2), fragIndex, fragCount);
    }

    // ---------- criptografia ----------

    private static byte[] Encrypt(byte[] plain, byte[] key, uint frameId, byte kind)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        // O número do quadro e o tipo entram como "dados associados": não são
        // secretos, mas ficam presos ao pacote. Se alguém trocar o número do
        // quadro no caminho, a verificação falha e o pacote é descartado.
        aes.Encrypt(nonce, plain, cipher, tag, AssociatedData(frameId, kind));

        var result = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, result, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, result, NonceSize + cipher.Length, TagSize);
        return result;
    }

    public static byte[]? Decrypt(byte[] sealed_, byte[] key, uint frameId, byte kind)
    {
        if (sealed_.Length < NonceSize + TagSize) return null;

        var nonce = new byte[NonceSize];
        Buffer.BlockCopy(sealed_, 0, nonce, 0, NonceSize);

        int cipherLength = sealed_.Length - NonceSize - TagSize;
        var cipher = new byte[cipherLength];
        Buffer.BlockCopy(sealed_, NonceSize, cipher, 0, cipherLength);

        var tag = new byte[TagSize];
        Buffer.BlockCopy(sealed_, NonceSize + cipherLength, tag, 0, TagSize);

        var plain = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, AssociatedData(frameId, kind));
            return plain;
        }
        catch (CryptographicException)
        {
            // Pacote adulterado, ou de alguém com outro código de sala.
            return null;
        }
    }

    private static byte[] AssociatedData(uint frameId, byte kind)
    {
        var data = new byte[5];
        WriteUInt32(data, 0, frameId);
        data[4] = kind;
        return data;
    }

    // ---------- utilidades ----------

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 1] = (byte)(value & 0xFF);
    }

    internal static uint ReadUInt32(byte[] buffer, int offset) =>
        (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) |
               (buffer[offset + 2] << 8) | buffer[offset + 3]);

    internal static ushort ReadUInt16(byte[] buffer, int offset) =>
        (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
}
