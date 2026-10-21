using System.Text.Json;

namespace WeLinkMessageExporter.Services;

/// <summary>
/// Normalizes known variations of WeLink history responses.
/// Only a positively identified empty result is an end-of-history marker.
/// </summary>
internal static class HistoryResponse
{
    internal sealed class UnrecognizedHistoryException : Exception
    {
        public UnrecognizedHistoryException(string detail) : base(detail) { }
    }

    private static readonly string[] MessageArrays =
    {
        "chatInfo", "chat_info", "messageList", "msgList", "messages", "messageInfo"
    };

    public static JsonElement[] ReadRows(JsonElement root)
    {
        foreach (string key in MessageArrays)
        {
            var arr = JsonHelpers.FindArray(root, key);
            if (arr.ValueKind == JsonValueKind.Array)
                return ValidateRows(arr, key);
        }

        // Certain responses put the message array directly into respData.
        var respData = JsonHelpers.Get(root, "respData", "data");
        if (respData.ValueKind == JsonValueKind.Array)
            return ValidateRows(respData, "respData");
        if (root.ValueKind == JsonValueKind.Array)
            return ValidateRows(root, "root");

        // Newer service wrappers sometimes use a different array key. Accept one only
        // when every non-empty entry has the documented msgId and serverSendTime fields.
        var unknownList = FindTypedMessageArray(respData);
        if (unknownList is not null) return unknownList;

        // Some JSON wrappers may embed respData as a serialized JSON string.
        if (respData.ValueKind == JsonValueKind.String)
        {
            string? nested = respData.GetString();
            if (!string.IsNullOrWhiteSpace(nested) &&
                (nested.TrimStart().StartsWith("{") || nested.TrimStart().StartsWith("[")))
            {
                try
                {
                    using var doc = JsonDocument.Parse(nested);
                    return ReadRows(doc.RootElement);
                }
                catch (JsonException) { /* surface the envelope diagnostics instead */ }
            }
        }

        // An empty response is only proven by an explicit zero-count indicator.
        // A null/missing respData with success=true is NOT proof of no history.
        foreach (var node in new[] { respData, root })
        {
            var total = JsonHelpers.Get(node, "msgTotalCount", "messageCount", "totalCount");
            if (total.ValueKind != JsonValueKind.Undefined &&
                total.ValueKind != JsonValueKind.Null &&
                JsonHelpers.Long(node, "msgTotalCount", "messageCount", "totalCount") == 0)
                return Array.Empty<JsonElement>();
        }

        var directMessage = JsonHelpers.Get(respData, "chatInfo", "chat_info");
        if (LooksLikeMessage(directMessage)) return new[] { directMessage.Clone() };

        throw new UnrecognizedHistoryException(DescribeShape(root));
    }

    private static JsonElement[]? FindTypedMessageArray(JsonElement node, int depth = 0)
    {
        if (depth > 5) return null;
        if (node.ValueKind == JsonValueKind.Array)
        {
            var entries = node.EnumerateArray().ToArray();
            if (entries.Length > 0 && entries.All(LooksLikeMessage))
                return entries.Select(row => row.Clone()).ToArray();
            foreach (var entry in entries.Take(12))
            {
                var match = FindTypedMessageArray(entry, depth + 1);
                if (match is not null) return match;
            }
        }
        else if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in node.EnumerateObject())
            {
                var match = FindTypedMessageArray(prop.Value, depth + 1);
                if (match is not null) return match;
            }
        }
        return null;
    }

    private static JsonElement[] ValidateRows(JsonElement rows, string source)
    {
        var array = rows.EnumerateArray().ToArray();
        if (array.Length == 0) return Array.Empty<JsonElement>();
        if (array.Any(row => !LooksLikeMessage(row)))
            throw new UnrecognizedHistoryException(source + " 数组包含缺少 msgId/serverSendTime 的记录");
        return array.Select(row => row.Clone()).ToArray();
    }

    private static bool LooksLikeMessage(JsonElement row) =>
        row.ValueKind == JsonValueKind.Object &&
        JsonHelpers.Long(row, "msgId") > 0 &&
        JsonHelpers.Long(row, "serverSendTime") > 0;

    // Describes only keys, types, status and bounded message counts, never content values.
    private static string DescribeShape(JsonElement root)
    {
        var data = JsonHelpers.Get(root, "respData", "data");
        var status = JsonHelpers.Str(root, "resultCode");
        var rootKeys = KeyNames(root);
        var dataKeys = KeyNames(data);
        return $"resultCode={SafeCode(status)}, 根对象={root.ValueKind} [{rootKeys}], respData={data.ValueKind} [{dataKeys}]";
    }

    private static string KeyNames(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return "";
        return string.Join(",", node.EnumerateObject().Take(16)
            .Select(x => x.Name.Length <= 45 ? x.Name : x.Name[..45] + "…"));
    }

    private static string SafeCode(string value) =>
        value.Length == 0 ? "(无)" : value.Length > 15 ? "(较长)" :
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? value : "(非标准)";
}
