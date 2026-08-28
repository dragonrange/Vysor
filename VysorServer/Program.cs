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
