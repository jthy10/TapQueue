using System.Buffers.Binary;
using System.Text;

namespace TapQueue.Server.Ipp;

/// <summary>
/// Reads the attribute section of an IPP message (RFC 8010). Reading stops right after the
/// end-of-attributes tag, so any document data that follows is left unread in the stream.
/// </summary>
public sealed class IppReader(Stream stream)
{
    private readonly byte[] _scratch = new byte[8];

    public static async Task<IppMessage> ReadAsync(Stream stream, CancellationToken ct = default) =>
        await new IppReader(stream).ReadMessageAsync(ct);

    public async Task<IppMessage> ReadMessageAsync(CancellationToken ct)
    {
        var message = new IppMessage
        {
            VersionMajor = await ReadByteAsync(ct),
            VersionMinor = await ReadByteAsync(ct),
            Code = await ReadInt16Async(ct),
            RequestId = await ReadInt32Async(ct),
        };

        IppGroup? group = null;
        IppAttribute? current = null;
        while (true)
        {
            var tag = await ReadByteAsync(ct);
            if (tag == IppTag.EndOfAttributes)
                return message;

            if (IppTag.IsDelimiter(tag))
            {
                group = new IppGroup(tag);
                message.Groups.Add(group);
                current = null;
                continue;
            }

            if (group is null)
                throw new InvalidDataException("IPP attribute appeared before any attribute group.");

            var name = await ReadStringAsync(ct);
            var value = await ReadValueAsync(tag, ct);
            if (name.Length == 0)
            {
                if (current is null)
                    throw new InvalidDataException("IPP additional value appeared without an attribute.");
                current.Values.Add(value);
            }
            else
            {
                current = new IppAttribute(name, value);
                group.Attributes.Add(current);
            }
        }
    }

    private async Task<IppValue> ReadValueAsync(byte tag, CancellationToken ct)
    {
        var bytes = await ReadBytesAsync(await ReadLengthAsync(ct), ct);
        if (tag == IppTag.BegCollection)
            return new IppValue(tag, await ReadCollectionAsync(ct));
        return Decode(tag, bytes);
    }

    private async Task<IppCollection> ReadCollectionAsync(CancellationToken ct)
    {
        var collection = new IppCollection();
        IppAttribute? member = null;
        while (true)
        {
            var tag = await ReadByteAsync(ct);
            await ReadStringAsync(ct); // name is always empty inside a collection
            if (tag == IppTag.EndCollection)
            {
                await ReadBytesAsync(await ReadLengthAsync(ct), ct);
                return collection;
            }

            if (tag == IppTag.MemberAttrName)
            {
                var memberName = Encoding.UTF8.GetString(await ReadBytesAsync(await ReadLengthAsync(ct), ct));
                member = new IppAttribute(memberName);
                collection.Add(member);
                continue;
            }

            if (member is null)
                throw new InvalidDataException("IPP collection value appeared before a member name.");
            member.Values.Add(await ReadValueAsync(tag, ct));
        }
    }

    private static IppValue Decode(byte tag, byte[] bytes)
    {
        object? value = tag switch
        {
            >= 0x10 and <= 0x1F => null, // out-of-band: unsupported, unknown, no-value
            IppTag.Integer or IppTag.Enum when bytes.Length == 4 => BinaryPrimitives.ReadInt32BigEndian(bytes),
            IppTag.Boolean when bytes.Length == 1 => bytes[0] != 0,
            IppTag.RangeOfInteger when bytes.Length == 8 => new IppRange(
                BinaryPrimitives.ReadInt32BigEndian(bytes),
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4))),
            IppTag.Resolution when bytes.Length == 9 => new IppResolution(
                BinaryPrimitives.ReadInt32BigEndian(bytes),
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4)),
                bytes[8]),
            IppTag.TextWithLanguage or IppTag.NameWithLanguage => DecodeWithLanguage(bytes),
            >= 0x40 and <= 0x5F => Encoding.UTF8.GetString(bytes),
            _ => bytes,
        };
        return new IppValue(tag, value);
    }

    private static string DecodeWithLanguage(byte[] bytes)
    {
        // [2-byte len][language][2-byte len][text]; we keep only the text.
        if (bytes.Length < 4) return "";
        var langLength = BinaryPrimitives.ReadInt16BigEndian(bytes);
        var textStart = 2 + langLength + 2;
        return textStart <= bytes.Length ? Encoding.UTF8.GetString(bytes, textStart, bytes.Length - textStart) : "";
    }

    private async Task<string> ReadStringAsync(CancellationToken ct) =>
        Encoding.UTF8.GetString(await ReadBytesAsync(await ReadLengthAsync(ct), ct));

    // Lengths are unsigned 16-bit, so a malformed message can't make us allocate more than 64 KB at a time.
    private async Task<int> ReadLengthAsync(CancellationToken ct) => (ushort)await ReadInt16Async(ct);

    private async Task<byte[]> ReadBytesAsync(int count, CancellationToken ct)
    {
        if (count == 0) return [];
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    private async Task<byte> ReadByteAsync(CancellationToken ct)
    {
        await stream.ReadExactlyAsync(_scratch.AsMemory(0, 1), ct);
        return _scratch[0];
    }

    private async Task<short> ReadInt16Async(CancellationToken ct)
    {
        await stream.ReadExactlyAsync(_scratch.AsMemory(0, 2), ct);
        return BinaryPrimitives.ReadInt16BigEndian(_scratch);
    }

    private async Task<int> ReadInt32Async(CancellationToken ct)
    {
        await stream.ReadExactlyAsync(_scratch.AsMemory(0, 4), ct);
        return BinaryPrimitives.ReadInt32BigEndian(_scratch);
    }
}
