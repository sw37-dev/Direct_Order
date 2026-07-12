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
using System.Media;
using System.Threading.Tasks;

public class MeridianTrust : Script
{
    private const int FRANKLIN_HASH = -1692214353;
    private const int MICHAEL_HASH = 225514697;
    private const int TREVOR_HASH = -1686040670;

    private static string CONTACT_NAME => L("HFP_ContactName", "Meridian Trust");
    private const int CONTACT_DIAL_TIMEOUT_MS = 2000;
    private const int FUND_TERM_DAYS = 5;
    private const long MIN_DEPOSIT = 5_000_000L;
    private const long MAX_DEPOSIT = 50_000_000L;

    private const int HFP_INTRO_DELAY_MS = 800;

    private static string GetScriptsDirectory()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string scriptsDir = Path.Combine(baseDir, "scripts");

            if (Directory.Exists(scriptsDir))
            {
                return scriptsDir;
            }

            return baseDir;
        }
        catch
        {
            return ".";
        }
    }

    private readonly string HfpAudioRoot = Path.Combine(
        GetScriptsDirectory(),
        "Audio"
    );

    private readonly string HfpIntroWavFileName = "HFP.wav";

    private bool _introWavPlaying = false;
    private int _introWavStartedAt = 0;

    private static readonly Random _rng = new Random();

    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Hedge Fund Pooling"
    );

    private readonly ObjectPool _uiPool = new ObjectPool();

    private CustomiFruit _phoneInstance;
    private iFruitContact _contact;
    private bool _contactAdded;
    private bool _contactAnsweredBound;

    private bool _promptPending;
    private int _promptDueAt;
    private int _promptOwnerHash;

    private bool _menuReady;
    private bool _menuOpen;
    private NativeMenu _menu;

    private NativeItem _orgItem;
    private NativeItem _customerItem;
    private NativeItem _amountItem;
    private NativeItem _strategyHeaderItem;
    private NativeCheckboxItem _safeItem;
    private NativeCheckboxItem _attackItem;
    private NativeCheckboxItem _specItem;
    private NativeItem _lockPeriodItem;
    private NativeItem _confirmItem;
    private NativeItem _cancelItem;

    private bool _strategySyncLock;
    private HedgeFundState _state;
    private int _stateOwnerHash;

    private int _nextWantedCheckAtGameTime = 0;
    private int _lastSyncedDayKey = -1;

    private static string L(string key, string fallback = "") => Language.Get(key, fallback);

    private enum HedgeFundStrategy
    {
        Safe = 0,
        Attack = 1,
        Speculation = 2
    }

    private enum HfpSpecialMode
    {
        None = 0,
        CrashLoss = 1,
        TerminateAndRefund = 2
    }

    private sealed class HedgeFundState
    {
        public int OwnerHash;
        public bool Active;
        public string CustomerName = "";
        public HedgeFundStrategy Strategy;
        public long InitialDeposit;
        public long CurrentBalance;
        public string DepositTimestamp = "";
        public int DepositDayKey;
        public int LastProcessedDayKey;
        public int CurrentDayWantedMax;
        public HfpDailyProfile CurrentProfile = new HfpDailyProfile();

        // NEW
        public long PendingPayout;
        public bool AwaitingPayout;
    }

    private struct HfpDailyProfile
    {
        public double PositiveChance;
        public double NegativeChance;
        public double SpecialChance;
        public double PositiveMinPercent;
        public double PositiveMaxPercent;
        public double NegativeMinPercent;
        public double NegativeMaxPercent;
        public HfpSpecialMode SpecialMode;
        public double SpecialValuePercent;
    }

    public MeridianTrust()
    {
        Interval = 1000;
        Tick += OnTick;
        KeyDown += OnKeyDown;
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            SyncStateForCurrentCharacter();
            EnsureContactRegistered();

            if (_introWavPlaying)
            {
                if (Game.GameTime - _introWavStartedAt >= HFP_INTRO_DELAY_MS)
                {
                    _introWavPlaying = false;
                    _promptPending = true;
                    _promptDueAt = Game.GameTime + 1;
                }
                else
                {
                    Interval = 0;
                    return;
                }
            }

            ProcessPromptState();
            ProcessDailyState();
            UpdateMenuIfOpen();

            if (_promptPending || _menuOpen)
                Interval = 0;
            else
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
            if (_promptPending)
            {
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                {
                    CancelPrompt();
                    return;
                }

                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
                {
                    AcceptPrompt();
                    return;
                }
            }

            if (_menuOpen && (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape))
            {
                CloseMenu();
            }
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
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_phoneInstance, phone))
            {
                _phoneInstance = phone;
                _contact = null;
                _contactAdded = false;
                _contactAnsweredBound = false;
            }

            int ownerHash = GetCurrentCharacterHash();
            bool shouldBeActive = ownerHash != 0 && !HasActiveFundForOwner(ownerHash);

            if (_contact == null)
            {
                _contact = phone.Contacts.FirstOrDefault(c =>
                    c != null && string.Equals(c.Name, CONTACT_NAME, StringComparison.OrdinalIgnoreCase));
            }

            if (_contact == null)
            {
                _contact = new iFruitContact(CONTACT_NAME)
                {
                    Active = shouldBeActive,
                    DialTimeout = CONTACT_DIAL_TIMEOUT_MS,
                    Bold = false,
                    Icon = ContactIcon.MP_StripclubPr
                };

                _contact.Answered += OnContactAnswered;
                _contactAnsweredBound = true;
                phone.Contacts.Add(_contact);
            }
            else
            {
                _contact.Active = shouldBeActive;
                if (!_contactAnsweredBound)
                {
                    _contact.Answered += OnContactAnswered;
                    _contactAnsweredBound = true;
                }
            }

            _contactAdded = true;
        }
        catch
        {
        }
    }

    private void OnContactAnswered(iFruitContact sender)
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
            {
                ShowTrustNotification(
                    CONTACT_NAME,
                    L("HFP_NotifyTitle_Info", "Thông báo"),
                    L("HFP_NotifyNoCharacter", "Hiện không có nhân vật phù hợp để sử dụng dịch vụ này.")
                );
                TryClosePhone();
                return;
            }

            if (HasActiveFundForOwner(ownerHash))
            {
                ShowTrustNotification(
                    CONTACT_NAME,
                    L("HFP_NotifyTitle_Denied", "Từ chối dịch vụ"),
                    L("HFP_NotifyHasActiveFund", "Hiện tại bạn đang có một quỹ đang hoạt động. Meridian Trust sẽ không mở thêm quỹ mới cho tới khi quỹ hiện tại kết thúc.")
                );
                TryClosePhone();
                return;
            }

            // --- Bắt đầu đoạn thay thế ---
            RefreshStateForCurrentCharacter(forceReload: true);

            if (_state != null && _state.Active)
            {
                ShowTrustNotification(
                    CONTACT_NAME,
                    L("HFP_NotifyTitle_Info", "Thông báo"),
                    L("HFP_NotifyActiveFund", "Quỹ này đang hoạt động rồi."));
                TryClosePhone();
                return;
            }

            _promptPending = false;
            _promptOwnerHash = ownerHash;
            _introWavPlaying = true;
            _introWavStartedAt = Game.GameTime;

            PlayHfpIntroWav();
            TryClosePhone();
            return;
            // --- Kết thúc đoạn thay thế ---
        }
        catch
        {
            TryClosePhone();
        }
    }

    private void ProcessPromptState()
    {
        try
        {
            if (!_promptPending)
                return;

            if (Game.GameTime < _promptDueAt)
                return;

            GTA.UI.Screen.ShowHelpTextThisFrame(
                 L("HFP_PromptHelp",
                "Đây là Quỹ phòng hộ, bạn muốn nộp tiền vào quỹ? (có thể sinh lời/mất mát)\n~INPUT_FRONTEND_ACCEPT~ để đồng ý\n~INPUT_FRONTEND_LEFT~ để hủy"));
        }
        catch { }
    }

    private void AcceptPrompt()
    {
        try
        {
            if (!_promptPending)
                return;

            _promptPending = false;
            EnsureMenuCreated();
            BuildMenuForCurrentCharacter();
            CloseMenuVisibleOnly();
            _menuOpen = true;

            if (_menu != null)
                _menu.Visible = true;

            TryClosePhone();
            UpdateMenuMouseState();
            _uiPool.Process();
            Interval = 0;
        }
        catch
        {
            CancelPrompt();
        }
    }

    private void CancelPrompt()
    {
        try
        {
            _promptPending = false;
            _promptOwnerHash = 0;
            _introWavPlaying = false;
            TryClosePhone();
        }
        catch
        {
        }
    }

    private void EnsureMenuCreated()
    {
        try
        {
            if (_menuReady)
                return;

            _menu = new NativeMenu(
                L("HFP_MenuTitle", "Hedge Fund Pooling"),
                L("HFP_MenuSubtitle", "CHI TIẾT QUỸ PHÒNG HỘ"));
            ConfigureKeyboardOnlyMenu(_menu);
            _uiPool.Add(_menu);
            _menuReady = true;
        }
        catch
        {
        }
    }

    private void BuildMenuForCurrentCharacter()
    {
        try
        {
            if (_menu == null)
                return;

            RefreshStateForCurrentCharacter(forceReload: true);

            _menu.Clear();

            string customerName = _state != null && !string.IsNullOrWhiteSpace(_state.CustomerName)
                ? _state.CustomerName
                : GetCharacterDisplayName(GetCurrentCharacterHash());

            long amount = _state != null ? _state.InitialDeposit : 0L;
            HedgeFundStrategy selectedStrategy = _state != null ? _state.Strategy : HedgeFundStrategy.Safe;

            _orgItem = new NativeItem(string.Format(
                CultureInfo.InvariantCulture,
                "1. {0}: {1}",
                L("HFP_FieldOrg", "Tên tổ chức"),
                CONTACT_NAME));

            _customerItem = new NativeItem(string.Format(
                CultureInfo.InvariantCulture,
                "2. {0}: {1}",
                L("HFP_FieldCustomer", "Tên khách hàng"),
                customerName));

            _amountItem = new NativeItem(string.Format(
                CultureInfo.InvariantCulture,
                "3. {0}: {1}",
                L("HFP_FieldAmount", "Số tiền nạp quỹ"),
                FormatMoney(amount)));
            _amountItem.Activated += (s, e) => PromptForDepositAmount();

            _strategyHeaderItem = new NativeItem(L("HFP_StrategyHeader", "4. Chiến lược đầu tư"));
            _strategyHeaderItem.Enabled = true;

            _safeItem = new NativeCheckboxItem(L("HFP_StrategySafe", "An toàn"), false);
            _attackItem = new NativeCheckboxItem(L("HFP_StrategyAttack", "Tấn công"), false);
            _specItem = new NativeCheckboxItem(L("HFP_StrategySpeculation", "Đầu cơ"), false);

            _safeItem.CheckboxChanged += (s, e) => OnStrategyCheckboxChanged(HedgeFundStrategy.Safe, _safeItem.Checked);
            _attackItem.CheckboxChanged += (s, e) => OnStrategyCheckboxChanged(HedgeFundStrategy.Attack, _attackItem.Checked);
            _specItem.CheckboxChanged += (s, e) => OnStrategyCheckboxChanged(HedgeFundStrategy.Speculation, _specItem.Checked);

            _lockPeriodItem = new NativeItem(L("HFP_LockPeriod", "5. Thời gian khóa vốn: 5 ngày"));
            _lockPeriodItem.Enabled = true;

            _confirmItem = new NativeItem(L("HFP_ConfirmFund", "Xác nhận nạp quỹ"));
            _cancelItem = new NativeItem(L("HFP_CancelFund", "Hủy đầu cơ HFP"));

            _confirmItem.Activated += (s, e) => ConfirmFundDeposit();
            _cancelItem.Activated += (s, e) => CloseMenu();

            _menu.Add(_orgItem);
            _menu.Add(_customerItem);
            _menu.Add(_amountItem);
            _menu.Add(_strategyHeaderItem);
            _menu.Add(_safeItem);
            _menu.Add(_attackItem);
            _menu.Add(_specItem);
            _menu.Add(_lockPeriodItem);
            _menu.Add(_confirmItem);
            _menu.Add(_cancelItem);

            ApplyStrategySelection(selectedStrategy, false);
            UpdateMenuText();
        }
        catch
        {
        }
    }

    private static string MenuDim(string text)
    {
        return "~c~" + text + "~s~";
    }

    private static string MenuBright(string text)
    {
        return "~w~" + text + "~s~";
    }

    private void UpdateStrategyItemVisuals(HedgeFundStrategy selectedStrategy)
    {
        try
        {
            if (_safeItem != null)
            {
                string safeText = L("HFP_StrategySafe", "An toàn");
                _safeItem.Title = (_safeItem.Checked = selectedStrategy == HedgeFundStrategy.Safe)
                    ? MenuBright(safeText)
                    : MenuDim(safeText);
            }

            if (_attackItem != null)
            {
                string attackText = L("HFP_StrategyAttack", "Tấn công");
                _attackItem.Title = (_attackItem.Checked = selectedStrategy == HedgeFundStrategy.Attack)
                    ? MenuBright(attackText)
                    : MenuDim(attackText);
            }

            if (_specItem != null)
            {
                string specText = L("HFP_StrategySpeculation", "Đầu cơ");
                _specItem.Title = (_specItem.Checked = selectedStrategy == HedgeFundStrategy.Speculation)
                    ? MenuBright(specText)
                    : MenuDim(specText);
            }
        }
        catch
        {
        }
    }

    private void OnStrategyCheckboxChanged(HedgeFundStrategy strategy, bool checkedState)
    {
        if (_strategySyncLock)
            return;

        try
        {
            if (checkedState)
            {
                ApplyStrategySelection(strategy, true);
                return;
            }

            if (_state != null && _state.Active)
            {
                ApplyStrategySelection(_state.Strategy, true);
            }
            else
            {
                ApplyStrategySelection(HedgeFundStrategy.Safe, true);
            }
        }
        catch
        {
        }
    }

    private void ApplyStrategySelection(HedgeFundStrategy strategy, bool saveState)
    {
        try
        {
            _strategySyncLock = true;
            UpdateStrategyItemVisuals(strategy);

            if (_state != null && saveState)
            {
                _state.Strategy = strategy;
                _state.CurrentProfile = BuildDailyProfile(strategy, GetWantedPenaltyTier(_state.CurrentDayWantedMax));
                SaveStateForOwner(_state.OwnerHash);
            }
        }
        finally
        {
            _strategySyncLock = false;
        }
    }

    private void UpdateMenuText()
    {
        try
        {
            if (_menu == null)
                return;

            if (_state == null)
            {
                _amountItem.Title = string.Format(
                    CultureInfo.InvariantCulture,
                    "3. {0}: $0",
                    L("HFP_FieldAmount", "Số tiền nạp quỹ"));
                return;
            }

            _amountItem.Title = string.Format(
                CultureInfo.InvariantCulture,
                "3. {0}: {1}",
                L("HFP_FieldAmount", "Số tiền nạp quỹ"),
                FormatMoney(_state.InitialDeposit));
        }
        catch
        {
        }
    }

    private void PromptForDepositAmount()
    {
        try
        {
            if (_state != null && (_state.Active || _state.AwaitingPayout))
            {
                ShowTrustNotification(
                    CONTACT_NAME,
                    L("HFP_NotifyTitle_Denied", "Từ chối"),
                    L("HFP_NotifyActiveOrPending", "Bạn đang có một quỹ đang hoạt động hoặc một khoản thanh toán đang chờ xử lý nên không thể nạp thêm quỹ mới."));
                return;
            }

            string input = Game.GetUserInput(string.Empty);
            if (string.IsNullOrWhiteSpace(input))
                return;

            var digits = new StringBuilder();
            foreach (char ch in input)
            {
                if (char.IsDigit(ch))
                    digits.Append(ch);
            }

            if (!long.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
            {
                GTA.UI.Screen.ShowSubtitle(L("HFP_InvalidAmount", "Số tiền không hợp lệ."), 2500);
                return;
            }

            if (amount < MIN_DEPOSIT)
            {
                GTA.UI.Screen.ShowSubtitle(L("HFP_MinDeposit", "Số tiền nạp tối thiểu là ~HUD_COLOUR_DEGEN_RED~$5,000,000~s~."), 2500);
                return;
            }

            if (amount > MAX_DEPOSIT)
            {
                GTA.UI.Screen.ShowSubtitle(L("HFP_MaxDeposit", "Số tiền nạp tối đa là ~HUD_COLOUR_DEGEN_YELLOW~$50,000,000~s~."), 2500);
                return;
            }

            if (amount > Math.Max(0, Game.Player.Money))
            {
                GTA.UI.Screen.ShowSubtitle(L("HFP_NotEnoughMoney", "Bạn không đủ tiền để nạp khoản này."), 2500);
                return;
            }

            if (_state == null)
                _state = CreateNewStateForCurrentCharacter();

            _state.InitialDeposit = amount;
            _state.CurrentBalance = amount;
            _state.Strategy = _state.Strategy;
            _state.CurrentProfile = BuildDailyProfile(_state.Strategy, 0);
            _state.CurrentDayWantedMax = Math.Max(0, Game.Player.WantedLevel);

            UpdateMenuText();
            ApplyStrategySelection(_state.Strategy, false);
        }
        catch
        {
        }
    }

    private void ConfirmFundDeposit()
    {
        try
        {
            if (_state == null)
                _state = CreateNewStateForCurrentCharacter();

            if (_state.Active || _state.AwaitingPayout)
            {
                ShowTrustNotification(
                    CONTACT_NAME,
                    L("HFP_NotifyTitle_Denied", "Từ chối"),
                    L("HFP_NotifyActiveOrPendingConfirm", "Bạn đang có một quỹ đang hoạt động hoặc một khoản thanh toán đang chờ xử lý nên không thể xác nhận nạp quỹ mới."));
                return;
            }

            if (_state.InitialDeposit < MIN_DEPOSIT)
            {
                GTA.UI.Screen.ShowSubtitle(L("HFP_MinDepositSimple", "Số tiền nạp tối thiểu là $5,000,000."), 2500);
                return;
            }

            if (_state.InitialDeposit > MAX_DEPOSIT)
            {
                GTA.UI.Screen.ShowSubtitle(L("HFP_MaxDepositSimple", "Số tiền nạp tối đa là $50,000,000."), 2500);
                return;
            }

            if (!Enum.IsDefined(typeof(HedgeFundStrategy), _state.Strategy))
                _state.Strategy = HedgeFundStrategy.Safe;

            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            if (Game.Player.Money < (int)Math.Min(_state.InitialDeposit, int.MaxValue))
            {
                GTA.UI.Screen.ShowSubtitle(L("HFP_NotEnoughMoneyFund", "Bạn không đủ tiền để nạp quỹ."), 2500);
                return;
            }

            Game.Player.Money -= (int)Math.Min(_state.InitialDeposit, int.MaxValue);

            DateTime now = GetCurrentInGameDateTime();
            int dayKey = GetCurrentGameDayKey();

            _state.OwnerHash = ownerHash;
            _state.Active = true;
            _state.AwaitingPayout = false;
            _state.PendingPayout = 0L;
            _state.CustomerName = GetCharacterDisplayName(ownerHash);
            _state.DepositTimestamp = now.ToString("o", CultureInfo.InvariantCulture);
            _state.DepositDayKey = dayKey;
            _state.LastProcessedDayKey = dayKey;
            _state.CurrentBalance = Math.Max(0L, _state.InitialDeposit);
            _state.CurrentDayWantedMax = Math.Max(0, Game.Player.WantedLevel);
            _state.CurrentProfile = BuildDailyProfile(_state.Strategy, 0);

            SaveStateForOwner(ownerHash);
            UpdateContactActiveState();
            CloseMenu();

            ShowTrustNotification(
                CONTACT_NAME,
                L("HFP_NotifyTitle_Confirm", "Xác nhận"),
                string.Format(
                    CultureInfo.InvariantCulture,
                    L("HFP_DepositConfirmed",
                    "Bạn đã nạp {0} vào quỹ. Meridian Trust sẽ theo dõi hiệu quả đầu tư trong 5 ngày."),
                    FormatMoney(_state.InitialDeposit)));
        }
        catch
        {
        }
    }

    private void SyncStateForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
            {
                _state = null;
                _stateOwnerHash = 0;
                UpdateContactActiveState();
                return;
            }

            if (_stateOwnerHash != ownerHash || _state == null)
            {
                _stateOwnerHash = ownerHash;
                LoadStateForOwner(ownerHash);
                UpdateContactActiveState();
            }
            else
            {
                RefreshStateForCurrentCharacter(forceReload: false);
            }

            UpdateContactActiveState();
        }
        catch
        {
        }
    }

    private void RefreshStateForCurrentCharacter(bool forceReload)
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            if (_state == null || _stateOwnerHash != ownerHash)
            {
                LoadStateForOwner(ownerHash);
                return;
            }

            if (forceReload)
                LoadStateForOwner(ownerHash);
        }
        catch
        {
        }
    }

    private void UpdateContactActiveState()
    {
        try
        {
            if (_contact == null)
                return;

            int ownerHash = GetCurrentCharacterHash();
            bool shouldBeActive = ownerHash != 0 && !HasActiveFundForOwner(ownerHash);
            _contact.Active = shouldBeActive;
        }
        catch
        {
        }
    }

    private bool HasActiveFundForOwner(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return false;

            if (_state != null && _state.OwnerHash == ownerHash && (_state.Active || _state.AwaitingPayout))
                return true;

            string file = GetStateFile(ownerHash);
            if (!File.Exists(file))
                return false;

            var parsed = LoadStateFromFile(file, ownerHash);
            return parsed != null && (parsed.Active || parsed.AwaitingPayout);
        }
        catch
        {
            return false;
        }
    }

    private void ProcessDailyState()
    {
        try
        {
            if (_state == null)
                return;

            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0 || ownerHash != _state.OwnerHash)
                return;

            // NEW: nếu đang chờ thanh toán thì cứ thử trả dồn tiếp
            if (_state.AwaitingPayout)
            {
                TrySettlePendingPayout(ownerHash);
                return;
            }

            if (!_state.Active)
                return;

            int currentDayKey = GetCurrentGameDayKey();
            if (currentDayKey < 0)
                return;

            int elapsedDays = GetDayDistance(_state.DepositDayKey, currentDayKey);
            if (elapsedDays >= FUND_TERM_DAYS)
            {
                MatureFund(ownerHash);
                return;
            }

            if (_lastSyncedDayKey == currentDayKey)
                return;

            if (_state.LastProcessedDayKey != currentDayKey)
            {
                ApplyDailyOutcomeForEndedDay(ownerHash, currentDayKey);
            }

            _lastSyncedDayKey = currentDayKey;
        }
        catch { }
    }

    private void BeginDeferredPayout(int ownerHash, long payout)
    {
        try
        {
            if (_state == null)
                return;

            payout = Math.Max(0L, payout);

            _state.Active = false;
            _state.AwaitingPayout = true;
            _state.PendingPayout = payout;
            _state.CurrentBalance = payout;
            _state.LastProcessedDayKey = GetCurrentGameDayKey();

            SaveStateForOwner(ownerHash);
            TrySettlePendingPayout(ownerHash);
        }
        catch
        {
        }
    }

    private void TrySettlePendingPayout(int ownerHash)
    {
        try
        {
            if (_state == null || !_state.AwaitingPayout)
                return;

            long due = Math.Max(0L, _state.PendingPayout > 0L ? _state.PendingPayout : _state.CurrentBalance);
            if (due <= 0L)
            {
                ShowTrustNotification(
                    CONTACT_NAME,
                    L("HFP_NotifyTitle_End", "Kết thúc quỹ"),
                    L("HFP_PayoutCompletedAll", "Meridian Trust đã hoàn tất thanh toán toàn bộ khoản còn lại vào tài khoản của bạn."));
                DeleteState(ownerHash);
                _state = null;
                _stateOwnerHash = 0;
                UpdateContactActiveState();
                return;
            }

            long currentMoney = Math.Max(0L, (long)Game.Player.Money);
            long room = (long)int.MaxValue - currentMoney;
            if (room < 0L)
                room = 0L;

            long paid = Math.Min(due, room);
            if (paid <= 0L)
                return;

            Game.Player.Money += (int)Math.Min(paid, int.MaxValue);

            long remaining = due - paid;
            _state.PendingPayout = remaining;
            _state.CurrentBalance = remaining;
            _state.Active = false;
            _state.AwaitingPayout = remaining > 0L;

            if (remaining > 0L)
            {
                SaveStateForOwner(ownerHash);
                ShowTrustNotification(
                    CONTACT_NAME,
                    L("HFP_NotifyTitle_Pending", "Thanh toán tạm hoãn"),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        L("HFP_PayoutPartial",
                        "Hôm nay Meridian Trust đã trả ~HUD_COLOUR_DEGEN_GREEN~{0}~s~. Phần còn lại ~HUD_COLOUR_DEGEN_YELLOW~{1}~s~ sẽ được cộng dồn sang ngày tiếp theo."),
                        FormatMoney(paid),
                        FormatMoney(remaining)));
                return;
            }

            ShowTrustNotification(
                CONTACT_NAME,
                L("HFP_NotifyTitle_End", "Kết thúc quỹ"),
                string.Format(
                    CultureInfo.InvariantCulture,
                    L("HFP_PayoutCompleted",
                    "Meridian Trust đã hoàn tất thanh toán số tiền là ~HUD_COLOUR_DEGEN_CYAN~{0}~s~ vào tài khoản của bạn."),
                    FormatMoney(paid)));

            DeleteState(ownerHash);
            _state = null;
            _stateOwnerHash = 0;
            UpdateContactActiveState();
        }
        catch
        {
        }
    }

    private void ApplyDailyOutcomeForEndedDay(int ownerHash, int currentDayKey)
    {
        try
        {
            if (_state == null || !_state.Active)
                return;

            int tier = GetWantedPenaltyTier(_state.CurrentDayWantedMax);
            HfpDailyProfile profile = _state.CurrentProfile;
            if (profile.PositiveChance <= 0.0 && profile.NegativeChance <= 0.0 && profile.SpecialChance <= 0.0)
                profile = BuildDailyProfile(_state.Strategy, tier);

            ApplyProfileToBalance(ownerHash, ref _state.CurrentBalance, _state.InitialDeposit, _state.Strategy, profile);

            if (_state.Active)
            {
                _state.LastProcessedDayKey = currentDayKey;
                _state.CurrentDayWantedMax = Math.Max(0, Game.Player.WantedLevel);
                _state.CurrentProfile = BuildDailyProfile(_state.Strategy, GetWantedPenaltyTier(_state.CurrentDayWantedMax));
                SaveStateForOwner(ownerHash);
            }
        }
        catch
        {
        }
    }

    private void MatureFund(int ownerHash)
    {
        try
        {
            if (_state == null || !_state.Active)
                return;

            long payout = Math.Max(0L, _state.CurrentBalance);
            BeginDeferredPayout(ownerHash, payout);
        }
        catch
        {
        }
    }

    private void ApplyProfileToBalance(int ownerHash, ref long balance, long initialDeposit, HedgeFundStrategy strategy, HfpDailyProfile profile)
    {
        try
        {
            double roll = _rng.NextDouble() * 100.0;
            if (roll < profile.PositiveChance)
            {
                double pct = RandomRange(profile.PositiveMinPercent, profile.PositiveMaxPercent);
                balance = RoundMoney((double)balance * (1.0 + pct));
                return;
            }

            roll -= profile.PositiveChance;
            if (roll < profile.NegativeChance)
            {
                double pct = RandomRange(profile.NegativeMinPercent, profile.NegativeMaxPercent);
                balance = Math.Max(0L, RoundMoney((double)balance * (1.0 + pct)));
                return;
            }

            if (profile.SpecialChance <= 0.0)
                return;

            if (profile.SpecialMode == HfpSpecialMode.CrashLoss)
            {
                balance = Math.Max(0L, RoundMoney((double)balance * (1.0 - profile.SpecialValuePercent / 100.0)));
                return;
            }

            if (profile.SpecialMode == HfpSpecialMode.TerminateAndRefund)
            {
                long payout = RoundMoney((double)initialDeposit * (profile.SpecialValuePercent / 100.0));
                BeginDeferredPayout(ownerHash, payout);
                return;
            }
        }
        catch
        {
        }
    }

    private static string FormatMoney(long value)
    {
        if (value < 0) value = 0;
        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private string GetHfpWavPath()
    {
        try
        {
            Directory.CreateDirectory(HfpAudioRoot);
        }
        catch
        {
            // Thực hiện bỏ qua lỗi nếu không tạo được thư mục
        }

        return Path.Combine(HfpAudioRoot, HfpIntroWavFileName);
    }

    private void PlayHfpIntroWav()
    {
        try
        {
            string path = GetHfpWavPath();
            if (!File.Exists(path))
                return;

            GTA.UI.Screen.ShowSubtitle(
                "Welcome to Meridian Trust, an investment fund dedicated to growing your wealth. How can we assist you with our services today?",
                6500
            );

            Task.Run(() =>
            {
                try
                {
                    using (var sp = new SoundPlayer(path))
                    {
                        sp.Load();
                        sp.PlaySync();
                    }
                }
                catch
                {
                    // Thực hiện bỏ qua lỗi khi phát âm thanh
                }
            });
        }
        catch
        {
            // Thực hiện bỏ qua lỗi tổng thể của hàm
        }
    }

    private HfpDailyProfile BuildDailyProfile(HedgeFundStrategy strategy, int wantedTier)
    {
        wantedTier = Math.Max(0, Math.Min(3, wantedTier));

        switch (strategy)
        {
            case HedgeFundStrategy.Safe:
                if (wantedTier >= 3)
                {
                    return new HfpDailyProfile
                    {
                        PositiveChance = 70.0,
                        NegativeChance = 30.0,
                        SpecialChance = 0.0,
                        PositiveMinPercent = 0.003,
                        PositiveMaxPercent = 0.010,
                        NegativeMinPercent = -0.020,
                        NegativeMaxPercent = -0.008,
                        SpecialMode = HfpSpecialMode.None,
                        SpecialValuePercent = 0.0
                    };
                }

                if (wantedTier == 2)
                {
                    return new HfpDailyProfile
                    {
                        PositiveChance = 75.0,
                        NegativeChance = 25.0,
                        SpecialChance = 0.0,
                        PositiveMinPercent = 0.005,
                        PositiveMaxPercent = 0.013,
                        NegativeMinPercent = -0.002,
                        NegativeMaxPercent = -0.0068,
                        SpecialMode = HfpSpecialMode.None,
                        SpecialValuePercent = 0.0
                    };
                }

                if (wantedTier == 1)
                {
                    return new HfpDailyProfile
                    {
                        PositiveChance = 78.0,
                        NegativeChance = 22.0,
                        SpecialChance = 0.0,
                        PositiveMinPercent = 0.005,
                        PositiveMaxPercent = 0.013,
                        NegativeMinPercent = -0.002,
                        NegativeMaxPercent = -0.0068,
                        SpecialMode = HfpSpecialMode.None,
                        SpecialValuePercent = 0.0
                    };
                }

                return new HfpDailyProfile
                {
                    PositiveChance = 80.0,
                    NegativeChance = 20.0,
                    SpecialChance = 0.0,
                    PositiveMinPercent = 0.005,
                    PositiveMaxPercent = 0.013,
                    NegativeMinPercent = -0.002,
                    NegativeMaxPercent = -0.0068,
                    SpecialMode = HfpSpecialMode.None,
                    SpecialValuePercent = 0.0
                };

            case HedgeFundStrategy.Attack:
                if (wantedTier >= 3)
                {
                    return new HfpDailyProfile
                    {
                        PositiveChance = 50.0,
                        NegativeChance = 40.0,
                        SpecialChance = 10.0,
                        PositiveMinPercent = 0.04,
                        PositiveMaxPercent = 0.08,
                        NegativeMinPercent = -0.05,
                        NegativeMaxPercent = -0.02,
                        SpecialMode = HfpSpecialMode.CrashLoss,
                        SpecialValuePercent = 15.0
                    };
                }

                if (wantedTier == 2)
                {
                    return new HfpDailyProfile
                    {
                        PositiveChance = 55.0,
                        NegativeChance = 38.0,
                        SpecialChance = 7.0,
                        PositiveMinPercent = 0.04,
                        PositiveMaxPercent = 0.08,
                        NegativeMinPercent = -0.05,
                        NegativeMaxPercent = -0.02,
                        SpecialMode = HfpSpecialMode.CrashLoss,
                        SpecialValuePercent = 15.0
                    };
                }

                if (wantedTier == 1)
                {
                    return new HfpDailyProfile
                    {
                        PositiveChance = 57.0,
                        NegativeChance = 36.0,
                        SpecialChance = 7.0,
                        PositiveMinPercent = 0.04,
                        PositiveMaxPercent = 0.08,
                        NegativeMinPercent = -0.05,
                        NegativeMaxPercent = -0.02,
                        SpecialMode = HfpSpecialMode.CrashLoss,
                        SpecialValuePercent = 15.0
                    };
                }

                return new HfpDailyProfile
                {
                    PositiveChance = 60.0,
                    NegativeChance = 35.0,
                    SpecialChance = 5.0,
                    PositiveMinPercent = 0.04,
                    PositiveMaxPercent = 0.08,
                    NegativeMinPercent = -0.05,
                    NegativeMaxPercent = -0.02,
                    SpecialMode = HfpSpecialMode.CrashLoss,
                    SpecialValuePercent = 15.0
                };

            case HedgeFundStrategy.Speculation:
                if (wantedTier >= 3)
                {
                    return new HfpDailyProfile
                    {
                        PositiveChance = 37.0,
                        NegativeChance = 45.0,
                        SpecialChance = 18.0,
                        PositiveMinPercent = 0.10,
                        PositiveMaxPercent = 0.15,
                        NegativeMinPercent = -0.12,
                        NegativeMaxPercent = -0.08,
                        SpecialMode = HfpSpecialMode.TerminateAndRefund,
                        SpecialValuePercent = 20.0
                    };
                }

                if (wantedTier == 2)
                {
                    return new HfpDailyProfile
                    {
                        PositiveChance = 40.0,
                        NegativeChance = 45.0,
                        SpecialChance = 15.0,
                        PositiveMinPercent = 0.10,
                        PositiveMaxPercent = 0.15,
                        NegativeMinPercent = -0.12,
                        NegativeMaxPercent = -0.08,
                        SpecialMode = HfpSpecialMode.TerminateAndRefund,
                        SpecialValuePercent = 20.0
                    };
                }

                if (wantedTier == 1)
                {
                    return new HfpDailyProfile
                    {
                        PositiveChance = 43.0,
                        NegativeChance = 42.0,
                        SpecialChance = 15.0,
                        PositiveMinPercent = 0.10,
                        PositiveMaxPercent = 0.15,
                        NegativeMinPercent = -0.12,
                        NegativeMaxPercent = -0.08,
                        SpecialMode = HfpSpecialMode.TerminateAndRefund,
                        SpecialValuePercent = 20.0
                    };
                }

                return new HfpDailyProfile
                {
                    PositiveChance = 45.0,
                    NegativeChance = 40.0,
                    SpecialChance = 15.0,
                    PositiveMinPercent = 0.10,
                    PositiveMaxPercent = 0.15,
                    NegativeMinPercent = -0.12,
                    NegativeMaxPercent = -0.08,
                    SpecialMode = HfpSpecialMode.TerminateAndRefund,
                    SpecialValuePercent = 20.0
                };

            default:
                return BuildDailyProfile(HedgeFundStrategy.Safe, wantedTier);
        }
    }

    private int GetWantedPenaltyTier(int wantedLevel)
    {
        wantedLevel = Math.Max(0, wantedLevel);
        if (wantedLevel >= 5) return 3;
        if (wantedLevel >= 3) return 2;
        if (wantedLevel >= 1) return 1;
        return 0;
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

    private string GetCharacterDisplayName(int ownerHash)
    {
        switch (ownerHash)
        {
            case FRANKLIN_HASH: return "Franklin Clinton";
            case MICHAEL_HASH: return "Michael De Santa";
            case TREVOR_HASH: return "Trevor Philips";
            default: return "N/A";
        }
    }

    private int GetCurrentGameDayKey()
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

    private static DateTime GetCurrentInGameDateTime()
    {
        try
        {
            int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);
            int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
            int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);
            int hour = Function.Call<int>(Hash.GET_CLOCK_HOURS);
            int minute = Function.Call<int>(Hash.GET_CLOCK_MINUTES);

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
            return new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        }
    }

    private int GetDayDistance(int startDayKey, int endDayKey)
    {
        try
        {
            if (startDayKey <= 0 || endDayKey <= 0)
                return 0;

            if (TryStampToDate(startDayKey, out DateTime start) && TryStampToDate(endDayKey, out DateTime end))
                return Math.Max(0, (end.Date - start.Date).Days);
        }
        catch
        {
        }

        return 0;
    }

    private static bool TryStampToDate(int stamp, out DateTime date)
    {
        date = default(DateTime);
        try
        {
            int year = stamp / 10000;
            int month = (stamp / 100) % 100;
            int day = stamp % 100;

            if (year < 1 || day < 1)
                return false;

            if (month >= 1 && month <= 12)
            {
                try
                {
                    date = new DateTime(year, month, day);
                    return true;
                }
                catch
                {
                }
            }

            if (month >= 0 && month <= 11)
            {
                try
                {
                    date = new DateTime(year, month + 1, day);
                    return true;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static long RoundMoney(double value)
    {
        try
        {
            if (value <= 0.0)
                return 0L;

            if (value > long.MaxValue)
                return long.MaxValue;

            return (long)Math.Round(value, MidpointRounding.AwayFromZero);
        }
        catch
        {
            return 0L;
        }
    }

    private static double RandomRange(double min, double max)
    {
        if (max < min)
        {
            double tmp = min;
            min = max;
            max = tmp;
        }

        return min + (_rng.NextDouble() * (max - min));
    }

    private static int SafeAddMoney(int currentMoney, long add)
    {
        try
        {
            long result = (long)currentMoney + Math.Max(0L, add);
            if (result > int.MaxValue)
                result = int.MaxValue;
            if (result < int.MinValue)
                result = int.MinValue;
            return (int)result;
        }
        catch
        {
            return currentMoney;
        }
    }

    private void UpdateMenuIfOpen()
    {
        try
        {
            if (_menu != null && _menu.Visible)
                _uiPool.Process();
        }
        catch
        {
        }
    }

    private void CloseMenuVisibleOnly()
    {
        try
        {
            if (_menu != null)
                _menu.Visible = false;
        }
        catch
        {
        }
    }

    private void CloseMenu()
    {
        try
        {
            CloseMenuVisibleOnly();
            _menuOpen = false;
            Interval = 1000;
        }
        catch
        {
        }
    }

    private void UpdateMenuMouseState()
    {
        try
        {
            if (_menu == null)
                return;

            _menu.MouseBehavior = MenuMouseBehavior.Disabled;
            _menu.ResetCursorWhenOpened = false;
            _menu.CloseOnInvalidClick = false;
            _menu.RotateCamera = true;
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

    private void TryClosePhone()
    {
        try
        {
            CustomiFruit.GetCurrentInstance()?.Close(0);
        }
        catch { }
    }

    private void ShowTrustNotification(string title, string subject, string message)
    {
        try
        {
            PlayFrontendSound("Text_Arrive_Tone", "Phone_SoundSet_Default");
            Notification.Show(NotificationIcon.MpStripclubPr, title, subject, message);
        }
        catch
        {
            try
            {
                Notification.Show(title + ": " + message);
            }
            catch
            {
                try { GTA.UI.Screen.ShowSubtitle(title + ": " + message, 5000); } catch { }
            }
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

    private string GetStateRootPath()
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
        }
        catch
        {
        }

        return DataRoot;
    }

    private string GetStateFile(int ownerHash)
    {
        return Path.Combine(GetStateRootPath(), $"hfp_state_{ownerHash}.dat");
    }

    private HedgeFundState CreateNewStateForCurrentCharacter()
    {
        return new HedgeFundState
        {
            OwnerHash = GetCurrentCharacterHash(),
            Active = false,
            AwaitingPayout = false,
            PendingPayout = 0L,
            CustomerName = GetCharacterDisplayName(GetCurrentCharacterHash()),
            Strategy = HedgeFundStrategy.Safe,
            InitialDeposit = 0L,
            CurrentBalance = 0L,
            DepositTimestamp = "",
            DepositDayKey = GetCurrentGameDayKey(),
            LastProcessedDayKey = GetCurrentGameDayKey(),
            CurrentDayWantedMax = Math.Max(0, Game.Player.WantedLevel),
            CurrentProfile = BuildDailyProfile(HedgeFundStrategy.Safe, 0)
        };
    }

    private void LoadStateForOwner(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
            {
                _state = null;
                return;
            }

            string file = GetStateFile(ownerHash);
            if (!File.Exists(file))
            {
                _state = CreateNewStateForCurrentCharacter();
                _state.OwnerHash = ownerHash;
                _state.Active = false;
                _state.Strategy = HedgeFundStrategy.Safe;
                _state.CurrentProfile = BuildDailyProfile(HedgeFundStrategy.Safe, 0);
                return;
            }

            _state = LoadStateFromFile(file, ownerHash) ?? CreateNewStateForCurrentCharacter();
            _state.OwnerHash = ownerHash;
            if (!Enum.IsDefined(typeof(HedgeFundStrategy), _state.Strategy))
                _state.Strategy = HedgeFundStrategy.Safe;

            if (_state.CurrentProfile.PositiveChance <= 0.0 && _state.CurrentProfile.NegativeChance <= 0.0 && _state.CurrentProfile.SpecialChance <= 0.0)
                _state.CurrentProfile = BuildDailyProfile(_state.Strategy, GetWantedPenaltyTier(_state.CurrentDayWantedMax));

            _lastSyncedDayKey = _state.LastProcessedDayKey;
            UpdateMenuText();
        }
        catch
        {
            _state = CreateNewStateForCurrentCharacter();
        }
    }

    private HedgeFundState LoadStateFromFile(string file, int ownerHash)
    {
        try
        {
            string[] p = File.ReadAllText(file, Encoding.UTF8).Split('|');
            if (p.Length < 20)
                return null;

            var state = new HedgeFundState
            {
                OwnerHash = ownerHash,
                Active = ParseBool(p[2]),
                Strategy = (HedgeFundStrategy)ParseInt(p[3], 0),
                CustomerName = p[4],
                InitialDeposit = ParseLong(p[5]),
                CurrentBalance = ParseLong(p[6]),
                DepositTimestamp = p[7],
                DepositDayKey = ParseInt(p[8], -1),
                LastProcessedDayKey = ParseInt(p[9], -1),
                CurrentDayWantedMax = ParseInt(p[10], 0),
                CurrentProfile = new HfpDailyProfile
                {
                    PositiveChance = ParseDouble(p[11]),
                    NegativeChance = ParseDouble(p[12]),
                    SpecialChance = ParseDouble(p[13]),
                    PositiveMinPercent = ParseDouble(p[14]),
                    PositiveMaxPercent = ParseDouble(p[15]),
                    NegativeMinPercent = ParseDouble(p[16]),
                    NegativeMaxPercent = ParseDouble(p[17]),
                    SpecialMode = (HfpSpecialMode)ParseInt(p[18], 0),
                    SpecialValuePercent = ParseDouble(p[19])
                },

                // NEW
                PendingPayout = ParseLong(p.Length > 20 ? p[20] : "0"),
                AwaitingPayout = ParseBool(p.Length > 21 ? p[21] : "0")
            };

            return state;
        }
        catch
        {
            return null;
        }
    }

    private bool SaveStateForOwner(int ownerHash)
    {
        try
        {
            if (ownerHash == 0 || _state == null)
                return false;

            Directory.CreateDirectory(GetStateRootPath());
            string file = GetStateFile(ownerHash);

            string payload = string.Join("|", new[]
            {
            "1",
            ownerHash.ToString(CultureInfo.InvariantCulture),
            _state.Active ? "1" : "0",
            ((int)_state.Strategy).ToString(CultureInfo.InvariantCulture),
            (_state.CustomerName ?? "").Replace("|", ""),
            _state.InitialDeposit.ToString(CultureInfo.InvariantCulture),
            _state.CurrentBalance.ToString(CultureInfo.InvariantCulture),
            (_state.DepositTimestamp ?? "").Replace("|", ""),
            _state.DepositDayKey.ToString(CultureInfo.InvariantCulture),
            _state.LastProcessedDayKey.ToString(CultureInfo.InvariantCulture),
            _state.CurrentDayWantedMax.ToString(CultureInfo.InvariantCulture),
            _state.CurrentProfile.PositiveChance.ToString(CultureInfo.InvariantCulture),
            _state.CurrentProfile.NegativeChance.ToString(CultureInfo.InvariantCulture),
            _state.CurrentProfile.SpecialChance.ToString(CultureInfo.InvariantCulture),
            _state.CurrentProfile.PositiveMinPercent.ToString(CultureInfo.InvariantCulture),
            _state.CurrentProfile.PositiveMaxPercent.ToString(CultureInfo.InvariantCulture),
            _state.CurrentProfile.NegativeMinPercent.ToString(CultureInfo.InvariantCulture),
            _state.CurrentProfile.NegativeMaxPercent.ToString(CultureInfo.InvariantCulture),
            ((int)_state.CurrentProfile.SpecialMode).ToString(CultureInfo.InvariantCulture),
            _state.CurrentProfile.SpecialValuePercent.ToString(CultureInfo.InvariantCulture),
            _state.PendingPayout.ToString(CultureInfo.InvariantCulture),
            _state.AwaitingPayout ? "1" : "0"
        });

            File.WriteAllText(file, payload, Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DeleteState(int ownerHash)
    {
        try
        {
            string file = GetStateFile(ownerHash);
            if (File.Exists(file))
                File.Delete(file);
        }
        catch
        {
        }
    }

    private bool ParseBool(string s)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(s))
                return false;
            s = s.Trim();
            return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private int ParseInt(string s, int fallback = 0)
    {
        try
        {
            if (int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return v;
        }
        catch
        {
        }

        return fallback;
    }

    private long ParseLong(string s)
    {
        try
        {
            if (long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                return v;
        }
        catch
        {
        }

        return 0L;
    }

    private double ParseDouble(string s)
    {
        try
        {
            if (double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return v;
        }
        catch
        {
        }

        return 0.0;
    }
}