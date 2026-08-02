using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GitVault.Core.Platform;

namespace GitVault.Platform.Linux;

/// <summary>Linux filesystem locations for GitVault, following the XDG base directory spec.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformPaths : PlatformPathsBase
{
    /// <inheritdoc/>
    public override string AppDataDirectory => Path.Combine(XdgConfigHome, "gitvault");

    /// <inheritdoc/>
    public override string GlobalGitConfigPath
    {
        get
        {
            // git prefers $XDG_CONFIG_HOME/git/config only when ~/.gitconfig does not exist.
            var dotFile = Path.Combine(HomeDirectory, ".gitconfig");
            if (File.Exists(dotFile))
            {
                return dotFile;
            }

            var xdgFile = Path.Combine(XdgConfigHome, "git", "config");
            return File.Exists(xdgFile) ? xdgFile : dotFile;
        }
    }

    /// <inheritdoc/>
    public override IReadOnlyList<string> SystemGitConfigCandidates =>
    [
        "/etc/gitconfig",
        "/usr/local/etc/gitconfig",
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> AdditionalKeyDirectories => ExistingDirectories(
    [
        Path.Combine(HomeDirectory, "keys"),
        Path.Combine(XdgConfigHome, "ssh"),
    ]);

    private string XdgConfigHome
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(HomeDirectory, ".config")
                : xdg;
        }
    }
}

/// <summary>Linux platform facts.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformInfo : PlatformInfoBase
{
    /// <inheritdoc/>
    public override string PlatformId => "linux";

    /// <inheritdoc/>
    public override bool SupportsPosixPermissions => true;

    /// <inheritdoc/>
    public override bool IsElevated
    {
        get
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
    }

    // Classic DllImport rather than LibraryImport: the signature is fully blittable, so the
    // source generator would buy nothing and would force AllowUnsafeBlocks on the project.
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();
}
