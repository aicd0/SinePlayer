# Player sessions — one playback, several pages

Status: plan. Nothing here is implemented yet.

## 1. What is being changed

Today a `PlayerPage` owns its `CoreEngine`: the page creates it, uses it, and disposes it in `OnStop`. Two player
pages therefore mean two engines, two pipelines, two decoders and two clocks — two players, not one player shown
twice. This plan introduces a `PlayerSession` between the page and the engine:

- **One `PlayerSession` owns exactly one `CoreEngine`**, and any number of `PlayerPage`s can attach to it.
- Each attached page gets a **view**: its own swap-chain panel and its own video chain, so it can show a
  **different video track of the same media**, at the same position, with the picture it chose.
- Audio and subtitle stay session-wide in v1: one device, one clock, one set of words.
- A session is persisted under `/Resources/PlayerSessions/{sessionId}`; what a particular page chose (its video
  track) is persisted under `/Resources/Tabs/{tabId}/PlayerSession`.

The route of a player tab stops naming a media and starts naming a session.

This is a new application, so nothing is migrated: the old route parameters, the runtime play-queue key and the
per-tab queue snapshot are deleted rather than kept alongside the new ones, and no record written by an older build
is read.

## 2. Why this is now cheap

The pipeline work of v1/v2 already paid for most of it, and the plan leans on specific parts of it:

- **A pipeline holds N chains of a kind.** `AddVideoChain(target)` per view, `RemoveChain(chain)` when a page
  closes, `Reconcile()` as the single place that starts, stops and joins them (`PlaybackPipelineChains.md`,
  phases A–I).
- **Two chains may read one stream.** If both windows pick the same video track they share the demuxed packets by
  reference count, not by a copy (`PlaybackPipelineChainsV2.md`, J5/J6) — so "same track in both windows" is a
  supported case rather than a duplicate.
- **Removal, seek, end and read-ahead are per chain, not per first-of-kind** (J4, J7). Closing one window removes
  one chain and the others carry on; the pipeline ends when every chain has reached its end.

What is *not* there is the app layer: `CoreEngine` still assumes one video chain, `PlayerPage` assumes it owns the
engine, and the route assumes the media is what a tab remembers.

## 3. What is true today (facts the steps rely on)

1. **`CoreEngine` holds one video output.** `_videoPanel`, `_videoRenderTarget`, `_videoChain` are single fields;
   `CreateVideoSurface()` returns the one cached panel; `PlaybackComponents.Apply` registers video components on
   `pipeline.VideoChains[0]`; `PlaybackInfoHandle` reports one video chain.
2. **`PlayerPage` owns the engine.** `EnsureEngine()` in `OnStart`/`OpenMedia`, `DisposeEngine()` in `OnStop`.
3. **The route carries the media, not a session.** `OpenMediaHelper.GetMediaRoute` writes a play queue into
   `/Resources/Runtime/PlayQueues` and builds `app://player?play_queue_id=…&playback=…&position=…`;
   `PlayerPage.GetPlayQueue` reads it back, and the page rewrites its own URL with a fresh serialized `playback`
   blob on every playback change.
4. **The play queue is persisted, but as a per-tab snapshot.** `AppDB.Initialize` wipes `/Resources/Runtime`
   (`RemoveKey(RUNTIME_RESOURCES)`), which is where `GetMediaRoute` puts the queue under `PLAY_QUEUES`.
   `PlayerPage.GetPlayQueue` reads that first and then falls back to `/Resources/Tabs/{tabId}/PlayQueues`, which
   survives a launch — and it *writes* that tab copy at open time. So a restored tab does get its queue back
   today, but the copy is a snapshot of the moment the tab opened: queue edits made during the session are never
   written back to it. The playback state travels in the route's `playback` blob, rewritten on every change; the
   position comes from `media.LastPositionMs` (media-wide, gated by `RestoreLastPlaybackPosition`), not from the
   route.
