using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Infrastructure.Clients.HomeAssistant;

namespace Tests.Integration.Fixtures;

// Boots a real Home Assistant container (ghcr.io/home-assistant/home-assistant:stable) with a
// pre-seeded /config volume so the REST API is reachable without going through HA's interactive
// onboarding flow. The seeding consists of:
//   - configuration.yaml: minimal core + an `input_boolean.test_switch` so the call_service
//     test has a real entity to toggle.
//   - .storage/auth: one owner user + groups + a `long_lived_access_token` refresh token whose
//     `jwt_key` we control. We sign an HS256 JWT with that key and use it as the Bearer token.
//   - .storage/onboarding: marks every onboarding step as done.
//
// HA cold-starts in ~30-60s on a fresh /config; the readiness loop polls `/api/` with the
// bearer token until 200.
public class HomeAssistantFixture : IAsyncLifetime
{
    private const int HaPort = 8123;
    private const string ContainerImage = "ghcr.io/home-assistant/home-assistant:stable";

    private static readonly TimeSpan _readyTimeout = TimeSpan.FromMinutes(3);

    private IContainer _container = null!;
    private string _configDir = null!;

    public string BaseUrl { get; private set; } = null!;
    public string Token { get; private set; } = null!;
    public const string TestEntityId = "input_boolean.test_switch";

    public async Task InitializeAsync()
    {
        _configDir = Path.Combine(Path.GetTempPath(), $"ha-test-{Guid.NewGuid():N}");
        Token = SeedConfig(_configDir);

        _container = new ContainerBuilder(ContainerImage)
            .WithPortBinding(HaPort, true)
            .WithBindMount(_configDir, "/config")
            .WithEnvironment("TZ", "UTC")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(HaPort))
            .Build();

        await _container.StartAsync();

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(HaPort);
        BaseUrl = $"http://{host}:{port}";

        await WaitForApiReadyAsync();
    }

    public HomeAssistantClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/") };
        return new HomeAssistantClient(http, Token);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _container.DisposeAsync();
        }
        finally
        {
            if (_configDir is not null && Directory.Exists(_configDir))
            {
                try
                { Directory.Delete(_configDir, recursive: true); }
                catch { /* best effort — container may still hold handles momentarily */ }
            }
        }
    }

    // HA serves `/api/` long before YAML-loaded integrations finish registering. Wait until
    // both the API responds 200 *and* `/api/services` lists the seeded `input_boolean` domain
    // so tests don't race the integration loader.
    private async Task WaitForApiReadyAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var deadline = DateTime.UtcNow + _readyTimeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var apiResponse = await http.GetAsync("api/");
                if (apiResponse.StatusCode == HttpStatusCode.OK)
                {
                    using var servicesResponse = await http.GetAsync("api/services");
                    if (servicesResponse.IsSuccessStatusCode)
                    {
                        var json = await servicesResponse.Content.ReadAsStringAsync();
                        if (json.Contains("\"input_boolean\""))
                        {
                            return;
                        }
                        lastError = new InvalidOperationException("input_boolean domain not yet registered");
                    }
                }
                else
                {
                    lastError = new InvalidOperationException($"HA /api/ returned {(int)apiResponse.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(1500);
        }

        var logs = await _container.GetLogsAsync();
        throw new TimeoutException(
            $"Home Assistant did not become ready within {_readyTimeout}. Last error: {lastError?.Message}.\n" +
            $"--- container stdout (tail) ---\n{Tail(logs.Stdout, 4000)}\n" +
            $"--- container stderr (tail) ---\n{Tail(logs.Stderr, 4000)}");
    }

    private static string Tail(string s, int chars) => s.Length <= chars ? s : s[^chars..];

    // Returns the long-lived access token (Bearer JWT) for the seeded user.
    private static string SeedConfig(string configDir)
    {
        Directory.CreateDirectory(Path.Combine(configDir, ".storage"));

        // configuration.yaml: minimal core + a known entity for the call_service test +
        // a `script.echo` that returns a response dict so we can exercise the
        // return_response code path against a real HA.
        File.WriteAllText(Path.Combine(configDir, "configuration.yaml"),
            """
            homeassistant:
              name: Test
              unit_system: metric
              time_zone: UTC

            http:
              server_host: 0.0.0.0

            api:

            input_boolean:
              test_switch:
                name: Test Switch
                initial: false

            script:
              echo:
                sequence:
                  - variables:
                      out:
                        echoed: "{{ value | default('echo-default') }}"
                  - stop: ""
                    response_variable: out
                fields:
                  value:
                    required: false
            """);

        // Random IDs so each fixture run is independent. HA's auth schema accepts arbitrary hex.
        var userId = RandomHex(16);
        var refreshTokenId = RandomHex(16);
        var refreshTokenHash = RandomHex(32);
        var jwtKey = RandomHex(32);
        var tokenVersion = RandomHex(16);
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o");

        var auth = new
        {
            version = 1,
            minor_version = 1,
            key = "auth",
            data = new
            {
                users = new[]
                {
                    new
                    {
                        id = userId,
                        group_ids = new[] { "system-admin" },
                        is_owner = true,
                        is_active = true,
                        name = "Test",
                        system_generated = false,
                        local_only = false
                    }
                },
                groups = new[]
                {
                    new { id = "system-admin", name = "Admin" },
                    new { id = "system-users", name = "Users" },
                    new { id = "system-read-only", name = "Read-only" }
                },
                credentials = Array.Empty<object>(),
                refresh_tokens = new object[]
                {
                    new
                    {
                        id = refreshTokenId,
                        user_id = userId,
                        client_id = (string?)null,
                        client_name = "Test LLAT",
                        client_icon = (string?)null,
                        token_type = "long_lived_access_token",
                        created_at = createdAt,
                        access_token_expiration = 315360000.0,
                        token = refreshTokenHash,
                        jwt_key = jwtKey,
                        last_used_at = (string?)null,
                        last_used_ip = (string?)null,
                        credential_id = (string?)null,
                        version = tokenVersion
                    }
                }
            }
        };

        var onboarding = new
        {
            version = 4,
            minor_version = 1,
            key = "onboarding",
            data = new
            {
                done = new[] { "user", "core_config", "integration", "analytics" }
            }
        };

        var jsonOpts = new JsonSerializerOptions { WriteIndented = false };
        File.WriteAllText(Path.Combine(configDir, ".storage", "auth"), JsonSerializer.Serialize(auth, jsonOpts));
        File.WriteAllText(Path.Combine(configDir, ".storage", "onboarding"), JsonSerializer.Serialize(onboarding, jsonOpts));

        return CreateAccessToken(refreshTokenId, jwtKey);
    }

    // HA's long-lived access tokens are HS256 JWTs with `iss = refresh_token.id`,
    // signed by `refresh_token.jwt_key`. See homeassistant/auth/__init__.py.
    private static string CreateAccessToken(string refreshTokenId, string jwtKey)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = now + 315_360_000L;
        var headerJson = """{"typ":"JWT","alg":"HS256"}""";
        var payloadJson = $$"""{"iss":"{{refreshTokenId}}","iat":{{now}},"exp":{{exp}}}""";

        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{headerB64}.{payloadB64}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(jwtKey));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string RandomHex(int byteLength)
    {
        var buf = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }
}