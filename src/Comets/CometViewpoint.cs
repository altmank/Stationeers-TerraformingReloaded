using System;
using System.Globalization;

namespace TerraformingReloaded.Comets
{
    /// <summary>Whose view a console comet is aimed from (docs/COMET.md section 2, "Where it lands").</summary>
    public enum ViewSource
    {
        /// <summary>This machine's camera: the player's own view, or the screenshot rig's camera.</summary>
        Camera,

        /// <summary>This machine's own character, where it has no camera.</summary>
        OwnCharacter,

        /// <summary>The player who ran the command from their own game (the game's serverrun).</summary>
        Invoker,

        /// <summary>The first player online.</summary>
        OnlinePlayer,

        /// <summary>Nobody: the world's origin, facing north.</summary>
        WorldOrigin,
    }

    /// <summary>What a machine has to aim a console comet from, as plain facts.</summary>
    public readonly struct ViewFacts
    {
        /// <summary>A batch-mode instance: a dedicated server, or the screenshot rig.</summary>
        public readonly bool BatchMode;

        /// <summary>The machine draws frames (a graphics device that is not the null device).</summary>
        public readonly bool Draws;

        /// <summary>A camera is found: the game's own, or any scene camera tagged as the main one.</summary>
        public readonly bool HasCamera;

        public readonly bool HasOwnCharacter;

        /// <summary>The command came from a joined player through serverrun, and that player's character is found.</summary>
        public readonly bool HasInvoker;

        public readonly bool HasOnlinePlayer;

        public ViewFacts(bool batchMode, bool draws, bool hasCamera, bool hasOwnCharacter, bool hasInvoker, bool hasOnlinePlayer)
        {
            BatchMode = batchMode;
            Draws = draws;
            HasCamera = hasCamera;
            HasOwnCharacter = hasOwnCharacter;
            HasInvoker = hasInvoker;
            HasOnlinePlayer = hasOnlinePlayer;
        }

        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "batch {0}, draws {1}, camera {2}, own character {3}, invoker {4}, player online {5}", BatchMode, Draws, HasCamera, HasOwnCharacter, HasInvoker, HasOnlinePlayer);
    }

    /// <summary>
    /// The rule for whose view a console comet is aimed from. The player who ran the command comes first, on
    /// any machine. A machine a person plays on takes its camera, then its own character. A batch-mode instance
    /// is a dedicated server: its scene still holds the game's main camera, standing wherever the scene left it
    /// and never drawn, so it takes the first player online, then a camera only where frames are drawn (the
    /// screenshot rig, which stands that camera where it wants the comet seen from), then the world's origin.
    /// Plain logic with no game or Unity calls, so tools/PatchCheck runs <see cref="Check(CometViewpoint)"/>
    /// outside the game; virtual so it can hand the check wrong rules.
    /// </summary>
    public class CometViewpoint
    {
        public static readonly CometViewpoint Shipped = new CometViewpoint();

        public virtual ViewSource Choose(ViewFacts facts)
        {
            if (facts.HasInvoker)
            {
                return ViewSource.Invoker;
            }
            if (!facts.BatchMode)
            {
                return facts.HasCamera ? ViewSource.Camera
                    : facts.HasOwnCharacter ? ViewSource.OwnCharacter
                    : facts.HasOnlinePlayer ? ViewSource.OnlinePlayer
                    : ViewSource.WorldOrigin;
            }
            if (facts.HasOnlinePlayer)
            {
                return ViewSource.OnlinePlayer;
            }
            return facts.HasCamera && facts.Draws ? ViewSource.Camera : ViewSource.WorldOrigin;
        }

        private static readonly Lazy<string> ShippedVerdict = new Lazy<string>(() =>
        {
            try
            {
                return Check(Shipped);
            }
            catch (Exception e)
            {
                return "the check itself failed: " + e.Message;
            }
        });

        /// <summary>The shipped rule's own check, run once: null when it holds, else the first case that does not.</summary>
        public static string Verdict => ShippedVerdict.Value;

        /// <summary>The machines a console comet can be called on, each with the view it must be aimed from. Null when every one holds.</summary>
        public static string Check(CometViewpoint rule)
        {
            var cases = new (string What, ViewFacts Facts, ViewSource Expected)[]
            {
                ("a dedicated server, its scene camera never drawn, a player online", new ViewFacts(true, false, true, false, false, true), ViewSource.OnlinePlayer),
                ("a dedicated server, the command run by a joined player, another online", new ViewFacts(true, false, true, false, true, true), ViewSource.Invoker),
                ("a dedicated server with nobody online", new ViewFacts(true, false, true, false, false, false), ViewSource.WorldOrigin),
                ("a dedicated server with no camera and nobody online", new ViewFacts(true, false, false, false, false, false), ViewSource.WorldOrigin),
                ("the screenshot rig, drawing its camera, nobody online", new ViewFacts(true, true, true, false, false, false), ViewSource.Camera),
                ("a batch instance drawing frames, a player online", new ViewFacts(true, true, true, false, false, true), ViewSource.OnlinePlayer),
                ("a game a person plays, single player or hosting", new ViewFacts(false, true, true, true, false, false), ViewSource.Camera),
                ("a hosting game, the command run by a joined player", new ViewFacts(false, true, true, true, true, true), ViewSource.Invoker),
                ("a game with a character and no camera", new ViewFacts(false, true, false, true, false, true), ViewSource.OwnCharacter),
                ("a game with neither a camera nor a character, a player online", new ViewFacts(false, true, false, false, false, true), ViewSource.OnlinePlayer),
                ("a game with nothing to look from", new ViewFacts(false, true, false, false, false, false), ViewSource.WorldOrigin),
            };
            foreach (var one in cases)
            {
                ViewSource got = rule.Choose(one.Facts);
                if (got != one.Expected)
                {
                    return string.Format(CultureInfo.InvariantCulture, "{0} ({1}) aims from {2}, expected {3}", one.What, one.Facts, got, one.Expected);
                }
            }
            return null;
        }
    }
}
