using System.Security.Cryptography;
using System.Text;

namespace StressManager.Calculations;

/// <summary>
/// Access code check. The cookie holds an HMAC derived from the code, so it survives pod restarts
/// and stops working when the code changes.
/// </summary>
public static class AccessGate
{
    public const string CodeVariable = "STRESS_ACCESS_CODE";
    public const string CookieName = "sm_access";
    public const string LoginPage = "/login.html";
    public const string LoginEndpoint = "/login";

    private static readonly string[] PublicPaths = ["/healthz", LoginPage, LoginEndpoint, "/app.css", "/favicon.ico"];

    public static string Token(string code) =>
        Convert.ToBase64String(HMACSHA256.HashData(Hash("stress-manager-access:" + code), "v1"u8))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool CodeMatches(string? input, string code) =>
        CryptographicOperations.FixedTimeEquals(Hash(input ?? ""), Hash(code));

    public static bool TokenMatches(string? cookie, string code) =>
        cookie is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(Token(code)));

    public static bool IsPublic(string path) => PublicPaths.Contains(path, StringComparer.OrdinalIgnoreCase);

    public static bool IsApi(string path) => path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Only same-site relative paths; anything else (absolute, protocol-relative, backslash) becomes "/".</summary>
    public static string SafeNext(string? next) =>
        next is { Length: > 0 } && next[0] == '/' && !next.StartsWith("//") && !next.Contains('\\') && !next.Contains("://")
            ? next
            : "/";

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
