using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Serialization;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// One file per world, holding a snapshot of the settings that decide how that particular world
    /// behaves. Public with public fields because XmlSerializer needs it, and the same shape as the
    /// curves file so anyone who has edited that can edit this.
    ///
    /// Every field is nullable, and the file is always written whole, so an absent element can only
    /// mean the version that wrote it did not know the field. That keeps "the player meant zero"
    /// and "this version did not have this setting" apart without companion flags, makes a later
    /// field free to add, and removes the all-defaults hazard by construction: an all-defaults parse
    /// of a non-nullable class would set the greenhouse strength to 0 and silently kill the response.
    ///
    /// Nothing that arrives here is trusted. A value outside the range the config editor itself
    /// enforces on the same setting is not a setting, it is a hand edit or a damaged file, and
    /// <see cref="Sidecar.Take"/> throws it away.
    ///
    /// Version carries no initialiser on purpose. It is read off the raw XML before this class is
    /// built (see <see cref="Sidecar.ReadVersion"/>); a default of 1 here would make a file with no
    /// version attribute read as a version 1 file.
    /// </summary>
    [XmlRoot("TerraformingReloaded")]
    public class SidecarFile
    {
        [XmlAttribute("Version")]
        public int Version;

        /// <summary>Null means no ceiling.</summary>
        public double? MaxPressureKPa;

        public double? MaxExternalOffsetKelvin;

        /// <summary>Null means stored heat never fades.</summary>
        public double? ExternalHeatHalfLifeMinutes;

        public double? GhgResponseScale;
        public double? DensityResponseScale;
        public double? AirlessAlbedo;

        public bool? DynamicSky;
        public bool? WeatherOnWeatherlessWorlds;

        public bool? StormsStopWhenStripped;
        public double? StrippedAtmosphereShare;
        public bool? StormsStopWhenAtmosphereIsMild;
        public double? MildAtmosphereColdestKelvin;
        public double? MildAtmosphereHottestKelvin;
        public double? MildAtmosphereMinPressureKpa;
        public double? MildAtmosphereMaxPressureKpa;
        public double? MildAtmosphereMaxToxinsKpa;
        public bool? MildAtmosphereStopsSolarStorms;

        public double? TraceGasGathering;
        public double? TraceGasLine;
    }

    /// <summary>
    /// Per-world settings. One global config decides how a particular world behaves, so tuning a
    /// setting for a new save silently changes or damages an older one; MaxPressureKPa is the worst,
    /// because it deletes air above the ceiling for good and saves the loss. This records every
    /// setting that affects a world beside its save and assigns <see cref="Effective"/> whole at
    /// every world start, so nothing needs restoring when a player leaves a world. The config only
    /// decides what a new world starts with, and fills in what a world did not record.
    ///
    /// Read and write are asymmetric, because on a new world neither the folder nor the save name
    /// exists when the planet is built, while on a load both do:
    ///   read            prefix on PlanetaryAtmosphereSimulation.CreateGlobalAtmosphere;
    ///   write, new      postfix on SaveHelper.CreateSaveDirectory, the one place a folder is born;
    ///   write, loaded   inside the read prefix, when the folder exists but the file does not;
    ///   write, changed  terraform set, and nothing else. A config edit never reaches a world.
    ///
    /// New and loaded are told apart by the game's own flag, taken from World.Initialize, and never
    /// inferred from whether a name or a folder happens to be set: the console's own new-game paths
    /// (FileCommand.NewGameTask, LoadGameCommand.NewGameTask) start a world without clearing the
    /// last one's station name first, so a world being created can carry the previous world's name
    /// and resolve to the previous world's folder.
    ///
    /// The file sits in the world folder root. Never inside autosave/, quicksave/ or manualsave/,
    /// and never named *.save: SaveHelper.RollSaveFiles counts every file in a folder, not just
    /// saves, so a sidecar beside each save would halve the player's autosave depth and delete real
    /// saves.
    ///
    /// Everything here is best effort. A read-only folder, a full disk or a cloud sync mid-write
    /// must not stop a world loading, so the prefix catches everything and falls back, the postfix
    /// catches and logs, and nothing of the player's is deleted or moved: a file that could not be
    /// believed is copied aside before it is replaced, so a hand edit with one typo in it is not
    /// thrown away along with the settings it got right.
    /// </summary>
    public static class Sidecar
    {
        /// <summary>Bumped when a field is added. A file from a higher version is not guessed at.</summary>
        public const int SchemaVersion = 1;

        public const string FileName = "terraforming-reloaded.xml";

        /// <summary>
        /// Where a file that could not be believed is copied before it is replaced. Not *.save, and
        /// in the world folder root beside the file it came from, for the same reasons as FileName.
        /// </summary>
        public const string SalvagedFileName = "terraforming-reloaded.broken.xml";

        // BCL only: tools/PatchCheck runs Patcher.Apply outside Unity, so no static field here may
        // reach game or Unity code.
        //
        // Built on first use rather than in a static initialiser, and this is the reason: the world
        // start prefix is a required patch, so if building the serializer ever threw, a static
        // initialiser would turn that into a cached TypeInitializationException thrown before the
        // prefix body is even entered, on every world start, with nothing able to catch it. Lazily
        // it is an ordinary exception inside a try, and the world loads on the config.
        private static XmlSerializer _serializer;

        private static XmlSerializer Serializer => _serializer ?? (_serializer = new XmlSerializer(typeof(SidecarFile)));

        private static readonly XmlWriterSettings WriterSettings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            // A StringWriter would declare utf-16, which is not what lands on disk.
            Encoding = new UTF8Encoding(false),
        };

        private static string _folder;
        private static string _filePath;
        private static int _fileVersion;
        private static string _source;
        private static string _noFolder;
        private static string _standDown;
        private static int _writeFaults;
        private static string _salvage;
        private static bool _newWorld;

        /// <summary>The file this world's settings are in, or null while there is none.</summary>
        public static string FilePath => _filePath;

        /// <summary>The schema version of that file, or 0 when there is none.</summary>
        public static int FileVersion => _fileVersion;

        /// <summary>
        /// Why the values in force did not come from the file, or null when they did. For readouts.
        /// </summary>
        public static string Source => _source;

        /// <summary>
        /// Stops all file work for the rest of the session, so every world takes the missing-file
        /// fallback and nothing is written over. Used when the round-trip self-test fails, and when
        /// a world turns out to carry a file from a later version than this build understands.
        /// </summary>
        public static void Disable(string reason) => _standDown = reason;

        // ---- World.Initialize (prefix and finalizer, an Extra) ------------------------------------

        /// <summary>
        /// The game's own answer to "is this world being created or loaded", taken where the game
        /// itself decides it. Nothing else can tell them apart: the console's new-game paths leave
        /// the previous world's station name in place, so a name, and the folder it resolves to, can
        /// both belong to a different world entirely.
        ///
        /// The flag is set for the length of World.Initialize and cleared again on the way out,
        /// which is what makes it safe to keep in a static. Both of the game's
        /// CreateGlobalAtmosphere call sites for a new world are inside that one call
        /// (GridController.InitializeWorldController builds the world's AtmosphericsController,
        /// whose constructor creates the planet, and WorldManager.InitializeWorldEnvironment creates
        /// it again), and the load path reaches both of them without going through World.Initialize.
        /// So the flag is true for exactly the reads that belong to a world being created, and false
        /// at every other moment of the session, including one world started straight after another.
        /// </summary>
        public static void WorldInitializePrefix(bool newWorld)
        {
            _newWorld = newWorld;
        }

        /// <summary>A finalizer, so a world start that throws part way still leaves the flag down.</summary>
        public static void WorldInitializeFinalizer()
        {
            _newWorld = false;
        }

        // ---- PlanetaryAtmosphereSimulation.Clear (postfix, an Extra) ------------------------------

        /// <summary>
        /// The planet has been torn down, so no world is being played. Without this the values in
        /// force at the main menu are the last world played: Climate's temperature postfixes gate on
        /// the config's master switch and not on a world being in play, so the new-world menu's
        /// temperature readout would depend on which world happened to be played before it. Back to
        /// the config, and the pressure ceiling off, because a ceiling belongs to a world.
        /// </summary>
        public static void WorldEndedPostfix()
        {
            try
            {
                SeedFromConfig();
                _folder = null;
                _filePath = null;
                _fileVersion = 0;
                _salvage = null;
                _newWorld = false;
                if (_standDown != null)
                {
                    _source = _standDown;
                    _noFolder = "Per-world settings are off for this session (" + _standDown + "), so nothing can be recorded; nothing was changed.";
                }
            }
            catch (Exception e)
            {
                Log.Warn("Per-world settings could not be put back to the config on leaving the world: " + e.Message);
            }
        }

        // ---- PlanetaryAtmosphereSimulation.CreateGlobalAtmosphere (prefix, Priority.First) --------

        /// <summary>
        /// Every world start, before the planet is built. Assigns the values in force whole.
        /// It catches everything: a prefix that throws takes the world with it.
        ///
        /// The ceiling is switched off first, before anything else and outside the try, so no path
        /// below can leave the last world's ceiling standing, the catch included. The only code
        /// anywhere that can turn it back on is one of Effective's three named methods, and exactly
        /// one of those is reachable from here.
        /// </summary>
        public static void WorldStartPrefix()
        {
            Effective.NoCeiling();
            try
            {
                Begin();
            }
            catch (Exception e)
            {
                _folder = null;
                _filePath = null;
                _fileVersion = 0;
                _salvage = null;
                TakeFromConfig();
                _source = "this world's settings file could not be looked at (" + e.Message
                    + "), so the pressure ceiling is off for it and the rest is from the config";
                _noFolder = "This world's settings file could not be looked at (" + e.Message
                    + "), so there is nowhere to record settings for it; nothing was changed.";
                Log.Warn("Per-world settings: " + _source);
            }
        }

        private static void Begin()
        {
            _folder = null;
            _filePath = null;
            _fileVersion = 0;
            _salvage = null;
            TakeFromConfig();

            // A client is handed the host's planet state by Sync and owns no save folder, so it
            // reads and writes nothing. It evaluates the temperature response and the sky locally,
            // on the host's settings for them once Sync delivers them (TakenFromHost), on its own
            // config until then. It never runs the planet upkeep, and deleting the host's air is
            // not its business, so it holds no ceiling either.
            if (NetworkManager.IsClient)
            {
                _source = "this is a network client, so no file is read or written, and its own config is in force until the host's planet arrives";
                _noFolder = "This is a network client; settings for this world are the host's to record. Nothing was changed.";
                return;
            }
            if (_standDown != null)
            {
                _source = _standDown;
                _noFolder = "Per-world settings are off for this session (" + _standDown + "), so nothing can be recorded for this world; nothing was changed.";
                return;
            }

            if (_newWorld)
            {
                // A new world is not a missing file. It takes the config in full, ceiling included,
                // because those are the settings the player chose for the world they are creating
                // and it has no history to damage, and the CreateSaveDirectory postfix records them
                // the moment it is first saved.
                Effective.CeilingForNewWorld(ConfigCeiling());
                _source = "this world is being created, so the config is in force for it; it is recorded when the world is first saved";
                _noFolder = "This world has not been saved yet, so there is nowhere to record settings for it. Save the world first.";
                Log.Info("Per-world settings: " + _source + ".");
                return;
            }

            // Everything from here is a world being loaded, and a loaded world's ceiling comes out
            // of its own file or nowhere. Each way of failing to get there leaves it off.
            string station = XmlSaveLoad.Instance?.CurrentStationName;
            if (string.IsNullOrEmpty(station))
            {
                _noFolder = "The game has not named the world being played, so there is nowhere to record settings for it; nothing was changed.";
                Missing("this world is being loaded but the game has not named it, so its settings file could not be looked for");
                return;
            }
            string folder = Path.Combine(StationSaveUtils.GetSavePathSavesSubDir().FullName, station);
            if (!Directory.Exists(folder))
            {
                // Not the same thing as a world that has never been saved, and the difference is the
                // whole point: this world exists and its folder has moved or been renamed under it,
                // so giving it the config in full would hand a world that has been played a
                // destructive setting it never had.
                _noFolder = "This world's save folder is not where the game says it is (" + folder + "), so there is nowhere to record settings for it; nothing was changed.";
                Missing("the save folder for this world is not where the game says it is (" + folder + ")");
                return;
            }

            _folder = folder;
            _noFolder = null;
            string path = Path.Combine(folder, FileName);
            if (!File.Exists(path))
            {
                Missing(path + " does not exist yet");
                return;
            }

            // The version comes off the raw XML first, before the serializer is let near the file:
            // absent or not a number means unreadable, and it is the only guard against a file a
            // later version wrote, because unknown elements are ignored silently.
            int version;
            try
            {
                version = ReadVersion(path);
            }
            catch (Exception e)
            {
                Unreadable(path, "could not be opened (" + e.Message + ")");
                return;
            }
            if (version <= 0)
            {
                Unreadable(path, "carries no version number");
                return;
            }
            if (version > SchemaVersion)
            {
                // Do not guess at a shape this build does not know, and do not write over it.
                _folder = null;
                _filePath = path;
                _fileVersion = version;
                Disable(path + " is version " + version + ", which this build of the mod does not understand, so per-world settings are off for the session and nothing is written over it");
                _source = _standDown;
                _noFolder = "Per-world settings are off for this session (" + _standDown + "), so nothing can be recorded for this world; nothing was changed.";
                Log.Warn("Per-world settings: " + _source + ". This world's pressure ceiling is off and the rest is from the config.");
                return;
            }

            SidecarFile file;
            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    file = (SidecarFile)Serializer.Deserialize(stream);
                }
            }
            catch (Exception e)
            {
                Unreadable(path, "could not be read (" + (e.InnerException ?? e).Message + ")");
                return;
            }
            if (file == null)
            {
                Unreadable(path, "held nothing");
                return;
            }

            _filePath = path;
            _fileVersion = version;
            Take(file, path);
        }

        /// <summary>
        /// A world from before this existed, a workshop import, a folder copied by hand, or a file
        /// that could not be believed. A setting that destroys state falls back to its no-op value;
        /// one that only changes behaviour falls back to the config. So the ceiling is off whatever
        /// the config says and everything else takes the config: without that rule a player who set
        /// a 100 kPa ceiling for a new world and then loaded an old Venus at 9,000 kPa would lose
        /// 99 % of its air over the next in-game day, permanently, which is the damage this file
        /// exists to prevent.
        ///
        /// Info, not Warn: it is the ordinary state of every world that predates the feature. The
        /// reason always names the file or the folder, because a player has to be able to find the
        /// file the mod has just written.
        /// </summary>
        private static void Missing(string why)
        {
            _source = why + ", so this world's pressure ceiling is off and the rest is from the config";
            Log.Info("Per-world settings: " + _source + ".");
            Save();
        }

        /// <summary>
        /// A file that is there but cannot be believed. The path comes first, because the one thing
        /// a player needs is to be able to find the file that was just replaced, and the copy taken
        /// beside it is what makes a hand edit with a typo in it recoverable.
        /// </summary>
        private static void Unreadable(string path, string why)
        {
            _salvage = path;
            Log.Warn("Per-world settings: " + path + " " + why
                + ", so it is treated as missing: this world's pressure ceiling is off and the rest is from the config. The file as it stands is copied to "
                + SalvagedFileName + " beside it before it is replaced.");
            Missing(path + " could not be read");
        }

        /// <summary>
        /// What the file recorded, field by field, and nothing that is not a setting. Every value is
        /// checked against the same range the config editor enforces on the same setting, read from
        /// the one declaration in <see cref="Limits"/> so the two cannot drift, and a value that is
        /// not a finite number is refused as well. Measured consequences of trusting the file
        /// instead: a recorded ceiling of 0 scales the whole planet to nothing on the next tick, -5
        /// drives every gas negative, an infinity puts the tank at NaN, and a NaN response scale
        /// makes Climate's cache key never match, so its entry is rebuilt for every outdoor cell of
        /// every tick with an error logged each time.
        ///
        /// A field that fails is treated as not recorded at all, which is what the file already
        /// means by a field the version that wrote it did not know: the ceiling falls to none, the
        /// rest fall back to the config.
        ///
        /// Null in the two fields the config flags with 0 is a recorded state, not an absence: no
        /// ceiling, and heat that never fades. A 0 in either of them in the file is the config's
        /// sentinel leaking through a hand edit, and is out of range exactly like a negative is.
        /// </summary>
        private static void Take(SidecarFile file, string path)
        {
            List<string> bad = new List<string>();

            Effective.CeilingFromWorldFile(
                Recorded(file.MaxPressureKPa, Limits.MaxPressureKPa.AboveMin, "MaxPressureKPa", bad));

            if (!file.ExternalHeatHalfLifeMinutes.HasValue)
            {
                Effective.ExternalHeatHalfLifeMinutes = null;               // recorded: never fades
            }
            else
            {
                double? minutes = Recorded(file.ExternalHeatHalfLifeMinutes, Limits.ExternalHeatHalfLifeMinutes.AboveMin,
                    "ExternalHeatHalfLifeMinutes", bad);
                // A refused value is not a recorded "never fades": it falls back to the config,
                // which TakeFromConfig has already put there.
                if (minutes.HasValue)
                {
                    Effective.ExternalHeatHalfLifeMinutes = minutes;
                }
            }

            Effective.MaxExternalOffsetKelvin =
                Recorded(file.MaxExternalOffsetKelvin, Limits.MaxExternalOffsetKelvin, "MaxExternalOffsetKelvin", bad)
                ?? Settings.MaxExternalOffsetKelvin;
            Effective.GhgResponseScale =
                Recorded(file.GhgResponseScale, Limits.GhgResponseScale, "GhgResponseScale", bad)
                ?? Settings.GhgResponseScale;
            Effective.DensityResponseScale =
                Recorded(file.DensityResponseScale, Limits.DensityResponseScale, "DensityResponseScale", bad)
                ?? Settings.DensityResponseScale;
            Effective.AirlessAlbedo =
                Recorded(file.AirlessAlbedo, Limits.AirlessAlbedo, "AirlessAlbedo", bad)
                ?? Settings.AirlessAlbedo;

            // A switch has no range to be outside of, so it is either recorded or not.
            Effective.DynamicSky = file.DynamicSky ?? Settings.DynamicSky;
            Effective.WeatherOnWeatherlessWorlds = file.WeatherOnWeatherlessWorlds ?? Settings.WeatherOnWeatherlessWorlds;
            Effective.StormsStopWhenStripped = file.StormsStopWhenStripped ?? Settings.StormsStopWhenStripped;
            Effective.StrippedAtmosphereShare =
                Recorded(file.StrippedAtmosphereShare, Limits.StrippedAtmosphereShare, "StrippedAtmosphereShare", bad)
                ?? Settings.StrippedAtmosphereShare;
            Effective.StormsStopWhenAtmosphereIsMild = file.StormsStopWhenAtmosphereIsMild ?? Settings.StormsStopWhenAtmosphereIsMild;
            Effective.MildAtmosphereColdestKelvin =
                Recorded(file.MildAtmosphereColdestKelvin, Limits.MildAtmosphereColdestKelvin, "MildAtmosphereColdestKelvin", bad)
                ?? Settings.MildAtmosphereColdestKelvin;
            Effective.MildAtmosphereHottestKelvin =
                Recorded(file.MildAtmosphereHottestKelvin, Limits.MildAtmosphereHottestKelvin, "MildAtmosphereHottestKelvin", bad)
                ?? Settings.MildAtmosphereHottestKelvin;
            Effective.MildAtmosphereMinPressureKpa =
                Recorded(file.MildAtmosphereMinPressureKpa, Limits.MildAtmosphereMinPressureKpa, "MildAtmosphereMinPressureKpa", bad)
                ?? Settings.MildAtmosphereMinPressureKpa;
            Effective.MildAtmosphereMaxPressureKpa =
                Recorded(file.MildAtmosphereMaxPressureKpa, Limits.MildAtmosphereMaxPressureKpa, "MildAtmosphereMaxPressureKpa", bad)
                ?? Settings.MildAtmosphereMaxPressureKpa;
            Effective.MildAtmosphereMaxToxinsKpa =
                Recorded(file.MildAtmosphereMaxToxinsKpa, Limits.MildAtmosphereMaxToxinsKpa, "MildAtmosphereMaxToxinsKpa", bad)
                ?? Settings.MildAtmosphereMaxToxinsKpa;
            Effective.MildAtmosphereStopsSolarStorms = file.MildAtmosphereStopsSolarStorms ?? Settings.MildAtmosphereStopsSolarStorms;
            Effective.TraceGasGathering =
                Recorded(file.TraceGasGathering, Limits.TraceGasGathering, "TraceGasGathering", bad)
                ?? Settings.TraceGasGathering;
            Effective.TraceGasLine =
                Recorded(file.TraceGasLine, Limits.TraceGasLine, "TraceGasLine", bad)
                ?? Settings.TraceGasLine;

            if (bad.Count == 0)
            {
                _source = null;
                return;
            }
            _salvage = path;
            string fields = string.Join(", ", bad.ToArray());
            string them = bad.Count == 1 ? "that field is" : "those fields are";
            _source = "it records " + fields + " outside what the setting accepts, so " + them
                + " treated as not recorded: this world's pressure ceiling is off and the rest is from the config";
            Log.Warn("Per-world settings: " + path + " records " + fields + " outside what the setting accepts, so "
                + them + " treated as not recorded: this world's pressure ceiling is off, and the rest of them fall back to the config."
                + " Fix the file, or set them with terraform set and they will be written back.");
        }

        /// <summary>
        /// One field as the file recorded it: the value when the file recorded a usable one, null
        /// when it recorded none, and null plus a name on the list when what it recorded is not a
        /// value this setting can take.
        /// </summary>
        private static double? Recorded(double? value, Range range, string field, List<string> bad)
        {
            if (!value.HasValue)
            {
                return null;
            }
            if (range.Holds(value.Value))
            {
                return value;
            }
            bad.Add(field + " " + value.Value.ToString("R", CultureInfo.InvariantCulture) + " (allowed: " + range + ")");
            return null;
        }

        /// <summary>
        /// Copies the config into the values in force, except the pressure ceiling, which this
        /// cannot touch: a loaded world's ceiling comes out of its own file or nowhere, and a new
        /// world's out of <see cref="Effective.CeilingForNewWorld"/>. None of the rest can delete
        /// air, so they fall back to the config.
        ///
        /// The ONE place that knows BepInEx cannot express a nullable double, so the config uses 0
        /// as a flag and this file does not: the translation happens here, on the way in, and the
        /// rules downstream ask whether stored heat fades rather than whether its half-life is zero.
        /// </summary>
        private static void TakeFromConfig()
        {
            Effective.ExternalHeatHalfLifeMinutes = Settings.ExternalHeatHalfLifeMinutes > 0.0
                ? (double?)Settings.ExternalHeatHalfLifeMinutes
                : null;
            Effective.MaxExternalOffsetKelvin = Settings.MaxExternalOffsetKelvin;
            Effective.GhgResponseScale = Settings.GhgResponseScale;
            Effective.DensityResponseScale = Settings.DensityResponseScale;
            Effective.AirlessAlbedo = Settings.AirlessAlbedo;
            Effective.DynamicSky = Settings.DynamicSky;
            Effective.WeatherOnWeatherlessWorlds = Settings.WeatherOnWeatherlessWorlds;
            Effective.StormsStopWhenStripped = Settings.StormsStopWhenStripped;
            Effective.StrippedAtmosphereShare = Settings.StrippedAtmosphereShare;
            Effective.StormsStopWhenAtmosphereIsMild = Settings.StormsStopWhenAtmosphereIsMild;
            Effective.MildAtmosphereColdestKelvin = Settings.MildAtmosphereColdestKelvin;
            Effective.MildAtmosphereHottestKelvin = Settings.MildAtmosphereHottestKelvin;
            Effective.MildAtmosphereMinPressureKpa = Settings.MildAtmosphereMinPressureKpa;
            Effective.MildAtmosphereMaxPressureKpa = Settings.MildAtmosphereMaxPressureKpa;
            Effective.MildAtmosphereMaxToxinsKpa = Settings.MildAtmosphereMaxToxinsKpa;
            Effective.MildAtmosphereStopsSolarStorms = Settings.MildAtmosphereStopsSolarStorms;
            Effective.TraceGasGathering = Settings.TraceGasGathering;
            Effective.TraceGasLine = Settings.TraceGasLine;
        }

        /// <summary>
        /// A config entry changed. The config is what a new world starts with and nothing more, so
        /// this never reaches a world being played or its file. With no world in play the values in
        /// force are the config's, so they follow it, which keeps the new-world screen's readouts in
        /// step with what the player is setting up. Under the tank lock, like every other writer.
        /// </summary>
        public static void ConfigEdited()
        {
            GameState state = GameManager.GameState;
            if (state == GameState.Running || state == GameState.Paused)
            {
                return;
            }
            Planet.UnderTankLock(TakeFromConfig);
        }

        /// <summary>The config's ceiling as a rule sees one: a pressure, or nothing at all.</summary>
        private static double? ConfigCeiling()
        {
            return Settings.MaxPressureKPa > 0.0 ? (double?)Settings.MaxPressureKPa : null;
        }

        /// <summary>
        /// Everything in force taken from the config, for the moments when no world is being played:
        /// at startup, so the values in force are seeded by something that says it is seeding them
        /// rather than as a side effect of the config binder calling each apply, and on leaving a
        /// world. The ceiling is off, because a ceiling belongs to a world and there is none.
        /// </summary>
        public static void SeedFromConfig()
        {
            Effective.NoCeiling();
            TakeFromConfig();
            _source = "no world is being played, so the config is in force";
            _noFolder = "No world is being played, so there is nowhere to record settings.";
        }

        /// <summary>
        /// A client has been sent the host's response settings and sky setting with its planet.
        /// </summary>
        public static void TakenFromHost()
        {
            _source = "this is a network client, so no file is read or written; the three response settings and the sky are the host's, sent with its planet, and the rest are not used here";
        }

        // ---- SaveHelper.CreateSaveDirectory (postfix, an Extra so it fails soft) ------------------

        /// <summary>
        /// A new world's folder has just been born, which is the only moment the settings it was
        /// created with can be recorded. The folder comes from the out parameter and not from a
        /// second resolution: the game sets CurrentStationName only once this save has succeeded, so
        /// at this moment it is still null.
        ///
        /// The main menu also creates a folder here to import a workshop save, with no world in
        /// play. That world must land in the missing-file path when it loads, so only a world being
        /// played records anything.
        /// </summary>
        public static void SaveDirectoryPostfix(bool __result, ref DirectoryInfo directoryInfo)
        {
            try
            {
                if (!__result || directoryInfo == null || _standDown != null || NetworkManager.IsClient)
                {
                    return;
                }
                GameState state = GameManager.GameState;
                if (state != GameState.Running && state != GameState.Paused)
                {
                    return;
                }
                _folder = directoryInfo.FullName;
                _noFolder = null;
                if (Save())
                {
                    // What is in force is now what the file records, so the readout must stop
                    // saying this world has yet to be saved.
                    _source = null;
                }
            }
            catch (Exception e)
            {
                // The save itself must be unaffected by anything that happens here.
                WriteFailed(e.Message);
            }
        }

        /// <summary>
        /// Why this world's settings cannot be recorded, or null while they can. The reason is the
        /// one the read recorded, so a world whose folder moved, or whose file could not be read, is
        /// not told instead that it has never been saved.
        /// </summary>
        public static string RecordRefusal()
        {
            if (_standDown != null)
            {
                return "Per-world settings are off for this session (" + _standDown + "), so nothing can be recorded for this world; nothing was changed.";
            }
            if (_folder == null)
            {
                return _noFolder ?? "There is nowhere to record settings for this world; nothing was changed.";
            }
            return null;
        }

        /// <summary>Writes the values in force now. Returns null, or what stopped it.</summary>
        public static string Record()
        {
            string refusal = RecordRefusal();
            if (refusal != null)
            {
                return refusal;
            }
            return Save()
                ? null
                : "This world's settings could not be written to " + _folder + ", so the change holds for this session only. The log says why.";
        }

        // ---- the file ----------------------------------------------------------------------------

        private static bool Save()
        {
            string folder = _folder;
            if (_standDown != null || string.IsNullOrEmpty(folder))
            {
                return false;
            }
            try
            {
                if (!Directory.Exists(folder))
                {
                    return false;           // a new world's folder is born by CreateSaveDirectory, not here
                }
                SidecarFile file = new SidecarFile
                {
                    Version = SchemaVersion,
                    MaxPressureKPa = Effective.MaxPressureKPa,
                    MaxExternalOffsetKelvin = Effective.MaxExternalOffsetKelvin,
                    ExternalHeatHalfLifeMinutes = Effective.ExternalHeatHalfLifeMinutes,
                    GhgResponseScale = Effective.GhgResponseScale,
                    DensityResponseScale = Effective.DensityResponseScale,
                    AirlessAlbedo = Effective.AirlessAlbedo,
                    DynamicSky = Effective.DynamicSky,
                    WeatherOnWeatherlessWorlds = Effective.WeatherOnWeatherlessWorlds,
                    StormsStopWhenStripped = Effective.StormsStopWhenStripped,
                    StrippedAtmosphereShare = Effective.StrippedAtmosphereShare,
                    StormsStopWhenAtmosphereIsMild = Effective.StormsStopWhenAtmosphereIsMild,
                    MildAtmosphereColdestKelvin = Effective.MildAtmosphereColdestKelvin,
                    MildAtmosphereHottestKelvin = Effective.MildAtmosphereHottestKelvin,
                    MildAtmosphereMinPressureKpa = Effective.MildAtmosphereMinPressureKpa,
                    MildAtmosphereMaxPressureKpa = Effective.MildAtmosphereMaxPressureKpa,
                    MildAtmosphereMaxToxinsKpa = Effective.MildAtmosphereMaxToxinsKpa,
                    MildAtmosphereStopsSolarStorms = Effective.MildAtmosphereStopsSolarStorms,
                    TraceGasGathering = Effective.TraceGasGathering,
                    TraceGasLine = Effective.TraceGasLine,
                };
                string path = Path.Combine(folder, FileName);
                Salvage(path);
                // Straight over the file: no temp file, no rename, no delete, so a failure part way
                // through is the worst this can do and nothing of the player's is ever moved.
                using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    Write(stream, file);
                }
                _filePath = path;
                _fileVersion = SchemaVersion;
                return true;
            }
            catch (Exception e)
            {
                WriteFailed(e.Message);
                return false;
            }
        }

        /// <summary>
        /// A copy of a file the mod could not believe, taken once, immediately before the write that
        /// replaces it. The design's own rule is that nothing of the player's is deleted or moved,
        /// and replacing a hand edit outright throws away the settings it got right along with the
        /// one it did not. One fixed name, so a world that loads with a broken file every time
        /// cannot fill its own folder with copies.
        ///
        /// Best effort like everything else here: a copy that fails must not stop the write, or a
        /// read-only folder would leave the world with no settings file at all.
        /// </summary>
        private static void Salvage(string path)
        {
            string source = _salvage;
            _salvage = null;
            if (source == null || !string.Equals(source, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            try
            {
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(Path.GetDirectoryName(source) ?? "", SalvagedFileName), true);
                }
            }
            catch (Exception e)
            {
                Log.Warn("A copy of this world's unreadable settings file could not be kept: " + e.Message
                    + ". It is replaced anyway, so the world loads.");
            }
        }

        private static void WriteFailed(string message)
        {
            if (_writeFaults++ == 0)
            {
                Log.Warn("This world's settings could not be written; the session carries on with them in memory, and nothing was deleted or moved. " + message);
            }
        }

        /// <summary>
        /// The one writer, so the self-test exercises exactly what lands on disk. The xsd and xsi
        /// namespace declarations XmlSerializer puts on the root are deliberately not suppressed:
        /// without them the declaration moves onto each nil element, and a file that lost them
        /// altogether would not parse at all.
        /// </summary>
        internal static void Write(Stream stream, SidecarFile file)
        {
            using (XmlWriter writer = XmlWriter.Create(stream, WriterSettings))
            {
                Serializer.Serialize(writer, file);
            }
        }

        /// <summary>The XML the writer would produce, for the self-test.</summary>
        internal static string ToXml(SidecarFile file)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                Write(stream, file);
                return new UTF8Encoding(false).GetString(stream.ToArray());
            }
        }

        /// <summary>Parses what the writer produced, for the self-test.</summary>
        internal static SidecarFile FromXml(string xml)
        {
            using (MemoryStream stream = new MemoryStream(new UTF8Encoding(false).GetBytes(xml)))
            {
                return (SidecarFile)Serializer.Deserialize(stream);
            }
        }

        /// <summary>
        /// The Version attribute off the root element, without deserializing anything. 0 when it is
        /// absent or not a number, which is the same as unreadable.
        /// </summary>
        internal static int ReadVersion(string path)
        {
            using (XmlReader reader = XmlReader.Create(path))
            {
                reader.MoveToContent();
                string text = reader.GetAttribute("Version");
                return !string.IsNullOrEmpty(text)
                    && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version)
                    ? version
                    : 0;
            }
        }
    }
}
