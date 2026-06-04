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

    private readonly ObjectPool _uiPool = new ObjectPool();

    private NativeMenu _cliffordMenu;
    private bool _cliffordMenuInitialized = false;

    private NativeMenu _cliffordMainMenu;
    private bool _cliffordMainMenuInitialized = false;

    private readonly Random _cliffordRng = new Random();

    private const int CLIFFORD_FORECAST_DELAY_MS = 3000;
    private bool _cliffordForecastPending = false;
    private int _cliffordForecastDueGameTime = -1;
    private string _cliffordForecastMessage = string.Empty;
    private int _cliffordForecastPendingOwnerHash = 0;

    // --- Dự báo theo ngày ---
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

    private readonly string _cliffordContactName = T("Clifford_ContactName", "Clifford");

    private readonly List<NativeItem> _staticItems = new List<NativeItem>();
    private NativeMenu _vehiclesOwnedMenu;
    private NativeMenu _vehiclesOwnedValueMenu;
    private NativeMenu _collateralMenu;
    private NativeMenu _collateralValueMenu;

    private bool _vehiclesOwnedMenuInitialized = false;
    private bool _vehiclesOwnedValueMenuInitialized = false;
    private bool _collateralMenuInitialized = false;
    private bool _collateralValueMenuInitialized = false;

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
        public string CustomerName; // NEW
        public long CurrentCash;
        public int VehicleCount;
        public long VehicleValueTotal;
        public long RewardPoints;
        public long IllegalMoney;
        public long FleecaDebt;
        public int CollateralVehicleCount;
        public long CollateralVehicleValue;
        public long LombankDebt;
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

            if (IsAnyCliffordMenuVisible())
            {
                if (!_cliffordMenuInitialized)
                    BuildCliffordMenu();

                ProcessVisibleMenuState();
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

            ObserveBlackoutForToday();
        }
        catch
        {
        }
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
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                int idx = raw.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = raw.Substring(0, idx).Trim().ToLowerInvariant();
                string val = raw.Substring(idx + 1).Trim();

                int intValue;

                switch (key)
                {
                    case "forecastdaykey":
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
                            _cliffordForecastDayKey = intValue;
                        break;

                    case "forecastpercent":
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
                            _cliffordForecastPercent = Math.Max(0, Math.Min(100, intValue));
                        break;

                    case "forecastsuccess":
                        _cliffordForecastSuccess = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;

                    case "blackoutobserveddaykey":
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
                            _cliffordBlackoutObservedDayKey = intValue;
                        break;
                }
            }
        }
        catch
        {
        }
    }

    private void SaveCliffordForecastStateForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

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
        catch
        {
        }
    }

    private void ObserveBlackoutForToday()
    {
        try
        {
            int dayKey = GetCurrentGameDayKey();
            if (dayKey == -1)
                return;

            var state = CityBlackoutHackerState.GetWorldPriceStateForCurrentCharacter();
            if (state == CityBlackoutHackerState.WorldPriceState.Normal)
                return;

            if (_cliffordBlackoutObservedDayKey != dayKey)
            {
                _cliffordBlackoutObservedDayKey = dayKey;
                SaveCliffordForecastStateForCurrentCharacter();
            }
        }
        catch
        {
        }
    }

    private bool HasForecastForToday(int dayKey)
    {
        return dayKey != -1 && _cliffordForecastDayKey == dayKey && _cliffordForecastPercent >= 0;
    }

    private bool HasBlackoutObservedToday(int dayKey)
    {
        return dayKey != -1 && _cliffordBlackoutObservedDayKey == dayKey;
    }

    private static int GetCurrentGameDayKey()
    {
        try
        {
            int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);
            int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
            int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);

            if (month < 1 || month > 12)
                month += 1;

            if (year < 1) year = 1;
            if (month < 1) month = 1;
            if (month > 12) month = 12;

            int maxDay = DateTime.DaysInMonth(year, month);
            if (day < 1) day = 1;
            if (day > maxDay) day = maxDay;

            return (year * 10000) + (month * 100) + day;
        }
        catch
        {
            return -1;
        }
    }

    private bool IsAnyCliffordMenuVisible()
    {
        return (_cliffordMainMenu != null && _cliffordMainMenu.Visible)
            || (_cliffordMenu != null && _cliffordMenu.Visible)
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

            if (h == FRANKLIN_HASH) return "Franklin Clinton";
            if (h == MICHAEL_HASH) return "Michael De Santa";
            if (h == TREVOR_HASH) return "Trevor Philips";
        }
        catch { }

        return "N/A";
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (IsAnyCliffordMenuVisible())
            {
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                {
                    CloseCliffordMenu();
                    return;
                }
            }
        }
        catch { }
    }

    private void EnsureCliffordContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_cliffordPhoneInstance, phone))
            {
                _cliffordPhoneInstance = phone;
                _cliffordContactAdded = false;
            }

            if (_cliffordContactAdded)
                return;

            if (phone.Contacts.Any(c =>
                c != null &&
                string.Equals(c.Name, _cliffordContactName, StringComparison.OrdinalIgnoreCase)))
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
        try
        {
            OpenCliffordMainMenu();
        }
        finally
        {
            try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
        }
    }

    private void EnsureCliffordMainMenuCreated()
    {
        try
        {
            if (_cliffordMainMenuInitialized)
                return;

            _cliffordMainMenu = new NativeMenu("Clifford", "CÁC THÔNG TIN CẦN XEM");
            _uiPool.Add(_cliffordMainMenu);
            ConfigureKeyboardOnlyMenu(_cliffordMainMenu);
            _cliffordMainMenu.Visible = false;
            _cliffordMainMenuInitialized = true;
        }
        catch
        {
        }
    }

    private void BuildCliffordMainMenu()
    {
        try
        {
            if (_cliffordMainMenu == null)
                return;

            _cliffordMainMenu.Clear();

            var financial = new NativeItem("1. Thống kê tài chính");
            financial.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
            financial.Activated += (s, e) =>
            {
                OpenCliffordReportMenu();
            };
            _cliffordMainMenu.Add(financial);

            var forecast = new NativeItem("2. Dự báo cúp điện");
            forecast.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
            forecast.Activated += (s, e) =>
            {
                ScheduleCliffordForecastNotification();
            };
            _cliffordMainMenu.Add(forecast);

            var close = new NativeItem("Hủy bỏ dịch vụ");
            close.Activated += (s, e) => CloseCliffordMenu();
            _cliffordMainMenu.Add(close);
        }
        catch
        {
        }
    }

    private void OpenCliffordMainMenu()
    {
        try
        {
            EnsureCliffordMainMenuCreated();
            BuildCliffordMainMenu();

            CloseAllCliffordSubMenus();

            if (_cliffordMenu != null)
                _cliffordMenu.Visible = false;

            if (_cliffordMainMenu != null)
                _cliffordMainMenu.Visible = true;

            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch
        {
        }
    }

    private void OpenCliffordReportMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureCliffordMenuCreated();
            BuildCliffordMenu();

            CloseAllCliffordSubMenus();

            if (_cliffordMainMenu != null)
                _cliffordMainMenu.Visible = false;

            _cliffordMenu.Visible = true;
            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch
        {
        }
    }

    private void CloseCliffordMenu()
    {
        try
        {
            CloseAllCliffordSubMenus();

            if (_cliffordMainMenu != null)
                _cliffordMainMenu.Visible = false;

            if (_cliffordMenu != null)
                _cliffordMenu.Visible = false;
        }
        catch
        {
        }

        Interval = MENU_IDLE_INTERVAL_MS;
    }

    private void CloseAllCliffordSubMenus()
    {
        try
        {
            if (_vehiclesOwnedMenu != null) _vehiclesOwnedMenu.Visible = false;
            if (_vehiclesOwnedValueMenu != null) _vehiclesOwnedValueMenu.Visible = false;
            if (_collateralMenu != null) _collateralMenu.Visible = false;
            if (_collateralValueMenu != null) _collateralValueMenu.Visible = false;
        }
        catch
        {
        }
    }

    private void ShowOnlyCliffordMenu(NativeMenu menu)
    {
        try
        {
            CloseAllCliffordSubMenus();

            if (_cliffordMainMenu != null)
                _cliffordMainMenu.Visible = false;

            if (_cliffordMenu != null)
                _cliffordMenu.Visible = false;

            if (menu != null)
                menu.Visible = true;

            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
            _uiPool.Process();
        }
        catch
        {
        }
    }

    private void ReturnToCliffordMenu()
    {
        try
        {
            CloseAllCliffordSubMenus();

            if (_cliffordMainMenu != null)
                _cliffordMainMenu.Visible = false;

            if (_cliffordMenu != null)
                _cliffordMenu.Visible = true;

            Interval = MENU_PROCESS_INTERVAL_WHEN_VISIBLE;
        }
        catch
        {
        }
    }

    private void ScheduleCliffordForecastNotification()
    {
        try
        {
            SyncCliffordDailyState();

            int dayKey = GetCurrentGameDayKey();
            if (dayKey == -1)
                return;

            // 1) Nếu hôm nay đã dự báo rồi thì dùng lại đúng tỷ lệ cũ
            if (HasForecastForToday(dayKey))
            {
                ShowCliffordNotification(
                    "Dự báo cúp điện",
                    FormatRepeatedForecastMessage(_cliffordForecastPercent, _cliffordForecastSuccess));
                CloseCliffordMenu();
                return;
            }

            // 2) Nếu hôm nay đã có cúp điện rồi thì không dự báo mới nữa
            if (HasBlackoutObservedToday(dayKey))
            {
                ShowCliffordNotification(
                    "Dự báo cúp điện",
                    "Hôm nay đã có cúp điện rồi nên tui không dự báo nữa đâu.");
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
                ? string.Format(CultureInfo.InvariantCulture,
                    "Hôm nay có khả năng {0}% cúp điện thành công (tui đoán lụi).",
                    roll)
                : string.Format(CultureInfo.InvariantCulture,
                    "Hôm nay khả năng {0}% không có cúp điện (tui đoán lụi).",
                    roll);

            _cliffordForecastDueGameTime = Game.GameTime + CLIFFORD_FORECAST_DELAY_MS;
            _cliffordForecastPending = true;
            _cliffordForecastPendingOwnerHash = GetCurrentCharacterHash();

            CloseCliffordMenu();
        }
        catch
        {
        }
    }

    private static string FormatRepeatedForecastMessage(int percent, bool success)
    {
        percent = Math.Max(0, Math.Min(100, percent));
        return success
            ? string.Format(CultureInfo.InvariantCulture,
                "Tui đã dự đoán {0}% khả năng cúp điện hôm nay rồi mà?", percent)
            : string.Format(CultureInfo.InvariantCulture,
                "Tui đã dự đoán {0}% không có cúp điện hôm nay rồi mà?", percent);
    }

    private void ProcessPendingCliffordForecastNotification()
    {
        try
        {
            if (!_cliffordForecastPending)
                return;

            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0 || _cliffordForecastPendingOwnerHash != ownerHash)
            {
                _cliffordForecastPending = false;
                _cliffordForecastDueGameTime = -1;
                _cliffordForecastMessage = string.Empty;
                _cliffordForecastPendingOwnerHash = 0;
                return;
            }

            if (Game.GameTime < _cliffordForecastDueGameTime)
                return;

            _cliffordForecastPending = false;
            _cliffordForecastDueGameTime = -1;
            _cliffordForecastPendingOwnerHash = 0;

            string message = _cliffordForecastMessage;
            _cliffordForecastMessage = string.Empty;

            ShowCliffordNotification(
                "Dự báo cúp điện",
                message);
        }
        catch
        {
        }
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
        catch
        {
        }

        try
        {
            if (!File.Exists(DirectOrderIniPath))
                return 0.0;

            bool inSection = false;
            foreach (string rawLine in File.ReadAllLines(DirectOrderIniPath, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith(";") || line.StartsWith("#"))
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string sec = line.Substring(1, line.Length - 2).Trim();
                    inSection = sec.Equals("CityBlackout", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection)
                    continue;

                int idx = line.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = line.Substring(0, idx).Trim();
                string val = line.Substring(idx + 1).Trim();

                if (!key.Equals("TriggerChance", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    return Math.Max(0.0, Math.Min(1.0, parsed));

                break;
            }
        }
        catch
        {
        }

        return 0.0;
    }

    private static int GetCliffordForecastSuccessPercent(double triggerChance)
    {
        triggerChance = Math.Max(0.0, Math.Min(1.0, triggerChance));

        int percent = (int)Math.Ceiling(triggerChance * 10.0) * 10;
        if (percent < 10)
            percent = 10;
        if (percent > 90)
            percent = 90;

        return percent;
    }

    private void ShowCliffordNotification(string title, string message, int timeout = 7000)
    {
        try
        {
            Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, message);

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_MESSAGETEXT,
                "DIA_CLIFFORD",
                "DIA_CLIFFORD",
                false,
                0,
                "Clifford",
                title);

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_TICKER, false, true);
        }
        catch
        {
            try
            {
                Notification.Show(title + ": " + message);
            }
            catch
            {
                GTA.UI.Screen.ShowSubtitle(title + ": " + message, timeout);
            }
        }
    }

    private void EnsureCliffordMenuCreated()
    {
        try
        {
            if (_cliffordMenuInitialized)
                return;

            _cliffordMenu = new NativeMenu("Clifford Analytics", "THÔNG TIN NHÂN VẬT");
            _uiPool.Add(_cliffordMenu);
            ConfigureKeyboardOnlyMenu(_cliffordMenu);
            _cliffordMenu.Visible = false;
            _cliffordMenuInitialized = true;
        }
        catch
        {
        }
    }

    private void BuildCliffordMenu()
    {
        try
        {
            if (_cliffordMenu == null)
                return;

            _cliffordMenu.Clear();
            _staticItems.Clear();

            AddInfoItem("Tên khách hàng", _snapshot.CustomerName);
            AddInfoItem("Số tiền hiện tại", FormatMoney(_snapshot.CurrentCash));
            AddActionInfoItem("Số phương tiện sở hữu", FormatInt(_snapshot.VehicleCount), OpenVehiclesOwnedMenu);
            AddActionInfoItem("Tổng giá trị kho phương tiện", FormatMoney(_snapshot.VehicleValueTotal), OpenVehiclesValueMenu);
            AddInfoItem("Số điểm thưởng", FormatMoney(_snapshot.RewardPoints));
            AddInfoItem("Số tiền bất hợp pháp", FormatMoney(_snapshot.IllegalMoney));
            AddInfoItem("Dư nợ ngân hàng Lom", FormatMoney(_snapshot.LombankDebt));
            AddInfoItem("Khoản nợ ngân hàng Fleeca", FormatMoney(_snapshot.FleecaDebt));
            AddActionInfoItem("Số phương tiện thế chấp", FormatInt(_snapshot.CollateralVehicleCount), OpenCollateralMenu);
            AddActionInfoItem("Tổng giá trị xe thế chấp", FormatMoney(_snapshot.CollateralVehicleValue), OpenCollateralValueMenu);

            var close = new NativeItem("Đóng báo cáo tài chính");
            close.Activated += (s, e) => CloseCliffordMenu();
            _cliffordMenu.Add(close);

            _staticItems.Add(close);
        }
        catch
        {
        }
    }

    private void AddInfoItem(string label, string value)
    {
        var item = new NativeItem($"{label}: {value}");
        item.Description = "";
        _cliffordMenu.Add(item);
        _staticItems.Add(item);
    }

    private void AddActionInfoItem(string label, string value, Action onActivated)
    {
        var item = new NativeItem($"{label}: {value}");
        item.Description = "Xem chi tiết thông tin này!";
        item.Activated += (s, e) =>
        {
            onActivated?.Invoke();
        };

        _cliffordMenu.Add(item);
        _staticItems.Add(item);
    }

    private void EnsureVehiclesOwnedMenuCreated()
    {
        try
        {
            if (_vehiclesOwnedMenuInitialized)
                return;

            _vehiclesOwnedMenu = new NativeMenu("Vehicles", "CÁC PHƯƠNG TIỆN SỞ HỮU");
            _uiPool.Add(_vehiclesOwnedMenu);
            ConfigureKeyboardOnlyMenu(_vehiclesOwnedMenu);
            _vehiclesOwnedMenu.Visible = false;
            _vehiclesOwnedMenuInitialized = true;
        }
        catch
        {
        }
    }

    private void EnsureVehiclesOwnedValueMenuCreated()
    {
        try
        {
            if (_vehiclesOwnedValueMenuInitialized)
                return;

            _vehiclesOwnedValueMenu = new NativeMenu("Vehicles Value", "CHI TIẾT GIÁ TRỊ PHƯƠNG TIỆN");
            _uiPool.Add(_vehiclesOwnedValueMenu);
            ConfigureKeyboardOnlyMenu(_vehiclesOwnedValueMenu);
            _vehiclesOwnedValueMenu.Visible = false;
            _vehiclesOwnedValueMenuInitialized = true;
        }
        catch
        {
        }
    }

    private void EnsureCollateralMenuCreated()
    {
        try
        {
            if (_collateralMenuInitialized)
                return;

            _collateralMenu = new NativeMenu("Collateral", "CHI TIẾT PHƯƠNG TIỆN THẾ CHẤP");
            _uiPool.Add(_collateralMenu);
            ConfigureKeyboardOnlyMenu(_collateralMenu);
            _collateralMenu.Visible = false;
            _collateralMenuInitialized = true;
        }
        catch
        {
        }
    }

    private void EnsureCollateralValueMenuCreated()
    {
        try
        {
            if (_collateralValueMenuInitialized)
                return;

            _collateralValueMenu = new NativeMenu("Collateral Value", "GIÁ TRỊ PHƯƠNG TIỆN THẾ CHẤP");
            _uiPool.Add(_collateralValueMenu);
            ConfigureKeyboardOnlyMenu(_collateralValueMenu);
            _collateralValueMenu.Visible = false;
            _collateralValueMenuInitialized = true;
        }
        catch
        {
        }
    }

    private NativeCheckboxItem CreateLockedCheckboxItem(string title, string description)
    {
        bool suppress = false;
        var item = new NativeCheckboxItem(title, description, true);

        item.CheckboxChanged += (s, e) =>
        {
            if (suppress)
                return;

            try
            {
                if (!item.Checked)
                {
                    suppress = true;
                    item.Checked = true;
                    suppress = false;
                }
            }
            catch
            {
                suppress = false;
            }
        };

        return item;
    }

    private void OpenVehiclesOwnedMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureVehiclesOwnedMenuCreated();
            BuildVehiclesOwnedMenu();
            ShowOnlyCliffordMenu(_vehiclesOwnedMenu);
        }
        catch
        {
        }
    }

    private void OpenVehiclesValueMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureVehiclesOwnedValueMenuCreated();
            BuildVehiclesOwnedValueMenu();
            ShowOnlyCliffordMenu(_vehiclesOwnedValueMenu);
        }
        catch
        {
        }
    }

    private void OpenCollateralMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureCollateralMenuCreated();
            BuildCollateralMenu();
            ShowOnlyCliffordMenu(_collateralMenu);
        }
        catch
        {
        }
    }

    private void OpenCollateralValueMenu()
    {
        try
        {
            RefreshSummarySnapshot();
            EnsureCollateralValueMenuCreated();
            BuildCollateralValueMenu();
            ShowOnlyCliffordMenu(_collateralValueMenu);
        }
        catch
        {
        }
    }

    private void BuildVehiclesOwnedMenu()
    {
        try
        {
            if (_vehiclesOwnedMenu == null)
                return;

            _vehiclesOwnedMenu.Clear();

            int ownerHash = _snapshot.OwnerHash;
            var vehicles = LoadOwnedVehicles(ownerHash);

            if (vehicles.Count == 0)
            {
                _vehiclesOwnedMenu.Add(new NativeItem("Không có phương tiện sở hữu."));
            }
            else
            {
                int index = 1;
                foreach (var v in vehicles)
                {
                    string name = string.IsNullOrWhiteSpace(v.DisplayName) ? "N/A" : v.DisplayName;
                    var item = CreateLockedCheckboxItem($"{index}. {name}", "Xe đang hoạt động");
                    _vehiclesOwnedMenu.Add(item);
                    index++;
                }
            }

            var back = new NativeItem("Quay lại trang trước");
            back.Activated += (s, e) => ReturnToCliffordMenu();
            _vehiclesOwnedMenu.Add(back);
        }
        catch
        {
        }
    }

    private void BuildVehiclesOwnedValueMenu()
    {
        try
        {
            if (_vehiclesOwnedValueMenu == null)
                return;

            _vehiclesOwnedValueMenu.Clear();

            int ownerHash = _snapshot.OwnerHash;
            var vehicles = LoadOwnedVehicles(ownerHash);

            if (vehicles.Count == 0)
            {
                _vehiclesOwnedValueMenu.Add(new NativeItem("Không có phương tiện sở hữu."));
            }
            else
            {
                int index = 1;
                foreach (var v in vehicles)
                {
                    string name = string.IsNullOrWhiteSpace(v.DisplayName) ? "N/A" : v.DisplayName;
                    var item = new NativeItem($"{index}. {name}: {FormatMoney(v.PurchasePrice)}");
                    _vehiclesOwnedValueMenu.Add(item);
                    index++;
                }
            }

            var back = new NativeItem("Quay lại trang trước");
            back.Activated += (s, e) => ReturnToCliffordMenu();
            _vehiclesOwnedValueMenu.Add(back);
        }
        catch
        {
        }
    }

    private void BuildCollateralMenu()
    {
        try
        {
            if (_collateralMenu == null)
                return;

            _collateralMenu.Clear();

            int ownerHash = _snapshot.OwnerHash;
            var ownedVehicles = LoadOwnedVehicles(ownerHash);
            var collateralStates = LoadCollateralStates(ownerHash);

            var matchedNames = new List<string>();

            foreach (var state in collateralStates)
            {
                var match = FindMatchingVehicleForCollateralState(state, ownedVehicles);
                if (match == null)
                    continue;

                string name = string.IsNullOrWhiteSpace(match.DisplayName) ? "N/A" : match.DisplayName;
                matchedNames.Add(name);
            }

            if (matchedNames.Count == 0)
            {
                _collateralMenu.Add(new NativeItem("Không có phương tiện thế chấp."));
            }
            else
            {
                for (int i = 0; i < matchedNames.Count; i++)
                {
                    _collateralMenu.Add(new NativeItem($"{i + 1}. {matchedNames[i]}"));
                }
            }

            var back = new NativeItem("Quay lại trang trước");
            back.Activated += (s, e) => ReturnToCliffordMenu();
            _collateralMenu.Add(back);
        }
        catch
        {
        }
    }

    private void BuildCollateralValueMenu()
    {
        try
        {
            if (_collateralValueMenu == null)
                return;

            _collateralValueMenu.Clear();

            int ownerHash = _snapshot.OwnerHash;
            var ownedVehicles = LoadOwnedVehicles(ownerHash);
            var collateralStates = LoadCollateralStates(ownerHash);

            var matchedLines = new List<Tuple<string, long>>();

            foreach (var state in collateralStates)
            {
                var match = FindMatchingVehicleForCollateralState(state, ownedVehicles);
                if (match == null)
                    continue;

                string name = string.IsNullOrWhiteSpace(match.DisplayName) ? "N/A" : match.DisplayName;
                long price = Math.Max(0L, match.PurchasePrice);
                matchedLines.Add(Tuple.Create(name, price));
            }

            if (matchedLines.Count == 0)
            {
                _collateralValueMenu.Add(new NativeItem("Không có phương tiện thế chấp."));
            }
            else
            {
                for (int i = 0; i < matchedLines.Count; i++)
                {
                    _collateralValueMenu.Add(
                        new NativeItem($"{i + 1}. {matchedLines[i].Item1}: {FormatMoney(matchedLines[i].Item2)}"));
                }
            }

            var back = new NativeItem("Quay lại trang trước");
            back.Activated += (s, e) => ReturnToCliffordMenu();
            _collateralValueMenu.Add(back);
        }
        catch
        {
        }
    }

    private static string FormatInt(int value)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0:N0}", Math.Max(0, value));
    }

    private static string FormatMoney(long value)
    {
        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", Math.Max(0, value));
    }

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
        catch
        {
            return 0L;
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
                CustomerName = GetCurrentCharacterName(), // NEW
                CurrentCash = GetStoryCashByCharacterHash(ownerHash)
            };

            // Đọc trực tiếp từ persistent_vehicles.txt theo ownerHash hiện tại
            var vehicles = LoadOwnedVehicles(ownerHash);
            _snapshot.VehicleCount = vehicles.Count;
            _snapshot.VehicleValueTotal = vehicles.Sum(v => Math.Max(0L, v.PurchasePrice));

            ReloadSpendingAccumulatorFromDisk();
            _snapshot.RewardPoints = TryGetRewardPoints();

            _snapshot.IllegalMoney = CityBlackoutHackerState.GetDirtyMoneyBalanceForCurrentCharacter();
            _snapshot.FleecaDebt = ReadFleecaDebt(ownerHash);

            // Đọc collateral state của đúng nhân vật hiện tại,
            // rồi truy ngược sang persistent_vehicles.txt để lấy giá mua
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
        }
        catch
        {
            _snapshot = new SummarySnapshot();
        }
    }

    private List<VehicleSnapshot> LoadOwnedVehicles(int ownerHash)
    {
        var result = new List<VehicleSnapshot>();

        try
        {
            if (ownerHash == 0)
                return result;

            if (!File.Exists(PersistentVehiclesFile))
                return result;

            foreach (string raw in File.ReadAllLines(PersistentVehiclesFile, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                if (TryParseVehicleLine(raw, out VehicleSnapshot vehicle))
                {
                    if (vehicle != null && (ownerHash == 0 || vehicle.OwnerHash == ownerHash))
                        result.Add(vehicle);
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private bool TryParseVehicleLine(string line, out VehicleSnapshot vehicle)
    {
        vehicle = null;

        try
        {
            string[] p = line.Split('|');
            if (p.Length < 11)
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

            long purchasePrice = 0;
            if (p.Length > 10)
                long.TryParse(p[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out purchasePrice);

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
        catch
        {
            vehicle = null;
            return false;
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
            if (string.IsNullOrWhiteSpace(raw))
                return 0u;

            raw = raw.Trim();

            // Case 1: "Hakuchou Drag (0xF0C2A91F)"
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

            // Case 2: "D757D97D" hoặc "0xD757D97D"
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

            // Fallback decimal
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
        return (c >= '0' && c <= '9')
            || (c >= 'a' && c <= 'f')
            || (c >= 'A' && c <= 'F');
    }

    private VehicleSnapshot FindMatchingVehicleForCollateralState(
    CollateralStateEntry state,
    List<VehicleSnapshot> ownedVehicles)
    {
        try
        {
            if (state == null || ownedVehicles == null || ownedVehicles.Count == 0)
                return null;

            string statePlate = NormalizePlate(state.Plate);

            foreach (var vehicle in ownedVehicles)
            {
                if (vehicle == null || vehicle.ModelHash != state.ModelHash)
                    continue;

                string vehiclePlate = NormalizePlate(vehicle.Plate);

                if (!string.IsNullOrEmpty(statePlate) &&
                    !string.IsNullOrEmpty(vehiclePlate) &&
                    statePlate == vehiclePlate)
                {
                    return vehicle;
                }

                if (vehicle.Position.DistanceTo2D(state.Position) < 5f)
                    return vehicle;
            }
        }
        catch
        {
        }

        return null;
    }

    private List<CollateralStateEntry> LoadCollateralStates(int ownerHash)
    {
        var result = new List<CollateralStateEntry>();

        try
        {
            if (ownerHash == 0)
                return result;

            string file = Path.Combine(FleecaStateRoot, $"collateral_state_{ownerHash}.dat");
            if (!File.Exists(file))
                return result;

            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string[] p = raw.Split('|');
                if (p.Length < 6)
                    continue;

                uint modelHash = ParseModelHash(p[0]);
                if (modelHash == 0)
                    continue;

                string plate = (p[1] ?? "").Trim();

                float x = ParseFloat(p, 2);
                float y = ParseFloat(p, 3);
                float z = ParseFloat(p, 4);
                float heading = ParseFloat(p, 5);

                result.Add(new CollateralStateEntry
                {
                    ModelHash = modelHash,
                    Plate = plate,
                    Position = new Vector3(x, y, z),
                    Heading = heading
                });
            }
        }
        catch
        {
        }

        return result;
    }

    private static string NormalizePlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
            return "";

        return new string(plate.Trim().Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
    }

    private long TryGetRewardPoints()
    {
        try
        {
            long points = 0;
            string text = File.Exists(SpendingFile) ? File.ReadAllText(SpendingFile).Trim() : "";

            if (string.IsNullOrWhiteSpace(text))
                return 0L;

            string digits = new string(text.Where(char.IsDigit).ToArray());
            if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                points = parsed;

            return Math.Max(0L, points);
        }
        catch
        {
            return 0L;
        }
    }

    private void ReloadSpendingAccumulatorFromDisk()
    {
        try
        {
            // đọc trực tiếp file để menu luôn phản ánh giá trị mới nhất
            if (!File.Exists(SpendingFile))
                return;
        }
        catch
        {
        }
    }

    private long ReadFleecaDebt(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return 0L;

            string file = Path.Combine(FleecaStateRoot, $"loan_state_{ownerHash}.dat");
            if (!File.Exists(file))
                return 0L;

            string text = File.ReadAllText(file, Encoding.UTF8).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return 0L;

            string[] p = text.Split('|');
            if (p.Length < 2)
                return 0L;

            long debt = 0L;
            if (long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out debt))
                return Math.Max(0L, debt);

            return 0L;
        }
        catch
        {
            return 0L;
        }
    }

    private long ReadLombankDebt(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return 0L;

            string file = Path.Combine(LombankStateRoot, $"lombank_state_{ownerHash}.dat");
            if (!File.Exists(file))
                return 0L;

            foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                int idx = line.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim();

                if (!key.Equals("debt", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long debt))
                    return Math.Max(0L, debt);
            }
        }
        catch
        {
        }

        return 0L;
    }

    private void ProcessVisibleMenuState()
    {
        try
        {
            if (_cliffordMenu == null || !_cliffordMenu.Visible)
                return;

            if (!_cliffordMenuInitialized)
                BuildCliffordMenu();

            // Không tự động đóng, chỉ cho phép Back/Escape từ OnKeyDown.
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
}
