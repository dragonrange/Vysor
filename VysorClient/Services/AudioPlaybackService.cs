using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VysorClient.Services;

// Toca o áudio recebido de cada participante que você está assistindo, com
// volume e mudo independentes por pessoa (0% a 150%, como os controles de
// cada "telinha" pedem). Todos os fluxos são mixados e tocados juntos numa
// única saída de áudio.
public sealed class AudioPlaybackService : IDisposable
{
    private const int WireSampleRate = 48000;

    private readonly object _lock = new();
    private readonly Dictionary<string, ParticipantAudio> _participants = new();
    private readonly MixingSampleProvider _mixer;
    private readonly IWavePlayer _output;
    private bool _disposed;

    private sealed class ParticipantAudio
    {
        public required BufferedWaveProvider Buffer;
        public required VolumeSampleProvider VolumeProvider;
        public bool IsMuted;
        public float VolumePercent = 100f;
    }

    public AudioPlaybackService()
    {
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(WireSampleRate, 2))
        {
            ReadFully = true
        };

        _output = new WaveOutEvent();
        _output.Init(_mixer);
        _output.Play();
    }

    public void Feed(string userId, byte[] muLawPayload)
    {
        if (_disposed || muLawPayload.Length == 0) return;

        lock (_lock)
        {
            var participant = GetOrCreateParticipant(userId);
            byte[] pcm = MuLawCodec.DecodeToPcm16(muLawPayload, 0, muLawPayload.Length);
            participant.Buffer.AddSamples(pcm, 0, pcm.Length);
        }
    }

    // Nos dois métodos abaixo o participante é CRIADO se ainda não existir.
    // Antes eles saíam sem fazer nada quando a pessoa ainda não tinha
    // mandado áudio nenhum, e o ajuste era simplesmente perdido: dava pra
    // abrir a telinha de alguém e mutar, e quando essa pessoa começasse a
    // falar o áudio saía no volume normal — com o ícone na tela mostrando
    // "mudo".
    public void SetVolumePercent(string userId, double percent)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var participant = GetOrCreateParticipant(userId);
            participant.VolumePercent = (float)Math.Clamp(percent, 0, 150);
            ApplyVolume(participant);
        }
    }

    public void SetMuted(string userId, bool muted)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var participant = GetOrCreateParticipant(userId);
            participant.IsMuted = muted;
            ApplyVolume(participant);
        }
    }

    public void RemoveParticipant(string userId)
    {
        lock (_lock)
        {
            if (_participants.TryGetValue(userId, out var participant))
            {
                _mixer.RemoveMixerInput(participant.VolumeProvider);
                _participants.Remove(userId);
            }
        }
    }

    private static void ApplyVolume(ParticipantAudio participant)
    {
        participant.VolumeProvider.Volume = participant.IsMuted ? 0f : participant.VolumePercent / 100f;
    }

    private ParticipantAudio GetOrCreateParticipant(string userId)
    {
        if (_participants.TryGetValue(userId, out var existing)) return existing;

        var waveFormat = new WaveFormat(WireSampleRate, 16, 1);
        var buffer = new BufferedWaveProvider(waveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };

        // mono 16-bit -> float -> estéreo, pra bater com o formato do mixer.
        var stereo = buffer.ToSampleProvider().ToStereo();
        var volume = new VolumeSampleProvider(stereo) { Volume = 1.0f };

        var participant = new ParticipantAudio { Buffer = buffer, VolumeProvider = volume };
        _participants[userId] = participant;
        _mixer.AddMixerInput(volume);
        return participant;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _output.Stop(); } catch { /* ignora */ }
        try { _output.Dispose(); } catch { /* ignora */ }

        lock (_lock)
        {
            // Desconecta cada fluxo do mixer antes de esquecer os
            // participantes, senão o mixer continuaria segurando (e lendo)
            // buffers de gente que já saiu.
            foreach (var participant in _participants.Values)
            {
                try { _mixer.RemoveMixerInput(participant.VolumeProvider); } catch { }
            }
            _participants.Clear();
        }
    }
}
