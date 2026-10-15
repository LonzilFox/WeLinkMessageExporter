using System.IO;
using System.Text.Json;
using WeLinkMessageExporter.Models;
namespace WeLinkMessageExporter.Services;
public static class ConversationStore
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeLinkMessageExporter", "conversations.json");
    public static List<Conversation> Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<Conversation>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { /* ignore corrupt cache */ }
        return new();
    }
    public static void Save(IEnumerable<Conversation> conversations)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(conversations.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* folder ACL may prohibit cache */ }
    }
}
