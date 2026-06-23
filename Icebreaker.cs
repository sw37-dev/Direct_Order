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

public class Icebreaker : Script
{
    private static string ContactDisplayName => L("Icebreaker_ContactDisplayName", "Icebreaker");
    private static string NotificationBrand => L("Icebreaker_NotificationBrand", "Icebreaker");

    private const int NotificationTimeoutMs = 3000;
    private const int MenuCooldownMs = 1000;
    private const int CheckIntervalMs = 1000;

    private static readonly string HackerStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Hacker");

    private static readonly string LomBankStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Lom Bank");

    private readonly ObjectPool _uiPool = new ObjectPool();
    private NativeMenu _menu;
    private bool _menuInitialized = false;

    private NativeItem _customerItem;
    private NativeItem _bankStatusItem;
    private NativeItem _severityItem;
    private NativeCheckboxItem _lomBankItem;
    private NativeCheckboxItem _fleecaBankItem;
    private NativeCheckboxItem _dirtyMoneyItem;
    private NativeCheckboxItem _cashItem;
    private NativeItem _confirmItem;
    private NativeItem _cancelItem;

    private CustomiFruit _phoneInstance = null;
    private iFruitContact _icebreakerContact = null;
    private bool _icebreakerContactAnsweredBound = false;
    private bool _icebreakerContactAdded = false;

    private int _menuCooldownUntil = 0;
    private bool _uiSync = false;

    private sealed class HackerStateData
    {
        public long DirtyMoney = 0;
        public DateTime? AtmLockedUntil = null;
        public DateTime? FleecaLockedUntil = null;
        public bool AtmUnlockNoticeShown = false;
        public bool FleecaUnlockNoticeShown = false;
        public List<string> PassthroughLines = new List<string>();
    }

    private sealed class Snapshot
    {
        public int OwnerHash;
        public string CharacterName;
        public long TotalLimit;
        public long DirtyMoneyBalance;
        public long DirtyMoneyRequired;
        public long CashRequired;
        public bool LomLocked;
        public bool FleecaLocked;
        public DateTime? LomLockedUntil;
        public DateTime? FleecaLockedUntil;
        public string HackerStateFile;
        public string LomBankStateFile;
        public bool LesterRequestArmed;

        public bool Eligible => LomLocked && FleecaLocked;
    }

    public Icebreaker()
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

            EnsureIcebreakerContactRegistered(hasSnapshot ? snapshot.Eligible : false);

