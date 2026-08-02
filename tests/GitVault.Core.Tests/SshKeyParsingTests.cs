using FluentAssertions;
using GitVault.Core.Models;
using GitVault.Core.Ssh;
using Xunit;

namespace GitVault.Core.Tests;

/// <summary>
/// Every assertion here is against fixtures produced by OpenSSH itself, so the tests compare
/// GitVault with the implementation it has to interoperate with rather than with itself.
/// </summary>
public sealed class SshKeyParsingTests
{
    public static TheoryData<string> KeyNames => SshFixtures.AllKeyNames();

    [Theory]
    [MemberData(nameof(KeyNames))]
    public void Public_key_fingerprints_match_ssh_keygen(string name)
    {
        var expected = SshFixtures.Expected[name];

        SshPublicKeyReader.TryParseFile(SshFixtures.Text(name + ".pub"), out var key).Should().BeTrue();

        key!.FingerprintSha256.Should().Be(expected.Sha256, "ssh-keygen -lf is the reference");
        key.FingerprintMd5.Should().Be(expected.Md5, "ssh-keygen -l -E md5 is the reference");
        key.BitLength.Should().Be(expected.Bits);
    }

    [Theory]
    [InlineData("ed25519_plain", SshKeyAlgorithm.Ed25519, "ssh-ed25519")]
    [InlineData("rsa2048_plain", SshKeyAlgorithm.Rsa, "ssh-rsa")]
    [InlineData("rsa4096_plain", SshKeyAlgorithm.Rsa, "ssh-rsa")]
    [InlineData("ecdsa256_plain", SshKeyAlgorithm.Ecdsa, "ecdsa-sha2-nistp256")]
    [InlineData("ecdsa384_plain", SshKeyAlgorithm.Ecdsa, "ecdsa-sha2-nistp384")]
    [InlineData("ecdsa521_plain", SshKeyAlgorithm.Ecdsa, "ecdsa-sha2-nistp521")]
    [InlineData("dsa1024_plain", SshKeyAlgorithm.Dsa, "ssh-dss")]
    public void Algorithms_are_identified_from_the_blob(string name, SshKeyAlgorithm algorithm, string keyType)
    {
        SshPublicKeyReader.TryParseFile(SshFixtures.Text(name + ".pub"), out var key).Should().BeTrue();

        key!.Algorithm.Should().Be(algorithm);
        key.KeyType.Should().Be(keyType);
    }

    [Fact]
    public void Public_key_comments_survive()
    {
        SshPublicKeyReader.TryParseFile(SshFixtures.Text("ed25519_plain.pub"), out var key).Should().BeTrue();

        key!.Comment.Should().Be("ada@example.com");
    }

    [Theory]
    [MemberData(nameof(KeyNames))]
    public void Private_containers_fingerprint_identically_to_their_public_half(string name)
    {
        if (name == "orphan")
        {
            return; // A public-only fixture by design.
        }

        var expected = SshFixtures.Expected[name];

        SshKeyReader.TryReadPrivateKeyFile(SshFixtures.Path(name), out var info).Should().BeTrue();

        if (info!.IsEncrypted)
        {
            // Encrypted PEM and PKCS#8 hide the public half; the OpenSSH container does not.
            if (info.Format == SshKeyFormat.OpenSsh)
            {
                info.PublicKey.Should().NotBeNull();
                info.PublicKey!.FingerprintSha256.Should().Be(expected.Sha256);
            }

            return;
        }

        info.PublicKey.Should().NotBeNull($"{name} is unencrypted, so its public half is recoverable");
        info.PublicKey!.FingerprintSha256.Should().Be(expected.Sha256);
        info.PublicKey.FingerprintMd5.Should().Be(expected.Md5);
        info.PublicKey.BitLength.Should().Be(expected.Bits);
    }

    [Theory]
    [InlineData("ed25519_plain", SshKeyFormat.OpenSsh, false)]
    [InlineData("ed25519_encrypted", SshKeyFormat.OpenSsh, true)]
    [InlineData("rsa2048_encrypted", SshKeyFormat.OpenSsh, true)]
    [InlineData("rsa2048_pem", SshKeyFormat.Pem, false)]
    [InlineData("rsa2048_pem_locked", SshKeyFormat.Pem, true)]
    [InlineData("rsa2048_pkcs8", SshKeyFormat.Pkcs8, false)]
    // ssh-keygen silently ignores "-m PKCS8" for ed25519 and writes its own container anyway,
    // so this fixture is an OpenSSH file despite its name. Detecting the real format from the
    // content rather than trusting the extension is exactly the point.
    [InlineData("ed25519_pkcs8", SshKeyFormat.OpenSsh, false)]
    public void Container_format_and_encryption_state_are_detected(
        string name,
        SshKeyFormat format,
        bool encrypted)
    {
        SshKeyReader.TryReadPrivateKeyFile(SshFixtures.Path(name), out var info).Should().BeTrue();

        info!.Format.Should().Be(format);
        info.IsEncrypted.Should().Be(encrypted);
    }

