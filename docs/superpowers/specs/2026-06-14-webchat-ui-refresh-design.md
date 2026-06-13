# WebChat UI Refresh — "Ember Study" + "The Hearth" Navigation

**Date:** 2026-06-14
**Branch:** `ui-refresh`
**Status:** Design approved — ready for implementation planning
**Note:** §-claims about the current code were verified against the repository in an adversarial review; corrections from that review are folded in throughout.

## 1. Problem & Goals

The WebChat (Blazor WebAssembly SPA) has two problems:

1. **The look is generic and boring.** It loads `Inter` (plus `JetBrains Mono`) from the **Google Fonts CDN** (`index.html` preconnects + stylesheet link; `app.css` sets `font-family: 'Inter'`) — a runtime CDN dependency — paired with an indigo→violet "purple gradient" accent and a cold blue-slate dark theme. The textbook "AI slop" aesthetic with no personality.
2. **Thread navigation is bad, especially on mobile.** Below 768px the topic sidebar collapses into a cramped **horizontal-scrolling strip** pinned above the chat (`@media (max-width: 768px)` reflows `.topic-list` to `flex-direction: row; overflow-x: auto`). It wastes vertical chat space and hides conversations behind a sideways swipe with poor discoverability.

This is a **bold reinvention** (user's explicit choice): we may rethink layout, navigation, header, and visual language. All existing features are kept; only their presentation changes.

### Success criteria

- The app has a distinctive, cohesive visual identity that feels intentionally designed — not template-generic.
- Mobile navigation reclaims the chat's vertical space and makes 6–20 conversations per agent easy to scan and switch between, in the thumb zone.
- Desktop uses its width well (not a stretched phone).
- Light **and** dark themes, both warm.
- No feature regressions: agent switching (2–4 agents), topic list with recency/unread/streaming/delete, new chat, search, connection status, theme toggle, identity picker, approval modal, suggestion chips, empty state.

### Usage profile (drives the design)

- **A few agents** (2–4) the user switches between — agent switch is a one-tap *secondary* axis, not the dominant one.
- **Moderate topics** (6–20 active per agent) — a flat list starts to strain; recency order, unread cues, quick scan, and light search matter.

## 2. Non-Goals

- No backend/protocol changes. No new **persisted** fields (in particular, **no** new `StoredTopic` preview field — see §5.4 — so the dual chat-history readers are untouched).
- No change to the message pipeline, streaming protocol, or approval flow logic (visual restyle only).
- Not fixing the documented pre-existing interleaved-`messageId` streaming bug — but the new shared selectors must **not regress** it (§7).

**Bounded exception (approved):** to support cross-agent activity dots (§5.3), one new client-side effect/selector and a small in-memory state slice may load **lightweight topic metadata** (topic id/name, `LastMessageAt`, streaming/recency flags — **not** full message history) for all 2–4 agents in the space at startup. This is purely client-side state; it touches no backend, protocol, or persisted schema.

## 3. Locked Decisions

| Area | Decision |
|------|----------|
| **Aesthetic** | "Ember Study" — warm paper (light) & warm espresso (dark); Fraunces (display/names/headings), Hanken Grotesk (body/chat), JetBrains Mono (metadata/labels); ember-orange `#e9601f` as the single constant action/"live" color. Calm, editorial, unhurried. |
| **Navigation** | "The Hearth" — mobile: draggable bottom sheet with peek/half/full detents + a thin bottom bar (agent chip + new-chat). Desktop: the **same DOM** un-docks via a `@media (min-width: 768px)` breakpoint into a pinned ~320px left rail. |
| **Themes** | Both light and dark, both warm. Dark is espresso, not cold slate. |
| **Per-space accent** | **Subtle tint.** Ember stays the constant action color (`--accent` = fixed ember). The per-space accent flows into a separate `--space-accent` used only for the agent active-ring, small fills, and the favicon — never the action color. |
| **Cross-agent dots** | Load lightweight per-agent topic metadata at startup so a background agent's chip can light up (see §2 bounded exception). |
| **Scope** | Whole-app reskin + Hearth navigation. All features kept. |
| **Drag feel** | **Buttery flick-drag from day one** — the JS gesture shim is in v1 (user's explicit choice), not deferred. |
| **Fonts** | Self-hosted `woff2` in `wwwroot/fonts` (PWA-safe, no CDN dependency). Requires the service-worker precache fix in §6.7. |

## 4. Visual System ("Ember Study")

### 4.1 Color tokens

Replace the entire `:root` / `[data-theme="dark"]` token block in `wwwroot/css/app.css`. Indicative palette (hex values refined during implementation):

**Light (warm paper):** canvas `#f3ede1`, panel/elevated `#fffaf1`, rail `#efe7d7`, ink `#2a2118`, muted `#a08a68`, border `#e6dbc6`, ember `#e9601f`, ember-subtle `#fbe7d6`, ember-border `#f0c9a8`, success `#5aa06e`.

**Dark (warm espresso):** canvas `#171310`, panel `#241e17`, rail `#1d1812`, header `#211b15`, ink `#efe4d2`, muted `#a4937a`, border `#3a2f24`, ember `#f0712f` (slightly brighter for contrast), ember-subtle `#33231a`, ember-border `#6a3d20`.

**Accent chain (two separate variables — do not conflate):**
- `--accent` = the **fixed ember** token. It is the action color (buttons, focus rings, send button, the user-message fill). Today `app.css:19` sets `--accent: #6366f1` (light) / `#818cf8` (dark) — both replaced by ember.
- `--space-accent` = the **per-space tint**, fed from `SpaceState.AccentColor`. Used only for the agent active-ring, small fills, and the favicon. Never replaces `--accent`.
- **Warm default:** the current `SpaceConfig.DefaultAccentColor` is `#e94560` (pink-red) — not warm. Set a warm Ember-Study default (or default `--space-accent` to ember when a space has no override) so the out-of-box tint harmonizes.

### 4.2 Typography

- **Fraunces** (variable serif, optical sizing) — agent names, headings, conversation titles, the brand mark.
- **Hanken Grotesk** — body & chat message text.
- **JetBrains Mono** — timestamps, labels, status, code.
- Self-host as `woff2` under `wwwroot/fonts/` with `@font-face`; remove the `Inter` references (the `<link>` and preconnects in `index.html` **and** the `font-family` in `app.css`).

### 4.3 Motion

- Staggered page-load reveal; gentle message entrance; the **ember "thinking" glow** on a streaming conversation (peek bar + its rail row).
- **All** motion gated behind a global `prefers-reduced-motion` guard. The codebase currently has **none** — this is net-new and required.

### 4.4 Reskin surface (no logic change)

`MainLayout` header, `ChatMessage`/`MessageList`, `StreamingMessageDisplay`, `ChatInput`, code blocks, `ApprovalModal`, `Toast/*`, `EmptyState`, `SuggestionChips`, `ConnectionStatus`, `UserIdentityPicker`, scrollbars.

## 5. The Hearth Navigation

### 5.1 Mobile (< 768px)

Persistent chrome is minimal:

- **Peek bar (~56px)** anchored at the bottom: current conversation name + mono timestamp; ember glow when that conversation is streaming. The message list is padded so the peek bar never occludes content.
- **Bottom bar (thin):** active agent monogram chip (left) → tap opens the agent switcher (2–4 agents, one-tap switch, each chip carrying a per-agent activity dot); ember **＋** (right) → new chat.

**Drag detents:** drag the sheet up through **peek → half (~6 recent rows) → full (with live search)**. Tap a row to select the conversation and snap back to peek. The handle shows an aggregate-unread badge + glow when any thread streams.

**Rows:** name · time · unread pill · streaming dots. **Delete** is an overflow/confirm action (`ShowDeleteConfirm` → `ConfirmDelete`); swipe-to-delete is **out of v1** so the horizontal gesture never competes with the vertical drag (§6.5).

**Viewport/keyboard:** the shell and sheet are sized in `dvh` (with a `vh` fallback), and at the full detent a `visualViewport`-driven offset keeps the search field above the soft keyboard (§6.5).

### 5.2 Desktop (≥ 768px)

A `@media (min-width: 768px)` breakpoint un-docks the **same sheet DOM** into a pinned **~320px left rail**, frozen open:

- Agent switcher as a **segmented strip** at the top (zero-click visible identity).
- Search field below.
- **Two-line rows** with a last-message preview (derived client-side, §5.4).
- **＋ New chat** in the footer.

Identical markup/bindings → glow, pills, search, delete behave the same in both postures. One component, two postures, no parallel build. (A plain media query — not a container query — avoids the `container-type` silent-no-op trap; the breakpoint is viewport-based anyway.)

### 5.3 Shared upgrades

- **⌘K / Ctrl-K** quick-switch into search (guarded so it never fires while the chat input is focused).
- **Per-agent activity dots** on agent chips — fed by the lightweight all-agents topic metadata (§2 bounded exception). A `groupBy(AgentId)` over that metadata + live streaming events lets a *background* agent's chip light up. This is a **coarse activity signal** (is-streaming / has-recent-activity), not an exact unread count for background agents (§5.4).
- **Ember thinking-glow** on the open/streaming conversation.

### 5.4 Previews, recency & unread semantics

- **Recency ordering is unchanged:** the persisted `StoredTopic.LastMessageAt ?? CreatedAt` (`TopicList.razor:195`). Only the **preview text** is client-derived, so ordering and preview cannot drift.
- **Active-agent previews/unread are exact:** message history is eagerly loaded into `MessagesStore` for the **selected** agent's topics (`InitializationEffect`, `AgentSelectionEffect`), so the desktop two-line preview is a client-side selector over loaded messages — no `StoredTopic` field, no backend change.
- **Background agents are coarse:** their full history is not loaded, so their rows show no exact preview/unread count — only the coarse activity dot from §5.3. (Exact background previews would require loading all history at startup; explicitly out of scope.)

## 6. Architecture & Build

### 6.1 Components

- `Components/TopicList.razor` → rewritten as The Hearth (peek bar + bottom bar + draggable sheet; desktop rail via media query). Reuses all existing handlers: `HandleTopicClick→SelectTopic`, `HandleNewTopic→CreateNewTopic`, `SelectAgent`, `ShowDeleteConfirm`/`ConfirmDelete→RemoveTopic` (`CancelDelete` clears local state only), `IsTopicStreaming`, `ComputeUnreadCounts`, recency `OrderByDescending(t => t.LastMessageAt ?? t.CreatedAt)`. New **local** fields only: detent state (peek/half/full), search query, aggregate-unread. Keep the `#tooltip` div (used by `app.js` `data-tooltip`).
- `Components/Chat/ChatContainer.razor` → the sheet overlays the chat on mobile (fixed/absolute); on desktop it's a grid/flex rail sibling. Add peek-bar padding to the message list. The `<ApprovalModal>` here uses native `<dialog>.showModal()`, which renders in the **top layer** above the sheet at any detent — an approval prompt is never hidden behind a fully-expanded sheet.
- `Layout/MainLayout.razor` → restyled header (logo/brand, connection status, identity picker, theme toggle); registers the ⌘K keydown interop (follows the existing `visibilityHelper.register` `DotNetObjectReference` pattern). Preserve the existing `icon.svg?color=` logo coupling (see §6.3).
- **`Components/AgentSelector.razor`** → this currently-**unused** reusable dropdown becomes the new **AgentSwitcher** (native `<dialog>`/popover on mobile, segmented strip on desktop). The live switcher logic to port lives inline in `TopicList.razor`'s `custom-dropdown` block (`ToggleDropdown`/`SelectAgent`/`CloseDropdown`); consolidate into `AgentSelector.razor` rather than spawning a third component.
- **New** sheet-gesture JS module + registration in `wwwroot/app.js`.

### 6.2 State

- **Reuses** `SelectTopic`, `SelectAgent`, `CreateNewTopic`, `RemoveTopic`, `StreamingStore.StreamingTopics`, `ComputeUnreadCounts`, recency ordering, `SpaceState.AccentColor`.
- **Refactor:** lift the cross-store unread/streaming fold (today inlined in `TopicList`/`ComputeUnreadCounts`) into a **shared selector** (e.g. `State/Topics/UnreadSelectors.cs`) so the rail rows, the aggregate badge, and the per-agent activity dots share one source of truth.
- **New (bounded, §2 exception):** a small client-side slice + effect that loads lightweight topic metadata for all space agents at startup, plus a `groupBy(AgentId)` selector feeding the per-agent activity dots. No new persisted/backend state.
- Search & detent state are local component state.

### 6.3 Prerequisites (Step 0 — land before the rest)

1. **Wire `SpaceState.AccentColor` into `--space-accent`** (a root CSS custom property), keeping the existing favicon (`faviconHelper.setColor`) and header-logo (`MainLayout.razor:11` `icon.svg?color=`) couplings. Today `AccentColor` drives only those two; it is **not** yet in the CSS custom-property chain. Do **not** route it into `--accent` (that is ember; §4.1).
2. **Agent monograms.** `AgentCatalogEntry` has only `Id`/`Name`/`Description` (no avatar/color). Reuse `Helpers/AvatarHelper.cs`'s deterministic logic — `GetInitials(string?)` and `GetColorForUser(string?)` (currently user-keyed; pass the agent id/name as the seed) — but **swap its cold `_colors` palette** (`#FF6B6B`, `#4ECDC4`, …) for a warm Ember-Study tint ramp, or derive the chip tint from `--space-accent` at low opacity, so monograms match the aesthetic.

### 6.4 CSS

- Delete the `.topic-sidebar` / `.topic-list` / `.topic-item` block **and** the horizontal-strip reflow rules in the single `@media (max-width: 768px)` block (`.topic-list { flex-direction: row; overflow-x: auto }`, `.topic-item { min-width/max-width/flex-shrink }`, `.chat-layout { flex-direction: column }`).
- Add: sheet detent transforms driven by a `--sheet-offset` custom property the JS writes; the ember-glow keyframe; peek-bar chrome padding; the `@media (min-width: 768px)` desktop ~320px rail; `dvh` shell/sheet sizing; reduced-motion fallbacks.
- **Snap-mechanism ownership:** detent snapping is owned by the JS/transform path (translate + settle). CSS `scroll-snap` is scoped **only** to the inner scrollable row list, never the sheet container — the two must not contend on the same axis.
- Adopt (first-of-kind here, all low-risk/pure-CSS): native `<dialog>`/popover, `:has()`, scroll-snap.

### 6.5 Gesture interop (buttery flick — v1)

The live drag runs **entirely in JS**: `pointermove` writes `--sheet-offset` via `requestAnimationFrame` with **no Blazor re-render mid-gesture**; on release, a single `DotNetObjectReference` callback commits the settled detent to .NET. This matches existing `app.js` patterns (`chatScroll` sticky-scroll, `chatInput` autoResize). Do **not** route `pointermove` through `@onpointermove`.

**Axis disambiguation contract:**
- A sheet drag is captured only past a first-move angle/Δy threshold (the single tunable, §10); below it, taps/clicks pass through.
- If the inner row list is scrolled away from its top, the sheet stays at the full detent and the gesture scrolls the list (no drag handoff mid-scroll).
- Swipe-to-delete is **out of v1** — delete stays the overflow/confirm path — so there is no horizontal-vs-vertical contention.

**Viewport/keyboard:** `dvh` units for shell + sheet; at the full detent, a `visualViewport`-driven offset lifts the search field above the soft keyboard (mirror existing `app.js` helper shape).

### 6.6 Accessibility

- Native `<dialog>.showModal()` / popover for the **agent switcher** and **delete confirm** → free focus-trap, `inert` background, Esc. (No `<dialog>`/focus-trap precedent in the codebase — net-new.)
- The **draggable sheet is not a `<dialog>`** (it's a peek-able, drag-resizable overlay). Its focus handling is hand-rolled and detent-keyed: no focus trap at peek/half; at full, move focus to the search field and restore focus on collapse. `aria-expanded` + roles on the sheet.
- Agent rail/segmented strip as a proper `tablist`/`radiogroup` with roving tabindex.
- ⌘K guarded against firing while the chat input is focused.
- `prefers-reduced-motion` disables the ember-glow, drag transitions, and entrance animations.

### 6.7 Fonts & PWA

- Self-hosted `woff2` under `wwwroot/fonts/` with `@font-face`. Enumerate exact faces to keep payload small: **Fraunces** variable (wght + opsz, Latin subset), **Hanken Grotesk** 400/600, **JetBrains Mono** 400/500. All three are OFL-licensed. Preload only the **critical** faces (body + brand-mark) in `index.html` for first paint.
- **Service-worker fix (required, else the "no-CDN PWA" goal silently fails):** `wwwroot/service-worker.published.js` `offlineAssetsInclude` uses `/\.woff$/`, which does **not** match `.woff2`. Change to `/\.woff2?$/` so the self-hosted faces are precached offline. Add this file to the Step-1 (Foundations) triplet.

## 7. Risks

| Risk | Mitigation |
|------|------------|
| **Gesture ↔ Blazor interop** (now in v1, highest risk) | JS-only live drag via CSS var + rAF; single `DotNetObjectReference` callback on release. Mirror existing `app.js` helpers. Built/tested in sub-steps (§9.6a–c). |
| **Axis ownership: drag vs inner-list scroll** | Angle/Δy capture threshold; no drag handoff once the inner list is scrolled off its top (§6.5). |
| **Snap mechanism contention** | JS/transform owns detents; `scroll-snap` only on the inner row list, never the sheet container (§6.4). |
| **iOS soft-keyboard / dynamic viewport** | `dvh` sizing + `visualViewport` offset for the full-detent search field (§6.5); not deferred to tuning. |
| **PWA fonts not cached** | Fix the service-worker `woff2` regex in Foundations (§6.7). |
| **Net-new accessibility** (no dialog/focus-trap precedent) | Native `<dialog>` for switcher/delete; hand-rolled detent-keyed focus for the sheet, with a keyboard E2E case (§9.7). |
| **Cross-agent metadata cost** | Only 2–4 agents; load lightweight topic lists (not history); coarse activity dot only (§5.3/§5.4). |
| **Shared selector touches fragile streaming bookkeeping** (interleaved-`messageId` bug) | Own tests for the shared unread/streaming + activity selectors; no change to streaming reducers; verify no regression. |
| **E2E flakiness** (known shared-Redis stream-resume) | Scope new E2E assertions narrowly; don't assert across sibling-test state. |

## 8. Testing (TDD — Red → Green → Refactor)

- **bUnit / unit:** shared unread/streaming selector; per-agent activity fold; monogram determinism + warm palette; detent state transitions; agent-switch dispatch; search filtering; new-chat & delete dispatch.
- **Gesture (per sub-step):** rAF `--sheet-offset` write with no Blazor render (9.6a); axis-lock threshold + inner-scroll-no-handoff (9.6b); flick velocity → detent + release-commit (9.6c).
- **E2E (Playwright):** mobile sheet drag detents (peek/half/full); **rail visible at ≥768px**; agent switch popover/segmented strip; ⌘K quick-switch; keyboard path through the sheet/switcher. Scope around the known stream-resume E2E flakiness.
- Honor project conventions: no trailing newline in `.cs` files; pre-commit `dotnet format` re-stages whole files.

## 9. Build Sequence (commit per triplet)

0. **Prerequisites** — wire `AccentColor` → `--space-accent` (+ keep favicon/logo couplings); agent-monogram helper with warm palette.
1. **Foundations** — self-host fonts + `@font-face`; **service-worker `woff2` regex fix**; Ember Study token layer (light + dark, `--accent` = ember); `dvh` shell sizing; global reduced-motion scaffold.
2. **Static reskin** — header, bubbles, input, code, approval modal, toasts, empty state, suggestion chips, connection status, identity picker, scrollbars.
3. **State plumbing** — shared unread/streaming selector; cross-agent lightweight-metadata slice/effect + per-agent activity fold.
4. **Desktop rail** — The Hearth un-docked posture via `@media (min-width: 768px)` (no gesture), reusing all bindings; AgentSwitcher (segmented strip) from `AgentSelector.razor`.
5. **Mobile sheet (snap)** — peek/half/full detents via tap/snap + agent popover + new chat + delete; `dvh` + `visualViewport` keyboard handling. **Go/no-go checkpoint:** confirm the snap UX is acceptable as a shippable fallback before investing in the gesture shim.
6. **Gesture interop (buttery flick)** — split:
   - **6a** pointer down/move/up plumbing writing `--sheet-offset` via rAF, no Blazor render.
   - **6b** two-axis lock (angle/Δy threshold; no drag start when the inner list is scrolled off its top).
   - **6c** flick velocity → detent selection + `DotNetObjectReference` release-commit.
7. **⌘K + a11y/polish** — quick-switch; per-agent dots; native `<dialog>` switcher/delete + hand-rolled detent-keyed sheet focus; reduced-motion verification.

## 10. Open Questions

None blocking. Tuned during implementation against the real rendered UI: palette hex values, exact detent heights/breakpoints, and the gesture capture angle/Δy threshold (§6.5).
