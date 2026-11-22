// The red tunnel vision a hit on the local player draws (docs/HURT_FEEDBACK.md §3).
//
// A red edge that closes in from the rim of the screen, darker towards the rim. `intensity`
// is how far it has closed: the TunnelVision script (Scripts/Effects/TunnelVision.cs) writes
// it every frame from how grievous the hit was and how far the fade has got. Everything
// else here is the look, and is the artist's: open Effects/Hurt/TunnelVision.tscn, select
// the root, and change the uniforms under Material > Shader Parameters. Drag `intensity`
// there to see any depth in the editor; the game sets it back to 0 when it starts.
shader_type canvas_item;
render_mode unshaded;

// 0: nothing is drawn. 1: as deep as the deepest hit takes it.
uniform float intensity : hint_range(0.0, 1.0) = 0.0;

group_uniforms colour;
// The colour where the tunnel's edge begins.
uniform vec4 edge_color : source_color = vec4(0.8, 0.04, 0.04, 1.0);
// The colour at the rim of the screen, where it is darkest.
uniform vec4 rim_color : source_color = vec4(0.32, 0.0, 0.0, 1.0);
// How opaque the rim is once the edge has fully come in.
uniform float max_opacity : hint_range(0.0, 1.0) = 0.85;
// Below this intensity the whole edge fades in from nothing, so a light hit tints the
// corners rather than popping a ring onto the screen.
uniform float onset : hint_range(0.001, 1.0) = 0.2;
group_uniforms;

group_uniforms shape;
// Where the edge begins at the lightest and at the deepest, as a distance from the middle
// of the screen: 1 is the middle of its top and bottom edges.
uniform float radius_light : hint_range(0.0, 2.0) = 1.25;
uniform float radius_deep : hint_range(0.0, 2.0) = 0.35;
// How far the edge takes to go from clear to the rim's colour.
uniform float softness : hint_range(0.01, 2.0) = 0.6;
// 0: the tunnel is the screen's shape, an ellipse as wide as it is. 1: it is a circle.
uniform float roundness : hint_range(0.0, 1.0) = 0.6;
group_uniforms;

group_uniforms motion;
// A heartbeat in the edge: how far in it pushes at full intensity, and beats a second.
uniform float pulse_amount : hint_range(0.0, 0.5) = 0.06;
uniform float pulse_rate : hint_range(0.0, 4.0) = 1.3;
// A ragged edge: a noise texture wrapped round the rim, pushing the edge in where it is
// bright. Leave it empty for a clean edge.
uniform sampler2D edge_noise : filter_linear, repeat_enable, hint_default_black;
uniform float edge_noise_amount : hint_range(0.0, 0.5) = 0.12;
// How fast the noise drifts outwards, in texture widths a second.
uniform float edge_noise_drift : hint_range(0.0, 2.0) = 0.15;
group_uniforms;

void fragment() {
	// -1 to 1 across the rect, which fills the screen; widened towards a circle by roundness.
	vec2 p = (UV - 0.5) * 2.0;
	float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
	p.x *= mix(1.0, aspect, roundness);
	float dist = length(p);

	float edge = mix(radius_light, radius_deep, intensity);

	// A sharp beat and a slow recovery, rather than a sine's even swing.
	float beat = pow(0.5 + 0.5 * sin(TIME * pulse_rate * TAU), 4.0);
	edge -= beat * pulse_amount * intensity;

	vec2 around = vec2(atan(p.y, p.x) / TAU + 0.5, dist * 0.5 - TIME * edge_noise_drift);
	edge -= texture(edge_noise, around).r * edge_noise_amount;

	float t = smoothstep(edge, edge + softness, dist);
	vec4 color = mix(edge_color, rim_color, t);
	float alpha = t * max_opacity * smoothstep(0.0, onset, intensity);
	COLOR = vec4(color.rgb, color.a * alpha);
}
