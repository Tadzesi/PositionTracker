namespace PositionTracker.Api.Endpoints;

/// <summary>
/// Read-only, forward-only view of a request body that throws a 413 <see cref="BadHttpRequestException"/> once
/// more than the allowed number of bytes has been read. Enforces the cap on servers without
/// <c>IHttpMaxRequestBodySizeFeature</c> (for example the test server) and for bodies without <c>Content-Length</c>.
/// </summary>
internal sealed class MaxLengthReadStream(Stream inner, long maxBytes) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Count(int read)
    {
        _read += read;
        if (_read > maxBytes)
            throw new BadHttpRequestException($"The request body must not exceed {maxBytes} bytes.",
                StatusCodes.Status413PayloadTooLarge);
        return read;
    }
}
