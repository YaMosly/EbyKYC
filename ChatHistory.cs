using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Relay;

internal sealed record ChatEntry(string Key, string Title, string Project, long Updated)
{
    public string Detail
    {
        get
        {
            if (Updated <= 0)
                return Project;
            var date = DateTimeOffset.FromUnixTimeSeconds(Updated).ToLocalTime().ToString("dd.MM HH:mm");
            return string.IsNullOrWhiteSpace(Project) ? date : $"{Project} · {date}";
        }
    }
}

internal sealed record ChatMessage(string Role, string Text);
internal sealed record ChatTranscript(ChatMessage[] Messages, bool Limited);

internal static class ChatHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<string> Reader = new(() =>
    {
        using var stream = typeof(ChatHistory).Assembly.GetManifestResourceStream("ClaudeHistory.py")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    });

    public static Task<ChatEntry[]> ListAsync(SshProfile profile, CancellationToken token) =>
        RequestAsync<ChatEntry[]>(profile, "list", token);

    public static Task<ChatTranscript> ReadAsync(SshProfile profile, string key, CancellationToken token)
    {
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9_-]{1,512}\z"))
            throw new InvalidDataException("Сервер вернул неверный идентификатор чата.");
        return RequestAsync<ChatTranscript>(profile, $"read {key}", token);
    }

    private static async Task<T> RequestAsync<T>(SshProfile profile, string arguments, CancellationToken token)
    {
        var result = await Ssh.RunAsync(profile, $"exec python3 - {arguments}", Reader.Value, token);
        if (result.Code != 0)
            throw new IOException(result.Failure);
        using var document = JsonDocument.Parse(result.Output);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("error", out var error))
            throw new IOException(error.GetString());
        return document.RootElement.Deserialize<T>(JsonOptions)
            ?? throw new InvalidDataException("Сервер не вернул данные чата.");
    }
}
