# HTN bots — research and implementation plan

Status: **H0 done** (§6). FluidHTN is vendored at `ThirdParty/FluidHTN` and compiled into the game
and the tests; `PooledHtnFactory` is in `Scripts/Sim/Htn`; `--bot-ai legacy|htn` and
`GameModeDefinition.BotAi` are parsed, and `htn` still runs legacy; `HtnPlannerTests` holds probes
P1–P8 in `dotnet test`. Nothing in the game plans with an HTN yet: that starts at H2. H0 found one
FluidHTN defect the probes had not reached, a queue dropped when a paused partial plan is replaced
(§3.4, D6). `tools/Gdpyr.HtnBench` — §5.1's domain written against FluidHTN, the behaviour probes of
§3.4 and the cost measurement of §3.5 — and `scripts/htn-bench.sh`, which runs them, now run against
the vendored copy.

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
| `IFactory` (`CreateArray` / `FreeArray`, `CreateQueue` / `FreeQueue`, …) | Every collection the planner borrows goes through it and is handed back — all but one (D6) | A pooled factory; with it the planner allocates nothing (P8, §3.5), except where a paused partial plan is replaced (D6) |

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
  by applying it and reverting it; the vendored copy is unmodified). Two `HtnPlannerTests` facts
  pin both halves; the one that asserts the drop fails the day it is fixed. What to do about it is
  D6.

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
| A replan that replaces a paused partial plan drops the queue it set the remainder aside in (§3.4, P8). | 128 B per such pre-emption even pooled, in `PausePlan` domains only; pinned by a test, decided in D6 before H4. |
| The builder's `Do` takes `start` and `forceStop` but not the operator's `abort` callback. | A task that must clean up when aborted gets a builder verb that calls `SetOperator(new FuncOperator<T>(…, funcAborted: …))` directly. |
| `Build()` throws on a malformed domain; `FindPlan` throws on an uninitialised context. | Both surface at start-up, not mid-round; a test builds every domain. |
| Task names are strings; the decomposition log and `DebugMTR` build more. | Names are made once at build time. Logging and `DebugMTR` stay off outside development builds. |

### 3.8 How it is taken in

Done in H0.

- **`Fluid-HTN/` at `e67af26` is vendored into `ThirdParty/FluidHTN/`**: the library folder's 32
  `.cs` files (3,340 lines), byte-identical to upstream, without `Properties/AssemblyInfo.cs`, the
  old-style `.csproj` or the Unity package metadata (`Fluid.HTN.asmdef`, its `.meta`,
  `package.json`), with upstream's `LICENSE` beside them and the commit in a one-line `README`. Not
  upstream's `Fluid-HTN.UnitTests/`: it is MSTest, and the glob in §3.5 would compile it into the
  game. `ThirdParty/.gdignore` keeps the editor from writing `.uid` files into it.
- `Gdpyr.csproj` picks the library up through the SDK's default glob, unchanged (`dotnet build
  Gdpyr.csproj`: 0 warnings, 0 errors); `Tests/Gdpyr.Tests.csproj` has one
  `<Compile Include="../ThirdParty/FluidHTN/**/*.cs" />`, plus the bench's `GroundSketch.cs` and
  `Population.cs` for the probes.
- The probes are in xUnit (`HtnPlannerTests`), on the sketch domain until H2–H4 write the real ones.
  Bumping the pinned commit is: replace the folder, run `dotnet test` and `./scripts/htn-bench.sh`;
  a change in MTR behaviour fails a test rather than a playtest (checked: disabling
  `Selector.BeatsLastMTR`'s rejection, then reverting it, fails P3).
- No upstream changes are needed for H0–H3. One defect matters from H4 (D6). If upstream stops, the
  code is MIT and ours to maintain.

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
   `GameModeDefinition`. `legacy` is today's code path, kept intact, and stays the default until H6.

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

### 4.4 Where it plugs in

| File | Change |
|---|---|
| `ThirdParty/FluidHTN/` | Vendored library (§3.8). |
| `Scripts/Sim/Htn/` (new) | `PooledHtnFactory`, fact enums, `GroundContext` / `UnitContext` / `CommanderContext`, the three domain builders, `ContactMemory`, `Neighbourhood`, `ZoneBoard`, `SquadBoard`, `GroundIntent` / `UnitIntent`. |
| `Scripts/Bots/BotPilot.cs` | `Sample` asks the ground domain for a `GroundIntent` instead of calling `Objective`; `AcquireTarget` prefers the intent's target. `OutsideDefences`, `Reachable`, stuck recovery, the friendly-fire and blast checks stay. `BotBrain` gains a `Use` press (a locker tap). |
| `Scripts/Bots/BotDirector.cs` | Owns the ground coordinator and the team `ContactMemory`; builds pilots and commanders with the selected `BotAi`. Agent fallback branches unchanged. |
| `Scripts/Rts/UnitManager.cs` | In `SimulateUnit`, after acquisition and before `UnitBrain.Destination`, run the unit's planner and apply its `UnitIntent` (destination override, target preference, hold). `ApplyOrder` records the order batch as a squad and the issuer on the unit. |
| `Scripts/Rts/Unit.cs` | Server-only fields: squad id, order issuer, HTN context. Nothing replicated. |
| `Scripts/Bots/BotStrategist.cs` | `ServerTick` becomes the commander domain's tick; `Build` / `Fortify` / `StrategistBrain` survive as its primitive tasks. |
| `Scripts/Core/LaunchOptions.cs`, `Scripts/Match/GameModeDefinition.cs` | `--bot-ai`, `BotAi`. |
| `Scripts/Ui/Debug.cs`, `Scripts/Core/ConsoleCommands.cs` | A plan row on the debug HUD; `bot_plan <n>` prints a bot's current task chain from FluidHTN's `OnNewTask` / MTR debug. |

No message, codec, snapshot field or agent observation changes. A demo records frames and "no bot
thinks" during playback (DEMOS.md), so demos are unaffected.

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
└─ Economy                    existing StrategistBrain.TryChooseTier / ShouldQueue / builder / Fortify as primitives,
                                with production directed at the squad the plan above is short of
```

