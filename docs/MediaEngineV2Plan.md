# Media Engine — v2 and deferred work

Everything deliberately outside v1. [MediaEngineImplementationPlan.md](./MediaEngineImplementationPlan.md) is
kept to v1 alone; this document holds what was moved out of it, so that the v1 plan reads as a list of work
that is actually going to be done.

Nothing here is blocked by v1: each entry says why it is not v1 and what v1 leaves in place, so adding it is
additive rather than a redesign.

## 1. Track selection (scenario 6)

**Why not v1.** It is the only scenario that changes none of the others, and its cost is a chain-rebuild
mechanism nothing else needs. Every other scenario is reachable through components the pipeline already swaps.

**What v1 leaves in place.** `ITrackController` exists with no implementer, `MediaInfo` enumerates every stream
(video, audio and subtitle), and the reader already skips the ones nobody selected — so this adds a rebuild and
a picker, not a new pipeline.

| Step | Goal |
|---|---|
| **T1** | The pipeline can change one chain, audio first. A switch disposes that chain's decoder, bumps the epoch, re-creates the decoder for the new stream and resumes the previous state — reusing the seek path to keep the position. Verify with a fixture that muxes two audio tracks. |
| **T2** | Video track switching. The renderer already reallocates when a frame's size or format changes, so a different-resolution track needs no reconfigure. |
| **T3** | The switch reaches a surface: the Lab first, then a Tracks section in the player's settings panel, with subtitle streams listed but disabled. |

## 2. Video filter chain (the other half of scenario 5)

**Why not v1.** v1 proves the chain on the audio side, where it pays for itself immediately (see S15 in the v1
plan: volume and mute become a filter). Video adds two problems that audio does not have, and neither is worth
taking on before the chain itself is proven:

1. **Format negotiation.** An audio filter in v1 is required to keep the format it was given, so the chain is a
   straight line. A video effect can change what the renderer must be configured for — rotation swaps width and
   height, scaling changes both, colour work can change the pixel format — so `IVideoFilter.OutputFormat` has to
   be consulted when the renderer is configured, and a chain whose last `OutputFormat` differs from the decoder's
   stops being a chain and becomes a pipeline decision.
2. **Frame ownership.** A decoded frame is either in system memory or an `IGpuVideoBuffer` living on the device
   the renderer draws with. A filter therefore has three possible answers — work on the GPU, force a copy back,
   or refuse — and each has a different cost story. Doing this badly is how a "high-performance" effect becomes
   the reason a 4K60 file stops holding its frame rate.

**What v1 leaves in place.** `IVideoFilter` (`Configure(stream)`, `OutputFormat`, `Apply(input, out output)`) and
`IVideoFilterFactory`; the documented rule that a filter may return its input unchanged and that the caller owns
whatever comes back; and the seam between decoder and renderer, which is where the chain goes.

**Sketch.**

| Step | Goal |
|---|---|
| **V1** | The chain itself: filters resolved from the registry like every other component, built with the chain, and applied in order between decode and render. No effects yet — one pass-through filter proves the seam and its cost. |
| **V2** | Rotation and scaling, in that order: rotation is a pure geometry change that can be done in the shader the renderer already has, while scaling is where sampling quality has to be chosen. |
| **V3** | Format negotiation: the renderer is configured from the last filter's `OutputFormat`, and a chain whose output the renderer cannot take is refused at build time rather than per frame. |
| **V4** | The GPU path, so that a hardware-decoded frame can be processed without a round trip through system memory. |

## 3. Subtitle rendering

**Why not v1.** It is the one feature that adds a whole stage — a decoder, a renderer and their timing — without
being needed to prove anything about the others.

**What v1 leaves in place.** `ISubtitleDecoder`, `ISubtitleRenderer` and `ISubtitleDecoderFactory` exist, the
demuxer enumerates subtitle streams so they appear in `MediaInfo`, and the pipeline has the slot. Nothing
implements them, and a file with subtitles plays with the picture and the sound only.

## 4. Plugin surface

**Why not v1.** A plugin contract is a compatibility promise; publishing one before the component API has
settled would freeze whatever it happened to look like.

**What v1 leaves in place.** The registry is `internal` in v1 and is not exposed through any `IPluginContext`,
but registering a factory is one call, so exposing it later is purely additive.