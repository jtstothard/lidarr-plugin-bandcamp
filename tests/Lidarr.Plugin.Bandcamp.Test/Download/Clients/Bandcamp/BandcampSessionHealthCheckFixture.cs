using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Download.Clients.Bandcamp.Session;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Http.Bandcamp;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Events;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    public class BandcampSessionHealthCheckFixture
    {
        private const string LiveHomepage =
            @"<html><body><div id=""HomepageApp"" data-blob=""{&quot;pageContext&quot;:{&quot;identity&quot;:{&quot;fanId&quot;:12345678,&quot;isLoggedIn&quot;:true}}}""></div></body></html>";

        private const string DeadHomepage =
            @"<html><body><div id=""HomepageApp"" data-blob=""{&quot;pageContext&quot;:{&quot;identity&quot;:{&quot;fanId&quot;:null,&quot;isLoggedIn&quot;:false}}}""></div></body></html>";

        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly DownloadClientDefinition _definition = new()
        {
            Id = 7,
            Name = "My Bandcamp",
            Enable = true,
            Implementation = nameof(BandcampDownloadClient),
            Settings = new BandcampDownloadSettings { Cookies = "identity=testcookie", DownloadPath = "/tmp/bandcamp" }
        };

        private string _homepage = LiveHomepage;
        private int _homepageCalls;
        private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task Check_ExpiredCookie_WarnsNamingClientAndCause_ThenClearsOnceSessionRestored()
        {
            var registry = CreateRegistry();
            var check = new BandcampSessionHealthCheck(CreateFactory(), registry, Mock.Of<ILocalizationService>());
            var provider = registry.Get(_definition.Id, (BandcampDownloadSettings)_definition.Settings);

            _homepage = DeadHomepage;
            await provider.ValidateAsync();

            var warning = check.Check();
            Assert.Equal(HealthCheckResult.Warning, warning.Type);
            Assert.Contains("My Bandcamp", warning.Message);
            Assert.Contains(BandcampSessionProblem.CookieExpired.Description, warning.Message);
            Assert.Equal(BandcampSessionProblem.CookieExpired, provider.State.Problem);

            _homepage = LiveHomepage;
            await provider.ValidateAsync();

            Assert.Equal(HealthCheckResult.Ok, check.Check().Type);
        }

        [Fact]
        public async Task Monitor_RevalidatesEachSessionHourly()
        {
            var registry = CreateRegistry();
            var monitor = new BandcampSessionMonitor(CreateFactory(), registry, Mock.Of<IEventAggregator>(), _logger, () => _now);

            await monitor.RunDueChecksAsync();
            Assert.Equal(1, _homepageCalls);

            _now = _now.AddMinutes(59);
            await monitor.RunDueChecksAsync();
            Assert.Equal(1, _homepageCalls);

            _homepage = DeadHomepage;
            _now = _now.AddMinutes(2);
            await monitor.RunDueChecksAsync();
            Assert.True(_homepageCalls >= 2);
            Assert.Equal(BandcampSessionProblem.CookieExpired, registry.Find(_definition.Id)!.State.Problem);
        }

        [Fact]
        public async Task Monitor_UnrecognizedPage_ReportsHealthWithoutImmediateSecondValidation()
        {
            _homepage = "<html>Verify you are human</html>";
            var registry = CreateRegistry();
            var monitor = new BandcampSessionMonitor(CreateFactory(), registry, Mock.Of<IEventAggregator>(), _logger, () => _now);
            await monitor.RunDueChecksAsync();
            Assert.Equal(1, _homepageCalls);
            Assert.Equal("ChallengeOrUnrecognized", registry.Find(_definition.Id)!.State.Problem?.Code);
        }

        private IDownloadClientFactory CreateFactory()
        {
            var factory = new Mock<IDownloadClientFactory>();
            factory.Setup(f => f.All()).Returns(new List<DownloadClientDefinition> { _definition });
            return factory.Object;
        }

        private BandcampSessionProviderRegistry CreateRegistry()
        {
            var httpClient = new Mock<IHttpClient>();
            httpClient
                .Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .Returns((HttpRequest request) =>
                {
                    Interlocked.Increment(ref _homepageCalls);
                    return Task.FromResult(new HttpResponse(request, new HttpHeader(), _homepage, System.Net.HttpStatusCode.OK));
                });

            var apiClient = new BandcampApiClient(new BandcampHttpClient(httpClient.Object, _logger), _logger);
            return new BandcampSessionProviderRegistry(apiClient, null, null, _logger, () => _now);
        }
    }
}
