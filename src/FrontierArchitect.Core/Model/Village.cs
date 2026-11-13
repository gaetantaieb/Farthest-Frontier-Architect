using System;
using System.Collections.Generic;
using FrontierArchitect.Core.Data;

namespace FrontierArchitect.Core.Model
{
    public struct Cell : IEquatable<Cell>
    {
        public int X, Z;
        public Cell(int x, int z) { X = x; Z = z; }
        public bool Equals(Cell o) => X == o.X && Z == o.Z;
        public override bool Equals(object obj) => obj is Cell c && Equals(c);
        public override int GetHashCode() => (X * 73856093) ^ (Z * 19349663);
        public override string ToString() => "[" + X + "," + Z + "]";
        public double DistanceTo(Cell o) => Math.Sqrt((X - o.X) * (double)(X - o.X) + (Z - o.Z) * (double)(Z - o.Z));
        public JArray ToJson() => new JArray { (long)X, (long)Z };

        public static bool TryParse(object v, out Cell c)
        {
            c = default(Cell);
            if (v is JArray a && a.Count >= 2 && Json.IsNumber(a[0]) && Json.IsNumber(a[1]))
            {
                c = new Cell((int)Math.Round(Json.ToDouble(a[0])), (int)Math.Round(Json.ToDouble(a[1])));
                return true;
            }
            if (v is JObject o && o.ContainsKey("x") && o.ContainsKey("z"))
            {
                c = new Cell((int)Math.Round(o.Num("x")), (int)Math.Round(o.Num("z")));
                return true;
            }
            return false;
        }
    }

    /// <summary>Axis-aligned tile rectangle: X,Z is the min corner; W along X, H along Z.</summary>
    public struct Rect
    {
        public int X, Z, W, H;
        public Rect(int x, int z, int w, int h) { X = x; Z = z; W = w; H = h; }
        public int MaxX => X + W - 1;
        public int MaxZ => Z + H - 1;
        public int Area => W * H;
        public bool Contains(Cell c) => c.X >= X && c.X <= MaxX && c.Z >= Z && c.Z <= MaxZ;
        public bool Overlaps(Rect o) => X <= o.MaxX && o.X <= MaxX && Z <= o.MaxZ && o.Z <= MaxZ;
        public Cell Center => new Cell(X + W / 2, Z + H / 2);
        public IEnumerable<Cell> Cells()
        {
            for (int z = Z; z <= MaxZ; z++)
                for (int x = X; x <= MaxX; x++)
                    yield return new Cell(x, z);
        }
        public JArray ToJson() => new JArray { (long)X, (long)Z, (long)W, (long)H };
        public override string ToString() => "[" + X + "," + Z + "," + W + "," + H + "]";

        public static bool TryParse(object v, out Rect r)
        {
            r = default(Rect);
            if (v is JArray a && a.Count >= 4)
            {
                r = new Rect((int)Json.ToLong(a[0]), (int)Json.ToLong(a[1]), (int)Json.ToLong(a[2]), (int)Json.ToLong(a[3]));
                return r.W > 0 && r.H > 0;
            }
            if (v is JObject o && o.ContainsKey("w"))
            {
                r = new Rect(o.Int("x"), o.Int("z"), o.Int("w"), o.Int("h"));
                return r.W > 0 && r.H > 0;
            }
            return false;
        }
    }

    public enum Terrain : byte { Land = 0, Water = 1, Cliff = 2, Edge = 3, Unknown = 255 }

    /// <summary>Tile grid of the map. Claude reads it through get_region; it is never sent whole.</summary>
    public sealed class TileGrid
    {
        public int Width, Height;
        /// <summary>World units per tile and world position of tile (0,0)'s min corner.</summary>
        public float TileSize = 1f, OriginX, OriginZ;
        public Terrain[] Terrain;
        public float[] Slope;      // degrees
        public float[] Elevation;  // world height (for the overlay)
        public float[] Fertility;  // 0..1, NaN when unknown
        public float[] Clay;       // 0..1 share of clay, NaN when unknown
        public float[] Sand;
        public byte[] Clutter;     // 0 none, 1 trees, 2 rocks

        public TileGrid(int w, int h)
        {
            Width = w; Height = h;
            int n = w * h;
            Terrain = new Terrain[n];
            Slope = new float[n];
            Elevation = new float[n];
            Fertility = new float[n];
            Clay = new float[n];
            Sand = new float[n];
            Clutter = new byte[n];
            for (int i = 0; i < n; i++) { Fertility[i] = float.NaN; Clay[i] = float.NaN; Sand[i] = float.NaN; }
        }

