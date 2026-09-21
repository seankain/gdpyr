"""A Gymnasium-shaped Python client for gdpyr's agent channel (docs/AGENT_API.md §9)."""

from .gdpyr_env import (  # noqa: F401
    AgentError,
    BUTTONS,
    GdpyrEnv,
    GroundAction,
    Observation,
    PROTOCOL_VERSION,
    SCHEMA_VERSION,
    Schema,
)

__all__ = [
    "AgentError",
    "BUTTONS",
    "GdpyrEnv",
    "GroundAction",
    "Observation",
    "PROTOCOL_VERSION",
    "SCHEMA_VERSION",
    "Schema",
]