Garrison stays as today's first `GarrisonUnits` defending the first barracks, now a standing
`Defend Zone` squad. Units a human ordered in the last 30 s are not re-ordered (D1).

### 5.4 The five tasks at each layer

| Task | Ground bot | RTS unit | Strategist commander |
|---|---|---|---|
| **Defend Zone** | Hold a strategist node to stop its income (denier role) | Spread to posts round the anchor, answer friends' fights inside the leash | Send a squad sized to the threat to a contested node or barracks |
| **Recon** | Sweep the least-recently-seen node; investigate ghosts | Patrol without stopping for contacts it can report | One technical through stale zones on a patrol |
| **Attack** | Focus the coordinator's target with a buddy; armour only with an explosive | Wait for the squad, then focus fire; support engaged friends | Stage out of sight, strike when assembled, escort armour |
| **Retreat** | Leave barracks defences; fall back to teammates when critical and outnumbered | Back to friends when critical and outnumbered (autonomous stance only) | Pull a losing squad back to a held zone and merge |
| **Resupply** | Locker run for a launcher when armour is known; reload between fights | Heal inside the barracks ring (D2) | Refill a depleted squad from production; heal (D2) |

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

### H1 — Shared knowledge (1.5 days)

- `ContactMemory`, `Neighbourhood`, `ZoneBoard`, `SquadBoard`, engine-free, fixed-size, tested
  (ageing, capacity overflow, force-ratio bands, squad membership when a unit dies or is re-ordered).
- Feeds: `VisibilityService` → strategist memory after each fog refresh; `GroundSensor` scans →
  ground memory; `ApplyOrder` → ad-hoc squads and issuer.
- **Done when:** the debug HUD shows each side's contact count and squad count, and the numbers
  match what is on the field in a `--listen` round.

### H2 — Ground bot HTN (2–3 days)

- Ground facts, domain (§5.1), coordinator; `BotPilot` consumes `GroundIntent`; `BotBrain` taps `Use`.
- `GroundHtnTests`: for each row of the domain, a fact vector and the task chain it must produce —
  e.g. armour known + small arms + locker runner → `[path to locker, use locker]`; critical +
  outnumbered while in `Attack` → the plan is replaced by `Retreat`.
- **Done when:** in a `--listen` round with `--bot-ai htn`, bots stand on strategist nodes (the HUD's
  `nodes` row shows contested nodes), a bot fetches a launcher after a tank is seen, and the
  `locker_launcher_vs_tank` scenario still passes.

### H3 — Unit HTN (2 days)

- Unit facts, domain (§5.2), `UnitIntent` applied in `SimulateUnit`; defend posts; squad focus.
- `UnitHtnTests` over fact vectors; existing `UnitBrainTests` unchanged and passing.
- **Done when:** a human's selection given one attack order arrives together and focuses one target;
  a garrison spreads round its anchor; per-tick server time on the debug HUD is within noise of
  `legacy` at 64 units.

### H4 — Strategist commander (3 days)

