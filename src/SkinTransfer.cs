using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RainMeadow;
using UnityEngine;

namespace DMSxMeadow
{
    public class SkinTransfer : IUseCustomPackets
    {
        public static readonly string PacketKey = "DMS_SkinData";
        private const int MaxCustomPacketBytes = 32768;

        // --- Throttling de envío ---
        private const float SendIntervalSeconds = 0.05f;
        private static float _nextSendAllowedTime = 0f;

        // --- Reintento por archivo ---
        private const float FileAckTimeoutSeconds = 4f;
        private const int MaxFileAttempts = 6;

        // --- Reintento de la petición inicial ---
        private const float RequestTimeoutSeconds = 4f;
        private const int MaxRequestAttempts = 6;
        // Re-arm: al agotar los reintentos no se abandona; tras un silencio largo (con jitter) se reintenta.
        private const float RequestRearmSeconds = 60f;
        private const float RequestRearmJitter = 20f;

        private static SkinTransfer _instance;

        public bool Active => true;

        public static void Initialize()
        {
            if (_instance == null)
            {
                _instance = new SkinTransfer();
                CustomManager.Subscribe(PacketKey, _instance);
                Plugin.Logger.LogDebug($"[DMSxMeadow] SkinTransfer suscrito con éxito a la clave de paquetes '{PacketKey}'.");
            }
        }

        // ===================================================================
        // SOLICITUD
        // ===================================================================

        private class PendingRequest
        {
            public OnlinePlayer Target;
            public string SkinId;
            public float LastRequestTime;
            public int Attempts;
            public bool AnyFileReceived;
            public float RearmInterval;
        }

        private static readonly Dictionary<string, PendingRequest> PendingRequests =
            new Dictionary<string, PendingRequest>(StringComparer.Ordinal);

        private static string RequestKey(string targetUniqueId, string skinId) => $"{targetUniqueId}_{skinId}";

        public static void RequestSkinFromPlayer(OnlinePlayer targetPlayer, string skinId)
        {
            if (targetPlayer == null || string.IsNullOrEmpty(skinId)) return;

            string key = RequestKey(targetPlayer.GetUniqueID(), skinId);
            if (!PendingRequests.TryGetValue(key, out var pending))
            {
                pending = new PendingRequest
                {
                    Target = targetPlayer,
                    SkinId = skinId,
                    RearmInterval = RequestRearmSeconds + UnityEngine.Random.Range(-RequestRearmJitter, RequestRearmJitter)
                };
                PendingRequests[key] = pending;
            }

            pending.Attempts++;
            pending.LastRequestTime = Time.time;
            pending.AnyFileReceived = false;

            Plugin.Logger.LogDebug($"[DMSxMeadow] Solicitando skin '{skinId}' al jugador {targetPlayer.id}... (intento {pending.Attempts}/{MaxRequestAttempts})");
            targetPlayer.InvokeRPC(DMSNetworkTester.SkinSerializer.RPC_RequestSkin, DMSNetworkTester.SkinSerializer.GetPlayerSteamId(OnlineManager.mePlayer), skinId);
        }

        public static void RetryPendingRequests()
        {
            if (PendingRequests.Count == 0) return;

            var toRemove = new List<string>();
            var toRetry = new List<PendingRequest>();

            foreach (var kvp in PendingRequests)
            {
                var pending = kvp.Value;
                if (pending.AnyFileReceived) { toRemove.Add(kvp.Key); continue; }

                if (Time.time - pending.LastRequestTime < RequestTimeoutSeconds) continue;

                if (pending.Attempts >= MaxRequestAttempts)
                {
                    if (pending.Target == null) { toRemove.Add(kvp.Key); continue; }
                    if (Time.time - pending.LastRequestTime < pending.RearmInterval) continue;

                    pending.Attempts = 0;
                    pending.RearmInterval = RequestRearmSeconds + UnityEngine.Random.Range(-RequestRearmJitter, RequestRearmJitter);
                    Plugin.Logger.LogDebug($"[DMSxMeadow] 🔄 Re-arm: reintentando '{pending.SkinId}' hacia {pending.Target?.id} (se había agotado el límite de reintentos).");
                }

                toRetry.Add(pending);
            }

            foreach (string key in toRemove) PendingRequests.Remove(key);

            foreach (var pending in toRetry)
            {
                if (pending.Target == null) continue;
                RequestSkinFromPlayer(pending.Target, pending.SkinId);
            }
        }

