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
| 5 | High-performance post-effects | **Audio half** — the chain, proven by a volume filter |
| 6 | Select video/audio/subtitle channels | **v2** |
| 7 | Query playback info | Yes |

**Deferred to v2 — see [MediaEngineV2Plan.md](./MediaEngineV2Plan.md).** Subtitle rendering, the plugin surface,
the fallback policy, track selection (scenario 6) and the video half of scenario 5. That document records, for
each, why it is not v1 and what v1 leaves in place, so adding it is additive rather than a redesign.

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
7. **No app code until S14 is finished** (rule added during S13d; **the gate is lifted** — S14 is done, so surface
   work may proceed). Until then: engine, tests and the Playback Lab only, because the Lab is where a new capability
   is exercised, and the player page, its view model and the settings surfaces wait. A capability that needs a
   user-facing switch therefore finishes its engine half and parks the surface, rather than reaching into the player
   to prove itself.
8. **The resolver is the only source of what to use** (rule added during S14d). A host registers components,
   orders them, and sets preferences on `PlaybackPipeline.Components`; nothing else says which decoder or
   renderer runs, and the pipeline holds no choice of its own. Registration order **is** the order — nothing in
   the engine re-sorts it, which is what makes "I chose this one" answerable. Changing what is *playing* takes
   one more step: `Reconcile()`. Registering alone changes nothing, so several edits can be made and applied
   once. The resolver is also the only place the **candidates** come from (rule added during S14d-6, restated in
   S14d-13): it answers with the creators to try, each of which makes its own component, so the pipeline can never
   pick one thing and describe another. Walking that sequence is the pipeline's (rule added during S14d-13): a
   creator whose signature is the one running means nothing is built, and a creator that cannot make what it
   offered is passed over for the next. Whether a component is a **change** is the component's own answer, through
   `Signature`: a replacement that cannot be told apart from what is already running is not a swap, and the
   running one stays. A factory describes what it would make before making it (rule added during S14d-8), so
   asking "is this a change?" never costs the thing being asked about. Nothing in the registry is labelled (rule added during S14d-7): a role is the factory
   interface, a registration is the instance, and neither a display name nor an enum stands between them.

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
│  └─ Registry/  IComponentRegistry, IComponentFactory, IComponentCreator,
│                ISourceProviderFactory, IVideoDecoderFactory, IAudioDecoderFactory,
│                ISubtitleDecoderFactory, PlaybackPreferences
├─ Internal/                      # orchestration + cross-cutting implementation
│  ├─ Registry/  ComponentRegistry.cs
│  ├─ Selection/ ComponentResolver.cs
│  ├─ PlaybackPipeline.cs, AudioFilterChain.cs, PacketBuffer.cs, FramePool.cs
│  └─ Timing/  AudioClock.cs, VideoClock.cs, DefaultSynchronizer.cs
└─ Backends/                      # one folder per backend; the only place its types appear
   ├─ FFmpeg/       FFmpegRuntime, FFmpegInterop, FFmpegSource, FFmpegSourceProviderFactory,
   │                FFmpegPacketBuffer, FFmpegVideoDecoder, FFmpegVideoDecoderFactory,
   │                FFmpegVideoFrameBuffer
   │  └─ Interop/   FFmpegTypes, FFmpegNative, FFmpegConstants   (pinned ABI, read from headers)
   ├─ Direct3D11/   D3D11Device, D3D11Interop, D3D11VideoShaders, D3D11RenderTarget,
   │                D3D11SwapChain, D3D11VideoRenderer, D3D11VideoRendererFactory
   │                (COM interop generated by CsWin32 from NativeMethods.$(Platform).txt)
   ├─ Wasapi/       WasapiAudioRenderer
   ├─ Gain/         GainAudioFilter (S15)
   └─ Null/         NullVideoDecoder, NullAudioDecoder, NullVideoRenderer, NullAudioRenderer
                    (and one factory for each: a component that does nothing, for switching tests)
```

Namespaces match folders exactly (`SinePlayer.Core.Playback.Abstractions.Decode`, …), which makes the
isolation rules below visible in every type name.

### 4.2 Dependency rules (enforced by review, and by the architecture test from S16)

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
enforces it by convention + a cheap architecture test (S16) that reflects over
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
| Architecture coverage | **win-x64 only** (`runtimes/win-x64`; PE machine `0x8664` for both the runtime and `build/native/bin` copies) — arm64 comes from `src/SinePlayer.Core/native/win-arm64`, and the package carries no 32-bit assets at all |
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

**Architecture decision — x64 and arm64 supported, x86 dropped.** The imported package covers
**x64 only**, which reproduced as a real gap: an x64 build of the app carries the FFmpeg DLLs, an
ARM64 build carried none. Neither BtbN nor any other public Windows channel publishes a 32-bit
FFmpeg build for the ABI the interop is written against, so the two architectures that are supported
are each supplied by whatever can supply them:

| Architecture | Availability | Handling |
|---|---|---|
| x64 | Available — BtbN/FFmpeg-Builds, master channel, `win64-gpl-shared` | **Supported**; the seven DLLs are placed in `src/SinePlayer.Core/native/win-x64` by hand |
| arm64 | Available — BtbN/FFmpeg-Builds, master channel, `winarm64-gpl-shared`; no NuGet package carries it | **Supported**; the seven DLLs are placed in `src/SinePlayer.Core/native/win-arm64` by hand |
| x86 (32-bit) | **Not available** — BtbN ships `win64` and `winarm64` only, gyan.dev states "all builds are 64-bit", and MSYS2's `mingw-w64-ffmpeg` has `clang64`/`clangarm64`/`mingw64`/`ucrt64` repos and no 32-bit one | **Dropped** — solution configurations, project `Platforms`, `RuntimeIdentifiers`, publish profile, `NativeMethods.x86.txt` and the two `#if x86` interop branches are gone |

x86 was dropped rather than parked because supporting it would mean building and maintaining FFmpeg
9.x for win32 ourselves — a toolchain, a build pipeline and a third copy of ~105 MB of native code —
for an architecture Windows 11 does not have and Windows 10 has retired. `FFmpegRuntime` now names
the architectures it has a build for: asking it for an x86 process raises
`NotSupportedException("This build has no media engine for X86 …")` instead of failing later on a
missing file.

**Provisioning.** Neither architecture's libraries come from a `PackageReference`: `FFmpeg.GPL` was
removed, and both builds read `src/SinePlayer.Core/native/win-<arch>` — a folder `.gitignore` excludes,
because it is ~190 MB per architecture and reproducible from two pinned archives. The README's
development steps carry the two URLs and where each archive goes. Both archives are the **master
channel** of BtbN/FFmpeg-Builds — the same channel the removed NuGet package was built from, pinned to
one autobuild so the bytes never move — one commit (`31855b2a70`) for both architectures, so the two
cannot drift.

The runtime resolves `runtimes/win-<arch>/native` then the application directory, and names the missing
files when it cannot find them, so a wrong folder is a sentence rather than a crash. What keeps a drop
honest:

1. **The build pins what it packages.** `SinePlayer.Core.csproj` maps `Platform` → `FFmpegNativeRid`,
   and its `None`/`Link` pair copies that one folder to `runtimes/<rid>/native` for the output and the
   publish directory. Without the `Link` the files would land at `native/<rid>/` — where the runtime
   does not look — which is what the packaging is for.
2. **`FFmpegRuntime`'s expected majors** (63/61/63) refuse a mismatched drop before it can corrupt
   memory, which is why a newer FFmpeg has to be adopted here, in this document's company.
3. **`FFmpegRuntimeTests`** reads the PE machine type of the libraries the process actually loaded and
   compares it with the process architecture — the one mistake a file listing would not show.

*Verified:* both outputs carry the seven libraries under their own `runtimes/win-<rid>/native`, PE
machine `0x8664` and `0xAA64`; the x64 suite passes end to end on the downloaded binaries; swapping an
arm64 library into the x64 set fails the runtime test.

*Rejected — fetching them during the build.* Two shapes were tried and removed: committed binaries
(105 MB of history every clone pays for) and a `prebuild.ps1` that downloaded them before compiling
(the build then depended on network access, on GitHub's anonymous rate limit, and on ~150 MB per clean
build). A folder filled by hand, once, from two documented URLs is the honest middle: no history
weight, no build-time network, and the instructions are the only thing in the repository.

**The channel is not interchangeable — release branches cannot decode to a drawable texture.** Measured
by swapping only the DLLs underneath an unchanged suite:

| Build of FFmpeg 9.0 series | Decoder textures bindable as | Hardware frames draw? |
|---|---|---|
| `ffmpeg-N-128094-g31855b2a70-win64-gpl-shared` (master) | decoder **and** shader resource | yes — 318 tests pass |
| `ffmpeg-n9.0.2-36-g6882969ce9-win64-gpl-shared-9.0` (release/9.0) | decoder only | no — "The device does not offer a shader view of the decoded texture" |

A texture created for decoder output alone cannot have a shader resource view, so the renderer refuses
a frame that decoded correctly: the GPU path decodes and then fails to present. Master's
`hwcontext_d3d11va.c` asks for `D3D11_BIND_SHADER_RESOURCE`; the release branch does not. Both channels
report the same majors, so `FFmpegRuntime` accepts either and the defect appears only as a picture that
never arrives. This is why the arm64 folder uses the master build as well: it was the 9.0 build that was
wrong, not the vendor, and an arm64 machine would have hit the same wall.

*Attempted and abandoned:* patching the flag ourselves. The frames context does not exist right after
`avcodec_open2` in either build (it is created lazily on the first decode), so nothing can be set there;
the documented route is to provide the frames context (`avcodec_get_hw_frames_parameters` → tinker with
`AVD3D11VAFramesContext.BindFlags` → `av_hwframe_ctx_init` → assign `hw_frames_ctx`), which is three
more P/Invokes and unnecessary once the right build is pinned.
**Known cost.** The GPL build is large: `avcodec-63.dll` alone is ~114 MB (x64) / ~61 MB (arm64), and
a full set is ~183 MB / ~105 MB, the arm64 half of which is now in the repository's history. This is
a packaging concern, not an API concern — the isolation
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
including the solution configurations, publish profile, `NativeMethods.x86.txt` and the two
`#if x86` interop branches in the window's message loop; the arm64 native libraries are now
provisioned and packaged by the build.
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
   without a live pipeline. *Amended in v2:* the contract was removed. It promised decoupling it could not
   deliver — a switch reads the live source, the media info and the clock, so there is nothing to exercise
   without a pipeline — and with one sealed implementer and no consumer written against it, the interface only
   added a second name for `MediaInfo`. `SelectStream` is a member of the pipeline, as `Seek` is.
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
   (`NativeMethods.x64.txt`, `.arm64.txt`) copied to `NativeMethods.txt` at build time,
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
   *(Revised since: 36 programmes registered up front cost 169 ms per renderer — 36 compiles of the same 14 KB
   source, 14 of them of entry points already compiled. Measured at 6.4 ms for a heavy tone-map programme against
   0.75 ms for a one-line one, so almost all of it is re-parsing. The registry now holds entry-point names,
   compiles each distinct one at most once per process, and makes the device object on first use: a renderer is
   34 ms cold and ~14.5 ms warm, of which 7.8 ms is the device. `ValidateProgrammes` compiles all of them in a
   test, which is where "every combination works" is proven instead of at every open.)*
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
   gate as the dev tools page, with a menu entry beside it (**since moved: the Lab is opened by a button on the
   Dev tools page**, which is where developer surfaces belong, so the main menu carries one entry instead of two).
   It picks a file, opens it with
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

1. **The renderer asks for the stream's own sample rate and lets the audio engine convert it.**
   `AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | SRC_DEFAULT_QUALITY` lets a shared-mode endpoint open a format
   it is handed, so rate conversion stays in the engine instead of in the renderer. That keeps this step
   out of the resampler business; a higher quality resampler becomes an optional later filter, and the
   renderer's contract does not change when it arrives.
   **Speaker mixing does not stay in the engine, though — measured, not assumed.** An endpoint mixes in a
   fixed number of channels (two, on the machine this was measured on), and a 5.1 stream handed to it
   arrives about ten decibels quieter than it left the decoder: the engine's fold-down divides the level
   down until no arrangement of the source could clip. A front channel measured −10.6 dB against what was
   submitted and the centre −13.6 dB, and an eight-second film excerpt played through the renderer peaked
   at −15.4 dBFS where the same excerpt downmixed by ffmpeg peaks at −5.14 dBFS. Damping the dialogue's
   channel is exactly what a viewer hears as "this player is quiet", so the mix is folded down here —
   front channels at unity, centre and surrounds at −3 dB, the low-frequency channel dropped — and the
   device is opened as a stereo stream so only the rate is left to the engine. After the change the same
   excerpt measures −5.13 dBFS, matching the ffmpeg reference.
2. **The renderer's only format conversion is interleaving; the fold-down is an audio filter.** Decoders hand
   out planar blocks for most codecs (AAC, FLAC) and devices read interleaved, so the channels are woven
   together — layout arithmetic rather than signal processing, checked byte for byte (`AudioInterleaverTests`).
   Folding a surround mix down is sample arithmetic too, so it lives in `DownmixAudioFilter`, an `IAudioFilter`
   with no device, no WASAPI type and no pipeline in it.
   
   **The formats are negotiated rather than assumed, which is the contract that makes it a filter.** An
   `IAudioFilter` is now told both the format of the block it is given and the format the stage after it can
   take, and it reports the format it hands on (`OutputFormat`); the chain prepares each filter for what the one
   before it produced. `IAudioRenderer` answers `Negotiate(stream)` with the format its endpoint plays — the
   stream itself when it fits, or the same stream narrowed to what the device has.
   
   **The host adds the fold, and the pipeline knows no filter.** The pipeline asks the renderer, points the host's
   chain at that target, and leaves the opening to the audio render thread — the one thread that opens or reopens a
   device, following the chain's format version when a filter moves it mid-playback; `CoreEngine` adds a
   `DownmixAudioFilter` next to its gain filter, exactly as it would add anything else. The filter decides for
   itself whether the stream needs changing, so a 5.1 stream on a two-channel endpoint is folded before it reaches
   WASAPI, a stream that already fits costs one reference, and a layout whose speaker positions are unknown keeps
   the engine's conversion rather than being guessed at. `DownmixAudioFilterTests` checks the coefficients, the
   clamping, the format it keeps and the block it hands straight back; `FoldsTheMixDownToWhatTheDevicePlays` and
   `HandsTheStreamOverUnfoldedWhenTheHostAddedNoFilter` check the two halves of that arrangement, with a stand-in
   device that plays two channels and a file that has six.
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

1. **The stream is opened with the decoder's own sample rate and the audio engine converts it**, so rate
   conversion stays out of the engine. Speaker mixing does not: a surround stream is folded down to what
   the endpoint actually plays before it is handed over (S7b), because the engine's own fold-down costs
   about ten decibels of level.
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
   usable. Failing the whole open, or silently dropping the audio, would both have been worse. *Amended in v2:* the
   pipeline no longer decides this. Choosing a track and building its chain is one command, and a chain that cannot be
   built fails it — the pipeline cannot know whether the host would rather have the picture alone. `CoreEngine` asks
   for each kind in turn and plays what works, which is the same outcome with the decision where it belongs; what was
   removed is the half-published selection the warning left behind.
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
(Decision 3 is superseded by S9z: a seek now always leaves playback paused and the host's one `Play` decides
whether it goes on — the app's "a jump while playing keeps playing" lives in the adapter.)

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
AV1 and HEVC are common enough that it needed covering before the cutover. *(Since closed: ten-bit planar
and P010/P016 frames have programmes of their own, and an HDR stream is tone-mapped — see §14.16 of
`PlaybackPipeline.md`.)*

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
test needs to look at it rather than racing to the end of a fixture. (The third row's test is now
`RefusesASeekTheSourceWillNotCarryOut`, renamed by S9z because a refused seek no longer keeps playback playing:
a seek pauses, and a refusal leaves that pause standing.)

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
6. **`_audioMinimumSamples` / `_audioWakeSamples` looked like live shared state** but were set before the reader
   started and never written again — now said out loud, together with the audio decoder they pace (which a seek
   flushes under the media lock). *Amended in v2:* they are written again, because a track can be switched and the
   read-ahead is measured in the playing stream's own samples. The write is the queue thread's, inside the one place a
   selection becomes the one in effect, and it happens before the wake that follows any publication — so a reader sees
   the new number from the next wake and, at worst, acts on the previous one for a single turn if its own backstop
   woke it first.
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

*Also recorded:* §13 of [PlaybackPipeline.md](./PlaybackPipeline.md) now says what `AverageVideoRenderMilliseconds`
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
- **The queue hears about the ending and decides it** — `NotifySourceEnded(epoch)` now says the file has run
  out, arms the ending for that position and returns. (It states the end with the position it is about —
  `_endedEpoch` — which is what S9q changed from a boolean.) It treats a stale epoch as a report that is not
  about where playback is, and drops it.
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
covers the other half — the ending arrives when the sound has been heard, not when a timer says so. (The
*words* this step introduced are superseded by S9w: a stage no longer repeats its word level-triggered and the
decode word is gone, because the end of a stream is now handed to a consumer as an entry of its queue. What
survives is the model: nothing waits, the ending is earned, and a command is answered while the tail plays.)

**S9p — Four questions about shared state, and what they found.** *Done, from the user's review: "1.
`_tailDecodedFrameTicks` has two writers during playback, reconsider your design; 2. `_clock?.SetPosition(position);`
why clock can be set by seek directly before it is anchored? 3. `_videoFlushPending` has only one consumer and
one writer, why cannot use epoch? 4. `SignalChange();` is already contained by PauseCore and PlayCore, why the
duplicated work?"*

All four were right, and two of them were the same defect seen from different sides.

**1 and 3 are one change: the epoch is the request.** `_videoFlushPending` (written by the queue under
`_videoDecodeLock`, taken by the decode thread under the same lock) existed to say "the position moved — let
your decoder go". That is what the epoch *is*, and the decode thread is the only reader, so its memory of
having done it belongs in a local of its loop (`flushedForEpoch`), exactly like `producedForEpoch`. The flag,
the lock and one writer of `_tailDecodedFrameTicks` all go: the clear now happens on the thread that owns the
decoder, in program order with that thread's own writes — which is what the tail needed, because two writers
(the decode thread publishing progress, the seek retracting it) can land in either order, and the old
position's tail surviving a seek makes the starvation rule hold the clock back for the distance the seek
travelled. §2.2 already said the decoder "belongs to the decode thread, which flushes it when it sees the epoch
move"; the flag and the lock were leftovers from before that was true.

The take-up happens twice per iteration and that is deliberate: at the top of the loop, and again immediately
before a decode — because a seek that lands while the thread waits inside `TryDequeue` can hand it a packet
from the new position, and that packet must not reach a decoder still holding the old one. Exactly the fix the
audio thread needed for its device last step, which is a good sign that the rule is the general one.

**2 is not the same defect but the same family: a decision reading a value that is not yet true.** The seek sets
the clock so the host can see where playback was sent, and because a seek while paused has no media coming to
answer with. But the clock only becomes a fact about *media* when the first media of the position anchors it,
and until then it reads the position the file was *sent* to — which a demuxer lands before. The drop rule was
the one decision that did not wait for the anchor (starvation already had the guard), so a seek could make the
picture skip the keyframe distance: frames "late" against a position nothing was playing. It now waits like
starvation does.

**4 is duplication, and it was real in two of the three cases.** `SetState` signals on every change, so a seek
that paused and resumed playback woke every thread twice after publishing the same facts, and a seek from the
end of the file woke them from its state change as well. Only a seek while *paused* — where nothing about the
state changes and everything about the position does — needs the seek's own signal, and that is now the only
branch that takes it. The rule is one wake per change: the state change where there is one, the change signal
where there is not.

*Verified:* 132/132 tests, x64 and ARM64 clean. No test pins 1–3, and saying so is more useful than a
pretend-one: each closed window is shorter than a frame on any local source (a wake-up, not a wait), and the
only way to make them deterministic would be a seam in the pipeline that exists for the test's sake. What the
suite does prove is that nothing was disabled by the guards: the drop tests, the starvation tests and the two
seek-position tests all still pass, and the guards themselves are now stated as rules in §4.3, §6 and
Appendix A (items 18 and 19).

**S9q — The end of a file stated with its position, and a stop that says what it means.** *Done, from the
user's review: "1. Consider also advance epoch on StopCore, so NotifySourceEnded won't have to query
`_stopRequested` (which breaks symmetry); 2. The epoch value provided to VideoProducedCore is acquired after
querying `_sourceEnded`; if the `_sourceEnded` is published through epoch (which is your current check),
`_sourceEnded` have to be acquired after snapshot the epoch, and reset before advancing epoch."*

**1. The stop is a position statement like any other.** `NotifySourceEnded` guarded on two things — the epoch
(about the position) and `_stopRequested` (about the pipeline) — and the second was there for a real reason: a
report posted by a thread during a teardown would otherwise arrive after the stop, set "the file has run out"
for a playback that no longer existed, and be inherited by the *next* playback on the same instance (nothing
had moved the epoch, so the next playback's epoch was the same number). The fix is the one the user proposed:
the teardown advances the epoch at its end — "everything in flight belongs to a position that no longer
exists", exactly what a seek says — and the handler's guard is then one statement. It is last in the teardown
on purpose: the media threads are joined by then, so none of them can read the new epoch as a request to move
a cursor that is being disposed (the reader, alone among the threads, *acts* on an epoch change by seeking, and
a stop is not a request to move).

**2. The statement carries its position, so nothing has to be ordered.** The decode thread read
`_sourceEnded` and then the epoch, and the pair could be mismatched — the file "had run out" at one position
and the report carried the next one, which also meant the drain could be tagged with a position whose media
the decoder had not been flushed for. The boolean is now a tag: `_endedEpoch`, set by the queue with the
position it is about, read by the stages through `HasRunOut(epoch)`. What that removes is worth listing,
because each item was a rule somebody had to remember: the seek's `_sourceEnded = false` (nothing to clear —
a new position is simply not the one the file ran out at), the ordering requirement between the flag and the
epoch (there is no pair to order), the stop-me flag in the handler (point 1), and `DrainTheTail`'s re-read of
the gate (the decode thread now snapshots the position once, asks about *that* position, drains for it and
reports it — the user's point 2 exactly).

*Verified:* 133/133 tests, x64 and ARM64 clean. `PlaysAgainAfterAStop` is new and covers a path that had no
test at all: stop, reopen on the same instance, play to the end, with the assertion that the second playback
hands over the whole file. It does **not** pin the teardown window the epoch advance exists for — the advance
was commented out and the whole suite still passed, which is the honest measure of that window (a report
posted inside a teardown that lasts milliseconds). The rule is recorded instead: §7's stop paragraph, §8's
table, §11's new row about a boolean end-of-file flag, and Appendix A item 20.

**The proof that was missing from point 2**, from the next question: *"DrainVideo tags frames with current
epoch, which is the `_endedEpoch`. How to prove that `_endedEpoch` must be the same epoch of those packets?"* —
it cannot be proven as it stood, and the counterexample is short: a seek to a position with no packets (the end
of the file, or a point the reader immediately runs out at) can be reported ended by the reader *before* the
decode thread's next take-up, so the branch ran with the decoder still holding the *previous* position's media
while the gate — and therefore the tag — was the new position. Those frames would have been queued as the new
position's, shown at the new position, and `QueueFrames`'s own epoch check cannot catch it, because that check
only rejects a tag *older* than the gate.

The fix is to make the tag come from the decoder rather than from the gate: the drain branch now takes the
position up **first** (flushing if it moved, which discards exactly the media that is no longer wanted) and then
drains, tags and reports with `flushedForEpoch` — one value for the frames, the report and the memory of having
drained. That rests on an invariant worth stating once and relying on everywhere: **the decoder holds media of
`flushedForEpoch` and of nothing else**, because any other position flushes it first. Given that, a drained
tail is that position's media by construction, and the "proof" is an induction over the loop rather than a
check: packets are decoded only while the gate still agrees with the packet's own epoch (so
`epoch == flushedForEpoch` at every decode), and every other production path tags from the packet or from the
reader's carried-out epoch. The rule is written into §6's list of details that are easy to get wrong, into
`TakeUpThePosition`'s own doc comment, and as Appendix A item 21 — because the same mistake can be made
anywhere a producer tags its output with a fresh read of the gate.

**S9r — The line-by-line audit, and why the earlier reviews were not one.** *Done, from the user's question:
"I found these issues just by static analysis, without actually running the codes. So why can't you find them?
Please review the entire file line by line. Make sure each line of codes stays there for a reason, do not
overdesign. The synchronization mechanism has to be simple, straight forward and human readable."*

*(`flushedForEpoch` and `TakeUpThePosition`, named in S9q below, are the same two things this step renamed to
`decoderEpoch` and `EnsureTheDecoderHolds`; the reason is in the addendum at the end of this entry.)*

**The honest answer first.** The issues were missed because the review was *rhetorical* rather than
*mechanical*: each design claim was checked against the code by reading the code that the claim was about
("the epoch says the position, so the tag is consistent"), which confirms the intent and never tests the
concurrent case. That is what the user's method does differently — list every shared fact, every write and
every read, and ask what can be observed at that instant — and it is the method this step applied:

1. every field's writes were mapped to their enclosing method (a script, not a reading), and each method's
   thread was named; a write outside its group's thread is a bug;
2. every private member was counted across the whole repository: a member with one reference (its declaration)
   is dead;
3. every section header and ownership comment was checked against the fields under it;
4. the reads matter more than the writes, so each read of a shared fact was asked the same question the two
   real bugs above came from: *what has this thread established about that fact at this point?*

**What it found (five things, all fixed):**

- `WaitForAudioMilliseconds` **referenced nowhere** — a constant whose doc described bounds that S9o had
  removed. Dead lines keep a purpose alive in the reader's head that the code no longer has.
- The **device-watch fields' ownership comment** ("owned by that thread and nothing else") was false: the
  teardown resets all five after the join. Safe, but the sentence a reader relies on was wrong — and it is
  the sentence that would be used to justify reusing those fields somewhere else.
- The section header above them, "**Shared between a stage and the queue: volatile**", headed a group with no
  volatile field and nothing shared: a leftover from `_videoFlushPending`. A header that misdescribes its
  contents is worse than no header.
- `NotifySourceEnded` had **lost its doc comment** in an earlier edit of mine — the entry point of the whole
  ending model, undocumented.
- `private bool _disposed = false;` — the only default initialiser in the file.

**And one structural finding, which is the "human readable" half of the request:** the state was declared in
*two* places (a `#region Variables` at the top and a second block in the middle of the file), and the
ownership comments were in the second one. Every field is now in the single region, in the order §2.2 gives —
fixed for the life of the pipeline, open state, the queue's own decisions, thread-owned, counters — with the
constants likewise collected in one region. Reading the state and its owners is now one screen instead of an
interrogation, and §2.2 states the rule the audit used: a write outside its group's thread is the bug.

**Also removed, found by the same scan outside this file:** `D3D11VideoShaders.GetTextureCount` (a second
implementation of `FFmpegInterop.GetPlaneCount`, unreferenced) and `FFmpegInterop.DescribeChannelLayout` with
its `av_channel_layout_describe` P/Invoke (`AudioStreamInfo` carries a structured `ChannelLayout`, so the
string description was never wired). Left alone and reported rather than deleted: `MediaTypeData`,
`MediaTypeSubtitle` and `MediaTypeAttachment` (a mirrored constant table), and `avformat_network_init`
(a declaration for network sources that nothing calls yet).

*Verified:* 133/133 tests, x64 and ARM64 clean. No behaviour changed — this step's diff is declarations,
comments and dead code, which is exactly what a reviewer should be able to see at a glance: the audit's value
was in finding the *false statements*, not in new machinery.

**The follow-up, and the last of the three lessons.** The next question was: "Your fix confuses me even more
than the previous version: `flushedForEpoch` is still taken from `epoch.Current`, not epoch from queue. Why is
that?" — and the confusion was justified, because the guarantee was in the *ordering* (the take-up ran after
the dequeue and the decode was gated on the packet's own epoch against the gate), not in where the number came
from. A proof that lives in the order of three statements inside one function is a proof the reviewer has to
reconstruct; the code should say it where it happens.

So the position is now a **parameter**: `EnsureTheDecoderHolds(ref decoderEpoch, epoch)`, called three times
with two different claims — the packet's own epoch immediately before a decode (inside the gate check, so a
stale packet is dropped rather than flushed for), and *where playback is* at the idle sites and before the
end-of-file drain (where whatever is let go of is discarded, never tagged). The local is named `decoderEpoch`,
"the position the decoder belongs to", rather than after the flush that happens to maintain it, and §6 now
carries a two-row table of what each call site claims. Nothing about the mechanism changed; what changed is
that the invariant is now visible at the line that relies on it.

