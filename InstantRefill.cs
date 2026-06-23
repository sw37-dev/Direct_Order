using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;

public partial class InstantRefill : Script
{
    private static string L(string key, string fallback = "")
    => Language.Get(key, fallback);

    private static string ContactName(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    private static string[] LMany(string key, params string[] fallback)
    {
        string raw = Language.Get(key, fallback != null ? string.Join("|", fallback) : "");
        if (string.IsNullOrWhiteSpace(raw))
            return fallback ?? new string[0];

        string[] parts = raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return fallback ?? new string[0];

        for (int i = 0; i < parts.Length; i++)
            parts[i] = parts[i].Trim();

        return parts;
    }

    private NativeMenu _luiStoreMenu;

    private NativeMenu _luiWeaponAmmoMenu;
    private NativeMenu _luiWeaponAmmoDetailMenu;

    private NativeMenu _luiBodyguardMenu;
    private NativeMenu _luiBodyguardDetailMenu;

    private sealed class BodyguardSpec
    {
        public string Name;
        public uint ModelHash;
        public bool UsePlayerClone;
    }

    private sealed class BodyguardOffer
    {
        public bool Valid;
        public string Label = string.Empty;
        public uint ModelHash;
        public bool UsePlayerClone;
        public int Price;
    }

    private readonly List<BodyguardSpec> _bodyguardSpecs = new List<BodyguardSpec>();
    private readonly List<BodyguardOffer> _bodyguardOffers = new List<BodyguardOffer>();
    private BodyguardOffer _activeBodyguardOffer = null;
    private int _bodyguardPendingCost = 0;

    private enum WeaponAmmoChoice
    {
        CurrentWeapon,
        AllWeapons
    }

    private sealed class WeaponAmmoOffer
    {
        public bool Valid;
        public bool Locked;
        public WeaponAmmoChoice Choice;
        public string Label = string.Empty;
        public uint CurrentWeaponHash;
        public int Price;
        public int TotalAmmo;
        public readonly List<KeyValuePair<uint, int>> AmmoLines = new List<KeyValuePair<uint, int>>();
    }

    private readonly WeaponAmmoOffer _ammoSingleOffer = new WeaponAmmoOffer();
    private readonly WeaponAmmoOffer _ammoAllOffer = new WeaponAmmoOffer();
    private WeaponAmmoOffer _activeAmmoOffer = null;

    private bool _ammoLocked = false;
    private int _ammoBackCancelCount = 0;

    private NativeCheckboxItem _nightVisionGlowCheckbox;
    private NativeCheckboxItem _nightVisionThermalCheckbox;
    private bool _nightVisionUiSyncLock = false;

    private enum StoreItemType
    {
        Health,
        Armor,
        Special,
        Oxygen,
        Parachute,
        NightVisionHat
    }

    // Mũ / kính nhìn đêm
    private const int HatDrawableIndex = 21;
    private const int HatTextureIndex = 0;
    private const int HatPropSlot = 0;

    private bool _nightVisionHatEnabled = false;
    private bool _nightVisionEnabled = false;
    private bool _thermalVisionEnabled = false;

    private sealed class StoreItemEntry
    {
        public StoreItemType Type;
        public string Name;
        public int MinPrice;
        public int MaxPrice;
        public bool Free;
        public int CurrentPrice;
        public NativeItem MenuItem;
    }

    private readonly List<StoreItemEntry> _storeItems = new List<StoreItemEntry>();

    // ----- RANDOM COST RANGES -----
    private int HealthMin, HealthMax;
    private int ArmorMin, ArmorMax;
    private int SpecialMin, SpecialMax;
    private int AmmoAllMin, AmmoAllMax;
    private int AmmoSingleMin, AmmoSingleMax;
    private int _gameReadyTime = -1;
    private bool _modReady = false;

    // --- SOFT DISABLE (temporary reset) ---
    private bool _softDisabled = false;
    private int _softDisabledExpiry = 0;
    private const int SoftDisableDurationMs = 10000;

    private Keys KeyAmmo;
    private Keys KeyStoreMenu = Keys.NumPad1;
    private readonly string iniPath = Path.Combine("scripts", "DirectOrder.ini");
    private readonly string customVehicleXmlPath = Path.Combine("scripts", "DirectOrder_CustomVehicles.xml");

    // ---------------- Reward accumulation ----------------
    private const long REWARD_THRESHOLD = 10000000L;
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GTA V Mods", "PersistentManager");
    private readonly string _spendFilePath = Path.Combine(DataFolder, "DirectOrder_spend.dat");
    private readonly SpendingAccumulatorStore _spendingAccumulator;

    private readonly Random _rng = new Random();

    // --- BOOST ---
    private const int BASE_HEALTH = 200;
    private const int HEALTH_BOOST_STEP = 60;
    private const int HEALTH_BOOST_MAX_EXTRA = 300;

    private const int BASE_ARMOR = 100;
    private const int ARMOR_BOOST_STEP = 80;
    private const int ARMOR_EXTRA_CAP = 400;
    private static readonly int ARMOR_MAX_TOTAL = BASE_ARMOR + ARMOR_EXTRA_CAP;

    // ---------------- Pending confirmation state ----------------
    private enum PendingType
    {
        None,
        Health,
        Armor,
        Special,
        AmmoSingle,
        AmmoAll,
        WeaponOffer,
        VehicleSelection,
        VehicleOffer,
        RewardRedeem,      // redeem points -> vehicle / reward
        HireBodyguard,     // <-- NEW: hire bodyguard pending
        LockedStats,
        LockedWeapon,
        LockedVehicle
    }

    private PendingType _pendingType = PendingType.None;
    private int _peCo = 0;   // _pendingCost
    private const int PendingDurationMs = 15000;

    // --- Throttle / limits for ammo processing and weapon enumeration ---
    private const int MAX_AMMO_WEAPONS_QUEUE = 64;
    private const int AMMO_PROCESS_PER_TICK = 3;
    private const int AMMO_PROCESS_INTERVAL_MS = 300;
    private int _lastSpecialUpdateTime = 0;

    private uint _pendingWeaponHash = 0;
    private int _peAA = 0;   // _pendingAmmoAmount

    // Accessory pending info (weapon)
    private bool _pendingAccessoryAvailable = false;
    private int _pendingAccessoryPercent = 0;
    private int _pendAcCost = 0;   // _pendingAccessoryCost

    private Queue<uint> _pendingWeaponHashes = new Queue<uint>();

    private int _cancelFeeBase = 50;
    private int _expireFeeBase = 20;
    private int _currentCancelFee = 50;
    private int _currentExpireFee = 20;
    private const int CancelFeeCap = 500;
    private const int ExpireFeeCap = 500;

    private const int InteractionCooldownMs = 600;
    private int _lastInteractionGameTime = -99999;

    // --- HELP-BOX COOLDOWN (PATCH) ---
    private const int HelpBoxCooldownMs = 5000;
    private int _helpBoxCooldownExpiry = 0;

    // Kiểm tra có đang trong thời gian cooldown không
    private bool IsHelpBoxCooldownActive()
    {
        try { return Game.GameTime < _helpBoxCooldownExpiry; }
        catch { return false; }
    }

    // Thiết lập cooldown (đặt expiry)
    private void EnsureHelpBoxCooldownSet()
    {
        try { _helpBoxCooldownExpiry = Game.GameTime + HelpBoxCooldownMs; }
        catch { _helpBoxCooldownExpiry = Game.GameTime; }
    }

    // Hiển thị thông báo cooldown cho người chơi
    private void ShowHelpCooldownMessage()
    {
        try
        {
            int remainMs = Math.Max(0, _helpBoxCooldownExpiry - Game.GameTime);
            int sec = (int)Math.Ceiling(remainMs / 1000.0);
            GTA.UI.Screen.ShowSubtitle(
                string.Format(L("HelpCooldownMessage",
                                "~h~Đợi ~HUD_COLOUR_DEGEN_GREEN~{0}s~s~ trước khi mở menu."), sec),
                                3000);
        }
        catch { }
    }

    // --- Vehicle selection UI state ---
    // <<< PATCH: ICON TOKENS FOR HELP TEXT (insert near UI state constants) >>>
    private const string LFT = "~INPUT_FRONTEND_LEFT~";   // ICON_LEFT
    private const string R = "~INPUT_FRONTEND_RIGHT~";   // ICON_RIGHT
    // Use cursor scroll icon to represent mouse (scrollwheel) — renders appropriate mouse icon on PC.
    private const string M = "~INPUT_CURSOR_SCROLL_UP~";   // ICON_MOUSE_SCROLL
    private const string ES = "~INPUT_FRONTEND_CANCEL~";   // Esc
    private const string E = "~INPUT_FRONTEND_ACCEPT~";   // Enter
    private const string S = "~INPUT_SPRINT~";   // Shift

    private string _peMesTem = "";
    private string _penMesTi = "";   // _pendingMessageTitle
    private string _penMesBo = "";   // _pendingMessageBody

    private readonly string[] _luiPreviewSkipTokens;

    private enum VehicleBrowseMode
    {
        None,
        Filtered,
        Full
    }

    private readonly string _plateHintBeforeEdit;
    private readonly string _plateHintAfterEdit;

    // thresholds and fees (constants)
    private int BackCancelThreshold = 5;
    private const int UnlockFeeStats = 1500;
    private const int UnlockFeeWeapon = 10000;

    private const int VehicleSaleAutoCheckIntervalMs = 90000;

    private readonly string[] _msgSuccess;
    private readonly string[] _msgCancel;
    private readonly string[] _msgNoMoney;
    private readonly string[] _msgFail;
    private readonly string[] _msgExpireCharged;
    private readonly string[] _msgExpireNoMoney;
    private readonly string[] _msgCancelCharged;
    private readonly string[] _msgCancelNoMoney;

    private T SafeCall<T>(Func<T> fn, T fallback = default) => Helper.SafeCall(fn, fallback);
    private void SafeCall(Action act) => Helper.SafeCall(act);

    // trim / limit help-text to avoid UI overflow
    private string TruncateHelpText(string text, int maxChars = 400)
    => Helper.TruncateHelpText(text, maxChars);

    // Load accumulator from file (robust, swallows errors)
    private void LoadSpendingAccumulator()
    {
        _spendingAccumulator.LoadSpendingAccumulator();
    }

    // Đọc lại giá trị chi tiêu từ file ngay tại thời điểm cần hiển thị menu.
    private void ReloadSpendingAccumulatorFromDisk()
    {
        _spendingAccumulator.ReloadSpendingAccumulatorFromDisk();
    }

    private void SaveSpendingAccumulator()
    {
        _spendingAccumulator.SaveSpendingAccumulator();
    }

    private void AddToSpendingAccumulator(long amount)
    {
        _spendingAccumulator.AddToSpendingAccumulator(amount);
    }

    private string FormatML(long v)
    {
        return _spendingAccumulator.FormatML(v);
    }

    // <<< ADDED: helpers for back-cancel tracking and unlocking >>>
    // ------------------- REPLACE: RecordBackCancel -------------------
    private void RecordBackCancel(PendingType type)
    {
        try
        {
            switch (type)
            {
                case PendingType.Health:
                case PendingType.Armor:
                case PendingType.Special:
                    HandleBackCancel(ref _statsBackCancelCount, ref _statsLocked);
                    break;

                case PendingType.WeaponOffer:
                    HandleBackCancel(ref _weaponBackCancelCount, ref _weaponLocked);
                    break;

                case PendingType.VehicleOffer:
                    HandleBackCancel(ref _vehicleBackCancelCount, ref _vehicleLocked);
                    break;

                default:
                    // do nothing for other types
                    break;
            }
        }
        catch
        {
            /* swallow all to avoid crash in-game */
        }
    }

    private void ShowFeedMessage(string sender, string subject, string body)
    => Helper.ShowFeedMessage(sender, subject, body);

    private char GetCharFromKey(Keys k)
    => Helper.GetCharFromKey(k);

    private void HandleBackCancel(ref int counter, ref bool locked)
    => Helper.HandleBackCancel(
        ref counter,
        ref locked,
        BackCancelThreshold,
        msg => Notification.Show(msg),
        (msg, time) => GTA.UI.Screen.ShowSubtitle(msg, time),
        () => PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET"));

    private void ResetBackCountersForType(PendingType type)
    {
        try
        {
            switch (type)
            {
                case PendingType.Health:
                case PendingType.Armor:
                case PendingType.Special:
                    _statsBackCancelCount = 0;
                    break;
                case PendingType.WeaponOffer:
                    _weaponBackCancelCount = 0;
                    break;
                case PendingType.VehicleOffer:
                    _vehicleBackCancelCount = 0;
                    break;
            }
        }
        catch { }
    }

    // --- SEARCH (quick-filter) state ---
    private bool _searchActive = false;               // true while help-box awaiting single-char input
    private char _searchChar = '\0';                  // currently typed char (or '\0' if none)
    private int _searchStartGameTime = 0;             // game-time when search started
    private const int SearchTimeoutMs = 15000;        // 15 seconds timeout as requested

    // Ctrl+L purchase config (weapon)
    private int WeaponOfferDefaultMinPrice = 50000;
    private int WeaponOfferDefaultMaxPrice = 70000;
    private int WeaponOfferFirstAmmoMin = 20;
    private int WeaponOfferFirstAmmoMax = 30;

    private const int WeaponOfferDurationMs = 15000;

    // Phone contact replacement for Ctrl+L weapon offer
    private CustomiFruit _ammunationPhoneInstance = null;
    private bool _ammunationContactAdded = false;
    private const int AMMUNATION_CALL_DURATION_MS = 2000;

    // Phone contact replacement for Alt+Shift vehicle search
    private CustomiFruit _legendaryMotorSportPhoneInstance = null;
    private bool _legendaryMotorSportContactAdded = false;
    private const int LEGENDARY_MOTORSPORT_CALL_DURATION_MS = 2000;

    // Phone contact replacement for Shift+L bodyguard hire
    private CustomiFruit _eliteProtectionPhoneInstance = null;
    private bool _eliteProtectionContactAdded = false;
    private const int BODYGUARDS_CALL_DURATION_MS = 2000;

    // Phone contact replacement for Shift+Z reward redeem
    private CustomiFruit _privilegeCreditsPhoneInstance = null;
    private bool _privilegeCreditsContactAdded = false;
    private const int REWARD_REDEEM_CALL_DURATION_MS = 2000;

    public InstantRefill()
    {
        Instance = this;
        Interval = 1000;

        _luiPreviewSkipTokens = LMany("VehiclePreviewSkipTokens", "blimp", "khinh khí cầu");
        _plateHintBeforeEdit = L("PlateHintBeforeEdit",
            "Phương tiện sau khi mua sẽ mang biển số xe này, và bạn có thể dùng Enter để thay đổi biển số theo ý muốn!");
        _plateHintAfterEdit = L("PlateHintAfterEdit",
            "Đây là biển số xe mới nhất sẽ được sử dụng cho phương tiện sau khi thanh toán hoàn tất.");

        _msgSuccess = LMany("MsgSuccessList",
            "Cảm ơn bạn đã tin tưởng sốp! Sốp củm ơn ^^",
            "Hàng hiệu đã trao tay, chúc quý khách quậy nát thành phố!",
            "Đúng là đại gia Los Santos, xuống tiền nhanh như chớp!",
            "Nhớ ủng hộ tiếp nhen, biết đâu mua nhiều có quà thì sao? ^^",
            "Mua ít thế, mua thêm đi bạn iu ơi!!!",
            "Mua như thế thì chưa đủ để sử dụng đâu bạn iu, thêm nữa đi!!");
        _msgCancel = LMany("MsgCancelList",
            "~y~~h~Hẹn gặp lại nha! Bái bai~h~~s~",
            "~y~~h~Sốp đã đóng đơn, lần sau nhớ chốt nha!~h~~s~");
        _msgNoMoney = LMany("MsgNoMoneyList",
            "~w~~h~Không đủ xèn rồi ní ê! Thêm chút đỉnh đi!~h~~s~",
            "~w~~h~Nghèo thì đừng ham đồ hiệu nha mạy! Nghe chưa?~h~~s~");
        _msgFail = LMany("MsgFailList",
            "~w~~h~Sốp đang bận nên không thể tiếp khách lúc này.~h~~s~",
            "~w~~h~Sốp phá sản rồi!~h~~s~");
        _msgExpireCharged = LMany("MsgExpireChargedList",
            "~y~~h~Sốp thu nhẹ ~s~~w~${0}~s~~y~~h~ tiền công đợi chờ nha. Hẹ hẹ ^^~h~~s~",
            "~y~~h~Đợi chờ không là hạnh phúc, mà là tốn ~s~~w~${0}~s~~y~~h~ tiền phí đó nha!~h~~s~");
        _msgExpireNoMoney = LMany("MsgExpireNoMoneyList",
            "~y~~h~Sốp ghi mày vào danh sách đen với số tiền ~s~~w~${0}~s~~y~~h~ nhá!~h~~s~",
            "~y~~h~Nhìn bảnh bao mà ~s~~w~${0}~s~~y~~h~ cũng không có xiền nộp phạt.~h~~s~");
        _msgCancelCharged = LMany("MsgCancelChargedList",
            "~b~~h~Hủy kèo hả mạy? Sốp thu phí huỷ đơn ~s~~w~${0}~s~~b~~h~ nha!~h~~s~",
            "~b~~h~Đúng là phong cách đại gia, bỏ ~s~~w~${0}~s~~b~~h~ ra chỉ để... bấm nút hủy!~h~~s~");
        _msgCancelNoMoney = LMany("MsgCancelNoMoneyList",
            "~w~~h~Hủy đơn mà trong túi không có nổi ~s~~y~${0}~s~~w~~h~ trả phí nữa hả?~h~~s~",
            "~w~~h~Nghèo quá không đủ ~s~~y~${0}~s~~w~~h~ để hủy đơn luôn ơ?!~h~~s~");

        LoadSettings();
        LoadVehicleDiscountTicketState();
        InitializeSpecialWeaponList();
        InitializeVehicleList();
        InitializeLemonVehicleMenus();   // ADD THIS
        InitializeLemonWeaponMenus();
        InitializeLemonWeaponAmmoMenus();
        InitializeLemonStoreMenu();
        InitializeLemonBodyguardMenus();
        _spendingAccumulator = new SpendingAccumulatorStore(_spendFilePath);
        _spendingAccumulator.LoadSpendingAccumulator();
        _gameReadyTime = Game.GameTime + 3000;
        _mazeBankAtmSpawnReadyTime = Game.GameTime + MazeBankAtmSpawnDelayMs;
        Tick += OnTick;
        KeyDown += OnKeyDown;
    }

    public sealed class UnlockableVehicleOption
    {
        public uint Hash;
        public string Name;
        public string Class;
    }

    public List<UnlockableVehicleOption> GetDirtyOnlyVehicleOptionsForUnlockMenu()
    {
        try
        {
            return _vehicles
                .Where(v => v != null &&
                            CityBlackoutHackerState.IsDirtyOnlyVehicle(v.Hash, v.Name, v.Class))
                .Select(v => new UnlockableVehicleOption
                {
                    Hash = v.Hash,
                    Name = v.Name,
                    Class = v.Class
                })
                .ToList();
        }
        catch
        {
            return new List<UnlockableVehicleOption>();
        }
    }

    private void LoadSettings()
    {
        if (!File.Exists(iniPath))
        {
            // defaults
            AmmoToAddGun = 150;
            ExplosiveToAdd = 10;
            SingleAmmoAmountGun = 50;
            SingleExplosiveAmount = 5;

            RandomMinGun = 50; RandomMaxGun = 75;
            RandomMinExplosive = 2; RandomMaxExplosive = 5;

            HealthMin = 400; HealthMax = 800;
            ArmorMin = 950; ArmorMax = 1500;
            SpecialMin = 1250; SpecialMax = 2000;

            // K key ammo-all: 50k-150k per request
            AmmoAllMin = 50000; AmmoAllMax = 150000;
            AmmoSingleMin = 3500; AmmoSingleMax = 6000;

            _cancelFeeBase = 50; _expireFeeBase = 20;
            _currentCancelFee = _cancelFeeBase; _currentExpireFee = _expireFeeBase;

            // New defaults for added settings
            BackCancelThreshold = 5;
            _saleMultiplier = 0.3;

            // Default hotkeys
            KeyStoreMenu = Keys.NumPad1;
            KeyAmmo = Keys.NumPad2;

            return;
        }

        var cfg = ScriptSettings.Load(iniPath);

        AmmoToAddGun = cfg.GetValue("Ammo", "GunAmmo", 150);
        ExplosiveToAdd = cfg.GetValue("Ammo", "Explosives", 10);

        SingleAmmoAmountGun = cfg.GetValue("Cost", "AmmoSingleAmountGun", 50);
        SingleExplosiveAmount = cfg.GetValue("Cost", "AmmoSingleAmountExplosive", 5);

        RandomMinGun = cfg.GetValue("Random", "MinGun", 50);
        RandomMaxGun = cfg.GetValue("Random", "MaxGun", 75);
        RandomMinExplosive = cfg.GetValue("Random", "MinExplosive", 2);
        RandomMaxExplosive = cfg.GetValue("Random", "MaxExplosive", 5);

        HealthMin = cfg.GetValue("CostRange", "HealthMin", 400);
        HealthMax = cfg.GetValue("CostRange", "HealthMax", 800);

        ArmorMin = cfg.GetValue("CostRange", "ArmorMin", 950);
        ArmorMax = cfg.GetValue("CostRange", "ArmorMax", 1500);

        SpecialMin = cfg.GetValue("CostRange", "SpecialMin", 1250);
        SpecialMax = cfg.GetValue("CostRange", "SpecialMax", 2000);

        // K key (AmmoAll) default changed
        AmmoAllMin = cfg.GetValue("CostRange", "AmmoAllMin", 50000);
        AmmoAllMax = cfg.GetValue("CostRange", "AmmoAllMax", 150000);

        AmmoSingleMin = cfg.GetValue("CostRange", "AmmoSingleMin", 3500);
        AmmoSingleMax = cfg.GetValue("CostRange", "AmmoSingleMax", 6000);

        _cancelFeeBase = cfg.GetValue("Fees", "CancelFeeBase", 50);
        _expireFeeBase = cfg.GetValue("Fees", "ExpireFeeBase", 20);

        _currentCancelFee = Math.Max(0, _cancelFeeBase);
        _currentExpireFee = Math.Max(0, _expireFeeBase);

        // ----------------- New: load/validate BackCancelThreshold and sale multiplier -----------------
        try
        {
            // BackCancelThreshold: read as string then validate strictly (no decimals allowed)
            string bctRaw = cfg.GetValue("Misc", "BackCancelThreshold", "5").ToString().Trim();
            int bctParsed = 5;
            bool bctOk = int.TryParse(bctRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out bctParsed);
            if (!bctOk)
            {
                // invalid input -> safe default
                BackCancelThreshold = 5;
            }
            else
            {
                if (bctParsed < 5) BackCancelThreshold = 5;
                else if (bctParsed > 8) BackCancelThreshold = 6; // per requirement: >8 => auto set 6
                else BackCancelThreshold = bctParsed;
            }

            // Sale multiplier: allow decimal up to 4 places, allowed range 0.25 .. 0.40 inclusive
            // Read raw string so we can check decimal places
            string saleRaw = cfg.GetValue("Sale", "Multiplier", "0.3").ToString().Trim();
            // normalize comma -> dot for user convenience
            saleRaw = saleRaw.Replace(',', '.');
            double saleVal = 0.3;
            bool saleOk = false;

            // only accept if it parses and fractional digits <= 4
            if (!string.IsNullOrEmpty(saleRaw))
            {
                // count decimal digits if any
                int fracLen = 0;
                int dotIndex = saleRaw.IndexOf('.');
                if (dotIndex >= 0)
                    fracLen = saleRaw.Length - dotIndex - 1;

                // parse using invariant culture
                if (double.TryParse(saleRaw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out saleVal))
                {
                    // normalize to typical double rounding
                    if (fracLen <= 4 && saleVal >= 0.25 && saleVal <= 0.40)
                        saleOk = true;
                }
            }

            _saleMultiplier = saleOk ? saleVal : 0.3; // fallback safe default
        }
        catch
        {
            // Any unexpected parsing error => keep safe defaults
            BackCancelThreshold = 5;
            _saleMultiplier = 0.3;
        }

        // ----------------- Hotkeys -----------------
        KeyStoreMenu = ReadKeyOrDefault(cfg, "Keys", "StoreMenu", Keys.NumPad1);
        KeyAmmo = ReadKeyOrDefault(cfg, "Keys", "WeaponAmmoMenu", Keys.NumPad2);
    }

    private static Keys ReadKeyOrDefault(ScriptSettings cfg, string section, string name, Keys fallback)
    {
        try
        {
            string raw = cfg.GetValue(section, name, fallback.ToString()).ToString().Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            raw = raw.Replace("Numpad", "NumPad");

            Keys parsed;
            if (Enum.TryParse(raw, true, out parsed))
                return parsed;
        }
        catch { }

        return fallback;
    }

    // ----------------------- Tick / Help display -----------------------
    private void OnTick(object sender, EventArgs e)
    {
        if (!_modReady)
        {
            if (Game.IsLoading || Game.IsCutsceneActive) return;

            if (Game.GameTime >= _gameReadyTime)
            {
                _modReady = true;
                Interval = 1000;
            }
            return;
        }

        // Register Ammunation phone contact as soon as the iFruit phone exists.
        EnsureAmmunationContactRegistered();
        EnsureEliteProtectionContactRegistered();
        EnsurePrivilegeCreditsContactRegistered();
        EnsureLegendaryMotorsportContactRegistered();
        EnsureSmugglerContactRegistered();
        EnsureMazeBankAtmSpawned();
        ProcessIllegalMoneyWantedRisk();
        EnsureDaveyContactRegistered();
        SyncDaveyContactState();
        EnsureSteveHainesContactRegistered();
        ProcessSteveBribeWantedRestore();

        if (_illegalMoneyRedeemArmed && IsNearMazeBankAtm())
        {
            ShowMazeBankAtmHelpText();
        }

        if (_smugglerRedeemArmed && IsNearSmugglerNpc())
        {
            UpdateSmugglerPrompt();
        }

        // --- SOFT DISABLE: nếu mod đang tắt tạm, chỉ chờ tới khi hết hạn ---
        if (_softDisabled)
        {
            // nếu đã hết thời gian tắt -> bật lại và thông báo
            if (Game.GameTime >= _softDisabledExpiry)
            {
                _softDisabled = false;
                // Thông báo mod tái sử dụng
                SafeCall(() => Notification.Show(L("SoftDisableReenabled", "Mod đã được tái sử dụng")));
                // restore normal tick cadence (an toàn)
                Interval = Math.Max(100, 1000);
            }
            else
            {
                // trong lúc tắt thì không làm gì cả (bỏ qua mọi logic)
                return;
            }
        }

        UpdateAuctionHouse();

        // --- NEW: sale timer / daily reset ---
        RefreshVehicleSaleDailyLock();
        StopVehicleSaleIfExpired();
        TryAutoRollVehicleSale();

        // Periodic updates for weapon states
        if (Game.GameTime - _lastSpecialUpdateTime >= 5000)
        {
            try
            {
                UpdateSpecialWeaponUnlocks();
                UpdateAccessoryOwnership();
            }
            catch { }
            _lastSpecialUpdateTime = Game.GameTime;
        }

        // ADD INSIDE OnTick, before SEARCH HELP-BOX and pending logic
        if (_luiMenuAutoCloseEnabled && _luiMenuAutoCloseExpiry > 0 && Game.GameTime >= _luiMenuAutoCloseExpiry)
        {
            CloseLemonVehicleMenus(false);
            CloseLemonWeaponMenus(false);
        }

        if (_luiPool != null && SafeCall(() => _luiPool.AreAnyVisible, false))
        {
            Interval = 0;          // chạy riêng khi menu LemonUI đang mở
            SafeCall(() => _luiPool.Process());
            return;
        }

        // Chèn theo hướng dẫn: sau khối xử lý _luiPool.AreAnyVisible và trước search/pending
        HandlePdmShowroomLifecycle();

        // ---------- SEARCH HELP-BOX (single-char) ----------
        if (_searchActive)
        {
            // Persist per-frame help text for search with countdown
            int elapsed = Game.GameTime - _searchStartGameTime;
            int remainingMs = Math.Max(0, SearchTimeoutMs - elapsed);
            int remainingSec = (int)Math.Ceiling(remainingMs / 1000.0);

            string title = L("SearchTitle", "~b~~h~TÌM PHƯƠNG TIỆN~s~");
            string body = string.Format(L("SearchBody",
              "Nhập 1 ký tự: [{0}]\n" +
              "{1} để tìm\n" +
              "{2} để hủy"),
              (_searchChar == '\0' ? " " : _searchChar.ToString()), E, ES);

            string composed = title + "\n" + body;
            composed = TruncateHelpText(composed, 400);

            SafeCall(() =>
            {
                if (!Game.IsLoading && !Game.IsPaused)
                {
                    GTA.UI.Screen.ShowHelpTextThisFrame(composed);
                }
            });

            // Disable some controls while in search to reduce interference
            try
            {
                // existing weapon / scroll / phone / script pad disables (keep as before)
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.SelectNextWeapon, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.SelectPrevWeapon, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.NextWeapon, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.PrevWeapon, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.CursorScrollDown, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.CursorScrollUp, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.Phone, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.ScriptPadUp, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.ScriptPadDown, true);

                // --- NEW: key-function locks while searching ---
                // Prevent Pause menu on ESC (frontend pause inputs)
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.FrontendPause, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.FrontendPauseAlternate, true);

                // Prevent camera/view toggle (V) and look-behind while searching.
                // NextCamera is the general "cycle camera" input; LookBehind/VehicleLookBehind as redundancy.
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.NextCamera, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.LookBehind, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.VehicleLookBehind, true);

                // --- NEW: prevent Interaction Menu (M) and Enter/Exit vehicle (F) from firing while typing ---
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.InteractionMenu, true); // M key
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.Enter, true);           // Context / E / Enter (safer to include)
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.VehicleExit, true);    // F: enter/exit vehicle

                // (optional extra safe disables - rarely used but harmless)
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.FrontendAccept, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.FrontendCancel, true);
            }
            catch { /* swallow - never crash game */ }

            // Auto-timeout: if exceeded -> cancel help-box and restore
            if (elapsed >= SearchTimeoutMs)
            {
                _searchActive = false;
                _searchChar = '\0';
                _searchStartGameTime = 0;
                _vehicleViewList = null;
                Interval = 1000; // back to normal tick cadence
            }

            // Do not proceed further in OnTick while in search
            return;
        }

        // ---------- PENDING LOGIC ----------
        if (_pendingType != PendingType.None)
        {
            // Force 0ms interval while pending so OnTick runs every frame
            Interval = 0;

            if (!string.IsNullOrEmpty(_peMesTem))
            {
                // Giữ nguyên tiêu đề gốc, không ghép countdown vào tiêu đề nữa
                string titleOnly = _penMesTi;

                string composed;
                if (!string.IsNullOrEmpty(_penMesBo))
                    composed = titleOnly + "\n" + _penMesBo;
                else
                    composed = titleOnly;

                composed = TruncateHelpText(composed, 400);

                if (!Game.IsLoading && !Game.IsPaused)
                {
                    SafeCall(() =>
                    {
                        GTA.UI.Screen.ShowHelpTextThisFrame(composed);
                    });
                }

                if (_pendingType == PendingType.VehicleSelection)
                {
                    ProcessVehicleSelectionWheelInput();
                }
            }

            if (Game.GameTime >= _pendingExpiryGameTime)
            {
                if (_pendingType == PendingType.WeaponOffer || _pendingType == PendingType.VehicleOffer || _pendingType == PendingType.VehicleSelection)
                {
                    ClearPending(false, false);
                    _lastInteractionGameTime = Game.GameTime;
                }
                else
                {
                    ExpirePending();
                }
            }

            return;
        }

        if (_isProcessingAmmo) ProcessAmmoBatch();

        // Giữ tick 0ms bất cứ khi nào help-box / menu / pending / search / ammo đang hoạt động
        Interval = ShouldRunPerFrameTick() ? 0 : 1000;
    }

    private void InitializeLemonWeaponAmmoMenus()
    {
        try
        {
            if (_luiWeaponAmmoMenu != null)
                return;

            _luiWeaponAmmoMenu = new NativeMenu(
                L("WeaponAmmo_MenuTitle", "Weapons Ammo"),
                L("WeaponAmmo_SubTitle", "Lựa chọn kiểu nạp"));

            _luiPool.Add(_luiWeaponAmmoMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiWeaponAmmoMenu);
            _luiWeaponAmmoMenu.Visible = false;

            _luiWeaponAmmoDetailMenu = new NativeMenu(
                L("WeaponAmmo_MenuTitle", "Weapons Ammo"),
                L("WeaponAmmo_DetailTitle", "Chi tiết nạp đạn"));

            _luiPool.Add(_luiWeaponAmmoDetailMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiWeaponAmmoDetailMenu);
            _luiWeaponAmmoDetailMenu.Visible = false;
        }
        catch
        {
        }
    }

    private void RefreshWeaponAmmoOffers(Ped ped)
    {
        PrepareSingleAmmoOffer(ped, _ammoSingleOffer);
        PrepareAllAmmoOffer(ped, _ammoAllOffer);
    }

    private bool PrepareSingleAmmoOffer(Ped ped, WeaponAmmoOffer offer)
    {
        if (offer == null)
            return false;

        offer.Valid = false;
        offer.AmmoLines.Clear();
        offer.TotalAmmo = 0;
        offer.Price = 0;
        offer.Label = L("AmmoSingle_Title", "Vũ khí đang sử dụng");

        if (ped == null || !ped.Exists())
            return false;

        var weaponArg = new OutputArgument();
        Function.Call(Hash.GET_CURRENT_PED_WEAPON, ped.Handle, weaponArg, true);
        uint weaponHash = weaponArg.GetResult<uint>();

        if (weaponHash == 0 || weaponHash == (uint)WeaponHash.Unarmed)
            return false;

        bool isExplosive = IsExplosive(weaponHash);
        int baseAmount = isExplosive ? SingleExplosiveAmount : SingleAmmoAmountGun;
        int randomBonus = _rng.Next(
            isExplosive ? RandomMinExplosive : RandomMinGun,
            (isExplosive ? RandomMaxExplosive : RandomMaxGun) + 1);

        offer.Choice = WeaponAmmoChoice.CurrentWeapon;
        offer.CurrentWeaponHash = weaponHash;
        offer.Price = _rng.Next(AmmoSingleMin, AmmoSingleMax + 1);
        offer.TotalAmmo = Math.Max(1, baseAmount + randomBonus);
        offer.Locked = _ammoLocked;
        offer.Valid = true;

        return true;
    }

    private bool PrepareAllAmmoOffer(Ped ped, WeaponAmmoOffer offer)
    {
        if (offer == null)
            return false;

        offer.Valid = false;
        offer.AmmoLines.Clear();
        offer.TotalAmmo = 0;
        offer.Price = 0;
        offer.Label = L("AmmoAll_Title", "Tất cả vũ khí");

        if (ped == null || !ped.Exists())
            return false;

        var weaponHashes = new List<uint>();

        try
        {
            foreach (Weapon w in ped.Weapons)
            {
                if (w == null) continue;

                uint wh = (uint)w.Hash;
                if (wh == 0 || wh == (uint)WeaponHash.Unarmed) continue;

                if (!weaponHashes.Contains(wh))
                    weaponHashes.Add(wh);

                if (weaponHashes.Count >= MAX_AMMO_WEAPONS_QUEUE)
                    break;
            }
        }
        catch
        {
            foreach (WeaponHash wh in Enum.GetValues(typeof(WeaponHash)))
            {
                uint whu = (uint)wh;
                if (whu == 0) continue;

                try
                {
                    if (Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, ped.Handle, whu, false))
                    {
                        if (!weaponHashes.Contains(whu))
                            weaponHashes.Add(whu);

                        if (weaponHashes.Count >= MAX_AMMO_WEAPONS_QUEUE)
                            break;
                    }
                }
                catch { }
            }
        }

        if (weaponHashes.Count == 0)
            return false;

        offer.Choice = WeaponAmmoChoice.AllWeapons;
        offer.Price = _rng.Next(AmmoAllMin, AmmoAllMax + 1);
        offer.Locked = _ammoLocked;

        int total = 0;
        foreach (uint wh in weaponHashes)
        {
            bool isExplosive = IsExplosive(wh);
            int baseAmount = isExplosive ? ExplosiveToAdd : AmmoToAddGun;
            int randomBonus = _rng.Next(
                isExplosive ? RandomMinExplosive : RandomMinGun,
                (isExplosive ? RandomMaxExplosive : RandomMaxGun) + 1);

            int add = Math.Max(1, baseAmount + randomBonus);
            offer.AmmoLines.Add(new KeyValuePair<uint, int>(wh, add));
            total += add;
        }

        offer.TotalAmmo = total;
        offer.Valid = true;
        return true;
    }

    private void OpenWeaponAmmoMenu()
    {
        try
        {
            if (IsHelpBoxCooldownActive())
            {
                ShowHelpCooldownMessage();
                return;
            }

            if (_pendingType != PendingType.None)
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("WeaponAmmo_BusySubtitle", "~HUD_COLOUR_DEGEN_YELLOW~Hiện có cửa sổ mua hàng khác đang mở."),
                    3000);
                return;
            }

            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists() || ped.IsDead)
                return;

            RefreshWeaponAmmoOffers(ped);

            if (!_ammoSingleOffer.Valid && !_ammoAllOffer.Valid)
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("WeaponAmmo_NoAmmoSubtitle", "~HUD_COLOUR_DEGEN_RED~Không có vũ khí nào để nạp đạn."),
                    3000);
                return;
            }

            BuildWeaponAmmoMenu();
            _luiWeaponAmmoMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private void BuildWeaponAmmoMenu()
    {
        if (_luiWeaponAmmoMenu == null)
            return;

        _luiWeaponAmmoMenu.Clear();

        var currentWeaponItem = new NativeItem(L("WeaponAmmo_CurrentWeapon", "1. Vũ khí đang sử dụng"));
        UpdateWeaponAmmoRootItemVisual(
            currentWeaponItem,
            _ammoSingleOffer,
            L("WeaponAmmo_NoCurrentWeapon", "Bạn đang không cầm vũ khí nào."));

        currentWeaponItem.Activated += (s, e) =>
        {
            HandleWeaponAmmoRootActivated(_ammoSingleOffer);
        };
        _luiWeaponAmmoMenu.Add(currentWeaponItem);

        var allWeaponsItem = new NativeItem(L("WeaponAmmo_All", "2. Tất cả vũ khí"));
        UpdateWeaponAmmoRootItemVisual(
            allWeaponsItem,
            _ammoAllOffer,
            L("WeaponAmmo_NoWeapons", "Không có vũ khí nào để nạp."));

        allWeaponsItem.Activated += (s, e) =>
        {
            HandleWeaponAmmoRootActivated(_ammoAllOffer);
        };
        _luiWeaponAmmoMenu.Add(allWeaponsItem);

        var decline = new NativeItem(L("WeaponAmmo_DeclineRoot", "Quyết định từ chối mua hàng"));
        decline.Activated += (s, e) =>
        {
            CloseWeaponAmmoMenus(true);
        };
        _luiWeaponAmmoMenu.Add(decline);
    }

    private void HandleWeaponAmmoRootActivated(WeaponAmmoOffer offer)
    {
        try
        {
            if (offer == null || !offer.Valid)
                return;

            if (offer.Locked || _ammoLocked)
            {
                UnlockWeaponAmmoFeature();
                return;
            }

            if (offer.Choice == WeaponAmmoChoice.CurrentWeapon)
            {
                HandleSingleAmmoInitiate(Game.Player.Character);
            }
            else
            {
                HandleAllAmmoInitiate(Game.Player.Character);
            }
        }
        catch
        {
        }
    }

    private void UnlockWeaponAmmoFeature()
    {
        try
        {
            int cost = UnlockFeeWeapon;

            if (Game.Player.Money < cost)
            {
                Notification.Show(string.Format(_msgNoMoney[_rng.Next(_msgNoMoney.Length)], cost));
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            Game.Player.Money -= cost;
            AddToSpendingAccumulator(cost);

            _ammoLocked = false;
            _ammoBackCancelCount = 0;

            _ammoSingleOffer.Locked = false;
            _ammoAllOffer.Locked = false;

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");

            if (_luiWeaponAmmoMenu != null)
            {
                BuildWeaponAmmoMenu();
                _luiWeaponAmmoMenu.Visible = true;
            }
        }
        catch
        {
        }
    }

    private void UpdateWeaponAmmoRootItemVisual(NativeItem item, WeaponAmmoOffer offer, string invalidMessage)
    {
        if (item == null || offer == null)
            return;

        if (!offer.Valid)
        {
            item.AltTitle = "~c~N/A~s~";
            item.Description = invalidMessage;
            return;
        }

        if (offer.Locked)
        {
            item.AltTitle = "~r~~h~Locked~s~";
            item.Description = string.Format(
                L("WeaponAmmo_LockedDescription", "Tính năng này hiện đang bị khóa. Bạn cần phải trả ${0:N0} để mở khóa vật phẩm này!"),
                UnlockFeeWeapon);
            return;
        }

        item.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
        item.Description = L("WeaponAmmo_EnterHint", "Nhấn Enter để mở chi tiết.");
    }

    private void OpenWeaponAmmoDetailMenu(WeaponAmmoOffer offer)
    {
        try
        {
            if (offer == null || !offer.Valid)
                return;

            _activeAmmoOffer = offer;

            BuildWeaponAmmoDetailMenu(offer);

            if (_luiWeaponAmmoMenu != null)
                _luiWeaponAmmoMenu.Visible = false;

            _luiWeaponAmmoDetailMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private void BuildWeaponAmmoDetailMenu(WeaponAmmoOffer offer)
    {
        if (_luiWeaponAmmoDetailMenu == null || offer == null)
            return;

        _luiWeaponAmmoDetailMenu.Clear();

        var typeItem = new NativeItem(
            LT("WeaponAmmoDetail_Type", "Loại nạp: {type}", "{type}", offer.Label ?? string.Empty));
        _luiWeaponAmmoDetailMenu.Add(typeItem);

        AddReadOnlyMenuLine(
            _luiWeaponAmmoDetailMenu,
            LT("WeaponAmmoDetail_Amount", "Số lượng đi kèm: {ammo}", "{ammo}", offer.TotalAmmo.ToString("N0"))
        );

        AddReadOnlyMenuLine(
            _luiWeaponAmmoDetailMenu,
            LT("WeaponAmmoDetail_Price", "Giá bán: ${price}", "{price}", offer.Price.ToString("N0"))
        );

        var confirm = new NativeItem(L("WeaponAmmoDetail_Confirm", "Xác nhận thanh toán"));
        confirm.Activated += (s, e) =>
        {
            ConfirmWeaponAmmoPurchase(offer);
        };
        _luiWeaponAmmoDetailMenu.Add(confirm);

        var decline = new NativeItem(L("WeaponAmmoDetail_Decline", "Từ chối thanh toán"));
        decline.Activated += (s, e) =>
        {
            CloseWeaponAmmoMenus(true);
        };
        _luiWeaponAmmoDetailMenu.Add(decline);
    }

    private void ConfirmWeaponAmmoPurchase(WeaponAmmoOffer offer)
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
            {
                CloseWeaponAmmoMenus(false);
                return;
            }

            if (offer == null || !offer.Valid)
            {
                CloseWeaponAmmoMenus(false);
                return;
            }

            int cost = Math.Max(0, offer.Price);

            if (Game.Player.Money < cost)
            {
                Notification.Show(string.Format(_msgNoMoney[_rng.Next(_msgNoMoney.Length)], cost));
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            Game.Player.Money -= cost;
            AddToSpendingAccumulator(cost);

            if (offer.Choice == WeaponAmmoChoice.CurrentWeapon)
            {
                GiveAmmoToWeapon(player, offer.CurrentWeaponHash, offer.TotalAmmo);
            }
            else
            {
                foreach (var kv in offer.AmmoLines)
                {
                    GiveAmmoToWeapon(player, kv.Key, kv.Value);
                }
            }

            _ammoLocked = false;
            _ammoBackCancelCount = 0;

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            ShowFeedMessage(
                L("FeedSender", "Online Shop"),
                L("FeedPurchaseSubject", "Mua hàng"),
                _msgSuccess[_rng.Next(_msgSuccess.Length)] +
                L("FeedThanksSuffix", " Dù gì thì cũng cảm ơn bạn đã mua hàng nhé! Nhớ quay lại mua tiếp nha!"));

            CloseWeaponAmmoMenus(false);
        }
        catch
        {
            CloseWeaponAmmoMenus(false);
        }
    }

    private void GiveAmmoToWeapon(Ped player, uint weaponHash, int amount)
    {
        if (player == null || !player.Exists() || amount <= 0)
            return;

        bool hasWeapon = SafeCall(
            () => Function.Call<bool>(Hash.HAS_PED_GOT_WEAPON, player.Handle, weaponHash, false),
            false);

        if (!hasWeapon)
        {
            SafeCall(() =>
                Function.Call(Hash.GIVE_WEAPON_TO_PED,
                    player.Handle,
                    weaponHash,
                    0,
                    false,
                    false));
        }

        SafeCall(() =>
            Function.Call(Hash.ADD_AMMO_TO_PED,
                player.Handle,
                weaponHash,
                amount));
    }

    private void CloseWeaponAmmoMenus(bool countCancel)
    {
        try
        {
            if (_luiWeaponAmmoDetailMenu != null)
            {
                _luiWeaponAmmoDetailMenu.Visible = false;
                _luiWeaponAmmoDetailMenu.Clear();
            }
        }
        catch { }

        try
        {
            if (_luiWeaponAmmoMenu != null)
            {
                _luiWeaponAmmoMenu.Visible = false;
                _luiWeaponAmmoMenu.Clear();
            }
        }
        catch { }

        _activeAmmoOffer = null;

        if (countCancel)
        {
            try
            {
                HandleBackCancel(ref _ammoBackCancelCount, ref _ammoLocked);
            }
            catch { }

            EnsureHelpBoxCooldownSet();
        }

        Interval = 1000;
    }

    private void InitializeLemonStoreMenu()
    {
        try
        {
            if (_luiStoreMenu != null)
                return;

            _luiStoreMenu = new NativeMenu(
                L("Store_MenuTitle", "Items"),
                L("Store_SubTitle", "Danh sách vật phẩm"));

            _luiPool.Add(_luiStoreMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiStoreMenu);
            _luiStoreMenu.Visible = false;

            _storeItems.Clear();
            _storeItems.Add(new StoreItemEntry
            {
                Type = StoreItemType.Health,
                Name = L("Store_Health", "1. Máu"),
                MinPrice = HealthMin,
                MaxPrice = HealthMax,
                Free = false
            });
            _storeItems.Add(new StoreItemEntry
            {
                Type = StoreItemType.Armor,
                Name = L("Store_Armor", "2. Giáp"),
                MinPrice = ArmorMin,
                MaxPrice = ArmorMax,
                Free = false
            });
            _storeItems.Add(new StoreItemEntry
            {
                Type = StoreItemType.Special,
                Name = L("Store_Special", "3. Năng lực đặc biệt"),
                MinPrice = SpecialMin,
                MaxPrice = SpecialMax,
                Free = false
            });
            _storeItems.Add(new StoreItemEntry
            {
                Type = StoreItemType.Oxygen,
                Name = L("Store_Oxygen", "4. Khí oxy"),
                Free = true
            });
            _storeItems.Add(new StoreItemEntry
            {
                Type = StoreItemType.Parachute,
                Name = L("Store_Parachute", "5. Dù"),
                Free = true
            });
            _storeItems.Add(new StoreItemEntry
            {
                Type = StoreItemType.NightVisionHat,
                Name = L("Store_NightVisionHat", "6. Mũ nhìn đêm"),
                Free = true
            });

            BuildStoreMenu();
        }
        catch
        {
        }
    }

    private void BuildStoreMenu()
    {
        if (_luiStoreMenu == null)
            return;

        _luiStoreMenu.Clear();

        foreach (var entry in _storeItems)
        {
            var item = new NativeItem(entry.Name);
            entry.MenuItem = item;

            UpdateStoreItemVisual(entry);

            item.Selected += (s, e) =>
            {
                try { UpdateStoreItemVisual(entry); } catch { }
            };

            item.Activated += (s, e) =>
            {
                HandleStoreItemActivated(entry);
            };

            _luiStoreMenu.Add(item);

            // Khi Mũ nhìn đêm đang ON thì chèn 2 mục con ngay bên dưới
            if (entry.Type == StoreItemType.NightVisionHat && _nightVisionHatEnabled)
            {
                AddNightVisionSubItems();
            }
        }

        var leave = new NativeItem(L("Store_LeaveStore", "Rời khỏi cửa hàng"));
        leave.Activated += (s, e) =>
        {
            CloseStoreMenu(false);
        };
        _luiStoreMenu.Add(leave);

        var decline = new NativeItem(L("Store_DeclineRoot", "Quyết định từ chối mua hàng"));
        decline.Activated += (s, e) =>
        {
            CloseStoreMenu(true);
        };
        _luiStoreMenu.Add(decline);
    }

    private void RefreshStoreMenuVisible()
    {
        if (_luiStoreMenu == null)
            return;

        bool wasVisible = _luiStoreMenu.Visible;
        BuildStoreMenu();
        _luiStoreMenu.Visible = wasVisible;
    }

    private bool IsWearingNightVisionHat(Ped ped)
    {
        if (ped == null || !ped.Exists())
            return false;

        int drawable = Function.Call<int>(Hash.GET_PED_PROP_INDEX, ped.Handle, HatPropSlot);
        if (drawable != HatDrawableIndex)
            return false;

        int texture = Function.Call<int>(Hash.GET_PED_PROP_TEXTURE_INDEX, ped.Handle, HatPropSlot);
        return texture == HatTextureIndex;
    }

    private bool TryEquipNightVisionHat(Ped ped)
    {
        if (ped == null || !ped.Exists())
            return false;

        try
        {
            Function.Call(Hash.SET_PED_PROP_INDEX, ped.Handle, HatPropSlot, HatDrawableIndex, HatTextureIndex, true);
            return IsWearingNightVisionHat(ped);
        }
        catch
        {
            return false;
        }
    }

    private void ClearNightVisionHat(Ped ped)
    {
        if (ped == null || !ped.Exists())
            return;

        try
        {
            Function.Call(Hash.CLEAR_PED_PROP, ped.Handle, HatPropSlot);
        }
        catch { }
    }

    private void DisableAllNightVisionModes()
    {
        _nightVisionEnabled = false;
        _thermalVisionEnabled = false;

        try
        {
            Function.Call(Hash.SET_NIGHTVISION, false);
        }
        catch { }

        try
        {
            Function.Call(Hash.SET_SEETHROUGH, false);
        }
        catch { }
    }

    private void ApplyNightVisionMode(bool useNightVision)
    {
        _nightVisionEnabled = useNightVision;
        _thermalVisionEnabled = !useNightVision;

        try
        {
            Function.Call(Hash.SET_NIGHTVISION, useNightVision);
        }
        catch { }

        try
        {
            Function.Call(Hash.SET_SEETHROUGH, !useNightVision);
        }
        catch { }
    }

    private void ToggleNightVisionHatFeature()
    {
        try
        {
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists() || ped.IsDead)
                return;

            if (_nightVisionHatEnabled)
            {
                _nightVisionHatEnabled = false;
                DisableAllNightVisionModes();
                ClearNightVisionHat(ped);

                try
                {
                    _nightVisionUiSyncLock = true;
                    if (_nightVisionGlowCheckbox != null) _nightVisionGlowCheckbox.Checked = false;
                    if (_nightVisionThermalCheckbox != null) _nightVisionThermalCheckbox.Checked = false;
                }
                finally
                {
                    _nightVisionUiSyncLock = false;
                }
            }
            else
            {
                if (!TryEquipNightVisionHat(ped))
                {
                    return;
                }

                _nightVisionHatEnabled = true;
            }

            RefreshStoreMenuVisible();
        }
        catch { }
    }

    private void AddNightVisionSubItems()
    {
        if (_luiStoreMenu == null)
            return;

        _nightVisionGlowCheckbox = new NativeCheckboxItem(L("Store_NightVisionGlow", "Kính phát quang"), _nightVisionEnabled);
        _nightVisionGlowCheckbox.CheckboxChanged += (s, e) =>
        {
            HandleNightVisionCheckboxChanged(true);
        };
        _luiStoreMenu.Add(_nightVisionGlowCheckbox);

        _nightVisionThermalCheckbox = new NativeCheckboxItem(L("Store_NightVisionThermal", "Kính nhiệt"), _thermalVisionEnabled);
        _nightVisionThermalCheckbox.CheckboxChanged += (s, e) =>
        {
            HandleNightVisionCheckboxChanged(false);
        };
        _luiStoreMenu.Add(_nightVisionThermalCheckbox);
    }

    private void HandleNightVisionCheckboxChanged(bool glowMode)
    {
        if (_nightVisionUiSyncLock)
            return;

        if (!_nightVisionHatEnabled)
        {
            try
            {
                _nightVisionUiSyncLock = true;
                if (_nightVisionGlowCheckbox != null) _nightVisionGlowCheckbox.Checked = false;
                if (_nightVisionThermalCheckbox != null) _nightVisionThermalCheckbox.Checked = false;
            }
            finally
            {
                _nightVisionUiSyncLock = false;
            }
            return;
        }

        try
        {
            _nightVisionUiSyncLock = true;

            if (glowMode)
            {
                if (_nightVisionGlowCheckbox != null && _nightVisionGlowCheckbox.Checked)
                {
                    if (_nightVisionThermalCheckbox != null)
                        _nightVisionThermalCheckbox.Checked = false;

                    ApplyNightVisionMode(true);
                }
                else if ((_nightVisionThermalCheckbox == null) || !_nightVisionThermalCheckbox.Checked)
                {
                    DisableAllNightVisionModes();
                }
            }
            else
            {
                if (_nightVisionThermalCheckbox != null && _nightVisionThermalCheckbox.Checked)
                {
                    if (_nightVisionGlowCheckbox != null)
                        _nightVisionGlowCheckbox.Checked = false;

                    ApplyNightVisionMode(false);
                }
                else if ((_nightVisionGlowCheckbox == null) || !_nightVisionGlowCheckbox.Checked)
                {
                    DisableAllNightVisionModes();
                }
            }
        }
        finally
        {
            _nightVisionUiSyncLock = false;
        }

        RefreshStoreMenuVisible();
    }

    private void ToggleNightVisionMode(bool nightVision)
    {
        try
        {
            Ped ped = Game.Player.Character;
            if (ped == null || !ped.Exists() || ped.IsDead)
                return;

            if (!_nightVisionHatEnabled)
            {
                GTA.UI.Screen.ShowSubtitle(L("NightVisionHat_RequireFirst", "Bạn phải sử dụng Mũ nhìn đêm trước."), 2500);
                return;
            }

            if (!IsWearingNightVisionHat(ped))
            {
                if (!TryEquipNightVisionHat(ped))
                {
                    GTA.UI.Screen.ShowSubtitle(L("NightVisionHat_EquipFail", "Không thể đội Mũ nhìn đêm trên nhân vật này."), 2500);
                    return;
                }
            }

            try
            {
                _nightVisionUiSyncLock = true;

                if (nightVision)
                {
                    if (_nightVisionGlowCheckbox != null) _nightVisionGlowCheckbox.Checked = true;
                    if (_nightVisionThermalCheckbox != null) _nightVisionThermalCheckbox.Checked = false;
                    ApplyNightVisionMode(true);
                }
                else
                {
                    if (_nightVisionThermalCheckbox != null) _nightVisionThermalCheckbox.Checked = true;
                    if (_nightVisionGlowCheckbox != null) _nightVisionGlowCheckbox.Checked = false;
                    ApplyNightVisionMode(false);
                }
            }
            finally
            {
                _nightVisionUiSyncLock = false;
            }

            RefreshStoreMenuVisible();
        }
        catch { }
    }

    private void RefreshStorePrices()
    {
        foreach (var entry in _storeItems)
        {
            if (entry.Free)
            {
                entry.CurrentPrice = 0;
                continue;
            }

            if (entry.MaxPrice < entry.MinPrice)
            {
                entry.CurrentPrice = entry.MinPrice;
            }
            else if (entry.MinPrice == entry.MaxPrice)
            {
                entry.CurrentPrice = entry.MinPrice;
            }
            else
            {
                entry.CurrentPrice = _rng.Next(entry.MinPrice, entry.MaxPrice + 1);
            }
        }
    }

    private bool IsStoreStatsLocked()
    {
        return _statsLocked;
    }

    private void UpdateStoreItemVisual(StoreItemEntry entry)
    {
        if (entry == null || entry.MenuItem == null)
            return;

        // Item đặc biệt: Mũ nhìn đêm
        if (entry.Type == StoreItemType.NightVisionHat)
        {
            entry.MenuItem.AltTitle = _nightVisionHatEnabled ? "~HUD_COLOUR_DEGEN_GREEN~ON~s~" : "~c~OFF~s~";
            entry.MenuItem.Description = _nightVisionHatEnabled
                ? L("Store_NightVisionHatOnDesc", "Nhấn Enter để tắt Mũ nhìn đêm. Khi đang ON, hai mục kính bên dưới mới dùng được.")
                : L("Store_NightVisionHatOffDesc", "Nhấn Enter để bật Mũ nhìn đêm.");
            return;
        }

        bool locked = IsStoreStatsLocked() &&
                      (entry.Type == StoreItemType.Health ||
                       entry.Type == StoreItemType.Armor ||
                       entry.Type == StoreItemType.Special);

        if (entry.Free)
        {
            entry.MenuItem.AltTitle = L("Store_FreeAltTitle", "FREE");
            return;
        }

        if (locked)
        {
            entry.MenuItem.AltTitle = "~r~~h~Locked~s~";
            entry.MenuItem.Description = string.Format(
                L("Store_LockedDescription", "Tính năng này hiện đang bị khóa. Bạn cần phải trả ${0:N0} để mở khóa vật phẩm này!"),
                UnlockFeeStats);
            return;
        }

        entry.MenuItem.AltTitle = string.Format("${0:N0}", entry.CurrentPrice);
        entry.MenuItem.Description = L("Store_PurchaseHint", "Nhấn Enter để mua vật phẩm này.");
    }

    private void OpenStoreMenu()
    {
        try
        {
            if (_pendingType != PendingType.None)
                return;

            RefreshStorePrices();
            BuildStoreMenu();

            foreach (var entry in _storeItems)
                UpdateStoreItemVisual(entry);

            _luiStoreMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }
    }

    private void CloseStoreMenu(bool countCancel)
    {
        try
        {
            if (_luiStoreMenu != null)
            {
                _luiStoreMenu.Visible = false;
                _luiStoreMenu.Clear();
            }
        }
        catch { }

        if (countCancel)
        {
            try
            {
                RecordBackCancel(PendingType.Health);
            }
            catch { }
        }

        EnsureHelpBoxCooldownSet();
    }

    private void HandleStoreItemActivated(StoreItemEntry entry)
    {
        try
        {
            if (entry == null)
                return;

            if (entry.Type == StoreItemType.NightVisionHat)
            {
                ToggleNightVisionHatFeature();
                return;
            }

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return;

            bool locked = IsStoreStatsLocked() &&
                          (entry.Type == StoreItemType.Health ||
                           entry.Type == StoreItemType.Armor ||
                           entry.Type == StoreItemType.Special);

            if (locked)
            {
                if (Game.Player.Money < UnlockFeeStats)
                {
                    Notification.Show(string.Format(_msgNoMoney[_rng.Next(_msgNoMoney.Length)], UnlockFeeStats));
                    PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                    return;
                }

                Game.Player.Money -= UnlockFeeStats;
                AddToSpendingAccumulator(UnlockFeeStats);

                _statsLocked = false;
                _statsBackCancelCount = 0;

                PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                ShowFeedMessage(
                    L("FeedSender", "Online Shop"),
                    L("FeedUnlockSubject", "Mở khóa"),
                    L("Store_UnlockMessage", "Vật phẩm đã được mở khóa trở lại. Lần sau hãy chú ý hơn nhá!?"));

                RefreshStorePrices();
                BuildStoreMenu();

                if (_luiStoreMenu != null)
                    _luiStoreMenu.Visible = true;

                return;
            }

            int price = entry.Free ? 0 : entry.CurrentPrice;
            if (price < 0) price = 0;

            if (Game.Player.Money < price)
            {
                Notification.Show(string.Format(_msgNoMoney[_rng.Next(_msgNoMoney.Length)], price));
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            if (price > 0)
            {
                Game.Player.Money -= price;
                AddToSpendingAccumulator(price);
            }

            switch (entry.Type)
            {
                case StoreItemType.Health:
                    {
                        int maxAllowed = BASE_HEALTH + HEALTH_BOOST_MAX_EXTRA;
                        int newMax = Math.Min(player.MaxHealth + HEALTH_BOOST_STEP, maxAllowed);
                        player.MaxHealth = newMax;
                        player.Health = newMax;
                        Function.Call(Hash.CLEAR_PED_BLOOD_DAMAGE, player.Handle);
                        break;
                    }

                case StoreItemType.Armor:
                    {
                        player.Armor = Math.Min(player.Armor + ARMOR_BOOST_STEP, ARMOR_MAX_TOTAL);
                        break;
                    }

                case StoreItemType.Special:
                    {
                        Function.Call(Hash.SPECIAL_ABILITY_CHARGE_NORMALIZED, Game.Player.Handle, 1.0f, true);
                        break;
                    }

                case StoreItemType.Oxygen:
                    {
                        Function.Call(Hash.SET_PED_SCUBA_GEAR_VARIATION, player.Handle);
                        Function.Call(Hash.SET_PED_MAX_TIME_UNDERWATER, player.Handle, 200.0f);
                        break;
                    }

                case StoreItemType.Parachute:
                    {
                        Function.Call(Hash.GIVE_WEAPON_TO_PED, player.Handle, (uint)WeaponHash.Parachute, 1, false, true);
                        break;
                    }
            }

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            ShowFeedMessage(
                L("FeedSender", "Online Shop"),
                L("FeedPurchaseSubject", "Mua hàng"),
                _msgSuccess[_rng.Next(_msgSuccess.Length)] +
                L("FeedThanksSuffix", " Dù gì thì cũng cảm ơn bạn đã mua hàng nhé! Nhớ quay lại mua tiếp nha!"));

            CloseStoreMenu(false);
        }
        catch
        {
            CloseStoreMenu(false);
        }
    }

    // ----------------------- Key handling -----------------------
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Kiểm tra an toàn cơ bản
        if (!_modReady) return;
        if (Game.IsLoading || Game.IsCutsceneActive) return;

        // --- HANDLE: Insert = soft-disable / extend soft-disable ---
        if (_softDisabled)
        {
            if (e.KeyCode == Keys.Insert)
            {
                _softDisabledExpiry = Game.GameTime + SoftDisableDurationMs;
                GTA.UI.Screen.ShowSubtitle(L("SoftDisableExtended", "~HUD_COLOUR_DEGEN_RED~Thời gian tắt 10 giây.~h~~s~"));
            }
            return;
        }

        if (e.KeyCode == Keys.Insert)
        {
            _softDisabled = true;
            _softDisabledExpiry = Game.GameTime + SoftDisableDurationMs;

            try
            {
                _isProcessingAmmo = false;
                _weaponHashesToProcess.Clear();
            }
            catch { }

            try { ClearPending(false, false, false); } catch { }

            _searchActive = false;
            _searchChar = '\0';
            _searchStartGameTime = 0;
            _vehicleViewList = null;

            GTA.UI.Screen.ShowSubtitle(L("SoftDisableEnabled", "~r~~h~Mod đã tắt tạm thời.~h~~s~"));
            return;
        }

        if (e.Shift && e.KeyCode == Keys.L)
        {
            // Shift+L is replaced by the Elite Protection Unit phone contact.
            return;
        }

        // --- XỬ LÝ KHI ĐANG Ở CHẾ ĐỘ SEARCH ---
        if (_searchActive)
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (_searchChar == '\0')
                {
                    GTA.UI.Screen.ShowSubtitle(L("SearchMissingChar", "~HUD_COLOUR_DEGEN_YELLOW~Hãy nhập ký tự của phương tiện!!!"), 3000);
                    return;
                }

                if (OpenFilteredVehicleMenu(_searchChar))
                {
                    _searchActive = false;
                    _searchChar = '\0';
                    _searchStartGameTime = 0;
                    _vehicleViewList = null;
                    EnsureHelpBoxCooldownSet();
                }
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                _searchActive = false;
                _searchChar = '\0';
                _searchStartGameTime = 0;
                _vehicleViewList = null;

                if (OpenAllVehiclesMenu())
                    EnsureHelpBoxCooldownSet();

                return;
            }

            if (e.KeyCode == Keys.Back)
            {
                _searchChar = '\0';
                return;
            }

            char c = GetCharFromKey(e.KeyCode);
            if (c != '\0')
            {
                _searchChar = c;
                PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }
            return;
        }

        // --- PHẦN MỚI: XỬ LÝ LEMONUI STORE MENU ---
        if (_luiStoreMenu != null && _luiStoreMenu.Visible && e.KeyCode == Keys.Back)
        {
            CloseStoreMenu(true);
            return;
        }

        if (e.KeyCode == KeyStoreMenu)
        {
            OpenStoreMenu();
            return;
        }

        // --- MỚI: Xử lý đóng Menu đạn bằng phím Backspace ---
        if ((_luiWeaponAmmoMenu != null && _luiWeaponAmmoMenu.Visible) ||
            (_luiWeaponAmmoDetailMenu != null && _luiWeaponAmmoDetailMenu.Visible))
        {
            if (e.KeyCode == Keys.Back)
            {
                CloseWeaponAmmoMenus(true);
                return;
            }
        }

        if (HandleDaveyBribeMenuInput(e))
            return;

        if (_luiRewardInsuranceMenu != null && _luiRewardInsuranceMenu.Visible)
        {
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
            {
                CloseRewardInsuranceMenu(false);
                ShowRewardRootMenu();
                return;
            }
        }

        if (_luiRewardDetailMenu != null && _luiRewardDetailMenu.Visible)
        {
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
            {
                CloseRewardVehicleDetailMenu(false);
                ShowRewardRootMenu();
                return;
            }
        }

        if (_luiRewardRootMenu != null && _luiRewardRootMenu.Visible)
        {
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
            {
                CloseRewardMenus(true);
                return;
            }
        }

        if (_luiBodyguardDetailMenu != null && _luiBodyguardDetailMenu.Visible)
        {
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
            {
                OpenBodyguardSelectionMenu();
                return;
            }
        }

        if (_luiBodyguardMenu != null && _luiBodyguardMenu.Visible)
        {
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
            {
                CloseBodyguardMenus(true);
                return;
            }
        }

        // --- THÊM MỚI: Xử lý đóng các Menu giao dịch tiền lậu ---
        if (_luiIllegalMoneyTradeMenu != null && _luiIllegalMoneyTradeMenu.Visible)
        {
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
            {
                CloseIllegalMoneyTradeMenu(false);
                return;
            }
        }

        if (_luiIllegalMoneyDetailMenu != null && _luiIllegalMoneyDetailMenu.Visible)
        {
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
            {
                if (_luiIllegalMoneyDetailMenu != null)
                    _luiIllegalMoneyDetailMenu.Visible = false;

                ShowRewardRootMenu();
                return;
            }
        }

        // Kiểm tra nếu có bất kỳ menu LemonUI nào đang hiển thị thì không xử lý phím bên dưới
        if (_luiPool != null && SafeCall(() => _luiPool.AreAnyVisible, false))
            return;

        // 9) Vá OnKeyDown() để nhấn Enter gần ATM / NPC là mở thẳng menu giao dịch
        //    Chèn đoạn này sau chỗ kiểm tra _luiPool.AreAnyVisible và trước các logic khác:
        if (_illegalMoneyRedeemArmed && e.KeyCode == Keys.Enter && !e.Shift && IsNearMazeBankAtm())
        {
            OpenIllegalMoneyTradeMenu();
            return;
        }

        if (_smugglerRedeemArmed && e.KeyCode == Keys.Enter && !e.Shift && IsNearSmugglerNpc())
        {
            OpenSmugglerTradeMenu();
            return;
        }

        // --- TIẾP TỤC CÁC LOGIC CŨ ---
        if (_pendingType != PendingType.VehicleSelection && Game.GameTime - _lastInteractionGameTime < InteractionCooldownMs)
            return;

        // --- PHẦN 1: XỬ LÝ KHI ĐANG CÓ THÔNG BÁO CHỜ (PENDING) ---
        if (_pendingType != PendingType.None)
        {
            if (_pendingType == PendingType.VehicleSelection)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    _lastInteractionGameTime = Game.GameTime;
                    var list = GetActiveVehicleList();
                    var chosen = list[_vehSeIn];
                    ClearPending(false, false);
                    ShowVehicleOffer(chosen, true);
                    return;
                }

                if (e.KeyCode == Keys.Back)
                {
                    _lastInteractionGameTime = Game.GameTime;
                    ClearPending(false, false);
                    HandleVehicleOffer();
                    return;
                }

                if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Left)
                {
                    int now = Game.GameTime;
                    if (now - _lastVehicleSelectionChangeTime >= VehicleSelectionDebounceMs)
                    {
                        var currentList = GetActiveVehicleList();
                        if (e.KeyCode == Keys.Right)
                            _vehSeIn = (_vehSeIn + 1) % currentList.Count;
                        else
                        {
                            _vehSeIn = (_vehSeIn - 1);
                            if (_vehSeIn < 0) _vehSeIn = currentList.Count - 1;
                        }

                        _lastVehicleSelectionChangeTime = now;
                        UpdateVehicleSelectionMessage();
                        PlayFrontendSound("NAV_UP_DOWN", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                    }
                    return;
                }
                return;
            }

            if (_pendingType == PendingType.RewardRedeem)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    _lastInteractionGameTime = Game.GameTime;
                    RedeemReward();
                    return;
                }
                if (e.KeyCode == Keys.Back)
                {
                    _lastInteractionGameTime = Game.GameTime;
                    ClearPending(false, false);
                    return;
                }
            }

            if (_pendingType == PendingType.HireBodyguard)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    _lastInteractionGameTime = Game.GameTime;
                    OpenBodyguardSelectionMenu();
                    return;
                }

                if (e.KeyCode == Keys.Back)
                {
                    _lastInteractionGameTime = Game.GameTime;
                    _bodyguardPendingCost = 0;
                    ClearPending(false, false);
                    return;
                }
            }

            if (e.KeyCode == Keys.Enter)
            {
                _lastInteractionGameTime = Game.GameTime;
                if (_pendingType == PendingType.WeaponOffer || _pendingType == PendingType.VehicleOffer)
                {
                    if (_pendingType == PendingType.WeaponOffer) AcceptPending(!e.Shift);
                    else AcceptVehiclePending();
                    return;
                }
                AcceptPending(!e.Shift);
                return;
            }
            else if (e.KeyCode == Keys.Back)
            {
                _lastInteractionGameTime = Game.GameTime;
                if (_pendingType == PendingType.WeaponOffer || _pendingType == PendingType.VehicleOffer)
                {
                    try { RecordBackCancel(_pendingType); } catch { }
                    ClearPending(false, false);
                    return;
                }
                CancelPending();
                return;
            }
            return;
        }

        // --- PHẦN 2: XỬ LÝ CÁC PHÍM TẮT THÔNG THƯỜNG ---
        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead) return;

        // --- MỚI: Mở menu vũ khí bằng Numpad2 ---
        if (e.KeyCode == KeyAmmo)
        {
            OpenWeaponAmmoMenu();
            return;
        }
    }

    // ----------------------- Start / Split / Clear pending -----------------------
    private void StartVehicleSearch()
    {
        // Nếu đang trong thời gian cooldown -> chặn mở
        if (IsHelpBoxCooldownActive())
        {
            ShowHelpCooldownMessage();
            return;
        }

        // Không kích hoạt search nếu có pending khác
        if (_pendingType != PendingType.None)
        {
            GTA.UI.Screen.ShowSubtitle(L("SearchNotAllowedWhilePending", "~y~Không thể tìm khi có cửa sổ đang chờ. Hủy/xác nhận trước."), 3000);
            return;
        }

        if (_vehicles == null || _vehicles.Count == 0)
        {
            GTA.UI.Screen.ShowSubtitle(L("VehicleListEmpty", "~HUD_COLOUR_DEGEN_RED~Phương tiện trống."), 3000);
            return;
        }

        _searchActive = true;
        _searchChar = '\0';
        _searchStartGameTime = Game.GameTime;
        _vehicleViewList = null; // clear previous filter

        // Force per-frame ticks so ShowHelpTextThisFrame persists
        Interval = 0;

        PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
    }

    private void InitializeLemonBodyguardMenus()
    {
        try
        {
            if (_luiBodyguardMenu != null)
                return;

            if (_bodyguardSpecs.Count == 0)
            {
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_Clone", "Clone"), ModelHash = 0u, UsePlayerClone = true });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_Hacker", "Hacker"), ModelHash = 0x99BB00F8u, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_Marine", "Marine"), ModelHash = 0x58D696FEu, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_Swat", "Swat"), ModelHash = 0x8D8F1B10u, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_AmandaTownley", "Amanda Townley"), ModelHash = 0x6D1E15F7u, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_Musclbeac", "Musclbeac"), ModelHash = 0x4B652906u, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_CiaSec", "CiaSec"), ModelHash = 0x625D6958u, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_OgBoss", "Og Boss"), ModelHash = 0x681BD012u, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_Tranvest", "Tranvest"), ModelHash = 0xE0E69974u, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_LamarDavis", "Lamar Davis"), ModelHash = 0x65B93076u, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_Brad", "Brad"), ModelHash = 0xBDBB4922u, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_TaoCheng", "Tao Cheng"), ModelHash = 0xDC5C5EA5u, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_DaveNorton", "Dave Norton"), ModelHash = 0x15CD4C33u, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_Devin", "Devin"), ModelHash = 0x7461A0B0u, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_SteveHains", "Steve Hains"), ModelHash = 0x382121C8u, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_PestContGunman", "Pest Cont Gunman"), ModelHash = 0x0B881AEEu, UsePlayerClone = false });
                _bodyguardSpecs.Add(new BodyguardSpec { Name = L("BodyguardSpec_JimmyDisanto", "Jimmy Disanto"), ModelHash = 0x570462B9u, UsePlayerClone = false });
            }

            _luiBodyguardMenu = new NativeMenu(L("Bodyguard_MenuTitle", "Bodyguard"), L("Bodyguard_SubTitle", "Danh sách vệ sĩ"));
            _luiPool.Add(_luiBodyguardMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiBodyguardMenu);
            _luiBodyguardMenu.Visible = false;

            _luiBodyguardDetailMenu = new NativeMenu(L("Bodyguard_MenuTitle", "Bodyguard"), L("Bodyguard_DetailSubTitle", "Chi tiết vệ sĩ"));
            _luiPool.Add(_luiBodyguardDetailMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiBodyguardDetailMenu);
            _luiBodyguardDetailMenu.Visible = false;
        }
        catch { }
    }

    private void RefreshBodyguardOffers()
    {
        _bodyguardOffers.Clear();

        foreach (var spec in _bodyguardSpecs)
        {
            _bodyguardOffers.Add(new BodyguardOffer
            {
                Valid = true,
                Label = spec.Name,
                ModelHash = spec.ModelHash,
                UsePlayerClone = spec.UsePlayerClone,
                Price = _bodyguardPendingCost
            });
        }
    }

    private void BuildBodyguardMenu()
    {
        if (_luiBodyguardMenu == null)
            return;

        _luiBodyguardMenu.Clear();
        RefreshBodyguardOffers();

        for (int i = 0; i < _bodyguardOffers.Count; i++)
        {
            BodyguardOffer offer = _bodyguardOffers[i];
            var item = new NativeItem(string.Format("{0}. {1}", i + 1, offer.Label));
            item.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
            item.Description = L("Bodyguard_EnterHint", "Nhấn Enter để xem chi tiết.");

            item.Activated += (s, e) =>
            {
                OpenBodyguardDetailMenu(offer);
            };

            _luiBodyguardMenu.Add(item);
        }

        var decline = new NativeItem(L("Bodyguard_Decline", "Từ chối thuê dịch vụ"));
        decline.Activated += (s, e) =>
        {
            CloseBodyguardMenus(true);
        };
        _luiBodyguardMenu.Add(decline);
    }

    private void OpenBodyguardSelectionMenu()
    {
        try
        {
            if (_bodyguardPendingCost <= 0)
                _bodyguardPendingCost = _peCo > 0 ? _peCo : 0;

            if (_bodyguardPendingCost <= 0)
                return;

            ClearPending(false, false, false);
            BuildBodyguardMenu();

            if (_luiBodyguardDetailMenu != null)
            {
                _luiBodyguardDetailMenu.Visible = false;
                _luiBodyguardDetailMenu.Clear();
            }

            _luiBodyguardMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch { }
    }

    private void OpenBodyguardDetailMenu(BodyguardOffer offer)
    {
        try
        {
            if (offer == null || !offer.Valid)
                return;

            _activeBodyguardOffer = offer;
            BuildBodyguardDetailMenu(offer);

            if (_luiBodyguardMenu != null)
                _luiBodyguardMenu.Visible = false;

            _luiBodyguardDetailMenu.Visible = true;
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch { }
    }

    private void BuildBodyguardDetailMenu(BodyguardOffer offer)
    {
        if (_luiBodyguardDetailMenu == null || offer == null)
            return;

        _luiBodyguardDetailMenu.Clear();

        AddReadOnlyMenuLine(_luiBodyguardDetailMenu, string.Format(L("Bodyguard_DetailLabel", "Vệ sĩ: {0}"), offer.Label));
        AddReadOnlyMenuLine(_luiBodyguardDetailMenu, string.Format(L("Bodyguard_DetailPrice", "Giá thuê: ${0:N0}"), offer.Price));

        var confirm = new NativeItem(L("Bodyguard_Confirm", "Xác nhận thuê vệ sĩ"));
        confirm.Activated += (s, e) =>
        {
            ConfirmBodyguardPurchase(offer);
        };
        _luiBodyguardDetailMenu.Add(confirm);

        var back = new NativeItem(L("Bodyguard_Back", "Quay lại danh sách vệ sĩ"));
        back.Activated += (s, e) =>
        {
            OpenBodyguardSelectionMenu();
        };
        _luiBodyguardDetailMenu.Add(back);
    }

    private void ConfirmBodyguardPurchase(BodyguardOffer offer)
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
            {
                CloseBodyguardMenus(false);
                return;
            }

            if (offer == null || !offer.Valid)
            {
                CloseBodyguardMenus(false);
                return;
            }

            int cost = Math.Max(0, _bodyguardPendingCost);

            if (Game.Player.Money < cost)
            {
                Notification.Show(string.Format(_msgNoMoney[_rng.Next(_msgNoMoney.Length)], cost));
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            Game.Player.Money -= cost;
            AddToSpendingAccumulator(cost);

            bool spawnSucceeded = false;
            try
            {
                if (Bodyguards.Instance != null)
                {
                    spawnSucceeded = Bodyguards.Instance.TrySpawnClone(
                        offer.UsePlayerClone ? 0u : offer.ModelHash);
                }
            }
            catch
            {
                spawnSucceeded = false;
            }

            if (!spawnSucceeded)
            {
                Game.Player.Money += cost;
                _spendingAccumulator.SubtractFromSpendingAccumulator(cost);
                Notification.Show(_msgFail[_rng.Next(_msgFail.Length)]);
                CloseBodyguardMenus(false);
                return;
            }

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            ShowFeedMessage(
                L("FeedSender", "Online Shop"),
                L("FeedPurchaseSubject", "Mua hàng"),
                _msgSuccess[_rng.Next(_msgSuccess.Length)] +
                L("FeedThanksSuffix", " Dù gì thì cũng cảm ơn bạn đã mua hàng nhé! Nhớ quay lại mua tiếp nha!"));

            CloseBodyguardMenus(false);
        }
        catch
        {
            CloseBodyguardMenus(false);
        }
    }

    private void CloseBodyguardMenus(bool setCooldown)
    {
        try
        {
            if (_luiBodyguardDetailMenu != null)
            {
                _luiBodyguardDetailMenu.Visible = false;
                _luiBodyguardDetailMenu.Clear();
            }
        }
        catch { }

        try
        {
            if (_luiBodyguardMenu != null)
            {
                _luiBodyguardMenu.Visible = false;
                _luiBodyguardMenu.Clear();
            }
        }
        catch { }

        _activeBodyguardOffer = null;

        if (setCooldown)
            EnsureHelpBoxCooldownSet();

        Interval = 1000;
    }

    private void StartPending(PendingType type, int cost, string message)
    {
        if (IsHelpBoxCooldownActive())
        {
            ShowHelpCooldownMessage();
            return;
        }

        _pendingType = type;
        _peCo = cost;
        _pendingExpiryGameTime = Game.GameTime + PendingDurationMs;
        _peMesTem = message;
        SplitPendingTemplate(message);
        // Force per-frame ticks while showing help
        Interval = 0;

        PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
    }

    private void StartPendingCustom(PendingType type, int cost, string message, int durationMs)
    {
        if (IsHelpBoxCooldownActive())
        {
            ShowHelpCooldownMessage();
            return;
        }

        _pendingType = type;
        _peCo = cost;
        _pendingExpiryGameTime = Game.GameTime + durationMs;
        _peMesTem = message;
        SplitPendingTemplate(message);
        // Force per-frame ticks while displaying help text
        Interval = 0;
        PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
    }

    // ---------------- Pending initiation (do not charge yet) ----------------
    private void SplitPendingTemplate(string template)
    {
        if (string.IsNullOrEmpty(template))
        {
            _penMesTi = "";
            _penMesBo = "";
            return;
        }

        int idx = template.IndexOf('\n');
        if (idx < 0)
        {
            _penMesTi = template;
            _penMesBo = "";
        }
        else
        {
            _penMesTi = template.Substring(0, idx);
            _penMesBo = template.Substring(idx + 1);
        }
    }

    private void ClearPending(bool showMessage, bool wasAccepted, bool setCooldown = true)
    {
        ClearVehiclePreview();
        CloseLemonVehicleMenus(false);
        CloseLemonWeaponMenus(false);
        CloseRewardMenus(false);
        // reset pending state
        _pendingType = PendingType.None;
        _peCo = 0;
        _pendingExpiryGameTime = 0;
        _pendingWeaponHash = 0;
        _peAA = 0;
        _pendingAccessoryAvailable = false;
        _pendAcCost = 0;
        _pendingAccessoryPercent = 0;
        _pendingWeaponHashes.Clear();

        // reset selection helper (QUAN TRỌNG: Đảm bảo reset lại vị trí chọn xe)
        _vehSeIn = 0;
        _lastVehicleSelectionChangeTime = -99999;

        // vehicle pending
        _pendingVehicleHash = 0;
        _pendingVehiclePrice = 0;
        _pendingVehicleEntry = null;

        _peMesTem = "";
        _penMesTi = "";
        _penMesBo = "";

        // --- Reset search view (Phần mới cập nhật) ---
        _vehicleViewList = null;
        _searchActive = false;
        _searchChar = '\0';
        _searchStartGameTime = 0;

        // reset timing
        Interval = 1000;
        // Đảm bảo Interval luôn hợp lệ
        Interval = Math.Max(100, 1000);

        if (showMessage)
        {
            if (wasAccepted)
            {
                ShowFeedMessage(
                     L("FeedSender", "Online Shop"),
                     L("FeedPurchaseSubject", "Mua hàng"),
                     _msgSuccess[_rng.Next(_msgSuccess.Length)] +
                     L("FeedThanksSuffix", " Dù gì thì cũng cảm ơn bạn đã mua hàng nhé! Nhớ quay lại mua tiếp nha!")
                );
            }
            else
            {
                Notification.Show(_msgCancel[_rng.Next(_msgCancel.Length)]);
            }
        }

        // Nếu được chỉ định, kích hoạt cooldown để tránh mở help-box ngay lập tức
        if (setCooldown)
        {
            EnsureHelpBoxCooldownSet();
        }
    }

    // ----------------------- AcceptPending (weapons & other) -----------------------
    private void AcceptPending(bool includeAccessory = true)
    {
        Ped player = Game.Player.Character;

        if (player == null || !player.Exists() || player.IsDead)
        {
            ClearPending(true, false);
            return;
        }

        // <<< ADDED: handle unlock payments for locked features >>>
        if (_pendingType == PendingType.LockedStats)
        {
            if (Game.Player.Money < _peCo)
            {
                Notification.Show(string.Format(_msgNoMoney[_rng.Next(_msgNoMoney.Length)], _peCo));
                ClearPending(true, false);
                return;
            }

            // deduct and unlock
            Game.Player.Money -= _peCo;
            AddToSpendingAccumulator(_peCo); // <<< ADDED: tích lũy chi tiêu
            _statsLocked = false;
            _statsBackCancelCount = 0;
            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            _lastInteractionGameTime = Game.GameTime;
            ClearPending(false, true);
            return;
        }
        else if (_pendingType == PendingType.LockedWeapon)
        {
            if (Game.Player.Money < _peCo)
            {
                Notification.Show(string.Format(_msgNoMoney[_rng.Next(_msgNoMoney.Length)], _peCo));
                ClearPending(true, false);
                return;
            }

            Game.Player.Money -= _peCo;
            AddToSpendingAccumulator(_peCo); // <<< ADDED: tích lũy chi tiêu
            _weaponLocked = false;
            _weaponBackCancelCount = 0;
            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            _lastInteractionGameTime = Game.GameTime;
            ClearPending(false, true);
            return;
        }

        // ===== WEAPON OFFER ======
        if (_pendingType == PendingType.WeaponOffer)
        {
            var wentry = _specialWeapons.Find(w => w.Hash == _pendingWeaponHash);

            bool hasComps =
                _weaponAttachments.TryGetValue(_pendingWeaponHash, out uint[] comps) &&
                comps != null && comps.Length > 0;

            bool accessoryApplicable =
                _pendingAccessoryAvailable &&
                hasComps &&
                wentry != null &&
                !wentry.PlayerHasAccessory;

            int totalCost = _peCo;
            if (includeAccessory && accessoryApplicable)
                totalCost += _pendAcCost;

            if (Game.Player.Money < totalCost)
            {
                Notification.Show(string.Format(_msgNoMoney[_rng.Next(_msgNoMoney.Length)], totalCost));
                ClearPending(true, false);
                return;
            }

            Game.Player.Money -= totalCost;
            AddToSpendingAccumulator(totalCost); // <<< ADDED

            bool success = true;

            bool playerHasWeapon = SafeCall(
                () => Function.Call<bool>(
                    Hash.HAS_PED_GOT_WEAPON,
                    player.Handle,
                    _pendingWeaponHash,
                    false),
                false);

            int preservedAmmo = 0;

            if (playerHasWeapon)
            {
                preservedAmmo = SafeCall(
                    () => Function.Call<int>(
                        Hash.GET_AMMO_IN_PED_WEAPON,
                        player.Handle,
                        _pendingWeaponHash),
                    0);
            }

            // ===== CASE 1: Đã có súng + mua phụ kiện =====
            if (playerHasWeapon && includeAccessory && accessoryApplicable)
            {
                SafeCall(() =>
                    Function.Call(Hash.REMOVE_WEAPON_FROM_PED,
                        player.Handle,
                        _pendingWeaponHash));

                SafeCall(() =>
                    Function.Call(Hash.GIVE_WEAPON_TO_PED,
                        player.Handle,
                        _pendingWeaponHash,
                        0,
                        false,
                        false));

                foreach (var comp in comps)
                {
                    SafeCall(() =>
                        Function.Call(Hash.GIVE_WEAPON_COMPONENT_TO_PED,
                            player.Handle,
                            _pendingWeaponHash,
                            comp));
                }

                if (preservedAmmo > 0)
                {
                    SafeCall(() =>
                        Function.Call(Hash.ADD_AMMO_TO_PED,
                            player.Handle,
                            _pendingWeaponHash,
                            preservedAmmo));
                }
            }
            else
            {
                bool gaveWeapon = false;

                if (!playerHasWeapon)
                {
                    SafeCall(() =>
                        Function.Call(Hash.GIVE_WEAPON_TO_PED,
                            player.Handle,
                            _pendingWeaponHash,
                            0,
                            false,
                            false));

                    gaveWeapon = true;
                }

                if (includeAccessory && accessoryApplicable)
                {
                    foreach (var comp in comps)
                    {
                        SafeCall(() =>
                            Function.Call(Hash.GIVE_WEAPON_COMPONENT_TO_PED,
                                player.Handle,
                                _pendingWeaponHash,
                                comp));
                    }
                }

                if (gaveWeapon)
                {
                    if (_peAA > 0)
                    {
                        SafeCall(() =>
                            Function.Call(Hash.ADD_AMMO_TO_PED,
                                player.Handle,
                                _pendingWeaponHash,
                                _peAA));
                    }
                }
                else if (!includeAccessory && _peAA > 0)
                {
                    SafeCall(() =>
                        Function.Call(Hash.ADD_AMMO_TO_PED,
                            player.Handle,
                            _pendingWeaponHash,
                            _peAA));
                }
            }

            if (!success)
            {
                Notification.Show(_msgFail[_rng.Next(_msgFail.Length)]);
                SafeCall(() => Game.Player.Money += _peCo);
                ClearPending(true, false);
                return;
            }

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            ShowFeedMessage(
                      L("FeedSender", "Online Shop"),
                      L("FeedPurchaseSubject", "Mua hàng"),
                      _msgSuccess[_rng.Next(_msgSuccess.Length)] +
                      L("FeedThanksSuffix", " Dù gì thì cũng cảm ơn bạn đã mua hàng nhé! Nhớ quay lại mua tiếp nha!")
            );

            SafeCall(() =>
                PersistentManager.RegisterWeaponHash(_pendingWeaponHash));

            if (wentry != null)
            {
                wentry.TimesPurchased = Math.Max(0, wentry.TimesPurchased + 1);
                if (includeAccessory && accessoryApplicable)
                    wentry.PlayerHasAccessory = true;

                wentry.PlayerHasWeapon = true;

                if (wentry.Hash == 0x060EC506u)
                    _specialWeapons.RemoveAll(w => w.Hash == 0x060EC506u);

                if (wentry.Hash == 0x94117305u)
                    _specialWeapons.RemoveAll(w => w.Hash == 0x94117305u);
            }

            UpdateAccessoryOwnership();

            // <<< ADDED: Reset counter for WeaponOffer after successful purchase >>>
            ResetBackCountersForType(PendingType.WeaponOffer);

            _lastInteractionGameTime = Game.GameTime;
            ClearPending(false, true);
            return;
        }

        // ===== OTHER TYPES =======
        if (_peCo <= 0)
        {
            ClearPending(true, false);
            return;
        }

        if (Game.Player.Money < _peCo)
        {
            Notification.Show(string.Format(_msgNoMoney[_rng.Next(_msgNoMoney.Length)], _peCo));
            ClearPending(true, false);
            return;
        }

        Game.Player.Money -= _peCo;
        AddToSpendingAccumulator(_peCo); // <<< ADDED

        bool processSuccess = true;

        switch (_pendingType)
        {
            case PendingType.Health:
                SafeCall(() =>
                {
                    int newMax = Math.Min(
                        player.MaxHealth + HEALTH_BOOST_STEP,
                        BASE_HEALTH + HEALTH_BOOST_MAX_EXTRA);

                    player.MaxHealth = newMax;
                    player.Health = newMax;

                    Function.Call(Hash.CLEAR_PED_BLOOD_DAMAGE, player.Handle);
                });
                break;

            case PendingType.Armor:
                SafeCall(() =>
                    player.Armor = Math.Min(
                        player.Armor + ARMOR_BOOST_STEP,
                        ARMOR_MAX_TOTAL));
                break;

            case PendingType.Special:
                SafeCall(() =>
                    Function.Call(Hash.SPECIAL_ABILITY_CHARGE_NORMALIZED,
                        Game.Player.Handle,
                        1.0f,
                        true));
                break;

            case PendingType.AmmoSingle:
                SafeCall(() =>
                    Function.Call(Hash.ADD_AMMO_TO_PED,
                        player.Handle,
                        _pendingWeaponHash,
                        _peAA));
                break;

            case PendingType.AmmoAll:
                _weaponHashesToProcess.Clear();
                foreach (var wh in _pendingWeaponHashes)
                    _weaponHashesToProcess.Enqueue(wh);

                _isProcessingAmmo = true;
                break;
        }

        if (processSuccess)
        {
            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            ShowFeedMessage(
                   L("FeedSender", "Online Shop"),
                   L("FeedPurchaseSubject", "Mua hàng"),
                   _msgSuccess[_rng.Next(_msgSuccess.Length)] +
                   L("FeedThanksSuffix", " Dù gì thì cũng cảm ơn bạn đã mua hàng nhé! Nhớ quay lại mua tiếp nha!")
            );

            // <<< ADDED: Reset counters for stats (Health/Armor/Special) >>>
            if (_pendingType == PendingType.Health || _pendingType == PendingType.Armor || _pendingType == PendingType.Special)
            {
                ResetBackCountersForType(_pendingType);
            }
        }
        else
        {
            Notification.Show(_msgFail[_rng.Next(_msgFail.Length)]);
        }

        _lastInteractionGameTime = Game.GameTime;
        ClearPending(false, true);
    }

    // ----------------------- Hire Bodyguard (NEW) -----------------------
    private void AcceptHireBodyguard()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
            {
                ClearPending(true, false);
                return;
            }

            if (_pendingType != PendingType.HireBodyguard)
            {
                ClearPending(true, false);
                return;
            }

            int cost = _peCo;

            if (Game.Player.Money < cost)
            {
                // Not enough money
                Notification.Show(string.Format(_msgNoMoney[_rng.Next(_msgNoMoney.Length)], cost));
                ClearPending(true, false);
                return;
            }

            // Deduct money, accumulate spending
            Game.Player.Money -= cost;
            AddToSpendingAccumulator(cost);

            // Try to call CompanionCloner if available
            bool spawnSucceeded = false;
            try
            {
                // Attempt direct static Instance call (requires CompanionCloner.Instance to be set)
                var ccType = Type.GetType("CompanionCloner");
                if (ccType != null)
                {
                    // Try to get static Instance field
                    var instField = ccType.GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (instField != null)
                    {
                        var inst = instField.GetValue(null);
                        if (inst != null)
                        {
                            // Try to find TrySpawnClone method (public)
                            var m = ccType.GetMethod("TrySpawnClone", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (m != null)
                            {
                                m.Invoke(inst, null);
                                spawnSucceeded = true;
                            }
                            else
                            {
                                // try non-public fallback
                                var m2 = ccType.GetMethod("TrySpawnClone", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                if (m2 != null)
                                {
                                    m2.Invoke(inst, null);
                                    spawnSucceeded = true;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                spawnSucceeded = false;
            }

            // If CompanionCloner not present or call failed, fallback: spawn simple companion locally
            if (!spawnSucceeded)
            {
                try
                {
                    SpawnSimpleCompanion(player);
                    spawnSucceeded = true;
                }
                catch
                {
                    spawnSucceeded = false;
                }
            }

            // Feedback
            if (spawnSucceeded)
            {
                PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                ShowFeedMessage(
                      L("FeedSender", "Online Shop"),
                      L("FeedPurchaseSubject", "Mua hàng"),
                      _msgSuccess[_rng.Next(_msgSuccess.Length)] +
                      L("FeedThanksSuffix", " Dù gì thì cũng cảm ơn bạn đã mua hàng nhé! Nhớ quay lại mua tiếp nha!")
                );
            }
            else
            {
                // refund if spawn completely failed to avoid losing player's money silently
                Game.Player.Money += cost;
                // hoàn lại bộ đếm vì giao dịch thất bại
                _spendingAccumulator.SubtractFromSpendingAccumulator(cost);
                Notification.Show(_msgFail[_rng.Next(_msgFail.Length)]);
            }

            _lastInteractionGameTime = Game.GameTime;
            ClearPending(false, spawnSucceeded);
        }
        catch
        {
            ClearPending(false, false);
        }
    }

    private void SpawnSimpleCompanion(Ped player)
    {
        if (player == null || !player.Exists()) return;

        // spawn position: behind player a bit
        Vector3 spawnPos = player.GetOffsetPosition(new Vector3(0.6f, -1.2f, 0.0f));

        // model
        var model = player.Model;
        int waited = 0;
        if (!model.IsLoaded)
        {
            model.Request(500);
            while (!model.IsLoaded && waited < 2000)
            {
                Script.Wait(50);
                waited += 50;
            }
        }

        var clone = World.CreatePed(model, spawnPos);
        if (clone == null || !clone.Exists()) throw new Exception("Spawn fallback failed");

        try
        {
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, clone.Handle, true, true);
        }
        catch { }

        try
        {
            Function.Call(Hash.SET_PED_MAX_HEALTH, clone.Handle, 300);
            Function.Call(Hash.SET_ENTITY_HEALTH, clone.Handle, Math.Min(player.Health, 300));
            Function.Call(Hash.SET_PED_ARMOUR, clone.Handle, 300);
        }
        catch { }

        // make friendly to player
        try
        {
            var playerGroup = Game.Player.Character.RelationshipGroup;
            // create a transient relationship group name unique to avoid conflicts
            var relName = "ir_companion_group";
            RelationshipGroup rel = null;
            try
            {
                rel = World.AddRelationshipGroup(relName);
                // set friendly
                rel.SetRelationshipBetweenGroups(playerGroup, Relationship.Companion, true);
            }
            catch { /* ignore - not critical */ }
        }
        catch { }

        // basic combat attributes (lightweight)
        try
        {
            Function.Call(Hash.SET_PED_ACCURACY, clone.Handle, 60);
            Function.Call(Hash.SET_PED_COMBAT_ABILITY, clone.Handle, 1);
            Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, clone.Handle, 1);
            Function.Call(Hash.SET_PED_COMBAT_RANGE, clone.Handle, 1);
            clone.Weapons.Give(WeaponHash.Pistol, 200, true, true);
        }
        catch { }

        // issue follow-to-offset (persisting)
        try
        {
            Vector3 offset = new Vector3(0f, -1.5f - (0.5f * (_vehicles?.Count ?? 0) % 3), 0f);
            Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY, clone.Handle, player.Handle, offset.X, offset.Y, offset.Z, 10000, -1, 2.5f, true);
        }
        catch { }
    }

    // ----------------------- Cancel / Expire -----------------------
    private void CancelPending()
    {
        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
        {
            ClearPending(true, false);
            return;
        }

        int charged = 0;
        if (Game.Player.Money >= _currentCancelFee)
        {
            Game.Player.Money -= _currentCancelFee;
            charged = _currentCancelFee;
            AddToSpendingAccumulator(charged); // <<< ADDED: chỉ khi bị trừ phí hủy

            // Lấy tin nhắn ngẫu nhiên và điền số tiền vào chỗ {0}
            string randomMsg = _msgCancelCharged[_rng.Next(_msgCancelCharged.Length)];
            Notification.Show(string.Format(randomMsg, _currentCancelFee));
        }
        else
        {
            // Lấy tin nhắn ngẫu nhiên khi không đủ tiền
            string randomMsg = _msgCancelNoMoney[_rng.Next(_msgCancelNoMoney.Length)];
            Notification.Show(string.Format(randomMsg, _currentCancelFee));
        }

        // Logic tăng phí (giữ nguyên)
        if (charged == CancelFeeCap)
        {
            _currentCancelFee = 60;
        }
        else
        {
            int pct = _rng.Next(1, 51);
            double increased = _currentCancelFee * (1.0 + pct / 100.0);
            int nextFee = (int)Math.Round(increased);
            if (nextFee > CancelFeeCap) nextFee = CancelFeeCap;
            _currentCancelFee = nextFee;
        }

        _lastInteractionGameTime = Game.GameTime;

        // <<< ADDED: record back-cancel for stats-type cancels >>>
        try
        {
            // Only record "back cancels" for stats types (Health/Armor/Special)
            if (_pendingType == PendingType.Health || _pendingType == PendingType.Armor || _pendingType == PendingType.Special)
            {
                RecordBackCancel(_pendingType);
            }
        }
        catch { }

        ClearPending(false, false);
    }

    private int ApplyVehiclePriceModifiers(int basePrice, bool useTicket)
    {
        int price = Math.Max(0, basePrice);

        if (_vehOS)
        {
            price = (int)Math.Ceiling(price * _saleMultiplier);
        }

        if (useTicket)
        {
            price = ApplyVehicleDiscountTicket(price);
        }

        return Math.Max(0, price);
    }

    private void ExpirePending()
    {
        Ped player = Game.Player.Character;
        int charged = 0;
        if (player != null && player.Exists() && !player.IsDead)
        {
            if (Game.Player.Money >= _currentExpireFee)
            {
                Game.Player.Money -= _currentExpireFee;
                charged = _currentExpireFee;
                AddToSpendingAccumulator(charged); // <<< ADDED: tích lũy phí hết hạn

                // Hiện tin nhắn ngẫu nhiên khi bị trừ tiền
                string randomMsg = _msgExpireCharged[_rng.Next(_msgExpireCharged.Length)];
                Notification.Show(string.Format(randomMsg, _currentExpireFee));
            }
            else
            {
                // Hiện tin nhắn ngẫu nhiên khi được miễn phí
                string randomMsg = _msgExpireNoMoney[_rng.Next(_msgExpireNoMoney.Length)];
                Notification.Show(string.Format(randomMsg, _currentExpireFee));
            }
        }

        // Logic tăng phí cho lần sau
        if (charged == ExpireFeeCap) _currentExpireFee = 30;
        else
        {
            int pct = _rng.Next(1, 51);
            double increased = _currentExpireFee * (1.0 + pct / 100.0);
            int nextExpire = (int)Math.Round(increased);
            if (nextExpire > ExpireFeeCap) nextExpire = ExpireFeeCap;
            _currentExpireFee = nextExpire;
        }

        _lastInteractionGameTime = Game.GameTime;
        ClearPending(false, false);
    }

    private void PlayFrontendSound(string soundName, string soundSet)
    {
        SafeCall(() =>
        {
            Audio.PlaySoundFrontend(soundName, soundSet);
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, soundName, soundSet, true);
        });
    }

    // ----------------------- Privilege Credits phone contact -----------------------
    private void EnsurePrivilegeCreditsContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_privilegeCreditsPhoneInstance, phone))
            {
                _privilegeCreditsPhoneInstance = phone;
                _privilegeCreditsContactAdded = false;
            }

            if (_privilegeCreditsContactAdded)
                return;

            string mazeBankName = ContactName("Contact_MazeBank", "Maze Bank");

            if (phone.Contacts.Any(c =>
                string.Equals(c.Name, mazeBankName, StringComparison.OrdinalIgnoreCase)))
            {
                _privilegeCreditsContactAdded = true;
                return;
            }

            var creditContact = new iFruitContact(mazeBankName)
            {
                Active = true,
                DialTimeout = REWARD_REDEEM_CALL_DURATION_MS,
                Bold = false,
                Icon = ContactIcon.MazeBank
            };

            creditContact.Answered += OnPrivilegeCreditsContactAnswered;
            phone.Contacts.Add(creditContact);

            _privilegeCreditsContactAdded = true;
        }
        catch (Exception ex) { }
    }

    private void OnPrivilegeCreditsContactAnswered(iFruitContact sender)
    {
        try
        {
            OpenRewardRedeemMenu();

            try
            {
                CustomiFruit.GetCurrentInstance()?.Close(0);
            }
            catch { }
        }
        catch (Exception ex) { }
    }

    // ----------------------- Ammunation phone contact -----------------------
    private void EnsureAmmunationContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            // If the phone instance changed, allow re-registering.
            if (!ReferenceEquals(_ammunationPhoneInstance, phone))
            {
                _ammunationPhoneInstance = phone;
                _ammunationContactAdded = false;
            }

            if (_ammunationContactAdded)
                return;

            string ammoName = ContactName("Contact_AmmuNation", "AmmuNation");

            if (phone.Contacts.Any(c =>
                string.Equals(c.Name, ammoName, StringComparison.OrdinalIgnoreCase)))
            {
                _ammunationContactAdded = true;
                return;
            }

            var ammoContact = new iFruitContact(ammoName)
            {
                Active = true,
                DialTimeout = AMMUNATION_CALL_DURATION_MS,
                Bold = false,
                Icon = ContactIcon.Ammunation
            };

            ammoContact.Answered += OnAmmunationContactAnswered;
            phone.Contacts.Add(ammoContact);

            _ammunationContactAdded = true;
        }
        catch (Exception ex) { }
    }

    private void OnAmmunationContactAnswered(iFruitContact sender)
    {
        try
        {
            TriggerAmmunationWeaponOffer();

            try
            {
                CustomiFruit.GetCurrentInstance()?.Close(0);
            }
            catch { }
        }
        catch (Exception ex) { }
    }

    // ----------------------- Legendary Motorsport phone contact -----------------------
    private void EnsureLegendaryMotorsportContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_legendaryMotorSportPhoneInstance, phone))
            {
                _legendaryMotorSportPhoneInstance = phone;
                _legendaryMotorSportContactAdded = false;
            }

            if (_legendaryMotorSportContactAdded)
                return;

            string legendaryName = ContactName("Contact_LegendaryMotorsport", "Legendary Motorsport");

            if (phone.Contacts.Any(c =>
                string.Equals(c.Name, legendaryName, StringComparison.OrdinalIgnoreCase)))
            {
                _legendaryMotorSportContactAdded = true;
                return;
            }

            var lmContact = new iFruitContact(legendaryName)
            {
                Active = true,
                DialTimeout = LEGENDARY_MOTORSPORT_CALL_DURATION_MS,
                Bold = false,
                Icon = ContactIcon.LegendaryMotorsport
            };

            lmContact.Answered += OnLegendaryMotorsportContactAnswered;
            phone.Contacts.Add(lmContact);

            _legendaryMotorSportContactAdded = true;
        }
        catch (Exception ex) { }
    }

    private void OnLegendaryMotorsportContactAnswered(iFruitContact sender)
    {
        try
        {
            TriggerLegendaryMotorsportSearch();

            try
            {
                CustomiFruit.GetCurrentInstance()?.Close(0);
            }
            catch { }
        }
        catch (Exception ex) { }
    }

    private void TriggerLegendaryMotorsportSearch()
    {
        try
        {
            if (IsHelpBoxCooldownActive())
            {
                ShowHelpCooldownMessage();
                return;
            }

            if (_pendingType != PendingType.None)
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("SearchBlockedByPending", "~HUD_COLOUR_DEGEN_YELLOW~Hiện có cửa sổ mua hàng khác đang mở. Hủy/xác nhận trước khi tìm phương tiện.~h~"),
                    3000);
                return;
            }

            StartVehicleSearch();
        }
        catch (Exception ex) { }
    }

    private void EnsureEliteProtectionContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_eliteProtectionPhoneInstance, phone))
            {
                _eliteProtectionPhoneInstance = phone;
                _eliteProtectionContactAdded = false;
            }

            if (_eliteProtectionContactAdded)
                return;

            string eliteName = ContactName("Contact_EliteProtectionUnit", "Elite Protection Unit");

            if (phone.Contacts.Any(c =>
                string.Equals(c.Name, eliteName, StringComparison.OrdinalIgnoreCase)))
            {
                _eliteProtectionContactAdded = true;
                return;
            }

            var eliteContact = new iFruitContact(eliteName)
            {
                Active = true,
                DialTimeout = BODYGUARDS_CALL_DURATION_MS,
                Bold = false,
                Icon = ContactIcon.MP_Merryweather
            };

            eliteContact.Answered += OnEliteProtectionContactAnswered;
            phone.Contacts.Add(eliteContact);

            _eliteProtectionContactAdded = true;
        }
        catch (Exception ex) { }
    }

    private void OnEliteProtectionContactAnswered(iFruitContact sender)
    {
        try
        {
            TriggerEliteProtectionOffer();

            try
            {
                CustomiFruit.GetCurrentInstance()?.Close(0);
            }
            catch { }
        }
        catch (Exception ex) { }
    }

    private void TriggerEliteProtectionOffer()
    {
        try
        {
            if (IsHelpBoxCooldownActive())
            {
                ShowHelpCooldownMessage();
                return;
            }

            if (_pendingType != PendingType.None)
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("HireBodyguardBlockedByPending", "~HUD_COLOUR_DEGEN_YELLOW~Hiện có cửa sổ mua hàng khác đang mở. Hủy/xác nhận trước khi thuê vệ sĩ."),
                    3000);
                return;
            }

            long playerMoney = Game.Player.Money;
            if (playerMoney < 75000)
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("HireBodyguardNotEnough", "~HUD_COLOUR_DEGEN_RED~Chưa đủ điều kiện để thuê vệ sĩ!!!"),
                    3000);
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            double raw = playerMoney * 0.12;
            int computed = (int)Math.Round(raw, 0, MidpointRounding.AwayFromZero);
            if (computed > 1500000) computed = 1500000;

            _bodyguardPendingCost = computed;

            _pendingType = PendingType.HireBodyguard;
            _peCo = computed;
            _peMesTem = L("HireBodyguard_PendingTitle", "~b~~h~Thuê Vệ Sĩ~h~~s~") + "\n" +
            L("HireBodyguard_PendingBody", "Bạn có chắc muốn thuê vệ sĩ không?");
            _pendingExpiryGameTime = Game.GameTime + PendingDurationMs;
            SplitPendingTemplate(_peMesTem);
            Interval = 0;
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch (Exception ex) { }
    }

    private void TriggerAmmunationWeaponOffer()
    {
        try
        {
            if (IsHelpBoxCooldownActive())
            {
                ShowHelpCooldownMessage();
                return;
            }

            if (_pendingType != PendingType.None)
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("WeaponOfferBlockedByPending", "~HUD_COLOUR_DEGEN_YELLOW~Hiện có cửa sổ mua hàng khác đang mở. Hủy/xác nhận trước khi tiếp tục."),
                    3000);
                return;
            }

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return;

            if (_weaponLocked)
            {
                string lockedMsg = string.Format(L("LockedWeaponMessage", "~b~~h~MỞ KHÓA~s~\n" +
                    "Tính năng này hiện đang bị khóa. Bạn cần trả ${0:N0} để mở khóa!\n" +
                    "{1} để mở khóa\n" +
                    "{2} để hủy"),
                    UnlockFeeWeapon, E, LFT);
                StartPendingCustom(PendingType.LockedWeapon, UnlockFeeWeapon, lockedMsg, PendingDurationMs);
                return;
            }

            // Reuse the existing weapon-offer flow unchanged.
            HandleCtrlLWeaponOffer(player);
        }
        catch (Exception ex) { }
    }
}