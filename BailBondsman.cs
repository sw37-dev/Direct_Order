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
using System.Windows.Forms;

public class SentenceReduction : Script
{
    private static string ContactDisplayName => L("SentenceReduction_ContactDisplayName", "Bail Bondsman");
    private static string NotificationBrand => L("SentenceReduction_NotificationBrand", "Bail Bondsman");

    private const int NotificationTimeoutMs = 3000;
    private const int CheckIntervalMs = 1000;
    private const int MenuCooldownMs = 1000;

    private static readonly string HackerStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Hacker");

    private static readonly string SentenceReductionStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "SentenceReduction");

    private readonly ObjectPool _uiPool = new ObjectPool();
    private NativeMenu _menu;
    private bool _menuInitialized = false;

    private NativeItem _customerItem;
    private NativeItem _statusItem;
    private NativeItem _feeItem;
    private NativeItem _executorItem;
    private NativeItem _confirmItem;
    private NativeItem _cancelItem;

    private CustomiFruit _phoneInstance = null;
    private iFruitContact _contact = null;
    private bool _contactAnsweredBound = false;

    private int _menuCooldownUntil = 0;
    private Snapshot _currentSnapshot = null;

    private sealed class HackerStateData
    {
        public long DirtyMoney = 0;
        public DateTime? AtmLockedUntil = null;
        public DateTime? FleecaLockedUntil = null;
        public bool AtmUnlockNoticeShown = false;
        public bool FleecaUnlockNoticeShown = false;
        public List<string> PassthroughLines = new List<string>();
    }

    private sealed class SentenceReductionStateData
    {
        public DateTime? ConsumedUntil = null;
    }

    private sealed class Snapshot
    {
        public int OwnerHash;
        public string CharacterName;

        public bool LomLocked;
        public bool FleecaLocked;
        public TimeSpan FleecaRemaining;

        public long DirtyMoneyBalance;
        public long CashBalance;

        public long DirtyFee;
        public long CashFee;
        public long TotalFee;

        public DateTime? FleecaLockedUntil;
        public bool ReductionConsumed;

        public bool Eligible => !LomLocked && FleecaLocked && !ReductionConsumed;
    }

    public SentenceReduction()
    {
        Language.Initialize();
        Tick += OnTick;
        KeyDown += OnKeyDown;
        Aborted += OnAborted;

        Interval = CheckIntervalMs;
    }

    private static string L(string key, string fallback = "")
    {
        return Language.Get(key, fallback);
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            Snapshot snapshot;
            bool hasSnapshot = TryBuildSnapshot(out snapshot);

            EnsureBailBondsmanContactRegistered(hasSnapshot);

            if (_menu != null && _menu.Visible)
            {
                if (!hasSnapshot || snapshot == null || !snapshot.Eligible)
                {
                    CloseMenu(false);
                    return;
                }

                RefreshMenu(snapshot);
                UpdateLemonUiMouseState();

                Interval = 0;
                _uiPool.Process();
                UpdateLemonUiMouseState();
                return;
            }

            if (_uiPool.AreAnyVisible)
            {
                UpdateLemonUiMouseState();
                Interval = 0;
                _uiPool.Process();
                UpdateLemonUiMouseState();
                return;
            }

            Interval = CheckIntervalMs;
        }
        catch (Exception ex)
        {
            Log(L("SentenceReduction_LogOnTickFailed", "OnTick failed: ") + ex);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (_menu != null && _menu.Visible)
            {
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                {
                    CloseMenu(true);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log(L("SentenceReduction_LogOnKeyDownFailed", "OnKeyDown failed: ") + ex);
        }
    }

    private void OnAborted(object sender, EventArgs e)
    {
        try
        {
            CloseMenu(false);
            ResetContactState();
        }
        catch
        {
        }
    }

    private void ResetContactState()
    {
        _contact = null;
        _contactAnsweredBound = false;
        _phoneInstance = null;
    }

    private void EnsureBailBondsmanContactRegistered(bool hasSnapshot)
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
                _contactAnsweredBound = false;
            }

            Snapshot snapshot;
            bool ok = TryBuildSnapshot(out snapshot);

            bool contactShouldBeActive = ok && snapshot != null && snapshot.Eligible;

            if (_contact == null)
            {
                _contact = phone.Contacts.FirstOrDefault(c =>
                    string.Equals(c.Name, ContactDisplayName, StringComparison.OrdinalIgnoreCase));
            }

            if (_contact == null)
            {
                _contact = new iFruitContact(ContactDisplayName)
                {
                    Active = contactShouldBeActive,
                    DialTimeout = 2000,
                    Bold = false,
                    Icon = ContactIcon.MP_FmContact
                };

                _contact.Answered += OnContactAnswered;
                _contactAnsweredBound = true;
                phone.Contacts.Add(_contact);
            }
            else
            {
                _contact.Active = contactShouldBeActive;
                _contact.DialTimeout = 2000;
                _contact.Bold = false;
                _contact.Icon = ContactIcon.MP_FmContact;

                if (!_contactAnsweredBound)
                {
                    _contact.Answered += OnContactAnswered;
                    _contactAnsweredBound = true;
                }
            }
        }
        catch (Exception ex)
        {
            Log(L("SentenceReduction_LogEnsureContactFailed", "EnsureBailBondsmanContactRegistered failed: ") + ex);
        }
    }

    private void OnContactAnswered(iFruitContact sender)
    {
        try
        {
            Snapshot snapshot;
            if (!TryBuildSnapshot(out snapshot) || snapshot == null)
            {
                ShowSentenceNotification(
                    L("SentenceReduction_DeniedTitle", "Từ chối"),
                    L("SentenceReduction_DeniedMessage", "Không xác định được trạng thái hiện tại."));
                return;
            }

            if (snapshot.LomLocked)
            {
                ShowSentenceNotification(
                    L("SentenceReduction_DeniedTitle", "Từ chối"),
                    L("SentenceReduction_DeniedLom", "Tôi không xử lý khi Lom Bank đang khóa giao dịch."));
                return;
            }

            if (snapshot.ReductionConsumed)
            {
                ShowSentenceNotification(
                    L("SentenceReduction_DeniedTitle", "Từ chối"),
                    L("SentenceReduction_AlreadyUsed",
                    "Tôi đã xử lý rồi. Hãy đợi đến khi Fleeca Bank mở khóa xong mới dùng lại được."));
                return;
            }

            if (!snapshot.FleecaLocked)
            {
                ShowSentenceNotification(
                    L("SentenceReduction_NoLockTitle", "Thông báo"),
                    L("SentenceReduction_NoLockMessage", "Hiện tại Fleeca chưa bị khóa giao dịch."));
                return;
            }

            OpenMenu(snapshot);
        }
        finally
        {
            TryClosePhone();
        }
    }

    private void EnsureMenuCreated()
    {
        try
        {
            if (_menuInitialized)
                return;

            _menu = new NativeMenu(
                L("SentenceReduction_MenuTitle", "Sentence Redution"),
                L("SentenceReduction_MenuSubtitle", "CHI TIẾT DỊCH VỤ"));

            _menu.MouseBehavior = MenuMouseBehavior.Disabled;
            _menu.ResetCursorWhenOpened = false;
            _menu.CloseOnInvalidClick = false;
            _menu.RotateCamera = true;

            _uiPool.Add(_menu);

            _customerItem = new NativeItem("");
            _statusItem = new NativeItem("");
            _feeItem = new NativeItem("");
            _executorItem = new NativeItem("");

            _confirmItem = new NativeItem(
                L("SentenceReduction_Confirm", "Xác nhận trả phí"));

            _cancelItem = new NativeItem(
                L("SentenceReduction_Cancel", "Hủy bỏ giao dịch"));

            _confirmItem.Activated += (s, e) =>
            {
                ConfirmReduction();
            };

            _cancelItem.Activated += (s, e) =>
            {
                CloseMenu(true);
            };

            _menu.Add(_customerItem);
            _menu.Add(_statusItem);
            _menu.Add(_feeItem);
            _menu.Add(_executorItem);
            _menu.Add(_confirmItem);
            _menu.Add(_cancelItem);

            _menuInitialized = true;
        }
        catch (Exception ex)
        {
            Log(L("SentenceReduction_LogEnsureMenuFailed", "EnsureMenuCreated failed: ") + ex);
        }
    }

    private void OpenMenu(Snapshot snapshot)
    {
        try
        {
            if (Game.GameTime < _menuCooldownUntil)
                return;

            EnsureMenuCreated();

            _currentSnapshot = snapshot;
            RefreshMenu(snapshot);

            if (_menu != null)
                _menu.Visible = true;

            UpdateLemonUiMouseState();
            Interval = 0;
        }
        catch (Exception ex)
        {
            Log(L("SentenceReduction_LogOpenMenuFailed", "OpenMenu failed: ") + ex);
        }
    }

    private void CloseMenu(bool setCooldown)
    {
        try
        {
            if (_menu != null)
                _menu.Visible = false;
        }
        catch
        {
        }

        if (setCooldown)
            _menuCooldownUntil = Game.GameTime + MenuCooldownMs;

        UpdateLemonUiMouseState();
        Interval = CheckIntervalMs;
    }

    private void RefreshMenu(Snapshot snapshot)
    {
        try
        {
            if (snapshot == null)
                return;

            EnsureMenuCreated();

            _customerItem.Title = string.Format(
                CultureInfo.InvariantCulture,
                L("SentenceReduction_Customer", "1. Tên khách hàng: {0}"),
                snapshot.CharacterName);

            _statusItem.Title = string.Format(
                CultureInfo.InvariantCulture,
                L("SentenceReduction_Status", "2. Trạng thái khóa Fleeca: {0}"),
                FormatDays(snapshot.FleecaRemaining));

            _feeItem.Title = string.Format(
                CultureInfo.InvariantCulture,
                L("SentenceReduction_Fee", "3. Phí giảm án ngầm: {0}"),
                FormatMoney(snapshot.TotalFee));

            _feeItem.Description = string.Format(
                CultureInfo.InvariantCulture,
                L("SentenceReduction_FeeDesc",
                    "30% tiền bẩn hiện có ({0}) + 8% tiền mặt hiện tại ({1})"),
                FormatMoney(snapshot.DirtyFee),
                FormatMoney(snapshot.CashFee));

            _executorItem.Title = L("SentenceReduction_Executor", "4. Người thực hiện: Bail Bondsman");

            _confirmItem.Title = L("SentenceReduction_Confirm", "Xác nhận trả phí");
            _cancelItem.Title = L("SentenceReduction_Cancel", "Hủy bỏ giao dịch");
        }
        catch (Exception ex)
        {
            Log(L("SentenceReduction_LogRefreshMenuFailed", "RefreshMenu failed: ") + ex);
        }
    }

    private void ConfirmReduction()
    {
        try
        {
            if (_currentSnapshot == null)
            {
                ShowSentenceNotification(
                    L("SentenceReduction_ErrorTitle", "Giao dịch thất bại"),
                    L("SentenceReduction_NoSnapshot", "Không có dữ liệu giao dịch hợp lệ."));
                return;
            }

            Snapshot live;
            if (!TryBuildSnapshot(out live) || live == null)
            {
                ShowSentenceNotification(
                    L("SentenceReduction_ErrorTitle", "Giao dịch thất bại"),
                    L("SentenceReduction_NoSnapshot", "Không thể đọc lại trạng thái hiện tại."));
                return;
            }

            if (!live.Eligible)
            {
                ShowSentenceNotification(
                    L("SentenceReduction_ErrorTitle", "Giao dịch thất bại"),
                    L("SentenceReduction_NotEligible", "Bail Bondsman chỉ xử lý khi Lom Bank không khóa và Fleeca đang bị khóa."));
                return;
            }

            if (IsReductionConsumedForCurrentCharacter(live.OwnerHash))
            {
                ShowSentenceNotification(
                    L("SentenceReduction_ErrorTitle", "Giao dịch thất bại"),
                    L("SentenceReduction_AlreadyUsed",
                    "Giao dịch này đã được thực hiện rồi. Hãy chờ Fleeca Bank mở khóa xong."));
                return;
            }

            HackerStateData data = LoadHackerState(_currentSnapshot.OwnerHash);
            long currentDirty = Math.Max(0, data.DirtyMoney);
            long currentCash = Math.Max(0, Game.Player.Money);

            if (currentDirty < _currentSnapshot.DirtyFee)
            {
                ShowSentenceNotification(
                    L("SentenceReduction_ErrorTitle", "Giao dịch thất bại"),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        L("SentenceReduction_NotEnoughDirty",
                            "Không đủ tiền bẩn. Cần {0} nhưng hiện có {1}."),
                        FormatMoney(_currentSnapshot.DirtyFee),
                        FormatMoney(currentDirty)));
                return;
            }

            if (currentCash < _currentSnapshot.CashFee)
            {
                ShowSentenceNotification(
                    L("SentenceReduction_ErrorTitle", "Giao dịch thất bại"),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        L("SentenceReduction_NotEnoughCash",
                            "Không đủ tiền mặt. Cần {0} nhưng hiện có {1}."),
                        FormatMoney(_currentSnapshot.CashFee),
                        FormatMoney(currentCash)));
                return;
            }

            if (!TryApplyReductionTransaction(_currentSnapshot, out string error))
            {
                ShowSentenceNotification(
                    L("SentenceReduction_ErrorTitle", "Giao dịch thất bại"),
                    error);
                return;
            }

            CloseMenu(false);

            ShowSentenceNotification(
                L("SentenceReduction_SuccessTitle", "Thành công"),
                L("SentenceReduction_SuccessMessage", "Fleeca đã được giảm án ngầm còn 1 ngày."));
        }
        catch (Exception ex)
        {
            Log(L("SentenceReduction_LogConfirmFailed", "ConfirmReduction failed: ") + ex);
        }
    }

    private bool TryApplyReductionTransaction(Snapshot snapshot, out string errorMessage)
    {
        errorMessage = string.Empty;

        try
        {
            if (snapshot == null || snapshot.OwnerHash == 0)
            {
                errorMessage = L("SentenceReduction_NoCharacter", "Không xác định được nhân vật hiện tại.");
                return false;
            }

            HackerStateData data = LoadHackerState(snapshot.OwnerHash);
            DateTime now = GetCurrentInGameDateTime();

            if (data.AtmLockedUntil.HasValue && now < data.AtmLockedUntil.Value)
            {
                errorMessage = L("SentenceReduction_DeniedLom", "Tôi không xử lý khi Lom Bank đang khóa giao dịch.");
                return false;
            }

            if (!data.FleecaLockedUntil.HasValue || now >= data.FleecaLockedUntil.Value)
            {
                errorMessage = L("SentenceReduction_NoLockMessage", "Fleeca hiện không còn bị khóa giao dịch.");
                return false;
            }

            long dirtyNow = Math.Max(0, data.DirtyMoney);
            long cashNow = Math.Max(0, Game.Player.Money);

            if (dirtyNow < snapshot.DirtyFee)
            {
                errorMessage = L("SentenceReduction_DirtyChanged", "Tiền bất hợp pháp đã thay đổi, vui lòng thử lại.");
                return false;
            }

            if (cashNow < snapshot.CashFee)
            {
                errorMessage = L("SentenceReduction_CashChanged", "Tiền mặt đã thay đổi, vui lòng thử lại.");
                return false;
            }

            // Giảm còn 1 ngày, nhưng không bao giờ kéo dài lock nếu lock hiện tại còn ít hơn 1 ngày.
            DateTime oneDayFromNow = now.AddDays(1);
            DateTime newExpiry = data.FleecaLockedUntil.Value < oneDayFromNow
                ? data.FleecaLockedUntil.Value
                : oneDayFromNow;

            data.DirtyMoney = dirtyNow - snapshot.DirtyFee;
            data.FleecaLockedUntil = newExpiry;
            data.FleecaUnlockNoticeShown = false;

            SaveHackerState(snapshot.OwnerHash, data);
            SetReductionConsumedForOwner(snapshot.OwnerHash, data.FleecaLockedUntil);

            Game.Player.Money = (int)(cashNow - snapshot.CashFee);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = L("SentenceReduction_SaveFailed", "Không thể lưu thay đổi giao dịch.");
            Log(L("SentenceReduction_LogApplyFailed", "TryApplyReductionTransaction failed: ") + ex);
            return false;
        }
    }

    private bool TryBuildSnapshot(out Snapshot snapshot)
    {
        snapshot = null;

        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return false;

            string characterName = GetCharacterName(ownerHash);
            HackerStateData data = LoadHackerState(ownerHash);
            SentenceReductionStateData reductionState = LoadSentenceReductionState(ownerHash);
            DateTime now = GetCurrentInGameDateTime();

            bool lomLocked = data.AtmLockedUntil.HasValue && now < data.AtmLockedUntil.Value;
            bool fleecaLocked = data.FleecaLockedUntil.HasValue && now < data.FleecaLockedUntil.Value;
            bool reductionConsumed = IsReductionConsumedNow(reductionState, data.FleecaLockedUntil, now);

            if (!fleecaLocked || !reductionConsumed)
                ClearExpiredReductionStateIfNeeded(ownerHash, data.FleecaLockedUntil, now);

            if (!fleecaLocked)
            {
                snapshot = new Snapshot
                {
                    OwnerHash = ownerHash,
                    CharacterName = characterName,
                    LomLocked = lomLocked,
                    FleecaLocked = false,
                    FleecaRemaining = TimeSpan.Zero,
                    DirtyMoneyBalance = Math.Max(0, data.DirtyMoney),
                    CashBalance = Math.Max(0, Game.Player.Money),
                    DirtyFee = 0,
                    CashFee = 0,
                    TotalFee = 0,
                    FleecaLockedUntil = data.FleecaLockedUntil
                };
                return true;
            }

            long dirtyBalance = Math.Max(0, data.DirtyMoney);
            long cashBalance = Math.Max(0, Game.Player.Money);

            long dirtyFee = RoundMoney((decimal)dirtyBalance * 0.30m);
            long cashFee = RoundMoney((decimal)cashBalance * 0.08m);

            snapshot = new Snapshot
            {
                OwnerHash = ownerHash,
                CharacterName = characterName,
                LomLocked = lomLocked,
                FleecaLocked = fleecaLocked,
                FleecaRemaining = data.FleecaLockedUntil.HasValue
                    ? (data.FleecaLockedUntil.Value - now)
                    : TimeSpan.Zero,
                DirtyMoneyBalance = dirtyBalance,
                CashBalance = cashBalance,
                DirtyFee = dirtyFee,
                CashFee = cashFee,
                TotalFee = dirtyFee + cashFee,
                FleecaLockedUntil = data.FleecaLockedUntil,
                ReductionConsumed = reductionConsumed
            };

            return true;
        }
        catch (Exception ex)
        {
            Log(L("SentenceReduction_LogSnapshotFailed", "TryBuildSnapshot failed: ") + ex);
            return false;
        }
    }

    private static string GetSentenceReductionStatePath(int ownerHash)
    {
        Directory.CreateDirectory(SentenceReductionStateRoot);
        return Path.Combine(SentenceReductionStateRoot, $"sentence_reduction_{ownerHash}.dat");
    }

    private SentenceReductionStateData LoadSentenceReductionState(int ownerHash)
    {
        var data = new SentenceReductionStateData();

        try
        {
            string file = GetSentenceReductionStatePath(ownerHash);
            if (!File.Exists(file))
                return data;

            foreach (string rawLine in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;

                int idx = rawLine.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = rawLine.Substring(0, idx).Trim();
                string val = rawLine.Substring(idx + 1).Trim();

                if (key.Equals("consumedUntil", StringComparison.OrdinalIgnoreCase))
                {
                    data.ConsumedUntil = ParseNullableDateTime(val);
                    break;
                }
            }
        }
        catch
        {
        }

        return data;
    }

    private void SaveSentenceReductionState(int ownerHash, SentenceReductionStateData data)
    {
        try
        {
            if (ownerHash == 0 || data == null)
                return;

            Directory.CreateDirectory(SentenceReductionStateRoot);

            string file = GetSentenceReductionStatePath(ownerHash);
            var lines = new List<string>
            {
                "consumedUntil=" + (data.ConsumedUntil.HasValue
                    ? data.ConsumedUntil.Value.ToString("o", CultureInfo.InvariantCulture)
                    : "")
            };

            File.WriteAllLines(file, lines.ToArray());
        }
        catch
        {
        }
    }

    private bool IsReductionConsumedNow(
        SentenceReductionStateData reductionState,
        DateTime? fleecaLockedUntil,
        DateTime now)
    {
        try
        {
            if (reductionState == null || !reductionState.ConsumedUntil.HasValue)
                return false;

            if (!fleecaLockedUntil.HasValue)
                return false;

            return now < reductionState.ConsumedUntil.Value && now < fleecaLockedUntil.Value;
        }
        catch
        {
            return false;
        }
    }

    private bool IsReductionConsumedForCurrentCharacter(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
                return false;

            var hacker = LoadHackerState(ownerHash);
            var reduction = LoadSentenceReductionState(ownerHash);
            DateTime now = GetCurrentInGameDateTime();

            if (!hacker.FleecaLockedUntil.HasValue)
                return false;

            return IsReductionConsumedNow(reduction, hacker.FleecaLockedUntil, now);
        }
        catch
        {
            return false;
        }
    }

    private void SetReductionConsumedForOwner(int ownerHash, DateTime? fleecaLockedUntil)
    {
        try
        {
            if (ownerHash == 0)
                return;

            if (!fleecaLockedUntil.HasValue)
                return;

            SaveSentenceReductionState(ownerHash, new SentenceReductionStateData
            {
                ConsumedUntil = fleecaLockedUntil.Value
            });
        }
        catch
        {
        }
    }

    private void ClearExpiredReductionStateIfNeeded(int ownerHash, DateTime? fleecaLockedUntil, DateTime now)
    {
        try
        {
            if (ownerHash == 0)
                return;

            string file = GetSentenceReductionStatePath(ownerHash);
            if (!File.Exists(file))
                return;

            var state = LoadSentenceReductionState(ownerHash);

            if (!state.ConsumedUntil.HasValue || now >= state.ConsumedUntil.Value || !fleecaLockedUntil.HasValue || now >= fleecaLockedUntil.Value)
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
        }
    }

    private HackerStateData LoadHackerState(int ownerHash)
    {
        var data = new HackerStateData();

        try
        {
            string file = GetHackerStatePath(ownerHash);
            if (!File.Exists(file))
                return data;

            foreach (string rawLine in File.ReadAllLines(file))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(rawLine))
                        continue;

                    int idx = rawLine.IndexOf('=');
                    if (idx <= 0)
                    {
                        data.PassthroughLines.Add(rawLine);
                        continue;
                    }

                    string key = rawLine.Substring(0, idx).Trim();
                    string val = rawLine.Substring(idx + 1).Trim();

                    if (key.Equals("dirtyMoney", StringComparison.OrdinalIgnoreCase))
                    {
                        if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long dm))
                            data.DirtyMoney = Math.Max(0, dm);
                        continue;
                    }

                    if (key.Equals("atmLockedUntil", StringComparison.OrdinalIgnoreCase))
                    {
                        data.AtmLockedUntil = ParseNullableDateTime(val);
                        continue;
                    }

                    if (key.Equals("fleecaLockedUntil", StringComparison.OrdinalIgnoreCase))
                    {
                        data.FleecaLockedUntil = ParseNullableDateTime(val);
                        continue;
                    }

                    if (key.Equals("atmUnlockNoticeShown", StringComparison.OrdinalIgnoreCase))
                    {
                        data.AtmUnlockNoticeShown = ParseBool(val);
                        continue;
                    }

                    if (key.Equals("fleecaUnlockNoticeShown", StringComparison.OrdinalIgnoreCase))
                    {
                        data.FleecaUnlockNoticeShown = ParseBool(val);
                        continue;
                    }

                    data.PassthroughLines.Add(rawLine);
                }
                catch
                {
                    data.PassthroughLines.Add(rawLine);
                }
            }
        }
        catch (Exception ex)
        {
            Log(L("SentenceReduction_LogLoadStateFailed", "LoadHackerState failed: ") + ex);
        }

        return data;
    }

    private void SaveHackerState(int ownerHash, HackerStateData data)
    {
        try
        {
            if (ownerHash == 0 || data == null)
                return;

            Directory.CreateDirectory(HackerStateRoot);

            string file = GetHackerStatePath(ownerHash);

            var lines = new List<string>
            {
                "dirtyMoney=" + Math.Max(0, data.DirtyMoney).ToString(CultureInfo.InvariantCulture),
                "atmLockedUntil=" + (data.AtmLockedUntil.HasValue ? data.AtmLockedUntil.Value.ToString("o", CultureInfo.InvariantCulture) : ""),
                "fleecaLockedUntil=" + (data.FleecaLockedUntil.HasValue ? data.FleecaLockedUntil.Value.ToString("o", CultureInfo.InvariantCulture) : ""),
                "atmUnlockNoticeShown=" + (data.AtmUnlockNoticeShown ? "1" : "0"),
                "fleecaUnlockNoticeShown=" + (data.FleecaUnlockNoticeShown ? "1" : "0")
            };

            foreach (string raw in data.PassthroughLines)
            {
                if (!string.IsNullOrWhiteSpace(raw))
                    lines.Add(raw);
            }

            File.WriteAllLines(file, lines.ToArray());
        }
        catch (Exception ex)
        {
            Log(L("SentenceReduction_LogSaveStateFailed", "SaveHackerState failed: ") + ex);
            throw;
        }
    }

    private static string GetHackerStatePath(int ownerHash)
    {
        Directory.CreateDirectory(HackerStateRoot);
        return Path.Combine(HackerStateRoot, $"hacker_state_{ownerHash}.dat");
    }

    private static DateTime? ParseNullableDateTime(string value)
    {
        try
        {
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
        }
        catch
        {
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

    private static long RoundMoney(decimal value)
    {
        if (value <= 0m)
            return 0L;

        return (long)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p != null && p.Exists())
            {
                int hash = p.Model.Hash;
                if (hash == 225514697 || hash == -1692214353 || hash == -1686040670)
                    return hash;
            }
        }
        catch
        {
        }

        return 0;
    }

    private static string GetCharacterName(int hash)
    {
        if (hash == 225514697)
            return L("SentenceReduction_CharacterMichael", "Michael De Santa");

        if (hash == -1692214353)
            return L("SentenceReduction_CharacterFranklin", "Franklin Clinton");

        if (hash == -1686040670)
            return L("SentenceReduction_CharacterTrevor", "Trevor Philips");

        return L("SentenceReduction_CurrentCharacter", "Nhân vật hiện tại");
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

    private static string FormatMoney(long value)
    {
        if (value < 0)
            value = 0;

        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private static string FormatDays(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return L("SentenceReduction_DaysZero", "0 ngày");

        int days = Math.Max(1, (int)Math.Ceiling(remaining.TotalDays));
        return string.Format(CultureInfo.InvariantCulture, L("SentenceReduction_Days", "{0} ngày"), days);
    }

    private void ShowSentenceNotification(string title, string message, int timeout = NotificationTimeoutMs)
    {
        try
        {
            Notification.Show(
                NotificationIcon.MpFmContact,
                NotificationBrand,
                title,
                message);
        }
        catch
        {
            try
            {
                GTA.UI.Screen.ShowSubtitle($"{title}: {message}", timeout);
            }
            catch
            {
            }
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

    private void UpdateLemonUiMouseState()
    {
        try
        {
            bool anyMenuVisible = (_menu != null && _menu.Visible);

            if (!anyMenuVisible)
                return;

            SetBoolPropertyIfExists(_menu, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");

            SetBoolPropertyIfExists(_uiPool, false,
                "MouseControlsEnabled", "MouseControls", "EnableMouseControls", "UseMouse",
                "MouseEdgeEnabled", "MouseEdgesEnabled", "AllowMouseControls");
        }
        catch
        {
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
                var prop = t.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
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

    private void Log(string message)
    {
        try
        {
            // File.AppendAllText("SentenceReduction.log", DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }
}
