# Media Engine Redesign

Status: **Design proposal** · Scope: architecture and extension points only (no implementation)

---

## 1. Summary

The current playback stack is a thin wrapper around the platform media player
(`Windows.Media.Playback.MediaPlayer`). It exposes a single `IMediaEngine` façade with one
hardcoded implementation and cannot be decomposed. This document proposes a **composable,
layered media pipeline** in which every stage — source, demux, decode, post-processing, and
render — is an isolated, replaceable component selected through a registry. The goal is a
structure that makes the seven required scenarios (decoder/renderer/effect/track switching and
playback-info queries) *ordinary configuration*, not special cases.

This document deliberately stays at the level of **components, contracts, and extension
points**. It does not specify concrete decoding/rendering implementations or backend libraries.

---

## 2. Current state and why it does not scale

The engine was replaced, and this section is kept as the record of what it was like before — it is the
reason the design looks the way it does.

Then:

- [`IMediaEngine`](../src/SinePlayer/Common/Playback/IMediaEngine.cs) mixes transport control,
  media I/O, rendering, and lifecycle into one interface.
- [`NativeMediaEngine`](../src/SinePlayer/Common/Playback/NativeMediaEngine.cs) was the only
  implementation and delegated everything to `MediaPlayer`; the pipeline inside it was opaque.
- [`MediaEngineService`](../src/SinePlayer/Common/Playback/MediaEngineService.cs) constructed
  `NativeMediaEngine` directly, so there was no factory/registry indirection.
- [`PlayerPageViewModel`](../src/SinePlayer/Views/Pages/Player/PlayerPageViewModel.cs) is bound
  to this façade, including a `CreateVideoSurface()` call that hardcodes the WinUI rendering
  host.

Consequences against the required scenarios:

| Requirement | Blocker then |
|---|---|
| Switch video/audio decoder | `MediaPlayer` owns decoder selection; only a coarse `MediaSource` / platform preference is reachable |
| Switch video/audio renderer | Rendering is `MediaPlayerElement`; no seam between decode output and presentation |
| Post-effects (rotation/scale/color) in high performance | No frame-level access; effects can only be faked on the UI element (CPU/composition cost, quality loss) |
| Select video/audio/subtitle channels | No track enumeration or switching API |
| Query playback info (codec, bitrate, sample rate, FPS) | Properties are not surfaced; `NaturalDuration` is essentially the only signal |

The root cause was a missing **stage boundary**: there was nowhere to insert or swap a component
because everything was fused behind one sealed object. It has since been replaced: the engine draws into a
swap chain it owns instead of handing the picture to a XAML element, and the façade above is an adapter
over [`PlaybackPipeline`](../src/SinePlayer.Core/Playback/PlaybackPipeline.cs) — which is where the seams
the remaining scenarios build on now live.

---

## 3. Goals and non-goals

**Goals**

1. A pipeline composed of independently replaceable stages, each described by a narrow contract.
2. A registry + capability model so a stage implementation is chosen by *policy*, not hardcoded.
3. Zero-copy-friendly frame handoff between decode → post-process → render.
4. Runtime switching of decoders, renderers, filters, and tracks where technically feasible.
5. A query surface that reports container/stream/decoder facts and capabilities.
6. A UI-agnostic engine core: the presentation host is an implementation detail of the video
   renderer, not of the engine.

**Non-goals (this document)**

- Choosing specific backends (Media Foundation vs. FFmpeg, D3D11 vs. D3D12, WASAPI, libass, …).
  Candidates are named only as illustrative examples of *what plugs in where*.
- Implementation of any decoder, renderer, filter, or scheduler.
- Plugin public-API changes (see §10 for the direction, not the final shape).

---

## 4. Architectural overview

### 4.1 Layers

