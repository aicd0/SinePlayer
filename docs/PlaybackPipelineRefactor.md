# Pipeline refactor: waits that always end

Status: **implemented**. Supersedes the orchestration built in S8a–S8n of
[MediaEngineImplementationPlan.md](./MediaEngineImplementationPlan.md); everything below the orchestration
(contracts, FFmpeg/WASAPI/D3D11 backends, registry, adapter, Lab) is kept.

The design itself is stated, without code and with the rules a future change must not break, in
[PlaybackPipeline.md](./PlaybackPipeline.md); this document is the review record that produced it, and §3
below is the same rule set in the form it was agreed.

The first version of this document proposed a different answer — a wall-clock timeline that the device merely
steers, so that playback would keep running against a device that had stopped. That was rejected, and rightly:
the device *is* the clock of this engine, a device that stops is a playback that waits, and the honest fix is
to make that wait end rather than to invent a second source of time. What is left is a smaller claim, and it is
the one the code now makes:

> **Every wait in the pipeline ends, and nothing a thread waits for is another thread's progress.**

§3 is the rule set, §4 why it cannot close a circle, §5 what was built and how it is verified.

---

## 1. What the last dump says

| # | Thread | Where it is | What it means |
|---|---|---|---|
| 1 | reader | `PacketQueue.TryEnqueue` | blocked: the video packet queue is full |
| 2 | audio | `WaitForAudioRoom` | the device is not giving buffer room back |
| 3 | presentation | `FrameQueue.TryDequeue` | the frame queue is empty: nothing to show |
| 4 | video decoding | `WaitForDecodeWindow` | paced by the clock, and the clock is not moving |

Threads 3 and 4 are where they are *supposed* to be if the clock has stopped: presentation follows the clock,
and decoding is paced by it. Thread 1 follows from 4 — a decoder that is parked drains no packets. Thread 2 is
the cause, and everything else is a consequence of it.

So this is not the old deadlock this time. **It is the design working as designed, against a device that stopped
reporting progress.** Three of four stages take their liveness from that device, so when it stops, the pipeline
stops — and it stops *silently*, because the three pieces of code that decide what to do about a stalled device
are in different places and cannot see each other:

- `WaitForAudioRoom` nudges the stream once per call and fails only if it is still stuck after that. A nudge
  that clears the padding for a moment lets the block through, and the next block starts a fresh call with
  `nudged = false` — an unbounded nudge loop that never reaches `Fail` and reports nothing.
- `NoticeIfTheClockHasStopped` runs at the top of the audio thread's loop, so it cannot run at all while that
  thread is inside the wait above. It also counts any wobble in the reported position as movement.
- The decoding gate treats "the clock has not moved" as "wait", with no bound and no report.

I have to own the last part in particular: **pacing decoding by the clock (S8n) added a fourth stage whose
liveness comes from the device, and it made this failure worse than it was.** Before it, a device stall froze
the picture but the reader kept running and the sound kept flowing. Now the sound stops too.

## 2. Why this keeps happening

Six reports, one shape. Each line is "a stage could not make progress, and the reason it could not was upstream
of it":

| Report | Stage that stopped | What it was waiting for |
|---|---|---|
| `+30 s` froze in `lock (_mediaLock)` | the seek | a queue the seek had to empty (S8f) |
| After a seek, the picture stuck ~1 in 4 | presentation | a clock anchored to the wrong position (S8g) |
| Video and audio stopped after a few seconds | the reader | a frame queue that could not drain (S8j) |
| After a seek: picture stuck, sound fine | presentation | a clock that was not moving (S8l) |
| Picture stuck, sound breaking up | the reader | a stall guard shedding 5 frames/s (S8m) |
| **Picture and sound stop, no message** | decoding, presentation | **a device that stopped reporting (S8n)** |

Every fix was a new wait, timeout, nudge or guard *inside the same topology*: four threads, five blocking
waits, one source of time that an external device owns. That topology — not any individual bug — is what makes
this unfixable by patching. Concretely:

