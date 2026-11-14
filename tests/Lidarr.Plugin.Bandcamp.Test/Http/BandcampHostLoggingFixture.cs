using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DryIoc;
using Moq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Http.Bandcamp;
using Xunit;
using HttpRequest = NzbDrone.Common.Http.HttpRequest;
using HttpResponse = NzbDrone.Common.Http.HttpResponse;

namespace Lidarr.Plugin.Bandcamp.Test.Http;

public class BandcampHostLoggingFixture
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public BandcampHostLoggingFixture(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContainerResolvedExecutionDoesNotExposeSignedUrlsOrNonHtmlErrorBodies(bool raw)
    {
        using var factory = new LogFactory();
        var target = new MemoryTarget { Layout = "${logger} ${message} ${exception:format=tostring}" };
        var config = new LoggingConfiguration(); config.AddRule(LogLevel.Trace, LogLevel.Fatal, target); factory.Configuration = config;
        var logger = factory.GetLogger("host-http-seam");
        var secret = "signed-host-seam-private-token";
        var bodySecret = "nonhtml-error-private-body";
        var handler = new ResponseHandler(bodySecret);
        var dispatcher = new FixtureDispatcher(handler, logger);
        var limiter = new Mock<IRateLimitService>();
        limiter.Setup(l => l.WaitAndPulseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(Task.CompletedTask);
        var host = new NzbDrone.Common.Http.HttpClient(Array.Empty<IHttpRequestInterceptor>(), new CacheManager(), limiter.Object, dispatcher, logger);

        // Positive control: these exact host sources leak both sentinels despite the flags.
        var control = new HttpRequest("https://bandcamp.com/download?token=" + secret)
        { SuppressHttpError = true, LogHttpError = false, LogResponseContent = false, AllowAutoRedirect = false };
        await host.ExecuteAsync(control);
        Assert.Contains(target.Logs, l => l.Contains(secret));
        Assert.Contains(target.Logs, l => l.Contains(bodySecret));
        target.Logs.Clear();

        using var container = new Container(rules => rules.WithNzbDroneRules()).AutoAddServices(new List<Assembly>
        { typeof(ManagedHttpDispatcher).Assembly, typeof(BandcampHttpClient).Assembly });
        container.RegisterInstance<IHttpClient>(host, IfAlreadyRegistered.Replace);
        container.RegisterInstance<BandcampHttpDispatcher>(dispatcher, IfAlreadyRegistered.Replace);
        container.RegisterInstance(limiter.Object, IfAlreadyRegistered.Replace);
        container.RegisterInstance(logger, IfAlreadyRegistered.Replace);
        Assert.Same(host, container.Resolve<IHttpClient>());
        var client = container.Resolve<BandcampHttpClient>();
        var request = client.CreateRequestBuilder("https://bandcamp.com/download?token=" + secret, "identity=private-cookie").Build();
        request.SuppressHttpError = true;
        request.ResponseStream = new MemoryStream();
        var exception = await Record.ExceptionAsync(async () =>
        {
            var response = raw ? await client.ExecuteRawAsync(request) : await client.ExecuteAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(bodySecret, response.Content);
        });
        var captured = string.Join("\n", target.Logs) + exception;
        foreach (var value in new[] { secret, bodySecret, "private-cookie" })
        {
            Assert.DoesNotContain(value, captured);
            Assert.DoesNotContain(value[..12], captured);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            Assert.DoesNotContain(hash, captured, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(hash[..12], captured, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Null(exception);
        Assert.Equal("https://bandcamp.com/download?token=" + secret, handler.LastUrl);
        Assert.Equal("identity=private-cookie", handler.LastCookie);
        Assert.Contains(target.Logs, l => l.Contains("Bandcamp response: 400"));
        _output.WriteLine("Original host Trace exposure positive control: confirmed for URL and error body.");
        _output.WriteLine(string.Join("\n", target.Logs));
        limiter.Verify(l => l.WaitAndPulseAsync("bandcamp.com", "bandcamp", TimeSpan.FromSeconds(2)), Times.Once);
    }

    private sealed class ResponseHandler(string body) : HttpMessageHandler
    {
        public string? LastUrl;
        public string? LastCookie;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.AbsoluteUri;
            LastCookie = request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.SingleOrDefault() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FixtureDispatcher : BandcampHttpDispatcher
    {
        private readonly System.Net.Http.HttpClient _client;
        public FixtureDispatcher(HttpMessageHandler handler, Logger logger) : base(Mock.Of<IHttpProxySettingsProvider>(), Mock.Of<ICreateManagedWebProxy>(),
            Mock.Of<ICertificateValidationService>(), UserAgent(), new CacheManager(), logger) => _client = new(handler);
        protected override System.Net.Http.HttpClient GetClient(HttpUri uri) => _client;
        private static IUserAgentBuilder UserAgent()
        {
            var mock = new Mock<IUserAgentBuilder>(); mock.Setup(x => x.GetUserAgent(It.IsAny<bool>())).Returns("Lidarr/test"); return mock.Object;
        }
    }
}
