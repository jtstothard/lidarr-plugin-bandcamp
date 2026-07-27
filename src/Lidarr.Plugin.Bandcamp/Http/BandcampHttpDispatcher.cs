using System.Net.Http;
using System.Net.Http.Headers;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;

namespace NzbDrone.Core.Http.Bandcamp
{
    /// <summary>
    /// Self-contained HTTP dispatcher that permits the Bandcamp plugin to set
    /// browser-like User-Agent headers without depending on Tubifarry's
    /// FlexibleHttpDispatcher.
    ///
    /// Lidarr's built-in ManagedHttpDispatcher throws
    /// <c>NotSupportedException("User-Agent other than Lidarr not allowed.")</c>
    /// for any request that sets a custom User-Agent. Tubifarry works around
    /// this by registering FlexibleHttpDispatcher, which overrides
    /// AddRequestHeaders to allow custom UAs. Without Tubifarry installed
    /// first, all Bandcamp connection tests fail.
    ///
    /// This dispatcher subclasses ManagedHttpDispatcher (inheriting all proxy,
    /// certificate, cache, and connection behaviour) and overrides only
    /// AddRequestHeaders to permit the User-Agent header. Lidarr's DryIoc
    /// container registers the last-loaded IHttpDispatcher implementation as
    /// the singleton, so this class replaces ManagedHttpDispatcher when the
    /// plugin is loaded.
    /// </summary>
    public class BandcampHttpDispatcher : ManagedHttpDispatcher, IHttpDispatcher
    {
        public BandcampHttpDispatcher(
            IHttpProxySettingsProvider proxySettingsProvider,
            ICreateManagedWebProxy createManagedWebProxy,
            ICertificateValidationService certificateValidationService,
            IUserAgentBuilder userAgentBuilder,
            ICacheManager cacheManager,
            Logger logger)
            : base(proxySettingsProvider, createManagedWebProxy, certificateValidationService,
                   userAgentBuilder, cacheManager, logger)
        {
        }

        /// <summary>
        /// Adds request headers, permitting a custom User-Agent instead of
        /// throwing NotSupportedException.
        ///
        /// This mirrors the base implementation exactly except for the
        /// User-Agent case: instead of throwing, it parses and applies the
        /// header value, allowing the Bandcamp plugin's browser-like UA to
        /// reach the wire.
        /// </summary>
        protected override void AddRequestHeaders(HttpRequestMessage webRequest, HttpHeader headers)
        {
            foreach (var header in headers)
            {
                switch (header.Key)
                {
                    case "Accept":
                        webRequest.Headers.Accept.ParseAdd(header.Value);
                        break;
                    case "Connection":
                        webRequest.Headers.Connection.Clear();
                        webRequest.Headers.Connection.Add(header.Value);
                        break;
                    case "Content-Length":
                        AddContentHeader(webRequest, "Content-Length", header.Value);
                        break;
                    case "Content-Type":
                        AddContentHeader(webRequest, "Content-Type", header.Value);
                        break;
                    case "Content-Encoding":
                        AddContentHeader(webRequest, "Content-Encoding", header.Value);
                        break;
                    case "Date":
                        webRequest.Headers.Remove("Date");
                        webRequest.Headers.Date = HttpHeader.ParseDateTime(header.Value);
                        break;
                    case "Expect":
                        webRequest.Headers.Expect.ParseAdd(header.Value);
                        break;
                    case "Host":
                        webRequest.Headers.Host = header.Value;
                        break;
                    case "If-Modified-Since":
                        webRequest.Headers.IfModifiedSince = HttpHeader.ParseDateTime(header.Value);
                        break;
                    case "Range":
                        throw new NotImplementedException();
                    case "Referer":
                        webRequest.Headers.Add("Referer", header.Value);
                        break;
                    case "Transfer-Encoding":
                        webRequest.Headers.TransferEncoding.ParseAdd(header.Value);
                        break;
                    case "User-Agent":
                        // Permit custom User-Agent instead of throwing.
                        // The Bandcamp plugin sets a browser-like UA so
                        // Bandcamp serves full search pages.
                        webRequest.Headers.UserAgent.Clear();
                        webRequest.Headers.UserAgent.ParseAdd(header.Value);
                        break;
                    case "Proxy-Connection":
                        throw new NotImplementedException();
                    default:
                        webRequest.Headers.Add(header.Key, header.Value);
                        break;
                }
            }
        }
    }
}
