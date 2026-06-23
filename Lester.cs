using GTA;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

public class Lester : Script
{
    private const string LesterContactNameFallback = "Lester Crest";
    private const string LesterContactIconName = "CHAR_LESTER";
    private const int LesterDialTimeoutMs = 2500;

    private const int MenuCooldownMs = 5000;
    private const int InputBlockMs = 180;

    private const int DefaultMinDelta = -25;
    private const int DefaultMaxDelta = 25;

    // User requested thresholds:
    private const int XCapWhenIncreasing = -8;
    private const int YCapWhenDecreasing = 8;

    private const long BaseFee = 750000L;
    private const long FeePerStep = 25000L;
    private const long MinFee = 450000L;
    private const long MaxFee = 3500000L;

    private readonly ObjectPool _pool = new ObjectPool();

    private NativeMenu _mainMenu = null;
    private NativeMenu _manipulationMenu = null;

    private NativeItem _mainServiceItem = null;
    private NativeCheckboxItem _bankReductionItem = null;
    private NativeItem _mainConfirmItem = null;
    private NativeItem _mainCancelItem = null;

    private NativeItem _serviceItem = null;
    private NativeItem _manipulationItem = null;
    private NativeItem _feeItem = null;
    private NativeItem _confirmItem = null;
    private NativeItem _cancelItem = null;

    private CustomiFruit _phoneInstance = null;
    private bool _contactAdded = false;

    private bool _manipulationFocused = true;
    private int _inputBlockUntil = 0;
    private int _cooldownExpiry = 0;

    private int _currentMinDelta = DefaultMinDelta;
    private int _currentMaxDelta = DefaultMaxDelta;

    private bool _suppressMainCheckboxEvent = false;

    public Lester()
    {
        Tick += OnTick;
        KeyDown += OnKeyDown;
        Interval = 1000;
    }

    private static string T(string key, string fallback)
    {
        try
        {
            return Language.Get(key, fallback);
        }
        catch
        {
            return fallback;
        }
    }

    private static string L(string key, string fallback)
    {
        return T(key, fallback);
    }

    private static void Log(string message)
    {
        try
        {
            System.Diagnostics.Trace.WriteLine(message);
        }
        catch
        {
        }
    }

    private static string GetLesterContactName()
    {
        return T("Lester_ContactName", LesterContactNameFallback);
    }

