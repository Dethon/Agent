# Toast Notifications Design

## Overview

Surface errors that are currently swallowed silently in the WebUI through a non-intrusive toast notification system.

## Requirements

- **Style**: Toast notifications (slide in from corner, auto-dismiss)
- **Types**: Errors only (no warnings/info/success)
- **Position**: Top-right corner
- **Dismiss**: Hybrid - auto-dismiss after 8 seconds, manual dismiss with X button
- **Stacking**: Vertical stack, max 3 visible (oldest removed when 4th arrives)
- **Mobile**: Compact, full-width with small margins

## Component Structure

```
WebChat.Client/
├── Components/
│   └── Toast/
│       ├── ToastContainer.razor    # Positioned top-right, renders toast stack
│       └── ToastItem.razor         # Individual toast with message + X button
├── State/
│   └── Toast/
│       ├── ToastActions.cs         # ShowError, DismissToast
│       ├── ToastState.cs           # State record with toast list
│       └── ToastStore.cs           # Manages toast lifecycle + auto-dismiss
```

## State Management

### Actions

```csharp
public record ShowError(string Message);      // Dispatched when error occurs
public record DismissToast(Guid Id);          // Manual or auto-dismiss
```

### State

```csharp
public record ToastState(ImmutableList<ToastItem> Toasts);
public record ToastItem(Guid Id, string Message, DateTime CreatedAt);
```

### Store Responsibilities

- On `ShowError`: Create toast with unique ID, add to list, enforce max 3 (remove oldest if needed), deduplicate same message
- On `DismissToast`: Remove toast by ID
- Expose `StateObservable` for component subscription

### Auto-dismiss Flow

Each `ToastItem` component starts an 8-second timer on render. When timer fires, it dispatches `DismissToast(Id)`. If user clicks X first, same action fires immediately and timer is cancelled on dispose.

## Styling

### ToastContainer

```css
.toast-container {
    position: fixed;
    top: 1rem;
    right: 1rem;
    z-index: 1000;
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    pointer-events: none;
}
```

### ToastItem

```css
.toast-item {
    pointer-events: auto;
    background: var(--error-bg, #fee2e2);
    border-left: 4px solid var(--error-color, #dc2626);
    border-radius: 4px;
    padding: 0.75rem 1rem;
    box-shadow: 0 4px 12px rgba(0,0,0,0.15);
    display: flex;
    align-items: flex-start;
    gap: 0.75rem;
    max-width: 360px;
    animation: slideIn 0.2s ease-out;
}
```

### Mobile Responsiveness

```css
@media (max-width: 640px) {
    .toast-container {
        top: 0.5rem;
        right: 0.5rem;
        left: 0.5rem;
    }

    .toast-item {
        max-width: 100%;
        padding: 0.5rem 0.75rem;
        font-size: 0.875rem;
    }
}
```

### Animations

- **Enter**: Slide in from right (translateX 100% → 0)
- **Exit**: Fade out + slide right (opacity 1→0, translateX 0→50%)

## Error Message Handling

### Message Formatting

- Truncate long messages to ~150 characters with "..."
- Strip technical details (stack traces) - show user-friendly text only
- For unknown errors: "Something went wrong. Please try again."

### Error Source Mapping

| Source | Example Message |
|--------|-----------------|
| Stream error chunk | Use server's error message directly |
| Connection lost | "Connection lost. Reconnecting..." |
| Resume failed | "Failed to resume message stream" |
| Send failed | "Failed to send message" |
| Load failed | "Failed to load messages" |

### Deduplication

If the same error message is already visible in the stack, don't add a duplicate.

### Transient Error Filtering

Filter out cancellation/transient errors that reconnection handles:

```csharp
private static bool IsTransientError(Exception ex)
{
    return ex is OperationCanceledException or TaskCanceledException;
}

private static bool IsTransientErrorMessage(string message)
{
    return string.IsNullOrWhiteSpace(message)
        || message.Contains("OperationCanceled", StringComparison.OrdinalIgnoreCase)
        || message.Contains("TaskCanceled", StringComparison.OrdinalIgnoreCase)
        || message.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase);
}
```

## Integration Points

### Files to Create

| File | Purpose |
|------|---------|
| `Components/Toast/ToastContainer.razor` | Container component, mounts in MainLayout |
| `Components/Toast/ToastItem.razor` | Individual toast with timer + dismiss |
| `State/Toast/ToastActions.cs` | ShowError, DismissToast records |
| `State/Toast/ToastState.cs` | State record with toast list |
| `State/Toast/ToastStore.cs` | Store with reducers + observable |

### Files to Modify

| File | Change |
|------|--------|
| `MainLayout.razor` | Add `<ToastContainer />` at root |
| `Program.cs` | Register ToastStore as singleton |
| `Services/Streaming/StreamingService.cs` | Dispatch ShowError on error chunks and in catch blocks (with transient filtering) |
| `State/Effects/*.cs` | Wrap async calls in try-catch, dispatch on failure |

### StreamingService Integration

```csharp
// For error chunks (around line 113):
if (chunk.Error is not null)
{
    if (!IsTransientErrorMessage(chunk.Error))
    {
        dispatcher.Dispatch(new ShowError(chunk.Error));
    }
    continue;
}

// In catch blocks (around line 193):
catch (Exception ex) when (!IsTransientError(ex))
{
    dispatcher.Dispatch(new ShowError($"Error: {ex.Message}"));
}
catch
{
    // Transient errors silently ignored - reconnection handles recovery
}
```

## Not Changing

- Existing components (MessageList, ChatInput, etc.)
- Connection status indicator (keeps its current behavior)
- Approval modal system
- Error messages no longer appear in chat (toast replaces this)
