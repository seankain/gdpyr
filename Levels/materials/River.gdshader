shader_type spatial;
render_mode depth_draw_always, cull_disabled;

group_uniforms water;
uniform vec3 water_color : source_color;
uniform sampler2D water_noise_1;
uniform sampler2D water_noise_2;

group_uniforms vertex_waves;
uniform bool use_vertex_waves = false;
uniform float wave_height : hint_range(0.0, 2.0, 0.1) = 0.2;
uniform float wave_speed : hint_range(0.0, 1.0, 0.01) = 0.05;
uniform float wave_scale : hint_range(1.0, 50.0, 1.0) = 10.0;

group_uniforms rendering;
uniform bool double_sided = true;
uniform vec4 surface_bottom : source_color = vec4(0.29, 0.53, 0.67, 0.65);

group_uniforms depth;
uniform sampler2D depth_texture : hint_depth_texture;
uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap, repeat_disable;
uniform float depth_distance : hint_range(0.0, 10.0, 0.1) = 0.5;
uniform float water_color_ratio : hint_range(0.0, 1.0, 0.1) = 0.5;
uniform float beers_law : hint_range(0.0, 20.0, 0.1) = 3.0;

group_uniforms surface;
uniform float normal_scale : hint_range(0.0, 1.0, 0.1) = 0.5;
uniform float roughness_scale : hint_range(0.0, 1.0, 0.1) = 0.2;

group_uniforms river_flow;
uniform bool use_river_flow = false;
uniform float flow_speed : hint_range(0.0, 10.0, 0.1) = 1.0;
uniform float flow_direction_multiplier : hint_range(-1.0, 1.0, 0.1) = 1.0;
uniform vec2 uv1_scale = vec2(50.0, 1.0);
uniform vec2 uv1_offset = vec2(0.0, 0.0);
uniform bool debug_tangent = false;

group_uniforms foam;
uniform bool use_foam = false;
uniform bool debug_foam = false;
uniform sampler2D foam_texture : repeat_enable;
uniform float foam_uv_scale : hint_range(0.001, 1.0, 0.001) = 0.01;
uniform float foam_scale : hint_range(1.0, 100.0, 1.0) = 25.0;
uniform float foam_speed : hint_range(0.0, 2.0, 0.1) = 0.4;
uniform float foam_falloff_distance : hint_range(0.0, 2.0, 0.1) = 0.4;
uniform float foam_edge_distance : hint_range(0.0, 1.0, 0.1) = 0.2;
uniform float foam_edge_bias : hint_range(0.0, 1.0, 0.1) = 0.1;
uniform vec3 foam_color : source_color = vec3(0.8, 0.8, 0.8);

group_uniforms camera;
uniform float camera_near = 0.05;
uniform float camera_far = 200.0;

varying vec4 world_uv;
varying vec2 flow_direction;
varying flat vec2 TANGENT_DEBUG;
varying vec3 world_pos;

float edge(float near, float far, float depth){
	depth = 1.0 - 2.0 * depth;
	return near * far / (far + depth * (near - far));
}

void vertex() {
	world_uv = MODEL_MATRIX * vec4(VERTEX, 1.0);
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;

	if (use_vertex_waves) {
		vec2 wave_uv = world_pos.xz / wave_scale;
		wave_uv.x += TIME * wave_speed;
		wave_uv.y += TIME * wave_speed * 0.5;
		wave_uv = mod(wave_uv, 1.0);

		float noise1 = texture(water_noise_1, wave_uv).r;
		float noise2 = texture(water_noise_2, wave_uv * 0.5 + vec2(TIME * wave_speed * 0.3, 0.0)).r;
		noise2 = mod(noise2, 1.0);

		float wave_offset = (noise1 + noise2) * 0.5 - 0.5;
		VERTEX.y += wave_offset * wave_height;
	}

	if (use_river_flow) {
		flow_direction = vec2(1.0, 0.0);
		if (debug_tangent) {
			TANGENT_DEBUG = normalize(TANGENT.xz);
		} else {
			TANGENT_DEBUG = vec2(0.0);
		}
	} else {
		flow_direction = vec2(0.0, 1.0);
		TANGENT_DEBUG = vec2(0.0);
	}
}

