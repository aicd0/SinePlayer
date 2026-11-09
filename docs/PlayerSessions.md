# Player sessions — one playback, several pages

Status: plan. Nothing here is implemented yet.

## 1. What is being changed

Today a `PlayerPage` owns its engine (formerly `CoreEngine`, 573 lines of it): the page creates it, uses it, and
disposes it in `OnStop`. Two player pages therefore mean two engines, two pipelines, two decoders and two clocks —
two players, not one player shown twice. This plan turns that engine into the thing a page attaches to:

- **One `PlayerSession` per playback**, and any number of `PlayerPage`s can attach to it. The session *is* the
  engine — one class, holding the pipeline, the queue, the cursor and the surfaces (decision 8).
- Each attached page gets a **view**: its own swap-chain panel and its own video chain, so it can show a
  **different video track of the same media**, at the same position, with the picture it chose.
- Audio and subtitle stay session-wide in v1: one device, one clock, one set of words.
- **Beyond v1 (phases F and G) the same model carries more streams**: several audio streams held by the session
  (a chain each, added and removed through the session and never bound to a view), and several subtitle streams on
  a view (a chain and a surface each, because words are drawn into a window).
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

What is *not* there is the app layer: the engine still assumes one video chain, `PlayerPage` assumes it owns the
engine, and the route assumes the media is what a tab remembers.

## 3. What is true today (facts the steps rely on)

1. **The engine holds one video output.** `_videoPanel`, `_videoRenderTarget`, `_videoChain` are single fields;
   `CreateVideoSurface()` returns the one cached panel; `PlaybackComponents.Apply` registers video components on
   `pipeline.VideoChains[0]`; `PlaybackInfoHandle` reports one video chain.
2. **`PlayerPage` owns the engine.** `EnsureSession()` in `OnStart`/`OpenMedia`, `ReleaseSession()` in `OnStop`.
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
10. **The audio path assumes exactly one chain.** `PlayerSession` holds `_audioChains`, a list of
    `AudioChainEntry` (chain, gain filter, downmix filter), and volume, mute, `Duration`, the audio re-resolve
    guard and disposal walk it — but only ever one entry is made, and `AudioChain` hands the first one out for a
    view's report. `PlaybackComponents.SetAudioDecoders`/`SetAudioRenderers` still write
    `pipeline.AudioChains[0]`, and `SelectAudioStream` acts on the first entry. The pipeline underneath already
    holds many (`AudioChains`, `AddAudioChain()`, `RemoveChain`).
11. **The subtitle path is already per chain.** `PlaybackComponents.ApplySubtitle(chain)` and
    `SetSubtitleDecoders`/`SetSubtitleRenderers(chain, …)` take a chain, and `SubtitleOf(view)` reads the view's
    chain. What is single is the view's one chain field and the one panel it draws into.

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

**Decision 4 — the sound is session-wide; the picture and the words are per view.** Audio has no surface, one device
plays it and one clock times it, so one chain serves every page. Subtitles turned out not to be the same: a subtitle
chain draws into a swap chain, a swap chain attaches to one `SwapChainPanel`, and a panel belongs to one window — so a
session-wide subtitle chain crashed the moment a second page put its panel in a second window. The words are therefore
per view, like the picture (B3), which also means choosing a subtitle track per window is already possible. That is more
than v1 asked for, and it cost nothing extra: K1 had already made every subtitle surface share one Direct3D device.

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

**Decision 8 — the session is the engine: one class, not two.** B1 first introduced a `PlayerSession` that *held* a
`CoreEngine`, and that was a wrapper with nothing in it: the sharing and the lifetime live in `PlayerSessionManager`,
so all the type carried was an id, a queue, a playback model and a six-line "load once" guard, while every call site
read `session.Engine.…`. Worse, two objects describing one playback is what produced a duplicate reference the moment
a second holder appeared. They are merged: the type is `PlayerSession`, it owns the pipeline, the queue, the cursor
and the surfaces, and the page holds exactly one of it. What stays separate is `PlayerSessionManager`, which is real
behaviour (registry, page count, disposal).

**Decision 9 — an audio stream belongs to the session, never to a view.** The sound has no surface and one device
should time it (decision 4), so a chain per window would mean two clocks for one playback. The session therefore
holds the audio chains and the two operations that change them — add a stream, remove a stream — and a view only
reports what the playback is playing. `EnsureAudioChain(pipeline, view)` as it stands is a staging point: the view
argument exists only so the chain reaches that view's report, and it goes away in F1.

**Decision 10 — a subtitle stream belongs to the view, and a view may hold several.** Words are drawn into a panel,
and a panel belongs to a window (B3), so the chains follow the view; several tracks at once therefore means several
chains on one view, added and removed through that view's page, each carrying the view's presentation. The session's
role stays what it is today — it resolves, reconciles and aggregates the outcome per view.

