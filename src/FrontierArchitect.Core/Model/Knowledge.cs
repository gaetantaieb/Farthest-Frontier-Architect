using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FrontierArchitect.Core.Data;

namespace FrontierArchitect.Core.Model
{
    public sealed class BuildingSpec
    {
        public string Type;
        public string Category = "other";
        public int Tier = 1;
        public int W = 2, H = 2;
        public float WorkRadius, ServiceRadius;
        public string ServiceType;
        public Dictionary<string, double> Cost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public double BuildDays = 10;
        public int MaxWorkers;
        /// <summary>Resource type the building must sit on or beside (mines, pits, fishing). Null = free.</summary>
        public string FixedResource;
        /// <summary>Desirability effect on nearby houses (negative = pollution) and its radius.</summary>
        public float Desirability, DesirabilityRadius;
        public bool ProducesFood, ProducesFirewood;
        public JObject Raw = new JObject();

        public JObject ToJson()
        {
            var cost = new JObject();
            foreach (var kv in Cost) cost.Set(kv.Key, kv.Value);
            var o = new JObject().Set("type", Type).Set("category", Category).Set("tier", Tier)
                .Set("footprint", new JArray { (long)W, (long)H });
            if (cost.Count > 0) o.Set("cost", cost);
            o.Set("build_days", BuildDays);
            if (MaxWorkers > 0) o.Set("max_workers", MaxWorkers);
            if (WorkRadius > 0) o.Set("work_radius", WorkRadius);
            if (ServiceRadius > 0) o.Set("service", new JObject().Set("type", ServiceType).Set("radius", ServiceRadius));
            if (FixedResource != null) o.Set("fixed_resource", FixedResource);
            if (Math.Abs(Desirability) > 0.001f) o.Set("desirability", new JObject().Set("value", Desirability).Set("radius", DesirabilityRadius));
            if (ProducesFood) o.Set("produces_food", true);
            if (ProducesFirewood) o.Set("produces_firewood", true);
            return o;
        }
    }

    public sealed class CropSpec
    {
        public string Name;
        public string Group = "";
        /// <summary>Change in field fertility per year it is grown (0..1 scale).</summary>
        public float FertilityDelta;
        /// <summary>Weed reduction per year (0..1 scale); 0 = none.</summary>
        public float WeedSuppression;
        public string Disease;
        public float IdealClay = 0.5f;
        public float BaseYield = 1f;
        public bool NeedsHarvest = true;
        public string HarvestSeason = "autumn";
        public bool Approximate = true;
    }

    /// <summary>Classifies building types from their names when the game data does not say.</summary>
    public sealed class CategoryRule
    {
        public Regex Pattern;
        public string Category;
        public string ServiceType;
        public float ServiceRadius;
        public string FixedResource;
        public float Desirability;
        public float DesirabilityRadius;
        public bool Food, Firewood;
    }

    /// <summary>
    /// Static game knowledge: building catalogue (extracted from game data at load), crop table and
    /// classification rules (from the editable agents/shared/*.yaml files).
    /// </summary>
    public sealed class KnowledgePack
    {
        public string GameVersion = "";
        public readonly Dictionary<string, BuildingSpec> Buildings = new Dictionary<string, BuildingSpec>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, CropSpec> Crops = new Dictionary<string, CropSpec>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<int, List<string>> TierUnlocks = new Dictionary<int, List<string>>();
        public readonly List<CategoryRule> Rules = new List<CategoryRule>();
        public readonly HashSet<string> TierOneTwoCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string Notes = "";
        /// <summary>District templates (agents/shared/districts.yaml): name → {purpose, hub, edge, away, spacing, guidance…}.</summary>
        public JObject Districts = new JObject();

        /// <summary>Loads agents/shared/districts.yaml.</summary>
        public void LoadDistricts(JObject doc)
        {
            Districts = doc?.Obj("districts") ?? new JObject();
        }

        public BuildingSpec Spec(string type)
        {
            if (type == null) return null;
            BuildingSpec s;
            if (Buildings.TryGetValue(type, out s)) return s;
            // unknown type: classify by name so validation and scoring still work
            s = new BuildingSpec { Type = type };
            Classify(s);
            return s;
        }

        public CropSpec Crop(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            CropSpec c;
            return Crops.TryGetValue(name.Trim(), out c) ? c : null;
        }

        public void Classify(BuildingSpec s)
        {
            foreach (var r in Rules)
            {
                if (!r.Pattern.IsMatch(s.Type)) continue;
                if (s.Category == "other" || string.IsNullOrEmpty(s.Category)) s.Category = r.Category;
                if (s.ServiceType == null && r.ServiceType != null) s.ServiceType = r.ServiceType;
                if (s.ServiceRadius <= 0 && r.ServiceRadius > 0) s.ServiceRadius = r.ServiceRadius;
                if (s.FixedResource == null && r.FixedResource != null) s.FixedResource = r.FixedResource;
                if (Math.Abs(s.Desirability) < 0.001f && Math.Abs(r.Desirability) > 0.001f)
                {
                    s.Desirability = r.Desirability;
                    s.DesirabilityRadius = r.DesirabilityRadius;
                }
                s.ProducesFood |= r.Food;
                s.ProducesFirewood |= r.Firewood;
                break;
            }
        }