- **P1 — the device is the only source of time.** The clock is a WASAPI stream position. Everything that has to
  happen "now" is derived from it, so a device that stops reporting stops a pipeline that has nothing else to
  measure time with. No fallback exists.
- **P2 — a stage's liveness is another stage's progress.** Blocking queues make every stall bidirectional: the
  reader waits for the decoder, the decoder waits for room, presentation waits for the clock, the clock waits
  for the device, the device is fed by the reader. With one external dependency in that loop, the loop can
  always be closed by something outside the engine.
- **P3 — no single owner of "we are not making progress".** There are eight independent timeouts (20 ms, 50 ms,
  500 ms, 1 s, 2 s, and three ad-hoc nudges/notices) with three different policies (continue, warn, fail). None
  can see the others' evidence, which is why a total stop can produce no message at all.
- **P4 — the reader is shared between the sound and the picture.** Video back-pressure stops audio production
  (S8h's coupling, still there and only hidden behind a 2 s audio queue).
- **P5 — invariants live in comments.** Single-ownership, epoch discipline and lock ordering are documented but
  not enforced by types, so each new change can break one without the compiler noticing.

How each is answered, because two of them are deliberately *not* fixed:

| # | Answer |
|---|---|
| P1 | **Accepted, and made visible.** The device is the clock, and it stays the clock: a device that stops is time that has stopped, and inventing a second source of time was considered and rejected. What changed is that the stop is a *named, reversible wait* (`The audio device is not moving.`, `IsStarving`) rather than a silent freeze — and that nothing else in the pipeline takes its liveness from the device |
| P2 | **Removed at the source.** The reader no longer waits for a queue, so a stopped decoder cannot stop the sound; the decoder no longer waits for a clock, so a stopped device cannot stop decoding; and presentation is the only thread that waits on time |
| P3 | **One vocabulary, two flags.** "We are waiting" is `IsStarving`/`StarvedFor` and the Lab prints it; "the device is not moving" is one warning with one owner (`WatchDeviceProgress`); the eight timeouts and three nudges are gone |
| P4 | **Removed.** The reader is paced by its own forward reading, not by room in a queue, so video back-pressure cannot reach the sound at all — this was the coupling under several of the six reports |
| P5 | **Partly addressed, and said so rather than implied.** Every wait is now interruptible and epoch-checked and the class remarks state the rules, but the invariants are still comments rather than types; the one wait that remains another component's is recorded in §3.5 |

> **Superseded in part — S11o.** The stall line named in P1 and P3 is gone: the watchdog that raised it was
> removed, because a device that is late and a device that is broken report the same frozen position and the line
> was therefore a guess with a threshold behind it. The design's answer to a stalled device is now to wait and say
> nothing (§9 and §14 of [PlaybackPipeline.md](./PlaybackPipeline.md)). Everything else in this table still holds.

## 3. The design: five rules

**Every stage waits only for what it alone owns, and every wait it takes ends without another stage having to
make progress.** That is the whole design; the table is it, and the rest of this section is the reasoning behind
each line.

| # | Thread | Waits for | What ends the wait | What happens if it never ends |
|---|---|---|---|---|
| 1 | Reader | The file | A packet is read | The file is not delivering: the one stall that belongs to IO and not to the pipeline |
| 2 | Reader | *Nothing else* — it idles while both queues hold their minimums | Either queue falling below half of it | It keeps idling and re-checking; there is no maximum to block on |
| 3 | Video decoding | Room in the frame queue | The picture taking a frame; a seek moving the epoch on | The picture has stopped draining — and a seek still releases it |
| 4 | Presentation | The clock reaching a frame's moment | Time passing | A clock that is not moving (rule 5) |
| 5 | Audio sender | Room in the device's buffer | The device playing; a seek moving the epoch on | A device that is not consuming (rule 5) |

> **Only presentation waits on the clock. Only the reader waits on the file. Nothing waits on the reader.**

### 3.1 The reader never drops, and never blocks on a stage

The reader is the one cursor the file has, so it reads both streams, and everything the sound needs comes from
it. The two queues in front of it used to be bounded, and a full video packet queue stopped the reader — a
stopped reader being a stopped sound. **Neither queue has a maximum now.** What paces the reader is its own
forward reading: fill both queues to their minimums (500 ms of audio, 64 video packets), idle, and read again
when either falls below half of its minimum. The cost is bounded by playback itself, because the minimums
advance only as fast as the file is played.

No cap is implemented, and none is needed: the read-ahead *is* the bound, and the difference from a queue
maximum is that it is the file position applying back-pressure rather than a busy queue. There is no drop path
in the reader at all — the one way a packet is not handed on is a seek that moved the epoch while the packet was
being read, which is work for a position nobody wants.

### 3.2 The decoder never drops, and never consults the clock

The frame queue is the only queue that stops its producer, and by design: its consumer is the picture, and the
picture is the one thing that follows the clock. A decoder waiting for room is a decoder that has produced
everything the picture can hold — which is a *visible* wait, and the alternative is worse. The queue used to
discard its oldest frame to make room, which meant a decoder that could not keep up threw away the very frames
the picture was about to need, and the freeze that followed looked like a rendering fault rather than a slow
decoder.

Nothing is discarded now. The decoder stops at the queue's capacity (8 frames) and starts again when the
picture takes one, and the ending that is always available is the epoch: a seek releases a decoder waiting for
room, and the frame it was carrying is let go of rather than decoded into a queue being emptied.

The decoder does not look at the clock. Showing a frame at its moment is the presentation thread's business, and
a decoder waiting on the clock is how a stalled device used to stop the sound as well as the picture.

### 3.3 The presentation thread is the only one that loses frames, and only the ones whose moment has gone

The picture is the follower, so it is the one thread allowed to give up on a frame:

- **It never shows a frame early.** If the frame's moment has not come, it waits for it — a millisecond at a
  time, interruptible — because that wait is what makes the picture follow the sound.
- **It drops a frame only when a closer one is already waiting.** The frame it holds is due; the queue is
  peeked, and if what is behind it belongs closer to the clock, the held frame is released and counted as
  dropped. `TryPeek` is why this is a decision rather than a guess: the pipe used to drop a frame on the
  synchronizer's opinion alone, which threw away the newest frame there was when the queue was empty, and made
  a slow decoder look like a stopped one.
- **When the picture cannot catch up at all it holds the clock back.** If the newest frame the decoder has
  produced is more than 200 ms behind the clock, the clock is stopped — playback waits for its own picture —
  and it starts again when the gap closes to within 100 ms. A wide threshold to stop and a narrow one to start,
  so a decoder hovering at the boundary cannot make playback stutter.
- **The threshold is a lag, not a gap between frames.** A gap only exists when the picture has stopped
  arriving, which a uniformly slow decoder never produces; a lag exists as soon as the decoder falls behind,
  which is exactly the case that needs the sound to wait. This was the design's one correction to itself: the
  original rule would not have ended the stall it was written for.
- **Waiting costs no frames.** While the clock is held, the decoder keeps producing and the picture keeps
  showing; frames are only ever lost to the drop rule above, which is why `DroppedFrameCount` climbing means
  the picture could not be shown in step — a decoder or a renderer too slow — and not merely that something
  waited.

### 3.4 The audio sender keeps the clock honest

The audio thread hands blocks over one at a time and never drops any. Two things can be true while it works:

- **The audio queue is empty.** There is nothing to play, so the clock is stopped — time may not pass while the
  sound is missing, or the picture would run ahead of silence. The next dequeue starts it again. After the end
  of the file an empty queue is the sound playing out rather than the sound falling behind, so the stop does not
  apply there.
- **The device has no room.** The block waits, and the wait ends when the device takes what it holds, when a
  seek moves the position on, or when playback is stopped or paused. There is no deadline and no retry: a
  device that is not consuming is a device whose clock is not moving, which the whole pipeline already
  follows.

The block that was being carried through a seek is let go of rather than played, and the device is flushed if
the submit raced the seek — what is heard must never belong to where playback came from.

### 3.5 A device that stops is a pause, not a fault

The device owns the clock, so a device that stops taking audio is time that has stopped: presentation waits for
a moment that never arrives, the audio thread waits for room, and the reader idles once both queues hold their
minimums. Nothing fails, nothing is dropped and nothing needs a special case — which is the point, because the
previous design's special case (`WaitForAudioRoom`'s nudge, the stall guard, the decode gate) was itself the
chief source of stalls.

