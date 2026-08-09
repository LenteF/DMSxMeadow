using Menu;
using Menu.Remix;
using Menu.Remix.MixedUI;
using System;
using UnityEngine;

namespace DMSxMeadow
{
    public class MeadowProfileUI
    {
        private DressMySlugcat.FancyMenu _fancyMenu;
        private SelectOneButton _meadowModeButton;
        private OpTextBox _steamIdField;
        private OpTextBox _profileNumberField;
        private SimpleButton _profileSetButton;
        private MenuLabel _statusLabel;
        private MenuLabel _steamLabel;
        private MenuLabel _profileLabel;

        private MenuTabWrapper _tabWrapper;
        private bool _uiAdded = false;

        private bool _profileFieldWasHeld = false;
        private bool _steamFieldWasHeld = false;
        private string _lastConfirmedSteamId = "";
        private string _lastConfirmedProfileNumber = "";
        private string _pendingProfileInput = "1";
        private string _lastKnownSlugcat = "";

        private static System.Reflection.FieldInfo _uiConfigValueField;

        public MeadowProfileUI(DressMySlugcat.FancyMenu fancyMenu)
        {
            _fancyMenu = fancyMenu;
        }

        public void Initialize()
        {
            if (_uiAdded) return;

            try
            {
                var textBoxBorderField = _fancyMenu.GetType()
                    .GetField("textBoxBorder",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance);

                if (textBoxBorderField == null)
                {
                    return;
                }

                var textBoxBorder = textBoxBorderField?.GetValue(_fancyMenu) as RoundedRect;
                if (textBoxBorder == null)
                {
                    return;
                }

                var playerButtonsField = _fancyMenu.GetType()
                    .GetField("playerButtons",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance);

                int playerCount = 4;
                if (playerButtonsField != null)
                {
                    var playerButtons = playerButtonsField.GetValue(_fancyMenu) as Array;
                    if (playerButtons != null)
                    {
                        playerCount = playerButtons.Length;
                    }
                }

                // ============================================================
                // POSICIONAMIENTO RESPONSIVO A RESOLUCIÓN
                // ============================================================
                // textBoxBorder.pos.x YA incluye leftAnchor.
                // NO restamos leftAnchor para que el desplazamiento se mantenga.
                // ============================================================
                float baseStartX = textBoxBorder.pos.x + (65f * playerCount) + 10f;

                // ============================================================
                // LIMITACIÓN DE SEGURIDAD: evitar invadir el área de los botones
                // de control de DMS (reset/defaults/copy/paste) que se anclan
                // al borde derecho de la caja en resoluciones angostas.
                // ============================================================
                float maxSafeX = textBoxBorder.pos.x + textBoxBorder.size.x - 260f;
                baseStartX = Mathf.Min(baseStartX, maxSafeX);

                // ============================================================
                // AJUSTE GENERAL DE POSICIÓN (X)
                // ============================================================
                float positionOffsetX = -10f;
                baseStartX += positionOffsetX;

                // ============================================================
                // FILA PROPIA: Y = -80f (debajo de Player buttons, lejos de
                // los botones de control en Y = +20f)
                // ============================================================
                float baseYPos = textBoxBorder.pos.y - 75f;

                // ============================================================
                // CREACIÓN DE UI
                // ============================================================

                _tabWrapper = new MenuTabWrapper(_fancyMenu, _fancyMenu.pages[0]);
                _fancyMenu.pages[0].subObjects.Add(_tabWrapper);

                var meadowArray = new SelectOneButton[1];
                _meadowModeButton = new SelectOneButton(
                    _fancyMenu,
                    _fancyMenu.pages[0],
                    "MEADOW",
                    "MEADOW_SERIES",
                    new Vector2(baseStartX, baseYPos + 35f),
                    new Vector2(80f, 30f),
                    meadowArray,
                    0
                );
                meadowArray[0] = _meadowModeButton;
                _fancyMenu.pages[0].subObjects.Add(_meadowModeButton);

                float yOffset = 0f;

                // ============================================================
                // AJUSTE DE POSICIÓN SOLO SECCIÓN (Y)
                // ============================================================
                float sectionOffsetY = 25f;

                // ============================================================
                // AJUSTE DE POSICIÓN SOLO SECCIÓN (X)
                // ============================================================
                float sectionOffsetX = 12f;

                _profileLabel = new MenuLabel(
                    _fancyMenu,
                    _fancyMenu.pages[0],
                    "Profile:",
                    new Vector2(baseStartX + sectionOffsetX + 3f, baseYPos + 35f + yOffset + 35f + sectionOffsetY),
                    new Vector2(60f, 20f),
                    false
                );
                _fancyMenu.pages[0].subObjects.Add(_profileLabel);

                var dummyOI = new DressMySlugcat.DMSOptions();
                var profileNumberConfig = new Configurable<string>(
                    dummyOI,
                    "meadow_profile_number",
                    "1",
                    null
                );
                _profileNumberField = new OpTextBox(
                    profileNumberConfig,
                    new Vector2(baseStartX + 86f + sectionOffsetX, baseYPos + 35f + yOffset + 35f + sectionOffsetY),
                    60f
                );
                _profileNumberField.allowSpace = false;
                _profileNumberField.maxLength = 3;
                _profileNumberField.greyedOut = true;
                new UIelementWrapper(_tabWrapper, _profileNumberField);

                _profileSetButton = new SimpleButton(
                    _fancyMenu,
                    _fancyMenu.pages[0],
                    "SET",
                    "PROFILE_SET",
                    new Vector2(baseStartX + 151f + sectionOffsetX, baseYPos + 35f + yOffset + 35f + sectionOffsetY),
                    new Vector2(40f, 30f)
                );
                _fancyMenu.pages[0].subObjects.Add(_profileSetButton);
                _profileSetButton.inactive = true;

                yOffset = 35f;

                float playerIdOffsetX = -9f;

                _steamLabel = new MenuLabel(
                    _fancyMenu,
                    _fancyMenu.pages[0],
                    "Player ID:",
                    new Vector2(baseStartX + playerIdOffsetX + sectionOffsetX + 3f, baseYPos + 35f + yOffset + 35f + sectionOffsetY),
                    new Vector2(90f, 20f),
                    false
                );
                _fancyMenu.pages[0].subObjects.Add(_steamLabel);

                var steamConfig = new Configurable<string>(
                    dummyOI,
                    "meadow_steamid",
                    "",
                    null
                );
                _steamIdField = new OpTextBox(
                    steamConfig,
                    new Vector2(baseStartX + 95f + playerIdOffsetX + sectionOffsetX, baseYPos + 35f + yOffset + 35f + sectionOffsetY),
                    130f
                );
                _steamIdField.allowSpace = true;
                _steamIdField.maxLength = 17;
                _steamIdField.greyedOut = true;
                _steamIdField.value = "unassigned";
                _steamIdField.OnValueChanged += (sender, oldV, newV) =>
                {
                    if (newV.Length > 18)
                    {
                        _steamIdField.value = newV.Substring(0, 18);
                    }
                };
                new UIelementWrapper(_tabWrapper, _steamIdField);

                yOffset = 70f;

                _statusLabel = new MenuLabel(
                    _fancyMenu,
                    _fancyMenu.pages[0],
                    "",
                    new Vector2(baseStartX + sectionOffsetX, baseYPos + 35f + yOffset + 35f + sectionOffsetY),
                    new Vector2(250f, 20f),
                    false
                );
                _fancyMenu.pages[0].subObjects.Add(_statusLabel);

                _uiAdded = true;

                _profileNumberField.OnValueChanged += OnProfileNumberChanged;

                _lastKnownSlugcat = _fancyMenu.selectedSlugcat;

                if (_uiConfigValueField == null)
                {
                    _uiConfigValueField = typeof(UIconfig).GetField("_value",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error initializing meadow UI: {ex.Message}");
                Plugin.Logger.LogError(ex.StackTrace);
            }
        }

        private void OnProfileNumberChanged(UIconfig sender, string oldValue, string newValue)
        {
            if (string.IsNullOrEmpty(newValue)) return;

            if (!int.TryParse(newValue, out int n) || n < 1 || n > 99)
            {
                _profileNumberField.value = oldValue;
                return;
            }

            _pendingProfileInput = newValue;
        }

        public void CheckSlugcatChange()
        {
            if (!MeadowProfileManager.IsMeadowModeActive) return;
            string currentSlugcat = _fancyMenu.selectedSlugcat;
            if (currentSlugcat != _lastKnownSlugcat)
            {
                _lastKnownSlugcat = currentSlugcat;
                LoadProfile(MeadowProfileManager.CurrentProfileNumber);
            }
        }

        private void SetProfileNumber()
        {
            if (!MeadowProfileManager.IsMeadowModeActive)
            {
                _statusLabel.text = "Meadow mode is OFF";
                return;
            }

            string input = _pendingProfileInput;

            if (string.IsNullOrEmpty(input) || !int.TryParse(input, out int profileNumber))
            {
                _statusLabel.text = "Invalid profile number";
                return;
            }

            if (profileNumber < 1 || profileNumber > 99)
            {
                _statusLabel.text = "Profile must be 1-99";
                return;
            }

            if (profileNumber == MeadowProfileManager.CurrentProfileNumber)
            {
                _statusLabel.text = $"Already on profile {profileNumber}";
                return;
            }

            SaveCurrentProfile();

            MeadowProfileManager.SetCurrentProfile(profileNumber);
            _profileNumberField.value = profileNumber.ToString();
            _pendingProfileInput = profileNumber.ToString();

            LoadProfile(profileNumber);

            string steamId = MeadowProfileManager.GetSteamID(profileNumber);
            _steamIdField.value = string.IsNullOrEmpty(steamId) ? "unassigned" : steamId;

            _lastConfirmedSteamId = steamId;
            _lastConfirmedProfileNumber = profileNumber.ToString();
        }

        public void SaveCurrentProfile()
        {
            try
            {
                if (!MeadowProfileManager.IsMeadowModeActive) return;

                var customization = GetLiveCustomization();
                if (customization != null)
                {
                    string slugcatName = _fancyMenu.selectedSlugcat;
                    MeadowProfileManager.SaveCurrentProfile(slugcatName, customization);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error saving profile: {ex.Message}");
            }
        }

        private DressMySlugcat.Customization GetLiveCustomization()
        {
            try
            {
                return DressMySlugcat.Customization.For(
                    _fancyMenu.selectedSlugcat,
                    _fancyMenu.selectedPlayerIndex,
                    false
                );
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error getting live customization: {ex.Message}");
                return null;
            }
        }

        private void LoadProfile(int displayNumber)
        {
            try
            {
                string slugcatName = _fancyMenu.selectedSlugcat;
                var customization = MeadowProfileManager.GetProfileCustomization(displayNumber, slugcatName);
                var live = GetLiveCustomization();
                if (live == null)
                {
                    return;
                }

                if (customization != null)
                {
                    live.CustomTail.Length = customization.CustomTail.Length;
                    live.CustomTail.Wideness = customization.CustomTail.Wideness;
                    live.CustomTail.Roundness = customization.CustomTail.Roundness;
                    live.CustomTail.Lift = customization.CustomTail.Lift;
                    live.CustomTail.Color = customization.CustomTail.Color;
                    live.CustomTail.CustTailShape = customization.CustomTail.CustTailShape;
                    live.CustomTail.AsymTail = customization.CustomTail.AsymTail;

                    live.CustomSprites.Clear();
                    foreach (var sprite in customization.CustomSprites)
                    {
                        live.CustomSprites.Add(new DressMySlugcat.CustomSprite
                        {
                            Sprite = sprite.Sprite,
                            SpriteSheetID = sprite.SpriteSheetID,
                            ColorHex = sprite.ColorHex,
                            Enforce = sprite.Enforce
                        });
                    }
                }
                else
                {
                    live.CustomSprites.Clear();
                    live.CustomTail = new DressMySlugcat.CustomTail();
                }

                _lastKnownSlugcat = slugcatName;
                RefreshDummyAndControls();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error loading profile: {ex.Message}");
            }
        }

        private void RefreshDummyAndControls()
        {
            try
            {
                var dummyField = _fancyMenu.GetType()
                    .GetField("slugcatDummy",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance);

                var dummy = dummyField?.GetValue(_fancyMenu);
                if (dummy != null)
                {
                    var updateMethod = dummy.GetType().GetMethod("UpdateSprites",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);

                    if (updateMethod != null)
                    {
                        updateMethod.Invoke(dummy, null);
                    }
                }

                var updateControlsMethod = _fancyMenu.GetType()
                    .GetMethod("UpdateControls",
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance);
                updateControlsMethod?.Invoke(_fancyMenu, null);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error refreshing dummy: {ex.Message}");
            }
        }

        public void CheckFieldFocusLoss()
        {
            try
            {
                bool profileHeld = GetHeld(_profileNumberField);
                if (_profileFieldWasHeld && !profileHeld)
                {
                    string currentProfile = MeadowProfileManager.CurrentProfileNumber.ToString();
                    if (_profileNumberField.value != currentProfile)
                    {
                        _profileNumberField.value = currentProfile;
                    }
                }
                _profileFieldWasHeld = profileHeld;

                bool steamHeld = GetHeld(_steamIdField);

                if (!_steamFieldWasHeld && steamHeld)
                {
                    if (_steamIdField.value == "unassigned")
                    {
                        _steamIdField.value = "";
                    }
                }

                if (_steamFieldWasHeld && !steamHeld)
                {
                    string currentSteamId = MeadowProfileManager.GetSteamID(MeadowProfileManager.CurrentProfileNumber);
                    string fieldValue = _steamIdField.value;
                    string cleanValue = (fieldValue == "unassigned") ? "" : fieldValue;

                    if (cleanValue != currentSteamId)
                    {
                        MeadowProfileManager.SetSteamID(MeadowProfileManager.CurrentProfileNumber, cleanValue);
                        _lastConfirmedSteamId = cleanValue;

                        if (!string.IsNullOrEmpty(cleanValue))
                        {
                            _statusLabel.text = "Player ID saved";
                            SaveCurrentProfile();
                        }
                        else
                        {
                            MeadowProfileManager.DeleteProfile(MeadowProfileManager.CurrentProfileNumber);
                            _statusLabel.text = "Profile deleted";
                        }
                    }

                    string displayValue = string.IsNullOrEmpty(cleanValue) ? "unassigned" : cleanValue;
                    _steamIdField.value = displayValue;
                }
                _steamFieldWasHeld = steamHeld;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error in CheckFieldFocusLoss: {ex.Message}");
            }
        }

        private bool GetHeld(OpTextBox field)
        {
            if (field == null) return false;

            try
            {
                var heldProp = typeof(UIfocusable).GetProperty("held",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);

                if (heldProp != null)
                {
                    return (bool)(heldProp.GetValue(field) ?? false);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Error getting Held property: {ex.Message}");
            }

            return false;
        }

        public void ActivateMeadowMode()
        {
            if (MeadowProfileManager.IsMeadowModeActive)
            {
                return;
            }

            MeadowProfileManager.IsMeadowModeActive = true;

            _profileNumberField.greyedOut = false;
            _steamIdField.greyedOut = false;
            _profileSetButton.inactive = false;

            int profileNumber = MeadowProfileManager.CurrentProfileNumber;
            _profileNumberField.value = profileNumber.ToString();
            _pendingProfileInput = profileNumber.ToString();

            string steamId = MeadowProfileManager.GetSteamID(profileNumber);
            _steamIdField.value = string.IsNullOrEmpty(steamId) ? "unassigned" : steamId;

            _lastConfirmedSteamId = steamId;
            _lastConfirmedProfileNumber = profileNumber.ToString();

            _lastKnownSlugcat = _fancyMenu.selectedSlugcat;
            LoadProfile(profileNumber);

            RefreshDummyAndControls();
        }

        public void DeactivateMeadowMode()
        {
            if (!MeadowProfileManager.IsMeadowModeActive)
            {
                return;
            }

            SaveCurrentProfile();

            _profileNumberField.greyedOut = true;
            _steamIdField.greyedOut = true;
            _profileSetButton.inactive = true;

            _lastConfirmedSteamId = "";
            _lastConfirmedProfileNumber = "";
            _profileFieldWasHeld = false;
            _steamFieldWasHeld = false;
            _lastKnownSlugcat = "";

            MeadowProfileManager.IsMeadowModeActive = false;
            _statusLabel.text = "";

            RefreshDummyAndControls();
        }

        public void ToggleMeadowMode()
        {
            if (!MeadowProfileManager.IsMeadowModeActive)
            {
                ActivateMeadowMode();
                _fancyMenu.PlaySound(SoundID.MENU_Switch_Page_Out);
            }
        }

        public void HandleSignal(string message)
        {
            if (message == "MEADOW_TOGGLE")
            {
                ToggleMeadowMode();
            }
            else if (message == "PROFILE_SET")
            {
                SetProfileNumber();
            }
        }

        public void ForceDeactivateMeadowMode()
        {
            if (!MeadowProfileManager.IsMeadowModeActive) return;

            SaveCurrentProfile();

            _profileNumberField.greyedOut = true;
            _steamIdField.greyedOut = true;
            _profileSetButton.inactive = true;

            _lastConfirmedSteamId = "";
            _lastConfirmedProfileNumber = "";
            _profileFieldWasHeld = false;
            _steamFieldWasHeld = false;
            _lastKnownSlugcat = "";

            MeadowProfileManager.IsMeadowModeActive = false;
            _statusLabel.text = "";

            RefreshDummyAndControls();
        }

        // ============================================================
        // Manejo de Ctrl+V para pegar desde portapapeles
        // ============================================================
        public void CheckPasteInput()
        {
            if (!MeadowProfileManager.IsMeadowModeActive) return;
            if (_steamIdField == null) return;

            bool steamHeld = GetHeld(_steamIdField);
            if (!steamHeld) return;

            if (Input.GetKeyDown(KeyCode.V) &&
                (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            {
                string clipboard = GUIUtility.systemCopyBuffer;
                if (!string.IsNullOrEmpty(clipboard))
                {
                    string clean = clipboard.Trim();

                    if (_uiConfigValueField != null)
                    {
                        _uiConfigValueField.SetValue(_steamIdField, clean);
                        _steamIdField.Change();
                    }
                    else
                    {
                        _steamIdField.value = clean;
                    }
                }
            }
        }
    }
}