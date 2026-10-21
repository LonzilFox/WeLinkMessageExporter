using System.Text.Json;
using WeLinkMessageExporter.Models;

namespace WeLinkMessageExporter.Services;

/// <summary>
/// Reads history in reverse chronological order. A malformed response is never interpreted
/// as end of history; only an explicit empty page or a validated zero message count is.
/// </summary>
public sealed class MessageService
{
    private readonly CliService _cli;
    public MessageService(CliService cli) => _cli = cli;

    /// <summary>
    /// Lightweight timestamp probe for sorting. Does not fetch a full chat history and does
    /// not store message bodies. Failure to probe never changes the export completeness rules.
    /// </summary>
    public async Task<long> GetLatestMessageTimeAsync(Conversation conversation, CancellationToken ct)
    {
        var args = BuildHistoryArgs(conversation, 0, 5, false);
        string raw = await _cli.ExecuteAsync(args, ct);
        using var doc = JsonHelpers.Parse(raw);
        JsonHelpers.CheckResult(doc.RootElement);
        var rows = HistoryResponse.ReadRows(doc.RootElement);
        long newest = 0;
        foreach (var row in rows)
        {
            long ms = JsonHelpers.Long(row, "serverSendTime");
            if (ms > 946684800000L && ms < 4102444800000L)
                newest = Math.Max(newest, ms);
        }
        return newest;
    }

