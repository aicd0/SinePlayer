# PlaybackPipeline chains — v2 (multi-chain)

Status: Phases H and I done; the pipeline is now an orchestrator over chain lists with no per-kind state of its own. What is left of Phase I — `Add*Chain`/`RemoveChain`, a kind with no chain, and `Reset` losing its targets — is multi-chain API and therefore belongs with Phase J, which is not started. v1 (phases A–G) is complete: the pipeline was an orchestrator over three chain
objects, but only ever one of each kind. This plan publishes the model and makes a second (and third) chain of a
kind actually work.

Answers to the two questions that had to come first are sections 1 and 2; everything after them is the plan.

## 1. The shape of the API

Three principles, all of them already paid for in v1:

1. **A chain is an object.** Reference is identity — no `PlaybackChainId` (v1 removed the id it had started with,
   and nothing since has asked for one back: a chain lives as long as the pipeline, survives `Reconcile`, and is
   freed only by `RemoveChain`).
2. **Declaring a chain touches nothing that runs.** `Add`/`Remove`/`SelectStream` record intent; `Reconcile()` is
   what builds components, starts threads and joins the ones that are gone. This is v1's rhythm and the reason it
   could be verified by unchanged tests.
3. **No member assumes one chain of a kind.** Not even a convenience one: the pipeline exposes the chains of each
   kind as a list and nothing singular. A caller that wants "its" video chain holds the one `AddVideoChain`
   returned, which is what the app and every test do — the list is for enumerating, not for reaching a known chain.

### The pipeline

```csharp
public sealed class PlaybackPipeline : IDisposable
{
    // Chains — a list per kind, never one of a kind
    public IReadOnlyList<VideoChain> VideoChains { get; }
    public IReadOnlyList<AudioChain> AudioChains { get; }
    public IReadOnlyList<SubtitleChain> SubtitleChains { get; }

    public Task<OperationResult<VideoChain>> AddVideoChain(IVideoRenderTarget target);
    public Task<OperationResult<AudioChain>> AddAudioChain();
    public Task<OperationResult<SubtitleChain>> AddSubtitleChain(IVideoRenderTarget target);
    public Task<OperationResult> RemoveChain(PlaybackChain chain);

    // Sources and streams
    public Task<OperationResult<MediaSource>> AddSource(MediaLocator locator);
    public Task<OperationResult> SelectStream(PlaybackChain chain, MediaStreamInfo? stream);
    public Task<OperationResult<ReconcileResult>> Reconcile();

    // Transport — one clock, so one of each
    public Task<OperationResult> Play();
    public Task<OperationResult> Pause();
    public Task<OperationResult> Seek(TimeSpan position, SeekMode mode = SeekMode.Fast);
    public Task<OperationResult> Reset();    // stop everything, drop sources and components, keep the chains

    // State — the parts that really are pipeline-wide
    public MediaEngineState State { get; }
    public string? Error { get; }
    public TimeSpan Position { get; }
    public IReadOnlyList<MediaSource> Sources { get; }
    public event Action<MediaEngineState, string?>? StateChanged;
    public event Action<PlaybackChain?, string>? WarningRaised;   // null when it is not a chain's

    // Configuration — what is not a chain's
    public ISourceProviderFactory? SourceFactory { get; set; }
}
```

**A pipeline starts with no chains.** There is no default chain of any kind and no `Reset(targets)` that would
create one: the host adds the first chain it wants, and a pipeline with no chain has nothing to build, nothing to
play and no clock owner. `Play()` on it fails with "no chain has a stream" rather than pretending, `Reconcile()`
succeeds with three empty dictionaries, and `Position` stays where the timeline was left. Everything the chains
hold — components, targets, preferences — arrives with them, so a pipeline with no chain also holds no surface
and no resolution.

### The chains

A chain is reached through an interface of its kind. The chain types themselves are internal, exactly as in v1: a
host holds a `IVideoChain` and cannot reach its queues, nodes or coroutines, and the implementation can grow
without that becoming API.

```csharp
public interface IPlaybackChain
{
    MediaStreamKind Kind { get; }
    string? DecoderName { get; }
    string? RendererName { get; }
    bool IsHardwareAccelerated { get; }
}

public interface IPlaybackChain<out TStream> : IPlaybackChain where TStream : MediaStreamInfo
{
    TStream? SelectedStream { get; }   // what the host asked for
    TStream? PlayingStream { get; }    // what Reconcile built; null until reconciled
}

public interface IVideoChain : IPlaybackChain<VideoStreamInfo>
{
    IVideoRenderTarget? Target { get; }
    VideoComponentResolver Components { get; }   // this chain's own, created with the chain
    VideoTransform Transform { get; set; }
    ISynchronizer Synchronizer { get; set; }
    long PresentedFrameCount { get; }
    long DroppedFrameCount { get; }
    long DecodedFrameCount { get; }
    double AverageDecodeMilliseconds { get; }
    double AverageRenderMilliseconds { get; }
    TimeSpan PictureLag { get; }
    TimeSpan LastPresentedTimestamp { get; }
}

public interface IAudioChain : IPlaybackChain<AudioStreamInfo>
{
    AudioComponentResolver Components { get; }
    AudioFilterChain Filters { get; }
    long Underruns { get; }
    bool OwnsClock { get; }                    // the chain that anchors and corrects the clock
}

public interface ISubtitleChain : IPlaybackChain<SubtitleStreamInfo>
{
    IVideoRenderTarget? Target { get; }
    SubtitleComponentResolver Components { get; }
    SubtitlePresentation Presentation { get; set; }
}
```

