using System.IO;
using System.Text;
using System.Threading.Channels;
namespace WeLinkMessageExporter.Services;

public sealed class DiagnosticLog : IDisposable
{
    public static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeLinkMessageExporter", "logs");
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _writeTask;
    public DiagnosticLog()
    {
        _writeTask = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var file = Path.Combine(LogDirectory, DateTime.Now.ToString("yyyyMMdd") + ".log");
                await using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                await foreach (string line in _lines.Reader.ReadAllAsync()) await writer.WriteLineAsync(line);
            }
            catch { /* Logging errors must not break export. */ }
        });
    }
    public void Write(string message) => _lines.Writer.TryWrite($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}");
    public void Dispose()
    {
        _lines.Writer.TryComplete();
        try { _writeTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
    }
}
