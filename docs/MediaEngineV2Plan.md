# Media Engine — v2 and deferred work

Everything deliberately outside v1. [MediaEngineImplementationPlan.md](./MediaEngineImplementationPlan.md) is
kept to v1 alone; this document holds what was moved out of it, so that the v1 plan reads as a list of work
that is actually going to be done.

Nothing here is blocked by v1: each entry says why it is not v1 and what v1 leaves in place, so adding it is
additive rather than a redesign.

## 1. Track selection (scenario 6)

**Why not v1.** It is the only scenario that changes none of the others, and its cost is a chain-rebuild
mechanism nothing else needs. Every other scenario is reachable through components the pipeline already swaps.

**What v1 left in place.** A `ITrackController` contract with no implementer — since removed, because a sealed
pipeline is the only thing that can implement it and nothing was written against the abstraction — `MediaInfo`
enumerating every stream (video, audio and subtitle), and a reader that already skipped the ones nobody selected.
So this added a rebuild and a picker, not a new pipeline.

| Step | Goal | Where it stands |
|---|---|---|
| **T1** | The pipeline can change one chain, audio first. A switch re-creates the decoder for the new stream and keeps the position rather than restarting the file. | Done. `PlaybackPipeline.SelectStreamCore` publishes a new `MediaSourceHolder` (the source plus the two selected streams) together with the rebuilt chain, under one number per publication; the position is republished as a precise seek at where playback already was, and that epoch change releases the threads parked on the cleared queues. Verified with a fixture that muxes two tracks of each kind. |
| **T2** | Video track switching. The renderer already reallocates when a frame's size or format changes, so a different-resolution track needs no reconfigure. | Done, as predicted: the switch is one more frame with a different size, and the renderer follows it. A track whose decoder cannot be offered is refused and playback stays on the track it had. |
| **T3** | The switch reaches a surface: the Lab first, then a Tracks section in the player's settings panel, with subtitle streams listed but disabled. | Lab done — a video track picker and an audio track picker, each with an `Off` entry that turns its kind off, rebuilt from the playing selection after every attempt, so a refused switch shows the answer rather than the ask; with nothing chosen the line says so and Play explains the refusal. The player opens a file and then chooses what plays it: `PlayerPage` selects one track per kind with one `SelectStream` per kind, a kind that cannot be played is logged and the other one plays, and auto-play is asked for only after that (`CoreEngine.Load` opens and no more, the same as the pipeline, so a host that wants nothing selected can leave the file unplayed). What it selects is the track the user chose for that file last time — remembered in the media's ext (`SelectedVideoTrack`/`SelectedAudioTrack`, where `-1` records a kind the user turned off, which is a choice and not the absence of one) — and, when nothing was ever chosen, the track the container marks as its default (`MediaStreamInfo.IsDefault`, read from the disposition flags: Matroska's `FlagDefault`, MP4's track flags), falling back to the first of the kind when the container marks none. A stored choice that no longer names a track of the file is ignored, and a choice that would leave nothing to play is not restored at all. The player's own surface is the video host's context menu: **Video → Select video track** and **Audio → Select audio track**, each listing the file's tracks of that kind with the playing one checked and an `Off` entry that turns the kind off (the picture's surface is handed back when its renderer is let go of, so the last frame does not stand there for ever) (a kind the file has none of keeps the entry with a disabled `None` in it, rather than leaving the menu silent about why nothing is offered), built from `CoreEngine.PlaybackInfo` at the moment the menu opens and reporting a refusal in the player's prompt. The Tracks section in the settings panel is still to come. |

**What the switch taught the clock.** A switch re-publishes the position as a precise seek, and the sound of the
new track may have nothing to claim it with — the reported case is switching to a track that ends at 3:12 while
playback stands at 3:20. The picture claimed the position only when there was *no* audio decoder at all, so with
a decoder that had nothing to give, nothing claimed the position, `StartOrPauseClock` refused to start an
unclaimed clock, and the frame — nineteen milliseconds *ahead* of it — waited for a moment only the clock could
bring: `Playing`, a still picture, and nothing starved. The picture now asks on every present and `AnchorClock`
refuses when the position already has an owner, so the sound's claim still wins the ordinary case and the
picture covers no decoder, nothing left, or the kind turned off. Measured on the 1080p track of the sample file:
two and a half seconds of re-decoded lead-in, then sixty frames a second. The re-decode itself is the switch's
cost, not the clock's — a switch moves the shared demuxer, so the untouched picture is re-read from its
keyframe (§2.5 of `PlaybackPipeline.md`).

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