            if (_menu != null && _menu.Visible)
            {
                if (!hasSnapshot || !snapshot.Eligible)
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
            Log(L("Icebreaker_LogOnTickFailed", "OnTick failed: ") + ex);
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
            Log(L("Icebreaker_LogOnKeyDownFailed", "OnKeyDown failed: ") + ex);
        }
    }

    private void OnAborted(object sender, EventArgs e)
    {
        try
        {
            CloseMenu(false);
            ResetContactState();
        }
        catch { }
    }

    private void ResetContactState()
    {
        _icebreakerContact = null;
        _icebreakerContactAnsweredBound = false;
        _icebreakerContactAdded = false;
        _phoneInstance = null;
    }

    private void EnsureIcebreakerContactRegistered(bool eligible)
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_phoneInstance, phone))
            {
                _phoneInstance = phone;
                _icebreakerContact = null;
                _icebreakerContactAnsweredBound = false;
                _icebreakerContactAdded = false;
            }

            if (_icebreakerContact == null)
            {
                _icebreakerContact = phone.Contacts.FirstOrDefault(c =>
                    string.Equals(c.Name, ContactDisplayName, StringComparison.OrdinalIgnoreCase));
            }

            if (_icebreakerContact == null)
            {
                _icebreakerContact = new iFruitContact(ContactDisplayName)
                {
                    Active = eligible,
                    DialTimeout = 2000,
                    Bold = false,
                    Icon = new ContactIcon("WEB_DIGIFARM")
                };

                _icebreakerContact.Answered += OnIcebreakerAnswered;
                _icebreakerContactAnsweredBound = true;
                phone.Contacts.Add(_icebreakerContact);
                _icebreakerContactAdded = true;
            }
            else
            {
                _icebreakerContact.Active = eligible;
                _icebreakerContact.DialTimeout = 2000;
                _icebreakerContact.Bold = false;
                _icebreakerContact.Icon = new ContactIcon("WEB_DIGIFARM");

                if (!_icebreakerContactAnsweredBound)
                {
                    _icebreakerContact.Answered += OnIcebreakerAnswered;
                    _icebreakerContactAnsweredBound = true;
                }

                _icebreakerContactAdded = true;
            }

            if (!eligible && _menu != null && _menu.Visible)
            {
                CloseMenu(false);
            }
        }
        catch (Exception ex)
        {
            Log(L("Icebreaker_LogEnsureContactFailed", "EnsureIcebreakerContactRegistered failed: ") + ex);
        }
    }

    private void OnIcebreakerAnswered(iFruitContact sender)
    {
        try
        {
            Snapshot snapshot;
            if (!TryBuildSnapshot(out snapshot) || !snapshot.Eligible)
            {
                ShowIcebreakerNotification(
                    L("PowCleanse_DeniedTitle", "Từ chối"),
                    L("PowCleanse_DeniedMessage", "Tôi chỉ xử lý khi ngân hàng Lom và ngân hàng Fleeca đều từ chối bạn thôi."));
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
                L("Icebreaker_MenuTitle", "Unfreeze Ops"),
                L("Icebreaker_MenuSubtitle", "CHI TIẾT DỊCH VỤ GIẢM KHÓA"));

            _menu.MouseBehavior = MenuMouseBehavior.Disabled;
            _menu.ResetCursorWhenOpened = false;
            _menu.CloseOnInvalidClick = false;
            _menu.RotateCamera = true;

            _uiPool.Add(_menu);

            _customerItem = new NativeItem("");
            _bankStatusItem = new NativeItem("");
            _severityItem = new NativeItem("");

            _lomBankItem = new NativeCheckboxItem(L("Icebreaker_LomBank", "Ngân hàng Lom"), true);
            _fleecaBankItem = new NativeCheckboxItem(L("Icebreaker_FleecaBank", "Ngân hàng Fleeca"), true);

            _dirtyMoneyItem = new NativeCheckboxItem(L("Icebreaker_DirtyMoneyRequiredFormat", "Trả Lester"), false);
            _cashItem = new NativeCheckboxItem(L("Icebreaker_CashRequiredFormat", "Trả Icebreaker"), true);

            _confirmItem = new NativeItem(
                L("PowCleanse_Confirm", "Xác nhận giảm ngầm thời gian"));

            _cancelItem = new NativeItem(
                L("PowCleanse_Cancel", "Hủy dịch vụ này"));

            _lomBankItem.CheckboxChanged += (s, e) =>
            {
                ForceMandatoryCheckbox(_lomBankItem);
            };

            _fleecaBankItem.CheckboxChanged += (s, e) =>
            {
                ForceMandatoryCheckbox(_fleecaBankItem);
            };

            _dirtyMoneyItem.CheckboxChanged += (s, e) =>
            {
                ForceReadOnlyCheckbox(_dirtyMoneyItem, LesterAuctionManipulationState.HasPendingBankLockReductionRequestForCurrentCharacter());
            };

            _cashItem.CheckboxChanged += (s, e) =>
            {
                ForceReadOnlyCheckbox(_cashItem, true);
            };

            _confirmItem.Activated += (s, e) =>
            {
                ConfirmReduceLockTime();
            };

            _cancelItem.Activated += (s, e) =>
            {
                CloseMenu(true);
            };

            _menu.Add(_customerItem);
            _menu.Add(_bankStatusItem);
            _menu.Add(_severityItem);
            _menu.Add(_lomBankItem);
            _menu.Add(_fleecaBankItem);
            _menu.Add(_dirtyMoneyItem);
            _menu.Add(_cashItem);
            _menu.Add(_confirmItem);
            _menu.Add(_cancelItem);

            _menuInitialized = true;
        }
        catch (Exception ex)
        {
            Log(L("Icebreaker_LogEnsureMenuFailed", "EnsureMenuCreated failed: ") + ex);
        }
    }

    private void OpenMenu(Snapshot snapshot)
    {
        try
        {
            if (Game.GameTime < _menuCooldownUntil)
                return;

            EnsureMenuCreated();
            RefreshMenu(snapshot);

            if (_menu != null)
                _menu.Visible = true;

            UpdateLemonUiMouseState();
            Interval = 0;
        }
        catch (Exception ex)
        {
            Log(L("Icebreaker_LogOpenMenuFailed", "OpenMenu failed: ") + ex);
        }
    }

    private void CloseMenu(bool setCooldown)
    {
        try
        {
            if (_menu != null)
                _menu.Visible = false;
        }
        catch { }

        if (setCooldown)
            _menuCooldownUntil = Game.GameTime + MenuCooldownMs;

        UpdateLemonUiMouseState();
        Interval = CheckIntervalMs;
    }

    private void RefreshMenu(Snapshot snapshot)
    {
        try
        {
            EnsureMenuCreated();

            if (snapshot == null)
                return;

            _customerItem.Title = string.Format(
                CultureInfo.InvariantCulture,
                L("Icebreaker_CustomerNameFormat", "Tên khách hàng: {0}"),
                snapshot.CharacterName);

            _bankStatusItem.Title = L("Icebreaker_BankStatusLocked", "Tình trạng ngân hàng: Đã bị khóa");
            _severityItem.Title = L("Icebreaker_RiskVeryDangerous", "Mức độ rủi ro: Rất nguy hiểm");

            SetMandatoryCheckboxAppearance(_lomBankItem, true, true);
            SetMandatoryCheckboxAppearance(_fleecaBankItem, true, true);

            _dirtyMoneyItem.Title = string.Format(
                CultureInfo.InvariantCulture,
                L("Icebreaker_DirtyMoneyRequiredFormat", "Trả Lester: {0}"),
                FormatMoney(snapshot.DirtyMoneyRequired));

            _cashItem.Title = string.Format(
                CultureInfo.InvariantCulture,
                L("Icebreaker_CashRequiredFormat", "Trả Icebreaker: {0}"),
                FormatMoney(snapshot.CashRequired));

            SetCheckboxCheckedIfExists(_dirtyMoneyItem, snapshot.LesterRequestArmed);
            SetCheckboxCheckedIfExists(_cashItem, true);

            _confirmItem.Title = L("PowCleanse_Confirm", "Xác nhận giảm ngầm thời gian");
            _cancelItem.Title = L("PowCleanse_Cancel", "Hủy dịch vụ này");
        }
        catch (Exception ex)
        {
            Log(L("Icebreaker_LogRefreshMenuFailed", "RefreshMenu failed: ") + ex);
        }
    }

    private void ConfirmReduceLockTime()
    {
        try
        {
            Snapshot snapshot;
            if (!TryBuildSnapshot(out snapshot))
            {
                ShowIcebreakerNotification(
                    L("PowCleanse_ErrorTitle", "Giao dịch thất bại"),
                    L("PowCleanse_NotEligible", "Chưa đủ điều kiện để dùng Icebreaker."));
                return;
            }

            if (!snapshot.Eligible)
            {
                ShowIcebreakerNotification(
                    L("PowCleanse_ErrorTitle", "Giao dịch thất bại"),
                    L("PowCleanse_NotEligible", "Icebreaker chỉ hoạt động khi Lom Bank và Fleeca Bank đều đang bị khóa."));
                return;
            }

            if (!_dirtyMoneyItem.Checked || !_cashItem.Checked || !snapshot.LesterRequestArmed)
            {
                ShowIcebreakerNotification(
                    L("PowCleanse_ErrorTitle", "Giao dịch thất bại"),
                    L("PowCleanse_NotReady", "Thiếu điều kiện xác nhận. Hãy chọn đúng dịch vụ Lester trước khi giảm án."));
                return;
            }

            if (snapshot.DirtyMoneyBalance < snapshot.DirtyMoneyRequired)
            {
                ShowIcebreakerNotification(
                    L("PowCleanse_ErrorTitle", "Giao dịch thất bại"),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        L("PowCleanse_NotEnoughDirty",
                            "Không đủ tiền bất hợp pháp. Cần {0} nhưng hiện có {1}."),
                        FormatMoney(snapshot.DirtyMoneyRequired),
                        FormatMoney(snapshot.DirtyMoneyBalance)));
                return;
            }

            if (Game.Player.Money < snapshot.CashRequired)
            {
                ShowIcebreakerNotification(
                    L("PowCleanse_ErrorTitle", "Giao dịch thất bại"),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        L("PowCleanse_NotEnoughCash",
                            "Không đủ tiền mặt. Cần {0} nhưng hiện có {1}."),
                        FormatMoney(snapshot.CashRequired),
                        FormatMoney(Math.Max(0, Game.Player.Money))));
                return;
            }

            if (!TryApplyCleanse(snapshot, out string error))
            {
                ShowIcebreakerNotification(
                    L("PowCleanse_ErrorTitle", "Giao dịch thất bại"),
                    error);
                return;
            }

            try
            {
                LesterAuctionManipulationState.ConsumeBankLockReductionRequestForCurrentCharacter();
            }
            catch (Exception ex)
            {
                Log(L("Icebreaker_LogConsumeLesterRequestFailed", "ConsumeBankLockReductionRequestForCurrentCharacter failed: ") + ex);
            }

            CloseMenu(false);

            ShowIcebreakerNotification(
                L("PowCleanse_SuccessTitle", "Thành công"),
                L("PowCleanse_SuccessMessage",
                    "Đã giảm ngầm thời gian khóa xuống còn 1 ngày cho cả Lom Bank và Fleeca Bank."));
        }
        catch (Exception ex)
        {
            Log(L("Icebreaker_LogConfirmFailed", "ConfirmReduceLockTime failed: ") + ex);
        }
    }

    private bool TryApplyCleanse(Snapshot snapshot, out string errorMessage)
    {
        errorMessage = string.Empty;

        try
        {
            int ownerHash = snapshot.OwnerHash;
            if (ownerHash == 0)
            {
                errorMessage = L("PowCleanse_NoCharacter", "Không xác định được nhân vật hiện tại.");
                return false;
            }

            HackerStateData data = LoadHackerState(ownerHash);

            if (data.DirtyMoney < snapshot.DirtyMoneyRequired)
            {
                errorMessage = L("PowCleanse_DirtyChanged", "Tiền bất hợp pháp đã thay đổi, vui lòng thử lại.");
                return false;
            }

            if (Game.Player.Money < snapshot.CashRequired)
            {
                errorMessage = L("PowCleanse_CashChanged", "Tiền mặt hiện tại đã thay đổi, vui lòng thử lại.");
                return false;
            }

            DateTime now = GetCurrentInGameDateTime();
            DateTime newExpiry = now.AddDays(1);

            data.DirtyMoney = Math.Max(0, data.DirtyMoney - snapshot.DirtyMoneyRequired);
            data.AtmLockedUntil = newExpiry;
            data.FleecaLockedUntil = newExpiry;
            data.AtmUnlockNoticeShown = false;
            data.FleecaUnlockNoticeShown = false;

            SaveHackerState(ownerHash, data);

            Game.Player.Money -= (int)Math.Min(int.MaxValue, snapshot.CashRequired);

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = L("PowCleanse_SaveFailed", "Không thể lưu thay đổi dịch vụ. Vui lòng thử lại.");
            Log(L("Icebreaker_LogTryApplyCleanseFailed", "TryApplyCleanse failed: ") + ex);
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
            string hackerFile = GetHackerStatePath(ownerHash);
            string lombankFile = GetLombankStatePath(ownerHash);

            HackerStateData hacker = LoadHackerState(ownerHash);
            DateTime now = GetCurrentInGameDateTime();

            bool lomLocked = hacker.AtmLockedUntil.HasValue && now < hacker.AtmLockedUntil.Value;
            bool fleecaLocked = hacker.FleecaLockedUntil.HasValue && now < hacker.FleecaLockedUntil.Value;
            bool lesterArmed = LesterAuctionManipulationState.HasPendingBankLockReductionRequestForCurrentCharacter();

            long totalLimit = GetTotalLimitForCurrentCharacter(ownerHash);
            long dirtyRequired = RoundMoney((decimal)totalLimit * 1.3m);
            long cashRequired = RoundMoney((decimal)totalLimit * 4.5m);

            snapshot = new Snapshot
            {
                OwnerHash = ownerHash,
                CharacterName = characterName,
                TotalLimit = totalLimit,
                DirtyMoneyBalance = Math.Max(0, hacker.DirtyMoney),
                DirtyMoneyRequired = dirtyRequired,
                CashRequired = cashRequired,
                LomLocked = lomLocked,
                FleecaLocked = fleecaLocked,
                LomLockedUntil = hacker.AtmLockedUntil,
                FleecaLockedUntil = hacker.FleecaLockedUntil,
                HackerStateFile = hackerFile,
                LomBankStateFile = lombankFile,
                LesterRequestArmed = lesterArmed
            };

            return true;
        }
        catch (Exception ex)
        {
            Log(L("Icebreaker_LogTryBuildSnapshotFailed", "TryBuildSnapshot failed: ") + ex);
            return false;
        }
    }

    private long GetTotalLimitForCurrentCharacter(int ownerHash)
    {
        const long fallbackLimit = 1_000_000L;

        try
        {
            string file = GetLombankStatePath(ownerHash);
            if (!File.Exists(file))
                return fallbackLimit;

            foreach (string raw in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                int idx = raw.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = raw.Substring(0, idx).Trim();
                string val = raw.Substring(idx + 1).Trim();

                if (!key.Equals("limit", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long limit))
                    return Math.Max(fallbackLimit, Math.Min(15_000_000L, limit));

                break;
            }
        }
        catch (Exception ex)
        {
            Log(L("Icebreaker_LogGetLimitFailed", "GetTotalLimitForCurrentCharacter failed: ") + ex);
        }

        return fallbackLimit;
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
            Log(L("Icebreaker_LogLoadHackerStateFailed", "LoadHackerState failed: ") + ex);
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
            Log(L("Icebreaker_LogSaveHackerStateFailed", "SaveHackerState failed: ") + ex);
            throw;
        }
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
        catch { }

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
        catch { }

        return 0;
    }

    private static string GetCharacterName(int hash)
    {
        if (hash == 225514697)
            return L("Icebreaker_CharacterMichael", "Michael De Santa");

        if (hash == -1692214353)
            return L("Icebreaker_CharacterFranklin", "Franklin Clinton");

        if (hash == -1686040670)
            return L("Icebreaker_CharacterTrevor", "Trevor Philips");

        return L("Icebreaker_CurrentCharacter", "Nhân vật hiện tại");
    }

    private static string FormatMoney(long value)
    {
        if (value < 0)
            value = 0;

        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
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

    private static string GetHackerStatePath(int ownerHash)
    {
        Directory.CreateDirectory(HackerStateRoot);
        return Path.Combine(HackerStateRoot, $"hacker_state_{ownerHash}.dat");
    }

    private static string GetLombankStatePath(int ownerHash)
    {
        Directory.CreateDirectory(LomBankStateRoot);
        return Path.Combine(LomBankStateRoot, $"lombank_state_{ownerHash}.dat");
    }

    private void SetMandatoryCheckboxAppearance(NativeCheckboxItem item, bool checkedState, bool enabledState)
    {
        try
        {
            if (item == null)
                return;

            SetCheckboxCheckedIfExists(item, checkedState);
            SetBoolPropertyIfExists(item, enabledState,
                "Enabled", "Active", "Selectable", "CanSelect", "CanBeSelected", "IsEnabled");
        }
        catch { }
    }

    private void ForceMandatoryCheckbox(NativeCheckboxItem item)
    {
        try
        {
            if (item == null)
                return;

            SetCheckboxCheckedIfExists(item, true);
        }
        catch { }
    }

    private void ForceReadOnlyCheckbox(NativeCheckboxItem item, bool checkedState)
    {
        try
        {
            if (item == null)
                return;

            SetCheckboxCheckedIfExists(item, checkedState);
        }
        catch { }
    }

    private static void SetCheckboxCheckedIfExists(object item, bool value)
    {
        try
        {
            if (item == null)
                return;

            Type t = item.GetType();

            string[] propertyNames = { "Checked", "IsChecked", "CheckedState" };
            foreach (string name in propertyNames)
            {
                PropertyInfo prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
                {
                    prop.SetValue(item, value, null);
                    return;
                }
            }

            string[] fieldNames = { "checked", "_checked", "isChecked" };
            foreach (string name in fieldNames)
            {
                FieldInfo field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(bool))
                {
                    field.SetValue(item, value);
                    return;
                }
            }
        }
        catch { }
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
        catch (Exception ex)
        {
            Log(L("Icebreaker_LogUpdateMouseStateFailed", "UpdateLemonUiMouseState failed: ") + ex);
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

    private void ShowIcebreakerNotification(string title, string message, int timeout = NotificationTimeoutMs)
    {
        try
        {
            Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, message);

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_MESSAGETEXT,
                "WEB_DIGIFARM",
                "WEB_DIGIFARM",
                false,
                0,
                NotificationBrand,
                title);

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_TICKER, false, true);
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
        catch { }
    }

    private void Log(string message)
    {
        try
        {
            // File.AppendAllText("PowCleanse.log", DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch { }
    }
}