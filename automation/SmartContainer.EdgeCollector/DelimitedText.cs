using System.Text;

namespace SmartContainer.EdgeCollector;

internal static class DelimitedText
{
    public static IReadOnlyList<IReadOnlyList<string>> ParseTsv(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var value = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (!quoted && character == '\t')
            {
                row.Add(value.ToString());
                value.Clear();
                continue;
            }

            if (!quoted && character is '\r' or '\n')
            {
                if (character == '\r'
                    && index + 1 < text.Length
                    && text[index + 1] == '\n')
                {
                    index++;
                }

                row.Add(value.ToString());
                value.Clear();
                rows.Add(row);
                row = [];
                continue;
            }

            value.Append(character);
        }

        if (value.Length > 0 || row.Count > 0)
        {
            row.Add(value.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
