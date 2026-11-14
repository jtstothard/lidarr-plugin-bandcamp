using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.CookiePush.Test;

public sealed class CookiePushCommandFixture
{
    private const string Cookie = "cookie-value-that-must-stay-secret";
    private const string ApiKey = "api-key-that-must-stay-secret";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_save_identifies_provider_and_skips_explicit_test(bool indexer)
    {
        var api = new MockLidarrApi();
        api.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", HttpStatusCode.OK, Clients());
        if (indexer)
        {
            api.Enqueue(HttpMethod.Get, "/api/v1/indexer", HttpStatusCode.OK, "[" + Indexer() + "]");
            api.Enqueue(HttpMethod.Put, "/api/v1/downloadclient/7", HttpStatusCode.OK, Client());
            api.Enqueue(HttpMethod.Post, "/api/v1/downloadclient/test", HttpStatusCode.OK, "{}");
        }
        api.Enqueue(HttpMethod.Put, indexer ? "/api/v1/indexer/11" : "/api/v1/downloadclient/7", HttpStatusCode.BadRequest, Cookie + ApiKey);
        var output = new StringWriter();
        var error = new StringWriter();
        var args = new List<string> { "--url", "http://lidarr.test", "--api-key", ApiKey };
        if (indexer) args.Add("--update-indexer");
        Assert.Equal(1, await Run(api, Cookie, output, error, args.ToArray()));
        Assert.Contains(indexer ? "indexer save failed" : "download client save failed", error.ToString());
        Assert.Contains("Validation/Test", error.ToString());
        Assert.Equal(indexer ? 5 : 2, api.Requests.Count);
        AssertSecretSafe(output, error);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    public async Task Requested_indexer_is_resolved_before_writes_and_dry_run_reports_both(bool dryRun, int count)
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", HttpStatusCode.OK, Clients());
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/indexer", HttpStatusCode.OK,
            "[" + string.Join(",", Enumerable.Range(0, count).Select(i => Indexer(11 + i))) + "]");
        var output = new StringWriter();
        var error = new StringWriter();
        var args = new List<string> { "--url", "http://lidarr.test", "--api-key", ApiKey, "--update-indexer" };
        if (dryRun) args.Add("--dry-run");

        var result = await Run(lidarr, Cookie, output, error, args.ToArray());

