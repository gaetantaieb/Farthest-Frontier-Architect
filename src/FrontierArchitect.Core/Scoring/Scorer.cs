using System;
using System.Collections.Generic;
using System.Linq;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Layout;
using FrontierArchitect.Core.Model;
using FrontierArchitect.Core.Roads;

namespace FrontierArchitect.Core.Scoring
{
    public sealed class RotationYear
    {
        public int Year;
        public List<string> Crops = new List<string>();
        public double FertilityStart, FertilityEnd, Weeds, DiseaseRisk, Yield;
    }

    public sealed class RotationResult
    {
        public List<RotationYear> Years = new List<RotationYear>();
        public double Score; // 0..1
        public List<string> Notes = new List<string>();

        public JObject ToJson()
        {
            var ys = new JArray();
            foreach (var y in Years)
                ys.Add(new JObject().Set("year", y.Year).Set("crops", y.Crops.ToJArray())
                    .Set("fertility_start", Math.Round(y.FertilityStart, 3)).Set("fertility_end", Math.Round(y.FertilityEnd, 3))
                    .Set("weeds", Math.Round(y.Weeds, 3)).Set("disease_risk", Math.Round(y.DiseaseRisk, 3))
                    .Set("yield_index", Math.Round(y.Yield, 3)));
            return new JObject().Set("years", ys).Set("score", Math.Round(Score, 3)).Set("notes", Notes.ToJArray())
                .Set("note", "Crop effects come from agents/shared/crops.yaml; values marked approximate are estimates, not game data.");
        }
    }

    /// <summary>Projects fertility, weeds, disease risk and yield over one 3-year cycle.</summary>
    public static class RotationEvaluator
    {
        public static RotationResult Evaluate(FieldInfo field, List<List<string>> rotation, KnowledgePack k, IEnumerable<FieldInfo> neighbours = null)
        {
            var res = new RotationResult();
            double fert = float.IsNaN(field.Fertility) ? 0.5 : field.Fertility;
            double weeds = field.Weeds;
            double rocks = field.Rocks;
            double clay = float.IsNaN(field.Clay) ? 0.5 : field.Clay;
            var neighbourDiseases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (neighbours != null)
                foreach (var n in neighbours)
                    foreach (var y in n.Rotation)
                        foreach (var c in y) { var cs = k.Crop(c); if (cs?.Disease != null) neighbourDiseases.Add(cs.Disease); }
            var diseaseCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in field.Diseases) diseaseCount[d] = 1;

            double total = 0;
            for (int year = 0; year < 3; year++)
            {
                var crops = year < rotation.Count ? rotation[year] : new List<string>();
                var ry = new RotationYear { Year = year + 1, Crops = crops, FertilityStart = fert };
                double yieldSum = 0; double fertDelta = 0; double suppression = 0; double risk = 0;
                int planted = 0;
                foreach (var name in crops)
                {
                    if (string.Equals(name, "fallow", StringComparison.OrdinalIgnoreCase)) { fertDelta += 0.06; continue; }
                    if (string.Equals(name, "maintenance", StringComparison.OrdinalIgnoreCase)) { suppression += 0.25; rocks = Math.Max(0, rocks - 0.2); continue; }
                    var c = k.Crop(name);
                    if (c == null) { res.Notes.Add("unknown crop " + name); continue; }
                    planted++;
                    fertDelta += c.FertilityDelta;
                    suppression += c.WeedSuppression;
                    double soilMatch = 1.0 + 0.10 * (1.0 - Math.Min(1.0, Math.Abs(clay - c.IdealClay) * 2)); // up to +10%
                    double y = c.BaseYield * (0.4 + 0.6 * fert) * (1 - weeds * 0.7) * (1 - rocks * 0.5) * soilMatch;
                    if (c.Disease != null)
                    {
                        int cnt;
                        diseaseCount.TryGetValue(c.Disease, out cnt);
                        double r = Math.Min(1.0, cnt * 0.35 + (neighbourDiseases.Contains(c.Disease) ? 0.15 : 0));
                        risk = Math.Max(risk, r);
                        y *= 1 - r * 0.6;
                        diseaseCount[c.Disease] = cnt + 1;
                    }
                    if (c.NeedsHarvest) yieldSum += y;
                }
                // diseases fade from soil when the host crop is not grown
                foreach (var key in diseaseCount.Keys.ToList())
                    if (!crops.Any(n => k.Crop(n)?.Disease == key)) diseaseCount[key] = Math.Max(0, diseaseCount[key] - 1);
                if (planted > 1) { fertDelta /= planted; suppression /= Math.Max(1, planted); }
                fert = Clamp01(fert + fertDelta);
                weeds = Clamp01(weeds + 0.08 - suppression);
                ry.FertilityEnd = fert;
                ry.Weeds = weeds;
                ry.DiseaseRisk = risk;
                ry.Yield = yieldSum;
                res.Years.Add(ry);
                total += yieldSum;
            }
            double fertTrend = res.Years.Count > 0 ? res.Years[res.Years.Count - 1].FertilityEnd - res.Years[0].FertilityStart : 0;
            double avgYield = total / 3.0;
            double avgRisk = res.Years.Count == 0 ? 0 : res.Years.Average(y => y.DiseaseRisk);
            res.Score = Clamp01(0.55 * Math.Min(1.0, avgYield) + 0.25 * Clamp01(0.5 + fertTrend * 3) + 0.2 * (1 - avgRisk));
            if (fertTrend < -0.05) res.Notes.Add("rotation drains fertility; add a nitrogen-fixing crop or clover");
            if (avgRisk > 0.3) res.Notes.Add("high disease risk: same disease family repeats or a neighbour grows it");
            return res;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }

    public sealed class ScoreCard
    {
        public readonly Dictionary<string, double> Objectives = new Dictionary<string, double>();
        public readonly Dictionary<string, List<string>> Details = new Dictionary<string, List<string>>();
        public double Weighted;
        public readonly List<string> Violations = new List<string>();
        public NetworkResult Roads;

        public JObject ToJson()
        {
            var o = new JObject();
            foreach (var key in new[] { "O1", "O2", "O3", "O4", "O5", "O6" })
            {
                double v;
                Objectives.TryGetValue(key, out v);
                o.Set(key, Math.Round(v, 1));
            }
            var details = new JObject();
            foreach (var kv in Details) details.Set(kv.Key, kv.Value.Take(6).ToJArray());
            return new JObject().Set("objectives", o).Set("weighted", Math.Round(Weighted, 2))
                .Set("violations", Violations.ToJArray()).Set("details", details);
        }

        public JObject ObjectivesJson()
        {
            var o = new JObject();
            foreach (var key in new[] { "O1", "O2", "O3", "O4", "O5", "O6" }) o.Set(key, Math.Round(Objectives.TryGetValue(key, out double v) ? v : 0, 1));
            o.Set("weighted", Math.Round(Weighted, 2));
            return o;
        }
    }

    /// <summary>
    /// Deterministic 0–100 score per objective (O1 farming … O6 roads) used for the before/after
    /// figures and for Claude's score_draft tool. Heuristic by design: it ranks layouts, it does not
    /// simulate the game.
    /// </summary>
    public sealed class Scorer
    {
        private readonly RunSettings _s;
        public Scorer(RunSettings s) { _s = s; }