The pipeline makes that possible in one panel (decision 14): a compositor sits between the subtitle renderers and the
swap chain that reaches the panel, so several chains share one surface instead of needing one panel each. So a view
keeps exactly one subtitle panel and one render target, and the number of chains on it is free.

**Decision 11 — the session observes its own playback, so opening happens once.** The queue and the playback are the
session's, and a page only ever *shows* them; letting a page react to a playback change and then open the media meant
every page ran the open, so two pages needed a gate to agree on who actually did it, and each page's view model told
the page to do it through a callback on the way. The session subscribes to `Playback.PlaybackStateChanged` instead,
coalesces changes that arrive while it is opening, opens the media, and raises `MediaChanged` — a plain notification,
because what a page does about it is the page's own business and the session has no reason to wait for it. A page's
part is what only it can do: what it shows, its own view's chains, whether playback starts. `Ended` moves with the
open for the same reason — one playback ends once, so one place decides repeat and auto-next.

**Decision 12 — a tab title is not assigned before the tab item has loaded.** Assigning an *empty* `TabViewItem.Header`
while the item's template is still being realized breaks that realization: the item's own content presenter then takes
the tab's `Frame` away from `TabContentPresenter` — the `TabView` content host that `TabContentPresenter_Loaded` moves
out of the template into `ContentGrid` — and that presenter is left with no children, so its next measure returns
`E_INVALIDARG` from native `Microsoft.ui.xaml.dll` (`FrameworkElement.MeasureOverride` → `base.MeasureOverride`). The
app treats an unhandled UI-thread exception as fatal, so the tab painted nothing and the process ended; the tab
bookkeeping the failure left behind is also what made closing the window throw. `MainPageAbilityForTab.SetTitle`
therefore holds the title in `TabInfo.PendingTitle` until `TabItem_Loaded` applies it.

The failing element was identified first: the tab's `Frame` was measured through both presenters in one pass, first
through `TabContentPresenter` (`ContentGrid < ContentGridImmersive < Grid < MainPage`, available width 1905), then
through the item's presenter inside the tab strip (`TabViewItem < LayoutRoot < TabContainer < ContentPresenter`,
available width 164), confirmed with a native debugger and by re-measuring the tree by hand.

Which component reaches that state was narrowed with a repeatable harness: one build, one configuration per run chosen
by a marker file, three launches per configuration, always the same click on the first library item.

| configuration | crashes |
| --- | --- |
| session load started from `OnStart` (baseline) | 3/3 |
| the same, deferred to `LayoutUpdated` | 0/3 |
| `session.Load` not called at all | 0/3 |
| load called, the open body skipped entirely | 3/3 |
| load called, the page's `MediaChanged` handler inert | 3/3 |
| load called, the page's view-model observers not registered | 0/3 |
| observers on, only the playback observer skipped | 0/3 |
| playback observer: head only (`TransportBar`, `Emit`) | 0/3 |
| playback observer: tail only (title, icon, settings, URL, sync) | 3/3 |
| without `SetTitle` and `SetIcon` | 0/3 |
| without `SetUrl` and `SyncCurrentMedia` | 3/3 |
| without `SetPlayerSettings` | 3/3 |
| without `SetIcon` only | 3/3 |
| without `SetTitle` only | 0/3 |
| the player tab reported as non-immersive | 3/3 |
| header assigned from the playback dispatch, title `'1'` | 0/3 |
| header assigned from the playback dispatch, title `''` | 3/3 |
| header assigned directly in `OnStart`, title `'EARLY'` | 0/3 |
| header assigned directly in `OnStart`, title `''` | 3/3 |
| `SetTitle` holding the title until the item loads (the fix) | 0/3 |

So the window is "the page has started and the tab item has not loaded yet", but inside it only an empty header is
fatal: the same assignment with a non-empty value is harmless at the same moment, from the same dispatch. Immersive
mode and nesting are not factors at all — a player tab treated as non-immersive still crashed 3/3, and a direct
`OnStart` call with an empty value crashed 3/3. Once the item has loaded, even an empty header is safe.

Why only the player tab: it is the only page that ever assigns an empty title. Its observer reports
`SetTitle(string.Empty)` whenever the media is not known yet, which — after the session took over the open — was always
the case at the playback dispatch because the media only reaches a page in `MediaChanged`, after that dispatch. Home
pages report `'New tab'` and are unaffected.

That same ordering is why the tab also lost its title when the crash was first papered over: reading `ViewModel.Media`
gave null and the page reported `''`. The header is now taken from `session.CurrentMedia`, which is already set at that
point; verified by reading the tab titles back through UI Automation (`'New tab', '1'`).

The earlier workaround — a page creating its session waited for its first `LayoutUpdated` before loading its queue — is
no longer needed and has been removed.