```
┌────────────────────────────────────────────────────────────────────────────┐
│  Presentation / UI layer  (WinUI pages, view models)                        │
│    consumes: IMediaEngine façade (transport + selection + query + surface)  │
└───────────────────────────────┬────────────────────────────────────────────┘
                                │
┌───────────────────────────────▼────────────────────────────────────────────┐
│  Orchestration layer                                                        │
│    PlaybackPipeline · PlaybackClock · Synchronizer · state machine          │
└───┬───────────────┬───────────────┬───────────────┬───────────────┬────────┘
    │               │               │               │               │
┌───▼────┐   ┌──────▼─────┐   ┌─────▼──────┐   ┌────▼─────┐   ┌─────▼──────┐
│ Source │ → │  Demuxer   │ → │  Decoder   │ → │  Filter  │ → │  Renderer  │
│        │   │            │   │ video/audio│   │ (post-   │   │  video /   │
│        │   │            │   │ /subtitle  │   │  effects)│   │  audio     │
└────────┘   └────────────┘   └────────────┘   └──────────┘   └────────────┘
                                 │
┌────────────────────────────────▼───────────────────────────────────────────┐
│  Cross-cutting services                                                     │
│    ComponentRegistry · CapabilityModel · TrackSelector · MediaInfoProvider  │
│    BufferPool / ResourceManager · Logging                                   │
└────────────────────────────────────────────────────────────────────────────┘
```

### 4.2 Layer responsibilities

| Layer | Responsibility | Must not know about |
|---|---|---|
| Source | Turn a URI/path/stream into openable bytes; seekable/streamable abstraction | Codecs, GPU, UI |
| Demux | Parse container, enumerate streams, emit timestamped encoded packets per stream | Decoding, rendering |
| Decode | Turn encoded packets into raw frames/buffers; report codec facts | Container, presentation |
| Filter (post-process) | Transform raw frames/buffers in a chain | Where frames came from, where they go |
| Render | Present raw frames/buffers to a device or surface; supply timing | Codecs, container |
| Orchestration | Drive the pull, own the clock, keep A/V in sync, own lifecycle/state | Concrete codec/renderer choices |
| Cross-cutting | Selection policy, capability discovery, info reporting, buffer reuse | Pipeline traversal order |

### 4.3 Data flow and control

The pipeline is **pull-based**: the renderer pulls the next frame it needs, which pulls from the
filter chain, which pulls from the decoder, which pulls a packet from the demuxer. This makes
back-pressure natural (a slow renderer stops the decode chain) and makes the clock authoritative.

- One **PlaybackClock** is the time master. It is driven by the audio renderer when audio is
  present (audio-master, the default), by the video renderer otherwise, or externally.
- The **Synchronizer** compares each frame's presentation timestamp against the clock and decides
  *present / hold / drop*, so A/V drift is corrected in one place.
- Because stages are pull-based and typed by frame kind, a filter or renderer can be swapped
  without touching the demux/decode stages.

---

## 5. Core components and contracts

Namespace proposal: a dedicated `SinePlayer.Playback` (or `SinePlayer.Media`) subtree, with
concrete backends in sub-namespaces (`…Backends.FFmpeg`, `…Backends.MediaFoundation`,
`…Renderers.D3D11`). The contracts below are **illustrative structure**, not final signatures.

### 5.1 Engine façade (UI-facing)

The façade is what view models already use; it stays small and stable while the pipeline behind
it grows. It covers transport, selection, component configuration, query, and surface hosting.

> **Layering note.** The UI-facing surface is realized as a *port* owned by the app, with a thin
> adapter delegating to the engine core in `SinePlayer.Core`. This keeps UI-coupled types (the
> existing `ErrorResult`) and the WinUI presentation host out of the core, and lets the engine be
> swapped without changing view-model call sites. See
> [MediaEngineImplementationPlan.md](./MediaEngineImplementationPlan.md) §5.

| Member group | Examples |
|---|---|
| Lifecycle / transport | `Load(source)`, `Play/Pause/Stop`, `Seek(position)`, `Volume`, `Muted`, `PlaybackRate` |
| State / events | `State`, `Position`, `Duration`, `StateChanged`, `MediaOpened`, `MediaEnded`, `ErrorOccurred` |
| Track selection | `GetTracks()`, `SelectedVideoTrack`, `SelectedAudioTrack`, `SelectedSubtitleTrack`, `SelectTrack(...)` |
| Component control | `ApplyProfile(PlaybackProfile)`, `GetActiveComponents()` |
| Query | `GetMediaInfo()`, `GetCapabilities()` |
| Presentation | `CreatePresentationHost()` → a UI-agnostic host the renderer attaches to |

