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
using System.Windows.Forms;
using LemonUI;
using LemonUI.Menus;

public class FleecaBankLoanScript : Script
{
    private string ContactName => L("Credit_ContactName", "Fleeca Bank");
    private string BankerDisplayName => L("Credit_BankerDisplayName", "Nhân viên ngân hàng");

    private static readonly Vector3 NpcSpawnPosition = new Vector3(-1573.982000f, -612.021700f, 31.282150f);
    private const float ChairSearchRadius = 12.0f;
    private const string ChairScenarioName = "PROP_HUMAN_SEAT_CHAIR";

    private const float InteractionRadius = 3.0f;
    private const int ContactDialTimeoutMs = 3000;
    private const int SpawnDelayMs = 2000;

    private Ped _npcMovingToChair = null;
    private Prop _targetChairProp = null;
    private int _chairMoveDeadline = 0;

    private readonly ObjectPool _uiPool = new ObjectPool();
    private NativeMenu _loanMenu;
    private NativeItem _loanConfirmItem;
    private NativeItem _loanQuickPayItem;
    private NativeItem _loanCancelItem;
    private bool _loanMenuInitialized = false;

    private NativeMenu _loanDetailMenu;
    private NativeItem _detailDebtItem;
    private NativeItem _detailFeeItem;
    private NativeItem _detailTotalItem;
    private NativeItem _detailConfirmItem;
    private NativeItem _detailCancelItem;
    private bool _loanDetailMenuInitialized = false;

    private const int CHARACTER_SWITCH_GRACE_MS = 5000;
    private bool _catchUpPendingAfterSwitch = false;
    private int _catchUpReadyAtGameTime = 0;
    private int _activeCharacterHash = 0;

    private static readonly Random _rng = new Random();

    private const int FRANKLIN_HASH = -1692214353;
    private const int MICHAEL_HASH = 225514697;
    private const int TREVOR_HASH = -1686040670;

    private string FleecaNotifTitle => L("Credit_NotificationTitle", "Fleeca Bank");
    private static readonly NotificationIcon FleecaNotifIcon = NotificationIcon.Carsite;

    private const decimal QUICK_PAY_FEE_RATE = 0.03m;

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

    private Ped _bankerNpc = null;
    private Blip _bankerBlip = null;

    private bool _spawnRequested = false;
    private int _spawnExecuteAtGameTime = 0;

    private bool _stateLoaded = false;
    private bool _loanActive = false;
    private long _loanPrincipal = 0;
    private long _loanRemainingDebt = 0;
    private long _loanDailyDue = 0;

    private const int DueCheckIntervalMs = 40000;
    private const int DueWindowHours = 2;

    private int _loanLastChargedDayStamp = -1;   // ngày game cuối cùng đã thu tiền
    private int _dueWindowStartHour = -1;
    private int _dueWindowEndHour = -1;
    private int _nextDueCheckAtGameTime = 0;     // throttle check 40 giây

    private bool _loanDialogActive = false;

