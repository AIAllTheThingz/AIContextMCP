using System.Text.Json;
using System.Text;
using AIContextMCP.Core;
using ModelContextProtocol.Protocol;

namespace AIContextMCP.Server;

internal sealed class BoundedJsonLineStream(Stream source, int maximumBytes) : Stream
{
    private readonly Stream _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly byte[] _line = new byte[maximumBytes > 0 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes))];
    private readonly byte[] _sourceBuffer = new byte[4096];
    private readonly TaskCompletionSource<bool> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<RequestId?, CancellationToken, Task>? _rejection;
    private int _sourceOffset;
    private int _sourceCount;
    private int _lineOffset;
    private int _lineCount;
    private bool _newline;

    public void Bind(Func<RequestId?, CancellationToken, Task> rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        if (Interlocked.CompareExchange(ref _rejection, rejection, null) is not null) throw new InvalidOperationException("The rejection handler is already bound.");
        _bound.TrySetResult(true);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        var written = 0;
        while (written < buffer.Length)
        {
            if (_lineOffset < _lineCount)
            {
                var count = Math.Min(buffer.Length - written, _lineCount - _lineOffset);
                _line.AsSpan(_lineOffset, count).CopyTo(buffer.Span[written..]);
                _lineOffset += count;
                written += count;
                continue;
            }

            if (_newline)
            {
                buffer.Span[written++] = (byte)'\n';
                _newline = false;
                break;
            }

            if (written != 0 || !await ReadAcceptedLineAsync(cancellationToken).ConfigureAwait(false)) break;
        }

        return written;
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async ValueTask<bool> ReadAcceptedLineAsync(CancellationToken cancellationToken)
    {
        await _bound.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var count = 0;
            var overflowed = false;
            var terminated = false;
            while (true)
            {
                var value = await ReadSourceByteAsync(cancellationToken).ConfigureAwait(false);
                if (value < 0) break;
                if (value == '\n')
                {
                    terminated = true;
                    break;
                }

                if (count == _line.Length)
                {
                    overflowed = true;
                }
                else if (!overflowed)
                {
                    _line[count++] = (byte)value;
                }
            }

            if (!overflowed && IsWhitespace(_line.AsSpan(0, count)))
            {
                _lineOffset = 0;
                _lineCount = count;
                _newline = terminated;
                return count != 0 || terminated;
            }

            RequestId? id = null;
            if (!overflowed && IsValidJson(_line, count, out id))
            {
                _lineOffset = 0;
                _lineCount = count;
                _newline = terminated;
                return true;
            }

            await RejectAsync(overflowed ? null : id, cancellationToken).ConfigureAwait(false);
            if (!terminated) return false;
        }
    }

    private async ValueTask<int> ReadSourceByteAsync(CancellationToken cancellationToken)
    {
        if (_sourceOffset == _sourceCount)
        {
            _sourceCount = await _source.ReadAsync(_sourceBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            _sourceOffset = 0;
        }

        return _sourceCount == 0 ? -1 : _sourceBuffer[_sourceOffset++];
    }

    private async Task RejectAsync(RequestId? id, CancellationToken cancellationToken)
    {
        try
        {
            await _rejection!(id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }
    }

    private static bool IsValidJson(byte[] utf8, int count, out RequestId? id)
    {
        id = null;
        try
        {
            using var document = JsonDocument.Parse(utf8.AsMemory(0, count), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            if (HasDuplicateProperty(document.RootElement)) return false;
            return TryRequestIdOf(document.RootElement, out id);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryRequestIdOf(JsonElement root, out RequestId? id)
    {
        id = null;
        if (root.ValueKind != JsonValueKind.Object) return true;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.NameEquals("id")) continue;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String when property.Value.GetString() is { } value && IsSafeId(value):
                    id = new RequestId(value);
                    return true;
                case JsonValueKind.Number when property.Value.TryGetInt64(out var value):
                    id = new RequestId(value);
                    return true;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool IsSafeId(string value) => Encoding.UTF8.GetByteCount(value) <= 512
        && !value.Any(char.IsControl)
        && !SecretValueClassifier.IsSecretShaped(value);

    private static bool HasDuplicateProperty(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperty(property.Value)) return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (HasDuplicateProperty(item)) return true;
            }
        }

        return false;
    }

    private static bool IsWhitespace(ReadOnlySpan<byte> value) => value.IndexOfAnyExcept((byte)' ', (byte)'\t', (byte)'\r') < 0;
}
