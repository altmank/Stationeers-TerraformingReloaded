using System;
using System.Collections.Generic;
using Assets.Scripts;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// What a player sees of a comet parked at Entry: a small glow with a tail turned away from the sun, on
    /// the sky where the game's own sky puts the comet's body (CometSkyBodies). Drawn by the mod, not by one of
    /// the game's sky prefabs: the game's rocky bodies are placed at their true scaled distance, a fraction of
    /// a metre from the eye for a low orbit, and a nucleus a few kilometres across would be under a pixel; its
    /// star sprites come from a pool of twenty shared with the world's own and point-like. So the glow sits
    /// 180 m out along the body's direction, inside the game's 190 m sun and 200 m star sphere, facing the
    /// camera, and hides below the horizon. Local to each machine; nothing in batch mode.
    /// </summary>
    internal sealed class CometSkyGlow : MonoBehaviour
    {
        private const float Distance = 180f;
        private const float HeadSize = 2.2f;
        private const float TailLength = 16f;
        private const float TailWidth = 1.8f;

        private static readonly Color HeadTint = new Color(0.85f, 0.95f, 1f, 0.9f);
        private static readonly Color TailTint = new Color(0.6f, 0.85f, 1f, 0.45f);

        private sealed class Drawn
        {
            public int CometId;
            public Transform Head;
            public Transform Tail;
        }

        private readonly List<Drawn> _drawn = new List<Drawn>();
        private Material _headMaterial;
        private Material _tailMaterial;
        private bool _broken;

        private void LateUpdate()
        {
            if (_broken)
            {
                return;
            }
            try
            {
                Draw();
            }
            catch (Exception e)
            {
                _broken = true;
                Clear();
                Log.Warn("[TR comet sky] The comet's glow in the sky could not be drawn; the comet is still in the sky for telescopes. " + e.Message);
            }
        }

        private void Draw()
        {
            CometSkyBodies.Shown[] shown = CometSkyBodies.All;
            Camera camera = CameraController.CurrentCamera ? CameraController.CurrentCamera : Camera.main;
            if (shown.Length == 0 || camera == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Clear();
                return;
            }
            Match(shown);
            Vector3 eye = camera.transform.position;
            Vector3 sun = OrbitalSimulation.WorldSunVector;
            for (int i = 0; i < _drawn.Count; i++)
            {
                Drawn drawn = _drawn[i];
                Vector3 direction = shown[i].Body.WorldVector;
                bool up = direction.y > -0.01f && direction.sqrMagnitude > 0.5f;
                drawn.Head.gameObject.SetActive(up);
                drawn.Tail.gameObject.SetActive(up);
                if (!up)
                {
                    continue;
                }
                Vector3 head = eye + direction * Distance;
                drawn.Head.SetPositionAndRotation(head, Quaternion.LookRotation(head - eye));
                drawn.Head.localScale = new Vector3(HeadSize, HeadSize, 1f);
                // A comet's tail points away from the sun: across the sky from the head, the way the sun is not.
                Vector3 away = -sun - direction * Vector3.Dot(direction, -sun);
                if (away.sqrMagnitude < 1e-6f)
                {
                    drawn.Tail.gameObject.SetActive(false);
                    continue;
                }
                away.Normalize();
                Vector3 tail = head + away * (TailLength * 0.45f);
                drawn.Tail.SetPositionAndRotation(tail, Quaternion.LookRotation(tail - eye, away));
                drawn.Tail.localScale = new Vector3(TailWidth, TailLength, 1f);
            }
        }

        /// <summary>One head and one tail for each comet in the sky, in the same order.</summary>
        private void Match(CometSkyBodies.Shown[] shown)
        {
            bool same = _drawn.Count == shown.Length;
            for (int i = 0; same && i < shown.Length; i++)
            {
                same = _drawn[i].CometId == shown[i].CometId;
            }
            if (same)
            {
                return;
            }
            Clear();
            if (_headMaterial == null)
            {
                _headMaterial = CometArt.Additive(CometArt.Glow, HeadTint);
                _tailMaterial = CometArt.Additive(CometArt.Glow, TailTint);
            }
            foreach (CometSkyBodies.Shown one in shown)
            {
                _drawn.Add(new Drawn { CometId = one.CometId, Head = Quad("Comet head", _headMaterial), Tail = Quad("Comet tail", _tailMaterial) });
            }
        }

        private Transform Quad(string name, Material material)
        {
            GameObject quad = new GameObject(name) { hideFlags = HideFlags.HideInHierarchy };
            quad.transform.SetParent(transform, false);
            quad.AddComponent<MeshFilter>().sharedMesh = CometArt.Quad;
            MeshRenderer renderer = quad.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return quad.transform;
        }

        private void Clear()
        {
            foreach (Drawn drawn in _drawn)
            {
                if (drawn.Head != null)
                {
                    Destroy(drawn.Head.gameObject);
                }
                if (drawn.Tail != null)
                {
                    Destroy(drawn.Tail.gameObject);
                }
            }
            _drawn.Clear();
        }
    }
}
