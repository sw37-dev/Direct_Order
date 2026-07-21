using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

public class Navigation : Script
{
    private const int FRANKLIN_HASH = -1692214353;
    private const int MICHAEL_HASH = 225514697;
    private const int TREVOR_HASH = -1686040670;

    private const int CONTACT_DIAL_TIMEOUT_MS = 2000;
    private const int MENU_IDLE_INTERVAL_MS = 1000;
    private const int MENU_VISIBLE_INTERVAL_MS = 0;
    private const int ROUTE_ACTIVE_INTERVAL_MS = 250;

    private const float ROUTE_CLEAR_RADIUS = 10.0f;
    private const int STATE_SAVE_BACKOFF_MS = 5000;

    // GTA V blip colors are documented in the 0..85 range.
    private const int MIN_ROUTE_COLOR_ID = 0;
    private const int MAX_ROUTE_COLOR_ID = 85;

    private static readonly string NavigationDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Navigation");

    private static readonly string NavigationStateFilePrefix = "navigation_state_";

    private static readonly string PersistentManagerDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PersistentManager");

    private static readonly string PersistentVehiclesFile = Path.Combine(PersistentManagerDataFolder, "persistent_vehicles.txt");
    private static readonly string DestroyedVehiclesFile = Path.Combine(PersistentManagerDataFolder, "persistent_destroyed_vehicles.txt");

    private static string L(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    private readonly ObjectPool _uiPool = new ObjectPool();
    private readonly Random _rng = new Random();

    private CustomiFruit _phoneInstance;
    private iFruitContact _navContact;
    private bool _contactBound;

    private NativeMenu _infoMenu;
    private NativeMenu _routeMenu;

    private bool _infoMenuReady;
    private bool _routeMenuReady;

    private bool _routeSelectionSync;
    private VehicleSnapshot _selectedRouteVehicle;

    private readonly Dictionary<int, NavigationState> _states = new Dictionary<int, NavigationState>();
    private int _currentOwnerHash = 0;
    private int _nextStateSaveAt = 0;

    private static readonly string ContactName = L("Navigation_ContactName", "AI điều hướng");
    private static readonly string MainTitle = L("Navigation_MainTitle", "Information");
    private static readonly string MainSubtitle = L("Navigation_MainSubtitle", "THÔNG TIN TỔNG QUAN");
    private static readonly string RouteTitle = L("Navigation_RouteTitle", "Route Navigation");
    private static readonly string RouteSubtitle = L("Navigation_RouteSubtitle", "ĐIỀU HƯỚNG TÌM PHƯƠNG TIỆN");

    private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly int[] ROUTE_COLOR_POOL = Enumerable
        .Range(MIN_ROUTE_COLOR_ID, MAX_ROUTE_COLOR_ID - MIN_ROUTE_COLOR_ID + 1)
        // Avoid colors that are effectively transparent / not useful for a GPS line.
        .Where(id => id != 72 && 
        id != 85 && 
        id != 40 && 
        id != 52 && 
        id != 54 && 
        id != 58 && 
        id != 10 && 
        id != 6 && 
        id != 21 && 
        id != 25 && 
        id != 56 && 
        id != 78 &&
        id != 12 &&
        id != 13 &&
        id != 14 &&
        id != 20 &&
        id != 22 &&
        id != 39 &&
        id != 55 &&
        id != 62 &&
        id != 65 &&
        id != 76)
        .ToArray();

    private sealed class VehicleSnapshot
    {
        public string Key = "";
        public uint ModelHash;
        public int OwnerHash;
        public string DisplayName = "";
        public string Plate = "";
        public Vector3 Position;
        public float Heading;
        public bool IsDestroyed;
    }

    private sealed class NavigationState
    {
        public int OwnerHash;
        public bool Active;
        public string TargetKey = "";
        public string TargetName = "";
        public uint TargetModelHash;
        public Vector3 TargetPosition;
        public float TargetHeading;
        public Vector3 LastAppliedWaypointPos = Vector3.Zero;
        public bool WaypointApplied;
        public bool Dirty;
        public int RouteColorId = -1;
        public Blip RouteBlip;
        public bool RouteConfigured;
        public int RouteColorApplied = -1;
    }

    public Navigation()
    {
        Interval = MENU_IDLE_INTERVAL_MS;
        Tick += OnTick;
        KeyDown += OnKeyDown;
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            EnsureContactRegistered();
            SyncCurrentCharacterContext();
            ProcessCurrentCharacterRoute();
            FlushDirtyStatesIfNeeded();

            bool menuVisible = IsAnyMenuVisible();
            bool routeActive = IsCurrentRouteActive();

            if (menuVisible)
            {
                Interval = MENU_VISIBLE_INTERVAL_MS;
                _uiPool.Process();
            }
            else
            {
                Interval = routeActive ? ROUTE_ACTIVE_INTERVAL_MS : MENU_IDLE_INTERVAL_MS;
            }
        }
        catch
        {
            Interval = MENU_IDLE_INTERVAL_MS;
        }
    }

