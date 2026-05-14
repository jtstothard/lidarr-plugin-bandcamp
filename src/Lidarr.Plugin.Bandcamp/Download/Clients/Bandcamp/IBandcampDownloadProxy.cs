using System.Threading;
using System.Threading.Tasks;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    /// <summary>
    /// Orchestrates the full Bandcamp download flow for a single item:
    /// resolve purchase → get download URL → statdownload → stream ZIP → extract.
    /// The proxy is called by the shared Bandcamp download queue for each enqueued item.
    /// </summary>
    public interface IBandcampDownloadProxy
    {
        /// <summary>
        /// Executes the full download flow for the given item.
        /// Updates the item's status, progress, and output path as it progresses.
        /// Called by the shared Bandcamp download queue for each enqueued item.
        /// </summary>
        /// <param name="item">The download item with AlbumUrl, OutputPath, Cookies, and MediaFormat set.</param>
        /// <param name="cancellationToken">Cancellation token for graceful shutdown.</param>
        Task ExecuteDownloadAsync(BandcampDownloadItem item, CancellationToken cancellationToken);
    }
}
