# User ID Simplification Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Eliminate username/userId distinction - Id becomes both identifier and display name, fixing user identification across browser sessions.

**Architecture:** Remove `Username` field from data models, pass only `SenderId` through the backend chain, have UI components look up avatar from `UserIdentityStore` by `SenderId`.

**Tech Stack:** .NET 10, Blazor WebAssembly, SignalR, Redis

---

## Task 1: Update Data Files (users.json)

**Files:**
- Modify: `Agent/wwwroot/users.json`
- Modify: `WebChat.Client/wwwroot/users.json`

**Step 1: Update Agent/wwwroot/users.json**

```json
[
  { "id": "Alice", "avatarUrl": "avatars/alice.png" },
  { "id": "Bob", "avatarUrl": "avatars/bob.png" },
  { "id": "Charlie", "avatarUrl": "avatars/charlie.png" }
]
```

**Step 2: Update WebChat.Client/wwwroot/users.json**

Same content as above.

**Step 3: Commit**

```bash
git add Agent/wwwroot/users.json WebChat.Client/wwwroot/users.json
git commit -m "chore: simplify users.json - use Id as display name"
```

---

## Task 2: Update UserConfig Records

**Files:**
- Modify: `Agent/Services/UserConfigService.cs:5`
- Modify: `WebChat.Client/Models/UserConfig.cs:3`

**Step 1: Update Agent/Services/UserConfigService.cs**

Change the record from:
```csharp
public record UserConfig(string Id, string Username, string AvatarUrl);
```

To:
```csharp
public record UserConfig(string Id, string AvatarUrl);
```

**Step 2: Update WebChat.Client/Models/UserConfig.cs**

Change the record from:
```csharp
public record UserConfig(string Id, string Username, string AvatarUrl);
```

To:
```csharp
public record UserConfig(string Id, string AvatarUrl);
```

**Step 3: Build to verify no compile errors**

