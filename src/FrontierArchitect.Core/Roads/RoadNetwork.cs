using System;
using System.Collections.Generic;
using System.Linq;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Layout;
using FrontierArchitect.Core.Model;

namespace FrontierArchitect.Core.Roads
{
    /// <summary>Binary min-heap keyed by double (Dijkstra / A* open set).</summary>
    internal sealed class MinHeap
    {
        private readonly List<KeyValuePair<double, int>> _a = new List<KeyValuePair<double, int>>();
        public int Count => _a.Count;

        public void Push(double k, int v)
        {
            _a.Add(new KeyValuePair<double, int>(k, v));
            int i = _a.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (_a[p].Key <= _a[i].Key) break;
                var t = _a[p]; _a[p] = _a[i]; _a[i] = t; i = p;
            }
        }

        public int Pop(out double key)
        {
            var top = _a[0];
            var last = _a[_a.Count - 1];
            _a.RemoveAt(_a.Count - 1);
            if (_a.Count > 0)
            {
                _a[0] = last;
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < _a.Count && _a[l].Key < _a[m].Key) m = l;
                    if (r < _a.Count && _a[r].Key < _a[m].Key) m = r;
                    if (m == i) break;
                    var t = _a[m]; _a[m] = _a[i]; _a[i] = t; i = m;
                }
            }
            key = top.Key;
            return top.Value;
        }
    }

    public sealed class Terminal
    {
        public string Id;
        public Cell Cell;
        public bool IsRoot;
    }

    public sealed class NetworkResult
    {
        public HashSet<Cell> Tiles = new HashSet<Cell>();
        public int NewTiles, ReusedTiles, ShortcutTiles;
        public int Terminals, Connected;
        public List<string> Unreachable = new List<string>();
        public List<JObject> Trips = new List<JObject>();
        public double WorstDetour, MeanDetour;

        public double Connectivity => Terminals == 0 ? 1 : (double)Connected / Terminals;

        public JObject ToJson(bool includeTiles)
        {
            var o = new JObject()
                .Set("tile_count", Tiles.Count).Set("new_tiles", NewTiles).Set("reused_tiles", ReusedTiles)
                .Set("shortcut_tiles", ShortcutTiles)
                .Set("connectivity", Math.Round(Connectivity, 3))
                .Set("terminals", Terminals).Set("connected", Connected)
                .Set("unreachable", Unreachable.ToJArray())
                .Set("detour_mean", Math.Round(MeanDetour, 2)).Set("detour_worst", Math.Round(WorstDetour, 2))
                .Set("key_trips_over_limit", new JArray(Trips.Where(t => t.Bool("over_limit")).Take(20).Cast<object>()));
            if (includeTiles)
            {
                var t = new JArray();
                foreach (var c in Tiles.OrderBy(c => c.Z).ThenBy(c => c.X)) t.Add(c.ToJson());
                o.Set("tiles", t);
            }
            return o;
        }
    }

    /// <summary>
    /// Road design on the draft layout (spec O6): terminals → approximate minimum Steiner tree
    /// (shortest-path heuristic over terrain-weighted Dijkstra) → shortcuts for key trips over the
    /// detour limit → reuse of existing segments at no cost.
    /// </summary>
    public sealed class RoadPlanner
    {
        private readonly DraftLayout _d;
        private readonly TileGrid _g;
        private readonly RunSettings _s;

        public RoadPlanner(DraftLayout draft, RunSettings settings)
        {
            _d = draft; _g = draft.Grid; _s = settings;
        }

        // ---------------------------------------------------------------- costs

        /// <summary>Cost of laying road on a tile; +inf when impossible.</summary>
        public double LayCost(Cell c, ISet<Cell> network)
        {
            if (!_g.In(c)) return double.PositiveInfinity;
            int i = _g.Idx(c);
            if (_d.OwnerAt(c) != null) return double.PositiveInfinity;
            if (network != null && network.Contains(c)) return 0.0001;
            if (_s.ReuseExistingRoads && _d.Roads.Contains(c)) return 0.05;
            var t = _g.Terrain[i];
            if (t != Terrain.Land && !_d.Flattened.Contains(c)) return double.PositiveInfinity;
            double cost = 1.0 + Math.Max(0, _g.Slope[i]) * 0.06;
            if (_g.Clutter[i] != 0 && !_d.Cleared.Contains(c)) cost += 0.6;
            return cost;
        }

        /// <summary>Walking cost (roads are faster; buildings and water block).</summary>
        public double WalkCost(Cell c, ISet<Cell> roads)
        {
            if (!_g.In(c)) return double.PositiveInfinity;
            if (roads.Contains(c)) return 0.6;
            var owner = _d.OwnerAt(c);
            if (owner != null && !owner.StartsWith("field:")) return double.PositiveInfinity;
            var t = _g.Terrain[_g.Idx(c)];
            if (t == Terrain.Water || t == Terrain.Cliff || t == Terrain.Edge) return double.PositiveInfinity;
            return 1.0 + _g.Slope[_g.Idx(c)] * 0.03 + (owner != null ? 0.3 : 0);
        }

        // ---------------------------------------------------------------- terminals

        /// <summary>The road tile each building and field connects through.</summary>
        public List<Terminal> Terminals()
        {
            var list = new List<Terminal>();
            var root = _d.LiveBuildings.FirstOrDefault(b => b.Category == "town_center");
            foreach (var b in _d.LiveBuildings.OrderBy(b => b.Id, StringComparer.Ordinal))
            {
                if (b.Category == "wall" || b.Category == "decoration") continue;
                var cell = AccessCell(b.EntranceCell, b.Footprint);
                if (cell == null) { list.Add(new Terminal { Id = b.Id, Cell = b.EntranceCell, IsRoot = b == root }); continue; }
                list.Add(new Terminal { Id = b.Id, Cell = cell.Value, IsRoot = b == root });
            }
            foreach (var f in _d.Fields.OrderBy(f => f.Id, StringComparer.Ordinal))
            {
                var c = AccessCell(new Cell(f.Rect.X + f.Rect.W / 2, f.Rect.Z - 1), f.Rect);
                if (c != null) list.Add(new Terminal { Id = "field:" + f.Id, Cell = c.Value });
            }
            if (root == null && list.Count > 0) list[0].IsRoot = true;
            return list;
        }

        private Cell? AccessCell(Cell preferred, Rect around)
        {
            if (!double.IsInfinity(LayCost(preferred, null))) return preferred;
            foreach (var c in DraftLayout.Border(around, 1))
                if (!double.IsInfinity(LayCost(c, null))) return c;
            return null;
        }

        // ---------------------------------------------------------------- shortest paths

        /// <summary>Dijkstra from a set of sources until one of the targets is reached.</summary>
        private List<Cell> PathToNearest(HashSet<Cell> sources, HashSet<Cell> targets, Func<Cell, double> cost, int maxExpand = 400000)
        {
            int n = _g.Width * _g.Height;
            var dist = new Dictionary<int, double>();
            var prev = new Dictionary<int, int>();
            var heap = new MinHeap();
            foreach (var s in sources)
            {
                if (!_g.In(s)) continue;
                int i = _g.Idx(s);
                dist[i] = 0;
                heap.Push(0, i);
            }
            int expanded = 0;
            while (heap.Count > 0)
            {
                double d;
                int i = heap.Pop(out d);
                if (d > dist[i]) continue;
                var c = new Cell(i % _g.Width, i / _g.Width);
                if (targets.Contains(c)) return Reconstruct(prev, i);
                if (++expanded > maxExpand) break;
                foreach (var nb in DraftLayout.Neighbours(c))
                {
                    if (!_g.In(nb)) continue;
                    double step = targets.Contains(nb) ? Math.Min(1.0, cost(nb)) : cost(nb);
                    if (double.IsInfinity(step)) continue;
                    int j = _g.Idx(nb);
                    double nd = d + step;
                    double old;
                    if (!dist.TryGetValue(j, out old) || nd < old - 1e-12)
                    {
                        dist[j] = nd;
                        prev[j] = i;
                        heap.Push(nd, j);
                    }
                }
            }
            return null;
        }

        private List<Cell> Reconstruct(Dictionary<int, int> prev, int end)
        {
            var path = new List<Cell>();
            int cur = end;
            path.Add(new Cell(cur % _g.Width, cur / _g.Width));
            int p;
            while (prev.TryGetValue(cur, out p))
            {
                cur = p;
                path.Add(new Cell(cur % _g.Width, cur / _g.Width));
            }
            path.Reverse();
            return path;
        }

        /// <summary>A buildable road path between two cells, reusing existing road where cheaper.</summary>
        public List<Cell> RouteRoad(Cell from, Cell to)
        {
            return PathToNearest(new HashSet<Cell> { from }, new HashSet<Cell> { to }, c => LayCost(c, null));
        }

        /// <summary>Walking distance in tiles on the draft (roads included); -1 when unreachable.</summary>
        public double PathDistance(Cell a, Cell b, ISet<Cell> roads, out List<Cell> path)
        {
            path = PathToNearest(new HashSet<Cell> { a }, new HashSet<Cell> { b }, c => WalkCost(c, roads));
            if (path == null) return -1;
            double d = 0;
            for (int i = 1; i < path.Count; i++) d += WalkCost(path[i], roads) == 0.6 ? 0.6 : 1.0;
            return d;
        }

        // ---------------------------------------------------------------- network

        public NetworkResult Design(IList<Terminal> terminals = null, bool shortcuts = true)
        {
            terminals = terminals ?? Terminals();
            var res = new NetworkResult { Terminals = terminals.Count };
            if (terminals.Count == 0) return res;

            var root = terminals.FirstOrDefault(t => t.IsRoot) ?? terminals[0];
            var tree = new HashSet<Cell> { root.Cell };
            var pending = new HashSet<Cell>(terminals.Where(t => t != root).Select(t => t.Cell));
            pending.ExceptWith(tree);
            var byCell = new Dictionary<Cell, List<string>>();
            foreach (var t in terminals)
            {
                List<string> ids;
                if (!byCell.TryGetValue(t.Cell, out ids)) byCell[t.Cell] = ids = new List<string>();
                ids.Add(t.Id);
            }
            res.Connected = byCell[root.Cell].Count;

            // Shortest-path heuristic for the Steiner tree: repeatedly attach the nearest
            // unconnected terminal to the tree through the cheapest path.
            while (pending.Count > 0)
            {
                var path = PathToNearest(tree, pending, c => LayCost(c, tree));
                if (path == null) break;
                foreach (var c in path) tree.Add(c);
                        foreach (var c in path)
                    if (pending.Remove(c)) res.Connected += byCell.ContainsKey(c) ? byCell[c].Count : 0;
            }
            foreach (var c in pending)
                foreach (var id in byCell[c]) res.Unreachable.Add(id);

            res.Tiles = tree;
            if (shortcuts) AddShortcuts(res, terminals);
            res.ReusedTiles = res.Tiles.Count(c => _d.Roads.Contains(c));
            res.NewTiles = res.Tiles.Count - res.ReusedTiles;
            return res;
        }

        private void AddShortcuts(NetworkResult res, IList<Terminal> terminals)
        {
            var trips = KeyTrips(terminals);
            var detours = new List<double>();
            int added = 0;
            foreach (var trip in trips)
            {
                double straight = trip.Item1.Cell.DistanceTo(trip.Item2.Cell);
                if (straight < 4) continue;
                double onRoad = RoadDistance(res.Tiles, trip.Item1.Cell, trip.Item2.Cell);
                double ratio = onRoad < 0 ? double.PositiveInfinity : onRoad / straight;
                bool over = ratio > _s.DetourLimit;
                if (over && added < 40)
                {
                    // shortest connector, reusing the network for free
                    var path = PathToNearest(new HashSet<Cell> { trip.Item1.Cell }, new HashSet<Cell> { trip.Item2.Cell }, c => LayCost(c, res.Tiles));
                    if (path != null)
                    {
                        int fresh = path.Count(c => !res.Tiles.Contains(c));
                        double saved = onRoad < 0 ? straight * 2 : onRoad - path.Count;
                        // keep it only if the yearly walking saved outweighs its tiles at the configured weight
                        double yearlySaved = saved * trip.Item3;
                        if (fresh > 0 && yearlySaved >= fresh * _s.RoadTileWeight)
                        {
                            foreach (var c in path) res.Tiles.Add(c);
                            res.ShortcutTiles += fresh;
                            added++;
                            onRoad = RoadDistance(res.Tiles, trip.Item1.Cell, trip.Item2.Cell);
                            ratio = onRoad < 0 ? double.PositiveInfinity : onRoad / straight;
                            over = ratio > _s.DetourLimit;
                        }
                    }
                }
                if (!double.IsInfinity(ratio)) detours.Add(ratio);
                res.Trips.Add(new JObject().Set("from", trip.Item1.Id).Set("to", trip.Item2.Id)
                    .Set("straight", Math.Round(straight, 1)).Set("road", Math.Round(onRoad, 1))
                    .Set("ratio", double.IsInfinity(ratio) ? (object)null : Math.Round(ratio, 2)).Set("over_limit", over));
            }
            res.MeanDetour = detours.Count == 0 ? 1 : detours.Average();
            res.WorstDetour = detours.Count == 0 ? 1 : detours.Max();
        }

        /// <summary>BFS distance along road tiles; -1 when the two cells are not joined by road.</summary>
        public double RoadDistance(HashSet<Cell> roads, Cell a, Cell b)
        {
            if (!roads.Contains(a) || !roads.Contains(b)) return -1;
            var q = new Queue<Cell>();
            var dist = new Dictionary<Cell, int> { { a, 0 } };
            q.Enqueue(a);
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                if (c.Equals(b)) return dist[c];
                foreach (var n in DraftLayout.Neighbours(c))
                    if (roads.Contains(n) && !dist.ContainsKey(n)) { dist[n] = dist[c] + 1; q.Enqueue(n); }
            }
            return -1;
        }

        /// <summary>(from, to, trips per year) for the configured key trip kinds.</summary>
        public List<Tuple<Terminal, Terminal, double>> KeyTrips(IList<Terminal> terminals)
        {
            var map = terminals.ToDictionary(t => t.Id, t => t);
            Func<string, IEnumerable<BuildingInfo>> ofCat = cat => _d.LiveBuildings.Where(b => b.Category == cat && map.ContainsKey(b.Id));
            Func<string, IEnumerable<BuildingInfo>> ofService = svc => _d.LiveBuildings.Where(b => b.ServiceType == svc && map.ContainsKey(b.Id));
            var list = new List<Tuple<Terminal, Terminal, double>>();
            Action<IEnumerable<BuildingInfo>, IEnumerable<BuildingInfo>, double> nearest = (from, to, freq) =>
            {
                var targets = to.ToList();
                if (targets.Count == 0) return;
                foreach (var f in from)
                {
                    var best = targets.OrderBy(t => t.Center.DistanceTo(f.Center)).First();
                    if (best.Id == f.Id) continue;
                    list.Add(Tuple.Create(map[f.Id], map[best.Id], freq));
                }
            };
            foreach (var kind in _s.KeyTrips)
            {
                switch (kind)
                {
                    case "house_market": nearest(ofCat("housing"), ofService("market"), 50); break;
                    case "house_well": nearest(ofCat("housing"), ofService("well"), 100); break;
                    case "producer_storage":
                        nearest(_d.LiveBuildings.Where(b => (b.Category == "production" || b.Category == "resource" || b.Category == "food") && map.ContainsKey(b.Id)), ofCat("storage"), 80);
                        break;
                    case "storage_market": nearest(ofCat("storage"), ofService("market"), 60); break;
                    case "field_barn":
                        {
                            var barns = ofCat("storage").Concat(ofCat("farm")).ToList();
                            if (barns.Count == 0) break;
                            foreach (var t in terminals.Where(t => t.Id.StartsWith("field:")))
                            {
                                var best = barns.OrderBy(b => b.Center.DistanceTo(t.Cell)).First();
                                list.Add(Tuple.Create(t, map[best.Id], 30.0));
                            }
                            break;
                        }
                }
            }
            return list;
        }

        // ---------------------------------------------------------------- connectivity

        /// <summary>Share of terminals joined by road to the root, on the current draft roads.</summary>
        public NetworkResult CheckConnectivity(ISet<Cell> roads = null)
        {
            roads = roads ?? _d.Roads;
            var terms = Terminals();
            var res = new NetworkResult { Terminals = terms.Count };
            var root = terms.FirstOrDefault(t => t.IsRoot);
            if (root == null) return res;
            var start = roads.Contains(root.Cell) ? root.Cell : DraftLayout.Neighbours(root.Cell).FirstOrDefault(roads.Contains);
            var seen = new HashSet<Cell>();
            if (roads.Contains(start))
            {
                var q = new Queue<Cell>();
                q.Enqueue(start); seen.Add(start);
                while (q.Count > 0)
                {
                    var c = q.Dequeue();
                    foreach (var n in DraftLayout.Neighbours(c))
                        if (roads.Contains(n) && seen.Add(n)) q.Enqueue(n);
                }
            }
            foreach (var t in terms)
            {
                bool ok = seen.Contains(t.Cell) || DraftLayout.Neighbours(t.Cell).Any(seen.Contains);
                if (ok) res.Connected++;
                else res.Unreachable.Add(t.Id);
            }
            res.Tiles = new HashSet<Cell>(roads);
            return res;
        }
    }
}