- Commander domain (§5.3) over the boards; `Build` / `Fortify` as primitives; human-claim rule.
- `CommanderHtnTests`: contested node + idle squad → `Defend Zone` on that node; no contact for 20 s +
  a technical → `Recon`; squad at 30% vs 2× enemy → `Retreat`; existing `StrategistBrainTests`
  unchanged.
- **Done when:** in a bot-only round the strategist retakes a denied node, a technical scouts, and a
  losing squad withdraws rather than dying in place.

### H5 — Unit resupply (0.5–1 day, only if D2 is adopted)

- `RegenPerSecond` on `UnitDefinition`, applied inside a friendly barracks' defended ring; tested in
  `DefenseTests` style.

### H6 — Scenarios, playtest, switch the default (1 day, then ongoing)

- New scenarios in `Tests/Scenarios/`, distributional as AGENT_API.md §3 requires, e.g.
  `htn_ground_denies_node.json` (strategist nodes contested for ≥ N ticks), `htn_strategist_defends_node.json`,
  `htn_recon_finds_contact.json`, `htn_squad_retreats.json` (units lost per engagement vs `legacy`).
  Metrics beyond today's vocabulary need events on `AgentEventBus` (e.g. a squad mission change),
  which is an agent-API change and is documented there when made.
- Playtest with people; compare rounds against `legacy` on the M8 columns (round length, outcome,
  units lost by tier, nodes held over time).
- **Done when:** the default flips to `htn`, and `docs/TRAINING.md` / `RL_ARCHITECTURE.md` say which
  bot a trained policy was evaluated against.

Total: about 10–12 days before playtesting.

---

## 7. Verification

| Claim | Proof |
|---|---|
| FluidHTN behaves as §3 says it does | `./scripts/htn-bench.sh --probe` today (§3.4, nine passing); `HtnPlannerTests` in `dotnet test` from H0 |
| The planner allocates nothing on the tick | P8, in both of the above; `./scripts/htn-bench.sh` for the per-tick figure |
| A given situation produces a given plan | Fact-vector → task-chain tests per domain (H2–H4); no engine needed |
| Legacy behaviour is intact | Existing `BotBrainTests`, `UnitBrainTests`, `StrategistBrainTests` unchanged; `--bot-ai legacy` default until H6 |
| The bots behave as described in a real round | Scenarios in H6, run by `./scripts/playtest.sh` |
| It costs nothing noticeable | Debug HUD server-frame-time row, `legacy` vs `htn`, 64 units |

---

## 8. Decisions needed

| # | Question | Recommendation |
|---|---|---|
| D1 | Should unit-level HTN act on units a **human** strategist ordered? | Yes for how an order is carried out (cohesion, focus fire, defend posts, answering calls); no for autonomous Retreat/Resupply, which only units last ordered by a bot get (`Stance`). The bot commander does not re-order a unit a human ordered in the last 30 s. |
| D2 | RTS units have nothing to resupply: magazines reload without a reserve and nothing heals. Add healing inside a friendly barracks' defended ring? | Yes — one `RegenPerSecond` per `UnitDefinition`. Without it, unit Resupply means reinforcement only, and unit Retreat saves a unit that can never recover. |
| D3 | May a ground bot know what a **teammate** has seen? | Yes, as a team `ContactMemory` — the equivalent of voice callouts, and less than a human client already draws (the unit snapshot is not fogged for the ground force, IMPLEMENTATION_PLAN.md §M4). Today a bot knows only its own eyes (`BotTraits.SensorRadiusMeters`). |
| D4 | May the computer strategist act on **ghosts** (last-known positions)? `BotStrategist.TryObjective` deliberately does not. | Yes, for Recon and for staging only, and never to name a target: a human strategist can click a ghost (M4), so the bot doing so is parity, not cheating. |
| D5 | The scripted bots are the RL baseline ("beats the bot" should stay true, RL_ARCHITECTURE.md). | Keep `legacy` selectable for ever; `scripts/evaluate.sh` and the trainer pin `--bot-ai legacy` until a deliberate re-baseline, and training output records which one was used. |
| D6 | FluidHTN drops a borrowed queue when a plan replaces a paused partial plan: 128 B per pre-emption, pooled or not (§3.4, P8). Needed only from H4, whose stage → strike is the one `PausePlan`. | Before H4: offer the one-line fix upstream; if it has not landed by then, patch the vendored copy with that line, record the patch in `ThirdParty/FluidHTN/README`, and flip the pinning test to assert 0 B. Accepting the allocation is the fallback: it is per event, not per tick. |

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
