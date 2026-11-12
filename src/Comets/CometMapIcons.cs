using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;
using Objects.Rockets;
using Objects.Rockets.UI;
using UnityEngine;
using UnityEngine.UI;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// The comets on the rocket map screen: one icon each, made from the game's own rocket icon and
    /// placed by the same plain interpolation the game places a rocket's with, between the two nodes of
    /// the hop it is in. Told apart from rockets by its tint, its picture and its tooltip.
    ///
    /// The game rebuilds its rocket icons by destroying every child of their parent
    /// (MapView.InitializeRocketIcons), so the icons are made again in a postfix on that, and kept in a
    /// list of their own: the game's own list switches off an icon with no rocket behind it. Each
    /// machine draws its own map from its own copy of the comets, as it does rockets. Nothing here runs
    /// in batch mode, where the map is never drawn.
    /// </summary>
    public static class CometMapIcons
    {
        /// <summary>An icy blue, unlike any colour the game gives a rocket's icon.</summary>
        private static readonly Color Tint = new Color(0.55f, 0.9f, 1f, 1f);

        /// <summary>Below a rocket standing at the same node, so the two can both be seen.</summary>
        private const float OffsetBelow = -14f;

        /// <summary>The game's asteroid pictures for map nodes, in the order tried; none found keeps the rocket's.</summary>
        private static readonly string[] Pictures = { "StatAsteroidFragment", "SparseAsteroid07", "MetallicAsteroid06" };

        private const float TooltipEverySeconds = 0.5f;

        private static AccessTools.FieldRef<MapView, RocketIcon> _prefab;
        private static AccessTools.FieldRef<MapView, Transform> _parent;
        private static AccessTools.FieldRef<RocketIcon, Image> _image;

        private sealed class Icon
        {
            public MapView View;
            public RocketIcon Shown;
            public int CometId;
            public float NextTooltip;
        }

        private static readonly List<Icon> Icons = new List<Icon>();
        private static int _faults;

        public static void Apply(Harmony harmony)
        {
            MethodInfo initialize = AccessTools.DeclaredMethod(typeof(MapView), "InitializeRocketIcons", new[] { typeof(RocketMotherboard) });
            MethodInfo refresh = AccessTools.DeclaredMethod(typeof(MapView), "Refresh");
            if (initialize == null || refresh == null)
            {
                throw new MissingMethodException("the rocket map no longer builds and places its rocket icons where the comets expect");
            }
            _prefab = AccessTools.FieldRefAccess<MapView, RocketIcon>("_rocketIconPrefab");
            _parent = AccessTools.FieldRefAccess<MapView, Transform>("_rocketIconParent");
            _image = AccessTools.FieldRefAccess<RocketIcon, Image>("_image");
            harmony.Patch(initialize, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CometMapIcons), nameof(InitializePostfix))));
            harmony.Patch(refresh, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CometMapIcons), nameof(RefreshPostfix))));
        }

        private static void InitializePostfix(MapView __instance)
        {
            try
            {
                Rebuild(__instance, Shown());
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        private static void RefreshPostfix(MapView __instance)
        {
            try
            {
                CometView[] comets = Shown();
                if (!Matches(__instance, comets))
                {
                    Rebuild(__instance, comets);
                }
                Place(__instance, comets);
            }
            catch (Exception e)
            {
                Fault(e);
            }
        }

        /// <summary>The comets the map shows: every one, or none while comets are off for this world.</summary>
        private static CometView[] Shown() => Patching.Gate.CometsEnabled() ? CometMap.Views() : new CometView[0];

        /// <summary>The icons this map shows are one per comet, in the same order, none destroyed by the game.</summary>
        private static bool Matches(MapView view, CometView[] comets)
        {
            int at = 0;
            foreach (Icon icon in Icons)
            {
                if (icon.View != view)
                {
                    continue;
                }
                if (at >= comets.Length || icon.Shown == null || icon.CometId != comets[at].Id)
                {
                    return false;
                }
                at++;
            }
            return at == comets.Length;
        }

        private static void Rebuild(MapView view, CometView[] comets)
        {
            for (int i = Icons.Count - 1; i >= 0; i--)
            {
                Icon old = Icons[i];
                if (old.View == null || old.Shown == null || old.View == view)
                {
                    if (old.Shown != null)
                    {
                        UnityEngine.Object.Destroy(old.Shown.gameObject);
                    }
                    Icons.RemoveAt(i);
                }
            }
            RocketIcon prefab = _prefab(view);
            Transform parent = _parent(view);
            if (prefab == null || parent == null)
            {
                return;
            }
            Sprite picture = Picture();
            foreach (CometView comet in comets)
            {
                RocketIcon shown = UnityEngine.Object.Instantiate(prefab, parent);
                shown.Initialize(view);
                shown.SetState(selected: false, connected: false);
                if (picture != null)
                {
                    shown.SetMapSprite(picture, null);
                }
                Image image = _image(shown);
                if (image != null)
                {
                    image.color = Tint;
                }
                shown.SetTooltip(Tooltip(comet));
                Icons.Add(new Icon { View = view, Shown = shown, CometId = comet.Id, NextTooltip = Time.unscaledTime + TooltipEverySeconds });
            }
        }

        private static void Place(MapView view, CometView[] comets)
        {
            int at = 0;
            float now = Time.unscaledTime;
            foreach (Icon icon in Icons)
            {
                if (icon.View != view || at >= comets.Length)
                {
                    continue;
                }
                CometView comet = comets[at++];
                Vector3? from = MapPosition(comet.FromNode);
                Vector3? to = MapPosition(comet.ToNode);
                if (from == null || to == null)
                {
                    icon.Shown.SetActive(false);
                    continue;
                }
                icon.Shown.SetActive(true);
                icon.Shown.SetBaseScale(1f);
                icon.Shown.Transform.localPosition = comet.AtNode != 0L
                    ? (comet.AtNode == comet.ToNode ? to.Value : from.Value)
                    : Vector3.Lerp(from.Value, to.Value, (float)comet.Progress);
                icon.Shown.Offset(0f, OffsetBelow);
                if (now >= icon.NextTooltip)
                {
                    icon.NextTooltip = now + TooltipEverySeconds;
                    icon.Shown.SetTooltip(Tooltip(comet));
                }
            }
        }

        /// <summary>Where a node is drawn on this map, or null when the map does not draw it.</summary>
        private static Vector3? MapPosition(long referenceId)
        {
            SpaceMapNode node = SpaceMapNode.Get(referenceId);
            MapLocation location = node?.MapLocation;
            return location != null ? location.GetMapPosition() : (Vector3?)null;
        }

        private static Sprite Picture()
        {
            foreach (string id in Pictures)
            {
                Sprite sprite = NodeIcon.Find(id)?.IconSprite;
                if (sprite != null)
                {
                    return sprite;
                }
            }
            return null;
        }

        private static string Tooltip(CometView comet)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string head = string.Format(c, "Comet {0}: {1:N0} mol, {2:N1} t, {3} engine{4}", comet.Id, comet.TotalMoles, comet.CometKg / 1000.0, comet.Engines, comet.Engines == 1 ? "" : "s");
            string name = SpaceMapNode.Get(comet.ToNode)?.DisplayName ?? "Entry";
            switch (comet.State)
            {
                case MapCometState.Waiting:
                    return head + Environment.NewLine + string.Format(c, "Waiting at its site, {0:N0} mol of fuel", comet.FuelLeft);
                case MapCometState.Flying:
                    return head + Environment.NewLine + string.Format(c, "Flying to {0}, {1:0} % of the route, {2:N0} mol of fuel left", name,
                        comet.RouteShare * 100.0, comet.FuelLeft);
                case MapCometState.Arrived:
                    return head + Environment.NewLine + string.Format(c, "In orbit at {0}, {1:N0} mol of fuel left", name, comet.FuelLeft);
                default:
                    return head + Environment.NewLine + string.Format(c, "Out of fuel, {0:0} % of the way to orbit", comet.RouteShare * 100.0);
            }
        }

        private static void Fault(Exception e)
        {
            if (++_faults <= 3)
            {
                Log.Warn("[TR comet map] The map icon of a comet could not be drawn: " + e.Message);
            }
        }
    }
}