        private static void MarkRequestSatisfied(OnlinePlayer sender, string skinId)
        {
            string key = RequestKey(sender.GetUniqueID(), skinId);
            if (PendingRequests.TryGetValue(key, out var pending))
            {
                pending.AnyFileReceived = true;
            }
        }

        // ===================================================================
        // ENVÍO
        // ===================================================================

        private class OutgoingFile
        {
            public OnlinePlayer Target;
            public string SkinId;
            public string FileName;
            public int FileIndex;
            public int TotalFiles;
            public byte[] PacketBytes;
            public float LastSentTime;
            public int Attempts;
        }

        private static readonly Queue<OutgoingFile> OutgoingQueue = new Queue<OutgoingFile>();

        private static readonly Dictionary<string, OutgoingFile> InFlightFiles =
            new Dictionary<string, OutgoingFile>(StringComparer.Ordinal);

        private static readonly Dictionary<string, int> TransfersRemaining =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static readonly Dictionary<string, int> DroppedFiles =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static string FileKey(string targetUniqueId, string skinId, int fileIndex) =>
            $"{targetUniqueId}_{skinId}_{fileIndex}";

        public static void SendSkinToPlayer(OnlinePlayer requester, string skinId)
        {
            if (requester == null || string.IsNullOrEmpty(skinId)) return;

            string transferKey = RequestKey(requester.GetUniqueID(), skinId);
            if (TransfersRemaining.ContainsKey(transferKey))
            {
                Plugin.Logger.LogDebug($"[DMSxMeadow] Ya hay una transferencia de '{skinId}' en curso hacia {requester.id}; se ignora la petición duplicada (probablemente un reintento del solicitante).");
                return;
            }

            var fileData = SkinRegistration.ExportEquippedSkinToDTO(skinId);
            if (fileData == null || fileData.Count == 0)
            {
                Plugin.Logger.LogError($"[DMSxMeadow] No se pudieron empaquetar los archivos de la skin '{skinId}' para enviar a {requester.id}.");
                return;
            }

            var sendable = fileData.Where(kvp => kvp.Value.Length <= MaxCustomPacketBytes).ToList();
            int omitted = fileData.Count - sendable.Count;
            if (omitted > 0)
            {
                Plugin.Logger.LogWarning($"[DMSxMeadow] Se omiten {omitted} archivos de '{skinId}' por exceder {MaxCustomPacketBytes} B (no caben en un CustomPacket).");
            }

            int total = sendable.Count;
            if (total == 0) return;

            Plugin.Logger.LogDebug($"[DMSxMeadow] Encolando {total} archivos de la skin '{skinId}' hacia {requester.id} (envío throttled con ACK)...");

            TransfersRemaining[transferKey] = total;

            int index = 0;
            foreach (var kvp in sendable)
            {
                byte[] packetBytes = BuildSkinPacket(skinId, kvp.Key, kvp.Value, index, total);
                OutgoingQueue.Enqueue(new OutgoingFile
                {
                    Target = requester,
                    SkinId = skinId,
                    FileName = kvp.Key,
                    FileIndex = index,
                    TotalFiles = total,
                    PacketBytes = packetBytes,
                    Attempts = 0
                });
                index++;
            }
        }

        private static byte[] BuildSkinPacket(string skinId, string fileName, byte[] rawData, int fileIndex, int totalFiles)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(skinId);
                writer.Write(fileName);
                writer.Write(fileIndex);
                writer.Write(totalFiles);
                writer.Write(rawData.Length);
                writer.Write(rawData);

