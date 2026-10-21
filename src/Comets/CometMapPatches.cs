using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Serialization;
using Assets.Scripts.UI;
using HarmonyLib;
using ICSharpCode.SharpZipLib.Zip;
using Objects.Rockets;
using Objects.Rockets.UI;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// Where the comets on the space map meet the game: the save and its load, and the guard that keeps
    /// a site a comet holds on the map. Each is its own part in Patcher, so one the game no longer fits
    /// costs only itself. The map icon is <see cref="CometMapIcons"/>.
    /// </summary>
    public static class CometMapPatches
    {
        /// <summary>
        /// The save: the comets go into the .save as an entry of their own, written after the terrain, and
        /// come back when the map and its sites have been rebuilt from the save; a flight goes back on the
        /// clock when the game puts its saved clock back. Refused unless all four places are there, and
        /// without it no comet is made: one that could not be saved would vanish at the next load.
        /// </summary>
        public static void ApplySave(Harmony harmony)
        {
            MethodInfo gather = AccessTools.DeclaredMethod(typeof(XmlSaveLoad), "GetWorldData");
            MethodInfo zip = AccessTools.DeclaredMethod(typeof(SaveHelper), "AddZipFile", new[] { typeof(ZipOutputStream), typeof(System.IO.Stream), typeof(string) });
            MethodInfo load = AccessTools.DeclaredMethod(typeof(SpaceMap), "LoadGameSave");
            MethodInfo clock = AccessTools.DeclaredMethod(typeof(OrbitalSimulation), "DeserializeSave");
            if (gather == null || zip == null || load == null || clock == null)
            {
                throw new MissingMethodException("the save no longer gathers, zips, rebuilds the space map or restores its clock where the comets expect");
            }
            if (AccessTools.Property(typeof(StationSaveContainer), "RootDir") == null)
            {
                throw new MissingMemberException("a loaded save no longer says which folder it was unpacked to");
            }
            // The gathering last: it reaches Unity's own code, so tools/PatchCheck can bind it but not
            // compile it, and the three before it are then still checked there. Until all four are in,
            // no comet is made.
            harmony.Patch(zip, postfix: Body(nameof(ZipPostfix)));
            harmony.Patch(load, postfix: Body(nameof(LoadPostfix)));
            harmony.Patch(clock, postfix: Body(nameof(ClockPostfix)));
            harmony.Patch(gather, postfix: Body(nameof(GatherPostfix)));
            CometMap.MarkSaveInstalled();
        }

        /// <summary>
        /// The guard: a site a comet holds stays on the map. The host's two ways of removing a site (one, or
        /// every one of its kind under a node) skip a held one, and the map's remove button is greyed out
        /// over one, so a player sees why.
        /// </summary>
        public static void ApplyGuard(Harmony harmony)
        {
            MethodInfo one = AccessTools.DeclaredMethod(typeof(RocketMotherboard), "RemoveSpaceMapNode", new[] { typeof(SpaceMapNode) });
            MethodInfo kind = AccessTools.DeclaredMethod(typeof(RocketMotherboard), "RemoveSpaceMapNodesOfType", new[] { typeof(SpaceMapNode) });
            MethodInfo panel = AccessTools.DeclaredMethod(typeof(SelectedDestinationPanel), "Refresh");
            if (one == null || kind == null || panel == null)
            {
                throw new MissingMethodException("the map no longer removes a site where the guard expects");
            }
            _panelLocation = AccessTools.FieldRefAccess<SelectedDestinationPanel, MapLocation>("_location");
            _panelRemove = AccessTools.FieldRefAccess<SelectedDestinationPanel, BasicButton>("_removeNodeButton");
            harmony.Patch(one, prefix: Body(nameof(RemoveOnePrefix)));
            harmony.Patch(kind, prefix: Body(nameof(RemoveKindPrefix)));
            harmony.Patch(panel, postfix: Body(nameof(PanelPostfix)));
        }

        private static AccessTools.FieldRef<SelectedDestinationPanel, MapLocation> _panelLocation;
        private static AccessTools.FieldRef<SelectedDestinationPanel, BasicButton> _panelRemove;

        private static HarmonyMethod Body(string name) =>
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CometMapPatches), name) ?? throw new MissingMethodException(nameof(CometMapPatches), name));

        // ---- the save ---------------------------------------------------------------------------------------

        private static void GatherPostfix()
        {
            try
            {
                CometMap.SnapshotForSave();
            }
            catch (Exception e)
            {
                Log.Error("[TR comet map] The comets could not be gathered for this save; the save itself goes on without them. " + e.Message);
            }
        }

        private static void ZipPostfix(ZipOutputStream zipStream, string fileName) => CometMap.WriteZipEntry(zipStream, fileName);

        private static void LoadPostfix() => CometMap.LoadFromSave();

        private static void ClockPostfix()
        {
            try
            {
                CometMap.ClockRestored();
            }
            catch (Exception e)
            {
                CometMap.Fault("A flight from this save could not be put back on the clock yet: " + e.Message);
            }
        }

        // ---- the guard --------------------------------------------------------------------------------------

        private static bool RemoveOnePrefix(SpaceMapNode node)
        {
            if (NetworkManager.IsClient || !CometMap.Holds(node))
            {
                return true;
            }
            Log.Info("[TR comet map] " + node.DisplayName + " stays on the map: a comet is there or on its way from it.");
            return false;
        }

        /// <summary>The game's own loop, less the sites a comet holds.</summary>
        private static bool RemoveKindPrefix(SpaceMapNode node)
        {
            SpaceMapNode parent = node?.ParentConnection?.Parent;
            if (NetworkManager.IsClient || parent == null || node.Data == null)
            {
                return true;
            }
            List<SpaceMapNode> kind = new List<SpaceMapNode>();
            bool held = false;
            foreach (NodeConnection connection in parent.ChildConnections)
            {
                SpaceMapNode child = connection.Child;
                if (child?.Data != null && child.Data.Id == node.Data.Id)
                {
                    if (CometMap.Holds(child))
                    {
                        held = true;
                    }
                    else
                    {
                        kind.Add(child);
                    }
                }
            }
            if (!held)
            {
                return true;
            }
            foreach (SpaceMapNode child in kind)
            {
                child.DeRegister();
            }
            Log.Info("[TR comet map] Removed " + kind.Count + " sites like " + node.DisplayName + "; those a comet holds stay on the map.");
            return false;
        }

        private static void PanelPostfix(SelectedDestinationPanel __instance)
        {
            try
            {
                MapLocation location = _panelLocation(__instance);
                if (location != null && location.Node != null && CometMap.Holds(location.Node))
                {
                    _panelRemove(__instance)?.SetInteractable(false);
                }
            }
            catch (Exception e)
            {
                CometMap.Fault("The map's remove button could not be greyed out over a comet's site: " + e.Message);
            }
        }
    }
}
