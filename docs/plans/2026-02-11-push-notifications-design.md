# Push Notifications for Mobile (Android)

Send Web Push notifications to Android devices when the agent completes a response, so users receive alerts even when the app is not in the foreground.

## Approach

W3C Push API with VAPID keys. Works on Android Chrome/Edge/Firefox with no third-party service dependencies. Notifications arrive even when the app is closed.

## Architecture

```
Agent (Backend)
  HubNotifier
    NotifyStreamChangedAsync(Completed)
      ├── SignalR group broadcast (existing)
      └── PushNotificationService.SendAsync() (new)
           └── WebPush library → Push Service (Google FCM)

  ChatHub
    ├── SubscribePush(subscription)   (new)
    └── UnsubscribePush(endpoint)     (new)

  RedisPushSubscriptionStore          (new)
    └── Key: push:subs:{userId} → Hash<endpoint, {p256dh, auth}>

Service Worker (client device)
  push event → check if any app window is focused
    ├── focused: suppress notification
    └── not focused: show notification
  notificationclick → focus or open app window

WebChat.Client (Blazor WASM)
  PushNotificationService (new, JS interop)
    ├── Request notification permission
    ├── PushManager.subscribe(vapidPublicKey)
    └── Send subscription to hub
  UI toggle (bell icon in header)
```

## Backend

### VAPID Configuration

Store in .NET User Secrets:

- `WebPush:PublicKey` - base64url-encoded public key
- `WebPush:PrivateKey` - base64url-encoded private key
- `WebPush:Subject` - contact email (e.g. `mailto:admin@example.com`)

Expose the public key through the existing `GET /api/config` response by adding a `VapidPublicKey` field to `AppConfig`.

### Domain Layer

`PushSubscriptionDto` record in `Domain/DTOs/WebChat/`:

```csharp
public record PushSubscriptionDto(string Endpoint, string P256dh, string Auth);
```

`IPushSubscriptionStore` interface in `Domain/Contracts/`:

```csharp
public interface IPushSubscriptionStore
{
    Task SaveAsync(string userId, PushSubscriptionDto subscription, CancellationToken ct = default);
    Task RemoveAsync(string userId, string endpoint, CancellationToken ct = default);
    Task RemoveByEndpointAsync(string endpoint, CancellationToken ct = default);
    Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetAllAsync(CancellationToken ct = default);
}
```

`IPushNotificationService` interface in `Domain/Contracts/`:

```csharp
public interface IPushNotificationService
{
    Task SendToSpaceAsync(string spaceSlug, string title, string body, string url, CancellationToken ct = default);
}
```

### Infrastructure Layer

`RedisPushSubscriptionStore` — Redis hash per user:

- Key: `push:subs:{userId}`
- Field: endpoint URL
- Value: JSON `{ "p256dh": "...", "auth": "..." }`

`WebPushNotificationService`:

- Uses `WebPush` NuGet package (`WebPushClient`)
- Fetches all subscriptions, sends push to each
- On HTTP 410 Gone: removes expired subscription from store
- Catches and logs send failures without blocking the caller

### Agent Integration

**ChatHub** — two new methods:

- `SubscribePush(PushSubscriptionDto subscription)` — saves for the calling user
- `UnsubscribePush(string endpoint)` — removes the subscription

**HubNotifier** — in `NotifyStreamChangedAsync`, when change type is `Completed`, call `pushNotificationService.SendToSpaceAsync()` with title "New response" and the space URL.

**DI Registration** in `InjectorModule`:

- `IPushSubscriptionStore` → `RedisPushSubscriptionStore` (singleton)
- `IPushNotificationService` → `WebPushNotificationService` (singleton)

## Client

### Service Worker

Both `service-worker.js` (dev) and `service-worker.published.js` (prod) get push handlers:

```js
self.addEventListener('push', event => {
    const data = event.data?.json() ?? { title: 'New message', body: '' };
    event.waitUntil(
        self.clients.matchAll({ type: 'window', includeUncontrolled: true })
            .then(clients => {
                const anyVisible = clients.some(c => c.visibilityState === 'visible');
                if (!anyVisible) {
                    return self.registration.showNotification(data.title, {
                        body: data.body,
                        icon: '/icon.svg',
                        data: { url: data.url }
                    });
                }
            })
    );
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = event.notification.data?.url ?? '/';
    event.waitUntil(
        self.clients.matchAll({ type: 'window' }).then(clients => {
            const existing = clients.find(c => c.url.includes(url));
            return existing ? existing.focus() : self.clients.openWindow(url);
        })
    );
});
```

### JS Interop Module (`wwwroot/push-notifications.js`)

Functions callable from Blazor via `IJSRuntime`:

- `requestPermission()` — returns `"granted"`, `"denied"`, or `"default"`
- `subscribe(vapidPublicKey)` — subscribes via `PushManager`, returns `{ endpoint, p256dh, auth }`
- `getExistingSubscription()` — returns current subscription or null
- `unsubscribe()` — unsubscribes from push

### Blazor `PushNotificationService`

Injected service wrapping JS interop:

- On initialization (in `InitializationEffect`): checks if the browser supports push and if the user is already subscribed
- `RequestAndSubscribeAsync()` — requests permission, subscribes, sends to hub
- `UnsubscribeAsync()` — unsubscribes and notifies hub
- `IsSubscribedAsync()` — checks current subscription state

### UI

Bell icon in the header:

- Not subscribed: outline bell, click to enable
- Subscribed: filled bell, click to disable
- Permission denied: show disabled state with tooltip

## Notification Suppression

Handled entirely client-side in the service worker. The backend always sends the push message. The service worker checks `self.clients.matchAll()` and only shows the notification if no app window is currently visible. This avoids backend visibility tracking complexity.

## Edge Cases

- **Expired subscriptions**: HTTP 410 from push service triggers automatic removal from Redis
- **Multiple devices**: Each device gets its own subscription, all receive notifications
- **Multiple spaces**: Notifications scoped to the space where the response occurred
- **Permission denied**: UI shows disabled state, no errors
- **Service worker update**: Initialization logic re-checks and re-registers subscription if changed
- **No VAPID keys configured**: Push features silently disabled (public key absent from config)

## Testing

- **Unit**: `RedisPushSubscriptionStore` — save, remove, get operations
- **Unit**: `WebPushNotificationService` — correct targeting, expired subscription cleanup
- **Unit**: `HubNotifier` — push triggered on `StreamCompleted` only
- **Integration**: Playwright end-to-end — grant notification permission, send message, verify push received

## Out of Scope

- Per-topic or per-space notification preferences
- Notification batching or debouncing
- iOS Safari Web Push (different requirements)
- Rich notifications with action buttons
- Notification history or read tracking
