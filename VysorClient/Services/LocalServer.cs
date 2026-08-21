using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VysorServer;
using VysorServer.Hubs;

namespace VysorClient.Services;

// O Vysor hospedando a sala no próprio PC.
//
// POR QUE ISSO EXISTE
// Até aqui a sala morava num servidor alugado na internet. Ele fazia duas
// coisas muito diferentes: organizar quem está na sala (custo quase zero) e
// repassar o vídeo de todo mundo (custo altíssimo). Era a segunda que
// estourava o limite do plano grátis, todo mês.
//
// A saída é simples: o computador de quem está na sala faz esse trabalho.
// É EXATAMENTE o mesmo código do servidor — RoomHub, RoomManager e
// RoomSweeper são os mesmos arquivos, compartilhados com o projeto
// VysorServer, não uma cópia. Tudo que já foi acertado lá (tolerância a
// queda de conexão, limite de fila, reentrada automática na sala) continua
// valendo aqui, sem nada reescrito.
//
// FICA SEMPRE LIGADO
// Não existe botão de "hospedar". Todo Vysor abre esta porta assim que
// inicia, em silêncio. Assim ninguém precisa combinar quem é o host, e —
// mais importante — quando o host atual fecha o app, o próximo da fila
// assume em segundos, porque ele já estava pronto. Se tivesse que ligar o
// servidor na hora, a sala cairia junto com o host.
//
// SEGURANÇA
// Este servidor só sabe repassar bytes entre quem está na mesma sala. Ele
// não lê arquivo, não executa nada, não abre nada do seu PC. E quem não
// alcança a sua máquina pela rede não consegue nem chegar nele.
public static class LocalServer
{
    // Porta fixa e documentada: é ela que aparece no guia de conexão, e é
    // ela que você libera no roteador se for por esse caminho. Escolhida
    // numa faixa alta pra não brigar com nada comum.
    public const int DefaultPort = 5799;

    private static WebApplication? _app;
    private static readonly object _lock = new();

    public static bool IsRunning { get; private set; }
    public static int Port { get; private set; } = DefaultPort;

    // Por que não subiu, quando não sobe. Serve pra tela poder explicar em
    // vez de só ficar sem hospedar sem dizer nada.
    public static string? LastError { get; private set; }

    // Endereço que ESTE app usa pra falar com o servidor daqui mesmo.
    // Sempre 127.0.0.1: quem hospeda não precisa dar a volta pela rede.
    public static string LoopbackUrl => $"http://127.0.0.1:{Port}/roomhub";

    public static async Task<bool> StartAsync(int port = DefaultPort)
    {
        lock (_lock)
        {
            if (IsRunning) return true;
        }

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = "Production"
            });

            // Um app de janela não tem console pra onde mandar log, e deixar
            // os provedores padrão ligados só gasta tempo e memória à toa.
            builder.Logging.ClearProviders();

            builder.WebHost.UseKestrel(options =>
            {
                // Escuta em TODAS as interfaces de rede de propósito: assim o
                // mesmo servidor atende quem chega pela rede local, pelo
                // Tailscale ou por uma porta liberada no roteador, sem
                // precisar escolher antes qual dos caminhos você vai usar.
                options.ListenAnyIP(port);
                options.Limits.MaxRequestBodySize = null;
            });

            ConfigureServices(builder.Services);

            var app = builder.Build();
            ConfigurePipeline(app);

            await app.StartAsync();

            lock (_lock)
            {
                _app = app;
                Port = port;
                IsRunning = true;
                LastError = null;
            }
            return true;
        }
        catch (Exception ex)
        {
            // Causa mais comum de longe: já tem um Vysor aberto neste PC
            // usando a porta. Não é um problema — o segundo app simplesmente
            // não hospeda, e continua funcionando normalmente como
            // participante.
            LastError = ex.Message;
            IsRunning = false;
            return false;
        }
    }

    public static async Task StopAsync()
    {
        WebApplication? app;
        lock (_lock)
        {
            app = _app;
            _app = null;
            IsRunning = false;
        }

        if (app == null) return;

        try
        {
            // Prazo curto: fechar o app não pode ficar preso esperando
            // conexão de ninguém.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(cts.Token);
            await app.DisposeAsync();
        }
        catch
        {
            // Já estava caindo de qualquer jeito.
        }
    }

    // As configurações abaixo são as MESMAS do VysorServer/Program.cs, e
    // precisam continuar assim: elas são o resultado de bugs reais que já
    // aconteceram em uso (transmissões travando e a sala inteira caindo
    // junto). Se um dia mudar de um lado, mude do outro.
    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSignalR(options =>
        {
            // Um quadro-chave de 1080p passa longe disso, mas deixamos folga.
            options.MaximumReceiveMessageSize = 10 * 1024 * 1024;

            // Prazos folgados: sob carga de vídeo, um pico de tráfego atrasava
            // o "ping" de manutenção o bastante pra derrubar clientes que
            // estavam perfeitamente saudáveis.
            options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);

            options.MaximumParallelInvocationsPerClient = 4;
        });

        services.AddSingleton<RoomManager>();
        services.AddHostedService<RoomSweeper>();
    }

    private static void ConfigurePipeline(WebApplication app)
    {
        // Endereço de teste: dá pra abrir no navegador pra confirmar que o
        // PC de quem hospeda está mesmo sendo alcançado. É por aqui que o
        // app dos outros descobre se você está no ar.
        app.MapGet("/", () => "Vysor — hospedando aqui! ✅");
        app.MapGet("/status", () => "Vysor — hospedando aqui! ✅");

        // "Você tem esta sala aberta?"
        //
        // Esta pergunta é o que impede a sala de RACHAR EM DUAS. Quando o host
        // sai, os apps procuram um novo. Se cada um simplesmente subisse a
        // sala em si mesmo, ou se o host antigo voltasse e recriasse a dele,
        // acabariam existindo duas salas com o mesmo código e as pessoas
        // divididas entre elas, sem entender por que o amigo "sumiu".
        //
        // Perguntando antes, quem já tem a sala viva sempre ganha, e quem
        // volta atrasado entra na sala que continuou em vez de abrir outra.
        app.MapGet("/room/{code}", (string code, RoomManager rooms) =>
            rooms.GetRoom((code ?? string.Empty).Trim().ToUpperInvariant()) != null
                ? Results.Ok("existe")
                : Results.NotFound());

        app.MapHub<RoomHub>("/roomhub", options =>
        {
            // O padrão do SignalR pro buffer de saída é 32 KB — pequeno demais
            // pra quadros de vídeo. Com o buffer estourando, cada envio ficava
            // esperando, e essa espera se propagava até derrubar conexões.
            options.ApplicationMaxBufferSize = 10 * 1024 * 1024;
            options.TransportMaxBufferSize = 10 * 1024 * 1024;
        });
    }
}