void fragment() {
	if (!double_sided && !FRONT_FACING) {
		discard;
	}

	vec2 _uv;

	if (debug_tangent && use_river_flow) {
		vec2 tangent_dir = TANGENT_DEBUG;
		ALBEDO = vec3(tangent_dir.x * 0.5 + 0.5, 0.0, tangent_dir.y * 0.5 + 0.5);
		ALPHA = 1.0;
	} else {
	 
	if (use_river_flow) { 
		vec2 flow_offset = TIME * flow_speed * flow_direction * flow_direction_multiplier / uv1_scale.x;
		_uv = (UV + flow_offset) * uv1_scale + uv1_offset;
	} else { 
		_uv = world_uv.xz;
	}
	
	vec2 _suv = SCREEN_UV;

	float scaled_time = use_river_flow ? TIME * flow_speed / uv1_scale.x : TIME;
	float spatial_freq = use_river_flow ? 25.0 / uv1_scale.x : 25.0;
	float distortion_x = sin(scaled_time + (_uv.x + _uv.y) * spatial_freq) * 0.01;
	float distortion_y = cos(scaled_time + (_uv.x - _uv.y) * spatial_freq) * 0.01;
	
	_uv.x += distortion_x;
	_uv.y += distortion_y;
	
	_suv.x += distortion_x;
	_suv.y += distortion_y;
	
	float depth_r = textureLod(depth_texture, SCREEN_UV, 0.0).r;
	vec4 world = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth_r, 1.0);
	world.xyz /= world.w;

	float depth_blend = 1.0 - smoothstep(world.z + depth_distance, world.z, VERTEX.z);
	depth_blend = exp(depth_blend * -beers_law);
	depth_blend = clamp(pow(depth_blend, 3.0), 0.0, 1.0);

	vec3 refraction = textureLod(screen_texture, _suv, 0.0).rgb;
	NORMAL_MAP = mix(texture(water_noise_1, _uv).rgb, texture(water_noise_2, _uv).rgb, (sin(TIME * flow_speed) + 1.0) / 2.0);

	if (FRONT_FACING) {
		ALBEDO = mix(refraction * depth_blend, water_color, water_color_ratio);
		NORMAL *= normal_scale;
	} else {
		NORMAL = -NORMAL;
		NORMAL_MAP = mix(vec3(1.0), vec3(0.0), NORMAL_MAP);
		ALBEDO = mix(refraction, surface_bottom.rgb, surface_bottom.a);
		NORMAL *= normal_scale;
	}

	ROUGHNESS = roughness_scale;

	if (use_foam) {
		float z_depth = edge(camera_near, camera_far, textureLod(depth_texture, SCREEN_UV, 0.0).r);
		float z_pos = edge(camera_near, camera_far, FRAGCOORD.z);
		float waterDepth = z_depth - z_pos;
		waterDepth = max(waterDepth, 0.0);

		float edgePatternScroll = TIME * foam_speed;

		vec2 world_uv_foam = world_pos.xz * foam_uv_scale;
		vec2 scaledUV = world_uv_foam * foam_scale;

		float falloff = 1.0 - (waterDepth / foam_falloff_distance) + foam_edge_bias;
		float channelA = texture(foam_texture, scaledUV - vec2(edgePatternScroll, cos(scaledUV.x))).r;
		float channelB = texture(foam_texture, scaledUV * 0.5 + vec2(sin(scaledUV.y), edgePatternScroll)).b;

		float mask = (channelA + channelB) * 0.95;
		mask = pow(mask, 2.0);
		mask = clamp(mask, 0.0, 1.0);

		if(waterDepth < foam_falloff_distance * foam_edge_distance) {
			float leading = waterDepth / (foam_falloff_distance * foam_edge_distance);
			ALPHA *= leading;
			mask *= leading;
		}

		vec3 edge = foam_color * falloff;

		if (debug_foam) {
			ALBEDO = vec3(falloff, mask, 0.0);
		} else {
			ALBEDO += clamp(edge - vec3(mask), 0.0, 1.0);
		}
	}
	}
}
