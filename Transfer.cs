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

public class Transfer : Script
{
    private const int FRANKLIN_HASH = -1692214353;
    private const int MICHAEL_HASH = 225514697;
    private const int TREVOR_HASH = -1686040670;

    private const int CALL_DURATION_MS = 2000;
    private const int MENU_IDLE_INTERVAL_MS = 1000;
    private const int MENU_PROCESS_INTERVAL_MS = 0;

    private const int TRANSFER_NOTIFICATION_DELAY_MS = 1000;

    private static string L(string key, string fallback = "")
    {
        return Language.Get(key, fallback);
    }

    private static string LT(string key, string fallback, params string[] tokensAndValues)
    {
        return Language.ReplaceTokens(Language.Get(key, fallback), tokensAndValues);
    }

    private readonly ObjectPool _uiPool = new ObjectPool();

    private CustomiFruit _phoneInstance = null;
    private bool _contactAdded = false;
    private iFruitContact _transferContact = null;

    private NativeMenu _mainMenu;
    private bool _mainMenuReady = false;

    private NativeItem _recipientItem;
    private NativeItem _amountItem;
    private NativeItem _confirmItem;
    private NativeItem _cancelItem;

    private bool _mainMenuBuilt = false;
    private bool _menuOpen = false;
    private bool _callPending = false;
    private int _callDueGameTime = 0;

    private bool _pendingTransferNotification = false;
    private int _pendingTransferNotificationDue = 0;
    private int _pendingTransferRecipientHash = 0;
    private int _pendingTransferSenderHash = 0;
    private NotificationIcon _pendingTransferIcon = NotificationIcon.AllPlayersConf;
    private string _pendingTransferTitle = "";
    private string _pendingTransferSubject = "";
    private string _pendingTransferMessage = "";

    private enum FocusTarget
    {
        None,
        Recipient,
        Amount,
        Confirm,
        Cancel
    }

    private FocusTarget _focusedTarget = FocusTarget.None;

    private sealed class TransferState
    {
        public int DayKey;
        public long BaselineCash;
        public long MaxTransferAllowed;
        public long TransferredToday;
        public int RecipientHash;
    }

    private TransferState _state = new TransferState();
    private bool _stateLoaded = false;
    private int _currentStateOwnerHash = 0;
    private int _currentDayKey = -1;
    private int _lastDayCheckGameTime = 0;

