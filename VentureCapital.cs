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

public class VentureCapital : Script
{
    private static string VentureCapitalContactName => L("VentureCapital_ContactName", "Paradigm Royalty");
    private static string VentureCapitalNotificationTitle => L("VentureCapital_NotificationTitle", "Quỹ đầu tư mạo hiểm");
    private static readonly NotificationIcon VentureCapitalNotificationIcon = NotificationIcon.Epsilon;
    private const string VentureCapitalMessageSoundName = "Text_Arrive_Tone";
    private const string VentureCapitalMessageSoundSet = "Phone_SoundSet_Default";

    private const int ContactDialTimeoutMs = 2000;
    private const int IdleTickIntervalMs = 1000;
    private const int DebtCheckIntervalMs = 40000;
    private const decimal IncomeTaxRate = 0.30m;
    private const decimal IncomeTaxRateStep = 0.002m;
    private const decimal MaxIncomeTaxRate = 0.33m;
    private const int DebtMaturityDays = 7;
    private const int WantedCheckIntervalMs = 30000;
    private const decimal OverdueIncomeTaxRate = 0.66m;
    private const int WantedStarsOnTrigger = 2;
    private const int WantedWindowSpanHours = 2;

    private const int OVERDUE_WANTED_STRIKES_BEFORE_HANDOFF = 4;
    private const decimal PARADIGM_TO_FLEECA_DAILY_RATE = 0.0093m;
    private const decimal PARADIGM_TO_FLEECA_MINOTAUR_DAILY_RATE = 0.0062m;
    private const decimal PARADIGM_TO_FLEECA_MINOTAUR_PRINCIPAL_BONUS = 0.10m;

    private int _overdueWantedStrikeCount = 0;

    private const decimal ManualIncomeTaxRateMinPercent = 31m;
    private const decimal ManualIncomeTaxRateMaxPercentExclusive = 100m;

    private bool _loanOverdue = false;
    private bool _overdueRateNoticeShown = false;
    private int _loanWantedWindowStartHour = -1;
    private int _loanWantedWindowEndHour = -1;
    private int _nextWantedCheckAt = 0;
    private int _lastWantedAppliedDayStamp = -1;
    private int _wantedDayStamp = -1;
    private int _loanTierIndex = 0;

    private bool _manualIncomeTaxRatePending = false;
    private string _manualIncomeTaxRateInput = string.Empty;
    private decimal _selectedIncomeTaxRate = IncomeTaxRate;

    private sealed class LoanTierConfig
    {
        public long MaxAmount { get; }
        public decimal FeeMultiplier { get; }

        public LoanTierConfig(long maxAmount, decimal feeMultiplier)
        {
            MaxAmount = maxAmount;
            FeeMultiplier = feeMultiplier;
        }
    }

    private static readonly LoanTierConfig[] LoanTiers = new[]
    {
        new LoanTierConfig(2_000_000L, 1.51m),
        new LoanTierConfig(8_000_000L, 1.48m),
        new LoanTierConfig(14_000_000L, 1.45m),
        new LoanTierConfig(20_000_000L, 1.42m),
        new LoanTierConfig(26_000_000L, 1.39m),
        new LoanTierConfig(32_000_000L, 1.36m),
        new LoanTierConfig(38_000_000L, 1.33m),
        new LoanTierConfig(45_000_000L, 1.30m),
    };

    private readonly ObjectPool _uiPool = new ObjectPool();
    private NativeMenu _menu;
    private NativeItem _customerItem;
    private NativeItem _organizationItem;
    private NativeItem _statusItem;
    private NativeItem _amountItem;
    private NativeItem _repaymentItem;
    private NativeItem _confirmItem;
    private NativeItem _cancelItem;
    private bool _menuBuilt = false;
    private CustomiFruit _phoneInstance = null;
    private iFruitContact _contact = null;
    private bool _contactAnsweredBound = false;
    private bool _consentPending = false;
    private bool _loanActive = false;
    private long _loanPrincipal = 0L;
    private long _loanRemainingDebt = 0L;
    private long _selectedLoanAmount = 0L;
    private long _selectedRepaymentAmount = 0L;
    private decimal _currentIncomeTaxRate = IncomeTaxRate;
    private long _lastTrackedMoney = 0L;
    private int _nextDebtCheckAt = 0;
    private int _nextMaturityCheckAt = 0;
    private int _activeCharacterHash = 0;
    private bool _stateLoaded = false;
    private int _loanDueDayStamp = -1;

