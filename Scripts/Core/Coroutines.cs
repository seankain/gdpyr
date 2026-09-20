using System.Collections;
using System.Collections.Generic;
using Godot;

namespace Gdpyr.Core;

/// <summary>
/// Steps an iterator one render frame at a time.
///
/// Nothing in the simulation may use this: it resolves on the render frame, so a
/// state transition driven from here would land on a different tick on every
/// machine and could not be replayed (docs/NETCODE.md §3.1). M1 removed the last
/// such use. Cosmetic sequencing — a UI fade, a delayed sound — is fair game.
/// </summary>
public static class Coroutines
{
    public static async void StartCoroutine(IEnumerable objects)
    {
        var mainLoopTree = Engine.GetMainLoop();
        foreach (var _ in objects)
        {
            await mainLoopTree.ToSignal(mainLoopTree, SceneTree.SignalName.ProcessFrame);
        }
    }
}
