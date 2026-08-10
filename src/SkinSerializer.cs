using System;
using System.Collections.Generic;
using System.Linq;
using DressMySlugcat;
using Newtonsoft.Json;
using RainMeadow;
using UnityEngine;

namespace DMSxMeadow
{
    public class DMSNetworkTester
    {
        internal static class SkinSerializer
        {
            private static readonly HashSet<string> SentPlayersForCurrentSkin = new HashSet<string>();
            private static string lastSentJsonCustomization = "";
            private static bool lastSentShareSkin = false;

            /// <summary>
            /// Customizaciones recibidas por handshake, indexadas por SteamID del emisor
            /// y luego por slugcat. H-2: la piel del jugador remoto se aplica aquí.
            /// </summary>
            private static readonly Dictionary<string, Dictionary<string, DressMySlugcat.Customization>> ReceivedCustomizations =
                new Dictionary<string, Dictionary<string, DressMySlugcat.Customization>>(StringComparer.Ordinal);

            public static void StoreReceivedCustomization(string steamId, string slugcatName, DressMySlugcat.Customization customization)
            {
                if (string.IsNullOrEmpty(steamId) || string.IsNullOrEmpty(slugcatName) || customization == null) return;

                if (!ReceivedCustomizations.TryGetValue(steamId, out var bySlugcat))
                {
                    bySlugcat = new Dictionary<string, DressMySlugcat.Customization>(StringComparer.Ordinal);
                    ReceivedCustomizations[steamId] = bySlugcat;
                }

                bySlugcat[slugcatName] = customization.Copy();
                Plugin.Logger.LogInfo($"[DMSxMeadow] 💾 Customización de '{slugcatName}' guardada para el jugador {steamId} (caché de memoria).");
            }

            public static DressMySlugcat.Customization GetReceivedCustomization(string steamId, string slugcatName)
            {
                if (string.IsNullOrEmpty(steamId) || string.IsNullOrEmpty(slugcatName)) return null;

                if (ReceivedCustomizations.TryGetValue(steamId, out var bySlugcat) &&
                    bySlugcat.TryGetValue(slugcatName, out var customization))
                {
                    ApplyRemapToCustomization(steamId, customization);
                    return customization;
                }

                return null;
            }

            // H-5: el slug del emisor referencia la skin por su id ORIGINAL (el del handshake);
            // en disco vive renombrada. Se reescribe el id en el momento de la consulta y es
            // idempotente: los ids ya renombrados no vuelven a matchear contra la tabla de
            // remaps (original -> renombrado), así que la instancia almacenada solo se toca una vez.
            private static readonly HashSet<string> RemapAppliedLogKeys = new HashSet<string>(StringComparer.Ordinal);

            private static void ApplyRemapToCustomization(string steamId, DressMySlugcat.Customization customization)
            {
                if (customization?.CustomSprites == null) return;

                bool changed = false;
                foreach (var sprite in customization.CustomSprites)
                {
                    if (sprite == null || string.IsNullOrEmpty(sprite.SpriteSheetID)) continue;

                    string renamedId = SkinRegistration.GetActiveRemap(steamId, sprite.SpriteSheetID);
                    if (renamedId == null) continue;

                    sprite.SpriteSheetID = renamedId;
                    changed = true;
                }

                if (changed && RemapAppliedLogKeys.Add(steamId + "|" + customization.Slugcat))
                {
                    Plugin.Logger.LogInfo($"[DMSxMeadow] 🔄 Customización de {steamId} ({customization.Slugcat}) re-escrita con ids renombrados de caché (H-5).");
                }
            }

            public class MeadowHandshakeDTO
            {
                public string SteamId { get; set; }
                public string Slugcat { get; set; }
                public bool ShareSkin { get; set; }
                public List<string> RequiredSpriteSheetIds { get; set; } = new List<string>();
                public string CustomizationJson { get; set; }
            }

            public class DMSCustomizationDTO
            {
                public string Slugcat { get; set; }
                public TailDTO CustomTail { get; set; }
                public List<SpriteDTO> CustomSprites { get; set; } = new List<SpriteDTO>();

                public class SpriteDTO
                {
                    public string Sprite { get; set; }
                    public string SpriteSheetId { get; set; }
                    public bool Enforce { get; set; }

                    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
                    public string ColorHex { get; set; }
                }

