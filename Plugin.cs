using System;
using System.Net.Http;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using DG.Tweening;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WKMusic;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger = null!;
    internal static Plugin Instance = null!;

    internal volatile PlaybackState State = PlaybackState.Empty;
    internal volatile bool Initialized;
    internal volatile bool Initializing;

    internal volatile byte[]? PendingCoverBytes;

    internal static float PlayerScale = 0.8f;
    internal static float CoverSize = 64f;
    internal static float CoverOpacity = 0.6f;

    private void Awake()
    {
        Instance = this;
        Logger = base.Logger;

        var workerUrl = Config.Bind("Spotify", "WorkerUrl",
            "https://wkmusic.eventeventeventevent1.workers.dev",
            "Cloudflare Worker URL; change only if you host your own").Value;

        PlayerScale = Config.Bind("HUD", "PlayerScale", 0.8f, new BepInEx.Configuration.ConfigDescription(
            "Overall player scale (affects font size and layout dimensions)",
            new BepInEx.Configuration.AcceptableValueRange<float>(0.5f, 3f))).Value;
        CoverSize = Config.Bind("HUD", "CoverSize", 64f, new BepInEx.Configuration.ConfigDescription(
            "Album cover size in pixels",
            new BepInEx.Configuration.AcceptableValueRange<float>(20f, 300f))).Value;
        CoverOpacity = Config.Bind("HUD", "CoverOpacity", 0.6f, new BepInEx.Configuration.ConfigDescription(
            "Album cover opacity (0 = invisible, 1 = fully opaque)",
            new BepInEx.Configuration.AcceptableValueRange<float>(0f, 1f))).Value;

        var credStorage = new CredentialsStorage();
        var saved = credStorage.Load();

        Task.Run(async () =>
        {
            Initializing = true;
            try
            {
                string clientId, clientSecret;

                if (saved is null)
                {
                    Logger.LogInfo("WKMusic: credentials missing, opening browser setup...");
                    var setup = await BrowserSetup.RunAsync(
                        workerUrl,
                        (id, secret, code) =>
                        {
                            var auth = new SpotifyAuth(workerUrl, secret);
                            var redirectUri = $"{workerUrl}/callback";
                            return auth.ExchangeCodeAsync(id, code, redirectUri, default);
                        });

                    if (setup is null)
                    {
                        Logger.LogWarning("WKMusic: setup cancelled.");
                        return;
                    }

                    credStorage.Save(setup.ClientId, setup.ClientSecret);
                    new TokenStorage().Save(setup.Token);

                    clientId = setup.ClientId;
                    clientSecret = setup.ClientSecret;
                }
                else
                {
                    clientId = saved.ClientId;
                    clientSecret = saved.ClientSecret;
                }

                var client = new SpotifyClient(
                    new SpotifyAuth(workerUrl, clientSecret),
                    new TokenStorage(),
                    clientId);

                await client.InitializeAsync();
                Initialized = true;
                Logger.LogInfo("WKMusic: Spotify connected.");
                StartPolling(client);
            }
            catch (Exception ex)
            {
                Logger.LogError($"WKMusic: init failed — {ex.GetType().Name}: {ex.Message}");
                Logger.LogError(ex.StackTrace ?? "");
            }
            finally
            {
                Initializing = false;
            }
        });

        DOTween.Init();
        new Harmony(MyPluginInfo.PLUGIN_GUID).PatchAll();
    }

    private void StartPolling(IMusicClient client)
    {
        var http = new HttpClient();
        string? lastCoverToken = null;

        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    State = await client.GetPlaybackStateAsync();

                    var track = State.Track;
                    var coverToken = track is null
                        ? null
                        : track.CoverBytes != null
                            ? $"embedded:{track.Id}"
                            : track.CoverUrl;

                    if (coverToken != lastCoverToken)
                    {
                        lastCoverToken = coverToken;
                        PendingCoverBytes = track?.CoverBytes != null
                            ? track.CoverBytes
                            : track?.CoverUrl != null
                                ? await http.GetByteArrayAsync(track.CoverUrl)
                                : Array.Empty<byte>();
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"WKMusic: poll error - {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }

                await Task.Delay(300);
            }
        });
    }

    internal static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}

[HarmonyPatch(typeof(CL_UIManager), "Awake")]
internal static class Patch_UIManager_Awake
{
    private const float RightMargin = 10f;
    private const float TextWidth = 320f;
    private const float CoverGap = 10f;