    public async Task<List<ChatMessage>> GetRangeAsync(Conversation conversation, DateTime start, DateTime end,
        int pageSize, Action<string> report, CancellationToken ct)
    {
        var found = new Dictionary<long, ChatMessage>();
        long cursor = 0;
        var previousCursors = new HashSet<long>();
        bool attemptedBoundaryRecovery = false;
        int effectivePageSize = pageSize;
        long firstDayUnix = new DateTimeOffset(DateTime.SpecifyKind(start.Date, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();

        for (int page = 1; page <= 50000; page++)
        {
            ct.ThrowIfCancellationRequested();
            var response = await ReadPageWithRecoveryAsync(conversation, cursor, effectivePageSize, report, ct);
            effectivePageSize = response.PageSize; // Keep using a smaller supported page size after a recovery.
            var rows = response.Rows;
            if (rows.Length == 0)
            {
                report(found.Count == 0
                    ? $"{conversation.Name}：接口确认未返回历史消息；若该会话最近有消息，请检查所选账号与 WeLink 客户端中的会话是否一致。"
                    : $"{conversation.Name}：已到达可读取历史的末尾。");
                return Filter(found, start, end);
            }

            long smallestId = long.MaxValue;
            long oldestTime = long.MaxValue;
            int newItems = 0;
            foreach (var row in rows)
            {
                long msgId = JsonHelpers.Long(row, "msgId");
                long unixMillis = JsonHelpers.Long(row, "serverSendTime");
                if (msgId <= 0 || unixMillis <= 0)
                    throw new InvalidOperationException("接口返回了不完整的消息 ID/时间戳，无法保证消息完整性。请查看运行日志。");
                smallestId = Math.Min(smallestId, msgId);
                oldestTime = Math.Min(oldestTime, unixMillis);
                if (found.ContainsKey(msgId)) continue;

                var contentValue = JsonHelpers.Get(row, "content");
                string content = contentValue.ValueKind switch
                {
                    JsonValueKind.String => contentValue.GetString() ?? "",
                    JsonValueKind.Null or JsonValueKind.Undefined => "",
                    _ => contentValue.GetRawText()
                };
                string contentType = JsonHelpers.Str(row, "contentType");
                if (content.Length == 0 && contentType != "TEXT_MSG") content = row.GetRawText();
                found.Add(msgId, new ChatMessage
                {
                    Id = msgId,
                    Sender = JsonHelpers.Str(row, "sender"),
                    Content = content,
                    ContentType = contentType,
                    Time = DateTimeOffset.FromUnixTimeMilliseconds(unixMillis)
                });
                newItems++;
            }
            report($"{conversation.Name} · 第 {page} 页 · 已读取 {found.Count:N0} 条");
            if (oldestTime < firstDayUnix)
                return Filter(found, start, end);

            if (smallestId <= 0) throw new InvalidOperationException("返回了无效消息 ID，无法完成分页。");
            if (newItems == 0)
            {
                // Some WeLink versions paginate inclusively and repeat the oldest row.
                // Confirm the boundary exactly once, rather than silently dropping messages.
                if (!attemptedBoundaryRecovery && cursor > 1)
                {
                    attemptedBoundaryRecovery = true;
                    cursor--;
                    report($"{conversation.Name}：分页边界重复，正在进一步确认历史末尾…");
                    continue;
                }
                throw new InvalidOperationException("分页边界重复且无法继续；为避免漏消息，未生成文件。");
            }
            if (cursor > 0 && smallestId > cursor)
                throw new InvalidOperationException("历史消息游标反向移动，无法保证数据完整。");
            if (!previousCursors.Add(smallestId))
                throw new InvalidOperationException("分页游标循环，无法确认历史消息完整性。");
            attemptedBoundaryRecovery = false;
            cursor = smallestId;
        }
        throw new InvalidOperationException("达到安全分页上限，无法保证完整性，未生成文件。");
    }

    private async Task<(JsonElement[] Rows, int PageSize)> ReadPageWithRecoveryAsync(
        Conversation conversation, long cursor, int pageSize, Action<string> report, CancellationToken ct)
    {
        // The official documentation does not specify a separate API for new private chats.
        // Some CLI/backend versions return a successful envelope with no chatInfo field.
        // In that case try a smaller query with an explicit older-direction hint, then
        // one unchanged retry. Never treat an unrecognised envelope as an empty page.
        var attempts = new List<(int Size, bool Direction, string Label)>
        {
            (pageSize, cursor > 0, "标准查询")
        };
        if (cursor == 0)
        {
            attempts.Add((Math.Min(pageSize, 20), true, "兼容查询（向旧消息、最多20条）"));
            attempts.Add((Math.Min(pageSize, 20), false, "小页查询（最多20条）"));
        }
        else
        {
            attempts.Add((Math.Min(pageSize, 20), true, "小页兼容查询（最多20条）"));
            attempts.Add((pageSize, true, "重试当前游标"));
        }

        var errors = new List<string>();
        bool confirmedEmpty = false;
        int emptyPageSize = pageSize;
        foreach (var attempt in attempts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var args = BuildHistoryArgs(conversation, cursor, attempt.Size, attempt.Direction);
                string raw = await _cli.ExecuteAsync(args, ct);
                using var doc = JsonHelpers.Parse(raw);
                JsonHelpers.CheckResult(doc.RootElement);
                var rows = HistoryResponse.ReadRows(doc.RootElement);
                if (rows.Length == 0 && !confirmedEmpty && attempt.Size > 20)
                {
                    // A large first page can be misreported as empty by older gateways.
                    // Confirm once with a smaller request before concluding there is no history.
                    confirmedEmpty = true;
                    emptyPageSize = attempt.Size;
                    report($"{conversation.Name}：本页返回 0 条，正在使用小页复核以避免漏消息…");
                    continue;
                }
                if (errors.Count > 0)
                    report($"{conversation.Name}：使用「{attempt.Label}」成功识别历史消息结构。");
                return (rows, attempt.Size);
            }
            catch (OperationCanceledException) { throw; }
            catch (HistoryResponse.UnrecognizedHistoryException ex)
            {
                errors.Add(attempt.Label + "：" + ex.Message);
                if (errors.Count == 1)
                    report($"{conversation.Name}：历史记录返回格式与常规会话不同，正在尝试兼容查询…");
            }
            catch (Exception ex) when ((errors.Count > 0 || confirmedEmpty) && ex is not OperationCanceledException)
            {
                // A compatibility variant can be unsupported by some CLI releases.
                // Keep trying the documented original query, but preserve first error.
                errors.Add(attempt.Label + "：" + Short(ex.Message));
            }
            if (errors.Count > 0) await Task.Delay(250, ct);
        }

        if (confirmedEmpty)
        {
            report($"{conversation.Name}：服务端明确返回空页；兼容查询未获取更多记录。");
            return (Array.Empty<JsonElement>(), emptyPageSize);
        }

        // No message body, token or raw response is included in diagnostics.
        throw new InvalidOperationException(
            "CLI 返回成功，但历史消息字段无法确认；已尝试兼容读取，仍不能保证完整性。" +
            "请检查该私聊是否有消息访问权限、确认会话账号，然后在 PowerShell 使用 query-history-message 测试。" +
            " 响应结构：" + string.Join("；", errors));
    }

    private static List<string> BuildHistoryArgs(Conversation conversation, long cursor, int size, bool includeDirection)
    {
        var args = new List<string>
        {
            "im", "query-history-message",
            conversation.Type == "group" ? "--group-id" : "--user-account",
            CliService.ValidateId(conversation.Id),
            "--query-count", size.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (cursor > 0)
        {
            args.Add("--message-id");
            args.Add(cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (includeDirection)
        {
            args.Add("--query-direction");
            args.Add("0");
        }
        return args;
    }

    private static string Short(string message) => message.Length <= 200 ? message : message[..200] + "…";
    private static List<ChatMessage> Filter(Dictionary<long, ChatMessage> found, DateTime start, DateTime end) =>
        found.Values.Where(x => InRange(x.Time, start, end)).OrderBy(x => x.Time).ThenBy(x => x.Id).ToList();

    public static DateTime Beijing(DateTimeOffset time) => time.ToOffset(TimeSpan.FromHours(8)).DateTime;
    private static bool InRange(DateTimeOffset time, DateTime start, DateTime end)
    {
        var bj = Beijing(time);
        return bj >= start.Date && bj < end.Date.AddDays(1);
    }
}
