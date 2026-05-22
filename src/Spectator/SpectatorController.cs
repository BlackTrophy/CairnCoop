using BepInEx.Logging;
using CairnCoop.Network;
using CairnCoop.Players;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace CairnCoop.Spectator
{
    /// <summary>
    /// Spectator mode: follows a remote player's pawn with a free-floating camera.
    ///
    /// Activated when local player's ClimberState == Spectating (set after death
    /// or explicit spectate request).
    ///
    /// Hooks FreeCamController (from RuntimeInitializeOnLoads) or falls back to
    /// our own Cinemachine virtual camera.
    /// </summary>
    public sealed class SpectatorController : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.Spectator");

        private NetworkManager    _net       = null!;
        private RemotePlayer?     _target;
        private int               _targetSlot = -1;
        private bool              _active;

        // Camera follow settings
        private Vector3 _camOffset       = new Vector3(0, 1.5f, -3.5f);
        private Vector3 _smoothedPos;
        private const float CAM_SMOOTH = 5f;

        // ----------------------------------------------------------------
        // Init
        // ----------------------------------------------------------------
        [HideFromIl2Cpp]
        public void Initialize(NetworkManager net)
        {
            _net = net;
        }

        // ----------------------------------------------------------------
        // Public API
        // ----------------------------------------------------------------
        public void EnterSpectate()
        {
            _active = true;
            NextPlayer();
            Log.LogInfo("Spectator mode activated.");
        }

        public void ExitSpectate()
        {
            _active = false;
            _target = null;
            _targetSlot = -1;
        }

        public void NextPlayer()  => CycleTarget(+1);
        public void PrevPlayer()  => CycleTarget(-1);

        private void CycleTarget(int dir)
        {
            var connected = _net.Session?.ConnectedSlots;
            if (connected == null) return;

            int localSlot  = _net.Session!.LocalSlot;
            var slots      = new System.Collections.Generic.List<int>();

            foreach (var s in connected)
                if (s.SlotIndex != localSlot)
                    slots.Add(s.SlotIndex);

            if (slots.Count == 0) return;

            int curIdx = slots.IndexOf(_targetSlot);
            int next   = ((curIdx + dir) % slots.Count + slots.Count) % slots.Count;
            _targetSlot = slots[next];

            var peer  = _net.Session.GetPeerBySlot(_targetSlot);
            // Retrieve from RemotePlayerManager (via internal dict — expose via method)
            _target   = GetRemotePlayer(peer);

            Log.LogInfo($"Spectating slot {_targetSlot}");
        }

        // ----------------------------------------------------------------
        // Camera follow (LateUpdate for smooth tracking)
        // ----------------------------------------------------------------
        private void LateUpdate()
        {
            if (!_active || _target == null) return;

            var pawn = GetPawnOf(_target);
            if (pawn == null) return;

            Vector3 desiredPos = pawn.transform.TransformPoint(_camOffset);
            _smoothedPos = Vector3.Lerp(_smoothedPos, desiredPos, CAM_SMOOTH * Time.deltaTime);

            Camera.main.transform.position = _smoothedPos;
            Camera.main.transform.LookAt(
                pawn.transform.position + Vector3.up * 1.2f,
                Vector3.up);
        }

        // ----------------------------------------------------------------
        // Helpers — resolve RemotePlayer from manager
        // ----------------------------------------------------------------
        private RemotePlayer? GetRemotePlayer(PeerID peer)
        {
            // RemotePlayerManager exposes via a public method we'll add
            return _net.RemotePlayers?.GetPlayer(peer);
        }

        private static GameObject? GetPawnOf(RemotePlayer? rp)
        {
            if (rp == null) return null;
            // RemotePlayer.Pawn property (we'll expose it)
            return rp.Pawn;
        }
    }
}
