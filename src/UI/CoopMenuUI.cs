using System;
using BepInEx.Logging;
using CairnCoop.Network;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnCoop.UI
{
    /// <summary>
    /// IMGUI overlay — host / join windows in Cairn's minimalist style.
    ///
    /// Opened by InputHandler:  F5 = host window,  F6 = join window.
    /// ESC or toggling the same key closes the active window.
    /// The overlay dims the background and blocks game mouse input
    /// while a window is visible.
    /// </summary>
    public sealed class CoopMenuUI : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.CoopMenuUI");

        public static CoopMenuUI? Instance { get; private set; }

        // ----------------------------------------------------------------
        // State
        // ----------------------------------------------------------------
        private enum WindowState { Hidden, Host, Join }
        private WindowState _state = WindowState.Hidden;

        public bool IsVisible => _state != WindowState.Hidden;

        // Host state
        private string _hostCode = "";

        // Join state
        private string _joinInput  = "";
        private string _joinStatus = "";
        private bool   _joining    = false;
        private float  _joinTimer  = 0f;
        private float  _closeTimer = 0f;   // auto-close after success

        // ----------------------------------------------------------------
        // Unity lifecycle
        // ----------------------------------------------------------------
        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            // Cache IL2CPP delegates once — GUI.WindowFunction ctor takes IntPtr in IL2CPP,
            // so we must go through DelegateSupport.ConvertDelegate.
            _hostFunc = DelegateSupport.ConvertDelegate<GUI.WindowFunction>(
                new System.Action<int>(DrawHostContents));
            _joinFunc = DelegateSupport.ConvertDelegate<GUI.WindowFunction>(
                new System.Action<int>(DrawJoinContents));
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            // Auto-close after successful join
            if (_closeTimer > 0f)
            {
                _closeTimer -= Time.deltaTime;
                if (_closeTimer <= 0f) Close();
                return;
            }

            if (!_joining || _state != WindowState.Join) return;

            _joinTimer += Time.deltaTime;
            var net = NetworkManager.Instance;
            if (net == null) return;

            if (net.IsInGame)
            {
                _joinStatus = "Verbunden! Lade Spielwelt...";
                _joining    = false;
                _closeTimer = 2.5f;
            }
            else if (_joinTimer > 15f)
            {
                _joinStatus = "Timeout — keine Verbindung zum Host.";
                _joining    = false;
            }
        }

        // ----------------------------------------------------------------
        // Public API (called from InputHandler)
        // ----------------------------------------------------------------
        public void OpenHost()
        {
            if (_state == WindowState.Host) { Close(); return; } // toggle

            var net = NetworkManager.Instance;
            if (net == null) return;

            // NetworkManager.Start() may not have run yet — lazy-init if needed
            if (net.Session == null)
            {
                try { net.Initialize(); }
                catch (Exception ex)
                {
                    Log.LogWarning($"OpenHost: init failed ({ex.Message}), will retry.");
                    _hostCode = "Steam nicht bereit — F5 nochmal drücken";
                    _state = WindowState.Host;
                    CenterWindow(500, 340);
                    return;
                }
            }

            if (!net.IsInGame)
            {
                try
                {
                    _hostCode = net.HostSession(maxPlayers: 8);
                    Log.LogInfo($"Hosting session. Code: {_hostCode}");
                }
                catch (Exception ex)
                {
                    Log.LogError($"HostSession failed: {ex.Message}");
                    _hostCode = "Fehler beim Starten der Session";
                }
            }
            else
            {
                // Already hosting — just show the code
                _hostCode = net.Transport?.LocalPeerID.Value.ToString() ?? "???";
            }

            _state = WindowState.Host;
            CenterWindow(500, 340);
        }

        public void OpenJoin()
        {
            if (_state == WindowState.Join) { Close(); return; } // toggle

            var net = NetworkManager.Instance;
            if (net?.Session == null && net != null)
            {
                try { net.Initialize(); } catch { /* retry on connect click */ }
            }

            _joinInput  = ReadSavedCode();
            _joinStatus = "Steam-ID eingeben und Verbinden klicken.";
            _joining    = false;
            _joinTimer  = 0f;
            _state      = WindowState.Join;
            CenterWindow(480, 280);
        }

        public void Close()
        {
            _state      = WindowState.Hidden;
            _joining    = false;
            _closeTimer = 0f;
        }

        // ----------------------------------------------------------------
        // IMGUI
        // ----------------------------------------------------------------

        // Cached delegates (avoids re-boxing every frame in IL2CPP)
        private GUI.WindowFunction? _hostFunc;
        private GUI.WindowFunction? _joinFunc;

        // Styles + textures — lazy-initialized on first OnGUI call
        private bool _stylesReady = false;

        private GUIStyle? _winStyle;
        private GUIStyle? _titleStyle;
        private GUIStyle? _labelStyle;
        private GUIStyle? _smallLabelStyle;
        private GUIStyle? _codeBoxStyle;
        private GUIStyle? _btnStyle;
        private GUIStyle? _btnConfirmStyle;
        private GUIStyle? _inputStyle;
        private GUIStyle? _statusStyle;

        private Texture2D? _bgTex;
        private Texture2D? _accentTex;
        private Texture2D? _dimTex;
        private Texture2D? _btnNormalTex;
        private Texture2D? _btnHoverTex;
        private Texture2D? _btnConfirmTex;
        private Texture2D? _btnConfirmHoverTex;
        private Texture2D? _inputNormalTex;
        private Texture2D? _inputFocusTex;
        private Texture2D? _codeBoxTex;

        private Rect _windowRect;

        // Cairn-inspired palette
        private static readonly Color C_BG            = new Color(0.07f, 0.07f, 0.09f, 0.97f);
        private static readonly Color C_ACCENT        = new Color(0.80f, 0.71f, 0.55f, 1.00f); // warm stone
        private static readonly Color C_TEXT          = new Color(0.92f, 0.92f, 0.92f, 1.00f);
        private static readonly Color C_SUBTLE        = new Color(0.55f, 0.55f, 0.55f, 1.00f);
        private static readonly Color C_BTN           = new Color(0.17f, 0.17f, 0.22f, 1.00f);
        private static readonly Color C_BTN_HOVER     = new Color(0.27f, 0.27f, 0.35f, 1.00f);
        private static readonly Color C_BTN_OK        = new Color(0.20f, 0.32f, 0.20f, 1.00f);
        private static readonly Color C_BTN_OK_HOVER  = new Color(0.28f, 0.45f, 0.28f, 1.00f);
        private static readonly Color C_INPUT         = new Color(0.13f, 0.13f, 0.17f, 1.00f);
        private static readonly Color C_INPUT_FOCUS   = new Color(0.17f, 0.17f, 0.23f, 1.00f);
        private static readonly Color C_CODE_BOX      = new Color(0.10f, 0.10f, 0.13f, 1.00f);
        private static readonly Color C_DIM           = new Color(0.00f, 0.00f, 0.00f, 0.55f);

        private void OnGUI()
        {
            if (_state == WindowState.Hidden) return;
            if (!_stylesReady) InitStyles();

            // Full-screen dim
            var prev = GUI.color;
            GUI.color = C_DIM;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _dimTex!);
            GUI.color = prev;

            // GUI.Window (not GUILayout.Window — Unity 6 IL2CPP LayoutedWindow ctor mismatch)
            if (_state == WindowState.Host && _hostFunc != null)
                _windowRect = GUI.Window(9901, _windowRect, _hostFunc, GUIContent.none, _winStyle!);
            else if (_state == WindowState.Join && _joinFunc != null)
                _windowRect = GUI.Window(9902, _windowRect, _joinFunc, GUIContent.none, _winStyle!);
        }

        // ----------------------------------------------------------------
        // Host window
        // ----------------------------------------------------------------
        private void DrawHostContents(int id)
        {
            DrawTitle("CAIRNCOOP  ·  SESSION HOSTEN");

            GUILayout.Label("Teile diese Steam-ID mit deinen Mitspielern:", _labelStyle!);
            GUILayout.Space(8);

            // Big code box
            GUILayout.BeginHorizontal();
            GUILayout.Label(_hostCode, _codeBoxStyle!);
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            if (GUILayout.Button("  ↑  In Zwischenablage kopieren", _btnStyle!))
            {
                GUIUtility.systemCopyBuffer = _hostCode;
                Log.LogInfo("Session code copied to clipboard.");
            }

            GUILayout.Space(16);
            DrawSeparator();
            GUILayout.Space(10);

            // Player list
            var net = NetworkManager.Instance;
            if (net?.Session != null)
            {
                int count = 0;
                foreach (var _ in net.Session.ConnectedSlots) count++;

                GUILayout.Label($"Spieler: {count} / {net.Session.MaxPlayers}", _smallLabelStyle!);
                GUILayout.Space(4);

                foreach (var slot in net.Session.ConnectedSlots)
                {
                    string tag = slot.IsHost ? "  [Host]" : "";
                    string you = slot.SlotIndex == net.Session.LocalSlot ? "  ← Du" : "";
                    GUILayout.Label($"  · Slot {slot.SlotIndex + 1}{tag}{you}", _smallLabelStyle!);
                }
            }
            else
            {
                GUILayout.Label("Session wird gestartet...", _smallLabelStyle!);
            }

            GUILayout.Space(14);
            DrawSeparator();
            GUILayout.Space(10);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Schließen  [ESC]", _btnStyle!)) Close();
            GUILayout.EndHorizontal();

            HandleEsc();
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 40));
        }

        // ----------------------------------------------------------------
        // Join window
        // ----------------------------------------------------------------
        private void DrawJoinContents(int id)
        {
            DrawTitle("CAIRNCOOP  ·  BEITRETEN");

            GUILayout.Label("Steam-ID des Hosts:", _labelStyle!);
            GUILayout.Space(6);

            // Custom text input — GUILayout.TextField calls TextEditor.SaveBackup() which is
            // IL2CPP-stripped in Cairn's build and throws NotSupportedException every frame.
            // Instead we draw a GUI.Box for visual and handle raw KeyDown events ourselves.
            Rect inputRect = GUILayoutUtility.GetRect(
                GUIContent.none, _inputStyle!,
                GUILayout.ExpandWidth(true), GUILayout.Height(42));
            bool blink = ((int)(Time.realtimeSinceStartup * 2)) % 2 == 0;
            GUI.Box(inputRect, _joinInput + (blink ? "|" : " "), _inputStyle!);
            HandleTextInput();

            GUILayout.Space(12);

            // Status
            GUILayout.Label(_joinStatus, _statusStyle!);

            GUILayout.Space(16);
            DrawSeparator();
            GUILayout.Space(10);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            bool canJoin = !_joining && _joinInput.Length >= 10;
            GUI.enabled = canJoin;
            if (GUILayout.Button("Verbinden", _btnConfirmStyle!))
                StartJoin();
            GUI.enabled = true;

            GUILayout.Space(8);
            if (GUILayout.Button("Abbrechen  [ESC]", _btnStyle!)) Close();
            GUILayout.EndHorizontal();

            HandleEsc();
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 40));
        }

        private void StartJoin()
        {
            var net = NetworkManager.Instance;
            if (net == null) { _joinStatus = "Fehler: Netzwerk nicht bereit."; return; }

            string code = _joinInput.Trim();
            if (!ulong.TryParse(code, out _))
            {
                _joinStatus = "Ungültige ID — nur Zahlen (z.B. 76561198...)";
                return;
            }

            // Persist for next launch
            SaveCode(code);

            _joining    = true;
            _joinTimer  = 0f;
            _joinStatus = "Verbinde mit Host...";
            net.JoinSession(code);
        }

        // ----------------------------------------------------------------
        // Drawing helpers
        // ----------------------------------------------------------------
        private void DrawTitle(string text)
        {
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(text, _titleStyle!);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            // Accent separator
            Rect sep = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(1));
            if (Event.current.type == EventType.Repaint && _accentTex != null)
                GUI.DrawTexture(sep, _accentTex);

            GUILayout.Space(14);
        }

        private void DrawSeparator()
        {
            Rect sep = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(1));
            if (Event.current.type == EventType.Repaint && _accentTex != null)
            {
                var old = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.12f);
                GUI.DrawTexture(sep, _accentTex);
                GUI.color = old;
            }
        }

        private void HandleEsc()
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                Close();
                Event.current.Use();
            }
        }

        /// <summary>
        /// Manual keyboard-event driven text input for _joinInput.
        /// Called from DrawJoinContents (inside a GUI.Window callback).
        ///
        /// We never call GUI.TextField / GUILayout.TextField because
        /// TextEditor.SaveBackup() is IL2CPP-stripped in Cairn's Unity build
        /// and throws NotSupportedException on every frame.
        ///
        /// Instead the calling code renders a GUI.Box for the visual, and this
        /// method reads raw KeyDown events from Event.current to edit _joinInput.
        /// </summary>
        private void HandleTextInput()
        {
            var evt = Event.current;
            if (evt == null) return;
            if (evt.type != EventType.KeyDown) return;

            switch (evt.keyCode)
            {
                case KeyCode.Backspace:
                    if (_joinInput.Length > 0)
                        _joinInput = _joinInput.Substring(0, _joinInput.Length - 1);
                    evt.Use();
                    break;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (!_joining && _joinInput.Length >= 10)
                        StartJoin();
                    evt.Use();
                    break;

                default:
                    // Append any printable character.
                    // Steam IDs are 17-digit numbers; non-digit chars simply fail the
                    // ulong.TryParse validation in StartJoin() and are shown as-is,
                    // so we don't need to restrict input here.
                    char c = evt.character;
                    if (c != '\0' && !char.IsControl(c))
                    {
                        _joinInput += c;
                        evt.Use();
                    }
                    break;
            }
        }

        private void CenterWindow(float w, float h)
        {
            _windowRect = new Rect(
                (Screen.width  - w) * 0.5f,
                (Screen.height - h) * 0.5f,
                w, h);
        }

        // ----------------------------------------------------------------
        // Persistence helpers (read/write join_code.txt)
        // ----------------------------------------------------------------
        private static string CodeFilePath()
        {
            string dir = System.IO.Path.GetDirectoryName(
                typeof(CoopMenuUI).Assembly.Location) ?? ".";
            return System.IO.Path.Combine(dir, "join_code.txt");
        }

        private static string ReadSavedCode()
        {
            try
            {
                string p = CodeFilePath();
                return System.IO.File.Exists(p)
                    ? System.IO.File.ReadAllText(p).Trim()
                    : "";
            }
            catch { return ""; }
        }

        private static void SaveCode(string code)
        {
            try { System.IO.File.WriteAllText(CodeFilePath(), code); }
            catch { /* non-critical */ }
        }

        // ----------------------------------------------------------------
        // Style initialisation (must run inside OnGUI due to GUI.skin)
        // ----------------------------------------------------------------
        private void InitStyles()
        {
            _bgTex           = MakeTex(C_BG);
            _accentTex       = MakeTex(C_ACCENT);
            _dimTex          = MakeTex(Color.white);
            _btnNormalTex    = MakeTex(C_BTN);
            _btnHoverTex     = MakeTex(C_BTN_HOVER);
            _btnConfirmTex   = MakeTex(C_BTN_OK);
            _btnConfirmHoverTex = MakeTex(C_BTN_OK_HOVER);
            _inputNormalTex  = MakeTex(C_INPUT);
            _inputFocusTex   = MakeTex(C_INPUT_FOCUS);
            _codeBoxTex      = MakeTex(C_CODE_BOX);

            // Window background — set ALL states to avoid null-texture DrawTexture warnings
            // when hovering / clicking anywhere on the window area.
            _winStyle = new GUIStyle(GUI.skin.box);
            _winStyle.normal.background  = _bgTex;
            _winStyle.hover.background   = _bgTex;
            _winStyle.active.background  = _bgTex;
            _winStyle.focused.background = _bgTex;
            _winStyle.border  = new RectOffset(4, 4, 4, 4);
            _winStyle.padding = new RectOffset(24, 24, 18, 18);

            // Section title
            _titleStyle = new GUIStyle(GUI.skin.label);
            _titleStyle.fontSize  = 15;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.normal.textColor = C_ACCENT;
            _titleStyle.alignment = TextAnchor.MiddleCenter;
            _titleStyle.wordWrap  = false;

            // Body label
            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 13;
            _labelStyle.normal.textColor = C_TEXT;
            _labelStyle.wordWrap = false;

            // Small secondary label
            _smallLabelStyle = new GUIStyle(GUI.skin.label);
            _smallLabelStyle.fontSize = 12;
            _smallLabelStyle.normal.textColor = C_SUBTLE;
            _smallLabelStyle.wordWrap = false;

            // Code display box
            _codeBoxStyle = new GUIStyle(GUI.skin.box);
            _codeBoxStyle.normal.background = _codeBoxTex;
            _codeBoxStyle.normal.textColor  = C_ACCENT;
            _codeBoxStyle.fontSize   = 20;
            _codeBoxStyle.fontStyle  = FontStyle.Bold;
            _codeBoxStyle.alignment  = TextAnchor.MiddleCenter;
            _codeBoxStyle.stretchWidth = true;
            _codeBoxStyle.padding    = new RectOffset(14, 14, 12, 12);

            // Generic button
            _btnStyle = new GUIStyle(GUI.skin.button);
            _btnStyle.normal.background = _btnNormalTex;
            _btnStyle.normal.textColor  = C_TEXT;
            _btnStyle.hover.background  = _btnHoverTex;
            _btnStyle.hover.textColor   = Color.white;
            _btnStyle.active.background  = _btnHoverTex;
            _btnStyle.focused.background = _btnNormalTex;  // keyboard focus — no null texture
            _btnStyle.focused.textColor  = Color.white;
            _btnStyle.fontSize  = 13;
            _btnStyle.padding   = new RectOffset(16, 16, 8, 8);
            _btnStyle.alignment = TextAnchor.MiddleCenter;

            // Confirm / primary button (green)
            _btnConfirmStyle = new GUIStyle(_btnStyle);
            _btnConfirmStyle.normal.background  = _btnConfirmTex;
            _btnConfirmStyle.hover.background   = _btnConfirmHoverTex;
            _btnConfirmStyle.active.background  = _btnConfirmHoverTex;
            _btnConfirmStyle.focused.background = _btnConfirmTex;
            _btnConfirmStyle.normal.textColor   = Color.white;
            _btnConfirmStyle.hover.textColor    = Color.white;

            // Text input
            _inputStyle = new GUIStyle(GUI.skin.textField);
            _inputStyle.normal.background  = _inputNormalTex;
            _inputStyle.normal.textColor   = Color.white;
            _inputStyle.focused.background = _inputFocusTex;
            _inputStyle.focused.textColor  = Color.white;
            _inputStyle.hover.background   = _inputFocusTex;
            _inputStyle.hover.textColor    = Color.white;
            _inputStyle.active.background  = _inputFocusTex;  // prevent null-texture on click
            _inputStyle.active.textColor   = Color.white;
            _inputStyle.fontSize  = 17;
            _inputStyle.alignment = TextAnchor.MiddleLeft;
            _inputStyle.padding   = new RectOffset(12, 12, 10, 10);
            _inputStyle.stretchWidth = true;

            // Status / hint text
            _statusStyle = new GUIStyle(GUI.skin.label);
            _statusStyle.fontSize = 12;
            _statusStyle.normal.textColor = C_SUBTLE;
            _statusStyle.wordWrap  = true;
            _statusStyle.alignment = TextAnchor.UpperLeft;

            _stylesReady = true;
        }

        private static Texture2D MakeTex(Color col)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, col);
            t.Apply();
            return t;
        }
    }
}
