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
using System.Windows.Forms;

public class MinotaurScript : Script
{
    private const int CONTACT_DIAL_TIMEOUT_MS = 3000;
    private const int PROMPT_TIMEOUT_MS = 15000;
    private const decimal SERVICE_FEE_RATE = 0.15m;
    private const decimal TRANSFERRED_SERVICE_FEE_RATE = 0.15m;
    private const decimal TRANSFERRED_DAILY_RATE = 0.0062m;

    private int _lastSyncedCollateralCount = -1;
    private long _lastSyncedRemainingDebt = -1;

    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Fleeca Bank Loan");

    private static readonly string MinotaurStateRoot = Path.Combine(DataRoot, "Minotaur");

    private readonly ObjectPool _uiPool = new ObjectPool();

    private static string L(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    private static string FormatMoney(long value)
    {
        if (value < 0) value = 0;
        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private sealed class MinotaurState
    {
        public int OwnerHash;
        public bool Active;
        public long CurrentDebt;
        public long ProcessingFee;
        public long NewDebt;
        public int CollateralCount;
        public int LoanStateVersion;
        public DateTime UpdatedUtc;
        public bool TransferredFromParadigmRoyalty;
        public decimal TransferredDailyRate;
    }

    private sealed class MinotaurInfoItem : NativeItem
    {
        public MinotaurInfoItem(string title, string description)
            : base(title, description)
        {
        }

        public override void Recalculate(System.Drawing.PointF pos, System.Drawing.SizeF size, bool selected)
        {
            base.Recalculate(pos, size, selected);
            UpdateColors();
        }
    }

    private CustomiFruit _phoneInstance;
    private iFruitContact _minotaurContact;
    private bool _minotaurContactAnsweredBound;
    private bool _contactAdded;

    private bool _promptPending;
    private int _promptExpireAt;

    private bool _serviceMenuInitialized;
    private NativeMenu _serviceMenu;
    private NativeItem _debtItem;
    private NativeItem _feeItem;
    private NativeItem _newDebtItem;
    private NativeItem _rateItem;
    private NativeItem _confirmItem;
    private NativeItem _declineItem;

    private MinotaurState _cachedState;
    private int _serviceOwnerHash;

    public MinotaurScript()
    {
        Interval = 0;
        Tick += OnTick;
        KeyDown += OnKeyDown;
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            SyncActiveMinotaurState();   // NEW: đồng bộ trạng thái Minotaur <-> Fleeca mỗi tick

            EnsureMinotaurContactRegistered();
            UpdatePromptState();
            ProcessUi();
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

            if (IsAnyServiceMenuVisible())
            {
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                    CloseAllMenus();
            }
        }
        catch (Exception ex)
        {
            Log("OnKeyDown failed: " + ex);
        }
    }

    private void EnsureMinotaurContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_phoneInstance, phone))
            {
                _phoneInstance = phone;
                _minotaurContact = null;
                _minotaurContactAnsweredBound = false;
                _contactAdded = false;
            }

            string contactName = GetContactName();
            bool contactAllowed = !IsMinotaurStateActiveForCurrentCharacter();

            if (_minotaurContact == null)
            {
                _minotaurContact = phone.Contacts.FirstOrDefault(c =>
                    string.Equals(c.Name, contactName, StringComparison.OrdinalIgnoreCase));
            }

            if (_minotaurContact == null)
            {
                _minotaurContact = new iFruitContact(contactName)
                {
                    Active = contactAllowed,
                    DialTimeout = CONTACT_DIAL_TIMEOUT_MS,
                    Bold = false,
                    Icon = ContactIcon.Minotaur
                };

                _minotaurContact.Answered += OnMinotaurAnswered;
                _minotaurContactAnsweredBound = true;
                phone.Contacts.Add(_minotaurContact);
            }
            else
            {
                _minotaurContact.Active = contactAllowed;

                if (!_minotaurContactAnsweredBound)
                {
                    _minotaurContact.Answered += OnMinotaurAnswered;
                    _minotaurContactAnsweredBound = true;
                }
            }

            _contactAdded = true;
        }
        catch (Exception ex)
        {
            Log("EnsureMinotaurContactRegistered failed: " + ex);
        }
    }

    private bool IsMinotaurStateActiveForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return false;

            return IsMinotaurStateActiveForOwner(ownerHash);
        }
        catch
        {
            return false;
        }
    }

    private bool IsMinotaurStateActiveForOwner(int ownerHash)
    {
        try
        {
            string file = GetStateFileForOwner(ownerHash);
            if (!File.Exists(file))
                return false;

            string[] parts = File.ReadAllText(file).Split('|');
            if (parts.Length < 2)
                return false;

            int parsedOwner = (int)ParseLong(parts[0]);
            if (parsedOwner != ownerHash)
                return false;

            return parts[1].Trim() == "1";
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadFleecaLoanStateForOwner(int ownerHash, out long remainingDebt, out bool active)
    {
        remainingDebt = 0;
        active = false;

        try
        {
            string file = GetFleecaLoanStateFileForOwner(ownerHash);
            if (!File.Exists(file))
                return false;

            string[] parts = File.ReadAllText(file).Split('|');
            if (parts.Length < 4)
                return false;

            remainingDebt = ParseLong(parts[1]);
            active = parts[3].Trim() == "1" && remainingDebt > 0;
            return true;
        }
        catch
        {
            remainingDebt = 0;
            active = false;
            return false;
        }
    }

    private void SyncActiveMinotaurState()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            MinotaurState loanState = ReadLoanState(ownerHash);
            if (loanState == null || !loanState.Active || loanState.CurrentDebt <= 0)
            {
                if (IsMinotaurStateActiveForOwner(ownerHash))
                    TryDeleteMinotaurState(ownerHash);

                _lastSyncedCollateralCount = -1;
                _lastSyncedRemainingDebt = -1;

                if (_minotaurContact != null)
                    _minotaurContact.Active = true;

                return;
            }

            // Nếu Minotaur chưa được kích hoạt thì không ép gì cả, chỉ để contact bật.
            if (!IsMinotaurStateActiveForOwner(ownerHash))
            {
                if (_minotaurContact != null)
                    _minotaurContact.Active = true;

                return;
            }

            int collateralCount = CountCollateralVehicles(ownerHash);
            long remainingDebt = loanState.CurrentDebt;
            bool transferredLoan = loanState.TransferredFromParadigmRoyalty;

            // Chỉ ghi lại khi có thay đổi để tránh spam file I/O mỗi tick
            if (remainingDebt == _lastSyncedRemainingDebt && collateralCount == _lastSyncedCollateralCount)
            {
                if (_minotaurContact != null)
                    _minotaurContact.Active = false;
                return;
            }

            _lastSyncedRemainingDebt = remainingDebt;
            _lastSyncedCollateralCount = collateralCount;

            var state = new MinotaurState
            {
                OwnerHash = ownerHash,
                Active = true,
                CurrentDebt = remainingDebt,
                ProcessingFee = 0,
                NewDebt = remainingDebt,
                CollateralCount = collateralCount,
                LoanStateVersion = 1,
                UpdatedUtc = DateTime.UtcNow,
                TransferredFromParadigmRoyalty = transferredLoan,
                TransferredDailyRate = transferredLoan ? TRANSFERRED_DAILY_RATE : 0m
            };

            _cachedState = state;
            _serviceOwnerHash = ownerHash;

            SaveMinotaurState(state);

            // Đồng bộ lại loan_state để file lưu trữ luôn phản ánh rate mới
            TryPatchFleecaLoanStateFile(ownerHash, remainingDebt, collateralCount, false, transferredLoan);

            if (_minotaurContact != null)
                _minotaurContact.Active = false;
        }
        catch (Exception ex)
        {
            Log("SyncActiveMinotaurState failed: " + ex);
        }
    }

    private string GetContactName()
    {
        return L("Minotaur_ContactName", "Minotaur");
    }

    private string GetMenuTitle()
    {
        return L("Minotaur_MenuTitle", "Minotaur");
    }

    private string GetMenuSubtitle()
    {
        return L("Minotaur_MenuSubtitle", "CHI TIẾT DỊCH VỤ CỦA MINOTAUR");
    }

    private void OnMinotaurAnswered(iFruitContact sender)
    {
        try
        {
            if (IsMinotaurStateActiveForCurrentCharacter())
            {
                ShowMinotaurNotification(
                    L("Minotaur_ErrorTitle", "Đang hoạt động"),
                    L("Minotaur_AlreadyActive", "Minotaur đang tác động lên khoản vay này rồi. Bạn không thể kích hoạt lại."));
                TryClosePhone();
                return;
            }

            if (!TryGetCurrentLoanState(out MinotaurState state))
            {
                ShowMinotaurNotification(
                    L("Minotaur_ErrorTitle", "Thất bại"),
                    L("Minotaur_NoLoan", "Bạn không có khoản vay Fleeca đang hoạt động."));
                TryClosePhone();
                return;
            }

            if (state.CurrentDebt <= 0)
            {
                ShowMinotaurNotification(
                    L("Minotaur_ErrorTitle", "Thông báo"),
                    L("Minotaur_NoDebt", "Không tìm thấy khoản nợ hợp lệ để xử lý."));
                TryClosePhone();
                return;
            }

            _cachedState = state;
            _serviceOwnerHash = state.OwnerHash;
            _promptPending = true;
            _promptExpireAt = Game.GameTime + PROMPT_TIMEOUT_MS;

            TryClosePhone();
        }
        catch (Exception ex)
        {
            Log("OnMinotaurAnswered failed: " + ex);
        }
    }

    private void UpdatePromptState()
    {
        try
        {
            if (!_promptPending)
                return;

            if (_cachedState == null || !_cachedState.Active)
            {
                CancelPrompt();
                return;
            }

            if (Game.GameTime > _promptExpireAt)
            {
                CancelPrompt();
                return;
            }

            string text = L(
                "Minotaur_Prompt",
                "Công ty sẽ ~y~mua lại và điều chỉnh khoản nợ~s~ tại Fleeca. Bạn chắc sẽ nhờ công ty chứ?\n{0} để đồng ý\n{1} để hủy");

            GTA.UI.Screen.ShowHelpTextThisFrame(string.Format(
                CultureInfo.InvariantCulture,
                text,
                "~INPUT_FRONTEND_ACCEPT~",
                "~INPUT_FRONTEND_LEFT~"));
        }
        catch (Exception ex)
        {
            Log("UpdatePromptState failed: " + ex);
        }
    }

    private void AcceptPrompt()
    {
        try
        {
            if (!_promptPending)
                return;

            _promptPending = false;
            EnsureServiceMenuCreated();
            RefreshServiceMenu();

            if (_serviceMenu != null)
            {
                CloseAllMenus();
                _serviceMenu.Visible = true;
                UpdateMenuMouseState();
            }
        }
        catch (Exception ex)
        {
            Log("AcceptPrompt failed: " + ex);
        }
    }

    private void CancelPrompt()
    {
        try
        {
            _promptPending = false;
            _cachedState = null;
            _serviceOwnerHash = 0;

            ShowMinotaurNotification(
                L("Minotaur_CancelTitle", "Đã hủy"),
                L("Minotaur_CancelDesc", "Bạn đã từ chối dịch vụ Minotaur."));

            TryClosePhone();
        }
        catch (Exception ex)
        {
            Log("CancelPrompt failed: " + ex);
        }
    }

    private bool IsAnyServiceMenuVisible()
    {
        return _serviceMenu != null && _serviceMenu.Visible;
    }

    private void EnsureServiceMenuCreated()
    {
        try
        {
            if (_serviceMenuInitialized)
                return;

            _serviceMenu = new NativeMenu(GetMenuTitle(), GetMenuSubtitle());
            _uiPool.Add(_serviceMenu);

            _debtItem = new MinotaurInfoItem("", "");
            _feeItem = new MinotaurInfoItem("", "");
            _newDebtItem = new MinotaurInfoItem("", "");
            _rateItem = new MinotaurInfoItem("", "");

            _confirmItem = new NativeItem(
                L("Minotaur_Confirm", "Xác nhận dịch vụ"),
                L("Minotaur_ConfirmDesc", "Áp dụng thay đổi vào khoản vay Fleeca ngay bây giờ"));

            _declineItem = new NativeItem(
                L("Minotaur_Decline", "Từ chối dịch vụ"),
                L("Minotaur_DeclineDesc", "Quay lại và không thay đổi gì"));

            _confirmItem.Activated += (s, e) =>
            {
                ActivateService();
            };

            _declineItem.Activated += (s, e) =>
            {
                CloseAllMenus();
            };

            _serviceMenu.Add(_debtItem);
            _serviceMenu.Add(_feeItem);
            _serviceMenu.Add(_newDebtItem);
            _serviceMenu.Add(_rateItem);
            _serviceMenu.Add(_confirmItem);
            _serviceMenu.Add(_declineItem);

            _serviceMenuInitialized = true;
            UpdateMenuMouseState();
        }
        catch (Exception ex)
        {
            Log("EnsureServiceMenuCreated failed: " + ex);
        }
    }

    private void RefreshServiceMenu()
    {
        try
        {
            if (_serviceMenu == null || _cachedState == null)
                return;

            long currentDebt = Math.Max(0, _cachedState.CurrentDebt);
            bool transferredLoan = _cachedState.TransferredFromParadigmRoyalty;
            decimal feeRate = transferredLoan ? TRANSFERRED_SERVICE_FEE_RATE : SERVICE_FEE_RATE;
            decimal dailyRate = transferredLoan ? TRANSFERRED_DAILY_RATE : GetEffectiveMinotaurRate(_cachedState.CollateralCount);
            
            long fee = Math.Max(0, (long)Math.Ceiling(currentDebt * feeRate));
            long newDebt = currentDebt + fee;
            decimal rate = dailyRate;

            _cachedState.ProcessingFee = fee;
            _cachedState.NewDebt = newDebt;

            _debtItem.Title = string.Format(
                CultureInfo.InvariantCulture,
                L("Minotaur_CurrentDebt", "Khoản nợ hiện tại: {0}"),
                FormatMoney(currentDebt));

            _feeItem.Title = string.Format(
                CultureInfo.InvariantCulture,
                L("Minotaur_ProcessingFee", "Khoản nợ thêm: {0}"),
                FormatMoney(fee));

            _newDebtItem.Title = string.Format(
                CultureInfo.InvariantCulture,
                L("Minotaur_NewDebt", "Khoản nợ mới: {0}"),
                FormatMoney(newDebt));

            _rateItem.Title = string.Format(
                CultureInfo.InvariantCulture,
                L("Minotaur_NewRate", "Lãi suất mới: {0:0.00}%/ngày"),
                (double)(rate * 100m));

            _rateItem.Description = string.Format(
                CultureInfo.InvariantCulture,
                L("Minotaur_NewRateDesc",
                "Cơ chế Minotaur chỉ thay đổi lãi của Fleeca khi đang tác động."));

            _feeItem.Description = L(
                "Minotaur_ProcessingFeeDesc",
                transferredLoan
                    ? "Khoản nợ chuyển chỉ bị cộng thêm 15% trước khi áp dụng lãi cố định 0.62%/ngày."
                    : "Khoản nợ thêm 15% được cộng thêm vào khoản nợ hiện tại trước khi áp dụng lãi mới.");

            _newDebtItem.Description = L(
                "Minotaur_NewDebtDesc",
                "Khoản nợ mới = khoản nợ hiện tại + khoản nợ thêm 15%.");

            _confirmItem.Title = L("Minotaur_Confirm", "Xác nhận dịch vụ");
            _declineItem.Title = L("Minotaur_Decline", "Từ chối dịch vụ");
        }
        catch (Exception ex)
        {
            Log("RefreshServiceMenu failed: " + ex);
        }
    }

    private void ActivateService()
    {
        try
        {
            if (_cachedState == null)
                return;

            if (!_cachedState.Active || _cachedState.CurrentDebt <= 0)
            {
                ShowMinotaurNotification(
                    L("Minotaur_ErrorTitle", "Lỗi"),
                    L("Minotaur_NoLoan", "Bạn không có khoản vay Fleeca đang hoạt động."));
                CloseAllMenus();
                return;
            }

            if (IsMinotaurStateActiveForCurrentCharacter())
            {
                ShowMinotaurNotification(
                    L("Minotaur_ErrorTitle", "Lỗi"),
                    L("Minotaur_AlreadyActive", "Minotaur đang tác động lên khoản vay này rồi. Bạn không thể kích hoạt lại."));
                CloseAllMenus();
                return;
            }

            bool transferredLoan = _cachedState.TransferredFromParadigmRoyalty;
            if (!transferredLoan && _cachedState.CollateralCount <= 0)
            {
                ShowMinotaurNotification(
                    L("Minotaur_ErrorTitle", "Lỗi"),
                    L("Minotaur_NoCollateral", "Minotaur chỉ hoạt động khi Fleeca đang giữ ít nhất 1 xe thế chấp."));
                CloseAllMenus();
                return;
            }

            long currentDebt = Math.Max(0, _cachedState.CurrentDebt);
            decimal serviceFeeRate = transferredLoan ? TRANSFERRED_SERVICE_FEE_RATE : SERVICE_FEE_RATE;
            long fee = Math.Max(0, (long)Math.Ceiling(currentDebt * serviceFeeRate));
            long newDebt = currentDebt + fee;
            int collateralCount = CountCollateralVehicles(_cachedState.OwnerHash);
            decimal appliedRate = transferredLoan ? TRANSFERRED_DAILY_RATE : GetEffectiveMinotaurRate(collateralCount);

            _cachedState.Active = true;
            _cachedState.CurrentDebt = newDebt;
            _cachedState.ProcessingFee = fee;
            _cachedState.NewDebt = newDebt;
            _cachedState.CollateralCount = collateralCount;
            _cachedState.UpdatedUtc = DateTime.UtcNow;
            _cachedState.TransferredFromParadigmRoyalty = transferredLoan;
            _cachedState.TransferredDailyRate = transferredLoan ? TRANSFERRED_DAILY_RATE : 0m;

            _lastSyncedRemainingDebt = newDebt;
            _lastSyncedCollateralCount = collateralCount;

            SaveMinotaurState(_cachedState);

            // updatePrincipal = true để khoản vay ban đầu được ghi thành khoản nợ sau phí dịch vụ
            TryPatchFleecaLoanStateFile(_cachedState.OwnerHash, newDebt, collateralCount, true, transferredLoan);

            ShowMinotaurNotification(
                L("Minotaur_SuccessTitle", "Minotaur"),
                string.Format(
                    CultureInfo.InvariantCulture,
                    transferredLoan
                        ? L("Minotaur_SuccessDescTransferred", "Dịch vụ đã được kích hoạt cho nợ chuyển. Khoản nợ mới: {0}. Lãi cố định áp dụng: {1:0.00}%/ngày.")
                        : L("Minotaur_SuccessDesc", "Dịch vụ đã được kích hoạt. Khoản nợ mới: {0}."),
                    FormatMoney(newDebt),
                    (double)(appliedRate * 100m)));

            CloseAllMenus();
        }
        catch (Exception ex)
        {
            Log("ActivateService failed: " + ex);
        }
    }

    private void CloseAllMenus()
    {
        try
        {
            if (_serviceMenu != null)
                _serviceMenu.Visible = false;
        }
        catch { }
    }

    private void UpdateMenuMouseState()
    {
        try
        {
            if (_serviceMenu == null)
                return;

            _serviceMenu.MouseBehavior = MenuMouseBehavior.Disabled;
            _serviceMenu.ResetCursorWhenOpened = false;
            _serviceMenu.CloseOnInvalidClick = false;
            _serviceMenu.RotateCamera = true;
        }
        catch { }
    }

    private void ProcessUi()
    {
        try
        {
            _uiPool.Process();
            UpdateMenuMouseState();
        }
        catch (Exception ex)
        {
            Log("ProcessUi failed: " + ex);
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

    private void ShowMinotaurNotification(string subtitle, string message)
    {
        try
        {
            PlayFrontendSound("Text_Arrive_Tone", "Phone_SoundSet_Default");
            Notification.Show(NotificationIcon.Minotaur, GetMenuTitle(), subtitle, message);
        }
        catch
        {
            Notification.Show(message);
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

    private static string GetStateFileForOwner(int ownerHash)
    {
        try
        {
            Directory.CreateDirectory(MinotaurStateRoot);
            return Path.Combine(MinotaurStateRoot, $"minotaur_state_{ownerHash}.dat");
        }
        catch
        {
            return Path.Combine(MinotaurStateRoot, $"minotaur_state_{ownerHash}.dat");
        }
    }

    private static string GetFleecaLoanStateFileForOwner(int ownerHash)
    {
        return Path.Combine(DataRoot, $"loan_state_{ownerHash}.dat");
    }

    private bool TryGetCurrentLoanState(out MinotaurState state)
    {
        state = null;

        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return false;

            var parsed = ReadLoanState(ownerHash);
            if (parsed == null || !parsed.Active || parsed.CurrentDebt <= 0)
                return false;

            int collateralCount = CountCollateralVehicles(ownerHash);

            parsed.CollateralCount = collateralCount;
            state = parsed;
            return true;
        }
        catch (Exception ex)
        {
            Log("TryGetCurrentLoanState failed: " + ex);
            return false;
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
            if (h == -1692214353 || h == 225514697 || h == -1686040670)
                return h;
        }
        catch { }

        return 0;
    }

    private static MinotaurState ReadLoanState(int ownerHash)
    {
        try
        {
            string file = GetFleecaLoanStateFileForOwner(ownerHash);
            if (!File.Exists(file))
            {
                TryDeleteMinotaurState(ownerHash);
                return null;
            }

            string[] parts = File.ReadAllText(file).Split('|');
            if (parts.Length < 4)
            {
                TryDeleteMinotaurState(ownerHash);
                return null;
            }

            long principal = ParseLong(parts[0]);
            long remaining = ParseLong(parts[1]);
            bool active = parts[3].Trim() == "1" && remaining > 0;
            bool transferredLoan = parts.Length >= 14 && ParseBool(parts[13]);
            decimal transferredDailyRate = parts.Length >= 15 ? ParseDecimal(parts[14]) : 0m;

            if (!active || remaining <= 0)
            {
                TryDeleteMinotaurState(ownerHash);
                return null;
            }

            return new MinotaurState
            {
                OwnerHash = ownerHash,
                Active = true,
                CurrentDebt = remaining,
                ProcessingFee = 0,
                NewDebt = remaining,
                CollateralCount = 0,
                LoanStateVersion = parts.Length >= 13 ? (int)ParseLong(parts[12]) : 1,
                UpdatedUtc = DateTime.UtcNow,
                TransferredFromParadigmRoyalty = transferredLoan,
                TransferredDailyRate = transferredDailyRate
            };
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteMinotaurState(int ownerHash)
    {
        try
        {
            string file = GetStateFileForOwner(ownerHash);
            if (File.Exists(file))
                File.Delete(file);
        }
        catch
        {
        }
    }

    private static void SaveMinotaurState(MinotaurState state)
    {
        try
        {
            if (state == null || state.OwnerHash == 0)
                return;

            Directory.CreateDirectory(MinotaurStateRoot);
            string file = GetStateFileForOwner(state.OwnerHash);

            string payload = string.Join("|", new[]
            {
                state.OwnerHash.ToString(CultureInfo.InvariantCulture),
                state.Active ? "1" : "0",
                state.CurrentDebt.ToString(CultureInfo.InvariantCulture),
                state.ProcessingFee.ToString(CultureInfo.InvariantCulture),
                state.NewDebt.ToString(CultureInfo.InvariantCulture),
                state.CollateralCount.ToString(CultureInfo.InvariantCulture),
                state.LoanStateVersion.ToString(CultureInfo.InvariantCulture),
                state.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture)
            });

            File.WriteAllText(file, payload);
        }
        catch (Exception ex)
        {
            Log("SaveMinotaurState failed: " + ex);
        }
    }

    private static decimal GetEffectiveMinotaurRate(int collateralCount)
    {
        try
        {
            if (CreditDefaultSwapBridge.TryGetOverrideRate(out decimal cdsRate) && cdsRate > 0m)
                return cdsRate;
        }
        catch { }

        if (collateralCount >= 3) return 0.0058m;
        if (collateralCount == 2) return 0.0113m;
        if (collateralCount == 1) return 0.0325m;
        return 0.10m;
    }

    private static long ParseLong(string value)
    {
        try
        {
            if (long.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
                return n;
        }
        catch { }
        return 0;
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

    private int CountCollateralVehicles(int ownerHash)
    {
        try
        {
            Type pmType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("PersistentManager", false, true))
                .FirstOrDefault(t => t != null);

            if (pmType == null)
                return 0;

            FieldInfo vehiclesField = pmType.GetField("_persistVehicles", BindingFlags.NonPublic | BindingFlags.Static);
            if (vehiclesField == null)
                return 0;

            var list = vehiclesField.GetValue(null) as System.Collections.IEnumerable;
            if (list == null)
                return 0;

            int count = 0;
            foreach (object entry in list)
            {
                try
                {
                    if (entry == null)
                        continue;

                    if (!IsOwnedBy(entry, ownerHash))
                        continue;

                    if (IsCollateralLocked(entry))
                        count++;
                }
                catch { }
            }

            return count;
        }
        catch (Exception ex)
        {
            Log("CountCollateralVehicles failed: " + ex);
            return 0;
        }
    }

    private static bool IsOwnedBy(object entry, int ownerHash)
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

    private static bool IsCollateralLocked(object entry)
    {
        try
        {
            var t = entry.GetType();
            FieldInfo f = t.GetField("IsCollateralLocked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null)
                return false;

            object v = f.GetValue(entry);
            if (v is bool b)
                return b;
        }
        catch { }

        return false;
    }

    private static void TryPatchFleecaLoanStateFile(int ownerHash, long debtValue, int collateralCount, bool updatePrincipal, bool transferredLoan = false)
    {
        try
        {
            string file = GetFleecaLoanStateFileForOwner(ownerHash);
            if (!File.Exists(file))
                return;

            string[] parts = File.ReadAllText(file).Split('|');
            if (parts.Length < 13)
                return;

            decimal rate = transferredLoan ? TRANSFERRED_DAILY_RATE : GetEffectiveMinotaurRate(collateralCount);
            long dailyDue = (long)Math.Ceiling((decimal)debtValue * rate);

            if (updatePrincipal)
                parts[0] = debtValue.ToString(CultureInfo.InvariantCulture);

            parts[1] = debtValue.ToString(CultureInfo.InvariantCulture);             // remaining debt
            parts[2] = dailyDue.ToString(CultureInfo.InvariantCulture);              // daily due theo collateral hiện tại
            parts[3] = debtValue > 0 ? "1" : "0";                                    // active
            parts[7] = transferredLoan ? "0" : (collateralCount > 0 ? "1" : "0");    // transferred loan không cần cọc

            File.WriteAllText(file, string.Join("|", parts));
        }
        catch (Exception ex)
        {
            Log("TryPatchFleecaLoanStateFile failed: " + ex);
        }
    }

    private static void Log(string message)
    {
        try
        {
            // silent by default
        }
        catch { }
    }
}

public static class MinotaurBankBridge
{
    private const decimal RATE_3_COLLATERAL = 0.0058m; // 0.58%
    private const decimal RATE_2_COLLATERAL = 0.0113m; // 1.13%
    private const decimal RATE_1_COLLATERAL = 0.0325m; // 3.25%
    private const decimal RATE_0_COLLATERAL = 0.10m;   // 10.00%
    private const decimal TRANSFERRED_DAILY_RATE = 0.0062m; // 0.62%

    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Fleeca Bank Loan");

    public static void ClearMinotaurStateForOwner(int ownerHash)
    {
        try
        {
            string file = Path.Combine(DataRoot, "Minotaur", $"minotaur_state_{ownerHash}.dat");
            if (File.Exists(file))
                File.Delete(file);
        }
        catch
        {
        }
    }

    private static int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists())
                return 0;

            return p.Model.Hash;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryReadMinotaurActivationState(int ownerHash, out bool active)
    {
        active = false;

        try
        {
            string file = Path.Combine(DataRoot, "Minotaur", $"minotaur_state_{ownerHash}.dat");
            if (!File.Exists(file))
                return false;

            string[] stored = File.ReadAllText(file).Split('|');
            if (stored.Length < 2)
                return false;

            int parsedOwner = (int)ParseLong(stored[0]);
            if (parsedOwner != ownerHash)
                return false;

            active = stored[1].Trim() == "1";
            return true;
        }
        catch
        {
            active = false;
            return false;
        }
    }

    private static bool TryReadFleecaLoanState(int ownerHash, out long remainingDebt, out bool active, out bool transferredFromParadigmRoyalty)
    {
        remainingDebt = 0;
        active = false;
        transferredFromParadigmRoyalty = false;

        try
        {
            string file = Path.Combine(DataRoot, $"loan_state_{ownerHash}.dat");
            if (!File.Exists(file))
                return false;

            string[] parts = File.ReadAllText(file).Split('|');
            if (parts.Length < 4)
                return false;

            remainingDebt = ParseLong(parts[1]);
            active = parts[3].Trim() == "1" && remainingDebt > 0;
            transferredFromParadigmRoyalty = parts.Length >= 14 && ParseBool(parts[13]);
            return true;
        }
        catch
        {
            remainingDebt = 0;
            active = false;
            transferredFromParadigmRoyalty = false;
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

            if (!TryReadMinotaurActivationState(ownerHash, out bool active) || !active)
                return false;

            if (!TryReadFleecaLoanState(ownerHash, out long remainingDebt, out bool loanActive, out bool transferredLoan) || !loanActive || remainingDebt <= 0)
            {
                ClearMinotaurStateForOwner(ownerHash);
                return false;
            }

            int collateralCount = CountCollateralVehicles(ownerHash);
            rate = transferredLoan ? TRANSFERRED_DAILY_RATE : GetEffectiveRate(collateralCount);
            return true;
        }
        catch
        {
            rate = 0m;
            return false;
        }
    }

    public static bool TryGetOverrideDebtAndRate(out long debt, out decimal rate)
    {
        debt = 0;
        rate = 0m;

        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return false;

            if (!TryReadMinotaurActivationState(ownerHash, out bool active) || !active)
                return false;

            if (!TryReadFleecaLoanState(ownerHash, out long remainingDebt, out bool loanActive, out bool transferredLoan) || !loanActive || remainingDebt <= 0)
            {
                ClearMinotaurStateForOwner(ownerHash);
                return false;
            }

            int collateralCount = CountCollateralVehicles(ownerHash);

            debt = remainingDebt;   // luôn lấy debt hiện tại từ Fleeca
            rate = transferredLoan ? TRANSFERRED_DAILY_RATE : GetEffectiveRate(collateralCount);
            return true;
        }
        catch
        {
            debt = 0;
            rate = 0m;
            return false;
        }
    }

    private static int CountCollateralVehicles(int ownerHash)
    {
        try
        {
            Type pmType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("PersistentManager", false, true))
                .FirstOrDefault(t => t != null);

            if (pmType == null)
                return 0;

            FieldInfo vehiclesField = pmType.GetField("_persistVehicles", BindingFlags.NonPublic | BindingFlags.Static);
            if (vehiclesField == null)
                return 0;

            var list = vehiclesField.GetValue(null) as System.Collections.IEnumerable;
            if (list == null)
                return 0;

            int count = 0;
            foreach (object entry in list)
            {
                try
                {
                    if (entry == null)
                        continue;

                    if (!IsOwnedBy(entry, ownerHash))
                        continue;

                    if (IsCollateralLocked(entry))
                        count++;
                }
                catch { }
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsOwnedBy(object entry, int ownerHash)
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

    private static bool IsCollateralLocked(object entry)
    {
        try
        {
            var t = entry.GetType();
            FieldInfo f = t.GetField("IsCollateralLocked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null)
                return false;

            object v = f.GetValue(entry);
            if (v is bool b)
                return b;
        }
        catch { }

        return false;
    }

    private static decimal GetEffectiveRate(int collateralCount)
    {
        try
        {
            if (CreditDefaultSwapBridge.TryGetOverrideRate(out decimal cdsRate) && cdsRate > 0m)
                return cdsRate;
        }
        catch { }

        if (collateralCount >= 3) return RATE_3_COLLATERAL;
        if (collateralCount == 2) return RATE_2_COLLATERAL;
        if (collateralCount == 1) return RATE_1_COLLATERAL;
        return RATE_0_COLLATERAL;
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

    private static long ParseLong(string value)
    {
        try
        {
            if (long.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
                return n;
        }
        catch { }
        return 0;
    }
}