# PlaybackPipeline — the design, and the rules a change must not break

**Status:** implemented and verified (S8o). **Audience:** anyone changing the orchestration. This is the
conceptual contract — deliberately no code — and it is written to be the thing a future change is checked
against rather than a description of whatever the code happens to do today.

Related documents: [MediaEngineDesign.md](./MediaEngineDesign.md) (why the engine has this shape),
[PlaybackPipelineRefactor.md](./PlaybackPipelineRefactor.md) (the design review, the six failures that led to
this design, and the reasoning behind each rule), [MediaEngineImplementationPlan.md](./MediaEngineImplementationPlan.md)
(the step-by-step delivery record; S8o is this design).

---

## 1. What the pipeline is

One instance is one playback session. It:

- composes the participants — a source, one decoder per selected stream, one renderer per stream — from
  injected factories, so the same orchestration runs against real backends and against test doubles;
- owns the four threads that move media, and the state machine the host sees;
- owns **the clock**: for a file with sound, the audio device's position is playback time; for a silent file,
  a wall clock is;
- owns the **epoch**: a counter that says which "where playback is" a piece of work belongs to;
- reports what is happening: position, counts, and the one thing the host needs when the picture stops — the
  reason.

It is not responsible for container parsing, decoding, device IO, the present/render decision, or which
component is chosen for a stream. Those live behind the contracts, one layer down.

## 2. The four threads

| Thread | Priority | Owns | Produces | Consumes |
|---|---|---|---|---|
| **Reader** (the "playback" thread) | Above normal | the source, the audio decoder, and both of their queues | video packets → packet queue; decoded audio buffers → audio queue | the file |
| **Video decode** | Normal | the video decoder | decoded frames → frame queue | the packet queue |
| **Video presentation** | Normal | the video renderer | what is shown | the frame queue, and the clock |
| **Audio sender** | Above normal | the audio renderer | nothing; it submits | the audio queue, and the device's room |

```
           file
            │
        ┌───┴──────────── reader ────────────┐
        │  video packets           audio decode (in place)
        ▼                                    ▼
   packet queue                          audio queue
        │                                    │ (wait: device room)
        ▼                                    ▼
   video decode                        audio renderer ──── device
        │ (wait: frame-queue room)                     ▲
        ▼                                              │
   frame queue ──── video presentation ────────────────┘
        ▲                    │  (wait: the frame's moment)     the clock
        └────────────────────┴──────────────────────────── runs / stops here
```

Five things to know about the thread layout, because each is load-bearing:

1. **Audio is decoded on the reader's thread.** It is cheap, and every hand-off is another chance for the
   sound to be late. Video is not, so video is handed on and decoded elsewhere — otherwise the reader would
   wait for a video decoder and stop reading the audio packets interleaved behind the frame being decoded.
2. **Audio submission is a thread of its own** because it waits on the device. Reading and the device are two
   different long waits and cannot share a thread.
3. **Presentation is a thread of its own** because it waits on time; if it shared a thread with decoding, a
   slow keyframe would become a visible stall, and the picture would lose its look-ahead.
4. **FFmpeg's internal threads do not reduce this count.** They are helpers inside one blocking call — a thread
   we own still has to drive the decoder and can still block inside it — and they know nothing about our clock,
   our device, or which frames have become too late to show.
5. **Nothing here waits for another stage's progress.** That is the property the whole design exists to
   preserve; §10 and §12 are about keeping it.

## 3. The three hand-offs

There are exactly three queues, and each has a different policy because each is there for a different reason.

| | Packet queue | Audio queue | Frame queue |
|---|---|---|---|
| **Between** | Reader → video decode | Reader → audio sender | Video decode → presentation |
| **Holds** | encoded video packets | decoded audio buffers | decoded video frames |
| **Maximum** | none | none | 8 frames |
| **A full queue means** | — | — | the picture is behind, so the decoder stops |
| **Producer waits?** | never | never | yes, until the picture takes a frame or a seek moves the epoch on |
| **Consumer waits?** | up to a short poll for a packet | up to a short poll for a buffer | up to a short poll for a frame |
| **On a seek** | cleared; a packet still in flight is refused (it belongs to the old epoch) | cleared; a buffer already handed to the device is flushed by the sender | cleared; a frame the decoder was mid-hand-off with is refused |
| **At end of file** | completed: no more packets; the reader *survives* so a replay can read again | nothing special — an empty queue after the end is the sound playing out | nothing special — the remaining frames play out |

