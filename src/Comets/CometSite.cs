using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using UnityEngine;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// Where a comet comes down and the way it comes in (docs/COMET.md section 2). It starts from
    /// where the host is looking, turned and at the range asked for, and must be at least the safe
    /// distance from every structure and every player in the world. A point too close moves outward
    /// along the same bearing, then tries other bearings; the approach path is held to the same
    /// distance, so the comet never passes low over a base.
    /// </summary>
    internal sealed class CometSite
    {
        private const float MaxRange = 20000f;
        private const float RangeStep = 100f;
        private const float BearingStep = 15f;
        private const float DescentDegrees = 24f;
        private const float SideDegrees = 40f;

        /// <summary>Structures are judged in squares this wide; a square's centre stands for every structure in it.</summary>
        private const float Bucket = 100f;
        private static readonly float BucketSlack = Bucket * 0.5f * Mathf.Sqrt(2f);

        public Vector3 Eye;
        public Vector3 Impact;
        public Vector3 Start;
        public Vector3 Travel;

        /// <summary>Metres from the eye to the impact, along the ground.</summary>
        public float Range;

        /// <summary>Degrees right of where the host was looking, after any turn needed to fit.</summary>
        public float Bearing;

        public float SafeDistance;

        /// <summary>For the command's reply when the comet could not land where it was asked to; else null.</summary>
        public string Moved;

        /// <summary>Every decision, for the log.</summary>
        public string Log;

        private readonly struct Obstacle
        {
            public readonly Vector3 At;
            public readonly float Slack;

            public Obstacle(Vector3 at, float slack)
            {
                At = at;
                Slack = slack;
            }
        }

        /// <summary>Null, with the reason, when nowhere within 20 km is clear.</summary>
        public static CometSite Pick(CometPlan plan, CometRules rules, int seed, out string refused)
        {
            refused = null;
            CultureInfo c = CultureInfo.InvariantCulture;
            Look(out Vector3 eye, out Vector3 forward, out string from);
            float safe = (float)rules.SafeDistance(plan.Scale);
            List<Obstacle> obstacles = Obstacles(out int structures, out int humans);
            float asked = plan.Range ?? safe;
            float first = Mathf.Max(asked, safe);
            System.Random random = new System.Random(seed);
            float side = random.NextDouble() < 0.5 ? -1f : 1f;
            float pathLength = 13000f + 2000f * plan.Scale;

            for (int k = 0; k <= (int)(180f / BearingStep) * 2; k++)
            {
                float offset = k == 0 ? 0f : ((k + 1) / 2) * BearingStep * (k % 2 == 1 ? 1f : -1f);
                if (Mathf.Abs(offset) > 180f || (k > 1 && Mathf.Abs(offset) == 180f && offset < 0f))
                {
                    continue;
                }
                float bearing = plan.TurnDegrees + offset;
                Vector3 direction = Quaternion.AngleAxis(bearing, Vector3.up) * forward;
                for (float range = first; range <= MaxRange; range += RangeStep)
                {
                    Vector3 flat = eye + direction * range;
                    if (!Clear(flat, safe, obstacles))
                    {
                        continue;
                    }
                    Vector3 impact = CometTerrain.Ground(flat, eye.y - 1.7f, out string ground);
                    foreach (float tried in new[] { side, -side })
                    {
                        Vector3 across = Quaternion.AngleAxis(tried * SideDegrees, Vector3.up) * -direction;
                        Vector3 travel = (across * Mathf.Cos(DescentDegrees * Mathf.Deg2Rad) - Vector3.up * Mathf.Sin(DescentDegrees * Mathf.Deg2Rad)).normalized;
                        Vector3 start = impact - travel * pathLength;
                        if (!PathClear(start, impact, safe, obstacles))
                        {
                            continue;
                        }
                        CometSite site = new CometSite
                        {
                            Eye = eye,
                            Impact = impact,
                            Start = start,
                            Travel = travel,
                            Range = range,
                            Bearing = bearing,
                            SafeDistance = safe,
                        };
                        if (offset != 0f)
                        {
                            site.Moved = string.Format(c, "It could not land where you asked and stay {0:N0} m from every structure and player, so it comes down {1:0}° further {2}.",
                                safe, Mathf.Abs(offset), offset < 0f ? "left" : "right");
                        }
                        else if (range > asked + 0.5f)
                        {
                            site.Moved = plan.Range.HasValue && asked < safe
                                ? string.Format(c, "range={0:N0} is closer than a comet this size may land to anyone, {1:N0} m, so it comes down at {2:N0} m.", asked, safe, range)
                                : string.Format(c, "It moved out to {0:N0} m to stay {1:N0} m from every structure and player.", range, safe);
                        }
                        site.Log = string.Format(c, "Site: looking from {0} ({1}), bearing {2:0}° (asked {3:0}°), range {4:N0} m (asked {5}), safe distance {6:N0} m, judged against {7} structures in {8} squares and {9} players, comes in from the {10}, impact {11}.",
                            eye, from, bearing, plan.TurnDegrees, range, plan.Range.HasValue ? plan.Range.Value.ToString("N0", c) + " m" : "none", safe, structures, obstacles.Count - humans, humans, tried < 0f ? "left" : "right", ground);
                        return site;
                    }
                }
            }
            refused = string.Format(c, "There is nowhere within {0:N0} m of you that a comet this size can land {1:N0} m from every structure and player, so it was not sent.", MaxRange, safe);
            return null;
        }

        /// <summary>
        /// Where the host is looking from and toward, flat. The camera when this machine has one; on a
        /// dedicated server the first player online, then the world origin facing north.
        /// </summary>
        private static void Look(out Vector3 eye, out Vector3 forward, out string from)
        {
            Camera camera = CameraController.CurrentCamera ? CameraController.CurrentCamera : Camera.main;
            Transform view = null;
            from = "camera";
            if (camera != null)
            {
                view = camera.transform;
            }
            else if (InventoryManager.ParentHuman != null)
            {
                view = InventoryManager.ParentHuman.transform;
                from = "your character";
            }
            else
            {
                foreach (Human human in Human.AllHumans)
                {
                    if (human != null && human.OrganBrain != null && human.OrganBrain.IsOnline && !human.IsDead)
                    {
                        view = human.transform;
                        from = "player " + human.DisplayName;
                        break;
                    }
                }
            }
            if (view == null)
            {
                eye = CometTerrain.Ground(Vector3.zero, 0f, out _) + Vector3.up * 1.7f;
                forward = Vector3.forward;
                from = "the world origin";
                return;
            }
            eye = view.position;
            forward = Vector3.ProjectOnPlane(view.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = Vector3.ProjectOnPlane(view.up, Vector3.up);
            }
            forward = forward.sqrMagnitude < 1e-4f ? Vector3.forward : forward.normalized;
        }

        /// <summary>Every structure, by square, and every player, online or not.</summary>
        private static List<Obstacle> Obstacles(out int structures, out int humans)
        {
            Dictionary<long, Vector3> squares = new Dictionary<long, Vector3>();
            int count = 0;
            GridController.AllStructuresPool.ForEach((Action<Structure>)(structure =>
            {
                if (structure == null)
                {
                    return;
                }
                count++;
                Vector3 at = structure.ThingTransformPosition;
                long x = (long)Mathf.Floor(at.x / Bucket);
                long z = (long)Mathf.Floor(at.z / Bucket);
                long key = (x << 32) ^ (z & 0xffffffffL);
                if (!squares.ContainsKey(key))
                {
                    squares[key] = new Vector3((x + 0.5f) * Bucket, at.y, (z + 0.5f) * Bucket);
                }
            }));
            structures = count;
            List<Obstacle> obstacles = new List<Obstacle>(squares.Count + Human.AllHumans.Count);
            foreach (Vector3 square in squares.Values)
            {
                obstacles.Add(new Obstacle(square, BucketSlack));
            }
            humans = 0;
            foreach (Human human in Human.AllHumans)
            {
                if (human != null)
                {
                    obstacles.Add(new Obstacle(human.transform.position, 0f));
                    humans++;
                }
            }
            return obstacles;
        }

        /// <summary>The impact, judged along the ground: nothing nearer than the safe distance.</summary>
        private static bool Clear(Vector3 at, float safe, List<Obstacle> obstacles)
        {
            foreach (Obstacle obstacle in obstacles)
            {
                float dx = obstacle.At.x - at.x;
                float dz = obstacle.At.z - at.z;
                float reach = safe + obstacle.Slack;
                if (dx * dx + dz * dz < reach * reach)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The approach, sampled every 100 m: no point of it nearer than the safe distance to anything,
        /// its height taken above the impact's ground.
        /// </summary>
        private static bool PathClear(Vector3 start, Vector3 impact, float safe, List<Obstacle> obstacles)
        {
            float length = Vector3.Distance(start, impact);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / 100f));
            for (int i = 0; i <= steps; i++)
            {
                Vector3 p = Vector3.Lerp(impact, start, i / (float)steps);
                float height = Mathf.Max(0f, p.y - impact.y);
                if (height >= safe)
                {
                    break;
                }
                foreach (Obstacle obstacle in obstacles)
                {
                    float dx = obstacle.At.x - p.x;
                    float dz = obstacle.At.z - p.z;
                    float horizontal = Mathf.Max(0f, Mathf.Sqrt(dx * dx + dz * dz) - obstacle.Slack);
                    if (horizontal * horizontal + height * height < safe * safe)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
