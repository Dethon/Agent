# Phase 8: User Identity - Research

**Researched:** 2026-01-21
**Domain:** Blazor WebAssembly client-side state persistence and UI components
**Confidence:** HIGH

## Summary

This phase implements a user identity picker in Blazor WebAssembly with localStorage persistence. The standard approach uses direct JavaScript interop for localStorage access (already implemented in the codebase), JSON configuration files loaded via HttpClient, and a custom dropdown component following existing AgentSelector patterns. The codebase already has LocalStorageService and related infrastructure, reducing implementation risk.

**Key findings:**
- Codebase has custom LocalStorageService via IJSRuntime (not Blazored.LocalStorage)
- HttpClient in Blazor WASM can fetch JSON from wwwroot with GetFromJsonAsync
- Existing AgentSelector provides proven dropdown pattern with backdrop click-to-close
- Store<T> pattern with BehaviorSubject enables reactive state management
- System.Text.Json handles record type serialization natively

**Primary recommendation:** Extend existing patterns - create UserIdentityStore following the Topics/Messages/Streaming store structure, load user config via HttpClient from wwwroot/users.json, and build UserIdentityPicker component based on AgentSelector's dropdown pattern with circular avatar styling.

## Standard Stack

The established libraries/tools for this domain:

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| IJSRuntime | .NET 10 | Browser API access (localStorage) | Built-in Blazor WASM capability |
| System.Text.Json | .NET 10 | JSON serialization/deserialization | Default .NET serializer, handles record types |
| HttpClient | .NET 10 | Fetch static JSON files from wwwroot | Pre-configured in Blazor WASM with BaseAddress |
| System.Reactive | (current) | BehaviorSubject for Store pattern | Already used in Store<T> implementation |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Blazored.LocalStorage | 4.5.0 | Third-party localStorage wrapper | NOT NEEDED - codebase has custom implementation |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Custom LocalStorageService | Blazored.LocalStorage | More features (sync API, auto-serialization) but adds dependency; codebase already has working solution |
| JSON config file | Hardcoded array | Config file more maintainable for adding users, matches CONTEXT.md decision |
| BehaviorSubject Store | Simple field + StateHasChanged | Store pattern maintains consistency with existing state management |

**Installation:**
No new packages required - all capabilities already in .NET 10 and existing codebase.

## Architecture Patterns

### Recommended Project Structure
```
WebChat.Client/
├── State/
│   └── UserIdentity/
│       ├── UserIdentityState.cs      # Record type with selected user
│       ├── UserIdentityActions.cs    # SetUser, LoadUser actions
│       ├── UserIdentityReducers.cs   # Pure functions for state transitions
│       └── UserIdentityStore.cs      # Store<UserIdentityState>
├── Components/
│   └── UserIdentityPicker.razor      # Circular avatar dropdown
├── Models/
│   └── UserConfig.cs                 # Config file schema (record)
└── wwwroot/
    ├── users.json                    # User definitions
    └── avatars/                      # Avatar images
        ├── user1.jpg
        └── user2.jpg
```

### Pattern 1: Store-Based State Management
**What:** Redux-like pattern with Store<T>, Actions, and Reducers
**When to use:** All application state that needs reactivity
**Example:**
```csharp
// Source: Existing codebase WebChat.Client/State/Topics/TopicsStore.cs
public sealed class UserIdentityStore(
    UserIdentityState initialState,
    ILocalStorageService localStorage) : Store<UserIdentityState>(initialState)
{
    private const string StorageKey = "user-identity";

    public async Task LoadUserAsync()
    {
        var json = await localStorage.GetAsync(StorageKey);
        if (!string.IsNullOrEmpty(json))
        {
            var userId = JsonSerializer.Deserialize<string>(json);
            Dispatch(new SetUserAction(userId), UserIdentityReducers.SetUser);
        }
    }

    public async Task SaveUserAsync(string userId)
    {
        var json = JsonSerializer.Serialize(userId);
        await localStorage.SetAsync(StorageKey, json);
        Dispatch(new SetUserAction(userId), UserIdentityReducers.SetUser);
    }
}
```

### Pattern 2: HttpClient JSON Fetching
**What:** Load configuration from wwwroot using pre-configured HttpClient
**When to use:** Static config files, initialization data
**Example:**
```csharp
// Source: Microsoft Learn - Blazor configuration
// https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/configuration
[Inject] public HttpClient Http { get; set; } = default!;

protected override async Task OnInitializedAsync()
{
    var users = await Http.GetFromJsonAsync<List<UserConfig>>("users.json");
}
```

