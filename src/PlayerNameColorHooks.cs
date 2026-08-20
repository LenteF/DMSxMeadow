using DressMySlugcat;
using MonoMod.RuntimeDetour;
using RainMeadow;
using RainMeadow.UI.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace DMSxMeadow
{
    public static class PlayerNameColorHooks
    {
        public const string SectionName = "Identity";

        private static Hook chatColorHook;
        private static Hook slugcatColorHook;
        private static Hook onlinePlayerDisplayCtorHook;

        private static FieldInfo shortcutHandlerField;
        private static FieldInfo spritesField;

        private static bool InOnlineSession => OnlineManager.lobby != null && OnlineManager.lobby.isAvailable;

        public static void Initialize()
        {
            On.PlayerGraphics.ColoredBodyPartList += ColoredBodyPartList_Hook;
            On.PlayerGraphics.DefaultBodyPartColorHex += DefaultBodyPartColorHex_Hook;
            On.ShortcutGraphics.Draw += ShortcutGraphics_Draw_Hook;

            try
            {
                shortcutHandlerField = typeof(ShortcutGraphics).GetField(
                    "shortcutHandler", BindingFlags.NonPublic | BindingFlags.Instance);
                spritesField = typeof(ShortcutGraphics).GetField(
                    "sprites", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"No se resolvieron los campos privados de ShortcutGraphics: {ex.Message}");
            }

            try
            {
                MethodInfo original = typeof(ChatLogManager).GetMethod(
                    "GetDisplayPlayerColor",
                    BindingFlags.Public | BindingFlags.Static);
                MethodInfo hook = typeof(PlayerNameColorHooks).GetMethod(
                    nameof(ChatLogManager_GetDisplayPlayerColor),
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (original != null && hook != null)
                {
                    chatColorHook = new Hook(original, hook);
                }
                else
                {
                    Plugin.Logger.LogWarning("No se resolvió ChatLogManager.GetDisplayPlayerColor (API de Rain Meadow cambiada): color de nombre en chat desactivado.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"No se pudo enganchar ChatLogManager.GetDisplayPlayerColor: {ex.Message}");
            }

            try
            {
                MethodInfo original = typeof(SlugcatCustomization).GetMethod(
                    "SlugcatColor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo hook = typeof(PlayerNameColorHooks).GetMethod(
                    nameof(SlugcatCustomization_SlugcatColor_Hook),
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (original != null && hook != null)
                {
                    slugcatColorHook = new Hook(original, hook);
                }
                else
                {
                    Plugin.Logger.LogWarning("No se resolvió SlugcatCustomization.SlugcatColor (API de Rain Meadow cambiada): nombre flotante sin color de identidad.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"No se pudo enganchar SlugcatCustomization.SlugcatColor: {ex.Message}");
            }

            try
            {
                ConstructorInfo ctor = typeof(OnlinePlayerDisplay).GetConstructor(
                    new[] { typeof(PlayerSpecificOnlineHud), typeof(SlugcatCustomization), typeof(OnlinePlayer) });
                MethodInfo hook = typeof(PlayerNameColorHooks).GetMethod(
                    nameof(OnlinePlayerDisplay_Ctor_Hook),
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (ctor != null && hook != null)
                {
                    onlinePlayerDisplayCtorHook = new Hook(ctor, hook);
                }
                else
                {
                    Plugin.Logger.LogWarning("No se resolvió OnlinePlayerDisplay.ctor (API de Rain Meadow cambiada): refuerzo del nombre flotante desactivado.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"No se pudo enganchar OnlinePlayerDisplay.ctor: {ex.Message}");
            }

            Plugin.Logger.LogDebug("🎨 Sección 'Identity' activa en sesiones de Meadow (color de nombre en chat + nombre flotante + luz de tubería, sync por currentColors de Meadow).");
        }

        public static void Dispose()
        {
            On.PlayerGraphics.ColoredBodyPartList -= ColoredBodyPartList_Hook;
            On.PlayerGraphics.DefaultBodyPartColorHex -= DefaultBodyPartColorHex_Hook;
            On.ShortcutGraphics.Draw -= ShortcutGraphics_Draw_Hook;
            chatColorHook?.Dispose();
            chatColorHook = null;
            slugcatColorHook?.Dispose();
            slugcatColorHook = null;
            onlinePlayerDisplayCtorHook?.Dispose();
            onlinePlayerDisplayCtorHook = null;
            shortcutHandlerField = null;
            spritesField = null;
        }

        // ===================================================================
        // SELECCIONADOR DE COLORES: SECCIÓN "IDENTITY"
        // ===================================================================

        private static List<string> ColoredBodyPartList_Hook(
            On.PlayerGraphics.orig_ColoredBodyPartList orig,
            SlugcatStats.Name slugcatID)
        {
            var list = orig(slugcatID);
            try
            {
                if (!InOnlineSession) return list;
                if (list == null) list = new List<string>();
                if (!list.Contains(SectionName)) list.Add(SectionName);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"ColoredBodyPartList hook error: {ex.Message}");
            }
            return list;
        }

        private static List<string> DefaultBodyPartColorHex_Hook(
            On.PlayerGraphics.orig_DefaultBodyPartColorHex orig,
            SlugcatStats.Name slugcatID)
        {
            var list = orig(slugcatID);
            try
            {
                if (!InOnlineSession) return list;
                if (list == null) list = new List<string>();
                list.Add(RWCustom.Custom.colorToHex(PlayerGraphics.DefaultSlugcatColor(slugcatID)));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"DefaultBodyPartColorHex hook error: {ex.Message}");
            }
            return list;
        }

        // ===================================================================
        // LECTURA DEL COLOR DE NOMBRE
        // ===================================================================

        public static int GetNameColorIndex(SlugcatStats.Name slugcat)
        {
            if (slugcat == null || string.IsNullOrEmpty(slugcat.value)) return -1;

            int index = -1;
            try
            {
                var parts = PlayerGraphics.ColoredBodyPartList(slugcat);
                for (int i = 0; i < parts.Count; i++)
                {
                    if (parts[i] == SectionName)
                    {
                        index = i;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug($"GetNameColorIndex error: {ex.Message}");
            }

            return index;
        }

        public static bool TryGetNameColor(SlugcatCustomization custom, out Color color)
        {
            color = default;
            if (custom == null) return false;

            var colors = custom.currentColors;
            if (colors == null || colors.Count == 0) return false;

            int index = GetNameColorIndex(custom.playingAs);
            if (index < 0 || index >= colors.Count) return false;

            color = colors[index];
            return true;
        }

        // ===================================================================
        // COLOR DE LA LUZ DE LA TUBERÍA
        // ===================================================================

        private static void ShortcutGraphics_Draw_Hook(
            On.ShortcutGraphics.orig_Draw orig,
            ShortcutGraphics self,
            float timeStacker,
            Vector2 camPos)
        {
            orig(self, timeStacker, camPos);
            try
            {
                if (!InOnlineSession) return;
                if (self == null || self.room == null || shortcutHandlerField == null || spritesField == null) return;

                var handler = shortcutHandlerField.GetValue(self) as ShortcutHandler;
                if (handler == null || handler.transportVessels == null || handler.transportVessels.Count == 0) return;

                var sprites = spritesField.GetValue(self) as Dictionary<int, FSprite>;
                if (sprites == null) return;

                int tileHeight = self.room.TileHeight;
                foreach (var vessel in handler.transportVessels)
                {
                    if (vessel.room != self.room.abstractRoom) continue;
                    if (!(vessel.creature is Player player)) continue;
                    if (!RainMeadow.RainMeadow.creatureCustomizations.TryGetValue(player, out var custom)
                        || !(custom is SlugcatCustomization slugcatCustom)
                        || !TryGetNameColor(slugcatCustom, out var nameColor)) continue;

                    int hash = vessel.pos.x * tileHeight + vessel.pos.y;
                    if (sprites.TryGetValue(hash, out var sprite)) sprite.color = nameColor;

                    if (player.Template.shortcutSegments > 1 && vessel.lastPositions != null)
                    {
                        foreach (var lastPos in vessel.lastPositions)
                        {
                            int lastHash = lastPos.x * tileHeight + lastPos.y;
                            if (sprites.TryGetValue(lastHash, out var lastSprite)) lastSprite.color = nameColor;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"ShortcutGraphics.Draw hook error: {ex.Message}");
            }
        }

        // ===================================================================
        // COLOR DEL NOMBRE EN EL CHAT
        // ===================================================================

        private static Color ChatLogManager_GetDisplayPlayerColor(
            Func<string, Color, Color> orig,
            string playerName,
            Color colorIfNotFound)
        {
            Color color = orig(playerName, colorIfNotFound);
            try
            {
                if (OnlineManager.lobby == null || OnlineManager.lobby.playerAvatars == null) return color;

                foreach (var kv in OnlineManager.lobby.playerAvatars)
                {
                    if (kv.Key?.id?.DisplayName != playerName) continue;

                    if (kv.Value.FindEntity(true) is OnlinePhysicalObject opo
                        && opo.TryGetData<SlugcatCustomization>(out var custom)
                        && TryGetNameColor(custom, out var nameColor))
                    {
                        Color.RGBToHSV(nameColor, out float H, out float S, out float V);
                        return V < 0.8f ? Color.HSVToRGB(H, S, 0.8f) : nameColor;
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"GetDisplayPlayerColor hook error: {ex.Message}");
            }
            return color;
        }

        // ===================================================================
        // COLOR DEL NOMBRE FLOTANTE SOBRE EL SLUGCAT
        // ===================================================================

        private static Color SlugcatCustomization_SlugcatColor_Hook(
            Func<SlugcatCustomization, Color> orig,
            SlugcatCustomization self)
        {
            Color color = orig(self);
            try
            {
                if (InOnlineSession && TryGetNameColor(self, out var nameColor))
                {
                    return nameColor;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"SlugcatColor hook error: {ex.Message}");
            }
            return color;
        }

        // ===================================================================
        // REFUERZO: NOMBRE FLOTANTE SOBRE EL SLUGCAT
        // ===================================================================

        private static bool loggedCtorIdentificationFail;
        private static bool loggedCtorApply;

        private static void OnlinePlayerDisplay_Ctor_Hook(
            Action<OnlinePlayerDisplay, PlayerSpecificOnlineHud, SlugcatCustomization, OnlinePlayer> orig,
            OnlinePlayerDisplay self,
            PlayerSpecificOnlineHud owner,
            SlugcatCustomization customization,
            OnlinePlayer player)
        {
            orig(self, owner, customization, player);
            try
            {
                if (!InOnlineSession) return;
                if (customization == null) return;

                if (!TryGetNameColor(customization, out var nameColor))
                {
                    if (!loggedCtorIdentificationFail)
                    {
                        loggedCtorIdentificationFail = true;
                        Plugin.Logger.LogDebug($"OnlinePlayerDisplay sin color de identidad (playingAs={customization.playingAs?.value ?? "null"}, currentColors={customization.currentColors?.Count ?? 0}) — se mantiene el color del cuerpo.");
                    }
                    return;
                }

                if (RainMeadow.RainMeadow.isArenaMode(out _)) return;

                if (RainMeadow.RainMeadow.isStoryMode(out _)
                    && OnlineManager.lobby?.clientSettings != null
                    && OnlineManager.lobby.clientSettings.TryGetValue(player, out var cs))
                {
                    var chatColorField = cs.GetType().GetField(
                        "chatUsernameColor", BindingFlags.Public | BindingFlags.Instance);
                    if (chatColorField != null && chatColorField.GetValue(cs) != null)
                    {
                        return;
                    }
                }

                self.color = nameColor;
                Color.RGBToHSV(nameColor, out float H, out float S, out float V);
                self.lighter_color = V < 0.8f ? Color.HSVToRGB(H, S, 0.8f) : nameColor;
                self.username.color = self.lighter_color;
                self.playerIcon.icon.color = self.lighter_color;
                ApplyCustomSlugIconColors(self, customization);
                self.pingLabel.color = self.lighter_color;
                self.arrowSprite.color = self.lighter_color;

                if (!loggedCtorApply)
                {
                    loggedCtorApply = true;
                    Plugin.Logger.LogDebug("Color de identidad aplicado al nombre flotante (OnlinePlayerDisplay.ctor).");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"OnlinePlayerDisplay ctor hook error: {ex.Message}");
            }
        }

        private static void ApplyCustomSlugIconColors(OnlinePlayerDisplay self, SlugcatCustomization customization)
        {
            List<Color> baseColors = new List<Color>();
            if (SlugIcon.SlugcatNameToDefaultColors.TryGetValue(self.playerIcon.slugIcon.slugcatName, out var hexList))
            {
                baseColors = hexList.Select(hex => RWCustom.Custom.hexToColor(hex ?? "FFFFFF")).ToList();
            }
            else
            {
                baseColors = new List<Color> { Color.white, RWCustom.Custom.hexToColor("101010"), RWCustom.Custom.hexToColor("E59D52") };
            }

            List<Color> paletteToSend = new List<Color>();

            paletteToSend.Add(self.lighter_color);

            if (customization.currentColors != null && customization.currentColors.Count > 1)
            {
                paletteToSend.Add(customization.currentColors[1]);
            }
            else if (baseColors.Count > 1)
            {
                paletteToSend.Add(baseColors[1]);
            }

            if (customization.currentColors != null && customization.currentColors.Count > 2)
            {
                paletteToSend.Add(customization.currentColors[2]);
            }
            else if (baseColors.Count > 2)
            {
                paletteToSend.Add(baseColors[2]);
            }

            self.playerIcon.slugIcon.ApplyPalette(paletteToSend);
        }
    }
}