using System.Text.Json.Serialization;

namespace SkillSync.Daemon.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderType
{
    Canonical = 0,
    Claude = 1,
    Gemini = 2,
    Codex = 3,
    Cursor = 4
}