**Decision 13 — several sound tracks are chosen from one menu, behind a preference.** The audio track menu (`Audio →
Select audio track`) gains a toggle at its top, *Allow multi-tracks*, which is a preference
(`PlayerMenuContext.AllowMultiAudioTracks`, default false). It is a UI preference, not a property of the media, so it
outlives the tab; today it sits in a static field on `PlayerMenuContext`, which should move into the settings system
when there is one. With it off the submenu behaves as before: one track checked, plus *Off*. With it on every track is anindependent checkbox that says whether that track's chain is playing, and clicking one adds or removes that chain —
several tracks therefore play at once, each with its own chain, its own components and its own gain/downmix filters,
all of them the session's. *Off* with several chains clears them all. Turning the preference off while several play
collapses to the first, so a preference change never silences the playback by surprise. The toggle needs no rebuild of
the open menu: a menu item closes the menu it belongs to, and the mode is read when the menu is built.

## 5. Phases

Invariant, as in the previous plans: one coherent change per step, a build that is clean (0 errors, 0 warnings) at
the end of every step, and the manual check named for the step performed before the next one starts.

The sections are in the order they are done: A, B, C, F, G, D, E. The letters keep the names the earlier steps and
decisions already use in their cross-references, rather than being renumbered by order.

### Phase A — the video output becomes a view

| # | Step | Change | Review focus | Verify |
|---|---|---|---|---|
| A1 | One view, extracted — **done** | `PlayerView` holds the panel, the render target, the video chain, its `VideoTransform` and its `PlaybackInfoHandle`. `CoreEngine` keeps exactly one, created where the panel is created today; `CreateVideoSurface`, `SetVideoTransform`, `PlaybackInfo` and `SelectStream(Video, …)` forward to it. No new public members | nothing but movement: the page, the menus and the read surfaces must not notice. `PlaybackInfoHandle` is already per-chain, so the view's handle is an `Attach(pipeline, viewChain, audio, subtitle)` | build + play a file: picture, surface resize, stretch/rotation change, video track switch |

### Phase B — the session and its views

| # | Step | Change | Review focus | Verify |
|---|---|---|---|---|
| B1 | The session and its manager — **done** | The engine *is* the session (decision 8): `CoreEngine` is renamed `PlayerSession` and absorbs the id, the `PlayQueueModel`, the `PlaybackModel` and a first-caller-wins `Load`, which is the hook the attaching page needs in C1. `PlayerSessionManager.Shared` maps an id to a live session and counts pages, disposing it when the last page releases (outside the lock). The page holds a session instead of an engine; `EnsureEngine`/`DisposeEngine` become `EnsureSession`/`ReleaseSession`; `session.Engine.…` is gone, and the locals, the event handlers and `PlayerMenuContext.Engine` are named for a session. The route is unchanged, so the session id is still the play queue id — but it is now resolved in `OnStart`, so a page always knows which session it is on before the queue is read. Views arrive in B2 | the page is the only holder: the view model is given the session's `PlaybackModel` and nothing else, and its "load the queue" method is gone because loading is the session's. Volume and mute are applied only by the page that created the session, and are emitted stickily so the order against `ObserveData` no longer matters | build + play a file, next/previous, switch tracks, close the tab: exactly as before |
| B2 | Views become plural — **done** | `PlayerSession` holds a list of views. `CreateView()` gives a page its panel and nothing else; each chain is made by its own `EnsureVideoChain`/`EnsureAudioChain`/`EnsureSubtitleChain`, which finds the chain or makes one and configures it alone (`PlaybackComponents.ApplyVideo(chain)`) so the chains already playing are untouched: `EnsurePipeline` calls all three for the views that existed before the pipeline, and `SelectVideoStream`/`SelectSubtitleStream` call theirs for a view created mid-flight. So a view created while playback runs gets its chain the first time it has a track to show, and never one that idles with nothing to play. `RemoveView(view)` records the removal and releases the panel *after* the reconcile that stops the chain — a removal only takes effect at a reconcile, so disposing the panel first would leave the chain presenting into a dead surface. `SelectVideoStream(view, stream)` and `ResultOf(result, view)` are the picture's; `SelectStream(kind, stream)` and `ResultOf(result, kind)` the sound's and the words'; `ChainOf` no longer has a video case. `Duration` takes the longest of the views' streams so a second page cannot shorten what the first is watching. `CreateVideoSurface`, `SetVideoTransform` and `PlaybackInfo` are gone from the session: the page holds its own `PlayerView`, and `PlayerMenuContext` carries both the session and the page's view | a page's picture is its own chain and its own read surface; the sound and the words are the session's. `EnsurePipeline` adds the chains *before* `Apply`, because the components go to every video chain there is. `PlaybackInfoHandle.Attach` takes the sound and the words as nullable, because a view's handle can exist before those chains are configured | build + one page: parity with B1, which C1's check then exercises with a second page |
| B3 | The words become per view — **done** | Triggered by a crash: `CreateSubtitleSurface` returned one cached panel, so a second page putting it in a second window threw. A subtitle chain draws into a swap chain, a swap chain attaches to one panel, and a panel has one window, so the words had to follow the picture. `PlayerView` gained a subtitle panel, its render target, its chain and `EnsureSubtitleRenderTarget()`; the chains are attached separately and one at a time (`AttachVideo`/`AttachSubtitle`, and for a while a third for the sound, dropped once the report read the sound from the session), each taking only its own chain so no attach point has to be told about the others; either can be the one a page asks for first, and the report exists only once there is a picture to report on. a view's picture and its words are made by their own `EnsureVideoChain`/`EnsureSubtitleChain`, so the page that picks subtitles before video gets the words' chain then and the picture's later. The session lost `_subtitlePanel`, `_subtitleRenderTarget`, `_subtitleChain` and `CreateSubtitleSurface`; `SelectStream(kind, …)` split into `SelectVideoStream(view, …)`, `SelectSubtitleStream(view, …)` and `SelectAudioStream(…)`, and `ResultOf(result, kind)` into three extensions on the reconcile result — `VideoOf(view)`, `SubtitleOf(view)` and `AudioOf()`, the last taking every audio chain together — so there is no kind-shaped member left on the session and it no longer answers what reconciling built. `PlaybackComponents` gained `ApplySubtitle(chain)`, and `SetSubtitleDecoders`/`SetSubtitleRenderers` take a chain instead of `SubtitleChains[0]`. `SetSubtitlePresentation` is the playback's, so it is pushed into each view's `PlayerView.SubtitlePresentation` and the view applies it to its chain — the same split as `VideoTransform`, which a page owns | one surface per window, so nothing is shared where sharing cannot work; the sound is untouched, since it has no surface and one device plays it. Every view draws its own cues from the same stream, which packet fan-out already feeds to both (J5/J6) | build + open a file, "New window": the second window shows the same picture-less state as before, and a subtitle track chosen there draws in that window without disturbing the first |

