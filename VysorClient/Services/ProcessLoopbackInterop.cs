using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace VysorClient.Services;

// -----------------------------------------------------------------------------------
// Interoperabilidade de baixo nível com a API do Windows "Process Loopback"
// (ActivateAudioInterfaceAsync + AUDIOCLIENT_ACTIVATION_PARAMS), disponível a
// partir do Windows 10 build 20348 / Windows 11. Ela permite capturar o áudio
// de UM processo específico (e seus processos-filhos) — ou o inverso, capturar
// TUDO exceto um processo específico.
//
// É exatamente essa API que usamos para dois cenários pedidos:
//   1) Ao compartilhar uma JANELA específica: capturamos só o áudio do processo
//      dono daquela janela (modo "incluir"), então nunca vaza áudio de outros apps.
//   2) Ao compartilhar a TELA INTEIRA: capturamos o áudio do sistema todo, EXCETO
//      o do Discord (modo "excluir"), para que a chamada de voz dos seus amigos
//      no Discord nunca seja transmitida junto.
//
// Esta é uma API do Windows relativamente nova e pouco documentada em C# (a
// Microsoft só publica exemplos em C++). O código abaixo segue fielmente a
// estrutura oficial (GUIDs e layout de structs conferidos com a documentação e
// com implementações de referência). Ainda assim, por ser interoperabilidade
// COM de baixo nível "na mão", QUALQUER falha aqui é capturada pelo chamador
// (AudioCaptureService), que cai automaticamente para a captura de áudio do
// sistema inteiro (NAudio.WasapiLoopbackCapture) — ou seja, se algo não
// funcionar nesta parte específica em algum Windows, o app não quebra: ele só
// deixa de isolar o áudio, mas continua transmitindo áudio normalmente.
// -----------------------------------------------------------------------------------
internal static class ProcessLoopbackInterop
{
    // Caminho "virtual" de dispositivo usado para pedir uma captura de loopback
    // por processo em vez de um dispositivo de áudio físico normal.
    private const string VirtualAudioDeviceProcessLoopback = @"VAD\Process_Loopback";

    private const int ModeIncludeTargetProcessTree = 0;
    private const int ModeExcludeTargetProcessTree = 1;

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [In, MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [In] IntPtr activationParams,
        [In] IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(
            out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    // Struct nativa AUDIOCLIENT_ACTIVATION_PARAMS "achatada": no C++ original o
    // segundo campo é uma union com um único membro possível (ProcessLoopbackParams),
    // então representá-la como três campos sequenciais tem exatamente o mesmo layout
    // de memória.
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;      // 1 = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
        public uint TargetProcessId;
        public int ProcessLoopbackMode; // 0 = incluir árvore do processo, 1 = excluir
    }

    // PROPVARIANT reduzida apenas ao layout necessário para vt = VT_BLOB
    // (cabeçalho de 8 bytes + BLOB{cbSize:4 (+4 padding), pBlobData:8} = 24 bytes no x64).
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint blobSize;
        [FieldOffset(16)] public IntPtr blobData;
    }

    private const ushort VT_BLOB = 0x41;

    private sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEventSlim _completedEvent = new(false);
        public int HResult { get; private set; }
        public object? ActivatedInterface { get; private set; }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object obj);
                HResult = hr;
                ActivatedInterface = obj;
            }
            catch (Exception)
            {
                HResult = -1;
            }
            finally
            {
                _completedEvent.Set();
            }
        }

        public bool Wait(TimeSpan timeout) => _completedEvent.Wait(timeout);
    }

    /// <summary>
    /// Ativa um IAudioClient de loopback filtrado por processo. Lança exceção em
    /// qualquer falha — quem chama deve tratar isso e cair de volta para a
    /// captura de sistema inteiro.
    /// </summary>
    /// <param name="targetProcessId">PID do processo alvo.</param>
    /// <param name="exclude">
    /// Se true, captura TUDO menos o áudio desse processo (e filhos).
    /// Se false, captura só o áudio desse processo (e filhos).
    /// </param>
    public static object ActivateProcessLoopbackAudioClient(uint targetProcessId, bool exclude)
    {
        var nativeParams = new AudioClientActivationParams
        {
            ActivationType = 1,
            TargetProcessId = targetProcessId,
            ProcessLoopbackMode = exclude ? ModeExcludeTargetProcessTree : ModeIncludeTargetProcessTree
        };

        int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(paramsSize);
        IntPtr propvariantPtr = IntPtr.Zero;

        try
        {
            Marshal.StructureToPtr(nativeParams, paramsPtr, false);

            var propvariant = new PropVariantBlob
            {
                vt = VT_BLOB,
                blobSize = (uint)paramsSize,
                blobData = paramsPtr
            };

            propvariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
            Marshal.StructureToPtr(propvariant, propvariantPtr, false);

            var handler = new ActivationCompletionHandler();

            ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                IID_IAudioClient,
                propvariantPtr,
                handler,
                out _);

            if (!handler.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Ativação do loopback por processo não respondeu a tempo.");

            if (handler.HResult != 0 || handler.ActivatedInterface == null)
                throw new InvalidOperationException($"Falha ao ativar loopback por processo (HRESULT=0x{handler.HResult:X8}).");

            return handler.ActivatedInterface;
        }
        finally
        {
            if (paramsPtr != IntPtr.Zero) Marshal.FreeHGlobal(paramsPtr);
            if (propvariantPtr != IntPtr.Zero) Marshal.FreeHGlobal(propvariantPtr);
        }
    }
}
