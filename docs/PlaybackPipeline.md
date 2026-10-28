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
6. **Nothing here polls a media queue, and each thread sleeps in exactly one place.** Every thread waits on
   its own `PipelineNode` — one condition variable per thread — and nothing else: no thread sleeps inside a
   `MediaQueue<T>`, no thread sleeps on another thread's node, and no thread sleeps on a lock it shares with
   anyone. A thread with nothing to do waits for a **wake**; a thread whose answer also depends on time waits
   for a **fixed cadence** on that same node. Only three waits are paced by something the pipeline cannot
   signal, and each is named: a frame's moment and the picture's starvation threshold (time), and the device's
   room and drained state (a device that cannot call back). Everything else is woken by whoever changed
   something the thread reads (§2.3).
7. **A wake must never be missed, and the pattern that guarantees it is the order of the call:** a thread
   reads its node's stamp *before* it looks at anything, then waits only when it finds nothing to do. A wake
   arriving while it decides has already moved the stamp, so the wait returns at once; a wake arriving after
   is a pulse with a waiter in place. Getting this order wrong is now a hang rather than a delay — there is no
   backstop to hide it — which is why every edge in the wake table has a test whose bound turns a missing wake
   into a failure. A wake may be *spurious*: a thread that finds nothing to do sleeps again and wakes nobody,
   so a generous wake costs one iteration rather than a spin.

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
  and the rule is flat: it leaves the state as it found it. A file that was playing goes on playing from the new
  position, and a paused one stays paused — a jump is not a pause, because pausing is its own command and a seek
  that paused would overrule a user who pressed pause while it was in flight. The one state that moves is `Ended`:
  a seek from the end takes the file back into itself as `Paused`, since there is nothing to play from the end
  until it is told to. Nothing runs ahead of the media for it — a position with nothing yet to show is a position
  the render threads hold the clock for (§8).
- **What playback did is announced, and the events are the shared account.** A state change carries the state
  and the error (`StateChanged`), and something that did not stop playback is one line (`WarningRaised`). A
  command's own outcome is the one thing that is *not* an event: it belongs to the caller that asked, which is
  also why a seek needs no event of its own: the only state it can change is a file taken back from `Ended`.
- **`Dispose` is the one call that waits.** A disposed pipeline must not still hold the file open — whoever
  opens it next would not be able to — so teardown is posted and waited for, with a bound
  (`TeardownWaitMilliseconds`) because a teardown that cannot finish is a broken pipeline, not a slow one.
- **The threads that move media only post.** The end of the file, a failure, a stage with nothing to play, a
  warning about the device: each is handed to the queue as an item of its own. They never change state, never
  raise an event, and never wait for an item to be done — so "the state machine is only touched on the queue"
  holds by construction rather than by care.
- **The state is therefore a volatile field, not a locked one.** There is exactly one writer, and a reader of
  `State`, `Error` or `Position` wants the value it sees now rather than a consistent picture of everything.
- **A state change tells subscribers; the caller wakes the threads.** `SetState` moves the state and raises
  `StateChanged`, and it does not wake anyone — because what a woken thread must see depends on the caller. A
  seek is the case that matters: it is a state change *and* a position change, and a thread woken between the
  two reads the position playback has left and waits again (for the reader, parked without a timeout once the
  file has run out, that is the seek never happening at all). So the caller wakes when the change it is making
  is complete — `WakeAll` once the epoch and the clears have moved; the open not at all, since it starts its
  threads afterwards; the stop before its state, because what releases those threads is the flag and the
  closed queues rather than the state it ends in. The clock is not re-decided here at all: the two render
  threads decide it from what they read, which is why nothing has to be pushed at them (§5).
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
| **The position change, as one fact** — the epoch, the position a seek asked for published with it (`_pendingSeekPosition`), and what belongs to a position: the queues' contents | the queue, inside the change, and nothing else ever | the queue; and the media threads through `_epochs.Read()`, without the lock | a **seqlock** (`SeqLock<long>`), because it is a compound update several threads act on: a thread that reads the epoch must not be able to see the new position while the queues still hold the old one's media, or the reset that follows would delete media it produced for the new position. The readers are the reason it is not a plain lock: the epoch is read per packet and per frame on three threads, and a seqlock makes those reads a sequence check rather than a contention point — while a plain lock would be taken and released for every one of them. A reader that sees the change still running reads again, and waits for the writer's own lock if the change outlasts its attempts |
| **Open state** — the source, the decoders, the renderer, the three queues, the four threads, the streams, the info, the clock object | the orchestration queue, **only while no media thread is running**: before they start, or after they are joined | the media threads and the host | the invariant above, which is why there is no lock around any of it. The audio renderer is asked of its factory per open and disposed after the joins, so a pipeline owns exactly one device stream at a time — two pipelines playing at once hold two, and nothing is shared between them but the endpoint's session (§14) |
| **One owner, no lock** — the media cursor (`IMediaSource`) and the audio decoder that rides on it belong to the reader, which reads, moves, flushes and drains them and nothing else touches; the video decoder belongs to the decode thread, which flushes it and forgets its tail when it sees the epoch move | the owner, and only the owner | the owner | nothing: a lock around a cursor two threads move was the thing this replaced. The flush is not requested with a flag either — the epoch *is* the request, and the owner's memory of having taken it up is a local of its loop |
| **The queue's own decisions** — the state and its error, `_disposed`, `_stopRequested`, the position the file ran out at (`_endingEpoch`), the position each stage has said its stream is over for, and the position a seek asked for | the queue, and only the queue | the queue, except where a media thread needs the value *now* | one writer, no compound update, nothing shared with a second field: `volatile` where a media thread reads it, `Interlocked` for the tag that has to be long, and nothing at all where only the queue does. A stage's word is filed by the queue and never read back by the stage itself — it is said once, when that stage's stream ends |
| **The clock's inputs, owned by the render threads** — whether a stage has nothing to play, and the position whose clock the media has claimed | the two render threads, under `_clockLock`, and only them | both render threads, each reading the other's verdict; and the host, for `StarvedFor` | `volatile` for the verdicts and the claim, so noticing that one of them changed is a read rather than a lock entrance; the lock is entered for the decision itself. Each flag has exactly one writer and the claim is a comparison rather than a flag — a position nothing of the media has claimed is one nothing was reset for |
| **Thread-owned** — the audio thread's device state: the offset that relates the device's clock to the pipeline's, and the hand-off it is carrying | that thread while it runs | that thread | a local of the loop where nothing outside needs it — the offset and the hand-off are, which is why a seek only has to move the epoch for them to be let go of and re-taken. The device's own clock is reached through the renderer, and the renderer belongs to that thread |
| **Published by a stage** — whether it has nothing to play, which is also one of the clock's inputs, how far the reader has taken each stream in for the position it is reading, and the three things a stage says about the end of a file (the decoder has produced its tail, the picture has shown everything, the sound has played out) | the stage writes the verdict for the position it is looking at, the reader writes its two marks under the position they belong to, and the queue owns what the end-of-file words become | the queue (for the words), and both render threads and the host (for the verdict) | `volatile` where a media thread reads, and no copy kept by the stage: what a seek makes meaningless is retracted where it is owned. The end-of-file words are messages rather than flags, which is why a stage never reads another stage's; the reader's marks are numbers stamped with their position, which is why a stale one is a mismatch rather than a value |
| **Counters and hand-offs** — presented/dropped/displayed frames, the tick totals, the last presented picture's timestamp | any thread | anyone, including the host | `Interlocked` |
| **The clock object** | the two render threads, and only them: one places the position a seek asked for, while nothing of the media has claimed it; one claims it with the first media that plays; and either starts and stops it | every thread reads its position | the clock implementation must be safe for that on its own — a read is a read from anywhere, which is what lets the host ask for the position without waiting for a render thread — and the writers are serialized by `_clockLock` |

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

### 2.3 Who wakes whom

Each thread sleeps in exactly one place — its own `PipelineNode` — so a thread can only be moved by a wake sent
to its node. A wake is either **data** (an input the thread reads has changed, and a cheap predicate worth
sending for) or **control** (the state, the position or the stop flag moved, which no queue level can describe,
so every thread is woken and one that finds nothing to do sleeps again). No wake is ever sent while holding a
queue's lock, and a wake never waits for the woken thread.

| Fact that changed | Written by | Wakes | Kind |
|---|---|---|---|
| a packet was queued, or the packet stream ended | reader | video decode | data |
| an audio block was queued, or the audio stream ended | reader | audio render | data |
| a packet was taken | video decode | reader | data |
| an audio block was taken | audio render | reader | data |
| a frame was queued, or the frame stream ended | video decode | video render | data |
| a frame was taken (room) | video render | video decode | data |
| the state, the position, the stop flag, or the ending changed | the orchestration queue | **all four** (`WakeAll`) | control |
| the presentation surface changed size | the host's thread | video render | control |
| a frame's moment arrived, or the starvation threshold passed | time | — | **fixed cadence** |
| the device made room, or the sound is waiting for it | the device | — | **fixed cadence** |

The two cadence rows are the only waits that are not woken: a fact that no writer in this process can signal.
Each is a constant on the waiting thread's own node (`VideoIdleTimeoutMilliseconds`, `AudioIdleTimeoutMilliseconds`),
and neither is derived from the clock's state — a cadence computed from
whether the clock is running, claimed or at a particular rate would be a prediction whose subject belongs to
another thread, and a prediction that turns out wrong is a hold that never arrives.

**A data wake says "there may be work", never "something was asked of you".** Everything on the control rows reaches
every thread through one `WakeAll`, which is why no `TryWake*` looks at the stop flag: a stop, a seek, a resume and an
ending are announced once, by the queue thread, after the fact they announce has been written. The order matters and is
the queue's to keep — set the flag, then wake — because a thread that sees the wake and then reads a flag still set to
`false` would go back to sleep having been told to stop. A data wake during a stop is therefore neither needed nor
harmful; skipping one costs nothing, and one that happens costs a thread that is leaving one last iteration.

Two rules make the table sufficient, and both are checklist items (§Appendix A):

1. **Read your node's stamp before you look at anything.** A wake that lands between the read and the wait is
   what the wait itself notices; a wake that lands before the read is visible in the facts.
2. **Every `Try*` queue call is followed by the other end's wake**, after the queue's lock has been released.
   The queue never wakes anyone itself — it has no waits, no timeouts and no pulses left.

A hold on the clock is enforced by the same rules and nothing else: the decision pauses the pipeline's clock, the
audio thread — the only one that touches the device — sees that on its next look and pauses the device with it,
and what ends the writing is the device's buffer filling up — the hand-off then says it has no room and the
thread waits on its own node for the verdict to change. No component may release a hold by starting the device on
its own (I19): a hand-off into a stopped stream leaves it stopped.

The second cadence row is also what makes a device wait reachable: the sound's hand-off *returns* when the
device is full rather than waiting inside the renderer (I20), so the sleep that follows is a sleep on the
thread's own node, and a pause, a seek and a stop all end it. A wait inside the renderer would end only when
the device changed its mind — which is exactly the case where it never does.

### 2.4 What a package is compatible with

The epoch says *what a package is a package of* — the position it belongs to, and what the cursor must do about it. It
deliberately does not say *which component may consume it*: that is a different question, and answering it with the
epoch would rebuild a decoder on every seek. Component identity is carried separately, as one number per component
in `PipelineVersion` (`Source`, `AudioDecoder`, `AudioRenderer`, `VideoDecoder`, `VideoRenderer`):

| Who publishes | When | What it does |
|---|---|---|
| the queue thread | a file is opened | publishes the source, with the two streams it will be read for, under the first number |
| the queue thread | a track is switched | republishes that holder with the new selection, under the same new number as the chain it rebuilt (§2.5) |
| the queue thread | a chain is (re)built | raises the two halves of that chain to `highest in use + 1` when both are new, and gives each new slot the snapshot of every number in use; a lone replacement joins the generation already running, so a repair does not make the frames in flight belong to nobody |

- **No counter is kept for the next number.** "The highest in use" is derived from the components themselves — the
  source and both values of each of the four role slots — so there is no field beside them that could disagree
  (§15).
- The two halves of a chain are **one generation**: a renderer exists to draw the frames of the decoder published
  with it, and the device exists to play what its decoder produced. That is why they are raised together.
- **The source and a decoder are two nodes.** The source a package came from travels as a `MediaSourceHolder` — the
  `IMediaSource` and the two streams selected from it — so *which file* and *which tracks of it* are one identity
  rather than two facts that could disagree. A packet leaves the reader stamped with the source it came from, and
  both the audio decoder (which reads the cursor itself) and the video decoder check that stamp before decoding —
  the same comparison a renderer makes, against a node one step further back. A source that is replaced can
  therefore never feed a decoder that was built for the one before it.
- **A package is stamped with `Max(package, self)`** when a node hands it on: it keeps what it has met and gains
  this node's numbers. A member neither side carries stays zero, so a frame leaving the decoder says nothing about
  the renderer, and a decoder's numbers say nothing about a device they have not reached.