### Phase C — the second window (all in memory)

| # | Step | Change | Review focus | Verify |
|---|---|---|---|---|
| C1 | A second page attaches instead of opening — **done** | The video host menu gains a `NewWindow` item (`StringResourceProvider.NewWindow`, already in all nine locales) that runs the existing `OpenTabProvider` action with `PARAM_URL` = the page's own address and `PARAM_WINDOW_ID = WINDOW_ID_NEW`, so `MainWindow.Open(url)` makes one tab with no `OldTabId` — no tab record is copied. `EnsureSession` records whether it created the session; a page that attached skips the queue entirely and calls `ViewModel.Adopt(playback)`, which does exactly two things: shows the media the session is playing, and publishes the playback change. Everything else the page shows is derived from the session — `ShowState(session.State, session.Error)` and `Session_PositionChanged()`, the same two methods the events route to, so there is one way for a page to learn the state and the position whether it just joined or was told. `OpenMedia` also adopts instead of opening when the session already holds that media, so a page whose view model reacts to the shared playback cannot reset it | `ViewModel` derives nothing: it shows the media and raises the change, and the state comes from the session. `Session_StateChanged` was split so that showing the state is separate from acting on the change — the `Ended` branch runs `HandleMediaEnded`, and a page joining an already-ended playback must not skip the track it just joined. `PlayerSession.Error` joined `State` and `Position` as playback-wide state | build + open a file, "New window": a second player window on the same media, blank picture until a track is picked there, sound still once, position and transport shared |
| C2 | Two views, two pictures — **done** | Each page chooses its own picture and words for its own view; B2 and B3 had already routed every selection that way, so the defect left was opening, which both pages ran on one `Next()`: both read `session.Sources` before either had added a source, and both reset the pipeline and added the media again. The round trip that produced it — `Playback` change → view model → a callback into the page → `PlayerSession.Open` behind a semaphore → back to the page — is gone. `PlayerSession` subscribes to its own `Playback` (it holds it, so nobody has to tell it), so one change opens one media, coalescing changes that arrive mid-open the way the view model used to; it opens the media's own source, the sources its remembered tracks live in, the same-name subtitles next to it, and the sound, then raises `MediaChanged` — a plain notification the session does not wait on, so a page sequences its own work and drops a media that has already been replaced by the time an await returns. A page's half is what only it can do: `ViewModel.ShowMedia` (title, panel, history, favorite), its own view's picture and words via `TrackSelection.Resolve`, the reconcile that says whether anything played, the seek, and whether playback starts. `TrackSelection` (with `SelectedTrackModel`) moved from `Views/Pages/Player` to `Common/Playback/Core` because both halves use it, and gained `Resolve(media, kind, sources)` — "which track of this kind does this media want" — which replaces three copied blocks. `Ended` also moved: the session decides repeat-one and auto-next, so two pages cannot each advance the queue | a page must never add a source or choose the sound (it no longer can, except through the session's own selection API used by its menu); the session must never name a view, which is why `MediaChanged` hands over a media and a result rather than an instruction. Opening once must survive a page being added or removed mid-open — hence the awaited handlers rather than a fan-out of fire-and-forget callbacks. The seek stays with the page because `SeekCore` refuses unless the state is Paused, and only the first reconcile gets there | build + two windows: A on track 1, B on track 2, each shows its own; set B to track 1: both show track 1 (packet fan-out), sound unaffected; hold "next" in both windows: one reset, one source, no duplicate; close B: A keeps playing |
| C3 | The media record is the session's — **done** | A page was writing what the media remembers: `SetAsVisited` and the history row when it showed it, `SetDuration` when the length was first known, `SetProgress` every five seconds and again at the end, and `FavoriteModel` when the star was clicked. All of it belongs to the playback, not to a window: two pages saved one position twice, and a page joining mid-playback rewrote what the media had already recorded. `PlayerSession` now keeps `_openedMedia` and writes those records itself — `RecordOpen` (visited + history), `SaveProgress` (progress, and a length it did not know), `CompleteProgress` (on `Ended`), `SetFavorite`, and the debounced volume/mute settings — saving the outgoing media's progress just before `Reset` and once more in `Dispose`, where the pipeline still knows where it was. `PlayerPageViewModel` keeps presentation and transport only: `ShowMedia` became synchronous, and `SavePosition`, `CompleteMedia`, the duration write, the favorite write and `SaveVolumeAndMute` are gone | nothing may write a media's record from a page, and no write may happen after `_openedMedia` was cleared — that is why it is cleared right after `Reset` and set again only once a source has opened, so a failed open cannot record progress against the media that just closed. `SaveProgress` reads position and duration before posting its write, because `Dispose` runs it against a pipeline that is about to go | build + play a file, seek to the middle, close the tab, reopen: it resumes; play to the end: the media is marked played and the next one starts once, with two windows open; press the star in one window: both show it favorited (the existing `FavoriteUpdated` event) |

