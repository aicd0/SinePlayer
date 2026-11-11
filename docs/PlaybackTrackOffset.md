# Playback track offset

Status: design agreed; implementation in steps (§8). Pipeline ability only — no UI, no decoding change.

## 1. What it is

A per-chain shift inside one playback: a single video, audio or subtitle chain can be moved forward or
backward relative to the timeline, to line up sound against picture or words against sound.

## 2. Semantics

`offset` is stored per chain and is the amount added to that chain's own timestamps:

- a buffer, frame or cue at media time `t` is due when the clock reads `t + offset`
- positive = the track plays later (delayed), negative = earlier (advanced)

The timeline itself never moves. `Position`, `Duration` and seek targets are the timeline's and are
unaffected by any offset: "no matter what the new offset is, the current playback position has to
remain unchanged".

The read head is where the earliest content is needed. A chain delayed by `r` needs data from
`seek - r`, so the read position of an epoch is

```
readPosition = seekPosition + min(readOffset over playing chains)     where readOffset = -offset
             = seekPosition - max(offset over playing chains)
```

This is the "Min of all offsets of playing tracks" rule: the minimum is taken over the *read* offsets,
which are the negatives of the values the renderers add. Reading earlier than the minimum is never
required, and reading later loses the start of the most delayed track.

## 3. EpochGate

The gate is the only carrier of an offset: a renderer thread learns its own offset from the gate it
already reads each iteration, never from a second source.

```csharp
Epoch           // generation of what the reader has delivered
SeekPosition    // the timeline position the epoch was published at
SeekMode        // precise or keyframe landing, as before
ReadOffset      // min over playing chains of their read offset (-offset)
Offsets         // per chain read offset, frozen at that epoch
OffsetOf(chain) // TimeSpan.Zero when the chain has none
```

No `Kind`: a reseek and a resync are not categories of a publish, they are two consequences of it.

## 4. Who decides, and why there

The **worker queue** — `Pipeline - Playback`, the queue every pipeline operation is serialized on
(`PipelineEvents.Submit`). Deciding there is what makes the decision atomic with `Seek`, `Reconcile`
and `AddChain`: by the time the new gate is visible, it is already known whether the data the chains
hold is about to be invalidated. The reader is asynchronous, so deciding there would be too late — the
gate would already be published with a new read position while chains still apply the old one.

The worker publishes one of two things:

- **move** — from `Seek`, `Reset`, `Stop`, `Reconcile` (current position): a new `SeekPosition`/
  `SeekMode`; the read position follows from the offsets.
- **offsets only** — from `SetOffset`: same `SeekPosition`/`SeekMode`, replaced `Offsets`.

**The epoch advances exactly when the reader will re-seek**, because a re-seek voids everything that
has been delivered. Nothing else advances it, so "re-seek without a bump" and "bump without a re-seek"
cannot happen, and a chain may rely on it when it drops queued entries.

Re-seek decision:

| what happened | read start | re-seek |
| --- | --- | --- |
| the timeline moved (seek) | anywhere | yes |
| a chain was built since (track switch) | at the clock | yes, it holds nothing |
| a track moved **forward** | later | no, already delivered |
| a track moved **backward**, still within what was delivered | later or equal | no |
| a track moved **backward** past the delivered point | earlier | yes |

So the worker keeps, besides the offsets: the read position it last positioned the sources at
(`deliveredFrom`), and whether any chain is new since. The criterion is not "did the minimum change"
but "does the new read start lie before what has already been delivered".

## 5. MediaReader

No decisions. On an epoch change it seeks every source to `SeekPosition + ReadOffset` (and picks the
landing stream for that position), resets its tails, and continues. On an offsets-only publish nothing
changes for it.

`ReadTail` is reported in clock domain (`packet.Timestamp - offset`), so `ReadAhead`, `IsStreamSpent`
and `IsBuffered` keep comparing a delivered time with the clock. `SourceTail` stays in media domain —
it only compares packets with each other.

## 6. Chains

Read `gate.OffsetOf(this)` **per iteration** (not once per epoch: an offsets-only publish does not
change the epoch), and apply it at every place where a timestamp is compared with the clock.

Video (`VideoChain.Render`):

| site | after |
| --- | --- |
| start-time filter | `frame.Timestamp + offset < startTime` |
| sync evaluation | `Evaluate(frame.Timestamp + offset, position)` |
| anchor, `firstFrameTime` | `frame.Timestamp + offset` |
| wait for the next frame | `(frame.Timestamp + offset) - position` |
| `_pictureTicks` | `(frame.Timestamp + offset)` — clock domain, so `PictureLag`, `IsStarved` and `LastPresentedTimestamp` are untouched |

Audio (`AudioChain.Render`): the same four sites — start-time filter, `tailBufferPosition`, `Anchor`,
`clockOffset`.

Subtitles (`SubtitleChain.Render`): cues have no buffer timestamp, so the sites are the cue query
(`GetVisibleCues`, `TimeUntilNextChange`, `drawnPosition` comparisons, all with `position - offset` —
the chain's cursor at clock `P` is `P - offset`) and the cue window floor in the chain's epoch handling
(`CueWindow.SeekTo(epoch, SeekPosition - offset)`). That is a line in the chain, not in a decoder.

Cached values that live in clock domain must be recomputed when the offset changes; the simplest way
is to keep the raw media time and convert at the comparison.

## 7. API

```csharp
public Task<OperationResult> SetOffset(IPlaybackChain chain, TimeSpan offset)
public TimeSpan GetOffset(IPlaybackChain chain)
```

- rejects a chain this pipeline does not own, and any state but playing/paused/ended
- unchanged value: success, no publish
- offset survives `SelectStream` (the correction belongs to the track position, not the resource);
  it is dropped when the chain is removed
- after storing, the worker recomputes the read offset and publishes per §4

## 8. Steps

Each step builds, changes no behaviour beyond what it states, and is reviewable on its own.

1. **Gate and publish plumbing.** `EpochGate` gains `ReadOffset`, `Offsets`, `OffsetOf`;
   `PipelineTimeline.AdvanceEpoch` becomes `Publish(position, mode, readOffset, offsets)`; every
   publisher passes zero/empty; `MediaReader` seeks to `SeekPosition + ReadOffset` and seeds its tails
   from it. With zeros this is exactly today's behaviour.
2. **Renderer threads apply the offset.** Video, audio and subtitle take `gate.OffsetOf(this)` each
   iteration and apply it at the sites in §6; `ReadTail` is reported in clock domain. Still zeros, so
   still no behaviour change — the diff is mechanical and verifiable line by line against §6.
3. **Worker-side decision.** Offsets dictionary, `SetOffset`/`GetOffset`, `deliveredFrom`, the
   re-seek table of §4, publishing the gate with or without a bump, and the bookkeeping of §7.
4. **Tests.** A fake source that counts seeks: a forward move does not re-seek; a backward move past
   the delivered point does; a seek always does; the clock position is unchanged in every case; and
   the presented timestamps of a moved track shift by the offset while `PictureLag` stays small.

## 9. Open

- The published value is `offset` as §2 defines it (renderer adds it; positive = later), and the
  reader's read offset is its negative. Settled at review of step 1, because the sign appears in
  exactly one place: the worker that turns offsets into `ReadOffset`.