That is the third time in three rounds that the same fault line showed up: a tag, a flag and a request can all
be *correct* and still be unreviewable when the reason is not written where the decision is taken. The design
rules in §6 and Appendix A now carry it: **a value that is relied on for a position must name where it came
from, at the point where it is used.**

**S9s — The crash the throwing-decoder test found, and where a guard belongs.** *Done, from a report: "The test
process crashed while I'm running test" — the host died on my own new test, with the stack pointing at the
decoder's `TryDecode` leaving the thread.*

**What happened.** The test (`ReportsADecoderThatThrowsAsPlaybackFailing`) wraps the real decoder so it throws
after a packet, which is how a fault arrives when it comes out of interop rather than out of a return code.
The decode thread had no guard at all — the `try`/`catch` that used to wrap its loop had been lost in the
refactor (an earlier edit of mine had emptied the catch body, which the compiler catches as CS0168 but only
when that content is actually compiled; a stash of stale build outputs hid it once). An exception escaping a
media thread is not reported anywhere: .NET terminates the process, which in VS shows up as "Test host process
crashed" and in the application would be a silent exit while a file is playing.

**The policy, from the review that followed:** *"Only try catch the specific place that is expected to throw,
do not try-catch the entire method."* So the guards are now at the component calls and nowhere else:

| Thread | Guarded call | What the guard does |
|---|---|---|
| reader | `source.Seek`, `source.TryReadPacket`, `_audioDecoder.Flush` | the throw becomes the failure the site already handles (a warning for the seek and the flush, `PostFailure` for the read) |
| video decode | `videoDecoder.TryDecode` (in `DecodeVideo`), `videoDecoder.Drain` (in `DrainVideo`), `_videoDecoder.Flush` (in `EnsureTheDecoderHolds`) | a failure result, except the flush, which cannot be reported through a result and so goes to `PostFailure` |
| video render | `renderer.Render` (in `RenderVideoFrame`), `renderer.RefreshSurface` while paused | the failure result the caller already checks |
| audio | — | the whole-method catch stays, as the one deliberate exception: its interop surface includes *property reads* (device buffer fill, the device clock) that have no call to guard and no result to return |

The first version of this fix wrapped each media loop instead, and the review rejected it — correctly: a
loop-level catch reports a bug in the pipeline's own code as a component failure, ending playback as if the
decoder had died and making the bug unreproducible. The rule is now §7's paragraph and Appendix A item 22,
with §11 gaining the row for the shape that was rejected.

*Verified:* 134/134 tests, x64 clean; the new test is what pins the guard (it crashes the host when the guard is
missing), and it asserts the failure's message is the exception's own, so an emptied catch fails it rather than
passing quietly.

**S9t — Pause parks the threads on their queues, and a seek has to clear them.** *Done, from a report: "I
changed the behavior: now pause will not block reader and decoder, they are automatically blocked by TryEnqueue,
which is a better design. However, a seek during pause now is broken: reader and decode is still blocked but
they are not suppose to. I think the best solution will be reset the queues in SeekCore, which automatically
unblocks the two threads."*

**The model.** Pause no longer tells the reader or the decoder to stop; it stops the *picture*, and the two
threads behind it park on their own queues without consulting the state at all — the decoder in
`TryEnqueue`'s wait for room in the full frame queue, the reader on its read-ahead minimums, which stop
advancing once nothing is consumed. That is strictly better than a state check: fewer places decide what a
pause means, no thread wakes to ask, and the same machinery that paces a slow decoder paces a paused one.

**What it broke.** An epoch advance releases nobody. A parked thread is waiting on a *queue*, and a queue
knows nothing about positions, so the seek's "everything in flight is stale" — which the tags handle perfectly
— left both producers exactly where they were: the decoder waiting for room that only the paused picture could
make, and the reader idling because the queues it filled still said "buffered". Consequence: after a paused
seek, the frames of the new position were not produced, and on resume the old position's queue played out
first.

**The fix, as proposed, is `MediaQueue.Clear()`** (the user's "reset the queues"): drop what the queue holds,
dispose it like any media the queue lets go of, and let the producers waiting for room go on. `SeekCore` calls
it on all three queues, right after the epoch advance — because all three hold media of the position being
left, and because a clear is a *state* statement where the tags are a *media* statement: the released
producers hand over what they were holding, still carrying its own epoch, so nothing stale is taken for new,
and the next thing they produce is for the new position.

**The test, and the one that was wrong first.** `SeeksWhilePausedAndMovesTheReaderAndDecoder` plays a
0.5 fps fixture, pauses with both producers parked, seeks, and requires the decoder to have produced a frame of
the *new* position while still paused. The first version of it sought to 20 s and passed *without* the fix:
the old position's own queue fill reaches ~18 s — the decoder stops with the frame queue full, a few frames
past whatever the picture last took — so the assertion was measuring the wrong thing. Seeking past what the old
position can possibly reach (60 s, asserting 50 s) is what makes it discriminate: with the clear commented out
it fails on the assertion, with it in place it passes.

*Verified:* 135/135 tests, x64 clean. Recorded: §3's queue table (the seek row, the room-wait row, and the
paragraph that used to say a seek does nothing to a queue), §4's rule 3, §6's step 4, §7's pause bullet (which
described "freezes all four threads", the old model), I5, §11's new row for the rejected shape ("the tags
already handle it"), and Appendix A item 23 — a queue paces a thread and cannot tell it the position moved.

**S9u — The position change is one fact (the epoch stops being lock-free).** *Done, from a review: "However
this introduce a new race condition: 1. After `_epochs.Advance()`; all threads start processing from new
position; 2. `xxxQueue.Clear()` has a chance to accidentally clear the new packets, causing data loss. In order
to fix it, I think we need to introduce lock: 1. Make `_epochs` a simple long value; 2. Read and write `_epochs`
inside the lock; 3. Reset other states inside the lock too, so this becomes an atomic operation."*

The review is right, and the window is wider than "a chance": with the gap widened to 300 ms for a probe, **two
runs out of three dropped 48 packets of the new position — tag 2, the whole first second and a half including
the keyframe** — because the reader, woken by its own 15 ms cadence, took up the new epoch, moved the cursor,
read on, and queued media that the reset then deleted. The probe logged the tags each `Clear` dropped, which is
what made this measurable rather than a matter of opinion; with the same 300 ms window *inside* the lock, all
three runs dropped only tag-1 items. (That the pipeline still reported `Playing` in the first case is why the
probe was written to log tags at all: FFmpeg swallowed the missing keyframe, so the damage was media lost, not
a failure reported.)

*What changed, exactly as proposed:*

- `EpochGate` is gone; `_epochs` is a `long` that is **only** ever read or written under `_positionLock`, and
  media threads read it through `ReadEpoch()` — 21 read sites converted, so there is one rule to audit rather
  than a mix of `Volatile.Read` and locks.
- `SeekCore` does the whole change in one section: publish the position, advance the epoch, clear the three
  queues, clear the starvation flags, re-arm the anchor, set the clock's position. `StopCore` advances the
  epoch under the same lock (after the joins).
- The reason is recorded where it matters: the epoch alone says what is *stale*, but only the reset says what
  is *gone*, and a thread that acts between the two produces media the second step deletes.

*What this costs, honestly.* The epoch was a lock-free counter **on purpose** ("a seek has to be able to say
'everything in flight is worthless' without waiting for whatever that work is doing" — S9i). It is not any
more: the seek now takes a lock that 21 read sites also take. The cost is bounded — the only writers are the
queue's own seek and stop, the readers hold it for a load, and nobody holds it while waiting — but the
rationale has changed, and §2.1's "the epoch needs no lock" claim is now "the epoch needs the lock, because the
reset has to travel with it". The alternative considered and rejected: a `Clear` that keeps items tagged with
the current epoch, which needs no lock but puts a tag comparison inside the queue — the one place S9k said the
tag must not be interpreted.

*Verified:* 135/135 tests, x64 and ARM64 clean; the paused-seek test still passes, and `EpochGate.cs` and its
test coverage (`MediaQueueTests` never touched it) are deleted. The probe and its instrumentation are gone;
this entry keeps the measurement. (The lock this step introduced is the shape S9v then replaced with a seqlock;
what survives from it is the *bracket* — one change, not a sequence of steps.)

**S9v — The position change is published through a seqlock.** *Done, from a review: "Implement a Seqlock to
improve performance. Most reads shouldn't be locked." Then: "Can you introduce a new SeqLock class?
PlaybackPipeline is large enough" and "Make SeqLock generic".*

S9u left one honest cost: the epoch is read by 21 sites on three media threads, and every one of them took the
writer's lock — a reader stalling on a lock is exactly what the threads that move media must not do. The epoch
is also the ideal seqlock value: it changes only when the user seeks or stops, and is read per packet and per
frame.

*What changed:*

- `Internal/SeqLock.cs` — `SeqLock<TValue>`: `TValue Read()` without the lock, and
  `void Change(Func<TValue, TValue> update, Action<TValue>? work = null)`. The sequence is even while the value
  is settled and odd while it is changing; a reader takes sequence → value → sequence and starts over if either
  read of the sequence saw a change or the odd half of one. `Change` runs `update` *and* `work` inside the odd
  window, under the same `Lock` the readers fall back to — that lock is what keeps two changes from interleaving,
  and it is never held by a reader that got its value on the first try.
- The pipeline now holds `SeqLock<long> _epochs = new(0)`; the 21 read sites are `_epochs.Read()`, `SeekCore`
  is `_epochs.Change(epoch => epoch + 1, _ => { … })` and `StopCore` is `_epochs.Change(epoch => epoch + 1)`
  (nothing to reset after a stop: no queues to clear, no anchor to arm). `ReadEpoch()` and the
  `_epochLock`/`_epoch`/`_epochSequence` fields are gone.
- **Why the work is inside the window rather than after it** is S9u's measurement: the reset of the old
  position's media has to be finished before any thread may act on the new value, or that reset deletes media
  produced for the new position — 48 packets, keyframe included, when the window was widened to 300 ms. A
  seqlock whose writer only published the value and then reset would reintroduce exactly that race, with the
  readers merely unable to see the value during the *publish*.
- **Generic rather than `long`** because the fact may be several fields: the sequence is read again *after* the
  value, so a value torn by a change in progress — or replaced by a change that ran entirely between the two
  reads of the sequence — is rejected and read again. `Read()` is bounded (4 attempts, then it waits for the
  writer's lock): a change that outlasts several attempts has something slow inside it, and a reader holding a
  core is a reader delaying the writer. The `finally` re-bumps the sequence even if the work throws, so a
  reader is never left waiting for a change that is not coming.

*Verified:* 139/139 tests (135 + `SeqLockTests`), x64 and ARM64 clean. The four new tests cover the class's one
guarantee from both sides (a reader that races a writer never acts on a change that has not finished; a reader
that meets a deliberately slow change waits for it instead of taking it half-made), the compound value being
handed out as one fact, and the work being told which value it belongs to.

*Recorded:* §2.1 (the position change is its own shape among the five questions), §2.2's row for the position
change and the "fixed at construction" row (two locks became one), §6 rule 3, Appendix A item 24, and
Appendix B's epoch row.

**S9w — The end of a stream is an entry of its queue.** *Done, from a review: "Currently video decoder detects
playback end by starving + IsEnding, however this is not a reliable way. Add an IsLast field for each item in
MediaQueue, so that decoder will know for sure it is the last item without getting blocked by the queue first
or quering epoch. This pattern also applies to video and audio render threads." Then: "Remove TryDequeueEnd,
add an out bool isLast parameter to TryDequeue, when true, item is null. Avoid using non-null assert (!)
anywhere."*

The review names a real defect in S9o's model, and it is worth stating exactly: the decode thread learned that
the stream had ended by finding its queue empty **and then** asking `IsEnding(workingEpoch)` — an empty queue to
be woken by, and a question about a position. The question was about the position the thread had just made the
decoder hold (not a fresh read of the epoch), so the answer was *internally* consistent, but the shape itself
is two faults: the fact is only visible after a wake-up and a timeout (up to 20 ms of nothing to dequeue), and
it is a fact about the producer held somewhere else — `_endingEpoch` lives in the queue's own decisions, and
the worker has to ask for it. Nothing about the drain should be a question at all: the reader knows the packet
stream ended, so it can say so, and it is the reader that knows it first.

*What changed:*

- `MediaQueue<T>` carries an end entry now. `EndStream(tag)` puts an entry behind the media with no item and
  an `isLast` flag, and `TryDequeue(out T? item, out long tag, out bool isLast, int timeout)` hands it over
  like any other entry — true, item null, `isLast` set, and the tag of the position that stream belonged to.
  Three things fall out of that: it is *ordered* (the consumer cannot see it before the media it follows), it
  *wakes* whoever is waiting (so the end is not something a consumer discovers after a poll), and it takes no
  room (so a producer can end its stream while the queue is full, which is exactly when the consumer is most
  likely to be waiting for room). An end entry has no weight and does not count as media, so a producer's
  pacing and a consumer's "how much is buffered" are about media and nothing else.
- The reader says the ends of the two streams it owns (`_packetQueue.EndStream`, `_audioQueue.EndStream`,
  after the audio drain) and *then* posts the pipeline's own statement `NotifySourceEnded(epoch)` — the file
  ran out at this position, a different fact from either stream's end, and the one the ending is decided on.
- The decode thread takes the end of the packet stream and drains the decoder into the frame queue; because
  handing over the tail *is* ending that stream, `DrainVideo` says so (`_frameQueue.EndStream(epoch)`) behind
  the frames it produced. The thread no longer asks anything: no `IsEnding`, no per-thread memory of having
  drained a position (the `producedForEpoch` local is gone), and no report of its own — the picture saying it
  has shown everything already says the tail came out, because it takes the end entry only after every frame
  the decoder produced.
- The picture's word comes from the entry: taking the end of the frame stream while holding nothing means
  everything this position has has been shown. The sound's word is unchanged in substance — the device holds
  nothing after the stream ended — but it too learns the end from the entry rather than from `IsEnding`.
- `_videoDecodeEnded` is gone (there is no decode word), the two remaining words are **positions** rather than
  booleans (`_videoRenderEndedEpoch`, `_audioRenderEndedEpoch`), and `TryNotifyPlaybackEnded` compares them
  with `_endingEpoch`. Comparing rather than consuming is what makes the words order-free: a stage's stream can
  end and be reported before the reader's own end-of-file report (different threads, same decision queue), and
  the word has to survive arriving first — the old code cleared all three flags in `NotifySourceEnded`, which
  would have thrown away a word that arrived early. (This step decided the ending only while playing and
  re-asked it from `PlayCore`; S9x removes both.)
- `IsEnding` is now read in exactly one place — the picture's starvation rule, where an empty frame queue at
  the end of a file is the picture playing out rather than falling behind.

**The bug this step introduced and the review's rule that found it.** The first version reported the end with
the thread's own loop variable — `Submit(() => NotifyVideoRenderEnded(heldEpoch))` — and five tests failed with
playback stuck in `Playing`. A probe printed `video render ended for 0`: the lambda is read when the queue
reaches it, not when it was written, and by then the next failed dequeue had set the variable back to its
out-parameter default. The report is about a moment, so it now carries a copy (`endedEpoch`, `playedOut`,
`ranOutAt`) — the same rule the review had already applied to the tag checks ("the tag has to be right by
construction"), one level further out. Appendix A item 21 and §11 carry it.

*Also from the review:* no null-forgiving operator anywhere in the pipeline or the queue — the item is
nullable out of `TryDequeue` because the end entry has none, so consumers check it (`packet is null` after the
`isLast` branch) instead of asserting it, and `PrepareVideo` guards its source instead of `_source!`.

*Verified:* 142 tests (140 run, 2 skipped without an audio device), x64 clean. New coverage:
`HandsTheEndOfTheStreamOverAfterEverythingQueuedBeforeIt` and `ReleasesAConsumerWaitingForAnEntryWhenTheStreamEnds`
in `MediaQueueTests` (the end is ordered, takes no room, wakes a waiter and is handed over once), and
`ProducesTheTailOfAFileWhenItsPacketStreamEnds` in the pipeline tests — a decoder wrapper that keeps every
frame until it is drained, so the test can only pass if the tail is produced, which is the one thing a
pipeline test can see of the difference between "there is nothing right now" and "the stream is over".

*Recorded:* §3's queue table and the paragraph after it, §7's end-of-file model (step 1–5 rewritten; the
statement is now read in one place), §2.1's first question, §2.2's two rows, I7, §11's two new rows, Appendix
A items 20–21, and Appendix B's three rows.

**S9x — The ending is not the state's to gate.** *Done, from a review: "Is it possible to remove
TryNotifyPlaybackEnded in PlayCore as well as _state != MediaEngineState.Playing check in
TryNotifyPlaybackEnded? If the playback ended during a pause (very rare scenario), just transit to End state."*

S9w left the ending gated on `Playing` and asked again from `PlayCore`, which reads as caution and is really
two faults. A file that has played out and is then paused stays `Paused` for good: the words are already filed,
nothing except a resume asks the question again, and the pipeline is holding a file that nothing will play. And
the resume that "fixes" it reports a `Play` that succeeds and then immediately ends — a caller told it started
what it finished. The words are the stages saying they have nothing left, so the ending is earned wherever they
are heard; the state is not part of the fact.

*What changed:*

- `PlayCore` no longer asks. With the gate gone there is nothing to ask about: every word handler decides when
  it completes the set, the file's own end included, so an ending that is fully said is an ending that happened.
- `TryNotifyPlaybackEnded` guards on `_state == MediaEngineState.Error` instead of `_state != Playing`. `Error`
  is the one state the decision must not leave — it is the reason playback cannot continue, and `SetState(Ended)`
  would clear `_error` and replace a failure with a file that merely finished. `None`/`Opening`/`Ended` need no
  guard: a stop resets `_endingEpoch` and advances the epoch (and every open goes through `StopCore`), so the
  position check blocks them, and ending an already-ended pipeline is a no-op.
- The paused case is now decided by the stage that says the last word: a pause that lands between a word being
  earned and being heard no longer holds the file open.

*The test, and why it can name the case rather than race for it.* `EndsWhilePausedWhenTheFileHasPlayedOut` plays
the wave fixture against a device that is one sample short of full — room for every block, and never empty — so
the sound has nothing left to hand over and the only thing holding the ending up is the device not being empty.
The pause is then taken **from inside the sound's own question to the device** (`FakeAudioRenderer`'s
`BeforeReadingQueuedSampleCount`, one-shot), which is the moment the word turns on: the pause is processed, the
device is allowed to empty, and the word is posted with the pipeline already paused. Without the change the
pipeline stays `Paused` and the test fails on "the file has to end even though it was paused" — verified, by
putting the `Playing` guard back and rebuilding (the first attempt at that falsification ran against a stale
copy of the core assembly in the test output, which is worth remembering: a change to `SinePlayer.Core` needs
the *solution* built before `dotnet test --no-build` means anything).

*Verified:* 143 tests (141 run, 2 skipped without an audio device), 0 failed, x64 clean; the new test passes in
~110 ms and was run repeatedly (5/5).

*Recorded:* §7 step 5, I7, §11's new row (the shape that was there before, and what it cost), Appendix A item
25, Appendix B's ending row, and a forward note in S9w.

**S9y — A failed playback does nothing, and is not torn down.** *Done, from a review: "What is our expectation
for Error state? If the expectation is stop the playback, shouldn't we trigger StopCore on error? This way
_state == MediaEngineState.Error will be unnecessary. I'm just proposing an idea, you can disprove me if I was
wrong." Then: "I think the correct check is state not in (Playing, Pause), since do-nothing should be the
default behavior, and do-something requires restrict state check."*

The second message is the answer to the first, and it is the better rule. `Error` *is* "playback has stopped" —
the clock stops, and every media thread parks — but it is deliberately not "the pipeline has been torn down",
and the reason it is not is the whole of this entry.

*Why not tear down on failure (the review's idea, disproved).*

- **The reason would lose its home, and the scene with it.** `StopCore` ends in `SetState(None, null)`: the
  error, the `MediaInfo`, every diagnostic counter and the clock are cleared, so `Position` becomes zero — the
  pipeline could no longer say *what* failed *where*, and describing a failure is exactly what those pieces
  are for. Keeping `Error` after a teardown instead is worse in a smaller way: `Play` would answer "Nothing is
  open." rather than "Playback has failed: \<reason\>", because `PlayCore` checks the source first.
- **A spontaneous failure has no caller to answer.** That is the contrast with an *open*, which fails and
  leaves `None` because the caller holds the task the reason can travel in. A failure that arrives on its own
  needs somewhere to *live* until the host looks, and `Error` is that place — I16 read from the other side.
- **The teardown is four unbounded joins on the queue every command is posted to**, and that queue is shared by
  every pipeline. A failure is the moment another thread may be stuck inside a component (a hung read, a wedged
  device submit), so doing it automatically converts "one playback failed, and the app was told why" into "no
  pipeline in the process accepts a command until the broken thing returns". Today the host decides when to pay
  that, and it is also who can preserve the evidence first.

*What did change, from the review's rule.* The reader reads for `Playing` and `Paused` and does nothing in any
other state — checked **in the positive direction** (`_state is not (Playing or Paused)` parks), because
do-nothing is the default and work is what has to be justified. It is the one thread that works while paused
(the read-ahead is what a resume starts on). A failed playback is therefore quiet: the reader no longer fills a
read-ahead and re-checks the queues every 15 ms for a consumer that cannot come, and the other three threads
were already at rest (the picture and the sound park on the state; the decoder is stopped by the frame queue it
can no longer empty, which is bounded by that queue — the one remaining place where a failed playback still
does a queueful of work for nobody). Nothing is released or discarded by parking, which is what keeps the
failure's scene and reason intact for the host.

*The test.* `StopsReadingOncePlaybackHasFailed` plays the raw fixture against a **paced and counted audio
decoder** — the reader decodes the sound inline, so a call to that decoder *is* the reader having read another
packet — and a video decoder that dies on its second packet. Paced, because the failure has to land while the
reader is still inside its read-ahead for "still reading" and "parked" to be different observations at all; and
the assertion allows the one read that was already in flight. Falsified by removing the check and rebuilding the
solution: the reader then finishes the read-ahead and the test fails on "a failed playback has nothing to read
for". (The first attempt at that falsification was run against a stale core assembly — the same trap S9x
records: build the *solution* before `dotnet test --no-build`.)

*Found while running the suite, and fixed.* One full-suite run failed `ReportsADecoderThatThrowsAsPlaybackFailing`
in 26 ms. The cause is that test's own race, not this change's: the decode thread decodes ahead **without asking
whether anything is playing**, so a decoder that dies can be heard before the test's `Play` is processed — and
`Play` is then refused *because* playback has already failed. The assertion `Play` succeeds was therefore a coin
toss in the two decoder-failure tests. Both now don't assert it (with the race named in the comment), and the new
test does not play at all: it leans on exactly that property — the reader reads, and the decoder decodes, while
the open's state is still paused.

*Verified:* 144 tests (142 run, 2 skipped without an audio device), 0 failed, three consecutive suite runs, x64
and ARM64 clean; the new test passes in ~0.4 s and was run repeatedly.

*Recorded:* §7's "what `Error` is, mechanically" paragraph, §4.1's positive-direction rule, §11's new row (the
teardown that looks tidy and costs three things), Appendix A item 26 (do-nothing is the default), and the two
test comments that now name the play-versus-failure race.

**S9z — A seek pauses, the host resumes; and the clock is told only what changed.** *Amended in v2: the pause
was removed. A seek now leaves the state as it found it — a file that was playing goes on playing from the new
position, and a paused one stays paused — so there is nothing for a host to resume, and a pause the user pressed
while the seek was in flight is not overruled. `SeeksWhilePlayingAndLeavesItPaused` is now
`SeeksWhilePlayingAndGoesOnPlaying`, and the rejected shape is "pause and then resume", which is what the
§11 entry in [PlaybackPipeline.md](./PlaybackPipeline.md) argues against.* *Done, from a report: "1.
I've changed the seek behavior: after a seek, the playback state will always be Paused. The host needs to call
Play if it wants to resume the playback; 2. StartOrStopClock has no internal dedup, same method can be called
multiple times in a row."*

The user's first change was already in the working tree: `PlayCore` is for `Paused` only, `PauseCore` for
`Playing` only, `SeekCore` for `Paused`/`Playing`/`Ended` and it *always* leaves `Paused` (the `wasPlaying` /
resume pair is gone), all through one `CheckState(out state, params states)` helper. The state is now what
playback *is*; whether the new position goes on playing is the host's one `Play`. That is a real simplification
of a genuinely awkward question — "a seek while playing keeps playing" made the state depend on both what the
file is doing and what the user last asked for, and made the asynchronous refusal undecidable: a source that
refuses the move is only known *after* the move was accepted, so the old shape had to resume before it could
know whether there was anything to resume.

*What that cost, and what was done about it.*

- **Seven tests pinned the old contract** and were rewritten, not deleted: `PausesAndResumes` (pausing what is
  already paused, and playing what is already playing, are now refusals that name the state — pinned);
  `SeeksWhilePlayingAndKeepsGoing` → `SeeksWhilePlayingAndLeavesItPaused` (the seek leaves it paused, then one
  `Play` runs the file out); `SeeksWhileTheNextFrameIsStillWaiting` and `StartsWhereTheFileResumesRatherThanWhereTheSeekAsked`
  (the frame/clock assertions now follow a `Play`); `AnswersACommandWhileTheSoundHasNotPlayedOut` and
  `EndsWhenTheDeviceHasPlayedOutTheSound` (paused, not playing, is what "not an ending" means now — the second
  plays on before the ending is earned); `KeepsPlayingWhenTheSourceRefusesToSeek` →
  `RefusesASeekTheSourceWillNotCarryOut`, whose two cases are now genuinely different commands: refused in
  advance changes nothing (I16, unchanged), refused when asked is the *move's* pause (I16 amended).
- **The host's expectation was restored where it belongs.** `PlaybackEngineAdapter.Seek` remembers whether
  playback was playing *at the moment the seek is asked with*, and plays again once the seek's task completes —
  including when the source refused it, which is the behaviour that used to be the pipeline's. `PlayerPageViewModel`
  is untouched, which is the adaptation layer's whole purpose.

*The clock dedup (the second point).* `StartOrStopClock` now asks the clock what it is doing and tells it only
when the answer changes (`if (start == clock.IsRunning) return;`). Several things decide it in a row — a state
change, then each starvation flag, or the reverse, and a seek does all of them inside one change — and relying
on the clock to absorb repeats is relying on something the interface does not promise: the two real clocks
guard themselves, but a naive `Start` re-anchors from now and a naive `Pause` records a stop that never
happened. Asking the clock is also what makes the check exact when the clock *instance* is replaced (a switch
to the device's clock, a new file), which a remembered flag would get wrong.

*The test.* `TellsTheClockOnlyWhenWhatItShouldDoChanges` uses the arrangement where the difference is visible:
a decoder far too slow for the stream holds the clock (`StarvedFor == "video"`), so the clock is already
stopped when a `Pause` arrives — and a pipeline that tells the clock anyway says "pause" twice for one fact.
`FakeAudioRenderer` counts the pipeline's `Start`/`Pause` calls on the device clock (changed or not), which is
what makes "it was not told" observable at all. Falsified by removing the `IsRunning` check and rebuilding the
solution: it fails on "the clock is not told what it already knows".

*Verified:* 145 tests (143 run, 2 skipped without an audio device), 0 failed, x64 and ARM64 clean, the app
project included (the adapter's resuming `Play` is awaited, which is also what makes the position the page
re-reads afterwards the one playback resumed at).

*Recorded:* §2.1 (two new rules: each command names its states; the state says what playback is doing, never
what the caller wants), §6's seek steps 1–5, §5's clock bullet, §7's Play bullet, I10 (the clock is *told* only
on a change, with its pin), I6/I15/I16 (renamed pins, and I16's asynchronous-refusal nuance), §11's new row
(the command undoing its own pause), and the adapter's comment.

**S9aa — No clock in a queue: `Dequeue` replaces the timed wait, and the sound watches the device instead.**
*Done, from two reports: "Remove timeoutMilliseconds parameter on TryDequeue, rename TryDequeue to Dequeue and
returns void. Timeout is a bad design which introduce unpredictability." — then, after the plan was reviewed
and rejected in three places: "Return bool instead, true for item or isLast signal, false for close".*

The plan the first report produced was wrong in three ways, and the review is why the shape ended up small:

- it kept a "nothing was handed over" word for a wake that carries no entry, with `void` and a documented
  meaning to interpret. There is no such wake to interpret. `Clear` pulses for the *producer* parked for room
  (that is what releases the decoder after a seek); a *consumer* has nothing to do about a seek — what it
  should play next arrives as the next entry, stamped with the position it belongs to — so a clear must leave
  it waiting, and a condition loop does that by itself. With `bool`, the type says the only two things a wait
  can end with: an entry, or closed.
- it called the pause reaching a blocked consumer at its next item a "consequence to accept". Accepting it is
  what makes it not a consequence: the reader deliberately keeps reading while paused, so the picture reaches
  its state check within about one decode, and the sound's carried block waits inside `WaitForAudioRoom`,
  which is state-aware. It also claimed the end-of-file word would "never fire"; under the same review's rule
  — *after `isLast` for this position, do not ask the queue again until the epoch advances* — the thread is
  watching the device rather than the queue, and the word fires as before.
- it claimed the decode thread's eager "hold the decoder" after a seek became lazy in one case. It did not
  matter: when the thread *is* parked on the packet queue the clear still releases it, and when it was busy
  the hold happens before the first decode of the new position, which the packet's own epoch check requires.

*What changed.* `MediaQueue<T>.Dequeue(out T? item, out long tag, out bool isLast)`: true is an entry — media,
or the end of the stream with `isLast` — and false is the queue closed, answered at once and never waited for.
The wait is one unbounded `Monitor.Wait` inside a condition loop (`while (_items.Count == 0 && !_closed)`), so
the consumer side now has the same shape as the producer side, which has waited without a clock since S9t.
Every consumer leaves on false rather than asking again (a closed queue answers immediately, so a loop that
ignored the word would spin), and each of the four releases is now something a test can drop: `TryEnqueue`,
`EndStream`, `Clear` (producers only), `Dispose`.

*The one place a clock was doing real work.* The sound's 50 ms poll *was* the end-of-file drain question: after
the end entry, the device drains asynchronously and nothing wakes the thread. That question moved out of the
queue and into a **device watch**, which is also where the audio starvation statement was hiding. When the
queue holds nothing at all (`TryPeek` — not `Count`, which counts media and not the end entry, a defect this
step made and the suite caught), the sound asks the device instead of the queue, and asks it two things. Time
should pass while the device still holds audio, because that audio is playing; and it should be held when the
device holds nothing *and* the file has not run out at this position. So `StarvedFor == "audio"` now means
"the device has nothing left in it and the pipe is behind it" — a fact about what is being heard rather than
about how far behind the reader is — and the device emptying after the stream ended is the sound's half of the
ending, said once per position. This is the one wait in the pipeline paced by a clock, and that is stated
where it lives: the device is the thing this thread's world is arranged around that cannot signal.

*What the first version got wrong, measurably.* Asking `queue.Count == 0` instead of `TryPeek` meant the end
entry — which takes no room and is therefore not counted — was never taken: the sound watched the device
forever, `endedEpoch` never arrived, and twelve tests failed in one run (every audio test that ends a file,
plus the seek and pause tests behind them). The suite went from 134 passed / 12 failed to 147 / 0 with that one
line changed.

*Falsified, both ways round.* Removing the close's pulse from `MediaQueue<T>.Dispose` fails
`ReleasesAConsumerWaitingForAnEntryWhenTheQueueIsClosed` and `ReleasesAProducerWaitingForRoomWhenTheQueueIsClosed`,
each within its own bounded join rather than by hanging. Disabling the device watch's report and deleting its
starvation statement fails `EndsWhenTheDeviceHasPlayedOutTheSound`, `EndsWhilePausedWhenTheFileHasPlayedOut`
and the new `HoldsTheClockBackWhenTheSoundHasNothingToPlay`.

*The hang question, answered before implementing it (from the review).* A failure ends a producer without
ending its stream, so a consumer parked in a queue waits for the host's `Stop` — and that is not a defect
because `Error` is terminal: `Play`, `Pause` and `Seek` are all refused from it, the only recovery is `Open`
(which begins with `StopCore`), and `TryNotifyPlaybackEnded` names the states it accepts, so no late word can
end a stopped or failed playback. `StopCore` releases every thread by construction and before the first join:
the stop flag, the state signal, then all three queues closed.

*Verified:* 149 tests (147 run, 2 skipped without an audio device), 0 failed, run twice, x64 and ARM64 clean.
Three new queue/pipeline tests — `ReleasesAConsumerWaitingForAnEntryWhenTheQueueIsClosed`,
`DoesNotWakeAConsumerToTellItNothingHappened` (a clear is not a wake), `StopsWhileAThreadIsParkedOnAnEmptyQueue`
(bounded, so a missing release fails rather than hangs) — and one for the starvation hold,
`HoldsTheClockBackWhenTheSoundHasNothingToPlay` (starved and clock-stopped are read inside one wait, so a
stop/start cycle cannot pass for an answer).

*Recorded:* §2 items 6–7 (three timed waits left, and what a queue's wait can and cannot rely on), §3's
consumer row and the paragraph about what a clear does to a consumer, §4.4's first bullet (the device watch and
its two questions), §8's flag table and the note on why the two waits are measured differently, §10's I12
amendment and the new **I17**, §11's row (a timeout in a queue is a sedative, not a backstop), §12 (a freeze
inside a queue is one of four releases going missing), §13's `StarvedFor` row, Appendix A item 27, and
Appendix B (the `Dequeue` row and what releases a consumer).

**S9ab — The scripted host was not told the new contract, and a paused file now shows where it is.**
*Done, from a report and a request: "1. playback seems paused after a seek in PlaybackLab, check your host
implementation; 2. Draft a plan for this change: After a seek, the video render should render a frame from
latest epoch dispite the Pause state so the player displays the latest frame preview when seeking during a
pause." — then, on the plan: "1. I prefer A2, but I want to discuss further: My idea is keeps dequeuing until
gets the first fresh frame from new epoch. No peek required at all. No new API... 2. There is no need for a
'second wake'... 3. Uniform of course. 1. U1; 2. I want to move SignalPlaybackStateChange out of SetState, so
the caller can decide whether and when to trigger the signal".*

*The report was the contract, not a defect in it.* `PlaybackLabPage.SeekBy` awaited `pipeline.Seek(target)`
and stopped — and a seek now always leaves playback `Paused` (S9z), so the Lab halted after every jump. It is
a host like any other: it reads `pipeline.State` as the seek is carried out and calls `Play` when playback was
playing, which is the app adapter's policy in one line. (The app was already right; the Lab is a second host
and had not been taught.)

