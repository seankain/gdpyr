# Hurt feedback

What a ground-force player sees when they are hit: a small shake of the camera, and a red tunnel
vision that closes in from the edge of the screen and fades again. Both are deeper the more
grievous the hit. Presentation only: nothing here feeds the simulation or goes on the wire, and
a peer that drew none of it would still agree with the server about every hit.

The look is built from Godot resources, so an artist changes it in the editor without touching
code:

| Part | File | What it holds |
|---|---|---|
| camera shake | `Effects/Hurt/CameraShake.tscn` | a `Curve` (severity to trauma), a `FastNoiseLite`, and the exported reach, speed and settle rate on the root node |
| tunnel vision | `Effects/Hurt/TunnelVision.tscn` | a `ShaderMaterial` on a full-screen `ColorRect`, two `Curve`s (severity to depth, the fade over time), and the exported durations and stacking on the root node |
| tunnel shader | `Effects/Hurt/tunnel_vision.gdshader` | colours, shape, heartbeat and ragged edge, as shader uniforms |

The player's scene, `Scenes/fps_controller.tscn`, instances both:
`CameraController/Camera3D/CameraShake` and `UserInterface/TunnelVision`. The root's *Hurt
feedback* exports, `HurtShake` and `HurtVision`, point at them.

The code that decides what happened is engine-free: `Scripts/Sim/HurtFeedback.cs`, tested by
`Tests/HurtFeedbackTests.cs`. The code that draws it is `Scripts/Effects/CameraShake.cs` and
`Scripts/Effects/TunnelVision.cs`.

## 1. How grievous a hit is

`HurtFeedback.Severity` turns a hit into one number from 0 to 1, and both effects read their
curves at that number:

- the health the hit took, as a share of a full life (100): a rifle round 0.20, the heavy machine
  gun 0.45, a DMR round 0.60;
- plus the vest's points it took, counted at 0.35 each (`HurtFeedback.VestWeight`). A vest makes
  a hit feel lighter, and a hit the vest mostly soaked is still felt;
- a killing blow is 1, however little it took.

Several hits in one player record, which happens when rounds land close together, are felt as
one hit as grievous as all of them.

## 2. Camera shake

`CameraShake` keeps **trauma** from 0 to 1. A hit adds `TraumaBySeverity(severity)`. Trauma drains
at `DecayPerSecond`. The camera is shaken by trauma to the power `Exponent`, so a light hit is a
twitch and a heavy hit is a jolt. In each axis the shake follows `Noise`, read at `NoiseSpeed`:

| Export | Default | Effect |
|---|---|---|
| `TraumaBySeverity` | 0.3 at severity 0, rising to 1 at severity 1 | how much trauma a hit adds |
| `DecayPerSecond` | 2 | full trauma settles in half a second |
| `Exponent` | 2 | 1 is linear; higher values separate light hits from heavy ones more |
| `Noise` | simplex smooth, frequency 0.5, no fractal | the wander in each axis |
| `NoiseSpeed` | 22 | faster reads give a buzz, slower reads a sway |
| `MaxAngleDegrees` | pitch 1.5°, yaw 1.5°, roll 4° | how far the camera turns at full shake |
| `MaxOffset` | 0.04 m across and up | how far the picture slides at full shake |

With the defaults, a rifle round adds 0.44 trauma, which is under a degree for about a quarter
second. A killing blow adds full trauma; the engine check measured it turning the camera up to
2.5° and sliding the picture up to 0.037 m, over 28 frames at 60 fps.

**The shake moves only what is drawn.** A shot leaves from the camera's position
(`fps_controller.EyePosition`) along the look's yaw and pitch (`fps_controller.AimDirection`).
The shake turns the camera about its own position, which moves neither. It slides the picture
with `Camera3D.HOffset` and `VOffset`, which move the projection and not the node. The look writes
the camera's rotation on every simulated tick; on every render frame the shake is applied on top
of whatever the look last wrote. When the shake is over, the look's own rotation is restored
exactly. The reticle stays in the middle of the screen, so for the fraction of a second the camera
is turned, the reticle is a little off from where a round would go. That is the flinch.

## 3. Tunnel vision

`TunnelVision` draws one `ColorRect` that covers the screen with `tunnel_vision.gdshader`. The
script writes a single uniform, `intensity` (0 to 1), and hides the rect when `intensity` is 0, so
the effect costs nothing between hits.

A hit sets the tunnel's peak to `DepthBySeverity(severity)`. If the tunnel is still showing from
an earlier hit, the new peak is the deeper of the two plus `Stacking` of the shallower, up to 1
(`HurtFeedback.Stack`). Each hit restarts the fade, which lasts from `ShortestSeconds` for the
lightest peak to `LongestSeconds` for the deepest. While the fade runs, `intensity` is the peak
times `Envelope`, sampled at how far through the fade it is.

| Export (node) | Default | Effect |
|---|---|---|
| `DepthBySeverity` | 0.1 at severity 0, 0.42 at 0.25, 0.8 at 0.6, 1 at 1 | how deep a hit takes it |
| `Stacking` | 0.5 | 0: a hit only replaces a shallower tunnel; 1: overlapping hits add |
| `Envelope` | holds near full for the first 30 %, then eases out to 0 | the shape of the fade; it should end at 0 |
| `ShortestSeconds` / `LongestSeconds` | 0.8 / 3 s | how long the lightest and the deepest last |

