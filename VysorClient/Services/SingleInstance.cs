using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace VysorClient.Services;

// Garante que existe UM Vysor rodando, e entrega os convites clicados pro que
// já está aberto.
//
// POR QUE ISTO É OBRIGATÓRIO (e não um refinamento)
// Um link "vysor://..." faz o Windows ABRIR O PROGRAMA passando o link como
// argumento. Ele não tem como saber que já existe um Vysor aberto. Sem o que
// está aqui, clicar num convite enquanto você já está numa sala abriria um
// SEGUNDO Vysor: duas janelas iguais, dois processos disputando a mesma
// captura de tela e o mesmo áudio, e a pessoa sem entender qual das duas é a
// "de verdade". O link só é utilizável junto com isto.
//
// COMO FUNCIONA
// Um "mutex" nomeado é uma plaquinha que só um processo consegue segurar no
// Windows inteiro. Quem consegue é o dono e passa a escutar num cano nomeado;
// quem não consegue sabe que chegou depois, joga o link pelo cano e encerra na
// hora, sem nunca desenhar janela nenhuma.
public sealed class SingleInstance : IDisposable
{
    // Os dois nomes precisam ser estáveis entre versões — é assim que um Vysor
    // 1.3 reconhece um 1.3 já aberto. Trocar isto quebra a entrega de convites
    // entre uma versão e outra durante uma atualização.
    private const string MutexName = @"Local\VysorApp.SingleInstance";
    private const string PipeName = "VysorApp.Invites";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _stop;

    public bool IsOwner { get; }

    // Chegou um convite de outra instância (alguém clicou num link com o app
    // já aberto). Vem de uma thread de fundo: quem se inscreve precisa
    // marshalizar pra interface.
    public event Action<string>? OnInviteReceived;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsOwner = createdNew;
    }

    // Manda o convite pro Vysor que já está aberto. Só faz sentido quando
    // IsOwner é false. Devolve false se não conseguiu falar com ele — nesse
    // caso quem chama deve seguir abrindo normalmente, porque um convite que
    // não chega em lugar nenhum é pior que uma segunda janela.
    public static bool SendInvite(string invite, int timeoutMs = 2000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);

            byte[] payload = Encoding.UTF8.GetBytes(invite);
            client.Write(payload, 0, payload.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Começa a escutar convites. Só o dono chama isto.
    public void StartListening()
    {
        if (!IsOwner) return;

        _stop = new CancellationTokenSource();
        var token = _stop.Token;

        var thread = new Thread(() => ListenLoop(token))
        {
            IsBackground = true,
            Name = "VysorInviteListener"
        };
        thread.Start();
    }

    private void ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Um servidor novo por convite recebido. É mais simples e mais
                // robusto que reaproveitar: se um cliente morrer no meio do
                // envio, o cano fica num estado esquisito, e recriar limpa
                // isso sozinho.
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                if (token.IsCancellationRequested) return;

                using var reader = new StreamReader(server, Encoding.UTF8);
                string invite = reader.ReadToEnd().Trim();

                if (invite.Length is > 0 and <= 200)
                {
                    try { OnInviteReceived?.Invoke(invite); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Um erro aqui nunca pode derrubar o app: no pior caso, este
                // convite se perde e a pessoa cola o código na mão.
                // Sem a pausa, um erro persistente viraria laço quente.
                try { Thread.Sleep(200); } catch { }
            }
        }
    }

    public void Dispose()
    {
        try { _stop?.Cancel(); } catch { }

        try
        {
            if (IsOwner) _mutex.ReleaseMutex();
        }
        catch { }

        try { _mutex.Dispose(); } catch { }
    }
}
