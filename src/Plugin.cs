using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using CairnCoop.IK;
using CairnCoop.Network;
using CairnCoop.Players;
using CairnCoop.Rope;
using CairnCoop.Spectator;
using CairnCoop.UI;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

[assembly: System.Reflection.AssemblyVersion("0.1.0")]

namespace CairnCoop
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BasePlugin
    {
        public const string GUID    = "cairncoop.multiplayer";
        public const string NAME    = "Cairn Co-op";
        public const string VERSION = "0.1.0";

        internal static new ManualLogSource Log = null!;

        public override void Load()
        {
            Log = base.Log;
            Log.LogInfo($"=== {NAME} v{VERSION} loading ===");

            // ----------------------------------------------------------------
            // 1. Register custom MonoBehaviour types with IL2CPP
            // ----------------------------------------------------------------
            RegisterIL2CppTypes();

            // ----------------------------------------------------------------
            // 2. Apply Harmony patches (fault-tolerant per class)
            //    harmony.PatchAll() throws if ANY target method is missing,
            //    aborting the entire plugin load. We patch each class
            //    individually so a bad method name is just a warning.
            // ----------------------------------------------------------------
            var harmony = new Harmony(GUID);
            int patchOk = 0, patchSkip = 0;
            foreach (var type in typeof(Plugin).Assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    patchOk++;
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"[Harmony] Patch class {type.Name} skipped — " +
                                   $"target method not found: {ex.Message.Split('\n')[0]}");
                    patchSkip++;
                }
            }
            Log.LogInfo($"Harmony: {patchOk} patch class(es) applied, {patchSkip} skipped.");

            // ----------------------------------------------------------------
            // 3. Create the NetworkManager GameObject (DontDestroyOnLoad)
            //    Initialize() is called from NetworkManager.Start() — AFTER
            //    the first scene loads and Steam has been initialized by the game.
            // ----------------------------------------------------------------
            NetworkManager.EnsureExists();

            // ----------------------------------------------------------------
            // 4. Attach UI + input handler to the NetworkManager object
            // ----------------------------------------------------------------
            var nmGo = NetworkManager.Instance.gameObject;
            nmGo.AddComponent<CoopMenuUI>();
            nmGo.AddComponent<InputHandler>();

            Log.LogInfo($"=== {NAME} ready ===");
        }

        private static void RegisterIL2CppTypes()
        {
            ClassInjector.RegisterTypeInIl2Cpp<NetworkManager>();
            ClassInjector.RegisterTypeInIl2Cpp<Client.LocalPlayerController>();
            ClassInjector.RegisterTypeInIl2Cpp<RemoteIKController>();
            ClassInjector.RegisterTypeInIl2Cpp<RemoteRopeController>();
            ClassInjector.RegisterTypeInIl2Cpp<LocalIKTracker>();
            ClassInjector.RegisterTypeInIl2Cpp<LocalRopeTracker>();
            ClassInjector.RegisterTypeInIl2Cpp<SpectatorController>();
            ClassInjector.RegisterTypeInIl2Cpp<CoopMenuUI>();
            ClassInjector.RegisterTypeInIl2Cpp<InputHandler>();
            Log.LogInfo("IL2CPP types registered.");
        }
    }

    // ================================================================
    //  Keyboard handler — thin layer that delegates to CoopMenuUI.
    //  Does NOT directly call network; all logic lives in the UI class.
    // ================================================================
    public sealed class InputHandler : MonoBehaviour
    {
        private void Update()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;

            var ui = CoopMenuUI.Instance;

            // While any window is open, swallow F-key events so the
            // game doesn't interpret them (e.g. photo mode, debug menus).
            bool uiOpen = ui != null && ui.IsVisible;

            // F5 — host window (toggle)
            if (kb.f5Key.wasPressedThisFrame)
            {
                if (ui != null) ui.OpenHost();
            }

            // F6 — join window (toggle)
            if (kb.f6Key.wasPressedThisFrame)
            {
                if (ui != null) ui.OpenJoin();
            }

            // F7 — spectator (only when no UI is open)
            if (kb.f7Key.wasPressedThisFrame && !uiOpen)
            {
                var net = NetworkManager.Instance;
                if (net != null)
                {
                    var spectator = net.gameObject.GetComponent<SpectatorController>();
                    if (spectator != null) spectator.EnterSpectate();
                }
            }
        }
    }

    // ================================================================
    //  BepInEx plugin metadata (normally auto-generated)
    // ================================================================
    public static class MyPluginInfo
    {
        public const string PLUGIN_GUID    = Plugin.GUID;
        public const string PLUGIN_NAME    = Plugin.NAME;
        public const string PLUGIN_VERSION = Plugin.VERSION;
    }
}
