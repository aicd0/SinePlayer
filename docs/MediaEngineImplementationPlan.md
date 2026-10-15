# Media Engine — Implementation Plan

Status: **Draft for review** · Companion to [MediaEngineDesign.md](./MediaEngineDesign.md)

This plan turns the design in [MediaEngineDesign.md](./MediaEngineDesign.md) into a sequence of
small, individually reviewable changes. It is ordered so the solution **builds and the app
behaves as before at every step**, with the engine swap isolated to a single cutover step.

---

## 1. Objective

Replace the native-player wrapper with a composable, FFmpeg-backed playback engine living in
`SinePlayer.Core`, without disturbing the player UI beyond a one-line change until the engine is
ready.

## 2. v1 scope

**In scope (the seven scenarios from the design doc)**

| # | Scenario | v1 |
|---|---|---|
| 1 | Switch video decoder (HW/SW) | Yes |
| 2 | Switch audio decoder | Yes |
| 3 | Switch video renderer | Yes |
| 4 | Switch audio renderer | Yes |
| 5 | High-performance post-effects | Yes (chain + rotation/scale first) |
| 6 | Select video/audio/subtitle channels | Yes (video/audio rendered; subtitle enumerated only) |
| 7 | Query playback info | Yes |

**Explicitly deferred — but the framework must not block them**

| Deferred | How v1 keeps the door open |
|---|---|
| Subtitle rendering | `ISubtitleDecoder` / `ISubtitleRenderer` contracts exist and the pipeline has the slot; the demuxer enumerates subtitle streams so they appear in info/track lists. No implementation; the engine degrades gracefully when no subtitle renderer is registered. |
| Plugin surface | The registry is `internal` in v1 and is *not* exposed through `IPluginContext`. Its API is designed so that exposing it later is purely additive (register a factory = one call). No plugin contract changes in v1. |
| Fallback policy | Component resolution always goes through a selectable policy object. v1 ships exactly one policy (registry-only, no platform fallback). A fallback strategy is a later *addition*, not a redesign. The legacy `Windows.Media.*` path is removed, so there is no fallback in v1. |

## 3. Working agreements

1. **Minimal, reviewable steps.** One step = one reviewable change. Every step compiles and
   leaves the app working.
2. **Everything engine-related goes in Core.** All new engine code lives under
   `src/SinePlayer.Core/Playback/`. The app keeps only a thin port + adapter (see §5).
3. **FFmpeg is a backend, not a dependency of the design.** FFmpeg types may appear only inside
   `Playback/Backends/FFmpeg/`. Contracts and data types are backend-neutral. See §4.
4. **Minimal player changes.** `PlayerPageViewModel` gets a one-line `using` change at the seam
   (step S1) and is otherwise untouched until we deliberately surface new features (step S10+).
5. **Old engine is deleted.** `NativeMediaEngine` and the `Windows.Media.*` usage are removed in
   step S9, immediately after the new engine reaches parity.
6. **Verification before advancing.** Each step lists how it is verified, including a
   development harness ("Playback Lab") so the engine can be driven before the player is switched
   over.

## 4. Structure and coupling

### 4.1 Target layout

```
src/SinePlayer.Core/Playback/
├─ MediaEngineState.cs            # shared state vocabulary (S1)
├─ MediaEngineService.cs          # composition root: builds registry + default profile
├─ MediaEngineOptions.cs          # native library directory, default profile, limits
├─ Abstractions/                  # contracts only — no backend, no UI types (S2, S3)
│  ├─ (root)     PlaybackResult, IMediaBuffer, MediaStreamKind, PixelFormat,
│  │             AudioSampleFormat, ChannelLayout
│  ├─ Info/      MediaInfo, MediaStreamInfo, VideoStreamInfo, AudioStreamInfo, SubtitleStreamInfo
│  ├─ Source/    IMediaSource, MediaPacket, MediaLocator
│  ├─ Decode/    IMediaDecoder, IVideoDecoder, IAudioDecoder, ISubtitleDecoder, DecoderOptions,
│  │             RawVideoFrame, IGpuVideoBuffer, IPlanarVideoBuffer, RawAudioBuffer, SubtitleCue
│  ├─ Filter/    IMediaFilter, IVideoFilter, IAudioFilter
│  ├─ Render/    IVideoRenderTarget, IVideoRenderer, IAudioRenderer, ISubtitleRenderer
│  ├─ Timing/    IPlaybackClock, ISynchronizer, FrameSyncAction
│  ├─ Tracks/    ITrackController
│  └─ Registry/  IComponentRegistry, IComponentFactory, ISourceProviderFactory,
│                IVideoDecoderFactory, IAudioDecoderFactory, ISubtitleDecoderFactory,
│                ComponentCapabilities, ComponentSelectionOptions, ExtensionPoint,
│                ComponentRegistryExtensions
├─ Internal/                      # orchestration + cross-cutting implementation
│  ├─ Registry/  ComponentRegistry.cs
│  ├─ Selection/ ComponentResolver.cs
│  ├─ PlaybackPipeline.cs, PacketBuffer.cs, FramePool.cs
│  └─ Timing/  AudioClock.cs, VideoClock.cs, DefaultSynchronizer.cs
└─ Backends/                      # one folder per backend; the only place its types appear
   ├─ FFmpeg/       FFmpegRuntime, FFmpegInterop, FFmpegSource, FFmpegSourceProviderFactory,
   │                FFmpegPacketBuffer, FFmpegVideoDecoder, FFmpegVideoDecoderFactory,
   │                FFmpegVideoFrameBuffer
   │  └─ Interop/   FFmpegTypes, FFmpegNative, FFmpegConstants   (pinned ABI, read from headers)
   ├─ Direct3D11/   D3D11Device, D3D11Interop, D3D11VideoShaders, D3D11RenderTarget,
   │                D3D11SwapChain, D3D11VideoRenderer, D3D11VideoRendererFactory
   │                (COM interop generated by CsWin32 from NativeMethods.$(Platform).txt)
   └─ Wasapi/       WasapiAudioRenderer
```

Namespaces match folders exactly (`SinePlayer.Core.Playback.Abstractions.Decode`, …), which makes the
isolation rules below visible in every type name.

### 4.2 Dependency rules (enforced by review; optionally by test in S15)

```
Abstractions      → BCL + SinePlayer.Core.Common        (nothing else)
Internal          → Abstractions
Backends.*        → Abstractions (+ that backend's own native/lib dependency)
MediaEngineService→ Abstractions + Internal + Backends  (composition root only)
```

**Hard rules**

1. No FFmpeg type (including `FFmpeg.AutoGen` types) may appear in a signature outside
   `Backends/FFmpeg/`. The composition root names only a factory *type*, never an FFmpeg type.
   FFmpeg `AVFrame`/`AVPacket`/`AVSampleFormat` never cross into `Abstractions`.
2. No WinUI type appears outside the app-side presentation host (see §5). Core creates no
   `SwapChainPanel`.
3. `Abstractions` carries no third-party reference at all. `RawVideoFrame` uses our own
   `PixelFormat` enum; `RawAudioBuffer` uses our own `SampleFormat`/`ChannelLayout` enums.
4. `RawVideoFrame` must be **GPU-capable from day one** (a device resource handle plus an optional
   CPU view), even though the first decoder implemented is software. Retrofitting this later would
   invalidate the filter/render contracts.

### 4.3 Why the boundary holds with the backend in the same assembly

Because everything is one assembly, the compiler cannot enforce rule 1. v1 accepts this and
enforces it by convention + a cheap architecture test (S15) that reflects over
`SinePlayer.Core` and fails if any type outside `…Backends.FFmpeg` exposes an FFmpeg type in its
signature. If hard enforcement is later wanted, moving `Backends/FFmpeg` into its own project
(`SinePlayer.Core.Playback.FFmpeg`) is a mechanical, no-API-change move.

## 5. Adaptation layer (keeping `PlayerPageViewModel` nearly untouched)

Two facts drive the design:

- `PlayerPageViewModel.Load` consumes the app's `ErrorResult`, which is `internal` and pulls in
  UI/action/localization types (`ActionHandler`, `StringResourceProvider`). It must **not** move
  into Core.
- `GetVideoSurface()` returns a WinUI `UIElement` and is added to a plain `Grid`
  (`VideoHost` in [PlayerPage.xaml](../src/SinePlayer/Views/Pages/Player/PlayerPage.xaml)), so any
  `UIElement` works — including a `SwapChainPanel`.

Therefore the UI-facing contract stays in the app as a **port**, and a thin **adapter** bridges it
to the Core engine:

```
PlayerPageViewModel
      │  (unchanged call sites)
      ▼
SinePlayer.Common.Playback.IMediaEngine        ← UI-facing port (app)
SinePlayer.Common.Playback.MediaEngineService  ← factory (app)          ┐ adaptation
SinePlayer.Common.Playback.PlaybackEngineAdapter  (arrives at S9)      ┘ layer (app)
      │  maps state 1:1, maps PlaybackResult → ErrorResult,
      │  owns the WinUI presentation host
      ▼
SinePlayer.Core.Playback.PlaybackEngine        ← engine + all contracts (Core)
```

Consequences:

- **`PlayerPageViewModel` diff at the seam = one added `using SinePlayer.Core.Playback;`**
  (needed because `MediaEngineState` moves to Core). No call-site or logic changes.
- `ErrorResult` handling is unchanged in the VM; the adapter converts the engine's own
  `PlaybackResult` (success flag + message + exception, Core-local) into the app's `ErrorResult`
  via the existing `ErrorLogger`.
- `MediaEngineService.CreateEngine()` keeps its name and signature, so the VM is untouched there.
- The adapter owns a ~50-line WinUI host (`SwapChainPanel` + attach to the Core renderer), which is
  the only WinUI code near the engine — keeping Core UI-free and `CreateVideoSurface()` working
  unchanged.
- Rich features (tracks, info, switching) are added as **additional capability interfaces**
  implemented by the Core engine and reachable from the port, so growing them never breaks the
  existing port shape.

> Note: this refines design doc §5.1 (the façade stays in the app as a port rather than moving
> wholesale into Core). The design doc has been updated accordingly.

## 6. Step plan

### Phase 0 — Decisions (resolved)

| Step | Goal | Deliverable | Status |
|---|---|---|---|
| **S0** | Lock the decisions that gate later steps | Decisions recorded below | Resolved |

**FFmpeg backend.** FFmpeg (GPL build) is the v1 backend; the app moves to GPL-3.0 to match.
Verified facts about the imported `FFmpeg.GPL 20261013.1.0` package:

| Item | Value |
|---|---|
| FFmpeg version | FFmpeg 9.0 ABI series — `Lavc63.18.101`, `Lavu61.13.100` (`libavcodec 63`, `libavformat 63`, `libavutil 61`, `libavfilter 12`, `swresample 7`, `swscale 10`). Built from BtbN's master channel, so it may sit slightly ahead of the 9.0 tag within the same ABI series. |
| Managed bindings | **None** — headers, `.lib` import libs and runtime DLLs only |
| Architecture coverage | **win-x64 only** (`runtimes/win-x64`; PE machine `0x8664` for both the runtime and `build/native/bin` copies) |
| License | `GPL-3.0-or-later` |

Because the package ships no managed bindings, the P/Invoke layer is ours, written against
`libavcodec`/`libavformat`/`libavutil`/`swresample`/`swscale`. This is deliberate: a narrow,
hand-written surface is exactly the interop that can be kept behind the isolation boundary, and it
avoids depending on a binding generator that must chase the FFmpeg ABI.

**Video renderer.** Direct **D3D11 + DXGI composition swap chain**, attached to a WinUI
`SwapChainPanel` through `ISwapChainPanelNative`. Rationale: lowest latency, no wrapper overhead,
full control of the present path, and the GPU stage is where post-effects must live. Interop is
confined to `Backends/Direct3D11` plus one app-side host file, with the COM interface declared
once.

**Audio renderer.** Direct **WASAPI**, event-driven, shared mode by default with exclusive mode as
an option. Rationale: lowest and most predictable latency, direct device control, and a reliable
clock for A/V sync (the audio renderer is the clock master).

**Interop policy.** Interop code is unavoidable for all three of FFmpeg, D3D11 and WASAPI. It is
contained by three rules: (1) one directory per backend, (2) all native handles wrapped in a
`SafeHandle`-derived type owned by that backend, and (3) no native handle, struct or enum in a
signature outside its backend directory. See §4.2.

**Architecture decision — arm64 required, x86 deliberately parked.** The imported package covers
**x64 only**, which reproduces as a real gap: an x64 build of the app carries the FFmpeg DLLs, an
ARM64 build carries none. BtbN publishes no 32-bit Windows FFmpeg builds at all:

| Architecture | Availability | Handling |
|---|---|---|
| x64 | Available | Supported |
| arm64 | Available — `ffmpeg-*-winarm64-gpl-shared.zip` from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds); the NuGet package simply omits it | **Required**; binaries must come from the BtbN channel |
| x86 (32-bit) | **Not available** from any BtbN channel | **Parked** — the build configuration is kept so the decision can be revisited, and the engine deliberately has no source for it for now |

x86 is therefore expected to build but **not** to play media; it is a known, accepted gap rather
than an oversight, and resolving it is explicitly out of the current scope.

**Remaining provisioning work (before shipping).** Provisioning must be arch-aware and cannot rely on
the package's `runtimes/` folder alone, because x64 and arm64 arrive through different channels. The
runtime side of this is already in place — `FFmpegRuntime` resolves
`runtimes/win-<arch>/native` then the application directory, and fails loudly with the missing file
names. What remains is supplying the files:

1. Pin **one** FFmpeg version for both architectures — mixed versions would undermine the
   reliability priority, and arch-specific bugs would be very hard to attribute.
2. Place the arm64 libraries where the runtime looks for them; x64 currently resolves through the
   NuGet package's RID assets.
3. Keep `FFmpegRuntime`'s expected major versions in step with whatever is supplied — it is the
   guard that stops a mismatched drop from corrupting memory.

**Known cost.** The current GPL build is large: `avcodec-63.dll` alone is ~114 MB, ~183 MB of
native DLLs per architecture. This is a packaging concern, not an API concern — the isolation
boundary means a trimmed custom FFmpeg build can be swapped in later without touching any
contract.

### Phase 1 — Seam and contracts (no behavior change)

| Step | Goal | Verify | Review size |
|---|---|---|---|
| **S1** ✅ | Engine layer and shared vocabulary | Solution builds for x64 and ARM64; playback unchanged | 4 files, mechanical |
| **S2** ✅ | Add the backend-neutral contracts | Solution builds with warnings-as-errors; contracts reviewed | 33 interface/type files, no implementations |
| **S3** ✅ | Add the registry + selection policy | Unit tests on selection | 11 files + 20 tests |

**S1 — Engine layer and shared vocabulary.** *Done.*
`MediaEngineState` moved to `SinePlayer.Core.Playback`; the app copy is deleted, and the port
[IMediaEngine](../src/SinePlayer/Common/Playback/IMediaEngine.cs), the legacy engine and
[PlayerPageViewModel](../src/SinePlayer/Views/Pages/Player/PlayerPageViewModel.cs) now consume
the Core enum. The `FFmpeg.GPL` native dependency moved from the app project to
[SinePlayer.Core](../src/SinePlayer.Core/SinePlayer.Core.csproj) so native assets flow through the
engine layer rather than the UI layer.

*Deliberate deviation from the sketch below:* `PlaybackResult` and a `PlaybackEngineAdapter` class
are **not** introduced yet. The only engine behind the seam is still the legacy one, so an adapter
would be pure pass-through; it arrives at S9 together with the error-type mapping, once there is
something real to adapt. The seam that matters already exists — `MediaEngineService.CreateEngine()`
is the single construction point and the view model only ever touches the port.

Also folded in: the x86 build configuration was removed (see the architecture decision above),
including the solution configurations, publish profile and `NativeMethods.x86.txt`.
*Verified:* the full solution builds for x64 and ARM64; all 25 existing unit tests pass; behaviour
unchanged.

**S2 — Backend-neutral contracts.** *Done.*
Added `src/SinePlayer.Core/Playback/Abstractions/**` — 33 contract and data-type files, no
implementations, nothing wired into the app:

| Folder | Contents |
|---|---|
| `Abstractions/` | `PlaybackResult`, `IMediaBuffer`, `MediaStreamKind`, `PixelFormat`, `AudioSampleFormat`, `ChannelLayout` |
| `Abstractions/Info/` | `MediaInfo`, `MediaStreamInfo`, `VideoStreamInfo`, `AudioStreamInfo`, `SubtitleStreamInfo` |
| `Abstractions/Source/` | `IMediaSource`, `MediaPacket` |
| `Abstractions/Decode/` | `IMediaDecoder`, `IVideoDecoder`, `IAudioDecoder`, `ISubtitleDecoder`, `DecoderOptions`, `RawVideoFrame`, `IGpuVideoBuffer`, `RawAudioBuffer`, `SubtitleCue` |
| `Abstractions/Filter/` | `IMediaFilter`, `IVideoFilter`, `IAudioFilter` |
| `Abstractions/Render/` | `IVideoRenderTarget`, `IVideoRenderer`, `IAudioRenderer`, `ISubtitleRenderer` |
| `Abstractions/Timing/` | `IPlaybackClock`, `ISynchronizer`, `FrameSyncAction` |
| `Abstractions/Tracks/` | `ITrackController` |

