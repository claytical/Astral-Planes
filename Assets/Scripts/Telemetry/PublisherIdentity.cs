using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

// Anonymous Flight Plan publication credentials, persisted once and reused for every future
// upload. Deliberately has no relationship to Steam identity, install ID, or any other machine
// identifier — publisher_id/publisher_token exist solely so the Flight Plan server can recognize
// repeat contributions from the same anonymous publisher and let them later withdraw all of them.
//
// Never regenerated automatically (including on a server-side credential rejection) — see
// PUBLISH-25 in MazeTelemetryUploader for why an HTTP 403 must not touch these values.
public static class PublisherIdentity
{
    [Serializable]
    private class PublisherIdentityFile
    {
        public string PublisherId;
        public string PublisherToken;
    }

    private const int TokenBytes = 32;

    private static string FilePath =>
        Application.persistentDataPath + "/GreaterMazeTelemetry/publisher_identity.json";

    private static PublisherIdentityFile _cached;

    public static bool TryGetExisting(out string publisherId, out string publisherToken)
    {
        var identity = Load();
        if (identity != null)
        {
            publisherId = identity.PublisherId;
            publisherToken = identity.PublisherToken;
            return true;
        }

        publisherId = null;
        publisherToken = null;
        return false;
    }

    public static string PublisherId => GetOrCreate().PublisherId;
    public static string PublisherToken => GetOrCreate().PublisherToken;

    private static PublisherIdentityFile GetOrCreate()
    {
        var existing = Load();
        if (existing != null) return existing;

        var created = new PublisherIdentityFile
        {
            PublisherId = Guid.NewGuid().ToString(),
            PublisherToken = GenerateToken(),
        };

        try
        {
            string dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonUtility.ToJson(created));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PublisherIdentity] Failed to persist new publisher identity: {e.Message}");
        }

        _cached = created;
        return _cached;
    }

    private static PublisherIdentityFile Load()
    {
        if (_cached != null && IsValid(_cached)) return _cached;

        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonUtility.FromJson<PublisherIdentityFile>(File.ReadAllText(FilePath));
                if (IsValid(loaded))
                {
                    _cached = loaded;
                    return _cached;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PublisherIdentity] Failed to read publisher identity: {e.Message}");
        }

        return null;
    }

    private static bool IsValid(PublisherIdentityFile f) =>
        f != null && !string.IsNullOrEmpty(f.PublisherId) && !string.IsNullOrEmpty(f.PublisherToken)
        && f.PublisherToken.Length == TokenBytes * 2;

    private static string GenerateToken()
    {
        byte[] bytes = new byte[TokenBytes];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);

        var sb = new StringBuilder(TokenBytes * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
