using System;
using System.Collections.Generic;
using System.Linq;
using DressMySlugcat;
using Newtonsoft.Json;
using RainMeadow;
using UnityEngine;

namespace DMSxMeadow
{
    internal static class SkinSerializer
    {
        private static readonly HashSet<string> SentPlayersForCurrentSkin = new HashSet<string>();
        private static string lastSentJsonCustomization = "";
        private static bool lastSentShareSkin = false;

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

        private static void ApplyRemapToCustomization(string steamId, DressMySlugcat.Customization customization)
        {
            if (customization?.CustomSprites == null) return;

            foreach (var sprite in customization.CustomSprites)
            {
                if (sprite == null || string.IsNullOrEmpty(sprite.SpriteSheetID)) continue;

                string renamedId = SkinRegistration.GetActiveRemap(steamId, sprite.SpriteSheetID);
                if (renamedId == null) continue;

                sprite.SpriteSheetID = renamedId;
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
                var customization = MeadowProfileManager.GetCustomizationBySteamID(GetLocalSteamId(), slugcatName);
                if (customization == null)
                {
                    customization = DressMySlugcat.Customization.For(slugcatName, 0);
                    if (customization == null) return null;
                }

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
                    return;
                }

                string jsonCustomization = DMSCustomizationDTO.SerializeLocalCustomization(slugcatName);
                if (string.IsNullOrEmpty(jsonCustomization))
                {
                    return;
                }

                bool localShareSkin = DMSxMeadowOptions.ShareSkinEnabled;

                if (jsonCustomization != lastSentJsonCustomization || localShareSkin != lastSentShareSkin)
                {
                    lastSentJsonCustomization = jsonCustomization;
                    lastSentShareSkin = localShareSkin;
                    SentPlayersForCurrentSkin.Clear();
                }

                string payloadJson = BuildHandshakePayload(slugcatName, jsonCustomization, localShareSkin);
                if (string.IsNullOrEmpty(payloadJson)) return;

                SentPlayersForCurrentSkin.RemoveWhere(id => !OnlineManager.players.Select(p => GetPlayerSteamId(p)).ToHashSet().Contains(id));

                foreach (var onlinePlayer in OnlineManager.players)
                {
                    if (onlinePlayer.isMe) continue;

                    string targetId = GetPlayerSteamId(onlinePlayer);
                    if (SentPlayersForCurrentSkin.Contains(targetId)) continue;

                    onlinePlayer.InvokeRPC(RPC_ReceiveHandshake, payloadJson);
                    SentPlayersForCurrentSkin.Add(targetId);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error al emitir Handshake: {ex}");
            }
        }

        private static string BuildHandshakePayload(string slugcatName, string jsonCustomization, bool localShareSkin)
        {
            if (jsonCustomization == null)
            {
                jsonCustomization = DMSCustomizationDTO.SerializeLocalCustomization(slugcatName);
            }
            if (string.IsNullOrEmpty(jsonCustomization)) return null;

            var dto = JsonConvert.DeserializeObject<DMSCustomizationDTO>(jsonCustomization);
            var requiredSkins = dto?.CustomSprites?.Select(s => s.SpriteSheetId)
                .Where(id => !string.IsNullOrEmpty(id) && !SkinRegistration.NativeDmsSkins.Contains(id))
                .Distinct().ToList() ?? new List<string>();

            var handshake = new MeadowHandshakeDTO
            {
                SteamId = GetLocalSteamId(),
                Slugcat = slugcatName,
                ShareSkin = localShareSkin,
                RequiredSpriteSheetIds = requiredSkins,
                CustomizationJson = jsonCustomization
            };

            return JsonConvert.SerializeObject(handshake);
        }

        public static void SendHandshakeTo(OnlinePlayer target, string reason)
        {
            if (target == null || target.isMe) return;
            if (OnlineManager.lobby == null || !OnlineManager.lobby.isAvailable) return;

            try
            {
                var (slugcatName, _) = Plugin.ResolveLocalSlugcatName();
                if (string.IsNullOrEmpty(slugcatName)) return;

                string payloadJson = BuildHandshakePayload(slugcatName, null, DMSxMeadowOptions.ShareSkinEnabled);
                if (string.IsNullOrEmpty(payloadJson)) return;

                target.InvokeRPC(RPC_ReceiveHandshake, payloadJson);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error reenviando handshake a {target.id}: {ex.Message}");
            }
        }

