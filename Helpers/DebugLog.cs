using System.Text;

namespace PMMovil.Api.Helpers;

public static class DebugLog
{
    private static readonly object _lock = new();
    private static string? _logPath;

    private static string GetPath()
    {
        if (_logPath != null) return _logPath;
        var dir = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "entrada-debug.log");
        return _logPath;
    }

    public static void Write(string tag, object? data = null)
    {
        try
        {
            var linea = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {tag} {data}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(GetPath(), linea, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
