namespace WeLinkMessageExporter.Models;
public sealed class ChatMessage
{
    public long Id { get; init; }
    public string Sender { get; init; } = "";
    public string Content { get; init; } = "";
    public string ContentType { get; init; } = "TEXT_MSG";
    public DateTimeOffset Time { get; init; }
}
public sealed class ContactInfo
{
    public string Name { get; init; } = "";
    public string EmployeeNumber { get; init; } = "";
    public bool Resolved => !string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(EmployeeNumber);
    public string Display(string fallback) => !Resolved ? fallback + "（未解析）" : string.IsNullOrWhiteSpace(EmployeeNumber) ? Name + " " + fallback : (string.IsNullOrWhiteSpace(Name) ? fallback : Name) + " " + EmployeeNumber;
}
