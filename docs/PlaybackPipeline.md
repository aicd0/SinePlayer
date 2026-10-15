# PlaybackPipeline — the design, and the rules a change must not break

**Status:** implemented and verified (S8o, with the orchestration queue added in S9f). **Audience:** anyone
changing the orchestration. This is the conceptual contract — deliberately no code — and it is written to be
the thing a future change is checked against rather than a description of whatever the code happens to do
today.

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
- owns **the queue**: everything that decides anything is an item of it, and it is shared by every pipeline in
  the process (§2.1);
- owns **the clock**: for a file with sound, the audio device's position is playback time; for a silent file,
  a wall clock is;
- owns the **epoch**: a counter that says which "where playback is" a piece of work belongs to;
- reports what is happening through **three events, and nothing else**: a state change carries the state and
  the reason it failed, a warning is one line about something that did not stop playback, and a seek that has
  been carried out is announced so a position can be re-read. None of them is a value to be read back — see
  §13.

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

Seven things to know about the thread layout, because each is load-bearing:

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
6. **Nothing here polls.** A thread with nothing to do waits on a change — the state, the position, the
   presentation surface, or a stop — and every one of those waits has a timeout as its backstop, so the
   property in (5) still holds even if a signal is somehow missed. Two kinds of wait have to be told apart
   when reading this: the backstops (the waits that expect a signal, where a fire means a missed signal and is
   **currently invisible**, recorded as follow-up work) and the waits where the timeout *is* the mechanism,
   because no signal exists to rely on — the reader's read-ahead check, room in the device's buffer, and the
   end-of-file drain. The frame queue's dequeue is neither: it is woken by the queue itself whenever a frame
   arrives.
7. **A signal must never be missed, and the pattern that guarantees it is the order of the call:** a thread
   reads the change stamp *before* it decides there is nothing to do, then waits. A change arriving while it
   decides is then seen by the stamp check inside the wait, and a change arriving after is a pulse with a
   waiter already in place. Getting this order wrong does not break correctness — the timeout still ends the
   wait — it breaks *promptness*: a resume that should be instant becomes a quarter of a second late, which
   is exactly what the test `WakesForEachSurfaceChangeWhilePausedRatherThanWaitingForTheBackstop` pins.

### 2.1 One queue decides everything

The four threads move media and decide nothing. **Everything that decides anything is an item of one queue,
and that queue is shared by every pipeline in the process.** The rules are few and each of them removes a
whole class of questions:

- **A call from outside is posted, not run where it was made.** `Play`, `Pause`, `Seek` and `Stop` return as
  soon as the job is queued, and the order the calls were made in is the order they happen in. What a caller
  gets back is the task of its own job, carrying whether that job could be carried out; it is free to drop it,
  and the state machine is unaffected either way. Nobody at this layer waits for anything, which is what keeps
  a caller's thread out of the queue — a wait there stalls behind whatever is already queued, including
  another pipeline's work.
- **A command reports; playback fails.** A request that cannot be carried out — playing with nothing open,
  seeking a source that will not move — is answered by the task it handed back and changes nothing else: no
  state, no event, no error. `Error` is reserved for the other thing entirely, which is playback running into
  something nobody asked for and nothing being able to continue (a decoder that dies, a device that stops).
  Folding the two together is how a pipeline ends up dead because somebody dragged a slider.
- **What playback did is announced, and the events are the shared account.** A state change carries the state
  and the error (`StateChanged`), and something that did not stop playback is one line (`WarningRaised`). A
  command's own outcome is the one thing that is *not* an event: it belongs to the caller that asked, which is
  also why a seek — a command with no state change to announce — needs no event of its own.
- **`Dispose` is the one call that waits.** A disposed pipeline must not still hold the file open — whoever
  opens it next would not be able to — so teardown is posted and waited for, with a bound
  (`TeardownWaitMilliseconds`) because a teardown that cannot finish is a broken pipeline, not a slow one.
- **The threads that move media only post.** The end of the file, a failure, a stage with nothing to play, a
  warning about the device: each is handed to the queue as an item of its own. They never change state, never
  raise an event, and never wait for an item to be done — so "the state machine is only touched on the queue"
  holds by construction rather than by care.
- **The state is therefore a volatile field, not a locked one.** There is exactly one writer, and a reader of
  `State`, `Error` or `Position` wants the value it sees now rather than a consistent picture of everything.
- **Nothing is inlined "because the caller is already on the queue".** The call sites know where they are —
  the open calls the seek's core method directly, the audio thread posts — so there is no need for a flag that
  says which thread is where, and no second path through the state machine to reason about.
- **A queue item is short.** The cost of one shared queue is that a long item delays everything queued behind
  it, including another pipeline's calls. Items that could be long — creating a device, an open, a seek's file
  move — are exactly why the work that can block (the move, a decode, a device flush) is done where the object
  it touches is owned: the queue asks, the owner acts, and nothing waits for it. That is also why the media
  threads post small jobs rather than doing work on the caller's behalf.

### 2.2 Who owns what

State here is owned, not shared by default, and which kind a field is should be readable from where it is
declared. There are only a few kinds:

