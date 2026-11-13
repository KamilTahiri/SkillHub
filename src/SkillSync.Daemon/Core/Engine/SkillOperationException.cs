namespace SkillSync.Daemon.Core.Engine;

public sealed class SkillConflictException(string message) : InvalidOperationException(message);