What the design does owe the user is the *reason* — and since S11o the pipeline does not pretend to have one.
A watchdog that named the device (`The audio device is not moving.`) was removed: the engine cannot tell a slow
device from a broken one, so the line was a guess with a threshold behind it, and the freeze is now silent —
`Position` stopped while the state is `Playing` and nothing starving (§9 and §14 of
[PlaybackPipeline.md](./PlaybackPipeline.md)). No state change, no error, no interruption either way.

**What this does not cover, stated rather than implied:** a presentation thread stuck *inside* the renderer — a
GPU hang — grows the frame queue until the decoder stops, but it produces no lag against the clock, so it is
not detected as starvation; the pipeline waits there until the renderer returns. It is the one wait in the
engine whose end is another component's, and it is called out here so it is not mistaken for an oversight.

## 4. Why a circle can no longer form

The old failures were all one shape: a cycle of waits. Each edge of that cycle is now either gone or one-way.

| The old edge | What it is now |
|---|---|
| Presentation waits for the clock; the clock waits for the device | Still true, and allowed to be: the device is the outside world, and a stopped device is a wait that nothing in the engine names or breaks (§3.5) |
| Presentation waits for a frame; the decoder waits for room; the reader waits for the packet queue | Broken twice over: the reader never waits for a queue (unbounded, self-paced), so a stopped decoder cannot stop the reader; and a seek releases both waits through the epoch |
| The reader stops ⇒ no audio ⇒ the clock never moves | Broken by the reader's own rule: it blocks only on the file and idles only when both queues are already full, so audio production never waits on the picture |
| The decoder waits for the clock (So it cannot run while the device is stalled) | Deleted: the decoder waits for frame-queue room only, and the presentation thread is the only thing that consults the clock |
| A stalled pipeline reports nothing | The two starvation flags are states the pipeline enters deliberately, the Lab prints them, and a device that has stopped is named by warning |

