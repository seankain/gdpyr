# gdpyr — Which RL, and why

A reading of the field as it stands, aimed at one question: what should actually
be run against a gdpyr seat, and what is worth ignoring.

Companion documents:
[`TRAINING.md`](TRAINING.md) — how to start a run, and what the flags do ·
[`AGENT_API.md`](AGENT_API.md) — the protocol both seats sit behind ·
[`NETCODE.md`](NETCODE.md) — the tick model that decides throughput.

---

## 1. The short answer

**PPO is still the default, and Q-learning is not dead — it is a specialist.**
That is the honest summary of a decade of churn, and it has been stable for long
enough to plan around:

- **PPO** (Schulman et al., 2017) remains the thing you reach for first on a
  control problem with a dense-ish reward and a long episode. It is on-policy, it
  is forgiving of hyperparameters relative to everything around it, it scales by
  adding actors, and almost every large game-playing result of the last decade is
  PPO or a close relative — OpenAI Five, and the actor-critic core of AlphaStar.
  It is also, since 2022, the workhorse of RLHF, which is why it has had far more
  engineering attention than its age suggests.
- **DQN and its descendants** are not obsolete; they answer a different question.
  Off-policy value learning with a replay buffer reuses every transition many
  times, which matters when *stepping the environment is the expensive part*.
  Rainbow (Hessel et al., 2017) is the combination that made the family
  respectable — double Q, duelling heads, prioritized replay, n-step returns,
  distributional value, noisy exploration — and a modern "DQN" means that, not
  the 2013 paper.

**That distinction is the whole decision, and for gdpyr it is genuinely close.**
A gdpyr server produces 60 ticks of game per second of wall clock and cannot be
made to go faster ([`AGENT_API.md`](AGENT_API.md) §5.4), so samples are expensive
and sample efficiency is worth paying for. PPO is still where to start, because
its failure modes are legible and its wall-clock throughput scales with instances
you can just add. Run the off-policy arm second, on the same reward and the same
action space, and compare — the trainer ships both, and switching is `--algo dqn`.

What has actually changed since the era the question comes from is **not the
algorithm**. It is everything around it: how partial observability is handled,
how the action space is shaped, how opponents are chosen, and how results are
measured. Sections 4 through 8.

## 2. The families, and what each is for

| Family | Representative | What it is good at | For gdpyr |
|---|---|---|---|
| On-policy policy gradient | PPO, TRPO | Stable, parallel, forgiving; the default | **Start here.** Shipped as `--algo ppo` |
| Off-policy value | DQN → Rainbow, R2D2 | Sample efficiency; discrete actions | **Run second.** Shipped as `--algo dqn` |
| Off-policy actor-critic | SAC (discrete variants), TD3 | Continuous control; sample efficiency | Not shipped; SAC's home is continuous torque control |
| Distributed on-policy | IMPALA (V-trace), SEED RL | Thousands of actors, one learner | Only once one box is saturated (§5) |
| Model-based | DreamerV3, MuZero, EfficientZero | Best-in-class sample efficiency | The right answer if samples stay the bottleneck, and the most work; no C# implementation |
| Offline / sequence | Decision Transformer, IQL, CQL | Learning from logged play, no environment | Interesting here: playtest traces are already logged (§9.1 of the API) |
| Black-box | CMA-ES, NEAT, GA | No gradients, trivially parallel | A fine baseline for a small policy; rarely the endpoint |
| LLM post-training | PPO-RLHF, DPO, GRPO | Aligning language models | **Not this problem.** See below |

**On the LLM detour.** Since 2023 most public writing about "RL" has been about
language models, where the environment is a scoring function over one response and
the interesting work is in the preference signal (DPO removes the RL loop
entirely; GRPO drops the learned critic and normalizes rewards within a group of
samples). None of it transfers to a 60 Hz partially observed game with a
twenty-minute horizon. If a search for "state of the art RL" turns up an argument
about critics and KL penalties on token sequences, it is about a different field
wearing the same name.

