using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WeLinkMessageExporter.Services;

public sealed class CliService
{
    // WeLink CLI npm package exposes a Windows .cmd shim. Run only fixed commands and validated values.
    public static string ValidateSearch(string text)
    {
        text = text.Trim();
        if (text.Length is < 1 or > 80 || !Regex.IsMatch(text, @"^[\p{L}\p{N}\s_\-·.（）()【】]+$"))
            throw new InvalidOperationException("搜索内容仅支持文字、数字、空格、点、下划线、括号和连字符（最多80字）。");
        return text;
    }
    public static string ValidateId(string id)
    {
        id = id.Trim();
        if (!Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,100}$")) throw new InvalidOperationException("会话 ID 只能包含字母、数字、下划线和连字符。");
        return id;
    }
    private static string Quote(string s) => "\"" + s + "\"";
    public async Task<string> ExecuteAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        var args = arguments.ToArray();
        // Never allow untrusted shell metacharacters into cmd.exe; all dynamic user values are validated above.
        foreach (var x in args)
            if (x.Contains('"') || x.Contains('%') || x.Contains('!') || x.Contains('\r') || x.Contains('\n') || x.Contains('^') || x.Contains('&') || x.Contains('|') || x.Contains('<') || x.Contains('>'))
                throw new InvalidOperationException("参数中存在不支持的字符。");
        string command = "\"welink-cli.cmd\" " + string.Join(" ", args.Select(Quote));
        // /s /c expects outer quotes around a command containing quoted executable name.
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Arguments = "/d /v:off /s /c \"" + command + "\"";
        psi.Environment["NO_COLOR"] = "1";
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 WeLink CLI，请确认已安装 Node.js 和 welink-cli。");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(100));
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
            {
                var detail = (error.Length > 0 ? error : output).Trim();
                if (detail.Length > 450) detail = detail[..450] + "…";
                throw new InvalidOperationException("WeLink CLI 命令失败：" + detail);
            }
            return output.Trim().TrimStart('\uFEFF');
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("WeLink CLI 超时（100秒），请检查客户端登录、网络和授权状态。");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }
    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch { /* already exited */ }
    }

    /// <summary>
    /// Run the real WeLink CLI auth login flow in a visible native console.
    /// The console is required to render its QR code and wait for phone approval.
    /// This method does not return until the CLI login command exits.
    /// </summary>
    public async Task<int> LoginInteractiveAsync(CancellationToken cancellationToken)
    {
        string batchPath = Path.Combine(AppContext.BaseDirectory, "login_auth.bat");
        if (!File.Exists(batchPath))
            throw new FileNotFoundException("未找到登录脚本 login_auth.bat；请使用完整工程重新编译。", batchPath);

        // cmd.exe /d /c ""C:\path with spaces\login_auth.bat"" handles spaces and parentheses.
        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/d /c \"\"" + batchPath + "\"\"",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
        };
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动 WeLink 扫码登录控制台。");
        try
        {
            // No 100-second timeout here: QR scanning and mobile confirmation take as long as needed.
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }
}
