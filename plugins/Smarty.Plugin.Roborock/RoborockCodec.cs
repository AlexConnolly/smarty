using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Smarty.Plugin.Roborock;

/// <summary>One frame on the wire, before or after the encrypted part.</summary>
internal sealed record RoborockFrame(uint Sequence, uint Random, uint Timestamp, ushort Protocol, byte[] Payload)
{
    /// <summary>A remote-procedure call to the vacuum.</summary>
    public const ushort RpcRequest = 101;

    /// <summary>Its answer — and also the unsolicited status pushes the vacuum sends while it works.</summary>
    public const ushort RpcResponse = 102;
}

/// <summary>
/// Roborock's wire format. Every message — over the cloud broker or over the local network — is the same
/// small binary header wrapped round an AES-encrypted JSON body, with a CRC32 on the end.
/// </summary>
/// <remarks>
/// The one thing worth knowing: the encryption key is not the device key. It is derived per message from the
/// timestamp in that message's own header, the device's local key, and a fixed salt — with the timestamp's hex
/// digits shuffled through a fixed permutation first. So a frame carries everything needed to decrypt itself,
/// and a frame whose timestamp is altered in flight simply won't decode.
/// </remarks>
internal static class RoborockCodec
{
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("TXdfu$jyZ#TZHsg4");
    private static readonly byte[] Version = Encoding.UTF8.GetBytes("1.0");

    /// <summary>The header, up to but not including the payload length.</summary>
    private const int HeaderBytes = 3 + 4 + 4 + 4 + 2;

    public static byte[] Encode(RoborockFrame frame, string localKey)
    {
        var body = Encrypt(frame.Payload, KeyFor(frame.Timestamp, localKey));
        var message = new byte[HeaderBytes + 2 + body.Length];
        var span = message.AsSpan();

        Version.CopyTo(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[3..], frame.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(span[7..], frame.Random);
        BinaryPrimitives.WriteUInt32BigEndian(span[11..], frame.Timestamp);
        BinaryPrimitives.WriteUInt16BigEndian(span[15..], frame.Protocol);
        BinaryPrimitives.WriteUInt16BigEndian(span[17..], (ushort)body.Length);
        body.CopyTo(span[19..]);

        var framed = new byte[message.Length + 4];
        message.CopyTo(framed, 0);
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(message.Length), Crc32.Of(message));
        return framed;
    }

    /// <summary>
    /// Every frame in a buffer. A single MQTT message usually holds one, but the local socket concatenates
    /// them, and a frame this build doesn't understand is skipped rather than allowed to derail the rest.
    /// </summary>
    public static IReadOnlyList<RoborockFrame> Decode(ReadOnlySpan<byte> data, string localKey)
    {
        var frames = new List<RoborockFrame>();
        int offset = 0;

        while (offset + HeaderBytes + 2 <= data.Length)
        {
            var header = data[offset..];
            if (!header[..3].SequenceEqual(Version))
            {
                // Not a frame boundary. Rather than give up on the buffer, walk to the next one.
                int next = IndexOfVersion(data[(offset + 1)..]);
                if (next < 0) break;
                offset += 1 + next;
                continue;
            }

            var sequence = BinaryPrimitives.ReadUInt32BigEndian(header[3..]);
            var random = BinaryPrimitives.ReadUInt32BigEndian(header[7..]);
            var timestamp = BinaryPrimitives.ReadUInt32BigEndian(header[11..]);
            var protocol = BinaryPrimitives.ReadUInt16BigEndian(header[15..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header[17..]);

            int end = offset + HeaderBytes + 2 + length;
            if (end > data.Length) break; // a partial frame; the caller will bring more bytes
            var body = data[(offset + HeaderBytes + 2)..end];

            byte[] payload;
            try { payload = length == 0 ? Array.Empty<byte>() : Decrypt(body, KeyFor(timestamp, localKey)); }
            catch (CryptographicException) { payload = Array.Empty<byte>(); }

            frames.Add(new RoborockFrame(sequence, random, timestamp, protocol, payload));
            offset = end + (length > 0 ? 4 : 0); // the CRC32 is only there when there is a payload
        }

        return frames;
    }

    /// <summary>
    /// The per-message key: MD5 of the shuffled timestamp, the device's local key, and the salt. The shuffle
    /// is Roborock's, and it is the whole reason a key can't be worked out from the local key alone.
    /// </summary>
    internal static byte[] KeyFor(uint timestamp, string localKey)
    {
        var material = new List<byte>();
        material.AddRange(EncodeTimestamp(timestamp));
        material.AddRange(Encoding.UTF8.GetBytes(localKey));
        material.AddRange(Salt);
        return MD5.HashData(material.ToArray());
    }

    /// <summary>The timestamp as 8 hex digits, reordered 5 6 3 7 1 2 0 4.</summary>
    internal static byte[] EncodeTimestamp(uint timestamp)
    {
        var hex = timestamp.ToString("x8");
        Span<int> order = stackalloc int[] { 5, 6, 3, 7, 1, 2, 0, 4 };
        var shuffled = new char[8];
        for (int i = 0; i < 8; i++) shuffled[i] = hex[order[i]];
        return Encoding.UTF8.GetBytes(shuffled);
    }

    private static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        return aes.EncryptEcb(plaintext, PaddingMode.PKCS7);
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> ciphertext, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(ciphertext, PaddingMode.PKCS7);
    }

    private static int IndexOfVersion(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 3 <= data.Length; i++)
            if (data.Slice(i, 3).SequenceEqual(Version)) return i;
        return -1;
    }
}

/// <summary>The IEEE CRC32 the frame ends with. Written out rather than taken from a package because it is
/// twenty lines and a plugin's dependencies all have to be shipped in its zip.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    public static uint Of(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;
            for (int bit = 0; bit < 8; bit++)
                entry = (entry & 1) != 0 ? 0xEDB88320 ^ (entry >> 1) : entry >> 1;
            table[i] = entry;
        }
        return table;
    }
}
