# Phase 6: Clean Architecture Alignment - Research

**Researched:** 2026-01-20
**Domain:** Clean Architecture layer separation, dependency inversion, DI registration patterns
**Confidence:** HIGH

## Summary

This phase addresses Clean Architecture violations in the WebChat implementation. The primary issue is that `INotifier` is defined in Domain but implemented in Agent/Hubs (`Notifier.cs`), which violates the dependency rule since the implementation uses `IHubContext<ChatHub>` - a SignalR-specific type that belongs at the Agent layer.

The solution follows the Adapter pattern: create an abstraction (`IHubNotificationSender`) in Domain that defines what notifications need to be sent, implement the actual notifier (`HubNotifier`) in Infrastructure using this abstraction, and keep the SignalR adapter (`HubNotificationAdapter`) in Agent/Hubs that wraps `IHubContext`.

Additionally, the WebChat.Client stores and effects need their DI registrations organized into extension methods following the Service Collection Extension Pattern.

**Primary recommendation:** Split the notification concern using the Adapter pattern - Domain defines what to notify, Infrastructure implements the business logic, Agent provides the SignalR transport.

## Standard Stack

The established libraries/tools for this domain:

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Microsoft.AspNetCore.SignalR | 10.0.x | Real-time communication | Built into ASP.NET Core |
| Microsoft.Extensions.DependencyInjection | 10.0.x | DI container | .NET standard DI |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| System.Reactive | 6.1.0 | Rx observables | Already in WebChat.Client for BehaviorSubject |

### No Alternatives Needed
This phase uses only built-in .NET patterns and existing codebase conventions. No new libraries required.

**Installation:**
No additional packages needed.

## Architecture Patterns

### Recommended Project Structure
```
Domain/
  Contracts/
    INotifier.cs              # Already exists - defines notification methods
    IHubNotificationSender.cs # NEW - transport-agnostic send abstraction

Infrastructure/
  Notifications/              # NEW folder
    HubNotifier.cs            # NEW - implements INotifier using IHubNotificationSender

Agent/
  Hubs/
    ChatHub.cs                # Existing - orchestrates client calls
    HubNotificationAdapter.cs # NEW (renamed from Notifier.cs) - wraps IHubContext
```

### Pattern 1: Adapter Pattern for Cross-Layer Notifications

**What:** Separate notification business logic from SignalR transport mechanism
**When to use:** When an inner layer (Infrastructure) needs functionality from an outer layer (Agent/SignalR)

**Architecture:**
```
Domain/Contracts/INotifier.cs (interface - what notifications exist)
         |
         v
Infrastructure/Notifications/HubNotifier.cs (implementation - when/how to notify)
         |
         | depends on
         v
Domain/Contracts/IHubNotificationSender.cs (interface - transport abstraction)
         |
         | implemented by
         v
Agent/Hubs/HubNotificationAdapter.cs (adapter - wraps IHubContext<ChatHub>)
```

**Example:**
```csharp
// Domain/Contracts/IHubNotificationSender.cs
// Source: Clean Architecture Dependency Inversion pattern
namespace Domain.Contracts;

public interface IHubNotificationSender
{
    Task SendToAllAsync(string method, object arg, CancellationToken cancellationToken = default);
}

// Infrastructure/Notifications/HubNotifier.cs
namespace Infrastructure.Notifications;

public sealed class HubNotifier(IHubNotificationSender sender) : INotifier
{
    public async Task NotifyTopicChangedAsync(
        TopicChangedNotification notification,
        CancellationToken cancellationToken = default)
    {
        await sender.SendToAllAsync("OnTopicChanged", notification, cancellationToken);
    }
    // ... other methods
}

// Agent/Hubs/HubNotificationAdapter.cs
namespace Agent.Hubs;

public sealed class HubNotificationAdapter(IHubContext<ChatHub> hubContext) : IHubNotificationSender
{
    public async Task SendToAllAsync(string method, object arg, CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync(method, arg, cancellationToken);
    }
}
```

### Pattern 2: Service Collection Extension Pattern

**What:** Organize DI registrations into cohesive extension methods
**When to use:** When a module has multiple related services to register

