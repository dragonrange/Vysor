using System.Windows;
using VysorClient.Services;

namespace VysorClient;

public partial class App : System.Windows.Application
{
    // A instância única e o ouvinte de convites vivem aqui porque precisam
    // existir ANTES da janela — a decisão de "esta cópia do Vysor deve mesmo
    // abrir?" é tomada antes de desenhar qualquer coisa.
    private SingleInstance? _instance;

    // Convite que veio na linha de comando (alguém clicou num link com o app
    // fechado). A janela consome isto quando terminar de carregar; guardar
    // aqui evita o problema clássico de o evento chegar antes de existir quem
    // escute.
    public static string? PendingInvite { get; private set; }

    // Convite que chegou com o app JÁ aberto, entregue pela outra instância.
    public static event Action<string>? OnInviteReceived;

    protected override void OnStartup(StartupEventArgs e)
    {
        string? invite = VysorLink.FromCommandLine(e.Args);

        _instance = new SingleInstance();

        if (!_instance.IsOwner)
        {
            // Já existe um Vysor aberto. Se viemos por causa de um convite,
            // entrega pra ele e encerra sem nunca desenhar janela.
            //
            // Se a entrega FALHAR, seguimos abrindo normalmente: um convite
            // que não chegou a lugar nenhum é pior do que uma segunda janela —
            // pelo menos assim a pessoa vê o app e consegue colar o código.
            if (invite != null && SingleInstance.SendInvite(invite))
            {
                _instance.Dispose();
                Shutdown();
                return;
            }

            if (invite == null)
            {
                // Abriram o Vysor duas vezes sem convite nenhum: traz a
                // janela existente pra frente em vez de criar outra.
                SingleInstance.SendInvite(string.Empty);
                _instance.Dispose();
                Shutdown();
                return;
            }
        }

        PendingInvite = invite;

        _instance.OnInviteReceived += received =>
        {
            // Vem de uma thread de fundo (ver SingleInstance): tudo daqui pra
            // frente mexe com janela, então precisa voltar pra thread da
            // interface.
            Dispatcher.InvokeAsync(() =>
            {
                BringToFront();
                if (received.Length > 0) OnInviteReceived?.Invoke(received);
            });
        };
        _instance.StartListening();

        base.OnStartup(e);
    }

    // Traz a janela pro primeiro plano. Necessário porque quem clicou no link
    // clicou no Discord (ou no navegador) — o Vysor está atrás de tudo, e uma
    // sala que "entrou" numa janela invisível não serve pra nada.
    private void BringToFront()
    {
        var window = MainWindow;
        if (window == null) return;

        try
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Activate();

            // O Windows impede que um programa em segundo plano roube o foco.
            // Marcar Topmost e desmarcar em seguida é o jeito consagrado de
            // pedir atenção sem deixar a janela presa na frente de tudo.
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }
        catch
        {
            // Trazer pra frente é cortesia; falhar nisso não pode quebrar a
            // entrada na sala, que é o que realmente importa.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