| Uniform (material) | Default | Effect |
|---|---|---|
| `edge_color`, `rim_color` | red, dark red | the colour where the edge starts, and at the rim of the screen |
| `max_opacity` | 0.85 | how opaque the rim gets |
| `onset` | 0.2 | below this intensity the whole edge fades in from nothing, so a light hit tints the corners rather than popping a ring onto the screen |
| `radius_light`, `radius_deep` | 1.25, 0.35 | where the edge starts at the lightest and the deepest; 1 is the middle of the screen's top and bottom edges |
| `softness` | 0.6 | how far the edge takes to go from clear to the rim's colour |
| `roundness` | 0.6 | 0 makes the tunnel the screen's shape; 1 makes it a circle |
| `pulse_amount`, `pulse_rate` | 0.06, 1.3 beats/s | a heartbeat in the edge, scaled by intensity |
| `edge_noise`, `edge_noise_amount`, `edge_noise_drift` | a seamless `NoiseTexture2D`, 0.12, 0.15 | a ragged, drifting edge; clear the texture for a clean edge |

To see the look in the editor, open `TunnelVision.tscn`, select the root, and drag
`Material > Shader Parameters > intensity`. The game resets it to 0 when the scene starts.

In the player's `UserInterface` the tunnel comes after the reticle and the scope's eyepiece, so it
is drawn over them, and before the debug panel and the pause menu, so it is drawn under them. The
combat HUD's labels are on a canvas layer above all of it. Even at the deepest, the middle of the
screen, where the reticle is, stays clear.

## 4. When it is drawn

Only for the local player, and only by a process that draws. A dedicated server draws nothing.
`fps_controller.PresentHurt(severity)` runs both effects. It is called by the combat manager:

| Process | Where | What it reads |
|---|---|---|
| a listen host or an offline round | `CombatManager.Damage`, for the local player | the health and vest points the hit took, and whether it killed |
| a client | `CombatManager.ApplySnapshot`, for its own player record | a fall in health, or in the same vest's points, in a life that was going on, the killing one included |

On a client the feedback arrives with the player record, one network trip after the hit, the
same time the health number changes. A ground-force player whose health goes to 0 because the
server took them off the field (the role menu at a round's start, or the strategist's chair) was
not hit, and nothing is drawn.

A killing blow's tunnel is left to fade over the death screen. A respawn clears both effects at
once (`fps_controller.SetDeadPresentation`), and so does leaving the field for the role menu or
the strategist's chair.

## 5. Checking it

- `hurt <severity 0-1>` on the `~` console draws both effects as a hit of that severity would
  (0.2 a rifle round, 0.6 a DMR round, 1 a killing blow) and reports what they reached:
  `hurt 0.60: tunnel 0.80 for 2.6 s, shake 0.72 trauma`. `hurt clear` removes both. Use it to
  tune the scenes: change a curve or a uniform, run the game, type `hurt 0.6`.
- `dotnet test --filter HurtFeedbackTests`: severity, stacking, durations, the fade's timing and
  the shake's trauma.
- `./scripts/hurt-feedback-checks.sh` (a Godot 4.6 .NET binary on `PATH` or in `$GODOT`):
  - `listen`, a listen host's own player: the effects are the scenes in `Effects/Hurt/`, wired in
    with their curves, noise and shader; each severity reaches the depth and trauma its curves
    give, written into the shader's `intensity`, deeper, harder and longer the more grievous; the
    shake turns the drawn camera and slides its picture within the exported reach and does not
    move where a shot leaves from or where it goes; once settled, the camera's rotation is the
    look's again exactly; the tunnel only fades once a hit has landed and is gone in its time; two
    hits stack; real rifle and DMR rounds on the player reach the depth their damage gives, a
    killing blow the deepest, still fading over the death screen and cleared by the respawn; a
    rifle round on a medium vest is felt lighter than on none; and the console's `hurt`.
  - `net`, a dedicated server and a client: the server fires a rifle round and then DMR rounds at
    the client's player until one kills; the client draws each from its player record, as
    grievous as the record says.
  - With `SHOTS=<dir>`, the listen part runs on a display (`$DISPLAY`, or `xvfb-run`) and saves
    the view with no hit and at the top of a 0.2, a 0.6 and a 1.0 hit.

## 6. Not done

- No shake or tunnel for a player in a crew vehicle's seat being thrown about, or for explosions
  that miss. `CameraShake.AddTrauma` takes any amount and is the place to start; the explosion
  effects ([`EFFECTS.md`](EFFECTS.md)) do not call it.
- No direction. The tunnel closes in evenly from all sides; it does not say where the round came
  from.
- No sound for being hit.
- The strategist's units have no feedback of this kind; there is no camera inside them.
- Demo playback ([`DEMOS.md`](DEMOS.md)) applies player records through the client's path, so it
  may draw the recorded player's hits; not checked.