| Kind | Written by | Read by | What makes it safe |
|---|---|---|---|
| **Fixed at construction** (the factories, the wake lock, the epoch gate, and the decode lock, named for the pair of users it is between) | the constructor | anyone | nothing is ever assigned again; the lock carries the name of what it protects |
| **Open state** — the source, the decoders, the renderer, the three queues, the four threads, the streams, the info, the clock object | the orchestration queue, **only while no media thread is running**: before they start, or after they are joined | the media threads and the host | the invariant above, which is why there is no lock around any of it |
| **One owner, no lock** — the media cursor (`IMediaSource`) and the audio decoder that rides on it belong to the reader, which reads, moves, flushes and drains them and nothing else touches; the video decoder belongs to the decode thread, which flushes it when it sees the epoch move | the owner, and only the owner | the owner | nothing: a lock around a cursor two threads move was the thing this replaced |
| **The queue's own decisions** — the state and its error, `_disposed`, `_stopRequested`, `_sourceEnded`, the two starvation flags, the anchor flag, the published seek position, and the ending (which position is playing out and which stages have said they have nothing left) | the queue, and only the queue | the queue, except where a media thread needs the value *now* | one writer, no compound update, nothing shared with a second field: `volatile` where a media thread reads it, and nothing at all where only the queue does. The ending is filled in by messages from the stages, not read by them |
| **Thread-owned** — the audio thread's device watch | that thread, and only that thread | that thread | a field only because the state must outlive a wait; everything else a thread needs is a local of its loop. A seek is taken up by the watch noticing the epoch move, not by the queue resetting it |
| **Published by a stage** — the starvation ask each stage makes, and the three things a stage says about the end of a file (the decoder has produced its tail, the picture has shown everything, the sound has played out) | the stage writes the ask; the queue owns what the ask becomes | the queue, and the stage itself reading back *what it asked for* | `volatile` where a media thread reads, and no copy kept by the stage: what a seek makes meaningless is retracted where it is owned. The end-of-file words are messages rather than flags, which is why a stage never reads another stage's |
| **Counters and hand-offs** — presented/dropped frames, the tick totals, the picture timestamp, the decoded tail, the anchor flag | any thread | anyone, including the host | `Interlocked` |
| **The clock object** | the queue (start, stop, set position), and the first media of a position (the anchor) | every thread reads its position | the clock implementation must be safe for that on its own; readers never lock around it |

Five questions, in this order, decide where a piece of state goes. The first that can be answered yes is the
answer:

1. **Is it the work-state of one thread's loop?** Make it a local of that loop. (The reader's filling latch
   and the epoch whose end it has reported were both fields once; neither had any business being one.)
2. **Does one thread own the fact while others only need to see it?** One writer, readers read it — that is
   all `volatile` means, and it is *sufficient* exactly because there is one writer, no compound update, and
   no invariant tying it to another field. If those three cannot be said out loud, this is not the answer.
3. **Is it a fact about a position rather than about a thread?** Let the piece of shared state that already
   carries positions carry it — the epoch — and let each thread re-derive its own answer when it sees the
   epoch move. A flag that another thread has to *retract* is the smell this replaces.
4. **Do several threads write it?** Then it belongs to all of them: `Interlocked` for a counter or a
   one-time claim, a lock for a region — named for the two users it is between, which have to be exactly the
   users it has.
5. **Is it written only while the threads are not running?** Then it needs nothing at all, and what deserves
   saying is *when* that is: open state, written on the queue before the media threads start and after they
   have been joined.

**`volatile` is never an answer on its own.** It is the last inch of question 2, in a place where the
ownership was already decided. A flag one thread must retract for another is the shape to look for and
remove — the stage re-derives, the watch re-seeds, the queue keeps its own copies out of it — because the
retraction is a write with no ordering behind it, and no keyword fixes that.

The same questions apply to *where a value is compared*, not just where it lives. The epoch is the pipeline's
fact about the position; the queues carry it as an opaque tag and compare nothing, every producer checks it
before handing work over (handing over is what books the work as progress), and every consumer checks it
before using what it took. A queue that filtered on it would be a fourth place deciding what a position
means, with no way to see the other three.

## 3. The three hand-offs

There are exactly three queues, and each has a different policy because each is there for a different reason.

| | Packet queue | Audio queue | Frame queue |
|---|---|---|---|
| **Between** | Reader → video decode | Reader → audio sender | Video decode → presentation |
| **Holds** | encoded video packets | decoded audio buffers | decoded video frames |
| **Maximum** | none | none | 8 frames |
| **A full queue means** | — | — | the picture is behind, so the decoder stops |
| **Producer waits?** | never | never | yes, until the picture takes a frame (or the queue is closed, which is what a stop does) |
| **Consumer waits?** | up to a short poll for a packet | up to a short poll for a buffer | up to a short poll for a frame |
| **On a seek** | nothing happens to the queue; the media it holds carries the old epoch and the consumer drops it | same | same |
| **At end of file** | nothing happens to the queue: the pipeline says the file has run out (`_sourceEnded`, set by the reader's report, after it has queued the sound's tail itself), the decoder drains what it holds and tells the queue so, and the reader *survives* so a replay can read again | nothing special — an empty queue after the end is the sound playing out, and it is the sound that says it has | nothing special — the remaining frames play out, and it is the picture that says it has |