### Pattern 3: Dropdown with Backdrop Click-to-Close
**What:** Invisible fullscreen backdrop closes dropdown on outside click
**When to use:** Dropdown menus, modals, popovers
**Example:**
```csharp
// Source: Existing codebase WebChat.Client/Components/AgentSelector.razor
@if (_dropdownOpen)
{
    <div class="dropdown-backdrop" @onclick="CloseDropdown"></div>
    <div class="dropdown-menu">
        @* menu items *@
    </div>
}

// CSS for backdrop
.dropdown-backdrop {
    position: fixed;
    inset: 0;  // Covers entire viewport
    z-index: 999;  // Below dropdown-menu (z-index: 1000)
}
```

### Pattern 4: StoreSubscriberComponent
**What:** Base component that handles Store subscriptions and automatic re-rendering
**When to use:** Components that display state from Store instances
**Example:**
```csharp
// Source: Existing codebase WebChat.Client/State/StoreSubscriberComponent.cs
public partial class UserIdentityPicker : StoreSubscriberComponent
{
    [Inject] public UserIdentityStore Store { get; set; } = default!;

    private string? _selectedUserId;

    protected override void OnInitialized()
    {
        Subscribe(
            Store.StateObservable,
            state => state.SelectedUserId,
            userId => _selectedUserId = userId
        );
    }
}
```

### Anti-Patterns to Avoid
- **Direct localStorage.setItem in components:** Use service abstraction (ILocalStorageService) for testability and consistency
- **Mutating Store.State directly:** Always use Dispatch with Reducers (immutable updates)
- **OnInitialized for localStorage in Blazor Server:** Would fail during prerendering (not applicable here - WebAssembly only)
- **Forgetting to extend StoreSubscriberComponent:** Manual subscription management leads to memory leaks

## Don't Hand-Roll

Problems that look simple but have existing solutions:

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| localStorage abstraction | Custom IJSRuntime calls everywhere | ILocalStorageService (already in codebase) | Centralized error handling, testability, consistent API |
| State subscriptions | Manual Subscribe + IDisposable | StoreSubscriberComponent base class | Automatic cleanup, prevents memory leaks, handles re-rendering |
| JSON deserialization | String parsing + manual object creation | System.Text.Json.JsonSerializer | Handles nullability, record types, nested objects, error cases |
| Click-outside-to-close | Custom JavaScript event handlers | Backdrop div pattern (already in codebase) | Works with Blazor's rendering model, no JS interop needed |
| Circular avatar images | Complex CSS transforms | border-radius: 50% + aspect-ratio: 1/1 + object-fit: cover | Modern CSS handles it natively, responsive, maintains proportions |

**Key insight:** Blazor WASM already provides robust solutions for common patterns. The codebase's existing Store/Service/Component architecture should be extended, not replaced.

## Common Pitfalls

### Pitfall 1: localStorage Access Before JS Runtime Ready
**What goes wrong:** Calling localStorage during OnInitialized in Blazor Server causes null reference exceptions
**Why it happens:** JS runtime not available during server-side prerendering phase
**How to avoid:** In Blazor WASM (this project), this is NOT an issue - JS runtime is always available. Safe to use OnInitialized/OnInitializedAsync.
**Warning signs:** "System.InvalidOperationException: JavaScript interop calls cannot be issued at this time" (Blazor Server only)

### Pitfall 2: Forgetting JSON Serialization for Objects
**What goes wrong:** Storing object.ToString() instead of JSON, leading to "[Object object]" in localStorage
**Why it happens:** localStorage.setItem only accepts strings; must explicitly serialize
**How to avoid:** Always use JsonSerializer.Serialize before SetAsync, JsonSerializer.Deserialize after GetAsync
**Warning signs:** Retrieved value is string representation of object type rather than actual data

### Pitfall 3: Store Subscription Memory Leaks
**What goes wrong:** Components subscribe to Store but never unsubscribe, causing memory leaks
**Why it happens:** Forgetting to dispose subscriptions when component unmounts
**How to avoid:** Always extend StoreSubscriberComponent for components that subscribe to stores
**Warning signs:** Increasing memory usage over time, components receiving updates after disposal

### Pitfall 4: Not Using DistinctUntilChanged
**What goes wrong:** Component re-renders on every state change, even when selected value unchanged
**Why it happens:** BehaviorSubject emits on every OnNext, regardless of value equality
**How to avoid:** StoreSubscriberComponent.Subscribe already includes DistinctUntilChanged - use it
**Warning signs:** Excessive re-renders visible in browser dev tools, performance degradation

### Pitfall 5: Dropdown Z-Index Conflicts
**What goes wrong:** Dropdown menu appears behind other elements or backdrop appears above menu
**Why it happens:** Incorrect z-index layering between backdrop and menu
**How to avoid:** Use z-index hierarchy: backdrop (999) < dropdown-menu (1000), both above normal content
**Warning signs:** Clicking menu items doesn't work (backdrop is on top), or dropdown hidden by other elements

