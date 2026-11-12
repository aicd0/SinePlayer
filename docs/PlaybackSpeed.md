# Playback speed

## What the feature is

The user sets how fast the pipeline plays: 0.1x to 8x, clamped, defaulting to 1.0x. The rate is not
persisted - every session starts at 1.0x. `PlayerSession.PlaybackRate` and `SetPlaybackRate` already exist
as the surface for it; `SetPlaybackRate` currently throws `NotSupportedException`, and `PlaybackRate`
returns 1.0.

## What speed costs, per chain

| chain | work | why |
| --- | --- | --- |
| video | none | pacing is `Synchronizer.Evaluate(frameTimestamp, timeline.Position)`, so a faster clock drops and repeats frames by itself |
| subtitle | none | cues are placed against `timeline.Position - offset` |
| timeline clock | rate plumbing | the timeline's own clock is the authority when no audio chain owns the clock; when one does, it free-runs between `SyncClock` corrections and must advance at the same rate or the 10 ms dead band corrects it on every iteration |
| audio | resampling | audio is pushed eagerly and the device renders at its own fixed rate. Faster playback means handing the device *fewer* frames for the same media (output frames = input frames / rate). Without this the audio-led sync pulls the clock straight back to 1.0x |
| `AudioPlayhead` | spans carry a media extent | it resolves `media = MediaStart + frames / sampleRate`, which stops being true once blocks are resampled: at 2x a block of 20 ms of media is 10 ms of frames. Each span must say which media extent its frames cover |

Two waits convert a *media* delta into a *wall-clock* timeout, and both are correct only at 1.0x:

* `VideoChain` - `remaining = (timestamp - position).TotalMilliseconds` before `RenderNode.Wait`;
* `SubtitleChain` - `timeout = ceil(next.Value.TotalMilliseconds)` before `RenderNode.Wait`.

At 8x they sleep eight times too long and then catch up by dropping frames or skipping subtitle changes.

`AudioChain` has one more site that assumes frames encode media time: `tailBufferPosition = timestamp +
buffer.Duration`. With resampling, `Duration` is `output frames / sampleRate`, i.e. the media extent divided
by the rate, so it must be given the media extent instead.

`AudioChain`'s "the device is not playing what it is given" guard compares `tailBufferPosition - position`
against 3000 ms of *media* time. At 8x the queue holds eight times more media than at 1.0x, so the guard
must scale with the rate or it will fire on a healthy pipeline.

## Design

### The rate is a layer above the clock

`IPlaybackClock` describes what a clock reports — position, whether it runs, start, pause, set position — and nothing about
how fast playback is. A device renders at its own rate and a stopwatch measures real time; neither knows how fast the file
is being played. `Rate` and `SetRate` therefore come off the interface, and with them the copies that had grown in
`StopwatchClock` and `WasapiAudioClock`.

The rate lives in `RetimedClock`, a clock in its own right that wraps another one and scales what it reports from the
moment the rate was set. `PipelineTimeline` holds one over a `StopwatchClock` and exposes `Rate`/`SetRate`, so everything
else in the timeline — seeks, anchors, corrections from the audio device — is unchanged, because the retimed clock *is*
its clock. Setting a position (a seek, an anchor, a correction) restarts the measurement from exactly there, so a placed
position is never scaled by what came before it; setting a rate re-anchors the same way, so the position does not move.

### The audio is resampled

A new `SpeedAudioFilter` in the audio filter chain, in the shape of `GainAudioFilter`:

* `IAudioFilter`, `OutputFormat => null`, so a rate change never reconfigures the renderer and never flushes;
* `SetSpeed(double)` is a single value written whole and read once per block, so any thread may set it while
  the audio thread transforms;
* the output block keeps the input's `Timestamp` and media extent, and its `SampleCount` becomes
  `input.SampleCount / rate`;
* it is stateful across blocks - a fractional source position, and the accumulator used when decimating;
* for rate > 1 it averages the samples it skips rather than picking one, because point-sampling at 8x aliases
  badly. That is the cheap stand-in, not the final answer.

It is registered per audio chain in `PlayerSession` after gain and downmix, so it resamples the fewest
channels.