**Why two of the three have no maximum.** The reader is the single cursor of an interleaved file: what it has
not read yet is *both* streams. A bounded packet queue therefore means "the picture being slow stops the
sound" — the coupling behind several of the historical failures. The reader paces itself instead (§4.1), and
what bounds the read-ahead in practice is playback itself: the minimums advance only as fast as the file is
played.

**Why the frame queue does have one.** Its consumer is the picture, and the picture is the one stage that
follows the clock. A decoder stopped here is stopped by the thing it feeds, and starts again the moment the
picture takes a frame. It is also the only place where decoded — i.e. largest — data can pile up.

## 4. The five rules

**Every thread waits only for something it alone depends on, and every wait it takes ends without another
stage having to make progress.** These are the rules; §4.1–§4.5 are why each is written the way it is.

| # | Thread | Waits for | Ended by | If it never ends |
|---|---|---|---|---|
| 1 | Reader | the file | a packet arriving | the file is the outside world; this is the one legitimate IO stall |
| 2 | Reader | *nothing else* — it idles while both queues hold their minimums | either queue falling below half of its minimum | it keeps idling and re-checking |
| 3 | Video decode | room in the frame queue | the picture taking a frame; a seek moving the epoch on | the picture is not draining — and a seek still releases it |
| 4 | Presentation | the clock reaching a frame's moment | time passing | only a clock that is not moving, i.e. rule 5 |
| 5 | Audio sender | room in the device's buffer | the device playing; a seek moving the epoch on; pause/stop | a device that is not consuming — which stops the clock, so it is the same wait as rule 4 |

> **Only presentation waits on the clock. Only the reader waits on the file. Nothing waits on the reader.**

### 4.1 The reader never drops and never blocks on a stage

It fills both queues to their minimums, then idles until either falls below half of its minimum, then fills
again. The minimums are *read-ahead*, not capacity: 500 ms of audio samples and 64 video packets, waking at
250 ms and 32. A stream the file does not have is trivially satisfied.

The reader has **no drop path at all**. The single case in which a read packet is not handed on is a seek that
moved the epoch while it was being read — work for a position nobody wants — and that is the seek's doing, not
a policy of the reader's.

*Forbidden by this rule:* any maximum on the packet or audio queue; any drop on overflow; asking the reader to
wait for a decoder, a renderer or the device.

### 4.2 The video decoder never drops and never consults the clock

It takes a packet, decodes it, and hands the frames to the frame queue; if there is no room it waits there.
Two things it deliberately does *not* do:

- **It does not discard frames to make room.** Discarding the oldest frame throws away exactly the frames the
  picture is about to need — and makes a slow decoder look like a rendering fault rather than a slow decoder.
  A decoder that cannot keep up therefore *pauses playback* (§4.3), which is the honest signal.
- **It does not look at the clock.** Showing a frame at the right moment is the presentation thread's
  business. A decoder gated by the clock is how a stalled device used to stop the sound as well as the
  picture: it is an edge of the historical deadlock (§12).

While paused the decoder stands still rather than filling the frame queue; that is what keeps a pause from
spending the look-ahead the resume needs.

### 4.3 Presentation loses frames, best-effort, and holds the clock when it cannot

The picture is the follower, and it is the **only** stage allowed to give up on media:

- **Never early.** If the frame's moment has not come, it waits for it, a millisecond at a time. That wait is
  what makes the picture follow the sound, and nothing else waits on this thread.
- **Drop only when a closer frame is already waiting.** A frame is released only when the clock has left it
  behind *and* the queue already holds a frame that belongs closer to the clock. The alternative — dropping on
  the clock's opinion alone — throws away the newest frame there is when the queue is empty, which turns a
  slow decoder into a frozen picture.
- **Hold the clock back when the picture cannot catch up.** If the newest frame the decoder has produced is
  more than **200 ms** behind the clock, the clock stops — playback waits for its own picture — and starts
  again once the gap is within **100 ms**. A wide threshold to halt and a narrow one to resume, so a decoder
  hovering at the boundary cannot make playback stop-start.
- **The trigger is a lag, not a gap between frames.** A gap only exists when frames stop arriving, which a
  uniformly slow decoder never produces; a lag exists as soon as the decoder falls behind, which is exactly
  the case that needs the sound to wait. This was the one correction the design made to itself during review.
- **Waiting costs no frames.** While the clock is held, the decoder keeps producing and the picture keeps
  showing; only the drop rule above loses anything.