### Pitfall 6: Non-Square Images in Circular Avatars
**What goes wrong:** border-radius: 50% creates oval shape instead of circle
**Why it happens:** Container dimensions not 1:1 aspect ratio
**How to avoid:** Use aspect-ratio: 1/1 with object-fit: cover to ensure square container, centered crop
**Warning signs:** Avatar appears stretched or oval-shaped with different viewport sizes

### Pitfall 7: Config File 404 Errors Not Handled
**What goes wrong:** Application breaks if users.json missing or malformed
**Why it happens:** HttpClient.GetFromJsonAsync throws on HTTP errors or JSON parse errors
**How to avoid:** Wrap in try-catch, provide fallback empty array or default user
**Warning signs:** White screen of death, console shows 404 or JSON deserialization errors

## Code Examples

Verified patterns from official sources and existing codebase:

### Loading JSON Config File
```csharp
// Source: Microsoft Learn - How do I read a JSON file in Blazor WebAssembly?
// https://www.syncfusion.com/faq/blazor/web-api/how-do-i-read-a-json-file-in-blazor-webassembly
[Inject] public HttpClient Http { get; set; } = default!;

private List<UserConfig> _users = [];

protected override async Task OnInitializedAsync()
{
    try
    {
        var users = await Http.GetFromJsonAsync<List<UserConfig>>("users.json");
        _users = users ?? [];
    }
    catch (HttpRequestException)
    {
        // users.json not found or network error
        _users = [];
    }
    catch (JsonException)
    {
        // Invalid JSON format
        _users = [];
    }
}
```

### Circular Avatar CSS
```css
/* Source: CSS Circle Avatar Best Practices
 * https://www.w3schools.com/howto/howto_css_image_avatar.asp
 * https://css3shapes.com/a-deep-dive-into-aspect-ratio-for-responsive-shapes/
 */
.avatar-button {
    width: 40px;
    height: 40px;
    border-radius: 50%;
    overflow: hidden;
    border: 2px solid #ddd;
    cursor: pointer;
    padding: 0;
}

.avatar-image {
    width: 100%;
    height: 100%;
    aspect-ratio: 1 / 1;
    object-fit: cover;
    object-position: center;
}

/* Question mark icon when no user selected */
.avatar-placeholder {
    width: 100%;
    height: 100%;
    display: flex;
    align-items: center;
    justify-content: center;
    background-color: #e0e0e0;
    color: #666;
    font-size: 20px;
}
```

### Dropdown Menu with User List
```razor
<!-- Source: Existing codebase WebChat.Client/Components/AgentSelector.razor -->
<div class="user-picker">
    <button class="avatar-button" @onclick="ToggleDropdown">
        @if (SelectedUser is not null)
        {
            <img src="@SelectedUser.AvatarUrl" alt="@SelectedUser.Username" class="avatar-image" />
        }
        else
        {
            <div class="avatar-placeholder">?</div>
        }
    </button>

    @if (_dropdownOpen)
    {
        <div class="dropdown-backdrop" @onclick="CloseDropdown"></div>
        <div class="dropdown-menu">
            @foreach (var user in _users)
            {
                <div class="dropdown-item @(user.Id == SelectedUser?.Id ? "selected" : "")"
                     @onclick="() => SelectUser(user)">
                    <img src="@user.AvatarUrl" alt="@user.Username" class="user-avatar-small" />
                    <span>@user.Username</span>
                </div>
            }
        </div>
    }
}
```

### UserIdentityStore Pattern
```csharp
// Source: Existing codebase pattern from TopicsStore, MessagesStore
public sealed class UserIdentityStore(
    UserIdentityState initialState,
    ILocalStorageService localStorage) : Store<UserIdentityState>(initialState)
{
    private const string StorageKey = "user-identity";

    public async Task LoadFromStorageAsync()
    {
        var json = await localStorage.GetAsync(StorageKey);
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            var userId = JsonSerializer.Deserialize<string>(json);
            if (!string.IsNullOrEmpty(userId))
            {
                Dispatch(new SetUserAction(userId), UserIdentityReducers.SetUser);
            }
        }
        catch (JsonException)
        {
            // Invalid JSON in localStorage, ignore
        }
    }

    public async Task SetUserAsync(string userId)
    {
        var json = JsonSerializer.Serialize(userId);
        await localStorage.SetAsync(StorageKey, json);
        Dispatch(new SetUserAction(userId), UserIdentityReducers.SetUser);
    }
}
```

