using System;
using System.Globalization;

namespace FrontierArchitect.Core.Updates
{
    /// <summary>Semantic version (major.minor.patch[-prerelease]); build metadata is ignored.</summary>
    public sealed class SemVer : IComparable<SemVer>
    {
        public int Major, Minor, Patch;
        public string Pre = "";

        public static SemVer Parse(string s)
        {
            SemVer v;
            if (!TryParse(s, out v)) throw new FormatException("Not a version: " + s);
            return v;
        }

        public static bool TryParse(string s, out SemVer v)
        {
            v = null;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("v") || s.StartsWith("V")) s = s.Substring(1);
            int plus = s.IndexOf('+');
            if (plus >= 0) s = s.Substring(0, plus);
            string pre = "";
            int dash = s.IndexOf('-');
            if (dash >= 0) { pre = s.Substring(dash + 1); s = s.Substring(0, dash); }
            var parts = s.Split('.');
            if (parts.Length < 1 || parts.Length > 4) return false;
            int[] n = new int[3];
            for (int i = 0; i < Math.Min(3, parts.Length); i++)
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])) return false;
            v = new SemVer { Major = n[0], Minor = n[1], Patch = n[2], Pre = pre };
            return true;
        }

        public int CompareTo(SemVer o)
        {
            if (o == null) return 1;
            int c = Major.CompareTo(o.Major); if (c != 0) return c;
            c = Minor.CompareTo(o.Minor); if (c != 0) return c;
            c = Patch.CompareTo(o.Patch); if (c != 0) return c;
            // a release sorts after its prereleases
            if (Pre.Length == 0 && o.Pre.Length > 0) return 1;
            if (Pre.Length > 0 && o.Pre.Length == 0) return -1;
            var a = Pre.Split('.'); var b = o.Pre.Split('.');
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                bool an = int.TryParse(a[i], out int ai), bn = int.TryParse(b[i], out int bi);
                if (an && bn) { c = ai.CompareTo(bi); }
                else if (an) c = -1;
                else if (bn) c = 1;
                else c = string.CompareOrdinal(a[i], b[i]);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }

        public bool IsNewerThan(SemVer other) => CompareTo(other) > 0;

        public override string ToString() => Major + "." + Minor + "." + Patch + (Pre.Length > 0 ? "-" + Pre : "");
    }
}
