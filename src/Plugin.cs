using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MonoMod.RuntimeDetour;
using RainMeadow;
using UnityEngine;

namespace DMSxMeadow
{
    [BepInPlugin("dmsxmeadow", "DMSxMeadow", "1.4.5")]
    [BepInDependency("dressmyslugcat", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("henpemaz.rainmeadow", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        public static new ManualLogSource Logger;

        // Localization: resolves a dmsxm_ key through the game's InGameTranslator
        // (mod files: text/text_eng/strings.txt, text/text_spa/strings.txt).
        // Falls back to the English literal if the key is missing or the game
        // translator is not ready yet.
        public static string Tr(string key, string fallback)
        {
            try
            {
                var rainWorld = RWCustom.Custom.rainWorld;
                var translator = rainWorld != null ? rainWorld.inGameTranslator : null;
                string res = translator != null ? translator.Translate(key) : null;
                if (string.IsNullOrEmpty(res) || res == key || res == "!NO TRANSLATION!") return fallback;
                return res;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private Hook customizationHook;
        private Hook handleJoinHook;
        private Hook handleDisconnectHook;
        private Hook leaveLobbyHook;
        private Hook meadowCheckHook;
        private bool isInit = false;

        private static FieldInfo _dmsxmeadowEnabledField;
        private static bool _dmsxmeadowEnabledResolved;

        public void Awake()
        {
            Instance = this;
            Logger = base.Logger;

            try
            {
                On.RainWorld.OnModsInit += OnModsInit;
                On.RainWorld.Update += RainWorld_Update;
                On.ProcessManager.PostSwitchMainProcess += ProcessManager_PostSwitchMainProcess;
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
                ForceDefaultMeadowSkinsOff();

                if (isInit) return;
                isInit = true;

                Plugin.Logger.LogInfo("Build 1.4.5 (12/09/2026: sistema de verificación de identificador de skin mas permisivo). Si NO ves esta línea, se está cargando un DLL viejo.");

                MachineConnector.SetRegisteredOI("dmsxmeadow", DMSxMeadowOptions.Instance);
                DMSxMeadowOptions.Instance.EnsureConfigBound();

                MeadowProfileManager.Load();
                SkinBanManager.Load();
                SkinTransfer.Initialize();

                InitializeHooks();
                FancyMenuHookHandler.Initialize();
                SpectatorPlayerButtonHook.Initialize();
                StoryLobbyShareSkinIndicator.Initialize();
                PlayerNameColorHooks.Initialize();
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

                MethodInfo leaveLobbyMethod = typeof(OnlineManager).GetMethod("LeaveLobby", BindingFlags.Public | BindingFlags.Static);
                MethodInfo leaveLobbyHook = typeof(Plugin).GetMethod("OnlineManager_LeaveLobby", BindingFlags.NonPublic | BindingFlags.Static);
                if (leaveLobbyMethod != null && leaveLobbyHook != null)
                {
                    this.leaveLobbyHook = new Hook(leaveLobbyMethod, leaveLobbyHook);
                }

                try
                {
                    var dmsAssembly = typeof(DressMySlugcat.Customization).Assembly;
                    var meadowType = dmsAssembly.GetType("DressMySlugcat.MeadowCompatibility", false);
                    if (meadowType != null)
                    {
                        MethodInfo meadowCheckMethod = meadowType.GetMethod(
                            "CheckForMeadowNonselfClient", BindingFlags.Public | BindingFlags.Static);
                        MethodInfo meadowCheckHookInfo = typeof(Plugin)
                            .GetMethod("MeadowCompatibility_CheckNonselfHook",
                                BindingFlags.NonPublic | BindingFlags.Static);

                        if (meadowCheckMethod != null && meadowCheckHookInfo != null)
                        {
                            meadowCheckHook = new Hook(meadowCheckMethod, meadowCheckHookInfo);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"No se pudo resolver CheckForMeadowNonselfClient por reflexión: {ex.Message}");
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
                SkinSerializer.BroadcastHandshake(slugcatName);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Hook error (SpriteLeaser_ctor): {ex.Message}");
            }
        }

        // ===================================================================
        // P4: RESOLUCIÓN DEL SLUGCAT LOCAL SEGÚN EL CONTEXTO
        // ===================================================================

        private static string LastEmittedLobbySlugcat = null;

        private static bool _handshakeReemitPending;

        public static void RequestHandshakeReemit()
        {
            _handshakeReemitPending = true;
        }

        private static bool MeadowInterfaceActive = false;

        private static bool RandomSlugcatSkipLogged = false;

        private static bool IsMeadowInterfaceProcessId(ProcessManager.ProcessID id)
        {
            return id == RainMeadow.RainMeadow.Ext_ProcessID.MeadowMenu
                || id == RainMeadow.RainMeadow.Ext_ProcessID.LobbySelectMenu
                || id == RainMeadow.RainMeadow.Ext_ProcessID.LobbyCreateMenu
                || id == RainMeadow.RainMeadow.Ext_ProcessID.ArenaLobbyMenu
                || id == RainMeadow.RainMeadow.Ext_ProcessID.StoryMenu
                || id == RainMeadow.RainMeadow.Ext_ProcessID.SpectatorMode
                || id == RainMeadow.RainMeadow.Ext_ProcessID.OnlineManager;
        }

        private static bool IsMeadowInterfaceProcess(MainLoopProcess currentMainLoop)
        {
            return currentMainLoop != null && IsMeadowInterfaceProcessId(currentMainLoop.ID);
        }

        private static void ProcessManager_PostSwitchMainProcess(On.ProcessManager.orig_PostSwitchMainProcess orig, ProcessManager self, ProcessManager.ProcessID ID)
        {
            orig(self, ID);

            try
            {
                bool newIsInterface = IsMeadowInterfaceProcessId(ID);
                if (newIsInterface && !MeadowInterfaceActive)
                {
                    MeadowInterfaceActive = true;
                    bool lobbyActive = OnlineManager.lobby != null && OnlineManager.lobby.isAvailable;
                    var (lobbySlugcat, lobbyMode) = lobbyActive ? ResolveLocalSlugcatName() : (null, null);
                    return;
                }

                if (!newIsInterface && MeadowInterfaceActive)
                {
                    MeadowInterfaceActive = false;
                    LastEmittedLobbySlugcat = null;

                    if (ID == ProcessManager.ProcessID.Game)
                    {
                    }
                    else if (ID == ProcessManager.ProcessID.MainMenu)
                    {
                        SkinRegistration.WipeAllMemory();
                    }
                    else
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Hook error (PostSwitchMainProcess): {ex.Message}");
            }
        }

        internal static (string Slugcat, string Mode) ResolveLocalSlugcatName()
        {
            var currentMainLoop = RWCustom.Custom.rainWorld?.processManager?.currentMainLoop;

            if (currentMainLoop is RainWorldGame rainWorldGame)
            {
                string realized = rainWorldGame.FirstRealizedPlayer?.slugcatStats.name.value;
                return (realized, realized == null ? null : "partida");
            }

            if (OnlineManager.lobby == null || !OnlineManager.lobby.isAvailable) return (null, null);

            if (OnlineManager.lobby.gameMode is ArenaOnlineGameMode)
            {
                var playingAs = ArenaHelpers.GetArenaClientSettings(OnlineManager.mePlayer)?.playingAs;
                if (playingAs == null || playingAs == RainMeadow.RainMeadow.Ext_SlugcatStatsName.OnlineRandomSlugcat || playingAs == RainMeadow.RainMeadow.Ext_SlugcatStatsName.OnlineOverseerSpectator)
                {
                    if (playingAs == RainMeadow.RainMeadow.Ext_SlugcatStatsName.OnlineRandomSlugcat && !RandomSlugcatSkipLogged)
                    {
                        RandomSlugcatSkipLogged = true;
                    }
                    return (null, "arena");
                }
                return (playingAs.value, "arena");
            }

            if (OnlineManager.lobby.gameMode is StoryGameMode storyGameMode)
            {
                string slugcat = storyGameMode.preferredSlug?.value ?? storyGameMode.currentCampaign?.value;
                return (slugcat, "historia");
            }

            return (null, null);
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

                var (slugcatName, mode) = ResolveLocalSlugcatName();
                if (string.IsNullOrEmpty(slugcatName)) return;

                LastEmittedLobbySlugcat = slugcatName;
                SkinSerializer.BroadcastHandshake(slugcatName);
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

                string steamId = SkinSerializer.GetPlayerSteamId(player);
                if (string.IsNullOrEmpty(steamId)) return;

                SkinSerializer.ForgetPlayer(steamId);
                SkinTransfer.ForgetPlayer(player);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Hook error (HandleDisconnect): {ex.Message}");
            }
        }

        private static void OnlineManager_LeaveLobby(Action orig)
        {
            bool wasInLobby = OnlineManager.lobby != null;
            orig();

            if (!wasInLobby) return;

            try
            {
                SkinSerializer.ForgetAllPlayers();
                SkinTransfer.ClearAllTransfers();
                PendingRecreateSteamIds.Clear();
                RealizedPlayersBySteamId.Clear();
                LastNoSlugLogBySteamId.Clear();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Hook error (LeaveLobby): {ex.Message}");
            }
        }

        private void RainWorld_Update(On.RainWorld.orig_Update orig, RainWorld self)
        {
            orig(self);

            try
            {
                try { if (DressMySlugcat.Plugin.Options?.DefaultMeadowSkins?.Value == true) DressMySlugcat.Plugin.Options.DefaultMeadowSkins.Value = false; } catch { }

                if (_handshakeReemitPending)
                {
                    _handshakeReemitPending = false;
                    if (OnlineManager.lobby != null && OnlineManager.lobby.isAvailable)
                    {
                        var (slugcat, _) = ResolveLocalSlugcatName();
                        if (slugcat != null)
                        {
                            LastEmittedLobbySlugcat = slugcat;
                            SkinTransfer.AbortAllOutgoing();
                            SkinSerializer.BroadcastHandshake(slugcat);
                        }
                    }
                }

                if (OnlineManager.lobby != null && OnlineManager.lobby.isAvailable)
                {
                    if (RWCustom.Custom.rainWorld?.processManager?.currentMainLoop is not RainWorldGame)
                    {
                        var (lobbySlugcat, _) = ResolveLocalSlugcatName();
                        if (lobbySlugcat != null)
                        {
                            if (lobbySlugcat != LastEmittedLobbySlugcat)
                            {
                                LastEmittedLobbySlugcat = lobbySlugcat;
                                SkinTransfer.AbortAllOutgoing();
                                SkinSerializer.BroadcastHandshake(lobbySlugcat);
                            }
                        }
                        else
                        {
                            LastEmittedLobbySlugcat = null;
                        }
                    }
                }
                else
                {
                    LastEmittedLobbySlugcat = null;
                }

                SkinSerializer.PollMissingHandshakes();

                SkinTransfer.UpdatePendingTransfers();
                SkinTransfer.RetryPendingRequests();

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
                Logger.LogError($"Error en RainWorld_Update (SkinTransfer): {ex.Message}");
            }
        }

        // ===================================================================
        // RECREACIÓN EN CALIENTE DEL SLUG REMOTO
        // ===================================================================

        private static readonly HashSet<string> PendingRecreateSteamIds = new HashSet<string>(StringComparer.Ordinal);

        private static readonly Dictionary<string, WeakReference> RealizedPlayersBySteamId =
            new Dictionary<string, WeakReference>(StringComparer.Ordinal);

        private static readonly Dictionary<string, float> LastNoSlugLogBySteamId =
            new Dictionary<string, float>(StringComparer.Ordinal);

        internal static void ScheduleRecreateForSteamId(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return;

            if (TryRecreateFromCache(steamId)) return;

            PendingRecreateSteamIds.Add(steamId);
        }

        internal static void ScheduleRecreateAllSlugs()
        {
            int scheduled = 0;
            foreach (var player in OnlineManager.players)
            {
                string steamId = SkinSerializer.GetPlayerSteamId(player);
                if (string.IsNullOrEmpty(steamId)) continue;

                if (TryRecreateFromCache(steamId))
                {
                    scheduled++;
                    continue;
                }

                PendingRecreateSteamIds.Add(steamId);
                scheduled++;
            }


        }

        private static bool TryRecreateFromCache(string steamId)
        {
            if (RealizedPlayersBySteamId.TryGetValue(steamId, out var wr) && wr != null && wr.IsAlive && wr.Target is Player cached)
            {
                return ScheduleRecreationFor(cached, steamId);
            }

            return false;
        }

        private static bool ScheduleRecreationFor(Player player, string steamId)
        {
            if (player?.graphicsModule is PlayerGraphics pg
                && DressMySlugcat.Hooks.PlayerGraphicsHooks.PlayerGraphicsData.TryGetValue(pg, out var data))
            {
                data.ScheduleForRecreation = true;
                return true;
            }

            return false;
        }

        private static bool ApplyRecreateForSteamId(string steamId)
        {
            try
            {
                if (RWCustom.Custom.rainWorld?.processManager?.currentMainLoop is not RainWorldGame gameState)
                {
                    return false;
                }

                if (TryRecreateFromCache(steamId)) return true;

                bool foundSlug = false;
                foreach (AbstractCreature absPlayer in gameState.Players)
                {
                    if (absPlayer?.realizedCreature is not Player player) continue;

                    if (!RainMeadow.OnlinePhysicalObject.map.TryGetValue(absPlayer, out var onlineEntity)) continue;
                    if (!string.Equals(SkinSerializer.GetPlayerSteamId(onlineEntity.owner), steamId, StringComparison.Ordinal)) continue;

                    foundSlug = true;
                    if (ScheduleRecreationFor(player, steamId))
                    {
                        return true;
                    }
                }

                if (!foundSlug)
                {
                    float now = Time.time;
                    if (!LastNoSlugLogBySteamId.TryGetValue(steamId, out float last) || now - last > 5f)
                    {
                        LastNoSlugLogBySteamId[steamId] = now;
                    }
                }

                return foundSlug;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error al programar recreación para {steamId}: {ex.Message}");
                return true;
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
                            string steamId;
                            if (owner.id is RainMeadow.SteamMatchmakingManager.SteamPlayerId steamPlayerId)
                            {
                                steamId = steamPlayerId.steamID.m_SteamID.ToString();
                            }
                            else
                            {
                                steamId = owner.id.ToString();
                            }

                            string slugcatName = player.slugcatStats.name.value;

                            RealizedPlayersBySteamId[steamId] = new WeakReference(player);

                            if (PendingRecreateSteamIds.Remove(steamId))
                            {
                                ScheduleRecreationFor(player, steamId);
                            }

                            if (SkinBanManager.IsBanned(steamId))
                            {
                                if (DefaultSkinDebugLogged.Add("banned|" + steamId + "|" + slugcatName))
                                {
                                }

                                // Strip only the custom skin parts, keep the tail geometry: changing tail
                                // size mid-game makes the tail sprite stick and stretch abnormally.
                                var bannedCustomization = MeadowProfileManager.GetCustomizationBySteamID(steamId, slugcatName);
                                if (bannedCustomization == null)
                                {
                                    bannedCustomization = SkinSerializer.GetReceivedCustomization(steamId, slugcatName);
                                }

                                if (bannedCustomization != null)
                                {
                                    var bannedWithTail = bannedCustomization.Copy();
                                    bannedWithTail.CustomSprites.Clear();
                                    // Keep tail geometry (size/shape) but reset its color to default.
                                    bannedWithTail.CustomTail.ColorHex = null;
                                    bannedWithTail.PlayerNumber = 0;
                                    return bannedWithTail;
                                }

                                var bannedClean = new DressMySlugcat.Customization
                                {
                                    Slugcat = slugcatName,
                                    PlayerNumber = 0
                                };
                                return bannedClean;
                            }

                            var customization = MeadowProfileManager.GetCustomizationBySteamID(steamId, slugcatName);
                            if (customization == null)
                            {
                                customization = SkinSerializer.GetReceivedCustomization(steamId, slugcatName);
                            }

                            if (customization != null)
                            {
                                var result = customization.Copy();
                                result.PlayerNumber = 0;
                                return result;
                            }

                            if (!onlineEntity.isMine)
                            {
                                if (DefaultSkinDebugLogged.Add(steamId + "|" + slugcatName))
                                {
                                }
                                var clean = new DressMySlugcat.Customization
                                {
                                    Slugcat = slugcatName,
                                    PlayerNumber = 0
                                };
                                return clean;
                            }

                            if (!HasSavedCustomization(slugcatName, player.playerState?.playerNumber ?? 0))
                            {
                                if (DefaultSkinDebugLogged.Add("local-clean|" + slugcatName))
                                {
                                }
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

            try
            {
                if (player?.playerState != null &&
                    !HasSavedCustomization(player.slugcatStats.name.value, player.playerState.playerNumber))
                {
                    if (DefaultSkinDebugLogged.Add("fallback-clean|" + player.slugcatStats.name.value))
                    {
                    }
                    return new DressMySlugcat.Customization
                    {
                        Slugcat = player.slugcatStats.name.value,
                        PlayerNumber = 0
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Fallback hook error: {ex.Message}");
            }

            return orig(player, mergeDefaults);
        }

        private static bool HasSavedCustomization(string slugcatName, int playerNumber)
        {
            try
            {
                return DressMySlugcat.SaveManager.Customizations.Any(
                    x => x.Matches(slugcatName, playerNumber));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"No se pudo consultar SaveManager.Customizations: {ex.Message}");
                return false;
            }
        }

        private static void ForceDefaultMeadowSkinsOff()
        {
            if (!_dmsxmeadowEnabledResolved)
            {
                try
                {
                    var dmsAssembly = typeof(DressMySlugcat.Customization).Assembly;
                    var dmsPluginType = dmsAssembly.GetType("DressMySlugcat.Plugin", false);
                    if (dmsPluginType != null)
                    {
                        _dmsxmeadowEnabledField = dmsPluginType.GetField("dmsxmeadowEnabled", BindingFlags.Static | BindingFlags.Public);
                        if (_dmsxmeadowEnabledField != null && _dmsxmeadowEnabledField.FieldType == typeof(bool))
                        {
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Error resolviendo dmsxmeadowEnabled: {ex.Message}");
                }
                _dmsxmeadowEnabledResolved = true;
            }

            if (_dmsxmeadowEnabledField != null)
            {
                try
                {
                    if (!(bool)_dmsxmeadowEnabledField.GetValue(null))
                    {
                        _dmsxmeadowEnabledField.SetValue(null, true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"No se pudo setear dmsxmeadowEnabled: {ex.Message}");
                }
            }

            try
            {
                var ds = DressMySlugcat.Plugin.Options?.DefaultMeadowSkins;
                if (ds != null && ds.Value)
                {
                    ds.Value = false;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"No se pudo desactivar DefaultMeadowSkins: {ex.Message}");
            }
        }

        private static bool MeadowCompatibility_CheckNonselfHook(
            Func<Player, bool> orig, Player self)
        {
            try
            {
                if (DressMySlugcat.Plugin.Options != null && DressMySlugcat.Plugin.Options.DefaultMeadowSkins.Value)
                {
                    return orig(self);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"No se pudo leer DefaultMeadowSkins: {ex.Message}");
            }

            return false;
        }

        private static readonly HashSet<string> DefaultSkinDebugLogged = new HashSet<string>();

        public void OnDestroy()
        {
            On.RainWorld.OnModsInit -= OnModsInit;
            On.RainWorld.Update -= RainWorld_Update;
            On.ProcessManager.PostSwitchMainProcess -= ProcessManager_PostSwitchMainProcess;
            On.RoomCamera.SpriteLeaser.ctor -= SpriteLeaser_Ctor_Hook;
            customizationHook?.Dispose();
            handleJoinHook?.Dispose();
            handleDisconnectHook?.Dispose();
            leaveLobbyHook?.Dispose();
            meadowCheckHook?.Dispose();
            FancyMenuHookHandler.Dispose();
            SpectatorPlayerButtonHook.Dispose();
            StoryLobbyShareSkinIndicator.Dispose();
            PlayerNameColorHooks.Dispose();
        }
    }
}