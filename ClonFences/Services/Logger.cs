using System;
using System.IO;

namespace ClonFences.Services;

internal static class Logger
{
    private static readonly object Gate = new();
    private static string LogPath => Path.Combine(ConfigService.Folder, "log.txt");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ConfigService.Folder);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // El registro nunca debe tumbar la aplicación.
        }
    }

    public static void Log(Exception ex) => Log(ex.ToString());
}
