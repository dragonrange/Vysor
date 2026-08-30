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
    public async Task<bool> AnnounceRoomAsync(string roomCode, string displayName)
    {
        if (!IsConfigured) return false;

        roomCode = Clean(roomCode, 12);
        if (roomCode.Length == 0) return false;

        lock (_lock)
        {
            if (!_announced.Add(roomCode)) return true;   // já anunciada: nada a fazer

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
                $"https://discord.com/api/v10/channels/{_channelId!.Trim()}/messages", body);

            if (!response.IsSuccessStatusCode)
            {
                // Vale registrar: token errado, canal errado ou bot sem
                // permissão de escrever ali são os três erros prováveis, e
                // sem esta linha eles seriam indistinguíveis de "não
                // configurado".
                _log.LogWarning("Discord recusou o anúncio: {Status}", response.StatusCode);

                // Deixa a sala poder ser anunciada de novo numa próxima
                // tentativa, em vez de marcá-la como feita.
                lock (_lock) { _announced.Remove(roomCode); }
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Falha ao avisar o Discord");
            lock (_lock) { _announced.Remove(roomCode); }
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
