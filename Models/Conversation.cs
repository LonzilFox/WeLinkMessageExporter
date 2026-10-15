using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
namespace WeLinkMessageExporter.Models;
public sealed class Conversation : INotifyPropertyChanged
{
    public string Type { get; set; } = "group";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public string GroupType { get; set; } = "";
    private bool _selected;
    public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
    [JsonIgnore] public string Key => Type + ":" + Id;
    [JsonIgnore] public string KindText => Type == "group" ? "群聊" : "私聊";
    public event PropertyChangedEventHandler? PropertyChanged;
}