- **Zero means "not visited"**, never "version zero".
- A node compares the source and its own chain's numbers (`CompareVideo` / `CompareAudio`): a picture package says
  nothing about the sound, so the sound's numbers are not its business.

For a node B between its upstream components A and whatever consumes it, in this order:

| # | Test | Verdict |
|---|---|---|
| 1 | `package.B > self.B` | **block**: the package expects a newer B. Its replacement has already been published, so the next turn installs it and the package is taken then |
| 2 | any upstream A with `package.A < self.A` | **drop**: the package was produced by a generation this node has left |
| 3 | otherwise | **accept**, and the package is stamped `Max(package, self)` when this node hands it on |

Order matters: blocking wins over dropping, because waiting costs nothing while giving a package up is lossy.
Two directions are deliberately *not* verdicts: a package naming an **older B** is accepted — a node's own number
only travels in its own pair's packages, and a pair is published together, so an older number there means the
package is from a generation this node has left, which rule 2 reports through the partner's number. And a package
from a **newer A** is accepted — the pair that produced it carries that generation for B as well, which is where
rule 1 fires.

**There is no wildcard.** A rule with `0`-means-not-visited would have to be written for every field, so the
model instead requires that **every package carries the whole snapshot of the component that produced it** —
including the end-of-stream markers, which are packages like any other. A producer therefore always stamps its own
chain's numbers, and the only zero any node can meet is its own field on a package it has not produced yet, which
rule 1 (a `>` test) can never turn into a drop.

The block is safe because a version number can only come from a publication, and a publication is complete before
anybody is woken: it puts the component in the role's slot, and only then does `WakeAll` run — so a node that finds
itself behind is never waiting for something that is not already on its way. The epoch is checked first, because it
also carries the obligation to move the cursor; the versions are checked second, because they only say whether this
stage may touch the package at all. A node that has no component of its own yet (`default` snapshot) compares
nothing, which is why the first publication happens before the threads are started.

**How a component reaches its thread: one slot per role, `Publish` and `Update`.** There is no "current" field with
a "pending" field beside it — one `ComponentSlot<T>` per role, alive for the pipeline's whole life, holds both:

| Call | Who | What it does |
|---|---|---|
| `Publish(component, signature, versions)` | the queue thread | makes `component` the one the owner adopts next; a component published earlier and never adopted is released here, because the queue built it for a change the owner never got to. Publishing **null** is how a role is emptied — the owner adopts nothing and disposes of what it held (§2.5) |
| `Update(out newComponent, out oldComponent)` | the thread that owns the role | adopts the published component and hands back both what the owner now holds and the one it replaced, which the caller releases — it is the thread that was using it. With nothing published, `newComponent` is what the owner already holds and nothing changes |

The four worker threads keep the component they adopted in a local of their loop (`decoder`, `renderer`, `source`,
`audioDecoder`) and never read `Current`: what they decode, flush, render and submit is the component *they* took
up, not one the queue has published behind their back. `Current` is for the queue thread and the host — a name to
report, a device to flush at a stop — where "the component in effect" is exactly the right question.

**Publication needs no lock of its own, and has none.** `Publish` and `Reset` happen only in commands on the
playback queue, and the queue runs one command at a time, so the five publishes of one `BuildChains` are
serialised by the queue rather than by a lock around them — which is also what lets `CurrentVersions()` read
five slots, take the next number from that reading and publish under it without racing another publisher. A
worker thread must never publish: two of them reading the highest number in use would give two generations the
same number, and "a decoder and its renderer are one generation" would stop meaning anything.

The worker threads take no shared lock at all — their adoption is the slot's own lock — which is the other half
of why a lock around the publication would buy nothing: it could not make a publication atomic against a thread
that never enters it. What makes a publication that is half-seen harmless is publish-before-wake plus the number
the holder shares with the pair it was published with (§2.5).

A slot answers two different questions on purpose: `Component`/`Signature`/`Versions` are about the component **in
effect** — the one just published, or the one the owner still holds until it adopts the new one — which is what the
queue and the host ask about; `Current` is what the owner has actually adopted. The worker threads use neither: they
hold `newComponent` from their own `Update`. `Reset()` releases both, and is called only while no thread is running.

### 2.5 What opening does not decide, and what a selection republishes

**Opening a file chooses nothing to play.** `Open` opens the source, reads the stream listing and publishes a holder
with **no streams**, so nothing is created and nothing is read: no decoder, no device, no read-ahead, no frame
decoded. That is deliberate, because the streams a host chooses from are only known once the file is open — a
pipeline that guessed "the first of each kind" would build, configure and decode tracks the host is about to replace,
and a video track switch builds a *new* renderer (and so a new graphics device) because the chain must be able to
re-decode. A file opened onto a track menu should cost the container and nothing else.

The consequence is a two-step start, and the host owns the second step: `SelectStream(kind, stream)` chooses one
kind — with a null stream turning it off — and the host calls it once per kind it wants. `Play` is refused while
both are unselected — played anyway, the clock would run, the position would advance and no stage would ever say a
word, because the reader has nothing to read for and the ending is a statement about tracks that were never chosen.
A file opened and never chosen from holds its source (and the file) until it is stopped or disposed of.

**A kind is chosen on its own, and its failure is its own.** A chain that cannot be built is neither a warning nor a
half-published selection: the whole command fails, nothing is published, and the kind that was already playing is
untouched. That is what makes the host the one that weighs the two — a file whose device is missing is still worth
watching, so a host plays the picture and reports the sound, and a host that would rather refuse the file refuses it.
The alternative (build what can be built and warn about the rest) was tried and removed: it left a holder naming a
stream with no chain to play it, which fed that stream's packets to the decoder of the stream it replaced (§2.5).

The selection itself lives with the source, not beside it: `_source` holds a `MediaSourceHolder` (the file and the video and
audio streams selected from it), so the reader routes by the streams of the holder it adopted. A switch therefore
publishes a new holder, and it does so under **one new number shared with the chain it rebuilt**:

| Published | Number | Why |
|---|---|---|
| the holder, carrying the new selection | `highest in use + 1` | the selection is new, and this number is the generation built for it. It is published whenever the comparison says anything changed, including the first publication of a file |
| the decoder and renderer of the switched kind | the same number | they are one generation (§2.4), and it is the generation that goes with this selection |
| the untouched chain | unchanged | rule 2 of §2.4 drops only *older* upstream numbers, so a newer source is accepted by the chain that was not rebuilt: it keeps playing the media it already has and never notices |

**The read-ahead moves with the selection, in the same act.** `BuildChains` also sets the audio minimum from the
stream it was given — after everything is resolved, so a failed build cannot re-pace a chain that is still the one
playing, and before anything is published, so no thread can adopt the new selection and pace itself by the old
stream's numbers.

**What to rebuild is read from the holder, not passed beside it.** `BuildChains` takes the holder and compares it with
the one in effect: the file, and each kind's stream by its container index. A kind whose index changed is built
afresh — the point of a switch is a decoder built for a *different stream* even when the two streams happen to share
codec parameters, and a signature match would quietly keep the decoder that was reading the old track; a kind whose
stream went away is emptied; a kind whose stream is unchanged keeps what it has, the signature deciding whether
anything needs repairing (`Reconcile`). The comparison is the same one `SelectStream` already makes, so it is made in
one place, and a caller cannot ask for a publication that disagrees with the holder it hands over — the earlier
shape, a "rebuild this kind" flag plus a "renew the source" flag, was a restatement of the holder that could be
passed wrongly (and once was `true` for an unchanged holder, which cost the reader an epoch it could not see).

Two consequences worth stating, because both are easy to get wrong:

- **The position does not move.** The switch publishes the position it already had, with `SeekMode.Precise`, so the
  media resumes at the switch and not a frame earlier — the same epoch change also clears the queues, which is what
  releases the threads parked on them. A switch is therefore a seek in the only sense that matters here: freshness
  comes from the epoch, compatibility from the version (§2.4).
- **The holder does not own the source.** `MediaSourceHolder.Dispose` is empty and `StopCore` releases the file it
  finds in the current holder, because one source outlives every selection of it: disposing it as a holder is
  replaced would end the file the new holder is still reading.
- **A kind can be turned off, and that is a publication like any other.** `SelectStream(kind, null)` publishes a
  holder without that kind's stream and, in that kind's two roles, publishes **nothing** — the same two publishes as a
  switch, with no component to put in them. The owner adopts the emptiness and disposes of what it was holding, which is
  the only safe way to take a component away from a thread that is using it (§2.4). Nothing about the mechanism is
  special: turning a kind off is a pending change of that kind's two roles, so it is marked where a rebuild is marked,
  published where a component is published, and takes the publication's number along with the selection it belongs to.
  The other kind is untouched and carries on; when the last kind is turned off the file returns to the state an opened
  file is in — paused, with nothing to play — because a clock left running would be advancing over nothing. Subtitles
  are the exception at the surface: asking for one is refused, while turning subtitles off is trivially satisfied,
  since they were never on.
- **The surface is given back when the picture is turned off.** A renderer paints into a surface the *host* owns, so a
  frame it has presented stays there until something paints over it — and once the picture's renderer is let go of,
  nothing ever will: the last frame would stand on screen for a file playing its sound without a picture. The render
  thread, which is the one that owns the renderer, therefore empties the surface with `ClearSurface` **on the way out**,
  at the only moment it exists and may be used, and only when the renderer is handed back rather than replaced — a
  replaced renderer is drawn over by its successor, and a black flash between two pictures of the same film would be a
  worse answer than the frame it hides. The clear also forgets the frame a redraw would use, so a surface resize or a
  pause cannot put it back. A renderer kept because the picture ended (rather than because it was turned off) is not
  cleared: the file still holds a picture, and the last frame of it is the truest thing on screen.

Nothing outside the reader side of the pipeline changes, because the switch is expressed entirely in what is
published: each render and decode thread takes up its new component from its own `Update` and judges the packages
in flight by the rules above, rather than by a case that knows a track was switched.

## 3. The three hand-offs

There are exactly three queues, and each has a different policy because each is there for a different reason.

| | Packet queue | Audio queue | Frame queue |
|---|---|---|---|
| **Between** | Reader → video decode | Reader → audio sender | Video decode → presentation |
| **Holds** | encoded video packets | decoded audio buffers | decoded video frames |
| **Maximum** | none | none | none in the queue: the decoder paces itself by a window of 8 before it takes a packet |
| **A full window means** | — | — | the picture is behind, so the decoder stops taking packets |
| **Producer waits?** | never | never | yes, on its own node, until the picture takes a frame or the position moves; the queue itself never makes anyone wait |
| **Consumer waits?** | never in the queue: it sleeps on its own node until the reader wakes it with an entry (a packet, or the end of the stream) | the same, woken by the reader | the same, woken by the decoder — and, with nothing in hand, by the clock's cadence |
| **On a seek** | cleared: what it holds belongs to the position playback has left. The clear wakes nobody — the position moving is what wakes the threads (`WakeAll`) | cleared, for the same reason | cleared, for the same reason — which also frees the decoder, whose window the clear empties |
| **At end of file** | the reader says the end of the packet stream with an entry of its own, after the last packet; the decoder takes that entry, produces its tail into the frame queue, and says *that* stream's end behind it; the pipeline's own statement — the file ran out at this position (`_endingEpoch`) — is the third thing the reader does, and the ending is decided on it | the reader says the end of the audio stream with an entry, after the last block (the drain above is what produces it); the sound says it has played out when the device holds nothing after that | the decoder says the end of the frame stream with an entry behind every frame it produced; the picture says it has shown everything when it takes that entry while holding nothing — and an empty queue before it means the decoder is behind |

The three are one class, `MediaQueue<T>`, given one thing each: nothing, a way to weigh items in samples, and
nothing. The window and the read-ahead minimums live at the ends that act on them, and the queue enforces
nothing; it also waits for nothing — see §2.3.
The queue itself knows nothing about epochs — it carries a tag,
decided by the producer that writes it and the consumer that reads it — and it knows nothing about what the
end of a stream *means* either, though it can be told that one has happened: `EndStream` puts an entry behind
the media, and the entry is taken like any other, with no media and a flag. That is deliberate and is the one
thing an empty queue cannot say: "empty" and "over" are different facts, and only the producer knows which one
it is leaving behind. What a seek does to them is `Clear`: drop what they hold, releasing nothing and waking
nobody — the threads are woken by the position moving, and the decoder, whose window the clear empties, is
woken the same way. The end entries go with it, which is what makes them meaningless without anything having
to retract them: what the producer hands over next still carries its own epoch, so nothing stale is taken for
new.

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
stage having to make progress.** These are the rules; §4.1–§4.6 are why each is written the way it is.

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
again. A holder with no streams in it is trivially at its minimums, so a file that is open and unselected is a file
the reader does not read: what is not going to be played is not read ahead. The minimums are *read-ahead*, not
capacity: 500 ms of audio samples — of the *selected stream's* own samples, so the number is set with the selection
(§2.5) — and 64 video packets, waking at 250 ms and 32. A queue is paced only when it can fill *and* drain, which takes
**both** halves: the kind is being read — the selection has a stream of it — *and* a decoder exists, because the audio
decoder is what puts samples in the audio queue while the video decode thread is what takes the packet queue back
down. Either half alone can hold while its queue cannot reach the minimum: a kind turned off leaves a decoder in
place for a stream nobody reads, and a stream whose decoder could not be built is the mirror image — since a build
that fails publishes nothing, that one is not reachable today and is exactly what a partial publication would create.
In either case the reader would never settle into its paced read and would read the file to its end in one go.
Neither the file's stream list nor the components can answer this alone: it is a question about a queue that
something fills and something drains.

