# Environment-Configurable Users Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable WebChat user configuration via environment variables in Docker Compose, replacing static JSON files.

**Architecture:** Move user configuration from static `users.json` files to `WebChat/appsettings.json`. Extend the existing `/api/config` endpoint to include users. The client fetches users from this API instead of a static file. Environment variables override appsettings using the standard .NET configuration pattern (`USERS__0__ID=Alice`).

**Tech Stack:** ASP.NET Core Configuration, Blazor WebAssembly, SignalR

---

### Task 1: Add Users to WebChat Configuration

**Files:**
- Modify: `WebChat/appsettings.json`

**Step 1: Add default users array to appsettings**

Replace contents of `WebChat/appsettings.json`:

```json
{
  "AgentUrl": "http://localhost:5000",
  "Users": [
    { "Id": "Alice", "AvatarUrl": "avatars/alice.png" },
    { "Id": "Bob", "AvatarUrl": "avatars/bob.png" },
    { "Id": "Charlie", "AvatarUrl": "avatars/charlie.png" }
  ]
}
```

**Step 2: Verify JSON is valid**

Run: `cd WebChat && dotnet build --no-restore`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add WebChat/appsettings.json
git commit -m "feat: add users configuration to WebChat appsettings"
```

---

### Task 2: Update WebChat API Endpoint

**Files:**
- Modify: `WebChat/Program.cs`

**Step 1: Update /api/config endpoint to include users**

Replace contents of `WebChat/Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGet("/api/config", (IConfiguration config) =>
{
    var users = config.GetSection("Users").Get<UserConfig[]>() ?? [];
    return new AppConfig(
        config["AgentUrl"] ?? "http://localhost:5000",
        users);
});

app.MapFallbackToFile("index.html");

await app.RunAsync();

record UserConfig(string Id, string AvatarUrl);
record AppConfig(string AgentUrl, UserConfig[] Users);
```

**Step 2: Verify build succeeds**

Run: `cd WebChat && dotnet build`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add WebChat/Program.cs
git commit -m "feat: include users in /api/config endpoint"
```

---

### Task 3: Update Client AppConfig Model

**Files:**
- Modify: `WebChat.Client/Services/ChatConnectionService.cs:81`

**Step 1: Update AppConfig record to include Users**

In `WebChat.Client/Services/ChatConnectionService.cs`, replace line 81:

```csharp
internal record AppConfig(string? AgentUrl);
```

With:

```csharp
internal record AppConfig(string? AgentUrl, UserConfigDto[]? Users);
internal record UserConfigDto(string Id, string AvatarUrl);
```

**Step 2: Verify build succeeds**

Run: `cd WebChat.Client && dotnet build`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add WebChat.Client/Services/ChatConnectionService.cs
git commit -m "feat: add Users to client AppConfig model"
```

---

### Task 4: Create ConfigService for Shared Config Access

**Files:**
- Create: `WebChat.Client/Services/ConfigService.cs`
- Modify: `WebChat.Client/Extensions/ServiceCollectionExtensions.cs`

**Step 1: Create ConfigService**

Create `WebChat.Client/Services/ConfigService.cs`:

```csharp
using System.Net.Http.Json;
using WebChat.Client.Models;

namespace WebChat.Client.Services;

public sealed class ConfigService(HttpClient httpClient)
{
    private AppConfig? _config;

    public async Task<AppConfig> GetConfigAsync()
    {
        return _config ??= await httpClient.GetFromJsonAsync<AppConfig>("/api/config")
            ?? new AppConfig(null, []);
    }
}

internal record AppConfig(string? AgentUrl, UserConfig[]? Users);
```

**Step 2: Register ConfigService in DI**

In `WebChat.Client/Extensions/ServiceCollectionExtensions.cs`, add after the existing service registrations (around line 25):

```csharp
.AddSingleton<ConfigService>()
```

Add the using at the top if not present:

```csharp
using WebChat.Client.Services;
```

**Step 3: Verify build succeeds**

Run: `cd WebChat.Client && dotnet build`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add WebChat.Client/Services/ConfigService.cs WebChat.Client/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat: add ConfigService for shared config access"
```

---

