# Playback structure redesign

Redesigning what `SinePlayer.Core.Playback` exposes, and how the engine gets at what it needs, without
changing behaviour. Two problems are addressed together because they share one cause:

1. **Too much is public.** Types that only the engine uses are exported, either because a public member
   mentions them or because a public interface inherits them.
2. **The engine identifies its own objects at runtime.** Because one object answers to two interfaces — a
   public view for the host and an internal view for the engine — every host-supplied value has to be
   type-tested before the engine may use it.

## 1. What is wrong today

`SinePlayer.Core.Playback` exports 125 public types. The app never names about half of them. Only the
test assembly is granted internals (`InternalsVisibleTo` in `SinePlayer.Core.csproj`), and no XAML
`x:DataType`, no `SinePlayer.SDK` reference and no reflection over these types exist — so neither tests
nor tooling are a reason for anything to be public.

### 1.1 Three mechanisms

**Hierarchy-induced.** A public member forces a public type, and that type drags its dependencies out.

- `IPictureFrameSource` is public only because `IVideoChain` inherits it. Its single member,
  `SubtitleFrame`, is read in one place inside the engine (`SubtitleChain`) and nowhere in the app.
- `ReconcileResult` is public because `Reconcile()` returns it, but it is shaped like an engine record:
  `required init` dictionaries of `OperationResult` keyed by chain interfaces.
- `AudioComponentResolver`, `VideoComponentResolver`, `SubtitleComponentResolver` and `VideoPreferences`
  are public because the app does `chain.Components.Register(...)` and `.Preferences = ...` — legitimate,
  but only a fraction of each type is meant for the host.

**Public leaves nobody names.** `AudioFilterChainSnapshot` is a public type with an `internal`
constructor, `internal Empty` and `internal TryApply`: readable by hosts, creatable by none.
`AudioFilterChain.Snapshot()` and `.Version` exist only for the engine. The same shape applies to the
concrete backends whose factory is the only public need (`FFmpegSource`, `FFmpegVideoDecoder`,
`FFmpegAudioDecoder`, `FFmpegSubtitleDecoder`, `WasapiAudioRenderer`, `AssSubtitleRenderer`,
`Win2DSubtitleRenderer`, the `Null*` implementations), plus `ComponentRegistry` and `DefaultSynchronizer`.

**Engine contracts that look like extension points.** `IMediaDecoder`, `IVideoDecoder`, `IAudioDecoder`,
`ISubtitleDecoder`, `IAudioRenderer`, `IVideoRenderer`, `IAudioSubmit`, `IPlaybackClock`, `IMediaBuffer`,
`RawAudioBuffer`, `RawVideoFrame`, `MediaPacket`, `DecoderOptions`, `ISynchronizer` and the colour types
are public because public filter and renderer contracts mention them. The host implements none of them in
this repository.

### 1.2 Runtime type tests

All in `PlaybackPipeline.cs` except the last:

| Site | Code | Why it is needed today |
| --- | --- | --- |
| 148 | `chain is IChainFeed feed ? feed.Offset : TimeSpan.Zero` | `Offset` is not on `IPlaybackChain` |
| 255–257, 330–332 | `chain is VideoChain v && Array.IndexOf(_videoChains, v) >= 0 \|\| …` | membership tested by type, three kinds at a time, twice |
| 333 | `chain is not IChainFeed feed` | the same dual interface |
| 519 | `(IChainFeed)chain` | downcast from the public view |
| 558 | `subtitle.LayoutVideoChain is not VideoChain picture` | a public property holds a view, the engine needs the implementation |
| 567 | `_timeline.DeviceOwner is AudioChain owner` | the same |
| `VideoChain.cs` 368 | `(VideoStreamInfo)stream` | discards the typed binding the chain already holds |

The root cause is one: an object implements a public interface and an internal one, so a value that
arrives through the public one must be tested before the engine may use it.

## 2. Rules

- **R1 — public only if the host compiles against it.** "The tests need it" is not a reason; they have
  internals.
- **R2 — a public interface never inherits or returns an internal one.** Where the engine needs a wider
  view, it uses its own static type (§2 R7), not a side interface on the host's object.
- **R3 — data crossing the boundary is an immutable public record of public types.**
- **R4 — engine contracts the host may not implement are internal**, even when they look like extension
  points.
- **R5 — one contract per object, no side interfaces.** A value has exactly one static type at each
  boundary. The engine's view extends the host's (`IChainFeed : IPlaybackChain`) instead of being a
  sibling of it, so a chain registered once as the engine's view never has to be tested or cast again.
- **R6 — no runtime type tests in the engine.** A type test or downcast is allowed only at a trust
  boundary, at most once per value, never in a loop or an accessor. Membership is an index lookup;
  choosing a kind is one interface call; a public property that needs the implementation resolves it
  once, in its setter.
- **R7 — the engine's shared surface is an interface, not a base class.** The chains share an engine
  contract by implementing `IChainFeed`; there is no common implementation type, so nothing inherits
  state by accident and a chain keeps full control of its own fields.

## 3. Target shape

Two rings, in one assembly, in different namespaces:

- **`Playback.Hosting` (public)** — what a host must compile against: `PlaybackPipeline`, the chain
  interfaces, `MediaEngineState`, `SeekMode`, a slim reconcile report, component selection, the values the
  host passes in (`IVideoRenderTarget`, `VideoTransform`, `SubtitlePresentation`, `SubtitleFrame`,
  orientations and modes, stream/source info, `MediaLocator`, `OperationResult`), the filter surface it
  attaches (`AudioFilterChain` = `Add`/`Remove`/`Clear`/`Count`/`Offset`) and the factories it registers.
