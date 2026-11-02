using Gdpyr.Sim;

namespace Gdpyr.Effects;

/// <summary>
/// One explosion as drawn (docs/EFFECTS.md §4): what <see cref="ExplosionEffects"/>
/// pools and plays. <see cref="PlaceholderExplosion"/> is one, built from primitive
/// meshes for every kind; real art is another — a scene at
/// <c>res://Effects/explosion_&lt;kind&gt;.tscn</c> whose root is a <c>Node3D</c> with a
/// script implementing this, which replaces the placeholder for that kind with no
/// other change.
///
/// Presentation only. Nothing here may feed the simulation, and a peer that draws no
/// explosions at all still agrees with the server about every hit.
/// </summary>
public interface IExplosionEffect
{
	/// <summary>
	/// Starts from the beginning at <see cref="ExplosionRequest.Point"/>, made visible,
	/// whatever it was doing — a pooled effect is reused, and may be cut short for a new one.
	/// </summary>
	void Play(in ExplosionRequest request);

	/// <summary>Moves it on by <paramref name="delta"/> seconds. False once it has finished and hidden itself.</summary>
	bool Advance(float delta);

	/// <summary>Hides it at once, for a round being cleared away.</summary>
	void Stop();
}
