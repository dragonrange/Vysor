using System;
using System.IO;

namespace VysorClient.Services;

// Resolve o caminho do ffmpeg.exe empacotado do lado do Vysor.exe — mesma
// ideia do server.txt em SignalRService.GetServerUrl(): um arquivo extra
// colocado ao lado do executável, sem precisar estar no PATH do Windows nem
// ser instalado separadamente. Se o arquivo não existir (por exemplo,
// alguém rodou só o Vysor.exe sem incluir o ffmpeg no zip), tudo que depende
// dele simplesmente relata "não disponível" e quem chamou cai de volta pro
// pipeline JPEG/GDI de sempre.
public static class FfmpegLocator
{
    private static string? _cachedPath;
    private static bool _checked;

    public static string? GetFfmpegPath()
    {
        if (_checked) return _cachedPath;
        _checked = true;

        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(path))
            {
                _cachedPath = path;
            }
        }
        catch
        {
            _cachedPath = null;
        }

        return _cachedPath;
    }

    public static bool IsAvailable => GetFfmpegPath() != null;
}
