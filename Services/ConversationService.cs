using WeLinkMessageExporter.Models;
using System.Text.Json;
namespace WeLinkMessageExporter.Services;
public sealed class ConversationService
{
    private readonly CliService _cli;
    public ConversationService(CliService cli) { _cli = cli; }

    public async Task<List<Conversation>> RecentAsync(int count, CancellationToken ct)
    {
        string raw = await _cli.ExecuteAsync(new[] { "im", "query-recent-conversation", "--count", count.ToString() }, ct);
        using var doc = JsonHelpers.Parse(raw);
        JsonHelpers.CheckResult(doc.RootElement);
        var arr = JsonHelpers.FindArray(doc.RootElement, "conversation_info");
        if (arr.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("无法读取 conversation_info，CLI 返回格式可能已改变。");
        var result = new List<Conversation>();
        foreach (var row in arr.EnumerateArray())
        {
            string type = JsonHelpers.Str(row, "recent_conversation_type");
            if (type == "CHAT_TYPE_GROUP_MSG")
            {
                var id = JsonHelpers.Str(row, "group_id");
                if (string.IsNullOrWhiteSpace(id) || id == "0") continue;
                string name = JsonHelpers.Str(row, "group_name");
                result.Add(new Conversation { Type = "group", Id = id, Name = string.IsNullOrWhiteSpace(name) ? id : name, Detail = "群 ID: " + id, GroupType = JsonHelpers.Str(row, "group_type") });
            }
            else if (type == "CHAT_TYPE_P2P_MSG")
            {
                var id = JsonHelpers.Str(row, "target_account");
                if (string.IsNullOrWhiteSpace(id)) continue;
                string name = JsonHelpers.Str(row, "native_name", "staff_name");
                string staff = JsonHelpers.Str(row, "staff_name");
                result.Add(new Conversation { Type = "private", Id = id, Name = string.IsNullOrWhiteSpace(name) ? id : name, Detail = string.IsNullOrWhiteSpace(staff) ? id : staff + " · " + id });
            }
        }
        return result;
    }

    public async Task<List<Conversation>> SearchAsync(string keyword, CancellationToken ct)
    {
        keyword = CliService.ValidateSearch(keyword);
        var results = new Dictionary<string, Conversation>();
        // Search is the documented discoverability fallback: not a whole-account conversation enumeration.
        var errors = new List<string>();
        foreach (var type in new[] { "group", "person" })
        {
            try
            {
                string raw = await _cli.ExecuteAsync(new[] { "search", type, "--text", keyword, "--page-size", "100" }, ct);
                using var doc = JsonHelpers.Parse(raw);
                JsonHelpers.CheckResult(doc.RootElement);
                foreach (var row in JsonHelpers.EnumerateObjects(doc.RootElement))
                {
                    if (type == "group")
                    {
                        string name = JsonHelpers.Str(row, "group_name", "groupName");
                        string id = JsonHelpers.Str(row, "group_id", "groupId", "groupID");
                        if (id.Length == 0 && name.Length > 0) id = JsonHelpers.Str(row, "id");
                        if (string.IsNullOrWhiteSpace(id) || id == "0") continue;
                        var conv = new Conversation { Type = "group", Id = id, Name = string.IsNullOrWhiteSpace(name) ? id : name, Detail = "群 ID: " + id };
                        results[conv.Key] = conv;
                    }
                    else
                    {
                        string account = JsonHelpers.Str(row, "w3account", "w3Account", "target_account", "userAccount");
                        string name = JsonHelpers.Str(row, "native_name", "staff_name", "staffName", "userName", "name", "displayName", "cnName");
                        if (string.IsNullOrWhiteSpace(account)) continue;
                        var employee = JsonHelpers.Str(row, "employeeNumber", "employee_number");
                        var conv = new Conversation { Type = "private", Id = account, Name = string.IsNullOrWhiteSpace(name) ? account : name, Detail = (employee.Length > 0 ? employee + " · " : "") + account };
                        results[conv.Key] = conv;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add(type + "：" + ex.Message); }
        }
        if (errors.Count == 2) throw new InvalidOperationException(string.Join("；", errors));
        return results.Values.ToList();
    }
}