Run: `dotnet build`
Expected: Build succeeds (or shows errors in dependent files we'll fix next)

**Step 4: Commit**

```bash
git add Agent/Services/UserConfigService.cs WebChat.Client/Models/UserConfig.cs
git commit -m "refactor: remove Username from UserConfig records"
```

---

## Task 3: Update Domain DTOs

**Files:**
- Modify: `Domain/DTOs/WebChat/ChatHistoryMessage.cs`
- Modify: `WebChat.Client/Models/ChatMessageModel.cs`

**Step 1: Update ChatHistoryMessage.cs**

Change from:
```csharp
public record ChatHistoryMessage(
    string Role,
    string Content,
    string? SenderId,
    string? SenderUsername,
    string? SenderAvatarUrl);
```

To:
```csharp
public record ChatHistoryMessage(
    string Role,
    string Content,
    string? SenderId);
```

**Step 2: Update ChatMessageModel.cs**

Change from:
```csharp
public record ChatMessageModel
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = "";
    public string? Reasoning { get; init; }
    public string? ToolCalls { get; init; }
    public bool IsError { get; init; }

    public string? SenderId { get; init; }
    public string? SenderUsername { get; init; }
    public string? SenderAvatarUrl { get; init; }

    public bool HasContent =>
        !string.IsNullOrEmpty(Content) ||
        !string.IsNullOrEmpty(ToolCalls) ||
        !string.IsNullOrEmpty(Reasoning);
}
```

To:
```csharp
public record ChatMessageModel
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = "";
    public string? Reasoning { get; init; }
    public string? ToolCalls { get; init; }
    public bool IsError { get; init; }

    public string? SenderId { get; init; }

    public bool HasContent =>
        !string.IsNullOrEmpty(Content) ||
        !string.IsNullOrEmpty(ToolCalls) ||
        !string.IsNullOrEmpty(Reasoning);
}
```

**Step 3: Build to identify remaining errors**

Run: `dotnet build`
Expected: Build errors in ChatHub, effects, and UI components

**Step 4: Commit**

```bash
git add Domain/DTOs/WebChat/ChatHistoryMessage.cs WebChat.Client/Models/ChatMessageModel.cs
git commit -m "refactor: remove SenderUsername/SenderAvatarUrl from DTOs"
```

---

## Task 4: Update ChatHub (Backend)

**Files:**
- Modify: `Agent/Hubs/ChatHub.cs`

**Step 1: Remove Username from Context.Items and simplify RegisterUser**

In `RegisterUser` method, remove the Username line:
```csharp
public Task RegisterUser(string userId)
{
    var user = userConfigService.GetUserById(userId);
    if (user is null)
    {
        throw new HubException($"Invalid user ID: {userId}");
    }

    Context.Items["UserId"] = userId;
    return Task.CompletedTask;
}
```

**Step 2: Replace GetRegisteredUsername with GetRegisteredUserId**

Remove the `GetRegisteredUsername` method entirely.

Add new method:
```csharp
private string? GetRegisteredUserId()
{
    return Context.Items.TryGetValue("UserId", out var userId)
        ? userId as string
        : null;
}
```

**Step 3: Update SendMessage to use userId**

Change:
```csharp
var username = GetRegisteredUsername() ?? "Anonymous";
var responses = messengerClient.EnqueuePromptAndGetResponses(topicId, message, username, cancellationToken);
```

To:
```csharp
var userId = GetRegisteredUserId() ?? "Anonymous";
var responses = messengerClient.EnqueuePromptAndGetResponses(topicId, message, userId, cancellationToken);
```

**Step 4: Simplify GetHistory mapping**

Change the Select mapping from:
```csharp
.Select(m => new ChatHistoryMessage(
    m.Role.Value,
    string.Join("", m.Contents.OfType<TextContent>().Select(c => c.Text)),
    m.AdditionalProperties?.GetValueOrDefault("SenderId") as string,
    m.AdditionalProperties?.GetValueOrDefault("SenderUsername") as string,
    m.AdditionalProperties?.GetValueOrDefault("SenderAvatarUrl") as string))
```

To:
```csharp
.Select(m => new ChatHistoryMessage(
    m.Role.Value,
    string.Join("", m.Contents.OfType<TextContent>().Select(c => c.Text)),
    m.AdditionalProperties?.GetValueOrDefault("SenderId") as string))
```

**Step 5: Commit**

```bash
git add Agent/Hubs/ChatHub.cs
git commit -m "refactor: ChatHub uses userId instead of username"
```

---

## Task 5: Update ChatMonitor (Domain)

**Files:**
- Modify: `Domain/Monitor/ChatMonitor.cs:72-79`

**Step 1: Remove SenderUsername from AdditionalProperties**

Change:
```csharp
var userMessage = new ChatMessage(ChatRole.User, x.Prompt)
{
    AdditionalProperties = new AdditionalPropertiesDictionary
    {
        ["SenderId"] = x.Sender,
        ["SenderUsername"] = x.Sender
    }
};
```

To:
```csharp
var userMessage = new ChatMessage(ChatRole.User, x.Prompt)
{
    AdditionalProperties = new AdditionalPropertiesDictionary
    {
        ["SenderId"] = x.Sender
    }
};
```

**Step 2: Commit**

```bash
git add Domain/Monitor/ChatMonitor.cs
git commit -m "refactor: ChatMonitor stores only SenderId"
```

---

## Task 6: Update Client Effects

**Files:**
- Modify: `WebChat.Client/State/Effects/SendMessageEffect.cs`
- Modify: `WebChat.Client/State/Effects/TopicSelectionEffect.cs`
- Modify: `WebChat.Client/State/Effects/InitializationEffect.cs`

**Step 1: Update SendMessageEffect.cs**

Change the AddMessage dispatch (around line 103-110):
```csharp
_dispatcher.Dispatch(new AddMessage(topic.TopicId, new ChatMessageModel
{
    Role = "user",
    Content = action.Message,
    SenderId = currentUser?.Id
}));
```

**Step 2: Update TopicSelectionEffect.cs**

Change the history mapping (around line 64-71):
```csharp
var messages = history.Select(h => new ChatMessageModel
{
    Role = h.Role,
    Content = h.Content,
    SenderId = h.SenderId
}).ToList();
```

**Step 3: Update InitializationEffect.cs**

Change the history mapping (around line 101-108):
```csharp
var messages = history.Select(h => new ChatMessageModel
{
    Role = h.Role,
    Content = h.Content,
    SenderId = h.SenderId
}).ToList();
```

**Step 4: Build to verify**

Run: `dotnet build`
Expected: Build succeeds for effects, may have UI errors

**Step 5: Commit**

```bash
git add WebChat.Client/State/Effects/SendMessageEffect.cs WebChat.Client/State/Effects/TopicSelectionEffect.cs WebChat.Client/State/Effects/InitializationEffect.cs
git commit -m "refactor: client effects use only SenderId"
```

---

## Task 7: Update UI Components

**Files:**
- Modify: `WebChat.Client/Components/ChatMessage.razor`
- Modify: `WebChat.Client/Helpers/AvatarHelper.cs`

**Step 1: Update AvatarHelper.cs method names**

Rename methods from `Username` to `UserId` for clarity:
```csharp
namespace WebChat.Client.Helpers;

public static class AvatarHelper
{
    private static readonly string[] Colors =
    [
        "#FF6B6B",
        "#4ECDC4",
        "#45B7D1",
        "#FFA07A",
        "#98D8C8",
        "#F7DC6F",
        "#BB8FCE",
        "#85C1E2"
    ];

    public static string GetColorForUser(string? userId)
    {
        if (string.IsNullOrEmpty(userId))
            return Colors[0];

        var hash = 0;
        foreach (var c in userId)
        {
            hash = (hash * 31 + c) & 0x7FFFFFFF;
        }

        return Colors[hash % Colors.Length];
    }

    public static string GetInitials(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return "?";

        var words = userId.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
            return "?";

        if (words.Length == 1)
            return char.ToUpper(words[0][0]).ToString();

        return $"{char.ToUpper(words[0][0])}{char.ToUpper(words[1][0])}";
    }
}
```

**Step 2: Update ChatMessage.razor to inject UserIdentityStore and look up user**

Add injection and lookup logic:
```razor
@using Markdig
@using WebChat.Client.State.UserIdentity
@inject UserIdentityStore UserIdentityStore

@code {
    [Parameter] public ChatMessageModel Message { get; set; } = new();
    [Parameter] public bool IsStreaming { get; set; }
    [Parameter] public bool ShowAvatar { get; set; }
    [Parameter] public bool IsOwnMessage { get; set; }

    private bool _showReasoning;
    private bool _showToolCalls;

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static string RenderMarkdown(string? content)
    {
        return string.IsNullOrEmpty(content) ? "" : Markdown.ToHtml(content, MarkdownPipeline);
    }

    private (string? AvatarUrl, string DisplayName) GetSenderInfo()
    {
        if (string.IsNullOrEmpty(Message.SenderId))
            return (null, "Anonymous");

        var user = UserIdentityStore.State.AvailableUsers
            .FirstOrDefault(u => u.Id == Message.SenderId);

        return (user?.AvatarUrl, Message.SenderId);
    }

    private string GetMessageClass()
    {
        var classes = new List<string> { "chat-message", Message.Role };
        if (Message.IsError)
        {
            classes.Add("error");
        }
        if (IsStreaming && !string.IsNullOrEmpty(Message.Content))
        {
            classes.Add("streaming-cursor");
        }
        if (IsStreaming && string.IsNullOrEmpty(Message.Content) &&
            string.IsNullOrEmpty(Message.Reasoning) && string.IsNullOrEmpty(Message.ToolCalls))
        {
            classes.Add("thinking-only");
        }
        if (IsOwnMessage)
        {
            classes.Add("own");
        }

        return string.Join(" ", classes);
    }

    private string GetMessageWrapperClass()
    {
        var classes = new List<string> { "message-wrapper" };
        if (Message.Role == "assistant")
        {
            classes.Add("agent");
        }
        else
        {
            classes.Add("user");
        }
        return string.Join(" ", classes);
    }

    private string? GetTooltipText()
    {
        return Message.Role == "user" ? Message.SenderId : null;
    }
}
```

**Step 3: Update the avatar rendering in ChatMessage.razor markup**

Change the avatar section:
```razor
<div class="@GetMessageWrapperClass()">
    @if (Message.Role != "assistant")
    {
        var (avatarUrl, displayName) = GetSenderInfo();
        <div class="message-avatar-column">
            @if (ShowAvatar)
            {
                <AvatarImage UserId="@displayName"
                             AvatarUrl="@avatarUrl"
                             Size="28" />
            }
            else
            {
                <div class="avatar-placeholder-space"></div>
            }
        </div>
    }
    ... rest of markup unchanged ...
</div>
```

**Step 4: Commit**

```bash
git add WebChat.Client/Components/ChatMessage.razor WebChat.Client/Helpers/AvatarHelper.cs
git commit -m "refactor: ChatMessage looks up avatar by SenderId"
```

---

## Task 8: Update AvatarImage Component

**Files:**
- Modify: `WebChat.Client/Components/AvatarImage.razor`

**Step 1: Rename Username parameter to UserId**

```razor
@using WebChat.Client.Helpers

<div class="avatar-image-container" style="width: @(Size)px; height: @(Size)px;">
    @if (!string.IsNullOrEmpty(AvatarUrl) && !_imageLoadFailed)
    {
        <img src="@AvatarUrl"
             alt="@UserId"
             class="avatar-image"
             style="width: @(Size)px; height: @(Size)px; border-radius: 50%;"
             @onerror="HandleImageError" />
    }
    else
    {
        <div class="avatar-fallback"
             style="width: @(Size)px; height: @(Size)px; background-color: @_backgroundColor; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-weight: bold; color: white; font-size: @(Size / 2)px;">
            @_initials
        </div>
    }
</div>

@code {
    [Parameter]
    public string? UserId { get; set; }

    [Parameter]
    public string? AvatarUrl { get; set; }

    [Parameter]
    public int Size { get; set; } = 32;

    private bool _imageLoadFailed;
    private string _backgroundColor = "#FF6B6B";
    private string _initials = "?";

    protected override void OnParametersSet()
    {
        _backgroundColor = AvatarHelper.GetColorForUser(UserId);
        _initials = AvatarHelper.GetInitials(UserId);
    }

    private void HandleImageError()
    {
        _imageLoadFailed = true;
        StateHasChanged();
    }
}
```

**Step 2: Build full solution**

Run: `dotnet build`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add WebChat.Client/Components/AvatarImage.razor
git commit -m "refactor: AvatarImage uses UserId parameter"
```

---

## Task 9: Run Tests and Fix Any Failures

**Step 1: Run all tests**

Run: `dotnet test`
Expected: All tests pass (no tests directly reference removed fields)

**Step 2: If any test failures, fix them**

**Step 3: Commit any test fixes**

```bash
git add -A
git commit -m "test: fix tests for user ID simplification"
```

---

## Task 10: Final Verification

**Step 1: Run the application**

Run: `dotnet run --project Agent`

**Step 2: Manual test checklist**

- [ ] Open WebChat in browser, select a user
- [ ] Send a message - avatar shows correctly
- [ ] Refresh browser - message still shows correct avatar
- [ ] Open second browser tab - messages show correct avatars
- [ ] Send message from second tab - first tab shows it with correct avatar

**Step 3: Commit any final fixes**

```bash
git add -A
git commit -m "fix: final adjustments for user ID simplification"
```
