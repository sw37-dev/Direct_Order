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
using System.Media;
using System.Threading.Tasks;

public class CreditDefaultSwap : Script
{
    private const int FRANKLIN_HASH = -1692214353;
    private const int MICHAEL_HASH = 225514697;
    private const int TREVOR_HASH = -1686040670;

    private const int CONTACT_DIAL_TIMEOUT_MS = 2000;
    private const int HELP_PROMPT_TIMEOUT_MS = 15000;
    private const int PAYMENT_CHECK_INTERVAL_MS = 15000;

    private const decimal DAILY_FEE_RATE = 0.0004m; // 0.04%

    private const int CDS_INTRO_DELAY_MS = 800;

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

    private readonly string CDSAudioRoot = Path.Combine(
        GetScriptsDirectory(),
        "Audio"
    );

    private readonly string CDSIntroFileName = "CDS.wav";
    private readonly string CDSDeclineFileName = "CDS_Decline.wav";

    private bool _introWavPlaying = false;
    private bool _introIsCancelFlow = false;
    private int _introWavStartedAt = 0;

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "TPI Financial");

    private static readonly string PersistentManagerFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PersistentManager");

    private static readonly string PersistentVehiclesFile = Path.Combine(PersistentManagerFolder, "persistent_vehicles.txt");
    private static readonly string LoanStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Fleeca Bank Loan");

    private static readonly object _sync = new object();

    private readonly ObjectPool _uiPool = new ObjectPool();
    private NativeMenu _menu;
    private bool _menuReady;
    private iFruitContact _contact;
    private CustomiFruit _phoneInstance;
    private bool _contactAdded;
    private bool _contactAnsweredBound;

    private bool _promptPending;
    private bool _promptIsCancelFlow;
    private int _promptDueAt;
    private int _promptOwnerHash;

    private int _menuOwnerHash;
    private bool _menuOpen;

    private bool _stateLoaded;
    private int _activeOwnerHash;
    private CdsState _state = new CdsState();

    private int _nextPaymentCheckAt;
    private int _nextSnapshotRefreshAt;
    private int _cachedSnapshotOwnerHash;
    private CollateralSnapshot _snapshot = new CollateralSnapshot();

    private const string CDSMessageSoundName = "Text_Arrive_Tone";
    private const string CDSMessageSoundSet = "Phone_SoundSet_Default";

    private static string CONTACT_NAME => L("CDS_ContactName", "TPIF");

    private static string L(string key, string fallback) => Language.Get(key, fallback);

    private sealed class CdsState
    {
        public int OwnerHash;
        public bool Active;
        public long DailyFee;
        public decimal FrozenRate;
        public int PaymentWindowStartHour = -1;
        public int PaymentWindowEndHour = -1;
        public long CollateralValueAtPurchase;
        public int CollateralCountAtPurchase;
        public string CustomerName = "";
        public int Version = 1;
        public int LastPaidDayKey = -1;
        public int LastAttemptDayKey = -1;

        // Mốc giờ in-game, lưu dạng ISO để không phụ thuộc thời gian thực
        public string CoverageActivatedAt = "";
        public string CoverageExpiresAt = "";
    }

    private sealed class CollateralSnapshot
    {
        public int OwnerHash;
        public string CustomerName = "";
        public int CollateralCount;
        public long CollateralValue;
        public decimal CurrentReferenceRate;
        public bool HasActiveLoan;
        public bool HasActiveCDS;
        public int LastPaidDayKey;
    }

    private sealed class CollateralVehicleEntry
    {
        public uint ModelHash;
        public string DisplayName = "";
        public string Plate = "";
        public long PurchasePrice;
        public bool Locked;
        public Vector3 Position;
        public float Heading;
    }

    private sealed class CdsMenuItem : NativeItem
    {
        public CdsMenuItem(string title, string description = "")
            : base(title, description)
        {
        }

        public override void Recalculate(System.Drawing.PointF pos, System.Drawing.SizeF size, bool selected)
        {
            base.Recalculate(pos, size, selected);
            try { UpdateColors(); } catch { }
        }
    }

    public CreditDefaultSwap()
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

            EnsureContactRegistered();
            RefreshCurrentSnapshotIfNeeded();

            if (_introWavPlaying)
            {
                if (Game.GameTime - _introWavStartedAt >= CDS_INTRO_DELAY_MS)
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

            if (_promptPending)
                Interval = 0;
            else if (_menuOpen)
                Interval = 0;
            else
                Interval = 1000;

            UpdatePromptState();
            ProcessDailyPaymentWindow();
            UpdateUi();
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
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
                {
                    AcceptPrompt();
                    return;
                }

                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                {
                    DeclinePrompt();
                    return;
                }
            }

            if (_menuOpen && (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape))
            {
                CloseMenu();
                return;
            }
        }
        catch { }
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

    private static string FormatInGameDateTime(DateTime dt)
    {
        return dt.ToString("o", CultureInfo.InvariantCulture);
    }

    private static bool TryParseInGameDateTime(string s, out DateTime dt)
    {
        dt = default(DateTime);

        try
        {
            if (string.IsNullOrWhiteSpace(s))
                return false;

            return DateTime.TryParseExact(
                s.Trim(),
                "o",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out dt);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCoverageActiveNow(CdsState state)
    {
        try
        {
            if (state == null || !state.Active)
                return false;

            if (!TryParseInGameDateTime(state.CoverageExpiresAt, out DateTime expiresAt))
                return false;

            return GetCurrentInGameDateTime() < expiresAt;
        }
        catch
        {
            return false;
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
            }

            if (_contactAdded)
            {
                if (_contact != null)
                    _contact.Active = CanUseContactForCurrentCharacter();
                if (_contact != null && !_contactAnsweredBound)
                {
                    _contact.Answered += OnContactAnswered;
                    _contactAnsweredBound = true;
                }
                return;
            }

            _contact = phone.Contacts.FirstOrDefault(c =>
                c != null && string.Equals(c.Name, CONTACT_NAME, StringComparison.OrdinalIgnoreCase));

            if (_contact == null)
            {
                _contact = new iFruitContact(CONTACT_NAME)
                {
                    Active = CanUseContactForCurrentCharacter(),
                    DialTimeout = CONTACT_DIAL_TIMEOUT_MS,
                    Bold = false,
                    Icon = ContactIcon.Bugstars
                };
                _contact.Answered += OnContactAnswered;
                _contactAnsweredBound = true;
                phone.Contacts.Add(_contact);
            }
            else
            {
                _contact.Active = CanUseContactForCurrentCharacter();
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

    private bool CanUseContactForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return false;

            var snap = BuildSnapshot(ownerHash);
            return snap.HasActiveLoan;
        }
        catch
        {
            return false;
        }
    }

    private void OnContactAnswered(iFruitContact sender)
    {
        try
        {
            RefreshCurrentSnapshotIfNeeded(force: true);

            if (!_snapshot.HasActiveLoan || _snapshot.OwnerHash == 0)
            {
                ShowSubtitle(L("CDS_NoLoan", "Không có khoản vay của ngân hàng hoặc tổ chức nào đang hoạt động."));
                TryClosePhone();
                return;
            }

            if (IsPolicyActiveForCurrentOwner())
            {
                _promptIsCancelFlow = true;
                _promptPending = false;
                _promptOwnerHash = _snapshot.OwnerHash;
                _promptDueAt = 0;

                StartCdsIntroSequence(decline: true);
                TryClosePhone();
                return;
            }

            if (_snapshot.CollateralCount <= 0)
            {
                ShowSubtitle(L("CDS_NoCollateral", "Không có ~r~phương tiện thế chấp hợp lệ~s~ để mua CDS."));
                TryClosePhone();
                return;
            }

            _promptIsCancelFlow = false;
            _promptPending = false;
            _promptOwnerHash = _snapshot.OwnerHash;
            _promptDueAt = 0;

            StartCdsIntroSequence(decline: false);
            TryClosePhone();
        }
        catch
        {
            TryClosePhone();
        }
    }

    private void UpdatePromptState()
    {
        try
        {
            if (!_promptPending)
                return;

            if (Game.GameTime < _promptDueAt)
                return;

            if (_promptIsCancelFlow)
            {
                ShowHelpText(L(
                    "CDS_Help_CancelFlow",
                    "Bạn đang sử dụng ~g~Hợp đồng Chuyển đổi Rủi ro Tín dụng~s~, có chắc sẽ hủy?"));
            }
            else
            {
                ShowHelpText(L(
                    "CDS_Help_BuyFlow",
                    "Nếu mua gói bảo hiểm này, bạn cần ~y~trả phí định kỳ hằng ngày~s~ để bảo vệ khoản vay của mình?"));
            }
        }
        catch { }
    }

    private void AcceptPrompt()
    {
        try
        {
            if (!_promptPending)
                return;

            int ownerHash = _promptOwnerHash;
            _promptPending = false;
            _promptDueAt = 0;

            if (_promptIsCancelFlow)
            {
                DeactivatePolicy(ownerHash, showMessage: true);
                _promptIsCancelFlow = false;
                TryClosePhone();
                return;
            }

            _promptIsCancelFlow = false;
            RefreshCurrentSnapshotIfNeeded(force: true);

            if (_snapshot.OwnerHash == 0 || !_snapshot.HasActiveLoan)
            {
                ShowSubtitle(L("CDS_NoLoan", "Không có khoản vay của ngân hàng hay tổ chức tài chính nào đang hoạt động."));
                TryClosePhone();
                return;
            }

            OpenPurchaseMenu(ownerHash);
            TryClosePhone();
        }
        catch
        {
            _promptPending = false;
            _promptIsCancelFlow = false;
            _promptDueAt = 0;
            TryClosePhone();
        }
    }

    private void DeclinePrompt()
    {
        try
        {
            _promptPending = false;
            _promptIsCancelFlow = false;
            _promptDueAt = 0;
            TryClosePhone();
        }
        catch
        {
        }
    }

    private void UpdateUi()
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

    private void OpenPurchaseMenu(int ownerHash)
    {
        try
        {
            RefreshCurrentSnapshotIfNeeded(force: true);

            EnsureMenuCreated();
            BuildPurchaseMenu(ownerHash);

            _menuOwnerHash = ownerHash;
            _menuOpen = true;

            if (_menu != null)
                _menu.Visible = true;

            Interval = 0;
            _uiPool.Process();
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
                L("CDS_Menu_Title", "CDS"),
                L("CDS_Menu_Subtitle", "CHI TIẾT BẢO HIỂM CDS"));
            ConfigureKeyboardOnlyMenu(_menu);
            _uiPool.Add(_menu);
            _menuReady = true;
        }
        catch
        {
        }
    }

    private void BuildPurchaseMenu(int ownerHash)
    {
        try
        {
            if (_menu == null)
                return;

            _menu.Clear();

            var snap = BuildSnapshot(ownerHash);
            string customer = string.IsNullOrWhiteSpace(snap.CustomerName) ? "N/A" : snap.CustomerName;

            var itemCustomer = new CdsMenuItem(
                string.Format(CultureInfo.InvariantCulture, L("CDS_Menu_ItemCustomer", "1. Tên khách hàng: {0}"), customer),
                L("CDS_Menu_ItemCustomerDesc", "Tên nhân vật hiện tại"));
            var itemProduct = new CdsMenuItem(
                L("CDS_Menu_ItemProduct", "2. Sản phẩm: Hợp đồng Hoán đổi Rủi ro"),
                L("CDS_Menu_ItemProductDesc", "Sản phẩm Hợp đồng Chuyển đổi Rủi ro Tín dụng này sẽ bảo vệ khoản vay theo ngày"));
            var itemCollateralCount = new CdsMenuItem(
                string.Format(CultureInfo.InvariantCulture, L("CDS_Menu_ItemCollateralCount", "3. Phương tiện thế chấp: {0}"), snap.CollateralCount),
                L("CDS_Menu_ItemCollateralCountDesc", "Số phương tiện thế chấp hiện tại mà ngân hàng Fleeca đang nắm giữ"));
            var itemCollateralValue = new CdsMenuItem(
                string.Format(CultureInfo.InvariantCulture, L("CDS_Menu_ItemCollateralValue", "4. Tổng giá trị thế chấp: {0}"), FormatMoney(snap.CollateralValue)),
                L("CDS_Menu_ItemCollateralValueDesc", "Đây là tổng giá trị của các phương tiện thế chấp hiện tại tại ngân hàng Fleeca"));
            var itemFee = new CdsMenuItem(
                string.Format(CultureInfo.InvariantCulture, L("CDS_Menu_ItemFee", "5. Giá sản phẩm: {0}"), FormatMoney(ComputeDailyFee(snap.CollateralValue))),
                L("CDS_Menu_ItemFeeDesc", "Đây là phí định kỳ cần trả hằng ngày để duy trì sự ổn định của lãi suất tại ngân hàng/tổ chức tài chính"));

            var confirm = new NativeItem(
                L("CDS_Menu_Confirm", "Xác nhận mua CDS"),
                L("CDS_Menu_ConfirmDesc", "Mua gói bảo hiểm CDS này và kích hoạt thu phí định kỳ"));
            var cancel = new NativeItem(
                L("CDS_Menu_Cancel", "Hủy bỏ sản phẩm CDS"),
                L("CDS_Menu_CancelDesc", "Đóng menu và không mua"));

            confirm.Activated += (s, e) =>
            {
                try
                {
                    ActivatePolicy(ownerHash);
                    CloseMenu();
                }
                catch
                {
                    CloseMenu();
                }
            };

            cancel.Activated += (s, e) => CloseMenu();

            _menu.Add(itemCustomer);
            _menu.Add(itemProduct);
            _menu.Add(itemCollateralCount);
            _menu.Add(itemCollateralValue);
            _menu.Add(itemFee);
            _menu.Add(confirm);
            _menu.Add(cancel);
        }
        catch
        {
        }
    }

    private void ActivatePolicy(int ownerHash)
    {
        try
        {
            RefreshCurrentSnapshotIfNeeded(force: true);
            if (_snapshot.OwnerHash == 0 || !_snapshot.HasActiveLoan)
            {
                ShowSubtitle(L("CDS_NoLoan", "Không có khoản vay của ngân hàng hay tổ chức tài chính nào đang hoạt động."));
                return;
            }

            if (_snapshot.CollateralCount <= 0 || _snapshot.CollateralValue <= 0)
            {
                ShowSubtitle(L("CDS_NoCollateral", "Không có ~r~phương tiện thế chấp hợp lệ~s~ để mua CDS."));
                return;
            }

            // --- Cập nhật đoạn tạo state theo hướng dẫn ---
            int currentHour = GetCurrentHour();
            int currentDayKey = GetCurrentGameDayKey();
            DateTime now = GetCurrentInGameDateTime();

            var liveRate = GetCurrentLiveLoanRate(ownerHash);
            if (liveRate <= 0m)
                liveRate = 0.015m;

            var state = new CdsState
            {
                OwnerHash = ownerHash,
                Active = true,
                DailyFee = ComputeDailyFee(_snapshot.CollateralValue),
                FrozenRate = liveRate,
                PaymentWindowStartHour = currentHour,
                PaymentWindowEndHour = (currentHour + 1) % 24,
                LastPaidDayKey = currentDayKey,
                LastAttemptDayKey = currentDayKey,
                CoverageActivatedAt = FormatInGameDateTime(now),
                CoverageExpiresAt = FormatInGameDateTime(now.AddHours(24)),
                CollateralCountAtPurchase = _snapshot.CollateralCount,
                CollateralValueAtPurchase = _snapshot.CollateralValue,
                CustomerName = _snapshot.CustomerName,
                Version = 2
            };

            SaveState(state);
            _state = state;
            _activeOwnerHash = ownerHash;
            _stateLoaded = true;

            ShowFeed(
                L("CDS_Feed_Sender", "TPIF"),
                L("CDS_Feed_Subject_Activated", "CDS"),
                string.Format(CultureInfo.InvariantCulture,
                    L("CDS_Feed_ActivatedBody",
                    "Đã kích hoạt gói bảo hiểm CDS này. Phí hàng ngày sẽ là {0}. Khung thu phí nằm khoảng {1:00}:00 - {2:00}:00."),
                    FormatMoney(state.DailyFee),
                    state.PaymentWindowStartHour,
                    state.PaymentWindowEndHour));

            RefreshCurrentSnapshotIfNeeded(force: true);
        }
        catch (Exception ex)
        {
            ShowSubtitle("CDS ~r~failed~s~.");
            try { Log("ActivatePolicy failed: " + ex); } catch { }
        }
    }

    private void DeactivatePolicy(int ownerHash, bool showMessage)
    {
        try
        {
            if (ownerHash == 0)
                return;

            DeleteState(ownerHash);

            if (_state != null && _state.OwnerHash == ownerHash)
            {
                _state.Active = false;
                _state.DailyFee = 0;
                _state.FrozenRate = 0m;
                _state.PaymentWindowStartHour = -1;
                _state.PaymentWindowEndHour = -1;
                _state.LastPaidDayKey = -1;
                _state.LastAttemptDayKey = -1;
                _state.CollateralValueAtPurchase = 0;
                _state.CollateralCountAtPurchase = 0;
            }

            if (showMessage)
            {
                ShowFeed(
                    L("CDS_Feed_Sender", "TPI Financial"),
                    L("CDS_Feed_Subject_Deactivated", "CDS"),
                    L("CDS_Feed_DeactivatedBody", "Khách hàng đã chấm dứt hợp đồng CDS này. Lãi suất của ngân hàng và tổ chức tài chính sẽ hoạt động bình thường trở lại."));
            }
        }
        catch (Exception ex)
        {
            try { Log("DeactivatePolicy failed: " + ex); } catch { }
        }
    }

    private void ProcessDailyPaymentWindow()
    {
        try
        {
            if (_nextPaymentCheckAt > 0 && Game.GameTime < _nextPaymentCheckAt)
                return;

            _nextPaymentCheckAt = Game.GameTime + PAYMENT_CHECK_INTERVAL_MS;

            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            if (!TryLoadState(ownerHash, out CdsState state) || state == null || !state.Active)
                return;

            int currentDayKey = GetCurrentGameDayKey();
            if (currentDayKey < 0)
                return;

            int currentHour = GetCurrentHour();
            int currentMinute = GetCurrentMinute();

            if (!IsInsidePaymentWindow(currentHour, currentMinute, state.PaymentWindowStartHour, state.PaymentWindowEndHour))
                return;

            // Chỉ thử 1 lần mỗi ngày game trong khung giờ đó
            if (state.LastAttemptDayKey == currentDayKey)
                return;

            state.LastAttemptDayKey = currentDayKey;

            long fee = Math.Max(0L, state.DailyFee);
            long currentMoney = Math.Max(0, Game.Player.Money);

            if (fee > 0L && currentMoney < fee)
            {
                state.Active = false;
                state.LastPaidDayKey = -1;
                state.CoverageActivatedAt = "";
                state.CoverageExpiresAt = "";
                SaveState(state);
                _state = state;

                ShowFeed(
                    L("CDS_Feed_Sender", "TPIF"),
                    L("CDS_Feed_Subject_Fail", "Bảo hiểm CDS"),
                    string.Format(CultureInfo.InvariantCulture,
                        L("CDS_Feed_FailBody",
                        "Không thu được phí bảo hiểm CDS trong hôm nay ({0}). Bảo hiểm hôm nay không có hiệu lực."),
                        FormatMoney(fee)));

                return;
            }

            if (fee > 0L)
            {
                Game.Player.Money -= (int)Math.Min(fee, int.MaxValue);
                PlaySound();
            }

            DateTime now = GetCurrentInGameDateTime();
            state.LastPaidDayKey = currentDayKey;
            state.CoverageActivatedAt = FormatInGameDateTime(now);
            state.CoverageExpiresAt = FormatInGameDateTime(now.AddHours(24));
            state.Active = true;

            SaveState(state);
            _state = state;

            ShowFeed(
                L("CDS_Feed_Sender", "TPI Financial"),
                L("CDS_Feed_Subject_Paid", "Bảo hiểm CDS"),
                string.Format(CultureInfo.InvariantCulture,
                    L("CDS_Feed_PaidBody",
                    "TPIF đã thành công thu phí bảo hiểm CDS {0}. Bảo hiểm này đang có hiệu lực trong 1 ngày."),
                    FormatMoney(fee)));

            RefreshCurrentSnapshotIfNeeded(force: true);
        }
        catch (Exception ex)
        {
            try { Log("ProcessDailyPaymentWindow failed: " + ex); } catch { }
        }
    }

    private void RefreshCurrentSnapshotIfNeeded(bool force = false)
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            if (!force && _cachedSnapshotOwnerHash == ownerHash && Game.GameTime < _nextSnapshotRefreshAt)
                return;

            _snapshot = BuildSnapshot(ownerHash);
            _cachedSnapshotOwnerHash = ownerHash;
            _nextSnapshotRefreshAt = Game.GameTime + 5000;

            if (TryLoadState(ownerHash, out CdsState state))
            {
                _state = state;
                _activeOwnerHash = ownerHash;
                _stateLoaded = true;
                _snapshot.HasActiveCDS = state.Active;
                _snapshot.LastPaidDayKey = state.LastPaidDayKey;
            }
            else
            {
                _state = new CdsState { OwnerHash = ownerHash };
                _activeOwnerHash = ownerHash;
                _stateLoaded = true;
                _snapshot.HasActiveCDS = false;
            }
        }
        catch
        {
        }
    }

    private CollateralSnapshot BuildSnapshot(int ownerHash)
    {
        var snapshot = new CollateralSnapshot
        {
            OwnerHash = ownerHash,
            CustomerName = GetCharacterDisplayName(ownerHash),
            HasActiveLoan = HasActiveLoan(ownerHash)
        };

        var vehicles = GetCollateralVehicles(ownerHash);
        snapshot.CollateralCount = vehicles.Count;
        snapshot.CollateralValue = vehicles.Sum(v => Math.Max(0L, v.PurchasePrice));
        snapshot.CurrentReferenceRate = GetCurrentLiveLoanRate(ownerHash);
        snapshot.HasActiveCDS = IsPolicyActiveForOwner(ownerHash);
        snapshot.LastPaidDayKey = GetStateForOwner(ownerHash)?.LastPaidDayKey ?? -1;

        return snapshot;
    }

    private static long ComputeDailyFee(long totalCollateralValue)
    {
        try
        {
            if (totalCollateralValue <= 0)
                return 0L;

            decimal fee = (decimal)totalCollateralValue * DAILY_FEE_RATE;
            return (long)Math.Round(fee, MidpointRounding.AwayFromZero);
        }
        catch
        {
            return 0L;
        }
    }

    private static string FormatMoney(long value)
    {
        if (value < 0)
            value = 0;
        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private string GetCdsAudioPath(bool decline)
    {
        try
        {
            Directory.CreateDirectory(CDSAudioRoot);
        }
        catch
        {
        }

        return Path.Combine(CDSAudioRoot, decline ? CDSDeclineFileName : CDSIntroFileName);
    }

    private void PlayCdsIntroWav(bool decline)
    {
        try
        {
            string path = GetCdsAudioPath(decline);

            if (!File.Exists(path))
                return;

            if (decline)
            {
                GTA.UI.Screen.ShowSubtitle(
                    "Our records show you are an active insurance policyholder. Are you calling to temporarily suspend your service?",
                    7200
                );
            }
            else
            {
                GTA.UI.Screen.ShowSubtitle(
                    "I represent our insurance organization. Do you need to purchase insurance for your collateral vehicles at Fleeca Bank?",
                    6100
                );
            }

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
                }
            });
        }
        catch
        {
        }
    }

    private void StartCdsIntroSequence(bool decline)
    {
        try
        {
            _introWavPlaying = true;
            _introIsCancelFlow = decline;
            _introWavStartedAt = Game.GameTime;
            PlayCdsIntroWav(decline);
        }
        catch
        {
            _introWavPlaying = false;
        }
    }

    private static bool HasActiveLoan(int ownerHash)
    {
        try
        {
            var loan = ReadLoanState(ownerHash);
            return loan.HasValue && loan.Value.active && loan.Value.remainingDebt > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPolicyActiveForOwner(int ownerHash)
    {
        try
        {
            var state = GetStateForOwner(ownerHash);
            return state != null && IsCoverageActiveNow(state);
        }
        catch
        {
            return false;
        }
    }

    private bool IsPolicyActiveForCurrentOwner()
    {
        int ownerHash = GetCurrentCharacterHash();
        return ownerHash != 0 && IsPolicyActiveForOwner(ownerHash);
    }

    private static CdsState GetStateForOwner(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return null;

            string file = GetStateFile(ownerHash);
            if (!File.Exists(file))
                return null;

            string[] p = File.ReadAllText(file, Encoding.UTF8).Split('|');
            if (p.Length < 10)
                return null;

            var state = new CdsState
            {
                OwnerHash = ownerHash,
                Active = ParseBool(p[1]),
                DailyFee = ParseLong(p[2]),
                FrozenRate = ParseDecimal(p[3]),
                PaymentWindowStartHour = ParseInt(p[4], -1),
                PaymentWindowEndHour = ParseInt(p[5], -1),
                LastPaidDayKey = ParseInt(p[6], -1),
                LastAttemptDayKey = ParseInt(p[7], -1),
                CollateralCountAtPurchase = ParseInt(p[8], 0),
                CollateralValueAtPurchase = ParseLong(p[9]),
                CustomerName = p.Length > 10 ? p[10] : "",
                Version = p.Length > 11 ? ParseInt(p[11], 1) : 1,
                CoverageActivatedAt = p.Length > 12 ? p[12] : "",
                CoverageExpiresAt = p.Length > 13 ? p[13] : ""
            };
            return state;
        }
        catch
        {
            return null;
        }
    }

    private bool TryLoadState(int ownerHash, out CdsState state)
    {
        state = GetStateForOwner(ownerHash);
        return state != null;
    }

    private static void SaveState(CdsState state)
    {
        try
        {
            if (state == null || state.OwnerHash == 0)
                return;

            Directory.CreateDirectory(DataFolder);
            string file = GetStateFile(state.OwnerHash);

            var payload = string.Join("|", new[]
            {
            "2",
            state.Active ? "1" : "0",
            state.DailyFee.ToString(CultureInfo.InvariantCulture),
            state.FrozenRate.ToString(CultureInfo.InvariantCulture),
            state.PaymentWindowStartHour.ToString(CultureInfo.InvariantCulture),
            state.PaymentWindowEndHour.ToString(CultureInfo.InvariantCulture),
            state.LastPaidDayKey.ToString(CultureInfo.InvariantCulture),
            state.LastAttemptDayKey.ToString(CultureInfo.InvariantCulture),
            state.CollateralCountAtPurchase.ToString(CultureInfo.InvariantCulture),
            state.CollateralValueAtPurchase.ToString(CultureInfo.InvariantCulture),
            (state.CustomerName ?? "").Replace("|", ""),
            state.Version.ToString(CultureInfo.InvariantCulture),
            (state.CoverageActivatedAt ?? ""),
            (state.CoverageExpiresAt ?? "")
        });

            File.WriteAllText(file, payload, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void DeleteState(int ownerHash)
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

    private static string GetStateFile(int ownerHash)
    {
        Directory.CreateDirectory(DataFolder);
        return Path.Combine(DataFolder, $"cds_state_{ownerHash}.dat");
    }

    private static (long principal, long remainingDebt, long dailyDue, bool active)? ReadLoanState(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return null;

            string file = Path.Combine(LoanStateRoot, $"loan_state_{ownerHash}.dat");
            if (!File.Exists(file))
                return null;

            string[] p = File.ReadAllText(file, Encoding.UTF8).Split('|');
            if (p.Length < 4)
                return null;

            long principal = ParseLong(p[0]);
            long remaining = ParseLong(p[1]);
            long dailyDue = ParseLong(p[2]);
            bool active = ParseBool(p[3]) && remaining > 0;

            if (!active || remaining <= 0)
                return null;

            return (principal, remaining, dailyDue, active);
        }
        catch
        {
            return null;
        }
    }

    private static decimal GetCurrentLiveLoanRate(int ownerHash)
    {
        try
        {
            var loan = ReadLoanState(ownerHash);
            if (!loan.HasValue)
                return 0m;

            long principal = loan.Value.principal;
            long dailyDue = loan.Value.dailyDue;

            if (principal <= 0 || dailyDue <= 0)
                return 0m;

            return Math.Max(0m, (decimal)dailyDue / (decimal)principal);
        }
        catch
        {
            return 0m;
        }
    }

    private static List<CollateralVehicleEntry> GetCollateralVehicles(int ownerHash)
    {
        var result = new List<CollateralVehicleEntry>();

        try
        {
            var runtime = GetRuntimeVehiclesFromPersistentManager();
            if (runtime != null && runtime.Count > 0)
            {
                foreach (var e in runtime)
                {
                    try
                    {
                        if (e == null)
                            continue;

                        if (!TryGetIntField(e, "OwnerModelHash", out int entryOwner) || entryOwner != ownerHash)
                            continue;

                        if (!TryGetBoolField(e, "IsCollateralLocked", out bool locked) || !locked)
                            continue;

                        uint modelHash = TryGetUIntField(e, "ModelHash");

                        var item = new CollateralVehicleEntry
                        {
                            ModelHash = modelHash,
                            Plate = TryGetStringField(e, "Plate"),
                            PurchasePrice = TryGetLongField(e, "PurchasePrice"),
                            Locked = locked,
                            Position = TryGetVector3Field(e, "Position"),
                            Heading = TryGetFloatField(e, "Heading"),
                            DisplayName = GetDisplayNameFromModel(modelHash)
                        };

                        result.Add(item);
                    }
                    catch
                    {
                    }
                }

                return result;
            }
        }
        catch
        {
        }

        // File fallback
        try
        {
            if (!File.Exists(PersistentVehiclesFile))
                return result;

            foreach (string raw in File.ReadAllLines(PersistentVehiclesFile, Encoding.UTF8))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    var p = raw.Split('|');
                    if (p.Length < 22)
                        continue;

                    uint modelHash = ParseModelHash(p[0]);
                    if (modelHash == 0)
                        continue;

                    int fileOwner = ParseInt(p[6], 0);
                    if (fileOwner != ownerHash)
                        continue;

                    bool locked = ParseBool(p[21]);
                    if (!locked)
                        continue;

                    long price = ParseLong(p[10]);
                    string plate = p.Length > 5 ? p[5] : "";
                    float x = ParseFloat(p[1]);
                    float y = ParseFloat(p[2]);
                    float z = ParseFloat(p[3]);
                    float heading = ParseFloat(p[4]);

                    result.Add(new CollateralVehicleEntry
                    {
                        ModelHash = modelHash,
                        DisplayName = ExtractVehicleDisplayName(p[0]),
                        Plate = plate,
                        PurchasePrice = price,
                        Locked = true,
                        Position = new Vector3(x, y, z),
                        Heading = heading
                    });
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static List<object> GetRuntimeVehiclesFromPersistentManager()
    {
        try
        {
            Type pmType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("PersistentManager", false, true))
                .FirstOrDefault(t => t != null);

            if (pmType == null)
                return null;

            FieldInfo field = pmType.GetField("_persistVehicles", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
                return null;

            var list = field.GetValue(null) as System.Collections.IEnumerable;
            if (list == null)
                return null;

            return list.Cast<object>().ToList();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetIntField(object entry, string fieldName, out int value)
    {
        value = 0;
        try
        {
            var f = entry.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
                return false;

            object v = f.GetValue(entry);
            if (v is int i)
            {
                value = i;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool TryGetLongField(object entry, string fieldName, out long value)
    {
        value = 0;
        try
        {
            var f = entry.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
                return false;

            object v = f.GetValue(entry);
            if (v is long l)
            {
                value = l;
                return true;
            }
            if (v is int i)
            {
                value = i;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool TryGetBoolField(object entry, string fieldName, out bool value)
    {
        value = false;
        try
        {
            var f = entry.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
                return false;

            object v = f.GetValue(entry);
            if (v is bool b)
            {
                value = b;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool TryGetFloatField(object entry, string fieldName, out float value)
    {
        value = 0f;
        try
        {
            var f = entry.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
                return false;

            object v = f.GetValue(entry);
            if (v is float fl)
            {
                value = fl;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool TryGetVector3Field(object entry, string fieldName, out Vector3 value)
    {
        value = Vector3.Zero;
        try
        {
            var f = entry.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
                return false;

            object v = f.GetValue(entry);
            if (v is Vector3 vec)
            {
                value = vec;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool TryGetStringField(object entry, string fieldName, out string value)
    {
        value = "";
        try
        {
            var f = entry.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
                return false;

            object v = f.GetValue(entry);
            if (v is string s)
            {
                value = s;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static uint TryGetUIntField(object entry, string fieldName)
    {
        try
        {
            var f = entry.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
                return 0u;

            object v = f.GetValue(entry);
            if (v is uint u) return u;
            if (v is int i && i >= 0) return (uint)i;
            if (v is long l && l >= 0) return (uint)l;
        }
        catch
        {
        }
        return 0u;
    }

    private static string TryGetStringField(object entry, string fieldName)
    {
        TryGetStringField(entry, fieldName, out string value);
        return value;
    }

    private static Vector3 TryGetVector3Field(object entry, string fieldName)
    {
        TryGetVector3Field(entry, fieldName, out Vector3 value);
        return value;
    }

    private static long TryGetLongField(object entry, string fieldName)
    {
        TryGetLongField(entry, fieldName, out long value);
        return value;
    }

    private static float TryGetFloatField(object entry, string fieldName)
    {
        TryGetFloatField(entry, fieldName, out float value);
        return value;
    }

    private static string GetDisplayNameFromModel(uint modelHash)
    {
        try
        {
            Type pmType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("PersistentManager", false, true))
                .FirstOrDefault(t => t != null);

            if (pmType != null)
            {
                MethodInfo m = pmType.GetMethod("GetVehicleDisplayNamePublic", BindingFlags.Public | BindingFlags.Static);
                if (m != null)
                    return Convert.ToString(m.Invoke(null, new object[] { modelHash }));
            }
        }
        catch
        {
        }

        return modelHash == 0 ? "N/A" : $"0x{modelHash:X}";
    }

    private static string ExtractVehicleDisplayName(string raw)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(raw)) return "N/A";
            raw = raw.Trim();
            int idx = raw.IndexOf(" (0x", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return raw.Substring(0, idx).Trim();
            idx = raw.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return raw.Substring(0, idx).Trim().TrimEnd('(').Trim();
            return raw;
        }
        catch
        {
            return raw ?? "N/A";
        }
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
                if (len > 0 && uint.TryParse(raw.Substring(start, len), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
                    return parsed;
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
                if (len > 0 && uint.TryParse(raw.Substring(firstHex, len), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
                    return parsed;
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

    private static string GetCharacterDisplayName(int ownerHash)
    {
        switch (ownerHash)
        {
            case FRANKLIN_HASH: return "Franklin Clinton";
            case MICHAEL_HASH: return "Michael De Santa";
            case TREVOR_HASH: return "Trevor Philips";
            default: return "N/A";
        }
    }

    private static int GetCurrentCharacterHash()
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

    private static int GetCurrentHour()
    {
        try { return Function.Call<int>(Hash.GET_CLOCK_HOURS); }
        catch { return 0; }
    }

    private static int GetCurrentMinute()
    {
        try { return Function.Call<int>(Hash.GET_CLOCK_MINUTES); }
        catch { return 0; }
    }

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
        catch
        {
            return -1;
        }
    }

    private static bool IsInsidePaymentWindow(int hour, int minute, int startHour, int endHour)
    {
        try
        {
            if (startHour < 0 || endHour < 0)
                return false;

            if (startHour < endHour)
                return hour >= startHour && hour < endHour;

            return hour >= startHour || hour < endHour;
        }
        catch
        {
            return false;
        }
    }

    private void ShowFeed(string sender, string subject, string body)
    {
        PlayFrontendSound(CDSMessageSoundName, CDSMessageSoundSet);

        try
        {
            Notification.Show(NotificationIcon.Bugstars, sender, subject, body);
        }
        catch
        {
            try { Notification.Show(body); } catch { }
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

    private void ShowSubtitle(string text)
    {
        try { GTA.UI.Screen.ShowSubtitle(text, HELP_PROMPT_TIMEOUT_MS); }
        catch { }
    }

    private void ShowHelpText(string text)
    {
        try { GTA.UI.Screen.ShowHelpTextThisFrame(text); }
        catch { }
    }

    private void CloseMenu()
    {
        try
        {
            if (_menu != null)
                _menu.Visible = false;
        }
        catch
        {
        }

        _menuOpen = false;
        Interval = 1000;
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

    private static void PlaySound()
    {
        try
        {
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "PURCHASE", "HUD_FRONTEND_TATTOO_SHOP_SOUNDSET", true);
        }
        catch
        {
        }
    }

    private static bool ParseBool(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;
        s = s.Trim();
        return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseInt(string s, int fallback = 0)
    {
        try
        {
            if (int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                return i;
        }
        catch { }
        return fallback;
    }

    private static long ParseLong(string s)
    {
        try
        {
            if (long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long i))
                return i;
        }
        catch { }
        return 0L;
    }

    private static decimal ParseDecimal(string s)
    {
        try
        {
            if (decimal.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
                return d;
        }
        catch { }
        return 0m;
    }

    private static float ParseFloat(string s)
    {
        try
        {
            if (float.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                return f;
        }
        catch { }
        return 0f;
    }

    private static void Log(string message)
    {
        try
        {
            // silent by default
        }
        catch
        {
        }
    }
}

public static class CreditDefaultSwapBridge
{
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "TPI Financial");

    private static string GetStateFile(int ownerHash)
    {
        Directory.CreateDirectory(DataFolder);
        return Path.Combine(DataFolder, $"cds_state_{ownerHash}.dat");
    }

    private static int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists())
                return 0;

            int h = p.Model.Hash;
            if (h == -1692214353 || h == 225514697 || h == -1686040670)
                return h;
        }
        catch
        {
        }
        return 0;
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

    private static bool TryParseInGameDateTime(string s, out DateTime dt)
    {
        dt = default(DateTime);

        try
        {
            if (string.IsNullOrWhiteSpace(s))
                return false;

            return DateTime.TryParseExact(
                s.Trim(),
                "o",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out dt);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCoverageActiveNow(State state)
    {
        try
        {
            if (state == null || !state.Active)
                return false;

            if (!TryParseInGameDateTime(state.CoverageExpiresAt, out DateTime expiresAt))
                return false;

            return GetCurrentInGameDateTime() < expiresAt;
        }
        catch
        {
            return false;
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

            if (!TryReadState(ownerHash, out var state) || state == null)
                return false;

            if (!IsCoverageActiveNow(state))
                return false;

            rate = state.FrozenRate;
            return rate > 0m;
        }
        catch
        {
            rate = 0m;
            return false;
        }
    }

    public static bool IsCoverageActiveForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return false;

            if (!TryReadState(ownerHash, out var state) || state == null)
                return false;

            return IsCoverageActiveNow(state);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPolicyActiveForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return false;

            if (!TryReadState(ownerHash, out var state) || state == null)
                return false;

            return state.Active;
        }
        catch
        {
            return false;
        }
    }

    private sealed class State
    {
        public int OwnerHash;
        public bool Active;
        public long DailyFee;
        public decimal FrozenRate;
        public int PaymentWindowStartHour;
        public int PaymentWindowEndHour;
        public int LastPaidDayKey;
        public int LastAttemptDayKey;
        public int CollateralCountAtPurchase;
        public long CollateralValueAtPurchase;
        public string CustomerName;
        public int Version;
        public string CoverageActivatedAt;
        public string CoverageExpiresAt;
    }

    private static bool TryReadState(int ownerHash, out State state)
    {
        state = null;

        try
        {
            string file = GetStateFile(ownerHash);
            if (!File.Exists(file))
                return false;

            string[] p = File.ReadAllText(file, Encoding.UTF8).Split('|');
            if (p.Length < 10)
                return false;

            state = new State
            {
                OwnerHash = ownerHash,
                Active = ParseBool(p[1]),
                DailyFee = ParseLong(p[2]),
                FrozenRate = ParseDecimal(p[3]),
                PaymentWindowStartHour = ParseInt(p[4], -1),
                PaymentWindowEndHour = ParseInt(p[5], -1),
                LastPaidDayKey = ParseInt(p[6], -1),
                LastAttemptDayKey = ParseInt(p[7], -1),
                CollateralCountAtPurchase = ParseInt(p[8], 0),
                CollateralValueAtPurchase = ParseLong(p[9]),
                CustomerName = p.Length > 10 ? p[10] : "",
                Version = p.Length > 11 ? ParseInt(p[11], 1) : 1,
                CoverageActivatedAt = p.Length > 12 ? p[12] : "",
                CoverageExpiresAt = p.Length > 13 ? p[13] : ""
            };
            return true;
        }
        catch
        {
            state = null;
            return false;
        }
    }

    private static bool ParseBool(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseInt(string s, int fallback = 0)
    {
        try
        {
            if (int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                return i;
        }
        catch { }
        return fallback;
    }

    private static long ParseLong(string s)
    {
        try
        {
            if (long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long i))
                return i;
        }
        catch { }
        return 0L;
    }

    private static decimal ParseDecimal(string s)
    {
        try
        {
            if (decimal.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
                return d;
        }
        catch { }
        return 0m;
    }
}