The three are one class, `MediaQueue<T>`, configured three ways: no limit, no limit but weighed in samples,
and a limit. The policies differ *on purpose* and each is one argument at its construction site in
`PlaybackPipeline`; the queue itself knows nothing about epochs — it carries a tag, and what the tag means is
decided by the producer that writes it and the consumer that reads it — and it knows nothing about ends
either: the only two things it can be told are "wait for room" and "this is closed", and the second is what a
stop does. That is why a seek and the end of a file do nothing to any queue: there is nothing in one for
either to fix.

**Why two of the three have no maximum.** The reader is the single cursor of an interleaved file: what it has
not read yet is *both* streams. A bounded packet queue therefore means "the picture being slow stops the
sound" — the coupling behind several of the historical failures. The reader paces itself instead (§4.1), and
what bounds the read-ahead in practice is playback itself: the minimums advance only as fast as the file is
played.

**Why the frame queue does have one.** Its consumer is the picture, and the picture is the one stage that
follows the clock. A decoder stopped here is stopped by the thing it feeds, and starts again the moment the
picture takes a frame. It is also the only place where decoded — i.e. largest — data can pile up. Its wait
used to be released by a seek as well, so that a producer waiting to hand over work for a position nobody
wants would not be held by the picture; it is not any more, for the reason the seek rule gives: freeing the
decoder while the picture is stuck helps nothing, since its work would be dropped. What *does* have to end
that wait is a stop, and closing the queue is what does it — before the joins (§7).

## 4. The five rules

**Every thread waits only for something it alone depends on, and every wait it takes ends without another
stage having to make progress.** These are the rules; §4.1–§4.5 are why each is written the way it is.

| # | Thread | Waits for | Ended by | If it never ends |
|---|---|---|---|---|
| 1 | Reader | the file | a packet arriving | the file is the outside world; this is the one legitimate IO stall |
| 2 | Reader | *nothing else* — it idles while both queues hold their minimums | either queue falling below half of its minimum | it keeps idling and re-checking |
| 3 | Video decode | room in the frame queue | the picture taking a frame; the queue being closed (a stop) | the picture is not draining — which is the picture's problem, and the one wait that ends with the consumer |
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
- **A paused picture still follows the window.** While nothing is being presented, the renderer is offered any
  change the host has reported to the surface — a new size, or a new display scale — and draws the last frame
  again from what it kept, so a resize is not left for the host to stretch into. It is deliberately the
  renderer's job rather than the pipeline's: the renderer is the one that hears the surface change, and the
  offer has to be harmless when nothing changed. Without it, a paused video keeps the size it had when it was
  paused and is stretched whenever the window changes.
