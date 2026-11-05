# PlaybackPipeline chain decomposition — v1 plan

Status: v1 complete. Phases A–G done (A1–A6, B1–B4, C1–C6, D1–D6, E1–E5, F1–F4, G1a/G2/G3), except the clock
governor (G1b), which belongs to the multi-chain milestone. The definition of done is checked off below.

`PlaybackPipeline` was 3212 lines and held one video chain, one audio chain and one subtitle chain inline:
their queues, their threads, their component slots, their statistics and their component reconciliation.
The goal of this plan is to make a chain a thing of its own, and to make the pipeline an orchestrator that
happens to hold exactly one chain of each kind.

**v1 exposes no multi-chain ability.** The public API does not change, the app does not change, and the 150
tests in `PlaybackPipelineTests` do not change. What changes is underneath: chains become objects held in a
list, so that a later milestone can allow two of a kind without touching the foundation again.

## Decisions this plan is built on

### 1. Threading: `1 + 2V + A + S`

| Stage | Threads | Why it multiplies |
|---|---|---|
| Read + audio/subtitle decode | 1 (coroutines = `1 + A + S`) | Cooperative loop over sources; a decode is short and already shares the thread today |
| Video decode | one per video chain | A decode call is blocking and uninterruptible |
| Video render | one per video chain | `TryAcquireFrameSlot` blocks up to 50 ms waiting for the display (`D3D11SwapChain`), so two chains on one thread would serialise their vertical-blank waits and both drop |
| Audio render | one per audio chain | `IAudioSubmit` is owned by the thread that calls `MoveNext`, and each renderer carries its own device clock |
| Subtitle render | one per subtitle chain | `PipelineNode` has exactly one waiter: the thread it belongs to |

At `V = A = S = 1` this is exactly today's five threads, so the refactor changes no threading behaviour.
Two audio chains on one endpoint each get their own `WasapiAudioRenderer` and their own stream — the factory
hands out one per call and shared mode mixes them — so nothing in the component registry has to change.

### 2. One timeline, one clock

A pipeline has one `EpochGate` and one clock, and a seek moves every chain. The chain that owns the clock
(audio if the pipeline has a live audio chain, else video) anchors it; nobody else may re-anchor it, and
**only that chain may correct it** from a device clock. A chain that starves reports it, and a chain that is
not the clock's owner never pauses playback for its own sake.

### 3. Chains are declared; `Reconcile` builds them

Declaring a chain touches nothing that runs. Components and threads appear when `Reconcile()` runs, which is
today's rhythm of `AddSource` / `SelectStream` / `Reconcile` and is why the refactor can be verified by the
existing tests without touching them.

### 4. A chain is bound to a stream, which is a source plus a stream

`StreamBinding(MediaSource Source, MediaStreamInfo Stream)` replaces the single triple inside
`MediaSourceHolder` and the `FindSource` lookups that follow it. One chain per stream. Chains bound to the
same source share its read position — the reader delivers each packet to the chain that selected that stream,
and keeps reading a source while any chain bound to it is short of read-ahead.

### 5. Internal only

No chain type appears in a public signature in v1. Chain types are `internal` from the first step, with the
names and the members they will have when published: the public-looking state on the class, the machinery
(`Reconcile`, `Start`, `StopAndJoin`, `WakeAll`, `TryAccept`, `IsBuffered`, `IsSpent`, `ReportEnded`) on a
separate internal interface. Publishing later is a change of accessibility plus `Add`/`Remove`, not a move.

The interface itself waits for its first user. While there is one chain per kind, a chain is a plain class and
the pipeline holds it in a typed field; `IPlaybackChain`/`IPlaybackChainRuntime` and the chain list land in the
step that needs to treat chains alike — the reader's feed and the reconcile dispatch — rather than before it.
For the same reason a chain carries no identifier and overrides no equality: the reference is the identity,
and an id arrives in the step that first has two chains of one kind to tell apart.

### 6. Deliberately not in v1

- `PipelineVersion` stays as it is. Each `ComponentSlot<T>` already carries its own instance and every
  comparator reads only its own role's fields, so a second video chain simply gets a second slot with a
  second instance. Only `BumpVersions`/`CurrentVersions` have to become per chain, and only when chains
  multiply.