**On model-based.** DreamerV3 (Hafner et al., 2023) is the most interesting thing
on this table for gdpyr's actual constraint — it learns a world model and does most
of its learning inside it, which is precisely what you want when the environment
costs wall clock. It is also a substantial implementation with no C# port, so
taking it would mean moving the learner to Python against
[`tools/gdpyr_env/`](../tools/gdpyr_env/). That is a real option and a real
project; it is not the first thing to do.

## 3. What to run here, in order

1. **PPO on the ground seat against the scripted bots**, with the shipped reward
   and action space, until the win rate against `BotStrategist` is clearly above
   what a random policy gets. This is the smoke test for the whole pipeline: if it
   does not move, the problem is the plumbing or the reward, not the algorithm.

   ```bash
   ./scripts/train.sh --steps 200000 --history 4 --save runs/ppo-ground \
       --metrics runs/ppo-ground.csv
   ```

2. **The same thing on the strategist seat.** Different rate (2 Hz), different
   horizon (a whole round), different reward — and a far smaller action space, so
   it should be the easier of the two to move.

   ```bash
   ./scripts/train.sh --policy strategist --steps 100000 --save runs/ppo-strategist \
       --metrics runs/ppo-strategist.csv
   ```

3. **Rainbow-flavoured DQN on whichever of the two moved**, same reward, same
   action space, same evaluation. This is the comparison that answers the question
   this document opens with *for this game* rather than in general.

   ```bash
   ./scripts/train.sh --algo dqn --steps 200000 --save runs/dqn-ground
   ```

4. **Only then**, self-play: freeze one side, train the other, alternate (§7).

Each of those is one number at the end — the win rate over decided rounds, which
`gdpyr-train` prints and `scripts/evaluate.sh` measures properly (§6).

## 4. Partial observability is the part that bites

Neither gdpyr seat is Markov in one frame.

- A ground policy sees eight contacts **its own eyes acquired**, a 16-ray fan and
  its own state ([`AGENT_API.md`](AGENT_API.md) §6.1). It cannot see behind
  itself, and one frame does not say whether the contact ahead is advancing or
  retreating.
- A strategist sees ghosts that decay (§6.2). A ghost forty seconds old is a
  memory, and whether the enemy has moved since is exactly the thing it has to
  guess.

The two standard answers:

**Frame stacking.** Concatenate the last N observations into the state. Cheap,
model-agnostic, and enough for most velocity-and-heading questions — it is what
made Atari work in 2015 and it has not stopped working. Shipped: `--history N`
(`tools/Gdpyr.AgentClient/ObservationStack.cs`). `--history 4` at 15 Hz gives a
ground policy a quarter second of past, which is roughly the reaction window the
bots are tuned to.

**Recurrence.** An LSTM or GRU in the policy, so the network carries its own
belief state; R2D2 (Kapturowski et al., 2019) is the canonical treatment of doing
this with a replay buffer, and it is how AlphaStar handled a far worse version of
this problem. RLMatrix does not expose a recurrent policy through the interface
this trainer uses, so **the shipped answer here is frame stacking**. If
recurrence turns out to be the thing that matters, that is a reason to move the
learner to Python rather than a reason to fight the C# trainer.

Stacking multiplies the input width by N — 145 floats becomes 580 at `--history 4`
on the ground, and 1,092 becomes 4,368 for a strategist, which is a good reason to
leave the strategist at 1 or 2 and let the ghost ages in its vector carry the
history instead. They already do: `ticks_since_seen` **is** a memory the server
maintains.

## 5. Throughput, and the sample budget

This is the number that decides how ambitious to be.

