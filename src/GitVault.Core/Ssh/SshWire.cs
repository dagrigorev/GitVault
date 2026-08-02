using System.Buffers.Binary;
using System.Text;

namespace GitVault.Core.Ssh;

/// <summary>Raised when a buffer does not follow the SSH wire encoding.</summary>
public sealed class SshWireException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was wrong with the buffer.</param>
    public SshWireException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SshWireException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was wrong with the buffer.</param>
    /// <param name="innerException">Underlying failure.</param>
    public SshWireException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Sequential reader for the SSH binary encoding used by public key blobs, private key
/// containers and the agent protocol: big-endian lengths, length-prefixed strings, mpints.
/// </summary>
public ref struct SshWireReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    /// <summary>Creates a reader over a buffer.</summary>
    /// <param name="buffer">Bytes to read.</param>
    public SshWireReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>Bytes not yet consumed.</summary>
    public readonly int Remaining => _buffer.Length - _position;

    /// <summary>Current offset.</summary>
    public readonly int Position => _position;

    /// <summary>Reads one byte.</summary>
    /// <returns>The byte.</returns>
    public byte ReadByte()
    {
        Require(1);
        return _buffer[_position++];
    }

    /// <summary>Reads a big-endian 32-bit unsigned integer.</summary>
    /// <returns>The value.</returns>
    public uint ReadUInt32()
    {
        Require(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(_buffer[_position..]);
        _position += 4;
        return value;
    }

    /// <summary>Reads a length-prefixed byte string.</summary>
    /// <returns>The bytes, as a slice of the source buffer.</returns>
    public ReadOnlySpan<byte> ReadString()
    {
        var length = ReadUInt32();
        if (length > int.MaxValue)
        {
            throw new SshWireException("String length exceeds the addressable range");
        }

        Require((int)length);
        var slice = _buffer.Slice(_position, (int)length);
        _position += (int)length;
        return slice;
    }

    /// <summary>Reads a length-prefixed string and decodes it as UTF-8.</summary>
    /// <returns>The decoded text.</returns>
    public string ReadText() => Encoding.UTF8.GetString(ReadString());

    /// <summary>Reads a multiple-precision integer, keeping its canonical big-endian bytes.</summary>
    /// <returns>The magnitude, without the sign-padding zero byte.</returns>
    public ReadOnlySpan<byte> ReadMpint()
    {
        var value = ReadString();

        // A leading zero is sign padding when the next byte has its high bit set.
        return value.Length > 1 && value[0] == 0 ? value[1..] : value;
    }

    /// <summary>Reads the rest of the buffer.</summary>
    /// <returns>The remaining bytes.</returns>
    public ReadOnlySpan<byte> ReadRemaining()
    {
        var slice = _buffer[_position..];
        _position = _buffer.Length;
        return slice;
    }

    /// <summary>Skips a length-prefixed string without materialising it.</summary>
    public void SkipString() => ReadString();

    private readonly void Require(int count)
    {
        if (Remaining < count)
        {
            throw new SshWireException(
                $"Buffer ended after {_buffer.Length} bytes while {count} more were required");
        }
    }
}

/// <summary>Builder for the SSH binary encoding.</summary>
public sealed class SshWireWriter
{
    private readonly MemoryStream _stream = new();

    /// <summary>Appends one byte.</summary>
    /// <param name="value">Byte to append.</param>
    public void WriteByte(byte value) => _stream.WriteByte(value);

    /// <summary>Appends a big-endian 32-bit unsigned integer.</summary>
    /// <param name="value">Value to append.</param>
    public void WriteUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    /// <summary>Appends a length-prefixed byte string.</summary>
    /// <param name="value">Bytes to append.</param>
    public void WriteString(ReadOnlySpan<byte> value)
    {
        WriteUInt32((uint)value.Length);
        _stream.Write(value);
    }

    /// <summary>Appends a length-prefixed UTF-8 string.</summary>
    /// <param name="value">Text to append.</param>
    public void WriteText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteString(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>Appends a multiple-precision integer, adding sign padding when required.</summary>
    /// <param name="magnitude">Big-endian magnitude.</param>
    public void WriteMpint(ReadOnlySpan<byte> magnitude)
    {
        var start = 0;
        while (start < magnitude.Length && magnitude[start] == 0)
        {
            start++;
        }

        var trimmed = magnitude[start..];
        if (trimmed.Length == 0)
        {
            WriteUInt32(0);
            return;
        }

        if ((trimmed[0] & 0x80) != 0)
        {
            WriteUInt32((uint)trimmed.Length + 1);
            _stream.WriteByte(0);
            _stream.Write(trimmed);
            return;
        }

        WriteString(trimmed);
    }

    /// <summary>Appends raw bytes with no length prefix.</summary>
    /// <param name="value">Bytes to append.</param>
    public void WriteRaw(ReadOnlySpan<byte> value) => _stream.Write(value);

    /// <summary>Produces the accumulated bytes.</summary>
    /// <returns>A copy of the buffer.</returns>
    public byte[] ToArray() => _stream.ToArray();
}
