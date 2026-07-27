using System;
using System.Net.Http;
using System.Reflection;
using Moq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Http.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Http
{
    public class BandcampHttpDispatcherFixture
    {
        private readonly BandcampHttpDispatcher _dispatcher;

        public BandcampHttpDispatcherFixture()
        {
            var proxySettings = new Mock<IHttpProxySettingsProvider>();
            var createProxy = new Mock<ICreateManagedWebProxy>();
            var certValidation = new Mock<ICertificateValidationService>();
            var userAgentBuilder = new Mock<IUserAgentBuilder>();
            userAgentBuilder.Setup(x => x.GetUserAgent(It.IsAny<bool>()))
                           .Returns("Lidarr/1.0.0");
            var cacheManager = new CacheManager();
            var logger = LogManager.GetCurrentClassLogger();

            _dispatcher = new BandcampHttpDispatcher(
                proxySettings.Object,
                createProxy.Object,
                certValidation.Object,
                userAgentBuilder.Object,
                cacheManager,
                logger);
        }

        [Fact]
        public void Implements_IHttpDispatcher()
        {
            Assert.IsAssignableFrom<IHttpDispatcher>(_dispatcher);
        }

        [Fact]
        public void Subclasses_ManagedHttpDispatcher()
        {
            Assert.IsAssignableFrom<ManagedHttpDispatcher>(_dispatcher);
        }

        [Fact]
        public void AddRequestHeaders_AllowsCustomUserAgent_InsteadOfThrowing()
        {
            // The built-in ManagedHttpDispatcher throws NotSupportedException
            // for any custom User-Agent. Our override must permit it.
            var message = new HttpRequestMessage();
            var headers = new HttpHeader();
            headers.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            // Invoke protected method via reflection
            var method = typeof(ManagedHttpDispatcher).GetMethod(
                "AddRequestHeaders",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var ex = Record.Exception(() => method?.Invoke(_dispatcher, new object[] { message, headers }));

            // Must not throw NotSupportedException
            Assert.Null(ex);
            // The User-Agent must be applied
            Assert.Equal(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
                message.Headers.UserAgent.ToString());
        }

        [Fact]
        public void AddRequestHeaders_StillThrowsForUnimplementedHeaders()
        {
            // Our override must preserve the Range/Proxy-Connection throws
            // to avoid silently dropping headers.
            var message = new HttpRequestMessage();
            var headers = new HttpHeader();
            headers.Set("Range", "bytes=0-100");

            var method = typeof(ManagedHttpDispatcher).GetMethod(
                "AddRequestHeaders",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var ex = Assert.Throws<TargetInvocationException>(
                () => method?.Invoke(_dispatcher, new object[] { message, headers }));

            Assert.IsType<NotImplementedException>(ex.InnerException);
        }

        [Fact]
        public void AddRequestHeaders_HandlesNonUserAgentHeaders_Normally()
        {
            // Standard headers must still be applied, not broken by the UA override.
            var message = new HttpRequestMessage();
            var headers = new HttpHeader();
            headers.Set("Accept", "text/html");
            headers.Set("Accept-Language", "en-US,en;q=0.9");

            var method = typeof(ManagedHttpDispatcher).GetMethod(
                "AddRequestHeaders",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var ex = Record.Exception(() => method?.Invoke(_dispatcher, new object[] { message, headers }));
            Assert.Null(ex);
        }
    }
}