**Example:**
```csharp
// WebChat.Client/Extensions/StateServiceCollectionExtensions.cs
// Source: .NET Service Collection Extension Pattern
namespace WebChat.Client.Extensions;

public static class StateServiceCollectionExtensions
{
    public static IServiceCollection AddWebChatStores(this IServiceCollection services)
    {
        // State infrastructure
        services.AddScoped<Dispatcher>();
        services.AddScoped<IDispatcher>(sp => sp.GetRequiredService<Dispatcher>());

        // Stores
        services.AddScoped<TopicsStore>();
        services.AddScoped<MessagesStore>();
        services.AddScoped<StreamingStore>();
        services.AddScoped<ConnectionStore>();
        services.AddScoped<ApprovalStore>();

        // Coordination
        services.AddScoped<RenderCoordinator>();

        return services;
    }

    public static IServiceCollection AddWebChatEffects(this IServiceCollection services)
    {
        services.AddScoped<ReconnectionEffect>();
        services.AddScoped<SendMessageEffect>();
        services.AddScoped<TopicSelectionEffect>();
        services.AddScoped<TopicDeleteEffect>();
        services.AddScoped<InitializationEffect>();
        services.AddScoped<AgentSelectionEffect>();

        return services;
    }
}
```

### Anti-Patterns to Avoid

- **Implementation in wrong layer:** Never put implementations that use outer-layer types (like `IHubContext`) in inner layers
- **Direct dependency on outer layers:** Infrastructure must never reference Agent namespace
- **Mixing abstractions:** Don't create interfaces that leak transport details (e.g., don't put "Hub" in Domain interface names beyond the adapter)
- **Scattered DI registrations:** Group related service registrations into extension methods

## Don't Hand-Roll

Problems that look simple but have existing solutions:

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Cross-layer notification | Direct SignalR call from Infrastructure | Adapter pattern with interface | Violates Clean Architecture dependency rule |
| Service registration organization | Inline registrations in Program.cs | Extension methods on IServiceCollection | Standard pattern, chainable, testable |
| Hub access from services | `IHubContext<ChatHub>` directly | Abstract sender interface | Keeps SignalR concern in Agent layer |

**Key insight:** The Adapter pattern is specifically designed for this scenario - when you need functionality from an outer layer but can't directly depend on it. Define the abstraction where it's needed (Domain), implement the adapter where the technology lives (Agent).

## Common Pitfalls

### Pitfall 1: Leaking SignalR Types into Domain/Infrastructure
**What goes wrong:** Importing `Microsoft.AspNetCore.SignalR` in Domain or Infrastructure projects
**Why it happens:** Seems simpler to just use the types directly
**How to avoid:** Use the Adapter pattern - define transport-agnostic interface in Domain
**Warning signs:** `using Microsoft.AspNetCore.SignalR;` in non-Agent projects

### Pitfall 2: Interface in Wrong Layer
**What goes wrong:** Moving `INotifier` from Domain to Infrastructure (as some research suggested)
**Why it happens:** The current implementation uses SignalR, so it seems like an Infrastructure concern
**How to avoid:** Keep `INotifier` in Domain because consumers (WebChatMessengerClient, WebChatApprovalManager) are in Infrastructure and need to reference it
**Warning signs:** Infrastructure project can't find the interface after moving

### Pitfall 3: Breaking Tests During Refactoring
**What goes wrong:** Tests fail because `Notifier` is renamed/moved
**Why it happens:** Tests reference the old class directly
**How to avoid:** Update `WebChatServerFixture` to register new types; the test still needs to register `INotifier` and `IHubNotificationSender`
**Warning signs:** `WebChatServerFixture.cs` has compile errors after changes

### Pitfall 4: Forgetting to Activate Effects
**What goes wrong:** Effects don't run because they're not activated at startup
**Why it happens:** Effects use constructor injection for handler registration
**How to avoid:** Call `app.Services.GetRequiredService<EffectType>()` in Program.cs after building
**Warning signs:** Effect handlers don't fire despite being registered

### Pitfall 5: Chain Pattern Not Followed
**What goes wrong:** Extension methods don't return `IServiceCollection`, breaking chaining
**Why it happens:** Forgetting to add `return services;`
**How to avoid:** Always return `services` at end of extension method
**Warning signs:** Can't chain `.AddWebChatStores().AddWebChatEffects()`

## Code Examples

Verified patterns from codebase and official sources:

### DI Registration Wire-Up (Agent Layer)

```csharp
// Agent/Modules/InjectorModule.cs - AddWebClient() method update
// Source: Existing codebase pattern
private IServiceCollection AddWebClient()
{
    return services
        // Adapter in Agent layer
        .AddSingleton<IHubNotificationSender, HubNotificationAdapter>()
        // Implementation in Infrastructure
        .AddSingleton<INotifier, HubNotifier>()
        // Rest unchanged
        .AddSingleton<WebChatSessionManager>()
        .AddSingleton<WebChatStreamManager>()
        .AddSingleton<WebChatApprovalManager>()
        .AddSingleton<WebChatMessengerClient>()
        .AddSingleton<IChatMessengerClient>(sp => sp.GetRequiredService<WebChatMessengerClient>())
        .AddSingleton<IToolApprovalHandlerFactory>(sp =>
            new WebToolApprovalHandlerFactory(
                sp.GetRequiredService<WebChatApprovalManager>(),
                sp.GetRequiredService<WebChatSessionManager>()));
}
```