Refinements made while writing the contracts, and why:

1. **Source and demux collapse into one stage.** The sketch had `IDemuxer` separately;
   `IMediaSource` now carries the stream table, packet reading and seeking. A demuxer-driven backend
   keeps the bytes and the container in the same object, so the split would have produced a seam with
   exactly one implementation on each side. Custom input kinds are still an extension point — they
   just produce an `IMediaSource`.
2. **Results are engine-local.** `PlaybackResult` exists so the engine never references the app's
   UI-coupled `ErrorResult`; the adapter maps between them at S9.
3. **GPU frames are first-class from the start.** `IGpuVideoBuffer` is part of the frame contract, so
   hardware frames can flow decoder → filter → renderer without a CPU copy, and the GPU resource stays
   an engine concept instead of a private detail of the FFmpeg backend.
4. **Ownership is explicit.** `MediaPacket`, `RawVideoFrame` and `RawAudioBuffer` are `IDisposable`
   and release their buffer; filters may return the input instance to signal pass-through or in-place
   work, and renderers never take ownership of a frame. This is what makes zero-copy and in-place
   effects expressible without ambiguity.
5. **The registry is entirely S3.** S2 defines *what* a stage does; S3 defines *which* implementation
   is chosen. `DecoderOptions` is the only capability hint here, and it expresses a preference rather
   than performing a lookup.
6. **Track selection is a stage, not a façade detail** (`ITrackController`), so it can be exercised
   without a live pipeline.
7. **Subtitle contracts are empty slots.** `ISubtitleDecoder`/`ISubtitleRenderer` exist, carry no
   implementation, and the pipeline is expected to tolerate their absence — which is what keeps the
   deferred subtitle work additive.

*Verified:* `SinePlayer.Core` and the full solution build for x64 with `TreatWarningsAsErrors` and
`EnforceCodeStyleInBuild` on; behaviour is unchanged because nothing consumes the contracts yet.

**S3 — Registry and selection policy.** *Done.*
Added the component model in `Abstractions/Registry/` (11 files including the typed registration
helpers), `Abstractions/Source/MediaLocator.cs`, and the two implementations —
`Internal/Registry/ComponentRegistry.cs` and `Internal/Selection/ComponentResolver.cs`. 20 new unit
tests; 45 pass in total.

Refinements made while implementing, and why:

1. **Resolution returns an ordered list, not a single winner.** `ResolveVideoDecoders(stream,
   options)` hands back candidates best-first; the pipeline walks the list and stops at the first
   candidate it can actually create. That is what makes hardware→software fallback a property of
   *selection* rather than a special case inside the pipeline, and it keeps "nothing matched" (an
   empty list) distinct from "the chosen decoder failed to initialise" — the caller interprets each
   correctly because only it knows whether the stream was playable at all.
2. **Selection is separated from decoder creation.** `DecoderOptions` lost the two selection flags it
   carried in S2 and now holds only creation knobs; selecting *which* decoder runs is
   `ComponentSelectionOptions`. The two questions have different owners (policy versus backend), and
   merging them would have forced every backend to understand the fallback policy.
3. **`AllowFallback` is symmetric.** The sketched `AllowSoftwareFallback` was asymmetric and had no
   coherent meaning when software was the preferred kind. `AllowFallback = false` now simply means
   "only the preferred kind", which behaves identically in both directions.
4. **Capabilities do not name their own extension point.** The registry key is the single source of
   truth, so a capability cannot claim to be something it is not; the typed `Register` overloads stop
   a factory from being registered under the wrong role in the first place.
5. **`PreferredName` is in the policy from the start.** Scenarios 1–4 are fundamentally "the user
   picks a named decoder or renderer", so the ordering policy already accepts an explicit name. This
   avoids changing the resolution signature when the settings surface arrives at S12/S13.
6. **`MediaLocator` carries a scheme.** Matching source providers on a scheme string keeps selection
   purely data-driven, instead of asking each provider a `CanOpen` predicate that unit tests would
   have to reason about opaquely.

Behaviour covered by tests: exact codec match, unrestricted ("any codec") match, no match yielding an
empty list, hardware-before-software, software-before-hardware, `AllowFallback = false` dropping the
non-preferred kind, priority tie-breaks, registration order as the final tie-break, `PreferredName`
overriding automatic ordering, scheme filtering for source providers, priority ordering for
components with no hardware distinction, and capability listing order. `MediaLocator` covers drive
paths, forward-slash paths, UNC paths, relative paths, HTTP/HTTPS URLs, scheme lower-casing and
rejection of empty input.

*Verified:* full solution builds for x64 and ARM64; 45/45 unit tests pass; behaviour unchanged
because nothing consumes the registry yet.

### Phase 2 — FFmpeg engine, driven by a development harness

A temporary **Playback Lab** page at
[Views/Pages/PlaybackLab](../src/SinePlayer/Views/Pages/PlaybackLab) drives the engine directly
(choose a file, show state and errors). It is throwaway scaffolding that lets us verify without
touching the player: it is registered as `playback_lab`, opens only in developer mode like the dev
tools page, and has an entry beside it in the main page menu.

| Step | Goal | Verify | Review size |
|---|---|---|---|
| **S4** ✅ | FFmpeg runtime + source + `MediaInfo` probe | 5 unit tests demux a generated container through the real pinned natives | 8 files + 5 tests |
| **S5** ✅ | Software video decoder | 3 unit tests decode a real bitstream through libavcodec | 5 files + 3 tests |
| **S5b** ✅ | Decoder selection (libdav1d for AV1) | 1 test decodes a generated AV1-in-MP4 with timed frames | 1 file + 1 test |
| **S6** ✅ | D3D11 foundation: device, interop strategy | 2 tests create a real device and texture | 4 files + 2 tests |
| **S6b** ✅ | D3D11 frame upload + shader draw + readback | 4 tests render frames offscreen and verify exact pixels | 3 files + 4 tests |
| **S6c** ✅ | Presentation: swap chain + WinUI host | 2 tests create a composition swap chain, draw, read back, present | 3 files + 2 tests |
| **S6d** ✅ | Planar frames (NV12, YUV420P) + Playback Lab page | 5 tests convert synthetic planar frames to RGB and check every pixel | 3 files + 5 tests, UI |
| **S7a** ✅ | FFmpeg audio decoder | 3 tests decode a generated wave file and compare the samples byte for byte | 4 files + 3 tests |
| **S7b** ✅ | WASAPI format description + interleaving | 8 tests pin the descriptor and the interleaved byte order | 2 files + 8 tests |
| **S7c** ✅ | WASAPI renderer + device clock + synchronizer | 6 tests, two of them against the real endpoint | 4 files + 6 tests |
| **S8a** ✅ | `PlaybackPipeline` orchestration | 6 tests play real files to the end with stand-in renderers | 3 files + 6 tests |
| **S8b** ✅ | Seeking + the Lab drives the pipeline | 2 tests seek while playing and while paused | pipeline + Lab |
| **S8d** ✅ | Audio thread + bounded queue | 92/92, including the every-sample and pause tests | 1 file + pipeline |
| **S8e** ✅ | Video presentation thread + frame queue | 94/94, including the frame queue's lifecycle | 1 file + pipeline |
| **S8f** ✅ | A seek writes off what is in flight; the worker survives the end | 99/99; the regression test fails without the epoch advance | 1 file + 3 test files |
| **S8g** ✅ | A seek anchors the clock where the file resumes | 100/100; the regression test fails without the anchor | pipeline + 2 test files |
| **S7** | Audio decoder + WASAPI renderer + clock + synchronizer | Lab plays A/V in sync | large-ish |
| **S8** | `PlaybackPipeline` orchestration | Lab: seek, pause, end, errors, backpressure | medium |
| **S9** ✅ | Cutover + delete legacy engine | 105/105; the player page is untouched and runs on the pipeline | 1 file + 1 deletion + renderer fit |
| **S9b** ✅ | Device buffer + audio thread priority | 106/106; the real endpoint reports 200 ms of buffer where it held 10 ms | 2 files + 1 test |
| **S9c** ✅ | Device-resolution presentation | 125/125; the buffer is the panel's logical size at the display's scale, the swap chain carries the inverse transform, and a paused video follows a resize | 5 files + 4 tests |
| **S9d** ✅ | Idle threads wait for a change instead of polling | 126/126; the paused steady state is below the processor-time counter's resolution where it was 0.4% of a core | 1 file + 1 test |
| **S9e** ✅ | Two events: a state change carries its error, a warning is a line | 124/124 (2 skipped for the missing audio endpoint); a stall is one line and its recovery is another, and nothing is read back | 5 files + tests |
| **S8h** ✅ | Video decoding gets its own thread + packet queue | 109/109; the suite runs 7 s → 5 s because the reader no longer waits for decoding | 1 file + new queue + 3 tests |
| **S8i** ✅ | A video stall can no longer stop the sound | 110/110; the replay test fails without the fix | pipeline + 1 test |
| **S8j** ✅ | A stalled clock no longer freezes playback | 110/110; the guard is asserted not to misfire on a legitimate two-second wait | pipeline + 1 test |
| **S8k** ✅ | The decoder is allowed more than one thread | 111/111; the thread count is pinned by a test, and the seek test fails at an unbounded count | 1 file + 2 tests |
| **S8l** ✅ | The audio device is named as the last possible stall | 112/112; a stand-in device that never accepts audio ends playback with a message naming it | pipeline + Lab |
| **S8m** ✅ | The picture can no longer hold the reader hostage | 113/113; the new test fails without the fix — the reader jams after 0.57 s and only 40% of the sound arrives | pipeline + fixtures |
| **S8n** ✅ | The stall class is removed by construction, not by another guard | 115/115 twice, x64 and ARM64 build clean; the queue's contract test asserts it cannot block its producer | pipeline, queue, tests, design §9.1 |

**S4 — FFmpeg runtime and media source.** *Done.*
Added `Backends/FFmpeg/`: 4 files (`FFmpegRuntime`, `FFmpegInterop`, `FFmpegSource`,
`FFmpegSourceProviderFactory`), 3 files under `Backends/FFmpeg/Interop/` (struct layouts, entry
points, constants), plus 5 tests under `SinePlayer.Core.Tests.Playback`.

Decisions made while implementing:

1. **Struct layouts are taken from the pinned headers, not from memory.** Every field in
   `Interop/FFmpegTypes.cs` was read out of the headers shipped with the package, and only the
   *prefix* of each struct is declared, because the offsets are what make the declared fields
   addressable. `AVFormatContext` in particular had two fields added ahead of `url` that a guess
   would have missed. This is the single highest-risk file in the backend and it is annotated as
   such.
2. **FFmpeg names values itself, so the interop does not replicate its enums.** Codec ids come from
   `avcodec_get_name`, pixel and sample formats from `av_get_pix_fmt_name` /
   `av_get_sample_fmt_name`. Mapping those names to the neutral enums is version-tolerant: an
   unrecognised name degrades to `Unknown` instead of silently meaning something else. Replicating
   the multi-hundred-value `AVCodecID` enum by hand was the alternative and it buys nothing.
3. **The runtime loads its own libraries, in dependency order, by full path.** NuGet places the
   natives under `runtimes/win-<arch>/native`, and Windows would not find `avcodec`'s dependencies
   from there on its own. `FFmpegRuntime` preloads them in order, registers a `DllImportResolver`
   for the assembly, and falls back to the plain application directory when the package layout is
   absent — so the same code serves tests and the packaged app.
4. **Version mismatches fail immediately and loudly.** The interop is written against libavutil 61,
   libavcodec 63 and libavformat 63; any other major version throws at initialisation with the
   expected and found versions in the message. This is the mitigation for the pinned-version rule
   from S0, and it converts the worst failure mode (silent struct misread) into a startup error.
5. **The tests generate their own fixture.** A 0.5 s PCM wave file is written by the test, so no
   binary asset is checked in, while the assertions still run through the real demuxer: container
   id, duration, codec id, sample rate, channel count, layout, sample format, packet reading to end
   of stream, seeking, and the failure path for a missing file.

*Deferred from this step:* the Playback Lab page. With the probe covered by tests it would only
display the same data, so it moves to S6 where there is something visual to drive. Wiring the
provider into `MediaEngineService` belongs to S9.

*Verified:* full solution builds for x64 and ARM64; 50/50 tests pass, including the FFmpeg path.
**S5 — Software video decoder.** *Done.*
Added `FFmpegVideoDecoder`, `FFmpegVideoDecoderFactory` and `FFmpegVideoFrameBuffer` to the FFmpeg
backend, extended `Interop/` with `AVFrame` and the libavcodec decode entry points, and added 3 tests
under `SinePlayer.Core.Tests.Playback`.

Refinements made while implementing:

1. **Decoder factories receive the opened source.** `TryCreate(source, stream, options, …)`. A decoder
   cannot be built without the container's codec configuration — for codecs like H.264 and HEVC the
   decoder is useless without extradata — and only the demuxer has it. The official FFmpeg sequence is
   `avcodec_alloc_context3` → `avcodec_parameters_to_context(stream->codecpar)` → `avcodec_open2`, so
   the information genuinely has to travel from source to decoder. Making that dependency explicit on
   the factory signature was preferable to inventing a second opaque hand-off type or polluting the
   query surface with binary codec config.
2. **`Configure` was removed from the decoder contracts.** With the factory performing creation *and*
   configuration, a decoder that exists but is not yet configured would be a state every caller and
   every backend would have to guard. The contract is now purely: what am I decoding, is it hardware,
   flush, decode, drain.
3. **`IPlanarVideoBuffer` was added.** A CPU-decoded frame is otherwise a black box, and the renderer
   is the component that owns the graphics device. Exposing plane pointers and strides keeps the
   decoder independent of D3D while still avoiding a copy inside the decoder.
4. **`AVCodecContext` stays opaque.** It never has to be dereferenced — every libavcodec call takes it
   as a handle — so it is declared as `nint`. The same reasoning that removed the need to replicate
   `AVCodecID` removes the need for the largest and most volatile struct in the codec API.
5. **The decoder is found by name, not by id.** `avcodec_find_decoder_by_name(stream.CodecId)` closes
   the loop opened in S4, where codec ids were obtained from `avcodec_get_name`. No enum is
   replicated in either direction.
6. **Frame output format is best-effort until proven.** FFmpeg chooses the output pixel format when it
   opens the codec, so `OutputFormat` starts from the stream's declared format and is corrected by the
   first decoded frame. The tests assert the exact format rather than merely "not unknown", so the
   mapping is verified against real decoder output instead of assumed.

The tests decode a bitmap that the test writes itself: FFmpeg demuxes it as a single frame video
stream and decodes it through the same codec plumbing a video stream uses, so no binary fixture is
needed. Coverage: geometry and format of the decoded frame, readable plane pointer and stride, and
rejection of a packet that did not come from the FFmpeg backend.

*Verified:* full solution builds for x64 and ARM64; 53/53 tests pass; the decoder produces
`Bgra8`, one plane, stride ≥ width × 4.

**S5b — Decoder selection for AV1.** *Done, and the first real decode bug the Lab found.*
Point 5 above was too optimistic. `avcodec_find_decoder_by_name` returns FFmpeg's *default* decoder
for a codec, and for AV1 that default is the built-in `av1` decoder, which on this platform only
decodes through a hardware path — FFmpeg itself says so:

```
[av1] Your platform doesn't support hardware accelerated AV1 decoding.
[av1] Failed to get pixel format.
[av1] Get current frame error
```

`avcodec_send_packet` then returns `AVERROR(ENOSYS)`, which reached the Lab as "Function not
implemented" on a 1080p60 AV1 MP4. The package also ships **libdav1d**, the reference software AV1
decoder, so `FFmpegVideoDecoder` now walks an ordered candidate list per codec — `libdav1d` before
`av1` for AV1, the codec's own name for everything else — and keeps the first candidate that opens.
That is the selection policy from S3 applied inside one backend, and it is the seam explicit hardware
decoding plugs into at S12 instead of being left to decoder *ordering*.

The regression test encodes its own AV1 fixture with libaom and muxes it into an MP4, then decodes it
through the production source and decoder factories: 8 frames out of 8 packets. It is the first test
in the project that decodes a **temporally compressed stream in a container**, which the single frame
bitmap fixture never covered. The generator is test-side interop only — the engine's own entry point
list stays decoder-only.

**S6 — Direct3D 11 foundation.** *Done.*
Added `Backends/Direct3D11/` with `D3D11Device` (device, immediate context, feature level) and
`D3D11Interop` (the plumbing that turns the generated pointer-based ABI back into usable COM
objects), plus 2 tests.

The step turned on one question that had to be answered before any renderer code was worth writing:
**how the Direct3D interop is produced.** Hand-writing COM vtables means reproducing
`ID3D11DeviceContext`'s ~110 methods in exact order, where a single mistake silently corrupts memory.
The repository already depends on **CsWin32** for its Win32 calls, and it turns out to generate the
whole Direct3D 11 and DXGI COM surface correctly from the Windows metadata — so the backend uses the
same generator the application already uses, and no vtable is written by hand.

Consequences worth reviewing:

1. **CsWin32 lives in `SinePlayer.Core`** and is configured with per-platform API lists
   (`NativeMethods.x64.txt`, `.arm64.txt`, `.x86.txt`) copied to `NativeMethods.txt` at build time,
   plus a `NativeMethods.json` with `"public": true`. It is a source generator
   (`PrivateAssets="all"`), so it adds no runtime dependency and no assembly to ship. Making the
   generated types public is what lets the application project keep using the Win32 calls it already
   had after the generator moved out of it.
