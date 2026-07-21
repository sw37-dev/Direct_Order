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

public class SymbolixIcons : Script
{
    private const int FRANKLIN_HASH = -1692214353;
    private const int MICHAEL_HASH = 225514697;
    private const int TREVOR_HASH = -1686040670;

    private const int MIN_ICON_ID = 0;
    private const int MAX_ICON_ID = 957;
    private const int MIN_COLOR_ID = 0;
    private const int MAX_COLOR_ID = 85;
    private const int CONTACT_DIAL_TIMEOUT_MS = 2000;
    private const int MENU_IDLE_INTERVAL_MS = 500;
    private const int MENU_VISIBLE_INTERVAL_MS = 0;

    private static string L(string key, string fallback)
    {
        try { return Language.Get(key, fallback); }
        catch { return fallback; }
    }

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PersistentManager");

    private static readonly string VehiclesFile = Path.Combine(DataFolder, "persistent_vehicles.txt");

    private readonly Dictionary<string, int?> _baselineColors = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int?> _previewColors = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

    private NativeItem _detailColorItem;

    private readonly ObjectPool _uiPool = new ObjectPool();
    private NativeMenu _mainMenu;
    private NativeMenu _ownerVehiclesMenu;
    private NativeMenu _detailMenu;

    private bool _mainMenuReady;
    private bool _ownerVehiclesMenuReady;
    private bool _detailMenuReady;

    private bool _detailMenuNeedsRefreshAfterKeyboardCommit;

    private bool _contactAdded;
    private CustomiFruit _phoneInstance;
    private readonly string _contactName = L("Symbolix_ContactName", "Symbolix");

    private readonly List<VehicleEntry> _ownerVehicles = new List<VehicleEntry>();
    private readonly List<NativeItem> _detailItems = new List<NativeItem>();
    private int _currentOwnerHash;

    private readonly List<string> _selectedVehicleKeys = new List<string>();
    private bool _ownerSelectionSyncGuard = false;

    private bool _keyboardPromptActive;
    private string _keyboardPromptKey = string.Empty;
    private string _keyboardPromptDefaultText = string.Empty;
    private int _keyboardPromptTargetIndex = -1;

    private readonly Dictionary<string, int?> _baselineIcons = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int?> _previewIcons = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, float?> _baselineScales = new Dictionary<string, float?>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float?> _previewScales = new Dictionary<string, float?>(StringComparer.OrdinalIgnoreCase);

    private NativeItem _detailSizeItem;
    private bool _keyboardPromptIsScale;

    private const float MIN_SCALE = 0.50f;
    private const float MAX_SCALE = 2.00f;
    private const float SCALE_STEP = 0.01f;

    private readonly List<NativeCheckboxItem> _ownerCheckboxItems = new List<NativeCheckboxItem>();
    private int _selectedVehicleIndex = -1;
    private string _selectedVehicleKey = string.Empty;
    private bool _singleSelectGuard = false;
    private NativeItem _detailVehicleItem;

    private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private class VehicleEntry
    {
        public uint ModelHash;
        public int OwnerHash;
        public string Plate = string.Empty;
        public Vector3 Position;
        public float Heading;
        public string DisplayName = string.Empty;
        public string Key = string.Empty;
    }

    public SymbolixIcons()
    {
        Interval = MENU_IDLE_INTERVAL_MS;
        Tick += OnTick;
        KeyDown += OnKeyDown;
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading)
                return;

            EnsureContactRegistered();
            ProcessKeyboardPrompt();

            if (_detailMenuNeedsRefreshAfterKeyboardCommit && IsDetailMenuVisible() && !_keyboardPromptActive)
            {
                RefreshDetailMenuAfterKeyboardCommit();
            }