### Phase F — several audio streams

Decision 9: the session holds the sound, a view only reports it. These steps come after C because the view/attach
model they change is the one C1–C3 settle, and before D because the record persists the track selection they turn into
a set (F3).

| # | Step | Change | Review focus | Verify |
|---|---|---|---|---|
| F1 | The sound becomes a list — **started** | `PlayerSession`'s `_audioChain`, `_gainFilter` and `_downmixFilter` are already one entry per chain (`AudioChainEntry` = chain + its gain filter + its downmix filter): `SetVolume`/`SetMuted`, `Duration`, `SelectAudioStream`, the audio re-resolve guard and disposal walk the list, and `PlaybackInfoHandle` reads `session.AudioChains` (the first entry) because the sound belongs to the playback. Done: `CreateAudioChain` gives a chain the session's volume and mute and the components the settings name, `AddAudioStream(stream)` puts a stream on a chain — taking over a chain no track is chosen for, which is what the open leaves behind, so the open path is an add and nothing else — `RemoveAudioStream(stream)` clears that chain instead of destroying it, so the next stream takes the same chain over, which is the path a single-track playback switches tracks on, and `PlaybackComponents` applies audio decoders and renderers per chain (`ApplyAudio(chain)`) instead of to `AudioChains[0]`. Chains that carry a stream are kept ahead of cleared ones, because the first audio chain is the one the picture follows. There is no "only this one" operation in the session: the single-track menu expresses it with the two it has — remove the others, then add — because that policy is the menu's, not the playback's. What remains of this step: `PlaybackInfoHandle`/the report still reads the first chain, so the info page shows one audio row | nothing may still say `AudioChains[0]`: a chain added later must get the same components and the same filters as one added at open. `AudioOf()` (B3) already aggregates, so "any chain failed" keeps its meaning. No audio chain may be reachable from a view alone | build + one file: sound plays, volume and mute work, switching an audio track rebuilds only that chain, the info page shows the audio rows it shows today, and switching tracks twice leaves no chain behind |
| F2 | Two audio streams at once — **started** | The menu gained the shape decision 13 describes: an *Allow multi-tracks* toggle at the top of `Audio → Select audio track`, and, with it on, a checkbox per track that adds or removes that track's chain (`AddAudioStream`/`RemoveAudioStream`), with *Off* clearing them all. Each chain is built the way the first one is, so nothing else in the session had to learn about a second sound | the open question is still the output, not the app layer: two chains build two `IAudioRenderer`s on one endpoint. Whether a shared-mode endpoint mixes them, and which of them times the picture (today "the first audio chain keeps the clock"), has to be established before this is called done. If the answer is two clocks that drift, the fix is one output device fed by N decode chains — a mixer in the audio backend, i.e. a pipeline plan of its own (see §6) | build + `Multi-tracks/multi-track.mkv` (two audio streams): turn the toggle on, check both tracks, both audible, transport and position unchanged, picture still in sync after five minutes, unchecking one leaves the other playing |
| F3 | The menu and the record — **moved**: the menu part is done in F2, the record part is D2 | `TrackSelection` stores a set of audio indices per media instead of one `SelectedTrackModel` (fact 8), the menu offers every active track with a checkmark, and the info page lists each chain rather than one. The checkmark half is in F2; storing and reading a set is what D2 writes down, together with the subtitle set, so the schema is written once | the preference is read at open by `SelectTracks` and again by an attaching page (C1); both must handle a set, and a stored index that no longer exists must be dropped rather than failing the open | see D2 |

