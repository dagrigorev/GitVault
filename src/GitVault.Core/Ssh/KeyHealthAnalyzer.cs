using GitVault.Core.Models;

namespace GitVault.Core.Ssh;

/// <summary>
/// Turns a parsed key into actionable findings. Every code here has a matching
/// <c>Warning_&lt;code&gt;_Title</c> and <c>Warning_&lt;code&gt;_Body</c> resource.
/// </summary>
public static class KeyHealthAnalyzer
{
    /// <summary>Private key readable by accounts other than the owner.</summary>
    public const string WorldReadableCode = "KeyWorldReadable";

    /// <summary>RSA key below the currently recommended size.</summary>
    public const string RsaTooShortCode = "RsaTooShort";

    /// <summary>DSA key, which current OpenSSH refuses by default.</summary>
    public const string DsaDeprecatedCode = "DsaDeprecated";

    /// <summary>Private key stored without a passphrase.</summary>
    public const string NoPassphraseCode = "KeyNoPassphrase";

    /// <summary>Public key with no matching private key.</summary>
    public const string OrphanedPublicKeyCode = "OrphanedPublicKey";

    /// <summary>Private key with no matching public key file.</summary>
    public const string MissingPublicKeyCode = "MissingPublicKey";

    /// <summary>Container's own integrity check failed.</summary>
    public const string IntegrityCheckFailedCode = "KeyIntegrityFailed";

    /// <summary>Minimum RSA modulus size GitVault considers acceptable.</summary>
    public const int MinimumRsaBits = 3072;

    /// <summary>Analyses one key.</summary>
    /// <param name="key">The key to inspect.</param>
    /// <param name="integrityIsValid">Container integrity result, when the format has one.</param>
    /// <returns>Findings, most severe first.</returns>
    public static IReadOnlyList<KeyWarning> Analyze(SshKey key, bool? integrityIsValid = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        var subject = key.PrivatePath ?? key.PublicPath ?? key.FingerprintSha256;
        var warnings = new List<KeyWarning>();

        if (key.Permissions is { IsWorldReadable: true } or { IsGroupReadable: true })
        {
            // OpenSSH refuses such a key outright, so this is the one finding that stops work.
            warnings.Add(new KeyWarning(WorldReadableCode, WarningSeverity.High, subject, IsAutoFixable: true));
        }

        if (key.Algorithm == SshKeyAlgorithm.Dsa)
        {
            warnings.Add(new KeyWarning(DsaDeprecatedCode, WarningSeverity.High, subject));
        }

        if (key.Algorithm == SshKeyAlgorithm.Rsa && key.BitLength is { } bits && bits < MinimumRsaBits)
        {
            warnings.Add(new KeyWarning(RsaTooShortCode, WarningSeverity.Medium, subject));
        }

        if (key.PrivatePath is not null && !key.IsEncrypted && !key.IsHardwareBacked)
        {
            warnings.Add(new KeyWarning(NoPassphraseCode, WarningSeverity.Medium, subject));
        }

        if (key.PrivatePath is null && key.PublicPath is not null && !key.IsAgentOnly)
        {
            warnings.Add(new KeyWarning(OrphanedPublicKeyCode, WarningSeverity.Low, subject));
        }

        if (key.PrivatePath is not null && key.PublicPath is null)
        {
            warnings.Add(new KeyWarning(MissingPublicKeyCode, WarningSeverity.Low, subject, IsAutoFixable: !key.IsEncrypted));
        }

        if (integrityIsValid == false)
        {
            warnings.Add(new KeyWarning(IntegrityCheckFailedCode, WarningSeverity.High, subject));
        }

        return [.. warnings.OrderByDescending(w => w.Severity)];
    }
}
