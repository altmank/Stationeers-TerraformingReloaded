using System;
using System.Reflection;
using HarmonyLib;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// The joined player whose command the host is running. The game's serverrun hands a command from a
    /// player's game to the host's console, which runs it without saying who sent it; a prefix on that message
    /// keeps the sender's client id for the length of the one command, so a console comet it calls is aimed
    /// from that player (<see cref="CometViewpoint"/>).
    /// </summary>
    public static class CometInvoker
    {
        [ThreadStatic]
        private static ulong _current;

        private static Func<object, ulong> _clientOf;

        /// <summary>The client id of the player whose serverrun command is running on this thread now, or 0.</summary>
        public static ulong Current => _current;

        /// <summary>Patcher's "comet invoking player".</summary>
        public static void Apply(Harmony harmony)
        {
            Type message = AccessTools.TypeByName("Util.Commands.ServerRunCommandMessage");
            MethodInfo process = message == null ? null : AccessTools.DeclaredMethod(message, "Process", new[] { typeof(long) });
            PropertyInfo client = message == null ? null : AccessTools.DeclaredProperty(message, "ClientId");
            if (process == null || client == null || client.PropertyType != typeof(ulong))
            {
                throw new MissingMethodException("the game's serverrun message no longer carries the client that sent it where the comet expects");
            }
            _clientOf = instance => (ulong)client.GetValue(instance);
            harmony.Patch(process, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CometInvoker), nameof(Prefix))),
                finalizer: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CometInvoker), nameof(Finalizer))));
        }

        private static void Prefix(object __instance)
        {
            try
            {
                _current = __instance != null && _clientOf != null ? _clientOf(__instance) : 0UL;
            }
            catch (Exception)
            {
                _current = 0UL;
            }
        }

        private static Exception Finalizer(Exception __exception)
        {
            _current = 0UL;
            return __exception;
        }
    }
}