*What the request turned into* (the plan is in the conversation; the review is what shaped it):

- **The still.** While paused, nothing of the current position has been shown, so the picture thread takes
  frames until it gets the position's first one and shows it **once**. `shownEpoch` — set wherever a frame
  reaches the renderer, playing or paused — is what makes it a still rather than a slideshow. The queue is the
  only mechanism: no peek, no new API, no second wake, no signal from the decoder — the frame arriving *is*
  the wake, and the seek's own signal is only what gets the thread into that wait. A frame whose tag is stale
  is disposed and the loop takes the next; the current position is re-read each time round, because a seek
  landing while this waits leaves the frame it is about to be handed belonging to nobody. Nothing is anchored,
  dropped or starved: a still is not a presentation.
- **U1, the picture's word said at once.** A position with no frame at all — the end entry arrives before any
  frame does — is a position whose picture has shown everything it has, so the still path says the picture's
  word there and then instead of leaving it for a presentation that is not coming. Visible consequence: a
  paused seek past the end of a video-only file now reaches `Ended` (an A/V file still waits for the sound's
  word, which is only evaluated while playing — the asymmetry S9x already documented, unchanged). The Lab's
  play button therefore seeks to zero when it is asked to play an ended file, as the adapter does.
- **The signal moved out of `SetState`, and the seek's signal moved after its epoch change.** `SetState` now
  moves the state, re-decides the clock and raises `StateChanged`, and tells the threads nothing — a signal is
  a statement that a change is *complete*, and only the caller knows what its change consists of. Every caller
  signals when its own change is done: `PlayCore`/`PauseCore` after the state, `SeekCore` **once, after the
  epoch, the clears and the anchor have moved**, `Fail` and `TryNotifyPlaybackEnded` after theirs, `StopCore`
  before the join it depends on, `OpenCore` not at all (it starts its threads afterwards, and they read the
  state when they start).

*What was measured, and what it corrected.* The signal ordering was a **latent defect S9z introduced**: a seek
signalled with its state change, so a thread woken in between read the position playback had left and waited
again — and the reader, parked with no timeout once a file has run out, waited for a wake that never came
(the file did not move; the clock, the state and the seek's answer all said it had). The window is
microseconds, so the first falsification attempt *passed*; widening it to 150 ms showed the mechanism exactly
(`TakesUpASeekFromTheEndWithoutAnotherCommand` fails on "the reader has to take up a seek from the end without
another command"), and removing the signal altogether fails it with no widening at all. Both were run before
the fix was kept.

*The falsifications, in order, all run before keeping the code:* the seek's signal removed → the reader never
moves, test fails; the seek's signal before the epoch change with a 150 ms window → same failure; the paused
branch's still removed → `ShowsTheStillOfThePositionASeekMovesToWhilePaused` and
`EndsAtOnceWhenThePausedPositionHasNoPicture` both fail; the still's end word removed → the ending test fails
and nothing else does; `shownEpoch` not updated by the playing path → `DoesNotShowAStillWhenNothingMoved`
fails *after* the test itself was fixed (its first version counted the frames after the pause, so a still that
fired as the pause landed was inside its own baseline — the count is now taken before the pause, and the
arrangement shows two frames while playing with the next one a second away so nothing can slip in between).

*One existing test changed for the new rule.* `ReportsWhatTheDisplayTookWhenTheRendererCanTell` used a
one-frame bitmap and asserted the renderer's answer arrives while playing: with the still, the file's only
frame is shown at the open (paused), before the renderer was able to tell anything, so nothing more was
presented to carry the answer. It now uses a two-frame y4m (`CreateY4mFile` gained a frame-rate argument,
default unchanged) and the first assertion is honest for a new reason: a renderer that cannot tell says
nothing, whether or not frames have been offered to it.

*Verified:* 153 tests, all passing, three consecutive full runs (23 s each), x64 and ARM64 clean, and the five
new/changed tests run four extra times each for flake. New tests: `ShowsTheStillOfThePositionASeekMovesToWhilePaused`
(one frame, and one only), `DoesNotShowAStillWhenNothingMoved`, `EndsAtOnceWhenThePausedPositionHasNoPicture`,
`TakesUpASeekFromTheEndWithoutAnotherCommand`.

*Recorded:* §2.1 (the new rule: a state change tells subscribers, the caller wakes the threads), §4.3 (the
still, and the ending word for a position with no picture), §6 step 5 and §7's pause bullet (a seek signals
once, after the position has moved; a paused file shows where it is), I4 (the one frame the presentation
thread waits for has no clock behind it), I11 (the still, both ways round), §11's two new rows (signalling
with the state change; showing a peeked frame), Appendix A items 28–29, Appendix B (the still and who wakes
the threads), and the Lab/adapter as hosts that decide whether a jump goes on playing.

**S9ac — One loop that both presents and offers the still.** *Done, from a report: "Can you to merge the loop
of TryShowTheStill into the main loop so that only one loop exists. Current codes have many duplicated part
which makes the codes unreadable. Take Video decode section and reader section as example."*

*What the duplication was.* S9ab added the still as a helper with a loop of its own, and it had grown into a
second copy of the render thread's walk: take an entry, answer the end of the stream, give up on a frame whose
tag is stale, hand a frame over. Two loops meant two places where any change had to be mirrored — the shape
the decode thread and the reader never have, because each of them is one loop.

*What replaced it.* The two things the thread does are named once, in one loop, and everything else is a wait:

```csharp
bool presenting = CheckState(out MediaEngineState state, MediaEngineState.Playing);
…
bool offeringTheStill = state == MediaEngineState.Paused && shownEpoch != epoch && endedForEpoch != epoch;
if (!presenting && !offeringTheStill) { WaitForPlaybackStateChange(stamp); continue; }
… take (dequeue → end entry → stale tag → adopt) …
if (presenting) { anchor; drop; wait-for-the-moment; }
Show(held); shownEpoch = heldEpoch;
```

Everything the two paths shared — the take, the end-entry word (with its once-per-position dedup), the stale
tag check, the `Show`, the failure, the `shownEpoch` bookkeeping — is written once. Everything only
presentation does (starvation, anchoring, the drop rule, waiting for the frame's moment) is inside one
`if (presenting)`, with the sentence that says why: a still is not a presentation, so none of it applies.

*What falling out of the merge fixed.* A frame taken while playing and not yet shown (waiting for its moment)
was, on a pause with nothing of the position yet shown, left in hand while the *old* helper took a *newer*
frame from the queue and showed that as the still — so a resume put the picture back to the older frame. Now
the held frame *is* the still, and a resume follows it. The helper's own epoch re-read — and with it a second
`_epochs.Read()` per iteration — is gone too: the loop reads the stamp and the epoch once at the top, in the
documented order, so a seek landing mid-iteration is caught by the wait's own check.

*The falsifications, re-run against the merged loop.* `offeringTheStill` forced false fails
`ShowsTheStillOfThePositionASeekMovesToWhilePaused` and `EndsAtOnceWhenThePausedPositionHasNoPicture`; the
`shownEpoch` term dropped from it fails `DoesNotShowAStillWhenNothingMoved`; and the `if (presenting)` wrapper
removed fails the still test **after the test was tightened to say what the wrapper is for** — "a still claims
no anchor": with the wrapper gone the still anchors the clock to the keyframe it showed (18 s) instead of
leaving it where the seek asked (20 s), and the next frame would then be two seconds early on resume and
waited out. That assertion (`Position >= 19 s`) and "a still is not a presentation, so nothing about it is
late" (no drops) are what the wrapper is now pinned by.

*Verified:* 153 tests, all passing, three consecutive full runs, x64 and ARM64 clean. The render section is
52 lines shorter than it was (99 inserted, 151 deleted across the two files).

*Recorded:* §4.3's still bullet (one loop, presentation steps skipped), §11's peeked-frame row and
Appendix B's still row (neither names a helper that no longer exists).

**S9ad — The entry becomes a thing, and the picture looks before it takes.** *Done, from a review the user
asked for: "Review this plan: 1. Turn (long Tag, T? Item, bool IsLast) into a named class MediaItem<T>; 2. All
MediaQueue API now exposes MediaItem<T> instead; 3. Add a non-blocking bool TryDequeue(MediaItem) API, which
internally compares object ID." — with the amendments agreed in the review: a blocking `Peek` (the plan was
silent on the empty queue), `TryDequeue(entry)` with no out parameter, the entry owning nothing, and the
producer's calls unchanged.*

*Why the wrapper is the right shape.* The conditional take's token had been the *tag* — "the head is still
the entry I looked at, because a clear always comes with a new position" — which is sound but indirect. As an
object, the entry *is* the token: identity says exactly what the take needs, and it says it for the end entry
too, which is an entry with no media in it and therefore indistinguishable from "nothing" in the old
`TryPeek(out T? item, out long tag)`. That ambiguity was real, not cosmetic: the drop rule wrote
`closer is not null` to mean "the head is media", and it was correct only by luck. As a bonus, `MediaItem<T>?`
makes null mean exactly one thing everywhere — the queue is closed — because "the end of the stream" is an
object like any other.

*What changed.* `MediaItem<T>` (new, `Internal/MediaItem.cs`): sealed, immutable, no `IDisposable`, no
equality overload — identity is the token, and an overload would silently break it. The queue's consumer side
is now four words: `Dequeue()` (waits; takes; null = closed), `Peek()` (waits; takes nothing; null = closed),
`TryPeek(out entry)` (answers now), `TryDequeue(entry)` (takes *that* entry, or nothing, by
`ReferenceEquals`), with a private `TakeHead()` doing the counting and the pulse once. The producer side is
unchanged: `Enqueue(T item, long tag)` and `EndStream(long tag)` build the entry on the way in — the entry is
the queue's thing, and a producer has nothing to build for an end of stream.

*The render loop, which is what all this was for.* `VideoRenderCore` no longer holds a frame across
iterations: it looks (`Peek`), decides (is this position's next frame due? is it even this position's?), takes
what it looked at (`TryDequeue`), and settles it inside the same iteration — shown, dropped, or let go of.
The frame a slow decoder has not produced yet, and the frame whose moment has not come, are waited for *in the
queue*: the loop holds nothing while it waits, the exits need no cleanup (`queue is null`, a refused surface,
a render failure, the stop), and there is no `frame`/`frameEpoch` pair to keep straight. The stale-frame
window is covered where it belongs — the taken entry's tag is checked against the epoch before the anchor is
claimed — and the take itself cannot hand over anything the loop did not judge: a clear disposes what it
holds, so a head that is no longer the looked-at entry is refused rather than taken.

*The falsifications, all run before the code was kept.* `TryDequeue` comparing the tag instead of the identity
fails `TakesTheEntryThatWasLookedAtRatherThanWhateverIsAtTheHead` on "the entry that was looked at is gone, so
nothing is taken" — the replacement entry carries the same tag, which is exactly why the tag is not a token.
`Peek` answering without waiting fails `ReleasesAConsumerWaitingForALook` on "there is nothing to look at
yet". And deleting the loop's wait-for-the-moment (`if (playing && … == FrameSyncAction.Wait)`) fails
`SeeksWhileTheNextFrameIsStillWaiting` — the frame is shown immediately, and the picture's own bookkeeping
falls over precisely as the test's arithmetic expects ("showed 0 frame(s) where 1 were expected").

*Verified:* 155 tests (153 run, 2 skipped without an audio device), 0 failed, three consecutive full runs, x64
and ARM64 clean. Two new queue tests: `ReleasesAConsumerWaitingForALook` (released by an entry, by the end of
the stream, by a close — the look takes nothing throughout) and
`TakesTheEntryThatWasLookedAtRatherThanWhateverIsAtTheHead` (identity against a replacement with the same tag,
deterministic, no timing).

*Recorded:* §3's consumer row (a close releases a consumer with nothing), I17 (the look/take pair, and why
holding media while waiting is what makes the freeing logic complicated), §4.3's still bullet (nothing is held
between the look and the hand-over), §12 (the queue's four words), Appendix B (the four waits, and the entry
row), and `MediaQueue<T>`/`MediaItem<T>`'s own docs.

