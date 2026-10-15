using System.IO;
using System.Text.Json;
using WeLinkMessageExporter.Models;
namespace WeLinkMessageExporter.Services;
public sealed class ContactService
{
    private readonly CliService _cli;
    private readonly Dictionary<string, ContactInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string CachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeLinkMessageExporter", "contacts.json");
    public ContactService(CliService cli)
    {
        _cli = cli;
        try
        {
            if (File.Exists(CachePath))
                foreach (var item in JsonSerializer.Deserialize<Dictionary<string, ContactInfo>>(File.ReadAllText(CachePath)) ?? new()) _cache[item.Key] = item.Value;
        }
        catch { /* corrupted cache shouldn't prevent exporting */ }
    }
    public async Task<Dictionary<string, ContactInfo>> ResolveAsync(IEnumerable<string> senders, Action<string> report, CancellationToken ct)
    {
        var list = senders.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int index = 0;
        foreach (string account in list)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            if (_cache.TryGetValue(account, out var cached) && cached.Resolved) continue;
            try
            {
                string json = await _cli.ExecuteAsync(new[] { "contact", "detail", CliService.ValidateId(account) }, ct);
                using var doc = JsonHelpers.Parse(json);
                JsonHelpers.CheckResult(doc.RootElement);
                ContactInfo result = ParseContact(doc.RootElement);
                if (result.Resolved) _cache[account] = result;
                else _cache[account] = new ContactInfo();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _cache[account] = new ContactInfo(); report("联系人 " + account + " 未解析：" + ex.Message); }
            if (index % 5 == 0) report($"联系人解析 {index}/{list.Count}");
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* no write access to cache */ }
        return list.ToDictionary(x => x, x => _cache.GetValueOrDefault(x) ?? new ContactInfo(), StringComparer.OrdinalIgnoreCase);
    }
    private static ContactInfo ParseContact(JsonElement root)
    {
        string bestName = "";
        string bestNumber = "";
        foreach (var node in JsonHelpers.EnumerateObjects(root))
        {
            string name = JsonHelpers.Str(node, "nativeName", "native_name", "displayName", "display_name", "chineseName", "cnName", "userName", "staffName", "staff_name", "name");
            string number = JsonHelpers.Str(node, "employeeNumber", "employee_number", "staffNo", "employeeNo", "staffNumber");
            if (name.Length > 0 && number.Length > 0) return new ContactInfo { Name = name, EmployeeNumber = number };
            if (bestName.Length == 0 && name.Length > 0) bestName = name;
            if (bestNumber.Length == 0 && number.Length > 0) bestNumber = number;
        }
        return new ContactInfo { Name = bestName, EmployeeNumber = bestNumber };
    }
}