### 4.4 The audio sender keeps the clock honest

It never drops a buffer. Two situations arise while it works:

- **The audio queue is empty.** There is nothing to play, so the clock is stopped: time may not pass while
  the sound is missing, or the picture would run ahead of silence. The next buffer starts it again. After the
  end of the file an empty queue is the sound *playing out*, not falling behind, so the stop does not apply.
- **The device has no room.** The buffer waits — no deadline, no retry, no nudging — while the device is
  watched (§9). The wait ends when the device takes what it holds, when a seek moves the epoch on, or when
  playback is paused or stopped. The buffer being carried is *kept* across a pause and *let go of* on a seek.

One ordering rule matters here: the clock is anchored from the first buffer that will actually be heard, which
means the claim happens before the wait for room. A seek re-arms the anchor after it flushes, so a claim made
for the old position cannot outlive the seek.

### 4.5 A device that stops is a pause, not a fault

The device is the clock, so a device that stops taking audio is time that has stopped: presentation waits for
a moment that never arrives, the audio sender waits for room, the decoder fills the frame queue and stops, and
the reader idles once both of its queues hold their minimums. **Nothing fails, nothing is dropped, and nothing
anywhere else in the pipeline needs a special case.** The historical special cases (a nudge, a stall guard, a
decode gate) were themselves the chief source of stalls, which is why the design refuses to have any.

What the design does owe the user is the reason, so the device is *named* (§9) — a picture that stopped for no
stated reason is the failure mode this whole document exists to prevent.

## 5. The clock, and the anchor

- **There is one clock, and its source is chosen at open**: the audio renderer's device position when the file
  has sound, otherwise a wall clock. Nothing else in the pipeline measures time.
- **The clock runs only while playing and not waiting for a stage** (§8). Every state change reapplies this.
- **The clock is not authoritative about where playback started.** A demuxer lands on the keyframe at or before
  the requested position, so what will actually play first is earlier than what was asked for. The clock is
  therefore *anchored* by the first piece of media that plays — the first buffer handed to the device, or,
  for a silent file, the first frame the presentation thread takes up — never by the requested position.
  Anchoring to the request leaves the picture permanently a keyframe behind the sound, every frame of it late
  enough to be dropped: the picture then never moves again.
- **The anchor is re-armed by a seek**, and the reset happens after the flush, so no stale claim survives a
  position change.
- `Position` is the clock's position. Until something plays, it is whatever a seek set.

## 6. Seek

A seek is the one operation that must interrupt other threads, and it does so by **making the epoch move
rather than by waiting for them**. In order:

1. Pause (if playing), so the state machine says nothing is playing while the media state is rewritten.
2. **Advance the epoch — before taking the lock the reader holds.** This is what releases the reader if it is
   mid-read, and what makes everything already queued or in flight belong to a position nobody wants. Doing it
   before the lock is what keeps a seek from waiting on the reader for more than a single packet read.
3. Move the source, then under the media lock: flush the audio decoder, clear all three queues, reset the
   decoded tail, clear both starvation flags and the "source ended" flag, flush the device, set the clock to
   the requested position, re-arm the anchor, and ask the video decode thread to flush its decoder.
4. Resume if it was playing.

What each waiter does when the epoch moves:

| Waiter | Reaction |
|---|---|
| Reader | a packet read for the old epoch is released rather than routed |
| Video decode | a packet queued under the old epoch is dropped without decoding; a flush request is honoured before the next decode |
| Frame queue producer | a wait for room is released, and the frame it was carrying is released with it |
| Presentation | the held frame is released; frames queued under the old epoch are invalid by construction |
| Audio sender | a wait for room is released and the carried buffer is let go of; a submit that raced the seek is followed by a device flush |

Two details that are easy to get wrong and were both bugs once: the flush of the video decoder must happen
**on the thread that owns it** (a frame-threaded decoder cannot be flushed from outside), and the epoch check
around a submit has to happen **after** the device call as well as before it, because a device that had
stopped consuming can make a submit outlast the seek that interrupted it.

## 7. States, pause, and the end of the file

States: `None → Opening → Playing ⇄ Paused → Ended`, with `Error` reachable from anywhere. `Stopped` is
`None` with nothing open.

- **Pause** stops time and freezes all four threads where they are; each queue keeps what it holds. Nothing is
  discarded, and a buffer the audio sender was carrying is kept rather than dropped.