2. **The Direct3D entry points were added to all three per-platform lists**, since that is now the
   source of truth. `NativeMethods.txt` itself is generated and git-ignored.
3. **The Direct3D backend's own types are internal even though the generated ones are public.** The
   renderer is an implementation detail behind the neutral contracts, so the test project gets
   `InternalsVisibleTo` (added to `SinePlayer.Core.csproj`) rather than the backend getting a public
   surface it does not need.
4. **Calls currently go through runtime callable wrappers.** That is the simple path and it is what
   the tests verify; if profiling later shows RCW overhead matters on the per-frame path, switching
   CsWin32 to its unmanaged mode is contained to this one backend.
5. **The tests check that the interop runs, not that it compiles.** They create a real hardware
   device at feature level ≥ 11.0 and create a texture and read its description back through the
   generated calls, which exercises device creation, the COM hand-off of the created interfaces, and
   resource creation.

*Scope note:* presentation is not implemented yet. The renderer's frame upload, shader draw and
readback move to S6b, and the swap chain plus the WinUI host to S6c. Splitting
here was deliberate: the upload and shader path is verifiable offscreen by reading pixels back,
while the swap chain path can only be confirmed on screen.

*Verified:* full solution builds for x64 and ARM64; 55/55 tests pass, including a real Direct3D 11
device.

**S6b — Frame upload, shader draw and readback.** *Done.*
Added `D3D11VideoShaders` (the vertex and pixel programme), `D3D11RenderTarget` (an offscreen surface
plus a readback helper) and `D3D11VideoRenderer`, and grew `D3D11Interop` with the texture upload,
viewport, binding, readback and shader compilation wrappers. 4 tests.

Decisions worth reviewing:

1. **The draw needs no vertex buffer.** The vertex shader generates a full-screen triangle from
   `SV_VertexID`, so there is no input layout, no vertex buffer and no index buffer — one
   `Draw(3, 0)` covers the target. Less state to get wrong, and it is the standard way to present a
   single texture.
2. **The frame is uploaded through a box with an explicit row pitch.** Decoded rows are not always
   tightly packed, so the upload passes the plane's stride rather than assuming `width * 4`. The test
   fixture deliberately pads its rows to prove that path is exercised rather than incidentally
   correct.
3. **Point sampling is chosen deliberately.** It keeps the mapping from source texels to destination
   pixels exact, which is what makes the readback assertions meaningful and what later format
   negotiation can rely on. Linear filtering would have made "did it work" unanswerable.
4. **Shaders are compiled at runtime with `D3DCompile`.** No build step depends on the DirectX SDK,
   and the HLSL lives beside the code that uses it. Shader compilation failure is reported through
   the usual `PlaybackResult`, with the compiler's own message.
5. **`D3D11RenderTarget` is separate from the renderer** so that a swap chain back buffer is the same
   abstraction with a different owner. The renderer only ever sees a render target view and a size,
   which is why presentation can be added without touching the draw path.
6. **Readback is a first-class helper** rather than test-only code: it is what frame capture and
   thumbnails will use, and it is the only way to verify a GPU pipeline without a window.

Supported so far: packed BGRA/RGBA frames. Planar formats (NV12, YUV420P) plug into the same upload
step by adding their extra plane views and a shader branch; that lands in S6d alongside real
decoded video, which is the first source of such frames.

*Verified:* 59/59 tests pass. Four of them render a synthetic frame offscreen and read it back,
asserting **every** pixel of a solid frame byte-for-byte, that row order is not flipped, and both
failure paths (a frame with no readable planes, and a renderer with no output surface).

**S6c — Presentation.** *Done.*
Added `D3D11SwapChain` (composition swap chain, back buffer, resize, present) and
`D3D11VideoRendererFactory`, made `D3D11VideoRenderer` implement `IVideoRenderer`, and added the
app-side `SwapChainPanelRenderTarget`. 2 more tests.

Decisions worth reviewing:

1. **The contract changed: the host attaches the swap chain, not the engine.** `IVideoRenderTarget`
   dropped `NativeHandle` in favour of `AttachPresentationSurface(swapChainHandle)`. The engine builds
   a swap chain and hands over a handle; the host decides what that means for its own element. Had the
   engine kept a native handle and called `ISwapChainPanelNative::SetSwapChain` itself, Core would have
   needed a XAML-specific COM declaration — exactly the UI coupling the layering exists to avoid.
2. **The host's interop is ten lines and lives in one file.** `SwapChainPanelRenderTarget` queries
   `ISwapChainPanelNative` through the panel's `IInspectable` and calls vtable slot 3. It is the only
   place in the application that knows how a swap chain reaches a UI element.
3. **Composition rather than a window handle.** `CreateSwapChainForComposition` with
   `FLIP_SEQUENTIAL` is what lets the same swap chain serve a XAML panel, and it is why the swap chain
   can be exercised with no window at all.
4. **Resize is driven by the host's size events, not polled.** The renderer subscribes to
   `SurfaceSizeChanged` and applies the resize at the top of the next frame, which keeps the resize
   off the UI thread's event path and out of the middle of a draw.
5. **One draw path, two outputs.** The renderer draws to a render target view; whether that view
   belongs to a swap chain back buffer or an offscreen texture only changes what happens after the
   draw. Presentation is a single `Present()` call at the end, which is why the verified offscreen path
   and the presented path cannot drift apart.
6. **The swap chain can be verified without a window.** `ReadPixels()` on the swap chain reads its back
   buffer, so the tests cover creation, the attach hand-off, drawing into the back buffer and
   `Present` — everything except whether XAML actually composes the result onto the screen.

*Verified:* 61/61 tests pass. The two new ones create a real composition swap chain, confirm the host
receives the handle, draw a frame into the back buffer, read the exact pixels back, and present.

*Follow-up found by the Lab:* the swap chain was sized at device resolution — measured as 2860×1303
for a 1907×869 panel at 150% — but a `SwapChainPanel` maps its swap chain in **logical** units and
applies its own composition scale when it draws. The surface was therefore 1.5× larger than the panel,
XAML clipped it, and a 1080p video looked cropped to its top-left corner. The host now reports a scale
of 1, one surface pixel per logical unit, which is the mapping the panel actually performs.

That left the surface at logical resolution, so a display above 100% upscaled the picture. Getting
device resolution *and* a correct mapping needs a matrix transform on the swap chain
(`IDXGISwapChain2::SetMatrixTransform`, with the inverse of the composition scale) — the standard
pattern for this control. **Done in S9c**, once the priority work had finished: the panel reports its
real scale and the swap chain cancels it, so the buffer is the display's pixel count and lands on it
one to one.

**S6d — Planar frames and the Playback Lab.** *Done.*
The renderer now understands NV12 and YUV420P as well as packed BGRA/RGBA, and the Lab page exists.

Decisions worth reviewing:

1. **Planar frames are uploaded as one texture per plane.** `D3D11VideoRenderer` asks the frame for
   its plane layout and creates an `R8` texture for each luma and chroma plane, with chroma at half
   resolution for 4:2:0. Nothing in the upload path assumes a packed layout any more, which is what
   makes a software decoder's native output usable without a conversion pass.
2. **Colour conversion happens in the pixel shader, not on the CPU.** Three pixel shaders cover
   packed, semi-planar and planar sources, and the shared `ConvertYuvToRgb` does BT.601
   limited-range YUV→RGB. Doing this on the GPU is what keeps software decoding at frame rate, and it
   is the point the filter chain (S14) will join.
3. **A frame that omits a plane it claims is rejected up front** rather than sampled as garbage; the
   error names the format. A test asserts the rejection.
4. **The Lab is a page, not a window**, registered as `playback_lab` behind the same developer-mode
   gate as the dev tools page, with a menu entry beside it. It picks a file, opens it with
   `FFmpegSourceProviderFactory`, creates a decoder with `FFmpegVideoDecoderFactory`, creates a
   renderer with `D3D11VideoRendererFactory`, and runs a frame timer that reads packets, decodes and
   renders. That is a hand-written version of what `PlaybackPipeline` (S8) will do, which is why the
   page is kept out of the app's navigation for normal use.
5. **The Lab does not pace to the stream clock.** It presents one decoded frame per timer tick, which
   is enough to see pictures; pacing and audio are the pipeline's and the audio step's (S7) job.
6. **The Lab takes `BasePage`'s window id for file picking**, so it uses the same file picker as the
   rest of the app instead of a hand-rolled one.

*Verified:* 66/66 tests pass, including five that build synthetic NV12 and YUV420P frames with padded
rows, render them, and read the RGB result back within ±2 per channel for white, black and red.
*Not verified from here:* the Lab compiles and is reachable from the developer menu, but whether XAML
actually composes the swap chain onto the screen can only be confirmed by running it.

**S7a — Software audio decoder.** *Done.*
`FFmpegAudioDecoder` and `FFmpegAudioDecoderFactory` join the FFmpeg backend, `IPlanarAudioBuffer`
joins the contracts, and the interop gains two entries (`av_get_bytes_per_sample`,
`av_sample_fmt_is_planar`). 3 tests.

Decisions worth reviewing:

1. **Sample rate, channel count and layout come from the stream; only the sample format comes from the
   frames.** A decoder does not resample, so the first three cannot change; the sample format is the
   one thing a codec may legitimately choose differently from what the container declared, and it is
   read back exactly the way the video decoder reads its pixel format. The backend stays explicit
   about what it knows versus what it assumes.
2. **Planar and interleaved blocks are described, not converted.** `IPlanarAudioBuffer` mirrors the
   video one: planar formats report one plane per channel, interleaved formats a single plane, so a
   renderer can walk either without the decoder copying. Channels past the inline array are reached
   through the frame's extended data pointer.
3. **Plane sizes are computed rather than read from the frame.** `bytes per sample × samples` (times
   the channel count when interleaved) is what the API contract guarantees, and it is the number the
   renderer has to be exact about.
4. **Resampling is deliberately absent.** It belongs to the pipeline, not the decoder: the WASAPI
   renderer will state what it accepts and a resampler step will bridge the two. Deciding it here
   would have made either the engine or the renderer silently depend on FFmpeg's resampler.
5. **The decoder keeps the ordered-candidate shape** the video decoder gained with AV1 — one candidate
   today — so an audio preference can be added without touching the caller and both decoders read
   alike.

*Verified:* 70/70 tests pass. The fixture is a wave file the test writes, and the decoded samples are
compared **byte for byte** against what was written: a wave file is PCM, so resampling, a format
conversion or a wrong plane size fails the test instead of merely looking plausible.

**S7b — WASAPI format description and interleaving.** *Done.*
`WasapiAudioFormat` maps a decoded stream onto a device format descriptor, and `AudioInterleaver`
expands a decoded block into the layout a device reads. 8 tests. This is the part of the renderer that
is a pure function, which is why it is separated from the device work: it can be verified exactly
instead of by listening.

Decisions worth reviewing:

1. **The renderer will ask for the stream's own format and let the audio engine convert.**
   `AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | SRC_DEFAULT_QUALITY` lets a shared-mode endpoint open a format
   it is handed, so rate conversion and speaker mixing stay in the engine instead of in the renderer.
   That keeps this step out of the resampler business; a higher quality resampler becomes an optional
   later filter, and the renderer's contract does not change when it arrives.
2. **The only conversion the renderer performs itself is interleaving.** Decoders hand out planar
   blocks for most codecs (AAC, FLAC) and devices read interleaved, so the channels are woven together.
   That is a layout change rather than signal processing, and it is checked byte for byte.
3. **Channel masks are the KSAUDIO speaker positions**, derived from the engine's `ChannelLayout`, so a
   5.1 stream arrives with 5.1 positions and the engine places the channels where they belong.
4. **Formats that cannot be described are refused rather than approximated.** A 24 bit stream is not
   silently widened — `s24` has no equivalent in a plain descriptor — and a multi-channel stream whose
   layout the engine cannot name is refused rather than guessed at, because a guessed mask tells the
   device to put channels on speakers they do not belong to. Both refusals are tested.
5. **Everything device-facing moves to S7c.** Creating the client, the render client, the device clock
   and the volume control needed the WASAPI surface in the per-platform `NativeMethods` lists. The
   shapes CsWin32 generates are now confirmed — `PInvoke.CoCreateInstance<T>`, `IAudioClient` as a
   callable `[ComImport]` interface, and `in`/`out` extension helpers for the methods that take
   pointers — so S7c is plumbing rather than discovery.

*Verified:* 78/78 tests pass. The descriptor tests pin block alignment, byte rate, the extensible flag
and the speaker mask for stereo, mono and 5.1, plus the refusal paths; the interleaving tests pin the
raw byte order for planar input and confirm interleaved input survives untouched.
*Not verified here:* anything requiring a real endpoint. This machine may have no audio device at all,
so the device path is exercised on yours.

**S7c — WASAPI renderer, device clock and synchronizer.** *Done.*
`WasapiAudioRenderer` (shared-mode stream on the default endpoint), `WasapiAudioClock` (the device's own
position) and `DefaultSynchronizer`. 6 tests, two of which drive the real endpoint.

Decisions worth reviewing:

1. **The stream is opened with the decoder's own format and the audio engine converts**, so sample rate
   conversion and speaker mixing stay out of the engine. The renderer's only format work is
   interleaving, which was verified byte for byte in S7b.
2. **The stream is started before the first write, and that was a real bug the device test caught.** A
   stopped WASAPI stream does not consume its buffer, so writing first filled it and then waited for
   room that could never appear: the test took 31 s and failed with "the audio device did not free its
   buffer in time". Starting the stream first turned it into a 414 ms pass. This is exactly the class
   of mistake the format-only tests could not see.
3. **A block is handed over in whatever chunks the device has room for.** A decoded block is normally
   *larger* than the device buffer, so requiring room for all of it could never succeed; submission
   loops, and the wait budget restarts whenever progress is made.
4. **Waiting is deliberate, and belongs to the audio thread.** The contract hands over one block with no
   way to resume a partial write, so `Submit` waits for the device rather than dropping audio. The
   pipeline (S8) owns the thread this runs on.
5. **The clock is the device's own position**, which is what is actually being heard — including when
   the engine converts the rate. Start and pause are then free (a stopped stream simply stops
   advancing), pauses are visible but harmless, and a seek re-anchors the base.
6. **Device rate is read-side only for now.** `SetRate` re-anchors and rescales how device time is read;
   really changing speed needs `AUDCLNT_STREAMFLAGS_RATEADJUST` and belongs with the speed-control work.

*Verified:* 84/84 tests pass. The two endpoint tests run for real on this machine: the descriptor is
accepted by the device, chunks are written, the clock advances past zero and stays under a second,
pausing freezes it, and flushing succeeds. The synchronizer is covered by four table-driven tests.

**S8a — Playback pipeline.** *Done.*
`PlaybackPipeline` composes a source, a decoder per stream and a renderer per stream, and runs one
playback on a thread of its own: open, play, pause, stop, position, duration, and the state machine
(`None → Opening → Paused/Playing → Ended/Error`). `StopwatchClock` joins the timing code as the master
for files with no audio. 6 tests.

Decisions worth reviewing:

1. **Pacing is implicit, so there are no queues yet.** Audio submission blocks on the device and video
   waits on the clock, which means the file is consumed exactly as fast as it is played and no decoded
   data is buffered at all. Bounded queues earn their place when buffering and seeking arrive (S8b);
   adding them now would have been machinery with nothing to do.
2. **Components are injected rather than created.** That is what lets the same orchestration run
   against the real backends in the Lab and against stand-in renderers in tests. The tests here play a
   real bitmap and a real wave file end to end through the real FFmpeg source and decoders.
3. **Audio that will not open is a warning, not a failure.** A file whose audio the device rejects
   still plays, with the reason kept in `Warning` rather than `Error` so the state machine stays
   usable. Failing the whole open, or silently dropping the audio, would both have been worse.
4. **The host supplies the presentation surface**, because only the host knows what element the video
   is shown in; `Open` takes an `IVideoRenderTarget` and refuses a video file without one.
5. **The clock starts before the worker, and `Ended` waits for the device.** The first frame is then
   never late by construction, and the end of the stream is not declared until what is already queued
   has been heard.

*Verified:* 90/90 tests pass. The pipeline tests play a bitmap and a wave file through to `Ended`, check
that every sample reaches the renderer, that pausing freezes the position, that a refused device
degrades to a warning, and that failures are reported through the state machine rather than as
exceptions.
*Not yet:* seeking and the buffering sub-state (S8b), and the Lab still drives the decoders by hand
instead of the pipeline.

**S8b — Seeking, and the Lab on the pipeline.** *Done.*
`PlaybackPipeline.Seek` pauses for the duration so the worker cannot decode into the state being
flushed, seeks the source, flushes both decoders and the audio renderer, and re-anchors the clock. The
Lab now drives the pipeline with the real backends and has `-10s` / `+30s` buttons.

Decisions worth reviewing:

1. **Back-pressure moved out of the renderer into the pipeline.** A pause stops the device, so a
   renderer waiting for room would have been waiting on something that had just stopped — and would have
   failed after its two second budget, turning a pause into an error. The pipeline now waits for room
   *before* submitting, using a quarter second of queued audio as the watermark, so every wait in the
   engine is interruptible by pause and stop.
