namespace Raizen.Server.Setup.Installer;

/// <summary>
/// Simple append-only log written to %TEMP%\RaizenSetup.log.
/// Never throws — logging failures are silently swallowed.
/// </summary>
public static class SetupLog
{
    public static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "RaizenSetup.log");

    public static void Info(string message)  => Write("INFO ", message);
    public static void Warn(string message)  => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}{Environment.NewLine}  {ex}");

    public static void Section(string title)
    {
        var bar = new string('-', 60);
        Write("-----", $"{bar}{Environment.NewLine}  {title}{Environment.NewLine}  {bar}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { /* never throw from logging */ }
    }
}
