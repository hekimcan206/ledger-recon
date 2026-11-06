using System.Globalization;
using System.Text;

namespace LedgerRecon.Core.Reconciliation.Parsing;

/// <summary>Minimal RFC 4180 reader for one-line records (quoted fields, doubled quotes; no embedded newlines).</summary>
internal static class Csv
{
    public static IEnumerable<(int LineNumber, string Line)> Lines(string content)
    {
        using var reader = new StringReader(content);
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if (line.Length > 0 && line[0] == '﻿')
            {
                line = line[1..];
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return (number, line);
            }
        }
    }

    public static string[]? Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"' && current.Length == 0)
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (quoted)
        {
            return null;
        }

        fields.Add(current.ToString());
        return [.. fields];
    }

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }

    public static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static bool TryAmount(string value, out long minor)
    {
        minor = 0;
        return decimal.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount)
            && Ledger.Money.TryToMinor(amount, out minor);
    }
}
