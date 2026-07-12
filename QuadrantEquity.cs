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

public class QuadrantEquity : Script
{
    private static string L(string key, string fallback = "")
    {
        return Language.Get(key, fallback);
    }

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

    private const long MinimumEntryMoney = 10_000_000L;

    private const long PackageLowAmount = 5_000_000L;
    private const long PackageMediumAmount = 25_000_000L;
    private const long PackageHighAmount = 50_000_000L;

    private const decimal DividendRate = 0.00032m; // 0.032%
    private const decimal CancelRefundRate = 0.35m; // 40%

    private const int DividendCheckIntervalMs = 20_000;
    private const int DividendOfferTimeoutMs = 10_000;
    private const int ContactLockDays = 4;

    private const long LombankInitialTotalLimit = 1_000_000L;
    private const long LombankMaxTotalLimit = 15_000_000L;

    private const int QuadrantEquityIntroDelayMs = 800;

    private readonly string QuadrantEquityAudioRoot = Path.Combine(
        GetScriptsDirectory(),
        "Audio"
    );

    private readonly string QuadrantEquityIntroWavFileName = "Quadrant_Equity.wav";

    private bool _introWavPlaying = false;
    private int _introWavStartedAt = 0;

    private static string ContactName => L("REIT_ContactName", "Quadrant Equity");
    private static string NotificationBrand => L("REIT_NotificationBrand", "Quadrant Equity");
    private static string MenuTitle => L("REIT_MenuTitle", "REIT");
    private static string MenuSubtitle => L("REIT_MenuSubtitle", "CHI TIẾT QUỸ REIT");
    private static string MenuFooter => L("REIT_MenuFooter", "QUADRANT EQUITY");

    private static readonly Vector3[] _unused = new Vector3[0];

    private readonly ObjectPool _uiPool = new ObjectPool();
    private NativeMenu _mainMenu;
    private bool _menuInitialized;

    private NativeItem _orgItem;
    private NativeItem _customerItem;
    private NativeItem _sectionItem;
    private NativeCheckboxItem _lowItem;
    private NativeCheckboxItem _mediumItem;
    private NativeCheckboxItem _highItem;
    private NativeItem _priceItem;
    private NativeItem _paymentItem;
    private NativeItem _confirmItem;
    private NativeItem _cancelItem;

    private bool _updatingPackageSelection;

    private CustomiFruit _phoneInstance;
    private iFruitContact _reitContact;
    private bool _contactAnsweredBound;

    private int _activeCharacterHash;
    private string _customerName = "N/A";

