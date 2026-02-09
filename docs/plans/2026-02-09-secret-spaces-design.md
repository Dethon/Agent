# Secret Spaces — Design Document

## Overview

Spaces are named, URL-routed partitions of topics within the WebChat UI. Each space uses the same agents and users but has its own isolated topic list. The main view at `/` is the default space. Secret spaces live at `/{slug}` and are accessible only to those who know the URL.

## Routing

| URL | Space |
|-----|-------|
| `/` | Default space (slug: `"default"`) |
| `/{slug}` | Named space matching configured slug |
| `/{unknown}` | Redirects to `/` |

A single Blazor page component with an optional `{slug?}` route parameter handles all routes. When the slug changes, the app dispatches a `SelectSpace` action that clears current state and reloads topics for that space.

## Configuration

Spaces are defined server-side in app settings:

```json
{
  "Spaces": [
    { "Slug": "default", "Name": "Main", "AccentColor": "#e94560" },
    { "Slug": "my-secret-room", "Name": "Secret Room", "AccentColor": "#6366f1" }
  ]
}
```

- `Slug`: URL path segment and topic partition key
- `Name`: Internal label (not exposed to client)
- `AccentColor`: Hex color applied to the header logo for visual distinction

The default space is always present. Unknown slugs are silently rejected (empty results, no error).

## Data Model

Add `SpaceSlug` property to `StoredTopic`:

```csharp
public string SpaceSlug { get; set; } = "default";
```

Existing topics default to `"default"` — no data migration needed.

## Hub API Changes

### Modified methods

| Method | Change |
|--------|--------|
| `GetAllTopics(agentId, spaceSlug)` | Filter topics by space slug |
| `SaveTopic(topic, isNew)` | Topic carries `SpaceSlug`; hub validates slug before persisting |
| `DeleteTopic(topicId, agentId, spaceSlug)` | Scoped to space |

### Space validation

All topic-related hub methods validate the `spaceSlug` against the configured spaces:
- Valid slug: proceed normally
- Invalid slug: return empty results (indistinguishable from empty valid space)

No endpoint lists available spaces. You either know the slug or you don't.

### Connection space tracking

The hub tracks which space each SignalR connection belongs to (set when the client calls `GetAllTopics` or a dedicated `JoinSpace` method). Notifications (`TopicChangedNotification`, `StreamChangedNotification`, etc.) are only sent to connections in the matching space.

## Client State

### New `SpaceStore`

```
State: { CurrentSlug: string, AccentColor: string }
Actions: SelectSpace(slug), SpaceValidated(slug, accentColor), InvalidSpace
```

### `SpaceEffect`

- On `SelectSpace`: sends slug to hub (via `GetAllTopics` or `JoinSpace`)
- Hub response includes accent color for the space
- If slug is valid: dispatches `SpaceValidated`, triggers topic reload
- If slug is invalid (empty/error response): redirects to `/`

### Initialization flow

1. App starts, route provides `slug` parameter (empty = `"default"`)
2. `SpaceEffect` sends slug to hub for validation
3. Valid: load topics for space, apply accent color
4. Invalid: redirect to `/`

### Space change behavior

When navigating between spaces:
- Clear current topics and messages from stores
- Reload topics for the new space
- Selected agent persists (user preference, not space-specific)
- Selected user persists

## UI

### Logo accent color

The cat logo (`ᓚᘏᗢ`) in the header is inlined as SVG. Its `fill` attribute is bound to the `SpaceStore`'s `AccentColor`. Default: `#e94560` (current color).

This is the only visual indicator of which space is active. No space name, no switcher, no additional UI elements.

### Everything else

The UI is identical across spaces: same layout, same components, same behavior. Only the URL and logo color differ.

## Security Model

- **URL-is-the-secret**: knowing the slug grants access (like an unlisted link)
- **No enumeration**: no endpoint reveals which spaces exist
- **Silent rejection**: invalid slugs return empty results, not errors
- **Server-side notification filtering**: connections only receive notifications for their current space
- **No cross-space access**: topics in one space cannot be seen or modified from another

## What Changes

| Layer | Change |
|-------|--------|
| Config | Add `Spaces` section to app settings |
| Domain | Add `SpaceSlug` to `StoredTopic` |
| Hub | Add space slug param to topic methods, track connection's space, filter notifications |
| Blazor routing | Add `/{slug?}` route parameter to main page |
| Client state | New `SpaceStore` + `SpaceEffect` |
| Topic store/effects | Pass space slug in all topic operations |
| Layout | Inline cat logo SVG, bind fill to accent color |

## What Doesn't Change

- User identity system (same users across all spaces)
- Agent system (same agents everywhere)
- Message handling, streaming, tool approval
- UI appearance (identical look apart from logo color)

## Out of Scope

- Space management UI
- Per-space user restrictions
- Per-space agent configuration
- Space listing or discovery
