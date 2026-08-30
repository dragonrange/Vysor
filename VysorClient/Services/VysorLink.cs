namespace VysorClient.Services;

// O formato de convite clicável do Vysor: "vysor://T2X9RF".
//
// POR QUE ISTO EXISTE
// Até agora, convidar alguém era mandar um texto pra pessoa DIGITAR. Todo
// passo manual nesse caminho é um lugar onde alguém erra uma letra e conclui
// que "o Vysor não achou a sala" — já aconteceu nesta própria sala de testes.
//
// Um link resolve isso e, mais importante, é a peça que qualquer integração
// precisa por baixo. O botão "Entrar" do Discord, um atalho, uma mensagem no
// WhatsApp: todos terminam na MESMA pergunta — "abra o Vysor e entre na sala
// X". Tendo um formato único pra isso, cada nova forma de convidar vira só uma
// maneira diferente de produzir o mesmo link, em vez de um caminho novo de
// ponta a ponta.
//
// O código continua aceitando o convite completo com endereço
// ("100.94.12.7:5799/AB12CD"), porque a sala pode estar hospedada no PC de
// alguém — quem recebe só o código não teria como chegar lá.
public static class VysorLink
{
    public const string Scheme = "vysor";
    private const string Prefix = Scheme + "://";

    // Monta o link a partir do que o app já tem em mãos: o convite completo
    // quando existe, senão o código puro.
    public static string Build(string codeOrInvite)
    {
        string payload = (codeOrInvite ?? string.Empty).Trim();
        return payload.Length == 0 ? string.Empty : Prefix + Uri.EscapeDataString(payload);
    }

    // Extrai o código/convite de um link. Devolve null se não for um link do
    // Vysor — quem chama usa isso pra ignorar em silêncio qualquer outro
    // argumento de linha de comando.
    //
    // Tolerante de propósito com a barra final: o Windows e alguns aplicativos
    // de mensagem acrescentam uma sozinhos ("vysor://T2X9RF/"), e um convite
    // que falha por causa disso seria impossível de diagnosticar pra quem
    // recebeu.
    public static string? TryParse(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return null;

        string text = argument.Trim().Trim('"');
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;

        string payload = text[Prefix.Length..].TrimEnd('/');
        if (payload.Length == 0) return null;

        try { payload = Uri.UnescapeDataString(payload); } catch { }

        // Trava de sanidade: o que vem daqui vai virar uma tentativa de
        // conexão. Um argumento absurdo (alguém montando um link à mão) para
        // aqui em vez de descer pro resto do app.
        return payload.Length <= 200 ? payload : null;
    }

    // Registra o "vysor://" para ESTE usuário, sem precisar de administrador.
    //
    // POR QUE EXISTE, se o instalador já faz isso
    // O instalador grava a associação para a máquina toda, o que exige
    // elevação. Só que nem todo mundo consegue passar pelo instalador: o Smart
    // App Control do Windows 11 recusa executáveis sem assinatura digital, e aí
    // a única saída é a versão portátil — que não instala nada e, sem isto,
    // deixaria os convites por link sem funcionar.
    //
    // Gravar no ramo do usuário tem precedência sobre o da máquina, então
    // chamar isto sempre é seguro: no computador que passou pelo instalador,
    // aponta pro mesmo lugar; no portátil, faz os links passarem a funcionar.
    // O caminho se corrige sozinho se a pasta do app mudar.
    public static void RegisterForCurrentUser()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0 || !System.IO.File.Exists(exe)) return;

            string root = $@"Software\Classes\{Scheme}";

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root);
            if (key == null) return;
            key.SetValue("", "URL:Vysor");
            key.SetValue("URL Protocol", "");

            using var icon = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{root}\DefaultIcon");
            icon?.SetValue("", $"\"{exe}\",0");

            using var command = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{root}\shell\open\command");
            command?.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch
        {
            // Perfil restrito ou política de empresa: os links deixam de abrir
            // sozinhos, mas o código da sala continua funcionando na mão.
        }
    }

    // --- Escolha de canal do Discord -----------------------------------------
    //
    // Um segundo tipo de link: "vysor://canal/<id>/<nome>". Ele não convida
    // ninguém pra sala — ele traz de volta, do navegador pro app, o canal que
    // a pessoa acabou de escolher depois de instalar o Vysor no servidor dela.
    //
    // Usar o MESMO mecanismo de link do convite economiza uma peça inteira: o
    // Windows já sabe entregar "vysor://" pro app, e a instância única já sabe
    // levar isso pra janela que está aberta. Sem isso, seria preciso inventar
    // um jeito de o navegador falar com o programa.
    private const string ChannelMarker = "canal/";

    public sealed record ChannelChoice(string ChannelId, string ChannelName);

    public static ChannelChoice? TryParseChannel(string? argument)
    {
        string? payload = TryParse(argument);
        if (payload == null || !payload.StartsWith(ChannelMarker, StringComparison.OrdinalIgnoreCase))
            return null;

        string[] parts = payload[ChannelMarker.Length..].Split('/');
        if (parts.Length == 0) return null;

        string id = parts[0].Trim();
        // Identificadores do Discord são só dígitos. Recusar o resto evita que
        // um link forjado vire um pedido de postar em lugar nenhum.
        if (id.Length is < 5 or > 25 || !id.All(char.IsDigit)) return null;

        string name = "canal escolhido";
        if (parts.Length > 1 && parts[1].Length > 0)
        {
            try
            {
                // O nome vem em base64url porque nome de canal aceita
                // caracteres que estragariam o formato do link.
                string b64 = parts[1].Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Trim();
                if (decoded.Length is > 0 and <= 60) name = decoded;
            }
            catch { }
        }

        return new ChannelChoice(id, name);
    }

    // Acha o link entre os argumentos que o Windows passou. O primeiro
    // argumento pode ser qualquer coisa dependendo de como o app foi aberto,
    // então varremos todos em vez de assumir a posição.
    public static string? FromCommandLine(IEnumerable<string> args)
    {
        foreach (string arg in args)
        {
            string? parsed = TryParse(arg);
            if (parsed != null) return parsed;
        }
        return null;
    }
}
