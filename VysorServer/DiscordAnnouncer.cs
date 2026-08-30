using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VysorServer;

// Avisa um canal do Discord quando uma sala é criada, usando um bot.
//
// POR QUE AQUI NO SERVIDOR E NÃO NO APP
// A versão anterior mandava a mensagem do próprio Vysor de cada pessoa, com um
// endereço de webhook embutido no programa. Aquilo funcionava, mas tinha dois
// limites que não dava pra contornar:
//
//  1. O endereço é um SEGREDO — quem o tem posta o que quiser naquele canal,
//     com qualquer nome e qualquer foto. Estando dentro do programa, ele está
//     na máquina de todo mundo que instalou.
//  2. Trocar de canal exigia publicar uma versão nova do app pra todos.
//
// Com o bot, o segredo fica só aqui, numa variável de ambiente que ninguém
// além do dono do servidor enxerga. Trocar de canal é trocar uma variável.
//
// E NÃO PRECISA DE CONEXÃO PERMANENTE. Essa era a razão de eu ter descartado
// bot antes, e estava errada: conexão permanente é necessária pra RECEBER
// eventos do Discord. Pra ENVIAR uma mensagem basta uma chamada HTTPS comum —
// que acontece no exato momento em que a sala é criada, quando este servidor
// já está acordado atendendo quem a criou.
public class DiscordAnnouncer
{
    private readonly string? _token;
    private readonly string? _channelId;
    private readonly HttpClient _http;
    private readonly ILogger<DiscordAnnouncer> _log;

    // Salas já anunciadas. Sem isto, uma reconexão (que refaz o caminho de
    // criar/entrar) publicaria a mesma sala de novo, e o canal encheria de
    // mensagens repetidas justamente quando a internet de alguém está ruim.
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public DiscordAnnouncer(IConfiguration config, ILogger<DiscordAnnouncer> log)
    {
        _log = log;
        _token = config["DISCORD_BOT_TOKEN"];
        _channelId = config["DISCORD_CHANNEL_ID"];

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        if (!string.IsNullOrWhiteSpace(_token))
        {
            _http.DefaultRequestHeaders.Add("Authorization", $"Bot {_token.Trim()}");
            _http.DefaultRequestHeaders.Add("User-Agent", "Vysor (https://github.com/dragonrange/Vysor, 1.0)");
        }
    }