### Task 5: Update ChatConnectionService to Use ConfigService

**Files:**
- Modify: `WebChat.Client/Services/ChatConnectionService.cs`

**Step 1: Update ChatConnectionService to use ConfigService**

Replace the constructor and ConnectAsync method in `WebChat.Client/Services/ChatConnectionService.cs`:

```csharp
public sealed class ChatConnectionService(
    ConfigService configService,
    ConnectionEventDispatcher connectionEventDispatcher) : IChatConnectionService
{
    private readonly ConnectionEventDispatcher _connectionEventDispatcher = connectionEventDispatcher;
    public bool IsConnected => HubConnection?.State == HubConnectionState.Connected;
    public bool IsReconnecting => HubConnection?.State == HubConnectionState.Reconnecting;

    public HubConnection? HubConnection { get; private set; }

    public event Action? OnStateChanged;
    public event Func<Task>? OnReconnected;
    public event Action? OnReconnecting;

    public async Task ConnectAsync()
    {
        if (HubConnection is not null)
        {
            return;
        }

        var config = await configService.GetConfigAsync();
        var agentUrl = config.AgentUrl ?? "http://localhost:5000";
        var hubUrl = $"{agentUrl.TrimEnd('/')}/hubs/chat";

        HubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new AggressiveRetryPolicy())
            .WithServerTimeout(TimeSpan.FromMinutes(3))
            .WithKeepAliveInterval(TimeSpan.FromSeconds(10))
            .Build();

        HubConnection.Closed += exception =>
        {
            _connectionEventDispatcher.HandleClosed(exception);
            OnStateChanged?.Invoke();
            return Task.CompletedTask;
        };

        HubConnection.Reconnecting += _ =>
        {
            _connectionEventDispatcher.HandleReconnecting();
            OnReconnecting?.Invoke();
            OnStateChanged?.Invoke();
            return Task.CompletedTask;
        };

        HubConnection.Reconnected += async _ =>
        {
            _connectionEventDispatcher.HandleReconnected();
            if (OnReconnected is not null)
            {
                await OnReconnected.Invoke();
            }

            OnStateChanged?.Invoke();
        };

        _connectionEventDispatcher.HandleConnecting();
        await HubConnection.StartAsync();
        _connectionEventDispatcher.HandleConnected();
        OnStateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (HubConnection is not null)
        {
            await HubConnection.DisposeAsync();
        }
    }
}
```

**Step 2: Remove the old AppConfig and UserConfigDto records** (lines 81-82)

Delete these lines from the bottom of the file:

```csharp
internal record AppConfig(string? AgentUrl, UserConfigDto[]? Users);
internal record UserConfigDto(string Id, string AvatarUrl);
```

**Step 3: Remove unused HttpClient using**

Remove `using System.Net.Http.Json;` if no longer needed.

**Step 4: Verify build succeeds**

Run: `cd WebChat.Client && dotnet build`
Expected: Build succeeds

**Step 5: Commit**

```bash
git add WebChat.Client/Services/ChatConnectionService.cs
git commit -m "refactor: use ConfigService in ChatConnectionService"
```

---

### Task 6: Update UserIdentityEffect to Use ConfigService

**Files:**
- Modify: `WebChat.Client/State/Effects/UserIdentityEffect.cs`

**Step 1: Update UserIdentityEffect to fetch from ConfigService**

Replace contents of `WebChat.Client/State/Effects/UserIdentityEffect.cs`:

```csharp
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.State.Effects;

public sealed class UserIdentityEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConfigService _configService;
    private readonly ILocalStorageService _localStorage;
    private const string StorageKey = "selectedUserId";

    public UserIdentityEffect(
        Dispatcher dispatcher,
        ConfigService configService,
        ILocalStorageService localStorage)
    {
        _dispatcher = dispatcher;
        _configService = configService;
        _localStorage = localStorage;

        dispatcher.RegisterHandler<Initialize>(HandleInitialize);
        dispatcher.RegisterHandler<SelectUser>(HandleSelectUser);
    }

    private void HandleInitialize(Initialize action)
    {
        _ = LoadUsersAsync();
    }

    private async Task LoadUsersAsync()
    {
        _dispatcher.Dispatch(new LoadUsers());

        try
        {
            var config = await _configService.GetConfigAsync();
            var users = config.Users?.Select(u => new UserConfig(u.Id, u.AvatarUrl)).ToList() ?? [];
            _dispatcher.Dispatch(new UsersLoaded(users));

            var savedUserId = await _localStorage.GetAsync(StorageKey);
            if (!string.IsNullOrEmpty(savedUserId) && users.Any(u => u.Id == savedUserId))
            {
                _dispatcher.Dispatch(new SelectUser(savedUserId));
            }
        }
        catch (HttpRequestException)
        {
            _dispatcher.Dispatch(new UsersLoaded([]));
        }
    }

    private void HandleSelectUser(SelectUser action)
    {
        _ = _localStorage.SetAsync(StorageKey, action.UserId);
    }

    public void Dispose()
    {
        // No subscriptions to dispose
    }
}
```

**Step 2: Verify build succeeds**

Run: `cd WebChat.Client && dotnet build`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add WebChat.Client/State/Effects/UserIdentityEffect.cs
git commit -m "refactor: fetch users from ConfigService instead of static file"
```

---

### Task 7: Remove Static users.json Files

**Files:**
- Delete: `WebChat.Client/wwwroot/users.json`
- Delete: `Agent/wwwroot/users.json`

**Step 1: Delete static users.json files**

```bash
rm WebChat.Client/wwwroot/users.json
rm Agent/wwwroot/users.json
```

**Step 2: Verify build succeeds**

Run: `dotnet build`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add -A
git commit -m "chore: remove static users.json files"
```

---

### Task 8: Remove UserConfigService from Agent

**Files:**
- Delete: `Agent/Services/UserConfigService.cs`
- Modify: `Agent/Modules/InjectorModule.cs:149`
- Modify: `Agent/Hubs/ChatHub.cs`

**Step 1: Delete UserConfigService.cs**

```bash
rm Agent/Services/UserConfigService.cs
```

**Step 2: Remove registration from InjectorModule.cs**

In `Agent/Modules/InjectorModule.cs`, remove line 149:

```csharp
.AddSingleton<UserConfigService>()
```

**Step 3: Update ChatHub to remove UserConfigService dependency**

In `Agent/Hubs/ChatHub.cs`, remove `UserConfigService userConfigService` from the constructor (line 22) and update `RegisterUser` method to accept any user ID:

Replace constructor (lines 16-22):

```csharp
public sealed class ChatHub(
    IAgentFactory agentFactory,
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    IThreadStateStore threadStateStore,
    WebChatMessengerClient messengerClient,
    INotifier hubNotifier) : Hub
```

Replace `RegisterUser` method (lines 33-43):

```csharp
public Task RegisterUser(string userId)
{
    if (string.IsNullOrWhiteSpace(userId))
    {
        throw new HubException("User ID cannot be empty");
    }

    Context.Items["UserId"] = userId;
    return Task.CompletedTask;
}
```

Remove the `using Agent.Services;` import (line 2).

**Step 4: Verify build succeeds**

Run: `dotnet build`
Expected: Build succeeds

**Step 5: Commit**

```bash
git add -A
git commit -m "refactor: remove UserConfigService, simplify RegisterUser"
```

---

### Task 9: Update Integration Test Fixture

**Files:**
- Modify: `Tests/Integration/Fixtures/WebChatServerFixture.cs`

**Step 1: Remove UserConfigService mock from WebChatServerFixture**

In `Tests/Integration/Fixtures/WebChatServerFixture.cs`, remove lines 101-110 (the UserConfigService mock setup):

```csharp
// Add UserConfigService with mock IWebHostEnvironment (returns test users)
var usersJson = """[{"id":"alice","username":"Alice","avatarUrl":"/avatars/alice.png"},{"id":"bob","username":"Bob","avatarUrl":"/avatars/bob.png"},{"id":"test-user","username":"Test User","avatarUrl":"/avatars/test.png"}]""";
var mockFileInfo = new Mock<IFileInfo>();
mockFileInfo.Setup(f => f.Exists).Returns(true);
mockFileInfo.Setup(f => f.CreateReadStream()).Returns(() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(usersJson)));
var mockFileProvider = new Mock<IFileProvider>();
mockFileProvider.Setup(p => p.GetFileInfo(It.IsAny<string>())).Returns(mockFileInfo.Object);
var mockWebHostEnv = new Mock<IWebHostEnvironment>();
mockWebHostEnv.Setup(e => e.WebRootFileProvider).Returns(mockFileProvider.Object);
builder.Services.AddSingleton(new UserConfigService(mockWebHostEnv.Object));
```