- **Play** restarts time through the same single path every state change uses, which consults the starvation
  flags: playback resumes into "playing and not waiting", never into "playing but stopped".
- **A state change is the only thing that starts or stops the clock**, so there is exactly one place where
  "should time be passing" is decided.

**End of file.** The reader's end-of-file is the end of the *packets*, not of the media:

1. The reader marks the source as ended and completes the packet queue.
2. The video decode thread drains the decoder's delayed frames into the frame queue and reports the tail is
   done.
3. The audio decoder is drained into the audio queue.
4. The pipeline waits for the frame queue to empty, for the device to finish what it holds, and for the audio
   queue to empty — each bounded, and the frame wait also ends if the clock stands still for half a second,
   because frames nobody can show are not worth the wait.
5. Only if the epoch has not moved in the meantime is the state `Ended`; a seek that arrived during the
   tail-out wins and playback simply continues from the new position.

The "source ended" flag matters beyond bookkeeping: it suppresses both starvation halts, because *after the
end an empty queue is the media playing out, not a stage falling behind*. Getting that wrong makes the end of
the file hold the clock back forever.

Both the reader and the video decode thread stay alive past the end of the file, because a replay — or a seek
from the end — has to have something to read and decode with.

**Stop and dispose** close the queues first, which releases the threads waiting on them, then join each thread
with a bound, then release the device. The order is deliberate: closing the queue the thread is waiting on is
what makes a join that follows safe, and flushing the device before joining the audio sender is what releases
a submit that a device has stopped consuming.

## 8. Starvation: what holding the clock back means

"Halting the clock" means exactly one thing: **playback waits**. The state stays `Playing`, nothing is
discarded, nothing fails, and time resumes on its own when the wait ends.

| Flag | Set by | Set when | Cleared when |
|---|---|---|---|
| Waiting for video | presentation | newest decoded frame > 200 ms behind the clock | within 100 ms again, or no clock/tail/anchor yet, or the source has ended |
| Waiting for audio | audio sender | the queue was empty and the source has not ended | the next buffer is there |

Both feed one decision — *should time be passing* — which is: playing, and neither wait is in force. The
hysteresis (200/100 ms) and the "not before the first anchor" condition exist so that ordinary jitter cannot
make playback stop-start; the "source ended" condition exists so the tail-out is not mistaken for a stall.

Two things this must never become:

- a **state change** (no `Rebuffering`, no error, no event storm at the display rate);
- a **reason to drop anything** — the drops stay where they are, in the presentation thread's best-effort rule.

## 9. The stalled device

A device that accepts audio and stops moving its position is the one failure the engine cannot repair, and the
design's answer is to **do nothing about it, and say so**.

- Everything already waits correctly: the audio sender waits for room, the clock does not move, presentation
  waits for a moment that never arrives. No thread is spinning and nothing is burning CPU.
- The device is watched from the audio thread, which is the thread that owns it, and only when a stall is
  *expected to show progress*: playing, not waiting for a stage, not ending, its anchor claimed, and the clock
  running. A position that has not moved for a second under those conditions earns the warning
  `The audio device is not moving.`; the first movement clears it. No error is raised, no wait is interrupted,
  and no recovery is attempted.
- **Forbidden here:** nudging the device, deadline-based retries, treating the stall as an error, or letting
  any wait on the device end without the device. Each of those was tried and each one turned a visible wait
  into an invisible one (or into a pipeline that thrashes). If the device is removed from the machine, the
  device call itself returns an error, and *that* surfaces as a pipeline error — which is the correct place
  for it.

## 10. Invariants a change must preserve

A change is safe exactly when it leaves all of these true. "Pinned by" names the test that fails first.

