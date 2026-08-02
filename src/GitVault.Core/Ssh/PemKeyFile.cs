using GitVault.Core.Models;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace GitVault.Core.Ssh;

/// <summary>What a PEM or PKCS#8 private key file declares.</summary>
/// <param name="Format">Which of the two containers it is.</param>
/// <param name="IsEncrypted">True when a passphrase is required.</param>
/// <param name="PublicKey">Public half, recoverable only when the file is unencrypted.</param>
/// <param name="Algorithm">Algorithm family, as far as the header reveals it.</param>
public sealed record PemKeyContainer(
    SshKeyFormat Format,
    bool IsEncrypted,
    SshPublicKey? PublicKey,
    SshKeyAlgorithm Algorithm);

/// <summary>
/// Reader for traditional PEM private keys (<c>BEGIN RSA/DSA/EC PRIVATE KEY</c>) and for PKCS#8,
/// encrypted or not.
/// </summary>
/// <remarks>
/// Encryption state is determined from the headers alone, so an encrypted key is classified
/// without ever attempting the passphrase. The public half is reconstructed only for unencrypted
/// files, which is what lets GitVault fingerprint a key whose <c>.pub</c> has been lost.
/// </remarks>
public static class PemKeyFile
{
    /// <summary>True when the text looks like a PEM or PKCS#8 private key.</summary>
    /// <param name="text">File contents.</param>
    /// <returns><see langword="true"/> when a recognised header is present.</returns>
    public static bool Matches(string text) =>
        text is not null
        && (text.Contains("-----BEGIN RSA PRIVATE KEY-----", StringComparison.Ordinal)
            || text.Contains("-----BEGIN DSA PRIVATE KEY-----", StringComparison.Ordinal)
            || text.Contains("-----BEGIN EC PRIVATE KEY-----", StringComparison.Ordinal)
            || text.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal)
            || text.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal));

    /// <summary>Parses the container.</summary>
    /// <param name="text">File contents.</param>
    /// <param name="container">The parsed container.</param>
    /// <returns><see langword="true"/> when the container was understood.</returns>
    public static bool TryParse(string text, out PemKeyContainer? container)
    {
        container = null;
        if (!Matches(text))
        {
            return false;
        }

        var isPkcs8 = text.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal)
                      || text.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal);

        var format = isPkcs8 ? SshKeyFormat.Pkcs8 : SshKeyFormat.Pem;

        // Traditional PEM marks encryption with Proc-Type/DEK-Info headers; PKCS#8 uses a
        // different BEGIN line entirely. Neither needs the passphrase to detect.
        var isEncrypted = text.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal)
                          || text.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal)
                          || text.Contains("DEK-Info:", StringComparison.Ordinal);

        var declaredAlgorithm = DeclaredAlgorithm(text);

        if (isEncrypted)
        {
            container = new PemKeyContainer(format, true, null, declaredAlgorithm);
            return true;
        }

        try
        {
            using var reader = new StringReader(text);
            var parsed = new PemReader(reader).ReadObject();

            var publicKey = parsed switch
            {
                AsymmetricCipherKeyPair pair => ToPublicKey(pair.Public),
                AsymmetricKeyParameter { IsPrivate: true } priv => ToPublicKey(DerivePublic(priv)),
                AsymmetricKeyParameter pub => ToPublicKey(pub),
                _ => null,
            };

            container = new PemKeyContainer(
                format,
                false,
                publicKey,
                publicKey?.Algorithm ?? declaredAlgorithm);

            return true;
        }
        catch (PemException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidCipherTextException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // BouncyCastle raises this for structurally invalid key material.
            return false;
        }
    }

    private static SshKeyAlgorithm DeclaredAlgorithm(string text) => text switch
    {
        _ when text.Contains("BEGIN RSA PRIVATE KEY", StringComparison.Ordinal) => SshKeyAlgorithm.Rsa,
        _ when text.Contains("BEGIN DSA PRIVATE KEY", StringComparison.Ordinal) => SshKeyAlgorithm.Dsa,
        _ when text.Contains("BEGIN EC PRIVATE KEY", StringComparison.Ordinal) => SshKeyAlgorithm.Ecdsa,
        _ => SshKeyAlgorithm.Unknown,
    };

    private static AsymmetricKeyParameter DerivePublic(AsymmetricKeyParameter privateKey) => privateKey switch
    {
        Ed25519PrivateKeyParameters ed => ed.GeneratePublicKey(),
        _ => privateKey,
    };

    /// <summary>Converts a BouncyCastle public key into an SSH wire-format blob.</summary>
    /// <param name="key">Public key parameters.</param>
    /// <returns>The SSH public key, or null for an algorithm SSH has no encoding for.</returns>
    internal static SshPublicKey? ToPublicKey(AsymmetricKeyParameter? key)
    {
        var writer = new SshWireWriter();

        switch (key)
        {
            case RsaKeyParameters { IsPrivate: false } rsa:
                writer.WriteText("ssh-rsa");
                writer.WriteMpint(rsa.Exponent.ToByteArrayUnsigned());
                writer.WriteMpint(rsa.Modulus.ToByteArrayUnsigned());
                break;

            case RsaPrivateCrtKeyParameters rsaPrivate:
                writer.WriteText("ssh-rsa");
                writer.WriteMpint(rsaPrivate.PublicExponent.ToByteArrayUnsigned());
                writer.WriteMpint(rsaPrivate.Modulus.ToByteArrayUnsigned());
                break;

            case DsaPublicKeyParameters dsa:
                writer.WriteText("ssh-dss");
                writer.WriteMpint(dsa.Parameters.P.ToByteArrayUnsigned());
                writer.WriteMpint(dsa.Parameters.Q.ToByteArrayUnsigned());
                writer.WriteMpint(dsa.Parameters.G.ToByteArrayUnsigned());
                writer.WriteMpint(dsa.Y.ToByteArrayUnsigned());
                break;

            case DsaPrivateKeyParameters dsaPrivate:
            {
                var parameters = dsaPrivate.Parameters;
                var y = parameters.G.ModPow(dsaPrivate.X, parameters.P);
                writer.WriteText("ssh-dss");
                writer.WriteMpint(parameters.P.ToByteArrayUnsigned());
                writer.WriteMpint(parameters.Q.ToByteArrayUnsigned());
                writer.WriteMpint(parameters.G.ToByteArrayUnsigned());
                writer.WriteMpint(y.ToByteArrayUnsigned());
                break;
            }

            case ECPublicKeyParameters ec:
            {
                var curve = SshCurveName(ec.Parameters.Curve.FieldSize);
                if (curve is null)
                {
                    return null;
                }

                writer.WriteText("ecdsa-sha2-" + curve);
                writer.WriteText(curve);
                writer.WriteString(ec.Q.GetEncoded(false));
                break;
            }

            case ECPrivateKeyParameters ecPrivate:
            {
                var curve = SshCurveName(ecPrivate.Parameters.Curve.FieldSize);
                if (curve is null)
                {
                    return null;
                }

                var q = ecPrivate.Parameters.G.Multiply(ecPrivate.D).Normalize();
                writer.WriteText("ecdsa-sha2-" + curve);
                writer.WriteText(curve);
                writer.WriteString(q.GetEncoded(false));
                break;
            }

            case Ed25519PublicKeyParameters ed:
                writer.WriteText("ssh-ed25519");
                writer.WriteString(ed.GetEncoded());
                break;

            case Ed25519PrivateKeyParameters edPrivate:
                writer.WriteText("ssh-ed25519");
                writer.WriteString(edPrivate.GeneratePublicKey().GetEncoded());
                break;

            default:
                return null;
        }

        return SshPublicKeyReader.FromBlob(writer.ToArray());
    }

    private static string? SshCurveName(int fieldSize) => fieldSize switch
    {
        256 => "nistp256",
        384 => "nistp384",
        521 => "nistp521",
        _ => null,
    };

    /// <summary>
    /// Reads a PKCS#8 <c>SubjectPublicKeyInfo</c> or <c>PrivateKeyInfo</c> from DER, which is
    /// what an unencrypted <c>BEGIN PRIVATE KEY</c> body contains.
    /// </summary>
    /// <param name="der">DER bytes.</param>
    /// <returns>The public key, or null when it could not be recovered.</returns>
    internal static SshPublicKey? FromPkcs8(byte[] der)
    {
        try
        {
            var info = PrivateKeyInfo.GetInstance(der);
            return ToPublicKey(PrivateKeyFactory.CreateKey(info));
        }
        catch (ArgumentException)
        {
            // Not a PrivateKeyInfo; try the public counterpart before giving up.
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            var info = SubjectPublicKeyInfo.GetInstance(der);
            return ToPublicKey(PublicKeyFactory.CreateKey(info));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