2. **Seeking is briefly synchronous, under a lock the worker shares.** The worker holds it only while a
   packet is decoded and handed on, which is short; the waits above sit outside it, so a seek does not
   wait for the device.
3. **Seeking keeps the state it found**: seeking while playing resumes playing, seeking while paused
   stays paused, and seeking from the end becomes paused rather than finished so Play continues.
4. **The Lab is a driver rather than a player.** It builds the pipeline from the real backends and
   reports state, position, duration, presented frames and surface size — one screen showing what the
   engine is doing.

*Verified:* 92/92 tests pass. The new tests seek while playing (resuming, then running to the end) and
while paused (staying paused, then running to the end).
*Not verified here:* whether the Lab looks and sounds right, which is yours to judge.

**S8c — Apartments: the engine owns its objects on the thread that uses them.** *Done.*
The Lab found a defect the tests structurally could not. The Direct3D device was created by the host, on
the WinUI **UI thread, which is an STA**, so its runtime callable wrapper was bound to that apartment.
The first time the playback thread touched it, the marshaller looked for a proxy, found none and failed:

```
Unable to cast COM object of type 'System.__ComObject' to interface type
'Windows.Win32.Graphics.Direct3D11.ID3D11Device' … E_NOINTERFACE
```

That is exactly why the Lab worked before S8 and stopped after it — rendering used to happen on the UI
thread — and why 92 tests could not see it, since they run everything on MTA threads where nothing is
marshalled. Audio had the same defect queued behind it.

The fix belongs in the composition, not in the renderer:

1. **The pipeline takes a video renderer factory rather than an instance**, and creates, owns and
   disposes the renderer itself. That is the right ownership for "composition + lifecycle", and it is
   what makes the graphics device belong to the engine instead of to the host.
2. **`Open` performs its setup on the playback thread**, so the source, the decoders, the graphics
   device, the swap chain and the audio client are all created in the apartment that will use them. The
   caller waits for the setup to finish; asynchronous open is a later refinement.
3. **The host marshals the surface hand-off to its own thread** and caches `SurfaceWidth`,
   `SurfaceHeight` and the scale on the UI thread instead of reading XAML properties from the engine's
   thread. Core stays thread-agnostic and the WinUI knowledge stays in one host file.

*Verified:* 92/92 tests still pass and the application builds. The apartment behaviour itself can only be
confirmed in the Lab, which is where it was found.

*Follow-up, found by the Lab on the first run after the fix:* moving setup to the playback thread
introduced a deadlock — opening blocked the UI thread while setup marshalled *to* the UI thread and
waited for it, so the two waited for each other. `Open` is now asynchronous (`OpenAsync` at the time, awaited
by the host) with a blocking `Open` kept for callers that have no message pump, such as the tests — both were
later folded into one posted call: see S9f. That is the general shape any host-side marshalling needs: the
engine must never hold a thread the host may need.

*A separate gap, unrelated to the failure:* the Direct3D renderer has no 10 bit path — `GetPlaneLayout`
covers BGRA, NV12 and YUV420P and there is no 10 bit pixel shader, although `GetTextureCount` already
lists `Yuv420P10`. The Lab's file turned out to decode as 8 bit, so this was not the cause, but 10 bit
AV1 and HEVC are common enough that it needs covering before the cutover.

*A separate gap, unrelated to the failure:* the Direct3D renderer has no 10 bit path — `GetPlaneLayout`
covers BGRA, NV12 and YUV420P and there is no 10 bit pixel shader, although `GetTextureCount` already
lists `Yuv420P10`. The Lab's file turned out to decode as 8 bit, so this was not the cause, but 10 bit
AV1 and HEVC are common enough that it needs covering before the cutover.

**S8d — Audio gets its own thread.** *Done.*
`AudioQueue` (bounded, holding up to two seconds) plus a dedicated audio thread that owns `Submit` and
its blocking back-pressure. The thread reading the file never waits on the device any more.

Why: the measured numbers said the single-threaded pipeline stalled on the device and starved video —
`presented` froze at 60 frames while 935 were dropped at 1080p60, and the device ran dry about three
times a second. Waiting for the device was stealing decode time from video. Moving that wait onto its
own thread is the fix, and it is what the design's bounded queues were always for.

Three details worth reviewing:

1. **The queue owns its buffers.** A decoded block is handed over rather than borrowed, so the reader
   never has to keep it alive; the audio thread disposes it after submitting, and `Clear` disposes
   whatever a seek abandons.
2. **A stopped stream is a pause, not a failure.** The renderer's wait for room no longer gives up when
   the device stops, because the device keeps what it holds and resumes where it left off. Giving up
   there is what used to turn a pause into an error.
3. **Pausing stops only the device.** The audio thread notices the state and stops taking work; the
   queue keeps what it has, so resuming is continuous.

*Verified:* 92/92 tests pass, including the audio test that requires every sample to reach the renderer
(so the queue drains exactly) and the pause test that requires the clock to stop.

*The defect that actually froze the picture, found by the user.* `FFmpegInterop.ToTimeSpan` clamped any
timestamp past **one second** to zero. Every video frame after the first second of a file was therefore
stamped time zero while the audio clock ran on, so the synchronizer correctly dropped all of them: the
picture froze at exactly 60 frames — one second at 60 fps — with the sound still playing. The same clamp
explains why the glitching was worst at the start, because during that first second the video was
*early*, waited on the clock, and those waits were what left the device unfed.

No test could see it: every video fixture in the suite was shorter than a second, a one frame bitmap and
an eight frame AV1 clip. The AV1 fixture is now eighty frames (two and a half seconds) and asserts that
timestamps progress past a second, and `FFmpegInteropTests` pins the conversion itself with values at
half a second, one second, twenty minutes and an hour.

The lesson worth keeping: the counters were honest — frames really were late — but the layer they pointed
at was wrong. Latency in *presentation* came from latency in the *timestamps*, and three rounds of
scheduling work were spent before anyone checked the arithmetic the scheduler compares against.
*To be measured:* whether the picture now flows at 1080p60, or whether the reader is itself decode-bound
— `dropped` and `presented` in the Lab answer that rather than a guess.

**S8e — Video gets its own presentation thread.** *Done.*
The measurements settled what nothing else had: 1.2 ms to decode a 1080p60 AV1 frame and 0.7 ms to
present it — about 12% of a frame budget — yet the device still underran once per audio block. Nothing
was compute-bound; the problem was **look-ahead**. The reader was paced by video presentation, so it
produced audio exactly in step with the device, the audio queue was always empty, and even a scheduling
hiccup became an audible gap.

Frames now go into a bounded queue (eight frames, roughly 130 ms at 60 fps) and a presentation thread
shows them as the clock reaches them. The reader is free to stay ahead, which is what keeps the audio
queue — and therefore the device — fed. Decode and presentation no longer share a thread either, which is
the shape the design asked for from the start.

*Verified:* 94/94 tests pass, including the frame queue's whole lifecycle — open, drain at the end of the
stream, seek clearing it, and stop closing it.

**S8f — Seeking no longer waits for the picture.** *Done.*
`+30s` froze the app on `lock (_mediaLock)` in `Seek`. It was a cycle between three parties, each waiting
for the next and none able to move:

| Party | Waiting for |
| --- | --- |
| the reader | room in a full frame queue, **while holding `_mediaLock`** |
| the presentation thread | a frame's moment, which cannot arrive because the seek paused the clock |
| the seek | `_mediaLock`, to flush the decoders |

Each part was behaving correctly on its own. The defect was structural: the pipeline assumed a full queue
would always drain, and it assumed no caller would ever need the lock a waiting producer held.

Two rules fix it, and they are the rules the design should have stated from the start:

1. **A seek writes off everything in flight.** An `EpochGate` counts how many times playback moved, and
   work carries the count it started under. A producer waiting for room gives up when the count moves on,
   a frame whose position is gone is not waited for, and a packet read for the old position is not routed
   into the decoders that were flushed for the new one. The count is taken *before* the lock, which is why
   it can release a producer that is holding the lock.
2. **The worker outlives the end of the file.** It used to return at EOF, so a seek landing while the tail
   was still being played out left a pipeline that reported itself as playing with no thread left to read
   it. The worker now goes idle at the end and wakes on the next seek.

*Verified:* 99/99 tests pass. The regression test (`SeeksWhileTheNextFrameIsStillWaiting`) reproduces the
freeze exactly — a manually written YUV4MPEG file at half a frame per second parks the presentation thread
two seconds away from its frame, which is what fills the queue behind it. With the epoch advance removed it
fails with *"the seek never returned"*; with it, the seek returns in milliseconds.

**S8g — A seek starts where the file resumes, not where it was asked to.** *Done.*
Seeks made the picture freeze for good about one time in four, with the sound carrying on and `dropped`
climbing all the while — the signature of a clock that is ahead of the media rather than of anything being
late by accident.

`Seek` anchored the clock to **the position that was asked for**. A demuxer does not go there: it stops at
the keyframe at or before it. The clock counts rendered audio, so it started the new position already that
far ahead of the media, and every video frame after the seek was permanently that far behind the clock.
Frames later than the 40 ms tolerance are dropped rather than shown, so the picture stopped moving for good
while the audio — which is the clock — was unaffected. Whether a given seek froze depended on how far short
of the requested position the file resumed: under the tolerance it worked, over it never did, which is the
one-in-four.

The clock is now re-anchored to the first piece of media that actually plays after a seek — the first block
handed to the device when there is audio, the first frame taken up when there is not — through
`AnchorClock`. The requested position is still set immediately so the position display does not lag behind
the click, and the honest value replaces it as soon as there is something to be honest about. `PictureLag`
(clock minus the last frame shown) and the Lab's `lag` reading make that measurable from outside.

*Verified:* 100/100 tests pass, with `StartsWhereTheFileResumesRatherThanWhereTheSeekAsked` as the
regression test. It builds an AV1 file whose keyframes libaom places by itself, then *probes* the file for a
position it cannot resume at rather than assuming one, and would skip itself if the fixture stopped offering
one. Without the anchor it fails on the clock staying at the requested position and on ten frames being
dropped; with it, playback continues from the resume point with nothing dropped.

**S9 — The player runs on the pipeline.** *Done.*
`PlaybackEngineService` now hands the page a `PlaybackEngineAdapter`, and `NativeMediaEngine.cs` is gone
along with every `Windows.Media.Core`/`Windows.Media.Playback` reference in the repository — the last of the
opaque player is out of the build. `PlayerPageViewModel` was not touched: the adapter satisfies the same
port it always used, and only the two things that port could not express needed deciding.

- **Volume and mute** are properties of the audio renderer, which the adapter composes anyway, so they pass
  straight through. **Playback rate** is not something the pipeline can do yet, and it is the one member the
  page never calls, so it refuses rather than quietly accepting a rate it would not honour.
- **The end of the file** is where the port needed the most care. The old player was asked to play again and
  rewound itself; the adapter now seeks to the start and plays, which works only because a seek is no longer
  refused by an engine whose worker had already left (S8f).
- **Position** arrives as a 250 ms timer rather than an event from a player, raised while playing and once
  after a seek — the same occasions as before, since a paused player never raised it either.

The renderer also learned to keep the picture's shape. `MediaPlayerElement` used to letterbox; the
Direct3D renderer drew a single triangle over the whole target, which stretches whatever does not match the
window. `VideoFit` now decides the largest rectangle with the frame's shape that fits the surface, and the
renderer clears the surface and draws into it — so the bars are black rather than whatever the last frame
left behind.

*Verified:* 105/105 tests pass, x64 and ARM64 both build, and the four new `VideoFit` tests pin the
geometry (wide frame in a tall window, tall in wide, an exact match, a picture larger than the surface, and
a frame with no shape at all). The renderer's own offscreen tests still pass unchanged, which is what says
the fitted path draws the same pixels when the shapes agree. **Still to do on the player page:** watching a
real file through it end to end, which is the one thing tests cannot do.

**S9b — The device gets room to be late.** *Done.*
Audio still clicked about once every three seconds with nothing else wrong: `underruns` climbing while
`dropped` stayed at zero and nothing was being seeked. Underruns with two seconds of decoded audio queued
were worth taking seriously, because the queue cannot be the reason.

An empty audio queue would need the reader to stop for two seconds, and it demonstrably never does — the
video plays on throughout. That leaves the thread that hands blocks to the device, and the device itself:
the stream was opened with the engine's default buffer, which is **one period, about ten milliseconds**.
Ten milliseconds of slack is nothing against a thread that can be kept off the processor for tens of
milliseconds by whatever else the machine is doing, and how often it is delayed is how often it is heard.

Two things changed:

1. The stream asks the device for a fifth of a second of buffer, falling back to the engine's default if an
   endpoint refuses it. The pipeline's existing 100 ms watermark then keeps roughly that much in the
   device, so a late submit has ~100 ms to spare instead of 10 ms.
2. The submitting thread runs at above normal priority, so the threads decoding video cannot take the
   processor from the one stage that must never be late.

The latency this adds is not really latency: the clock counts what has been *played*, so the picture stays
with the sound whatever the buffer holds, and a pause or a seek discards it outright. If underruns ever
reappear, the next lever is registering the audio thread with MMCSS (`AvSetMmThreadCharacteristics`),
which buys a guaranteed processor class rather than a relative priority.

The counter was also misleading in two ways — it carried over from one file to the next, and the empty
device a `Flush` leaves behind counted as a gap — so the number that appears now means *gaps in this
playback* and can be trusted when it says zero.

*Verified:* 106/106 tests pass, including a new one against the real endpoint that asks the device what it
is holding and fails below 100 ms. It reports **200 ms** here, and the old code would have reported about
10 ms. The Lab's `underruns` is the measurement that matters from here.

**S9c — Device-resolution presentation.** *Done.*

This was the last quality gap left by the cutover, and it was self-inflicted history: the first swap chain
was sized in device pixels in a panel that maps its swap chain in *logical* units, which made the surface
1.5× larger than the panel and cropped the picture to its top-left corner (S6c). The fix then — report a
scale of 1, one surface pixel per logical unit — removed the cropping and left the picture at logical
resolution, so a display above 100% enlarged it. Both halves were always needed; only the first was safe to
do alone.

The pair is now complete:

1. The host reports the panel's real `CompositionScaleX` instead of 1, so the engine allocates a buffer of
   the display's actual pixel count (a 1280×720 panel at 150% gets 1920×1080).
2. The swap chain cancels exactly that scale with `IDXGISwapChain2::SetMatrixTransform` (the inverse), so
   the panel's own composition scale lands the buffer on the display one pixel to one pixel instead of
   enlarging it a second time.

Two details made the difference between "works on my display" and correct:

- **The transform and the buffer size are one decision, so they are one call.** `ResizeBuffers` discards the
  transform, and a resize that forgot to re-apply it would silently show the picture at 1/scale — so the
  scale is a required argument of both the creation and the resize rather than something a caller may
  forget to do afterwards.
- **The transform is read back in the test.** `GetMatrixTransform` is queried on the real swap chain and
  asserted to be the inverse of the panel's scale, after creation and again after a scale change, so the
  assertion is about what DXGI holds rather than about what the engine intended.

What is still not covered: a XAML transform around the panel (the composition scale is then an estimate, as
the WinUI documentation says), and the one frame between a scale change and the next rendered frame, during
which the panel composes with the new scale on a buffer sized for the old one. Neither is visible in this
app, which applies no projection around the surface.

*Follow-up found by using it: a paused video did not follow a resize.* The size was taken up inside `Render`,
and nothing renders while paused — so the host stretched the paused picture into the window it had become.
The renderer is now asked to take a surface change up whenever nothing is being presented: it resizes, and it
draws the last frame again **from the planes it kept**, so the paused picture is the frame the user left on
screen rather than an emptied buffer. Two things fall out of that, both deliberately part of the contract
rather than of one renderer: whoever asks cannot know whether the surface moved (only the renderer hears that
event), so the call has to be harmless when nothing changed; and taking the planes back is what makes the
redraw possible without the pipeline retaining frames.

*Verified:* 125/125 tests pass, x64 and ARM64 build clean. Four tests pin the pair and the paused case: the
buffer pixels and the read-back transform at creation and after a scale change, the resize taken up while
paused, the last frame drawn again after the surface is emptied (proving it comes from the renderer's own
planes), and the pipeline offering the change up to the renderer while paused without presenting anything.

**S9d — Idle threads wait for a change instead of polling.** *Done.*
The paused picture exposed the shape of the whole thing: every thread that had nothing to do slept for a few
milliseconds and looked again — the video thread and the decoder and the audio thread every 5 ms, the reader
every 2 ms while it had read enough, and the presentation thread every 1 ms while it waited for a frame's
moment. Nothing was wrong with it except that it was a poll: a paused pipeline cost a couple of hundred
wake-ups a second, and the picture's wait cost one per millisecond for as long as a frame was early.

What replaced it is one wake mechanism for the whole pipeline. A counter (`_changeStamp`) is bumped under a lock
and every waiter pulsed whenever something changes that a sleeping thread could not discover for itself: the
state, the position, a stop, and the host's presentation surface (which the pipeline now subscribes to as well
as the renderer, for exactly this reason). A thread with nothing to do reads the stamp *before* it decides there
is nothing to do, then waits; that order is what makes the signal impossible to miss — a change arriving while
it decides is caught by the stamp check inside the wait, and a change arriving after is a pulse with a waiter
already in place. Every such wait still has a timeout, so the design's "every wait ends" rule survives a mistake
in that order: the pipeline cannot hang, it can only be late.

