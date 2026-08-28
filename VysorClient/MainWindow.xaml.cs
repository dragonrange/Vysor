using VysorClient.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;
using Screen = System.Windows.Forms.Screen;

namespace VysorClient;

public partial class MainWindow : Window
{
    private readonly SignalRService _signalR = new();
    private readonly AudioCaptureService _audioCapture = new();
    private readonly AudioPlaybackService _audioPlayback = new();
    private readonly ObservableCollection<ParticipantViewModel> _participants = new();
    private readonly ObservableCollection<StreamTileViewModel> _watchedStreams = new();
    private readonly ObservableCollection<ShareSourceItem> _displaySources = new();
    private readonly ObservableCollection<ShareSourceItem> _windowSources = new();
    private readonly List<string> _recentRooms = new();

    private bool _isStreaming = false;
    private int _selectedTabIndex = 0;

    // Identidade estável deste app dentro da sala (ver SignalRService.UserId)
    // — é essa string, e não o nome de exibição, que identifica "quem sou eu"
    // em toda a sala. Duas pessoas com o mesmo nome (ex: o padrão "Usuário")
    // não se confundem, e ela NÃO muda quando a conexão cai e volta, então
    // uma reconexão não faz você virar uma "pessoa nova" para quem está
    // assistindo.
    private string? _myUserId;

    // Tile atualmente fixado (pin), ou null se nenhum estiver fixado.
    private StreamTileViewModel? _pinnedTile;

    // Codificação por hardware (GPU) da própria transmissão, quando
    // disponível — null enquanto não está transmitindo ou quando caiu pro
    // pipeline JPEG/GDI de sempre (ver VIDEO_GPU_NOTES.md).
    private VideoEncodeService? _videoEncode;

    // Um decodificador H.264 por tile assistido (inclusive a própria
    // prévia), criado sob demanda só quando o remetente daquele tile está
    // de fato mandando vídeo por hardware — se o remetente estiver usando o
    // pipeline JPEG de sempre, o tile correspondente nunca ganha uma
    // entrada aqui.
    private readonly Dictionary<StreamTileViewModel, VideoDecodeService> _tileDecoders = new();

    // Espelho de "quem eu estou assistindo" que pode ser lido com segurança
    // de outras threads (o áudio chega por uma thread do SignalR). O
    // _watchedStreams é da interface e não pode ser percorrido de fora dela.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _watchedUserIds = new();

    // Identifica cada "sessão de transmissão". Toda vez que a transmissão
    // começa, esse número aumenta; os laços de captura guardam o número que
    // viram ao iniciar e param sozinhos assim que ele muda. Antes disso,
    // parar e começar de novo rapidinho deixava o laço antigo rodando junto
    // com o novo (dobrando o uso de CPU e mandando o dobro de quadros).
    private int _streamGeneration;

    // PID da janela sendo compartilhada no momento (só relevante no modo "Janelas"),
    // usado para pedir áudio isolado daquele processo.
    private uint? _sharedWindowProcessId;

    // Qualidade escolhida pelo usuário no modal de compartilhamento (não há mais
    // uma tela de "Configurações" separada para isso).
    private int _targetWidth = 1920;
    private int _targetHeight = 1080;
    private int _targetFps = 30;

    // Representa uma fonte selecionável no modal de compartilhamento (tela ou janela),
    // já com a miniatura carregada de forma assíncrona, no estilo do seletor do Discord.
    public class ShareSourceItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public bool IsWindow { get; set; }
        public Screen? ScreenRef { get; set; }
        public IntPtr HWnd { get; set; }

