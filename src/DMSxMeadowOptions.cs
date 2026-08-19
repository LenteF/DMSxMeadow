using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Menu;
using Menu.Remix.MixedUI;
using UnityEngine;

namespace DMSxMeadow
{
    public class DMSxMeadowOptions : OptionInterface
    {
        public static DMSxMeadowOptions Instance = new DMSxMeadowOptions();

        private OpScrollBox _scrollBox;
        private OpTextBox _searchBox;
        private OpSimpleButton _modeButton;
        private bool _searchBySteamId = true;
        private List<ProfileRow> _currentRows = new List<ProfileRow>();

        private OpScrollBox _bannedScrollBox;
        private OpTextBox _bannedSearchBox;
        private OpSimpleButton _bannedModeButton;
        private bool _searchBannedBySteamId = true;
        private List<BannedRow> _bannedRows = new List<BannedRow>();

        private const string ONLINE_TAB_NAME = "Online";
        private const string LOCAL_TAB_NAME = "Local";

        private OpTab _onlineTab;
        private OpTab _profilesTab;

        private Configurable<string> _searchConfig;
        private Configurable<string> _bannedSearchConfig;

        // ============================================================
        // FLAG DE CONSENTIMIENTO
        // ============================================================
        private static Configurable<bool> _shareSkinConfig;

        // ============================================================
        // GATE "SOLO AMIGOS"
        // ============================================================
        private static Configurable<bool> _friendsOnlyConfig;

        public static bool ShareSkinEnabled => ReadBoolFromConfigFile("shareSkin");

        public static bool FriendsOnlyEnabled => ReadBoolFromConfigFile("friendsOnly");