    private static string FormatCash(long value)
    {
        if (value < 0)
            value = 0;

        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private static string FormatSignedPercent(int value)
    {
        return value >= 0
            ? "+" + value.ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
    }

    private void SetBoolPropertyIfExists(object target, bool value, params string[] propertyNames)
    {
        try
        {
            if (target == null || propertyNames == null || propertyNames.Length == 0)
                return;

            Type t = target.GetType();

            foreach (string name in propertyNames)
            {
                PropertyInfo prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
                {
                    prop.SetValue(target, value, null);
                    return;
                }
            }
        }
        catch
        {
        }
    }

    private void SetCheckboxCheckedIfExists(NativeCheckboxItem item, bool checkedState)
    {
        try
        {
            if (item == null)
                return;

            Type t = item.GetType();

            PropertyInfo prop = t.GetProperty("Checked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
            {
                prop.SetValue(item, checkedState, null);
                return;
            }

            prop = t.GetProperty("IsChecked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
            {
                prop.SetValue(item, checkedState, null);
                return;
            }

            FieldInfo field = t.GetField("_checked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(bool))
            {
                field.SetValue(item, checkedState);
                return;
            }
        }
        catch
        {
        }
    }

    private void ForceReadOnlyCheckbox(NativeCheckboxItem item, bool checkedState)
    {
        try
        {
            if (item == null)
                return;

            SetCheckboxCheckedIfExists(item, checkedState);
        }
        catch
        {
        }
    }

    private bool IsAnyMenuVisible()
    {
        try
        {
            return (_mainMenu != null && _mainMenu.Visible)
                || (_manipulationMenu != null && _manipulationMenu.Visible);
        }
        catch
        {
            return false;
        }
    }

    private void UpdateLemonUiMouseState()
    {
        try
        {
            bool anyMenuVisible = IsAnyMenuVisible();

            if (!anyMenuVisible)
                return;

            if (_mainMenu != null && _mainMenu.Visible)
            {
                SetBoolPropertyIfExists(_mainMenu, false,
                    "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                    "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");
            }

            if (_manipulationMenu != null && _manipulationMenu.Visible)
            {
                SetBoolPropertyIfExists(_manipulationMenu, false,
                    "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                    "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");
            }

            SetBoolPropertyIfExists(_pool, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");
        }
        catch
        {
        }
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            EnsureLesterContactRegistered();

            if (IsAnyMenuVisible())
            {
                UpdateLemonUiMouseState();
                Interval = 0;
                _pool.Process();
                UpdateLemonUiMouseState();
                return;
            }

            Interval = 1000;
        }
        catch
        {
            Interval = 1000;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            bool mainVisible = _mainMenu != null && _mainMenu.Visible;
            bool manipulationVisible = _manipulationMenu != null && _manipulationMenu.Visible;

            if (!mainVisible && !manipulationVisible)
                return;

            if (Game.GameTime < _inputBlockUntil)
                return;

            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
            {
                if (manipulationVisible)
                    CloseManipulationMenu(true);
                else if (mainVisible)
                    CloseMainMenu(true);

                return;
            }

            if (manipulationVisible)
            {
                if (e.KeyCode == Keys.Left)
                {
                    if (_manipulationFocused)
                    {
                        AdjustManipulationRange(-1);
                        BlockInput(InputBlockMs);
                    }
                    return;
                }

                if (e.KeyCode == Keys.Right)
                {
                    if (_manipulationFocused)
                    {
                        AdjustManipulationRange(+1);
                        BlockInput(InputBlockMs);
                    }
                    return;
                }
            }
        }
        catch
        {
        }
    }

    private void BlockInput(int ms)
    {
        try
        {
            _inputBlockUntil = Game.GameTime + Math.Max(1, ms);
        }
        catch
        {
            _inputBlockUntil = 0;
        }
    }

    private void EnsureCooldownSet()
    {
        try
        {
            _cooldownExpiry = Game.GameTime + MenuCooldownMs;
        }
        catch
        {
            _cooldownExpiry = Game.GameTime;
        }
    }

    private bool IsCooldownActive()
    {
        try
        {
            return Game.GameTime < _cooldownExpiry;
        }
        catch
        {
            return false;
        }
    }

    private void ShowCooldownMessage()
    {
        try
        {
            int remainMs = Math.Max(0, _cooldownExpiry - Game.GameTime);
            int sec = (int)Math.Ceiling(remainMs / 1000.0);

            GTA.UI.Screen.ShowSubtitle(
                string.Format(
                    T("LesterAuctionManipulation_Cooldown",
                        "~y~Hãy đợi ~b~{0}s~s~ trước khi mở lại menu Lester."),
                    sec),
                2500);
        }
        catch
        {
        }
    }

    private void ShowIcebreakerStyleError(string title, string message)
    {
        try
        {
            GTA.UI.Screen.ShowSubtitle(
                string.Format(CultureInfo.InvariantCulture, "~r~{0}~s~: {1}", title, message),
                3000);
        }
        catch
        {
        }
    }

    private void EnsureLesterContactRegistered()
    {
        try
        {
            CustomiFruit phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            string contactName = GetLesterContactName();

            if (!ReferenceEquals(_phoneInstance, phone))
            {
                _phoneInstance = phone;
                _contactAdded = false;
            }

            if (_contactAdded)
                return;

            if (phone.Contacts.Any(c =>
                c != null && string.Equals(c.Name, contactName, StringComparison.OrdinalIgnoreCase)))
            {
                _contactAdded = true;
                return;
            }

            ContactIcon icon;
            try
            {
                icon = new ContactIcon(LesterContactIconName);
            }
            catch
            {
                icon = null;
            }

            iFruitContact contact = new iFruitContact(contactName)
            {
                Active = true,
                DialTimeout = LesterDialTimeoutMs,
                Bold = false,
                Icon = icon
            };

            contact.Answered += OnLesterContactAnswered;
            phone.Contacts.Add(contact);
            _contactAdded = true;
        }
        catch
        {
        }
    }

    private void OnLesterContactAnswered(iFruitContact sender)
    {
        try
        {
            OpenMainLesterMenu();
        }
        finally
        {
            try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
        }
    }

    private void EnsureMainMenu()
    {
        try
        {
            if (_mainMenu != null)
                return;

            _mainMenu = new NativeMenu(
                T("LesterAuctionManipulation_MenuTitle", "Lester Crest"),
                T("LesterAuctionManipulation_MainSubtitle", "CHI TIẾT CÁC DỊCH VỤ"));

            _mainMenu.MouseBehavior = MenuMouseBehavior.Disabled;
            _mainMenu.ResetCursorWhenOpened = false;
            _mainMenu.CloseOnInvalidClick = false;
            _mainMenu.RotateCamera = true;

            _pool.Add(_mainMenu);

            _mainServiceItem = new NativeItem(
                T("LesterAuctionManipulation_Banner", "1. Thao túng đấu giá thị trường xe"));
            _mainServiceItem.AltTitle = "~w~>~s~";

            _bankReductionItem = new NativeCheckboxItem(
                T("LesterAuctionManipulation_BankReduction", "2. Giảm án khóa ngân hàng"),
                false);

            _mainConfirmItem = new NativeItem(
                T("PowCleanse_Confirm", "Xác nhận giao dịch"));

            _mainCancelItem = new NativeItem(
                T("PowCleanse_Cancel", "Hủy bỏ giao dịch"));

            _mainServiceItem.Activated += (s, e) =>
            {
                OpenManipulationMenu();
            };

            _bankReductionItem.CheckboxChanged += (s, e) =>
            {
                try
                {
                    if (_suppressMainCheckboxEvent)
                        return;

                    if (_bankReductionItem.Checked)
                    {
                        // Chọn là tick và giữ trạng thái này cho tới khi Icebreaker tiêu thụ nó
                        LesterAuctionManipulationState.ArmBankLockReductionRequestForCurrentCharacter();
                    }
                    else
                    {
                        // không cho bỏ tick bằng tay
                        _suppressMainCheckboxEvent = true;
                        ForceReadOnlyCheckbox(_bankReductionItem, true);
                        _suppressMainCheckboxEvent = false;
                        return;
                    }

                    SyncMainMenuCheckboxes();
                }
                catch
                {
                    _suppressMainCheckboxEvent = false;
                }
            };

            _mainConfirmItem.Activated += (s, e) =>
            {
                ConfirmMainMenuTransaction();
            };

            _mainCancelItem.Activated += (s, e) =>
            {
                CloseMainMenu(true);
            };

            _mainMenu.Add(_mainServiceItem);
            _mainMenu.Add(_bankReductionItem);
            _mainMenu.Add(_mainConfirmItem);
            _mainMenu.Add(_mainCancelItem);
        }
        catch (Exception ex)
        {
            Log(L("Lester_LogEnsureMenuFailed", "EnsureMainMenu failed: ") + ex);
        }
    }

    private void EnsureManipulationMenu()
    {
        try
        {
            if (_manipulationMenu != null)
                return;

            _manipulationMenu = new NativeMenu(
                T("LesterAuctionManipulation_MenuTitle", "Lester Crest"),
                T("LesterAuctionManipulation_MenuSubtitle", "CHI TIẾT THAO TÚNG ĐẤU GIÁ"));

            _manipulationMenu.MouseBehavior = MenuMouseBehavior.Disabled;
            _manipulationMenu.ResetCursorWhenOpened = false;
            _manipulationMenu.CloseOnInvalidClick = false;
            _manipulationMenu.RotateCamera = true;

            _pool.Add(_manipulationMenu);
        }
        catch (Exception ex)
        {
            Log(L("Lester_LogEnsureMenuFailed", "EnsureManipulationMenu failed: ") + ex);
        }
    }

    // Compatibility wrapper if something else still calls old name.
    private void EnsureMenu()
    {
        EnsureManipulationMenu();
    }

    private void SyncMainMenuCheckboxes()
    {
        try
        {
            bool armed = LesterAuctionManipulationState.HasPendingBankLockReductionRequestForCurrentCharacter();
            SetCheckboxCheckedIfExists(_bankReductionItem, armed);
        }
        catch
        {
        }
    }

    private void SyncMenuSelectionFromSavedState()
    {
        try
        {
            int minDelta;
            int maxDelta;
            int fee;

            if (LesterAuctionManipulationState.TryPeekPendingManipulationForCurrentCharacter(out minDelta, out maxDelta, out fee))
            {
                _currentMinDelta = minDelta;
                _currentMaxDelta = maxDelta;
            }
            else
            {
                _currentMinDelta = DefaultMinDelta;
                _currentMaxDelta = DefaultMaxDelta;
            }
        }
        catch
        {
            _currentMinDelta = DefaultMinDelta;
            _currentMaxDelta = DefaultMaxDelta;
        }
    }

    private void BuildManipulationMenu()
    {
        if (_manipulationMenu == null)
            return;

        _manipulationMenu.Clear();

        _serviceItem = new NativeItem(
            T("LesterAuctionManipulation_Service", "Dịch vụ: Thao túng thị trường đấu giá"));
        _serviceItem.Description = T(
            "LesterAuctionManipulation_ServiceDesc",
            "Điều chỉnh biên độ dao động của phiên đấu giá kế tiếp.");
        _serviceItem.Selected += (s, e) =>
        {
            _manipulationFocused = false;
        };
        _manipulationMenu.Add(_serviceItem);

        _manipulationItem = new NativeItem(
            T("LesterAuctionManipulation_Level", "Mức thao túng"));
        _manipulationItem.Description = T(
            "LesterAuctionManipulation_LevelDesc",
            "Có thể thay đổi mức độ dao động. Biên độ dao động này chỉ được áp dụng một lần cho phiên đấu giá kế tiếp.");
        _manipulationItem.Selected += (s, e) =>
        {
            _manipulationFocused = true;
        };
        _manipulationMenu.Add(_manipulationItem);

        _feeItem = new NativeItem(
            T("LesterAuctionManipulation_Fee", "Phí thao túng"));
        _feeItem.Description = T(
            "LesterAuctionManipulation_FeeDesc",
            "Phí sẽ được trừ ngay khi xác nhận.");
        _feeItem.Selected += (s, e) =>
        {
            _manipulationFocused = false;
        };
        _manipulationMenu.Add(_feeItem);

        _confirmItem = new NativeItem(
            T("LesterAuctionManipulation_Confirm", "Xác nhận giao dịch"));
        _confirmItem.Description = T(
            "LesterAuctionManipulation_ConfirmDesc",
            "Áp dụng biên độ đã chọn cho phiên đấu giá kế tiếp một lần.");
        _confirmItem.Activated += (s, e) =>
        {
            ConfirmManipulation();
        };
        _confirmItem.Selected += (s, e) =>
        {
            _manipulationFocused = false;
        };
        _manipulationMenu.Add(_confirmItem);

        _cancelItem = new NativeItem(
            T("LesterAuctionManipulation_Cancel", "Hủy bỏ giao dịch"));
        _cancelItem.Description = T(
            "LesterAuctionManipulation_CancelDesc",
            "Đóng menu.");
        _cancelItem.Activated += (s, e) =>
        {
            CloseManipulationMenu(true);
        };
        _cancelItem.Selected += (s, e) =>
        {
            _manipulationFocused = false;
        };
        _manipulationMenu.Add(_cancelItem);

        UpdateMenuVisuals();
    }

    // Compatibility wrapper if something else still calls old name.
    private void BuildMenu()
    {
        BuildManipulationMenu();
    }

    private void UpdateMenuVisuals()
    {
        try
        {
            if (_manipulationItem != null)
            {
                _manipulationItem.AltTitle = string.Format(
                    CultureInfo.InvariantCulture,
                    "\u2190{0}%\u2192 : \u2190{1}%\u2192",
                    FormatSignedPercent(_currentMinDelta),
                    FormatSignedPercent(_currentMaxDelta));
            }

            if (_feeItem != null)
            {
                _feeItem.AltTitle = FormatCash(ComputeFee(_currentMinDelta, _currentMaxDelta));
            }

            if (_confirmItem != null)
            {
                _confirmItem.Description = string.Format(
                    CultureInfo.InvariantCulture,
                    T("LesterAuctionManipulation_ConfirmDesc2",
                        "Phí hiện tại: {0}. Biên độ này chỉ áp dụng một lần cho phiên đấu giá kế tiếp."),
                    FormatCash(ComputeFee(_currentMinDelta, _currentMaxDelta)));
            }
        }
        catch
        {
        }
    }

    private void OpenMainLesterMenu()
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            if (IsCooldownActive())
            {
                ShowCooldownMessage();
                return;
            }

            EnsureMainMenu();
            SyncMainMenuCheckboxes();

            if (_mainMenu != null)
                _mainMenu.Visible = true;

            if (_manipulationMenu != null)
                _manipulationMenu.Visible = false;

            UpdateLemonUiMouseState();
            Interval = 0;
            BlockInput(250);
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private void OpenManipulationMenu()
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            EnsureManipulationMenu();
            SyncMenuSelectionFromSavedState();
            BuildManipulationMenu();

            if (_manipulationMenu != null)
                _manipulationMenu.Visible = true;

            if (_mainMenu != null)
                _mainMenu.Visible = false;

            UpdateLemonUiMouseState();
            Interval = 0;
        }
        catch
        {
        }
    }

    // Compatibility wrapper if something else still calls old name.
    private void OpenLesterManipulationMenu()
    {
        OpenManipulationMenu();
    }

    private void CloseMainMenu(bool setCooldown)
    {
        try
        {
            if (_mainMenu != null)
            {
                _mainMenu.Visible = false;
                _mainMenu.Clear();
            }
        }
        catch
        {
        }

        if (setCooldown)
            EnsureCooldownSet();

        UpdateLemonUiMouseState();
        Interval = 1000;
    }

    private void CloseManipulationMenu(bool setCooldown)
    {
        try
        {
            if (_manipulationMenu != null)
            {
                _manipulationMenu.Visible = false;
                _manipulationMenu.Clear();
            }
        }
        catch
        {
        }

        _manipulationFocused = true;

        if (setCooldown)
            EnsureCooldownSet();

        UpdateLemonUiMouseState();
        Interval = 1000;
    }

    // Compatibility wrapper if something else still calls old name.
    private void CloseMenu(bool setCooldown)
    {
        CloseManipulationMenu(setCooldown);
    }

    private void AdjustManipulationRange(int delta)
    {
        try
        {
            if (delta == 0)
                return;

            int nextMin = _currentMinDelta;
            int nextMax = _currentMaxDelta;

            // Keep moving both ends together, exactly like the example:
            if (delta > 0)
            {
                if (_currentMinDelta >= XCapWhenIncreasing)
                    return;

                nextMin = _currentMinDelta + 1;
                nextMax = _currentMaxDelta + 1;

                if (nextMin > XCapWhenIncreasing)
                    nextMin = XCapWhenIncreasing;
            }
            else
            {
                if (_currentMaxDelta <= YCapWhenDecreasing)
                    return;

                nextMin = _currentMinDelta - 1;
                nextMax = _currentMaxDelta - 1;

                if (nextMax < YCapWhenDecreasing)
                    nextMax = YCapWhenDecreasing;
            }

            _currentMinDelta = nextMin;
            _currentMaxDelta = nextMax;

            UpdateMenuVisuals();
            PlayFrontendSound("NAV_UP_DOWN", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private int ComputeFee(int minDelta, int maxDelta)
    {
        try
        {
            long distance =
                Math.Abs(minDelta - DefaultMinDelta) +
                Math.Abs(maxDelta - DefaultMaxDelta);

            long fee = BaseFee + (distance * FeePerStep);
            if (fee < MinFee) fee = MinFee;
            if (fee > MaxFee) fee = MaxFee;

            return (int)Math.Min(int.MaxValue, fee);
        }
        catch
        {
            return (int)BaseFee;
        }
    }

    private void ConfirmMainMenuTransaction()
    {
        try
        {
            bool bankReductionArmed = LesterAuctionManipulationState.HasPendingBankLockReductionRequestForCurrentCharacter();

            if (!bankReductionArmed)
            {
                ShowIcebreakerStyleError(
                    T("Lester_BankReductionNeedPickTitle", "Giao dịch thất bại"),
                    T("Lester_BankReductionNeedPickMessage", "Hãy chọn Giảm án khóa ngân hàng trước khi xác nhận."));
                return;
            }

            // Chỉ đánh dấu request. Icebreaker sẽ tiêu thụ nó khi xử lý thành công.
            LesterAuctionManipulationState.ArmBankLockReductionRequestForCurrentCharacter();

            CloseMainMenu(false);
        }
        catch (Exception ex)
        {
            Log(L("Lester_LogConfirmFailed", "ConfirmMainMenuTransaction failed: ") + ex);
        }
    }

    private void ConfirmManipulation()
    {
        try
        {
            int fee = ComputeFee(_currentMinDelta, _currentMaxDelta);
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
            {
                CloseManipulationMenu(false);
                return;
            }

            if (Game.Player.Money < fee)
            {
                GTA.UI.Screen.ShowSubtitle(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        T("LesterAuctionManipulation_NotEnough",
                            "~r~Không đủ tiền~s~ để thao túng đấu giá."),
                        FormatCash(fee)),
                    3000);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            Game.Player.Money -= fee;
            LesterAuctionManipulationState.ArmForCurrentCharacter(_currentMinDelta, _currentMaxDelta, fee);

            GTA.UI.Screen.ShowSubtitle(
                string.Format(
                    CultureInfo.InvariantCulture,
                    T("LesterAuctionManipulation_Success",
                        "~HUD_COLOUR_DEGEN_GREEN~Đã kích hoạt thao túng đấu giá"),
                    string.Format(CultureInfo.InvariantCulture, "\u2190{0}%\u2192", FormatSignedPercent(_currentMinDelta)),
                    string.Format(CultureInfo.InvariantCulture, "\u2190{0}%\u2192", FormatSignedPercent(_currentMaxDelta))),
                3500);

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            CloseManipulationMenu(false);
        }
        catch
        {
            CloseManipulationMenu(false);
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
            catch
            {
            }
        }
    }
}

internal static class LesterAuctionManipulationState
{
    private sealed class Session
    {
        public int MinDelta;
        public int MaxDelta;
        public int Fee;
        public bool Armed;
    }

    private static readonly object _sync = new object();
    private static readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>();
    private static readonly Dictionary<int, bool> _bankReductionRequests = new Dictionary<int, bool>();

    private static int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p != null && p.Exists())
                return p.Model.Hash;
        }
        catch
        {
        }

        return 0;
    }

    public static void ArmForCurrentCharacter(int minDelta, int maxDelta, int fee)
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return;

        lock (_sync)
        {
            _sessions[ownerHash] = new Session
            {
                MinDelta = minDelta,
                MaxDelta = maxDelta,
                Fee = Math.Max(0, fee),
                Armed = true
            };
        }
    }

    public static bool TryPeekPendingManipulationForCurrentCharacter(out int minDelta, out int maxDelta, out int fee)
    {
        minDelta = -25;
        maxDelta = 25;
        fee = 0;

        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return false;

        lock (_sync)
        {
            Session s;
            if (_sessions.TryGetValue(ownerHash, out s) && s != null && s.Armed)
            {
                minDelta = s.MinDelta;
                maxDelta = s.MaxDelta;
                fee = s.Fee;
                return true;
            }
        }

        return false;
    }

    public static bool HasPendingManipulationForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return false;

        lock (_sync)
        {
            Session s;
            return _sessions.TryGetValue(ownerHash, out s) && s != null && s.Armed;
        }
    }

    public static bool TryTakePendingManipulationForCurrentCharacter(out int minDelta, out int maxDelta, out int fee)
    {
        minDelta = -25;
        maxDelta = 25;
        fee = 0;

        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return false;

        lock (_sync)
        {
            Session s;
            if (!_sessions.TryGetValue(ownerHash, out s) || s == null || !s.Armed)
                return false;

            minDelta = s.MinDelta;
            maxDelta = s.MaxDelta;
            fee = s.Fee;

            _sessions.Remove(ownerHash);
            return true;
        }
    }

    public static void ClearForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return;

        lock (_sync)
        {
            _sessions.Remove(ownerHash);
        }
    }

    public static void ArmBankLockReductionRequestForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return;

        lock (_sync)
        {
            _bankReductionRequests[ownerHash] = true;
        }
    }

    public static bool HasPendingBankLockReductionRequestForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return false;

        lock (_sync)
        {
            bool armed;
            return _bankReductionRequests.TryGetValue(ownerHash, out armed) && armed;
        }
    }

    public static bool ConsumeBankLockReductionRequestForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return false;

        lock (_sync)
        {
            bool armed;
            if (_bankReductionRequests.TryGetValue(ownerHash, out armed) && armed)
            {
                _bankReductionRequests.Remove(ownerHash);
                return true;
            }
        }

        return false;
    }

    public static void ClearBankLockReductionRequestForCurrentCharacter()
    {
        int ownerHash = GetCurrentCharacterHash();
        if (ownerHash == 0)
            return;

        lock (_sync)
        {
            _bankReductionRequests.Remove(ownerHash);
        }
    }
}