Two waits remain polls on purpose and are named in the design document: room in the device's buffer (a device
has no way to tell us it has played something — 5 ms), and the frame queue's dequeue, which is already woken by
the queue itself the moment a frame arrives. The reader's read-ahead check went from every 2 ms to every 15 ms
(its effective pace anyway, given the timer's own granularity), and the presentation thread's early wait now
sleeps exactly until the frame is due instead of in one-millisecond steps.

*Measured:* the paused steady state went from a measurable ~0.4% of one core to below the processor-time
counter's resolution — 0.0 ms over a ten-second window, against 78 ms over twenty seconds before — and a
disposed pipeline measures 0.0 as well, which is the point: nothing is running that has nothing to do.

*Verified:* 126/126 tests pass, x64 and ARM64 build clean. `WakesForEachSurfaceChangeWhilePausedRatherThanWaiting
ForTheBackstop` is the shape of test this needed: it raises four surface changes inside a window shorter than the
backstop and counts the times the renderer was offered one, so it fails deterministically (4 of 7 seen) when the
signal is removed and passes when it is there. It also caught a real pre-existing defect on the way: the clock's
anchor could be claimed by a frame dequeued across a seek — the "stale anchor" the plan had recorded as a
mitigation rather than a fix — and the frame now has to belong to the current epoch before it may anchor.

*Ordering defect found by review, fixed.* Three loops read the change stamp *after* the check that decides to
wait — the decoder (both its waits), the presentation thread and the audio thread — so a `Play()` landing in the
gap between the decision and the wait was seen by neither the check nor the pulse, and the resume happened at
the backstop's pace instead of at once. The reader had it right (read before the decision); the three are now
the same. No test pins this: the window is a few instructions wide, so what makes it correct is the stated order
rather than a measurement — which is its own argument for the instrumentation recorded below.

*Recorded, not implemented* (they change behaviour that is currently correct, so they wait for a reason to
touch it):

1. **Backstop fires are invisible.** A 250 ms wait that times out because a signal was missed only ever shows
   as latency. `Monitor.Wait` reports pulsed-versus-timed-out, so a counter (and `Debug.Fail` in debug builds)
   would turn "the safety net is complete" from an argument into an observation, and zero hits would be the
   evidence needed to remove it.
2. **The reader's 15 ms poll is the last one with a signal available.** The queues know when a level crosses a
   threshold; either they raise the change signal at the reader's wake threshold, or the reader computes its
   sleep from the sample rate, since the audio queue's drain rate is known.
3. **The device-room 5 ms poll can only be computed, not signalled** — a WASAPI device has no way to report that
   it played something — so if it is ever worth reducing, the sleep follows from `(queued − limit) ÷ sample rate`.

**S9e — What the engine reports is two events, and a warning is not a state.** *Done.*
The pipeline had one `Action` event called `StateChanged` and three properties on it: `State`, `Error` and
`Warning`. Two things were wrong with that shape, and the second was the one that mattered:

- **`StateChanged` did not say what changed**, so every subscriber read `State` and `Error` back from
  properties — three lock acquisitions to learn one fact, and a window in which the state could have moved on
  between the event and the reads. It now carries both: `StateChanged(MediaEngineState, string?)`, so the fact
  and the reason cannot belong to different states.
- **A warning is not a state.** The device-stall warning and the "audio was refused" warning were folded into
  the same change detection, the same event, and a `Warning` property — which meant `StateChanged` was raised
  when the *state had not changed*, that subscribers had to ask whether the state moved, and that the warning's
  lifetime was a value nobody owned: the pipeline had to remember to clear it, and a reader could see a stale
  one. Warnings are now `WarningRaised(string)` — one line, raised as it happens, several times per playback if
  that is what happens, and **nothing is kept**: whoever raises it decides what counts as a repeat (the device
  watch says so once per stall and once more when the device moves again), and whoever displays it keeps the
  last line if it wants one. `SetState` no longer takes a warning at all, and `SetWarning`/`HasWarning` are
  gone.

*Verified:* 124/124 tests (2 skipped: this machine currently exposes no audio endpoint for the two WASAPI
tests), x64 and ARM64 build clean. Two things fell out of writing it, both worth recording:

- **A stall was named at a moment nobody expected.** The device watch counted "a second without movement" from
  a timestamp that starts at zero, which meant the countdown had already expired on the first look and a
  device that had simply not had time to move yet could be called stuck — a test asserting a healthy file says
  nothing caught it. The countdown now starts at the first look of a playback, which is also what makes the
  test's own timing honest: it waits for the line rather than assuming when it comes.
- **The recovery line went missing when the file ended first**, because it was raised only while a stall was
  "expected" — and after the end of a file nothing is. Movement of the device's own position is the fact that
  ends a stall, whether or not anything is expected of it, so that is what it is raised on now.

**S9f — One queue decides everything (orchestration).** *Done.*
Playback used to be edited by whoever got there first: the reader set `Ended` when the file ran out, a media
thread called `Fail` or raised a warning directly, and a call from the host ran on the host's thread under a
`_stateLock`. Three writers is three sets of rules to remember, and every subscriber had to ask which thread a
callback would arrive on. The rules now are:

- **One queue, owned by the pipeline.** `s_orchestration` is the pipeline's own static queue
  (`TaskDispatcher.Factory.NewQueue`, the house dispatcher — no new mechanism was invented for this), shared by
  every instance, so "the state machine" is a single thread for the whole process and the calls happen in the
  order they were made.
- **Every call is posted and hands back the task of its own job.** `Open` returns the task of the open and its
  `PlaybackResult`; `Play`, `Pause`, `Seek` and `Stop` return the task of their job. A caller that needs to
  sequence its own calls awaits one; a caller that does not drops it, because what playback *did* is still
  announced by `StateChanged` and `WarningRaised`. Treating `Open` as the special case was backwards — the
  calls are the same, only one of them happens to carry a result.
- **What a posted call did is announced, never returned** — except the seek, which changes no state: the
  `Seeked` event that existed for exactly one turn was removed again, and a caller showing a position awaits
  the seek's task instead. That is the honest shape: "tell me when my own call landed" is a task, not an event
  every subscriber pays for.
- **The threads that move media only post.** The reader reads and posts `OnSourceEnded` rather than finishing
  playback itself; a failure, a warning, a starvation request are each an item of their own. Nothing decides off
  the queue, so "only the queue writes the state" holds by construction.
- **Nothing is inlined "because the caller is already on the queue."** There is no thread-identity flag and no
  `IsInOrchestration`/`EnterOrchestration` machinery: the call sites know where they are, and the core methods
  (`PlayCore`, `SeekCore`) call each other directly rather than re-posting.
- **The state is a volatile field again, not a locked one**, and `_stateLock` is gone: there is one writer, and
  a reader wants the value it sees now rather than a consistent picture of everything.
- **`Dispose` is the one call that waits** — it posts the teardown and waits for it (bounded by
  `TeardownWaitMilliseconds`), because a disposed pipeline that still holds the file open would surprise
  whoever opens it next.

*Verified:* 126/126 tests (2 skipped: no audio endpoint exposed on this machine), x64 and ARM64 build clean.
Three defects surfaced while making the change, and each was real rather than cosmetic:

- **A startup race, made visible by the queue.** `_stopRequested` was cleared by the reader thread alone, so
  the audio, video and decode threads could read the stop that `OpenCore` had just set and leave before the
  file was open — nine flaky failures in one run. The flag is now cleared before any thread starts.
- **A frame dequeued across a seek could anchor the clock** to the position it came from, because the anchor
  did not check the epoch it was claiming for. Media now has to carry the current epoch to claim anything.
- **Two tests were asserting too early**, which is how a posted call is meant to be tested: the seek tests
  waited for a state they could not see yet instead of for the call's own task, and one waited on a clock
  condition that a paused pipeline satisfies for the wrong reason. The helpers (`WaitFor`, `WaitForState`,
  `SeekAndWait`) exist for this and are the pattern for any test that follows.

**S9g — A command reports; playback fails.** *Done.*
Two sentences from review, and the second is the one that changed behaviour: *"Play, Pause, Seek and Stop
should return `Task<PlaybackResult>` like `Open` does — and a failed operation shouldn't turn the pipeline into
Error state."*

- **Every call hands back what became of its own job.** `Play`, `Pause`, `Seek` and `Stop` now return the task
  of their queue item carrying a `PlaybackResult`, exactly as `Open` does. The uniform shape is the point:
  treating `Open` as the special case was backwards, and with a result in hand a caller no longer has to infer
  from the events whether its own call happened, which is the *only* way a caller could ever learn that a seek
  was refused (a seek changes no state, so nothing else would say so).
- **What each command answers.** `Play` fails when nothing is open and when playback has ended or failed;
  `Pause` fails when nothing is open and *succeeds* when playback is already not playing, because that is what
  was asked for; `Stop` always succeeds, because there is nothing it can fail at; `Seek` fails when nothing is
  open and when the source will not move. None of these touch the state, the clock or the events.
- **`Error` is reserved for playback that cannot continue.** It was `Fail(...)` — state to `Error` plus the
  message — and it is now only reached by the reader and the media threads: a decoder that dies, a reader
  whose file went away, a device that stops consuming at the end of a file. Those are failures nobody asked
  for; a seek a source refuses is not, and turning it into one ended a playback that was still perfectly able
  to carry on. A failed *open* is the same rule from the other end: it leaves the pipeline at `None` with
  nothing open — whatever the attempt built is released through the same path a stop uses — rather than at
  `Error`, since it is the caller that holds the reason.
- **A refused seek changes nothing.** The seek now checks `IMediaSource.CanSeek` before it pauses anything, so
  a source that says it cannot move is refused for free; and when the move is attempted and fails, playback is
  resumed if it was playing, the position is not moved, and the caller gets the reason. The `PlayCore` call
  that resumes is the *core* method, so no second state change is queued behind the seek.

*Deleted:* nothing, and that is worth saying — the command results were the whole change. `Fail` remains, now
with a single meaning.

*Verified:* 131/131 tests, x64 and ARM64 build clean. Four of them are new and each pins one sentence of the
rule, so it cannot be undone by accident:

| Test | Pins |
|---|---|
| `RefusesCommandsWhenNothingIsOpen` | every command answers instead of failing playback, `Stop` included as the one that always can |
| `ReportsAFailedPlayOnceTheFileHasEnded` | a command that cannot be carried out leaves the state it was (`Ended`) and raises nothing |
| `KeepsPlayingWhenTheSourceRefusesToSeek` (both refusal paths) | a refused seek: the reason to the caller, `Playing` kept, and still playing a moment later |
| `ReportsADecoderThatDiesAsPlaybackFailing` | the other half: a decoder that dies still *is* `Error`, so the two kinds of failure are not conflated |

The two refusal paths needed a source that refuses, which no file on disk does, so the tests wrap the engine's
real source (`LiveRefusingSourceFactory`) and give it a video decoder that never produces a frame
(`SilentVideoDecoderFactory`) — the real decoder insists on its own source type. The wrapper loops the file
instead of ending it, which is what makes the test a *live* stream: playback stays `Playing` for as long as the
test needs to look at it rather than racing to the end of a fixture.

**S9h — Ownership made explicit.** *Done, from a review the user asked for.*
The complaint was exact: *"I can't tell which thread owns which variables, and which variables are shared
state. For example `_readerFilling` can be accessed by both reader thread and worker thread, but it sounds like
a reader-thread-owned variable."* Every field was audited for who writes it and who reads it, and six things
came out of it — four of them fixed here, and the doc now states the rules (§2.2 of
[PlaybackPipeline.md](./PlaybackPipeline.md)):

1. **`_readerFilling` was a thread's work-state in a field.** The reader owned it, and the queue wrote it in two
   places (`OnSourceEnded`, `ReleaseMedia`) as a nudge with no barrier in either direction. It is a local of
   `ReaderCore` now, next to the local `endedAtEpoch` that was already there, and the two queue-side writes are
   gone: a stale value only ever means "keep filling until the minimums are met" or "check `NeedsBuffering()`
   first", both of which are right after a seek.
2. **`_videoStarvedRequested` / `_audioStarvedRequested` were two threads' state behind a plain bool** — the
   stage latched what it last asked for and the queue cleared it on a seek, which is a *retraction*, the write
   with no ordering behind it. Both fields are gone: the stage now compares against the published
   `_videoStarved` / `_audioStarved`, which the queue owns and a seek already clears, so there is one writer
   for that fact and the stage keeps no copy of it. Asking twice before the queue applies the first ask is
   possible and harmless — the setter ignores a value it already has.
3. **`_deviceStallReported` was the same shape**, one field: the sound's watch set and read it and a seek
   cleared it live from the queue. It is owned by the audio thread now, which re-seeds its whole watch when it
   sees the epoch move (a device's position jumping with a seek is movement, not recovery) — so the queue
   reaches into none of it, and no keyword is doing the work a missing owner was doing.
4. **`_disposed` was written by the caller and read by the queue**, with a `volatile` in between doing the
   work of an owner that was never named — the reviewer's example, and they were right about what it means:
   *"adding a volatile doesn't automatically save our brain work on how to synchronize these fields."*
   `Dispose` now posts the teardown and the flag is written and read **only on the queue**: a second dispose is
   a second teardown item that finds the first one done, a call queued before the dispose still finds the
   pipeline gone, and there is nothing left to synchronise. (`_stopRequested` is the other half of the
   question and stays: it is per-playback, it is *read by the four media threads* as the release their waits
   are looking for, and it cannot be replaced by the state, because a stop joins those threads before it
   publishes "nothing is open" — the flag is what releases them *before* the join. Disposed and stopped are
   different lifetimes, which is why they are different fields.)
5. **`_clockLock` guarded nothing.** Every caller of `ApplyClockState` is the orchestration queue, so it
   serialised one thread with itself — while the clock object *is* shared: the queue starts, pauses and moves
   it, a stage anchors it, and the host reads its position. Deleted, and the wall-clock fallback
   (`StopwatchClock`) was made safe for that, which is what the device clock already was: it locks, and it says
   so. The field comment now carries the contract instead of a lock that only looked like one.
6. **`_audioMinimumSamples` / `_audioWakeSamples` looked like live shared state** but are set before the reader
   starts and never written again — now said out loud, together with the audio decoder they pace (which a seek
   flushes under the media lock).
7. **The invariant that justifies seventeen unlocked fields was written nowhere**: *open state is written on
   the queue only while the media threads are stopped — before they start or after they are joined*. The
   declarations are grouped by owner now (fixed / queue-owned / open state / stage-shared / device watch /
   counters) with that stated, and each stage-shared flag says which direction it travels.

A finding left alone on purpose: `_decodeDrained` is not scoped to a position, so a drain reported for the end
of the file a seek has just left behind can be taken for the one being waited for. The impact is small (the
frame and audio waits that follow still hold the pipeline) and the flag is entangled with the completed-queue
lifecycle at the end of a file, so it wants its own change rather than a drive-by one.

*What the exercise produced beyond the code:* the way these questions were being answered before was "make it
volatile", which is the last inch of a decision rather than the decision. §2.2 of
[PlaybackPipeline.md](./PlaybackPipeline.md) now asks five ordered questions instead — local to the loop;
one owner with readers; a fact about a position, carried by the epoch and re-derived by each thread; several
writers, which means `Interlocked` or a named lock; or written only while the threads are stopped, which needs
nothing. The shape to look for and remove is **a flag one thread retracts for another**, because the
retraction is a write with no ordering behind it.

**S9i — The reader owns the cursor (`_mediaLock` deleted).** *Done.*
The question that produced this was the owner's, and it was the right shape: *"Is it possible to remove the
lock by proper design?"* — seek advances the epoch, the reader notices and moves the source itself, and every
stage drops media whose epoch tag is not the current one. It is possible, and the answer is better than a
narrower lock: the lock disappears because the thing it protected (a cursor two threads moved) now has one
owner.

What changed:

- **The media lock is gone, and with it the last reason for a seek to touch the source.** `SeekCore` no longer
  moves the cursor or flushes the audio decoder; it publishes the position, advances the epoch, withdraws what
  the epoch makes false (the packet queue's "the stream has ended", the decoded tail, the starvation flags,
  the anchor, the video flush request), and answers the caller with the result of *the request*. The reader
  performs the move on the thread that owns the cursor and the decoder — so the source is read, moved, flushed
  and drained by exactly one thread, and nothing else in the pipeline touches it at all.
- **The reader stamps with the epoch it has carried out, never with the epoch the gate holds.** That is the
  invariant that replaces the lock, and it is the one that is easy to miss: a packet read from the old cursor
  must keep the old stamp, or every downstream filter is fooled at once. Until the move is made, the reader's
  epoch stays behind the gate — which is how it knows a seek is waiting.
- **The queue's answer is the request's, not the source's.** What can be known without moving anything
  (`CanSeek`) comes back in the result; what only a move can reveal is reported by the reader as a line, since
  the caller has already been answered. The trade was accepted deliberately: *"do not defer the completion to
  other thread; just use the result of SeekCore as task result."*
- **Nothing is cleared on a seek.** What the queues hold belongs to where playback is leaving, and the stages
  that take it are the ones that can tell — they check the tag before decoding, showing or submitting (the
  audio thread checks *before* anchoring, which the old clear used to cover). The one thing that had to come
  back is `PacketQueue.Reopen()`: "the end of the stream has been reached" is a statement about the position
  the reader had got to, and a seek makes it stale — without it, replaying from the end played one frame and
  ended again.
- **The frame queue keeps the epoch out of its room wait**, as decided: a renderer that has hung is not helped
  by freeing the decoder, and the wait release a seek would provide is not worth the coupling.

Two hang-and-fail bugs surfaced on the way, both in the stop path, and both are now fixed there:

1. **`StopCore` joined its threads before releasing what they were waiting on**, and the joins were plain
   (unbounded). A decoder waiting for room in a full frame queue is waiting for another thread's progress —
   exactly the shape the design forbids elsewhere — so a stop with a full frame queue wedged the global queue
   forever, and every later test queued behind it. The frame queue is now closed *before* the joins, which is
   what makes an unbounded join correct rather than merely optimistic: nothing left waiting is waiting for
   something that will not come. (Three of the four waits end on their own; this was the fourth.)
2. **`Dispose` returned without waiting for its teardown**, so a caller that deleted or reopened the file
   straight afterwards raced the teardown and hit a locked file — which is what the tests' fixtures were doing.
   It waits again, and with no bound now: the wait needs none, because the teardown cannot block on a wait that
   is not released.

*Verified:* 132/132 tests, four runs, no hangs; x64 and ARM64 build clean. The suite also gained
`RefusesAnAudioFileWhenTheDeviceWillNotTakeIt` (the file that has neither video nor playable audio is a file
with nothing to play, so the open fails with the reason) and lost an old expectation: a file whose *audio* is
refused but which has video still opens, as a warning.

*What is left of the plan, in the order it was agreed:* the decode thread flushing itself when it sees the
epoch move (which deletes `_videoDecodeLock` and `_videoFlushPending`), the audio thread flushing the device
on its own when a submit raced a seek, and the three queues losing their epoch plumbing now that their
consumers filter. Each is small, and each removes the last of what is still shared.

**S9j — The device the seek forgot to reset.** *Done, from a report: "The seek doesn't work now: after
seeking, the video looks like speeding up."*

The report was mine to answer, and it was caused by S9i — which is worth writing down, because the *checks*
that missed it are the interesting part.

*What was wrong.* Moving the seek off the queue deleted the flush the queue used to do inside its critical
section (the one that emptied the device) and did not leave anyone else to do it. Only the rare raced-submit
case flushed afterwards. So on an ordinary seek:

1. The queue repositioned the clock: `SetPosition(target)`.
2. `WasapiAudioClock.SetPosition` did what its comment said — "called after the pipeline has flushed, so the
   device is back at its start" — and set `base = target, basePosition = 0`.
3. Nothing had flushed the device, so the device was *not* back at its start: its position counted from the
   last reset, which on a real file is however long the device has been playing (minutes on the fixture
   played in the Lab).
4. The clock therefore read `target + minutes`, and the picture, which follows the clock, raced to catch up:
   frames were late the moment they arrived, so they were shown (or skipped) as fast as they could be
   decoded. "The video looks like speeding up."
5. The sound kept playing the old position's audio as well, since the device still held it.

*The fix, on the two owners.* The audio thread — the device's owner — takes the move up itself, at the top of
its loop: when the epoch has moved it lets the device go of what it holds. And `WasapiAudioClock.SetPosition`
no longer assumes a reset device: it takes the device's current position as its zero point, so the clock reads
as the position that was asked for from the moment it is asked — including while the reset is still on its
way to the thread that performs it, and for a seek while paused, where the UI reads this value. The flush is
still the device's own; the clock simply stops depending on it having happened.

*Why the suite was green.* `FakeAudioRenderer` stood in for the device with a `StopwatchClock` — a wall
clock. A wall clock cannot model the thing that was broken: a device whose travel survives a seek. The seek
repositioned the wall clock to the target and everything looked right, on a machine where the real clock was
minutes ahead. The stand-in is now a `DeviceClock`: a base plus the device's travel since it was last reset,
with `ResetPoint()` as `IAudioClient::Reset()`, and it keeps a flush count. That is a stand-in that can fail
for the right reason.

*Verified, in the order that matters:*
- A new test, `LetsTheDeviceGoOfTheOldPositionWhenPlaybackMoves`, **fails when the fix is removed** — checked
  by disabling the flush and watching it fail with "the device was never let go of the position playback
  left" — and passes with it. That check is the point: the previous design's tests were green while the app
  was broken, and the only way to know a test pins something is to break the thing and watch.
- 133/133 tests, three runs; x64 and ARM64 build clean.
- The clock's own reference to a flushed device is gone from both places that carried it (`SetPosition`'s
  comment and `WasapiAudioRenderer.Flush`'s assumption), and the pipeline document now states the rule the
  hard way: *the clock never assumes a reset device, because the reset belongs to a thread that may not have
  run yet.*

*What is left* is unchanged from S9i: the decode thread flushing itself on an epoch change, and the queues
losing their epoch plumbing once their consumers filter. The one lesson added to the pile is about test
doubles: **a stand-in that is simpler than the real thing in a way the real thing's failure modes depend on
is a stand-in that hides them.**

**S9k — The queues stop deciding, and become one queue.** *Done, from two questions: "PacketQueue and
FrameQueue still reference EpochGate — is it possible to totally move them out of the queue implementation?"
and "all 3 queue implementations are quite similar — is it possible to merge them into one class?"*

Both yes, and they are one change: the epoch knowledge moves to the two ends of each queue, and what is left
is one generic queue.

- **The queues carry a tag and compare nothing.** `MediaQueue<T>` stores `(tag, item)` and hands both back.
  The meaning of the tag is the pipeline's: the *producer* checks `_epochs.Current == epoch` before handing
  work over, and the *consumer* checks it before using what it took. That is where the checks always belonged
  — a queue that filtered was a fourth place deciding what a position meant, and the only one that could not
  see the other three.
- **The producer's check is not optional, it is the reason the merge is safe.** `QueueFrames` books every
  frame it hands over as `_tailDecodedFrameTicks` — the decoder's progress, which is what starvation is
  measured from — so a frame produced for a position playback has left must not be booked against the new
  one. (Reachable when a decode that was already running returns frames, which is what an immediate-output
  codec does, or any decoder whose output is not delayed.) While looking for a way to test that, the probe
  showed the stale frame never materialising with the fixtures we have: the flush discards what a decoder
  *holds*, and any later packet is dropped by the epoch check before it is decoded. The guard is kept for
  what it is — the honest value of a number the starvation rule reads — with the comment saying how it is
  reachable.
- **The frame queue's room wait loses its seek exit** (the user's earlier call: freeing the decoder while the
  picture is stuck helps nothing). It is now a plain `Monitor.Wait` — the 10 ms tick existed only to re-check
  the epoch — released by a consumer taking an item or by the close a stop performs. §3's table, rule 3 and
  I5 in [PlaybackPipeline.md](./PlaybackPipeline.md) now say exactly that.
- **One class, three configurations.** The policies are arguments at three construction sites:
  `new MediaQueue<MediaPacket>()`, `new MediaQueue<RawAudioBuffer>(weigh: …)` and
  `new MediaQueue<RawVideoFrame>(limit: FrameQueueCapacity)` — no limit, no limit but weighed in samples, and
  a limit. Three classes (~410 lines with comments) became one (~200), with the doc comment saying which
  configuration is which policy and why. The pipeline's field names (`_packetQueue`, `_audioQueue`,
  `_frameQueue`) and `Count`/`Weight`/`Complete`/`Reopen`/`TryPeek` keep every call site unchanged except
  for the type.
