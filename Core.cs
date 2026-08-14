using AltShift.Exodus.Audio;
using AltShift.Exodus.Config;
using AltShift.LifeCycle;
using AltShift.Productivity;
using FMOD;
using FMOD.Studio;
using FMODUnity;
using HarmonyLib;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

[assembly: MelonInfo(typeof(CustomMusic.Core), "CustomMusic", "0.2.0", "Zachary Aidan Kosma", null)]
[assembly: MelonGame("Alt Shift", "Battlestar Galactica Scattered Hopes")]

namespace CustomMusic
{
    public class Core : MelonMod
    {
        private const string ASSET_ROOT = "CustomMusicAssets";
        private static readonly string[] SupportedExtensions = { "*.wav", "*.mp3", "*.ogg" };

        // This build is music-only: folders are only created (and overrides
        // only checked) for these contexts, even though ContextByGuid still
        // maps all ~150 discovered events. That keeps the full discovery/
        // mapping logic reusable for a future ambient/SFX mod — just add
        // names here (or swap this for a different whitelist) to widen scope.
        // Names must match AudioConfiguration's field names exactly, which
        // is what the "Known contexts" log line prints on first launch.
        internal static readonly HashSet<string> MusicContexts = new HashSet<string>
        {
            "MainMusicTitleScreenEventRef",
            "MainMusicRecap",
            "MainMusicFleetEventRef",
            "MainMusicGameOver",
            "MainMusicCombatListEventRef",
            "MainMusicCombatBossFirstAndSecondEventRef",
            "MainMusicCombatBossFinalEventRef",
            "MainMusicMetaUpgradeEventRef",
            "MainMusicTechnicalInteriorsEventRef",
            "MainMusicVictoryEventRef",
            "MainMusicBarEventRef",
            "MainMusicVisionEventRef",
            // Ambient beds — not music, left out by default. Uncomment to include:
            // "AmbientCic",
            // "AmbientHangar",
            // "AmbientSecurity",
            // "AmbientScienceLab",
        };

        // Populated once from AudioConfiguration's own EventReference fields —
        // no manual list of context names to keep in sync with the game.
        internal static readonly Dictionary<Guid, string> ContextByGuid = new Dictionary<Guid, string>();
        internal static CustomMusicController Controller;

        // Gate sets so repeated identical calls don't spam the log.
        private static readonly HashSet<Guid> loggedUnmappedGuids = new HashSet<Guid>();
        private static readonly HashSet<string> loggedNoOverrideContexts = new HashSet<string>();

        private static HarmonyLib.Harmony harmony;

        // --- Preferences ---
        private static MelonPreferences_Category prefsCategory;
        private static MelonPreferences_Entry<string> playbackOrderEntry;
        private static MelonPreferences_Entry<float> introFadeSecondsEntry;
        private static MelonPreferences_Entry<float> outroFadeSecondsEntry;
        private static MelonPreferences_Entry<float> hastenedFadeSecondsEntry;

        internal static bool IsAlphabeticalOrder =>
            string.Equals(playbackOrderEntry?.Value, "Alphabetical", StringComparison.OrdinalIgnoreCase);

        // How long a fresh incoming track takes to fade up to full volume.
        internal static float IntroFadeSeconds => introFadeSecondsEntry?.Value ?? 5f;

        // How long the outgoing track takes to fade to silence the FIRST time
        // it's superseded by a new context.
        internal static float OutroFadeSeconds => outroFadeSecondsEntry?.Value ?? 5f;

        // If a track is ALREADY fading out and yet another new context arrives
        // before it finishes, its remaining fade is hastened to this (shorter)
        // duration instead of continuing at the normal Outro pace — keeps
        // rapid trigger chains (e.g. flapping between two contexts) from
        // stacking up several slowly-dying tracks at once.
        internal static float HastenedFadeSeconds => hastenedFadeSecondsEntry?.Value ?? 1f;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("CustomMusic mod loaded.");

            SetupPreferences();

