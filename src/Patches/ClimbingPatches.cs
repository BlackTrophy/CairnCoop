using BepInEx.Logging;
using CairnCoop.Network;
using CairnCoop.Protocol;
using HarmonyLib;
using UnityEngine;

namespace CairnCoop.Patches
{
    /// <summary>
    /// Harmony patches for Cairn's climbing subsystems.
    ///
    /// Target classes (from RuntimeInitializeOnLoads.json analysis):
    ///   - ClimbingV2HoldsManager  — grip detection / hold ID assignment
    ///   - ClimbingV2RAHManager    — Reach-And-Hold IK target updates
    ///
    /// Since IL2CPP interop generates stubs matching the original C# class names,
    /// [HarmonyPatch(typeof(ClassName))] works as normal after BepInEx generates
    /// the interop assemblies.
    ///
    /// IMPORTANT: If ClimbingV2RAHManager or ClimbingV2HoldsManager are NOT found
    /// in the generated interop, use the string-based [HarmonyPatch("ClassName","MethodName")]
    /// overload and resolve via Il2CppType.
    /// </summary>
    [HarmonyPatch]
    public static class ClimbingPatches
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.ClimbingPatches");

        // ================================================================
        // ClimbingV2HoldsManager — grip state changes
        // ================================================================

        /// <summary>
        /// Fires when the local player grips a hold.
        /// Method name: to be confirmed with Il2CppDumper dump.cs output.
        /// Likely candidates: "SetGrip", "OnHoldGrabbed", "GrabHold"
        /// </summary>
        [HarmonyPatch("ClimbingV2HoldsManager", "SetGrip")]
        [HarmonyPostfix]
        public static void Postfix_SetGrip(object __instance, object hand, object hold)
        {
            if (!ShouldPatch()) return;

            // Extract hold ID — hold objects likely have an integer ID field
            // We'll cast to Il2CppSystem.Object and use reflection to get ID
            byte holdID = GetHoldID(hold);
            bool isLeft = IsLeftHand(hand);

            Log.LogDebug($"Grip set: hand={isLeft} holdID={holdID}");

            // Notify host via GripChanged event
            if (isLeft)
                NetworkManager.Instance?.Router?.SendGripChanged(holdID, 0xFF);
            else
                NetworkManager.Instance?.Router?.SendGripChanged(0xFF, holdID);
        }

        [HarmonyPatch("ClimbingV2HoldsManager", "ReleaseGrip")]
        [HarmonyPostfix]
        public static void Postfix_ReleaseGrip(object __instance, object hand)
        {
            if (!ShouldPatch()) return;
            bool isLeft = IsLeftHand(hand);

            if (isLeft)
                NetworkManager.Instance?.Router?.SendGripChanged(0, 0xFF);
            else
                NetworkManager.Instance?.Router?.SendGripChanged(0xFF, 0);
        }

        // ================================================================
        // Anchor placement
        // ================================================================

        [HarmonyPatch("ClimbingV2HoldsManager", "PlaceAnchor")]
        [HarmonyPostfix]
        public static void Postfix_PlaceAnchor(
            object __instance, Vector3 position, Vector3 normal)
        {
            if (!ShouldPatch()) return;

            var net = NetworkManager.Instance;
            if (net == null) return;

            var pkt = new AnchorEventPacket
            {
                Header    = new PacketHeader(PacketType.AnchorPlaced, (byte)net.Session!.LocalSlot),
                EventID   = (uint)net.Ticks.CurrentTick,
                AnchorID  = 0, // host assigns real ID
                EventType = 0, // placed
                IsValid   = 0, // pending host validation
                Position  = (NetVector3)position,
                Normal    = (NetVector3)normal,
            };

            net.Router?.SendAnchorEvent(pkt);
        }

        [HarmonyPatch("ClimbingV2HoldsManager", "RemoveAnchor")]
        [HarmonyPostfix]
        public static void Postfix_RemoveAnchor(object __instance, ushort anchorID)
        {
            if (!ShouldPatch()) return;

            var net = NetworkManager.Instance;
            if (net == null) return;

            var pkt = new AnchorEventPacket
            {
                Header    = new PacketHeader(PacketType.AnchorRemoved, (byte)net.Session!.LocalSlot),
                AnchorID  = anchorID,
                EventType = 1, // removed
                IsValid   = 1,
            };
            net.Router?.SendAnchorEvent(pkt);
        }

        // ================================================================
        // Checkpoint activation
        // ================================================================

        [HarmonyPatch("ClimbingSceneManager", "ActivateCheckpoint")]
        [HarmonyPostfix]
        public static void Postfix_ActivateCheckpoint(object __instance, int checkpointID)
        {
            if (!ShouldPatch()) return;

            var net = NetworkManager.Instance;
            if (net == null || !net.IsInGame) return;

            // Only the local player sends checkpoint events to host
            net.Router?.SendCheckpointActivated(checkpointID);
        }

        // ================================================================
        // Player death / respawn
        // ================================================================

        [HarmonyPatch("PawnManager", "OnPawnDied")]
        [HarmonyPostfix]
        public static void Postfix_PawnDied(object __instance)
        {
            if (!ShouldPatch()) return;
            // Host will trigger respawn for all clients
            // Local player respawn is handled by host correction loop
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static bool ShouldPatch()
            => NetworkManager.Instance?.IsInGame == true;

        private static byte GetHoldID(object hold)
        {
            if (hold == null) return 0;
            try
            {
                // Attempt to get ID via IL2CPP reflection
                var idField = hold.GetType().GetField("holdID")
                           ?? hold.GetType().GetField("id")
                           ?? hold.GetType().GetField("ID");
                if (idField != null)
                    return (byte)(int)idField.GetValue(hold);
            }
            catch { /* fallback */ }
            return (byte)hold.GetHashCode();
        }

        private static bool IsLeftHand(object hand)
        {
            if (hand == null) return false;
            try
            {
                var str = hand.ToString();
                return str != null &&
                    (str.Contains("Left") || str.Contains("left") || str.Contains("L"));
            }
            catch { return false; }
        }
    }
}
