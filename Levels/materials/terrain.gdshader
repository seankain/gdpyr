// A data map's ground (docs/LEVEL_EDITOR.md §7.2). The band is computed, never painted
// (LE-D7): u from the fragment's height over the water and its slope, as TerrainBands.U
// computes it in C#, and two neighbouring bands blended across `blend` metres of u, centred
// on their edge. Sand, mud and grass are sampled by world x and z; stone, which is what
// cliffs wear, on three planes. A road is mixed in by the vertex colour's red, its coverage
// (LE6's; zero until then). Five sampler2D: nothing the Compatibility renderer lacks (§3).
//
// The two need not agree to the bit: nothing but the picture reads this.
shader_type spatial;

uniform sampler2D sand_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D mud_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D grass_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D stone_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D road_texture : source_color, filter_linear_mipmap, repeat_enable;

// Metres one repeat of each covers: sand, mud, grass, stone.
uniform vec4 texture_meters = vec4(4.0, 4.0, 4.0, 4.0);
uniform float road_meters = 4.0;

// §6.4's seven numbers, the map's.
uniform float water_level = -5.0;
uniform float slope_free = 15.0;
uniform float slope_gain = 0.6;
uniform float sand_top = 1.5;
uniform float mud_top = 4.0;
uniform float grass_top = 22.0;
uniform float blend = 1.0;

// The editor's overlays (§8.3, LE4): 0 none, 1 the bands flat-coloured, 2 the slope of each
// face — green under 35°, yellow to 40°, red past it (LE-D23) — and 3 contours every 2 m.
uniform int overlay = 0;

varying vec3 world_position;
varying vec3 world_normal;

void vertex() {
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

// How far past an edge u is, across the blend: 0 a half-blend under it, 1 a half-blend over.
float past(float u, float edge) {
	return clamp((u - edge) / max(blend, 0.001) + 0.5, 0.0, 1.0);
}

vec3 flat_sample(sampler2D tex, float meters) {
	return texture(tex, world_position.xz / meters).rgb;
}

vec3 triplanar(sampler2D tex, float meters, vec3 normal) {
	vec3 weights = pow(abs(normal), vec3(4.0));
	weights /= max(weights.x + weights.y + weights.z, 0.0001);
	vec3 x = texture(tex, world_position.zy / meters).rgb;
	vec3 y = texture(tex, world_position.xz / meters).rgb;
	vec3 z = texture(tex, world_position.xy / meters).rgb;
	return x * weights.x + y * weights.y + z * weights.z;
}

void fragment() {
	vec3 normal = normalize(world_normal);
	float slope = degrees(acos(clamp(normal.y, -1.0, 1.0)));
	float above = world_position.y - water_level;
	float u = above + slope_gain * max(0.0, slope - slope_free);

	float to_mud = past(u, sand_top);
	float to_grass = past(u, mud_top);
	float to_stone = past(u, grass_top);

	vec3 sand = flat_sample(sand_texture, texture_meters.x);
	vec3 colour = mix(sand, flat_sample(mud_texture, texture_meters.y), to_mud);
	colour = mix(colour, flat_sample(grass_texture, texture_meters.z), to_grass);
	colour = mix(colour, triplanar(stone_texture, texture_meters.w, normal), to_stone);

	// Under the water: its bed, darker; the water's own surface is drawn over it.
	if (above < 0.0) {
		colour = sand * 0.55;
	}

	colour = mix(colour, flat_sample(road_texture, road_meters), clamp(COLOR.r, 0.0, 1.0));

	if (overlay == 1) {
		vec3 band = above < 0.0 ? vec3(0.15, 0.3, 0.85)
			: u < sand_top ? vec3(0.9, 0.82, 0.4)
			: u < mud_top ? vec3(0.65, 0.4, 0.2)
			: u < grass_top ? vec3(0.3, 0.65, 0.25)
			: vec3(0.55, 0.55, 0.55);
		colour = band;
	} else if (overlay == 2) {
		vec3 face = normalize(cross(dFdy(world_position), dFdx(world_position)));
		float face_slope = degrees(acos(clamp(abs(face.y), 0.0, 1.0)));
		vec3 tint = face_slope < 35.0 ? vec3(0.2, 0.75, 0.2) : face_slope <= 40.0 ? vec3(0.9, 0.85, 0.15) : vec3(0.9, 0.15, 0.1);
		colour = mix(colour, tint, 0.65);
	} else if (overlay == 3) {
		float line = abs(fract(world_position.y / 2.0 + 0.5) - 0.5) / max(fwidth(world_position.y / 2.0), 0.0001);
		colour = mix(vec3(0.05), colour, clamp(line - 0.5, 0.0, 1.0));
	}

	ALBEDO = colour;
	ROUGHNESS = 0.95;
}