- **Dead code went with it**: `Clear()` was called by nobody since the seek stopped clearing (S9i), and the
  three near-identical dispose-the-leftovers loops are one. The three test fixtures became one
  (`MediaQueueTests`), which is what they always were: the three policies, plus the tag travelling untouched
  through a position change, plus what a close releases.

*Verified:* 130/130 tests, three runs; x64 and ARM64 build clean. The probe used to answer the guard question
(stacks and a printed timeline, then deleted) also confirmed the shapes that matter: after a seek the tail is
0 until the *new* position's first frame lands, the audio thread's flush is what resets the device, and a
pipeline whose file has ended parks at `Paused` when a seek arrives — the documented S8g behaviour, which is
why the guard's first test attempt was measuring the wrong thing.

*What is left of the S9i list:* the decode thread flushing itself on an epoch change (`_videoDecodeLock` and
`_videoFlushPending`), and the audio thread owning its own device flush for the raced-submit case (it already
owns the seek's). Neither changes what runs; both remove a lock or a flag.

**S9l — The queue stops knowing whether the stream has ended.** *Done, from one question: "Why do we need a
`_completed` flag in `MediaQueue`?"*

It was a second copy of a fact the pipeline already held. `_sourceEnded` is set in `OnSourceEnded` and
`_packetQueue.Complete()` in the `Finish()` it calls — same thread, same queue item — and cleared together in
`SeekCore` (`_sourceEnded = false;` and `Reopen()`), while the decode thread read the queue's copy to decide
"drain and idle" and the clock, starvation rule and device watch read the pipeline's. One fact, two flags, two
writes to keep in step by hand.

It is also a leftover of the pre-orchestration design: the *reader thread* used to complete the queue itself,
so the queue was a sensible place to hold "no more will come" and `Complete()`'s pulse was what released a
waiting consumer immediately. After S9f the reader only *posts* the end, and the queue worker sets both flags
— so the queue-side one became a mirror. Same history as `_readerFilling`, `_videoStarvedRequested` and the
`_disposed` volatile: the ownership moved and the flag stayed behind.

*Removed:* `_completed`, `Complete()`, `Reopen()`, `IsCompleted`, and the completion terms in both wait
conditions — so the only two things `MediaQueue<T>` can be told are "wait for room" (its limit) and "this is
closed" (a stop). The decode thread's drain-and-idle branch now reads `_sourceEnded`, which it was already
reading indirectly through the clock and starvation logic; `SeekCore` keeps the single `_sourceEnded = false`;
`Finish` has one fewer line. `MediaQueueTests`' completion test became a close test (what it still has to pin:
a closed queue takes nothing more, releases what it holds, and releases a waiting consumer), and the end-to-end
behaviour is unchanged where it matters — `PlaysAgainAfterTheEndOfTheFile` and the end-of-file drain tests
still pass untouched.

*What it costs:* the decode thread notices the end of the file up to one dequeue timeout (20 ms) later, so the
tail starts draining at most a poll later; `WaitForVideoDecodeToDrain` waits far longer than that. *What it
buys:* one less piece of state that can disagree with the pipeline, and a queue that knows neither what its
tags mean nor whether anything more is coming.

*Verified:* 129/129 tests, three runs; x64 and ARM64 build clean. The docs now state the rule as a check to
make on any change (Appendix A item 15): **a flag beside the flag it mirrors is the shape to delete.**

**S9m — The renderer was waiting for the display, and the pipeline was dropping frames because of it.**
*Done, from a report: "Frame dropped steadily, with a lag higher than usual… 2566 frame(s), dropped 279,
underruns 0, lag 70 ms, decode 0.3 ms, render 18.0 ms, surface 1871x851 at 1.5x", on a 1080p60 AV1 file.*

The numbers said where to look before any code was read: decode 0.3 ms and render 18.0 ms, against a 16.7 ms
frame interval. A stage that costs more than a frame cannot keep up, so the picture falls behind, and the drop
rule does the only thing it can. The question was what the 18.0 ms was.

*Measured first, on this machine, before touching anything:* a temporary test rendered 300 1080p YUV420P
frames through the real D3D11 renderer into a 2807x1277 offscreen target (the reported surface's physical
size) and timed `Render` — upload, clear, draw, no swap chain. **0.38 ms per frame.** The renderer's work is a
quarter of a millisecond; 17.6 ms of the reported 18.0 was *waiting*, and the only wait inside `Render` is
`Present(1, …)` — the vertical blank. (The probe was deleted again; the number lives here and in §13.)

*Why waiting for the blank breaks this pipeline.* `Present` with a sync interval of one blocks the calling
thread until the display's next blank — up to a whole interval, and on average one, since we present once per
frame at the display's rate. Three consequences, all of which the report shows:

1. **The wait is inside the presentation thread's frame, and is counted as the renderer's cost** — hence
   `render 18.0 ms` for 0.38 ms of work, and a diagnostic that blamed the renderer for a pause.
2. **At a frame rate equal to the display's, the thread has no slack at all**: its per-frame budget is exactly
   one display interval, so any loop cost at all makes the next frame late, and the lateness ratchets.
3. **The drop rule then discards frames for a delay it cannot control.** Lateness creeps past the
   synchronizer's 40 ms late tolerance, and since the decoder is far ahead (0.3 ms per frame) there is always
   a closer frame waiting — which is the drop rule's condition — so drops continue at whatever rate the
   ratchet requires. The reported lag of 70 ms is that tolerance plus one display interval plus the
   compositor's own interval; a tenth of frames dropped is what holding it there costs.