                public class TailDTO
                {
                    public float Roundness { get; set; }
                    public float Wideness { get; set; }
                    public float Length { get; set; }
                    public float Lift { get; set; }

                    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
                    public string ColorHex { get; set; }

                    public bool CustTailShape { get; set; }
                    public bool AsymTail { get; set; }
                    public bool ForbidTailResize { get; set; }
                }

                public static string SerializeLocalCustomization(string slugcatName)
                {
                    var customization = DressMySlugcat.Customization.For(slugcatName, 0);
                    if (customization == null) return null;

                    var dto = new DMSCustomizationDTO
                    {
                        Slugcat = customization.Slugcat,
                        CustomTail = new TailDTO
                        {
                            Roundness = customization.CustomTail.Roundness,
                            Wideness = customization.CustomTail.Wideness,
                            Length = customization.CustomTail.Length,
                            Lift = customization.CustomTail.Lift,
                            ColorHex = customization.CustomTail.ColorHex,
                            CustTailShape = customization.CustomTail.CustTailShape,
                            AsymTail = customization.CustomTail.AsymTail,
                            ForbidTailResize = customization.CustomTail.ForbidTailResize
                        }
                    };

                    if (customization.CustomSprites != null)
                    {
                        foreach (var sprite in customization.CustomSprites)
                        {
                            dto.CustomSprites.Add(new SpriteDTO
                            {
                                Sprite = sprite.Sprite,
                                SpriteSheetId = sprite.SpriteSheetID,
                                Enforce = sprite.Enforce,
                                ColorHex = sprite.ColorHex
                            });
                        }
                    }

                    return JsonConvert.SerializeObject(dto, new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore
                    });
                }

                public static Customization DeserializeToCustomization(string json)
                {
                    var dto = JsonConvert.DeserializeObject<DMSCustomizationDTO>(json);
                    if (dto == null) return null;

                    var customization = new DressMySlugcat.Customization
                    {
                        Slugcat = dto.Slugcat,
                        PlayerNumber = 0
                    };

                    if (dto.CustomTail != null)
                    {
                        customization.CustomTail.Roundness = dto.CustomTail.Roundness;
                        customization.CustomTail.Wideness = dto.CustomTail.Wideness;
                        customization.CustomTail.Length = dto.CustomTail.Length;
                        customization.CustomTail.Lift = dto.CustomTail.Lift;
                        customization.CustomTail.ColorHex = dto.CustomTail.ColorHex;
                        customization.CustomTail.CustTailShape = dto.CustomTail.CustTailShape;
                        customization.CustomTail.AsymTail = dto.CustomTail.AsymTail;
                        customization.CustomTail.ForbidTailResize = dto.CustomTail.ForbidTailResize;
                    }

                    if (dto.CustomSprites != null)
                    {
                        foreach (var spriteDto in dto.CustomSprites)
                        {
                            customization.CustomSprites.Add(new DressMySlugcat.CustomSprite
                            {
                                Sprite = spriteDto.Sprite,
                                SpriteSheetID = spriteDto.SpriteSheetId,
                                Enforce = spriteDto.Enforce,
                                ColorHex = spriteDto.ColorHex,
                            });
                        }
                    }

                    return customization;
                }
            }

