# DMSxMeadow

A high-performance **BepInEx code mod** for *Rain World* that bridges **Dress My Slugcat (DMS)** cosmetics with **Rain Meadow** multiplayer.

Instead of relying on local player slots (which leads to skin cloning in online lobbies), **DMSxMeadow** implements a full **Asynchronous Custom Skin Transfer Protocol**, local memory caching, safety/validation guards, and dynamic UI hooks for both legacy and revamped Rain Meadow interfaces.

---

## Key Features & Technical Highlights

* **Decoupled Local Profile Management (`MeadowProfileManager`):** Maps DMS profile customizations directly to Rain Meadow Steam/Online IDs rather than local gamepad slots, completely eliminating the skin-cloning issue in multiplayer.
* **Pre-Export File Validation (`SkinPartGuard`):** Inspects and validates local sprite files, atlas assignments, and skin assets *before* they are packed into Data Transfer Objects, preventing broken or corrupted files from being transmitted over the network.
* **Handshake Metadata Negotiation:** Session events trigger an initial lightweight handshake containing sender metadata (`SteamID`, `SlugcatName`, `ShareSkin`, `RequiredSpriteSheetId`, and `CustomizationJson`).
* **Pull-Request Asset Transfer Protocol:** If a recipient player does not possess the sender's skin locally, `SkinTransfer` initiates an authenticated pull request utilizing key verification and a 6-attempt retry mechanic.
* **On-the-Fly DTO Export & Memory Registration:** When requested, `SkinRegistration.ExportEquippedSkinToDTO` converts validated custom skin assets into network-ready payloads. Upon arrival, the receiver unpacks and caches the skin exclusively in memory.
* **Security & Blacklist Validation (`SkinBanManager`):** Receivers cross-check incoming handshakes against `blacklist.txt` and inspect the `ShareSkin` flag prior to initiating any transfer request.
* **Memory Lifecycle & Cleanup:** Dynamically registered skins reside in runtime memory during active sessions and are completely cleared upon returning to the Main Menu to prevent texture leakage and state overlap.
* **Dedicated UI Controls & Privacy Indicators:** Adds a local spectator button (`SpectatorPlayerButtonHook`) to easily toggle/ban specific player skins on the fly, alongside a client-side visual indicator (`StoryLobbyShareSkinIndicator`) displaying the local status of the `ShareSkin` setting.
* **Independent Identity Color Parameter:** Introduces a dedicated `identity` color parameter alongside skin and eye colors. This allows players to customize the color of their Nameplate, slugcat HUD icon, chat username, and pipe travel highlights completely independently from their slugcat's body skin color.

---

## 🛠️ Technical Architecture

### 1. Persistent Profile & Customization Mapping (`MeadowProfileManager`)
* **Offset Mapping:** Isolates custom Meadow profiles from local game options using a fixed offset (`PROFILE_OFFSET = 4`), storing persistence mappings in `meadowcustom.dat` and `dmsxmeadow.txt`.
* **Runtime Interception:** Detours `DressMySlugcat.Customization.For(Player, bool)`, resolving the `abstractCreature` back to its network owner ID. Forces `PlayerNumber = 0` on returning instances to prevent gamepad polling crashes.

### 2. Validation & Security (`SkinPartGuard` & `SkinBanManager`)
* **Asset Sanitization (`SkinPartGuard`):** Acts as a pre-export filter that verifies sprite elements, textures, and atlas integrity prior to serializing skins for network delivery.
* **Blacklist Filtering (`SkinBanManager`):** Parses `blacklist.txt` on startup. If a sender's Steam ID or skin name is flagged, the handshake is rejected and rendering falls back safely to default Slugcat graphics.

### 3. Handshake & Pull-Request Pipeline
* **Handshake Phase:** `SkinSerializer` builds and transmits initial metadata parameters.
* **Verification:** The receiver checks `blacklist.txt`, `ShareSkin` status, and local DMS disk/memory registration.
* **Pull Request:** If missing, `SkinTransfer` manages a key-authenticated request loop (up to 6 max attempts) back to the sender.
* **Export & Memory Storage:** The sender validates local assets via `SkinPartGuard`, exports them with `ExportEquippedSkinToDTO`, and transmits the DTO. The receiver unpacks the payload directly into volatile session memory.

### 4. UI Controls & Icon Compatibility Engine
* **Spectator Skin Ban Button (`SpectatorPlayerButtonHook`):** Injects a dedicated UI button into the spectator interface, allowing users to quickly ban/block the skin rendering of specific players during gameplay.
* **Local ShareSkin Indicator (`StoryLobbyShareSkinIndicator`):** Renders a local, client-side UI indicator in story lobbies to remind the user whether their `ShareSkin` setting is currently active.
* **Identity & UI Color Engine (`FancyMenuHookHandler` & `PlayerNameColorHooks`):** Hooks into Rain Meadow's UI to apply the custom `identity` color parameter across nameplates, chat tags, pipe travel indicators, and HUD icons without modifying the slugcat's underlying body/skin palette.

---

## ⚙️ Compilation Notes
Target framework: **.NET Framework 4.8**
Dependencies required for compilation:
* `0Harmony.dll`
* `BepInEx.dll`
* `com.rlabrecque.steamworks.net.dll`
* `DressMySlugcat.dll`
* `HOOKS-Assembly-CSharp.dll`
* `Mono.Cecil.dll`
* `MonoMod.RuntimeDetour.dll`
* `MonoMod.Utils.dll`
* `Newtonsoft.json.dll`
* `PUBLIC-Assembly-CSharp.dll`
* `RainMeadow.dll`
* `UnityEngine.dll`
* `UnityEngine.CoreModule.dll`
* `UnityEngine.IMGUIModule.dll`
* `UnityEngine.InputLegacyModule`