    private long _requestedTransferAmount = 0;
    private bool _requestedTransferValid = false;

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "Transfer");

    private static readonly string CashStateRoot = Path.Combine(DataFolder, "cash_states");
    private static readonly Dictionary<int, long> _cashCache = new Dictionary<int, long>();
    private int _lastCashSyncOwnerHash = 0;

    private static readonly string StateFileRoot = Path.Combine(DataFolder, "states");

    private static readonly string TransferContactName = L("Transfer_ContactName", "Transfer");

    public Transfer()
    {
        Interval = MENU_IDLE_INTERVAL_MS;
        Tick += OnTick;
        KeyDown += OnKeyDown;
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            EnsureTransferContactRegistered();
            SyncStateForCurrentCharacterAndDay();
            UpdateTransferContactActiveState();

            if (_callPending)
            {
                if (Game.GameTime >= _callDueGameTime)
                {
                    _callPending = false;
                    try { Game.Player.Character.Task.PutAwayMobilePhone(); } catch { }
                    try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
                    OpenTransferMenu();
                }
            }

            ProcessPendingTransferNotification();

            if (_mainMenu != null && _mainMenu.Visible)
            {
                Interval = MENU_PROCESS_INTERVAL_MS;
                _uiPool.Process();
                HandleRecipientCycling();
                return;
            }

            Interval = MENU_IDLE_INTERVAL_MS;
        }
        catch
        {
            Interval = MENU_IDLE_INTERVAL_MS;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (_mainMenu == null || !_mainMenu.Visible)
                return;

            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Back)
            {
                CloseTransferMenu();
            }
        }
        catch { }
    }

    private static string GetCashStateFilePath(int ownerHash)
    {
        return Path.Combine(CashStateRoot, $"cash_state_{ownerHash}.dat");
    }

    private static long ClampMoney(long value)
    {
        if (value < 0L) return 0L;
        if (value > int.MaxValue) return int.MaxValue;
        return value;
    }

    private long LoadCashFromDiskOrStat(int characterHash)
    {
        try
        {
            if (!IsValidCharacter(characterHash))
                return 0L;

            Directory.CreateDirectory(CashStateRoot);
            string path = GetCashStateFilePath(characterHash);

            if (File.Exists(path))
            {
                string raw = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long cached))
                    return ClampMoney(cached);
            }

            return ClampMoney(GetStoryCashByCharacterHash(characterHash));
        }
        catch
        {
            return 0L;
        }
    }

    private long GetCachedCashForCharacter(int characterHash)
    {
        try
        {
            if (!IsValidCharacter(characterHash))
                return 0L;

            lock (_cashCache)
            {
                if (_cashCache.TryGetValue(characterHash, out long value))
                    return ClampMoney(value);
            }

            long loaded = LoadCashFromDiskOrStat(characterHash);
            lock (_cashCache)
            {
                _cashCache[characterHash] = loaded;
            }
            return loaded;
        }
        catch
        {
            return 0L;
        }
    }

    private void PersistCashForCharacter(int characterHash, long cash)
    {
        try
        {
            if (!IsValidCharacter(characterHash))
                return;

            Directory.CreateDirectory(CashStateRoot);
            string path = GetCashStateFilePath(characterHash);
            File.WriteAllText(path, ClampMoney(cash).ToString(CultureInfo.InvariantCulture), Encoding.UTF8);
        }
        catch { }
    }

    private void SetCachedCashForCharacter(int characterHash, long cash)
    {
        try
        {
            if (!IsValidCharacter(characterHash))
                return;

            long safe = ClampMoney(cash);

            lock (_cashCache)
            {
                _cashCache[characterHash] = safe;
            }

            SetStoryCashByCharacterHash(characterHash, safe);
            PersistCashForCharacter(characterHash, safe);

            if (GetCurrentCharacterHash() == characterHash)
            {
                try { Game.Player.Money = (int)safe; } catch { }
            }
        }
        catch { }
    }

    private void SyncCurrentCharacterCashFromCache()
    {
        try
        {
            int currentHash = GetCurrentCharacterHash();
            if (!IsValidCharacter(currentHash))
                return;

            long cachedCash = GetCachedCashForCharacter(currentHash);
            SetCachedCashForCharacter(currentHash, cachedCash);
            _lastCashSyncOwnerHash = currentHash;
        }
        catch { }
    }

    private void EnsureTransferContactRegistered()
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
                _transferContact = null;
            }

            if (_contactAdded && _transferContact != null)
                return;

            var existing = phone.Contacts.FirstOrDefault(c => c != null && string.Equals(c.Name, TransferContactName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                _transferContact = existing;
                _contactAdded = true;
                return;
            }

            var contact = new iFruitContact(TransferContactName)
            {
                Active = true,
                DialTimeout = CALL_DURATION_MS,
                Bold = false,
                Icon = ContactIcon.AllCharacters,
            };

            contact.Answered += OnTransferContactAnswered;
            phone.Contacts.Add(contact);
            _transferContact = contact;
            _contactAdded = true;
        }
        catch { }
    }

    private void OnTransferContactAnswered(iFruitContact sender)
    {
        try
        {
            _callPending = true;
            _callDueGameTime = Game.GameTime + CALL_DURATION_MS;
        }
        catch { }
    }

    private void SyncStateForCurrentCharacterAndDay()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            int dayKey = GetCurrentGameDayKey();

            // Giữ nguyên logic ngày và kiểm tra điều kiện đầu vào
            if (ownerHash == 0 || dayKey == -1)
                return;

            // Chỉ sync tiền khi đổi nhân vật (ownerHash khác với _lastCashSyncOwnerHash)
            if (ownerHash != _lastCashSyncOwnerHash)
            {
                SyncCurrentCharacterCashFromCache();
            }

            if (_currentStateOwnerHash != ownerHash)
            {
                _currentStateOwnerHash = ownerHash;
                _stateLoaded = false;
            }

            if (_currentDayKey != dayKey)
            {
                _currentDayKey = dayKey;
                _stateLoaded = false;
            }

            if (!_stateLoaded)
                LoadOrResetStateForCurrentCharacter();
        }
        catch
        {
            // Xem xét việc log lỗi ở đây nếu cần thiết để dễ debug sau này
        }
    }

    private void UpdateTransferContactActiveState()
    {
        try
        {
            if (_transferContact == null)
                return;

            bool canUse = IsValidCharacter(GetCurrentCharacterHash());
            if (!canUse)
            {
                _transferContact.Active = false;
                return;
            }

            if (!_stateLoaded)
            {
                _transferContact.Active = true;
                return;
            }

            _transferContact.Active = GetRemainingTransferLimit() > 0;
        }
        catch { }
    }

    private void LoadOrResetStateForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            int dayKey = GetCurrentGameDayKey();
            if (ownerHash == 0 || dayKey == -1)
                return;

            Directory.CreateDirectory(StateFileRoot);
            string path = GetStateFilePath(ownerHash);

            TransferState loaded = null;
            if (File.Exists(path))
                loaded = LoadStateFromFile(path);

            long currentCash = GetCurrentCharacterCash();
            long percent = GetTransferPercentForCash(currentCash);
            long maxAllowed = ComputeTransferCap(currentCash, percent);

            if (loaded == null || loaded.DayKey != dayKey)
            {
                _state = new TransferState
                {
                    DayKey = dayKey,
                    BaselineCash = Math.Max(0L, currentCash),
                    MaxTransferAllowed = Math.Max(0L, maxAllowed),
                    TransferredToday = 0L,
                    RecipientHash = GetDefaultRecipientHash(ownerHash)
                };
                SaveStateForCurrentCharacter();
                _stateLoaded = true;

                // Đồng bộ dữ liệu ngay sau khi tạo state mới và đánh dấu load thành công
                SyncCurrentCharacterCashFromCache();
                return;
            }

            // Same day, reuse saved state but correct any invalid fields.
            _state = loaded;
            if (_state.DayKey != dayKey)
                _state.DayKey = dayKey;

            if (_state.BaselineCash < 0)
                _state.BaselineCash = 0;
            if (_state.MaxTransferAllowed < 0)
                _state.MaxTransferAllowed = 0;
            if (_state.TransferredToday < 0)
                _state.TransferredToday = 0;

            // If the saved recipient is not valid for the current character, pick a new default.
            if (!IsRecipientValidForCurrentCharacter(_state.RecipientHash, ownerHash))
                _state.RecipientHash = GetDefaultRecipientHash(ownerHash);

            // Guard against corrupted saves where transfer exceeded cap.
            if (_state.TransferredToday > _state.MaxTransferAllowed)
                _state.TransferredToday = _state.MaxTransferAllowed;

            _stateLoaded = true;

            // Đồng bộ dữ liệu khi tải thành công state cũ từ file
            SyncCurrentCharacterCashFromCache();
        }
        catch
        {
            _state = new TransferState
            {
                DayKey = GetCurrentGameDayKey(),
                BaselineCash = Math.Max(0L, GetCurrentCharacterCash()),
                MaxTransferAllowed = Math.Max(0L, ComputeTransferCap(GetCurrentCharacterCash(), GetTransferPercentForCash(GetCurrentCharacterCash()))),
                TransferredToday = 0L,
                RecipientHash = GetDefaultRecipientHash(GetCurrentCharacterHash())
            };
            _stateLoaded = true;

            // Đảm bảo đồng bộ cache kể cả khi rơi vào trường hợp lỗi (Exception)
            SyncCurrentCharacterCashFromCache();
        }
    }

    private static string GetStateFilePath(int ownerHash)
    {
        return Path.Combine(StateFileRoot, $"transfer_state_{ownerHash}.dat");
    }

    private TransferState LoadStateFromFile(string path)
    {
        try
        {
            var state = new TransferState();
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                int idx = raw.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = raw.Substring(0, idx).Trim().ToLowerInvariant();
                string val = raw.Substring(idx + 1).Trim();

                switch (key)
                {
                    case "daykey":
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dayKey))
                            state.DayKey = dayKey;
                        break;
                    case "baselinecash":
                        if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long baseline))
                            state.BaselineCash = Math.Max(0L, baseline);
                        break;
                    case "maxtransferallowed":
                        if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long maxAllowed))
                            state.MaxTransferAllowed = Math.Max(0L, maxAllowed);
                        break;
                    case "transferredtoday":
                        if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long transferred))
                            state.TransferredToday = Math.Max(0L, transferred);
                        break;
                    case "recipienthash":
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int recipientHash))
                            state.RecipientHash = recipientHash;
                        break;
                }
            }

            return state;
        }
        catch
        {
            return null;
        }
    }

    private void SaveStateForCurrentCharacter()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            Directory.CreateDirectory(StateFileRoot);
            string path = GetStateFilePath(ownerHash);

            var sb = new StringBuilder();
            sb.AppendLine("version=1");
            sb.AppendLine("dayKey=" + _state.DayKey.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("baselineCash=" + _state.BaselineCash.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("maxTransferAllowed=" + _state.MaxTransferAllowed.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("transferredToday=" + _state.TransferredToday.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("recipientHash=" + _state.RecipientHash.ToString(CultureInfo.InvariantCulture));

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    private static int GetCurrentGameDayKey()
    {
        try
        {
            int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);
            int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
            int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);

            if (month < 1 || month > 12)
                month += 1;
            if (year < 1)
                year = 1;
            if (month < 1)
                month = 1;
            if (month > 12)
                month = 12;

            int maxDay = DateTime.DaysInMonth(year, month);
            if (day < 1)
                day = 1;
            if (day > maxDay)
                day = maxDay;

            return (year * 10000) + (month * 100) + day;
        }
        catch
        {
            return -1;
        }
    }

    private static bool IsValidCharacter(int hash)
    {
        return hash == FRANKLIN_HASH || hash == MICHAEL_HASH || hash == TREVOR_HASH;
    }

    private int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists())
                return 0;

            int hash = p.Model.Hash;
            return IsValidCharacter(hash) ? hash : 0;
        }
        catch
        {
            return 0;
        }
    }

    private string GetCurrentCharacterName()
    {
        try
        {
            int hash = GetCurrentCharacterHash();
            if (hash == FRANKLIN_HASH) return L("Transfer_Character_Franklin", "Franklin Clinton");
            if (hash == MICHAEL_HASH) return L("Transfer_Character_Michael", "Michael De Santa");
            if (hash == TREVOR_HASH) return L("Transfer_Character_Trevor", "Trevor Philips");
        }
        catch { }

        return L("Transfer_Character_NA", "N/A");
    }

    private static string GetCharacterNameByHash(int hash)
    {
        if (hash == FRANKLIN_HASH) return "Franklin Clinton";
        if (hash == MICHAEL_HASH) return "Michael De Santa";
        if (hash == TREVOR_HASH) return "Trevor Philips";
        return "N/A";
    }

    private static int GetStoryCashStatHashName(int characterHash)
    {
        if (characterHash == MICHAEL_HASH) return Game.GenerateHash("SP0_TOTAL_CASH");
        if (characterHash == FRANKLIN_HASH) return Game.GenerateHash("SP1_TOTAL_CASH");
        if (characterHash == TREVOR_HASH) return Game.GenerateHash("SP2_TOTAL_CASH");
        return Game.GenerateHash("SP1_TOTAL_CASH");
    }

    private long GetStoryCashByCharacterHash(int characterHash)
    {
        try
        {
            if (!IsValidCharacter(characterHash))
                return 0L;

            int statHash = GetStoryCashStatHashName(characterHash);
            using (var outArg = new OutputArgument())
            {
                Function.Call(Hash.STAT_GET_INT, statHash, outArg, -1);
                int value = outArg.GetResult<int>();
                return Math.Max(0L, (long)value);
            }
        }
        catch
        {
            return 0L;
        }
    }

    private void SetStoryCashByCharacterHash(int characterHash, long newCash)
    {
        try
        {
            if (!IsValidCharacter(characterHash))
                return;

            long safeCash = Math.Max(0L, newCash);
            int cashAsInt = safeCash > int.MaxValue ? int.MaxValue : (int)safeCash;
            int statHash = GetStoryCashStatHashName(characterHash);
            Function.Call(Hash.STAT_SET_INT, statHash, cashAsInt, true);
        }
        catch { }
    }

    private long GetCurrentCharacterCash()
    {
        try
        {
            int charHash = GetCurrentCharacterHash();
            if (charHash == 0)
                return 0L;

            long cash = GetStoryCashByCharacterHash(charHash);
            if (cash <= 0)
            {
                try { cash = Math.Max(0L, (long)Game.Player.Money); } catch { }
            }

            return Math.Max(0L, cash);
        }
        catch
        {
            return 0L;
        }
    }

    private static long GetTransferPercentForCash(long cash)
    {
        if (cash <= 1000000L) return 30L;
        if (cash <= 1500000L) return 27L;
        if (cash <= 3000000L) return 24L;
        if (cash <= 6000000L) return 21L;
        if (cash <= 12000000L) return 18L;
        if (cash <= 24000000L) return 15L;
        if (cash <= 50000000L) return 12L;
        if (cash <= 150000000L) return 9L;
        if (cash <= 300000000L) return 6L;
        return 3L;
    }

    private static long ComputeTransferCap(long cash, long percent)
    {
        if (cash <= 0 || percent <= 0)
            return 0L;

        return (long)Math.Floor((decimal)cash * (decimal)percent / 100m);
    }

    private long GetDailyCap()
    {
        try
        {
            if (!_stateLoaded)
                LoadOrResetStateForCurrentCharacter();

            return Math.Max(0L, _state.MaxTransferAllowed);
        }
        catch
        {
            return 0L;
        }
    }

    private long GetRemainingTransferLimit()
    {
        try
        {
            if (!_stateLoaded)
                LoadOrResetStateForCurrentCharacter();

            long remaining = _state.MaxTransferAllowed - _state.TransferredToday;
            if (remaining < 0)
                remaining = 0;
            return remaining;
        }
        catch
        {
            return 0L;
        }
    }

    private static bool IsRecipientValidForCurrentCharacter(int recipientHash, int currentHash)
    {
        if (!IsValidCharacter(currentHash))
            return false;

        if (!IsValidCharacter(recipientHash))
            return false;

        return recipientHash != currentHash;
    }

    private static int GetDefaultRecipientHash(int currentHash)
    {
        if (currentHash == FRANKLIN_HASH) return TREVOR_HASH;
        if (currentHash == MICHAEL_HASH) return FRANKLIN_HASH;
        if (currentHash == TREVOR_HASH) return FRANKLIN_HASH;
        return 0;
    }

    private List<int> GetRecipientChoicesForCurrentCharacter()
    {
        var currentHash = GetCurrentCharacterHash();
        if (!IsValidCharacter(currentHash))
            return new List<int>();

        return new List<int>
        {
            currentHash == FRANKLIN_HASH ? MICHAEL_HASH : FRANKLIN_HASH,
            currentHash == TREVOR_HASH ? MICHAEL_HASH : TREVOR_HASH
        }.Distinct().ToList();
    }

    private int GetSelectedRecipientHash()
    {
        try
        {
            var choices = GetRecipientChoicesForCurrentCharacter();
            if (choices.Count == 0)
                return 0;

            if (choices.Contains(_state.RecipientHash) && _state.RecipientHash != GetCurrentCharacterHash())
                return _state.RecipientHash;

            return choices[0];
        }
        catch
        {
            return 0;
        }
    }

    private void CycleRecipient(int direction)
    {
        try
        {
            if (_focusedTarget != FocusTarget.Recipient)
                return;

            var choices = GetRecipientChoicesForCurrentCharacter();
            if (choices.Count == 0)
                return;

            int current = GetSelectedRecipientHash();
            int index = choices.IndexOf(current);
            if (index < 0)
                index = 0;

            index += direction;
            while (index < 0) index += choices.Count;
            index %= choices.Count;

            _state.RecipientHash = choices[index];
            SaveStateForCurrentCharacter();
            BuildTransferMenu();
            PlayFrontendSound("NAV_UP_DOWN", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch { }
    }

    private void HandleRecipientCycling()
    {
        try
        {
            if (_mainMenu == null || !_mainMenu.Visible)
                return;

            bool left = SafeDisabledControlJustPressed(GTA.Control.FrontendLeft) || SafeDisabledControlJustPressed(GTA.Control.CursorScrollUp);
            bool right = SafeDisabledControlJustPressed(GTA.Control.FrontendRight) || SafeDisabledControlJustPressed(GTA.Control.CursorScrollDown);

            if (left)
                CycleRecipient(-1);
            else if (right)
                CycleRecipient(1);
        }
        catch { }
    }

    private bool SafeDisabledControlJustPressed(GTA.Control control)
    {
        try
        {
            return Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)control);
        }
        catch
        {
            return false;
        }
    }

    private void ConfigureKeyboardOnlyMenu(NativeMenu menu)
    {
        if (menu == null) return;

        try
        {
            menu.MouseBehavior = MenuMouseBehavior.Disabled;
            menu.ResetCursorWhenOpened = false;
            menu.CloseOnInvalidClick = false;
            menu.RotateCamera = true;
        }
        catch { }
    }

    private void EnsureMainMenuCreated()
    {
        try
        {
            if (_mainMenuReady)
                return;

            _mainMenu = new NativeMenu(
                L("Transfer_MenuTitle", "Transfer"),
                L("Transfer_MenuSubtitle", "CHI TIẾT CHUYỂN TIỀN"));

            _uiPool.Add(_mainMenu);
            ConfigureKeyboardOnlyMenu(_mainMenu);
            _mainMenu.Visible = false;
            _mainMenuReady = true;
        }
        catch { }
    }

    private void OpenTransferMenu()
    {
        try
        {
            if (!IsValidCharacter(GetCurrentCharacterHash()))
            {
                GTA.UI.Screen.ShowSubtitle(L("Transfer_InvalidCharacter", "~r~Nhân vật hiện tại không hỗ trợ chuyển tiền."), 2500);
                return;
            }

            SyncStateForCurrentCharacterAndDay();
            EnsureMainMenuCreated();
            BuildTransferMenu();
            ConfigureKeyboardOnlyMenu(_mainMenu);

            _mainMenu.Visible = true;
            _menuOpen = true;
            Interval = MENU_PROCESS_INTERVAL_MS;
            _uiPool.Process();
        }
        catch { }
    }

    private void CloseTransferMenu()
    {
        try
        {
            if (_mainMenu != null)
                _mainMenu.Visible = false;
        }
        catch { }

        _focusedTarget = FocusTarget.None;
        _requestedTransferAmount = 0L;
        _requestedTransferValid = false;
        _menuOpen = false;
        Interval = MENU_IDLE_INTERVAL_MS;
    }

    private void BuildTransferMenu()
    {
        try
        {
            if (_mainMenu == null)
                return;

            _mainMenu.Clear();

            int currentHash = GetCurrentCharacterHash();
            string senderName = GetCurrentCharacterName();
            int recipientHash = GetSelectedRecipientHash();
            string recipientName = GetCharacterNameByHash(recipientHash);

            long currentCash = GetCurrentCharacterCash();
            long limitPercent = GetTransferPercentForCash(_state.BaselineCash > 0 ? _state.BaselineCash : currentCash);
            long dailyCap = GetDailyCap();
            long remaining = GetRemainingTransferLimit();

            var sender = new NativeItem(LT("Transfer_Sender", "Người chuyển: {name}", "{name}", senderName));
            _mainMenu.Add(sender);

            _recipientItem = new NativeItem(L("Transfer_Recipient", "Người thụ hưởng"));
            _recipientItem.AltTitle = "\u2190 " + recipientName + " \u2192";

            _recipientItem.Description = L("Transfer_RecipientHint", "Cho phép đổi người thụ hưởng.");
            _recipientItem.Selected += (s, e) =>
            {
                _focusedTarget = FocusTarget.Recipient;
                try { SpawnRecipientPrompt(); } catch { }
            };
            {
                _focusedTarget = FocusTarget.Recipient;
                try { SpawnRecipientPrompt(); } catch { }
            };
            _recipientItem.Activated += (s, e) =>
            {
                _focusedTarget = FocusTarget.Recipient;
            };
            _mainMenu.Add(_recipientItem);

            var cashItem = new NativeItem(LT("Transfer_CurrentCash", "Số tiền hiện tại: ${cash}", "{cash}", currentCash.ToString("N0", CultureInfo.InvariantCulture)));
            _mainMenu.Add(cashItem);

            var maxItem = new NativeItem(LT("Transfer_MaxTransfer", "Số tiền chuyển tối đa: ${max}", "{max}", remaining.ToString("N0", CultureInfo.InvariantCulture)));
            maxItem.Description = LT(
                "Transfer_DailyCapDesc",
                "Hạn mức hôm nay: ~HUD_COLOUR_CONTROLLER_FRANKLIN~${cap}~s~. Đã chuyển: ~HUD_COLOUR_PINKLIGHT~${sent}~s~.",
                "{cap}", dailyCap.ToString("N0", CultureInfo.InvariantCulture),
                "{sent}", _state.TransferredToday.ToString("N0", CultureInfo.InvariantCulture));
            _mainMenu.Add(maxItem);

            _amountItem = new NativeItem(LT(
                "Transfer_AmountLine",
                "Số tiền cần chuyển: {amount}",
                "{amount}", _requestedTransferValid ? "$" + _requestedTransferAmount.ToString("N0", CultureInfo.InvariantCulture) : "N/A"));
            _amountItem.Description = L("Transfer_AmountHint", "Nhập số tiền cần chuyển.");
            _amountItem.Selected += (s, e) =>
            {
                _focusedTarget = FocusTarget.Amount;
            };
            _amountItem.Activated += (s, e) =>
            {
                _focusedTarget = FocusTarget.Amount;
                PromptTransferAmount();
            };
            _mainMenu.Add(_amountItem);

            _confirmItem = new NativeItem(L("Transfer_Confirm", "Xác nhận chuyển tiền"));
            _confirmItem.Description = L("Transfer_ConfirmHint", "Nhấn Enter để thực hiện chuyển tiền.");
            _confirmItem.Selected += (s, e) => { _focusedTarget = FocusTarget.Confirm; };
            _confirmItem.Activated += (s, e) => ConfirmTransfer();
            _mainMenu.Add(_confirmItem);

            _cancelItem = new NativeItem(L("Transfer_Cancel", "Hủy giao dịch này"));
            _cancelItem.Selected += (s, e) => { _focusedTarget = FocusTarget.Cancel; };
            _cancelItem.Activated += (s, e) => CloseTransferMenu();
            _mainMenu.Add(_cancelItem);

            _mainMenuVisibleFix();
        }
        catch { }
    }

    private void _mainMenuVisibleFix()
    {
        try
        {
            if (_mainMenu != null)
                _mainMenu.Visible = true;
        }
        catch { }
    }

    private void SpawnRecipientPrompt()
    {
        // no-op: recipient changes via Left/Right in Tick
    }

    private void PromptTransferAmount()
    {
        try
        {
            long currentMax = GetRemainingTransferLimit();
            if (currentMax <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(L("Transfer_NoLimit", "Bạn đã ~r~đạt hạn mức~s~ chuyển tiền trong ngày."), 2500);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            string currentText = _requestedTransferValid ? _requestedTransferAmount.ToString(CultureInfo.InvariantCulture) : "";
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", currentText, "", "", "", 12);

            while (true)
            {
                int state = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);

                if (state == 0)
                {
                    Script.Wait(0);
                    continue;
                }

                if (state == 2)
                {
                    return;
                }

                string result = Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT);
                long amount = ParseMoneyString(result);

                if (amount <= 0)
                {
                    _requestedTransferAmount = 0L;
                    _requestedTransferValid = false;
                    BuildTransferMenu();
                    GTA.UI.Screen.ShowSubtitle(L("Transfer_InvalidAmount", "~r~Số tiền nhập không hợp lệ."), 2500);
                    PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                    return;
                }

                _requestedTransferAmount = amount;
                _requestedTransferValid = true;
                BuildTransferMenu();
                PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }
        }
        catch
        {
            GTA.UI.Screen.ShowSubtitle(L("Transfer_AmountFailed", "~r~Không thể nhập số tiền."), 2500);
        }
    }

    private static long ParseMoneyString(string raw)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(raw))
                return 0L;

            string digits = new string(raw.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(digits))
                return 0L;

            if (!long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
                return 0L;

            return Math.Max(0L, amount);
        }
        catch
        {
            return 0L;
        }
    }

    private void ConfirmTransfer()
    {
        try
        {
            if (!IsValidCharacter(GetCurrentCharacterHash()))
            {
                GTA.UI.Screen.ShowSubtitle(L("Transfer_InvalidCharacter", "~r~Nhân vật hiện tại không hỗ trợ chuyển tiền."), 2500);
                return;
            }

            SyncStateForCurrentCharacterAndDay();

            int currentHash = GetCurrentCharacterHash();
            int recipientHash = GetSelectedRecipientHash();
            if (!IsRecipientValidForCurrentCharacter(recipientHash, currentHash))
            {
                GTA.UI.Screen.ShowSubtitle(L("Transfer_InvalidRecipient", "~r~Người thụ hưởng không hợp lệ."), 2500);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            long currentCash = GetCurrentCharacterCash();
            long remaining = GetRemainingTransferLimit();
            if (remaining <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(L("Transfer_NoLimit", "~r~Bạn đã đạt hạn mức chuyển tiền trong ngày."), 2500);
                _transferContact.Active = false;
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            if (!_requestedTransferValid || _requestedTransferAmount <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(L("Transfer_NoAmount", "~y~Bạn chưa nhập số tiền cần chuyển."), 2500);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            long finalAmount = Math.Min(_requestedTransferAmount, remaining);
            finalAmount = Math.Min(finalAmount, currentCash);

            if (finalAmount <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(L("Transfer_NoFunds", "~r~Không đủ tiền để chuyển."), 2500);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            if (_requestedTransferAmount > remaining)
            {
                GTA.UI.Screen.ShowSubtitle(LT(
                    "Transfer_ClampedToRemaining",
                    "Số tiền vượt hạn mức còn lại, hệ thống sẽ chỉ chuyển ~HUD_COLOUR_PLATFORM_GREEN~${amount}~s~.",
                    "{amount}", finalAmount.ToString("N0", CultureInfo.InvariantCulture)), 3000);
            }

            if (currentCash < finalAmount)
            {
                GTA.UI.Screen.ShowSubtitle(L("Transfer_NotEnoughCash", "Nhân vật hiện tại ~r~không đủ tiền~s~."), 2500);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            // --- BẮT ĐẦU KHỐI THAY THẾ ---
            long senderAfter = Math.Max(0L, currentCash - finalAmount);

            long recipientCash = GetCachedCashForCharacter(recipientHash);
            if (recipientCash <= 0L)
                recipientCash = GetStoryCashByCharacterHash(recipientHash);

            long recipientAfter = ClampMoney(recipientCash + finalAmount);

            // cập nhật cả 2 bên
            SetCachedCashForCharacter(currentHash, senderAfter);
            SetCachedCashForCharacter(recipientHash, recipientAfter);
            // --- KẾT THÚC KHỐI THAY THẾ ---

            // Update daily quota.
            _state.TransferredToday = Math.Min(_state.MaxTransferAllowed, _state.TransferredToday + finalAmount);
            _state.BaselineCash = Math.Max(_state.BaselineCash, currentCash);
            _state.RecipientHash = recipientHash;
            SaveStateForCurrentCharacter();

            if (GetRemainingTransferLimit() <= 0)
            {
                if (_transferContact != null)
                    _transferContact.Active = false;
            }

            PlayFrontendSound("Text_Arrive_Tone", "Phone_SoundSet_Default");

            ScheduleTransferNotification(currentHash, recipientHash);

            CloseTransferMenu();
        }
        catch (Exception ex)
        {
            GTA.UI.Screen.ShowSubtitle(LT("Transfer_Failed", "~r~Chuyển tiền thất bại: {msg}", "{msg}", ex.Message), 3500);
            PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
    }

    private sealed class TransferNotificationData
    {
        public NotificationIcon Icon;
        public string Title;
        public string Subject;
        public string Message;
    }

    private TransferNotificationData GetTransferNotificationData(int senderHash, int recipientHash)
    {
        try
        {
            // Franklin -> Michael
            if (senderHash == FRANKLIN_HASH && recipientHash == MICHAEL_HASH)
            {
                return new TransferNotificationData
                {
                    Icon = NotificationIcon.Michael,
                    Title = L("Transfer_Name_Michael", "Michael De Santa"),
                    Subject = L("Transfer_MessageSubject", "Tin nhắn"),
                    Message = L(
                        "Transfer_Message_FranklinToMichael",
                        "Anh nhận được tiền từ chú em rồi nhé. Cảm ơn chú em đã giúp anh! Nếu được thì anh sẽ trả lại hoặc giúp lại chú em sau nhá!")
                };
            }

            // Franklin -> Trevor
            if (senderHash == FRANKLIN_HASH && recipientHash == TREVOR_HASH)
            {
                return new TransferNotificationData
                {
                    Icon = NotificationIcon.Trevor,
                    Title = L("Transfer_Name_Trevor", "Trevor Philips"),
                    Subject = L("Transfer_MessageSubject", "Tin nhắn"),
                    Message = L(
                        "Transfer_Message_FranklinToTrevor",
                        "Anh thấy tiền của chú chuyển qua cho anh rồi. Cảm ơn nhiều nhé Franklin!")
                };
            }

            // Michael -> Franklin
            if (senderHash == MICHAEL_HASH && recipientHash == FRANKLIN_HASH)
            {
                return new TransferNotificationData
                {
                    Icon = NotificationIcon.Franklin,
                    Title = L("Transfer_Name_Franklin", "Franklin Clinton"),
                    Subject = L("Transfer_MessageSubject", "Tin nhắn"),
                    Message = L(
                        "Transfer_Message_MichaelToFranklin",
                        "Tiền ông anh chuyển qua, tui thấy thông báo ngân hàng rồi. Cảm ơn lòng tốt của ông anh nhé!")
                };
            }

            // Michael -> Trevor
            if (senderHash == MICHAEL_HASH && recipientHash == TREVOR_HASH)
            {
                return new TransferNotificationData
                {
                    Icon = NotificationIcon.Trevor,
                    Title = L("Transfer_Name_Trevor", "Trevor Philips"),
                    Subject = L("Transfer_MessageSubject", "Tin nhắn"),
                    Message = L(
                        "Transfer_Message_MichaelToTrevor",
                        "Đúng là bạn thân nhất của tao, biết giúp tao như vậy mới đúng là bạn thân của tao chứ!!!! Hahahahaha")
                };
            }

            // Trevor -> Franklin
            if (senderHash == TREVOR_HASH && recipientHash == FRANKLIN_HASH)
            {
                return new TransferNotificationData
                {
                    Icon = NotificationIcon.Franklin,
                    Title = L("Transfer_Name_Franklin", "Franklin Clinton"),
                    Subject = L("Transfer_MessageSubject", "Tin nhắn"),
                    Message = L(
                        "Transfer_Message_TrevorToFranklin",
                        "Tôi nhận được tiền từ tài khoản của ông anh rồi. Cảm ơn đã gửi nha Trev!!!")
                };
            }

            // Trevor -> Michael
            if (senderHash == TREVOR_HASH && recipientHash == MICHAEL_HASH)
            {
                return new TransferNotificationData
                {
                    Icon = NotificationIcon.Michael,
                    Title = L("Transfer_Name_Michael", "Michael De Santa"),
                    Subject = L("Transfer_MessageSubject", "Tin nhắn"),
                    Message = L(
                        "Transfer_Message_TrevorToMichael",
                        "Thực ra tao không muốn mượn tiền gì mày đâu, nhưng tao thì ngại mượn thằng ku Frank nên mới nhờ mày một tí. Cảm ơn vì số tiền đã gửi qua nhé! Tao sẽ trả lại sau!!!")
                };
            }
        }
        catch { }

        return null;
    }

    private void ScheduleTransferNotification(int senderHash, int recipientHash)
    {
        try
        {
            var data = GetTransferNotificationData(senderHash, recipientHash);
            if (data == null)
                return;

            _pendingTransferNotification = true;
            _pendingTransferNotificationDue = Game.GameTime + TRANSFER_NOTIFICATION_DELAY_MS;
            _pendingTransferSenderHash = senderHash;
            _pendingTransferRecipientHash = recipientHash;
            _pendingTransferIcon = data.Icon;
            _pendingTransferTitle = data.Title;
            _pendingTransferSubject = data.Subject;
            _pendingTransferMessage = data.Message;
        }
        catch { }
    }

    private void ProcessPendingTransferNotification()
    {
        try
        {
            if (!_pendingTransferNotification)
                return;

            if (Game.GameTime < _pendingTransferNotificationDue)
                return;

            _pendingTransferNotification = false;

            try
            {
                PlayFrontendSound("Text_Arrive_Tone", "Phone_SoundSet_Default");

                Notification.Show(
                    _pendingTransferIcon,
                    _pendingTransferTitle,
                    _pendingTransferSubject,
                    _pendingTransferMessage);
            }
            catch
            {
                try
                {
                    PlayFrontendSound("Text_Arrive_Tone", "Phone_SoundSet_Default");
                    Notification.Show(_pendingTransferTitle + ": " + _pendingTransferMessage);
                }
                catch
                {
                    try
                    {
                        PlayFrontendSound("Text_Arrive_Tone", "Phone_SoundSet_Default");
                        GTA.UI.Screen.ShowSubtitle(_pendingTransferTitle + ": " + _pendingTransferMessage, 5000);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static void PlayFrontendSound(string soundName, string soundSet)
    {
        try
        {
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, soundName, soundSet, true);
        }
        catch { }
    }
}