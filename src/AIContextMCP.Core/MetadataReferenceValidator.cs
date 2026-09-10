namespace AIContextMCP.Core;

internal static class MetadataReferenceValidator
{
    public static string? Optional(string? value, string field, bool allowWebUri = true)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidInput, $"{field} is invalid.");
        }

        if (value.Length > StorageLimits.Reference)
        {
            throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, $"{field} exceeds its limit.");
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!allowWebUri || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ApplicationException(ApplicationErrorCode.PathRejected, $"{field} is not a safe reference.");
            }

            return uri.GetLeftPart(UriPartial.Path);
        }

        if (Path.IsPathRooted(value)
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || value.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || value.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || value.Contains(':', StringComparison.Ordinal)
            || value.IndexOf('\0') >= 0
            || value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, $"{field} is not a safe reference.");
        }

        return value.Replace('\\', '/');
    }

    public static IReadOnlyList<string> RelativeList(IReadOnlyList<string> values, string field)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > StorageLimits.CollectionCount)
        {
            throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, $"{field} exceeds its limit.");
        }

        return values.Select(value => Optional(value, field, allowWebUri: false)!).ToArray();
    }
}
