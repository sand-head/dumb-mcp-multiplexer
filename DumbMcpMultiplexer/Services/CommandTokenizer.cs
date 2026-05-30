using System.Text;

namespace DumbMcpMultiplexer.Services;

internal static class CommandTokenizer
{
    internal static IReadOnlyList<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inSingleQuotes = false;
        var inDoubleQuotes = false;
        var isEscaping = false;

        foreach (var ch in command)
        {
            if (isEscaping)
            {
                current.Append(ch);
                isEscaping = false;
                continue;
            }

            if (ch == '\\' && !inSingleQuotes)
            {
                isEscaping = true;
                continue;
            }

            if (ch == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
                continue;
            }

            if (ch == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inSingleQuotes && !inDoubleQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (isEscaping)
        {
            throw new FormatException("Command contains a dangling escape character.");
        }

        if (inSingleQuotes || inDoubleQuotes)
        {
            throw new FormatException("Command contains unmatched quotes.");
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
