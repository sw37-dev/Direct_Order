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

public partial class LombankScript : Script
{
    private const long INITIAL_TOTAL_LIMIT = 1_000_000L;
    private const long MAX_TOTAL_LIMIT = 15_000_000L;
    private const long LIMIT_INCREMENT = 200_000L;
    private const decimal LIMIT_INCREASE_WITHDRAW_RATIO = 0.30m; // 30%

    private const double OVERDUE_DAILY_MULTIPLIER = 1.002; // +0.2% mỗi ngày
    private static readonly TimeSpan LOAN_TERM = TimeSpan.FromDays(7);
    private static readonly TimeSpan OVERDUE_NOTICE_WINDOW = TimeSpan.FromHours(2);

    private static readonly TimeSpan LOAN_REPAY_UNLOCK_DELAY = TimeSpan.FromDays(2);
    private static readonly TimeSpan LOAN_REPAY_UNLOCK_NOTICE_WINDOW = TimeSpan.FromHours(2);

    private static string TerminalBusyMessage =>
        L("Lombank_TerminalBusyMessage",
            "Hãy hoàn tất giao dịch hiện tại trước khi giao dịch mới! Xin cảm ơn quý khách!");

    private static string LombankNotificationBrand =>
        L("Lombank_NotificationBrand", "Lom Bank");
    private const int LombankNotificationTimeout = 2500;

    // Tọa độ ATM chính thức
    private static readonly Vector3 TerminalPosition = new Vector3(-1566.704f, -588.7851f, 33.4065f);
    private const float TerminalHeading = 35.1283f;

    // Vị trí marker hiển thị khi đã xác nhận giao dịch
    private static readonly Vector3 TerminalMarkerPosition = new Vector3(-1566.221f, -589.4427f, 33.96097f);
    private const float TerminalMarkerScaleX = 0.75f;
    private const float TerminalMarkerScaleY = 0.75f;
    private const float TerminalMarkerScaleZ = 0.75f;

    private const float TerminalRadius = 1.9f;

    private const int InputCooldownMs = 1000;
    private const int LoanStateCheckIntervalMs = 75_000;

    private CustomiFruit _lombankPhoneInstance = null;
    private iFruitContact _lombankContact = null;
    private bool _lombankContactAnsweredBound = false;

    private readonly ObjectPool _uiPool = new ObjectPool();
    private NativeMenu _mainMenu;
    private bool _menuInitialized = false;

    private NativeItem _customerItem;
    private NativeItem _totalLimitItem;
    private NativeItem _currentDebtItem;
    private NativeItem _totalDueItem;
    private NativeItem _remainingLimitItem;
    private NativeItem _withdrawConfirmItem;
    private NativeItem _cancelItem;

    // Các item mới trong menu
    private NativeItem _transactionDateItem;
    private NativeItem _transactionTimeItem;

    // Lưu thời điểm rút tiền đầu tiên thành công trong chu kỳ hiện tại
    private DateTime? _firstWithdrawAt = null;

    private const int HASH_MICHAEL = 225514697;
    private const int HASH_FRANKLIN = -1692214353;
    private const int HASH_TREVOR = -1686040670;

    private bool _menuOpen = false;
    private bool _awaitTerminalAction = false;
    private bool _terminalMarkerVisible = false;
    private int _lastInputGameTime = 0;

    private int _lastLoanStateCheckGameTime = -LoanStateCheckIntervalMs;

    private CustomiFruit _phoneInstance = null;
    private bool _contactAdded = false;

    private int _activeCharacterHash = 0;
    private string _customerName = "N/A";

    private long _currentDebt = 0;
    private long _totalLimit = INITIAL_TOTAL_LIMIT;
    private long _cycleWithdrawnAmount = 0;

    private DateTime? _loanOpenedAt = null;
    private DateTime? _lastPenaltyAppliedAt = null;
    private bool _overdueNoticeShown = false;
    private bool _repayUnlockNoticeShown = false;

    private readonly string _stateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Lom Bank");

    private int _scriptStartGameTime = 0;
    private const int AutoSpawnDelayMs = 5000;
    private bool _terminalPropSpawned = false;