- **`Playback.Engine` (internal)** — chain implementations, reader, timeline, gate, queues, coroutines,
  backend classes, and every decoder, renderer, clock and buffer contract.

### 3.1 The engine's view of a chain

Interfaces only — there is no common base class, so a chain keeps full control of its own fields and
nothing is inherited by accident.

```csharp
// Public: what a host holds.
public interface IPlaybackChain
{
    MediaStreamKind Kind { get; }
    TimeSpan Offset { get; set; }    // step 3: the host's whole side of a shift; the engine is told when it changes
    string? DecoderName { get; }
    string? RendererName { get; }
    bool IsHardwareAccelerated { get; }
}

// Internal: the same object, seen by the engine. It extends the public interface, so one registration
// is enough for every later use — nothing has to be tested to find out whether a chain is one of these.
internal interface IChainFeed : IPlaybackChain
{
    ChainTail ReadTail { get; }
    bool IsConsuming { get; }
    void Accept(MediaStreamInfo stream, MediaPacket packet, ChainVersion versions, long epoch);
    void SignalEnd(ChainVersion versions, long epoch);
    void Select(StreamBinding<MediaStreamInfo>? binding);   // was the 3-way `is` cascade; null is "nothing chosen"
}
```

A shift is the host's to set — `chain.Offset = value` — and the chain is built with a callback that tells
the pipeline a shift changed, so the pipeline only has to ask the reader to look. There is no
`PlaybackPipeline.SetOffset`/`GetOffset`: the entrance is the property, and the pipeline keeps no opinion
about what is allowed, only the consequence.

```csharp
// The pipeline, once, at the boundary:
private readonly AtomicCell<IReadOnlyDictionary<IPlaybackChain, IChainFeed>> _feeds = new(…);

private bool TryGetFeed(IPlaybackChain chain, [NotNullWhen(true)] out IChainFeed? feed)
    => _feeds.Read().TryGetValue(chain, out feed);
```

The registry is written when a chain is built and pruned when it is removed, so the lookup is the only
place ownership is decided. Per-site outcomes: 148 becomes one lookup (and, after step 3, disappears);
255–257/330–333 become that same lookup plus `feed.Select(...)`; 519 gone, because `_removedChains` holds
the engine's view already; 558 becomes a registry lookup plus `feed.Kind`; 567 gone by typing the clock
owner as `AudioChain?`; `VideoChain` 368 uses `Selected.Stream`.

## 4. Deriving the surface mechanically

Not by guessing:

1. **Baseline.** A test compares `typeof(PlaybackPipeline).Assembly.GetExportedTypes()` against a
   checked-in list. It fails on any addition or removal, so every later step is reviewed as a diff of
   that list.
2. **Flip.** Script every type in `Playback` to `internal`, then compile: Core alone first (must succeed),
   then the app. Each `CS0122`, `CS0050`, `CS0051`, `CS0053` or `CS0055` names exactly one type or member
   that has to stay public — re-publicise only those and repeat until the app builds. Tests keep building
   through `InternalsVisibleTo`, so they never constrain the answer.
3. **Review the residue by hand**: anything re-publicised only to satisfy a member that should itself be
   internal is where `Snapshot()`, `ReconcileResult` and `IPictureFrameSource` get caught.
4. **Lock** with the baseline test.

## 5. Steps

Each builds, changes no behaviour beyond what it states, and is reviewable on its own.

1. **Guardrail.** `PublicSurfaceTests` and its checked-in baseline (§4.1). No production change.
2. **One engine view.** `IChainFeed` extends `IPlaybackChain` and gains `Select`; the pipeline keeps a
   registry of the chains it built, and every host-supplied chain is resolved through it once — replacing
   the four `is` cascades and the cast, and the three-way select switch.
3. **Offset on the interface.** `IPlaybackChain.Offset`, so reading an offset needs no lookup at all;
   `LayoutVideoChain` setter resolves the frame source once; clock owner typed; remove the
   `VideoStreamInfo` downcast.
4. **Demote leaves.** Concrete backends, `AudioFilterChainSnapshot`, `Snapshot()`/`Version`,
   `ComponentRegistry`, `DefaultSynchronizer`.
5. **Reconcile report.** `ReconcileResult` becomes one map keyed by the chain a host holds, plus an indexer,
   and `ResultOf(chain)` stops being a host-side type test.
6. **Layout and docs.** Move into `Hosting`/`Engine` folders and namespaces, record R1–R7.

Order rationale: steps 2–3 come before the accessibility work because they define the engine's view of a
chain, so step 4 has less to cascade through. They also come before it because they are the steps whose
absence is invisible in review: a type test works, so nothing complains.

## 6. Open questions

- `IVideoFilter`, `IVideoFilterFactory`, `IMediaFilter` and `IMediaDecoder` are unused in this repository.
  If out-of-tree plugins implement them, step 4 must keep them public — this changes the outcome for the
  whole filter and decoder ring, so it is worth confirming first.
- Step 5 is the one place where "no behaviour change" needs a small API change rather than an
  accessibility flip, because the app reads `ReconcileResult` (`PlayerSession`).
- A source-scanning guard for R6 (counting `is`/`as`/downcasts) is deliberately not automated in step 1:
  it would have to read the source tree from a test. R6 is a review rule until a cheap check exists.
