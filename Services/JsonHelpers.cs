using System.Text.Json;
namespace WeLinkMessageExporter.Services;
internal static class JsonHelpers
{
    public static JsonDocument Parse(string raw)
    {
        try { return JsonDocument.Parse(raw); }
        catch (JsonException)
        {
            int start = raw.IndexOf('{'); int end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) throw new InvalidOperationException("CLI 没有返回可解析的 JSON，可能是登录过期或版本不兼容。");
            return JsonDocument.Parse(raw.Substring(start, end - start + 1));
        }
    }
    public static JsonElement Get(JsonElement parent, params string[] keys)
    {
        if (parent.ValueKind != JsonValueKind.Object) return default;
        foreach (var k in keys)
            foreach (var p in parent.EnumerateObject())
                if (p.Name.Equals(k, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return default;
    }
    public static string Str(JsonElement obj, params string[] keys)
    {
        var el = Get(obj, keys);
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.GetRawText(),
            _ => ""
        };
    }
    public static long Long(JsonElement obj, params string[] keys)
    {
        var el = Get(obj, keys);
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n) ? n
            : long.TryParse(el.ValueKind == JsonValueKind.String ? el.GetString() : "", out n) ? n : 0;
    }
    public static JsonElement FindArray(JsonElement node, string key, int depth = 0)
    {
        if (depth > 6) return default;
        if (node.ValueKind == JsonValueKind.Object)
        {
            var first = Get(node, key);
            if (first.ValueKind == JsonValueKind.Array) return first;
            foreach (var field in node.EnumerateObject())
            {
                var found = FindArray(field.Value, key, depth + 1);
                if (found.ValueKind == JsonValueKind.Array) return found;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray())
            {
                var found = FindArray(child, key, depth + 1);
                if (found.ValueKind == JsonValueKind.Array) return found;
            }
        return default;
    }
    public static IEnumerable<JsonElement> EnumerateObjects(JsonElement root, int depth = 0)
    {
        if (depth > 6) yield break;
        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
            foreach (var prop in root.EnumerateObject())
                foreach (var item in EnumerateObjects(prop.Value, depth + 1)) yield return item;
        }
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var value in root.EnumerateArray())
                foreach (var item in EnumerateObjects(value, depth + 1)) yield return item;
    }
    public static void CheckResult(JsonElement root)
    {
        var success = Get(root, "success");
        if (success.ValueKind == JsonValueKind.False) throw new InvalidOperationException("WeLink 返回失败：" + Str(root, "message", "errorMessage"));
        string result = Str(root, "resultCode");
        if (result.Length > 0 && result != "0") throw new InvalidOperationException("WeLink 返回失败：" + result + " " + Str(root, "resultContext"));
        var error = Get(root, "error");
        string code = Str(error, "error_code");
        if (code.Length > 0 && code != "IM.0000") throw new InvalidOperationException("WeLink 返回失败：" + code + " " + Str(error, "error_msg"));
    }
}