**S9ae — The clock belongs to the render threads, and nothing is notified.** *Done, from a rule the user
stated: "The clock can only be modified by video render thread and audio render thread. Introduce a
_clockLock, read and write starvation flag, anchor flag within the lock by video/audio threads. No more
notification."*

*Why the notification had to go.* The clock decision had been assembled from two sides: each stage posted what
it knew ("I have nothing to play", "I am hungry"), the queue turned those into flags, and the threads read the
result — so the facts lived in one place and the decision in another, and the two were kept in step by posts.
The user's rule is the other arrangement: the threads that use the clock are the ones that change it, so a
thread that learns something decides on the spot, and there is nothing left to send, nothing to miss, and no
second path between a fact and the decision it feeds.

*What changed.* `_clockLock` guards the clock and every fact its decision is made of
(`_clock`, `_clockEpoch`, `_anchorPending`, `_videoStarved`, `_audioStarved`);
`UpdateClock()`/`ApplyClock()` run at the top of both render loops, so a state change, a new position or a
verdict that changed is answered as soon as either thread wakes; `SetVideoStarved`/`SetAudioStarved` write the
flag and re-decide the clock under one acquisition; the two starvation *posts*
(`RequestVideoStarved`/`RequestAudioStarved`) are gone. `SetState` no longer touches the clock at all, and
`SeekCore` no longer arms the anchor, clears the flags or places the position — the seek publishes a request,
and the render threads notice the epoch it came with. `StarvedFor`, `Position`, the device watch and the drop
rule read under the lock, and `StopCore` resets the fields under it.

*The consequence, stated rather than discovered later.* The clock's position after a seek, and the clock
stopping when a pause arrives, are now *asynchronous*: they happen on the first render thread to wake, not
inside the command. Four tests had asserted them at the moment the command returned
(`SeeksWhilePlayingAndLeavesItPaused`, `SeeksWhilePausedAndStaysPaused`, `PausesAndResumes`, and
`SeeksWhileTheNextFrameIsStillWaiting` — missed at first, and it flaked about one run in four until it was
converted too) and were converted to waits, with the reason written where they are: the clock is the render
threads' to place and stop.

*The falsifications.* Removing `UpdateClock()` from the audio loop fails `PausesAndResumes`; from the video
loop it fails `ShowsTheStillOfThePositionASeekMovesToWhilePaused`; filling `AnchorClock` in unconditionally
fails `WaitsForADeviceThatIsNotMovingAndThenCarriesOn`.

*Verified:* 155 tests (153 run, 2 skipped without an audio device), 0 failed, x64 and ARM64 clean.

**S9af — The clock is decided only when something about it changed, and the claim replaces two fields.**
*Done, from a review: "Try to minimize lock entrance: 1. Try to merge UpdateClock into starvation detection
logic, since they are related; 2. Minimize the lock range, first detects for changes (e.g. starvation changed,
playback state changed, anchor needed), only enters the lock when there are changes as this is an expensive
operation; 3. Why do we need _clockEpoch?"*

*Why `_clockEpoch` was two fields pretending to be one.* It existed so that "the clock has been placed for this
position" was decided once *across* two threads: with two render threads both noticing a new epoch, the second
one had to be told the first had already placed the request, or it would place the request over the media's
answer if the media had claimed the clock in between. The claim itself already says that. While nothing of the
media has claimed the clock for a position, the request is the only thing there is to place and placing it
twice places the same value; once it has been claimed, the claim's own position refuses the request. So
`_clockEpoch` and `_anchorPending` both disappear into one `volatile long _anchoredEpoch` — the position whose
clock the media claimed — and with them the whole idea of *arming* an anchor: a new position is simply one
nothing has claimed yet, which is what the seek always wanted and could not have while the arming belonged to
the queue.

*What changed.* The video thread computes its verdict and decides the clock in one block at the top of its
loop (the merged answer to 1): the verdict is all reads — the clock answers its own position, the tail and the
end of the file are interlocked, the claim and the flags are volatile — and the block is entered only when one
of its three inputs has changed: the verdict itself, the epoch, or the change stamp (the answer to 2, and the
stamp is what makes a state change detectable without the lock, since a caller that changes the state always
signals). The audio thread keeps its verdict's own gate (`SetAudioStarved` returns before the lock when the
verdict is the one already published) and adds the same stamp/epoch gate for the position and the state, which
is what an audio-only file needs — there is no video thread in it to stop the clock. `ApplyClock(epoch, state)`
takes the position and the state as arguments now instead of reading them, so what it decides is exactly what
the caller detected. Everything else a reader could want without the lock no longer takes it: `Position` is the
clock's own answer, `StarvedFor` is two volatile reads, the device watch and the drop rule read the claim
directly.

*The falsifications, all four run against the finished shape.* Removing the placement guard in `ApplyClock`
passed the whole suite — a **coverage gap, not a proof**: no test caused a decision after the claim had been
made, so `KeepsTheClockWhereTheMediaPutItWhenSomethingElseChangesIt` was written for it (seek to a position the
file cannot resume at, play until the media's position replaces the request, pause, and the clock must still be
where the media put it), and with the guard removed it fails while
`StartsWhereTheFileResumesRatherThanWhereTheSeekAsked` still passes. Removing the audio thread's clock block
fails four tests (`PlaysAgainAfterAStop`, `PlaysAnAudioFileThroughTheRenderer`,
`SeeksWhilePausedAndStaysPaused`, `SeeksWhilePlayingAndLeavesItPaused`); removing the starvation
recomputation from the merged block fails `HoldsTheClockBackWhenTheVideoCannotCatchUp` and
`TellsTheClockOnlyWhenWhatItShouldDoChanges`; removing the claim guard fails
`WaitsForADeviceThatIsNotMovingAndThenCarriesOn`.

*Verified:* 156 tests (154 run, 2 skipped), 0 failed, five consecutive full runs, x64 and ARM64 clean, four
falsification cycles reverted to this state.

*Recorded:* §5 (the clock belongs to the render threads; the request, the claim, and why a position needs no
re-arming), §3's table (the position change no longer carries the flags, the anchor or the clock's position; a
new row for the clock's inputs; the clock object's writers), §6's steps 3–5, §7, I10, I14, §11 (a field for
"the position the clock has been placed for", and deciding the clock on every iteration under the lock),
§12 (the clock lock is not the deadlock class), §4.4's ordering rule, Appendix A item 30, and Appendix B.

**S9ag — The picture's wait ends when its verdict could change.** *Done, from a report: "VideoRenderCore has
a bug: Peek has no timeout, a long blocking makes the thread unable to report the starving state, which
prevents the clocking from pausing. A fix is to add an optional timeout parameter to Peek. If video is already
starved, call Peek without timeout. If video is not starving yet, call with timeout=RemainingTime."*

*The bug, confirmed before anything was changed.* The picture's starvation is read at the top of the render
loop, and the thread is parked on the frame queue between iterations: a decoder that goes quiet leaves it with
nothing to wake it, so the clock runs on while the picture starves, and the hold arrives when the next frame
turns up — as late as the decoder was quiet. A test was written for it first
(`HoldsTheClockBackWhileTheDecoderIsQuiet`, an audio fixture, a fast first frame and a 1.5 s one after it, an
800 ms bound): against the unfixed code the hold never arrives inside the bound.

*What changed.* `MediaQueue<T>.Peek` takes an optional time (`Peek(int millisecondsTimeout =
Timeout.Infinite)`), and `IsClosed` — which the queue already had — is what tells a null from a timeout apart
from a null from a close; the loop distinguishes them and re-reads the verdict on a timeout rather than ending
the thread. `PlaybackPipeline.IsVideoStarved(epoch, out int waitMilliseconds)` answers both the verdict and
how long its thread may wait before the verdict could change — the time left before the clock reaches the
threshold (`(threshold − behind) / rate`, clamped), which is the only thing that changes a judgeable verdict
without an entry. The loop passes it to the wait, and the two of them are the fix.

*The two amendments, both forced by measurement.* **(a)** "Not starving yet" is three different situations.
Where the verdict is judgeable it is the clock's progress that changes it, so the deadline is exact. Where it
is *not* judgeable because nothing of this position has claimed the clock yet, the deadline does not exist —
and with a sound the claim is the sound's own first block, handed over on *another* thread, so no entry is
coming either: that wait is bounded by the housekeeping interval (`IdleWaitMilliseconds`) instead. Without a
sound the claim is made by this thread as it shows the first frame that plays, which is an entry like any
other, and the wait needs no bound. Measured: with the bound removed, `HoldsTheClockBackWhileTheDecoderIsQuiet`
fails on three runs of three (before that, the test raced the sound's claim and only failed two runs in three
— the arrangement was then made deterministic by pacing the sound's first block, which is what puts the
picture's judgement before the claim every time). **(b)** Where nothing can change the verdict with the clock —
already starving (only a frame can end it), a clock that is not running, the file run out here, nothing decoded
yet — no time is given at all: a wait that fires without the verdict changing is a poll, which is the mistake
the queue's old fixed timeout was removed for (S9aa).

*Two things found on the way, neither of them this bug.* A file whose packets have all been read is a file that
has run out, and the ending suppresses the verdict — so a *short* file with a slow decoder never reports
starvation at all, since `IsEnding` is the reader's fact rather than the decoder's end. That is what the first
version of the test tripped over (a 6-frame fixture: with the ending suppression removed the test passes), and
it is left alone here: what should replace `IsEnding(epoch)` in that condition — the render thread's own
`endingEpoch`, i.e. the decoder's stream having ended rather than the reader's file — is a design decision to
be taken on its own. And a *still* shown for a position that turns out to be playing claims no anchor by
design, so a fast first frame shown while the state was still `Paused` leaves the clock unclaimed until a
*second* frame plays; that is what makes amendment (a) reachable rather than theoretical, and the second test
below depends on it deliberately (a soundless file, where the claim is this thread's own).

*The falsifications.* Removing the deadline (`waitMilliseconds = Timeout.Infinite` in the judgeable case)
fails `HoldsTheClockBackWhenTheClockReachesTheThreshold` — the hold then arrives with the *next* frame,
1300 ms later, outside the 800 ms bound — and fails `HoldsTheClockBackWhileTheDecoderIsQuiet` too. Removing the
unclaimed bound fails `HoldsTheClockBackWhileTheDecoderIsQuiet` in three runs of three. Making the timed look
wait forever (`Monitor.Wait(_lock)`) fails
`AnswersNothingWhenTheTimeGivenToALookRunsOut` on "a look with a time on it has to end by itself". The starved
case arming a deadline anyway is a poll rather than a defect any test can see (the clock is already stopped, so
a 1 ms wake changes nothing but the processor) — argued from the code rather than measured, and said so.

*Verified:* 159 tests (158 passing), x64 and ARM64 clean, three consecutive full runs. **The one failure was
not this change, and it was reconciled in S9ah:** the device-stall warnings were rewritten in the working tree
(`WarningAudioDeviceStalled`, "The audio device is stalled.", one line and no recovery line) while the test
still expected HEAD's pair ("not moving"/"moving again") — the stall line arriving at all is what proves this
change left the watch's `expected` condition alone. Three new tests:
`HoldsTheClockBackWhileTheDecoderIsQuiet` (the sound's claim is the reason the verdict would otherwise stay
unread), `HoldsTheClockBackWhenTheClockReachesTheThreshold` (a soundless file, where the deadline is the only
thing that can end the wait), and `AnswersNothingWhenTheTimeGivenToALookRunsOut` (the queue's timed look, and
`IsClosed` telling a timeout from a close).

*Recorded:* §8 (the wait and its deadline, and the claim case), I12, I17 (the three ways a look can end, and
why a caller's deadline is not the old sedative), §11's timeout row, §12 (five releases, not four), Appendix A
item 31, Appendix B (the consumer's four words, and the verdict's owner), and `MediaQueue<T>.Peek`'s own doc.

**S9ah — The device watch's contract is the watchdog's, not the pair's.** *Done, from your decision on the
reconciliation question: update the test (and the contract) to the new watchdog, rather than restoring the
"not moving"/"moving again" pair.*

*What the watchdog does.* `CheckStalledAudioClock` (renamed from `WatchDeviceProgress`) is still called by the
audio thread and still gated on a stall being *expected to show progress* (playing, nothing starved, the
position claimed, the clock running); it looks every `StalledAudioDeviceCheckIntervalMilliseconds` (100 ms) and
names a device whose position has not moved for `StalledAudioDeviceThresholdMilliseconds` (1000 ms) with one
line — `WarningAudioDeviceStalled`, "The audio device is stalled." — once per stall. Movement clears the flag
and says nothing, so a later stall is named again. A moment in which nothing is expected restarts the
countdown, as it did before.

*What changed for it.* The test now waits for "stalled", asserts the line count is one *after the device has
come back* (the recovery is not announced), and pins the recovery by the picture rather than by a message:
`WaitForPresentedFrames(presentedWhileStuck + 2)`, no drops, and the stall line still the last thing said.
I13 and §9 were reworded to that contract, with the reason stated rather than implied: a line about a freeze
explains something the user cannot see, a line about a recovery explains nothing they cannot see — and the
device watch's own contract row (I9) and the `WarningRaised` row in Appendix B were updated with it.

*Left alone, deliberately.* `MediaEngineDesign.md` (two lines) and `PlaybackPipelineRefactor.md` (its P1/P3
table) still quote the old pair; they are design narrative rather than the contract this pipeline is held to.

*Verified:* 159 tests, 0 failing, x64 and ARM64 clean, three consecutive full runs.

**S10 — One wait per thread: the communication mechanism rebuilt.** *Done, from a design the user set out:
"we need a large refactor on the communication machanism: 1. Remove _playbackStateLock and split into 4
PipelineNodeLock (already implemented) for each thread; 2. Introduce TryWakeXXX() API for each thread, this
method checks the wake condition and wake the target PipelineNodeLock; 3. Makes all API of MediaQueue
non-blocking, each thread now waits only on its own PipelineNodeLock. After TryDequeue/TryEnqueue, calls the
producer/consumer's TryWakeXXX()" — with three amendments from the review that followed: the queue enforces
no limit (the producer owns the window), the picture's idle wait keeps a cadence, and that cadence must not be
derived from clock state.*

*Why the previous fix could not be repaired where it stood.* The render thread slept in two locks: the state
lock and, since S9ag, the frame queue's. A command pulsed only the first, so a thread inside `Peek` could not
be reached at all — its only releases were an entry, a close, or the timeout. And the timeout was `Infinite`
in the states that mattered (already starving; paused with a still pending, where the verdict is not computed
at all), so a pause, a second seek or a surface change could wait forever. `WakesForEachSurfaceChangeWhilePausedRatherThanWaitingForTheBackstop`
passed only because the queue was full at that moment, so the thread was on the *state* wait. The defect was
structural: **the waiter waited on a lock the waker did not own**, and no timeout converts two locks into one.

*S10a — the node.* `PipelineNode` (renamed from `PipelineNodeLock.cs` to match the class) documented as the
one place a thread sleeps: one waiter per node, the stamp read before the decision, a wake that may be
spurious and must never be missed, a wake that never waits for the woken thread, and `WakeCount` as the
diagnostic that proves a parked pipeline is not polling. `PipelineNodeTests`: the already-waiting wake, the
wake that arrives *between* the stamp read and the wait (the window the whole design rests on), the timeout,
the wake that does not block the waker, and the stamp/wake count. *Falsified:* removing the stamp comparison
fails `DoesNotSleepWhenAWakeArrivedBeforeTheWait`.

*S10b — the reader.* The reader sleeps on `_readerNode`; the read-ahead wait loses its 15 ms tick
(`ReaderCheckIntervalMilliseconds` deleted) because the two consumers now call `TryWakeReader()` after a take.
`SignalPlaybackStateChange` temporarily wakes the node too, so each step converts one thread and the suite
stays green throughout. *New test:* `KeepsTheSoundComingAfterTheReadAheadIsUsedUp` — a three-second wave read
in several passes, which is what makes the reader wait at all. *Falsified:* removing the audio take's wake makes
it fail on "Playback did not finish" (the reader parks for good).

*S10c — the decoder.* `MediaQueue` loses its limit and every wait: `TryEnqueue` (false only when closed),
`TryDequeue(out entry)`, and no `Monitor` at all. The frame window moves to the decoder
(`HasRoomForAFrame`, checked **before a packet is taken**), which is what keeps a decoded frame from ever being
held across a wait — a drain may push the queue past eight for the moment it takes the picture to catch up,
which is memory rather than correctness. `TryWakeVideoDecode` (packets present **and** room) is called by the
reader on enqueue, by the picture on take, and by the seek's clear. *New test:*
`KeepsThePictureComingAfterTheFrameWindowIsUsedUp` — the AV1 fixture, whose 80 frames at 30 fps use the window
up many times over (the first attempt used a y4m with a frame-rate argument that does not mean what its name
suggests; the fixture was the problem, not the wake). *Falsified:* removing the take's wake stops the picture
one window in and the file never ends.

*S10d — the picture, which is the bug.* `TryPeek` plus a wait on `_videoRenderNode`; the contention between a
frame arriving and the clock reaching a frame's moment is now two waits on one node. `IsVideoStarved(epoch)`
went back to being a pure predicate: no out-parameter, no `Rate`, no `IsRunning`, no branch that decides
"nothing can change". **The idle wait while playing is always a fixed cadence**
(`VideoIdleCheckMilliseconds = 50`) and while paused it is unbounded — because time is the only writer that
cannot send a wake, and a cadence derived from the clock's state is a prediction about another thread's state.
`AnchorClock` wakes the picture (the verdict becomes judgeable), the surface event wakes only the picture, and
`WakeAll` covers the controls. *New tests:* `TakesUpASurfaceChangeWhileThePictureWaitsForItsPosition` and
`TakesUpASeekWhileThePictureWaitsForItsPosition` — both wait in the paused-with-a-still-pending state, where
the wait is unbounded and only a wake can reach the thread. *Falsified:* removing the render node from
`WakeAll` makes both hang — not fail: the parked thread never sees the stop, so the join never returns. That is
the stated price of there being no backstop, and it was observed rather than assumed.

*S10e — the sound.* The audio thread sleeps on `_audioRenderNode`: the resume wait is unbounded (a control
wake), the device waits keep `DeviceRoomCheckMilliseconds` on the same node (a device cannot signal), and
`WaitForAudioRoom`'s paused branch lost its 250 ms tick.

*S10f — the deletion.* `_playbackStateLock`, `_playbackStateStamp`, `SignalPlaybackStateChange`,
`WaitForPlaybackStateChange`, `MediaQueue.Dequeue`, `MediaQueue.Peek(int)` and every `Monitor` in the queue are
gone; `SignalPlaybackStateChange` became `WakeAll` (four wakes). `MediaQueueTests` was rewritten to the
non-blocking contract: seven of its fourteen tests were about waits that no longer exist, and the queue's
guarantee is now "every word answers at once" (`AnswersAtOnceOnceItIsClosed`).

*S10g — the contract.* `PlaybackPipeline.md`: a new §2.3 with the wake table and its two ordering rules, §2.6/7
rewritten (one wait per thread; a cadence never derived from clock state), §3's queue table (no maximum, no
waits, the clear wakes nobody), §7's stop paragraph (a missing wake is a join that never returns), §8's
cadence, I12 and I17 rewritten, I18 added, §11's two new rejected shapes, §12's release table, Appendix A
items 31–32, and Appendix B.

*Verified after every step; final state:* 162 tests (160 run, 2 skipped without an audio device), 0 failed,
three consecutive full runs, x64 and ARM64 clean. Every step compiles on its own, with the pipeline green
between steps — the transitional `SignalPlaybackStateChange` is why.

**S11 — A position's first media holds the clock, on both sides.** *Done, from a flaw the user named: "video
render thread waits a fixed time when there is no frame presented in the queue yet, especially when playback is
just started. Since the audio render thread might have already started the audio rendering (as well as starting
the clock), the first few frames that come later might be considered as Dropped frames. IsVideoStarved returns
false since tailTicks < 0... My idea is, both video and audio should be considered starving at the beginning of
an epoch." — with their two amendments: the verdicts stay in their own threads, and the diagnostic is not to be
overdesigned.*

*The flaw, confirmed by measurement before anything was changed.* The clock's start was gated only by the sound:
the audio thread finds the device empty, holds; the first audio block releases it; `ApplyClock` starts. The
picture's verdict said `tailTicks < 0 → not starved` — "nothing decoded yet is not fallen behind" — which is
right about behindness and wrong about readiness. Measured with a decoder that takes 400 ms on its first
packet (audio+video, 100 ms samples): the clock ran to 435 ms with an empty picture, **ten frames were dropped
as late**, and the picture started at **666 ms** — 0.67 s of the file never shown — with `StarvedFor` empty
throughout, because a video that has produced nothing had no way to say so.

*The rule.* The picture holds the clock while **this position has produced nothing to show**: nothing shown
for the epoch (`showingEpoch` — the render loop's own memory) and nothing waiting in its queue. It is asked
before the clock's claim, because with sound the claim is the first audio block and a claim-first test waves
the picture through exactly when it has nothing. It is *not* asked from the decoder's tail: the tail is
written after the frame is enqueued, and while a decode is in flight after a seek it still holds the previous
position's value — which is *ahead* of the new request, so a tail-based verdict finds the picture comfortably
ahead. `IsEnding(epoch)` still releases everything (a file that has run out is played out, and that is also
what releases a position whose picture never produces a frame at all), and an end entry counts as "something
to show" so a video stream that ends without frames finishes instead of holding. The sound's rule needed no new
fact — an empty device is already "nothing to play" — but its verdict was published one iteration *after* the
clock was decided in the same iteration, so it is now set right after the epoch's flush, on the thread that
owns it.

*The tests.* `HoldsTheClockBackUntilThePictureHasSomethingToShow` (the measured arrangement): the hold is
reported before the first frame, the clock is stopped, the first frame is shown at the start of the file with
**zero** drops, and time starts with it. `HoldsTheClockBackUntilThePictureHasSomethingToShowAfterASeek` (a
backwards seek with the same slow first decode). `HoldsTheClockBackWhenAPositionMovesDuringADecode` — a gated
decoder held *inside* a decode while the position moves underneath it, which is the only way to make the stale
tail's window deterministic; a real seek does exactly this to a real decoder.

*The falsifications.* Removing the term brings the measured failure back (10 drops, a 666 ms start). Removing
the fixed cadence still fails the two cadence tests. And the tail variant (`tail < 0` as the readiness fact)
passes the startup and seek tests — the decode thread resets the tail within microseconds of the epoch change —
but **fails the gated test**, which is what makes that test worth its decoder double. One honest note: the
audio's epoch-start line has no test that distinguishes it, because the ordering it fixes (one start/stop at the
beginning of a position) is a race whose count is not stable; it is justified by the reasoning, not measured.

*Side effect found and fixed.* `HoldsTheClockBackWhenTheClockReachesTheThreshold`, the silent-file cadence test,
was racy: the first frame can be presented as the paused position's *still* (claiming nothing) when the decoder
is fast, and then no frame claims the clock and the verdict is never judgeable. It now uses a decoder that is
slow on its first packet — so playback is certainly running before the first frame arrives — and 1300 ms after
it, which is the arrangement the test is about.

*Verified:* 165 tests (163 run, 2 skipped), 0 failed, three consecutive full runs, x64 and ARM64 clean.

*Recorded:* §8's starvation table and the new "both stages hold the clock at the start of a position"
paragraph, §14.6 (a pathologically slow first decode freezes both, by design), Appendix A item 33, and the
`IsVideoStarved` doc.

**S11b — The clock is taken once, and `Position` is not a fact to decide on.** *Done, from a review: "Video
thread should avoid using Position property directly, that's for external caller. Moving the clock existence
check out of IsVideoStarted and acquire position there." — then: "Why keeping clock == null branch? Does this
scenario really exist?"*

*The answer is no, and the ordering is what makes it no.* The clock is created or selected during
`PreparePlayback` — before any thread is started — and `StopCore` nulls it **after** all four joins. So while a
media thread runs, the field cannot be null; `clock is null` in the video loop, in `ApplyClock`, in
`CheckStalledAudioClock` and the `_clock?.` in `AnchorClock` were four dead branches, and the previous change
had added one more (a `Position`-style zero fallback for a case that cannot occur).

*What replaced them.* Each render thread takes the clock **once**, at the top of its loop, with
`?? throw new InvalidOperationException(...)` — the invariant stated where it is relied on, and a loud pipeline
bug if a future change breaks the open's ordering. The clock is then handed to the helpers that need it
(`ApplyClock(clock, epoch, state)`, `AnchorClock(clock, …)`, `SetAudioStarved(clock, …)`,
`CheckStalledAudioClock(clock)`, `WaitForAudioRoom(clock, epoch)`, `IsVideoStarved(clock, …)`), so the field is
read by the open, the stop, the two threads' starts and the host's `Position`/`PictureLag` — and by nothing
else. The threads never read the host's `Position`: it answers zero when there is no clock, which is a state the
host may see and a running thread cannot be in. `CheckStalledAudioClock` also lost its `_audioDecoder is null`
check, which was dead for the same reason: the thread is only started when the audio decode exists.

*Recorded:* §5's new lifetime bullet (with why the threads must not read `Position`), Appendix A item 4, and
Appendix B's clock row.

*Verified:* 165 tests, 0 failed, three consecutive full runs, x64 and ARM64 clean.

**S11c — One clock decision per iteration, and a verdict belongs to the position it was computed for.**
*Done, from a review: "Why SetAudioStarved receive epoch from _epochs.Read() not local variable workingEpoch
or epoch" — then: "SetAudioStarved calls ApplyClock, then if (epoch != clockEpoch || stamp != clockStamp) calls
ApplyClock again. Looks like a waste."*

*The first was a leftover, and the second was worse than waste.* `SetAudioStarved` read `_epochs.Read()` and
`_state` itself because it had been written before it took a clock; that let a seek landing in between hand a
verdict computed for one position to another. It now takes the epoch and the state **from the caller** — the
facts the verdict was reached under — like `ApplyClock` and `IsVideoStarved` already did, and the same
correction was applied to the loop's own `ApplyClock(clock, epoch, _state)` call, which now passes the
iteration's `state`.

*The double decision was also load-bearing.* The take-up block called `SetAudioStarved(…, true)`, which decided
the clock; the block below then decided again for the same epoch and state. Removing it naively would have
broken a real case, because `SetAudioStarved` returns early when the verdict is unchanged — so a **second seek
while already starving** skipped its decision, and only the block below placed the clock for the new position.
The take-up now *feeds* that one decision: it flushes, records that this iteration took up a new position, and
the decision block — whose condition includes "took up a position" — publishes the verdict and applies the
clock once, in one lock entrance. The two mid-iteration verdict changes (nothing to hand over; a block arriving)
still decide on the spot, as they must, and they are after the decision block, so nothing is decided twice.

*Verified:* 165 tests, 0 failed, three consecutive full runs, x64 and ARM64 clean.

*Recorded:* §5's ownership bullet (once per iteration; a take-up always counts and feeds that decision).

**S11d — A hold on the clock is the pipeline's, and the device was releasing it.** *Done, from a question:
"How is `QueuedLimitMilliseconds` determined? Does the audio renderer have an upper limit on how many data it
can hold internally? Does the audio renderer block on `Submit`?"*

*The answer to the third one was the bug.* `WasapiAudioRenderer.Submit` ends with

```csharp
if (!clock.IsRunning) { clock.Start(); }
```

and its comment explains why: a stopped WASAPI stream does not consume its buffer, so without it the first
block fills the device and the rest wait forever. That reasoning is sound and the code is wrong, because
"the rest wait forever" is **the hold**: writing into a stopped device is what holding the clock back means,
and the device's own buffer filling is what tells the pipeline to stop handing blocks over.

What it cost, measured rather than argued — with the test double made to start the clock on submit, exactly
as the device did:

* `HoldsTheClockBackUntilThePictureHasSomethingToShow` failed at 445 ms of elapsed time with nothing on
  screen: the audio thread's first block arrives in milliseconds, the first frame after a slow first decode,
  and the block *started the clock* — so S11's startup hold never held on real hardware. Its 10 dropped
  frames were never fixed outside the tests.
* `HoldsTheClockBackWhenTheVideoCannotCatchUp` failed with the picture 1.30 s behind the sound: each block
  handed over during a video hold restarted the device, and once the decoder's frame window is full nothing
  wakes the video thread to pause it again.

*Nothing could see it,* because the double's `Submit` was silent about the clock and the only renderer that
starts it is the one that needs a device. Two things now make the hole visible: the double documents that a
submit leaves the clock alone, and the contract is stated where a new backend will read it (`IAudioRenderer`,
`WasapiAudioRenderer`'s class doc and `Submit`'s comment).

*The fix is the deletion of those four lines,* and the reasoning that makes it safe is worth writing down,
since it is the answer to "does anything else start the device":

* The clock is started by `ApplyClock` and nowhere else (I10), and the audio thread decides before it hands
  a block over — `SetAudioStarved(…, false)` comes first, and at the start of a position the verdict change
  is guaranteed, so a decision always runs before the first submit.
* The clock is device-position based (`_base` plus frames the device has *played*), so writing into a
  stopped device offsets nothing: the buffered audio is ahead of the clock, not behind it.
* A stopped device that fills up parks the sender in `WaitForAudioRoom`, on its own node, where a pause, a
  seek and a stop all reach it.
* A park *inside* `Submit` still ends: `StopCore` flushes the renderer before it joins, and `Reset` frees
  the whole buffer, so the waiting write finds room and returns.
* `QueuedLimitMilliseconds` (100 ms) sits below the buffer the renderer asks WASAPI for (200 ms), so the
  sender normally stops before the device is full; when an endpoint refuses the longer buffer the renderer's
  own wait does the pacing instead, and the hold is still a hold because the stream is stopped.

*(The last four bullets describe the pipeline as it stood at S11d. S11e — immediately below — replaced
`WaitForAudioRoom` and the 100 ms target with a hand-off that returns, so the wait is the thread's own and no
number describes the device; the reasoning above about the clock still holds, and the arguments about *where*
the wait lives are the ones S11e supersedes.)*

*Verified:* 165 tests, 0 failed; x64 and ARM64 clean. The falsification is the pair of failures quoted above,
run with the double starting the clock and reverted.

*Recorded:* I19, and §2.3's wake table gains the sentence that a hold is enforced by a stopped device.

**S11e — The hand-off returns instead of waiting: `WAIT` → `YIELD`, and the audio thread's wait moves home.**
*Done, from a review: "Since `Submit` internally implement blocking and timeout for us, there is no need for us
to add a second threshold. Remove `WaitForAudioRoom`" — then the design that replaced it: "Makes it
`IEnumerable<SomeInfo> Submit(buffer)`, so we can yield from within Submit if the device has no room. Which
left the caller to decide what to do: wait (on its own Monitor)? or break … or other ops?"*

*The first proposal was half right and fatal in the half it missed.* `Submit` did block, but its block was a
1 ms spin loop on the caller's thread, `Thread.Sleep`-ing where no wake could reach it — and its 2 s give-up
only ran while the clock was running, so a paused device that never drained waited for ever. Deleting
`WaitForAudioRoom` on top of that would have moved the audio thread's only wait into a place where pause, seek
and stop could not reach it. The measurement that settled it was the same falsification as S11d: a double that
starts the clock on submit fails `HoldsTheClockBackUntilThePictureHasSomethingToShow` and
`HoldsTheClockBackWhenTheVideoCannotCatchUp`.

*What was built.* `Submit` no longer hands over a block; it starts a **hand-off** (`IAudioSubmit`), which the
caller drives with `MoveNext()`. A hand-off that cannot continue says `AudioSubmitYield.NoRoom` and keeps its
place; the pipeline then waits on its own node and resumes it. The contract is stated in one place
(`IAudioSubmit`): single use, `Dispose` abandons (the frames already in the device stay there, so the caller
flushes), a renderer never sleeps or retries, and a backend that can accept an unbounded amount must impose
its own high-water mark and yield the same way — otherwise the pipeline becomes "decode the file into RAM".

*What went with it.* `WaitForAudioRoom`, `QueuedLimitMilliseconds` (the second threshold), the 2 s
`MaxWaitMilliseconds` give-up, the renderer's shared `_staging` field (the bytes moved into the hand-off, and
are rented per block from `ArrayPool<byte>`), and `StopCore`'s flush-before-the-joins — which existed to free
the room a parked `Submit` was waiting for, and is now a plain flush after them, when no thread of ours can be
inside the device (the concurrency question, answered by construction rather than by a lock).

*What replaced them.* An in-flight slot in `AudioRenderCore` (`inFlight`/`inFlightBuffer`), kept across a pause,
released on completion, dropped on a seek's epoch change, and released in a `finally` so no exit path leaks the
block. `QueuedSampleCount` lost its back-pressure meaning and is now only "the device still holds something of
ours" (the sound's half of the ending, and the starvation verdict).

*Verified:* 167 tests, 0 failed (two new), x64 and ARM64 clean. The endpoint was driven for real:
`SaysItHasNoRoomRatherThanWaiting` fills a 200 ms/9600-frame buffer with a stopped stream and gets `NoRoom`
back in 7 ms, then gets the room back from `Flush`. Falsifications that failed as they should: a yield treated
as "let go" (`WaitsForADeviceThatNeverTakesAudio` and the new test both saw the block abandoned instead of
kept), and a seek that does not reach a suspended hand-off (the new test timed out waiting for the abandon).

*Recorded:* I20, §2.3 (a device wait is the thread's, and why), §4.4, §9, §11 (two rejected shapes: a wait
inside the renderer, and a queued-limit/capacity number in the pipeline), Appendix A item 34, Appendix B.

**S11f — The sound's device belongs to one pipeline for one open.** *Done, from a request: "Use a factory for
audio renderer creation as well, so each pipeline owns its own WASAPI audio renderer."*

*Why it was wrong before.* `PlaybackPipeline` took an `IAudioRenderer` **instance**, and every host created
one and kept it: `PlaybackEngineAdapter` had a single `WasapiAudioRenderer` for the life of the app, and so did
the Lab. That is one device stream, one clock and one flush shared by every pipeline built from that host — so
a second pipeline would have played at the first one's position, and closing either would have reset the
other's device. Nothing enforced "one thread per renderer" either; the pipeline's own submit and flush were the
only users, by luck of the wiring.

*What changed.* The constructor takes `IComponentFactory<IAudioRenderer>` (`WasapiAudioRendererFactory`, which
caches nothing) exactly as it already took the video renderer's factory. `PrepareAudio` asks for a renderer,
configures it, publishes it, and applies the host's volume and mute — a value set before the open is applied to
the device the open creates, ordered so that neither a concurrent setter nor the open can lose it.
`StopCore` takes the field away, flushes, and disposes it after every join, so the device is never touched by
two threads. The render thread also takes the renderer once per iteration, like the clock, instead of reading
the field at each use.

*What the host had to give up.* `Volume`/`IsMuted` were read off the renderer instance it owned, so both moved
to `PlaybackPipeline` (kept, applied per device) and the adapter now stores the two values and forwards them to
whatever pipeline is current. Behaviour is unchanged — including reading them back after setting them, which is
what the settings page does.

*Verified:* 169 tests, 0 failed (two new), x64 and ARM64 clean, and the real endpoint was driven again
(200 ms buffer, `NoRoom` in 8 ms). `CreatesItsOwnAudioRendererForEachOpen` fails when `StopCore` is stopped
from disposing (0 disposals instead of 1), which is the line the change is about. The device tests skipped once
mid-session with `0x80070490` (no default endpoint enumerating) and passed on the next run: environment, not
code — the renderer was untouched by this step.

*Recorded:* §2.2's ownership table (one device stream per pipeline per open), §14 items 7–8 (the session
volume two pipelines share, and the graphics device two pipelines must not), Appendix B.

