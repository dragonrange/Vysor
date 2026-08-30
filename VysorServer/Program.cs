using VysorServer;
using VysorServer.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Render fornece a porta pública pela variável de ambiente PORT.
// Localmente, usamos 10000 como fallback.
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Registra o SignalR (o motor de comunicação em tempo real).
builder.Services.AddSignalR(options =>
{
    // Este servidor não repassa mais vídeo nem áudio de jeito nenhum (ver
    // RoomHub.cs) — só sinalização (códigos de sala, endereços, listas de
    // participantes), sempre uns poucos KB. 256 KB é folga generosa pra isso;
    // NÃO aumente este valor pra "resolver" um problema de vídeo — se um
    // frame estiver tentando passar por aqui, o bug está em outro lugar (o
    // cliente caiu de volta pra um caminho de repasse que não deveria
    // existir mais).
    options.MaximumReceiveMessageSize = 256 * 1024;

    // Prazos de manutenção da conexão, mais folgados que o padrão (15s/30s).
    // Sob carga de vídeo, um pico de tráfego podia atrasar o "ping" de
    // manutenção o suficiente pra derrubar clientes que estavam
    // perfeitamente saudáveis — e uma queda coletiva esvaziava a sala.
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);

    // Permite que o servidor processe várias mensagens do mesmo cliente ao
    // mesmo tempo, em vez de estritamente uma por vez.
    options.MaximumParallelInvocationsPerClient = 4;
});

// Registra nosso "cérebro" das salas como singleton (uma única instância
// compartilhada por todas as conexões, viva enquanto o servidor rodar).
builder.Services.AddSingleton<RoomManager>();

// Aviso no Discord (ver DiscordAnnouncer.cs). Fica INERTE sem as variáveis de
// ambiente DISCORD_BOT_TOKEN e DISCORD_CHANNEL_ID — e é justamente isso que
// permite desfazer sem publicar nada: apagou as variáveis, o app volta sozinho
// pro caminho antigo.
builder.Services.AddSingleton<DiscordAnnouncer>();

// Faxina periódica: remove quem caiu e não voltou dentro do prazo (avisando
// os outros só nesse momento) e apaga salas vazias antigas.
builder.Services.AddHostedService<RoomSweeper>();

// Libera qualquer origem a se conectar. Como o nosso "cliente" é um app
// desktop (não um navegador com regras de segurança), isso é seguro aqui.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

app.UseCors();

// Rotas simples só pra conferir no navegador se o servidor está de pé.
//
// O "/" NÃO é decoração: é exatamente o endereço que a checagem de saúde do
// Fly.io consulta a cada 30 segundos (veja [[http_service.checks]] no
// fly.toml). Se ele parar de responder 200, o Fly conclui que o servidor
// quebrou e REINICIA a máquina — e um reinício apaga todas as salas da
// memória, derrubando a sala de todo mundo de uma vez. Ou seja: mexer aqui
// sem ajustar o fly.toml junto tem consequência de verdade.
app.MapGet("/", () => "Vysor — servidor de sinalização rodando! ✅");
app.MapGet("/status", () => "Vysor — servidor de sinalização rodando! ✅");

// "Você tem esta sala aberta?" — mesma rota que o Vysor expõe quando hospeda
// no PC de alguém. Serve pro app escolher onde entrar quando existe mais de
// um lugar possível, em vez de rachar a sala em duas.
app.MapGet("/room/{code}", (string code, RoomManager rooms) =>
    rooms.GetRoom((code ?? string.Empty).Trim().ToUpperInvariant()) != null
        ? Results.Ok("existe")
        : Results.NotFound());

