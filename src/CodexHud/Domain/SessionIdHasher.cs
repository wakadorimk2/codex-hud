using System.Security.Cryptography;
using System.Text;

namespace CodexHud.Domain;

public static class SessionIdHasher
{
    public static string Hash(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return "session-unknown";
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId));
        return $"session-{Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant()}";
    }
}
