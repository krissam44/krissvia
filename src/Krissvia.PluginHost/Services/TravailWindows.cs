using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Krissvia.PluginHost.Services;

/// <summary>
/// Un Job Object Windows qui tue tous ses processus quand son dernier handle se ferme
/// (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>).
/// </summary>
/// <remarks>
/// C'est ce qui empêche un plugin de survivre au cœur : à la mort du cœur, même brutale (arrêt du débogueur,
/// kill, plantage), Windows ferme ses handles, donc celui-ci, et les plugins partent avec lui. Le
/// <c>Kill(entireProcessTree)</c> du superviseur ne couvre que les arrêts voulus. Un processus lancé par le
/// plugin entre son démarrage et son affectation au job n'y entrerait pas ; aucun plugin ne le fait.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class TravailWindows : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    private readonly HandleTravail _handle;

    public TravailWindows()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());

        var limites = new LimitesEtendues { Base = new LimitesDeBase { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref limites, (uint)Marshal.SizeOf<LimitesEtendues>()))
        {
            var erreur = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw new Win32Exception(erreur);
        }
    }

    /// <summary>Le processus mourra avec le job, donc avec le cœur.</summary>
    public void Ajouter(Process processus)
    {
        if (!AssignProcessToJobObject(_handle, processus.SafeHandle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    /// <summary>Ferme le job : les processus qui y sont encore sont tués.</summary>
    public void Dispose() => _handle.Dispose();

    // ── Win32 ────────────────────────────────────────────────────────────────

    private sealed class HandleTravail() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LimitesDeBase
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
    private struct CompteursES
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LimitesEtendues
    {
        public LimitesDeBase Base;
        public CompteursES IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial HandleTravail CreateJobObjectW(IntPtr attributs, string? nom);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(HandleTravail job, int classe, ref LimitesEtendues limites, uint taille);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(HandleTravail job, SafeProcessHandle processus);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
