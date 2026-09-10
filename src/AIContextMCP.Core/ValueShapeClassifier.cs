using System.Text.RegularExpressions;

namespace AIContextMCP.Core;

public static class SecretValueClassifier
{
    private static readonly Regex Pattern = new(
        """(?:["']?(?:password|pwd|passphrase|api[_-]?key|access[_-]?token|token)["']?\s*[:=]\s*["']?[^\s,}\]]+|bearer\s+[A-Za-z0-9._~-]{8,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|https?://[^\s/:@]+:[^\s@/]+@|(?:mongodb|postgres(?:ql)?|mysql|sqlserver)://[^\s:]+:[^\s@]+@)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsSecretShaped(string? value) => value is not null && Pattern.IsMatch(value);
}
