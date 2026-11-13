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
        private string _scene = "";
        private float _nextDetect;
        private GameObject _overlayHost;
        private KeyCode _hudKey = KeyCode.F8, _overlayKey = KeyCode.F9, _lockKey = KeyCode.L;
        private bool _guiErrorLogged;

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
                                   " Buttons are at the top right of the screen (" + _hudKey + " shows/hides them)." +
                                   (string.IsNullOrWhiteSpace(_c.Config.ApiKey) ? " Add your Anthropic API key in Settings." : ""));
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
            _scene = sceneName ?? "";
            LoggerInstance.Msg("Scene '" + _scene + "' (index " + buildIndex + ") initialised.");
            _inGame = false;
            _nextDetect = 0;
        }

        /// <summary>
        /// The settlement is "ready" once the game's GameManager exists, whatever the scene is called.
        /// Checked every few seconds because the manager can appear after the scene event.
        /// </summary>
        private void DetectGame()
        {
            if (_inGame || Time.realtimeSinceStartup < _nextDetect) return;
            _nextDetect = Time.realtimeSinceStartup + 3f;
            bool found;
            try
            {
                _c.Access.ResetCache();
                found = _c.Access.InGame || string.Equals(_scene, _c.Map.Scene, StringComparison.OrdinalIgnoreCase);
            }
            catch { found = false; }
            if (!found) return;
            _inGame = true;
            LoggerInstance.Msg("Settlement detected in scene '" + _scene + "'.");
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
            if (Input.GetKeyDown(_hudKey)) UI.Windows.HudVisible = !UI.Windows.HudVisible;
            DetectGame();
            if (!_inGame) return;
            if (Input.GetKeyDown(_overlayKey)) UI.Overlay.Enabled = !UI.Overlay.Enabled;
            if (Input.GetKeyDown(_lockKey) && GUIUtility.keyboardControl == 0) _c.LockOrTestUnderCursor();
            _c.Tick();
        }

        public override void OnGUI()
        {
            if (_c == null) return;
            try
            {
                UI.Windows.InGame = _inGame;
                UI.Windows.Draw();
            }
            catch (Exception e)
            {
                if (!_guiErrorLogged) { _guiErrorLogged = true; LoggerInstance.Error("UI error: " + e); }
            }
        }

        public override void OnApplicationQuit()
        {
            try { _c?.Cancel(); _c?.SavePlan(); } catch { }
        }
    }
}