### Phase G — several subtitle streams on a view

Decision 10: the words follow the view, so several tracks means several chains on one view. What stands in the way is
the pipeline, not the app, and it stands in the way in three places, so the phase starts there:

- **A chain draws straight into the panel.** A subtitle renderer builds a swap chain and attaches it to its target, so
  two chains on one target each attach a swap chain to the same panel and the last attach wins — which is why B3 and
  the old G2 concluded "one set of words per surface". The answer is a compositor **per render target** between the
  renderers and that target's swap chain (G2): one layer per subtitle renderer, composed in registration order. Two
  chains on different targets need no composition at all — each target has its own compositor with a single layer.
- **Win2D lays out each chain alone.** Two chains drawn as two layers would stack on top of each other, because each
  lays out its own cues from the bottom margin up. So chains that share a render target and the Win2D renderer share one
  *renderer instance* (G3); because a layer is a renderer (G2), they are then automatically one layer laying out every
  cue set together. The key is the target plus the renderer kind, not the video chain: what decides the bands is the
  surface being composited, and words bound to different pictures can only reach the same surface through two
  different chains, which the shared layer stacks in its own order.
- **The words are laid out against another window's picture.** `ReconcileLayoutSources` gives every subtitle chain the
  *first* video chain of the pipeline, so a second window's cues are positioned from the first window's frame — wrong
  as soon as there are two views, and it has to become the video chain of the same view (G1).

Persistence is not part of this phase: the subtitle choice becomes a set here (G5) and is written in D2, which reads
the shape this phase leaves behind.

