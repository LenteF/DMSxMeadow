using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RainMeadow;
using UnityEngine;

namespace DMSxMeadow
{
    /// <summary>
    /// Transporte CustomPacket (canal 1) para archivos de skin.
    ///
    /// FIX: la versión original mandaba todos los archivos de una skin en el
    /// mismo frame, de forma síncrona, y sin ningún ACK/reintento propio.
    /// Se detectaron dos causas de pérdida:
    ///
    ///  1) OnlineManager.SendCustomData (Rain Meadow) descarta el envío EN SILENCIO
    ///     si el emisor todavía no tiene sincronizado el CustomClientSettings del
    ///     receptor (anuncio de qué claves de CustomPacket soporta). Esto es una
    ///     carrera contra el sync de estado normal de Rain Meadow y ocurre sobre
    ///     todo justo tras el join (que es cuando disparamos el handshake).
    ///  2) Incluso una vez pasado ese filtro, mandar 15-30 paquetes "Reliable" en
    ///     ráfaga en el mismo frame, justo cuando la sesión P2P puede seguir
    ///     estabilizándose, puede perder algún paquete suelto sin que nada lo
    ///     detecte ni lo reintente.
    ///
    /// Solución: cola de salida con throttling (1 paquete cada SEND_INTERVAL
    /// segundos) + ACK explícito por archivo (RPC de sesión, canal 0, fiable por
    /// reintento nativo de Rain Meadow) + reintento automático de archivos no
    /// confirmados + reintento con backoff de la petición inicial si no llega
    /// NINGÚN archivo en un tiempo razonable (cubre el caso 1).
    /// </summary>
    public class SkinTransfer : IUseCustomPackets
    {
        public static readonly string PacketKey = "DMS_SkinData";
        private const int MaxCustomPacketBytes = 32768; // Límite duro de CustomPacket (CustomPacket.cs:42)

        // --- Throttling de envío ---
        private const float SendIntervalSeconds = 0.05f;  // ~20 paquetes/seg como máximo
        private static float _nextSendAllowedTime = 0f;

        // --- Reintento por archivo (lado emisor) ---
        private const float FileAckTimeoutSeconds = 4f;
        private const int MaxFileAttempts = 6;

        // --- Reintento de la petición inicial (lado solicitante) ---
        private const float RequestTimeoutSeconds = 4f;
        private const int MaxRequestAttempts = 6; // ~24s de reintentos antes de rendirse

        private static SkinTransfer _instance;

        public bool Active => true;

        public static void Initialize()
        {
            if (_instance == null)
            {
                _instance = new SkinTransfer();
                CustomManager.Subscribe(PacketKey, _instance);
                Plugin.Logger.LogInfo($"[DMSxMeadow] SkinTransfer suscrito con éxito a la clave de paquetes '{PacketKey}'.");
            }
        }

        // ===================================================================
        // SOLICITUD (lado que pide la skin) — con reintento por timeout
        // ===================================================================

        private class PendingRequest
        {
            public OnlinePlayer Target;
            public string SkinId;
            public float LastRequestTime;
            public int Attempts;
            public bool AnyFileReceived;
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
                pending = new PendingRequest { Target = targetPlayer, SkinId = skinId };
                PendingRequests[key] = pending;
            }

            pending.Attempts++;
            pending.LastRequestTime = Time.time;
            pending.AnyFileReceived = false;

            Plugin.Logger.LogInfo($"[DMSxMeadow] Solicitando skin '{skinId}' al jugador {targetPlayer.id}... (intento {pending.Attempts}/{MaxRequestAttempts})");
            targetPlayer.InvokeRPC(DMSNetworkTester.SkinSerializer.RPC_RequestSkin, DMSNetworkTester.SkinSerializer.GetPlayerSteamId(OnlineManager.mePlayer), skinId);
        }