    private bool IsCurrentRouteActive()
    {
        try
        {
            if (_currentOwnerHash == 0)
                return false;

            return _states.TryGetValue(_currentOwnerHash, out NavigationState state) && state.Active;
        }
        catch
        {
            return false;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if ((e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape) && IsAnyMenuVisible())
                CloseCurrentVisibleMenu();
        }
        catch
        {
        }
    }

    private int PickRandomRouteColorId()
    {
        try
        {
            if (ROUTE_COLOR_POOL == null || ROUTE_COLOR_POOL.Length == 0)
                return (int)BlipColor.Purple;

            return ROUTE_COLOR_POOL[_rng.Next(ROUTE_COLOR_POOL.Length)];
        }
        catch
        {
            return (int)BlipColor.Purple;
        }
    }

    private static bool IsUsableRouteColorId(int colorId)
    {
        return colorId >= MIN_ROUTE_COLOR_ID &&
               colorId <= MAX_ROUTE_COLOR_ID &&
               colorId != 72 &&
               colorId != 40 &&
               colorId != 52 &&
               colorId != 54 &&
               colorId != 58 &&
               colorId != 76 &&
               colorId != 10 &&
               colorId != 6 &&
               colorId != 21 &&
               colorId != 25 &&
               colorId != 56 &&
               colorId != 78 &&
               colorId != 12 &&
               colorId != 13 &&
               colorId != 14 &&
               colorId != 20 &&
               colorId != 22 &&
               colorId != 39 &&
               colorId != 55 &&
               colorId != 62 &&
               colorId != 65 &&
               colorId != 85;
    }

    private static int NormalizeRouteColorId(int colorId)
    {
        try
        {
            if (IsUsableRouteColorId(colorId))
                return colorId;

            if (colorId < MIN_ROUTE_COLOR_ID)
                return (int)BlipColor.Purple;

            if (colorId > MAX_ROUTE_COLOR_ID)
                return (int)BlipColor.Purple;

            // If caller passes a transparent / unusable ID, fall back to a visible color.
            return (int)BlipColor.Purple;
        }
        catch
        {
        }

        return (int)BlipColor.Green;
    }