**It reads for a playback that is playing or paused, and does nothing in any other state.** It is the one
thread that works while paused — the read-ahead is what a resume starts on — so those two states are what its
work is checked against, in the positive direction: a stopped, ended or failed playback has nothing to read
*for*, because nothing will consume what it produces and the position it would fill will never be played. A
state added later therefore gets the safe answer (park) without anyone having to remember to exclude it
(Appendix A item 26).

The reader has **no drop path at all**. The single case in which a read packet is not handed on is a seek that
moved the epoch while it was being read — work for a position nobody wants — and that is the seek's doing, not
a policy of the reader's.

**It moves the cursor by asking the source to land by a stream**, not by asking for a position and accepting
whatever the container falls back to (§6). The picture is what a landing is measured by while it has media at the
position — its keyframes are the only ones there are, and the media it lands on is the lead-in the decoder needs —
and otherwise it is whichever of the two streams reaches furthest, because sound has no keyframes and lands within a
packet of the position. That choice is a fact about the file rather than about the request, and it is made where both
are known: against the lengths the container declares for the picture and the sound, which is also what says whether
the position is one the picture can reach at all.

*Forbidden by this rule:* any maximum on the packet or audio queue; any drop on overflow; asking the reader to
wait for a decoder, a renderer or the device.

### 4.2 The video decoder drops nothing it could decode, and never consults the clock

It takes a packet, decodes it, and hands the frames to the frame queue; if there is no room it waits there.
Two things it deliberately does *not* do:

- **It does not discard frames to make room.** Discarding the oldest frame throws away exactly the frames the
  picture is about to need — and makes a slow decoder look like a rendering fault rather than a slow decoder.
  A decoder that cannot keep up therefore *pauses playback* (§4.3), which is the honest signal.
- **It does not look at the clock.** Showing a frame at the right moment is the presentation thread's
  business. A decoder gated by the clock is how a stalled device used to stop the sound as well as the
  picture: it is an edge of the historical deadlock (§12).

The one packet it does give up on is one it has **no decoder for**. The thread no longer parks when the slot
holds nothing: it walks the iteration as usual — the epoch, the room, the version verdict, the take — and then
releases a packet it cannot decode. That is not the dropping the rule forbids, which exists so a slow decoder
cannot hide behind lost frames; a decoder that does not exist has no slowness to hide, and leaving the packet
where it is has a cost the drop avoids: nothing else consumes that queue, the reader's read-ahead skips a kind
no decoder fills, so the queue becomes the whole file read into memory. Two details make it safe. The **end of
the stream** is not a packet and is never given up on — it still travels to the frame queue, because the video
stage's word is what earns the ending (§7), and a chain with no decoder that swallowed that word would leave
the file unable to end. And the **verdict is still taken first**, which is what tells the two null cases apart:
a package naming a generation that has been published but not yet adopted *blocks* — that is a decoder on its
way — while a package naming none is accepted and released, because no decoder was ever published for it.
That is also why the block is self-limiting here rather than a stall: a publication always precedes the wake,
so the generation a package names is always on its way, and the next turn adopts it.

While paused the decoder stands still rather than filling the frame queue; that is what keeps a pause from
spending the look-ahead the resume needs.

A hardware frame is copied out of the GPU before it is handed on, and **the copy carries the frame's time by
hand**. `av_hwframe_transfer_data` moves pixels and nothing else: the destination frame comes back with no
timestamp, and a frame with none is placed by the last packet that was submitted — which is not where it belongs.
The consequence is not a small one, and it is invisible to every counter the pipeline keeps: the frames of a
hardware-decoded file carry the times of the packets that happened to be in flight, the first of them is stamped
with a moment well past the start, and the clock's anchor (§5) is that frame, so playback of every file began
several tenths of a second in and the first subtitle cue arrived late with it. Measured on a 10 fps file: frames
of the first 0.9 s all absent from the queue with the software decoder showing them from 0, and the difference
was the timestamp alone — the frames were never lost, only mis-dated.

### 4.3 Presentation loses frames, best-effort, and holds the clock when it cannot

The picture is the follower, and it is the **only** stage allowed to give up on media *it could have
played*. (A chain with no decoder gives up on its input too, but never on media anybody could have shown —
§4.2.)

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
- **Hold the clock back when the picture cannot catch up.** If the frame the picture last *showed* is more than
  **200 ms** behind the clock — and nothing is queued behind it, so the picture really has nothing to offer — the
  clock stops, playback waits for its own picture, and starts again once the gap is within **100 ms**. A wide
  threshold to halt and a narrow one to resume, so a decoder hovering at the boundary cannot make playback
  stop-start.
- **The trigger is a lag, not a gap between frames.** A gap only exists when frames stop arriving, which a
  uniformly slow decoder never produces; a lag exists as soon as the decoder falls behind, which is exactly
  the case that needs the sound to wait. This was the one correction the design made to itself during review.
- **Waiting costs no frames.** While the clock is held, the decoder keeps producing and the picture keeps
  showing; only the drop rule above loses anything.
- **A paused file shows the picture of where it is.** While nothing is being presented, the picture thread
  offers the position's first frame that is still to be seen — one, once per position, taken from the queue the
  decoder filled and handed to the renderer. It is not a presentation: the clock is not consulted and is not
  moved, nothing is dropped or starved, and the wait for it has no clock behind it — a frame the decoder has not
  produced yet leaves the thread waiting inside the queue, where the frame arriving is itself the wake. What the
  still *is* is the position's first shown frame, and that is what the clock is anchored to if playback starts
  afterwards (§5) — remembered, not acted on, so a seek to 20 s whose keyframe is at 18 s does not run the
  remaining two seconds with a picture two seconds behind. Without it a paused seek shows the picture of the position it came from, because the frames of the
  new position are decoded and simply never looked at. Frames of a position playback has left are given up on
  rather than shown, and the position that is current is re-read as it goes, because a seek landing while this
  waits leaves the frame it is about to be handed belonging to nobody. A position with no frame at all says
  the picture's ending word there and then, rather than leaving it for a presentation that is not coming — a
  paused file whose picture has shown all it has, has shown all it has. This is the same walk through the
  render loop that presents a frame — look at the position's next entry, decide, take the entry that was
  looked at, hand it over — with the presentation steps (the `playing` branch in the code) skipped: the two
  are one loop, not two paths that have to be kept in step. And nothing is held between the look and the
  hand-over: a frame whose moment has not come is waited for *in the queue*, never in a local — which is what
  keeps the freeing logic down to one release per decision instead of one per way out of the loop.
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
- **The device has no room.** The hand-off says so and the thread waits — no deadline, no retry, no
  nudging — while the device is watched (§9). The wait ends when the device takes what it holds, when a seek
  moves the epoch on, or when playback is paused or stopped. The hand-off being carried is *kept* across a
  pause and *let go of* on a seek, and it holds its own place in the block, so this thread holds nothing but
  the wait.

One ordering rule matters here: the clock is anchored from the first buffer that will actually be heard, which
means the claim happens before the wait for room. A claim is for the position it was made for, so one made for
the old position cannot outlive the seek: a new position is one nothing has claimed yet, and needs no re-arming.

### 4.5 The sound reaches the device in the format the device plays

What a block looks like when it is handed over is decided before anything is opened, not by whoever converts it
last. The renderer is asked what its endpoint plays (`IAudioRenderer.Negotiate`), the host's chain is prepared
with that as its target, and the chain reports what it ends on. Each filter is told both what it is given and what
the next stage can take, and each reports the format it produces — the second filter of a chain is prepared for
what the first one hands on, not for the stream the chain opened with.

**One thread opens the device: the audio render thread.** The pipeline does not configure the renderer at all; it
asks, points the chain, and publishes. That leaves a device that cannot be reached to be found out in two places,
and both are deliberate. A device that cannot even be described is the answer to the command that asked for the
sound — `Negotiate` fails, so `SelectStream` fails while it is still the command, which is what a person sees when
they change to a track the machine cannot play. A device that can be described and then not opened is the audio
thread's to report, as a warning: the picture plays, and the reason reaches the host rather than the sound simply
being missing. The opening is attempted once per snapshot — the thread takes one, opens for it, and takes another
only when the chain has been edited — so a device that refuses is not asked again on every block.

**The pipeline knows no filter.** Which filters exist is the host's business: it adds a level filter, or a downmix,
the way it adds anything else, and the pipeline's whole part is to pass the device's target down the chain and
read back the format it ends on — which the chain reports as a format from the moment it is prepared, the stream
itself when no filter changes it, so a reader never has to reinterpret "nothing changed it". A host that adds a fold down gets a 5.1 file played at the level it was made at;
a host that adds nothing gets the stream handed to the audio engine as it is. That is the price of the decoupling,
and it is one line in the host.

The level matters because the engine's own conversion is not a neutral one: an endpoint with two speakers plays
two channels, and handing WASAPI six lets it convert, which divides the level down until no arrangement of the
source could clip. Measured, a front channel came through about ten decibels quieter than it left the decoder and
the centre about thirteen. Folding the mix down instead keeps the level the file was made at, and leaves the
engine only the sample rate, which it converts transparently.

The same version is what lets the chain move while the sound plays — a host adding or removing a filter. It is not
used to check a block, though: the audio thread holds a **snapshot** (`AudioFilterChain.Clone`), an object that never
changes, and runs every block through that. So what a block goes through and the shape the device was opened for are
one answer rather than two reads that could disagree, and an edit landing mid-block simply publishes a new snapshot
for the next pass. The thread compares the snapshot's version with the chain's once per pass — one read — and takes a
new one when they differ; the format is then compared directly, because a version can move without the shape doing
so (a filter that leaves the format alone is still an edit).

What a snapshot cannot survive is a filter that fails: a block that cannot be transformed is a failure of the sound
rather than a lost effect, because what the filter would have produced is the shape everything after it — the
device included — was prepared for. That ends playback with the filter's reason rather than feeding a device
something it was not opened for. The reopening writes nothing back to the clock: the clock offset (§5) is dropped,
and the next block reads the fresh device clock and works out where it stands, so a timeline that carried on across
the reopen is kept by the correction rather than by a position. The cost is a gap for the length of an open — the
device's buffer is gone with the old stream — which is why the version exists rather than a comparison on every
block (§14).

### 4.6 A device that stops is a pause, not a fault

The device corrects the pipeline's clock while it plays, so a device that stops taking audio is time that
stops: the correction holds the clock at the device's own reading, presentation waits for a moment that never
arrives, the audio sender waits for room, the decoder fills the frame queue and stops, and the reader idles once
both of its queues hold their minimums. **Nothing fails, nothing is dropped, and nothing anywhere else in the
pipeline needs a special case.** The historical special cases (a nudge, a stall guard, a decode gate) were
themselves the chief source of stalls, which is why the design refuses to have any.

The last of those — a watchdog that watched the device's position and warned that it was stalled — is gone as
well (§9). It was a second opinion about time held by the one thread that can read the device, and a device that
is late cannot be told from a device that is broken from inside that call, so the line it raised was a guess: it
fired on a slow device and stayed silent on nothing. The freeze is real either way, and the pipeline's answer to
it is the same as before — wait, lose nothing, and let the position say what is happening.

## 5. The clock, and the anchor

- **There is one clock, and its source is chosen at open**: the audio renderer's device position when the file
  has sound, otherwise a wall clock. Nothing else in the pipeline measures time.
- **Its lifetime is the pipeline's, not a thread's.** It is made during the open — always before the threads
  are started — and released after they have all been joined, so while a media thread runs the clock cannot be
  absent. Each render thread therefore takes it **once**, at the top of its loop, and hands it to the helpers
  that need it (`ApplyClock`, `AnchorClock`, the verdict, the device watch) instead of consulting the field
  again or checking it for null. The field's only other readers are the open, the stop, and the host's
  `Position`/`PictureLag`, where "no clock yet" is a real state and answers zero — which is exactly why the
  threads must not read `Position`: they would be mistaking "no clock" for "at zero", and a thread that is
  running always has one. The one-line `?? throw` at each thread's start is the invariant stated where it is
  relied on, not a case that happens.
