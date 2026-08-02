namespace GitVault.Core.Abstractions;

/// <summary>
/// Opens folders and files in the platform's file manager. The only outward-facing action
/// GitVault performs; it launches no network-capable handler of its own.
/// </summary>
public interface IShellLauncher
{
    /// <summary>Opens a folder in the file manager.</summary>
    /// <param name="directoryPath">Directory to open.</param>
    /// <returns><see langword="true"/> when the file manager was launched.</returns>
    bool OpenDirectory(string directoryPath);

    /// <summary>Opens the file manager with the file selected, when the platform supports it.</summary>
    /// <param name="filePath">File to reveal.</param>
    /// <returns><see langword="true"/> when the file manager was launched.</returns>
    bool RevealFile(string filePath);
}
