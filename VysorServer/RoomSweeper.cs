using Microsoft.AspNetCore.SignalR;
// Este "using" parece supérfluo (o projeto do servidor já o inclui sozinho),
// mas não é: este mesmo arquivo é compartilhado com o app do Vysor, que agora
// hospeda a sala no próprio PC e NÃO tem esses atalhos automáticos. Sem esta
// linha, o app não compila.
using Microsoft.Extensions.Hosting;
using VysorServer.Hubs;

namespace VysorServer;

// Faxina periódica das salas.
//
// Duas tarefas, as duas de uma vez a cada poucos segundos:
//
//  1. Remove de vez quem caiu e não voltou dentro do prazo de tolerância, e
//     só ENTÃO avisa os outros que a pessoa saiu. É esse atraso proposital
//     que faz uma reconexão rápida passar despercebida: quem está assistindo
//     não perde a telinha e não precisa clicar em assistir de novo.
//
//  2. Apaga salas que ficaram vazias por tempo demais, liberando a memória.
//
// Precisa ser um serviço à parte (e não um timer dentro do RoomManager)
// porque avisar os participantes exige o IHubContext, que só o sistema de
// injeção de dependências do ASP.NET sabe fornecer.
public class RoomSweeper : BackgroundService
{
    private readonly RoomManager _roomManager;
    private readonly IHubContext<RoomHub> _hubContext;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public RoomSweeper(RoomManager roomManager, IHubContext<RoomHub> hubContext)
    {
        _roomManager = roomManager;
        _hubContext = hubContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);

                foreach (var (roomCode, userId) in _roomManager.SweepExpired())
                {
                    try
                    {
                        await _hubContext.Clients.Group(roomCode).SendAsync("UserLeft", userId, stoppingToken);
                    }
                    catch
                    {
                        // Sala já sumiu ou ninguém pra avisar: tudo bem.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break; // servidor desligando
            }
            catch
            {
                // Nenhum erro de faxina pode derrubar o servidor.
            }
        }
    }
}
