using System.Buffers.Binary;
using System.Text;

namespace TapQueue.Server.Ipp;

/// <summary>Encodes an <see cref="IppMessage"/> (RFC 8010). Document data, if any, is written separately after it.</summary>
public static class IppWriter
{
    public static byte[] Encode(IppMessage message)
    {
        using var ms = new MemoryStream();
        Write(ms, message);
        return ms.ToArray();
    }

    public static void Write(Stream stream, IppMessage message)
    {
        stream.WriteByte(message.VersionMajor);
        stream.WriteByte(message.VersionMinor);
        WriteInt16(stream, message.Code);
        WriteInt32(stream, message.RequestId);

        foreach (var group in message.Groups)
        {
            stream.WriteByte(group.Tag);
            foreach (var attribute in group.Attributes)
                WriteAttribute(stream, attribute);
        }
        stream.WriteByte(IppTag.EndOfAttributes);
    }

    private static void WriteAttribute(Stream stream, IppAttribute attribute)
    {
        // An attribute with no values is sent as no-value so the name still reaches the client.
        if (attribute.Values.Count == 0)
        {
            WriteValue(stream, attribute.Name, IppValue.NoValue());
            return;
        }
        for (var i = 0; i < attribute.Values.Count; i++)
            WriteValue(stream, i == 0 ? attribute.Name : "", attribute.Values[i]);
    }

    private static void WriteValue(Stream stream, string name, IppValue value)
    {
        stream.WriteByte(value.Tag);
        WriteString(stream, name);

        switch (value.Value)
        {
            case IppCollection collection:
                WriteInt16(stream, 0);
                foreach (var member in collection)
                {
                    stream.WriteByte(IppTag.MemberAttrName);
                    WriteInt16(stream, 0);
                    WriteString(stream, member.Name);
                    foreach (var memberValue in member.Values)
                        WriteValue(stream, "", memberValue);
                }
                stream.WriteByte(IppTag.EndCollection);
                WriteInt16(stream, 0);
                WriteInt16(stream, 0);
                break;
            case null:
                WriteInt16(stream, 0);
                break;
            case int i:
                WriteInt16(stream, 4);
                WriteInt32(stream, i);
                break;
            case bool b:
                WriteInt16(stream, 1);
                stream.WriteByte(b ? (byte)1 : (byte)0);
                break;
            case IppRange r:
                WriteInt16(stream, 8);
                WriteInt32(stream, r.Lower);
                WriteInt32(stream, r.Upper);
                break;
            case IppResolution res:
                WriteInt16(stream, 9);
                WriteInt32(stream, res.CrossFeed);
                WriteInt32(stream, res.Feed);
                stream.WriteByte(res.Units);
                break;
            case string s:
                WriteString(stream, s);
                break;
            case byte[] bytes:
                WriteInt16(stream, (short)bytes.Length);
                stream.Write(bytes);
                break;
            default:
                throw new InvalidOperationException($"Cannot encode IPP value of type {value.Value.GetType()}.");
        }
    }

    private static void WriteString(Stream stream, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length > ushort.MaxValue)
            throw new InvalidOperationException("IPP string is too long.");
        WriteInt16(stream, (short)(ushort)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt16(Stream stream, short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
