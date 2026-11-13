using System;
using System.Collections.Generic;
using System.Linq;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Model;

namespace FrontierArchitect.Core.Planning
{
    public sealed class PhaseForecast
    {
        public int Phase;
        public double FoodMonthsStart, FoodMonthsEnd, FirewoodMonthsStart, FirewoodMonthsEnd;
        public Dictionary<string, double> MaterialsAfter = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public List<string> Breaches = new List<string>();

        public JObject ToJson()
        {
            var mats = new JObject();
            foreach (var kv in MaterialsAfter.OrderBy(k => k.Key, StringComparer.Ordinal)) mats.Set(kv.Key, Math.Round(kv.Value, 1));
            return new JObject().Set("phase", Phase)
                .Set("food_months_start", Math.Round(FoodMonthsStart, 2)).Set("food_months_end", Math.Round(FoodMonthsEnd, 2))
                .Set("firewood_months_start", Math.Round(FirewoodMonthsStart, 2)).Set("firewood_months_end", Math.Round(FirewoodMonthsEnd, 2))
                .Set("materials_after", mats).Set("breaches", Breaches.ToJArray());
        }
    }

    /// <summary>
    /// Projects food, firewood and building materials through a plan in Native mode (monthly
    /// steps). Rates come from the snapshot metrics; offline food/firewood producers reduce output
    /// for the phase's duration.
    /// </summary>
    public static class StockForecast
    {
        public static List<PhaseForecast> Run(VillageSnapshot snap, IList<Phase> phases, double foodThresholdMonths, double firewoodThresholdMonths, double daysPerMonth = 30)
        {
            var m = snap.Metrics;
            var food = m.Obj("food");
            var winter = m.Obj("winter");
            double foodStock = food.Num("stock_total", 0);
            double foodUse = Math.Max(0.01, food.Num("consumption_per_month", Math.Max(1, m.Obj("population").Num("count", 10)) * 1.0));
            double foodProd = food.Num("production_per_month", foodUse);
            double wood = winter.Num("firewood", snap.StockOf("Firewood"));
            double woodBurn = Math.Max(0.01, winter.Num("firewood_burn_per_month", Math.Max(1, m.Obj("population").Num("count", 10)) * 0.5));
            double woodProd = winter.Num("firewood_production_per_month", woodBurn);

            var materials = new Dictionary<string, double>(snap.Stock, StringComparer.OrdinalIgnoreCase);
            var materialProd = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in m.Obj("economy")?.Obj("production_per_month") ?? new JObject()) materialProd[kv.Key] = Json.ToDouble(kv.Value);

            var foodProducers = snap.Buildings.Where(b => snap.Knowledge.Spec(b.Type).ProducesFood).Select(b => b.Id).ToList();
            var woodProducers = snap.Buildings.Where(b => snap.Knowledge.Spec(b.Type).ProducesFirewood).Select(b => b.Id).ToList();

            var result = new List<PhaseForecast>();
            foreach (var p in phases)
            {
                var f = new PhaseForecast { Phase = p.N, FoodMonthsStart = foodStock / foodUse, FirewoodMonthsStart = wood / woodBurn };
                foreach (var kv in p.EstCost)
                {
                    double v = Json.ToDouble(kv.Value);
                    if (kv.Key == "build_days" || kv.Key == "labour_days" || kv.Key == "road_tiles") continue;
                    double have;
                    materials.TryGetValue(kv.Key, out have);
                    materials[kv.Key] = have - v;
                }
                double months = Math.Max(0.25, p.EstDays / daysPerMonth);
                var offline = new HashSet<string>(p.Offline);
                foreach (var a in p.Actions)
                    if ((a.Op == Ops.Relocate || a.Op == Ops.Demolish) && a.Target != null) offline.Add(a.Target);
                double foodShare = foodProducers.Count == 0 ? 0 : foodProducers.Count(offline.Contains) / (double)foodProducers.Count;
                double woodShare = woodProducers.Count == 0 ? 0 : woodProducers.Count(offline.Contains) / (double)woodProducers.Count;
                foodStock = Math.Max(0, foodStock + (foodProd * (1 - foodShare) - foodUse) * months);
                wood = Math.Max(0, wood + (woodProd * (1 - woodShare) - woodBurn) * months);
                foreach (var kv in materialProd)
                {
                    double have;
                    materials.TryGetValue(kv.Key, out have);
                    materials[kv.Key] = have + kv.Value * months;
                }
                f.FoodMonthsEnd = foodStock / foodUse;
                f.FirewoodMonthsEnd = wood / woodBurn;
                // A breach is a phase that takes food/firewood producers offline while stock ends below
                // the threshold. A village that is already short is the guardrail monitor's job (it pauses).
                if (f.FoodMonthsEnd < foodThresholdMonths && foodShare > 0)
                    f.Breaches.Add("food falls to " + Math.Round(f.FoodMonthsEnd, 1) + " months with " + Math.Round(foodShare * 100) + "% of food producers offline (threshold " + foodThresholdMonths + ")");
                if (f.FirewoodMonthsEnd < firewoodThresholdMonths && woodShare > 0)
                    f.Breaches.Add("firewood falls to " + Math.Round(f.FirewoodMonthsEnd, 1) + " months with " + Math.Round(woodShare * 100) + "% of firewood producers offline (threshold " + firewoodThresholdMonths + ")");
                foreach (var kv in materials)
                    if (kv.Value < -0.5 && p.EstCost.ContainsKey(kv.Key)) f.Breaches.Add(kv.Key + " short by " + Math.Round(-kv.Value));
                f.MaterialsAfter = new Dictionary<string, double>(materials, StringComparer.OrdinalIgnoreCase);
                result.Add(f);
            }
            return result;
        }
    }
}
