using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using LemonUI;
using LemonUI.Menus;
using System.Drawing;

public partial class FleecaBankLoanScript : Script
{
    private string ContactName => L("Credit_ContactName", "Fleeca Bank");
    private string BankerDisplayName => L("Credit_BankerDisplayName", "Nhân viên ngân hàng");

    // thêm trong class
    private static readonly Color MenuBannerBlack = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color MenuBannerGold = Color.FromArgb(255, 255, 215, 0);

    private static string QuickPayBaseTitle => L("Credit_QuickPayBaseTitle", "Tất toán nợ trước hạn");
    private readonly BadgeSet _debtBadge = new BadgeSet
    {
        NormalDictionary = "commonmenu",
        NormalTexture = "shop_new_star",
        HoveredDictionary = "commonmenu",
        HoveredTexture = "shop_new_star_b"
    };

    private readonly string _collateralStateRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "GTA V Mods", "Fleeca Bank Loan");

    private sealed class CollateralStateEntry
    {
        public uint ModelHash;
        public string Plate;
        public float X;
        public float Y;
        public float Z;
        public float Heading;
    }

    private NativeMenu _loanCollateralMenu;
    private NativeItem _loanCollateralItem;
    private bool _loanCollateralMenuInitialized = false;

    private bool _loanCollateralMode = false;

    private static readonly BadgeSet _lockBadge = new BadgeSet
    {
        NormalDictionary = "commonmenu",
        NormalTexture = "shop_lock",
        HoveredDictionary = "commonmenu",
        HoveredTexture = "shop_lock_b"
    };

    private const int QUICK_PAY_FEE_RATE_STATE_VERSION = 2;
    private const decimal NO_COLLATERAL_DAILY_RATE = 0.04m;
    private const decimal COLLATERAL_RATE_3 = 0.0075m;
    private const decimal COLLATERAL_RATE_2 = 0.015m;
    private const decimal COLLATERAL_RATE_1 = 0.03m;
    private const decimal COLLATERAL_RATE_0 = 0.08m;

    private readonly ObjectPool _uiPool = new ObjectPool();
    private NativeMenu _loanMenu;
    private NativeItem _loanConfirmItem;
    private QuickPayDebtItem _loanQuickPayItem;
    private NativeItem _loanCancelItem;
    private bool _loanMenuInitialized = false;

    private NativeMenu _loanTypeMenu;
    private NativeItem _loanTypePackageItem;
    private NativeItem _loanTypeOptionItem;
    private NativeItem _loanTypeBackItem;
    private bool _loanTypeMenuInitialized = false;

    private NativeMenu _loanPackageMenu;
    private readonly List<LoanPackageOption> _loanPackageOptions = new List<LoanPackageOption>();
    private NativeItem _loanPackageConfirmItem;
    private NativeItem _loanPackageBackItem;
    private bool _loanPackageMenuInitialized = false;

    private long _selectedLoanPackageAmount = 0;
    private bool _loanPackageFlowPending = false;

    private NativeMenu _loanDetailMenu;
    private NativeItem _detailDebtItem;
    private NativeItem _detailFeeItem;
    private NativeItem _detailTotalItem;
    private NativeItem _detailConfirmItem;
    private NativeItem _detailCancelItem;
    private bool _loanDetailMenuInitialized = false;

    private int _loanStartDayStamp = -1;   // ngày game bắt đầu khoản vay

    private const int CHARACTER_SWITCH_GRACE_MS = 5000;
    private bool _catchUpPendingAfterSwitch = false;
    private int _catchUpReadyAtGameTime = 0;
    private int _activeCharacterHash = 0;

    private static readonly Random _rng = new Random();

    private const int FRANKLIN_HASH = -1692214353;
    private const int MICHAEL_HASH = 225514697;
    private const int TREVOR_HASH = -1686040670;

    private string FleecaNotifTitle => L("Credit_NotificationTitle", "Fleeca Bank");
    // Dùng icon ngân hàng Fleeca cho toàn bộ notification của mod
    private static readonly NotificationIcon FleecaNotifIcon = NotificationIcon.BankFleeca;

    private const decimal QUICK_PAY_FEE_RATE_FIRST_7_DAYS_BASE = 0.0731m;   // 0 -> 6 ngày
    private const decimal QUICK_PAY_FEE_RATE_NEXT_10_DAYS_BASE = 0.0370m;    // 7 -> 16 ngày
    private const decimal QUICK_PAY_FEE_RATE_AFTER_17_DAYS_BASE = 0.0167m;   // từ ngày 17 trở đi

    // Dao động ngẫu nhiên: -0.50% .. +0.50%
    private const decimal QUICK_PAY_FEE_RATE_RANDOM_OFFSET_MAX = 0.0050m;

    // Lưu rate đã chốt cho từng tier của riêng khoản vay hiện tại
    private decimal _quickPayFeeRateFirst7Days = -1m;
    private decimal _quickPayFeeRateNext10Days = -1m;
    private decimal _quickPayFeeRateAfter17Days = -1m;

    private readonly string _stateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Fleeca Bank Loan");

    private static string L(string key, string fallback = "")
    {
        return Language.Get(key, fallback);
    }

    private static string FormatMoney(long value)
    {
        if (value < 0) value = 0;
        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private int _stateOwnerHash = 0;

    private CustomiFruit _phoneInstance = null;
    private bool _contactAdded = false;

    private iFruitContact _fleecaContact = null;
    private bool _fleecaContactAnsweredBound = false;

    private bool _stateLoaded = false;
    private bool _loanActive = false;
    private long _loanPrincipal = 0;
    private long _loanRemainingDebt = 0;
    private long _loanDailyDue = 0;

    private const int DueCheckIntervalMs = 60000;
    private const int DueWindowHours = 2;

    private int _loanLastChargedDayStamp = -1;   // ngày game cuối cùng đã thu tiền
    private int _dueWindowStartHour = -1;
    private int _dueWindowEndHour = -1;
    private int _nextDueCheckAtGameTime = 0;     // throttle check 60 giây

    private bool _loanDialogActive = false;

    public FleecaBankLoanScript()
    {
        Interval = 0;
        Tick += OnTick;
        KeyDown += OnKeyDown;

        SyncStateForCurrentCharacter();
    }

    // 2) Thêm class này בתוך cùng file, داخل class FleecaBankLoanScript
    private sealed class QuickPayDebtItem : NativeItem
    {
        public QuickPayDebtItem(string title, string description)
            : base(title, description)
        {
        }

        public override void Recalculate(PointF pos, SizeF size, bool selected)
        {
            base.Recalculate(pos, size, selected);

            // Nếu đang chọn item này thì ẩn icon, title trở về như item bình thường
            if (selected)
            {
                badgeLeft = null;
                title.Position = new PointF(pos.X + 6, pos.Y + 3);
            }
            else if (LeftBadgeSet != null)
            {
                // Không selected và đang có nợ => giữ icon như logic hiện tại
                title.Position = new PointF(pos.X + 40, pos.Y + 3);
            }

            altTitle.Position = new PointF(pos.X + size.Width - altTitle.Width - 6, pos.Y + 3);
            UpdateColors();
        }
    }

    private sealed class LoanPackageOption
    {
        public long Amount;
        public NativeCheckboxItem Item;
    }

    // Item có badge ở góc phải, tự ẩn khi đang selected
    private sealed class RightBadgeMenuItem : NativeItem
    {
        private readonly BadgeSet _rightBadge;
        private readonly Func<bool> _shouldShowBadge;

        public RightBadgeMenuItem(string title, string description, BadgeSet rightBadge, Func<bool> shouldShowBadge = null)
            : base(title, description)
        {
            _rightBadge = rightBadge;
            _shouldShowBadge = shouldShowBadge ?? (() => true);
        }

        public override void Recalculate(PointF pos, SizeF size, bool selected)
        {
            base.Recalculate(pos, size, selected);

            // Chỉ hiện icon ở bên phải khi không selected và điều kiện cho phép
            SetRightBadgeSetIfExists(this, (!selected && _shouldShowBadge()) ? _rightBadge : null);

            UpdateColors();
        }
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            SyncStateForCurrentCharacter();

            EnsureFleecabankContactRegistered();
            EnsureLoanMenuCreated();
            SyncCollateralStateFromFileForCurrentCharacter();

            UpdateLemonUiMouseState();   // NEW
            _uiPool.Process();
            UpdateLemonUiMouseState();   // NEW

            HandleNpcSpawnTimer();
            UpdateNpcSeatSequence();
            UpdateNpcInteractionPrompt();
            ProcessLoanDuePayments();
        }
        catch (Exception ex)
        {
            Log("OnTick failed: " + ex);
        }
    }

    private void ResetQuickPayFeeRates()
    {
        _quickPayFeeRateFirst7Days = -1m;
        _quickPayFeeRateNext10Days = -1m;
        _quickPayFeeRateAfter17Days = -1m;
    }

    private void UpdateLemonUiMouseState()
    {
        try
        {
            bool anyMenuVisible =
                (_loanMenu != null && _loanMenu.Visible) ||
                (_loanDetailMenu != null && _loanDetailMenu.Visible) ||
                (_loanCollateralMenu != null && _loanCollateralMenu.Visible) ||
                (_loanTypeMenu != null && _loanTypeMenu.Visible) ||
                (_loanPackageMenu != null && _loanPackageMenu.Visible);

            if (!anyMenuVisible)
                return;

            SetBoolPropertyIfExists(_loanMenu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");

            SetBoolPropertyIfExists(_loanDetailMenu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");

            SetBoolPropertyIfExists(_loanCollateralMenu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");

            SetBoolPropertyIfExists(_loanTypeMenu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");

            SetBoolPropertyIfExists(_loanPackageMenu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");

            SetBoolPropertyIfExists(_uiPool, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");
        }
        catch (Exception ex)
        {
            Log("UpdateLemonUiMouseState failed: " + ex);
        }
    }

    private bool IsAnyLoanMenuVisible()
    {
        return
            (_loanMenu != null && _loanMenu.Visible) ||
            (_loanDetailMenu != null && _loanDetailMenu.Visible) ||
            (_loanCollateralMenu != null && _loanCollateralMenu.Visible) ||
            (_loanTypeMenu != null && _loanTypeMenu.Visible) ||
            (_loanPackageMenu != null && _loanPackageMenu.Visible);
    }

    // thêm hàm này trong class
    private bool HasActiveDebt()
    {
        return _loanActive && _loanRemainingDebt > 0;
    }

    private bool IsFleecaBankLockedForCurrentCharacter()
    {
        return CityBlackoutHackerState.IsFleecaBankLockedForCurrentCharacter();
    }

    private bool EnsureFleecaBankAccessAllowed()
    {
        if (!CityBlackoutHackerState.IsFleecaBankLockedForCurrentCharacter())
            return true;

        ShowFleecaNotification(
            L("Credit_NotificationTitle", "Thông báo"),
            L("Credit_BankLockedMessage", "Ngân hàng đã khóa giao dịch, hãy quay lại sau."));

        TryClosePhone();
        return false;
    }

    private void RefreshLoanMenuQuickPayText()
    {
        if (_loanQuickPayItem == null)
            return;

        _loanQuickPayItem.Title = L("Credit_MenuQuickPay", "Tất toán trước hạn");
        _loanQuickPayItem.LeftBadgeSet = HasActiveDebt() ? _debtBadge : null;
    }

    private void ApplyLoanMenuTheme(NativeMenu menu)
    {
        try
        {
            if (menu == null)
                return;

            if (menu.BannerText != null)
                menu.BannerText.Color = MenuBannerGold;

            // Header rectangle nội bộ của LemonUI 2.2 (theo source hiện tại)
            FieldInfo fi = typeof(NativeMenu).GetField("nameImage", BindingFlags.Instance | BindingFlags.NonPublic);
            object rect = fi?.GetValue(menu);

            if (rect != null)
            {
                PropertyInfo colorProp = rect.GetType().GetProperty("Color", BindingFlags.Instance | BindingFlags.Public);
                if (colorProp != null && colorProp.CanWrite)
                    colorProp.SetValue(rect, MenuBannerBlack, null);
            }
        }
        catch (Exception ex)
        {
            Log("ApplyLoanMenuTheme failed: " + ex);
        }
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
                PropertyInfo prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(bool))
                    continue;

                prop.SetValue(target, value, null);
                return;
            }
        }
        catch
        {
            // cố tình bỏ qua để không làm hỏng logic chính
        }
    }

    private static void SetRightBadgeSetIfExists(NativeItem item, BadgeSet badge)
    {
        try
        {
            if (item == null)
                return;

            Type t = item.GetType();

            // Ưu tiên property nếu LemonUI build hiện tại có hỗ trợ
            string[] propertyNames = { "RightBadgeSet", "BadgeRightSet", "RightBadge" };
            foreach (string name in propertyNames)
            {
                PropertyInfo prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(BadgeSet))
                {
                    prop.SetValue(item, badge, null);
                    return;
                }
            }

            // Fallback qua field nội bộ nếu property không tồn tại
            string[] fieldNames = { "badgeRight", "rightBadge", "_rightBadge", "m_rightBadge" };
            foreach (string name in fieldNames)
            {
                FieldInfo field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(BadgeSet))
                {
                    field.SetValue(item, badge);
                    return;
                }
            }
        }
        catch
        {
            // bỏ qua để không ảnh hưởng logic chính
        }
    }

    private void BuildDueWindowFromCurrentClock()
    {
        GetCurrentInGameClock(out int hour, out _);

        _dueWindowStartHour = (hour / DueWindowHours) * DueWindowHours;
        _dueWindowEndHour = (_dueWindowStartHour + DueWindowHours) % 24;
    }

    private bool IsClockInsideDueWindow(int currentHour, int currentMinute)
    {
        if (_dueWindowStartHour < 0 || _dueWindowEndHour < 0)
            return false;

        // Cửa sổ không qua nửa đêm, ví dụ 20 -> 22
        if (_dueWindowStartHour < _dueWindowEndHour)
            return currentHour >= _dueWindowStartHour && currentHour < _dueWindowEndHour;

        // Cửa sổ qua nửa đêm, ví dụ 22 -> 0
        return currentHour >= _dueWindowStartHour || currentHour < _dueWindowEndHour;
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
        catch { }
        return 0;
    }

    private string GetStateFileForOwner(int ownerHash)
    {
        Directory.CreateDirectory(_stateRoot);
        return Path.Combine(_stateRoot, $"loan_state_{ownerHash}.dat");
    }

    private void SyncStateForCurrentCharacter()
    {
        try
        {
            int currentHash = GetCurrentCharacterHash();
            if (currentHash == 0)
                return;

            if (_stateOwnerHash == currentHash && _stateLoaded)
            {
                RefreshLoanMenuQuickPayText();
                return;
            }

            bool switchedCharacter = (_stateOwnerHash != 0 && _stateOwnerHash != currentHash);

            if (_stateOwnerHash != 0 && _stateOwnerHash != currentHash)
                SaveStateForOwner(_stateOwnerHash);

            LoadStateForOwner(currentHash);
            SyncCollateralStateFromFileForCurrentCharacter();
            _stateOwnerHash = currentHash;
            _activeCharacterHash = currentHash;

            if (switchedCharacter && _loanActive && _loanRemainingDebt > 0)
            {
                _catchUpPendingAfterSwitch = true;
                _catchUpReadyAtGameTime = Game.GameTime + CHARACTER_SWITCH_GRACE_MS;
            }
            else
            {
                _catchUpPendingAfterSwitch = false;
                _catchUpReadyAtGameTime = 0;
            }

            RefreshLoanMenuQuickPayText();
        }
        catch (Exception ex)
        {
            Log("SyncStateForCurrentCharacter failed: " + ex);
        }
    }

    private bool TryStampToDate(int stamp, out DateTime date)
    {
        date = default(DateTime);

        try
        {
            int year = stamp / 10000;
            int month = (stamp / 100) % 100;
            int day = stamp % 100;

            if (year < 1 || day < 1)
                return false;

            // Ưu tiên kiểu 1..12
            if (month >= 1 && month <= 12)
            {
                try
                {
                    date = new DateTime(year, month, day);
                    return true;
                }
                catch { }
            }

            // Fallback nếu tháng đang lưu theo kiểu 0..11
            if (month >= 0 && month <= 11)
            {
                try
                {
                    date = new DateTime(year, month + 1, day);
                    return true;
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    private int GetElapsedInGameDaysSinceLastCharge()
    {
        try
        {
            int currentStamp = GetInGameDayStamp();
            if (currentStamp < 0 || _loanLastChargedDayStamp < 0)
                return 0;

            if (currentStamp <= _loanLastChargedDayStamp)
                return 0;

            if (TryStampToDate(currentStamp, out DateTime nowDate) &&
                TryStampToDate(_loanLastChargedDayStamp, out DateTime lastDate))
            {
                return Math.Max(0, (nowDate.Date - lastDate.Date).Days);
            }

            // fallback an toàn
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    private int GetInGameDayStamp()
    {
        try
        {
            int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);
            int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
            int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);

            return (year * 10000) + (month * 100) + day;
        }
        catch
        {
            return -1;
        }
    }

    private int GetElapsedInGameDaysSinceLoanStart()
    {
        try
        {
            int currentStamp = GetInGameDayStamp();
            if (currentStamp < 0 || _loanStartDayStamp < 0)
                return 0;

            if (TryStampToDate(currentStamp, out DateTime nowDate) &&
                TryStampToDate(_loanStartDayStamp, out DateTime startDate))
            {
                return Math.Max(0, (nowDate.Date - startDate.Date).Days);
            }
        }
        catch { }

        return 0;
    }

    private decimal GetQuickPayFeeRateByLoanAgeDays(int elapsedDays)
    {
        EnsureQuickPayFeeRatesInitialized();

        if (elapsedDays < 7)
            return _quickPayFeeRateFirst7Days;

        if (elapsedDays < 17)
            return _quickPayFeeRateNext10Days;

        return _quickPayFeeRateAfter17Days;
    }

    private string GetQuickPayTierLabel(int elapsedDays)
    {
        if (elapsedDays < 7)
            return L("Credit_QuickPayTier_0_6", "0-6 ngày");

        if (elapsedDays < 17)
            return L("Credit_QuickPayTier_7_16", "7-16 ngày");

        return L("Credit_QuickPayTier_17Plus", "Từ ngày 17");
    }

    private static decimal RoundQuickPayRate(decimal rate)
    {
        // rate là dạng fraction, ví dụ 0.0712 = 7.12%
        return Math.Round(rate, 4, MidpointRounding.AwayFromZero);
    }

    private decimal RollQuickPayRate(decimal baseRate)
    {
        // -50..+50 basis points = -0.50%..+0.50%
        int offsetBp = _rng.Next(-50, 51);
        decimal offset = offsetBp / 10000m;

        decimal finalRate = baseRate + offset;
        if (finalRate < 0m)
            finalRate = 0m;

        return RoundQuickPayRate(finalRate);
    }

    private void EnsureQuickPayFeeRatesInitialized()
    {
        if (_quickPayFeeRateFirst7Days < 0m)
            _quickPayFeeRateFirst7Days = RollQuickPayRate(QUICK_PAY_FEE_RATE_FIRST_7_DAYS_BASE);

        if (_quickPayFeeRateNext10Days < 0m)
            _quickPayFeeRateNext10Days = RollQuickPayRate(QUICK_PAY_FEE_RATE_NEXT_10_DAYS_BASE);

        if (_quickPayFeeRateAfter17Days < 0m)
            _quickPayFeeRateAfter17Days = RollQuickPayRate(QUICK_PAY_FEE_RATE_AFTER_17_DAYS_BASE);
    }

    private decimal GetCurrentQuickPayFeeRate()
    {
        int elapsedDays = GetElapsedInGameDaysSinceLoanStart();
        return GetQuickPayFeeRateByLoanAgeDays(elapsedDays);
    }

    private string GetDueWindowText()
    {
        if (_dueWindowStartHour < 0 || _dueWindowEndHour < 0)
            return "N/A";

        return string.Format("{0:00}:00~{1:00}:00", _dueWindowStartHour, _dueWindowEndHour);
    }

    private void EnsureFleecabankContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_phoneInstance, phone))
            {
                _phoneInstance = phone;
                _fleecaContact = null;
                _fleecaContactAnsweredBound = false;
                _contactAdded = false;
            }

            // Đang bị khóa thì tắt contact, không cho gọi
            if (IsFleecaBankLockedForCurrentCharacter())
            {
                if (_fleecaContact == null)
                {
                    _fleecaContact = phone.Contacts.FirstOrDefault(c =>
                        string.Equals(c.Name, ContactName, StringComparison.OrdinalIgnoreCase));
                }

                if (_fleecaContact != null)
                    _fleecaContact.Active = false;

                _contactAdded = true;
                return;
            }

            if (_fleecaContact == null)
            {
                _fleecaContact = phone.Contacts.FirstOrDefault(c =>
                    string.Equals(c.Name, ContactName, StringComparison.OrdinalIgnoreCase));
            }

            if (_fleecaContact == null)
            {
                _fleecaContact = new iFruitContact(ContactName)
                {
                    Active = true,
                    DialTimeout = ContactDialTimeoutMs,
                    Bold = false,
                    Icon = ContactIcon.FleecaBank
                };

                _fleecaContact.Answered += OnFleecabankAnswered;
                _fleecaContactAnsweredBound = true;
                phone.Contacts.Add(_fleecaContact);
            }
            else
            {
                _fleecaContact.Active = true;

                if (!_fleecaContactAnsweredBound)
                {
                    _fleecaContact.Answered += OnFleecabankAnswered;
                    _fleecaContactAnsweredBound = true;
                }
            }

            _contactAdded = true;
        }
        catch (Exception ex)
        {
            Log("EnsureFleecabankContactRegistered failed: " + ex);
        }
    }

    private void OnFleecabankAnswered(iFruitContact sender)
    {
        try
        {
            if (!EnsureFleecaBankAccessAllowed())
                return;

            OpenLoanMenu();
            TryClosePhone();
        }
        catch (Exception ex)
        {
            Log("OnFleecabankAnswered failed: " + ex);
        }
    }

    private void EnsureLoanMenuCreated()
    {
        try
        {
            if (!_loanMenuInitialized)
            {
                _loanMenu = new NativeMenu(
                    FleecaNotifTitle,
                    L("Credit_MenuSubtitle", "CÁC MỤC CỦA NGÂN HÀNG"));

                _loanCollateralItem = new RightBadgeMenuItem(
                    L("Credit_MenuCollateral", "Tài sản thế chấp"),
                    L("Credit_MenuCollateralDesc", "Xem chi tiết các phương tiện đang bị ngân hàng khóa"),
                    _lockBadge,
                    () => GetCurrentCollateralCount() > 0);
                _loanCollateralItem.Activated += (s, e) =>
                {
                    OpenCollateralDetailMenu();
                };

                _loanConfirmItem = new NativeItem(
                    L("Credit_MenuLoan", "Vay tiền"),
                    L("Credit_MenuLoanDesc", "Xác nhận và tiếp tục quy trình vay"));

                _loanQuickPayItem = new QuickPayDebtItem(
                    L("Credit_MenuQuickPay", QuickPayBaseTitle),
                    L("Credit_MenuQuickPayDesc", "Xem chi tiết số nợ cần tất toán của mình"));

                _loanCancelItem = new NativeItem(
                    L("Credit_MenuCancel", "Hủy dịch vụ"));

                _loanMenu.Add(_loanConfirmItem);
                _loanMenu.Add(_loanCollateralItem);
                _loanMenu.Add(_loanQuickPayItem);
                _loanMenu.Add(_loanCancelItem);

                _loanConfirmItem.Activated += (s, e) =>
                {
                    OpenLoanTypeMenu();
                };

                _loanQuickPayItem.Activated += (s, e) =>
                {
                    OpenQuickPayDetailMenu();
                };

                _loanCancelItem.Activated += (s, e) =>
                {
                    CloseAllLoanMenus();
                };

                _uiPool.Add(_loanMenu);
                _loanMenuInitialized = true;
            }

            if (!_loanDetailMenuInitialized)
            {
                _loanDetailMenu = new NativeMenu(
                    L("Credit_NotificationTitle", "Fleeca Bank"),
                    L("Credit_QuickPayTitle", "CHI TIẾT TRẢ NỢ TRƯỚC HẠN"));

                _detailDebtItem = new NativeItem("", "");
                _detailFeeItem = new NativeItem("", "");
                _detailTotalItem = new NativeItem("", "");

                _detailConfirmItem = new NativeItem(
                    L("Credit_QuickPayConfirm", "Xác nhận tất toán khoản nợ"),
                    L("Credit_QuickPayConfirmDesc", "Trả nợ ngay và xử lý theo số tiền hiện có"));

                _detailCancelItem = new NativeItem(
                    L("Credit_QuickPayCancel", "Từ chối tất toán nợ"),
                    L("Credit_QuickPayCancelDesc", "Quay lại menu"));

                _loanDetailMenu.Add(_detailDebtItem);
                _loanDetailMenu.Add(_detailFeeItem);
                _loanDetailMenu.Add(_detailTotalItem);
                _loanDetailMenu.Add(_detailConfirmItem);
                _loanDetailMenu.Add(_detailCancelItem);

                _detailConfirmItem.Activated += (s, e) =>
                {
                    CloseAllLoanMenus();
                    ConfirmQuickSettleRemainingDebt();
                };

                _detailCancelItem.Activated += (s, e) =>
                {
                    CloseAllLoanMenus();
                    OpenLoanMenu();
                };

                _uiPool.Add(_loanDetailMenu);
                _loanDetailMenuInitialized = true;
            }

            // Gọi thêm các hàm cập nhật sau khi menu đã được tạo xong
            RefreshLoanMenuQuickPayText();
            RefreshCollateralMenuText();
            ApplyLoanMenuTheme(_loanMenu);
            ApplyLoanMenuTheme(_loanDetailMenu);
            EnsureLoanTypeMenuCreated();
            EnsureLoanPackageMenuCreated();
        }
        catch (Exception ex)
        {
            Log("EnsureLoanMenuCreated failed: " + ex);
        }
    }

    private void EnsureLoanTypeMenuCreated()
    {
        try
        {
            if (_loanTypeMenuInitialized)
                return;

            _loanTypeMenu = new NativeMenu(
                L("Credit_NotificationTitle", "Fleeca Bank"),
                L("Credit_LoanTypeTitle", "CHI TIẾT CÁC LOẠI VAY VỐN"));

            SetBoolPropertyIfExists(_loanTypeMenu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");

            _loanTypePackageItem = new NativeItem(
                L("Credit_LoanTypePackage", "Vay theo gói có sẵn"),
                L("Credit_LoanTypePackageDesc", "Chọn một gói vay đã được ngân hàng định sẵn"),
                "~HUD_COLOUR_YELLOWLIGHT~>~s~");

            _loanTypeOptionItem = new NativeItem(
                L("Credit_LoanTypeOption", "Vay theo mức tùy chọn"),
                L("Credit_LoanTypeOptionDesc", "Nhập số tiền vay mà bạn mong muốn chính xác, linh hoạt hơn"),
                "~HUD_COLOUR_YELLOWLIGHT~>~s~");

            _loanTypeBackItem = new NativeItem(
                L("Credit_LoanTypeBack", "Quay lại dịch vụ ngân hàng"));

            _loanTypePackageItem.Activated += (s, e) =>
            {
                OpenLoanPackageMenu();
            };

            _loanTypeOptionItem.Activated += (s, e) =>
            {
                if (RequestLoanSpawn())
                    CloseAllLoanMenus();
            };

            _loanTypeBackItem.Activated += (s, e) =>
            {
                if (_loanTypeMenu != null) _loanTypeMenu.Visible = false;
                if (_loanMenu != null) _loanMenu.Visible = true;
            };

            _loanTypeMenu.Add(_loanTypePackageItem);
            _loanTypeMenu.Add(_loanTypeOptionItem);
            _loanTypeMenu.Add(_loanTypeBackItem);

            _uiPool.Add(_loanTypeMenu);
            _loanTypeMenuInitialized = true;
        }
        catch (Exception ex)
        {
            Log("EnsureLoanTypeMenuCreated failed: " + ex);
        }
    }

    private void OpenLoanTypeMenu()
    {
        try
        {
            if (!EnsureFleecaBankAccessAllowed())
                return;

            EnsureLoanTypeMenuCreated();

            if (_loanMenu != null) _loanMenu.Visible = false;
            if (_loanPackageMenu != null) _loanPackageMenu.Visible = false;
            if (_loanTypeMenu != null) _loanTypeMenu.Visible = true;

            UpdateLemonUiMouseState();
        }
        catch (Exception ex)
        {
            Log("OpenLoanTypeMenu failed: " + ex);
        }
    }

    private void EnsureLoanPackageMenuCreated()
    {
        try
        {
            if (_loanPackageMenuInitialized)
                return;

            _loanPackageMenu = new NativeMenu(
                L("Credit_NotificationTitle", "Fleeca Bank"),
                L("Credit_LoanPackageTitle", "CHI TIẾT CÁC MỨC VAY SẴN CÓ"));

            SetBoolPropertyIfExists(_loanPackageMenu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");

            long[] packages = new long[]
            {
                165000L,
                300000L,
                500000L,
                850000L,
                1000000L,
                1500000L,
                2000000L,
                3000000L,
                5000000L,
                8000000L,
                10000000L,
                12000000L,
                20000000L,
                30000000L,
                45000000L,
                60000000L,
                80000000L,
                100000000L,
                120000000L,
                150000000L,
                180000000L,
                200000000L,
                220000000L,
                245000000L,
                260000000L
            };

            _loanPackageOptions.Clear();

            for (int i = 0; i < packages.Length; i++)
            {
                long amount = packages[i];
                var checkbox = new NativeCheckboxItem(
                    string.Format(
                        L("Credit_LoanPackageFormat", "{0}. Vay gói {1}"),
                        i + 1,
                        FormatMoney(amount)),
                    "");

                var option = new LoanPackageOption
                {
                    Amount = amount,
                    Item = checkbox
                };

                checkbox.Activated += (s, e) =>
                {
                    SelectLoanPackage(option.Amount);
                };

                _loanPackageOptions.Add(option);
                _loanPackageMenu.Add(checkbox);
            }

            _loanPackageConfirmItem = new NativeItem(
                L("Credit_LoanPackageConfirm", "Xác nhận gói vay này"));

            _loanPackageBackItem = new NativeItem(
                L("Credit_LoanPackageBack", "Quay lại dịch vụ vay"),
                L("Credit_LoanPackageBackDesc", "Trở lại menu loại vay"));

            _loanPackageConfirmItem.Activated += (s, e) =>
            {
                ConfirmSelectedLoanPackage();
            };

            _loanPackageBackItem.Activated += (s, e) =>
            {
                if (_loanPackageMenu != null) _loanPackageMenu.Visible = false;
                if (_loanTypeMenu != null) _loanTypeMenu.Visible = true;
            };

            _loanPackageMenu.Add(_loanPackageConfirmItem);
            _loanPackageMenu.Add(_loanPackageBackItem);

            _uiPool.Add(_loanPackageMenu);
            _loanPackageMenuInitialized = true;

            RefreshLoanPackageMenu();
        }
        catch (Exception ex)
        {
            Log("EnsureLoanPackageMenuCreated failed: " + ex);
        }
    }

    private void RefreshLoanPackageMenu()
    {
        try
        {
            if (_loanPackageMenu == null)
                return;

            foreach (var opt in _loanPackageOptions)
            {
                if (opt?.Item == null)
                    continue;

                SetCheckboxCheckedIfExists(opt.Item, _selectedLoanPackageAmount == opt.Amount);
            }

            if (_loanPackageConfirmItem != null)
            {
                if (_selectedLoanPackageAmount > 0)
                {
                    _loanPackageConfirmItem.Title = L("Credit_LoanPackageConfirm", "Xác nhận gói vay này");
                    _loanPackageConfirmItem.Description = string.Format(
                        L("Credit_LoanPackageConfirmDescSelected", "Gói đang chọn: {0}"),
                        FormatMoney(_selectedLoanPackageAmount));
                }
                else
                {
                    _loanPackageConfirmItem.Title = L("Credit_LoanPackageConfirm", "Xác nhận gói vay này");
                    _loanPackageConfirmItem.Description = L("Credit_LoanPackageConfirmDescEmpty", "Chọn một gói trước khi xác nhận");
                }
            }
        }
        catch (Exception ex)
        {
            Log("RefreshLoanPackageMenu failed: " + ex);
        }
    }

    private void SelectLoanPackage(long amount)
    {
        try
        {
            long creditPoints = GetCreditPoints();
            long maxLoan = GetLoanLimitFromPoints(creditPoints);

            if (amount > maxLoan)
            {
                ShowFleecaNotification(
                    L("Credit_Notify_Title", "Thông báo"),
                    string.Format(
                        L("Credit_LoanPackageNotEligible",
                        "Bạn chưa đủ điều kiện để chọn gói {0}."),
                        FormatMoney(amount)));
                return;
            }

            _selectedLoanPackageAmount = amount;
            RefreshLoanPackageMenu();
        }
        catch (Exception ex)
        {
            Log("SelectLoanPackage failed: " + ex);
        }
    }

    private void OpenLoanPackageMenu()
    {
        try
        {
            if (!EnsureFleecaBankAccessAllowed())
                return;

            EnsureLoanPackageMenuCreated();
            RefreshLoanPackageMenu();

            if (_loanMenu != null) _loanMenu.Visible = false;
            if (_loanTypeMenu != null) _loanTypeMenu.Visible = false;
            if (_loanPackageMenu != null) _loanPackageMenu.Visible = true;

            UpdateLemonUiMouseState();
        }
        catch (Exception ex)
        {
            Log("OpenLoanPackageMenu failed: " + ex);
        }
    }

    private void ConfirmSelectedLoanPackage()
    {
        try
        {
            if (_selectedLoanPackageAmount <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("Credit_LoanPackageChooseFirst", "Bạn phải chọn một gói vay trước."),
                    2500);
                return;
            }

            if (RequestLoanSpawn(_selectedLoanPackageAmount, true))
                CloseAllLoanMenus();
        }
        catch (Exception ex)
        {
            Log("ConfirmSelectedLoanPackage failed: " + ex);
        }
    }

    private void SetCheckboxCheckedIfExists(object item, bool value)
    {
        try
        {
            if (item == null)
                return;

            Type t = item.GetType();

            string[] propertyNames = { "Checked", "IsChecked", "CheckedState" };
            foreach (string name in propertyNames)
            {
                PropertyInfo prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
                {
                    prop.SetValue(item, value, null);
                    return;
                }
            }

            string[] fieldNames = { "checked", "_checked", "isChecked" };
            foreach (string name in fieldNames)
            {
                FieldInfo field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(bool))
                {
                    field.SetValue(item, value);
                    return;
                }
            }
        }
        catch
        {
            // bỏ qua để không phá logic chính
        }
    }

    private void RefreshCollateralMenuText()
    {
        if (_loanCollateralItem == null)
            return;

        SyncCollateralStateFromFileForCurrentCharacter();

        int count = GetCurrentCollateralCount();

        _loanCollateralItem.Title = L("Credit_MenuCollateral", "Tài sản thế chấp");
        _loanCollateralItem.Description = _loanCollateralMode
            ? string.Format(
                L("Credit_MenuCollateralDescActive", "Ngân hàng đang giữ {0} xe làm tài sản thế chấp."),
                count)
            : L("Credit_MenuCollateralDescInactive", "Không có khoản vay thế chấp nào đang hoạt động.");

        _loanCollateralItem.LeftBadgeSet = null;
    }

    private void EnsureCollateralMenuCreated()
    {
        try
        {
            if (_loanCollateralMenuInitialized)
                return;

            _loanCollateralMenu = new NativeMenu(
                FleecaNotifTitle,
                L("Credit_CollateralTitle", "CHI TIẾT TÀI SẢN THẾ CHẤP"));

            // Ép tắt chuột cho menu chi tiết thế chấp
            SetBoolPropertyIfExists(_loanCollateralMenu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");

            _uiPool.Add(_loanCollateralMenu);
            _loanCollateralMenuInitialized = true;
        }
        catch (Exception ex)
        {
            Log("EnsureCollateralMenuCreated failed: " + ex);
        }
    }

    private void RefreshCollateralDetailMenu()
    {
        try
        {
            if (_loanCollateralMenu == null)
                return;

            SyncCollateralStateFromFileForCurrentCharacter();

            _loanCollateralMenu.Clear();

            int count = GetCurrentCollateralCount();
            long dailyDue = GetCurrentDailyDueAmount();

            _loanCollateralMenu.Add(new NativeItem(
                string.Format(L("Credit_CollateralSummary", "Số xe đang thế chấp: {0}"), count),
                string.Format(L("Credit_CollateralRate", "Tiền thu hằng ngày hiện tại: {0:0.##}%"), (double)(GetCurrentDailyRate() * 100m))
            ));

            _loanCollateralMenu.Add(new NativeItem(
                string.Format(L("Credit_CollateralDaily", "Tiền phải thu hôm nay: {0}"), FormatMoney(dailyDue)),
                L("Credit_CollateralDailyDesc", "Tỷ lệ sẽ thay đổi theo số xe còn tồn tại trong danh sách thế chấp.")
            ));

            var lockedEntries = GetPlayerCollateralVehicles()
                .Where(e => IsCollateralLocked(e))
                .ToList();

            if (lockedEntries.Count == 0)
            {
                _loanCollateralMenu.Add(new NativeItem(
                    L("Credit_CollateralEmpty", "Không có xe thế chấp"),
                    L("Credit_CollateralEmptyDesc", "Hiện tại không có tài sản nào đang bị khóa.")
                ));
            }
            else
            {
                int index = 1;
                foreach (var entry in lockedEntries)
                {
                    string name = GetEntryDisplayName(entry);
                    var item = new RightBadgeMenuItem(
                        string.Format("{0}. {1}", index, name),
                        L("Credit_CollateralLockedDesc", "Phương tiện này đang bị cược thành tài sản thế chấp, không thể thanh lý."),
                        _lockBadge
                    );
                    _loanCollateralMenu.Add(item);
                    index++;
                }
            }

            var back = new NativeItem(
                L("Credit_CollateralBack", "Quay lại trang trước"),
                L("Credit_CollateralBackDesc", "Trở về menu Fleeca Bank")
            );
            back.Activated += (s, e) =>
            {
                if (_loanCollateralMenu != null) _loanCollateralMenu.Visible = false;
                if (_loanMenu != null) _loanMenu.Visible = true;
            };
            _loanCollateralMenu.Add(back);
        }
        catch (Exception ex)
        {
            Log("RefreshCollateralDetailMenu failed: " + ex);
        }
    }

    private void OpenCollateralDetailMenu()
    {
        try
        {
            if (!EnsureFleecaBankAccessAllowed())
                return;

            // Thêm hàm đồng bộ trạng thái từ file cho nhân vật hiện tại
            SyncCollateralStateFromFileForCurrentCharacter();

            EnsureCollateralMenuCreated();
            RefreshCollateralDetailMenu();
            UpdateLemonUiMouseState();

            if (_loanMenu != null)
                _loanMenu.Visible = false;

            if (_loanCollateralMenu != null)
                _loanCollateralMenu.Visible = true;

            UpdateLemonUiMouseState();
        }
        catch (Exception ex)
        {
            Log("OpenCollateralDetailMenu failed: " + ex);
        }
    }

    private void OpenQuickPayDetailMenu()
    {
        try
        {
            if (!EnsureFleecaBankAccessAllowed())
                return;

            if (!_loanActive || _loanRemainingDebt <= 0)
            {
                ShowFleecaNotification(
                    L("Credit_NotificationTitle", "Thông báo"),
                    L("Credit_NoDebt", "Bạn không có khoản nợ nào cần thanh toán.")
                );
                return;
            }

            RefreshQuickPayDetailMenu();
            UpdateLemonUiMouseState();

            if (_loanMenu != null) _loanMenu.Visible = false;
            if (_loanDetailMenu != null) _loanDetailMenu.Visible = true;

            UpdateLemonUiMouseState();
        }
        catch (Exception ex)
        {
            Log("OpenQuickPayDetailMenu failed: " + ex);
        }
    }

    private void RefreshQuickPayDetailMenu()
    {
        try
        {
            if (_detailDebtItem == null || _detailFeeItem == null || _detailTotalItem == null)
                return;

            long total = GetQuickPayTotal();
            int elapsedDays = GetElapsedInGameDaysSinceLoanStart();
            decimal feeRate = GetCurrentQuickPayFeeRate();

            _detailDebtItem.Title = string.Format(
                L("Credit_QuickPayDebt", "Số tiền nợ còn lại: {0}"),
                FormatMoney(_loanRemainingDebt));

            _detailFeeItem.Title = string.Format(
                L("Credit_QuickPayFee", "Tỷ lệ phí hiện tại: ~HUD_COLOUR_DEGEN_CYAN~{0:0.00}%~s~"),
                (double)(feeRate * 100m),
                GetQuickPayTierLabel(elapsedDays));

            _detailTotalItem.Title = string.Format(
                L("Credit_QuickPayTotal", "Số tiền cần tất toán: ~HUD_COLOUR_DEGEN_YELLOW~{0}"),
                FormatMoney(total));
        }
        catch (Exception ex)
        {
            Log("RefreshQuickPayDetailMenu failed: " + ex);
        }
    }

    private void CloseAllLoanMenus()
    {
        try
        {
            if (_loanMenu != null) _loanMenu.Visible = false;
            if (_loanDetailMenu != null) _loanDetailMenu.Visible = false;
            if (_loanCollateralMenu != null) _loanCollateralMenu.Visible = false;
            if (_loanTypeMenu != null) _loanTypeMenu.Visible = false;
            if (_loanPackageMenu != null) _loanPackageMenu.Visible = false;
        }
        catch (Exception ex)
        {
            Log("CloseAllLoanMenus failed: " + ex);
        }
    }

    private void OpenLoanMenu()
    {
        try
        {
            if (!EnsureFleecaBankAccessAllowed())
                return;

            EnsureLoanMenuCreated();
            SyncCollateralStateFromFileForCurrentCharacter();

            if (_loanTypeMenu != null) _loanTypeMenu.Visible = false;
            if (_loanPackageMenu != null) _loanPackageMenu.Visible = false;
            if (_loanDetailMenu != null) _loanDetailMenu.Visible = false;
            if (_loanCollateralMenu != null) _loanCollateralMenu.Visible = false;

            RefreshLoanMenuQuickPayText();
            ApplyLoanMenuTheme(_loanMenu);
            ApplyLoanMenuTheme(_loanDetailMenu);
            UpdateLemonUiMouseState();

            if (_loanMenu != null)
                _loanMenu.Visible = true;

            UpdateLemonUiMouseState();
        }
        catch (Exception ex)
        {
            Log("OpenLoanMenu failed: " + ex);
        }
    }

    private void CloseLoanMenu()
    {
        try
        {
            if (_loanMenu != null)
                _loanMenu.Visible = false;
        }
        catch (Exception ex)
        {
            Log("CloseLoanMenu failed: " + ex);
        }
    }

    private void OnLoanMenuItemSelected(object sender, EventArgs e)
    {
        try
        {
            if (sender == _loanConfirmItem)
            {
                CloseLoanMenu();
                RequestLoanSpawn();
                return;
            }

            if (sender == _loanQuickPayItem)
            {
                CloseLoanMenu();
                GetQuickPayTotal();
                return;
            }

            if (sender == _loanCancelItem)
            {
                CloseLoanMenu();
                return;
            }
        }
        catch (Exception ex)
        {
            Log("OnLoanMenuItemSelected failed: " + ex);
        }
    }

    private bool RequestLoanSpawn(long presetLoanAmount = 0L, bool isPackageLoan = false)
    {
        try
        {
            if (!EnsureFleecaBankAccessAllowed())
                return false;

            int currentHash = GetCurrentCharacterHash();
            if (currentHash == 0)
            {
                ShowFleecaNotification(
                    L("Credit_Notify_Title", "Thông báo"),
                    L("Credit_Error_CharacterNotFound", "Không xác định được nhân vật hiện tại."));
                return false;
            }

            if (_loanActive)
            {
                ShowFleecaNotification(
                    L("Credit_CurrentLoan_Title", "Khoản vay hiện tại"),
                    string.Format(
                        L("Credit_CurrentLoan_Desc", "Bạn đang có khoản vay chưa thanh toán: ${0:N0} còn lại."),
                        _loanRemainingDebt));
                return false;
            }

            _selectedLoanPackageAmount = presetLoanAmount > 0 ? presetLoanAmount : 0;
            _loanPackageFlowPending = isPackageLoan && presetLoanAmount > 0;

            _spawnRequested = true;
            _spawnExecuteAtGameTime = Game.GameTime + SpawnDelayMs;

            TryClosePhone();
            return true;
        }
        catch (Exception ex)
        {
            Log("RequestLoanSpawn failed: " + ex);
            return false;
        }
    }

    private long GetQuickPayTotal()
    {
        try
        {
            if (_loanRemainingDebt <= 0)
                return 0;

            decimal feeRate = GetCurrentQuickPayFeeRate();
            return (long)Math.Ceiling((decimal)_loanRemainingDebt * (1m + feeRate));
        }
        catch
        {
            return _loanRemainingDebt;
        }
    }

    private void ConfirmQuickSettleRemainingDebt()
    {
        try
        {
            if (!EnsureFleecaBankAccessAllowed())
                return;

            int currentHash = GetCurrentCharacterHash();
            if (currentHash == 0)
            {
                ShowFleecaNotification(
                    L("Credit_ErrorTitle", "Lỗi xác thực"),
                    L("Credit_ErrorNoCharacter", "Không xác định được nhân vật hiện tại."));
                return;
            }

            if (_stateOwnerHash != 0 && _stateOwnerHash != currentHash)
            {
                ShowFleecaNotification(
                    L("Credit_DisbursementTitle", "Giải ngân"),
                    L("Credit_NotYourLoan", "Khoản vay này không phải của bạn"));
                return;
            }

            if (!_loanActive || _loanRemainingDebt <= 0)
            {
                ShowFleecaNotification(
                    L("Credit_MessageTitle", "Tin nhắn ngân hàng"),
                    L("Credit_NoDebt", "Bạn không có khoản nợ nào cần thanh toán."));
                return;
            }

            long totalPayoff = GetQuickPayTotal();
            long money = Math.Max(0, Game.Player.Money);

            // NHÁNH 1: TRẢ ĐỦ TIỀN
            if (money >= totalPayoff)
            {
                DeductPlayerMoney(totalPayoff);

                _loanRemainingDebt = 0;
                _loanActive = false;
                _loanPrincipal = 0;
                _loanDailyDue = 0;
                _loanStartDayStamp = -1;
                _loanLastChargedDayStamp = GetInGameDayStamp();
                _nextDueCheckAtGameTime = 0;

                // Mở khóa xe và tắt chế độ thế chấp trước khi lưu trạng thái
                UnlockAllCollateralVehiclesForCurrentCharacter();
                _loanCollateralMode = false;

                SaveStateForOwner(currentHash);
                RefreshLoanMenuQuickPayText();

                ShowFleecaNotification(
                    L("Credit_QuickPaySuccessTitle", "Tất toán sớm"),
                    string.Format(
                        L("Credit_QuickPaySuccessDesc",
                        "Đã tất toán nợ trước hạn. Số tiền đã trừ: ~HUD_COLOUR_DEGEN_GREEN~${0:N0}~s~."),
                        totalPayoff));

                DespawnBankerNpc();
                return;
            }

            int allMoney = (int)Math.Max(0, Game.Player.Money);
            if (allMoney > 0)
                DeductPlayerMoney(allMoney);

            ShowFleecaNotification(
                L("Credit_WarningTitle", "Cảnh báo ngân hàng"),
                L("Credit_NotEnoughMoneyQuickPay", "Bạn không đủ tiền để tất toán."));

            bool seized = TrySeizeOneOwnedVehicleForCurrentCharacter(currentHash);

            // NHÁNH 2: BỊ TỊCH THU XE VÀ XÓA NỢ (KHOẢN VAY ĐƯỢC ĐÓNG)
            if (seized)
            {
                _loanRemainingDebt = 0;
                _loanActive = false;
                _loanPrincipal = 0;
                _loanDailyDue = 0;
                _loanLastChargedDayStamp = GetInGameDayStamp();
                _nextDueCheckAtGameTime = 0;

                // Mở khóa các xe còn lại (nếu có) và tắt chế độ thế chấp trước khi lưu trạng thái
                UnlockAllCollateralVehiclesForCurrentCharacter();
                _loanCollateralMode = false;

                SaveStateForOwner(currentHash);
                RefreshLoanMenuQuickPayText();

                ShowFleecaNotification(
                    L("Credit_SeizureTitle", "Siết nợ"),
                    L("Credit_SeizureDesc",
                    "Ngân hàng đã tịch thu phương tiện để tất toán khoản nợ vì bạn không đủ khả năng trả nợ."));

                DespawnBankerNpc();
            }
            else
            {
                long stillOwe = Math.Max(0, totalPayoff - allMoney);
                _loanRemainingDebt = stillOwe;
                _loanActive = _loanRemainingDebt > 0;

                SaveStateForOwner(currentHash);
                RefreshLoanMenuQuickPayText();

                ShowFleecaNotification(
                    L("Credit_WarningShortTitle", "Cảnh báo"),
                    L("Credit_NoVehicleSeized",
                    "Không tìm thấy phương tiện nào để tịch thu. Khoản nợ ~HUD_COLOUR_REDLIGHT~vẫn còn tồn tại.~s~"));
            }
        }
        catch (Exception ex)
        {
            Log("ConfirmQuickSettleRemainingDebt failed: " + ex);
        }
    }

    private string GetCollateralStateFileForOwner(int ownerHash)
    {
        Directory.CreateDirectory(_collateralStateRoot);
        return Path.Combine(_collateralStateRoot, $"collateral_state_{ownerHash}.dat");
    }

    private static string NormalizePlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
            return "";

        return new string(plate.Trim().Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
    }

    private static string BuildCollateralKey(uint modelHash, string plate, Vector3 pos)
    {
        string normalizedPlate = NormalizePlate(plate);

        if (!string.IsNullOrEmpty(normalizedPlate))
            return $"{modelHash:X}|PLATE|{normalizedPlate}";

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:X}|POS|{1:0.0}|{2:0.0}|{3:0.0}",
            modelHash, pos.X, pos.Y, pos.Z);
    }

    private static bool TryGetEntryPlate(object entry, out string plate)
    {
        plate = "";

        try
        {
            var t = entry.GetType();
            var f = t.GetField("Plate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return false;

            object v = f.GetValue(entry);
            if (v is string s)
            {
                plate = s;
                return true;
            }
        }
        catch { }

        return false;
    }

    private static bool TryGetEntryPosition(object entry, out Vector3 pos)
    {
        pos = Vector3.Zero;

        try
        {
            var t = entry.GetType();
            var f = t.GetField("Position", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return false;

            object v = f.GetValue(entry);
            if (v is Vector3 p)
            {
                pos = p;
                return true;
            }
        }
        catch { }

        return false;
    }

    private static bool TryGetEntryRuntimeVehicle(object entry, out Vehicle veh)
    {
        veh = null;

        try
        {
            var t = entry.GetType();
            var f = t.GetField("RuntimeVehicle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return false;

            object v = f.GetValue(entry);
            if (v is Vehicle vehicle)
            {
                veh = vehicle;
                return true;
            }
        }
        catch { }

        return false;
    }

    private static bool TryGetEntryHeading(object entry, out float heading)
    {
        heading = 0f;

        try
        {
            var t = entry.GetType();
            var f = t.GetField("Heading", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return false;

            object v = f.GetValue(entry);
            if (v is float h)
            {
                heading = h;
                return true;
            }
        }
        catch { }

        return false;
    }

    private List<CollateralStateEntry> LoadCollateralStateForOwner(int ownerHash)
    {
        var result = new List<CollateralStateEntry>();

        try
        {
            string file = GetCollateralStateFileForOwner(ownerHash);
            if (!File.Exists(file))
                return result;

            foreach (var line in File.ReadAllLines(file))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var p = line.Split('|');
                    if (p.Length < 6)
                        continue;

                    uint modelHash;
                    if (!uint.TryParse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out modelHash) &&
                        !uint.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out modelHash))
                    {
                        continue;
                    }

                    float x = 0f, y = 0f, z = 0f, heading = 0f;
                    float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                    float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
                    float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
                    float.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out heading);

                    result.Add(new CollateralStateEntry
                    {
                        ModelHash = modelHash,
                        Plate = p[1],
                        X = x,
                        Y = y,
                        Z = z,
                        Heading = heading
                    });
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log("LoadCollateralStateForOwner failed: " + ex);
        }

        return result;
    }

    private void SaveCollateralStateForOwner(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return;

            Directory.CreateDirectory(_collateralStateRoot);

            var vehicles = GetPlayerCollateralVehicles();
            var lines = new List<string>();

            foreach (var entry in vehicles)
            {
                try
                {
                    if (!IsCollateralLocked(entry))
                        continue;

                    uint modelHash = GetEntryModelHash(entry);
                    if (modelHash == 0)
                        continue;

                    string plate = "";
                    Vector3 pos = Vector3.Zero;
                    float heading = 0f;

                    TryGetEntryPlate(entry, out plate);

                    Vehicle veh;
                    if (TryGetEntryRuntimeVehicle(entry, out veh) && veh != null && veh.Exists())
                    {
                        pos = veh.Position;
                        heading = veh.Heading;
                    }
                    else
                    {
                        TryGetEntryPosition(entry, out pos);
                        TryGetEntryHeading(entry, out heading);
                    }

                    lines.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:X}|{1}|{2:0.###}|{3:0.###}|{4:0.###}|{5:0.###}",
                        modelHash,
                        (plate ?? "").Replace("|", ""),
                        pos.X, pos.Y, pos.Z,
                        heading
                    ));
                }
                catch { }
            }

            string file = GetCollateralStateFileForOwner(ownerHash);
            File.WriteAllLines(file, lines.ToArray());
        }
        catch (Exception ex)
        {
            Log("SaveCollateralStateForOwner failed: " + ex);
        }
    }

    private bool IsEntryLockedByCollateralFile(object entry, List<CollateralStateEntry> states)
    {
        try
        {
            if (entry == null || states == null || states.Count == 0)
                return false;

            uint modelHash = GetEntryModelHash(entry);
            if (modelHash == 0)
                return false;

            string plate = "";
            Vector3 pos = Vector3.Zero;

            TryGetEntryPlate(entry, out plate);

            Vehicle veh;
            if (TryGetEntryRuntimeVehicle(entry, out veh) && veh != null && veh.Exists())
                pos = veh.Position;
            else
                TryGetEntryPosition(entry, out pos);

            string entryKey = BuildCollateralKey(modelHash, plate, pos);

            foreach (var s in states)
            {
                try
                {
                    string stateKey = BuildCollateralKey(s.ModelHash, s.Plate, new Vector3(s.X, s.Y, s.Z));
                    if (string.Equals(entryKey, stateKey, StringComparison.OrdinalIgnoreCase))
                        return true;

                    // fallback theo model + plate nếu có
                    if (s.ModelHash == modelHash &&
                        !string.IsNullOrWhiteSpace(s.Plate) &&
                        NormalizePlate(s.Plate) == NormalizePlate(plate))
                    {
                        return true;
                    }

                    // fallback theo model + khoảng cách
                    if (s.ModelHash == modelHash)
                    {
                        var savedPos = new Vector3(s.X, s.Y, s.Z);
                        if (savedPos.DistanceTo2D(pos) < 5f)
                            return true;
                    }
                }
                catch { }
            }
        }
        catch { }

        return false;
    }

    private void SyncCollateralStateFromFileForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            var states = LoadCollateralStateForOwner(ownerHash);
            if (states.Count == 0)
                return;

            bool changed = false;
            var entries = GetPlayerCollateralVehicles();

            foreach (var entry in entries)
            {
                try
                {
                    bool shouldLock = IsEntryLockedByCollateralFile(entry, states);
                    bool currentLock = IsCollateralLocked(entry);

                    if (currentLock != shouldLock)
                    {
                        SetCollateralLocked(entry, shouldLock);
                        changed = true;
                    }
                }
                catch { }
            }

            if (changed)
                MarkPersistentVehiclesDirtyAndSave();
        }
        catch (Exception ex)
        {
            Log("SyncCollateralStateFromFileForCurrentCharacter failed: " + ex);
        }
    }

    private void RefreshCollateralStateFileForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            SaveCollateralStateForOwner(ownerHash);
        }
        catch (Exception ex)
        {
            Log("RefreshCollateralStateFileForCurrentCharacter failed: " + ex);
        }
    }

    private void BeginLoanDialog()
    {
        try
        {
            if (!EnsureFleecaBankAccessAllowed())
                return;

            if (_loanDialogActive)
                return;

            if (_loanActive)
            {
                ShowFleecaNotification(
                    L("Credit_CurrentLoanTitle", "Khoản vay hiện tại"),
                    string.Format(
                        L("Credit_CurrentLoanDesc", "Bạn đang có khoản vay chưa thanh toán: ~HUD_COLOUR_REDLIGHT~${0:N0}~s~."),
                        _loanRemainingDebt));
                return;
            }

            if (_loanPackageFlowPending && _selectedLoanPackageAmount > 0)
            {
                long packageAmount = _selectedLoanPackageAmount;
                StartLoan(packageAmount);

                if (_loanActive && _loanPrincipal == packageAmount)
                {
                    _loanPackageFlowPending = false;
                    _selectedLoanPackageAmount = 0;
                }

                return;
            }

            long creditPoints = GetCreditPoints();
            long maxLoan = GetLoanLimitFromPoints(creditPoints);

            if (maxLoan <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("Credit_NotEligible", "Bạn ~HUD_COLOUR_REDLIGHT~chưa đủ~s~ điều kiện vay!!!"),
                    3000);
                return;
            }

            _loanDialogActive = true;

            string input = Game.GetUserInput(string.Empty);
            if (string.IsNullOrWhiteSpace(input))
                return;

            string digitsOnly = new string(input.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(digitsOnly))
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("Credit_InvalidAmount", "Số tiền ~HUD_COLOUR_REDLIGHT~không hợp lệ~s~."),
                    2500);
                return;
            }

            if (!long.TryParse(digitsOnly, NumberStyles.Integer, CultureInfo.InvariantCulture, out long inputAmount))
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("Credit_InvalidAmount", "Số tiền ~HUD_COLOUR_REDLIGHT~không hợp lệ~s~."),
                    2500);
                return;
            }

            if (inputAmount <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("Credit_AmountMustBePositive", "Số tiền phải ~HUD_COLOUR_DEGEN_YELLOW~lớn hơn 0.~s~"),
                    2500);
                return;
            }

            if (inputAmount > maxLoan)
            {
                GTA.UI.Screen.ShowSubtitle(string.Format(
                    L("Credit_ExceedLimit", "Bạn chỉ có thể vay tối đa ~HUD_COLOUR_DEGEN_YELLOW~${0:N0}~s~ theo hạn mức hiện tại."),
                    maxLoan),
                    3000);
                return;
            }

            StartLoan(inputAmount);
        }
        catch (Exception ex)
        {
            Log("BeginLoanDialog failed: " + ex);
        }
        finally
        {
            _loanDialogActive = false;
        }
    }

    private void StartLoan(long amount)
    {
        try
        {
            if (!EnsureFleecaBankAccessAllowed())
                return;

            amount = Math.Min(amount, 200_000_000L);
            if (amount <= 0)
                return;

            if (_loanActive)
            {
                ShowFleecaNotification(
                    L("Credit_ReminderTitle", "Nhắc nhở"),
                    L("Credit_AlreadyHasLoan", "Bạn đang có khoản vay ~HUD_COLOUR_DEGEN_YELLOW~chưa thanh toán.~s~"));
                return;
            }

            _loanPrincipal = amount;
            _loanRemainingDebt = amount;
            _loanStartDayStamp = GetInGameDayStamp();

            // Mỗi khoản vay mới phải roll lại fee rate từ gốc
            ResetQuickPayFeeRates();
            EnsureQuickPayFeeRatesInitialized();

            // --- Logic mới: Khóa xe và tính lãi ---
            int currentHash = GetCurrentCharacterHash();

            // Cố gắng khóa tối đa 3 xe ngẫu nhiên của nhân vật hiện tại
            int lockedCount = LockRandomCollateralVehiclesForCurrentCharacter(currentHash, 3);

            // Nếu có ít nhất 1 xe bị khóa, chế độ thế chấp (CollateralMode) được kích hoạt
            _loanCollateralMode = lockedCount > 0;

            // Tính toán tiền nợ hàng ngày dựa trên lãi suất động
            // GetCurrentDailyRate() sẽ trả về 1.05% (0.0105m) nếu không có xe nào bị khóa
            _loanDailyDue = (long)Math.Ceiling((decimal)amount * GetCurrentDailyRate());

            _loanLastChargedDayStamp = GetInGameDayStamp();
            BuildDueWindowFromCurrentClock();
            _nextDueCheckAtGameTime = Game.GameTime + DueCheckIntervalMs;

            _loanActive = true;
            Game.Player.Money += (int)amount;

            // Lưu trạng thái và cập nhật UI
            SaveStateForOwner(currentHash);
            RefreshLoanMenuQuickPayText();
            RefreshCollateralMenuText();
            // ---------------------------------------

            ShowFleecaNotification(
                L("Credit_DisbursementTitle", "Giải ngân"),
                string.Format(
                    L("Credit_DisbursementSuccess",
                      "Khoản vay ~HUD_COLOUR_DEGEN_GREEN~${0:N0}~s~ đã được giải ngân thành công.\nTài sản thế chấp: ~HUD_COLOUR_DEGEN_YELLOW~{2} xe~s~. Thu nợ trong ~HUD_COLOUR_DEGEN_CYAN~{1}~s~."),
                    amount,
                    GetDueWindowText(),
                    lockedCount));

            DespawnBankerNpc();
        }
        catch (Exception ex)
        {
            Log("StartLoan failed: " + ex);
        }
    }

    private void ProcessLoanDuePayments()
    {
        try
        {
            int currentHash = GetCurrentCharacterHash();
            if (currentHash == 0)
                return;

            if (_stateOwnerHash != 0 && _stateOwnerHash != currentHash)
                return;

            if (!_loanActive || _loanRemainingDebt <= 0)
                return;

            // --- NHÁNH 1: Catch-up sau khi switch nhân vật ---
            if (_catchUpPendingAfterSwitch)
            {
                if (Game.GameTime < _catchUpReadyAtGameTime)
                    return;

                _catchUpPendingAfterSwitch = false;
                _catchUpReadyAtGameTime = 0;

                int catchUpDays = GetElapsedInGameDaysSinceLastCharge();
                if (catchUpDays > 0)
                {
                    long catchUpDue = GetCurrentDailyDueAmount() * (long)catchUpDays;

                    if (catchUpDue > _loanRemainingDebt)
                        catchUpDue = _loanRemainingDebt;

                    long catchUpCollected = CollectDueAmount(catchUpDue);
                    _loanRemainingDebt = Math.Max(0, _loanRemainingDebt - catchUpCollected);
                    _loanLastChargedDayStamp = GetInGameDayStamp();

                    if (_loanRemainingDebt <= 0)
                    {
                        _loanActive = false;

                        // Giải phóng phương tiện thế chấp
                        UnlockAllCollateralVehiclesForCurrentCharacter();
                        _loanCollateralMode = false;

                        ShowFleecaNotification(
                            L("Credit_PaymentTitle", "Trả nợ"),
                            L("Credit_PaidFull", "Khoản vay đã được thanh toán đầy đủ."));

                        _loanStartDayStamp = -1;
                        SaveStateForOwner(currentHash);
                        RefreshLoanMenuQuickPayText();
                        return;
                    }

                    if (catchUpCollected < catchUpDue)
                    {
                        ShowFleecaNotification(
                            L("Credit_CatchUpTitle", "Bù nợ"),
                            string.Format(
                                L("Credit_CatchUpPartial",
                                  "Đã bù ~HUD_COLOUR_DEGEN_YELLOW~${0:N0}/${1:N0}~s~ sau khi quay lại. Nợ còn lại: ~HUD_COLOUR_REDLIGHT~${2:N0}~s~."),
                                catchUpCollected,
                                catchUpDue,
                                _loanRemainingDebt));
                    }

                    SaveStateForOwner(currentHash);
                }

                return;
            }

            // --- NHÁNH 2: Thu nợ định kỳ (Check mỗi 40s) ---
            if (_nextDueCheckAtGameTime > 0 && Game.GameTime < _nextDueCheckAtGameTime)
                return;

            _nextDueCheckAtGameTime = Game.GameTime + DueCheckIntervalMs;

            int currentDayStamp = GetInGameDayStamp();
            if (currentDayStamp < 0)
                return;

            int daysPassed = GetElapsedInGameDaysSinceLastCharge();
            if (daysPassed <= 0)
                return;

            int currentHour, currentMinute;
            GetCurrentInGameClock(out currentHour, out currentMinute);

            if (!IsClockInsideDueWindow(currentHour, currentMinute))
                return;

            long dueAmount = GetCurrentDailyDueAmount() * (long)daysPassed;

            if (dueAmount > _loanRemainingDebt)
                dueAmount = _loanRemainingDebt;

            long collectedAmount = CollectDueAmount(dueAmount);

            _loanRemainingDebt = Math.Max(0, _loanRemainingDebt - collectedAmount);
            _loanLastChargedDayStamp = currentDayStamp;

            if (_loanRemainingDebt <= 0)
            {
                _loanActive = false;

                // Giải phóng phương tiện thế chấp
                UnlockAllCollateralVehiclesForCurrentCharacter();
                _loanCollateralMode = false;

                ShowFleecaNotification(
                    L("Credit_SettleTitle", "Tất toán khoản nợ"),
                    L("Credit_PaidFull", "Khoản vay đã được thanh toán đầy đủ."));

                SaveStateForOwner(currentHash);
                RefreshLoanMenuQuickPayText();
                return;
            }

            if (collectedAmount < dueAmount)
            {
                ShowFleecaNotification(
                    L("Credit_DailyCollectionTitle", "Thu nợ định kỳ"),
                    string.Format(
                        L("Credit_DailyCollectionPartial",
                          "Hôm nay chỉ thu được ${0:N0}/${1:N0}. Phần còn lại sẽ được xử lý theo quy định."),
                        collectedAmount,
                        dueAmount));
            }

            SaveStateForOwner(currentHash);
            RefreshLoanMenuQuickPayText();
        }
        catch (Exception ex)
        {
            Log("ProcessLoanDuePayments failed: " + ex);
        }
    }

    private bool TrySeizeOneOwnedVehicleForCurrentCharacter(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return false;

            var vehicles = GetPlayerCollateralVehicles();
            if (vehicles == null || vehicles.Count == 0)
                return false;

            var candidates = vehicles.Where(v => IsOwnedByCurrentPlayer(v, ownerHash)).ToList();
            if (candidates.Count == 0)
                return false;

            object chosen = candidates[_rng.Next(candidates.Count)];
            RemovePersistentVehicleEntry(chosen);
            RefreshCollateralStateFileForCurrentCharacter();
            return true;
        }
        catch (Exception ex)
        {
            Log("TrySeizeOneOwnedVehicleForCurrentCharacter failed: " + ex);
            return false;
        }
    }

    private void GetCurrentInGameClock(out int hour, out int minute)
    {
        hour = 0;
        minute = 0;

        try
        {
            hour = Function.Call<int>(Hash.GET_CLOCK_HOURS);
            minute = Function.Call<int>(Hash.GET_CLOCK_MINUTES);
        }
        catch
        {
            hour = 0;
            minute = 0;
        }
    }

    private long CollectDueAmount(long due)
    {
        long collected = 0;
        try
        {
            if (due <= 0)
                return 0;

            long money = Math.Max(0, Game.Player.Money);
            long cashTaken = Math.Min(money, due);
            if (cashTaken > 0)
            {
                DeductPlayerMoney(cashTaken);
                collected += cashTaken;
            }

            long remaining = due - cashTaken;
            if (remaining <= 0)
                return collected;

            var vehicles = GetPlayerCollateralVehicles();
            if (vehicles.Count > 1)
                Shuffle(vehicles);

            bool seizedAny = false;
            foreach (var entry in vehicles)
            {
                if (remaining <= 0)
                    break;

                long estimatedValue = GetVehicleCollateralValue(entry);
                RemovePersistentVehicleEntry(entry);
                seizedAny = true;

                long covered = Math.Min(estimatedValue, remaining);
                remaining -= covered;
                collected += covered;
            }

            if (seizedAny)
                RefreshCollateralStateFileForCurrentCharacter();

            if (remaining > 0)
                Game.Player.WantedLevel = 5;

            return collected;
        }
        catch (Exception ex)
        {
            Log("CollectDueAmount failed: " + ex);
            return collected;
        }
    }

    private long GetCreditPoints()
    {
        try
        {
            long fromAccumulator = TryReadLongFromKnownSpendFile();
            if (fromAccumulator > 0)
                return fromAccumulator;

            return Math.Max(0, Game.Player.Money);
        }
        catch
        {
            return Math.Max(0, Game.Player.Money);
        }
    }

    private long GetLoanLimitFromPoints(long points)
    {
        if (points < 2_500_000L)
            return 0L; // Không đủ điều kiện vay

        if (points <= 3_000_000L)
            return 2_750_000L;

        if (points <= 4_000_000L)
            return 3_500_000L;

        if (points <= 5_500_000L)
            return 4_750_000L;

        if (points <= 6_300_000L)
            return 5_650_000L;

        if (points <= 7_100_000L)
            return 6_700_000L;

        if (points <= 8_000_000L)
            return 7_550_000L;

        if (points <= 10_000_000L)
            return 9_000_000L;

        if (points <= 15_200_000L)
            return 12_600_000L;

        if (points <= 20_800_000L)
            return 18_000_000L;

        if (points <= 25_300_000L)
            return 23_050_000L;

        if (points <= 30_300_000L)
            return 27_800_000L;

        if (points <= 40_300_000L)
            return 35_300_000L;

        if (points <= 50_200_000L)
            return 45_250_000L;

        if (points <= 60_800_000L)
            return 55_500_000L;

        if (points <= 70_300_000L)
            return 65_550_000L;

        if (points <= 80_600_000L)
            return 75_450_000L;

        if (points <= 90_600_000L)
            return 85_600_000L;

        if (points <= 100_900_000L)
            return 95_750_000L;

        if (points <= 110_700_000L)
            return 105_800_000L;

        if (points <= 120_800_000L)
            return 115_750_000L;

        if (points <= 130_800_000L)
            return 125_800_000L;

        if (points <= 140_300_000L)
            return 135_550_000L;

        if (points <= 150_100_000L)
            return 145_200_000L;

        if (points <= 160_400_000L)
            return 155_250_000L;

        if (points <= 170_800_000L)
            return 165_600_000L;

        if (points <= 180_800_000L)
            return 175_800_000L;

        if (points <= 190_200_000L)
            return 185_500_000L;

        if (points <= 200_700_000L)
            return 195_450_000L;

        if (points <= 210_000_000L)
            return 205_350_000L;

        if (points <= 220_500_000L)
            return 215_250_000L;

        if (points <= 230_100_000L)
            return 225_300_000L;

        if (points <= 240_500_000L)
            return 235_300_000L;

        if (points <= 250_700_000L)
            return 245_600_000L;

        return 260_000_000L; // Từ 250.7M trở lên
    }

    private long TryReadLongFromKnownSpendFile()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GTA V Mods", "PersistentManager", "DirectOrder_spend.dat");

            if (!File.Exists(path))
                return 0;

            string text = File.ReadAllText(path).Trim();
            string digits = new string(text.Where(ch => char.IsDigit(ch)).ToArray());

            if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                return value;
        }
        catch { }

        return 0;
    }

    private List<object> GetPlayerCollateralVehicles()
    {
        var result = new List<object>();
        try
        {
            Type pmType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("PersistentManager", false, true))
                .FirstOrDefault(t => t != null);

            if (pmType == null)
                return result;

            FieldInfo vehiclesField = pmType.GetField("_persistVehicles", BindingFlags.NonPublic | BindingFlags.Static);
            if (vehiclesField == null)
                return result;

            var list = vehiclesField.GetValue(null) as System.Collections.IEnumerable;
            if (list == null)
                return result;

            int ownerHash = Game.Player.Character?.Model.Hash ?? 0;

            foreach (object entry in list)
            {
                if (entry == null) continue;
                if (IsOwnedByCurrentPlayer(entry, ownerHash))
                    result.Add(entry);
            }
        }
        catch (Exception ex)
        {
            Log("GetPlayerCollateralVehicles failed: " + ex);
        }

        return result;
    }

    private bool IsCollateralLocked(object entry)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField("IsCollateralLocked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return false;
            object v = f.GetValue(entry);
            if (v is bool b) return b;
        }
        catch { }
        return false;
    }

    private void SetCollateralLocked(object entry, bool locked)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField("IsCollateralLocked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return;
            f.SetValue(entry, locked);
        }
        catch { }
    }

    private uint GetEntryModelHash(object entry)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField("ModelHash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return 0u;
            object v = f.GetValue(entry);
            if (v is uint u) return u;
        }
        catch { }
        return 0u;
    }

    private string GetEntryDisplayName(object entry)
    {
        uint modelHash = GetEntryModelHash(entry);
        if (modelHash == 0u)
            return "0x0";

        return PersistentManager.GetVehicleDisplayNamePublic(modelHash);
    }

    private void MarkPersistentVehiclesDirtyAndSave()
    {
        try
        {
            Type pmType = typeof(PersistentManager);

            FieldInfo dirtyField = pmType.GetField("_vehiclesDirty", BindingFlags.NonPublic | BindingFlags.Static);
            if (dirtyField != null)
                dirtyField.SetValue(null, true);

            MethodInfo saveMethod = pmType.GetMethod("SaveVehiclesFileInternal", BindingFlags.NonPublic | BindingFlags.Static);
            if (saveMethod != null)
                saveMethod.Invoke(null, null);
        }
        catch (Exception ex)
        {
            Log("MarkPersistentVehiclesDirtyAndSave failed: " + ex);
        }
    }

    private int LockRandomCollateralVehiclesForCurrentCharacter(int ownerHash, int maxCount = 3)
    {
        try
        {
            var owned = GetPlayerCollateralVehicles()
                .Where(e => !IsCollateralLocked(e))
                .ToList();

            if (owned.Count == 0)
                return 0;

            Shuffle(owned);

            int locked = 0;
            for (int i = 0; i < owned.Count && locked < maxCount; i++)
            {
                SetCollateralLocked(owned[i], true);
                locked++;
            }

            if (locked > 0)
            {
                MarkPersistentVehiclesDirtyAndSave();
                RefreshCollateralStateFileForCurrentCharacter();
            }

            return locked;
        }
        catch (Exception ex)
        {
            Log("LockRandomCollateralVehiclesForCurrentCharacter failed: " + ex);
            return 0;
        }
    }

    private int GetCurrentCollateralCount()
    {
        try
        {
            SyncCollateralStateFromFileForCurrentCharacter();

            int ownerHash = Game.Player.Character?.Model.Hash ?? 0;
            if (ownerHash == 0) return 0;

            return GetPlayerCollateralVehicles()
                .Count(e => IsCollateralLocked(e));
        }
        catch
        {
            return 0;
        }
    }

    private decimal GetCurrentDailyRate()
    {
        if (!_loanCollateralMode)
            return NO_COLLATERAL_DAILY_RATE;

        int alive = GetCurrentCollateralCount();

        if (alive >= 3) return COLLATERAL_RATE_3;
        if (alive == 2) return COLLATERAL_RATE_2;
        if (alive == 1) return COLLATERAL_RATE_1;
        return COLLATERAL_RATE_0;
    }

    private long GetCurrentDailyDueAmount()
    {
        decimal rate = GetCurrentDailyRate();
        return Math.Max(1L, (long)Math.Ceiling((decimal)_loanPrincipal * rate));
    }

    private void UnlockAllCollateralVehiclesForCurrentCharacter()
    {
        try
        {
            foreach (var entry in GetPlayerCollateralVehicles())
            {
                if (IsCollateralLocked(entry))
                    SetCollateralLocked(entry, false);
            }

            MarkPersistentVehiclesDirtyAndSave();
            RefreshCollateralStateFileForCurrentCharacter();
        }
        catch (Exception ex)
        {
            Log("UnlockAllCollateralVehiclesForCurrentCharacter failed: " + ex);
        }
    }

    private bool IsOwnedByCurrentPlayer(object entry, int ownerHash)
    {
        try
        {
            var t = entry.GetType();
            FieldInfo ownerField = t.GetField("OwnerModelHash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (ownerField == null)
                return false;

            object value = ownerField.GetValue(entry);
            if (value is int intValue)
                return intValue == ownerHash;
        }
        catch { }

        return false;
    }

    private long GetVehicleCollateralValue(object entry)
    {
        try
        {
            var t = entry.GetType();
            FieldInfo priceField = t.GetField("PurchasePrice", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (priceField != null)
            {
                object value = priceField.GetValue(entry);
                if (value is int intValue && intValue > 0)
                    return intValue;
            }

            FieldInfo modelField = t.GetField("ModelHash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (modelField != null)
            {
                object modelValue = modelField.GetValue(entry);
                if (modelValue is uint modelHash)
                    return 100_000L + (modelHash % 500_000u);
            }
        }
        catch { }

        return 100_000L;
    }

    private void RemovePersistentVehicleEntry(object entry)
    {
        try
        {
            if (entry == null)
                return;

            var t = entry.GetType();

            try
            {
                FieldInfo runtimeField = t.GetField("RuntimeVehicle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (runtimeField != null)
                {
                    object runtime = runtimeField.GetValue(entry);
                    if (runtime is Vehicle veh)
                    {
                        try
                        {
                            if (veh.Exists())
                                veh.Delete();
                        }
                        catch { }
                    }
                    runtimeField.SetValue(entry, null);
                }
            }
            catch { }

            try
            {
                FieldInfo blipField = t.GetField("MapBlip", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (blipField != null)
                {
                    object blipObj = blipField.GetValue(entry);
                    if (blipObj is Blip b)
                    {
                        try
                        {
                            if (b.Exists())
                                b.Delete();
                        }
                        catch { }
                    }
                    blipField.SetValue(entry, null);
                }
            }
            catch { }

            try
            {
                Type pmType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("PersistentManager", false, true))
                    .FirstOrDefault(x => x != null);

                if (pmType == null)
                    return;

                FieldInfo vehiclesField = pmType.GetField("_persistVehicles", BindingFlags.NonPublic | BindingFlags.Static);
                if (vehiclesField == null)
                    return;

                object listObj = vehiclesField.GetValue(null);
                if (listObj is System.Collections.IList list)
                    list.Remove(entry);

                FieldInfo dirtyField = pmType.GetField("_vehiclesDirty", BindingFlags.NonPublic | BindingFlags.Static);
                if (dirtyField != null)
                    dirtyField.SetValue(null, true);

                MethodInfo saveMethod = pmType.GetMethod("SaveVehiclesFileInternal", BindingFlags.NonPublic | BindingFlags.Static);
                if (saveMethod != null)
                    saveMethod.Invoke(null, null);
            }
            catch { }
        }
        catch (Exception ex)
        {
            Log("RemovePersistentVehicleEntry failed: " + ex);
        }
    }

    private void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            T temp = list[i];
            list[i] = list[j];
            list[j] = temp;
        }
    }

    private void LoadStateForOwner(int ownerHash)
    {
        try
        {
            Directory.CreateDirectory(_stateRoot);

            string file = GetStateFileForOwner(ownerHash);
            if (!File.Exists(file))
            {
                _stateLoaded = true;
                RefreshLoanMenuQuickPayText();
                return;
            }

            string[] parts = File.ReadAllText(file).Split('|');
            int savedVersion = 1;

            if (parts.Length >= 13)
            {
                _loanPrincipal = ParseLong(parts[0]);
                _loanRemainingDebt = ParseLong(parts[1]);
                _loanDailyDue = ParseLong(parts[2]);
                _loanActive = parts[3].Trim() == "1" && _loanRemainingDebt > 0;
                _loanLastChargedDayStamp = (int)ParseLong(parts[4]);
                _dueWindowStartHour = (int)ParseLong(parts[5]);
                _dueWindowEndHour = (int)ParseLong(parts[6]);
                _loanCollateralMode = parts[7].Trim() == "1";
                _loanStartDayStamp = (int)ParseLong(parts[8]);
                _quickPayFeeRateFirst7Days = ParseDecimal(parts[9]);
                _quickPayFeeRateNext10Days = ParseDecimal(parts[10]);
                _quickPayFeeRateAfter17Days = ParseDecimal(parts[11]);
                savedVersion = (int)ParseLong(parts[12]);
            }
            else if (parts.Length >= 12)
            {
                _loanPrincipal = ParseLong(parts[0]);
                _loanRemainingDebt = ParseLong(parts[1]);
                _loanDailyDue = ParseLong(parts[2]);
                _loanActive = parts[3].Trim() == "1" && _loanRemainingDebt > 0;
                _loanLastChargedDayStamp = (int)ParseLong(parts[4]);
                _dueWindowStartHour = (int)ParseLong(parts[5]);
                _dueWindowEndHour = (int)ParseLong(parts[6]);
                _loanCollateralMode = parts[7].Trim() == "1";
                _loanStartDayStamp = (int)ParseLong(parts[8]);
                _quickPayFeeRateFirst7Days = ParseDecimal(parts[9]);
                _quickPayFeeRateNext10Days = ParseDecimal(parts[10]);
                _quickPayFeeRateAfter17Days = ParseDecimal(parts[11]);
                savedVersion = 1;
            }
            else if (parts.Length >= 9)
            {
                _loanPrincipal = ParseLong(parts[0]);
                _loanRemainingDebt = ParseLong(parts[1]);
                _loanDailyDue = ParseLong(parts[2]);
                _loanActive = parts[3].Trim() == "1" && _loanRemainingDebt > 0;
                _loanLastChargedDayStamp = (int)ParseLong(parts[4]);
                _dueWindowStartHour = (int)ParseLong(parts[5]);
                _dueWindowEndHour = (int)ParseLong(parts[6]);
                _loanCollateralMode = parts[7].Trim() == "1";
                _loanStartDayStamp = (int)ParseLong(parts[8]);
                savedVersion = 1;
            }
            else if (parts.Length >= 8)
            {
                _loanPrincipal = ParseLong(parts[0]);
                _loanRemainingDebt = ParseLong(parts[1]);
                _loanDailyDue = ParseLong(parts[2]);
                _loanActive = parts[3].Trim() == "1" && _loanRemainingDebt > 0;
                _loanLastChargedDayStamp = (int)ParseLong(parts[4]);
                _dueWindowStartHour = (int)ParseLong(parts[5]);
                _dueWindowEndHour = (int)ParseLong(parts[6]);
                _loanCollateralMode = parts[7].Trim() == "1";
                _loanStartDayStamp = _loanLastChargedDayStamp > 0 ? _loanLastChargedDayStamp : GetInGameDayStamp();
                savedVersion = 1;
            }
            else
            {
                _loanCollateralMode = false;
                _loanStartDayStamp = -1;
                savedVersion = 1;
            }

            // Kiểm tra phiên bản lưu trữ để quyết định có roll lại phí hay không
            if (savedVersion < QUICK_PAY_FEE_RATE_STATE_VERSION)
            {
                // Save cũ: ép roll lại rate theo bản mới
                ResetQuickPayFeeRates();
            }

            EnsureQuickPayFeeRatesInitialized();

            _nextDueCheckAtGameTime = 0;
            _stateLoaded = true;
            _stateOwnerHash = ownerHash;

            RefreshLoanMenuQuickPayText();
        }
        catch (Exception ex)
        {
            Log("LoadStateForOwner failed: " + ex);
            _stateLoaded = true;
            RefreshLoanMenuQuickPayText();
        }
    }

    private void SaveStateForOwner(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return;

            Directory.CreateDirectory(_stateRoot);
            EnsureQuickPayFeeRatesInitialized();

            string file = GetStateFileForOwner(ownerHash);

            string payload = string.Join("|", new[]
            {
                _loanPrincipal.ToString(CultureInfo.InvariantCulture),
                _loanRemainingDebt.ToString(CultureInfo.InvariantCulture),
                _loanDailyDue.ToString(CultureInfo.InvariantCulture),
                _loanActive ? "1" : "0",
                _loanLastChargedDayStamp.ToString(CultureInfo.InvariantCulture),
                _dueWindowStartHour.ToString(CultureInfo.InvariantCulture),
                _dueWindowEndHour.ToString(CultureInfo.InvariantCulture),
                _loanCollateralMode ? "1" : "0",
                _loanStartDayStamp.ToString(CultureInfo.InvariantCulture),
                _quickPayFeeRateFirst7Days.ToString(CultureInfo.InvariantCulture),
                _quickPayFeeRateNext10Days.ToString(CultureInfo.InvariantCulture),
                _quickPayFeeRateAfter17Days.ToString(CultureInfo.InvariantCulture),
                QUICK_PAY_FEE_RATE_STATE_VERSION.ToString(CultureInfo.InvariantCulture)
            });

            File.WriteAllText(file, payload);
        }
        catch (Exception ex)
        {
            Log("SaveStateForOwner failed: " + ex);
        }
    }

    private long ParseLong(string value)
    {
        try
        {
            if (long.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
                return n;
        }
        catch { }
        return 0;
    }

    private decimal ParseDecimal(string value)
    {
        try
        {
            if (decimal.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
                return d;
        }
        catch
        {
            // Xử lý lỗi nếu cần thiết
        }
        return -1m;
    }

    private void PlayPurchaseSound()
    {
        try
        {
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "PURCHASE", "HUD_FRONTEND_TATTOO_SHOP_SOUNDSET", true);
        }
        catch { }
    }

    private void ShowFleecaNotification(string subtitle, string message, bool playSound = false)
    {
        try
        {
            if (playSound)
                PlayPurchaseSound();

            Notification.Show(
                FleecaNotifIcon,
                FleecaNotifTitle,
                subtitle,
                message);
        }
        catch
        {
            // fallback nếu API notification lỗi
            if (playSound)
                PlayPurchaseSound();

            Notification.Show(message);
        }
    }

    private void DeductPlayerMoney(long amount)
    {
        if (amount <= 0)
            return;

        Game.Player.Money -= (int)amount;
        PlayPurchaseSound();
    }

    private void Log(string message)
    {
        try
        {
            // Nếu cần debug thì mở dòng dưới:
            // File.AppendAllText("LombankLoanScript.log", DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch { }
    }
}