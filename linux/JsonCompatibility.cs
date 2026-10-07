using System.Collections;
using System.Text.Json;

// Only the .NET 10 build uses this adapter. The Windows build keeps System.Web's serializer.
namespace System.Web.Script.Serialization;

public sealed class JavaScriptSerializer
{
    static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
    public int MaxJsonLength { get; set; } = 2097152;
    public int RecursionLimit { get; set; } = 64;
    public string Serialize(object value) => JsonSerializer.Serialize(value, Options);
    public T Deserialize<T>(string text)
    {
        if (text.Length > MaxJsonLength) throw new ArgumentException("JSON exceeds the size limit.");
        return JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions(Options) { MaxDepth = RecursionLimit });
    }
    public object DeserializeObject(string text)
    {
        if (text.Length > MaxJsonLength) throw new ArgumentException("JSON exceeds the size limit.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = RecursionLimit });
        return Convert(document.RootElement);
    }
    static object Convert(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Convert(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(Convert).ToArray(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt32(out int n) ? (object)n : e.TryGetInt64(out long l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}
