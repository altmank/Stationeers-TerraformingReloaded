using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Weather;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// Rain turning to snow, or snow to rain, made on the main thread.
    ///
    /// The planet tick runs on a worker thread. When the ice clouds fill while rain is running it
    /// stops the rain and starts snow at once, and the liquid clouds do the same the other way round.
    /// Starting weather sets the fog, the storm cards and the lighting, which Unity allows only on the
    /// main thread, so the game crashed outright on a world that rains and freezes in the same night.
    /// The tick's two calls are held here as one switch and made by the weather manager's own update,
    /// before anything else it does that frame. The server still makes the switch, so clients get it
    /// the way they get any change of weather.
    ///
    /// Held only from inside the tick (<see cref="Guards.InTick"/>), so the game's own stops and
    /// starts on the main thread, and the console's, run as they always did.
    /// </summary>
    public static class WeatherSwitch
    {
        /// <summary>
        /// What the tick asked for. <see cref="From"/> is the weather it stopped, null when it only
        /// started one; <see cref="To"/> the weather it started, null when it only stopped one.
        /// </summary>
        internal sealed class Request
        {
            internal Request(WeatherEvent from, WeatherEvent to)
            {
                From = from;
                To = to;
            }

            internal WeatherEvent From { get; }
            internal WeatherEvent To { get; }

            /// <summary>
            /// The stop was asked of the weather running then. If that has already ended, or the world
            /// has been left, the switch is about weather that is gone and is dropped whole.
            /// </summary>
            internal bool StillApplies(WeatherEvent current, bool running) => From == null || (running && current == From);
        }

        private static readonly object Lock = new object();
        private static Request _pending;
        private static int _faults;

        public static void Apply(Harmony harmony)
        {
            Type manager = typeof(WeatherManager);
            // The instance stop rather than the static StopCurrentWeatherEvent, which is small enough
            // to be inlined into the tick and then never see a patch.
            MethodInfo stop = Need(AccessTools.DeclaredMethod(manager, "StopWeatherEventServer", Type.EmptyTypes), "WeatherManager.StopWeatherEventServer");
            MethodInfo start = Need(AccessTools.DeclaredMethod(manager, "ImmediatelyActivateWeatherEvent", new[] { typeof(WeatherEvent) }), "WeatherManager.ImmediatelyActivateWeatherEvent");
            MethodInfo update = Need(AccessTools.DeclaredMethod(manager, "ManagerUpdate", Type.EmptyTypes), "WeatherManager.ManagerUpdate");
            // The update first: a held switch nothing would ever make is worse than the crash.
            harmony.Patch(update, prefix: Body(nameof(UpdatePrefix)));
            harmony.Patch(stop, prefix: Body(nameof(StopPrefix)));
            harmony.Patch(start, prefix: Body(nameof(StartPrefix)));
        }

        public static bool StopPrefix()
        {
            if (!Guards.InTick)
            {
                return true;
            }
            Hold(WeatherManager.CurrentWeatherEvent, stopping: true);
            return false;
        }

        public static bool StartPrefix(WeatherEvent weatherEvent)
        {
            if (!Guards.InTick)
            {
                return true;
            }
            Hold(weatherEvent, stopping: false);
            return false;
        }

        /// <summary>Main thread, every frame: one field read when nothing is held.</summary>
        public static void UpdatePrefix()
        {
            Request request = Take(WeatherManager.CurrentWeatherEvent, WeatherManager.IsWeatherEventRunning);
            if (request == null)
            {
                return;
            }
            try
            {
                if (request.From != null)
                {
                    WeatherManager.StopCurrentWeatherEvent();
                }
                if (request.To != null)
                {
                    WeatherManager.ImmediatelyActivateWeatherEvent(request.To);
                }
            }
            catch (Exception e)
            {
                if (Interlocked.Increment(ref _faults) <= 3)
                {
                    Log.Error("Switching between rain and snow failed: " + e);
                }
            }
        }

        private static void Hold(WeatherEvent weather, bool stopping)
        {
            lock (Lock)
            {
                _pending = stopping
                    ? new Request(weather, null)
                    : new Request(_pending?.From, weather);
            }
        }

        /// <summary>The held switch if it still applies, and nothing held afterwards either way.</summary>
        internal static Request Take(WeatherEvent current, bool running)
        {
            if (Volatile.Read(ref _pending) == null)
            {
                return null;
            }
            lock (Lock)
            {
                Request request = _pending;
                _pending = null;
                return request != null && request.StillApplies(current, running) ? request : null;
            }
        }

        /// <summary>
        /// Holds and takes switches on two plain event objects. Null when every case comes out right,
        /// otherwise what did not. Run by tools/PatchCheck.
        /// </summary>
        public static string Check()
        {
            Request saved;
            lock (Lock)
            {
                saved = _pending;
                _pending = null;
            }
            try
            {
                var rain = new WeatherEvent();
                var snow = new WeatherEvent();

                if (Take(rain, true) != null)
                {
                    return "a switch was made with nothing held";
                }

                Hold(rain, stopping: true);
                Hold(snow, stopping: false);
                Request held = Take(rain, true);
                if (held == null || held.From != rain || held.To != snow)
                {
                    return "rain to snow was not made as one switch from rain to snow";
                }
                if (Take(rain, true) != null)
                {
                    return "a switch was made twice";
                }

                Hold(rain, stopping: true);
                Hold(snow, stopping: false);
                if (Take(rain, false) != null || Take(rain, true) != null)
                {
                    return "a switch was made after the rain it stops had already ended, or was kept after it was dropped";
                }

                Hold(rain, stopping: true);
                Hold(snow, stopping: false);
                if (Take(snow, true) != null)
                {
                    return "a switch was made while other weather was running";
                }

                Hold(snow, stopping: true);
                Hold(rain, stopping: false);
                Hold(rain, stopping: true);
                Hold(snow, stopping: false);
                held = Take(rain, true);
                if (held == null || held.From != rain || held.To != snow)
                {
                    return "a later switch did not replace an earlier one";
                }

                Hold(snow, stopping: false);
                held = Take(null, false);
                if (held == null || held.From != null || held.To != snow)
                {
                    return "a start with no stop before it was not made";
                }

                return null;
            }
            finally
            {
                lock (Lock)
                {
                    _pending = saved;
                }
            }
        }

        private static MethodInfo Need(MethodInfo method, string name)
        {
            return method ?? throw new MissingMethodException(name + " is gone");
        }

        private static HarmonyMethod Body(string name)
        {
            return new HarmonyMethod(AccessTools.DeclaredMethod(typeof(WeatherSwitch), name));
        }
    }
}
