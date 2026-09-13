using System.Diagnostics;

namespace ZweDesk.Core;

/// <summary>Daily rolling text log under %LocalAppData%\ZweDesk\logs. Never receives passwords.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string Dir { get; private set; } = "";

    public static void Init(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(dir);
    }

    public static string CurrentFile => Path.Combine(Dir, $"zwedesk-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        if (string.IsNullOrEmpty(Dir)) return;
        lock (Gate)
        {
            try { File.AppendAllText(CurrentFile, line + Environment.NewLine); }
            catch { /* logging must never take the app down */ }
        }
    }
}
