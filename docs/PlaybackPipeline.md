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
6. **Nothing here polls a media queue.** A thread with nothing to do waits on a change — the state, the
   position, the presentation surface, a stop, or an entry of the queue in front of it — and every one of
   those waits is released by a fact. Only three waits are still paced by a clock, and each is a thing the
   world cannot signal: the reader's read-ahead check (a rate rather than a fact), room in the device's
   buffer, and the device draining while the sound has nothing to hand over (§4.4). Everything else,
   including both ends of every `MediaQueue<T>`, is woken by something happening. The backstop on the state
   signal (a quarter of a second) is what keeps a mistake in the *order* of a signal from becoming a hang —
   and its fires are still invisible, recorded as follow-up work.
7. **A signal must never be missed, and the pattern that guarantees it is the order of the call:** a thread
   reads the change stamp *before* it decides there is nothing to do, then waits. A change arriving while it
   decides is then seen by the stamp check inside the wait, and a change arriving after is a pulse with a
   waiter already in place. Getting this order wrong does not break correctness — for a wait on the *state*
   the timeout still ends it — it breaks *promptness*: a resume that should be instant becomes a quarter of
   a second late, which is exactly what the test `WakesForEachSurfaceChangeWhilePausedRatherThanWaitingForTheBackstop`
   pins. A queue's wait has no such backstop: it hands over an entry or the closed word and never wakes
   anyone to say that nothing happened, so a release that is missing is a thread that never moves — which is
   why every release in I17 has a test that fails rather than hangs.

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
- **Each command names the states it is for**, and anything else is refused with the state it found: `Play`
  is for `Paused` (and a host that wants to play from `Ended` seeks first — one line in the adapter), `Pause`
  is for `Playing`, `Seek` is for `Paused`, `Playing` and `Ended`, `Stop` is for anything. A command is not
  quietly absorbed where it does not apply, because "pausing was not needed" and "pausing was refused" are
  different answers, and only the caller knows which one it asked for.
- **The state says what playback is doing, never what the caller wants it to do.** A seek is the sharpest case
  and the rule is flat: it leaves playback `Paused`, whatever it found. Whether the new position goes on
  playing is the *host's* decision, made with one `Play` — which is also the only way the host can tell the
  pipeline "the user paused while the seek was in flight". The adapter is where the app's expectation ("a jump
  while playing keeps playing") is restored (§11).
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
- **A state change tells subscribers; the caller wakes the threads.** `SetState` moves the state, re-decides
  the clock and raises `StateChanged`, and it does not signal — because what a woken thread must see depends
  on the caller. A seek is the case that matters: it is a state change *and* a position change, and a thread
  woken between the two reads the position playback has left and waits again (for the reader, parked without a
  timeout once the file has run out, that is the seek never happening at all). So the caller signals when the
  change it is making is complete — the seek after the epoch, the clears and the anchor have moved; the open
  not at all, since it starts its threads afterwards; the stop before its state, because what releases those
  threads is the flag and the closed queues rather than the state it ends in.
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
| **Fixed at construction** (the factories, the wake lock) | the constructor | anyone | nothing is ever assigned again; a lock that is needed carries the name of what it protects |
| **The position change, as one fact** — the epoch, the position published with it (`_pendingSeekPosition`), and what belongs to a position: the queues' contents, the starvation flags, the anchor, the clock's position | the queue, inside the change, and nothing else ever | the queue; and the media threads through `_epochs.Read()`, without the lock | a **seqlock** (`SeqLock<long>`), because it is a compound update several threads act on: a thread that reads the epoch must not be able to see the new position while the queues still hold the old one's media, or the reset that follows would delete media it produced for the new position. The readers are the reason it is not a plain lock: the epoch is read per packet and per frame on three threads, and a seqlock makes those reads a sequence check rather than a contention point — while a plain lock would be taken and released for every one of them. A reader that sees the change still running reads again, and waits for the writer's own lock if the change outlasts its attempts |
| **Open state** — the source, the decoders, the renderer, the three queues, the four threads, the streams, the info, the clock object | the orchestration queue, **only while no media thread is running**: before they start, or after they are joined | the media threads and the host | the invariant above, which is why there is no lock around any of it |
| **One owner, no lock** — the media cursor (`IMediaSource`) and the audio decoder that rides on it belong to the reader, which reads, moves, flushes and drains them and nothing else touches; the video decoder belongs to the decode thread, which flushes it and forgets its tail when it sees the epoch move | the owner, and only the owner | the owner | nothing: a lock around a cursor two threads move was the thing this replaced. The flush is not requested with a flag either — the epoch *is* the request, and the owner's memory of having taken it up is a local of its loop |
| **The queue's own decisions** — the state and its error, `_disposed`, `_stopRequested`, the position the file ran out at (`_endingEpoch`), the position each stage has said its stream is over for, the two starvation flags, the anchor flag, and the published seek position | the queue, and only the queue | the queue, except where a media thread needs the value *now* | one writer, no compound update, nothing shared with a second field: `volatile` where a media thread reads it, `Interlocked` for the tag that has to be long, and nothing at all where only the queue does. A stage's word is filed by the queue and never read back by the stage itself — it is said once, when that stage's stream ends |
| **Thread-owned** — the audio thread's device watch | that thread while it runs, and the queue's teardown after the join | that thread | a field only because the state must outlive a wait; everything else a thread needs is a local of its loop. A seek is taken up by the watch noticing the epoch move, not by the queue resetting it — and the teardown reset is the one write from outside, made when there is no thread left to share the fields with |
| **Published by a stage** — the starvation ask each stage makes, and the three things a stage says about the end of a file (the decoder has produced its tail, the picture has shown everything, the sound has played out) | the stage writes the ask; the queue owns what the ask becomes | the queue, and the stage itself reading back *what it asked for* | `volatile` where a media thread reads, and no copy kept by the stage: what a seek makes meaningless is retracted where it is owned. The end-of-file words are messages rather than flags, which is why a stage never reads another stage's |
| **Counters and hand-offs** — presented/dropped/displayed frames, the tick totals, the picture timestamp, the decoded tail | any thread | anyone, including the host | `Interlocked` |
| **The clock object** | the queue (start, stop, set position), and the first media of a position (the anchor) | every thread reads its position | the clock implementation must be safe for that on its own; readers never lock around it |

Every field is declared in the file's one `#region Variables`, in one of those groups, and the group's comment
says who may write it. That is not decoration: **a write that sits outside its group's thread is the bug**, and
it is the check that found the two ownership mistakes in S9q and S9r without running anything. If a new fact
cannot be placed in one of these groups, it is not the fact that is unusual — the placement is the design
decision, and it has to be made before the field is declared.

Five questions, in this order, decide where a piece of state goes. The first that can be answered yes is the
answer:

1. **Is it the work-state of one thread's loop?** Make it a local of that loop. (The reader's filling latch
   and the epoch whose end it has reported were both fields once; neither had any business being one. And
   the decode thread's "have I produced this position's tail yet" was a local for the same reason, until the
   end of the stream started arriving as an entry of the queue itself — the one thing worth deleting more
   eagerly than a field is a field that no longer answers a question.)
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

The position change is the one fact that is none of these and needs a shape of its own: one writer (the
queue), several fields that must move together, and readers that read it per packet and per frame on three
threads. A lock is what "one writer, many readers" would pay for that — once per read, on the threads whose
whole job is not to stall. `SeqLock<TValue>` is the answer the pipeline uses: the sequence says whether a
change is in progress, so a reader normally pays two extra reads and nothing else, and the work that belongs
to a change runs inside its window so no reader can act on half of it (§6, question 4).

**`volatile` is never an answer on its own.** It is the last inch of question 2, in a place where the
ownership was already decided. A flag one thread must retract for another is the shape to look for and
remove — the stage re-derives, the watch re-seeds, the queue keeps its own copies out of it — because the
retraction is a write with no ordering behind it, and no keyword fixes that.