        Assert.Equal(count == 1 ? 0 : count == 0 ? 1 : 2, result);
        Assert.Equal(2, lidarr.Requests.Count);
        if (count == 1)
        {
            Assert.Contains("Bandcamp Search", output.ToString());
            Assert.Contains("download client Test", output.ToString());
            Assert.Contains("indexer Test", output.ToString());
        }
        else Assert.Contains("indexer", error.ToString());
        AssertSecretSafe(output, error);
    }

    [Theory]
    [InlineData("--indexer-id", "12", 0)]
    [InlineData("--indexer-name", "Downstairs Search", 0)]
    [InlineData("--indexer-id", "99", 2)]
    [InlineData("--indexer-name", "Missing", 2)]
    public async Task Dry_run_applies_indexer_selectors_without_writes(string flag, string value, int expectedExit)
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", HttpStatusCode.OK, Clients());
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/indexer", HttpStatusCode.OK,
            "[" + Indexer(11, "Upstairs Search") + "," + Indexer(12, "Downstairs Search") + "]");
        var output = new StringWriter();
        var error = new StringWriter();
        var result = await Run(lidarr, Cookie, output, error,
            "--url", "http://lidarr.test", "--api-key", ApiKey, "--dry-run", "--update-indexer", flag, value);
        Assert.Equal(expectedExit, result);
        Assert.Equal(2, lidarr.Requests.Count);
        if (expectedExit == 0)
        {
            Assert.Contains("Downstairs Search", output.ToString());
            Assert.DoesNotContain("Upstairs Search", output.ToString());
        }
        else Assert.Contains("Choose one Bandcamp indexer", error.ToString());
        AssertSecretSafe(output, error);
    }

    [Fact]
    public async Task Indexer_test_failure_names_the_indexer_and_keeps_response_secrets_private()
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", HttpStatusCode.OK, Clients());
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/indexer", HttpStatusCode.OK, "[" + Indexer() + "," + Indexer(99, "Other provider").Replace("BandcampIndexer", "OtherIndexer") + "]");
        lidarr.Enqueue(HttpMethod.Put, "/api/v1/downloadclient/7", HttpStatusCode.Accepted, Client());
        lidarr.Enqueue(HttpMethod.Post, "/api/v1/downloadclient/test", HttpStatusCode.OK, "{}");
        lidarr.Enqueue(HttpMethod.Put, "/api/v1/indexer/11", HttpStatusCode.Accepted, Indexer());
        lidarr.Enqueue(HttpMethod.Post, "/api/v1/indexer/test", HttpStatusCode.BadRequest, Cookie + ApiKey);
        var output = new StringWriter();
        var error = new StringWriter();
        var result = await Run(lidarr, Cookie, output, error,
            "--url", "http://lidarr.test", "--api-key", ApiKey, "--update-indexer");
        Assert.Equal(1, result);
        Assert.Contains("Lidarr indexer Test failed with HTTP 400", error.ToString());
        Assert.DoesNotContain("download client", error.ToString());
        AssertSecretSafe(output, error);
    }

    [Fact]
    public async Task Single_bandcamp_client_is_updated_and_tested()
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", HttpStatusCode.OK, Clients());
        lidarr.Enqueue(HttpMethod.Put, "/api/v1/downloadclient/7", HttpStatusCode.Accepted, Client());
        lidarr.Enqueue(HttpMethod.Post, "/api/v1/downloadclient/test", HttpStatusCode.OK, "{}");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CookiePushCommand.RunAsync(
            ["--url", "http://lidarr.test:8686", "--api-key", ApiKey],
            new StringReader(Cookie),
            output,
            error,
            _ => null,
            lidarr);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, lidarr.Requests.Count);
        Assert.All(lidarr.Requests, request => Assert.Equal(ApiKey, request.ApiKey));
        Assert.Equal("identity=" + Cookie, lidarr.Requests[1].Body?["fields"]?.AsArray()
            .Single(field => field?["name"]?.GetValue<string>() == "cookies")?["value"]?.GetValue<string>());
        Assert.Equal(lidarr.Requests[1].Body?.ToJsonString(), lidarr.Requests[2].Body?.ToJsonString());
        Assert.Contains("Bandcamp", output.ToString());
        Assert.Contains("Test passed", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
        AssertSecretSafe(output, error);
    }

    [Fact]
    public async Task Update_indexer_option_updates_and_tests_the_only_bandcamp_indexer()
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", HttpStatusCode.OK, Clients());
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/indexer", HttpStatusCode.OK, "[" + Indexer() + "," + Indexer(99, "Other provider").Replace("BandcampIndexer", "OtherIndexer") + "]");
        lidarr.Enqueue(HttpMethod.Put, "/api/v1/downloadclient/7", HttpStatusCode.Accepted, Client());
        lidarr.Enqueue(HttpMethod.Post, "/api/v1/downloadclient/test", HttpStatusCode.OK, "{}");
        lidarr.Enqueue(HttpMethod.Put, "/api/v1/indexer/11", HttpStatusCode.Accepted, Indexer());
        lidarr.Enqueue(HttpMethod.Post, "/api/v1/indexer/test", HttpStatusCode.OK, "{}");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Run(lidarr, Cookie, output, error,
            "--url", "http://lidarr.test", "--api-key", ApiKey, "--update-indexer");

        Assert.Equal(0, exitCode);
        Assert.Equal(6, lidarr.Requests.Count);
        Assert.Equal("identity=" + Cookie, lidarr.Requests[4].Body?["fields"]?.AsArray()
            .Single(field => field?["name"]?.GetValue<string>() == "cookies")?["value"]?.GetValue<string>());
        Assert.Equal(lidarr.Requests[4].Body?.ToJsonString(), lidarr.Requests[5].Body?.ToJsonString());
        Assert.Contains("indexer Test passed", output.ToString());
        AssertSecretSafe(output, error);
    }

    [Theory]
    [InlineData("--indexer-id", "12")]
    [InlineData("--indexer-name", "Downstairs Search")]
    public async Task Indexer_can_be_chosen_by_id_or_name(string flag, string value)
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", HttpStatusCode.OK, Clients());
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/indexer", HttpStatusCode.OK,
            "[" + Indexer(11, "Upstairs Search") + "," + Indexer(12, "Downstairs Search") + "]");
        lidarr.Enqueue(HttpMethod.Put, "/api/v1/downloadclient/7", HttpStatusCode.Accepted, Client());
        lidarr.Enqueue(HttpMethod.Post, "/api/v1/downloadclient/test", HttpStatusCode.OK, "{}");
        lidarr.Enqueue(HttpMethod.Put, "/api/v1/indexer/12", HttpStatusCode.Accepted, Indexer(12, "Downstairs Search"));
        lidarr.Enqueue(HttpMethod.Post, "/api/v1/indexer/test", HttpStatusCode.OK, "{}");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Run(lidarr, Cookie, output, error,
            "--url", "http://lidarr.test", "--api-key", ApiKey, "--update-indexer", flag, value);

        Assert.Equal(0, exitCode);
        Assert.Equal("Downstairs Search", lidarr.Requests[4].Body?["name"]?.GetValue<string>());
        AssertSecretSafe(output, error);
    }

    [Fact]
    public async Task Several_bandcamp_clients_require_an_id_or_name()
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", HttpStatusCode.OK, "[" + Client(7, "Upstairs") + "," + Client(8, "Downstairs") + "]");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Run(lidarr, Cookie, output, error);

        Assert.Equal(2, exitCode);
        Assert.Single(lidarr.Requests);
        Assert.Contains("--client-id", error.ToString());
        Assert.Contains("--client-name", error.ToString());
        AssertSecretSafe(output, error);
    }

    [Fact]
    public async Task Failed_lidarr_test_is_a_clear_secret_safe_error()
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", HttpStatusCode.OK, Clients());
        lidarr.Enqueue(HttpMethod.Put, "/api/v1/downloadclient/7", HttpStatusCode.Accepted, Client());
        lidarr.Enqueue(HttpMethod.Post, "/api/v1/downloadclient/test", HttpStatusCode.BadRequest, Cookie + ApiKey);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Run(lidarr, Cookie, output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Test failed", error.ToString());
        AssertSecretSafe(output, error);
    }

    [Fact]
    public async Task Unreachable_lidarr_is_a_clear_secret_safe_error()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Run(new UnreachableApi(), Cookie, output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Could not reach Lidarr", error.ToString());
        AssertSecretSafe(output, error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Bad_api_key_is_a_clear_secret_safe_error(HttpStatusCode status)
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", status, Cookie + ApiKey);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Run(lidarr, Cookie, output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("API key", error.ToString());
        AssertSecretSafe(output, error);
    }

    [Fact]
    public async Task No_bandcamp_client_is_a_clear_error()
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", HttpStatusCode.OK, "[]");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Run(lidarr, Cookie, output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("No Bandcamp download client found", error.ToString());
        AssertSecretSafe(output, error);
    }

    [Fact]
    public async Task Lidarr_settings_can_come_from_environment_variables()
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/lidarr/api/v1/downloadclient", HttpStatusCode.OK, Clients());
        lidarr.Enqueue(HttpMethod.Put, "/lidarr/api/v1/downloadclient/7", HttpStatusCode.Accepted, Client());
        lidarr.Enqueue(HttpMethod.Post, "/lidarr/api/v1/downloadclient/test", HttpStatusCode.OK, "{}");
        var output = new StringWriter();
        var error = new StringWriter();
        var environment = new Dictionary<string, string?>
        {
            ["LIDARR_URL"] = "http://lidarr.test/lidarr",
            ["LIDARR_API_KEY"] = ApiKey,
        };

        var exitCode = await CookiePushCommand.RunAsync(
            [],
            new StringReader("identity=" + Cookie + "\n"),
            output,
            error,
            name => environment.GetValueOrDefault(name),
            lidarr);

        Assert.Equal(0, exitCode);
        Assert.Equal("identity=" + Cookie, lidarr.Requests[1].Body?["fields"]?[0]?["value"]?.GetValue<string>());
        AssertSecretSafe(output, error);
    }

    [Fact]
    public async Task Help_does_not_require_connection_settings_or_read_stdin()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CookiePushCommand.RunAsync(
            ["--help"],
            new ThrowingReader(),
            output,
            error,
            _ => null,
            new MockLidarrApi());

        Assert.Equal(0, exitCode);
        Assert.Contains("--client-id", output.ToString());
        Assert.Contains("--print-cookie", output.ToString());
        Assert.Contains("--dry-run", output.ToString());
        Assert.Contains("opens a visible browser", output.ToString());
        Assert.Contains("LIDARR_API_KEY", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData("--client-id", "not-a-number")]
    [InlineData("--client-id")]
    [InlineData("--unknown")]
    public async Task Invalid_arguments_are_usage_errors(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var fullArgs = new[] { "--url", "http://lidarr.test", "--api-key", ApiKey }.Concat(args).ToArray();

        var exitCode = await Run(new MockLidarrApi(), Cookie, output, error, fullArgs);

        Assert.Equal(2, exitCode);
        Assert.StartsWith("Error:", error.ToString());
        AssertSecretSafe(output, error);
    }

    [Theory]
    [InlineData("", "--url", "http://lidarr.test", "--api-key", ApiKey)]
    [InlineData(Cookie, "--api-key", ApiKey)]
    [InlineData(Cookie, "--url", "http://lidarr.test")]
    public async Task Missing_cookie_or_connection_settings_is_a_usage_error(string cookie, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Run(new MockLidarrApi(), cookie, output, error, args);

        Assert.Equal(2, exitCode);
        Assert.StartsWith("Error:", error.ToString());
        AssertSecretSafe(output, error);
    }

    [Theory]
    [InlineData("--client-id", "8")]
    [InlineData("--client-name", "Downstairs")]
    public async Task Client_can_be_chosen_by_id_or_name(string flag, string value)
    {
        var lidarr = new MockLidarrApi();
        lidarr.Enqueue(HttpMethod.Get, "/api/v1/downloadclient", HttpStatusCode.OK, "[" + Client(7, "Upstairs") + "," + Client(8, "Downstairs") + "]");
        lidarr.Enqueue(HttpMethod.Put, "/api/v1/downloadclient/8", HttpStatusCode.Accepted, Client(8, "Downstairs"));
        lidarr.Enqueue(HttpMethod.Post, "/api/v1/downloadclient/test", HttpStatusCode.OK, "{}");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Run(lidarr, Cookie, output, error, "--url", "http://lidarr.test", "--api-key", ApiKey, flag, value);

        Assert.Equal(0, exitCode);
        Assert.Equal("Downstairs", lidarr.Requests[1].Body?["name"]?.GetValue<string>());
        AssertSecretSafe(output, error);
    }

    private static Task<int> Run(HttpMessageHandler lidarr, string cookie, StringWriter output, StringWriter error, params string[] args)
    {
        return CookiePushCommand.RunAsync(
            args.Length > 0 ? args : ["--url", "http://lidarr.test:8686", "--api-key", ApiKey],
            new StringReader(cookie),
            output,
            error,
            _ => null,
            lidarr);
    }

    private static void AssertSecretSafe(StringWriter output, StringWriter error)
    {
        Assert.DoesNotContain(Cookie, output.ToString());
        Assert.DoesNotContain(Cookie, error.ToString());
        Assert.DoesNotContain(ApiKey, output.ToString());
        Assert.DoesNotContain(ApiKey, error.ToString());
    }

    private static string Clients() => "[" + Client() + "]";

    private static string Client(int id = 7, string name = "Bandcamp") => $$"""
        {
          "id": {{id}},
          "name": {{System.Text.Json.JsonSerializer.Serialize(name)}},
          "implementation": "BandcampDownloadClient",
          "fields": [
            { "name": "cookies", "label": "Session Cookie (optional)", "value": "identity=old" },
            { "name": "downloadPath", "value": "/downloads" }
          ]
        }
        """;

    private static string Indexer(int id = 11, string name = "Bandcamp Search") => $$"""
        {
          "id": {{id}},
          "name": {{System.Text.Json.JsonSerializer.Serialize(name)}},
          "implementation": "BandcampIndexer",
          "fields": [
            { "name": "cookies", "label": "Session Cookies", "value": "identity=old" },
            { "name": "minimumPopularity", "value": 0 }
          ]
        }
        """;

    private sealed class ThrowingReader : TextReader
    {
        public override string ReadToEnd() => throw new InvalidOperationException("stdin should not be read");

        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("stdin should not be read");
    }

    private sealed class UnreachableApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Connection refused", null, HttpStatusCode.ServiceUnavailable);
        }
    }

    private sealed class MockLidarrApi : HttpMessageHandler
    {
        private readonly Queue<ExpectedRequest> _expected = new();

        public List<RecordedRequest> Requests { get; } = new();

        public void Enqueue(HttpMethod method, string path, HttpStatusCode status, string body)
        {
            _expected.Enqueue(new ExpectedRequest(method, path, status, body));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotEmpty(_expected);
            var expected = _expected.Dequeue();
            Assert.Equal(expected.Method, request.Method);
            Assert.Equal(expected.Path, request.RequestUri?.AbsolutePath);
            request.Headers.TryGetValues("X-Api-Key", out var apiKeys);
            var body = request.Content is null
                ? null
                : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?.AsObject();
            Requests.Add(new RecordedRequest(apiKeys?.SingleOrDefault(), body));
            return new HttpResponseMessage(expected.Status)
            {
                Content = new StringContent(expected.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record ExpectedRequest(HttpMethod Method, string Path, HttpStatusCode Status, string Body);

    private sealed record RecordedRequest(string? ApiKey, JsonObject? Body);
}
