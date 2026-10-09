# HTN bots — research and implementation plan

Status: **H6 done: the HTN bots are the default. The playtest with people and the display checks
of H2–H4 are outstanding** (§6). FluidHTN is vendored at
`ThirdParty/FluidHTN` and compiled into the game and the tests; `PooledHtnFactory` is in
`Scripts/Sim/Htn`; `--bot-ai legacy|htn` and `GameModeDefinition.BotAi` are parsed;
`HtnPlannerTests` holds probes P1–P8 in `dotnet test`. H1 put the four boards of §4.3 in
`Scripts/Sim/Htn` and fed three of them in the running game: each side's `ContactMemory` and the
strategist's `SquadBoard`, with the order issuer on every unit; the debug HUD shows them. H2 wrote
the ground domain of §5.1 and its coordinator (`GroundDomain.cs`, `GroundCoordinator.cs`): under
`--bot-ai htn` every ground bot plans with it, on the team's `ContactMemory`, a `Neighbourhood`
survey on its own scan and a `ZoneBoard` of the nodes. H3 wrote the unit domain of §5.2 and a
squad census (`UnitDomain.cs`, `SquadCensus.cs`): under `--bot-ai htn` every unit plans with it,
whoever ordered it — keeping pace with its squad, focusing its squad's target, standing on a post
round a defended anchor. H4 wrote the strategist's commander of §5.3 (`CommanderDomain.cs`,
`Commander.cs`): under `--bot-ai htn` a computer strategist forms squads, sends one to a threatened
zone, stages and strikes at a node or a contact, scouts, pulls a losing squad back and refills a
depleted one, and leaves a person's units alone. H5 decided D2 (§8) and built it: units heal inside
a barracks' ring and round a new **supply truck** that any strategist can buy (`Resupply.cs`,
`Units/supply_truck.tres`); under `--bot-ai htn` a bot's wounded unit walks to the nearest supply
source, a depleted squad heals there rather than being merged, and the commander buys a truck and
stations it behind its squads. H6 wrote four scenarios that pass under `htn` and fail under
`legacy` (`Tests/Scenarios/htn_*.json`), on three new agent events and event filters in the
playtest vocabulary; compared bot-only rounds under both; flipped the default to `htn`; pinned
`legacy` for training and evaluation, with every trainer output naming the bots it played (D5);
and fixed a spawn bug that had been pushing up to four ground players out of the world at every
round reset. H0 found one FluidHTN defect the probes had not
reached, a queue dropped when a paused partial plan is replaced (§3.4); the vendored copy carries
the fix from H4 (D6). `tools/Gdpyr.HtnBench` — §5.1's domain written against
FluidHTN, the behaviour probes of §3.4 and the cost measurement of §3.5 — and
`scripts/htn-bench.sh`, which runs them, run against the vendored copy.

Scope: replace the goal selection of the three computer-controlled deciders — the ground bot
(`BotPilot`), the RTS unit (`UnitManager.SimulateUnit` over `UnitBrain`) and the computer strategist
(`BotStrategist`) — with hierarchical task network planners, built around five tasks: **Defend
Zone, Recon, Attack, Retreat, Resupply**. RTS units plan with their standing order, the side's
known enemy positions and the friendly units around them as inputs.

What does not change: the micro. `BotBrain.Frame` still turns intent into an `InputFrame`,
`UnitBrain` still decides Idle/Moving/Engaging and where a given order walks, `Unit.Steer` and
`ServerFire` still walk and shoot. An HTN decides *what to do*; the existing code keeps deciding
*how to do it this tick*.

---

## 1. What the bots do today

Read from the code on this branch (`04e2012`).

| Decider | Goal selection today | What it does not consider |
|---|---|---|
| Ground bot — `Scripts/Bots/BotPilot.cs` | Nearest hostile unit or armed structure its own `GroundSensor` scan can see (`AcquireTarget`, l. 243); otherwise the nearest enemy barracks, at a per-bot loiter point or at the edge of its defended ring (`Objective` / `TryFindObjective`, l. 370–445). | Resource nodes — it never walks to one, so bots never deny income, which M5 made the ground force's reason to move. Anything a teammate has seen. Where teammates are, except to avoid shooting them. Its own health. The weapon locker — a bot's large weapon is fixed by roster slot (`BotDirector`, l. 433), so four in six can never hurt a tank. |
| RTS unit — `UnitManager.SimulateUnit` (l. 353), `Scripts/Sim/UnitBrain.cs` | A pure FSM: nearest ground-force player inside its own sensor with a clear ray (`UnitManager.AcquireTarget`, l. 460), or the order's named target; destination from the order (`UnitBrain.Destination`, l. 130). | Contacts the side already has through `VisibilityService`. Friendly units nearby: no focus fire, no waiting for the group, no answering a friend in a fight. Its own health. A defending garrison stands on one point. |
| Strategist — `Scripts/Bots/BotStrategist.cs` | Every 30 ticks: queue the best affordable tier; the first four units by registry order `Defend` the first barracks' rally point; every other unit attack-moves to the nearest *currently visible* player, or to the ground spawns in turn every 25 s (`TryObjective`, l. 513); one builder fortifies nodes. | Squads: the whole assault is one blob given one point. Defending the nodes that are its income (only the barracks is garrisoned). Scouting — the technical's 70 m sensor is never used on purpose. Retreat: units fight until dead. Last-known positions: ghosts are ignored by design (`TryObjective` doc). A human strategist sharing the army: units are owned by the *team* (`ApplyOrder`, l. 798–817), so the bot re-orders a human's units whenever their order differs from its plan. |

Two properties the current code has and the plan keeps:

- **Decisions are engine-free and tested.** `BotBrain`, `UnitBrain` and `StrategistBrain` live in
  `Scripts/Sim` and are pure; `Tests/*BrainTests.cs` cover them without Godot.
- **Bots have no privileges.** The strategist sees through the same `VisibilityService` a human's
  packet is filtered by and acts through the same `ServerQueueUnit` / `ServerIssueOrder` /
  `ServerConstruct` a human's RPC lands in; a ground bot sees with its own eyes.

---

## 2. Why an HTN

An HTN planner decomposes a root task through an ordered list of methods, each guarded by
conditions on a world state, into a queue of primitive tasks; primitive tasks carry effects so the
planner can look ahead within the decomposition (Humphreys, *Exploring HTN Planners through
Example*). What that buys over the alternatives, for this codebase:

| Approach | Fit here |
|---|---|
| FSM / hand-written selection (today) | Every new goal is a new branch in imperative code, and priorities between goals are implicit in statement order. It is what produced §1's gaps. |
| Behaviour tree | Reactive only: no effects, so no "go to the locker, *then* the tank is attackable" lookahead. Re-evaluates from the root every tick unless hand-cached. |
| GOAP | Plans by search over actions; cost grows with action count and designer control over *which* plan comes out is indirect (action costs). |
| Utility | Good at scoring "which node / which target", poor at sequencing. Used *inside* HTN conditions and operators here, not instead of them. |
| **HTN** | Author-ordered priorities are explicit (method order), plans are short and readable, a higher-priority method can pre-empt a running plan (§3.4, P2), and the same planner serves every layer — which is how Killzone 3's bots were built: commander, squad and individual layers on an HTN planner, orders down, information up, individuals still autonomous in combat (Straatman et al., *Hierarchical AI for Multiplayer Bots in Killzone 3*). |

The RL policies of M6–M7.5 are not replaced by this. They take a seat through the agent API and the
seat falls back to the bot when the policy goes quiet (`BotDirector.ServerTick`, l. 92;
`BotDirector.Sample`, l. 112); that branch is untouched. What changes is the *opponent* those
policies are trained and evaluated against — see D5 in §8.

---

## 3. FluidHTN

**Recommendation: FluidHTN v0.4.1 (commit `e67af26`), vendored as source.** It is MIT, pure C# with
no dependencies beyond the BCL, maintained since 2019, and small enough (3,340 lines) to own
outright. Everything this section claims about it was checked against its source, a compiled
sketch of §5.1's domain, nine behaviour probes and a cost measurement, all in
`tools/Gdpyr.HtnBench` and re-runnable with `./scripts/htn-bench.sh [--probe | --default-factory]`.
The other .NET options are in §3.9; none is close.

### 3.1 Provenance and maintenance

| | |
|---|---|
| Repository | <https://github.com/ptrefall/fluid-hierarchical-task-network>, MIT, © 2019 Pål Trefall |
| Origin | Written by Pål Trefall out of his work on the tactical combat AI of *Rust* (GameDev.net announcement); a total-order forward-decomposition planner after Humphreys' Game AI Pro chapter; builder API modelled on Fluid Behaviour Tree (README) |
| Size and dependencies | 32 source files, 3,340 lines (without `Properties/AssemblyInfo.cs`); `System` and `System.Collections.Generic` are the only namespaces it imports; no `UnityEngine` reference anywhere in the library (checked) — the Unity support is package metadata |
| History | 264 commits: 209 in 2019, then 1 (2020), 10 (2021), 20 (2022), 17 (2024), 4 (2025), 3 (2026). 255 by the author, 9 by four contributors |
| Releases | Tags `v0.1` … `v0.4`, `v0.4.1`. `v0.4.1` is `ca9eee3`, 2026-02-16. `e67af26` (2026-03-18) is `v0.4.1` plus comments only: `git diff v0.4.1 e67af26 -- Fluid-HTN` is 14 inserted comment lines |
| API changes that matter | 2024-05: all planner state moved into an `IPlannerState` on the context, so `Planner<T>` is stateless — "easier to multi-thread" (commit `1981fcf`). 2025-11 (`v0.4`): `Aborted` renamed `Abort`, `Start` added to `IOperator` (`d1cc031`). Pinning a commit is what insulates the game from the next one |
| Issues | 8 ever filed, 2 open: #19, a request for a graphical view of a domain, and #20. Closed: #8 (2021) and #12 (2022), both **method traversal record priority bugs**; #21 (2026), an operator ticked twice in one planner tick — closed by renaming the flag that causes it to `allowImmediateReplanAndExecute` and adding a test that documents it (`ca9eee3`), so it is intended behaviour. MTR is the part with a bug history, so it is the part the probes exercise hardest (P2, P3, P6) |
| Tests | 159 upstream MSTest tests, each commented as documentation since 2025-11 |
| Distribution | NuGet `Fluid-HTN` has one version, `0.0.0`, published 2021-09-01 — four and a half years stale. Source it is |
| Ports | JavaScript, C++, Lua and a Godot 4 GDExtension, all third-party. Not needed here, but four independent re-implementations of one design is evidence the design is understood |

### 3.2 Its concepts, and what each is for here