    private readonly string _stateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods",
        "Venture Capital"
    );

    private string GetStateFilePath(int characterHash)
    {
        if (characterHash == 0) characterHash = GetCurrentCharacterHash();
        return Path.Combine(
            _stateRoot,
            string.Format(
                CultureInfo.InvariantCulture,
                "venture_capital_state_{0:X8}.dat",
                characterHash
            )
        );
    }

    public VentureCapital()
    {
        Tick += OnTick;
        KeyDown += OnKeyDown;
        Aborted += OnAborted;
        Interval = IdleTickIntervalMs;
    }

    private static string L(string key, string fallback = "")
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

    private static string FormatMoney(long value)
    {
        if (value < 0) value = 0;
        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private static long CalculateRepaymentAmount(long loanAmount, decimal feeMultiplier)
    {
        if (loanAmount <= 0) return 0L;
        decimal repayment = Math.Round(loanAmount * feeMultiplier, 0, MidpointRounding.AwayFromZero);
        if (repayment < 0m) return 0L;
        if (repayment > (decimal)long.MaxValue) return long.MaxValue;
        return (long)repayment;
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
        catch
        {
            return new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        }
    }

    private static int GetDateStamp(DateTime dt)
    {
        return (dt.Year * 10000) + (dt.Month * 100) + dt.Day;
    }

    private static string GetWeekday(int dayOfWeek)
    {
        switch (dayOfWeek)
        {
            case 1: return L("VentureCapital_Weekday_Monday", "Thứ 2");
            case 2: return L("VentureCapital_Weekday_Tuesday", "Thứ 3");
            case 3: return L("VentureCapital_Weekday_Wednesday", "Thứ 4");
            case 4: return L("VentureCapital_Weekday_Thursday", "Thứ 5");
            case 5: return L("VentureCapital_Weekday_Friday", "Thứ 6");
            case 6: return L("VentureCapital_Weekday_Saturday", "Thứ 7");
            default: return L("VentureCapital_Weekday_Sunday", "Chủ nhật");
        }
    }

    private static string FormatDateLine(int dayOfWeek, int year, int month, int day)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}, {1:0000}-{2:00}-{3:00}",
            GetWeekday(dayOfWeek),
            year,
            month,
            day
        );
    }

    private static int GetCurrentInGameDayOfWeek()
    {
        try
        {
            int dayOfWeek = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_WEEK);
            if (dayOfWeek < 0 || dayOfWeek > 6) return 0;
            return dayOfWeek;
        }
        catch
        {
            return 0;
        }
    }

    private void AddWantedStars(int starsToAdd)
    {
        try
        {
            int current = Math.Max(0, Game.Player.WantedLevel);
            int target = Math.Min(5, current + Math.Max(0, starsToAdd));
            Game.Player.WantedLevel = target;
        }
        catch { }
    }

    private void ShowVentureCapitalNotification(string message)
    {
        try
        {
            PlayFrontendSound(VentureCapitalMessageSoundName, VentureCapitalMessageSoundSet);

            Notification.Show(
                VentureCapitalNotificationIcon,
                VentureCapitalContactName,
                VentureCapitalNotificationTitle,
                message
            );
        }
        catch
        {
            try
            {
                GTA.UI.Screen.ShowSubtitle(message, 3000);
            }
            catch { }
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

    private static long SafeAddLong(long a, long b)
    {
        try
        {
            if (b > 0 && a > long.MaxValue - b) return long.MaxValue;
            if (b < 0 && a < long.MinValue - b) return long.MinValue;
            return a + b;
        }
        catch
        {
            return a;
        }
    }

    private static int ClampToInt(long value)
    {
        if (value < 0) return 0;
        if (value > int.MaxValue) return int.MaxValue;
        return (int)value;
    }

    private static int GetMaxTierIndex()
    {
        return LoanTiers.Length > 0 ? LoanTiers.Length - 1 : 0;
    }

    private int GetCurrentTierIndex()
    {
        if (_loanTierIndex < 0) return 0;
        int maxIndex = GetMaxTierIndex();
        if (_loanTierIndex > maxIndex) return maxIndex;
        return _loanTierIndex;
    }

    private LoanTierConfig GetCurrentTier()
    {
        return LoanTiers[GetCurrentTierIndex()];
    }

    private static int InferTierIndexFromLoanAmount(long principal, long selectedLoanAmount)
    {
        long probe = Math.Max(principal, selectedLoanAmount);
        if (probe <= 0 || LoanTiers.Length == 0) return 0;

        for (int i = 0; i < LoanTiers.Length; i++)
        {
            if (probe <= LoanTiers[i].MaxAmount) return i;
        }

        return GetMaxTierIndex();
    }

    private static string FormatFeeMultiplier(decimal value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static bool TryParseManualIncomeTaxPercent(string raw, out decimal percent)
    {
        percent = 0m;

        try
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string normalized = raw.Trim().Replace(',', '.');

            if (normalized.StartsWith(".", StringComparison.Ordinal))
                normalized = "0" + normalized;

            if (normalized.EndsWith(".", StringComparison.Ordinal))
                normalized = normalized.Substring(0, normalized.Length - 1);

            if (!decimal.TryParse(
                normalized,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out percent))
            {
                return false;
            }

            return percent >= ManualIncomeTaxRateMinPercent &&
                   percent < ManualIncomeTaxRateMaxPercentExclusive;
        }
        catch
        {
            percent = 0m;
            return false;
        }
    }

    private static string GetManualIncomeTaxInputDisplay(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "[]";
        return "[" + input.Trim().Replace(',', '.') + "]";
    }

    private void BeginManualIncomeTaxEntry()
    {
        _consentPending = false;
        _manualIncomeTaxRatePending = true;
        _manualIncomeTaxRateInput = string.Empty;
    }

    private void ReturnToInitialConsentPrompt()
    {
        _manualIncomeTaxRatePending = false;
        _manualIncomeTaxRateInput = string.Empty;
        _consentPending = true;
        _selectedIncomeTaxRate = IncomeTaxRate;
    }

    private void CancelConsent()
    {
        _consentPending = false;
        _manualIncomeTaxRatePending = false;
        _manualIncomeTaxRateInput = string.Empty;
        _selectedIncomeTaxRate = IncomeTaxRate;
        UpdateContactAvailability();
    }

    private void AppendManualIncomeTaxKey(Keys key)
    {
        try
        {
            if (key >= Keys.D0 && key <= Keys.D9)
            {
                _manualIncomeTaxRateInput += ((char)('0' + (key - Keys.D0)));
                return;
            }

            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            {
                _manualIncomeTaxRateInput += ((char)('0' + (key - Keys.NumPad0)));
                return;
            }

            if (key == Keys.Decimal || key == Keys.OemPeriod || key == Keys.Oemcomma || key == Keys.Separator)
            {
                if (_manualIncomeTaxRateInput.Contains(".") || _manualIncomeTaxRateInput.Contains(","))
                    return;

                if (string.IsNullOrWhiteSpace(_manualIncomeTaxRateInput))
                    _manualIncomeTaxRateInput = "0.";
                else
                    _manualIncomeTaxRateInput += ".";
            }
        }
        catch
        {
        }
    }

    private bool TryCommitManualIncomeTaxRate()
    {
        try
        {
            if (!TryParseManualIncomeTaxPercent(_manualIncomeTaxRateInput, out decimal percent))
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("VentureCapital_RateRange",
                    "Mức tỷ lệ trích thu phải từ ~HUD_COLOUR_DEGEN_YELLOW~31~s~ đến ~HUD_COLOUR_DEGEN_CYAN~100~s~"),
                    3000);
                return false;
            }

            decimal rate = percent / 100m;
            _selectedIncomeTaxRate = rate;
            _currentIncomeTaxRate = rate;

            ShowVentureCapitalNotification(
                string.Format(
                    CultureInfo.InvariantCulture,
                    L("VentureCapital_ManualRateAccepted",
                    "Tổ chức Paradigm Royalty sẽ áp dụng theo ~HUD_COLOUR_DEGEN_CYAN~{0}%~s~ trích doanh thu do chính bạn cam kết, tổ chức sẽ không rút lại ~HUD_COLOUR_DEGEN_YELLOW~tỷ lệ %~s~ này vì đây là quyết định của bạn, không phải sự ép buộc của chúng tôi!"),
                    _manualIncomeTaxRateInput.Trim().Replace(',', '.')
                )
            );

            _manualIncomeTaxRatePending = false;
            _manualIncomeTaxRateInput = string.Empty;
            OpenMainMenu();
            return true;
        }
        catch
        {
            GTA.UI.Screen.ShowSubtitle(L("VentureCapital_RateRangeShort", "Mức % phải từ 31 đến dưới 100."), 3000);
            return false;
        }
    }

    private static int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p != null && p.Exists()) return p.Model.Hash;
        }
        catch
        {
        }
        return 0;
    }

    private static string GetCharacterNameByHash(int hash)
    {
        if (hash == -1692214353) return "Franklin Clinton";
        if (hash == 225514697) return "Michael De Santa";
        if (hash == -1686040670) return "Trevor Philips";
        return "Unknown";
    }

    private bool IsMenuVisible()
    {
        return _menu != null && _menu.Visible;
    }

    private bool IsContactAvailable()
    {
        return !_consentPending &&
               !_manualIncomeTaxRatePending &&
               !IsMenuVisible() &&
               !_loanActive &&
               ParadigmRoyaltyTransferBridge.IsParadigmRoyaltyContactAvailableForCurrentCharacter();
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive) return;

            SyncStateForCurrentCharacter();
            SyncParadigmRoyaltyHandoffState();
            EnsureContactRegistered();
            UpdateContactAvailability();

            if (_consentPending)
            {
                GTA.UI.Screen.ShowHelpTextThisFrame(
                    L("VentureCapital_ConsentPrompt",
                    "Tổ chức cấp cho lượng tiền ngay và ~y~tự động~s~ trích % thu nhập hoặc ~y~tự thỏa thuận~s~ % tùy thích?\n~INPUT_FRONTEND_ACCEPT~ để đồng ý\n~INPUT_FRONTEND_LEFT~ để nhập %")
                );
            }
            else if (_manualIncomeTaxRatePending)
            {
                GTA.UI.Screen.ShowHelpTextThisFrame(
                    L("VentureCapital_ManualRatePrompt",
                    "Tổ chức đang muốn ~g~an toàn~s~ cho bạn mà lại không chịu? Vậy bạn muốn ~y~trích %~s~ là ") +
                    GetManualIncomeTaxInputDisplay(_manualIncomeTaxRateInput) +
                    L("VentureCapital_ManualRatePromptTail", "\n~INPUT_FRONTEND_ACCEPT~ để đồng ý\n~INPUT_FRONTEND_LEFT~ để hủy")
                );
            }

            if (IsMenuVisible())
            {
                RefreshMenuText();
                UpdateLemonUiMouseState();
                _uiPool.Process();
                Interval = 0;
                return;
            }

            if (_loanActive)
            {
                ProcessLoanMaturity();
                ProcessIncomeTax();
                ProcessWantedEscalation();
            }

            Interval = (_consentPending || _manualIncomeTaxRatePending ? 0 : IdleTickIntervalMs);
        }
        catch
        {
            Interval = IdleTickIntervalMs;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive) return;

            if (IsMenuVisible())
            {
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                {
                    CloseMenu();
                    return;
                }
            }

            if (_manualIncomeTaxRatePending)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
                {
                    TryCommitManualIncomeTaxRate();
                    return;
                }

                if (e.KeyCode == Keys.Back)
                {
                    if (!string.IsNullOrEmpty(_manualIncomeTaxRateInput))
                    {
                        _manualIncomeTaxRateInput = _manualIncomeTaxRateInput.Substring(0, _manualIncomeTaxRateInput.Length - 1);
                    }
                    return;
                }

                if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Escape)
                {
                    ReturnToInitialConsentPrompt();
                    return;
                }

                AppendManualIncomeTaxKey(e.KeyCode);
                return;
            }

            if (_consentPending)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
                {
                    _selectedIncomeTaxRate = IncomeTaxRate;
                    _consentPending = false;
                    _manualIncomeTaxRatePending = false;
                    _manualIncomeTaxRateInput = string.Empty;
                    OpenMainMenu();
                    return;
                }

                if (e.KeyCode == Keys.Back)
                {
                    BeginManualIncomeTaxEntry();
                    return;
                }

                if (e.KeyCode == Keys.Escape)
                {
                    CancelConsent();
                    return;
                }
            }
        }
        catch
        {
        }
    }

    private void OnAborted(object sender, EventArgs e)
    {
        try
        {
            SaveStateForCurrentCharacter();
        }
        catch
        {
        }
    }

    private void EnsureContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null) return;

            if (!ReferenceEquals(_phoneInstance, phone))
            {
                _phoneInstance = phone;
                _contact = null;
                _contactAnsweredBound = false;
            }

            if (_contact == null)
            {
                _contact = phone.Contacts.FirstOrDefault(c =>
                    c != null &&
                    string.Equals(c.Name, VentureCapitalContactName, StringComparison.OrdinalIgnoreCase));
            }

            if (_contact == null)
            {
                _contact = new iFruitContact(VentureCapitalContactName)
                {
                    Active = IsContactAvailable(),
                    DialTimeout = ContactDialTimeoutMs,
                    Bold = false,
                    Icon = ContactIcon.Epsilon
                };
                _contact.Answered += OnContactAnswered;
                _contactAnsweredBound = true;
                phone.Contacts.Add(_contact);
            }
            else
            {
                _contact.Active = IsContactAvailable();
                try
                {
                    _contact.DialTimeout = ContactDialTimeoutMs;
                    _contact.Bold = false;
                    _contact.Icon = ContactIcon.Epsilon;
                }
                catch
                {
                }

                if (!_contactAnsweredBound)
                {
                    _contact.Answered += OnContactAnswered;
                    _contactAnsweredBound = true;
                }
            }
        }
        catch
        {
        }
    }

    private void UpdateContactAvailability()
    {
        try
        {
            if (_contact != null) _contact.Active = IsContactAvailable();
        }
        catch
        {
        }
    }

    private void OnContactAnswered(iFruitContact sender)
    {
        try
        {
            if (!IsContactAvailable()) return;

            _selectedIncomeTaxRate = IncomeTaxRate;
            _manualIncomeTaxRateInput = string.Empty;
            _manualIncomeTaxRatePending = false;
            _consentPending = true;
        }
        finally
        {
            TryClosePhone();
        }
    }

    private void TryClosePhone()
    {
        try
        {
            CustomiFruit.GetCurrentInstance()?.Close(0);
        }
        catch
        {
        }
    }

    private void OpenMainMenu()
    {
        try
        {
            if (_loanActive) return;

            _consentPending = false;
            _manualIncomeTaxRatePending = false;
            _manualIncomeTaxRateInput = string.Empty;

            EnsureMenuCreated();
            RefreshMenuText();
            _menu.Visible = true;
            UpdateContactAvailability();
        }
        catch
        {
        }
    }

    private void EnsureMenuCreated()
    {
        try
        {
            if (_menuBuilt) return;

            _menu = new NativeMenu(
                L("VentureCapital_MenuTitle", "Venture Capital"),
                L("VentureCapital_MenuSubtitle", "CHI TIẾT QUỸ ĐẦU TƯ MẠO HIỂM"));
            try
            {
                _menu.MouseBehavior = MenuMouseBehavior.Disabled;
                _menu.ResetCursorWhenOpened = false;
                _menu.CloseOnInvalidClick = false;
                _menu.RotateCamera = true;
            }
            catch
            {
            }

            _customerItem = new NativeItem("", "");
            _organizationItem = new NativeItem("", "");
            _statusItem = new NativeItem("", "");
            _amountItem = new NativeItem("", "");
            _repaymentItem = new NativeItem("", "");
            _confirmItem = new NativeItem("", "");
            _cancelItem = new NativeItem("", "");

            _amountItem.Activated += (s, e) => PromptForLoanAmount();
            _confirmItem.Activated += (s, e) => ConfirmLoan();
            _cancelItem.Activated += (s, e) => CancelMenu();

            _menu.Add(_customerItem);
            _menu.Add(_organizationItem);
            _menu.Add(_statusItem);
            _menu.Add(_amountItem);
            _menu.Add(_repaymentItem);
            _menu.Add(_confirmItem);
            _menu.Add(_cancelItem);

            _uiPool.Add(_menu);
            _menuBuilt = true;
        }
        catch
        {
        }
    }

    private void RefreshMenuText()
    {
        try
        {
            if (_customerItem != null)
            {
                _customerItem.Title = string.Format(
                    CultureInfo.InvariantCulture,
                    L("VentureCapital_MenuCustomer", "1. Tên khách hàng: {0}"),
                    GetCharacterNameByHash(GetCurrentCharacterHash())
                );
            }

            if (_organizationItem != null)
            {
                _organizationItem.Title = L("VentureCapital_MenuOrg", "2. Tên tổ chức: Paradigm Royalty");
            }

            if (_statusItem != null)
            {
                LoanTierConfig tier = GetCurrentTier();
                _statusItem.Title = string.Format(
                    CultureInfo.InvariantCulture,
                    L("VentureCapital_MenuStatus", "3. Tình trạng: Thanh khoản ~HUD_COLOUR_DEGEN_CYAN~{0}~s~"),
                    FormatMoney(tier.MaxAmount),
                    FormatFeeMultiplier(tier.FeeMultiplier)
                );
            }

            if (_amountItem != null)
            {
                if (_selectedLoanAmount > 0)
                {
                    _amountItem.Title = string.Format(
                        CultureInfo.InvariantCulture,
                        L("VentureCapital_MenuLoanAmount", "4. Số tiền muốn vay: {0}"),
                        FormatMoney(_selectedLoanAmount)
                    );
                }
                else
                {
                    _amountItem.Title = L("VentureCapital_MenuLoanAmountNA", "4. Số tiền muốn vay: N/A");
                }
            }

            if (_repaymentItem != null)
            {
                if (_selectedRepaymentAmount > 0)
                {
                    _repaymentItem.Title = string.Format(
                        CultureInfo.InvariantCulture,
                        L("VentureCapital_MenuRepayment", "5. Số tiền cần trả: {0}"),
                        FormatMoney(_selectedRepaymentAmount)
                    );
                }
                else
                {
                    _repaymentItem.Title = L("VentureCapital_MenuRepaymentNA", "5. Số tiền cần trả: N/A");
                }
            }

            if (_confirmItem != null) _confirmItem.Title = L("VentureCapital_ConfirmLoan", "Xác nhận vay tiền");
            if (_cancelItem != null) _cancelItem.Title = L("VentureCapital_CancelTransaction", "Hủy bỏ giao dịch");
        }
        catch
        {
        }
    }

    private void PromptForLoanAmount()
    {
        try
        {
            if (_loanActive) return;

            LoanTierConfig tier = GetCurrentTier();
            string input = Game.GetUserInput(string.Empty);
            if (string.IsNullOrWhiteSpace(input)) return;

            StringBuilder sb = new StringBuilder();
            foreach (char ch in input)
            {
                if (char.IsDigit(ch)) sb.Append(ch);
            }

            string digitsOnly = sb.ToString();
            if (string.IsNullOrWhiteSpace(digitsOnly) ||
                !long.TryParse(digitsOnly, NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
            {
                ShowVentureCapitalNotification(L("VentureCapital_InvalidAmount", "Số tiền không hợp lệ."));
                return;
            }

            if (amount <= 0)
            {
                ShowVentureCapitalNotification(L("VentureCapital_AmountMustBePositive", "Số tiền phải lớn hơn 0."));
                return;
            }

            if (amount > tier.MaxAmount)
            {
                GTA.UI.Screen.ShowSubtitle(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        L("VentureCapital_LoanTooHigh", "Số tiền vay ~r~không được vượt quá~s~ {0}."),
                        FormatMoney(tier.MaxAmount)
                    ),
                    3000
                );
                return;
            }

            _selectedLoanAmount = amount;
            _selectedRepaymentAmount = CalculateRepaymentAmount(amount, tier.FeeMultiplier);
            RefreshMenuText();
        }
        catch
        {
        }
    }

    private void ConfirmLoan()
    {
        try
        {
            if (_loanActive) return;

            if (_selectedLoanAmount <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(L("VentureCapital_NoLoanAmount", "Bạn ~y~chưa nhập~s~ số tiền vay."), 2500);
                return;
            }

            LoanTierConfig tier = GetCurrentTier();
            if (_selectedLoanAmount > tier.MaxAmount)
            {
                GTA.UI.Screen.ShowSubtitle(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        L("VentureCapital_LoanTooHigh", "Số tiền vay ~r~không được vượt quá~s~ {0}."),
                        FormatMoney(tier.MaxAmount)
                    ),
                    3000
                );
                return;
            }

            long currentMoney = Math.Max(0, Game.Player.Money);
            long newMoney = SafeAddLong(currentMoney, _selectedLoanAmount);
            Game.Player.Money = ClampToInt(newMoney);

            _loanActive = true;
            _loanOverdue = false;
            _overdueRateNoticeShown = false;
            _loanPrincipal = _selectedLoanAmount;
            _loanTierIndex = GetCurrentTierIndex();
            _loanRemainingDebt = (_selectedRepaymentAmount > 0L)
                ? _selectedRepaymentAmount
                : CalculateRepaymentAmount(_selectedLoanAmount, tier.FeeMultiplier);

            _currentIncomeTaxRate = (_selectedIncomeTaxRate > 0m)
                ? _selectedIncomeTaxRate
                : IncomeTaxRate;

            _lastTrackedMoney = Math.Max(0, Game.Player.Money);
            _nextDebtCheckAt = Game.GameTime + DebtCheckIntervalMs;
            _nextMaturityCheckAt = Game.GameTime + DebtCheckIntervalMs;

            DateTime now = GetCurrentInGameDateTime();
            DateTime due = now.Date.AddDays(DebtMaturityDays);
            int nowWeekday = GetCurrentInGameDayOfWeek();
            int dueWeekday = (nowWeekday + DebtMaturityDays) % 7;
            _loanDueDayStamp = GetDateStamp(due);

            _loanWantedWindowStartHour = now.Hour;
            _loanWantedWindowEndHour = (_loanWantedWindowStartHour + WantedWindowSpanHours) % 24;
            _nextWantedCheckAt = Game.GameTime + WantedCheckIntervalMs;
            _lastWantedAppliedDayStamp = -1;
            _wantedDayStamp = GetDateStamp(now);

            SaveStateForCurrentCharacter();
            CloseMenu();

            ShowVentureCapitalNotification(
                string.Format(
                    CultureInfo.InvariantCulture,
                    L("VentureCapital_DueNotice",
                    "Hiện tại là ~HUD_COLOUR_DEGEN_GREEN~{0}~s~, bạn cần trả hết số tiền này cho tổ chức vào ~HUD_COLOUR_DEGEN_YELLOW~{1}~s~. Nếu đến ngày đáo hạn mà không trả hết, tổ chức sẽ có biện pháp mạnh để giải quyết vấn đề này!"),
                    FormatDateLine(nowWeekday, now.Year, now.Month, now.Day),
                    FormatDateLine(dueWeekday, due.Year, due.Month, due.Day)
                )
            );
        }
        catch
        {
        }
    }

    private void CancelMenu()
    {
        CloseMenu();
    }

    private void CloseMenu()
    {
        try
        {
            if (_menu != null) _menu.Visible = false;
        }
        catch { }
        UpdateContactAvailability();
    }

    private void ProcessIncomeTax()
    {
        try
        {
            if (!_loanActive || _loanRemainingDebt <= 0) return;
            if (Game.GameTime < _nextDebtCheckAt) return;

            _nextDebtCheckAt = Game.GameTime + DebtCheckIntervalMs;
            long currentMoney = Math.Max(0, Game.Player.Money);

            if (currentMoney > _lastTrackedMoney)
            {
                long incomeDelta = currentMoney - _lastTrackedMoney;
                decimal taxRate = _loanOverdue ? OverdueIncomeTaxRate : _currentIncomeTaxRate;
                long tax = (long)Math.Floor((decimal)incomeDelta * taxRate);

                if (tax < 0) tax = 0;

                if (tax > 0)
                {
                    long deducted = Math.Min(tax, _loanRemainingDebt);
                    long postTaxMoney = Math.Max(0, currentMoney - deducted);
                    Game.Player.Money = ClampToInt(postTaxMoney);
                    _loanRemainingDebt -= deducted;
                    _lastTrackedMoney = Math.Max(0, Game.Player.Money);

                    ShowVentureCapitalNotification(
                        L("VentureCapital_UpgradeNotice_Deduction",
                        "Chúng tôi thấy tài khoản của bạn vừa có ~HUD_COLOUR_DEGEN_YELLOW~sự biến động~s~, theo thỏa thuận, chúng tôi sẽ trích một phần thu nhập vừa rồi của bạn vào Quỹ của tổ chức. Cảm ơn bạn!")
                    );

                    if (_loanRemainingDebt <= 0)
                    {
                        FinalizeLoanRepayment();
                        return;
                    }

                    SaveStateForCurrentCharacter();
                    return;
                }

                _lastTrackedMoney = currentMoney;
            }
            else if (currentMoney < _lastTrackedMoney)
            {
                if (!_loanOverdue && _currentIncomeTaxRate < MaxIncomeTaxRate)
                {
                    _currentIncomeTaxRate = Math.Min(MaxIncomeTaxRate, _currentIncomeTaxRate + IncomeTaxRateStep);
                    SaveStateForCurrentCharacter();
                }
                _lastTrackedMoney = currentMoney;
            }
        }
        catch
        {
        }
    }

    private void FinalizeLoanRepayment()
    {
        try
        {
            int tierIndexBeforeClear = GetCurrentTierIndex();
            bool shouldUpgrade =
                !_loanOverdue &&
                tierIndexBeforeClear < GetMaxTierIndex() &&
                _loanPrincipal >= LoanTiers[tierIndexBeforeClear].MaxAmount;

            if (shouldUpgrade) _loanTierIndex = tierIndexBeforeClear + 1;

            ClearLoanState();

            if (shouldUpgrade)
            {
                LoanTierConfig nextTier = GetCurrentTier();
                ShowVentureCapitalNotification(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        L("VentureCapital_UpgradeNotice_LimitIncrease",
                        "Khoản cấp vốn trước đó đã được thanh toán thành công. Hạn mức của quý khách đã được Paradigm Royalty nâng lên {0}."),
                        FormatMoney(nextTier.MaxAmount),
                        FormatFeeMultiplier(nextTier.FeeMultiplier)
                    )
                );
            }
            else
            {
                ShowVentureCapitalNotification(L("VentureCapital_PaidOff", "Khoản tiền của Quỹ Đầu Tư Mạo Hiểm đã được thanh toán đầy đủ."));
            }
        }
        catch
        {
            ClearLoanState();
            ShowVentureCapitalNotification(L("VentureCapital_PaidOff", "Khoản tiền của Quỹ Đầu Tư Mạo Hiểm đã được thanh toán đầy đủ."));
        }
    }

    private void ProcessLoanMaturity()
    {
        try
        {
            if (!_loanActive || _loanRemainingDebt <= 0 || _loanDueDayStamp <= 0) return;
            if (Game.GameTime < _nextMaturityCheckAt) return;

            // tách riêng timer đáo hạn khỏi timer trừ thu nhập
            _nextMaturityCheckAt = Game.GameTime + DebtCheckIntervalMs;

            DateTime now = GetCurrentInGameDateTime();
            if (GetDateStamp(now) < _loanDueDayStamp) return;

            if (!_loanOverdue)
            {
                _loanOverdue = true;
                _overdueRateNoticeShown = false;
            }

            if (!_overdueRateNoticeShown)
            {
                _overdueRateNoticeShown = true;
                ShowVentureCapitalNotification(
                    L("VentureCapital_OverdueNotice",
                    "Tổ chức sẽ đẩy nhanh tốc độ thu hồi của mình để thu hồi vốn cấp tốc, yêu cầu bạn phải trả nhanh và trả đủ. Xin cảm ơn!")
                );
                SaveStateForCurrentCharacter();
            }
        }
        catch
        {
        }
    }

    private void ProcessWantedEscalation()
    {
        try
        {
            if (!_loanActive || !_loanOverdue || _loanRemainingDebt <= 0 || _loanDueDayStamp <= 0)
                return;

            if (Game.GameTime < _nextWantedCheckAt)
                return;

            _nextWantedCheckAt = Game.GameTime + WantedCheckIntervalMs;

            DateTime now = GetCurrentInGameDateTime();
            int todayStamp = GetDateStamp(now);

            if (_wantedDayStamp <= 0)
            {
                _wantedDayStamp = todayStamp;
            }
            else if (todayStamp != _wantedDayStamp)
            {
                // Qua 00:00 -> ngày mới, mở lại cờ truy nã trong ngày
                _wantedDayStamp = todayStamp;
                _lastWantedAppliedDayStamp = -1;
                SaveStateForCurrentCharacter();
            }

            if (todayStamp < _loanDueDayStamp)
                return;

            if (!IsWithinWantedWindow(now))
                return;

            if (_lastWantedAppliedDayStamp == todayStamp)
                return;

            _lastWantedAppliedDayStamp = todayStamp;
            _overdueWantedStrikeCount = Math.Min(OVERDUE_WANTED_STRIKES_BEFORE_HANDOFF, _overdueWantedStrikeCount + 1);

            AddWantedStars(WantedStarsOnTrigger);

            if (_overdueWantedStrikeCount >= OVERDUE_WANTED_STRIKES_BEFORE_HANDOFF)
            {
                TransferParadigmDebtToFleeca();
                return;
            }

            SaveStateForCurrentCharacter();
        }
        catch
        {
        }
    }

    private void SyncParadigmRoyaltyHandoffState()
    {
        try
        {
            ParadigmRoyaltyTransferBridge.IsParadigmRoyaltyContactAvailableForCurrentCharacter();
        }
        catch
        {
        }
    }

    private void TransferParadigmDebtToFleeca()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0 || _loanRemainingDebt <= 0)
                return;

            long sourceDebt = Math.Max(0L, _loanRemainingDebt);

            bool fleecaHasActiveLoan =
                TryReadFleecaLoanState(ownerHash, out FleecaLoanSnapshot fleecaState) &&
                fleecaState != null &&
                fleecaState.Active &&
                fleecaState.RemainingDebt > 0;

            bool minotaurBranchUsed = false;
            decimal specialFleecaRate = PARADIGM_TO_FLEECA_DAILY_RATE;
            long effectiveDebtToWrite = sourceDebt;

            if (!fleecaHasActiveLoan)
            {
                try
                {
                    minotaurBranchUsed = MinotaurBankBridge.TryGetOverrideDebtAndRate(out _, out _);
                }
                catch
                {
                    minotaurBranchUsed = false;
                }

                if (minotaurBranchUsed)
                {
                    effectiveDebtToWrite = (long)Math.Ceiling(sourceDebt * (1m + PARADIGM_TO_FLEECA_MINOTAUR_PRINCIPAL_BONUS));
                    specialFleecaRate = PARADIGM_TO_FLEECA_MINOTAUR_DAILY_RATE;
                }
                else
                {
                    specialFleecaRate = PARADIGM_TO_FLEECA_DAILY_RATE;
                }
            }

            if (fleecaHasActiveLoan)
            {
                long mergedRemaining = SafeAddLong(fleecaState.RemainingDebt, sourceDebt);
                long mergedPrincipal = SafeAddLong(fleecaState.Principal, sourceDebt);

                // Hấp thụ khoản chuyển vào nợ gốc Fleeca:
                // - bỏ cờ "nợ chuyển"
                // - giữ logic Fleeca gốc (nợ đang có sẵn sẽ tiếp tục theo rate hiện tại của nó)
                decimal nativeRate;
                if (fleecaState.TransferredFromParadigmRoyalty)
                {
                    nativeRate = 0.04m; // fallback nợ gốc Fleeca không có cọc
                }
                else if (fleecaState.RemainingDebt > 0 && fleecaState.DailyDue > 0)
                {
                    nativeRate = (decimal)fleecaState.DailyDue / (decimal)fleecaState.RemainingDebt;
                }
                else
                {
                    nativeRate = 0.04m;
                }

                fleecaState.Principal = mergedPrincipal;
                fleecaState.RemainingDebt = mergedRemaining;
                fleecaState.DailyDue = Math.Max(1L, (long)Math.Ceiling((decimal)mergedRemaining * nativeRate));
                fleecaState.Active = true;

                fleecaState.TransferredFromParadigmRoyalty = false;
                fleecaState.TransferredDailyRate = -1m;

                WriteFleecaLoanState(ownerHash, fleecaState);

                // Giữ trạng thái handoff để contact Paradigm Royalty vẫn bị khóa
                // cho đến khi khoản nợ Fleeca này được thanh toán hết.
                ParadigmRoyaltyTransferBridge.MarkTransferForCurrentCharacter(
                    mergedRemaining,
                    nativeRate,
                    false);
            }
            else
            {
                DateTime now = GetCurrentInGameDateTime();
                int dayStamp = GetDateStamp(now);

                var newFleecaState = new FleecaLoanSnapshot
                {
                    Principal = effectiveDebtToWrite,
                    RemainingDebt = effectiveDebtToWrite,
                    DailyDue = Math.Max(1L, (long)Math.Ceiling((decimal)effectiveDebtToWrite * specialFleecaRate)),
                    Active = true,
                    LastChargedDayStamp = dayStamp,
                    DueWindowStartHour = now.Hour,
                    DueWindowEndHour = (now.Hour + 2) % 24,
                    CollateralMode = false,
                    LoanStartDayStamp = dayStamp,
                    QuickPayRateFirst7 = 0.0731m,
                    QuickPayRateNext10 = 0.0370m,
                    QuickPayRateAfter17 = 0.0167m,
                    Version = 3,
                    TransferredFromParadigmRoyalty = true,
                    TransferredDailyRate = specialFleecaRate
                };

                WriteFleecaLoanState(ownerHash, newFleecaState);

                ParadigmRoyaltyTransferBridge.MarkTransferForCurrentCharacter(
                    effectiveDebtToWrite,
                    specialFleecaRate,
                    minotaurBranchUsed);
            }

            ClearLoanState(clearHandoff: false);

            ShowVentureCapitalNotification(
                string.Format(
                    CultureInfo.InvariantCulture,
                    L("VentureCapital_TransferredToFleeca",
                    "Khoản nợ của tổ chức Paradigm Royalty quá hạn ~HUD_COLOUR_DEGEN_YELLOW~đã chuyển sang~s~ cho ~HUD_COLOUR_DEGEN_GREEN~ngân hàng Fleeca quản lý~s~ với số tiền ~HUD_COLOUR_DEGEN_CYAN~{0}~s~."),
                    FormatMoney(minotaurBranchUsed && !fleecaHasActiveLoan ? effectiveDebtToWrite : sourceDebt)));
        }
        catch
        {
            // Thực hiện handle exception hoặc log lỗi tại đây nếu cần thiết
        }
    }

    private sealed class FleecaLoanSnapshot
    {
        public long Principal;
        public long RemainingDebt;
        public long DailyDue;
        public bool Active;
        public int LastChargedDayStamp;
        public int DueWindowStartHour;
        public int DueWindowEndHour;
        public bool CollateralMode;
        public int LoanStartDayStamp;
        public decimal QuickPayRateFirst7;
        public decimal QuickPayRateNext10;
        public decimal QuickPayRateAfter17;
        public int Version;

        public bool TransferredFromParadigmRoyalty;
        public decimal TransferredDailyRate;
    }

    private static string GetFleecaLoanStatePath(int ownerHash)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GTA V Mods", "Fleeca Bank Loan");
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"loan_state_{ownerHash}.dat");
    }

    private static bool TryReadFleecaLoanState(int ownerHash, out FleecaLoanSnapshot state)
    {
        state = null;

        try
        {
            string path = GetFleecaLoanStatePath(ownerHash);
            if (!File.Exists(path))
                return false;

            string[] p = File.ReadAllText(path, Encoding.UTF8).Split('|');
            if (p.Length < 13)
                return false;

            state = new FleecaLoanSnapshot
            {
                Principal = ParseLong(p[0]),
                RemainingDebt = ParseLong(p[1]),
                DailyDue = ParseLong(p[2]),
                Active = ParseBool(p[3]) && ParseLong(p[1]) > 0,
                LastChargedDayStamp = ParseInt(p[4], -1),
                DueWindowStartHour = ParseInt(p[5], -1),
                DueWindowEndHour = ParseInt(p[6], -1),
                CollateralMode = ParseBool(p[7]),
                LoanStartDayStamp = ParseInt(p[8], -1),
                QuickPayRateFirst7 = ParseDecimal(p[9]),
                QuickPayRateNext10 = ParseDecimal(p[10]),
                QuickPayRateAfter17 = ParseDecimal(p[11]),
                Version = ParseInt(p[12], 2),
                TransferredFromParadigmRoyalty = false,
                TransferredDailyRate = -1m
            };

            if (p.Length >= 15)
            {
                state.TransferredFromParadigmRoyalty = ParseBool(p[13]);
                state.TransferredDailyRate = ParseDecimal(p[14]);
            }

            return true;
        }
        catch
        {
            state = null;
            return false;
        }
    }

    private static long ParseLong(string value)
    {
        try
        {
            if (long.TryParse((value ?? string.Empty).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long n))
            {
                return n;
            }
        }
        catch { }

        return 0L;
    }

    private static int ParseInt(string value, int fallback = 0)
    {
        try
        {
            if (int.TryParse((value ?? string.Empty).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int n))
            {
                return n;
            }
        }
        catch { }

        return fallback;
    }

    private static decimal ParseDecimal(string value)
    {
        try
        {
            if (decimal.TryParse((value ?? string.Empty).Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out decimal n))
            {
                return n;
            }
        }
        catch { }

        return 0m;
    }

    private static bool ParseBool(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string s = value.Trim();
            return s == "1"
                || s.Equals("true", StringComparison.OrdinalIgnoreCase)
                || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteFleecaLoanState(int ownerHash, FleecaLoanSnapshot state)
    {
        try
        {
            if (ownerHash == 0 || state == null)
                return;

            string path = GetFleecaLoanStatePath(ownerHash);

            string payload = string.Join("|", new[]
            {
            state.Principal.ToString(CultureInfo.InvariantCulture),
            state.RemainingDebt.ToString(CultureInfo.InvariantCulture),
            state.DailyDue.ToString(CultureInfo.InvariantCulture),
            state.Active ? "1" : "0",
            state.LastChargedDayStamp.ToString(CultureInfo.InvariantCulture),
            state.DueWindowStartHour.ToString(CultureInfo.InvariantCulture),
            state.DueWindowEndHour.ToString(CultureInfo.InvariantCulture),
            state.CollateralMode ? "1" : "0",
            state.LoanStartDayStamp.ToString(CultureInfo.InvariantCulture),
            state.QuickPayRateFirst7.ToString(CultureInfo.InvariantCulture),
            state.QuickPayRateNext10.ToString(CultureInfo.InvariantCulture),
            state.QuickPayRateAfter17.ToString(CultureInfo.InvariantCulture),
            state.Version.ToString(CultureInfo.InvariantCulture),
            state.TransferredFromParadigmRoyalty ? "1" : "0",
            state.TransferredDailyRate.ToString(CultureInfo.InvariantCulture)
        });

            File.WriteAllText(path, payload, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private bool IsWithinWantedWindow(DateTime now)
    {
        try
        {
            if (_loanWantedWindowStartHour < 0 || _loanWantedWindowStartHour > 23 ||
                _loanWantedWindowEndHour < 0 || _loanWantedWindowEndHour > 23)
            {
                return false;
            }

            int hour = now.Hour;

            if (_loanWantedWindowStartHour == _loanWantedWindowEndHour) return true;

            if (_loanWantedWindowStartHour < _loanWantedWindowEndHour)
            {
                return hour >= _loanWantedWindowStartHour && hour < _loanWantedWindowEndHour;
            }

            return hour >= _loanWantedWindowStartHour || hour < _loanWantedWindowEndHour;
        }
        catch
        {
            return false;
        }
    }

    private void ClearLoanState(bool clearHandoff = true)
    {
        _loanActive = false;
        _loanOverdue = false;
        _overdueRateNoticeShown = false;
        _loanPrincipal = 0L;
        _loanRemainingDebt = 0L;
        _selectedLoanAmount = 0L;
        _selectedRepaymentAmount = 0L;
        _currentIncomeTaxRate = IncomeTaxRate;
        _selectedIncomeTaxRate = IncomeTaxRate;
        _lastTrackedMoney = Math.Max(0, Game.Player.Money);
        _nextDebtCheckAt = 0;
        _nextMaturityCheckAt = 0;
        _nextWantedCheckAt = 0;
        _loanDueDayStamp = -1;
        _loanWantedWindowStartHour = -1;
        _loanWantedWindowEndHour = -1;
        _lastWantedAppliedDayStamp = -1;
        _wantedDayStamp = -1;
        _loanTierIndex = 0;
        _overdueWantedStrikeCount = 0;

        if (clearHandoff)
            ParadigmRoyaltyTransferBridge.ClearTransferForCurrentCharacter();

        SaveStateForCurrentCharacter();
        UpdateContactAvailability();
    }

    private void SyncStateForCurrentCharacter()
    {
        try
        {
            int currentHash = GetCurrentCharacterHash();
            if (currentHash == 0) return;

            if (!_stateLoaded)
            {
                _activeCharacterHash = currentHash;
                LoadStateForCharacter(currentHash);
                _stateLoaded = true;

                if (_loanActive)
                {
                    _lastTrackedMoney = Math.Max(0, Game.Player.Money);
                    _nextDebtCheckAt = Game.GameTime + DebtCheckIntervalMs;
                    _nextMaturityCheckAt = Game.GameTime + DebtCheckIntervalMs;
                    _nextWantedCheckAt = Game.GameTime + WantedCheckIntervalMs;
                }

                return;
            }

            if (_activeCharacterHash != currentHash)
            {
                if (_activeCharacterHash != 0) SaveStateForCharacter(_activeCharacterHash);
                _activeCharacterHash = currentHash;
                LoadStateForCharacter(currentHash);

                if (_loanActive)
                {
                    _lastTrackedMoney = Math.Max(0, Game.Player.Money);
                    _nextDebtCheckAt = Game.GameTime + DebtCheckIntervalMs;
                    _nextMaturityCheckAt = Game.GameTime + DebtCheckIntervalMs;
                    _nextWantedCheckAt = Game.GameTime + WantedCheckIntervalMs;
                }
            }
        }
        catch
        {
        }
    }

    private void SaveStateForCurrentCharacter()
    {
        SaveStateForCharacter(GetCurrentCharacterHash());
    }

    private void SaveStateForCharacter(int characterHash)
    {
        try
        {
            Directory.CreateDirectory(_stateRoot);

            string payload = string.Join("|", new[]
            {
            "4",
            _loanActive ? "1" : "0",
            _loanPrincipal.ToString(CultureInfo.InvariantCulture),
            _loanRemainingDebt.ToString(CultureInfo.InvariantCulture),
            _selectedLoanAmount.ToString(CultureInfo.InvariantCulture),
            _selectedRepaymentAmount.ToString(CultureInfo.InvariantCulture),
            _lastTrackedMoney.ToString(CultureInfo.InvariantCulture),
            _nextDebtCheckAt.ToString(CultureInfo.InvariantCulture),
            _loanDueDayStamp.ToString(CultureInfo.InvariantCulture),
            _loanOverdue ? "1" : "0",
            _overdueRateNoticeShown ? "1" : "0",
            _loanWantedWindowStartHour.ToString(CultureInfo.InvariantCulture),
            _loanWantedWindowEndHour.ToString(CultureInfo.InvariantCulture),
            _lastWantedAppliedDayStamp.ToString(CultureInfo.InvariantCulture),
            _currentIncomeTaxRate.ToString(CultureInfo.InvariantCulture),
            _loanTierIndex.ToString(CultureInfo.InvariantCulture),
            _overdueWantedStrikeCount.ToString(CultureInfo.InvariantCulture)
        });

            File.WriteAllText(GetStateFilePath(characterHash), payload, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private void LoadStateForCharacter(int characterHash)
    {
        try
        {
            // Khởi tạo các giá trị mặc định ban đầu
            _loanActive = false;
            _loanOverdue = false;
            _overdueRateNoticeShown = false;
            _loanPrincipal = 0L;
            _loanRemainingDebt = 0L;
            _selectedLoanAmount = 0L;
            _selectedRepaymentAmount = 0L;
            _currentIncomeTaxRate = IncomeTaxRate;
            _lastTrackedMoney = Math.Max(0, Game.Player.Money);
            _nextDebtCheckAt = 0;
            _nextWantedCheckAt = 0;
            _loanDueDayStamp = -1;
            _loanWantedWindowStartHour = -1;
            _loanWantedWindowEndHour = -1;
            _lastWantedAppliedDayStamp = -1;
            _wantedDayStamp = GetDateStamp(GetCurrentInGameDateTime());
            _loanTierIndex = 0;
            _overdueWantedStrikeCount = 0; // Thêm cho v4

            string path = GetStateFilePath(characterHash);
            if (!File.Exists(path)) return;

            string raw = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (string.IsNullOrWhiteSpace(raw)) return;

            string[] p = raw.Split('|');

            // FORMAT MỚI NHẤT (v4)
            if (p.Length >= 17 && p[0].Trim() == "4")
            {
                bool active = p[1].Trim() == "1";
                long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out _loanPrincipal);
                long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out _loanRemainingDebt);
                long.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out _selectedLoanAmount);
                long.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out _selectedRepaymentAmount);
                long.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out _lastTrackedMoney);
                int.TryParse(p[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out _nextDebtCheckAt);
                int.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out _loanDueDayStamp);
                _loanOverdue = p[9].Trim() == "1";
                _overdueRateNoticeShown = p[10].Trim() == "1";
                int.TryParse(p[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out _loanWantedWindowStartHour);
                int.TryParse(p[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out _loanWantedWindowEndHour);
                int.TryParse(p[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out _lastWantedAppliedDayStamp);
                decimal.TryParse(p[14], NumberStyles.Number, CultureInfo.InvariantCulture, out _currentIncomeTaxRate);
                int.TryParse(p[15], NumberStyles.Integer, CultureInfo.InvariantCulture, out _loanTierIndex);
                int.TryParse(p[16], NumberStyles.Integer, CultureInfo.InvariantCulture, out _overdueWantedStrikeCount);

                _loanActive = active && _loanRemainingDebt > 0;
                if (!_loanActive)
                {
                    _loanPrincipal = 0L;
                    _loanRemainingDebt = 0L;
                    _selectedLoanAmount = 0L;
                    _selectedRepaymentAmount = 0L;
                }

                if (_loanDueDayStamp <= 0 && _loanActive)
                {
                    DateTime fallbackDue = GetCurrentInGameDateTime().Date.AddDays(DebtMaturityDays);
                    _loanDueDayStamp = GetDateStamp(fallbackDue);
                }

                return;
            }

            // ==========================================
            // FALLBACK: FORMAT CŨ (v3)
            // ==========================================
            if (p.Length >= 15 && p[0].Trim() == "3")
            {
                bool active = p[1].Trim() == "1";
                long principal = 0L;
                long remaining = 0L;
                long selected = 0L;
                long selectedRepayment = 0L;
                long trackedMoney = Math.Max(0, Game.Player.Money);
                int nextCheck = 0;
                int dueStamp = -1;
                bool overdue = false;
                bool noticeShown = false;
                int wantedStart = -1;
                int wantedEnd = -1;
                int lastWantedStamp = -1;
                decimal currentRate = IncomeTaxRate;
                int tierIndex = 0;

                long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out principal);
                long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out remaining);
                long.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out selected);
                long.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out selectedRepayment);
                long.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out trackedMoney);
                int.TryParse(p[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out nextCheck);
                int.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out dueStamp);
                overdue = p[9].Trim() == "1";
                noticeShown = p[10].Trim() == "1";
                int.TryParse(p[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out wantedStart);
                int.TryParse(p[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out wantedEnd);
                int.TryParse(p[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out lastWantedStamp);
                decimal.TryParse(p[14], NumberStyles.Number, CultureInfo.InvariantCulture, out currentRate);
                if (p.Length >= 16) int.TryParse(p[15], NumberStyles.Integer, CultureInfo.InvariantCulture, out tierIndex);

                _loanPrincipal = Math.Max(0L, principal);
                _loanRemainingDebt = Math.Max(0L, remaining);
                _selectedLoanAmount = Math.Max(0L, selected);
                _selectedRepaymentAmount = Math.Max(0L, selectedRepayment);

                if (_selectedRepaymentAmount <= 0L && _selectedLoanAmount > 0L)
                {
                    int inferredIndex = InferTierIndexFromLoanAmount(_loanPrincipal, _selectedLoanAmount);
                    _selectedRepaymentAmount = CalculateRepaymentAmount(_selectedLoanAmount, LoanTiers[inferredIndex].FeeMultiplier);
                }

                _lastTrackedMoney = Math.Max(0L, trackedMoney);
                _nextDebtCheckAt = Math.Max(0, nextCheck);
                _loanDueDayStamp = dueStamp;
                _loanOverdue = overdue;
                _overdueRateNoticeShown = noticeShown;
                _loanWantedWindowStartHour = wantedStart;
                _loanWantedWindowEndHour = wantedEnd;
                _lastWantedAppliedDayStamp = lastWantedStamp;

                // Clamp _currentIncomeTaxRate mới
                _currentIncomeTaxRate = Math.Max(0m, currentRate);
                if (_currentIncomeTaxRate >= 1m)
                {
                    _currentIncomeTaxRate = 0.9999m;
                }

                _loanTierIndex = Math.Max(0, Math.Min(tierIndex, GetMaxTierIndex()));
                _loanActive = active && _loanRemainingDebt > 0;

                if (!_loanActive)
                {
                    _loanPrincipal = 0L;
                    _loanRemainingDebt = 0L;
                    _selectedLoanAmount = 0L;
                    _loanDueDayStamp = -1;
                    _loanWantedWindowStartHour = -1;
                    _loanWantedWindowEndHour = -1;
                    _lastWantedAppliedDayStamp = -1;
                    _wantedDayStamp = GetDateStamp(GetCurrentInGameDateTime());
                    _loanOverdue = false;
                    _overdueRateNoticeShown = false;
                }
                else
                {
                    if (_selectedLoanAmount > 0L)
                    {
                        int inferredIndex = InferTierIndexFromLoanAmount(_loanPrincipal, _selectedLoanAmount);
                        _loanTierIndex = Math.Max(_loanTierIndex, inferredIndex);
                    }

                    if (_loanDueDayStamp <= 0)
                    {
                        DateTime fallbackDue = GetCurrentInGameDateTime().Date.AddDays(DebtMaturityDays);
                        _loanDueDayStamp = GetDateStamp(fallbackDue);
                    }

                    _wantedDayStamp = GetDateStamp(GetCurrentInGameDateTime());
                }

                return;
            }
        }
        catch
        {
            // Reset toàn bộ trạng thái về mặc định an toàn nếu xảy ra lỗi đọc file
            _loanActive = false;
            _loanOverdue = false;
            _overdueRateNoticeShown = false;
            _loanPrincipal = 0L;
            _loanRemainingDebt = 0L;
            _selectedLoanAmount = 0L;
            _selectedRepaymentAmount = 0L;
            _currentIncomeTaxRate = IncomeTaxRate;
            _selectedIncomeTaxRate = IncomeTaxRate;
            _lastTrackedMoney = Math.Max(0, Game.Player.Money);
            _nextDebtCheckAt = 0;
            _nextMaturityCheckAt = 0;
            _nextWantedCheckAt = 0;
            _loanDueDayStamp = -1;
            _loanWantedWindowStartHour = -1;
            _loanWantedWindowEndHour = -1;
            _lastWantedAppliedDayStamp = -1;
            _wantedDayStamp = GetDateStamp(GetCurrentInGameDateTime());
            _loanTierIndex = 0;
            _overdueWantedStrikeCount = 0; // Thêm đồng bộ vào catch block
        }
    }

    private void UpdateLemonUiMouseState()
    {
        try
        {
            if (!IsMenuVisible()) return;

            SetBoolPropertyIfExists(
                _menu,
                false,
                "MouseControlsEnabled",
                "MouseControls",
                "EnableMouseControls",
                "UseMouse",
                "MouseEdgeEnabled",
                "MouseEdgesEnabled",
                "AllowMouseControls"
            );

            SetBoolPropertyIfExists(
                _uiPool,
                false,
                "MouseControlsEnabled",
                "MouseControls",
                "EnableMouseControls",
                "UseMouse",
                "MouseEdgeEnabled",
                "MouseEdgesEnabled",
                "AllowMouseControls"
            );
        }
        catch
        {
        }
    }

    private static void SetBoolPropertyIfExists(object target, bool value, params string[] propertyNames)
    {
        try
        {
            if (target == null || propertyNames == null || propertyNames.Length == 0) return;

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
}

public static class ParadigmRoyaltyTransferBridge
{
    private static readonly string TransferRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Venture Capital");

    private static readonly string FleecaRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Fleeca Bank Loan");

    private sealed class TransferState
    {
        public bool Active;
        public long EffectiveDebt;
        public decimal SpecialRate;
        public bool MinotaurBoosted;
        public int CreatedDayStamp;
    }

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

    public static bool IsParadigmRoyaltyContactAvailableForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return false;

            if (!TryReadTransferState(ownerHash, out var state) || state == null || !state.Active)
                return true;

            // Có handoff => chỉ mở contact khi chắc chắn Fleeca đã hết nợ
            if (!TryReadFleecaLoanActive(ownerHash, out bool fleecaActive))
                return false;

            if (fleecaActive)
                return false;

            // Fleeca đã thanh toán xong => dọn handoff cũ, contact mở lại
            ClearTransferForOwner(ownerHash);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ClearTransferForOwner(int ownerHash)
    {
        try
        {
            string path = GetTransferStatePath(ownerHash);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static string GetTransferStatePath(int ownerHash)
    {
        Directory.CreateDirectory(TransferRoot);
        return Path.Combine(TransferRoot, $"paradigm_handoff_{ownerHash:X8}.dat");
    }

    private static string GetFleecaLoanStatePath(int ownerHash)
    {
        Directory.CreateDirectory(FleecaRoot);
        return Path.Combine(FleecaRoot, $"loan_state_{ownerHash:X8}.dat");
    }

    public static bool IsTransferPendingForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return false;

            return TryReadTransferState(ownerHash, out var state) && state != null && state.Active;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsFleecaLoanActiveForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return false;

            return TryReadFleecaLoanActive(ownerHash, out bool active) && active;
        }
        catch
        {
            return false;
        }
    }

    public static void MarkTransferForCurrentCharacter(long effectiveDebt, decimal specialRate, bool minotaurBoosted)
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            Directory.CreateDirectory(TransferRoot);

            int dayStamp = GetCurrentGameDayStamp();
            string payload = string.Join("|", new[]
            {
                "1",
                "1",
                effectiveDebt.ToString(CultureInfo.InvariantCulture),
                specialRate.ToString(CultureInfo.InvariantCulture),
                minotaurBoosted ? "1" : "0",
                dayStamp.ToString(CultureInfo.InvariantCulture)
            });

            File.WriteAllText(GetTransferStatePath(ownerHash), payload, Encoding.UTF8);
        }
        catch
        {
        }
    }

    public static void ClearTransferForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            string path = GetTransferStatePath(ownerHash);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    public static bool TryGetOverrideRate(out decimal rate)
    {
        rate = 0m;

        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return false;

            if (!TryReadTransferState(ownerHash, out var state) || state == null || !state.Active)
                return false;

            if (!TryReadFleecaLoanActive(ownerHash, out bool fleecaActive) || !fleecaActive)
            {
                ClearTransferForCurrentCharacter();
                return false;
            }

            if (state.SpecialRate <= 0m)
                return false;

            rate = state.SpecialRate;
            return true;
        }
        catch
        {
            rate = 0m;
            return false;
        }
    }

    private static bool TryReadTransferState(int ownerHash, out TransferState state)
    {
        state = null;

        try
        {
            string path = GetTransferStatePath(ownerHash);
            if (!File.Exists(path))
                return false;

            string[] p = File.ReadAllText(path, Encoding.UTF8).Split('|');
            if (p.Length < 6)
                return false;

            state = new TransferState
            {
                Active = p[1].Trim() == "1",
                EffectiveDebt = ParseLong(p[2]),
                SpecialRate = ParseDecimal(p[3]),
                MinotaurBoosted = p[4].Trim() == "1",
                CreatedDayStamp = ParseInt(p[5], -1)
            };

            return true;
        }
        catch
        {
            state = null;
            return false;
        }
    }

    private static bool TryReadFleecaLoanActive(int ownerHash, out bool active)
    {
        active = false;

        try
        {
            string path = GetFleecaLoanStatePath(ownerHash);
            if (!File.Exists(path))
                return false;

            string[] p = File.ReadAllText(path, Encoding.UTF8).Split('|');
            if (p.Length < 4)
                return false;

            active = (p[3].Trim() == "1") && ParseLong(p[1]) > 0;
            return true;
        }
        catch
        {
            active = false;
            return false;
        }
    }

    private static int GetCurrentGameDayStamp()
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
        catch
        {
            return -1;
        }
    }

    private static long ParseLong(string value)
    {
        try
        {
            if (long.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
                return n;
        }
        catch { }
        return 0L;
    }

    private static int ParseInt(string value, int fallback = 0)
    {
        try
        {
            if (int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                return n;
        }
        catch { }
        return fallback;
    }

    private static decimal ParseDecimal(string value)
    {
        try
        {
            if (decimal.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal n))
                return n;
        }
        catch { }
        return 0m;
    }
}