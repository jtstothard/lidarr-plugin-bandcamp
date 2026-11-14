namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// Persists a renewed session into the client definition's saved settings, so a restart
    /// doesn't force a new login and a downgraded plugin still finds a working cookie.
    /// </summary>
    public interface IBandcampSessionStore
    {
        void SaveCookies(int definitionId, string cookies);

        /// <summary>Clears the one-time verification code from the saved settings.</summary>
        void ClearVerificationCode(int definitionId);
    }

    public class BandcampSessionStore : IBandcampSessionStore
    {
        private readonly IDownloadClientRepository _repository;

        public BandcampSessionStore(IDownloadClientRepository repository)
        {
            _repository = repository;
        }

        public void ClearVerificationCode(int definitionId)
        {
            Update(definitionId, s => s.VerificationCode = string.Empty);
        }

        public void SaveCookies(int definitionId, string cookies)
        {
            Update(definitionId, s => s.Cookies = cookies);
        }

        private void Update(int definitionId, System.Action<BandcampDownloadSettings> change)
        {
            var definition = _repository.Find(definitionId);
            if (definition?.Settings is not BandcampDownloadSettings settings)
            {
                return;
            }

            change(settings);
            _repository.SetFields(definition, d => d.Settings);
        }
    }
}
