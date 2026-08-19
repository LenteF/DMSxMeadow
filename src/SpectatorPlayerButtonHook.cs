using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;
using RainMeadow;
using UnityEngine;

namespace DMSxMeadow
{
    public static class SpectatorPlayerButtonHook
    {
        private const string BanSymbol = "Kill_Slugcat";
        private const string BannedSymbol = "FriendA";
        private const string BanSignal = "DMSXMEADOW_BAN_SKIN";

        private static Hook spectatorUpdateHook;

        private static readonly ConditionalWeakTable<SpectatorOverlay.PlayerButton, object>
            PlaceholdersByButton = new ConditionalWeakTable<SpectatorOverlay.PlayerButton, object>();

        public static void Initialize()
        {
            if (spectatorUpdateHook != null) return;

            MethodInfo updateMethod = typeof(SpectatorOverlay)
                .GetMethod("Update", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (updateMethod == null)
            {
                Plugin.Logger.LogWarning("[DMSxMeadow] No se encontró SpectatorOverlay.Update (API de Rain Meadow cambiada): botón placeholder desactivado.");
                return;
            }

            MethodInfo hookMethod = typeof(SpectatorPlayerButtonHook)
                .GetMethod(nameof(SpectatorOverlay_Update), BindingFlags.NonPublic | BindingFlags.Static);

            spectatorUpdateHook = new Hook(updateMethod, hookMethod);
            Plugin.Logger.LogDebug("[DMSxMeadow] Hook del overlay de espectador activado (botón placeholder por jugador tras el 'x').");
        }

        private static void SpectatorOverlay_Update(Action<SpectatorOverlay> orig, SpectatorOverlay self)
        {
            orig(self);

            try
            {
                if (self == null || self.PlayerButtons == null) return;

                foreach (SpectatorOverlay.PlayerButton playerButton in self.PlayerButtons)
                {
                    if (playerButton == null) continue;

                    if (playerButton.player == null || playerButton.player.isMe) continue;

                    if (PlaceholdersByButton.TryGetValue(playerButton, out _)) continue;

                    string steamId = DMSNetworkTester.SkinSerializer.GetPlayerSteamId(playerButton.player);
                    string displayName = playerButton.player.id?.DisplayName ?? "";
                    bool banned = SkinBanManager.IsBanned(steamId);

                    var banSkinButton = new SimplerSymbolButton(
                        self,
                        playerButton,
                        banned ? BannedSymbol : BanSymbol,
                        BanSignal,
                        new Vector2(playerButton.size.x + 10f + 24f + 4f, 0f),
                        Describe(steamId, banned, displayName));

                    banSkinButton.OnClick += (_) =>
                    {
                        bool nowBanned = SkinBanManager.ToggleBan(steamId, displayName);
                        if (nowBanned)
                        {
                            // Do NOT purge the received customization: the banned slug renders with a
                            // default skin but keeps its tail geometry. Changing tail size mid-game makes
                            // the tail sprite stick and stretch abnormally (reported bug).
                            SkinTransfer.ForgetPlayer(playerButton.player);
                            SkinRegistration.ForgetSender(steamId);
                            Plugin.ScheduleRecreateForSteamId(steamId);
                            Plugin.Logger.LogInfo($"[DMSxMeadow] 🚫 Skin de '{displayName}' ({steamId}) BANEADA localmente (permanente en blacklist.txt). Su skin ya no se descarga ni se aplica (la cola se conserva).");
                        }
                        else
                        {
                            Plugin.Logger.LogInfo($"[DMSxMeadow] ✅ Skin de '{displayName}' ({steamId}) desbaneada. Solicitando re-handshake...");
                            DMSNetworkTester.SkinSerializer.RequestHandshakeFrom(steamId);
                        }

                        banSkinButton.UpdateSymbol(nowBanned ? BannedSymbol : BanSymbol);
                        banSkinButton.description = Describe(steamId, nowBanned, displayName);
                        self.infolabelDirty = true;
                        self.PlaySound(nowBanned ? SoundID.MENU_Remove_Level : SoundID.MENU_Checkbox_Uncheck);
                    };

                    playerButton.subObjects.Add(banSkinButton);
                    PlaceholdersByButton.Add(playerButton, banSkinButton);
                    Plugin.Logger.LogDebug($"[DMSxMeadow] Botón de ban de skin añadido a la fila de {playerButton.player.id} ({(banned ? "baneado" : "activo")}).");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DMSxMeadow] Hook error (SpectatorOverlay.Update): {ex}");
            }
        }

        private static string Describe(string steamId, bool banned, string displayName)
        {
            string cleanName = string.IsNullOrWhiteSpace(displayName) ? "" : displayName.Trim();
            string target = cleanName.Length > 0 ? $"{cleanName} ({steamId})" : steamId;
            return banned
                ? $"Unban the skin of {target} (local, permanent)"
                : $"Ban the skin of {target} (local, permanent)";
        }

        public static void Dispose()
        {
            spectatorUpdateHook?.Dispose();
            spectatorUpdateHook = null;
        }
    }
}