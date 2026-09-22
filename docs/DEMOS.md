# gdpyr — Demos: recording a round, and watching it back

`record game.demo` in the console, `stoprecord` when you are done, `playdemo game.demo` from the
main menu. That is the whole of it from the outside.

From the inside it is the idea Quake III's `com_journal` is named after, pointed at the one place
this project already has for it. The simulation reads nothing but the recorded `InputFrame` for the
tick ([`NETCODE.md`](NETCODE.md) §3.1), and `PlayerManager.SimulatePlayers` is the single line where
a character's intent — a socket, a device or a bot's brain — becomes that frame. Write the frame out
there and you have written the round down: everything that happened is a function of those frames
and the tick they were applied on.

What follows is the file format (§2), the console (§3), what each kind of process records (§4), what
playback does with it (§5), and what this first cut deliberately does not do (§6).

---

## 1. Two kinds of demo, because there are two kinds of process

A demo is recorded by whoever is running it, and what they *have* to record with is not the same
thing.

| | recorded by | holds | played back by |
|---|---|---|---|
| **Journal** | the authority — a server, a listen host, or a round played offline | every character's resolved `InputFrame`, every tick, plus the snapshots the authority broadcast | re-running the movement step over the journal, corrected by the snapshots |
| **Stream** | a client | its own input, and the snapshots it was sent | interpolating the snapshots, exactly as the live client did |

The asymmetry is the authority model, not an omission. A client is never told what anybody else
pressed — that is the whole of §1 of the netcode design — so a client cannot journal a round it was
not the authority for. What it *can* record is everything it was sent, which is what a Source or
Quake demo is, and which is enough to watch.

Nothing chooses between them. `record` looks at what this process is and writes the kind it can.

---

## 2. The file

`user://demos/<name>`, which resolves to the platform's writable directory — `~/.local/share/godot/
app_userdata/gdpyr/demos` on Linux. `DemoFiles.Root` prints it, and so does `demos` in the console.

### 2.1 The header

Eight bytes of magic (`GDPYRDEM`), a format version, and then what a reader has to know before the
first record means anything: the kind, the three rates the recording was made at, the first tick,
the wall clock, the round's seed, the peer that recorded it, the map, and a free-text name for the
machine.

The rates are in the file rather than assumed because they are protocol constants
(`SimConfig.TickRate`). A demo recorded at 60 Hz and replayed by a build that ticks at anything else
is not the same round — every recorded tick index means something different — so
`DemoHeader.IsPlayable` refuses it rather than scaling it. Same for a format version this build does
not read: half a decoded round is worse than a refusal.

### 2.2 The records

A flat sequence of tagged, length-delimited records. Little endian, no padding, no self-description.

| tag | record | body |
|---|---|---|
| 0 | `End` | — |
| 1 | `Tick` | `u32` — opens a block; everything after belongs to this tick |
| 2 | `Input` | `i32` peer, then the 12 bytes `InputCodec` puts on the wire |
| 3 | `Spawn` | `i32` peer, `u16` spawn index, `u8` flags (bot) |
| 4 | `Despawn` | `i32` peer |
| 5 | `PlayerSnapshot` | `u16` length, then a `SnapshotCodec` payload verbatim |
| 6 | `UnitSnapshot` | `u16` length, then a `UnitSnapshotCodec` payload verbatim |
| 7 | `Mark` | `u8` length, then UTF-8 |

Three things are worth saying about that table.

**The two payloads are the snapshot codecs' own bytes, copied.** A demo costs no second encoding of
anything, and a codec change is a version bump here rather than a translation layer. The price is
that the demo version is coupled to the wire version, which is the right way round: those bytes
already *are* the protocol.

**Tick markers are lazy, and never go backwards.** A marker is written only when the tick a record
is stamped with changes, so a tick nothing happened on costs nothing. A record stamped with an
earlier tick joins the block being written rather than opening an older one — on the authority that
never fires, because there is one clock there and it counts up, but a client has three
([`NETCODE.md`](NETCODE.md) §2): its input clock runs ahead of its estimate of the server's tick,
and the snapshots it is recording are from a tick that has already happened. The tick a *payload* is
about is inside the payload and is untouched. The block index is the order things were seen in, and
that is all playback asks of it.

**An unknown tag ends the stream.** The reader does not resynchronize on a tag byte, because a
payload's contents would read as records. Reading is otherwise total, like every other decoder in
`Scripts/Sim/`: a truncated, spliced or hostile file ends with a reason rather than an exception. A
demo arrives from a disk, from a zip somebody mailed around, or off the end of a process that was
killed mid-round, and none of those may take a client down.

### 2.3 What it costs

A journalled tick is 5 bytes for the marker and 17 per character — a tag, a peer id and the frame.
Eight players at 60 Hz is about 8.4 KB/s. The keyframes are the rest: a full player snapshot is
`5 + 29n` bytes at 30 Hz and a unit snapshot `5 + 13n` at 20 Hz, so a busy round with fifty units
adds roughly 24 KB/s. Call it a megabyte a half-minute, or a few hundred megabytes for a long
evening.

The journal is the cheap half, and that is the argument for journalling rather than recording state:
the round costs what the players pressed.

---

## 3. The console

`~` — shift and the backtick — drops it down. `` ` `` on its own is still the net debug HUD, which it
has been since M1 ([`NETCODE.md`](NETCODE.md) §8); they are the same physical key and the two actions
are matched exactly, so neither answers the other's press.