### Record Types for Config
```csharp
// Source: .NET Modern C# Patterns (.claude/rules/dotnet-style.md)
namespace WebChat.Client.Models;

public record UserConfig(
    string Id,
    string Username,
    string AvatarUrl
);

// State record
namespace WebChat.Client.State.UserIdentity;

public record UserIdentityState(string? SelectedUserId);
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Blazored.LocalStorage v3 | Blazored v4 or custom IJSRuntime | 2023+ | Default JsonSerializerOptions, breaking change for string values |
| Manual subscription disposal | StoreSubscriberComponent base class | v1.0 (codebase) | Automatic cleanup, prevents memory leaks |
| Hardcoded dropdown styles | CSS custom properties | 2024+ | Themeable, maintainable styling |
| object-fit polyfills | Native CSS object-fit/aspect-ratio | 2020+ | All modern browsers support, no polyfills needed |

**Deprecated/outdated:**
- **CSS hacks for circular images**: Modern `aspect-ratio` and `object-fit` replace old padding-bottom % tricks
- **jQuery dropdown plugins**: Blazor's component model handles state/events natively
- **sessionStorage for cross-session data**: Use localStorage for persistent user identity
- **JavaScript for click-outside detection**: Backdrop div pattern works without JS interop

## Open Questions

Things that couldn't be fully resolved:

1. **Avatar image optimization/format**
   - What we know: wwwroot serves static files, any format works (jpg, png, svg, webp)
   - What's unclear: Optimal format/size for performance vs quality
   - Recommendation: Start with jpg/png at 80x80px, optimize later if needed

2. **User config change detection**
   - What we know: HttpClient fetches on component init, no automatic reload
   - What's unclear: Should app detect users.json changes without refresh?
   - Recommendation: Initial implementation requires page refresh to reload config; consider SignalR push updates in future phase if needed

3. **Default user selection**
   - What we know: localStorage may be empty on first visit
   - What's unclear: Should app auto-select first user or require explicit choice?
   - Recommendation: Show question mark (no selection) on first visit, require explicit user selection (matches CONTEXT.md "picker shown when no username set")

## Sources

### Primary (HIGH confidence)
- Existing codebase patterns:
  - `WebChat.Client/State/Store.cs` - Store pattern implementation
  - `WebChat.Client/State/StoreSubscriberComponent.cs` - Subscription management
  - `WebChat.Client/Components/AgentSelector.razor` - Dropdown pattern with backdrop
  - `WebChat.Client/Services/LocalStorageService.cs` - localStorage abstraction
  - `.claude/rules/dotnet-style.md` - Codebase C# patterns
- [Microsoft Learn - ASP.NET Core Blazor configuration](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/configuration?view=aspnetcore-9.0) - Official config loading docs
- [Microsoft Learn - Blazor component lifecycle](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/lifecycle?view=aspnetcore-10.0) - Official lifecycle documentation
- [Microsoft Learn - Blazor state management](https://learn.microsoft.com/en-us/aspnet/core/blazor/state-management/protected-browser-storage?view=aspnetcore-10.0) - Official storage guidance

### Secondary (MEDIUM confidence)
- [GitHub - Blazored/LocalStorage](https://github.com/Blazored/LocalStorage) - Community standard library (not used in codebase, but patterns verified)
- [Syncfusion - How do I read a JSON file in Blazor WebAssembly?](https://www.syncfusion.com/faq/blazor/web-api/how-do-i-read-a-json-file-in-blazor-webassembly) - HttpClient.GetFromJsonAsync pattern
- [Blazor School - Component Lifecycle](https://blazorschool.com/tutorial/blazor-wasm/dotnet7/component-lifecycle-923500) - Lifecycle method usage
- [Jon Hilton - Persisting user preferences with Blazor and Local Storage](https://jonhilton.net/blazor-tailwind-dark-mode-local-storage/) - localStorage persistence pattern
- [W3Schools - CSS Avatar Images](https://www.w3schools.com/howto/howto_css_image_avatar.asp) - Circular avatar CSS
- [CSS3 Shapes - aspect-ratio guide](https://css3shapes.com/a-deep-dive-into-aspect-ratio-for-responsive-shapes/) - Modern CSS for avatars

### Tertiary (LOW confidence)
- WebSearch results for Blazor dropdown best practices (multiple sources agree on backdrop pattern)
- WebSearch results for localStorage pitfalls (cross-verified with Microsoft docs)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - All capabilities exist in codebase or .NET 10
- Architecture: HIGH - Verified against existing codebase patterns
- Pitfalls: HIGH - Combination of official docs + existing codebase patterns + WebSearch cross-verification
- Code examples: HIGH - Sourced from existing codebase and official documentation

**Research date:** 2026-01-21
**Valid until:** 2026-02-20 (30 days - stable domain, .NET 10 won't change significantly)
