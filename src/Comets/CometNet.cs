using System;
using System.Runtime.CompilerServices;
using Assets.Scripts.Networking;
using LaunchPadBooster;
using LaunchPadBooster.Networking;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// The instant half of a comet's delivery: one LaunchPadBooster message from the host to every
    /// client the moment it is called, so the start is not held back by the planet sync interval.
    /// The other half rides in the planet sync (Sync, layout 5), for a player who joins during an
    /// event or whose message was lost.
    ///
    /// Everything that touches LaunchPadBooster is in its own method that is never inlined, so a
    /// LaunchPadBooster without mod messages costs the instant delivery and nothing else.
    /// </summary>
    internal static class CometNet
    {
        private static volatile bool _registered;
        private static int _faults;

        /// <summary>Called once, from Sync.Register, on the mod LaunchPadBooster knows TR by.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Register(Mod mod)
        {
            mod.Networking.RegisterMessage<CometMessage>();
            mod.Networking.RegisterMessage<CometMapMessage>();
            _registered = true;
        }

        /// <summary>Host only. Best effort: the planet sync carries the same event within its interval.</summary>
        public static void Broadcast(CometEvent comet)
        {
            if (!_registered || NetworkManager.IsClient)
            {
                return;
            }
            try
            {
                Send(comet);
            }
            catch (Exception e)
            {
                if (++_faults <= 3)
                {
                    Log.Warn("Could not send the comet to joined players at once; they get it with the next planet sync. " + e.Message);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Send(CometEvent comet)
        {
            new CometMessage { Event = comet }.SendAll(-1L);
        }

        /// <summary>
        /// Host only: the comets on the space map as they stand, after any change. Best effort, as the
        /// event is: the planet sync carries the same list within its interval.
        /// </summary>
        public static void BroadcastMap()
        {
            if (!_registered || NetworkManager.IsClient || !NetworkManager.IsServer)
            {
                return;
            }
            try
            {
                SendMap(CometMap.All);
            }
            catch (Exception e)
            {
                if (++_faults <= 3)
                {
                    Log.Warn("Could not send the comets on the space map to joined players at once; they get them with the next planet sync. " + e.Message);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SendMap(MapComet[] comets)
        {
            new CometMapMessage { Comets = comets }.SendAll(-1L);
        }
    }

    /// <summary>
    /// The comets on the space map, host to clients, sent whole after every change: made, sent, arrived,
    /// stranded, or a flight back on the clock after a load. A flight is the state it set off with, so
    /// nothing more is sent while it flies (docs/COMET-ROCKETS.md, stage 3).
    /// </summary>
    internal sealed class CometMapMessage : INetworkMessage
    {
        public MapComet[] Comets;

        /// <summary>The payload bays at Entry and their targets, as read; the host writes its own as they stand.</summary>
        public CometAim.Target[] Targets;

        public void Serialize(RocketBinaryWriter writer) => CometMap.Write(writer, Comets ?? new MapComet[0]);

        public void Deserialize(RocketBinaryReader reader) => Comets = CometMap.Read(reader, out Targets);

        public void Process(long clientId)
        {
            if (Comets != null && NetworkManager.IsClient)
            {
                CometMap.FromHost(Comets, Targets);
            }
        }
    }

    /// <summary>
    /// The comet, host to clients. Processed where LaunchPadBooster reads its packets, which is
    /// NetworkManager.ManagerUpdate on the main thread; the event is handed to
    /// <see cref="CometDirector"/> all the same, which acts on it in its own Update.
    /// </summary>
    internal sealed class CometMessage : INetworkMessage
    {
        public CometEvent Event;

        public void Serialize(RocketBinaryWriter writer) => Event.Write(writer);

        public void Deserialize(RocketBinaryReader reader) => Event = CometEvent.Read(reader);

        public void Process(long clientId)
        {
            if (Event != null && NetworkManager.IsClient)
            {
                CometDirector.FromHost(Event);
            }
        }
    }
}
