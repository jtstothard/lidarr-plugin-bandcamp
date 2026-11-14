using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.CookiePush.Test;

public class SecretHygieneFixture
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HelperOutputNeverEchoesAnySecretOrCorrelatableDigest(bool throwTransport)
    {
        string[] secrets = ["password-53-private", "verification-53-private", "generated-53-private", "identity-53-private"];
        var payload = string.Join(" ", secrets.Concat(secrets.Select(s => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))))));
        var output = new StringWriter(); var error = new StringWriter();
        var result = await CookiePushCommand.RunAsync(["--client-name", "Owner", "--fan-id", "123"], new StringReader(secrets[3]), output, error,
            name => name == "LIDARR_URL" ? "https://lidarr.example.test" : secrets[2], new FaultHandler(payload, throwTransport));
        Assert.Equal(1, result);
        var captured = output.ToString() + error;
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, captured, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret[..12], captured, StringComparison.OrdinalIgnoreCase);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
            Assert.DoesNotContain(hash, captured, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(hash[..12], captured, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class FaultHandler(string payload, bool throwTransport) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (throwTransport) throw new HttpRequestException(payload);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(payload) });
        }
    }
}