The same questions apply to *where a value is compared*, not just where it lives. The epoch is the pipeline's
fact about the position; the queues carry it as an opaque tag and compare nothing, every producer checks it
before handing work over (handing over is what books the work as progress), and every consumer checks it
before using what it took. A queue that filtered on it would be a fourth place deciding what a position
means, with no way to see the other three.

One thing the tags cannot do is move a thread that is parked on a queue, and that is what the reset inside the
position change is for (§4, question 4): the epoch says what is stale, the *reset* says what is no longer
there, and both have to happen before any thread can act on the new position — otherwise the reset deletes
what the new position's first thread produced. So the epoch is no longer the lock-free counter it once was
(§4, question 4, and `SeqLock<TValue>`): it is read without a lock, but a read that lands inside a change
reads again rather than acting on a position whose reset is still running, and the reason is written where the
change is made (`SeekCore`).

## 3. The three hand-offs

There are exactly three queues, and each has a different policy because each is there for a different reason.

| | Packet queue | Audio queue | Frame queue |
|---|---|---|---|
| **Between** | Reader → video decode | Reader → audio sender | Video decode → presentation |
| **Holds** | encoded video packets | decoded audio buffers | decoded video frames |
| **Maximum** | none | none | 8 frames |
| **A full queue means** | — | — | the picture is behind, so the decoder stops |
| **Producer waits?** | never | never | yes, until the picture takes a frame, the queue is cleared (a seek) or closed (a stop) |
| **Consumer waits?** | until an entry arrives — a packet, or the end of the stream; a clear leaves it waiting (it has nothing to do about a seek), a close releases it with false | until an entry arrives, on the same terms | until an entry arrives, on the same terms |
| **On a seek** | cleared: what it holds belongs to the position playback has left, and dropping it also releases the thread parked on it | cleared, for the same reason | cleared — which is what frees the decoder waiting for room, because the picture cannot while it is paused |
| **At end of file** | the reader says the end of the packet stream with an entry of its own, after the last packet; the decoder takes that entry, produces its tail into the frame queue, and says *that* stream's end behind it; the pipeline's own statement — the file ran out at this position (`_endingEpoch`) — is the third thing the reader does, and the ending is decided on it | the reader says the end of the audio stream with an entry, after the last block (the drain above is what produces it); the sound says it has played out when the device holds nothing after that | the decoder says the end of the frame stream with an entry behind every frame it produced; the picture says it has shown everything when it takes that entry while holding nothing — and an empty queue before it means the decoder is behind |

The three are one class, `MediaQueue<T>`, configured three ways: no limit, no limit but weighed in samples,
and a limit. The policies differ *on purpose* and each is one argument at its construction site in
`PlaybackPipeline`; the queue itself knows nothing about epochs — it carries a tag, and what the tag means is
decided by the producer that writes it and the consumer that reads it — and it knows nothing about what the
end of a stream *means* either, though it can be told that one has happened: `EndStream` puts an entry behind
the media, and the entry is taken like any other, with no media and a flag. That is deliberate and is the one
thing an empty queue cannot say: "empty" and "over" are different facts, and only the producer knows which one
it is leaving behind. What a seek does to them is `Clear`: drop what they hold and let the parked producers go
on — the end entries included, which is what makes them meaningless without anything having to retract them.
That is a *state* question and not a tag one — a full queue is where the decoder and the reader are parked, and
a queue cannot pace a thread on a position the position itself has left. What the released producer hands over
next still carries its own epoch, so nothing stale is taken for new; the tag is not what the seek is using the
queue for. A *consumer* waiting for an entry is not released by any of this, and it does not need to be: it
has nothing to do about a seek, and what it should play next arrives as the next entry, stamped with the
position that entry belongs to. What releases a consumer is an entry, or the close a stop performs — a queue
hands over one of those two and never wakes anyone to say that nothing happened.

**Why two of the three have no maximum.** The reader is the single cursor of an interleaved file: what it has
not read yet is *both* streams. A bounded packet queue therefore means "the picture being slow stops the
sound" — the coupling behind several of the historical failures. The reader paces itself instead (§4.1), and
what bounds the read-ahead in practice is playback itself: the minimums advance only as fast as the file is
played.

**Why the frame queue does have one.** Its consumer is the picture, and the picture is the one stage that
follows the clock. A decoder stopped here is stopped by the thing it feeds, and starts again the moment the
picture takes a frame. It is also the only place where decoded — i.e. largest — data can pile up. It is the
other half of the new pause model (§7): with the picture parked, this queue is what stops the decoder, and the
reader behind it stops on its own read-ahead, so no thread has to consult the state to know it should not be
working. A seek releases it by *clearing* it — see the row above; a stop releases it by closing it, and that
close is what the joins depend on not blocking (§7).

## 4. The five rules

**Every thread waits only for something it alone depends on, and every wait it takes ends without another
stage having to make progress.** These are the rules; §4.1–§4.5 are why each is written the way it is.

| # | Thread | Waits for | Ended by | If it never ends |
|---|---|---|---|---|
| 1 | Reader | the file | a packet arriving | the file is the outside world; this is the one legitimate IO stall |
| 2 | Reader | *nothing else* — it idles while both queues hold their minimums | either queue falling below half of its minimum | it keeps idling and re-checking |
| 3 | Video decode | room in the frame queue | the picture taking a frame; the queue being cleared (a seek) or closed (a stop) | the picture is not draining — which is the picture's problem, and the one wait that ends with the consumer, or with a seek that moves the decoder on |
| 4 | Presentation | the clock reaching a frame's moment | time passing | only a clock that is not moving, i.e. rule 5 |
| 5 | Audio sender | room in the device's buffer | the device playing; a seek moving the epoch on; pause/stop | a device that is not consuming — which stops the clock, so it is the same wait as rule 4 |

> **Only presentation waits on the clock. Only the reader waits on the file. Nothing waits on the reader.**

### 4.1 The reader never drops and never blocks on a stage

It fills both queues to their minimums, then idles until either falls below half of its minimum, then fills
again. The minimums are *read-ahead*, not capacity: 500 ms of audio samples and 64 video packets, waking at
250 ms and 32. A stream the file does not have is trivially satisfied.

**It reads for a playback that is playing or paused, and does nothing in any other state.** It is the one
thread that works while paused — the read-ahead is what a resume starts on — so those two states are what its
work is checked against, in the positive direction: a stopped, ended or failed playback has nothing to read
*for*, because nothing will consume what it produces and the position it would fill will never be played. A
state added later therefore gets the safe answer (park) without anyone having to remember to exclude it
(Appendix A item 26).

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
  slow decoder into a frozen picture. And only when the clock is a fact about *media*: until the first media
  of the position has anchored it (§5), the clock reads the position a seek asked for rather than the one the
  file resumes at, and a frame judged against that would make the picture skip the distance between the two —
  so before the anchor, a late frame is shown rather than dropped. Starvation has the same rule for the same
  reason.
- **Hold the clock back when the picture cannot catch up.** If the newest frame the decoder has produced is
  more than **200 ms** behind the clock, the clock stops — playback waits for its own picture — and starts
  again once the gap is within **100 ms**. A wide threshold to halt and a narrow one to resume, so a decoder
  hovering at the boundary cannot make playback stop-start.
- **The trigger is a lag, not a gap between frames.** A gap only exists when frames stop arriving, which a
  uniformly slow decoder never produces; a lag exists as soon as the decoder falls behind, which is exactly
  the case that needs the sound to wait. This was the one correction the design made to itself during review.
- **Waiting costs no frames.** While the clock is held, the decoder keeps producing and the picture keeps
  showing; only the drop rule above loses anything.
