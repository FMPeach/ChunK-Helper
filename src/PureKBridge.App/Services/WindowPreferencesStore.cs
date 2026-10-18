using System.IO;
using System.Text.Json;

namespace PureKBridge.App.Services;

public sealed class WindowPreferencesStore
{
    private readonly string _path;
    private readonly AppLog _log;

    public WindowPreferencesStore(AppLog log, string? path = null)
    {
        _log = log;
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChunKHelper", "settings.json");
    }

    public bool LoadCloseToTray()
    {
        try
        {
            return !File.Exists(_path) ||
                (JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_path))?.CloseToTray ?? true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Write("无法读取窗口设置，使用关闭后隐藏到托盘", error);
            return true;
        }
    }

    public void SaveCloseToTray(bool value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Preferences(value)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _log.Write("无法保存窗口设置", error); }
    }

    private sealed record Preferences(bool CloseToTray = true);
}