        public bool In(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Height;
        public bool In(Cell c) => In(c.X, c.Z);
        public int Idx(int x, int z) => z * Width + x;
        public int Idx(Cell c) => c.Z * Width + c.X;

        public Cell WorldToCell(float wx, float wz) =>
            new Cell((int)Math.Floor((wx - OriginX) / TileSize), (int)Math.Floor((wz - OriginZ) / TileSize));

        public void CellCenterWorld(Cell c, out float wx, out float wz)
        {
            wx = OriginX + (c.X + 0.5f) * TileSize;
            wz = OriginZ + (c.Z + 0.5f) * TileSize;
        }

        public bool IsLand(Cell c) => In(c) && Terrain[Idx(c)] == Model.Terrain.Land;
    }

    public sealed class BuildingInfo
    {
        public string Id;
        public string Type;
        public string Category = "other";
        public int Tier = 1;
        /// <summary>Min corner of the footprint on the tile grid.</summary>
        public int X, Z;
        /// <summary>Rotation in quarter turns (0..3).</summary>
        public int Rot;
        /// <summary>Footprint at rotation 0.</summary>
        public int W = 2, H = 2;
        public int WorkersAssigned, WorkersMax;
        public Cell? WorkCircleCenter;
        public float WorkRadius;
        public float ServiceRadius;
        public string ServiceType;
        public List<string> Inputs = new List<string>();
        public List<string> Outputs = new List<string>();
        public JObject ProductionLimit = new JObject();
        public string Status = "active";
        public bool Locked;
        public bool IsNew;
        /// <summary>Game-side handle (the live object); never serialised.</summary>
        [NonSerialized] public object Handle;
        public JObject Extra = new JObject();

        public Rect Footprint
        {
            get
            {
                bool swap = (Rot & 1) == 1;
                return new Rect(X, Z, swap ? H : W, swap ? W : H);
            }
        }

        public Cell Center => Footprint.Center;

        /// <summary>Tile just outside the footprint on the side the door faces (rot 0 = -Z side).</summary>
        public Cell EntranceCell
        {
            get
            {
                var r = Footprint;
                switch (Rot & 3)
                {
                    case 0: return new Cell(r.X + r.W / 2, r.Z - 1);
                    case 1: return new Cell(r.MaxX + 1, r.Z + r.H / 2);
                    case 2: return new Cell(r.X + r.W / 2, r.MaxZ + 1);
                    default: return new Cell(r.X - 1, r.Z + r.H / 2);
                }
            }
        }

        public BuildingInfo Clone()
        {
            var b = (BuildingInfo)MemberwiseClone();
            b.Inputs = new List<string>(Inputs);
            b.Outputs = new List<string>(Outputs);
            b.ProductionLimit = ProductionLimit.Clone();
            b.Extra = Extra.Clone();
            return b;
        }

        public JObject ToJson()
        {
            var o = new JObject()
                .Set("id", Id).Set("type", Type).Set("category", Category).Set("tier", Tier)
                .Set("pos", new Cell(X, Z).ToJson()).Set("rot", Rot).Set("footprint", new JArray { (long)Footprint.W, (long)Footprint.H })
                .Set("entrance", EntranceCell.ToJson())
                .Set("workers", new JObject().Set("assigned", WorkersAssigned).Set("max", WorkersMax));
            if (WorkRadius > 0)
                o.Set("work_circle", new JObject().Set("center", (WorkCircleCenter ?? Center).ToJson()).Set("radius", Math.Round(WorkRadius, 1)));
            if (ServiceRadius > 0) o.Set("service", new JObject().Set("type", ServiceType).Set("radius", Math.Round(ServiceRadius, 1)));
            o.Set("inputs", Inputs.ToJArray()).Set("outputs", Outputs.ToJArray())
             .Set("production_limit", ProductionLimit).Set("status", Status).Set("locked", Locked);
            if (Extra.Count > 0) o.Set("extra", Extra);
            return o;
        }
    }

    public sealed class FieldInfo
    {
        public string Id;
        public Rect Rect;
        public float Fertility = float.NaN, Weeds, Rocks;
        public float Clay = float.NaN, Sand = float.NaN;
        public List<List<string>> Rotation = new List<List<string>> { new List<string>(), new List<string>(), new List<string>() };
        public int CurrentYear = 1;
        public string CropInGround = "";
        public List<string> Diseases = new List<string>();
        public int Workers;
        public string FarmId;
        public bool Locked;
        [NonSerialized] public object Handle;

        public FieldInfo Clone()
        {
            var f = (FieldInfo)MemberwiseClone();
            f.Rotation = new List<List<string>>();
            foreach (var y in Rotation) f.Rotation.Add(new List<string>(y));
            f.Diseases = new List<string>(Diseases);
            return f;
        }

