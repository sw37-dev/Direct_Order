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
using System.Text;
using System.Windows.Forms;

public class Clifford : Script
{
    private const int FRANKLIN_HASH = -1692214353;
    private const int MICHAEL_HASH = 225514697;
    private const int TREVOR_HASH = -1686040670;

    private static string T(string key, string fallback = "")
    {
        return Language.Get(key, fallback);
    }

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PersistentManager");

    private static readonly string PersistentVehiclesFile = Path.Combine(DataFolder, "persistent_vehicles.txt");
    private static readonly string SpendingFile = Path.Combine(DataFolder, "DirectOrder_spend.dat");

    private static readonly string FleecaStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Fleeca Bank Loan");

    private static readonly string LombankStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Lom Bank");

    private static readonly string HackerStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Hacker");

    private readonly ObjectPool _uiPool = new ObjectPool();

    private NativeMenu _cliffordMainMenu;
    private bool _cliffordMainMenuInitialized = false;

    private NativeMenu _cliffordCurrencyMenu;
    private bool _cliffordCurrencyMenuInitialized = false;

    private NativeMenu _cliffordCurrencyRedeemMenu;
    private bool _cliffordCurrencyRedeemMenuInitialized = false;

    private NativeMenu _cliffordVehiclesMenu;
    private bool _cliffordVehiclesMenuInitialized = false;

    private NativeMenu _cliffordLombankMenu;
    private bool _cliffordLombankMenuInitialized = false;

    private NativeMenu _cliffordFleecaMenu;
    private bool _cliffordFleecaMenuInitialized = false;

    private NativeMenu _vehiclesOwnedMenu;
    private NativeMenu _vehiclesOwnedValueMenu;
    private NativeMenu _collateralMenu;
    private NativeMenu _collateralValueMenu;

    private bool _vehiclesOwnedMenuInitialized = false;
    private bool _vehiclesOwnedValueMenuInitialized = false;
    private bool _collateralMenuInitialized = false;
    private bool _collateralValueMenuInitialized = false;

    private readonly Random _cliffordRng = new Random();

    private const int CLIFFORD_FORECAST_DELAY_MS = 3000;
    private bool _cliffordForecastPending = false;
    private int _cliffordForecastDueGameTime = -1;
    private string _cliffordForecastMessage = string.Empty;
    private int _cliffordForecastPendingOwnerHash = 0;

    private int _cliffordForecastStateOwnerHash = 0;
    private int _cliffordForecastDayKey = -1;
    private int _cliffordForecastPercent = -1;
    private bool _cliffordForecastSuccess = false;
    private int _cliffordBlackoutObservedDayKey = -1;

    private static readonly string DirectOrderIniPath = Path.Combine("scripts", "DirectOrder.ini");

    private CustomiFruit _cliffordPhoneInstance = null;
    private bool _cliffordContactAdded = false;

    private const int CLIFFORD_CALL_DURATION_MS = 2000;
    private const int MENU_PROCESS_INTERVAL_WHEN_VISIBLE = 0;
    private const int MENU_IDLE_INTERVAL_MS = 1000;

    private const int FLEECA_DUE_WARNING_CHECK_INTERVAL_MS = 30000;
    private const int FLEECA_DUE_WARNING_LEAD_MINUTES = 60;

    private int _fleecaDueWarningNextCheckAtGameTime = 0;
    private int _fleecaDueWarningStateOwnerHash = 0;
    private int _fleecaDueWarningLastNotifiedCycleDayKey = -1;

    private const string ClifffordMessageSoundName = "Text_Arrive_Tone";
    private const string ClifffordMessageSoundSet = "Phone_SoundSet_Default";

    private readonly string _cliffordContactName = T("Clifford_ContactName", "Clifford");

    private sealed class VehicleSnapshot
    {
        public uint ModelHash;
        public string DisplayName = "";
        public int OwnerHash;
        public string Plate = "";
        public long PurchasePrice;
        public Vector3 Position;
        public float Heading;
    }

    private sealed class CollateralStateEntry
    {
        public uint ModelHash;
        public string Plate = "";
        public Vector3 Position;
        public float Heading;
    }

    private sealed class SummarySnapshot
    {
        public int OwnerHash;
        public string CustomerName = T("Clifford_CustomerNameNA", "N/A");
        public long CurrentCash;
        public int VehicleCount;
        public long VehicleValueTotal;
        public long RewardPoints;
        public long IllegalMoney;

        public long FleecaDebt;
        public string FleecaDueWindowText = T("Clifford_TimeWindowNA", "--:--");
        public long FleecaDailyDue = 0L;
        public string FleecaBankStatusText = T("Clifford_StatusActive", "Đang hoạt động");

        public int CollateralVehicleCount;
        public long CollateralVehicleValue;

        public long LombankDebt;
        public long LombankTotalLimit;
        public string LombankStatusText = T("Clifford_StatusActive", "Đang hoạt động");
        public string LombankUnlockTimeText = T("Clifford_NA", "N/A");
        public string LombankRemainingText = T("Clifford_NA", "N/A");
        public string CurrentGameDateText = T("Clifford_CurrentGameDateNA", "N/A");
        public string FleecaUnlockTimeText = T("Clifford_NA", "N/A");
        public string FleecaRemainingText = T("Clifford_NA", "N/A");
    }

    private SummarySnapshot _snapshot = new SummarySnapshot();

    public Clifford()
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

            EnsureCliffordContactRegistered();
            SyncCliffordDailyState();

            ProcessPendingCliffordForecastNotification();
            ProcessFleecaDueWarningNotification();

            if (IsAnyCliffordMenuVisible())
            {
                Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
                _uiPool.Process();
                return;
            }

