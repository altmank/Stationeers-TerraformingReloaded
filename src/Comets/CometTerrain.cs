using System.Globalization;
using Assets.Scripts;
using TerrainSystem;
using TerrainSystem.Lods;
using UnityEngine;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// <see cref="CometGround"/> fed from the running game: a column of the voxel terrain, and the
    /// terrain shader's curve settings for this frame. Main thread only.
    ///
    /// The voxels, not a ray: the game gives terrain colliders only to the finest level of detail,
    /// tens of metres around each player, and a comet lands kilometres out.
    /// </summary>
    internal static class CometTerrain
    {
        private static readonly int StartId = Shader.PropertyToID("_GlobalSphereStart");
        private static readonly int AmountId = Shader.PropertyToID("_GlobalSphereAmount");
        private static readonly int FoldId = Shader.PropertyToID("_GlobalSphereFold");
        private static readonly int FoldStartId = Shader.PropertyToID("_GlobalSphereFoldStart");
        private static readonly int LiftId = Shader.PropertyToID("_GlobalSphereLift");

        /// <summary>
        /// The ground under a point: its own column of voxels inside the world, the nearest column on
        /// the world's edge outside it, and <paramref name="fallbackY"/> only where there are no voxels
        /// to read at all. <paramref name="how"/> says which, for the log.
        /// </summary>
        public static Vector3 Ground(Vector3 at, float fallbackY, out string how)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            CometGround rule = CometGround.Shipped;
            if (VoxelTerrain.Octree == null)
            {
                how = string.Format(c, "no voxel terrain loaded, so at {0:0.0} m", fallbackY);
                return new Vector3(at.x, fallbackY, at.z);
            }
            CometGround.Column column = rule.ColumnFor(at.x, at.z, VoxelConstants.Offset);
            float x = (float)column.X;
            float z = (float)column.Z;
            int top = LodManager.Instance != null ? Mathf.FloorToInt(LodManager.Instance.TerrainHeight.y) : CometGround.TopVoxel;
            double? surface = rule.Surface(y => VoxelTerrain.GetDensityAtSize(new Vector3(x, y, z), 1), top);
            if (!surface.HasValue)
            {
                how = string.Format(c, "no solid voxel under ({0:0.#}, {1:0.#}), so at {2:0.0} m", x, z, fallbackY);
                return new Vector3(at.x, fallbackY, at.z);
            }
            how = column.Inside
                ? string.Format(c, "ground {0:0.0} m from the voxels", surface.Value)
                : string.Format(c, "outside the voxel world ({1} m across), so the ground of its nearest edge column ({2:0.#}, {3:0.#}), {0:0.0} m",
                    surface.Value, 2 * VoxelConstants.Offset, x, z);
            return new Vector3(at.x, (float)surface.Value, at.z);
        }

        /// <summary>The terrain shader's curve as the game set it for this frame, from the same globals the shader reads.</summary>
        public static CometGround.Curve CurveNow()
        {
            return new CometGround.Curve(
                Shader.GetGlobalFloat(AmountId),
                Shader.GetGlobalFloat(StartId),
                Shader.GetGlobalFloat(FoldId),
                Shader.GetGlobalFloat(FoldStartId),
                Shader.GetGlobalFloat(LiftId),
                OrbitalViewController.RecenterX,
                OrbitalViewController.RecenterZ);
        }

        /// <summary>Where the game draws a point on the ground for a viewer: its real place, recentred and dropped.</summary>
        public static Vector3 Drawn(Vector3 real, Vector3 viewer, out float drop)
        {
            CometGround.Curve curve = CurveNow();
            drop = (float)CometGround.Shipped.DropAt(real.x, real.z, viewer.x, viewer.z, curve);
            return new Vector3(real.x + (float)curve.RecenterX, real.y - drop, real.z + (float)curve.RecenterZ);
        }
    }
}