`Target` is read-only here and fixed at `Add`, because a surface is not something a playing chain can swap; the
implementation keeps the setter for the pipeline to install it with. `Components` is read-only for the same reason:
the chain creates its resolver, and the host registers into it.

Publishing was a change of accessibility plus `Add`/`Remove` — not a move — exactly as v1 planned.
### `Reconcile()` answers per chain

```csharp
public sealed class ReconcileResult
{
    public IReadOnlyDictionary<IVideoChain, OperationResult> VideoResults { get; }
    public IReadOnlyDictionary<IAudioChain, OperationResult> AudioResults { get; }
    public IReadOnlyDictionary<ISubtitleChain, OperationResult> SubtitleResults { get; }
}
```

One entry per chain present at the time of the call, keyed by the chain itself (reference identity, which is what
a chain has instead of an id). The outer `OperationResult` still says whether the reconcile ran at all; the
entries say whether each chain got what it asked for. A dictionary rather than a list of pairs, so a caller that
holds one chain reads `result.VideoResults[chain]`; declared `IReadOnlyDictionary` because a result is not
something a caller should edit — the storage behind it is the plain dictionary either way.

### Component resolution belongs to the chain, entirely

Today resolution is the pipeline's: `BuildVideoChain(resolver, desired)` in
`PlaybackPipeline.Components.cs` offers every creator in registry order and the first one that works wins, and
`Preferences` is one value for the whole pipeline. `ComponentResolver` also answers every kind's questions, which
means a video chain can ask an audio one if it is handed the wrong object. With two chains of a kind that is wrong
in three ways — the secondary video chain may want software decoding while the main one uses the GPU, a chain
pointed at an offscreen target may want a different set of *factories*, not just a different preference, and the
questions are per kind anyway. So v2 moves the whole thing:

- **There is no pipeline-side registration.** `pipeline.Components` goes.
- **`ComponentResolver` splits into three**, one per kind: `VideoComponentResolver`, `AudioComponentResolver`,
  `SubtitleComponentResolver`. Each offers exactly the roles its kind has — video decoders and video renderers;
  audio decoders and audio renderers; subtitle decoders and subtitle renderers — with `Register`/`Remove` overloads
  typed to those roles, so registering an audio decoder into a video chain does not compile. Each has its own
  `Preferences` and its own `Clone()`. The shared part (a `ComponentRegistry` and what to do with it) is one
  internal helper they delegate to, not a public base class: there is no question a resolver of one kind can answer
  about another, so there is nothing for the three to share in public.
- **The chain owns one**, created with the chain: `VideoChain.Components`, `AudioChain.Components`,
  `SubtitleChain.Components`. It is settable, so a host can assign a preconfigured resolver to a chain it has just
  added; the property is the only channel, which is why `Add*Chain` takes no resolver.
- **Resolution moves into the chain**: the chain builds its own decoder and renderer from its own resolver,
  publishes its own slots and bumps its own `ChainVersion`. It works from a `Clone()` of its resolver taken at
  reconcile, so a registration that lands mid-reconcile is not seen half-applied.
- **The pipeline keeps only what is genuinely shared**: the published source holder, the epoch advance, and the
  decision to start threads.

The cost is that the host configures each chain — a registration loop over `VideoChains`/`AudioChains`/
`SubtitleChains` instead of one pipeline object, and a settings change re-applies the same way. That is the honest
shape: what a chain decodes with is that chain's business, and a pipeline-wide registry would be a second place to
say it.

`VideoPreferences` currently holds one knob (`PreferHardwareDecoding`) and only video decoder resolution reads it, so
it lives on the video resolver and the audio and subtitle resolvers have none. Per-chain preferences is a small
feature on its own; what it buys immediately is that the mechanism exists before the second chain of a kind
arrives, and the knob is the one that already means something per chain.

### What happens to today's kind-shaped members

