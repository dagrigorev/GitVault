using System.Runtime.Versioning;
using GitVault.Core.Abstractions;
using Microsoft.Win32;

namespace GitVault.Platform.Windows;

/// <summary>
/// Where Git for Windows puts itself. The registry key is the authoritative answer; the fixed
/// paths cover portable and scoop/chocolatey installs that never write it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGitInstallHints : IGitInstallHints
{
    /// <inheritdoc/>
    public string GitExecutableName => "git.exe";

    /// <inheritdoc/>
    public IReadOnlyList<string> CandidateGitPaths
    {
        get
        {
            var candidates = new List<string>();

            foreach (var installPath in ReadRegistryInstallPaths())
            {
                candidates.Add(Path.Combine(installPath, "cmd", "git.exe"));
                candidates.Add(Path.Combine(installPath, "bin", "git.exe"));
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            candidates.AddRange(
            [
                Path.Combine(programFiles, "Git", "cmd", "git.exe"),
                Path.Combine(programFilesX86, "Git", "cmd", "git.exe"),
                Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe"),
                // VERIFY: scoop and chocolatey layouts against real installs.
                Path.Combine(userProfile, "scoop", "apps", "git", "current", "cmd", "git.exe"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "chocolatey", "bin", "git.exe"),
            ]);

            return candidates;
        }
    }

    private static IReadOnlyList<string> ReadRegistryInstallPaths()
    {
        var results = new List<string>();

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\GitForWindows");
                    if (key?.GetValue("InstallPath") is string path && !string.IsNullOrWhiteSpace(path))
                    {
                        results.Add(path);
                    }
                }
                catch (System.Security.SecurityException)
                {
                    // No read access to the hive: fall through to the fixed candidates.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same.
                }
                catch (IOException)
                {
                    // Key removed while we were reading it.
                }
            }
        }

        return results;
    }
}