    [Fact]
    public void Bcrypt_rounds_are_read_without_decrypting()
    {
        SshKeyReader.TryReadPrivateKeyFile(SshFixtures.Path("ed25519_encrypted"), out var info).Should().BeTrue();

        info!.IsEncrypted.Should().BeTrue();
        info.KdfRounds.Should().BeGreaterThan(0, "the bcrypt work factor is stored in the clear");
        info.PublicKey.Should().NotBeNull("the public half of an OpenSSH container is never encrypted");
    }

    [Fact]
    public void An_unencrypted_container_yields_its_comment()
    {
        SshKeyReader.TryReadPrivateKeyFile(SshFixtures.Path("ed25519_plain"), out var info).Should().BeTrue();

        info!.Comment.Should().Be("ada@example.com");
    }

    [Fact]
    public void An_encrypted_container_hides_its_comment()
    {
        SshKeyReader.TryReadPrivateKeyFile(SshFixtures.Path("ed25519_encrypted"), out var info).Should().BeTrue();

        info!.Comment.Should().BeNull("the comment lives inside the encrypted section");
    }

    [Theory]
    [InlineData("malformed_truncated.key")]
    [InlineData("malformed_not_a_key.key")]
    public void Malformed_private_keys_are_rejected_without_throwing(string name)
    {
        var act = () => SshKeyReader.TryReadPrivateKeyFile(SshFixtures.Path(name), out _);

        act.Should().NotThrow();
        SshKeyReader.TryReadPrivateKeyFile(SshFixtures.Path(name), out var info).Should().BeFalse();
        info.Should().BeNull();
    }

    [Fact]
    public void A_public_key_line_with_broken_base64_is_rejected()
    {
        SshPublicKeyReader
            .TryParseFile(SshFixtures.Text("malformed_bad_base64.pub"), out var key)
            .Should().BeFalse();

        key.Should().BeNull();
    }

    [Fact]
    public void A_declared_type_that_disagrees_with_the_blob_is_rejected()
    {
        var line = SshFixtures.Text("ed25519_plain.pub").Trim();
        var tampered = "ssh-rsa" + line["ssh-ed25519".Length..];

        SshPublicKeyReader.TryParseOpenSshLine(tampered, out var key).Should().BeFalse();
        key.Should().BeNull();
    }

    [Fact]
    public void An_authorized_keys_options_prefix_is_tolerated()
    {
        var line = "no-pty,command=\"/bin/true\" " + SshFixtures.Text("ed25519_plain.pub").Trim();

        SshPublicKeyReader.TryParseOpenSshLine(line, out var key).Should().BeTrue();
        key!.FingerprintSha256.Should().Be(SshFixtures.Expected["ed25519_plain"].Sha256);
    }

    [Fact]
    public void Rfc4716_public_keys_are_read()
    {
        SshPublicKeyReader
            .TryParseFile(SshFixtures.Text("ed25519_plain.rfc4716.pub"), out var key)
            .Should().BeTrue();

        key!.FingerprintSha256.Should().Be(SshFixtures.Expected["ed25519_plain"].Sha256);
    }

    [Fact]
    public void Rfc4716_output_round_trips()
    {
        SshPublicKeyReader.TryParseFile(SshFixtures.Text("rsa4096_plain.pub"), out var original).Should().BeTrue();

        SshPublicKeyReader.TryParseRfc4716(original!.ToRfc4716(), out var reparsed).Should().BeTrue();

        reparsed!.FingerprintSha256.Should().Be(original.FingerprintSha256);
        reparsed.Comment.Should().Be(original.Comment);
    }

    [Fact]
    public void OpenSsh_line_output_round_trips()
    {
        SshPublicKeyReader.TryParseFile(SshFixtures.Text("ecdsa521_plain.pub"), out var original).Should().BeTrue();

        SshPublicKeyReader.TryParseOpenSshLine(original!.ToOpenSshLine(), out var reparsed).Should().BeTrue();

        reparsed!.ToOpenSshLine().Should().Be(original.ToOpenSshLine());
    }