// ---- Convite clicável: /j/CODIGO ----
//
// POR QUE ISTO PRECISA EXISTIR NO SERVIDOR
// O app entende "vysor://T2X9RF" e entra na sala sozinho. Só que o Discord
// (como quase todo lugar) NÃO transforma um endereço desses em link clicável —
// ele sai como texto morto na mensagem. E botão também não resolve: botão de
// link no Discord só aceita http/https.
//
// Então o convite precisa ser um endereço https de verdade. Esta página é a
// ponte: ela recebe o clique, manda o navegador abrir "vysor://CODIGO" e o
// Windows entrega pro Vysor. Uma linha de HTML resolve o que nenhuma
// configuração do Discord resolveria.
//
// De quebra, ela atende quem AINDA NÃO TEM o Vysor — antes essa pessoa
// recebia um texto que não fazia nada. Agora ela cai numa página que explica
// o que é e oferece o download.
app.MapGet("/j/{code}", (string code) =>
{
    // Só letras e números, e curto. O que vem daqui é escrito dentro de uma
    // página HTML: aceitar qualquer coisa seria deixar alguém montar um link
    // que injeta script na página (e o link viajaria com a nossa cara).
    string clean = new string((code ?? string.Empty)
        .Where(char.IsLetterOrDigit).Take(12).ToArray()).ToUpperInvariant();

    if (clean.Length == 0) return Results.NotFound();

    string html = $$"""
<!doctype html>
<html lang="pt-BR"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Entrar na sala {{clean}} — Vysor</title>
<style>
 body{background:#0d0d10;color:#e5e7eb;font-family:Segoe UI,system-ui,sans-serif;
      display:flex;min-height:100vh;margin:0;align-items:center;justify-content:center;text-align:center}
 .box{padding:32px;max-width:420px}
 .code{font-size:34px;font-weight:700;letter-spacing:3px;color:#5865f2;margin:14px 0}
 a.btn{display:inline-block;background:#5865f2;color:#fff;text-decoration:none;
       padding:13px 26px;border-radius:8px;font-weight:700;margin-top:8px}
 p{color:#9ca3af;line-height:1.5}
 .small{font-size:13px;margin-top:26px}
 .small a{color:#8b93f8}
</style></head>
<body><div class="box">
 <h2>Abrindo o Vysor…</h2>
 <p>Se nada acontecer, use o botão. O código da sala é:</p>
 <div class="code">{{clean}}</div>
 <a class="btn" href="vysor://{{clean}}">Entrar na sala</a>
 <p class="small">Ainda não tem o Vysor?
   <a href="https://github.com/dragonrange/Vysor/releases/latest">Baixe aqui</a>.</p>
</div>
<script>
 // Tenta abrir sozinho. Se o Vysor não estiver instalado, o navegador
 // simplesmente ignora e a pessoa fica na página, com o botão e o código.
 location.href = "vysor://{{clean}}";
</script>
</body></html>
""";

    return Results.Content(html, "text/html; charset=utf-8");
});

// ---- Páginas exigidas pra verificação do app no Discord ----
//
// O Discord exige um link de termos e um de privacidade pra verificar um
// aplicativo. Ficam aqui porque este servidor já existe e já é o endereço
// público do Vysor — criar um site à parte só pra duas páginas seria mais uma
// coisa pra manter e pra expirar.
//
// O conteúdo é verdadeiro e específico, não um modelo genérico: descreve o que
// este servidor realmente faz e o que ele realmente guarda. Um texto de
// privacidade que não corresponde ao programa é pior do que nenhum.
app.MapGet("/termos", () => Results.Content(LegalPages.Terms, "text/html; charset=utf-8"));
app.MapGet("/privacidade", () => Results.Content(LegalPages.Privacy, "text/html; charset=utf-8"));

// Estado da integração com o Discord, consultado pelo app quando ele abre.
//
// Rota HTTP simples de propósito: o app NÃO abre conexão com o servidor
// enquanto está na tela inicial (só ao criar/entrar numa sala), e obrigá-lo a
// abrir só pra isso seria caro. De quebra, esta chamada ACORDA o servidor —
// então criar a sala logo depois já pega tudo de pé, sem a espera do plano
// grátis.
app.MapGet("/discord/estado", async (HttpContext ctx, DiscordAnnouncer discord) =>
    Results.Text(await discord.CheckAsync(ctx.Request.Query["canal"].ToString()),
                 "text/plain; charset=utf-8"));

// ---- "Em qual dos MEUS servidores o Vysor está?" ----
//
// O QUE ISTO RESOLVE
// Antes, avisar no Discord dependia de alguém ESCOLHER um canal — e só admins
// conseguem. Um membro comum ficava sem aviso, mesmo estando no mesmo grupo,
// com o bot instalado ali do lado.
//
// Aqui a pessoa entra com a conta do Discord dela (uma vez) e o Vysor descobre
// sozinho: cruza os servidores dela com os servidores onde o bot está. Achou um
// só, pronto — nem escolha de canal, nem ser admin, nem digitar nada.
//
// O CRUZAMENTO ACONTECE NO NAVEGADOR DELA, de propósito. A lista de servidores
// do bot é pedida pra cá, a lista dela vem direto do Discord, e as duas se
// encontram na máquina dela. Este servidor nunca vê de quais servidores a
// pessoa participa — o que é a diferença entre "descobrir onde avisar" e
// "coletar o perfil social de alguém".
app.MapGet("/discord/servidores-do-bot", async (DiscordAnnouncer discord) =>
    Results.Json(await discord.BotGuildIdsAsync()));

app.MapGet("/discord/canal-principal/{guildId}", async (string guildId, DiscordAnnouncer discord) =>
{
    // Só responde por servidores onde o bot REALMENTE está: sem isso, este
    // endereço viraria uma forma de qualquer um perguntar o nome de canais de
    // servidores alheios.
    var allowed = await discord.BotGuildIdsAsync();
    if (!allowed.Contains(guildId)) return Results.NotFound();

    var channel = await discord.MainChannelOfAsync(guildId);
    return channel == null
        ? Results.NotFound()
        : Results.Json(new { id = channel.Value.Id, name = channel.Value.Name });
});

