namespace SafeGitPublisher.Models;

/// <summary>
/// 日志级别，对应 UI 底部日志区的颜色与图标。
/// </summary>
public enum LogLevel
{
    Info,
    Pass,
    Warn,
    Blocked,
    Error,
    Ready
}

/// <summary>
/// 一条 UI 日志记录。
/// </summary>
public sealed class LogEntry
{
    public LogEntry(LogLevel level, string message)
    {
        Level = level;
        Message = message;
        Time = DateTime.Now;
    }

    public DateTime Time { get; }

    public LogLevel Level { get; }

    public string Message { get; }

    public string DisplayTime => Time.ToString("HH:mm:ss");

    public string LevelShort => Level switch
    {
        LogLevel.Pass => "PASS",
        LogLevel.Warn => "WARN",
        LogLevel.Blocked => "BLOCKED",
        LogLevel.Error => "ERROR",
        LogLevel.Ready => "READY",
        _ => "INFO"
    };
}