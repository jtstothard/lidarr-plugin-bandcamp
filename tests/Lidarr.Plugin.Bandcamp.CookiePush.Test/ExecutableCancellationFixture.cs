using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.CookiePush.Test;

public sealed class ExecutableCancellationFixture
{
    [Theory]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Executable_rejects_cross_origin_redirect_without_forwarding_credentials(int status)
    {
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        target.Start();
        var port = ((IPEndPoint)source.LocalEndpoint).Port;
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        info.Environment["LIDARR_API_KEY"] = "redirect-api-secret";
        info.ArgumentList.Add(typeof(CookiePushCommand).Assembly.Location);
        info.ArgumentList.Add("--url");
        info.ArgumentList.Add($"http://127.0.0.1:{port}");
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync("redirect-cookie-secret");
            process.StandardInput.Close();
            using var connection = await source.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))) { }
            var response = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Redirect\r\nLocation: http://127.0.0.1:{targetPort}/external-login?secret=do-not-log\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response);
            // A vulnerable executable follows the redirect and waits on this listener.
            // Bound the wait and always terminate it in finally on failure.
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, process.ExitCode);
            Assert.False(target.Pending());
            var diagnostics = await output + await error;
            Assert.Contains("redirects are disabled", diagnostics);
            Assert.DoesNotContain("redirect-api-secret", diagnostics);
            Assert.DoesNotContain("redirect-cookie-secret", diagnostics);
            Assert.DoesNotContain("do-not-log", diagnostics);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Sigint_cancels_executable_during_pending_http_without_stack_or_secrets()
    {
        // SIGINT delivery is Unix-specific; command/session tests also run on Windows.
        if (OperatingSystem.IsWindows()) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // CI installs SDK 10; allow this net8 executable to use its installed runtime.
        info.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        info.ArgumentList.Add(typeof(CookiePushCommand).Assembly.Location);
        info.ArgumentList.Add("--url");
        info.ArgumentList.Add($"http://127.0.0.1:{port}");
        info.Environment["LIDARR_API_KEY"] = "executable-test-api-secret";
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync("executable-test-cookie-secret");
            process.StandardInput.Close();
            using var connection = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            // Wait until the request is in flight; deliberately never send a response.
            using var request = new StreamReader(connection.GetStream());
            Assert.Equal("GET /api/v1/downloadclient HTTP/1.1", await request.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            using var signal = Process.Start(new ProcessStartInfo("/bin/kill")
            {
                ArgumentList = { "-INT", process.Id.ToString() },
                UseShellExecute = false,
            })!;
            await signal.WaitForExitAsync();
            Assert.Equal(0, signal.ExitCode);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(130, process.ExitCode);
            Assert.Equal("Cancelled. Completed Lidarr writes are not rolled back; check settings before retrying." + Environment.NewLine, await error);
            Assert.Equal(string.Empty, await output);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