| FluidHTN concept | What it does | Used here for |
|---|---|---|
| Context (`BaseContext`) with a `byte[] WorldState` indexed by an enum | The planner's blackboard. `SetState` marks the context dirty only when a value changes, and a dirty context replans on its next tick | One context per agent. Facts are discretised bands written by the existing sensor scans (§4.2, §5) |
| Domain builder, extensible by subclassing `BaseDomainBuilder<DB, T>` | Fluent domain definition; the README's own "Extending the Domain Builder" pattern | `GroundDomainBuilder` adds `If`, `IfNot`, `While`, `Act`, `Predict`, so a domain reads like §5's trees (§3.3) |
| `Select`, `Sequence` | Compound tasks: first valid sub-task; all sub-tasks in order | Priorities are selector order; methods with several steps are sequences |
| `Condition` | Guards decomposition | Fact tests |
| `ExecutingCondition` | Re-checked before every operator update; failing it aborts the task and replans | Ends a long-running task the tick its premise goes (P3) |
| Operator: `Start` / `Update` / `Stop` / `Abort`, returning `Continue` / `Success` / `Failure` | The per-tick behaviour of a primitive task | Writes an intent (goal, target, stance) for the engine half to carry out; `Stop` clears it |
| Effects: `PlanOnly`, `PlanAndExecute`, `Permanent` | World-state changes during planning, optionally re-applied on success | `PlanOnly` predictions that a later sensor scan must confirm — "the locker will give me a launcher" (P4) |
| `PausePlan` | Plans up to the pause, continues from it once that part is done | A squad stages, then strikes against the world as it is when it has assembled (P5) |
| `Splice` / `Slot` | Reuse a sub-domain at build time / swap one at run time | `Splice` for sub-trees shared between domains (Retreat for every unit type). `Slot` is per domain, not per agent, so it is not used (§3.7) |
| Method traversal record (MTR) | The branch indices a plan was decomposed through. A dirty replan may replace the running plan only with one that decomposes through a strictly earlier branch | Pre-emption: Retreat interrupts Attack (P2). The corollary: nothing lower-priority interrupts anything (P3) |
| `allowImmediateReplanAndExecute` (default on) | After a task succeeds or fails, replan and start the next task in the same tick | No idle tick between tasks. An operator can therefore run twice in one tick (#21), so operators must be idempotent; writing an intent is |
| `IPlannerState` callbacks (`OnNewPlan`, `OnReplacePlan`, `OnNewTask`, `OnCurrentTaskFailed`, `OnCurrentTaskExecutingConditionFailed`, …) | Hooks into every planner event | The debug HUD's plan row, `bot_plan <n>`, and H6's `AgentEventBus` events |
| Decomposition log, `DebugMTR` | Human-readable traces of decomposition | Development builds only: both build strings |
| `IFactory` (`CreateArray` / `FreeArray`, `CreateQueue` / `FreeQueue`, …) | Every collection the planner borrows goes through it and is handed back — all but one upstream, which the vendored copy patches (D6) | A pooled factory; with it the planner allocates nothing (P8, §3.5) |

### 3.3 What a domain looks like in it

`tools/Gdpyr.HtnBench/GroundSketch.cs` is §5.1 written against FluidHTN and compiled. The builder
extension is a few lines per verb; every lambda is created once, when the domain is built:

```csharp
public sealed class GroundDomainBuilder : BaseDomainBuilder<GroundDomainBuilder, GroundContext>
{
	public GroundDomainBuilder If<TValue>(Fact fact, TValue value) where TValue : struct, Enum
	{
		byte wanted = Convert.ToByte(value);
		return Condition($"{fact}={value}", c => c.Get(fact) == wanted);
	}

	public GroundDomainBuilder While<TValue>(Fact fact, TValue value) where TValue : struct, Enum
	{
		byte wanted = Convert.ToByte(value);
		return ExecutingCondition($"while {fact}={value}", c => c.Get(fact) == wanted);
	}

	public GroundDomainBuilder Act(GroundGoal goal)
	{
		Action(goal.ToString());
		return Do(c => c.Perform(goal), forceStopAction: c => c.Intent = GroundGoal.None);
	}

	public GroundDomainBuilder Predict<TValue>(Fact fact, TValue value) where TValue : struct, Enum
	{
		byte predicted = Convert.ToByte(value);
		return Effect($"{fact}:={value}", EffectType.PlanOnly,
			(c, type) => c.SetState((int)fact, predicted, false, type));
	}
	// IfNot, and bool overloads of If / While / Predict, alike. The sketch's While also has a
	// switch that leaves it out, which is how probe P3 builds the domain without one.
}
```

and the domain then reads as the plan does (excerpt; §5.1 has the whole tree):

```csharp
.Select("resupply")
	.Sequence("rearm for armour")
		.If(Fact.Role, GroundRole.LockerRunner)
		.If(Fact.ThreatKind, Threat.Armour)
		.If(Fact.Armed, Arms.SmallArms)
		.IfNot(Fact.Contact, ContactLevel.None)
		.Act(GroundGoal.PathToLocker).End()
		.Act(GroundGoal.UseLocker).Predict(Fact.Armed, Arms.Explosive).End()
		// Only plannable through the prediction above.
		.Act(GroundGoal.EngageArmour).If(Fact.Armed, Arms.Explosive)
			.While(Fact.Armed, Arms.Explosive).End()
	.End()
	.Sequence("reload")
		.IfNot(Fact.Contact, ContactLevel.Visible)
		.If(Fact.MagazineLow)
		.Act(GroundGoal.Reload).While(Fact.MagazineLow).End()
	.End()
.End()
.Select("attack")
	.IfNot(Fact.Contact, ContactLevel.None)
	.Sequence("press with allies")
		.If(Fact.ThreatKind, Threat.Infantry)
		.If(Fact.AlliesNear)
		.IfNot(Fact.Odds, OddsBand.Outnumbered)
		.Act(GroundGoal.TakeFocusTarget).End()
		.Act(GroundGoal.AdvanceWithBuddy)
			.While(Fact.AlliesNear).While(Fact.Contact, ContactLevel.Visible).End()
	.End()
	// …
```

In the game, `Perform` writes a `GroundIntent` for `BotPilot`; in the sketch it records the goal
and asks a scripted world whether it has finished.

### 3.4 Verified behaviour

`./scripts/htn-bench.sh --probe` runs each against the sketch domain and exits non-zero on any
failure; since H0, P1–P8 are also `Tests/HtnPlannerTests.cs`, so `dotnet test` fails on them too.
Result at `v0.4.1` / `e67af26`, all nine passing — re-run in H0 against the vendored copy and
`PooledHtnFactory`, with the same results but P9's domain figure (the factory's per-instance pools):

| Probe | What it establishes | Observed |
|---|---|---|
| P1 priority | Selector order is priority: with a visible infantry contact, an ally near and the denier role, Attack wins over Defend Zone and "press with allies" over "engage" | Plan `[TakeFocusTarget, AdvanceWithBuddy]` |
| P2 pre-emption | A higher-priority method replaces a running plan the tick its facts appear | `AdvanceWithBuddy` running; health critical and outnumbered sensed → `[FallBack]` on the next tick, one `OnReplacePlan` |
| P3 no downgrade | MTR never lets a lower-priority method replace a running one. A stance task with no executing condition outlives its premise | Contact lost while engaging, denier role set: 30 ticks later, **without** executing conditions the bot is still on `Engage`; **with** them it is on `TakeNode` |
| P4 lookahead | A `PlanOnly` effect lets the planner chain "fetch a launcher" into "engage the tank"; the prediction is dropped before execution, so the scan must confirm it | Locker runner vs armour with small arms → `[PathToLocker, UseLocker, EngageArmour]`, ending on `EngageArmour` once the scan reports an explosive. A rifleman without the role → `[Standoff]`. Locker never delivers → it never engages unarmed; it **plans the same trip again** |
| P5 partial plan | `PausePlan` defers the rest of a sequence until the first part finishes | Squad sketch: `[stage]` while assembling, then `[strike]` |
| P6 shared domain | One `Domain` and one `Planner` for every agent gives each agent the trace it gets with its own | 16 agents × 3,000 ticks of pseudo-random facts: per-agent trace hashes identical |
| P7 determinism | Same facts, same trace | Two runs identical |
| P8 zero allocation | With a pooled `IFactory` the planner allocates nothing on the tick | 64 agents, 7,200 ticks, 253,425 dirtying fact changes: **0 bytes**. Found in H0, outside the sketch (it has no `PausePlan`): a plan that replaces a *paused* partial plan allocates, pooled or not — below |
| P9 memory | One-off costs | Building the §5.1 domain: 18,216 B, once (17,752 B with the bench's original static-pool factory). One agent's context: 856 B before its first plan (64 units ≈ 55 KB) |

What the probes change in the plan:

- **P3:** every long-running operator in every domain gets an executing condition for its premise,
  and each domain test includes "premise gone → plan ends" for each such operator.
- **P4:** a prediction that does not come true replans the same method, for ever. Every method whose
  success depends on the world confirming a prediction needs a give-up fact — here the coordinator
  drops the locker-runner role after a trip that produced no explosive.
- **P6:** sharing is safe *because* the server tick is single-threaded. Planning never moves to a
  worker thread without a domain per thread.
- **P8, partial plans (H0):** when a fact changes while a partial plan is paused,
  `Planner.PrepareDirtyWorldStateForReplan` sets the paused remainder aside in a
  `Queue<PartialPlanEntry>` borrowed from the factory, and `Planner.TryFindNewPlan` returns it only
  when no new plan is found. A plan that *replaces* the paused one drops the queue: each such
  pre-emption allocates one, pooled or not. Measured over 100 stage → retreat → strike rounds:
  100 queues borrowed, 0 returned, 128 B a pre-emption; the same rounds without the retreat, 0 B.
  Only domains with `PausePlan` reach it — here, the commander's stage → strike (§5.3, H4) — and
  only at the rate squads are pulled out of staging, not per tick. Freeing the queue in
  `TryFindNewPlan`'s found-a-plan branch (one line) makes it 0 B with P1–P8 still passing (checked
  by applying it and reverting it). Two `HtnPlannerTests` facts pinned both halves until H4, which
  took D6's recommendation: the vendored copy carries that line, recorded in
  `ThirdParty/FluidHTN/README`, and the second fact now asserts the queue is returned and 0 B
  (removing the line fails it: checked, then restored).

### 3.5 Cost

`./scripts/htn-bench.sh`: the §5.1 domain shared by 64 agents (`SimConfig.MaxUnits`), each planner
ticked every server tick for 3,600 ticks (one minute at 60 Hz) after a 2,000-tick warm-up. Facts
walk through eight situations, one per branch of the domain, changing on a 10-tick stagger (the unit
target refresh) and, in the worst case, on every tick for every agent. Walks finish after three
ticks; stances run until a fact ends them. `TieredCompilation` is off so both rows run optimised
code. Three runs each, 4-core Intel Xeon @ 2.10 GHz, .NET SDK 8.0.131:

| Factory | Facts change | Planner time per server tick, 64 agents | Allocated per tick | Per 20-minute round |
|---|---|---|---|---|
| `DefaultFactory` | every 10 ticks | 57.6–66.6 µs (0.35–0.40% of a tick) | 2,918.4 B | ~210 MB |
| `DefaultFactory` | every tick | 98.9–114.9 µs (0.59–0.69%) | 3,583.9 B | ~258 MB |
| pooled | every 10 ticks | 53.3–67.0 µs (0.32–0.40%) | **0 B** | **0** |
| pooled | every tick | 92.8–125.8 µs (0.56–0.75%) | **0 B** | **0** |

Byte counts were identical on every run; times moved by up to a third between runs on this shared
VM. Re-run in H0 against the vendored copy and `PooledHtnFactory`, same machine type and SDK, three
runs each: pooled 42.6–53.9 µs (every 10 ticks) and 68.6–75.0 µs (every tick), **0 B** on every row;
`DefaultFactory` 2,918.4 B and 3,583.9 B per tick, identical to the table. Conclusions: time is not
the constraint — the real cost will be the conditions and sensors the game supplies, which is why
facts are encoded on the existing scans (§4.2) — and **the default factory is not acceptable** on a
path that must not allocate, while a pooled one costs nothing.

It builds on net8.0 with 0 warnings and 0 errors, unchanged, with `Nullable` off as this repo's
projects have it — once `Properties/AssemblyInfo.cs` is left out. Left in, the game assembly fails
with `CS0579: Duplicate 'AssemblyVersionAttribute'`; observed when a checkout sat inside the
project directory, which the Godot SDK's default glob compiles whether or not git ignores it.

### 3.6 The extension library