### Test Fixture Update

```csharp
// Tests/Integration/Fixtures/WebChatServerFixture.cs update
// Source: Existing codebase pattern
// Replace:
//   builder.Services.AddSingleton<INotifier, Notifier>();
// With:
builder.Services.AddSingleton<IHubNotificationSender, HubNotificationAdapter>();
builder.Services.AddSingleton<INotifier, HubNotifier>();
```

### WebChat.Client Program.cs With Extension Methods

```csharp
// WebChat.Client/Program.cs after refactoring
// Source: Service Collection Extension Pattern
using WebChat.Client.Extensions;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Hub event dispatching
builder.Services.AddScoped<IHubEventDispatcher, HubEventDispatcher>();

// Connection services
builder.Services.AddScoped<ChatConnectionService>();
builder.Services.AddScoped<IChatConnectionService>(sp => sp.GetRequiredService<ChatConnectionService>());

// Core services
builder.Services.AddScoped<IChatSessionService, ChatSessionService>();
builder.Services.AddScoped<IChatMessagingService, ChatMessagingService>();
builder.Services.AddScoped<ITopicService, TopicService>();
builder.Services.AddScoped<IAgentService, AgentService>();
builder.Services.AddScoped<IApprovalService, ApprovalService>();

// State management
builder.Services.AddScoped<IChatStateManager, ChatStateManager>();
builder.Services.AddScoped<ILocalStorageService, LocalStorageService>();

// State stores and effects via extension methods
builder.Services
    .AddWebChatStores()
    .AddWebChatEffects();

// Streaming services
builder.Services.AddScoped<IStreamingCoordinator, StreamingCoordinator>();
builder.Services.AddScoped<StreamResumeService>();
builder.Services.AddScoped<IStreamResumeService>(sp => sp.GetRequiredService<StreamResumeService>());

// Notification handling
builder.Services.AddScoped<IChatNotificationHandler, ChatNotificationHandler>();
builder.Services.AddScoped<ISignalREventSubscriber, SignalREventSubscriber>();

var app = builder.Build();

// Activate effects (needs concrete instances to register handlers)
app.Services.ActivateWebChatEffects();

await app.RunAsync();
```

### Effect Activation Extension

```csharp
// WebChat.Client/Extensions/StateServiceCollectionExtensions.cs
public static void ActivateWebChatEffects(this IServiceProvider services)
{
    _ = services.GetRequiredService<ReconnectionEffect>();
    _ = services.GetRequiredService<SendMessageEffect>();
    _ = services.GetRequiredService<TopicSelectionEffect>();
    _ = services.GetRequiredService<TopicDeleteEffect>();
    _ = services.GetRequiredService<InitializationEffect>();
    _ = services.GetRequiredService<AgentSelectionEffect>();
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| All registrations in Program.cs | Extension method grouping | .NET Core 2.0+ | Better organization, modularity |
| Direct outer-layer dependencies | Adapter pattern | Always (Clean Architecture principle) | Proper layer separation |
| `Startup.cs` configuration | Minimal API in Program.cs | .NET 6+ | Simplified bootstrapping |

**Deprecated/outdated:**
- Putting all service registrations directly in Program.cs/Startup.cs is discouraged for larger applications

## Open Questions

None identified. The decisions from CONTEXT.md are clear and implementable.

## Sources

### Primary (HIGH confidence)
- [Microsoft Learn: SignalR HubContext](https://learn.microsoft.com/en-us/aspnet/core/signalr/hubcontext?view=aspnetcore-10.0) - IHubContext injection patterns
- [Microsoft Learn: Dependency Injection](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection) - DI patterns and guidelines
- [Uncle Bob: Clean Architecture](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html) - The Dependency Rule
- Existing codebase patterns in `Agent/Modules/InjectorModule.cs`, `Infrastructure/Extensions/`

### Secondary (MEDIUM confidence)
- [Service Collection Extension Pattern](https://thecodeman.net/posts/the-service-collection-extension-pattern) - Extension method organization
- [Clean Architecture in .NET](https://medium.com/@roshikanayanadhara/clean-architecture-in-net-a-practical-guide-with-examples-817568b3f42e) - Practical implementation guidance

### Tertiary (LOW confidence)
- None - all findings verified with primary sources

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - Uses only built-in .NET patterns already in codebase
- Architecture: HIGH - Adapter pattern is well-established, verified in official docs
- Pitfalls: HIGH - Based on direct codebase analysis and Clean Architecture principles

**Research date:** 2026-01-20
**Valid until:** 2026-03-20 (60 days - stable patterns, no version dependencies)
