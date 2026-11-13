using System.Text.Json;
using System.Text;

namespace SkillSync.Daemon.Core.Engine;

internal static class TomlMcpConfigEditor
{
    private static readonly string[] Target = ["mcp_servers", "skillhub"];

    private sealed record Statement(string[] Key, string[] Table, int Start, int End, int ValueStart, int ValueEnd, bool Header);

    public static bool Contains(string text) => Parse(text.TrimStart('\uFEFF')).Any(item =>
        item.Header && item.Table.SequenceEqual(Target));

    public static string Update(string text, string command, IReadOnlyList<string> args)
    {
        var statements = Parse(text);
        var headers = statements.Where(item => item.Header && item.Table.SequenceEqual(Target)).ToArray();
        if (headers.Length > 1)
        {
            throw new InvalidDataException("Codex configuration declares the SkillHub table more than once.");
        }

        // Inline or dotted definitions may already own this table. A new header would invalidate the document.
        if (statements.Any(item => !item.Header &&
            (item.Table.Length < 2 && item.Table.Concat(item.Key).Take(2).SequenceEqual(Target) ||
             item.Table.Length == 0 && item.Key.SequenceEqual(Target.Take(1)))))
        {
            throw new InvalidDataException("SkillHub cannot safely edit a Codex MCP entry defined as an inline table or dotted assignment.");
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["command"] = Quote(command),
            ["args"] = $"[{string.Join(", ", args.Select(Quote))}]"
        };

        if (headers.Length == 0)
        {
            var separator = text.Length == 0 ? "" : text.EndsWith('\n') ? newline : newline + newline;
            return text + separator + "[mcp_servers.skillhub]" + newline +
                   string.Concat(values.Select(pair => $"{pair.Key} = {pair.Value}{newline}"));
        }

        var edits = new List<(int Start, int End, string Text)>();
        var header = headers[0];
        var missing = new List<string>();
        foreach (var (key, value) in values)
        {
            var matches = statements.Where(item => !item.Header && item.Table.SequenceEqual(Target) &&
                item.Key.SequenceEqual([key])).ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidDataException($"Codex configuration defines '{key}' more than once in the SkillHub table.");
            }
            if (matches.Length == 0)
            {
                missing.Add($"{key} = {value}{newline}");
            }
            else
            {
                var existing = matches[0];
                edits.Add((existing.ValueStart, existing.ValueEnd, value));
            }
        }
        if (missing.Count > 0)
        {
            var prefix = header.End > 0 && text[header.End - 1] == '\n' ? "" : newline;
            edits.Add((header.End, header.End, prefix + string.Concat(missing)));
        }
        return Apply(text, edits);
    }

    public static string Remove(string text)
    {
        var statements = Parse(text);
        var header = statements.SingleOrDefault(item => item.Header && item.Table.SequenceEqual(Target));
        if (header is null)
        {
            return text;
        }
        var args = statements.SingleOrDefault(item => !item.Header && item.Table.SequenceEqual(Target) && item.Key.SequenceEqual(["args"]));
        if (args is null || !text[args.ValueStart..args.ValueEnd].Contains("mcp-stdio", StringComparison.Ordinal))
        {
            throw new SkillConflictException("The existing Codex SkillHub entry could not be identified as a SkillHub registration.");
        }
        return Apply(text, statements.Where(item => item.Table.Take(2).SequenceEqual(Target))
            .Select(item => (item.Start, item.End, string.Empty)));
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var character in value)
        {
            result.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                _ when character < 0x20 || character == 0x7f => $"\\u{(int)character:X4}",
                _ => character.ToString()
            });
        }
        return result.Append('"').ToString();
    }

    private static string Apply(string text, IEnumerable<(int Start, int End, string Text)> edits)
    {
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            text = text[..edit.Start] + edit.Text + text[edit.End..];
        }
        return text;
    }

    private static IReadOnlyList<Statement> Parse(string text)
    {
        var statements = new List<Statement>();
        string[] table = [];
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            SkipSpaces(text, ref index);
            if (index == text.Length || text[index] is '#' or '\r' or '\n')
            {
                SkipLine(text, ref index);
                continue;
            }

            if (text[index] == '[')
            {
                index++;
                var array = index < text.Length && text[index] == '[';
                if (array)
                {
                    index++;
                }
                table = ReadKey(text, ref index, ']');
                index++;
                if (array)
                {
                    Expect(text, ref index, ']');
                    if (table.Take(2).SequenceEqual(Target))
                    {
                        throw new InvalidDataException("SkillHub's MCP entry must be a regular TOML table.");
                    }
                }
                EndStatement(text, ref index);
                statements.Add(new Statement([], table, start, index, 0, 0, true));
                continue;
            }

            var key = ReadKey(text, ref index, '=');
            index++;
            SkipSpaces(text, ref index);
            var valueStart = index;
            var valueEnd = ReadValue(text, ref index);
            if (valueEnd <= valueStart)
            {
                throw new InvalidDataException("A TOML assignment has no value.");
            }
            EndStatement(text, ref index);
            statements.Add(new Statement(key, table, start, index, valueStart, valueEnd, false));
        }

        return statements;
    }

    private static string[] ReadKey(string text, ref int index, char terminator)
    {
        var components = new List<string>();
        while (index < text.Length)
        {
            SkipSpaces(text, ref index);
            if (index >= text.Length)
            {
                break;
            }
            if (text[index] is '"' or '\'')
            {
                var start = index;
                ReadString(text, ref index, allowMultiline: false);
                var raw = text[start..index];
                components.Add(raw[0] == '\'' ? raw[1..^1] : JsonSerializer.Deserialize<string>(raw)!);
            }
            else
            {
                var start = index;
                while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] is '_' or '-'))
                {
                    index++;
                }
                if (index == start)
                {
                    throw new InvalidDataException("Invalid TOML key.");
                }
                components.Add(text[start..index]);
            }

            SkipSpaces(text, ref index);
            if (index < text.Length && text[index] == terminator)
            {
                return components.ToArray();
            }
            Expect(text, ref index, '.');
        }
        throw new InvalidDataException("Unterminated TOML key.");
    }

    private static int ReadValue(string text, ref int index)
    {
        var brackets = new Stack<char>();
        var end = index;
        while (index < text.Length)
        {
            var character = text[index];
            if (brackets.Count == 0 && character is '\r' or '\n' or '#')
            {
                break;
            }
            switch (character)
            {
                case '"' or '\'':
                    ReadString(text, ref index, allowMultiline: true);
                    break;
                case '[' or '{':
                    brackets.Push(character == '[' ? ']' : '}');
                    index++;
                    break;
                case ']' or '}':
                    if (!brackets.TryPop(out var expected) || expected != character)
                    {
                        throw new InvalidDataException("Invalid TOML array or inline table.");
                    }
                    index++;
                    break;
                case '#':
                    SkipLine(text, ref index);
                    continue;
                default:
                    index++;
                    break;
            }
            if (!char.IsWhiteSpace(character))
            {
                end = index;
            }
        }
        if (brackets.Count != 0)
        {
            throw new InvalidDataException("Unterminated TOML array or inline table.");
        }
        return end;
    }

    private static void ReadString(string text, ref int index, bool allowMultiline)
    {
        var quote = text[index++];
        var multiline = allowMultiline && index + 1 < text.Length && text[index] == quote && text[index + 1] == quote;
        if (multiline)
        {
            index += 2;
        }

        while (index < text.Length)
        {
            var character = text[index++];
            if (quote == '"' && character == '\\')
            {
                index++;
            }
            else if (character == quote)
            {
                if (!multiline)
                {
                    return;
                }
                var first = index - 1;
                while (index < text.Length && text[index] == quote)
                {
                    index++;
                }
                if (index - first >= 3)
                {
                    return;
                }
            }
            else if (!multiline && character is '\r' or '\n')
            {
                break;
            }
        }
        throw new InvalidDataException("Unterminated TOML string.");
    }

    private static void Expect(string text, ref int index, char expected)
    {
        if (index >= text.Length || text[index] != expected)
        {
            throw new InvalidDataException($"Expected '{expected}' in the Codex configuration.");
        }
        index++;
    }

    private static void SkipSpaces(string text, ref int index)
    {
        while (index < text.Length && text[index] is ' ' or '\t')
        {
            index++;
        }
    }

    private static void EndStatement(string text, ref int index)
    {
        SkipSpaces(text, ref index);
        if (index < text.Length && text[index] is not ('\r' or '\n' or '#'))
        {
            throw new InvalidDataException("Unexpected characters after a TOML statement.");
        }
        SkipLine(text, ref index);
    }

    private static void SkipLine(string text, ref int index)
    {
        var newline = text.IndexOf('\n', index);
        index = newline < 0 ? text.Length : newline + 1;
    }
}