- **A paused file shows the picture of where it is.** While nothing is being presented, the picture offers
  the position's first frame that is still to be seen — one, once per position, taken from the queue the
  decoder filled and handed to the renderer. It is not a presentation: the clock is not asked, nothing is
  anchored, dropped or starved, and the wait for the frame has no clock behind it — the frame arriving is the
  wake. Without it a paused seek shows the picture of the position it came from, because the frames of the new
  position are decoded and simply never looked at. Frames of a position playback has left are given up on
  rather than shown, and the position that is current is re-read as it goes, because a seek landing while this
  waits leaves the frame it is about to be handed belonging to nobody. A position with no frame at all says
  the picture's ending word there and then, rather than leaving it for a presentation that is not coming — a
  paused file whose picture has shown all it has, has shown all it has.
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

- **The audio queue is empty.** There is nothing to hand over, so the thread stops waiting on the queue and
  watches the device instead — the device is the one thing in this thread's world that cannot signal, which
  is why this is the wait in the pipeline that is paced by a clock. The question it asks is what is being
  heard: while the device still holds audio the clock is left alone, because that audio is playing; once the
  device holds nothing — and the file has not run out at this position — nothing is playing, so the clock is
  stopped, or the picture would run ahead of silence. The next buffer starts it again. After the end of the
  file an empty queue is the sound *playing out*, not falling behind, so the stop does not apply: the device
  emptying then is the sound's word that everything has been heard, which is its half of the ending.
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
- **The clock runs only while playing and not waiting for a stage** (§8). Every state change re-evaluates
  this, and the clock is *told* only when the answer has changed — asking it what it is doing is what makes
  that check exact, and it is what lets a clock implement `Start`/`Pause` naively (I10).
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

1. Refuse it outright if the source cannot be moved at all (`CanSeek`) or if the state will not take one
   (`Paused`, `Playing` and `Ended` are the states a seek is for) — before anything is paused, so the refusal
   costs nothing. This is the only refusal that can be answered here, because whether a source *can* be moved
   is fixed when it is opened; whether it *will* is only known by trying.
2. **Pause**: a seek leaves playback paused, always, whatever it was doing. It is a state change of its own —
   the position has moved, so nothing about the old one is being played any more — and whether the *new*
   position goes on playing is the host's decision, made with one `Play`. That is also what keeps the two
   refusals straight: a seek refused here (step 1) changes nothing at all, while a seek the source refuses
   *when asked* was accepted first, so the pause is the move's doing and the reader's refusal only means the
   position did not change (§11, I16).
3. **Change the position as one fact, in one change**: publish the position, advance the epoch, clear
   the three queues, clear both starvation flags, re-arm the anchor, and set the clock's position. The change
   is one fact rather than a sequence of steps — the threads that act on the new position act on all of it, and
   an epoch that moved before the reset did is an epoch they can produce media under, media the reset then
   deletes (measured: with the window widened to 300 ms, the reader's first 48 packets of the new position were
   thrown away, keyframe included). The same change is what a thread reads the epoch through
   (`_epochs.Read()`), and a read that lands inside it reads again instead of seeing the new position before
   everything that moved with it has moved. A second request before the reader acts overwrites the published
   position, which is what makes the latest request the one carried out.