    private static string L(string key, string fallback = "")
    {
        return Language.Get(key, fallback);
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
            // Xử lý ngoại lệ nếu cần thiết
        }
    }

    private void UpdateLemonUiMouseState()
    {
        try
        {
            bool anyMenuVisible = _mainMenu != null && _mainMenu.Visible;
            if (!anyMenuVisible)
                return;

            SetBoolPropertyIfExists(_mainMenu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");

            SetBoolPropertyIfExists(_uiPool, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");
        }
        catch
        {
            // Xử lý ngoại lệ nếu cần thiết
        }
    }

    public LombankScript()
    {
        Interval = 1000;
        Tick += OnTick;
        KeyDown += OnKeyDown;

        _scriptStartGameTime = Game.GameTime;

        SyncCharacterState();
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            SyncCharacterState();
            EnsureLombankContactRegistered();
            EnsureMainMenuCreated();
            EnsureTerminalPropSpawned();

            ProcessLoanTimeEffects(force: false, showNotice: true);

            if (_awaitTerminalAction && IsPlayerNearTerminal())
            {
                ShowTerminalHelpText();
            }

            if (_terminalMarkerVisible)
            {
                DrawTerminalMarker();
            }

            UpdateLemonUiMouseState();

            if (_uiPool.AreAnyVisible)
            {
                _uiPool.Process();
                UpdateLemonUiMouseState();
                Interval = 0;
            }
            else
            {
                Interval = _awaitTerminalAction ? 0 : 1000;
            }
        }
        catch (Exception ex)
        {
            Log("OnTick failed: " + ex);
        }
    }

    private void DrawTerminalMarker()
    {
        try
        {
            if (!_terminalMarkerVisible)
                return;

            Vector3 markerPos = TerminalMarkerPosition + new Vector3(0.0f, 0.0f, -1.42f);

            Function.Call(Hash.DRAW_MARKER,
                1, // cylindrical marker
                markerPos.X,
                markerPos.Y,
                markerPos.Z,
                0f, 0f, 0f,
                0f, 0f, 0f,
                TerminalMarkerScaleX,
                TerminalMarkerScaleY,
                TerminalMarkerScaleZ,
                235, 242, 255, 210,
                false,
                true,
                2,
                false,
                null,
                null,
                false);
        }
        catch { }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            // Chặn tương tác khi menu hacker đang mở
            if (_uiPool != null && _uiPool.AreAnyVisible)
                return;

            if (_mainMenu != null && _mainMenu.Visible)
            {
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                {
                    CloseMainMenu();
                    return;
                }
            }

            if (!_awaitTerminalAction)
                return;

            if (!IsPlayerNearTerminal())
                return;

            if (e.KeyCode == Keys.Back)
            {
                FinishTerminalSession(L("Lombank_TerminalCanceled", "Đã hủy dịch vụ tại ATM."));
                return;
            }

            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                BeginWithdrawDialog();
                return;
            }

            if (e.KeyCode == Keys.Enter && e.Shift)
            {
                BeginRepayDialog();
                return;
            }
        }
        catch (Exception ex)
        {
            Log("OnKeyDown failed: " + ex);
        }
    }

    private static long GetRequiredWithdrawAmountForLimitIncrease(long totalLimit)
    {
        if (totalLimit <= 0)
            return long.MaxValue;

        return (long)Math.Ceiling(totalLimit * LIMIT_INCREASE_WITHDRAW_RATIO);
    }

    private DateTime GetCurrentInGameDateTime()
    {
        try
        {
            int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);
            int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
            int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);
            int hour = Function.Call<int>(Hash.GET_CLOCK_HOURS);
            int minute = Function.Call<int>(Hash.GET_CLOCK_MINUTES);

            // GTA có thể trả month theo kiểu 0..11 hoặc 1..12 tùy build/mod
            if (month < 1 || month > 12)
                month += 1;

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
        catch
        {
            // fallback an toàn, nhưng vẫn ưu tiên in-game
            return new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        }
    }

    private static string FormatLockRemaining(TimeSpan ts)
    {
        if (ts <= TimeSpan.Zero)
            return "0 phút";

        int totalMinutes = (int)Math.Ceiling(ts.TotalMinutes);
        int days = totalMinutes / (24 * 60);
        int hours = (totalMinutes % (24 * 60)) / 60;
        int minutes = totalMinutes % 60;

        if (days > 0)
            return string.Format(CultureInfo.InvariantCulture, "{0} ngày {1} giờ", days, hours);

        if (hours > 0)
            return string.Format(CultureInfo.InvariantCulture, "{0} giờ {1} phút", hours, minutes);

        return string.Format(CultureInfo.InvariantCulture, "{0} phút", Math.Max(1, minutes));
    }

    private bool EnsureAtmTransactionsAllowed()
    {
        if (!CityBlackoutHackerState.IsAtmTransactionLockedForCurrentCharacter())
            return true;

        TimeSpan remain = CityBlackoutHackerState.GetAtmTransactionLockRemainingForCurrentCharacter();

        ShowLombankNotification(
            L("Lombank_RepayFailTitle", "Giao dịch thất bại"),
            string.Format(
                L("Lombank_AtmLockedNotice",
                    "Lom Bank đã khóa toàn bộ giao dịch ATM của quý khách. Vui lòng quay lại sau {0}."),
                FormatLockRemaining(remain)));

        return false;
    }

    private void SyncCharacterState()
    {
        try
        {
            int hash = GetCurrentCharacterHash();
            if (hash == 0)
                return;

            if (_activeCharacterHash == hash)
            {
                RefreshMainMenu();
                return;
            }

            _activeCharacterHash = hash;
            _customerName = GetCustomerDisplayName(hash);
            LoadStateForCurrentCharacter();
            RefreshMainMenu();
        }
        catch (Exception ex)
        {
            Log("SyncCharacterState failed: " + ex);
        }
    }

    private void ShowLombankNotification(string title, string message, int timeout = LombankNotificationTimeout)
    {
        try
        {
            Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, message);

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_MESSAGETEXT,
                "WEB_LOMBANK",
                "WEB_LOMBANK",
                false,
                0,
                LombankNotificationBrand,
                title);

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_TICKER, false, true);
        }
        catch
        {
            try
            {
                Notification.Show($"{title}: {message}");
            }
            catch
            {
                GTA.UI.Screen.ShowSubtitle($"{title}: {message}", timeout);
            }
        }
    }

    private int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists())
                return 0;

            int hash = p.Model.Hash;

            if (hash == HASH_MICHAEL ||
                hash == HASH_FRANKLIN ||
                hash == HASH_TREVOR)
            {
                return hash;
            }
        }
        catch { }

        return 0;
    }

    private string GetCustomerDisplayName(int modelHash)
    {
        if (modelHash == HASH_MICHAEL)
            return "Michael De Santa";

        if (modelHash == HASH_FRANKLIN)
            return "Franklin Clinton";

        if (modelHash == HASH_TREVOR)
            return "Trevor Philips";

        return "Không xác định";
    }

    private string GetStateFileForOwner(int ownerHash)
    {
        Directory.CreateDirectory(_stateRoot);
        return Path.Combine(_stateRoot, $"lombank_state_{ownerHash}.dat");
    }

    private void LoadStateForCurrentCharacter()
    {
        try
        {
            Directory.CreateDirectory(_stateRoot);

            // 1. Reset trạng thái ngay đầu hàm (bao gồm các biến mới)
            _currentDebt = 0;
            _totalLimit = INITIAL_TOTAL_LIMIT;
            _cycleWithdrawnAmount = 0; // Biến mới
            _loanOpenedAt = null;
            _lastPenaltyAppliedAt = null;
            _overdueNoticeShown = false;
            _repayUnlockNoticeShown = false;
            _firstWithdrawAt = null;

            string file = GetStateFileForOwner(_activeCharacterHash);
            if (!File.Exists(file))
                return;

            string text = File.ReadAllText(file).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            // 2. Xử lý kiểu dữ liệu Legacy (không có dấu =)
            if (!text.Contains("="))
            {
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long legacyDebt))
                {
                    _currentDebt = Math.Max(0, legacyDebt);
                    _cycleWithdrawnAmount = _currentDebt; // Gán mặc định bằng nợ cũ

                    if (_currentDebt > 0)
                    {
                        DateTime now = GetCurrentInGameDateTime();
                        _loanOpenedAt = now;
                        _lastPenaltyAppliedAt = now.AddDays(7);
                        _overdueNoticeShown = false;
                        _repayUnlockNoticeShown = false;
                    }
                }

                return;
            }

            // 3. Parse dữ liệu vào Dictionary
            Dictionary<string, string> data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int idx = line.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim();
                data[key] = value;
            }

            // 4. Đọc các trường dữ liệu cơ bản
            if (data.TryGetValue("debt", out string debtText) &&
                long.TryParse(debtText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long debt))
            {
                _currentDebt = Math.Max(0, debt);
            }

            if (data.TryGetValue("limit", out string limitText) &&
                long.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long limit))
            {
                _totalLimit = Math.Max(INITIAL_TOTAL_LIMIT, Math.Min(MAX_TOTAL_LIMIT, limit));
            }

            // 5. Đọc trường withdrawnThisCycle mới hoặc fallback
            if (data.TryGetValue("withdrawnThisCycle", out string withdrawnText) &&
                long.TryParse(withdrawnText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long withdrawn))
            {
                _cycleWithdrawnAmount = Math.Max(0, withdrawn);
            }
            else if (_currentDebt > 0)
            {
                _cycleWithdrawnAmount = _currentDebt;
            }

            // 6. Đọc các thông tin thời gian và thông báo
            _loanOpenedAt = ParseNullableDateTime(data, "loanOpenedAt");
            _lastPenaltyAppliedAt = ParseNullableDateTime(data, "lastPenaltyAppliedAt");
            _firstWithdrawAt = ParseNullableDateTime(data, "firstWithdrawAt");

            if (data.TryGetValue("overdueNoticeShown", out string shownText))
            {
                _overdueNoticeShown = shownText == "1" ||
                                      shownText.Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            if (data.TryGetValue("repayUnlockNoticeShown", out string repayShownText))
            {
                _repayUnlockNoticeShown = repayShownText == "1" ||
                                          repayShownText.Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            // 7. Logic kiểm tra tính toàn vẹn dữ liệu
            if (_currentDebt > 0 && !_loanOpenedAt.HasValue)
            {
                _loanOpenedAt = GetCurrentInGameDateTime();
            }

            if (_currentDebt > 0 && !_lastPenaltyAppliedAt.HasValue && _loanOpenedAt.HasValue)
            {
                _lastPenaltyAppliedAt = _loanOpenedAt.Value.AddDays(7);
            }
        }
        catch (Exception ex)
        {
            Log("LoadStateForCurrentCharacter failed: " + ex);

            // Reset về mặc định nếu có lỗi xảy ra
            _currentDebt = 0;
            _totalLimit = INITIAL_TOTAL_LIMIT;
            _cycleWithdrawnAmount = 0;
            _loanOpenedAt = null;
            _lastPenaltyAppliedAt = null;
            _overdueNoticeShown = false;
            _repayUnlockNoticeShown = false;
        }
    }

    private void SaveStateForCurrentCharacter()
    {
        try
        {
            if (_activeCharacterHash == 0)
                return;

            Directory.CreateDirectory(_stateRoot);

            string file = GetStateFileForOwner(_activeCharacterHash);

            var sb = new StringBuilder();
            sb.AppendLine("version=5");
            sb.AppendLine("debt=" + _currentDebt.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("limit=" + _totalLimit.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("withdrawnThisCycle=" + _cycleWithdrawnAmount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("loanOpenedAt=" + (_loanOpenedAt.HasValue ? _loanOpenedAt.Value.ToString("o", CultureInfo.InvariantCulture) : ""));
            sb.AppendLine("lastPenaltyAppliedAt=" + (_lastPenaltyAppliedAt.HasValue ? _lastPenaltyAppliedAt.Value.ToString("o", CultureInfo.InvariantCulture) : ""));
            sb.AppendLine("firstWithdrawAt=" + (_firstWithdrawAt.HasValue ? _firstWithdrawAt.Value.ToString("o", CultureInfo.InvariantCulture) : ""));
            sb.AppendLine("overdueNoticeShown=" + (_overdueNoticeShown ? "1" : "0"));
            sb.AppendLine("repayUnlockNoticeShown=" + (_repayUnlockNoticeShown ? "1" : "0"));

            File.WriteAllText(file, sb.ToString());
        }
        catch (Exception ex)
        {
            Log("SaveStateForCurrentCharacter failed: " + ex);
        }
    }

    private static DateTime? ParseNullableDateTime(Dictionary<string, string> data, string key)
    {
        if (!data.TryGetValue(key, out string value))
            return null;

        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParseExact(
            value,
            "o",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTime parsed))
        {
            return parsed;
        }

        if (DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out parsed))
        {
            return parsed;
        }

        return null;
    }

    private void ResetLoanCycleState()
    {
        _loanOpenedAt = null;
        _lastPenaltyAppliedAt = null;
        _overdueNoticeShown = false;
        _repayUnlockNoticeShown = false;
        _cycleWithdrawnAmount = 0;
        _firstWithdrawAt = null; // thêm dòng này
    }

    private void ApplyOnTimeRepaymentLimitIncrease(DateTime repayAt)
    {
        try
        {
            if (!_loanOpenedAt.HasValue)
                return;

            DateTime dueAt = _loanOpenedAt.Value.Add(LOAN_TERM);

            // Chỉ thưởng khi trả hết nợ trước hoặc đúng thời điểm đáo hạn
            if (repayAt > dueAt)
                return;

            long oldLimit = _totalLimit;
            _totalLimit = Math.Min(MAX_TOTAL_LIMIT, _totalLimit + LIMIT_INCREMENT);

            if (_totalLimit > oldLimit)
            {
                ShowLombankNotification(
                    L("Lombank_LimitIncreasedTitle", "Hạn mức"),
                    string.Format(
                        L("Lombank_LimitIncreasedNotice", "Trả nợ đúng hạn, hạn mức của quý khách đã tăng thêm {0}."),
                        FormatMoney(_totalLimit - oldLimit)));
            }
        }
        catch (Exception ex)
        {
            Log("ApplyOnTimeRepaymentLimitIncrease failed: " + ex);
        }
    }

    private void StartNewLoanCycle()
    {
        DateTime now = GetCurrentInGameDateTime();
        _loanOpenedAt = now;
        _lastPenaltyAppliedAt = now.AddDays(7);
        _overdueNoticeShown = false;
        _repayUnlockNoticeShown = false;
        _cycleWithdrawnAmount = 0;
        _firstWithdrawAt = null; // thêm dòng này
    }

    private string FormatTransactionDate()
    {
        if (!_firstWithdrawAt.HasValue)
            return "----/--/--";

        DateTime dt = _firstWithdrawAt.Value;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}, {1}",
            GetVietnameseDayName(dt.DayOfWeek),
            dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private string FormatTransactionTime()
    {
        if (!_firstWithdrawAt.HasValue)
            return "--:--";

        DateTime dt = _firstWithdrawAt.Value;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} - {1}",
            GetVietnameseTimePeriodLabel(dt),
            dt.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    private static bool IsInOverdueNoticeWindow(DateTime now, DateTime dueAt)
    {
        DateTime windowStart = new DateTime(dueAt.Year, dueAt.Month, dueAt.Day, dueAt.Hour, 0, 0, dueAt.Kind);
        DateTime windowEnd = windowStart.Add(OVERDUE_NOTICE_WINDOW);
        return now >= windowStart && now < windowEnd;
    }

    private static bool IsInRepayUnlockNoticeWindow(DateTime now, DateTime unlockAt)
    {
        DateTime windowStart = new DateTime(unlockAt.Year, unlockAt.Month, unlockAt.Day, unlockAt.Hour, 0, 0, unlockAt.Kind);
        DateTime windowEnd = windowStart.Add(LOAN_REPAY_UNLOCK_NOTICE_WINDOW);
        return now >= windowStart && now < windowEnd;
    }

    private bool ProcessLoanTimeEffects(bool force = false, bool showNotice = true)
    {
        try
        {
            if (_currentDebt <= 0 || !_loanOpenedAt.HasValue)
                return false;

            int gameTime = Game.GameTime;
            if (!force && gameTime - _lastLoanStateCheckGameTime < LoanStateCheckIntervalMs)
                return false;

            _lastLoanStateCheckGameTime = gameTime;

            bool changed = false;
            DateTime now = GetCurrentInGameDateTime();
            DateTime dueAt = _loanOpenedAt.Value.Add(LOAN_TERM);
            DateTime repayUnlockAt = _loanOpenedAt.Value.Add(LOAN_REPAY_UNLOCK_DELAY);

            if (showNotice && !_repayUnlockNoticeShown && now >= repayUnlockAt && IsInRepayUnlockNoticeWindow(now, repayUnlockAt))
            {
                ShowLombankNotification(
                    L("Lombank_RepayUnlockedTitle", "Thông báo"),
                    L("Lombank_RepayUnlockedNotice", "Ngân hàng đã mở khóa kỳ trả nợ của quý khách rồi nhé!"));

                _repayUnlockNoticeShown = true;
                changed = true;
            }

            if (now >= dueAt)
            {
                if (!_overdueNoticeShown && showNotice && IsInOverdueNoticeWindow(now, dueAt))
                {
                    ShowLombankNotification(
                        L("Lombank_OverdueTitle", "Quá hạn"),
                        L("Lombank_OverdueNotice", "Lom Bank sẽ tăng phí do quá hạn trả nợ tín dụng!"));

                    _overdueNoticeShown = true;
                    changed = true;
                }

                DateTime lastAppliedAt = _lastPenaltyAppliedAt ?? dueAt;
                if (now >= lastAppliedAt.AddDays(1))
                {
                    int daysToApply = (int)Math.Floor((now - lastAppliedAt).TotalDays);

                    if (daysToApply > 0)
                    {
                        double multiplier = Math.Pow(OVERDUE_DAILY_MULTIPLIER, daysToApply);
                        long newDebt = (long)Math.Round(_currentDebt * multiplier, 0, MidpointRounding.AwayFromZero);

                        if (newDebt < _currentDebt)
                            newDebt = _currentDebt;

                        _currentDebt = newDebt;
                        _lastPenaltyAppliedAt = lastAppliedAt.AddDays(daysToApply);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                SaveStateForCurrentCharacter();
                RefreshMainMenu();
            }

            return changed;
        }
        catch (Exception ex)
        {
            Log("ProcessLoanTimeEffects failed: " + ex);
            return false;
        }
    }

    private long GetTotalAmountDue()
    {
        return Math.Max(0, _currentDebt);
    }

    private long GetRemainingLimit()
    {
        return Math.Max(0, _totalLimit - _currentDebt);
    }

    private static string FormatMoney(long value)
    {
        if (value < 0) value = 0;
        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private static string FormatDebtMoney(long value)
    {
        if (value <= 0)
            return "$0";

        return "-" + FormatMoney(value);
    }

    private static string GetVietnameseDayName(DayOfWeek dayOfWeek)
    {
        switch (dayOfWeek)
        {
            case DayOfWeek.Monday: return "Thứ Hai";
            case DayOfWeek.Tuesday: return "Thứ Ba";
            case DayOfWeek.Wednesday: return "Thứ Tư";
            case DayOfWeek.Thursday: return "Thứ Năm";
            case DayOfWeek.Friday: return "Thứ Sáu";
            case DayOfWeek.Saturday: return "Thứ Bảy";
            default: return "Chủ Nhật";
        }
    }

    private static string GetVietnameseTimePeriodLabel(DateTime time)
    {
        int minutes = time.Hour * 60 + time.Minute;

        if (minutes >= 0 && minutes <= 59)
            return "Nửa đêm";
        if (minutes >= 60 && minutes <= 299)
            return "Rạng sáng";
        if (minutes >= 300 && minutes <= 659)
            return "Sáng";
        if (minutes >= 660 && minutes <= 779)
            return "Trưa";
        if (minutes >= 780 && minutes <= 1079)
            return "Chiều";
        if (minutes >= 1080 && minutes <= 1259)
            return "Tối";
        return "Đêm";
    }

    private void EnsureLombankContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_lombankPhoneInstance, phone))
            {
                _lombankPhoneInstance = phone;
                _lombankContact = null;
                _lombankContactAnsweredBound = false;
            }

            bool contactEnabled = CityBlackoutHackerState.IsAtmTransactionContactEnabledForCurrentCharacter();

            // Tìm contact hiện có
            if (_lombankContact == null)
            {
                _lombankContact = phone.Contacts.FirstOrDefault(c =>
                    string.Equals(c.Name, "Lom Bank", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Name, "Lombank", StringComparison.OrdinalIgnoreCase));
            }

            // Nếu đang khóa ATM: contact phải bị tắt, không gắn Answered
            if (!contactEnabled)
            {
                if (_lombankContact != null)
                {
                    _lombankContact.Active = false;
                }

                return;
            }

            // Nếu chưa có contact thì tạo mới
            if (_lombankContact == null)
            {
                _lombankContact = new iFruitContact("Lom Bank")
                {
                    Active = true,
                    DialTimeout = 2500,
                    Bold = false,
                    Icon = new ContactIcon("WEB_LOMBANK")
                };

                phone.Contacts.Add(_lombankContact);
            }
            else
            {
                _lombankContact.Active = true;
            }

            // Chỉ gắn event khi contact thật sự đang bật
            if (!_lombankContactAnsweredBound)
            {
                _lombankContact.Answered += OnLombankAnswered;
                _lombankContactAnsweredBound = true;
            }
        }
        catch (Exception ex)
        {
            Log("EnsureLombankContactRegistered failed: " + ex);
        }
    }

    private void OnLombankAnswered(iFruitContact sender)
    {
        try
        {
            if (!CityBlackoutHackerState.IsAtmTransactionContactEnabledForCurrentCharacter())
            {
                ShowLombankNotification(
                    L("Lombank_TerminalTitle", "Giao dịch"),
                    L("Lombank_AtmLockedNotice", "Lom Bank đang tạm khóa giao dịch ATM của quý khách."));
                return;
            }

            OpenMainMenu();
            TryClosePhone();
        }
        catch (Exception ex)
        {
            Log("OnLombankAnswered failed: " + ex);
        }
    }

    private void EnsureMainMenuCreated()
    {
        try
        {
            if (_menuInitialized)
                return;

            _mainMenu = new NativeMenu(
                L("Lombank_MenuTitle", "LomBank"),
                L("Lombank_MenuSubtitle", "CÁC DANH MỤC NGÂN HÀNG"));

            // Khởi tạo các item cũ
            _customerItem = new NativeItem("");
            _totalLimitItem = new NativeItem("");
            _remainingLimitItem = new NativeItem("");
            _currentDebtItem = new NativeItem("");
            _totalDueItem = new NativeItem("");

            // Khởi tạo 2 item mới theo hướng dẫn
            _transactionDateItem = new NativeItem("");
            _transactionTimeItem = new NativeItem("");

            _withdrawConfirmItem = new NativeItem(
                L("Lombank_MenuWithdrawConfirm", "Xác nhận giao dịch"));
            _cancelItem = new NativeItem(
                L("Lombank_MenuCancel", "Hủy dịch vụ"));

            // Xử lý sự kiện khi nhấn Xác nhận giao dịch
            _withdrawConfirmItem.Activated += (s, e) =>
            {
                if (_awaitTerminalAction)
                {
                    ShowLombankNotification(
                        L("Lombank_TerminalTitle", "Giao dịch"),
                        TerminalBusyMessage,
                        3000);
                    return;
                }

                _awaitTerminalAction = true;
                _terminalMarkerVisible = true;
                CloseMainMenu();
                ShowLombankNotification(
                    L("Lombank_TerminalTitle", "Giao dịch"),
                    L("Lombank_TerminalGoToAtm", "Đến ATM của LomBank để thực hiện giao dịch."));
            };

            // Xử lý sự kiện khi nhấn Hủy dịch vụ
            _cancelItem.Activated += (s, e) =>
            {
                CloseMainMenu();
            };

            // Thêm các item vào Menu theo thứ tự yêu cầu
            _mainMenu.Add(_customerItem);
            _mainMenu.Add(_totalLimitItem);
            _mainMenu.Add(_remainingLimitItem);
            _mainMenu.Add(_currentDebtItem);
            _mainMenu.Add(_totalDueItem);

            // Chèn 2 item mới sau remainingLimitItem
            _mainMenu.Add(_transactionDateItem); // Mới
            _mainMenu.Add(_transactionTimeItem); // Mới

            _mainMenu.Add(_withdrawConfirmItem);
            _mainMenu.Add(_cancelItem);

            _uiPool.Add(_mainMenu);
            _menuInitialized = true;

            RefreshMainMenu();
        }
        catch (Exception ex)
        {
            Log("EnsureMainMenuCreated failed: " + ex);
        }
    }

    private void RefreshMainMenu()
    {
        try
        {
            if (_customerItem == null)
                return;

            _customerItem.Title = string.Format(
                L("Lombank_CustomerLabel", "Tên khách hàng: {0}"),
                _customerName);

            _totalLimitItem.Title = string.Format(
                L("Lombank_TotalLimitLabel", "Tổng hạn mức: {0}"),
                FormatMoney(_totalLimit));

            _remainingLimitItem.Title = string.Format(
                L("Lombank_RemainingLimitLabel", "Hạn mức còn lại: {0}"),
                FormatMoney(GetRemainingLimit()));

            _currentDebtItem.Title = string.Format(
                L("Lombank_CurrentDebtLabel", "Dư nợ hiện tại: {0}"),
                FormatDebtMoney(_currentDebt));

            _totalDueItem.Title = string.Format(
                L("Lombank_TotalDueLabel", "Số tiền cần nạp: {0}"),
                FormatMoney(GetTotalAmountDue()));

            _transactionDateItem.Title = string.Format(
                L("Lombank_TransactionDateLabel", "Ngày giao dịch: {0}"),
                FormatTransactionDate());

            _transactionTimeItem.Title = string.Format(
                L("Lombank_TransactionTimeLabel", "Thời gian giao dịch: {0}"),
                FormatTransactionTime());

            _withdrawConfirmItem.Title = L("Lombank_MenuWithdrawConfirm", "Xác nhận giao dịch");
            _cancelItem.Title = L("Lombank_MenuCancel", "Hủy dịch vụ");
        }
        catch (Exception ex)
        {
            Log("RefreshMainMenu failed: " + ex);
        }
    }

    private void OpenMainMenu()
    {
        try
        {
            EnsureMainMenuCreated();
            ProcessLoanTimeEffects(force: true, showNotice: true);
            RefreshMainMenu();

            _mainMenu.Visible = true;
            _menuOpen = true;
            Interval = 0;
            UpdateLemonUiMouseState();
        }
        catch (Exception ex)
        {
            Log("OpenMainMenu failed: " + ex);
        }
    }

    private void CloseMainMenu()
    {
        try
        {
            if (_mainMenu != null)
                _mainMenu.Visible = false;

            _menuOpen = false;
            Interval = _awaitTerminalAction ? 0 : 1000;
            UpdateLemonUiMouseState();
        }
        catch (Exception ex)
        {
            Log("CloseMainMenu failed: " + ex);
        }
    }

    private bool IsPlayerNearTerminal()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists())
                return false;

            return p.Position.DistanceTo(TerminalPosition) <= TerminalRadius;
        }
        catch
        {
            return false;
        }
    }

    private void ShowTerminalHelpText()
    {
        try
        {
            GTA.UI.Screen.ShowHelpTextThisFrame(
                L("Lombank_TerminalHelp",
                "~b~~h~GIAO DỊCH ATM LOMBANK~h~~s~\nBạn muốn rút tiền hay nạp tiền?\n~INPUT_FRONTEND_ACCEPT~ để rút tiền\n~INPUT_SPRINT~ ~INPUT_FRONTEND_ACCEPT~ để nạp tiền"));
        }
        catch { }
    }

    private void FinishTerminalSession(string subtitle = null)
    {
        try
        {
            _awaitTerminalAction = false;
            _terminalMarkerVisible = false;

            if (!string.IsNullOrWhiteSpace(subtitle))
                ShowLombankNotification(L("Lombank_TerminalTitle", "Giao dịch"), subtitle);
        }
        catch (Exception ex)
        {
            Log("FinishTerminalSession failed: " + ex);
        }
    }

    private void EnsureTerminalPropSpawned()
    {
        try
        {
            if (_terminalPropSpawned)
                return;

            if (Game.GameTime - _scriptStartGameTime < AutoSpawnDelayMs)
                return;

            if (DoesTerminalPropExist())
            {
                _terminalPropSpawned = true;
                return;
            }

            int modelHash = Function.Call<int>(Hash.GET_HASH_KEY, "prop_atm_02");
            Model model = new Model(modelHash);
            model.Request(1000);

            if (!model.IsLoaded)
                return;

            int handle = Function.Call<int>(
                Hash.CREATE_OBJECT_NO_OFFSET,
                modelHash,
                TerminalPosition.X,
                TerminalPosition.Y,
                TerminalPosition.Z,
                true,
                true,
                false);

            if (handle != 0)
            {
                Function.Call(Hash.SET_ENTITY_HEADING, handle, TerminalHeading);
                Function.Call(Hash.FREEZE_ENTITY_POSITION, handle, true);
                Function.Call(Hash.SET_ENTITY_INVINCIBLE, handle, true);
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, handle, true, true);

                _terminalPropSpawned = true;
            }

            model.MarkAsNoLongerNeeded();
        }
        catch (Exception ex)
        {
            Log("EnsureTerminalPropSpawned failed: " + ex);
        }
    }

    private bool DoesTerminalPropExist()
    {
        try
        {
            int modelHash = Function.Call<int>(Hash.GET_HASH_KEY, "prop_atm_02");

            int handle = Function.Call<int>(
                Hash.GET_CLOSEST_OBJECT_OF_TYPE,
                TerminalPosition.X,
                TerminalPosition.Y,
                TerminalPosition.Z,
                1.5f,
                modelHash,
                false,
                false,
                false);

            return handle != 0 && Function.Call<bool>(Hash.DOES_ENTITY_EXIST, handle);
        }
        catch
        {
            return false;
        }
    }

    private void BeginWithdrawDialog()
    {
        try
        {
            if (Game.GameTime - _lastInputGameTime < InputCooldownMs)
                return;

            if (!EnsureAtmTransactionsAllowed())
                return;

            ProcessLoanTimeEffects(force: true, showNotice: false);

            long remaining = GetRemainingLimit();
            if (remaining <= 0)
            {
                ShowLombankNotification(
                    L("Lombank_WithdrawFailTitle", "Giao dịch thất bại"),
                    L("Lombank_WithdrawLimitReached", "Hạn mức đã đầy, không thể rút thêm."));
                return;
            }

            string input = Game.GetUserInput(string.Empty);
            if (string.IsNullOrWhiteSpace(input))
                return;

            string digits = new string(input.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(digits))
            {
                ShowLombankNotification(
                    L("Lombank_WithdrawFailTitle", "Giao dịch thất bại"),
                    L("Lombank_WithdrawInvalidAmount", "Số tiền không hợp lệ."));
                return;
            }

            if (!long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
            {
                ShowLombankNotification(
                    L("Lombank_WithdrawFailTitle", "Giao dịch thất bại"),
                    L("Lombank_WithdrawInvalidAmount", "Số tiền không hợp lệ."));
                return;
            }

            if (amount <= 0)
            {
                ShowLombankNotification(
                    L("Lombank_WithdrawFailTitle", "Giao dịch thất bại"),
                    L("Lombank_WithdrawMustBePositive", "Số tiền phải lớn hơn 0."));
                return;
            }

            if (amount > remaining)
            {
                ShowLombankNotification(
                    L("Lombank_WithdrawFailTitle", "Giao dịch thất bại"),
                    string.Format(
                        L("Lombank_WithdrawExceedRemaining", "Bạn chỉ còn có thể rút tối đa {0}."),
                        FormatMoney(remaining)));
                return;
            }

            if (amount > int.MaxValue)
                amount = int.MaxValue;

            bool isNewLoanCycle = (_currentDebt <= 0);
            if (isNewLoanCycle)
            {
                StartNewLoanCycle();
            }

            // NEW: chỉ ghi nhận thời điểm ở lần rút đầu tiên thành công
            if (!_firstWithdrawAt.HasValue && _cycleWithdrawnAmount <= 0)
            {
                _firstWithdrawAt = GetCurrentInGameDateTime();
            }

            Game.Player.Money += (int)amount;

            CityBlackoutHackerState.RecordAtmHackWithdrawalForCurrentCharacter(amount);

            _currentDebt += amount;
            _cycleWithdrawnAmount += amount;

            SaveStateForCurrentCharacter();
            RefreshMainMenu();

            FinishTerminalSession(
                string.Format(
                    L("Lombank_WithdrawSuccess", "Đã rút ~HUD_COLOUR_REDLIGHT~{0}~s~ từ ATM của Lombank."),
                    FormatMoney(amount)));

            _lastInputGameTime = Game.GameTime;
        }
        catch (Exception ex)
        {
            Log("BeginWithdrawDialog failed: " + ex);
        }
    }

    private void BeginRepayDialog()
    {
        try
        {
            if (Game.GameTime - _lastInputGameTime < InputCooldownMs)
                return;

            if (!EnsureAtmTransactionsAllowed())
                return;

            ProcessLoanTimeEffects(force: true, showNotice: false);

            if (_currentDebt <= 0)
            {
                ShowLombankNotification(
                    L("Lombank_RepayFailTitle", "Giao dịch thất bại"),
                    L("Lombank_RepayNoDebt", "Bạn không còn dư nợ nào để nạp vào tài khoản LomBank."));
                return;
            }

            string input = Game.GetUserInput(string.Empty);
            if (string.IsNullOrWhiteSpace(input))
                return;

            string digits = new string(input.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(digits))
            {
                ShowLombankNotification(
                    L("Lombank_RepayFailTitle", "Giao dịch thất bại"),
                    L("Lombank_RepayInvalidAmount", "Số tiền không hợp lệ."));
                return;
            }

            if (!long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
            {
                ShowLombankNotification(
                    L("Lombank_RepayFailTitle", "Giao dịch thất bại"),
                    L("Lombank_RepayInvalidAmount", "Số tiền không hợp lệ."));
                return;
            }

            if (amount <= 0)
            {
                ShowLombankNotification(
                    L("Lombank_RepayFailTitle", "Giao dịch thất bại"),
                    L("Lombank_RepayMustBePositive", "Số tiền phải lớn hơn 0."));
                return;
            }

            if (!_loanOpenedAt.HasValue)
            {
                ShowLombankNotification(
                    L("Lombank_RepayFailTitle", "Giao dịch"),
                    L("Lombank_RepayLocked",
                        "Lom Bank chưa mở khóa kỳ trả nợ. Hãy quay lại đây sau 2 ngày."));
                return;
            }

            DateTime now = GetCurrentInGameDateTime();
            DateTime repayUnlockAt = _loanOpenedAt.Value.Add(LOAN_REPAY_UNLOCK_DELAY);

            if (now < repayUnlockAt)
            {
                ShowLombankNotification(
                    L("Lombank_RepayFailTitle", "Giao dịch"),
                    L("Lombank_RepayLocked",
                        "Lom Bank chưa mở khóa kỳ trả nợ. Hãy quay lại sau 2 ngày."));
                return;
            }

            if (amount > _currentDebt)
                amount = _currentDebt;

            if (amount > Game.Player.Money)
            {
                ShowLombankNotification(
                    L("Lombank_RepayFailTitle", "Giao dịch thất bại"),
                    L("Lombank_RepayNotEnoughMoney", "Bạn không đủ tiền để nạp số tiền này."));
                return;
            }

            if (amount > int.MaxValue)
                amount = int.MaxValue;

            Game.Player.Money -= (int)amount;
            _currentDebt = Math.Max(0, _currentDebt - amount);

            if (_currentDebt <= 0)
            {
                _currentDebt = 0;
                DateTime repayAt = GetCurrentInGameDateTime();
                ApplyOnTimeRepaymentLimitIncrease(repayAt);
                ResetLoanCycleState();
            }

            SaveStateForCurrentCharacter();
            RefreshMainMenu();

            FinishTerminalSession(
                string.Format(
                    L("Lombank_RepaySuccess", "Đã nạp ~HUD_COLOUR_DEGEN_GREEN~{0}~s~ vào tài khoản LomBank."),
                    FormatMoney(amount)));

            _lastInputGameTime = Game.GameTime;
        }
        catch (Exception ex)
        {
            Log("BeginRepayDialog failed: " + ex);
        }
    }

    private void TryClosePhone()
    {
        try
        {
            CustomiFruit.GetCurrentInstance()?.Close(0);
        }
        catch { }
    }

    private void Log(string message)
    {
        try
        {
            // File.AppendAllText("LombankScript.log", DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch { }
    }
}