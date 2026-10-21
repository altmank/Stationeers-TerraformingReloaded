using System;
using System.Collections.Generic;
using Assets.Scripts.Sound;
using Assets.Scripts.Util;
using Sound;
using UnityEngine;
using UnityEngine.Audio;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// What a comet sounds like (docs/COMET.md section 7). Everything goes through the game's mixer,
    /// so the player's volume settings, pausing, the helmet and the air around the player apply as
    /// for any game sound. Two routes: through the air (the External group, which the game muffles
    /// with the air around the listener) for the roar, the boom, the gust and the glass; and through
    /// the ground (LocalPlayerInternal, which only the helmet filters) for the thump and the rumble,
    /// so an airless world still gets a thud. The game's own clip sets are played at a point between
    /// the listener and the event, because they fall off with distance and the event is kilometres
    /// away. Main thread only; on a dedicated server the game plays nothing and this costs nothing.
    /// </summary>
    internal sealed class CometSound
    {
        private static readonly int RocketFall = Animator.StringToHash("RocketFall");
        private static readonly int SonicBoom = Animator.StringToHash("ShuttleLargeSonicBoom");
        private static readonly int ExplodeDistant = Animator.StringToHash("RocketExplodeDistant");
        private static readonly int ExplosionLarge = Animator.StringToHash("ExplosionLarge");
        private static readonly int WindHeavy = Animator.StringToHash("WindLocalHeavy");
        private static readonly int AirBurst = Animator.StringToHash("FlareAirBurst");
        private static readonly int Storm = Animator.StringToHash("Storm");
        private static readonly int StormSheltered = Animator.StringToHash("StormSheltered");

        /// <summary>A raw clip the game ships but plays from no clip set of its own: a far rocket landing, 18 s.</summary>
        private const string LandFarClip = "Sfx_RocketLandFar";

        private static AudioClip _landFar;
        private static bool _lookedForLandFar;

        private readonly AudioSource _air;
        private readonly AudioSource _ground;
        private readonly float _loudness;
        private readonly List<PooledAudioSource> _loops = new List<PooledAudioSource>();
        private PooledAudioSource _rush;
        private PooledAudioSource _storm;
        private PooledAudioSource _sheltered;
        private bool _rushStarted;
        private bool _stormStarted;

        /// <summary>Which routes found their mixer group, for the log.</summary>
        public readonly string Report;

        /// <param name="loudness">0 to 1 of the game's volume (CometRules.Loudness).</param>
        public CometSound(GameObject owner, float loudness)
        {
            _loudness = Mathf.Clamp01(loudness);
            AudioMixerGroup external = Group("External", "World", "Master");
            AudioMixerGroup ground = Group("LocalPlayerInternal", "LocalPlayer", "Master");
            _air = Source(owner, external);
            _ground = Source(owner, ground);
            Report = "air " + (external != null ? external.name : "unrouted") + ", ground " + (ground != null ? ground.name : "unrouted");
        }

        private static AudioMixerGroup Group(params string[] names)
        {
            AudioManager audio = Singleton<AudioManager>.Instance;
            if (audio?.MixerGroups == null)
            {
                return null;
            }
            foreach (string name in names)
            {
                if (audio.MixerGroups.TryGetValue(Animator.StringToHash(name), out AudioMixerGroup group) && group != null)
                {
                    return group;
                }
            }
            return null;
        }

        private static AudioSource Source(GameObject owner, AudioMixerGroup group)
        {
            AudioSource source = owner.AddComponent<AudioSource>();
            source.spatialBlend = 0f;
            source.playOnAwake = false;
            source.volume = 1f;
            source.outputAudioMixerGroup = group;
            return source;
        }

        private static PooledAudioSource PlayAt(int clips, Vector3 at, float volume)
        {
            try
            {
                return Singleton<AudioManager>.Instance?.PlayAudioClipsData(clips, at, volume);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TR comet] a game sound would not play: " + e.Message);
                return null;
            }
        }

        private static Vector3 Toward(Vector3 listener, Vector3 source, float metres)
        {
            Vector3 way = source - listener;
            float distance = way.magnitude;
            return distance < 1e-3f ? listener : listener + way / distance * Mathf.Min(distance, metres);
        }

        // ---- approach and entry

        /// <summary>The synthesised low roar under the approach, timed to end at the impact.</summary>
        public void Roar() => _air.PlayOneShot(CometArt.Roar, 0.9f * _loudness);

        /// <summary>The game's falling-rocket air rush, kept between the listener and the comet, rising with its heat.</summary>
        public void Rush(Vector3 listener, Vector3 head, float heat)
        {
            if (!_rushStarted)
            {
                _rushStarted = true;
                _rush = PlayAt(RocketFall, listener, 0.01f);
                if (_rush != null)
                {
                    _loops.Add(_rush);
                }
            }
            // A pooled source that has finished goes back to the pool for another sound to use.
            if (_rush == null || !_rush.IsPlaying)
            {
                _rush = null;
                return;
            }
            _rush.SetPosition(Toward(listener, head, Mathf.Lerp(300f, 40f, heat)));
            _rush.GameAudioSource.SetVolumeMultiplier(RocketFall, Mathf.Clamp01(0.15f + 0.85f * heat) * _loudness);
        }

        public void EndRush()
        {
            if (_rush != null)
            {
                _rush.Stop();
                _loops.Remove(_rush);
                _rush = null;
            }
        }

        /// <summary>The game's large sonic boom as the comet comes through the sky.</summary>
        public void Entry(Vector3 listener, Vector3 head) => PlayAt(SonicBoom, Toward(listener, head, 150f), _loudness);

        // ---- the sound arriving, d / 340 s after the impact

        /// <summary>Through the feet first.</summary>
        public void Thump() => _ground.PlayOneShot(CometArt.Thump, _loudness);

        /// <summary>The crack and boom, through the air.</summary>
        public void Boom() => _air.PlayOneShot(CometArt.Boom, _loudness);

        /// <summary>The game's distant rocket explosion and large explosion, the second close by because it falls silent past 80 m.</summary>
        public void Explosions(Vector3 listener, Vector3 impact)
        {
            PlayAt(ExplodeDistant, Toward(listener, impact, 600f), _loudness);
            PlayAt(ExplosionLarge, Toward(listener, impact, 40f), 0.8f * _loudness);
        }

        /// <summary>The long tail: the far rocket landing through the air and a sub-bass rumble through the ground.</summary>
        public void Tail()
        {
            AudioClip land = LandFar();
            if (land != null)
            {
                _air.PlayOneShot(land, 0.9f * _loudness);
            }
            _ground.PlayOneShot(CometArt.Rumble, 0.8f * _loudness);
        }

        private static AudioClip LandFar()
        {
            if (!_lookedForLandFar)
            {
                _lookedForLandFar = true;
                foreach (AudioClip clip in Resources.FindObjectsOfTypeAll<AudioClip>())
                {
                    if (clip != null && clip.name == LandFarClip)
                    {
                        _landFar = clip;
                        break;
                    }
                }
                Debug.Log("[TR comet] " + LandFarClip + (_landFar != null ? " found" : " is not loaded, so the tail is the rumble alone"));
            }
            return _landFar;
        }

        // ---- the wave arriving

        /// <summary>A heavy gust, louder outdoors.</summary>
        public void Gust(Vector3 listener, bool outdoors, float strength)
        {
            PlayAt(WindHeavy, listener, Mathf.Clamp01(strength) * (outdoors ? 1f : 0.35f));
        }

        /// <summary>A structure breaking or creaking, at the structure, when the wave reaches it.</summary>
        public void Structure(int clips, Vector3 at) => PlayAt(clips, at, 1f);

        // ---- after

        /// <summary>A piece of debris bursting high in the sky.</summary>
        public void Pop(Vector3 at) => PlayAt(AirBurst, at, _loudness);

        /// <summary>The storm loop at <paramref name="level"/>, muffled when the listener is indoors.</summary>
        public void Storming(Vector3 listener, float level, bool indoors)
        {
            if (level <= 0.002f && !_stormStarted)
            {
                return;
            }
            if (!_stormStarted)
            {
                _stormStarted = true;
                _storm = PlayAt(Storm, listener, 0.01f);
                _sheltered = PlayAt(StormSheltered, listener, 0.01f);
                if (_storm != null)
                {
                    _loops.Add(_storm);
                }
                if (_sheltered != null)
                {
                    _loops.Add(_sheltered);
                }
            }
            float v = Mathf.Clamp01(level) * _loudness;
            if (_storm != null && _storm.IsPlaying)
            {
                _storm.GameAudioSource.SetVolumeMultiplier(Storm, indoors ? 0f : v);
            }
            if (_sheltered != null && _sheltered.IsPlaying)
            {
                _sheltered.GameAudioSource.SetVolumeMultiplier(StormSheltered, indoors ? v : 0f);
            }
        }

        public void Stop()
        {
            foreach (PooledAudioSource loop in _loops)
            {
                try
                {
                    if (loop != null && loop.IsPlaying)
                    {
                        loop.Stop();
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[TR comet] a sound would not stop: " + e.Message);
                }
            }
            _loops.Clear();
            _rush = _storm = _sheltered = null;
        }
    }
}
