using WeLinkMessageExporter.Models;
using System.Text.Json;
namespace WeLinkMessageExporter.Services;
public sealed class MessageService
{
    private readonly CliService _cli;
    public MessageService(CliService cli) => _cli = cli;
    public async Task<List<ChatMessage>> GetRangeAsync(Conversation conversation, DateTime start, DateTime end,
        int pageSize, Action<string> report, CancellationToken ct)
    {
        var found = new Dictionary<long, ChatMessage>();
        long cursor = 0;
        var previousCursors = new HashSet<long>();
        bool attemptedBoundaryRecovery = false;
        for (int page = 1; page <= 50000; page++)
        {
            ct.ThrowIfCancellationRequested();
            var args = new List<string> { "im", "query-history-message", conversation.Type == "group" ? "--group-id" : "--user-account", CliService.ValidateId(conversation.Id), "--query-count", pageSize.ToString() };
            if (cursor > 0)
            {
                args.Add("--message-id"); args.Add(cursor.ToString());
                args.Add("--query-direction"); args.Add("0");
            }
            var raw = await _cli.ExecuteAsync(args, ct);
            using var doc = JsonHelpers.Parse(raw);
            JsonHelpers.CheckResult(doc.RootElement);
            var arr = JsonHelpers.FindArray(doc.RootElement, "chatInfo");
            if (arr.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("缺少 respData.chatInfo；不生成可能缺失消息的文件。");
            var rows = arr.EnumerateArray().ToArray();
            if (rows.Length == 0) return found.Values.Where(x => InRange(x.Time, start, end)).OrderBy(x => x.Time).ThenBy(x => x.Id).ToList();
            long smallestId = long.MaxValue;
            long oldestTime = long.MaxValue;
            int newItems = 0;
            foreach (var row in rows)
            {
                long msgId = JsonHelpers.Long(row, "msgId");
                long unixMillis = JsonHelpers.Long(row, "serverSendTime");
                if (msgId <= 0 || unixMillis <= 0) throw new InvalidOperationException("消息缺少 msgId 或 serverSendTime；导出已停止以避免漏数据。");
                smallestId = Math.Min(smallestId, msgId);
                oldestTime = Math.Min(oldestTime, unixMillis);
                if (!found.ContainsKey(msgId))
                {
                    var contentValue = JsonHelpers.Get(row, "content");
                    string content = contentValue.ValueKind switch
                    {
                        JsonValueKind.String => contentValue.GetString() ?? "",
                        JsonValueKind.Null or JsonValueKind.Undefined => "",
                        _ => contentValue.GetRawText()
                    };
                    string contentType = JsonHelpers.Str(row, "contentType");
                    // Don't silently discard unknown message types or payload-only rich media.
                    if (content.Length == 0 && contentType != "TEXT_MSG") content = row.GetRawText();
                    found.Add(msgId, new ChatMessage { Id = msgId, Sender = JsonHelpers.Str(row, "sender"), Content = content, ContentType = contentType, Time = DateTimeOffset.FromUnixTimeMilliseconds(unixMillis) });
                    newItems++;
                }
            }
            report($"{conversation.Name} · 第 {page} 页 · 已读取 {found.Count:N0} 条");
            // Dates are Beijing time (UTC+8), with inclusive end-date and start-date.
            var firstDayUnix = new DateTimeOffset(DateTime.SpecifyKind(start.Date, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
            if (oldestTime < firstDayUnix)
            {
                // The page has crossed the requested inclusive start day.
                // Return the complete range collected so far instead of falling
                // through to the pagination-limit error.
                return found.Values.Where(x => InRange(x.Time, start, end))
                    .OrderBy(x => x.Time).ThenBy(x => x.Id).ToList();
            }
            if (smallestId <= 0) throw new InvalidOperationException("返回了无效消息 ID，无法完成分页。");
            if (newItems == 0)
            {
                // An inclusive pagination endpoint may repeat its final message forever.
                // Retry from the immediately preceding ID once; if that is empty, we've exhausted history.
                if (!attemptedBoundaryRecovery && cursor > 1)
                {
                    attemptedBoundaryRecovery = true;
                    cursor--;
                    report("检测到分页边界重复，正在确认是否已到最早消息…");
                    continue;
                }
                throw new InvalidOperationException("分页边界重复且无法继续，不生成可能缺失历史的文件。");
            }
            if (cursor > 0 && smallestId > cursor) throw new InvalidOperationException("历史消息游标反向移动，无法保证数据完整。");
            if (!previousCursors.Add(smallestId)) throw new InvalidOperationException("分页游标循环，无法确认历史消息完整性。");
            attemptedBoundaryRecovery = false;
            cursor = smallestId;
        }
        throw new InvalidOperationException("达到安全分页上限，无法保证数据完整，不会导出不完整文件。");
    }
    public static DateTime Beijing(DateTimeOffset time) => time.ToOffset(TimeSpan.FromHours(8)).DateTime;
    private static bool InRange(DateTimeOffset time, DateTime start, DateTime end)
    {
        var bj = Beijing(time);
        return bj >= start.Date && bj < end.Date.AddDays(1);
    }
}