        public JObject ToJson()
        {
            var rot = new JArray();
            foreach (var y in Rotation) rot.Add(y.ToJArray());
            return new JObject()
                .Set("id", Id).Set("rect", Rect.ToJson())
                .Set("fertility", R(Fertility)).Set("weeds", R(Weeds)).Set("rocks", R(Rocks))
                .Set("soil", new JObject().Set("clay", R(Clay)).Set("sand", R(Sand)))
                .Set("rotation", rot).Set("current_year", CurrentYear)
                .Set("crop_in_ground", CropInGround).Set("diseases", Diseases.ToJArray())
                .Set("workers", Workers).Set("farm", FarmId).Set("locked", Locked);
        }

        private static object R(float v) => float.IsNaN(v) ? null : (object)Math.Round(v, 3);
    }

    public sealed class RoadInfo
    {
        public string Type = "dirt";
        public List<Cell> Tiles = new List<Cell>();

        public JObject ToJson()
        {
            var t = new JArray();
            foreach (var c in Tiles) t.Add(c.ToJson());
            return new JObject().Set("type", Type).Set("tiles", t);
        }
    }

    public sealed class ResourceNode
    {
        public string Type;
        public Cell Pos;
        public double Remaining;
        public JObject ToJson() => new JObject().Set("type", Type).Set("pos", Pos.ToJson()).Set("remaining", Math.Round(Remaining, 1));
    }

    public sealed class SnapshotMeta
    {
        public string GameVersion = "", ModVersion = "", SettlementName = "", SaveName = "";
        public int Year = 1, Day, Month, TownTier = 1;
        public string Season = "spring";
        /// <summary>0..1 position inside the current season (for "late autumn").</summary>
        public float SeasonProgress;
        public bool GroundFrozen;
        public string Difficulty = "";

        public JObject ToJson(TileGrid grid) => new JObject()
            .Set("game_version", GameVersion).Set("mod_version", ModVersion)
            .Set("settlement", SettlementName)
            .Set("year", Year).Set("season", Season).Set("season_progress", Math.Round(SeasonProgress, 2))
            .Set("month", Month).Set("day", Day).Set("ground_frozen", GroundFrozen)
            .Set("town_tier", TownTier).Set("difficulty", Difficulty)
            .Set("map_size", new JArray { (long)(grid?.Width ?? 0), (long)(grid?.Height ?? 0) });
    }

    /// <summary>
    /// Immutable picture of the village, built on the Unity main thread and then read from
    /// background threads by the Claude loop and the tool server.
    /// </summary>
    public sealed class VillageSnapshot
    {
        public SnapshotMeta Meta = new SnapshotMeta();
        public JObject Metrics = new JObject();
        public List<BuildingInfo> Buildings = new List<BuildingInfo>();
        public List<FieldInfo> Fields = new List<FieldInfo>();
        public List<RoadInfo> Roads = new List<RoadInfo>();
        public List<ResourceNode> Resources = new List<ResourceNode>();
        public Dictionary<string, double> Stock = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public List<string> Unlocked = new List<string>();
        public TileGrid Grid;
        public KnowledgePack Knowledge = new KnowledgePack();
        /// <summary>What could not be read from the game (shown to Claude so it does not guess).</summary>
        public List<string> Gaps = new List<string>();

        public BuildingInfo Building(string id)
        {
            foreach (var b in Buildings) if (b.Id == id) return b;
            return null;
        }

        public FieldInfo Field(string id)
        {
            foreach (var f in Fields) if (f.Id == id) return f;
            return null;
        }

        public HashSet<Cell> RoadCells()
        {
            var set = new HashSet<Cell>();
            foreach (var r in Roads) foreach (var c in r.Tiles) set.Add(c);
            return set;
        }

        /// <summary>The snapshot as sent to Claude (section 7 contract). The tile grid is not included.</summary>
        public JObject ToJson(bool includeRoads = true)
        {
            var b = new JArray(); foreach (var x in Buildings) b.Add(x.ToJson());
            var f = new JArray(); foreach (var x in Fields) f.Add(x.ToJson());
            var r = new JArray(); if (includeRoads) foreach (var x in Roads) r.Add(x.ToJson());
            var res = new JArray(); foreach (var x in Resources) res.Add(x.ToJson());
            var stock = new JObject();
            var keys = new List<string>(Stock.Keys); keys.Sort(StringComparer.Ordinal);
            foreach (var k in keys) stock.Set(k, Math.Round(Stock[k], 1));
            var o = new JObject()
                .Set("meta", Meta.ToJson(Grid))
                .Set("metrics", Metrics)
                .Set("buildings", b).Set("fields", f).Set("roads", r).Set("resources", res)
                .Set("stock", stock)
                .Set("unlocked_buildings", Unlocked.ToJArray());
            if (Gaps.Count > 0) o.Set("data_gaps", Gaps.ToJArray());
            return o;
        }

        public double StockOf(string item)
        {
            double v;
            return Stock.TryGetValue(item, out v) ? v : 0;
        }
    }
}