- `ReconcileResult` keeps its three named properties: correct while there is at most one chain per kind.
- `PlaybackChainId`, `ChainStatus`, `FindChain`, chain collections, per-chain warnings, per-chain reconcile
  results: all of these belong to the milestone that publishes the model.

## Target layout

```
src/SinePlayer.Core/Playback/Pipeline/
  PlaybackPipeline.cs               public API, lifecycle, state, reconciliation dispatch
  PlaybackPipeline.Components.cs    component resolution and per-chain reconciliation (until Phase E)
  PlaybackPipeline.Reader.cs        the reader coroutine (until Phase D)
  PlaybackPipeline.Video.cs         the two video loops (until Phase C)
  PlaybackPipeline.Audio.cs         the two audio loops (until Phase C)
  PlaybackPipeline.Subtitle.cs      the two subtitle loops (until Phase C)
  PipelineEvents.cs                 state, error, submit, warnings, failures
  PipelineTimeline.cs               epoch, clock, anchor, starvation
  ClockGovernor.cs                  who owns the clock (Phase G)
  Chain/IPlaybackChain.cs           identity, binding, published state
  Chain/IPlaybackChainRuntime.cs    Reconcile / start / stop / wake / feed / report
  Chain/VideoChain.cs
  Chain/AudioChain.cs
  Chain/SubtitleChain.cs
  Chain/StreamBinding.cs
  Chain/IChainFeed.cs               the reader's view of a chain
  Reader/MediaReader.cs             reader loop and routing
  Utils/PipelineThread.cs           stop flag, node, synchronization context, join
```

## Invariants for every step

- **Reviewable on its own.** One coherent change per step. A step that moves code changes no condition, no
  field ordering that matters, and no wake; a step that changes behaviour is called out as such.
- **Build.** `& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' src\SinePlayer.sln /p:Platform=x64 /v:q /nologo`
  (`dotnet build` does not work in this repo). Analyzer diagnostics are errors: IDE0007, IDE0008, IDE2001, IDE1006.
- **Tests.** `PlaybackPipelineTests` stays green **with zero edits** through the whole of v1; a test that has
  to change is evidence the refactor changed behaviour. Run only when asked:
  `dotnet test src\Tests\SinePlayer.Core.Tests\SinePlayer.Core.Tests.csproj --no-build -p:Platform=x64 --logger "console;verbosity=normal"`
- **App untouched.** `src/SinePlayer` must show no diff at any point in v1.
- **Names preserved.** Thread names (`Pipeline - Reader`, `Pipeline - Video decode`, …) stay as they are, so
  logs and traces remain comparable.
- Intermediate steps may leave the build red *inside* a phase; a phase ends green.

## Phase A — seams

| # | Step | Size | Review focus |
|---|---|---|---|
| A1 | Split `PlaybackPipeline.cs` into partials along the existing `#region`s: `.Components`, `.Reader`, `.Video`, `.Audio`, `.Subtitle` | cut/paste ~3200 | nothing but placement. Review by concatenating the files against the original |
| A2 | `WakeAll()` iterates a registered list of wake targets instead of naming seven nodes | ~40 | the same seven registrations; no wake lost |
| A3 | `PipelineEvents`: state, error, `SetState`, `PostWarning`, `PostFailure`, `RaiseWarning`, `Submit`, `SafeInvoke` | ~120 moved | failure still sets the global state exactly as before |
| A4 | `PipelineTimeline` epoch half: `_epochGate`, `AdvanceEpoch`, the ending epoch | ~60 | callers and reads unchanged in effect |
| A5 | `PipelineTimeline` clock half: `_clock`, `_anchoredEpoch`, `StartOrPauseClock`, `AnchorClock`, `NotifyClockMoved`, `SyncClock` | ~120 | the anchor-once-per-epoch rule is untouched |
| A6 | Starvation moves into the timeline as `ReportStarved(kind, bool)` / `IsStarved(kind)`; the single-chain rule is unchanged | ~50 | `StarvedFor` reads the same |

