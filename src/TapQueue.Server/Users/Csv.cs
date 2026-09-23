using System.Text;

namespace TapQueue.Server.Users;

/// <summary>Just enough RFC 4180 CSV for user import and export: quoted fields, doubled quotes, CRLF or LF.</summary>
public static class Csv
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add([.. row]);
                row.Clear();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }
        // Blank lines (often a trailing newline) aren't rows.
        return rows.Where(r => r.Any(f => f.Trim().Length > 0)).ToList();
    }

    public static string Write(IEnumerable<IEnumerable<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.Append(string.Join(',', row.Select(Quote))).Append("\r\n");
        return sb.ToString();
    }

    private static string Quote(string field) =>
        field.IndexOfAny([',', '"', '\n', '\r']) >= 0 || field != field.Trim() ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
}