5. **Tab ids survive a restart** (they are part of the saved window state), and `/Resources/Tabs/{tabId}` is
   already a per-tab registry area: `MainPage` copies it when a tab moves to another window, and
   `WindowManager.CleanUpTabResources` deletes it for tabs that no longer exist.
6. **`PlayerPage` is not deduplicated by identity** — `PlayerPageTrait.AllowMultiplePages` is `true`, so the same
   URL opened twice gives two tabs. Two tabs in one window, or one tab in each of two windows, both work.
7. **All windows run on one UI thread** (`MainWindow.Open` is called from UI code and constructs on that thread),
   so views, panels and dispatcher callbacks need no extra marshalling between windows.
8. **Tracks are remembered per media**, not per page: `TrackSelection` stores
   `SelectedTrackModel {Enabled, Path, Index}` under `MediaExt.SELECTED_VIDEO_TRACK` (and audio/subtitle
   equivalents) on the `MediaModel`.
9. **There are no tests for the app project.** `src/Tests` covers `SinePlayer.Core` only. Every step below is
   verified by a clean build plus a named manual check.

## 4. Decisions

**Decision 1 — the session record owns the queue and the playback state.** It is written when they change and
again at last detach. The runtime `PLAY_QUEUES` key, the per-tab `PlayQueues` snapshot and the URL rewriting on
every playback change are deleted (fact 4). Reason: the per-tab copy is taken when the tab opens, which is enough
while one tab is one player — but two pages sharing a session would keep two snapshots of one queue, both stale the
moment the queue is edited, and the route's `playback` blob duplicates what the session already knows. One session
must have one record of what it is playing.

**Decision 2 — the position is recorded per session, and per media for a fresh open.** `media.LastPositionMs`
keeps its meaning (where this file was last left, gated by `RestoreLastPlaybackPosition`), because that is what
opening the file fresh resumes from; the session record additionally holds the position of that session, which is
what two sessions of one file need in order not to overwrite each other's place.

**Decision 3 — a page's video choice is written to two places.** The tab record
(`/Resources/Tabs/{tabId}/PlayerSession`) is what that page restores from; `MediaExt.SELECTED_VIDEO_TRACK` keeps
its current meaning as *the default for the next open of that media*. Reason: without the second write, opening the
same file later would no longer remember the last chosen picture, which is existing behaviour a user would notice.
Confirmed.

**Decision 4 — audio and subtitle stay session-wide in v1.** One audio chain, one subtitle chain, one subtitle
target. A selection in any window's menu changes it for the session. Out of scope by request; recorded here so the
menus are not mistaken for per-view later.

**Decision 5 — creating a route does not write persisted state.** `GetMediaRoute` is called while *building a
context menu*, whether or not the user ever opens the item, so it may not create a session record. It writes a
**draft** — queue, playback state, position — to `/Resources/Runtime/PlayerSessionDrafts/{sessionId}`, which is
wiped at launch like the rest of the runtime area, and returns `app://player?session={id}`. The page is what
materialises the record from the draft, and deletes the draft once it has. So the route has exactly one shape from
the first commit: a session id, never a queue blob and never a second parameter set that only exists before the
first open.

**Decision 6 — a session lives while at least one page is attached.** Last detach saves the record and disposes
the engine. The record itself stays until the next launch's cleanup, which is the only thing that can know whether
a tab will still ask for it.

**Decision 7 — the second window lands before the persistence rework.** The visible feature ("New window", two
video tracks of one media) is reachable while sessions are still keyed by the play queue id, so the registry
changes are made against something that can be seen working, and a persistence bug can be told apart from a
multi-view bug.

## 5. Phases

Invariant, as in the previous plans: one coherent change per step, a build that is clean (0 errors, 0 warnings) at
the end of every step, and the manual check named for the step performed before the next one starts.

### Phase A — the video output becomes a view