                return ms.ToArray();
            }
        }

        public static void UpdatePendingTransfers()
        {
            if (Time.time < _nextSendAllowedTime) return;

            if (OutgoingQueue.Count > 0)
            {
                var file = OutgoingQueue.Dequeue();
                SendOneFile(file);
                return;
            }

            OutgoingFile toRetry = null;
            foreach (var kvp in InFlightFiles)
            {
                var f = kvp.Value;
                if (Time.time - f.LastSentTime < FileAckTimeoutSeconds) continue;

                if (f.Attempts >= MaxFileAttempts)
                {
                    continue;
                }

                toRetry = f;
                break;
            }

            if (toRetry != null)
            {
                Plugin.Logger.LogDebug($"[DMSxMeadow] ⏱️ Sin ACK para '{toRetry.FileName}' ({toRetry.SkinId}) hacia {toRetry.Target?.id}. Reintentando (intento {toRetry.Attempts + 1}/{MaxFileAttempts})...");
                SendOneFile(toRetry);
                return;
            }

            var exhausted = InFlightFiles.Where(kvp => kvp.Value.Attempts >= MaxFileAttempts).ToList();
            foreach (var kvp in exhausted)
            {
                Plugin.Logger.LogDebug($"[DMSxMeadow] ❌ '{kvp.Value.FileName}' de la skin '{kvp.Value.SkinId}' hacia {kvp.Value.Target?.id} no se pudo confirmar tras {kvp.Value.Attempts} intentos. Se abandona ese archivo (el re-arm reintentará la skin).");
                string droppedKey = RequestKey(kvp.Value.Target.GetUniqueID(), kvp.Value.SkinId);
                DroppedFiles[droppedKey] = DroppedFiles.TryGetValue(droppedKey, out int drops) ? drops + 1 : 1;
                CompleteOneFile(kvp.Key, kvp.Value);
            }
        }

        private static void SendOneFile(OutgoingFile file)
        {
            if (file.Target == null) return;

            file.Attempts++;
            file.LastSentTime = Time.time;

            var packet = new CustomPacket(PacketKey, file.PacketBytes, (ushort)file.PacketBytes.Length);
            OnlineManager.SendCustomData(file.Target, packet, NetIO.SendType.Reliable);

            InFlightFiles[FileKey(file.Target.GetUniqueID(), file.SkinId, file.FileIndex)] = file;
            _nextSendAllowedTime = Time.time + SendIntervalSeconds;
        }

        public static void OnFileAcked(OnlinePlayer fromPlayer, string skinId, int fileIndex)
        {
            if (fromPlayer == null) return;
            string key = FileKey(fromPlayer.GetUniqueID(), skinId, fileIndex);
            if (InFlightFiles.TryGetValue(key, out var file))
            {
                CompleteOneFile(key, file);
            }
        }

        private static void CompleteOneFile(string fileKey, OutgoingFile file)
        {
            InFlightFiles.Remove(fileKey);

            string transferKey = RequestKey(file.Target.GetUniqueID(), file.SkinId);
            if (TransfersRemaining.TryGetValue(transferKey, out int remaining))
            {
                remaining--;
                if (remaining <= 0)
                {
                    TransfersRemaining.Remove(transferKey);
                    if (DroppedFiles.TryGetValue(transferKey, out int drops))
                    {
                        DroppedFiles.Remove(transferKey);
                        Plugin.Logger.LogDebug($"[DMSxMeadow] 🚨 Transferencia de '{file.SkinId}' hacia {file.Target?.id} INCOMPLETA: {drops} archivo(s) abandonados tras agotar reintentos. El receptor no podrá recomponer la skin (RNF-3: queda con la piel por defecto hasta un re-arm).");
                    }
                    else
                    {
                        Plugin.Logger.LogDebug($"[DMSxMeadow] ✅ Transferencia de '{file.SkinId}' hacia {file.Target?.id} finalizada (todos los archivos confirmados).");
                    }
                }
                else
                {
                    TransfersRemaining[transferKey] = remaining;
                }
            }
        }

        // ===================================================================
        // RECEPCIÓN
        // ===================================================================

        public void ProcessPacket(OnlinePlayer fromPlayer, CustomPacket packet)
        {
            if (packet == null || packet.data == null || packet.data.Length == 0) return;

            try
            {
                using (var ms = new MemoryStream(packet.data))
                using (var reader = new BinaryReader(ms))
                {
                    string skinId = reader.ReadString();
                    string fileName = reader.ReadString();
                    int fileIndex = reader.ReadInt32();
                    int totalFiles = reader.ReadInt32();
                    int dataLength = reader.ReadInt32();
                    byte[] fileBytes = reader.ReadBytes(dataLength);

                    if (!SkinRegistration.IsValidSkinIdentifier(skinId) || !SkinRegistration.IsValidSkinIdentifier(fileName))
                    {
                        Plugin.Logger.LogWarning($"[DMSxMeadow] ⛔ CustomPacket de skin de {fromPlayer?.id} RECHAZADO: skinId='{skinId}' fileName='{fileName}' no cumplen ^[a-zA-Z0-9_.-]+$.");
                        return;
                    }

                    string bannedIdentity = DMSNetworkTester.SkinSerializer.GetPlayerSteamId(fromPlayer);
                    if (SkinBanManager.IsBanned(bannedIdentity))
                    {
                        Plugin.Logger.LogDebug($"[DMSxMeadow] ⛔ CustomPacket de skin de {fromPlayer?.id} DESCARTADO: jugador baneado localmente.");
                        return;
                    }

                    if (!DMSNetworkTester.SkinSerializer.IsSteamFriendAllowed(bannedIdentity))
                    {
                        Plugin.Logger.LogDebug($"[DMSxMeadow] ⛔ CustomPacket de skin de {fromPlayer?.id} DESCARTADO: 'solo amigos' ON y el emisor no es amigo de Steam.");
                        return;
                    }

                    OnChunkReceived(fromPlayer, skinId, fileName, fileBytes, fileIndex, totalFiles);

                    fromPlayer.InvokeRPC(DMSNetworkTester.SkinSerializer.RPC_AckSkinFile, DMSNetworkTester.SkinSerializer.GetPlayerSteamId(OnlineManager.mePlayer), skinId, fileIndex);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DMSxMeadow] Error al procesar CustomPacket de skin de {fromPlayer?.id}: {ex}");
            }
        }

        private static readonly Dictionary<string, Dictionary<string, byte[]>> IncomingTransfers = new Dictionary<string, Dictionary<string, byte[]>>();

        private static readonly HashSet<string> AbortedIncomingTransfers = new HashSet<string>(StringComparer.Ordinal);

        private static void AbortIncomingTransfer(string transferKey)
        {
            IncomingTransfers.Remove(transferKey);
            AbortedIncomingTransfers.Add(transferKey);
            SkinPartGuard.ForgetTransfer(transferKey);
            Plugin.Logger.LogWarning($"[DMSxMeadow] ⛔ Transferencia {transferKey} ABORTADA (rechazo 3a estricto): la skin no se registra y el jugador queda con la skin por defecto.");
        }

        public static void ForgetPlayer(OnlinePlayer player)
        {
            if (player == null) return;
            string uid = player.GetUniqueID();
            string prefix = $"{uid}_";

            var incompleteKeys = IncomingTransfers.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            foreach (string key in incompleteKeys)
            {
                IncomingTransfers.Remove(key);
            }

            if (incompleteKeys.Count > 0)
            {
                Plugin.Logger.LogDebug($"[DMSxMeadow] 🧹 Descartadas {incompleteKeys.Count} transferencia(s) entrante(s) incompleta(s) de {player.id} (jugador salió).");
            }

            var abortedKeys = AbortedIncomingTransfers.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in abortedKeys) AbortedIncomingTransfers.Remove(key);
            SkinPartGuard.ForgetPlayer(player.GetUniqueID());

            var outgoingToDrop = InFlightFiles.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in outgoingToDrop) InFlightFiles.Remove(key);

            var transfersToDrop = TransfersRemaining.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in transfersToDrop) TransfersRemaining.Remove(key);

            var dropsToDrop = DroppedFiles.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in dropsToDrop) DroppedFiles.Remove(key);

            var requestsToDrop = PendingRequests.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in requestsToDrop) PendingRequests.Remove(key);

            if (OutgoingQueue.Count > 0)
            {
                var kept = OutgoingQueue.Where(f => f.Target != player).ToList();
                OutgoingQueue.Clear();
                foreach (var f in kept) OutgoingQueue.Enqueue(f);
            }
        }

        public static void ForgetSenderState(OnlinePlayer player)
        {
            if (player == null) return;
            string uid = player.GetUniqueID();
            string prefix = $"{uid}_";

            var requestsToDrop = PendingRequests.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in requestsToDrop) PendingRequests.Remove(key);

            var incomingToDrop = IncomingTransfers.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in incomingToDrop) IncomingTransfers.Remove(key);

            var abortedToDrop = AbortedIncomingTransfers.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in abortedToDrop) AbortedIncomingTransfers.Remove(key);

            SkinPartGuard.ForgetPlayer(uid);

            int dropped = requestsToDrop.Count + incomingToDrop.Count + abortedToDrop.Count;
            if (dropped > 0)
            {
                Plugin.Logger.LogDebug($"[DMSxMeadow] 🧹 Estado entrante de {player.id} purgado ({dropped} elementos): handshake nuevo, el slugcat anterior quedó obsoleto.");
            }
        }

        public static void AbortAllOutgoing()
        {
            int dropped = OutgoingQueue.Count + InFlightFiles.Count + TransfersRemaining.Count;
            OutgoingQueue.Clear();
            InFlightFiles.Clear();
            TransfersRemaining.Clear();
            DroppedFiles.Clear();
            if (dropped > 0)
            {
                Plugin.Logger.LogDebug($"[DMSxMeadow] 🧹 Envíos en curso abortados ({dropped} elementos) por cambio de slugcat local: la skin vieja quedó obsoleta.");
            }
        }

        public static void ClearAllTransfers()
        {
            PendingRequests.Clear();
            OutgoingQueue.Clear();
            InFlightFiles.Clear();
            TransfersRemaining.Clear();
            DroppedFiles.Clear();
            IncomingTransfers.Clear();
            AbortedIncomingTransfers.Clear();
            SkinPartGuard.ClearAll();
            _nextSendAllowedTime = 0f;
            Plugin.Logger.LogDebug("[DMSxMeadow] 🧹 Transferencias pendientes y recepciones incompletas purgadas (sesión terminada).");
        }

        private static void OnChunkReceived(OnlinePlayer sender, string skinId, string fileName, byte[] fileBytes, int fileIndex, int totalFiles)
        {
            MarkRequestSatisfied(sender, skinId);

            string transferKey = $"{sender.GetUniqueID()}_{skinId}";

            if (AbortedIncomingTransfers.Contains(transferKey))
            {
                Plugin.Logger.LogDebug($"[DMSxMeadow] Archivo [{fileIndex + 1}/{totalFiles}] '{fileName}' de la transferencia rechazada {transferKey} ignorado (3a estricto).");
                return;
            }

            if (!IncomingTransfers.ContainsKey(transferKey))
            {
                IncomingTransfers[transferKey] = new Dictionary<string, byte[]>();
            }

            IncomingTransfers[transferKey][fileName] = fileBytes;
            Plugin.Logger.LogDebug($"[DMSxMeadow] Archivo [{fileIndex + 1}/{totalFiles}] '{fileName}' recibido para skin '{skinId}' desde {sender.id}.");

            if (!SkinPartGuard.ValidateIncomingFile(transferKey, fileName, IncomingTransfers[transferKey]))
            {
                AbortIncomingTransfer(transferKey);
                return;
            }

            if (IncomingTransfers[transferKey].Count >= totalFiles)
            {
                Plugin.Logger.LogDebug($"[DMSxMeadow] 📦 Skin completa '{skinId}' recibida de {sender.id}. Registrando en caché...");
                var completeSkinFiles = IncomingTransfers[transferKey];

                if (!SkinPartGuard.ValidateDeferredAtCompletion(transferKey, completeSkinFiles))
                {
                    AbortIncomingTransfer(transferKey);
                    return;
                }

                IncomingTransfers.Remove(transferKey);
                string senderSteamId = DMSNetworkTester.SkinSerializer.GetPlayerSteamId(sender);
                SkinRegistration.RegisterReceivedSkin(senderSteamId, skinId, completeSkinFiles);

            }
        }
    }
}