    static void Postfix(CL_UIManager __instance)
    {
        Plugin.Logger.LogInfo("WKMusic: CL_UIManager.Awake fired.");

        var canvas = __instance.canvas;
        var timerRef = __instance.timer;

        if (canvas == null || timerRef == null)
        {
            Plugin.Logger.LogWarning($"WKMusic: canvas={canvas}, timer={timerRef} - aborting.");
            return;
        }

        var s = Plugin.PlayerScale;
        var coverSize = Plugin.CoverSize;
        var coverRight = RightMargin + TextWidth * s + CoverGap;

        var containerGo = new GameObject("WKMusic_HUD");
        containerGo.transform.SetParent(canvas, worldPositionStays: false);
        var containerRect = containerGo.AddComponent<RectTransform>();
        containerRect.anchorMin = containerRect.anchorMax = new Vector2(1f, 1f);
        containerRect.pivot = new Vector2(1f, 1f);
        containerRect.anchoredPosition = Vector2.zero;
        containerRect.sizeDelta = Vector2.zero;
        Patch_UIManager_Update.HudGroup = containerGo.AddComponent<CanvasGroup>();
        var hud = containerGo.transform;

        Patch_UIManager_Update.CoverA = CreateCover(hud, coverSize, coverRight);
        Patch_UIManager_Update.CoverB = CreateCover(hud, coverSize, coverRight);
        Patch_UIManager_Update.RestoreCover();
        Patch_UIManager_Update.TrackLabel = CreateLabel(hud, timerRef, "WKMusic_Track",
            new Vector2(-RightMargin, -10f), s, TextAlignmentOptions.Top, height: 42f * s);
        Patch_UIManager_Update.ProgressLabel = CreateLabel(hud, timerRef, "WKMusic_Progress",
            new Vector2(-RightMargin, -62f * s), s);
        Patch_UIManager_Update.ResetCache();
        if (!Plugin.Instance.Initialized)
            Patch_UIManager_Update.TrackLabel.text = "WKMusic: loading...";

        Plugin.Logger.LogInfo("WKMusic: HUD created.");
    }

    private static RawImage CreateCover(Transform canvas, float size, float coverRight)
    {
        var go = new GameObject("WKMusic_Cover");
        go.transform.SetParent(canvas, worldPositionStays: false);
        go.SetActive(false);

        var img = go.AddComponent<RawImage>();
        var rect = img.rectTransform;
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(-coverRight, -10f);
        rect.sizeDelta = new Vector2(size, size);

        return img;
    }

    private static TMP_Text CreateLabel(Transform canvas, TMP_Text reference, string name, Vector2 offset,
        float scale = 1f, TextAlignmentOptions alignment = TextAlignmentOptions.TopLeft, float height = 30f)
    {
        var go = new GameObject(name);
        go.transform.SetParent(canvas, worldPositionStays: false);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.font = reference.font;
        tmp.fontSize = reference.fontSize * 0.85f * scale;
        tmp.color = reference.color;
        tmp.alignment = alignment;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.richText = true;

        var rect = tmp.rectTransform;
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = offset;
        rect.sizeDelta = new Vector2(TextWidth * scale, height);

        return tmp;
    }
}

[HarmonyPatch(typeof(CL_UIManager), "Update")]
internal static class Patch_UIManager_Update
{
    internal static TMP_Text? TrackLabel;
    internal static TMP_Text? ProgressLabel;
    internal static CanvasGroup? HudGroup;

    internal static RawImage? CoverA;
    internal static RawImage? CoverB;
    private static bool _aIsFront = true;

    private static RawImage? Front => _aIsFront ? CoverA : CoverB;
    private static RawImage? Back => _aIsFront ? CoverB : CoverA;

    private static Texture2D? _coverTexA;
    private static Texture2D? _coverTexB;
    private static byte[]? _lastCoverBytes;

    private static bool _wasPaused;
    private static string? _lastTrackText;
    private static float _trackAlpha = 1f;

    internal static void ResetCache()
    {
        if (TrackLabel != null) TrackLabel.text = "";
        if (ProgressLabel != null) ProgressLabel.text = "";
        _wasPaused = false;
        _lastTrackText = null;
        _trackAlpha = 1f;
        DOTween.Kill("coverA");
        DOTween.Kill("coverB");
        _aIsFront = true;
    }

    internal static void RestoreCover()
    {
        if (_lastCoverBytes == null) return;
        var front = Front;
        if (front == null) return;
        SetLayerTexture(front, ref _aIsFront ? ref _coverTexA : ref _coverTexB, _lastCoverBytes);
        front.color = new Color(1f, 1f, 1f, Plugin.CoverOpacity);
        front.gameObject.SetActive(true);
        var back = Back;
        if (back != null) back.gameObject.SetActive(false);
    }

    private static void SetLayerTexture(RawImage layer, ref Texture2D? texSlot, byte[] bytes)
    {
        if (texSlot != null) UnityEngine.Object.Destroy(texSlot);
        texSlot = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        if (ImageConversion.LoadImage(texSlot, bytes))
        {
            texSlot.wrapMode = TextureWrapMode.Clamp;
            texSlot.Apply();
        }

        layer.texture = texSlot;
    }