Also remove the `using Microsoft.Extensions.FileProviders;` import if no longer needed.

Remove `using Agent.Services;` import.

**Step 2: Verify tests compile**

Run: `dotnet build Tests`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add Tests/Integration/Fixtures/WebChatServerFixture.cs
git commit -m "test: remove UserConfigService from test fixture"
```

---

### Task 10: Run All Tests

**Step 1: Run the full test suite**

Run: `dotnet test`
Expected: All tests pass

**Step 2: If tests fail, fix issues and re-run**

**Step 3: Final commit if any fixes were needed**

```bash
git add -A
git commit -m "fix: resolve test failures after user config refactor"
```

---

### Task 11: Update Documentation

**Files:**
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Step 1: Update README.md User Identity Configuration section**

Replace the "User Identity Configuration" section in README.md:

```markdown
#### User Identity Configuration

WebChat supports multiple user identities configured via `WebChat/appsettings.json` or environment variables:

**appsettings.json:**
```json
{
  "Users": [
    { "Id": "Alice", "AvatarUrl": "avatars/alice.png" },
    { "Id": "Bob", "AvatarUrl": "avatars/bob.png" }
  ]
}
```

**Environment variables (Docker Compose):**
```env
USERS__0__ID=Alice
USERS__0__AVATARURL=avatars/alice.png
USERS__1__ID=Bob
USERS__1__AVATARURL=avatars/bob.png
```

Place avatar images in `WebChat.Client/wwwroot/avatars/`. Selected identity persists in browser local storage.
```

**Step 2: Update CLAUDE.md User Identity System section**

Replace the "User Identity System" section in CLAUDE.md:

```markdown
### User Identity System

WebChat supports multiple user identities with avatars, configured via environment variables:

- **ConfigService** (`WebChat.Client/Services/ConfigService.cs`) - Fetches and caches app config including users
- **UserIdentityStore** (`WebChat.Client/State/UserIdentity/`) - Client-side state for user selection
- **UserIdentityEffect** (`WebChat.Client/State/Effects/UserIdentityEffect.cs`) - Loads users from config and persists selection to local storage
- **UserIdentityPicker** (`WebChat.Client/Components/UserIdentityPicker.razor`) - Dropdown for identity selection
- **AvatarImage** (`WebChat.Client/Components/AvatarImage.razor`) - Avatar display with fallback to initials

Configuration via `WebChat/appsettings.json` or environment variables:
```json
{
  "Users": [{ "Id": "Alice", "AvatarUrl": "avatars/alice.png" }]
}
```

Environment override: `USERS__0__ID=Alice`, `USERS__0__AVATARURL=avatars/alice.png`
```

**Step 3: Remove App services pattern from CLAUDE.md**

In CLAUDE.md File Patterns table, remove the line:
```
| App services             | `Agent/Services/*.cs`                     |
```

**Step 4: Verify documentation is accurate**

Review the changes to ensure they reflect the new implementation.

**Step 5: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: update user identity configuration for env variables"
```

---

### Task 12: Verify End-to-End

**Step 1: Start WebChat with default config**

Run: `cd WebChat && dotnet run`

**Step 2: Open browser and verify users load**

Navigate to `http://localhost:5000` (or configured port)
Expected: User identity picker shows Alice, Bob, Charlie

**Step 3: Test environment variable override**

Stop server, then run with env var override:
```bash
USERS__0__ID=TestUser USERS__0__AVATARURL=avatars/test.png dotnet run
```
Expected: User identity picker shows only TestUser

**Step 4: Final verification commit**

```bash
git add -A
git commit -m "feat: complete env-configurable users implementation"
```
