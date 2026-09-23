using System.Text;

namespace TapQueue.Server.Ipp;

public sealed record IppResolution(int CrossFeed, int Feed, byte Units)
{
    public const byte DotsPerInch = 3;
}

public sealed record IppRange(int Lower, int Upper);

/// <summary>A collection value: an ordered list of member attributes.</summary>
public sealed class IppCollection : List<IppAttribute>
{
    public IppCollection Add(string name, params IppValue[] values)
    {
        Add(new IppAttribute(name, values));
        return this;
    }
}

/// <summary>
/// One IPP value with its wire tag. <see cref="Value"/> is a string, int, bool, byte[],
/// <see cref="IppResolution"/>, <see cref="IppRange"/>, <see cref="IppCollection"/>, or null for out-of-band tags.
/// </summary>
public readonly record struct IppValue(byte Tag, object? Value)
{
    public static IppValue Keyword(string v) => new(IppTag.Keyword, v);
    public static IppValue Text(string v) => new(IppTag.Text, v);
    public static IppValue Name(string v) => new(IppTag.Name, v);
    public static IppValue Uri(string v) => new(IppTag.Uri, v);
    public static IppValue UriScheme(string v) => new(IppTag.UriScheme, v);
    public static IppValue Charset(string v) => new(IppTag.Charset, v);
    public static IppValue Language(string v) => new(IppTag.NaturalLanguage, v);
    public static IppValue MimeType(string v) => new(IppTag.MimeMediaType, v);
    public static IppValue Integer(int v) => new(IppTag.Integer, v);
    public static IppValue Enum(int v) => new(IppTag.Enum, v);
    public static IppValue Boolean(bool v) => new(IppTag.Boolean, v);
    public static IppValue Range(int lower, int upper) => new(IppTag.RangeOfInteger, new IppRange(lower, upper));
    public static IppValue Resolution(int x, int y) => new(IppTag.Resolution, new IppResolution(x, y, IppResolution.DotsPerInch));
    public static IppValue Collection(IppCollection c) => new(IppTag.BegCollection, c);
    public static IppValue NoValue() => new(IppTag.NoValue, null);
    public static IppValue Unknown() => new(IppTag.Unknown, null);

    /// <summary>RFC 2579 DateAndTime, always sent in UTC.</summary>
    public static IppValue DateTime(DateTimeOffset time)
    {
        var t = time.UtcDateTime;
        return new(IppTag.DateTime, new byte[]
        {
            (byte)(t.Year >> 8), (byte)t.Year, (byte)t.Month, (byte)t.Day, (byte)t.Hour, (byte)t.Minute,
            (byte)t.Second, (byte)(t.Millisecond / 100), (byte)'+', 0, 0,
        });
    }

    public string? AsString() => Value as string;
    public int? AsInt() => Value is int i ? i : null;
    public bool? AsBool() => Value is bool b ? b : null;
}

public sealed class IppAttribute(string name, IEnumerable<IppValue> values)
{
    public string Name { get; } = name;
    public List<IppValue> Values { get; } = [.. values];

    public IppAttribute(string name, params IppValue[] values) : this(name, (IEnumerable<IppValue>)values) { }

    public IppValue? First => Values.Count > 0 ? Values[0] : null;
}

public sealed class IppGroup(byte tag)
{
    public byte Tag { get; } = tag;
    public List<IppAttribute> Attributes { get; } = [];

    public IppGroup Add(string name, params IppValue[] values)
    {
        Attributes.Add(new IppAttribute(name, values));
        return this;
    }

    public IppGroup Add(string name, IEnumerable<IppValue> values)
    {
        Attributes.Add(new IppAttribute(name, values));
        return this;
    }

    public IppAttribute? Find(string name) => Attributes.Find(a => a.Name == name);
}

public sealed class IppMessage
{
    public byte VersionMajor { get; set; } = 2;
    public byte VersionMinor { get; set; } = 0;

    /// <summary>operation-id on requests, status-code on responses.</summary>
    public short Code { get; set; }

    public int RequestId { get; set; }
    public List<IppGroup> Groups { get; } = [];

    public IppGroup Group(byte tag)
    {
        var group = Groups.Find(g => g.Tag == tag);
        if (group is null)
        {
            group = new IppGroup(tag);
            Groups.Add(group);
        }
        return group;
    }

    public IppAttribute? Find(byte groupTag, string name) =>
        Groups.Where(g => g.Tag == groupTag).Select(g => g.Find(name)).FirstOrDefault(a => a is not null);

    public string? OperationString(string name) => Find(IppTag.OperationAttributes, name)?.First?.AsString();

    public int? OperationInt(string name) => Find(IppTag.OperationAttributes, name)?.First?.AsInt();

    public static IppMessage CreateRequest(short operation, int requestId, string printerUri)
    {
        var message = new IppMessage { Code = operation, RequestId = requestId };
        message.Group(IppTag.OperationAttributes)
            .Add("attributes-charset", IppValue.Charset("utf-8"))
            .Add("attributes-natural-language", IppValue.Language("en"))
            .Add("printer-uri", IppValue.Uri(printerUri));
        return message;
    }

    public static IppMessage CreateResponse(IppMessage request, short status, string? statusMessage = null)
    {
        var response = new IppMessage
        {
            VersionMajor = Math.Min(request.VersionMajor, (byte)2),
            VersionMinor = request.VersionMajor >= 2 ? (byte)0 : request.VersionMinor,
            Code = status,
            RequestId = request.RequestId,
        };
        var op = response.Group(IppTag.OperationAttributes)
            .Add("attributes-charset", IppValue.Charset("utf-8"))
            .Add("attributes-natural-language", IppValue.Language("en"));
        if (statusMessage is not null)
            op.Add("status-message", IppValue.Text(statusMessage));
        return response;
    }

    public override string ToString()
    {
        var sb = new StringBuilder($"IPP/{VersionMajor}.{VersionMinor} code=0x{Code:X4} request-id={RequestId}");
        foreach (var group in Groups)
            foreach (var attr in group.Attributes)
                sb.Append($"\n  [{group.Tag:X2}] {attr.Name} = {string.Join(", ", attr.Values.Select(v => v.Value))}");
        return sb.ToString();
    }
}
