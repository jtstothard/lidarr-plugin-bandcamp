using System;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    /// <summary>
    /// Per-download state tracked by the DownloadTaskQueue.
    /// Represents a single album/track download from Bandcamp with progress,
    /// status, and error information for Lidarr's GetItems() reporting.
    /// </summary>
    public class BandcampDownloadItem
    {
        /// <summary>
        /// Unique identifier for this download (used as DownloadId in Lidarr).
        /// </summary>
        public string DownloadId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// The Bandcamp album/track URL that initiated this download.
        /// </summary>
        public string AlbumUrl { get; set; } = string.Empty;

        /// <summary>
        /// Display title for the download (artist - album).
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Desired output directory for the extracted download.
        /// </summary>
        public string OutputPath { get; set; } = string.Empty;

        /// <summary>
        /// Current status of the download.
        /// </summary>
        public BandcampDownloadStatus Status { get; set; } = BandcampDownloadStatus.Queued;

        /// <summary>
        /// Download progress as a value between 0.0 and 1.0.
        /// </summary>
        public double Progress { get; set; }

        /// <summary>
        /// Total size of the download in bytes (populated after headers received).
        /// </summary>
        public long TotalSize { get; set; }

        /// <summary>
        /// Number of bytes downloaded so far.
        /// </summary>
        public long DownloadedBytes { get; set; }

        /// <summary>
        /// Error message if the download failed.
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// When this item was added to the queue.
        /// </summary>
        public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the download completed (successfully or with failure).
        /// </summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// The current phase of the download orchestration, for logging.
        /// </summary>
        public string Phase { get; set; } = "queued";

        /// <summary>
        /// Internal: canonical metadata to apply to extracted files before Lidarr import.
        /// Built from Lidarr's matched album context at grab time so we can normalize
        /// tags without guessing from Bandcamp metadata alone.
        /// </summary>
        internal BandcampRetagContext? RetagContext { get; set; }

        /// <summary>
        /// Internal: session cookies for authenticating with Bandcamp.
        /// Not exposed to Lidarr — used only during download processing.
        /// </summary>
        internal string? Cookies { get; set; }
    }

    /// <summary>
    /// Status values for a Bandcamp download item.
    /// Maps to Lidarr's DownloadItemStatus in the proxy layer.
    /// </summary>
    public enum BandcampDownloadStatus
    {
        Queued,
        Resolving,
        Downloading,
        Extracting,
        Completed,
        Failed
    }
}
