using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace RemoteScreen;

sealed class RememberedDevices
{
    static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteScreen", "devices.json");
    public static TimeSpan Lifetime { get; } = TimeSpan.FromDays(365);

    readonly object _gate = new();
    Dictionary<string, DateTimeOffset> _devices = File.Exists(StorePath)
        ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(StorePath)) ?? []
        : [];

    public bool Contains(string? token)
    {
        if (token?.Length != 43)
        {
            return false;
        }

        string hash = Hash(token);
        lock (_gate)
        {
            return _devices.TryGetValue(hash, out DateTimeOffset expires) && expires > DateTimeOffset.UtcNow;
        }
    }

    public string Add()
    {
        string token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            Dictionary<string, DateTimeOffset> updated = _devices
                .Where(entry => entry.Value > now)
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            updated.Add(Hash(token), now + Lifetime);
            Save(updated);
            _devices = updated;
        }

        return token;
    }

    // Call after the sharing session is disposed, so no request can write a new approval.
    internal static void ClearSavedApprovals()
    {
        try
        {
            File.Delete(StorePath);
        }
        catch (DirectoryNotFoundException)
        {
            // No approvals have been saved yet.
        }
    }

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    static void Save(Dictionary<string, DateTimeOffset> devices)
    {
        string directory = Path.GetDirectoryName(StorePath)
            ?? throw new InvalidOperationException("端末情報の保存先が見つかりません。");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $"devices-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(devices));
            File.Move(temporary, StorePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
