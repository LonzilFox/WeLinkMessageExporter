using WeLinkMessageExporter.Models;
using System.Text.Json;
using System.Globalization;
namespace WeLinkMessageExporter.Services;
public sealed class ConversationService
{
    private readonly CliService _cli;
    public ConversationService(CliService cli) { _cli = cli; }

    // Field names only (never values) are retained for diagnostics when the CLI
    // does not expose a native pin indicator.
    public string LastRecentFieldNames { get; private set; } = "";
    public string LastRecentPinFieldNames { get; private set; } = "";

    public async Task<List<Conversation>> RecentAsync(int count, CancellationToken ct)
    {
        string raw = await _cli.ExecuteAsync(new[] { "im", "query-recent-conversation", "--count", count.ToString() }, ct);
        using var doc = JsonHelpers.Parse(raw);
        JsonHelpers.CheckResult(doc.RootElement);
        var arr = JsonHelpers.FindArray(doc.RootElement, "conversation_info");
        if (arr.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("无法读取 conversation_info，CLI 返回格式可能已改变。");
        var result = new List<Conversation>();
        var rawRows = arr.EnumerateArray().ToList();
        var allFieldNames = rawRows
            .SelectMany(row => JsonHelpers.EnumerateObjects(row))
            .SelectMany(obj => obj.ValueKind == JsonValueKind.Object ? obj.EnumerateObject().Select(p => p.Name) : Enumerable.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        LastRecentFieldNames = string.Join(", ", allFieldNames);
        string[] pinAliases = GetPinAliases();
        LastRecentPinFieldNames = string.Join(", ", allFieldNames.Where(name => pinAliases.Contains(name, StringComparer.OrdinalIgnoreCase)));
        foreach (var row in rawRows)
        {
            string type = JsonHelpers.Str(row, "recent_conversation_type");
            if (type == "CHAT_TYPE_GROUP_MSG")
            {
                var id = JsonHelpers.Str(row, "group_id");
                if (string.IsNullOrWhiteSpace(id) || id == "0") continue;
                string name = JsonHelpers.Str(row, "group_name");
                var conversation = new Conversation { Type = "group", Id = id, Name = string.IsNullOrWhiteSpace(name) ? id : name, Detail = "群 ID: " + id, GroupType = JsonHelpers.Str(row, "group_type"), LastMessageUnixMs = ReadRecentTime(row) };
                ApplyPin(conversation, row);
                result.Add(conversation);
            }
            else if (type == "CHAT_TYPE_P2P_MSG")
            {
                var id = JsonHelpers.Str(row, "target_account");
                if (string.IsNullOrWhiteSpace(id)) continue;
                string name = JsonHelpers.Str(row, "native_name", "staff_name");
                string staff = JsonHelpers.Str(row, "staff_name");
                var conversation = new Conversation { Type = "private", Id = id, Name = string.IsNullOrWhiteSpace(name) ? id : name, Detail = string.IsNullOrWhiteSpace(staff) ? id : staff + " · " + id, LastMessageUnixMs = ReadRecentTime(row) };
                ApplyPin(conversation, row);
                result.Add(conversation);
            }
        }
        return result;
    }

    private static string[] GetPinAliases() => new[]
    {
        "is_pinned", "isPinned", "pinned", "pinnedFlag", "pinned_flag", "pin_flag", "pinStatus", "pin_status",
        "is_top", "isTop", "topFlag", "top_flag", "topStatus", "top_status",
        "isTopChat", "is_top_chat", "isTopConversation", "is_top_conversation",
        "isPinnedConversation", "is_pinned_conversation", "stickTop", "stick_top", "isStickTop", "is_stick_top",
        "isStick", "is_sticky", "isToped", "stickTopStatus"
    };

    private static (bool known, bool pinned) ReadWeLinkPinned(JsonElement row)
    {
        // The published CLI sample does not document a pin field. Read it only when
        // the backend actually returns an explicit flag; never synthesize local pins.
        string[] aliases = GetPinAliases();
        foreach (var item in JsonHelpers.EnumerateObjects(row))
        {
            var value = JsonHelpers.Get(item, aliases);
            if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind == JsonValueKind.True) return (true, true);
            if (value.ValueKind == JsonValueKind.False) return (true, false);
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number is 0 or 1)
                return (true, number == 1);
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()?.Trim() ?? "";
                if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text is "1" or "Y" or "yes" or "on" or "pinned" or "top") return (true, true);
                if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text is "0" or "N" or "no" or "off" or "unpinned" or "normal") return (true, false);
            }
        }
        return (false, false);
    }

    private static void ApplyPin(Conversation conversation, JsonElement row)
    {
        var pin = ReadWeLinkPinned(row);
        conversation.PinStatusKnown = pin.known;
        conversation.IsWeLinkPinned = pin.known && pin.pinned;
    }

    // Some CLI/backend builds may expose a last-message field. The supplied CLI example does not,
    // so this is only an opportunistic fast path and never substitutes an unknown time with "now".
    private static long ReadRecentTime(JsonElement row)
    {
        var value = JsonHelpers.Get(row, "lastMsgTime", "lastMessageTime", "last_msg_time",
            "last_message_time", "lastSendTime", "lastMessageSendTime", "serverSendTime",
            "latestMessageTime", "latest_msg_time", "recentTime");
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return 0;
        long numeric;
        bool parsedNumber = value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt64(out numeric)
            : long.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : "", out numeric);
        if (parsedNumber)
        {
            if (numeric is > 946684800 and < 4102444800L) numeric *= 1000;
            return numeric is > 946684800000L and < 4102444800000L ? numeric : 0;
        }
        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed)) return parsed.ToUnixTimeMilliseconds();
        return 0;
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
