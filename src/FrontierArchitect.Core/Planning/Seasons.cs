using System;
using System.Collections.Generic;
using System.Linq;

namespace FrontierArchitect.Core.Planning
{
    /// <summary>Season slots used for phase windows: spring, summer, early_autumn, late_autumn, winter.</summary>
    public static class Seasons
    {
        public static readonly string[] Slots = { "spring", "summer", "early_autumn", "late_autumn", "winter" };

        public static string Slot(string season, float progress)
        {
            switch ((season ?? "").ToLowerInvariant())
            {
                case "spring": return "spring";
                case "summer": return "summer";
                case "autumn":
                case "fall": return progress >= 0.5f ? "late_autumn" : "early_autumn";
                case "winter": return "winter";
            }
            return "spring";
        }

        /// <summary>Normalises a window list ("autumn" expands to both halves; "any"/"all" = every slot).</summary>
        public static HashSet<string> Normalise(IEnumerable<string> window)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in window ?? Enumerable.Empty<string>())
            {
                var s = (w ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
                if (s == "any" || s == "all" || s == "anytime") { foreach (var x in Slots) set.Add(x); continue; }
                if (s == "autumn" || s == "fall") { set.Add("early_autumn"); set.Add("late_autumn"); continue; }
                if (s == "late_fall") s = "late_autumn";
                if (s == "early_fall") s = "early_autumn";
                if (Slots.Contains(s)) set.Add(s);
            }
            return set;
        }

        public static bool IsFrozen(string slot) => slot == "winter";
    }
}