    // ESTE É O INTERRUPTOR DE VOLTA.
    //
    // Sem as duas variáveis de ambiente, o bot não existe: o servidor responde
    // "não anunciei" e o app cai sozinho no caminho antigo (o webhook embutido
    // — ver DiscordWebhook no cliente). Ou seja, desfazer é APAGAR DUAS
    // VARIÁVEIS no painel do Render; ninguém precisa atualizar nada, e não sai
    // versão nova do app.
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_token) && !string.IsNullOrWhiteSpace(_channelId);

    // Posta o convite. Devolve false quando não fez nada — é esse false que
    // manda o app usar o caminho antigo.
    // Lista os canais de texto de um servidor, pra pessoa poder CLICAR num em
    // vez de descobrir e digitar um número de canal (o que exigiria ligar o
    // "modo desenvolvedor" do Discord — passo em que quase todo mundo desiste).
    // Devolve TAMBÉM o motivo quando dá errado.
    //
    // A primeira versão só devolvia a lista, e uma lista vazia podia significar
    // quatro coisas completamente diferentes: falta o token no servidor, o
    // Discord não informou qual servidor, a chamada foi recusada, ou o bot
    // realmente não vê canal nenhum. Cada uma pede uma correção diferente — e a
    // página mostrava a mesma frase pras quatro. Foi exatamente esse tipo de
    // "falha sem pista" que já custou uma rodada de adivinhação neste projeto.
    public sealed record ChannelList(List<(string Id, string Name)> Channels, string? Problem);

    public async Task<ChannelList> ListTextChannelsAsync(string? guildId)
    {
        var result = new List<(string, string)>();

        if (string.IsNullOrWhiteSpace(_token))
            return new ChannelList(result,
                "O servidor do Vysor ainda não recebeu o token do bot. " +
                "Falta definir a variável DISCORD_BOT_TOKEN no painel do Render.");

        guildId = Clean(guildId, 25);
        if (guildId.Length == 0 || !guildId.All(char.IsDigit))
            return new ChannelList(result,
                "O Discord não informou qual servidor foi escolhido. " +
                "Volte ao Vysor e clique em adicionar novamente.");

        try
        {
            var response = await _http.GetAsync($"https://discord.com/api/v10/guilds/{guildId}/channels");
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Discord recusou listar canais: {Status}", response.StatusCode);

                string detail = (int)response.StatusCode switch
                {
                    401 => "o token do bot está inválido ou foi regerado — atualize DISCORD_BOT_TOKEN no Render",
                    403 => "o bot não tem permissão de Ver canais neste servidor",
                    404 => "o bot não está neste servidor (a instalação não chegou a concluir)",
                    429 => "o Discord pediu pra esperar um pouco; tente de novo em instantes",
                    _   => $"o Discord respondeu {(int)response.StatusCode}"
                };

                return new ChannelList(result, $"Não consegui listar os canais: {detail}.");
            }

            using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                // 0 = canal de texto comum. Os outros (voz, categoria, fórum)
                // não recebem mensagem simples e só confundiriam a escolha.
                if (!item.TryGetProperty("type", out var type) || type.GetInt32() != 0) continue;
                if (!item.TryGetProperty("id", out var id)) continue;
                if (!item.TryGetProperty("name", out var name)) continue;

                result.Add((id.GetString() ?? "", name.GetString() ?? ""));
                if (result.Count >= 50) break;   // servidores grandes: não vira uma parede
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Falha ao listar canais");
            return new ChannelList(result, $"Erro ao falar com o Discord: {ex.GetType().Name}.");
        }

        return new ChannelList(result, result.Count > 0
            ? null
            : "O bot está no servidor, mas não enxerga nenhum canal de texto. " +
              "Confira se o cargo Vysor tem a permissão Ver canais.");
    }

    // Anuncia num canal ESCOLHIDO por quem criou a sala. É o que permite cada
    // pessoa avisar o servidor dela, em vez de existir um único canal fixo pro
    // mundo inteiro.
    //
    // O canal vem do app, e isso é aceitável: o bot só está em servidores onde
    // um administrador o instalou, então o alcance disto é exatamente o
    // conjunto de servidores que já aceitaram o Vysor. E o TEXTO da mensagem é
    // montado aqui — o app não escolhe o que é dito, só para onde vai.
    public async Task<bool> AnnounceRoomAsync(string roomCode, string displayName, string? channelId)
    {
        channelId = Clean(channelId, 25);
        if (channelId.Length == 0 || !channelId.All(char.IsDigit)) return false;
        return await PostAsync(roomCode, displayName, channelId);
    }

    // Canal fixo do servidor (variável de ambiente). Continua existindo como
    // reserva pra quem ainda não escolheu canal nenhum.
    public async Task<bool> AnnounceRoomAsync(string roomCode, string displayName)
    {
        if (!IsConfigured) return false;
        return await PostAsync(roomCode, displayName, _channelId!.Trim());
    }

    private async Task<bool> PostAsync(string roomCode, string displayName, string channelId)
    {
        if (string.IsNullOrWhiteSpace(_token)) return false;

        roomCode = Clean(roomCode, 12);
        if (roomCode.Length == 0) return false;

        // A trava de repetição é por SALA + CANAL: a mesma sala anunciada em
        // dois canais diferentes são dois avisos legítimos, enquanto a mesma
        // sala no mesmo canal (o que acontece numa reconexão) é repetição.
        string key = $"{roomCode}@{channelId}";

        lock (_lock)
        {
            if (!_announced.Add(key)) return true;   // já anunciada: nada a fazer

            // Trava de memória: o servidor é de graça e vive de RAM. Uma sala
            // só volta a poder ser anunciada depois disso, o que é
            // irrelevante na prática (códigos são sorteados).
            if (_announced.Count > 500) _announced.Clear();
        }

        try
        {
            string who = Clean(displayName, 40);
            if (who.Length == 0) who = "Alguém";

            string link = $"https://vysorserver-cjxi.onrender.com/j/{roomCode}";
            string content =
                $"🖥️ **{who}** abriu uma sala no Vysor\n{link}\n-# ou entre pelo código: **{roomCode}**";

            // "allowed_mentions" vazio impede que um nome contendo "@everyone"
            // vire menção pro servidor inteiro. O nome é digitado por quem usa
            // o app, então tratá-lo como texto confiável seria entregar o sino
            // do servidor pra qualquer um.
            string json =
                "{\"content\":" + JsonString(content) + ",\"allowed_mentions\":{\"parse\":[]}}";

            using var body = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(
                $"https://discord.com/api/v10/channels/{channelId}/messages", body);

            if (!response.IsSuccessStatusCode)
            {
                // Vale registrar: token errado, canal errado ou bot sem
                // permissão de escrever ali são os três erros prováveis, e
                // sem esta linha eles seriam indistinguíveis de "não
                // configurado".
                _log.LogWarning("Discord recusou o anúncio: {Status}", response.StatusCode);

                // Deixa a sala poder ser anunciada de novo numa próxima
                // tentativa, em vez de marcá-la como feita.
                lock (_lock) { _announced.Remove(key); }
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Falha ao avisar o Discord");
            lock (_lock) { _announced.Remove(key); }
            return false;
        }
    }

    // Tira o que quebraria a formatação ou viraria menção.
    private static string Clean(string? text, int max)
    {
        text = (text ?? string.Empty).Trim();
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '*' or '_' or '`' or '~' or '@' or '\\' or '\n' or '\r' or '<' or '>') continue;
            sb.Append(c);
            if (sb.Length >= max) break;
        }
        return sb.ToString().Trim();
    }

    private static string JsonString(string value)
    {
        var sb = new StringBuilder(value.Length + 16);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
