namespace TapQueue.Server.Ipp;

/// <summary>Stores a job-attributes group as bytes so it can be replayed to the physical printer at release.</summary>
public static class JobTemplate
{
    // Attributes that describe the job on our side; the physical printer gets its own values.
    private static readonly HashSet<string> Excluded = ["job-name", "job-priority", "job-hold-until", "ipp-attribute-fidelity"];

    public static byte[] Encode(IppGroup jobAttributes)
    {
        var message = new IppMessage();
        var group = message.Group(IppTag.JobAttributes);
        group.Attributes.AddRange(jobAttributes.Attributes.Where(a => !Excluded.Contains(a.Name)));
        return IppWriter.Encode(message);
    }

    public static async Task<IppGroup> DecodeAsync(byte[] bytes, CancellationToken ct)
    {
        var message = await IppReader.ReadAsync(new MemoryStream(bytes), ct);
        return message.Groups.FirstOrDefault(g => g.Tag == IppTag.JobAttributes) ?? new IppGroup(IppTag.JobAttributes);
    }
}
