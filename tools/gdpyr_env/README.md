# `gdpyr_env` — a Gymnasium-shaped Python client

One module, `numpy` and nothing else. The protocol is documented in
[`docs/AGENT_API.md`](../../docs/AGENT_API.md); this is what it looks like from Python.

```bash
godot --headless --path . -- --server 7777 --agent-api 127.0.0.1:7900 --bots 6:1
PYTHONPATH=tools python -c "
from gdpyr_env import GdpyrEnv, GroundAction
with GdpyrEnv(port=7900, policy='ground', step_mul=4) as env:
    obs, info = env.reset(seed=7)
    for _ in range(600):
        obs, reward, terminated, truncated, info = env.step(
            GroundAction(move_z=-1.0, buttons=GroundAction.held('sprint')))
        if terminated or truncated:
            break
    print('health', env.scalar(obs, 'self.health'), 'tick', env.tick)
"
```

**The reference learner for this project is .NET**, not Python
([`docs/TRAINING.md`](../../docs/TRAINING.md)): the rest of the repository is C#, and a training
loop in a second language means a second toolchain, a second set of types for `InputFrame` and a
second place for the observation layout to be wrong. This module exists because a great deal of RL
tooling is Python and the protocol was designed to be speakable from anywhere — not because the
observation layout lives in two places. It does not: the schema is **fetched** at the handshake and
decoded by name, and a `schema_version` this module does not recognise is refused rather than
misread.

Three things worth knowing before you train against it:

- **There is no reward.** `reward` is always `0.0`. `info["events"]` carries the raw record — kills,
  damage, units built and lost, node transitions, round outcomes — and what to value is your
  business (`AGENT_API.md` §8).
- **`seed` labels an episode; it does not determine one.** Every stochastic decision in the game is
  a hash of the absolute server tick, which never rewinds, so two episodes never share a starting
  state (§3.1). Claims about this environment are distributional.
- **The fairness ceilings are on by default.** An attached seat is clamped server-side to a bot's
  turn rate, and a strategist to eight commands a second. `--agent-unbounded` lifts them, is stamped
  into every observation, and is for research runs rather than for playtests (§7.3).

A strategist seat takes a list of commands rather than an `InputFrame`:

```python
with GdpyrEnv(port=7900, policy="strategist", step_mul=30) as env:
    obs, info = env.reset(seed=3)
    obs, reward, terminated, truncated, info = env.step([
        {"cmd": "build", "barracks": 0, "tier": 0},
        {"cmd": "order", "kind": "attack", "units": [1, 2], "target": [42.0, -8.5]},
    ])
```

Pass `feature_planes=True` for the optional `32 × 32 × 4` grid, which arrives in
`info["planes"]` as an `(N, N, C)` array (§6.3). It is a strategist affordance: a ground policy's
spatial signal is the ray fan in its own vector.