**S11i — A component does not diagnose; it preserves.** *Done, from a review that rejected the first attempt
outright: "How do you know it must be 'the device has gone', not other reason?" — then: "we have to preserve the
original, unaltered exception info so it makes the diagnostic and debug much easier."*

*What I had written was a guess wearing a diagnosis.* Every catch in the WASAPI clock and the renderer's padding
reads did `catch (Exception) { _deviceLost = true; }` and then reported `"The audio device was lost."` — a
statement about the world inferred from the mere fact that something had failed. `Stop()` can fail because the
client was never initialised, the service died, a marshalling bug exists, or memory ran out; none of those is
"the device is gone", and the string was on its way into the user-facing `Error`.

*What replaced it.* The exception itself. `WasapiAudioClock` keeps the **first** one as `Cause` (a later failure
is a consequence, and replacing the origin hides it), latches the loss, and reads as a frozen position — it
cannot throw, because the host's position timer and every frame the picture judges read it. The renderer reports
that cause on the next `Submit`/`Flush` as `Failure($"{context}: {cause.Message}", cause)`: the context says what
was being attempted, the message and the object are the original. Deliberately-ignored calls (`Dispose`'s stop,
the volume members, an endpoint with no session volume) now write the exception out whole at W/D level instead
of vanishing. And `PlaybackPipeline.Fail` — the single place a failure becomes state — logs `Logger.E(TAG, …,
exception)`, so **every** failure in the engine reaches the log with its stack through one choke point.

*What is deliberately absent.* No HRESULT table. Enumerating the causes is impossible (the user's point), and a
partial table would be worse than none: it would classify the listed ones and guess at the rest.

*Verified:* 173 tests, 0 failed (three clock tests, one pipeline test), x64 clean, endpoint tests run for real.
The falsification was instructive: replacing `_cause ??= exception` with `_cause = exception` **passed** the
first version of the test, because the clock's own calls early-return once lost — the only second failure comes
from `ReportDeviceLost`, so the test now reports one that way and fails the overwrite as it should.

*Recorded:* Appendix A item 36 (with the amendment the user made to it: *"A component does not diagnose; it
preserves, unless it has 100 % confidence on what is really happening"*), §9, Appendix B.

**S11j — Each player is its own stream, and its own device.** *Done, from: "Each player must have its own
volume/mute" and "video device isolation should go on with (a) Device per renderer."*

*Volume was a session control.* `ISimpleAudioVolume` belongs to the **session**, which belongs to the process, so
two players shared one slider and one mute button — turning one down turned the other down. The renderer now takes
`IAudioStreamVolume` (the stream's own service) and applies the level itself; WASAPI has no per-stream mute, so
mute is the level going to zero while the level the user set is kept, which is what makes unmuting restore it.
The arithmetic lives in `WasapiVolumeControl` behind a one-method seam, so `WasapiVolumeControlTests` pins it
without a device. `IAudioStreamVolume` had to be added to the three per-platform `NativeMethods.*.txt` files — the
root `NativeMethods.txt` is **generated** by a `CopyNativeMethods` target before every build, and editing it
silently does nothing (I found that the hard way: three compile errors, then a rebuild that reverted it).

*The video device was shared per factory.* A renderer draws through its device's **immediate context**, and a
frame is a *sequence* of calls on it — upload, set output, bind, draw. D3D11 protects each call, not the sequence,
so two renderers on one context can have one player's state land between the other's setup and its draw, and a
frame is composited into the wrong surface. The factory now creates a device per renderer, and the renderer owns
and disposes it. Cost: one device per simultaneous player; benefit: correctness by construction rather than by a
rule every caller must remember.

*Verified:* 178 tests, 0 failed, x64 and ARM64 clean; every GPU test and every endpoint test ran for real (not
skipped). New: `WasapiVolumeControlTests` (3), `PlaysTwoFilesAtOnceWithoutEitherTouchingTheOther` (two pipelines,
two devices, two clocks, both to `Ended` with all of their own sound), and
`CreatesItsNextRendererAfterThePreviousOneWasLetGoOf`. The last one was falsified by restoring the cached device —
it fails with `ObjectDisposedException`, which is exactly the state a shared device leaves behind.

*Recorded:* §14 items 7–8 rewritten (per-stream volume; a device per renderer), Appendix B.

**S11n — The clock is the pipeline's own; the sound's thread keeps it honest.** *Done, from the plan the user settled on: "Decouple from device clock entirely, use our own clock, and sync the clock whenever there is audio playing" — then: "If the device ever deviate from our own clock, we sync our clock to match the device clock, instead of using the device clock directly" — and: "Write sync clock logic in the renderer thread. Do not couple the clock with PrepareAudio or inject any correction source."*

*The defect this removes.* The pipeline's clock **was** the audio device's clock. A WASAPI position counts frames *played*, so it stops the moment the device has nothing left — and for a file whose picture outlasts its sound (the ordinary shape: the container's duration is the longest stream's) the clock stopped on the audio's last sample while frames were still due. Those frames waited for a moment that could never arrive, the picture's end-of-stream entry sat behind them, and the file never ended. Measured before the change: a 40-frame file (1.3333 s) whose audio ends at 1.2333 s froze at `0:00:01.2333283` with the next frame due at `0:00:01.266`, `queued=0`, `presented=38` of 40, `Playing` for ever.

*What changed.* `PreparePlayback` makes a `StopwatchClock` for every open — with sound or without — and nothing else replaces it. `PrepareAudio` records the device's clock separately (`_deviceClock`); it no longer touches the pipeline clock. Three rules keep the two in step, each written where the device is visible:

- **`ApplyClock` tells both**, always together: the pipeline's clock (what the picture judges time against) and the device's (what makes sound come out). One decision, because "time is passing" is one thing.
- **`AnchorClock` puts both on the media's timeline** at the moment the first media claims the position, so "the device says X" means "what is being heard is at X" from then on.
- **`SyncClockToTheDevice` runs in the audio thread** each iteration while the device holds unplayed audio: if the two clocks differ by more than `ClockSyncToleranceMilliseconds` (10 ms), the device wins and the pipeline's clock is moved to it. A device with nothing to play is not consulted at all — which is precisely what lets time go on after the sound has run out.
- **`CheckStalledAudioClock` watches the device clock**, not the pipeline's: a stopwatch that free-runs while the device idles is not evidence that the device is working.

*What was deliberately not done.* No injected correction source, no `Func<TimeSpan?>` handed to the clock, no clock knowledge of devices: the clock is a stopwatch, and the thread that can see the device is the thread that corrects it.

*Verified:* 180 tests, 0 failed, twice consecutively, x64 and ARM64 clean; every GPU and endpoint test ran for real. Two new tests, both of which fail with the clock put back to the device's (`Playback did not finish; the pipeline is Playing`): `EndsWhenThePictureOutlastsTheSound` (3 frames after the sound) and `PlaysTheTailAtItsOwnRate` (0.5 s after the sound, asserted to take real time rather than being flushed through). The fixtures that make them possible are back: `RawAudioVideoFixtureFile.Create(frameCount, videoFramesAfterTheSound)` and a stand-in device that counts *played* frames (`PlaysWhatItIsGiven` / `DrainingDeviceClock`), which is the model the suite never had.

*Recorded:* §5 and §14 of `PlaybackPipeline.md`.

**S11o — The picture that stops is judged by what is on the screen, and two freezes the verdict was hiding.** *Done, from the user's review of S11n: "IsVideoStarved should never use the `_lastDecodedFrameTicks` as reference, why doesn't render thread maintain its own lastPresentedTicks? I would remove the entire `_lastDecodedFrameTicks` in the first place … Remove the watchdog."*

*The verdict.* The picture's starvation mark is now the timestamp of the last frame the render thread **showed** (`_pictureTicks`), not the newest one the decoder produced. The decoder's tail is wrong twice: it advances while the renderer is blocked on a GPU fence, so it reports a picture that nobody can see; and it survives the frame being shown, so a decoder that goes quiet is reported as "not behind" for as long as its last timestamp is ahead of the clock. `_lastDecodedFrameTicks` and every write to it are gone.

One rule had to be added to that, and the suite is why: **a frame waiting in the queue is never starvation.** Judged by the shown frame alone, a frame that is *due later* holds the clock that is supposed to reach it, which is a permanent freeze rather than a delay — measured as ten tests timing out at eight seconds. So the verdict is: nothing shown and nothing queued → starving; a frame queued → never starving (it is either due now or the clock must run to make it due); nothing queued and the shown frame more than 200 ms behind → starving, until 100 ms.

*The freeze the verdict then exposed, and it was not the verdict's fault.* With the clock really held for a late picture, three video tests stopped presenting frames for the rest of their budget — 57 packets queued, an empty frame queue, and neither thread moving. `VideoRenderCore` sleeps on its node when the frame queue is full, and the **only** thing that can wake it is the picture taking a frame; the presentation path took frames without calling `TryWakeVideoDecode`, which only the stale-entry and end-of-stream branches did. So the decoder filled the queue once, drained it, and slept for good — while the reader, correctly "buffered", slept too. The tail-based verdict had been hiding it: the decoder's last timestamp stayed ahead of the clock, the clock kept running, so playback *looked* alive with a picture that had stopped (which is the same shape as the reported "the file never ends"). One line after the successful dequeue fixes it, and the trace after it is 40 s of steady 30 fps at a 20 ms lag with nothing dropped.

*Three more things the removal of the old clock left behind.*

- **The anchor is the first frame a position *showed*.** A still offered while paused is the first frame of the new position, and playback must resume at it — not at the seek's request. A silent file that opened while paused, showed its still, and was then played could never anchor at all (the still does not anchor, and by the time playback started the still had already been taken), so `_anchoredEpoch` stayed `-1`, `StartOrPauseClock` refused to start, and the clock stood at zero for ever. The epoch's first shown frame is remembered and the clock anchored to it when playback starts.
- **A held clock is not the device's to move.** The correction to the clock now runs only while the clock is running; otherwise the few milliseconds the device played between the verdict and the thread that stops it leaked into a held position (measured: 16 ms of drift in a clock that is supposed to be still).
- **A file with no picture has nothing to starve.** The render thread runs for every file, so an audio-only file reported the picture as starving from its first iteration and the clock never started. `IsVideoStarved` answers `false` with no video decoder.

*The watchdog, removed.* `CheckStalledAudioClock`, `_deviceStalled` and `WarningAudioDeviceStalled` are gone, and so is `WaitsForADeviceThatIsNotMovingAndThenCarriesOn`. A device that stops is a wait and nothing more: the pipeline cannot tell a device that is late from one that is broken — the same frozen position, the same call — so the line it raised was a guess with a threshold behind it. The cost is stated instead: a stalled device now freezes the picture silently, and the host can tell it apart only by `Position` standing still with the state `Playing` and `StarvedFor` null (§9, §14 of `PlaybackPipeline.md`).

*Verified:* 179 tests, 0 failed, three consecutive runs, x64 and ARM64 clean; every GPU and endpoint test ran for real. `WaitForPresentedFrames` waits five seconds rather than two (the video fixtures run at a frame every two seconds, so a test asking for the second frame needs more than the interval and nothing else), and the tests that read the device's run state wait for it rather than asserting it, because the device is now the audio thread's to start and stop.

*Recorded:* §2.3, §4.3, §4.5, §5, §8, §9, §10 (I9, I13, I20) and §14 of `PlaybackPipeline.md`.

**S11p — VT and AT: a stage that has nothing left here is not a stage that is behind.** *Done, from the plan the user settled on: "Reader should maintain the latest timestamp for video and audio stream (denoted as VT, AT). When this condition is met a video render thread cannot be considered as starving: VT < clockPosition && max(VT, AT) >= clockPosition."*

*What is published.* The reader writes, for the position it is reading, the newest timestamp it has taken in for each stream (`_readVideoTicks`, `_readAudioTicks`) under a third field that stamps the position they belong to (`_readEpoch`). Ticks are written as a maximum rather than the last value seen — a stream's packets are not guaranteed to arrive in display order — and the position is stamped *after* the two are emptied, so a render thread that sees the new position can only ever see "nothing yet", never a number from where playback came from. The stamp is published by the reader, not by the seek, because the reader is the thread that actually turns "at this position" into "past this packet".

*How it is used.* `IsStreamSpent(epoch, position, video)` answers "the file has nothing more of this stream here": its own mark is behind the clock while the other stream's has been read up to or past it. Both verdicts consult it — the picture's (`starved = … && !IsStreamSpent(…, video: true)`, before the "nothing shown for this position" term, because a spent stream will never show one) and the sound's (`starved = !deviceHasAudio && !ending && !IsStreamSpent(…, video: false)`). "Spent" is not a flag of its own: it is a fact about what has been read, so it is asked for rather than stored.

*The measurement, which is the whole reason this took two attempts.* The first attempt's tests — a file whose picture ends two seconds before its sound, and the mirror — **passed with the rule switched off**. Every finite file this project can mux reports its end when it is read out, and the reader's read-ahead reaches that end long before playback needs it, so `ending`/`IsEnding` already release the hold in milliseconds: the shape those tests build is not the shape the rule is for. To reach it, a source has to go quiet *without* ending. A wrapper over the FFmpeg source cannot be used for that (the decoder factories require `FFmpegSource` exactly — measured: "The video stream does not come from an FFmpeg source"), so `SyntheticMedia.cs` was written: a scripted source plus decoders that turn one packet into one frame or one block of samples, in their own test-only file.

*Falsified, both ways.* With the rule in place: `KeepsTheSoundPlayingWhenThePictureStopsArriving` — a live source whose video stops at 1 s while the sound runs to 3 and no end of file is ever reported — plays through to the sound's end with nothing starving. With `IsStreamSpent` forced to `false`: the same test fails at its 6 s budget with the clock frozen at 1.2 s, which is the picture holding the sound hostage. And the rule is not "never hold": `HoldsTheClockWhenNothingIsLeftOfEitherStream` (both streams stop, nothing read past the clock) still holds, before and after.

*What this does not buy, said out loud.* Plan item 3 — "when the situation is no data, the end notification is not required for the corresponding stream to transit to End state" — has no case to fix: a spent stream in a finite file still hands its end-of-file entry over through its queue, and a live source has no end of file for the pipeline to reach, so there is no ending to earn. No flag was added for it.

*Verified:* 182 tests, 0 failed, twice consecutively, x64 and ARM64 clean.

*Recorded:* §2.2 and §8 of `PlaybackPipeline.md`.

**S11g — The claim of the clock is not a wake.** *Done, from a review that would not accept the previous
answer: "Why `AnchorClock` has to call `_videoRenderNode.Wake()`? … what's the consequence of removing it?" —
then: "Why audio thread starts at T not K? The reader reads from K and provided K to audio threads."*

*My first two answers were wrong, and the user was right both times.* I claimed the wake mattered because the
sound anchors at the seek target while the picture's newest frame is a keyframe behind. It does not: `Seek` is
`av_seek_frame(…, -1, ts, AVSEEK_FLAG_BACKWARD)`, nothing filters what is read, and `DecodeAudio` enqueues the
first buffer after the seek with **its own** timestamp — so the sound anchors on the keyframe the demuxer
landed on, which is exactly where the picture's newest decoded frame is (§5 says so in as many words). At the
claim, `behind ≈ 0`: the verdict cannot flip. I then offered a "pre-roll still" path that fails for the same
reason, and a "50 ms promptness" figure that was arithmetically real but attached to a transition that cannot
happen.

*What the verdict can be.* Walking `IsVideoStarved` at the claim instant: nothing shown and nothing waiting →
`true` before *and* after, because the readiness term precedes the gate; something shown or waiting → the tail
is the same position's keyframe, so `behind` is a few milliseconds at most, below either threshold; `tail < 0`
→ `false` either way. The state that turns a picture starved later is the clock advancing while the decoder is
quiet, which is a time-shaped fact the cadence owns (§8). And the wake could only ever land on a bounded wait:
the anchor is claimed only while playing, so the paused infinite wait is never in play.

*What changed.* The wake is deleted, so the claim publishes a fact with no publisher-to-sleeper edge — the one
fact in the table like that, and deliberately so. §2.3's row is gone, §8's sentence now says *why* the claim
needs none, and Appendix A item 35 asks the question that would have caught this earlier: **can the fact's
transition change the answer at the moment it happens?** A wake that cannot is worse than none, because the
table then claims a dependency the code does not have — and this one misled a reviewer and me in the same
conversation.

*Verified:* 169 tests, 0 failed with the wake removed (and with it in place, before), x64 clean. There is
nothing to falsify: the change is the removal of an edge with no reachable effect, and the argument for that
is the case analysis above, which the tests can only fail to contradict.

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
  and both are already on the list: hardware decode (**S13**) removes most of the decode cost, and a cheaper
  frame upload would remove much of the other. Neither is a substitute for the priority policy now in place,
  which is what stops a slow machine from taking it out on the sound.
- **S13a.** Hardware video decode whose frames come back in system memory — the first half of the answer to
  the question above, and done since this note was written.
- **Watch for.** `PlaybackEngineAdapter` refuses `SetPlaybackRate` rather than pretending; if speed control
  is wanted, the pipeline needs rate support (the clock can already be retimed, the audio device cannot).
- **The Lab stays** as an internal dev tool: it is the only place that shows decode/render cost, drops,
  underruns and picture lag side by side.

### Phase 3 — Flexibility (the five scenarios v1 keeps)

Phase 2 consumed S1–S11p (the orchestration rework, then the clock and starvation work), so Phase 3 continues at
**S12** rather than restarting at S10. Two rows moved since this table was first written: media information is
done, and track selection — the one scenario with no fallout on the others — is deferred to v2.

| Step | Goal | Verify | Review size |
|---|---|---|---|
| **S12** ✅ | Media info query surfaced to the port and the player | Playback information pane shows container, per-stream codec, bitrate, sample rate, FPS, and the decoder's hardware/software answer | small |
| **S13** ✅ | Hardware video decode + HW/SW switching | Toggle HW/SW at runtime; capability-driven, with software fallback | large (highest risk) — eight rounds, see below |
| **S14** ✅ | Renderer switching | Swap video renderer / audio device at runtime | medium — four rounds, see below |
| **S14d** ✅ | Component selection rework (added during S14; not in the original plan) | The resolver as the only source of what to use, and `Reconcile` as the only way to change what is playing | medium — five rounds, see below |
| **S14e** ✅ | Failure and resynchronisation: a device failure is a warning and a fallback rather than the end of playback, a rebuilt chain restarts at the position playback is at, and the read-ahead stops depending on a renderer telling the truth. Supersedes parts of S14d. | Suite + eight new tests | medium — see below |
| **S15** | Audio filter chain + volume filter | Volume and mute applied in the chain by the host, so no renderer has to provide them — and `Volume` is gone from the pipeline | medium |
| **S16** ✅ | Isolation hardening | Architecture test fails on an FFmpeg type crossing the boundary | small |

Sequencing notes:

- **S13 is the highest technical risk, and it is done.** The 4K60 AV1 file that motivated this engine was decoded in
  software at 12.9 ms a frame and could not hold its frame rate; it now decodes on the GPU with no copy per frame.
- **S12–S16 are independent of each other** and can be reordered; each adds a capability and a small amount of
  UI, and none changes the port shape.
- **S15 is audio-only on purpose.** The chain is proven where it pays for itself immediately: volume and mute
  move out of `IAudioRenderer` and into a filter, so every renderer — the null one included — gets working level
  control for free, and the device's own stream volume stops being an engine dependency. The video half of
  scenario 5 needs format negotiation and GPU/CPU frame ownership from a filter; it is v2, and it is described in
  [MediaEngineV2Plan.md](./MediaEngineV2Plan.md).

**S12 — Media information reaches the port and the player.** *Done. The plan's Phase 3 row of that name, reshaped
during review from a dialog into a pane.*

*What was built.* `PlaybackPipeline` reports the chosen decoders' own answers (`IsVideoHardwareAccelerated`,
`IsAudioHardwareAccelerated`) beside the demuxer's `MediaInfo`. The app port gained `IPlaybackInfoProvider`,
implemented by a `PlaybackInfoHandle` that holds the pipeline and nothing else — attached when a file opens, and
detached *before* the pipeline is disposed, so a pane that keeps it answers "nothing" instead of reaching a
disposed engine. A new sidebar pane ("Playback information", route `side_pane_playback_info`) renders it as a
two-column table refreshed every 500 ms, with a Copy button that writes `key: value` lines sorted by key.

*Reviewed into shape rather than written once.* The playback pane and the media-info pane now share one
`MediaChanged` event (a second one was added and removed); the provider is a member of `IMediaEngine` rather than a
separate capability interface; the `PlaybackInfo` record was folded into that interface; the row names are
hard-coded English for now; and the table is a single `Grid`, so its name column is as wide as the widest name in
it rather than a hand-picked `MinWidth`.

*Deliberately not done.* Nothing is localized beyond English. A file with several streams of one kind would
produce duplicate keys in the copied text, because a key carries no stream index — moot until track selection (v2)
makes several streams reachable. `IsVideoHardwareAccelerated` reads `false` for everything until S13, which is
where the answer starts to mean something.

**S13 — Hardware video decode.** *Complete; what remains is a surface, and it is deferred with the others.*

| Round | What it adds |
|---|---|
| **S13a** ✅ | Hardware decoding whose frames come back in system memory. `FFmpegHardware` owns the D3D11 device, the `get_format` callback and the one hand-written field offset the ABI needs; `FFmpegVideoDecoder` opens with a device, copies each hardware frame out with `av_hwframe_transfer_data`, and reports `IsHardwareAccelerated` from the frame it actually got rather than from what it asked for. `DecoderOptions.PreferHardware` is the request, and every failure on the way falls back to the same decoder opened without a device. |
| **S13b-1** ✅ | Frames that never leave the GPU: a hardware frame is handed out as `FFmpegGpuVideoFrameBuffer`, an `IGpuVideoBuffer` over the texture libavcodec wrote, with no copy at all. |
| **S13b-2** ✅ | Decoding on a device the caller owns. `DecoderOptions.GpuDevice` carries it; the device context is allocated around it with `av_hwdevice_ctx_alloc` and the device receives a reference of its own, because libavcodec releases whatever it is given. No sharing, no handle, no copy. |
| **S13b-3a** ✅ | One device per pipeline, and a lock to match: the renderer offers `IGpuDevice`, `PrepareVideo` hands it to the decoder it creates next, and both take the same recursive lock — libavcodec through `AVD3D11VADeviceContext.lock/unlock`, the renderer around a whole draw. |
| **S13b-3b** ✅ | The renderer draws the texture where it lies: `R8_UNORM` and `R8G8_UNORM` views are cast out of the decoded texture over the frame's array slice, and an array-sampling variant of the semi-planar shader reads both planes. No copy, no CPU traffic. `gpuFrames` follows the device: a decoder with the pipeline's GPU hands out textures, one with a device of its own still copies back. `IGpuVideoBuffer.Retain()` exists because a redraw after a surface change outlives the frame, and holding the reference is what stops libavcodec from reusing the slice. |
| **S13c** ✅ | Selection and fallback, both halves. A decoder that opened and then produced nothing is replaced by the next candidate — which is what makes libavcodec's hardware-only AV1 decoder usable, so it is now tried first with libdav1d behind it. And the candidates come from the registry: the pipeline takes a `ComponentResolver` and walks `ResolveVideoDecoders(stream, options)`, so what can serve a stream and in what order is decided where components are registered, not inside the pipeline. |
| **S13d-1** ✅ | The replacement mechanism. `SetVideoDecoderHardwareAcceleration(bool)` records a *pending* preference and raises the epoch, which is already the engine's "everything you were doing is void" signal: the decode thread flushes, throws away what the old decoder produced and builds a new decoder from the registry, so playback never stops and no other thread ever touches a decoder that is being swapped. |
| **S13d-2a** ✅ | What is running: the pipeline records the *component* the decoder came from and reports it as `VideoDecoderName`, so `PlaybackInfo` can say `Hardware (ffmpeg-video)` instead of only which kind it is. The component's name rather than libavcodec's own (`h264`, `libdav1d`) because that is what a settings surface picks and what a replacement changes; the library's name is a diagnostic for a surface that asks for one. The Lab has the toggle that calls `SetVideoDecoderHardwareAcceleration`, and its status line names the decoder beside its decode cost. |
| **S13d-2b** ✅ | The choice itself, engine-side. `VideoDecoderSelection` is the choice — a kind and, optionally, one component by name — and it reaches the resolver as `ComponentSelectionOptions`, so a name is honoured by selection rather than by the decoders. The name is the stronger wish: naming a software decoder while preferring hardware runs that decoder, because a caller who names one has said something more specific than "prefer hardware". The pipeline keeps the last choice, so a file opened afterwards is decoded the same way, and `SetVideoDecoderHardwareAcceleration` is now a thin caller of `SetVideoDecoder` that sets the kind alone. |
| **S13e** ✅ | One place says what a hardware frame may be. `IGpuDevice` kept the general question (*can you draw this layout?*) and the API's own vocabulary — the device handle, the lock that guards it — moved to `ID3D11GpuDevice`, so a renderer of another API declares none of it and cannot be mistaken for one this backend decodes on; the FFmpeg side gained `IFFmpegHardwareBackend` + `FFmpegHardwareBackends` (was one static `FFmpegHardware`), and `FFmpegVideoDecoder` no longer names an API at all. The hardware texture layouts the renderer can sample are one table read twice — by the pre-check before a texture exists, by the draw from the texture's own `DXGI_FORMAT` — because two copies could disagree in the direction that accepts a frame and then refuses every one that arrives. A stream whose hardware layout nothing can draw is now refused at open, which is the first point the resolver's software fallback can actually fire. |

*Verified for a).* `DecodesOnTheGpuWithTheSamePictureTheCpuProduces` decodes a generated H.264 stream twice, once
asking for hardware and once for software, and compares the luma: the GPU has to produce the picture the CPU
produced. On the development machine it engages and matches within one. `FallsBackToSoftwareWhenTheCodecHasNoHardwareDecoder`
pins the other half — a bitmap, which no GPU decodes, still decodes when hardware is asked for.

*Verified for b-1 and b-2).* `KeepsTheFrameOnTheDeviceItWasGiven` creates a device the way the renderer does,
hands it to the decoder, decodes one frame and checks that the decoder reports exactly that device, that the
frame is a texture, and that copying its slice back through **that** device yields the software decoder's
picture — a copy through any other device would not be allowed at all.

