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
