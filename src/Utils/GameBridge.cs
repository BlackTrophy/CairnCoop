using System;
using System.Reflection;
using BepInEx.Logging;
using CairnCoop.Protocol;
using Obi;
using UnityEngine;
// using UnityEngine.AddressableAssets; -- not in interop yet
using UnityEngine.SceneManagement;

namespace CairnCoop
{
    /// <summary>
    /// GameBridge is the single point of contact between the mod and Cairn's
    /// game logic.  All IL2CPP reflection and interop is isolated here.
    ///
    /// Each method has a documented assumption about the game class/field it
    /// targets, derived from:
    ///   - RuntimeInitializeOnLoads.json analysis
    ///   - ECTO file format analysis
    ///   - Known Unity / Obi / FinalIK API
    ///
    /// WHEN IL2CPPDUMPER OUTPUT IS AVAILABLE:
    ///   Replace every TODO comment with the confirmed field/method name.
    /// </summary>
    public static class GameBridge
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.GameBridge");

        // ================================================================
        // LOCAL PAWN ACCESS
        // ================================================================

        /// <summary>The local player's pawn GameObject.</summary>
        public static GameObject? LocalPawn { get; private set; }

        public static void RegisterLocalPawn(GameObject go)
        {
            LocalPawn = go;
            Log.LogInfo($"Local pawn registered: {go.name}");
        }

        public static void ClearLocalPawn() => LocalPawn = null;

        // ================================================================
        // PLAYER STATE SAMPLING
        // ================================================================

        public static PlayerStatePacket SampleLocalPlayerState(uint tick)
        {
            if (LocalPawn == null)
                return new PlayerStatePacket { Tick = tick };

            var rb     = LocalPawn.GetComponent<Rigidbody>();
            var vel    = rb != null ? rb.velocity : Vector3.zero;
            var pos    = LocalPawn.transform.position;
            var yaw    = LocalPawn.transform.eulerAngles.y;

            float stamina = SampleStamina();
            byte climbState = SampleClimbState();

            return new PlayerStatePacket
            {
                Tick       = tick,
                Position   = pos,
                Yaw        = yaw,
                Velocity   = vel,
                Stamina    = stamina,
                ClimbState = (ClimberState)climbState,
            };
        }

        private static float SampleStamina()
        {
            if (LocalPawn == null) return 1f;

            // Cairn's pawn likely has a stamina component with a "currentStamina" field.
            // ASSUMPTION: Field name "currentStamina" (from BreathingManager / AssistManager context)
            // TODO: Confirm field name from Il2CppDumper dump.cs
            // NOTE: IL2CPP GetComponent(string) requires Il2CppSystem.Type — use GetComponents and filter by name.
            try
            {
                var components = LocalPawn.GetComponentsInChildren<Component>(true);
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    string name = comp.GetType().Name;
                    if (name == "StaminaSystem" || name == "StaminaManager")
                    {
                        var field = comp.GetType().GetField("currentStamina",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (field != null) return (float)field.GetValue(comp);
                    }
                }
            }
            catch (Exception ex) { Log.LogDebug($"Stamina sample failed: {ex.Message}"); }

            return 1f; // default full
        }

        private static byte SampleClimbState()
        {
            if (LocalPawn == null) return 0;

            // ClimberState is likely an enum on the pawn or a separate state machine component.
            // ASSUMPTION: Component named "ClimberStateMachine" with "currentState" enum field.
            // TODO: Confirm from Il2CppDumper
            // NOTE: IL2CPP GetComponent(string) requires Il2CppSystem.Type — use GetComponents and filter by name.
            try
            {
                var components = LocalPawn.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    string name = comp.GetType().Name;
                    if (name == "ClimberStateMachine" || name == "ClimbingController")
                    {
                        var field = comp.GetType().GetField("currentState",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (field != null) return Convert.ToByte(field.GetValue(comp));
                    }
                }
            }
            catch { }
            return 0;
        }

        // ================================================================
        // INPUT COLLECTION
        // ================================================================

        public static InputFramePacket CollectInput(uint tick)
        {
            // Reads from Unity Input System — no IL2CPP reflection needed.
            var move = UnityEngine.InputSystem.Keyboard.current != null
                ? new Vector2(
                    (UnityEngine.InputSystem.Keyboard.current.dKey.isPressed ? 1 : 0) -
                    (UnityEngine.InputSystem.Keyboard.current.aKey.isPressed ? 1 : 0),
                    (UnityEngine.InputSystem.Keyboard.current.wKey.isPressed ? 1 : 0) -
                    (UnityEngine.InputSystem.Keyboard.current.sKey.isPressed ? 1 : 0))
                : Vector2.zero;

            // Gamepad
            var pad = UnityEngine.InputSystem.Gamepad.current;
            if (pad != null)
            {
                move = pad.leftStick.ReadValue();
            }

            byte buttons = 0;
            if (pad != null)
            {
                if (pad.buttonSouth.isPressed)     buttons |= 0x01; // jump
                if (pad.leftShoulder.isPressed)    buttons |= 0x02; // left grip
                if (pad.rightShoulder.isPressed)   buttons |= 0x04; // right grip
                if (pad.buttonWest.isPressed)      buttons |= 0x08; // rope action
            }

            float lt = pad?.leftTrigger.ReadValue() ?? 0f;
            float rt = pad?.rightTrigger.ReadValue() ?? 0f;

            return new InputFramePacket
            {
                Tick         = tick,
                MoveX        = move.x,
                MoveY        = move.y,
                Buttons      = buttons,
                LeftTrigger  = (byte)(lt * 255f),
                RightTrigger = (byte)(rt * 255f),
            };
        }