A2–A6 are what make it possible for a chain to exist without a back-reference to the pipeline object.

## Phase B — the chain types, one state move at a time

The chains are introduced in the order they will be finished in, and each one arrives holding real state its own
loops already use, so nothing is a shell. The interfaces and the chain list arrive with their first user — the
reader's feed (D) and the reconcile dispatch (E) — rather than before it.

| # | Step | Size | Review focus |
|---|---|---|---|
| B1 | `SubtitleChain` owns its state: two slots, packet queue, cue window, two nodes, presentation, target. Loops stay in the pipeline and go through `_subtitle` | ~100 touched | pure field relocation; `_readerTailSubtitleTicks`, `_subtitleSurfaceVersion` and the thread stay behind, because their owners have not moved yet |
| B2 | `VideoChain` owns its state: two slots, packet queue, frame queue, two nodes, counters, target, transform | ~150 touched | same; transform is the chain's now, which is what lets two videos crop differently later |
| B3 | `AudioChain` owns its state: two slots, packet queue, audio queue, two nodes, `AudioFilterChain`; `PlaybackPipeline.AudioFilters` forwards to it | ~120 | the audio loop's locals stay local until C6 |
| B4 | `SubtitleChain.LayoutSource` — the video target, video stream and transform that `ResolveSubtitlesFrame` reads, behind one seam | ~40 | the frame rectangle maths is copied verbatim; this is what keeps C2 from dragging the video chain along |
| B5 | Selection becomes a source list plus one `StreamBinding` per chain; `MediaSourceHolder` shrinks to a published snapshot of sources and bindings | ~150 | `AddSource`, `SelectStream`, `DiffSource` and the reader's install path all still see what they saw before |

B6 is the step that makes the reader's later generalisation possible; until D it still publishes exactly one
binding per kind.

## Phase C — loops move, one at a time

Done. Every loop now lives on its chain, and the pipeline's `.Video`, `.Audio` and `.Subtitle` partials are
deleted; what is left is `PlaybackPipeline.cs`, `.Components` and `.Reader`.

| # | Step | Moves into | Size | State |
|---|---|---|---|---|
| C1 | `SubtitleDecodeCore`, `DecodeSubtitle` | `SubtitleChain` | ~140 | done |
| C2 | `SubtitleRenderCore`, `SameCues`, `SurfaceVersion`, `CheckState` → `PipelineEvents` | `SubtitleChain` | ~200 | done |
| C3 | `VideoDecodeCore`, `DecodeVideo`, `DrainVideoDecoder`, `QueueFrames`, `HasRoomForVideoFrame`, `TryWakeVideoRender` | `VideoChain` | ~220 | done |
| C4 | `VideoRenderCore`, `RenderVideoFrame`, `IsVideoStarved`, `DequeueAndDispose`, `Synchronizer`, `TryWakeVideoDecode` | `VideoChain` | ~300 | done |
| C5 | `AudioDecodeCore`, `DecodeAudio`, `DrainAudioDecoder`, `TryWakeAudioRender` | `AudioChain` | ~190 | done |
| C6 | `AudioRenderCore`, `ReleaseInFlightAudio`/`AbandonFailedDevice`, `TryWakeAudioDecode`, `TryWakeSubtitleDecode` | `AudioChain` | ~370 | done |

Each chain now takes `(PipelineTimeline, PipelineEvents)` in its constructor and its loops take what is left
from the pipeline as functions: `stopRequested` everywhere, plus `wakeReader` for the decode coroutines and
`isStreamSpent`/`reportEnded` for the render loops. Those five delegates are the exact list of things D, F and G
remove.

## Phase D — the reader becomes chain-driven

Done. `IChainFeed` is what the reader knows of a chain — `IsActive`, `Tail`, `TryAccept`, `SignalEnd` — and the
reader itself is now `MediaReader`, a class with its own node and its own read-ahead policy. The routing loop
names no kind at all.

