using System;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;

namespace TerraformingReloaded.Patching
{
    /// <summary>Why the planet keeps a side of its fire back from the outdoor air. Exactly one of the three below.</summary>
    public abstract class FireHold
    {
        private protected FireHold()
        {
        }

        public abstract HeldGases Gases { get; }
    }

    /// <summary>Nothing is held for the fire: it is off, the planet's air would not light itself, or nothing burns.</summary>
    public sealed class NoFireHold : FireHold
    {
        public static readonly NoFireHold Instance = new NoFireHold();

        private NoFireHold()
        {
        }

        public override HeldGases Gases => HeldGases.None;
    }

    /// <summary>The armed hold-back: the planet's air would light itself, so the side its fire would use up is held whether or not anything burns yet.</summary>
    public sealed class ArmedHold : FireHold
    {
        public ArmedHold(HeldGases gases)
        {
            Gases = gases;
        }

        public override HeldGases Gases { get; }
    }

    /// <summary>The planet burns and holds back the side it uses up for as long as it burns: a fire a spark lit, or any fire with the armed hold-back off.</summary>
    public sealed class BurningHold : FireHold
    {
        public BurningHold(HeldGases gases)
        {
            Gases = gases;
        }

        public override HeldGases Gases { get; }
    }

    /// <summary>
    /// Everything the planet keeps back from the outdoor air in one tick, and why: for its fire, and
    /// as a trace too thin for an outdoor cell to keep. Immutable, built once a planet tick.
    /// </summary>
    public sealed class PlanetHold
    {
        public static readonly PlanetHold Nothing = new PlanetHold(NoFireHold.Instance, HeldGases.None);

        public readonly FireHold Fire;

        /// <summary>Gases the trace hold keeps (<see cref="TraceHoldRule"/>).</summary>
        public readonly HeldGases Trace;

        private PlanetHold(FireHold fire, HeldGases trace)
        {
            Fire = fire;
            Trace = trace;
        }

        public HeldGases All => Fire.Gases.With(Trace);

        public bool IsEmpty => All.IsEmpty;

        /// <summary>
        /// The tick's hold from what the fire decided and the two world settings. With the armed
        /// hold-back on and the planet's air past its ignition point, the side <paramref name="armed"/>
        /// names is held, burning or not, along with whatever a fire is using up. Otherwise the side a
        /// fire uses up is held while it burns, as before the setting existed. With the trace hold
        /// on, <paramref name="thin"/> is held too; off, it is handed out and the game deletes it.
        /// </summary>
        public static PlanetHold Of(FireTick tick, HeldGases burning, HeldGases armed, bool armedHoldBack, HeldGases thin, bool traceHold)
        {
            FireHold fire = armedHoldBack && !armed.IsEmpty ? new ArmedHold(armed.With(burning))
                : tick is Burning && !burning.IsEmpty ? new BurningHold(burning)
                : NoFireHold.Instance;
            return new PlanetHold(fire, traceHold ? thin : HeldGases.None);
        }

        /// <summary>The same fire hold with <paramref name="thin"/> as the trace hold.</summary>
        public PlanetHold WithTrace(HeldGases thin) => new PlanetHold(Fire, thin);

        /// <summary>"armed hold-back", "while burning", "trace hold", or two of them, for one gas the hold keeps.</summary>
        public string Reasons(Chemistry.GasType type)
        {
            string fire = !Fire.Gases.Contains(type) ? null
                : Fire switch
                {
                    ArmedHold _ => "armed hold-back",
                    BurningHold _ => "while burning",
                    NoFireHold _ => null,
                    _ => throw new InvalidOperationException("a fire hold of an unknown kind"),
                };
            string trace = Trace.Contains(type) ? "trace hold" : null;
            return fire != null && trace != null ? fire + " and " + trace : fire ?? trace;
        }
    }

    /// <summary>
    /// The trace hold. An outdoor cell beside open ground relaxes toward the planet every tick
    /// (Atmosphere.LerpToGlobalAtmosphere, Atmosphere.cs:1710-1716): it takes one cell's worth of the
    /// planet's air (PlanetaryAtmosphereSimulation.TakeGlobalGasMix, :126-139) and keeps a share t of
    /// the difference (Mole.Lerp, Mole.cs:1191-1200, by RocketMath.Lerp, RocketMath.cs:599-602), where
    /// t is AtmosphereHelper.LerpRate (AtmosphereHelper.cs:435-438). Later the same tick the cell's
    /// Cleanup (AtmosphericsController.cs:262, Atmosphere.cs:2181-2194) clears every gas it holds
    /// under Chemistry.MINIMUM_QUANTITY_MOLES (Mole.Cleanup, Mole.cs:1042-1048; Chemistry.cs:193).
    /// So a gas whose cell's worth times t is under that minimum is drawn from the planet into a cell
    /// that started the tick without it and deleted, every tick, by every exchanging cell. The trace
    /// hold keeps such a gas in the planet instead.
    ///
    /// Virtual so tools/PatchCheck can hand the check a rule with its test broken.
    /// </summary>
    public class TraceHoldRule
    {
        public static readonly TraceHoldRule Shipped = new TraceHoldRule();

        /// <summary>The gases the planet holds some of, but so little that an exchanging cell's draw of each is deleted.</summary>
        public HeldGases Thin(GlobalGasMix tank, double exchangeRate)
        {
            double volume = tank?.Volume.ToDouble() ?? 0.0;
            if (!(volume > 0.0) || double.IsInfinity(volume))
            {
                return HeldGases.None;
            }
            double perCell = Chemistry.GridVolume.ToDouble() / volume;
            HeldGases thin = HeldGases.None;
            foreach (Chemistry.GasType type in FireGases.Gases)
            {
                double share = tank.Get(type).ToDouble() * perCell;
                if (share > 0.0 && Deleted(share, exchangeRate))
                {
                    thin = thin.With(HeldGases.Of(new[] { type }));
                }
            }
            return thin;
        }

        /// <summary>
        /// The game's own test on a cell that starts the tick without the gas: what the lerp leaves it,
        /// 0 + (share - 0) x t with t clamped to 0..1 as RocketMath.Lerp clamps it, under the least a
        /// cell keeps. Written out because RocketMath.Lerp calls Math.Clamp, which tools/PatchCheck's
        /// runtime lacks; the arithmetic is the game's MoleQuantity product.
        /// </summary>
        public virtual bool Deleted(double share, double exchangeRate)
        {
            double t = Math.Max(0.0, Math.Min(1.0, exchangeRate));
            MoleQuantity kept = MoleQuantity.Zero + new MoleQuantity(share) * new MoleQuantity(t);
            return kept < Chemistry.MINIMUM_QUANTITY_MOLES;
        }
    }
}
