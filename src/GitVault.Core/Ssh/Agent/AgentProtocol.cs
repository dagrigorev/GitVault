using System.Buffers.Binary;

namespace GitVault.Core.Ssh.Agent;

/// <summary>
/// Message numbers and framing for the SSH agent protocol
/// (<c>draft-miller-ssh-agent</c>, the protocol OpenSSH's agent actually speaks).
/// </summary>
public static class AgentProtocol
{
    /// <summary>Generic failure reply.</summary>
    public const byte Failure = 5;

    /// <summary>Generic success reply.</summary>
    public const byte Success = 6;

    /// <summary>Request the list of held identities.</summary>
    public const byte RequestIdentities = 11;

    /// <summary>Reply carrying the held identities.</summary>
    public const byte IdentitiesAnswer = 12;

    /// <summary>Request a signature.</summary>
    public const byte SignRequest = 13;

    /// <summary>Reply carrying a signature.</summary>
    public const byte SignResponse = 14;

    /// <summary>Add a private key.</summary>
    public const byte AddIdentity = 17;

    /// <summary>Remove one identity by public key blob.</summary>
    public const byte RemoveIdentity = 18;

    /// <summary>Remove every identity.</summary>
    public const byte RemoveAllIdentities = 19;

    /// <summary>Lock the agent with a passphrase.</summary>
    public const byte Lock = 22;

    /// <summary>Unlock the agent.</summary>
    public const byte Unlock = 23;

    /// <summary>Add a private key with constraints.</summary>
    public const byte AddIdConstrained = 25;

    /// <summary>Protocol extension request.</summary>
    public const byte Extension = 27;

    /// <summary>Reply when an extension is not understood.</summary>
    public const byte ExtensionFailure = 28;

    /// <summary>Constraint: the key expires after a number of seconds.</summary>
    public const byte ConstrainLifetime = 1;

    /// <summary>Constraint: each use must be confirmed by the user.</summary>
    public const byte ConstrainConfirm = 2;

    /// <summary>
    /// Largest message GitVault will read. An agent holding a few keys sends a few kilobytes;
    /// refusing anything larger stops a hostile or broken endpoint from exhausting memory.
    /// </summary>
    public const int MaxMessageLength = 256 * 1024;

    /// <summary>Wraps a payload in the protocol's length prefix.</summary>
    /// <param name="payload">Message payload, starting with the message number.</param>
    /// <returns>The framed message.</returns>
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var framed = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)payload.Length);
        payload.CopyTo(framed.AsSpan(4));
        return framed;
    }

    /// <summary>Builds a payload consisting of a single message number.</summary>
    /// <param name="messageNumber">The message number.</param>
    /// <returns>The framed message.</returns>
    public static byte[] FrameSimple(byte messageNumber) => Frame([messageNumber]);

    /// <summary>Parses an <see cref="IdentitiesAnswer"/> payload.</summary>
    /// <param name="payload">Reply payload, starting with the message number.</param>
    /// <returns>The identities the agent reported.</returns>
    /// <exception cref="SshWireException">The reply was not a well-formed identities answer.</exception>
    public static IReadOnlyList<Models.AgentKeyEntry> ParseIdentitiesAnswer(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload[0] != IdentitiesAnswer)
        {
            throw new SshWireException(
                $"Expected message {IdentitiesAnswer}, got {(payload.Length == 0 ? "nothing" : payload[0].ToString())}");
        }

        var reader = new SshWireReader(payload[1..]);
        var count = reader.ReadUInt32();

        if (count > 1024)
        {
            throw new SshWireException($"Agent reported an implausible identity count of {count}");
        }

        var entries = new List<Models.AgentKeyEntry>((int)count);
        for (var i = 0; i < count; i++)
        {
            var blob = reader.ReadString().ToArray();
            var comment = reader.ReadText();

            var algorithm = Models.SshKeyAlgorithm.Unknown;
            try
            {
                algorithm = SshPublicKeyReader.FromBlob(blob).Algorithm;
            }
            catch (SshWireException)
            {
                // An agent may hold a key type we do not model; it still has a fingerprint.
            }

            entries.Add(new Models.AgentKeyEntry(blob, comment, SshFingerprint.Sha256(blob), algorithm));
        }

        return entries;
    }

    /// <summary>Builds a <see cref="RemoveIdentity"/> request.</summary>
    /// <param name="publicKeyBlob">Blob of the key to remove.</param>
    /// <returns>The framed message.</returns>
    public static byte[] BuildRemoveIdentity(ReadOnlySpan<byte> publicKeyBlob)
    {
        var writer = new SshWireWriter();
        writer.WriteByte(RemoveIdentity);
        writer.WriteString(publicKeyBlob);
        return Frame(writer.ToArray());
    }

    /// <summary>Builds a <see cref="Lock"/> or <see cref="Unlock"/> request.</summary>
    /// <param name="passphrase">Passphrase bytes.</param>
    /// <param name="lock">True to lock, false to unlock.</param>
    /// <returns>The framed message.</returns>
    public static byte[] BuildLock(ReadOnlySpan<byte> passphrase, bool @lock)
    {
        var writer = new SshWireWriter();
        writer.WriteByte(@lock ? Lock : Unlock);
        writer.WriteString(passphrase);
        return Frame(writer.ToArray());
    }

    /// <summary>
    /// Builds an add-identity request, using the constrained form when a lifetime or a
    /// confirmation requirement is asked for.
    /// </summary>
    /// <param name="privateKeyBlob">Private key in the agent's wire format, without framing.</param>
    /// <param name="comment">Comment to store with the key.</param>
    /// <param name="lifetimeSeconds">Optional lifetime constraint.</param>
    /// <param name="requireConfirmation">Whether each use must be confirmed.</param>
    /// <returns>The framed message.</returns>
    public static byte[] BuildAddIdentity(
        ReadOnlySpan<byte> privateKeyBlob,
        string comment,
        int? lifetimeSeconds,
        bool requireConfirmation)
    {
        var constrained = lifetimeSeconds is > 0 || requireConfirmation;

        var writer = new SshWireWriter();
        writer.WriteByte(constrained ? AddIdConstrained : AddIdentity);
        writer.WriteRaw(privateKeyBlob);
        writer.WriteText(comment ?? string.Empty);

        if (lifetimeSeconds is > 0)
        {
            writer.WriteByte(ConstrainLifetime);
            writer.WriteUInt32((uint)lifetimeSeconds.Value);
        }

        if (requireConfirmation)
        {
            writer.WriteByte(ConstrainConfirm);
        }

        return Frame(writer.ToArray());
    }

    /// <summary>Interprets a reply that is expected to be a bare success or failure.</summary>
    /// <param name="payload">Reply payload.</param>
    /// <returns><see langword="true"/> when the agent replied with success.</returns>
    public static bool IsSuccess(ReadOnlySpan<byte> payload) =>
        payload.Length > 0 && payload[0] == Success;
}