4. Clearing the queues is also what releases the two threads parked on them: a full frame queue is where the
   decoder waits for room that only the picture can make, and a filled packet queue is what tells the reader
   it has read enough (see §3's table). The clock gets the requested position *for the host's benefit* — the
   media will set it to where playback actually resumes, see the anchor, §5 — and until it does, no decision
   is allowed to depend on it. The tail the decoder produced is not cleared here and the flush is not
   requested here: both belong to the decode thread, which takes up the new position itself the moment it
   sees the epoch move (§2.2). The end of the file is not withdrawn here either, because it is stated *with*
   a position: a new position is simply not the one the file ran out at.
5. One signal for one seek, sent here rather than with the state change of step 2: what the wake is for is
   that the position *moved*, and a thread woken between the two would read the position playback has left
   and go back to sleep — for the reader, which waits without a timeout once the file has run out, that means
   not taking the seek up at all. So the state change tells subscribers, and this — after the epoch, the
   clears and the anchor have all moved — tells the threads.
6. Answer the caller with the result of the request — accepted, or the predictable refusal from step 1.

Then, on the reader's side, where the cursor lives:

1. Notice the epoch has moved past the one this thread has carried out, and take the position published
   with it. If a newer request arrived in between, take that one instead: the newest request is the one that
   matters.
2. Move the source and flush the audio decoder — the two objects the reader owns, which no other thread
   touches at all now, which is why there is no lock around either.
3. From that point on, stamp what it reads with the epoch it has carried out — never with the epoch the lock
   happens to hold. A packet read from the old cursor must keep the old stamp, or every stage downstream
   would take it for new media and play it.
4. A source that refuses the move is reported as one line, and the reader changes nothing else: nothing was
   flushed and no position moved, so playback stands exactly where it was — *paused*, because the pipeline's
   own step 2 happened before the reader was ever asked.

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
| Video decode | a packet queued under the old epoch is dropped without decoding; the decoder is flushed and its tail forgotten (by this thread, which owns it) before anything from the new epoch is decoded — taken up at the top of its loop *and* again immediately before a decode, since a seek can land while it waits for a packet |
| Frame queue producer | a wait for room is released by the close a stop performs, not by the seek: releasing it for a seek helps nothing when the picture is stuck, and its work would be dropped |
| Presentation | the held frame is released; frames queued under the old epoch are invalid by construction |
| Audio sender | a buffer whose epoch is not the current one is given up on before it can anchor the clock; when it sees the epoch move it lets the device go of what it holds (the reset only the device's owner can do); a wait for room that the seek interrupted ends with the buffer let go of, and a submit that raced the seek is followed by a device flush |
| Audio clock | it does not assume a reset device: wherever it is repositioned from, the device's current position is taken as the clock's zero point, so reading it is safe while the reset is still on its way |

Six details that are easy to get wrong, five of which were bugs once:

- The flush of the video decoder must happen **on the thread that owns it** (a frame-threaded decoder cannot
  be flushed from outside), and the epoch the packet carries is what says whether there is any point decoding
  it.
- The epoch check around an audio submit has to happen **after** the device call as well as before it, because
  a device that had stopped consuming can make a submit outlast the seek that interrupted it.
- The device is let go of **before** the first block of a newer position reaches it, in the same step as the
  submit: the top of the audio thread's loop is not enough, because a seek can hand it a block of the new
  position while it waits inside the dequeue (the same is true of the decoder, which is why it takes the
  position up again immediately before a decode and not only at the top of its loop).
- The tail the decoder has produced is cleared by **its owner**, in its own order, and not by the queue — two
  writers to one word with no ordering between them is how the old position's tail survives a seek and holds
  the clock back for it.
- The reader stamps what it reads with **the epoch it has carried out**, never with the epoch the lock holds,
  or media read from the old cursor is labelled as belonging to the new position and every filter downstream
  is fooled at once.
- Every frame is tagged with **the position the decoder was made to belong to**, never with a fresh read of the
  gate. That position is a parameter of the one call that changes it (`EnsureTheDecoderHolds`), so each call
  site says what it is claiming:

  | Call site | Argument | What it claims |
  |---|---|---|
  | before a decode | the **packet's** epoch, inside the epoch check | the decoder holds nothing but this packet's position, so its frames can be tagged with that epoch |
  | idle, and before the end-of-file drain | **where playback is** (`_epochs.Current`) | the decoder must not go on holding a position playback has left; whatever it lets go of is discarded, never tagged |

  After the call the decoder holds nothing but what is decoded for that position next, and the decode path is
  what guarantees the next decode *is* for it. A fresh read of the epoch cannot be used as a tag: it may be a
  position the decoder has not been let go of yet, and the frame queue's own check cannot catch that — it
  rejects a tag *older* than the current epoch, never one that is newer.

## 7. States, pause, and the end of the file

States: `None → Opening → Playing ⇄ Paused → Ended`, with `Error` reachable from anywhere. `Stopped` is
`None` with nothing open. **`Error` means playback ran into something nobody asked for and nothing can carry
on through** — a decoder that died, a reader whose file went away, a stage that failed while it was playing
the tail out. It is never reached because a *command* could not be carried out (I16): that is answered to the
caller and nothing else changes, and an open that fails leaves `None` — nothing open — rather than `Error`.
A device that stops consuming is not on that list, at the end of a file or anywhere else: it is time that
has stopped, which is a wait (§9).

**What `Error` is, mechanically: playback has stopped, and the pipeline has not been torn down.** The clock
stops and every thread stops working for playback: the picture and the sound park on the state, the decoder is
stopped by the frame queue it can no longer empty, and the reader parks on the state too — playing and paused
are the only states it reads *for* — instead of filling a read-ahead and re-checking the queues every 15 ms for
a consumer that is not coming. What stays is what explains the failure and what the host needs to decide about
it: the reason (`Error`), the scene (`MediaInfo`, `Position`, the §13 counters), and the resources — the same
lifetime a *paused* pipeline has. Stopping or opening something else is what releases them, and both are the
host's call, which is the only place that decision belongs: a teardown is four joins with no timeout on the
queue every command is posted to, and a failure is exactly the moment another thread may be stuck inside a
component (§11).

**A component that throws has failed, and the guard belongs at the call.** The components — the source, the
decoders, the renderer, the clock — report failure by *returning* it (`PlaybackOperationResult`), and several of
them are interop, where a fault can arrive as an exception instead. Where a component is called, the exception
is turned into the failure the caller already handles, and there is no other try/catch in the pipeline: a
thread's whole loop is **not** wrapped, because an exception from the pipeline's own code is a bug in the
pipeline and must stay visible rather than being reported as whatever component happened to be called last.
Two consequences worth stating:

- A media thread that meets a throwing component reports it (`PostFailure`) and ends; with no guard, the
  exception leaves the thread and takes the *process* with it — .NET kills it outright, which is how a codec
  fault becomes an application crash with no message at all (this is what `ReportsADecoderThatThrowsAsPlaybackFailing`
  pins).
- The audio thread is the one exception, and it is deliberate: its interop surface includes *property reads*
  with no failure channel at all — how full the device's buffer is, what its clock reads — so there is no call
  to guard, and the method-level catch is the only shape that covers them.

- **Pause** stops time, and stops *work* by the queues rather than by telling the threads: the picture parks
  on the state, and the two threads behind it park on their own queues without ever consulting the state — the
  decoder on a full frame queue (its producer-side limit), the reader on its read-ahead minimums, which stop
  advancing once nothing is consumed. Nothing is discarded, and a buffer the audio sender was carrying is kept
  rather than dropped. Two consequences worth knowing: a paused pipeline costs less than one that wakes
  threads to check a flag, and a seek while paused must *clear* the queues, or the two parked threads never
  hear about the new position (`SeeksWhilePausedAndMovesTheReaderAndDecoder`). Two things keep moving. The
  surface: the presentation thread offers the renderer any window resize while nothing is being presented
  (§4.3), because a paused picture still has to fit the window it is in. And the picture itself: a pause is a
  position, and a position that has not been shown yet is shown once — the still of §4.3 — which is what makes
  a seek that landed while paused show the new position instead of the picture of where it came from. A pause
  that moved nothing shows nothing, because the picture on the screen is already this position's.
- **Play** restarts time through the same single path every state change uses, which consults the starvation
  flags: playback resumes into "playing and not waiting", never into "playing but stopped". It is for `Paused`
  only: from `Ended` the host seeks first (the adapter does), and from `Playing` it is refused rather than
  absorbed.
- **A state change is the only thing that starts or stops the clock**, so there is exactly one place where
  "should time be passing" is decided — and the clock is told there, once per change of the answer, not once
  per command that touches it.

**End of file.** The reader's end-of-file is the end of the *packets*, not of the media, and the ending is
**played out by the stages that hold the media and decided by the queue** — the queue is told, never held:

1. The reader drains the audio decoder's tail into the audio queue itself (the decoder is its own) and then
   *says the end of its two streams*: one entry behind the audio queue's last block, one behind the packet
   queue's last packet. Draining first is what makes both honest — a stage that later says it has nothing left
   can only be talking about media it was given — and the entries are what say "nothing more is coming"
   without any stage having to infer it from an empty queue or ask about the position.
2. It then posts the pipeline's own statement, `_endingEpoch` — the file ran out at this position — which is
   a different fact from either stream's end: it is about the *file*, and it is what the ending is decided on.
   Nothing is expected yet and nothing is waited for; a seek that arrived first, or a stop, makes the report
   stale by that position and it is dropped.
3. The video decode thread takes the end of the packet stream as the entry it is — the last thing it will ever
   be handed for this position — makes the decoder hold that position, and drains the decoder's delayed frames
   into the frame queue. Handing the tail over is also what ends that stream: the frame queue gets an entry
   behind the frames it produced. Without that, "the picture has nothing to show" cannot be told from "the tail
   has not come out yet".
4. Each consumer says its own piece when its stream ends, and says it once: the picture has shown everything
   when it takes the end of the frame stream while holding nothing (an empty queue before that entry is the
   decoder being behind), and the sound has played out when the device holds nothing after the end of the
   audio stream. A stage can only be asked about itself, and no stage reads another stage's word. The two
   words and the file's end are different threads, so they can arrive in any order: each word carries the
   position it is about and is *compared*, not consumed, so one that arrives before the file's own end has
   been reported is still there when it is.
5. The queue ends playback when the file has run out at the current position and every stage that had media
   has said its word about that same position. A pause is not in the way: the words are the stages saying
   they have nothing left, so a file that has played out is over even if the user paused on the last of it,
   and nothing has to ask again for the file to be allowed to finish (the ending used to be decided only
   while playing, with a resume asking the question a second time — which left a paused-at-the-end pipeline
   holding a file nothing was ever going to play again). The one state the decision does respect is the one
   it would overwrite: `Error` means playback cannot continue, and an ending is not allowed to replace that
   with a file that merely finished. A seek leaves the ending behind: every word carries the position it is
   about, and that position is gone.

One consequence of the shape worth stating, because it is what makes the model reliable: **a stage learns the
end of its stream from the media it is handed, never from a poll.** The decode thread used to find out by
seeing its queue empty *and* asking whether the file had run out — a question about a position it was not
holding at that moment, answered about wherever playback had got to. It now cannot get that wrong: the entry
carries the position with it, and the thread's tag for what it holds comes from the same entry.

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

The end-of-file statement matters beyond bookkeeping: it suppresses the picture's starvation halt, because
*after the end an empty frame queue is the media playing out, not a stage falling behind*. Getting that wrong
makes the end of the file hold the clock back forever. It is now read in exactly one place — that starvation
rule (`IsEnding(epoch)`, a comparison rather than a shared flag, which is what keeps the statement and the
position it is about from ever being read apart) — because the other readers it used to have are gone with the
empty-queue inference that needed them: the decode thread is handed the end of its stream instead of asking
whether the file has run out, the sound knows its stream ended the same way, and the device watch does not need
it to know that a device stopping with the last of the sound inside it is the same stall as any other (§8, §9).

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

The last thing the teardown does is **advance the epoch**, and it is the same statement a seek makes: everything
in flight belongs to a position that no longer exists. It is what lets a report posted by a thread on its way
out be dropped by the position check that drops a seek's leftovers, so no handler has to ask whether a stop is
in progress — and it is why reopening the same instance cannot inherit a stale "the file ran out"
(`PlaysAgainAfterAStop` covers that path; the window itself is milliseconds wide and no test hits it, which is
said plainly rather than papered over). The advance is last, and deliberately: the media threads are joined by
then, so none of them can read the new epoch as a request to move a cursor that is being disposed.

## 8. Starvation: what holding the clock back means

"Halting the clock" means exactly one thing: **playback waits**. The state stays `Playing`, nothing is
discarded, nothing fails, and time resumes on its own when the wait ends.

| Flag | Set by | Set when | Cleared when |
|---|---|---|---|
| Waiting for video | presentation | newest decoded frame > 200 ms behind the clock | within 100 ms again, or no clock/tail/anchor yet, or the file has run out at this position |
| Waiting for audio | audio sender | the device held nothing, the queue was empty, and the file has not run out at this position | the next buffer is there |

Both feed one decision — *should time be passing* — which is: playing, and neither wait is in force. The
hysteresis (200/100 ms) and the "not before the first anchor" condition exist so that ordinary jitter cannot
make playback stop-start; the end-of-file condition exists so the tail-out is not mistaken for a stall, and it
is *about a position* rather than about the pipeline: "the file has run out where I am", which is why a seek
does not have to retract it and a stop need only advance the epoch. The two waits are measured differently
because the two stages know different things: the picture measures itself against the clock (a time it owns a
view of), while the sound asks the device whether anything is still in it — the sound having nothing to hand
over says nothing about what is being heard, and it is the hearing that time must not outrun.

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
| I4 | The presentation thread never displays early and never waits for one particular frame | Both are the failure modes called "frozen" and "out of sync" | `StartsWhereTheFileResumesRatherThanWhereTheSeekAsked`, `DropsOnlyTheFramesWhoseMomentHasGone`. The one frame it does wait for is the still of a paused position, and that wait has no clock behind it: the frame arriving is the wake (§4.3) |
| I5 | Nothing waits for a queue that no longer exists, and no wait holds on to a position playback has left: a seek is a request the cursor's owner carries out rather than a lock the seek takes, and it *clears* the queues so the threads parked on them are released | Bounded liveness: the decoder's wait for room ends with the picture taking a frame, with the clear a seek performs, or with the close a stop performs — never with a state check that a pause would make false | the seek tests, delivered as "the seek never returned" assertions, `SeeksWhilePausedAndMovesTheReaderAndDecoder` (a paused seek with both producers parked), and every test that disposes a pipeline with a full frame queue |
| I6 | Media carries the epoch it was produced for, and is dropped rather than replayed when the epoch has moved — checked by the producer before it hands work over and by the consumer before it uses what it took, never by the queue in between | A seek that half-works is worse than one that visibly restarts; and a queue that filtered would be a queue that decides, which is how the epoch gets back into a place with no business knowing it | `SeeksWhilePlayingAndLeavesItPaused`, `SeeksWhileTheNextFrameIsStillWaiting`, and `MediaQueueTests` for the tag travelling untouched |
| I7 | An empty queue is a wait, and only an empty queue *after the end of the stream* is played out: the end arrives as an entry behind the media it follows, and the ending is *earned* — the queue ends playback when the picture and the sound have each said their stream is over, for the position the file ran out at; a pause does not hold it open, and a failure is never talked over by it | A timeout in the queue is two faults at once — it blocks the queue every command is posted to, and it ends a file whose sound has not been heard; and inferring the end from an empty queue makes "the decoder is behind" and "the decoder is done" the same observation, which is a question about a position the asker is not holding. Gating the decision on the state instead needs a second place to ask it again, which is what left a paused-at-the-end file open for good | `PlaysAnAudioFileThroughTheRenderer`, `PlaysAVideoOnlyFileToTheEnd`, `ProducesTheTailOfAFileWhenItsPacketStreamEnds` (a decoder that only releases its tail when drained), `PlaysAgainAfterTheEndOfTheFile`, `AnswersACommandWhileTheSoundHasNotPlayedOut` (fails if the wait comes back), `EndsWhenTheDeviceHasPlayedOutTheSound`, `EndsWhilePausedWhenTheFileHasPlayedOut` (fails if the ending waits for a resume) |
| I8 | Losing media happens in exactly one place, and it is counted | A media pipeline that loses frames silently cannot be diagnosed | `DroppedFrameCount` assertions in the drop and device tests |
| I9 | Every wait ends; a stalled device is a wait that ends when the device does, and is named while it lasts | The user must never see a freeze with no reason | `WaitsForADeviceThatNeverTakesAudio`, `WaitsForADeviceThatIsNotMovingAndThenCarriesOn` |
| I10 | The clock starts and stops in exactly one decision, a state change always re-evaluates it, and the clock is *told* only when the answer changes | Two places deciding "is time passing" is how pause, starvation and seek start fighting — and a clock told the same thing twice is a clock whose `Start`/`Pause` have to tolerate it, which the interface does not promise (a naïve `Start` re-anchors, a naïve `Pause` records a stop that never happened; the two real clocks happen to guard themselves) | `PausesAndResumes`, `TellsTheClockOnlyWhenWhatItShouldDoChanges` (a state change that leaves the answer the same tells the clock nothing), and every test that asserts `ClockIsRunning` |
| I11 | A surface change is taken up whether or not frames are being presented, and drawing the last frame again never depends on a frame still being alive — and what a paused picture shows is the current position's first unseen frame, once, so a seek that landed while paused is not left showing the position it came from | A paused video that ignores a resize is stretched by the host, and a redraw that needed the frame's memory would force the pipeline to retain frames. A paused seek that showed nothing new would be showing a picture of a position playback has left — and one that showed the frame *again* on every pause would jump backwards to the oldest frame the decoder is holding | `TakesUpASurfaceChangeWhilePaused`, `RedrawsTheLastFrameWhenTheSurfaceChangesWithoutANewFrame`, `LetsTheRendererFollowTheSurfaceWhilePaused`, `ShowsTheStillOfThePositionASeekMovesToWhilePaused` (one frame, and one only), `DoesNotShowAStillWhenNothingMoved` |
| I12 | An idle thread sleeps on a change signal with a bounded timeout, never on a fixed short sleep, and it reads the change stamp before it decides there is nothing to do | Polling costs power and processor time for nothing, and a missed signal costs the promptness a pause, resume or seek is supposed to have — the timeout keeps a mistake in the *order* from becoming a hang. That backstop belongs to the waits on the *state*; a queue's wait has no clock in it at all (I17) | `WakesForEachSurfaceChangeWhilePausedRatherThanWaitingForTheBackstop` (counts the wakings inside one backstop period, so it fails if the signal is dropped) |
| I13 | A warning is a line, not a state: it is raised as it happens, nothing reads a "current warning" back, and what counts as a repeat is decided by whoever raises it | A warning folded into the state event makes every subscriber ask whether the state moved; a warning held as a value makes its lifetime someone's responsibility, and a value nobody clears is a stale one | `WaitsForADeviceThatIsNotMovingAndThenCarriesOn` (one line for the stall, one for the recovery, nothing in between), `PlaysAnAudioFileThroughTheRenderer` (a file that plays says nothing at all) |
| I14 | Only an item of the orchestration queue changes state, moves the clock, or raises an event; the threads that move media only post | This is what makes the state a volatile field instead of a locked one, and what lets a subscriber touch UI state without asking which thread it is on | every pipeline test: they call the pipeline from the test thread and assert on what was announced — and on what a command handed back — never on which thread a callback arrived on |
| I15 | Every call into the pipeline is posted and hands back the task of its own job, carrying whether it could be carried out; nothing inside the pipeline waits for a queue item, no queue item waits for a media thread's progress either, and the only wait in its own surface is `Dispose`'s teardown — a plain wait, because nothing in the teardown waits for something that will not come | A wait inside a queue item — the caller's thread stalls behind whatever is ahead of it in the queue, including another pipeline's work, and a nested call waits for itself. The end of a file used to be exactly that wait, which is why it is now a decision taken from messages instead of a wait taken from flags. A caller awaiting a task it was handed is doing what any asynchronous API allows | `SeeksWhilePlayingAndLeavesItPaused`, `SeeksWhilePausedAndStaysPaused`, `SeeksWhileTheNextFrameIsStillWaiting` (all wait for the seek's task), `AnswersACommandWhileTheSoundHasNotPlayedOut` (a command answered while the tail is still playing out), and every test whose fixture it deletes straight after a dispose |
| I16 | **A command reports; playback fails.** A request that cannot be carried out is answered by the task it handed back and changes nothing else — no state, no event, no error — while `Error` is only ever playback that cannot continue. The one thing a command *can* leave behind is its own state change: a seek that was accepted pauses playback, and if the source then refuses the move (only known by trying, so only known afterwards) the position is simply where it was — a line, not a failure | Folding the two together is how a pipeline dies because somebody dragged a slider past a source that will not move, and it leaves a caller unable to tell "that did not work" from "this is over" | `RefusesCommandsWhenNothingIsOpen`, `ReportsAFailedPlayOnceTheFileHasEnded`, `RefusesASeekTheSourceWillNotCarryOut` (both refusal paths: refused in advance changes nothing, refused when asked was the move's pause), and `ReportsADecoderThatDiesAsPlaybackFailing` for the other side of it |
| I17 | **A queue hands over an entry or says it is closed, and nothing else.** A consumer's wait ends with an entry (media, or the end of the stream), with the close a stop performs, or with nothing at all — a clear leaves it waiting — and there is no clock anywhere in it. Every consumer leaves on the closed word rather than asking again | A queue that woke on a clock is a queue whose missing release is survivable and therefore invisible: the end-of-file word and the "nothing to play" hold were both quietly resting on one 20/50 ms tick, and neither release would have been missed if it had been dropped. With no clock in the wait, what ends it has to be stated and proved — and a release that goes missing is a hang, so each one has a test whose bound makes that a failure | `ReleasesAConsumerWaitingForAnEntryWhenTheQueueIsClosed`, `ReleasesAConsumerWaitingForAnEntryWhenTheStreamEnds`, `DoesNotWakeAConsumerToTellItNothingHappened` (a clear is not a wake), `HandsTheEndOfTheStreamOverAfterEverythingQueuedBeforeIt` (the end is handed over once, and `TryPeek` is how a test asks what is behind), `StopsWhileAThreadIsParkedOnAnEmptyQueue` |

## 11. Changes that look reasonable and are not

| Change | What it will cause |
|---|---|
| Give the packet queue a maximum, or make the reader wait on it | The picture's slowness stops the reader, and the reader is the sound: audio starves behind video. This is the coupling the design removed; it is the first thing to check if a "sound stutters when the video is heavy" report ever comes back |
| Give a queue's wait a timeout so a missing wake is survivable | It reads as a backstop and behaves as a sedative: with a clock in the queue, an ending nobody produced and a sound nobody released both look like "working, only late", which is how the ending came to be earned by an empty queue and how the sound's half of it came to be said by a timeout rather than by the device. The wait now says what will end it (I17), and every release has a test whose bound turns a missing one into a failure instead of a hang |
| Give the audio queue a maximum | Same failure from the other side, and now the picture waits too, because the clock is the sound |
| Make the frame queue discard to make room again | Frames the picture is about to need are thrown away; a slow decoder then *looks* fine (nothing blocks) while the picture stutters, and starvation never triggers because the decoder never falls behind |
| Seek without clearing the queues, "because every item carries its epoch and consumers drop the stale ones" | The tags handle *media*, not *threads*: a full frame queue is where the decoder is parked and a filled packet queue is what tells the reader to idle, so after a seek both sit on the old position — the picture of the new one is not decoded until the picture consumes the old one's queue first, and while paused it is not consumed at all. The clear is what the position moving means to a thread that is parked on a queue; the tags are what it means to the media already inside |
| Drain the decoder when its queue is empty *and* the file has run out at the position the thread asks about, instead of being handed the end of the stream | Two faults in one: the thread has to be *woken* by an empty queue before it can learn the stream ended, and the position it asks about is not the position it holds — between the drain decision and the answer, a seek can move playback, and the drain is then taken as the new position's tail. The end of a stream is a fact about the media that carried it, so it travels with the media (`MediaQueue.EndStream`) and arrives even when the queue is full, which is exactly when a consumer waiting for room most needs it |
| Post a report with the loop's variable instead of a copy of it | The report is read when the queue reaches it, not when it was written, so it says whatever the loop has moved the variable on to: the picture can report the end of a stream for a position it never showed, and the ending it feeds is then decided about the wrong position. A report names a moment, so it carries that moment's value (`endOfStreamEpoch`, `playedOut`, `ranOutAt`) |
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
| Flush the video decoder from the seek's thread | A frame-threaded decoder is not thread-safe for that; the flush happens on the thread that owns it. Nothing needs to *ask* for it either: the epoch is the request, and the owner takes it up — a flag beside the epoch, plus a lock around it, was three pieces of machinery for one fact the epoch already carries |
| Anchor the clock to the seek target rather than the first played media | The picture sits a keyframe behind the sound forever, every frame dropped, the picture never moves again (the S8g symptom) |
| Make the pipeline wait for one of its own queue items "so the call behaves like it used to" | A caller's thread — often the UI thread — stalls behind whatever is queued, including another pipeline's open or teardown, and a nested call would wait for itself. The task every call hands back already lets a caller sequence its own calls, without the pipeline putting its queue inside somebody's thread |
| Present with a sync interval of one, "to keep the picture paced to the display" | The display becomes a second clock the pipeline cannot see, and the wait for it lands inside the presentation thread's frame: at a frame rate equal to the display's that is the thread's whole interval, so each frame's lateness ratchets up by the loop's own cost until the drop rule fires, and the drops are steady from then on. Measured: `render 18.0 ms` and a tenth of frames dropped with a 70 ms lag, on a machine whose renderer does the frame in 0.38 ms. The clock paces the picture; the display only decides what it shows (`Present(0)` — it still blocks when every buffer is in flight, which is the compositor being behind and cannot be helped) |
| Have the queue move the source "so the seek's result can carry what the source said" | That is two threads on one cursor, which is exactly what the media lock was for: the seek then waits for the reader's read (or the reader for the seek's IO), and the answer to a move the queue did not perform is not the queue's to know. The owner moves it; what can be known without moving it — `CanSeek` — is still in the result, and what only a move can reveal is a line |
| Have the queue flush the device "so the seek is clean before it returns" | The device is the audio thread's, and a second thread reaching into it is what makes a device that stopped consuming able to outlast a seek. The queue positions the clock and asks; the owner resets what it holds. The cost is that the reset arrives a few milliseconds later, which is why the clock must not *assume* the reset has happened (see §6) |
| Reposition the clock against a device that may not have been reset (the old `SetPosition`: base set, device position assumed zero) | The clock then reads as the position that was asked for plus everything the device has played since it was last reset — a jump forward of minutes on a real device. The picture follows the clock, so it races: this is what "the video speeds up after a seek" was |
| Report a command that could not be carried out by failing the pipeline (`Error` for a seek the source refused) | A seek a source will not take is a sentence, not an ending: the state becomes unrecoverable for a request that only had to be refused, playback that was carrying on is torn down around it, and every subscriber sees "playback failed" for somebody else's slider drag |
| Treat the position a seek asked for as where playback is | That position is where the file was *sent*: a demuxer lands on the keyframe at or before it, and the sound and picture both resume there. A decision taken against the request — the drop rule skipping frames as "late", starvation holding the clock — acts on a position nothing is playing, which is the picture skipping the keyframe distance and then settling. The request is the host's answer (a paused seek has to show something); every decision waits for the anchor, which is the media's |
| Answer a command with nothing because "the events already say what happened" | A seek changes no state, so the caller that asked has no way to tell a seek that happened from one that was refused — and the pull to fix that by putting the refusal into the state is what produces the row above |
| Keep the end of the file as a boolean that a seek clears | The clear is a write by a thread that does not own the fact, ordered against nothing: a stage can read the boolean and the epoch as a pair that never existed together, report the end of a position that is gone, and — because the flag says "the file is over" without saying *where* — make the next playback believe it too. Stated with its position, the question answers itself and nothing is retracted (§7) |
| Keep a stage's own work-state in a field "so the queue can nudge it" | The nudge is a write by a thread that does not own the state, so the stage may never see it — nothing orders the two, and no barrier is there to help. Worse, the field's name then claims an owner it does not have: the reader's filling latch read as reader-private while the queue also wrote it, until it became a local of the reader's loop, which is what it always was (§2.2). Influence a stage through the change signal or a queue item, never through its own state |
| Inline a call when the caller turns out to be on the queue already, with a flag that says so | The flag has to be right for every path (a callback into the pipeline from an event handler belongs to the item that is running), it turns one rule into two, and the call sites already know: the open calls the seek's core method, the audio thread posts. There is no case that needs it |
| Have the end of the file wait for the tail inside the queue item that hears about it ("so the caller's next command lands after the file finished") | The queue every command is posted to is then busy for as long as the tail takes — seconds — so a seek is answered when the file finished instead of when it was asked for, and the ending becomes a timeout: a sound that has not played out is cut off and nothing says so. Every stage says its own piece to the queue instead, and the ending is the queue's decision over those words (`AnswersACommandWhileTheSoundHasNotPlayedOut` keeps the wait from coming back) |
| Wrap a media thread's loop in `try`/`catch` "so nothing escapes the thread" | Two faults become one: an exception from the pipeline's own code (a bug, which must be visible and reproducible) is reported as a component failure and playback ends as if the source or decoder had broken — and the bug is now unreproducible from the state the user is left in. The guard goes at the component call, where the failure channel already exists (§7) |
| Keep a "the decoder has drained" flag for the queue to poll, beside the messages the stages send | It was two writers (the decoder's thread setting it, a seek clearing it) with no ordering between them, so the older fact could land last and silence the next file's tail — and it existed only to serve the wait the row above removes. The fact belongs to the decoder's thread as a local, and its consequence is a message: the tag that carries a position is how a seek is taken up, never a flag retracted behind a writer's back |
| Change the state, raise an event, or touch the clock from a media thread "because it is the only thread that knows" | The single-writer property that makes the state a volatile field is gone, and every event becomes a thread question for every subscriber. Media threads hand what they know to the queue; that is what the queue is for |
| Tear the playback down automatically when it fails, so `Error` means "stopped and released" | Three costs for one tidy word. The teardown ends in `SetState(None, null)`, which clears the reason, the `MediaInfo`, every diagnostic counter and the clock — so the pipeline can no longer say *what* failed *where*, which is exactly what a report needs; if `Error` were kept instead, `Play` would answer "Nothing is open." rather than naming the failure, because the source is checked first. And the teardown is four joins with **no timeout** on the queue every command is posted to: a failure is the moment another thread may be stuck inside a component (a hung read, a wedged device submit), so automatic teardown turns "one playback failed, and the app was told why" into "no pipeline in the process accepts a command any more". The release belongs to the host, which can time it and can keep the evidence until it does (`StopsReadingOncePlaybackHasFailed` pins what the failed pipeline *does* do: nothing) |
| Have a seek resume playback when it found it playing — the command undoing its own pause | The state stops being a fact about playback and becomes half of one: the same command both changes the state and changes it back, so a host cannot ask for "seek and stay paused" (a drag on a paused player, a preview) without racing that decision, and the user pressing pause *while the seek is in flight* is silently overruled. The asynchronous case is worse: a source that refuses the move is only known afterwards, so the pipeline would have to decide the resume before it can know whether there was a move to resume — which is why a refused seek used to resume anyway. A seek pauses, the host's one `Play` says whether it goes on, and the adapter is where the app's "a jump while playing keeps playing" is restored |
| Decide the ending only while playing, and ask it again when Play is pressed | Two faults: a file that has played out stays *Paused* — the words are already in and only a resume asks again, so the pipeline holds a file nothing will play — and the resume that "fixes" it reports a `Play` that succeeds into an immediate end, which is a caller told it started what it finished. The ending is earned by the stages, so where they were heard is where it is decided; the one state to respect is `Error`, which an ending would replace with a file that merely finished (`EndsWhilePausedWhenTheFileHasPlayedOut`) |
| Signal the threads with the state change, "because a woken thread re-reads the epoch anyway" | It re-reads it *before* the change is complete and then waits again — against a signal that has already been sent. For a wait with a timeout that costs promptness; for the reader once a file has run out, which waits with none, it costs the seek: it wakes, reads the position playback has left, parks, and nothing is coming to wake it again. The window is microseconds and the race is usually won, which is the whole reason to remove it rather than race it — measured by widening the window to 150 ms, where `TakesUpASeekFromTheEndWithoutAnotherCommand` fails on exactly that: the file sits where it was while the clock, the state and the seek's answer all say it moved |
| Show the paused still by peeking at the queue and handing the peeked frame to the renderer | The frame is not the caller's while it sits in the queue, and a seek's clear disposes what is there — so the renderer could be handed memory that is already released, which is a crash whose timing nobody controls. The still takes the frame, renders it and lets it go: the queue hands over ownership or hands over nothing (`TryShowTheStill`) |

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

The same discipline applies *inside* a queue, and it is why a queue's wait has no clock in it (I17): a thread
parked on a queue is released by one of four things and nothing else — an entry arriving, the end of a stream
arriving as an entry, the clear a seek performs (which releases the *producer* parked for room; a consumer has
nothing to do about a seek and stays waiting), and the close a stop performs, which releases both ends. A
freeze with a thread inside `MediaQueue<T>.Dequeue` or `TryEnqueue` is one of those four going missing, and
each of them has a test that fails rather than hangs.

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
| `IsStarving` / `StarvedFor` | time is deliberately stopped, and for which stage | "video" ⇒ a slow decoder is pausing playback (expected under load); "audio" ⇒ the sound has nothing to play *and* the device has nothing left in it, so the pipe behind it is what is not feeding the sound |
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
    had, and the shape to delete: the end of the file was already the statement, and the queue's
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
18. Is a request to a stage — flush yourself, forget what you were producing, take up this position — carried
    by a flag beside the fact that already says it? The epoch *is* the position moving, so the request is the
    epoch and the stage's memory of having honoured it is a local of its loop; a flag beside the epoch needs a
    lock to be safe, and gives the fact a second writer (the seek, retracting it) whose write can land on top
    of a newer one. The video flush and the decoder's tail were exactly that pair.
19. Does a decision read the clock, and is the clock a fact about media at that moment? It is one only after
    the first media of the position has anchored it; before that it reads the position a seek *asked for*
    (§5). A frame judged "late" against the request skips the keyframe distance, which is why the drop rule
    and starvation both wait for the anchor.
20. Is a statement about a position — the file has run out, the device is holding a position — being kept as a
    *boolean* that something has to retract when playback moves? State it with the position it is about
    instead: a tag compared with the epoch the reader is working at (`IsEnding(epoch)`), or a word filed under
    the position it was said about (`_videoRenderEndedEpoch`), which needs no clearing on a seek, no ordering
    rule between the tag and the epoch, and no stop-me flag beside it — a stop advances the epoch and every
    in-flight statement about the old position is stale by construction. The end of a *stream* is the same
    rule one level down: it travels as an entry of the queue it belongs to (`MediaQueue.EndStream`), behind the
    media and carrying the position, so what a seek makes of it is "old tag", not "something to retract".
21. Does the change tag something with a position — a frame, a buffer, a request, a report — and where does
    that position come from? It must come from **the thing that produced it, or from the position that thing
    was made to belong to**: the packet's own epoch for a decoded frame, the reader's carried-out epoch for a
    read, the entry's own tag for a drained tail, and for the decoder's output the position the decode thread
    named when it made the decoder belong to one (`EnsureTheDecoderHolds` — the packet's epoch for a decode,
    where playback is for an idle flush). Never from a fresh read of the epoch: it may already be a position
    the producer has not been let go of, which is how a tail of the old position gets presented as the new
    one's. Consumers can only catch the *older* direction of that mistake, so the tag has to be right by
    construction rather than checked. And a tag that is *posted* is a copy of the value, never the loop's
    variable for it: the loop carries on, and a report read later than it was written says something about a
    moment that has passed.
22. Does the change call a component — the source, a decoder, a renderer, the clock? That call is what gets a
    guard, and nothing else does: an exception out of a component is the failure it would otherwise have
    returned, and it is reported through the path that failure already has. The thread's loop is **not**
    wrapped, because an exception from the pipeline's own code is a bug that has to stay visible instead of
    being reported as a decoder that died (and an unguarded component call takes the process with it, which is
    the other half of the same rule). The audio thread's one method-level catch is the deliberate exception:
    its interop surface includes property reads — device buffer fill, the device clock — that have no call to
    guard and no failure channel to use.
23. Does the change leave a thread parked on a *queue* while the position moves? A queue paces a thread and
    knows nothing about positions, so a seek has to **clear** what the old position produced — that is what
    releases the decoder waiting for room and the reader idling on its read-ahead — and only then do the
    consumers' tag checks matter for the media already in flight. A seek that only advanced the epoch
    releases nobody, and with the picture paused nothing else ever will (§3, §6, §7).
24. Does the change move a position — the epoch, or anything that belongs to it? The whole change is one
    `SeqLock<long>.Change`, and it is the same value a media thread reads (`Read()`) without taking a lock. The
    bracket is not for tidiness: two separate steps mean a thread can act on the new position between them and
    produce media the second step deletes — measured at 48 packets (with the keyframe) when the window was
    widened to 300 ms. Tags tell a thread what is stale; only the reset tells it what is gone.
25. Does a decision ask about the *state* when the fact it decides is about the media? The ending is earned by
    the stages saying they have nothing left, so it is taken wherever those words are heard: a pause is not a
    reason to keep a file open, and a decision that waits for a particular state needs a second place to ask
    it again when that state arrives — which is a second place the ending is decided from, and the reason a
    file paused on its last frame used to stay open. The one state such a decision must respect is the one it
    would overwrite: a failure is a reason, and nothing turns `Error` into a file that merely finished.
26. Does a thread *do* something unless a state says otherwise? Do-nothing has to be the default, and work has
    to be checked for in the positive direction: name the states the work is *for* (the reader reads for
    playing and paused; the picture shows for playing and nothing else) rather than excluding the states it is
    not for. A state added later then gets the safe answer by construction instead of being one forgotten
    `!=` away from a thread doing work nobody wants — which is how a failed playback ended up still filling a
    read-ahead and polling the queues every 15 ms for a consumer that could not come.
27. Does a thread wait on a queue that no clock can release? Then what releases it has to be one of the four —
    an entry, the end of a stream, the clear a seek performs (the producer's, never the consumer's), the close
    a stop performs — and the change has to leave a test that *fails*, with a bounded wait, if that release is
    dropped. A wait with no clock behind it does not degrade: a missing release is a thread that never moves
    again, so either the release is proved by a test or it is not there (I17; `MediaQueueTests`, and
    `StopsWhileAThreadIsParkedOnAnEmptyQueue` one level up).
28. Does the change wake the threads itself, or rely on a state change to do it? A wake is a statement that
    the change is *complete*, so the caller sends it when the position, the state and the queues have all
    moved — never from inside `SetState`, which runs before the caller's own work. And is there a wait that
    only this wake can end? The reader parked at the end of a file has no timeout, so a signal sent too early
    is not a late seek: it is no seek at all (`TakesUpASeekFromTheEndWithoutAnotherCommand`).
29. Does a paused thread show, or hold, something that belongs to the position? Then it must take what it
    renders from a queue that hands ownership over — never a peeked, borrowed frame — and it must record what
    it showed, so a pause that moved nothing does not show it again (the still, §4.3; I11).

## Appendix B — where each concept lives

| Concept | Where |
|---|---|
| The orchestration, threads, state machine, starvation, device watch | `PlaybackPipeline` |
| What a command may fail at, and what that failure does (nothing but answer) | `PlaybackPipeline`'s `PlayCore`/`PauseCore`/`SeekCore`/`StopCore`, each returning a `PlaybackResult`; `IMediaSource.CanSeek` for the refusal a source can predict |
| The one queue that decides, and what may run on it | `PlaybackPipeline` (the static queue), `TaskDispatcher` |
| "No maximum", and why | the two `MediaQueue<T>`s built without a limit in `PlaybackPipeline` |
| "Wait for the picture, keep everything" | the one `MediaQueue<T>` built with a limit |
| "The queue knows nothing" — not what the tag means, and not what the end of a stream means either | `MediaQueue<T>` (items, tags, a limit, a weight, a close, and an end entry that is handed over like any other) and the checks at both its ends in `PlaybackPipeline` |
| The end of a stream, and why an empty queue is not one | `MediaQueue<T>.EndStream` and the `isLast` of `Dequeue`; said by the reader (packets, audio) and by the video decode thread (frames, in `DrainVideo`) |
| What releases a queue's consumer, and what does not | `MediaQueue<T>.Dequeue` (one wait, an entry or the closed word, no clock), `Clear` (releases the producer parked for room; a consumer keeps waiting), `Dispose` (the close that releases both ends) |
| The end of a file, and what earns the ending | `PlaybackPipeline`: `_endingEpoch` (the position the file ran out at), `_videoRenderEndedEpoch`/`_audioRenderEndedEpoch` (what each stage said about which position), and `TryNotifyPlaybackEnded` — the one decision, taken when the last word arrives and nowhere else |
| Epoch discipline and what a seek uses to interrupt | `PlaybackPipeline`: the epoch, the position published with it, and the reset of everything that belongs to a position are one `SeqLock<long>.Change`; media threads read it through `_epochs.Read()`. The protocol itself — the three reads, the retry, the wait for a change that outlasts the attempts, and how a value of several fields survives it — lives in `Internal/SeqLock.cs` |
| When it is too late to show a frame | `ISynchronizer` (default: 2 ms early tolerance, 40 ms late) |
| What a paused file shows, and what wakes that thread | `PlaybackPipeline.VideoRenderCore`'s paused branch and `TryShowTheStill` (`shownEpoch` is the position whose picture is on the screen, `endedForEpoch` the picture's own word about a position with nothing to show) |
| Who wakes the threads, and when | `PlaybackPipeline.SignalPlaybackStateChange`, called by each command when its own change is complete (`PlayCore`/`PauseCore` after the state, `SeekCore` after the epoch and the clears, `StopCore` before them), by `Fail`/`TryNotifyPlaybackEnded`, and by the surface event — never by `SetState` |
| The clock contract, and the wall-clock fallback | `IPlaybackClock`, `StopwatchClock` |
| Device position as the source of time, and where a reposition takes its zero point | the audio renderer's clock (`WasapiAudioClock`), which the device's own thread resets (`WasapiAudioRenderer.Flush`) |