| # | Step | Change | Review focus | Verify |
|---|---|---|---|---|
| A1 | One view, extracted — **done** | `PlayerView` holds the panel, the render target, the video chain, its `VideoTransform` and its `PlaybackInfoHandle`. `CoreEngine` keeps exactly one, created where the panel is created today; `CreateVideoSurface`, `SetVideoTransform`, `PlaybackInfo` and `SelectStream(Video, …)` forward to it. No new public members | nothing but movement: the page, the menus and the read surfaces must not notice. `PlaybackInfoHandle` is already per-chain, so the view's handle is an `Attach(pipeline, viewChain, audio, subtitle)` | build + play a file: picture, surface resize, stretch/rotation change, video track switch |

### Phase B — the session and its views

| # | Step | Change | Review focus | Verify |
|---|---|---|---|---|
| B1 | `PlayerSession` and its manager | `PlayerSession` owns the `CoreEngine`, the `PlayQueueModel` and the `PlaybackModel`, plus the attached views. `PlayerSessionManager` maps a session id to a live session and counts attached pages. The page holds a session instead of an engine; `EnsureEngine`/`DisposeEngine` become `AcquireSession`/`ReleaseSession`. The route is unchanged, so for now the session id is the play queue id the route already carries | the page must not keep a second copy of what the session owns — no `_engine`, no `ViewModel.Playback` of its own. Transport, position, queue navigation and the sidebar's `PlaybackChanged` event now come from the session | build + play a file, next/previous, switch tracks, close the tab: exactly as before |
| B2 | Views become plural | `CoreEngine.CreateView()` / `RemoveView(view)`; `EnsurePipeline` adds one video chain per view; `SelectStream(view, kind, stream)`, `ResultOf(view, kind, result)`, `Apply` to every video chain, `Duration` from the view. `PlayerSession.AttachView()` / `DetachView()`; the page attaches in `OnStart`, detaches in `OnStop`; video menu items and video read surfaces go through the page's own view, audio/subtitle through the session | the pipeline already supports N chains, so this is app-side plumbing: what a page reads about the *picture* is its own chain, what it changes about the *sound* is the session's. Detaching the last view disposes the session | build + one page: parity with B1. The plural path is exercised by C1, one step later |

### Phase C — the second window (all in memory)

| # | Step | Change | Review focus | Verify |
|---|---|---|---|---|
| C1 | A second page attaches instead of opening | `PlayerMenuCreator.CreateVideoHostMenuItems` gains a `NewWindow` item (`StringResourceProvider.NewWindow` already exists in all nine locales) that opens the current tab's URL in a new window (`MainWindow.Open(url, oldTabId: "")`). A page whose route names a session that is already live **attaches**: no `Reset`, no re-adding sources, no audio/subtitle selection, and no video selected. `OpenMedia` becomes the open path only for a session that has none yet | the item must not copy the source tab's record (`oldTabId` empty), or the new page would inherit the track it is supposed to pick for itself. Attaching must not disturb the playing session — no epoch change, so no hiccup in the other window. The page's own load path must be skipped, not just its engine call: `ViewModel` reacts to a playback state change by opening the media, so an attaching page must not hand the shared `PlaybackModel` a queue to load | build + open a file, "New window": a second player window on the same media, blank until a track is picked, sound still once, position shared. The same URL in a second **tab** of one window behaves identically (pages are not deduplicated, fact 6) |
| C2 | Two views, two pictures | The new page picks a video track from its menu; the choice goes to its own view only (and to the media-level preference, decision 3). Both windows may pick different tracks, and both may pick the same one | a video selection is now `view.SelectStream`, not `engine.SelectStream(Video, …)`; audio/subtitle stay on the session (decision 4). Selecting a track rebuilds one chain — the other window must keep drawing | build + two windows: A on track 1, B on track 2, each shows its own; set B to track 1: both show track 1 (packet fan-out), sound unaffected; close B: A keeps playing |

### Phase D — persistence

