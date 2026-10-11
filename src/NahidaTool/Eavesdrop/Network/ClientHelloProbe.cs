using System;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace NahidaTool.Eavesdrop.Network;

internal static class ClientHelloProbe
{
    internal static async Task<(string? ServerName, byte[] Prefix)> ReadAsync(
        Stream transport, CancellationToken cancellationToken)
    {
        string? serverName = null;
        bool captured = false;
        using var capture = new CaptureStream(transport);
        using var probe = new SslStream(capture, leaveInnerStreamOpen: true);
        try
        {
            // Use the .NET TLS parser instead of duplicating the ClientHello wire
            // format. Stop at option selection, before any TLS session is created.
            await probe.AuthenticateAsServerAsync((_, hello, _, _) =>
            {
                serverName = hello.ServerName;
                captured = true;
                throw new HelloCapturedException();
            }, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (captured && !cancellationToken.IsCancellationRequested)
        {
        }
        catch (AuthenticationException) when (!cancellationToken.IsCancellationRequested)
        {
            // Unsupported ClientHello formats remain end-to-end passthrough.
            // Every consumed byte is replayed; probe TLS alerts never reach the peer.
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (serverName, capture.GetPrefix());
    }

    private sealed class HelloCapturedException : Exception { }

    private sealed class CaptureStream(Stream transport) : Stream
    {
        private const int MaximumPrefixBytes = 64 * 1024;
        private readonly MemoryStream _prefix = new();

        internal byte[] GetPrefix() => _prefix.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = transport.Read(buffer, offset, Limit(count));
            _prefix.Write(buffer, offset, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await transport.ReadAsync(buffer[..Limit(buffer.Length)], cancellationToken).ConfigureAwait(false);
            _prefix.Write(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Limit(int count)
        {
            int remaining = MaximumPrefixBytes - (int)_prefix.Length;
            if (remaining <= 0) throw new InvalidDataException("TLS ClientHello exceeds the capture limit");
            return Math.Min(count, remaining);
        }

        // Probe output is intentionally discarded, including fatal alerts caused
        // by ending option selection. This is inspection, not TLS termination.
        public override void Write(byte[] buffer, int offset, int count) { }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _prefix.Dispose();
            base.Dispose(disposing);
        }
    }
}