| # | Step | Change | Review focus | Verify |
|---|---|---|---|---|
| G1 | A chain is laid out against its own view's picture — **done** | `ISubtitleChain` gained a settable `LayoutVideoChain` (`IVideoChain`, because a public interface cannot name the internal `IPictureFrameSource`), held in an `AtomicCell` because the render thread reads it every frame while the host writes it. `ReconcileLayoutSources` stopped choosing a picture: it only lets go of one that has left the pipeline. **The binding is the view's**, not the session's: `PlayerView.AttachVideo` binds the view's existing words and `AttachSubtitle` binds the new chain, so the pairing lives where both chains are, and no caller can bind one view's words to another view's picture. A chain with no picture keeps drawing full-frame, which is what a page that picks subtitles before a video track needs | the pipeline must never pick a picture and must never bind a chain across views; `LayoutVideoChain` is settable, so it is not also an `AddSubtitleChain` parameter. J9's test changed meaning and was replaced by two: words follow the picture their host named, and a removed picture takes nothing with it | pipeline tests: `LaysTheWordsOutInThePictureTheyWereBoundTo` (unbound words keep the whole surface, then binding takes effect without stopping anything) and `LetsGoOfAPictureItWasLaidOutInWhenThatPictureIsRemoved`; `LaysTheSubtitlesOutInThePictureRatherThanTheSurface` still passes because the test host names its picture, as a real host does. App: open a file with subtitles, then "New window" with a different stretch mode — each window's cues sit on its own picture (G4 makes the second window's chains exist) |
| G2 | The subtitle compositor — **done** | A `SubtitleCompositor` per **render target**: `SubtitleSurface` stopped owning a swap chain and became a *layer* — an offscreen `CanvasRenderTarget` of the target's size plus a place in that target's compositor. `DrawAndPresent` draws into the layer's bitmap and then asks the compositor to compose, which draws every layer of that target in creation order over the swap chain and presents once. The compositor is acquired per target with a reference count and released with its last layer. Two chains on different targets never meet. One device per target: Win2D refuses to draw an image with a device other than the one that created it, so the layers' bitmaps are made by the compositor's device, and drawing plus composing happen under the compositor's lock. A layer with no target (a renderer measuring text, or an offscreen check) still makes its own device. The swap chain is still handed to the panel when the renderer is configured, as before, rather than at the first frame | composing touches one swap chain from more than one render thread, so it happens under the compositor's lock; a layer's bitmap persists between presents, which is why composing includes a chain that did not redraw. Resizing replaces the target's swap chain, because a Win2D wrapper around a back buffer cannot be resized | `SubtitleCompositorTests`: one compositor per target, two targets kept apart, a compositor let go with its last layer, and two layers of one target composed with a single swap chain handed to the panel. The painted result is checked on screen, not here |
| G3 | Win2D renderers on one target share a layer — **done** | Sharing happens **under** the Win2D renderer, not at resolution: a `Win2DSubtitleLayer` per render target, joined by every Win2D renderer bound to that target and released with the last of them. Each renderer keeps its latest cues and presentation and contributes them to one drawing pass, so the band stacking that separated two events of one chain now separates two chains — one set of bands for the whole layer. `Configure` leaves the previous layer and joins the new one, which is what makes a rebind follow the target instead of pinning the sharing to resolution time. The drawing itself lives in the layer and a renderer only hands its cues over, so the layer never reaches back into a renderer. The layer owns a `SubtitleSurface`, so a shared layer is exactly one compositor layer (G2); libass is untouched and keeps one layer per chain, which the compositor stacks | the cues a renderer holds stay alive until that chain shows again, which is what makes holding them across another chain's draw safe; the participant list is read as a snapshot so a chain can leave while another draws; state is a single volatile reference per renderer. A renderer with no target still makes its own device, which is what measuring and offscreen checks need | `Win2DSubtitleLayerTests`: two renderers on one target share the layer's device, two targets stay apart, and a rebind moves a renderer out of the layer it was sharing while the other stays. The pixel checks that used to drive the renderer now drive an offscreen layer. What the composed result looks like is still not checked here |
| G4 | The words become a list per view (app) — **done** | `PlayerView` replaces its subtitle chain field with a list; `AttachSubtitle` adds to the list and gives the new chain the view's `SubtitlePresentation`, which is then pushed to every chain. `PlaybackInfoHandle` reports the chain that is playing (else the first), so the info page keeps answering what it answered. `PlayerSession` gains `AddSubtitleStream(view, stream)` and `RemoveSubtitleStream(view, stream)` — the same two primitives the sound has, one chain per stream, a chain no track is chosen for taken over rather than left idle, and no chain of any kind is built before a track is chosen for it — the picture's, the sound's and the words' alike. `SelectSubtitleStream` is gone: "make this the only set" is policy, so the menu composes it the way the sound's single-track path does, and the file loader keeps "the picked file is what is shown". `ReconcileResult.Subtitle(view)` aggregates the view's chains the way `Audio()` aggregates the session's, and the view binds each new chain to its own picture as it attaches it (G1), so no chain reaches a picture except through the view it belongs to | the view's words stay the view's: nothing may reach a chain without going through the view, and the session must never confuse one view's chains with another's. `PlaybackComponents` needs no change — B3 already made `ApplySubtitle` and the subtitle decoder/renderer setters take a chain | build + one file: subtitles play, switching a subtitle track rebuilds only that view's chain, "New window" still draws its own cues, and a second window's cues are laid out against its own picture (G1) |
| G5 | The menu: a preference and several tracks — **done** | Mirroring the sound (decision 13): `Subtitle → Select subtitle track` gains *Allow multi-tracks* below its track list, where the sound's sits — a UI preference of its own, off by default — and with it on each track is an independent checkbox that adds or removes that view's chain, *Off* clearing them all; with it off the submenu is what it is today. The menu is one method again, not a single-select path and a multi-select path, the way `CreateAudioTrackItems` was merged, and each kind now owns its own item builder rather than sharing a generic one: the picture's list selects one track, the sound's and the words' switch mode in place. Turning the preference off collapses the view to the first set | per view, not per session: the mode is a preference, but what it addresses is one view's chains, and an attaching page (C1) begins with no subtitle chain at all. The subtitle preference must not be the audio one — the two menus are independent | build + two subtitle tracks on in one window and one in the other: each window shows its own sets, checking and unchecking changes only that window, and the choices survive a track switch within the same launch (they are not persisted yet — see D2) |
| G6 | The words at open apply a set per view | `SelectTracks` applies the media's remembered subtitle choice to each view as a set, so a page that attaches later starts from the same choice, and a stored index that no longer exists is dropped rather than failing the open. Reading the set at *attach* rather than once per session is the part C1 needs | the preference is media-level and the chains are view-level, so the set is applied once per view, at the open and at the attach, and applying it twice must not add a chain twice | build + choose two subtitle tracks, open "New window": both windows show both sets; reopen the media: the same choice is requested (it is only remembered in memory until D2) |

### Phase D — persistence

These steps come after F and G, because the record persists what those steps settle: today `TrackSelection` holds one
audio index and one subtitle index per media, and F3 and G3 turn both into sets. Writing a schema first and rewriting
it a phase later is what this plan avoids, the same reason B3 was allowed to reshape `ResultOf` rather than keep the
kind-shaped API.

| # | Step | Change | Review focus | Verify |
|---|---|---|---|---|
| D1 | The session record, and a route that names it | `PlayerSessionRecord` — queue, playback state, position — persisted at `/Resources/PlayerSessions/{sessionId}`; written when the first view attaches, when the queue or the playback state changes, and at last detach. `RouterConstants.ARG_SESSION`; `GetMediaRoute` writes a draft to `/Resources/Runtime/PlayerSessionDrafts/{sessionId}` and returns `app://player?session={id}` — one shape, always. `PlayerPage.GetPlayQueue` becomes "the record, else the draft (which it then deletes), else an empty queue". `ARG_PLAY_QUEUE_ID`, `ARG_PLAYBACK`, `ARG_POSITION`, the runtime `PLAY_QUEUES` key, the per-tab `PlayQueues` write and the per-change URL rewriting are all deleted | the record is the only persisted state of a session, and one record per session. Route creation stays free of persisted writes and the URL never changes after it is built — which is what lets the identity of a tab be its session rather than a blob that changes under it. A record or draft that fails to parse leaves the session empty rather than failing the app | build + open a file: the tab URL is `app://player?session=…`; edit the queue (add items), inspect `/Resources/PlayerSessions` for one record holding the queue as edited and the position, and no draft left behind; restart with window restore on: the tab comes back with the **edited** queue, the track and the position — where today it would come back with the queue as it was when the tab opened |
| D2 | The tab record | `/Resources/Tabs/{tabId}/PlayerSession` holds `SessionId` and the page's video `SelectedTrackModel`, plus the audio and subtitle sets F3 and G3 introduce. Written when the choice changes and when the page stops; read in `OnStart` before the view selects anything. `WindowManager.CleanUpTabResources` also prunes `/Resources/PlayerSessions` entries no restored tab names | per-tab state must never overwrite another tab's; the media-level preference is still written (decision 3). A tab whose record names a session that no longer exists opens with an empty session rather than failing | build + two windows on one session with different tracks, restart: each window restores its own track and both restore the same position; a third launch after closing one window prunes nothing it still needs |

### Phase E — edges

This step comes after D because it verifies what D writes: that a dragged tab carries its `/PlayerSession` record with
it, and that the last view out saves before disposing.

| # | Step | Change | Review focus | Verify |
|---|---|---|---|---|
| E1 | Closing, moving and sharing | Last view out saves and disposes; closing one window leaves the other playing; a tab dragged into another window carries its tab record (the existing `CopyTree` path — confirm it carries `/PlayerSession`); two tabs on one session inside one window behave the same as two windows | nothing may depend on attach order beyond the existing "first audio chain keeps the clock" rule. A hidden tab keeps decoding (known cost) but must not steal the clock | build + close the second window while the first plays: no hiccup, no second sound; drag the tab out and back; two tabs of one session in one window, switched back and forth |

## 6. Known costs and open items

- **Selecting a track re-anchors the shared playback.** `ReconcileCore` advances the epoch whenever any chain  reports a change, and the epoch is pipeline-wide: the reader seeks and every chain flushes. In C2 the visible
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
- **Two audio chains mean two renderers.** Nothing in the app prevents it, and a shared-mode endpoint would mix
  them, but each chain is timed by its own renderer, so two streams can drift apart and only one of them can be the
  clock the picture follows (F2). Mixing N decode chains into one output is a backend feature, not an app-layer one.
- **Several subtitle chains mean several panels** until the subtitle renderer can composite N cue lists into one swap
  chain (G2). The current answer — one render target per chain, stacked by the page — costs one swap chain per track
  and a decision about overlap and ordering.
- **`TrackSelection` stores one index per kind** (fact 8). F3 and G3 turn that into a set, which is the first change
  in this plan that touches what a *media* remembers rather than what a page does.

## 7. Definition of done for v1

1. One `PlayerSession` serves every page attached to it; a page owns no playback state of its
   own.
2. "New window" opens a second page on the same session, selecting no video track, and the track it chooses is
   independent of the other page's.
3. Both pages show the same position and transport state; sound plays once.
4. The session survives a restart: queue, playback state, position, and each page's video track come back.
5. Closing a page leaves the others playing; closing the last one saves and disposes.
6. Single-page behaviour is unchanged when no second page exists.

Phases F and G add no v1 feature — v1 ships one sound and one set of words per page — but they are done before D
because D persists the track selection they turn into a set (F3, G3).
