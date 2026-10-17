using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClonFences.Models;

namespace ClonFences.Services;

internal static class ConfigService
{
    // CLONFENCES_DATA permite usar otra carpeta de datos (pruebas, perfiles portátiles).
    public static readonly string Folder =
        Environment.GetEnvironmentVariable("CLONFENCES_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClonFences");

    public static string FilePath => Path.Combine(Folder, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options);
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex);
            try { File.Copy(FilePath, FilePath + ".corrupt", true); } catch { }
        }
        var fresh = new AppConfig();
        fresh.Normalize();
        return fresh;
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