| # | Step | Lands as | State |
|---|---|---|---|
| D1 | `IChainFeed`, `StreamBinding`, and the video branch of the reader | `VideoChain.TryAccept` + `Binding` | done |
| D2 | The audio branch | `AudioChain.TryAccept` + `Binding` | done |
| D3 | The subtitle branch | `SubtitleChain.TryAccept` + `Binding` | done |
| D4 | Tails and read-ahead move to the chains | `ChainTail`, `IChainFeed.Tail`, `IsBuffered` over feeds | done |
| D5 | Tails, spent and seek landing | `ChainTail.IsSpent`; `FurthestTailTicks` and `ChooseSeekLanding` stay in the pipeline | done |
| D6 | The reader becomes a class | `MediaReader`, partial deleted | done |

D4 and D5 were done as one step: `IsBuffered` and `IsStreamSpent` both read the tails, so a tail could not move
without the other. `IsStreamSpent` could not become a per-chain method either — it asks whether *some other*
stream still has data past the position, which is why the pipeline passes the furthest tail in.

## Phase E — reconciliation, without kind-shaped flags

Done, but not as planned: reconciliation stayed in the pipeline. Building a chain is *assembly* policy — it needs
a fresh `ComponentResolver`, `PipelineVersion` numbers that must stay globally monotonic, and the shared
`MediaSourceHolder` published to the reader. Handing that to a chain would have dragged all three along with it,
and the chain would have gained a dependency on its siblings through the holder.

What did move is the kind-shape of the flags. `ChainChanges` had one flag per *kind and role* —
`VideoDecoder`, `AudioDecoder`, `SubtitleDecoder` … — and every builder received the whole set and masked out its
own bits. It is now `ChainChange { Source, Decoder, Renderer }`, and each builder works out its own
`sourceChanged` by comparing its own binding with the desired state:

```csharp
bool sourceChanged = !_video.Binding.Matches(desired.VideoSource, desired.VideoStream);
```

`DiffSource`, the per-kind `SameStream` comparison and the flag juggling are gone; `BumpVersions` now takes the
kind and bumps only that kind's version fields.

| # | Step | Lands as | State |
|---|---|---|---|
| E1 | `DiffSource` and the seven-flag enum | per-builder `sourceChanged`, `ChainChange` | done |
| E2–E4 | Builders take `(resolver, desired)` instead of `(…, sourceChanges)` | `BuildVideoChain` / `BuildAudioChain` / `BuildSubtitleChain` | done |
| E5 | `BumpVersions(change, kind)`; `ReconcileCore` merges three booleans | | done |

## Phase F — threads

Done. The pipeline owns one `CoroutineHost` and the chains own their own threads.

| # | Step | Lands as | State |
|---|---|---|---|
| F1 | A thread with its own stop flag | `PipelineThread` | done |
| F2 | The coroutine thread becomes an object | `CoroutineHost` — pipeline-owned, with the reader registering like everyone else | done |
| F3 | Stop and join get one owner each | `StartThreads`/`StopThreads`; `_threadsStarted` and the pipeline's `_stopRequested` gone | done |
| F4 | Chains own their threads | `VideoChain` (decode + render), `AudioChain` (render), `SubtitleChain` (render) | done |

Ownership after this phase:

| Thread | Owner | Stop flag |
|---|---|---|
| `Pipeline - Reader` (reader + audio decode + subtitle decode) | `CoroutineHost`, held by the pipeline | the host's |
| `Pipeline - Video decode` / `- Video render` | `VideoChain` | each its own |
| `Pipeline - Audio render` | `AudioChain` | its own |
| `Pipeline - Subtitle render` | `SubtitleChain` | its own |

**Recorded decision for the future.** v1 keeps one stop flag on the host for all three coroutines, so the
coroutine *set* is a session constant: a chain cannot be stopped while its siblings keep decoding. When chains
multiply, the coroutine's lifecycle must belong to the chain — its own flag, stopped by setting it and waking its
own node — and `CoroutineHost.Run(Func<Task>)` must accept registrations instead of a fixed list, exiting when
the last coroutine has returned. That is the one place where the single-source-of-truth rule is knowingly
deferred, and it is deferred to a single object rather than spread across the chains.

## Phase G — clock governance and ending

