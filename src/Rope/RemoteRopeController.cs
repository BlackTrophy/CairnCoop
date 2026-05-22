using CairnCoop.Protocol;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace CairnCoop.Rope
{
    /// <summary>
    /// Added to remote player GameObjects.
    ///
    /// Renders a Catenary curve using LineRenderer based on 5 control points
    /// extracted from RopeSnapshotPacket (endpoints + 3 intermediate).
    /// NO Obi simulation on clients for remote ropes — purely visual.
    /// </summary>
    public sealed class RemoteRopeController : MonoBehaviour
    {
        private LineRenderer? _line;

        // Snapshot state stored as primitives — IL2CPP ClassInjector crashes on
        // custom struct VALUE TYPE fields on registered MonoBehaviours.
        private byte    _curActive;
        private Vector3 _curAttachA,  _curAttachB;
        private Vector3 _curCtrl1,    _curCtrl2,   _curCtrl3;
        private float   _curTension;

        private Vector3 _prevAttachA, _prevAttachB;
        private Vector3 _prevCtrl1,   _prevCtrl2,  _prevCtrl3;

        private float _lerpProgress = 1f; // 0 = previous, 1 = current

        private const int   SEGMENTS             = 20;
        private const float LERP_SPEED           = 10f;  // reach next snapshot in 100ms
        private const float DRIFT_SNAP_THRESHOLD = 0.5f; // 50cm

        private static readonly Color RopeColor = new Color(0.55f, 0.4f, 0.2f, 1f);

        // ----------------------------------------------------------------
        // Unity lifecycle
        // ----------------------------------------------------------------
        private void Awake()
        {
            _line = gameObject.AddComponent<LineRenderer>();
            _line.positionCount = SEGMENTS + 1;
            _line.startWidth    = 0.015f;
            _line.endWidth      = 0.015f;
            _line.useWorldSpace = true;
            _line.material      = CreateRopeMaterial();
            _line.enabled       = false;

            // Disable shadow casting for performance
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // ----------------------------------------------------------------
        // Called from RemotePlayer
        // ----------------------------------------------------------------
        [HideFromIl2Cpp]
        public void ReceiveSnapshot(in RopeSnapshotPacket snap)
        {
            if (snap.IsActive == 0)
            {
                _line!.enabled = false;
                _curActive = 0;
                return;
            }

            Vector3 snapAttachB = snap.AttachB.ToUnity();

            // Check for large drift → snap immediately
            if (_curActive == 1)
            {
                float drift = Vector3.Distance(_curAttachB, snapAttachB);
                if (drift > DRIFT_SNAP_THRESHOLD)
                {
                    StoreAsCurrent(snap);
                    CopyCurrentToPrevious();
                    _lerpProgress = 1f;
                    return;
                }
            }

            // Previous = current (or snap if first packet)
            if (_curActive == 1) CopyCurrentToPrevious();
            else                 StoreAsPrevious(snap);

            StoreAsCurrent(snap);
            _lerpProgress = 0f;
        }

        [HideFromIl2Cpp]
        public void HandleRopeEvent(PacketType type)
        {
            if (type == PacketType.RopeDetached)
            {
                _line!.enabled = false;
                _curActive = 0;
            }
        }

        [HideFromIl2Cpp]
        private void StoreAsCurrent(in RopeSnapshotPacket snap)
        {
            _curActive  = snap.IsActive;
            _curAttachA = snap.AttachA.ToUnity();
            _curAttachB = snap.AttachB.ToUnity();
            _curCtrl1   = snap.CtrlPoint1.ToUnity();
            _curCtrl2   = snap.CtrlPoint2.ToUnity();
            _curCtrl3   = snap.CtrlPoint3.ToUnity();
            _curTension = snap.Tension;
        }

        [HideFromIl2Cpp]
        private void StoreAsPrevious(in RopeSnapshotPacket snap)
        {
            _prevAttachA = snap.AttachA.ToUnity();
            _prevAttachB = snap.AttachB.ToUnity();
            _prevCtrl1   = snap.CtrlPoint1.ToUnity();
            _prevCtrl2   = snap.CtrlPoint2.ToUnity();
            _prevCtrl3   = snap.CtrlPoint3.ToUnity();
        }

        private void CopyCurrentToPrevious()
        {
            _prevAttachA = _curAttachA;
            _prevAttachB = _curAttachB;
            _prevCtrl1   = _curCtrl1;
            _prevCtrl2   = _curCtrl2;
            _prevCtrl3   = _curCtrl3;
        }

        // ----------------------------------------------------------------
        // Update — drive LineRenderer
        // ----------------------------------------------------------------
        private void Update()
        {
            if (_curActive == 0 || _line == null)
            {
                if (_line != null) _line.enabled = false;
                return;
            }

            _line.enabled = true;
            _lerpProgress = Mathf.Clamp01(_lerpProgress + Time.deltaTime * LERP_SPEED);

            // Lerp control points between previous and current snapshot
            Vector3 a  = Vector3.Lerp(_prevAttachA, _curAttachA, _lerpProgress);
            Vector3 b  = Vector3.Lerp(_prevAttachB, _curAttachB, _lerpProgress);
            Vector3 c1 = Vector3.Lerp(_prevCtrl1,   _curCtrl1,   _lerpProgress);
            Vector3 c2 = Vector3.Lerp(_prevCtrl2,   _curCtrl2,   _lerpProgress);
            Vector3 c3 = Vector3.Lerp(_prevCtrl3,   _curCtrl3,   _lerpProgress);

            // Apply tension factor: tighter rope = less catenary sag
            float sag = 1f - Mathf.Clamp01(_curTension);
            c2 -= Vector3.up * (sag * 0.15f);

            // Generate curve and write to LineRenderer
            for (int i = 0; i <= SEGMENTS; i++)
            {
                float t = (float)i / SEGMENTS;
                _line.SetPosition(i, CatmullRom(a, c1, c2, c3, b, t));
            }
        }

        // ----------------------------------------------------------------
        // Catmull-Rom through 5 points
        // ----------------------------------------------------------------
        private static Vector3 CatmullRom(
            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4, float t)
        {
            // Map t to segment
            if (t <= 0.25f)       return CRSegment(p0, p0, p1, p2, t / 0.25f);
            else if (t <= 0.5f)   return CRSegment(p0, p1, p2, p3, (t - 0.25f) / 0.25f);
            else if (t <= 0.75f)  return CRSegment(p1, p2, p3, p4, (t - 0.50f) / 0.25f);
            else                  return CRSegment(p2, p3, p4, p4, (t - 0.75f) / 0.25f);
        }

        private static Vector3 CRSegment(
            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3
            );
        }

        // ----------------------------------------------------------------
        // Material
        // ----------------------------------------------------------------
        private static Material CreateRopeMaterial()
        {
            // Use URP/Lit or fallback to legacy
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard");

            var mat = new Material(shader);
            mat.color = RopeColor;
            return mat;
        }
    }
}