*Verified for b-3a).* Three tests, two of them falsified before being trusted: `GivesTheDecoderTheDeviceItsRendererDrawsWith`
pins the wiring, `WaitsForTheDeviceLockBeforeDrawing` blocks a draw from another thread by holding the renderer's
lock, and `DecodingWaitsForTheDeviceLock` does the same to a decode — with the lock callbacks left unwritten it
completes in 100 ms instead of waiting.

*Verified for b-3b).* `DrawsAHardwareFrameWithoutCopyingItBack` decodes a hardware frame, checks it has no planes
to read, draws it and compares the result with the same picture drawn through the plane-upload path: identical
within one. Swapping the two cast views — luma as `R8G8`, chroma as `R8` — makes it fail, so the comparison really
does depend on the planes being addressed correctly. 189 tests, x64 and ARM64 clean.

*Verified for S13d-1.* `ReplacesTheVideoDecoderWithoutLeavingPlayback` plays a generated stream through a recording
factory, asks for the other decoder, and waits for the second request while the picture keeps arriving — with the
rebuild disabled it never comes. `KeepsTheRunningDecoderWhenThePreferenceIsUnchanged` asks for what is already
running and requires that no second decoder is created, and `KeepsTheRunningDecoderWhenTheReplacementFails` makes the
factory refuse the second request and requires the stream to carry on, one warning saying why, and playback still
`Playing`. 197 tests, x64 and ARM64 clean.

*Verified for S13d-2a.* `ReportsTheDecoderThatIsRunning` registers a decoder that serves hardware requests only
and one that serves software requests only, opens a file (hardware is the default, so the first one runs), switches,
and waits for the report to move to the second while the picture keeps coming. Leaving the name out of the
replacement makes it fail on the first assertion. `KeepsTheRunningDecoderWhenTheReplacementFails` also requires the
name to stay put when the replacement could not be made, so "what is running" never claims a decoder that never
started. 195 tests plus 3 environment-dependent skips, x64 and ARM64 clean.

*A failure a real file found, and the fix.* A 1080p60 AV1 file played for a moment and then stopped with
`Unable to submit a packet to the decoder: Cannot allocate memory`. Nothing was wrong with the stream or the decoder:
libavcodec sizes a D3D11 pool at twelve surfaces and **fails** rather than waits when they are all in use, and the
pipeline's own frame window is eight frames plus the renderer's retained one — so the decoder ran out of surfaces
while the queue was doing exactly what it was designed to do. FFmpeg says so on its log, which is worth reading
before guessing: `Consider setting extra_hw_frames to a larger value (currently set to -1, giving a pool size of 12)`.
`DecoderOptions.BufferedFrames` is the fix and the shape of it: the caller states how many decoded frames it may be
holding, and the decoder sizes its pool for them (`extra_hw_frames`), because a pool is the decoder's to size and the
number is the caller's to know. The pipeline passes the queue's capacity plus the few frames that are in flight;
`LeavesRoomInThePoolForTheFramesTheCallerHolds` holds twelve of them and requires four more decodes to succeed,
which fails with the same "cannot allocate memory" when the option is not passed. 199 tests, x64 and ARM64 clean.

*The other thing the same file showed: a green line along the bottom.* A hardware decoder decodes into a texture it
sized for itself, and the picture is smaller than that texture: probed on this machine, a 1920x1080 picture lives in
1920x**1088** for H.264 and 1920x**1152** for AV1. The rows below the picture are never written, and sampling the whole
texture across the picture's viewport draws them along the bottom, where "nothing" converts to green. The vertex shader
now takes a texture-coordinate scale from a constant buffer — the picture's size over the texture's — so a padded
texture is sampled over the part that holds the picture. `DrawsAPaddedHardwareTextureWithoutShowingThePadding` draws a
1080p hardware frame and the same picture decoded on the CPU into the same target and requires them to match: at that
size the 64x64 test's comparison passes either way, because a 64x64 picture needs no padding, and returning the scale
to one makes this one fail on the extra rows. Nothing about it is visible in the pipeline's counters: the frame count,
the render cost and the drops are all unchanged, which is why the picture itself has to be compared.

*What is still outstanding.* Only the surface, and it is now deliberately later: no app code is touched until S14 is
finished, so the player's settings panel waits. What the engine offers a surface today is `SetVideoDecoder` (a kind,
a component by name, or both) and `VideoDecoderName` (what is running) — both exercised from tests and the Lab's
hardware checkbox, and neither reachable from the player page. A picker that lists the decoders the registry offers
for the open stream is the small piece that would make the name path usable in the Lab, and it is not needed to
prove anything the tests do not already prove. And the measurement of the 4K60 AV1 file can be made now that it
decodes on the GPU.

*Verified for S13d-2b.* `LetsANamedDecoderWinOverThePreferredKind` registers the real decoder twice under the names
`hardware-video` and `software-video`, opens a file (the kind decides, so `hardware-video` runs), then asks for
`software-video` while still preferring hardware: the named one has to be the one running, which is the resolver's
ordering and not a decoder's own preference. Dropping the name from the `ComponentSelectionOptions` before it reaches
the resolver makes it fail on that assertion. The same test then opens the file again and requires the chosen decoder
to be used at open, which is the "the choice outlives the file" claim — a choice the pipeline keeps rather than one it
is told once per file.

*Verified for S13c.* The first half: `FallsBackToTheNextDecoderWhenTheFirstProducesNothing` opens the AV1 stream
with `av1` first and no device — the decoder opens and then fails on the first packet, as libavcodec's AV1 decoder
does without a GPU — and the stream still decodes, with the decoder reporting `libdav1d`. Disabling the fallback
makes it fail. `LeavesADecoderThatProducedFramesAlone` pins the other side: a decoder that has produced a frame is
never replaced. `DecodesAv1OnTheGpuWhenTheMachineHasTheDecoderForIt` reports whether this machine decodes AV1 on the
GPU — it does here, and every AV1 test in the suite now runs that way, seek and clock ones included. The second
half: `LetsTheNextDecoderServeAFileWhenTheFirstOneRefuses` registers a factory that refuses and one that works, and
the file is opened by the second; `ReportsWhyNoDecoderCouldServeTheFile` keeps the refusing factory's own reason as
the open's failure. 194 tests, x64 and ARM64 clean.

*Two things a) learned.* FFmpeg's native AV1 decoder has no software path: given a device but no AV1 hardware
support it fails at the first packet, and without a device it fails the same way. That is why its candidate order
had to stay software-first until a decoder that produces nothing could be replaced — which S13c's first half now
does, so the order flipped. And hardware decoding pins the decoder to one thread, which is why
`DoesNotLeaveTheDecoderOnOneThread` now asks for the software path explicitly.