        private BitmapImage? _thumbnail;
        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnPropertyChanged(); }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private class WindowHandle
    {
        public string Title { get; set; } = string.Empty;
        public IntPtr hWnd { get; set; }
    }

    public MainWindow()
    {
        InitializeComponent();

        DisplayList.ItemsSource = _displaySources;
        WindowList.ItemsSource = _windowSources;
        StreamsGrid.ItemsSource = _watchedStreams;
        _watchedStreams.CollectionChanged += (s, e) => UpdateStreamsLayout();
        UpdateStreamsLayout();

        _audioCapture.OnAudioChunk += AudioCapture_OnAudioChunk;

        // Estado inicial da tela inicial (botões apagados até ter nome) e
        // cursor já piscando no campo do nome, pra pessoa só digitar.
        UpdateLobbyButtons();
        Loaded += (_, _) => TxtDisplayName.Focus();

        InitSignalR();
    }

    // O convite completo da sala em que estamos ("100.94.12.7:5799/AB12CD").
    // É ele que vai pro grupo, e não só o código: com a sala morando no PC de
    // alguém, o código sozinho não diz ONDE ela está.
    private string _currentInvite = "";

    // Cuida de reencontrar a sala quando quem hospeda fecha o app.
    private HostFailover? _failover;

    // Manda o vídeo DIRETO pro PC de cada amigo, em vez de pelo servidor.
    // Quem não fechar caminho direto continua recebendo pelo servidor, sem
    // perceber diferença (ver PeerMedia).
    private PeerMedia? _peerMedia;

    private async void InitSignalR()
    {
        // A sala agora mora no PC de alguém do grupo, e este app fica pronto
        // pra ser esse alguém desde que abre. Ligar o servidor só na hora em
        // que fosse preciso não daria certo: quando o host cai, quem assume
        // precisa JÁ estar de pé — não dá pra pedir pra ele ligar depois,
        // porque nesse momento ninguém mais consegue falar com ninguém.
        HostDirectory.Load();
        await LocalServer.StartAsync();

        // Pede ao roteador pra abrir a porta, em segundo plano e sem
        // incomodar. Se der certo, o convite passa a sair com o endereço de
        // internet — que é o único que funciona pra um amigo de outra casa
        // sem ele instalar nada. Se não der, ninguém vê erro nenhum: o
        // "Testar minha conexão" é que serve pra investigar.
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await PortForwarding.TryOpenAsync(LocalServer.Port);
                if (result.Success && result.ExternalIp != null)
                {
                    string? publicIp = await ConnectivityCheck.GetPublicIpAsync();

                    // Só vale se o endereço do roteador for REALMENTE o
                    // endereço da internet. Quando a operadora usa CGNAT, o
                    // roteador abre uma porta que não leva a lugar nenhum, e
                    // anunciar esse número mandaria os amigos pra um endereço
                    // que nunca vai atender.
                    if (publicIp != null && publicIp == result.ExternalIp)
                    {
                        LocalAddresses.PublicAddress = $"{result.ExternalIp}:{LocalServer.Port}";
                    }
                }
            }
            catch { }
        });

        _failover = new HostFailover(_signalR);
        _peerMedia = new PeerMedia(_signalR);

        // Único caminho de entrada de vídeo/áudio: o direto (P2P). O servidor
        // não tem mais como mandar isto (ver RoomHub.cs).
        _peerMedia.OnVideo += (userId, frameBytes) =>
            Dispatcher.Invoke(() => HandleIncomingFrame(userId, frameBytes));
        _peerMedia.OnAudio += HandleIncomingAudio;

        _signalR.OnPeerCandidates += (userId, candidates) =>
            _peerMedia?.AddPeerCandidates(userId, candidates);

        _failover.OnStatus += (text) => Dispatcher.Invoke(() =>
        {
            TxtStreamNotice.Text = text;
            TxtStreamNotice.Visibility = string.IsNullOrEmpty(text)
                ? Visibility.Collapsed : Visibility.Visible;
        });

        _failover.OnRecovered += () => Dispatcher.Invoke(() =>
        {
            UpdateInvite(TxtActiveCode.Text);
            _ = _signalR.AnnounceAddressAsync(LocalAddresses.Best());
        });

        // A conexão com quem hospedava caiu de vez. Em vez de a sala acabar
        // pra todo mundo, procuramos o próximo da fila de sucessão.
        _signalR.OnConnectionLost += () => Dispatcher.Invoke(() =>
        {
            string code = TxtActiveCode.Text;
            if (string.IsNullOrWhiteSpace(code) || code == "------") return;
            string myName = string.IsNullOrWhiteSpace(TxtDisplayName.Text) ? "Usuário" : TxtDisplayName.Text;
            _failover?.Begin(code, myName);
        });

        _signalR.OnRoomCreated += (code, myId) => Dispatcher.Invoke(() =>
        {
            _myUserId = myId;
            EnterRoom(code);
            UpdateInvite(code);
            _ = _signalR.AnnounceAddressAsync(LocalAddresses.Best());

            // Abre o canal direto com os amigos. A partir daqui o vídeo passa
            // a ir de PC pra PC, e o servidor só apresenta um ao outro.
            _peerMedia?.Start(code);

            _participants.Clear();
            string myName = string.IsNullOrWhiteSpace(TxtDisplayName.Text) ? "Você" : TxtDisplayName.Text;
            _participants.Add(new ParticipantViewModel { UserId = myId, DisplayName = myName });
            ListParticipants.ItemsSource = _participants;
            RefreshLinkStatuses();
        });

        _signalR.OnRoomJoined += (code, myId, ids, names) => Dispatcher.Invoke(() =>
        {
            _myUserId = myId;
            EnterRoom(code);
            UpdateInvite(code);
            _ = _signalR.AnnounceAddressAsync(LocalAddresses.Best());

            _peerMedia?.Start(code);
            _peerMedia?.SetParticipants(ids);

            // Fecha tiles de gente que não está mais na sala (o caso normal é
            // uma reconexão automática: a lista chega novinha do servidor, e
            // sem isso sobrariam telinhas de participantes que já saíram).
            var stillPresent = new HashSet<string>(ids);
            foreach (var orphan in _watchedStreams.Where(t => !t.IsLocal && !stillPresent.Contains(t.UserId)).ToList())
            {
                RemoveWatchTile(orphan);
            }

            _participants.Clear();
            for (int i = 0; i < ids.Count && i < names.Count; i++)
            {
                _participants.Add(new ParticipantViewModel
                {
                    UserId = ids[i],
                    DisplayName = names[i],
                    // Reflete os tiles que continuam abertos, senão o ícone
                    // ao lado do nome voltava pra "▶" mesmo com a pessoa
                    // ainda aparecendo na tela.
                    IsWatching = _watchedStreams.Any(t => !t.IsLocal && t.UserId == ids[i])
                });
            }
            ListParticipants.ItemsSource = _participants;
            RefreshLinkStatuses();

            // Se eu já estava transmitindo quando entrei aqui (caso de uma
            // reconexão automática), a transmissão continua — só preciso
            // reapontar minha identidade, que o servidor acabou de trocar.
            // Sem isso, a prévia local ficava presa no identificador antigo e
            // meu próprio nome na lista aparecia como se eu não estivesse
            // transmitindo.
            if (_isStreaming)
            {
                var localTile = _watchedStreams.FirstOrDefault(t => t.IsLocal);
                if (localTile != null) localTile.UserId = myId;

                var me = _participants.FirstOrDefault(p => p.UserId == myId);
                if (me != null) me.IsSharing = true;
            }
        });

        _signalR.OnUserJoined += (id, displayName) => Dispatcher.Invoke(() =>
        {
            _peerMedia?.AddParticipant(id);
            // Ignora se essa pessoa já está na lista. Quando duas pessoas
            // entram quase ao mesmo tempo, dá pra receber o aviso de uma
            // delas E também vê-la na lista completa que o servidor manda —
            // sem esta checagem, ela aparecia duas vezes, e a linha
            // duplicada nunca mais saía da tela.
            if (_participants.Any(p => p.UserId == id)) return;

            _participants.Add(new ParticipantViewModel { UserId = id, DisplayName = displayName });
        });

        _signalR.OnUserLeft += (id) => Dispatcher.Invoke(() =>
        {
            _peerMedia?.RemoveParticipant(id);
            // Remove TODAS as linhas dessa pessoa, não só a primeira — assim,
            // se por algum motivo uma duplicata tiver escapado, ela não fica
            // encalhada na lista pra sempre.
            foreach (var user in _participants.Where(p => p.UserId == id).ToList())
            {
                _participants.Remove(user);
            }

            // Se a pessoa que saiu estava sendo assistida, remove o tile dela
            // também. O "!t.IsLocal" é importante: a sua própria prévia usa a
            // sua identidade, e sem esse cuidado um aviso de saída referente a
            // você mesmo derrubaria a sua prévia no meio da transmissão.
            var tile = _watchedStreams.FirstOrDefault(t => !t.IsLocal && t.UserId == id);
            if (tile != null) RemoveWatchTile(tile);
        });

        _signalR.OnUserStoppedSharing += (id) => Dispatcher.Invoke(() =>
        {
            var participant = _participants.FirstOrDefault(p => p.UserId == id);
            if (participant != null) participant.IsSharing = false;

            // Some com o tile em vez de deixar o último quadro congelado sem
            // nenhum aviso — se a pessoa voltar a transmitir, o play acende
            // de novo e dá pra assistir num tile novo. (O "!t.IsLocal"
            // protege a sua própria prévia, pelo mesmo motivo do OnUserLeft.)
            var tile = _watchedStreams.FirstOrDefault(t => !t.IsLocal && t.UserId == id);
            if (tile != null) RemoveWatchTile(tile);
        });

        // Cada frame chega com 1 byte de marcador na frente: 0x00 = JPEG
        // (pipeline de sempre), 0x01 = access unit H.264 (pipeline por
        // hardware). Isso deixa cada participante livre pra usar o
        // pipeline que funcionar no PC dele.
        //
        // Só existe UM caminho de entrada agora: _peerMedia.OnVideo/OnAudio,
        // registrado lá em cima. O servidor não tem mais como mandar vídeo ou
        // áudio (ver RoomHub.cs), então não existe handler equivalente pra
        // "veio pelo servidor" — se P2P não conectar com alguém, o quadro
        // dessa pessoa nunca chega, e é isso que o indicador de conexão da
        // lista de participantes (abaixo) existe pra explicar.
        _peerMedia.OnPathsChanged += () => Dispatcher.Invoke(RefreshLinkStatuses);
        _peerMedia.OnSameNetworkStuck += (userId) => Dispatcher.Invoke(() =>
        {
            var p = _participants.FirstOrDefault(x => x.UserId == userId);
            if (p != null) p.SameNetworkStuck = true;
        });

        ContinuarInit();
    }

    // Atualiza o indicador "conectando… / direto" de cada participante na
    // lista. Chamado sempre que algum caminho direto muda de estado (ver
    // _peerMedia.OnPathsChanged) e ao popular a lista pela primeira vez.
    private void RefreshLinkStatuses()
    {
        if (_peerMedia == null) return;
        foreach (var p in _participants)
        {
            p.IsDirect = _peerMedia.IsDirect(p.UserId);
        }
    }

    // Um quadro chegou — não importa se veio direto do PC do amigo ou pelo
    // servidor. Este é o único lugar que trata vídeo recebido.
    private void HandleIncomingFrame(string userId, byte[] frameBytes)
    {
        {
            var participant = _participants.FirstOrDefault(p => p.UserId == userId);
            if (participant != null && !participant.IsSharing)
            {
                participant.IsSharing = true;
            }

            var tile = _watchedStreams.FirstOrDefault(t => t.UserId == userId);
            if (tile == null || frameBytes.Length == 0) return;

            byte tag = frameBytes[0];

            if (tag == 0x01)
            {
                // Access unit H.264 — repassa pro decodificador desse tile,
                // criando um sob demanda na primeira vez. A criação roda
                // fora da thread da UI porque subir o ffmpeg pode levar
                // alguns instantes (e não podemos travar a janela por isso).
                if (!_tileDecoders.TryGetValue(tile, out var decoder))
                {
                    decoder = CreateDecoderForTile(tile);
                }

                // Feed() não bloqueia (enfileira internamente), então é
                // seguro chamar daqui mesmo estando na thread da interface.
                byte[] au = new byte[frameBytes.Length - 1];
                Buffer.BlockCopy(frameBytes, 1, au, 0, au.Length);
                decoder.Feed(au);
            }
            else
            {
                byte[] jpegBytes = new byte[frameBytes.Length - 1];
                Buffer.BlockCopy(frameBytes, 1, jpegBytes, 0, jpegBytes.Length);
                tile.Image = BytesToBitmapImage(jpegBytes);
            }
        }
    }

    // Só entregamos o áudio pro motor de reprodução se existir um tile aberto
    // pra essa pessoa — senão estaríamos tocando áudio de gente que o usuário
    // nem está assistindo.
    //
    // Consulta o _watchedUserIds (dicionário concorrente) em vez de varrer o
    // _watchedStreams: este callback vem de uma thread de rede, e percorrer
    // uma ObservableCollection que a interface está modificando ao mesmo
    // tempo lançava exceção justamente na hora de abrir/fechar um tile.
    private void HandleIncomingAudio(string userId, byte[] audioBytes)
    {
        if (_watchedUserIds.ContainsKey(userId))
        {
            _audioPlayback.Feed(userId, audioBytes);
        }
    }

    // Segunda metade da preparação. Só está separada porque os tratadores de
    // vídeo e áudio viraram métodos próprios no meio do caminho.
    private void ContinuarInit()
    {
        // Sem isso, digitar um código de sala errado (ou em minúsculas) não
        // dava reação nenhuma na tela — o servidor respondia "Sala não
        // encontrada" e o cliente ignorava a mensagem por completo.
        _signalR.OnServerError += (message) => Dispatcher.Invoke(() =>
        {
            System.Windows.MessageBox.Show(message, "Vysor", MessageBoxButton.OK, MessageBoxImage.Warning);
        });

        // Quando a conexão cai e volta sozinha, ela é uma conexão NOVA para o
        // servidor. Ele segura o seu lugar na sala por alguns segundos, mas é
        // esta reentrada que liga a conexão nova à sua identidade de sempre.
        // Sem ela, o app continuaria mostrando a sala normalmente sem mandar
        // nem receber mais nada — parecia estar funcionando e não estava.
        _signalR.OnReconnected += () => Dispatcher.Invoke(() =>
        {
            string code = TxtActiveCode.Text;
            if (string.IsNullOrWhiteSpace(code) || code == "------") return;

            string myName = string.IsNullOrWhiteSpace(TxtDisplayName.Text) ? "Usuário" : TxtDisplayName.Text;

            // RejoinRoom (e não JoinRoom): se a sala tiver sumido enquanto a
            // conexão estava caída, o servidor a recria com o mesmo código.
            // Com JoinRoom, uma queda que atingisse todo mundo ao mesmo tempo
            // matava a sala de vez e todos ficavam presos no "Sala não
            // encontrada".
            // A transmissão em andamento NÃO é interrompida aqui. Numa versão
            // anterior eu parava a transmissão nesta hora (o servidor passa a
            // te conhecer por um identificador novo depois de reconectar), mas
            // isso ficava idêntico a você mesmo ter clicado em "parar": do
            // nada, no meio do uso, a transmissão morria sozinha. Como
            // reconexões automáticas acontecem de vez em quando, era só uma
            // questão de tempo. Agora ela continua, e o reajuste do novo
            // identificador é feito quando o servidor confirma a reentrada
            // (ver OnRoomJoined).
            _ = _signalR.RejoinRoomAsync(code, myName);
        });

        // De propósito NÃO conectamos aqui. Antes fazia sentido: existia um
        // servidor único e deixar a conexão pronta adiantava. Agora "o
        // servidor" depende da sala — criar liga no PC daqui, entrar liga no
        // PC de quem convidou. Conectar antes de saber qual dos dois é o caso
        // deixaria o app preso no lugar errado, e o botão "Entrar" ia parecer
        // não funcionar.
    }

    // Monta o convite (endereço + código) e mostra na tela.
    //
    // Sem o endereço, o código sozinho não leva a lugar nenhum: ele só faz
    // sentido no computador que está hospedando aquela sala.
    private void UpdateInvite(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code == "------")
        {
            _currentInvite = "";
            return;
        }

        // Se você está usando um servidor fixo (server.txt), o convite volta a
        // ser só o código, como era antes.
        if (SignalRService.GetFixedServerUrl() != null)
        {
            _currentInvite = code;
            TxtStreamNotice.Visibility = Visibility.Collapsed;
            return;
        }

        // Quem hospeda é quem passa o próprio endereço. Quem só entrou passa
        // adiante o endereço de quem está hospedando agora — assim o convite
        // continua valendo mesmo depois de a sala trocar de dono.
        string? address = _signalR.IsHostingHere
            ? LocalAddresses.Best()
            : AddressFromHubUrl(_signalR.CurrentUrl);

        if (string.IsNullOrWhiteSpace(address))
        {
            _currentInvite = code;
            TxtStreamNotice.Text = "Este computador não tem endereço de rede utilizável, "
                                 + "então seus amigos não conseguem chegar aqui. "
                                 + "Veja o guia COMO_CONECTAR.md.";
            TxtStreamNotice.Visibility = Visibility.Visible;
            return;
        }

        _currentInvite = HostDirectory.BuildInvite(address!, code);
        TxtActiveCode.ToolTip = _currentInvite;

        // O aviso abaixo é o conserto do erro que fez o primeiro teste com um
        // amigo falhar: o app entregava um endereço de rede local sem dizer
        // que ele só vale dentro de casa, e a pessoa mandava pro amigo de
        // outra cidade achando que ia funcionar.
        if (_signalR.IsHostingHere && LocalAddresses.OnlyWorksOnSameWifi())
        {
            TxtStreamNotice.Text =
                $"⚠ Convite: {_currentInvite} — ATENÇÃO: este endereço só funciona pra quem "
                + "estiver no MESMO Wi-Fi que você. Pra amigos de outra casa, clique em "
                + "\"Testar minha conexão\" na tela inicial.";
        }
        else
        {
            TxtStreamNotice.Text = $"Convite: {_currentInvite}   (o botão Copiar já copia isto)";
        }
        TxtStreamNotice.Visibility = Visibility.Visible;
    }

    // "Testar minha conexão": responde de uma vez se os amigos conseguem
    // chegar até aqui, e o que fazer quando não conseguem.
    private async void BtnTestConnection_Click(object sender, RoutedEventArgs e)
    {
        BtnTestConnection.IsEnabled = false;
        TxtConnectionResult.Text = "Testando… (isso leva uns 10 segundos)";

        try
        {
            var report = await ConnectivityCheck.RunAsync(LocalServer.Port);

            if (report.Verdict == ConnectivityCheck.Verdict.ReachableFromInternet &&
                report.SuggestedAddress != null)
            {
                LocalAddresses.PublicAddress = $"{report.SuggestedAddress}:{LocalServer.Port}";
            }

            TxtConnectionResult.Text = ConnectivityCheck.ShortSummary(report);

            // Duas perguntas diferentes, mostradas juntas de propósito:
            //   1. "eu consigo hospedar a sala?" — vale pra versão de hoje;
            //   2. "eu consigo falar direto com meus amigos?" — é o que decide
            //      se o app pode deixar de precisar de host, e é a resposta
            //      que estamos coletando do grupo inteiro agora.
            string texto = report.Explanation + "\n\n" + report.WhatToDo;

            if (report.Nat != null)
            {
                texto += "\n\n"
                       + "───────────────\n"
                       + "CONEXÃO DIRETA COM SEUS AMIGOS\n"
                       + report.Nat.Title + "\n\n"
                       + report.Nat.Explanation;
            }

            texto += "\n\n───────────────\nRESUMO: " + ConnectivityCheck.ShortSummary(report);

            // Vai pra área de transferência já pronto pra colar no grupo —
            // é assim que dá pra juntar o resultado de várias pessoas sem
            // ninguém precisar digitar nada.
            try { Clipboard.SetText(ConnectivityCheck.ShortSummary(report)); } catch { }

            System.Windows.MessageBox.Show(
                texto + "\n\n(o resumo já foi copiado — é só colar no grupo)",
                report.Title,
                MessageBoxButton.OK,
                report.Verdict == ConnectivityCheck.Verdict.ReachableFromInternet
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            TxtConnectionResult.Text = "Não consegui completar o teste: " + ex.Message;
        }
        finally
        {
            BtnTestConnection.IsEnabled = true;
        }
    }

    private static string? AddressFromHubUrl(string? hubUrl)
    {
        if (string.IsNullOrWhiteSpace(hubUrl)) return null;
        try
        {
            var uri = new Uri(hubUrl);
            // 127.0.0.1 só serve pra este PC: não adianta mandar pros amigos.
            if (uri.IsLoopback) return LocalAddresses.Best();
            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }
        catch
        {
            return null;
        }
    }

    private void AudioCapture_OnAudioChunk(byte[] audioBytes)
    {
        _peerMedia?.SendAudio(audioBytes);
    }

    // --- Tiles de transmissão ------------------------------------------------------

    private void BtnViewParticipantStream_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not ParticipantViewModel participant)
            return;

        // A sua própria linha não tem "assistir": a sua prévia já aparece
        // sozinha enquanto você transmite, e ela é controlada pelo botão
        // TRANSMITIR. Sem esta guarda, clicar no play da própria linha
        // fechava a prévia (com a transmissão continuando no ar) e, no
        // clique seguinte, criava um tile fantasma que nunca recebia imagem
        // nenhuma — porque o servidor, corretamente, não devolve pra você
        // aquilo que você mesmo está mandando.
        if (!string.IsNullOrEmpty(_myUserId) && participant.UserId == _myUserId) return;

        var existing = _watchedStreams.FirstOrDefault(t => t.UserId == participant.UserId);
        if (existing != null)
        {
            RemoveWatchTile(existing);
            return;
        }

        var tile = new StreamTileViewModel { UserId = participant.UserId, DisplayName = participant.DisplayName };
        AddWatchTile(tile);
        participant.IsWatching = true;
    }

    // Devolve o tile que REALMENTE ficou na lista: se já existia um pra essa
    // mesma pessoa, devolve o antigo em vez do novo. Quem chama precisa usar
    // o retorno, senão acabaria trabalhando com um objeto que não está na
    // tela (foi assim que um decodificador chegou a ser registrado num tile
    // descartado, deixando a prévia parada em "Aguardando o primeiro
    // quadro..." e um ffmpeg rodando sem dono).
    private StreamTileViewModel AddWatchTile(StreamTileViewModel tile)
    {
        // Um tile por pessoa. Sem essa trava dava pra acabar com dois tiles
        // da mesma pessoa, e o segundo ficava eternamente "Aguardando o
        // primeiro quadro...".
        var existing = _watchedStreams.FirstOrDefault(t => t.UserId == tile.UserId);
        if (existing != null) return existing;

        // Se algum tile está fixado (pin), o novo entra escondido — senão
        // ele apareceria junto e quebraria o pin, que existe justamente pra
        // deixar UM vídeo ocupando todo o espaço.
        if (_pinnedTile != null) tile.TileVisibility = Visibility.Collapsed;

        tile.PropertyChanged += Tile_PropertyChanged;
        if (!tile.IsLocal) _watchedUserIds[tile.UserId] = 1;
        _watchedStreams.Add(tile);
        return tile;
    }

    // Cria (e registra) o decodificador H.264 de um tile. A subida do
    // processo roda numa thread de fundo porque leva algumas centenas de
    // milissegundos; se o tile for fechado nesse meio-tempo, o Stop() já
    // marcou o pedido de parada e o processo é encerrado assim que sobe.
    private VideoDecodeService CreateDecoderForTile(StreamTileViewModel tile)
    {
        var decoder = new VideoDecodeService();
        _tileDecoders[tile] = decoder;

        // InvokeAsync (não Invoke): esse evento vem da thread que lê a saída
        // do ffmpeg, e ela não pode ficar esperando a interface — se ficar,
        // ela para de drenar a saída, o ffmpeg trava, e o app inteiro
        // congela junto.
        decoder.OnFrameDecoded += bmpBytes => Dispatcher.InvokeAsync(() =>
        {
            if (_watchedStreams.Contains(tile)) tile.Image = BytesToBitmapImage(bmpBytes);
        }, System.Windows.Threading.DispatcherPriority.Background);

        _ = Task.Run(() =>
        {
            // Se não conseguir subir (sem ffmpeg.exe nesse PC, por
            // exemplo), o decoder fica parado e os frames desse tile são
            // descartados em silêncio — o resto da sala continua
            // funcionando normalmente.
            decoder.Start();
        });

        return decoder;
    }

    // Ponto único de remoção de um tile: cuida de desfixar (se estava em pin),
    // desinscrever eventos, tirar do engine de áudio e atualizar o indicador
    // "assistindo" do participante correspondente.
    private void RemoveWatchTile(StreamTileViewModel tile)
    {
        if (_pinnedTile == tile)
        {
            UnpinAll();
        }

        tile.PropertyChanged -= Tile_PropertyChanged;
        _watchedStreams.Remove(tile);

        if (!tile.IsLocal)
        {
            _watchedUserIds.TryRemove(tile.UserId, out _);
            _audioPlayback.RemoveParticipant(tile.UserId);
        }

        // Derruba o decodificador de vídeo por hardware desse tile, se
        // existir (tile que nunca recebeu H.264 nunca ganhou um).
        if (_tileDecoders.TryGetValue(tile, out var decoder))
        {
            decoder.Stop();
            _tileDecoders.Remove(tile);
        }

        var participant = _participants.FirstOrDefault(p => p.UserId == tile.UserId);
        if (participant != null) participant.IsWatching = false;
    }

    // Derruba todos os decodificadores de vídeo por hardware ainda de pé —
    // usado quando vários tiles somem de uma vez (sair da sala, fechar o
    // app) sem passar um por um pelo RemoveWatchTile. Sem isso, os
    // processos do ffmpeg ficariam órfãos rodando em segundo plano mesmo
    // depois do Vysor fechar.
    private void StopAllTileDecoders()
    {
        foreach (var decoder in _tileDecoders.Values)
        {
            decoder.Stop();
        }
        _tileDecoders.Clear();
        _watchedUserIds.Clear();
    }

    private void Tile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not StreamTileViewModel tile || tile.IsLocal) return;

        if (e.PropertyName == nameof(StreamTileViewModel.Volume))
        {
            _audioPlayback.SetVolumePercent(tile.UserId, tile.Volume);
        }
        else if (e.PropertyName == nameof(StreamTileViewModel.IsMuted))
        {
            _audioPlayback.SetMuted(tile.UserId, tile.IsMuted);
        }
    }

    private void BtnCloseTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not StreamTileViewModel tile)
            return;

        if (tile.IsLocal)
        {
            StopStream();
            return;
        }

        RemoveWatchTile(tile);
    }

    private void BtnToggleMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.DataContext is StreamTileViewModel tile)
        {
            tile.IsMuted = !tile.IsMuted;
        }
    }

    // --- Pin: fixa um tile, escondendo os outros temporariamente -------------------

    private void BtnPinTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not StreamTileViewModel tile)
            return;

        if (_pinnedTile == tile)
        {
            UnpinAll();
        }
        else
        {
            _pinnedTile = tile;
            foreach (var t in _watchedStreams)
            {
                bool isThis = ReferenceEquals(t, tile);
                t.IsPinned = isThis;
                t.TileVisibility = isThis ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        UpdateStreamsLayout();
    }

    private void UnpinAll()
    {
        _pinnedTile = null;
        foreach (var t in _watchedStreams)
        {
            t.IsPinned = false;
            t.TileVisibility = Visibility.Visible;
        }
    }

    private void UpdateStreamsLayout()
    {
        EmptyStatePanel.Visibility = _watchedStreams.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var uniformGrid = FindVisualChild<UniformGrid>(StreamsGrid);
        if (uniformGrid == null) return;

        if (_pinnedTile != null)
        {
            uniformGrid.Columns = 1;
            uniformGrid.Rows = 1;
        }
        else
        {
            int count = Math.Max(1, _watchedStreams.Count);
            int cols = (int)Math.Ceiling(Math.Sqrt(count));
            uniformGrid.Columns = cols;
            uniformGrid.Rows = 0;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;

            var descendant = FindVisualChild<T>(child);
            if (descendant != null) return descendant;
        }
        return null;
    }

    // --- Sala / navegação -------------------------------------------------------------

    // Os dois handlers abaixo são "async void" (exigência do WPF pra
    // eventos de clique): uma exceção aqui derruba o app inteiro em vez de
    // virar um erro tratável. Por isso tudo fica dentro de try/catch, e o
    // usuário recebe um aviso em vez de o programa simplesmente fechar.
    private async void BtnCreateRoom_Click(object sender, RoutedEventArgs e)
    {
        // Trava os botões e diz o que está acontecendo.
        //
        // Sem isto, o clique parecia não fazer nada: a conexão pode levar
        // alguns segundos (ainda mais com o servidor acordando de um período
        // sem uso), e nesse tempo a tela ficava idêntica a antes do clique.
        // A reação natural é clicar de novo — e cada clique extra atrapalhava
        // a tentativa que já estava em curso.
        SetLobbyBusy(true, "Criando a sala…");
        try
        {
            await _signalR.CreateRoomAnywhereAsync(TxtDisplayName.Text);
        }
        catch
        {
            ShowConnectionError();
        }
        finally
        {
            SetLobbyBusy(false);
        }
    }

    // Enquanto uma tentativa está em curso, os botões da tela inicial ficam
    // apagados e o texto conta o que está sendo feito.
    private void SetLobbyBusy(bool busy, string? message = null)
    {
        _lobbyBusy = busy;
        TxtNameHint.Text = busy
            ? (message ?? "Um instante…")
            : "Escolha um nome para seus amigos te reconhecerem na sala.";
        TxtNameHint.Visibility = Visibility.Visible;
        UpdateLobbyButtons();
    }

    private bool _lobbyBusy;

    private async void BtnJoinRoom_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtRoomCode.Text)) return;

        SetLobbyBusy(true, "Procurando a sala…");
        try
        {
            // Aceita tanto o convite completo ("100.94.12.7:5799/AB12CD")
            // quanto só o código. A normalização pra MAIÚSCULAS acontece lá
            // dentro: sem ela, digitar em minúsculas dava "sala não
            // encontrada" sem motivo aparente.
            await _signalR.EnterRoomAsync(TxtRoomCode.Text, TxtDisplayName.Text);
        }
        catch
        {
            ShowConnectionError();
        }
        finally
        {
            SetLobbyBusy(false);
        }
    }

    // Avisa quando o áudio não saiu como a pessoa pediu. Só aparece quando algo
    // realmente fugiu do esperado — no caminho normal, ninguém vê nada.
    //
    // Existe porque, antes, uma falha no isolamento de áudio era completamente
    // invisível: você compartilhava uma janela e o áudio do computador inteiro
    // ia junto sem nenhum sinal disso. Falha de privacidade não pode ser
    // silenciosa.
    private void WarnIfAudioIsNotWhatWasAsked(bool sharingWindow)
    {
        string? message = _audioCapture.ActiveMode switch
        {
            AudioCaptureService.AudioMode.None when sharingWindow =>
                "Não foi possível separar o áudio só dessa janela, então a transmissão vai SEM ÁUDIO.\n\n" +
                "Preferi não mandar nada a mandar o áudio do computador inteiro sem você esperar por isso.\n\n" +
                "Se quiser transmitir com som mesmo assim, compartilhe a tela inteira."
                + DiagnosticSuffix(),

            AudioCaptureService.AudioMode.None =>
                "Não foi possível capturar o áudio: a transmissão vai sem som.",

            AudioCaptureService.AudioMode.SystemUnfiltered =>
                "Atenção: não consegui excluir o Discord do áudio.\n\n" +
                "Como você está compartilhando a tela inteira, o áudio do sistema vai junto — " +
                "e isso inclui a conversa do Discord. Se não quiser que isso seja transmitido, " +
                "pare a transmissão e recomece sem o som ligado.",

            _ => null, // ProcessIsolated / SystemWithoutDiscord: tudo como esperado
        };

        if (message == null) return;

        System.Windows.MessageBox.Show(message, "Áudio da transmissão",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // Linha técnica com o motivo exato da falha, no fim do aviso. É feia de
    // propósito: serve pra você conseguir me dizer o que o Windows respondeu,
    // em vez de a gente ficar tentando adivinhar.
    private string DiagnosticSuffix()
    {
        string? detail = _audioCapture.LastFailureDetail;
        if (string.IsNullOrWhiteSpace(detail)) return string.Empty;

        return $"\n\nDetalhe técnico (Windows {Environment.OSVersion.Version}):\n{detail}";
    }

    private void ShowConnectionError()
    {
        System.Windows.MessageBox.Show(
            "Não foi possível falar com o servidor do Vysor.\n\n" +
            "Confira sua internet e tente de novo em alguns segundos " +
            "(se o servidor estava dormindo, ele pode levar um instante pra acordar).",
            "Sem conexão",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void EnterRoom(string code)
    {
        TxtActiveCode.Text = code;
        ViewLobby.Visibility = Visibility.Collapsed;
        ViewRoom.Visibility = Visibility.Visible;

        if (!_recentRooms.Contains(code))
        {
            _recentRooms.Add(code);
            // Guarda só as últimas salas — a lista não tem por que crescer
            // pra sempre dentro de uma sessão longa.
            while (_recentRooms.Count > 10) _recentRooms.RemoveAt(0);
            ListRecentRooms.ItemsSource = null;
            ListRecentRooms.ItemsSource = _recentRooms;
        }
    }

    private async void BtnLeaveRoom_Click(object sender, RoutedEventArgs e)
    {
        // Sair é de propósito: para de procurar a sala. Sem isto, a busca
        // continuaria rodando por baixo e te jogaria de volta pra dentro dela.
        _failover?.Cancel();
        _peerMedia?.Stop();
        _currentInvite = "";

        StopStream();

        foreach (var tile in _watchedStreams.Where(t => !t.IsLocal).ToList())
        {
            _audioPlayback.RemoveParticipant(tile.UserId);
        }

        // O Clear() abaixo não passa pelo RemoveWatchTile (que é quem
        // normalmente derruba o decodificador de cada tile), então cuidamos
        // disso à parte.
        StopAllTileDecoders();

        UnpinAll();
        _watchedStreams.Clear();

        // Limpa também a identidade e a lista de participantes: sem isso,
        // entrar em outra sala em seguida começava com resquícios da sala
        // anterior (nomes antigos na lista e uma identidade de sala que não
        // vale mais).
        string code = TxtActiveCode.Text;
        _participants.Clear();
        _myUserId = null;
        TxtActiveCode.Text = "------";

        // Troca a tela ANTES de esperar o servidor: se a conexão estiver
        // ruim, o usuário sairia "preso" na tela da sala até dar timeout —
        // ou pior, uma exceção num handler async void derrubaria o app.
        ViewRoom.Visibility = Visibility.Collapsed;
        ViewLobby.Visibility = Visibility.Visible;

        try
        {
            await _signalR.LeaveRoomAsync(code);
        }
        catch { }
    }

    // --- Modal de seleção de fonte, com miniaturas reais -------------------------------

    private void BtnShareScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_isStreaming)
        {
            StopStream();
            return;
        }

        ShareModal.Visibility = Visibility.Visible;
        _ = LoadSourcesAsync();
    }

    private async Task LoadSourcesAsync()
    {
        _displaySources.Clear();
        _windowSources.Clear();

        int index = 1;
        foreach (var scr in Screen.AllScreens)
        {
            _displaySources.Add(new ShareSourceItem
            {
                Name = $"Monitor {index++} ({scr.Bounds.Width}x{scr.Bounds.Height}){(scr.Primary ? " • Principal" : "")}",
                ScreenRef = scr,
                IsWindow = false
            });
        }
        if (_displaySources.Count > 0) _displaySources[0].IsSelected = true;

        foreach (var win in GetOpenWindows())
        {
            _windowSources.Add(new ShareSourceItem { Name = win.Title, HWnd = win.hWnd, IsWindow = true });
        }
        if (_windowSources.Count > 0) _windowSources[0].IsSelected = true;

        // Gera as miniaturas em segundo plano pra não travar a janela, e vai
        // preenchendo cada card assim que a imagem dele fica pronta.
        var displaySnapshot = _displaySources.ToList();
        var windowSnapshot = _windowSources.ToList();

        await Task.Run(() =>
        {
            foreach (var item in displaySnapshot)
            {
                try
                {
                    var bounds = item.ScreenRef!.Bounds;
                    using var raw = new Bitmap(bounds.Width, bounds.Height);
                    using (Graphics g = Graphics.FromImage(raw))
                    {
                        g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                    }
                    using Bitmap thumb = ResizeIfNeeded(raw, 320, 180);
                    byte[] bytes = CompressToJpeg(thumb, 80L);
                    var image = BytesToBitmapImage(bytes);
                    Dispatcher.Invoke(() => item.Thumbnail = image);
                }
                catch { }
            }

            foreach (var item in windowSnapshot)
            {
                try
                {
                    using var raw = CaptureWindow(item.HWnd);
                    if (raw == null) continue;
                    using Bitmap thumb = ResizeIfNeeded(raw, 320, 180);
                    byte[] bytes = CompressToJpeg(thumb, 80L);
                    var image = BytesToBitmapImage(bytes);
                    Dispatcher.Invoke(() => item.Thumbnail = image);
                }
                catch { }
            }
        });
    }

    private void SourceCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ShareSourceItem item)
            return;

        var list = item.IsWindow ? _windowSources : _displaySources;
        foreach (var i in list) i.IsSelected = ReferenceEquals(i, item);
    }

    private async void StartStream_Click(object sender, RoutedEventArgs e)
    {
        if (!_signalR.IsConnected)
        {
            try
            {
                await _signalR.ConnectAsync();
            }
            catch
            {
                System.Windows.MessageBox.Show(
                    "Não foi possível conectar ao servidor de transmissão.",
                    "Erro de Conexão",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
        }

        ShareSourceItem? selectedScreen = null;
        ShareSourceItem? selectedWindow = null;

        if (_selectedTabIndex == 0)
        {
            selectedScreen = _displaySources.FirstOrDefault(i => i.IsSelected) ?? _displaySources.FirstOrDefault();
        }
        else
        {
            selectedWindow = _windowSources.FirstOrDefault(i => i.IsSelected) ?? _windowSources.FirstOrDefault();
        }

        if (selectedScreen == null && selectedWindow == null)
        {
            System.Windows.MessageBox.Show(
                "Por favor, selecione um monitor ou janela para transmitir.",
                "Aviso",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // A janela escolhida pode ter sido fechada entre abrir o modal e
        // clicar em "Iniciar Transmissão". Sem esta checagem, a transmissão
        // "começava" (o botão virava PARAR TRANSMISSÃO) e simplesmente nunca
        // aparecia imagem nenhuma, sem nenhum aviso.
        if (selectedWindow != null && !IsWindow(selectedWindow.HWnd))
        {
            System.Windows.MessageBox.Show(
                "Essa janela foi fechada. Escolha outra na lista.",
                "Aviso",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            await LoadSourcesAsync();
            return;
        }

        // Lê a qualidade escolhida no modal antes de fechá-lo.
        if (CmbQuality.SelectedIndex == 0) { _targetWidth = 1280; _targetHeight = 720; }
        else { _targetWidth = 1920; _targetHeight = 1080; }
        _targetFps = CmbFrameRate.SelectedIndex == 1 ? 60 : 30;

        bool wantsAudio = AudioToggle.IsChecked == true;

        ShareModal.Visibility = Visibility.Collapsed;
        _isStreaming = true;

        // Marca esta sessão de transmissão. Se o usuário parar (ou começar
        // outra) enquanto a preparação abaixo ainda está rodando, este
        // número muda e tudo que pertence à sessão antiga se encerra sozinho
        // em vez de continuar rodando em paralelo.
        int myGeneration = System.Threading.Interlocked.Increment(ref _streamGeneration);

        // Zera o estado de captura: cada transmissão redescobre qual jeito de
        // capturar funciona pra janela escolhida, e volta a poder avisar caso
        // a imagem suma.
        _windowCaptureStrategy = WindowCaptureStrategy.Auto;
        _blankPrintWindowFrames = 0;
        _captureInspectCounter = 0;
        TxtStreamNotice.Visibility = Visibility.Collapsed;
        _consecutiveCaptureFailures = 0;
        _captureFailureWarned = false;

        string myName = string.IsNullOrWhiteSpace(TxtDisplayName.Text) ? "Você" : TxtDisplayName.Text;
        // Combina pela identidade estável, não pelo nome — nome duplicado
        // entre participantes era a causa da terceira pessoa não conseguir
        // transmitir: o "myUser" combinava com a pessoa errada.
        var myUser = _participants.FirstOrDefault(p => p.UserId == _myUserId);
        if (myUser != null)
        {
            myUser.IsSharing = true;
        }

        BtnShareScreen.Content = "🛑 PARAR TRANSMISSÃO";

        // Mostra sua própria prévia como um dos tiles, igual o Discord faz enquanto você compartilha.
        var localTile = AddWatchTile(new StreamTileViewModel
        {
            UserId = _myUserId ?? myName,
            DisplayName = $"{myName} (Você)",
            IsLocal = true
        });

        if (wantsAudio)
        {
            if (selectedWindow != null)
            {
                _sharedWindowProcessId = GetWindowThreadProcessId(selectedWindow.HWnd, out uint pid) != 0 ? pid : (uint?)null;
                _audioCapture.Start(_sharedWindowProcessId);
            }
            else
            {
                _sharedWindowProcessId = null;
                _audioCapture.Start(null); // tela inteira: sistema todo, excluindo o Discord quando possível
            }

            WarnIfAudioIsNotWhatWasAsked(selectedWindow != null);
        }

        // Tenta codificar em H.264 usando a GPU (ver VIDEO_GPU_NOTES.md);
        // se não der (sem ffmpeg.exe empacotado, sem encoder de hardware
        // disponível, etc.), cai pro pipeline JPEG/GDI de sempre sem o
        // usuário precisar fazer nada. A parte que sobe os processos roda
        // em outra thread (Task.Run) porque tentar até 3 encoders em
        // sequência pode levar até uns 2 segundos — sem isso a janela
        // travaria durante essa checagem. Só a parte que mexe em campos
        // compartilhados (_videoEncode, _tileDecoders) roda de volta na
        // thread da UI, depois do await, pra evitar acesso concorrente.
        int monitorIndex = selectedScreen != null ? _displaySources.IndexOf(selectedScreen) : -1;
        var attempt = await Task.Run(() => TryStartHardwareEncodeCore(
            selectedScreen, selectedWindow, monitorIndex, _targetFps, _targetWidth, _targetHeight));

        // O usuário pode ter parado (ou reiniciado) a transmissão enquanto o
        // ffmpeg subia — o que leva até uns 2 segundos. Sem esta checagem, os
        // processos recém-criados eram guardados em campos que o Stop() já
        // tinha limpado, ficando rodando pra sempre sem ninguém pra
        // encerrá-los.
        if (myGeneration != Volatile.Read(ref _streamGeneration))
        {
            attempt.Encoder?.Stop();
            attempt.LocalDecoder?.Stop();
            return;
        }

        bool hardwareStarted = false;
        if (attempt.Started && attempt.Encoder != null)
        {
            _videoEncode = attempt.Encoder;

            var encoder = attempt.Encoder;
            var localDecoder = attempt.LocalDecoder;
            bool localDecoderOk = attempt.LocalDecoderOk;

            if (localDecoderOk && localDecoder != null)
            {
                // InvokeAsync (não Invoke): esse evento vem da thread que lê
                // a saída do ffmpeg e ela não pode ficar esperando a
                // interface — ver comentário em CreateDecoderForTile.
                localDecoder.OnFrameDecoded += bmpBytes => Dispatcher.InvokeAsync(() =>
                {
                    if (_watchedStreams.Contains(localTile)) localTile.Image = BytesToBitmapImage(bmpBytes);
                }, System.Windows.Threading.DispatcherPriority.Background);
                _tileDecoders[localTile] = localDecoder;
            }

            encoder.OnEncodedFrame += auBytes =>
            {
                if (_videoEncode != encoder) return;

                // Manda pra rede ANTES de alimentar a prévia local: quem
                // está do outro lado é mais importante que a sua própria
                // miniatura. As duas chamadas são não-bloqueantes.
                if (_signalR.IsConnected)
                {
                    byte[] framed = new byte[auBytes.Length + 1];
                    framed[0] = 0x01;
                    Buffer.BlockCopy(auBytes, 0, framed, 1, auBytes.Length);
                    _peerMedia?.SendVideo(framed);
                }

                if (localDecoderOk) localDecoder!.Feed(auBytes);
            };

            // Nível 1 (ddagrab) já captura a tela sozinho dentro do próprio
            // ffmpeg — só o Nível 2 precisa que a gente continue capturando
            // frame a frame (reaproveitando a captura GDI de sempre) e
            // alimentando via Feed().
            if (!encoder.IsSelfDriving)
            {
                // Se o decodificador da prévia não subiu, a própria captura
                // desenha a prévia (senão a sua telinha ficaria parada em
                // "Aguardando o primeiro quadro..." pra sempre, mesmo com a
                // transmissão indo normalmente pros outros).
                var previewTile = localDecoderOk ? null : localTile;

                _ = Task.Run(() => HardwareRawCaptureLoop(
                    selectedScreen, selectedWindow, attempt.FixedWidth, attempt.FixedHeight,
                    encoder, myGeneration, previewTile));
            }

            hardwareStarted = true;
        }

        if (!hardwareStarted)
        {
            _ = Task.Run(() => CaptureLoop(selectedScreen, selectedWindow, myGeneration));
        }
    }

    // Resultado de uma tentativa de subir o pipeline de vídeo por hardware —
    // só dados simples, sem tocar em nenhum campo compartilhado da janela,
    // porque é montado inteiramente numa thread de fundo (ver comentário em
    // StartStream_Click).
    private class HardwareEncodeAttempt
    {
        public bool Started;
        public VideoEncodeService? Encoder;
        public VideoDecodeService? LocalDecoder;
        public bool LocalDecoderOk;
        public int FixedWidth;
        public int FixedHeight;
    }

    // Orquestra os níveis 1 e 2 do pipeline de vídeo por hardware (ver
    // VIDEO_GPU_NOTES.md). Método estático de propósito: só sobe processos
    // e devolve o resultado, sem escrever em nenhum campo da instância —
    // quem chama (StartStream_Click) decide o que fazer com o resultado de
    // volta na thread da UI.
    // Nível 1 (ddagrab, captura 100% GPU) desligado por enquanto: no
    // primeiro teste real apareceu uma prévia corrompida (fundo quase
    // preto, com um rastro de vários cursores fantasmas) — padrão clássico
    // de captura de tela que só é atualizada quando o mouse se move (a API
    // de Desktop Duplication tem esse comportamento em certas condições, e
    // ainda não consegui confirmar/corrigir sem poder testar de verdade
    // aqui). Enquanto isso, tudo cai pro Nível 2, que reaproveita a captura
    // GDI de sempre (já testada e comprovada com os 3 amigos) e só troca a
    // compressão por GPU — mais seguro, e ajuda a isolar se o problema era
    // mesmo específico do ddagrab. Ver VIDEO_GPU_NOTES.md.
    // "static readonly" em vez de "const" de propósito: com "const" o
    // compilador sabe em tempo de compilação que isso nunca muda e marca o
    // bloco "if (EnableFullScreenGpuCapture)" abaixo como código
    // inacessível (aviso CS0162) — com "static readonly" o valor só é
    // resolvido em tempo de execução, então o aviso some, e o interruptor
    // continua funcionando do mesmo jeito (edite o "false" abaixo pra
    // "true" quando quiser religar o Nível 1).
    private static readonly bool EnableFullScreenGpuCapture = false;

    // Calcula o tamanho de saída mantendo a proporção original e sem AUMENTAR
    // nada (só reduz). É isso que faz a escolha "720p" do modal realmente
    // valer no caminho por hardware — antes ela era simplesmente ignorada, e
    // quem estava num monitor 4K transmitia 4K mesmo pedindo 720p. Também
    // garante dimensões pares, exigência do formato yuv420p usado pelo H.264.
    private static (int Width, int Height) FitInside(int srcW, int srcH, int maxW, int maxH)
    {
        if (srcW <= 0 || srcH <= 0) return (2, 2);

        double scale = Math.Min(1.0, Math.Min((double)maxW / srcW, (double)maxH / srcH));
        int w = Math.Max(2, (int)Math.Round(srcW * scale));
        int h = Math.Max(2, (int)Math.Round(srcH * scale));

        if (w % 2 != 0) w++;
        if (h % 2 != 0) h++;
        return (w, h);
    }

    private static HardwareEncodeAttempt TryStartHardwareEncodeCore(
        ShareSourceItem? screenItem, ShareSourceItem? windowItem,
        int monitorIndex, int fps, int targetWidth, int targetHeight)
    {
        var result = new HardwareEncodeAttempt();
        var videoEncode = new VideoEncodeService();
        bool started;
        int fixedWidth = targetWidth;
        int fixedHeight = targetHeight;

        if (screenItem?.ScreenRef != null)
        {
            var bounds = screenItem.ScreenRef.Bounds;
            fixedWidth = bounds.Width;
            fixedHeight = bounds.Height;

            started = false;
            if (EnableFullScreenGpuCapture)
            {
                var (fsW, fsH) = FitInside(fixedWidth, fixedHeight, targetWidth, targetHeight);
                started = videoEncode.StartFullScreenHardware(Math.Max(0, monitorIndex), fps, fsW, fsH);
            }
            if (!started)
            {
                var (outW, outH) = FitInside(fixedWidth, fixedHeight, targetWidth, targetHeight);
                started = videoEncode.StartRawPipeHardware(fixedWidth, fixedHeight, outW, outH, fps);
            }
        }
        else if (windowItem != null)
        {
            if (GetWindowRect(windowItem.HWnd, out RECT rect))
            {
                fixedWidth = Math.Max(2, rect.Right - rect.Left);
                fixedHeight = Math.Max(2, rect.Bottom - rect.Top);
            }

            var (outW, outH) = FitInside(fixedWidth, fixedHeight, targetWidth, targetHeight);
            started = videoEncode.StartRawPipeHardware(fixedWidth, fixedHeight, outW, outH, fps);
        }
        else
        {
            started = false;
        }

        if (!started)
        {
            result.Started = false;
            return result;
        }

        // Prévia local: decodifica o mesmo stream que está sendo mandado
        // pra rede, garantindo que a prévia mostra exatamente o que os
        // outros veem (em vez de reaproveitar o bitmap cru capturado).
        var localDecoder = new VideoDecodeService();
        bool localDecoderOk = localDecoder.Start();

        result.Started = true;
        result.Encoder = videoEncode;
        result.LocalDecoder = localDecoder;
        result.LocalDecoderOk = localDecoderOk;
        result.FixedWidth = fixedWidth;
        result.FixedHeight = fixedHeight;
        return result;
    }

    // Igual ao CaptureLoop de sempre (mesmas CopyFromScreen/CaptureWindow),
    // mas em vez de comprimir como JPEG, manda os bytes crus (BGRA) pro
    // encoder de hardware via Feed(). fixedWidth/fixedHeight são o tamanho
    // combinado com o ffmpeg lá no início: o pipe fica travado nesse tamanho,
    // então se a janela compartilhada mudar de tamanho no meio da
    // transmissão, o quadro é redesenhado nesse tamanho em vez de ser
    // descartado (antes, mudar a janela em 1 pixel congelava a transmissão
    // até parar e começar de novo).
    private async Task HardwareRawCaptureLoop(ShareSourceItem? screenItem, ShareSourceItem? windowItem, int fixedWidth, int fixedHeight, VideoEncodeService encoder, int generation, StreamTileViewModel? previewTile = null)
    {
        Stopwatch stopwatch = new Stopwatch();
        var previewClock = Stopwatch.StartNew();

        while (_isStreaming && _videoEncode == encoder && encoder.IsRunning
               && generation == Volatile.Read(ref _streamGeneration))
        {
            stopwatch.Restart();

            try
            {
                if (StopIfSharedWindowClosed(windowItem, generation)) return;

                using Bitmap? rawBmp = CaptureSource(screenItem, windowItem);
                NoteCaptureResult(rawBmp != null, generation);

                if (rawBmp != null)
                {
                    if (rawBmp.Width == fixedWidth && rawBmp.Height == fixedHeight)
                    {
                        encoder.Feed(BitmapToBgraBytes(rawBmp));
                    }
                    else
                    {
                        // Tamanho mudou (janela redimensionada): reenquadra
                        // no tamanho que o ffmpeg está esperando.
                        using var fitted = DrawToFixedSize(rawBmp, fixedWidth, fixedHeight);
                        encoder.Feed(BitmapToBgraBytes(fitted));
                    }

                    // Prévia de emergência (só quando o decodificador da
                    // prévia não subiu). Limitada a ~8 quadros por segundo
                    // porque é só uma miniatura pra você se ver — não vale
                    // gastar CPU comprimindo isso na taxa cheia.
                    if (previewTile != null && previewClock.ElapsedMilliseconds >= 125)
                    {
                        previewClock.Restart();
                        using Bitmap thumb = ResizeIfNeeded(rawBmp, 960, 540);
                        byte[] previewBytes = CompressToJpeg(thumb, 55L);
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            if (_watchedStreams.Contains(previewTile)) previewTile.Image = BytesToBitmapImage(previewBytes);
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
            }
            catch { }

            int frameIntervalMs = 1000 / Math.Max(1, _targetFps);
            int elapsedMs = (int)stopwatch.ElapsedMilliseconds;
            int delay = Math.Max(1, frameIntervalMs - elapsedMs);
            await Task.Delay(delay);
        }

        // Se o encoder morreu sozinho (driver de vídeo reiniciou, ffmpeg
        // caiu) mas o usuário ainda acha que está transmitindo, volta pro
        // pipeline JPEG em vez de deixar todo mundo olhando um quadro
        // congelado sem nenhum aviso.
        if (_isStreaming && !encoder.IsRunning && _videoEncode == encoder
            && generation == Volatile.Read(ref _streamGeneration))
        {
            encoder.Stop();
            await Dispatcher.InvokeAsync(() =>
            {
                if (_videoEncode == encoder) _videoEncode = null;
            });
            _ = Task.Run(() => CaptureLoop(screenItem, windowItem, generation));
        }
    }

    // Conta quantos quadros seguidos a captura falhou, pra avisar o usuário em
    // vez de deixar a transmissão congelada sem explicação. Antes, quando a
    // captura parava de funcionar (o caso do jogo em tela cheia), o app
    // continuava dizendo "transmitindo" e todo mundo via a mesma imagem parada
    // pra sempre, sem nenhum sinal do que tinha acontecido.
    private int _consecutiveCaptureFailures;
    private bool _captureFailureWarned;

    // Verifica se a janela que estava sendo transmitida deixou de existir. Se
    // sim, encerra a transmissão IMEDIATAMENTE em vez de ficar mandando o
    // último quadro parado até o aviso genérico aparecer segundos depois.
    // Fechar a janela é uma intenção clara: acabou o que havia pra transmitir.
    private bool StopIfSharedWindowClosed(ShareSourceItem? windowItem, int generation)
    {
        if (windowItem == null || IsWindow(windowItem.HWnd)) return false;

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_isStreaming || generation != Volatile.Read(ref _streamGeneration)) return;

            StopStream();
            TxtStreamNotice.Text = $"A transmissão foi encerrada: a janela \"{windowItem.Name}\" foi fechada.";
            TxtStreamNotice.Visibility = Visibility.Visible;
        });

        return true;
    }

    private void NoteCaptureResult(bool captured, int generation)
    {
        if (captured)
        {
            _consecutiveCaptureFailures = 0;
            return;
        }

        _consecutiveCaptureFailures++;

        // ~4 segundos sem conseguir um único quadro: algo está errado mesmo.
        int threshold = Math.Max(30, _targetFps * 4);
        if (_captureFailureWarned || _consecutiveCaptureFailures < threshold) return;

        _captureFailureWarned = true;

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_isStreaming || generation != Volatile.Read(ref _streamGeneration)) return;

            System.Windows.MessageBox.Show(
                "A transmissão parou de receber imagem da janela escolhida.\n\n" +
                "Isso costuma acontecer quando a janela é fechada, minimizada, ou quando um jogo " +
                "entra em tela cheia exclusiva — nesse modo o Windows não deixa capturar a janela " +
                "isoladamente.\n\n" +
                "O que costuma resolver: colocar o jogo em \"tela cheia sem bordas\" (ou \"janela sem bordas\") " +
                "nas opções de vídeo dele, ou parar e compartilhar a TELA INTEIRA em vez da janela.",
                "Transmissão sem imagem",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        });
    }

    // Captura a fonte escolhida (monitor ou janela) num Bitmap novo. Toda a
    // criação fica dentro do try/catch com descarte no erro: antes, se o
    // CopyFromScreen falhasse (tela bloqueada, troca de usuário, sessão
    // remota), o Bitmap já criado vazava — e como o laço tenta de novo 30 a
    // 60 vezes por segundo, isso consumia memória gráfica muito rápido.
    private Bitmap? CaptureSource(ShareSourceItem? screenItem, ShareSourceItem? windowItem)
    {
        if (screenItem?.ScreenRef != null)
        {
            var bounds = screenItem.ScreenRef.Bounds;
            Bitmap? bmp = null;
            try
            {
                bmp = new Bitmap(bounds.Width, bounds.Height);
                using Graphics g = Graphics.FromImage(bmp);
                g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                return bmp;
            }
            catch
            {
                bmp?.Dispose();
                return null;
            }
        }

        if (windowItem != null) return CaptureWindow(windowItem.HWnd);
        return null;
    }

    // Desenha um quadro de qualquer tamanho dentro de uma tela do tamanho
    // exato que o encoder espera, mantendo a proporção e preenchendo o resto
    // de preto.
    private static Bitmap DrawToFixedSize(Bitmap source, int width, int height)
    {
        Bitmap target = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using Graphics g = Graphics.FromImage(target);
            g.Clear(System.Drawing.Color.Black);
            g.InterpolationMode = InterpolationMode.Low;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.SmoothingMode = SmoothingMode.HighSpeed;

            double scale = Math.Min((double)width / source.Width, (double)height / source.Height);
            int w = Math.Max(1, (int)(source.Width * scale));
            int h = Math.Max(1, (int)(source.Height * scale));
            g.DrawImage(source, (width - w) / 2, (height - h) / 2, w, h);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    // Extrai os bytes crus BGRA de um Bitmap Format32bppArgb — o stride de
    // um bitmap 32bpp no GDI+ é sempre width*4 (múltiplo de 4 já garantido
    // por 4 bytes/pixel), então não tem padding de linha pra remover.
    private static byte[] BitmapToBgraBytes(Bitmap bmp)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int byteCount = data.Stride * data.Height;
            byte[] bytes = new byte[byteCount];
            Marshal.Copy(data.Scan0, bytes, 0, byteCount);
            return bytes;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private async Task CaptureLoop(ShareSourceItem? screenItem, ShareSourceItem? windowItem, int generation)
    {
        Stopwatch stopwatch = new Stopwatch();

        while (_isStreaming && generation == Volatile.Read(ref _streamGeneration))
        {
            stopwatch.Restart();

            try
            {
                if (StopIfSharedWindowClosed(windowItem, generation)) return;

                Bitmap? rawBmp = CaptureSource(screenItem, windowItem);
                NoteCaptureResult(rawBmp != null, generation);

                if (rawBmp != null)
                {
                    using (rawBmp)
                    {
                        using Bitmap resizedBmp = ResizeIfNeeded(rawBmp, _targetWidth, _targetHeight);
                        byte[] frameBytes = CompressToJpeg(resizedBmp, 50L);

                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            if (_isStreaming)
                            {
                                var localTile = _watchedStreams.FirstOrDefault(t => t.IsLocal);
                                if (localTile != null) localTile.Image = BytesToBitmapImage(frameBytes);
                            }
                        }, System.Windows.Threading.DispatcherPriority.Background);

                        if (_signalR.IsConnected)
                        {
                            // 0x00 na frente = "isto é um JPEG" (pipeline de
                            // sempre) — ver comentário em InitSignalR sobre o
                            // marcador de 1 byte que distingue JPEG de H.264.
                            byte[] framed = new byte[frameBytes.Length + 1];
                            framed[0] = 0x00;
                            Buffer.BlockCopy(frameBytes, 0, framed, 1, frameBytes.Length);
                            _peerMedia?.SendVideo(framed);
                        }
                    }
                }
            }
            catch { }

            int frameIntervalMs = 1000 / Math.Max(1, _targetFps);
            int elapsedMs = (int)stopwatch.ElapsedMilliseconds;
            int delay = Math.Max(1, frameIntervalMs - elapsedMs);
            await Task.Delay(delay);
        }
    }

    private void StopStream()
    {
        _isStreaming = false;

        // Invalida a sessão atual: qualquer laço de captura ainda rodando
        // (ou uma preparação de encoder ainda em andamento numa thread de
        // fundo) percebe a mudança e se encerra sozinho, em vez de continuar
        // vivo em paralelo com a próxima transmissão.
        System.Threading.Interlocked.Increment(ref _streamGeneration);

        _audioCapture.Stop();
        _sharedWindowProcessId = null;

        // Derruba o encoder por hardware, se estava em uso (nada acontece
        // se a transmissão estava no pipeline JPEG de sempre — _videoEncode
        // fica null nesse caso).
        _videoEncode?.Stop();
        _videoEncode = null;

        var myUser = _participants.FirstOrDefault(p => p.UserId == _myUserId);
        if (myUser != null)
        {
            myUser.IsSharing = false;
        }

        // Avisa o servidor (que repassa pros outros) que parei de transmitir.
        // Sem isso, quem estava assistindo ficava com o último quadro
        // congelado e o ícone de "assistindo" não voltava ao normal quando
        // eu começava a transmitir de novo depois.
        _ = _signalR.StopSharingAsync();

        Dispatcher.Invoke(() =>
        {
            BtnShareScreen.Content = "🖥️ TRANSMITIR";
            var localTile = _watchedStreams.FirstOrDefault(t => t.IsLocal);
            if (localTile != null) RemoveWatchTile(localTile);
        });
    }

    // Arrastar a barra do topo.
    //
    // O DragMove() do WPF simplesmente NÃO move uma janela maximizada — ele não
    // dá erro, não faz nada. Por isso, com o Vysor em tela cheia, clicar e
    // arrastar o topo não surtia efeito nenhum.
    //
    // O comportamento que todo mundo espera (e que o Windows faz nas janelas
    // normais) é: arrastar o topo de uma janela maximizada RESTAURA o tamanho
    // anterior e continua o arrasto, com a janela "pendurada" no cursor no
    // mesmo ponto proporcional onde você clicou.
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        // Duplo clique alterna maximizar/restaurar, como em qualquer janela.
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            // Onde o cursor está DENTRO da janela, em proporção (0 a 1).
            System.Windows.Point clickPoint = e.GetPosition(this);
            double ratioX = ActualWidth > 0 ? clickPoint.X / ActualWidth : 0.5;
            double ratioY = ActualHeight > 0 ? clickPoint.Y / ActualHeight : 0.5;

            // Posição do cursor na tela, convertida de pixels físicos para as
            // unidades que Left/Top usam. Sem essa conversão, a janela pularia
            // pra longe do cursor em telas com escala diferente de 100% (o
            // caso comum em notebooks).
            System.Windows.Point cursorOnScreen = PointToScreen(clickPoint);
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double cursorX = cursorOnScreen.X / dpi.DpiScaleX;
            double cursorY = cursorOnScreen.Y / dpi.DpiScaleY;

            double restoredWidth = RestoreBounds.Width > 0 ? RestoreBounds.Width : Width;
            double restoredHeight = RestoreBounds.Height > 0 ? RestoreBounds.Height : Height;

            WindowState = WindowState.Normal;

            // Reposiciona pra que o cursor caia no mesmo ponto proporcional da
            // janela já restaurada.
            Left = cursorX - restoredWidth * ratioX;
            Top = cursorY - restoredHeight * ratioY;
        }

        // Pode falhar se o botão do mouse for solto no meio da transição —
        // nesse caso não há arrasto pra fazer e o erro é inofensivo.
        try { DragMove(); } catch { }
    }
    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnMaximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void BtnClose_Click(object sender, RoutedEventArgs e) { StopStream(); Close(); }

    // Garante que qualquer processo ffmpeg (encode ou decode) seja
    // derrubado não importa como a janela feche — botão de fechar próprio,
    // Alt+F4, barra de tarefas, etc. Sem isso, processos ffmpeg.exe
    // ficariam rodando em segundo plano depois do Vysor já ter fechado.
    protected override void OnClosed(EventArgs e)
    {
        StopStream();

        // Avisa o servidor que estamos saindo de verdade (não é uma queda de
        // conexão). Sem isso, quem fecha o app continuaria aparecendo na lista
        // dos amigos por alguns segundos, até o prazo de reconexão expirar.
        // Task.Run + Wait em vez de await porque este método não pode ser
        // assíncrono; rodar fora da thread da interface evita travar o
        // fechamento, e o limite curto garante que a janela feche mesmo se o
        // servidor não responder.
        _failover?.Cancel();
        _peerMedia?.Stop();

        try
        {
            string code = TxtActiveCode.Text;
            if (!string.IsNullOrWhiteSpace(code) && code != "------")
            {
                Task.Run(() => _signalR.LeaveRoomAsync(code)).Wait(700);
            }
        }
        catch { }

        // Derruba o servidor que este PC mantinha de pé. Se a sala estava
        // hospedada aqui, é neste instante que ela deixa de existir neste
        // endereço — e é por isso que os amigos precisam da fila de sucessão:
        // em segundos, um deles assume e a sala continua.
        try { Task.Run(() => LocalServer.StopAsync()).Wait(2000); } catch { }

        // Fecha a porta que pedimos ao roteador. Deixar aberta não é perigoso
        // (o Vysor não estaria mais ouvindo nela), mas é falta de educação
        // deixar um buraco pra trás na rede da pessoa.
        try { Task.Run(() => PortForwarding.CloseAsync()).Wait(2000); } catch { }

        StopAllTileDecoders();

        // O motor de áudio segura um dispositivo de saída do Windows e uma
        // thread própria; sem descartar, eles continuavam vivos depois da
        // janela fechar.
        try { _audioCapture.Dispose(); } catch { }
        try { _audioPlayback.Dispose(); } catch { }

        base.OnClosed(e);
    }
    // Sem botão no XAML chamando isso desde que o menu lateral ("Início")
    // foi removido — deixei o método aqui, inofensivo, caso o menu volte
    // no futuro.
    private void NavLobby_Click(object sender, RoutedEventArgs e) { if (TxtActiveCode.Text != "------") ViewRoom.Visibility = Visibility.Visible; else ViewLobby.Visibility = Visibility.Visible; }
    private void BtnCloseShareModal_Click(object sender, RoutedEventArgs e) => ShareModal.Visibility = Visibility.Collapsed;

    private void BtnCopyCode_Click(object sender, RoutedEventArgs e)
    {
        // Copia o CONVITE (endereço + código), não só o código. O código
        // sozinho não leva a lugar nenhum agora que a sala mora no PC de
        // alguém — quem recebesse só ele não teria como chegar.
        string toCopy = string.IsNullOrWhiteSpace(_currentInvite)
            ? TxtActiveCode.Text
            : _currentInvite;

        if (string.IsNullOrWhiteSpace(toCopy)) return;

        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetText(toCopy);
                break;
            }
            catch
            {
                System.Threading.Thread.Sleep(10);
            }
        }
    }

    private void ListRecentRooms_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ListRecentRooms.SelectedItem is string code) TxtRoomCode.Text = code; }
    private void TxtDisplayName_TextChanged(object sender, TextChangedEventArgs e) => UpdateLobbyButtons();
    private void TxtRoomCode_TextChanged(object sender, TextChangedEventArgs e) => UpdateLobbyButtons();

    // Liga/desliga os botões da tela inicial conforme o que já foi preenchido.
    //
    // O nome é obrigatório: sem ele, os dois botões ficam apagados e o aviso
    // embaixo do campo explica o porquê. "Entrar com Código" exige também o
    // código, senão o clique não faria nada e pareceria que o app travou.
    private void UpdateLobbyButtons()
    {
        // Chamado pelo TextChanged, que dispara enquanto a janela ainda está
        // sendo montada — nesse momento os outros controles podem não existir.
        if (BtnCreateRoom == null || BtnJoinRoom == null || TxtNameHint == null) return;

        bool hasName = !string.IsNullOrWhiteSpace(TxtDisplayName?.Text);
        bool hasCode = !string.IsNullOrWhiteSpace(TxtRoomCode?.Text);

        // Enquanto uma tentativa está em curso, nada de clicar de novo: cada
        // clique extra derrubava a conexão que estava sendo montada e
        // recomeçava do zero — era isso que fazia parecer que só funcionava
        // "depois de clicar várias vezes".
        BtnCreateRoom.IsEnabled = hasName && !_lobbyBusy;
        BtnJoinRoom.IsEnabled = hasName && hasCode && !_lobbyBusy;

        if (_lobbyBusy) return;   // a mensagem de progresso manda na dica
        TxtNameHint.Visibility = hasName ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is System.Windows.Controls.TabControl tabControl)
        {
            _selectedTabIndex = tabControl.SelectedIndex;

            if (AudioToggle != null)
            {
                AudioToggle.Content = _selectedTabIndex == 0
                    ? "Transmitir com Som"
                    : "Transmitir Som da Janela";
            }
        }
    }

    #region Otimizadores de Imagem
    private byte[] CompressToJpeg(Bitmap bmp, long quality)
    {
        using MemoryStream ms = new MemoryStream();
        ImageCodecInfo jpegEncoder = GetEncoder(ImageFormat.Jpeg);
        using EncoderParameters encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);

        bmp.Save(ms, jpegEncoder, encoderParams);
        return ms.ToArray();
    }

    private ImageCodecInfo GetEncoder(ImageFormat format)
    {
        ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
        foreach (ImageCodecInfo codec in codecs)
        {
            if (codec.FormatID == format.Guid) return codec;
        }
        return codecs[0];
    }

    private Bitmap ResizeIfNeeded(Bitmap original, int maxWidth, int maxHeight)
    {
        if (original.Width <= maxWidth && original.Height <= maxHeight)
            return new Bitmap(original);

        float ratioX = (float)maxWidth / original.Width;
        float ratioY = (float)maxHeight / original.Height;
        float ratio = Math.Min(ratioX, ratioY);

        int newWidth = (int)(original.Width * ratio);
        int newHeight = (int)(original.Height * ratio);

        Bitmap resized = new Bitmap(newWidth, newHeight);
        try
        {
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.Low;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.SmoothingMode = SmoothingMode.HighSpeed;
                g.DrawImage(original, 0, 0, newWidth, newHeight);
            }
            return resized;
        }
        catch
        {
            resized.Dispose(); // senão esse Bitmap vazaria a cada quadro
            throw;
        }
    }
    #endregion

    #region Captura Win32
    private delegate bool EnumWindowsProc(IntPtr hWnd, int lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc enumFunc, int lParam);
    [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    // GetWindowLongPtr só existe em 64 bits; em 32 bits o nome é outro. Este
    // atalho escolhe o certo em tempo de execução.
    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private const uint GA_ROOT = 2;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const int DWMWA_CLOAKED = 14;
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBmp, uint nFlags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // Monta a lista de janelas que a pessoa pode escolher pra transmitir.
    //
    // O filtro é bem mais rigoroso do que o óbvio "está visível?", porque no
    // Windows moderno esse teste sozinho não serve: aplicativos do tipo
    // Configurações, Xbox e afins ficam PRONTOS em segundo plano mesmo quando
    // você nunca os abriu. Para o sistema eles continuam "visíveis" — só estão
    // *encobertos* (o termo do Windows é "cloaked"). Era por isso que a lista
    // vinha cheia de janelas fantasma, com miniatura preta, de programas que
    // nem estavam abertos.
    private List<WindowHandle> GetOpenWindows()
    {
        List<WindowHandle> windows = new();
        uint myProcessId = (uint)Environment.ProcessId;

        EnumWindows((hWnd, lParam) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;

                // Só janelas de verdade, de primeiro nível — nada de janelinhas
                // auxiliares que pertencem a outra.
                if (GetAncestor(hWnd, GA_ROOT) != hWnd) return true;

                // Janelas "de ferramenta" (barras flutuantes, elementos internos
                // de programas) não são coisas que alguém escolheria transmitir.
                long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
                if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

                // ESTE é o filtro que resolve as janelas fantasma.
                if (IsCloaked(hWnd)) return true;

                // Ignora as nossas próprias janelas (não faz sentido transmitir
                // o Vysor de dentro do Vysor).
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == myProcessId) return true;

                // Descarta tamanhos absurdos (janelas de serviço costumam ser
                // 0x0 ou 1x1).
                if (!GetWindowRect(hWnd, out RECT rect)) return true;
                if (rect.Right - rect.Left < 80 || rect.Bottom - rect.Top < 80) return true;

                int length = GetWindowTextLength(hWnd);
                if (length <= 0) return true;

                StringBuilder sb = new StringBuilder(length + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                windows.Add(new WindowHandle { Title = title, hWnd = hWnd });
            }
            catch
            {
                // Uma janela problemática nunca pode derrubar a listagem toda.
            }
            return true;
        }, 0);

        return windows;
    }

    // Pergunta ao gerenciador de janelas do Windows se a janela está
    // "encoberta" — o estado em que apps ficam pré-carregados sem estar
    // realmente abertos na tela.
    private static bool IsCloaked(IntPtr hWnd)
    {
        try
        {
            int cloaked;
            int hr = DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
            return hr == 0 && cloaked != 0;
        }
        catch
        {
            return false; // versão de Windows sem essa consulta: não filtra
        }
    }

    // Como capturar a janela escolhida.
    //
    // Existem dois jeitos, e nenhum serve pra todos os casos:
    //
    //  - PrintWindow: pede pra própria janela se desenhar. É o melhor jeito
    //    pra programas comuns, porque funciona até se a janela estiver atrás
    //    de outra. MAS não funciona com jogos e programas que desenham pela
    //    placa de vídeo (DirectX/OpenGL): nesses casos ele costuma dizer que
    //    deu certo e entregar uma imagem PRETA ou parada. Era exatamente isso
    //    que acontecia com o Dota 2 — como a função dizia "sucesso", o código
    //    nunca tentava a alternativa, e a transmissão congelava pra sempre.
    //
    //  - Recortar da tela: copia o pedaço da tela onde a janela está. Funciona
    //    com QUALQUER coisa, inclusive jogos, mas mostra junto o que estiver
    //    por cima da janela.
    //
    // Então: começamos no PrintWindow e, se ele entregar imagem vazia várias
    // vezes seguidas, trocamos de vez pro recorte de tela nesta transmissão.
    private enum WindowCaptureStrategy { Auto, PrintWindow, ScreenRegion }

    private WindowCaptureStrategy _windowCaptureStrategy = WindowCaptureStrategy.Auto;
    private int _blankPrintWindowFrames;
    private int _captureInspectCounter;

    private Bitmap? CaptureWindow(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out RECT rect)) return null;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        // Janela minimizada não tem o que capturar. Sem esta checagem, saía
        // uma imagem lixo (ou um retângulo do que estivesse atrás).
        if (IsIconic(hWnd)) return null;

        if (_windowCaptureStrategy == WindowCaptureStrategy.ScreenRegion)
        {
            return CaptureScreenRegion(rect, width, height);
        }

        // Todo o preenchimento fica dentro do try: se algo falhar no meio
        // (tela bloqueada, janela sumindo, sessão remota), o Bitmap já
        // alocado é descartado em vez de vazar. Como o laço de captura
        // repete isso 30 a 60 vezes por segundo, um vazamento aqui consumia
        // memória gráfica muito rápido — e memória de GDI não gera pressão
        // no coletor de lixo, então ninguém vinha limpar.
        Bitmap? bmp = null;
        try
        {
            bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            bool success;

            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    success = PrintWindow(hWnd, hdc, 2);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }

            // Enquanto ainda não decidimos, examinamos todo quadro. Depois de
            // concluir que o PrintWindow funciona, examinamos só de vez em
            // quando — o suficiente pra perceber quando um jogo que estava em
            // janela ENTRA em tela cheia e o PrintWindow para de funcionar
            // (foi assim que a transmissão do Dota 2 congelou no meio), sem
            // pagar essa checagem 60 vezes por segundo.
            bool shouldInspect = _windowCaptureStrategy == WindowCaptureStrategy.Auto
                                 || (++_captureInspectCounter % 30) == 0;

            if (success && !(shouldInspect && LooksBlank(bmp)))
            {
                _windowCaptureStrategy = WindowCaptureStrategy.PrintWindow;
                _blankPrintWindowFrames = 0;
                return bmp;
            }

            bmp.Dispose();
            bmp = null;

            // Uma imagem vazia isolada é normal (tela de carregamento preta,
            // por exemplo). Só troca de estratégia depois de várias seguidas.
            if (!success || ++_blankPrintWindowFrames >= 10)
            {
                _windowCaptureStrategy = WindowCaptureStrategy.ScreenRegion;
            }

            return CaptureScreenRegion(rect, width, height);
        }
        catch
        {
            bmp?.Dispose();
            return null;
        }
    }

    private static Bitmap? CaptureScreenRegion(RECT rect, int width, int height)
    {
        Bitmap? bmp = null;
        try
        {
            bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height));
            return bmp;
        }
        catch
        {
            bmp?.Dispose();
            return null;
        }
    }

    // Verifica por amostragem se a imagem é "vazia" (tudo da mesma cor), que é
    // como o PrintWindow devolve conteúdo desenhado pela placa de vídeo. Só
    // olha algumas dezenas de pontos, então custa quase nada mesmo a 60
    // quadros por segundo.
    private static bool LooksBlank(Bitmap bmp)
    {
        // Lê a memória da imagem uma vez só. A alternativa óbvia (GetPixel)
        // tranca e destranca o bitmap a cada ponto lido — custo alto demais
        // pra rodar dentro do laço de captura.
        System.Drawing.Imaging.BitmapData? data = null;
        try
        {
            data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            const int steps = 8;
            int first = 0;
            bool haveFirst = false;

            for (int j = 0; j < steps; j++)
            {
                int y = Math.Min(bmp.Height - 1, (int)((j + 0.5) * bmp.Height / steps));

                for (int i = 0; i < steps; i++)
                {
                    int x = Math.Min(bmp.Width - 1, (int)((i + 0.5) * bmp.Width / steps));
                    int pixel = Marshal.ReadInt32(data.Scan0, y * data.Stride + x * 4);

                    if (!haveFirst) { first = pixel; haveFirst = true; }
                    else if (pixel != first) return false; // achou variação: imagem real
                }
            }

            return true; // todos os pontos iguais
        }
        catch
        {
            return false; // na dúvida, trata como imagem boa
        }
        finally
        {
            if (data != null) { try { bmp.UnlockBits(data); } catch { } }
        }
    }

    private BitmapImage? BytesToBitmapImage(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using (var ms = new MemoryStream(bytes))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }
        catch
        {
            return null;
        }
    }
    #endregion
}
