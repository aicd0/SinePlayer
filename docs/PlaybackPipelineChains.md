# PlaybackPipeline chain decomposition — v1 plan

Status: in progress. Phases A–C done (steps A1–A6, B1–B4, C1–C6); next is Phase D.

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

| # | Step | Size | Review focus |
|---|---|---|---|
| D1 | `IChainFeed` (`TryAccept`, `IsBuffered`, `IsSpent`, `SeekLanding`, `ReportTail`) implemented by `VideoChain`; the video branch of `ReaderCore` uses it | ~80 | the other two branches still work as before |
| D2 | The audio branch uses its chain's feed | ~60 | |
| D3 | The subtitle branch uses its chain's feed | ~60 | the reader no longer names a kind |
| D4 | `IsBuffered` becomes a query over the chains: stop reading when any chain is at `ReadAheadMax`, keep reading until every chain is at `ReadAheadMin` | ~120 | the read-ahead policy is the same rule it was, applied to a list |
| D5 | `AdvanceReaderTail` → `feed.ReportTail`; `IsStreamSpent` → `feed.IsSpent`; `ChooseSeekLanding` → `feed.SeekLanding` | ~80 | a seek still lands on the video stream when there is one |
| D6 | `ReaderCore` and `SourceTail` → `Reader/MediaReader.cs` | ~350 | a pure move |

## Phase E — reconciliation per chain

| # | Step | Size | Note |
|---|---|---|---|
| E1 | `DiffSource` → `chain.Matches(desired)` per chain | ~90 | |
| E2 | `BuildVideoChain` → `VideoChain.Reconcile(resolver, desired, versions)` | ~120 | |
| E3 | `BuildAudioChain` → `AudioChain.Reconcile` | ~110 | |
| E4 | `BuildSubtitleChain` → `SubtitleChain.Reconcile` | ~140 | the most cross-chain dependencies in the file; depends on B5 |
| E5 | `ChainChanges` per chain; `ReconcileCore` dispatches and merges; `ReconcileResult` keeps its shape | ~100 | the pipeline no longer knows how a chain is built |

## Phase F — threads and lifecycle

| # | Step | Size |
|---|---|---|
| F1 | `PipelineThread`: name, stop flag, node, `SingleThreadSynchronizationContext` host, `Join` | ~90 |
| F2 | The video render thread uses it | ~30 |
| F3 | The other four threads use it | ~80 |
| F4 | `Start`/`StopAndJoin` per chain; `_stopRequested` and `_threadsStarted` disappear; `StopCore` iterates the chains | ~120 |

## Phase G — clock governance and ending

| # | Step | Size | Note |
|---|---|---|---|
| G1 | `ClockGovernor`: one owner (first live audio chain, else first video chain) anchors and alone corrects the clock; starvation is per chain and only the owner pauses playback; `StarvedFor` names the chain | ~120 | with one chain of each kind this reproduces today's behaviour exactly |
| G2 | `chain.ReportEnded(epoch)` replaces `NotifyVideoRenderEnded`/`NotifyAudioRenderEnded`; playback ends when every active video and audio chain has reported; subtitles never gate it | ~90 | same rule as today, stated per chain |

## Definition of done for v1

1. `PlaybackPipeline.cs` holds no decode or render loop, no queue, no component slot, and no thread field.
2. Chains live in `_chains` and are looked up by kind; no six-field-per-kind state remains.
3. The reader routes by chain, not by kind.
4. Threads, stop flags and wake nodes belong to chains.
5. The public surface of `PlaybackPipeline` is byte-for-byte what it is today; `src/SinePlayer` has no diff;
   `PlaybackPipelineTests` is green with no edits.
6. A guard test fails if a chain type starts appearing in a public signature (added in the same step that
   creates the first chain type).

## The next milestone (out of scope here)

Publish the model: chain collections, `AddVideoChain`/`AddAudioChain`/`AddSubtitleChain`, `RemoveChain`,
typed `SelectStream`, per-chain `ReconcileResult`, per-chain warnings, `PlaybackChainId`. Then per-chain
versions if they still look worth it, and the app layer rewritten against chains. Nothing in that milestone
requires reopening Phases A–G — which is the test of whether this plan did its job.
