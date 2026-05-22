// ============================================================
//  BUILD STUBS — nur für CI/CD ohne Spieldateien
//  Diese Datei wird nur eingebunden wenn -p:UseStubs=true
//  Im echten Build werden diese durch die BepInEx Interop-DLLs ersetzt.
// ============================================================
#if USE_STUBS

// RootMotion Final IK stubs
namespace RootMotion.FinalIK
{
    public class FullBodyBipedIK : UnityEngine.MonoBehaviour
    {
        public IKSolverFullBodyBiped solver = new();
        public bool fixTransforms;
    }
    public class IKSolverFullBodyBiped
    {
        public IKEffector leftHandEffector  = new();
        public IKEffector rightHandEffector = new();
        public IKEffector leftFootEffector  = new();
        public IKEffector rightFootEffector = new();
        public void Update() { }
    }
    public class IKEffector
    {
        public UnityEngine.Vector3 position;
        public float positionWeight;
    }
}

// Obi stubs
namespace Obi
{
    public class ObiSolver : UnityEngine.MonoBehaviour
    {
        public ObiNativeVector4List positions = new();
        public event System.Action<ObiSolver>? OnSolverStepEnd;
    }
    public class ObiRope : UnityEngine.MonoBehaviour
    {
        public ObiNativeIntList    solverIndices = new();
        public float               restLength;
        public ObiSolver?          Solver;
        public ObiActor?           Actor;
    }
    public class ObiActor : UnityEngine.MonoBehaviour { }
    public class ObiParticleAttachment : UnityEngine.MonoBehaviour
    {
        public ObiActor? Actor;
    }
    public class ObiNativeVector4List
    {
        private readonly System.Collections.Generic.List<UnityEngine.Vector4> _data = new();
        public UnityEngine.Vector4 this[int i] => _data.Count > i ? _data[i] : default;
        public int count => _data.Count;
    }
    public class ObiNativeIntList
    {
        private readonly System.Collections.Generic.List<int> _data = new();
        public int this[int i] => _data.Count > i ? _data[i] : 0;
        public int count => _data.Count;
    }
}

// Steamworks stubs (minimal surface needed for compilation)
namespace Steamworks
{
    public static class SteamAPI
    {
        public static bool IsSteamRunning() => false;
        public static void RunCallbacks() { }
    }
    public static class SteamUser
    {
        public static CSteamID GetSteamID() => new(0);
    }
    public static class SteamNetworkingMessages
    {
        public static int SendMessageToUser(ref SteamNetworkingIdentity id,
            System.IntPtr data, uint size, int flags, int port) => 0;
        public static int ReceiveMessagesOnChannel(int port,
            System.IntPtr[] msgs, int max) => 0;
        public static void AcceptSessionWithUser(ref SteamNetworkingIdentity id) { }
        public static void CloseSessionWithUser(ref SteamNetworkingIdentity id) { }
    }
    public struct CSteamID
    {
        public ulong m_SteamID;
        public CSteamID(ulong id) { m_SteamID = id; }
    }
    public struct SteamNetworkingIdentity
    {
        public void SetSteamID(CSteamID id) { }
        public CSteamID GetSteamID() => new(0);
        public ulong GetSteamID64() => 0;
    }
    public struct SteamNetworkingMessage_t
    {
        public System.IntPtr m_pData;
        public int           m_cbSize;
        public SteamNetworkingIdentity m_identityPeer;
        public static void Release(System.IntPtr msg) { }
    }
    public struct SteamNetworkingMessagesSessionRequest_t
    {
        public SteamNetworkingIdentity m_identityRemote;
    }
    public struct SteamNetworkingMessagesSessionFailed_t
    {
        public SteamNetworkingConnectionInfo_t m_info;
    }
    public struct SteamNetworkingConnectionInfo_t
    {
        public SteamNetworkingIdentity m_identityRemote;
    }
    public static class Constants
    {
        public const int k_nSteamNetworkingSend_Reliable   = 8;
        public const int k_nSteamNetworkingSend_Unreliable = 0;
    }
    public class Callback<T>
    {
        public static Callback<T> Create(System.Action<T> fn) => new();
    }
}

#endif // USE_STUBS
