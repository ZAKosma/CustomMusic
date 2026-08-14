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

        internal static bool IsAlphabeticalOrder =>
            string.Equals(playbackOrderEntry?.Value, "Alphabetical", StringComparison.OrdinalIgnoreCase);

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

            LoggerInstance.Msg($"CustomMusic: PlaybackOrder = '{playbackOrderEntry.Value}'.");
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

            MelonLogger.Msg($"CustomMusic: discovered {ContextByGuid.Values.Distinct().Count()} music contexts and " +
                             $"created their folders under 'Mods/{ASSET_ROOT}/'. Drop any {string.Join("/", SupportedExtensions)} " +
                             $"file(s) into a context's folder to override it — with PlaybackOrder='{playbackOrderEntry.Value}', " +
                             $"multiple files in a folder will be picked accordingly. For a parameter-driven crossfade instead, " +
                             $"name files stem_0.ogg, stem_1.ogg, stem_2.ogg... (low to high intensity) — stem order always " +
                             $"follows the numeric suffix regardless of PlaybackOrder. " +
                             $"Known contexts: {string.Join(", ", ContextByGuid.Values.Distinct())}");
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
            Core.EnsureContextMapBuilt();
            string bucketId = isAmbient ? "ambient" : "music";

            if (stockRef.IsNull) return;

            if (!Core.ContextByGuid.TryGetValue(stockRef.Guid, out string context))
            {
                if (Core.ShouldLogUnmappedGuid(stockRef.Guid))
                    MelonLogger.Msg($"CustomMusic [{callSite}]: encountered unmapped music event (guid={stockRef.Guid}).");
                return;
            }

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
            public float targetVolume;
        }

        private class Lane
        {
            public string bucketId;
            public string activeContext;

            // Discrete mode
            public Sound sound;
            public FMOD.Channel channel;
            public float fadeTarget = 1f;

            // Stem mode
            public List<StemChannel> stems;
        }

        private readonly Dictionary<string, Lane> lanes = new Dictionary<string, Lane>();
        private const float FadeDuration = 1.0f;

        // Playback-order bookkeeping, keyed by context name.
        private readonly Dictionary<string, int> rotationIndexByContext = new Dictionary<string, int>();
        private readonly Dictionary<string, string> lastFileByContext = new Dictionary<string, string>();

        public bool IsOverriding(string bucketId)
        {
            if (!lanes.TryGetValue(bucketId, out var lane)) return false;
            if (lane.activeContext == null) return false;
            return lane.channel.hasHandle() || (lane.stems != null && lane.stems.Count > 0);
        }

        public void PlayContext(string bucketId, string context, string[] candidateFiles)
        {
            var stemFiles = candidateFiles
                .Where(f => Regex.IsMatch(Path.GetFileNameWithoutExtension(f), @"^stem_\d+$"))
                .OrderBy(ExtractStemIndex)
                .ToArray();

            if (stemFiles.Length >= 2)
                PlayStemLane(bucketId, context, stemFiles);
            else
                PlayDiscreteLane(bucketId, context, candidateFiles);
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

        private void PlayDiscreteLane(string bucketId, string context, string[] candidateFiles)
        {
            if (!lanes.TryGetValue(bucketId, out var lane))
            {
                lane = new Lane { bucketId = bucketId };
                lanes[bucketId] = lane;
            }

            if (lane.activeContext == context && lane.channel.hasHandle())
            {
                MelonLogger.Msg($"CustomMusic: lane '{bucketId}' already playing context '{context}' — no restart needed.");
                return;
            }

            StopLane(lane);

            string chosenFile = ChooseFile(context, candidateFiles);
            MODE mode = MODE.CREATESTREAM | MODE.LOOP_NORMAL;
            RESULT result = RuntimeManager.CoreSystem.createSound(chosenFile, mode, out Sound newSound);
            if (result != RESULT.OK)
            {
                MelonLogger.Error($"CustomMusic: failed to load '{chosenFile}' for lane '{bucketId}' ({result}).");
                return;
            }
            newSound.setLoopCount(-1);

            RuntimeManager.CoreSystem.getMasterChannelGroup(out ChannelGroup group);
            RuntimeManager.CoreSystem.playSound(newSound, group, paused: false, out FMOD.Channel newChannel);
            newChannel.setVolume(0f);

            lane.sound = newSound;
            lane.channel = newChannel;
            lane.activeContext = context;
            lane.fadeTarget = 1f;

            MelonLogger.Msg($"CustomMusic: lane '{bucketId}' now playing '{Path.GetFileName(chosenFile)}' for context '{context}' (fading in, order={(Core.IsAlphabeticalOrder ? "Alphabetical" : "Shuffle")}).");
        }

        private void PlayStemLane(string bucketId, string context, string[] stemFiles)
        {
            if (!lanes.TryGetValue(bucketId, out var lane))
            {
                lane = new Lane { bucketId = bucketId };
                lanes[bucketId] = lane;
            }
            if (lane.activeContext == context && lane.stems != null && lane.stems.Count > 0) return;

            StopLane(lane);
            lane.stems = new List<StemChannel>();

            ChannelGroup group;
            try
            {
                RuntimeManager.CoreSystem.getMasterChannelGroup(out group);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"CustomMusic: RuntimeManager.CoreSystem.getMasterChannelGroup threw — {ex}");
                return;
            }

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
                    lane.stems.Add(new StemChannel { sound = sound, channel = ch, currentVolume = 0f, targetVolume = 0f });
                    MelonLogger.Msg($"CustomMusic: stem loaded OK — '{Path.GetFileName(file)}'.");
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"CustomMusic: exception loading stem '{file}' — {ex}");
                }
            }

            lane.activeContext = context;
            if (lane.stems.Count > 0) lane.stems[0].targetVolume = 1f;

            MelonLogger.Msg($"CustomMusic: lane '{bucketId}' loaded {lane.stems.Count} stem(s) for context '{context}'.");
        }

        // Drives crossfade position from a float parameter. Clamped to 0..1
        // for now — check the log for "observed parameter" lines to see the
        // real authored range and adjust the normalization below if needed.
        public void OnParameterChanged(string parameterName, float value)
        {
            foreach (var lane in lanes.Values)
            {
                if (lane.stems == null || lane.stems.Count < 2) continue;

                float t = Mathf.Clamp01(value);
                float scaled = t * (lane.stems.Count - 1);
                int lowerIndex = Mathf.FloorToInt(scaled);
                float frac = scaled - lowerIndex;

                for (int i = 0; i < lane.stems.Count; i++)
                {
                    float vol = 0f;
                    if (i == lowerIndex) vol = 1f - frac;
                    else if (i == lowerIndex + 1) vol = frac;
                    lane.stems[i].targetVolume = vol;
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
            if (lanes.TryGetValue(bucketId, out var lane))
                StopLane(lane);
        }

        private void StopLane(Lane lane)
        {
            if (lane.activeContext == null) return;

            MelonLogger.Msg($"CustomMusic: lane '{lane.bucketId}' fading out context '{lane.activeContext}'.");

            if (lane.channel.hasHandle())
            {
                lane.fadeTarget = 0f;
            }
            if (lane.stems != null)
            {
                foreach (var stem in lane.stems)
                    stem.targetVolume = 0f;
            }
        }

        private void Update()
        {
            float userVolume = ScopedSettings<CommonUserSettings>.Values.musicOn
                ? ScopedSettings<CommonUserSettings>.Values.musicsVolume
                : 0f;

            foreach (var lane in lanes.Values)
            {
                // Discrete single-track fade
                if (lane.channel.hasHandle())
                {
                    lane.channel.getVolume(out float current);
                    float next = Mathf.MoveTowards(current, lane.fadeTarget, Time.deltaTime / FadeDuration);
                    lane.channel.setVolume(next * userVolume);

                    if (lane.fadeTarget == 0f && next <= 0.001f)
                    {
                        string finishedContext = lane.activeContext;
                        lane.channel.stop();
                        lane.sound.release();
                        lane.channel = default;
                        lane.activeContext = null;
                        MelonLogger.Msg($"CustomMusic: lane '{lane.bucketId}' finished fade-out and released audio for context '{finishedContext}'.");
                    }
                }

                // Stem crossfade
                if (lane.stems != null && lane.stems.Count > 0)
                {
                    bool allSilent = true;
                    foreach (var stem in lane.stems)
                    {
                        if (!stem.channel.hasHandle()) continue;
                        stem.currentVolume = Mathf.MoveTowards(stem.currentVolume, stem.targetVolume, Time.deltaTime / FadeDuration);
                        stem.channel.setVolume(stem.currentVolume * userVolume);
                        if (stem.currentVolume > 0.001f) allSilent = false;
                    }

                    // If every stem has faded to silence (e.g. after StopLane),
                    // release them all and clear the lane's stem state.
                    if (allSilent && lane.stems.All(s => s.targetVolume == 0f))
                    {
                        string finishedContext = lane.activeContext;
                        foreach (var stem in lane.stems)
                        {
                            if (stem.channel.hasHandle()) stem.channel.stop();
                            stem.sound.release();
                        }
                        lane.stems.Clear();
                        lane.activeContext = null;
                        MelonLogger.Msg($"CustomMusic: lane '{lane.bucketId}' finished stem fade-out and released audio for context '{finishedContext}'.");
                    }
                }
            }
        }
    }
}