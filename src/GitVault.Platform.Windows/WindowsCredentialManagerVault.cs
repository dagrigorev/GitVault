using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using GitVault.Core.Abstractions;
using GitVault.Core.Credentials;
using GitVault.Core.Models;

namespace GitVault.Platform.Windows;

/// <summary>
/// Windows Credential Manager, through the <c>Cred*</c> family in <c>advapi32</c>.
/// </summary>
/// <remarks>
/// Enumeration deliberately reads metadata only. Secret bytes are fetched one entry at a time by
/// <see cref="RevealAsync"/>, so a scan never materialises a password, and the buffers that do
/// hold one are zeroed before being released.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerVault : ICredentialVault
{
    private const int CredTypeGeneric = 1;
    private const int CredTypeDomainPassword = 2;
    private const int CredPersistLocalMachine = 2;

    private const int ErrorNotFound = 1168;
    private const int ErrorNoSuchLogonSession = 1312;
    private const int ErrorInvalidParameter = 87;

    /// <inheritdoc/>
    public VaultKind Kind => VaultKind.WindowsCredentialManager;

    /// <inheritdoc/>
    public bool IsAvailable => OperatingSystem.IsWindows();

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc/>
    public Task<IReadOnlyList<CredentialEntry>> ListAsync(CancellationToken cancellationToken)
    {
        var entries = new List<CredentialEntry>();

        if (!CredEnumerate(null, 0, out var count, out var credentialsPointer))
        {
            var error = Marshal.GetLastWin32Error();

            // An empty store and a session with no credentials both report "not found"; neither
            // is a failure worth surfacing.
            if (error is ErrorNotFound or ErrorNoSuchLogonSession)
            {
                return Task.FromResult<IReadOnlyList<CredentialEntry>>(entries);
            }

            throw new Win32Exception(error);
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entryPointer = Marshal.ReadIntPtr(credentialsPointer, i * IntPtr.Size);
                var credential = Marshal.PtrToStructure<Credential>(entryPointer);

                entries.Add(ToEntry(credential));
            }
        }
        finally
        {
            CredFree(credentialsPointer);
        }

        return Task.FromResult<IReadOnlyList<CredentialEntry>>(entries);
    }

    /// <inheritdoc/>
    public Task<byte[]?> RevealAsync(string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var type in new[] { CredTypeGeneric, CredTypeDomainPassword })
        {
            if (!CredRead(target, type, 0, out var pointer))
            {
                continue;
            }

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                {
                    return Task.FromResult<byte[]?>(null);
                }

                var blob = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                return Task.FromResult<byte[]?>(blob);
            }
            finally
            {
                CredFree(pointer);
            }
        }

        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc/>
    public Task WriteAsync(CredentialEntry entry, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        var blob = secret.ToArray();
        var blobPointer = Marshal.AllocHGlobal(blob.Length == 0 ? 1 : blob.Length);

        try
        {
            if (blob.Length > 0)
            {
                Marshal.Copy(blob, 0, blobPointer, blob.Length);
            }

            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = entry.Target,
                UserName = entry.UserName,
                CredentialBlob = blobPointer,
                CredentialBlobSize = blob.Length,
                Persist = CredPersistLocalMachine,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Task.CompletedTask;
        }
        finally
        {
            // Zero both copies: the managed array and the unmanaged block we handed to Windows.
            CryptographicOperations.ZeroMemory(blob);

            if (blob.Length > 0)
            {
                var zeros = new byte[blob.Length];
                Marshal.Copy(zeros, 0, blobPointer, zeros.Length);
            }

            Marshal.FreeHGlobal(blobPointer);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var type in new[] { CredTypeGeneric, CredTypeDomainPassword })
        {
            if (CredDelete(target, type, 0))
            {
                return Task.FromResult(true);
            }

            var error = Marshal.GetLastWin32Error();
            if (error is not (ErrorNotFound or ErrorInvalidParameter))
            {
                throw new Win32Exception(error);
            }
        }

        return Task.FromResult(false);
    }

    /// <summary>Converts a native credential record into the domain model, without its secret.</summary>
    /// <param name="credential">Native record.</param>
    /// <returns>Metadata about the entry.</returns>
    private static CredentialEntry ToEntry(Credential credential)
    {
        var target = credential.TargetName ?? string.Empty;

        return new CredentialEntry(
            VaultKind.WindowsCredentialManager,
            target,
            CredentialTargetFilter.ExtractHost(target),
            credential.UserName ?? string.Empty,
            credential.CredentialBlobSize > 0,
            CredentialTargetFilter.ExtractProtocol(target),
            ToTimestamp(credential.LastWritten),
            GuessOwningClient(target, credential.Comment),
            IsReadOnly: false);
    }

    /// <summary>
    /// Attributes an entry to the tool that created it, from the naming conventions each uses.
    /// </summary>
    /// <param name="target">Target string.</param>
    /// <param name="comment">Comment the writer left, when any.</param>
    /// <returns>A product name, or null when it is not recognisable.</returns>
    internal static string? GuessOwningClient(string target, string? comment)
    {
        // VERIFY: these prefixes against fresh installs. They are conventions, not contracts,
        // and a wrong guess here only mislabels a row rather than breaking anything.
        if (target.StartsWith("git:", StringComparison.OrdinalIgnoreCase)
            || comment?.Contains("Git Credential Manager", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Git Credential Manager";
        }

        if (target.Contains("sourcetree", StringComparison.OrdinalIgnoreCase))
        {
            return "Sourcetree";
        }

        if (target.Contains("GitHub", StringComparison.Ordinal) && target.Contains("Desktop", StringComparison.Ordinal))
        {
            return "GitHub Desktop";
        }

        if (target.Contains("gitkraken", StringComparison.OrdinalIgnoreCase))
        {
            return "GitKraken";
        }

        return null;
    }

    private static DateTimeOffset? ToTimestamp(System.Runtime.InteropServices.ComTypes.FILETIME fileTime)
    {
        var value = ((long)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;
        if (value <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string? filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>
/// Git Credential Manager's DPAPI store: one file per credential under
/// <c>%USERPROFILE%\.gcm\dpapi_store</c>, encrypted to the current user.
/// </summary>
/// <remarks>
/// VERIFY: the on-disk layout against a current Git Credential Manager. GitVault treats each
/// file as an opaque DPAPI blob and never guesses at an internal structure it cannot confirm.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class GcmDpapiStoreVault : ICredentialVault
{
    private readonly IPlatformPaths _paths;

    /// <summary>Creates the vault.</summary>
    /// <param name="paths">Platform paths, for locating the store.</param>
    public GcmDpapiStoreVault(IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc/>
    public VaultKind Kind => VaultKind.GcmDpapi;

    /// <inheritdoc/>
    public bool IsAvailable => Directory.Exists(StoreDirectory);

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <summary>Directory the store lives in.</summary>
    public string StoreDirectory => Path.Combine(_paths.HomeDirectory, ".gcm", "dpapi_store");

    /// <inheritdoc/>
    public Task<IReadOnlyList<CredentialEntry>> ListAsync(CancellationToken cancellationToken)
    {
        var entries = new List<CredentialEntry>();

        if (!Directory.Exists(StoreDirectory))
        {
            return Task.FromResult<IReadOnlyList<CredentialEntry>>(entries);
        }

        foreach (var file in SafeEnumerate(StoreDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The file name is the target, with path separators encoded as directories.
            var target = Path.GetRelativePath(StoreDirectory, file).Replace('\\', '/');

            entries.Add(new CredentialEntry(
                VaultKind.GcmDpapi,
                target,
                CredentialTargetFilter.ExtractHost(target),
                UserName: string.Empty,
                SecretPresent: true,
                CredentialTargetFilter.ExtractProtocol(target),
                SafeLastWrite(file),
                OwningClient: "Git Credential Manager",
                IsReadOnly: true)
            {
                SourcePath = file,
            });
        }

        return Task.FromResult<IReadOnlyList<CredentialEntry>>(entries);
    }

    /// <inheritdoc/>
    public async Task<byte[]?> RevealAsync(string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var path = Path.Combine(StoreDirectory, target.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        try
        {
            return ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Written by another user, or by a different GCM version with added entropy.
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    /// <inheritdoc/>
    public Task WriteAsync(CredentialEntry entry, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "GitVault does not write into another application's private store.");

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(string target, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "GitVault does not delete from another application's private store.");

    private static IEnumerable<string> SafeEnumerate(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DateTimeOffset? SafeLastWrite(string path)
    {
        try
        {
            return new FileInfo(path).LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
