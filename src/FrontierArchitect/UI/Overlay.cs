using System.Collections.Generic;
using System.Linq;
using FrontierArchitect.Core.Model;
using FrontierArchitect.Core.Planning;
using UnityEngine;
using Rect = FrontierArchitect.Core.Model.Rect;
using Terrain = UnityEngine.Terrain;

namespace FrontierArchitect.UI
{
    /// <summary>
    /// Draws the scenario on the map: ghost footprints at target positions, arrows from old to new
    /// position, new roads, removed roads struck through and new field outlines. Colour = phase.
    /// </summary>
    internal sealed class Overlay : MonoBehaviour
    {
        public static bool Enabled = true;
        private static Material _mat;

        private static void EnsureMaterial()
        {
            if (_mat != null) return;
            var shader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
            if (shader == null) { Enabled = false; Log.Warn("overlay disabled: no line shader in this build"); return; }
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _mat.SetInt("_ZWrite", 0);
            _mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        }

        private void OnRenderObject()
        {
            var c = Windows.C;
            if (!Enabled || c == null || c.Plan == null || c.LastSnapshot?.Grid == null || !Windows.Architect.ShowOverlay) return;
            if (Camera.current != Camera.main) return;
            EnsureMaterial();
            if (_mat == null) return;
            var grid = c.LastSnapshot.Grid;
            var snap = c.LastSnapshot;
            _mat.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            int only = Windows.Architect.OverlayPhase;
            // zones (set_zones): a tinted outline per district, drawn under the actions
            foreach (var z in c.Plan.Zones) RectLines(grid, z.Rect, ZoneColour(z.Purpose), 1.2f);
            foreach (var p in c.Plan.Phases)
            {
                if (only != 0 && p.N != only) continue;
                var col = Styles.Phase(p.N);
                foreach (var a in p.Actions)
                {
                    if (a.Status == ActionStatus.Rejected) continue;
                    bool done = a.Status == ActionStatus.Done;
                    var colour = done ? new Color(col.r, col.g, col.b, 0.35f) : new Color(col.r, col.g, col.b, 0.9f);
                    switch (a.Op)
                    {
                        case Ops.Build:
                        case Ops.Relocate:
                            {
                                var spec = snap.Knowledge.Spec(a.Op == Ops.Build ? a.Target : snap.Building(a.Target)?.Type);
                                var existing = a.Op == Ops.Relocate ? snap.Building(a.Target) : null;
                                var ghost = new BuildingInfo { X = a.X, Z = a.Z, Rot = a.Rot, W = existing?.W ?? spec.W, H = existing?.H ?? spec.H };
                                RectLines(grid, ghost.Footprint, colour, 0.4f);
                                Cross(grid, ghost.Footprint, colour);
                                if (existing != null) Arrow(grid, existing.Center, ghost.Center, colour);
                                break;
                            }
                        case Ops.Demolish:
                            {
                                var b = snap.Building(a.Target);
                                if (b != null) { RectLines(grid, b.Footprint, new Color(1, 0.3f, 0.3f, 0.9f), 0.5f); Cross(grid, b.Footprint, new Color(1, 0.3f, 0.3f, 0.9f)); }
                                break;
                            }
                        case Ops.RoadAdd:
                            {
                                var t = a.Tiles();
                                for (int i = 1; i < t.Count; i++)
                                    if (Mathf.Abs(t[i].X - t[i - 1].X) + Mathf.Abs(t[i].Z - t[i - 1].Z) == 1) Line(grid, t[i - 1], t[i], colour, 0.3f);
                                foreach (var cell in t) Dot(grid, cell, colour);
                                break;
                            }
                        case Ops.RoadRemove:
                            foreach (var cell in a.Tiles()) Strike(grid, cell, new Color(1, 0.35f, 0.3f, 0.85f));
                            break;
                        case Ops.FieldCreate:
                        case Ops.FieldResize:
                        case Ops.ClearArea:
                        case Ops.Flatten:
                            {
                                Rect r;
                                if (a.TryRect(out r)) RectLines(grid, r, a.Op == Ops.Flatten ? new Color(0.8f, 0.6f, 0.3f, 0.9f) : colour, 0.2f);
                                break;
                            }
                        case Ops.FieldRemove:
                            {
                                var f = snap.Field(a.Target);
                                if (f != null) Cross(grid, f.Rect, new Color(1, 0.4f, 0.3f, 0.8f));
                                break;
                            }
                    }
                }
            }
            // locked buildings: white outline
            foreach (var id in c.Locked)
            {
                var b = snap.Building(id);
                if (b != null) RectLines(grid, b.Footprint, new Color(1, 1, 1, 0.8f), 0.6f);
            }
            GL.End();
            GL.PopMatrix();
        }

