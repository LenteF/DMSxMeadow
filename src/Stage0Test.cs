using DressMySlugcat;
using DressMySlugcat.Hooks;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DMSxMeadow
{
    public class Stage0Test : MonoBehaviour
    {
        private const string TEST_SKIN_PATH = @"C:\Program Files (x86)\Steam\steamapps\common\Rain World\RainWorld_Data\StreamingAssets\mods\hollowknight\dressmyslugcat\The Knight";

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F5))
            {
                RunStage0Test();
            }
        }

        private void RunStage0Test()
        {
            try
            {
                Player localPlayer = GetLocalPlayer();
                if (localPlayer == null) return;

                string skinId = "Boombloxxed.Knight"; // Skin de pruebas que he utilizado

                var files = new Dictionary<string, byte[]>();
                foreach (string filePath in Directory.GetFiles(TEST_SKIN_PATH, "*.*", SearchOption.AllDirectories))
                {
                    string relativePath = filePath.Substring(TEST_SKIN_PATH.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    files[relativePath] = File.ReadAllBytes(filePath);
                }

                if (SkinRegistration.SaveAndRegisterCacheSkin(skinId, files))
                {
                    ApplySkinToLocalPlayer(localPlayer, skinId);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DMSxMeadow] Error en el test de la Etapa 0: {ex}");
            }
        }

        private void ApplySkinToLocalPlayer(Player player, string skinId)
        {
            try
            {
                string slugcatName = ((ExtEnumBase)player.slugcatStats.name).value;
                int playerNumber = player.playerState.playerNumber;

                var customization = Customization.For(slugcatName, playerNumber, mergeDefaults: false);
                if (customization == null)
                {
                    customization = new Customization
                    {
                        Slugcat = slugcatName,
                        PlayerNumber = playerNumber
                    };
                    SaveManager.Customizations.Add(customization);
                }

                SpriteSheet injectedSheet = SpriteSheet.Get(skinId);
                if (injectedSheet != null)
                {
                    foreach (string spriteCategory in injectedSheet.AvailableSpriteNames)
                    {
                        var customSprite = customization.CustomSprite(spriteCategory, createIfNotExists: true);
                        customSprite.SpriteSheetID = skinId;
                        customSprite.Enforce = true;
                    }

                    Debug.Log($"[DMSxMeadow] Customization de SaveManager actualizada con la skin '{skinId}' para {slugcatName} ({playerNumber}).");
                }

                if (player.graphicsModule is PlayerGraphics pg)
                {
                    if (PlayerGraphicsHooks.PlayerGraphicsData.TryGetValue(pg, out var pgEx))
                    {
                        pgEx.Customization = Customization.For(slugcatName, playerNumber, mergeDefaults: true);
                        pgEx.ScheduleForRecreation = true;
                        Debug.Log("[DMSxMeadow] ✅ ScheduleForRecreation marcado en el PlayerGraphicsEx del jugador local.");
                    }
                    else
                    {
                        Debug.LogWarning("[DMSxMeadow] No se encontró el PlayerGraphicsEx del jugador local.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DMSxMeadow] Error al aplicar customización inyectada: {ex}");
            }
        }

        private Player GetLocalPlayer()
        {
            var rainWorldGame = RWCustom.Custom.rainWorld.processManager.currentMainLoop as RainWorldGame;

            if (rainWorldGame == null || rainWorldGame.Players == null || rainWorldGame.Players.Count == 0)
            {
                return null;
            }

            foreach (var abstractPlayer in rainWorldGame.Players)
            {
                if (abstractPlayer != null && abstractPlayer.realizedCreature is Player player)
                {
                    return player;
                }
            }

            return null;
        }
    }
}
