using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VysorClient.Services;

// Amarra os processos filhos (ffmpeg.exe) ao tempo de vida do Vysor usando um
// "Job Object" do Windows configurado com KILL_ON_JOB_CLOSE.
//
// Por que isso existe: o Vysor sobe um ffmpeg pra codificar a sua transmissão
// e mais um por pessoa que você assiste. O código tenta encerrar cada um deles
// na mão (Stop()), mas isso só funciona quando o app fecha de forma normal. Se
// o Vysor travar, for morto pelo Gerenciador de Tarefas, ou cair por um erro
// não tratado, esses ffmpeg ficariam rodando pra sempre em segundo plano —
// consumindo memória e, pior, segurando sessões do encoder da GPU (a NVENC tem
// um limite pequeno de sessões simultâneas, então depois de algumas vezes a
// codificação por hardware simplesmente pararia de funcionar até reiniciar o
// PC).
//
// Com o Job Object, o Windows garante que, no instante em que o processo do
// Vysor morre (por qualquer motivo, inclusive um crash), todos os ffmpeg
// associados morrem junto. É a única garantia que não depende do nosso código
// conseguir rodar na hora de fechar.
public static class ChildProcessTracker
{
    private static readonly object _lock = new();
    private static IntPtr _jobHandle = IntPtr.Zero;
    private static bool _initTried;

    // Associa um processo recém-criado ao job. Silenciosamente não faz nada se
    // o job não pôde ser criado (versões antigas do Windows, políticas de
    // segurança, o processo já estar em outro job que não permite aninhar) —
    // nesse caso continua valendo só a limpeza manual via Stop().
    public static void Track(Process process)
    {
        try
        {
            IntPtr job = EnsureJob();
            if (job == IntPtr.Zero) return;
            AssignProcessToJobObject(job, process.Handle);
        }
        catch
        {
            // Nunca deixa uma falha aqui atrapalhar a transmissão.
        }
    }

    private static IntPtr EnsureJob()
    {
        lock (_lock)
        {
            if (_initTried) return _jobHandle;
            _initTried = true;

            try
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;

                var limitInfo = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                };

                var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = limitInfo
                };

                int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr extendedPtr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(extended, extendedPtr, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, extendedPtr, (uint)length))
                    {
                        CloseHandle(job);
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(extendedPtr);
                }

                _jobHandle = job;
            }
            catch
            {
                _jobHandle = IntPtr.Zero;
            }

            return _jobHandle;
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