        /// <summary>Se llama cada Update. Reintenta peticiones que no han recibido NINGÚN
        /// archivo tras RequestTimeoutSeconds — cubre el caso en que el envío completo se
        /// perdió en el emisor por la carrera de CustomClientSettings.</summary>
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
                    Plugin.Logger.LogError($"[DMSxMeadow] ❌ Se agotaron los reintentos pidiendo '{pending.SkinId}' a {pending.Target?.id}. Abandonando.");
                    toRemove.Add(kvp.Key);
                    continue;
                }

                toRetry.Add(pending);
            }

            foreach (string key in toRemove) PendingRequests.Remove(key);

            foreach (var pending in toRetry)
            {
                if (pending.Target == null) continue;
                RequestSkinFromPlayer(pending.Target, pending.SkinId); // re-encola con Attempts++
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
        // ENVÍO (lado que tiene la skin) — cola throttled + ACK + reintento
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

        // clave = "{targetUniqueId}_{skinId}_{fileIndex}"
        private static readonly Dictionary<string, OutgoingFile> InFlightFiles =
            new Dictionary<string, OutgoingFile>(StringComparer.Ordinal);

        // clave = "{targetUniqueId}_{skinId}" -> archivos que faltan por confirmar o mandar
        private static readonly Dictionary<string, int> TransfersRemaining =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static string FileKey(string targetUniqueId, string skinId, int fileIndex) =>
            $"{targetUniqueId}_{skinId}_{fileIndex}";

        public static void SendSkinToPlayer(OnlinePlayer requester, string skinId)
        {
            if (requester == null || string.IsNullOrEmpty(skinId)) return;

            string transferKey = RequestKey(requester.GetUniqueID(), skinId);
            if (TransfersRemaining.ContainsKey(transferKey))
            {
                Plugin.Logger.LogInfo($"[DMSxMeadow] Ya hay una transferencia de '{skinId}' en curso hacia {requester.id}; se ignora la petición duplicada (probablemente un reintento del solicitante).");
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

            Plugin.Logger.LogInfo($"[DMSxMeadow] Encolando {total} archivos de la skin '{skinId}' hacia {requester.id} (envío throttled con ACK)...");

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

        /// <summary>Se llama cada Update (RainWorld_Update). Manda como mucho 1 paquete por
        /// tick, respetando SendIntervalSeconds: primero archivos nunca enviados, luego
        /// reintentos de archivos cuyo ACK no llegó a tiempo.</summary>
        public static void UpdatePendingTransfers()
        {
            if (Time.time < _nextSendAllowedTime) return;

            if (OutgoingQueue.Count > 0)
            {
                var file = OutgoingQueue.Dequeue();
                SendOneFile(file);
                return;
            }

            // Sin nada nuevo que mandar: revisamos si algún archivo en vuelo se pasó del timeout.
            OutgoingFile toRetry = null;
            foreach (var kvp in InFlightFiles)
            {
                var f = kvp.Value;
                if (Time.time - f.LastSentTime < FileAckTimeoutSeconds) continue;

                if (f.Attempts >= MaxFileAttempts)
                {
                    continue; // se limpia más abajo
                }

                toRetry = f;
                break;
            }

            if (toRetry != null)
            {
                Plugin.Logger.LogWarning($"[DMSxMeadow] ⏱️ Sin ACK para '{toRetry.FileName}' ({toRetry.SkinId}) hacia {toRetry.Target?.id}. Reintentando (intento {toRetry.Attempts + 1}/{MaxFileAttempts})...");
                SendOneFile(toRetry);
                return;
            }

            // Limpieza: descartamos entradas que agotaron reintentos para no revisarlas cada frame.
            var exhausted = InFlightFiles.Where(kvp => kvp.Value.Attempts >= MaxFileAttempts).ToList();
            foreach (var kvp in exhausted)
            {
                Plugin.Logger.LogError($"[DMSxMeadow] ❌ '{kvp.Value.FileName}' de la skin '{kvp.Value.SkinId}' hacia {kvp.Value.Target?.id} no se pudo confirmar tras {kvp.Value.Attempts} intentos. Se abandona ese archivo.");
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

        /// <summary>Llamado cuando llega el RPC_AckSkinFile del receptor.</summary>
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
                    Plugin.Logger.LogInfo($"[DMSxMeadow] ✅ Transferencia de '{file.SkinId}' hacia {file.Target?.id} finalizada (confirmada o agotada archivo por archivo).");
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

                    // H-3: validación anti path traversal. Nada de estos nombres llega a
                    // Path.Combine(CacheSkinsPath, ...) sin pasar por aquí. No se manda ACK:
                    // el emisor reintentará y acabará abandonando el archivo.
                    if (!SkinRegistration.IsValidSkinIdentifier(skinId) || !SkinRegistration.IsValidSkinIdentifier(fileName))
                    {
                        Plugin.Logger.LogWarning($"[DMSxMeadow] ⛔ CustomPacket de skin de {fromPlayer?.id} RECHAZADO: skinId='{skinId}' fileName='{fileName}' no cumplen ^[a-zA-Z0-9_.-]+$.");
                        return;
                    }

                    OnChunkReceived(fromPlayer, skinId, fileName, fileBytes, fileIndex, totalFiles);

                    // ACK inmediato: RPC de sesión (canal 0), fiable por el reintento nativo
                    // de Rain Meadow — no depende del filtro de CustomClientSettings porque
                    // los RPC de sesión no pasan por ese chequeo.
                    fromPlayer.InvokeRPC(DMSNetworkTester.SkinSerializer.RPC_AckSkinFile, DMSNetworkTester.SkinSerializer.GetPlayerSteamId(OnlineManager.mePlayer), skinId, fileIndex);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DMSxMeadow] Error al procesar CustomPacket de skin de {fromPlayer?.id}: {ex}");
            }
        }

        private static readonly Dictionary<string, Dictionary<string, byte[]>> IncomingTransfers = new Dictionary<string, Dictionary<string, byte[]>>();

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
                Plugin.Logger.LogInfo($"[DMSxMeadow] 🧹 Descartadas {incompleteKeys.Count} transferencia(s) entrante(s) incompleta(s) de {player.id} (jugador salió).");
            }

            // Limpieza del lado emisor: dejamos de mandarle/reintentarle archivos a quien se fue.
            var outgoingToDrop = InFlightFiles.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in outgoingToDrop) InFlightFiles.Remove(key);

            var transfersToDrop = TransfersRemaining.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in transfersToDrop) TransfersRemaining.Remove(key);

            var requestsToDrop = PendingRequests.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in requestsToDrop) PendingRequests.Remove(key);

            // La cola de salida es más barata de filtrar reconstruyéndola.
            if (OutgoingQueue.Count > 0)
            {
                var kept = OutgoingQueue.Where(f => f.Target != player).ToList();
                OutgoingQueue.Clear();
                foreach (var f in kept) OutgoingQueue.Enqueue(f);
            }
        }

        /// <summary>Purga total al terminar la sesión (host se fue / LeaveLobby):
        /// peticiones pendientes, cola throttleada de envío, archivos en vuelo,
        /// ACK sin confirmar y recepciones incompletas.</summary>
        public static void ClearAllTransfers()
        {
            PendingRequests.Clear();
            OutgoingQueue.Clear();
            InFlightFiles.Clear();
            TransfersRemaining.Clear();
            IncomingTransfers.Clear();
            _nextSendAllowedTime = 0f;
            Plugin.Logger.LogInfo("[DMSxMeadow] 🧹 Transferencias pendientes y recepciones incompletas purgadas (sesión terminada).");
        }

        public static void ForgetAllPlayers()
        {
            IncomingTransfers.Clear();
            OutgoingQueue.Clear();
            InFlightFiles.Clear();
            TransfersRemaining.Clear();
            PendingRequests.Clear();
        }

        private static void OnChunkReceived(OnlinePlayer sender, string skinId, string fileName, byte[] fileBytes, int fileIndex, int totalFiles)
        {
            MarkRequestSatisfied(sender, skinId);

            string transferKey = $"{sender.GetUniqueID()}_{skinId}";

            if (!IncomingTransfers.ContainsKey(transferKey))
            {
                IncomingTransfers[transferKey] = new Dictionary<string, byte[]>();
            }

            IncomingTransfers[transferKey][fileName] = fileBytes;
            Plugin.Logger.LogInfo($"[DMSxMeadow] Archivo [{fileIndex + 1}/{totalFiles}] '{fileName}' recibido para skin '{skinId}' desde {sender.id}.");

            if (IncomingTransfers[transferKey].Count >= totalFiles)
            {
                Plugin.Logger.LogInfo($"[DMSxMeadow] 📦 Skin completa '{skinId}' recibida de {sender.id}. Registrando en caché...");
                var completeSkinFiles = IncomingTransfers[transferKey];
                IncomingTransfers.Remove(transferKey);
                string senderSteamId = DMSNetworkTester.SkinSerializer.GetPlayerSteamId(sender);
                SkinRegistration.SaveAndRegisterCacheSkin(senderSteamId, skinId, completeSkinFiles);

                // La recreación del slug queda cubierta dentro de SaveAndRegisterCacheSkin:
                // si hubo ReloadAtlases() (primera escritura en disco) se recrean TODOS los
                // slugs (ScheduleRecreateAllSlugs, evita sprites inválidos → invisibilidad);
                // si la skin ya existía no hubo reload y no hay nada que recrear. La
                // recreación por id aquí sería redundante, por eso no se repite.
            }
        }
    }
}