    private enum ReitPackage
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3
    }

    private ReitPackage _selectedPackage = ReitPackage.Low;

    private ReitPackage _activePackage = ReitPackage.None;
    private long _activeInvestmentAmount;
    private DateTime? _investmentStartedAt;
    private DateTime? _contactLockedUntil;

    private string _lastDividendPromptDateKey = string.Empty;

    private bool _awaitDividendChoice;
    private long _pendingDividendAmount;
    private string _pendingDividendDateKey = string.Empty;

    private bool _awaitDividendOfferChoice;
    private int _dividendOfferStartedGameTime;

    private bool _awaitCancelInvestment;
    private long _pendingCancelRefundAmount;

    private int _lastDividendCheckGameTime = -DividendCheckIntervalMs;
    private int _lastInputGameTime;

    public QuadrantEquity()
    {
        Interval = 1000;
        Tick += OnTick;
        KeyDown += OnKeyDown;

        SyncCharacterState();
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            SyncCharacterState();
            EnsureContactRegistered();
            UpdateContactAvailability();

            if (_introWavPlaying)
            {
                if (Game.GameTime - _introWavStartedAt >= QuadrantEquityIntroDelayMs)
                {
                    _introWavPlaying = false;
                    OpenMainMenu();
                }
                else
                {
                    Interval = 0;
                    return;
                }
            }

            ExpireDividendOfferIfNeeded();
            TryShowDailyDividendPrompt();

            ProcessPendingUiActions();

            if (_awaitDividendChoice)
                ShowDividendHelpText();

            if (_awaitCancelInvestment)
                ShowCancelInvestmentHelpText();

            UpdateLemonUiMouseState();

            bool keepFastTick =
                _awaitDividendOfferChoice ||
                _awaitDividendChoice ||
                _awaitCancelInvestment ||
                (_uiPool != null && _uiPool.AreAnyVisible);

            if (_uiPool != null && _uiPool.AreAnyVisible)
            {
                _uiPool.Process();
                UpdateLemonUiMouseState();
            }

            Interval = keepFastTick ? 0 : 1000;
        }
        catch (Exception ex)
        {
            Log("OnTick failed: " + ex);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            if (_awaitDividendOfferChoice)
            {
                if (Game.GameTime - _dividendOfferStartedGameTime >= DividendOfferTimeoutMs)
                {
                    ExpireDividendOfferIfNeeded();
                    return;
                }

                if (e.KeyCode == Keys.Enter)
                {
                    AcceptDividendOffer();
                    return;
                }

                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                {
                    DeclineDividendOffer();
                    return;
                }
            }

            if (_awaitDividendChoice)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    HandleDividendToCash();
                    return;
                }

                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                {
                    HandleDividendToLombank();
                    return;
                }
            }

            if (_awaitCancelInvestment)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    ConfirmCancelInvestment();
                    return;
                }

                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                {
                    DeclineCancelInvestment();
                    return;
                }
            }

            if (_uiPool != null && _uiPool.AreAnyVisible)
            {
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                    CloseMainMenu();
            }
        }
        catch (Exception ex)
        {
            Log("OnKeyDown failed: " + ex);
        }
    }

    private void ProcessPendingUiActions()
    {
        try
        {
            while (true)
            {
                Action action = null;

                lock (_pendingUiActions)
                {
                    if (_pendingUiActions.Count > 0)
                        action = _pendingUiActions.Dequeue();
                }

                if (action == null)
                    break;

                try
                {
                    action.Invoke();
                }
                catch (Exception ex)
                {
                    Log("ProcessPendingUiActions action failed: " + ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log("ProcessPendingUiActions failed: " + ex);
        }
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

            ResetTransientRuntimeState();
            LoadStateForCurrentCharacter();
            RefreshMainMenu();
        }
        catch (Exception ex)
        {
            Log("SyncCharacterState failed: " + ex);
        }
    }

    private void ResetTransientRuntimeState()
    {
        try
        {
            CloseMainMenu();

            _awaitDividendOfferChoice = false;
            _dividendOfferStartedGameTime = 0;

            _awaitDividendChoice = false;
            _pendingDividendAmount = 0;
            _pendingDividendDateKey = string.Empty;

            _awaitCancelInvestment = false;
            _pendingCancelRefundAmount = 0;

            _contactAnsweredBound = false;
            _reitContact = null;
            _phoneInstance = null;
        }
        catch { }
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
                _reitContact = null;
                _contactAnsweredBound = false;
            }

            if (_reitContact == null)
            {
                _reitContact = phone.Contacts.FirstOrDefault(c =>
                    string.Equals(c.Name, ContactName, StringComparison.OrdinalIgnoreCase));
            }

            if (_reitContact == null)
            {
                // Thay đổi dòng set Active khi tạo mới tại đây
                _reitContact = new iFruitContact(ContactName)
                {
                    Active = !IsContactLockedNow() && !_introWavPlaying,
                    DialTimeout = 2500,
                    Bold = false,
                    Icon = new ContactIcon("WEB_NATIONALOFFICEOFSECURITYENFORCEMENT")
                };

                phone.Contacts.Add(_reitContact);
            }

            if (!_contactAnsweredBound)
            {
                _reitContact.Answered += OnReitAnswered;
                _contactAnsweredBound = true;
            }

            // Thay đổi dòng set Active ở cuối hàm tại đây
            _reitContact.Active = !IsContactLockedNow() && !_introWavPlaying;
        }
        catch (Exception ex)
        {
            Log("EnsureContactRegistered failed: " + ex);
        }
    }

    private void UpdateContactAvailability()
    {
        try
        {
            if (_reitContact == null)
            {
                return;
            }

            if (_introWavPlaying)
            {
                _reitContact.Active = false;
                return;
            }

            if (_reitContact.Active)
            {
                if (IsContactLockedNow())
                {
                    _reitContact.Active = false;
                }
            }
            else
            {
                if (!IsContactLockedNow())
                {
                    _reitContact.Active = true;
                }
            }
        }
        catch
        {
            // Xem xét việc log lỗi ở đây nếu cần thiết thay vì bỏ trống
        }
    }

    private bool IsContactLockedNow()
    {
        try
        {
            if (!HasActiveInvestment())
                return false;

            if (!_contactLockedUntil.HasValue)
                return false;

            return GetCurrentInGameDateTime() < _contactLockedUntil.Value;
        }
        catch
        {
            return false;
        }
    }

    private void OnReitAnswered(iFruitContact sender)
    {
        try
        {
            if (_awaitDividendChoice || _awaitCancelInvestment)
            {
                TryClosePhone();
                return;
            }

            if (HasActiveInvestment())
            {
                StartCancelInvestmentPrompt();
                TryClosePhone();
                return;
            }

            if (!PrimeAutoHandoverBridge.IsBusinessPurchasedForCurrentCharacter())
            {
                ShowQuadrantNotification(
                    NotificationBrand,
                    L("REIT_RequireBusiness", "Hãy sở hữu doanh nghiệp D2D trước!")
                );
                TryClosePhone();
                return;
            }

            if (Game.Player.Money < MinimumEntryMoney)
            {
                ShowQuadrantNotification(
                    NotificationBrand,
                    L("REIT_MinEntryMoney", "Phải có 10 triệu trở lên nhé!")
                );
                TryClosePhone();
                return;
            }

            _introWavPlaying = true;
            _introWavStartedAt = Game.GameTime;
            PlayQuadrantEquityIntroWav();
            TryClosePhone();
        }
        catch (Exception ex)
        {
            Log("OnReitAnswered failed: " + ex);
        }
    }

    private void StartCancelInvestmentPrompt()
    {
        try
        {
            if (!HasActiveInvestment())
                return;

            _awaitCancelInvestment = true;
            _pendingCancelRefundAmount = CalculateRefundAmount(_activeInvestmentAmount);
        }
        catch (Exception ex)
        {
            Log("StartCancelInvestmentPrompt failed: " + ex);
        }
    }

    private void ShowCancelInvestmentHelpText()
    {
        try
        {
            if (!HasActiveInvestment())
                return;

            string packageName = GetPackageDisplayName(_activePackage);

            GTA.UI.Screen.ShowHelpTextThisFrame(
                string.Format(
                    L("REIT_HelpCancelInvestment",
                    "Hiện tại đang có gói đầu tư \"{0}\". Bạn muốn hủy?\n~INPUT_FRONTEND_ACCEPT~ để đồng ý\n~INPUT_FRONTEND_LEFT~ để tiếp tục"),
                    packageName));
        }
        catch { }
    }

    private void ShowDividendHelpText()
    {
        try
        {
            if (!_awaitDividendChoice)
                return;

            GTA.UI.Screen.ShowHelpTextThisFrame(
                string.Format(
                    L("REIT_HelpDividendChoice",
                    "Cổ tức ~g~{0}~s~ này nhận được từ Quỹ QE. Bạn muốn nhận nó vào đâu?\n~INPUT_FRONTEND_ACCEPT~ vào tiền mặt\n~INPUT_FRONTEND_LEFT~ vào Lom Bank"),
                    FormatMoney(_pendingDividendAmount)));
        }
        catch { }
    }

    private void ExpireDividendOfferIfNeeded()
    {
        try
        {
            if (!_awaitDividendOfferChoice)
                return;

            if (Game.GameTime - _dividendOfferStartedGameTime < DividendOfferTimeoutMs)
                return;

            AutoGrantDividendToCashOnTimeout();
        }
        catch
        {
        }
    }

    private void AutoGrantDividendToCashOnTimeout()
    {
        try
        {
            if (!_awaitDividendOfferChoice)
                return;

            long amount = _pendingDividendAmount;

            ClearDividendOfferState();

            if (amount > 0)
            {
                Game.Player.Money += (int)Math.Min(amount, int.MaxValue);
                ShowQuadrantNotification(
                    NotificationBrand,
                    string.Format(
                    L("REIT_AutoDividendToCash", "Cổ tức ~g~{0}~s~ đã tự động vào tiền mặt."),
                    FormatMoney(amount)));
            }

            SaveStateForCurrentCharacter();
        }
        catch (Exception ex)
        {
            Log("AutoGrantDividendToCashOnTimeout failed: " + ex);
        }
    }

    private void AcceptDividendOffer()
    {
        try
        {
            if (!_awaitDividendOfferChoice)
                return;

            _awaitDividendOfferChoice = false;
            _awaitDividendChoice = true;

            Interval = 0;
            ShowDividendHelpText();
        }
        catch (Exception ex)
        {
            Log("AcceptDividendOffer failed: " + ex);
        }
    }

    private void DeclineDividendOffer()
    {
        try
        {
            if (!_awaitDividendOfferChoice)
                return;

            ClearDividendOfferState();
            SaveStateForCurrentCharacter();
        }
        catch (Exception ex)
        {
            Log("DeclineDividendOffer failed: " + ex);
        }
    }

    private void ClearDividendOfferState()
    {
        _awaitDividendOfferChoice = false;
        _dividendOfferStartedGameTime = 0;
        _pendingDividendAmount = 0;
        _pendingDividendDateKey = string.Empty;
    }

    private void TryShowDailyDividendPrompt()
    {
        try
        {
            if (_awaitDividendOfferChoice || _awaitDividendChoice || _awaitCancelInvestment)
                return;

            if (!HasActiveInvestment())
                return;

            int gameTime = Game.GameTime;
            if (gameTime - _lastDividendCheckGameTime < DividendCheckIntervalMs)
                return;

            _lastDividendCheckGameTime = gameTime;

            DateTime now = GetCurrentInGameDateTime();
            if (!IsWithinDividendWindow(now))
                return;

            string todayKey = GetDateKey(now);
            if (string.Equals(_lastDividendPromptDateKey, todayKey, StringComparison.OrdinalIgnoreCase))
                return;

            _pendingDividendAmount = CalculateDividendAmount(_activeInvestmentAmount);
            if (_pendingDividendAmount <= 0)
                return;

            _pendingDividendDateKey = todayKey;
            _lastDividendPromptDateKey = todayKey;

            _awaitDividendOfferChoice = true;
            _dividendOfferStartedGameTime = Game.GameTime;

            ShowQuadrantNotification(
                NotificationBrand,
                L("REIT_DividendReady", "Đang có cổ tức của hôm nay từ quỹ QE rồi đấy!"));
            GTA.UI.Screen.ShowSubtitle(
                L("REIT_DividendSubtitle",
                "Hãy nhấn ~HUD_COLOUR_DEGEN_YELLOW~Enter~s~ (hoặc ~HUD_COLOUR_REDLIGHT~Back~s~) ngay lúc này!!!"),
                6000);
            SaveStateForCurrentCharacter();
        }
        catch (Exception ex)
        {
            Log("TryShowDailyDividendPrompt failed: " + ex);
        }
    }

    private static bool IsWithinDividendWindow(DateTime now)
    {
        int minutes = now.Hour * 60 + now.Minute;
        return minutes >= 12 * 60 && minutes < 13 * 60;
    }

    private void HandleDividendToCash()
    {
        try
        {
            if (!_awaitDividendChoice)
                return;

            long amount = _pendingDividendAmount;
            if (amount > 0)
            {
                Game.Player.Money += (int)Math.Min(amount, int.MaxValue);
                ShowQuadrantNotification(
                     NotificationBrand,
                     string.Format(
                         L("REIT_DividendToCash", "Phần cổ tức đã được cộng thêm ~g~{0}~s~ vào tài khoản của bạn."),
                         FormatMoney(amount)));
            }

            ClearDividendPromptState();
            SaveStateForCurrentCharacter();
        }
        catch (Exception ex)
        {
            Log("HandleDividendToCash failed: " + ex);
        }
    }

    private void HandleDividendToLombank()
    {
        try
        {
            if (!_awaitDividendChoice)
                return;

            long amount = _pendingDividendAmount;
            if (amount > 0)
            {
                long actualAdded = ApplyDividendToLombankLimit(amount);

                if (actualAdded > 0)
                {
                    ShowQuadrantNotification(
                        NotificationBrand,
                        string.Format(
                            L("REIT_DividendToLombank", "Đã thêm ~g~{0}~s~ vào Tổng hạn mức Lom Bank."),
                            FormatMoney(actualAdded)));
                }
                else
                {
                    ShowQuadrantNotification(
                        NotificationBrand,
                        L("REIT_LombankMaxed", "Tổng hạn mức Lom Bank đã đạt mức tối đa."));
                }
            }

            ClearDividendPromptState();
            SaveStateForCurrentCharacter();
        }
        catch (Exception ex)
        {
            Log("HandleDividendToLombank failed: " + ex);
        }
    }

    private void ClearDividendPromptState()
    {
        _awaitDividendOfferChoice = false;
        _dividendOfferStartedGameTime = 0;

        _awaitDividendChoice = false;
        _pendingDividendAmount = 0;
        _pendingDividendDateKey = string.Empty;
    }

    private void ConfirmCancelInvestment()
    {
        try
        {
            if (!HasActiveInvestment())
            {
                DeclineCancelInvestment();
                return;
            }

            long refund = _pendingCancelRefundAmount;
            if (refund > 0)
                Game.Player.Money += (int)Math.Min(refund, int.MaxValue);

            string packageName = GetPackageDisplayName(_activePackage);

            ResetActiveInvestmentState();
            SaveStateForCurrentCharacter();

            if (refund > 0)
            {
                ShowQuadrantNotification(
                    NotificationBrand,
                    string.Format(
                        L("REIT_CancelRefunded", "Đã hủy gói {0}. Bạn nhận lại {1}."),
                    packageName,
                    FormatMoney(refund)));
            }
            else
            {
                ShowQuadrantNotification(
                    NotificationBrand,
                    string.Format(
                        L("REIT_CancelNoRefund", "Đã hủy gói {0}."),
                        packageName));
            }

            TryClosePhone();
        }
        catch (Exception ex)
        {
            Log("ConfirmCancelInvestment failed: " + ex);
        }
        finally
        {
            _awaitCancelInvestment = false;
            _pendingCancelRefundAmount = 0;
        }
    }

    private void DeclineCancelInvestment()
    {
        try
        {
            _awaitCancelInvestment = false;
            _pendingCancelRefundAmount = 0;
            TryClosePhone();
        }
        catch (Exception ex)
        {
            Log("DeclineCancelInvestment failed: " + ex);
        }
    }

    private void OpenMainMenu()
    {
        try
        {
            EnsureMainMenuCreated();

            if (_selectedPackage == ReitPackage.None)
                SetSelectedPackage(ReitPackage.Low, true);

            RefreshMainMenu();

            if (_mainMenu != null)
            {
                _mainMenu.Visible = true;
                Interval = 0;
                UpdateLemonUiMouseState();
            }
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

            UpdateLemonUiMouseState();
        }
        catch (Exception ex)
        {
            Log("CloseMainMenu failed: " + ex);
        }
    }

    private void EnsureMainMenuCreated()
    {
        try
        {
            if (_menuInitialized)
                return;

            _mainMenu = new NativeMenu(MenuTitle, MenuSubtitle, MenuFooter);
            _mainMenu.KeepNameCasing = true;
            _mainMenu.MaxItems = 10;
            _mainMenu.NoItemsText = L("REIT_NoItemsText", "Không có dữ liệu");
            _uiPool.Add(_mainMenu);

            _orgItem = new NativeItem("");
            _customerItem = new NativeItem("");
            _sectionItem = new NativeItem(L("REIT_SectionPackage", "3. Gói đầu tư"), "");

            _lowItem = CreatePackageCheckbox(L("REIT_PackageLow", "Thấp"), ReitPackage.Low);
            _mediumItem = CreatePackageCheckbox(L("REIT_PackageMedium", "Trung bình"), ReitPackage.Medium);
            _highItem = CreatePackageCheckbox(L("REIT_PackageHigh", "Cao"), ReitPackage.High);

            _priceItem = new NativeItem("");
            _paymentItem = new NativeItem("");
            _confirmItem = new NativeItem(
                L("REIT_ConfirmInvestment", "Xác nhận đầu tư"),
                L("REIT_ConfirmInvestmentDesc", "Thanh toán bằng tiền mặt để tham gia Quỹ QE."));
            _cancelItem = new NativeItem(
                L("REIT_CancelTransaction", "Hủy giao dịch"),
                L("REIT_CancelTransactionDesc", "Đóng menu và quay lại."));

            _confirmItem.Activated += (s, e) => QueueUiAction(HandleInvestmentConfirm);
            _cancelItem.Activated += (s, e) => QueueUiAction(CloseMainMenu);

            _mainMenu.Add(_orgItem);
            _mainMenu.Add(_customerItem);
            _mainMenu.Add(_sectionItem);
            _mainMenu.Add(_lowItem);
            _mainMenu.Add(_mediumItem);
            _mainMenu.Add(_highItem);
            _mainMenu.Add(_priceItem);
            _mainMenu.Add(_paymentItem);
            _mainMenu.Add(_confirmItem);
            _mainMenu.Add(_cancelItem);

            _menuInitialized = true;

            SetSelectedPackage(ReitPackage.Low, true);
            RefreshMainMenu();
        }
        catch (Exception ex)
        {
            Log("EnsureMainMenuCreated failed: " + ex);
        }
    }

    private NativeCheckboxItem CreatePackageCheckbox(string title, ReitPackage package)
    {
        bool suppress = false;
        var item = new NativeCheckboxItem(title, "", false);

        item.CheckboxChanged += (s, e) =>
        {
            if (suppress || _updatingPackageSelection)
                return;

            try
            {
                if (item.Checked)
                {
                    SetSelectedPackage(package, false);
                    return;
                }

                suppress = true;
                SetSelectedPackage(_selectedPackage == ReitPackage.None ? ReitPackage.Low : _selectedPackage, true);
                suppress = false;
            }
            catch
            {
                suppress = false;
            }
        };

        return item;
    }

    private void SetSelectedPackage(ReitPackage package, bool forceRefresh)
    {
        try
        {
            if (package == ReitPackage.None)
                package = ReitPackage.Low;

            _updatingPackageSelection = true;
            _selectedPackage = package;

            if (_lowItem != null)
                _lowItem.Checked = package == ReitPackage.Low;

            if (_mediumItem != null)
                _mediumItem.Checked = package == ReitPackage.Medium;

            if (_highItem != null)
                _highItem.Checked = package == ReitPackage.High;
        }
        finally
        {
            _updatingPackageSelection = false;
        }

        if (forceRefresh)
            RefreshMainMenu();
    }

    private void RefreshMainMenu()
    {
        try
        {
            if (_mainMenu == null)
                return;

            _orgItem.Title = string.Format(
                L("REIT_MenuOrg", "1. Tên tổ chức: {0}"),
                NotificationBrand);
            _customerItem.Title = string.Format(
                L("REIT_MenuCustomer", "2. Tên khách hàng: {0}"),
                _customerName);
            _sectionItem.Title = L("REIT_SectionPackage", "3. Gói đầu tư");

            _priceItem.Title = string.Format(
                L("REIT_MenuPrice", "4. Giá gói: {0}"),
                FormatMoney(GetSelectedPackageAmount()));
            _paymentItem.Title = L("REIT_MenuPayment", "5. Cơ chế thanh toán: Tiền mặt");

            _confirmItem.Title = L("REIT_ConfirmInvestment", "Xác nhận đầu tư");
            _cancelItem.Title = L("REIT_CancelTransaction", "Hủy giao dịch");
        }
        catch (Exception ex)
        {
            Log("RefreshMainMenu failed: " + ex);
        }
    }

    private void HandleInvestmentConfirm()
    {
        try
        {
            if (HasActiveInvestment())
            {
                ShowQuadrantNotification(
                    NotificationBrand,
                    L("REIT_AlreadyHasInvestment", "Bạn đang có gói đầu tư rồi."));
                CloseMainMenu();
                return;
            }

            if (!PrimeAutoHandoverBridge.IsBusinessPurchasedForCurrentCharacter())
            {
                ShowQuadrantNotification(
                    NotificationBrand,
                    L("REIT_RequireBusiness", "Hãy sở hữu doanh nghiệp D2D trước!"));
                return;
            }

            if (Game.Player.Money < MinimumEntryMoney)
            {
                ShowQuadrantNotification(
                    NotificationBrand,
                    L("REIT_MinEntryMoney", "Phải có 10 triệu trở lên nhé!"));
                return;
            }

            long amount = GetSelectedPackageAmount();
            if (amount <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("REIT_SelectPackageFirst", "Hãy ~y~chọn~s~ một gói đầu tư trước đã."),
                    2500);
                return;
            }

            if (Game.Player.Money < amount)
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("REIT_NotEnoughMoney", "Bạn ~r~không đủ tiền~s~ cho gói đã chọn."),
                    2500);
                return;
            }

            Game.Player.Money -= (int)Math.Min(amount, int.MaxValue);

            _activePackage = _selectedPackage;
            _activeInvestmentAmount = amount;
            _investmentStartedAt = GetCurrentInGameDateTime();
            _contactLockedUntil = _investmentStartedAt.Value.AddDays(ContactLockDays);

            _lastDividendPromptDateKey = string.Empty;
            _pendingDividendDateKey = string.Empty;
            _awaitDividendChoice = false;
            _pendingDividendAmount = 0;

            _awaitCancelInvestment = false;
            _pendingCancelRefundAmount = 0;

            SaveStateForCurrentCharacter();

            ShowQuadrantNotification(
                NotificationBrand,
                string.Format(
                    L("REIT_InvestmentConfirmed", "Đã đầu tư {0} với giá {1}. QE sẽ tạm ngưng giao dịch."),
                    GetPackageDisplayName(_activePackage),
                    FormatMoney(amount)));
            CloseMainMenu();
        }
        catch (Exception ex)
        {
            Log("HandleInvestmentConfirm failed: " + ex);
        }
    }

    private void ResetActiveInvestmentState()
    {
        _activePackage = ReitPackage.None;
        _activeInvestmentAmount = 0;
        _investmentStartedAt = null;
        _contactLockedUntil = null;
        _lastDividendPromptDateKey = string.Empty;

        _awaitDividendChoice = false;
        _pendingDividendAmount = 0;
        _pendingDividendDateKey = string.Empty;

        _awaitCancelInvestment = false;
        _pendingCancelRefundAmount = 0;
    }

    private bool HasActiveInvestment()
    {
        return _activePackage != ReitPackage.None && _activeInvestmentAmount > 0;
    }

    private long GetSelectedPackageAmount()
    {
        switch (_selectedPackage)
        {
            case ReitPackage.Low:
                return PackageLowAmount;
            case ReitPackage.Medium:
                return PackageMediumAmount;
            case ReitPackage.High:
                return PackageHighAmount;
            default:
                return 0;
        }
    }

    private static long CalculateDividendAmount(long investmentAmount)
    {
        if (investmentAmount <= 0)
            return 0;

        decimal raw = investmentAmount * DividendRate;
        return (long)Math.Round(raw, 0, MidpointRounding.AwayFromZero);
    }

    private static long CalculateRefundAmount(long investmentAmount)
    {
        if (investmentAmount <= 0)
            return 0;

        decimal raw = investmentAmount * CancelRefundRate;
        return (long)Math.Round(raw, 0, MidpointRounding.AwayFromZero);
    }

    private string GetPackageDisplayName(ReitPackage package)
    {
        switch (package)
        {
            case ReitPackage.Low:
                return L("REIT_PackageLow", "Thấp");
            case ReitPackage.Medium:
                return L("REIT_PackageMedium", "Trung bình");
            case ReitPackage.High:
                return L("REIT_PackageHigh", "Cao");
            default:
                return L("REIT_Unknown", "Không xác định");
        }
    }

    private void ShowQuadrantNotification(string title, string message, int timeout = 2500)
    {
        try
        {
            PlayFrontendSound("Text_Arrive_Tone", "Phone_SoundSet_Default");

            Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, message);

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_MESSAGETEXT,
                "WEB_NATIONALOFFICEOFSECURITYENFORCEMENT",
                "WEB_NATIONALOFFICEOFSECURITYENFORCEMENT",
                false,
                0,
                "REIT",
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

    private string GetQuadrantEquityWavPath()
    {
        try
        {
            Directory.CreateDirectory(QuadrantEquityAudioRoot);
        }
        catch
        {
            // Thực hiện bỏ qua lỗi nếu không tạo được thư mục
        }

        return Path.Combine(QuadrantEquityAudioRoot, QuadrantEquityIntroWavFileName);
    }

    private void PlayQuadrantEquityIntroWav()
    {
        try
        {
            string path = GetQuadrantEquityWavPath();
            if (!File.Exists(path)) return;

            GTA.UI.Screen.ShowSubtitle(
                "Quadrant Equity, how may I help you? Please note that to process your request, you must first own a D2D business and have $10M in assets.",
                8100
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
            // Thực hiện bỏ qua lỗi tổng thể của hàm phát âm thanh
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

    private static int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists())
                return 0;

            int hash = p.Model.Hash;
            if (hash == PrimeAutoHandoverBridge.HashMichael ||
                hash == PrimeAutoHandoverBridge.HashFranklin ||
                hash == PrimeAutoHandoverBridge.HashTrevor)
            {
                return hash;
            }
        }
        catch { }

        return 0;
    }

    private string GetCustomerDisplayName(int modelHash)
    {
        if (modelHash == PrimeAutoHandoverBridge.HashMichael)
            return "Michael De Santa";

        if (modelHash == PrimeAutoHandoverBridge.HashFranklin)
            return "Franklin Clinton";

        if (modelHash == PrimeAutoHandoverBridge.HashTrevor)
            return "Trevor Philips";

        return "N/A";
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

    private static string GetDateKey(DateTime dt)
    {
        return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private string GetStateFileForOwner(int ownerHash)
    {
        Directory.CreateDirectory(GetStateRoot());
        return Path.Combine(GetStateRoot(), $"reit_state_{ownerHash}.dat");
    }

    private static string GetStateRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GTA V Mods",
            "Quadrant Equity");
    }

    private void LoadStateForCurrentCharacter()
    {
        try
        {
            Directory.CreateDirectory(GetStateRoot());

            _activePackage = ReitPackage.None;
            _activeInvestmentAmount = 0;
            _investmentStartedAt = null;
            _contactLockedUntil = null;
            _lastDividendPromptDateKey = string.Empty;
            _awaitDividendOfferChoice = false;
            _dividendOfferStartedGameTime = 0;
            _awaitDividendChoice = false;
            _pendingDividendAmount = 0;
            _pendingDividendDateKey = string.Empty;
            _awaitCancelInvestment = false;
            _pendingCancelRefundAmount = 0;

            _selectedPackage = ReitPackage.Low;

            string file = GetStateFileForOwner(_activeCharacterHash);
            if (!File.Exists(file))
                return;

            string text = File.ReadAllText(file).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            Dictionary<string, string> data = ParseKeyValueText(text);

            if (data.TryGetValue("activePackage", out string packageText))
            {
                if (Enum.TryParse(packageText, true, out ReitPackage parsedPackage))
                    _activePackage = parsedPackage;
            }

            if (data.TryGetValue("activeInvestmentAmount", out string amountText) &&
                long.TryParse(amountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
            {
                _activeInvestmentAmount = Math.Max(0, amount);
            }

            _investmentStartedAt = ParseNullableDateTime(data, "investmentStartedAt");
            _contactLockedUntil = ParseNullableDateTime(data, "contactLockedUntil");
            _lastDividendPromptDateKey = data.TryGetValue("lastDividendPromptDate", out string promptDate)
                ? promptDate
                : string.Empty;

            if (data.TryGetValue("pendingDividendChoice", out string pendingChoice))
                _awaitDividendChoice = ParseBool(pendingChoice);

            if (data.TryGetValue("pendingDividendAmount", out string pendingDividendAmountText) &&
                long.TryParse(pendingDividendAmountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long pendingDividendAmount))
            {
                _pendingDividendAmount = Math.Max(0, pendingDividendAmount);
            }

            if (data.TryGetValue("pendingDividendDate", out string pendingDividendDate))
                _pendingDividendDateKey = pendingDividendDate;

            if (HasActiveInvestment())
            {
                if (!_investmentStartedAt.HasValue)
                    _investmentStartedAt = GetCurrentInGameDateTime();

                if (!_contactLockedUntil.HasValue)
                    _contactLockedUntil = _investmentStartedAt.Value.AddDays(ContactLockDays);

                _selectedPackage = _activePackage;
            }
            else
            {
                ResetActiveInvestmentState();
                _selectedPackage = ReitPackage.Low;
            }
        }
        catch (Exception ex)
        {
            Log("LoadStateForCurrentCharacter failed: " + ex);
            ResetActiveInvestmentState();
            _selectedPackage = ReitPackage.Low;
        }
    }

    private void SaveStateForCurrentCharacter()
    {
        try
        {
            if (_activeCharacterHash == 0)
                return;

            Directory.CreateDirectory(GetStateRoot());

            string file = GetStateFileForOwner(_activeCharacterHash);

            var sb = new StringBuilder();
            sb.AppendLine("version=1");
            sb.AppendLine("activePackage=" + _activePackage);
            sb.AppendLine("activeInvestmentAmount=" + _activeInvestmentAmount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("investmentStartedAt=" + (_investmentStartedAt.HasValue ? _investmentStartedAt.Value.ToString("o", CultureInfo.InvariantCulture) : ""));
            sb.AppendLine("contactLockedUntil=" + (_contactLockedUntil.HasValue ? _contactLockedUntil.Value.ToString("o", CultureInfo.InvariantCulture) : ""));
            sb.AppendLine("lastDividendPromptDate=" + _lastDividendPromptDateKey);
            sb.AppendLine("pendingDividendChoice=" + (_awaitDividendChoice ? "1" : "0"));
            sb.AppendLine("pendingDividendAmount=" + _pendingDividendAmount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("pendingDividendDate=" + _pendingDividendDateKey);

            File.WriteAllText(file, sb.ToString());
        }
        catch (Exception ex)
        {
            Log("SaveStateForCurrentCharacter failed: " + ex);
        }
    }

    private static Dictionary<string, string> ParseKeyValueText(string text)
    {
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

        return data;
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

    private static bool ParseBool(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value == "1" ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private long ApplyDividendToLombankLimit(long amount)
    {
        try
        {
            if (amount <= 0)
                return 0;

            int hash = GetCurrentCharacterHash();
            if (hash == 0)
                return 0;

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GTA V Mods",
                "Lom Bank");

            Directory.CreateDirectory(root);
            string file = Path.Combine(root, $"lombank_state_{hash}.dat");

            Dictionary<string, string> data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(file))
            {
                string text = File.ReadAllText(file).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    data = ParseKeyValueText(text);
            }

            EnsureLombankDefaults(data);

            long currentLimit = LombankInitialTotalLimit;
            if (data.TryGetValue("limit", out string limitText) &&
                long.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedLimit))
            {
                currentLimit = Math.Max(LombankInitialTotalLimit, Math.Min(LombankMaxTotalLimit, parsedLimit));
            }

            long newLimit = Math.Min(LombankMaxTotalLimit, currentLimit + amount);
            long actualAdded = Math.Max(0, newLimit - currentLimit);

            data["version"] = "5";
            data["limit"] = newLimit.ToString(CultureInfo.InvariantCulture);

            File.WriteAllText(file, BuildLombankStateText(data), Encoding.UTF8);
            return actualAdded;
        }
        catch (Exception ex)
        {
            Log("ApplyDividendToLombankLimit failed: " + ex);
            return 0;
        }
    }

    private static void EnsureLombankDefaults(Dictionary<string, string> data)
    {
        if (!data.ContainsKey("version")) data["version"] = "5";
        if (!data.ContainsKey("debt")) data["debt"] = "0";
        if (!data.ContainsKey("limit")) data["limit"] = LombankInitialTotalLimit.ToString(CultureInfo.InvariantCulture);
        if (!data.ContainsKey("withdrawnThisCycle")) data["withdrawnThisCycle"] = "0";
        if (!data.ContainsKey("loanOpenedAt")) data["loanOpenedAt"] = "";
        if (!data.ContainsKey("lastPenaltyAppliedAt")) data["lastPenaltyAppliedAt"] = "";
        if (!data.ContainsKey("firstWithdrawAt")) data["firstWithdrawAt"] = "";
        if (!data.ContainsKey("overdueNoticeShown")) data["overdueNoticeShown"] = "0";
        if (!data.ContainsKey("repayUnlockNoticeShown")) data["repayUnlockNoticeShown"] = "0";
    }

    private static string BuildLombankStateText(Dictionary<string, string> data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("version=" + GetValueOrDefault(data, "version", "5"));
        sb.AppendLine("debt=" + GetValueOrDefault(data, "debt", "0"));
        sb.AppendLine("limit=" + GetValueOrDefault(data, "limit", LombankInitialTotalLimit.ToString(CultureInfo.InvariantCulture)));
        sb.AppendLine("withdrawnThisCycle=" + GetValueOrDefault(data, "withdrawnThisCycle", "0"));
        sb.AppendLine("loanOpenedAt=" + GetValueOrDefault(data, "loanOpenedAt", ""));
        sb.AppendLine("lastPenaltyAppliedAt=" + GetValueOrDefault(data, "lastPenaltyAppliedAt", ""));
        sb.AppendLine("firstWithdrawAt=" + GetValueOrDefault(data, "firstWithdrawAt", ""));
        sb.AppendLine("overdueNoticeShown=" + GetValueOrDefault(data, "overdueNoticeShown", "0"));
        sb.AppendLine("repayUnlockNoticeShown=" + GetValueOrDefault(data, "repayUnlockNoticeShown", "0"));
        return sb.ToString();
    }

    private static string GetValueOrDefault(Dictionary<string, string> data, string key, string fallback)
    {
        if (data != null && data.TryGetValue(key, out string value))
            return value;

        return fallback;
    }

    private void QueueUiAction(Action action)
    {
        if (action == null)
            return;

        lock (_pendingUiActions)
        {
            _pendingUiActions.Enqueue(action);
        }
    }

    private readonly Queue<Action> _pendingUiActions = new Queue<Action>();

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
        catch { }
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
        catch { }
    }

    private void Log(string message)
    {
        try
        {
            // File.AppendAllText("RealEstateInvestmentTrust.log", DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    private static string FormatMoney(long value)
    {
        if (value < 0)
            value = 0;

        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }
}

internal static class RealEstateInvestmentTrustBridge
{
    // Chỉ để giữ file tách biệt; hiện chưa cần thêm bridge riêng.
}