            harmony = new HarmonyLib.Harmony("com.zacharyaidankosma.custommusic");

            PatchInstanceMethod(typeof(AudioManager), "StartMainMusicIfNeeded", nameof(AudioPatches.StartMainMusicPostfix));
            PatchInstanceMethod(typeof(AudioManager), "ChangeMusicIfNeeded", nameof(AudioPatches.ChangeMusicPostfix));
            PatchInstanceMethod(typeof(AudioManager), "ChangeAmbientIfNeeded", nameof(AudioPatches.ChangeAmbientPostfix));
            PatchInstanceMethod(typeof(AudioManager), "InitialiseCombatMusicParameters", nameof(AudioPatches.InitCombatParamsPostfix));
            PatchInstanceMethod(typeof(AudioManager), "ChangeGlobalParameter", nameof(AudioPatches.ChangeGlobalParameterFloatPostfix), new[] { typeof(string), typeof(float) });
            PatchInstanceMethod(typeof(AudioManager), "ChangeGlobalParameter", nameof(AudioPatches.ChangeGlobalParameterStringPostfix), new[] { typeof(string), typeof(string) });
            PatchInstanceMethod(typeof(FMODStreamingAssetPlayer), "Play", nameof(AudioPatches.StreamingAssetPlayPostfix));

            var controllerGo = new GameObject("CustomMusicController");
            UnityEngine.Object.DontDestroyOnLoad(controllerGo);
            Controller = controllerGo.AddComponent<CustomMusicController>();
            LoggerInstance.Msg("CustomMusic: controller created.");
        }

        private void SetupPreferences()
        {
            prefsCategory = MelonPreferences.CreateCategory("CustomMusic");
            playbackOrderEntry = prefsCategory.CreateEntry(
                "PlaybackOrder",
                "Shuffle",
                "Playback Order",
                "How to pick among multiple override files in a context folder. Valid values: 'Shuffle' (random, avoids immediate repeats) or 'Alphabetical' (cycles through files in sorted order).");

            if (!string.Equals(playbackOrderEntry.Value, "Shuffle", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(playbackOrderEntry.Value, "Alphabetical", StringComparison.OrdinalIgnoreCase))
            {
                LoggerInstance.Warning($"CustomMusic: PlaybackOrder value '{playbackOrderEntry.Value}' not recognized — falling back to Shuffle. Valid values are 'Shuffle' or 'Alphabetical'.");
            }

            introFadeSecondsEntry = prefsCategory.CreateEntry(
                "IntroFadeSeconds",
                5f,
                "Intro Fade Seconds",
                "How long (in seconds) an incoming track takes to fade up to full volume when a game trigger switches context.");

            outroFadeSecondsEntry = prefsCategory.CreateEntry(
                "OutroFadeSeconds",
                5f,
                "Outro Fade Seconds",
                "How long (in seconds) the outgoing track takes to fade to silence the first time it's superseded. Independent from IntroFadeSeconds — set both the same (e.g. 5, or 6-7 for a slower blend) to match a symmetric crossfade.");

            hastenedFadeSecondsEntry = prefsCategory.CreateEntry(
                "HastenedFadeSeconds",
                1f,
                "Hastened Fade Seconds",
                "If a track is already fading out and another new context arrives before it finishes, its remaining fade is sped up to this shorter duration instead of continuing at OutroFadeSeconds — keeps rapid back-to-back triggers from stacking up multiple slowly-dying tracks.");

            if (introFadeSecondsEntry.Value < 0f || outroFadeSecondsEntry.Value < 0f || hastenedFadeSecondsEntry.Value < 0f)
            {
                LoggerInstance.Warning("CustomMusic: one or more fade-seconds preferences is negative — treated as an instant cut (0) at runtime.");
            }

            LoggerInstance.Msg($"CustomMusic: PlaybackOrder = '{playbackOrderEntry.Value}', " +
                                $"IntroFadeSeconds = {introFadeSecondsEntry.Value}, OutroFadeSeconds = {outroFadeSecondsEntry.Value}, " +
                                $"HastenedFadeSeconds = {hastenedFadeSecondsEntry.Value}.");
        }

        private void PatchInstanceMethod(Type declaringType, string methodName, string postfixMethodName, Type[] paramTypes = null)
        {
            try
            {
                MethodInfo target = paramTypes == null
                    ? AccessTools.Method(declaringType, methodName)
                    : AccessTools.Method(declaringType, methodName, paramTypes);

                if (target == null)
                {
                    LoggerInstance.Error($"CustomMusic: could not resolve {declaringType.Name}.{methodName}" +
                        $"({(paramTypes == null ? "" : string.Join(",", paramTypes.Select(t => t.Name)))}) — skipping.");
                    return;
                }
                MethodInfo postfix = typeof(AudioPatches).GetMethod(postfixMethodName, BindingFlags.Static | BindingFlags.Public);
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                LoggerInstance.Msg($"CustomMusic: patched {declaringType.Name}.{methodName}.");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"CustomMusic: failed to patch {declaringType.Name}.{methodName} — {ex}");
            }
        }

