"""A Gymnasium-shaped client for gdpyr's agent channel (docs/AGENT_API.md).

One module, ``numpy`` and nothing else. The reference learner for this project is
.NET (``tools/Gdpyr.Trainer``, RLMatrix) because the rest of the repository is
C#; this exists because a great deal of RL tooling is Python, and the protocol
was designed to be speakable in twenty lines from anywhere. It is not a second
implementation of the game's types — the observation layout is *fetched* at the
handshake and decoded by name, so adding a float to the observation is a server
change and not a coordinated release.

    from gdpyr_env import GdpyrEnv

    env = GdpyrEnv(port=7900, policy="ground", step_mul=4, stepped=True)
    obs, info = env.reset(seed=7)
    for _ in range(1000):
        obs, reward, terminated, truncated, info = env.step(env.sample_action())
        if terminated or truncated:
            obs, info = env.reset()
    env.close()

**There is no reward function in the game, and there is none here.** ``reward``
is always ``0.0``; ``info["events"]`` carries the raw record — kills, damage,
units built and lost, node transitions, round outcomes — and what to value is the
trainer's business (docs/AGENT_API.md §8). A ``GroundReward`` that reads that
stream is twenty lines and belongs in your code, not in the environment.

The API is Gymnasium's shape without the dependency: ``reset`` returns
``(observation, info)`` and ``step`` returns
``(observation, reward, terminated, truncated, info)``. It deliberately does not
subclass ``gymnasium.Env``; if you want that, wrap it in four lines and keep this
module importable with nothing but numpy installed.
"""

from __future__ import annotations

import json
import socket
import struct
from dataclasses import dataclass, field
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

import numpy as np

__all__ = [
    "AgentError",
    "GdpyrEnv",
    "GroundAction",
    "PROTOCOL_VERSION",
    "SCHEMA_VERSION",
]

PROTOCOL_VERSION = 1

#: The observation layout this client knows how to decode. A ``welcome`` carrying
#: anything else is refused rather than misread — the whole point of fetching the
#: schema instead of compiling it (docs/AGENT_API.md §4).
SCHEMA_VERSION = "gdpyr-agent-obs-2"

_KIND_REQUEST = 1
_KIND_RESPONSE = 2
_KIND_OBSERVATION = 3
_KIND_EVENT = 4
_KIND_ERROR = 5

_HEADER = 5
_MAX_FRAME = 1 << 20

#: Buttons a ground policy may hold, matching ``InputButtons`` on the wire.
BUTTONS = {
    "jump": 1 << 0,
    "crouch": 1 << 1,
    "sprint": 1 << 2,
    "fire": 1 << 3,
    "ads": 1 << 4,
    "reload": 1 << 5,
    "use": 1 << 6,
    "melee": 1 << 7,
    "weapon1": 1 << 8,
    "weapon2": 1 << 9,
    "weapon3": 1 << 10,
}

_TWO_PI = 2.0 * np.pi
_HALF_PI = 0.5 * np.pi


class AgentError(RuntimeError):
    """The server refused something, or the channel went away."""


@dataclass
class GroundAction:
    """One decision for a ground seat (docs/AGENT_API.md §7.2).

    The action *is* an ``InputFrame`` — the same twelve bytes a human client
    sends. ``dyaw`` and ``dpitch`` are deltas in radians and are clamped
    server-side to the seat's turn-rate ceiling, which is on by default: a policy
    that snap-aims is not playing the game people play (§7.3).
    """

    move_x: float = 0.0
    move_z: float = 0.0
    dyaw: float = 0.0
    dpitch: float = 0.0
    buttons: int = 0

    def pack(self, seat: int, tick: int = 0) -> bytes:
        move_x = int(np.clip(round(self.move_x * 127.0), -127, 127))
        move_z = int(np.clip(round(self.move_z * 127.0), -127, 127))
        yaw = int(round((self.dyaw % _TWO_PI) * (65536.0 / _TWO_PI))) & 0xFFFF
        pitch = int(round(float(np.clip(self.dpitch, -_HALF_PI, _HALF_PI)) * (32767.0 / _HALF_PI)))
        return struct.pack(
            "<iHHIbbHhH", seat, 1, 0, tick, move_x, move_z, yaw, pitch, self.buttons & 0xFFFF
        )

    @staticmethod
    def held(*names: str) -> int:
        """The button word for a set of names: ``GroundAction.held("sprint", "fire")``."""
        word = 0
        for name in names:
            word |= BUTTONS.get(name, 0)
        return word


