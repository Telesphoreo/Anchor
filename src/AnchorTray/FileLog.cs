namespace Anchor;

/// <summary>
/// Thread-safe append-only file log at %APPDATA%\Anchor\logs\anchor.log.
/// Rotates to a single .old file when the log exceeds 5 MB. Logging failures
/// are swallowed so they can never take the application down.
/// </summary>
public sealed class FileLog
{
    private const long MaxLogBytes = 5 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _logPath;
    private readonly string _oldLogPath;

    public FileLog()
    {
        var directory = Path.Combine(AppConfig.ConfigDirectory, "logs");
        _logPath = Path.Combine(directory, "anchor.log");
        _oldLogPath = _logPath + ".old";
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            // Nothing sensible to do; Log() will keep silently failing.
        }
    }

    public void Debug(string message) => Log("DEBUG", message);

    public void Info(string message) => Log("INFO", message);

    public void Warning(string message) => Log("WARNING", message);

    public void Error(string message) => Log("ERROR", message);

    /// <summary>Append one "yyyy-MM-dd HH:mm:ss.fff [LEVEL] msg" line.</summary>
    public void Log(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_logPath, line);
            }
            catch
            {
                // Logging must never crash the app.
            }
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_logPath);
        if (!info.Exists || info.Length < MaxLogBytes)
            return;
        File.Move(_logPath, _oldLogPath, overwrite: true);
    }
}