| Today | v2 |
|---|---|
| `Reset(videoTarget, subtitleTarget)` | nothing: a pipeline starts with no chains and `Add*Chain` brings the target. A new surface is `RemoveChain` + `Add` |
| `Stop()` | `Reset()` — with the targets gone the two were the same call, and `Stop()`'s four test call sites become `Reset()` |
| `SelectStream(kind, stream)` | `SelectStream(chain, stream)` |
| `SelectedVideoStream` / `Audio` / `Subtitle` | `chain.SelectedStream` on the chain the caller holds |
| `VideoTransform`, `Synchronizer` | `VideoChain.Transform`, `VideoChain.Synchronizer` |
| `SubtitlePresentation` | `SubtitleChain.Presentation` |
| `AudioFilters` | `AudioChain.Filters` |
| `VideoDecoderName`, `IsVideoHardwareAccelerated`, frame counts, `PictureLag`, `AudioUnderruns` | the member of the same name on that kind's chain |
| `StarvedFor` | gone: it collapsed every chain into one string. `chain.Starved` says it per chain |
| `State`, `Error`, `Position`, `Sources` | unchanged on the pipeline — one clock, one state, one source set |
| `ReconcileResult.Video/Audio/Subtitle` | `ReconcileResult.VideoResults`/`AudioResults`/`SubtitleResults`, keyed by chain. The singular views `Video`/`Audio`/`Subtitle`/`For(kind)` are still transitional — they die with the last reader |
| `PlaybackPipeline.SelectStream(MediaStreamKind, …)` | **deleted**; a host maps the kind to a chain |
| `pipeline.Components` (every kind's registry and preferences) | `chain.Components`, one typed resolver per kind; `Register` takes only that kind's factories |
| `WarningRaised(string)` | `WarningRaised(PlaybackChain?, string)` |

Two deliberate losses. The flat property surface goes, so every per-chain number is read from the chain — that is
~150 mechanical edits in `PlaybackPipelineTests`, almost all inside the file's four helpers, and about a dozen in
the app. And the singular accessors go with it: no `pipeline.Video`, only `pipeline.VideoChains`. The reason to
resist a convenience accessor is that it has to pick one of a list and then say which — "primary" would be a rule
the caller cannot see, and the caller that cares already has the reference `AddVideoChain` handed back. Where the
engine itself needs "the one", it is named and local: the clock owner is the first audio chain, and a seek prefers
the first video chain.

### The app, after v2

```csharp
VideoChain video = (await pipeline.AddVideoChain(videoTarget)).Result;
video.Components.Register(new FFmpegVideoDecoderFactory());
video.Components.Register(new D3D11VideoRendererFactory());
video.Components.Preferences = video.Components.Preferences with { PreferHardwareDecoding = true };

SubtitleChain original = (await pipeline.AddSubtitleChain(subtitleTarget)).Result;
original.Components.Register(new FFmpegSubtitleDecoderFactory());
original.Components.Register(new Win2DSubtitleRendererFactory());

SubtitleChain translation = (await pipeline.AddSubtitleChain(translationTarget)).Result;
translation.Components = original.Components.Clone();   // same factories, its own preferences from here on

AudioChain audio = (await pipeline.AddAudioChain()).Result;
audio.Components.Register(new FFmpegAudioDecoderFactory());
audio.Components.Register(new WasapiAudioRendererFactory());

await pipeline.AddSource(locator);
await pipeline.SelectStream(video, file.Info.VideoStreams[0]);
await pipeline.SelectStream(original, file.Info.SubtitleStreams[0]);
await pipeline.SelectStream(translation, file.Info.SubtitleStreams[1]);
await pipeline.Reconcile();
await pipeline.Play();
```

## 2. Can subtitle renderers share one surface, and should they?

### What is true today

Two facts decide this, both in the code:

1. **A target is one swap chain.** `IVideoRenderTarget.AttachPresentationSurface(nint)` ends in
   `ISwapChainPanelNative::SetSwapChain` (`SwapChainPanelRenderTarget.TrySetSwapChain`), and a
   `SwapChainPanel` holds exactly one. A second attach silently replaces the first — the panel composes the newer
   chain and the older one keeps presenting into nothing.
2. **A subtitle surface owns its device.** `AssSubtitleRenderer` and `Win2DSubtitleRenderer` each construct their
   own `SubtitleSurface`, and `SubtitleSurface.EnsureCanvasDevice` calls `D3D11Device.Create()` — a *new*
   Direct3D device, not the video renderer's. So each subtitle chain today costs one D3D11 device + one
   `CanvasDevice`, plus a premultiplied BGRA swap chain of two buffers at the panel size.

So **sharing is not possible today**, and not only because of the swap chain: the renderers are built to own a
surface each.

### Three ways to put N subtitle chains on screen

| | VRAM for the second chain (1080p) | D3D11 devices | Composition planes | Coordination |
|---|---|---|---|---|
| **A. One target per chain** (a panel each) | 15.8 MiB (2 × 7.91 MiB buffers) | +1 | +1 alpha plane, +1 present per frame | none |
| **B. One target, one layer texture per chain, composited** | 7.91 MiB (one offscreen texture) | +0 | +0 | per-frame: clear, draw every layer, one present |
| **C. One target, chains draw straight into the back buffer** | 0 | +0 | +0 | impossible — no barrier between the chains' threads, so clear/draw/present cannot be ordered |

(Buffer sizes: 1920×1080×4 = 7.91 MiB; 2560×1440 = 14.06 MiB; 3840×2160 = 31.64 MiB; a 1080p panel at 150 %
scale = 17.8 MiB. A swap chain is two buffers.)

### What sharing actually saves

B against A saves **one swap chain's worth of VRAM minus one layer texture — about 7.9 MiB at 1080p**, which is
small next to what both share — plus one `D3D11Device` (low single-digit MB of driver memory, tens of
milliseconds to create, its own command queue) and one full-screen alpha plane in the compositor. It costs a
composite pass per frame and a present schedule that no single chain owns, because two render threads cannot
agree on when a frame is complete without one.

That is **not worth building for v2**. The two things worth taking from it:

- **Share the device.** One `D3D11Device` for every subtitle surface is a constructor argument instead of a
  `D3D11Device.Create()` inside `SubtitleSurface`. No coordination, no new API, and it removes the only
  *unexpected* per-chain cost (a whole driver instance).
- **Keep the target as the surface key.** Several chains given the same target reference are, by definition, the
  chains that would be composited together — so a compositor can be added later (option B) without changing the
  public API, and without the caller telling the pipeline anything new.

The trigger to build B is offscreen rendering — export, thumbnails, or a host that cannot stack a panel per chain
— or video picture-in-picture, which needs the same machinery for a much harder reason (scaling, z-order,
blending). Neither is in v2.

**Verdict: v2 gives each subtitle chain its own target and each video chain its own target, and shares the
Direct3D device behind the subtitle surfaces.** The app already stacks a subtitle panel over the video panel
today (`CoreEngine.CreateSubtitleSurface`, with its opacity caveat), so a second subtitle panel is the same
mechanism, one more plane.

## 3. Threads, clock and versions with N chains

### Threads — `1 + 2V + A + S`, unchanged from the v1 decision

| Thread | Count | Owner |
|---|---|---|
| `Pipeline - Reader` (reader + audio decode + subtitle decode) | 1 | `CoroutineHost`, held by the pipeline; chains register coroutines with it |
| `Pipeline - Video decode` | V | `VideoChain` |
| `Pipeline - Video render` | V | `VideoChain` |
| `Pipeline - Audio render` | A | `AudioChain` |
| `Pipeline - Subtitle render` | S | `SubtitleChain` |

Names gain an index only when there is more than one of a kind (`Pipeline - Video render 2`), so logs from a
single-chain session stay comparable with v1's.

v1 deferred one thing here: the chains' decode coroutines still live on a host the pipeline owns. v2 pays it —
`CoroutineHost` takes registrations after it has started, each registration carries its own stop flag and wake,
and a chain's `RequestStop` stops its own coroutine. That is the single-source-of-truth rule restored, and it is
what makes `RemoveChain` possible: the host must be able to lose a coroutine without being restarted.

### Clock — one owner

- The **owner** is the first audio chain still present; with no audio chain, the first video chain. `OwnsClock`
  publishes which.
- Only the owner anchors the clock, and only the owner corrects it from a device clock. A second audio chain
  renders and reports but never touches the clock — otherwise two devices would pull it in two directions.
- **Starvation**: any chain that is starving pauses the clock, owner or not. This is a safety rule, not a
  governance one: with one chain of each kind it is exactly today's behaviour, and the test that depends on it
  (`HoldsTheClockBackUntilThePictureHasSomethingToShow`) keeps passing. `StarvedFor` is gone; `chain.Starved` and
  `OwnsClock` say the same thing without collapsing the list into one name.

### Versions — per chain

`PipelineVersion` carries all seven fields and cannot express a second video chain. v2 replaces it with a
per-chain structure:

- `ComponentSlot<T>` holds one `long` instead of a `PipelineVersion`, and its `IsBehind` guard compares that one
  number — the change that makes per-chain allocation safe.
- A chain owns a counter and publishes `ChainVersion(long Source, long Decoder, long Renderer)`; each slot takes
  its number from the chain's counter.
- A packet is stamped when the chain *accepts* it, with the chain's current versions — the reader stamps only the
  source version. A packet queued before that chain's own switch therefore looks stale to it, and a packet queued
  before *another* chain's switch does not.

`BumpVersions` and `CurrentVersions` disappear.

### Deliberately not in v2

- **Per-chain audio endpoints.** `IAudioRenderer` has no device parameter, so a second audio chain plays on the
  default endpoint and WASAPI mixes. Fine, and worth stating in the docs rather than discovering.
- **Video compositing** (picture-in-picture), subtitle layer compositing, playback rate.

### One packet, several chains

Two chains may select the same stream, so the reader's fan-out changes: today it hands each packet to the first
feed that accepts it and stops; in v2 it hands it to *every* accepting feed. Each chain has its own decoder, so
each needs its own copy of the compressed payload, and the copy has to be cheap because it happens per packet:

- `IMediaBuffer` gains `Retain()`: another buffer over the same payload, released independently. It has a default
  implementation returning `null` — "this payload cannot be shared" — so existing implementations keep compiling
  and the ones that can share opt in. `FFmpegPacketBuffer` opts in with `av_packet_ref`, which is a reference
  count on the `AVBufferRef`, not a copy of the data.
- `MediaPacket.Retain()` follows from it, and the reader retains once per consumer after the first and disposes its
  own reference at the end.
- A buffer that returns `null` sends the packet to the first accepting chain only, and the reader posts one warning
  per source saying so. Silent starvation of a duplicate chain would be far worse than a warning.
- Every accepting feed reports its tail, so read-ahead is still driven by the slowest consumer.

This is also why the same-stream case is cheap for the *sources*: one demuxer position, one read, one packet
refcount per extra chain — not a second `IMediaSource` per chain.

## 4. Phases

Invariants, as in v1: one coherent change per step; a step that moves code changes no condition; a step that
changes behaviour says so; build with MSBuild x64; `PlaybackPipelineTests` green at the end of every phase.
Tests are run when asked.

### Phase H — plural underneath, single chain above

H ends with the app untouched and every v1 test passing **with no edits**. Nothing in this phase is visible from
outside.

| # | Step | Lands as | Review focus |
|---|---|---|---|
| H1 | The selection stops being a triple | `MediaSourceHolder` deleted; each chain carries `Selected` (what the host asked for) beside a `Source` component slot (what it is bound to), published by the builder that owns it and installed by the reader; `CreateSourceHolderFromChange`/`FromSelection` deleted | `SelectedVideoStream` still reports the same thing |
| H2 | `ChainVersion` introduced; the video comparisons move to it | `ChainVersion`, `ComponentSlot` keeps both for one step | old packets still dropped after a switch |
| H3 | Audio comparisons moved | | as above |
| H4 | Subtitle comparisons moved; `PipelineVersion`, `BumpVersions`, `CurrentVersions` deleted; slots hold one `long` | `ComponentSlot<T>` | a stream switch still drops queued packets and keeps the new ones |
| H6 | `CoroutineHost` accepts registrations after start; each chain registers and stops its own decode coroutine | `CoroutineHost.Register`, `IChainFeed` gains `RequestStopCoroutine` | stop still joins, and a chain that ends does not take the host down |
| H7 | The clock's owner is decided, not raced: `PipelineTimeline.DeviceOwner` names the chain whose device may correct the clock, and `SyncClock` refuses any other chain. Anchor stays first-come-per-epoch and starvation still pauses the clock from any chain (the video-hold tests depend on it) | `DeviceOwner`; `SyncClock(chain, …)`; the governor type and `OwnsClock` wait until two chains of a kind exist, when there is something to observe | with one chain of each kind, the clock moves exactly as before |
| H8 | Chains become wake targets | `IPipelineWakeTarget` on the chains; `_wakeTargets` = reader + chains | no wake lost; the four direct node wakes go through the chain |

### Phase I — publish the model

| # | Step | Lands as | Review focus |
|---|---|---|---|
| I1 | `IPlaybackChain` published; the chains themselves stay internal; machinery internal | `IPlaybackChain` (4 members: `Kind`, `DecoderName`, `RendererName`, `IsHardwareAccelerated` — `IsActive` was dropped in J4 as unused and a duplicate of the feed's `IsConsuming`) plus `IPlaybackChain<TStream>` (`SelectedStream`, `PlayingStream`), typed by kind in I6a, `ChainVersion`, `ChainTail`, `PipelineCoroutine` | what is public is exactly the table in section 1; every `IPlaybackChain` implementor is invisible |
| I2 | Resolution moves onto the chains (`chain.Reconcile()`), and `ComponentResolver` splits into `VideoComponentResolver` / `AudioComponentResolver` / `SubtitleComponentResolver` — one per kind, each owning only its kind's roles | `PlaybackPipeline.Components.cs` deleted; `ChainChange` and `ComponentResolution` extracted; each `Reconcile` takes its own kind's resolver, so a video chain cannot ask for an audio factory. The type is now `VideoPreferences` and hangs off the video resolver; audio and subtitle resolution never read it. `ComponentResolver` survives **only as the host's registration surface** and dies in I3, when `chain.Components` becomes the channel | a resolver of one kind cannot be asked another kind's question; a role registered into the wrong chain does not compile |
| I3a | The three kind interfaces are published: `IVideoChain` / `IAudioChain` / `ISubtitleChain : IPlaybackChain<XxxStreamInfo>` (the stream type was added to the base in I6a), the chain types stay internal | each chain implements its kind's interface with the members it already has; nothing moves and nothing is constructed differently | a host can name a chain of a kind without naming its implementation; the guard test lists exactly three chain interfaces |
| I3b | A chain owns its resolver (`chain.Components`), so `Reconcile()` takes no resolver, the aggregate `ComponentResolver` is deleted, and the pipeline forwards `VideoComponents` / `AudioComponents` / `SubtitleComponents` to the three chains' own resolvers | each chain creates a `Video`/`Audio`/`SubtitleComponentResolver` with its own registry, so a factory can only ever reach the kind it belongs to; the app's `SetFactories<TFactory>` becomes six typed `Set*` helpers and the lab page's generic picker handler routes by role. The three forwarding properties are transitional — they die with the kind-shaped members | two chains cannot be built from one registry; nothing in the app names a chain type |
| I3b2 | The pipeline holds `VideoChains` / `AudioChains` / `SubtitleChains` (one array per kind, `IReadOnlyList<IXxxChain>` outside) instead of three fields; the constructor adds its three chains through the same `Add*ChainCore` path that `Add*Chain` will expose, and start/stop/reset walk the lists. `chain.Components` joins the three chain interfaces | the fields became `Video`/`Audio`/`Subtitle` = the first chain of its kind, which is what the pipeline-wide members still describe | a fresh pipeline holds one chain of each kind, and the pipeline's resolver is that chain's own |
| I3b3 | **Folded into I3c2b, moved to Phase J**: publishing `Add*Chain` is multi-chain API, and the internal `Add*ChainCore` path it would have published is the one the constructor already uses | nothing to publish while v1 holds one chain of each kind | a chain is added, reconciled and removed through one path |
| I3c1 | The pipeline stops describing "the first chain of each kind": `Video`/`Audio`/`Subtitle` are deleted and every walk is over the lists — start/stop/join/reset, the playable-track check, the selected sources a seek checks, the furthest read tail, starvation, the ending check, `ChooseSeekLanding`, and the three surface/clock wake paths. `SubtitleChain`/`VideoChain` receive themselves in the surface-size callback, so a resized surface wakes its own picture and what is laid out against it | a chain is now the unit of every question the pipeline asks, which is what a second chain of a kind needs; four `[0]` remain — the resolver forwarders, `FirstChainOf`, the clock owner and the layout source — and they are the only places that still assume "one per kind" | behaviour is unchanged with one chain per kind: 533 tests, no assertion touched |
| I3c2a | Registration moves off the pipeline: `VideoComponents`/`AudioComponents`/`SubtitleComponents` are deleted and the host registers into `chain.Components`, which every chain interface already exposes | 49 mechanical sites (app 26, tests 36 minus the lambdas' shared lines); the guard that pinned "the pipeline's resolver *is* the chain's" is deleted, because there is no second resolver to compare, and the per-kind guard now allows only the three chain lists | a registration surface that is not a chain is gone |
| I3c2b | **Moved to Phase J.** `Add*Chain`/`RemoveChain`, a kind with no chain, `Reset` losing its targets, and the constructor creating nothing are one indivisible change: publishing `Add` while the constructor already creates a chain of every kind would make `Add` fail on a fresh pipeline, and `Remove` is meaningless unless a kind may hold none | The cap option cannot be published: with at most one chain per kind, `Add` can only fail and `Remove` can only be refused. And none of it is needed for v1, whose contract is "one of each kind" — it is multi-chain API, so it lands with Phase J (J0) | Phase I leaves the pipeline with exactly one reachable path: the constructor's three chains |
| I4a | `SelectStream(chain, stream)`; the kind overload survived I4 as transitional and is **deleted now** — the pipeline has no member that takes a `MediaStreamKind` | a host that speaks in kinds maps the kind to a chain itself: `CoreEngine` and the Lab page switch on the kind to the chain they hold or the first in the list, and the tests have one `ChainOf(pipeline, kind)` helper | the stream is checked against the chain's kind, and a chain that is not this pipeline's is refused |
| I4b | `ReconcileResult` is three dictionaries keyed by the chain; `Video`/`Audio`/`Subtitle`/`For(kind)` survive as transitional views over them | the dictionaries are the only state a result has, so the kind-shaped views cannot disagree with them; each is `Single` over its dictionary and throws once a kind holds two chains, which is the reminder that Phase J cannot come before I6 | a failure is looked up by the chain that failed, and one chain's failure says nothing about another's |
| I4c | Warnings carry the chain: `WarningRaised` is `Action<IPlaybackChain?, string>` and every warning a chain raises is attributed to it; null means the pipeline itself (a seek that could not reach the sources) | the attribution is the call site's, not a lookup: a chain passes `this`, so nothing has to guess which chain a message came from | a warning names the chain it came from |
| I5 | **Moved to Phase J** with I3c2b: `Reset()` can only lose its targets once `Add` carries them | `Stop()` stays as it is for v1, since it already means "reset with no target" | no change in v1 |
| I6a | The video-shaped members leave the pipeline: `SelectedVideoStream`, `VideoDecoderName`/`VideoRendererName`, `IsVideoHardwareAccelerated`, `PresentedFrameCount`/`DroppedFrameCount`, `AverageVideoDecodeMilliseconds`/`AverageVideoRenderMilliseconds`, `PictureLag`, `LastPresentedTimestamp`, `Synchronizer`. Every one of them already existed on `IVideoChain`/`IPlaybackChain`, so nothing had to be invented | 63 test reads became `pipeline.VideoChains[0].…` with the interface's own names (`SelectedStream`, `RendererName`, `AverageDecodeMilliseconds`); the app's single read surface `PlaybackInfoHandle` gained one private `Video => _pipeline?.VideoChains.FirstOrDefault()` and the rest of the app kept its shape | **no assertion changes**; the app still speaks in its own property names, and only one file knows about "the first video chain" |
| I6b | The audio-shaped members leave: `SelectedAudioStream`, `AudioDecoderName`/`AudioRendererName`, `IsAudioHardwareAccelerated`, `AudioUnderruns`, `AudioFilters` | same shape as I6a, over `IAudioChain`: 32 test reads became `pipeline.AudioChains[0].…`, `PlaybackInfoHandle` gained `Audio => _pipeline?.AudioChains.FirstOrDefault()`, and the two places that add filters (`CoreEngine`, `PlaybackLabPage`) say `pipeline.AudioChains[0].Filters.Add(…)` — the chain that will run them | as I6a |
| I6c | The subtitle-shaped members leave: `SelectedSubtitleStream`, `SubtitleDecoderName`/`SubtitleRendererName` | same shape, over `ISubtitleChain`: 5 test reads and the Lab picker became `…SubtitleChains[0].…`, and `PlaybackInfoHandle` gained `Subtitle => _pipeline?.SubtitleChains.FirstOrDefault()` | as I6a |
| I6d | `VideoTransform` and `SubtitlePresentation` move to their chains: `IVideoChain.Transform` and `ISubtitleChain.Presentation`, each a plain property whose setter wakes its own render node. The cross-chain part — a picture that moved takes its words with it — becomes `VideoChain.LayoutChanged`, installed by the pipeline, which wakes every subtitle chain whose `LayoutSource` is that chain | the chain knows nothing about subtitles: it raises "my layout changed" and the pipeline decides who listens, the same way `SurfaceSizeChanged` already works. The cells stay internal (`TransformCell`, `PresentationCell`), so `AtomicCell` never becomes API | setting the transform still repaints the words, and now repaints only the words laid out against *that* picture. `StarvedFor` stays on the pipeline: starvation is the clock's question, and the clock is the pipeline's — it is revisited when Phase J lifts the one-chain-per-kind cap |
| I7 | The app ported — done as part of I6a–I6d and I3c2a: `PlaybackInfoHandle` holds the three chains, `CoreEngine`/`PlaybackLabPage`/`PlaybackComponents` read statistics, transform, presentation and resolvers from them | one file (`PlaybackInfoHandle`) knows about "the first chain of a kind"; everywhere else the app speaks its own vocabulary | the app never mentions an internal type |
| I8 | Guard tests extended: the published types are exactly the chain interfaces and the pipeline; every chain implementor is invisible; each kind interface declares its own stream type; and the pipeline's only per-kind members are `*Chains` and `*Components` | `PipelineSurfaceTests` (6 guards) | a per-kind member on the pipeline, or a chain type that becomes visible, fails here instead of quietly becoming API |

### Phase J — two of a kind

Each step adds tests rather than moving code; this is the phase that finds the bugs the first two did not.

| # | Step | Answers |
|---|---|---|
| J0 | `AddVideoChain(target)` / `AddAudioChain()` / `AddSubtitleChain(target)` / `RemoveChain` published; the constructor creates nothing, so a kind may hold zero chains; `Reset()` and `Stop()` no longer take targets, because a surface belongs to the chain from `Add`; the first audio chain added takes the clock, and removing it hands the clock on | `SelectStream(kind, …)` fails cleanly when the kind has no chain; every walk already ran over the lists; the app and the test fixture add their own chains, and a test's surface is chosen at `Add` rather than at open | a fresh pipeline has none: `Reconcile()` gives three empty dictionaries, `Play()` fails with "no track is selected", `Position` stays where the timeline was. `Add` and `Remove` are queued commands, and like `SelectStream` they only record intent — see J4 |
| J1 | Two subtitle chains, two targets — **done**: `DrawsEachSubtitleChainOnItsOwnSurface`. Each chain draws into the surface it was made with, shows only its own cues, and keeps its own presentation; the pipeline had no audio chain at all, which is the zero-chain case in use. Both chains sit on **different sources**: one stream can only feed one chain until packets are shared (J5), so sharing a stream stays J5/J6 | both draw, each with its own cues, presentation and layout source |
| J2 | Two audio chains — **done**: `PlaysTwoAudioChainsOfWhichOnlyTheFirstKeepsTheClock`. `IAudioChain` gained `OwnsClock` (it was in the sketch); the first audio chain added takes the clock and keeps it, a second only plays. Each chain hands its own samples to its own device, filters are per chain, and one device jamming stops only the chain in front of it | `OwnsClock` is read as `_timeline.DeviceOwner == this`, so ownership is the timeline's answer rather than a second flag to keep in step. Both chains sit on different sources, for the J1 reason | a starving chain is the one whose device is stuck, and it does not take the other chain's picture or sound with it |
| J3 | Two video chains, two targets — **done**: `PlaysTwoPicturesOnTheirOwnSurfaces` (each chain decodes and presents into its own surface; pointing one chain at another file rebuilds only that chain, the other keeps drawing and playback never pauses) and `GivesEachVideoChainItsOwnDecoderPreference` (`PreferHardwareDecoding = false` on one chain's resolver means that chain never asks for hardware at all, while the other tries the machine's decoder first) | the preference is asked of the chain's own resolver, so "hardware or not" is answered per chain and the machine-dependent part is never asserted; switching a stream is `SelectStream` + `Reconcile` on the one chain | two chains of a kind differ where their chains differ — stream, surface, decoder, and what they draw — and a rebuild of one is invisible to the other |
| J4 | `RemoveChain` while playing, including the clock owner — **done**: `RemovesTheChainThatKeepsTheClockWhileTheOtherPlaysOn` and `LaysTheWordsOutAgainstTheNextPictureWhenTheirsIsRemoved` | Principle 2 holds down to the reader and the clock: `Add` appends to the list and nothing else, `Remove` asks that chain to stop and drops it from the list, and **`Reconcile()` is the only place that changes what runs** — `FinishRemovals` joins the gone chains and hands the words to the picture that is left, `InstallSources` gives the reader a row for every chain it should feed, `ReconcileClockOwner` keeps the clock with a sound that is still here, and `StartThreads` starts the reader once and every held chain that is not already running (the chain answers for itself: `IsRunning` is its own threads, so the pipeline keeps no shadow list). Both commands are queued, so a removal cannot race the reconcile that acts on it, and the lists need no lock because the queue is their only writer | a command records intent; the reconcile is the single place that joins, installs, owns and starts |
| J5 | A packet fans out to every accepting chain | `IMediaBuffer.Retain()`, `MediaPacket.Retain()`, `av_packet_ref`; one warning per source when a buffer cannot share |
| J6 | Two chains on the same stream | both decode the same payload with their own decoder, both keep their read-ahead, and removing one leaves the other untouched |
| J7 | N-chain seek, end and read-ahead | `ChooseSeekLanding` prefers the first video chain; playback ends when every active video/audio chain has reported; the reader reads enough for every chain on a source |
| J8 | Threads are a resource — **done**: `HandsBackEveryThreadItTook` and `StopsAChainRemovedButNeverReconciled`, plus a `[SetUp]`/`[TearDown]` pair that counts `PipelineThread.Live` around **every** test in the fixture | `PipelineThread.Live` counts pipeline threads from the moment a thread begins to the moment its body returns, so "a removed chain costs no thread" and "dispose hands every thread back" are assertions rather than hopes; a chain removed and never reconciled is still stopped by the pipeline's stop path. The fixture counts the threads running before each test and fails it if any of the threads it started is still there afterwards, so a leak in a test that is about something else is caught where it happens rather than by a profiler | six threads for video + audio + two sets of words (reader 1, picture 2, sound 1, words 1 each), five after removing one subtitle chain, baseline after dispose; the removal tests count mid-test as well: six to four when a picture goes, three to two when a sound goes |

### Phase K — resources

| # | Step | Why |
|---|---|---|
| K1 | One `D3D11Device` shared by all subtitle surfaces | removes a driver instance per extra chain (section 2); no API change |
| K2 | Subtitle layer compositor, only if the host needs one panel per set of chains | option B in section 2; the target is already the surface key |

## 5. Definition of done for v2

1. A pipeline starts with no chains, and any number of chains of any kind can be added and removed while stopped
   or playing; each has its own threads, queues, slots, versions, resolver and target.
2. `PlaybackPipeline` holds no per-kind state and no member that assumes one chain of a kind: a list per kind,
   transport, and the state that is genuinely pipeline-wide. No component registration either — that is each
   chain's.
3. No internal type appears in the public surface, and the guard test says so.
4. The app runs on the plural API, including two subtitle chains on two targets.
5. New tests cover two chains of each kind, two chains on one stream, add/remove (including the clock owner), the
   clock with two audio chains, and two chains of one kind resolving components under different preferences.
6. Single-chain behaviour is unchanged: every v1 test passes having changed only its helpers.

## 6. Decisions taken before implementation

- No kind-shaped property and no singular accessor survives; the pipeline hands out a list per kind.
- `Stop()` folds into `Reset()`.
- `ReconcileResult` is three dictionaries keyed by chain.
- The whole component resolution — registry, preferences and the resolution itself — belongs to the chain;
  `ComponentResolver` is split per kind, nothing is registered on the pipeline, and `Add*Chain` takes no resolver
  because `chain.Components` is the channel.
- A chain's target is fixed at `Add`; a new surface is `Remove` + `Add`.
- Two chains may select the same stream, so packet buffers are retained rather than refused.
- The pipeline starts with no chains; the host adds the first one.

Left to settle while implementing, not blocking a start:

1. `Play()` with no chain: fail, or wait until a chain has a stream? Recommended: fail with the message, because a
   silent no-op hides a host mistake.
2. Whether `chain.Components = other.Components.Clone()` is the idiomatic way to copy a configuration, and therefore
   whether `Clone()` stays public.
3. `ReconcileResult` uses `IReadOnlyDictionary` rather than `Dictionary`. Recommended: keep it read-only.