@dataclass
class Schema:
    """The observation layout, as ``welcome`` published it."""

    version: str = ""
    tick_rate: int = 60
    ground_floats: int = 0
    strategist_floats: int = 0
    plane_floats: int = 0
    plane_size: int = 0
    plane_channels: int = 0
    ground_fields: Dict[str, Tuple[int, int]] = field(default_factory=dict)
    strategist_fields: Dict[str, Tuple[int, int]] = field(default_factory=dict)

    def fields(self, policy: str) -> Dict[str, Tuple[int, int]]:
        return self.strategist_fields if policy == "strategist" else self.ground_fields

    def floats(self, policy: str) -> int:
        return self.strategist_floats if policy == "strategist" else self.ground_floats


@dataclass
class Observation:
    """One decoded observation frame."""

    seat: int
    tick: int
    policy: str
    values: np.ndarray
    planes: Optional[np.ndarray] = None
    omniscient: bool = False
    unbounded: bool = False


class GdpyrEnv:
    """A seat in a gdpyr round, driven over the agent channel.

    ``stepped=True`` is the mode for training and for regression runs: the
    simulation does not advance until every attached stepped seat has acted, which
    is what makes a loop synchronous instead of sleep-and-hope. It refuses to
    engage while a human peer is connected, because their clock would resync and
    the round would be unplayable (docs/AGENT_API.md §5.2).
    """

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 7900,
        token: Optional[str] = None,
        policy: str = "ground",
        step_mul: Optional[int] = None,
        stepped: bool = True,
        feature_planes: bool = False,
        episode_ticks: int = 3600,
        timeout: float = 30.0,
    ) -> None:
        if policy not in ("ground", "strategist"):
            raise ValueError("policy is 'ground' or 'strategist'")

        self.policy = policy
        self.step_mul = step_mul if step_mul else (30 if policy == "strategist" else 4)
        self.stepped = stepped
        self.feature_planes = feature_planes
        self.episode_ticks = episode_ticks

        self._timeout = timeout
        self._socket = socket.create_connection((host, port), timeout=timeout)
        self._socket.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self._buffer = bytearray()
        self._correlation = 1
        self._observations: List[Observation] = []
        self._events: List[Dict[str, Any]] = []

        self.schema = Schema()
        self.omniscient = False
        self.unbounded = False
        self.tick = 0
        self.seat = 0
        self._elapsed = 0

        self._hello(token)
        self._configure()
        self._attach()

    # ---- the gym shape ---------------------------------------------------

    @property
    def observation_size(self) -> int:
        """Floats in one observation, planes excluded."""
        return self.schema.floats(self.policy)

    def reset(self, seed: Optional[int] = None) -> Tuple[np.ndarray, Dict[str, Any]]:
        """Ends the round now and starts a fresh one.

        The ``seed`` **labels** the episode and is echoed on ``round_start``; it
        does not determine it. Every stochastic decision in the game is a hash of
        the absolute server tick, which never rewinds, so two episodes never share
        a starting state (docs/AGENT_API.md §3.1). Write claims about
        distributions, not about trajectories.
        """
        self._request({"op": "reset", "seed": int(seed or 0)})
        self._observations.clear()
        self._events.clear()
        self._elapsed = 0

        observation = self._advance(GroundAction())
        return observation.values, {"tick": observation.tick, "seed": seed, "events": []}

    def step(self, action: Any) -> Tuple[np.ndarray, float, bool, bool, Dict[str, Any]]:
        """Submits one action and advances the seat's ``step_mul`` ticks.

        ``reward`` is always ``0.0``: the game emits events and no rewards
        (docs/AGENT_API.md §8).
        """
        observation = self._advance(action)
        self._elapsed += self.step_mul

        events = list(self._events)
        self._events.clear()

        terminated = any(record.get("kind") == "round_end" for record in events)
        truncated = self._elapsed >= self.episode_ticks and not terminated

        info: Dict[str, Any] = {
            "tick": observation.tick,
            "seat": observation.seat,
            "events": events,
            "omniscient": self.omniscient,
            "unbounded": self.unbounded,
        }

        if observation.planes is not None:
            info["planes"] = observation.planes

        return observation.values, 0.0, terminated, truncated, info

    def close(self) -> None:
        """Releases the seat back to its bot and drops the socket."""
        try:
            self._send(_KIND_REQUEST, 0, {"op": "quit"})
        except OSError:
            pass
        finally:
            self._socket.close()

    def __enter__(self) -> "GdpyrEnv":
        return self

    def __exit__(self, *_: Any) -> None:
        self.close()

    # ---- reading an observation by name ----------------------------------

    def field(self, values: np.ndarray, name: str) -> np.ndarray:
        """A named run of the vector, by the schema's own field name."""
        offset, count = self.schema.fields(self.policy).get(name, (-1, 0))
        return values[offset : offset + count] if offset >= 0 else np.empty(0, dtype=np.float32)

    def scalar(self, values: np.ndarray, name: str) -> float:
        run = self.field(values, name)
        return float(run[0]) if run.size else 0.0

    def list_units(self) -> List[Dict[str, Any]]:
        """The units this strategist seat commands, in the order the observation carries them.

        The strategist vector carries a unit's tier, position, health and order and
        no id, and an ``order`` command names ids (``AGENT_API.md`` §6.2, §7.4). This
        is the call that joins the two: entry *i* here is unit *i* of the
        observation's unit block, and its ``id`` is what an order names::

            army = env.list_units()
            env.step([{"cmd": "order", "kind": "attack",
                       "units": [u["id"] for u in army if u["alive"]],
                       "target": [42.0, -8.5]}])

        One round trip per decision, which at a strategist's 2 Hz is nothing.
        """
        if self.policy != "strategist":
            raise AgentError("a ground seat commands no units")

        response = self._request({"op": "list_units", "seat": self.seat})
        return list(response.get("units", []))

    def sample_action(self) -> GroundAction:
        """A uniformly silly ground action. For smoke tests, not for training."""
        return GroundAction(
            move_x=float(np.random.uniform(-1.0, 1.0)),
            move_z=float(np.random.uniform(-1.0, 1.0)),
            dyaw=float(np.random.uniform(-0.05, 0.05)),
            dpitch=float(np.random.uniform(-0.02, 0.02)),
            buttons=0,
        )

    # ---- the control plane -----------------------------------------------

    def _hello(self, token: Optional[str]) -> None:
        request: Dict[str, Any] = {
            "op": "hello",
            "protocol": PROTOCOL_VERSION,
            "observations": "binary",
        }
        if token:
            request["token"] = token

        welcome = self._request(request)
        schema = welcome["schema"]
        version = schema.get("schema_version", "")
        if version != SCHEMA_VERSION:
            raise AgentError(
                f"server publishes observation schema '{version}'; this client speaks"
                f" '{SCHEMA_VERSION}'. Update tools/gdpyr_env to the server's gdpyr."
            )

        self.omniscient = bool(welcome.get("omniscient", False))
        self.unbounded = bool(welcome.get("unbounded", False))
        self.tick = int(welcome.get("tick", 0))

        read = Schema(version=version, tick_rate=int(welcome.get("tick_rate", 60)))

        ground = schema["ground_observation"]
        read.ground_floats = int(ground["floats"])
        for entry in ground["fields"]:
            read.ground_fields[entry["name"]] = (int(entry["offset"]), int(entry["count"]))

        strategist = schema.get("strategist_observation")
        if strategist:
            read.strategist_floats = int(strategist["floats"])
            for entry in strategist["fields"]:
                read.strategist_fields[entry["name"]] = (int(entry["offset"]), int(entry["count"]))

        planes = schema.get("feature_planes")
        if planes:
            read.plane_floats = int(planes["floats"])
            read.plane_size = int(planes["size"])
            read.plane_channels = int(planes["channels"])

        self.schema = read

    def _configure(self) -> None:
        self._request(
            {
                "op": "config",
                "mode": "stepped" if self.stepped else "realtime",
                "feature_planes": bool(self.feature_planes),
            }
        )

    def _attach(self) -> None:
        response = self._request(
            {
                "op": "attach",
                "team": "strategist" if self.policy == "strategist" else "ground",
                "policy": self.policy,
                "step_mul": self.step_mul,
            }
        )
        self.seat = int(response["seat"])

    def _advance(self, action: Any) -> Observation:
        self._act(action)

        if self.stepped:
            response = self._request({"op": "step", "n": self.step_mul})
            self.tick = int(response.get("tick", self.tick))
        else:
            self._drain(block=False)

        for _ in range(64):
            for observation in self._observations:
                if observation.seat == self.seat:
                    self._observations.clear()
                    return observation

            self._observations.clear()
            self._drain(block=True)

        raise AgentError("the server pushed no observation for this seat")

    def _act(self, action: Any) -> None:
        if self.policy == "strategist":
            commands = action if isinstance(action, Sequence) else []
            self._send(
                _KIND_REQUEST,
                0,
                {"op": "act", "seat": self.seat, "tick": 0, "action": {"commands": list(commands)}},
            )
            return

        if isinstance(action, GroundAction):
            packed = action
        elif isinstance(action, dict):
            packed = GroundAction(**action)
        elif isinstance(action, (list, tuple, np.ndarray)):
            values = list(action) + [0] * (5 - len(action))
            packed = GroundAction(
                move_x=float(values[0]),
                move_z=float(values[1]),
                dyaw=float(values[2]),
                dpitch=float(values[3]),
                buttons=int(values[4]),
            )
        else:
            raise TypeError("a ground action is a GroundAction, a dict or a 5-sequence")

        self._write(_KIND_OBSERVATION, 0, packed.pack(self.seat))

    # ---- framing ---------------------------------------------------------

    def _request(self, body: Dict[str, Any]) -> Dict[str, Any]:
        self._correlation += 1
        correlation = self._correlation
        self._send(_KIND_REQUEST, correlation, body)

        while True:
            kind, echoed, payload = self._read_frame()
            if echoed == correlation and kind == _KIND_RESPONSE:
                return json.loads(payload)
            if echoed == correlation and kind == _KIND_ERROR:
                raise AgentError(_error(payload))
            self._park(kind, payload)

    def _send(self, kind: int, correlation: int, body: Dict[str, Any]) -> None:
        self._write(kind, correlation, json.dumps(body).encode("utf-8"))

    def _write(self, kind: int, correlation: int, body: bytes) -> None:
        frame = struct.pack("<IBI", _HEADER + len(body), kind, correlation) + body
        self._socket.sendall(frame)

    def _drain(self, block: bool) -> None:
        if not block:
            # settimeout(0) rather than setblocking(False): setblocking(True) would
            # restore *no* timeout rather than the one this connection was opened
            # with, and a socket that blocks forever is how a training loop hangs
            # without saying so.
            self._socket.settimeout(0.0)
            try:
                self._fill()
            except (BlockingIOError, OSError):
                pass
            finally:
                self._socket.settimeout(self._timeout)

        while True:
            frame = self._try_decode()
            if frame is None:
                if not block:
                    return
                self._fill()
                continue

            kind, _, payload = frame
            self._park(kind, payload)
            block = False

    def _park(self, kind: int, payload: bytes) -> None:
        if kind == _KIND_OBSERVATION:
            self._observations.append(self._decode(payload))
        elif kind == _KIND_EVENT:
            body = json.loads(payload)
            self._events.extend(body.get("events", []))
        elif kind == _KIND_ERROR:
            # An unsolicited error is the server saying something went wrong outside
            # a request — a step timeout, or a human connecting into a stepped round.
            # Both change what the episode means, so neither is swallowed.
            raise AgentError(_error(payload))

    def _read_frame(self) -> Tuple[int, int, bytes]:
        while True:
            frame = self._try_decode()
            if frame is not None:
                return frame
            self._fill()

    def _fill(self) -> None:
        chunk = self._socket.recv(65536)
        if not chunk:
            raise AgentError("the gdpyr agent channel closed")
        self._buffer.extend(chunk)

    def _try_decode(self) -> Optional[Tuple[int, int, bytes]]:
        if len(self._buffer) < 4:
            return None

        length = struct.unpack_from("<I", self._buffer, 0)[0]
        if length < _HEADER or length > _MAX_FRAME:
            raise AgentError("the gdpyr agent channel sent an undecodable frame")

        total = 4 + length
        if len(self._buffer) < total:
            return None

        kind = self._buffer[4]
        correlation = struct.unpack_from("<I", self._buffer, 5)[0]
        payload = bytes(self._buffer[9:total])
        del self._buffer[:total]
        return kind, correlation, payload

    def _decode(self, payload: bytes) -> Observation:
        seat, flags, _, tick = struct.unpack_from("<iHHI", payload, 0)
        floats = np.frombuffer(payload, dtype="<f4", offset=12)

        planes_attached = bool(flags & 4)
        plane_floats = self.schema.plane_floats if planes_attached else 0
        vector = floats.size - plane_floats

        if vector == self.schema.ground_floats:
            policy = "ground"
        elif self.schema.strategist_floats and vector == self.schema.strategist_floats:
            policy = "strategist"
        else:
            raise AgentError(
                f"observation carries {vector} floats; the schema publishes"
                f" {self.schema.ground_floats} for a ground seat and"
                f" {self.schema.strategist_floats} for a strategist"
            )

        grid = None
        if plane_floats:
            grid = floats[vector:].reshape(
                self.schema.plane_size, self.schema.plane_size, self.schema.plane_channels
            )

        self.tick = int(tick)
        return Observation(
            seat=int(seat),
            tick=int(tick),
            policy=policy,
            values=np.array(floats[:vector], dtype=np.float32),
            planes=grid,
            omniscient=bool(flags & 1),
            unbounded=bool(flags & 2),
        )


def _error(payload: bytes) -> str:
    try:
        body = json.loads(payload)
        return f"{body.get('code', 'error')}: {body.get('message', '')}"
    except (ValueError, TypeError):
        return payload.decode("utf-8", errors="replace")
