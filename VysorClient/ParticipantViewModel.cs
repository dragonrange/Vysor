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
            }
        }
    }

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
