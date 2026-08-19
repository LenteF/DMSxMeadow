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
    [BepInPlugin("dmsxmeadow", "DMS x Meadow", "1.4.0")]
    [BepInDependency("dressmyslugcat", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("henpemaz.rainmeadow", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        public static new ManualLogSource Logger;

        private Hook customizationHook;
        private Hook handleJoinHook;
        private Hook handleDisconnectHook;
        private Hook leaveLobbyHook;
        private Hook meadowCheckHook;
        private bool isInit = false;

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
                if (isInit) return;
                isInit = true;

                Plugin.Logger.LogInfo("[DMSxMeadow] Build 1.4.0 (13/08/2026: sección 'Name' en el selector de colores de historia — color de nombre en chat + luz de tubería independiente del color del cuerpo, sync via currentColors). Si NO ves esta línea, se está cargando un DLL viejo.");

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
                            Logger.LogDebug("[DMSxMeadow] DefaultMeadowHook override activado: se permitirá aplicar skins a jugadores remotos (DMS CheckForMeadowNonselfClient => false).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[DMSxMeadow] No se pudo resolver CheckForMeadowNonselfClient por reflexión: {ex.Message}");
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
                    Plugin.Logger.LogInfo($"[DMSxMeadow] 🎭 Interfaz Meadow detectada (proceso '{ID}', lobby {(lobbyActive ? "ACTIVA" : "sin lobby aún")}). Poll ON. Slugcat: '{lobbySlugcat ?? "sin resolver"}'{(lobbyMode != null ? $" (modo {lobbyMode})" : "")}.");
                    return;
                }

                if (!newIsInterface && MeadowInterfaceActive)
                {
                    MeadowInterfaceActive = false;
                    LastEmittedLobbySlugcat = null;

                    if (ID == ProcessManager.ProcessID.Game)
                    {
                        Plugin.Logger.LogInfo("[DMSxMeadow] 🎮 Entrando a partida (proceso 'Game'): la memoria NO se libera — la descarga de skins continúa en segundo plano.");
                    }
                    else if (ID == ProcessManager.ProcessID.MainMenu)
                    {
                        int released = SkinRegistration.WipeAllMemory();
                        Plugin.Logger.LogInfo($"[DMSxMeadow] 🚪 Volviste al MainMenu: wipe total del refactor ejecutado ({released} entradas liberadas). Poll OFF.");
                    }
                    else
                    {
                        Plugin.Logger.LogInfo($"[DMSxMeadow] 🚪 Saliste de la interfaz Meadow (proceso actual: '{ID}'). Poll OFF — el wipe del refactor se dispara al volver al MainMenu.");
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
                        Plugin.Logger.LogDebug("[DMSxMeadow] 🎲 Slugcat del lobby es 'MeadowRandom': se omite la emisión del handshake (se retoma al realizarse en partida).");
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
                Plugin.Logger.LogDebug($"[DMSxMeadow] Jugador {player.id} entró a la sala ({mode ?? "sin modo"}: '{slugcatName}'). Re-emitiendo handshake local hacia él...");
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

                DMSNetworkTester.SkinSerializer.ForgetPlayer(steamId);
                SkinTransfer.ForgetPlayer(player);

                Plugin.Logger.LogDebug($"[DMSxMeadow] Jugador {player.id} salió. Estado de transferencias y envíos purgados (su skin en memoria se conserva — DECISIONES §4).");
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
                DMSNetworkTester.SkinSerializer.ForgetAllPlayers();
                SkinTransfer.ClearAllTransfers();
                PendingRecreateSteamIds.Clear();
                RealizedPlayersBySteamId.Clear();
                LastNoSlugLogBySteamId.Clear();

                Plugin.Logger.LogDebug($"[DMSxMeadow] 🧹 Sesión terminada: customizaciones y transferencias purgadas (skins en memoria se conservan hasta volver al MainMenu — DECISIONES §4).");
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
                            Plugin.Logger.LogDebug("[DMSxMeadow] 🎭 Cambio de flag de compartir detectado. Re-emitiendo handshake...");
                            DMSNetworkTester.SkinSerializer.BroadcastHandshake(slugcat);
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
                                Plugin.Logger.LogDebug($"[DMSxMeadow] 🎭 Cambio de slugcat en lobby detectado: '{lobbySlugcat}'. Re-emitiendo handshake (los envíos en curso de la skin anterior fueron abortados)...");
                                DMSNetworkTester.SkinSerializer.BroadcastHandshake(lobbySlugcat);
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

                DMSNetworkTester.SkinSerializer.PollMissingHandshakes();

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
            Logger.LogDebug($"[DMSxMeadow] ⏳ Recreación diferida para {steamId} (se reintentará en el próximo Update).");
        }

        internal static void ScheduleRecreateAllSlugs()
        {
            int scheduled = 0;
            foreach (var player in OnlineManager.players)
            {
                string steamId = DMSNetworkTester.SkinSerializer.GetPlayerSteamId(player);
                if (string.IsNullOrEmpty(steamId)) continue;

                if (TryRecreateFromCache(steamId))
                {
                    scheduled++;
                    continue;
                }

                PendingRecreateSteamIds.Add(steamId);
                scheduled++;
            }

            if (scheduled > 0)
            {
                Logger.LogDebug($"[DMSxMeadow] 🔄 Recreación de {scheduled} slug(s) programada tras la recarga de atlas (evita sprites inválidos).");
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
                Logger.LogDebug($"[DMSxMeadow] 🔁 Recreación de gráficos programada para el slug de {steamId} (playerNumber {player.playerState?.playerNumber ?? -1}).");
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
                    if (!string.Equals(DMSNetworkTester.SkinSerializer.GetPlayerSteamId(onlineEntity.owner), steamId, StringComparison.Ordinal)) continue;

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
                        Logger.LogDebug($"[DMSxMeadow] Sin slug realizado del jugador {steamId} en el juego. No hay nada que recrear por ahora.");
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
                                    Plugin.Logger.LogDebug($"[DMSxMeadow] Slug de {steamId} ({slugcatName}) baneado localmente: aplicando skin default (la cola se conserva para evitar que se estire).");
                                }

                                // Strip only the custom skin parts, keep the tail geometry: changing tail
                                // size mid-game makes the tail sprite stick and stretch abnormally.
                                var bannedCustomization = MeadowProfileManager.GetCustomizationBySteamID(steamId, slugcatName);
                                if (bannedCustomization == null)
                                {
                                    bannedCustomization = DMSNetworkTester.SkinSerializer.GetReceivedCustomization(steamId, slugcatName);
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
                                customization = DMSNetworkTester.SkinSerializer.GetReceivedCustomization(steamId, slugcatName);
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
                                    Plugin.Logger.LogDebug($"[DMSxMeadow] Slug remoto {steamId} ({slugcatName}) sin customización. Aplicando skin default limpia.");
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
                                    Plugin.Logger.LogDebug($"[DMSxMeadow] Slug local ({slugcatName}) sin entrada en SaveManager. Aplicando skin default limpia (evita NRE de DMS).");
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
                        Plugin.Logger.LogDebug($"[DMSxMeadow] Fallback sin entrada en SaveManager ({player.slugcatStats.name.value}). Aplicando skin default limpia.");
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
                Plugin.Logger.LogWarning($"[DMSxMeadow] No se pudo consultar SaveManager.Customizations: {ex.Message}");
                return false;
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
                Plugin.Logger.LogWarning($"[DMSxMeadow] No se pudo leer DefaultMeadowSkins: {ex.Message}");
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