| # | Step | Change | Review focus | Verify |
|---|---|---|---|---|
| D1 | The session record, and a route that names it | `PlayerSessionRecord` — queue, playback state, position — persisted at `/Resources/PlayerSessions/{sessionId}`; written when the first view attaches, when the queue or the playback state changes, and at last detach. `RouterConstants.ARG_SESSION`; `GetMediaRoute` writes a draft to `/Resources/Runtime/PlayerSessionDrafts/{sessionId}` and returns `app://player?session={id}` — one shape, always. `PlayerPage.GetPlayQueue` becomes "the record, else the draft (which it then deletes), else an empty queue". `ARG_PLAY_QUEUE_ID`, `ARG_PLAYBACK`, `ARG_POSITION`, the runtime `PLAY_QUEUES` key, the per-tab `PlayQueues` write and the per-change URL rewriting are all deleted | the record is the only persisted state of a session, and one record per session. Route creation stays free of persisted writes and the URL never changes after it is built — which is what lets the identity of a tab be its session rather than a blob that changes under it. A record or draft that fails to parse leaves the session empty rather than failing the app | build + open a file: the tab URL is `app://player?session=…`; edit the queue (add items), inspect `/Resources/PlayerSessions` for one record holding the queue as edited and the position, and no draft left behind; restart with window restore on: the tab comes back with the **edited** queue, the track and the position — where today it would come back with the queue as it was when the tab opened |
| D2 | The tab record | `/Resources/Tabs/{tabId}/PlayerSession` holds `SessionId` and the page's video `SelectedTrackModel`. Written when the choice changes and when the page stops; read in `OnStart` before the view selects anything. `WindowManager.CleanUpTabResources` also prunes `/Resources/PlayerSessions` entries no restored tab names | per-tab state must never overwrite another tab's; the media-level preference is still written (decision 3). A tab whose record names a session that no longer exists opens with an empty session rather than failing | build + two windows on one session with different tracks, restart: each window restores its own track and both restore the same position; a third launch after closing one window prunes nothing it still needs |

### Phase E — edges

| # | Step | Change | Review focus | Verify |
|---|---|---|---|---|
| E1 | Closing, moving and sharing | Last view out saves and disposes; closing one window leaves the other playing; a tab dragged into another window carries its tab record (the existing `CopyTree` path — confirm it carries `/PlayerSession`); two tabs on one session inside one window behave the same as two windows | nothing may depend on attach order beyond the existing "first audio chain keeps the clock" rule. A hidden tab keeps decoding (known cost) but must not steal the clock | build + close the second window while the first plays: no hiccup, no second sound; drag the tab out and back; two tabs of one session in one window, switched back and forth |

## 6. Known costs and open items

- **Selecting a track re-anchors the shared playback.** `ReconcileCore` advances the epoch whenever any chain
  reports a change, and the epoch is pipeline-wide: the reader seeks and every chain flushes. In C2 the visible
  effect is a brief hiccup in the window that did not change. Accepted for v1; the fix (advance the epoch only
  when the *reader's* bindings change, not when a chain rebuilds its components) is a pipeline change deserving
  its own plan and tests.
- **A hidden tab keeps decoding.** Two tabs in one window both hold live chains; only the selected tab's panel is
  composed. Acceptable for v1 — pausing a view when its panel is not visible needs a visibility signal the page
  does not have today.
- **Stale session records and drafts.** Records are pruned at launch from the tab URLs that will be restored (D2);
  drafts left by a context menu that was never opened live in the runtime area and go with the rest of it. Growth is
  bounded by tabs a user actually opened.
- **`PlaybackLabPage` is untouched.** It drives the pipeline directly and is not part of this plan.

## 7. Definition of done for v1

1. One `CoreEngine` serves every page attached to its session; a page owns no engine and no playback state of its
   own.
2. "New window" opens a second page on the same session, selecting no video track, and the track it chooses is
   independent of the other page's.
3. Both pages show the same position and transport state; sound plays once.
4. The session survives a restart: queue, playback state, position, and each page's video track come back.
5. Closing a page leaves the others playing; closing the last one saves and disposes.
6. Single-page behaviour is unchanged when no second page exists.
