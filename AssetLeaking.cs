using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

public partial class AssetLeaking : Script
{
    private static string T(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    private static string TF(string key, string fallback, params object[] args)
    {
        return string.Format(CultureInfo.InvariantCulture, T(key, fallback), args);
    }

    private const int FRANKLIN_HASH = -1692214353;
    private const int MICHAEL_HASH = 225514697;
    private const int TREVOR_HASH = -1686040670;

    private const int MOLE_CALL_DURATION_MS = 2000;
    private const int WANTED_PENALTY_DELAY_MS = 20000;
    private const int WANTED_PENALTY_CHANCE_PERCENT = 60;
    private const int WANTED_PENALTY_ADD_STARS = 3;
    private const int WANTED_PENALTY_MAX_STARS = 5;
    private const int MAX_CONFISCATED_PER_CHAR = 10;
    private const decimal REDEEM_PERCENT = 0.62m;
    private const long ZERO_PRICE_REDEEM_COST = 100_000L;
    private const int MOLE_LOCK_CHANCE_PERCENT = 25;
    private const int MOLE_LOCK_DURATION_HOURS = 6;

    private readonly ObjectPool _uiPool = new ObjectPool();

    private NativeMenu _moleMenu;
    private bool _moleMenuReady = false;

    private CustomiFruit _molePhoneInstance = null;
    private bool _moleContactAdded = false;

    private bool _moleRestorePending = false;
    private int _moleRestoreDueTime = 0;

    // Mole contact lock state (in-game time)
    private bool _moleContactLocked = false;
    private DateTime? _moleContactUnlockAtGameTime = null;

    private readonly List<int> _wantedPenaltyDueTimes = new List<int>();

    private readonly List<ConfiscatedVehicleRecord> _moleEntries = new List<ConfiscatedVehicleRecord>();
    private readonly List<NativeCheckboxItem> _moleCheckboxItems = new List<NativeCheckboxItem>();
    private NativeItem _moleRedeemTotalItem = null;
    private bool _moleUiSync = false;

    private static readonly string ConfiscatedRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Asset Leaking");

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PersistentManager");

    private static readonly string PersistentVehiclesFile = Path.Combine(DataFolder, "persistent_vehicles.txt");

    private static readonly Random _rng = new Random();

    public AssetLeaking()
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

            UpdateMoleContactLockState();
            EnsureMoleContactRegistered();
            UpdateMolePendingCall();
            UpdateWantedPenaltyPending();
            AssetLeakingWantedState.ClearIfWantedLevelZero();

            if (_moleMenu != null && _moleMenu.Visible)
            {
                UpdateMoleRedeemTotalItem();
                _uiPool.Process();
                Interval = 0;
            }
            else
            {
                Interval = 1000;
            }
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
            if (_moleMenu != null && _moleMenu.Visible)
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Back)
                    CloseMoleMenu();
            }
        }
        catch
        {
        }
    }

    private void EnsureMoleContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_molePhoneInstance, phone))
            {
                _molePhoneInstance = phone;
                _moleContactAdded = false;
            }

            if (_moleContactAdded)
                return;

            string contactName = T("AssetLeaking.ContactName", "Mole");

            var existing = phone.Contacts.FirstOrDefault(c =>
                c != null &&
                string.Equals(c.Name, contactName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                _moleContactAdded = true;
                return;
            }

            var contact = new iFruitContact(contactName)
            {
                Active = !_moleContactLocked,
                DialTimeout = MOLE_CALL_DURATION_MS,
                Bold = false,
                Icon = new ContactIcon("CHAR_LESTER_DEATHWISH")
            };

            contact.Answered += OnMoleContactAnswered;
            phone.Contacts.Add(contact);

            _moleContactAdded = true;
        }
        catch
        {
        }
    }

    private void OnMoleContactAnswered(iFruitContact sender)
    {
        try
        {
            _moleRestorePending = true;
            _moleRestoreDueTime = Game.GameTime + 1;
        }
        catch
        {
        }
    }

    private void UpdateMolePendingCall()
    {
        try
        {
            if (!_moleRestorePending)
                return;

            if (Game.GameTime < _moleRestoreDueTime)
                return;

            _moleRestorePending = false;

            try { Game.Player.Character?.Task?.PutAwayMobilePhone(); } catch { }
            try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }

            OpenMoleMenu();
        }
        catch
        {
        }
    }

    private void EnsureMoleMenuCreated()
    {
        try
        {
            if (_moleMenuReady)
                return;

            _moleMenu = new NativeMenu(
                T("AssetLeaking.MenuTitle", "Confiscated Vehicles"),
                T("AssetLeaking.MenuSubtitle", "CÁC PHƯƠNG TIỆN TỊCH THU"));

            _uiPool.Add(_moleMenu);
            ConfigureKeyboardOnlyMenu(_moleMenu);
            _moleMenu.Visible = false;

            _moleMenuReady = true;
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

    private void OpenMoleMenu()
    {
        try
        {
            EnsureMoleMenuCreated();
            BuildMoleMenu();

            if (_moleMenu != null)
            {
                _moleMenu.Visible = true;
                Interval = 0;
            }
        }
        catch
        {
        }
    }

    private void CloseMoleMenu()
    {
        try
        {
            if (_moleMenu != null)
                _moleMenu.Visible = false;
        }
        catch
        {
        }

        Interval = 1000;
    }

    private void BuildMoleMenu()
    {
        try
        {
            if (_moleMenu == null)
                return;

            _moleMenu.Clear();
            _moleEntries.Clear();
            _moleCheckboxItems.Clear();
            _moleRedeemTotalItem = null;
            _moleUiSync = false;

            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
            {
                _moleMenu.Add(new NativeItem(T("AssetLeaking.UnknownCharacter", "Không xác định được nhân vật hiện tại.")));
            }
            else
            {
                var records = LoadConfiscatedRecords(ownerHash);

                if (records.Count == 0)
                {
                    _moleMenu.Add(new NativeItem(T("AssetLeaking.NoConfiscatedVehicles", "Chưa có phương tiện nào bị tịch thu.")));
                }
                else
                {
                    int index = 1;
                    foreach (var record in records)
                    {
                        var captured = record;
                        string name = GetVehicleDisplayNamePublic(captured.ModelHash);
                        if (string.IsNullOrWhiteSpace(name))
                            name = $"0x{captured.ModelHash:X}";

                        long originalPrice = GetOriginalVehiclePrice(captured.PurchasePrice);
                        long redeemCost = GetDisplayRedeemCost(originalPrice);

                        var item = new NativeCheckboxItem(
                            TF("AssetLeaking.VehicleItemTitle", "{0}. {1}: {2}", index, name, FormatMoney(redeemCost)),
                            false);

                        item.Description =
                            TF("AssetLeaking.PlateLabel", "Biển số: {0}", captured.Plate) + "\n" +
                            TF("AssetLeaking.OriginalPriceLabel", "Giá gốc: {0}", FormatMoney(originalPrice)) + "\n" +
                            TF("AssetLeaking.RedeemFeeLabel", "Phí chuộc: {0}", FormatMoney(redeemCost));

                        _moleEntries.Add(captured);
                        _moleCheckboxItems.Add(item);
                        _moleMenu.Add(item);

                        index++;
                    }

                    _moleRedeemTotalItem = new NativeItem(T("AssetLeaking.RedeemTotalNA", "Tổng số tiền cần chuộc: N/A"));
                    _moleMenu.Add(_moleRedeemTotalItem);
                }
            }

            var confirm = new NativeItem(T("AssetLeaking.ConfirmRedeem", "Xác nhận chuộc lại phương tiện"));
            var cancel = new NativeItem(T("AssetLeaking.CancelDangerousDeal", "Hủy giao dịch ngầm nguy hiểm"));

            confirm.Activated += (s, e) =>
            {
                ConfirmRedeemSelectedVehicles(ownerHash);
            };

            cancel.Activated += (s, e) =>
            {
                CloseMoleMenu();
            };

            _moleMenu.Add(confirm);
            _moleMenu.Add(cancel);
            _moleUiSync = true;

            UpdateMoleRedeemTotalItem();
        }
        catch
        {
        }
    }

    private void UpdateMoleRedeemTotalItem()
    {
        try
        {
            if (_moleRedeemTotalItem == null)
                return;

            long totalCost = 0L;
            bool anySelected = false;

            for (int i = 0; i < _moleCheckboxItems.Count && i < _moleEntries.Count; i++)
            {
                var cb = _moleCheckboxItems[i];
                var record = _moleEntries[i];

                if (cb != null && cb.Checked && record != null)
                {
                    anySelected = true;
                    totalCost += GetRedeemCost(record.PurchasePrice);
                }
            }

            _moleRedeemTotalItem.Title = anySelected
                ? TF("AssetLeaking.RedeemTotal", "Tổng số tiền cần chuộc: {0}", FormatMoney(totalCost))
                : T("AssetLeaking.RedeemTotalNA", "Tổng số tiền cần chuộc: N/A");
        }
        catch
        {
            // Có thể thêm log lỗi ở đây nếu cần thiết
        }
    }

    private void ConfirmRedeemSelectedVehicles(int ownerHash)
    {
        try
        {
            if (ownerHash == 0)
            {
                GTA.UI.Screen.ShowSubtitle(T("AssetLeaking.UnknownCharacter", "Không xác định được nhân vật hiện tại."), 2500);
                return;
            }

            var selected = new List<ConfiscatedVehicleRecord>();

            for (int i = 0; i < _moleCheckboxItems.Count && i < _moleEntries.Count; i++)
            {
                if (_moleCheckboxItems[i].Checked)
                    selected.Add(_moleEntries[i]);
            }

            if (selected.Count == 0)
            {
                GTA.UI.Screen.ShowSubtitle(T("AssetLeaking.SelectAtLeastOneVehicle", "Hãy chọn ít nhất một phương tiện."), 2500);
                return;
            }

            long totalCost = 0L;
            foreach (var r in selected)
                totalCost += GetDisplayRedeemCost(r.PurchasePrice);

            if (Game.Player.Money < totalCost)
            {
                GTA.UI.Screen.ShowSubtitle(
                    TF("AssetLeaking.InsufficientMoney", "Không đủ tiền. Cần ~HUD_COLOUR_REDLIGHT~{0}.~s~", FormatMoney(totalCost)),
                    3000);
                return;
            }

            DeductMoney(totalCost);
            ScheduleWantedPenaltyAfterRedeem();

            int restored = 0;

            for (int i = 0; i < selected.Count; i++)
            {
                var record = selected[i];
                if (record == null)
                    continue;

                if (!RemoveConfiscatedRecord(ownerHash, record))
                    continue;

                Vector3 spawnPos = record.Position;
                float spawnHeading = record.Heading;

                Vehicle spawned = SpawnVehicleFromRecord(record, spawnPos, spawnHeading);
                if (spawned != null && spawned.Exists())
                {
                    try
                    {
                        var meta = new PersistentManager.VehicleMeta
                        {
                            Mods = record.Mods != null ? new Dictionary<int, int>(record.Mods) : new Dictionary<int, int>(),
                            TurboOn = record.TurboOn,
                            PurchasePrice = (int)Math.Min(int.MaxValue, Math.Max(0L, record.PurchasePrice))
                        };

                        PersistentManager.RegisterVehicle(spawned, meta);
                        PersistentManager.UpdatePersistentFromVehicle(spawned);
                    }
                    catch
                    {
                    }

                    restored++;
                }
                else
                {
                    AppendConfiscatedLine(ownerHash, SerializeRecord(record));
                    EnforceConfiscatedCap(ownerHash);
                }
            }

            ShowMoleNotification(
                T("AssetLeaking.ContactName", "Mole"),
                TF("AssetLeaking.RedeemSuccess", "Đã chuộc lại {0} phương tiện. Tổng phí: {1}.", restored, FormatMoney(totalCost)));

            CloseMoleMenu();
        }
        catch (Exception ex)
        {
            Log(T("AssetLeaking.RedeemFailedLogPrefix", "ConfirmRedeemSelectedVehicles failed: ") + ex);
            GTA.UI.Notification.Show(T("AssetLeaking.RedeemFailed", "Chuộc phương tiện thất bại."));
        }
    }

    private static void DeductMoney(long amount)
    {
        try
        {
            if (amount <= 0)
                return;

            Game.Player.Money -= (int)Math.Min(int.MaxValue, amount);
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

    private static long GetDisplayRedeemCost(long purchasePrice)
    {
        if (purchasePrice <= 0)
            return ZERO_PRICE_REDEEM_COST;

        return GetRedeemCost(purchasePrice);
    }

    private static long GetOriginalVehiclePrice(long purchasePrice)
    {
        return Math.Max(0L, purchasePrice);
    }

    private static void ShowMoleNotification(string title, string message)
    {
        try
        {
            GTA.UI.Notification.Show(
                GTA.UI.NotificationIcon.LesterDeathwish,
                title,
                T("AssetLeaking.NotificationSubtitle", "Mole"),
                message);
        }
        catch
        {
            try { GTA.UI.Notification.Show(message); } catch { }
        }
    }

    private void ScheduleWantedPenaltyAfterRedeem()
    {
        try
        {
            _wantedPenaltyDueTimes.Add(Game.GameTime + WANTED_PENALTY_DELAY_MS);
        }
        catch
        {
        }
    }

    private void UpdateWantedPenaltyPending()
    {
        try
        {
            if (_wantedPenaltyDueTimes.Count == 0)
                return;

            int now = Game.GameTime;

            for (int i = _wantedPenaltyDueTimes.Count - 1; i >= 0; i--)
            {
                if (now < _wantedPenaltyDueTimes[i])
                    continue;

                _wantedPenaltyDueTimes.RemoveAt(i);

                // 60% cơ hội cộng 3 sao
                if (_rng.Next(100) >= WANTED_PENALTY_CHANCE_PERCENT)
                    continue;

                if (ApplyWantedPenalty(WANTED_PENALTY_ADD_STARS))
                    TryRollMoleLockAfterWantedPenalty();
            }
        }
        catch
        {
        }
    }

    private void TryRollMoleLockAfterWantedPenalty()
    {
        try
        {
            if (_rng.Next(100) >= MOLE_LOCK_CHANCE_PERCENT)
                return;

            LockMoleContactForHours(MOLE_LOCK_DURATION_HOURS);
        }
        catch
        {
        }
    }

    private void LockMoleContactForHours(int hours)
    {
        try
        {
            if (hours <= 0)
                return;

            DateTime now = GetCurrentGameDateTime();
            if (now == DateTime.MinValue)
                return;

            _moleContactLocked = true;
            _moleContactUnlockAtGameTime = now.AddHours(hours);
            SyncMoleContactActiveState();
        }
        catch
        {
        }
    }

    private void UpdateMoleContactLockState()
    {
        try
        {
            if (!_moleContactLocked || !_moleContactUnlockAtGameTime.HasValue)
                return;

            DateTime now = GetCurrentGameDateTime();
            if (now == DateTime.MinValue)
                return;

            if (now < _moleContactUnlockAtGameTime.Value)
                return;

            _moleContactLocked = false;
            _moleContactUnlockAtGameTime = null;
            SyncMoleContactActiveState();
        }
        catch
        {
        }
    }

    private void SyncMoleContactActiveState()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            string contactName = T("AssetLeaking.ContactName", "Mole");

            var contact = phone.Contacts.FirstOrDefault(c =>
                c != null &&
                string.Equals(c.Name, contactName, StringComparison.OrdinalIgnoreCase));

            if (contact != null)
            {
                try
                {
                    contact.Active = !_moleContactLocked;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static DateTime GetCurrentGameDateTime()
    {
        try
        {
            int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);
            int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
            int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);
            int hour = Function.Call<int>(Hash.GET_CLOCK_HOURS);
            int minute = Function.Call<int>(Hash.GET_CLOCK_MINUTES);
            int second = Function.Call<int>(Hash.GET_CLOCK_SECONDS);

            // Một số bản natives trả month theo kiểu 0..11, một số trả 1..12.
            if (month >= 0 && month <= 11)
                month += 1;

            month = Math.Max(1, Math.Min(12, month));
            day = Math.Max(1, Math.Min(31, day));
            hour = Math.Max(0, Math.Min(23, hour));
            minute = Math.Max(0, Math.Min(59, minute));
            second = Math.Max(0, Math.Min(59, second));

            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static bool ApplyWantedPenalty(int addStars)
    {
        try
        {
            if (addStars <= 0)
                return false;

            int current = 0;
            try
            {
                current = Game.Player.WantedLevel;
            }
            catch
            {
                current = 0;
            }

            int next = Math.Min(WANTED_PENALTY_MAX_STARS, current + addStars);
            if (next > current)
            {
                try
                {
                    Game.Player.WantedLevel = next;
                    AssetLeakingWantedState.MarkAssetLeakingWantedActive();
                }
                catch
                {
                }

                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    public static long GetRedeemCost(long purchasePrice)
    {
        if (purchasePrice <= 0)
            return ZERO_PRICE_REDEEM_COST;

        return (long)Math.Round((decimal)purchasePrice * REDEEM_PERCENT, MidpointRounding.AwayFromZero);
    }

    public static int GetOwnerHashFromCurrentCharacter()
    {
        return GetCurrentCharacterHash();
    }

    public static string GetConfiscatedFilePath(int ownerHash)
    {
        EnsureFolders();
        return Path.Combine(ConfiscatedRoot, $"confiscated_vehicles_{ownerHash}.dat");
    }

    public static int GetConfiscatedCount(int ownerHash)
    {
        try
        {
            return LoadConfiscatedRecords(ownerHash).Count;
        }
        catch
        {
            return 0;
        }
    }

    public static List<ConfiscatedVehicleRecord> LoadConfiscatedRecords(int ownerHash)
    {
        var result = new List<ConfiscatedVehicleRecord>();

        try
        {
            if (ownerHash == 0)
                return result;

            string file = GetConfiscatedFilePath(ownerHash);
            if (!File.Exists(file))
                return result;

            foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (TryParsePersistentVehicleLine(line, out ConfiscatedVehicleRecord record) && record != null)
                    result.Add(record);
            }
        }
        catch
        {
        }

        return result;
    }

    public static bool TryConfiscateVehicleEntry(object persistentEntry, int ownerHash = 0)
    {
        try
        {
            if (persistentEntry == null)
                return false;

            if (ownerHash == 0)
                ownerHash = GetEntryOwnerHash(persistentEntry);

            if (ownerHash == 0)
                return false;

            var record = BuildRecordFromPersistentEntry(persistentEntry);
            if (record == null)
                return false;

            record.OwnerHash = ownerHash;

            string line = SerializeRecord(record);

            RemoveFromPersistentRuntimeAndSave(persistentEntry);
            AppendConfiscatedLine(ownerHash, line);
            EnforceConfiscatedCap(ownerHash);
            RemoveMatchingLineFromPersistentFile(record);

            // Ép cập nhật lại icon còn lại cho đúng trạng thái hiện tại
            try { PersistentManager.RefreshVehicleBlipsForCurrentCharacter(); } catch { }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryConfiscateRandomVehicleForCurrentCharacter(IEnumerable<object> ownedEntries, out ConfiscatedVehicleRecord confiscated, out long coveredValue)
    {
        confiscated = null;
        coveredValue = 0L;

        try
        {
            var list = new List<object>();

            if (ownedEntries != null)
            {
                foreach (var e in ownedEntries)
                {
                    if (e != null)
                        list.Add(e);
                }
            }

            if (list.Count == 0)
                return false;

            object chosen = list[_rng.Next(list.Count)];
            confiscated = BuildRecordFromPersistentEntry(chosen);
            if (confiscated == null)
                return false;

            coveredValue = Math.Max(0L, confiscated.PurchasePrice);

            int ownerHash = confiscated.OwnerHash != 0 ? confiscated.OwnerHash : GetEntryOwnerHash(chosen);
            if (ownerHash == 0)
                return false;

            return TryConfiscateVehicleEntry(chosen, ownerHash);
        }
        catch
        {
            confiscated = null;
            coveredValue = 0L;
            return false;
        }
    }

    public static bool TryConfiscateRandomVehicleForCurrentCharacter(IEnumerable<object> ownedEntries)
    {
        ConfiscatedVehicleRecord r;
        long v;
        return TryConfiscateRandomVehicleForCurrentCharacter(ownedEntries, out r, out v);
    }

    public static bool TryRedeemConfiscatedVehicle(int ownerHash, int index, out long cost, Vector3? spawnPosition = null, float? heading = null)
    {
        cost = 0L;

        try
        {
            if (ownerHash == 0)
                return false;

            var list = LoadConfiscatedRecords(ownerHash);
            if (index < 0 || index >= list.Count)
                return false;

            var record = list[index];
            if (record == null)
                return false;

            cost = GetRedeemCost(record.PurchasePrice);

            if (Game.Player.Money < cost)
                return false;

            Vector3 pos = spawnPosition ?? record.Position;
            float hdg = heading ?? record.Heading;

            if (!RemoveConfiscatedRecord(ownerHash, record))
                return false;

            Vehicle spawned = SpawnVehicleFromRecord(record, pos, hdg);
            if (spawned == null || !spawned.Exists())
            {
                AppendConfiscatedLine(ownerHash, SerializeRecord(record));
                EnforceConfiscatedCap(ownerHash);
                return false;
            }

            DeductMoney(cost);

            var meta = new PersistentManager.VehicleMeta
            {
                Mods = record.Mods != null ? new Dictionary<int, int>(record.Mods) : new Dictionary<int, int>(),
                TurboOn = record.TurboOn,
                PurchasePrice = (int)Math.Min(int.MaxValue, Math.Max(0L, record.PurchasePrice))
            };

            try
            {
                PersistentManager.RegisterVehicle(spawned, meta);
            }
            catch
            {
            }

            try
            {
                PersistentManager.UpdatePersistentFromVehicle(spawned);
            }
            catch
            {
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryRedeemConfiscatedVehicleByRecord(int ownerHash, ConfiscatedVehicleRecord record, out long cost, Vector3? spawnPosition = null, float? heading = null)
    {
        cost = 0L;

        try
        {
            if (ownerHash == 0 || record == null)
                return false;

            var list = LoadConfiscatedRecords(ownerHash);
            int index = list.FindIndex(x => RecordEquals(x, record));
            if (index < 0)
                return false;

            return TryRedeemConfiscatedVehicle(ownerHash, index, out cost, spawnPosition, heading);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryRemoveConfiscatedVehicleByRecord(int ownerHash, ConfiscatedVehicleRecord record)
    {
        try
        {
            if (ownerHash == 0 || record == null)
                return false;

            return RemoveConfiscatedRecord(ownerHash, record);
        }
        catch
        {
            return false;
        }
    }

    public static string GetDisplayName(uint modelHash)
    {
        try
        {
            return PersistentManager.GetVehicleDisplayNamePublic(modelHash);
        }
        catch
        {
            return $"0x{modelHash:X}";
        }
    }

    public static string GetVehicleDisplayNamePublic(uint modelHash)
    {
        return GetDisplayName(modelHash);
    }

    private static void EnsureFolders()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            Directory.CreateDirectory(ConfiscatedRoot);
        }
        catch
        {
        }
    }

    private static int GetEntryOwnerHash(object entry)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField("OwnerModelHash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return 0;

            object v = f.GetValue(entry);
            if (v is int i) return i;
        }
        catch
        {
        }

        return 0;
    }

    private static uint GetEntryModelHash(object entry)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField("ModelHash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return 0u;

            object v = f.GetValue(entry);
            if (v is uint u) return u;
            if (v is int i) return unchecked((uint)i);
        }
        catch
        {
        }

        return 0u;
    }

    private static Vector3 GetEntryPosition(object entry)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField("Position", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return Vector3.Zero;

            object v = f.GetValue(entry);
            if (v is Vector3 p) return p;
        }
        catch
        {
        }

        return Vector3.Zero;
    }

    private static float GetEntryHeading(object entry)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField("Heading", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return 0f;

            object v = f.GetValue(entry);
            if (v is float h) return h;
        }
        catch
        {
        }

        return 0f;
    }

    private static string GetEntryPlate(object entry)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField("Plate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return "";

            object v = f.GetValue(entry);
            if (v is string s) return s;
        }
        catch
        {
        }

        return "";
    }

    private static bool GetEntryAutoSpawn(object entry)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField("AutoSpawnEnabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return false;

            object v = f.GetValue(entry);
            if (v is bool b) return b;
        }
        catch
        {
        }

        return false;
    }

    private static bool GetEntryTurbo(object entry)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField("TurboOn", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return false;

            object v = f.GetValue(entry);
            if (v is bool b) return b;
        }
        catch
        {
        }

        return false;
    }

    private static long GetEntryPurchasePrice(object entry)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField("PurchasePrice", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return 0L;

            object v = f.GetValue(entry);
            if (v is int i) return Math.Max(0L, i);
            if (v is long l) return Math.Max(0L, l);
        }
        catch
        {
        }

        return 0L;
    }

    private static int? GetEntryNullableInt(object entry, string fieldName)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return null;

            object v = f.GetValue(entry);
            if (v == null) return null;
            if (v is int i) return i;
        }
        catch
        {
        }

        return null;
    }

    private static int[] GetEntryIntArray(object entry, string fieldName, int length = 3)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return null;

            object v = f.GetValue(entry);
            if (v is int[] arr && arr.Length >= length)
                return (int[])arr.Clone();
        }
        catch
        {
        }

        return null;
    }

    private static List<int> GetEntryIntList(object entry, string fieldName)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return new List<int>();

            object v = f.GetValue(entry);
            if (v is List<int> list)
                return new List<int>(list);
        }
        catch
        {
        }

        return new List<int>();
    }

    private static Dictionary<int, int> GetEntryMods(object entry)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField("Mods", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return new Dictionary<int, int>();

            object v = f.GetValue(entry);
            if (v is Dictionary<int, int> dict)
                return new Dictionary<int, int>(dict);
        }
        catch
        {
        }

        return new Dictionary<int, int>();
    }

    private static bool GetEntryBool(object entry, string fieldName)
    {
        try
        {
            var t = entry.GetType();
            var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return false;

            object v = f.GetValue(entry);
            if (v is bool b) return b;
        }
        catch
        {
        }

        return false;
    }

    private static ConfiscatedVehicleRecord BuildRecordFromPersistentEntry(object entry)
    {
        try
        {
            if (entry == null)
                return null;

            var record = new ConfiscatedVehicleRecord
            {
                ModelHash = GetEntryModelHash(entry),
                Position = GetEntryPosition(entry),
                Heading = GetEntryHeading(entry),
                Plate = GetEntryPlate(entry),
                OwnerHash = GetEntryOwnerHash(entry),
                AutoSpawnEnabled = GetEntryAutoSpawn(entry),
                Mods = GetEntryMods(entry),
                TurboOn = GetEntryTurbo(entry),
                PurchasePrice = GetEntryPurchasePrice(entry),
                SavedLivery = GetEntryNullableInt(entry, "SavedLivery"),
                PrimaryColor = GetEntryNullableInt(entry, "PrimaryColor"),
                SecondaryColor = GetEntryNullableInt(entry, "SecondaryColor"),
                PearlColor = GetEntryNullableInt(entry, "PearlColor"),
                WheelColor = GetEntryNullableInt(entry, "WheelColor"),
                WindowTint = GetEntryNullableInt(entry, "WindowTint"),
                TyreSmokeColor = GetEntryIntArray(entry, "TyreSmokeColor", 3) ?? new int[3] { 0, 0, 0 },
                ExtrasExist = GetEntryIntList(entry, "ExtrasExist"),
                NeonColor = GetEntryIntArray(entry, "NeonColor", 3),
                DashboardColor = GetEntryNullableInt(entry, "DashboardColor"),
                IsCollateralLocked = GetEntryBool(entry, "IsCollateralLocked")
            };

            return record;
        }
        catch
        {
            return null;
        }
    }

    private static string SerializeRecord(ConfiscatedVehicleRecord v)
    {
        try
        {
            if (v == null)
                return "";

            string modsSerialized = "";
            if (v.Mods != null && v.Mods.Count > 0)
            {
                modsSerialized = string.Join(",", v.Mods.Select(kv => $"{kv.Key}:{kv.Value}"));
            }

            string tyreRgb = "";
            if (v.TyreSmokeColor != null && v.TyreSmokeColor.Length >= 3)
                tyreRgb = $"{v.TyreSmokeColor[0]},{v.TyreSmokeColor[1]},{v.TyreSmokeColor[2]}";

            string extrasSerialized = "";
            if (v.ExtrasExist != null && v.ExtrasExist.Count > 0)
                extrasSerialized = string.Join(",", v.ExtrasExist);

            string neonRgb = "";
            if (v.NeonColor != null && v.NeonColor.Length >= 3)
                neonRgb = $"{v.NeonColor[0]},{v.NeonColor[1]},{v.NeonColor[2]}";

            string dashStr = v.DashboardColor.HasValue
                ? v.DashboardColor.Value.ToString(CultureInfo.InvariantCulture)
                : "";

            string modelName = GetDisplayName(v.ModelHash);
            modelName = (modelName ?? $"0x{v.ModelHash:X}").Replace("|", "");

            string hexSuffix = $" (0x{v.ModelHash:X})";
            if (!modelName.EndsWith(hexSuffix, StringComparison.OrdinalIgnoreCase))
                modelName += hexSuffix;

            return string.Format(CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}|{11}|{12}|{13}|{14}|{15}|{16}|{17}|{18}|{19}|{20}",
                modelName,
                v.Position.X, v.Position.Y, v.Position.Z,
                v.Heading,
                (v.Plate ?? "").Replace("|", ""),
                v.OwnerHash,
                v.AutoSpawnEnabled ? 1 : 0,
                modsSerialized,
                v.TurboOn ? 1 : 0,
                v.PurchasePrice,
                v.SavedLivery.HasValue ? v.SavedLivery.Value.ToString(CultureInfo.InvariantCulture) : "",
                v.PrimaryColor.HasValue ? v.PrimaryColor.Value.ToString(CultureInfo.InvariantCulture) : "",
                v.SecondaryColor.HasValue ? v.SecondaryColor.Value.ToString(CultureInfo.InvariantCulture) : "",
                v.PearlColor.HasValue ? v.PearlColor.Value.ToString(CultureInfo.InvariantCulture) : "",
                v.WheelColor.HasValue ? v.WheelColor.Value.ToString(CultureInfo.InvariantCulture) : "",
                v.WindowTint.HasValue ? v.WindowTint.Value.ToString(CultureInfo.InvariantCulture) : "",
                tyreRgb,
                extrasSerialized,
                neonRgb,
                dashStr
            );
        }
        catch
        {
            return "";
        }
    }

    private static bool TryParsePersistentVehicleLine(string line, out ConfiscatedVehicleRecord record)
    {
        record = null;

        try
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string[] p = line.Split('|');
            if (p.Length < 7)
                return false;

            uint modelHash = ParseModelHash(p[0]);
            if (modelHash == 0u)
                return false;

            float x = ParseFloat(p, 1);
            float y = ParseFloat(p, 2);
            float z = ParseFloat(p, 3);
            float heading = ParseFloat(p, 4);

            int ownerHash = 0;
            int.TryParse(GetSafePart(p, 6), NumberStyles.Integer, CultureInfo.InvariantCulture, out ownerHash);

            bool autoSpawn = false;
            if (p.Length > 7)
            {
                int a = 0;
                int.TryParse(p[7], out a);
                autoSpawn = (a != 0);
            }

            Dictionary<int, int> modsDict = new Dictionary<int, int>();
            if (p.Length > 8 && !string.IsNullOrEmpty(p[8]))
            {
                foreach (var part in p[8].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var kv = part.Split(':');
                        if (kv.Length != 2) continue;

                        int mt = 0, mi = 0;
                        if (int.TryParse(kv[0], out mt) && int.TryParse(kv[1], out mi))
                            modsDict[mt] = mi;
                    }
                    catch
                    {
                    }
                }
            }

            int turbo = 0;
            if (p.Length > 9)
                int.TryParse(p[9], out turbo);

            long purchasePrice = 0;
            if (p.Length > 10)
                long.TryParse(p[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out purchasePrice);

            int? savedLivery = null;
            int? primary = null;
            int? secondary = null;
            int? pearl = null;
            int? wheel = null;
            int? windowTint = null;
            int[] tyreRgb = new int[3] { 0, 0, 0 };
            List<int> extrasExist = new List<int>();
            int[] neonRgb = null;
            int? dashColor = null;

            if (p.Length > 11 && !string.IsNullOrWhiteSpace(p[11]))
            {
                int tmp;
                if (int.TryParse(p[11], out tmp)) savedLivery = tmp;
            }

            if (p.Length > 12 && !string.IsNullOrWhiteSpace(p[12]))
            {
                int tmp;
                if (int.TryParse(p[12], out tmp)) primary = tmp;
            }

            if (p.Length > 13 && !string.IsNullOrWhiteSpace(p[13]))
            {
                int tmp;
                if (int.TryParse(p[13], out tmp)) secondary = tmp;
            }

            if (p.Length > 14 && !string.IsNullOrWhiteSpace(p[14]))
            {
                int tmp;
                if (int.TryParse(p[14], out tmp)) pearl = tmp;
            }

            if (p.Length > 15 && !string.IsNullOrWhiteSpace(p[15]))
            {
                int tmp;
                if (int.TryParse(p[15], out tmp)) wheel = tmp;
            }

            if (p.Length > 16 && !string.IsNullOrWhiteSpace(p[16]))
            {
                int tmp;
                if (int.TryParse(p[16], out tmp)) windowTint = tmp;
            }

            if (p.Length > 17 && !string.IsNullOrWhiteSpace(p[17]))
            {
                var rr = p[17].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (rr.Length >= 3)
                {
                    int.TryParse(rr[0], out tyreRgb[0]);
                    int.TryParse(rr[1], out tyreRgb[1]);
                    int.TryParse(rr[2], out tyreRgb[2]);
                }
            }

            if (p.Length > 18 && !string.IsNullOrWhiteSpace(p[18]))
            {
                foreach (var s in p[18].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int ei = 0;
                    if (int.TryParse(s, out ei))
                        extrasExist.Add(ei);
                }
            }

            if (p.Length > 19 && !string.IsNullOrWhiteSpace(p[19]))
            {
                var rr = p[19].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (rr.Length >= 3)
                {
                    int nr = 0, ng = 0, nb = 0;
                    int.TryParse(rr[0], out nr);
                    int.TryParse(rr[1], out ng);
                    int.TryParse(rr[2], out nb);
                    neonRgb = new int[] { nr, ng, nb };
                }
            }

            if (p.Length > 20 && !string.IsNullOrWhiteSpace(p[20]))
            {
                int tmp;
                if (int.TryParse(p[20], out tmp))
                    dashColor = tmp;
            }

            record = new ConfiscatedVehicleRecord
            {
                ModelHash = modelHash,
                Position = new Vector3(x, y, z),
                Heading = heading,
                Plate = p.Length > 5 ? p[5] : "",
                OwnerHash = ownerHash,
                AutoSpawnEnabled = autoSpawn,
                Mods = modsDict,
                TurboOn = turbo != 0,
                PurchasePrice = purchasePrice,
                SavedLivery = savedLivery,
                PrimaryColor = primary,
                SecondaryColor = secondary,
                PearlColor = pearl,
                WheelColor = wheel,
                WindowTint = windowTint,
                TyreSmokeColor = tyreRgb,
                ExtrasExist = extrasExist,
                NeonColor = neonRgb,
                DashboardColor = dashColor
            };

            return true;
        }
        catch
        {
            record = null;
            return false;
        }
    }

    private static uint ParseModelHash(string raw)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(raw))
                return 0u;

            raw = raw.Trim();

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

            if (uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex2))
                return hex2;

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

    private static float ParseFloat(string[] parts, int index)
    {
        if (parts == null || index < 0 || index >= parts.Length)
            return 0f;

        float value = 0f;
        float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return value;
    }

    private static string GetSafePart(string[] p, int index)
    {
        if (p == null || index < 0 || index >= p.Length)
            return "";
        return p[index] ?? "";
    }

    private static bool RecordEquals(ConfiscatedVehicleRecord a, ConfiscatedVehicleRecord b)
    {
        try
        {
            if (a == null || b == null)
                return false;

            return a.ModelHash == b.ModelHash
                && NormalizePlate(a.Plate) == NormalizePlate(b.Plate)
                && a.Position.DistanceTo2D(b.Position) < 5f;
        }
        catch
        {
            return false;
        }
    }

    private static bool RecordMatchesLine(string line, ConfiscatedVehicleRecord record)
    {
        try
        {
            if (!TryParsePersistentVehicleLine(line, out ConfiscatedVehicleRecord parsed) || parsed == null || record == null)
                return false;

            if (parsed.ModelHash != record.ModelHash)
                return false;

            string plateA = NormalizePlate(parsed.Plate);
            string plateB = NormalizePlate(record.Plate);

            if (!string.IsNullOrEmpty(plateA) && !string.IsNullOrEmpty(plateB) && plateA == plateB)
                return true;

            if (parsed.Position.DistanceTo2D(record.Position) < 5f)
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePlate(string plate)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(plate))
                return "";

            return new string(plate.Trim().Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
        }
        catch
        {
            return "";
        }
    }

    private static void AppendConfiscatedLine(int ownerHash, string line)
    {
        try
        {
            if (ownerHash == 0 || string.IsNullOrWhiteSpace(line))
                return;

            EnsureFolders();
            string file = GetConfiscatedFilePath(ownerHash);

            var lines = new List<string>();
            if (File.Exists(file))
                lines.AddRange(File.ReadAllLines(file, Encoding.UTF8).Where(x => !string.IsNullOrWhiteSpace(x)));

            lines.Add(line);
            File.WriteAllLines(file, lines.ToArray(), Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static bool RemoveConfiscatedRecord(int ownerHash, ConfiscatedVehicleRecord record)
    {
        try
        {
            if (ownerHash == 0 || record == null)
                return false;

            string file = GetConfiscatedFilePath(ownerHash);
            if (!File.Exists(file))
                return false;

            var kept = new List<string>();
            bool removed = false;

            foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
            {
                try
                {
                    if (!removed && RecordMatchesLine(line, record))
                    {
                        removed = true;
                        continue;
                    }

                    kept.Add(line);
                }
                catch
                {
                    kept.Add(line);
                }
            }

            File.WriteAllLines(file, kept.ToArray(), Encoding.UTF8);
            return removed;
        }
        catch
        {
            return false;
        }
    }

    private static void EnforceConfiscatedCap(int ownerHash)
    {
        try
        {
            string file = GetConfiscatedFilePath(ownerHash);
            if (!File.Exists(file))
                return;

            var lines = File.ReadAllLines(file, Encoding.UTF8)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            while (lines.Count > MAX_CONFISCATED_PER_CHAR)
                lines.RemoveAt(0);

            File.WriteAllLines(file, lines.ToArray(), Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void RemoveMatchingLineFromPersistentFile(ConfiscatedVehicleRecord record)
    {
        try
        {
            if (record == null)
                return;

            if (!File.Exists(PersistentVehiclesFile))
                return;

            var kept = new List<string>();
            bool removed = false;

            foreach (var line in File.ReadAllLines(PersistentVehiclesFile, Encoding.UTF8))
            {
                try
                {
                    if (!removed && RecordMatchesLine(line, record))
                    {
                        removed = true;
                        continue;
                    }

                    kept.Add(line);
                }
                catch
                {
                    kept.Add(line);
                }
            }

            File.WriteAllLines(PersistentVehiclesFile, kept.ToArray(), Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void RemoveFromPersistentRuntimeAndSave(object entry)
    {
        try
        {
            Type pmType = typeof(PersistentManager);

            FieldInfo vehiclesField = pmType.GetField("_persistVehicles", BindingFlags.NonPublic | BindingFlags.Static);
            if (vehiclesField != null)
            {
                object listObj = vehiclesField.GetValue(null);
                if (listObj is System.Collections.IList list)
                {
                    try
                    {
                        // Xóa icon cũ ngay trước khi gỡ khỏi danh sách
                        ClearPersistentEntryMapBlip(entry);

                        if (entry != null && list.Contains(entry))
                            list.Remove(entry);
                    }
                    catch
                    {
                    }
                }
            }

            FieldInfo dirtyField = pmType.GetField("_vehiclesDirty", BindingFlags.NonPublic | BindingFlags.Static);
            if (dirtyField != null)
                dirtyField.SetValue(null, true);

            MethodInfo saveMethod = pmType.GetMethod("SaveVehiclesFileInternal", BindingFlags.NonPublic | BindingFlags.Static);
            if (saveMethod != null)
                saveMethod.Invoke(null, null);
        }
        catch
        {
        }
    }

    private static void ClearPersistentEntryMapBlip(object persistentEntry)
    {
        try
        {
            if (persistentEntry == null)
                return;

            var t = persistentEntry.GetType();

            FieldInfo blipField = t.GetField("MapBlip", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (blipField != null)
            {
                object blipObj = blipField.GetValue(persistentEntry);
                if (blipObj is Blip b)
                {
                    try
                    {
                        if (b.Exists())
                            b.Delete();
                    }
                    catch { }

                    blipField.SetValue(persistentEntry, null);
                }
            }

            FieldInfo runtimeField = t.GetField("RuntimeVehicle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (runtimeField != null)
                runtimeField.SetValue(persistentEntry, null);

            FieldInfo suppressField = t.GetField("SuppressBlipWhileOccupied", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (suppressField != null)
                suppressField.SetValue(persistentEntry, false);
        }
        catch
        {
        }
    }

    private static Vehicle SpawnVehicleFromRecord(ConfiscatedVehicleRecord record, Vector3 position, float heading)
    {
        try
        {
            if (record == null)
                return null;

            Model model = new Model((int)record.ModelHash);
            if (!model.IsValid || !model.IsInCdImage)
                return null;

            if (!model.IsLoaded)
                model.Request(1500);

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
            catch
            {
            }

            Vehicle v = World.CreateVehicle(model, position, heading);
            if (v == null || !v.Exists())
            {
                model.MarkAsNoLongerNeeded();
                return null;
            }

            try
            {
                v.PlaceOnGround();
            }
            catch
            {
            }

            ApplyRecordToVehicle(v, record);

            model.MarkAsNoLongerNeeded();
            return v;
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyRecordToVehicle(Vehicle v, ConfiscatedVehicleRecord record)
    {
        try
        {
            if (v == null || !v.Exists() || record == null)
                return;

            try
            {
                if (!string.IsNullOrWhiteSpace(record.Plate))
                    v.Mods.LicensePlate = record.Plate;
            }
            catch
            {
            }

            try { v.DirtLevel = 0f; } catch { }
            try { v.IsEngineRunning = true; } catch { }

            try { Function.Call(Hash.SET_VEHICLE_MOD_KIT, v.Handle, 0); } catch { }

            if (record.Mods != null)
            {
                foreach (var kv in record.Mods)
                {
                    try { Function.Call(Hash.SET_VEHICLE_MOD, v.Handle, kv.Key, kv.Value, false); } catch { }
                }
            }

            if (record.TurboOn)
            {
                try { Function.Call(Hash.TOGGLE_VEHICLE_MOD, v.Handle, 18, true); } catch { }
            }

            if (record.SavedLivery.HasValue)
            {
                try { Function.Call(Hash.SET_VEHICLE_LIVERY, v.Handle, record.SavedLivery.Value); } catch { }
            }

            if (record.PrimaryColor.HasValue && record.SecondaryColor.HasValue)
            {
                try
                {
                    Function.Call(Hash.SET_VEHICLE_COLOURS, v.Handle, record.PrimaryColor.Value, record.SecondaryColor.Value);
                }
                catch
                {
                }
            }

            if (record.PearlColor.HasValue && record.WheelColor.HasValue)
            {
                try
                {
                    Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, v.Handle, record.PearlColor.Value, record.WheelColor.Value);
                }
                catch
                {
                }
            }

            if (record.WindowTint.HasValue)
            {
                try { Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v.Handle, record.WindowTint.Value); } catch { }
            }

            if (record.TyreSmokeColor != null && record.TyreSmokeColor.Length >= 3)
            {
                try
                {
                    Function.Call(Hash.SET_VEHICLE_TYRE_SMOKE_COLOR,
                        v.Handle,
                        record.TyreSmokeColor[0],
                        record.TyreSmokeColor[1],
                        record.TyreSmokeColor[2]);
                }
                catch
                {
                }
            }

            if (record.ExtrasExist != null && record.ExtrasExist.Count > 0)
            {
                foreach (var ex in record.ExtrasExist)
                {
                    try
                    {
                        if (Function.Call<bool>(Hash.DOES_EXTRA_EXIST, v.Handle, ex))
                            Function.Call(Hash.SET_VEHICLE_EXTRA, v.Handle, ex, 0);
                    }
                    catch
                    {
                    }
                }
            }

            if (record.NeonColor != null && record.NeonColor.Length >= 3)
            {
                for (int i = 0; i <= 3; i++)
                {
                    try { Function.Call(Hash.SET_VEHICLE_NEON_ENABLED, v.Handle, i, true); } catch { }
                }

                try
                {
                    Function.Call(Hash.SET_VEHICLE_NEON_COLOUR, v.Handle, record.NeonColor[0], record.NeonColor[1], record.NeonColor[2]);
                }
                catch
                {
                }
            }

            try { v.Repair(); } catch { }
        }
        catch
        {
        }
    }

    private static void Log(string message)
    {
        try
        {
            Debug.WriteLine(message);
        }
        catch
        {
        }
    }

    public sealed class ConfiscatedVehicleRecord
    {
        public uint ModelHash;
        public Vector3 Position;
        public float Heading;
        public string Plate = "";
        public int OwnerHash;
        public bool AutoSpawnEnabled;
        public Dictionary<int, int> Mods = new Dictionary<int, int>();
        public bool TurboOn;
        public long PurchasePrice;

        public int? SavedLivery;
        public int? PrimaryColor;
        public int? SecondaryColor;
        public int? PearlColor;
        public int? WheelColor;
        public int? WindowTint;

        public int[] TyreSmokeColor = new int[3] { 0, 0, 0 };
        public List<int> ExtrasExist = new List<int>();
        public int[] NeonColor;
        public int? DashboardColor;
        public bool IsCollateralLocked;
    }

    public static class AssetLeakingWantedState
    {
        private static readonly object _sync = new object();
        private static bool _lockedByAssetLeaking = false;

        public static bool IsLockedByAssetLeaking
        {
            get
            {
                lock (_sync)
                    return _lockedByAssetLeaking;
            }
        }

        public static void MarkAssetLeakingWantedActive()
        {
            lock (_sync)
                _lockedByAssetLeaking = true;
        }

        public static void ClearIfWantedLevelZero()
        {
            try
            {
                if (Game.Player == null)
                    return;

                if (Game.Player.WantedLevel <= 0)
                {
                    lock (_sync)
                        _lockedByAssetLeaking = false;
                }
            }
            catch
            {
            }
        }
    }
}