    public FleecaBankLoanScript()
    {
        Interval = 0;
        Tick += OnTick;
        KeyDown += OnKeyDown;

        SyncStateForCurrentCharacter();
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

    private void UpdateLemonUiMouseState()
    {
        try
        {
            bool anyMenuVisible =
                (_loanMenu != null && _loanMenu.Visible) ||
                (_loanDetailMenu != null && _loanDetailMenu.Visible);

            if (!anyMenuVisible)
                return;

            // Tắt các kiểu mouse control mà LemonUI có thể đang dùng.
            SetBoolPropertyIfExists(_loanMenu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse");

            SetBoolPropertyIfExists(_loanDetailMenu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse");

            SetBoolPropertyIfExists(_uiPool, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse");
        }
        catch (Exception ex)
        {
            Log("UpdateLemonUiMouseState failed: " + ex);
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
                return;

            bool switchedCharacter = (_stateOwnerHash != 0 && _stateOwnerHash != currentHash);

            if (_stateOwnerHash != 0 && _stateOwnerHash != currentHash)
                SaveStateForOwner(_stateOwnerHash);

            LoadStateForOwner(currentHash);
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

    private string GetDueWindowText()
    {
        if (_dueWindowStartHour < 0 || _dueWindowEndHour < 0)
            return "chưa xác định";

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
                _contactAdded = false;
            }

            if (_contactAdded)
                return;

            if (phone.Contacts.Any(c => string.Equals(c.Name, ContactName, StringComparison.OrdinalIgnoreCase)))
            {
                _contactAdded = true;
                return;
            }

            var contact = new iFruitContact(ContactName)
            {
                Active = true,
                DialTimeout = ContactDialTimeoutMs,
                Bold = false,
                Icon = ContactIcon.FleecaBank
            };

            contact.Answered += OnFleecabankAnswered;
            phone.Contacts.Add(contact);

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
                    L("Credit_MenuSubtitle", "Chọn mục tiêu của ngân hàng"));

                _loanConfirmItem = new NativeItem(
                    L("Credit_MenuLoan", "Vay tiền"),
                    L("Credit_MenuLoanDesc", "Xác nhận và tiếp tục quy trình vay"));

                _loanQuickPayItem = new NativeItem(
                    L("Credit_MenuQuickPay", "Thanh toán nhanh số tiền nợ"),
                    L("Credit_MenuQuickPayDesc", "Xem chi tiết số nợ cần tất toán của mình"));

                _loanCancelItem = new NativeItem(
                    L("Credit_MenuCancel", "Hủy dịch vụ"),
                    L("Credit_MenuCancelDesc", "Đóng menu ngân hàng"));

                _loanMenu.Add(_loanConfirmItem);
                _loanMenu.Add(_loanQuickPayItem);
                _loanMenu.Add(_loanCancelItem);

                _loanConfirmItem.Activated += (s, e) =>
                {
                    CloseAllLoanMenus();
                    RequestLoanSpawn();
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
        }
        catch (Exception ex)
        {
            Log("EnsureLoanMenuCreated failed: " + ex);
        }
    }

    private void OpenQuickPayDetailMenu()
    {
        try
        {
            if (!_loanActive || _loanRemainingDebt <= 0)
            {
                ShowFleecaNotification(
                    L("Credit_NotificationTitle", "Thông báo"),
                    L("Credit_NoDebt", "Bạn không có khoản nợ nào cần thanh toán.")
                );
                return;
            }

            RefreshQuickPayDetailMenu();

            if (_loanMenu != null) _loanMenu.Visible = false;
            if (_loanDetailMenu != null) _loanDetailMenu.Visible = true;
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

            _detailDebtItem.Title = string.Format(
                L("Credit_QuickPayDebt", "Số tiền nợ còn lại: {0}"),
                FormatMoney(_loanRemainingDebt));

            _detailFeeItem.Title = L("Credit_QuickPayFee", "Tỷ lệ phí: ~HUD_COLOUR_DEGEN_CYAN~3%");

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
            EnsureLoanMenuCreated();
            if (_loanMenu != null)
                _loanMenu.Visible = true;
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

    private void RequestLoanSpawn()
    {
        try
        {
            int currentHash = GetCurrentCharacterHash();
            if (currentHash == 0)
            {
                ShowFleecaNotification(
                    L("Credit_Notify_Title", "Thông báo"),
                    L("Credit_Error_CharacterNotFound", "Không xác định được nhân vật hiện tại."));
                return;
            }

            if (_loanActive)
            {
                ShowFleecaNotification(
                    L("Credit_CurrentLoan_Title", "Khoản vay hiện tại"),
                    string.Format(
                        L("Credit_CurrentLoan_Desc", "Bạn đang có khoản vay chưa thanh toán: ${0:N0} còn lại."),
                        _loanRemainingDebt));
                return;
            }

            _spawnRequested = true;
            _spawnExecuteAtGameTime = Game.GameTime + SpawnDelayMs;

            TryClosePhone();
        }
        catch (Exception ex)
        {
            Log("RequestLoanSpawn failed: " + ex);
        }
    }

    private long GetQuickPayTotal()
    {
        try
        {
            if (_loanRemainingDebt <= 0)
                return 0;

            return (long)Math.Ceiling((decimal)_loanRemainingDebt * (1m + QUICK_PAY_FEE_RATE));
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

            if (money >= totalPayoff)
            {
                DeductPlayerMoney(totalPayoff);

                _loanRemainingDebt = 0;
                _loanActive = false;
                _loanPrincipal = 0;
                _loanDailyDue = 0;
                _loanLastChargedDayStamp = GetInGameDayStamp();
                _nextDueCheckAtGameTime = 0;

                SaveStateForOwner(currentHash);

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

            if (seized)
            {
                _loanRemainingDebt = 0;
                _loanActive = false;
                _loanPrincipal = 0;
                _loanDailyDue = 0;
                _loanLastChargedDayStamp = GetInGameDayStamp();
                _nextDueCheckAtGameTime = 0;

                SaveStateForOwner(currentHash);

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

    private void HandleNpcSpawnTimer()
    {
        try
        {
            if (!_spawnRequested)
                return;

            if (Game.GameTime < _spawnExecuteAtGameTime)
                return;

            _spawnRequested = false;
            SpawnBankerNpc();
        }
        catch (Exception ex)
        {
            Log("HandleNpcSpawnTimer failed: " + ex);
        }
    }

    private void SpawnBankerNpc()
    {
        try
        {
            DespawnBankerNpc();

            // Spawn NPC trực tiếp tại tọa độ có sẵn, không chỉnh Z, không heading cố định
            string[] modelsToTry = new[]
            {
            "a_m_y_business_01",
            "s_m_m_bankman_01",
            "s_m_y_cop_01"
        };

            Ped spawnedNpc = null;

            foreach (string modelName in modelsToTry)
            {
                spawnedNpc = TryCreatePed(modelName, NpcSpawnPosition);
                if (spawnedNpc != null && spawnedNpc.Exists())
                    break;
            }

            if (spawnedNpc == null || !spawnedNpc.Exists())
            {
                Log("SpawnBankerNpc failed: all models failed.");
                return;
            }

            _bankerNpc = spawnedNpc;

            ShowFleecaNotification(
                   L("Credit_RequestTitle", "Yêu cầu"),
                   L("Credit_StaffWaiting", "Nhân viên ngân hàng đang đợi sẵn bạn rồi. Ra đó gặp để vay nhé!")
            );

            try
            {
                _bankerBlip = _bankerNpc.AddBlip();
                if (_bankerBlip != null && _bankerBlip.Exists())
                {
                    _bankerBlip.IsShortRange = false;
                    _bankerBlip.Sprite = BlipSprite.Standard;
                    _bankerBlip.Color = BlipColor.Green;
                    Function.Call(Hash.SET_BLIP_COLOUR, _bankerBlip.Handle, (int)BlipColor.Green);
                    SetBlipName(_bankerBlip, BankerDisplayName);
                }
                else
                {
                    Log("Create blip failed: AddBlip returned null/invalid.");
                }
            }
            catch (Exception ex)
            {
                Notification.Show("Tạo blip lỗi.");
                Log("Create banker blip failed: " + ex);
            }

            // Sau khi spawn xong, tìm ghế gần nhất và cho NPC ngồi
            TrySeatNpcOnNearestChair(_bankerNpc);
        }
        catch (Exception ex)
        {
            Notification.Show("Spawn NPC lỗi.");
            Log("SpawnBankerNpc failed: " + ex);
        }
    }

    private Ped TryCreatePed(string modelName, Vector3 position)
    {
        try
        {
            Model model = new Model(Game.GenerateHash(modelName));
            if (!model.IsValid || !model.IsInCdImage)
                return null;

            if (!model.IsLoaded)
                model.Request(2000);

            int waited = 0;
            while (!model.IsLoaded && waited < 3000)
            {
                Script.Wait(50);
                waited += 50;
            }

            if (!model.IsLoaded)
            {
                model.MarkAsNoLongerNeeded();
                return null;
            }

            try
            {
                Function.Call(Hash.CLEAR_AREA, position.X, position.Y, position.Z, 3.5f, true, false, false, false);
            }
            catch { }

            // Spawn đúng tọa độ có sẵn, không cộng Z
            Ped npc = World.CreatePed(model, position);

            if (npc == null || !npc.Exists())
            {
                model.MarkAsNoLongerNeeded();
                return null;
            }

            try { npc.IsPersistent = true; } catch { }
            try { Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, npc.Handle, true, true); } catch { }
            try { Function.Call(Hash.SET_ENTITY_INVINCIBLE, npc.Handle, true); } catch { }
            try { Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, npc.Handle, true); } catch { }
            try { Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, npc.Handle, 0, false); } catch { }
            try { Function.Call(Hash.SET_PED_CAN_RAGDOLL, npc.Handle, false); } catch { }
            try { Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, npc.Handle, false); } catch { }
            try { Function.Call(Hash.SET_PED_DIES_WHEN_INJURED, npc.Handle, false); } catch { }
            try { Function.Call(Hash.SET_PED_KEEP_TASK, npc.Handle, true); } catch { }

            try { npc.Task.ClearAllImmediately(); } catch { }

            model.MarkAsNoLongerNeeded();
            return npc;
        }
        catch
        {
            return null;
        }
    }

    private Prop FindNearestChairProp(Vector3 origin, float radius)
    {
        try
        {
            Prop nearest = null;
            float bestDistance = radius;

            foreach (Prop prop in World.GetAllProps())
            {
                if (prop == null || !prop.Exists())
                    continue;

                // FIX ở đây
                if (!IsChairModel(prop))
                    continue;

                float distance = prop.Position.DistanceTo(origin);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    nearest = prop;
                }
            }

            return nearest;
        }
        catch (Exception ex)
        {
            Log("FindNearestChairProp failed: " + ex);
            return null;
        }
    }

    private bool IsChairModel(Prop prop)
    {
        if (prop == null || !prop.Exists())
            return false;

        string modelName = prop.Model.ToString().ToLower();

        return modelName.Contains("chair")
            || modelName.Contains("seat")
            || modelName.Contains("bench")
            || modelName.Contains("stool")
            || modelName.Contains("sofa");
    }

    private void TrySeatNpcOnNearestChair(Ped npc)
    {
        try
        {
            if (npc == null || !npc.Exists() || npc.IsDead)
                return;

            Prop chair = FindNearestChairProp(npc.Position, ChairSearchRadius);
            if (chair == null || !chair.Exists())
            {
                // Không có ghế thì cho NPC đứng/đi lại tự nhiên, đừng đóng băng
                try { npc.Task.WanderAround(); } catch { }
                return;
            }

            Vector3 chairPos = chair.Position;

            try { npc.IsPersistent = true; } catch { }
            try { Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, npc.Handle, true, true); } catch { }
            try { Function.Call(Hash.SET_ENTITY_INVINCIBLE, npc.Handle, true); } catch { }
            try { Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, npc.Handle, true); } catch { }
            try { Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, npc.Handle, 0, false); } catch { }
            try { Function.Call(Hash.SET_PED_CAN_RAGDOLL, npc.Handle, false); } catch { }
            try { Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, npc.Handle, false); } catch { }
            try { Function.Call(Hash.SET_PED_DIES_WHEN_INJURED, npc.Handle, false); } catch { }
            try { Function.Call(Hash.SET_PED_KEEP_TASK, npc.Handle, true); } catch { }

            try { npc.Task.ClearAllImmediately(); } catch { }

            // Cho NPC tự đi tới gần ghế trước
            try
            {
                Function.Call(
                    Hash.TASK_FOLLOW_NAV_MESH_TO_COORD,
                    npc.Handle,
                    chairPos.X, chairPos.Y, chairPos.Z,
                    1.0f,      // tốc độ đi bộ
                    30000,     // timeout
                    1.0f,      // stopping range
                    false,
                    0.0f);
            }
            catch
            {
                // Fallback nhẹ nếu nav mesh fail
                try { npc.Task.GoTo(chairPos); } catch { }
            }

            _npcMovingToChair = npc;
            _targetChairProp = chair;
            _chairMoveDeadline = Game.GameTime + 30000;
        }
        catch (Exception ex)
        {
            Log("TrySeatNpcOnNearestChair failed: " + ex);
        }
    }

    private void UpdateNpcSeatSequence()
    {
        try
        {
            if (_npcMovingToChair == null || !_npcMovingToChair.Exists())
            {
                _npcMovingToChair = null;
                _targetChairProp = null;
                return;
            }

            if (_targetChairProp == null || !_targetChairProp.Exists())
            {
                _npcMovingToChair = null;
                _targetChairProp = null;
                return;
            }

            if (Game.GameTime > _chairMoveDeadline)
            {
                try { _npcMovingToChair.Task.ClearAllImmediately(); } catch { }
                try { _npcMovingToChair.Task.StandStill(-1); } catch { }

                _npcMovingToChair = null;
                _targetChairProp = null;
                return;
            }

            float dist = _npcMovingToChair.Position.DistanceTo(_targetChairProp.Position);
            if (dist > 1.25f)
                return;

            Vector3 chairPos = _targetChairProp.Position;
            float chairHeading = _targetChairProp.Heading;

            try { _npcMovingToChair.Task.ClearAllImmediately(); } catch { }
            try { _npcMovingToChair.Position = chairPos; } catch { }
            try { _npcMovingToChair.Heading = chairHeading; } catch { }

            try
            {
                Function.Call(
                    Hash.TASK_START_SCENARIO_AT_POSITION,
                    _npcMovingToChair.Handle,
                    ChairScenarioName,
                    chairPos.X, chairPos.Y, chairPos.Z,
                    chairHeading,
                    -1,
                    true,
                    true);
            }
            catch
            {
                try { _npcMovingToChair.Task.StartScenarioInPlace(ChairScenarioName, -1, true); } catch { }
            }

            try { Function.Call(Hash.SET_PED_KEEP_TASK, _npcMovingToChair.Handle, true); } catch { }
            try { Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, _npcMovingToChair.Handle, true); } catch { }

            _npcMovingToChair = null;
            _targetChairProp = null;
        }
        catch (Exception ex)
        {
            Log("UpdateNpcSeatSequence failed: " + ex);
        }
    }