            if (IsAnyMenuVisible())
            {
                Interval = MENU_VISIBLE_INTERVAL_MS;
                _uiPool.Process();
                SyncMenuSelectionVisuals();
            }
            else
            {
                Interval = MENU_IDLE_INTERVAL_MS;
            }
        }
        catch
        {
            Interval = MENU_IDLE_INTERVAL_MS;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (_keyboardPromptActive)
                return;

            // Xử lý khi Menu Detail đang hiển thị
            if (IsDetailMenuVisible())
            {
                int selectedIndex = GetSelectedMenuIndex(_detailMenu, _detailItems);

                if (selectedIndex == 0)
                {
                    if (e.KeyCode == Keys.Left) AdjustSelectedVehicleIcon(-1);
                    else if (e.KeyCode == Keys.Right) AdjustSelectedVehicleIcon(+1);
                    else if (e.KeyCode == Keys.Enter) BeginEditingSelectedVehicleIcon();
                    else if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape) CancelDetailAndReturn();
                }
                else if (selectedIndex == 1)
                {
                    if (e.KeyCode == Keys.Left) AdjustSelectedVehicleScale(-1);
                    else if (e.KeyCode == Keys.Right) AdjustSelectedVehicleScale(+1);
                    else if (e.KeyCode == Keys.Enter) BeginEditingSelectedVehicleScale();
                    else if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape) CancelDetailAndReturn();
                }
                else if (selectedIndex == 2)
                {
                    if (e.KeyCode == Keys.Left) AdjustSelectedVehicleColor(-1);
                    else if (e.KeyCode == Keys.Right) AdjustSelectedVehicleColor(+1);
                    else if (e.KeyCode == Keys.Enter) BeginEditingSelectedVehicleColor();
                    else if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape) CancelDetailAndReturn();
                }
                else if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                {
                    CancelDetailAndReturn();
                }

                return;
            }

            // Xử lý khi các Menu khác đang hiển thị
            if (IsOwnerVehiclesMenuVisible())
            {
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                    ReturnToMainMenu();
            }
            else if (IsMainMenuVisible())
            {
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                    CloseAllMenus();
            }
        }
        catch
        {
            // Xử lý ngoại lệ nếu cần thiết
        }
    }

    private static string FormatColorId(int colorId)
    {
        return colorId.ToString(CultureInfo.InvariantCulture);
    }

    private int GetWorkingColorId(string vehicleKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vehicleKey))
                return -1;

            if (_previewColors.TryGetValue(vehicleKey, out int? preview) && preview.HasValue)
                return preview.Value;

            int persisted = PersistentManager.GetVehicleEffectiveColorByKey(vehicleKey);
            return persisted >= 0 ? persisted : -1;
        }
        catch
        {
            return -1;
        }
    }

    private void BeginEditingSelectedVehicleColor()
    {
        try
        {
            if (_selectedVehicleIndex < 0 || _selectedVehicleIndex >= _ownerVehicles.Count)
                return;

            var entry = _ownerVehicles[_selectedVehicleIndex];
            int currentColor = GetWorkingColorId(entry.Key);
            if (currentColor < 0)
                currentColor = 0;

            StartKeyboardPrompt(
                entry.Key,
                currentColor.ToString(CultureInfo.InvariantCulture),
                _selectedVehicleIndex,
                isScalePrompt: false
            );
        }
        catch
        {
            // Thực thi khi có lỗi (bỏ trống theo code gốc)
        }
    }

    private void AdjustSelectedVehicleColor(int delta)
    {
        try
        {
            if (!IsDetailMenuVisible())
                return;

            if (_selectedVehicleIndex < 0 || _selectedVehicleIndex >= _ownerVehicles.Count)
                return;

            var entry = _ownerVehicles[_selectedVehicleIndex];
            int currentColor = GetWorkingColorId(entry.Key);
            if (currentColor < 0)
                currentColor = 0;

            int newColor = currentColor + delta;
            if (newColor < MIN_COLOR_ID)
                newColor = MIN_COLOR_ID;
            if (newColor > MAX_COLOR_ID)
                newColor = MAX_COLOR_ID;

            CommitDraftColor(entry.Key, newColor, previewOnly: true, refreshMenu: false);
            UpdateDetailVehicleVisuals();
        }
        catch
        {
            // Thực thi khi có lỗi (bỏ trống theo code gốc)
        }
    }

    private void CommitDraftColor(string vehicleKey, int colorId, bool previewOnly, bool refreshMenu = false)
    {
        try
        {
            if (colorId < MIN_COLOR_ID || colorId > MAX_COLOR_ID)
                return;

            foreach (var targetKey in GetTargetVehicleKeys(vehicleKey))
            {
                _previewColors[targetKey] = colorId;
                PersistentManager.SetVehicleCustomColorByKey(targetKey, colorId, false);
            }

            if (!previewOnly)
            {
                foreach (var targetKey in GetTargetVehicleKeys(vehicleKey))
                {
                    _baselineColors[targetKey] = colorId;
                    _previewColors[targetKey] = colorId;
                    PersistentManager.SetVehicleCustomColorByKey(targetKey, colorId, true);
                }

                foreach (var targetKey in GetTargetVehicleKeys(vehicleKey))
                {
                    PersistentManager.SetVehicleCustomColorByKey(targetKey, colorId, false);
                }
            }

            if (refreshMenu)
                _detailMenuNeedsRefreshAfterKeyboardCommit = true;

            ForceDetailPreviewRefresh(vehicleKey);
        }
        catch
        {
            // Thực thi khi có lỗi (bỏ trống theo code gốc)
        }
    }

    private static bool TryParseScaleFlexible(string text, out float scale)
    {
        scale = 0f;

        try
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            // cho phép cả 1.25 và 1,25
            string normalized = text.Trim().Replace(',', '.');
            return float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out scale);
        }
        catch
        {
            return false;
        }
    }

    private void AdjustSelectedVehicleScale(int delta)
    {
        try
        {
            if (!IsDetailMenuVisible())
                return;

            if (_selectedVehicleIndex < 0 || _selectedVehicleIndex >= _ownerVehicles.Count)
                return;

            var entry = _ownerVehicles[_selectedVehicleIndex];
            float currentScale = GetWorkingScale(entry.Key);
            float newScale = ClampScale(currentScale + (delta * SCALE_STEP));

            CommitDraftScale(entry.Key, newScale, previewOnly: true, refreshMenu: false);
            UpdateDetailVehicleVisuals();
        }
        catch
        {
        }
    }

    private bool IsDetailMenuVisible()
    {
        return _detailMenu != null && _detailMenu.Visible;
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
                _contactAdded = false;
            }

            if (_contactAdded)
                return;

            if (phone.Contacts.Any(c => c != null && string.Equals(c.Name, _contactName, StringComparison.OrdinalIgnoreCase)))
            {
                _contactAdded = true;
                return;
            }

            var contact = new iFruitContact(_contactName)
            {
                Active = true,
                DialTimeout = CONTACT_DIAL_TIMEOUT_MS,
                Bold = false,
                Icon = new ContactIcon("DIA_LOST")
            };

            contact.Answered += OnSymbolixAnswered;
            phone.Contacts.Add(contact);
            _contactAdded = true;
        }
        catch { }
    }

    private void OnSymbolixAnswered(iFruitContact sender)
    {
        try
        {
            OpenMainMenuForCurrentCharacter();
        }
        finally
        {
            try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
        }
    }

    private int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists())
                return 0;

            int h = p.Model.Hash;
            if (h == FRANKLIN_HASH || h == MICHAEL_HASH || h == TREVOR_HASH)
                return h;
        }
        catch
        {
        }

        return 0;
    }

    private static string GetCharacterNameFromHash(int hash)
    {
        if (hash == FRANKLIN_HASH) return "Franklin Clinton";
        if (hash == MICHAEL_HASH) return "Michael De Santa";
        if (hash == TREVOR_HASH) return "Trevor Philips";
        return "N/A";
    }

    private void OpenMainMenuForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            _currentOwnerHash = ownerHash;
            BuildOwnerVehicleCache(ownerHash);

            _selectedVehicleKeys.Clear();
            _selectedVehicleIndex = -1;
            _selectedVehicleKey = string.Empty;

            _baselineIcons.Clear();
            _previewIcons.Clear();
            _baselineScales.Clear();
            _previewScales.Clear();

            // Thêm phần clear danh sách màu sắc baseline và preview
            _baselineColors.Clear();
            _previewColors.Clear();

            foreach (var v in _ownerVehicles)
            {
                int? savedIcon = PersistentManager.GetPersistedVehicleIconByKey(v.Key);
                float? savedScale = PersistentManager.GetPersistedVehicleScaleByKey(v.Key);

                _baselineIcons[v.Key] = savedIcon;
                _previewIcons[v.Key] = savedIcon;

                _baselineScales[v.Key] = savedScale;
                _previewScales[v.Key] = savedScale;

                // Thêm phần lấy và lưu thông tin màu sắc đã lưu
                int? savedColor = PersistentManager.GetPersistedVehicleColorByKey(v.Key);

                _baselineColors[v.Key] = savedColor;
                _previewColors[v.Key] = savedColor;
            }

            EnsureMainMenuCreated();
            BuildMainMenu();
            CloseVisibleMenusExcept(_mainMenu);
            _mainMenu.Visible = true;
            Interval = MENU_VISIBLE_INTERVAL_MS;
            _uiPool.Process();
        }
        catch { }
    }

    private static float ClampScale(float scale)
    {
        if (float.IsNaN(scale) || float.IsInfinity(scale))
            return 1.00f;

        if (scale < MIN_SCALE) scale = MIN_SCALE;
        if (scale > MAX_SCALE) scale = MAX_SCALE;

        return (float)Math.Round(scale, 2, MidpointRounding.AwayFromZero);
    }

    private float GetWorkingScale(string vehicleKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vehicleKey))
                return 1.00f;

            if (_previewScales.TryGetValue(vehicleKey, out float? preview) && preview.HasValue)
                return ClampScale(preview.Value);

            if (_baselineScales.TryGetValue(vehicleKey, out float? baseScale) && baseScale.HasValue)
                return ClampScale(baseScale.Value);
        }
        catch { }

        return 1.00f;
    }

    private static string FormatScale(float scale)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0:0.00}x", scale);
    }

    private void EnsureMainMenuCreated()
    {
        try
        {
            if (_mainMenuReady)
                return;

            _mainMenu = new NativeMenu(
                L("Symbolix_Menu_MainTitle", "Information"),
                L("Symbolix_Menu_MainSubtitle", "CHỌN BIỂU TƯỢNG PHƯƠNG TIỆN"));
            _uiPool.Add(_mainMenu);
            ConfigureKeyboardOnlyMenu(_mainMenu);
            _mainMenu.Visible = false;
            _mainMenuReady = true;
        }
        catch
        {
        }
    }

    private void EnsureOwnerVehiclesMenuCreated()
    {
        try
        {
            if (_ownerVehiclesMenuReady)
                return;

            _ownerVehiclesMenu = new NativeMenu(
                L("Symbolix_Menu_OwnerTitle", "Owner Vehicles"),
                L("Symbolix_Menu_OwnerSubtitle", "CHI TIẾT PHƯƠNG TIỆN SỞ HỮU"));
            _uiPool.Add(_ownerVehiclesMenu);
            ConfigureKeyboardOnlyMenu(_ownerVehiclesMenu);
            _ownerVehiclesMenu.Visible = false;
            _ownerVehiclesMenuReady = true;
        }
        catch
        {
        }
    }

    private void EnsureDetailMenuCreated()
    {
        try
        {
            if (_detailMenuReady)
                return;

            _detailMenu = new NativeMenu(
                L("Symbolix_Menu_DetailTitle", "Vehicle Icons"),
                L("Symbolix_Menu_DetailSubtitle", "CHI TIẾT BIỂU TƯỢNG"));
            _uiPool.Add(_detailMenu);
            ConfigureKeyboardOnlyMenu(_detailMenu);
            _detailMenu.Visible = false;
            _detailMenuReady = true;
        }
        catch
        {
        }
    }

    private void ConfigureKeyboardOnlyMenu(NativeMenu menu)
    {
        try
        {
            if (menu == null) return;
            menu.MouseBehavior = MenuMouseBehavior.Disabled;
            menu.ResetCursorWhenOpened = false;
            menu.CloseOnInvalidClick = false;
            menu.RotateCamera = true;
        }
        catch
        {
        }
    }

    private void ForceDetailPreviewRefresh(string vehicleKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vehicleKey))
                return;

            _selectedVehicleKey = vehicleKey;

            // Ép cập nhật lại các dòng hiển thị đang mở
            UpdateDetailVehicleVisuals();

            // Nếu menu đang mở thì cho UI redraw sớm hơn
            if (IsDetailMenuVisible())
                Interval = MENU_VISIBLE_INTERVAL_MS;
        }
        catch
        {
        }
    }

    private void BuildMainMenu()
    {
        try
        {
            if (_mainMenu == null)
                return;

            _mainMenu.Clear();

            AddInfoItem(
                _mainMenu,
                L("Symbolix_Main_InfoCustomer", "Tên khách hàng"),
                GetCharacterNameFromHash(_currentOwnerHash));

            AddActionItem(
                _mainMenu,
                string.Format(CultureInfo.InvariantCulture,
                    L("Symbolix_Main_OwnedVehicles", "Số phương tiện sở hữu: {0}"),
                    _ownerVehicles.Count),
                OpenOwnerVehiclesMenu);

            var cancel = new NativeItem(L("Symbolix_Main_Cancel", "Hủy thay đổi biểu tượng"));
            cancel.Activated += (s, e) => CloseAllMenus();
            _mainMenu.Add(cancel);
        }
        catch
        {
        }
    }

    private void OpenOwnerVehiclesMenu()
    {
        try
        {
            EnsureOwnerVehiclesMenuCreated();
            BuildOwnerVehiclesMenu();
            CloseVisibleMenusExcept(_ownerVehiclesMenu);
            _ownerVehiclesMenu.Visible = true;
            Interval = MENU_VISIBLE_INTERVAL_MS;
            _uiPool.Process();
        }
        catch
        {
        }
    }

    private void BuildOwnerVehiclesMenu()
    {
        try
        {
            if (_ownerVehiclesMenu == null)
                return;

            _ownerVehiclesMenu.Clear();
            _ownerCheckboxItems.Clear();
            _selectedVehicleKeys.Clear();
            _selectedVehicleIndex = -1;
            _selectedVehicleKey = string.Empty;

            if (_ownerVehicles.Count == 0)
            {
                _ownerVehiclesMenu.Add(new NativeItem(
                    L("Symbolix_Owner_NoVehicles", "Không có phương tiện sở hữu.")));
            }
            else
            {
                for (int i = 0; i < _ownerVehicles.Count; i++)
                {
                    int index = i;
                    var entry = _ownerVehicles[i];

                    var item = new NativeCheckboxItem(
                        string.Format(CultureInfo.InvariantCulture, "{0}. {1}", index + 1, entry.DisplayName),
                        false
                    );

                    item.CheckboxChanged += (s, e) =>
                    {
                        if (_ownerSelectionSyncGuard)
                            return;

                        try
                        {
                            _ownerSelectionSyncGuard = true;

                            if (item.Checked)
                            {
                                if (!_selectedVehicleKeys.Any(k =>
                                    string.Equals(k, entry.Key, StringComparison.OrdinalIgnoreCase)))
                                {
                                    _selectedVehicleKeys.Add(entry.Key);
                                }

                                _selectedVehicleIndex = index;
                                _selectedVehicleKey = entry.Key;
                            }
                            else
                            {
                                _selectedVehicleKeys.RemoveAll(k =>
                                    string.Equals(k, entry.Key, StringComparison.OrdinalIgnoreCase));

                                if (_selectedVehicleKeys.Count == 0)
                                {
                                    _selectedVehicleIndex = -1;
                                    _selectedVehicleKey = string.Empty;
                                }
                                else if (string.Equals(_selectedVehicleKey, entry.Key, StringComparison.OrdinalIgnoreCase))
                                {
                                    _selectedVehicleKey = _selectedVehicleKeys[0];
                                    _selectedVehicleIndex = _ownerVehicles.FindIndex(v =>
                                        string.Equals(v.Key, _selectedVehicleKey, StringComparison.OrdinalIgnoreCase));
                                }
                            }
                        }
                        finally
                        {
                            _ownerSelectionSyncGuard = false;
                        }
                    };

                    _ownerVehiclesMenu.Add(item);
                    _ownerCheckboxItems.Add(item);
                }
            }

            var confirm = new NativeItem(
                L("Symbolix_Owner_Confirm", "Thay đổi biểu tượng cho các xe đã chọn"));
            confirm.Activated += (s, e) => OpenDetailMenuForSelectedVehicle();

            var back = new NativeItem(
                L("Symbolix_Common_Back", "Quay lại menu trước"));
            back.Activated += (s, e) => ReturnToMainMenu();

            _ownerVehiclesMenu.Add(confirm);
            _ownerVehiclesMenu.Add(back);

            if (_ownerVehicles.Count > 0 && _selectedVehicleKeys.Count == 0)
            {
                _selectedVehicleIndex = 0;
                _selectedVehicleKey = _ownerVehicles[0].Key;
                _selectedVehicleKeys.Add(_selectedVehicleKey);

                _ownerSelectionSyncGuard = true;
                _ownerCheckboxItems[0].Checked = true;
                _ownerSelectionSyncGuard = false;
            }
        }
        catch
        {
        }
    }

    private List<string> GetTargetVehicleKeys(string primaryKey)
    {
        try
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_selectedVehicleKeys != null && _selectedVehicleKeys.Count > 0)
            {
                foreach (var key in _selectedVehicleKeys)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                        set.Add(key);
                }
            }

            if (!string.IsNullOrWhiteSpace(primaryKey))
                set.Add(primaryKey);

            return set.ToList();
        }
        catch
        {
            return string.IsNullOrWhiteSpace(primaryKey)
                ? new List<string>()
                : new List<string> { primaryKey };
        }
    }

    private int GetPrimarySelectedVehicleIndex()
    {
        try
        {
            if (_selectedVehicleIndex >= 0 && _selectedVehicleIndex < _ownerVehicles.Count)
                return _selectedVehicleIndex;

            if (_selectedVehicleKeys.Count > 0)
            {
                string key = _selectedVehicleKeys[0];
                return _ownerVehicles.FindIndex(v =>
                    string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch
        {
        }

        return -1;
    }

    private void OpenDetailMenuForSelectedVehicle()
    {
        try
        {
            int index = GetPrimarySelectedVehicleIndex();
            if (index < 0 || index >= _ownerVehicles.Count)
            {
                ShowSubtitle(L("Symbolix_Detail_SelectVehicle", "Hãy ~HUD_COLOUR_DEGEN_YELLOW~chọn ít nhất một phương tiện~s~ trước."));
                return;
            }

            _selectedVehicleIndex = index;
            _selectedVehicleKey = _ownerVehicles[index].Key;

            EnsureDetailMenuCreated();
            BuildDetailMenuForSelectedVehicle();

            CloseVisibleMenusExcept(_detailMenu);
            _detailMenu.Visible = true;
            Interval = MENU_VISIBLE_INTERVAL_MS;
            _uiPool.Process();
        }
        catch
        {
        }
    }

    private void BuildDetailMenuForSelectedVehicle()
    {
        try
        {
            if (_detailMenu == null)
                return;

            // 1. Khởi tạo và xóa dữ liệu cũ trong menu/danh sách
            _detailMenu.Clear();
            _detailItems?.Clear(); // Sử dụng toán tử ?. để tránh lỗi nếu _detailItems chưa được khởi tạo

            // 2. Lấy thông tin phương tiện hiện tại
            var entry = _ownerVehicles[_selectedVehicleIndex];
            int currentIcon = GetWorkingIconId(entry.Key);
            float currentScale = GetWorkingScale(entry.Key);

            // 3. Khởi tạo mục chỉnh sửa Biểu tượng
            _detailVehicleItem = new NativeItem(BuildDetailVehicleLabel(entry.DisplayName));
            _detailVehicleItem.AltTitle = string.Format(CultureInfo.InvariantCulture, "\u2190 {0} \u2192", currentIcon);
            _detailVehicleItem.Activated += (s, e) => BeginEditingSelectedVehicleIcon();

            // 4. Khởi tạo mục chỉnh sửa Kích thước
            _detailSizeItem = new NativeItem(
                L("Symbolix_Detail_SizeLabel", "Kích thước biểu tượng"));
            _detailSizeItem.AltTitle = string.Format(CultureInfo.InvariantCulture, "\u2190 {0} \u2192", FormatScale(currentScale));
            _detailSizeItem.Activated += (s, e) => BeginEditingSelectedVehicleScale();

            // 4.5. Khởi tạo mục chỉnh sửa Màu sắc (Thêm mới sau _detailSizeItem)
            _detailColorItem = new NativeItem(
                L("Symbolix_Detail_ColorLabel", "Màu sắc"));
            _detailColorItem.AltTitle = string.Format(CultureInfo.InvariantCulture, "\u2190 {0} \u2192", FormatColorId(GetWorkingColorId(entry.Key)));
            _detailColorItem.Activated += (s, e) => BeginEditingSelectedVehicleColor();

            // 5. Khởi tạo các nút Xác nhận và Quay lại (Đã cập nhật)
            var confirm = new NativeItem(
                L("Symbolix_Detail_Confirm", "Xác nhận biểu tượng, kích thước và màu sắc"));
            confirm.Activated += (s, e) => ConfirmSelectedVehicleIcon();

            var back = new NativeItem(
                L("Symbolix_Common_Back", "Quay lại menu trước"));
            back.Activated += (s, e) => CancelDetailAndReturn();

            // 6. Thêm các mục vào Menu giao diện (_detailMenu)
            _detailMenu.Add(_detailVehicleItem);
            _detailMenu.Add(_detailSizeItem);
            _detailMenu.Add(_detailColorItem);
            _detailMenu.Add(confirm);
            _detailMenu.Add(back);

            // 7. Thêm các mục vào danh sách quản lý code (_detailItems) nếu danh sách này tồn tại
            if (_detailItems != null)
            {
                _detailItems.Add(_detailVehicleItem);
                _detailItems.Add(_detailSizeItem);
                _detailItems.Add(_detailColorItem);
                _detailItems.Add(confirm);
                _detailItems.Add(back);
            }
        }
        catch
        {
            // Có thể thêm log lỗi tại đây nếu cần thiết trong quá trình debug
        }
    }

    private static string BuildDetailVehicleLabel(string vehicleName)
    {
        return vehicleName ?? string.Empty;
    }

    private static string BuildVehicleItemLabel(VehicleEntry entry, int index, bool selected, int iconId)
    {
        try
        {
            if (entry == null)
                return string.Empty;

            return string.Format(CultureInfo.InvariantCulture, "{0}. {1}", index + 1, entry.DisplayName);
        }
        catch
        {
            return entry?.DisplayName ?? string.Empty;
        }
    }

    private int GetWorkingIconId(string vehicleKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vehicleKey))
                return -1;

            if (_previewIcons.TryGetValue(vehicleKey, out int? preview) && preview.HasValue)
                return preview.Value;

            int persisted = PersistentManager.GetVehicleEffectiveIconIdByKey(vehicleKey);
            return persisted >= 0 ? persisted : -1;
        }
        catch
        {
            return -1;
        }
    }

    private void BeginEditingSelectedVehicleIcon()
    {
        try
        {
            if (_selectedVehicleIndex < 0 || _selectedVehicleIndex >= _ownerVehicles.Count)
                return;

            var entry = _ownerVehicles[_selectedVehicleIndex];
            int currentIcon = GetWorkingIconId(entry.Key);
            if (currentIcon < 0) currentIcon = 0;

            StartKeyboardPrompt(
                entry.Key,
                currentIcon.ToString(CultureInfo.InvariantCulture),
                _selectedVehicleIndex,
                isScalePrompt: false);
        }
        catch
        {
        }
    }

    private void BeginEditingSelectedVehicleScale()
    {
        try
        {
            if (_selectedVehicleIndex < 0 || _selectedVehicleIndex >= _ownerVehicles.Count)
                return;

            var entry = _ownerVehicles[_selectedVehicleIndex];
            float currentScale = GetWorkingScale(entry.Key);

            StartKeyboardPrompt(
                entry.Key,
                currentScale.ToString("0.00", CultureInfo.InvariantCulture),
                _selectedVehicleIndex,
                isScalePrompt: true);
        }
        catch
        {
        }
    }

    private void AdjustSelectedVehicleIcon(int delta)
    {
        try
        {
            if (!IsDetailMenuVisible())
                return;

            if (_selectedVehicleIndex < 0 || _selectedVehicleIndex >= _ownerVehicles.Count)
                return;

            var entry = _ownerVehicles[_selectedVehicleIndex];
            int currentIcon = GetWorkingIconId(entry.Key);
            if (currentIcon < 0) currentIcon = 0;

            int newIcon = currentIcon + delta;
            if (newIcon < MIN_ICON_ID) newIcon = MIN_ICON_ID;
            if (newIcon > MAX_ICON_ID) newIcon = MAX_ICON_ID;

            CommitDraftIcon(entry.Key, newIcon, previewOnly: true, refreshMenu: false);
            UpdateDetailVehicleVisuals();
        }
        catch
        {
        }
    }

    private void ResetKeyboardPromptState()
    {
        _keyboardPromptActive = false;
        _keyboardPromptIsScale = false;
        _keyboardPromptKey = string.Empty;
        _keyboardPromptDefaultText = string.Empty;
        _keyboardPromptTargetIndex = -1;
    }

    private void StartKeyboardPrompt(string vehicleKey, string currentText, int index, bool isScalePrompt)
    {
        try
        {
            if (_keyboardPromptActive)
                return;

            _keyboardPromptActive = true;
            _keyboardPromptIsScale = isScalePrompt;
            _keyboardPromptKey = vehicleKey;
            _keyboardPromptDefaultText = currentText ?? string.Empty;
            _keyboardPromptTargetIndex = index;

            Function.Call(
                Hash.DISPLAY_ONSCREEN_KEYBOARD,
                0,
                "FMMC_KEY_TIP8",
                "",
                _keyboardPromptDefaultText,
                "",
                "",
                "",
                8
            );
        }
        catch
        {
            ResetKeyboardPromptState();
        }
    }

    private void ProcessKeyboardPrompt()
    {
        try
        {
            if (!_keyboardPromptActive)
                return;

            int status;
            try
            {
                status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            }
            catch
            {
                ResetKeyboardPromptState();
                return;
            }

            if (status == 0)
                return; // vẫn đang nhập

            string key = _keyboardPromptKey;
            bool isScalePrompt = _keyboardPromptIsScale;

            string result = null;
            if (status == 1)
            {
                try
                {
                    result = Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT);
                }
                catch
                {
                    result = null;
                }
            }

            ResetKeyboardPromptState();

            if (status != 1 || string.IsNullOrWhiteSpace(result))
                return;

            result = result.Trim();

            if (isScalePrompt)
            {
                if (!TryParseScaleFlexible(result, out float value))
                {
                    GTA.UI.Screen.ShowSubtitle(
                        L("Symbolix_Error_InvalidScale", "~r~Kích cỡ không hợp lệ."),
                        2500);
                    return;
                }

                CommitDraftScale(key, value, previewOnly: false, refreshMenu: true);
            }
            else
            {
                if (!int.TryParse(result, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    GTA.UI.Screen.ShowSubtitle(
                        L("Symbolix_Error_InvalidIcon", "~r~ID biểu tượng không hợp lệ."),
                        2500);
                    return;
                }

                value = Math.Max(MIN_ICON_ID, Math.Min(MAX_ICON_ID, value));

                CommitDraftIcon(key, value, previewOnly: false, refreshMenu: true);
            }

            _detailMenuNeedsRefreshAfterKeyboardCommit = true;

            // Ép refresh ngay để preview icon/size hiện ra dù nhập trực tiếp
            ForceDetailPreviewRefresh(key);

            RefreshDetailMenuAfterKeyboardCommit();
        }
        catch
        {
            ResetKeyboardPromptState();
        }
    }

    private void ApplyIconPreview(string vehicleKey, int iconId)
    {
        try
        {
            foreach (var targetKey in GetTargetVehicleKeys(vehicleKey))
            {
                _previewIcons[targetKey] = iconId;
                PersistentManager.SetVehicleCustomIconByKey(targetKey, iconId, false);
            }

            _selectedVehicleKey = vehicleKey;
            UpdateDetailVehicleVisuals();
        }
        catch
        {
        }
    }

    private void ApplyScalePreview(string vehicleKey, float scale)
    {
        try
        {
            float normalized = ClampScale(scale);

            foreach (var targetKey in GetTargetVehicleKeys(vehicleKey))
            {
                _previewScales[targetKey] = normalized;
                PersistentManager.SetVehicleCustomScaleByKey(targetKey, normalized, false);
            }

            _selectedVehicleKey = vehicleKey;
            UpdateDetailVehicleVisuals();
        }
        catch
        {
        }
    }

    private void CommitDraftScale(string vehicleKey, float scale, bool previewOnly, bool refreshMenu = false)
    {
        try
        {
            float normalized = ClampScale(scale);

            // 1) Preview trước cho toàn bộ xe đã tick
            foreach (var targetKey in GetTargetVehicleKeys(vehicleKey))
            {
                _previewScales[targetKey] = normalized;
                PersistentManager.SetVehicleCustomScaleByKey(targetKey, normalized, false);
            }

            // 2) Nếu là xác nhận cuối cùng thì lưu thật
            if (!previewOnly)
            {
                foreach (var targetKey in GetTargetVehicleKeys(vehicleKey))
                {
                    _baselineScales[targetKey] = normalized;
                    _previewScales[targetKey] = normalized;
                    PersistentManager.SetVehicleCustomScaleByKey(targetKey, normalized, true);
                }

                // 3) Re-apply preview sau khi lưu để giữ hiển thị tức thì
                foreach (var targetKey in GetTargetVehicleKeys(vehicleKey))
                {
                    PersistentManager.SetVehicleCustomScaleByKey(targetKey, normalized, false);
                }
            }

            if (refreshMenu)
                _detailMenuNeedsRefreshAfterKeyboardCommit = true;

            ForceDetailPreviewRefresh(vehicleKey);
        }
        catch
        {
        }
    }

    private void CommitDraftIcon(string vehicleKey, int iconId, bool previewOnly, bool refreshMenu = false)
    {
        try
        {
            if (iconId < MIN_ICON_ID || iconId > MAX_ICON_ID)
                return;

            // 1) Preview trước cho toàn bộ xe đã tick
            foreach (var targetKey in GetTargetVehicleKeys(vehicleKey))
            {
                _previewIcons[targetKey] = iconId;
                PersistentManager.SetVehicleCustomIconByKey(targetKey, iconId, false);
            }

            // 2) Nếu là xác nhận cuối cùng thì lưu thật
            if (!previewOnly)
            {
                foreach (var targetKey in GetTargetVehicleKeys(vehicleKey))
                {
                    _baselineIcons[targetKey] = iconId;
                    _previewIcons[targetKey] = iconId;
                    PersistentManager.SetVehicleCustomIconByKey(targetKey, iconId, true);
                }

                // 3) Re-apply preview sau khi lưu để giữ hiển thị tức thì
                foreach (var targetKey in GetTargetVehicleKeys(vehicleKey))
                {
                    PersistentManager.SetVehicleCustomIconByKey(targetKey, iconId, false);
                }
            }

            if (refreshMenu)
                _detailMenuNeedsRefreshAfterKeyboardCommit = true;

            ForceDetailPreviewRefresh(vehicleKey);
        }
        catch
        {
        }
    }

    private void ConfirmSelectedVehicleIcon()
    {
        try
        {
            int index = GetPrimarySelectedVehicleIndex();
            if (index < 0 || index >= _ownerVehicles.Count)
            {
                ShowSubtitle(L("Symbolix_Error_SelectVehicle", "Hãy ~y~chọn~s~ ~HUD_COLOUR_DEGEN_CYAN~ít nhất một phương tiện~s~ trước."));
                return;
            }

            var entry = _ownerVehicles[index];
            int iconId = GetWorkingIconId(entry.Key);

            if (iconId < MIN_ICON_ID || iconId > MAX_ICON_ID)
            {
                ShowSubtitle(L("Symbolix_Error_InvalidIcon", "Biểu tượng ~HUD_COLOUR_REDLIGHT~không hợp lệ.~s~"));
                return;
            }

            float scale = GetWorkingScale(entry.Key);

            // Đọc và lưu màu hiện tại của phương tiện được chọn
            int colorId = GetWorkingColorId(entry.Key);

            foreach (var targetKey in GetTargetVehicleKeys(entry.Key))
            {
                // --- Xử lý Icon và Scale ---
                PersistentManager.SetVehicleCustomIconByKey(targetKey, iconId, true);
                PersistentManager.SetVehicleCustomScaleByKey(targetKey, scale, true);

                _baselineIcons[targetKey] = iconId;
                _previewIcons[targetKey] = iconId;
                _baselineScales[targetKey] = scale;
                _previewScales[targetKey] = scale;

                // --- Xử lý Màu sắc (Color) ---
                // Lưu màu vào dữ liệu cấu hình (true)
                PersistentManager.SetVehicleCustomColorByKey(targetKey, colorId, true);

                // Sync preview ngay sau khi lưu (false)
                PersistentManager.SetVehicleCustomColorByKey(targetKey, colorId, false);

                // Cập nhật vào bộ nhớ tạm cache
                _baselineColors[targetKey] = colorId;
                _previewColors[targetKey] = colorId;
            }

            CloseAllMenus();
        }
        catch
        {
            // Bạn có thể cân nhắc log lỗi ở đây nếu cần thiết trong quá trình debug
        }
    }

    private void CancelDetailAndReturn()
    {
        try
        {
            RevertAllPreviewIcons();
            if (_detailMenu != null) _detailMenu.Visible = false;
            if (_ownerVehiclesMenu != null) _ownerVehiclesMenu.Visible = true;
            Interval = MENU_VISIBLE_INTERVAL_MS;
            _uiPool.Process();
        }
        catch
        {
        }
    }

    private void RefreshDetailMenuAfterKeyboardCommit()
    {
        try
        {
            if (_detailMenu == null || !_detailMenu.Visible)
                return;

            if (_selectedVehicleIndex < 0 || _selectedVehicleIndex >= _ownerVehicles.Count)
                return;

            _detailMenuNeedsRefreshAfterKeyboardCommit = false;

            _detailMenu.Visible = false;
            BuildDetailMenuForSelectedVehicle();

            CloseVisibleMenusExcept(_detailMenu);
            _detailMenu.Visible = true;

            UpdateDetailVehicleVisuals();
            Interval = MENU_VISIBLE_INTERVAL_MS;
            _uiPool.Process();
        }
        catch
        {
            _detailMenuNeedsRefreshAfterKeyboardCommit = true;
        }
    }

    private void RevertAllPreviewIcons()
    {
        try
        {
            foreach (var kv in _baselineIcons)
            {
                PersistentManager.ClearVehiclePreviewIconByKey(kv.Key);
                _previewIcons[kv.Key] = kv.Value;
            }

            foreach (var kv in _baselineScales)
            {
                PersistentManager.ClearVehiclePreviewScaleByKey(kv.Key);
                _previewScales[kv.Key] = kv.Value;
            }

            // Đoạn mã mới được thêm vào theo hướng dẫn
            foreach (var kv in _baselineColors)
            {
                PersistentManager.ClearVehiclePreviewColorByKey(kv.Key);
                _previewColors[kv.Key] = kv.Value;
            }
        }
        catch
        {
            // Xử lý ngoại lệ nếu có (hiện tại đang để trống theo code gốc)
        }
    }

    private void CloseAllMenus()
    {
        try
        {
            RevertAllPreviewIcons();

            if (_mainMenu != null) _mainMenu.Visible = false;
            if (_ownerVehiclesMenu != null) _ownerVehiclesMenu.Visible = false;
            if (_detailMenu != null) _detailMenu.Visible = false;

            _keyboardPromptActive = false;
            _keyboardPromptKey = string.Empty;
            _keyboardPromptDefaultText = string.Empty;
            _keyboardPromptTargetIndex = -1;
            _selectedVehicleKey = string.Empty;
            _selectedVehicleIndex = -1;
            _selectedVehicleKeys.Clear();
        }
        catch
        {
        }
    }

    private void ReturnToMainMenu()
    {
        try
        {
            if (_ownerVehiclesMenu != null) _ownerVehiclesMenu.Visible = false;
            if (_mainMenu != null)
                _mainMenu.Visible = true;
            Interval = MENU_VISIBLE_INTERVAL_MS;
            _uiPool.Process();
        }
        catch
        {
        }
    }

    private bool IsMainMenuVisible()
    {
        return _mainMenu != null && _mainMenu.Visible;
    }

    private bool IsOwnerVehiclesMenuVisible()
    {
        return _ownerVehiclesMenu != null && _ownerVehiclesMenu.Visible;
    }

    private bool IsAnyMenuVisible()
    {
        return IsMainMenuVisible() || IsOwnerVehiclesMenuVisible() || IsDetailMenuVisible();
    }

    private void CloseVisibleMenusExcept(NativeMenu keep)
    {
        try
        {
            if (_mainMenu != null && !ReferenceEquals(_mainMenu, keep)) _mainMenu.Visible = false;
            if (_ownerVehiclesMenu != null && !ReferenceEquals(_ownerVehiclesMenu, keep)) _ownerVehiclesMenu.Visible = false;
            if (_detailMenu != null && !ReferenceEquals(_detailMenu, keep)) _detailMenu.Visible = false;
        }
        catch
        {
        }
    }

    private void SyncMenuSelectionVisuals()
    {
        try
        {
            if (IsOwnerVehiclesMenuVisible())
                UpdateOwnerVehicleMenuLabels(GetSelectedMenuIndex(_ownerVehiclesMenu, _ownerCheckboxItems.Cast<NativeItem>()));

            if (IsDetailMenuVisible())
                UpdateDetailVehicleVisuals();
        }
        catch
        {
        }
    }

    private void UpdateDetailVehicleVisuals()
    {
        try
        {
            // Kiểm tra thêm điều kiện _detailColorItem để đảm bảo an toàn nếu cần, 
            // hoặc giữ nguyên các điều kiện cũ tùy thuộc vào cấu trúc khởi tạo của bạn.
            if (_detailMenu == null || _detailVehicleItem == null || _detailSizeItem == null)
                return;

            if (_selectedVehicleIndex < 0 || _selectedVehicleIndex >= _ownerVehicles.Count)
                return;

            var entry = _ownerVehicles[_selectedVehicleIndex];
            int currentIcon = GetWorkingIconId(entry.Key);
            float currentScale = GetWorkingScale(entry.Key);

            // Cập nhật nhãn và giá trị cho Biểu tượng (Icon)
            SetMenuItemLabel(_detailVehicleItem, L("Symbolix_Detail_IconLabel", "Biểu tượng"));
            _detailVehicleItem.AltTitle = string.Format(CultureInfo.InvariantCulture, "\u2190 {0} \u2192", currentIcon);

            // Cập nhật nhãn và giá trị cho Kích thước (Size)
            SetMenuItemLabel(_detailSizeItem, L("Symbolix_Detail_SizeLabel", "Kích thước biểu tượng"));
            _detailSizeItem.AltTitle = string.Format(CultureInfo.InvariantCulture, "\u2190 {0} \u2192", FormatScale(currentScale));

            // Cập nhật nhãn và giá trị cho Màu sắc (Color) theo hướng dẫn mới
            if (_detailColorItem != null)
            {
                SetMenuItemLabel(_detailColorItem, L("Symbolix_Detail_ColorLabel", "Màu sắc biểu tượng"));
                _detailColorItem.AltTitle = string.Format(CultureInfo.InvariantCulture, "\u2190 {0} \u2192", FormatColorId(GetWorkingColorId(entry.Key)));
            }
        }
        catch
        {
            // Catch block trống như hàm gốc của bạn để tránh crash ứng dụng khi có lỗi xảy ra
        }
    }

    private int GetSelectedMenuIndex(NativeMenu menu, IEnumerable<NativeItem> items)
    {
        try
        {
            if (menu == null)
                return -1;

            var type = menu.GetType();
            string[] intPropNames = { "CurrentItemIndex", "SelectedIndex", "CurrentSelectionIndex", "Index" };
            foreach (var propName in intPropNames)
            {
                var p = type.GetProperty(propName, AnyInstance);
                if (p != null && p.PropertyType == typeof(int))
                {
                    object value = p.GetValue(menu);
                    if (value is int i)
                        return i;
                }
            }

            string[] itemPropNames = { "CurrentItem", "SelectedItem" };
            foreach (var propName in itemPropNames)
            {
                var p = type.GetProperty(propName, AnyInstance);
                if (p != null)
                {
                    object currentItem = p.GetValue(menu);
                    if (currentItem != null && items != null)
                    {
                        int idx = items.ToList().FindIndex(x => ReferenceEquals(x, currentItem));
                        if (idx >= 0)
                            return idx;
                    }
                }
            }
        }
        catch
        {
        }

        return -1;
    }

    private void UpdateOwnerVehicleMenuLabels(int selectedIndex)
    {
        try
        {
            for (int i = 0; i < _ownerVehicles.Count && i < _ownerCheckboxItems.Count; i++)
            {
                var entry = _ownerVehicles[i];
                bool selected = (i == selectedIndex);
                int iconId = GetWorkingIconId(entry.Key);
                string label = BuildVehicleItemLabel(entry, i, selected, iconId);
                SetMenuItemLabel(_ownerCheckboxItems[i], label);
            }
        }
        catch
        {
        }
    }

    private static string BuildVehicleItemLabel(VehicleEntry entry, int index, bool selected)
    {
        try
        {
            if (entry == null)
                return string.Empty;

            return string.Format(CultureInfo.InvariantCulture, "{0}. {1}", index + 1, entry.DisplayName);
        }
        catch
        {
            return entry?.DisplayName ?? string.Empty;
        }
    }

    private static void SetMenuItemLabel(NativeItem item, string text)
    {
        try
        {
            if (item == null)
                return;

            var type = item.GetType();
            string[] propNames = { "Title", "Text", "Name", "Caption" };
            foreach (var propName in propNames)
            {
                var p = type.GetProperty(propName, AnyInstance);
                if (p != null && p.CanWrite && p.PropertyType == typeof(string))
                {
                    p.SetValue(item, text);
                    return;
                }
            }

            string[] fieldNames = { "Title", "Text", "Name", "Caption" };
            foreach (var fieldName in fieldNames)
            {
                var f = type.GetField(fieldName, AnyInstance);
                if (f != null && f.FieldType == typeof(string))
                {
                    f.SetValue(item, text);
                    return;
                }
            }
        }
        catch
        {
        }
    }

    private void AddInfoItem(NativeMenu menu, string label, string value)
    {
        if (menu == null) return;
        var item = new NativeItem(string.Format(CultureInfo.InvariantCulture, "{0}: {1}", label, value));
        menu.Add(item);
    }

    private void AddActionItem(NativeMenu menu, string label, Action onActivated)
    {
        if (menu == null) return;
        var item = new NativeItem(label);
        item.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
        item.Activated += (s, e) => onActivated?.Invoke();
        menu.Add(item);
    }

    private void BuildOwnerVehicleCache(int ownerHash)
    {
        try
        {
            _ownerVehicles.Clear();
            _currentOwnerHash = ownerHash;

            if (ownerHash == 0)
                return;

            if (!File.Exists(VehiclesFile))
                return;

            foreach (string line in File.ReadAllLines(VehiclesFile, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (TryParseVehicleLine(line, out VehicleEntry entry) && entry != null)
                {
                    if (entry.OwnerHash == ownerHash)
                        _ownerVehicles.Add(entry);
                }
            }
        }
        catch
        {
            _ownerVehicles.Clear();
        }
    }

    private bool TryParseVehicleLine(string line, out VehicleEntry entry)
    {
        entry = null;

        try
        {
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

            string plate = p.Length > 5 ? (p[5] ?? string.Empty).Trim() : string.Empty;
            int ownerHash = 0;
            if (p.Length > 6)
                int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out ownerHash);

            entry = new VehicleEntry
            {
                ModelHash = modelHash,
                OwnerHash = ownerHash,
                Plate = plate,
                Position = new Vector3(x, y, z),
                Heading = heading,
                DisplayName = GetVehicleDisplayName(modelHash),
            };
            entry.Key = BuildVehicleKey(entry.OwnerHash, entry.ModelHash, entry.Plate, entry.Position);
            return true;
        }
        catch
        {
            entry = null;
            return false;
        }
    }

    private static float ParseFloat(string[] parts, int index)
    {
        if (parts == null || index < 0 || index >= parts.Length)
            return 0f;

        float value = 0f;
        float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return value;
    }

    private static uint ParseModelHash(string raw)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0u;
            raw = raw.Trim();

            int idx = raw.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int start = idx + 2;
                int len = 0;
                while (start + len < raw.Length && IsHexDigit(raw[start + len])) len++;
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
                while (firstHex + len < raw.Length && IsHexDigit(raw[firstHex + len])) len++;
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

    private static string GetVehicleDisplayName(uint modelHash)
    {
        try
        {
            var t = FindTypeByName("PersistentManager");
            if (t != null)
            {
                var m = t.GetMethod("GetVehicleDisplayNamePublic", AnyStatic, null, new[] { typeof(uint) }, null);
                if (m != null)
                {
                    var result = m.Invoke(null, new object[] { modelHash }) as string;
                    if (!string.IsNullOrWhiteSpace(result))
                        return result;
                }
            }
        }
        catch
        {
        }

        return $"0x{modelHash:X}";
    }

    private static Type FindTypeByName(string name)
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(name, false, false);
                    if (t != null)
                        return t;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string NormalizePlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return string.Empty;
        return new string(plate.Trim().Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
    }

    private static string BuildVehicleKey(int ownerHash, uint modelHash, string plate, Vector3 pos)
    {
        string normalizedPlate = NormalizePlate(plate);
        if (!string.IsNullOrEmpty(normalizedPlate))
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1:X}|PLATE|{2}", ownerHash, modelHash, normalizedPlate);

        return string.Format(CultureInfo.InvariantCulture,
            "{0}|{1:X}|POS|{2:0.0}|{3:0.0}|{4:0.0}",
            ownerHash, modelHash, pos.X, pos.Y, pos.Z);
    }

    private static bool TryGetPersistentManagerVehiclesList(out object listObj)
    {
        listObj = null;
        try
        {
            var pm = FindTypeByName("PersistentManager");
            if (pm == null)
                return false;

            var field = pm.GetField("_persistVehicles", AnyStatic);
            if (field == null)
                return false;

            listObj = field.GetValue(null);
            return listObj != null;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<object> EnumeratePersistentVehicles()
    {
        try
        {
            if (!TryGetPersistentManagerVehiclesList(out object listObj))
                return Enumerable.Empty<object>();

            if (listObj is System.Collections.IEnumerable enumerable)
            {
                var snapshot = new List<object>();
                foreach (var item in enumerable)
                    snapshot.Add(item);

                return snapshot;
            }
        }
        catch
        {
        }

        return Enumerable.Empty<object>();
    }

    private static FieldInfo GetField(Type t, string name)
    {
        if (t == null) return null;
        return t.GetField(name, AnyInstance) ?? t.GetField(name, AnyStatic);
    }

    private static T ReadField<T>(object obj, string fieldName, T fallback = default)
    {
        try
        {
            if (obj == null) return fallback;
            var f = obj.GetType().GetField(fieldName, AnyInstance);
            if (f == null) return fallback;
            object value = f.GetValue(obj);
            if (value == null) return fallback;
            if (value is T typed) return typed;
            return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static void WriteField(object obj, string fieldName, object value)
    {
        try
        {
            if (obj == null) return;
            var f = obj.GetType().GetField(fieldName, AnyInstance);
            if (f == null) return;
            f.SetValue(obj, value);
        }
        catch
        {
        }
    }

    private static string GetVehicleKeyFromPersistentObject(object pv)
    {
        try
        {
            if (pv == null)
                return string.Empty;

            int ownerHash = ReadField<int>(pv, "OwnerModelHash", 0);
            uint modelHash = ReadField<uint>(pv, "ModelHash", 0u);
            string plate = ReadField<string>(pv, "Plate", string.Empty) ?? string.Empty;
            Vector3 pos = ReadField<Vector3>(pv, "Position", Vector3.Zero);
            return BuildVehicleKey(ownerHash, modelHash, plate, pos);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int GetBlipSpriteId(Blip blip)
    {
        try
        {
            if (blip == null || !blip.Exists())
                return -1;

            return (int)blip.Sprite;
        }
        catch
        {
            return -1;
        }
    }

    private static void EnsureLiveBlipVisible(object pv)
    {
        try
        {
            if (pv == null)
                return;

            var blip = ReadField<Blip>(pv, "MapBlip", null);
            var runtime = ReadField<Vehicle>(pv, "RuntimeVehicle", null);
            Vector3 pos = ReadField<Vector3>(pv, "Position", Vector3.Zero);

            if (blip != null && blip.Exists())
                return;

            Blip created = null;
            try
            {
                if (runtime != null && runtime.Exists())
                {
                    created = runtime.AddBlip();
                }
                else if (pos != Vector3.Zero)
                {
                    created = World.CreateBlip(pos);
                }
            }
            catch
            {
            }

            if (created != null)
            {
                try
                {
                    created.IsShortRange = false;
                }
                catch { }

                try
                {
                    created.Position = pos;
                }
                catch { }

                try
                {
                    created.Color = GetBlipColorForPersistentVehicle(ReadField<int>(pv, "OwnerModelHash", 0), ReadField<bool>(pv, "IsCollateralLocked", false));
                }
                catch { }

                WriteField(pv, "MapBlip", created);
            }
        }
        catch
        {
        }
    }

    private static void ApplyIconToPersistentObject(object pv, int iconId)
    {
        try
        {
            if (pv == null)
                return;

            Blip blip = ReadField<Blip>(pv, "MapBlip", null);
            Vehicle runtime = ReadField<Vehicle>(pv, "RuntimeVehicle", null);

            if ((blip == null || !blip.Exists()) && runtime != null && runtime.Exists())
            {
                blip = runtime.AddBlip();
                WriteField(pv, "MapBlip", blip);
            }
            else if (blip == null || !blip.Exists())
            {
                Vector3 pos = ReadField<Vector3>(pv, "Position", Vector3.Zero);
                if (pos != Vector3.Zero)
                {
                    blip = World.CreateBlip(pos);
                    WriteField(pv, "MapBlip", blip);
                }
            }

            if (blip == null || !blip.Exists())
                return;

            blip.Sprite = (BlipSprite)iconId;
            blip.IsShortRange = false;

            try
            {
                Vector3 pos = ReadField<Vector3>(pv, "Position", Vector3.Zero);
                if (runtime != null && runtime.Exists())
                    blip.Position = runtime.Position;
                else if (pos != Vector3.Zero)
                    blip.Position = pos;
            }
            catch { }
        }
        catch
        {
        }
    }

    private static BlipColor GetBlipColorForPersistentVehicle(int ownerHash, bool collateralLocked)
    {
        try
        {
            if (collateralLocked)
                return BlipColor.Red;

            if (ownerHash == FRANKLIN_HASH) return BlipColor.Green;
            if (ownerHash == MICHAEL_HASH) return BlipColor.Blue;
            if (ownerHash == TREVOR_HASH) return BlipColor.Orange;
        }
        catch
        {
        }

        return BlipColor.White;
    }

    private void ApplyIconToVehicleByKey(string vehicleKey, int iconId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vehicleKey))
                return;

            foreach (var pv in EnumeratePersistentVehicles())
            {
                try
                {
                    string key = GetVehicleKeyFromPersistentObject(pv);
                    if (!string.Equals(key, vehicleKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    EnsureLiveBlipVisible(pv);
                    ApplyIconToPersistentObject(pv, iconId);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private void ShowSubtitle(string text, int durationMs = 2500)
    {
        try { GTA.UI.Screen.ShowSubtitle(text, durationMs); }
        catch { }
    }
}