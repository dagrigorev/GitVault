using System.ComponentModel;
using System.Diagnostics;
using GitVault.Core.Abstractions;

namespace GitVault.Core.Platform;

/// <summary>
/// Shared process-launching plumbing for the per-OS <see cref="IShellLauncher"/> implementations.
/// A failure to launch is reported as <see langword="false"/>, never as an exception.
/// </summary>
public abstract class ShellLauncherBase : IShellLauncher
{
    /// <inheritdoc/>
    public bool OpenDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        var (fileName, arguments) = BuildOpenDirectoryCommand(directoryPath);
        return Launch(fileName, arguments);
    }

    /// <inheritdoc/>
    public bool RevealFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        var (fileName, arguments) = BuildRevealFileCommand(filePath);
        return Launch(fileName, arguments);
    }

    /// <summary>Builds the command that opens a directory.</summary>
    /// <param name="directoryPath">Directory to open.</param>
    /// <returns>Executable and argument list.</returns>
    protected abstract (string FileName, IReadOnlyList<string> Arguments) BuildOpenDirectoryCommand(
        string directoryPath);

    /// <summary>Builds the command that reveals a file.</summary>
    /// <param name="filePath">File to reveal.</param>
    /// <returns>Executable and argument list.</returns>
    protected abstract (string FileName, IReadOnlyList<string> Arguments) BuildRevealFileCommand(
        string filePath);

    private static bool Launch(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}
