using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace DMSxMeadow
{
    internal static class SkinPartGuard
    {
        public enum PartKind { Arm, Legs, Head, Face, Hips, Body, Tail, HandOnPoles, Misc }

        private static readonly Dictionary<PartKind, Vector2Int> Limits =
            new Dictionary<PartKind, Vector2Int>
            {
                { PartKind.Body, new Vector2Int(90, 60) },
                { PartKind.Head, new Vector2Int(1110, 280) },
                { PartKind.Legs, new Vector2Int(1960, 70) },
                { PartKind.Arm, new Vector2Int(450, 1010) },
                { PartKind.Face, new Vector2Int(280, 240) },
                { PartKind.Hips, new Vector2Int(50, 60) },
                { PartKind.Tail, new Vector2Int(2048, 2048) },
                { PartKind.HandOnPoles, new Vector2Int(40, 50) },
                { PartKind.Misc, new Vector2Int(522, 522) }
            };

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static bool TryReadPngSize(byte[] bytes, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (bytes == null || bytes.Length < 24) return false;

            for (int i = 0; i < PngSignature.Length; i++)
            {
                if (bytes[i] != PngSignature[i]) return false;
            }

            width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return width > 0 && height > 0;
        }

        // ---- CLASIFICACIÓN DE PARTE ----

        public static PartKind ClassifyTxt(byte[] txtBytes, string fileName)
        {
            if (txtBytes == null || txtBytes.Length == 0) return PartKind.Misc;

            string text = Encoding.UTF8.GetString(txtBytes, 0, txtBytes.Length);
            string trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

            if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                Plugin.Logger.LogWarning($"⚠️ 3a estricto: .txt '{fileName}' no parece JSON de TexturePacker. Se trata como misc (tope 522).");
                return PartKind.Misc;
            }

            if (ContainsAny(trimmed, "PlayerArm", "LeftPlayerArm", "RightPlayerArm")) return PartKind.Arm;
            if (ContainsAny(trimmed, "LegsA", "LeftLegsA", "RightLegsA")) return PartKind.Legs;
            if (ContainsAny(trimmed, "HeadA", "LeftHeadA", "RightHeadA")) return PartKind.Head;
            if (ContainsAny(trimmed, "FaceA", "FaceB", "FaceDead", "FaceStunned")) return PartKind.Face;
            if (ContainsAny(trimmed, "HipsA", "LeftHipsA", "RightHipsA")) return PartKind.Hips;
            if (ContainsAny(trimmed, "BodyA", "LeftBodyA", "RightBodyA")) return PartKind.Body;
            if (trimmed.IndexOf("TailTexture", StringComparison.Ordinal) >= 0) return PartKind.Tail;
            if (trimmed.IndexOf("OnTopOfTerrainHand", StringComparison.Ordinal) >= 0) return PartKind.HandOnPoles;
            return PartKind.Misc;
        }

        private static bool ContainsAny(string text, params string[] tokens)
        {
            for (int i = 0; i < tokens.Length; i++)
            {
                if (text.IndexOf(tokens[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // ---- Estado de validación por transferencia entrante ----

        private static readonly Dictionary<string, PartKind> PendingKinds =
            new Dictionary<string, PartKind>(StringComparer.Ordinal);

        private static readonly HashSet<string> DeferredPngs =
            new HashSet<string>(StringComparer.Ordinal);

        private static string PartKey(string transferKey, string fileName) =>
            $"{transferKey}|{Path.GetFileNameWithoutExtension(fileName)}";

        private static bool IsExtension(string fileName, string ext) =>
            Path.GetExtension(fileName).Equals(ext, StringComparison.OrdinalIgnoreCase);

        private static string FindPairedPng(string txtName, Dictionary<string, byte[]> transfer)
        {
            string txtBase = Path.GetFileNameWithoutExtension(txtName);
            return FindPngByBase(txtBase, transfer);
        }

        private static string FindPngByBase(string baseName, Dictionary<string, byte[]> transfer)
        {
            foreach (string key in transfer.Keys)
            {
                if (!IsExtension(key, ".png")) continue;
                if (string.Equals(Path.GetFileNameWithoutExtension(key), baseName, StringComparison.OrdinalIgnoreCase))
                    return key;
            }
            return null;
        }

        public static bool ValidateIncomingFile(string transferKey, string fileName, Dictionary<string, byte[]> transfer)
        {
            if (IsExtension(fileName, ".txt"))
            {
                byte[] txtBytes;
                if (!transfer.TryGetValue(fileName, out txtBytes)) return true;

                string partKey = PartKey(transferKey, fileName);
                PendingKinds[partKey] = ClassifyTxt(txtBytes, fileName);

                if (DeferredPngs.Remove(partKey))
                {
                    string pngName = FindPairedPng(fileName, transfer);
                    if (pngName != null && transfer.TryGetValue(pngName, out byte[] pngBytes))
                    {
                        return ValidatePng(transferKey, pngName, PendingKinds[partKey], pngBytes);
                    }
                }
                return true;
            }

            if (IsExtension(fileName, ".png"))
            {
                string partKey = PartKey(transferKey, fileName);
                PartKind part;
                if (PendingKinds.TryGetValue(partKey, out part))
                {
                    byte[] pngBytes;
                    if (transfer.TryGetValue(fileName, out pngBytes))
                    {
                        return ValidatePng(transferKey, fileName, part, pngBytes);
                    }
                    return true;
                }

                DeferredPngs.Add(partKey);
                return true;
            }

            return true;
        }

        public static bool ValidateDeferredAtCompletion(string transferKey, Dictionary<string, byte[]> transfer)
        {
            if (DeferredPngs.Count == 0) return true;

            string transferPrefix = transferKey + "|";
            var keys = new List<string>();
            foreach (string partKey in DeferredPngs)
            {
                if (partKey.StartsWith(transferPrefix, StringComparison.Ordinal)) keys.Add(partKey);
            }

            foreach (string partKey in keys)
            {
                DeferredPngs.Remove(partKey);
                string baseName = partKey.Substring(transferPrefix.Length);
                string pngName = FindPngByBase(baseName, transfer);
                if (pngName == null || !transfer.TryGetValue(pngName, out byte[] pngBytes)) continue;
                if (!ValidatePng(transferKey, pngName, PartKind.Misc, pngBytes)) return false;
            }
            return true;
        }

        private static bool ValidatePng(string transferKey, string pngName, PartKind part, byte[] pngBytes)
        {
            int width;
            int height;
            if (!TryReadPngSize(pngBytes, out width, out height))
            {
                Plugin.Logger.LogWarning($"⛔ 3a estricto: '{pngName}' ({transferKey}) no es un PNG válido (< 24 B o firma IHDR incorrecta). Transferencia rechazada -> skin por defecto.");
                return false;
            }

            Vector2Int limit;
            if (!Limits.TryGetValue(part, out limit)) return true;

            if (width <= limit.x && height <= limit.y) return true;

            Plugin.Logger.LogWarning($"⛔ 3a estricto: '{pngName}' ({transferKey}) rechazado — parte '{part}', dimensiones {width}x{height} > tope {limit.x}x{limit.y}. Transferencia rechazada -> skin por defecto (RNF-3).");
            return false;
        }

        // ---- LIMPIEZA ----

        public static void ForgetPlayer(string playerUniqueId)
        {
            string prefix = playerUniqueId + "_";
            RemoveKindsWhere(prefix);
            RemovePngsWhere(prefix);
        }

        public static void ForgetTransfer(string transferKey)
        {
            string prefix = transferKey + "|";
            RemoveKindsWhere(prefix);
            RemovePngsWhere(prefix);
        }

        public static void ClearAll()
        {
            PendingKinds.Clear();
            DeferredPngs.Clear();
        }

        private static void RemoveKindsWhere(string prefix)
        {
            var toRemove = new List<string>();
            foreach (var kvp in PendingKinds)
            {
                if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal)) toRemove.Add(kvp.Key);
            }
            foreach (string key in toRemove) PendingKinds.Remove(key);
        }

        private static void RemovePngsWhere(string prefix)
        {
            var toRemove = new List<string>();
            foreach (string key in DeferredPngs)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal)) toRemove.Add(key);
            }
            foreach (string key in toRemove) DeferredPngs.Remove(key);
        }
    }
}