- **The two render threads own it.** Each of them decides where the clock should be and whether it should run,
  and each decides it *once per iteration, when something about the decision has changed*: the state (read as
  the change stamp the waits use), the position, the claim below, or its own verdict that it has nothing to
  play. A position it has just taken up always counts as a change — the clock has to be placed for it even
  when the verdict was already "nothing to play" — and it is *fed into that one decision* rather than making a
  decision of its own, so a seek does not decide the clock twice in one iteration. All of
  those are read without a lock — the stamp and the verdicts are volatile, the position is a seqlock — so a
  wake that finds nothing changed costs a read rather than a lock entrance, and a thread that changes one of
  the facts decides the clock in the same breath. There is nothing to notify anybody about, and no signal to
  keep in step.
- **The clock runs only while playing and not waiting for a stage** (§8). The clock is *told* only when the
  answer has changed — asking it what it is doing is what makes that check exact, and it is what lets a clock
  implement `Start`/`Pause` naively (I10).
- **The clock is not authoritative about where playback started.** A demuxer lands on the keyframe at or before
  the requested position, so what will actually play first is earlier than what was asked for. The clock is
  therefore *anchored* by the first piece of media that plays — the first buffer handed to the device, or the
  first frame the position **showed** (the still offered while paused counts, which is why the clock is anchored
  at the moment playback starts rather than at the moment the still is drawn) — never by the requested position.
  Anchoring to the request leaves the picture permanently a keyframe behind the sound, every frame of it late
  enough to be dropped: the picture then never moves again.
- **Whichever media plays first claims the position, and the sound is not the only claimant.** The picture asks
  for the claim on every frame it presents, and `AnchorClock` refuses when this position already has an owner, so
  in the ordinary case the sound's first buffer claims it and the picture's request costs a comparison. Asking
  unconditionally is what covers a sound that *cannot* claim: no decoder for the new track yet, a track with
  nothing left at this position (switching to a shorter track late in the file), or the kind turned off. Leaving
  those cases to the picture would be a playback that stands still forever with nothing to report: the frame
  ahead of an unclaimed position waits for a moment only the clock could bring, and the clock waits for a claim
  only that frame could make (§8). The frame that claims a position is not early against it — its moment is when
  the position becomes a fact.
- **The clock is the pipeline's own, and the device corrects it.** It is a stopwatch, so it is never hostage to a
  device that has nothing to play: a WASAPI position counts frames *played*, and a device that has run dry does not
  advance — which would leave every frame due after the sound's last sample waiting for a moment that can never
  arrive, with the picture's end-of-stream entry stuck behind it and the file never ending. The device is still the
  truth while it is playing, so the sound's thread — the only thread that can read it — moves the pipeline's clock
  to it whenever the two disagree by more than a few milliseconds, and both clocks are anchored together by the
  first media of a position so that the comparison means something. Two consequences worth keeping: the pipeline's
  clock may step *backwards* by that tolerance (a stalled device pulls it back), and a device with nothing to play is
  not consulted at all, which is what lets time go on after the sound has ended.
- **What a seek places is a request, and it is placed by whichever render thread decides first for the new
  position** — out of `_pendingSeekPosition`, which the seek publishes with the new epoch — but only *while
  nothing of the media has claimed the clock for it*. That one condition is what lets either thread place it
  (placing the same value twice is placing the same value) and what stops a second thread from putting the
  request back over the media's answer once the claim has been made.
- **The anchor belongs to a position rather than to a state machine**: the claim is recorded as the position
  it was made for, so a seek needs no re-arming step, no reset in the right order, and no flush order to be
  right about — a new position is simply one nothing has claimed yet, and a claim that arrives late for a
  position playback has left is refused by the same comparison. Until a position is claimed the clock holds
  nothing but the request, which is also what tells the picture's starvation check and its drop rule that
  there is nothing to judge yet.
- `Position` is the clock's position, answered by the clock itself rather than through the pipeline's lock, so
  the host's thread does not wait for a render thread to read it.

## 6. Seek

A seek is the one operation that must interrupt other threads, and it does so by **making the epoch move
rather than by waiting for them** — and by **asking the cursor's owner to move it rather than reaching in**.
In order, on the queue's side:

1. Refuse it outright if the source cannot be moved at all (`CanSeek`) or if the state will not take one
   (`Paused`, `Playing` and `Ended` are the states a seek is for) — before anything else moves, so the refusal
   costs nothing. This is the only refusal that can be answered here, because whether a source *can* be moved
   is fixed when it is opened; whether it *will* is only known by trying.
2. **Leave the state alone.** A seek is not a pause: a file that was playing goes on playing from the new position,
   and a paused one stays paused, so a jump never overrules a pause the user pressed while it was in flight and a
   host never has to restore what the seek took away (§11). This also keeps the two refusals straight — a seek
   refused here (step 1) changes nothing at all, and a seek the source refuses *when asked* was accepted first, so
   the only thing its refusal means is that the position did not change (§11, I16). The one state a seek does move
   is `Ended`: a seek from the end takes the file back into itself as `Paused`, because there is nothing to play
   from the end until it is told to.
3. **Change the position as one fact, in one change**: publish the position that was asked for, advance the
   epoch, and clear the three queues. The change
   is one fact rather than a sequence of steps — the threads that act on the new position act on all of it, and
   an epoch that moved before the reset did is an epoch they can produce media under, media the reset then
   deletes (measured: with the window widened to 300 ms, the reader's first 48 packets of the new position were
   thrown away, keyframe included). The same change is what a thread reads the epoch through
   (`_epochs.Read()`), and a read that lands inside it reads again instead of seeing the new position before
   everything that moved with it has moved. A second request before the reader acts overwrites the published
   position, which is what makes the latest request the one carried out. Nothing about the clock, the anchor or
   the starvation flags is touched here: the render threads notice the epoch move, and their own decisions
   follow from it (§5) — a starvation flag describes the position it was reached at, so there is nothing to
   clear, and a position nothing has claimed is one nothing has claimed.
4. Clearing the queues is also what releases the two threads parked on them: a full frame queue is where the
   decoder waits for room that only the picture can make, and a filled packet queue is what tells the reader
   it has read enough (see §3's table). The clock gets the requested position *for the host's benefit* — the
   media will set it to where playback actually resumes, see the claim, §5 — and until it does, no decision
   is allowed to depend on it. The tail the decoder produced is not cleared here and the flush is not
   requested here: both belong to the decode thread, which takes up the new position itself the moment it
   sees the epoch move (§2.2). The end of the file is not withdrawn here either, because it is stated *with*
   a position: a new position is simply not the one the file ran out at.
5. One signal for one seek, sent once the epoch and the clears have moved: what the wake is for is that the
   position *moved*, and a thread woken early would read the position playback has left and go back to sleep —
   for the reader, which waits without a timeout once the file has run out, that means not taking the seek up at
   all. There is nothing else to announce: a seek raises no event, because the only state it can change is a file
   taken back from `Ended`.
6. Answer the caller with the result of the request — accepted, or the predictable refusal from step 1.

### Seek modes

What a position is *taken as* is part of the request, and it travels with the position in the same change:

| Mode | The position is | What plays first |
|---|---|---|
| `Fast` (the default) | a wish | the keyframe at or before it; that media anchors the clock, so playback resumes *earlier* than asked (§5) |
| `Precise` | a floor | the first frame at or after it and the first sound block at or after it; nothing earlier is shown or played, and the clock is anchored by that media, so it never stands before the request |

**A wish the picture cannot grant is landed by the stream that reaches furthest.** A demuxer lands on a keyframe,
and keyframes are the picture's, so the cursor is asked to land by the picture — that is what a fast seek is *for*,
because the media between a keyframe and the request is the lead-in the decoder needs. A picture that has ended
before the request has no keyframe there, only its last one — which can be seconds behind — so a landing measured by
it would put the whole file, the sound included, at the moment the picture stopped, and everything from there to the
request would be read and thrown away to arrive at it. A position past the end of the picture is a position only the
sound reaches, and there the cursor lands by the sound: it has no keyframes, so it lands within a packet of the
request — which is what a file with no picture at all has always done. Where the sound has ended too, the landing
goes to whichever of the two is the longer one, because that is the closest anything can come to the request; and
where the container declares no length at all, nothing has been shown to have ended and the picture keeps it. The
mode is left as it was asked for: a fast seek still resumes at the media of its landing, which is now the request to
within one sound block, and a precise one still never stands before it.

The picture's end is the length the container declares for the stream (`MediaStreamInfo.Duration`, which Matroska
writes as a `DURATION` tag rather than as a stream field), and a container that declares nothing leaves the wish as
it was — silence about a stream's length is not a claim that it has ended.

A precise seek still lets the source land on the keyframe at or before the position: the media between the
two is needed — the frames after it refer to it — so the decoder produces it as usual. Giving up on it belongs
to the stages that play it, not to the decoder (§4.2) or the reader (§4.1): the picture drops frames that
begin before the floor, and the sound gives up on blocks that begin before it, both *before* their media can
claim the clock. It is the same class of loss as the media of a position playback has left (I6), and it is the
reason the anchor rule needs no special case: what claims the clock is already at or after the request.

The boundary is read from the position the epoch publishes, so a component rebuild (`Reconcile`) publishes
the position playback is at and the mode in effect keeps applying to it — precision is a property of the
position, not of one call.

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
- The audio thread's epoch check happens **once, at the top of its loop**, and that is enough because nothing
  between it and the device is a wait: media comes from a queue that never waits, an entry carries the epoch it
  was produced for and is dropped when it no longer matches, and a hand-off that a seek interrupted is
  abandoned and the device reset at the top of the very next iteration, before it could be resumed. What the
  single check still has to cover is both kinds of stale work — media read for a position playback has left,
  and a device that had stopped consuming so that a submit outlasted the seek that interrupted it.
- Work carries the epoch it was **acquired** under, and the consumer drops it rather than judging it against a
  fresh read: that is what makes one check per thread enough. A packet tagged with a position the decoder has
  left is dropped there, and a buffer tagged with one the device has left is dropped on the audio thread —
  neither needs a second check beside the decode or the submit.
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
- **Play** restarts time through the same single decision every state change uses, which consults the
  starvation flags: playback resumes into "playing and not waiting", never into "playing but stopped". It is
  for `Paused` only: from `Ended` the host seeks first (the adapter does), and from `Playing` it is refused
  rather than absorbed.
- **A state change is the only thing that makes the clock be re-decided**, and the decision itself belongs to
  the two render threads: the state is one of the facts their decision reads, the wake a change sends is what
  gets them to read it, and the clock is told only when the answer has changed — once per change of the
  answer, not once per command that touches it.

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
and the order is not a preference, it is what makes the joins plain joins: the stop flag is set and **every
node is woken**, so each thread finishes whatever it is doing and finds the flag at its next decision. That
includes the two threads that would otherwise be waiting for a stage: the decoder's wait for room is woken by
the same `WakeAll`, and the picture's wait for a frame by the same one. Only then are the threads joined — with
no timeout, because a timeout here would only ever be hiding
a wait that was not released — and only then is the device flushed, the decoders and the renderer let go, and
the source closed. `Dispose` waits for the whole of that, on the caller's thread, which is the one call that
waits: after it returns, the file is closed and the threads are gone, which is what whoever is about to
delete, move or reopen it is entitled to assume.

One consequence worth stating plainly, because it is the price of there being no backstop: **a wake that was
never sent is a join that never returns.** That is deliberate — a missing wake should be loud rather than be
papered over by a timeout — and it is why every edge in §2.3's table has a test whose own wait is bounded.

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
| Waiting for video | presentation | this position has produced nothing to show yet, or the frame it last *showed* is > 200 ms behind the clock and nothing is queued behind it | a frame has been shown or is waiting to be shown, or the file has run out at this position — the verdict is re-read on a fixed cadence while the picture is idle, so the hold arrives at the threshold rather than at the next frame (§8) |
| Waiting for audio | audio sender | this position has produced nothing yet, or the device held nothing, and the file has not run out at this position | the next buffer is there (or the device still holds one) |

The picture's mark is **the last frame it put on the screen**, not the newest the decoder produced: what is
being judged is whether the viewer is looking at a picture that has fallen behind, and a frame the decoder has
finished but the thread has not reached is not something anybody can see. The decoder's own newest timestamp was
tried first and is wrong twice over — it advances while the renderer is blocked on a GPU fence, and it *survives
the frame being shown*, so a decoder that stops producing reports a picture that is "not behind" for as long as
its last frame's timestamp is ahead of the clock.

**A frame waiting for its moment is never starvation.** Whatever the last shown frame says, if the queue holds a
frame then the picture is not waiting for anything: it is either due now and will be shown, or due later — and
the clock that will make it due is exactly the clock a hold would stop. Judging by the shown frame alone would
therefore hold the clock for the frame the hold is what makes late, which is a permanent freeze rather than a
delay: the picture never comes, the hold never lifts, and nothing anywhere reports a problem.

