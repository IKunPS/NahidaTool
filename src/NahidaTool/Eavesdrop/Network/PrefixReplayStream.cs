using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NahidaTool.Eavesdrop.Network;

/// <summary>Replays bytes read for routing before continuing the original TLS transport.</summary>
internal sealed class PrefixReplayStream(Stream transport, byte[] prefix) : Stream
{
    private int _position;
    public override bool CanRead => transport.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => transport.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => transport.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => transport.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        int copied = CopyPrefix(buffer);
        return copied > 0 ? copied : transport.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0) return ValueTask.FromResult(0);
        int copied = CopyPrefix(buffer.Span);
        return copied > 0 ? ValueTask.FromResult(copied) : transport.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int CopyPrefix(Span<byte> buffer)
    {
        int count = Math.Min(buffer.Length, prefix.Length - _position);
        prefix.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override void Write(byte[] buffer, int offset, int count) => transport.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => transport.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        transport.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        transport.WriteAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) transport.Dispose();
        base.Dispose(disposing);
    }
}
