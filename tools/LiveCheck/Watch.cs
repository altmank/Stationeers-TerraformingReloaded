using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using BepInEx.Logging;
using HarmonyLib;
using TerraformingReloaded.Patching;
using UnityEngine;

namespace TerraformingReloaded.LiveCheck
{
    /// <summary>
    /// The leak test's eyes (tools/LeakTest): every outdoor cell, read inside the mod's fire upkeep where the
    /// cells are at rest, summed in rings around the release point and around each named station, and every
    /// burn of a cell near a station caught at the burn itself. One line every <c>TR_LIVECHECK_WATCH_EVERY</c>
    /// ticks (default 2) and a burn line whenever a watched cell burns. The runner builds, releases and reads
    /// damage through StationGod; this only measures.
    ///
    /// TR_LIVECHECK_WATCH = "R:x,y,z;far:x,y,z;s2:x,y,z;..." in world metres; the first point is the centre
    /// of the rings. TR_LIVECHECK_STATUS_EVERY = n also logs the readout's fire and hold lines and the
    /// three fire and hold settings every n ticks.
    /// </summary>
    internal sealed class WatchCheck
    {
        private static readonly double[] Rings = { 3, 6, 12, 24, 48, 96, 200, double.PositiveInfinity };
        private const double StationRadius = 5.0;

        private static WatchCheck _current;
        private static int _statusEvery;

        private readonly ManualLogSource _log;
        private readonly List<(string Name, Vector3 At)> _points = new List<(string, Vector3)>();
        private readonly int _every;

        private readonly object _lock = new object();
        private readonly Dictionary<string, (int Lit, double Held, double Burnt, double Energy, double HeldMax)> _burns =
            new Dictionary<string, (int, double, double, double, double)>();

        // Which station each watched cell belongs to, rebuilt every reading.
        private volatile Dictionary<Atmosphere, string> _near = new Dictionary<Atmosphere, string>();

        private readonly string _file;
        private DateTime _fileStamp;

        private WatchCheck(ManualLogSource log, string spec, int every)
        {
            _log = log;
            _every = Math.Max(1, every);
            // A spec that is a path is a file of points the runner rewrites once it has built the site.
            if (spec.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                _file = spec;
                _points.Add(("R", Vector3.zero));
                return;
            }
            Parse(spec, _points);
        }

        private static void Parse(string spec, List<(string Name, Vector3 At)> points)
        {
            foreach (string part in spec.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] named = part.Split(':');
                if (named.Length != 2)
                {
                    continue;
                }
                string[] xyz = named[1].Split(',');
                points.Add((named[0].Trim(), new Vector3(
                    float.Parse(xyz[0], CultureInfo.InvariantCulture), float.Parse(xyz[1], CultureInfo.InvariantCulture), float.Parse(xyz[2], CultureInfo.InvariantCulture))));
            }
        }

        private void Reload()
        {
            if (_file == null || !System.IO.File.Exists(_file))
            {
                return;
            }
            DateTime stamp = System.IO.File.GetLastWriteTimeUtc(_file);
            if (stamp == _fileStamp)
            {
                return;
            }
            _fileStamp = stamp;
            List<(string, Vector3)> points = new List<(string, Vector3)>();
            Parse(System.IO.File.ReadAllText(_file), points);
            if (points.Count > 0)
            {
                _points.Clear();
                _points.AddRange(points);
                _log.LogInfo("LiveCheck: watch points " + _points.Count + " from " + _file);
            }
        }

        public static WatchCheck FromEnvironment(ManualLogSource log)
        {
            string spec = Environment.GetEnvironmentVariable("TR_LIVECHECK_WATCH");
            if (string.IsNullOrEmpty(spec))
            {
                return null;
            }
            int every = int.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_WATCH_EVERY"), out int e) ? e : 2;
            _statusEvery = int.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_STATUS_EVERY"), out int s) ? s : 0;
            return new WatchCheck(log, spec, every);
        }

