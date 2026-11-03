#if DEBUG
using System;
using System.Collections.Generic;
using UnityEngine.AI;
using ActuallyWorkingEmployees.Features;
using ActuallyWorkingEmployees.Features.Reliability;
using Il2CppScheduleOne.Tiles;
using PropertyT = Il2CppScheduleOne.Property.Property;

namespace ActuallyWorkingEmployees.Debugging
{
    /// <summary>
    /// DEBUG-only test rig for the furniture switch: 'awe ovenrig place' builds a closed ring of eight lab ovens (each on
    /// its own table) on a free patch of an owned property, puts a cauldron outside the ring, and hires a chemist there
    /// when the property has none. 'awe ovenrig walk' warps the chemist into the ring and sends him to a point behind
    /// the cauldron, so the straight way leads through an oven and the cauldron. With the furniture obstacles active
    /// the ring is closed and he cannot get out; with them off he walks through. The result line names the path status,
    /// the time, the nearest player and the avoidance state the walk ran with.
    /// </summary>
    internal sealed class OvenRig : IFeature
    {
        public string Name => "ovenrig";

        // Region of free tiles the rig needs, in grid tiles of 0.5 m.
        private const int RegionW = 19, RegionH = 12;
        private const float WalkTimeout = 25f;

        // Lab oven footprint is 4 x 2 tiles. Rotation 0 covers (x..x+3, y..y+1); rotation 90 covers (x..x+1, y-3..y).
        private static readonly int[][] Ovens =
        {
            new[] { 3, 8, 0 }, new[] { 7, 8, 0 },     // top row
            new[] { 3, 2, 0 }, new[] { 7, 2, 0 },     // bottom row
            new[] { 1, 5, 90 }, new[] { 1, 9, 90 },   // left column
            new[] { 11, 5, 90 }, new[] { 11, 9, 90 }, // right column, on the way out
        };
        private static readonly int[] CauldronAt = { 14, 5, 0 };
        private static readonly int[] StartTile = { 6, 5 }, TargetTile = { 17, 6 }, PlayerTile = { 17, 10 };

        private readonly List<Il2CppScheduleOne.EntityFramework.BuildableItem> _items = new List<Il2CppScheduleOne.EntityFramework.BuildableItem>();
        private PropertyT _property;
        private Vector3 _start, _target, _player;

        private Chemist _walker;
        private float _walkStart, _nextIssue;
        private float _closest;
        private int _issued, _otherGoal;
        private string _pathAtStart = "", _midState = "";
        private bool _walking;
        private string _last = "no walk yet";

        public void Init()
        {
            DevConsole.Register("ovenrig", "awe ovenrig <place [code]|walk|status|clear>  (oven ring test for the furniture switch)", Dev);
        }

        public void Reset()
        {
            _items.Clear();
            _property = null;
            _walker = null;
            _walking = false;
            _last = "no walk yet";
        }

        public string Status => "items=" + _items.Count + " property=" + (_property == null ? "-" : _property.PropertyCode) + " walking=" + _walking + " last=" + _last;

