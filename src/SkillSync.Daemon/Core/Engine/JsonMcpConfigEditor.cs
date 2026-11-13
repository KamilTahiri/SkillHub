using System.Text;
using System.Text.Json;

namespace SkillSync.Daemon.Core.Engine;

internal static class JsonMcpConfigEditor
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private sealed record Property(string Name, int KeyStart, int ValueStart, int ValueEnd);
    private sealed record ObjectSlice(int ClosingBrace, IReadOnlyList<Property> Properties)
    {
        public Property? Find(string name) => Properties.SingleOrDefault(property => property.Name == name);
    }

    public static bool Contains(string text)
    {
        text = text.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var root = ReadObject(text, 0);
        return root.Find("mcpServers") is { } servers &&
               ReadObject(text, servers.ValueStart).Find(InstallManager.McpServerKey) is { } server &&
               IsRegistration(text, server, legacy: false);
    }

    public static string Update(string text, string command, IReadOnlyList<string> args)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "{}\n";
        }
        var root = ReadObject(text, 0);
        var registration = JsonSerializer.Serialize(new { command, args });
        if (root.Find("mcpServers") is not { } servers)
        {
            return Set(text, root, "mcpServers", "{\"skillhub\":" + registration + "}");
        }
        var serverMap = ReadObject(text, servers.ValueStart);
        if (serverMap.Find(InstallManager.LegacyMcpServerKey) is { } legacy && IsRegistration(text, legacy, legacy: true))
        {
            text = Remove(text, serverMap, legacy);
            serverMap = ReadObject(text, servers.ValueStart);
        }
        if (serverMap.Find(InstallManager.McpServerKey) is not { } current)
        {
            return Set(text, serverMap, InstallManager.McpServerKey, registration);
        }

        var entry = ReadObject(text, current.ValueStart);
        text = Set(text, entry, "command", JsonSerializer.Serialize(command));
        entry = ReadObject(text, current.ValueStart);
        return Set(text, entry, "args", JsonSerializer.Serialize(args));
    }

    public static string RemoveRegistration(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        var root = ReadObject(text, 0);
        if (root.Find("mcpServers") is not { } servers)
        {
            return text;
        }
        var serverMap = ReadObject(text, servers.ValueStart);
        if (serverMap.Find(InstallManager.McpServerKey) is { } current && IsRegistration(text, current, legacy: false))
        {
            text = Remove(text, serverMap, current);
        }
        serverMap = ReadObject(text, servers.ValueStart);
        return serverMap.Find(InstallManager.LegacyMcpServerKey) is { } legacy && IsRegistration(text, legacy, legacy: true)
            ? Remove(text, serverMap, legacy) : text;
    }

    private static bool IsRegistration(string text, Property property, bool legacy)
    {
        using var doc = JsonDocument.Parse(text[property.ValueStart..property.ValueEnd], Options);
        var entry = doc.RootElement;
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array ||
            !args.EnumerateArray().Any(arg => arg.ValueKind == JsonValueKind.String && arg.GetString() == "mcp-stdio"))
        {
            return false;
        }
        return !legacy || entry.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String &&
            (command.GetString()?.Contains("skillsync", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static ObjectSlice ReadObject(string text, int start)
    {
        var bytes = Encoding.UTF8.GetBytes(text[start..]);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = Options.AllowTrailingCommas,
            CommentHandling = Options.CommentHandling
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException("The client configuration and MCP entries must be JSON objects.");
        }

        int Offset(long index) => start + Encoding.UTF8.GetCharCount(bytes.AsSpan(0, checked((int)index)));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var properties = new List<Property>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString()!;
            var key = Offset(reader.TokenStartIndex);
            if (!names.Add(name) || !reader.Read())
            {
                throw new InvalidDataException($"Duplicate or incomplete JSON property '{name}'.");
            }
            var value = Offset(reader.TokenStartIndex);
            reader.Skip();
            properties.Add(new Property(name, key, value, Offset(reader.BytesConsumed)));
        }
        if (reader.TokenType != JsonTokenType.EndObject)
        {
            throw new InvalidDataException("The client configuration has an incomplete object.");
        }
        var closing = Offset(reader.TokenStartIndex);
        if (start == 0 && reader.Read())
        {
            throw new InvalidDataException("Unexpected content after the client configuration.");
        }
        return new ObjectSlice(closing, properties);
    }

    private static string Set(string text, ObjectSlice target, string name, string value)
    {
        if (target.Find(name) is { } property)
        {
            return text[..property.ValueStart] + value + text[property.ValueEnd..];
        }

        var previous = target.Properties.LastOrDefault();
        var position = previous?.ValueEnd ?? target.ClosingBrace;
        var prefix = previous is null ? string.Empty : ",";
        return text.Insert(position, $"{prefix}\n  {JsonSerializer.Serialize(name)}: {value}");
    }

    private static string Remove(string text, ObjectSlice target, Property property)
    {
        var next = target.Properties.FirstOrDefault(other => other.KeyStart > property.KeyStart);
        var previous = target.Properties.LastOrDefault(other => other.KeyStart < property.KeyStart);
        var comma = FindComma(text, property.ValueEnd, next?.KeyStart ?? target.ClosingBrace);
        if (comma < 0 && previous is not null)
        {
            comma = FindComma(text, previous.ValueEnd, property.KeyStart);
        }

        if (comma >= property.ValueEnd)
        {
            text = text.Remove(comma, 1);
        }
        text = text.Remove(property.KeyStart, property.ValueEnd - property.KeyStart);
        return comma >= 0 && comma < property.KeyStart ? text.Remove(comma, 1) : text;
    }

    private static int FindComma(string text, int start, int end)
    {
        while (start < end)
        {
            if (text[start] == ',')
            {
                return start;
            }
            if (start + 1 < end && text[start] == '/' && text[start + 1] is '/' or '*')
            {
                var multiline = text[start + 1] == '*';
                var close = text.IndexOf(multiline ? "*/" : "\n", start + 2, StringComparison.Ordinal);
                start = close < 0 ? end : close + (multiline ? 2 : 1);
            }
            else
            {
                start++;
            }
        }
        return -1;
    }
}