**A stage whose stream has nothing left *here* is not starving either.** The reader publishes, per position, the
newest timestamp it has taken in for each stream — `_readVideoTicks` and `_readAudioTicks`, stamped with the
position they belong to, so a number from a position playback has left is never read as this one's. A render
thread with an empty queue is **spent** when its own stream's newest timestamp is behind the clock while the
*other* stream has been read up to or past it: the file has nothing more of this stream at this position and
still has data elsewhere, so nothing is coming and a hold would stop the stage that is still playing. That is the
difference between "the picture has ended" — a stuck picture with the sound going on — and "the picture is
behind", and it cannot be seen from the render thread's side of the pipeline at all, which is why the fact is
published rather than guessed. It is deliberately *not* "the reader has read past the clock with everything":
with both streams spent, time stops with them, which is the state a live source leaves a stream in
(`HoldsTheClockWhenNothingIsLeftOfEitherStream`).

The distinction is reachable in only one shape, and it is worth knowing which: every finite file reports its end
when it is read out, and the reader reaches that end long before playback needs it, so an early-ending stream is
released by the end of the file in milliseconds and never holds anything. What needs the reader's words is a
source that goes quiet without ending — a live stream, or a file read through a connection that has stopped
carrying one of its streams. Measured: with the rule switched off, that picture holds the clock at 1.2 s and the
sound stops with it; with it in place the sound reaches the end of the file
(`KeepsTheSoundPlayingWhenThePictureStopsArriving`).

Both feed one decision — *should time be passing* — which is: playing, and neither wait is in force. The
hysteresis (200/100 ms) and the "not before the first anchor" condition exist so that ordinary jitter cannot
make playback stop-start; the end-of-file condition exists so the tail-out is not mistaken for a stall, and it
is *about a position* rather than about the pipeline: "the file has run out where I am", which is why a seek
does not have to retract it and a stop need only advance the epoch. The two waits are measured differently
because the two stages know different things: the picture measures itself against the clock (a time it owns a
view of), while the sound asks the device whether anything is still in it — the sound having nothing to hand
over says nothing about what is being heard, and it is the hearing that time must not outrun.

**Both stages hold the clock at the start of a position, for the same reason.** The sound's rule already had
it — an empty device is "nothing to play" — and the picture's did not: it measured only how far the newest
decoded frame was behind, so *nothing decoded at all* counted as "not behind", and the clock was free to run on
the strength of the sound alone. Measured with a first decode of 400 ms: the clock reached 435 ms, ten frames
were dropped as late, and the picture started 666 ms into the file with nothing reported. The rule is now the
same fact on both sides — *this position has produced nothing yet* — expressed with the picture's own
epoch-exact facts: nothing shown for the position and nothing waiting in its queue (the queue is cleared
inside the position change, so a previous position's frames cannot be mistaken for this one's). It is asked
*before* the clock's claim, because with sound the claim is the first audio block: a claim-first test waves the
picture through exactly when it has nothing. Nothing decoded is released by the first entry — a frame, or the
end of the stream — and a file that has run out here never holds the clock at all.

**The picture's verdict is read by the thread that waits for the picture**, so that wait is the only place it
can be read again — and a picture whose decoder has gone quiet is exactly a thread with nothing in its queue
and nothing coming to wake it. The wait is therefore **bounded by a fixed cadence** (`VideoIdleTimeoutMilliseconds`)
whenever the thread is idle and playing, so the starvation is reported when it *happens* rather than when the
next frame turns up. The cadence is a constant and not a number computed from the clock: not from how far
behind the picture is, not from whether the clock is running, and not from whether the media has claimed it
yet. Each of those would be a prediction that the clock cannot change the answer, and the clock is owned by
another thread — a claim that lands, a resume after a hold, or a rate change makes the prediction false, and
the cost is a hold that never arrives.

While the thread is paused nothing about time can make the verdict true, so that wait is left to the wakers: a
frame, a seek, a surface change or a stop. And a position whose clock nothing has claimed yet is *not* a case
of its own: the claim only opens the gate the verdict reads, and opening it cannot change the answer at the
moment it happens — the demuxer lands on a keyframe, so the first media that claims the clock and the newest
frame the decoder has produced are the *same* piece of the position (`behind ≈ 0`, §5). A picture that then
falls behind does so because the clock ran while its decoder was quiet, which is the cadence's business and
nobody else's.

Two things this must never become:

- a **state change** (no `Rebuffering`, no error, no event storm at the display rate);
- a **reason to drop anything** — the drops stay where they are, in the presentation thread's best-effort rule.

## 9. The stalled device

A device that accepts audio and stops moving its position is the one failure the engine cannot repair, and the
design's answer is to **do nothing about it** — and, since the watchdog was removed, to say nothing about it
either.

- Everything already waits correctly: the audio sender waits for room, the clock does not move, presentation
  waits for a moment that never arrives. No thread is spinning and nothing is burning CPU — the sender's wait
  is on its own node, because the hand-off that found no room *returned* instead of waiting (I20).
- **Why the watchdog went.** It was the only thing in the pipeline that did not have to be there. The device is
  read by the audio thread and by nobody else, and the only judgement the pipeline needs from it is the one the
  clock's correction uses — *what is being heard now*. Whether the device has stopped is a different question,
  and one the same call cannot answer: a device that is drawing on a starved graph, one that has been handed
  nothing to play, and one whose endpoint has died all report the same frozen position, so a line that called
  that "stalled" was a guess with a threshold behind it. It fired on a slow device — a real report — and the
  number it fired at (a second without progress) was not derived from anything.
- **What a stall looks like now.** `Position` stops and stays stopped, the state stays `Playing`, no warning is
  raised, nothing is dropped, and `StarvedFor` says nothing — the sound is not starving, it is waiting for room;
  the picture is not starving, it has a frame and its moment never comes. A host that wants to explain the
  freeze has exactly what the pipeline has: time stopped moving while the device still holds audio.
- **Forbidden here:** nudging the device, deadline-based retries, treating the stall as an error, or letting
  any wait on the device end without the device. Each of those was tried and each one turned a visible wait
  into an invisible one (or into a pipeline that thrashes). If the device is removed from the machine, the
  device call itself fails, and *that* surfaces as a pipeline error — which is the correct place for it. The
  backend does not decide what the failure means: it keeps the original exception exactly as it was thrown,
  freezes its clock at the position it was last put at (a reader must never be thrown at — the host's position
  timer reads it), and reports that exception on the next hand-off or flush. The pipeline's single `Fail` is
  where it becomes an `Error`, and the one place it is logged, with its stack.

## 10. Invariants a change must preserve

A change is safe exactly when it leaves all of these true. "Pinned by" names the test that fails first.