        private static bool ReadBoolFromConfigFile(string key)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "ModConfigs", "dmsxmeadow.txt");
                if (!File.Exists(path)) return false;

                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase) && line.Contains("="))
                    {
                        string value = line.Substring(line.IndexOf('=') + 1).Trim();
                        if (bool.TryParse(value, out bool parsed))
                        {
                            return parsed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                string what = key == "shareSkin" ? "de compartir" : "'solo amigos'";
                Plugin.Logger.LogError($"Error leyendo el flag {what} del archivo de config: {ex.Message}");
            }
            return false;
        }

        public void EnsureConfigBound()
        {
            try
            {
                if (_shareSkinConfig == null && this.config != null)
                {
                    _shareSkinConfig = this.config.Bind<bool>("shareSkin", ReadBoolFromConfigFile("shareSkin"), new ConfigurableInfo(
                        "Allow other players to request the skins you have equipped. OFF by default: your skins never leave your machine."));
                }

                if (_friendsOnlyConfig == null && this.config != null)
                {
                    _friendsOnlyConfig = this.config.Bind<bool>("friendsOnly", ReadBoolFromConfigFile("friendsOnly"), new ConfigurableInfo(
                        "Only download skins from players on your Steam friends list. ON: others (host or client) are never downloaded and appear with the default skin."));
                }

                _shareSkinConfig.Value = ShareSkinEnabled;
                _friendsOnlyConfig.Value = FriendsOnlyEnabled;

                Plugin.Logger.LogDebug($"Flags (fuente: ModConfigs/dmsxmeadow.txt): ShareSkin={ShareSkinEnabled}, SoloAmigos={FriendsOnlyEnabled}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"No se pudo bindear temprano el flag de compartir: {ex.Message}");
            }
        }

        private static void WriteBoolToConfigFile(string key, bool value)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "ModConfigs", "dmsxmeadow.txt");
                var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
                string newLine = key + " = " + value.ToString().ToLowerInvariant();
                int keyIndex = lines.FindIndex(l => l.TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase) && l.Contains("="));
                if (keyIndex >= 0) lines[keyIndex] = newLine;
                else lines.Add(newLine);
                File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                string what = key == "shareSkin" ? "de compartir" : "'solo amigos'";
                Plugin.Logger.LogError($"Error escribiendo el flag {what} al archivo de config: {ex.Message}");
            }
        }

        // ============================================================
        // CACHE PARA UNLOAD
        // ============================================================
        private static MethodInfo _unloadMethod;

        private class ProfileRow
        {
            public int ProfileNumber;
            public string SteamId;
            public OpLabel Label;
            public OpSimpleButton DeleteButton;
        }

        private class BannedRow
        {
            public string BannedId;
            public string BannedName;
            public OpLabel Label;
            public OpSimpleButton DeleteButton;
        }

        public override void Initialize()
        {
            base.Initialize();

            // ============================================================
            // TABS: "Online" and "Local"
            // ============================================================
            _onlineTab = new OpTab(this, ONLINE_TAB_NAME);
            _profilesTab = new OpTab(this, LOCAL_TAB_NAME);
            Tabs = new[] { _onlineTab, _profilesTab };

            float row1Y = 515f;
            float searchRowOffsetX = -21f;
            float titleY = row1Y + 55f;

            if (_shareSkinConfig == null)
            {
                _shareSkinConfig = this.config.Bind<bool>("shareSkin", ReadBoolFromConfigFile("shareSkin"), new ConfigurableInfo(
                    "Allow other players to request the skins you have equipped. OFF by default: your skins never leave your machine."));
            }

            if (_friendsOnlyConfig == null)
            {
                _friendsOnlyConfig = this.config.Bind<bool>("friendsOnly", ReadBoolFromConfigFile("friendsOnly"), new ConfigurableInfo(
                    "Only download skins from players on your Steam friends list. ON: others (host or client) are never downloaded and appear with the default skin."));
            }

            _shareSkinConfig.Value = ShareSkinEnabled;
            _friendsOnlyConfig.Value = FriendsOnlyEnabled;

            var onlineTitle = new OpLabel(
                new Vector2(20f + searchRowOffsetX + 124f, titleY),
                new Vector2(350f, 20f),
                "ONLINE OPTIONS",
                FLabelAlignment.Center,
                false
            );
            _onlineTab.AddItems(onlineTitle);

            var shareCheckbox = new OpCheckBox(_shareSkinConfig, new Vector2(20f, 515f));
            shareCheckbox.OnValueChanged += (_, _, newValue) =>
            {
                if (bool.TryParse(newValue, out bool parsed))
                {
                    bool changed = parsed != ShareSkinEnabled;
                    WriteBoolToConfigFile("shareSkin", parsed);
                    Plugin.Logger.LogDebug($"Checkbox de compartir: {(parsed ? "ON" : "OFF")} — escrito al archivo al instante{(changed ? ", re-emitiendo handshake." : " (sin cambio real).")}");
                    if (changed)
                    {
                        Plugin.RequestHandshakeReemit();
                    }
                }
            };
            _onlineTab.AddItems(shareCheckbox);

            var shareDescription = new OpLabel(
                new Vector2(52f, 490f),
                new Vector2(520f, 60f),
                "Share Your Skin",
                FLabelAlignment.Left,
                false
            );
            shareDescription.color = new Color(0.65f, 0.65f, 0.65f);
            _onlineTab.AddItems(shareDescription);

            // ============================================================
            // GATE "SOLO AMIGOS"
            // ============================================================
            var friendsOnlyCheckbox = new OpCheckBox(_friendsOnlyConfig, new Vector2(300f, 515f));
            friendsOnlyCheckbox.OnValueChanged += (_, _, newValue) =>
            {
                if (bool.TryParse(newValue, out bool parsed))
                {
                    bool changed = parsed != FriendsOnlyEnabled;
                    WriteBoolToConfigFile("friendsOnly", parsed);
                    Plugin.Logger.LogDebug($"Checkbox 'solo amigos': {(parsed ? "ON" : "OFF")} — escrito al archivo al instante.");
                    if (changed && parsed)
                    {
                        SkinSerializer.ForgetAllPlayers();
                        SkinTransfer.ClearAllTransfers();
                        Plugin.ScheduleRecreateAllSlugs();
                        Plugin.Logger.LogDebug("🤝 'Solo amigos' activado: skins de no-amigos purgadas y slugs de la sala recreados a default.");
                    }
                }
            };
            _onlineTab.AddItems(friendsOnlyCheckbox);

            var friendsOnlyDescription = new OpLabel(
                new Vector2(332f, 490f),
                new Vector2(300f, 60f),
                "Only Steam Friends Skins",
                FLabelAlignment.Left,
                false
            );
            friendsOnlyDescription.color = new Color(0.65f, 0.65f, 0.65f);
            _onlineTab.AddItems(friendsOnlyDescription);

            // ============================================================
            // ONLINE TAB — BANNED PLAYERS MANAGEMENT
            // ============================================================
            if (_bannedSearchConfig == null)
            {
                _bannedSearchConfig = this.config.Bind<string>("bannedSearchQuery", "", new ConfigurableInfo("Search banned players"));
            }

            float bannedTitleY = 480f;
            float bannedRow1Y = 440f;

            var bannedTitle = new OpLabel(
                new Vector2(20f + searchRowOffsetX + 124f, bannedTitleY),
                new Vector2(350f, 20f),
                "BANNED SKIN LIST",
                FLabelAlignment.Center,
                false
            );
            _onlineTab.AddItems(bannedTitle);

            _bannedSearchBox = new OpTextBox(_bannedSearchConfig, new Vector2(20f + searchRowOffsetX, bannedRow1Y), 200f);
            _bannedSearchBox.OnValueChanged += (sender, oldV, newV) => RefreshBannedList();
            _onlineTab.AddItems(_bannedSearchBox);

            _bannedModeButton = new OpSimpleButton(
                new Vector2(230f + searchRowOffsetX, bannedRow1Y),
                new Vector2(140f, 24f),
                _searchBannedBySteamId ? "Player ID" : "Name"
            );
            _bannedModeButton.OnClick += (_) =>
            {
                _searchBannedBySteamId = !_searchBannedBySteamId;
                _bannedModeButton.text = _searchBannedBySteamId ? "Player ID" : "Name";
                RefreshBannedList();
            };
            _onlineTab.AddItems(_bannedModeButton);

            float bannedScrollY = 30f;
            float bannedScrollHeight = 385f;
            float bannedContentHeight = 100f;

            _bannedScrollBox = new OpScrollBox(
                new Vector2(0f, bannedScrollY),
                new Vector2(600f, bannedScrollHeight),
                bannedContentHeight,
                false,
                true,
                true
            )
            {
                colorEdge = MenuColorEffect.rgbMediumGrey,
                colorFill = MenuColorEffect.rgbBlack,
                fillAlpha = 0.3f
            };
            _onlineTab.AddItems(_bannedScrollBox);

            RefreshBannedList();

            // ============================================================
            // LOCAL TAB — LOCAL PROFILE MANAGEMENT
            // ============================================================
            if (_searchConfig == null)
            {
                _searchConfig = this.config.Bind<string>("searchQuery", "", new ConfigurableInfo("Search query"));
            }

            var titleLabel = new OpLabel(
                new Vector2(20f + searchRowOffsetX + 124f, titleY),
                new Vector2(350f, 20f),
                "PROFILE MANAGER",
                FLabelAlignment.Center,
                false
            );
            _profilesTab.AddItems(titleLabel);

            _searchBox = new OpTextBox(_searchConfig, new Vector2(20f + searchRowOffsetX, row1Y), 200f);
            _searchBox.OnValueChanged += (sender, oldV, newV) => RefreshList();
            _profilesTab.AddItems(_searchBox);

            _modeButton = new OpSimpleButton(
                new Vector2(230f + searchRowOffsetX, row1Y),
                new Vector2(140f, 24f),
                _searchBySteamId ? "Player ID" : "Profile #"
            );
            _modeButton.OnClick += (_) =>
            {
                _searchBySteamId = !_searchBySteamId;
                _modeButton.text = _searchBySteamId ? "Player ID" : "Profile #";
                RefreshList();
            };
            _profilesTab.AddItems(_modeButton);

            float scrollY = 30f;
            float scrollHeight = 460f;
            float contentHeight = 100f;

            _scrollBox = new OpScrollBox(
                new Vector2(0f, scrollY),
                new Vector2(600f, scrollHeight),
                contentHeight,
                false,
                true,
                true
            )
            {
                colorEdge = MenuColorEffect.rgbMediumGrey,
                colorFill = MenuColorEffect.rgbBlack,
                fillAlpha = 0.3f
            };
            _profilesTab.AddItems(_scrollBox);

            if (_unloadMethod == null)
            {
                _unloadMethod = typeof(UIelement).GetMethod("Unload",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            MeadowProfileManager.DeleteOrphanProfiles();
            RefreshList();
        }

        // ============================================================
        // HELPER: UNLOAD ELEMENT
        // ============================================================
        private void UnloadElement(UIelement element)
        {
            if (element == null) return;
            try
            {
                _unloadMethod?.Invoke(element, null);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error unloading element: {ex.Message}");
            }
        }

        private void RefreshList()
        {
            try
            {
                // ============================================================
                // 1. ELIMINAR FILAS ACTUALES
                // ============================================================
                foreach (var row in _currentRows)
                {
                    OpScrollBox.RemoveItemsFromScrollBox(row.Label, row.DeleteButton);
                    UnloadElement(row.Label);
                    UnloadElement(row.DeleteButton);
                }
                _currentRows.Clear();

                // ============================================================
                // 2. OBTENER TODOS LOS PERFILES
                // ============================================================
                var allProfiles = MeadowProfileManager.GetAllProfileNumbers();

                string query = _searchBox.value?.Trim() ?? "";
                bool hasQuery = !string.IsNullOrEmpty(query);

                const float ROW_HEIGHT = 26f;
                const float VISIBLE_HEIGHT = 460f;

                // ============================================================
                // 3. FILTRAR Y CONTAR
                // ============================================================
                var matchingProfiles = new List<(int num, string steamId, bool orphan)>();

                foreach (int p in allProfiles)
                {
                    string sid = MeadowProfileManager.GetSteamID(p);
                    bool isOrphan = string.IsNullOrEmpty(sid);

                    bool matches = !hasQuery || (_searchBySteamId
                        ? sid.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                        : p.ToString().Contains(query));

                    if (matches) matchingProfiles.Add((p, sid, isOrphan));
                }

                // ============================================================
                // 4. ORDENAR DE MENOR A MAYOR
                // ============================================================
                matchingProfiles = matchingProfiles.OrderBy(x => x.num).ToList();

                // ============================================================
                // 5. CALCULAR ALTURA TOTAL DEL CONTENIDO
                // ============================================================
                float contentHeight = Math.Max(matchingProfiles.Count * ROW_HEIGHT + 20f, VISIBLE_HEIGHT);

                // ============================================================
                // 6. COLOCAR FILAS
                // ============================================================
                float y = contentHeight - ROW_HEIGHT - 10f;

                foreach (var (profileNum, steamId, isOrphan) in matchingProfiles)
                {
                    string display = isOrphan
                        ? $"Profile {profileNum}  [orphan]"
                        : $"Profile {profileNum}  {steamId}";

                    var label = new OpLabel(10f, y, display, false);
                    if (isOrphan)
                    {
                        label.color = Color.gray;
                    }

                    var deleteBtn = new OpSimpleButton(
                        new Vector2(350f, y - 3f),
                        new Vector2(70f, 22f),
                        "Delete"
                    );

                    int capturedNum = profileNum;
                    deleteBtn.OnClick += (_) => DeleteProfile(capturedNum);

                    _scrollBox.AddItems(label, deleteBtn);
                    _currentRows.Add(new ProfileRow
                    {
                        ProfileNumber = profileNum,
                        SteamId = steamId,
                        Label = label,
                        DeleteButton = deleteBtn
                    });

                    y -= ROW_HEIGHT;
                }

                // ============================================================
                // 7. ACTUALIZAR SCROLL
                // ============================================================
                _scrollBox.SetContentSize(contentHeight, true);
                _scrollBox.MarkDirty();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error refreshing profile list: {ex.Message}");
                Plugin.Logger.LogError(ex.StackTrace);
            }
        }

        private void DeleteProfile(int profileNumber)
        {
            try
            {
                MeadowProfileManager.DeleteProfile(profileNumber);
                MeadowProfileManager.RemoveAssignment(profileNumber);
                RefreshList();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error deleting profile {profileNumber}: {ex.Message}");
            }
        }

        // ============================================================
        // LISTA DE BANEADOS
        // ============================================================
        private void RefreshBannedList()
        {
            try
            {
                foreach (var row in _bannedRows)
                {
                    OpScrollBox.RemoveItemsFromScrollBox(row.Label, row.DeleteButton);
                    UnloadElement(row.Label);
                    UnloadElement(row.DeleteButton);
                }
                _bannedRows.Clear();

                var allBanned = SkinBanManager.GetAllBanned();

                string query = _bannedSearchBox.value?.Trim() ?? "";
                bool hasQuery = !string.IsNullOrEmpty(query);

                const float ROW_HEIGHT = 26f;
                const float VISIBLE_HEIGHT = 385f;

                var matching = new List<(string name, string id)>();
                foreach (var (name, id) in allBanned)
                {
                    bool matches = !hasQuery || (_searchBannedBySteamId
                        ? id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                        : name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (matches) matching.Add((name, id));
                }

                matching = matching
                    .OrderBy(x => string.IsNullOrEmpty(x.name))
                    .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.id, StringComparer.Ordinal)
                    .ToList();

                float contentHeight = Math.Max(matching.Count * ROW_HEIGHT + 20f, VISIBLE_HEIGHT);

                float y = contentHeight - ROW_HEIGHT - 10f;

                foreach (var (name, id) in matching)
                {
                    string display = string.IsNullOrEmpty(name) ? id : $"{name}    {id}";

                    var label = new OpLabel(10f, y, display, false);

                    var deleteBtn = new OpSimpleButton(
                        new Vector2(350f, y - 3f),
                        new Vector2(70f, 22f),
                        "Delete"
                    );

                    string capturedId = id;
                    deleteBtn.OnClick += (_) => DeleteBanned(capturedId);

                    _bannedScrollBox.AddItems(label, deleteBtn);
                    _bannedRows.Add(new BannedRow
                    {
                        BannedId = id,
                        BannedName = name,
                        Label = label,
                        DeleteButton = deleteBtn
                    });

                    y -= ROW_HEIGHT;
                }

                _bannedScrollBox.SetContentSize(contentHeight, true);
                _bannedScrollBox.MarkDirty();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error refreshing banned list: {ex.Message}");
                Plugin.Logger.LogError(ex.StackTrace);
            }
        }

        private void DeleteBanned(string identity)
        {
            try
            {
                bool removed = SkinBanManager.RemoveBan(identity);
                if (removed)
                {
                    Plugin.Logger.LogInfo($"🚫 {identity} quitada de la lista negra (desbaneado). Solicitando re-handshake...");
                    SkinSerializer.RequestHandshakeFrom(identity);
                }
                RefreshBannedList();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error removing ban {identity}: {ex.Message}");
            }
        }
    }
}