        public void ApplyTo(BuildingInfo b)
        {
            var s = Spec(b.Type);
            if (b.Category == "other" || string.IsNullOrEmpty(b.Category)) b.Category = s.Category;
            if (b.ServiceRadius <= 0) b.ServiceRadius = s.ServiceRadius;
            if (b.ServiceType == null) b.ServiceType = s.ServiceType;
            if (b.WorkRadius <= 0) b.WorkRadius = s.WorkRadius;
            if (b.WorkersMax <= 0) b.WorkersMax = s.MaxWorkers;
            if (b.Tier <= 0) b.Tier = s.Tier;
        }

        public bool IsTierOneTwo(string type)
        {
            var s = Spec(type);
            return s.Tier <= 2;
        }

        // ---------------------------------------------------------------- loading rules

        /// <summary>Loads agents/shared/buildings.yaml (category rules) — see that file for the format.</summary>
        public void LoadRules(JObject doc)
        {
            Rules.Clear();
            foreach (var r in doc.Arr("rules").Objects())
            {
                string pattern = r.Str("match");
                if (string.IsNullOrEmpty(pattern)) continue;
                Rules.Add(new CategoryRule
                {
                    Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    Category = r.Str("category", "other"),
                    ServiceType = r.Str("service"),
                    ServiceRadius = (float)r.Num("service_radius", 0),
                    FixedResource = r.Str("fixed_resource"),
                    Desirability = (float)r.Num("desirability", 0),
                    DesirabilityRadius = (float)r.Num("desirability_radius", 0),
                    Food = r.Bool("food"),
                    Firewood = r.Bool("firewood"),
                });
            }
            foreach (var kv in doc.Obj("default_specs") ?? new JObject())
            {
                if (!(kv.Value is JObject o)) continue;
                if (Buildings.ContainsKey(kv.Key)) continue;
                var s = new BuildingSpec
                {
                    Type = kv.Key,
                    Category = o.Str("category", "other"),
                    Tier = o.Int("tier", 1),
                    W = o.Int("w", 2),
                    H = o.Int("h", 2),
                    WorkRadius = (float)o.Num("work_radius", 0),
                    ServiceRadius = (float)o.Num("service_radius", 0),
                    ServiceType = o.Str("service"),
                    MaxWorkers = o.Int("max_workers", 0),
                    BuildDays = o.Num("build_days", 10),
                    FixedResource = o.Str("fixed_resource"),
                };
                foreach (var c in o.Obj("cost") ?? new JObject()) s.Cost[c.Key] = Json.ToDouble(c.Value);
                Classify(s);
                Buildings[kv.Key] = s;
            }
        }

        /// <summary>Loads agents/shared/crops.yaml.</summary>
        public void LoadCrops(JObject doc)
        {
            foreach (var kv in doc.Obj("crops") ?? new JObject())
            {
                if (!(kv.Value is JObject o)) continue;
                Crops[kv.Key] = new CropSpec
                {
                    Name = kv.Key,
                    Group = o.Str("group", ""),
                    FertilityDelta = (float)o.Num("fertility_delta", -0.05),
                    WeedSuppression = (float)o.Num("weed_suppression", 0),
                    Disease = o.Str("disease"),
                    IdealClay = (float)o.Num("ideal_clay", 0.5),
                    BaseYield = (float)o.Num("base_yield", 1),
                    NeedsHarvest = o.Bool("needs_harvest", true),
                    HarvestSeason = o.Str("harvest", "autumn"),
                    Approximate = o.Bool("approximate", true),
                };
            }
        }

        // ---------------------------------------------------------------- prompt text

        /// <summary>
        /// Deterministic text sent as the cached system block (sorted keys, no timestamps), so
        /// the prefix stays byte-identical between runs of the same game version.
        /// </summary>
        public string ToPromptText()
        {
            var sb = new StringBuilder();
            sb.Append("# Game knowledge pack (game version ").Append(GameVersion).Append(")\n\n");
            sb.Append("## Building catalogue\n");
            var keys = new List<string>(Buildings.Keys);
            keys.Sort(StringComparer.Ordinal);
            var arr = new JArray();
            foreach (var k in keys) arr.Add(Buildings[k].ToJson());
            sb.Append(Json.Write(arr)).Append("\n\n");
            sb.Append("## Crops\n");
            var crops = new List<string>(Crops.Keys);
            crops.Sort(StringComparer.Ordinal);
            var ca = new JArray();
            foreach (var k in crops)
            {
                var c = Crops[k];
                ca.Add(new JObject().Set("crop", c.Name).Set("group", c.Group).Set("fertility_delta", c.FertilityDelta)
                    .Set("weed_suppression", c.WeedSuppression).Set("disease", c.Disease).Set("ideal_clay", c.IdealClay)
                    .Set("base_yield", c.BaseYield).Set("needs_harvest", c.NeedsHarvest).Set("approximate", c.Approximate));
            }
            sb.Append(Json.Write(ca)).Append("\n\n");
            if (TierUnlocks.Count > 0)
            {
                sb.Append("## Unlocks by town tier\n");
                var tiers = new List<int>(TierUnlocks.Keys);
                tiers.Sort();
                foreach (var t in tiers) sb.Append("- tier ").Append(t).Append(": ").Append(string.Join(", ", TierUnlocks[t])).Append('\n');
                sb.Append('\n');
            }
            if (Districts.Count > 0)
            {
                sb.Append("## District templates (place_group template=<name>)\n");
                foreach (var name in Districts.Keys.OrderBy(x => x, StringComparer.Ordinal))
                    sb.Append("- ").Append(name).Append(": ").Append(Json.Write(Districts.Obj(name))).Append('\n');
                sb.Append('\n');
            }
            if (!string.IsNullOrEmpty(Notes)) sb.Append(Notes).Append('\n');
            return sb.ToString();
        }
    }
}