*One thing b-1 learned.* The layout of the pixels inside a hardware frame is neither the frame's own format (that
is the hardware one) nor `AVCodecContext.sw_pix_fmt` (that is the software decoder's `yuv420p`): it is
`AVHWFramesContext.sw_format`, which says `nv12`. Reading the wrong one would have made every renderer pick the
wrong shader.

*Two things b-2 decided.* The device has to be one per **pipeline**, not shared between pipelines: the renderer
owns it, and a decoder borrows it (a reference of its own, handed to libavcodec). And sharing a texture across two
devices was dropped rather than implemented — a keyed mutex only orders anything if the producer takes it, which
libavcodec will not do, so that route would have needed a fence per frame on top of the pool interop.

*Two things d-1 learned.* A test that decodes one frame to prove the decoder takes the device lock cannot tell a
decoder that takes it from one that does not: libavcodec takes the lock around the calls that touch the device
context, and a single frame may need none of them, so the test intermittently passed its own 250 ms "must block"
check by finishing too fast to matter. It now decodes the rest of the stream under the held lock. Decoding it also
showed the other half of hardware frames: a frame that is alive pins a slice of the decoder's pool, so the whole
file cannot be collected into a list the way a software decode can — the helper that only counts frames disposes each
one, and a list of hardware frames runs the pool dry and fails with `AVERROR_INVALIDDATA`.

And one regression the refactor caused, which the S13b-3a test caught: the new `CreateVideoDecoder` helper built its
`DecoderOptions` from `_videoRenderer`, but it is called *before* the newly created renderer is assigned to that
field, so `GpuDevice` came out null and every decoder silently fell back to decoding into memory. The device is now
a parameter of the helper, taken from the renderer at hand rather than from the field that will hold it later.

*One thing b-3a learned.* A draw has to take the lock around the **whole** sequence, not per call, and the
recursive kind is required: libavcodec states that in the header, and it is what lets a caller that already holds
the lock call in again.

*What b-3b starts from.* It was planned around a video processor, because Direct3D appeared to offer no shader
access to a semi-planar plane. A probe says otherwise: `CreateShaderResourceView` refuses `DXGI_FORMAT_NV12` on
such a texture but **accepts** `R8_UNORM` and `R8G8_UNORM` views of it, which are exactly the planes a shader
needs. What the offscreen comparison has to settle is whether those cast views address the planes correctly; the
video processor stays the fallback if they do not.

*Numbers worth writing down.* `get_format` is at byte 192 of `AVCodecContext`, `hw_frames_ctx` at 552 and
`hw_device_ctx` at 560 in the pinned headers, taken with a compiler against them rather than counted by hand;
the first is reached through the declared part of the struct, the other two by offset, and `FFmpegRuntime`'s
major-version check is what keeps them honest.

**S14 — Renderer switching.** *Done: both halves of the mechanism, and the device picker.*

| Round | What it adds |
|---|---|
| **S14a** ✅ | The property the swap stands on: a decoder that decodes on a renderer's device keeps that device alive by itself. It holds a reference of its own, and libavcodec holds one to the immediate context it derives from the device, so disposing the renderer is one release among several — which is what makes the swap a piece of bookkeeping rather than a two-thread handshake. |
| **S14b** ✅ | `SetVideoRenderer(factory)`: the new renderer is built and configured on the playback queue, so one that cannot be made changes nothing; it takes over on the render thread, which is the only thread that calls into a renderer, so the one it replaces is disposed where no draw can be in flight; and the video chain is then rebuilt around the new device, because every frame decoded so far belongs to the old one. The factory is remembered, so the next file opens with it, and `VideoRendererName` says what is drawing. The Lab has a *New renderer* button, which is the only way to exercise the real path — a test has no panel to present into. |
| **S14c-1** ✅ | The audio half's mechanism: `SetAudioRenderer(factory)`. The new device is built and configured on the playback queue — so one that cannot be opened changes nothing — and takes over on the audio thread, the only thread that hands it audio, so the device being replaced is flushed and released where no hand-off can be in flight. The host's volume and mute are applied to the new device because they belong to the pipeline rather than to a device. |
| **S14c-2** ✅ | The picker. `WasapiAudioEndpoints.TryGetRenderEndpoints()` lists the active output devices (id, friendly name from the property store, and which one is the default), `WasapiAudioRendererFactory.For(endpoint)` opens that device rather than the default one, and the Lab has an output-device list that swaps on selection. A device whose property store refuses gets its id as the name rather than no entry, so a list is never missing a device it can play through. |

*Verified for S14a.* `KeepsDecodingAfterTheRendererItWasBuiltOnIsGone` builds a hardware decoder on a renderer's
device, decodes a frame, disposes the renderer, and requires another frame — on the GPU — from the same decoder.
Without the decoder's own reference to the device the test host **crashes** rather than fails, which is what a
use-after-free looks like from a test and the reason the reference is not an optimisation.

*Verified for S14c-1.* `SwapsTheAudioRendererWithoutLeavingPlayback` plays a file through a device that never
takes the audio, swaps to a second one, and requires the new device to be handed sound while the state stays
`Playing`, the clock keeps moving, the replaced device is released, and the level the host set reaches the new
device. Making the audio thread ignore the pending device makes it fail on "the new device to be playing", so the
assertions really do depend on the swap happening where it says. `KeepsTheRunningAudioRendererWhenTheNewOneFails`
requires a device that cannot be opened to be the caller's failure to see, with the playing device untouched and
the clock still moving. 207 tests, x64 and ARM64 clean.

*Verified for S14c-2.* `ListsTheOutputDevicesThisMachineHas` reads the list and pins what a picker needs: every
entry has an id and a name, Windows has at most one default, and the default comes first when there is one. The
development machine reports no output device at all, so the test skips after proving the call itself succeeds —
which is still worth having, because a missing CsWin32 symbol or a wrong signature fails there rather than in the
Lab. The friendly names come from the device property store (`IPropertyStore` + `PKEY_Device_FriendlyName`), and a
device that refuses it is listed by its id instead of being dropped.

*What the picker is not.* It lists devices, not renderer implementations: `wasapi` is the only audio renderer there
is, so choosing a device is the only choice that exists today. The video side keeps its *New renderer* button
rather than a list of one entry — when a second video renderer exists, the same shape (a list fed by what the
registry offers) is what it should become.

*One thing the audio half needed that the video half did not.* The audio thread captures its renderer in a local
when it starts — it is the only thread that calls into it — so the swap had to happen there rather than through
the field alone: the pending device is taken at the top of that thread's loop, and the field is updated beside the
local so that `Volume`, `IsMuted` and the reports follow it. The video thread had no such local, which is why its
swap could be a helper.

*What the tests cannot say about an audio swap.* A stand-in device consumes everything instantly, so "the sound
keeps coming" is not observable there; the tests assert that the clock keeps moving and that the new device is
handed audio, and the audible result needs the Lab and two real endpoints.

*Verified for S14b.* `SwapsTheVideoRendererWithoutLeavingPlayback` plays, asks for a second renderer, and requires
the new one to draw while the state stays `Playing`, the position stays put and the replaced renderer is released;
reopening the file then has to use the factory that was chosen. `SwapsTheVideoRendererWhilePaused` requires the
swap to happen with the picture standing still, which it cannot without the wake — the render thread is asleep in
an unbounded wait there, and removing the wake makes the test fail on that assertion and nothing else.
`KeepsTheRunningRendererWhenTheNewOneFails` requires a factory that refuses to be a failure the caller sees, with
the running renderer still drawing. 205 tests, x64 and ARM64 clean.

*What the swap does not do.* It does not avoid the black frame between the new swap chain being attached (at
configure time) and the first picture being drawn (after the rebuild), and it does not keep the last picture
across the swap: `RefreshSurface` redraws from the renderer that has the frame, and the new one has none yet. Both
are visible as a single black frame on a real panel, and neither is wrong for a renderer that was just replaced.

**S14d — Component selection rework.** *Done. The resolver is the only source of what to use, and `Reconcile` is
the only way to change what is playing.*

| Round | What it adds |
|---|---|
| **S14d-1** ✅ | The registry becomes what a host *changes*, not just what it declares. *(Superseded by S14d-7: identity was a name then, and S14d-10: the snapshot became a clone.)* A component's identity is its name, unique within its role (`Register` refuses a second one rather than leaving "which is running" unanswerable); `Replace` keeps a component in its position; `Remove` and `Reorder` exist, and `Reorder` takes only the names to move to the front, so "prefer this one" does not require knowing everything else; `Version` moves on every real change, so a caller can skip work when nothing was touched — while putting the same instance back is not a change; a snapshot freezes everything at one instant, which is what a decision spanning two roles needs. |
| **S14d-2** ✅ | The video chain decided as one thing. `PlaybackPreferences` (one object, `PreferHardwareDecoding` today) lives on the resolver; `ResolveVideoChain` returns the renderers to try, the decoders to try, and whether any renderer can keep frames on the GPU. `Capabilities.IsHardwareAccelerated` now has a defined meaning for a video renderer — it draws on the GPU and lends its device — and `D3D11VideoRendererFactory` declares it, so "hardware decode is preferred but no renderer offers a device" is a fact a caller can see instead of a copy it discovers per frame. |
| **S14d-3** ✅ | The pipeline has no constructor arguments left: `SourceFactory` is a property (null means `Open` says so and closes what was playing, like every other failed open), `Components` is a `ComponentResolver` the pipeline never replaces, and `Synchronizer` defaults to the shipped policy. **Audio is now selected the way video is** — decoder and renderer both walk the registry's ordered candidates, which is what gives audio a selection at all; a factory a caller swapped in still comes first until the swaps become registrations. The resolver gained the host-facing registration API (`Register`, `Replace`, `Remove`, `MoveToFront`, `Preferences`) and can be built over a registry, over nothing, or over a copy of one. |
| **S14d-4** ✅ | `Reconcile()` replaces the four setters. It resolves both chains from one snapshot, compares them instance by instance with what it applied last time, and applies only what differs — building anything that can fail before publishing anything, so a renderer that cannot be created leaves the running one drawing and returns the failure. The renderers are then published and taken over by the threads that own them, which raise the epoch themselves: that ordering is what keeps a decoder from being rebuilt on a device that is about to be replaced. Audio decoding gets its first rebuild path, consumed by the reader thread, which is the only thread that decodes audio. The answer names what changed (`ComponentChange`, by component), so a host or a status line can say *what* rather than *that*. *(Superseded by S14d-6: the answer says only whether the change was applied.)* |
| **S14d-5** ✅ | The surface and the residue. The Lab's three switches (hardware decoding, new renderer, output device) all edit the resolver and reconcile through one helper, and the status line now says what a reconcile applied — `video.renderer fake-video → fake-video`, by name — beside the counters that say what is actually running. Two dead test helpers and the `VideoDecoderSelection` type are deleted. The rule is recorded in §3.8: the resolver is the only source of what to use, registration order is the only order, and `Reconcile` is the only way to change what is playing. |
| **S14d-6** (awaiting review) | Creation moves into the resolver, and the pipeline is reduced to one method. `TryCreateVideoRenderer`, `TryCreateAudioRenderer`, `TryCreateVideoDecoder` and `TryCreateAudioDecoder` walk the registrations and answer with a component, or with a failure that says why nothing could be made; `Clone()` gives a resolver over one picture of the registry carrying the same preferences. `PlaybackPipeline.BuildChains()` takes no arguments and returns nothing but a result: it reads the streams, the surface and what is running from fields, builds both chains, keeps a component whose **signature** matches the one already going to run (`IMediaDecoder.Signature`, `IVideoRenderer.Signature`, `IAudioRenderer.Signature` — what a component *is*, as opposed to the `Name` it shows people), and publishes the rest as the pending renderer or decoder that the owning thread takes over. `Open` and `Reconcile` are the same call, so neither can drift: an open is a reconcile where nothing is running yet. |
| **S14d-7** (awaiting review) | The registry loses its labels. `ExtensionPoint` is deleted: a role is the factory interface itself, so registrations are stored as `Map<Type, IComponentFactory>` and asked for by type (`GetComponents<IVideoDecoderFactory>()`) — the role is discovered from the instance once, at `Register`, and a factory implementing two roles or none is refused rather than guessed at. Names go with it: a component is addressed by the instance the host registered (`Replace(existing, replacement)`, `Remove(factory)`, `MoveToFront(params IComponentFactory[])`), because that is the only identity a registration needed once the diff became `Signature`. `RegisteredComponent`, `ComponentRegistryExtensions`, `ComponentResolverExtensions` and `ComponentCapability`-era leftovers are deleted. The decoder-name properties go with them: what the pipeline reports about decoding is `IsVideoHardwareAccelerated`/`IsAudioHardwareAccelerated` and what it reports about the sinks is each renderer's own `Name`, so no string the engine invented is published. |
| **S14d-8** (awaiting review) | Factories stop making components and start offering them. `IVideoDecoderFactory`, `IAudioDecoderFactory`, `IVideoRendererFactory` and `IAudioRendererFactory` answer with an `IComponentCreator<T>`: what it would make (`Signature`) and the one call that makes it (`Create`). The resolver asks the registrations what they would make, compares that with the signature the pipeline says is running, and creates only when the two differ — so a reconcile that changes nothing costs no graphics device, no decoder and no output stream, where the previous shape built everything to compare and threw it away. It makes the hardware fallback cheap for the same reason: after falling back, the software creator matches what is running, so the next reconcile builds nothing. An *offer* is a commitment: a factory that offers nothing is passed over and the next registration is asked, while a factory whose creator then fails to build something is the chosen one, and its failure is returned rather than hidden behind the next registration. *(Superseded in part by S14d-13: a creator that cannot build its component is passed over too.)* |
| **S14d-9** (awaiting review) | Null backends, so switching can be tested without a codec, a window or an output device. `NullVideoDecoder` and `NullAudioDecoder` produce something a real backend can still take — a fixed 16×16 black YUV420P frame per video packet at the packet's own timestamp, and a block of silence per audio packet in the stream's own format and length — so every stage downstream still queues, submits and runs dry exactly as it would, and a null decoder can be paired with a real renderer rather than only with a null one. `NullVideoRenderer` counts and drops frames; `NullAudioRenderer` takes every block, holds nothing and reports a stopwatch as its clock. Each has a factory (`NullVideoDecoderFactory`, …) that never refuses and takes a signature, so two can be registered at once and a switch between them is a real change. `RenderedFrameCount` and `SubmittedSampleCount` are what a test or the Lab watches to see the swap land. The Lab's second row became four pickers — video decoder, audio decoder, video renderer, audio renderer — in place of the hardware toggle, the new-renderer button and the device list. Each starts on **Auto**, an entry that registers everything its role can register in the order the picker lists it; the entries below it pin one component. Choosing an entry clears the role, registers what it holds and reconciles, so no record of what was registered is kept anywhere else: `IComponentRegistry.Clear<TFactory>()` empties a role, which is what let the page drop its mirror of the registry. |
| **S14d-10** (awaiting review) | `ComponentSnapshot` and `IComponentSource` are deleted. They existed only to be what a cloned resolver reads, and a clone can own a copy of the registry instead: `IComponentRegistry.Clone()` returns an independent registry — the same registrations in the same order, nothing shared — and `ComponentResolver.Clone()` wraps one with the same preferences. A resolver therefore has exactly one kind of source, its own registry, so a clone is registrable (onto its own copy) and there is no longer a "cannot register into this" case to explain. `CreateSnapshot` goes with them. |
| **S14d-11** (awaiting review) | The registry is reduced to what a host actually says. `Version`, `Replace` and `MoveToFront` are deleted: nothing in the pipeline, the app or the Lab called them, and each existed for a design that no longer does. `Version` was how a reconcile skipped work when nothing changed — it now asks each factory what it would make and compares signatures, so no counter is needed. `Replace` and `MoveToFront` were how a host changed one entry in a role's order — a host now says "this role is now this one" with `Clear<TFactory>()` followed by `Register`, and the order is the order it registers in. What remains is five members: `Register`, `Remove`, `Clear`, `GetComponents`, `Clone`. |
| **S14d-12** (awaiting review) | A renderer states what it draws with instead of being cast to it. `IVideoRenderer` gained `IGpuDevice? GpuDevice` — the device the renderer draws with, or null when it draws on the CPU — and the pipeline asks for it (`renderer?.GpuDevice`) instead of `(renderer as IGpuDevice)`. `D3D11VideoRenderer` answers with itself, since it owns the device and the lock that guards its context; the null and stand-in renderers answer null. The interface had been a hint to the compiler ("this renderer happens to be a device") where it should have been a fact the renderer states. |
| **S14d-13** (awaiting review) | The resolver hands out sequences instead of deciding one component. `CreateVideoRenderer`, `CreateAudioRenderer`, `CreateVideoDecoder` and `CreateAudioDecoder` become `ResolveVideoRenderers()`, `ResolveAudioRenderers()`, `ResolveVideoDecoders(...)` and `ResolveAudioDecoders(...)`, each returning `IEnumerable<IComponentCreator<T>>` — every registration that offered something, in registration order. Which one is worth taking is the caller's decision, so the "a creator that then fails is final" rule becomes "walk on to the next", because a machine that will not give what one factory promised may well give what the next one offers. `PlaybackPipeline` gains one helper that walks a sequence: a creator whose signature is the one running is the answer "nothing to do" and nothing is built, any other is asked to make its component, and a creator that cannot is passed over — the last failure is what the caller hears when the sequence runs out. Hardware decoding stays a requirement by being a second pass: `ResolveVideoDecoders` yields the registrations asked with it and then the same registrations asked without it, which reads as the fallback it is rather than as a loop inside a resolver. |
| **S14d-14** (awaiting review) | A component and its signature become one thing. `PendingComponent<T>` was already the pair — a component and the signature of the creator that made it — but only for what was waiting to be installed, so what was running had its component in one field and its signature in another (`_videoRenderer` and `_videoRendererSignature`, four of each). The type is renamed `ComponentSlot<TComponent>` and used for both: each role has a slot that is running and a slot that is pending, and taking a component over is moving the slot from one to the other. That deletes the four signature fields, makes the two writes of a takeover one (there is no longer a moment where a renderer is running and its signature is still the old one), and lets `Resolve` hand back exactly what the caller publishes — the pipeline has no place left where a component and its signature are separate variables. |
| **S14d-15** (awaiting review) | The picture changes without moving the file. A video decoder or renderer swap used to invalidate everything — `DiscardInFlightAndReseek` raised the global epoch, so the reader sought, the audio device was flushed and the clock was re-anchored — because the epoch was the one token every stage understood. But only the picture's two components are coupled, to one another, through the device the decoder decodes on; nothing else was ever paired with them. The pipeline gains `_videoChain`, a token no other stage reads: `StartVideoChain()` runs on the render thread when it swaps the renderer and in `BuildChains` when only a decoder changed, and it drops what the old pair produced and wakes the decode thread, which takes the newest decoder, starts at the next keyframe — a freshly opened decoder has no reference picture — and checks every batch against the chain it was decoded for, so a batch finished a moment too late is dropped rather than drawn by a renderer on another device. The reader, the audio queue, the device and the clock are untouched, which is what a swap now costs instead of a repositioned file. The renderer's device also became part of the decoder's identity: a renderer that is being replaced forces the decoder to be built again, because two hardware decoders' signatures say nothing about which device they decode on, and keeping the old one would hand the new renderer frames it cannot sample. A swap while paused still shows a picture, because a chain that has not drawn yet is owed the still. *(The chain token and the dropped media are superseded by S14d-17: a queued picture now says which decoder made it, and the renderer skips the ones that are not its own.)* |
| **S14d-16** (awaiting review) | The sound stops moving the file too. `DiscardInFlightAndReseek` is gone: with the picture on its own chain, the two remaining callers were both audio, and neither had a reason to raise the epoch. The reader installs a new audio decoder at the top of its loop instead of inside the epoch branch — what is still to come is the same bitstream and what was decoded is sound any device can take, so neither was ever paired with the decoder that made it. A new audio device re-states one thing only: where its clock stands relative to the pipeline's (`deviceClock.Position - _clock.Position`), because it has played nothing yet. The epoch is now advanced by exactly two things — a seek and a stop — which is what it always meant; a change to any component is a change of chain, not a change of position. |
| **S14d-17** (awaiting review) | An entry says what it is for, not just what it holds. `MediaItem<T>` becomes three types — `PacketItem`, `AudioItem` and `FrameItem` — and `MediaQueue<TItem>` takes the item type, so each queue carries exactly the tags its stage has to compare: a packet and a block of sound were made for a position (`SourceEpoch`), and a picture was also made by a particular decoder (`DecoderEpoch`). The token that used to do that job, `_videoChain`, is gone, and so is the drop it needed: nothing is purged, and a renderer that has just taken over walks past the pictures made for the device it replaced instead of the queue being emptied under it. `DecoderEpoch` advances only when a decoder and the renderer it decodes for are replaced together, because that is the only change that makes what is already decoded unusable — a decoder replaced on its own keeps its generation, so the frames already queued keep being drawn while the new decoder waits for a picture it can start from, which is a better picture than the stall it used to cost. `Clear()` is still there and still used by a seek: with the tags in place a seek would only pay for draining stale entries, so it is a latency choice rather than a correctness one. *(The bespoke queue itself is superseded by S14d-18.)* |
| **S14d-18** (awaiting review) | The queues are the framework's. `MediaItem` and `MediaQueue` are deleted: an entry is a `readonly record struct` declared where it is used (`PacketEntry`, `AudioEntry`, `FrameEntry` — the last carrying the decoder generation as well as the position), and the queue is `ConcurrentQueue<T>`. Two facts make a lock-free queue sufficient where a bespoke one was needed: each queue has exactly one consumer and nothing else removes an entry, so a look is what a take returns and a picture can be judged before it is owned; and a stale entry is skipped by the consumer rather than purged, so a seek and a decoder swap no longer empty anything. What the queue used to supply and no longer has: `Clear` (a seek only advances the position — the tags make the queued entries recognisable as old), close-plus-dispose (a stop joins the threads and then drains what nobody took), and a weight (the sound queue keeps a sample count alongside, which is what the reader paces itself by). `MediaQueueTests` goes with the class: what it pinned — order, the end-of-stream entry, release on close — is now the framework's behaviour or the pipeline's, and the pipeline tests cover it. |
| **S14d-19** (awaiting review) | A swap is one step, and a generation lives on the component that has it. `ComponentSlot<T>` gains a `Version`, and what used to be a pipeline field (`_decoderEpoch`) becomes two properties of the slots: a decoder and the renderer it was built for carry the same version, a decoder replaced on its own keeps the version the drawing renderer has, and a picture records the version of the decoder that made it (`FrameEntry.ComponentVersion`) so a renderer can skip the ones that are not its own. Nothing is compared against a counter in the pipeline any more — the question "is this frame mine?" is answered by the two components that were handed over together. Around that, `_componentLock`: `BuildChains` decides and publishes under it, and each of the four threads takes its half under it, so a reconcile can never read what is waiting while a swap is moving it, and a swap can never land between a reconcile's question and its answer. What the lock does not cover is releasing a replaced component — a codec that is closing or a device that is being flushed is disposed after the lock is let go, so a slow release cannot hold up a reconcile. *Amended in v2:* the four threads stopped taking the lock when adoption moved into `ComponentSlot` (`Publish`/`Update`), which left the publisher and the stop as its only entrants — both commands on the playback queue. The lock was then removed: it excluded the queue thread from itself, and its stated job (keeping a half-published generation away from the threads) was never something it could do for threads that do not enter it. |

*Verified for S14d-1.* Eleven tests. `RefusesTwoComponentsWithTheSameName` pins identity; `ReplacesAComponentInPlace`

pins that a replacement keeps its position (a device change is not a promotion); `ASnapshotDoesNotSeeLaterChanges`
pins the picture — falsified by handing out the live list, which makes it fail immediately; and
`VersionChangesOnlyWhenSomethingChanges` pins the no-op case that makes reconciling cheap, falsified by letting a
same-instance replace bump the version. `StaysConsistentWhileRegistrationsAndReadsRace` runs three writer threads
against a reader for 2,000 steps each and requires every read to be one complete state (no duplicate names) — the
lock's actual job, stated as an assertion.

*Verified for S14d-2.* `TheChainKeepsTheOrderOfBothHalvesAndReportsTheDevice` pins the pair and the honest answer
for a chain that cannot keep frames on the GPU (`SaysWhenNoRendererCanKeepFramesOnTheGpu`), and
`ReorderingPutsTheNamedDecoderFirst` pins that the order is the host's. *Superseded in part by S14d-4:* this round
also ordered the renderers that offer a device first, which was removed two rounds later — see "Order is the only
order" below. The ordering was mine, not a requirement, and the host's order is the one that survived.

*One thing the plan changed while implementing it.* `PreferredName` and `AllowFallback` were scheduled for deletion
here; they were not deleted until S14d-4. They are what the S13d-2b test proves
(`LetsANamedDecoderWinOverThePreferredKind`), and their replacement — order decided by the host's registrations —
did not exist until `Reconcile` landed. Removing them first would have taken away a capability a round before its
replacement could be proven.

*Verified for S14d-3.* `ReportsWhenNoSourceProviderWasSupplied` pins the omission as a failure with a message and
leaves nothing open. Three resolution tests prove the walks are real rather than incidental:
`LetsTheNextAudioDecoderServeTheFileWhenTheFirstOneRefuses`, `LetsTheNextAudioRendererServeTheFileWhenTheFirstOneRefuses`
and `LetsTheNextVideoRendererServeTheFileWhenTheFirstOneRefuses` each register a refusing factory ahead of a working
one and require the second to be the one that is used — emptying the audio renderer's candidate list makes the second
one fail on "the second device has to be the one that was configured", so the assertion depends on the registry walk
and not on the helper. 226 tests, three consecutive clean runs, x64 and ARM64 clean.

*Verified for S14d-4.* The seven switch tests were rewritten to the new shape and keep their claims:
`ReplacesTheVideoDecoderWithoutLeavingPlayback` changes the preference and reconciles, and requires the new
decoder to be created with it while the state and position hold; `ReportsTheDecoderThatIsRunning` requires the
report to move with the registry's order; `SwapsTheVideoRendererWithoutLeavingPlayback` now also requires the
**decoder to be rebuilt on the new renderer's device** — the assertion that failed twice while the code was wrong,
once because the plan was published after the renderer (the render thread could raise the epoch before the decoder
plan existed) and once because the test could not see the rebuild at all. `ReplacesTheAudioDecoderWithoutLeavingPlayback`
is the round's new capability: an audio decoder replaced while playing, on the reader thread, with playback never
pausing. `AppliesBothChainsInOneReconcile` replaces the video renderer and the audio device in one edit and
requires both to be applied and reported by one call. `KeepsTheRunningDecoderWhenThePreferenceIsUnchanged` requires
a reconcile that finds nothing to do to change nothing (and report nothing). `KeepsTheRunningDecoderWhenTheReplacementFails`
and `KeepsTheRunningRendererWhenTheNewOneFails` leave the registry holding only a factory that refuses, and require
a warning and a failure respectively — with the running component untouched. 223 tests, x64 and ARM64 clean.

*Two things this round changed that were not in the plan.*

**Order is the only order.** The plan said the diff would be conservative; it did not anticipate that three
orderings were still competing. `ComponentResolver` sorted decoders by hardware-versus-software kind and then by a
declared `Priority`, so a host could not put a software decoder first while preferring hardware — the exact thing
`Reorder` is for. Registration order is now the order, everywhere, and `ComponentSelectionOptions` (name,
preference, fallback) is deleted: the host orders candidates, and `PlaybackPreferences` only says how a decoder
should be *created*. `ComponentCapabilities.Priority` stays as advice for a host building a list, and the resolver
tests now pin that it is ignored.

**Not one epoch, but one per owning thread.** The earlier plan claimed a reconcile raises one epoch. It cannot,
and the reason is the reason the design works: the render thread raises it *after* swapping, so no decoder is
rebuilt on a device that is about to go, and the audio thread does the same for the device it owns. A reconcile
that changes both chains therefore produces two epoch changes, each from the thread that owns what changed. A
change with no renderer behind it is voided by the reconcile itself.

*One observation about the suite.* A run immediately after a heavy build produced 33 failures across timing-sensitive
audio tests, then three consecutive runs of the same binaries were clean (222 passed, 4 skipped, 50 s each, against
81 s for the failing run). The tests that failed are the ones that wait on the clock with a deadline, so a loaded
machine reads as a stall. Recorded here rather than dismissed: a real stall and a busy machine look the same to those
tests, and that is a limitation of the test design, not of the engine. A second, unrelated trap appeared in this
round: restoring a file from a backup gives it an **older** timestamp than the binary built from the falsified
version, so MSBuild skips it and the old binary runs — a falsification "restore" needs `/t:Rebuild`, not `/t:Build`.

*Verified for S14d-5.* No new tests: the switches it touches are exercised by the Lab, and the helper it introduces
is display only. The proof it matters is that everything else still passes — 223 tests, x64 and ARM64 clean — with
the old selection API gone from the tree entirely.

*Verified for S14d-11.* No new tests: the three members it deletes had no callers left in the engine, the app or the Lab, so what proves it is that everything else still passes. The registry tests it removed — `ReplacesAComponentInPlace`, `MovingToFrontPutsTheNamedComponentsFirstAndKeepsTheRestInOrder`, `VersionChangesOnlyWhenSomethingChanges` — are deleted rather than rewritten, because the premise each pinned (a counter, a slot replacement, a reordering) no longer exists; `ACloneDoesNotSeeLaterChanges` keeps its claim and now compares registrations instead of a version.
These tests pre-date the rework and are kept as the record of what each round pinned; the ones named here that no longer exist were deleted in the rounds noted above.

**S14e — Failure is a device's, and a rebuilt chain is a new start.** *The round that closes v1's engine work, and
the one that supersedes parts of S14d.*

*What it changed.*

1. **A failed device is survivable.** A renderer that cannot start, cannot take a block, or cannot be handed one is
   reported once and then spent at the clock's pace: playback, the position and the end of the file all continue,
   and the swap that replaces the device cures it. Before this, the only states a device failure could produce were
   "silent forever" or "Error, where a swap is refused" — the cure was unavailable exactly when it was needed.
   The same rule now covers the decoder's **end-of-stream drain**, which runs after the last packet has been read:
   a decoder that cannot give up its tail has lost the tail and nothing more.
2. **A device that takes sound and never plays it is throttled.** It holds nothing, so every consumer-reported
   bound says "no room needed" while the sound runs away from the clock. The evidence is the clock: when the last
   block taken is more than 3 s past the position, the sound is held back until the device catches up.
3. **The read-ahead no longer depends on a renderer telling the truth.** Each of the two queues paces the reader
   only while a chain fills it, so a target that can never be met (the sound with no decoder, the picture with no
   decoder) no longer lets the reader consume the whole file.
4. **BuildChains reports; the caller decides.** It returns what it replaced as a ChainChanges flags value and
   wakes nobody. Open ignores it. Reconcile treats any change as a new start: it advances the epoch with the
   position playback is at, which flushes the decoders, drops what was queued for the old chains and re-anchors the
   clock. **This supersedes S14d-15/16**, which are why FrameEntry carries a ComponentVersion: a swap used to
   leave the file where it was, and now deliberately does not. The version is therefore redundant with the epoch
   bump, and removing it is a candidate deletion rather than a behaviour change.
5. **A clock offset is chosen when a device appears**, not only when the position is armed — otherwise a device that
   arrives after the picture armed the position would never be synced to, and the pipeline would free-run on its own
   stopwatch for the rest of that position.

*Verified.* The full suite, repeatedly: 222 tests, all passing, x64 and ARM64 clean. Eight of those tests exist
because of this round (KeepsGoingWhenTheDeviceStopsTakingTheSound, BringsTheSoundBackWhenAFailedDeviceIsSwapped,
KeepsGoingWhenTheDeviceWillNotStart, KeepsGoingAfterASeekWhenTheDeviceHasFailed,
LetsGoOfAHandOffWhenTheDeviceDiesDuringIt, EndsWhenThePictureDeviceNeverComesBack,
BringsThePictureBackWhenAFailedDeviceIsSwapped, HoldsSoundBackFromADeviceThatDoesNotPlayIt), and three changed
meaning because the resync made their old premise false
(SwapsTheVideoRendererWithoutMovingPlayback, SwapsTheAudioRendererWithoutLeavingPlayback,
ReplacesTheAudioDecoderWithoutLeavingPlayback).
**S15 — Audio filter chain.** *The last engine capability in v1, and the one with a user-facing payoff from the
first filter.*

*Why audio first.* The chain is the architecture; a filter worth having on day one is what proves it. That filter
is **volume**: today every renderer carries `Volume`/`IsMuted`, `WasapiAudioRenderer` implements them by setting
the audio stream's volume through `IAudioStreamVolume`, and `NullAudioRenderer` implements them by doing nothing —
so level control is a property of *the device*, not of the pipeline, and a renderer with no device has no working
volume at all. (The stream volume is already per-player rather than the whole session's, so what is wrong here is
not two players sharing a slider — it is that the level belongs to a component that may not exist.) With a gain
filter in the chain:

- volume and mute become the chain's behaviour, identical on every renderer, and testable without an audio endpoint;
- the host's `SetVolume` becomes "set the gain on the filter", so a change applies to the next block — and the
  request lives where it belongs, with whoever is showing the slider;
- `IAudioRenderer.Volume`/`IsMuted` go away, which is one less thing a backend has to get right — and the reason
  the null backend had to pretend to have them;
- the player stops changing the audio stream's own volume, which the user can also set in the system mixer — a
  setting that is theirs to change, not the player's.

*Shape.* Filters are the host's own instances, not registry components: `PlaybackPipeline.AudioFilters` is an
`AudioFilterChain` the pipeline owns and the host edits, and that chain runs the filters — configure for the
stream, then every block — so the processing loop, the order and the dropping of a filter that cannot take part
all live in one place. Filters run **after the audio queue and before `Submit`** — the end of the chain — so a
change applies to the next block rather than the half second already queued. The level is not a pipeline concept
at all: the host keeps the gain filter it added and calls `SetLevel` on it, which is why the engine has no level to
store, no contract to look one up through, and no way to guess which filter stands for "the volume". A v1 audio
filter must keep the format it is given: the chain is a straight line, and a filter that cannot work with the
stream is left out of the chain with a warning rather than failing playback (the contract already written on
`IAudioFilter`).

*Plan (draft, for review).* Each decision names what it rejects, so a review can change the answer before the
next round builds on it.

1. **A filter is an instance the host hands over, not a component role.** The registry keeps deciding decoders and
   renderers; `AudioFilters` is the only way a filter gets into the chain — no factory and no resolver. Whoever
   wants to control an effect already holds it, so a factory would mean either building a second one or making the
   caller go and find the first again. Rule 8 of §3 survives untouched: the registry still answers for every
   component the engine chooses.
2. **The filters stay the host's.** The chain never disposes one — not when it is removed, not when the pipeline
   that owned the chain is gone — so a host can take a filter out, put it back, or dispose it itself, and the
   running chain only ever stops *using* what was removed rather than ending it while the audio thread might still
   be applying it. The consequence for implementers is one rule: a filter must tolerate a call that arrives after
   it has been disposed, because its host may outlive the pipeline.
3. **The chain configures, transforms, and keeps its own counsel.** It remembers the `AudioStreamInfo` it was
   given, so an edit is immediate: `Add` prepares the new filter for that stream there and then and it joins the
   running set — no reconcile, no restart — and `Remove` drops it from the running set without disposing it.
   `Apply(block)` runs a block through the running set, takes the block over (what it returns is the caller's to
   dispose: the same instance when nothing transformed it), and releases any intermediate a filter produced. A
   filter that refuses the stream, throws, or fails a block is left out or dropped and **written to the log by the
   chain itself** — nothing about a filter's health reaches the caller, because losing an effect is not a playback
   failure. The running set is replaced whole and read once per block, so a block never sees a half-edited chain.
4. **The pipeline has no level at all.** `PlaybackPipeline.Volume` and `IsMuted` are gone, and so is the line in
   `BuildChains` that copied a level onto the renderer. The host calls `SetLevel(double volume, bool muted)` on the
   `GainAudioFilter` it added: one call carrying both values, so the filter never applies half a change.
5. **Nothing about filters is a reconcile.** `BuildChains` hands the chain the stream as soon as there is one, and
   that is all: no `ChainChanges` flag, no restart, no epoch bump when a filter is added, removed or dropped — the
   sound changes at the next block instead. Being told the same stream twice does nothing, so a reconcile costs
   nothing.
6. **The contract gains exactly one threading rule.** `Apply` happens on the audio thread, and `Configure` happens
   either before anything plays or as the filter is added, so the two are never in flight for the same filter at
   once and a filter needs no lock for them. `SetLevel` may be called from any thread at any time — that is what
   "the host holds the filter" means — and does nothing on a filter that has been disposed. The gain filter meets
   it with one value written whole and no lock; the rule is stated on the class that owns the level rather than in
   a separate contract, because only that class defines it.
7. **A filter that cannot be used is left out, not fatal.** A configure failure is one log line naming the filter
   and a chain without it. A failure while applying is one log line, the filter out of the running set, and the
   block it was given carried on to the next filter and then submitted unchanged: the effect is lost, the sound is
   not. Format preservation is what makes that safe — nothing downstream is reconfigured. The instance stays the
   host's, so nothing is disposed behind its back.
8. **A device swap does not rebuild the filter chain**, so the level survives one: the added filters and the
   stream are unchanged and only the renderer is replaced. A new pipeline — a new file, an app restart — is a new
   filter instance starting at full level, and it is the host's job to set the level it wants on it (11).
9. **The gain filter is a backend, and it works in place.** `Backends/Gain` holds `GainAudioFilter`, public because
   a host constructs it. It handles every format the decoder can produce (`UInt8`, `Int16`, `Int24`, `Int32`,
   `Float32`, `Float64`), planar or interleaved, any channel count and rate, through `IPlanarAudioBuffer`. It reads
   the format from **each buffer** rather than from `Configure`, because a decoder reports what each frame actually
   is and the stream's declared format can differ. It scales in place, returns the same instance and allocates
   nothing. Full level with no mute is a pass-through that touches no bytes; mute is a gain of zero rather than a
   skipped block, so the clock sees the same stream either way. Its level is one value written whole — which is
   what lets it be set from the host's thread while the audio thread is mid-block — and a disposed instance keeps
   accepting it and doing nothing.
10. **Volume leaves the device and the pipeline.** `IAudioRenderer.Volume`/`IsMuted` go; Wasapi loses the
    `IAudioStreamVolume` service, `ApplyVolume` and its fields, and `WasapiVolumeControl` with its tests is deleted;
    `IAudioStreamVolume` leaves the three `NativeMethods` lists; and `NullAudioRenderer` loses the two fields that
    existed only to pretend. `PlaybackPipeline.Volume` and `IsMuted` go with them. One place is left where a level
    is written: the `GainAudioFilter` its host holds. Nothing is lost by it — the level no longer belongs to a
    component that may not exist, and a **device** swap cannot lose it because a device swap does not rebuild the
    filter chain.
11. **The adapter is the host.** `IMediaEngine.Volume`/`IsMuted`/`SetVolume`/`SetMuted` stay — that port is the
    player's, and the level is now its own. `PlaybackEngineAdapter` keeps `_volume`/`_muted` as the request and a
    `_gain` for the filter of the current pipeline: `Load` makes a `GainAudioFilter`, adds it to
    `pipeline.AudioFilters`, stores it in `_gain` and applies the request before `Open`; `SetVolume`/`SetMuted`
    store the value and, when `_gain` is there, set it at once. This is also a fix rather than only a move: the
    stored level is never applied to a freshly opened pipeline today, so the saved volume does not reach the first
    file. `Close` disposes the filter it made, because the engine is the host and the host owns it. `PlaybackInfoHandle`
    answers `Volume`/`IsMuted` from that request instead of from the pipeline, which no longer has one, so the
    playback information pane keeps its two rows.

| Round | What it adds | Verify |
|---|---|---|
| **S15a** ✅ | `GainAudioFilter`: every sample format, in place, a level that can be set from any thread, and the ownership/format/threading sentences on `IAudioFilter` | The filter's own tests; no factory, no contract of its own, nothing registered — the engine is otherwise untouched |
| **S15b** ✅ | `AudioFilterChain` and the get-only `AudioFilters` property; the stream it remembers; `Configure`/`Apply` inside the chain, the running set, the block hand-over, the drop-on-failure with the log line; the call from `BuildChains` and from the audio thread | Chain tests and three pipeline tests; with nothing added, behaviour is unchanged |
| **S15c** ✅ | The cutover: the adapter and the Lab add a `GainAudioFilter` and set the level on it, the renderers lose `Volume`/`IsMuted`, `PlaybackPipeline` loses `Volume`/`IsMuted`, `WasapiVolumeControl` and its tests are deleted, `NativeMethods` loses `IAudioStreamVolume`, `PlaybackInfoHandle` reads the host's level | The full suite; the player's volume still works, and the system's per-app volume is no longer touched |
| **S15d** ✅ | The Lab: a volume slider and a mute toggle that call `SetLevel` on the filter the page added, and the status line naming what is in the chain or saying there is none | Manual: the level is audible on WASAPI, survives an audio-renderer swap, and reaches zero and back |
| **S15e** ✅ | The plan of record: §4.1 has `Backends/Gain`, this section becomes the record | — |

*What the tests pin — the filter.* `LeavesAFullLevelBlockExactlyAsItWas` (same instance, bytes identical),
`ScalesEverySampleFormatByTheLevel` (one case per format, planar and interleaved),
`SilencesTheBlockWhileMutedAndRestoresItAfterwards`, `RefusesABufferThatDoesNotExposeItsPlanes`,
`TakesALevelFromAnotherThreadWhileApplying` (set it from a second thread throughout a run of blocks; every block
is scaled by one level or the other and never by a torn one) and `AcceptsALevelAfterItHasBeenDisposed`.

*What the tests pin — the chain.* `AddsFiltersInTheOrderTheyWereGiven`, `RefusesTheSameFilterTwice`,
`HandsAFilterBackWhenItIsRemoved`, `EmptiesTheChainWhenItIsCleared`, `LeavesOutAFilterThatRefusesTheStream`,
`PreparesAFilterAddedAfterTheStreamIsKnown`, `PreparesAnEarlyFilterWhenTheStreamArrives`,
`RunsTheFiltersInTheOrderTheyWereAdded`, `HandsBackTheBlockItWasGivenWhenNothingTransformedIt`,
`DropsAFilterThatCannotTransformAndCarriesTheBlockOn` (and is not asked again), and
`ReleasesABlockAFilterReplaced`; the pipeline adds `RunsTheAudioFiltersTheHostAdded`,
`TakesAFilterAddedWhilePlayingAtOnce` (no reconcile, state still `Playing`) and
`StopsUsingARemovedFilterWithoutDisposingIt`.
`FakeAudioRenderer` loses `Volume`/`IsMuted` and gains the block it was handed (`LastSamples`), and the two tests
that asserted on the removed properties became `AppliesTheLevelOfAFilterAddedBeforeAnythingIsOpen` and
`KeepsTheLevelWhenTheAudioRendererIsSwapped`, both of which compare the samples the device was actually given
against what the filter should have made of them.

*What the cutover changed.* `IAudioRenderer` no longer has `Volume`/`IsMuted`. `WasapiAudioRenderer` lost the
`IAudioStreamVolume` service, `ApplyVolume` and its fields, and `WasapiVolumeControl` with its tests is gone;
`IAudioStreamVolume` left the three `NativeMethods` lists; `NullAudioRenderer` lost the two fields it kept to
pretend; and `PlaybackPipeline.Volume`/`IsMuted` went with the line in `BuildChains` that copied a level onto the
renderer. `PlaybackEngineAdapter` now makes a `GainAudioFilter` per opened pipeline, sets the level the player has
already asked for on it before `Open`, keeps it in a field so `SetVolume`/`SetMuted` reach it directly, and disposes
it in `Close` — which is also the fix the earlier draft of this section promised, since the stored level used never
to reach a freshly opened pipeline. `PlaybackInfoHandle` reports that level instead of reading the pipeline, so the
playback information pane keeps its two rows. The Lab gained a volume slider and a mute toggle that drive the filter
the page added, and its status line names the filters in the chain.

*Verified for S15c and S15d.* The full suite, 250 tests, x64 and ARM64 clean for the whole solution.
`AppliesTheLevelOfAFilterAddedBeforeAnythingIsOpen` and `KeepsTheLevelWhenTheAudioRendererIsSwapped` assert on the
samples the device was actually handed (the file's loudest sample is 7967, so a quarter is 1992 and a half is 3984),
which is the difference between "the filter is configured" and "the sound came out quieter".

*What the plan leaves out.* No filters picker in the Lab: the page adds a gain filter and the slider drives it,
which is the configuration that ships. No extra row in the playback information pane: its `Volume`/`IsMuted` rows
keep working, now reading the player's own level. Nothing is localized beyond what was already there.

*What it does not cover.* Video effects (v2), filters that change the sample format, per-stream effect selection
beyond master volume, and enabling or disabling an effect without the restart `Reconcile` performs.

**S16 — Isolation hardening.** *Done. Small, and it turns a rule that was true by discipline into a gate.*

*What was measured.* Scanning the engine outside `Backends/FFmpeg` for FFmpeg names finds exactly one mention, in a
doc comment quoting libavcodec — no type crossed the boundary by the time this step was written either.

*What it adds.* `ArchitectureTests.NoFFmpegTypeCrossesTheEngineBoundary` walks every type in `SinePlayer.Core` that
is not in `Backends.FFmpeg` and asks what each one is made of: its base type and interfaces, its fields, properties
and events, the return type and parameters of every method and constructor, and the constraints of every generic
parameter — all of them including private members, because a backend detail kept in a private field is exactly the
drift the rule exists for. A type from the backend namespace reached directly, as an array or pointer element, as a
generic argument (`List<AVFrame>`), or as a constraint fails the test, and the message names the member and the type
so the report is a fix rather than an investigation. The test first asserts that the assembly really holds the
backend, so a scan that found nothing cannot pass by finding nothing.

*Verified.* Green on the tree, and proved non-vacuous the only way that counts: an `internal static AVFrame
LeakProbe` planted on `PlaybackPipeline` made it fail with `PlaybackPipeline.LeakProbe → …Interop.AVFrame`, and it
passed again once the probe was removed. 251 tests, x64 and ARM64 clean.

*What is still by review.* The other half of §4.2 — no WinUI type in the engine — has no test. It is true today (the
engine assembly references no `Microsoft.UI` type at all) and can be added to the same scan when there is a reason
to; the rule it protects is about the app-side presentation host, which is where a whitelist would be needed.

### Phase 4 — v2

Moved out: track selection (T1–T3), the video filter chain, subtitle rendering, the plugin surface and the
fallback policy are described in [MediaEngineV2Plan.md](./MediaEngineV2Plan.md).

## 7. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| x64 and arm64 FFmpeg binaries come from different channels | Version drift between architectures | `FFmpegRuntime` asserts the major versions at startup and reports what it found; supplying the arm64 files is the remaining step |
| Native payload size (~183 MB/arch) | Package size | Decoupled boundary allows a trimmed custom FFmpeg build later without API changes |
| arm64 builds from a checkout missing `native/win-arm64` | An arm64 build that opens no media | Mitigated — the libraries are committed, a build warning names the folder when they are absent, and `FFmpegRuntime` reports the missing file names per architecture |
| A pipeline test fails about once in ten suite runs | A red build that means nothing, and a real defect would hide among the noise | **Resolved — and it was not the tests.** In one process the whole assembly failed 17 of 209 while every one of them passed alone, and a two-pipeline test reproduced it without the suite at all. The snapshot of a stalled pipeline said it in one line: the reader had read the file to its end, `_readerTailVideoTicks` was still `-1` (no packet had ever been routed), and the pipeline was `Ended` at `00:00.000` with nothing shown — an *early* end, not a hang, so every test that waits for a frame simply ran out its budget. The cause: `RoutePacket` decides what a packet is for from the chain that is *running*, and the first video decoder is installed by the decode thread, so a reader that got in first dropped every packet down the "a stream nobody selected" path — a path that returns success — and then announced the end of the file. Two rules came out of it. **The reader routes by stream, never by component**: whether a decoder exists is not a fact for the reader to consult, so a packet is queued and the decode thread drops it if no chain can take it (it cannot eat the first GOP, because the swap is drained at the top of every iteration before a packet is taken, and the end marker is never dropped). **An end signal must be taken, not just posted**: the reader enqueues the audio end marker whether or not a chain exists, but the audio thread parked on `renderer is null` before it ever looked at the queue — so a file whose audio could not be opened never ended, and the pipeline sat in `Playing` after the picture had finished. The thread now consumes the queue with no device (spending the sound it cannot play) and reports the marker. The stream stays *selected* either way: the audio stream exists whether or not a device will take it, and a chain can be built for it later, so nulling the stream info would have made the failure permanent. The suite went from 187 of 209 in one process to 209 of 209, repeatedly |
| No fixture with audio *and* video | A defect whose cycle needs both cannot be reproduced in a test — the S8i freeze is the first of them, and it had to be reasoned out rather than measured | **Resolved in S8m** by `RawAudioVideoFixtureFile`: uncompressed frames and PCM written by hand into Matroska, intra-only and 200 frames long. It has already paid for itself by reproducing the reader-starvation deadlock that reasoning had missed twice |
| D3D/WinUI interop complexity (swap-chain attach, device loss) | Schedule, stability | COM interop is generated by CsWin32 rather than handwritten, so the ABI cannot drift; the swap chain remains the step that needs on-screen verification |
| A device that stops consuming audio | The one stall that can still stop the picture — and it is supposed to | **S8o:** this is no longer a special case. The device *is* the clock, so a device that takes nothing is time that has not moved: the audio thread waits for room without a deadline, the picture waits for a clock that is not moving, the reader idles once both queues are full, and nothing is dropped or failed. **S9e:** it is said as one line, `The audio device is not moving.`, raised on the audio thread a second into the stall and paired with `The audio device is moving again.` when the position moves |
| A device that accepts audio and never reports playing it | Every wait in the video path ends at the clock, so the picture stops and cannot be told to carry on | Same case as above, and the same answer: a wait that is visible (`StarvedFor`) and reported (one `WarningRaised` line). Nothing more can be done from inside the engine, and the report is what turns a mystery into a device problem |
| An audio block can claim the clock anchor with a pre-seek timestamp | The picture would sit behind the sound by up to a keyframe, which looks exactly like a seek that half-worked | Mitigated in S8o: `Seek` re-arms the anchor *after* flushing, so a claim made for the old position cannot outlive the seek — the next block or frame through `AnchorClock` re-points the clock at where playback now is |
| Every stage's liveness derives from the audio device | A device that stops reporting stops the picture *and* the sound, with no message: the sixth report | **Resolved in S8o** — the answer was not to give playback a second source of time but to make every wait end somewhere the device does not own: the reader waits only on the file, the decoder only on the picture, and the two waits that do end at the device are the ones that are supposed to (and are named while they last). See [PlaybackPipelineRefactor.md](./PlaybackPipelineRefactor.md) |
| A device fails mid-playback (lost adapter, removed endpoint) | One bad device would end the whole playback in a state where a renderer swap is refused — so the one cure available would be the one thing not allowed | **Resolved in S14e: a failed device is spent, not fatal.** The video and audio threads report a warning and keep going. Frames and buffers are spent rather than queued, so the end of the file is still reached and reported; the sound is spent at the clock's pace, so the position a swap would resume from is preserved; and the swap itself clears the failure and takes over. **Neither queue paces the read unless a chain fills it** — the read-ahead is driven by two targets (500 ms of sound, 32/64 picture packets) and each one is only consulted while something can meet it. A target that can never be met never lets the reader wait, so it reads the file at once: unbounded memory in `_audioQueue` for an audio-only file (decoded blocks, the expensive one), and unbounded in `_packetQueue` when the sound has no decoder (which also leaves a device attached later with nothing to play). Pinned by `DoesNotReadAnAudioOnlyFileAllAtOnce` (33 of 33 packets read in the first moment before the gate, about six after it) and `TakesSoundWhenADeviceArrivesAfterPlaybackBegan`. **A device that takes sound and never plays it is throttled, not trusted**: it holds nothing, so the queue it is fed from never fills and every consumer-reported bound says "no room needed" — while the sound it is given runs away from what has been played. When the last block taken is more than 3 s past the position, the sound is held back until the device has caught up (reported once per position and device — `The audio device is not playing what it is given.`), and while it is held back the clock is kept running, because otherwise the lead it is measured by could never shrink. `tailBufferPosition` is the evidence: the end of the last block taken, which for a working device can only be a queue target and a device buffer ahead of the clock. Pinned by `HoldsSoundBackFromADeviceThatDoesNotPlayIt`. **Reconcile rebuilds and resynchronises, and the two are separate steps**: `BuildChains` reports what it replaced through a `ChainChanges` flags value and wakes nobody; the *caller* decides. `Open` ignores both (the threads have not started). `Reconcile` treats any change as a new start — it advances the epoch with the position playback is at, which flushes the decoders, drops what was queued for the old chains and re-anchors the clock. That is a position decision, not part of building a chain, which is why the swap helpers know nothing about it. The consequence is deliberate: a swap now restarts the stream where it stands instead of carrying the old chains' media across (`SwapsTheVideoRendererWithoutMovingPlayback`, `SwapsTheAudioRendererWithoutLeavingPlayback`, `ReplacesTheAudioDecoderWithoutLeavingPlayback`). **A clock offset is chosen when a device appears, not only when the position is armed**: arming belongs to the sound and happens once per position, so a device that arrives after the picture armed the position would never get an offset — and `SyncClock` would stay off for the rest of that position, leaving the pipeline free-running on its own stopwatch. Measured before the change: 300 ms of position in 300 ms of wall time against a device running at 3×; `SyncsToADeviceThatArrivesAfterThePositionWasSet` pins it. **A device command throws, a device read does not**: `WasapiAudioClock.Start`/`Pause` latch the failure and let the exception out, because the pipeline is the single site that decides what it means — swallowing it there left the audio thread asking a dead device to start on every iteration and finding out only through a later hand-off. `Position` still never throws, because the host's timer and the picture both read it. Pinned by `EndsWhenThePictureDeviceNeverComesBack`, `BringsThePictureBackWhenAFailedDeviceIsSwapped`, `KeepsGoingWhenTheDeviceStopsTakingTheSound`, `BringsTheSoundBackWhenAFailedDeviceIsSwapped`, `KeepsGoingWhenTheDeviceWillNotStart`, `KeepsGoingAfterASeekWhenTheDeviceHasFailed`, `LetsGoOfAHandOffWhenTheDeviceDiesDuringIt`, and `RaisesAFailingStartOrStopAndKeepsTheFailure`. **Decoder failures stay fatal mid-stream** — a decoder is not a device that can be swapped from underneath, and `ReportsADecoderThatDiesAsPlaybackFailing` / `ReportsADecoderThatThrowsAsPlaybackFailing` pin that. **An end-of-stream drain is the exception**: it runs after the last packet has been read, so a decoder that cannot give up its tail has lost the tail and nothing more — the failure is reported as a warning and the end still goes out, or the picture and the sound would both wait for a stream that is already over. Pinned by `EndsWhenTheDecoderCannotGiveUpItsTail`, `EndsWhenTheSoundDecoderCannotGiveUpItsTail`, and `SurvivesADrainThatHandsBackNoTailAtAll` (a successful drain that hands back no list used to be walked as one, which is an exception on a thread with no one to catch it) |
| A/V sync and clock correctness | Core quality | Intro S7; keep `IPlaybackClock`/`ISynchronizer` swappable so alternatives can be compared |
| Silent coupling to FFmpeg over time | Loses the main design goal | Hard rules in §4.2 + architecture test in S16 |
| Long gap before user-visible payoff | Review fatigue | Playback Lab gives visible progress from S4 |

## 8. Definition of done (v1)

1. No `Windows.Media.Playback` / `MediaPlayerElement` usage remains in the repository.
2. All engine code lives under `SinePlayer.Core/Playback`; no FFmpeg type escapes
   `Backends/FFmpeg` (enforced by test).
3. `PlayerPageViewModel` differs from today only by the `using` line plus the deliberate feature
   wiring added in S10+.
4. Scenarios 1–4, the **audio half of scenario 5**, and 7 are reachable through the engine's capability interfaces
   and verified in the app. Scenarios 1–4 are reachable *through the engine* today (`Components` + `Reconcile`);
   reaching them from the player's own UI is the remaining surface work, unblocked now that S14 is done (§3.7).
   The video half of scenario 5 and scenario 6 (track selection) are v2 — see
   [MediaEngineV2Plan.md](./MediaEngineV2Plan.md) — and their contracts are present but unimplemented.
5. Subtitle, plugin, fallback and video-filter hooks are present as contracts/slots but unimplemented.