        internal static void EnsureContextMapBuilt()
        {
            if (ContextByGuid.Count > 0) return;

            var config = AltShift.Exodus.Config.AudioConfiguration.Me;
            if (config == null) return;

            foreach (var member in config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Cast<MemberInfo>()
                         .Concat(config.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)))
            {
                object value = member is PropertyInfo p ? p.GetValue(config) : ((FieldInfo)member).GetValue(config);
                if (value is EventReference singleRef)
                {
                    RegisterContext(member.Name, singleRef);
                }
                else if (value is System.Collections.IEnumerable list && !(value is string))
                {
                    foreach (var item in list)
                    {
                        if (item is EventReference listRef)
                            RegisterContext(member.Name, listRef);
                    }
                }
            }

            CreateContextFolders();

            MelonLogger.Msg($"CustomMusic: discovered {ContextByGuid.Values.Distinct().Count()} total audio contexts; " +
                             $"this is a music-only build, so folders were created for {MusicContexts.Count} of them under " +
                             $"'Mods/{ASSET_ROOT}/'. Drop any {string.Join("/", SupportedExtensions)} file(s) into a context's " +
                             $"folder to override it — with PlaybackOrder='{playbackOrderEntry.Value}', multiple files in a " +
                             $"folder will be picked accordingly. For a parameter-driven crossfade instead, name files " +
                             $"stem_0.ogg, stem_1.ogg, stem_2.ogg... (low to high intensity) — stem order always follows " +
                             $"the numeric suffix regardless of PlaybackOrder. " +
                             $"Music contexts: {string.Join(", ", MusicContexts)}");
        }

        // Pre-creates an empty folder for every known context so the user
        // never has to guess a folder name — they can just look at what's
        // on disk under CustomMusicAssets/.
        private static void CreateContextFolders()
        {
            string modDir = Path.GetDirectoryName(typeof(Core).Assembly.Location);
            string root = Path.Combine(modDir, ASSET_ROOT);

            foreach (var contextName in ContextByGuid.Values.Distinct())
            {
                if (!MusicContexts.Contains(contextName)) continue;

                try
                {
                    Directory.CreateDirectory(Path.Combine(root, contextName));
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"CustomMusic: failed to create folder for context '{contextName}' — {ex}");
                }
            }
        }

        private static void RegisterContext(string fieldName, EventReference eventRef)
        {
            if (eventRef.IsNull) return;
            if (!ContextByGuid.ContainsKey(eventRef.Guid))
                ContextByGuid[eventRef.Guid] = fieldName;
        }

        internal static string[] GetCustomFilesForContext(string context)
        {
            string modDir = Path.GetDirectoryName(typeof(Core).Assembly.Location);
            string folder = Path.Combine(modDir, ASSET_ROOT, context);
            if (!Directory.Exists(folder)) return Array.Empty<string>();

            return SupportedExtensions
                .SelectMany(ext => Directory.GetFiles(folder, ext))
                .ToArray();
        }

        internal static bool ShouldLogUnmappedGuid(Guid guid) => loggedUnmappedGuids.Add(guid);
        internal static bool ShouldLogNoOverride(string context) => loggedNoOverrideContexts.Add(context);
    }

    public static class AudioPatches
    {
        private static readonly HashSet<string> loggedRawParamValues = new HashSet<string>();

        public static void StartMainMusicPostfix(AudioManager __instance)
        {
            TryOverride(__instance, ref __instance.mainMusicInstance, AltShift.Exodus.Config.AudioConfiguration.Me.MainMusicTitleScreenEventRef, isAmbient: false, callSite: "StartMainMusicIfNeeded");
        }

        public static void StreamingAssetPlayPostfix(FMODStreamingAssetPlayer __instance)
        {
            MelonLogger.Msg($"CustomMusic: FMODStreamingAssetPlayer.Play() fired on '{__instance.gameObject.name}'.");
        }

        public static void ChangeMusicPostfix(AudioManager __instance, EventReference _newMusic)
        {
            TryOverride(__instance, ref __instance.mainMusicInstance, _newMusic, isAmbient: false, callSite: "ChangeMusicIfNeeded");
        }

        public static void ChangeAmbientPostfix(AudioManager __instance, EventReference _newMusic)
        {
            TryOverride(__instance, ref __instance.mainAmbientInstance, _newMusic, isAmbient: true, callSite: "ChangeAmbientIfNeeded");
        }

        public static void InitCombatParamsPostfix(AudioManager __instance)
        {
            MelonLogger.Msg("CustomMusic: stock combat music parameters initialized (tension/intensity/boss-phase layer) — " +
                             "this only affects the stock event's internal mix; our override (if any) reacts via the " +
                             "ChangeGlobalParameter patches below.");
        }

        public static void ChangeGlobalParameterFloatPostfix(string _parameterName, float _newValue)
        {
            string logKey = $"{_parameterName}:{Mathf.Round(_newValue * 4f) / 4f}";
            if (loggedRawParamValues.Add(logKey) && loggedRawParamValues.Count < 200)
            {
                MelonLogger.Msg($"CustomMusic: observed parameter '{_parameterName}' = {_newValue}");
            }
            Core.Controller.OnParameterChanged(_parameterName, _newValue);
        }

        public static void ChangeGlobalParameterStringPostfix(string _parameterName, string _newValue)
        {
            MelonLogger.Msg($"CustomMusic: observed string parameter '{_parameterName}' = '{_newValue}'");
            Core.Controller.OnParameterChangedDiscrete(_parameterName, _newValue);
        }

        private static void TryOverride(AudioManager audioManager, ref EventInstance stockInstance, EventReference stockRef, bool isAmbient, string callSite)
        {
            // Music-only build: never touch the ambient slot at all — leave it
            // fully vanilla. At least one scene (the Bar) routes the exact same
            // FMOD event GUID through both ChangeMusicIfNeeded AND
            // ChangeAmbientIfNeeded. Overriding both created two independent
            // playing copies of the same replacement track simultaneously, and
            // because ambient context changes are infrequent, the ambient copy
            // could linger for a long time (or indefinitely) with no further
            // call to fade it out — heard as a track that "didn't crossfade."
            if (isAmbient) return;

            Core.EnsureContextMapBuilt();
            string bucketId = isAmbient ? "ambient" : "music";

            if (stockRef.IsNull) return;

            if (!Core.ContextByGuid.TryGetValue(stockRef.Guid, out string context))
            {
                if (Core.ShouldLogUnmappedGuid(stockRef.Guid))
                    MelonLogger.Msg($"CustomMusic [{callSite}]: encountered unmapped music event (guid={stockRef.Guid}).");
                return;
            }

            // Music-only build: silently ignore anything outside the whitelist
            // (SFX/UI/ambient events resolve fine but we never act on them here).
            if (!Core.MusicContexts.Contains(context)) return;

            if (Core.Controller == null)
            {
                MelonLogger.Error($"CustomMusic [{callSite}]: Core.Controller is null — cannot proceed for context '{context}'.");
                return;
            }

            string[] files;
            try { files = Core.GetCustomFilesForContext(context); }
            catch (Exception ex) { MelonLogger.Error($"CustomMusic [{callSite}]: GetCustomFilesForContext threw — {ex}"); return; }

            if (files.Length == 0)
            {
                bool wasOverriding;
                try { wasOverriding = Core.Controller.IsOverriding(bucketId); }
                catch (Exception ex) { MelonLogger.Error($"CustomMusic [{callSite}]: IsOverriding threw — {ex}"); return; }

                try { stockInstance.setVolume(1f); }
                catch (Exception ex) { MelonLogger.Error($"CustomMusic [{callSite}]: stockInstance.setVolume(1f) threw — {ex}"); return; }

                try { Core.Controller.StopIfPlayingBucket(bucketId); }
                catch (Exception ex) { MelonLogger.Error($"CustomMusic [{callSite}]: StopIfPlayingBucket threw — {ex}"); return; }

                if (wasOverriding)
                    MelonLogger.Msg($"CustomMusic [{callSite}]: context '{context}' has no override files — restored stock audio, stopped lane '{bucketId}'.");
                else if (Core.ShouldLogNoOverride(context))
                    MelonLogger.Msg($"CustomMusic [{callSite}]: context '{context}' recognized, no override files — playing stock audio.");
                return;
            }

            MelonLogger.Msg($"CustomMusic [{callSite}]: context '{context}' has {files.Length} override file(s) — attempting mute + lane switch.");

            try { stockInstance.setVolume(0f); }
            catch (Exception ex) { MelonLogger.Error($"CustomMusic [{callSite}]: stockInstance.setVolume(0f) threw — {ex}"); return; }
            MelonLogger.Msg($"CustomMusic [{callSite}]: stock event muted OK.");

            try { Core.Controller.PlayContext(bucketId, context, files); }
            catch (Exception ex) { MelonLogger.Error($"CustomMusic [{callSite}]: PlayContext threw — {ex}"); return; }
            MelonLogger.Msg($"CustomMusic [{callSite}]: PlayContext returned OK.");
        }
    }

    // Owns actual FMOD low-level playback for our custom tracks: either a
    // single looping file per context ("discrete" mode) or multiple
    // simultaneously-playing stems crossfaded by parameter value ("stem" mode).
    public class CustomMusicController : MonoBehaviour
    {
        private class StemChannel
        {
            public Sound sound;
            public FMOD.Channel channel;
            public float currentVolume;
            // Crossfade weight from OnParameterChanged (0..1), independent of
            // the layer's overall in/out fade — the two are multiplied together
            // each frame in Update().
            public float weight;
        }

        // One "voice" — either a single discrete sound or a stem group — tied
        // to a specific context. A lane can hold several layers at once: the
        // current one fading in, and any number of previous ones still fading
        // out. Each layer owns its own FMOD resources for its whole lifetime,
        // so a new layer starting never touches an older layer's channel —
        // that overwrite was the bug causing tracks to stack up and overlap.
        private class Layer
        {
            public string context;
            public float fadeTarget = 1f; // 1 = fading in / held; 0 = fading out, remove when silent

            // Null = use Core.IntroFadeSeconds / Core.OutroFadeSeconds normally.
            // Set when a fade-out gets hastened because yet another new context
            // arrived before this layer finished dying — see FadeOutAllLayers.
            public float? fadeDurationOverride;

            // Discrete mode
            public bool isStem;
            public Sound sound;
            public FMOD.Channel channel;

            // Stem mode
            public List<StemChannel> stems;
        }

        private class LaneState
        {
            public string bucketId;
            public string activeContext; // context we currently intend to be playing (null once stopped)
            public readonly List<Layer> layers = new List<Layer>();
        }

        private readonly Dictionary<string, LaneState> lanes = new Dictionary<string, LaneState>();

        // Playback-order bookkeeping, keyed by context name.
        private readonly Dictionary<string, int> rotationIndexByContext = new Dictionary<string, int>();
        private readonly Dictionary<string, string> lastFileByContext = new Dictionary<string, string>();

        public bool IsOverriding(string bucketId)
        {
            return lanes.TryGetValue(bucketId, out var lane) && lane.activeContext != null;
        }

        public void PlayContext(string bucketId, string context, string[] candidateFiles)
        {
            if (!lanes.TryGetValue(bucketId, out var lane))
            {
                lane = new LaneState { bucketId = bucketId };
                lanes[bucketId] = lane;
            }

            bool alreadyActive = lane.activeContext == context &&
                                  lane.layers.Any(l => l.context == context && l.fadeTarget == 1f);
            if (alreadyActive)
            {
                MelonLogger.Msg($"CustomMusic: lane '{bucketId}' already playing context '{context}' — no restart needed.");
                return;
            }

            // Fade out whatever's currently in the lane (old active layer, plus
            // any earlier layer still finishing its own fade-out) while the new
            // one starts fading in at the same time — a short overlap, not a
            // silent gap. Each layer still owns its own FMOD resources for its
            // whole life, so this never touches the old layer's channel directly.
            FadeOutAllLayers(lane);

            var stemFiles = candidateFiles
                .Where(f => Regex.IsMatch(Path.GetFileNameWithoutExtension(f), @"^stem_\d+$"))
                .OrderBy(ExtractStemIndex)
                .ToArray();

            Layer newLayer = stemFiles.Length >= 2
                ? BuildStemLayer(bucketId, context, stemFiles)
                : BuildDiscreteLayer(bucketId, context, candidateFiles);

            if (newLayer == null) return; // load failure already logged

            lane.layers.Add(newLayer);
            lane.activeContext = context;
        }

        private static int ExtractStemIndex(string path)
        {
            var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"\d+");
            return match.Success ? int.Parse(match.Value) : 0;
        }

        // Picks which file to play for a discrete-mode context, honoring the
        // PlaybackOrder preference. Stem lanes never call this — their order
        // is always the numeric stem_N suffix.
        private string ChooseFile(string context, string[] candidateFiles)
        {
            var sorted = candidateFiles
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (sorted.Length == 1)
                return sorted[0];

            if (Core.IsAlphabeticalOrder)
            {
                int idx = rotationIndexByContext.TryGetValue(context, out var i) ? i % sorted.Length : 0;
                rotationIndexByContext[context] = (idx + 1) % sorted.Length;
                return sorted[idx];
            }
            else
            {
                string previous = lastFileByContext.TryGetValue(context, out var last) ? last : null;
                string chosen;
                int guard = 0;
                do
                {
                    chosen = sorted[UnityEngine.Random.Range(0, sorted.Length)];
                    guard++;
                } while (chosen == previous && guard < 10);

                lastFileByContext[context] = chosen;
                return chosen;
            }
        }

        private Layer BuildDiscreteLayer(string bucketId, string context, string[] candidateFiles)
        {
            string chosenFile = ChooseFile(context, candidateFiles);
            MODE mode = MODE.CREATESTREAM | MODE.LOOP_NORMAL;
            RESULT result = RuntimeManager.CoreSystem.createSound(chosenFile, mode, out Sound newSound);
            if (result != RESULT.OK)
            {
                MelonLogger.Error($"CustomMusic: failed to load '{chosenFile}' for lane '{bucketId}' ({result}).");
                return null;
            }
            newSound.setLoopCount(-1);

            RuntimeManager.CoreSystem.getMasterChannelGroup(out ChannelGroup group);
            RuntimeManager.CoreSystem.playSound(newSound, group, paused: false, out FMOD.Channel newChannel);
            newChannel.setVolume(0f);

            MelonLogger.Msg($"CustomMusic: lane '{bucketId}' now playing '{Path.GetFileName(chosenFile)}' for context '{context}' (fading in, order={(Core.IsAlphabeticalOrder ? "Alphabetical" : "Shuffle")}).");

            return new Layer { context = context, isStem = false, sound = newSound, channel = newChannel, fadeTarget = 1f };
        }

        private Layer BuildStemLayer(string bucketId, string context, string[] stemFiles)
        {
            ChannelGroup group;
            try
            {
                RuntimeManager.CoreSystem.getMasterChannelGroup(out group);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"CustomMusic: RuntimeManager.CoreSystem.getMasterChannelGroup threw — {ex}");
                return null;
            }

            var stems = new List<StemChannel>();
            foreach (var file in stemFiles)
            {
                try
                {
                    MODE mode = MODE.CREATESTREAM | MODE.LOOP_NORMAL;
                    RESULT result = RuntimeManager.CoreSystem.createSound(file, mode, out Sound sound);
                    if (result != RESULT.OK)
                    {
                        MelonLogger.Error($"CustomMusic: createSound('{file}') returned {result}.");
                        continue;
                    }
                    sound.setLoopCount(-1);
                    RuntimeManager.CoreSystem.playSound(sound, group, paused: false, out FMOD.Channel ch);
                    ch.setVolume(0f);
                    stems.Add(new StemChannel { sound = sound, channel = ch, currentVolume = 0f, weight = 0f });
                    MelonLogger.Msg($"CustomMusic: stem loaded OK — '{Path.GetFileName(file)}'.");
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"CustomMusic: exception loading stem '{file}' — {ex}");
                }
            }

            if (stems.Count > 0) stems[0].weight = 1f;

            MelonLogger.Msg($"CustomMusic: lane '{bucketId}' loaded {stems.Count} stem(s) for context '{context}' (fading in).");

            return new Layer { context = context, isStem = true, stems = stems, fadeTarget = 1f };
        }

        // Drives crossfade position from a float parameter. Applies to every
        // live stem layer across all lanes (a layer that's fading out stays
        // silent regardless, since its overall fadeTarget is 0 and multiplies
        // the weight to zero in Update()). Clamped to 0..1 for now — check the
        // log for "observed parameter" lines to see the real authored range
        // and adjust the normalization below if needed.
        public void OnParameterChanged(string parameterName, float value)
        {
            foreach (var lane in lanes.Values)
            {
                foreach (var layer in lane.layers)
                {
                    if (!layer.isStem || layer.stems == null || layer.stems.Count < 2) continue;

                    float t = Mathf.Clamp01(value);
                    float scaled = t * (layer.stems.Count - 1);
                    int lowerIndex = Mathf.FloorToInt(scaled);
                    float frac = scaled - lowerIndex;

                    for (int i = 0; i < layer.stems.Count; i++)
                    {
                        float w = 0f;
                        if (i == lowerIndex) w = 1f - frac;
                        else if (i == lowerIndex + 1) w = frac;
                        layer.stems[i].weight = w;
                    }
                }
            }
        }

        // Hook for string-valued parameters (e.g. boss phase name). Not
        // wired to a concrete behavior yet — extend once real values are
        // visible in the log.
        public void OnParameterChangedDiscrete(string parameterName, string value)
        {
        }

        public void StopIfPlayingBucket(string bucketId)
        {
            if (!lanes.TryGetValue(bucketId, out var lane)) return;
            FadeOutAllLayers(lane);
            lane.activeContext = null;
        }

        private void FadeOutAllLayers(LaneState lane)
        {
            foreach (var layer in lane.layers)
            {
                bool alreadyFadingOut = layer.fadeTarget == 0f;
                if (alreadyFadingOut)
                {
                    // This layer was already on its way out from a previous
                    // supersede, and now yet another new context has arrived —
                    // speed up its remaining fade so it clears quickly rather
                    // than continuing to linger at the normal (often slower)
                    // Outro pace while the newer track is also trying to fade
                    // in. Repeated triggers can only make this faster, never
                    // slower.
                    float hasten = Mathf.Max(0.01f, Core.HastenedFadeSeconds);
                    layer.fadeDurationOverride = layer.fadeDurationOverride.HasValue
                        ? Mathf.Min(layer.fadeDurationOverride.Value, hasten)
                        : hasten;
                }
                else
                {
                    MelonLogger.Msg($"CustomMusic: lane '{lane.bucketId}' fading out context '{layer.context}'.");
                    layer.fadeTarget = 0f;
                    layer.fadeDurationOverride = null; // first time fading out — use the normal Outro pace
                }
            }
        }

        private void Update()
        {
            float userVolume = ScopedSettings<CommonUserSettings>.Values.musicOn
                ? ScopedSettings<CommonUserSettings>.Values.musicsVolume
                : 0f;

            foreach (var lane in lanes.Values)
            {
                if (lane.layers.Count == 0) continue;

                // Snapshot so we can remove finished layers while iterating.
                foreach (var layer in lane.layers.ToArray())
                {
                    bool finished;

                    if (layer.isStem)
                        finished = UpdateStemLayer(layer, userVolume);
                    else
                        finished = UpdateDiscreteLayer(layer, userVolume);

                    if (finished)
                    {
                        lane.layers.Remove(layer);
                        MelonLogger.Msg($"CustomMusic: lane '{lane.bucketId}' finished fade-out and released audio for context '{layer.context}'.");
                    }
                }
            }
        }

        // Which duration governs this layer's current fade: a hastened
        // override (already-superseded fade-out) takes priority; otherwise
        // it's IntroFadeSeconds while fading in (fadeTarget == 1) or
        // OutroFadeSeconds while fading out (fadeTarget == 0).
        private static float ResolveFadeDuration(Layer layer)
        {
            if (layer.fadeDurationOverride.HasValue) return layer.fadeDurationOverride.Value;
            return layer.fadeTarget >= 1f ? Core.IntroFadeSeconds : Core.OutroFadeSeconds;
        }

        // Returns true once this discrete layer has fully faded out and its
        // FMOD resources have been released (i.e. it's safe to drop).
        private bool UpdateDiscreteLayer(Layer layer, float userVolume)
        {
            if (!layer.channel.hasHandle()) return true;

            layer.channel.getVolume(out float current);
            float duration = Mathf.Max(0.01f, ResolveFadeDuration(layer));
            float next = Mathf.MoveTowards(current, layer.fadeTarget, Time.deltaTime / duration);
            layer.channel.setVolume(next * userVolume);

            if (layer.fadeTarget == 0f && next <= 0.001f)
            {
                layer.channel.stop();
                layer.sound.release();
                layer.channel = default;
                return true;
            }
            return false;
        }

        // Returns true once every stem in this layer has faded to silence and
        // been released.
        private bool UpdateStemLayer(Layer layer, float userVolume)
        {
            if (layer.stems == null || layer.stems.Count == 0) return true;

            bool allSilent = true;
            foreach (var stem in layer.stems)
            {
                if (!stem.channel.hasHandle()) continue;
                float target = stem.weight * layer.fadeTarget;
                float duration = Mathf.Max(0.01f, ResolveFadeDuration(layer));
                stem.currentVolume = Mathf.MoveTowards(stem.currentVolume, target, Time.deltaTime / duration);
                stem.channel.setVolume(stem.currentVolume * userVolume);
                if (stem.currentVolume > 0.001f) allSilent = false;
            }

            if (allSilent && layer.fadeTarget == 0f)
            {
                foreach (var stem in layer.stems)
                {
                    if (stem.channel.hasHandle()) stem.channel.stop();
                    stem.sound.release();
                }
                layer.stems.Clear();
                return true;
            }
            return false;
        }
    }
}