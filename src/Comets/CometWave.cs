using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using UnityEngine;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// What the pressure wave does (docs/COMET.md sections 4 and 5): the game's own break, which side of a
    /// structure is outdoors, the push and the suit damage on a player outdoors, and the sounds of what
    /// breaks. Which structures and players it reaches is the impact's one pass, <see cref="CometZonePass"/>.
    /// Main thread only.
    /// </summary>
    internal static class CometWave
    {
        private static readonly int GlassFail = Animator.StringToHash("GlassWallFail");
        private static readonly int MetalFail = Animator.StringToHash("MetalWallFail");
        private static readonly int CompositeFail = Animator.StringToHash("CompositeWallFail");
        private static readonly int GlassStress = Animator.StringToHash("GlassWallStress");

        /// <summary>The game's creak of a window taken past most of its limit.</summary>
        internal static int StressSound => GlassStress;

        /// <summary>
        /// The break is the game's own: damage set to the maximum, which on the host syncs, switches
        /// to the broken model or removes the piece, and lets the air out. False if there was nothing
        /// left to break by the time the wave arrived.
        /// </summary>
        public static bool Break(Structure structure)
        {
            if (structure == null || structure.IsBroken || structure.CanAirPass || structure.Indestructable)
            {
                return false;
            }
            IndestructableDamageState damage = structure.DamageState;
            if (damage == null)
            {
                return false;
            }
            damage.Damage(ChangeDamageType.Set, damage.MaxDamage, DamageUpdateType.Brute);
            return true;
        }

        /// <summary>
        /// At least one side of one of its faces is outdoors. A window deep inside a base has a room
        /// on both sides and is shielded by the rooms around it. A structure that fills a cell rather
        /// than a face counts as facing out when a cell next to it is outdoors.
        /// </summary>
        public static bool FacesOutside(Structure structure)
        {
            RoomController rooms = RoomController.World;
            if (rooms == null)
            {
                return true;
            }
            Grid3[] grids = structure.BlockingGrids;
            bool anyFace = false;
            if (grids != null)
            {
                foreach (Grid3 grid in grids)
                {
                    Vector3 p = grid.ToVector3();
                    Vector3 offset = p - new WorldGrid(p).Value.ToVector3();
                    Vector3 normal = Mathf.Abs(offset.x) >= Mathf.Abs(offset.y) && Mathf.Abs(offset.x) >= Mathf.Abs(offset.z) ? new Vector3(Mathf.Sign(offset.x), 0f, 0f)
                        : Mathf.Abs(offset.y) >= Mathf.Abs(offset.z) ? new Vector3(0f, Mathf.Sign(offset.y), 0f)
                        : new Vector3(0f, 0f, Mathf.Sign(offset.z));
                    if (Vector3.Dot(offset, normal) < 0.5f)
                    {
                        continue;
                    }
                    anyFace = true;
                    if (rooms.GetRoom(new WorldGrid(p - normal * 0.5f)) == null || rooms.GetRoom(new WorldGrid(p + normal * 0.5f)) == null)
                    {
                        return true;
                    }
                }
            }
            if (anyFace)
            {
                return false;
            }
            Vector3 centre = new WorldGrid(structure.CenterPosition).Value.ToVector3();
            foreach (Vector3 step in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
            {
                if (rooms.GetRoom(new WorldGrid(centre + step * 2f)) == null)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Not in a room, not seated, not inside anything: the game's own test for weather exposure.</summary>
        public static bool Outdoors(Human human)
        {
            return human != null && human.Room == null && human.ParentSlot == null && !human.LockedToSeat;
        }

        /// <summary>
        /// Host: the suit and helmet damage of a hard push, through the game's own collision hit, so
        /// the suit leaks, takes damage and plays the game's sounds, and duct tape fixes it.
        /// </summary>
        public static bool Hurt(Human human, Vector3 impact, CometRules.Push push)
        {
            if (push.SuitImpulse <= 0.0 || human == null || human.IsDead || human.Invulnerability > 0f || !Outdoors(human))
            {
                return false;
            }
            Vector3 away = Away(human.transform.position, impact);
            Vector3 contact = human.transform.position + Vector3.up - away * 0.3f;
            human.OnNetworkCollision(contact, away * (float)push.Away + Vector3.up * (float)push.Up, null, (float)push.SuitImpulse);
            return true;
        }

        /// <summary>
        /// This machine's own player, thrown away from the impact and upward. Only the machine a player
        /// plays on can move them: the game overwrites any force the host puts on someone else.
        /// </summary>
        public static bool Shove(Vector3 impact, CometRules.Push push)
        {
            Human human = InventoryManager.ParentHuman;
            if (push.Band == 0 || human == null || !human.HasAuthority || human.IsDead || human.IsRagdoll || !Outdoors(human))
            {
                return false;
            }
            Rigidbody body = human.ActiveRigidbody;
            if (body == null || body.isKinematic)
            {
                return false;
            }
            Vector3 away = Away(human.transform.position, impact);
            body.AddForce(away * (float)push.Away + Vector3.up * (float)push.Up, ForceMode.VelocityChange);
            return true;
        }

        private static Vector3 Away(Vector3 from, Vector3 impact)
        {
            Vector3 away = Vector3.ProjectOnPlane(from - impact, Vector3.up);
            return away.sqrMagnitude > 1e-6f ? away.normalized : Vector3.forward;
        }

        internal static bool IsGlass(Structure structure)
        {
            return structure is Wall wall
                ? wall.WallMaterialType == Wall.WallMaterial.Glass
                : structure.PrefabName != null && structure.PrefabName.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static int FailSound(Structure structure)
        {
            if (IsGlass(structure))
            {
                return GlassFail;
            }
            if (structure is Wall wall)
            {
                return wall.WallMaterialType == Wall.WallMaterial.Composite ? CompositeFail : MetalFail;
            }
            return structure.PrefabName != null && structure.PrefabName.IndexOf("Composite", StringComparison.OrdinalIgnoreCase) >= 0 ? CompositeFail : MetalFail;
        }
    }
}
