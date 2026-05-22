using BepInEx.Logging;
using CairnCoop.Interpolation;
using CairnCoop.Network;
using CairnCoop.Protocol;
using Il2CppInterop.Runtime.Attributes;
using RootMotion.FinalIK;
using UnityEngine;

namespace CairnCoop.IK
{
    /// <summary>
    /// Added to remote player GameObjects.
    /// Receives IKSnapshotPackets from the network, interpolates targets,
    /// and drives the local FullBodyBipedIK solver.
    ///
    /// Requires: FullBodyBipedIK component on the same or parent GameObject.
    /// </summary>
    public sealed class RemoteIKController : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.RemoteIKController");

        private FullBodyBipedIK?  _fbik;
        private bool              _ready;

        // Interpolation buffer — 16 snapshots of IK data
        private readonly SnapshotInterpolator<IKFrame> _interp =
            new(LerpIKFrame);

        // Smoothed current targets (to avoid snapping)
        private Vector3 _curLH, _curRH, _curLF, _curRF;
        private const float MAX_SNAP_DIST = 2.0f;
        private const float SMOOTH_FACTOR = 8.0f;
        private const float MIN_SPEED     = 0.5f;

        // ----------------------------------------------------------------
        // Unity lifecycle
        // ----------------------------------------------------------------
        private void Start()
        {
            // Walk up hierarchy to find FullBodyBipedIK
            _fbik  = GetComponentInParent<FullBodyBipedIK>()
                  ?? GetComponentInChildren<FullBodyBipedIK>();

            if (_fbik == null)
            {
                Log.LogWarning("FullBodyBipedIK not found on remote pawn — IK disabled.");
                return;
            }

            // Disable auto-update: we call it manually in LateUpdate
            // so it fires after position interpolation has moved the pawn.
            _fbik.fixTransforms = false;
            _ready = true;
            Log.LogDebug("RemoteIKController ready.");
        }

        // ----------------------------------------------------------------
        // Called from PacketRouter → RemotePlayer → here
        // ----------------------------------------------------------------
        [HideFromIl2Cpp]
        public void ReceiveSnapshot(in IKSnapshotPacket pkt, float renderTime)
        {
            _interp.AddSnapshot(new IKFrame(pkt), renderTime + 0.1f);
        }

        // ----------------------------------------------------------------
        // LateUpdate — runs after Animator, before rendering
        // ----------------------------------------------------------------
        private void LateUpdate()
        {
            if (!_ready || _fbik == null) return;

            float renderTime = GetRenderTime();
            if (!_interp.TryGetInterpolated(renderTime, out var frame)) return;

            // Smooth each target
            _curLH = SmoothTarget(_curLH, frame.LeftHand);
            _curRH = SmoothTarget(_curRH, frame.RightHand);
            _curLF = SmoothTarget(_curLF, frame.LeftFoot);
            _curRF = SmoothTarget(_curRF, frame.RightFoot);

            // Apply to Final IK effectors
            var solver = _fbik.solver;
            solver.leftHandEffector.position      = _curLH;
            solver.rightHandEffector.position     = _curRH;
            solver.leftFootEffector.position      = _curLF;
            solver.rightFootEffector.position     = _curRF;

            solver.leftHandEffector.positionWeight  = frame.LHWeight;
            solver.rightHandEffector.positionWeight = frame.RHWeight;
            solver.leftFootEffector.positionWeight  = frame.LFWeight;
            solver.rightFootEffector.positionWeight = frame.RFWeight;

            // Manually solve IK this frame
            _fbik.solver.Update();
        }

        // ----------------------------------------------------------------
        // Smoothing
        // ----------------------------------------------------------------
        private Vector3 SmoothTarget(Vector3 current, Vector3 target)
        {
            float dist = Vector3.Distance(current, target);
            if (dist > MAX_SNAP_DIST) return target; // hard snap (teleport / respawn)

            float speed = Mathf.Max(dist * SMOOTH_FACTOR, MIN_SPEED);
            return Vector3.MoveTowards(current, target, speed * Time.deltaTime);
        }

        // ----------------------------------------------------------------
        // Lerp delegate for SnapshotInterpolator<IKFrame>
        // ----------------------------------------------------------------
        private static IKFrame LerpIKFrame(IKFrame a, IKFrame b, float t) => new()
        {
            LeftHand  = Vector3.Lerp(a.LeftHand,  b.LeftHand,  t),
            RightHand = Vector3.Lerp(a.RightHand, b.RightHand, t),
            LeftFoot  = Vector3.Lerp(a.LeftFoot,  b.LeftFoot,  t),
            RightFoot = Vector3.Lerp(a.RightFoot, b.RightFoot, t),
            LHWeight  = Mathf.Lerp(a.LHWeight, b.LHWeight, t),
            RHWeight  = Mathf.Lerp(a.RHWeight, b.RHWeight, t),
            LFWeight  = Mathf.Lerp(a.LFWeight, b.LFWeight, t),
            RFWeight  = Mathf.Lerp(a.RFWeight, b.RFWeight, t),
        };

        private static float GetRenderTime()
            => NetworkManager.Instance?.Ticks.RenderTime ?? Time.time;

        // ----------------------------------------------------------------
        // Internal value type
        // ----------------------------------------------------------------
        private struct IKFrame
        {
            public Vector3 LeftHand, RightHand, LeftFoot, RightFoot;
            public float   LHWeight, RHWeight, LFWeight, RFWeight;

            public IKFrame(in IKSnapshotPacket p)
            {
                LeftHand  = p.LeftHandTarget.ToUnity();
                RightHand = p.RightHandTarget.ToUnity();
                LeftFoot  = p.LeftFootTarget.ToUnity();
                RightFoot = p.RightFootTarget.ToUnity();
                LHWeight  = p.LHWeight / 255f;
                RHWeight  = p.RHWeight / 255f;
                LFWeight  = p.LFWeight / 255f;
                RFWeight  = p.RFWeight / 255f;
            }
        }
    }
}
