using DressMySlugcat;
using Menu;
using MonoMod.RuntimeDetour;
using RainMeadow;
using RainMeadow.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace DMSxMeadow
{
    public static class StoryLobbyShareSkinIndicator
    {
        private const float SquareSize = 50f;
        private const float ContentMargin = 5f;
        private const float HeadToFaceOffset = 2f;
private const float HeadSizeMultiplier = 1f;
        private const float SaintDefaultHeadSizeBoostPx = 10f;

        private const float OffIconSizeMultiplier = 0.95f;

        private const float PulseDuration = 0.85f;
        private const float PulseMinAlpha = 0.3f;
        private const float PulseMaxAlpha = 1f;

        private const string GlowAtlasName = "dmsxm_glow_local";
        private const float GlowSortZOffset = -1f;
        private const float GlowLayerBlurRadiusRatio = 0.22f;
        private const int GlowBlurPasses = 2;
        private const byte GlowAlphaThreshold = 96;
        private const byte GlowVisibleThreshold = 16;

        // --- Piezas de cabeza que faltan en HeadA0 ---
        private const float HeadPieceGillSidePx = 5f;
        private const float HeadPieceGillScaleFactor = 0.8f;
        private const float HeadPieceGillAnchorPoint = 0.1f;
        private const float HeadPieceGillFanDeg = 90f;
        private const int HeadPieceGillPerSide = 3;
        private const float HeadPieceScarOffsetRightPx = 3f;
        private const float HeadPieceScarOffsetUpPx = 3f;
        private const string HeadPieceGillElementA = "LizardScaleA3";
        private const string HeadPieceGillElementB = "LizardScaleB3";
        private const string HeadPieceScarElement = "MushroomA";

        private const string ModId = "dmsxmeadow";
        private const string HeadPngName = "head.png";

        private const string OffShareSkinSymbol = "GuidanceSlugcat";

        private static Hook storyMenuCtorHook;
        private static Hook arenaMenuCtorHook;

        public static void Initialize()
        {
            if (storyMenuCtorHook != null) return;
            try
            {
                ConstructorInfo ctor = typeof(StoryOnlineMenu)
                    .GetConstructor(new[] { typeof(ProcessManager) });
                if (ctor == null)
                {
                    Plugin.Logger.LogWarning("No se encontró StoryOnlineMenu(ProcessManager) (API de Rain Meadow cambiada): indicador ShareSkin desactivado.");
                    return;
                }

                MethodInfo hookMethod = typeof(StoryLobbyShareSkinIndicator)
                    .GetMethod(nameof(StoryOnlineMenu_Ctor), BindingFlags.NonPublic | BindingFlags.Static);
                storyMenuCtorHook = new Hook(ctor, hookMethod);
                Plugin.Logger.LogDebug("Indicador ShareSkin del lobby de historia activado (solo se muestra con ShareSkin ON).");

                ConstructorInfo arenaCtor = typeof(ArenaOnlineLobbyMenu)
                    .GetConstructor(new[] { typeof(ProcessManager) });
                if (arenaCtor != null)
                {
                    MethodInfo arenaHookMethod = typeof(StoryLobbyShareSkinIndicator)
                        .GetMethod(nameof(ArenaOnlineLobbyMenu_Ctor), BindingFlags.NonPublic | BindingFlags.Static);
                    arenaMenuCtorHook = new Hook(arenaCtor, arenaHookMethod);
                    Plugin.Logger.LogDebug("Indicador ShareSkin del lobby de arena activado (solo se muestra con ShareSkin ON).");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error inicializando indicador ShareSkin del lobby: {ex}");
            }
        }

        private static void StoryOnlineMenu_Ctor(Action<StoryOnlineMenu, ProcessManager> orig, StoryOnlineMenu self, ProcessManager manager)
        {
            orig(self, manager);
            TryCreateSquare(self, Vector2.zero);
        }

        private static void ArenaOnlineLobbyMenu_Ctor(Action<ArenaOnlineLobbyMenu, ProcessManager> orig, ArenaOnlineLobbyMenu self, ProcessManager manager)
        {
            orig(self, manager);
            TryCreateSquare(self, new Vector2(-558f, -44f));
        }

        private static void TryCreateSquare(Menu.Menu menu, Vector2 positionOffset)
        {
            try
            {
                if (menu?.pages == null || menu.pages.Count == 0) return;

                var (slugcat, _) = Plugin.ResolveLocalSlugcatName();
                if (string.IsNullOrEmpty(slugcat)) return;

                float centerX = menu.manager.rainWorld.options.ScreenSize.x / 2f
                    + (1366f - menu.manager.rainWorld.options.ScreenSize.x) / 2f;
                float centerY = 768f / 2f + 305f;
                float posX = centerX - SquareSize / 2f + positionOffset.x;
                float posY = centerY - SquareSize / 2f + positionOffset.y;
                var pos = new Vector2(posX, posY);

                var square = new ShareSkinSquare(menu, menu.pages[0], pos, slugcat);
                if (!square.HasContent) return;
                menu.pages[0].subObjects.Add(square);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error creando indicador ShareSkin del lobby: {ex}");
            }
        }

        public static void Dispose()
        {
            storyMenuCtorHook?.Dispose();
            storyMenuCtorHook = null;
            arenaMenuCtorHook?.Dispose();
            arenaMenuCtorHook = null;
        }

        private sealed class ShareSkinSquare : SimpleButton, IHaveADescription
        {
            public bool HasContent { get; private set; }

            public string Description => DMSxMeadowOptions.ShareSkinEnabled
                ? menu.Translate("Other players will be able to see your current skin")
                : menu.Translate("ShareSkin is off: other players will not see your skin");

            private readonly List<WrappedSprite> _contentSprites = new List<WrappedSprite>(2);
            private FSprite _headSprite;
            private FSprite _faceSprite;
            private string _currentSlugcat;

            private readonly bool _isArena;

            private bool _showingOffIcon;

            private float _pulseTime;

            private FSprite _glowSprite;

            private FAtlasElement _gillElementA;
            private FAtlasElement _gillElementB;
            private FAtlasElement _scarElement;

            private readonly List<FSprite> _gillSprites = new List<FSprite>();

            private readonly List<FSprite> _gillEffectSprites = new List<FSprite>();

            private readonly List<FSprite> _scarSprites = new List<FSprite>();

            private Color _gillEffectColor = new Color(0.87451f, 0.17647f, 0.91765f);
            private Color _scarColor = new Color(0.27059f, 0.15686f, 0.23529f);

            private static Color32[] _cachedDefaultHeadPixels;
            private static int _cachedDefaultHeadW;
            private static int _cachedDefaultHeadH;
            private static bool _cachedDefaultHeadLoaded;

            private bool _lastShareSkinOn = true;

            private List<Color> _arenaColorsCache;

            private Color _skinHeadColor;
            private bool _skinHeadHasColor;
            private Color _skinFaceColor;
            private bool _skinFaceHasColor;

            private Color _lastHeadColor = new Color(-1f, -1f, -1f, -1f);
            private Color _lastFaceColor = new Color(-1f, -1f, -1f, -1f);
            private Color _lastGlowColor = new Color(-1f, -1f, -1f, -1f);

            private readonly FContainer _contentGroup;

            private float _glowImageLuma = -1f;

            public ShareSkinSquare(Menu.Menu menu, MenuObject owner, Vector2 pos, string slugcat)
                : base(menu, owner, string.Empty, string.Empty, pos, new Vector2(SquareSize, SquareSize))
            {
                HideButtonRect(roundedRect);
                HideButtonRect(selectRect);
                fadeAlpha = 0f;
                _currentSlugcat = slugcat;
                _isArena = menu is ArenaOnlineLobbyMenu;
                _lastShareSkinOn = DMSxMeadowOptions.ShareSkinEnabled;
                _contentGroup = new FContainer();
                Container.AddChild(_contentGroup);
                if (!ResolveAndBuild(slugcat)) return;
                HasContent = true;
            }

            private static void HideButtonRect(RoundedRect rect)
            {
                if (rect?.sprites == null) return;
                foreach (FSprite sprite in rect.sprites)
                {
                    if (sprite != null) sprite.alpha = 0f;
                }
            }

            public override void Update()
            {
                base.Update();

                try
                {
                    bool shareSkinOn = DMSxMeadowOptions.ShareSkinEnabled;
                    if (shareSkinOn != _lastShareSkinOn)
                    {
                        _lastShareSkinOn = shareSkinOn;
                        RebuildContent();
                    }

                    var (slugcat, _) = Plugin.ResolveLocalSlugcatName();
                    if (string.IsNullOrEmpty(slugcat)) return;

                    if (_showingOffIcon)
                    {
                        if (_headSprite != null) _headSprite.alpha = UpdatePulse();
                        return;
                    }

                    float pulse = UpdatePulse();
                    if (_glowSprite != null && Mathf.Abs(_glowSprite.alpha - pulse) > 0.001f)
                    {
                        _glowSprite.alpha = pulse;
                    }

                    if (slugcat != _currentSlugcat)
                    {
                        _currentSlugcat = slugcat;
                        RebuildContent();
                    }

                    if (!HasContent) return;

                    if (_isArena)
                    {
                        _arenaColorsCache = TryGetArenaColors();
                    }

                    if (_headSprite != null)
                    {
                        Color headColor = EffectiveHeadColor();
                        if (headColor != _lastHeadColor)
                        {
                            _lastHeadColor = headColor;
                            _headSprite.color = headColor;
                        }
                    }
                    if (_gillSprites.Count > 0)
                    {
                        Color gillColor = EffectiveHeadColor();
                        foreach (var gill in _gillSprites)
                        {
                            if (gill != null) gill.color = gillColor;
                        }
                    }
                    if (_gillEffectSprites.Count > 0)
                    {
                        Color effectTint = EffectivePartExtraColor(_gillEffectColor);
                        foreach (var gill in _gillEffectSprites)
                        {
                            if (gill != null) gill.color = effectTint;
                        }
                    }
                    if (_scarSprites.Count > 0)
                    {
                        Color scarTint = EffectivePartExtraColor(_scarColor);
                        foreach (var scar in _scarSprites)
                        {
                            if (scar != null) scar.color = scarTint;
                        }
                    }
                    if (_glowSprite != null)
                    {
                        Color glowTint = EffectiveGlowColor(EffectiveHeadColor());
                        if (glowTint != _lastGlowColor)
                        {
                            _lastGlowColor = glowTint;
                            _glowSprite.color = glowTint;
                        }
                    }
                    if (_faceSprite != null)
                    {
                        Color faceColor = EffectiveFaceColor();
                        if (faceColor != _lastFaceColor)
                        {
                            _lastFaceColor = faceColor;
                            _faceSprite.color = faceColor;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"Error en poll del indicador ShareSkin: {ex.Message}");
                }
            }

            private float UpdatePulse()
            {
                _pulseTime += Time.unscaledDeltaTime;
                _pulseTime %= PulseDuration;

                float t = _pulseTime / (PulseDuration / 2f);
                return t <= 1f
                    ? Mathf.Lerp(PulseMaxAlpha, PulseMinAlpha, t)
                    : Mathf.Lerp(PulseMinAlpha, PulseMaxAlpha, t - 1f);
            }

            private Color EffectiveHeadColor()
            {
                if (TryGetLobbyPartColor(0, out Color bodyColor)) return bodyColor;
                if (_skinHeadHasColor) return _skinHeadColor;
                return Utils.DefaultBodyColor(_currentSlugcat);
            }

            private Color EffectiveFaceColor()
            {
                if (TryGetLobbyPartColor(1, out Color eyeColor)) return eyeColor;
                if (_skinFaceHasColor) return _skinFaceColor;
                return Utils.DefaultEyeColor(_currentSlugcat);
            }

            private Color EffectivePartExtraColor(Color defaultColor)
            {
                if (TryGetLobbyPartColor(2, out Color partColor)) return partColor;
                return defaultColor;
            }

            private Color EffectiveGlowColor(Color headColor)
            {
                float colorLuma = 0.299f * headColor.r + 0.587f * headColor.g + 0.114f * headColor.b;
                if (_glowImageLuma >= 0f)
                {
                    colorLuma = Mathf.Clamp01(colorLuma * _glowImageLuma);
                }
                float inv = 1f - colorLuma;
                return new Color(inv, inv, inv, 1f);
            }

            private static float ComputeAverageLuma(Color32[] frame, int frameW, int frameH)
            {
                if (frame == null || frame.Length == 0) return -1f;
                double sum = 0d;
                int count = 0;
                for (int i = 0; i < frame.Length; i++)
                {
                    Color32 p = frame[i];
                    if (p.a < 16) continue;
                    sum += 0.299f * p.r + 0.587f * p.g + 0.114f * p.b;
                    count++;
                }
                if (count == 0) return -1f;
                return Mathf.Clamp01((float)(sum / (count * 255f)));
            }

            private bool TryGetLobbyPartColor(int colorIndex, out Color color)
            {
                if (_isArena)
                {
                    color = default;
                    if (_arenaColorsCache == null || colorIndex < 0 || colorIndex >= _arenaColorsCache.Count) return false;
                    color = _arenaColorsCache[colorIndex];
                    return true;
                }
                return TryGetStoryLobbyPartColor(menu, colorIndex, out color);
            }

            private List<Color> TryGetArenaColors()
            {
                try
                {
                    var progression = menu?.manager?.rainWorld?.progression;
                    if (progression == null || string.IsNullOrEmpty(_currentSlugcat)) return null;
                    var slugcatName = new SlugcatStats.Name(_currentSlugcat);
                    if (!progression.IsCustomColorEnabled(slugcatName)) return null;
                    return progression.GetCustomColors(slugcatName);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            private bool ResolveAndBuild(string slugcat)
            {
                _pulseTime = 0f;

                if (!DMSxMeadowOptions.ShareSkinEnabled)
                {
                    return BuildOffIcon();
                }

                _showingOffIcon = false;
                if (_isArena) _arenaColorsCache = TryGetArenaColors();
                if (!TryResolveElement(slugcat, "HEAD", "HeadA0", out FAtlasElement headElement, out _skinHeadColor, out _skinHeadHasColor, out bool headFromSheet)) return false;
                bool hasFace = TryResolveElement(slugcat, "FACE", "FaceA0", out FAtlasElement faceElement, out _skinFaceColor, out _skinFaceHasColor, out bool faceFromSheet);

                Vector2 headVis = TryGetVisibleBounds(headElement, out Vector2 headBounds, out Vector2 headVisCenterOff)
                    ? headBounds
                    : new Vector2(headElement.sourceRect.width, headElement.sourceRect.height);
                Vector2 faceVis = hasFace && TryGetVisibleBounds(faceElement, out Vector2 faceBounds, out _)
                    ? faceBounds
                    : new Vector2(faceElement.sourceRect.width, faceElement.sourceRect.height);

                float scale = ComputeMountScale(headVis, faceVis, hasFace) * HeadSizeMultiplier;
                if (!headFromSheet && string.Equals(slugcat, "Saint", StringComparison.OrdinalIgnoreCase))
                {
                    scale += SaintDefaultHeadSizeBoostPx / Mathf.Max(headVis.y, 1f);
                }

                float headHalf = headVis.y / 2f;
                float faceHalf = faceVis.y / 2f;
                float faceOffset = hasFace ? HeadToFaceOffset : 0f;
                float groupBottom = Mathf.Max(headHalf, faceOffset + faceHalf);
                float headCenterX = SquareSize / 2f;
                float headCenterY = SquareSize / 2f - ((groupBottom - headHalf) / 2f) * scale;
                Vector2 headBase = new Vector2(headCenterX, headCenterY);
                Vector2 headPos = headBase - headVisCenterOff * scale;
                Vector2 anchorShift = headPos - headBase;

                ResolveHeadPieces(slugcat);

                Vector2 facePos = hasFace
                    ? new Vector2(headCenterX, headCenterY - faceOffset * scale) + anchorShift
                    : headPos;

                BuildLayerGlow(headElement, faceElement, hasFace, scale, headPos, facePos, !headFromSheet);

                _headSprite = AddMountedSprite(headElement, EffectiveHeadColor(), scale, headPos);
                MountHeadPieces(facePos, scale);

                _faceSprite = null;
                if (hasFace)
                {
                    _faceSprite = AddMountedSprite(faceElement, EffectiveFaceColor(), scale, facePos);
                }

                _lastHeadColor = EffectiveHeadColor();
                _lastFaceColor = hasFace ? EffectiveFaceColor() : default;
                return true;
            }

            private bool BuildOffIcon()
            {
                _showingOffIcon = true;
                _headSprite = null;
                _faceSprite = null;

                FAtlasElement iconElement = Futile.atlasManager.GetElementWithName(OffShareSkinSymbol);
                if (iconElement == null)
                {
                    Plugin.Logger.LogWarning($"No se encontró el elemento '{OffShareSkinSymbol}' en el catálogo del juego: indicador ShareSkin OFF sin icono.");
                    return false;
                }

                Vector2 iconVis = TryGetVisibleBounds(iconElement, out Vector2 iconBounds, out _)
                    ? iconBounds
                    : new Vector2(iconElement.sourceRect.width, iconElement.sourceRect.height);
                float scale = ComputeMountScale(iconVis, default, false) * HeadSizeMultiplier * OffIconSizeMultiplier;

                _headSprite = AddMountedSprite(iconElement, Color.white, scale, new Vector2(SquareSize / 2f, SquareSize / 2f));
                _headSprite.alpha = PulseMaxAlpha;
                _faceSprite = null;
                _lastHeadColor = Color.white;
                _lastFaceColor = default;
                return true;
            }

            private FSprite AddMountedSprite(FAtlasElement element, Color color, float scale, Vector2 localPos,
                float sortZ = 0f, float alpha = 1f)
            {
                var sprite = new FSprite("pixel")
                {
                    element = element,
                    color = color,
                    scaleX = scale,
                    scaleY = scale,
                    sortZ = sortZ,
                    alpha = alpha
                };
                var wrapped = new WrappedSprite(menu, this, sprite, localPos, _contentGroup);
                _contentSprites.Add(wrapped);
                subObjects.Add(wrapped);
                return sprite;
            }

            private void BuildLayerGlow(FAtlasElement headElement, FAtlasElement faceElement, bool hasFace,
                float scale, Vector2 headPos, Vector2 facePos, bool useDefaultSprite)
            {
                _glowImageLuma = -1f;
                try
                {
                    if (scale <= 0f || headElement == null || headElement.atlas?.texture == null) return;

                    float cHeadX = headPos.x / scale;
                    float cHeadY = headPos.y / scale;
                    float cFaceX = facePos.x / scale;
                    float cFaceY = facePos.y / scale;

                    Color32[] headArt = null; int aw = 0, ah = 0;
                    Color32[] headRawFrame = null; int hrw = 0, hrh = 0;
                    Color32[] faceArt = null; int faw = 0, fah = 0;
                    Color32[] faceRawFrame = null; int frw = 0, frh = 0;
                    Color32[] gillAArt = null; int gaw = 0, gah = 0;
                    Color32[] gillARawFrame = null; int gargw = 0, gargh = 0;
                    Color32[] gillBArt = null; int gbw = 0, gbh = 0;
                    Color32[] gillBRawFrame = null; int gbrw = 0, gbrh = 0;
                    Color32[] scarArt = null; int sw = 0, sh = 0;
                    Color32[] scarRawFrame = null; int srw = 0, srh = 0;
                    bool headOk = TryResolveHeadArt(headElement, useDefaultSprite, out headArt, out aw, out ah,
                            out headRawFrame, out hrw, out hrh)
                        && aw > 0 && ah > 0;
                    bool faceOk = hasFace && faceElement != null
                        && TryReadFramePixels(faceElement, out faceRawFrame, out frw, out frh)
                        && frw > 0 && frh > 0
                        && TryCropAlphaBBox(faceRawFrame, frw, frh, GlowVisibleThreshold, out faceArt, out faw, out fah)
                        && faw > 0 && fah > 0;
                    bool gillAOk = _gillElementA != null
                        && TryReadFramePixels(_gillElementA, out gillARawFrame, out gargw, out gargh)
                        && gargw > 0 && gargh > 0
                        && TryCropAlphaBBox(gillARawFrame, gargw, gargh, GlowVisibleThreshold, out gillAArt, out gaw, out gah)
                        && gaw > 0 && gah > 0;
                    bool gillBOk = _gillElementB != null
                        && TryReadFramePixels(_gillElementB, out gillBRawFrame, out gbrw, out gbrh)
                        && gbrw > 0 && gbrh > 0
                        && TryCropAlphaBBox(gillBRawFrame, gbrw, gbrh, GlowVisibleThreshold, out gillBArt, out gbw, out gbh)
                        && gbw > 0 && gbh > 0;
                    bool scarOk = _scarElement != null
                        && TryReadFramePixels(_scarElement, out scarRawFrame, out srw, out srh)
                        && srw > 0 && srh > 0
                        && TryCropAlphaBBox(scarRawFrame, srw, srh, GlowVisibleThreshold, out scarArt, out sw, out sh);

                    if (!headOk && !faceOk && !gillAOk && !gillBOk && !scarOk) return;

                    float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
                    float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
                    Action<float, float, float, float> addBlock = (cx, cy, hw, hh) =>
                    {
                        minX = Mathf.Min(minX, cx - hw); maxX = Mathf.Max(maxX, cx + hw);
                        minY = Mathf.Min(minY, cy - hh); maxY = Mathf.Max(maxY, cy + hh);
                    };
                    if (headOk) addBlock(cHeadX, cHeadY, aw * 0.5f, ah * 0.5f);
                    else addBlock(cHeadX, cHeadY, headElement.sourcePixelSize.x * 0.5f, headElement.sourcePixelSize.y * 0.5f);
                    if (faceOk) addBlock(cFaceX, cFaceY, faw * 0.5f, fah * 0.5f);
                    if (scarOk) addBlock(cFaceX + HeadPieceScarOffsetRightPx, cFaceY + HeadPieceScarOffsetUpPx, sw * 0.5f, sh * 0.5f);
                    if (gillAOk || gillBOk)
                    {
                        float gillHalf = 0.6f * Mathf.Max(Math.Max(gaw, gah), Math.Max(gbw, gbh)) * HeadPieceGillScaleFactor;
                        addBlock(cFaceX - HeadPieceGillSidePx, cFaceY, gillHalf, gillHalf);
                        addBlock(cFaceX + HeadPieceGillSidePx, cFaceY, gillHalf, gillHalf);
                    }
                    if (float.IsInfinity(minX)) return;

                    int blurRadius = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(1f, Mathf.Max(aw, ah)) * GlowLayerBlurRadiusRatio), 3, 10);
                    int margin = 3 * blurRadius + 4;
                    int minIx = Mathf.FloorToInt(minX) - margin;
                    int maxIx = Mathf.CeilToInt(maxX) + margin;
                    int minIy = Mathf.FloorToInt(minY) - margin;
                    int maxIy = Mathf.CeilToInt(maxY) + margin;
                    int cw = maxIx - minIx + 1;
                    int ch = maxIy - minIy + 1;
                    if (cw <= 1 || ch <= 1) return;
                    Color32[] canvas = new Color32[cw * ch];

                    if (headOk)
                    {
                        if (headRawFrame != null && hrw > 0 && hrh > 0)
                        {
                            StampHeadArt(canvas, cw, ch, headRawFrame, hrw, hrh, cHeadX - minIx, cHeadY - minIy, hrw, hrh);
                        }
                        else
                        {
                            StampHeadArt(canvas, cw, ch, headArt, aw, ah, cHeadX - minIx, cHeadY - minIy, aw, ah);
                        }
                    }
                    else
                    {
                        int hsx = Mathf.RoundToInt(headElement.sourcePixelSize.x);
                        int hsy = Mathf.RoundToInt(headElement.sourcePixelSize.y);
                        for (int yy = 0; yy < hsy; yy++)
                        {
                            for (int xx = 0; xx < hsx; xx++)
                            {
                                int dx = Mathf.RoundToInt(cHeadX - minIx - hsx * 0.5f + xx);
                                int dy = Mathf.RoundToInt(cHeadY - minIy - hsy * 0.5f + yy);
                                if (dx >= 0 && dy >= 0 && dx < cw && dy < ch)
                                {
                                    canvas[dy * cw + dx] = new Color32(255, 255, 255, 255);
                                }
                            }
                        }
                    }
                    if (faceOk) StampHeadArt(canvas, cw, ch, faceRawFrame, frw, frh, cFaceX - minIx, cFaceY - minIy, frw, frh);
                    if (scarOk) StampHeadArt(canvas, cw, ch, scarRawFrame, srw, srh,
                        cFaceX + HeadPieceScarOffsetRightPx - minIx, cFaceY + HeadPieceScarOffsetUpPx - minIy, srw, srh);
                    if (gillAOk) StampGillFan(canvas, cw, ch, gillARawFrame, gargw, gargh, cFaceX - minIx, cFaceY - minIy, HeadPieceGillScaleFactor);
                    if (gillBOk) StampGillFan(canvas, cw, ch, gillBRawFrame, gbrw, gbrh, cFaceX - minIx, cFaceY - minIy, HeadPieceGillScaleFactor);

                    _glowImageLuma = ComputeAverageLuma(canvas, cw, ch);

                    for (int i = 0; i < canvas.Length; i++)
                    {
                        canvas[i].r = 255;
                        canvas[i].g = 255;
                        canvas[i].b = 255;
                    }
                    SmoothAlpha(canvas, cw, ch, blurRadius, GlowBlurPasses);

                    Texture2D texture = new Texture2D(cw, ch, TextureFormat.RGBA32, false);
                    texture.wrapMode = TextureWrapMode.Repeat;
                    texture.anisoLevel = 0;
                    texture.filterMode = FilterMode.Bilinear;
                    texture.SetPixels32(canvas);
                    texture.Apply(false);

                    FAtlasElement glowElement = RegisterMemoryAtlasElement(GlowAtlasName, texture, headElement.isTrimmed);
                    if (glowElement == null) return;

                    Vector2 originWorld = new Vector2(minIx * scale, minIy * scale);
                    _glowSprite = AddMountedSprite(glowElement, EffectiveGlowColor(EffectiveHeadColor()), scale, originWorld, GlowSortZOffset, PulseMaxAlpha);
                    _glowSprite.anchorX = 0f;
                    _glowSprite.anchorY = 0f;
                    _glowSprite.alpha = PulseMaxAlpha;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogDebug($"No se pudo construir la capa del glow: {ex.Message}");
                }
            }

            private static void StampGillFan(Color32[] canvas, int cw, int ch, Color32[] art, int artw, int arth,
                float faceCx, float faceCy, float factor)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    for (int i = 0; i < HeadPieceGillPerSide; i++)
                    {
                        float rowAngle = i * (HeadPieceGillFanDeg / HeadPieceGillPerSide) - HeadPieceGillFanDeg / 2f;
                        float rot = side < 0 ? rowAngle - 90f : -rowAngle + 90f;
                        StampRotatedArt(canvas, cw, ch, art, artw, arth,
                            faceCx + side * HeadPieceGillSidePx, faceCy, rot, side < 0, factor, HeadPieceGillAnchorPoint);
                    }
                }
            }

            private static void StampRotatedArt(Color32[] canvas, int cw, int ch, Color32[] art, int artw, int arth,
                float pivotX, float pivotY, float rotDeg, bool mirrorX, float factor, float anchorY)
            {
                float rad = rotDeg * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad);
                float sin = Mathf.Sin(rad);
                for (int j = 0; j < arth; j++)
                {
                    for (int i = 0; i < artw; i++)
                    {
                        Color32 p = art[j * artw + i];
                        if (p.a < GlowVisibleThreshold) continue;
                        float lx = i + 0.5f - artw * 0.5f;
                        float ly = j + 0.5f - anchorY * arth;
                        if (mirrorX) lx = -lx;
                        float sx = lx * factor;
                        float sy = ly * factor;
                        float wx = sx * cos + sy * sin;
                        float wy = -sx * sin + sy * cos;
                        int dx = Mathf.RoundToInt(pivotX + wx);
                        int dy = Mathf.RoundToInt(pivotY + wy);
                        if (dx < 0 || dy < 0 || dx >= cw || dy >= ch) continue;
                        byte alpha = p.a >= GlowAlphaThreshold ? (byte)255 : Math.Max(p.a, GlowAlphaThreshold);
                        Color32 v = new Color32(255, 255, 255, alpha);
                        if (alpha >= GlowAlphaThreshold || canvas[dy * cw + dx].a == 0)
                        {
                            canvas[dy * cw + dx] = v;
                        }
                    }
                }
            }

            private void ResolveHeadPieces(string slugcat)
            {
                _gillElementA = null;
                _gillElementB = null;
                _scarElement = null;
                try
                {
                    if (string.Equals(slugcat, "Rivulet", StringComparison.OrdinalIgnoreCase))
                    {
                        _gillElementA = TryResolvePartElement(slugcat, "GILLS", HeadPieceGillElementA);
                        _gillElementB = TryResolvePartElement(slugcat, "GILLS", HeadPieceGillElementB);
                        _gillEffectColor = ResolvePartExtraColor(slugcat, "GILLS", new Color(0.87451f, 0.17647f, 0.91765f));
                    }
                    else if (string.Equals(slugcat, "Artificer", StringComparison.OrdinalIgnoreCase))
                    {
                        _scarElement = TryResolvePartElement(slugcat, "FACESCAR", HeadPieceScarElement);
                        _scarColor = ResolvePartExtraColor(slugcat, "FACESCAR", new Color(0.27059f, 0.15686f, 0.23529f));
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogDebug($"No se pudieron resolver las piezas de cabeza de '{slugcat}': {ex.Message}");
                }
            }

            private static FAtlasElement TryResolvePartElement(string slugcat, string partName, string elementName)
            {
                try
                {
                    var customization = MeadowProfileManager.GetCustomizationBySteamID(SkinSerializer.GetLocalSteamId(), slugcat);
                    if (customization == null)
                    {
                        customization = Customization.For(slugcat);
                    }
                    var customSprite = customization?.CustomSprite(partName);
                    var sheet = customSprite?.SpriteSheet;
                    if (sheet != null && sheet.Elements.TryGetValue(elementName, out FAtlasElement sheetElement))
                    {
                        return sheetElement;
                    }
                    return Futile.atlasManager.GetElementWithName(elementName);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            private static bool TryGetPartColor(string slugcat, string partName, out Color color)
            {
                color = default;
                try
                {
                    var customization = MeadowProfileManager.GetCustomizationBySteamID(SkinSerializer.GetLocalSteamId(), slugcat);
                    if (customization == null)
                    {
                        customization = Customization.For(slugcat);
                    }
                    var customSprite = customization?.CustomSprite(partName);
                    if (customSprite != null && customSprite.Color != default && customSprite.Color.a != 0f)
                    {
                        color = customSprite.Color;
                        return true;
                    }
                }
                catch (Exception) { }
                return false;
            }

            private static Color ResolvePartExtraColor(string slugcat, string partName, Color vanillaFallback)
            {
                if (TryGetPartColor(slugcat, partName, out Color skinColor)) return skinColor;
                try
                {
                    Color extra = DressMySlugcat.Utils.DefaultExtraColor(slugcat);
                    if (extra != default && extra.a != 0f) return extra;
                }
                catch (Exception) { }
                return vanillaFallback;
            }

            private void MountHeadPieces(Vector2 faceCenter, float scale)
            {
                _gillSprites.Clear();
                _gillEffectSprites.Clear();
                _scarSprites.Clear();
                if (_gillElementA != null)
                {
                    Color gillTint = EffectiveHeadColor();
                    for (int side = -1; side <= 1; side += 2)
                    {
                        for (int i = 0; i < HeadPieceGillPerSide; i++)
                        {
                            float rowAngle = i * (HeadPieceGillFanDeg / HeadPieceGillPerSide) - HeadPieceGillFanDeg / 2f;
                            float rot = side < 0 ? rowAngle - 90f : -rowAngle + 90f;
                            float gx = faceCenter.x + side * HeadPieceGillSidePx * scale;
                            float gy = faceCenter.y;
                            FSprite gill = AddMountedSprite(_gillElementA, gillTint, 1f, new Vector2(gx, gy));
                            ApplyGillScale(gill, scale);
                            gill.anchorY = HeadPieceGillAnchorPoint;
                            gill.rotation = rot;
                            if (side < 0) gill.scaleX *= -1f;
                            _gillSprites.Add(gill);
                        }
                    }
                }
                if (_gillElementB != null)
                {
                    Color effectTint = EffectivePartExtraColor(_gillEffectColor);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        for (int i = 0; i < HeadPieceGillPerSide; i++)
                        {
                            float rowAngle = i * (HeadPieceGillFanDeg / HeadPieceGillPerSide) - HeadPieceGillFanDeg / 2f;
                            float rot = side < 0 ? rowAngle - 90f : -rowAngle + 90f;
                            float gx = faceCenter.x + side * HeadPieceGillSidePx * scale;
                            float gy = faceCenter.y;
                            FSprite gill = AddMountedSprite(_gillElementB, effectTint, 1f, new Vector2(gx, gy));
                            ApplyGillScale(gill, scale);
                            gill.anchorY = HeadPieceGillAnchorPoint;
                            gill.rotation = rot;
                            if (side < 0) gill.scaleX *= -1f;
                            _gillEffectSprites.Add(gill);
                        }
                    }
                }
                if (_scarElement != null)
                {
                    float sx = faceCenter.x + HeadPieceScarOffsetRightPx * scale;
                    float sy = faceCenter.y + HeadPieceScarOffsetUpPx * scale;
                    FSprite scar = AddMountedSprite(_scarElement, EffectivePartExtraColor(_scarColor), scale, new Vector2(sx, sy));
                    _scarSprites.Add(scar);
                }
            }

            private static void ApplyGillScale(FSprite gill, float scale)
            {
                gill.scaleX = gill.scaleY = scale * HeadPieceGillScaleFactor;
            }

            private static bool TryResolveHeadArt(FAtlasElement headElement, bool useDefaultSprite, out Color32[] art, out int w, out int h,
                out Color32[] rawFrame, out int rawW, out int rawH)
            {
                art = null;
                w = h = 0;
                rawFrame = null;
                rawW = rawH = 0;
                if (!useDefaultSprite)
                {
                    if (TryReadFramePixels(headElement, out Color32[] px, out int pw, out int ph)
                        && pw > 0 && ph > 0
                        && pw <= headElement.sourcePixelSize.x * 2f + 1f
                        && ph <= headElement.sourcePixelSize.y * 2f + 1f)
                    {
                        rawFrame = px;
                        rawW = pw;
                        rawH = ph;
                        return TryCropAlphaBBox(px, pw, ph, GlowVisibleThreshold, out art, out w, out h);
                    }
                }
                if (!TryLoadDefaultHeadPixels(out Color32[] png, out int pngW, out int pngH))
                {
                    return false;
                }
                return TryCropAlphaBBox(png, pngW, pngH, GlowVisibleThreshold, out art, out w, out h);
            }

            private static bool TryCropAlphaBBox(Color32[] src, int srcW, int srcH, byte alphaThreshold, out Color32[] crop, out int w, out int h)
            {
                crop = null;
                w = h = 0;
                if (src == null || srcW <= 0 || srcH <= 0) return false;
                int x0 = srcW, y0 = srcH, x1 = -1, y1 = -1;
                for (int y = 0; y < srcH; y++)
                {
                    for (int x = 0; x < srcW; x++)
                    {
                        if (src[y * srcW + x].a >= alphaThreshold)
                        {
                            x0 = Mathf.Min(x0, x); y0 = Mathf.Min(y0, y);
                            x1 = Mathf.Max(x1, x); y1 = Mathf.Max(y1, y);
                        }
                    }
                }
                if (x1 < x0 || y1 < y0) return false;
                w = x1 - x0 + 1;
                h = y1 - y0 + 1;
                crop = new Color32[w * h];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        crop[y * w + x] = src[(y0 + y) * srcW + x0 + x];
                    }
                }
                return true;
            }

            private static void StampHeadArt(Color32[] frame, int frameW, int frameH, Color32[] art, int artW, int artH,
                float centerX, float centerY, float targetW, float targetH)
            {
                if (artW <= 0 || artH <= 0 || targetW <= 0f || targetH <= 0f) return;
                for (int ty = 0; ty < (int)Mathf.Ceil(targetH); ty++)
                {
                    for (int tx = 0; tx < (int)Mathf.Ceil(targetW); tx++)
                    {
                        int sx = Mathf.Clamp(Mathf.FloorToInt((tx - targetW * 0.5f) / targetW * artW + artW * 0.5f), 0, artW - 1);
                        int sy = Mathf.Clamp(Mathf.FloorToInt((ty - targetH * 0.5f) / targetH * artH + artH * 0.5f), 0, artH - 1);
                        int dx = Mathf.RoundToInt(centerX - targetW * 0.5f + tx);
                        int dy = Mathf.RoundToInt(centerY - targetH * 0.5f + ty);
                        if (dx >= 0 && dy >= 0 && dx < frameW && dy < frameH)
                        {
                            Color32 p = art[sy * artW + sx];
                            if (p.a >= GlowVisibleThreshold)
                            {
                                p.a = Math.Max(p.a, GlowAlphaThreshold);
                                frame[dy * frameW + dx] = p;
                            }
                        }
                    }
                }
            }

            private static FAtlasElement RegisterMemoryAtlasElement(string name, Texture2D texture, bool isTrimmed)
            {
                if (Futile.atlasManager.DoesContainAtlas(name))
                {
                    Futile.atlasManager.UnloadAtlas(name);
                }
                FAtlas atlas = new FAtlas(name, texture, SkinRegistration.NextMemoryAtlasIndex(), false);
                atlas.elements.Clear();
                var element = new FAtlasElement
                {
                    name = name,
                    indexInAtlas = 0,
                    atlas = atlas,
                    atlasIndex = atlas.index,
                    isTrimmed = isTrimmed,
                    uvRect = new Rect(0f, 0f, 1f, 1f),
                    sourceSize = new Vector2(texture.width, texture.height),
                    sourcePixelSize = new Vector2(texture.width, texture.height),
                    sourceRect = new Rect(0f, 0f, texture.width, texture.height)
                };
                element.uvTopLeft.Set(0f, 1f);
                element.uvTopRight.Set(1f, 1f);
                element.uvBottomRight.Set(1f, 0f);
                element.uvBottomLeft.Set(0f, 0f);
                atlas.elements.Add(element);
                SkinRegistration.AddAtlasToManager(atlas);
                return element;
            }

            private static bool TryReadFramePixels(FAtlasElement element, out Color32[] frame, out int frameW, out int frameH)
            {
                frame = null;
                frameW = 0;
                frameH = 0;
                try
                {
                    Texture2D texture = element.atlas.texture as Texture2D;
                    if (texture == null) return false;
                    float texW = texture.width;
                    float texH = texture.height;
                    Rect uv = element.uvRect;
                    if (texW <= 0f || texH <= 0f || uv.width <= 0f || uv.height <= 0f) return false;

                    int x0 = Math.Max(0, Math.Min(texture.width - 1, Mathf.FloorToInt(uv.xMin * texW)));
                    int y0 = Math.Max(0, Math.Min(texture.height - 1, Mathf.FloorToInt(uv.yMin * texH)));
                    frameW = Math.Max(1, Math.Min(texture.width - x0, Mathf.CeilToInt(uv.width * texW)));
                    frameH = Math.Max(1, Math.Min(texture.height - y0, Mathf.CeilToInt(uv.height * texH)));

                    if (!TryGetPixelsRegion(texture, x0, y0, frameW, frameH, out Color[] region))
                    {
                        Texture2D copy = new Texture2D(texture.width, texture.height, texture.format, false);
                        try
                        {
                            Graphics.ConvertTexture(texture, copy);
                            if (!TryGetPixelsRegion(copy, x0, y0, frameW, frameH, out region))
                            {
                                return false;
                            }
                        }
                        finally
                        {
                            UnityEngine.Object.Destroy(copy);
                        }
                    }
                    if (region == null || region.Length != frameW * frameH) return false;

                    frame = new Color32[frameW * frameH];
                    for (int i = 0; i < region.Length; i++)
                    {
                        frame[i] = new Color32(
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(region[i].r) * 255f),
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(region[i].g) * 255f),
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(region[i].b) * 255f),
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(region[i].a) * 255f));
                    }
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            private static bool TryGetPixelsRegion(Texture2D texture, int x0, int y0, int w, int h, out Color[] region)
            {
                try
                {
                    region = texture.GetPixels(x0, y0, w, h, 0);
                    return region != null;
                }
                catch (Exception)
                {
                    region = null;
                    return false;
                }
            }

            private static bool TryLoadDefaultHeadPixels(out Color32[] pixels, out int width, out int height)
            {
                pixels = null;
                width = 0;
                height = 0;
                if (_cachedDefaultHeadLoaded)
                {
                    pixels = _cachedDefaultHeadPixels;
                    width = _cachedDefaultHeadW;
                    height = _cachedDefaultHeadH;
                    return pixels != null;
                }
                try
                {
                    string modPath = null;
                    foreach (var mod in ModManager.ActiveMods)
                    {
                        if (mod.id == ModId) { modPath = mod.path; break; }
                    }
                    if (string.IsNullOrEmpty(modPath))
                    {
                        foreach (var mod in ModManager.InstalledMods)
                        {
                            if (mod.id == ModId) { modPath = mod.path; break; }
                        }
                    }
                    if (string.IsNullOrEmpty(modPath)) return false;

                    string basePath = Path.Combine(modPath, "ui");
                    string pngPath = Path.Combine(basePath, HeadPngName);
                    string txtPath = Path.Combine(basePath, "head.txt");
                    if (!File.Exists(pngPath) || !File.Exists(txtPath))
                    {
                        Plugin.Logger.LogDebug($"No se encontró 'ui\\{HeadPngName}' o 'ui\\head.txt': glow de skin default desactivado.");
                        return false;
                    }

                    Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!texture.LoadImage(File.ReadAllBytes(pngPath)))
                    {
                        Plugin.Logger.LogDebug($"No se pudo decodificar '{pngPath}': glow de skin default desactivado.");
                        return false;
                    }

                    int texH = texture.height;

                    if (!TryParseTexturePackerFrame(txtPath, "HeadA0.png", out int rx, out int ry, out int rw, out int rh)
                        || rw <= 0 || rh <= 0)
                    {
                        Plugin.Logger.LogDebug($"No se pudo parsear el rect de HeadA0 en ui\\head.txt: glow de skin default desactivado.");
                        UnityEngine.Object.Destroy(texture);
                        return false;
                    }

                    rx = Math.Max(0, Math.Min(texture.width - 1, rx));
                    int clampedW = Math.Max(1, Math.Min(texture.width - rx, rw));
                    int topY = texH - ry;
                    int yUnity = Math.Max(0, Math.Min(texture.height - 1, topY - rh));
                    int clampedH = Math.Max(1, Math.Min(texture.height - yUnity, rh));

                    int texW = texture.width;
                    int texH2 = texture.height;
                    Color32[] all = texture.GetPixels32(0);
                    UnityEngine.Object.Destroy(texture);
                    if (all == null) return false;

                    _cachedDefaultHeadPixels = new Color32[clampedW * clampedH];
                    for (int y = 0; y < clampedH; y++)
                    {
                        int srcRow2 = yUnity + y;
                        System.Array.Copy(all, srcRow2 * texW + rx, _cachedDefaultHeadPixels, y * clampedW, clampedW);
                    }
                    _cachedDefaultHeadW = clampedW;
                    _cachedDefaultHeadH = clampedH;
                    _cachedDefaultHeadLoaded = true;

                    pixels = _cachedDefaultHeadPixels;
                    width = _cachedDefaultHeadW;
                    height = _cachedDefaultHeadH;
                    return true;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogDebug($"No se pudo cargar el head default '{HeadPngName}': {ex.Message}");
                    return false;
                }
            }

            private static bool TryParseTexturePackerFrame(string txtPath, string key, out int rx, out int ry, out int rw, out int rh)
            {
                rx = ry = rw = rh = 0;
                try
                {
                    string json = File.ReadAllText(txtPath);
                    int idx = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                    if (idx < 0) return false;
                    string tail = json.Substring(idx);
                    var m = System.Text.RegularExpressions.Regex.Match(
                        tail,
                        "\"frame\"\\s*:\\s*\\{\\s*\"x\"\\s*:\\s*(-?\\d+)\\s*,\\s*\"y\"\\s*:\\s*(-?\\d+)\\s*,\\s*\"w\"\\s*:\\s*(-?\\d+)\\s*,\\s*\"h\"\\s*:\\s*(-?\\d+)\\s*\\}");
                    if (!m.Success) return false;
                    rx = int.Parse(m.Groups[1].Value);
                    ry = int.Parse(m.Groups[2].Value);
                    rw = int.Parse(m.Groups[3].Value);
                    rh = int.Parse(m.Groups[4].Value);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            private static void SmoothAlpha(Color32[] px, int w, int h, int radius, int passes)
            {
                Color32[] work = new Color32[px.Length];
                for (int pass = 0; pass < passes; pass++)
                {
                    for (int y = 0; y < h; y++)
                    {
                        int row = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int sum = 0;
                            int count_ = 0;
                            int fromX = Math.Max(0, x - radius);
                            int toX = Math.Min(w - 1, x + radius);
                            for (int k = fromX; k <= toX; k++)
                            {
                                sum += px[row + k].a;
                                count_++;
                            }
                            work[row + x].a = (byte)(sum / count_);
                        }
                    }
                    for (int x = 0; x < w; x++)
                    {
                        for (int y = 0; y < h; y++)
                        {
                            int sum = 0;
                            int count_ = 0;
                            int fromY = Math.Max(0, y - radius);
                            int toY = Math.Min(h - 1, y + radius);
                            for (int k = fromY; k <= toY; k++)
                            {
                                sum += work[k * w + x].a;
                                count_++;
                            }
                            px[y * w + x].a = (byte)(sum / count_);
                        }
                    }
                }
            }

            private void RebuildContent()
            {
                _headSprite = null;
                _faceSprite = null;
                _glowSprite = null;

                foreach (var sprite in _contentSprites)
                {
                    sprite.RemoveSprites();
                    subObjects.Remove(sprite);
                }
                _contentSprites.Clear();

                if (!ResolveAndBuild(_currentSlugcat))
                {
                    HasContent = false;
                    return;
                }
                HasContent = true;
            }

            private static float ComputeMountScale(Vector2 headVis, Vector2 faceVis, bool hasFace)
            {
                float headHalf = headVis.y / 2f;
                float faceHalf = hasFace ? faceVis.y / 2f : 0f;
                float faceOffset = hasFace ? HeadToFaceOffset : 0f;
                float groupBottomFromHeadCenter = Mathf.Max(headHalf, faceOffset + faceHalf);
                float groupHeight = headHalf + groupBottomFromHeadCenter;
                float groupHalfWidth = Mathf.Max(headVis.x / 2f, hasFace ? faceVis.x / 2f : 0f);

                float target = SquareSize - 2f * ContentMargin;
                float scaleH = target / Mathf.Max(groupHeight, 1f);
                float scaleW = target / Mathf.Max(groupHalfWidth * 2f, 1f);
                return Mathf.Clamp(Mathf.Min(scaleH, scaleW), 0.05f, 100f);
            }

            private static string GetSpecificPartElementName(string slugcat, string genericElementName)
            {
                try
                {
                    foreach (var def in SpriteDefinitions.AvailableSprites)
                    {
                        if (def.RequiredSprites == null || !def.RequiredSprites.Contains(genericElementName)) continue;
                        var rep = def.SlugcatSpecificReplacements?.FirstOrDefault(r =>
                            r.GenericName == genericElementName &&
                            string.Equals(r.Slugcat, slugcat, StringComparison.OrdinalIgnoreCase));
                        if (rep != null && !string.IsNullOrEmpty(rep.SpecificName))
                        {
                            return rep.SpecificName;
                        }
                    }
                }
                catch (Exception) { }
                return genericElementName;
            }

            private static bool TryResolveElement(string slugcat, string partName, string elementName,
                out FAtlasElement element, out Color skinColor, out bool skinHasColor, out bool fromSkinSheet)
            {
                element = null;
                skinColor = default;
                skinHasColor = false;
                fromSkinSheet = false;

                try
                {
                    var customization = MeadowProfileManager.GetCustomizationBySteamID(SkinSerializer.GetLocalSteamId(), slugcat);
                    if (customization == null)
                    {
                        customization = Customization.For(slugcat);
                        if (customization == null) return false;
                    }

                    var customSprite = customization.CustomSprite(partName);
                    var sheet = customSprite?.SpriteSheet;

                    if (sheet != null
                        && !string.Equals(sheet.ID, SpriteSheet.DefaultName, StringComparison.Ordinal)
                        && sheet.Elements != null
                        && sheet.Elements.TryGetValue(elementName, out element))
                    {
                        fromSkinSheet = true;
                    }
                    else
                    {
                        element = Futile.atlasManager?.GetElementWithName(GetSpecificPartElementName(slugcat, elementName));
                    }

                    if (customSprite != null && customSprite.Color != default && customSprite.Color.a != 0)
                    {
                        skinHasColor = true;
                        skinColor = customSprite.Color;
                    }

                    return element != null;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogDebug($"No se pudo resolver el elemento '{elementName}' de '{slugcat}' para el indicador ShareSkin: {ex.Message}");
                    return false;
                }
            }

            private static bool TryGetStoryLobbyPartColor(Menu.Menu menu, int colorIndex, out Color color)
            {
                color = default;
                try
                {
                    if (menu == null) return false;

                    FieldInfo colorInterfaceField = menu.GetType().GetField("colorInterface", BindingFlags.Public | BindingFlags.Instance);
                    if (colorInterfaceField == null) return false;
                    object colorInterface = colorInterfaceField.GetValue(menu);
                    if (colorInterface == null) return false;

                    FieldInfo bodyColorsField = colorInterface.GetType().GetField("bodyColors", BindingFlags.Public | BindingFlags.Instance);
                    if (bodyColorsField == null) return false;
                    object bodyColors = bodyColorsField.GetValue(colorInterface);

                    object slot = null;
                    if (bodyColors is Array array && colorIndex >= 0 && colorIndex < array.Length)
                    {
                        slot = array.GetValue(colorIndex);
                    }
                    else if (bodyColors is IList list && colorIndex >= 0 && colorIndex < list.Count)
                    {
                        slot = list[colorIndex];
                    }
                    else if (bodyColors is IDictionary dict && dict.Contains(colorIndex))
                    {
                        slot = dict[colorIndex];
                    }
                    if (slot == null) return false;

                    PropertyInfo colorProp = slot.GetType().GetProperty("color", BindingFlags.Public | BindingFlags.Instance);
                    if (colorProp?.GetValue(slot, null) is Color c)
                    {
                        color = c;
                        return true;
                    }

                    FieldInfo colorField = slot.GetType().GetField("color", BindingFlags.Public | BindingFlags.Instance);
                    if (colorField?.GetValue(slot) is Color cf)
                    {
                        color = cf;
                        return true;
                    }

                    return false;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            private static bool TryGetVisibleBounds(FAtlasElement element, out Vector2 visible, out Vector2 centerOffset)
            {
                visible = default;
                centerOffset = default;
                try
                {
                    if (element?.atlas?.texture == null) return false;

                    Texture2D texture = element.atlas.texture as Texture2D;
                    if (texture == null) return false;
                    float texW = texture.width;
                    float texH = texture.height;
                    Rect uv = element.uvRect;

                    if (texW <= 0f || texH <= 0f || uv.width <= 0f || uv.height <= 0f) return false;

                    int x0 = Math.Max(0, Math.Min(texture.width - 1, Mathf.FloorToInt(uv.xMin * texW)));
                    int y0 = Math.Max(0, Math.Min(texture.height - 1, Mathf.FloorToInt(uv.yMin * texH)));
                    int w = Math.Max(1, Math.Min(texture.width - x0, Mathf.CeilToInt(uv.width * texW)));
                    int h = Math.Max(1, Math.Min(texture.height - y0, Mathf.CeilToInt(uv.height * texH)));

                    Color32[] pixels = texture.GetPixels32();
                    if (pixels == null || pixels.Length < texture.width * texture.height) return false;

                    const byte AlphaThreshold = 16;
                    int firstRow = -1, lastRow = -1, firstCol = -1, lastCol = -1;

                    for (int row = 0; row < h; row++)
                    {
                        int baseIndex = (y0 + row) * texture.width + x0;
                        for (int col = 0; col < w; col++)
                        {
                            if (pixels[baseIndex + col].a > AlphaThreshold)
                            {
                                if (firstRow < 0) firstRow = row;
                                lastRow = row;
                                if (firstCol < 0 || col < firstCol) firstCol = col;
                                if (lastCol < 0 || col > lastCol) lastCol = col;
                            }
                        }
                    }

                    if (firstRow < 0) return false;

                    float visibleRows = lastRow - firstRow + 1;
                    float visibleCols = lastCol - firstCol + 1;
                    visible = new Vector2(
                        element.sourceRect.width * (visibleCols / w),
                        element.sourceRect.height * (visibleRows / h));

                    float centerCol = (firstCol + lastCol) * 0.5f;
                    float centerRow = (firstRow + lastRow) * 0.5f;
                    centerOffset = new Vector2(
                        element.sourceRect.width * ((centerCol + 0.5f - w * 0.5f) / w),
                        element.sourceRect.height * ((centerRow + 0.5f - h * 0.5f) / h));
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            private sealed class WrappedSprite : PositionedMenuObject
            {
                private readonly FSprite _sprite;

                public WrappedSprite(Menu.Menu menu, MenuObject owner, FSprite sprite, Vector2 pos, FContainer container = null) : base(menu, owner, pos)
                {
                    _sprite = sprite;
                    (container ?? Container).AddChild(sprite);
                }

                public override void GrafUpdate(float timeStacker)
                {
                    base.GrafUpdate(timeStacker);
                    _sprite.SetPosition(DrawPos(timeStacker));
                }

                public override void RemoveSprites()
                {
                    base.RemoveSprites();
                    _sprite.RemoveFromContainer();
                }
            }
        }
    }
}