using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VysorClient;

public class ParticipantViewModel : INotifyPropertyChanged
{
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    private bool _isSharing;
    public bool IsSharing
    {
        get => _isSharing;
        set
        {
            if (_isSharing != value)
            {
                _isSharing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Opacity));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(LinkStatusVisible));
            }
        }
    }

    // Está transmitindo e o caminho direto (P2P) com essa pessoa já fechou?
    // Como o servidor não carrega mais vídeo/áudio de jeito nenhum (ver
    // RoomHub.cs), enquanto isto for falso o quadro dela simplesmente não
    // chega — daí a barrinha de status abaixo do nome existir: sem ela, a
    // tela ficaria preta sem nenhuma explicação.
    private bool _isDirect;
    public bool IsDirect
    {
        get => _isDirect;
        set
        {
            if (_isDirect != value)
            {
                _isDirect = value;
                if (value) SameNetworkStuck = false; // conectou: some o aviso
                OnPropertyChanged();
                OnPropertyChanged(nameof(LinkStatusVisible));
                OnPropertyChanged(nameof(LinkStatusText));
            }
        }
    }

    // Indício forte de que vocês estão na mesma rede, mas o furo de NAT não
    // fechou depois de alguns segundos — normalmente isolamento de
    // cliente/AP no roteador, não "internet ruim".
    private bool _sameNetworkStuck;
    public bool SameNetworkStuck
    {
        get => _sameNetworkStuck;
        set
        {
            if (_sameNetworkStuck != value)
            {
                _sameNetworkStuck = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LinkStatusText));
                OnPropertyChanged(nameof(LinkStatusColor));
            }
        }
    }

    public bool LinkStatusVisible => IsSharing && !IsDirect;

    public string LinkStatusText => SameNetworkStuck
        ? "Mesma rede, mas sem conexão direta — veja o isolamento de cliente/AP no roteador"
        : "Conectando direto…";

    public string LinkStatusColor => SameNetworkStuck ? "#F0B132" : "#6B7280";

    // Verdadeiro quando essa pessoa esta atualmente sendo exibida em um dos tiles.
    private bool _isWatching;
    public bool IsWatching
    {
        get => _isWatching;
        set
        {
            if (_isWatching != value)
            {
                _isWatching = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RowBackground));
                OnPropertyChanged(nameof(WatchIndicatorVisible));
                OnPropertyChanged(nameof(PlayIcon));
            }
        }
    }

    public double Opacity => IsSharing ? 1.0 : 0.4;
    public string StatusColor => IsSharing ? "#23A55A" : "#6B7280";
    public string RowBackground => IsWatching ? "#3C3F58" : "#2D2D36";
    public bool WatchIndicatorVisible => IsWatching;

    // Muda de ícone quando você já está assistindo essa pessoa, pra deixar
    // claro que dá pra clicar de novo pra PARAR de assistir (antes o botão
    // era sempre "▶", sem indicar que ele também serve pra parar).
    public string PlayIcon => IsWatching ? "⏸" : "▶";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
