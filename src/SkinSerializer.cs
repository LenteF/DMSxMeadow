using System;
using System.Collections.Generic;
using System.Linq;
using DressMySlugcat;
using Newtonsoft.Json;
using RainMeadow;
using UnityEngine;

namespace DMSxMeadow
{
    public class DMSNetworkTester : MonoBehaviour
    {
        public KeyCode TriggerKey = KeyCode.K;

        private void Update()
        {
            if (Input.GetKeyDown(TriggerKey))
            {
                if (Input.GetKeyDown(TriggerKey))
                {
                    Plugin.Logger.LogInfo($"[DMSxMeadow] Tecla '{TriggerKey}' presionada. Iniciando broadcast de prueba...");

                    // Obtenemos el slugcat actual del jugador local o usamos "White" como valor predeterminado
                    string currentSlugcat = "White";
                    if (OnlineManager.mePlayer != null && OnlineManager.lobby != null)
                    {
                        // Si el juego ya instanció al Slugcat en la escena local
                        var rainWorldGame = RWCustom.Custom.rainWorld?.processManager?.currentMainLoop as RainWorldGame;
                        if (rainWorldGame?.FirstRealizedPlayer != null)
                        {
                            currentSlugcat = rainWorldGame.FirstRealizedPlayer.slugcatStats.name.value;
                        }
                    }

                    SkinSerializer.BroadcastHandshake(currentSlugcat);
                }
            }
        }

        internal static class SkinSerializer
        {
            private static readonly HashSet<string> SentPlayersForCurrentSkin = new HashSet<string>();
            private static string lastSentJsonCustomization = "";

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

                    string jsonCustomization = DMSCustomizationDTO.SerializeLocalCustomization(slugcatName);
                    if (string.IsNullOrEmpty(jsonCustomization))
                    {
                        Plugin.Logger.LogWarning($"[DMSxMeadow] No se encontró personalización local para '{slugcatName}'.");
                        return;
                    }

                    if (jsonCustomization != lastSentJsonCustomization)
                    {
                        lastSentJsonCustomization = jsonCustomization;
                        SentPlayersForCurrentSkin.Clear();
                        Plugin.Logger.LogInfo("[DMSxMeadow] Detectado cambio de skin local. Reiniciando registro de envíos...");
                    }

                    var dto = JsonConvert.DeserializeObject<DMSCustomizationDTO>(jsonCustomization);
                    var requiredSkins = dto?.CustomSprites?.Select(s => s.SpriteSheetId).Where(id => !string.IsNullOrEmpty(id) && !SkinRegistration.NativeDmsSkins.Contains(id)).Distinct().ToList() ?? new List<string>();
                    bool localShareSkin = true;

                    var handshake = new MeadowHandshakeDTO
                    {
                        SteamId = Steamworks.SteamUser.GetSteamID().m_SteamID.ToString(),
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

                    Plugin.Logger.LogInfo($"================ [DMSxMeadow RPC HANDSHAKE] ================");
                    Plugin.Logger.LogInfo($" Emisor SteamID : {handshake.SteamId}");
                    Plugin.Logger.LogInfo($" Slugcat        : {handshake.Slugcat}");
                    Plugin.Logger.LogInfo($" ShareSkin Bit  : {handshake.ShareSkin}");
                    Plugin.Logger.LogInfo($" Skins usadas   : {(handshake.RequiredSpriteSheetIds.Count > 0 ? string.Join(", ", handshake.RequiredSpriteSheetIds) : "Ninguna (Default)")}");

                    var customization = DMSCustomizationDTO.DeserializeToCustomization(handshake.CustomizationJson);
                    Plugin.Logger.LogInfo($" Customization  : {(customization != null ? "OK (Deserializado correctamente)" : "ERROR")}");

                    if (handshake.ShareSkin)
                    {
                        foreach (var skinId in handshake.RequiredSpriteSheetIds)
                        {
                            bool localExists = SkinRegistration.IsSkinAlreadyInstalled(skinId);
                            Plugin.Logger.LogInfo($" - Skin '{skinId}': {(localExists ? "Existe localmente (No se pedirá)" : "FALTANTE (Se solicitará vía CustomPacket)")}");
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
        }

        public static string GetPlayerSteamId(OnlinePlayer player)
        {
            if (player?.id == null) return string.Empty;
            if (player.id is SteamMatchmakingManager.SteamPlayerId steamPlayerId) return steamPlayerId.steamID.m_SteamID.ToString();
            return player.id.ToString();
        }
    }
}