The remaining waits are the five in §3's table, and each one belongs to the thread that owns it: the reader
waits for the file, the decoder for the picture, the presentation thread for time, the audio thread for the
device. **No thread waits for another thread's progress**, so no cycle of waits exists to be entered — the
topology has four one-way hand-offs (packets, frames, buffers, and the clock) and no way back.

The seek is the one operation that must interrupt others, and it does so by making the epoch move rather than
by waiting for them: every wait in the pipeline checks the epoch and releases, every queue is cleared, and the
flushers run so a producer that was mid-hand-off gives up rather than blocking the seek.

## 5. What was implemented, and how it is verified

| File | Change |
|---|---|
| `PlaybackPipeline.cs` | The rule set above: the reader's watermark loop, the decoder's room-only wait, the presentation thread's best-effort drop, both starvation halts with their thresholds, `WaitForAudioRoom` without a deadline, `WatchDeviceProgress` and its warning, and the exit paths every wait has |
| `PacketQueue.cs`, `AudioQueue.cs` | Unbounded, never block their producer, still epoch-checked and still completing at the end of the file |
| `FrameQueue.cs` | Blocks at capacity, keeps every frame, gains `TryPeek`; the eviction path is deleted |
| `PlaybackPipelineTests.cs` | Three tests of the old policy are replaced (`WaitsForADeviceThatNeverTakesAudio`, `WaitsForADeviceThatIsNotMovingAndThenCarriesOn`, `HoldsTheClockBackWhenTheVideoCannotCatchUp`) and one is added for the drop rule (`DropsOnlyTheFramesWhoseMomentHasGone`) |
| Queue tests | Rewritten for the new contracts: no eviction, no blocking enqueue, the reader is never stopped, the epoch is still what a seek is |
| `PlaybackLabPage.xaml.cs` | Prints the stage playback is waiting for, in the place of the old stalled counter |