        public void Install(Harmony harmony)
        {
            _current = this;
            // After the tick's hold is published, so the line shows what the cells are handed this tick.
            harmony.Patch(AccessTools.DeclaredMethod(typeof(PlanetCombustion), "PublishHold"),
                postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(WatchCheck), nameof(UpkeepPostfix))));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Atmosphere), "TryCombust", new[] { typeof(double), typeof(bool) }),
                prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(WatchCheck), nameof(TryCombustPrefix))),
                postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(WatchCheck), nameof(TryCombustPostfix))));
            _log.LogInfo("LiveCheck: watch installed on " + _points.Count + " points");
        }

        public static void TryCombustPrefix(Atmosphere __instance, out double __state)
        {
            WatchCheck watch = _current;
            __state = watch != null && watch._near.ContainsKey(__instance) ? __instance.GasMixture.TotalOxidiser.ToDouble() : double.NaN;
        }

        public static void TryCombustPostfix(Atmosphere __instance, double __state)
        {
            if (double.IsNaN(__state))
            {
                return;
            }
            WatchCheck watch = _current;
            if (!watch._near.TryGetValue(__instance, out string station))
            {
                return;
            }
            double after = __instance.GasMixture.TotalOxidiser.ToDouble();
            lock (watch._lock)
            {
                watch._burns.TryGetValue(station, out var b);
                if (__instance.Inflamed)
                {
                    b.Lit++;
                    b.Burnt += Math.Max(0.0, __state - after);
                    b.Energy += __instance.CombustionEnergy.ToDouble();
                }
                b.Held += Math.Max(0.0, __state);
                b.HeldMax = Math.Max(b.HeldMax, __state);
                watch._burns[station] = b;
            }
        }

        public static void UpkeepPostfix()
        {
            WatchCheck watch = _current;
            if (watch == null)
            {
                return;
            }
            try
            {
                watch.Read();
            }
            catch (Exception e)
            {
                watch._log.LogInfo("LiveCheck: watch FAIL " + e.Message);
            }
        }

        private void Read()
        {
            uint tick = GameManager.GameTickCount;
            if (tick % 20 == 0)
            {
                Reload();
            }
            Vector3 centre = _points[0].At;
            int n = Rings.Length;
            int[] cells = new int[n], lit = new int[n];
            double[] o2 = new double[n], n2o = new double[n], ch4 = new double[n], hot = new double[n];
            int[] sCells = new int[_points.Count], sLit = new int[_points.Count];
            double[] sOx = new double[_points.Count], sHot = new double[_points.Count];
            int world = 0, exchanging = 0;
            Dictionary<Atmosphere, string> near = new Dictionary<Atmosphere, string>();
            AtmosphericsManager.AllAtmospheres.ForEach((Action<Atmosphere>)(a =>
            {
                if (a == null || a.Mode != AtmosphereHelper.AtmosphereMode.World)
                {
                    return;
                }
                world++;
                Vector3 at = a.WorldGrid.Value.ToVector3();
                double d = Vector3.Distance(at, centre);
                int r = 0;
                while (d >= Rings[r])
                {
                    r++;
                }
                GasMixture mix = a.GasMixture;
                cells[r]++;
                lit[r] += a.Inflamed ? 1 : 0;
                o2[r] += mix.Oxygen.Quantity.ToDouble();
                n2o[r] += mix.NitrousOxide.Quantity.ToDouble();
                ch4[r] += mix.Methane.Quantity.ToDouble();
                hot[r] = Math.Max(hot[r], mix.Temperature.ToDouble());
                if (a.Room == null && a.Cell == null && GlobalNeighbours(a) > 0)
                {
                    exchanging++;
                }
                for (int i = 1; i < _points.Count; i++)
                {
                    if (Vector3.Distance(at, _points[i].At) <= StationRadius)
                    {
                        sCells[i]++;
                        sLit[i] += a.Inflamed ? 1 : 0;
                        sOx[i] += mix.TotalOxidiser.ToDouble();
                        sHot[i] = Math.Max(sHot[i], mix.Temperature.ToDouble());
                        near[a] = _points[i].Name;
                        break;
                    }
                }
            }));
            _near = near;

            Dictionary<string, (int Lit, double Held, double Burnt, double Energy, double HeldMax)> burns;
            lock (_lock)
            {
                burns = new Dictionary<string, (int, double, double, double, double)>(_burns);
                _burns.Clear();
            }
            CultureInfo c = CultureInfo.InvariantCulture;
            foreach (var b in burns)
            {
                if (b.Value.Lit > 0 || b.Value.Held > 0.0)
                {
                    _log.LogInfo(string.Format(c, "LiveCheck: watchburn tick {0} | {1} | lit {2} | held {3:G6} mol (max {4:G6} a cell) | burnt {5:G6} mol | {6:G6} J",
                        tick, b.Key, b.Value.Lit, b.Value.Held, b.Value.HeldMax, b.Value.Burnt, b.Value.Energy));
                }
            }
            if (tick % (uint)_every != 0)
            {
                return;
            }
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            double capacity = PlanetaryAtmosphereSimulation.GetHeatCapacity().ToDouble();
            FireReport report = PlanetCombustion.Now;
            StringBuilder line = new StringBuilder(1024);
            line.AppendFormat(c, "LiveCheck: watch tick {0} | planet O2 {1:G9} N2O {2:G9} CH4 {3:0.###} | fire {4} | held {5} | peak {6:0.#} K | now {7:0.#} K | fire heat {8:0.###} K | ext {9:0.###} K | world cells {10} | edge cells {11} | lerp {12:0.###} | sun {13:0.0}",
                tick, tank.Get(Chemistry.GasType.Oxygen).ToDouble(), tank.Get(Chemistry.GasType.NitrousOxide).ToDouble(), tank.Get(Chemistry.GasType.Methane).ToDouble(),
                Kind(report), Held(PlanetCombustion.HeldBackFrom(tank)), report?.PeakKelvin ?? double.NaN,
                tank.GetGlobalGasMixTemperature(WorldSetting.Current.Data.GlobalAtmosphereData).ToDouble(),
                capacity > 0 ? PlanetCombustion.Heat / capacity : 0.0, capacity > 0 ? PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset.ToDouble() / capacity : 0.0,
                world, exchanging, AtmosphereHelper.LerpRate(), Vector3.Angle(Vector3.up, OrbitalSimulation.WorldSunVector));
            for (int r = 0; r < n; r++)
            {
                line.AppendFormat(c, " | ring<{0} c{1} lit{2} O2 {3:G4} N2O {4:G4} CH4 {5:G4} T{6:0}", double.IsInfinity(Rings[r]) ? "inf" : Rings[r].ToString(c), cells[r], lit[r], o2[r], n2o[r], ch4[r], hot[r]);
            }
            for (int i = 1; i < _points.Count; i++)
            {
                line.AppendFormat(c, " | st {0} c{1} lit{2} ox {3:G4} T{4:0}", _points[i].Name, sCells[i], sLit[i], sOx[i], sHot[i]);
            }
            _log.LogInfo(line.ToString());
            if (_statusEvery > 0 && tick % (uint)_statusEvery == 0)
            {
                StringBuilder status = new StringBuilder(512);
                PlanetCombustion.Describe(status, c);
                foreach (string part in status.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    _log.LogInfo("LiveCheck: status tick " + tick + " |" + part);
                }
                _log.LogInfo(string.Format(c, "LiveCheck: status tick {0} | settings PlanetAirBurns {1} PlanetHoldsBackWhileIgnitable {2} PlanetKeepsTraceGas {3}",
                    tick, Effective.PlanetAirBurns, Effective.PlanetHoldsBackWhileIgnitable, Effective.PlanetKeepsTraceGas));
            }
        }

        private static readonly AccessTools.FieldRef<Atmosphere, int> GlobalNeighbours =
            AccessTools.FieldRefAccess<Atmosphere, int>("_previousGlobalNeighboursCount");

        private static string Kind(FireReport report)
        {
            return report?.Tick switch
            {
                null => "none",
                Burning b => (b.Cause == FireCause.Heat ? "heat " : "spark ") + (b.Rate * 100.0).ToString("0.0##", CultureInfo.InvariantCulture) + "%",
                Unlit _ => "unlit",
                _ => "nothing",
            };
        }

        private static string Held(HeldGases held)
        {
            List<string> names = new List<string>(3);
            foreach (Chemistry.GasType type in FireGases.Burnable)
            {
                if (held.Contains(type))
                {
                    names.Add(type.ToString());
                }
            }
            return names.Count == 0 ? "-" : string.Join("+", names.ToArray());
        }
    }
}