        private void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("ovenrig")) return;
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            try
            {
                if (mode == "place") Place(args.Length > 1 ? args[1] : null);
                else if (mode == "walk") StartWalk();
                else if (mode == "clear") Clear();
                else Core.Log?.Msg("[Dev] [ovenrig] " + Status);
            }
            catch (Exception e) { Core.Failed("ovenrig " + mode, e); }
        }

        private void Place(string code)
        {
            if (_items.Count > 0) { Core.Log?.Warning("[Dev] [ovenrig] a rig stands already; 'awe ovenrig clear' first."); return; }
            var ovenDef = Il2CppScheduleOne.Registry.GetItem("laboven");
            var cauldronDef = Il2CppScheduleOne.Registry.GetItem("cauldron");
            if (ovenDef == null || cauldronDef == null) { Core.Log?.Warning("[Dev] [ovenrig] item 'laboven' or 'cauldron' is not in the registry."); return; }
            var build = NetworkSingleton<Il2CppScheduleOne.Building.BuildManager>.Instance;
            if (build == null) { Core.Log?.Warning("[Dev] [ovenrig] no BuildManager in the scene."); return; }

            if (!FindRegion(code, out PropertyT property, out Grid grid, out int ox, out int oy)) return;
            foreach (int[] o in Ovens) PlaceOne(build, ovenDef, grid, ox + o[0], oy + o[1], o[2]);
            PlaceOne(build, cauldronDef, grid, ox + CauldronAt[0], oy + CauldronAt[1], CauldronAt[2]);
            _property = property;
            _start = TileWorld(grid, ox + StartTile[0], oy + StartTile[1]);
            _target = TileWorld(grid, ox + TargetTile[0], oy + TargetTile[1]);
            _player = TileWorld(grid, ox + PlayerTile[0], oy + PlayerTile[1]);

            bool hasChemist = FindChemist() != null;
            if (!hasChemist) NetworkSingleton<EmployeeManager>.Instance.CreateNewEmployee(property, EEmployeeType.Chemist);
            Core.Log?.Msg("[Dev] [ovenrig] placed " + _items.Count + " item(s) on " + property.PropertyCode + " at tile " + ox + "," + oy
                + (hasChemist ? "" : ", hired a chemist") + ". start=" + V(_start) + " target=" + V(_target) + " player=" + V(_player));
        }

        private void PlaceOne(Il2CppScheduleOne.Building.BuildManager build, Il2CppScheduleOne.ItemFramework.ItemDefinition def, Grid grid, int x, int y, int rot)
        {
            var item = build.CreateGridItem(def.GetDefaultInstance(1), grid, new Vector2(x, y), rot, "", null);
            if (item == null) { Core.Log?.Warning("[Dev] [ovenrig] " + def.ID + " at " + x + "," + y + " was not created."); return; }
            _items.Add(item);
        }

        private bool FindRegion(string code, out PropertyT property, out Grid grid, out int ox, out int oy)
        {
            property = null; grid = null; ox = oy = 0;
            var owned = PropertyT.OwnedProperties;
            if (owned == null || owned.Count == 0) { Core.Log?.Warning("[Dev] [ovenrig] no owned property."); return false; }
            for (int p = 0; p < owned.Count; p++)
            {
                var prop = owned[p];
                if (prop == null || (code != null && !string.Equals(prop.PropertyCode, code, StringComparison.OrdinalIgnoreCase))) continue;
                var grids = prop.Grids;
                if (grids == null) continue;
                for (int g = 0; g < grids.Count; g++)
                {
                    if (grids[g] == null || !FreeRegion(grids[g], out ox, out oy)) continue;
                    property = prop; grid = grids[g];
                    return true;
                }
                Core.Log?.Msg("[Dev] [ovenrig] " + prop.PropertyCode + ": no free patch of " + RegionW + " x " + RegionH + " tiles.");
            }
            Core.Log?.Warning("[Dev] [ovenrig] no owned property" + (code == null ? "" : " with code '" + code + "'") + " has a free patch of " + RegionW + " x " + RegionH + " tiles.");
            return false;
        }

        // Copies the grid into a managed map once; the search itself then makes no interop calls.
        private static bool FreeRegion(Grid grid, out int ox, out int oy)
        {
            ox = oy = 0;
            var tiles = grid.Tiles;
            if (tiles == null || tiles.Count == 0) return false;
            int maxX = 0, maxY = 0;
            for (int i = 0; i < tiles.Count; i++) { if (tiles[i] == null) continue; maxX = Math.Max(maxX, tiles[i].x); maxY = Math.Max(maxY, tiles[i].y); }
            var free = new bool[maxX + 1, maxY + 1];
            for (int i = 0; i < tiles.Count; i++)
            {
                var t = tiles[i];
                if (t == null || t.x < 0 || t.y < 0) continue;
                free[t.x, t.y] = (t.BuildableOccupants == null || t.BuildableOccupants.Count == 0) && t.CanBeBuiltOn();
            }
            for (int x = 0; x + RegionW <= maxX + 1; x++)
                for (int y = 0; y + RegionH <= maxY + 1; y++)
                {
                    bool ok = true;
                    for (int i = 0; i < RegionW && ok; i++)
                        for (int j = 0; j < RegionH && ok; j++)
                            ok = free[x + i, y + j];
                    if (ok) { ox = x; oy = y; return true; }
                }
            return false;
        }

        private static Vector3 TileWorld(Grid grid, int x, int y)
        {
            var t = grid.GetTile(new Coordinate(x, y));
            return t == null ? grid.transform.position : t.transform.position;
        }

        private Chemist FindChemist()
        {
            Chemist any = null;
            foreach (Employee e in Rel.Employees())
            {
                var c = e.TryCast<Chemist>();
                if (c == null) continue;
                if (_property != null && c.AssignedProperty != null && c.AssignedProperty.PropertyCode == _property.PropertyCode) return c;
                any ??= c;
            }
            return any;
        }

        private void StartWalk()
        {
            if (_items.Count == 0) { Core.Log?.Warning("[Dev] [ovenrig] no rig; 'awe ovenrig place' first."); return; }
            var c = FindChemist();
            if (c == null || c.Movement == null) { Core.Log?.Warning("[Dev] [ovenrig] no chemist yet; a hired one needs a few seconds to spawn."); return; }
            _walker = c;
            c.Movement.Warp(_start);
            _pathAtStart = PathStatus(c, _start, _target);
            _walkStart = Time.unscaledTime;
            _nextIssue = 0f;
            _closest = float.MaxValue;
            _issued = _otherGoal = 0;
            _midState = "";
            _walking = true;
            Core.Log?.Msg("[Dev] [ovenrig] walk started: " + Rel.Name(c) + " from " + V(_start) + " to " + V(_target) + ", path " + _pathAtStart
                + ", obstacles on " + ObstaclesOn() + ".");
        }

        public void Tick(float dt)
        {
            if (!_walking) return;
            if (Rel.IsGone(_walker) || _walker.Movement == null) { _walking = false; _last = "walker gone"; Core.Log?.Warning("[Dev] [ovenrig] walk ended: the chemist is gone."); return; }
            float now = Time.unscaledTime, t = now - _walkStart;
            var m = _walker.Movement;
            float d = Vector3.Distance(_walker.transform.position, _target);
            if (d < _closest) _closest = d;
            if (d < 1f) { Finish(true, t); return; }
            if (t > WalkTimeout) { Finish(false, t); return; }
            if (now < _nextIssue) return;
            _nextIssue = now + 0.5f;
            var agent = m._agent;
            // Counts each check where the agent goal is not the target: a work or idle behaviour set its own, or the path is
            // partial and NPCMovement walks to the nearest reachable point instead.
            if (agent != null && _issued > 0 && Vector3.Distance(agent.destination, _target) > 1.5f) _otherGoal++;
            m.SetDestination(_target);
            _issued++;
            if (_midState.Length == 0 && t > 2f)
                _midState = "playerDist=" + PlayerDistance(_walker.transform.position).ToString("F1") + " m avoidanceFlag=" + m._obstacleAvoidanceEnabled
                    + " avoidanceType=" + (agent == null ? "?" : agent.obstacleAvoidanceType.ToString());
        }

        private void Finish(bool reached, float t)
        {
            _walking = false;
            _last = "reached=" + reached + " t=" + t.ToString("F1") + " s closest=" + _closest.ToString("F2") + " m path=" + _pathAtStart
                + " " + (_midState.Length > 0 ? _midState : "playerDist=" + PlayerDistance(_walker.transform.position).ToString("F1") + " m")
                + " obstaclesOn=" + ObstaclesOn() + " otherGoal=" + _otherGoal + "/" + _issued;
            Core.Log?.Msg("[Dev] [ovenrig] walk result: " + _last);
        }

        private static string PathStatus(Chemist c, Vector3 from, Vector3 to)
        {
            var agent = c.Movement._agent;
            var path = new NavMeshPath();
            var filter = new NavMeshQueryFilter { agentTypeID = agent == null ? 0 : agent.agentTypeID, areaMask = NavMesh.AllAreas };
            if (!Rel.TryOnNavMesh(from, out Vector3 a) || !Rel.TryOnNavMesh(to, out Vector3 b)) return "NoNavMesh";
            if (!NavMesh.CalculatePath(a, b, filter, path)) return "NoPath";
            return path.status.ToString();
        }

        private string ObstaclesOn()
        {
            int on = 0, all = 0;
            foreach (var item in _items)
            {
                if (Rel.IsGone(item)) continue;
                var obs = item.GetComponentsInChildren<NavMeshObstacle>(true);
                for (int i = 0; i < obs.Length; i++) { all++; if (obs[i].enabled) on++; }
            }
            return on + "/" + all;
        }

        private static float PlayerDistance(Vector3 p)
        {
            float best = float.MaxValue;
            var list = Il2CppScheduleOne.PlayerScripts.Player.PlayerList;
            if (list == null) return best;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) best = Math.Min(best, Vector3.Distance(list[i].transform.position, p));
            return best;
        }

        private void Clear()
        {
            int n = 0;
            foreach (var item in _items)
            {
                if (Rel.IsGone(item)) continue;
                item.Destroy_Server();
                n++;
            }
            _items.Clear();
            _walking = false;
            Core.Log?.Msg("[Dev] [ovenrig] removed " + n + " rig item(s).");
        }

        private static string V(Vector3 v) { return v.x.ToString("F1") + "," + v.y.ToString("F1") + "," + v.z.ToString("F1"); }
    }
}
#endif
