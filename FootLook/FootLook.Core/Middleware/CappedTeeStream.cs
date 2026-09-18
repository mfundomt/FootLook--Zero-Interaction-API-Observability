namespace FootLook.Core.Middleware
{
    /// <summary>
    /// Wraps the real response stream so every write reaches the client immediately (true
    /// streaming - SSE, chunked transfer, large downloads are no longer delayed until the
    /// whole response completes), while separately mirroring at most
    /// <paramref name="captureLimitBytes"/> bytes into a bounded side-buffer for capture.
    /// Replaces the previous buffer-everything-then-copy-at-the-end approach, which forced
    /// the client to wait for the entire response and buffered arbitrarily large responses
    /// (a multi-GB file download, an endless SSE stream) fully in memory regardless of
    /// whether any of it was ever going to be captured.
    /// </summary>
    internal sealed class CappedTeeStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _captureLimitBytes;
        private readonly MemoryStream _capture;
        private bool _captureTruncated;

        public CappedTeeStream(Stream inner, int captureLimitBytes)
        {
            _inner = inner;
            _captureLimitBytes = Math.Max(0, captureLimitBytes);
            _capture = new MemoryStream(Math.Min(_captureLimitBytes, 64 * 1024));
        }

        /// <summary>True if more bytes were written than fit within the capture limit.</summary>
        public bool CaptureTruncated => _captureTruncated;

        /// <summary>The captured bytes, at most captureLimitBytes long.</summary>
        public byte[] GetCapturedBytes() => _capture.ToArray();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            MirrorIntoCapture(buffer.Span);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            MirrorIntoCapture(buffer.AsSpan(offset, count));
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            MirrorIntoCapture(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            MirrorIntoCapture(buffer);
        }

        private void MirrorIntoCapture(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                return;
            }

            var remaining = _captureLimitBytes - (int)_capture.Length;
            if (remaining <= 0)
            {
                _captureTruncated = true;
                return;
            }

            var toCapture = Math.Min(remaining, data.Length);
            _capture.Write(data[..toCapture]);

            if (toCapture < data.Length)
            {
                _captureTruncated = true;
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override void Flush() => _inner.Flush();

        // This stream is write-only - it exists purely to tee response output, never to be
        // read back from or seeked (that's what the capped capture buffer is for).
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // Deliberately does not dispose _inner - it's the real response stream, owned
            // by the host/Kestrel, not by this wrapper.
            if (disposing)
            {
                _capture.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