    internal static void ApplyCover(byte[] bytes)
    {
        var back = Back;
        if (back == null) return;

        DOTween.Kill("coverA");
        DOTween.Kill("coverB");

        if (_aIsFront)
            SetLayerTexture(back, ref _coverTexB, bytes);
        else
            SetLayerTexture(back, ref _coverTexA, bytes);

        back.color = new Color(1f, 1f, 1f, 0f);
        back.gameObject.SetActive(true);
        back.transform.SetAsLastSibling();

        var front = Front;
        DOVirtual.Float(0f, Plugin.CoverOpacity, 0.6f,
                v =>
                {
                    if (back != null) back.color = new Color(1f, 1f, 1f, v);
                })
            .SetId("coverB");

        if (front != null && front.gameObject.activeSelf)
        {
            var fromAlpha = front.color.a;
            DOVirtual.Float(fromAlpha, 0f, 0.6f,
                    v =>
                    {
                        if (front != null) front.color = new Color(1f, 1f, 1f, v);
                    })
                .SetId("coverA")
                .OnComplete(() =>
                {
                    if (front != null) front.gameObject.SetActive(false);
                });
        }

        _aIsFront = !_aIsFront;
    }

    internal static void HideCover()
    {
        DOTween.Kill("coverA");
        DOTween.Kill("coverB");
        var front = Front;
        if (front == null || !front.gameObject.activeSelf) return;
        var fromAlpha = front.color.a;
        DOVirtual.Float(fromAlpha, 0f, 0.5f,
                v =>
                {
                    if (front != null) front.color = new Color(1f, 1f, 1f, v);
                })
            .SetId("coverA")
            .OnComplete(() =>
            {
                if (front != null) front.gameObject.SetActive(false);
            });
    }

    private static void SetTrackLabel(string newText)
    {
        if (TrackLabel == null) return;
        if (_lastTrackText != newText)
        {
            _lastTrackText = newText;
            TrackLabel.text = newText;
            _trackAlpha = 0f;
        }

        if (_trackAlpha < 1f)
        {
            _trackAlpha = Mathf.Min(1f, _trackAlpha + Time.deltaTime * 2.5f);
            var c = TrackLabel.color;
            TrackLabel.color = new Color(c.r, c.g, c.b, _trackAlpha);
        }
    }

    static void Postfix()
    {
        if (TrackLabel == null || ProgressLabel == null)
            return;

        var p = Plugin.Instance;

        var pending = p.PendingCoverBytes;
        if (pending != null)
        {
            p.PendingCoverBytes = null;
            if (pending.Length == 0)
            {
                _lastCoverBytes = null;
                HideCover();
            }
            else
            {
                _lastCoverBytes = pending;
                ApplyCover(pending);
            }
        }

        if (p.Initializing)
        {
            SetTrackLabel("WKMusic: authorizing...");
            ProgressLabel.text = "";
            return;
        }

        if (!p.Initialized)
        {
            SetTrackLabel("");
            ProgressLabel.text = "";
            return;
        }

        var state = p.State;

        if (HudGroup != null)
        {
            var paused = state.Track != null && !state.IsPlaying;
            if (paused != _wasPaused)
            {
                _wasPaused = paused;
                DOVirtual.Float(HudGroup.alpha, paused ? 0.3f : 1f, 0.5f, v => HudGroup.alpha = v);
                HudGroup.transform.DOScale(paused ? 0.75f : 1f, 0.5f).SetEase(Ease.OutCubic);
            }
        }

        if (state.Track is null)
        {
            SetTrackLabel("Nothing playing");
            ProgressLabel.text = "";
            return;
        }

        var track = state.Track;
        var progress = TimeSpan.FromMilliseconds(state.ProgressMs);
        var duration = TimeSpan.FromMilliseconds(track.DurationMs);
        var ratio = track.DurationMs > 0 ? (double)state.ProgressMs / track.DurationMs : 0;

        const int barWidth = 50;
        var pos = Math.Clamp((int)Math.Round(ratio * barWidth), 0, barWidth);
        var bar = $"{new string('-', pos)}|<color=#303030>{new string('-', barWidth - pos)}</color>";

        SetTrackLabel(
            $"{Plugin.Truncate(track.Title, 40)}\n<line-height=65%><color=#888888><size=80%>{Plugin.Truncate(track.Artist, 40)}</size></color>");

        ProgressLabel.text =
            $"<mspace=0.45em>{progress:m\\:ss}</mspace>  <mspace=0.22em>{bar}</mspace>  <mspace=0.45em>{duration:m\\:ss}</mspace>";
    }
}