`PlaybackProfile` is the declarative input to component choice: it names the desired decoders,
renderers, filters, and preferences (e.g. prefer hardware, allow software fallback). The façade
resolves it through the registry (§5.2) into a concrete pipeline.

### 5.2 Component registry and capability model

The registry is the heart of extensibility: it maps an *extension point* to a set of
*implementations*, each advertising what it can do.

| Concept | Meaning |
|---|---|
| Extension point | The role being filled (source, demuxer, video decoder, audio renderer, video filter, …) |
| Factory | Creates an instance of a stage; declares identity, version, priority |
| Capability descriptor | What the implementation supports: codecs, pixel/sample formats, hardware/software, platform, HDR, … |
| Selection policy | Resolves *requested profile + media facts + capabilities + user preference* into one implementation, with fallback ordering |

Selection policy is centralized so "switch the video decoder" is a policy change, not a call into
a specific backend.

### 5.3 Source and demux

| Component | Responsibility | Key facts it owns |
|---|---|---|
| `IMediaSourceProvider` | Open a path/URI/stream into a source object | Locality (file/network), seekability |
| `IMediaSource` | Byte-level read/seek abstraction | Size, seek ranges |
| `IDemuxer` | Parse container, enumerate streams, emit packets, seek to keyframe/timestamp | Container format, stream table, packet timeline |

Demux exposes **`IMediaStreamInfo`** per stream: kind (video/audio/subtitle), codec id, extradata
reference, bitrate, language, and a kind-specific descriptor. This table is the single source of
truth that feeds decoding, track selection, and info queries.

### 5.4 Decode

A common base contract with three specializations keeps the pipeline uniform while allowing
codec-specific configuration.

| Contract | Input → Output | Notable members |
|---|---|---|
| `IMediaDecoder` (base) | packets → frames | `Configure(streamInfo, options)`, `Flush()`, `IsHardware`, `Reset()` |
| `IVideoDecoder` | encoded video packet → `RawVideoFrame` | output pixel format, dimensions, color info |
| `IAudioDecoder` | encoded audio packet → `RawAudioBuffer` | output sample rate, channel layout, sample format |
| `ISubtitleDecoder` | encoded subtitle packet → subtitle event | parsed cue list / bitmap |

`DecoderOptions` carries the hardware/software preference and allow-fallback flag that the
registry resolves. Decoder facts (actual codec profile, whether hardware is active) are reported
back so they surface through the query API.

### 5.5 Post-processing (filters / effects)

Filters are chainable, format-aware stages applied to raw frames without a round-trip to the CPU.

| Contract | Applies to | Examples |
|---|---|---|
| `IVideoFilter` | `RawVideoFrame` | rotation, scaling, color balancing/LUT, deinterlace, crop, HDR tone-map |
| `IAudioFilter` | `RawAudioBuffer` | gain, channel mix/downmix, resample, EQ, normalization |

The chain is a `FilterGraph`: the orchestrator negotiates formats across the chain once (each
filter declares what it accepts and produces) and inserts pass-through where a filter is not
needed, so enabling/disabling an effect does not force a pipeline rebuild. Filtering happens where
the frames already live (GPU for video, the render buffer for audio) to keep it high-performance.

### 5.6 Render

Renderers are the sink of the pipeline and, together with the clock, define timing.

| Contract | Role | Extension-relevant aspects |
|---|---|---|
| `IVideoRenderer` | Accept `RawVideoFrame`, present on device via a presentation host | GPU API (e.g. D3D), swap-chain/present mode, surface format, HDR, vsync |
| `IAudioRenderer` | Accept `RawAudioBuffer`, play on device | Audio API (e.g. WASAPI), device selection, exclusive/shared, buffer latency, clock source |
| `ISubtitleRenderer` | Draw cues over the video output | text vs. bitmap subtitles, styling, overlay compositing |

The **presentation host** abstraction replaces `CreateVideoSurface()`: the engine returns a
UI-agnostic host object, and each video renderer knows how to attach its output to it. This
decouples the engine from WinUI and makes a renderer swap invisible to the page.

### 5.7 Clock and synchronization

| Contract | Responsibility |
|---|---|
| `IPlaybackClock` | Authoritative position; source selectable (audio-master / video-master / external) |
| `ISynchronizer` | Compare frame PTS to clock; present / hold / drop; correct drift |