| # | Invariant | Why it matters | Pinned by |
|---|---|---|---|
| I1 | Only the presentation thread waits on the clock | Every historical deadlock had a clock wait somewhere else in the cycle | pipeline tests: a device that never moves must not stop the picture from being shown; a decoder too slow must hold the clock, not drop its output |
| I2 | The reader's only waits are the file and its own read-ahead idling | The reader is the sound; anything that stops it stops audio production | queue tests: unbounded enqueue; a decoder at a standstill must not stop the reader |
| I3 | The video decoder waits only for frame-queue room, and drops nothing **it has a decoder for** — a packet with no decoder in the slot is taken and released, and the end-of-stream marker is never released at all | A decoder that drops hides its own slowness; a decoder waiting on anything else adds a cycle edge. The exception is the other way round: a chain that cannot decode has nothing to hide, and the packet it leaves behind is the whole file read into the queue (§4.2) | frame-queue test: the producer waits for the picture rather than losing a frame |
| I4 | The presentation thread never displays early and never waits for one particular frame | Both are the failure modes called "frozen" and "out of sync" | `StartsWhereTheFileResumesRatherThanWhereTheSeekAsked`, `DropsOnlyTheFramesWhoseMomentHasGone`. The one frame it does wait for is the still of a paused position, and that wait has no clock behind it: the frame arriving is the wake (§4.3) |
| I5 | Nothing waits for a queue that no longer exists, and no wait holds on to a position playback has left: a seek is a request the cursor's owner carries out rather than a lock the seek takes, and it *clears* the queues so the threads parked on them are released | Bounded liveness: the decoder's wait for room ends with the picture taking a frame, with the clear a seek performs, or with the close a stop performs — never with a state check that a pause would make false | the seek tests, delivered as "the seek never returned" assertions, `SeeksWhilePausedAndMovesTheReaderAndDecoder` (a paused seek with both producers parked), and every test that disposes a pipeline with a full frame queue |
| I6 | Media carries the epoch it was produced for **and the component versions that produced it**, and is dropped rather than replayed when either has moved on — checked by the producer before it hands work over and by the consumer before it uses what it took, never by the queue in between | A seek that half-works is worse than one that visibly restarts; and a queue that filtered would be a queue that decides, which is how the epoch gets back into a place with no business knowing it. The version half is what stops a new decoder's packets reaching the decoder it replaced, and an old decoder's frames reaching the renderer that replaced it (§2.4) | `SeeksWhilePlayingAndGoesOnPlaying`, `SeeksWhileTheNextFrameIsStillWaiting`, `MediaQueueTests` for the tag travelling untouched, and `PipelineVersionTests` for the comparison itself |
| I7 | An empty queue is a wait, and only an empty queue *after the end of the stream* is played out: the end arrives as an entry behind the media it follows, and the ending is *earned* — the queue ends playback when the picture and the sound have each said their stream is over, for the position the file ran out at; a pause does not hold it open, and a failure is never talked over by it. Which stages owe a word is asked of the **selection** — the streams of the holder — rather than of the file or of the components: the ending says the media being played has run out, the selection is what names that media, and unlike a component it does not change under the answer. A stage exists for every open — all four threads start together, and each render loop walks its queue to the end marker with or without a component — so a file whose sound has no decoder at all still ends | A timeout in the queue is two faults at once — it blocks the queue every command is posted to, and it ends a file whose sound has not been heard; and inferring the end from an empty queue makes "the decoder is behind" and "the decoder is done" the same observation, which is a question about a position the asker is not holding. Gating the decision on the state instead needs a second place to ask it again, which is what left a paused-at-the-end file open for good | `PlaysAnAudioFileThroughTheRenderer`, `PlaysAVideoOnlyFileToTheEnd`, `ProducesTheTailOfAFileWhenItsPacketStreamEnds` (a decoder that only releases its tail when drained), `PlaysAgainAfterTheEndOfTheFile`, `AnswersACommandWhileTheSoundHasNotPlayedOut` (fails if the wait comes back), `EndsWhenTheDeviceHasPlayedOutTheSound`, `EndsWhilePausedWhenTheFileHasPlayedOut` (fails if the ending waits for a resume), `EndsWhenTheSoundHasNoChainToPlayItThrough` (a kind the file has but nothing can play still reports its end, so the ending does not wait for a stage that will never speak) |
| I8 | Losing media happens in exactly one place, and it is counted | A media pipeline that loses frames silently cannot be diagnosed | `DroppedFrameCount` assertions in the drop and device tests |
| I9 | Every wait ends; a stalled device is a wait that ends when the device does, and nothing in the pipeline names it | Nudging, timing out or failing a device that has stopped turns a wait the user can see into a fault they cannot; and naming it needs a judgement the pipeline cannot make — a late device and a broken one report the same frozen position, so the line would be a guess with a threshold behind it (§9) | `WaitsForADeviceThatNeverTakesAudio` (a device with no room parks and resumes without losing the block) |
| I10 | The clock starts and stops in exactly one decision, made by a render thread out of facts it reads (the state, the position, the starvation verdicts, the claim) — a state change always re-evaluates it, and the clock is *told* only when the answer changes | Two places deciding "is time passing" is how pause, starvation and seek start fighting — and a clock told the same thing twice is a clock whose `Start`/`Pause` have to tolerate it, which the interface does not promise (a naïve `Start` re-anchors, a naïve `Pause` records a stop that never happened; the two real clocks happen to guard themselves). The decision is taken where the facts change rather than asked for by someone who does not hold them, so there is no notification to keep in step — and the facts are all readable without the lock, so a wake that finds nothing changed costs nothing | `PausesAndResumes`, `TellsTheClockOnlyWhenWhatItShouldDoChanges` (a state change that leaves the answer the same tells the clock nothing), `KeepsTheClockWhereTheMediaPutItWhenSomethingElseChangesIt` (a decision made *after* the claim must not put the seek's request back over the media's answer), and every test that asserts `ClockIsRunning` |
| I11 | A surface change is taken up whether or not frames are being presented, and drawing the last frame again never depends on a frame still being alive — and what a paused picture shows is the current position's first unseen frame, once, so a seek that landed while paused is not left showing the position it came from | A paused video that ignores a resize is stretched by the host, and a redraw that needed the frame's memory would force the pipeline to retain frames. A paused seek that showed nothing new would be showing a picture of a position playback has left — and one that showed the frame *again* on every pause would jump backwards to the oldest frame the decoder is holding | `TakesUpASurfaceChangeWhilePaused`, `RedrawsTheLastFrameWhenTheSurfaceChangesWithoutANewFrame`, `LetsTheRendererFollowTheSurfaceWhilePaused`, `ShowsTheStillOfThePositionASeekMovesToWhilePaused` (one frame, and one only), `DoesNotShowAStillWhenNothingMoved` |
| I12 | An idle thread sleeps on its own node and is woken by whoever changed what it reads, never on a fixed short sleep — and a wait whose answer depends on time is armed with a **fixed cadence** that never depends on another thread's state | Polling costs power and processor time for nothing, and a missed wake costs the promptness a pause, resume or seek is supposed to have. A cadence derived from the clock's state (`IsRunning`, the claim, the rate, how far behind the picture is) is a prediction that the clock cannot change the answer, whose subject is owned by another thread; a prediction that turns out wrong is a hold that never arrives (§2.3, §8) | `WakesForEachSurfaceChangeWhilePausedRatherThanWaitingForTheBackstop` (counts the wakings inside one backstop period, so it fails if the wake is dropped), `HoldsTheClockBackWhenTheClockReachesTheThreshold` (a silent file: only the clock can change the verdict, and the cadence is what notices) |
| I13 | A warning is a line, not a state: it is raised as it happens, nothing reads a "current warning" back, and what counts as a repeat is decided by whoever raises it | A warning folded into the state event makes every subscriber ask whether the state moved; a warning held as a value makes its lifetime someone's responsibility, and a value nobody clears is a stale one. A warning's job is only to explain something the user cannot see for themselves, which is why there are as few as there are — a refused seek, audio the device would not take — and why the stalled device, which the pipeline cannot distinguish from a slow one, raises none at all (§9) | `PlaysAnAudioFileThroughTheRenderer` (a file that plays says nothing at all), `RefusesASeekTheSourceWillNotCarryOut` (one line for a refusal) |
| I14 | Only an item of the orchestration queue changes state or raises an event; the threads that move media only post — and the clock is moved only by the two render threads, out of facts the queue published (the state, the epoch and the position that was asked for) rather than out of a value handed along with a wake | This is what makes the state a volatile field instead of a locked one, and what lets a subscriber touch UI state without asking which thread it is on. Keeping the clock to the threads that read it is what keeps a seek from reaching into a decision it does not own: a seek publishes a request, and the first media of the position is what turns it into a fact | every pipeline test: they call the pipeline from the test thread and assert on what was announced — and on what a command handed back — never on which thread a callback arrived on |
| I15 | Every call into the pipeline is posted and hands back the task of its own job, carrying whether it could be carried out; nothing inside the pipeline waits for a queue item, no queue item waits for a media thread's progress either, and the only wait in its own surface is `Dispose`'s teardown — a plain wait, because nothing in the teardown waits for something that will not come | A wait inside a queue item — the caller's thread stalls behind whatever is ahead of it in the queue, including another pipeline's work, and a nested call waits for itself. The end of a file used to be exactly that wait, which is why it is now a decision taken from messages instead of a wait taken from flags. A caller awaiting a task it was handed is doing what any asynchronous API allows | `SeeksWhilePlayingAndGoesOnPlaying`, `SeeksWhilePausedAndStaysPaused`, `SeeksWhileTheNextFrameIsStillWaiting` (all wait for the seek's task), `AnswersACommandWhileTheSoundHasNotPlayedOut` (a command answered while the tail is still playing out), and every test whose fixture it deletes straight after a dispose |
| I16 | **A command reports; playback fails.** A request that cannot be carried out is answered by the task it handed back and changes nothing else — no state, no event, no error — while `Error` is only ever playback that cannot continue. The one thing a command *can* leave behind is its own state change — a seek from `Ended` leaves the file `Paused` — and a seek the source refuses (only known by trying, so only known afterwards) leaves the position where it was: a line, not a failure | Folding the two together is how a pipeline dies because somebody dragged a slider past a source that will not move, and it leaves a caller unable to tell "that did not work" from "this is over" | `RefusesCommandsWhenNothingIsOpen`, `ReportsAFailedPlayOnceTheFileHasEnded`, `RefusesASeekTheSourceWillNotCarryOut` (both refusal paths leave the state and the position as they were), and `ReportsADecoderThatDiesAsPlaybackFailing` for the other side of it |
| I17 | **A queue is a data structure: it takes what it is given, hands it back in order, and waits for nothing.** No thread ever sleeps inside a `MediaQueue<T>`, no entry is ever held across a wait, and the queue has no waits, no timeouts and no pulses left: a consumer that has nothing to do sleeps on its own node and is woken by the producer, a producer that may not run ahead checks its window and sleeps on its own node, and the seek's clear neither waits nor wakes anybody. A consumer that has to judge an entry before owning it looks first (`TryPeek`) and then takes back what it looked at (`TryDequeue(entry)`) | A queue that woke on a clock is a queue whose missing release is survivable and therefore invisible: the end-of-file word and the "nothing to play" hold were both quietly resting on one 20/50 ms tick, and neither release would have been missed if it had been dropped. A queue that makes a *thread* wait is worse than that: the waiter is then parked on a lock the waker does not own, so a pause, a seek or a surface change cannot reach it — which is how a picture went on sleeping through a seek while its decoder was quiet. Every wait now lives on the thread's own node, so there is exactly one lock per thread to reach, and where a wait ends has a test (§2.3, I18) | `HandsTheEndOfTheStreamOverAfterEverythingQueuedBeforeIt` (the end is handed over once, and `TryPeek` is how a test asks what is behind), `TakesTheEntryThatWasLookedAtRatherThanWhateverIsAtTheHead` (identity, not the tag: a replacement entry carries the same one), `AnswersAtOnceOnceItIsClosed`, `DisposesWhatItStillHoldsWhenItIsClosed`, `TakesAsMuchAsItIsGivenRatherThanStoppingTheProducer` |
| I18 | **One wait per thread.** Each of the four threads sleeps in exactly one place — its own `PipelineNode` — and every shared fact it reads before sleeping has a writer that wakes that node. A wake means "something you read changed": it may be spurious, it must never be missed, it never waits for the woken thread, and it is never sent while holding a queue's lock | Two places to sleep is two locks to reach, and a waker can only reach the one it knows about. The state lock and the queue's lock were exactly that pair: a thread inside `Peek` could not be told that the position had moved, so every command was a timeout away from being noticed, and a missing wake was indistinguishable from a slow one. With one wait, "how does this thread learn about X" has one answer per fact (§2.3) | `PipelineNodeTests` (the stamp protocol, the un-losable wake, the wake that never waits), `KeepsTheSoundComingAfterTheReadAheadIsUsedUp` and `KeepsThePictureComingAfterTheFrameWindowIsUsedUp` (a consumer that is the only possible waker), `TakesUpASurfaceChangeWhileThePictureWaitsForItsPosition` and `TakesUpASeekWhileThePictureWaitsForItsPosition` (a control wake reaching a thread that is waiting for media) |
| I19 | **A hold on the clock is the pipeline's, and no component may release it.** A renderer's hand-off writes into a stopped device and leaves it stopped; the only starter is the render thread's decision (I10), and a stopped device's buffer filling is the hold itself — the hand-off says it has no room until the decision changes or the device is reset | A component that starts the device decides that time may pass, in a place the decision cannot see. `WasapiAudioRenderer.Submit` did exactly that, and the hold was released by the next block: a first decode that took 400 ms came after the first audio block, so the clock ran with nothing to show and the frames were dropped as late, and the picture fell 1.3 s behind the sound in a held playback — both measured by making the test double start the clock the way the device did. Nothing in the double was starting it, which is why the whole suite stayed green while the backend released every hold | `HoldsTheClockBackUntilThePictureHasSomethingToShow`, `HoldsTheClockBackWhenTheVideoCannotCatchUp`, `PlaysAToneThroughTheDefaultDevice` (a hand-off into a stopped device leaves it stopped, and one into a paused device does not restart it), and their refusal to pass for a double that starts the stream on submit |
| I20 | **A component never waits on its device; it hands the thread back.** A hand-off writes what the device has room for and returns, saying it has no room; the pipeline then waits on its own node, where a pause, a seek, a stop and the device's own progress all reach it. Nothing sleeps, retries or gives up inside a renderer, and a backend that can accept an unbounded amount must still stop the caller running away — a high-water mark of its own, reported as the same yield | A wait inside the renderer is a wait nothing in the pipeline can reach: the state, the epoch and the stop flag are the thread's to read, not the component's, so a paused device that never drains parks a submit for good, and the seek that would have dropped the block waits behind it. That is also why the old `Submit` needed a two-second give-up — a number that turned a stalled device into a pipeline error, while §9 says a stalled device is a wait and nothing else. Nothing chose it; it was the shape of the wait | `LetsGoOfTheBlockWhenThePositionMovesWhileTheDeviceHasNoRoom` (a seek reaching a suspended hand-off), `WaitsForADeviceThatNeverTakesAudio` (a device with no room parks and resumes without losing the block), `SaysItHasNoRoomRatherThanWaiting` (the real endpoint reports a full buffer as a yield, and a reset gives the room back) |

## 11. Changes that look reasonable and are not

| Change | What it will cause |
|---|---|
| Give the packet queue a maximum, or make the reader wait on it | The picture's slowness stops the reader, and the reader is the sound: audio starves behind video. This is the coupling the design removed; it is the first thing to check if a "sound stutters when the video is heavy" report ever comes back |
| Give a queue's wait a **fixed** timeout so a missing wake is survivable | It reads as a backstop and behaves as a sedative: with a clock in the queue, an ending nobody produced and a sound nobody released both look like "working, only late", which is how the ending came to be earned by an empty queue and how the sound's half of it came to be said by a timeout rather than by the device. The queue now waits for nothing at all (I17), and every edge in the wake table has a test whose bound turns a missing wake into a failure instead of a hang (§2.3) |
| Let a thread keep waiting on a lock the waker does not own, and add a timeout to cover the difference | Two waits per thread is two locks to reach: a thread inside `Peek` could not be told that the position had moved, so a pause, a seek or a surface change was noticed only when a frame happened to arrive — or never, when the wait had no time on it. The timeout was not a fix, it was the symptom: the waking side had no way to reach the sleeping side (I18) |
| Let the renderer wait for the device, and give that wait a timeout | The wait is inside a component that cannot see the state, the epoch or the stop flag, so nothing in the pipeline can end it — and the timeout that makes it survivable is a number nobody chose: it turned a stalled device into a pipeline `Error` after two seconds, while §9 says the same device is a wait to be *named*. A paused device that never drains waits forever, and a seek arriving there waits behind the block it would have dropped. The hand-off returns instead, and the wait lives on the thread's own node (I20) |
| Ask the pipeline to keep the device from blocking by watching how full it is — a "queued limit" in milliseconds, or a capacity query — | It is a second threshold describing a device it does not own: a chosen number that is wrong for a small buffer, meaningless for a backend that queues without limit, and either way a duplicate of what the renderer already knows. The renderer asks the device, and says so through the hand-off (I20); the pipeline needs no number at all |
| Derive the idle wait from the clock's state — how far behind the picture is, whether the clock runs, whether the media has claimed it | A cadence computed that way is a *prediction* about another thread's state, and a prediction that turns out wrong is a hold that never arrives: the claim can land on the sound's thread, the clock can resume after a hold, the rate can change. The cadence is a constant, and the claim is a wake (§8, I12) |
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
| Have a seek pause and then resume what it found playing — the command undoing its own change | The state stops being a fact about playback and becomes half of one: the same command both changes it and changes it back, so a host cannot ask for "seek and stay paused" (a drag on a paused player, a preview) without racing that decision, and the user pressing pause *while the seek is in flight* is silently overruled. The asynchronous case is worse: a source that refuses the move is only known afterwards, so the pipeline would have to decide the resume before it can know whether there was a move to resume. The answer is not to pause at all: a seek leaves the state where it is, so there is nothing to undo, nothing to overrule, and nothing to restore in a host |
| Decide the ending only while playing, and ask it again when Play is pressed | Two faults: a file that has played out stays *Paused* — the words are already in and only a resume asks again, so the pipeline holds a file nothing will play — and the resume that "fixes" it reports a `Play` that succeeds into an immediate end, which is a caller told it started what it finished. The ending is earned by the stages, so where they were heard is where it is decided; the one state to respect is `Error`, which an ending would replace with a file that merely finished (`EndsWhilePausedWhenTheFileHasPlayedOut`) |
| Signal the threads with the state change, "because a woken thread re-reads the epoch anyway" | It re-reads it *before* the change is complete and then waits again — against a signal that has already been sent. For a wait with a timeout that costs promptness; for the reader once a file has run out, which waits with none, it costs the seek: it wakes, reads the position playback has left, parks, and nothing is coming to wake it again. The window is microseconds and the race is usually won, which is the whole reason to remove it rather than race it — measured by widening the window to 150 ms, where `TakesUpASeekFromTheEndWithoutAnotherCommand` fails on exactly that: the file sits where it was while the clock, the state and the seek's answer all say it moved |
| Keep a field for "the position the clock has been placed for", so that two threads place it exactly once | The claim is already that fact. While nothing of the media has claimed the clock for a position, the request is the only thing there is to place and placing it twice places the same value; once it has been claimed, the claim's own position refuses the request. A second field is a second thing to keep in step — written when placing, written when claiming, reset on a stop, read on every decision — and each of those is a way for it to disagree with the clock it describes. What made it look necessary was the *pair* of facts it stood in for: "has it been placed" and "has it been claimed", which the claim alone answers (measured by removing the guard that reads it: `KeepsTheClockWhereTheMediaPutItWhenSomethingElseChangesIt`) |
| Decide the clock at the top of every render iteration, under the lock, "because it is one line" | A lock taken by both threads more than once per frame each, to answer a question whose answer changes a handful of times a second. Everything the decision reads is available without it — the stamp and the verdicts are volatile, the position is a seqlock, the claim is a long — so the entrance is spent on a change and a wake that finds nothing changed spends nothing. The decision itself stays in one place; only the *asking* is gated |
| Show the paused still by peeking at the queue and handing the peeked frame to the renderer | The frame is not the caller's while it sits in the queue, and a seek's clear disposes what is there — so the renderer could be handed memory that is already released, which is a crash whose timing nobody controls. The still takes the frame, renders it and lets it go, in the same walk through the render loop that presents a frame: the queue hands over ownership or hands over nothing |

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

The same discipline applies *inside* a queue, and it is why a queue waits for nothing at all (I17, I18): a
thread is parked on its **own node**, and a queue is released by nothing because it holds no waiter — it takes
what it is given, hands it back in order, and the threads that use it are woken by each other (§2.3). A freeze
with a thread inside a `MediaQueue<T>` is therefore a thread inside a short lock and nowhere else; a freeze
with a thread *outside* one is a wake that was never sent, and the wake table names the sender.

The one lock the media threads take *among themselves* is the clock lock (§5), and it is not part of this
class: it is entered for a decision and never held across a wait, nothing inside it waits for another thread's
work, and the clock it covers never calls back into the pipeline — so a thread holding it is always making
progress. It stands in for a pair of notifications: the thread that can read the facts takes the decision,
rather than asking the thread that owns them to take it.

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
| `WarningRaised` (event) | one line about something that is not a failure: audio that was refused, and a device that stopped moving | a warning is not a failure and not a state; it is raised as it happens and nothing holds it afterwards, so a consumer that wants to display one keeps the last |
| `AverageVideoDecodeMilliseconds` / `AverageVideoRenderMilliseconds` | cost per stage | either approaching the frame interval means that stage is what limits playback — **but check what is inside it first**: the render figure is the renderer's own work (upload, draw, hand the frame to the compositor), measured at 0.38 ms for 1080p into a 1.5× surface, so anything near the frame interval is either a much slower machine or a wait hiding in there (the 18.0 ms in the report that wrote §11's presentation row was 17.6 ms of waiting for a vertical blank) |
| `AudioUnderruns` | how often the device was found completely empty between two blocks, as the device counts it | every one is an audible gap; a trickle is the sound arriving just in time, a climb is the pipeline not feeding the device — and a renderer that cannot tell reports zero |
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
5. **Opening chooses nothing, so a host that never chooses has a file it cannot play.** `Play` is refused until one
   kind is selected, and turning every kind off returns the file to that state — paused, with nothing to play.
   Subtitles are the one kind that cannot be chosen at all: a subtitle stream is refused, while turning subtitles off
   is trivially satisfied. A file opened and left alone keeps its source — and its file — until it is stopped. A
   switch also restarts the read from where playback stands, which a source that cannot seek cannot do: on such a
   source the media already read ahead is given up on rather than re-read, and a source whose read blocks (a live one)
   cannot be interrupted by the switch until the read returns — which is why the two kinds are chosen one at a time
   and a live source wants both choices before it has given much media.