| # | Invariant | Why it matters | Pinned by |
|---|---|---|---|
| I1 | Only the presentation thread waits on the clock | Every historical deadlock had a clock wait somewhere else in the cycle | pipeline tests: a device that never moves must not stop the picture from being shown; a decoder too slow must hold the clock, not drop its output |
| I2 | The reader's only waits are the file and its own read-ahead idling | The reader is the sound; anything that stops it stops audio production | queue tests: unbounded enqueue; a decoder at a standstill must not stop the reader |
| I3 | The video decoder waits only for frame-queue room, and drops nothing | A decoder that drops hides its own slowness; a decoder waiting on anything else adds a cycle edge | frame-queue test: the producer waits for the picture rather than losing a frame |
| I4 | The presentation thread never displays early and never waits for one particular frame | Both are the failure modes called "frozen" and "out of sync" | `StartsWhereTheFileResumesRatherThanWhereTheSeekAsked`, `DropsOnlyTheFramesWhoseMomentHasGone` |
| I5 | Nothing waits for a queue that no longer exists: every wait is released by a seek, a pause, a stop, or a close | Bounded liveness: every wait has at least one exit that is not another thread's progress | the seek tests, delivered as "the seek never returned" assertions |
| I6 | Media carries the epoch it was produced for, and is refused rather than replayed when the epoch has moved | A seek that half-works is worse than one that visibly restarts | `SeeksWhilePlayingAndKeepsGoing`, `SeeksWhileTheNextFrameIsStillWaiting` |
| I7 | An empty queue after the end of the file is played out, never waited for | Otherwise the tail of every file is held back forever | `PlaysAnAudioFileThroughTheRenderer`, `PlaysAVideoOnlyFileToTheEnd`, `PlaysAgainAfterTheEndOfTheFile` |
| I8 | Losing media happens in exactly one place, and it is counted | A media pipeline that loses frames silently cannot be diagnosed | `DroppedFrameCount` assertions in the drop and device tests |
| I9 | Every wait ends; a stalled device is a wait that ends when the device does, and is named while it lasts | The user must never see a freeze with no reason | `WaitsForADeviceThatNeverTakesAudio`, `WaitsForADeviceThatIsNotMovingAndThenCarriesOn` |
| I10 | The clock starts and stops in exactly one decision, and a state change always re-evaluates it | Two places deciding "is time passing" is how pause, starvation and seek start fighting | `PausesAndResumes`, and every test that asserts `ClockIsRunning` |

## 11. Changes that look reasonable and are not

| Change | What it will cause |
|---|---|
| Give the packet queue a maximum, or make the reader wait on it | The picture's slowness stops the reader, and the reader is the sound: audio starves behind video. This is the coupling the design removed; it is the first thing to check if a "sound stutters when the video is heavy" report ever comes back |
| Give the audio queue a maximum | Same failure from the other side, and now the picture waits too, because the clock is the sound |
| Make the frame queue discard to make room again | Frames the picture is about to need are thrown away; a slow decoder then *looks* fine (nothing blocks) while the picture stutters, and starvation never triggers because the decoder never falls behind |
| Let the decoder consult the clock (any "decode window", pacing or gating on time) | A stalled device stops decoding, so the delay outlives the device and the sound loses its catch-up window |
| Add a deadline, retry or nudge to the wait for device room | The wait now ends without the device; the block is submitted into a device that is not consuming, or dropped, and the "device is not moving" diagnosis stops being true |
| Drop audio when its queue grows | A gap in the sound, which is the one thing the whole design is arranged to prevent |
| Display a frame before its moment to "keep moving" | The picture runs ahead of the sound, and the drop rule then starts discarding frames to correct it — visible judder instead of a wait |
| Drop a frame whenever the clock has passed it (no peek) | The newest frame is thrown away whenever the queue is briefly empty; a slow decoder then freezes the picture instead of pausing playback |
| Treat starvation as a state or an error | The display starts flickering a rebuffering state at the frame rate, and hosts start tearing down playback that only needed to wait |
| Make the seek wait for the reader (lock the media while seeking without advancing the epoch first) | The seek waits for IO, and every wait behind it waits for the seek: the S8f freeze |
| Flush the video decoder from the seek's thread | A frame-threaded decoder is not thread-safe for that; the flush must be *requested* and performed by the owning thread |
| Anchor the clock to the seek target rather than the first played media | The picture sits a keyframe behind the sound forever, every frame dropped, the picture never moves again (the S8g symptom) |

## 12. The deadlock class, so a regression is recognizable

Every stall this engine had was the same shape: **a circle of waits**, entered from a different side each
time.

```
presentation waits for the clock to reach a frame
        ↑ because the frame queue is full
video decode waits for room in the frame queue
        ↑ because the packet queue is full
the reader waits for room in the packet queue
        ↑ and produces no more audio
the device has nothing to play, so the clock never moves
        ↳ which is what presentation was waiting for
```

The design cuts every edge of that circle: the reader never waits for a queue (so a stopped decoder cannot
stop the sound), the decoder never waits for the clock (so a stopped device cannot stop decoding), and the
only clock wait left is on the thread that is allowed to wait for time.

