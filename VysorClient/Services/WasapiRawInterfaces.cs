using System;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace VysorClient.Services;

// Definições mínimas e "na mão" das interfaces COM IAudioClient / IAudioCaptureClient
// (Windows Core Audio - audioclient.h), na ordem exata da vtable oficial. Precisamos
// delas porque o IAudioClient que recebemos de ActivateAudioInterfaceAsync (via
// ProcessLoopbackInterop) não passa pelo caminho normal do NAudio (que só sabe
// construir a partir de um MMDevice comum) — então falamos com ele diretamente.
//
// Cada método é [PreserveSig] e devolve o HRESULT cru como int: o chamador decide
// como tratar erro (nós preferimos checar manualmente e lançar uma exceção só
// quando faz sentido, em vez de depender do comportamento "automático" de exceptions
// da interop, que é mais fácil de acertar errado silenciosamente).
[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRawAudioClient
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, WaveFormat pFormat, ref Guid audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferSize);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out int currentPadding);
    [PreserveSig] int IsFormatSupported(int shareMode, WaveFormat pFormat, IntPtr closestMatchFormat);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormatPointer);
    [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId, out IntPtr interfacePointer);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRawAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr dataBuffer, out int numFramesToRead, out int bufferFlags, out long devicePosition, out long qpcPosition);
    [PreserveSig] int ReleaseBuffer(int numFramesRead);
    [PreserveSig] int GetNextPacketSize(out int numFramesInNextPacket);
}

internal static class WasapiConstants
{
    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const int AUDCLNT_STREAMFLAGS_LOOPBACK = unchecked((int)0x00020000);

    // Modo "avisa-me quando houver áudio" em vez de ficar perguntando de tempos
    // em tempos. Todas as implementações de referência da captura por processo
    // (o exemplo oficial da Microsoft e o OBS) usam este modo — ver comentário
    // em AudioCaptureService.TryStartRawCapture.
    public const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = unchecked((int)0x00040000);

    public const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
    public const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = unchecked((int)0x08000000);
    public const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
}