Because the clock source is itself selectable, correctness does not depend on which renderer
happens to be active — which is exactly what makes renderer switching safe.

### 5.8 Track selection

| Contract | Responsibility |
|---|---|
| `ITrackSelector` | Enumerate selectable tracks; change the active video/audio/subtitle stream |
| `MediaTrackInfo` | Kind, id, language, title, default/forced flags, plus kind-specific descriptor |

Selection is separated from demuxing so a track change can reuse the existing demuxer and only
re-point the affected decoder chain.

### 5.9 Playback info and capability query

Two distinct read-only surfaces:

| Query | Answer | Backing source |
|---|---|---|
| `MediaInfo` (per media) | container, duration, per-stream **codec, profile, bitrate, sample rate, channels, width/height, FPS, color info, rotation** | Populated by demuxer + decoder (facts reported back up) |
| `Capabilities` (per runtime) | Which decoders/renderers/filters exist and what they support | Registry capability descriptors |

This split matters: "what is this file?" and "what can this machine do?" are different questions
with different lifetimes, and later playback decisions are made from both.

---

## 6. Extensibility model

Each extension point is a registry slot. Adding a capability means registering a factory with a
capability descriptor; nothing in the orchestration layer changes.

| # | Extension point | Adds | Selected by | Hot-swap at runtime |
|---|---|---|---|---|
| 1 | Source provider | New input schemes (network, archive, custom) | Scheme/URI match | On load |
| 2 | Demuxer | New container formats | Container sniffing + capability | On load |
| 3 | Video decoder | Codecs, HW/SW variants | Codec + pixel format + HW preference | Yes (on keyframe / with reset) |
| 4 | Audio decoder | Codecs, HW/SW variants | Codec + sample format + HW preference | Yes (with flush/reset) |
| 5 | Subtitle decoder | Subtitle formats | Codec id | Yes |
| 6 | Video filter | Post-effects | Requested profile + format negotiation | Yes (chain re-negotiation) |
| 7 | Audio filter | Post-effects | Requested profile + format negotiation | Yes |
| 8 | Video renderer | GPU/present paths | Platform + format + user preference | Yes (presentation handoff) |
| 9 | Audio renderer | Audio device paths | Platform + device + user preference | Yes (device re-open) |
| 10 | Subtitle renderer | Overlay styling/compositing | Subtitle type + theme | Yes |
| 11 | Clock source | Timing strategies | Pipeline configuration | On configuration |

**Switch semantics.** Runtime switching is expressed uniformly as *re-resolve the affected slot
from the registry, then hand over state* (last frame for video, buffered samples for audio,
current position from the clock). Stages that cannot swap mid-stream degrade gracefully to a
brief reset at a safe boundary (keyframe or silence), and the façade reports this through state
transitions rather than failing the load.

---

## 7. Scenario walkthroughs

| # | Scenario | Where it lives in the design |
|---|---|---|
| 1 | Switch video decoder (HW/SW) | Register decoder factories with capability descriptors; `DecoderOptions.PreferHardware` + fallback; re-resolve the decoder slot (§5.4, §5.2) |
| 2 | Switch audio decoder | Same mechanism for the audio decoder slot (§5.4) |
| 3 | Switch video renderer | Video renderer slot + presentation host; frames handed over instead of a WinUI element swap (§5.6) |
| 4 | Switch audio renderer | Audio renderer slot; clock source re-established on switch (§5.6, §5.7) |
| 5 | High-performance post-effects | FilterGraph on raw frames near the device; format negotiation; pass-through when idle (§5.5) |
| 6 | Select video/audio/subtitle channels | `ITrackSelector` over the demuxer's stream table; re-points only the affected chain (§5.8, §5.3) |
| 7 | Query playback info | `MediaInfo` from demux+decoder facts; `Capabilities` from the registry (§5.9) |

---

## 8. Format negotiation and performance

- **Frames are resources, not bytes.** `RawVideoFrame` / `RawAudioBuffer` carry a reference to a
  pooled device resource; stages pass references, not copies.
- **Negotiation happens once per chain build.** Each stage declares accepted/produced formats;
  the FilterGraph resolves a compatible path and inserts converters only when unavoidable.
- **Buffer pooling.** Decoders/filters/renderers draw from a shared pool sized to the pipeline
  depth, avoiding per-frame allocation.
