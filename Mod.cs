using HarmonyLib;
using UnityEngine;

namespace RecipeGuide;

public sealed class RecipeGuideMod : Mod
{
    private static Harmony? harmony;

    public override void Ready()
    {
        if (RecipeGuideController.Instance != null)
            return;

        var go = new GameObject("RecipeGuideController");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<RecipeGuideController>();

        harmony = new Harmony("digitalshores.recipeguide");
        harmony.PatchAll(typeof(RecipeGuideMod).Assembly);
        Logger.Log("Recipe Guide ready");
    }
}
