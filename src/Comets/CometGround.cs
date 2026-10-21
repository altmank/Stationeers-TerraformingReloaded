using System;
using System.Globalization;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// The ground a comet lands on (docs/COMET.md section 2, "The ground"): how high it is under the
    /// impact, read from the voxel terrain, and how far below that the game draws it for one viewer.
    /// Plain arithmetic with no game or Unity calls: a column of the terrain comes in as a density per
    /// height, so tools/PatchCheck runs <see cref="Check(CometGround)"/> outside the game. The parts
    /// are virtual so PatchCheck can hand the check wrong rules and see each caught.
    ///
    /// The height is real: the host reads it and sends it in the event, and every machine lands the
    /// comet at the same point. The drop is drawn: the terrain shader lowers distant ground by the
    /// viewer's distance from it, so each machine works it out for its own camera, every frame.
    /// </summary>
    public class CometGround
    {
        public static readonly CometGround Shipped = new CometGround();

        /// <summary>
        /// Solid ground: a voxel's density is a byte over 255, and the game counts 127 and up as solid
        /// (VoxelTerrain.UpdateCanAirPass). The line sits halfway to 126 so a density handed over as a
        /// float, rounded either way, still lands on the right side of it.
        /// </summary>
        public const double SolidDensity = 126.5 / 255.0;

        /// <summary>The highest voxel the terrain is built to (LodManager.TerrainHeight).</summary>
        public const int TopVoxel = 1023;

        /// <summary>A column is read this many voxels apart from the top, then voxel by voxel above the first solid one.</summary>
        public const int Stride = 8;

        /// <summary>The distance from the viewer where the terrain starts to curve away (TerrainShaderScript.DEFAULT_SPHERE_START).</summary>
        public const double CurveStartMetres = 144.0;

        /// <summary>
        /// Where the ground under a point is read: the point itself inside the voxel world, else the
        /// nearest column on its edge, since nothing beyond the edge has voxels. The world spans
        /// -<paramref name="halfWidth"/> up to but not including +<paramref name="halfWidth"/> on both
        /// axes; an edge column is read at its middle.
        /// </summary>
        public virtual Column ColumnFor(double x, double z, int halfWidth)
        {
            bool inside = x >= -halfWidth && x < halfWidth && z >= -halfWidth && z < halfWidth;
            if (inside)
            {
                return new Column(x, z, true);
            }
            double last = halfWidth - 0.5;
            return new Column(Math.Max(-halfWidth + 0.5, Math.Min(last, x)), Math.Max(-halfWidth + 0.5, Math.Min(last, z)), false);
        }

        /// <summary>
        /// The top of the highest solid voxel in a column, from <paramref name="top"/> down to 0, or
        /// null when none is solid. Read every <see cref="Stride"/> voxels down from the top, then voxel
        /// by voxel between the first solid one and the air above it, so the answer is the exact top
        /// of the highest solid voxel unless a layer thinner than the stride floats clear of the ground.
        /// </summary>
        public virtual double? Surface(Func<int, double> density, int top)
        {
            int air = top + 1;
            for (int y = top; ; y = Math.Max(0, y - Stride))
            {
                if (density(y) >= SolidDensity)
                {
                    for (int up = air - 1; up > y; up--)
                    {
                        if (density(up) >= SolidDensity)
                        {
                            return up + 1;
                        }
                    }
                    return y + 1;
                }
                if (y == 0)
                {
                    return null;
                }
                air = y;
            }
        }

        /// <summary>
        /// How far below its real height the game draws ground <paramref name="metres"/> from the
        /// viewer, along the ground: the terrain shader's sphere term, its fold term (non-zero only in
        /// the orbital view) and less its lift.
        /// </summary>
        public virtual double Drop(double metres, Curve curve)
        {
            double sphere = Math.Max(0.0, metres - curve.Start) * curve.Amount;
            double fold = Math.Max(0.0, metres - curve.FoldStart) * curve.Fold;
            return 0.001 * (sphere * sphere + fold * fold) - curve.Lift;
        }

        /// <summary>The drop at a point for a viewer, its distance taken as the shader takes it, recentred.</summary>
        public virtual double DropAt(double x, double z, double viewerX, double viewerZ, Curve curve)
        {
            double dx = x + curve.RecenterX - viewerX;
            double dz = z + curve.RecenterZ - viewerZ;
            return Drop(Math.Sqrt(dx * dx + dz * dz), curve);
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

        /// <summary>
        /// The rule worked through on made-up columns and on the drop measured in the game. Returns
        /// the first case that does not hold, or null.
        /// </summary>
        public static string Check(CometGround ground)
        {
            CultureInfo c = CultureInfo.InvariantCulture;

            // Solid below h: the ground is at h, wherever h falls against the stride.
            foreach (int h in new[] { 1, 2, 7, 8, 9, 15, 16, 17, 300, 1015, 1016, 1017, 1022, 1023, 1024 })
            {
                int height = h;
                double? got = ground.Surface(y => y < height ? 1.0 : 0.0, TopVoxel);
                if (got != height)
                {
                    return string.Format(c, "ground solid below {0} m reads at {1}", height, Show(got));
                }
            }
            if (ground.Surface(y => 0.0, TopVoxel).HasValue)
            {
                return "a column with no solid voxel reads as ground";
            }
            double? overhang = ground.Surface(y => y < 200 || (y >= 400 && y < 420) ? 1.0 : 0.0, TopVoxel);
            if (overhang != 420.0)
            {
                return string.Format(c, "ground at 200 m under a ledge from 400 to 420 m reads at {0}, expected the ledge's top, 420", Show(overhang));
            }
            double? atThreshold = ground.Surface(y => y < 100 ? 1.0 : y == 100 ? 127.0 / 255.0 : 0.0, TopVoxel);
            double? underThreshold = ground.Surface(y => y < 100 ? 1.0 : y == 100 ? 126.0 / 255.0 : 0.0, TopVoxel);
            if (atThreshold != 101.0 || underThreshold != 100.0)
            {
                return string.Format(c, "a voxel at 127/255 must count as solid and one at 126/255 must not; read {0} and {1}", Show(atThreshold), Show(underThreshold));
            }

            // The column: the point itself inside, the nearest edge outside (Mars2: 4,096 m across).
            const int half = 2048;
            (double X, double Z, double ExpectX, double ExpectZ, bool Inside)[] columns =
            {
                (0.0, 0.0, 0.0, 0.0, true),
                (-2048.0, 2047.9, -2048.0, 2047.9, true),
                (2048.0, 0.0, 2047.5, 0.0, false),
                (5000.0, -300.0, 2047.5, -300.0, false),
                (-100.0, -9000.0, -100.0, -2047.5, false),
                (-7000.0, 7000.0, -2047.5, 2047.5, false),
            };
            foreach (var (x, z, expectX, expectZ, inside) in columns)
            {
                Column column = ground.ColumnFor(x, z, half);
                if (column.Inside != inside || Math.Abs(column.X - expectX) > 1e-9 || Math.Abs(column.Z - expectZ) > 1e-9)
                {
                    return string.Format(c, "the ground under ({0}, {1}) in a world {2} m across is read at ({3}, {4}), {5}; expected ({6}, {7}), {8}",
                        x, z, 2 * half, column.X, column.Z, column.Inside ? "inside" : "on the edge", expectX, expectZ, inside ? "inside" : "on the edge");
                }
            }

            // The drop, as measured with tools/CometShot at the Mars spawn (curvature 0.3).
            Curve mars = Curve.Flat(0.3);
            foreach (var (metres, measured) in new[] { (1750.0, 232.0), (2000.0, 310.0), (2500.0, 500.0), (3000.0, 734.0) })
            {
                double drop = ground.Drop(metres, mars);
                if (Math.Abs(drop - measured) > 1.0)
                {
                    return string.Format(c, "at {0:N0} m on Mars the ground is drawn {1:0.#} m lower, measured {2} m", metres, drop, measured);
                }
            }
            if (ground.Drop(0.0, mars) != 0.0 || ground.Drop(CurveStartMetres, mars) != 0.0)
            {
                return "the ground is drawn lower within 144 m of the viewer";
            }
            if (Math.Abs(ground.Drop(CurveStartMetres + 2000.0, mars) - 4.0 * ground.Drop(CurveStartMetres + 1000.0, mars)) > 1e-9)
            {
                return "the drop does not grow with the square of the distance past where the curve starts";
            }
            if (ground.Drop(20000.0, Curve.Flat(0.0)) != 0.0)
            {
                return "a world with no curvature draws its ground lower";
            }
            Curve orbital = new Curve(0.5, CurveStartMetres, 4.0, 1884.0, 50.0, 0.0, 0.0);
            if (Math.Abs(ground.Drop(100.0, orbital) + 50.0) > 1e-9 || Math.Abs(ground.Drop(2000.0, orbital) - (0.001 * (928.0 * 928.0 + 464.0 * 464.0) - 50.0)) > 1e-6)
            {
                return "in the orbital view the fold and the lift are not applied";
            }
            Curve recentred = new Curve(0.3, CurveStartMetres, 0.0, 0.0, 0.0, 1000.0, 0.0);
            if (Math.Abs(ground.DropAt(-1000.0, 0.0, 2000.0, 0.0, recentred) - ground.Drop(2000.0, recentred)) > 1e-9
                || Math.Abs(ground.DropAt(1200.0, 1600.0, 0.0, 0.0, mars) - ground.Drop(2000.0, mars)) > 1e-9)
            {
                return "the drop at a point is not taken at its recentred distance from the viewer";
            }
            return null;
        }

        private static string Show(double? metres) => metres.HasValue ? metres.Value.ToString("0.###", CultureInfo.InvariantCulture) : "nothing";

        /// <summary>Where a column of the terrain is read, and whether that is the point asked for. A plain struct: net472 has no IsExternalInit.</summary>
        public readonly struct Column
        {
            public readonly double X;
            public readonly double Z;

            /// <summary>False when the point is outside the voxel world and this is the nearest column on its edge.</summary>
            public readonly bool Inside;

            public Column(double x, double z, bool inside)
            {
                X = x;
                Z = z;
                Inside = inside;
            }
        }

        /// <summary>The terrain shader's curve settings, as the game sets them for this frame.</summary>
        public readonly struct Curve
        {
            /// <summary>The world's curvature (Mars2 0.3).</summary>
            public readonly double Amount;

            /// <summary>Metres from the viewer where the curve starts.</summary>
            public readonly double Start;

            public readonly double Fold;
            public readonly double FoldStart;
            public readonly double Lift;
            public readonly double RecenterX;
            public readonly double RecenterZ;

            public Curve(double amount, double start, double fold, double foldStart, double lift, double recenterX, double recenterZ)
            {
                Amount = amount;
                Start = start;
                Fold = fold;
                FoldStart = foldStart;
                Lift = lift;
                RecenterX = recenterX;
                RecenterZ = recenterZ;
            }

            /// <summary>A world's curvature as it is drawn on the ground, out of the orbital view.</summary>
            public static Curve Flat(double amount) => new Curve(amount, CurveStartMetres, 0.0, 0.0, 0.0, 0.0, 0.0);
        }
    }
}
