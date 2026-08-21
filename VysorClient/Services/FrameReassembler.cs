namespace VysorClient.Services;

// Remonta os quadros que chegaram picotados pela rede.
//
// Do outro lado, um quadro de vídeo virou dezenas de pacotes independentes.
// Eles chegam fora de ordem, às vezes duplicados, e às vezes não chegam. Esta
// classe junta os que chegaram, entrega o quadro quando ele fica completo, e
// — o mais importante — sabe DESISTIR.
//
// POR QUE DESISTIR É A PARTE CRÍTICA
// Sem uma regra de desistência, cada quadro incompleto ficaria guardado pra
// sempre esperando um pedaço que nunca vem. Numa transmissão de uma hora com
// perda de 1%, isso seria centenas de quadros pela metade ocupando memória —
// o app iria inchando até morrer, e o usuário só veria "o Vysor fica pesado
// depois de um tempo". Por isso existem os dois limites abaixo.
public class FrameReassembler
{
    // Quantos quadros incompletos podem estar em montagem ao mesmo tempo. Em
    // rede saudável isso fica em 1 ou 2; o limite existe pro caso ruim.
    private const int MaxPending = 16;

    // Quadro cujo primeiro pedaço chegou há mais que isto está perdido: numa
    // transmissão ao vivo, esperar mais que isso não tem sentido.
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(2);

    private sealed class Pending
    {
        public required byte[]?[] Fragments;
        public int Received;
        public int TotalBytes;
        public DateTime FirstSeen;
        public byte Kind;
    }

    private readonly Dictionary<uint, Pending> _pending = new();
    private readonly byte[] _key;

    // Último quadro entregue. Serve pra ignorar pedaços atrasados de quadros
    // que já foram entregues ou já foram dados como perdidos — sem isso, eles
    // reabririam a montagem de um quadro velho, que nunca completaria e ainda
    // ocuparia uma das vagas.
    private uint _lastDelivered;
    private bool _hasDelivered;

    public FrameReassembler(byte[] key)
    {
        _key = key;
    }

    public int PendingCount => _pending.Count;
    public long DroppedFrames { get; private set; }

    // Recebe um pacote. Devolve o quadro completo, ou null se ainda falta
    // pedaço (ou se o pacote não presta).
    public byte[]? Accept(byte[] packet, int length)
    {
        var header = PeerPacket.ReadHeader(packet, length);
        if (header == null) return null;

        if (_hasDelivered && IsOlderOrEqual(header.FrameId, _lastDelivered)) return null;

        DropExpired();

        if (!_pending.TryGetValue(header.FrameId, out var pending))
        {
            if (_pending.Count >= MaxPending) DropOldest();

            pending = new Pending
            {
                Fragments = new byte[]?[header.FragCount],
                FirstSeen = DateTime.UtcNow,
                Kind = header.Kind
            };
            _pending[header.FrameId] = pending;
        }

        // Quantidade de pedaços diferente da anunciada antes: pacote corrompido
        // ou de outra transmissão. Descarta o quadro todo em vez de montar
        // uma imagem misturada.
        if (pending.Fragments.Length != header.FragCount)
        {
            _pending.Remove(header.FrameId);
            DroppedFrames++;
            return null;
        }

        // Pedaço repetido: ignora sem contar duas vezes.
        if (pending.Fragments[header.FragIndex] != null) return null;

        int payloadSize = length - PeerPacket.HeaderSize;
        var payload = new byte[payloadSize];
        Buffer.BlockCopy(packet, PeerPacket.HeaderSize, payload, 0, payloadSize);

        pending.Fragments[header.FragIndex] = payload;
        pending.Received++;
        pending.TotalBytes += payloadSize;

        if (pending.Received < pending.Fragments.Length) return null;

        // Completou: junta tudo na ordem e abre.
        var sealed_ = new byte[pending.TotalBytes];
        int offset = 0;
        foreach (var fragment in pending.Fragments)
        {
            if (fragment == null) return null;   // não deveria acontecer
            Buffer.BlockCopy(fragment, 0, sealed_, offset, fragment.Length);
            offset += fragment.Length;
        }

        _pending.Remove(header.FrameId);
        _lastDelivered = header.FrameId;
        _hasDelivered = true;

        byte[]? frame = PeerPacket.Decrypt(sealed_, _key, header.FrameId, pending.Kind);
        if (frame == null) DroppedFrames++;
        return frame;
    }

    public byte LastKind { get; private set; }

    private void DropExpired()
    {
        if (_pending.Count == 0) return;
        DateTime cutoff = DateTime.UtcNow - MaxAge;

        List<uint>? expired = null;
        foreach (var (id, pending) in _pending)
        {
            if (pending.FirstSeen < cutoff) (expired ??= new List<uint>()).Add(id);
        }

        if (expired == null) return;
        foreach (uint id in expired)
        {
            _pending.Remove(id);
            DroppedFrames++;
        }
    }

    private void DropOldest()
    {
        uint oldest = 0;
        DateTime oldestTime = DateTime.MaxValue;
        foreach (var (id, pending) in _pending)
        {
            if (pending.FirstSeen < oldestTime) { oldestTime = pending.FirstSeen; oldest = id; }
        }
        _pending.Remove(oldest);
        DroppedFrames++;
    }

    // Comparação que aguenta o contador dar a volta (depois de 4 bilhões de
    // quadros ele volta a zero; sem isto, o app pararia de aceitar quadros
    // pra sempre no momento da virada).
    private static bool IsOlderOrEqual(uint candidate, uint reference)
        => (int)(candidate - reference) <= 0;
}