        public static void RequestHandshakeFrom(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            if (OnlineManager.lobby == null || !OnlineManager.lobby.isAvailable) return;
            if (SkinBanManager.IsBanned(steamId)) return;

            var target = ResolvePlayerByIdentity(steamId);
            if (target == null || target.isMe)
            {
                return;
            }

            target.InvokeRPC(RPC_RequestHandshake, GetLocalSteamId());
            Plugin.Logger.LogInfo($"🔄 Handshake re-solicitado a {target.id} tras desbaneo: si su ShareSkin está ON, se descargará su skin.");
        }

        [SoftRPCMethod]
        public static void RPC_RequestHandshake(string requesterIdentity)
        {
            try
            {
                var requester = ResolvePlayerByIdentity(requesterIdentity);
                if (requester == null)
                {
                    return;
                }
                SendHandshakeTo(requester, "resolicitud recibida");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error al procesar RPC_RequestHandshake: {ex}");
            }
        }

        // --- Re-solicitud de handshakes perdidos ---
        private class HandshakeRequestState
        {
            public float LastTime;
            public int Attempts;
            public float RearmInterval;
        }

        private static readonly Dictionary<string, HandshakeRequestState> _handshakeRequests =
            new Dictionary<string, HandshakeRequestState>(StringComparer.Ordinal);

        private const float HandshakeRequestCooldownSeconds = 10f;
        private const int MaxHandshakeRequests = 6;
        // Re-arm: al agotar los reintentos no se rinde para siempre; tras un silencio largo (con jitter) se reintenta.
        private const float HandshakeRequestRearmSeconds = 105f;
        private const float HandshakeRequestRearmJitter = 20f;

        public static void PollMissingHandshakes()
        {
            if (OnlineManager.lobby == null || !OnlineManager.lobby.isAvailable) return;
            if (IsDefaultMeadowSkinsEnabled()) return;

            foreach (var onlinePlayer in new List<OnlinePlayer>(OnlineManager.players))
            {
                try
                {
                    if (onlinePlayer == null || onlinePlayer.isMe) continue;

                    string steamId = GetPlayerSteamId(onlinePlayer);
                    if (string.IsNullOrEmpty(steamId)) continue;
                    if (ReceivedCustomizations.ContainsKey(steamId)) continue;
                    if (SkinBanManager.IsBanned(steamId)) continue;
                    if (!IsSteamFriendAllowed(steamId)) continue;

                    if (!_handshakeRequests.TryGetValue(steamId, out var state))
                    {
                        _handshakeRequests[steamId] = new HandshakeRequestState
                        {
                            LastTime = Time.time,
                            RearmInterval = HandshakeRequestRearmSeconds + UnityEngine.Random.Range(-HandshakeRequestRearmJitter, HandshakeRequestRearmJitter)
                        };
                        continue;
                    }

                    float elapsedSinceLast = Time.time - state.LastTime;

                    if (state.Attempts >= MaxHandshakeRequests)
                    {
                        if (elapsedSinceLast < state.RearmInterval) continue;
                        state.Attempts = 0;
                        state.RearmInterval = HandshakeRequestRearmSeconds + UnityEngine.Random.Range(-HandshakeRequestRearmJitter, HandshakeRequestRearmJitter);
                    }
                    else
                    {
                        if (elapsedSinceLast < HandshakeRequestCooldownSeconds) continue;
                    }

                    state.LastTime = Time.time;
                    state.Attempts++;

                    onlinePlayer.InvokeRPC(RPC_RequestHandshake, GetLocalSteamId());
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"Error en poll de handshakes: {ex.Message}");
                }
            }
        }

        public static void ForgetPlayer(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            _handshakeRequests.Remove(steamId);
            SentPlayersForCurrentSkin.Remove(steamId);
            ReceivedCustomizations.Remove(steamId);
        }

        public static void ForgetAllPlayers()
        {
            ReceivedCustomizations.Clear();
            SentPlayersForCurrentSkin.Clear();
            lastSentJsonCustomization = "";
            lastSentShareSkin = false;
            _handshakeRequests.Clear();
        }