            public static void BroadcastHandshake(string slugcatName)
            {
                try
                {
                    if (OnlineManager.lobby == null || !OnlineManager.lobby.isAvailable)
                    {
                        SentPlayersForCurrentSkin.Clear();
                        lastSentJsonCustomization = "";
                        Plugin.Logger.LogWarning("[DMSxMeadow] No se puede emitir el handshake: No hay lobby activa.");
                        return;
                    }

                    var rainWorldGame = RWCustom.Custom.rainWorld?.processManager?.currentMainLoop as RainWorldGame;
                    Player localPlayer = rainWorldGame?.Players?.Select(ap => ap?.realizedCreature as Player).FirstOrDefault(p => p != null && (p.abstractCreature.GetOnlineObject()?.isMine ?? false));
                    if (localPlayer == null) return;

                    string jsonCustomization = DMSCustomizationDTO.SerializeLocalCustomization(slugcatName);
                    if (string.IsNullOrEmpty(jsonCustomization))
                    {
                        Plugin.Logger.LogWarning($"[DMSxMeadow] No se encontró personalización local para '{slugcatName}'.");
                        return;
                    }

                    bool localShareSkin = DMSxMeadowOptions.ShareSkinEnabled;

                    // Re-emitir si cambió la skin O el bit de consentimiento: el handshake
                    // viaja una sola vez por destinatario, así que si el usuario toca la
                    // opción ShareSkin después de que el handshake ya salió, el otro
                    // jugador se quedaría con el bit viejo (bug reportado en pruebas).
                    if (jsonCustomization != lastSentJsonCustomization || localShareSkin != lastSentShareSkin)
                    {
                        lastSentJsonCustomization = jsonCustomization;
                        lastSentShareSkin = localShareSkin;
                        SentPlayersForCurrentSkin.Clear();
                        Plugin.Logger.LogInfo("[DMSxMeadow] Detectado cambio de skin local o de flag de compartir. Reiniciando registro de envíos...");
                    }

                    var dto = JsonConvert.DeserializeObject<DMSCustomizationDTO>(jsonCustomization);
                    var requiredSkins = dto?.CustomSprites?.Select(s => s.SpriteSheetId).Where(id => !string.IsNullOrEmpty(id) && !SkinRegistration.NativeDmsSkins.Contains(id)).Distinct().ToList() ?? new List<string>();

                    var handshake = new MeadowHandshakeDTO
                    {
                        SteamId = GetLocalSteamId(),
                        Slugcat = slugcatName,
                        ShareSkin = localShareSkin,
                        RequiredSpriteSheetIds = requiredSkins,
                        CustomizationJson = jsonCustomization
                    };

                    string payloadJson = JsonConvert.SerializeObject(handshake);
                    Plugin.Logger.LogInfo($"[DMSxMeadow] Transmitiendo Handshake RPC ({payloadJson.Length} bytes)...");

                    SentPlayersForCurrentSkin.RemoveWhere(id => !OnlineManager.players.Select(p => GetPlayerSteamId(p)).ToHashSet().Contains(id));
                    foreach (var onlinePlayer in OnlineManager.players)
                    {
                        if (onlinePlayer.isMe) continue;

                        string targetId = GetPlayerSteamId(onlinePlayer);
                        if (SentPlayersForCurrentSkin.Contains(targetId)) continue;

                        onlinePlayer.InvokeRPC(RPC_ReceiveHandshake, payloadJson);
                        SentPlayersForCurrentSkin.Add(targetId);
                        Plugin.Logger.LogInfo($"[DMSxMeadow] Skin transmitida con éxito a '{targetId}'");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[DMSxMeadow] Error al emitir Handshake: {ex}");
                }
            }

            public static void ForgetPlayer(string steamId)
            {
                if (string.IsNullOrEmpty(steamId)) return;
                bool removed = SentPlayersForCurrentSkin.Remove(steamId);
                if (ReceivedCustomizations.Remove(steamId))
                {
                    Plugin.Logger.LogInfo($"[DMSxMeadow] 🧹 Customizaciones recibidas del jugador '{steamId}' purgadas (abandonó).");
                }
                if (removed)
                {
                    Plugin.Logger.LogInfo($"[DMSxMeadow] 🧹 Destinatario '{steamId}' eliminado del registro de envíos (abandonó).");
                }
            }

            /// <summary>Purga total al terminar la sesión (host se fue / LeaveLobby):
            /// customizaciones recibidas de TODOS los jugadores y registro de envíos.</summary>
            public static void ForgetAllPlayers()
            {
                int count = ReceivedCustomizations.Count;
                ReceivedCustomizations.Clear();
                SentPlayersForCurrentSkin.Clear();
                lastSentJsonCustomization = "";
                lastSentShareSkin = false;

                if (count > 0)
                {
                    Plugin.Logger.LogInfo($"[DMSxMeadow] 🧹 Customizaciones recibidas de {count} jugador(es) purgadas (sesión terminada).");
                }
            }

            [SoftRPCMethod]
            public static void RPC_ReceiveHandshake(string payloadJson)
            {
                try
                {
                    var handshake = JsonConvert.DeserializeObject<MeadowHandshakeDTO>(payloadJson);
                    if (handshake == null)
                    {
                        Plugin.Logger.LogWarning("[DMSxMeadow] Recibido Handshake nulo o no válido.");
                        return;
                    }

                    // No confiamos en el arg OnlinePlayer (viaja como inLobbyId y RainMeadow
                    // lo resuelve con PlayerFromId, que devuelve null en sesiones GOG/LAN —
                    // "Player not found!"). La identidad viaja en el payload y se busca
                    // contra la lista de jugadores local.
                    var sender = ResolvePlayerByIdentity(handshake.SteamId);
                    if (sender == null)
                    {
                        Plugin.Logger.LogWarning($"[DMSxMeadow] Handshake de '{handshake.SteamId}' ignorado: no se encontró al remitente en la lista de jugadores local.");
                        return;
                    }

                    Plugin.Logger.LogInfo($"================ [DMSxMeadow RPC HANDSHAKE] ================");
                    Plugin.Logger.LogInfo($" Emisor SteamID : {handshake.SteamId}");
                    Plugin.Logger.LogInfo($" Emisor Player  : {sender.id}");
                    Plugin.Logger.LogInfo($" Slugcat        : {handshake.Slugcat}");
                    Plugin.Logger.LogInfo($" ShareSkin Bit  : {handshake.ShareSkin}");
                    Plugin.Logger.LogInfo($" Skins usadas   : {(handshake.RequiredSpriteSheetIds.Count > 0 ? string.Join(", ", handshake.RequiredSpriteSheetIds) : "Ninguna (Default)")}");

                    var customization = DMSCustomizationDTO.DeserializeToCustomization(handshake.CustomizationJson);
                    Plugin.Logger.LogInfo($" Customization  : {(customization != null ? "OK (Deserializado correctamente)" : "ERROR")}");

                    string senderSteamId = GetPlayerSteamId(sender);
                    if (customization != null && !string.IsNullOrEmpty(senderSteamId))
                    {
                        StoreReceivedCustomization(senderSteamId, handshake.Slugcat, customization);

                        // Recrear en caliente el slug remoto: aplica la customización
                        // recién recibida sin esperar a que cruce una tubería.
                        Plugin.ScheduleRecreateForSteamId(senderSteamId);
                    }

                    if (handshake.ShareSkin)
                    {
                        // Respetar DefaultMeadowSkins de DMS: si el usuario local la tiene ON
                        // no verá las skins de otros jugadores (DMS les aplica default), así
                        // que pedir los archivos sería trabajo y tráfico inútiles.
                        if (IsDefaultMeadowSkinsEnabled())
                        {
                            Plugin.Logger.LogInfo(" El receptor tiene 'DefaultMeadowSkins' de DMS ACTIVADO: no se solicitarán archivos de skin.");
                        }
                        else
                        {
                            foreach (var skinId in handshake.RequiredSpriteSheetIds)
                            {
                                if (!SkinRegistration.IsValidSkinIdentifier(skinId))
                                {
                                    Plugin.Logger.LogWarning($"[DMSxMeadow] ⛔ Skin '{skinId}' del handshake de {sender?.id} IGNORADA: no cumple ^[a-zA-Z0-9_.-]+$ (H-3).");
                                    continue;
                                }

                                bool localExists = SkinRegistration.IsSkinAlreadyInstalled(skinId);
                                if (!localExists && sender != null)
                                {
                                    localExists = SkinRegistration.HasCachedSkinFrom(GetPlayerSteamId(sender), skinId);
                                }

                                if (localExists)
                                {
                                    Plugin.Logger.LogInfo($" - Skin '{skinId}': Existe localmente (No se pedirá).");
                                }
                                else
                                {
                                    Plugin.Logger.LogInfo($" - Skin '{skinId}': FALTANTE. Solicitando transmisión a {sender?.id}...");

                                    if (sender != null)
                                    {
                                        SkinTransfer.RequestSkinFromPlayer(sender, skinId);
                                    }
                                    else
                                    {
                                        Plugin.Logger.LogError($" - Skin '{skinId}': No se pudo solicitar porque sender (RPCManager.currentAuthor) es nulo.");
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        Plugin.Logger.LogInfo(" El emisor tiene 'ShareSkin' desactivado. Se omitirá la solicitud de archivos.");
                    }

                    Plugin.Logger.LogInfo($"============================================================");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[DMSxMeadow] Error procesando RPC_ReceiveHandshake: {ex}");
                }
            }

            /// <summary>ACK de archivo individual (canal 0, RPC de sesión). Lo manda el
            /// receptor de un CustomPacket de skin en cuanto lo procesa, para que el emisor
            /// (SkinTransfer) sepa que no necesita reenviarlo.</summary>
            [SoftRPCMethod]
            public static void RPC_AckSkinFile(string receiverIdentity, string skinId, int fileIndex)
            {
                try
                {
                    var receiver = ResolvePlayerByIdentity(receiverIdentity);
                    if (receiver == null) return;
                    SkinTransfer.OnFileAcked(receiver, skinId, fileIndex);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[DMSxMeadow] Error al procesar RPC_AckSkinFile para '{skinId}' [{fileIndex}]: {ex}");
                }
            }

            [SoftRPCMethod]
            public static void RPC_RequestSkin(string requesterIdentity, string skinId)
            {
                try
                {
                    var requester = ResolvePlayerByIdentity(requesterIdentity);
                    if (requester == null)
                    {
                        Plugin.Logger.LogWarning($"[DMSxMeadow] RPC_RequestSkin para '{skinId}' ignorado: no se encontró '{requesterIdentity}' en la lista de jugadores local.");
                        return;
                    }

                    if (!SkinRegistration.IsValidSkinIdentifier(skinId))
                    {
                        Plugin.Logger.LogWarning($"[DMSxMeadow] ⛔ RPC_RequestSkin de {requester.id} para '{skinId}' RECHAZADO: skinId no cumple ^[a-zA-Z0-9_.-]+$ (H-3).");
                        return;
                    }

                    if (!DMSxMeadowOptions.ShareSkinEnabled)
                    {
                        Plugin.Logger.LogInfo($"[DMSxMeadow] RPC_RequestSkin de {requester.id} para '{skinId}' RECHAZADO: flag de compartir skin está OFF (Capa 0).");
                        return;
                    }

                    Plugin.Logger.LogInfo($"[DMSxMeadow] El jugador {requester.id} ha solicitado la transmisión de la skin '{skinId}'.");
                    SkinTransfer.SendSkinToPlayer(requester, skinId);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[DMSxMeadow] Error al procesar RPC_RequestSkin para skin '{skinId}': {ex}");
                }
            }

            public static string GetPlayerSteamId(OnlinePlayer player)
            {
                if (player?.id == null) return string.Empty;
                if (player.id is SteamMatchmakingManager.SteamPlayerId steamPlayerId) return steamPlayerId.steamID.m_SteamID.ToString();
                return player.id.ToString();
            }

            /// <summary>Resuelve un jugador de la lista local por su identidad (SteamID, o
            /// id de RainMeadow en sesiones sin Steam). Reemplaza la dependencia del arg
            /// OnlinePlayer de los RPC, que RainMeadow no puede resolver en GOG/LAN.</summary>
            internal static OnlinePlayer ResolvePlayerByIdentity(string identity)
            {
                if (string.IsNullOrEmpty(identity)) return null;
                return OnlineManager.players.FirstOrDefault(p => GetPlayerSteamId(p) == identity);
            }

            /// <summary>SteamID local para el handshake, con fallback para sesiones sin
            /// Steamworks inicializado (p.ej. Minecraft LAN con copia sin Steam): en ese
            /// caso se usa la identidad de RainMeadow del jugador local.</summary>
            private static string GetLocalSteamId()
            {
                try
                {
                    return Steamworks.SteamUser.GetSteamID().m_SteamID.ToString();
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[DMSxMeadow] Steam no disponible ({ex.Message}); usando identidad de RainMeadow para el handshake.");
                    return GetPlayerSteamId(OnlineManager.mePlayer);
                }
            }

            /// <summary>True si el usuario local tiene la opción nativa de DMS
            /// "DefaultMeadowSkins" activada (DMS aplica piel por defecto a jugadores
            /// remotos). En ese caso respetamos la opción y apagamos nuestro flujo de
            /// skins ajenas (no se piden archivos ni se aplican customizaciones recibidas).</summary>
            internal static bool IsDefaultMeadowSkinsEnabled()
            {
                try
                {
                    return DressMySlugcat.Plugin.Options != null
                        && DressMySlugcat.Plugin.Options.DefaultMeadowSkins.Value;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[DMSxMeadow] No se pudo leer DefaultMeadowSkins: {ex.Message}");
                    return false;
                }
            }
        }
    }
}