Pitch follows the rate, like a tape. Preserving pitch needs time-stretching (FFmpeg's `atempo`); the filter
keeps the same `SetSpeed` contract so that can be a separate step later.

### The playhead understands a media extent

`AudioPlayhead.OnHandOver(frames, sampleRate, startTime, endTime, deviceTime)`, with `Span` carrying both ends of the media
it covers, and the resolution interpolating by frame proportion:

    media = StartTime + (deviceFrame - StartFrame) / (EndFrame - StartFrame) * (EndTime - StartTime)

At 1.0x this is arithmetically identical to what came before, so every existing playhead test passes with only its call
sites changed.

One consequence: a block's media extent is a length, which the playhead has so far refused to use. There is
no way around it - at a rate other than 1.0 the frames no longer encode media time. The extent comes from the
block as the decoder produced it (before resampling), which is the same quantity the resolution already
assumed at 1.0x; and every block still re-anchors at its own timestamp, so a file whose timestamps disagree
with its sample count is still followed.

### The device clock is not touched

`WasapiAudioClock` reports what the device has rendered, and after this change that is all it can report: the abstraction it
implements has no rate. Once the audio is resampled and the playhead's spans carry the media extent, the mapping from device
time to media time is complete, and the retiming above applies to the timeline's own clock only. So the device's reading is
never scaled, and the rate cannot be counted twice.

### A rate change is adopted, not averaged

The playhead averages a new measurement inside 50 ms of the last one (weight 1/16). Right after a rate change,
`offset = devicePosition - position` moves quickly - about 15 ms per hand-over at 4x - and averaging would
take tens of hand-overs to follow. The rate change therefore sets a flag that makes the next measurement be
adopted outright.

Audio already in the device plays at the old rate until it drains - at most one queue depth, on the order of
200 ms. No flush: a gap would be worse, and the picture follows the clock meanwhile.

## Steps

1. **Done: the clock takes a rate, from above it (internal).** `Rate`/`SetRate` come off `IPlaybackClock`; a new `RetimedClock`
   wraps a clock and scales it; `PipelineTimeline` holds one over its stopwatch and exposes `Rate`/`SetRate`. Verified by
   the retimed clock's own tests (hand-cranked, no sleeps) and the timeline's.
2. **Done: the playhead understands a media extent.** `OnHandOver(frames, sampleRate, startTime, endTime, deviceTime)`,
   `Span` carrying both ends of the media, the interpolation, and `AudioChain` reading the media duration before the
   filters run - also for `tailBufferPosition`. Every existing playhead test passes with only its call sites changed, plus
   one where the frames and the media extent disagree.
3. **Done: `SpeedAudioFilter`.** `Playback/Backends/Speed/SpeedAudioFilter.cs`, in the shape of the gain filter: one
   lock-free speed read once per block, `OutputFormat => null`, and a speed of one returns the block it was given. Faster
   averages the frames it skips (point-sampling would alias at 8x), slower interpolates between the frames it lands on, and
   the fractional remainder carries into the next block so the sound stays continuous. Output keeps the timestamp, format
   and layout, so nothing downstream has to be reconfigured. Verified by 20 tests: frame counts across 0.1x-8x, a constant
   signal surviving all six sample formats, the two interpolation paths checked sample by sample, 40 blocks at a steady
   level staying steady (no drift), channels kept apart both interleaved and planar, unusable speeds taken as none, and a
   format it cannot read refused with the block handed back. Not wired into the pipeline yet - that is step 4.
4. **Wire it up.** `PlaybackPipeline.Rate/SetRate` (clamped to 0.1-8) sets the timeline rate, calls `SetSpeed`
   on every audio chain's filter, and raises the playhead's adopt-next flag; `PlayerSession.SetPlaybackRate`
   stops throwing; the two wall-clock conversions divide by the rate (one helper on the timeline, so no third
   site appears later); the audio leeway guard scales with the rate. Verified with the audio stand-in: the
   clock advances at the rate, the frames submitted per media second scale, a mid-stream change neither
   flushes nor jumps the position, and 1.0 restores exactly.
5. **Optional, later: pitch-preserving stretch.** `atempo` (or another time-stretcher) behind the same
   `SetSpeed` contract, chosen by preference.

## Accepted costs

* Pitch tracks speed until step 5. At 8x, decimation is box-filtered rather than properly low-passed.
* A rate change takes up to one queue depth to be fully heard, and is not persisted across sessions.
* At 8x the decoder really does read eight times as much media per wall second, and most video frames are
  dropped by design.
