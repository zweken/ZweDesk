namespace ZweDesk.Core;

/// <summary>All on-disk locations. Everything lives under %LocalAppData%\ZweDesk so the exe can sit anywhere, read-only included.</summary>
public static class AppPaths
{
    public static string DataDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZweDesk");

    public static string DbPath { get; private set; } = Path.Combine(DataDir, "zwedesk.db");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string WebViewDir => Path.Combine(DataDir, "WebView2");
    public static string UiDir => Path.Combine(DataDir, "ui", AppVersion);
    public static string ExeDir => AppContext.BaseDirectory;
    public static string PrereqDir => Path.Combine(ExeDir, "prereq");

    public static string AppVersion =>
        typeof(AppPaths).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static void OverrideDb(string path)
    {
        DbPath = Path.GetFullPath(path);
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(WebViewDir);
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
    }
}
