using System;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using HarmonyLib;
using UnityEngine;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// The darker sky after a comet (docs/COMET.md section 8): the sky's scattering, the ambient
    /// light and the sun's light, all dimmed by <see cref="Dim"/>. Postfixes on the two methods that
    /// rebuild those every frame from the game's own state, so a scaled value never feeds the next
    /// frame and nothing drifts, and they act whether or not the sky follows the air. Visual only:
    /// solar power is worked out from the irradiance, not from the light this scales.
    /// </summary>
    public static class CometSky
    {
        private static readonly string[] SkyFields = { "worldRayleighColorIntensity", "worldMieColorIntensity", "heightRayleighIntensity" };

        /// <summary>How much dimmer, 0 to 1. Set each frame by the comet's director on the main thread.</summary>
        public static float Dim;

        private static int _faults;

        /// <summary>Refuses, patching nothing, unless both methods and every value they scale are still there.</summary>
        public static void Apply(Harmony harmony)
        {
            Type scattering = typeof(AtmosphericScattering);
            MethodInfo update = AccessTools.DeclaredMethod(scattering, "ManagerUpdate");
            MethodInfo sun = AccessTools.DeclaredMethod(typeof(OrbitalSimulation), "SetSolarIntensity", Type.EmptyTypes);
            MethodInfo uniforms = AccessTools.DeclaredMethod(scattering, "UpdateStaticUniforms", Type.EmptyTypes);
            MethodInfo ambient = AccessTools.DeclaredMethod(typeof(AmbientLightingReference), "Apply", new[] { typeof(float) });
            if (update == null || sun == null || uniforms == null || ambient == null)
            {
                throw new MissingMethodException("a method the darker sky scales or calls is gone");
            }
            foreach (string field in SkyFields)
            {
                FieldInfo found = AccessTools.Field(scattering, field);
                if (found == null || found.FieldType != typeof(float) || found.IsStatic)
                {
                    throw new MissingFieldException("AtmosphericScattering." + field + " is gone or no longer a float");
                }
            }
            if (AccessTools.Field(typeof(OrbitalSimulation), "WorldSun") == null)
            {
                throw new MissingFieldException("OrbitalSimulation.WorldSun is gone");
            }
            harmony.Patch(update, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CometSky), nameof(SkyPostfix))));
            harmony.Patch(sun, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CometSky), nameof(SunPostfix))));
        }

        /// <summary>
        /// After the sky has been rebuilt for this frame. The game returns early while paused or out of
        /// a running world, which leaves nothing rebuilt to scale, and without scattering it only sets
        /// the ambient level.
        /// </summary>
        public static void SkyPostfix(AtmosphericScattering __instance)
        {
            float dim = Dim;
            if (!(dim > 0f) || GameManager.GameState != GameState.Running || WorldManager.IsGamePaused)
            {
                return;
            }
            try
            {
                float k = 1f - Mathf.Clamp01(dim);
                if (WorldManager.AtmosphericScattering && OrbitalSimulation.WorldSun && __instance != null)
                {
                    __instance.worldRayleighColorIntensity *= k;
                    __instance.worldMieColorIntensity *= k;
                    __instance.heightRayleighIntensity *= k;
                    __instance.UpdateStaticUniforms();
                    WorldSetting.Current?.AmbientLighting?.Apply(AtmosphericScattering.FogIntensity * k);
                }
                RenderSettings.ambientIntensity *= k;
            }
            catch (Exception e)
            {
                if (++_faults <= 3)
                {
                    Log.Warn("The darker sky after a comet could not be drawn this frame: " + e.Message);
                }
            }
        }

        /// <summary>After the sun's light has been set for this frame.</summary>
        public static void SunPostfix()
        {
            float dim = Dim;
            if (!(dim > 0f) || !OrbitalSimulation.WorldSun)
            {
                return;
            }
            OrbitalSimulation.WorldSun.intensity *= 1f - Mathf.Clamp01(dim);
        }
    }
}
