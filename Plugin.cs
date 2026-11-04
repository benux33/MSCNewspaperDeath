using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DeathNewspaper
{
    [BepInPlugin("local.deathnewspaper", "Death Newspaper", "1.0.19")]
    public sealed class Plugin : BaseUnityPlugin
    {
        static Plugin instance;
        static Camera photoCamera;
        Vector3 lastPhotoDirection;
        Component trackedBody;
        string deadProfile;
        bool waitingForPhotos, capturing, cleanupStarted;
        bool keepSongOnResults, raidPresentation;
        PhotoDetail photoDetail;
        Harmony harmony;
        ConfigEntry<float> delay, volume, duration;
        ConfigEntry<bool> enabledMod;
        NativeSong music;
        readonly NativeEffect escapeEffect = new NativeEffect();
        byte[] escapeWave;

        Task<byte[]> audioLoad;

        Texture2D paper, wide, close;
        Font font;
        GameObject overlay;
        RectTransform sheet;
        CanvasGroup group;
        Coroutine sequence;
        bool active;
        float started;
        readonly List<EventSystem> blocked = new List<EventSystem>();
        string Asset(string name) => Path.Combine(Path.GetDirectoryName(Info.Location), name);

        void Awake()
        {
            instance = this;
            enabledMod = Config.Bind("General", "Enabled", true, "Show the newspaper on local player death.");
            delay = Config.Bind("General", "DeathDelay", 3f, new ConfigDescription("Seconds after death before the newspaper appears.", new AcceptableValueRange<float>(0.5f, 12f)));
            volume = Config.Bind("General", "SongVolume", 0.75f, new ConfigDescription("Death song volume.", new AcceptableValueRange<float>(0f, 1f)));
            duration = Config.Bind("General", "AnimationSeconds", 2f, new ConfigDescription("Newspaper spin/zoom duration (replaces SpinDuration).", new AcceptableValueRange<float>(0.5f, 15f)));
            var originalTiming = Config.Bind("Migration", "TwoSecondSingleSpinApplied", false, "Tracks the one-time change to the requested two-second animation.");
            if (!originalTiming.Value)
            {
                duration.Value = 2f;
                originalTiming.Value = true;
                Config.Save();
            }
            try
            {
                paper = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!paper.LoadImage(File.ReadAllBytes(Asset("newspaper.png")))) throw new IOException("Invalid newspaper.png");
                paper.filterMode = FilterMode.Bilinear; paper.wrapMode = TextureWrapMode.Clamp;
                font = Font.CreateDynamicFontFromOSFont(new[] { "Times New Roman", "Liberation Serif", "Arial" }, 48);
                harmony = new Harmony("local.deathnewspaper");
                harmony.Patch(AccessTools.Method(typeof(Player), "OnDead", new[] { typeof(EDamageType) }),
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterDeath)));
                var decalCheck = AccessTools.Method(AccessTools.TypeByName("DeferredDecals.DeferredDecalRenderer"), "CanBeRendered");
                if (decalCheck != null)
                    harmony.Patch(decalCheck, postfix: new HarmonyMethod(typeof(Plugin), nameof(AllowPhotoDecals)));
                harmony.Patch(AccessTools.Method(typeof(EFT.UI.GUISounds), "PlayEndGameSound"),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(SkipRaidEndSound)));
                harmony.Patch(AccessTools.Method(typeof(GameWorld), "Dispose"),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeWorldCleanup)));
                harmony.Patch(AccessTools.Method(typeof(EFT.Interactive.Corpse), "Kill"),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeCorpseCleanup)));
                harmony.Patch(AccessTools.Method(typeof(Player), "Dispose"),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePlayerCleanup)));
                var resultsController = AccessTools.TypeByName("EFT.UI.SessionEnd.SessionResultExitStatus+ExitStatusScreenController");
                foreach (string method in new[] { "ShowNextScreen", "GoToMainMenu" })
                    harmony.Patch(AccessTools.Method(resultsController, method),
                        prefix: new HarmonyMethod(typeof(Plugin), nameof(FadeResultsSong)));
                string audioPath = Asset("death-song.wav");
                float songVolume = volume.Value; // Read config on main thread.
                audioLoad = Task.Run(() => WaveData.Read(audioPath).ToWave(1, songVolume));
                try { escapeWave = WaveData.Read(Asset("escsound.wav")).ToWave(0, 1f); }
                catch (Exception e) { Logger.LogWarning("Escape sound could not load: " + e); }
                Logger.LogInfo("Death Newspaper ready.");
            }
            catch (Exception e) { Logger.LogError(e); enabled = false; }
        }

        void FinishAudioLoad()
        {
            if (audioLoad == null || !audioLoad.IsCompleted) return;
            var completed = audioLoad; audioLoad = null;
            try
            {
                music = new NativeSong(completed.GetAwaiter().GetResult());
                Logger.LogInfo("Windows song ready; first 1 second trimmed. Playback is independent of Unity audio.");
                if (overlay != null || keepSongOnResults) PlaySong();
            }
            catch (Exception e) { Logger.LogError("Song load failed: " + e); }
        }

        void PlaySong()
        {
            if (music == null) { Logger.LogWarning("Song still loading; it will start when ready."); return; }
            try
            {
                music.Play();
                Logger.LogInfo("Windows death song started.");
            }
            catch (Exception e) { Logger.LogError("Song playback failed; newspaper remains available: " + e); }
        }
        static void AfterDeath(Player __instance)
        {
            if (instance == null || !instance.enabled || !instance.enabledMod.Value || !__instance.IsYourPlayer || instance.active) return;
            instance.active = true;
            instance.raidPresentation = true;
            instance.keepSongOnResults = false;
            instance.sequence = instance.StartCoroutine(instance.DeathSequence(__instance));
        }

        static void FadeResultsSong()
        {
            if (instance == null || !instance.keepSongOnResults) return;
            instance.keepSongOnResults = false;
            try { instance.music?.FadeOut(2f); }
            catch (Exception e) { instance.Logger.LogWarning("Music fade failed: " + e); instance.music?.Stop(); }
        }

        static void AllowPhotoDecals(Camera __0, ref bool __result)
        {
            if (photoCamera != null && __0 == photoCamera) __result = true;
        }

        static bool SkipRaidEndSound() => instance == null || !instance.enabled || !instance.enabledMod.Value;

        Component ResolveBody(Player player)
        {
            if (player == null) return null;
            var corpse = AccessTools.Field(typeof(Player), "Corpse").GetValue(player) as Component;
            if (corpse != null) return corpse;
            if (player.PlayerBody != null) return player.PlayerBody;
            return player;
        }

        Component FindTrackedBody()
        {
            if (trackedBody != null) return trackedBody;
            foreach (var corpse in FindObjectsOfType<EFT.Interactive.Corpse>())
                if (corpse.PlayerProfileID == deadProfile) { trackedBody = corpse; break; }
            return trackedBody;
        }
        // Observe cleanup only; never suppress, queue or replay the game's disposal.
        static void BeforeWorldCleanup() { instance?.CaptureBeforeCleanup(); }
        static void BeforeCorpseCleanup(EFT.Interactive.Corpse __instance)
        {
            if (instance != null && __instance.PlayerProfileID == instance.deadProfile)
            { instance.trackedBody = __instance; instance.CaptureBeforeCleanup(); }
        }
        static void BeforePlayerCleanup(Player __instance)
        {
            if (instance != null && __instance.ProfileId == instance.deadProfile) instance.CaptureBeforeCleanup();
        }
        void CaptureBeforeCleanup()
        {
            if (!waitingForPhotos || capturing || cleanupStarted) return;
            cleanupStarted = true;
            if (wide != null) { Logger.LogInfo("Raid cleanup started; freezing last live-scene photos."); return; }
            try
            {
                CapturePair(FindTrackedBody());
                Logger.LogInfo("Preserved final available body photos before raid cleanup.");
            }
            catch (Exception e) { Logger.LogWarning("Cleanup capture: " + e); }
        }

        void CapturePair(Component body)
        {
            if (body == null || capturing) return;
            capturing = true;
            Texture2D nextWide = null, nextClose = null;
            try
            {
                using (var visibleBody = new BodyPhotoScope(body))
                {
                    var bounds = visibleBody.Bounds;
                    float distance = Mathf.Max(3f, bounds.extents.magnitude / Mathf.Sin(27.5f * Mathf.Deg2Rad) * 1.15f);
                    nextWide = Photograph(bounds.center, new Vector3(2.5f, 3.8f, -2.5f).normalized * distance, 1360, 750, body.transform);
                    Vector3 closeTarget = bounds.center;
                    // Skeleton helpers can be displaced by ragdoll mods; use rendered geometry.
                    Vector3 wideDirection = lastPhotoDirection;
                    for (int attempt = 0; attempt < 3 && nextClose == null; attempt++)
                    {
                        Vector3 direction = Quaternion.Euler(0, attempt * 120f, 0) * new Vector3(-1.6f, 2.2f, -1.4f).normalized;
                        try { nextClose = Photograph(closeTarget, direction * distance, 722, 558, body.transform, wideDirection, 32f, true); }
                        catch (Exception e) { Logger.LogWarning($"Close-up attempt {attempt + 1} rejected: {e.Message}"); }
                    }
                }
                if (wide != null) Destroy(wide);
                if (nextClose != null) { if (close != null) Destroy(close); close = nextClose; }
                else Logger.LogWarning("No usable new close-up; retaining any previous close-up.");
                wide = nextWide;
                nextWide = nextClose = null;
                Logger.LogInfo("Body photographs captured and retained.");
            }
            catch (Exception e) { Logger.LogWarning("Photo capture failed; keeping any earlier pair: " + e); }
            finally
            {
                capturing = false;
                if (nextWide != null) Destroy(nextWide);
                if (nextClose != null) Destroy(nextClose);
            }
        }

        IEnumerator DeathSequence(Player player)
        {
            float deathTime = Time.realtimeSinceStartup;
            deadProfile = player.ProfileId; waitingForPhotos = true; cleanupStarted = false;
            Component body = null;
            try
            {
                body = ResolveBody(player); trackedBody = body;
                // Retain the body reference now, but do not photograph the standing pose.
                if (body != null) photoDetail = new PhotoDetail(body.transform.position);
            }
            catch (Exception e) { Logger.LogWarning("Initial photo preparation failed: " + e); }
            // Track the retained ragdoll even if the Player is unregistered during death.
            var bones = new List<Transform>();
            if (body != null)
                foreach (var skin in body.GetComponentsInChildren<SkinnedMeshRenderer>())
                    foreach (var bone in skin.bones)
                        if (bone != null && !bones.Contains(bone)) bones.Add(bone);
            var previous = new Vector3[bones.Count];
            for (int i = 0; i < bones.Count; i++) previous[i] = bones[i].position;
            float stableFor = 0;
            float sampledAt = Time.realtimeSinceStartup;
            float lastCaptureAt = deathTime;
            while (!cleanupStarted && Time.realtimeSinceStartup - deathTime < 7f)
            {
                yield return new WaitForSecondsRealtime(0.1f);
                body = FindTrackedBody();
                if (body == null) break;
                float now = Time.realtimeSinceStartup;
                float step = Mathf.Max(0.001f, now - sampledAt);
                float maxSpeed = 0;
                for (int i = 0; i < bones.Count; i++)
                {
                    if (bones[i] == null) continue;
                    Vector3 position = bones[i].position;
                    maxSpeed = Mathf.Max(maxSpeed, Vector3.Distance(previous[i], position) / step);
                    previous[i] = position;
                }
                stableFor = bones.Count > 0 && maxSpeed < 0.18f ? stableFor + step : 0;
                sampledAt = now;
                if (!cleanupStarted && now - deathTime >= 2.5f && now - lastCaptureAt >= 0.75f)
                { CapturePair(body); lastCaptureAt = now; }
                if (now - deathTime >= 2.5f && stableFor >= 0.5f) break;
            }
            try
            {
                // Keep the cleanup/fallback pair if the scene was already removed.
                Logger.LogInfo($"Capturing after {Time.realtimeSinceStartup - deathTime:F2}s; bone stability={stableFor:F2}s.");
                if (!cleanupStarted) CapturePair(FindTrackedBody());
                if (wide == null) Logger.LogWarning("Body disappeared before settled capture.");
            }
            finally { waitingForPhotos = false; photoDetail?.Dispose(); photoDetail = null; }
            while (Time.realtimeSinceStartup - deathTime < delay.Value) yield return null;
            Show();
            sequence = null;
        }
        Texture2D Photograph(Vector3 target, Vector3 offset, int width, int height, Transform bodyRoot, Vector3 avoidDirection = default(Vector3), float fieldOfView = 55f, bool rejectBlank = false)
        {
            var go = new GameObject("Newspaper photo camera");
            var camera = go.AddComponent<Camera>();
            var previous = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D result = null;
            float oldLodBias = QualitySettings.lodBias;
            int oldMaximumLod = QualitySettings.maximumLODLevel;
            try
            {
                if (Camera.main != null) camera.CopyFrom(Camera.main);
                camera.enabled = false;
                camera.renderingPath = RenderingPath.DeferredShading;
                camera.useOcclusionCulling = false;
                camera.layerCullDistances = new float[32];
                QualitySettings.lodBias = 100f;
                QualitySettings.maximumLODLevel = 0;
                camera.stereoTargetEye = StereoTargetEyeMask.None;
                camera.fieldOfView = fieldOfView; camera.nearClipPlane = 0.05f; camera.farClipPlane = 1500f;
                camera.cullingMask = ~(1 << 5); // Exclude UI, include the third-person corpse layers.
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.rect = new Rect(0, 0, 1, 1);
                camera.aspect = (float)width / height;
                // Ignore the body's own colliders; try alternate angles around walls.
                float desired = offset.magnitude, best = 0;
                Vector3 selected = offset.normalized;
                var candidates = new[] { selected, Quaternion.Euler(0, 45, 0) * selected, Quaternion.Euler(0, 90, 0) * selected,
                    Quaternion.Euler(0, 135, 0) * selected, Quaternion.Euler(0, 180, 0) * selected, Quaternion.Euler(0, 225, 0) * selected, Quaternion.Euler(0, 270, 0) * selected, Quaternion.Euler(0, 315, 0) * selected,
                    new Vector3(0.1f, 1f, 0.1f).normalized };
                foreach (var direction in candidates)
                {
                    if (avoidDirection.sqrMagnitude > 0.1f && Vector3.Angle(direction, avoidDirection) < 40f) continue;
                    float free = desired;
                    foreach (var hit in Physics.RaycastAll(target, direction, desired, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (hit.transform == bodyRoot || hit.transform.IsChildOf(bodyRoot)) continue;
                        free = Mathf.Min(free, Mathf.Max(0.4f, hit.distance - 0.2f));
                    }
                    if (free > best) { best = free; selected = direction; }
                    if (free >= desired) break;
                }
                if (best <= 0) throw new InvalidOperationException("No distinct photo angle available.");
                lastPhotoDirection = selected;
                offset = selected * best;
                if (best < desired)
                    camera.fieldOfView = Mathf.Min(95f, 2f * Mathf.Atan(Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad) * desired / Mathf.Max(best, 0.4f)) * Mathf.Rad2Deg);
                camera.transform.position = target + offset;
                camera.transform.LookAt(target);
                // This light exists only for the manual photo render, never a live frame.
                var flash = go.AddComponent<Light>();
                flash.type = LightType.Point; flash.range = 12f; flash.intensity = 2.5f;
                flash.shadows = LightShadows.None; flash.renderMode = LightRenderMode.ForcePixel;
                camera.allowHDR = false;
                rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = rt;
                photoCamera = camera;
                camera.enabled = true;
                camera.Render();
                camera.enabled = false;
                photoCamera = null;
                RenderTexture.active = rt;
                result = new Texture2D(width, height, TextureFormat.RGB24, false);
                result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                var pixels = result.GetPixels32();
                var histogram = new int[256];
                for (int i = 0; i < pixels.Length; i++)
                {
                    byte gray = (byte)((pixels[i].r * 77 + pixels[i].g * 150 + pixels[i].b * 29) >> 8);
                    histogram[gray]++;
                    pixels[i] = new Color32(gray, gray, gray, 255);
                }
                int count = 0, white = 255;
                if (rejectBlank)
                {
                    double sum = 0, squares = 0;
                    for (int level = 0; level < 256; level++)
                    { sum += (double)level * histogram[level]; squares += (double)level * level * histogram[level]; }
                    double mean = sum / pixels.Length;
                    double variance = squares / pixels.Length - mean * mean;
                    if (variance < 16) throw new InvalidDataException("Close-up is nearly uniform; trying another viewpoint.");
                }
                for (int level = 0; level < 256; level++)
                { count += histogram[level]; if (count >= pixels.Length * 0.98f) { white = level; break; } }
                float exposure = Mathf.Clamp(230f / Mathf.Max(white, 1), 1f, 4f);
                var tones = new byte[256];
                for (int level = 0; level < 256; level++)
                    tones[level] = (byte)Mathf.RoundToInt(255f * Mathf.Pow(Mathf.Clamp01(level * exposure / 255f), 0.72f));
                for (int i = 0; i < pixels.Length; i++)
                { byte gray = tones[pixels[i].r]; pixels[i] = new Color32(gray, gray, gray, 255); }
                result.filterMode = FilterMode.Bilinear;
                result.SetPixels32(pixels); result.Apply(false, true);
                return result;
            }
            catch { if (result != null) Destroy(result); throw; }
            finally
            {
                RenderTexture.active = previous;
                photoCamera = null;
                camera.enabled = false;
                QualitySettings.lodBias = oldLodBias;
                QualitySettings.maximumLODLevel = oldMaximumLod;
                camera.targetTexture = null;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                go.SetActive(false); Destroy(go);
            }
        }

        RectTransform Box(string name, Transform parent, float x, float y, float w, float h)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            r.SetParent(parent, false); r.anchorMin = r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1); r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(w, h);
            return r;
        }
        void Fill(Transform parent, float x, float y, float w, float h, Color color)
        { Box("Panel", parent, x, y, w, h).gameObject.AddComponent<Image>().color = color; }
        Text TextAt(string text, float x, float y, float w, float h, int size)
        {
            var t = Box("Headline", sheet, x, y, w, h).gameObject.AddComponent<Text>();
            t.font = font; t.text = text; t.fontSize = size; t.fontStyle = FontStyle.Bold;
            t.color = Color.black; t.alignment = TextAnchor.UpperLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
            t.fontSize = size > 30 ? size - 5 : size;
            t.resizeTextForBestFit = false;
            return t;
        }
        void Photo(Texture2D texture, float x, float y, float w, float h)
        {
            Fill(sheet, x, y, w, h, Color.black);
            if (texture != null)
            { var im = Box("Body photograph", sheet, x + 2, y + 2, w - 4, h - 4).gameObject.AddComponent<RawImage>(); im.texture = texture; }
            else
            { Fill(sheet, x + 2, y + 2, w - 4, h - 4, Color.gray); TextAt("PHOTO UNAVAILABLE", x + 12, y + h / 2 - 20, w - 24, 40, 22); }
        }
        void Show()
        {
            try
            {
                overlay = new GameObject("Death Newspaper", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
                DontDestroyOnLoad(overlay);
                var canvas = overlay.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 32767;
                var scaler = overlay.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080); scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
                group = overlay.GetComponent<CanvasGroup>();
                var bg = Box("Black background", overlay.transform, 0, 0, 0, 0);
                bg.anchorMin = Vector2.zero; bg.anchorMax = Vector2.one; bg.offsetMin = bg.offsetMax = Vector2.zero;
                bg.gameObject.AddComponent<Image>().color = Color.black;
                sheet = Box("Spinning newspaper", overlay.transform, 0, 0, 1072, 1395);
                sheet.anchorMin = sheet.anchorMax = sheet.pivot = new Vector2(0.5f, 0.5f); sheet.anchoredPosition = Vector2.zero;
                var original = sheet.gameObject.AddComponent<RawImage>(); original.texture = paper;
                original.uvRect = new Rect(0, 72f / 1467f, 1, 1395f / 1467f);
                Color stock = new Color(0.94f, 0.94f, 0.93f);
                // Supplied high-resolution artwork; retain its masthead and advertisements.
                Fill(sheet, 346, 217, 696, 127, stock);
                TextAt("Ampumavälikohtaus\nvaatii jälleen nuoren uhrin", 350, 219, 687, 125, 61);
                Photo(wide, 348, 350, 680, 375);
                Photo(close, 348, 732, 361, 279);
                Fill(sheet, 713, 730, 329, 282, stock);
                TextAt("Young male\ndead in a\nshooting", 717, 732, 320, 278, 61);
                started = Time.realtimeSinceStartup;
                sheet.localScale = Vector3.one * (0.035f * 1014f / 1395f);
                sheet.localRotation = Quaternion.Euler(0, 0, -360);
                group.alpha = 0;
                PlaySong();
                Logger.LogInfo("Newspaper shown. Escape dismisses it.");
            }
            catch (Exception e) { Logger.LogError(e); Dismiss(); }
        }

        void Update()
        {
            escapeEffect.Poll();
            FinishAudioLoad();
            if (active && Input.GetKeyDown(KeyCode.Escape))
            {
                try { if (escapeWave != null) escapeEffect.Play(escapeWave); }
                catch (Exception e) { Logger.LogWarning(e); }
                Dismiss(raidPresentation && overlay != null); return;
            }
            if (overlay == null) return;
            var system = EventSystem.current;
            if (system != null && system.enabled) { blocked.Add(system); system.enabled = false; }
            float t = Mathf.Clamp01((Time.realtimeSinceStartup - started) / duration.Value);
            float eased = 1 - Mathf.Pow(1 - t, 3);
            sheet.localScale = Vector3.one * (Mathf.Lerp(0.035f, 1f, eased) * 1014f / 1395f);
            sheet.localRotation = Quaternion.Euler(0, 0, Mathf.Lerp(-360, 0, eased));
            group.alpha = Mathf.Clamp01((Time.realtimeSinceStartup - started) / 0.4f);
        }
        void Dismiss(bool preserveSong = false)
        {
            keepSongOnResults = preserveSong;
            waitingForPhotos = false; trackedBody = null; deadProfile = null;
            if (sequence != null) { StopCoroutine(sequence); sequence = null; }
            photoDetail?.Dispose(); photoDetail = null;
           
            if (!preserveSong && music != null) music.Stop();
            if (overlay != null) { overlay.SetActive(false); Destroy(overlay); }
            overlay = null;
            foreach (var system in blocked) if (system != null) system.enabled = true;
            blocked.Clear();
            if (wide != null) Destroy(wide); if (close != null) Destroy(close);
            wide = close = null; active = false;
        }
        void OnDestroy()
        {
            Dismiss(); harmony?.UnpatchSelf();
            music?.Dispose();
            escapeEffect.Dispose();
            if (paper != null) Destroy(paper); if (font != null) Destroy(font);
            if (instance == this) instance = null;
        }
    }
}