        private static Vector3 W(TileGrid g, float cx, float cz, float lift)
        {
            float x = g.OriginX + cx * g.TileSize, z = g.OriginZ + cz * g.TileSize;
            return new Vector3(x, Game.Ground.Height(x, z) + lift, z);
        }

        private static Color ZoneColour(string purpose)
        {
            switch (purpose)
            {
                case "housing": return new Color(1f, 0.85f, 0.3f, 0.8f);
                case "farming": return new Color(0.4f, 0.9f, 0.3f, 0.8f);
                case "industry": return new Color(0.9f, 0.45f, 0.2f, 0.8f);
                case "storage": return new Color(0.6f, 0.5f, 0.95f, 0.8f);
                case "services": return new Color(0.3f, 0.8f, 0.95f, 0.8f);
                case "defence": return new Color(0.95f, 0.25f, 0.25f, 0.8f);
                default: return new Color(0.8f, 0.8f, 0.8f, 0.6f);
            }
        }

        private static void Seg(Vector3 a, Vector3 b, Color c) { GL.Color(c); GL.Vertex(a); GL.Vertex(b); }

        private static void RectLines(TileGrid g, Rect r, Color c, float lift)
        {
            var p0 = W(g, r.X, r.Z, lift); var p1 = W(g, r.X + r.W, r.Z, lift);
            var p2 = W(g, r.X + r.W, r.Z + r.H, lift); var p3 = W(g, r.X, r.Z + r.H, lift);
            Seg(p0, p1, c); Seg(p1, p2, c); Seg(p2, p3, c); Seg(p3, p0, c);
        }

        private static void Cross(TileGrid g, Rect r, Color c)
        {
            Seg(W(g, r.X, r.Z, 0.4f), W(g, r.X + r.W, r.Z + r.H, 0.4f), c);
            Seg(W(g, r.X + r.W, r.Z, 0.4f), W(g, r.X, r.Z + r.H, 0.4f), c);
        }

        private static void Line(TileGrid g, Cell a, Cell b, Color c, float lift) =>
            Seg(W(g, a.X + 0.5f, a.Z + 0.5f, lift), W(g, b.X + 0.5f, b.Z + 0.5f, lift), c);

        private static void Dot(TileGrid g, Cell a, Color c)
        {
            Seg(W(g, a.X + 0.3f, a.Z + 0.5f, 0.3f), W(g, a.X + 0.7f, a.Z + 0.5f, 0.3f), c);
            Seg(W(g, a.X + 0.5f, a.Z + 0.3f, 0.3f), W(g, a.X + 0.5f, a.Z + 0.7f, 0.3f), c);
        }

        private static void Strike(TileGrid g, Cell a, Color c)
        {
            Seg(W(g, a.X + 0.1f, a.Z + 0.1f, 0.3f), W(g, a.X + 0.9f, a.Z + 0.9f, 0.3f), c);
            Seg(W(g, a.X + 0.9f, a.Z + 0.1f, 0.3f), W(g, a.X + 0.1f, a.Z + 0.9f, 0.3f), c);
        }

        private static void Arrow(TileGrid g, Cell from, Cell to, Color c)
        {
            var a = W(g, from.X + 0.5f, from.Z + 0.5f, 1.0f);
            var b = W(g, to.X + 0.5f, to.Z + 0.5f, 1.0f);
            Seg(a, b, c);
            var dir = (b - a);
            dir.y = 0;
            if (dir.sqrMagnitude < 0.01f) return;
            dir.Normalize();
            var side = new Vector3(-dir.z, 0, dir.x);
            float len = g.TileSize * 1.2f;
            Seg(b, b - dir * len + side * len * 0.5f, c);
            Seg(b, b - dir * len - side * len * 0.5f, c);
        }
    }
}