app.MapGet("/discord/conectar", () => Results.Content(LegalPages.Shell("Conectar o Discord — Vysor", """
  <h2 id="t">Procurando seus servidores…</h2>
  <p id="m">Um instante.</p>
  <div class="list" id="l"></div>
  <script>
  (async () => {
    const t = document.getElementById('t'), m = document.getElementById('m'), l = document.getElementById('l');
    const say = (title, msg) => { t.textContent = title; m.innerHTML = msg; };

    // O Discord devolve a chave depois do "#", que NUNCA e enviado a servidor
    // nenhum pelo navegador. Ela existe so aqui, nesta aba, por segundos.
    const token = new URLSearchParams(location.hash.slice(1)).get('access_token');
    if (!token) return say('Nao consegui entrar',
      'O Discord nao devolveu a autorizacao. Volte ao Vysor e tente de novo.');

    try {
      const meus = await (await fetch('https://discord.com/api/v10/users/@me/guilds',
        { headers: { Authorization: 'Bearer ' + token } })).json();
      const doBot = await (await fetch('/discord/servidores-do-bot')).json();

      const set = new Set(doBot);
      const iguais = meus.filter(g => set.has(g.id));

      if (iguais.length === 0) return say('Nenhum dos seus servidores tem o Vysor',
        'Peca a quem administra o servidor para instalar o Vysor nele. ' +
        'Depois e so voltar aqui: vai funcionar sozinho, sem voce configurar nada.');

      for (const g of iguais) {
        const r = await fetch('/discord/canal-principal/' + g.id);
        if (!r.ok) continue;
        const c = await r.json();
        const a = document.createElement('a');
        a.className = 'ch';
        a.href = 'vysor://canal/' + c.id + '/' + btoa(unescape(encodeURIComponent(c.name)))
                   .replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
        a.textContent = g.name + '  ->  #' + c.name;
        l.appendChild(a);
      }

      if (!l.children.length) return say('Achei o servidor, mas nao o canal',
        'Confira se o Vysor tem permissao de <b>Ver canais</b> nesse servidor.');

      say(iguais.length === 1 ? 'Achei!' : 'Achei mais de um',
        iguais.length === 1
          ? 'Clique abaixo para confirmar e voltar ao Vysor.'
          : 'Escolha em qual servidor voce quer que os convites aparecam.');
    } catch (e) {
      say('Deu problema', 'Nao consegui falar com o Discord agora. Tente de novo em instantes.');
    }
  })();
  </script>
"""), "text/html; charset=utf-8"));

// ---- Depois de instalar o Vysor num servidor do Discord ----
//
// O Discord manda a pessoa pra cá assim que ela autoriza, com o identificador
// do servidor escolhido. Aqui perguntamos ao Discord quais canais existem lá e
// deixamos ela CLICAR num — em vez de pedir que descubra e digite um número de
// canal, que exigiria ligar o "modo desenvolvedor" e é onde quase todo mundo
// desiste.
//
// A escolha volta pro app pelo mesmo "vysor://" que já usamos pros convites.
app.MapGet("/discord/instalado", async (HttpContext ctx, DiscordAnnouncer discord) =>
{
    string guildId = ctx.Request.Query["guild_id"].ToString();
    var result = await discord.ListTextChannelsAsync(guildId);

    string body;
    if (result.Channels.Count == 0)
    {
        // Diz O QUE deu errado, não só que deu. Ver o comentário em
        // DiscordAnnouncer.ListTextChannelsAsync: uma lista vazia tinha quatro
        // causas possíveis, cada uma com uma correção diferente, e mostrar a
        // mesma frase pras quatro obrigava a adivinhar.
        string problem = System.Net.WebUtility.HtmlEncode(
            result.Problem ?? "Não consegui ver os canais deste servidor.");

        body = $"""
          <h2>Quase lá</h2>
          <p>O Vysor foi adicionado ao servidor, mas ainda falta uma coisa:</p>
          <p style="color:#faa61a">{problem}</p>
          <p class="small">Resolvido isso, é só abrir este endereço de novo
             (ou clicar em adicionar outra vez, no Vysor).</p>
        """;
    }
    else
    {
        var list = new System.Text.StringBuilder();
        foreach (var (id, name) in result.Channels)
        {
            // Nome em base64url: nome de canal aceita caracteres que
            // estragariam o formato do link de volta.
            string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(name))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            list.Append($"<a class=\"ch\" href=\"vysor://canal/{id}/{encoded}\">#{System.Net.WebUtility.HtmlEncode(name)}</a>");
        }

        body = $"""
          <h2>Em qual canal avisar?</h2>
          <p>Quando você abrir uma sala no Vysor, o convite vai pra este canal.</p>
          <div class="list">{list}</div>
          <p class="small">Escolha um e o Vysor guarda sozinho. Dá pra trocar depois.</p>
        """;
    }

    return Results.Content(LegalPages.Shell("Escolher canal — Vysor", body), "text/html; charset=utf-8");
});

// Aqui é onde os clientes (o app Vysor dos seus amigos) vão se conectar.
app.MapHub<RoomHub>("/roomhub", options =>
{
    // Idem: o padrão de 32 KB do SignalR já sobra pra sinalização. Não
    // precisa mais da folga de antes, porque vídeo/áudio nunca mais entram
    // nesse buffer.
    options.ApplicationMaxBufferSize = 256 * 1024;
    options.TransportMaxBufferSize = 256 * 1024;
});

app.Run();
