using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Ssh;

/// <summary>What kind of key to create.</summary>
/// <param name="Algorithm">Algorithm family.</param>
/// <param name="BitLength">Size for RSA and ECDSA; ignored for Ed25519.</param>
/// <param name="Comment">Comment to store in the key.</param>
/// <param name="KdfRounds">bcrypt work factor, when a passphrase is supplied.</param>
public sealed record SshKeyGenerationRequest(
    SshKeyAlgorithm Algorithm,
    int? BitLength,
    string Comment,
    int KdfRounds = 24);

/// <summary>Outcome of creating a key.</summary>
/// <param name="Succeeded">Whether the key was written.</param>
/// <param name="PrivatePath">Path of the new private key.</param>
/// <param name="PublicPath">Path of the new public key.</param>
/// <param name="Fingerprint">Canonical fingerprint of the new key.</param>
/// <param name="Diagnostics">Redacted explanation when the operation failed.</param>
public sealed record SshKeyOperationResult(
    bool Succeeded,
    string? PrivatePath,
    string? PublicPath,
    string? Fingerprint,
    string? Diagnostics)
{
    /// <summary>Builds a failure result.</summary>
    /// <param name="diagnostics">Redacted explanation.</param>
    /// <returns>The result.</returns>
    public static SshKeyOperationResult Failed(string diagnostics) =>
        new(false, null, null, null, diagnostics);
}

/// <summary>Creates and converts SSH keys.</summary>
public interface ISshKeyGenerator
{
    /// <summary>True when an <c>ssh-keygen</c> executable was located.</summary>
    bool HasSshKeygen { get; }

    /// <summary>Locates <c>ssh-keygen</c> once.</summary>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>A task that completes when the search has finished.</returns>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Creates a key pair.</summary>
    /// <param name="path">Path of the private key to write. The public key gets <c>.pub</c>.</param>
    /// <param name="request">What to generate.</param>
    /// <param name="passphrase">Passphrase bytes, or empty for an unprotected key.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened.</returns>
    Task<SshKeyOperationResult> GenerateAsync(
        string path,
        SshKeyGenerationRequest request,
        ReadOnlyMemory<byte> passphrase,
        CancellationToken cancellationToken);

    /// <summary>Writes the public key next to an existing private key.</summary>
    /// <param name="privateKeyPath">Private key to derive from.</param>
    /// <param name="passphrase">Passphrase bytes, or empty when the key is unprotected.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened.</returns>
    Task<SshKeyOperationResult> DerivePublicKeyAsync(
        string privateKeyPath,
        ReadOnlyMemory<byte> passphrase,
        CancellationToken cancellationToken);

    /// <summary>Adds, changes or removes a key's passphrase.</summary>
    /// <param name="privateKeyPath">Key to modify.</param>
    /// <param name="oldPassphrase">Current passphrase, empty when there is none.</param>
    /// <param name="newPassphrase">New passphrase, empty to remove protection.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened.</returns>
    Task<SshKeyOperationResult> ChangePassphraseAsync(
        string privateKeyPath,
        ReadOnlyMemory<byte> oldPassphrase,
        ReadOnlyMemory<byte> newPassphrase,
        CancellationToken cancellationToken);
}