Verified by the full suite twice (119/119) for x64, with the solution building clean for x64 and ARM64.

## 6. What is kept

- All of `Playback/Abstractions` (contracts, registry, epoch gate), the FFmpeg, WASAPI and Direct3D11 backends,
  `PlaybackEngineAdapter`, the tests below the pipeline, and the Lab.
- `PlayerPageViewModel` — untouched by this refactor, as ever.
- S10–S15 (media info, track selection, hardware decode, renderer switching, filters) sit on top of the
  contracts and are unaffected; S12 in particular becomes more useful once a slow decoder degrades rather than
  stops playback.

## 7. What is replaced

`PlaybackPipeline`'s orchestration (threads, waits and guards) and the three queues' policies as they are
described in §3. The class itself, the contracts and the backends stay exactly where they are.

## Appendix A — why four threads, and why fewer is worse

The refactor does not change how many threads there are; it changes what each is allowed to wait for. That
distinction is worth stating because the count is easy to read as complexity to be removed, and because
FFmpeg's own threads look like they should reduce it. They do not.

**FFmpeg's threads are helpers inside one blocking call.** `avcodec_send_packet` waits until a codec thread is
free and `avcodec_receive_frame` waits until a frame is ready; there is no callback and no future, so a thread
we own still has to drive the decoder and still has to be able to block inside it. Those threads give
throughput and nothing else: they know nothing about our clock, our device, or which frames have become too
late to show. They are also not free — frame threading holds output back by roughly one frame per thread, which
is the bug that made a seek near the end of a file produce no frames at all until the count was bounded.

The four threads exist because four things block on four different things, and each block is long:

| Thread | Waits on | What breaks if it shares a thread with the row above |
|---|---|---|
| Reader (and audio decode) | the file | — |
| Video decoding | the codec's threads, and room in the frame queue | Reading stops for the whole decode, and the packets behind a video frame are audio as much as video: the sound starves (this was the 4K60 report) |
| Presentation | **time** — the clock reaching a frame | Decoding stops for a frame interval, so there is no look-ahead and a slow keyframe becomes a visible stall |
| Audio sender | **the device** — room in its buffer | Reading stops for a whole buffer (~200 ms), so the picture stalls while the sound plays (this was the S8c report) |

The last two wait on something *outside* the engine — a wall-clock moment, and a device — and no amount of codec
threading absorbs either. Getting to three threads would need a device that never blocks (it does) or a decoder
that can be paused mid-call (it cannot).

The one merge the design already makes on purpose is audio *decoding*: it runs on the reader's thread, because
it is cheap and every hand-off is another chance for the sound to be late. Audio *submission* is separate
because it waits on the device. mpv, VLC and ffplay settle on the same four roles.

So the thread count is not the design. It is one thread owning one stage's state, and the queues as the only
hand-off — the waits, not the threads, are what this plan changed.

## Appendix B — the honest risk

The rules change behaviour under failure, and behaviour under failure is what the tests only partly cover: they
run against stand-in renderers and a real device, not against a device that misbehaves. Three of them now do
misbehave on purpose — a device that never takes audio, a device that takes audio and never moves, and a
decoder two and a half times too slow — and each is asserted to end in a state rather than a wait. What is
still unexercised is a *renderer* that stops returning, which §3.5 names as the one wait whose end is not the
pipeline's to control. When that turns into a report, the test to write first is the one where the renderer
stalls for a second and then returns: the picture must resume without a seek.
