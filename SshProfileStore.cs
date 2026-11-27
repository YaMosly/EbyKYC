using System.IO;
using System.Text.Json;

namespace Relay;

internal static class SshProfileStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EbyKYC");
    private static readonly string FilePath = Path.Combine(SettingsDirectory, "connection.json");

    public static SshProfile? Load()
    {
        try
        {
            var profile = JsonSerializer.Deserialize<SshProfile>(File.ReadAllText(FilePath));
            return profile is { Host: { } host, Session: { } session } &&
                SshProfile.IsHost(host) && SshProfile.IsSession(session) ? profile : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static bool TrySave(SshProfile profile)
    {
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile));
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    public static string[] LoadHiddenChatKeys(string host)
    {
        try
        {
            var path = Path.Combine(SettingsDirectory, "hidden-chats.json");
            var hidden = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path));
            return hidden is not null && hidden.TryGetValue(host.ToLowerInvariant(), out var keys)
                ? keys ?? []
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