/// <summary>
/// Key creation and passphrase management, performed by <c>ssh-keygen</c>.
/// </summary>
/// <remarks>
/// This deliberately does not reimplement OpenSSH's key writing. <c>ssh-keygen</c> is the
/// reference implementation, it already sets the right file mode, and it is the only way to
/// create <c>sk-*</c> keys, which live in a hardware token. Reimplementing <c>bcrypt_pbkdf</c> to
/// write an encrypted container ourselves would add unaudited cryptography to an application
/// whose whole job is to handle other people's private keys.
///
/// When <c>ssh-keygen</c> is absent the operations report that plainly instead of falling back to
/// something weaker.
/// </remarks>
public sealed class SshKeyGenerator : ISshKeyGenerator
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly IProcessRunner _runner;
    private readonly IFilePermissionService _permissions;
    private readonly ISshToolLocator _locator;
    private string? _sshKeygenPath;

    /// <summary>Creates the generator.</summary>
    /// <param name="runner">Process runner.</param>
    /// <param name="permissions">Permission service, used to verify the written key.</param>
    /// <param name="locator">Locator for the OpenSSH tools.</param>
    public SshKeyGenerator(IProcessRunner runner, IFilePermissionService permissions, ISshToolLocator locator)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(locator);

        _runner = runner;
        _permissions = permissions;
        _locator = locator;
    }

    /// <inheritdoc/>
    public bool HasSshKeygen => _sshKeygenPath is not null;

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _sshKeygenPath ??= await _locator.LocateSshKeygenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<SshKeyOperationResult> GenerateAsync(
        string path,
        SshKeyGenerationRequest request,
        ReadOnlyMemory<byte> passphrase,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(request);

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_sshKeygenPath is null)
        {
            return SshKeyOperationResult.Failed("ssh-keygen was not found");
        }

        if (File.Exists(path))
        {
            return SshKeyOperationResult.Failed("refusing to overwrite an existing key");
        }

        var typeName = TypeName(request.Algorithm);
        if (typeName is null)
        {
            return SshKeyOperationResult.Failed("unsupported algorithm");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var arguments = new List<string> { "-t", typeName, "-f", path, "-C", request.Comment, "-q" };

        if (request.BitLength is { } bits && request.Algorithm is SshKeyAlgorithm.Rsa or SshKeyAlgorithm.Ecdsa)
        {
            arguments.AddRange(["-b", bits.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        // ssh-keygen takes the passphrase as an argument. It is visible to a local process
        // listing for the lifetime of the call, which is why the UI warns before using it.
        arguments.AddRange(["-N", PassphraseArgument(passphrase)]);

        if (!passphrase.IsEmpty)
        {
            arguments.AddRange(["-a", request.KdfRounds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        var result = await _runner
            .RunAsync(_sshKeygenPath, arguments, directory, Timeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return SshKeyOperationResult.Failed(result.StandardError.Trim());
        }

        await _permissions.HardenAsync(path, cancellationToken).ConfigureAwait(false);

        var publicPath = path + ".pub";
        var fingerprint = SshKeyReader.TryReadPublicKeyFile(publicPath, out var publicKey) && publicKey is not null
            ? publicKey.FingerprintSha256
            : null;

        return new SshKeyOperationResult(true, path, publicPath, fingerprint, null);
    }

    /// <inheritdoc/>
    public async Task<SshKeyOperationResult> DerivePublicKeyAsync(
        string privateKeyPath,
        ReadOnlyMemory<byte> passphrase,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPath);

        // An unencrypted container already carries its public half, so no tool is needed.
        if (passphrase.IsEmpty
            && SshKeyReader.TryReadPrivateKeyFile(privateKeyPath, out var info)
            && info is { IsEncrypted: false, PublicKey: not null })
        {
            var target = privateKeyPath + ".pub";
            await File.WriteAllTextAsync(target, info.PublicKey.ToOpenSshLine() + "\n", cancellationToken)
                .ConfigureAwait(false);

            return new SshKeyOperationResult(true, privateKeyPath, target, info.PublicKey.FingerprintSha256, null);
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_sshKeygenPath is null)
        {
            return SshKeyOperationResult.Failed("ssh-keygen was not found");
        }

        var result = await _runner
            .RunAsync(_sshKeygenPath, ["-y", "-P", PassphraseArgument(passphrase), "-f", privateKeyPath],
                null, Timeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return SshKeyOperationResult.Failed(result.StandardError.Trim());
        }

        var publicPath = privateKeyPath + ".pub";
        await File.WriteAllTextAsync(publicPath, result.StandardOutput.Trim() + "\n", cancellationToken)
            .ConfigureAwait(false);

        var fingerprint = SshPublicKeyReader.TryParseOpenSshLine(result.StandardOutput, out var parsed)
            ? parsed!.FingerprintSha256
            : null;

        return new SshKeyOperationResult(true, privateKeyPath, publicPath, fingerprint, null);
    }

    /// <inheritdoc/>
    public async Task<SshKeyOperationResult> ChangePassphraseAsync(
        string privateKeyPath,
        ReadOnlyMemory<byte> oldPassphrase,
        ReadOnlyMemory<byte> newPassphrase,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPath);

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_sshKeygenPath is null)
        {
            return SshKeyOperationResult.Failed("ssh-keygen was not found");
        }

        var result = await _runner
            .RunAsync(
                _sshKeygenPath,
                [
                    "-p",
                    "-P", PassphraseArgument(oldPassphrase),
                    "-N", PassphraseArgument(newPassphrase),
                    "-f", privateKeyPath,
                ],
                null,
                Timeout,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return SshKeyOperationResult.Failed(result.StandardError.Trim());
        }

        await _permissions.HardenAsync(privateKeyPath, cancellationToken).ConfigureAwait(false);
        return new SshKeyOperationResult(true, privateKeyPath, null, null, null);
    }

    private static string PassphraseArgument(ReadOnlyMemory<byte> passphrase) =>
        passphrase.IsEmpty ? string.Empty : System.Text.Encoding.UTF8.GetString(passphrase.Span);

    private static string? TypeName(SshKeyAlgorithm algorithm) => algorithm switch
    {
        SshKeyAlgorithm.Ed25519 => "ed25519",
        SshKeyAlgorithm.Ed25519Sk => "ed25519-sk",
        SshKeyAlgorithm.Rsa => "rsa",
        SshKeyAlgorithm.Ecdsa => "ecdsa",
        SshKeyAlgorithm.EcdsaSk => "ecdsa-sk",
        _ => null,
    };
}
