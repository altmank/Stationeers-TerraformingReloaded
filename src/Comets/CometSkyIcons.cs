using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Assets.Scripts;
using HarmonyLib;
using UI.Motherboard;
using UnityEngine;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// The Map motherboard's two icons for a parked comet (docs/COMET-ROCKETS.md, "Tracking"): the target a
    /// rocket at Entry aims it at, and where it lands if it is brought down now, which slides along the line
    /// under the comet as it passes. Drawn with the panel's own trackable icon, in the same place a beacon's
    /// would be, while the map's trackables switch is on; named in the panel's own text when the map is
    /// clicked. Each machine draws them from its own copy of the comets and the targets (CometAim), so they
    /// stand in the same place on every machine.
    ///
    /// They are not entries in the game's list of trackables. The panel draws only beacons from that list
    /// (MapMotherboardPanel.UpdateTrackables), and the handheld Tracker steps through it as world things, so
    /// an entry that is not a thing would be a blank stop there. Nothing here is a world thing, so nothing is
    /// saved: the icons are worked out again from the comets every time the panel updates.
    /// </summary>
    public static class CometSkyIcons
    {
        /// <summary>The target: the orange of the game's warnings.</summary>
        private static readonly Color TargetColour = new Color(1f, 0.55f, 0.1f, 1f);

        /// <summary>Where it lands now: the comet's icy blue, as on the rocket map.</summary>
        private static readonly Color LandingColour = new Color(0.55f, 0.9f, 1f, 1f);

        private static AccessTools.FieldRef<MapMotherboardPanel, List<MapMotherboardPanelTrackableIconData>> _data;
        private static AccessTools.FieldRef<MapMotherboardPanel, TrackableCollection> _collection;
        private static AccessTools.FieldRef<MapMotherboardPanel, MapMotherboardPanelTrackableIcon> _prefab;
        private static AccessTools.FieldRef<MapMotherboardPanel, Transform> _parent;
        private static AccessTools.FieldRef<MapMotherboardPanel, RectTransform> _root;
        private static AccessTools.FieldRef<MapMotherboardPanelMouseHandler, MapMotherboardPanel> _clickedPanel;
        private static AccessTools.FieldRef<MapMotherboardPanelMouseHandler, StringBuilder> _clickedText;
        private static int _faults;

        public static void Apply(Harmony harmony)
        {
            MethodInfo update = AccessTools.DeclaredMethod(typeof(MapMotherboardPanel), nameof(MapMotherboardPanel.UpdateTrackables));
            MethodInfo click = AccessTools.DeclaredMethod(typeof(MapMotherboardPanelMouseHandler), nameof(MapMotherboardPanelMouseHandler.OnPointerClick));
            MethodInfo refill = AccessTools.DeclaredMethod(typeof(TrackableCollection).BaseType, "Update",
                new[] { typeof(List<MapMotherboardPanelTrackableIconData>), typeof(MapMotherboardPanelTrackableIcon), typeof(Transform) });
            if (update == null || click == null || refill == null)
            {
                throw new MissingMethodException("the Map motherboard no longer draws its trackables or answers a click where the comet's icons expect");
            }
            _data = AccessTools.FieldRefAccess<MapMotherboardPanel, List<MapMotherboardPanelTrackableIconData>>("_trackableData");
            _collection = AccessTools.FieldRefAccess<MapMotherboardPanel, TrackableCollection>("_trackables");
            _prefab = AccessTools.FieldRefAccess<MapMotherboardPanel, MapMotherboardPanelTrackableIcon>("_trackablePrefab");
            _parent = AccessTools.FieldRefAccess<MapMotherboardPanel, Transform>("_trackableParent");
            _root = AccessTools.FieldRefAccess<MapMotherboardPanel, RectTransform>("_mapRoot");
            _clickedPanel = AccessTools.FieldRefAccess<MapMotherboardPanelMouseHandler, MapMotherboardPanel>("_mapMotherboardPanel");
            _clickedText = AccessTools.FieldRefAccess<MapMotherboardPanelMouseHandler, StringBuilder>("_stringBuilder");
            harmony.Patch(update, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CometSkyIcons), nameof(UpdatePostfix))));
            harmony.Patch(click, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CometSkyIcons), nameof(ClickPostfix))));
        }

        /// <summary>The game's beacons are in the panel's list; the comet's icons join them and the panel is drawn again with both.</summary>
        private static void UpdatePostfix(MapMotherboardPanel __instance)
        {
            try
            {
                if (GameManager.IsBatchMode || __instance.TrackablesToggle == null || !__instance.TrackablesToggle.On)
                {
                    return;
                }
                CometAim.Icon[] icons = CometAim.Icons();
                if (icons.Length == 0)
                {
                    return;
                }
                List<MapMotherboardPanelTrackableIconData> data = _data(__instance);
                TrackableCollection collection = _collection(__instance);
                RectTransform root = _root(__instance);
                if (data == null || collection == null || root == null)
                {
                    return;
                }
                foreach (CometAim.Icon icon in icons)
                {
                    data.Add(new MapMotherboardPanelTrackableIconData
                    {
                        Id = icon.Id,
                        WorldPosition = new Vector3((float)icon.X, 0f, (float)icon.Z),
                        Scale = root.localScale.x,
                        Color = icon.IsTarget ? TargetColour : LandingColour,
                    });
                }
                collection.Update(data, _prefab(__instance), _parent(__instance));
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        /// <summary>A click on the map: the panel's own text, then the comet's icons by name, where each stands and how far.</summary>
        private static void ClickPostfix(MapMotherboardPanelMouseHandler __instance)
        {
            try
            {
                CometAim.Aim[] aims = CometAim.All;
                CometAim.Icon[] icons = CometAim.IconsOf(aims);
                MapMotherboardPanel panel = _clickedPanel(__instance);
                StringBuilder text = _clickedText(__instance);
                if (icons.Length == 0 || panel == null || text == null)
                {
                    return;
                }
                text.AppendLine();
                text.Append(Describe(icons, aims, CometEvent.Clock));
                panel.SetInfoText(text.ToString());
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        /// <summary>The icons as the map's text names them.</summary>
        public static string Describe(CometAim.Icon[] icons, CometAim.Aim[] aims, double now)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < icons.Length; i++)
            {
                CometAim.Icon icon = icons[i];
                string colour = icon.IsTarget ? "orange" : "#8ce6ff";
                text.Append("<color=").Append(colour).Append('>').Append(icon.Name).Append("</color> <color=grey>[")
                    .Append(Math.Round(icon.X).ToString(c)).Append(", ").Append(Math.Round(icon.Z).ToString(c)).Append("]</color>");
                if (!icon.IsTarget && icon.Aim >= 0 && icon.Aim < aims.Length)
                {
                    CometAim.Aim aim = aims[icon.Aim];
                    double gap = Math.Sqrt((aim.LandX - aim.TargetX) * (aim.LandX - aim.TargetX) + (aim.LandZ - aim.TargetZ) * (aim.LandZ - aim.TargetZ));
                    text.Append(aim.Tracked ? string.Format(c, ", {0:N0} m from the target", gap) : ", no target");
                    if (!double.IsNaN(aim.NextHighestClock))
                    {
                        text.Append(string.Format(c, ", highest in {0:N0} s", Math.Max(0.0, aim.NextHighestClock - now)));
                    }
                }
                text.AppendLine();
            }
            return text.ToString();
        }

        private static void Fault(Exception e)
        {
            if (++_faults <= 3)
            {
                Log.Warn("[TR comet icons] A comet's icon on the Map motherboard could not be drawn: " + e.Message);
            }
        }
    }
}