    private void UpdateNpcInteractionPrompt()
    {
        try
        {
            if (_bankerNpc == null || !_bankerNpc.Exists() || _bankerNpc.IsDead)
                return;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return;

            float distance = player.Position.DistanceTo(_bankerNpc.Position);
            if (distance > InteractionRadius)
                return;

            long creditPoints = GetCreditPoints();
            long maxLoan = GetLoanLimitFromPoints(creditPoints);

            string help;
            if (maxLoan <= 0)
            {
                help = string.Format(
                    L("Credit_Help_NoLoan",
                    "Bạn chưa đủ điều kiện vay.\nCần chi tầm {0}.\n{1} để đóng thông báo\n{2} để hủy"),
                    FormatMoney(1000000),
                    "~INPUT_FRONTEND_ACCEPT~",
                    "~INPUT_FRONTEND_LEFT~");
            }
            else
            {
                help = string.Format(
                    L("Credit_Help_CanLoan",
                    "Bạn muốn vay tiền ngân hàng à?\nHạn mức vay tối đa là {0} nha!!!\n{1} để vay tiền\n{2} để hủy"),
                    FormatMoney(maxLoan),
                    "~INPUT_FRONTEND_ACCEPT~",
                    "~INPUT_FRONTEND_LEFT~");
            }

            GTA.UI.Screen.ShowHelpTextThisFrame(help);
        }
        catch (Exception ex)
        {
            Log("UpdateNpcInteractionPrompt failed: " + ex);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (_loanMenu != null && _loanMenu.Visible)
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Back)
                    CloseLoanMenu();

                return;
            }