6. **A track is switched, never turned off, and subtitles are not selectable.** One video and one audio stream are
   played at a time, chosen by `SelectStream` (default: the first of each kind, skipping streams the demuxer says
   nothing about). Passing null to switch a kind off, and every subtitle stream, is refused with a message rather
   than ignored. A switch also rebuilds that kind's chain from scratch — the new decoder starts at the switch
   position, so a track whose codec has a long opening is not warmed up in advance — and a stream whose packets are
   interleaved badly enough to need a different source seek would show it as a longer gap than the track deserves.
7. **The clock waits for a position's first media, so a pathologically slow first decode freezes both stages.**
   The hold that keeps a picture from being skipped at the start of a position (§8) is released by the first
   frame, by the end of the stream, or by a stop — and by nothing else. A decoder that takes seconds to
   produce its first frame therefore pauses playback for seconds, by design: the alternative is to start the
   sound alone and lose the picture's opening.
8. **Two pipelines play two streams the operating system mixes, and each has its own volume and mute.** Each
   pipeline asks its factory for a renderer per open, so their buffers, clocks and flushes are separate, and each
   renderer drives the **stream's** volume service (`IAudioStreamVolume`), never the session's: a session belongs
   to the process, so a session control would move both players at once. WASAPI has no per-stream mute, so mute is
   the level going to zero while the level the user set is kept. What the two do share is the endpoint's session
   as far as the operating system's mixer is concerned — one app-wide slider still applies to both, which is the
   conventional behaviour. And because the mix happens in the audio engine, the two streams have no
   sample-accurate relationship — anything that needs one (a crossfade, stems, karaoke) belongs in a mixer inside
   **one** pipeline, not across two.
9. **Component factories are stateless or per-renderer; nothing is shared between two pipelines.** The audio
   factory hands out a renderer per call, and the Direct3D 11 factory creates a graphics device per renderer
   (which that renderer owns and disposes): a renderer draws through the device's immediate context, and a frame
   is a sequence of calls on it, so two renderers on one context let one player's state land between the other's
   setup and its draw. A device per renderer makes that impossible by construction, and
   `CreatesItsNextRendererAfterThePreviousOneWasLetGoOf` pins it — a cached device would be dead by then.
10. **A stream that ends before the file does is not yet *known* to have ended.** FFmpeg reports the end of the
   *container* (`AVERROR_EOF`), not of a stream: a stream simply stops producing packets, and `AVStream.duration` is
   metadata (right in MP4, often absent in MKV, meaningless when damaged or live). So the pipeline can know that the
   sound has *no data now*, but not that it will never have more. Today that shows as a hold: a mid-file audio gap
   pauses the clock, and only the container's end releases it. What makes the reported hang different is that it
   needs no such knowledge — the sound is simply over, the clock is the pipeline's own, and time carries on.
11. **The clock corrects itself to the device while the device plays, so it can step backwards by the sync
   tolerance.** A stalled device pulls the pipeline's clock back to itself every few milliseconds, which
   is how a stall keeps the picture waiting without freezing time altogether; the step is bounded by
   `ClockSyncToleranceMilliseconds` and is well inside the synchronizer's tolerances, but it does mean the clock is
   not strictly monotonic.
12. **A stalled device freezes playback silently.** The device is the truth about time while it holds audio, so a
   device that stops moving stops the clock with it: the picture holds, the sound has nothing more to give, and
   nothing in the pipeline says why (§9). This is the price of the watchdog that was removed — the pipeline cannot
   tell a device that is late from one that is broken, and a line that guessed wrong turned out to be worse than no
   line at all. What is left to the host is the shape of the freeze itself: `Position` stopped while the state is
   `Playing` and `StarvedFor` is null, which is the only combination that means "the device" rather than "a stage".
13. **A precise seek resumes at the first media *at or after* the request, not at the request itself.** Video
   resumes at the first frame at or after it and sound at the first whole block at or after it, so up to one
   frame interval and one audio block (tens of milliseconds) are skipped rather than trimmed; sample-accurate
   sound would need a buffer that can be sliced. A source that accepts a seek and then refuses it leaves the
   position published without moving the media: a refused *forward* precise seek therefore skips ahead to the
   position instead of standing still, which is the one behaviour a fast seek does not have.
14. **Changing the sound's format while it plays reopens the device, and the gap is audible.** The chain's version
   tells the audio thread that there is a newer snapshot; if its format differs it closes the device, opens it again
   for the new shape, and drops the clock offset so the next block re-reads the fresh device clock. What is lost is the
   audio the device held — up to its buffer, which the pipeline deliberately keeps large — so a filter that changes the
   format is heard as a short interruption. A host that cares can pause first; nothing in the pipeline pretends the
   switch is seamless.
15. **A fast seek past the end of the picture is exact only when the container says how long the picture is.** A
   container that declares no per-track length leaves nothing to compare the request against, so the seek stays a
   wish and resumes at the picture's last keyframe — the behaviour this rule exists to avoid, kept rather than
   guessed at. Matroska writes the length as a `DURATION` tag and MP4 as a stream field, and both are read; a
   container that writes neither is the gap.
16. **There are two containers, and a picture's treatment follows from the pair: what it is, and what the container
   expects.** A frame carries how its samples are to be read — `ColourInfo`: primaries, transfer and range, taken from
   the stream and from each decoded frame. The surface carries the other half, and is one of exactly two:
   - **SDR**, `B8G8R8A8_UNORM` with the colour space DXGI creates a chain with: gamma-encoded BT.709 relative to the
     display's white. An HDR picture is tone mapped into it — the transfer is undone, BT.2020 is brought to BT.709, the
     range is compressed and the result re-encoded. This is the container every SDR display needs and the only one such a
     display can show correctly. Reference white is 100 nits and lands at about 197 of 255; everything above 600 nits
     comes off one Hable shoulder instead of being clipped.
   - **HDR10**, `R10G10B10A2_UNORM` with `RGB_FULL_G2084_NONE_P2020` declared on it: PQ codes in BT.2020, which are
     absolute light levels. PQ content is handed over as it is — the shader only unpacks the planes and expands the
     limited range — so nothing is compressed unless the panel cannot reach the grade's peak. HLG content and SDR
     content are encoded into the same scale, at 1000 nits nominal and 203 nits respectively.
   The pairing is `VideoSurfaceTransforms.Select(kind, colour)` and it is the whole of the policy: six transforms, and a
   shader programme per layout and transform. `VideoSurfaceFactory` chooses the container from **both halves**: the picture
   must have range an SDR container would have to compress — `ColourInfo.IsHighDynamicRange`, so a PQ or HLG stream — and
   the display must be composed for HDR. A standard dynamic range picture gets the SDR container however capable the display
   is, because its white lands at the reference level either way and rebuilding the swap chain for it would gain nothing.
   The host's "Prefer HDR" setting (on by default) is exactly this choice; turned off it asks for the SDR container outright
   and the question is never asked. A host may also name a container outright, which is what makes either one testable on a
   display that cannot show it. A chain that refuses the ten-bit format or the colour space is disposed and the SDR one is
   used instead, because a PQ picture in a chain that still claims BT.709 gamma is a dim, flat picture with nothing to say
   why.
   What is not carried yet: the stream's mastering metadata (`MaxCLL`/`MaxFALL`), so a display's own mapping works from
   what it can measure rather than from what the grade declares; an HDR-to-HDR curve for a panel dimmer than the grade, so
   anything above the panel's peak is clipped by the display rather than rolled off here; the subtitle surface, which is
   still SDR and is therefore lifted by the compositor independently of the picture, leaving text dimmer than the highlights
   around it; and any reaction to the display changing HDR state while a file plays — the container is fixed when the chain
   is created, and the chain is created when the renderer is configured.
   The range field is read as limited, which is what every HDR delivery uses, and the eight-bit path assumes it too.

## Appendix A — the checklist for any change to this pipeline

1. Which thread's wait does this change add, remove or lengthen? If none, it is not an orchestration change.
2. Does any thread now wait for something another thread must do? That is the forbidden shape — no wait in
   this pipeline may depend on another stage's progress.
3. Does the change let a producer drop media? If yes, it is in the wrong layer: only the presentation thread
   may, and only under the best-effort rule.
4. Does anything new consult the clock? Only presentation and the audio sender may, and the audio sender only
   to keep it honest. And does it consult it *properly*? A media thread takes the clock once and reads its
   `Position`; the pipeline's `Position` is the host's face and answers zero when there is no clock, which is
   not a fact to decide on, and a null check per iteration is a case that cannot happen (§5).
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
30. Does the change hand a decision to a thread that only reads the input afterwards, or to the thread that
    holds the facts? The clock is the case that matters: it is placed, claimed, started and stopped only by
    the two render threads, each of which notices what changed by *reading* it (the change stamp, the epoch,
    the claim, its own verdict) and enters the lock only to decide — never a value pushed at a thread with the
    wake, and never a second thread reaching in (I10, §5; `KeepsTheClockWhereTheMediaPutItWhenSomethingElseChangesIt`).
31. Does a thread wait for something that only time can change? Then the wait is armed with a **fixed cadence**
    and never with a value derived from the clock's state (running, claimed, rate, how far behind the picture
    is): that derivation is a prediction whose subject belongs to another thread, and a wrong prediction is a
    hold that never arrives (§2.3, §8, I12; `HoldsTheClockBackWhenTheClockReachesTheThreshold`). A wait that
    only an entry can end is left with no time at all, because a missing wake must fail loudly rather than be
    papered over.
