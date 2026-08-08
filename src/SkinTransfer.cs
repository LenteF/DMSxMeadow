using System;
using System.Collections.Generic;
using System.IO;
using RainMeadow;

namespace DMSxMeadow
{
    public class SkinTransfer : IUseCustomPackets
    {
        public static readonly string PacketKey = "DMS_SkinData";
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

        public static void RequestSkinFromPlayer(OnlinePlayer targetPlayer, string skinId)
        {
            if (targetPlayer == null || string.IsNullOrEmpty(skinId)) return;
            Plugin.Logger.LogInfo($"[DMSxMeadow] Solicitando skin '{skinId}' al jugador {targetPlayer.id}...");
            targetPlayer.InvokeRPC(DMSNetworkTester.SkinSerializer.RPC_RequestSkin, OnlineManager.mePlayer, skinId);
        }

        public static void SendSkinToPlayer(OnlinePlayer requester, string skinId)
        {
            if (requester == null || string.IsNullOrEmpty(skinId)) return;

            var fileData = SkinRegistration.ExportEquippedSkinToDTO(skinId);
            if (fileData == null || fileData.Count == 0)
            {
                Plugin.Logger.LogError($"[DMSxMeadow] No se pudieron empaquetar los archivos de la skin '{skinId}' para enviar a {requester.id}.");
                return;
            }

            Plugin.Logger.LogInfo($"[DMSxMeadow] Enviando {fileData.Count} archivos de la skin '{skinId}' a {requester.id} vía CustomPacket...");

            int index = 0;
            int total = fileData.Count;

            foreach (var kvp in fileData)
            {
                byte[] packetBytes = BuildSkinPacket(skinId, kvp.Key, kvp.Value, index, total);
                ushort dataSize = (ushort)packetBytes.Length;

                var packet = new CustomPacket(PacketKey, packetBytes, dataSize);
                OnlineManager.SendCustomData(requester, packet, NetIO.SendType.Reliable);

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

                    OnChunkReceived(fromPlayer, skinId, fileName, fileBytes, fileIndex, totalFiles);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DMSxMeadow] Error al procesar CustomPacket de skin de {fromPlayer?.id}: {ex}");
            }
        }

        private static readonly Dictionary<string, Dictionary<string, byte[]>> IncomingTransfers = new Dictionary<string, Dictionary<string, byte[]>>();

        private static void OnChunkReceived(OnlinePlayer sender, string skinId, string fileName, byte[] fileBytes, int fileIndex, int totalFiles)
        {
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
                SkinRegistration.SaveAndRegisterCacheSkin(skinId, completeSkinFiles);
            }
        }
    }
}
