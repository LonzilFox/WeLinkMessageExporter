using System;
using System.IO;
using System.Text;

namespace WeLinkMessageExporter.Services;

/// <summary>
/// Synchronous startup/crash log. Independent of the UI and asynchronous DiagnosticLog.
/// No message content, access token, or CLI stdout is written by this class.
/// </summary>
public static class CrashReporter
{
    private static readonly object Gate = new();

    public static string DirectoryPath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(string.IsNullOrWhiteSpace(local) ? Path.GetTempPath() : local,
                "WeLinkMessageExporter", "logs");
        }
    }

    public static string FilePath => Path.Combine(DirectoryPath, "startup-crash.log");

    public static void Trace(string stage) => Append("[STARTUP] " + stage);

    public static void Exception(string stage, Exception error)
    {
        Append("[ERROR] " + stage + Environment.NewLine + error);
    }

    private static void Append(string text)
    {
        lock (Gate)
        {
            var record = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}";
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(FilePath, record, new UTF8Encoding(false));
            }
            catch
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "WeLinkMessageExporter-startup-crash.log"),
                        record, new UTF8Encoding(false));
                }
                catch { /* Never throw from diagnostic infrastructure. */ }
            }
        }
    }
}