- **The display is a consumer, not a clock.** Handing the frame to the compositor never waits for a vertical
  blank: the picture's pacing is the clock (§5, rule 4), and a blank wait inside this thread is a second
  pacing source the pipeline cannot account for — it eats the thread's whole interval, is measured as the
  renderer's cost, and at a frame rate equal to the display's leaves no slack at all, so lateness ratchets up
  until the drop rule starts firing (measured: a steady tenth of frames dropped with a 70 ms lag on a machine
  doing the frame's work in 0.38 ms — see §11). The price of not waiting is that a frame can be presented and
  then superseded before the display takes it, so that count is read back from the swap chain and published
  as `DisplayedFrameCount` (§13): drops that happen after the picture has left the pipeline are invisible to
  every counter inside it, and a price that cannot be measured cannot be argued about.

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
rather than by waiting for them** — and by **asking the cursor's owner to move it rather than reaching in**.
In order, on the queue's side:

1. Refuse it outright if the source cannot be moved at all (`CanSeek`) — before anything is paused, so the
   refusal costs nothing. This is the only refusal that can be answered here, because whether a source *can*
   be moved is fixed when it is opened; whether it *will* is only known by trying.
2. Pause (if playing), so the state machine says nothing is playing while the position is rewritten. The
   pause is for the duration of the request: a seek that works resumes, and one that is refused resumes too.
3. **Publish the position, then advance the epoch.** The position is written first so that a reader which
   sees the new epoch is guaranteed to see the position that goes with it; a second request before the reader
   acts overwrites both, which is what makes the latest request the one carried out.
4. Withdraw what the epoch has just made false: reopen the packet queue (the end of the stream was a
   statement about the old position), reset the decoded tail, clear both starvation flags and the
   "source ended" flag, set the clock to the requested position, re-arm the anchor, and ask the video decode
   thread to flush its decoder. Nothing is cleared: what the queues hold belongs to where playback is
   leaving, and the stages that take it are the ones that can tell.
5. Resume if it was playing, or leave `Ended` for `Paused` when the seek came from the end of the file.
6. Answer the caller with the result of the request — accepted, or the predictable refusal from step 1.

Then, on the reader's side, where the cursor lives:

1. Notice the epoch has moved past the one this thread has carried out, and take the position published
   with it. If a newer request arrived in between, take that one instead: the newest request is the one that
   matters.
2. Move the source and flush the audio decoder — the two objects the reader owns, which no other thread
   touches at all now, which is why there is no lock around either.
3. From that point on, stamp what it reads with the epoch it has carried out — never with the epoch the gate
   happens to hold. A packet read from the old cursor must keep the old stamp, or every stage downstream
   would take it for new media and play it.
4. A source that refuses the move is reported as one line and nothing else changes: nothing was flushed,
   the position has not moved, and playback carries on from where it was.

So a refusal is answered twice, in the two places that can know about it: the predictable one in the
caller's result, the attempted one as a line. Nothing waits for the reader, and the reader waits for nothing:
the only thing the two sides share is the epoch, which is a counter, and the published position, which is
written before it.

And on the audio thread's side, where the device lives:

1. Notice, at the top of its loop, that the epoch has moved — the earliest moment this thread can act,
   whether it is playing or paused, and whether or not the reader has produced anything yet.
2. **Let the device go of what it holds.** What the device still holds was produced for the position
   playback has left; a seek that leaves it there plays a moment of the old position over the start of the
   new one, and — because the clock counts what the device plays — leaves a device that was never reset
   still counting from the old position's travel.
3. Take up the first buffer of the new epoch as the anchor, as before.

The flush in step 2 cannot be skipped because of step 3: the anchor repositions the clock, and a repositioned
clock counts from wherever the device is at that moment. A clock repositioned over a device that was never
reset is a clock that jumps forward by however long the device has been playing — and the picture, following
the clock, races to catch up with it. That was the symptom: "the video looks like it is speeding up after a
seek."

The clock therefore never *assumes* a reset device either, wherever it is repositioned from (the queue's
seek, the anchor, the rate change): it takes the device's current position as its zero point. The reset
belongs to the thread that owns the device, and the clock is read by threads that have no idea whether that
reset has happened yet — so the only safe reading is the one that does not care.

What each waiter does when the epoch moves:

| Waiter | Reaction |
|---|---|
| Reader | it is the one that performs the move: it takes up the request before reading anything else, moves the cursor, flushes its decoder, and stamps what it reads from then on with the epoch it carried out |
| Video decode | a packet queued under the old epoch is dropped without decoding; the decoder is flushed (by this thread, which owns it) before anything from the new epoch is decoded |
| Frame queue producer | a wait for room is released by the close a stop performs, not by the seek: releasing it for a seek helps nothing when the picture is stuck, and its work would be dropped |
| Presentation | the held frame is released; frames queued under the old epoch are invalid by construction |
| Audio sender | a buffer whose epoch is not the current one is given up on before it can anchor the clock; when it sees the epoch move it lets the device go of what it holds (the reset only the device's owner can do); a wait for room that the seek interrupted ends with the buffer let go of, and a submit that raced the seek is followed by a device flush |
| Audio clock | it does not assume a reset device: wherever it is repositioned from, the device's current position is taken as the clock's zero point, so reading it is safe while the reset is still on its way |

Two details that are easy to get wrong and were both bugs once: the flush of the video decoder must happen
**on the thread that owns it** (a frame-threaded decoder cannot be flushed from outside), and the epoch check
around a submit has to happen **after** the device call as well as before it, because a device that had
stopped consuming can make a submit outlast the seek that interrupted it. A third one joined them: the reader
stamps what it reads with **the epoch it has carried out**, never with the epoch the gate holds, or media read
from the old cursor is labelled as belonging to the new position and every filter downstream is fooled at
once.

## 7. States, pause, and the end of the file

States: `None → Opening → Playing ⇄ Paused → Ended`, with `Error` reachable from anywhere. `Stopped` is
`None` with nothing open. **`Error` means playback ran into something nobody asked for and nothing can carry
on through** — a decoder that died, a reader whose file went away, a stage that failed while it was playing
the tail out. It is never reached because a *command* could not be carried out (I16): that is answered to the
caller and nothing else changes, and an open that fails leaves `None` — nothing open — rather than `Error`.
A device that stops consuming is not on that list, at the end of a file or anywhere else: it is time that
has stopped, which is a wait (§9).

- **Pause** stops time and freezes all four threads where they are; each queue keeps what it holds. Nothing is
  discarded, and a buffer the audio sender was carrying is kept rather than dropped. The one thing that keeps
  moving is the surface: the presentation thread offers the renderer any window resize while nothing is being
  presented (§4.3), because a paused picture still has to fit the window it is in.
- **Play** restarts time through the same single path every state change uses, which consults the starvation
  flags: playback resumes into "playing and not waiting", never into "playing but stopped".
- **A state change is the only thing that starts or stops the clock**, so there is exactly one place where
  "should time be passing" is decided.

**End of file.** The reader's end-of-file is the end of the *packets*, not of the media, and the ending is
**played out by the stages that hold the media and decided by the queue** — the queue is told, never held:

1. The reader drains the audio decoder's tail into the audio queue itself (the decoder is its own) and then
   posts the end of the file, stamped with the position it belongs to. Draining first is what makes the
   statement mean "the packets are all in *and the sound's tail is queued*" — a stage that later says it has
   nothing left can only be talking about media it was given.
2. The queue marks it (`_sourceEnded`) — the one place that statement is held, and what the decode thread
   reads when it finds its queue empty — and arms the ending for that position. Nothing is expected yet and
   nothing is waited for. A stop, or a seek that arrived first, makes the report stale and it is dropped.
3. The video decode thread drains the decoder's delayed frames into the frame queue, once per position, and
   tells the queue: *no more frames from me*. Without that word, "the picture has nothing to show" cannot be
   told from "the tail has not come out yet", and the ending would arrive while frames were still inside the
   decoder.
4. Each consumer says its own piece as it observes it, and repeats it while it stays true: the picture when
   it finds the frame queue empty and is holding nothing, the sound when its queue is empty and the device
   holds nothing. A stage can only be asked about itself, and no stage reads another stage's word — a look
   that predates the tail is honest and says nothing, which is why it only counts after the decoder's word
   and why the frame queue is checked again where it is owned.
5. The queue ends playback when every stage that had media has said it has none, for the position it armed,
   while playback is playing. A pause is respected rather than ended through (the stages say their piece
   again when they are playing); a seek leaves the ending behind, because every report carries the position
   it is about and that position is gone.

This is what keeps the end of a file from blocking anything: the queue every command is posted to stays free
throughout, and the waits a tail-out used to spend there — one for the decoder, one for the picture, one for
the device, each bounded at two seconds — no longer exist. The cost of the old shape was not only the wait: it
also made the ending a *timeout*, so a file whose sound had not been heard was ended anyway (silently cutting
the last of it), and a command posted at the end waited behind it — `AnswersACommandWhileTheSoundHasNotPlayedOut`
is the test for that, and it fails if the wait comes back.

**A stage that cannot finish is a wait, not a timeout.** If the device stops taking audio while the last of the
sound is still inside it, the sound never says it has played out, the ending never comes, and playback sits at
the end of the file — named by the device watch (§9), because a device that stops at the end of a file is the
same wait as one that stops in the middle, and gets the same line. When it moves again, the word arrives and
the file ends. The old bounded waits decided the opposite: they ended the file with its last sound unplayed,
and said nothing about why.

The "source ended" flag matters beyond bookkeeping: it suppresses both starvation halts, because *after the
end an empty queue is the media playing out, not a stage falling behind*. Getting that wrong makes the end of
the file hold the clock back forever. It is read by three threads for three different reasons: the decode
thread reads it to know an empty packet queue means "drain the decoder's tail" rather than "the reader is
behind"; both starvation rules read it because at the end nothing newer will ever arrive to end the wait; and
the device watch reads it as *not* a reason to stay quiet — a device that stops with the last of the sound
inside it at the end of a file is the same wait as one that stops in the middle, and the ending is waiting for
it (§8, §9).

Both the reader and the video decode thread stay alive past the end of the file, because a replay — or a seek
from the end — has to have something to read and decode with.

**Stop and dispose** release what the threads are waiting on first, then join, then release everything else —
and the order is not a preference, it is what makes the joins plain joins: three of the four waits end on
their own (bounded, or woken by the flag), but a decoder waiting for room in a full frame queue is waiting
for *another thread's progress*, and a picture that is stopping will never make room. Closing that queue is
what ends it, so a decoder doing exactly what it should is never left waiting for something that is not
coming. Only then are the threads joined — with no timeout, because a timeout here would only ever be hiding
a wait that was not released — and only then is the device flushed, the decoders and the renderer let go, and
the source closed. `Dispose` waits for the whole of that, on the caller's thread, which is the one call that
waits: after it returns, the file is closed and the threads are gone, which is what whoever is about to
delete, move or reopen it is entitled to assume.

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
  *expected to show progress*: playing, not waiting for a stage, its anchor claimed, and the clock
  running. A position that has not moved for a second under those conditions earns one line —
  `The audio device is not moving.` — and the position moving again earns the pair to it,
  `The audio device is moving again.` No error is raised, no wait is interrupted, and no recovery is attempted.
  The countdown starts at the first look of a playback rather than when the device first fails to move: a device
  that has not had a chance to move yet is not a stalled one. The end of a file is *not* an exception: a device
  that stops with the last of the sound still inside it is holding up the ending, and that is exactly the stall
  worth naming — once the file has been read out, the only thing left that can be waiting for the device is
  the device.
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
| I5 | Nothing waits for a queue that no longer exists, and no wait depends on another stage's progress: a seek is a request the cursor's owner carries out rather than a lock the seek takes | Bounded liveness: every wait has an exit that is not another thread's progress — for the decoder's wait for room, that exit is the close a stop performs, not a seek (freeing it for a seek helps nothing when the picture is stuck) | the seek tests, delivered as "the seek never returned" assertions, and every test that disposes a pipeline with a full frame queue |
| I6 | Media carries the epoch it was produced for, and is dropped rather than replayed when the epoch has moved — checked by the producer before it hands work over and by the consumer before it uses what it took, never by the queue in between | A seek that half-works is worse than one that visibly restarts; and a queue that filtered would be a queue that decides, which is how the epoch gets back into a place with no business knowing it | `SeeksWhilePlayingAndKeepsGoing`, `SeeksWhileTheNextFrameIsStillWaiting`, and `MediaQueueTests` for the tag travelling untouched |
| I7 | An empty queue after the end of the file is played out, never waited for, and the ending is *earned*: the queue ends playback when the picture and the sound have each said they have nothing left, for the position it armed | A timeout in the queue is two faults at once — it blocks the queue every command is posted to, and it ends a file whose sound has not been heard; and a stage that cannot finish must leave a wait that is named, not a file that ends early | `PlaysAnAudioFileThroughTheRenderer`, `PlaysAVideoOnlyFileToTheEnd`, `PlaysAgainAfterTheEndOfTheFile`, `AnswersACommandWhileTheSoundHasNotPlayedOut` (fails if the wait comes back), `EndsWhenTheDeviceHasPlayedOutTheSound` |
| I8 | Losing media happens in exactly one place, and it is counted | A media pipeline that loses frames silently cannot be diagnosed | `DroppedFrameCount` assertions in the drop and device tests |
| I9 | Every wait ends; a stalled device is a wait that ends when the device does, and is named while it lasts | The user must never see a freeze with no reason | `WaitsForADeviceThatNeverTakesAudio`, `WaitsForADeviceThatIsNotMovingAndThenCarriesOn` |
| I10 | The clock starts and stops in exactly one decision, and a state change always re-evaluates it | Two places deciding "is time passing" is how pause, starvation and seek start fighting | `PausesAndResumes`, and every test that asserts `ClockIsRunning` |
| I11 | A surface change is taken up whether or not frames are being presented, and drawing the last frame again never depends on a frame still being alive | A paused video that ignores a resize is stretched by the host, and a redraw that needed the frame's memory would force the pipeline to retain frames | `TakesUpASurfaceChangeWhilePaused`, `RedrawsTheLastFrameWhenTheSurfaceChangesWithoutANewFrame`, `LetsTheRendererFollowTheSurfaceWhilePaused` |
| I12 | An idle thread sleeps on a change signal with a bounded timeout, never on a fixed short sleep, and it reads the change stamp before it decides there is nothing to do | Polling costs power and processor time for nothing, and a missed signal costs the promptness a pause, resume or seek is supposed to have — the timeout keeps a mistake in the *order* from becoming a hang | `WakesForEachSurfaceChangeWhilePausedRatherThanWaitingForTheBackstop` (counts the wakings inside one backstop period, so it fails if the signal is dropped) |
| I13 | A warning is a line, not a state: it is raised as it happens, nothing reads a "current warning" back, and what counts as a repeat is decided by whoever raises it | A warning folded into the state event makes every subscriber ask whether the state moved; a warning held as a value makes its lifetime someone's responsibility, and a value nobody clears is a stale one | `WaitsForADeviceThatIsNotMovingAndThenCarriesOn` (one line for the stall, one for the recovery, nothing in between), `PlaysAnAudioFileThroughTheRenderer` (a file that plays says nothing at all) |
| I14 | Only an item of the orchestration queue changes state, moves the clock, or raises an event; the threads that move media only post | This is what makes the state a volatile field instead of a locked one, and what lets a subscriber touch UI state without asking which thread it is on | every pipeline test: they call the pipeline from the test thread and assert on what was announced — and on what a command handed back — never on which thread a callback arrived on |
| I15 | Every call into the pipeline is posted and hands back the task of its own job, carrying whether it could be carried out; nothing inside the pipeline waits for a queue item, no queue item waits for a media thread's progress either, and the only wait in its own surface is `Dispose`'s teardown — a plain wait, because nothing in the teardown waits for something that will not come | A wait inside a queue item — the caller's thread stalls behind whatever is ahead of it in the queue, including another pipeline's work, and a nested call waits for itself. The end of a file used to be exactly that wait, which is why it is now a decision taken from messages instead of a wait taken from flags. A caller awaiting a task it was handed is doing what any asynchronous API allows | `SeeksWhilePlayingAndKeepsGoing`, `SeeksWhilePausedAndStaysPaused`, `SeeksWhileTheNextFrameIsStillWaiting` (all wait for the seek's task), `AnswersACommandWhileTheSoundHasNotPlayedOut` (a command answered while the tail is still playing out), and every test whose fixture it deletes straight after a dispose |
| I16 | **A command reports; playback fails.** A request that cannot be carried out is answered by the task it handed back and changes nothing else — no state, no event, no error — while `Error` is only ever playback that cannot continue | Folding the two together is how a pipeline dies because somebody dragged a slider past a source that will not move, and it leaves a caller unable to tell "that did not work" from "this is over" | `RefusesCommandsWhenNothingIsOpen`, `ReportsAFailedPlayOnceTheFileHasEnded`, `KeepsPlayingWhenTheSourceRefusesToSeek` (both refusal paths, asserting the state it had is the state it keeps), and `ReportsADecoderThatDiesAsPlaybackFailing` for the other side of it |

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
| Take a surface change up only while frames are being presented | A paused video keeps the surface it had when it was paused, and the host stretches the picture into whatever the window becomes — the defect this rule was written from |
| Replace a change wait with a fixed short sleep "to be safe", or poll instead of signalled | Nothing breaks immediately, which is what makes it tempting: what it costs is a wake-up every few milliseconds on every thread — a paused pipeline burning power and processor time for nothing — and it is how the spin came back after being removed once |
| Signal a change *after* the thread has already decided to wait, or check the stamp after deciding | The wait is entered with no memory of the change, so it lasts until the timeout: resume, seek and resize all become up to a quarter of a second late. Correct, but wrong in the way users notice |
| Put a warning back into the state event (or into a property read back later) | Every warning then looks like a state change, and a subscriber that shows state will show a stall as if playback had stopped; a property read back makes the *reader* decide whether the warning is still true, which is knowledge only the raiser has |
| Drop a frame whenever the clock has passed it (no peek) | The newest frame is thrown away whenever the queue is briefly empty; a slow decoder then freezes the picture instead of pausing playback |
| Treat starvation as a state or an error | The display starts flickering a rebuffering state at the frame rate, and hosts start tearing down playback that only needed to wait |
| Make the seek wait for the reader (lock the media while seeking without advancing the epoch first) | The seek waits for IO, and every wait behind it waits for the seek: the S8f freeze |
| Flush the video decoder from the seek's thread | A frame-threaded decoder is not thread-safe for that; the flush must be *requested* and performed by the owning thread |
| Anchor the clock to the seek target rather than the first played media | The picture sits a keyframe behind the sound forever, every frame dropped, the picture never moves again (the S8g symptom) |
| Make the pipeline wait for one of its own queue items "so the call behaves like it used to" | A caller's thread — often the UI thread — stalls behind whatever is queued, including another pipeline's open or teardown, and a nested call would wait for itself. The task every call hands back already lets a caller sequence its own calls, without the pipeline putting its queue inside somebody's thread |
| Present with a sync interval of one, "to keep the picture paced to the display" | The display becomes a second clock the pipeline cannot see, and the wait for it lands inside the presentation thread's frame: at a frame rate equal to the display's that is the thread's whole interval, so each frame's lateness ratchets up by the loop's own cost until the drop rule fires, and the drops are steady from then on. Measured: `render 18.0 ms` and a tenth of frames dropped with a 70 ms lag, on a machine whose renderer does the frame in 0.38 ms. The clock paces the picture; the display only decides what it shows (`Present(0)` — it still blocks when every buffer is in flight, which is the compositor being behind and cannot be helped) |
| Have the queue move the source "so the seek's result can carry what the source said" | That is two threads on one cursor, which is exactly what the media lock was for: the seek then waits for the reader's read (or the reader for the seek's IO), and the answer to a move the queue did not perform is not the queue's to know. The owner moves it; what can be known without moving it — `CanSeek` — is still in the result, and what only a move can reveal is a line |
| Have the queue flush the device "so the seek is clean before it returns" | The device is the audio thread's, and a second thread reaching into it is what makes a device that stopped consuming able to outlast a seek. The queue positions the clock and asks; the owner resets what it holds. The cost is that the reset arrives a few milliseconds later, which is why the clock must not *assume* the reset has happened (see §6) |
| Reposition the clock against a device that may not have been reset (the old `SetPosition`: base set, device position assumed zero) | The clock then reads as the position that was asked for plus everything the device has played since it was last reset — a jump forward of minutes on a real device. The picture follows the clock, so it races: this is what "the video speeds up after a seek" was |
| Report a command that could not be carried out by failing the pipeline (`Error` for a seek the source refused) | A seek a source will not take is a sentence, not an ending: the state becomes unrecoverable for a request that only had to be refused, playback that was carrying on is torn down around it, and every subscriber sees "playback failed" for somebody else's slider drag |
| Answer a command with nothing because "the events already say what happened" | A seek changes no state, so the caller that asked has no way to tell a seek that happened from one that was refused — and the pull to fix that by putting the refusal into the state is what produces the row above |
| Keep a stage's own work-state in a field "so the queue can nudge it" | The nudge is a write by a thread that does not own the state, so the stage may never see it — nothing orders the two, and no barrier is there to help. Worse, the field's name then claims an owner it does not have: the reader's filling latch read as reader-private while the queue also wrote it, until it became a local of the reader's loop, which is what it always was (§2.2). Influence a stage through the change signal or a queue item, never through its own state |
| Inline a call when the caller turns out to be on the queue already, with a flag that says so | The flag has to be right for every path (a callback into the pipeline from an event handler belongs to the item that is running), it turns one rule into two, and the call sites already know: the open calls the seek's core method, the audio thread posts. There is no case that needs it |
| Have the end of the file wait for the tail inside the queue item that hears about it ("so the caller's next command lands after the file finished") | The queue every command is posted to is then busy for as long as the tail takes — seconds — so a seek is answered when the file finished instead of when it was asked for, and the ending becomes a timeout: a sound that has not played out is cut off and nothing says so. Every stage says its own piece to the queue instead, and the ending is the queue's decision over those words (`AnswersACommandWhileTheSoundHasNotPlayedOut` keeps the wait from coming back) |
| Keep a "the decoder has drained" flag for the queue to poll, beside the messages the stages send | It was two writers (the decoder's thread setting it, a seek clearing it) with no ordering between them, so the older fact could land last and silence the next file's tail — and it existed only to serve the wait the row above removes. The fact belongs to the decoder's thread as a local, and its consequence is a message: the tag that carries a position is how a seek is taken up, never a flag retracted behind a writer's back |
| Change the state, raise an event, or touch the clock from a media thread "because it is the only thread that knows" | The single-writer property that makes the state a volatile field is gone, and every event becomes a thread question for every subscriber. Media threads hand what they know to the queue; that is what the queue is for |

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
| `PresentedFrameCount` | frames shown | stagnant while `Position` advances ⇒ the picture is not following the sound. It counts what was handed to the compositor, not what was scanned out: since the swap chain presents without waiting for a blank (§4.3), a frame the compositor supersedes is counted here and nowhere else — so `presented + dropped` is not a count of photons |
| `DroppedFrameCount` | frames decoded and never shown, all of them lost by the best-effort rule | climbing ⇒ the picture cannot be shown in step: a decoder or a renderer too slow |
| `DisplayedFrameCount` | how many of the frames handed over reached the display, when the renderer can tell, else null | null ⇒ the backend will not report it (and a swap chain that is not a flip-model one may not); rising much more slowly than `PresentedFrameCount` ⇒ the compositor is superseding frames, which is the price of presenting without waiting for a blank. It is a statistic about a swap chain, so it is documented as unreliable with several monitors or another fullscreen application, and it lags by the frame or two still in flight |
| `PictureLag` | how far the shown picture is behind the clock | a small constant is the synchronizer working; a growing one is the old freeze arriving |
| `IsStarving` / `StarvedFor` | time is deliberately stopped, and for which stage | "video" ⇒ a slow decoder is pausing playback (expected under load); "audio" ⇒ the file or the decoder is not feeding the sound |
| `WarningRaised` (event) | one line about something that is not a failure: audio that was refused, a device that stopped moving, and the line for it moving again | a warning is not a failure and not a state; it is raised as it happens and nothing holds it afterwards, so a consumer that wants to display one keeps the last |
| `AverageDecodeMilliseconds` / `AverageRenderMilliseconds` | cost per stage | either approaching the frame interval means that stage is what limits playback — **but check what is inside it first**: the render figure is the renderer's own work (upload, draw, hand the frame to the compositor), measured at 0.38 ms for 1080p into a 1.5× surface, so anything near the frame interval is either a much slower machine or a wait hiding in there (the 18.0 ms in the report that wrote §11's presentation row was 17.6 ms of waiting for a vertical blank) |
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
11. Does the change decide anything off the queue, wait for an item to be done, or add a call that returns a
    result? Re-read §2.1 first: the answer is "post it and hand back the task", the only waits are a caller
    awaiting a task it asked for and `Dispose`'s bounded teardown, and every call hands back what became of
    its own job, so a caller never has to infer it from the events.
12. Does the change make a call whose effect is invisible — one that raises neither a state change nor a
    warning? Such a call has to answer for itself (the seek's task), or the UI it is for will show a stale
    value until something unrelated happens.
13. Can the change make a call fail? If it can, the failure belongs to the caller that asked and nothing else
    may change — the state, the clock and the events stay as they were (I16). If it cannot, say so in the doc
    comment rather than leaving the next reader to guess.
14. Does the change add or move state? The declaration has to say which thread writes it, which thread reads
    it, and what makes that safe — nothing, `volatile`, `Interlocked`, or "written only while the threads are
    stopped" (§2.2). If that sentence cannot be written, the state belongs in a local of the loop that uses
    it.
15. Does the change introduce a second copy of a fact that already exists — a flag beside the flag it mirrors,
    kept in step by hand at every site that changes either? That is the shape `MediaQueue`'s completion flag
    had, and the shape to delete: the pipeline's `_sourceEnded` was already the statement, and the queue's
    copy only had to be remembered in two more places.
16. Is this fact needed by *one* reader — a decision that has to be taken somewhere? Then send it to that
    reader as a message and let it decide, instead of publishing a flag for everyone to poll: the queue owns
    every decision (§2.1), and the three things said at the end of a file — the decoder has produced its tail,
    the picture has shown everything, the sound has played out — are messages to the queue for exactly that
    reason. A flag that a decider must wait on is a wait anywhere else too, which is what made the end of a
    file block the queue that every command is posted to.
17. Does the change make a stage's word about *itself* the deciding fact, when only that stage can know it?
    Then no other thread may hold, retract or infer it: the presentation thread's "nothing held", the audio
    thread's "the device holds nothing" and the decode thread's "the tail is out" are each unanswerable
    anywhere else, and the queue may only combine them.

## Appendix B — where each concept lives

| Concept | Where |
|---|---|
| The orchestration, threads, state machine, starvation, device watch | `PlaybackPipeline` |
| What a command may fail at, and what that failure does (nothing but answer) | `PlaybackPipeline`'s `PlayCore`/`PauseCore`/`SeekCore`/`StopCore`, each returning a `PlaybackResult`; `IMediaSource.CanSeek` for the refusal a source can predict |
| The one queue that decides, and what may run on it | `PlaybackPipeline` (the static queue), `TaskDispatcher` |
| "No maximum", and why | the two `MediaQueue<T>`s built without a limit in `PlaybackPipeline` |
| "Wait for the picture, keep everything" | the one `MediaQueue<T>` built with a limit |
| "The queue knows nothing" — not what the tag means, not whether the stream has ended | `MediaQueue<T>` (items, tags, a limit, a weight, a close) and the checks at both its ends in `PlaybackPipeline` |
| Epoch discipline and what a seek uses to interrupt | `EpochGate` |
| When it is too late to show a frame | `ISynchronizer` (default: 2 ms early tolerance, 40 ms late) |
| The clock contract, and the wall-clock fallback | `IPlaybackClock`, `StopwatchClock` |
| Device position as the source of time, and where a reposition takes its zero point | the audio renderer's clock (`WasapiAudioClock`), which the device's own thread resets (`WasapiAudioRenderer.Flush`) |
