using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    public interface IBandcampDownloadQueue : IDisposable
    {
        Task<string> EnqueueAsync(BandcampDownloadItem item);

        ConcurrentDictionary<string, BandcampDownloadItem> GetItems();

        void RemoveItem(string downloadId);
    }
}
