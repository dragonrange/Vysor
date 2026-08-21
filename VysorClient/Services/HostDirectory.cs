using System.IO;
using System.Text.Json;

namespace VysorClient.Services;

// A fila de sucessão da sala: quem assume se o host fechar o app.
//
// O PROBLEMA QUE ISTO RESOLVE
// Agora a sala mora no PC de alguém. Se essa pessoa fecha o Vysor, o ponto de
// encontro some — e todo mundo fica online, na mesma internet, sem conseguir
// se achar. Pior: quem sabia o endereço de todo mundo era justamente o host
// que acabou de sair.
//
// A SOLUÇÃO
// Enquanto o host está de pé, ele manda pra todos a lista de quem mais pode
// hospedar, em ordem. Cada app guarda essa lista (inclusive em disco, pra
// sobreviver a fechar e abrir). Quando a conexão cai, cada um tenta os
// endereços dessa lista na ordem e entra no primeiro que responder.
//
// Como a ordem é a mesma pra todo mundo, todos escolhem o mesmo sucessor sem
// precisar combinar nada — o que é essencial, porque nesse instante não
// existe mais ninguém no meio pra coordenar. E como todo Vysor já fica
// ouvindo desde que abre, o sucessor não precisa "ligar" nada: ele já está
// pronto. A sala volta em segundos, com o mesmo código, e quem estava
// transmitindo nem precisa parar.
public static class HostDirectory
{
    public sealed record Member(string UserId, string DisplayName, string Address);

    private static readonly object _lock = new();
    private static List<Member> _members = new();

    // Último endereço em que a sala funcionou de verdade. É por ele que
    // começamos a tentar quando o app abre de novo.
    public static string? LastGoodAddress { get; private set; }

    private static string StatePath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ultima-sala.json");

    public static IReadOnlyList<Member> Members
    {
        get { lock (_lock) return _members.ToList(); }
    }

    // Chamado quando o servidor manda a fila atualizada (evento
    // "RoomSuccession").
    public static void Update(IList<string> ids, IList<string> names, IList<string> addresses)
    {
        var list = new List<Member>();
        int count = Math.Min(ids.Count, Math.Min(names.Count, addresses.Count));
        for (int i = 0; i < count; i++)
        {
            if (string.IsNullOrWhiteSpace(addresses[i])) continue;
            list.Add(new Member(ids[i], names[i], addresses[i].Trim()));
        }

        lock (_lock) _members = list;
        Save();
    }

    public static void NoteWorking(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        LastGoodAddress = address.Trim();
        Save();
    }

    public static void Clear()
    {
        lock (_lock) _members = new List<Member>();
        LastGoodAddress = null;
        Save();
    }

    // Os endereços a tentar, em ordem, quando precisamos (re)entrar na sala.
    //
    // myUserId serve pra uma diferença importante: pra falar com o servidor
    // que roda NESTE PC, o caminho certo é 127.0.0.1, não o endereço externo.
    // Ir pela volta da rede pra chegar em si mesmo funciona em algumas
    // máquinas e falha em outras (é o roteador tendo que "dobrar" o tráfego de
    // volta), então nem tentamos.
    public static List<string> CandidateUrls(string myUserId, string? preferFirst = null)
    {
        var urls = new List<string>();

        void Add(string? address, string? ownerId)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            string url = ownerId != null && ownerId == myUserId
                ? LocalServer.LoopbackUrl
                : BuildHubUrl(address);
            if (!urls.Contains(url)) urls.Add(url);
        }

        Add(preferFirst, null);

        List<Member> snapshot;
        lock (_lock) snapshot = _members.ToList();
        foreach (var m in snapshot) Add(m.Address, m.UserId);

        Add(LastGoodAddress, null);

        // Última carta na manga: hospedar aqui mesmo. Se ninguém da lista
        // atender, alguém tem que abrir a sala — e este app já está ouvindo.
        if (!urls.Contains(LocalServer.LoopbackUrl)) urls.Add(LocalServer.LoopbackUrl);

        return urls;
    }

    // Monta a URL do hub a partir de "100.94.12.7" ou "100.94.12.7:5799".
    public static string BuildHubUrl(string address)
    {
        address = address.Trim();

        // Já veio uma URL completa (caso de quem usa um servidor externo).
        if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return address.TrimEnd('/').EndsWith("/roomhub", StringComparison.OrdinalIgnoreCase)
                ? address.TrimEnd('/')
                : address.TrimEnd('/') + "/roomhub";
        }

        if (!address.Contains(':')) address += ":" + LocalServer.DefaultPort;
        return $"http://{address}/roomhub";
    }

    // ---- convite: um texto só, com endereço e código juntos ----
    //
    // Sem isto, entrar numa sala exigiria colar DUAS coisas (o endereço do
    // host e o código). Juntando num texto só, quem entra continua colando
    // uma coisa só, exatamente como era antes.

    public static string BuildInvite(string address, string code)
        => $"{address.Trim()}/{code.Trim().ToUpperInvariant()}";

    // Aceita "100.94.12.7/AB12CD", "100.94.12.7:5799/AB12CD" ou só "AB12CD"
    // (este último pro caso de você voltar a usar um servidor na internet).
    public static (string? Address, string Code) ParseInvite(string text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0) return (null, string.Empty);

        // Tolera alguém colar uma URL inteira.
        text = text.Replace("http://", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("https://", "", StringComparison.OrdinalIgnoreCase);

        int slash = text.LastIndexOf('/');
        if (slash < 0) return (null, text.ToUpperInvariant());

        string address = text[..slash].Trim();
        string code = text[(slash + 1)..].Trim().ToUpperInvariant();
        return (address.Length == 0 ? null : address, code);
    }

    // ---- memória entre execuções ----

    private sealed class State
    {
        public List<Member> Members { get; set; } = new();
        public string? LastGood { get; set; }
    }

    private static void Save()
    {
        try
        {
            List<Member> snapshot;
            lock (_lock) snapshot = _members.ToList();
            var state = new State { Members = snapshot, LastGood = LastGoodAddress };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Não conseguiu gravar (pasta somente leitura): a sala continua
            // funcionando, só não sobrevive a fechar e abrir o app.
        }
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath));
            if (state == null) return;
            lock (_lock) _members = state.Members ?? new List<Member>();
            LastGoodAddress = state.LastGood;
        }
        catch
        {
            // Arquivo corrompido: começa do zero, sem drama.
        }
    }
}