    private void EnsureContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_phoneInstance, phone))
            {
                _phoneInstance = phone;
                _navContact = null;
                _contactBound = false;
            }

            if (_navContact == null)
            {
                _navContact = phone.Contacts.FirstOrDefault(c =>
                    c != null &&
                    string.Equals(c.Name, ContactName, StringComparison.OrdinalIgnoreCase));
            }

            if (_navContact == null)
            {
                _navContact = new iFruitContact(ContactName)
                {
                    Active = true,
                    DialTimeout = CONTACT_DIAL_TIMEOUT_MS,
                    Bold = false,
                    Icon = ContactIcon.GangApp
                };

                _navContact.Answered += OnNavigationContactAnswered;
                phone.Contacts.Add(_navContact);
                _contactBound = true;
                return;
            }

            if (!_contactBound)
            {
                _navContact.Answered += OnNavigationContactAnswered;
                _contactBound = true;
            }

            _navContact.Active = true;
        }
        catch
        {
        }
    }

    private void OnNavigationContactAnswered(iFruitContact sender)
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone != null)
            {
                try { phone.Close(0); } catch { }
            }

            OpenInformationMenu();
        }
        catch
        {
        }
    }

    private void SyncCurrentCharacterContext()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == _currentOwnerHash)
                return;

            NavigationState previousState = null;
            if (_currentOwnerHash != 0 && _states.TryGetValue(_currentOwnerHash, out previousState))
            {
                SaveState(previousState);
                previousState.WaypointApplied = false;
                previousState.LastAppliedWaypointPos = Vector3.Zero;
            }

            _currentOwnerHash = ownerHash;

            if (_currentOwnerHash == 0)
                return;

            var state = GetOrLoadState(_currentOwnerHash);

            if (state.Active)
            {
                ApplyWaypointFromState(state, forceRefresh: true);
            }
            else
            {
                ClearRouteBlipOnly(state);
                ClearGpsWaypointOnly();
            }
        }
        catch
        {
            // Có thể cân nhắc log lỗi ở đây nếu cần thiết
        }
    }

    private void ProcessCurrentCharacterRoute()
    {
        try
        {
            if (_currentOwnerHash == 0)
                return;

            if (!_states.TryGetValue(_currentOwnerHash, out NavigationState state))
                return;

            if (!state.Active)
            {
                state.WaypointApplied = false;
                state.LastAppliedWaypointPos = Vector3.Zero;
                return;
            }

            VehicleSnapshot liveTarget;
            if (TryResolveCurrentTargetSnapshot(state.TargetKey, _currentOwnerHash, out liveTarget))
            {
                if (liveTarget != null)
                {
                    bool changed =
                        state.TargetPosition.DistanceTo2D(liveTarget.Position) > 0.25f ||
                        Math.Abs(state.TargetHeading - liveTarget.Heading) > 0.25f ||
                        state.TargetModelHash != liveTarget.ModelHash ||
                        !string.Equals(state.TargetName, liveTarget.DisplayName ?? "", StringComparison.Ordinal);

                    state.TargetPosition = liveTarget.Position;
                    state.TargetHeading = liveTarget.Heading;
                    state.TargetModelHash = liveTarget.ModelHash;
                    if (!string.IsNullOrWhiteSpace(liveTarget.DisplayName))
                        state.TargetName = liveTarget.DisplayName;

                    if (changed)
                        state.Dirty = true;
                }
            }

            UpdateHiddenRouteBlipFromState(state);

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            if (state.TargetPosition == Vector3.Zero)
                return;

            float distance = player.Position.DistanceTo2D(state.TargetPosition);
            if (distance <= ROUTE_CLEAR_RADIUS)
            {
                ClearCurrentRoute(true);
            }
        }
        catch
        {
        }
    }

    private void ApplyWaypointFromState(NavigationState state, bool forceRefresh)
    {
        try
        {
            if (state == null || !state.Active)
                return;

            if (state.TargetPosition == Vector3.Zero)
                return;

            bool needSet =
                forceRefresh ||
                !state.WaypointApplied ||
                state.LastAppliedWaypointPos.DistanceTo2D(state.TargetPosition) > 0.25f;

            if (!needSet)
                return;

            EnsureHiddenRouteBlip(state);
            UpdateHiddenRouteBlipFromState(state);

            state.WaypointApplied = true;
            state.LastAppliedWaypointPos = state.TargetPosition;
        }
        catch
        {
        }
    }

    private static void SetBlipAlphaSafe(Blip blip, int alpha)
    {
        try
        {
            if (blip == null || !blip.Exists())
                return;

            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;

            Function.Call(Hash.SET_BLIP_ALPHA, blip.Handle, alpha);
        }
        catch
        {
        }
    }

    private static void SetBlipDisplaySafe(Blip blip, int display)
    {
        try
        {
            if (blip == null || !blip.Exists())
                return;

            Function.Call(Hash.SET_BLIP_DISPLAY, blip.Handle, display);
        }
        catch
        {
        }
    }

    private void EnsureHiddenRouteBlip(NavigationState state)
    {
        try
        {
            if (state == null || state.TargetPosition == Vector3.Zero)
                return;

            if (state.RouteBlip != null && state.RouteBlip.Exists())
                return;

            Blip b = World.CreateBlip(state.TargetPosition);
            if (b == null || !b.Exists())
                return;

            state.RouteBlip = b;
            state.RouteConfigured = false;
            state.RouteColorApplied = -1;

            try { b.IsShortRange = false; } catch { }

            // Hiển thị trên cả minimap và bản đồ lớn
            try { SetBlipDisplaySafe(b, 2); } catch { }

            // Giữ blip trong suốt, nhưng KHÔNG ẩn khỏi legend/map logic
            try { SetBlipAlphaSafe(b, 0); } catch { }
        }
        catch
        {
        }
    }

    private void ClearRouteBlipOnly(NavigationState state)
    {
        try
        {
            if (state == null)
                return;

            if (state.RouteBlip != null)
            {
                try
                {
                    if (state.RouteBlip.Exists())
                        state.RouteBlip.Delete();
                }
                catch
                {
                }
            }

            state.RouteBlip = null;
        }
        catch { }
    }

    private void UpdateHiddenRouteBlipFromState(NavigationState state)
    {
        try
        {
            if (state == null || !state.Active || state.TargetPosition == Vector3.Zero)
                return;

            EnsureHiddenRouteBlip(state);

            if (state.RouteBlip == null || !state.RouteBlip.Exists())
                return;

            // Chỉ cập nhật vị trí
            try { state.RouteBlip.Position = state.TargetPosition; } catch { }

            int routeColor = NormalizeRouteColorId(state.RouteColorId);

            // Chỉ cấu hình lại khi cần
            if (!state.RouteConfigured || state.RouteColorApplied != routeColor)
            {
                try { state.RouteBlip.IsShortRange = false; } catch { }
                try { SetBlipDisplaySafe(state.RouteBlip, 2); } catch { }
                try { SetBlipAlphaSafe(state.RouteBlip, 0); } catch { }

                // Apply the route palette color to the hidden waypoint blip itself.
                try { Function.Call(Hash.SET_BLIP_COLOUR, state.RouteBlip.Handle, routeColor); } catch { }
                try { Function.Call(Hash.SET_BLIP_ROUTE, state.RouteBlip.Handle, true); } catch { }
                try { state.RouteBlip.ShowRoute = true; } catch { }
                try { Function.Call(Hash.SET_BLIP_ROUTE_COLOUR, state.RouteBlip.Handle, routeColor); } catch { }

                state.RouteConfigured = true;
                state.RouteColorApplied = routeColor;
            }
        }
        catch
        {
        }
    }

    private static void ClearGpsWaypointOnly()
    {
        try
        {
            Function.Call(Hash.CLEAR_GPS_PLAYER_WAYPOINT);
        }
        catch
        {
        }
    }

    private void OpenInformationMenu()
    {
        try
        {
            EnsureInfoMenuCreated();
            BuildInformationMenu();
            HideAllMenus();
            _infoMenu.Visible = true;
            Interval = MENU_VISIBLE_INTERVAL_MS;
            _uiPool.Process();
        }
        catch
        {
        }
    }

    private void OpenRouteMenu()
    {
        try
        {
            EnsureRouteMenuCreated();
            BuildRouteMenu();
            HideAllMenus();
            _routeMenu.Visible = true;
            Interval = MENU_VISIBLE_INTERVAL_MS;
            _uiPool.Process();
        }
        catch
        {
        }
    }

    private void EnsureInfoMenuCreated()
    {
        try
        {
            if (_infoMenuReady)
                return;

            _infoMenu = new NativeMenu(MainTitle, MainSubtitle);
            _uiPool.Add(_infoMenu);
            ConfigureKeyboardOnlyMenu(_infoMenu);
            _infoMenu.Visible = false;
            _infoMenuReady = true;
        }
        catch
        {
        }
    }

    private void EnsureRouteMenuCreated()
    {
        try
        {
            if (_routeMenuReady)
                return;

            _routeMenu = new NativeMenu(RouteTitle, RouteSubtitle);
            _uiPool.Add(_routeMenu);
            ConfigureKeyboardOnlyMenu(_routeMenu);
            _routeMenu.Visible = false;
            _routeMenuReady = true;
        }
        catch
        {
        }
    }

    private void BuildInformationMenu()
    {
        try
        {
            if (_infoMenu == null)
                return;

            _infoMenu.Clear();

            string characterName = GetCurrentCharacterName();
            int vehicleCount = GetOwnedVehiclesForCurrentCharacter().Count;

            AddInfoItem(_infoMenu, L("Navigation_Info_CharacterName", "Tên nhân vật"), characterName);

            var ownedCountItem = new NativeItem(string.Format(CultureInfo.InvariantCulture,
                L("Navigation_Info_VehicleCount", "Số phương tiện sở hữu: {0}"), vehicleCount));
            ownedCountItem.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
            ownedCountItem.Activated += (s, e) => OpenRouteMenu();
            _infoMenu.Add(ownedCountItem);

            AddInfoItem(_infoMenu,
                L("Navigation_Info_RouteCapability", "Khả năng điều hướng"),
                L("Navigation_Info_RouteCapabilityValue", "GPS trực tiếp"));

            var cancelItem = new NativeItem(L("Navigation_Info_CancelRoute", "Hủy bỏ chỉ dẫn tìm phương tiện"));
            cancelItem.Activated += (s, e) =>
            {
                ClearCurrentRoute(true);
                CloseAllMenus();
            };
            _infoMenu.Add(cancelItem);
        }
        catch
        {
        }
    }

    private void BuildRouteMenu()
    {
        try
        {
            if (_routeMenu == null)
                return;

            _routeMenu.Clear();
            _selectedRouteVehicle = null;

            int ownerHash = GetCurrentCharacterHash();
            List<VehicleSnapshot> vehicles = GetOwnedVehiclesForCurrentCharacter();

            if (vehicles.Count == 0)
            {
                _routeMenu.Add(new NativeItem(L("Navigation_Route_NoVehicles", "Chưa có phương tiện nào được lưu.")));
            }
            else
            {
                int index = 1;
                NavigationState activeState = null;
                if (ownerHash != 0)
                    _states.TryGetValue(ownerHash, out activeState);

                foreach (var vehicle in vehicles)
                {
                    var captured = vehicle;

                    string prefix = captured.IsDestroyed
                        ? L("Navigation_Route_DestroyedPrefix", "~HUD_COLOUR_REDLIGHT~[Hỏng]~s~ ")
                        : "";

                    string lineTitle = string.Format(CultureInfo.InvariantCulture,
                        L("Navigation_RouteVehicleLineFormat", "{0}. {1}{2}"),
                        index,
                        prefix,
                        string.IsNullOrWhiteSpace(captured.DisplayName)
                            ? L("Navigation_UnknownLabel", "N/A")
                            : captured.DisplayName);

                    var checkbox = new NativeCheckboxItem(lineTitle, false);

                    if (activeState != null && activeState.Active &&
                        string.Equals(activeState.TargetKey, captured.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        _routeSelectionSync = true;
                        checkbox.Checked = true;
                        _routeSelectionSync = false;
                        _selectedRouteVehicle = captured;
                    }

                    checkbox.CheckboxChanged += (s, e) =>
                    {
                        if (_routeSelectionSync)
                            return;

                        try
                        {
                            if (checkbox.Checked)
                            {
                                _routeSelectionSync = true;

                                foreach (var other in _routeMenu.Items.OfType<NativeCheckboxItem>())
                                {
                                    if (!ReferenceEquals(other, checkbox) && other.Checked)
                                        other.Checked = false;
                                }

                                _routeSelectionSync = false;
                                _selectedRouteVehicle = captured;
                            }
                            else
                            {
                                if (_selectedRouteVehicle != null &&
                                    string.Equals(_selectedRouteVehicle.Key, captured.Key, StringComparison.OrdinalIgnoreCase))
                                {
                                    _selectedRouteVehicle = null;
                                }
                            }
                        }
                        finally
                        {
                            _routeSelectionSync = false;
                        }
                    };

                    _routeMenu.Add(checkbox);
                    index++;
                }
            }

            var confirm = new NativeItem(L("Navigation_Route_Confirm", "Xác nhận điều hướng GPS"));
            confirm.Activated += (s, e) => ConfirmNavigationForCurrentCharacter();

            var back = new NativeItem(L("Navigation_Route_Back", "Quay lại menu trước"));
            back.Activated += (s, e) => ReturnToInformationMenu();

            _routeMenu.Add(confirm);
            _routeMenu.Add(back);
        }
        catch
        {
        }
    }

    private void ConfirmNavigationForCurrentCharacter()
    {
        try
        {
            if (_currentOwnerHash == 0)
            {
                GTA.UI.Screen.ShowSubtitle(L("Navigation_Subtitle_NoCurrentCharacter", "Không xác định được nhân vật hiện tại."), 2500);
                return;
            }

            if (_selectedRouteVehicle == null)
            {
                GTA.UI.Screen.ShowSubtitle(L("Navigation_Subtitle_SelectVehicleFirst", "Hãy ~HUD_COLOUR_DEGEN_YELLOW~chọn~s~ một phương tiện trước."), 2500);
                return;
            }

            var state = GetOrLoadState(_currentOwnerHash);

            VehicleSnapshot resolved;
            if (!TryResolveCurrentTargetSnapshot(_selectedRouteVehicle.Key, _currentOwnerHash, out resolved) || resolved == null)
                resolved = _selectedRouteVehicle;

            if (resolved == null || resolved.Position == Vector3.Zero)
            {
                GTA.UI.Screen.ShowSubtitle(L("Navigation_Subtitle_NoTargetPosition", "Không thấy vị trí hiện tại của phương tiện."), 2500);
                return;
            }

            // Gán các giá trị state ban đầu
            state.Active = true;
            state.TargetKey = resolved.Key ?? "";
            state.TargetName = string.IsNullOrWhiteSpace(resolved.DisplayName) ? "N/A" : resolved.DisplayName;
            state.TargetModelHash = resolved.ModelHash;
            state.TargetPosition = resolved.Position;
            state.TargetHeading = resolved.Heading;

            // Cập nhật đầy đủ các trường logic theo hướng dẫn mới
            state.RouteColorId = PickRandomRouteColorId();
            state.WaypointApplied = false;
            state.LastAppliedWaypointPos = Vector3.Zero;
            state.RouteConfigured = false;
            state.RouteColorApplied = -1;
            state.Dirty = true;

            // Gọi hàm để áp dụng waypoint ngay lập tức và lưu lại trạng thái
            ApplyWaypointFromState(state, forceRefresh: true);
            SaveState(state);

            PlayFrontendSound("Text_Arrive_Tone", "Phone_SoundSet_Default");
            GTA.UI.Screen.ShowSubtitle(string.Format(CultureInfo.InvariantCulture,
                L("Navigation_Subtitle_TargetFound", 
                "Đã ~HUD_COLOUR_DEGEN_CYAN~tìm thấy~s~ chiếc ~HUD_COLOUR_DEGEN_GREEN~{0}~s~ rồi!"), 
                state.TargetName), 
                2500
            );

            CloseAllMenus();
            Interval = ROUTE_ACTIVE_INTERVAL_MS;
        }
        catch
        {
            // Xử lý ngoại lệ nếu cần thiết
        }
    }

    private void ClearCurrentRoute(bool showFeedback)
    {
        try
        {
            if (_currentOwnerHash == 0)
                return;

            if (!_states.TryGetValue(_currentOwnerHash, out NavigationState state))
                return;

            if (!state.Active)
            {
                state.WaypointApplied = false;
                state.LastAppliedWaypointPos = Vector3.Zero;
                return;
            }

            ClearRouteBlipOnly(state);
            ClearGpsWaypointOnly();

            state.Active = false;
            state.TargetKey = "";
            state.TargetName = "";
            state.TargetModelHash = 0;
            state.TargetPosition = Vector3.Zero;
            state.TargetHeading = 0f;
            state.WaypointApplied = false;
            state.LastAppliedWaypointPos = Vector3.Zero;
            state.RouteColorId = -1;
            state.RouteConfigured = false;
            state.RouteColorApplied = -1;
            state.Dirty = true;

            SaveState(state);

            if (showFeedback)
                PlayFrontendSound("Text_Arrive_Tone", "Phone_SoundSet_Default");
            GTA.UI.Screen.ShowSubtitle(
                L("Navigation_Subtitle_TargetCleared", "Đã ~HUD_COLOUR_DEGEN_CYAN~tìm thấy~s~ phương tiện."),
                2500);
        }
        catch
        {
            // Xử lý ngoại lệ nếu có
        }
    }

    private void PlayFrontendSound(string soundName, string soundSet)
    {
        try
        {
            Audio.PlaySoundFrontend(soundName, soundSet);
        }
        catch
        {
            try
            {
                Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, soundName, soundSet, true);
            }
            catch { }
        }
    }

    private void ReturnToInformationMenu()
    {
        try
        {
            HideAllMenus();
            if (_infoMenu != null)
                _infoMenu.Visible = true;
            Interval = MENU_VISIBLE_INTERVAL_MS;
            _uiPool.Process();
        }
        catch
        {
        }
    }

    private void CloseAllMenus()
    {
        try
        {
            if (_infoMenu != null) _infoMenu.Visible = false;
            if (_routeMenu != null) _routeMenu.Visible = false;
        }
        catch
        {
        }
    }

    private void CloseCurrentVisibleMenu()
    {
        try
        {
            if (_routeMenu != null && _routeMenu.Visible)
            {
                ReturnToInformationMenu();
                return;
            }

            if (_infoMenu != null && _infoMenu.Visible)
            {
                CloseAllMenus();
                Interval = MENU_IDLE_INTERVAL_MS;
                return;
            }

            CloseAllMenus();
            Interval = MENU_IDLE_INTERVAL_MS;
        }
        catch
        {
        }
    }

    private void HideAllMenus()
    {
        try
        {
            if (_infoMenu != null) _infoMenu.Visible = false;
            if (_routeMenu != null) _routeMenu.Visible = false;
        }
        catch
        {
        }
    }

    private bool IsAnyMenuVisible()
    {
        try
        {
            return (_infoMenu != null && _infoMenu.Visible) ||
                   (_routeMenu != null && _routeMenu.Visible);
        }
        catch
        {
            return false;
        }
    }

    private NavigationState GetOrLoadState(int ownerHash)
    {
        try
        {
            if (_states.TryGetValue(ownerHash, out NavigationState state))
                return state;

            state = new NavigationState
            {
                OwnerHash = ownerHash
            };

            LoadState(state);
            _states[ownerHash] = state;
            return state;
        }
        catch
        {
            return new NavigationState { OwnerHash = ownerHash };
        }
    }

    private static string GetNavigationStateFile(int ownerHash)
    {
        return Path.Combine(NavigationDataFolder, string.Format(CultureInfo.InvariantCulture,
            "{0}{1}.dat", NavigationStateFilePrefix, ownerHash));
    }

    private void LoadState(NavigationState state)
    {
        try
        {
            if (state == null || state.OwnerHash == 0)
                return;

            state.Active = false;
            state.TargetKey = "";
            state.TargetName = "";
            state.TargetModelHash = 0;
            state.TargetPosition = Vector3.Zero;
            state.TargetHeading = 0f;
            state.LastAppliedWaypointPos = Vector3.Zero;
            state.WaypointApplied = false;
            state.Dirty = false;

            string file = GetNavigationStateFile(state.OwnerHash);
            if (!File.Exists(file))
                return;

            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                int idx = raw.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = raw.Substring(0, idx).Trim().ToLowerInvariant();
                string val = raw.Substring(idx + 1).Trim();

                switch (key)
                {
                    case "active":
                        state.Active = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "targetkey":
                        state.TargetKey = val ?? "";
                        break;
                    case "targetname":
                        state.TargetName = val ?? "";
                        break;
                    case "targetmodelhash":
                        {
                            uint parsed;
                            if (uint.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                                state.TargetModelHash = parsed;
                        }
                        break;
                    case "targetx":
                        {
                            float x;
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out x))
                                state.TargetPosition.X = x;
                        }
                        break;
                    case "targety":
                        {
                            float y;
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                                state.TargetPosition.Y = y;
                        }
                        break;
                    case "targetz":
                        {
                            float z;
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                                state.TargetPosition.Z = z;
                        }
                        break;
                    case "targetheading":
                        {
                            float h;
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out h))
                                state.TargetHeading = h;
                        }
                        break;
                    case "routecolorid":
                        {
                            int c;
                            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out c))
                                state.RouteColorId = c;
                        }
                        break;
                }
            }
        }
        catch
        {
        }
    }

    private void SaveState(NavigationState state)
    {
        try
        {
            if (state == null || state.OwnerHash == 0)
                return;

            Directory.CreateDirectory(NavigationDataFolder);

            string file = GetNavigationStateFile(state.OwnerHash);

            var sb = new StringBuilder();
            sb.AppendLine("version=1");
            sb.AppendLine("active=" + (state.Active ? "1" : "0"));
            sb.AppendLine("targetKey=" + (state.TargetKey ?? ""));
            sb.AppendLine("targetName=" + (state.TargetName ?? ""));
            sb.AppendLine("targetModelHash=" + state.TargetModelHash.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("targetX=" + state.TargetPosition.X.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("targetY=" + state.TargetPosition.Y.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("targetZ=" + state.TargetPosition.Z.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("targetHeading=" + state.TargetHeading.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("routeColorId=" + state.RouteColorId.ToString(CultureInfo.InvariantCulture));

            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            state.Dirty = false;
        }
        catch
        {
        }
    }

    private void FlushDirtyStatesIfNeeded()
    {
        try
        {
            if (Game.GameTime < _nextStateSaveAt)
                return;

            bool anyDirty = false;

            foreach (var kv in _states)
            {
                try
                {
                    if (kv.Value != null && kv.Value.Dirty)
                    {
                        SaveState(kv.Value);
                        anyDirty = true;
                    }
                }
                catch
                {
                }
            }

            if (anyDirty)
                _nextStateSaveAt = Game.GameTime + STATE_SAVE_BACKOFF_MS;
        }
        catch
        {
        }
    }

    private bool TryResolveCurrentTargetSnapshot(string targetKey, int ownerHash, out VehicleSnapshot snapshot)
    {
        snapshot = null;

        try
        {
            if (string.IsNullOrWhiteSpace(targetKey))
                return false;

            var allVehicles = LoadAllVehiclesForCurrentCharacter(ownerHash);
            if (allVehicles.Count == 0)
                return false;

            snapshot = allVehicles.FirstOrDefault(v =>
                v != null &&
                string.Equals(v.Key, targetKey, StringComparison.OrdinalIgnoreCase));

            return snapshot != null;
        }
        catch
        {
            snapshot = null;
            return false;
        }
    }

    private List<VehicleSnapshot> GetOwnedVehiclesForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return new List<VehicleSnapshot>();

            var list = LoadAllVehiclesForCurrentCharacter(ownerHash);

            var unique = new Dictionary<string, VehicleSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in list)
            {
                if (v == null)
                    continue;

                if (!unique.ContainsKey(v.Key))
                    unique[v.Key] = v;
            }

            return unique.Values
                .OrderBy(v => v.IsDestroyed ? 1 : 0)
                .ThenBy(v => v.DisplayName ?? "")
                .ToList();
        }
        catch
        {
            return new List<VehicleSnapshot>();
        }
    }

    private List<VehicleSnapshot> LoadAllVehiclesForCurrentCharacter(int ownerHash)
    {
        var result = new List<VehicleSnapshot>();

        try
        {
            if (ownerHash == 0)
                return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in new[] { PersistentVehiclesFile, DestroyedVehiclesFile })
            {
                foreach (string raw in ReadAllLinesSafe(file))
                {
                    try
                    {
                        VehicleSnapshot snapshot;
                        if (!TryParsePersistentVehicleLine(raw, ownerHash, file == DestroyedVehiclesFile, out snapshot))
                            continue;

                        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Key))
                            continue;

                        if (seen.Add(snapshot.Key))
                            result.Add(snapshot);
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private bool TryParsePersistentVehicleLine(string line, int ownerHashFilter, bool isDestroyed, out VehicleSnapshot vehicle)
    {
        vehicle = null;

        try
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string[] p = line.Split('|');
            if (p.Length < 7)
                return false;

            uint modelHash = ParseModelHash(p[0]);
            if (modelHash == 0)
                return false;

            float x = ParseFloat(p, 1);
            float y = ParseFloat(p, 2);
            float z = ParseFloat(p, 3);
            float heading = ParseFloat(p, 4);

            string plate = p.Length > 5 ? (p[5] ?? "").Trim() : "";
            int ownerHash = 0;
            if (p.Length > 6)
                int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out ownerHash);

            if (ownerHashFilter != 0 && ownerHash != ownerHashFilter)
                return false;

            string displayName = ExtractVehicleDisplayName(p[0]);
            string key = BuildVehicleKey(ownerHash, modelHash, plate, new Vector3(x, y, z));

            vehicle = new VehicleSnapshot
            {
                Key = key,
                ModelHash = modelHash,
                OwnerHash = ownerHash,
                DisplayName = displayName,
                Plate = plate,
                Position = new Vector3(x, y, z),
                Heading = heading,
                IsDestroyed = isDestroyed
            };

            return true;
        }
        catch
        {
            vehicle = null;
            return false;
        }
    }

    private static IEnumerable<string> ReadAllLinesSafe(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Array.Empty<string>();

            return File.ReadAllLines(path, Encoding.UTF8);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string ExtractVehicleDisplayName(string raw)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "N/A";

            raw = raw.Trim();
            int idx = raw.IndexOf(" (0x", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
                return raw.Substring(0, idx).Trim();

            idx = raw.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
                return raw.Substring(0, idx).Trim().TrimEnd('(').Trim();

            return raw;
        }
        catch
        {
            return raw ?? "N/A";
        }
    }

    private static string BuildVehicleKey(int ownerHash, uint modelHash, string plate, Vector3 pos)
    {
        try
        {
            string normalizedPlate = NormalizePlate(plate);

            if (!string.IsNullOrEmpty(normalizedPlate))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|{1:X}|PLATE|{2}",
                    ownerHash,
                    modelHash,
                    normalizedPlate);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1:X}|POS|{2:0.0}|{3:0.0}|{4:0.0}",
                ownerHash,
                modelHash,
                pos.X,
                pos.Y,
                pos.Z);
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizePlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
            return "";

        return new string(plate.Trim().Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
    }

    private static uint ParseModelHash(string raw)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(raw))
                return 0u;

            raw = raw.Trim();

            int idx = raw.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int start = idx + 2;
                int len = 0;

                while (start + len < raw.Length && IsHexDigit(raw[start + len]))
                    len++;

                if (len > 0)
                {
                    string hex = raw.Substring(start, len);
                    if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
                        return parsed;
                }
            }

            int firstHex = -1;
            for (int i = 0; i < raw.Length; i++)
            {
                if (IsHexDigit(raw[i]))
                {
                    firstHex = i;
                    break;
                }
            }

            if (firstHex >= 0)
            {
                int len = 0;
                while (firstHex + len < raw.Length && IsHexDigit(raw[firstHex + len]))
                    len++;

                if (len > 0)
                {
                    string hex = raw.Substring(firstHex, len);
                    if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
                        return parsed;
                }
            }

            if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dec))
                return dec;
        }
        catch
        {
        }

        return 0u;
    }

    private static bool IsHexDigit(char c)
    {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }

    private static float ParseFloat(string[] parts, int index)
    {
        if (parts == null || index < 0 || index >= parts.Length)
            return 0f;

        float value = 0f;
        float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return value;
    }

    private static void AddInfoItem(NativeMenu menu, string label, string value)
    {
        try
        {
            if (menu == null)
                return;

            var item = new NativeItem(string.Format(CultureInfo.InvariantCulture, "{0}: {1}", label, value));
            menu.Add(item);
        }
        catch
        {
        }
    }

    private void ConfigureKeyboardOnlyMenu(NativeMenu menu)
    {
        try
        {
            if (menu == null)
                return;

            menu.MouseBehavior = MenuMouseBehavior.Disabled;
            menu.ResetCursorWhenOpened = false;
            menu.CloseOnInvalidClick = false;
            menu.RotateCamera = true;
        }
        catch
        {
        }
    }

    private string GetCurrentCharacterName()
    {
        try
        {
            int h = GetCurrentCharacterHash();
            if (h == FRANKLIN_HASH) return "Franklin Clinton";
            if (h == MICHAEL_HASH) return "Michael De Santa";
            if (h == TREVOR_HASH) return "Trevor Philips";
        }
        catch
        {
        }

        return "N/A";
    }

    private int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists())
                return 0;

            int hash = p.Model.Hash;
            if (hash == FRANKLIN_HASH || hash == MICHAEL_HASH || hash == TREVOR_HASH)
                return hash;
        }
        catch
        {
        }

        return 0;
    }
}