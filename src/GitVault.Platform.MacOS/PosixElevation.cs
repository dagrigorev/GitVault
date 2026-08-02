using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitVault.Platform.MacOS;

/// <summary>Determines whether the process runs as root.</summary>
[SupportedOSPlatform("macos")]
internal static class PosixElevation
{
    /// <summary>Returns true when the effective user id is 0.</summary>
    /// <returns><see langword="true"/> when running as root.</returns>
    internal static bool IsRoot()
    {
        try
        {
            return GetEuid() == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    // Classic DllImport rather than LibraryImport: the signature is fully blittable, so the
    // source generator would buy nothing and would force AllowUnsafeBlocks on the project.
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();
}