        public ScoreCard Score(DraftLayout d)
        {
            var card = new ScoreCard();
            var k = d.Knowledge;
            var live = d.LiveBuildings.ToList();
            var planner = new RoadPlanner(d, _s);

            // ---- O1 farming
            {
                var notes = new List<string>();
                double sum = 0, area = 0;
                int farmers = live.Where(b => b.Category == "farm").Sum(b => Math.Max(b.WorkersAssigned, 1));
                foreach (var f in d.Fields)
                {
                    double fert = float.IsNaN(f.Fertility) ? d.AverageFertility(f.Rect) : f.Fertility;
                    if (double.IsNaN(fert)) fert = 0.5;
                    var rot = RotationEvaluator.Evaluate(f, f.Rotation, k, d.Fields.Where(o => o != f && o.Rect.Center.DistanceTo(f.Rect.Center) < 16));
                    double s = 55 * fert + 35 * rot.Score + 10 * (1 - f.Weeds);
                    sum += s * f.Rect.Area; area += f.Rect.Area;
                    if (fert < 0.35) notes.Add(f.Id + " sits on low fertility (" + Math.Round(fert, 2) + ")");
                }
                double o1 = area > 0 ? sum / area : 50;
                // size the fields to the farmer pool: about 1 farmer per 20 tiles
                if (area > 0 && farmers > 0)
                {
                    double ratio = area / (farmers * 20.0);
                    if (ratio > 1.5) { o1 -= Math.Min(15, (ratio - 1.5) * 10); notes.Add("fields need more farmers than assigned"); }
                }
                card.Objectives["O1"] = Clamp(o1);
                card.Details["O1"] = notes;
            }

            // ---- O2 production logistics
            {
                var notes = new List<string>();
                var storage = live.Where(b => b.Category == "storage" || b.Category == "town_center").ToList();
                var producers = live.Where(b => b.Category == "production" || b.Category == "resource" || b.Category == "food").ToList();
                double distScore = 100;
                if (producers.Count > 0 && storage.Count > 0)
                {
                    double avg = producers.Average(p => storage.Min(s => s.Center.DistanceTo(p.Center)));
                    distScore = 100 * (1 - Math.Min(1, Math.Max(0, avg - 6) / 40));
                    foreach (var p in producers.OrderByDescending(p => storage.Min(s => s.Center.DistanceTo(p.Center))).Take(3))
                        notes.Add(p.Id + " is " + Math.Round(storage.Min(s => s.Center.DistanceTo(p.Center))) + " tiles from storage");
                }
                else if (producers.Count > 0) { distScore = 20; notes.Add("no storage building"); }
                double coverage = 100;
                var circles = live.Where(b => b.WorkRadius > 0).ToList();
                if (circles.Count > 0 && d.Snapshot.Resources.Count > 0)
                {
                    double covered = 0;
                    foreach (var b in circles)
                    {
                        var centre = b.WorkCircleCenter ?? b.Center;
                        var spec = k.Spec(b.Type);
                        string resType = spec.Raw.Str("resource") ?? spec.FixedResource;
                        var nodes = d.Snapshot.Resources.Where(r => resType == null || string.Equals(r.Type, resType, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (nodes.Count == 0) { covered += 1; continue; }
                        double inside = nodes.Where(r => r.Pos.DistanceTo(centre) <= b.WorkRadius).Sum(r => Math.Max(1, r.Remaining));
                        double all = nodes.Where(r => r.Pos.DistanceTo(centre) <= b.WorkRadius * 2.5).Sum(r => Math.Max(1, r.Remaining));
                        covered += all <= 0 ? 0.5 : Math.Min(1, inside / all);
                        if (all > 0 && inside / all < 0.4) notes.Add(b.Id + " work circle misses most nearby " + (resType ?? "resources"));
                    }
                    coverage = 100 * covered / circles.Count;
                }
                card.Objectives["O2"] = Clamp(0.65 * distScore + 0.35 * coverage);
                card.Details["O2"] = notes;
            }

            // ---- O3 residential services
            {
                var notes = new List<string>();
                var houses = live.Where(b => b.Category == "housing").ToList();
                var services = live.Where(b => b.ServiceType != null && b.ServiceRadius > 0).ToList();
                var types = services.Select(s => s.ServiceType).Distinct().ToList();
                foreach (var want in new[] { "market", "well" }) if (!types.Contains(want)) types.Add(want);
                double o3 = 50;
                if (houses.Count > 0)
                {
                    double cov = 0;
                    foreach (var h in houses)
                    {
                        int ok = 0;
                        foreach (var t in types)
                            if (services.Any(s => s.ServiceType == t && s.Center.DistanceTo(h.Center) <= s.ServiceRadius)) ok++;
                        cov += types.Count == 0 ? 1 : (double)ok / types.Count;
                    }
                    cov /= houses.Count;
                    // pollution: industry with negative desirability close to houses
                    double polluted = 0;
                    foreach (var h in houses)
                        foreach (var p in live)
                        {
                            var spec = k.Spec(p.Type);
                            if (spec.Desirability < 0 && p.Center.DistanceTo(h.Center) <= Math.Max(4, spec.DesirabilityRadius)) { polluted++; break; }
                        }
                    double pollutedShare = polluted / houses.Count;
                    o3 = 100 * cov * (1 - 0.35 * pollutedShare);
                    foreach (var t in types)
                    {
                        int miss = houses.Count(h => !services.Any(s => s.ServiceType == t && s.Center.DistanceTo(h.Center) <= s.ServiceRadius));
                        if (miss > 0) notes.Add(miss + "/" + houses.Count + " houses outside " + t + " coverage");
                    }
                    if (polluted > 0) notes.Add(polluted + " houses next to polluting industry");
                }
                card.Objectives["O3"] = Clamp(o3);
                card.Details["O3"] = notes;
            }

            // ---- O4 missing buildings (capacity gaps)
            {
                var notes = new List<string>();
                double o4 = 100;
                var m = d.Snapshot.Metrics;
                double pop = m.Obj("population").Num("count", live.Count(b => b.Category == "housing") * 4);
                double housingCap = m.Obj("population").Num("housing_capacity", 0);
                int newHouses = live.Count(b => b.IsNew && b.Category == "housing");
                housingCap += newHouses * k.Spec("House").Raw.Num("capacity", 4);
                if (housingCap > 0 && pop > housingCap) { o4 -= Math.Min(30, (pop - housingCap) * 2); notes.Add("housing short by " + (pop - housingCap)); }
                double fill = m.Obj("storage").Num("fill_ratio", 0);
                int newStorage = live.Count(b => b.IsNew && b.Category == "storage");
                if (fill > 0.85 && newStorage == 0) { o4 -= 15; notes.Add("storage " + Math.Round(fill * 100) + "% full"); }
                double monthsFood = m.Obj("food").Num("months_of_food", 12);
                int newFood = live.Count(b => b.IsNew && k.Spec(b.Type).ProducesFood);
                if (monthsFood < 3 && newFood == 0) { o4 -= 20; notes.Add("only " + Math.Round(monthsFood, 1) + " months of food"); }
                foreach (var svc in new[] { "well", "market", "healer" })
                    if (!live.Any(b => b.ServiceType == svc)) { o4 -= 8; notes.Add("no " + svc); }
                card.Objectives["O4"] = Clamp(o4);
                card.Details["O4"] = notes;
            }

            // ---- O5 defence
            {
                var notes = new List<string>();
                var protect = live.Where(b => b.Category == "housing" || b.Category == "storage").ToList();
                var towers = live.Where(b => b.Category == "defence" && b.ServiceRadius > 0).ToList();
                var walls = live.Where(b => b.Category == "wall" || b.Category == "gate").ToList();
                double towerCov = protect.Count == 0 ? 1 : protect.Count(p => towers.Any(t => t.Center.DistanceTo(p.Center) <= t.ServiceRadius)) / (double)protect.Count;
                double enclosed = 0;
                if (walls.Count >= 3 && protect.Count > 0)
                {
                    var hull = ConvexHull(walls.Select(w => w.Center).ToList());
                    enclosed = protect.Count(p => InsideHull(hull, p.Center)) / (double)protect.Count;
                }
                bool barracks = live.Any(b => b.Type.IndexOf("barrack", StringComparison.OrdinalIgnoreCase) >= 0);
                double o5 = walls.Count > 0 ? 45 * enclosed + 40 * towerCov + (barracks ? 15 : 0) : 70 * towerCov + (barracks ? 20 : 0) + 10;
                if (towerCov < 0.6) notes.Add(Math.Round((1 - towerCov) * 100) + "% of housing/storage outside tower range");
                if (walls.Count > 0 && enclosed < 0.8) notes.Add(Math.Round((1 - enclosed) * 100) + "% of housing/storage outside the walls");
                card.Objectives["O5"] = Clamp(o5);
                card.Details["O5"] = notes;
            }

            // ---- O6 roads
            {
                var notes = new List<string>();
                var conn = planner.CheckConnectivity();
                card.Roads = conn;
                double connectivity = conn.Connectivity;
                int buildings = Math.Max(1, live.Count);
                double tilesPer = d.Roads.Count / (double)buildings;
                double efficiency = 1 - Math.Min(1, Math.Max(0, tilesPer - 3) / 12);
                double o6 = 65 * connectivity + 35 * efficiency;
                if (conn.Unreachable.Count > 0)
                {
                    notes.Add(conn.Unreachable.Count + " buildings not connected: " + string.Join(", ", conn.Unreachable.Take(5)));
                    card.Violations.Add("connectivity " + Math.Round(connectivity * 100) + "% (required 100%)");
                }
                notes.Add(d.Roads.Count + " road tiles, " + Math.Round(tilesPer, 1) + " per building");
                card.Objectives["O6"] = Clamp(o6);
                card.Details["O6"] = notes;
            }

            double wsum = 0, total = 0;
            foreach (var kv in card.Objectives)
            {
                double w;
                if (!_s.Weights.TryGetValue(kv.Key, out w)) w = 1;
                wsum += w;
                total += w * kv.Value;
            }
            card.Weighted = wsum > 0 ? total / wsum : 0;
            return card;
        }

        private static double Clamp(double v) => v < 0 ? 0 : (v > 100 ? 100 : v);

        internal static List<Cell> ConvexHull(List<Cell> pts)
        {
            var p = pts.Distinct().OrderBy(c => c.X).ThenBy(c => c.Z).ToList();
            if (p.Count < 3) return p;
            Func<Cell, Cell, Cell, long> cross = (o, a, b) => (long)(a.X - o.X) * (b.Z - o.Z) - (long)(a.Z - o.Z) * (b.X - o.X);
            var lower = new List<Cell>();
            foreach (var c in p) { while (lower.Count >= 2 && cross(lower[lower.Count - 2], lower[lower.Count - 1], c) <= 0) lower.RemoveAt(lower.Count - 1); lower.Add(c); }
            var upper = new List<Cell>();
            for (int i = p.Count - 1; i >= 0; i--) { var c = p[i]; while (upper.Count >= 2 && cross(upper[upper.Count - 2], upper[upper.Count - 1], c) <= 0) upper.RemoveAt(upper.Count - 1); upper.Add(c); }
            lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        internal static bool InsideHull(List<Cell> hull, Cell c)
        {
            if (hull.Count < 3) return false;
            for (int i = 0; i < hull.Count; i++)
            {
                var a = hull[i]; var b = hull[(i + 1) % hull.Count];
                long cr = (long)(b.X - a.X) * (c.Z - a.Z) - (long)(b.Z - a.Z) * (c.X - a.X);
                if (cr < 0) return false;
            }
            return true;
        }
    }
}