        // ================================================================
        // OBI ROPE ACCESS
        // ================================================================

        public static ObiSolver? GetLocalObiSolver()
        {
            // ObiSolverManager is a singleton — find it
            // ASSUMPTION: ObiSolverManager.Instance.primarySolver or .GetSolvers()[0]
            try
            {
                var solverMgr = GameObject.FindObjectOfType<ObiSolver>();
                if (solverMgr != null) return solverMgr;

                // Try via ObiSolverManager type (singleton)
                var mgrType = Type.GetType("ObiSolverManager, TheGameBakers.Cairn.Global");
                if (mgrType != null)
                {
                    var instanceProp = mgrType.GetProperty("Instance")
                                    ?? mgrType.GetProperty("instance");
                    if (instanceProp != null)
                    {
                        var mgr = instanceProp.GetValue(null);
                        var solverField = mgrType.GetField("solver")
                                       ?? mgrType.GetField("_solver");
                        if (solverField != null)
                            return solverField.GetValue(mgr) as ObiSolver;
                    }
                }
            }
            catch (Exception ex) { Log.LogDebug($"ObiSolver search: {ex.Message}"); }
            return null;
        }

        public static ObiRope? GetPlayerRope()
        {
            // ASSUMPTION: The local pawn has an ObiParticleAttachment referencing an ObiRope
            // NOTE: ObiParticleAttachment.Actor is not available in the generated interop.
            // Fall back to finding ObiRope in the scene.
            if (LocalPawn != null)
            {
                var rope = LocalPawn.GetComponentInChildren<ObiRope>();
                if (rope != null) return rope;
            }
            return GameObject.FindObjectOfType<ObiRope>();
        }

        // ================================================================
        // ANCHOR VISUALS
        // ================================================================

        private static GameObject? _anchorPrefab;

        public static void SpawnAnchorVisual(ushort anchorID, Vector3 position, Vector3 normal)
        {
            // Simple sphere proxy until we extract the real anchor prefab
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.localScale = Vector3.one * 0.05f;
            go.transform.position   = position;
            go.transform.up         = normal;
            go.name                 = $"[CairnCoop_Anchor_{anchorID}]";

            var mr  = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.material.color = Color.yellow;

            // Remove collider — purely visual
            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);
        }

        // ================================================================
        // SCENE MANAGEMENT
        // ================================================================

        public static string? GetCurrentSceneName()
            => SceneManager.GetActiveScene().name;

        public static void LoadScene(string sceneName)
        {
            // Use Cairn's own CairnSceneManager if available
            // ASSUMPTION: CairnSceneManager.Instance.LoadScene(name)
            try
            {
                var smType = Type.GetType("CairnSceneManager, TheGameBakers.Cairn.Global");
                if (smType != null)
                {
                    var inst  = smType.GetProperty("Instance")?.GetValue(null);
                    var meth  = smType.GetMethod("LoadScene",
                        new[] { typeof(string) });
                    if (inst != null && meth != null)
                    {
                        meth.Invoke(inst, new object[] { sceneName });
                        return;
                    }
                }
            }
            catch { }

            // Fallback to Unity directly
            SceneManager.LoadScene(sceneName);
        }

        // ================================================================
        // CHECKPOINT MANAGEMENT
        // ================================================================

        public static void ActivateCheckpoint(int checkpointID)
        {
            Log.LogDebug($"ActivateCheckpoint: {checkpointID}");
            // ASSUMPTION: ClimbingSceneManager.Instance.ActivateCheckpoint(int)
            // TODO: Hook into actual game method after Il2CppDumper analysis
            try
            {
                var smType = Type.GetType("ClimbingSceneManager, TheGameBakers.Cairn.Global");
                if (smType != null)
                {
                    var inst = smType.GetProperty("Instance")?.GetValue(null);
                    var meth = smType.GetMethod("ActivateCheckpoint", new[] { typeof(int) });
                    meth?.Invoke(inst, new object[] { checkpointID });
                }
            }
            catch (Exception ex) { Log.LogDebug($"ActivateCheckpoint failed: {ex.Message}"); }
        }

        public static Vector3 GetCheckpointPosition(int checkpointID)
        {
            // ASSUMPTION: ClimbingSceneManager has a list of checkpoints with positions
            // Fallback: use player's last known position
            return LocalPawn?.transform.position ?? Vector3.zero;
        }
    }
}
