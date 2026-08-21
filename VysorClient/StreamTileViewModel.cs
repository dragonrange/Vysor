using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace VysorClient;

// Representa um "tile" de transmissão sendo assistido (a sua própria prévia
// enquanto compartilha, ou a tela de outro participante). Cada tile guarda seu
// próprio volume, estado de mudo e de fixado (pin), independente dos outros.
public class StreamTileViewModel : INotifyPropertyChanged
{
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    // Tile da sua própria transmissão (o "X" aqui para a transmissão em vez de só deixar de assistir).
    public bool IsLocal { get; set; }

    // Não faz sentido mostrar volume/mudo pra sua própria prévia (você não
    // ouve seu próprio áudio de volta) — só pros tiles dos outros participantes.
    public bool ShowAudioControls => !IsLocal;

    private BitmapImage? _image;
    public BitmapImage? Image
    {
        get => _image;
        set { _image = value; OnPropertyChanged(); }
    }

    // 0 a 150%, como pedido: 100% é o volume "normal" do áudio recebido.
    private double _volume = 100;
    public double Volume
    {
        get => _volume;
        set { _volume = value; OnPropertyChanged(); }
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted != value)
            {
                _isMuted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SpeakerBorderColor));
            }
        }
    }

    // Ícone fixo — quem indica o estado agora é a cor da borda (verde = ligado,
    // vermelho = mudo), com fundo sempre transparente, como pedido.
    public string SpeakerIcon => "🔊";
    public string SpeakerBorderColor => IsMuted ? "#F23F42" : "#23A55A";

    // Pin: quando fixado, esse tile ocupa sozinho o espaço de transmissões até
    // ser desfixado. Controlado pelo MainWindow (só um tile pode estar
    // fixado por vez), refletido aqui só para o binding do ícone/borda.
    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned != value)
            {
                _isPinned = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PinBorderColor));
            }
        }
    }

    public string PinBorderColor => IsPinned ? "#5865F2" : "#B3000000";

    // Visibilidade individual do tile dentro do grid — usada para esconder os
    // outros tiles quando um deles está fixado (pin).
    private Visibility _tileVisibility = Visibility.Visible;
    public Visibility TileVisibility
    {
        get => _tileVisibility;
        set { _tileVisibility = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
