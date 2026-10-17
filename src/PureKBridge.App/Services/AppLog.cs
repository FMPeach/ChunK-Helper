using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace PureKBridge.App.Services;

public sealed partial class AppLog
{
    private readonly object _gate = new();
    public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "ChunKHelper.log");

    public void Write(string message, Exception? error = null)
    {
        var text = SensitiveQuery().Replace(message + (error is null ? "" : Environment.NewLine + error), "$1[hidden]");
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {text}{Environment.NewLine}";
        Debug.WriteLine(line);
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > 1024 * 1024)
                    File.Move(Path, Path + ".1", true);
                File.AppendAllText(Path, line);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        { Debug.WriteLine(failure.GetType().Name); }
    }

    [GeneratedRegex(@"(?i)((?:token|projection_url|bill_id)=)[^\s&]+")]
    private static partial Regex SensitiveQuery();
}
