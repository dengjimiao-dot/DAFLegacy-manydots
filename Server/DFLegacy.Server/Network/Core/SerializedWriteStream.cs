namespace DFLegacy.Server;

internal sealed class SerializedWriteStream(Stream inner) : Stream
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanTimeout => inner.CanTimeout;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int ReadTimeout
    {
        get => inner.ReadTimeout;
        set => inner.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => inner.WriteTimeout;
        set => inner.WriteTimeout = value;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override int ReadByte() => inner.ReadByte();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _writeGate.Wait();
        try
        {
            inner.Write(buffer, offset, count);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _writeGate.Wait();
        try
        {
            inner.Write(buffer);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await inner.WriteAsync(buffer, offset, count, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await inner.WriteAsync(buffer, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override void WriteByte(byte value)
    {
        _writeGate.Wait();
        try
        {
            inner.WriteByte(value);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override void Flush()
    {
        _writeGate.Wait();
        try
        {
            inner.Flush();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await inner.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            _writeGate.Dispose();
        }

        base.Dispose(disposing);
    }
}
