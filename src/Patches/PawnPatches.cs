using BepInEx.Logging;
using CairnCoop.Network;
using HarmonyLib;
using UnityEngine;

namespace CairnCoop.Patches
{
    /// <summary>
    /// Patches PawnManager to give us access to the local pawn reference.
    /// Also patches scene-load lifecycle for multiplayer-aware scene handling.
    /// </summary>
    [HarmonyPatch]
    public static class PawnPatches
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.PawnPatches");

        // ----------------------------------------------------------------
        // PawnManager.SpawnPawn — capture the local pawn reference
        // ----------------------------------------------------------------
        [HarmonyPatch("PawnManager", "SpawnPawn")]
        [HarmonyPostfix]
        public static void Postfix_SpawnPawn(object __instance, object __result)
        {
            if (__result is GameObject pawnGO)
                GameBridge.RegisterLocalPawn(pawnGO);
            else if (__result is MonoBehaviour pawnMB)
                GameBridge.RegisterLocalPawn(pawnMB.gameObject);

            Log.LogInfo("Local pawn spawned and registered.");
        }

        // ----------------------------------------------------------------
        // PawnManager.DespawnPawn
        // ----------------------------------------------------------------
        [HarmonyPatch("PawnManager", "DespawnPawn")]
        [HarmonyPrefix]
        public static void Prefix_DespawnPawn(object __instance)
        {
            GameBridge.ClearLocalPawn();
        }

        // ----------------------------------------------------------------
        // PlayerSimulatorManager — used for ghost/recording playback.
        // We hook it to prevent ghost-replays from interfering with
        // multiplayer state.
        // ----------------------------------------------------------------
        [HarmonyPatch("PlayerSimulatorManager", "PlayGhost")]
        [HarmonyPrefix]
        public static bool Prefix_PlayGhost()
        {
            // During multiplayer, suppress standalone ghost playback
            // (we handle remote-player display ourselves).
            return NetworkManager.Instance?.IsInGame != true;
        }

        // ----------------------------------------------------------------
        // GlobalGameManager — intercept scene load to sync world state
        // ----------------------------------------------------------------
        [HarmonyPatch("GlobalGameManager", "LoadScene")]
        [HarmonyPostfix]
        public static void Postfix_LoadScene(object __instance, string sceneName)
        {
            Log.LogInfo($"Scene loading: {sceneName}");
            // If we're a late-joining client, the host will send
            // LateJoinData after scene is ready.
            NetworkManager.Instance?.LateJoin?.OnSceneLoaded(sceneName);
        }

        // ----------------------------------------------------------------
        // RecapAscentManager — suppress single-player recap in multiplayer
        // ----------------------------------------------------------------
        [HarmonyPatch("RecapAscentManager", "ShowRecap")]
        [HarmonyPrefix]
        public static bool Prefix_ShowRecap()
        {
            return NetworkManager.Instance?.IsInGame != true;
        }
    }
}
