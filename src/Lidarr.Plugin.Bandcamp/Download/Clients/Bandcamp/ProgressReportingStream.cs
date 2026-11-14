using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    /// <summary>
    /// Pass-through stream that mirrors the inner stream's position into
    /// <see cref="BandcampDownloadItem.DownloadedBytes"/> as bytes are written,
    /// so queue progress tracks a streamed download without buffering it.
    /// Does not own the inner stream.
    /// </summary>
    internal sealed class ProgressReportingStream : Stream
    {
        private readonly Stream _inner;
        private readonly BandcampDownloadItem _item;

        public ProgressReportingStream(Stream inner, BandcampDownloadItem item)
        {
            _inner = inner;
            _item = item;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            Report();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Report();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value)
        {
            _inner.SetLength(value);
            Report();
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        private void Report()
        {
            _item.DownloadedBytes = _inner.Position;
            if (_item.TotalSize > 0)
            {
                _item.Progress = Math.Min(1.0, (double)_item.DownloadedBytes / _item.TotalSize);
            }
        }
    }
}
