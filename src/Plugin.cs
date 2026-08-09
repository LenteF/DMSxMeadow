using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MonoMod.RuntimeDetour;
using RainMeadow;

namespace DMSxMeadow
{
    [BepInPlugin("dmsxmeadow", "DMS x Meadow", "2.0.0")]
    [BepInDependency("dressmyslugcat", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("henpemaz.rainmeadow", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        public static new ManualLogSource Logger;

        private Hook customizationHook;
        private Hook handleJoinHook;
        private Hook handleDisconnectHook;
        private bool isInit = false;

        public void Awake()
        {
            Instance = this;
            Logger = base.Logger;

            try
            {
                SkinRegistration.ClearCacheOnStartup();
                On.RainWorld.OnModsInit += OnModsInit;
                On.RainWorld.Update += RainWorld_Update;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Initialization error: {ex.Message}");
                Logger.LogError(ex.StackTrace);
            }
        }

        private void OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
        {
            orig(self);

            try
            {
                if (isInit) return;
                isInit = true;

                MachineConnector.SetRegisteredOI("dmsxmeadow", DMSxMeadowOptions.Instance);

                MeadowProfileManager.Load();
                SkinTransfer.Initialize();

                InitializeHooks();
                FancyMenuHookHandler.Initialize();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in OnModsInit: {ex.Message}");
                Logger.LogError(ex.StackTrace);
            }
        }

        private void InitializeHooks()
        {
            try
            {
                On.RoomCamera.SpriteLeaser.ctor += SpriteLeaser_Ctor_Hook;

                MethodInfo originalFor = typeof(DressMySlugcat.Customization)
                    .GetMethod("For", new Type[] { typeof(Player), typeof(bool) });

                if (originalFor != null)
                {
                    MethodInfo hookFor = typeof(Plugin)
                        .GetMethod("Customization_For_Hook", BindingFlags.NonPublic | BindingFlags.Static);

                    if (hookFor != null)
                    {
                        customizationHook = new Hook(originalFor, hookFor);
                    }
                }

                MethodInfo joinMethod = typeof(MatchmakingManager).GetMethod("HandleJoin", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo disconnectMethod = typeof(MatchmakingManager).GetMethod("HandleDisconnect", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo joinHook = typeof(Plugin).GetMethod("MatchmakingManager_HandleJoin", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo disconnectHook = typeof(Plugin).GetMethod("MatchmakingManager_HandleDisconnect", BindingFlags.NonPublic | BindingFlags.Static);

                if (joinMethod != null && joinHook != null)
                {
                    handleJoinHook = new Hook(joinMethod, joinHook);
                }
                if (disconnectMethod != null && disconnectHook != null)
                {
                    handleDisconnectHook = new Hook(disconnectMethod, disconnectHook);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Hook application error: {ex.Message}");
            }
        }

        private static void SpriteLeaser_Ctor_Hook(On.RoomCamera.SpriteLeaser.orig_ctor orig, RoomCamera.SpriteLeaser sLeaser, IDrawable obj, RoomCamera rCam)
        {
            orig(sLeaser, obj, rCam);

            try
            {
                if (obj is not PlayerGraphics playerGraphics) return;
                if (playerGraphics?.player == null) return;

                var onlineEntity = playerGraphics.player.abstractCreature.GetOnlineObject();
                if (onlineEntity == null || !onlineEntity.isMine) return;

                if (OnlineManager.lobby == null || !OnlineManager.lobby.isAvailable) return;

                string slugcatName = playerGraphics.player.slugcatStats.name.value;
                DMSNetworkTester.SkinSerializer.BroadcastHandshake(slugcatName);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Hook error (SpriteLeaser_ctor): {ex.Message}");
            }
        }

        private static void MatchmakingManager_HandleJoin(
            Action<MatchmakingManager, OnlinePlayer> orig,
            MatchmakingManager self,
            OnlinePlayer player)
        {
            orig(self, player);

            try
            {
                if (player == null || player.isMe) return;
                if (OnlineManager.lobby == null || !OnlineManager.lobby.isAvailable) return;

                string slugcatName = null;
                var rainWorldGame = RWCustom.Custom.rainWorld?.processManager?.currentMainLoop as RainWorldGame;
                if (rainWorldGame?.FirstRealizedPlayer != null)
                {
                    slugcatName = rainWorldGame.FirstRealizedPlayer.slugcatStats.name.value;
                }

                if (string.IsNullOrEmpty(slugcatName)) return;

                Plugin.Logger.LogInfo($"[DMSxMeadow] Jugador {player.id} entró a la sala. Re-emitiendo handshake local hacia él...");
                DMSNetworkTester.SkinSerializer.BroadcastHandshake(slugcatName);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Hook error (HandleJoin): {ex.Message}");
            }
        }

        private static void MatchmakingManager_HandleDisconnect(
            Action<MatchmakingManager, OnlinePlayer> orig,
            MatchmakingManager self,
            OnlinePlayer player)
        {
            orig(self, player);

            try
            {
                if (player == null) return;

                string steamId = DMSNetworkTester.SkinSerializer.GetPlayerSteamId(player);
                if (string.IsNullOrEmpty(steamId)) return;

                int clearedSkins = SkinRegistration.ClearCachedSkinsFor(steamId);
                DMSNetworkTester.SkinSerializer.ForgetPlayer(steamId);
                SkinTransfer.ForgetPlayer(player);

                Plugin.Logger.LogInfo($"[DMSxMeadow] Jugador {player.id} salió. Limpieza: {clearedSkins} skin(s) de caché de memoria, transferencias y registro de envíos purgados.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Hook error (HandleDisconnect): {ex.Message}");
            }
        }

        private static DressMySlugcat.Customization Customization_For_Hook(
            Func<Player, bool, DressMySlugcat.Customization> orig,
            Player player,
            bool mergeDefaults)
        {
            try
            {
                if (player?.abstractCreature != null)
                {
                    if (RainMeadow.OnlinePhysicalObject.map.TryGetValue(
                        player.abstractCreature, out var onlineEntity))
                    {
                        var owner = onlineEntity.owner;

                        if (owner != null && owner.id != null)
                        {
                            string steamId = SteamIdFromOwner(owner);

                            string slugcatName = player.slugcatStats.name.value;

                            var customization = MeadowProfileManager.GetCustomizationBySteamID(steamId, slugcatName);
                            string source = "perfil-manual";
                            if (customization == null)
                            {
                                customization = DMSNetworkTester.SkinSerializer.GetReceivedCustomization(steamId, slugcatName);
                                source = "handshake-recibido";
                            }

                            if (customization != null)
                            {
                                LogSkinResolution(steamId, slugcatName, source, customization);
                                var result = customization.Copy();
                                result.PlayerNumber = 0;
                                NormalizeSpriteSheetIds(result);
                                return result;
                            }

                            if (!onlineEntity.isMine)
                            {
                                LogSkinResolution(steamId, slugcatName, "default-nada", null);
                                Plugin.Logger.LogDebug($"[DMSxMeadow] Slug remoto {steamId} ({slugcatName}) sin customización. Aplicando skin default limpia.");
                                var clean = new DressMySlugcat.Customization
                                {
                                    Slugcat = slugcatName,
                                    PlayerNumber = 0
                                };
                                return clean;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Hook error: {ex.Message}");
                Logger.LogError(ex.StackTrace);
            }

            return orig(player, mergeDefaults);
        }

        private static string SteamIdFromOwner(RainMeadow.OnlinePlayer owner)
        {
            if (owner == null || owner.id == null) return null;

            if (owner.id is RainMeadow.SteamMatchmakingManager.SteamPlayerId steamPlayerId)
            {
                return steamPlayerId.steamID.m_SteamID.ToString();
            }

            return owner.id.ToString();
        }

private static readonly HashSet<string> PendingRecreateSteamIds = new HashSet<string>(StringComparer.Ordinal);

        private void RainWorld_Update(On.RainWorld.orig_Update orig, RainWorld self)
        {
            orig(self);

            try
            {
                if (PendingRecreateSteamIds.Count == 0) return;

                var stillPending = new HashSet<string>(StringComparer.Ordinal);
                foreach (string steamId in PendingRecreateSteamIds)
                {
                    if (!ApplyRecreateForSteamId(steamId))
                    {
                        stillPending.Add(steamId);
                    }
                }

                PendingRecreateSteamIds.Clear();
                PendingRecreateSteamIds.UnionWith(stillPending);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error al procesar recreaciones pendientes: {ex.Message}");
            }
        }

        internal static void ScheduleRecreateForSteamId(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return;

            if (ApplyRecreateForSteamId(steamId)) return;

            PendingRecreateSteamIds.Add(steamId);
            Logger.LogDebug($"[DMSxMeadow] ⏳ Recreación diferida para {steamId} (se reintentará en el próximo Update).");
        }

        private static bool ApplyRecreateForSteamId(string steamId)
        {
            try
            {
                if (RWCustom.Custom.rainWorld?.processManager?.currentMainLoop is not RainWorldGame gameState)
                {
                    return false;
                }

                bool foundSlug = false;
                foreach (AbstractCreature absPlayer in gameState.Players)
                {
                    if (absPlayer?.realizedCreature is not Player player) continue;

                    if (!RainMeadow.OnlinePhysicalObject.map.TryGetValue(absPlayer, out var onlineEntity)) continue;
                    if (!string.Equals(SteamIdFromOwner(onlineEntity.owner), steamId, StringComparison.Ordinal)) continue;

                    foundSlug = true;
                    if (player.graphicsModule is PlayerGraphics pg
                        && DressMySlugcat.Hooks.PlayerGraphicsHooks.PlayerGraphicsData.TryGetValue(pg, out var data))
                    {
                        data.ScheduleForRecreation = true;
                        Logger.LogDebug($"[DMSxMeadow] 🔁 Recreación de gráficos programada para el slug de {steamId} (playerNumber {player.playerState?.playerNumber ?? -1}).");
                    }
                }

                if (!foundSlug)
                {
                    Logger.LogDebug($"[DMSxMeadow] Sin slug realizado del jugador {steamId} en el juego. No hay nada que recrear por ahora.");
                }

                return foundSlug;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error al programar recreación para {steamId}: {ex.Message}");
                return true;
            }
        }

        private static readonly HashSet<string> ResolutionLogged = new HashSet<string>();

        private static void LogSkinResolution(string steamId, string slugcatName, string source, DressMySlugcat.Customization customization)
        {
            string key = steamId + "|" + slugcatName;
            if (!ResolutionLogged.Add(key)) return;

            string skins = customization?.CustomSprites == null
                ? "(sin sprites)"
                : string.Join(", ", customization.CustomSprites
                    .Where(s => s != null && !string.IsNullOrEmpty(s.SpriteSheetID))
                    .Select(s => s.SpriteSheetID)
                    .Distinct(StringComparer.OrdinalIgnoreCase));

            Plugin.Logger.LogInfo($"[DMSxMeadow] 🎯 RESOLUCIÓN de skin: jugador {steamId} ({slugcatName}) → [{source}] skins=[{skins}]");
        }

        private static void NormalizeSpriteSheetIds(DressMySlugcat.Customization customization)
        {
            if (customization?.CustomSprites == null) return;

            foreach (var sprite in customization.CustomSprites)
            {
                if (sprite == null || string.IsNullOrEmpty(sprite.SpriteSheetID)) continue;

                if (DressMySlugcat.SpriteSheet.Get(sprite.SpriteSheetID) != null) continue;

                var match = DressMySlugcat.Plugin.SpriteSheets.FirstOrDefault(s =>
                    s != null && !string.IsNullOrEmpty(s.ID)
                    && s.ID.Equals(sprite.SpriteSheetID, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    sprite.SpriteSheetID = match.ID;
                }
            }
        }

        public void OnDestroy()
        {
            On.RainWorld.OnModsInit -= OnModsInit;
            On.RainWorld.Update -= RainWorld_Update;
            On.RoomCamera.SpriteLeaser.ctor -= SpriteLeaser_Ctor_Hook;
            customizationHook?.Dispose();
            handleJoinHook?.Dispose();
            handleDisconnectHook?.Dispose();
            FancyMenuHookHandler.Dispose();
        }
    }
}
