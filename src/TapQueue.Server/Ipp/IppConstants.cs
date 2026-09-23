namespace TapQueue.Server.Ipp;

/// <summary>Delimiter and value tags from RFC 8010 section 3.5.</summary>
public static class IppTag
{
    public const byte OperationAttributes = 0x01;
    public const byte JobAttributes = 0x02;
    public const byte EndOfAttributes = 0x03;
    public const byte PrinterAttributes = 0x04;
    public const byte UnsupportedAttributes = 0x05;

    public const byte Unsupported = 0x10;
    public const byte Unknown = 0x12;
    public const byte NoValue = 0x13;

    public const byte Integer = 0x21;
    public const byte Boolean = 0x22;
    public const byte Enum = 0x23;

    public const byte OctetString = 0x30;
    public const byte DateTime = 0x31;
    public const byte Resolution = 0x32;
    public const byte RangeOfInteger = 0x33;
    public const byte BegCollection = 0x34;
    public const byte TextWithLanguage = 0x35;
    public const byte NameWithLanguage = 0x36;
    public const byte EndCollection = 0x37;

    public const byte Text = 0x41;
    public const byte Name = 0x42;
    public const byte Keyword = 0x44;
    public const byte Uri = 0x45;
    public const byte UriScheme = 0x46;
    public const byte Charset = 0x47;
    public const byte NaturalLanguage = 0x48;
    public const byte MimeMediaType = 0x49;
    public const byte MemberAttrName = 0x4A;

    public static bool IsDelimiter(byte tag) => tag < 0x10;
}

public static class IppOperation
{
    public const short PrintJob = 0x0002;
    public const short ValidateJob = 0x0004;
    public const short CreateJob = 0x0005;
    public const short SendDocument = 0x0006;
    public const short CancelJob = 0x0008;
    public const short GetJobAttributes = 0x0009;
    public const short GetJobs = 0x000A;
    public const short GetPrinterAttributes = 0x000B;
    public const short CancelMyJobs = 0x0039;
    public const short CloseJob = 0x003B;
    public const short IdentifyPrinter = 0x003C;
}

public static class IppStatus
{
    public const short Ok = 0x0000;
    public const short OkIgnoredOrSubstituted = 0x0001;
    public const short ClientErrorBadRequest = 0x0400;
    public const short ClientErrorNotPossible = 0x0404;
    public const short ClientErrorNotFound = 0x0406;
    public const short ClientErrorDocumentFormatNotSupported = 0x040A;
    public const short ServerErrorInternal = 0x0500;
    public const short ServerErrorOperationNotSupported = 0x0501;
    public const short ServerErrorVersionNotSupported = 0x0503;

    public static bool IsSuccess(short status) => status < 0x0100;
}

public static class IppJobState
{
    public const int Pending = 3;
    public const int PendingHeld = 4;
    public const int Processing = 5;
    public const int ProcessingStopped = 6;
    public const int Canceled = 7;
    public const int Aborted = 8;
    public const int Completed = 9;
}

public static class IppPrinterState
{
    public const int Idle = 3;
    public const int Processing = 4;
    public const int Stopped = 5;
}
