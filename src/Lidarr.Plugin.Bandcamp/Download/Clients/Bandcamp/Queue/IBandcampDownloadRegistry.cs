using System.Collections.Concurrent;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    public interface IBandcampDownloadRegistry
    {
        ConcurrentDictionary<string, BandcampDownloadItem> GetItems();

        void Upsert(BandcampDownloadItem item);

        void Remove(string downloadId);
    }
}