| | Ground seat | Strategist seat |
|---|---|---|
| Decision rate | 15 Hz (`--step-mul 4`) | 2 Hz (`--step-mul 30`) |
| Decisions per second of wall clock, one instance | 15 | 2 |
| Default episode | 1,800 decisions ≈ 2 min of round | 2,400 decisions = a whole 20-min round |
| Decisions in a 20-minute round | 18,000 | 2,400 |
| Wall clock for 100k decisions, one instance | ≈ 1.9 h | ≈ 13.9 h |

A gdpyr server runs the simulation at a fixed 60 Hz and a headless one is capped
there, deliberately ([`AGENT_API.md`](AGENT_API.md) §5.4). **So the first answer to
wanting more samples is more instances, not a faster one** — `--ports
7900,7901,7902,7903` feeds four servers into one learner, and how many fit on a box
is a measurement (the server frame-time row on the debug HUD,
[`NETCODE.md`](NETCODE.md) §8).

The second answer, for the strategist specifically, is **a shorter round**.
`RoundDurationMinutes` in `Match/default_gamemode.tres` is 20; a training round of
5 changes what is being learned (the clock is a win condition for the ground
force) and makes a strategist episode four times cheaper. That is a legitimate
curriculum choice as long as the final evaluation is run at the real duration, and
an illegitimate one if it is not.

Two more things that cost samples and are worth knowing before blaming the
algorithm:

- **Train in stepped mode** — it is the default and [`TRAINING.md`](TRAINING.md) §5
  explains why. In real time a slow optimizer step means the seat's bot played some
  of the transitions you are about to learn from.
- **A truncated episode is not a terminal one.** Both environments end an episode
  at `--episode-steps` and report `done`, and a value function trained on that
  learns that the world ends after two minutes. This is the standard time-limit
  bootstrapping problem; the mitigation here is to keep the cap long enough that
  most episodes end on a real `round_end`, and to read the win rate rather than
  the return when comparing runs.

## 6. Evaluation, which is where most RL projects quietly fail

**Reward is not a result.** It is a number this repository made up
(`GroundReward.cs`, `StrategistReward.cs`), and a policy that increases it may
simply have found a way to farm the shaping term. The game's own numbers are the
round outcome and the tickets, and those are what to report.

The protocol:

```bash
./scripts/evaluate.sh runs/ppo-ground --episodes 20
./scripts/evaluate.sh runs/ppo-strategist --policy strategist --episodes 20
```

That plays the checkpoint with learning switched off and prints episodes, decided
rounds and the win rate over the decided ones. Four rules around it:

1. **Claims are distributional.** A gdpyr episode is not reproducible — `reset`
   starts a fresh round rather than a repeatable one, and every stochastic
   decision hashes the absolute server tick, which never rewinds
   ([`AGENT_API.md`](AGENT_API.md) §3.1). "Wins 12 of 20 rounds" is a claim; "wins
   the round that starts at tick 51,204" is not.
2. **A truncated episode has no outcome**, and the win rate's denominator is
   decided rounds. A ground run at the default two-minute cap decides very few, so
   evaluate with `--episode-steps 18000` if the question is about rounds.
3. **Ablate the shaping term.** `ObjectiveProgress` in `GroundReward.cs` is what
   stops a fresh policy standing still for ever; it is also the first thing to set
   to zero when you want to know what the policy actually learned. If the win rate
   collapses without it, the policy learned to walk forwards and nothing else.
4. **Never evaluate under `--agent-omniscient`.** It is stamped into every
   observation frame and the playtest harness fails any scenario that claims a win
   under it (§6.4, §9), because a result obtained by cheating is a result about a
   different game.

Check `scripts/playtest.sh` when a run stops improving and you suspect the ground
moved: it answers "did the *game* change" with a deterministic exit code and no
learner involved.

## 7. Self-play, and why not to start with it

The tempting move is to point both learners at one server and let them fight. Do
not start there.