            Interval = MENU_IDLE_INTERVAL_MS;
        }
        catch
        {
            Interval = MENU_IDLE_INTERVAL_MS;
        }
    }

    private void SyncCliffordDailyState()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            if (_cliffordForecastStateOwnerHash != ownerHash)
            {
                _cliffordForecastStateOwnerHash = ownerHash;
                _cliffordForecastPending = false;
                _cliffordForecastDueGameTime = -1;
                _cliffordForecastMessage = string.Empty;
                _cliffordForecastPendingOwnerHash = 0;
                LoadCliffordForecastStateForCurrentCharacter();
            }

            if (_fleecaDueWarningStateOwnerHash != ownerHash)
            {
                _fleecaDueWarningStateOwnerHash = ownerHash;
                _fleecaDueWarningNextCheckAtGameTime = 0;
                _fleecaDueWarningLastNotifiedCycleDayKey = -1;
                LoadFleecaDueWarningStateForCurrentCharacter();
            }

            ObserveBlackoutForToday();
        }
        catch { }
    }

    private static string GetFleecaDueWarningStateFile(int ownerHash)
    {
        return Path.Combine(DataFolder, $"clifford_fleeca_due_warning_{ownerHash}.dat");
    }

    private void LoadFleecaDueWarningStateForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            _fleecaDueWarningLastNotifiedCycleDayKey = -1;

            string file = GetFleecaDueWarningStateFile(ownerHash);
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

                if (key == "lastnotifiedcycledaykey")
                {
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                        _fleecaDueWarningLastNotifiedCycleDayKey = parsed;
                }
            }
        }
        catch { }
    }

    private void SaveFleecaDueWarningStateForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            Directory.CreateDirectory(DataFolder);

            string file = GetFleecaDueWarningStateFile(ownerHash);
            var sb = new StringBuilder();
            sb.AppendLine("version=1");
            sb.AppendLine("lastNotifiedCycleDayKey=" + _fleecaDueWarningLastNotifiedCycleDayKey.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    private bool TryReadFleecaDueWindowForCurrentCharacter(out int dueStartHour, out int dueEndHour, out long remainingDebt)
    {
        dueStartHour = -1;
        dueEndHour = -1;
        remainingDebt = 0L;

        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return false;

            string file = Path.Combine(FleecaStateRoot, $"loan_state_{ownerHash}.dat");
            if (!File.Exists(file))
                return false;

            string text = File.ReadAllText(file, Encoding.UTF8).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string[] p = text.Split('|');
            if (p.Length < 7)
                return false;

            long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out remainingDebt);
            if (remainingDebt <= 0)
                return false;

            int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out dueStartHour);
            int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out dueEndHour);

            if (dueStartHour < 0 || dueStartHour > 23 || dueEndHour < 0 || dueEndHour > 23)
                return false;

            return true;
        }
        catch
        {
            dueStartHour = -1;
            dueEndHour = -1;
            remainingDebt = 0L;
            return false;
        }
    }

    private static int GetGameDayKeyFromDateTime(DateTime dt)
    {
        return (dt.Year * 10000) + (dt.Month * 100) + dt.Day;
    }

    private bool TryGetFleecaDueWarningTarget(out int cycleDayKey, out int dueStartHour)
    {
        cycleDayKey = -1;
        dueStartHour = -1;

        try
        {
            if (!TryReadFleecaDueWindowForCurrentCharacter(out int startHour, out int endHour, out long debt))
                return false;

            DateTime now = GetCurrentInGameDateTime();

            DateTime dueToday = new DateTime(now.Year, now.Month, now.Day, startHour, 0, 0, DateTimeKind.Unspecified);
            DateTime warningStartToday = dueToday.AddMinutes(-FLEECA_DUE_WARNING_LEAD_MINUTES);

            // Case 1: đang nằm trong 1 giờ trước khung thu nợ của "hôm nay"
            if (now >= warningStartToday && now < dueToday)
            {
                cycleDayKey = GetGameDayKeyFromDateTime(dueToday);
                dueStartHour = startHour;
                return true;
            }

            // Case 2: nếu đã qua giờ thu nợ hôm nay, kiểm tra cảnh báo cho "lần thu nợ kế tiếp"
            DateTime dueNext = dueToday.AddDays(1);
            DateTime warningStartNext = dueNext.AddMinutes(-FLEECA_DUE_WARNING_LEAD_MINUTES);

            if (now >= warningStartNext && now < dueNext)
            {
                cycleDayKey = GetGameDayKeyFromDateTime(dueNext);
                dueStartHour = startHour;
                return true;
            }

            return false;
        }
        catch
        {
            cycleDayKey = -1;
            dueStartHour = -1;
            return false;
        }
    }

    private void ProcessFleecaDueWarningNotification()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            if (_fleecaDueWarningStateOwnerHash != ownerHash)
                return;

            if (_fleecaDueWarningNextCheckAtGameTime > 0 && Game.GameTime < _fleecaDueWarningNextCheckAtGameTime)
                return;

            _fleecaDueWarningNextCheckAtGameTime = Game.GameTime + FLEECA_DUE_WARNING_CHECK_INTERVAL_MS;

            if (!TryGetFleecaDueWarningTarget(out int cycleDayKey, out int dueStartHour))
                return;

            if (cycleDayKey == -1)
                return;

            if (_fleecaDueWarningLastNotifiedCycleDayKey == cycleDayKey)
                return;

            ShowCliffordNotification(
                T("Clifford_FleecaDueWarningTitle", "Clifford"),
                T("Clifford_FleecaDueWarningMessage", "Sắp tới khung giờ thời gian thu nợ của Fleeca Bank rồi đó nha!"));

            _fleecaDueWarningLastNotifiedCycleDayKey = cycleDayKey;
            SaveFleecaDueWarningStateForCurrentCharacter();
        }
        catch { }
    }

    private static string GetCliffordForecastStateFile(int ownerHash)
    {
        return Path.Combine(DataFolder, $"clifford_forecast_state_{ownerHash}.dat");
    }

    private void LoadCliffordForecastStateForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            _cliffordForecastDayKey = -1;
            _cliffordForecastPercent = -1;
            _cliffordForecastSuccess = false;
            _cliffordBlackoutObservedDayKey = -1;

            string file = GetCliffordForecastStateFile(ownerHash);
            if (!File.Exists(file))
                return;

            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                int idx = raw.IndexOf('=');
                if (idx <= 0) continue;
                string key = raw.Substring(0, idx).Trim().ToLowerInvariant();
                string val = raw.Substring(idx + 1).Trim();
                int intValue;
                switch (key)
                {
                    case "forecastdaykey":
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue)) _cliffordForecastDayKey = intValue;
                        break;
                    case "forecastpercent":
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue)) _cliffordForecastPercent = Math.Max(0, Math.Min(100, intValue));
                        break;
                    case "forecastsuccess":
                        _cliffordForecastSuccess = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "blackoutobserveddaykey":
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue)) _cliffordBlackoutObservedDayKey = intValue;
                        break;
                }
            }
        }
        catch { }
    }

    private void SaveCliffordForecastStateForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0) return;
            Directory.CreateDirectory(DataFolder);
            string file = GetCliffordForecastStateFile(ownerHash);
            var sb = new StringBuilder();
            sb.AppendLine("version=2");
            sb.AppendLine("forecastDayKey=" + _cliffordForecastDayKey.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("forecastPercent=" + _cliffordForecastPercent.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("forecastSuccess=" + (_cliffordForecastSuccess ? "1" : "0"));
            sb.AppendLine("blackoutObservedDayKey=" + _cliffordBlackoutObservedDayKey.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    private void ObserveBlackoutForToday()
    {
        try
        {
            int dayKey = GetCurrentGameDayKey();
            if (dayKey == -1) return;
            var state = CityBlackoutHackerState.GetWorldPriceStateForCurrentCharacter();
            if (state == CityBlackoutHackerState.WorldPriceState.Normal) return;
            if (_cliffordBlackoutObservedDayKey != dayKey)
            {
                _cliffordBlackoutObservedDayKey = dayKey;
                SaveCliffordForecastStateForCurrentCharacter();
            }
        }
        catch { }
    }

    private bool HasForecastForToday(int dayKey) => dayKey != -1 && _cliffordForecastDayKey == dayKey && _cliffordForecastPercent >= 0;
    private bool HasBlackoutObservedToday(int dayKey) => dayKey != -1 && _cliffordBlackoutObservedDayKey == dayKey;

    private static int GetCurrentGameDayKey()
    {
        try
        {
            int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);
            int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
            int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);
            if (month < 1 || month > 12) month += 1;
            if (year < 1) year = 1;
            if (month < 1) month = 1;
            if (month > 12) month = 12;
            int maxDay = DateTime.DaysInMonth(year, month);
            if (day < 1) day = 1;
            if (day > maxDay) day = maxDay;
            return (year * 10000) + (month * 100) + day;
        }
        catch { return -1; }
    }

    private bool IsAnyCliffordMenuVisible()
    {
        return (_cliffordMainMenu != null && _cliffordMainMenu.Visible)
            || (_cliffordCurrencyMenu != null && _cliffordCurrencyMenu.Visible)
            || (_cliffordCurrencyRedeemMenu != null && _cliffordCurrencyRedeemMenu.Visible)
            || (_cliffordVehiclesMenu != null && _cliffordVehiclesMenu.Visible)
            || (_cliffordLombankMenu != null && _cliffordLombankMenu.Visible)
            || (_cliffordFleecaMenu != null && _cliffordFleecaMenu.Visible)
            || (_vehiclesOwnedMenu != null && _vehiclesOwnedMenu.Visible)
            || (_vehiclesOwnedValueMenu != null && _vehiclesOwnedValueMenu.Visible)
            || (_collateralMenu != null && _collateralMenu.Visible)
            || (_collateralValueMenu != null && _collateralValueMenu.Visible);
    }

    private string GetCurrentCharacterName()
    {
        try
        {
            int h = GetCurrentCharacterHash();
            if (h == FRANKLIN_HASH) return T("Clifford_Character_Franklin", "Franklin Clinton");
            if (h == MICHAEL_HASH) return T("Clifford_Character_Michael", "Michael De Santa");
            if (h == TREVOR_HASH) return T("Clifford_Character_Trevor", "Trevor Philips");
        }
        catch { }
        return T("Clifford_CustomerNameNA", "N/A");
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (!IsAnyCliffordMenuVisible()) return;
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                CloseCurrentVisibleMenu();
        }
        catch { }
    }

    private void EnsureCliffordContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null) return;
            if (!ReferenceEquals(_cliffordPhoneInstance, phone))
            {
                _cliffordPhoneInstance = phone;
                _cliffordContactAdded = false;
            }
            if (_cliffordContactAdded) return;
            if (phone.Contacts.Any(c => c != null && string.Equals(c.Name, _cliffordContactName, StringComparison.OrdinalIgnoreCase)))
            {
                _cliffordContactAdded = true;
                return;
            }
            var contact = new iFruitContact(_cliffordContactName)
            {
                Active = true,
                DialTimeout = CLIFFORD_CALL_DURATION_MS,
                Bold = false,
                Icon = ContactIcon.DIA_CLIFFORD
            };
            contact.Answered += OnCliffordContactAnswered;
            phone.Contacts.Add(contact);
            _cliffordContactAdded = true;
        }
        catch { }
    }

    private void OnCliffordContactAnswered(iFruitContact sender)
    {
        try { OpenCliffordMainMenu(); }
        finally { try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { } }
    }

    private void EnsureCliffordMainMenuCreated()
    {
        try
        {
            if (_cliffordMainMenuInitialized) return;
            _cliffordMainMenu = new NativeMenu(
                T("Clifford_AnalyticsTitle", "Clifford Analytics"),
                T("Clifford_AnalyticsSubtitle", "CÁC THÔNG TIN CẦN THIẾT"));
            _uiPool.Add(_cliffordMainMenu);
            ConfigureKeyboardOnlyMenu(_cliffordMainMenu);
            _cliffordMainMenu.Visible = false;
            _cliffordMainMenuInitialized = true;
        }
        catch { }
    }

    private void EnsureCurrencyMenuCreated()
    {
        try
        {
            if (_cliffordCurrencyMenuInitialized) return;
            _cliffordCurrencyMenu = new NativeMenu(
                T("Clifford_CurrencyTitle", "Currency Data"),
                T("Clifford_CurrencySubtitle", "CHI TIẾT TIỀN TỆ"));
            _uiPool.Add(_cliffordCurrencyMenu);
            ConfigureKeyboardOnlyMenu(_cliffordCurrencyMenu);
            _cliffordCurrencyMenu.Visible = false;
            _cliffordCurrencyMenuInitialized = true;
        }
        catch { }
    }

    private void EnsureCurrencyRedeemMenuCreated()
    {
        try
        {
            if (_cliffordCurrencyRedeemMenuInitialized) return;
            _cliffordCurrencyRedeemMenu = new NativeMenu(
                T("Clifford_RedeemTitle", "Redeem Rewards"),
                T("Clifford_RedeemSubtitle", "CHI TIẾT QUY ĐỔI"));
            _uiPool.Add(_cliffordCurrencyRedeemMenu);
            ConfigureKeyboardOnlyMenu(_cliffordCurrencyRedeemMenu);
            _cliffordCurrencyRedeemMenu.Visible = false;
            _cliffordCurrencyRedeemMenuInitialized = true;
        }
        catch { }
    }

    private void EnsureVehiclesTopMenuCreated()
    {
        try
        {
            if (_cliffordVehiclesMenuInitialized) return;
            _cliffordVehiclesMenu = new NativeMenu(
                T("Clifford_VehiclesMenuTitle", "Vehicles"),
                T("Clifford_VehiclesMenuSubtitle", "CHI TIẾT CÁC PHƯƠNG TIỆN"));
            _uiPool.Add(_cliffordVehiclesMenu);
            ConfigureKeyboardOnlyMenu(_cliffordVehiclesMenu);
            _cliffordVehiclesMenu.Visible = false;
            _cliffordVehiclesMenuInitialized = true;
        }
        catch { }
    }

    private void EnsureLombankMenuCreated()
    {
        try
        {
            if (_cliffordLombankMenuInitialized) return;
            _cliffordLombankMenu = new NativeMenu(
                T("Clifford_LombankTitle", "Lom Bank"),
                T("Clifford_LombankSubtitle", "CHI TIẾT NGÂN HÀNG LOM"));
            _uiPool.Add(_cliffordLombankMenu);
            ConfigureKeyboardOnlyMenu(_cliffordLombankMenu);
            _cliffordLombankMenu.Visible = false;
            _cliffordLombankMenuInitialized = true;
        }
        catch { }
    }

    private void EnsureFleecaMenuCreated()
    {
        try
        {
            if (_cliffordFleecaMenuInitialized) return;
            _cliffordFleecaMenu = new NativeMenu(
                T("Clifford_FleecaTitle", "Fleeca Bank"),
                T("Clifford_FleecaSubtitle", "CHI TIẾT NGÂN HÀNG FLEECA"));
            _uiPool.Add(_cliffordFleecaMenu);
            ConfigureKeyboardOnlyMenu(_cliffordFleecaMenu);
            _cliffordFleecaMenu.Visible = false;
            _cliffordFleecaMenuInitialized = true;
        }
        catch { }
    }

    private void OpenCliffordMainMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureCliffordMainMenuCreated();
            BuildCliffordMainMenu();
            HideAllMenus();
            _cliffordMainMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch { }
    }

    private void OpenCurrencyMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureCurrencyMenuCreated();
            BuildCurrencyMenu();
            HideAllMenus();
            _cliffordCurrencyMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch { }
    }

    private void OpenCurrencyRedeemMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureCurrencyRedeemMenuCreated();
            BuildCurrencyRedeemMenu();
            HideAllMenus();
            _cliffordCurrencyRedeemMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch { }
    }

    private void OpenVehiclesMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureVehiclesTopMenuCreated();
            BuildVehiclesTopMenu();
            HideAllMenus();
            _cliffordVehiclesMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch { }
    }

    private void OpenLombankMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureLombankMenuCreated();
            BuildLombankMenu();
            HideAllMenus();
            _cliffordLombankMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch { }
    }

    private void OpenFleecaMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureFleecaMenuCreated();
            BuildFleecaMenu();
            HideAllMenus();
            _cliffordFleecaMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch { }
    }

    private void OpenVehiclesOwnedMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureVehiclesOwnedMenuCreated();
            BuildVehiclesOwnedMenu();
            HideAllMenus();
            _vehiclesOwnedMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch { }
    }

    private void OpenVehiclesOwnedValueMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureVehiclesOwnedValueMenuCreated();
            BuildVehiclesOwnedValueMenu();
            HideAllMenus();
            _vehiclesOwnedValueMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch { }
    }

    private void OpenCollateralMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureCollateralMenuCreated();
            BuildCollateralMenu();
            HideAllMenus();
            _collateralMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch { }
    }

    private void OpenCollateralValueMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureCollateralValueMenuCreated();
            BuildCollateralValueMenu();
            HideAllMenus();
            _collateralValueMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch { }
    }

    private void BuildCliffordMainMenu()
    {
        try
        {
            if (_cliffordMainMenu == null) return;
            _cliffordMainMenu.Clear();
            AddInfoItem(_cliffordMainMenu, T("Clifford_Main_NameLabel", "1. Tên nhân vật"), _snapshot.CustomerName);
            AddInfoItem(_cliffordMainMenu, T("Clifford_Main_DateLabel", "2. Ngày hiện tại"), _snapshot.CurrentGameDateText);
            AddActionItem(_cliffordMainMenu, T("Clifford_Main_CurrencyLabel", "3. Số liệu tiền tệ"), OpenCurrencyMenu);
            AddActionItem(_cliffordMainMenu, T("Clifford_Main_VehiclesLabel", "4. Phương tiện"), OpenVehiclesMenu);
            AddActionItem(_cliffordMainMenu, T("Clifford_Main_LombankLabel", "5. Ngân hàng Lom"), OpenLombankMenu);
            AddActionItem(_cliffordMainMenu, T("Clifford_Main_FleecaLabel", "6. Ngân hàng Fleeca"), OpenFleecaMenu);
            AddActionItem(_cliffordMainMenu, T("Clifford_Main_ForecastLabel", "7. Dự báo cúp điện"), ScheduleCliffordForecastNotification);
            var close = new NativeItem(T("Clifford_CloseSummary", "Đóng báo cáo tổng thể"));
            close.Activated += (s, e) => CloseCliffordMenu();
            _cliffordMainMenu.Add(close);
        }
        catch { }
    }

    private void BuildCurrencyMenu()
    {
        try
        {
            if (_cliffordCurrencyMenu == null) return;

            _cliffordCurrencyMenu.Clear();

            AddInfoItem(_cliffordCurrencyMenu, T("Clifford_Currency_CurrentCash", "1. Số tiền hiện tại"), FormatMoney(_snapshot.CurrentCash));
            AddInfoItem(_cliffordCurrencyMenu, T("Clifford_Currency_RewardPoints", "2. Điểm thưởng"), FormatMoney(_snapshot.RewardPoints));

            var illegalMoneyItem = new NativeItem(
                string.Format(CultureInfo.InvariantCulture,
                    T("Clifford_Currency_IllegalMoney", "3. Tiền bất hợp pháp: {0}"),
                    FormatMoney(_snapshot.IllegalMoney))
            );
            illegalMoneyItem.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
            illegalMoneyItem.Activated += (s, e) => OpenCurrencyRedeemMenu();
            _cliffordCurrencyMenu.Add(illegalMoneyItem);

            var back = new NativeItem(T("Clifford_BackToMain", "Quay lại menu tổng thể"));
            back.Activated += (s, e) => ReturnToMainMenu();
            _cliffordCurrencyMenu.Add(back);
        }
        catch { }
    }

    private void BuildCurrencyRedeemMenu()
    {
        try
        {
            if (_cliffordCurrencyRedeemMenu == null) return;
            _cliffordCurrencyRedeemMenu.Clear();
            AddInfoItem(_cliffordCurrencyRedeemMenu, T("Clifford_Redeem_Maze", "1. Quy đổi Maze Bank (max)"), FormatMoney(GetMazeMaxConvertAmount(_snapshot.IllegalMoney)));
            AddInfoItem(_cliffordCurrencyRedeemMenu, T("Clifford_Redeem_Smuggler", "2. Quy đổi Smuggler (max)"), FormatMoney(GetSmuggleMaxConvertAmount(_snapshot.IllegalMoney)));
            var back = new NativeItem(T("Clifford_BackPrevious", "Quay lại menu trước"));
            back.Activated += (s, e) => ReturnToCurrencyMenu();
            _cliffordCurrencyRedeemMenu.Add(back);
        }
        catch { }
    }

    private void BuildVehiclesTopMenu()
    {
        try
        {
            if (_cliffordVehiclesMenu == null) return;
            _cliffordVehiclesMenu.Clear();
            AddActionItem(_cliffordVehiclesMenu,
                string.Format(CultureInfo.InvariantCulture, T("Clifford_Vehicles_OwnedCount", "1. Số phương tiện sở hữu: {0}"), FormatInt(_snapshot.VehicleCount)),
                OpenVehiclesOwnedMenu);
            AddActionItem(_cliffordVehiclesMenu,
                string.Format(CultureInfo.InvariantCulture, T("Clifford_Vehicles_CollateralCount", "2. Số phương tiện thế chấp: {0}"), FormatInt(_snapshot.CollateralVehicleCount)),
                OpenCollateralMenu);
            AddActionItem(_cliffordVehiclesMenu,
                string.Format(CultureInfo.InvariantCulture, T("Clifford_Vehicles_OwnedValue", "3. Tổng giá trị phương tiện: {0}"), FormatMoney(_snapshot.VehicleValueTotal)),
                OpenVehiclesOwnedValueMenu);
            AddActionItem(_cliffordVehiclesMenu,
                string.Format(CultureInfo.InvariantCulture, T("Clifford_Vehicles_CollateralValue", "4. Tổng giá trị xe thế chấp: {0}"), FormatMoney(_snapshot.CollateralVehicleValue)),
                OpenCollateralValueMenu);
            var back = new NativeItem(T("Clifford_BackToMain", "Quay lại menu tổng thể"));
            back.Activated += (s, e) => ReturnToMainMenu();
            _cliffordVehiclesMenu.Add(back);
        }
        catch { }
    }

    private void BuildLombankMenu()
    {
        try
        {
            if (_cliffordLombankMenu == null) return;
            _cliffordLombankMenu.Clear();
            AddInfoItem(_cliffordLombankMenu, T("Clifford_BankNameLabel", "1. Tên ngân hàng"), T("Clifford_LombankBankName", "Lom Bank"));
            AddInfoItem(_cliffordLombankMenu, T("Clifford_LombankStatusLabel", "2. Trạng thái Lom"), _snapshot.LombankStatusText);
            AddInfoItem(_cliffordLombankMenu, T("Clifford_LombankUnlockLabel", "3. Thời gian mở khóa"), _snapshot.LombankUnlockTimeText);
            AddInfoItem(_cliffordLombankMenu, T("Clifford_LombankRemainLabel", "4. Thời gian còn lại"), _snapshot.LombankRemainingText);
            AddInfoItem(_cliffordLombankMenu, T("Clifford_LombankLimitLabel", "5. Tổng hạn mức Lom"), FormatMoney(_snapshot.LombankTotalLimit));
            AddInfoItem(_cliffordLombankMenu, T("Clifford_LombankDebtLabel", "6. Dư nợ ngân hàng Lom"), FormatMoney(_snapshot.LombankDebt));
            var back = new NativeItem(T("Clifford_BackToMain", "Quay lại menu tổng thể"));
            back.Activated += (s, e) => ReturnToMainMenu();
            _cliffordLombankMenu.Add(back);
        }
        catch { }
    }

    private void BuildFleecaMenu()
    {
        try
        {
            if (_cliffordFleecaMenu == null) return;
            _cliffordFleecaMenu.Clear();
            AddInfoItem(_cliffordFleecaMenu, T("Clifford_BankNameLabel", "1. Tên ngân hàng"), T("Clifford_FleecaBankName", "Fleeca Bank"));
            AddInfoItem(_cliffordFleecaMenu, T("Clifford_FleecaStatusLabel", "2. Trạng thái Fleeca"), _snapshot.FleecaBankStatusText);
            AddInfoItem(_cliffordFleecaMenu, T("Clifford_FleecaUnlockLabel", "3. Thời gian mở khóa"), _snapshot.FleecaUnlockTimeText);
            AddInfoItem(_cliffordFleecaMenu, T("Clifford_FleecaRemainLabel", "4. Thời gian còn lại"), _snapshot.FleecaRemainingText);
            AddInfoItem(_cliffordFleecaMenu, T("Clifford_FleecaDebtLabel", "5. Khoản nợ Fleeca"), FormatMoney(_snapshot.FleecaDebt));
            AddInfoItem(_cliffordFleecaMenu, T("Clifford_FleecaDueWindowLabel", "6. Thời gian thu nợ"), _snapshot.FleecaDueWindowText);
            AddInfoItem(_cliffordFleecaMenu, T("Clifford_FleecaDailyDueLabel", "7. Số tiền thu mỗi ngày"),
                _snapshot.FleecaDailyDue > 0 ? FormatMoney(_snapshot.FleecaDailyDue) : T("Clifford_NA", "N/A"));
            var back = new NativeItem(T("Clifford_BackToMain", "Quay lại menu tổng thể"));
            back.Activated += (s, e) => ReturnToMainMenu();
            _cliffordFleecaMenu.Add(back);
        }
        catch { }
    }

    private void AddInfoItem(NativeMenu menu, string label, string value)
    {
        if (menu == null) return;
        var item = new NativeItem($"{label}: {value}");
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

    private void HideAllMenus()
    {
        try
        {
            if (_cliffordMainMenu != null) _cliffordMainMenu.Visible = false;
            if (_cliffordCurrencyMenu != null) _cliffordCurrencyMenu.Visible = false;
            if (_cliffordCurrencyRedeemMenu != null) _cliffordCurrencyRedeemMenu.Visible = false;
            if (_cliffordVehiclesMenu != null) _cliffordVehiclesMenu.Visible = false;
            if (_cliffordLombankMenu != null) _cliffordLombankMenu.Visible = false;
            if (_cliffordFleecaMenu != null) _cliffordFleecaMenu.Visible = false;
            if (_vehiclesOwnedMenu != null) _vehiclesOwnedMenu.Visible = false;
            if (_vehiclesOwnedValueMenu != null) _vehiclesOwnedValueMenu.Visible = false;
            if (_collateralMenu != null) _collateralMenu.Visible = false;
            if (_collateralValueMenu != null) _collateralValueMenu.Visible = false;
        }
        catch { }
    }

    private void CloseCliffordMenu()
    {
        HideAllMenus();
        Interval = MENU_IDLE_INTERVAL_MS;
    }

    private void CloseCurrentVisibleMenu()
    {
        try
        {
            if (_vehiclesOwnedValueMenu != null && _vehiclesOwnedValueMenu.Visible) { ReturnToVehiclesMenu(); return; }
            if (_vehiclesOwnedMenu != null && _vehiclesOwnedMenu.Visible) { ReturnToVehiclesMenu(); return; }
            if (_collateralValueMenu != null && _collateralValueMenu.Visible) { ReturnToVehiclesMenu(); return; }
            if (_collateralMenu != null && _collateralMenu.Visible) { ReturnToVehiclesMenu(); return; }
            if (_cliffordCurrencyRedeemMenu != null && _cliffordCurrencyRedeemMenu.Visible) { ReturnToCurrencyMenu(); return; }
            if (_cliffordCurrencyMenu != null && _cliffordCurrencyMenu.Visible) { ReturnToMainMenu(); return; }
            if (_cliffordVehiclesMenu != null && _cliffordVehiclesMenu.Visible) { ReturnToMainMenu(); return; }
            if (_cliffordLombankMenu != null && _cliffordLombankMenu.Visible) { ReturnToMainMenu(); return; }
            if (_cliffordFleecaMenu != null && _cliffordFleecaMenu.Visible) { ReturnToMainMenu(); return; }
            if (_cliffordMainMenu != null && _cliffordMainMenu.Visible) { CloseCliffordMenu(); return; }
            CloseCliffordMenu();
        }
        catch { }
    }

    private void ReturnToMainMenu()
    {
        try
        {
            HideAllMenus();
            if (_cliffordMainMenu != null) _cliffordMainMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch { }
    }

    private void ReturnToCurrencyMenu()
    {
        try
        {
            HideAllMenus();
            if (_cliffordCurrencyMenu != null) _cliffordCurrencyMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch { }
    }

    private void ReturnToVehiclesMenu()
    {
        try
        {
            HideAllMenus();
            if (_cliffordVehiclesMenu != null) _cliffordVehiclesMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch { }
    }

    private void ScheduleCliffordForecastNotification()
    {
        try
        {
            SyncCliffordDailyState();
            int dayKey = GetCurrentGameDayKey();
            if (dayKey == -1) return;

            if (HasForecastForToday(dayKey))
            {
                ShowCliffordNotification(T("Clifford_ForecastTitle", "Dự báo cúp điện"), FormatRepeatedForecastMessage(_cliffordForecastPercent, _cliffordForecastSuccess));
                CloseCliffordMenu();
                return;
            }

            if (HasBlackoutObservedToday(dayKey))
            {
                ShowCliffordNotification(T("Clifford_ForecastTitle", "Dự báo cúp điện"), T("Clifford_ForecastAlreadyBlackoutToday", "Hôm nay đã có cúp điện rồi nên tui không dự báo nữa đâu."));
                CloseCliffordMenu();
                return;
            }

            double triggerChance = ReadTriggerChanceFromDirectOrderIni();
            int successPercent = GetCliffordForecastSuccessPercent(triggerChance);
            int roll = _cliffordRng.Next(0, 100);
            bool success = roll < successPercent;

            _cliffordForecastDayKey = dayKey;
            _cliffordForecastPercent = roll;
            _cliffordForecastSuccess = success;
            SaveCliffordForecastStateForCurrentCharacter();

            _cliffordForecastMessage = success
                ? string.Format(CultureInfo.InvariantCulture, T("Clifford_ForecastSuccessMessage", "Hôm nay có khả năng {0}% cúp điện thành công (tui đoán lụi)."), roll)
                : string.Format(CultureInfo.InvariantCulture, T("Clifford_ForecastFailMessage", "Hôm nay khả năng {0}% không có cúp điện (tui đoán lụi)."), roll);

            _cliffordForecastDueGameTime = Game.GameTime + CLIFFORD_FORECAST_DELAY_MS;
            _cliffordForecastPending = true;
            _cliffordForecastPendingOwnerHash = GetCurrentCharacterHash();

            CloseCliffordMenu();
        }
        catch { }
    }

    private static string FormatRepeatedForecastMessage(int percent, bool success)
    {
        percent = Math.Max(0, Math.Min(100, percent));
        return success
            ? string.Format(CultureInfo.InvariantCulture, T("Clifford_ForecastRepeatedSuccess", "Tui đã dự đoán {0}% khả năng cúp điện hôm nay rồi mà?"), percent)
            : string.Format(CultureInfo.InvariantCulture, T("Clifford_ForecastRepeatedFail", "Tui đã dự đoán {0}% không có cúp điện hôm nay rồi mà?"), percent);
    }

    private void ProcessPendingCliffordForecastNotification()
    {
        try
        {
            if (!_cliffordForecastPending) return;
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0 || _cliffordForecastPendingOwnerHash != ownerHash)
            {
                _cliffordForecastPending = false;
                _cliffordForecastDueGameTime = -1;
                _cliffordForecastMessage = string.Empty;
                _cliffordForecastPendingOwnerHash = 0;
                return;
            }
            if (Game.GameTime < _cliffordForecastDueGameTime) return;
            _cliffordForecastPending = false;
            _cliffordForecastDueGameTime = -1;
            _cliffordForecastPendingOwnerHash = 0;
            string message = _cliffordForecastMessage;
            _cliffordForecastMessage = string.Empty;
            ShowCliffordNotification(T("Clifford_ForecastTitle", "Dự báo cúp điện"), message);
        }
        catch { }
    }

    private double ReadTriggerChanceFromDirectOrderIni()
    {
        try
        {
            if (File.Exists(DirectOrderIniPath))
            {
                var settings = ScriptSettings.Load(DirectOrderIniPath);
                string raw = settings.GetValue<string>("CityBlackout", "TriggerChance", "0");
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    return Math.Max(0.0, Math.Min(1.0, value));
            }
        }
        catch { }

        try
        {
            if (!File.Exists(DirectOrderIniPath)) return 0.0;
            bool inSection = false;
            foreach (string rawLine in File.ReadAllLines(DirectOrderIniPath, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string sec = line.Substring(1, line.Length - 2).Trim();
                    inSection = sec.Equals("CityBlackout", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;
                string key = line.Substring(0, idx).Trim();
                string val = line.Substring(idx + 1).Trim();
                if (!key.Equals("TriggerChance", StringComparison.OrdinalIgnoreCase)) continue;
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    return Math.Max(0.0, Math.Min(1.0, parsed));
                break;
            }
        }
        catch { }
        return 0.0;
    }

    private static int GetCliffordForecastSuccessPercent(double triggerChance)
    {
        triggerChance = Math.Max(0.0, Math.Min(1.0, triggerChance));
        int percent = (int)Math.Ceiling(triggerChance * 10.0) * 10;
        if (percent < 10) percent = 10;
        if (percent > 90) percent = 90;
        return percent;
    }

    private void ShowCliffordNotification(string title, string message, int timeout = 7000)
    {
        PlayFrontendSound(ClifffordMessageSoundName, ClifffordMessageSoundSet);

        try
        {
            Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, message);
            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_MESSAGETEXT, "DIA_CLIFFORD", "DIA_CLIFFORD", false, 0, "Clifford", title);
            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_TICKER, false, true);
        }
        catch
        {
            try { Notification.Show(title + ": " + message); }
            catch { GTA.UI.Screen.ShowSubtitle(title + ": " + message, timeout); }
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

    private NativeCheckboxItem CreateReadOnlyCheckedItem(string title, string description)
    {
        bool suppress = false;
        var item = new NativeCheckboxItem(title, description, true);
        item.CheckboxChanged += (s, e) =>
        {
            if (suppress) return;
            try
            {
                if (!item.Checked)
                {
                    suppress = true;
                    item.Checked = true;
                    suppress = false;
                }
            }
            catch { suppress = false; }
        };
        return item;
    }

    private void EnsureVehiclesOwnedMenuCreated()
    {
        try
        {
            if (_vehiclesOwnedMenuInitialized) return;
            _vehiclesOwnedMenu = new NativeMenu(
                T("Clifford_VehiclesMenuTitle", "Vehicles"),
                T("Clifford_VehiclesMenuSubtitle", "CÁC PHƯƠNG TIỆN SỞ HỮU"));
            _uiPool.Add(_vehiclesOwnedMenu);
            ConfigureKeyboardOnlyMenu(_vehiclesOwnedMenu);
            _vehiclesOwnedMenu.Visible = false;
            _vehiclesOwnedMenuInitialized = true;
        }
        catch { }
    }

    private void EnsureVehiclesOwnedValueMenuCreated()
    {
        try
        {
            if (_vehiclesOwnedValueMenuInitialized) return;
            _vehiclesOwnedValueMenu = new NativeMenu(
                T("Clifford_VehiclesValueMenuTitle", "Vehicles Value"),
                T("Clifford_VehiclesValueMenuSubtitle", "CHI TIẾT GIÁ TRỊ PHƯƠNG TIỆN"));
            _uiPool.Add(_vehiclesOwnedValueMenu);
            ConfigureKeyboardOnlyMenu(_vehiclesOwnedValueMenu);
            _vehiclesOwnedValueMenu.Visible = false;
            _vehiclesOwnedValueMenuInitialized = true;
        }
        catch { }
    }

    private void EnsureCollateralMenuCreated()
    {
        try
        {
            if (_collateralMenuInitialized) return;
            _collateralMenu = new NativeMenu(
                T("Clifford_CollateralMenuTitle", "Collateral"),
                T("Clifford_CollateralMenuSubtitle", "CHI TIẾT PHƯƠNG TIỆN THẾ CHẤP"));
            _uiPool.Add(_collateralMenu);
            ConfigureKeyboardOnlyMenu(_collateralMenu);
            _collateralMenu.Visible = false;
            _collateralMenuInitialized = true;
        }
        catch { }
    }

    private void EnsureCollateralValueMenuCreated()
    {
        try
        {
            if (_collateralValueMenuInitialized) return;
            _collateralValueMenu = new NativeMenu(T("Clifford_CollateralValueMenuTitle", "Collateral Value"), T("Clifford_CollateralValueMenuSubtitle", "GIÁ TRỊ PHƯƠNG TIỆN THẾ CHẤP"));
            _uiPool.Add(_collateralValueMenu);
            ConfigureKeyboardOnlyMenu(_collateralValueMenu);
            _collateralValueMenu.Visible = false;
            _collateralValueMenuInitialized = true;
        }
        catch { }
    }

    private void BuildVehiclesOwnedMenu()
    {
        try
        {
            if (_vehiclesOwnedMenu == null) return;
            RefreshSummarySnapshot();
            _vehiclesOwnedMenu.Clear();
            int ownerHash = _snapshot.OwnerHash;
            var vehicles = LoadOwnedVehicles(ownerHash);
            if (vehicles.Count == 0)
            {
                _vehiclesOwnedMenu.Add(new NativeItem(T("Clifford_NoOwnedVehicles", "Không có phương tiện sở hữu.")));
            }
            else
            {
                int index = 1;
                foreach (var v in vehicles)
                {
                    string name = string.IsNullOrWhiteSpace(v.DisplayName) ? T("Clifford_NA", "N/A") : v.DisplayName;
                    _vehiclesOwnedMenu.Add(CreateReadOnlyCheckedItem(
                        string.Format(CultureInfo.InvariantCulture, "{0}. {1}", index, name),
                        T("Clifford_VehicleActive", "Xe đang hoạt động")));
                    index++;
                }
            }
            var back = new NativeItem(T("Clifford_BackPrevious", "Quay lại menu trước"));
            back.Activated += (s, e) => ReturnToVehiclesMenu();
            _vehiclesOwnedMenu.Add(back);
        }
        catch { }
    }

    private void BuildVehiclesOwnedValueMenu()
    {
        try
        {
            if (_vehiclesOwnedValueMenu == null) return;
            RefreshSummarySnapshot();
            _vehiclesOwnedValueMenu.Clear();
            int ownerHash = _snapshot.OwnerHash;
            var vehicles = LoadOwnedVehicles(ownerHash);
            if (vehicles.Count == 0)
            {
                _vehiclesOwnedValueMenu.Add(new NativeItem(T("Clifford_NoOwnedVehicles", "Không có phương tiện sở hữu.")));
            }
            else
            {
                int index = 1;
                foreach (var v in vehicles)
                {
                    string name = string.IsNullOrWhiteSpace(v.DisplayName) ? T("Clifford_NA", "N/A") : v.DisplayName;
                    _vehiclesOwnedValueMenu.Add(new NativeItem(
                        string.Format(CultureInfo.InvariantCulture,
                        T("Clifford_VehicleValueLine", "{0}. {1}: {2}"),
                        index, name, FormatMoney(v.PurchasePrice))));
                    index++;
                }
            }
            var back = new NativeItem(T("Clifford_BackPrevious", "Quay lại menu trước"));
            back.Activated += (s, e) => ReturnToVehiclesMenu();
            _vehiclesOwnedValueMenu.Add(back);
        }
        catch { }
    }

    private void BuildCollateralMenu()
    {
        try
        {
            if (_collateralMenu == null) return;
            RefreshSummarySnapshot();
            _collateralMenu.Clear();
            int ownerHash = _snapshot.OwnerHash;
            var ownedVehicles = LoadOwnedVehicles(ownerHash);
            var collateralStates = LoadCollateralStates(ownerHash);
            var matchedVehicles = new List<VehicleSnapshot>();
            foreach (var state in collateralStates)
            {
                var match = FindMatchingVehicleForCollateralState(state, ownedVehicles);
                if (match == null) continue;
                matchedVehicles.Add(match);
            }
            if (matchedVehicles.Count == 0)
            {
                _collateralMenu.Add(new NativeItem(T("Clifford_NoCollateralVehicles", "Không có phương tiện thế chấp.")));
            }
            else
            {
                int index = 1;
                foreach (var v in matchedVehicles)
                {
                    string name = string.IsNullOrWhiteSpace(v.DisplayName) ? T("Clifford_NA", "N/A") : v.DisplayName;
                    _collateralMenu.Add(CreateReadOnlyCheckedItem(
                        string.Format(CultureInfo.InvariantCulture, "{0}. {1}", index, name),
                        T("Clifford_VehicleCollateral", "Xe đang thế chấp")));
                    index++;
                }
            }
            var back = new NativeItem(T("Clifford_BackPrevious", "Quay lại menu trước"));
            back.Activated += (s, e) => ReturnToVehiclesMenu();
            _collateralMenu.Add(back);
        }
        catch { }
    }

    private void BuildCollateralValueMenu()
    {
        try
        {
            if (_collateralValueMenu == null) return;
            RefreshSummarySnapshot();
            _collateralValueMenu.Clear();
            int ownerHash = _snapshot.OwnerHash;
            var ownedVehicles = LoadOwnedVehicles(ownerHash);
            var collateralStates = LoadCollateralStates(ownerHash);
            var matchedLines = new List<Tuple<string, long>>();
            foreach (var state in collateralStates)
            {
                var match = FindMatchingVehicleForCollateralState(state, ownedVehicles);
                if (match == null) continue;
                string name = string.IsNullOrWhiteSpace(match.DisplayName) ? T("Clifford_NA", "N/A") : match.DisplayName;
                long price = Math.Max(0L, match.PurchasePrice);
                matchedLines.Add(Tuple.Create(name, price));
            }
            if (matchedLines.Count == 0)
            {
                _collateralValueMenu.Add(new NativeItem(T("Clifford_NoCollateralVehicles", "Không có phương tiện thế chấp.")));
            }
            else
            {
                for (int i = 0; i < matchedLines.Count; i++)
                {
                    _collateralValueMenu.Add(new NativeItem(
                        string.Format(CultureInfo.InvariantCulture,
                        T("Clifford_CollateralValueLine", "{0}. {1}: {2}"),
                        i + 1, matchedLines[i].Item1, FormatMoney(matchedLines[i].Item2))));
                }
            }
            var back = new NativeItem(T("Clifford_BackPrevious", "Quay lại menu trước"));
            back.Activated += (s, e) => ReturnToVehiclesMenu();
            _collateralValueMenu.Add(back);
        }
        catch { }
    }

    private static string FormatInt(int value) => string.Format(CultureInfo.InvariantCulture, "{0:N0}", Math.Max(0, value));
    private static string FormatMoney(long value) => string.Format(CultureInfo.InvariantCulture, "${0:N0}", Math.Max(0, value));
    private static long GetMazeMaxConvertAmount(long illegalMoney) => illegalMoney <= 0 ? 0L : (long)Math.Round(illegalMoney / 18.0, MidpointRounding.AwayFromZero);
    private static long GetSmuggleMaxConvertAmount(long illegalMoney) => illegalMoney <= 0 ? 0L : (long)Math.Round(illegalMoney / 82.0, MidpointRounding.AwayFromZero);

    private static string GetStatNameForCharacter(int characterHash)
    {
        if (characterHash == MICHAEL_HASH) return "SP0_TOTAL_CASH";
        if (characterHash == FRANKLIN_HASH) return "SP1_TOTAL_CASH";
        if (characterHash == TREVOR_HASH) return "SP2_TOTAL_CASH";
        return "SP1_TOTAL_CASH";
    }

    private static long GetStoryCashByCharacterHash(int characterHash)
    {
        try
        {
            string statName = GetStatNameForCharacter(characterHash);
            int statHash = Game.GenerateHash(statName);
            using (var outArg = new OutputArgument())
            {
                Function.Call(Hash.STAT_GET_INT, statHash, outArg, -1);
                int value = outArg.GetResult<int>();
                return Math.Max(0L, (long)value);
            }
        }
        catch { return 0L; }
    }

    private int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists()) return 0;
            int h = p.Model.Hash;
            if (h == FRANKLIN_HASH || h == MICHAEL_HASH || h == TREVOR_HASH) return h;
        }
        catch { }
        return 0;
    }

    private void RefreshSummarySnapshot()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            _snapshot = new SummarySnapshot
            {
                OwnerHash = ownerHash,
                CustomerName = GetCurrentCharacterName(),
                CurrentCash = GetStoryCashByCharacterHash(ownerHash),
                CurrentGameDateText = GetCurrentInGameDayText()
            };

            var vehicles = LoadOwnedVehicles(ownerHash);
            _snapshot.VehicleCount = vehicles.Count;
            _snapshot.VehicleValueTotal = vehicles.Sum(v => Math.Max(0L, v.PurchasePrice));

            ReloadSpendingAccumulatorFromDisk();
            _snapshot.RewardPoints = TryGetRewardPoints();
            _snapshot.IllegalMoney = CityBlackoutHackerState.GetDirtyMoneyBalanceForCurrentCharacter();

            _snapshot.FleecaDebt = ReadFleecaDebt(ownerHash);
            ReadFleecaLoanDetails(ownerHash, out string fleecaDueWindow, out long fleecaDailyDue);
            _snapshot.FleecaDueWindowText = fleecaDueWindow;
            _snapshot.FleecaDailyDue = fleecaDailyDue;

            var collateralStates = LoadCollateralStates(ownerHash);
            _snapshot.CollateralVehicleCount = collateralStates.Count;
            _snapshot.CollateralVehicleValue = 0L;
            foreach (var state in collateralStates)
            {
                var match = FindMatchingVehicleForCollateralState(state, vehicles);
                if (match != null)
                    _snapshot.CollateralVehicleValue += Math.Max(0L, match.PurchasePrice);
            }

            _snapshot.LombankDebt = ReadLombankDebt(ownerHash);
            _snapshot.LombankTotalLimit = ReadLombankTotalLimit(ownerHash);
            _snapshot.LombankStatusText = GetBankStatusTextForCurrentCharacter("atmLockedUntil");
            _snapshot.LombankUnlockTimeText = GetUnlockTimeTextForCurrentCharacter("atmLockedUntil");
            _snapshot.LombankRemainingText = GetRemainingLockTimeTextForCurrentCharacter("atmLockedUntil");
            _snapshot.FleecaBankStatusText = GetBankStatusTextForCurrentCharacter("fleecaLockedUntil");
            _snapshot.FleecaUnlockTimeText = GetUnlockTimeTextForCurrentCharacter("fleecaLockedUntil");
            _snapshot.FleecaRemainingText = GetRemainingLockTimeTextForCurrentCharacter("fleecaLockedUntil");
        }
        catch { _snapshot = new SummarySnapshot(); }
    }

    private bool TryGetLockTimeForCurrentCharacter(string lockFieldName, out DateTime? lockedUntil)
    {
        lockedUntil = null;
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0) return false;
            string file = Path.Combine(HackerStateRoot, $"hacker_state_{ownerHash}.dat");
            if (!File.Exists(file)) return false;
            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                int idx = raw.IndexOf('=');
                if (idx <= 0) continue;
                string key = raw.Substring(0, idx).Trim();
                string val = raw.Substring(idx + 1).Trim();
                if (!key.Equals(lockFieldName, StringComparison.OrdinalIgnoreCase)) continue;
                lockedUntil = ParseNullableDateTime(val);
                return true;
            }
        }
        catch { }
        return false;
    }

    private string GetUnlockTimeTextForCurrentCharacter(string lockFieldName)
    {
        try
        {
            DateTime? lockedUntil;
            if (!TryGetLockTimeForCurrentCharacter(lockFieldName, out lockedUntil) || !lockedUntil.HasValue)
                return T("Clifford_NA", "N/A");
            return lockedUntil.Value.ToString("HH:mm, yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch { return T("Clifford_NA", "N/A"); }
    }

    private string GetRemainingLockTimeTextForCurrentCharacter(string lockFieldName)
    {
        try
        {
            DateTime? lockedUntil;
            if (!TryGetLockTimeForCurrentCharacter(lockFieldName, out lockedUntil) || !lockedUntil.HasValue)
                return T("Clifford_NA", "N/A");
            DateTime now = GetCurrentInGameDateTime();
            TimeSpan remain = lockedUntil.Value - now;
            if (remain <= TimeSpan.Zero) return T("Clifford_NA", "N/A");
            int days = (int)remain.TotalDays;
            int hours = remain.Hours;
            if (days > 0)
                return string.Format(CultureInfo.InvariantCulture,
                    T("Clifford_RemainingDaysFmt", "{0} ngày {1} tiếng"), days, hours);
            return string.Format(CultureInfo.InvariantCulture,
                T("Clifford_RemainingHoursFmt", "{0} tiếng"), hours);
        }
        catch { return T("Clifford_NA", "N/A"); }
    }

    private static DateTime GetCurrentInGameDateTime()
    {
        try
        {
            int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);
            int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
            int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);
            int hour = Function.Call<int>(Hash.GET_CLOCK_HOURS);
            int minute = Function.Call<int>(Hash.GET_CLOCK_MINUTES);
            if (month < 1 || month > 12) month += 1;
            if (year < 1) year = 1;
            if (month < 1) month = 1;
            if (month > 12) month = 12;
            int maxDay = DateTime.DaysInMonth(year, month);
            if (day < 1) day = 1;
            if (day > maxDay) day = maxDay;
            if (hour < 0) hour = 0;
            if (hour > 23) hour = 23;
            if (minute < 0) minute = 0;
            if (minute > 59) minute = 59;
            return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        }
        catch { return new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified); }
    }

    private static string GetLocalizedInGameDayName(int dayOfWeek)
    {
        switch (dayOfWeek)
        {
            case 0: return T("Clifford_Day_Sunday", "Chủ nhật");
            case 1: return T("Clifford_Day_Monday", "Thứ 2");
            case 2: return T("Clifford_Day_Tuesday", "Thứ 3");
            case 3: return T("Clifford_Day_Wednesday", "Thứ 4");
            case 4: return T("Clifford_Day_Thursday", "Thứ 5");
            case 5: return T("Clifford_Day_Friday", "Thứ 6");
            case 6: return T("Clifford_Day_Saturday", "Thứ 7");
            default: return T("Clifford_NA", "N/A");
        }
    }

    private static string GetCurrentInGameDayText()
    {
        try
        {
            DateTime currentDate = GetCurrentInGameDateTime();
            int dayOfWeek = 0;
            try { dayOfWeek = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_WEEK); }
            catch { dayOfWeek = (int)currentDate.DayOfWeek; }
            if (dayOfWeek < 0 || dayOfWeek > 6) dayOfWeek = (int)currentDate.DayOfWeek;
            string dayName = GetLocalizedInGameDayName(dayOfWeek);
            return string.Format(CultureInfo.InvariantCulture,
                T("Clifford_CurrentGameDateFmt", "{0}, {1:yyyy-MM-dd}"),
                dayName, currentDate);
        }
        catch { return T("Clifford_CurrentGameDateNA", "N/A"); }
    }

    private static DateTime? ParseNullableDateTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed)) return parsed;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed)) return parsed;
        return null;
    }

    private string GetBankStatusTextForCurrentCharacter(string lockFieldName)
    {
        try
        {
            DateTime? lockedUntil;
            if (!TryGetLockTimeForCurrentCharacter(lockFieldName, out lockedUntil) || !lockedUntil.HasValue)
                return T("Clifford_StatusActive", "Đang hoạt động");
            if (lockedUntil.HasValue && GetCurrentInGameDateTime() < lockedUntil.Value)
                return T("Clifford_StatusFrozen", "Đóng băng tài khoản");
            return T("Clifford_StatusActive", "Đang hoạt động");
        }
        catch { return T("Clifford_StatusActive", "Đang hoạt động"); }
    }

    private List<VehicleSnapshot> LoadOwnedVehicles(int ownerHash)
    {
        var result = new List<VehicleSnapshot>();
        try
        {
            if (ownerHash == 0 || !File.Exists(PersistentVehiclesFile)) return result;
            foreach (string raw in File.ReadAllLines(PersistentVehiclesFile, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (TryParseVehicleLine(raw, out VehicleSnapshot vehicle))
                    if (vehicle != null && (ownerHash == 0 || vehicle.OwnerHash == ownerHash)) result.Add(vehicle);
            }
        }
        catch { }
        return result;
    }

    private bool TryParseVehicleLine(string line, out VehicleSnapshot vehicle)
    {
        vehicle = null;
        try
        {
            string[] p = line.Split('|');
            if (p.Length < 11) return false;
            uint modelHash = ParseModelHash(p[0]);
            if (modelHash == 0) return false;
            float x = ParseFloat(p, 1);
            float y = ParseFloat(p, 2);
            float z = ParseFloat(p, 3);
            float heading = ParseFloat(p, 4);
            string plate = p.Length > 5 ? (p[5] ?? "").Trim() : "";
            int ownerHash = 0;
            if (p.Length > 6) int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out ownerHash);
            long purchasePrice = 0;
            if (p.Length > 10) long.TryParse(p[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out purchasePrice);
            vehicle = new VehicleSnapshot
            {
                ModelHash = modelHash,
                DisplayName = ExtractVehicleDisplayName(p[0]),
                OwnerHash = ownerHash,
                Plate = plate,
                PurchasePrice = Math.Max(0, purchasePrice),
                Position = new Vector3(x, y, z),
                Heading = heading
            };
            return true;
        }
        catch { vehicle = null; return false; }
    }

    private static string ExtractVehicleDisplayName(string raw)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(raw)) return T("Clifford_NA", "N/A");
            raw = raw.Trim();
            int idx = raw.IndexOf(" (0x", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return raw.Substring(0, idx).Trim();
            idx = raw.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return raw.Substring(0, idx).Trim().TrimEnd('(').Trim();
            return raw;
        }
        catch { return raw ?? T("Clifford_NA", "N/A"); }
    }

    private static float ParseFloat(string[] parts, int index)
    {
        if (parts == null || index < 0 || index >= parts.Length) return 0f;
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
                    if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed)) return parsed;
                }
            }
            int firstHex = -1;
            for (int i = 0; i < raw.Length; i++) if (IsHexDigit(raw[i])) { firstHex = i; break; }
            if (firstHex >= 0)
            {
                int len = 0;
                while (firstHex + len < raw.Length && IsHexDigit(raw[firstHex + len])) len++;
                if (len > 0)
                {
                    string hex = raw.Substring(firstHex, len);
                    if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed)) return parsed;
                }
            }
            if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dec)) return dec;
        }
        catch { }
        return 0u;
    }

    private static bool IsHexDigit(char c)
    {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }

    private VehicleSnapshot FindMatchingVehicleForCollateralState(CollateralStateEntry state, List<VehicleSnapshot> ownedVehicles)
    {
        try
        {
            if (state == null || ownedVehicles == null || ownedVehicles.Count == 0) return null;
            string statePlate = NormalizePlate(state.Plate);
            foreach (var vehicle in ownedVehicles)
            {
                if (vehicle == null || vehicle.ModelHash != state.ModelHash) continue;
                string vehiclePlate = NormalizePlate(vehicle.Plate);
                if (!string.IsNullOrEmpty(statePlate) && !string.IsNullOrEmpty(vehiclePlate) && statePlate == vehiclePlate) return vehicle;
                if (vehicle.Position.DistanceTo2D(state.Position) < 5f) return vehicle;
            }
        }
        catch { }
        return null;
    }

    private List<CollateralStateEntry> LoadCollateralStates(int ownerHash)
    {
        var result = new List<CollateralStateEntry>();
        try
        {
            if (ownerHash == 0) return result;
            string file = Path.Combine(FleecaStateRoot, $"collateral_state_{ownerHash}.dat");
            if (!File.Exists(file)) return result;
            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string[] p = raw.Split('|');
                if (p.Length < 6) continue;
                uint modelHash = ParseModelHash(p[0]);
                if (modelHash == 0) continue;
                string plate = (p[1] ?? "").Trim();
                float x = ParseFloat(p, 2);
                float y = ParseFloat(p, 3);
                float z = ParseFloat(p, 4);
                float heading = ParseFloat(p, 5);
                result.Add(new CollateralStateEntry { ModelHash = modelHash, Plate = plate, Position = new Vector3(x, y, z), Heading = heading });
            }
        }
        catch { }
        return result;
    }

    private static string NormalizePlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return "";
        return new string(plate.Trim().Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
    }

    private long TryGetRewardPoints()
    {
        try
        {
            long points = 0;
            string text = File.Exists(SpendingFile) ? File.ReadAllText(SpendingFile).Trim() : "";
            if (string.IsNullOrWhiteSpace(text)) return 0L;
            string digits = new string(text.Where(char.IsDigit).ToArray());
            if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)) points = parsed;
            return Math.Max(0L, points);
        }
        catch { return 0L; }
    }

    private void ReloadSpendingAccumulatorFromDisk()
    {
        try
        {
            if (!File.Exists(SpendingFile)) return;
        }
        catch { }
    }

    private long ReadFleecaDebt(int ownerHash)
    {
        try
        {
            if (ownerHash == 0) return 0L;
            string file = Path.Combine(FleecaStateRoot, $"loan_state_{ownerHash}.dat");
            if (!File.Exists(file)) return 0L;
            string text = File.ReadAllText(file, Encoding.UTF8).Trim();
            if (string.IsNullOrWhiteSpace(text)) return 0L;
            string[] p = text.Split('|');
            if (p.Length < 2) return 0L;
            long debt = 0L;
            if (long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out debt)) return Math.Max(0L, debt);
            return 0L;
        }
        catch { return 0L; }
    }

    private long ReadLombankDebt(int ownerHash)
    {
        try
        {
            if (ownerHash == 0) return 0L;
            string file = Path.Combine(LombankStateRoot, $"lombank_state_{ownerHash}.dat");
            if (!File.Exists(file)) return 0L;
            foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;
                string key = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim();
                if (!key.Equals("debt", StringComparison.OrdinalIgnoreCase)) continue;
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long debt)) return Math.Max(0L, debt);
            }
        }
        catch { }
        return 0L;
    }

    private void ReadFleecaLoanDetails(int ownerHash, out string dueWindowText, out long dailyDue)
    {
        dueWindowText = T("Clifford_TimeWindowNA", "--:--");
        dailyDue = 0L;
        try
        {
            if (ownerHash == 0) return;
            string file = Path.Combine(FleecaStateRoot, $"loan_state_{ownerHash}.dat");
            if (!File.Exists(file)) return;
            string text = File.ReadAllText(file, Encoding.UTF8).Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            string[] p = text.Split('|');
            if (p.Length < 7) return;
            long remainingDebt = 0L;
            if (p.Length > 1) long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out remainingDebt);
            if (remainingDebt <= 0) return;
            if (p.Length > 2)
            {
                long tmp;
                if (long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out tmp)) dailyDue = Math.Max(0L, tmp);
            }
            int startHour = -1;
            int endHour = -1;
            if (p.Length > 5) int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out startHour);
            if (p.Length > 6) int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out endHour);
            if (startHour >= 0 && startHour < 24 && endHour >= 0 && endHour < 24)
                dueWindowText = string.Format(CultureInfo.InvariantCulture, "{0:00}:00 - {1:00}:00", startHour, endHour);
        }
        catch
        {
            dueWindowText = T("Clifford_TimeWindowNA", "--:--");
            dailyDue = 0L;
        }
    }

    private long ReadLombankTotalLimit(int ownerHash)
    {
        try
        {
            if (ownerHash == 0) return 0L;
            string file = Path.Combine(LombankStateRoot, $"lombank_state_{ownerHash}.dat");
            if (!File.Exists(file)) return 0L;
            foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;
                string key = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim();
                if (!key.Equals("limit", StringComparison.OrdinalIgnoreCase)) continue;
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long limit)) return Math.Max(0L, limit);
            }
        }
        catch { }
        return 0L;
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
        catch { }
    }
}