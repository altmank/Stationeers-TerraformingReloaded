using System;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Networking;
using LaunchPadBooster;
using LaunchPadBooster.Networking;
using TerraformingReloaded.Patching;

namespace TerraformingReloaded
{
    /// <summary>
    /// The game never sends the planet tank to clients; each client builds its own from the world
    /// file and would go on seeing the untouched planet. This sends the host's.
    ///
    /// It rides LaunchPadBooster's per-mod sections, which are length-prefixed and skipped by a
    /// client that lacks this mod, so the mod stays optional in multiplayer: a client without it
    /// only misses the outdoor readings and sky, the cells themselves are synced by the game.
    ///
    /// The payload is the game's own save object for the tank, walked by reflection, so a gas added
    /// in a later game version is carried without a change here. Host and client run the same game
    /// build, so they walk the same fields in the same order.
    /// </summary>
    public sealed class Sync : IJoinSuffixSerializer, IUpdateSuffixSerializer
    {
        private const byte Nothing = 0;
        private const byte FullState = 1;

        private static readonly FieldInfo[] MixFields = typeof(GlobalGasMixSaveData)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(DoubleReference))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToArray();

        private int _faults;

        /// <summary>
        /// Kept apart from the plugin's Awake so that a LaunchPadBooster too old to have this API
        /// fails here, where it can be caught, and costs multiplayer sync rather than the whole mod.
        /// </summary>
        public static void Register(string name, string version)
        {
            Mod mod = new Mod(name, version);
            Sync sync = new Sync();
            mod.Networking.Required = false;
            mod.Networking.JoinSuffixSerializer = sync;
            mod.Networking.UpdateSuffixSerializer = sync;
        }

        public void SerializeJoinSuffix(RocketBinaryWriter writer) => Write(writer, true);

        public void DeserializeJoinSuffix(RocketBinaryReader reader) => Read(reader);

        public void SerializeUpdateSuffix(RocketBinaryWriter writer)
        {
            // One update is written per game tick and broadcast. Deciding from the tick number keeps
            // this stateless; the two tick window covers an update that lands a tick late.
            double seconds = Math.Max(1.0, Settings.SyncIntervalSeconds);
            uint every = (uint)Math.Max(2.0, Math.Round(seconds / Math.Max(0.05, GameManager.GameTickSpeedSeconds)));
            Write(writer, GameManager.GameTickCount % every < 2);
        }

        public void DeserializeUpdateSuffix(RocketBinaryReader reader) => Read(reader);

        private void Write(RocketBinaryWriter writer, bool due)
        {
            PlanetaryAtmosphereSaveData data = null;
            if (due && Gate.Enabled())
            {
                try
                {
                    data = PlanetaryAtmosphereSimulation.Save();
                }
                catch (Exception e)
                {
                    Fault("Could not snapshot the planet for clients: " + e.Message);
                }
            }
            if (data?.GlobalGasMix == null || data.LiquidClouds == null || data.IceClouds == null || data.IceCaps == null)
            {
                writer.WriteByte(Nothing);
                return;
            }
            writer.WriteByte(FullState);
            writer.WriteInt32(MixFields.Length);
            WriteMix(writer, data.GlobalGasMix);
            WriteMix(writer, data.LiquidClouds);
            WriteMix(writer, data.IceClouds);
            WriteMix(writer, data.IceCaps);
            writer.WriteDouble(data.LatentOffset?.Value ?? 0.0);
            writer.WriteDouble(data.ExternalOffset?.Value ?? 0.0);
        }

        private void Read(RocketBinaryReader reader)
        {
            try
            {
                if (reader.ReadByte() != FullState)
                {
                    return;
                }
                // A different field count means a different game build. The section is bounded by
                // LaunchPadBooster, so walking away mid-payload is safe.
                if (reader.ReadInt32() != MixFields.Length)
                {
                    Fault("Host sent a planet state this game build cannot read. Ignored.");
                    return;
                }
                PlanetaryAtmosphereSaveData data = new PlanetaryAtmosphereSaveData
                {
                    GlobalGasMix = ReadMix(reader),
                    LiquidClouds = ReadMix(reader),
                    IceClouds = ReadMix(reader),
                    IceCaps = ReadMix(reader),
                    LatentOffset = new DoubleReference(reader.ReadDouble()),
                    ExternalOffset = new DoubleReference(reader.ReadDouble()),
                };
                if (!NetworkManager.IsClient || PlanetaryAtmosphereSimulation.GetGlobalGasMix() == null)
                {
                    return;
                }
                // Every take and clone divides by the tank volume.
                if (data.GlobalGasMix.Volume.Value <= 0.0 || data.LiquidClouds.Volume.Value <= 0.0
                    || data.IceClouds.Volume.Value <= 0.0 || data.IceCaps.Volume.Value <= 0.0)
                {
                    Fault("Host sent a planet state with no volume. Ignored.");
                    return;
                }
                // The revision only decides whether Load discards old saves; this is live data.
                PlanetaryAtmosphereSimulation.Load(data, int.MaxValue);
            }
            catch (Exception e)
            {
                Fault("Could not apply the host's planet state: " + e.Message);
            }
        }

        private static void WriteMix(RocketBinaryWriter writer, GlobalGasMixSaveData mix)
        {
            foreach (FieldInfo field in MixFields)
            {
                writer.WriteDouble((field.GetValue(mix) as DoubleReference)?.Value ?? 0.0);
            }
        }

        private static GlobalGasMixSaveData ReadMix(RocketBinaryReader reader)
        {
            GlobalGasMixSaveData mix = new GlobalGasMixSaveData();
            foreach (FieldInfo field in MixFields)
            {
                double value = reader.ReadDouble();
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    value = 0.0;
                }
                field.SetValue(mix, new DoubleReference(value));
            }
            return mix;
        }

        private void Fault(string message)
        {
            if (++_faults <= 3)
            {
                Log.Warn(message);
            }
        }
    }
}