**Simultaneous self-play is non-stationary on both sides at once.** Each policy's
environment is the other policy, which is changing under it, and the standard
failure is a cycle — a counter to a counter to the thing you started with, with no
monotone progress and a reward curve that says nothing. The field's answer, from
AlphaStar's league and long before it from fictitious self-play, is **to train
against a population of frozen opponents**, not against a live one.

So, in order:

1. **Both sides against the scripted bots.** `BotBrain` and `BotStrategist` are a
   fixed, readable opponent, and "beats the bot" is a claim that stays true
   tomorrow.
2. **Freeze one, train the other.** `scripts/train-selfplay.sh --freeze ground
   --load-ground runs/ppo-ground` puts a trained ground policy in play-only mode
   and trains the strategist against it.
3. **Alternate.** Freeze the new strategist, retrain the ground policy against it,
   and keep every checkpoint. The set of frozen checkpoints *is* the league, and
   evaluating a new policy against all of them is what tells you whether you are
   going round in circles.
4. **Only then**, both learning at once, with the checkpoint pool as the
   regression test.

The mechanics of two learners on one server — who owns the round, why
`--no-reset` exists, why the step timeout has to go up — are in
[`TRAINING.md`](TRAINING.md) §8. The short version is that stepped mode makes two
learners a barrier rather than a race, and `reset` ends the round for everybody, so
exactly one process may own the episode boundary.

## 8. Things about this game that will bite a policy

- **The ceilings are on by default.** An attached seat turns at a bot's 4.5 rad/s
  and a strategist gets eight commands a second
  ([`AGENT_API.md`](AGENT_API.md) §7.3). A policy trained under
  `--agent-unbounded` learns to snap-aim and does not transfer. The flag is for
  research runs and is stamped into every trace.
- **The seat is a lease.** If a policy goes quiet for its grace window the bot
  takes the seat back for a while (§2.1), and in real-time mode that is silent.
  `gdpyr-train` prints `bot fallbacks` when it happens; a run with a rising count
  is a run learning from somebody else's actions.
- **The action space is a hypothesis, not an interface.** Six heads of five on the
  ground, six of eight in the chair, and both are files in
  `tools/Gdpyr.AgentClient/` written to be disagreed with. If a policy plateaus,
  ask whether the thing it needs to do is *expressible* before reaching for
  another algorithm.
- **The reward is a hypothesis too**, and the game deliberately emits none (§8 of
  the API). Every number in `GroundReward.cs` and `StrategistReward.cs` is a
  gameplay claim; changing one changes what "the policy got better" means.
- **Unit ids are not in the strategist's observation** and an order names them, so
  every strategist decision asks `list_units` (§7.5). If you write your own
  client, that join is the thing to get right — a client-side roster inferred from
  the event stream drifts the first time a corpse outlives its `unit_lost`.

## 9. The short reading list

Ordered by what it would change about a run here, not by date.

| | |
|---|---|
| Schulman et al., *Proximal Policy Optimization*, 2017 | The default. Read the clipped objective and the GAE paper behind it |
| Hessel et al., *Rainbow*, 2017 | What "DQN" means now, and which six ideas are doing the work |
| Andrychowicz et al., *What Matters in On-Policy RL*, 2020 | The empirical study that saves the most wall clock: normalization, initialization, and which knobs actually move |
| Kapturowski et al., *R2D2*, 2019 | Recurrence with a replay buffer, if §4's frame stacking runs out |
| Vinyals et al., *AlphaStar*, 2019 | The league, and the only serious public treatment of an RTS action space |
| Berner et al., *OpenAI Five*, 2019 | PPO at scale on a long-horizon team game; the surgery and reward-shaping sections |
| Hafner et al., *DreamerV3*, 2023 | Where to go if samples stay the bottleneck |
| Chen et al., *Decision Transformer*, 2021 | If the playtest traces ever become a dataset |

And two things not to read for this problem: anything about RLHF or DPO, which is
a different field with the same name (§2); and any benchmark result on Atari,
which has a reset that actually resets.