*The change:* present without the wait (`Present(0)`). The clock paces the picture (rule 4); the display only
decides what it shows, and a composition swap chain takes the newest buffer at its own composition. The call
still blocks when every buffer is in flight — the compositor being behind, which cannot be helped — but that
is the exception, where waiting for the blank was the rule. A 24 fps file on a 60 Hz display is unaffected
(the thread waits for each frame's moment either way), and a 60 fps file on a 60 Hz display goes from "the
thread is blocked every frame" to "the thread is idle nearly every frame".

*What the Lab should now show, which is also how this diagnosis is confirmed or refuted:* `render` in the
fraction of a millisecond the measurement says it is, and `dropped` staying at zero on a file the machine can
decode. If `render` stays at 18 ms, the wait is somewhere else and this entry is wrong — the honest thing for
the record to say.

*Also recorded:* §13 of [PlaybackPipeline.md](./PlaybackPipeline.md) now says what `AverageRenderMilliseconds`
contains and warns that a figure near the frame interval may be a wait hiding inside a stage rather than a
stage that is slow — the mistake this report was one step away from. §4.3 gained "the display is a consumer,
not a clock" as a rule, and §11 gained the row with the measurement.

*Verified:* x64 and ARM64 build clean, 129/129 tests, and the suite run repeatedly because the audit touched
the reader's loop. It also turned up a flaky test of S9g's own: `ReportsADecoderThatDiesAsPlaybackFailing`
waited for "playback stopped", which is *Ended* or *Error* — but the decoding thread's failure report and the
reader's end of file are two items of the same queue in whichever order they were posted, so the file can be
read out before the failure is heard. It now waits for the failure itself, which is what it was always about.

**S9n — The drops after the frame leaves the pipeline are counted too.** *Done, from a question: "Is there any
way that we can monitor how many frames has been discarded by DWM?", and the choice to put the answer in the
Lab rather than in a plan.*

`Present(0)` (S9m) made the display a consumer rather than a clock, and the price — a frame handed over that
the display never scans out because a newer one overtook it — is a drop that no counter inside the pipeline can
see. Counting it means asking the system, and answering the question meant settling three things first:

1. **Whether the system will say.** The documentation rules out one combination only — a swap chain that is
   both bitblt and windowed — so the composition (flip-model) chain answers, and the documented failures are
   `DXGI_ERROR_FRAME_STATISTICS_DISJOINT` ("an event, for example a power cycle, interrupted the gathering of
   presentation statistics") and a driver that simply will not. Both are a shrug rather than a bug to chase,
   so they leave the last answer standing.
2. **What its numbers mean.** `PresentCount` is "a running total count of the times that an image was
   presented to the monitor", and the documentation is explicit that this is *not necessarily the same* as the
   number of `Present` calls — which is exactly the difference being asked about. `PresentRefreshCount` is the
   blank the last image landed on; that one is not used, because the pipeline asks "how many reached the
   display", not "when".
3. **How far to trust it.** Documented as unreliable with several monitors or another fullscreen application,
   possibly in need of `DwmFlush` on a system with a hardware flip queue, and its counter is 32 bits and wraps.
   So it is a diagnostic: read after a present, on the thread that presents, subtracted from the first sample
   the swap chain ever gave as a baseline, in unsigned arithmetic so a wrap reads as the small number it is.
4. **What the count is anchored to**, which decides whether a baseline is needed at all and whether a resize
   can move the ground under it. The structure's own remarks settle both: "If the swap chain draws in
   full-screen mode, these values are based on when the computer booted. If the swap chain draws in windowed
   mode, these values are based on when the swap chain is created." Ours is a composition — windowed, flip
   model — chain, so the count belongs to that chain and starts near zero, and the only event that re-bases it
   is a new swap chain, which this renderer creates exactly once per playback (`Configure` attaches or resizes
   an existing one, and `ResizeBuffers` is not a new chain). The baseline is therefore the first answer rather
   than an assumed zero, and no reset has to be guessed at. (The documentation for this page is served by the
   SDK metadata as well, which is how it was read while learn.microsoft.com was unreachable.)

*Where the answer is published, and why it is a counter and not a property of the renderer.* The renderer is
asked (`IVideoRenderer.TryGetDisplayedFrameCount` — false meaning "this backend cannot tell", which is the
honest answer, not zero), and the **render thread** publishes what it answered into the pipeline right after
it publishes `PresentedFrameCount`, so `DisplayedFrameCount` is the same shape as every other counter in §2.2:
one thread that has the fact, everyone else reads it. The alternative — a reading property that reaches
through `_videoRenderer` from the host's thread — would be a second reader of a field that is written on the
open path, which is the shape S9h was about. It is null until a frame has been presented, so "no answer" and
"zero frames reached the display" stay different statements.

*And the duplicate that did not survive the same reasoning:* the first draft had the swap chain count its own
hand-overs as well, because "the count is what the frames the monitor took are compared against" — but the
pipeline already counts exactly that, from exactly the same presents, so the second count was a field beside
the field it mirrors (the shape §Appendix A item 15 is about) and was deleted. What the swap chain keeps is
only what the system said about the frames it handed over.

*What the Lab shows now:* `display took N, compositor dropped M` beside `presented` and `dropped`, where M is
the two counts subtracted. As the record of §13 says, it lags by the frame or two still in flight, and a
number that stops advancing on a machine where it used to advance is one of the documented unreliable
situations rather than a stall — which is why the absence of an answer is reported as an absence.

*Verified:* x64 and ARM64 build clean; 130/130 tests, including one that the renderer's answer is carried
through and that a renderer whose answer is zero is not confused with one that cannot answer. What the suite
cannot show is the number itself — an offscreen target has no swap chain, so the honest answer there is
"cannot tell". The measurement it is for is the Lab on a real window: `display took` should track `presented`
closely on a file that is not being superseded, and the gap should widen on one that is.

**S9o — The end of a file stops waiting, and starts being reported.** *Done, from the user's review: "The reader
always ends reading before decoder and presenter does, which leaves the entire worker queue blocked (remember
this is a global queue) until the playback actually finishes (normally a few seconds). The correct model should
be consumer-driven: after presenter presented the last frame and audio render finish rendering the last block,
they notify the worker to end the playback, so no blocking at all, and we don't even need a drained flag."*

The complaint was measurable in the code before anything else was read: the EOF item on the orchestration queue
called `WaitForVideoDecodeToDrain` (bounded 2 s), `DrainAudio`, `WaitForFramesToFinish` (2 s, with a 500 ms
clock-stall escape) and `WaitForAudioToFinish` (2 s) — up to six seconds of the *global* queue's time, spent
waiting for the tail of a file, while every other command (including this pipeline's own seeks) queued behind
it. Two of those waits were polls of a flag whose only purpose was to serve them.

The replacement is the user's model, spelled out precisely enough to build:

- **The reader drains the sound's tail itself** before it says anything. The audio decoder is the reader's own
  (§2.2), so this removes the queue's last reach into another thread's object; and it makes "the file has run
  out" a statement about a file whose tail is *already queued*, which is what lets a stage's later word about
  itself be taken at face value.
- **The queue hears about the ending and decides it** — `SourceEndedCore(epoch)` now sets `_sourceEnded`, arms
  the ending for that position and returns. It treats a stop or a stale epoch as a report that is not about
  where playback is, and drops it.
- **Three words, each from the only thread that can know it:** the decode thread's "no more frames from me for
  this position", the picture's "nothing left to show" (queue empty and nothing held), the sound's "the device
  holds nothing". Each is a message to the queue, repeated while it stays true at `EndingReportMilliseconds`
  (250 ms) — a stage cannot be asked a question it is not running to answer, and a level-triggered word makes a
  report that arrived too early a small delay rather than a lost ending.
- **The picture's word is gated by the decoder's, and by one more look at the frame queue** where the queue
  owns it: a look that happened before the tail was produced is honest and says nothing, which is exactly the
  trap a naive "the presenter notified us" design falls into (the ending arrives while frames are still inside
  the decoder, and the last of the picture is lost).
- **`_videoDecodeDrained` is gone**, replaced in the decode thread by a *local* (`producedForEpoch`) and in the
  queue by the armed ending. That flag was the S9h shape at its worst: two writers (the decode thread setting
  it, a seek clearing it), no ordering between them, so the older fact could land last and silence the next
  file's tail; and its readers were the queue's waits, which no longer exist. There is nothing to clear on a
  seek either, since a word carries the position it is about.

*What the redesign exposed, and the reason it is worth having the tests.* `LetsTheDeviceGoOfTheOldPositionWhenPlaybackMoves`
failed, and a probe showed why: the audio thread's epoch bookkeeping took up the position *without letting the
device go of the old one*, because the seek can hand it a buffer from the new position before it reaches the
top of its loop —

```
submit epoch=1 deviceEpoch=0 bufferEpoch=1     ← new position's audio, device never flushed
```

That is the S9j defect (a device left holding the old position, and a clock counting from it) in a rarely-taken
path: the mid-loop `deviceEpoch = epoch` swallowed the change. The old blocking EOF handling used to make the
window hard to hit; removing it made it easy. The fix is where the invariant belongs — the device is let go of
*before* the first block of a newer position reaches it, in the same step as the submit, not only at the top of
the loop.

*Verified:* 132/132 tests, x64 and ARM64 clean, and the suite got **faster** (21 s → 12 s) because tests that
used to wait out the bounded ends no longer do. `AnswersACommandWhileTheSoundHasNotPlayedOut` is the test for
the complaint itself (the device is jammed after it has taken the whole file, so the ending is outstanding; a
seek must still be answered within a second), and it was **falsified deliberately**: with a 3 s sleep put back
in the EOF item, before any epoch check, it fails on exactly that assertion. `EndsWhenTheDeviceHasPlayedOutTheSound`
covers the other half — the ending arrives when the sound has been heard, not when a timer says so.

**S8h — Video decoding gets a thread of its own.** *Done.*
The 4K60 file showed the next problem plainly: `decode 12.9 ms` a frame against a 16.7 ms budget with
`render 11.1 ms` behind it, and more than fifty underruns in ten seconds. Audio was glitching because the
reader was decoding video *inline*: a file interleaves its audio packets with its video ones, so a reader that
waits twelve milliseconds for a video decoder is a reader that is not reading the sound — and since that same
thread refills the audio queue, a machine at its limit takes it out on the sound first, which is exactly
backwards.

Video packets now go into a queue (`PacketQueue`, 64 of them, about a second at 60 fps) and are decoded on a
thread of their own, leaving reading as the reader's only job. Audio is decoded where it is read, because it
costs almost nothing and every hand-off is another chance for the sound to be late.

Priorities follow the same policy: the reader and the audio thread are above normal, since between them they
are what keeps the device fed, and the decoding thread is left ordinary **on purpose** — its lateness is the
one kind that can be absorbed by dropping a frame, so on a machine that cannot keep up it is the picture that
suffers and never the sound. A file beyond what the machine can decode in real time now shows up as dropped
frames and a stuttering picture, with the audio intact.

Two ordering bugs came out of the change, both found by tests rather than by ear. A decoder that had just been
flushed could be handed a packet queued *before* the flush, which it refuses outright — that is how it
surfaced. And a packet could still be enqueued after the stream was declared finished. Both are the same rule
stated once: **work carries the epoch it was acquired under, and nothing is done for a position that is gone.**
The epoch is now stored *with* each packet and each frame instead of being read again on the way out, and the
reader takes its epoch before it reads, so a seek already waiting for the lock invalidates what is being read.

*Verified:* 109/109 tests pass, including three for the new queue — order and epoch, a producer released by a
seek, and a consumer released by the end of the stream. The suite also got faster (5 s where it was 7 s),
because the reader no longer waits for decoding. What it cannot show is the point of the change: whether the
Lab's `underruns` stays at zero on the 4K60 file, which is the measurement to make next.

**S8i — A video stall must not be able to stop the sound.** *Done.*
Playback stopped outright a few seconds in: picture, sound and the Lab's counters all frozen, the app itself
responsive, and no error anywhere. Responsive with no error and nothing moving means every thread was
*blocked* rather than failed, and that the state was still `Playing`.

The cycle was this, and it was mine:

| Stage | Waiting for |
| --- | --- |
| the decoding thread | room in the frame queue, **while holding `_mediaLock`** |
| the reader | `_mediaLock`, so it could read the next packet — audio among them |
| the audio queue | the reader, so it ran dry |
| the device | audio that never came, so it stopped advancing |
| the presenting thread | the clock, to reach the frame in its hand |

Round in a circle and permanent: nothing in it can move first. The frame queue is freed by presenting, and
presenting waits for a clock that only the audio can restart — and the audio cannot be read because the reader
is behind the lock the decoder is holding.

Two things were wrong, and both are fixed. The decoding thread no longer takes the reader's lock at all — it
has `_videoDecodeLock`, which a seek takes only to flush the decoder — and it queues its frames *outside*
every lock, because waiting for frame-queue room is the one wait it is supposed to do, and doing it while
holding the reader's lock is exactly what starved the sound.

The end of the file held a second, quieter defect from the same change: the decoding thread exited once it had
drained, so anything that plays the file again — repeat one, or seeking back from the end — had packets
accepted and never decoded. It now idles at the end as the reader does, and a seek clears both the finished
flag and the drain flag so that a replay has something to decode with.

*Verified:* 110/110 tests pass, including `PlaysAgainAfterTheEndOfTheFile`, which plays a file to the end and
then starts it again and fails without the second fix. The cycle itself is prevented by construction rather
than covered by a test — the decoding thread cannot hold a lock the reader needs, because it takes a
different one — because exercising it needs a file with both audio and video, which the fixtures cannot make
yet. That gap is recorded under Risks.

**S8j — A stalled clock no longer freezes playback.** *Done.*
The freeze came back, and a thread dump said exactly where. Four states, all consistent with each other:

| Thread | Where it was |
| --- | --- |
| reader | `PacketQueue.TryEnqueue` — the video queue full, so it never reached the audio packets behind them |
| video | `_audioClock.GetPosition` — a frame waiting for a clock that had stopped |
| video decode | `FrameQueue.TryEnqueue` — the frame queue full, because nothing was being presented |
| audio | `AudioQueue.TryDequeue` — nothing to hand to the device |

and 65% of a processor core going into that second loop: 7382 clock reads in a few seconds.

The circle: the device runs dry, so it stops advancing → the frame in the presentation thread's hand is early
and *stays* early → the frame queue never drains → the decoder blocks → the packet queue stays full → the
reader never reaches the audio → the audio is late again, permanently. Every stage is patiently waiting for
the next, nothing is broken, and no error is ever raised.

The park loop was a spin as well: it waited on the resume event, which is *set* while playing, so every
iteration returned at once and the loop ran flat out on `GetPosition`.

`Present` now gives up on a frame when the clock has stood still for a fifth of a second while playing. That
is the only place the circle can be broken from, and the frame it abandons is one nobody could have seen —
the clock will never reach it. The sound restarts, the clock follows, and the picture rejoins it. The wait is
a real sleep rather than an event that was never going to block.

An earlier attempt is worth recording because it was wrong: dropping video packets when the queue was full
would have kept the reader moving, but it also throws away video that was never behind — the reader outruns
the decoder on any file, so "the queue is full" says nothing about the video being late. Reverted.

*Verified:* 110/110 tests pass, including an assertion that the guard does not misfire where it matters most —
a video whose frames are two seconds apart parks legitimately for two seconds and drops nothing. **The freeze
itself still has no test**: reproducing it needs a file with audio and video together, which is the same gap
recorded under Risks. What the guard is worth will be judged by the Lab: a stall should now cost a frame or
two and then recover, instead of ending playback.

**S8k — The decoder was using one core of the machine.** *Done.*
The freeze was fixed and the performance was not, and the evidence pointed away from the processor: 2% of it
in use while frames were being dropped, and PotPlayer playing the same 4K60 AV1 file with the same decoder
(libdav1d) without dropping one. Same decoder, different result — so the difference had to be configuration.

It was. `AVCodecContext` asks for **one thread** unless it is told otherwise (`{.i64 = 1}` is the default in
libavcodec's own option table), and this engine never told it anything, so every decoder it opened ran on a
single core. Every player that does this well asks for the machine instead. Measured on the fixture, seeking
and all: this is the difference between 12.9 ms a frame — three quarters of the budget for one frame at 60 fps,
with nothing left for the rest of the pipeline — and roughly a quarter of that.

The thread count is bounded at eight rather than left unbounded, and that is a finding rather than caution:
with the count handed to the machine (~32 here), a seek landing in the last second of a file produced *no*
frames at all. A frame-threaded decoder holds its output back by roughly one frame per thread, so a seek that
leaves less than that behind it has nothing to fill the pipeline with, and my S8g seek test caught it
deterministically. Four and eight threads pass that test; unbounded does not. Eight is enough to decode 4K in
real time and shallow enough that a seek can always refill it.

*Verified:* 111/111 tests pass, including the thread count being pinned by a test (so losing the setting again
would be loud rather than silent) and the seek test that fails at an unbounded count. What the Lab will show
is `decode` falling from ~12.9 ms to single digits; if drops persist after that, the next cost to attack
is `render`, which is a CPU upload of 12 MB per 4K frame and is the last stage still doing real work per frame.

### S8l — the audio device is where a stall can still hide ✅

*What was reported.* After seeking, the picture froze while the sound kept playing, and the drop counter crept up
without any visible drop. The thread dump then settled it, because four threads were each waiting in one place:

| Thread | Waiting in |
|---|---|
| playback (reader) | `AudioQueue.TryEnqueue` — the queue is full |
| audio | `WaitForAudioRoom` → `Thread.Sleep` — the queue is full *here* too |
| video | `FrameQueue.TryDequeue` — nothing left to present |
| video decode | `PacketQueue.TryDequeue` — nothing left to decode |

*What that means.* The audio queue is full and the audio thread is waiting for the device to accept more, so the
device is holding at least 100 ms and is not draining it. Everything downstream of the reader follows from that one
fact: the reader cannot get past the audio block, so no video packets are read, the decoder runs dry, the frame
queue empties, and the picture stops. The device is the only component here that can stop consuming without our code
appearing anywhere in the stack, which is why every one of these dumps has ended here.

*Two causes, one symptom.* The device is not consuming, and there are two ways to arrive at that: the device itself
stalled, or the stream was left stopped by a `Flush`/`Stop` whose matching start never came — the queue looks
identical either way, and both are the device's to fix from our side. Rather than guess between them, the message
names the symptom, and the recovery covers both.

*What changed.* `WaitForAudioRoom` used to wait indefinitely, which is right when the device is busy and wrong when
it is stuck — there is no difference visible from the calling side, so it is bounded now. On exceeding the bound it
nudges the stream once (`Clock.Pause()` then `Start()`, which restarts a stream that is not really running) and, if
the wait still fails afterwards, reports `The audio device stopped consuming audio.` and stops. A device stall can
now cost sound and a sentence, never silence and a freeze that never explains itself. `Finish` also no longer
reports `Ended` when something failed while the tail played out, which it would otherwise have done over the top of
this failure.

*Measured, not counted — a bug the test found.* The bound was first written as a count of `Thread.Sleep(2)` calls,
which is wrong: on Windows that sleep rounds up to the system timer, ~15.6 ms, so a "one second" bound was about
eight, and the S8f end-of-file waits counted the same way, turning a two-second bound into sixteen. The new test
failed at 2.5 s while the pipeline was still `Playing` — the honest answer being that the code was still asleep.
Every bound in the pipeline that counts sleeps now measures elapsed time instead. That the test caught this within
minutes of being written is the argument for writing it rather than reasoning about it.

*Honest caveat.* A test covers the bound and the diagnostic using a stand-in device that never accepts audio, which
is cheap because nothing there needs to be real. What cannot be tested is whether the nudge actually recovers a real
device: that needs a device that stops consuming, and no fake can say whether `Pause()`/`Start()` is enough for it.
So the failure path is proven and the recovery is not — and if the nudge turns out not to work in the field, the
user still gets the sentence above instead of a freeze, which is the part that matters.

*Counters mean one thing.* `dropped` was counting two different conditions — frames later than the 40 ms tolerance,
and frames the S8j stall guard abandons when the clock has stopped moving. Only the first is "the video is behind";
the second is "the clock stopped". They are now separate: `dropped` is the late frames, `stalled` is the frames
given up on while the clock stood still, and the Lab prints both. A nonzero `dropped` while nothing visibly stutters
was those two added together, which is exactly why the question "why is this not zero" had no readable answer.

*Verified:* 112/112 three times, x64 and ARM64 build clean, a test pins `stalled` at zero for a clock that keeps
time so the guard cannot quietly become a routine frame shredder, and one test drives the whole stall to the
message in about a second.

### S8m — the picture must never hold the sound hostage ✅

*What was reported.* After a seek the picture stuck and the sound broke up, with no error and with `dropped` at zero
while `stalled` and `underruns` climbed. The thread dump:

| Thread | Waiting in | Meaning |
|---|---|---|
| playback (reader) | `PacketQueue.TryEnqueue` | packet queue full — the reader is blocked on the **picture** |
| video decode | `FrameQueue.TryEnqueue` | frame queue full — the decoder is blocked on the presentation thread |
| video | `Present` → `Thread.Sleep` | the frame is *early*: the clock has not reached it |
| audio | `AudioQueue.TryDequeue` | **nothing to play** — the sound is starved |

*What that means.* The clock is what the picture waits for, and the clock only moves when audio plays — and the
audio comes from the reader, which the picture is holding up. The three waits are the same wait: presentation →
frame queue → decoder → packet queue → reader → no audio → clock stands still → presentation. This is the S8j cycle,
still closed, and the guard meant to break it only broke it a frame at a time.

*The rate was the bug.* The guard gave up on a frame once the clock had stood still for 200 ms — but it measured
that per frame, so each frame waited another 200 ms and the reader was freed **five frames a second against a
decoder producing sixty**. That is not enough to let the audio through: the sound starves while the picture is dead,
which is precisely the report. The clock is now remembered as stalled once noticed, and every frame behind it is
given up on immediately, so the frame queue empties at the decoder's own speed and the reader is released within
milliseconds.

*One line, one bug.* The stall time is now a field rather than a value re-read per frame, which is what makes the
difference between shedding one frame per fifth of a second and shedding while the clock is stopped. Nothing else
about the policy changed: a frame that cannot be due is still worthless, and it is still the picture that is given
up on, never the sound. `dropped` stays at zero in this state — and should: nothing is late when the clock has
stopped. `stalled` is the counter that moves, which is exactly what it was added for in S8l.

*Verified by reproduction, not reasoning.* A clock that is running and never moves is what a starved device looks
like, so the test uses one. The file is the missing fixture — **a real file with audio and video** — and with the fix
every one of its 6.67 s of sound reaches the device within 1.5 s; without the fix the reader jams after 0.57 s of
media and only 40% of the sound ever arrives, with the state stuck at `Playing` and the same `stalled`-rising,
`dropped`-zero signature as the report. That failing run is the reproduction this defect never had.

### The fixture that was missing ✅

Three defects in a row — S8i, S8g's audio side, and this one — could not be reproduced in a test because no fixture
had both audio and video, so they had to be reasoned out from thread dumps instead of measured. `RawAudioVideoFixtureFile`
writes one: uncompressed frames and PCM in Matroska, both written by hand rather than encoded, so it needs no encoder
a given FFmpeg build might lack, is intra-only so a seek lands exactly where it asks, and costs nothing to generate.
It is 200 frames, deliberately longer than the 64-packet queue, because the defect only appears once the reader can
be run into it.

### S8n — the deadlock class, removed by construction ✅ *(superseded — see below)*

> **Superseded by S8o.** The change below removed one route into the stall but added a stage whose liveness comes
> from the audio device (decoding was paced by the clock), and the next report showed a device stalling taking
> the picture *and* the sound down with it, silently. The lesson recorded there — "only presentation may wait
> for the clock" — was the right shape and the wrong scope: it is the *waits* that had to be bounded and
> one-directional, not the clock that had to be replaced. **S8o** replaces the orchestration itself.

*What was reported.* After a seek, the picture stuck again — reader in `PacketQueue.TryEnqueue`, decoder in
`FrameQueue.TryEnqueue`, presentation in `Present`, audio in `WaitForAudioRoom`. The fifth report of the same
shape, and the user's verdict was fair: *"each time you add some new patches to existing code, but none of them
permanently fix the issue… Avoid this kind of issue by design, not infinite patches."*

*The actual cause.* One circle, entered from a different side each time:

```
presentation waits for the clock
      ↑ frame queue full
decoding waits for room in the frame queue
      ↑ packet queue full
the reader waits for room in the packet queue
      ↑ so no more audio is produced
the device has nothing to play, so the clock stops
      ↳ which is what presentation was waiting for
```

The patches — the S8f epoch, the S8j stall guard, the S8l device nudge, the S8m shedding rate — each broke one
edge of that circle and left the circle. That is why each one worked and each one was followed by another report.

*What changed. The rule:* **only the presentation thread may wait for the clock, and nothing may wait for the
presentation thread.** It is now stated in the pipeline's class remarks, in design §9.1, and — the part that
matters — in what the code does:

1. **`FrameQueue` never blocks.** When it is full it discards its oldest frame and keeps the newest, because the
   oldest is the one nearest to being late. The edge "decoding waits for presentation" no longer exists, so the
   reader can never be held by the picture and the sound can always be produced.
2. **Decoding is paced by the clock, in real time.** Removing the blocking queue removed the decoder's pacemaker,
   so it has a real one: what it may work towards starts at the first frame of the current run and grows only by
   the distance the clock moves, never by more. Two things had to be got right, and a test caught the first: the
   gate must never run ahead of a seek's flush, because the flush happens on the same thread and behind the gate —
   a deadlock built out of a fix; and the clock reading has to be *recorded* when the pace is seeded rather than
   counted as movement, or the position a seek asked for gets credited as progress and the decoder runs a whole
   keyframe ahead. Measuring movement rather than the distance between the clock and the media is also what keeps
   this a pace and not an offset, so a file whose sound and picture do not start together still plays.
3. **Nothing is lost by waiting.** The gate is consulted before a packet is taken, so a frame the decoder has not
   taken is a frame it cannot drop.
4. **What used to be shed is now kept.** With pacing, a slow device stops the decoder instead of making it throw
   frames away: `LosesNothingWhenTheClockStandsStillAndThenCarriesOn` plays through a stalled clock and asserts
   not one frame is lost, where the previous design would have discarded everything decoded during the stall.

*What was removed.* The S8j/S8m stall guard and its `stalled` counter. The guard existed only to break the circle
a frame at a time, and it was itself a bug once (five frames a second, S8m); with the circle gone it has nothing
to do. `dropped` means one thing again — frames decoded and never shown, whether late or discarded for room —
and the Lab shows one number instead of two that had to be explained. The class remarks of `FrameQueue`,
`PlaybackPipeline` and design §9.1 carry the rule so the next change has something to violate on purpose.

*What is left, and said out loud.* A clock that never moves is a picture that never moves. That is correct — the
sound leads — and the one way to reach it without a device failure is a device that accepts audio and never
reports playing it, which nothing inside the engine can repair. It is now reported instead of being left as a
picture that stopped for no stated reason (`The audio device is not reporting progress.`, cleared by itself when
the clock moves again), checked from the audio thread because that is the thread whose device it is.

*Verified:* 115/115 twice, x64 and ARM64 build clean. `DiscardsTheOldestFrameRatherThanMakingAProducerWait` pins
the structural half directly — the old queue fails it — and the pipeline tests pin the pacing (no frames lost
across a stalled clock) and the new diagnosis (a device that reports no progress is named and the warning clears).

*Known race, recorded rather than fixed here.* The anchor is claimed by whoever plays first, and a seek landing
between an audio block being taken from the queue and being submitted can let that block claim the anchor with a
pre-seek timestamp — the S8g symptom, in a window up to `WaitForAudioRoom`'s bound. The fix is to carry the epoch
with each queued audio buffer (as packets and frames already do) and simply not submit one whose epoch has moved;
it is left for the next change to that path so this one stays reviewable.

### S8o — the waits, not the patches ✅

*What was reported.* The picture stuck again after a seek, for the sixth time, and the user's conclusion was
that the pipeline design itself was unfixable: *"we've been fixing this deadlock issue for a while… Please,
draft a plan for a large refactor. I think the current pipeline design is unfixable."* What followed was four
rounds of design review in which every rule below was dictated, challenged and corrected — including one
correction to the design itself (the halt had to be *lag*-driven, because a uniformly slow decoder never
produces a gap).

*The design.* Five rules, each of which says what one thread may wait for:

| # | Thread | Waits for | Ended by |
|---|---|---|---|
| 1 | Reader | The file, and nothing else — it *idles* while both queues hold their minimums | IO itself |
| 2 | Video decoding | Room in the frame queue | The picture taking a frame, or a seek moving the epoch on |
| 3 | Presentation | The clock reaching a frame's moment | Time passing |
| 4 | Audio sender | Room in the device's buffer | The device playing, or a seek moving the epoch on |
| 5 | Anyone | Another stage's progress | *Does not exist* |

*What changed, rule by rule.*

1. **The reader never drops and never blocks on a stage.** `PacketQueue` and `AudioQueue` have no maximum at
   all: the reader is the file's single interleaved cursor, so a bounded video queue is a stopped *audio*
   producer — the coupling behind several of the six reports. It is paced by its own forward reading instead
   (fill both queues to their minimums — 500 ms of audio, 64 video packets — then idle until either falls
   below half of one). The read-ahead is the bound; there is no drop path left in the reader at all.
2. **The decoder never drops and never consults the clock.** `FrameQueue` blocks at capacity again (8 frames)
   and keeps everything; the eviction path that used to make room is deleted, because discarding the oldest
   frame threw away precisely the frames the picture was about to need. The clock is gone from the video
   decode path entirely: showing a frame at its moment is the picture's business, and the decoder waiting on
   time is how a stalled device used to stop the sound as well as the picture.
3. **Only presentation loses frames, and best-effort.** It never shows a frame early; it drops a frame whose
   moment has gone *only when* `TryPeek` shows a closer one already waiting (the old code dropped on the
   synchronizer's opinion alone, which could throw away the newest frame there was); and when it cannot catch
   up it holds the clock back rather than waiting forever.
4. **The clock is held back by the lag, not by a gap.** If the newest decoded frame is more than 200 ms behind
   the clock the clock stops, and it starts again within 100 ms — a wide threshold to halt, a narrow one to
   resume, so a decoder hovering at the boundary cannot make playback stutter. A gap only exists when frames
   stop arriving, which a uniformly slow decoder never produces; a lag exists as soon as the decoder falls
   behind, which is the case that needs the sound to wait.
5. **The audio sender keeps the clock honest.** An empty audio queue stops the clock (unless the file has
   ended, where an empty queue is the sound playing out); the next dequeue starts it again. A device with no
   room is waited for without a deadline, retry or nudge, because the device *is* the clock and the whole
   pipeline already follows it.
6. **A stopped device is a pause, not a fault — but it says so.** After a second of working state with a
   claimed anchor and a position that has not moved, `WatchDeviceProgress` sets `The audio device is not
   moving.`, and clears it when the position moves. No state change, no error, and — the part the previous
   design got wrong — no special case anywhere else in the pipeline.

*Deleted.* `WaitForDecodeWindow` (the decoder's clock gate), `Present`'s drop-on-opinion path,
`Thread.Sleep`-based frame shedding, the `FrameQueue` eviction counter, `WaitForAudioRoom`'s nudge loop and
deadline, `NoticeIfTheClockHasStopped`, and `StalledFrameCount`.

*Added.* `FrameQueue.TryPeek`, `PlaybackPipeline.IsStarving` / `StarvedFor` (which stage playback is waiting
for) and `PictureLag`; the Lab prints the starving stage in the place of the stalled counter.

*What is left, said out loud.* A renderer that never returns — a GPU hang — grows the frame queue until the
decoder stops but produces no lag against the clock, so it is not treated as starvation; the pipeline waits
there until the renderer comes back. It is the one wait whose end belongs to another component, and it is
recorded in §3.5 of [PlaybackPipelineRefactor.md](./PlaybackPipelineRefactor.md) and §14 of
[PlaybackPipeline.md](./PlaybackPipeline.md), which is the durable, code-free statement of this design and the
rules a future change must not break.

*Verified:* the full suite twice (119/119, including five new or rewritten pipeline tests: a device that never
takes audio and then does, a device that takes audio and never moves and then does, a decoder two and a half
times too slow — which must hold the clock back without losing the sound — a renderer too slow to show every
frame — which must skip the frames whose moment has gone and no others — and the queue contract tests that
pin "no maximum" and "the decoder waits for the picture"), x64 and ARM64 build clean.

### What comes next

- **The 4K60 question.** If `underruns` still climbs on that file, the machine is decoding 4K60 AV1 in
  software (12.9 ms a frame) and presenting 4K (11.1 ms a frame) at the edge of its ability. Two things help
  and both are already on the list: hardware decode (**S12**) removes most of the decode cost, and a cheaper
  frame upload would remove much of the other. Neither is a substitute for the priority policy now in place,
  which is what stops a slow machine from taking it out on the sound.
- **S10.** Media info query surfaced to the port and the player — codec, bitrate, sample rate, FPS, HW/SW.
  `PlaybackPipeline.MediaInfo` already carries it; the port needs a member and the player a place to show
  it.
- **Watch for.** `PlaybackEngineAdapter` refuses `SetPlaybackRate` rather than pretending; if speed control
  is wanted, the pipeline needs rate support (the clock can already be retimed, the audio device cannot).
- **The Lab stays** as an internal dev tool: it is the only place that shows decode/render cost, drops,
  underruns and picture lag side by side.

### Phase 3 — Flexibility (the seven scenarios)

| Step | Goal | Verify | Review size |
|---|---|---|---|
| **S10** | Media info query surfaced to the port and the player | Info dialog/page shows codec, bitrate, sample rate, FPS, HW/SW | small |
| **S11** | Track selection | Switch video/audio tracks during playback | medium |
| **S12** | Hardware video decode + HW/SW switching | Toggle HW/SW at runtime; capability-driven | large (highest risk) |
| **S13** | Renderer switching | Swap video renderer / audio device at runtime | medium |
| **S14** | Filter chain + first effects | Rotation and scaling applied in-pipeline | medium |
| **S15** | Isolation hardening | Architecture test fails on an FFmpeg type crossing the boundary | small |
| **S16** | *(deferred)* subtitle rendering, plugin surface, fallback policy | — | later |

Sequencing notes:

- **S12 is the highest technical risk.** It is placed after the renderer works so there is a
  stable place to land HW frames, but it can be pulled forward to immediately after S6 if you
  prefer to de-risk early. Because `RawVideoFrame` is GPU-capable from S2, pulling it forward is
  a pure reordering.
- **S10–S14 are independent of each other** and can be reordered or parallelized to taste. Each
  adds a capability interface and wires a small amount of UI; none changes the port shape.
- **S14 is intentionally minimal**: the filter *chain* plus one or two simple filters is enough to
  prove scenario 5's architecture. Additional effects (color balancing, tone mapping) are
  incremental additions afterwards.

## 7. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| x64 and arm64 FFmpeg binaries come from different channels | Version drift between architectures | `FFmpegRuntime` asserts the major versions at startup and reports what it found; supplying the arm64 files is the remaining step |
| Native payload size (~183 MB/arch) | Package size | Decoupled boundary allows a trimmed custom FFmpeg build later without API changes |
| x86 builds but cannot play media | Known gap on 32-bit devices | Accepted and parked; the FFmpeg source problem is tracked independently of the engine design |
| A pipeline test fails about once in ten suite runs | A red build that means nothing, and a real defect would hide among the noise | Unresolved. Seen once for `PlaysAVideoOnlyFileToTheEnd` (at 47 ms, no message captured) and once for another timing-bounded test; each passes alone and in repeated runs. Prime suspect is a test bounded at a few seconds being starved when the suite follows another run back to back — the audio tests open the real device, so the suite is not cheap |
| No fixture with audio *and* video | A defect whose cycle needs both cannot be reproduced in a test — the S8i freeze is the first of them, and it had to be reasoned out rather than measured | **Resolved in S8m** by `RawAudioVideoFixtureFile`: uncompressed frames and PCM written by hand into Matroska, intra-only and 200 frames long. It has already paid for itself by reproducing the reader-starvation deadlock that reasoning had missed twice |
| D3D/WinUI interop complexity (swap-chain attach, device loss) | Schedule, stability | COM interop is generated by CsWin32 rather than handwritten, so the ABI cannot drift; the swap chain remains the step that needs on-screen verification |
| A device that stops consuming audio | The one stall that can still stop the picture — and it is supposed to | **S8o:** this is no longer a special case. The device *is* the clock, so a device that takes nothing is time that has not moved: the audio thread waits for room without a deadline, the picture waits for a clock that is not moving, the reader idles once both queues are full, and nothing is dropped or failed. **S9e:** it is said as one line, `The audio device is not moving.`, raised on the audio thread a second into the stall and paired with `The audio device is moving again.` when the position moves |
| A device that accepts audio and never reports playing it | Every wait in the video path ends at the clock, so the picture stops and cannot be told to carry on | Same case as above, and the same answer: a wait that is visible (`StarvedFor`) and reported (one `WarningRaised` line). Nothing more can be done from inside the engine, and the report is what turns a mystery into a device problem |
| An audio block can claim the clock anchor with a pre-seek timestamp | The picture would sit behind the sound by up to a keyframe, which looks exactly like a seek that half-worked | Mitigated in S8o: `Seek` re-arms the anchor *after* flushing, so a claim made for the old position cannot outlive the seek — the next block or frame through `AnchorClock` re-points the clock at where playback now is |
| Every stage's liveness derives from the audio device | A device that stops reporting stops the picture *and* the sound, with no message: the sixth report | **Resolved in S8o** — the answer was not to give playback a second source of time but to make every wait end somewhere the device does not own: the reader waits only on the file, the decoder only on the picture, and the two waits that do end at the device are the ones that are supposed to (and are named while they last). See [PlaybackPipelineRefactor.md](./PlaybackPipelineRefactor.md) |
| A/V sync and clock correctness | Core quality | Intro S7; keep `IPlaybackClock`/`ISynchronizer` swappable so alternatives can be compared |
| Silent coupling to FFmpeg over time | Loses the main design goal | Hard rules in §4.2 + architecture test in S15 |
| Long gap before user-visible payoff | Review fatigue | Playback Lab gives visible progress from S4 |

## 8. Definition of done (v1)

1. No `Windows.Media.Playback` / `MediaPlayerElement` usage remains in the repository.
2. All engine code lives under `SinePlayer.Core/Playback`; no FFmpeg type escapes
   `Backends/FFmpeg` (enforced by test).
3. `PlayerPageViewModel` differs from today only by the `using` line plus the deliberate feature
   wiring added in S10+.
4. Scenarios 1–7 are reachable through the engine's capability interfaces and verified in the app.
5. Subtitle, plugin, and fallback hooks are present as contracts/slots but unimplemented.
