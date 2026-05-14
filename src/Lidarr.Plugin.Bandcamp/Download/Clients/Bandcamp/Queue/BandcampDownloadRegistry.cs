using System.Collections.Concurrent;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    public class BandcampDownloadRegistry : IBandcampDownloadRegistry
    {
        private readonly ConcurrentDictionary<string, BandcampDownloadItem> _items = new();

        public ConcurrentDictionary<string, BandcampDownloadItem> GetItems()
        {
            return _items;
        }

        public void Upsert(BandcampDownloadItem item)
        {
            _items[item.DownloadId] = item;
        }

        public void Remove(string downloadId)
        {
            _items.TryRemove(downloadId, out _);
        }
    }
}