[fluid-hierarchical-task-network-ext](https://github.com/ptrefall/fluid-hierarchical-task-network-ext)
(MIT, same author) adds `RandomSelector`, `UtilitySelector` (sub-tasks implement `IUtilityTask.Score`),
`AlwaysSucceedSelector`, `InvertStatusSelector`, `RepeatSequence` and a `GOAPSequence`. Last updated
2024-05-24 "to Fluid HTN v0.3 compatibility"; it still compiles against `v0.4.1` with 0 errors
(checked).

**Not vendored.** `UtilitySelector` does no MTR tracking — its own comment says so — so a utility
choice cannot take part in pre-emption, which is the property the Retreat design rests on.
`RandomSelector` draws from an unseeded `System.Random`, which `Scripts/Sim` forbids. The one idea
worth having from it, utility scoring, goes *inside* a condition instead: the condition scores the
candidates (which node to defend, which contact to strike), binds the winner to the context, and
passes; the selector around it stays an ordinary, MTR-tracked `Select` (§5.3).

### 3.7 Integration constraints

| FluidHTN property | Consequence here |
|---|---|
| World state is a `byte[]` indexed by an enum; `SetState` marks the context dirty only when a value *changes*. | Facts are **discretised** (health bands, force-ratio bands, contact kinds). Positions, target ids and distances live on the context as ordinary fields that conditions read. Only a change worth replanning for goes in the world state — that is the anti-thrash lever. |
| A dirty replan replaces the running plan only if the new decomposition is strictly higher priority by MTR (`Selector.BeatsLastMTR`). | Retreat pre-empts Attack mid-plan (P2). Nothing lower-priority ever interrupts: **every long-running operator gets an executing condition** that fails when its premise goes, or the bot finishes an obsolete plan (P3). |
| `Selector` and `Sequence` hold a per-instance `Plan` queue. | One domain instance per agent kind, shared, which is safe only because the server tick is single-threaded (P6). |
| Slots (`Slot` / `TrySetSlotDomain`) belong to the domain, not the agent. | Per-unit-type behaviour (builder, tank, technical) is a separate domain built once, with shared parts spliced in; never a slot swapped per agent at run time. |
| No random selector in the core; planning reads only the world state and the MTR. | Deterministic (P7). Any variety (which loiter point, which flank) comes from `Spread.Seed`, as `BotBrain` does today. |
| `DefaultFactory` allocates on every replan. | A pooled `IFactory` is mandatory, and an xUnit test holds it at zero (H0). |
| A replan that replaces a paused partial plan drops the queue it set the remainder aside in (§3.4, P8). | 128 B per such pre-emption even pooled, in `PausePlan` domains only. Patched in the vendored copy from H4 (D6); a test fails if a bump loses the patch. |
| The builder's `Do` takes `start` and `forceStop` but not the operator's `abort` callback. | A task that must clean up when aborted gets a builder verb that calls `SetOperator(new FuncOperator<T>(…, funcAborted: …))` directly. |
| `Build()` throws on a malformed domain; `FindPlan` throws on an uninitialised context. | Both surface at start-up, not mid-round; a test builds every domain. |
| Task names are strings; the decomposition log and `DebugMTR` build more. | Names are made once at build time. Logging and `DebugMTR` stay off outside development builds. |

### 3.8 How it is taken in

Done in H0.

- **`Fluid-HTN/` at `e67af26` is vendored into `ThirdParty/FluidHTN/`**: the library folder's 32
  `.cs` files (3,340 lines), byte-identical to upstream but for D6's one line (from H4), without
  `Properties/AssemblyInfo.cs`, the old-style `.csproj` or the Unity package metadata
  (`Fluid.HTN.asmdef`, its `.meta`, `package.json`), with upstream's `LICENSE` beside them and the
  commit in a one-line `README`. Not upstream's `Fluid-HTN.UnitTests/`: it is MSTest, and the glob
  in §3.5 would compile it into the game. `ThirdParty/.gdignore` keeps the editor from writing
  `.uid` files into it.
- `Gdpyr.csproj` picks the library up through the SDK's default glob, unchanged (`dotnet build
  Gdpyr.csproj`: 0 warnings, 0 errors); `Tests/Gdpyr.Tests.csproj` has one
  `<Compile Include="../ThirdParty/FluidHTN/**/*.cs" />`, plus the bench's `GroundSketch.cs` and
  `Population.cs` for the probes.
- The probes are in xUnit (`HtnPlannerTests`), on the sketch domain until H2–H4 write the real ones.
  Bumping the pinned commit is: replace the folder, run `dotnet test` and `./scripts/htn-bench.sh`;
  a change in MTR behaviour fails a test rather than a playtest (checked: disabling
  `Selector.BeatsLastMTR`'s rejection, then reverting it, fails P3).
- No upstream changes were needed for H0–H3. One defect matters from H4 (D6); upstream's head was
  still `e67af26` when H4 was built, so the vendored copy carries the fix, and a bump either brings
  it or re-applies it. If upstream stops, the code is MIT and ours to maintain.

### 3.9 Alternatives considered

| Library | Why not |
|---|---|
| FluidHTN from NuGet (`Fluid-HTN` 0.0.0, 2021) | Four and a half years behind the source: it predates the #12 MTR fix, the `IPlannerState` split and the `v0.4` operator API |
| [CHP — C# HTN-Planner](https://sourceforge.net/projects/chpplanner/) | GPL-3.0, which cannot go into this MIT repository without relicensing it; dormant since 2013 |
| [Tencent/behaviac](https://github.com/Tencent/behaviac) | BSD-3-Clause BT/FSM/HTN framework whose workflow is a Windows-only designer; last updated 2023 |
| [SandboxAI](https://github.com/OneManMonkeySquad/SandboxAI) | Depends on `UnityEngine`; archived 2025-05-04 |
| [UnityHTN](https://github.com/konbraphat51/UnityHTN) | Unity-bound, 3 stars |
| [godot-fluid-hierarchical-task-network](https://github.com/fnaith/godot-fluid-hierarchical-task-network) | FluidHTN ported to a C++ GDExtension: every condition and operator would cross the C#/C++ boundary, and `Scripts/Sim` may not depend on the engine |
| Writing our own | Re-deriving MTR pre-emption, partial plans and their tests, in the area where FluidHTN's own bug history is; only worth it if FluidHTN had failed §3.4 or §3.5. It did not |

Reference-only, not libraries to embed: SHOP / JSHOP2 (Lisp / Java, academic), Pyhop / GTPyhop
(Python), PANDA and HDDL (academic hierarchical planning systems and their domain language).

---

## 4. Architecture

### 4.1 Layers

```
 STRATEGIST SIDE                                   GROUND SIDE
 ───────────────                                   ───────────
 Commander HTN  (BotStrategist, every 30 ticks)    Ground coordinator (every 30 ticks)
   │ missions: Defend Zone / Recon / Attack /        │ roles: node deniers, locker runner,
   │           Retreat / Resupply per squad          │        focus targets, buddy pairs
   │ + production and fortification                  │
   ▼  ServerIssueOrder / ServerQueueUnit / …         ▼  GroundRole on each pilot
 SquadBoard  (server-only: squad → units,          Team contact memory (union of the
   mission, phase, focus target, staging point)      ground seats' own GroundSensor scans)
   │                                                 │
   ▼                                                 ▼
 Unit HTN  (per unit, on its 10-tick scan)         Ground bot HTN  (per bot, on its 10-tick scan)
   │ standing order is the top-level task            │
   ▼  UnitIntent                                     ▼  GroundIntent
 UnitBrain + Unit.Steer + ServerFire  (unchanged)  BotBrain.Frame → InputFrame  (unchanged)
```

Orders go down as the orders that already exist; information goes up only through the shared
boards. Nothing new goes on the wire (§4.4).

### 4.2 Rules

1. **Engine-free planning.** Domains, contexts, fact encoders, boards and intents live in
   `Scripts/Sim/Htn/` and are tested by `dotnet test`. Operators never call the engine: a primitive
   task writes an **intent** (a struct: goal point, target preference, stance, buttons) onto its
   context, and the engine half — `BotPilot`, `UnitManager`, `BotStrategist` — carries it out.
2. **No new privileges.** The strategist and its units know what `VisibilityService` knows
   (live contacts and ghosts, `TryContact`). A ground bot knows what its team's seats have seen
   (D3). Every action goes through the requests a human's RPC lands in.
3. **Zero allocation on the tick.** Pooled `IFactory`; fixed-size boards pre-sized from `SimConfig`;
   an xUnit guard that plans over every domain and asserts `GetAllocatedBytesForCurrentThread`
   does not move (H0).
4. **Cadence, not every tick.** Facts are encoded on the scans that already exist and are already
   staggered: a unit's `NextScanTick` (10 ticks), a ground bot's `_nextScanTick` (10 ticks), the
   strategist's decision interval (30 ticks). The planner's `Tick` still runs every tick so
   operators progress, but it replans only when an encoded fact changed or the plan ended.
5. **Deterministic.** No RNG; ties broken by id or `Spread.Seed`.
6. **Switchable.** `--bot-ai legacy|htn` (`LaunchOptions`) and a `BotAi` field on
   `GameModeDefinition`. `legacy` is today's code path, kept intact, and was the default until H6,
   which made it `htn` (`BotAiNames.Default`); the training and evaluation scripts pin `legacy`
   (D5).

### 4.3 Shared knowledge

| Board | Side | Contents | Fed from | Cost |
|---|---|---|---|---|
| `ContactMemory` | one per side | Fixed array of contacts: owner id, kind (infantry / armour / armed structure / player), last-known position and velocity, last-seen tick, seen-by count. Ages out on `SimConfig.GhostLifetimeTicks`, as the strategist's ghosts do. | Strategist: `VisibilityService` after each refresh. Ground: each ground bot's `GroundSensor.Scan`. | Updated on existing scans; no new rays. |
| `Neighbourhood` | per unit / per bot | Friendlies within 30 m: count, summed health, how many are engaging, nearest friend, centroid; hostiles from `ContactMemory` within 40 m. Gives the **local force ratio** band. | Positions already in `UnitManager` / `CombatManager`. | 64 units: ≤ 4,096 distance checks per full refresh, spread over the 10-tick stagger. A uniform grid only if the debug HUD says so. |
| `ZoneBoard` | one per side | Per zone — each resource node, each barracks, each ground spawn — holder, contested, friendly strength, estimated enemy strength (cost-weighted, armour counted against explosives available), ticks since last observed. | `EconomyService` node state, `ContactMemory`, unit census. | 8 nodes + barracks + spawns; rebuilt per commander decision. |
| `SquadBoard` | strategist | Up to 8 squads: members, mission, target zone, staging point, phase (gathering / moving / engaged / falling back), focus target, strength at formation. | Written by the commander; read by unit HTNs. Also written by `ApplyOrder` for a human's order (below). | Fixed arrays, `SimConfig.MaxUnits` members total. |

**Human orders form squads too.** `ApplyOrder` already receives the units of one order as a batch.
Recording that batch as an ad-hoc squad (and the issuing peer on each unit, server-side) gives a
human's selection the same cohesion and focus fire as a bot's — and lets the commander HTN leave a
human's units alone (D1).

**As built in H1** (`Scripts/Sim/Htn/`, with `ForceRatio` holding the arithmetic the boards share):

| Board | Owner | Rules the table above leaves open |
|---|---|---|
| `ContactMemory` | Strategist: `VisibilityService.Contacts`. Ground: `BotDirector.GroundContacts`. | A contact is **live** while an observer has reported it within one scan interval (8 ticks for the fog refresh, 10 for a bot's scan), inclusive, so a contact every scan finds never flickers; a **ghost** after that; forgotten 8 s after it was last seen. Seen-by counts distinct observers within that interval: ground bots by roster slot; the fog is one observer, because it stops at the first sensor with a clear line. The strategist records live ground-force players only; the ground side, hostile units and armed structures, and forgets one the tick-10 prune finds dead — the unit snapshot is not fogged for the ground force, so that is parity. Full: a new contact takes the stalest ghost's entry, and is refused if every entry is live; both are counted and both are 0 when sized right. |
| `Neighbourhood` | The caller, per scan (H2, H3). | Bodies, not strengths, as §5.1 defines outnumbered, with the surveyor counted on its own side. Hostiles are live and ghost contacts within 40 m. Bands enter at 2 : 1 and are held down to 1.5 : 1 (§9). From H3 it also gives the nearest friend's position and the nearest friend in a fight within 40 m, a radius of its own (§5.2's "a friend within 40 m is engaging"). |
| `ZoneBoard` | The commander and the ground coordinator (H2, H4). | Strength is `ForceRatio.Strength`: cost × health fraction. Bullet-proof contacts are answered by the side's explosives in proportion; the unanswered share counts at `UnansweredArmourScale` times. Observation is range-only against a `VisionField`. `Stalest(kind)` is the least-recently-observed zone, never-seen first. |
| `SquadBoard` | `UnitManager.Squads`. | One order is one squad; the same issuer, kind and focus within 2 m of a squad's target joins it — which keeps a bot's garrison, reinforced a unit at a time, one squad. A stop, a build order or a death takes a unit out; the last one out closes the squad. Full: the least recently ordered squad is recycled. Membership lives only on the board, and the unit carries its order's issuer and tick. From H3 the unit census moves a squad between moving and engaged. From H4 a computer strategist's commander writes, onto the board squads its orders form, the mission (`SetMission`), the staging point and gathering (`Stage`) and falling back (`SetPhase`); its own squads, and their target zones, it keeps itself (§5.3, "as built"). |

### 4.4 Where it plugs in

| File | Change |
|---|---|
| `ThirdParty/FluidHTN/` | Vendored library (§3.8). |
| `Scripts/Sim/Htn/` (new) | `PooledHtnFactory`, fact enums, `GroundContext` / `UnitContext` / `CommanderContext`, the three domain builders, `GroundCoordinator`, `ContactMemory`, `Neighbourhood`, `ZoneBoard`, `SquadBoard`, `GroundIntent` / `UnitIntent`. |
| `Scripts/Bots/BotPilot.cs` | `Sample` asks the ground domain for a `GroundIntent` instead of calling `Objective`; `AcquireTarget` prefers the intent's target. `OutsideDefences`, `Reachable`, stuck recovery, the friendly-fire and blast checks stay. `BotBrain` gains a `Use` press (a locker tap). |
| `Scripts/Bots/BotDirector.cs` | Owns the ground coordinator and the team `ContactMemory`; builds pilots and commanders with the selected `BotAi`. Agent fallback branches unchanged. |
| `Scripts/Rts/UnitManager.cs` | In `SimulateUnit`, after acquisition and before `UnitBrain.Destination`, run the unit's planner and apply its `UnitIntent` (destination override, target preference, hold). `ApplyOrder` records the order batch as a squad and the issuer on the unit. |
| `Scripts/Rts/Unit.cs` | Server-only fields: order issuer and order tick (H1), HTN context. Squad membership is on `SquadBoard`, not the unit. Nothing replicated. |
| `Scripts/Bots/BotStrategist.cs` | `ServerTick` becomes the commander domain's tick; `Build` / `Fortify` / `StrategistBrain` survive as its primitive tasks. |
| `Scripts/Core/LaunchOptions.cs`, `Scripts/Match/GameModeDefinition.cs` | `--bot-ai`, `BotAi`. |
| `Scripts/Sim/Resupply.cs`, `Units/*.tres`, `Units/supply_truck.tres` (H5) | The resupply rule (D2), `RegenPerSecond` and `SupplyRadiusMeters` on `UnitDefinition`, and the supply truck, catalog id 4; `UnitManager` heals once a tick and hands each unit's scan the nearest source ([`NETCODE.md`](NETCODE.md) §10.7). |
| `Scripts/Ui/Debug.cs`, `Scripts/Core/ConsoleCommands.cs` | A plan row on the debug HUD; `bot_plan <n>` prints a bot's current task chain from FluidHTN's `OnNewTask` / MTR debug. **Built after H6 as the AI debugger** ([`AI_DEBUG.md`](AI_DEBUG.md)): a spectator's panel, graph and world overlay, and `ai_plan <target>` on the console, from the planner state and `IPlannerState` callbacks. |
| `Scripts/Sim/Agent/AgentEvents.cs`, `Scripts/Bots/BotStrategist.cs`, `Scripts/Match/VisibilityService.cs`, `Scripts/Match/EconomyService.cs` (H6) | Three agent events: `squad_task` when a commander squad's goal changes, `contact_spotted` when the strategist's fog picks a player up, `node_contested` when a node's contest starts and ends ([`AGENT_API.md`](AGENT_API.md) §8). `welcome` says which bot AI the server runs. |
| `tools/Gdpyr.Playtest`, `tools/Gdpyr.Trainer`, `tools/Gdpyr.AgentClient`, `scripts/*.sh`, `Tests/Scenarios/htn_*.json` (H6) | A scenario's `bot_ai` and event filters in the metric vocabulary ([`AGENT_API.md`](AGENT_API.md) §9.1); the trainer's `--bot-ai` pin and `bot_ai` in its output; the scripts' `--bot-ai legacy`; the four H6 scenarios. |

No message, codec, snapshot field or agent observation changes. A demo records frames and "no bot
thinks" during playback (DEMOS.md), so demos are unaffected. H5 appends one line to the unit
catalog; health was already in the unit snapshot. H6 adds three kinds to the agent event stream and
a field to `welcome`, both additive: the observation schema is still `gdpyr-agent-obs-2`.

---

## 5. Domains

Selectors are in priority order: first valid method wins, and a higher one can pre-empt a running
plan (§3.4, P2) while a lower one never can (P3). `[…]` is a condition. Operators marked *existing*
wrap code that exists today.

### 5.1 Ground bot

Facts (one byte each, encoded on the pilot's scan):

| Fact | Values |
|---|---|
| `Contact` | none · ghost (team saw one ≤ 8 s ago) · visible to me |
| `ThreatKind` | infantry · armour (bullet-proof unit or armed structure) |
| `Armed` | small arms only · explosive in hand |
| `Health` | ok (≥ 60%) · hurt · critical (< 30%) |
| `Odds` | favourable · even · outnumbered (local hostiles ≥ 2 × friends within 30 m) |
| `AlliesNear` | a teammate within 15 m |
| `Role` | assault · denier · locker runner (from the coordinator) |
| `AtZone` | inside the assigned node's capture radius |
| `InsideDefences` | inside an enemy barracks' ring (today's `OutsideDefences` test) |
| `MagazineLow` | today's `needsReload` |

`tools/Gdpyr.HtnBench/GroundSketch.cs` is this tree in FluidHTN code, compiled and probed (§3.3,
§3.4); the sweep branch of Recon is left out of the sketch. `{…}` is an executing condition, `→ X`
a `PlanOnly` prediction.

```
ground
├─ Retreat
│   ├─ leave defences         [InsideDefences]                             LeaveDefences {InsideDefences}
│   │                                                                        existing OutsideDefences as the operator
│   └─ fall back to allies    [Health=critical ∧ Odds=outnumbered]         FallBack {Odds=outnumbered}
│                                                                            nearest teammate cluster, else the spawn
├─ Resupply
│   ├─ rearm for armour       [Role=locker runner ∧ ThreatKind=armour ∧ Armed=small arms ∧ Contact≠none]
│   │                           PathToLocker → UseLocker (→ Armed=explosive) → EngageArmour [Armed=explosive] {Armed=explosive}
│   │                           UseLocker taps Use until an explosive is in hand (≤ 2 taps: rifle → launcher → DMR → rifle)
│   └─ reload                 [Contact≠visible ∧ MagazineLow]              Reload {MagazineLow}; existing BotBrain reload
├─ Attack                     [Contact≠none]
│   ├─ engage armour          [ThreatKind=armour]                          EngageArmour [Armed=explosive] {Armed=explosive}
│   │                                                                        existing blast-safe hold-fire applies
│   ├─ press with allies      [ThreatKind=infantry ∧ AlliesNear ∧ Odds≠outnumbered]
│   │                           TakeFocusTarget → AdvanceWithBuddy {AlliesNear, Contact=visible}
│   │                           coordinator's focus target; preferred range with the buddy within 15 m
│   ├─ engage                 [ThreatKind=infantry ∧ Contact=visible]      Engage {Contact=visible}
│   │                                                                        existing BotBrain range-hold and strafe
│   └─ investigate ghost      [Contact=ghost]                              InvestigateGhost {Contact=ghost}
│                                                                            last-known position; the ghost ages out at 8 s
├─ Defend Zone                [Role=denier]
│   ├─ hold node              [AtZone]                                     HoldNode {AtZone}
│   │                                                                        in the capture radius: one body stops the node paying (M5)
│   └─ take node                                                           TakeNode (→ AtZone) → HoldNode [AtZone] {AtZone}
└─ Recon
    ├─ sweep stalest zone     coordinator's least-recently-observed node
    └─ standoff                                                            Standoff: existing Objective(), the enemy barracks' ring
```

**Ground coordinator** (team level, every 30 ticks, a plain function rather than a planner — it
assigns, it does not sequence): one denier per strategist-held node nearest the ground spawn, up to
a third of the team; at most one locker runner while armour is in `ContactMemory` and fewer than two
bots hold an explosive, and the role is taken away after a trip that produced no explosive (the
give-up fact P4 showed the domain needs); a focus target per contact cluster (lowest health first,
then nearest); buddy pairs by proximity. Humans are counted for strength but never given roles.

**As built in H2** (`Scripts/Sim/Htn/GroundDomain.cs`, `GroundCoordinator.cs`). The tree above is
the domain, with these differences, each found by a test or by the probe round in §6:

| Where | Built | Why |
|---|---|---|
| Facts | An eleventh fact, `Sweep`: the coordinator has given this bot a stale node. `Contact=ghost` means the team remembers a contact within 100 m that this bot's own scan did not find. `ThreatKind=armour` means nothing in the contact is soft: a rifleman facing a tank and the infantry beside it has a fight. | Recon's sweep needs a fact to pre-empt Standoff with. A tank must not stop a rifleman shooting the riflemen next to it. |
| Bands | Health: ok ≥ 60 %, critical < 30 %, each held 5 points past its edge once entered. `AlliesNear`: entered at 15 m, held to 18 m. `Odds` is `Neighbourhood`'s band. `Armed` is written every tick, the rest on the scan. | §9, plan thrash. A locker swap lands between scans and the next task is planned on what it produced. |
| Resupply | `PathToLocker` and `UseLocker` hold only while the bot is still the runner; `UseLocker` taps use every quarter-second while the weapon in hand is not a launcher (a rifle needs one swap, a DMR two) and gives up after 2 s; `PathToLocker` after 30 s. A give-up is counted on the context, and the coordinator takes the role away for 30 s. `Reload` also ends when something comes into view. | P3 and P4. Resupply is above Attack, so only an executing condition ends a reload that a contact interrupts. |
| Attack | "press with allies" needs `Contact=visible` as well — with a ghost it would plan `AdvanceWithBuddy` and abort it on the same tick, every tick. "investigate ghost" is infantry only — a rifleman has nothing to do at a tank's last-known position, and a launcher has "engage armour", which walks to the nearest known armour when none is in sight — and not for a denier, whose job is its node. `EngageArmour` holds while there is a contact and it is armour; `AdvanceWithBuddy` while not outnumbered. | P3: every long-running operator ends when its premise does. |
| Defend Zone | A denier stands 40 % of the capture radius off the node's centre on the side facing the ground spawn, swung per bot by up to 60°. | The first probe round sent a denier to a point on the barracks side of the middle node, inside the barracks' defended ring (it reaches ~117 m); `OutsideDefences` held it on the ring's edge for good. |
| Movement | Each intent says how to get there: `Target` (at the target, else the intent's point), `Point` (to the point, strafing a target inside preferred range as the brain does), `Run` (to the point, shooting on the move: `BotSituation.KeepMoving`), `Stay`, `Standoff` (today's `Objective()`). Leave defences, fall back, the walk to the locker and take node run. | A bot falling back that stopped to strafe would not be falling back. |
| Targets | The pilot still acquires on its own scan; it prefers the intent's target — the coordinator's focus — when the scan can see it and the weapon can hurt it. | The micro is unchanged (§4.2); the plan only biases it. |
| Coordinator | Deniers go to strategist-held nodes nearest the spawn, then to neutral ones, never to ground-held ones, and keep their node while it still wants one. The runner is the living small-arms bot nearest the locker; people's launchers count towards the two. One sweeper, to the node no ground player has been within 40 m of for 30 s. The focus is, per bot, the weakest live contact it could see and hurt (then the nearest, then the lowest id) — bots in one place share it, which is a cluster without clustering. Buddies are the closest pairs, people included. A seat an external policy drives counts as a person. | Sticky roles keep a denier from being swapped for whoever walks past. |

### 5.2 RTS unit

The standing order is the top-level task: the planner chooses *how* to carry it out and never
*whether*. Facts, encoded on the unit's 10-tick scan:

| Fact | Values |
|---|---|
| `Order` | mirror of `Unit.Order.Kind` |
| `Contact` | none · side-known (a `ContactMemory` entry within sensor range of the unit or its anchor) · own target |
| `Health` | ok · hurt · critical |
| `Odds` | favourable · even · outnumbered, from `Neighbourhood` |
| `FriendEngaged` | a squad-mate or a friend within 40 m is engaging |
| `SquadPhase` | gathering · moving · engaged · falling back, from `SquadBoard` |
| `InLeash` | self and target inside the defend leash (today's `UnitBrain.Destination` test) |
| `Stance` | obey (last order from a human) · autonomous (last order from a bot) |

```
unit
├─ Retreat                    [Stance=autonomous ∧ Health=critical ∧ Odds=outnumbered ∧ Order∉{Move,Build}]
│                               walk to the nearest friendly cluster or the squad's staging point without stopping to fight
├─ Resupply                   [Stance=autonomous ∧ Health≠ok ∧ Contact=none ∧ D2 adopted]   return inside the barracks ring until healed
│                               (H5: or to a supply truck — whichever source's reach is nearest)
├─ Build                      [Order=Build]                                existing; builder steps to its nearest friend when shot at
├─ Attack                     [Order=Attack]
│   ├─ wait for squad         [SquadPhase=gathering]                       hold at the staging point
│   ├─ engage focus           [Contact=own]                                squad focus target if in range, else own; existing hold-while-engaging
│   ├─ support                [FriendEngaged]                              move towards the engaged friend's target
│   └─ advance                existing attack-move towards the order's point or last-known target position
├─ Defend Zone                [Order∈{Defend,None}]
│   ├─ engage in leash        [Contact=own ∧ InLeash]                      existing leash
│   ├─ answer a call          [FriendEngaged ∧ InLeash]                    converge on the engaged friend
│   └─ hold post              take post k of n on a ring round the anchor, facing the threat bearing from ZoneBoard
├─ Recon                      [Order=Patrol]                               existing patrol; target = squad focus; does not stop for a contact it can report
└─ Obey                       [Order=Move]                                 existing: go there, shoot on the way; target = squad focus
```

A `Move` order is never second-guessed: `UnitOrder` documents it as "go there … does not chase",
and a strategist flanking with it has already decided the risk.

**As built in H3** (`Scripts/Sim/Htn/UnitDomain.cs`, `SquadCensus.cs`; the engine half in
`UnitManager`). The tree above is the domain, with these differences, each found by a test or in
review:

| Where | Built | Why |
|---|---|---|
| Facts | Two more: `UnderFire` (hit within 2 s) and `FriendNear` (a friendly unit within 30 m), for Build's "steps to its nearest friend when shot at". `Order`, `Contact` and `InLeash` are written every tick, the rest on the unit's 10-tick scan. `InLeash` is today's test, now `UnitBrain.InLeash`, which `Destination` calls; defending and idle units only, on the unit's target, else on the fight a friend is in. `FriendEngaged` is true only with a point to go to. A unit in no squad reads `SquadPhase=moving`. | A target that dies ends "engage focus" on the tick it goes, as today's code stops engaging on that tick. `Gathering` is phase 0, so no squad must not read as 0. Without `FriendNear`, a builder under fire with nobody near would "take cover" where it stands — the survey's nearest friend is then itself — instead of building. |
| Order | Every operator holds only while its order does, and a new order — `ApplyOrder`, or a builder's assignment — resets the plan. The intent says how to move: `Order` (today's `UnitBrain.Destination`), `Target`, `Point` or `Hold`, and whether to stand to fight; an operator that follows the order stands to fight exactly when `HoldsWhileEngaging` says. A patrol turns round only at an end its order sent it to. | Four of the five top-level branches are below Attack, so an attack plan would outlive the defend order that replaced it (P3). A patrol that retreated would otherwise count the fall-back point as an end. |
| Retreat | For a computer strategist's order only (`Stance=autonomous`: the issuer is a bot the director drives, with no policy attached to its seat). Issuer 0 — a fresh unit walking to its rally point, a unit a scenario placed — obeys. It falls back to the squad's staging point once the commander sets one (H4), else the nearest friendly barracks' rally point. | D1. For a unit that is outnumbered, "the nearest friendly cluster" within reach is the fight it is losing; the barracks is where the defences are, and where D2 would heal it. |
| Resupply | Not built in H3. | D2 was undecided: with no healing a unit had nothing to resupply. Built in H5, below. |
| Build | "take cover" [UnderFire ∧ FriendNear] walks to the nearest friendly unit and does not work while it does; "work" is today's build. | A builder in reach of its site stands still to work, so the plan has to say it is not working for it to move. |
| Attack | "engage focus": at the target, standing to fight, the squad's focus preferred. "support": to the squad's focus where the side last saw it, else to where its squad-mates in the fight stand; with none of those, to the target of the nearest friend in a fight within 40 m, else to that friend. "advance": to the named target's last-known position when the side remembers it, else the order's point, **keeping pace** — a unit more than 8 m nearer the objective than its squad's centroid holds until it is within 3 m again, and one wait that lasts 8 s ends the pacing for that advance. "wait for squad": at the staging point, else where it stands. | Keeping pace is what makes one attack order arrive together (§6, H3): over 150 m a technical at 8 m/s arrives 14 s before riflemen at 4.5. A slow squad closes up again and again and is waited for each time; one that does not close in 8 s has a unit that is stuck, and is left. |
| Focus | `SquadCensus`, once a tick before any unit moves: a target a member holds — the target the order named while a member holds it, else the one already chosen while any member holds it, else the one most members hold, then the weakest the side remembers, then the lowest id. Acquisition takes it before the nearest when the unit's sensor has it with a clear line. | A focus worth having is one that is kept. One ray: it replaces the search's rays when the line is clear, and when it is not the search skips that player, so a scan casts at most one ray more than legacy's — against §9's "no new line-of-sight queries", and only while the focus is behind a wall. |
| Defend Zone | "hold post": post k of n on a ring round the anchor — k the unit's place by id among its squad's living units, 4 m apart, the radius at least 4 m and at most half the unit's leash — post 0 towards the nearest contact the side remembers within 80 m of the anchor, else towards the ground force's spawns, rounded to 45°. A squad of one stands on the anchor. "engage in leash" is today's destination. "answer a call" is for a defend order only. | A bearing that moved with every step a contact took would send the ring round every scan. The strategist's `ZoneBoard` is not fed until H4 and has no bearing. A unit told to stop holds where it is, as `OrderKind.None` says. |
| Recon, Obey | Today's destinations, the squad's focus preferred. | §5.2. |
| Phase | The census writes `Engaged` while any member is engaging and `Moving` when none is; `Gathering` and `FallingBack` it leaves as they are. | Information up (§4.1): H4's Resupply reads "not engaged". |

**As built in H5** (`Scripts/Sim/Htn/UnitDomain.cs`; the rule in `Scripts/Sim/Resupply.cs`, the
engine half in `UnitManager`). D2 was decided for healing at the barracks **and at a supply truck**,
a unit the strategist buys (§8); the branch sits second, below Retreat, as the tree has it:

| Where | Built | Why |
|---|---|---|
| Facts | Three more. `Wounded`: entered when `Health` leaves ok, held until the unit is back to 95 %. `Supply`: a friendly source that can heal it — a barracks' ring or a truck's reach, never its own truck, and only for a unit with a rate to heal at — is within 60 m of the edge of its reach, held to 70 m, and no resupply was given up on in the last 30 s. `Supplied`: inside that reach, entered 3 m inside the edge and held to the edge. All three on the unit's scan; "nearest" is by the edge, not the middle, so a barracks' 117 m ring is near long before its building is. | "Until healed" is a band of its own: ending at the ok band would send a unit back at 60 %. The margin means a unit that stops on entering is inside the reach the rule heals in. Beyond 60 m the unit stays with its squad; the commander's Refill (§5.3) is what brings a squad back from across the map, rather than its units one at a time. |
| Condition | `Stance=autonomous ∧ Wounded ∧ Supply ∧ Contact=none ∧ ¬Supplied ∧ Order∉{Move,Build}`. | D1, and §5.2's rule that a move is never second-guessed. A unit already inside a reach needs nothing: it heals where its order has it. |
| Operator | `Resupply` walks to the source's middle — which crosses the edge wherever it starts — and holds, standing to fight, once `Supplied`; a truck that drives off is followed on the next scan. It holds while `Wounded`, `Supply`, the stance and the order do, and ends on a target of its own, not on a contact the side merely remembers. | Nothing below it can replace it (P3), so these are what end it. Ending on a remembered contact would turn a unit round for every ghost on the walk back. |
| Give-up | A resupply that has not ended in 60 s fails, is counted, and turns `Supply` off for 30 s. | P4: success depends on the world — the unit reaching the reach, and not being hit in it — so it needs a give-up, or it would plan the same trip for ever. |
| Walking out | A wounded unit inside a reach that its order walks out of: `Supplied` goes, Resupply pre-empts the order, the unit steps back in and stays until it is healed. | The one pre-emption this branch makes; it happens once, at the edge. |

### 5.3 Strategist commander

Every 30 ticks, over `ZoneBoard`, `ContactMemory` (live contacts *and* ghosts — D4), the economy and
`SquadBoard`. Facts are per decision; which zone or squad a method binds to is chosen by scoring in
its condition, which binds the winner to the context (utility inside HTN, §3.6).

```
commander
├─ Retreat                    [a squad's strength < 40% of formation ∧ estimated enemy ≥ 1.5 × squad]
│                               Move order to the nearest held zone with friends; phase=falling back; merge on arrival
├─ Defend Zone                [a held node or barracks is contested, or ZoneBoard threat within 60 m]
│   ├─ reinforce              nearest idle or reserve squad with strength ≥ 1.2 × threat: Defend order at the zone
│   └─ buy time               no squad fits: rally fresh production to the zone; fortify it (existing Fortify)
├─ Attack                     [a target zone or contact cluster where squad strength ≥ 1.5 × estimate]
│   ├─ stage                  Move to a staging point outside the target's sensor; phase=gathering until ≥ 80% present
│   ├─ strike                 Attack order on the target, named contact as TargetOwnerId; phase=engaged
│   └─ escort armour          tanks go only with an infantry squad; they share its staging and strike
├─ Recon                      [no live contact for 20 s ∨ a zone unobserved for 60 s]
│                               one technical (70 m sensor, 8 m/s) — else one rifleman — on a Patrol through the
│                               stalest zones; Move, not Attack: a scout's job is to see
├─ Resupply                   [a squad is below 60% strength and not engaged]
│                               pull it to the rally point; route the next produced units into it; heal if D2 adopted
│                               (H5: heal at the nearest supply source when healing can restore it; station the trucks)
└─ Economy                    existing StrategistBrain.TryChooseTier / ShouldQueue / builder / Fortify as primitives,
                                with production directed at the squad the plan above is short of
```

Garrison stays as today's first `GarrisonUnits` defending the first barracks, now a standing
`Defend Zone` squad. Units a human ordered in the last 30 s are not re-ordered (D1).

**As built in H4** (`Scripts/Sim/Htn/CommanderDomain.cs`, `Commander.cs`; the engine half in
`BotStrategist`). The tree above is the commander, with these differences, each found by a test or
by the probe rounds in §6:

| Where | Built | Why |
|---|---|---|
| Shape | The tree is per squad. An assignment — a plain function, as the ground coordinator is — gives each squad a task (defend a zone, attack a target, scout) once a decision, and each squad plans over one shared domain: Retreat › Defend Zone › Attack (stage → `PausePlan` → strike) › Recon › Resupply › Hold. Retreat and Resupply are the squad's own facts, not tasks. Economy is not in the tree: `Build` and `Fortify` run every decision, as legacy's do. | One planner runs one plan, and a commander has several squads doing different things at once. Which zone or target a squad takes is scored in the assignment rather than in a condition (§3.6), so that two squads never take the same one. |
| Squads | The commander keeps its own, up to 8: the garrison (the first `GarrisonUnits` riflemen, legacy's 4), one scout, and assault squads that form from production and are given targets once they reach 4. Strength at formation is the most a squad has been, so units that refill it make up its losses rather than raising the bar. On the `SquadBoard`, the squads its orders form get the mission, the staging point and the phase. | One order is one board squad (§4.3), and stage → strike or a retreat is a new order, which would start a new board squad with a new strength at formation. |
| Retreat | Below 40 % of strength at formation, with the enemy remembered within 40 m of its centroid at least 1.5 times what is left. Latched until the centroid is within 10 m of the fall-back point, fixed when it starts: the nearest held node or barracks with friends at it (the squad itself not counted) that are not outmatched themselves, else home. A move order. On arrival the squad is merged: its units go back to the pool, which routes them to a squad refilling, else the one forming. Never the garrison. | A retreat that ended once contact was broken would turn round at the next decision (§9, plan thrash). A move rather than an attack-move, so it does not stop for every contact. |
| Defend Zone | Held nodes and barracks that are contested or have a remembered contact within 60 m, most threatened first. The garrison counts as the defence of its barracks. The nearest idle assault squad at least 1.2 times the threat; with none, the squad still forming goes, and `Fortify` puts that node first ("buy time"). A squad keeps its zone while the threat lasts; one already attacking is not recalled. | §5.3's "nearest idle or reserve squad". |
| Attack | For an idle, ready squad that is not depleted: the nearest live contact it is at least 1.5 times (the enemy within 25 m of it), named in the order; else the nearest node the ground force holds, then a neutral one; one squad per target. With nothing known it sweeps the ground spawns 25 s each, as legacy does. A squad too weak for everything known holds, and gets the next units produced. It stages 60 m short of the target on its own side, or where it stands if it is nearer: the attack order goes out at once and the board says gathering at the staging point, so its units' "wait for squad" (§5.2) holds them there. It strikes when 80 % are within 12 m of the point, or after 30 s. A new target stages again. | D4: ghosts count in estimates and never name a target. Ground bots see 45 m. A stage that waited for ever on a stuck unit would be P4's lesson again. |
| Escort armour | A tank never joins the garrison, and neither does a technical, whose sensor is the scout's. A tank joins the assault squad with infantry and the fewest tanks, and forms a squad of its own only when there is none. | "Tanks go only with an infantry squad." |
| Recon | Wanted after 20 s with no live contact, counted from the first decision, or while a node has gone 60 s unseen. The scout is a technical in no squad, else one in an idle squad out of a fight, else a rifleman in no squad, else one from the squad forming. It patrols to the stalest node, else to the stalest zone the side does not hold, and is disbanded when recon is no longer wanted. | |
| Resupply | Below 60 % of strength at formation and not in a fight; it ends above 70 %. A move to home's rally point, and the next units produced join it on the way; once home it is merged into the squad forming there, as a retreat is on arrival. No healing (D2). | In the first probe rounds a depleted squad sat at home for the rest of the round: with the ground force holding every node there was no income, and nothing came to refill it. |
| D1 | A unit is left alone for 30 s after a person or a policy's seat orders it, and for as long as another computer strategist is the last to have ordered it. | Two bots sharing an army otherwise re-order each other's units every decision, as legacy's do. |
| Orders | Once a decision, the members of a squad not already under its order get it in one batch through `ServerIssueOrder`. A member needs it when the order kind differs, the target is more than 12 m off, or a different contact is named (legacy's `NeedsOrder`). | The reason `NeedsOrder` gives. |
| Estimates | A ground player is worth two riflemen (100), from the side's `ContactMemory`, live and ghost. | |

**As built in H5** (`Scripts/Sim/Htn/Commander.cs`, `CommanderDomain.cs`; the engine half in
`BotStrategist`):

| Where | Built | Why |
|---|---|---|
| Buying | One supply truck (`StrategistTraits.SupplyTrucks`) once eight units are fighting (`UnitsBeforeSupply`), bought after `Fortify` has held back what a waiting builder needs (`StrategistBrain.ShouldQueueSupply`, the builder's rule with the truck's numbers). The commander only: legacy never buys one. | A truck heals an army; it is not one. Legacy is the RL baseline (D5). |
| Supply role | Trucks are a squad of their own, `CommandRole.Supply`: never the garrison, the scout, an escort or an assault; a truck with no slot is left alone. Its standing task, `CommandTask.Supply`, plans `Station`: a move to where the trucks should stand — with a depleted squad that healing will restore and that is out of its fight, whether it is coming to them to heal or still holding a zone; else the staging point of the nearest attack, and never nearer its target than the 60 m a staging point is; else 40 m behind the nearest squad defending a zone away from home; else in reserve. Never where the side remembers an enemy within 40 m: the next candidate, else reserve. Retreat applies to it as to any squad. | A move, not an attack-move: a truck's job is to be there. The staging point is where a strike's wounded units are nearest a source they can reach within §5.2's 60 m. The distance and enemy rules came out of the probe round (§6, H5): a squad already inside 72 m of its target stages where it stands, in the fight, and a truck sent there, or to a squad that had only just broken contact, was shot. |
| Refill | A depleted squad is **healable** when its units at full health would be worth the 70 % that ends Depleted. A healable one moves to the nearest supply source — home, or any truck, whoever's — and stays there until healing ends Depleted, which ends the task, so it goes back to work as the same squad. One that is not, or that has waited 60 s (`RefillHealTicks`), goes home and is merged as in H4. | A squad that lost half its units cannot heal its way back and still needs merging; one that lost none should not be broken up. The time limit is P4's give-up. |

### 5.4 The five tasks at each layer

| Task | Ground bot | RTS unit | Strategist commander |
|---|---|---|---|
| **Defend Zone** | Hold a strategist node to stop its income (denier role) | Spread to posts round the anchor, answer friends' fights inside the leash | Send a squad sized to the threat to a contested node or barracks |
| **Recon** | Sweep the least-recently-seen node; investigate ghosts | Patrol without stopping for contacts it can report | One technical through stale zones on a patrol |
| **Attack** | Focus the coordinator's target with a buddy; armour only with an explosive | Wait for the squad, then focus fire; support engaged friends | Stage out of sight, strike when assembled, escort armour |
| **Retreat** | Leave barracks defences; fall back to teammates when critical and outnumbered | Back to friends when critical and outnumbered (autonomous stance only) | Pull a losing squad back to a held zone and merge |
| **Resupply** | Locker run for a launcher when armour is known; reload between fights | Heal inside the barracks ring or at a supply truck (D2, H5) | Refill a depleted squad from production, or heal it at the nearest supply source; station the supply trucks (D2, H5) |

---

## 6. Phases

Estimates in solo-dev days, as IMPLEMENTATION_PLAN.md §4 counts them. Each phase ships behind
`--bot-ai htn` and leaves `legacy` untouched.

### H0 — Planner in the tree (0.5 day) — done

- Vendor FluidHTN at `e67af26` (§3.8); `Tests` include line; `PooledHtnFactory` in `Scripts/Sim/Htn`,
  from the bench's `PooledFactory`.
- `--bot-ai legacy|htn` and `GameModeDefinition.BotAi`, parsed and tested in `LaunchOptionsTests`.
- `HtnPlannerTests`: probes P1–P8 of §3.4 as xUnit facts, so that a bump of the pinned commit that
  changes priority, pre-emption, lookahead, partial planning, sharing or allocation fails
  `dotnet test`. They start on the sketch domain and move to the real ones as H2–H4 write them.
- **Done when:** `dotnet test` passes with FluidHTN compiled in, and `./scripts/htn-bench.sh --probe`
  uses the vendored copy and still passes all nine.
- **Result:** `dotnet test` 942 passed, 0 failed (908 before; 34 new: 16 in `HtnPlannerTests`, 6 in
  `PooledHtnFactoryTests`, 12 in `LaunchOptionsTests`), 0 warnings. `./scripts/htn-bench.sh --probe`
  runs on `ThirdParty/FluidHTN` with the game's `PooledHtnFactory`: all nine pass; the pooled cost
  rows read 0 B (§3.5). `dotnet build Gdpyr.csproj`: 0 warnings, 0 errors. The guards were checked
  to fail: no MTR rejection fails P3; unpooled arrays fail both P8 zero-allocation facts; unpooled
  queues fail the factory tests. Beyond the plan: the partial-plan finding and D6 (§3.4), and
  `BotDirector` resolving `--bot-ai` over the game mode and logging, for `htn`, that every bot
  still runs legacy.

### H1 — Shared knowledge (1.5 days) — done

- `ContactMemory`, `Neighbourhood`, `ZoneBoard`, `SquadBoard`, engine-free, fixed-size, tested
  (ageing, capacity overflow, force-ratio bands, squad membership when a unit dies or is re-ordered).
- Feeds: `VisibilityService` → strategist memory after each fog refresh; `GroundSensor` scans →
  ground memory; `ApplyOrder` → ad-hoc squads and issuer.
- **Done when:** the debug HUD shows each side's contact count and squad count, and the numbers
  match what is on the field in a `--listen` round.
- **Result:** `dotnet test` 1004 passed, 0 failed (942 before; 62 new: 13 in `ContactMemoryTests`,
  19 in `NeighbourhoodTests`, 13 in `ZoneBoardTests`, 17 in `SquadBoardTests`, each with a
  zero-allocation fact), 0 warnings; `dotnet build Gdpyr.csproj` 0 warnings, 0 errors. The rules
  each board settles are in §4.3, "As built in H1". On the HUD, under `fog`: `contacts: strategist
  <live> live <ghosts> ghosts  ground <live> live <ghosts> ghosts` and `squads: <n>/8  <units>
  units`, authority only; overflow, eviction and recycling counts appear only when they are not 0.
- **Checked in a `--listen` round** (Godot 4.6 .NET, Xvfb, default game mode: five ground bots and
  one computer strategist, the host on the ground, later strategist). A temporary probe, not
  committed, logged every 300 ticks the boards next to what it recomputed from the field — ground
  players alive and visible to the fog; hostile units and armed structures a living ground bot had in
  range with a clear line at that tick; living units grouped by issuer, order, target and focus. At
  tick 5400 the HUD read strategist 1 live 1 ghost, ground 4 live, squads 2 with 7 units, and the
  probe had the fog at 2 seen, one of them the host, dead — the ghost; the same four ground ids in
  memory and on the field (a tank seen by two bots); and two order groups of 4 and 3. The ground
  count trails the field by up to one scan interval when a unit first comes into view (3 against 4
  at tick 4200, equal at 4500): the memory is only as new as the last scan. Squads matched the order
  groups at every sample: the bot's garrison, reinforced a unit at a time, stayed one squad of 4;
  switching to strategist and giving every unit one move order formed one squad, issuer peer 1, of
  5, and closed the bot's two. A ghost left the memory once it was 480 ticks old.
- Beyond the plan: an order batch that repeats a squad's order joins it rather than forming a new
  one (§4.3), because without that the computer strategist's garrison is one squad per unit; and
  the ground memory forgets dead units and destroyed structures on a 10-tick prune, which the unit
  snapshot already shows every ground client.

### H2 — Ground bot HTN (2–3 days) — built; the `--listen` check is outstanding

- Ground facts, domain (§5.1), coordinator; `BotPilot` consumes `GroundIntent`; `BotBrain` taps `Use`.
- `GroundHtnTests`: for each row of the domain, a fact vector and the task chain it must produce —
  e.g. armour known + small arms + locker runner → `[path to locker, use locker]`; critical +
  outnumbered while in `Attack` → the plan is replaced by `Retreat`.
- **Done when:** in a `--listen` round with `--bot-ai htn`, bots stand on strategist nodes (the HUD's
  `nodes` row shows contested nodes), a bot fetches a launcher after a tank is seen, and the
  `locker_launcher_vs_tank` scenario still passes.
- **Result so far:** `dotnet test` 1080 passed, 0 failed (1004 before; 76 new: 59 in
  `GroundHtnTests`, 17 in `GroundCoordinatorTests`), 0 warnings; `dotnet build Gdpyr.csproj` 0
  warnings, 0 errors. `GroundHtnTests` covers every row of §5.1, Retreat pre-empting a running
  Attack, the P3 case on the real domain, every long-running operator ending when its premise goes
  (a theory over all ten), the locker trip, both give-ups, the fact bands and their hysteresis,
  determinism, and zero bytes over 64 bots × 7,200 ticks with the pooled factory (the same run with
  `DefaultFactory` must allocate, so the guard can fail). Removing `Sweep`'s executing condition and
  the give-up count fails five of them (checked, then reverted). `HtnPlannerTests` stays on the
  bench's sketch: it pins the library, and the bench still runs that sketch. The playtest harness
  takes `--bot-ai` and passes it to the server it starts; without it a scenario plays legacy, as
  before.
- **What the checks showed.** Headless, through the harness rather than `--listen` (Godot 4.6 .NET):
  - All twelve scenarios in `Tests/Scenarios` pass under `legacy` and under `htn`, and
    `./scripts/htn-bench.sh --probe` still passes all nine. `locker_launcher_vs_tank` is among them;
    its one ground seat is policy-driven, so under `htn` it passes without the planner deciding
    anything.
  - A temporary probe, not committed, logged every bot's task, role and position and every node's
    holder every 300 ticks, over a scenario not committed either: five ground bots, the computer
    strategist (legacy), one idle ground seat so that the bots fill, a tank on the middle node; two
    seeds of three minutes. **A bot fetched a launcher after the tank was seen**: at tick 1500 of a
    90 s run the coordinator made a rifleman the runner once one of the two launcher bots had died,
    and the log has `peer …830 swapped the rifle for the launcher`, 300 ticks before it was
    advancing with it. In the three-minute rounds two launchers were in hand nearly all the time, so
    no runner was needed. **Denial**: ticks with a node the ground force holds, 17,835 of 21,600
    under `htn` against 2,097 under `legacy`; ticks with a node contested, 492 against 484; ground
    deaths 59 against 65. Deniers took and held the two near nodes; the far one, the only one the
    strategist held for long, was never retaken — its deniers were pulled into fights on the way, as
    Attack's place above Defend Zone says they should be.
  - Two changes came out of the probe rounds, both in the "as built" table of §5.1: a denier's hold
    point is on the spawn side of its node (the first round parked a denier on the barracks' ring
    for good), and a denier does not investigate ghosts (before that, 0 contested ticks: deniers
    chased remembered contacts instead of walking to their node). A third is in the pilot: a jump
    of more than 5 m in one tick is a new life — a death's respawn, or a round reset, which
    respawns without a death — and the plan is dropped and the next scan taken at once.
- **Found, not caused by H2, not fixed here.** At a round reset `PlayerManager.TeleportToSpawn` puts
  spawn indices *i* and *i* + 4 on the same point of the test map's four, and two coincident
  character capsules push each other upward about 2 m a tick and out of the world; nothing kills a
  body that leaves it, so it stays alive with no position worth the name. Reproduced under `legacy`
  and `htn` alike with seven ground players. **Fixed in H6** (`SpawnLap`, §6 H6), which also found
  that every probe round here with more than four ground players had run with it. Separately, the barracks' defended ring reaches about
  117 m, over the far half of the middle node; `OutsideDefences` keeps every bot out of it.
- **Left for the `--listen` check and later:** the round with a display and the HUD's `nodes` and
  `ground plan` rows (the probe counted contested nodes, it did not read the HUD); `bot_plan <n>` on
  the console (§4.4), not written; friends' `Engaging` is not fed to the survey, which the ground
  domain does not read.

### H3 — Unit HTN (2 days) — built; the engine-side check is outstanding

- Unit facts, domain (§5.2), `UnitIntent` applied in `SimulateUnit`; defend posts; squad focus.
- `UnitHtnTests` over fact vectors; existing `UnitBrainTests` unchanged and passing.
- **Done when:** a human's selection given one attack order arrives together and focuses one target;
  a garrison spreads round its anchor; per-tick server time on the debug HUD is within noise of
  `legacy` at 64 units.
- **Result so far:** `dotnet test` 1184 passed, 0 failed (1080 before; 104 new: 71 in
  `UnitHtnTests`, 27 in `SquadCensusTests`, 3 in `NeighbourhoodTests`, 3 in `SquadBoardTests`), 0
  warnings; `UnitBrainTests` unchanged, 24 passing; `dotnet build Gdpyr.csproj` 0 warnings, 0 errors.
  `UnitHtnTests` covers every row of §5.2 as built, Retreat pre-empting a running Attack and only
  for a computer strategist's order, a new order ending a plan for a lower branch, the P3 case on the
  real domain, every operator ending when its premise goes (a theory over all twelve), keeping pace
  (the wait, its hysteresis, the cap on one wait, a slow squad waited for more than 8 s in all, a
  wait counted once a tick however often the operator runs), the fact bands, determinism, and zero
  bytes over 64 units × 7,200 ticks with the pooled factory (the same run with `DefaultFactory` must
  allocate). `SquadCensusTests` covers the counts, ranks, the focus rule and its stickiness, the
  phase, zero bytes for a census of 64 units a tick, and the posts and bearings. Checked to fail,
  then reverted: without Advance's executing condition on its order, 2 fail; with pacing off, 5;
  with the focus not kept, 1. Legacy is unchanged: with no plan, `SimulateUnit`, `AcquireTarget`,
  `ApplyOrder` and `Damage` compute what they did before.
- **Cost, the engine-free half.** A scratch program, not committed, doing what `UnitManager` does
  per tick under `htn` apart from the engine — the census and neighbour list, each unit's scan
  staggered over 10 ticks (survey, memory, posts, encode), then track and plan every unit — for 64
  units in 8 squads with 16 remembered contacts: 14.8–17.0 µs per tick over three runs of 3,600
  ticks (about 0.1 % of a tick), 0 B allocated; 4-core Intel Xeon @ 2.10 GHz, .NET SDK 8.0.131.
- **Not checked: everything that needs the engine.** This session had no Godot (the environment's
  network policy refused `github.com` release downloads), so the three checks above, and the twelve
  scenarios under `htn`, are still to run. The HUD has what they need, on the authority: `unit plan`
  (units on each task, and how many are waiting for their squad) and `unit ms` (the unit pass, mean
  of the last second, under either AI, which is the number to set against legacy at 64 units).
  Scenario-placed units have no issuer and no squad, so under `htn` they obey, and a lone defender's
  post is its anchor: the scenarios should play as they do under legacy, but that is a prediction.

### H4 — Strategist commander (3 days) — built; the display check is outstanding

- Commander domain (§5.3) over the boards; `Build` / `Fortify` as primitives; human-claim rule.
- `CommanderHtnTests`: contested node + idle squad → `Defend Zone` on that node; no contact for 20 s +
  a technical → `Recon`; squad at 30% vs 2× enemy → `Retreat`; existing `StrategistBrainTests`
  unchanged.
- **Done when:** in a bot-only round the strategist retakes a denied node, a technical scouts, and a
  losing squad withdraws rather than dying in place.
- **Result:** `dotnet test` 1236 passed, 0 failed (1184 before; 52 new: 51 in `CommanderHtnTests`, 1
  in `SquadBoardTests`), 0 warnings; `StrategistBrainTests` unchanged; `dotnet build Gdpyr.csproj`
  0 warnings, 0 errors; `./scripts/htn-bench.sh --probe` passes all nine. `CommanderHtnTests` has
  the three cases above as facts. It also covers every row of the squad domain; stage → strike
  through `PausePlan`, and the stage's time limit; a taken node ending the attack; a live contact
  named and a ghost never named (D4); the weak squad that waits and is sent production; buy time;
  the garrison as the defence of home; recon by stale node and by quiet; where a retreat goes, and
  its merge on arrival; refill and its merge at home; escort armour; D1; every operator ending
  when its premise goes (a theory over seven operator and premise pairs); and the P3 case on the
  real domain. On a round in miniature over 64 units it checks determinism across 2,000 decisions
  that reach every goal, and 0 B over 4,000 decisions after 800 of warm-up with the pooled factory
  (the same run with `DefaultFactory` must allocate). Checked to fail, then reverted: without
  Scout's executing condition, 2 fail; with a stage that never ends, 5; without D6's line, 1.
- **D6** is done (§8): the vendored `Planner.cs` carries the one line and the pinning test asserts
  0 B.
- **Cost, the engine-free half.** A scratch program, not committed, running `Commander.Decide` for
  64 units with 6 live contacts: 11.2, 11.3 and 45.7 µs per decision over three runs of 3,600
  decisions (the slow one the first, still compiling), one decision every 30 ticks: at most
  1.5 µs a tick; 4-core Intel Xeon @ 2.80 GHz, .NET SDK 8.0.131. The squad contexts are walked through every branch when they are
  made, because a slot that first fell back late in a round allocated then; after that a round
  allocates nothing.
- **What the checks showed.** Godot 4.6 .NET was available this time; everything ran headless
  through the harness.
  - All twelve scenarios in `Tests/Scenarios` pass under `legacy` and under `htn`, H3's
    outstanding scenario check included. `builder_fortifies` and `strategist_economy` have a
    computer strategist, so under `htn` they now play the commander.
  - A temporary probe, not committed, logged the commander's squads, their tasks and the node
    holders every 600 ticks, and each fall-back, strike and reinforcement as it happened. It ran
    over a scenario, not committed either: six ground seats (five bots, one idle scripted seat so
    that the bots fill), one computer strategist, and a technical placed at its barracks. For a
    like-for-like comparison, a temporary switch, also not committed, kept the strategist on
    legacy while the ground bots and units stayed on `htn`. Two seeds of six minutes each:

    | | Strategist legacy | Strategist `htn` |
    |---|---|---|
    | Node samples (3 nodes, once a decision) held by the strategist | 0 of 3,705 | 490 of 3,654 |
    | Denied nodes retaken | 0 | 1 |
    | Neutral nodes taken | 0 | 2 |
    | Seed 2 | holds no node | holds all three from tick ≈ 34,800 until the round resets at 36,816 |

  - **Retakes a denied node:** yes, in every `htn` run long enough to have a squad ready: a 3-minute
    round (node 1 at tick 5,771, after a staged strike), the six-minute seed 2 above, and a
    six-minute run without the placed technical (node 1 at tick 28,101).
  - **A technical scouts:** yes, once technicals were kept out of the garrison: in a 2-minute round
    all 46 decisions with a scout out had the technical as the scout. Before that change, the
    technical placed first on the field joined the garrison and a rifleman scouted.
  - **A losing squad withdraws:** the one squad that met Retreat's condition, in the 3-minute round
    (tick 8,501: 1 unit left, 259 of 650), fell back and arrived alive. No unit died while falling
    back in any round. Most squads shrank by attrition without meeting the condition, which needs
    the enemy remembered within 40 m of the squad at 1.5 times what is left: the fog saw the
    ground bots only intermittently. Those squads went home depleted through Resupply instead.
  - Two changes came out of the probe rounds, both in §5.3's table: a depleted squad that reaches
    home is merged, where the first rounds had one sit there for the rest of the round with
    nothing to refill it; and technicals stay out of the garrison.
- **Found, not caused by H4, not changed here.** With the ground bots on `htn`, the ground force
  held the nodes for most of each round (ground-held in 2,455 and 2,281 of the samples above), and
  while it held them all the strategist had no income: the legacy arm built 9 units in each seed,
  the `htn` arm 9 in seed 1 and 38 in seed 2, the one where it took the nodes. With so few units
  `TryChooseTier`'s infantry-per-heavy screen never buys a technical, which is why the scenario
  places one. Economy balance is H6's playtest question.
- **Left for a display check:** a `--listen` round with the HUD's `commander` row on screen, and
  H2's and H3's display checks (§6 above).

### H5 — Unit resupply (0.5–1 day, only if D2 is adopted) — built; D2 decided with a supply truck

- `RegenPerSecond` on `UnitDefinition`, applied inside a friendly barracks' defended ring; tested in
  `DefenseTests` style.
- **D2 as decided** (§8): healing at the barracks **and** at a new unit, the supply truck, which a
  strategist buys and units visit. So H5 also has: the truck (`Units/supply_truck.tres`, catalog id
  4, `SupplyRadiusMeters`), the human strategist's `8` key and barracks-card button, the agent
  API's `build` tier 4 and scenario name `supply_truck`, the unit domain's Resupply branch (§5.2) and
  the commander's truck and heal-refill (§5.3). The rule is [`NETCODE.md`](NETCODE.md) §10.7.
- **Result:** `dotnet test` 1297 passed, 0 failed (1236 before; 61 new: 18 in `ResupplyTests`, 27 in
  `UnitHtnTests`, 15 in `CommanderHtnTests`, 1 in `StructureTests`), 0 warnings; `dotnet build
  Gdpyr.csproj` 0 warnings, 0 errors; `./scripts/htn-bench.sh --probe` passes all nine.
  `ResupplyTests` covers reach (flat, the barracks' minimum, a truck's), whose sources count (the
  side's, never a truck's own), the three-second lockout and its wrap-around, the rate and its cap,
  and a full field checked without allocating. `UnitHtnTests` covers the branch for every order it
  applies to, standing in the reach until healed, following a truck that drives off, D1, move and
  build orders, a remembered contact against a target of its own, a unit already in reach walking
  out of it, range and its hysteresis, no source that can heal, the give-up and its retry,
  Retreat's priority, the three bands, three new premise rows, and the fourteen-situation
  determinism and zero-allocation runs. `CommanderHtnTests` covers the truck's squad (never the
  garrison, the scout or an assault), each station and the two rules the probe round added,
  healable and not, meeting a squad, a healed squad going back to work unmerged, healing at home,
  the heal time limit, the domain rows, and trucks in the churn that the determinism and 0 B runs
  go through. Checked to fail, then reverted: without Resupply's `Wounded` executing condition, 2
  fail; without its give-up, 1; without Refill's heal branch, 4; without `Station`'s executing
  condition, 1; without the trucks' enemy rule, 1.
- **What the checks showed.** Godot 4.6 .NET; the harness headless, and one display check.
  - All twelve scenarios in `Tests/Scenarios` pass under `legacy` and `htn`, before H5 and after
    it. `builder_fortifies` asserts `events.unit_built.tier.max == 3`: a truck bought inside its
    110 s would make that 4 under `htn`. None was — the commander waits for eight fighting units —
    but the assertion is one balance change from failing.
  - **Display check** (`--listen` under Xvfb, OpenGL, a temporary hook that took the host to the
    chair, selected the barracks, queued a truck and two riflemen and saved the viewport): the
    barracks card shows five buttons, the fifth `Supply Truck (8) 120 pts`; points went from 1,000
    to 780; the key list's new lines fit above the bottom edge; and the truck stands in its green
    12 m ring inside the barracks' red one. The H2–H4 display checks are still outstanding.
  - **Probe rounds.** A temporary probe, not committed, logged every two seconds the units healing
    and the health given back, the trucks, the units on each task, every wounded unit's Resupply
    facts, and each squad Refill and unit Resupply as it started and ended; a temporary switch, not
    committed either, turned the rule off. The H4 scenario — five ground bots, an idle seat, one
    computer strategist — did not exercise it: the ground force held all three nodes by tick
    ≈2,500, the strategist sat at 40 points with at most seven units, and no squad ever depleted
    (H4's economy finding again), nor with three ground seats. So the rounds placed an army of twelve
    riflemen at the barracks — free, and nobody's order, so the commander takes them — against the
    five bots, two seeds of four minutes under `htn`:
    - **Healing is live**, under either AI: 2,630 hp given back under `htn`, 771 under `legacy`
      (its garrison, inside the ring), 0 with the rule off.
    - **The commander buys a truck**: 24 s into every round with the army (ten of them the
      truck's build time), and again when one is lost — 57 s later, in one round.
    - **Squads heal instead of being merged.** In the final `htn` run, 10 Refills. Of the 7 that
      healing could restore, 4 ended with the squad healed and back at work as the same squad (3
      at a truck, 1 at home), 2 with the squad dead, and 1 when a threatened zone called it
      (Defend Zone is above Resupply). Of the 3 it could not, 2 were merged at home, as in H4, and
      1 was called to a zone.
    - **Unit Resupply rarely has work on this map.** Over a diagnostic run's two seeds, of 248
      samples of a wounded unit, 202 were already inside a reach — the barracks' ring comes within 2, 8 and 38
      m of the three nodes, so a wounded unit mostly heals where it stands — 67 had a target of
      their own, and 89 were under a move order (the commander's Refill or Retreat), which the
      branch never second-guesses. The branch ran 5 times in the final run: once to 96 %; the
      others ended on a new order, on Retreat when a contact came into view, or with the unit dead.
      On a map with nodes further from the barracks it has more to do; that is H6's playtest.
    - Two changes came out of the rounds, both in §5.3's table: a truck sent to the staging point of
      a squad already inside 72 m of its target stood in the fight, and one sent to meet a squad
      that had just broken contact was shot there. The first trucks of the two seeds lived 20 s
      and 87 s, then 26 s and 64 s with the staging distance alone; with the enemy rule as well,
      119 s, and the whole 217 s it had.
    - Not a balance result: of the four rounds only seed 1's with the army ended in both arms (the
      strategist won, in 3.75 min with the rule and 2.43 without), rounds restart without the
      placed army, and two seeds cannot say which side healing favours. H6 asks that.
- **Legacy.** The rule applies under `--bot-ai legacy` too; legacy's decisions are unchanged and it
  never buys a truck, but the world the RL baseline plays in is not (D5): its garrison now heals.

### H6 — Scenarios, playtest, switch the default (1 day, then ongoing) — done; the playtest with people is outstanding

- New scenarios in `Tests/Scenarios/`, distributional as AGENT_API.md §3 requires, e.g.
  `htn_ground_denies_node.json` (strategist nodes contested for ≥ N ticks), `htn_strategist_defends_node.json`,
  `htn_recon_finds_contact.json`, `htn_squad_retreats.json` (units lost per engagement vs `legacy`).
  Metrics beyond today's vocabulary need events on `AgentEventBus` (e.g. a squad mission change),
  which is an agent-API change and is documented there when made.
- Playtest with people; compare rounds against `legacy` on the M8 columns (round length, outcome,
  units lost by tier, nodes held over time).
- **Done when:** the default flips to `htn`, and `docs/TRAINING.md` / `RL_ARCHITECTURE.md` say which
  bot a trained policy was evaluated against.
- **Result:** the default is `htn` (`BotAiNames.Default`, `Match/default_gamemode.tres`), and
  `TRAINING.md` §3, §8, §9 and `RL_ARCHITECTURE.md` §6 rule 5 say every policy trained or evaluated
  with this repository's scripts was measured against the legacy bots, which the scripts pin (D5).
  `dotnet test` 1312 passed, 0 failed (1297 before; 15 new: 6 in `ScenarioTests`, 5 in
  `SpawnLapTests`, 3 in `AgentEventTests`, 1 in `LaunchOptionsTests`), 0 warnings; `dotnet build
  Gdpyr.csproj` 0 warnings, 0 errors; `./scripts/htn-bench.sh --probe` passes all nine. Checked to
  fail, then reverted: without the spawn side-step, 3 fail; with the default back at legacy, 1; with
  the harness not knowing `node_contested` and a filter ignoring its conditions, 4.
- **What H6 added to measure the bots with** (the agent-API change the plan anticipated;
  [`AGENT_API.md`](AGENT_API.md) §7.1, §8, §9.1):

  | Addition | What it says | Why |
  |---|---|---|
  | `squad_task` event | A commander squad took a new goal (`CommandGoal` by number: 1 fall back, 2 reinforce, 4 strike, 5 scout, …), with where its order points | The plan's "squad mission change": nothing on the stream said what the commander decided |
  | `contact_spotted` event | The strategist's fog picked a player up, the tier of the unit that saw it, and whether it was the first sighting this round | Recon is about who found whom |
  | `node_contested` event | A node's contest started, or ended with how long it lasted | "Contested for ≥ N ticks"; listed in §8 since M6 and never emitted |
  | Event filters | `events.<kind>[<field><op><value>,…].<rest>`: `events.node_captured[owner=1,claimant=2].count` | One kind, many questions: which node, whose, which goal. Also M8's "units lost by tier" (`events.unit_lost[tier=2].count`) |
  | `bot_ai` | On a scenario; in `welcome`; in the harness's summaries and trace headers | A claim about one AI is run against it, and checked against the other with `--bot-ai` |
  | A test over every checked-in scenario | Each parses, and each `events.*` metric in it resolves | A typo in a filter fails `dotnet test`, not a playtest. It found that a kind's field named in full errored rather than reading zero when the kind never fired, as §9.1 says it should; fixed |

- **The four scenarios.** Each makes a claim that the bots of §5 exist to satisfy, names `"bot_ai":
  "htn"`, passes, and fails under `--bot-ai legacy` on the claims that separate them. The setups
  were chosen, and the thresholds set, from draft runs of more seeds than the files hold, under both
  AIs — the denial and retreat drafts on the build with the spawn fix below; the recon and defence
  drafts, with one ground player, which that bug never reached, before it. A round with nobody
  attached has no bots (the backfill fills around somebody, `BotFillPolicy`), so a bot-only scenario attaches one idle ground seat and puts
  it in the far corner; with a sixth ground player, 5 bots play.

  | Scenario | Setup | Claims | Drafts: `htn` | Drafts: `legacy` |
  |---|---|---|---|---|
  | `htn_ground_denies_node` | 5 ground bots, no strategist; 2 riflemen placed on the far node (−25, −100), which they take 8 s in. 90 s, 4 seeds | The strategist takes the far node (the premise); the ground force takes ≥ 2 nodes, every seed; the far node is taken from the strategist in ≥ half | 7 seeds: ≥ 2 nodes 7/7; far node taken 5/7, 30–82 s in | ≥ 2 nodes 0/7 (0 or 1 each); far node 0/7, and 1/4 in the suite run below |
  | `htn_strategist_defends_node` | 1 strategist; 12 riflemen placed on the middle node; one ground seat that waits 15 s 60 m from it on the ground spawn's side, then walks at it for 8 s. 60 s, 3 seeds | The strategist takes all three nodes; a squad is sent to defend a node rather than home (`squad_task[goal=2,x<50]`; the barracks is at x = 100); no node is lost | 3/3 on each: reinforcements at the middle and west nodes as the raider came in | 1–2 nodes taken; nothing sent; the minute spent killing the raider at the spawn, 16–18 times |
  | `htn_recon_finds_contact` | 1 strategist; a technical placed at the barracks; a ground seat hiding still on the far node. 60 s, 3 seeds | A scout goes out; the hider's first sighting is the technical's | 3/3: the scout task at the first decision, first sighting 10–27 s in, then staged and struck | 0/3: nothing seen in 90 s — the technical stays in the garrison |
  | `htn_squad_retreats` | 5 ground bots, 1 strategist; 12 riflemen placed about 63 m from both the ground spawn and the middle node. 90 s, 4 seeds | A strike; a squad falls back in ≥ half the seeds | 7 seeds: a strike 7/7; a fall-back 6/7 — the scout in 6, an assault squad in mid-strike in 4 | legacy has no squads |

  The ground seat idle in the corner, the placed units that nobody ordered and the one computer
  strategist are the whole of each setup; everything else is the bots. `htn_ground_denies_node` has
  no computer strategist on purpose: the claim is the ground side's.
- **Units lost per engagement against legacy** — the comparison the plan gave
  `htn_squad_retreats`. It is **not** a claim the scenario makes, because it does not come out in
  the HTN's favour. The same setup, 7 seeds each:

  | | `legacy` | `htn` |
  |---|---|---|
  | Placed riflemen lost, of 12 | 8 in 7/7: the eight outside the garrison, every one | 8–9 |
  | Strategist units lost in all | 10 | 11–13 |
  | Ground players killed | 6–19, median 16 | 6–20, median 10 |
  | A squad fell back | never: legacy has no squads | 6/7 |

  The Retreat branch fires in a real engagement, and it saves nothing measurable: in the 30 s after
  each of the four assault fall-backs the side lost 1, 5, 6 and 6 units. Retreat waits for 40 % of
  strength at formation (§5.3): a squad of four riflemen at full health gets there with one left. With the army placed 37 m
  from the ground spawn instead, legacy's blob **spawn-camps**: in 3 of 7 seeds it killed all 50
  ground tickets in about 30 s for 2–3 units; the commander, whose squads go to nodes and targets,
  did that in none, and lost 9–12 units in each. In 1 of those 7 a depleted squad in the middle of
  its refill was sent back to reinforce the middle node: `Commander.Idle` does not look at
  `Depleted`, and Defend Zone is above Resupply in the tree (H5 saw the same once).
- **Comparison rounds on the M8 columns.** Bot-only rounds, 5 ground bots and the idle seat against
  one computer strategist, 20 minutes each, 4 seeds per AI, the fixed build, read off the event
  stream (no M8 writer exists yet):

  | Column | `legacy` | `htn` |
  |---|---|---|
  | Round length | 20:00, 4/4 | 20:00, 4/4 |
  | Outcome | the clock: the ground force wins, 4/4 | the same, 4/4 |
  | Tickets left, of 50 | 28–45 | 31–42 |
  | Units built, by tier | 7 infantry, 1 tank, 1 builder (4/4) | 7 infantry, 1 technical, 1 tank, 1 builder (4/4) |
  | Units lost, by tier | 3 infantry, 1 tank, 1 builder (4/4) | 3 infantry, 1 technical (4/4) |
  | Nodes held over time (share of 3 nodes × the round) | ground 32–62 %, neutral 38–68 %, strategist 0 | ground 98.3 %, neutral 1.7 %, strategist 0 |
  | Last death on either side | 2.2–2.8 min | 2.3–3.3 min |

  Every one of the eight rounds is decided in its first three minutes and waits seventeen for the
  clock. The strategist spends its opening 1,000 points in the first 1.1 minutes, never holds a node,
  so never earns another point, and nothing of either side dies after 3.3 minutes: the ground bots
  of both AIs stop at the edge of the barracks' defended ring (§1, `OutsideDefences`), and the
  strategist cannot be eliminated while it has a unit alive (`WinConditions`). The HTN ground force denies
  the nodes almost completely where legacy's leaves most of them neutral; the outcome is the same.
  That is H4's open question — economy balance — answered for bot-only rounds on the test map: the
  strategist is locked out of income by the ground force's first two minutes, under either AI.
- **Found and fixed: players pushed out of the world at every round reset.** H2 found it and left
  it (§6, H2): spawn index *i* is spawn point *i* mod 4 on the test map, so with more than four
  ground players two capsules are put in one place at a reset and push each other upward out of the
  world, where nothing kills them. It made the first run of `htn_ground_denies_node` fail on a seed
  where one bot of five was playing. `SpawnLap` puts each lap of indices round the points in a slot
  of its own — 2 m to the side, 2 m behind, or both — which is 16 players on four points
  (`SnapshotCodec.MaxPlayers`); `spawn_points_shared.json` is the regression scenario, six idle
  seats left where the reset puts them. Before the fix the four on shared points reached the
  observation's height ceiling (256 m) within 85 ticks of the reset and the other two stood at the
  spawn height, 4.07 m; after it all six stand at 4.07 m, in both seeds. **Every earlier probe round
  with more than four ground players — H2's, H4's, H5's — ran with up to four of them out of the
  world from each reset**, under both AIs alike; so did H6's first drafts with six ground players,
  which were thrown away. Every H6 number above from a run with more than four ground players is
  from the fixed build.
- **Found, not changed** — each is a change to §5 or to the game, and each is now measurable by the
  scenarios above:
  - Contests do not happen between bots: in 50 episodes of the fixed-build runs above, 0
    `node_contested` records. A fight at a node is decided at range before either side stands in
    it (`node_contested.json` shows the event itself works). So `htn_ground_denies_node` claims
    nodes taken, which is the stronger thing, rather than time contested.
  - A lone denier: the coordinator sends one denier per strategist node (§5.1), and against two
    riflemen the HTN ground force spent 1–13 lives (median 5) taking the far node; legacy spent 2–8
    (median 3) and did not take it.
  - The retreat comes late and a depleted squad counts as idle for Defend Zone (above).
  - A pillbox the builder puts up on the west node's spawn-facing side, (−25, −11), is 35–40 m from
    every ground spawn point, inside its 45 m reach, and kills players as they respawn — seen in the
    recon drafts. Fortify is shared by both AIs.
- **The suite, on the fixed build** (`./scripts/playtest.sh Tests/Scenarios/*.json`, headless,
  Godot 4.6 .NET): with no `--bot-ai`, all 17 pass — the twelve from before H6 under the new
  default among them. With `--bot-ai legacy`, the twelve and `spawn_points_shared` pass and the four
  `htn_*` fail, each on the claims that separate the two AIs: the denial scenario on nodes taken
  (0 in the first seed) and the far node (1 seed of 4), recon on the scout and its sighting, the
  retreat on the strike and the fall-back, the defence on nodes taken (2) and the squad sent.
  `node_contested.json`, written after those runs started, passes under both on its own run.
  `builder_fortifies` still asserts `events.unit_built.tier.max == 3`: the commander, now the
  default, did not buy a truck in its 110 s, as in H5.
- **Left:** the playtest with people, which a headless session cannot do; the display checks of
  H2–H4 (H5's own was done); `bot_plan <n>` on the console (§4.4), never written — since built as
  `ai_plan` and the spectator's AI debugger ([`AI_DEBUG.md`](AI_DEBUG.md)), whose first run found a
  ground bot thrashing between Standoff and "leave defences" at the barracks' ring (§9, plan
  thrash; [`AI_DEBUG.md`](AI_DEBUG.md) §9); and the changes
  the findings argue for — an earlier retreat, a depleted squad not idle for Defend Zone, deniers in
  pairs against a defended node, and an economy the strategist is not locked out of on a map whose
  nodes the ground force takes in two minutes — which are the next tuning, measured by these
  scenarios.

Total: about 10–12 days before playtesting.

---

## 7. Verification

| Claim | Proof |
|---|---|
| FluidHTN behaves as §3 says it does | `./scripts/htn-bench.sh --probe` today (§3.4, nine passing); `HtnPlannerTests` in `dotnet test` from H0 |
| The planner allocates nothing on the tick | P8, in both of the above; `./scripts/htn-bench.sh` for the per-tick figure |
| A given situation produces a given plan | Fact-vector → task-chain tests per domain (H2–H5); no engine needed |
| Units heal where and when D2 says | `ResupplyTests` (reach, whose sources, the lockout, the rate); the debug HUD's `resupply` row in a round |
| Legacy behaviour is intact | Existing `BotBrainTests`, `UnitBrainTests`, `StrategistBrainTests` unchanged; `--bot-ai legacy` one flag away since H6, and pinned by the training scripts |
| The bots behave as described in a real round | The four `htn_*` scenarios of H6, run by `./scripts/playtest.sh`, which pass under `htn` and fail under `--bot-ai legacy` |
| A trained policy's result names its opponent | `bot_ai` in `welcome`; the trainer's `--bot-ai` pin, attach line, summary line and `--metrics` column (H6) |
| It costs nothing noticeable | Debug HUD server-frame-time row, `legacy` vs `htn`, 64 units |

---

## 8. Decisions needed

| # | Question | Recommendation |
|---|---|---|
| D1 | Should unit-level HTN act on units a **human** strategist ordered? | Yes for how an order is carried out (cohesion, focus fire, defend posts, answering calls); no for autonomous Retreat/Resupply, which only units last ordered by a bot get (`Stance`). The bot commander does not re-order a unit a human ordered in the last 30 s. |
| D2 | RTS units have nothing to resupply: magazines reload without a reserve and nothing heals. Add healing inside a friendly barracks' defended ring? | Yes — one `RegenPerSecond` per `UnitDefinition`. Without it, unit Resupply means reinforcement only, and unit Retreat saves a unit that can never recover. **Decided for H5:** healing at the barracks, and at a new unit, the **supply truck**, which a strategist — a person with `8` or the barracks card, a policy with `build` tier 4, the commander on its own — buys and units visit. A game rule, not a bot's: it applies under legacy too, which changes the world legacy's baseline plays in (D5), not what legacy decides. |
| D3 | May a ground bot know what a **teammate** has seen? | Yes, as a team `ContactMemory` — the equivalent of voice callouts, and less than a human client already draws (the unit snapshot is not fogged for the ground force, IMPLEMENTATION_PLAN.md §M4). Today a bot knows only its own eyes (`BotTraits.SensorRadiusMeters`). |
| D4 | May the computer strategist act on **ghosts** (last-known positions)? `BotStrategist.TryObjective` deliberately does not. | Yes, for Recon and for staging only, and never to name a target: a human strategist can click a ghost (M4), so the bot doing so is parity, not cheating. |
| D5 | The scripted bots are the RL baseline ("beats the bot" should stay true, RL_ARCHITECTURE.md). | Keep `legacy` selectable for ever; `scripts/evaluate.sh` and the trainer pin `--bot-ai legacy` until a deliberate re-baseline, and training output records which one was used. **Done in H6:** `train.sh`, `train-selfplay.sh` and `evaluate.sh` start their servers with `--bot-ai legacy` (`--bot-ai htn` on the script is the re-baseline); `welcome` carries `bot_ai`; the trainer takes `--bot-ai legacy\|htn\|any`, default legacy, and refuses a server running other bots; its attach line, its summary line and a `bot_ai` column in `--metrics` say which bots it played. |
| D6 | FluidHTN drops a borrowed queue when a plan replaces a paused partial plan: 128 B per pre-emption, pooled or not (§3.4, P8). Needed only from H4, whose stage → strike is the one `PausePlan`. | Before H4: offer the one-line fix upstream; if it has not landed by then, patch the vendored copy with that line, record the patch in `ThirdParty/FluidHTN/README`, and flip the pinning test to assert 0 B. Accepting the allocation is the fallback: it is per event, not per tick. **Done in H4:** upstream's head was still `e67af26`; the vendored copy is patched, the README records it, and the test asserts 0 B. Offering the fix upstream is left to the maintainer. |

---

## 9. Risks

| Risk | Signal | Mitigation |
|---|---|---|
| Plan thrash: facts flicker across a band edge and plans churn | Debug HUD plan row changes every scan | Hysteresis on every band (the 1.25 × sensor drop rule `UnitBrain.ShouldDropTarget` uses); only banded facts in the world state |
| Obsolete plans run to completion | Units attack a contact that left | Executing conditions on every long-running operator (§3.7, shown necessary by P3); a test per operator that its premise failing fails the plan |
| Bots become too good | Ground force loses every round with bots on | Every threshold (force ratios, strength fractions, radii) on `BotTraits` / `StrategistTraits`; coordinator caps (one locker runner, a third as deniers) |
| Order semantics drift for human strategists | Players report units "not doing what they were told" | §5.2's rule: the order is the top-level task; `Move` is never second-guessed; autonomous Retreat only on bot-ordered units |
| Coordination costs rays | Fog or scan time grows on the HUD | Boards reuse existing scans and `VisibilityService`'s rays; no new line-of-sight queries in H1–H4 |
| Vendored library diverges from upstream, or a bump changes behaviour | Upstream fixes a bug we hit; MTR changes again (#8, #12) | Pinned commit in `ThirdParty/FluidHTN/README`; `HtnPlannerTests` and `./scripts/htn-bench.sh` gate every bump (§3.8) |
| Single maintainer | Upstream goes quiet | MIT, 3,340 lines, no dependencies: it becomes ours, with its 159 upstream tests' behaviour already pinned by the probes |

---

## Sources

- FluidHTN — <https://github.com/ptrefall/fluid-hierarchical-task-network> (read at `e67af26`,
  2026-03-18: `Planners/Planner.cs`, `Planners/IPlannerState.cs`, `Contexts/BaseContext.cs`,
  `Factory/DefaultFactory.cs`, `BaseDomainBuilder.cs`, `Operators/`, `Tasks/CompoundTasks/Selector.cs`,
  `README.md`; `git log` for history, tags and authorship)
- FluidHTN issues — <https://github.com/ptrefall/fluid-hierarchical-task-network/issues?q=is%3Aissue>
- FluidHTN announcement — <https://gamedev.net/news/fluid-hierarchical-task-network-planner-source-r853/>
- FluidHTN extension library — <https://github.com/ptrefall/fluid-hierarchical-task-network-ext>
  (read at its 2024-05-24 head: `UtilitySelector.cs`, `RandomSelector.cs`)
- `Fluid-HTN` on NuGet — <https://www.nuget.org/packages/Fluid-HTN> (registration API: one version,
  `0.0.0`, 2021-09-01)
- CHP: C# HTN-Planner — <https://sourceforge.net/projects/chpplanner/>
- Tencent behaviac — <https://github.com/Tencent/behaviac>
- SandboxAI — <https://github.com/OneManMonkeySquad/SandboxAI>
- UnityHTN — <https://github.com/konbraphat51/UnityHTN>
- godot-fluid-hierarchical-task-network — <https://github.com/fnaith/godot-fluid-hierarchical-task-network>
- T. Humphreys, *Exploring HTN Planners through Example*, Game AI Pro, ch. 12 —
  <https://www.gameaipro.com/GameAIPro/GameAIPro_Chapter12_Exploring_HTN_Planners_through_Example.pdf>
- R. Straatman, T. Verweij, A. Champandard, R. Morcus, H. Kleve, *Hierarchical AI for Multiplayer
  Bots in Killzone 3*, Game AI Pro, ch. 29 —
  <http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter29_Hierarchical_AI_for_Multiplayer_Bots_in_Killzone_3.pdf>
- T. Verweij, *A hierarchically-layered multiplayer bot system for a first-person shooter* (2007) —
  <https://www.guerrilla-games.com/media/News/Files/VUA07_Verweij_Hierarchically-Layered-MP-Bot_System.pdf>
- JSHOP2 — <https://github.com/mas-group/jshop2>
