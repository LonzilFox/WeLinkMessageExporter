using System.ComponentModel;
using System.Text.Json.Serialization;

namespace WeLinkMessageExporter.Models;

public sealed class Conversation : INotifyPropertyChanged
{
    public string Type { get; set; } = "group";
    public string Id { get; set; } = "";
    private string _name = "";
    public string Name { get => _name; set { if (_name == value) return; _name = value ?? ""; Changed(nameof(Name)); } }
    private string _detail = "";
    public string Detail { get => _detail; set { if (_detail == value) return; _detail = value ?? ""; Changed(nameof(Detail)); } }
    public string GroupType { get; set; } = "";

    private bool _selected;
    public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; Changed(nameof(Selected)); } }

    // Local-only pin. The documented CLI recent-conversation response has no WeLink pin field.
    private bool _isPinned;
    public bool IsPinned { get => _isPinned; set { if (_isPinned == value) return; _isPinned = value; Changed(nameof(IsPinned)); Changed(nameof(PinSymbol)); Changed(nameof(PinToolTip)); } }

    // Persisted: latest known message timestamp and the ordering returned by the last recent-chat refresh.
    // RecentRank is zero-based; positive RecentBatchTicks means that this was in a recent-chat response.
    private long _lastMessageUnixMs;
    public long LastMessageUnixMs { get => _lastMessageUnixMs; set { if (_lastMessageUnixMs == value) return; _lastMessageUnixMs = value; Changed(nameof(LastMessageUnixMs)); Changed(nameof(LastActivityText)); } }
    private long _recentBatchTicks;
    public long RecentBatchTicks { get => _recentBatchTicks; set { if (_recentBatchTicks == value) return; _recentBatchTicks = value; Changed(nameof(LastActivityText)); } }
    public int RecentRank { get; set; } = int.MaxValue;

    [JsonIgnore] public string Key => Type + ":" + Id;
    [JsonIgnore] public string KindText => Type == "group" ? "群聊" : "私聊";
    [JsonIgnore] public string PinSymbol => IsPinned ? "★" : "☆";
    [JsonIgnore] public string PinToolTip => IsPinned ? "取消本地置顶" : "在本工具中置顶";
    [JsonIgnore] public string LastActivityText
    {
        get
        {
            if (LastMessageUnixMs > 946684800000L && LastMessageUnixMs < 4102444800000L)
            {
                try { return "最新消息 " + DateTimeOffset.FromUnixTimeMilliseconds(LastMessageUnixMs).ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm"); }
                catch (ArgumentOutOfRangeException) { }
            }
            return RecentBatchTicks > 0 ? "最近会话 · 时间待确认" : "消息时间未知";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