    [Theory]
    [InlineData("rsa2048_v2.ppk", 2, SshKeyFormat.Ppk2)]
    [InlineData("rsa2048_v3.ppk", 3, SshKeyFormat.Ppk3)]
    public void Putty_files_are_parsed(string name, int version, SshKeyFormat format)
    {
        PuttyKeyFile.TryParse(SshFixtures.Text(name), out var container).Should().BeTrue();

        container!.Version.Should().Be(version);
        container.Format.Should().Be(format);
        container.KeyType.Should().Be("ssh-rsa");
        container.IsEncrypted.Should().BeFalse();
        container.Comment.Should().Be("rsa2048@example.com");
        container.PublicKey.FingerprintSha256.Should().Be(SshFixtures.Expected["rsa2048_pem"].Sha256);
        container.MacIsValid.Should().BeTrue();
    }

    [Fact]
    public void A_putty_file_with_a_wrong_mac_is_reported_as_such()
    {
        PuttyKeyFile.TryParse(SshFixtures.Text("rsa2048_v2_badmac.ppk"), out var container).Should().BeTrue();

        container!.MacIsValid.Should().BeFalse("the file's own integrity claim does not hold");
    }

    [Fact]
    public void Putty_v3_argon2_parameters_are_read()
    {
        PuttyKeyFile.TryParse(SshFixtures.Text("rsa2048_v3.ppk"), out var container).Should().BeTrue();

        container!.Argon2Parameters.Should().NotBeNull();
        container.Argon2Parameters!.Flavour.Should().Be("Argon2id");
        container.Argon2Parameters.MemoryKib.Should().Be(8192);
        container.Argon2Parameters.Passes.Should().Be(13);
    }

    [Fact]
    public void A_putty_file_reaches_the_generic_reader_too()
    {
        SshKeyReader.TryReadPrivateKeyFile(SshFixtures.Path("rsa2048_v2.ppk"), out var info).Should().BeTrue();

        info!.Format.Should().Be(SshKeyFormat.Ppk2);
        info.IntegrityIsValid.Should().BeTrue();
        info.PublicKey!.Algorithm.Should().Be(SshKeyAlgorithm.Rsa);
    }

    [Theory]
    [InlineData(new byte[] { 0x00 }, 0)]
    [InlineData(new byte[] { 0x01 }, 1)]
    [InlineData(new byte[] { 0xFF }, 8)]
    [InlineData(new byte[] { 0x00, 0x80, 0x00 }, 16)]
    [InlineData(new byte[] { 0x01, 0x00, 0x00 }, 17)]
    public void Bit_length_ignores_leading_zero_padding(byte[] magnitude, int expected) =>
        SshPublicKeyReader.BitLength(magnitude).Should().Be(expected);
}

public sealed class SshWireTests
{
    [Fact]
    public void Strings_round_trip()
    {
        var writer = new SshWireWriter();
        writer.WriteText("ssh-ed25519");
        writer.WriteUInt32(42);
        writer.WriteString([1, 2, 3]);

        var reader = new SshWireReader(writer.ToArray());
        reader.ReadText().Should().Be("ssh-ed25519");
        reader.ReadUInt32().Should().Be(42);
        reader.ReadString().ToArray().Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        reader.Remaining.Should().Be(0);
    }

    [Fact]
    public void Mpints_get_sign_padding_only_when_needed()
    {
        var writer = new SshWireWriter();
        writer.WriteMpint([0x80, 0x01]);
        writer.WriteMpint([0x7F, 0x01]);

        var encoded = writer.ToArray();

        // 0x80 has its high bit set, so it is padded: length 3, bytes 00 80 01.
        encoded[3].Should().Be(3);
        encoded[4].Should().Be(0);

        // 0x7F does not, so it is stored as-is: length 2, bytes 7F 01.
        encoded[10].Should().Be(2);
        encoded[11].Should().Be(0x7F);
    }

    [Fact]
    public void Leading_zeros_are_stripped_from_mpints()
    {
        var writer = new SshWireWriter();
        writer.WriteMpint([0x00, 0x00, 0x05]);

        var reader = new SshWireReader(writer.ToArray());
        reader.ReadMpint().ToArray().Should().BeEquivalentTo(new byte[] { 0x05 });
    }

    [Fact]
    public void Reading_past_the_end_throws_a_wire_exception()
    {
        var reader = new SshWireReader(new byte[] { 0, 0, 0, 8, 1, 2 });

        var act = () =>
        {
            var r = new SshWireReader(new byte[] { 0, 0, 0, 8, 1, 2 });
            r.ReadString();
        };

        act.Should().Throw<SshWireException>();
        reader.Remaining.Should().Be(6);
    }

    [Fact]
    public void An_empty_mpint_encodes_as_a_zero_length_string()
    {
        var writer = new SshWireWriter();
        writer.WriteMpint([0x00, 0x00]);

        writer.ToArray().Should().BeEquivalentTo(new byte[] { 0, 0, 0, 0 });
    }
}