32. Does the change give a thread a new fact to read, or a new way to fail? Then it must either wake that
    thread's node (a data wake when the fact is an input, `WakeAll` when it is control) or be provably
    unreachable while the thread is asleep — and it must read its node's stamp *before* deciding anything.
    One wait per thread is the invariant (I18); the wake table is the proof (§2.3).
33. Does the change decide when time may pass? Then both stages must be asked, not just the one that has an
    opinion: a position that has produced nothing yet is starving on the sound's side (*the device holds
    nothing*) **and** on the picture's (*nothing shown, nothing waiting*), and the picture's answer must be a
    read of its own epoch-exact facts — never the decoder's tail, which lags the queue and still describes the
    previous position while a decode is in flight (§8; `HoldsTheClockBackUntilThePictureHasSomethingToShow`,
    `HoldsTheClockBackWhenAPositionMovesDuringADecode`).
34. Does a component need to wait for something — a device with no room, a buffer that must drain? Then it
    must not wait where it stands: it returns to the thread that called it, says what it is waiting for, and
    keeps its place so it can be resumed (§2.3, I20; `LetsGoOfTheBlockWhenThePositionMovesWhileTheDeviceHasNoRoom`,
    `SaysItHasNoRoomRatherThanWaiting`). The pipeline then waits on its own node, where a pause, a seek, a stop
    and the device's own progress all reach it — and a timeout offered as a substitute is a number nobody
    chose, turning a wait into a failure (§9).
35. Does the change add a wake for a fact that only *another* thread can publish? Then check the harder
    question first: **can that fact's transition change the answer at the moment it happens?** If it cannot,
    the wake is decoration and the cadence owns the consequence. The anchor is the precedent: opening its gate
    lets the picture judge, but at the claim nothing has been shown for the position and nothing is queued, so
    the picture is starving before the claim and starving after it — the verdict cannot flip. What a picture
    starved *afterwards* is caused by the clock advancing, which no wake can report (§8). A wake kept
    for a reason that does not hold is worse than no wake: it makes the table claim a dependency the code does
    not have, and the next reader reasons from it.
36. Does the change catch an exception? Then **a component does not diagnose; it preserves** — unless it has
    100% confidence about what is really happening. The original object must leave the component intact — type,
    message, stack — with the context of what was being attempted added beside it, never in place of it. An
    exception that is turned into "the device was lost" without the HRESULT in hand is a guess wearing the
    clothes of a diagnosis, and it sends the next reader looking in the wrong place; a guess that is *repeated*
    into a user-facing error sends the user there too. The one place a failure becomes state is `Fail`, and that
    is therefore the one place every failure is logged, with its exception. (Precedents: `WasapiAudioClock`
    keeps the first exception as the cause and reads as a frozen position rather than throwing at a reader;
    `PlaybackOperationResult.Failure(exception)` keeps the object; §9's stalled-device line is a *fact* — a
    position that has not moved — not an inference about why.)

## Appendix B — where each concept lives

| Concept | Where |
|---|---|
| The orchestration, threads, state machine, starvation, device watch | `PlaybackPipeline` |
| What a command may fail at, and what that failure does (nothing but answer) | `PlaybackPipeline`'s `PlayCore`/`PauseCore`/`SeekCore`/`StopCore`, each returning a `PlaybackResult`; `IMediaSource.CanSeek` for the refusal a source can predict |
| The one queue that decides, and what may run on it | `PlaybackPipeline` (the static queue), `TaskDispatcher` |
| "No maximum", and why | the three `MediaQueue<T>`s: none of them has a limit, and the frame window lives in the decoder |
| "Pace the decoder, keep everything" | `PlaybackPipeline.VideoDecodeCore`'s `HasRoomForAFrame` and `FrameQueueCapacity`, checked *before* a packet is taken |
| "The queue knows nothing" — not what the tag means, not what the end of a stream means, and not how much may be waiting | `MediaQueue<T>` (items, tags, a weight, a close, and an end entry that is handed over like any other) and the checks at both its ends in `PlaybackPipeline` |
| The end of a stream, and why an empty queue is not one | `MediaQueue<T>.EndStream` and the `IsLast` of `MediaItem<T>`; said by the reader (packets, audio) and by the video decode thread (frames, in `DrainVideo`) |
| The queue's whole surface, and why nothing in it waits | `MediaQueue<T>.TryEnqueue` (false only when closed), `EndStream`, `TryPeek`, `TryDequeue()`, `TryDequeue(MediaItem<T>)` (by identity), `Count`, `Weight`, `IsClosed`, `Clear`, `Dispose` — no `Monitor`, no timeout, no waiter |
| The one place a thread sleeps, and how a wake cannot be lost | `Internal/PipelineNode.cs`: one node per thread, the stamp read before the decision, a `Wait` that returns at once for a stamp that moved, a wake that never waits for the woken thread, and `WakeCount` so a parked pipeline can be proved not to be polling |
| The entry a consumer is handed, and why it is a class | `MediaItem<T>` — media (or nothing, for the end of a stream), the tag, and `IsLast`, travelling together. `TryDequeue(MediaItem<T>)` takes the entry that was looked at *by identity*, which is what makes looking and taking two sound steps rather than a race: a `Clear` disposes what it holds, so an entry that is still the head is the one that was seen, whatever a replacement entry's tag might be |
| The end of a file, and what earns the ending | `PlaybackPipeline`: `_endingEpoch` (the position the file ran out at), `_videoRenderEndedEpoch`/`_audioRenderEndedEpoch` (what each stage said about which position), and `TryNotifyPlaybackEnded` — the one decision, taken when the last word arrives and nowhere else |
| Which tracks are read, and why that belongs to the source | `MediaSourceHolder` (the `IMediaSource` and the two selected streams, as one component) published in `_source`'s slot, adopted and held by the reader; `PlaybackPipeline.SelectStreamCore` publishes the new holder and the rebuilt pair under one number, through `ApplySelection` (§2.5) |
| What a publication rebuilds, and what it leaves alone | `PlaybackPipeline.DiffSelection` — the holder in effect against the one handed over: the file by reference, each kind's stream by container index — whose answer `BuildChains` acts on, and `ChainChanges` reports back (§2.5) |
| What opening leaves undecided, and who decides it | `PlaybackPipeline.PreparePlayback` publishes a holder with no streams (so nothing is created or read) and `PlayCore` refuses to start while both kinds are unselected; the host chooses with `SelectStream(kind, stream)`, once per kind, and a null stream turns that kind off (§2.5) |
| Which package may be consumed, and by whom | `PipelineVersion` (one number per node: `Source`, `AudioDecoder`, `AudioRenderer`, `VideoDecoder`, `VideoRenderer`, plus `Max`, `Next` and the comparisons) and the verdicts `CompareOwn`/`CompareUpstream`/`CombineVerdict`; carried by `PacketEntry`, `FrameEntry` and `AudioEntry` (§2.4) |
| How a component reaches the thread that owns it | `ComponentSlot<T>` — `Publish` (queue thread) and `Update(out new, out old)` (owner thread), one slot per role for the pipeline's life, with `Reset` only while no thread runs (§2.4). No lock beside the slots: the queue serialises publication, and the slot's own lock covers publish-against-adopt |
| Epoch discipline and what a seek uses to interrupt | `PlaybackPipeline`: the epoch, the position published with it, and the reset of everything that belongs to a position are one `SeqLock<long>.Change`; media threads read it through `_epochs.Read()`. The protocol itself — the three reads, the retry, the wait for a change that outlasts the attempts, and how a value of several fields survives it — lives in `Internal/SeqLock.cs` |
| When it is too late to show a frame | `ISynchronizer` (default: 2 ms early tolerance, 40 ms late) |
| What a picture's samples mean | `ColourInfo` — primaries, transfer, range — on `VideoStreamInfo` and on every `RawVideoFrame`, mapped from FFmpeg's enumerations once, in `FFmpegInterop.ToColourInfo` |
| Which container the picture goes into, and what that costs | `VideoSurfaceKind` (`Sdr`/`Hdr10`) plus `IVideoSurface` with one implementation per container (`SdrVideoSurface`, `Hdr10VideoSurface`), sharing the swap chain in `SwapChainVideoSurface`. `VideoSurfaceFactory` chooses, `DxgiDisplay` says whether the desktop is composed for HDR, and `VideoSurfaceTransforms.Select` turns (container, frame colour) into one of six transforms (§14.16) |
| Which programme draws a frame | `D3D11VideoShaders`: `GetPixelShader(format, transform)` for a memory frame and `GetArrayShader(transform)` for a hardware one, both falling back to the plain programme when the pair has none. A registry holds entry-point names only; code is compiled once per process and the device object made on first use, so opening a file pays for the two or three programmes it draws. `ValidateProgrammes` proves all of them in a test. The conversion itself is one shoulder in HLSL (§14.16) |
| What a paused file shows, and what wakes that thread | `PlaybackPipeline.VideoRenderCore`, whose one loop both presents and offers the still: `presenting` (the state the presentation steps are for) and `offeringTheStill` (a paused position whose picture has not been shown yet) name the two things the thread does, and `shownEpoch`/`endedForEpoch` are what tell it there is nothing left to offer. The frame arriving in the queue is the only wake the still needs |
| Who wakes the threads, and when | `PlaybackPipeline`: `WakeAll` (control — the state, the epoch, the stop, the ending) called by each command when its own change is complete (`PlayCore`/`PauseCore` after the state, `SeekCore` after the epoch and the clears, `StopCore` before them), by `Fail`/`TryNotifyPlaybackEnded`; and the data wakes `TryWakeReader`/`TryWakeVideoDecode`/`TryWakeVideoRender`/`TryWakeAudioRender`, called by the thread that changed the input (after every `Try*` queue call) — never by `SetState` |
| The clock contract, and the wall-clock fallback | `IPlaybackClock`, `StopwatchClock` |
| Who owns the clock, and how a change reaches it | `PlaybackPipeline`: `_clockLock` with `ApplyClock` (the one decision, taking the caller's clock), `IsVideoStarved`/`SetAudioStarved` (the verdicts a render thread publishes), and `_anchoredEpoch` (the position whose clock the media claimed). Each render thread takes the clock once for its whole life (it is made before the threads start and released after the joins) and never reads the host's `Position`; a render thread re-decides when something it reads has changed — its node's stamp, the epoch, its verdict, the claim — so a wake that finds nothing changed takes no lock, and a seek only publishes a request (§5) |
| Device position as the source of time, and where a reposition takes its zero point | the audio renderer's clock (`WasapiAudioClock`), which the device's own thread resets (`WasapiAudioRenderer.Flush`) |
| A hand-off that can be suspended, and who decides what to do about it | `IAudioSubmit` + `AudioSubmitYield` (the contract: single use, `Dispose` abandons, a component never waits, a queue-based backend must impose its own high-water mark), `WasapiAudioRenderer.Submit` (interleave once, write what fits, yield `NoRoom`) and `PlaybackPipeline.AudioRenderCore`'s in-flight slot (`inFlight`/`inFlightBuffer`, released on completion, on a seek's epoch change, and in the loop's `finally`) |
| Who creates the sound's device, and how long it lives | `IComponentFactory<IAudioRenderer>`, handed to the pipeline in place of an instance: `PlaybackPipeline.PrepareAudio` asks for one per open, publishes it, applies the host's `Volume`/`IsMuted`, and `StopCore` flushes and disposes it after the joins. `WasapiAudioRendererFactory` caches nothing — one renderer per call is what makes two pipelines two streams. The host keeps only the values it wants the device to have (`PlaybackEngineAdapter._volume`/`_muted`) |
| What happens when the sound's device fails | `WasapiAudioClock` latches the **first** exception (`Cause`) and freezes at the last placed position, so no reader is thrown at; `WasapiAudioRenderer` reports it on the next `Submit`/`Flush` (`DeviceFailure`), and `PlaybackPipeline.Fail` turns it into `Error` and logs the exception — one choke point for every failure in the engine. `IWasapiClockDevice` is the seam that lets a test lose a device without unplugging one |
| Per-player volume and mute, and why they are per stream | `WasapiVolumeControl` (applies the level to the **stream** through `IAudioStreamVolume`; mute is the level going to zero) and `WasapiAudioRenderer.Volume`/`IsMuted`, which hold the level the user set. `IWasapiStreamVolume` is the seam for the arithmetic; `WasapiVolumeControlTests` pins it |
| Two players at once: what is deliberately not shared | the audio renderer (per open, from a stateless factory), the graphics device (per renderer, owned and disposed by `D3D11VideoRenderer`), the orchestration queue (per pipeline) and the clock (per pipeline). `PlaysTwoFilesAtOnceWithoutEitherTouchingTheOther` and `CreatesItsNextRendererAfterThePreviousOneWasLetGoOf` are the guards |
