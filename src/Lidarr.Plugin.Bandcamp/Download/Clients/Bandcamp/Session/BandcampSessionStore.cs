using System;
using System.Collections.Concurrent;

namespace NzbDrone.Core.Download.Clients.Bandcamp.Session
{
    /// <summary>
    /// Persists a renewed session into the client definition's saved settings, so a restart
    /// doesn't force a new login and a downgraded plugin still finds a working cookie.
    /// </summary>
    public interface IBandcampSessionStore
    {
        /// <summary>Compare and replace under the per-definition plugin write gate.</summary>
        bool TryReplaceCookies(int definitionId, string expectedCookies, string expectedCredentials, string cookies);

        /// <summary>Save suppression and optional consumed-code clearing in one settings write.</summary>
        void SaveChallenge(int definitionId, DateTime untilUtc, string problem, bool clearVerificationCode = false);

        void SaveCookies(int definitionId, string cookies);

        /// <summary>Clears the one-time verification code from the saved settings.</summary>
        void ClearVerificationCode(int definitionId);
    }

    public class BandcampSessionStore : IBandcampSessionStore
    {
        private static readonly ConcurrentDictionary<int, object> Gates = new();
        private readonly IDownloadClientRepository _repository;

        public BandcampSessionStore(IDownloadClientRepository repository)
        {
            _repository = repository;
        }

        public bool TryReplaceCookies(int definitionId, string expectedCookies, string expectedCredentials, string cookies)
        {
            lock (Gates.GetOrAdd(definitionId, _ => new object()))
            {
                var definition = _repository.Find(definitionId);
                if (definition?.Settings is not BandcampDownloadSettings settings)
                {
                    throw new InvalidOperationException("Bandcamp definition is unavailable for session persistence");
                }

                if (settings.Cookies != expectedCookies || settings.CredentialKey() != expectedCredentials)
                {
                    return false;
                }

                var candidate = settings.Copy();
                candidate.Cookies = cookies;
                _repository.SetFields(new DownloadClientDefinition { Id = definitionId, Settings = candidate }, d => d.Settings);
                return true;
            }
        }

        public void SaveChallenge(int definitionId, DateTime untilUtc, string problem, bool clearVerificationCode = false)
        {
            Update(definitionId, s =>
            {
                s.ChallengeUntilUtc = untilUtc;
                s.ChallengeProblem = problem;
                if (clearVerificationCode)
                {
                    s.VerificationCode = string.Empty;
                }
            });
        }

        public void ClearVerificationCode(int definitionId)
        {
            Update(definitionId, s => s.VerificationCode = string.Empty);
        }

        public void SaveCookies(int definitionId, string cookies)
        {
            Update(definitionId, s => s.Cookies = cookies);
        }

        private void Update(int definitionId, Action<BandcampDownloadSettings> change)
        {
            lock (Gates.GetOrAdd(definitionId, _ => new object()))
            {
                var definition = _repository.Find(definitionId);
                if (definition?.Settings is not BandcampDownloadSettings settings)
                {
                    throw new InvalidOperationException("Bandcamp definition is unavailable for session persistence");
                }

                var candidate = settings.Copy();
                change(candidate);
                var update = new DownloadClientDefinition { Id = definitionId, Settings = candidate };
                _repository.SetFields(update, d => d.Settings);
            }
        }
    }
}