The console is an autoload, so it is there at the main menu as well as in the map: `playdemo` is
asked for before there is a round and `record` is asked for during one. A dedicated server builds no
console — it has no window — and records with a flag instead (§4.3).

Up and down walk the history, tab completes a command, escape closes it. While it is down the
keyboard belongs to it: `Input.IsActionPressed` reports the physical state of a key whatever has
focus, so without an explicit flag (`Core/InputFocus.cs`) typing `record` would also reload the
weapon and typing a name would walk you into a wall.

| command | |
|---|---|
| `help [command]` | what these do |
| `record <name>` | start writing a demo of this round |
| `stoprecord` | close it |
| `mark <text>` | put a note in the demo at this tick |
| `demos` | what is in the demo folder |
| `demoinfo <name>` | what is in one, without playing it |
| `playdemo <name>` | watch one |
| `stopdemo` | stop watching |
| `demospeed <1-8>` | recorded ticks per frame |
| `demopause` | hold the read head where it is |
| `demofollow [peer]` | sit on a player's shoulder; no peer frees the camera |
| `clear`, `echo`, `quit` | the usual |

### 3.1 Names

`record game.demo` writes `game.demo`; `record game` writes `game.dem`. A name is a file name and
nothing else — no separator, no drive, no `..`, no control character, no leading dot — because the
console is a text box that names a file this process then writes, and that is a trust boundary. The
rules are `DemoFormat.TryParseName`, they are engine-free, and `--record` and `--playdemo` go
through the same function so there is one answer to what a demo is called.

### 3.2 `demoinfo`

Walks the whole file and counts it: kind, when and by whom, the map, the tick range, how many
records of each sort, the seed, and any marks with the time they were written at. A count of records
is the only thing that distinguishes a demo that was cut off from one that recorded a round in which
nothing happened, which is why it reads the lot rather than stopping at the header.

---

## 4. Recording

### 4.1 On the authority

Three hooks, and they are all in `PlayerManager`:

1. **The journal.** `SimulatePlayers` resolves each character's frame from one of three sources and
   past that point nothing in the simulation knows which it was. The frame is written there, before
   `Simulate` applies the rules a frame is then put through — a dead player's look-only frame is
   *derived* on both sides rather than recorded on one, so the file holds intent and playback
   re-applies the rule.
2. **The keyframes.** `ReplicateSnapshot` already builds the roster's snapshot at 30 Hz;
   `UnitManager` already builds the units' at 20. Both now build it when a demo wants one even if no
   peer does, which is what lets a round played entirely on your own be recorded — the cheapest way
   to record anything.
3. **The roster.** Spawns and despawns, stamped with the tick that has just been simulated: a peer
   joins on the idle frame between two ticks, and a character that first moves on the next tick is
   a character that spawned at the end of this one.

The keyframe an authority records is the **unfiltered** roster, fog and all. The fog is a rule about
what a *player* may know during a round ([`NETCODE.md`](NETCODE.md) §6.2) and there is no player
here; a demo recorded by the authority is a record of the round rather than of one seat's view of
it. If that is the wrong trade for a given room — a recorded round handed to somebody who is about
to play the next one is a recorded round they can study — record it from a client instead, which
gets exactly what it was sent.

`record` mid-round writes the roster as it stands before the first tick of it, so the inputs that
follow are never about characters that never spawned.

### 4.2 On a client

Its own input frame per tick, blocked under its *server-tick estimate* rather than under the input
clock that stamped the frame (§2.2), and the snapshots it was sent — recorded where they are
decoded, because that is where the packet's own tick is in hand, and a demo's records are ordered by
the tick they are about.

### 4.3 From the command line

```bash
godot --path . --headless -- --server --record tuesday       # a whole session, unattended
godot --path . -- --client 192.168.1.20 --record game.demo   # the same, from a seat
```

Recording starts on the round's first tick. A dedicated server has no console to type into, so this
is how a box records; a failure to open the file is a warning rather than fatal, because a round
that is not being recorded is still a round and a full disk should not stop a server starting.

`--playdemo <name>` is the other half and goes straight past the main menu into the map. It names no
mode and refuses one beside it: a demo is watched, not played, so there is no authority, no peer and
nobody to be. A dedicated-server build refuses it outright — there is nowhere to draw it.

---

## 5. Playback

### 5.1 Getting there

