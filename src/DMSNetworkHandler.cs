using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Newtonsoft.Json;
using RainMeadow;

namespace DMSxMeadow
{
    internal static class DMSNetworkHandler
    {
        private static ManualLogSource Logger => Plugin.Logger;
        private static readonly HashSet<string> SentPlayersForCurrentSkin = new HashSet<string>();
        private static string lastSentPayload = "";

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
                            ColorHex = sprite.ColorHex,
                        });
                    }
                }

                return JsonConvert.SerializeObject(dto, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });
            }

            public static DressMySlugcat.Customization DeserializeToCustomization(string json)
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

        public static void BroadcastMyCustomization(string slugcatName)
        {
            try
            {
                if (OnlineManager.lobby == null || !OnlineManager.lobby.isAvailable)
                {
                    ResetNetworkState();
                    Logger.LogWarning("[DMSxMeadow] No se puede emitir la skin: No hay lobby activo.");
                    return;
                }

                string jsonPayload = DMSCustomizationDTO.SerializeLocalCustomization(slugcatName);
                if (string.IsNullOrEmpty(jsonPayload))
                {
                    Logger.LogWarning("[DMSxMeadow] No se encontró personalización local en el perfil 0 de DMS");
                    return;
                }

                if (jsonPayload != lastSentPayload)
                {
                    lastSentPayload = jsonPayload;
                    SentPlayersForCurrentSkin.Clear();
                    Logger.LogInfo("[DMSxMeadow] Detectado cambio de skin local. Reiniciando registro de envíos...");
                }

                Logger.LogInfo($"[DMSxMeadow] Transmitiendo skin local vía RPC ({jsonPayload.Length} bytes)...");

                string myId = OnlineManager.mePlayer.id.ToString();

                var activePlayerIds = OnlineManager.players.Select(p => p.id.ToString()).ToHashSet();
                SentPlayersForCurrentSkin.RemoveWhere(id => !activePlayerIds.Contains(id));

                foreach (var onlinePlayer in OnlineManager.players)
                {
                    if (onlinePlayer.isMe) continue;

                    string targetId = onlinePlayer.id.ToString();
                    if (SentPlayersForCurrentSkin.Contains(targetId)) continue;

                    onlinePlayer.InvokeRPC(RPC_ReceiveCustomization, myId, jsonPayload);
                    SentPlayersForCurrentSkin.Add(targetId);
                    Logger.LogInfo($"[DMSxMeadow] Skin transmitida con éxito a '{targetId}'");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[DMSxMeadow] Error al transmitir la skin local: {ex}");
            }
        }

        [RPCMethod]
        public static void RPC_ReceiveCustomization(string senderId, string jsonPayload)
        {
            try
            {
                Logger.LogInfo($"[DMSxMeadow] Recibida skin RPC del jugador: {senderId}");

                var customization = DMSCustomizationDTO.DeserializeToCustomization(jsonPayload);
                if (customization == null) 
                {
                    Logger.LogWarning($"[DMSxMeadow] Falló la deserialización del paquete de {senderId}");
                    return;
                }

                string slugcatName = customization.Slugcat;
                if (string.IsNullOrEmpty(slugcatName))
                {
                    Logger.LogWarning($"[DMSxMeadow] La customización recibida de {senderId} no especifica un Slugcat.");
                }

                int displayNumber = MeadowProfileManager.GetProfileBySteamID(senderId);

                if (displayNumber != -1)
                {
                    int internalNum = MeadowProfileManager.GetInternalProfile(displayNumber);

                    if (MeadowProfileManager.Database.Profiles.TryGetValue(internalNum, out var existingProfile))
                    {
                        if (!existingProfile.IsCache)
                        {
                            Logger.LogInfo($"[DMSxMeadow] El jugador '{senderId}' utiliza el Perfil Manual {displayNumber}. Se ignora la actualización por red.");
                            return;
                        }
                    }
                }

                if (displayNumber == -1)
                {
                    displayNumber = GetNextAvailableCacheSlot();
                    MeadowProfileManager.SetSteamID(displayNumber, senderId);
                    Logger.LogInfo($"[DMSxMeadow] Asignado Slot Caché {displayNumber} a '{senderId}'");
                }

                int internalNumber = MeadowProfileManager.GetInternalProfile(displayNumber);

                if (!MeadowProfileManager.Database.Profiles.TryGetValue(internalNumber, out var profileData))
                {
                    profileData = new MeadowProfileData
                    {
                        InternalProfileNumber = internalNumber,
                        IsCache = true
                    };
                    MeadowProfileManager.Database.Profiles[internalNumber] = profileData;
                }

                profileData.CustomizationsBySlugcat ??= new Dictionary<string, DressMySlugcat.Customization>();
                profileData.CustomizationsBySlugcat[slugcatName] = customization;
                profileData.LastUpdated = DateTime.Now;
                profileData.IsCache = true;

                MeadowProfileManager.Save();
                Logger.LogInfo($"[DMSxMeadow] Skin de {senderId} guardada con éxito en Slot {displayNumber} (Internal: {internalNumber})");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[DMSxMeadow] Error al procesar RPC_ReceiveCustomization de {senderId}: {ex}");
            }
        }

        private static int GetNextAvailableCacheSlot()
        {
            int candidateSlot = 10;

            while (true)
            {
                int internalNum = MeadowProfileManager.GetInternalProfile(candidateSlot);
                bool isOccupied = MeadowProfileManager.Database.Profiles.ContainsKey(internalNum);
                if (!isOccupied)
                {
                    return candidateSlot;
                }
                candidateSlot++;
            }
        }

        public static void ResetNetworkState()
        {
            SentPlayersForCurrentSkin.Clear();
            lastSentPayload = "";
        }
    }
}