- **Back-pressure, in one place.** Decoding is stopped by room in the frame queue and by nothing else, so
  nothing decodes faster than the renderer consumes; the queues in front of the reader have no maximum, so
  that back-pressure can never reach the reader — and through it, the sound.
- **Effects stay on the device.** Video effects run in the GPU stage between decode and present;
  audio effects run in the render buffer, so no full-frame CPU round-trip is introduced.

---

## 9. Threading and lifecycle

| Thread | Owns |
|---|---|
| Control / UI | Façade calls, state machine, events marshalled to the UI thread |
| Demux | Packet reading and seeking |
| Decode (one per active stream) | Packet → frame conversion |
| Present / render | Pulling frames, clock update, device presentation |

As built, that is four threads: one reading the file and decoding audio on the spot (audio is cheap and every
hand-off is a chance for it to be late), one decoding video, one presenting frames, and one handing audio
blocks to the device. Priority follows who cannot be late: the reader and the audio thread run above normal,
and the decoding thread is deliberately ordinary, because its lateness is absorbed by the starvation rule
rather than by discarding frames.

Handoff between threads uses thread-safe queues owned by the pipeline. The state machine
(`None → Opening → Playing/Paused → Ended/Error`) is preserved from today's model, extended with a
`Rebuffering`/component-swap sub-state so the UI can reflect a decoder/renderer change without
treating it as an error.

**The queues are what pace the reader, and each one has to answer the same question: what becomes of
work already in flight when the position changes?** A seek counts as a boundary. Work carries the epoch
it was started under, and anything from an older epoch is given up on rather than played as if it belonged
to the new position — including a producer waiting for room, which is what stops a seek waiting on the
queue it is about to empty. Playback also outlives the end of the file: the reading thread goes idle at
the end instead of exiting, so a seek from the end has something to wake.

### 9.1 The rules that keep four threads from waiting on each other

Every stall this engine has had — a picture frozen after a seek, sound breaking up while the picture stood
still, a seek that never returned — was the same thing: **a circle of waits**. Four stages, each waiting for
the next, and no way in:

```
presentation waits for the clock to reach a frame
        ↑ because the frame queue is full
video decoding waits for room in the frame queue
        ↑ because the packet queue is full
the reader waits for room in the packet queue
        ↑ and produces no more audio
the device has nothing to play, so the clock never moves
        ↳ which is what presentation was waiting for
```

The circle exists because one thread's progress is made to depend on another's *time* wait. The rules this
engine is built on are the answer, and they are about waits rather than about threads:

> **Only the presentation thread waits on the clock. Only the reader waits on the file. Nothing waits on the
> reader.**

| Stage | What it waits for | Why that is safe |
|---|---|---|
| Reader | The file | The one wait that belongs to the outside world, and the one case where the whole pipeline correctly waits for IO |
| Reader | *Nothing else*: it idles while both queues hold their minimums | The queues have no maximum, so no other stage's slowness can stop audio production |
| Video decoding | Room in the frame queue | The consumer is the picture, and a seek releases the wait through the epoch |
| Presentation | The clock reaching a frame's moment | It is the follower: it waits on time, which passes on its own |
| Audio sender | Room in the device's buffer | The device is the clock, so a device that is not consuming is time that has stopped, and everything follows it |
| Nothing | Another stage's progress | That is the property that makes a cycle impossible |

Three consequences worth stating, because all three are load-bearing:

- **The queues in front of the reader have no maximum.** The reader is the single cursor of an interleaved
  file, so a bounded video queue is a stopped *audio* producer. It paces itself instead: fill both queues to
  their minimums, idle, and read again when either runs low.
- **The decoder never drops a frame and never consults the clock.** It stops when the frame queue is full and
  starts when the picture takes one. A decoder that cannot keep up therefore pauses playback rather than
  discarding the frames the picture is about to need — and the pause is the honest signal.
- **Only the presentation thread loses frames, and only best-effort.** It drops a frame whose moment has gone
  when — and only when — a closer one is already waiting, and it holds the clock back when the picture has
  fallen more than 200 ms behind it, releasing it once the gap is within 100 ms.

