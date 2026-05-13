using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NLog;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    /// <summary>
    /// Background download processor using System.Threading.Channels for async
    /// producer/consumer semantics. Downloads are enqueued via EnqueueAsync() and
    /// processed sequentially with rate limiting. The queue runs until disposed.
    /// </summary>
    public class DownloadTaskQueue : IDisposable
    {
        private readonly Channel<BandcampDownloadItem> _channel;
        private readonly ConcurrentDictionary<string, BandcampDownloadItem> _activeItems = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly IBandcampDownloadProxy _downloadProxy;
        private readonly Logger _logger;
        private readonly Task _consumerTask;
        private bool _disposed;

        public DownloadTaskQueue(IBandcampDownloadProxy downloadProxy, Logger logger)
        {
            _downloadProxy = downloadProxy;
            _logger = logger;

            // Bounded channel to limit memory pressure; writers block when full
            _channel = Channel.CreateBounded<BandcampDownloadItem>(new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });

            // Start the background consumer loop
            _consumerTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
        }

        /// <summary>
        /// Enqueues a download item for background processing.
        /// Returns the download ID immediately while processing continues.
        /// </summary>
        /// <param name="item">The download to process (must have Cookies, AlbumUrl, OutputPath, MediaFormat set).</param>
        /// <returns>The download ID for tracking.</returns>
        public async Task<string> EnqueueAsync(BandcampDownloadItem item)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DownloadTaskQueue));
            }

            item.Status = BandcampDownloadStatus.Queued;
            item.QueuedAt = DateTime.UtcNow;
            item.Phase = "queued";

            _activeItems[item.DownloadId] = item;

            await _channel.Writer.WriteAsync(item, _cts.Token).ConfigureAwait(false);

            _logger.Debug("Bandcamp download queue: Enqueued download {0} for '{1}'",
                item.DownloadId, item.AlbumUrl);

            return item.DownloadId;
        }

        /// <summary>
        /// Returns all tracked download items (queued, active, completed, failed).
        /// Used by the download client's GetItems() to report state to Lidarr.
        /// </summary>
        public ConcurrentDictionary<string, BandcampDownloadItem> GetItems()
        {
            return _activeItems;
        }

        /// <summary>
        /// Removes a completed/failed item from tracking.
        /// </summary>
        public void RemoveItem(string downloadId)
        {
            _activeItems.TryRemove(downloadId, out _);
            _logger.Debug("Bandcamp download queue: Removed item {0}", downloadId);
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            _logger.Debug("Bandcamp download queue: Background processor started");

            try
            {
                await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await ProcessItemAsync(item, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Graceful shutdown — expected
                _logger.Debug("Bandcamp download queue: Processor shutting down");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Bandcamp download queue: Processor encountered fatal error");
            }
        }

        private async Task ProcessItemAsync(BandcampDownloadItem item, CancellationToken cancellationToken)
        {
            _logger.Debug("Bandcamp download queue: Starting download {0} for '{1}'",
                item.DownloadId, item.AlbumUrl);

            try
            {
                item.Status = BandcampDownloadStatus.Resolving;
                item.Phase = "resolving";

                await _downloadProxy.ExecuteDownloadAsync(item, cancellationToken).ConfigureAwait(false);

                item.Status = BandcampDownloadStatus.Completed;
                item.Progress = 1.0;
                item.CompletedAt = DateTime.UtcNow;
                item.Phase = "completed";

                _logger.Debug("Bandcamp download queue: Download {0} completed successfully -> {1}",
                    item.DownloadId, item.OutputPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                item.Status = BandcampDownloadStatus.Failed;
                item.ErrorMessage = "Download was cancelled";
                item.CompletedAt = DateTime.UtcNow;
                item.Phase = "cancelled";

                _logger.Debug("Bandcamp download queue: Download {0} was cancelled", item.DownloadId);
            }
            catch (Exception ex)
            {
                item.Status = BandcampDownloadStatus.Failed;
                item.ErrorMessage = ex.Message;
                item.CompletedAt = DateTime.UtcNow;
                item.Phase = "failed";

                _logger.Debug(ex, "Bandcamp download queue: Download {0} failed during phase '{1}'",
                    item.DownloadId, item.Phase);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            _channel.Writer.TryComplete();

            try
            {
                _consumerTask.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
                // Consumer task may throw on cancellation — safe to ignore
            }

            _cts.Dispose();
        }
    }
}
