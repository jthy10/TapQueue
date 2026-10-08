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

    /// <param name="guardFormulas">
    /// For files people open in a spreadsheet: a field that would start a formula (=, +, -, @) gets a
    /// leading ', which spreadsheets show as text. <see cref="Unguard"/> takes it off again on import.
    /// </param>
    public static string Write(IEnumerable<IEnumerable<string>> rows, bool guardFormulas = false)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.Append(string.Join(',', row.Select(f => Quote(guardFormulas ? Guard(f) : f)))).Append("\r\n");
        return sb.ToString();
    }

    private const string FormulaStarts = "=+-@\t\r";

    /// <summary>A ' in front of a field a spreadsheet would read as a formula, or that starts with ' already.</summary>
    public static string Guard(string field) =>
        field.Length > 0 && (FormulaStarts.Contains(field[0]) || field[0] == '\'') ? "'" + field : field;

    /// <summary>Undoes <see cref="Guard"/>: drops a leading ' in front of a formula character or another '.</summary>
    public static string Unguard(string field) =>
        field.Length > 1 && field[0] == '\'' && (FormulaStarts.Contains(field[1]) || field[1] == '\'') ? field[1..] : field;

    private static string Quote(string field) =>
        field.IndexOfAny([',', '"', '\n', '\r']) >= 0 || field != field.Trim() ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
}
