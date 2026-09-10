namespace AIContextMCP.Core;

public enum StorageErrorCode
{
    Validation,
    LimitExceeded,
    SecretRejected,
    Duplicate,
    NotFound,
    CrossProjectReference,
    Conflict,
    DatabaseUnavailable,
    DatabaseBusy,
    DatabaseCorrupt,
    UnsupportedSchema
}

public sealed class StorageException(StorageErrorCode code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public StorageErrorCode Code { get; } = code;
}
