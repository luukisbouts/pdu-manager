using System.IO;
using System.Text.Json;
using PduManager.Models;

namespace PduManager.Services;

public class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public ConfigStore(string? path = null)
    {
        // Next to the exe, so the app and its data move together.
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        _path = path ?? Path.Combine(exeDir, "config.json");

        // One-time migration from the old %APPDATA% location.
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PduManager", "config.json");
        if (path is null && !File.Exists(_path) && File.Exists(legacy))
        {
            try { File.Copy(legacy, _path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path)) ?? new();
        }
        catch (JsonException) { }
        return new AppConfig();
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        File.Move(tmp, _path, overwrite: true);
    }
}