**If a freeze is reported again, read the dump against this circle first.** The old signature — reader inside
an enqueue, decoder inside an enqueue, presentation waiting for time, audio waiting for the device — means the
circle has been re-formed; the question to answer is *which edge grew back*, and §11 is the list of the ways
that has happened before. A freeze with the *presentation thread inside a renderer call* is not this class: it
is §14.1.

## 13. Diagnostics

| Signal | Means | Watch for |
|---|---|---|
| `PresentedFrameCount` | frames shown | stagnant while `Position` advances ⇒ the picture is not following the sound |
| `DroppedFrameCount` | frames decoded and never shown, all of them lost by the best-effort rule | climbing ⇒ the picture cannot be shown in step: a decoder or a renderer too slow |
| `PictureLag` | how far the shown picture is behind the clock | a small constant is the synchronizer working; a growing one is the old freeze arriving |
| `IsStarving` / `StarvedFor` | time is deliberately stopped, and for which stage | "video" ⇒ a slow decoder is pausing playback (expected under load); "audio" ⇒ the file or the decoder is not feeding the sound |
| `Warning` | a device that has stopped moving, or audio that was refused | a warning is not a failure; it is the reason a picture is not moving |
| `AverageDecodeMilliseconds` / `AverageRenderMilliseconds` | cost per stage | either approaching the frame interval means that stage is what limits playback |
| `Position` / `Duration` | the clock, and the file | position advancing while nothing else moves ⇒ a starvation flag is being missed |

The Playback Lab shows all of the above in one line, which is the intended way to diagnose a report.

## 14. Known limitations, stated rather than implied

1. **A renderer that never returns** — a GPU hang — grows the frame queue until the decoder stops, but it
   produces no lag against the clock, so it is not detected as starvation and the pipeline waits there until
   the renderer returns. It is the one wait whose end belongs to another component. The test to write when it
   becomes real: a renderer that stalls for a second and then returns, asserting the picture resumes without a
   seek.
2. **A "cap" is a review concept, not a mechanism.** There is no overflow counter and no warning when a queue
   grows unexpectedly: with the reader self-paced and the frame queue bounded, an unexpectedly long queue
   means a bug (§11), not a condition to be handled at runtime.
3. **Playback rate changes and component switching are not implemented.** The clock can be retimed and the
   factories are swappable, but a rate change or a live decoder/renderer swap has no orchestration yet; the
   host currently refuses rather than pretending.
4. **Subtitles, plugins and fallback** are out of scope by decision, with seams in the contracts only — they
   are factors in the design, not behaviour of it.
5. **Exactly one video and one audio stream** are played: the first of each. Track selection is not wired to
   the pipeline yet, and a second track of the same kind is skipped by the reader.

## Appendix A — the checklist for any change to this pipeline

1. Which thread's wait does this change add, remove or lengthen? If none, it is not an orchestration change.
2. Does any thread now wait for something another thread must do? That is the forbidden shape — no wait in
   this pipeline may depend on another stage's progress.
3. Does the change let a producer drop media? If yes, it is in the wrong layer: only the presentation thread
   may, and only under the best-effort rule.
4. Does anything new consult the clock? Only presentation and the audio sender may, and the audio sender only
   to keep it honest.
5. Does anything new believe it knows where playback is? Only the epoch does, and media must carry the epoch it
   was produced for.
6. What does the change do on a seek? On a pause? On the end of the file? A wait with no exit for any of the
   three is a defect, even if it never fires in the happy path.
7. What does the change do when the device stops? The correct answer is "waits, and is reported" — anything
   else is a special case the design rejected.
8. Which invariant in §10 does this touch, and which test pins it?
9. If a user reports a freeze, will this change make the dump easier or harder to read against §12?
10. If the change is in the queue policies, re-read §3's table and §11 first: the policies are different on
    purpose, and none of them is an oversight.

## Appendix B — where each concept lives

| Concept | Where |
|---|---|
| The orchestration, threads, state machine, starvation, device watch | `PlaybackPipeline` |
| "No maximum", and why | `PacketQueue`, `AudioQueue` |
| "Wait for the picture, keep everything" | `FrameQueue` |
| Epoch discipline and what a seek uses to interrupt | `EpochGate` |
| When it is too late to show a frame | `ISynchronizer` (default: 2 ms early tolerance, 40 ms late) |
| The clock contract, and the wall-clock fallback | `IPlaybackClock`, `StopwatchClock` |
| Device position as the source of time | the audio renderer's clock |
