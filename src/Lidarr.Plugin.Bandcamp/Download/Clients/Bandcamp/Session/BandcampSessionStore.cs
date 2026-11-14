namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// Persists a renewed session into the client definition's saved settings, so a restart
    /// doesn't force a new login and a downgraded plugin still finds a working cookie.
    /// </summary>
    public interface IBandcampSessionStore
    {
        void SaveCookies(int definitionId, string cookies);
    }

    public class BandcampSessionStore : IBandcampSessionStore
    {
        private readonly IDownloadClientRepository _repository;

        public BandcampSessionStore(IDownloadClientRepository repository)
        {
            _repository = repository;
        }

        public void SaveCookies(int definitionId, string cookies)
        {
            var definition = _repository.Find(definitionId);
            if (definition?.Settings is not BandcampDownloadSettings settings)
            {
                return;
            }

            settings.Cookies = cookies;
            _repository.SetFields(definition, d => d.Settings);
        }
    }
}