| # | Step | Lands as | State |
|---|---|---|---|
| G1a | Starvation belongs to the chain that starves | `VideoChain.Starved` / `AudioChain.Starved`; `PipelineTimeline` takes a `Func<bool> starved` and asks it instead of holding two flags | done |
| G1b | `ClockGovernor`: one owner (first live audio chain, else first video chain) anchors and alone corrects the clock, and only the owner pauses playback for starving | | deferred to the multi-chain milestone: with one chain of each kind, "only the owner pauses" would stop a starving *video* chain from pausing the clock, which `HoldsTheClockBackUntilThePictureHasSomethingToShow` relies on. v1 keeps "any starving chain pauses" |
| G2 | `chain.ReportEnded(epoch)` replaces `NotifyVideoRenderEnded`/`NotifyAudioRenderEnded`; playback ends when every active video and audio chain has reported; subtitles never gate it | `VideoChain.EndedEpoch` / `AudioChain.EndedEpoch`; the report delegate carries no epoch, the chain records it; `TryNotifyPlaybackEnded` reads the chains' bindings | done |
| G3 | Each chain cleans up after itself | `chain.Reset()` clears the queues, resets the slots, drops the binding, clears starvation and the end report, resets the tail and the statistics; the chain subscribes to and unsubscribes from its own target's `SurfaceSizeChanged`. `StopCore` keeps only the assembly state: sources, source holder, timeline | done |

## Definition of done for v1

1. `PlaybackPipeline.cs` holds no decode or render loop, no queue at all, and no thread field of its own. Done: 625
   lines; no `while` is left in it, and the only component slot is `_source` (the assembly's held source, not a
   chain's). `_host` is the coroutine host, which the pipeline owns by decision.
2. Chains are objects held by the pipeline; no per-kind field block remains. Done: `_video`, `_audio`, `_subtitle`,
   `_feeds`. They stay as three fields rather than a list because v1 has exactly one of each kind.
3. The reader routes by chain, not by kind. Done: `IChainFeed.TryAccept` / `Accept` / `Tail`.
4. Threads, stop flags and wake nodes belong to chains. Done, with one knowing exception: the coroutine thread is
   owned by `CoroutineHost`, which the pipeline owns; the chains' coroutines still live on it.
5. The public surface of `PlaybackPipeline` is byte-for-byte what it is today; `src/SinePlayer` has no diff;
   `PlaybackPipelineTests` is green with no edits. Done: no public member was added, removed or re-signed, the app
   has no diff, and the 152 pipeline tests pass unedited.
6. A guard test fails if a chain type starts appearing in a public signature. Done: `PipelineSurfaceTests`
   (`KeepsEveryChainInternal`) scans every type implementing `IChainFeed` and fails if one of them is visible
   outside the assembly, so the chain the reader feeds is what the guard follows rather than a list of names; the
   two abilities a chain is reached through (`IChainFeed`, `IPictureFrameSource`) are held to the same rule and are
   named, because a rename there has to be noticed. C# already refuses a public signature that mentions an internal
   type, so what the guard really holds is the half the compiler cannot: that the chain vocabulary never becomes
   public itself.

## The next milestone (out of scope here)

Publish the model: chain collections, `AddVideoChain`/`AddAudioChain`/`AddSubtitleChain`, `RemoveChain`,
typed `SelectStream`, per-chain `ReconcileResult`, per-chain warnings, `PlaybackChainId`. Then per-chain
versions if they still look worth it, and the app layer rewritten against chains. Nothing in that milestone
requires reopening Phases A–G — which is the test of whether this plan did its job.

**Chain-wise versions.** `PipelineVersion` is one struct carrying all seven fields, and that is incidental:
one chain's version has nothing to do with another chain's, by design. Splitting it into a `ChainVersion`
(Source, Decoder, Renderer) per chain is the honest shape, and it is what would make per-chain version
allocation safe — today `ComponentSlot.Publish` guards with `IsBehind`, which compares every field, so a
chain publishing its own numbers would look stale against another chain's. A `ChainVersion` makes that guard
compare only what the slot owns, and `CurrentVersions`/`BumpVersions` stop needing a global maximum. Worth
doing when the chains multiply; not needed while there is one of each kind.
