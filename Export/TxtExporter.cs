using System.IO;
using System.Text;
using WeLinkMessageExporter.Models;
using WeLinkMessageExporter.Services;
namespace WeLinkMessageExporter.Export;
public static class TxtExporter
{
    public static async Task<string> SaveAsync(Conversation conversation, IReadOnlyList<ChatMessage> messages,
        IReadOnlyDictionary<string, ContactInfo> contacts, DateTime from, DateTime to, string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        string prefix = string.Join("", conversation.Name.Split(Path.GetInvalidFileNameChars())).Trim(' ', '.');
        if (prefix.Length == 0) prefix = conversation.Type == "group" ? "Group" : "Private";
        if (prefix.Length > 60) prefix = prefix[..60];
        string filename = $"{prefix}_{from:yyyyMMdd}-{to:yyyyMMdd}_{conversation.Id}.txt";
        string path = Path.Combine(directory, filename);
        int suffix = 2;
        while (File.Exists(path)) path = Path.Combine(directory, Path.GetFileNameWithoutExtension(filename) + $"_{suffix++}" + ".txt");
        // A unique staging file avoids colliding with a file left by a prior crash.
        // It is never presented to the user as a completed export.
        string temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // Fully close BOTH writer and file stream before File.Move on Windows.
            // Otherwise FileShare.None keeps the temporary file locked.
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                await writer.WriteLineAsync($"聊天记录：{conversation.Name}");
                await writer.WriteLineAsync($"类型：{conversation.KindText}");
                await writer.WriteLineAsync($"日期：{from:yyyy-MM-dd} 至 {to:yyyy-MM-dd}（北京时间 UTC+08:00）");
                await writer.WriteLineAsync($"消息数：{messages.Count}");
                await writer.WriteLineAsync(new string('─', 52));
                await writer.WriteLineAsync();
                foreach (var msg in messages)
                {
                    ct.ThrowIfCancellationRequested();
                    string author = contacts.TryGetValue(msg.Sender, out var profile) ? profile.Display(msg.Sender) : msg.Sender + "（未解析）";
                    await writer.WriteLineAsync($"{author} {MessageService.Beijing(msg.Time):yyyy-MM-dd HH:mm}");
                    if (!string.Equals(msg.ContentType, "TEXT_MSG", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync("[消息类型：" + msg.ContentType + "]");
                    await writer.WriteLineAsync(msg.Content); // Preserve original line breaks.
                    await writer.WriteLineAsync();
                }
                await writer.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path); // File handle is now closed.
            return path;
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}
