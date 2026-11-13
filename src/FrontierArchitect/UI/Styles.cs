using UnityEngine;

namespace FrontierArchitect.UI
{
    internal static class Styles
    {
        public static GUIStyle Title, Small, Wrap, Bold, Critical, Important, Optimisation, Good, Bad, Mono, Box, Button, Header;
        private static bool _ready;

        public static readonly Color[] PhaseColours =
        {
            new Color(0.30f, 0.75f, 1.00f), new Color(1.00f, 0.75f, 0.25f), new Color(0.55f, 0.90f, 0.45f),
            new Color(0.95f, 0.45f, 0.85f), new Color(0.95f, 0.95f, 0.40f), new Color(0.45f, 0.95f, 0.90f),
            new Color(1.00f, 0.55f, 0.45f), new Color(0.70f, 0.60f, 1.00f)
        };

        public static Color Phase(int n) => PhaseColours[(Mathf.Max(1, n) - 1) % PhaseColours.Length];

        public static void Ensure()
        {
            if (_ready) return;
            _ready = true;
            Wrap = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true };
            Small = new GUIStyle(Wrap) { fontSize = Mathf.Max(10, GUI.skin.label.fontSize - 2) };
            Bold = new GUIStyle(Wrap) { fontStyle = FontStyle.Bold };
            Title = new GUIStyle(Bold) { fontSize = GUI.skin.label.fontSize + 3 };
            Header = new GUIStyle(Bold) { fontSize = GUI.skin.label.fontSize + 1 };
            Critical = Coloured(new Color(1f, 0.35f, 0.3f));
            Important = Coloured(new Color(1f, 0.75f, 0.25f));
            Optimisation = Coloured(new Color(0.5f, 0.85f, 1f));
            Good = Coloured(new Color(0.45f, 0.95f, 0.45f));
            Bad = Coloured(new Color(1f, 0.45f, 0.4f));
            Mono = new GUIStyle(GUI.skin.textArea) { wordWrap = false, richText = false };
            Box = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, padding = new RectOffset(6, 6, 4, 4) };
            Button = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, padding = new RectOffset(10, 10, 6, 6) };
        }

        private static GUIStyle Coloured(Color c)
        {
            var s = new GUIStyle(Bold);
            s.normal.textColor = c;
            return s;
        }
    }
}
