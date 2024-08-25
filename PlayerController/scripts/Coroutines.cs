using System.Collections;
using System.Collections.Generic;
using Godot;

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