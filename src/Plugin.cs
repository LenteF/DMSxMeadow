using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using RainMeadow;
using UnityEngine;

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
                DMSxMeadowOptions.Instance.EnsureConfigBound();

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

                // DMS nativo no aplica skins a slugs ajenos en sesión Meadow si su opción
                // "DefaultMeadowSkins" (activa por defecto) está ON: InitiateCustomGraphics
                // devuelve null antes de construir el PlayerGraphicsData (PlayerGraphicsHooks.cs).
                // Forzamos false en memoria para que DMS procese a los jugadores remotos y
                // Customization.For efectivamente reciba nuestras skins descargadas.
                // (Resolución por reflexión: la DLL de referencia en lib\ es anterior a MeadowCompatibility.)
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
                            Logger.LogInfo("[DMSxMeadow] DefaultMeadowHook override activado: se permitirá aplicar skins a jugadores remotos (DMS CheckForMeadowNonselfClient => false).");
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

        /// <summary>
        /// Dispara el handshake automáticamente cuando se crea el leaser de un Player (entrada
        /// a sala / recreación del slugcat / cambio de slug), sin frame-watching.
        /// H-6 (RF-1): ya no hay tecla K de debug.
        /// </summary>
        private static void SpriteLeaser_Ctor_Hook(On.RoomCamera.SpriteLeaser.orig_ctor orig, RoomCamera.SpriteLeaser sLeaser, IDrawable obj, RoomCamera rCam)
        {
            orig(sLeaser, obj, rCam);

            try
            {
                if (obj is not PlayerGraphics playerGraphics) return;
                if (playerGraphics?.player == null) return;

                // Solo el jugador local de esta máquina emite su propio handshake (RF-7).
                var onlineEntity = playerGraphics.player.abstractCreature.GetOnlineObject();
                if (onlineEntity == null || !onlineEntity.isMine) return;

                // Solo emitir si hay sesión Meadow activa.
                if (OnlineManager.lobby == null || !OnlineManager.lobby.isAvailable) return;

                string slugcatName = playerGraphics.player.slugcatStats.name.value;
                DMSNetworkTester.SkinSerializer.BroadcastHandshake(slugcatName);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Hook error (SpriteLeaser_ctor): {ex.Message}");
            }
        }

        /// <summary>
        /// Entrada de jugador a la sala: el jugador local re-emite su handshake hacia el
        /// recién llegado. El dedupe interno de BroadcastHandshake (SentPlayersForCurrentSkin)
        /// hace que solo el jugador nuevo reciba el estado actual de la skin local.
        /// </summary>
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

        /// <summary>
        /// Salida de jugador: limpieza total de sus datos en memoria (skin cache por emisor,
        /// transferencias incompletas, registro de destinatarios) y de su carpeta de caché en
        /// disco si quedó huérfana. Hook en la fuente común de desconexión de Steam y LAN.
        /// </summary>
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

        /// <summary>
        /// Tick del mod: avanza la cola throttled de envío de archivos (1 paquete cada
        /// SendIntervalSeconds), reintenta archivos sin ACK, y reintenta la petición
        /// inicial de skin si no llegó ningún archivo a tiempo (ver SkinTransfer.cs).
        /// También procesa las recreaciones de slug pendientes (skin recién llegada).
        /// </summary>
        private void RainWorld_Update(On.RainWorld.orig_Update orig, RainWorld self)
        {
            orig(self);

            try
            {
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
        // Cuando llega la customización/skin de un jugador (handshake o archivos
        // completados), programamos la recreación de sus gráficos. DMS nativo
        // procesa PlayerGraphicsData.ScheduleForRecreation en el siguiente
        // SpriteLeaser.Update (PlayerGraphicsHooks.cs:317) y regenera el atuendo
        // con la customización actual — sin esperar a cruzar una tubería.
        // ===================================================================

        private static readonly HashSet<string> PendingRecreateSteamIds = new HashSet<string>(StringComparer.Ordinal);

        private static readonly Dictionary<string, WeakReference> RealizedPlayersBySteamId =
            new Dictionary<string, WeakReference>(StringComparer.Ordinal);

        // Evita loguear "Sin slug realizado" cada frame: máximo 1 mensaje cada 5 segs por steamId.
        private static readonly Dictionary<string, float> LastNoSlugLogBySteamId =
            new Dictionary<string, float>(StringComparer.Ordinal);

        internal static void ScheduleRecreateForSteamId(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return;

            if (TryRecreateFromCache(steamId)) return;

            PendingRecreateSteamIds.Add(steamId);
            Logger.LogDebug($"[DMSxMeadow] ⏳ Recreación diferida para {steamId} (se reintentará en el próximo Update).");
        }

        /// <summary>
        /// Recrea TODOS los slugs realizados (incluido el local). Se usa después de
        /// ReloadAtlases(): la recarga de atlas invalida los FAtlasElement de los
        /// sprites ya dibujados y deja los slugs invisibles/blancos hasta que se
        /// recrean (síntoma "me volví invisible al recibir la skin del otro").
        /// </summary>
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
                Logger.LogInfo($"[DMSxMeadow] 🔄 Recreación de {scheduled} slug(s) programada tras la recarga de atlas (evita sprites inválidos).");
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

                // Vía 1: registro de players visto desde el hook (fiable también en shelter/entrada).
                if (TryRecreateFromCache(steamId)) return true;

                // Vía 2: escaneo de gameState.Players (fallback).
                bool foundSlug = false;
                foreach (AbstractCreature absPlayer in gameState.Players)
                {
                    if (absPlayer?.realizedCreature is not Player player) continue;

                    if (!RainMeadow.OnlinePhysicalObject.map.TryGetValue(absPlayer, out var onlineEntity)) continue;
                    if (!string.Equals(SteamIdFromOwner(onlineEntity.owner), steamId, StringComparison.Ordinal)) continue;

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

        private static string SteamIdFromOwner(RainMeadow.OnlinePlayer owner)
        {
            if (owner == null || owner.id == null) return null;

            if (owner.id is RainMeadow.SteamMatchmakingManager.SteamPlayerId steamPlayerId)
            {
                return steamPlayerId.steamID.m_SteamID.ToString();
            }

            return owner.id.ToString();
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

                            // Registra el jugador bajo su steamId: la vía fiable para
                            // encontrar al slug remoto y recrearlo en caliente cuando
                            // llegue su skin (sin esperar a cruzar una tubería).
                            RealizedPlayersBySteamId[steamId] = new WeakReference(player);

                            // Si había una recreación pendiente para él, la aplicamos aquí
                            // mismo sobre sus gráficos reales (sin depender del Update).
                            if (PendingRecreateSteamIds.Remove(steamId))
                            {
                                ScheduleRecreationFor(player, steamId);
                            }

                            // D1: asignación manual > caché recibida por handshake > default.
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

                            // Slug remoto sin customización: devolver una customización
                            // limpia en vez de caer al orig() de DMS, que no distingue
                            // identidad y aplicaría la skin local del observador al slug
                            // ajeno (síntoma "clon de mi skin" reportado en pruebas).
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

        private static bool MeadowCompatibility_CheckNonselfHook(
            Func<Player, bool> orig, Player self)
        {
            return false;
        }

        private static readonly HashSet<string> DefaultSkinDebugLogged = new HashSet<string>();

        public void OnDestroy()
        {
            On.RainWorld.OnModsInit -= OnModsInit;
            On.RainWorld.Update -= RainWorld_Update;
            On.RoomCamera.SpriteLeaser.ctor -= SpriteLeaser_Ctor_Hook;
            customizationHook?.Dispose();
            handleJoinHook?.Dispose();
            handleDisconnectHook?.Dispose();
            meadowCheckHook?.Dispose();
            FancyMenuHookHandler.Dispose();
        }
    }
}