            if (_bankerNpc == null || !_bankerNpc.Exists() || _bankerNpc.IsDead)
                return;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return;

            float distance = player.Position.DistanceTo(_bankerNpc.Position);
            if (distance > InteractionRadius)
                return;

            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
            {
                BeginLoanDialog();
                return;
            }

            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
            {
                DespawnBankerNpc();
                return;
            }
        }
        catch (Exception ex)
        {
            Log("OnKeyDown failed: " + ex);
        }
    }

    private void BeginLoanDialog()
    {
        try
        {
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

            if (!long.TryParse(digitsOnly, NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("Credit_InvalidAmount", "Số tiền ~HUD_COLOUR_REDLIGHT~không hợp lệ~s~."),
                    2500);
                return;
            }

            if (amount <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("Credit_AmountMustBePositive", "Số tiền phải ~HUD_COLOUR_DEGEN_YELLOW~lớn hơn 0.~s~"),
                    2500);
                return;
            }

            if (amount > maxLoan)
            {
                GTA.UI.Screen.ShowSubtitle(string.Format(
                    L("Credit_ExceedLimit", "Bạn chỉ có thể vay tối đa ~HUD_COLOUR_DEGEN_YELLOW~${0:N0}~s~ theo hạn mức hiện tại."),
                    maxLoan),
                    3000);
                return;
            }

            StartLoan(amount);
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
            _loanDailyDue = Math.Max(1L, (long)Math.Ceiling(amount * 0.0075d));

            _loanLastChargedDayStamp = GetInGameDayStamp();
            BuildDueWindowFromCurrentClock();
            _nextDueCheckAtGameTime = Game.GameTime + DueCheckIntervalMs;

            _loanActive = true;

            Game.Player.Money += (int)amount;

            ShowFleecaNotification(
                L("Credit_DisbursementTitle", "Giải ngân"),
                string.Format(
                    L("Credit_DisbursementSuccess",
                      "Khoản vay ~HUD_COLOUR_DEGEN_GREEN~${0:N0}~s~ đã được giải ngân thành công. Thu nợ trong ~HUD_COLOUR_DEGEN_CYAN~{1}~s~ từ ngày hôm sau."),
                    amount,
                    GetDueWindowText()));

            SaveStateForOwner(GetCurrentCharacterHash());
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

            // Catch-up sau khi switch nhân vật
            if (_catchUpPendingAfterSwitch)
            {
                if (Game.GameTime < _catchUpReadyAtGameTime)
                    return;

                _catchUpPendingAfterSwitch = false;
                _catchUpReadyAtGameTime = 0;

                int catchUpDays = GetElapsedInGameDaysSinceLastCharge();
                if (catchUpDays > 0)
                {
                    long catchUpDue = _loanDailyDue * (long)catchUpDays;
                    if (catchUpDue > _loanRemainingDebt)
                        catchUpDue = _loanRemainingDebt;

                    long catchUpCollected = CollectDueAmount(catchUpDue);
                    _loanRemainingDebt = Math.Max(0, _loanRemainingDebt - catchUpCollected);
                    _loanLastChargedDayStamp = GetInGameDayStamp();

                    if (_loanRemainingDebt <= 0)
                    {
                        _loanActive = false;
                        ShowFleecaNotification(
                            L("Credit_PaymentTitle", "Trả nợ"),
                            L("Credit_PaidFull", "Khoản vay đã được thanh toán đầy đủ."));
                        SaveStateForOwner(currentHash);
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

            // Check mỗi 40s
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

            long dueAmount = _loanDailyDue * (long)daysPassed;
            if (dueAmount > _loanRemainingDebt)
                dueAmount = _loanRemainingDebt;

            long collectedAmount = CollectDueAmount(dueAmount);

            _loanRemainingDebt = Math.Max(0, _loanRemainingDebt - collectedAmount);
            _loanLastChargedDayStamp = currentDayStamp;

            if (_loanRemainingDebt <= 0)
            {
                _loanActive = false;
                ShowFleecaNotification(
                    L("Credit_SettleTitle", "Tất toán khoản nợ"),
                    L("Credit_PaidFull", "Khoản vay đã được thanh toán đầy đủ."));
                SaveStateForOwner(currentHash);
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

        if (points <= 5_000_000L)
            return 3_000_000L;

        if (points <= 10_000_000L)
            return 8_000_000L;

        if (points <= 20_000_000L)
            return 15_000_000L;

        if (points <= 50_000_000L)
            return 45_000_000L;

        if (points <= 100_000_000L)
            return 90_000_000L;

        return 200_000_000L;
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
                if (entry == null)
                    continue;

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

    private void DespawnBankerNpc()
    {
        try
        {
            if (_bankerBlip != null)
            {
                try
                {
                    if (_bankerBlip.Exists())
                        _bankerBlip.Delete();
                }
                catch { }
                _bankerBlip = null;
            }

            if (_bankerNpc != null)
            {
                try
                {
                    if (_bankerNpc.Exists())
                        _bankerNpc.Delete();
                }
                catch { }
                _bankerNpc = null;
            }
        }
        catch (Exception ex)
        {
            Log("DespawnBankerNpc failed: " + ex);
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
                return;
            }

            string[] parts = File.ReadAllText(file).Split('|');

            if (parts.Length >= 7)
            {
                _loanPrincipal = ParseLong(parts[0]);
                _loanRemainingDebt = ParseLong(parts[1]);
                _loanDailyDue = ParseLong(parts[2]);
                _loanActive = parts[3].Trim() == "1" && _loanRemainingDebt > 0;
                _loanLastChargedDayStamp = (int)ParseLong(parts[4]);
                _dueWindowStartHour = (int)ParseLong(parts[5]);
                _dueWindowEndHour = (int)ParseLong(parts[6]);
            }

            _nextDueCheckAtGameTime = 0;
            _stateLoaded = true;
            _stateOwnerHash = ownerHash;
        }
        catch (Exception ex)
        {
            Log("LoadStateForOwner failed: " + ex);
            _stateLoaded = true;
        }
    }

    private void SaveStateForOwner(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return;

            Directory.CreateDirectory(_stateRoot);

            string file = GetStateFileForOwner(ownerHash);

            string payload = string.Join("|", new[]
            {
            _loanPrincipal.ToString(CultureInfo.InvariantCulture),
            _loanRemainingDebt.ToString(CultureInfo.InvariantCulture),
            _loanDailyDue.ToString(CultureInfo.InvariantCulture),
            _loanActive ? "1" : "0",
            _loanLastChargedDayStamp.ToString(CultureInfo.InvariantCulture),
            _dueWindowStartHour.ToString(CultureInfo.InvariantCulture),
            _dueWindowEndHour.ToString(CultureInfo.InvariantCulture)
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

    private void SetBlipName(Blip blip, string displayName)
    {
        try
        {
            if (blip == null || !blip.Exists())
                return;

            Function.Call(Hash.BEGIN_TEXT_COMMAND_SET_BLIP_NAME, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, displayName);
            Function.Call(Hash.END_TEXT_COMMAND_SET_BLIP_NAME, blip.Handle);
        }
        catch (Exception ex)
        {
            Log("SetBlipName failed: " + ex);
        }
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