What is *not* protected, deliberately: if the clock never moves, the picture never moves. That is correct —
the sound is the leader and a picture that ran on regardless would be out of sync — and the one remaining way
to reach it is a device that takes audio and never reports playing it. Nothing inside the engine can repair
that, and nothing inside it names it either: a device that is late and a device that is broken are the same
frozen reading from the engine's side of the call, so there is no line to raise that is not a guess. What the
engine has instead is the shape of the freeze — `Position` stopped while the state is `Playing` and nothing is
starving — which is in §9 and §14 of [PlaybackPipeline.md](./PlaybackPipeline.md), along with the full rule
set, the rules a change must not break, and the reasoning behind each line; the review that produced them is
in [PlaybackPipelineRefactor.md](./PlaybackPipelineRefactor.md).

---

## 10. Plugin integration

The app already has a plugin system (see [`SinePlayer.SDK`](../src/SinePlayer.SDK) and
`IPluginContext`). The registry is the natural bridge: the media contracts should live somewhere
plugins can reference, and `IPluginContext` gains registration calls for the media extension
points (decoders, renderers, filters, subtitle renderers). This lets third parties add playback
capabilities exactly the way first-party backends do, without privileged access to the pipeline.

Direction (not final API): the contracts move to a shared contracts assembly; the registry
exposes registration methods; the engine's selection policy treats plugin-supplied factories as
equals, ordered by priority and capability match.

---

## 11. Migration plan

| Phase | Deliverable | Outcome |
|---|---|---|
| 1 | Introduce the façade + registry; wrap the existing native engine as first-party stage implementations | Behavior unchanged; every stage now has a seam |
| 2 | Add the query surfaces (`MediaInfo`, `Capabilities`) and track selection | Scenarios 6 and 7 available |
| 3 | Add filter chain and non-native decode/render backends | Scenarios 1–5 available |
| 4 | Open the registry to plugins | Third-party extensibility |

The façade keeps its current members, so [PlayerPageViewModel](../src/SinePlayer/Views/Pages/Player/PlayerPageViewModel.cs)
only needs to adopt the new selection/query members; the existing transport calls stay valid.

---

## 12. Open decisions

1. **Core backend set** — Media Foundation only, an FFmpeg-centered pipeline, or both behind the
   registry. Affects effort and licensing.
2. **Graphics API** — D3D11 vs. D3D12 for the video pipeline and present path.
3. **Audio path** — whether to keep a platform player for audio-only/fallback or standardize on
   one audio renderer.
4. **Subtitle rendering** — text/bitmap subtitle handling and styling (own renderer vs. library).
5. **Plugin exposure surface** — which media extension points are public API in v1.
6. **Fallback policy** — how aggressively to degrade (e.g. fall back to the platform player) when
   no registered component can handle a media file.

---

See [MediaEngineImplementationPlan.md](./MediaEngineImplementationPlan.md) for the step-by-step
delivery plan, target package layout, and the FFmpeg isolation boundary.

---

## Appendix A — Contract index

| Contract | Layer | Purpose |
|---|---|---|
| `IMediaEngine` | Façade | UI-facing transport, selection, query, presentation |
| `PlaybackProfile` | Façade | Declarative requested component composition |
| `IMediaEngineComponentRegistry` | Cross-cutting | Map extension points → factories + capabilities |
| `IMediaSourceProvider` / `IMediaSource` | Source | Open input, byte access |
| `IDemuxer` / `IMediaStreamInfo` | Demux | Container parsing, stream table, packets |
| `IMediaDecoder` / `IVideoDecoder` / `IAudioDecoder` / `ISubtitleDecoder` | Decode | Encoded → raw frames/buffers |
| `IVideoFilter` / `IAudioFilter` / `FilterGraph` | Filter | Post-effects chain |
| `IVideoRenderer` / `IAudioRenderer` / `ISubtitleRenderer` | Render | Presentation and device output |
| `IPresentationHost` | Render | UI-agnostic surface the video renderer attaches to |
| `IPlaybackClock` / `ISynchronizer` | Orchestration | Timing and A/V sync |
| `ITrackSelector` / `MediaTrackInfo` | Cross-cutting | Stream selection |
| `MediaInfo` / `Capabilities` | Query | Per-media facts and runtime capabilities |
| `PlaybackPipeline` | Orchestration | Composition + lifecycle of the above |