        [SoftRPCMethod]
        public static void RPC_ReceiveHandshake(string payloadJson)
        {
            try
            {
                var handshake = JsonConvert.DeserializeObject<MeadowHandshakeDTO>(payloadJson);
                if (handshake == null)
                {
                    Plugin.Logger.LogWarning("Recibido Handshake nulo o no válido.");
                    return;
                }

                var sender = ResolvePlayerByIdentity(handshake.SteamId);
                if (sender == null)
                {
                    return;
                }

                string senderSteamId = GetPlayerSteamId(sender);
                if (SkinBanManager.IsBanned(senderSteamId))
                {
                    return;
                }

                if (!IsSteamFriendAllowed(senderSteamId))
                {
                    return;
                }

                var customization = DMSCustomizationDTO.DeserializeToCustomization(handshake.CustomizationJson);

                if (customization != null && !string.IsNullOrEmpty(senderSteamId))
                {
                    StoreReceivedCustomization(senderSteamId, handshake.Slugcat, customization);

                    Plugin.ScheduleRecreateForSteamId(senderSteamId);

                    if (ReceivedCustomizations.TryGetValue(senderSteamId, out var bySlugcat))
                    {
                        var staleKeys = bySlugcat.Keys.Where(k => k != handshake.Slugcat).ToList();
                        foreach (string staleKey in staleKeys) bySlugcat.Remove(staleKey);
                    }
                }

                SkinTransfer.ForgetSenderState(sender);

                if (handshake.ShareSkin)
                {
                    if (MeadowProfileManager.GetCustomizationBySteamID(senderSteamId, handshake.Slugcat) == null)
                    {
                        foreach (var skinId in handshake.RequiredSpriteSheetIds)
                        {
                            if (!SkinRegistration.IsValidSkinIdentifier(skinId))
                            {
                                Plugin.Logger.LogWarning($"⛔ Skin '{skinId}' del handshake de {sender?.id} IGNORADA: no cumple ^[a-zA-Z0-9_.-]+$ (H-3).");
                                continue;
                            }

                            if (SkinRegistration.HasSkinInMemory(senderSteamId, skinId))
                            {
                                continue;
                            }

                            SkinTransfer.RequestSkinFromPlayer(sender, skinId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error procesando RPC_ReceiveHandshake: {ex}");
            }
        }

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
                Plugin.Logger.LogError($"Error al procesar RPC_AckSkinFile para '{skinId}' [{fileIndex}]: {ex}");
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
                    return;
                }

                if (!SkinRegistration.IsValidSkinIdentifier(skinId))
                {
                    Plugin.Logger.LogWarning($"⛔ RPC_RequestSkin de {requester.id} para '{skinId}' RECHAZADO: skinId no cumple ^[a-zA-Z0-9_.-]+$ (H-3).");
                    return;
                }

                if (!DMSxMeadowOptions.ShareSkinEnabled)
                {
                    return;
                }

                SkinTransfer.SendSkinToPlayer(requester, skinId);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error al procesar RPC_RequestSkin para skin '{skinId}': {ex}");
            }
        }

        public static string GetPlayerSteamId(OnlinePlayer player)
        {
            if (player?.id == null) return string.Empty;
            if (player.id is SteamMatchmakingManager.SteamPlayerId steamPlayerId) return steamPlayerId.steamID.m_SteamID.ToString();
            return player.id.ToString();
        }

        internal static OnlinePlayer ResolvePlayerByIdentity(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return null;
            return OnlineManager.players.FirstOrDefault(p => GetPlayerSteamId(p) == identity);
        }

        internal static string GetLocalSteamId()
        {
            try
            {
                return Steamworks.SteamUser.GetSteamID().m_SteamID.ToString();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"Steam no disponible ({ex.Message}); usando identidad de RainMeadow para el handshake.");
                return GetPlayerSteamId(OnlineManager.mePlayer);
            }
        }

        internal static bool IsDefaultMeadowSkinsEnabled()
        {
            try
            {
                return DressMySlugcat.Plugin.Options != null
                    && DressMySlugcat.Plugin.Options.DefaultMeadowSkins.Value;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"No se pudo leer DefaultMeadowSkins: {ex.Message}");
                return false;
            }
        }

        internal static bool IsSteamFriendAllowed(string steamId)
        {
            if (!DMSxMeadowOptions.FriendsOnlyEnabled) return true;

            bool steamUp;
            try { steamUp = Steamworks.SteamUser.BLoggedOn(); }
            catch { steamUp = false; }
            if (!steamUp) return true;

            if (string.IsNullOrEmpty(steamId) || !ulong.TryParse(steamId, out ulong rawSteamId))
            {
                return false;
            }

            try
            {
                return Steamworks.SteamFriends.HasFriend(
                    new Steamworks.CSteamID(rawSteamId),
                    Steamworks.EFriendFlags.k_EFriendFlagImmediate);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"Error comprobando amistad de '{steamId}': {ex.Message}");
                return false;
            }
        }
    }
}