From the main menu, `playdemo` loads the map and holds the file until `PlayerManager` has somewhere
to put it. From a demo already playing, a second one goes straight over the first. From a **live
round** it is refused: the roster, the clock and the prediction ledger are all built around this
process's place in that round, and none of them is rebuilt by loading a file — the same reason
leaving a round you are in is still quitting the process ([`LAN.md`](LAN.md) §4).

Playback replaces the tick loop rather than running beside it. No device is sampled, no bot thinks,
no packet is sent, the agent channel is not opened, and nothing is registered with the round — a
replayed character is not on a team, is not asked which side it wants and has no ticket to lose.

### 5.2 What each kind does

**A journal is re-simulated.** Each recorded input frame is put through `fps_controller.Simulate` —
the same function the authority ran, over the same frames, at the full 60 Hz rather than
interpolated at the snapshot rate. Every second tick the keyframe arrives and corrects it, exactly
the way a client applies a correction to its own character. That is what bounds the drift: the
re-simulation is free to disagree with the recording between keyframes and cannot accumulate past
one of them.

**A stream is interpolated.** There is no journal to re-run, so the keyframes go to the same
`SnapshotInterpolator` a live client uses and the bodies are drawn an interpolation delay behind the
newest of them.

**Units are keyframes in both.** Units are server-simulated and never predicted, so a client only
ever sees unit snapshots anyway; playback is that client. One difference from the wire: a unit the
demo has not mentioned is *created* from the snapshot and one missing from the newest snapshot is
despawned. On the wire that would be wrong twice over — a unit is announced by its own reliable
message, and a snapshot is an unreliable datagram that may simply not have arrived, so an absence
would kill the whole army on one dropped packet. A file drops nothing and reorders nothing, so there
the snapshot is the roster.

The render clock is measured against the newest keyframe rather than against the block index,
because the two are the same clock only in a journal (§2.2).

### 5.3 The camera

A replay has no local player, so no camera in the scene wants to be current and `DemoCamera` is it.
It flies — WASD, space and crouch for height, sprint for four times the speed, mouse to look — and
`demofollow <peer>` sits it behind that character's eye, pointing where they point. Touching the
movement keys lets go again, because the one thing that should never need a command is getting the
camera back.

It runs on the render frame on purpose: the read head advances on the physics tick and can be paused
or run at eight times speed, and a camera that stopped when the demo was paused would make a paused
demo unusable.

`demospeed` skips nothing — it applies more recorded ticks per frame — so a fast-forwarded demo's
state is right rather than approximately right.

There is no pause menu in a replay — the one in the map belongs to a local player's interface and is
stripped along with the rest of it — so leaving is `~` and then `quit`. `stopdemo` stops the read
head and leaves the field and the camera where they were; the next `playdemo` clears the field
itself.

A one-line bar in the top right says which file, how far in, at what speed and whether it is held.
It is on the console's layer rather than in the net debug HUD because that panel lives in the local
player's interface ([`NETCODE.md`](NETCODE.md) §8) and a replay has no local player.

---

## 6. What this does not do yet

Stated rather than discovered:

- **Projectiles, tracers, hit effects and damage numbers are not replayed.** The journal carries the
  inputs and the keyframes carry the outcome — a body that stops moving and goes invisible is a
  body that died — but weapons are not re-fired during playback, so a replay shows the shooting
  without the bullets. Re-running `CombatManager.ServerSimulate` over the journal is what would fix
  it, and it needs §6's next item first.
- **The RTS half is watched, not re-simulated.** Units, barracks queues and the economy come from
  the recorded unit snapshots. The strategist's orders are not journalled — they arrive as their own
  RPCs rather than as an `InputFrame`, so the one place §4.1 hooks does not see them — and until
  they are, re-simulating the round would produce an army that never got built.
- **Structures are not in a demo at all.** Pillboxes, sandbag walls and sniper towers
  ([`NETCODE.md`](NETCODE.md) §10.5) are not in the unit snapshot and their three messages are not
  journalled, so a replay shows builders working on nothing and riflemen crouching behind thin air.
  Recording `ServerSpawnStructure` / `ServerStructureState` / `ServerDespawnStructure` as three more
  record tags is the fix, and it is a demo version bump.
- **No scrubbing.** A demo is read forwards. Seeking backwards means either keeping every keyframe
  in memory or re-reading from the start, and neither is worth doing before somebody wants it.
- **No round-trip test.** The format is tested end to end over a `MemoryStream`
  (`Tests/DemoCodecTests.cs`); that a recorded round *replays as the same round* is not, because
  asserting it needs an engine and this repository's test suite deliberately does not have one. The
  honest place for that assertion is the playtest harness ([`AGENT_API.md`](AGENT_API.md) §9), which
  already runs a seeded episode headlessly and already has a divergence probe pointed at exactly
  this question.
- **The demo version is tied to the wire version.** §2.2 says why. A protocol change invalidates old
  demos, and `DemoHeader.IsPlayable` is what makes that a refusal rather than a mystery.
