using System;
using MelonLoader;
using UnityEngine;

namespace FrontierArchitect
{
    /// <summary>
    /// Frontier Architect: Assistant (report), Architect (redesign scenario) and Execute (phased
    /// build programme), powered by Claude through the player's own Anthropic API key.
    /// </summary>
    public sealed class FrontierArchitectMod : MelonMod
    {
        private Controller _c;
        private bool _inGame;
        private GameObject _overlayHost;
        private KeyCode _hudKey = KeyCode.F8, _overlayKey = KeyCode.F9, _lockKey = KeyCode.L;

        public override void OnInitializeMelon()
        {
            try
            {
                _c = new Controller();
                _c.Init();
                UI.Windows.C = _c;
                _hudKey = ParseKey(_c.Config.ToggleHudKey, KeyCode.F8);
                _overlayKey = ParseKey(_c.Config.ToggleOverlayKey, KeyCode.F9);
                _lockKey = ParseKey(_c.Config.LockKey, KeyCode.L);
                LoggerInstance.Msg("Frontier Architect v" + BuildInfo.Version + " ready. " + _c.SyncSummary +
                                   (string.IsNullOrWhiteSpace(_c.Config.ApiKey) ? " Add your Anthropic API key in the Settings panel." : ""));
                if (!string.IsNullOrEmpty(_c.Update.Message)) LoggerInstance.Msg("Updater: " + _c.Update.Message);
            }
            catch (Exception e)
            {
                LoggerInstance.Error("Frontier Architect failed to start: " + e);
            }
        }

        private static KeyCode ParseKey(string s, KeyCode def)
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), s, true); } catch { return def; }
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (_c == null) return;
            _inGame = string.Equals(sceneName, _c.Map.Scene, StringComparison.OrdinalIgnoreCase) || _c.Access.InGame;
            if (!_inGame) return;
            if (_overlayHost == null)
            {
                _overlayHost = new GameObject("FrontierArchitectOverlay");
                UnityEngine.Object.DontDestroyOnLoad(_overlayHost);
                _overlayHost.AddComponent<UI.Overlay>();
            }
            try { _c.OnGameSceneReady(); }
            catch (Exception e) { LoggerInstance.Warning("scene setup: " + e.Message); }
        }

        public override void OnUpdate()
        {
            if (_c == null) return;
            MainThread.Drain();
            if (!_inGame) return;
            if (Input.GetKeyDown(_hudKey)) UI.Windows.HudVisible = !UI.Windows.HudVisible;
            if (Input.GetKeyDown(_overlayKey)) UI.Overlay.Enabled = !UI.Overlay.Enabled;
            if (Input.GetKeyDown(_lockKey) && GUIUtility.keyboardControl == 0) _c.LockOrTestUnderCursor();
            _c.Tick();
        }

        public override void OnGUI()
        {
            if (_c == null || !_inGame) return;
            UI.Windows.Draw();
        }

        public override void OnApplicationQuit()
        {
            try { _c?.Cancel(); _c?.SavePlan(); } catch { }
        }
    }
}
