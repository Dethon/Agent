using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Infrastructure.Clients;

namespace Tests.Integration.Fixtures;

public class QBittorrentFixture : IAsyncLifetime
{
    private const int WebUiPort = 8080;
    private const string DefaultUsername = "admin";
    private const string DefaultPassword = "adminadmin";

    private IContainer _container = null!;

    private string ApiUrl { get; set; } = null!;
    private static string Username => DefaultUsername;
    private static string Password => DefaultPassword;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("qbittorrentofficial/qbittorrent-nox:5.1.2-2")
            .WithPortBinding(WebUiPort, true)
            .WithEnvironment("QBT_LEGAL_NOTICE", "confirm")
            .WithEnvironment("QBT_WEBUI_PORT", WebUiPort.ToString())
            .WithEnvironment("QBT_TORRENTING_PORT", "6881")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilExternalTcpPortIsAvailable(WebUiPort))
            .Build();

        await _container.StartAsync();

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(WebUiPort);
        ApiUrl = $"http://{host}:{port}/api/v2/";

        // Wait for container to be ready and configure credentials
        await WaitForContainerAndConfigure();
    }

    private async Task WaitForContainerAndConfigure()
    {
        // Wait for container logs to have the temporary password
        string? tempPassword = null;
        for (var i = 0; i < 60; i++)
        {
            var logs = await _container.GetLogsAsync();
            var allLines = (logs.Stdout + logs.Stderr)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");
            var passwordLine = allLines.Split('\n')
                .FirstOrDefault(l => l.Contains("temporary password"));

            if (passwordLine != null)
            {
                // Extract password from: "A temporary password is provided for this session: <password>"
                var parts = passwordLine.Split(':');
                if (parts.Length >= 2)
                {
                    tempPassword = parts[^1].Trim();
                }

                if (!string.IsNullOrEmpty(tempPassword))
                {
                    break;
                }
            }

            await Task.Delay(1000);
        }

        if (string.IsNullOrEmpty(tempPassword))
        {
            throw new TimeoutException("Could not get qBittorrent temporary password from logs");
        }

        // Give qBittorrent extra time to initialize its WebUI
        await Task.Delay(3000);

        // Login with temp password and set new password
        var cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer
        };
        using var httpClient = new HttpClient(handler);
        httpClient.BaseAddress = new Uri(ApiUrl);

        // qBittorrent has CSRF protection - Host header must match internal port
        httpClient.DefaultRequestHeaders.Host = $"localhost:{WebUiPort}";
        httpClient.DefaultRequestHeaders.Add("Origin", $"http://localhost:{WebUiPort}");
        httpClient.DefaultRequestHeaders.Add("Referer", $"http://localhost:{WebUiPort}/");

        // Retry login a few times
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var loginContent = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("username", DefaultUsername),
                new KeyValuePair<string, string>("password", tempPassword)
            ]);
            var loginResponse = await httpClient.PostAsync("auth/login", loginContent);
            var responseContent = await loginResponse.Content.ReadAsStringAsync();

            if (loginResponse.IsSuccessStatusCode && responseContent.Contains("Ok"))
            {
                break;
            }

            if (attempt == 9)
            {
                throw new InvalidOperationException(
                    $"Failed to login to qBittorrent after 10 attempts. Password: {tempPassword}, Response: {loginResponse.StatusCode}, Body: {responseContent}");
            }

            await Task.Delay(1000);
        }

        // Set new password
        var setPasswordContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("json", $$"""{"web_ui_password":"{{DefaultPassword}}"}""")
        ]);
        var setPasswordResponse = await httpClient.PostAsync("app/setPreferences", setPasswordContent);
        setPasswordResponse.EnsureSuccessStatusCode();
    }

    public QBittorrentDownloadClient CreateClient()
    {
        var cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer
        };
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(ApiUrl)
        };

        // qBittorrent has CSRF protection - Host header must match internal port
        httpClient.DefaultRequestHeaders.Host = $"localhost:{WebUiPort}";
        httpClient.DefaultRequestHeaders.Add("Origin", $"http://localhost:{WebUiPort}");
        httpClient.DefaultRequestHeaders.Add("Referer", $"http://localhost:{WebUiPort}/");

        return new QBittorrentDownloadClient(httpClient, cookieContainer, Username, Password);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}