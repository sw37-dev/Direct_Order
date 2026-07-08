using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

public partial class PersistentManager : Script
{
    private static string L(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    // --- Thay thế phần khai báo Folder / file paths ---
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PersistentManager"
    );

    private static readonly string FleecaCollateralStateRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "GTA V Mods", "Fleeca Bank Loan");

    private static readonly string DestroyedVehiclesFile = Path.Combine(DataFolder, "persistent_destroyed_vehicles.txt");

    private static readonly List<PersistentVehicle> _destroyedVehicles = new List<PersistentVehicle>();
    private static volatile bool _destroyedVehiclesDirty = false;

    private const int MAX_DESTROYED_VEHICLES_PER_CHAR = 10;
    private const double INSURANCE_RESTORE_PERCENT = 0.07;

    private const int MIN_CUSTOM_ICON_ID = 0;
    private const int MAX_CUSTOM_ICON_ID = 866;

    private const float MIN_CUSTOM_BLIP_SCALE = 0.50f;
    private const float MAX_CUSTOM_BLIP_SCALE = 2.00f;
    private const float DEFAULT_CUSTOM_BLIP_SCALE = 1.00f;

    // --- MMI Insurance menu state ---
    private NativeMenu _mmiInsuranceMenu;
    private NativeMenu _mmiInsuranceCharacterMenu;
    private NativeMenu _mmiDestroyedVehicleMenu;

    private bool _mmiInsuranceMenuReady = false;
    private bool _mmiInsuranceCharacterMenuReady = false;
    private bool _mmiDestroyedVehicleMenuReady = false;

    private NativeCheckboxItem _mmiStandardRestoreItem;
    private NativeItem _mmiRestoreDestroyedItem;
    private NativeItem _mmiInsuranceConfirmItem;
    private NativeItem _mmiInsuranceCancelItem;

    private int _mmiSelectedOwnerHash = 0;
    private readonly List<PersistentVehicle> _mmiDestroyedVehicleEntries = new List<PersistentVehicle>();
    private readonly List<NativeCheckboxItem> _mmiDestroyedVehicleCheckboxItems = new List<NativeCheckboxItem>();
    private bool _mmiDestroyedUiSync = false;

    private static readonly string WeaponsFile = Path.Combine(DataFolder, "persistent_weapons.txt");
    private static readonly string VehiclesFile = Path.Combine(DataFolder, "persistent_vehicles.txt");
    private const int MAX_BACKUPS = 1; // số bản backup giữ lại
    private const bool ENABLE_LOG = false;
    private static string MMI_CONTACT_NAME => L("MMI_ContactName", "Mors Mutual Insurance");
    private const int MMI_CALL_DURATION_MS = 2000;
    
    private static bool _mmiRestorePending = false;
    private static int _mmiRestoreDueTime = 0;
    private bool _mmiContactAdded = false;

    private static string ASSET_RECOVERY_CONTACT_NAME => L("ARC_ContactName", "Asset Recovery Center");
    private const int ASSET_RECOVERY_CALL_DURATION_MS = 2000;

    private static string PM_MenuInsuranceTitle => L("PM_MenuInsuranceTitle", "Insurance");
    private static string PM_MenuInsuranceSubtitle => L("PM_MenuInsuranceSubtitle", "DỊCH VỤ BẢO HIỂM");
    private static string PM_MenuCharacterSubtitle => L("PM_MenuCharacterSubtitle", "CHỌN NHÂN VẬT");
    private static string PM_MenuDestroyedSubtitle => L("PM_MenuDestroyedSubtitle", "PHƯƠNG TIỆN ĐÃ HỎNG");
    private static string PM_MenuNoDestroyedVehicles => L("PM_MenuNoDestroyedVehicles", "Chưa có xe đang chờ bảo hiểm.");
    private static string PM_MenuConfirmService => L("PM_MenuConfirmService", "Xác nhận dịch vụ");
    private static string PM_MenuCancelInsurance => L("PM_MenuCancelInsurance", "Hủy dịch vụ bảo hiểm MMI");
    private static string PM_MenuRestoreWeapons => L("PM_MenuRestoreWeapons", "1. Khôi phục phương tiện và vũ khí");
    private static string PM_MenuRestoreDestroyed => L("PM_MenuRestoreDestroyed", "2. Khôi phục phương tiện đã hỏng");
    private static string PM_MenuFranklin => L("PM_MenuFranklin", "1. Franklin Clinton");
    private static string PM_MenuMichael => L("PM_MenuMichael", "2. Michael De Santa");
    private static string PM_MenuTrevor => L("PM_MenuTrevor", "3. Trevor Philips");
    private static string PM_MenuCancelCharacter => L("PM_MenuCancelCharacter", "Hủy chọn nhân vật");
    private static string PM_MenuConfirmRecoverVehicle => L("PM_MenuConfirmRecoverVehicle", "Xác nhận lấy lại phương tiện");
    private static string PM_MenuCancelDestroyed => L("PM_MenuCancelDestroyed", "Hủy bỏ bảo hiểm MMI");
    private static string PM_MenuInsuranceDescFormat => L("PM_MenuInsuranceDescFormat", "{0} với Phí bảo hiểm là ${1:N0}");
    private static string PM_FeedInsuranceSender => L("PM_FeedInsuranceSender", "Mors Mutual");
    private static string PM_FeedInsuranceSubject => L("PM_FeedInsuranceSubject", "Bảo hiểm");
    private static string PM_FeedInsuranceBody => L("PM_FeedInsuranceBody", "Đã khôi phục {0} phương tiện. Phí bảo hiểm: ${1:N0}.");
    private static string PM_InsuranceNotEnoughMoney => L("PM_InsuranceNotEnoughMoney", "Không đủ tiền bảo hiểm. Cần ${0:N0}.");
    private static string PM_InsuranceRecoveryFailed => L("PM_InsuranceRecoveryFailed", "Khôi phục phương tiện đã hỏng thất bại.");
    private static string PM_InsuranceChooseOne => L("PM_InsuranceChooseOne", "Hãy chọn một dịch vụ trước.");
    private static string PM_InsuranceChooseAtLeastOne => L("PM_InsuranceChooseAtLeastOne", "Hãy chọn ít nhất một phương tiện.");
    private static string PM_VehicleRestoredName => L("PM_VehicleRestoredName", "Phương tiện đã khôi phục");

    private static bool _assetRecoveryRestorePending = false;
    private static int _assetRecoveryRestoreDueTime = 0;
    private bool _assetRecoveryContactAdded = false;

    private static readonly Dictionary<int, List<PersistentWeapon>> _characterWeapons = new Dictionary<int, List<PersistentWeapon>>();
    private static readonly List<PersistentVehicle> _persistVehicles = new List<PersistentVehicle>();

    // ===== Patch: Refund percent + vehicle name lookup =====
    private const double REFUND_PERCENT = 0.45;

    // Bảng ánh xạ modelHash -> tên hiển thị
    private static readonly Dictionary<uint, string> _vehicleNameMap = new Dictionary<uint, string>()
    {
    { 0x46699F47u, "Akula" },
    { 0x20314B42u, "Apocalypse ZR380" },
    { 0x81BD2ED0u, "Avenger" },
    { 0xB779A091u, "Adder" },
    { 0xED552C74u, "Autarch" },
    { 0x9A474B5Eu, "Avisa" },
    { 0xFE5F0722u, "Apocalypse Deathbike" },
    { 0x31F0B376u, "Annihilator" },
    { 0xF7004C86u, "Atomic Blimp" },
    { 0x2189D250u, "APC" },
    { 0x64DE07A1u, "B-11 Strikeforce" },
    { 0xD8A914D3u, "Banshee GTS" },
    { 0x4BFCF28Bu, "Bestia GTS" },
    { 0xA1355F67u, "Blazer Aqua" },
    { 0x25C5AF13u, "Banshee 900R" },
    { 0xFD231729u, "Blazer Lifeguard" },
    { 0x43779C54u, "BMX" },
    { 0xF9300CC5u, "Bati 801" },
    { 0xCADD5D2Du, "Bati 801RR" },
    { 0x05283265u, "BF400" }, // 0x5283265u in list (normalized leading zero)
    { 0x55365079u, "Brioso 300" },
    { 0x09E478B3u, "Buffalo EVX" },
    { 0xB1D95DA0u, "Cheetah" },
    { 0xC972A155u, "Champion" },
    { 0x52FF9437u, "Cyclone" },
    { 0x00ABB0C0u, "Carbon RS" }, // Carbon RS (single entry to avoid duplicate key)
    { 0x53174EEFu, "Cargobob" },
    { 0xC1AE4D16u, "Comet" },
    { 0x991EFC04u, "Comet S2" },
    { 0xE384DD25u, "Conada" },
    { 0x067BC037u, "Coquette" }, // 0x67BC037u in list (normalized)
    { 0x98F65A5Eu, "Coquette D10" },
    { 0x0796B7A5u, "Coquette D5" }, // 0x796B7A5u in list (normalized)
    { 0x79C12D73u, "Coquette D10 Pursuit" },
    { 0x5B531351u, "Deity" },
    { 0x586765FBu, "Deluxo" },
    { 0xF1B44F44u, "Diabolus" },
    { 0x107F392Cu, "Dinghy" },
    { 0xD876DBE2u, "Desert Raid" },
    { 0x9C669788u, "Double-T" },
    { 0xE882E5F6u, "Dubsta" },
    { 0x5EE005DAu, "Deveste Eight" },
    { 0xE5B3ACA1u, "Dominator GT" },
    { 0x4EE74355u, "Emerus" },
    { 0x8198AEDCu, "Entity XXR" },
    { 0x6838FC1Du, "Entity MT" },
    { 0x42D623C7u, "Envisage" },
    { 0xE4C8C4Du, "F-160 Raiju" },
    { 0x25676EAFu, "FCR 1000" },
    { 0x9DC66994u, "FIB" },
    { 0x9229E4EBu, "Faggio Sport" },
    { 0x5502626Cu, "FMJ" },
    { 0xC3F57329u, "FMJ MK V" },
    { 0x92EF6E04u, "811" },
    { 0x432EA949u, "FIB (Granger)" },
    { 0xCE23D3BFu, "Fixter" },
    { 0x93F09558u, "Future Shock Deathbike" },
    { 0x8911B9F5u, "Feltzer" },
    { 0xFD707EDEu, "FH-1 Hunter" },
    { 0x843B73DEu, "Fieldmaster" },
    { 0x5BEB3CE0u, "Future Shock Scarab" },
    { 0x817AFAADu, "Gauntlet" },
    { 0xDCBC1C3Bu, "Go Go Monkey Blista" },
    { 0x2C2C2324u, "Gargoyle" },
    { 0xF0C2A91Fu, "Hakuchou Drag" },
    { 0x39D6E83Fu, "Hydra" },
    { 0xB44F0582u, "Hot Rod Blazer" },
    { 0xC3F25753u, "Howard NX-25" },
    { 0x4B6C568Au, "Hakuchou" },
    { 0xFE141DA6u, "Half-track" },
    { 0xA9EC907Bu, "Ignus" },
    { 0xCA7C4AE9u, "Inductor" },
    { 0x5649FF41u, "Itali GTO Stinger TT" },
    { 0xBB78956Au, "Itali RSX" },
    { 0x85E8E76Bu, "Itali GTB" },
    { 0xE33A477Bu, "Itali GTB Custom" },
    { 0x1B8165D3u, "Jubilee" },
    { 0xB2A716A3u, "Jester" },
    { 0x5882160Fu, "Jester RR Widebody" },
    { 0x4FAF0D70u, "Kosatka" },
    { 0xD86A0247u, "Krieger" },
    { 0x206D1B68u, "Khamelion" },
    { 0xEF813606u, "Kurtz 31 Patrol" },
    { 0xC07107EEu, "Kraken" },
    { 0xFF5968CDu, "LM87" },
    { 0x1BF8D381u, "Lifeguard" },
    { 0x26321E67u, "Lectro" },
    { 0xCD93A7DBu, "Liberator" },
    { 0xC8163646u, "Luiva" },
    { 0x9A9EB7DEu, "LF-22 Starling" },
    { 0xC7E55211u, "Locust" },
    { 0xB79F589Eu, "Luxor Deluxe" },
    { 0x6EF89CCCu, "Longfin" },
    { 0xC1CE1183u, "Marquis" },
    { 0xB53C6C52u, "Mini Tank" },
    { 0xDA5819A3u, "Massacro (Racecar)" },
    { 0xD35698EFu, "Mogul" },
    { 0x79DD18AEu, "Menacer" },
    { 0x70241EEAu, "Niobe" },
    { 0x9F6ED5A2u, "Neo" },
    { 0x91CA96EEu, "Neon" },
    { 0xDA288376u, "Nemesis" },
    { 0xAE12C99Cu, "Nightmare Deathbike" },
    { 0x3DA47243u, "Nero" },
    { 0xA8E38B01u, "9F Cabrio" },
    { 0x4131F378u, "Nero Custom" },
    { 0xA0438767u, "Nightblade" },
    { 0x34B82784u, "Oppressor" },
    { 0x7B54A9D3u, "Oppressor Mk 2" },
    { 0x185E2FF3u, "Outlaw" },
    { 0x767164D6u, "Osiris" },
    { 0xB39B0AE6u, "P-996 LAZER" },
    { 0x9734F3EAu, "Penetrator" },
    { 0xE2E7D4ABu, "Police Predator" },
    { 0x33B98FE2u, "Pariah" },
    { 0x3DC92356u, "P-45 Nokota" },
    { 0x9DAE1398u, "Phantom Wedge" },
    { 0xD80F4A44u, "Patriot Mil-Spec" },
    { 0xF2AE3F81u, "Pipistrello" },
    { 0x75599EA7u, "Pizza Boy" },
    { 0xFDEFAEC3u, "Police Bike" },
    { 0xE644E480u, "Panto" },
    { 0x2C33B46Eu, "Park Ranger" },
    { 0xA46462F7u, "Police Rancher" },
    { 0xAD5E30D7u, "Powersurge" },
    { 0x71FA16EAu, "Police Cruiser" },
    { 0xAD6065C0u, "Pyro" },
    { 0x8B213907u, "R88" },
    { 0xEEF345ECu, "RC Bandito" },
    { 0xD7C56D39u, "Raptor" },
    { 0x2EA68690u, "Rhino Tank" },
    { 0x04F48FC4u, "Rebla GTS" }, // normalized 0x4F48FC4u
    { 0xCEB28249u, "Ramp Buggy (DUNE4)" },
    { 0xED62BFA9u, "Ramp Buggy (DUNE5)" },
    { 0xE00BADABu, "Ratel" },
    { 0x0DF381E5u, "Reaper" },
    { 0x3AF76F4Au, "Rocket Voltic" },
    { 0xC5DD6967u, "Rogue" },
    { 0xCABD11E8u, "Ruffian" },
    { 0x76D7C404u, "Reever" },
    { 0xFE0A508Cu, "RM-10 Bombushka" },
    { 0xEA313705u, "RO-86 Alkonost" },
    { 0x381E10BDu, "Ruiner 2000" },
    { 0x58E316C7u, "Sanctus" },
    { 0xD4AE63D9u, "Sea Sparrow" },
    { 0x97398A4Bu, "Seven-70" },
    { 0x2E3967B0u, "SM722" },
    { 0x400F5147u, "Specter Custom" },
    { 0x2A54C47Du, "SuperVolito" },
    { 0x9C5E5644u, "SuperVolito Carbon" },
    { 0x2EF89E46u, "Sanchez (SANCHEZ01)" },
    { 0xA960B13Eu, "Sanchez (SANCHEZ02)" },
    { 0xE5BA6858u, "Street Blazer" },
    { 0xEF2295C9u, "Suntrap" },
    { 0x28FC5B78u, "Suzume" },
    { 0xD9F0503Du, "Scramjet" },
    { 0x4019CB4Cu, "Swift Deluxe" },
    { 0xE8983F9Fu, "Seabreeze" },
    { 0x9C32EB57u, "Stanier LE Cruiser" },
    { 0x3AF8C345u, "Sandking SWB" },
    { 0x3E48BF23u, "Skylift" },
    { 0x2C509634u, "Sovereign" },
    { 0xF4E1AA15u, "Scorcher" },
    { 0x50A6FB9Cu, "Shinobi" },
    { 0xE7D2A16Eu, "Shotaro" },
    { 0x34DBA661u, "Stromberg" },
    { 0x42BC5E19u, "Slamvan Custom" },
    { 0xEE6024BCu, "Sultan RS" },
    { 0x1FD824AFu, "Space Docker" },
    { 0xEBC24DF2u, "Swift" },
    { 0x0D17099Du, "Speedo" },
    { 0x11F58A5Au, "Stryder" },
    { 0x6322B39Au, "T20" },
    { 0x58CDAF30u, "Thruster" },
    { 0x56C8A5EFu, "Toreador" },
    { 0x1044926Fu, "Tempesta" },
    { 0x3E3D1F59u, "Thrax" },
    { 0x761E2AD3u, "Titan" },
    { 0x3329757Eu, "Titan 250 D" },
    { 0xF8AB457Bu, "Turismo Omaggio" },
    { 0x7B406EFBu, "Tyrus" },
    { 0x3E2E4F8Au, "Tula" },
    { 0x185484E1u, "Turismo R" },
    { 0xE99011C2u, "Tyrant" },
    { 0x10635A0Eu, "10F Widebody" },
    { 0xA31CB573u, "Tornado Rat Rod" },
    { 0xBC5DC07Eu, "Taipan" },
    { 0x4662BCBBu, "Technical Aqua" },
    { 0x3D7C6410u, "Tezeract" },
    { 0x6D6F8F43u, "Thrust" },
    { 0xAA6F980Au, "TM-02 Khanjali" },
    { 0x96E24857u, "Ultralight" },
    { 0x8A63C7B9u, "Unmarked Cruiser" },
    { 0x7397224Cu, "Vagner" },
    { 0xB5EF4C33u, "Vigilante" },
    { 0xF79A00F7u, "Vader" },
    { 0x11CBC051u, "Verus" },
    { 0xAF599F01u, "Vindicator" },
    { 0xC4810400u, "Visione" },
    { 0x142E0DC3u, "Vacca" },
    { 0x5BFA5C4Bu, "Valkyrie MOD.0" },
    { 0x1AAD0DEDu, "Volatol" },
    { 0xAE2CC02Au, "Vivanite" },
    { 0xC58DA34Au, "Weaponized Dinghy" },
    { 0xB7D9F7F1u, "Weaponized Tampa" },
    { 0x7E8F677Fu, "X80 Proto" },
    { 0x36B4A8A9u, "XA-21" },
    { 0x95F6A2C9u, "X-treme" },
    { 0x2D3BD401u, "Z-Type" },
    { 0xAC5DF515u, "Zentorno" },
    { 0xDE05FB87u, "Zombie Chopper" },
    { 0x2714AA93u, "Zeno" },
    { 0xD757D97Du, "Zorrusso" },
    { 0x4C8DBA51u, "Zhaba" },
};

    private T SafeCall<T>(Func<T> fn, T fallback = default)
    {
        try
        {
            if (fn == null) return fallback;
            return fn();
        }
        catch
        {
            return fallback;
        }
    }

    // --- safety caps & throttles ---
    private const int MAX_PERSIST_VEHICLES = 30;      // giới hạn tổng số entry persistent vehicle
    private const int MAX_WEAPON_ENTRIES_PER_CHAR = 128; // cap weapons per character to avoid blowup
    private const int VEHICLE_POSITIONS_PER_TICK = 5; // process 5 vehicles / tick (throttle)
    private const int BLIPS_PER_TICK = 20;
    private const int SAVE_BACKOFF_MS = 10000;         // auto-save backoff 10s (thay cho 5s)
    private int _vehiclePositionsCursor = 0;
    private int _blipsCursor = 0;
    private int _lastSaveAttempt = 0;

    // per-character cap: mỗi nhân vật chỉ được lưu tối đa N xe
    private const int MAX_VEHICLES_PER_CHAR = 10;

    // add these near the top with other private fields
    private int _weaponFullScanCounter = 0;
    private const int WEAPON_FULL_SCAN_TICKS = 10; // mỗi 10 tick (~10s) làm 1 full-scan
    private const int MAX_COMPONENTS_PER_WEAPON = 16; // cap components để tránh blowup

    private bool _loaded = false;
    private int _readyTime;
    private int _lastPedHandle = 0;

    private static volatile bool _weaponsDirty = false;
    private static volatile bool _vehiclesDirty = false;
    private static volatile bool _dealershipMenuDirty = false;

    private int _lastSaveTime = 0;
    private int _maintenanceClock = 0;
    private int _weaponIdleCounter = 0;

    // NEW: track player vehicle state to detect exit events
    private bool _playerWasInVehicle = false;
    private Vehicle _lastPlayerVehicle = null;

    public PersistentManager()
    {
        Interval = 1000;
        Tick += OnTick;
        KeyDown += OnKeyDown;
        _readyTime = Game.GameTime + 2500;
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading) return;

            // --- KHỐI INIT (Chạy 1 lần) ---
            if (!_loaded)
            {
                if (Game.GameTime >= _readyTime)
                {
                    _loaded = true;
                    TryLoadAll();
                    _lastPedHandle = GetCurrentPedHandleSafe();

                    try { EnforceF2OnlyAfterLoad(); }
                    catch (Exception ex) { Log("OnTick: EnforceF2OnlyAfterLoad failed: " + ex.Message); }

                    EnsureMmiContactRegistered();
                    EnsureAssetRecoveryContactRegistered();
                    CreateBlipsForAllLoadedVehicles();
                }
                else return;
            }

            // --- Dealership UI / marker logic ---
            UpdateDealershipWorldState();
            UpdateDealershipSignalState();

            if (_dealershipMenuDirty)
            {
                RefreshDealershipMenu();
                _dealershipMenuDirty = false;
            }

            bool dealershipMenuOpen = (_dealershipMenu != null && _dealershipMenu.Visible) ||
                (_dealershipDetailMenu != null && _dealershipDetailMenu.Visible) ||
                (_dealershipBulkMenu != null && _dealershipBulkMenu.Visible);

            bool mmiMenuOpen = (_mmiInsuranceMenu != null && _mmiInsuranceMenu.Visible) ||
                (_mmiInsuranceCharacterMenu != null && _mmiInsuranceCharacterMenu.Visible) ||
                (_mmiDestroyedVehicleMenu != null && _mmiDestroyedVehicleMenu.Visible);

            if (dealershipMenuOpen || mmiMenuOpen)
            {
                _menuPool.Process();
                FlushUiActions();
            }
            else
            {
                FlushUiActions();
            }

            if (dealershipMenuOpen || mmiMenuOpen)
                Interval = 0;
            else if (_mmiRestorePending)
                Interval = 0;
            else if (_dealershipSignalActive)
                Interval = 100;
            else
                Interval = _dealershipNeedsFastTick ? 0 : 1000;

            // --- KHỐI LOGIC CHÍNH ---

            Ped player = Game.Player.Character;
            int currentHandle = (player != null && player.Exists()) ? player.Handle : 0;

            if (currentHandle != 0 && currentHandle != _lastPedHandle)
            {
                _lastPedHandle = currentHandle;
            }

            // --- KHỐI KIỂM TRA LÊN/XUỐNG XE ĐÃ ĐƯỢC THAY THẾ ---
            try
            {
                bool currentlyInVehicle = (player != null && player.Exists() && player.IsInVehicle());
                Vehicle currentVehicle = currentlyInVehicle ? player.CurrentVehicle : null;

                if (currentlyInVehicle)
                {
                    // Vừa mới lên xe, hoặc xe hiện tại khác với xe lưu trước đó
                    if (!_playerWasInVehicle || _lastPlayerVehicle == null || _lastPlayerVehicle.Handle != currentVehicle.Handle)
                    {
                        OnPlayerEnteredVehicle(currentVehicle);
                    }

                    _lastPlayerVehicle = currentVehicle;
                    _playerWasInVehicle = true;
                }
                else
                {
                    if (_playerWasInVehicle)
                    {
                        if (_lastPlayerVehicle != null)
                        {
                            OnPlayerExitedVehicle(_lastPlayerVehicle);
                        }

                        _playerWasInVehicle = false;
                        _lastPlayerVehicle = null;
                    }
                }
            }
            catch (Exception ex) { Log("OnTick: vehicle enter/exit detect error: " + ex.Message); }

            UpdateVehiclePositionsLogic();
            UpdateCurrentWeaponState();

            UpdateMmiContactCall();
            EnsureAssetRecoveryContactRegistered();
            UpdateAssetRecoveryContactCall();

            _maintenanceClock++;
            if (_maintenanceClock >= 3)
            {
                UpdatePersistentVehicleState();
                UpdateBlips();
                _maintenanceClock = 0;
            }

            // Thay đổi điều kiện kiểm tra bao gồm cả _destroyedVehiclesDirty tại đây
            if ((_weaponsDirty || _vehiclesDirty || _destroyedVehiclesDirty) && (Game.GameTime - _lastSaveTime > SAVE_BACKOFF_MS))
            {
                if (Game.GameTime - _lastSaveAttempt > SAVE_BACKOFF_MS)
                {
                    _lastSaveAttempt = Game.GameTime;

                    if (_weaponsDirty)
                    {
                        try { SaveWeaponFileInternal(); }
                        catch (Exception ex) { Log("OnTick: SaveWeaponFileInternal failed: " + ex.Message); }
                    }

                    if (_vehiclesDirty)
                    {
                        try { SaveVehiclesFileInternal(); }
                        catch (Exception ex) { Log("OnTick: SaveVehiclesFileInternal failed: " + ex.Message); }
                    }

                    // Thêm logic xử lý lưu xe bị hủy tại đây
                    if (_destroyedVehiclesDirty)
                    {
                        try { SaveDestroyedVehiclesFileInternal(); }
                        catch (Exception ex) { Log("OnTick: SaveDestroyedVehiclesFileInternal failed: " + ex.Message); }
                    }

                    _lastSaveTime = Game.GameTime;
                }
            }
        }
        catch (Exception ex)
        {
            Log("OnTick exception: " + ex.ToString());
        }
    }

    private static string NormalizePlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return "";
        return new string(plate.Trim().Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
    }

    private static string BuildVehicleKey(PersistentVehicle pv)
    {
        try
        {
            if (pv == null) return "";

            string normalizedPlate = NormalizePlate(pv.Plate);
            if (!string.IsNullOrEmpty(normalizedPlate))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|{1:X}|PLATE|{2}",
                    pv.OwnerModelHash,
                    pv.ModelHash,
                    normalizedPlate);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1:X}|POS|{2:0.0}|{3:0.0}|{4:0.0}",
                pv.OwnerModelHash,
                pv.ModelHash,
                pv.Position.X,
                pv.Position.Y,
                pv.Position.Z);
        }
        catch
        {
            return "";
        }
    }

    private static int GetDefaultVehicleIconId(PersistentVehicle pv)
    {
        try
        {
            if (pv != null && pv.RuntimeVehicle != null && pv.RuntimeVehicle.Exists())
                return (int)GetVehicleBlipSprite(pv.RuntimeVehicle);

            if (pv != null)
                return (int)GetVehicleBlipSprite(pv.ModelHash);
        }
        catch { }

        return (int)BlipSprite.PersonalVehicleCar;
    }

    private static int GetEffectiveVehicleIconId(PersistentVehicle pv)
    {
        try
        {
            if (pv != null)
            {
                if (pv.PreviewIconId.HasValue && pv.PreviewIconId.Value >= MIN_CUSTOM_ICON_ID && pv.PreviewIconId.Value <= MAX_CUSTOM_ICON_ID)
                    return pv.PreviewIconId.Value;

                if (pv.CustomIconId.HasValue && pv.CustomIconId.Value >= MIN_CUSTOM_ICON_ID && pv.CustomIconId.Value <= MAX_CUSTOM_ICON_ID)
                    return pv.CustomIconId.Value;
            }
        }
        catch { }

        return GetDefaultVehicleIconId(pv);
    }

    private static BlipSprite ResolveVehicleBlipSprite(PersistentVehicle pv)
    {
        int iconId = GetEffectiveVehicleIconId(pv);
        if (iconId >= MIN_CUSTOM_ICON_ID && iconId <= MAX_CUSTOM_ICON_ID)
            return (BlipSprite)iconId;

        return GetVehicleBlipSprite(pv != null ? pv.ModelHash : 0u);
    }

    public static int GetVehicleEffectiveIconIdByKey(string vehicleKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vehicleKey))
                return -1;

            lock (_persistVehicles)
            {
                var pv = _persistVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null) return GetEffectiveVehicleIconId(pv);
            }

            lock (_destroyedVehicles)
            {
                var pv = _destroyedVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null) return GetEffectiveVehicleIconId(pv);
            }
        }
        catch { }

        return -1;
    }

    public static int? GetPersistedVehicleIconByKey(string vehicleKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vehicleKey))
                return null;

            lock (_persistVehicles)
            {
                var pv = _persistVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null) return pv.CustomIconId;
            }

            lock (_destroyedVehicles)
            {
                var pv = _destroyedVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null) return pv.CustomIconId;
            }
        }
        catch { }

        return null;
    }

    public static bool SetVehicleCustomIconByKey(string vehicleKey, int iconId, bool commit)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vehicleKey))
                return false;

            int? normalized = null;
            if (iconId >= MIN_CUSTOM_ICON_ID && iconId <= MAX_CUSTOM_ICON_ID)
                normalized = iconId;

            bool touched = false;

            lock (_persistVehicles)
            {
                var pv = _persistVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null)
                {
                    if (commit)
                    {
                        pv.CustomIconId = normalized;
                        pv.PreviewIconId = null;
                        _vehiclesDirty = true;
                    }
                    else
                    {
                        pv.PreviewIconId = normalized;
                    }

                    try { RefreshSinglePersistentVehicleBlip(pv); } catch { }
                    touched = true;
                }
            }

            lock (_destroyedVehicles)
            {
                var pv = _destroyedVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null)
                {
                    if (commit)
                    {
                        pv.CustomIconId = normalized;
                        pv.PreviewIconId = null;
                        _destroyedVehiclesDirty = true;
                    }
                    else
                    {
                        pv.PreviewIconId = normalized;
                    }

                    try { RefreshSinglePersistentVehicleBlip(pv); } catch { }
                    touched = true;
                }
            }

            if (commit)
            {
                try
                {
                    if (_vehiclesDirty) SaveVehiclesFileInternal();
                }
                catch { }

                try
                {
                    if (_destroyedVehiclesDirty) SaveDestroyedVehiclesFileInternal();
                }
                catch { }
            }

            return touched;
        }
        catch
        {
            return false;
        }
    }

    public static bool ClearVehiclePreviewIconByKey(string vehicleKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vehicleKey))
                return false;

            bool touched = false;

            lock (_persistVehicles)
            {
                var pv = _persistVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null)
                {
                    pv.PreviewIconId = null;
                    try { RefreshSinglePersistentVehicleBlip(pv); } catch { }
                    touched = true;
                }
            }

            lock (_destroyedVehicles)
            {
                var pv = _destroyedVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null)
                {
                    pv.PreviewIconId = null;
                    try { RefreshSinglePersistentVehicleBlip(pv); } catch { }
                    touched = true;
                }
            }

            return touched;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContactNameMatches(string actualName, params string[] candidates)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(actualName) || candidates == null || candidates.Length == 0)
                return false;

            string actual = actualName.Trim();

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                if (string.Equals(actual, candidate.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private List<PersistentVehicle> GetOwnedVehiclesForCurrentPlayer()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return new List<PersistentVehicle>();

            int ownerHash = player.Model.Hash;

            lock (_persistVehicles)
            {
                return _persistVehicles
                    .Where(v => v.OwnerModelHash == ownerHash)
                    .ToList();
            }
        }
        catch
        {
            return new List<PersistentVehicle>();
        }
    }

    private void EnsureAssetRecoveryContactRegistered()
    {
        try
        {
            if (_assetRecoveryContactAdded)
                return;

            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            string arcName = ASSET_RECOVERY_CONTACT_NAME;

            if (phone.Contacts.Any(c =>
                ContactNameMatches(c.Name, arcName, "Asset Recovery Center")))
            {
                _assetRecoveryContactAdded = true;
                return;
            }

            var arc = new iFruitContact(arcName)
            {
                Active = true,
                DialTimeout = ASSET_RECOVERY_CALL_DURATION_MS,
                Bold = false,
                Icon = ContactIcon.BankOfLiberty,
            };

            arc.Answered += OnAssetRecoveryContactAnswered;
            phone.Contacts.Add(arc);

            _assetRecoveryContactAdded = true;
            Log("Asset Recovery Center contact registered into iFruitAddon2 contacts.");
        }
        catch (Exception ex)
        {
            Log("EnsureAssetRecoveryContactRegistered failed: " + ex);
        }
    }

    private void OnAssetRecoveryContactAnswered(iFruitContact sender)
    {
        try
        {
            _assetRecoveryRestorePending = true;
            _assetRecoveryRestoreDueTime = Game.GameTime + 1;
        }
        catch (Exception ex)
        {
            Log("OnAssetRecoveryContactAnswered failed: " + ex);
        }
    }

    private void UpdateAssetRecoveryContactCall()
    {
        try
        {
            if (!_assetRecoveryRestorePending)
                return;

            if (Game.GameTime < _assetRecoveryRestoreDueTime)
                return;

            _assetRecoveryRestorePending = false;

            try
            {
                Game.Player.Character.Task.PutAwayMobilePhone();
            }
            catch { }

            try
            {
                var phone = CustomiFruit.GetCurrentInstance();
                phone?.Close(0);
            }
            catch { }

            TriggerAssetRecoveryCenter();
        }
        catch (Exception ex)
        {
            Log("UpdateAssetRecoveryContactCall failed: " + ex);
        }
    }

    private void TriggerAssetRecoveryCenter()
    {
        try
        {
            if ((_dealershipMenu != null && _dealershipMenu.Visible) ||
                (_dealershipDetailMenu != null && _dealershipDetailMenu.Visible) ||
                (_dealershipBulkMenu != null && _dealershipBulkMenu.Visible))
                return;

            if (IsPlayerInsideDealershipZone())
            {
                OpenDealershipMenu(PickRandomDealershipAlias());
            }
            else
            {
                GTA.UI.Screen.ShowSubtitle(
                    L("PM_Subtitle_VisitDealershipToSellVehicle",
                      "~HUD_COLOUR_DEGEN_YELLOW~Bạn cần đến đại lý để bán phương tiện"),
                    3000);

                StartDealershipSignal();
            }
        }
        catch (Exception ex)
        {
            Log("TriggerAssetRecoveryCenter failed: " + ex.ToString());
        }
    }

    // ---------------- Key handling (F2 restore) ----------------
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            // Đã chuyển trigger đại lý sang contact điện thoại
        }
        catch (Exception ex)
        {
            Log("OnKeyDown: " + ex.ToString());
        }
    }

    private void EnsureMmiContactRegistered()
    {
        try
        {
            if (_mmiContactAdded)
                return;

            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            string mmiName = MMI_CONTACT_NAME;

            if (phone.Contacts.Any(c =>
                ContactNameMatches(c.Name, mmiName, "Mors Mutual Insurance")))
            {
                _mmiContactAdded = true;
                return;
            }

            var mmi = new iFruitContact(mmiName)
            {
                Active = true,
                DialTimeout = 2000,
                Bold = false,
                Icon = ContactIcon.MP_MorsMutual,
            };

            mmi.Answered += OnMmiContactAnswered;
            phone.Contacts.Add(mmi);

            _mmiContactAdded = true;
            Log("MMI contact registered into iFruitAddon2 contacts.");
        }
        catch (Exception ex)
        {
            Log("EnsureMmiContactRegistered failed: " + ex);
        }
    }

    private void OnMmiContactAnswered(iFruitContact sender)
    {
        try
        {
            _mmiRestorePending = true;
            _mmiRestoreDueTime = Game.GameTime + MMI_CALL_DURATION_MS;

            Log("MMI call answered, scheduled restore in 2 seconds.");
        }
        catch (Exception ex)
        {
            Log("OnMmiContactAnswered failed: " + ex);
        }
    }

    private void UpdateMmiContactCall()
    {
        try
        {
            if (!_mmiRestorePending)
                return;

            if (Game.GameTime < _mmiRestoreDueTime)
                return;

            _mmiRestorePending = false;

            try
            {
                Game.Player.Character.Task.PutAwayMobilePhone();
            }
            catch { }

            try
            {
                var phone = CustomiFruit.GetCurrentInstance();
                phone?.Close(0);
            }
            catch { }

            OpenMmiInsuranceMenu();
        }
        catch (Exception ex)
        {
            Log("UpdateMmiContactCall failed: " + ex);
        }
    }

    // ================= Vũ Khí =================

    public static void RegisterCurrentWeapon()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()) return;
            Weapon currentWeapon = player.Weapons.Current;
            if (currentWeapon == null || currentWeapon.Hash == WeaponHash.Unarmed) return;
            RegisterWeaponInternal(player, currentWeapon);
        }
        catch (Exception ex) { Log("RegisterCurrentWeapon: " + ex.ToString()); }
    }

    public static void RegisterWeaponHash(uint weaponHash)
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()) return;
            if (!player.Weapons.HasWeapon((WeaponHash)weaponHash))
            {
                player.Weapons.Give((WeaponHash)weaponHash, 0, false, true);
            }
            RegisterWeaponInternal(player, player.Weapons[(WeaponHash)weaponHash]);
        }
        catch (Exception ex) { Log("RegisterWeaponHash: " + ex.ToString()); }
    }

    private static void RegisterWeaponInternal(Ped player, Weapon w)
    {
        if (player == null || w == null) return;
        int charHash = player.Model.Hash;
        lock (_characterWeapons)
        {
            try
            {
                if (!_characterWeapons.ContainsKey(charHash))
                    _characterWeapons[charHash] = new List<PersistentWeapon>();

                var list = _characterWeapons[charHash];
                var existing = list.FirstOrDefault(x => x.Hash == (uint)w.Hash);

                // Read current active components
                List<uint> currentComponents = new List<uint>();
                foreach (var comp in w.Components)
                {
                    try
                    {
                        if (comp.Active) currentComponents.Add((uint)comp.ComponentHash);
                    }
                    catch { }
                }
                if (currentComponents.Count > MAX_COMPONENTS_PER_WEAPON)
                    currentComponents = currentComponents.Take(MAX_COMPONENTS_PER_WEAPON).ToList();

                if (existing != null)
                {
                    bool changed = false;
                    if (existing.Ammo != w.Ammo) { existing.Ammo = w.Ammo; changed = true; }
                    int tint = (int)w.Tint;
                    if (existing.Tint != tint) { existing.Tint = tint; changed = true; }

                    // compare components quickly
                    if (existing.Components == null) existing.Components = new List<uint>();
                    if (existing.Components.Count != currentComponents.Count || !existing.Components.SequenceEqual(currentComponents))
                    {
                        existing.Components = new List<uint>(currentComponents);
                        changed = true;
                    }

                    if (changed) _weaponsDirty = true;
                }
                else
                {
                    if (list.Count >= MAX_WEAPON_ENTRIES_PER_CHAR)
                    {
                        Log($"RegisterWeaponInternal: character 0x{charHash:X} reached MAX_WEAPON_ENTRIES_PER_CHAR, ignoring additional weapon 0x{w.Hash:X}");
                    }
                    else
                    {
                        list.Add(new PersistentWeapon
                        {
                            Hash = (uint)w.Hash,
                            Ammo = w.Ammo,
                            Tint = (int)w.Tint,
                            Components = currentComponents
                        });
                        _weaponsDirty = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("RegisterWeaponInternal: " + ex.ToString());
            }
        }
    }

    // --- Patch: Refresh a PersistentVehicle from its runtime Vehicle (best-effort, safe) ---
    private static void RefreshPersistentFromRuntime(PersistentVehicle pv)
    {
        if (pv == null) return;
        try
        {
            var v = pv.RuntimeVehicle;
            if (v == null) return;
            bool exists = false;
            try { exists = v.Exists(); } catch { exists = false; }
            if (!exists) return;

            // Ensure mod kit before reading
            try { Function.Call(Hash.SET_VEHICLE_MOD_KIT, v.Handle, 0); } catch { }

            // Read mods 0..49
            var mods = new Dictionary<int, int>();
            for (int modType = 0; modType <= 49; modType++)
            {
                try
                {
                    int idx = Function.Call<int>(Hash.GET_VEHICLE_MOD, v.Handle, modType);
                    if (idx >= 0) mods[modType] = idx;
                }
                catch { /* ignore per-mod errors */ }
            }
            pv.Mods = mods;

            // Turbo (toggle)
            try { pv.TurboOn = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, v.Handle, 18); } catch { }

            // Livery
            try
            {
                int liv = Function.Call<int>(Hash.GET_VEHICLE_LIVERY, v.Handle);
                if (liv >= 0) pv.SavedLivery = liv;
                else pv.SavedLivery = null;
            }
            catch { }

            // Colours
            try
            {
                var out1 = new OutputArgument();
                var out2 = new OutputArgument();
                Function.Call(Hash.GET_VEHICLE_COLOURS, v.Handle, out1, out2);
                pv.PrimaryColor = out1.GetResult<int>();
                pv.SecondaryColor = out2.GetResult<int>();
            }
            catch { }

            // Extra colours (pearl / wheel)
            try
            {
                var pearl = new OutputArgument();
                var wheel = new OutputArgument();
                Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, v.Handle, pearl, wheel);
                pv.PearlColor = pearl.GetResult<int>();
                pv.WheelColor = wheel.GetResult<int>();
            }
            catch { }

            // Window tint
            try { pv.WindowTint = Function.Call<int>(Hash.GET_VEHICLE_WINDOW_TINT, v.Handle); } catch { }

            // Tyre smoke
            try
            {
                var r = new OutputArgument();
                var g = new OutputArgument();
                var b = new OutputArgument();
                Function.Call(Hash.GET_VEHICLE_TYRE_SMOKE_COLOR, v.Handle, r, g, b);
                pv.TyreSmokeColor[0] = r.GetResult<int>();
                pv.TyreSmokeColor[1] = g.GetResult<int>();
                pv.TyreSmokeColor[2] = b.GetResult<int>();
            }
            catch { }

            // Extras: record which extras exist on this model
            try
            {
                pv.ExtrasExist = new List<int>();
                for (int ex = 1; ex <= 20; ex++)
                {
                    try
                    {
                        if (Function.Call<bool>(Hash.DOES_EXTRA_EXIST, v.Handle, ex))
                            pv.ExtrasExist.Add(ex);
                    }
                    catch { }
                }
            }
            catch { }

            // Neon colour (if available)
            try
            {
                var nr = new OutputArgument();
                var ng = new OutputArgument();
                var nb = new OutputArgument();
                Function.Call(Hash.GET_VEHICLE_NEON_COLOUR, v.Handle, nr, ng, nb);
                pv.NeonColor = new int[] { nr.GetResult<int>(), ng.GetResult<int>(), nb.GetResult<int>() };
            }
            catch { /* ignore if not supported */ }

            // Dashboard colour (best-effort)
            try
            {
                var outDash = new OutputArgument();
                Function.Call(Hash.GET_VEHICLE_EXTRA_COLOUR_6, v.Handle, outDash);
                pv.DashboardColor = outDash.GetResult<int>();
            }
            catch { }
        }
        catch (Exception ex)
        {
            Log("RefreshPersistentFromRuntime failed: " + ex.ToString());
        }
    }

    // Call this after you finish applying mods to a Vehicle in other mods
    public static void UpdatePersistentFromVehicle(Vehicle v)
    {
        try
        {
            if (v == null || !v.Exists()) return;
            lock (_persistVehicles)
            {
                // prefer exact runtime handle match
                var pv = _persistVehicles.FirstOrDefault(p => p.RuntimeVehicle != null && p.RuntimeVehicle.Handle == v.Handle);
                if (pv == null)
                {
                    // fallback: model + proximity + owner
                    int ownerHash = Game.Player?.Character?.Model.Hash ?? 0;
                    uint model = (uint)v.Model.Hash;
                    var pos = v.Position;
                    pv = _persistVehicles.FirstOrDefault(p => p.ModelHash == model && p.OwnerModelHash == ownerHash && p.Position.DistanceTo2D(pos) < 5f);
                }

                if (pv != null)
                {
                    // Refresh and mark dirty
                    RefreshPersistentFromRuntime(pv);
                    _vehiclesDirty = true;
                }
                else
                {
                    // not found -> create/register a new persistent entry
                    try
                    {
                        RegisterVehicle(v, null);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log("UpdatePersistentFromVehicle failed: " + ex.ToString());
        }
    }

    private void UpdateCurrentWeaponState()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()) return;

            // Nếu đang hành động (bắn/nhắm) -> reset idle counter để tránh cập nhật khi đang dùng
            if (Game.IsControlPressed(GTA.Control.Attack) || Game.IsControlPressed(GTA.Control.Aim))
            {
                _weaponIdleCounter = 0;
                return;
            }

            // tăng bộ đếm idle; chỉ tiếp tục khi đã idle đủ
            _weaponIdleCounter++;
            if (_weaponIdleCounter < 2) return;

            int charHash = player.Model.Hash;

            // 1) Update current weapon ammo quickly (low-cost)
            try
            {
                Weapon currentWep = player.Weapons.Current;
                if (currentWep != null && currentWep.Hash != WeaponHash.Unarmed)
                {
                    lock (_characterWeapons)
                    {
                        if (!_characterWeapons.ContainsKey(charHash))
                        {
                            // ensure list exists but don't create excessive entries
                            _characterWeapons[charHash] = new List<PersistentWeapon>();
                        }

                        var list = _characterWeapons[charHash];
                        var stored = list.FirstOrDefault(x => x.Hash == (uint)currentWep.Hash);
                        if (stored != null)
                        {
                            // ammo change?
                            if (stored.Ammo != currentWep.Ammo)
                            {
                                stored.Ammo = currentWep.Ammo;
                                _weaponsDirty = true;
                                _weaponIdleCounter = 0;
                            }

                            // tint change?
                            int tint = (int)currentWep.Tint;
                            if (stored.Tint != tint)
                            {
                                stored.Tint = tint;
                                _weaponsDirty = true;
                            }
                        }
                        else
                        {
                            // Add if under cap
                            if (list.Count < MAX_WEAPON_ENTRIES_PER_CHAR)
                            {
                                var comps = new List<uint>();
                                foreach (var c in currentWep.Components)
                                {
                                    try
                                    {
                                        if (c.Active) comps.Add((uint)c.ComponentHash);
                                    }
                                    catch { }
                                }
                                if (comps.Count > MAX_COMPONENTS_PER_WEAPON) comps = comps.Take(MAX_COMPONENTS_PER_WEAPON).ToList();

                                list.Add(new PersistentWeapon
                                {
                                    Hash = (uint)currentWep.Hash,
                                    Ammo = currentWep.Ammo,
                                    Tint = (int)currentWep.Tint,
                                    Components = comps
                                });
                                _weaponsDirty = true;
                            }
                            else
                            {
                                Log($"UpdateCurrentWeaponState: char 0x{charHash:X} reached MAX weapons, skipping add of 0x{currentWep.Hash:X}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log("UpdateCurrentWeaponState current-wep: " + ex.ToString()); }

            // 2) Periodic FULL SCAN of all player's weapons (throttled to WEAPON_FULL_SCAN_TICKS)
            _weaponFullScanCounter++;
            if (_weaponFullScanCounter >= WEAPON_FULL_SCAN_TICKS)
            {
                _weaponFullScanCounter = 0;
                try
                {
                    lock (_characterWeapons)
                    {
                        if (!_characterWeapons.ContainsKey(charHash))
                            _characterWeapons[charHash] = new List<PersistentWeapon>();

                        var list = _characterWeapons[charHash];

                        // Build a temporary map of current in-game weapons for faster lookup
                        var currentMap = new Dictionary<uint, (int ammo, int tint, List<uint> components)>();

                        foreach (var w in player.Weapons)
                        {
                            try
                            {
                                if (w == null || w.Hash == WeaponHash.Unarmed) continue;
                                var comps = new List<uint>();
                                foreach (var c in w.Components)
                                {
                                    try { if (c.Active) comps.Add((uint)c.ComponentHash); } catch { }
                                }
                                if (comps.Count > MAX_COMPONENTS_PER_WEAPON) comps = comps.Take(MAX_COMPONENTS_PER_WEAPON).ToList();

                                currentMap[(uint)w.Hash] = (w.Ammo, (int)w.Tint, comps);
                            }
                            catch { /* ignore per-weapon errors */ }
                        }

                        // Sync stored list with currentMap: update ammo/tint/components when changed
                        foreach (var wp in list)
                        {
                            try
                            {
                                if (currentMap.TryGetValue(wp.Hash, out var info))
                                {
                                    bool changed = false;
                                    if (wp.Ammo != info.ammo) { wp.Ammo = info.ammo; changed = true; }
                                    if (wp.Tint != info.tint) { wp.Tint = info.tint; changed = true; }

                                    // components comparison (cheap): lengths first, then sequence
                                    var newComps = info.components;
                                    bool compsDifferent = false;
                                    if (wp.Components == null) wp.Components = new List<uint>();
                                    if (wp.Components.Count != newComps.Count) compsDifferent = true;
                                    else
                                    {
                                        for (int i = 0; i < newComps.Count; i++)
                                        {
                                            if (wp.Components[i] != newComps[i]) { compsDifferent = true; break; }
                                        }
                                    }
                                    if (compsDifferent)
                                    {
                                        wp.Components = newComps;
                                        changed = true;
                                    }

                                    if (changed) _weaponsDirty = true;
                                }
                            }
                            catch { }
                        }

                        // Also: add any in-game weapons not yet tracked (respect cap)
                        foreach (var kv in currentMap)
                        {
                            if (!list.Any(x => x.Hash == kv.Key))
                            {
                                if (list.Count >= MAX_WEAPON_ENTRIES_PER_CHAR) break;
                                list.Add(new PersistentWeapon
                                {
                                    Hash = kv.Key,
                                    Ammo = kv.Value.ammo,
                                    Tint = kv.Value.tint,
                                    Components = kv.Value.components
                                });
                                _weaponsDirty = true;
                            }
                        }
                    }
                }
                catch (Exception ex) { Log("UpdateCurrentWeaponState full-scan: " + ex.ToString()); }
            }
        }
        catch (Exception ex) { Log("UpdateCurrentWeaponState: " + ex.ToString()); }
    }

    // Restore logic (F2)
    private void RestoreForCurrentPlayer()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
            {
                Notification.Show(L("PM_Restore_NoCharacter", "~y~Không có nhân vật (restore thất bại)."));
                return;
            }

            int charHash = player.Model.Hash;
            int restoredWeapons = 0;
            int vehiclesMarked = 0;
            int vehiclesSpawned = 0;

            // --- Weapons
            lock (_characterWeapons)
            {
                try
                {
                    if (_characterWeapons.ContainsKey(charHash))
                    {
                        var list = _characterWeapons[charHash];
                        var copy = list.ToList(); // iterate a copy for safety

                        foreach (var pw in copy)
                        {
                            try
                            {
                                bool valid = false;
                                try { valid = Function.Call<bool>(Hash.IS_WEAPON_VALID, pw.Hash); } catch { valid = false; }
                                if (!valid)
                                {
                                    Log($"Restore: weapon 0x{pw.Hash:X} invalid => skip");
                                    continue;
                                }

                                try
                                {
                                    // Give weapon with ammo: clamp ammo >= 0
                                    int ammoToGive = Math.Max(0, pw.Ammo);

                                    // Give the weapon (do not equip)
                                    player.Weapons.Give((WeaponHash)pw.Hash, ammoToGive, false, false);

                                    // Set properties defensively
                                    var w = player.Weapons[(WeaponHash)pw.Hash];
                                    if (w != null)
                                    {
                                        try { w.Ammo = ammoToGive; } catch { }
                                        try { w.Tint = (WeaponTint)pw.Tint; } catch { }
                                    }

                                    // Native fallback for ammo
                                    try { Function.Call(Hash.SET_PED_AMMO, player.Handle, pw.Hash, ammoToGive); } catch { }

                                    // Give components (if any) - cap to avoid error
                                    if (pw.Components != null && pw.Components.Count > 0)
                                    {
                                        int compCount = 0;
                                        foreach (var c in pw.Components)
                                        {
                                            try
                                            {
                                                if (compCount >= MAX_COMPONENTS_PER_WEAPON) break;
                                                Function.Call(Hash.GIVE_WEAPON_COMPONENT_TO_PED, player.Handle, pw.Hash, c);
                                                compCount++;
                                            }
                                            catch { }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log("Restore weapon give failed: " + ex.ToString());
                                    continue;
                                }

                                restoredWeapons++;
                            }
                            catch (Exception ex) { Log("Restore weapon loop error: " + ex.ToString()); }
                        }

                        // IMPORTANT: Do NOT remove the stored weapons entry here.
                        // Keeping stored data allows multiple restores (like vehicles) and keeps ammo/components persistent.
                        // We still mark dirty to ensure file-synced state remains consistent if we changed anything.
                        _weaponsDirty = true;
                    }
                }
                catch (Exception ex) { Log("RestoreForCurrentPlayer weapons block: " + ex.ToString()); }
            }

            // --- Vehicles: (unchanged code from original) ---
            List<PersistentVehicle> owned;
            lock (_persistVehicles)
            {
                owned = _persistVehicles.Where(p => p.OwnerModelHash == charHash).ToList();
            }

            foreach (var pv in owned)
            {
                try
                {
                    if (pv.RuntimeVehicle != null && pv.RuntimeVehicle.Exists())
                    {
                        Log($"Restore: vehicle model 0x{pv.ModelHash:X} already exists in world -> skip marking.");
                        continue;
                    }

                    Model m = new Model((int)pv.ModelHash);
                    bool modelValid = (m.IsValid && m.IsInCdImage);
                    if (!modelValid)
                    {
                        Log($"Restore: model 0x{pv.ModelHash:X} invalid or not in cdimage -> cannot restore (skip).");
                        m.MarkAsNoLongerNeeded();
                        continue;
                    }

                    lock (_persistVehicles)
                    {
                        pv.AutoSpawnEnabled = true;
                        try
                        {
                            SafeRemoveBlip(pv.MapBlip);
                            pv.MapBlip = World.CreateBlip(pv.Position);
                            if (pv.MapBlip != null)
                            {
                                pv.MapBlip.IsShortRange = false;
                                pv.MapBlip.Sprite = GetVehicleBlipSprite(pv.ModelHash);
                                pv.MapBlip.Color = GetBlipColorForPersistentVehicle(pv);
                                pv.MapBlip.Name = "Phương tiện đã trả lại";
                            }
                        }
                        catch (Exception ex) { Log("Restore create static blip: " + ex.ToString()); pv.MapBlip = null; }

                        _vehiclesDirty = true;
                        vehiclesMarked++;
                    }

                    try
                    {
                        var playerPos = Game.Player.Character.Position;
                        if (playerPos.DistanceTo(pv.Position) < 150f)
                        {
                            SpawnPersistentVehicle(pv);
                            if (pv.RuntimeVehicle != null && pv.RuntimeVehicle.Exists()) vehiclesSpawned++;
                        }
                    }
                    catch (Exception ex) { Log("Restore spawn-on-restore check: " + ex.ToString()); }
                }
                catch (Exception ex) { Log("Restore vehicle loop error: " + ex.ToString()); }
            }

            ShowFeedMessage(
                    L("PM_Feed_RestoreSender", "Mors Mutual"),
                    L("PM_Feed_RestoreSubject", "Hoàn trả vật phẩm"),
                    string.Format(
                    L("PM_Feed_RestoreBody",
                    "Trả vật hoàn tất: ~g~{0}~s~ vũ khí đã trả, ~y~{1}~s~ phương tiện kích hoạt ({2} đã được trả lại)."),
                    restoredWeapons, vehiclesMarked, vehiclesSpawned)
            );
            Log($"RestoreForCurrentPlayer: restoredWeapons={restoredWeapons}, vehiclesMarked={vehiclesMarked}, vehiclesSpawned={vehiclesSpawned}, forChar=0x{charHash:X}");
        }
        catch (Exception ex)
        {
            Log("RestoreForCurrentPlayer failed: " + ex.ToString());
            Notification.Show(L("PM_RestoreFailed", "~r~Restore thất bại. Kiểm tra logs."));
        }
    }

    private bool TryFindSafeInsuranceSpawnPoint(Vector3 origin, out Vector3 spawnPos, out float heading)
    {
        spawnPos = Vector3.Zero;
        heading = 0f;

        try
        {
            // Ưu tiên node rất gần trước, rồi mở rộng dần
            float[] radii = new float[] { 2f, 4f, 6f, 10f, 14f, 20f, 30f, 45f };
            const int samplesPerRing = 12;

            for (int r = 0; r < radii.Length; r++)
            {
                float radius = radii[r];

                for (int i = 0; i < samplesPerRing; i++)
                {
                    double angle = (Math.PI * 2.0 * i) / samplesPerRing;

                    Vector3 guess = origin + new Vector3(
                        (float)Math.Cos(angle) * radius,
                        (float)Math.Sin(angle) * radius,
                        0f);

                    OutputArgument posArg = new OutputArgument();
                    OutputArgument headArg = new OutputArgument();

                    bool found = Function.Call<bool>(
                        Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                        guess.X, guess.Y, guess.Z + 50f,
                        posArg, headArg,
                        1, 3.0f, 0);

                    if (!found)
                        continue;

                    Vector3 p = posArg.GetResult<Vector3>();
                    float h = headArg.GetResult<float>();

                    // kéo xuống mặt đất nếu native hỗ trợ
                    float groundZ = p.Z;
                    try
                    {
                        OutputArgument zArg = new OutputArgument();
                        if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, p.X, p.Y, p.Z + 100f, zArg, false))
                            groundZ = zArg.GetResult<float>();
                    }
                    catch { }

                    spawnPos = new Vector3(p.X, p.Y, groundZ + 0.5f);
                    heading = h;
                    return true;
                }
            }

            // Fallback cuối cùng: node đường gần nhất
            Vector3 street = World.GetNextPositionOnStreet(origin);
            if (street != Vector3.Zero)
            {
                spawnPos = street;
                heading = 0f;
                return true;
            }
        }
        catch { }

        return false;
    }

    // NEW: called when we detect the player just exited a vehicle
    private void OnPlayerExitedVehicle(Vehicle veh)
    {
        try
        {
            // Check nhanh để đỡ tốn resource
            if (veh == null || !veh.Exists()) return;

            lock (_persistVehicles)
            {
                // Tìm xe trùng khớp Handle (Runtime) trước - Nhanh nhất
                var pv = _persistVehicles.FirstOrDefault(p => p.RuntimeVehicle != null && p.RuntimeVehicle.Handle == veh.Handle);

                // Nếu không thấy, tìm theo Hash + Vị trí + Chủ sở hữu (Fallback)
                if (pv == null)
                {
                    // Lấy hash một lần để dùng trong LINQ
                    int ownerHash = Game.Player.Character?.Model.Hash ?? 0;
                    uint vehModelHash = (uint)veh.Model.Hash;
                    Vector3 vehPos = veh.Position;

                    pv = _persistVehicles.FirstOrDefault(p =>
                        p.ModelHash == vehModelHash &&
                        p.OwnerModelHash == ownerHash &&
                        p.Position.DistanceTo2D(vehPos) < 5f);
                }

                // Nếu tìm thấy xe trong database -> Cập nhật vị trí mới nhất
                if (pv != null)
                {
                    try
                    {
                        pv.Position = veh.Position;
                        pv.Heading = veh.Heading;

                        // Lấy biển số (chỉ khi cần thiết)
                        string plate = veh.Mods.LicensePlate;
                        if (!string.IsNullOrEmpty(plate)) pv.Plate = plate;

                        _vehiclesDirty = true;
                        Log($"OnPlayerExitedVehicle: updated pos for model 0x{pv.ModelHash:X} -> {pv.Position}");
                    }
                    catch (Exception ex) { Log("OnPlayerExitedVehicle update failed: " + ex.Message); }
                }
            }

            SetPersistentVehicleOccupiedState(veh, false);
        }
        catch (Exception ex)
        {
            Log("OnPlayerExitedVehicle failed: " + ex.ToString());
        }
    }

    private PersistentVehicle FindPersistentVehicleByRuntimeHandle(int handle)
    {
        try
        {
            lock (_persistVehicles)
            {
                return _persistVehicles.FirstOrDefault(p =>
                    p.RuntimeVehicle != null &&
                    p.RuntimeVehicle.Exists() &&
                    p.RuntimeVehicle.Handle == handle);
            }
        }
        catch
        {
            return null;
        }
    }

    private void SetPersistentVehicleOccupiedState(Vehicle veh, bool occupied)
    {
        try
        {
            if (veh == null || !veh.Exists())
                return;

            PersistentVehicle pv = FindPersistentVehicleByRuntimeHandle(veh.Handle);
            if (pv == null)
                return;

            pv.SuppressBlipWhileOccupied = occupied;

            if (occupied)
            {
                // Ẩn ngay blip hiện tại
                SafeRemoveBlip(pv.MapBlip);
                pv.MapBlip = null;
            }
            else
            {
                // Khi rời xe thì dựng lại blip theo logic hiện có
                RefreshSinglePersistentVehicleBlip(pv);
            }
        }
        catch (Exception ex)
        {
            Log("SetPersistentVehicleOccupiedState failed: " + ex.ToString());
        }
    }

    private void OnPlayerEnteredVehicle(Vehicle veh)
    {
        try
        {
            SetPersistentVehicleOccupiedState(veh, true);
        }
        catch (Exception ex)
        {
            Log("OnPlayerEnteredVehicle failed: " + ex.ToString());
        }
    }

    private void UpdateVehiclePositionsLogic()
    {
        try
        {
            lock (_persistVehicles)
            {
                if (_persistVehicles.Count == 0) return;

                // process a window of entries per tick to avoid long blocking loops
                int total = _persistVehicles.Count;
                int toProcess = Math.Min(VEHICLE_POSITIONS_PER_TICK, total);
                for (int processed = 0; processed < toProcess; processed++)
                {
                    // calculate index circularly from cursor
                    int i = (_vehiclePositionsCursor + processed) % total;
                    var pv = _persistVehicles[i];
                    try
                    {
                        if (pv.RuntimeVehicle == null) continue;

                        Vehicle v = pv.RuntimeVehicle;
                        if (v == null) continue;

                        bool exists = false;
                        try { exists = v.Exists(); } catch { exists = false; }
                        if (!exists)
                        {
                            HandleEngineDeletionForVehicle(pv);
                            continue;
                        }

                        if (!v.IsDead)
                        {
                            if (v.Velocity.LengthSquared() < 0.25f)
                            {
                                pv.StopCounter++;
                                if (pv.StopCounter >= 5)
                                {
                                    pv.Position = v.Position;
                                    pv.Heading = v.Heading;
                                    try { pv.Plate = v.Mods.LicensePlate; } catch { }
                                    _vehiclesDirty = true;
                                }
                            }
                            else
                            {
                                pv.StopCounter = 0;
                            }
                        }
                    }
                    catch (Exception ex) { Log($"UpdateVehiclePositionsLogic error at idx {i}: " + ex.Message); }
                }

                // advance cursor
                _vehiclePositionsCursor = (_vehiclePositionsCursor + toProcess) % Math.Max(1, _persistVehicles.Count);
            }
        }
        catch (Exception ex) { Log("UpdateVehiclePositionsLogic outer: " + ex.ToString()); }
    }

    // Overload: RegisterVehicle that captures meta from the vehicle (best-effort).
    public static void RegisterVehicle(Vehicle v)
    {
        RegisterVehicle(v, null);
    }

    // New overload: optional meta parameter (caller can pass metadata explicitly).
    public static void RegisterVehicle(Vehicle v, VehicleMeta explicitMeta)
    {
        try
        {
            if (v == null || !v.Exists()) return;
            if (Game.Player?.Character == null) return;

            int ownerHash = Game.Player.Character.Model.Hash;

            var pv = new PersistentVehicle
            {
                ModelHash = (uint)v.Model.Hash,
                Position = v.Position,
                Heading = v.Heading,
                Plate = SafeGetPlate(v),
                RuntimeVehicle = v,
                OwnerModelHash = ownerHash,
                StopCounter = 0,
                AutoSpawnEnabled = false
            };

            // Ngay sau đoạn khởi tạo pv, thêm patch Hao:
            if (explicitMeta != null)
            {
                if (explicitMeta.HaoUpgradeBranch.HasValue)
                    pv.HaoUpgradeBranch = explicitMeta.HaoUpgradeBranch;

                pv.HaoUpgradeApplied = explicitMeta.HaoUpgradeApplied;
            }

            try
            {
                if (explicitMeta != null)
                {
                    pv.PurchasePrice = explicitMeta.PurchasePrice;
                }
                else
                {
                    pv.PurchasePrice = 0;
                }
            }
            catch { pv.PurchasePrice = 0; }

            // ===== META FROM INPUT ===
            if (explicitMeta != null)
            {
                if (explicitMeta.Mods != null)
                    pv.Mods = new Dictionary<int, int>(explicitMeta.Mods);

                pv.TurboOn = explicitMeta.TurboOn;
            }
            else
            {
                try
                {
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, v.Handle, 0);

                    for (int modType = 0; modType <= 49; modType++)
                    {
                        try
                        {
                            int idx = Function.Call<int>(Hash.GET_VEHICLE_MOD, v.Handle, modType);
                            if (idx >= 0)
                                pv.Mods[modType] = idx;
                        }
                        catch { }
                    }

                    try
                    {
                        pv.TurboOn = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, v.Handle, 18);
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    Log("RegisterVehicle read mods: " + ex);
                }
            }

            // ===== VISUAL DATA =======
            try
            {
                int liv = Function.Call<int>(Hash.GET_VEHICLE_LIVERY, v.Handle);
                if (liv >= 0)
                    pv.SavedLivery = liv;
            }
            catch { }

            try
            {
                var out1 = new OutputArgument();
                var out2 = new OutputArgument();
                Function.Call(Hash.GET_VEHICLE_COLOURS, v.Handle, out1, out2);

                pv.PrimaryColor = out1.GetResult<int>();
                pv.SecondaryColor = out2.GetResult<int>();
            }
            catch { }

            try
            {
                var pearl = new OutputArgument();
                var wheel = new OutputArgument();
                Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, v.Handle, pearl, wheel);

                pv.PearlColor = pearl.GetResult<int>();
                pv.WheelColor = wheel.GetResult<int>();
            }
            catch { }

            try
            {
                pv.WindowTint = Function.Call<int>(Hash.GET_VEHICLE_WINDOW_TINT, v.Handle);
            }
            catch { }

            try
            {
                var r = new OutputArgument();
                var g = new OutputArgument();
                var b = new OutputArgument();

                Function.Call(Hash.GET_VEHICLE_TYRE_SMOKE_COLOR, v.Handle, r, g, b);

                pv.TyreSmokeColor[0] = r.GetResult<int>();
                pv.TyreSmokeColor[1] = g.GetResult<int>();
                pv.TyreSmokeColor[2] = b.GetResult<int>();
            }
            catch { }

            // --- PHẦN BỔ SUNG: Extras + Neon + Dashboard (Best-effort) ---
            try
            {
                // Extras: Ghi lại danh sách extra đang tồn tại trên model
                pv.ExtrasExist = new List<int>();
                for (int ex = 1; ex <= 20; ex++)
                {
                    try
                    {
                        bool exists = Function.Call<bool>(Hash.DOES_EXTRA_EXIST, v.Handle, ex);
                        if (exists) pv.ExtrasExist.Add(ex);
                    }
                    catch { /* ignore */ }
                }

                // Neon colour
                try
                {
                    var outR = new OutputArgument();
                    var outG = new OutputArgument();
                    var outB = new OutputArgument();
                    Function.Call(Hash.GET_VEHICLE_NEON_COLOUR, v.Handle, outR, outG, outB);
                    int nr = outR.GetResult<int>();
                    int ng = outG.GetResult<int>();
                    int nb = outB.GetResult<int>();

                    if (nr >= 0 || ng >= 0 || nb >= 0)
                    {
                        pv.NeonColor = new int[3] { nr, ng, nb };
                    }
                }
                catch { /* swallow neon read errors */ }

                // Dashboard colour
                try
                {
                    var outDash = new OutputArgument();
                    Function.Call(Hash.GET_VEHICLE_EXTRA_COLOUR_6, v.Handle, outDash);
                    int dashIdx = outDash.GetResult<int>();
                    pv.DashboardColor = dashIdx;
                }
                catch { /* ignore if getter not available */ }
            }
            catch (Exception ex)
            {
                Log("RegisterVehicle read extras/neon/dashboard: " + ex.ToString());
            }
            // --- KẾT THÚC PHẦN BỔ SUNG ---

            // === REGISTER / UPDATE ===

            lock (_persistVehicles)
            {
                try
                {
                    var existing = _persistVehicles.FirstOrDefault(x =>
                        x.ModelHash == pv.ModelHash &&
                        x.OwnerModelHash == ownerHash &&
                        x.Position.DistanceTo2D(pv.Position) < 5f);

                    if (existing != null)
                    {
                        // ===== UPDATE EXISTING =====
                        existing.RuntimeVehicle = v;
                        existing.Position = pv.Position;
                        existing.Heading = pv.Heading;
                        existing.Plate = pv.Plate;

                        existing.Mods = pv.Mods;
                        existing.TurboOn = pv.TurboOn;

                        existing.SavedLivery = pv.SavedLivery;
                        existing.PrimaryColor = pv.PrimaryColor;
                        existing.SecondaryColor = pv.SecondaryColor;
                        existing.PearlColor = pv.PearlColor;
                        existing.WheelColor = pv.WheelColor;
                        existing.WindowTint = pv.WindowTint;

                        existing.TyreSmokeColor = pv.TyreSmokeColor;

                        // Cập nhật các trường mới vào existing
                        existing.ExtrasExist = pv.ExtrasExist;
                        existing.NeonColor = pv.NeonColor;
                        existing.DashboardColor = pv.DashboardColor;

                        // 1.5. Bảo toàn cờ khóa khi cập nhật entry hiện có
                        existing.IsCollateralLocked = pv.IsCollateralLocked;

                        try
                        {
                            if (explicitMeta != null && explicitMeta.PurchasePrice > 0)
                            {
                                existing.PurchasePrice = explicitMeta.PurchasePrice;
                            }
                        }
                        catch { }

                        // Thêm vào cuối phần update metadata trong nhánh existing != null:
                        if (explicitMeta != null)
                        {
                            if (explicitMeta.HaoUpgradeBranch.HasValue)
                                existing.HaoUpgradeBranch = explicitMeta.HaoUpgradeBranch;

                            existing.HaoUpgradeApplied = explicitMeta.HaoUpgradeApplied;
                        }

                        _vehiclesDirty = true;
                    }
                    else
                    {
                        // ===== 1.4) PER-CHAR FIFO TRIM: CHỈ TRIM XE KHÔNG KHÓA =====
                        int ownerCount = _persistVehicles.Count(x => x.OwnerModelHash == ownerHash);

                        if (ownerCount >= MAX_VEHICLES_PER_CHAR)
                        {
                            var oldestForOwner = _persistVehicles
                                .FirstOrDefault(x => x.OwnerModelHash == ownerHash && !x.IsCollateralLocked);

                            if (oldestForOwner != null)
                            {
                                try
                                {
                                    int refund = 0;
                                    try
                                    {
                                        if (oldestForOwner.PurchasePrice > 0)
                                            refund = (int)Math.Round(oldestForOwner.PurchasePrice * REFUND_PERCENT);
                                    }
                                    catch { refund = 0; }

                                    try
                                    {
                                        if (refund > 0)
                                        {
                                            Game.Player.Money += refund;
                                            string vehicleName = GetVehicleDisplayName(oldestForOwner.ModelHash);

                                            try
                                            {
                                                string msg = string.Format(
                                                    CultureInfo.InvariantCulture,
                                                    L("PM_RegisterVehicle_RefundBody",
                                                    "Quyền lợi tất toán ~HUD_COLOUR_DEGEN_YELLOW~{1}~s~: ~HUD_COLOUR_DEGEN_GREEN~+${0:N0}~s~. Quý khách cứ mua thỏa thích vì việc hoàn tiền này sẽ giúp quý khách tái đầu tư tốt hơn. Cảm ơn quý khách đã ủng hộ!"),
                                                    refund,
                                                    vehicleName
                                                );

                                                ShowFeedMessage(
                                                    L("PM_RegisterVehicle_RefundSender", "Mors Mutual"),
                                                    L("PM_RegisterVehicle_RefundSubject", "Bảo Hiểm (hết hạn)"),
                                                    msg);
                                            }
                                            catch { }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log("RegisterVehicle: refund credit failed: " + ex.ToString());
                                    }

                                    RemovePersistentVehicleSafe(oldestForOwner);
                                    Log($"RegisterVehicle: evicted oldest UNLOCKED vehicle for owner 0x{ownerHash:X} (per-char cap) refund={refund}, model=0x{oldestForOwner.ModelHash:X}");
                                }
                                catch (Exception ex)
                                {
                                    Log("RegisterVehicle: per-char eviction failed: " + ex);
                                }
                            }
                            else
                            {
                                Log($"RegisterVehicle: per-char cap reached for owner 0x{ownerHash:X}, but no UNLOCKED vehicle found to trim. Skipping trim to preserve collateral-locked vehicles.");
                            }
                        }

                        // ===== 2) GLOBAL CAP CHECK =====
                        if (_persistVehicles.Count >= MAX_PERSIST_VEHICLES)
                        {
                            Log("RegisterVehicle: reached MAX_PERSIST_VEHICLES, refusing to add new persistent vehicle");
                        }
                        else
                        {
                            _persistVehicles.Add(pv);
                            _vehiclesDirty = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("RegisterVehicle lock block failed: " + ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log("RegisterVehicle: " + ex);
        }
    }

    // Handle engine deletion
    private void HandleEngineDeletionForVehicle(PersistentVehicle pv)
    {
        try
        {
            if (pv == null) return;

            lock (_persistVehicles)
            {
                try
                {
                    // ===== HandleEngineDeletionForVehicle(...) =====
                    pv.RuntimeVehicle = null;
                    pv.AutoSpawnEnabled = false;
                    pv.SuppressBlipWhileOccupied = false;

                    // xe despawn thì xóa blip như logic bình thường
                    SafeRemoveBlip(pv.MapBlip);
                    pv.MapBlip = null;

                    _vehiclesDirty = true;

                    Log($"HandleEngineDeletionForVehicle: engine removed runtime vehicle for model 0x{pv.ModelHash:X} at {pv.Position} owner=0x{pv.OwnerModelHash:X}");
                }
                catch (Exception ex) { Log("HandleEngineDeletionForVehicle lock body: " + ex.ToString()); }
            }
        }
        catch (Exception ex) { Log("HandleEngineDeletionForVehicle failed: " + ex.ToString()); }
    }

    private void UpdatePersistentVehicleState()
    {
        try
        {
            List<PersistentVehicle> copy;
            lock (_persistVehicles) { copy = _persistVehicles.ToList(); }

            for (int idx = copy.Count - 1; idx >= 0; idx--)
            {
                var pv = copy[idx];
                try
                {
                    if (pv.RuntimeVehicle != null)
                    {
                        bool exists = false;
                        try { exists = pv.RuntimeVehicle.Exists(); } catch (Exception ex) { Log("UpdatePersistentVehicleState exists check: " + ex.Message); exists = false; }

                        if (!exists)
                        {
                            HandleEngineDeletionForVehicle(pv);
                            continue;
                        }

                        if (pv.RuntimeVehicle.IsDead)
                        {
                            try { SafeRemoveBlip(pv.MapBlip); } catch (Exception ex) { Log("UpdatePersistentVehicleState remove blip: " + ex.Message); }

                            MovePersistentVehicleToDestroyedStorage(pv);
                            Log($"UpdatePersistentVehicleState: vehicle destroyed -> moved to destroyed storage model 0x{pv.ModelHash:X}");
                            continue;
                        }

                        continue;
                    }
                    else
                    {
                        var player = Game.Player.Character;
                        if (player != null && player.Exists())
                        {
                            if (pv.AutoSpawnEnabled && player.Position.DistanceTo(pv.Position) < 150f)
                                SpawnPersistentVehicle(pv);
                        }
                    }
                }
                catch (Exception ex) { Log("UpdatePersistentVehicleState inner: " + ex.ToString()); }
            }
        }
        catch (Exception ex) { Log("UpdatePersistentVehicleState: " + ex.ToString()); }
    }

    private void MovePersistentVehicleToDestroyedStorage(PersistentVehicle pv)
    {
        try
        {
            if (pv == null)
                return;

            PersistentVehicle stored = CloneVehicleForStorage(pv);
            if (stored == null)
                return;

            stored.RuntimeVehicle = null;
            stored.MapBlip = null;
            stored.AutoSpawnEnabled = false;

            lock (_persistVehicles)
            {
                var found = _persistVehicles.FirstOrDefault(x => x == pv);
                if (found != null)
                    _persistVehicles.Remove(found);

                _vehiclesDirty = true;
            }

            lock (_destroyedVehicles)
            {
                _destroyedVehicles.Add(stored);
                EnforceDestroyedVehicleCaps_NoSave();
                _destroyedVehiclesDirty = true;
            }
        }
        catch (Exception ex)
        {
            Log("MovePersistentVehicleToDestroyedStorage failed: " + ex.ToString());
        }
    }

    private void EnforceDestroyedVehicleCaps_NoSave()
    {
        try
        {
            var owners = _destroyedVehicles.Select(x => x.OwnerModelHash).Distinct().ToList();

            foreach (var owner in owners)
            {
                while (_destroyedVehicles.Count(x => x.OwnerModelHash == owner) > MAX_DESTROYED_VEHICLES_PER_CHAR)
                {
                    var oldest = _destroyedVehicles.FirstOrDefault(x => x.OwnerModelHash == owner);
                    if (oldest == null)
                        break;

                    _destroyedVehicles.Remove(oldest);
                }
            }

            while (_destroyedVehicles.Count > 30)
            {
                if (_destroyedVehicles.Count == 0) break;
                _destroyedVehicles.RemoveAt(0);
            }
        }
        catch (Exception ex)
        {
            Log("EnforceDestroyedVehicleCaps_NoSave failed: " + ex.ToString());
        }
    }

    private void EnforceDestroyedVehicleCaps()
    {
        lock (_destroyedVehicles)
        {
            EnforceDestroyedVehicleCaps_NoSave();
            _destroyedVehiclesDirty = true;
        }
    }

    private static void SaveDestroyedVehiclesFileInternal()
    {
        try
        {
            EnsureDataFolderExists();

            string[] lines;
            lock (_destroyedVehicles)
            {
                lines = _destroyedVehicles.Select(v =>
                {
                    string modsSerialized = "";
                    if (v.Mods != null && v.Mods.Count > 0)
                    {
                        var parts = v.Mods.Select(kv => $"{kv.Key}:{kv.Value}");
                        modsSerialized = string.Join(",", parts);
                    }

                    int turbo = v.TurboOn ? 1 : 0;

                    string extrasSerialized = "";
                    try
                    {
                        if (v.ExtrasExist != null && v.ExtrasExist.Count > 0)
                            extrasSerialized = string.Join(",", v.ExtrasExist);
                    }
                    catch { }

                    string tyreRgb = "";
                    try
                    {
                        if (v.TyreSmokeColor != null && v.TyreSmokeColor.Length >= 3)
                            tyreRgb = $"{v.TyreSmokeColor[0]},{v.TyreSmokeColor[1]},{v.TyreSmokeColor[2]}";
                    }
                    catch { }

                    string neonRgb = "";
                    try
                    {
                        if (v.NeonColor != null && v.NeonColor.Length >= 3)
                            neonRgb = $"{v.NeonColor[0]},{v.NeonColor[1]},{v.NeonColor[2]}";
                    }
                    catch { }

                    string dashStr = v.DashboardColor.HasValue ? v.DashboardColor.Value.ToString(CultureInfo.InvariantCulture) : "";

                    string modelString = GetVehicleDisplayName(v.ModelHash) ?? $"0x{v.ModelHash:X}";
                    modelString = modelString.Replace("|", "");
                    string hexSuffix = $" (0x{v.ModelHash:X})";
                    if (!modelString.EndsWith($"(0x{v.ModelHash:X})", StringComparison.OrdinalIgnoreCase))
                        modelString = $"{modelString}{hexSuffix}";

                    string haoBranchStr = v.HaoUpgradeBranch.HasValue
                        ? v.HaoUpgradeBranch.Value.ToString(CultureInfo.InvariantCulture)
                        : "";

                    string haoAppliedStr = v.HaoUpgradeApplied ? "1" : "0";

                    string customIconStr = v.CustomIconId.HasValue
                        ? v.CustomIconId.Value.ToString(CultureInfo.InvariantCulture)
                        : "";

                    string customScaleStr = v.CustomBlipScale.HasValue
                        ? v.CustomBlipScale.Value.ToString(CultureInfo.InvariantCulture)
                        : "";

                    return string.Format(CultureInfo.InvariantCulture,
                        "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}|{11}|{12}|{13}|{14}|{15}|{16}|{17}|{18}|{19}|{20}|{21}|{22}|{23}|{24}|{25}",
                        modelString,
                        v.Position.X, v.Position.Y, v.Position.Z,
                        v.Heading,
                        (v.Plate ?? "").Replace("|", ""),
                        v.OwnerModelHash,
                        v.AutoSpawnEnabled ? 1 : 0,
                        modsSerialized,
                        turbo,
                        v.PurchasePrice,
                        (v.SavedLivery.HasValue ? v.SavedLivery.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        (v.PrimaryColor.HasValue ? v.PrimaryColor.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        (v.SecondaryColor.HasValue ? v.SecondaryColor.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        (v.PearlColor.HasValue ? v.PearlColor.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        (v.WheelColor.HasValue ? v.WheelColor.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        (v.WindowTint.HasValue ? v.WindowTint.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        tyreRgb,
                        extrasSerialized,
                        neonRgb,
                        dashStr,
                        v.IsCollateralLocked ? 1 : 0,
                        haoBranchStr,
                        haoAppliedStr,
                        customIconStr,
                        customScaleStr
                    );
                }).ToArray();
            }

            WriteLinesAtomic(DestroyedVehiclesFile, lines);
            _destroyedVehiclesDirty = false;
        }
        catch (Exception ex)
        {
            Log("SaveDestroyedVehiclesFileInternal failed: " + ex.ToString());
        }
    }

    private bool TryParsePersistentVehicleLine(string line, out PersistentVehicle vehicle)
    {
        vehicle = null;

        try
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var p = line.Split('|');
            if (p.Length < 7)
                return false;

            int ownerH = 0;
            if (p.Length > 6)
                int.TryParse(p[6], out ownerH);

            uint modelHash = 0;
            string modelField = p[0].Trim();

            bool parsed = false;
            try
            {
                int idx = modelField.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string hexPart = modelField.Substring(idx + 2).Trim();
                    if (uint.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out modelHash))
                        parsed = true;
                }

                if (!parsed)
                {
                    if (uint.TryParse(modelField, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out modelHash))
                        parsed = true;
                }
            }
            catch
            {
                parsed = false;
            }

            if (!parsed)
            {
                if (!TryGetModelHashFromName(modelField, out modelHash))
                    return false;
            }

            float x = 0f, y = 0f, z = 0f, heading = 0f;
            float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x);
            float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
            float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
            float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out heading);

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
                var parts = p[8].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    try
                    {
                        var kv = part.Split(':');
                        if (kv.Length != 2) continue;

                        int mt = 0, mi = 0;
                        if (int.TryParse(kv[0], out mt) && int.TryParse(kv[1], out mi))
                            modsDict[mt] = mi;
                    }
                    catch { }
                }
            }

            int turbo = 0;
            if (p.Length > 9) int.TryParse(p[9], out turbo);

            int purchasePrice = 0;
            if (p.Length > 10) int.TryParse(p[10], out purchasePrice);

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
            bool collateralLocked = false;

            // Thêm theo hướng dẫn của bạn
            int? haoUpgradeBranch = null;
            bool haoUpgradeApplied = false;

            // Khởi tạo biến customIconId và customScale
            int? customIconId = null;
            float? customScale = null;

            try
            {
                if (p.Length > 11 && !string.IsNullOrEmpty(p[11])) { int tmp; if (int.TryParse(p[11], out tmp)) savedLivery = tmp; }
                if (p.Length > 12 && !string.IsNullOrEmpty(p[12])) { int tmp; if (int.TryParse(p[12], out tmp)) primary = tmp; }
                if (p.Length > 13 && !string.IsNullOrEmpty(p[13])) { int tmp; if (int.TryParse(p[13], out tmp)) secondary = tmp; }
                if (p.Length > 14 && !string.IsNullOrEmpty(p[14])) { int tmp; if (int.TryParse(p[14], out tmp)) pearl = tmp; }
                if (p.Length > 15 && !string.IsNullOrEmpty(p[15])) { int tmp; if (int.TryParse(p[15], out tmp)) wheel = tmp; }
                if (p.Length > 16 && !string.IsNullOrEmpty(p[16])) { int tmp; if (int.TryParse(p[16], out tmp)) windowTint = tmp; }

                if (p.Length > 17 && !string.IsNullOrEmpty(p[17]))
                {
                    var rr = p[17].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (rr.Length >= 3)
                    {
                        int.TryParse(rr[0], out tyreRgb[0]);
                        int.TryParse(rr[1], out tyreRgb[1]);
                        int.TryParse(rr[2], out tyreRgb[2]);
                    }
                }

                if (p.Length > 18 && !string.IsNullOrEmpty(p[18]))
                {
                    foreach (var s in p[18].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int ei = 0;
                        if (int.TryParse(s, out ei)) extrasExist.Add(ei);
                    }
                }

                if (p.Length > 19 && !string.IsNullOrEmpty(p[19]))
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

                if (p.Length > 20 && !string.IsNullOrEmpty(p[20]))
                {
                    int tmp;
                    if (int.TryParse(p[20], out tmp)) dashColor = tmp;
                }

                if (p.Length > 21)
                {
                    int tmp;
                    if (int.TryParse(p[21], out tmp))
                        collateralLocked = tmp != 0;
                }

                // Thêm xử lý phân tách chuỗi cho các trường mới nằm trong khối try-catch
                if (p.Length > 22 && !string.IsNullOrWhiteSpace(p[22]))
                {
                    int tmp;
                    if (int.TryParse(p[22], out tmp) && (tmp == 0 || tmp == 1))
                        haoUpgradeBranch = tmp;
                }

                if (p.Length > 23 && !string.IsNullOrWhiteSpace(p[23]))
                {
                    int tmp;
                    if (int.TryParse(p[23], out tmp))
                        haoUpgradeApplied = tmp != 0;
                }

                // Thêm xử lý cho customIconId từ mảng p[24]
                if (p.Length > 24 && !string.IsNullOrWhiteSpace(p[24]))
                {
                    int tmp;
                    if (int.TryParse(p[24], out tmp) && tmp >= MIN_CUSTOM_ICON_ID && tmp <= MAX_CUSTOM_ICON_ID)
                        customIconId = tmp;
                }

                // Thêm logic xử lý customScale từ mảng p[25]
                if (p.Length > 25 && !string.IsNullOrWhiteSpace(p[25]))
                {
                    float tmp;
                    if (float.TryParse(p[25], NumberStyles.Float, CultureInfo.InvariantCulture, out tmp))
                        customScale = NormalizeCustomBlipScale(tmp);
                }
            }
            catch { }

            vehicle = new PersistentVehicle
            {
                ModelHash = modelHash,
                Position = new Vector3(x, y, z),
                Heading = heading,
                Plate = p.Length > 5 ? p[5] : "",
                OwnerModelHash = ownerH,
                MapBlip = null,
                RuntimeVehicle = null,
                StopCounter = 0,
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
                TyreSmokeColor = tyreRgb ?? new int[3] { 0, 0, 0 },
                ExtrasExist = extrasExist ?? new List<int>(),
                NeonColor = neonRgb,
                DashboardColor = dashColor,
                IsCollateralLocked = collateralLocked,

                // Thêm gán thuộc tính vào Object Initializer
                HaoUpgradeBranch = haoUpgradeBranch,
                HaoUpgradeApplied = haoUpgradeApplied,

                // Cập nhật đầy đủ theo yêu cầu mới
                CustomIconId = customIconId,
                PreviewIconId = null,
                CustomBlipScale = customScale,
                PreviewBlipScale = null,
            };

            return true;
        }
        catch
        {
            vehicle = null;
            return false;
        }
    }

    private void LoadDestroyedVehiclesFileInternal()
    {
        try
        {
            var lines = ReadAllLinesSafeTryBackups(DestroyedVehiclesFile);

            foreach (var line in lines)
            {
                try
                {
                    if (TryParsePersistentVehicleLine(line, out PersistentVehicle pv) && pv != null)
                    {
                        lock (_destroyedVehicles)
                        {
                            _destroyedVehicles.Add(pv);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("LoadDestroyedVehiclesFileInternal line failed: " + ex.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            Log("LoadDestroyedVehiclesFileInternal failed: " + ex.ToString());
        }
    }

    private void SpawnPersistentVehicle(PersistentVehicle pv)
    {
        if (pv == null) return;

        try
        {
            Model m = new Model((int)pv.ModelHash);

            if (!m.IsValid || !m.IsInCdImage)
            {
                Log($"SpawnPersistentVehicle: model 0x{pv.ModelHash:X} invalid.");
                lock (_persistVehicles)
                {
                    var found = _persistVehicles.FirstOrDefault(x => x == pv);
                    if (found != null) _persistVehicles.Remove(found);

                    // Giữ nguyên phần despawn theo hướng dẫn
                    pv.RuntimeVehicle = null;
                    pv.AutoSpawnEnabled = false;
                    pv.SuppressBlipWhileOccupied = false;

                    SafeRemoveBlip(pv.MapBlip);
                    pv.MapBlip = null;

                    _vehiclesDirty = true;
                }
                m.MarkAsNoLongerNeeded();
                return;
            }

            if (!m.IsLoaded) m.Request(1500);
            if (!m.IsLoaded)
            {
                Log($"SpawnPersistentVehicle: model load failed 0x{pv.ModelHash:X}");
                m.MarkAsNoLongerNeeded();
                return;
            }

            Vehicle v = null;
            try
            {
                v = World.CreateVehicle(m, pv.Position, pv.Heading);
            }
            catch (Exception ex)
            {
                Log("CreateVehicle error: " + ex);
            }

            if (v == null || !v.Exists())
            {
                m.MarkAsNoLongerNeeded();
                return;
            }

            // ===== BASIC SETUP =====
            try
            {
                v.PlaceOnGround();
                if (!string.IsNullOrEmpty(pv.Plate))
                    v.Mods.LicensePlate = pv.Plate;

                v.IsEngineRunning = true;
                v.DirtLevel = 0f;
            }
            catch { }

            // ===== APPLY MODS =====
            try
            {
                Function.Call(Hash.SET_VEHICLE_MOD_KIT, v.Handle, 0);

                // Restore all saved mods
                if (pv.Mods != null)
                {
                    foreach (var kv in pv.Mods)
                    {
                        try
                        {
                            Function.Call(Hash.SET_VEHICLE_MOD, v.Handle, kv.Key, kv.Value, false);
                        }
                        catch { }
                    }
                }

                // Turbo
                if (pv.TurboOn)
                {
                    try
                    {
                        Function.Call(Hash.TOGGLE_VEHICLE_MOD, v.Handle, 18, true);
                    }
                    catch { }
                }

                // Livery
                if (pv.SavedLivery.HasValue)
                {
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_LIVERY, v.Handle, pv.SavedLivery.Value);
                    }
                    catch { }
                }

                // Colors
                if (pv.PrimaryColor.HasValue && pv.SecondaryColor.HasValue)
                {
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_COLOURS,
                            v.Handle,
                            pv.PrimaryColor.Value,
                            pv.SecondaryColor.Value);
                    }
                    catch { }
                }

                if (pv.PearlColor.HasValue && pv.WheelColor.HasValue)
                {
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS,
                            v.Handle,
                            pv.PearlColor.Value,
                            pv.WheelColor.Value);
                    }
                    catch { }
                }

                if (pv.WindowTint.HasValue)
                {
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v.Handle, pv.WindowTint.Value);
                    }
                    catch { }
                }

                // Tyre Smoke
                if (pv.TyreSmokeColor != null && pv.TyreSmokeColor.Length >= 3)
                {
                    try
                    {
                        Function.Call(Hash.SET_VEHICLE_TYRE_SMOKE_COLOR,
                            v.Handle,
                            pv.TyreSmokeColor[0],
                            pv.TyreSmokeColor[1],
                            pv.TyreSmokeColor[2]);
                    }
                    catch { }
                }

                // Extras: if we have saved extras list, enable those extras; else fallback to enabling all existing extras
                try
                {
                    if (pv.ExtrasExist != null && pv.ExtrasExist.Count > 0)
                    {
                        foreach (var ex in pv.ExtrasExist)
                        {
                            try
                            {
                                if (Function.Call<bool>(Hash.DOES_EXTRA_EXIST, v.Handle, ex))
                                {
                                    Function.Call(Hash.SET_VEHICLE_EXTRA, v.Handle, ex, 0); // 0 => enable/visible (as used in ApplyOnlineLikeUpgrades)
                                }
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        // fallback: enable all extras that exist on this vehicle
                        for (int ex = 1; ex <= 20; ex++)
                        {
                            try
                            {
                                if (Function.Call<bool>(Hash.DOES_EXTRA_EXIST, v.Handle, ex))
                                    Function.Call(Hash.SET_VEHICLE_EXTRA, v.Handle, ex, 0);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // Neon: apply neon colour + enable neon positions (if neon saved)
                try
                {
                    if (pv.NeonColor != null && pv.NeonColor.Length >= 3)
                    {
                        for (int i = 0; i <= 3; i++)
                        {
                            try { Function.Call(Hash.SET_VEHICLE_NEON_ENABLED, v.Handle, i, true); } catch { }
                        }
                        try
                        {
                            Function.Call(Hash.SET_VEHICLE_NEON_COLOUR, v.Handle, pv.NeonColor[0], pv.NeonColor[1], pv.NeonColor[2]);
                        }
                        catch { }
                    }
                }
                catch { /* swallow */ }

                // Dashboard colour (best-effort)
                try
                {
                    if (pv.DashboardColor.HasValue)
                    {
                        try { Function.Call(Hash.SET_VEHICLE_EXTRA_COLOUR_6, v.Handle, pv.DashboardColor.Value); } catch { }
                    }
                }
                catch { /* ignore */ }

                // Repair
                try { v.Repair(); } catch { }
            }
            catch (Exception ex)
            {
                Log("SpawnPersistentVehicle mod error: " + ex);
            }

            // ===== BLIP =====
            try
            {
                pv.RuntimeVehicle = v;
                pv.SuppressBlipWhileOccupied = false; // <-- Thêm vào đây trước khi gọi RefreshSinglePersistentVehicleBlip
                RefreshSinglePersistentVehicleBlip(pv);

                lock (_persistVehicles)
                {
                    if (pv.MapBlip != null && pv.MapBlip.Exists())
                        pv.MapBlip.Delete();

                    Blip b = v.AddBlip();
                    if (b != null)
                    {
                        b.IsShortRange = false;
                        b.Sprite = ResolveVehicleBlipSprite(pv); // Đồng bộ Sprite theo hướng dẫn
                        b.Color = GetBlipColorForPersistentVehicle(pv);
                        b.Scale = ResolveVehicleBlipScale(pv);  // Đồng bộ Scale theo hướng dẫn
                        b.Name = L("PM_VehicleRestoredBlip", PM_VehicleRestoredName);
                        pv.MapBlip = b;
                    }
                }
            }
            catch { }
            finally
            {
                m.MarkAsNoLongerNeeded();
            }
        }
        catch (Exception ex)
        {
            Log("SpawnPersistentVehicle total failure: " + ex);
        }
    }

    private void UpdateBlips()
    {
        try
        {
            List<PersistentVehicle> copy;
            lock (_persistVehicles) { copy = _persistVehicles.ToList(); }
            if (copy.Count == 0) return;

            int total = copy.Count;
            int toProcess = Math.Min(BLIPS_PER_TICK, total);

            for (int processed = 0; processed < toProcess; processed++)
            {
                int i = (_blipsCursor + processed) % total;
                var pv = copy[i];
                try
                {
                    RefreshSinglePersistentVehicleBlip(pv);
                }
                catch (Exception ex)
                {
                    Log("UpdateBlips loop error: " + ex.ToString());
                }
            }

            _blipsCursor = (_blipsCursor + toProcess) % Math.Max(1, copy.Count);
        }
        catch (Exception ex) { Log("UpdateBlips: " + ex.ToString()); }
    }

    private void CreateBlipsForAllLoadedVehicles()
    {
        try
        {
            lock (_persistVehicles)
            {
                foreach (var pv in _persistVehicles)
                {
                    try
                    {
                        RefreshSinglePersistentVehicleBlip(pv);
                    }
                    catch (Exception ex)
                    {
                        Log("CreateBlipsForAllLoadedVehicles inner: " + ex.ToString());
                    }
                }
            }
        }
        catch (Exception ex) { Log("CreateBlipsForAllLoadedVehicles: " + ex.ToString()); }
    }

    // =======================================================
    private static BlipColor GetBlipColorForCharacter(int modelHash)
    {
        uint hash = (uint)modelHash;

        if (hash == (uint)PedHash.Michael) return BlipColor.Blue;
        if (hash == (uint)PedHash.Franklin) return BlipColor.Green;
        if (hash == (uint)PedHash.Trevor) return BlipColor.Orange;

        return BlipColor.White;
    }

    private static int GetCurrentCharacterHashSafe()
    {
        try
        {
            return Game.Player?.Character?.Model.Hash ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool ShouldShowVehicleOnMapForCurrentCharacter(PersistentVehicle pv)
    {
        try
        {
            if (pv == null)
                return false;

            int currentHash = GetCurrentCharacterHashSafe();
            if (currentHash == 0)
                return false;

            return pv.OwnerModelHash == currentHash;
        }
        catch
        {
            return false;
        }
    }

    private static BlipColor GetBlipColorForPersistentVehicle(PersistentVehicle pv)
    {
        try
        {
            if (pv != null && pv.IsCollateralLocked)
                return BlipColor.Red;

            return GetBlipColorForCharacter(pv != null ? pv.OwnerModelHash : 0);
        }
        catch
        {
            return BlipColor.White;
        }
    }

    private static void RefreshSinglePersistentVehicleBlip(PersistentVehicle pv)
    {
        try
        {
            if (pv == null)
                return;

            // Chỉ hiện xe của nhân vật hiện tại.
            if (!ShouldShowVehicleOnMapForCurrentCharacter(pv))
            {
                SafeRemoveBlip(pv.MapBlip);
                pv.MapBlip = null;
                return;
            }

            if (pv.SuppressBlipWhileOccupied)
            {
                SafeRemoveBlip(pv.MapBlip);
                pv.MapBlip = null;
                return;
            }

            // Tích hợp logic mới thay thế cho phần xử lý blip cũ:
            BlipColor color = GetBlipColorForPersistentVehicle(pv);
            bool hasPreview = HasTemporaryPreview(pv);

            // Nếu runtime vehicle còn tồn tại
            if (pv.RuntimeVehicle != null)
            {
                bool exists = false;
                try { exists = pv.RuntimeVehicle.Exists(); } catch { exists = false; }

                if (exists)
                {
                    if (pv.MapBlip == null || !pv.MapBlip.Exists())
                        pv.MapBlip = pv.RuntimeVehicle.AddBlip();

                    if (pv.MapBlip != null && pv.MapBlip.Exists())
                    {
                        pv.MapBlip.IsShortRange = false;
                        pv.MapBlip.Sprite = ResolveVehicleBlipSprite(pv);
                        pv.MapBlip.Scale = ResolveVehicleBlipScale(pv);
                        pv.MapBlip.Color = color;
                        try { pv.MapBlip.Position = pv.RuntimeVehicle.Position; } catch { }
                        pv.MapBlip.Name = color == BlipColor.Red
                            ? L("PM_VehicleSeizedBlip", "Phương tiện bị niêm phong")
                            : L("PM_VehicleNearbyBlip", "Phương tiện đang ở gần");
                    }

                    return;
                }
            }

            // Xe không còn runtime:
            // chỉ giữ blip tạm nếu đang preview icon / preview size
            if (!pv.AutoSpawnEnabled && !hasPreview)
            {
                SafeRemoveBlip(pv.MapBlip);
                pv.MapBlip = null;
                return;
            }

            // Có preview thì cho phép dựng blip tạm để thấy ngay icon đang đổi
            if (pv.MapBlip == null || !pv.MapBlip.Exists())
            {
                pv.MapBlip = World.CreateBlip(pv.Position);
            }

            if (pv.MapBlip != null && pv.MapBlip.Exists())
            {
                pv.MapBlip.IsShortRange = false;
                pv.MapBlip.Sprite = ResolveVehicleBlipSprite(pv);
                pv.MapBlip.Scale = ResolveVehicleBlipScale(pv);
                pv.MapBlip.Color = color;
                try { pv.MapBlip.Position = pv.Position; } catch { }
                pv.MapBlip.Name = color == BlipColor.Red
                    ? L("PM_VehicleSeizedBlip", "Phương tiện bị niêm phong")
                    : L("PM_VehicleReturnedBlip", "Phương tiện đã trả lại");
            }
        }
        catch (Exception ex)
        {
            Log("RefreshSinglePersistentVehicleBlip failed: " + ex.ToString());
        }
    }

    public static void RefreshVehicleBlipsForCurrentCharacter()
    {
        try
        {
            lock (_persistVehicles)
            {
                foreach (var pv in _persistVehicles)
                {
                    try
                    {
                        RefreshSinglePersistentVehicleBlip(pv);
                    }
                    catch (Exception ex)
                    {
                        Log("RefreshVehicleBlipsForCurrentCharacter inner failed: " + ex.ToString());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("RefreshVehicleBlipsForCurrentCharacter failed: " + ex.ToString());
        }
    }

    private static BlipSprite GetVehicleBlipSprite(Vehicle v)
    {
        try
        {
            if (v != null && v.Exists())
            {
                if (v.IsBlimp) return BlipSprite.Blimp;
                if (v.IsSubmarine) return BlipSprite.Sub;
                if (v.IsBoat) return BlipSprite.Boat;
                if (v.IsHelicopter) return BlipSprite.Helicopter;
                if (v.IsPlane) return BlipSprite.Plane;
                if (v.IsBike || v.IsBicycle || v.IsMotorcycle) return BlipSprite.PersonalVehicleBike;
                if (v.IsTrain) return BlipSprite.PersonalVehicleCar; // không có icon train riêng trong enum SHVDN hiện tại
                return BlipSprite.PersonalVehicleCar;
            }
        }
        catch { }

        return BlipSprite.PersonalVehicleCar;
    }

    private static BlipSprite GetVehicleBlipSprite(uint modelHash)
    {
        try
        {
            int vehicleClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS_FROM_NAME, modelHash);

            switch (vehicleClass)
            {
                case 8:   // Motorcycles
                case 13:  // Cycles
                    return BlipSprite.PersonalVehicleBike;

                case 14:  // Boats
                    return BlipSprite.Boat;

                case 15:  // Helicopters
                    return BlipSprite.Helicopter;

                case 16:  // Planes
                    return BlipSprite.Plane;

                default:
                    break;
            }

            // Nếu model properties khả dụng thì dùng thêm lớp đặc biệt
            try
            {
                var m = new Model((int)modelHash);
                if (m.IsBlimp) return BlipSprite.Blimp;
                if (m.IsSubmarine) return BlipSprite.Sub;
                if (m.IsMotorcycle) return BlipSprite.PersonalVehicleBike;
            }
            catch { }

            return BlipSprite.PersonalVehicleCar;
        }
        catch
        {
            return BlipSprite.PersonalVehicleCar;
        }
    }

    // ================= Hệ thống Lưu File =================
    private static void SaveWeaponFileInternal()
    {
        try
        {
            EnsureDataFolderExists();

            var lines = new List<string>();
            lock (_characterWeapons)
            {
                foreach (var kvp in _characterWeapons)
                {
                    foreach (var wp in kvp.Value)
                    {
                        string comps = wp.Components != null ? string.Join(",", wp.Components) : "";
                        lines.Add($"{kvp.Key}|{wp.Hash}|{wp.Ammo}|{wp.Tint}|{comps}");
                    }
                }
            }

            WriteLinesAtomic(WeaponsFile, lines.ToArray());
            _weaponsDirty = false;
        }
        catch (Exception ex)
        {
            Log("SaveWeaponFileInternal failed: " + ex.ToString());
        }
    }

    private static void SaveVehiclesFileInternal()
    {
        try
        {
            EnsureDataFolderExists();

            string[] lines;
            lock (_persistVehicles)
            {
                // --- REFRESH: for any PV that currently has a runtime vehicle, read live values before save
                foreach (var pv in _persistVehicles)
                {
                    try
                    {
                        if (pv.RuntimeVehicle != null)
                        {
                            bool ex = false;
                            try { ex = pv.RuntimeVehicle.Exists(); } catch { ex = false; }
                            if (ex) RefreshPersistentFromRuntime(pv);
                        }
                    }
                    catch { /* tolerate per-entry errors */ }
                }

                lines = _persistVehicles.Select(v =>
                {
                    string modsSerialized = "";
                    if (v.Mods != null && v.Mods.Count > 0)
                    {
                        var parts = v.Mods.Select(kv => $"{kv.Key}:{kv.Value}");
                        modsSerialized = string.Join(",", parts);
                    }

                    int turbo = v.TurboOn ? 1 : 0;

                    // extrasExist: comma-separated integer list (e.g. "1,3,5")
                    string extrasSerialized = "";
                    try
                    {
                        if (v.ExtrasExist != null && v.ExtrasExist.Count > 0)
                            extrasSerialized = string.Join(",", v.ExtrasExist);
                    }
                    catch { extrasSerialized = ""; }

                    // tyre smoke as "r,g,b"
                    string tyreRgb = "";
                    try
                    {
                        if (v.TyreSmokeColor != null && v.TyreSmokeColor.Length >= 3)
                            tyreRgb = $"{v.TyreSmokeColor[0]},{v.TyreSmokeColor[1]},{v.TyreSmokeColor[2]}";
                    }
                    catch { tyreRgb = ""; }

                    // neon color as "r,g,b" or empty
                    string neonRgb = "";
                    try
                    {
                        if (v.NeonColor != null && v.NeonColor.Length >= 3)
                            neonRgb = $"{v.NeonColor[0]},{v.NeonColor[1]},{v.NeonColor[2]}";
                    }
                    catch { neonRgb = ""; }

                    string dashStr = v.DashboardColor.HasValue ? v.DashboardColor.Value.ToString(CultureInfo.InvariantCulture) : "";

                    // model string: "Hakuchou Drag (0xF0C2A91F)"  -- đảm bảo không có ký tự '|' trong tên
                    string modelString = GetVehicleDisplayName(v.ModelHash) ?? $"0x{v.ModelHash:X}";
                    modelString = modelString.Replace("|", ""); // tránh phá định dạng file
                    string hexSuffix = $" (0x{v.ModelHash:X})";
                    if (!modelString.EndsWith($"(0x{v.ModelHash:X})", StringComparison.OrdinalIgnoreCase))
                    {
                        // nếu GetVehicleDisplayName đã bao gồm hex (hiếm), vẫn đảm bảo có hex trong ngoặc
                        modelString = $"{modelString}{hexSuffix}";
                    }

                    string haoBranchStr = v.HaoUpgradeBranch.HasValue
                        ? v.HaoUpgradeBranch.Value.ToString(CultureInfo.InvariantCulture)
                        : "";

                    string haoAppliedStr = v.HaoUpgradeApplied ? "1" : "0";

                    string customIconStr = v.CustomIconId.HasValue
                        ? v.CustomIconId.Value.ToString(CultureInfo.InvariantCulture)
                        : "";

                    string customScaleStr = v.CustomBlipScale.HasValue
                        ? v.CustomBlipScale.Value.ToString(CultureInfo.InvariantCulture)
                        : "";

                    return string.Format(CultureInfo.InvariantCulture,
                        "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}|{11}|{12}|{13}|{14}|{15}|{16}|{17}|{18}|{19}|{20}|{21}|{22}|{23}|{24}|{25}",
                        modelString,
                        v.Position.X, v.Position.Y, v.Position.Z,
                        v.Heading,
                        (v.Plate ?? "").Replace("|", ""),
                        v.OwnerModelHash,
                        v.AutoSpawnEnabled ? 1 : 0,
                        modsSerialized,
                        turbo,
                        v.PurchasePrice,
                        (v.SavedLivery.HasValue ? v.SavedLivery.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        (v.PrimaryColor.HasValue ? v.PrimaryColor.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        (v.SecondaryColor.HasValue ? v.SecondaryColor.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        (v.PearlColor.HasValue ? v.PearlColor.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        (v.WheelColor.HasValue ? v.WheelColor.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        (v.WindowTint.HasValue ? v.WindowTint.Value.ToString(CultureInfo.InvariantCulture) : ""),
                        tyreRgb,
                        extrasSerialized,
                        neonRgb,
                        dashStr,
                        v.IsCollateralLocked ? 1 : 0,
                        haoBranchStr,
                        haoAppliedStr,
                        customIconStr,
                        customScaleStr
                    );
                }).ToArray();
            }

            WriteLinesAtomic(VehiclesFile, lines);
            _vehiclesDirty = false;
        }
        catch (Exception ex)
        {
            Log("SaveVehiclesFileInternal failed: " + ex.ToString());
        }
    }

    public static string GetVehicleDisplayNamePublic(uint modelHash)
    {
        return GetVehicleDisplayName(modelHash);
    }

    private void TryLoadAll()
    {
        try
        {
            // --- LOAD WEAPONS ---
            var weaponLines = ReadAllLinesSafeTryBackups(WeaponsFile);
            foreach (var line in weaponLines)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var p = line.Split('|');
                    if (p.Length < 4) continue;

                    int cHash;
                    if (!int.TryParse(p[0], out cHash)) continue;
                    if (!_characterWeapons.ContainsKey(cHash)) _characterWeapons[cHash] = new List<PersistentWeapon>();

                    List<uint> comps = new List<uint>();
                    if (p.Length > 4 && !string.IsNullOrEmpty(p[4]))
                    {
                        foreach (var s in p[4].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            uint v;
                            if (uint.TryParse(s, out v)) comps.Add(v);
                        }
                    }

                    uint wpHash; int ammo; int tint;
                    if (!uint.TryParse(p[1], out wpHash)) continue;
                    if (!int.TryParse(p[2], out ammo)) ammo = 0;
                    if (!int.TryParse(p[3], out tint)) tint = 0;

                    _characterWeapons[cHash].Add(new PersistentWeapon
                    {
                        Hash = wpHash,
                        Ammo = ammo,
                        Tint = tint,
                        Components = comps
                    });
                }
                catch (Exception ex) { Log("TryLoadAll(weapons-line) failed: " + ex.ToString()); }
            }

            // --- LOAD VEHICLES ---
            var vehicleLines = ReadAllLinesSafeTryBackups(VehiclesFile);
            foreach (var line in vehicleLines)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var p = line.Split('|');
                    if (p.Length < 7) continue;

                    int ownerH = 0;
                    if (p.Length > 6) int.TryParse(p[6], out ownerH);

                    // --- modelHash parsing: chấp nhận "0xHEX" hoặc "Tên" hoặc "Tên (0xHEX)" ---
                    uint modelHash = 0;
                    string modelField = p[0].Trim();

                    bool parsed = false;
                    try
                    {
                        int idx = modelField.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            string hexPart = modelField.Substring(idx + 2).Trim();
                            if (uint.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out modelHash))
                                parsed = true;
                        }

                        if (!parsed)
                        {
                            if (uint.TryParse(modelField, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out modelHash))
                                parsed = true;
                        }
                    }
                    catch { parsed = false; }

                    if (!parsed)
                    {
                        if (!TryGetModelHashFromName(modelField, out modelHash))
                        {
                            Log($"TryLoadAll: unable to parse model field '{modelField}' into a modelHash; skipping line.");
                            continue;
                        }
                    }

                    float x = 0f, y = 0f, z = 0f, heading = 0f;
                    float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                    float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
                    float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
                    float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out heading);

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
                        var parts = p[8].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            try
                            {
                                var kv = part.Split(':');
                                if (kv.Length != 2) continue;
                                int mt = 0, mi = 0;
                                if (int.TryParse(kv[0], out mt) && int.TryParse(kv[1], out mi))
                                {
                                    modsDict[mt] = mi;
                                }
                            }
                            catch { }
                        }
                    }

                    int turbo = 0;
                    if (p.Length > 9) int.TryParse(p[9], out turbo);

                    int purchasePrice = 0;
                    if (p.Length > 10) int.TryParse(p[10], out purchasePrice);

                    // NEW/EXTENDED FIELDS (optional)
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
                    bool collateralLocked = false;
                    int? haoUpgradeBranch = null;
                    bool haoUpgradeApplied = false;
                    int? customIconId = null; // Khởi tạo thêm biến customIconId
                    float? customScale = null; // Khởi tạo thêm biến customScale

                    try
                    {
                        if (p.Length > 11 && !string.IsNullOrEmpty(p[11])) { int tmp; if (int.TryParse(p[11], out tmp)) savedLivery = tmp; }
                        if (p.Length > 12 && !string.IsNullOrEmpty(p[12])) { int tmp; if (int.TryParse(p[12], out tmp)) primary = tmp; }
                        if (p.Length > 13 && !string.IsNullOrEmpty(p[13])) { int tmp; if (int.TryParse(p[13], out tmp)) secondary = tmp; }
                        if (p.Length > 14 && !string.IsNullOrEmpty(p[14])) { int tmp; if (int.TryParse(p[14], out tmp)) pearl = tmp; }
                        if (p.Length > 15 && !string.IsNullOrEmpty(p[15])) { int tmp; if (int.TryParse(p[15], out tmp)) wheel = tmp; }
                        if (p.Length > 16 && !string.IsNullOrEmpty(p[16])) { int tmp; if (int.TryParse(p[16], out tmp)) windowTint = tmp; }

                        if (p.Length > 17 && !string.IsNullOrEmpty(p[17]))
                        {
                            var rr = p[17].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                            if (rr.Length >= 3)
                            {
                                int.TryParse(rr[0], out tyreRgb[0]);
                                int.TryParse(rr[1], out tyreRgb[1]);
                                int.TryParse(rr[2], out tyreRgb[2]);
                            }
                        }

                        if (p.Length > 18 && !string.IsNullOrEmpty(p[18]))
                        {
                            foreach (var s in p[18].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                int ei = 0;
                                if (int.TryParse(s, out ei)) extrasExist.Add(ei);
                            }
                        }

                        if (p.Length > 19 && !string.IsNullOrEmpty(p[19]))
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

                        if (p.Length > 20 && !string.IsNullOrEmpty(p[20]))
                        {
                            int tmp;
                            if (int.TryParse(p[20], out tmp)) dashColor = tmp;
                        }

                        if (p.Length > 21)
                        {
                            int tmp;
                            if (int.TryParse(p[21], out tmp))
                                collateralLocked = tmp != 0;
                        }

                        if (p.Length > 22 && !string.IsNullOrWhiteSpace(p[22]))
                        {
                            int tmp;
                            if (int.TryParse(p[22], out tmp) && (tmp == 0 || tmp == 1))
                                haoUpgradeBranch = tmp;
                        }

                        if (p.Length > 23 && !string.IsNullOrWhiteSpace(p[23]))
                        {
                            int tmp;
                            if (int.TryParse(p[23], out tmp))
                                haoUpgradeApplied = tmp != 0;
                        }

                        // Thêm logic kiểm tra p[24] cho TryLoadAll
                        if (p.Length > 24 && !string.IsNullOrWhiteSpace(p[24]))
                        {
                            int tmp;
                            if (int.TryParse(p[24], out tmp) && tmp >= MIN_CUSTOM_ICON_ID && tmp <= MAX_CUSTOM_ICON_ID)
                                customIconId = tmp;
                        }

                        // Thêm logic kiểm tra p[25] cho TryLoadAll
                        if (p.Length > 25 && !string.IsNullOrWhiteSpace(p[25]))
                        {
                            float tmp;
                            if (float.TryParse(p[25], NumberStyles.Float, CultureInfo.InvariantCulture, out tmp))
                                customScale = NormalizeCustomBlipScale(tmp);
                        }
                    }
                    catch { /* tolerate parse errors */ }

                    lock (_persistVehicles)
                    {
                        _persistVehicles.Add(new PersistentVehicle
                        {
                            ModelHash = modelHash,
                            Position = new Vector3(x, y, z),
                            Heading = heading,
                            Plate = p.Length > 5 ? p[5] : "",
                            OwnerModelHash = ownerH,
                            MapBlip = null,
                            RuntimeVehicle = null,
                            StopCounter = 0,
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
                            TyreSmokeColor = tyreRgb ?? new int[3] { 0, 0, 0 },
                            ExtrasExist = extrasExist ?? new List<int>(),
                            NeonColor = neonRgb,
                            DashboardColor = dashColor,
                            IsCollateralLocked = collateralLocked,
                            HaoUpgradeBranch = haoUpgradeBranch,
                            HaoUpgradeApplied = haoUpgradeApplied,

                            // Áp dụng gán thuộc tính mới đầy đủ vào Object Initializer tại TryLoadAll
                            CustomIconId = customIconId,
                            PreviewIconId = null,
                            CustomBlipScale = customScale,
                            PreviewBlipScale = null,
                        });
                    }
                }
                catch (Exception ex) { Log("TryLoadAll(vehicles-line) failed: " + ex.ToString()); }
            }

            // --- LOAD DESTROYED VEHICLES (Thêm theo hướng dẫn) ---
            try
            {
                LoadDestroyedVehiclesFileInternal();
                EnforceDestroyedVehicleCaps();
            }
            catch (Exception ex)
            {
                Log("TryLoadAll(destroyed-vehicles) failed: " + ex.ToString());
            }

            // --- SAU KHI LOAD: TRIM PER-CHARACTER (Giới hạn xe mỗi nhân vật)
            try
            {
                lock (_persistVehicles)
                {
                    var owners = _persistVehicles.Select(x => x.OwnerModelHash).Distinct().ToList();
                    foreach (var owner in owners)
                    {
                        while (_persistVehicles.Count(x => x.OwnerModelHash == owner) > MAX_VEHICLES_PER_CHAR)
                        {
                            var oldestUnlocked = _persistVehicles
                                .FirstOrDefault(x => x.OwnerModelHash == owner && !x.IsCollateralLocked);

                            if (oldestUnlocked == null)
                            {
                                Log($"TryLoadAll: owner 0x{owner:X} vượt MAX_VEHICLES_PER_CHAR nhưng không còn xe UNLOCKED để trim. Giữ nguyên xe thế chấp.");
                                break;
                            }

                            RemovePersistentVehicleSafe(oldestUnlocked);
                            _vehiclesDirty = true;

                            Log($"TryLoadAll: trimmed oldest UNLOCKED vehicle for owner 0x{owner:X} to respect MAX_VEHICLES_PER_CHAR");
                        }
                    }
                }
            }
            catch (Exception ex) { Log("TryLoadAll per-character trim failed: " + ex.ToString()); }

            // --- KIỂM TRA GIỚI HẠN SAU KHI LOAD XONG (Global cap)
            try
            {
                lock (_persistVehicles)
                {
                    while (_persistVehicles.Count > MAX_PERSIST_VEHICLES)
                    {
                        var oldestUnlocked = _persistVehicles.FirstOrDefault(x => !x.IsCollateralLocked);
                        if (oldestUnlocked == null)
                        {
                            Log("TryLoadAll: reached MAX_PERSIST_VEHICLES but no UNLOCKED vehicle remains to trim. Preserving collateral-locked vehicles.");
                            break;
                        }

                        RemovePersistentVehicleSafe(oldestUnlocked);
                        _vehiclesDirty = true;

                        Log($"TryLoadAll: trimmed oldest UNLOCKED persisted vehicle to respect MAX_PERSIST_VEHICLES");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("TryLoadAll global cap trim failed: " + ex.ToString());
            }

            // --- ĐĂNG KÝ VŨ KHÍ HIỆN TẠI ---
            try
            {
                RegisterCurrentWeapon();
            }
            catch
            {
                // Catch lặng lẽ để không làm gián đoạn quá trình load chính
            }
        }
        catch (Exception ex)
        {
            Log("TryLoadAll failed: " + ex.ToString());
        }
    }

    private void EnsureMmiInsuranceMenuCreated()
    {
        try
        {
            if (_mmiInsuranceMenuReady)
                return;

            _mmiInsuranceMenu = new NativeMenu(PM_MenuInsuranceTitle, PM_MenuInsuranceSubtitle);
            _menuPool.Add(_mmiInsuranceMenu);
            _mmiInsuranceMenu.Visible = false;
            ConfigureKeyboardOnlyMmiMenu(_mmiInsuranceMenu);

            _mmiInsuranceMenuReady = true;
        }
        catch { }
    }

    private void EnsureMmiInsuranceCharacterMenuCreated()
    {
        try
        {
            if (_mmiInsuranceCharacterMenuReady)
                return;

            _mmiInsuranceCharacterMenu = new NativeMenu(PM_MenuInsuranceTitle, PM_MenuCharacterSubtitle);
            _menuPool.Add(_mmiInsuranceCharacterMenu);
            _mmiInsuranceCharacterMenu.Visible = false;
            ConfigureKeyboardOnlyMmiMenu(_mmiInsuranceCharacterMenu);

            _mmiInsuranceCharacterMenuReady = true;
        }
        catch { }
    }

    private void EnsureMmiDestroyedVehicleMenuCreated()
    {
        try
        {
            if (_mmiDestroyedVehicleMenuReady)
                return;

            _mmiDestroyedVehicleMenu = new NativeMenu(PM_MenuInsuranceTitle, PM_MenuDestroyedSubtitle);
            _menuPool.Add(_mmiDestroyedVehicleMenu);
            _mmiDestroyedVehicleMenu.Visible = false;
            ConfigureKeyboardOnlyMmiMenu(_mmiDestroyedVehicleMenu);

            _mmiDestroyedVehicleMenuReady = true;
        }
        catch { }
    }

    private void ConfigureKeyboardOnlyMmiMenu(NativeMenu menu)
    {
        if (menu == null) return;

        // Tắt điều hướng bằng chuột trong menu để chuột dùng camera như bình thường
        menu.MouseBehavior = MenuMouseBehavior.Disabled;

        // Không kéo con trỏ về menu khi mở
        menu.ResetCursorWhenOpened = false;

        // Không đóng menu do click lệch
        menu.CloseOnInvalidClick = false;

        // Cho phép camera/mouse của game hoạt động bình thường khi menu đang mở
        menu.RotateCamera = true;
    }

    private void OpenMmiInsuranceMenu()
    {
        try
        {
            EnsureMmiInsuranceMenuCreated();
            BuildMmiInsuranceMenu();
            ConfigureKeyboardOnlyMmiMenu(_mmiInsuranceMenu);

            if (_mmiInsuranceCharacterMenu != null) _mmiInsuranceCharacterMenu.Visible = false;
            if (_mmiDestroyedVehicleMenu != null) _mmiDestroyedVehicleMenu.Visible = false;

            if (_mmiInsuranceMenu != null)
            {
                _mmiInsuranceMenu.Visible = true;
                Interval = 0;
            }
        }
        catch { }
    }

    private void OpenMmiInsuranceCharacterMenu()
    {
        try
        {
            EnsureMmiInsuranceCharacterMenuCreated();
            BuildMmiInsuranceCharacterMenu();
            ConfigureKeyboardOnlyMmiMenu(_mmiInsuranceCharacterMenu);

            if (_mmiInsuranceMenu != null) _mmiInsuranceMenu.Visible = false;
            if (_mmiDestroyedVehicleMenu != null) _mmiDestroyedVehicleMenu.Visible = false;

            if (_mmiInsuranceCharacterMenu != null)
            {
                _mmiInsuranceCharacterMenu.Visible = true;
                Interval = 0;
            }
        }
        catch { }
    }

    private void OpenMmiDestroyedVehicleMenu(int ownerHash)
    {
        try
        {
            _mmiSelectedOwnerHash = ownerHash;

            EnsureMmiDestroyedVehicleMenuCreated();
            BuildMmiDestroyedVehicleMenu(ownerHash);
            ConfigureKeyboardOnlyMmiMenu(_mmiDestroyedVehicleMenu);

            if (_mmiInsuranceMenu != null) _mmiInsuranceMenu.Visible = false;
            if (_mmiInsuranceCharacterMenu != null) _mmiInsuranceCharacterMenu.Visible = false;

            if (_mmiDestroyedVehicleMenu != null)
            {
                _mmiDestroyedVehicleMenu.Visible = true;
                Interval = 0;
            }
        }
        catch { }
    }

    private void CloseAllMmiInsuranceMenus()
    {
        try
        {
            if (_mmiInsuranceMenu != null) _mmiInsuranceMenu.Visible = false;
            if (_mmiInsuranceCharacterMenu != null) _mmiInsuranceCharacterMenu.Visible = false;
            if (_mmiDestroyedVehicleMenu != null) _mmiDestroyedVehicleMenu.Visible = false;

            _mmiDestroyedVehicleEntries.Clear();
            _mmiDestroyedVehicleCheckboxItems.Clear();
            _mmiSelectedOwnerHash = 0;
        }
        catch { }

        Interval = 1000;
    }

    private void BuildMmiInsuranceMenu()
    {
        if (_mmiInsuranceMenu == null)
            return;

        _mmiInsuranceMenu.Clear();

        _mmiStandardRestoreItem = new NativeCheckboxItem(PM_MenuRestoreWeapons, false);

        _mmiRestoreDestroyedItem = new NativeItem(PM_MenuRestoreDestroyed);
        _mmiRestoreDestroyedItem.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";

        _mmiInsuranceConfirmItem = new NativeItem(PM_MenuConfirmService);
        _mmiInsuranceCancelItem = new NativeItem(PM_MenuCancelInsurance);

        _mmiRestoreDestroyedItem.Activated += (s, e) =>
        {
            if (_mmiInsuranceMenu != null) _mmiInsuranceMenu.Visible = false;
            OpenMmiInsuranceCharacterMenu();
        };

        _mmiInsuranceConfirmItem.Activated += (s, e) =>
        {
            try
            {
                if (_mmiStandardRestoreItem != null && _mmiStandardRestoreItem.Checked)
                {
                    RestoreForCurrentPlayer();
                }
                else
                {
                    GTA.UI.Screen.ShowSubtitle(PM_InsuranceChooseOne, 2500);
                    return;
                }
            }
            finally
            {
                CloseAllMmiInsuranceMenus();
            }
        };

        _mmiInsuranceCancelItem.Activated += (s, e) =>
        {
            CloseAllMmiInsuranceMenus();
        };

        _mmiInsuranceMenu.Add(_mmiStandardRestoreItem);
        _mmiInsuranceMenu.Add(_mmiRestoreDestroyedItem);
        _mmiInsuranceMenu.Add(_mmiInsuranceConfirmItem);
        _mmiInsuranceMenu.Add(_mmiInsuranceCancelItem);
    }

    private void BuildMmiInsuranceCharacterMenu()
    {
        if (_mmiInsuranceCharacterMenu == null)
            return;

        _mmiInsuranceCharacterMenu.Clear();

        var f = new NativeItem(PM_MenuFranklin);
        f.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";

        var m = new NativeItem(PM_MenuMichael);
        m.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";

        var t = new NativeItem(PM_MenuTrevor);
        t.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
        var cancel = new NativeItem(PM_MenuCancelCharacter);

        f.Activated += (s, e) => { if (_mmiInsuranceCharacterMenu != null) _mmiInsuranceCharacterMenu.Visible = false; OpenMmiDestroyedVehicleMenu(-1692214353); };
        m.Activated += (s, e) => { if (_mmiInsuranceCharacterMenu != null) _mmiInsuranceCharacterMenu.Visible = false; OpenMmiDestroyedVehicleMenu(225514697); };
        t.Activated += (s, e) => { if (_mmiInsuranceCharacterMenu != null) _mmiInsuranceCharacterMenu.Visible = false; OpenMmiDestroyedVehicleMenu(-1686040670); };

        cancel.Activated += (s, e) =>
        {
            if (_mmiInsuranceCharacterMenu != null) _mmiInsuranceCharacterMenu.Visible = false;
            OpenMmiInsuranceMenu();
        };

        _mmiInsuranceCharacterMenu.Add(f);
        _mmiInsuranceCharacterMenu.Add(m);
        _mmiInsuranceCharacterMenu.Add(t);
        _mmiInsuranceCharacterMenu.Add(cancel);
    }

    private void BuildMmiDestroyedVehicleMenu(int ownerHash)
    {
        if (_mmiDestroyedVehicleMenu == null)
            return;

        _mmiDestroyedVehicleMenu.Clear();
        _mmiDestroyedVehicleEntries.Clear();
        _mmiDestroyedVehicleCheckboxItems.Clear();

        List<PersistentVehicle> ownedDestroyed;
        lock (_destroyedVehicles)
        {
            ownedDestroyed = _destroyedVehicles
                .Where(v => v != null && v.OwnerModelHash == ownerHash)
                .ToList();
        }

        if (ownedDestroyed.Count == 0)
        {
            _mmiDestroyedVehicleMenu.Add(new NativeItem(PM_MenuNoDestroyedVehicles));
        }
        else
        {
            int index = 1;
            foreach (var pv in ownedDestroyed)
            {
                var captured = pv;

                string name = GetVehicleDisplayName(captured.ModelHash);
                var item = new NativeCheckboxItem($"{index}. {name}", false);

                int fee = ComputeInsuranceFee(captured);
                item.Description = string.Format(CultureInfo.InvariantCulture, PM_MenuInsuranceDescFormat, captured.Plate, fee);

                item.CheckboxChanged += (s, e) =>
                {
                    if (_mmiDestroyedUiSync)
                        return;

                    try
                    {
                        _mmiDestroyedUiSync = true;

                        if (item.Checked)
                        {
                            for (int i = 0; i < _mmiDestroyedVehicleCheckboxItems.Count; i++)
                            {
                                if (!ReferenceEquals(_mmiDestroyedVehicleCheckboxItems[i], item))
                                    _mmiDestroyedVehicleCheckboxItems[i].Checked = false;
                            }
                        }
                    }
                    finally
                    {
                        _mmiDestroyedUiSync = false;
                    }
                };

                _mmiDestroyedVehicleEntries.Add(captured);
                _mmiDestroyedVehicleCheckboxItems.Add(item);
                _mmiDestroyedVehicleMenu.Add(item);
                index++;
            }
        }

        var confirm = new NativeItem(PM_MenuConfirmRecoverVehicle);
        var cancel = new NativeItem(PM_MenuCancelDestroyed);

        confirm.Activated += (s, e) =>
        {
            ConfirmDestroyedVehicleRecovery(ownerHash);
        };

        cancel.Activated += (s, e) =>
        {
            if (_mmiDestroyedVehicleMenu != null) _mmiDestroyedVehicleMenu.Visible = false;
            OpenMmiInsuranceCharacterMenu();
        };

        _mmiDestroyedVehicleMenu.Add(confirm);
        _mmiDestroyedVehicleMenu.Add(cancel);
    }

    private static int ComputeInsuranceFee(PersistentVehicle pv)
    {
        try
        {
            if (pv == null || pv.PurchasePrice <= 0)
                return 0;

            decimal fee = (decimal)pv.PurchasePrice * (decimal)INSURANCE_RESTORE_PERCENT;
            return (int)Math.Round(fee, MidpointRounding.AwayFromZero);
        }
        catch
        {
            return 0;
        }
    }

    private static bool CollateralStateLineMatchesVehicle(string line, PersistentVehicle pv)
    {
        try
        {
            if (pv == null || string.IsNullOrWhiteSpace(line))
                return false;

            var p = line.Split('|');
            if (p.Length < 6)
                return false;

            uint modelHash = 0;
            string modelField = p[0].Trim();

            bool parsed = false;
            try
            {
                int idx = modelField.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string hexPart = modelField.Substring(idx + 2).Trim();
                    if (uint.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out modelHash))
                        parsed = true;
                }

                if (!parsed)
                {
                    if (uint.TryParse(modelField, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out modelHash))
                        parsed = true;
                }
            }
            catch
            {
                parsed = false;
            }

            if (!parsed)
                return false;

            float x = 0f, y = 0f, z = 0f;
            float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x);
            float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
            float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out z);

            string linePlate = (p[1] ?? "").Trim();
            string pvPlate = (pv.Plate ?? "").Trim();

            string Normalize(string plate)
            {
                if (string.IsNullOrWhiteSpace(plate))
                    return "";

                return new string(plate.Trim().Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
            }

            string BuildKey(uint h, string plate, Vector3 pos)
            {
                string normalizedPlate = Normalize(plate);

                if (!string.IsNullOrEmpty(normalizedPlate))
                    return $"{h:X}|PLATE|{normalizedPlate}";

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:X}|POS|{1:0.0}|{2:0.0}|{3:0.0}",
                    h, pos.X, pos.Y, pos.Z);
            }

            string lineKey = BuildKey(modelHash, linePlate, new Vector3(x, y, z));
            string vehicleKey = BuildKey(pv.ModelHash, pvPlate, pv.Position);

            if (string.Equals(lineKey, vehicleKey, StringComparison.OrdinalIgnoreCase))
                return true;

            if (modelHash == pv.ModelHash &&
                Normalize(linePlate) == Normalize(pvPlate))
                return true;

            if (modelHash == pv.ModelHash)
            {
                Vector3 linePos = new Vector3(x, y, z);
                if (linePos.DistanceTo2D(pv.Position) < 5f)
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static float? GetPersistedVehicleScaleByKey(string vehicleKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vehicleKey))
                return null;

            lock (_persistVehicles)
            {
                var pv = _persistVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null) return pv.CustomBlipScale;
            }

            lock (_destroyedVehicles)
            {
                var pv = _destroyedVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null) return pv.CustomBlipScale;
            }
        }
        catch { }

        return null;
    }

    public static float GetVehicleEffectiveScaleByKey(string vehicleKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vehicleKey))
                return DEFAULT_CUSTOM_BLIP_SCALE;

            lock (_persistVehicles)
            {
                var pv = _persistVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null) return GetEffectiveVehicleBlipScale(pv);
            }

            lock (_destroyedVehicles)
            {
                var pv = _destroyedVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null) return GetEffectiveVehicleBlipScale(pv);
            }
        }
        catch { }

        return DEFAULT_CUSTOM_BLIP_SCALE;
    }

    public static bool SetVehicleCustomScaleByKey(string vehicleKey, float scale, bool commit)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vehicleKey))
                return false;

            float normalized = NormalizeCustomBlipScale(scale);
            float? stored = Math.Abs(normalized - DEFAULT_CUSTOM_BLIP_SCALE) < 0.001f ? (float?)null : normalized;

            bool touched = false;

            lock (_persistVehicles)
            {
                var pv = _persistVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null)
                {
                    if (commit)
                    {
                        pv.CustomBlipScale = stored;
                        pv.PreviewBlipScale = null;
                        _vehiclesDirty = true;
                    }
                    else
                    {
                        pv.PreviewBlipScale = stored;
                    }

                    try { RefreshSinglePersistentVehicleBlip(pv); } catch { }
                    touched = true;
                }
            }

            lock (_destroyedVehicles)
            {
                var pv = _destroyedVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null)
                {
                    if (commit)
                    {
                        pv.CustomBlipScale = stored;
                        pv.PreviewBlipScale = null;
                        _destroyedVehiclesDirty = true;
                    }
                    else
                    {
                        pv.PreviewBlipScale = stored;
                    }

                    try { RefreshSinglePersistentVehicleBlip(pv); } catch { }
                    touched = true;
                }
            }

            if (commit)
            {
                try { if (_vehiclesDirty) SaveVehiclesFileInternal(); } catch { }
                try { if (_destroyedVehiclesDirty) SaveDestroyedVehiclesFileInternal(); } catch { }
            }

            return touched;
        }
        catch
        {
            return false;
        }
    }

    public static bool ClearVehiclePreviewScaleByKey(string vehicleKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vehicleKey))
                return false;

            bool touched = false;

            lock (_persistVehicles)
            {
                var pv = _persistVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null)
                {
                    pv.PreviewBlipScale = null;
                    try { RefreshSinglePersistentVehicleBlip(pv); } catch { }
                    touched = true;
                }
            }

            lock (_destroyedVehicles)
            {
                var pv = _destroyedVehicles.FirstOrDefault(x => string.Equals(BuildVehicleKey(x), vehicleKey, StringComparison.OrdinalIgnoreCase));
                if (pv != null)
                {
                    pv.PreviewBlipScale = null;
                    try { RefreshSinglePersistentVehicleBlip(pv); } catch { }
                    touched = true;
                }
            }

            return touched;
        }
        catch
        {
            return false;
        }
    }

    public static void UpdateHaoUpgradeState(Vehicle veh, int? haoBranch, bool haoApplied)
    {
        try
        {
            if (veh == null || !veh.Exists())
                return;

            lock (_persistVehicles)
            {
                var pv = _persistVehicles.FirstOrDefault(p =>
                    p.RuntimeVehicle != null &&
                    p.RuntimeVehicle.Exists() &&
                    p.RuntimeVehicle.Handle == veh.Handle);

                if (pv == null)
                {
                    int ownerHash = Game.Player?.Character?.Model.Hash ?? 0;
                    uint model = (uint)veh.Model.Hash;
                    var pos = veh.Position;

                    pv = _persistVehicles.FirstOrDefault(p =>
                        p.ModelHash == model &&
                        p.OwnerModelHash == ownerHash &&
                        p.Position.DistanceTo2D(pos) < 5f);
                }

                if (pv != null)
                {
                    pv.HaoUpgradeBranch = haoBranch;
                    pv.HaoUpgradeApplied = haoApplied;
                    _vehiclesDirty = true;
                }
            }
        }
        catch { }
    }

    private void RemoveCollateralStateRecordForRecoveredVehicle(PersistentVehicle pv)
    {
        try
        {
            if (pv == null || pv.OwnerModelHash == 0)
                return;

            Directory.CreateDirectory(FleecaCollateralStateRoot);

            string file = Path.Combine(FleecaCollateralStateRoot, $"collateral_state_{pv.OwnerModelHash}.dat");
            if (!File.Exists(file))
                return;

            var kept = new List<string>();
            var lines = File.ReadAllLines(file);

            foreach (var line in lines)
            {
                try
                {
                    if (CollateralStateLineMatchesVehicle(line, pv))
                        continue;

                    kept.Add(line);
                }
                catch
                {
                    kept.Add(line);
                }
            }

            File.WriteAllLines(file, kept.ToArray());
        }
        catch (Exception ex)
        {
            Log("RemoveCollateralStateRecordForRecoveredVehicle failed: " + ex.ToString());
        }
    }

    private PersistentVehicle CloneVehicleForStorage(PersistentVehicle src)
    {
        if (src == null) return null;

        return new PersistentVehicle
        {
            ModelHash = src.ModelHash,
            Position = src.Position,
            Heading = src.Heading,
            Plate = src.Plate,
            RuntimeVehicle = null,
            MapBlip = null,
            StopCounter = 0,
            OwnerModelHash = src.OwnerModelHash,
            AutoSpawnEnabled = src.AutoSpawnEnabled,
            Mods = src.Mods != null ? new Dictionary<int, int>(src.Mods) : new Dictionary<int, int>(),
            TurboOn = src.TurboOn,
            PurchasePrice = src.PurchasePrice,
            IsCollateralLocked = src.IsCollateralLocked,
            HaoUpgradeBranch = src.HaoUpgradeBranch,
            HaoUpgradeApplied = src.HaoUpgradeApplied,

            // Cập nhật cấu hình Clone theo hướng dẫn mới
            CustomIconId = src.CustomIconId,
            PreviewIconId = null,
            CustomBlipScale = src.CustomBlipScale,
            PreviewBlipScale = null,

            SavedLivery = src.SavedLivery,
            PrimaryColor = src.PrimaryColor,
            SecondaryColor = src.SecondaryColor,
            PearlColor = src.PearlColor,
            WheelColor = src.WheelColor,
            WindowTint = src.WindowTint,
            TyreSmokeColor = src.TyreSmokeColor != null ? (int[])src.TyreSmokeColor.Clone() : new int[3] { 0, 0, 0 },
            ExtrasExist = src.ExtrasExist != null ? new List<int>(src.ExtrasExist) : new List<int>(),
            NeonColor = src.NeonColor != null ? (int[])src.NeonColor.Clone() : null,
            DashboardColor = src.DashboardColor,
            SuppressBlipWhileOccupied = false
        };
    }

    private void ConfirmDestroyedVehicleRecovery(int ownerHash)
    {
        try
        {
            var selected = new List<PersistentVehicle>();

            for (int i = 0; i < _mmiDestroyedVehicleCheckboxItems.Count && i < _mmiDestroyedVehicleEntries.Count; i++)
            {
                if (_mmiDestroyedVehicleCheckboxItems[i].Checked)
                    selected.Add(_mmiDestroyedVehicleEntries[i]);
            }

            if (selected.Count == 0)
            {
                GTA.UI.Screen.ShowSubtitle(PM_InsuranceChooseAtLeastOne, 2500);
                return;
            }

            int totalFee = 0;
            foreach (var pv in selected)
                totalFee += ComputeInsuranceFee(pv);

            if (Game.Player.Money < totalFee)
            {
                GTA.UI.Screen.ShowSubtitle(string.Format(CultureInfo.InvariantCulture, PM_InsuranceNotEnoughMoney, totalFee), 3000);
                return;
            }

            Game.Player.Money -= totalFee;

            int restoredCount = 0;

            foreach (var src in selected)
            {
                try
                {
                    PersistentVehicle restored = CloneVehicleForStorage(src);
                    if (restored == null)
                        continue;

                    restored.OwnerModelHash = ownerHash;
                    restored.IsCollateralLocked = false;   // xe khôi phục không còn là xe thế chấp
                    restored.AutoSpawnEnabled = false;
                    restored.RuntimeVehicle = null;
                    restored.MapBlip = null;

                    // Quan trọng: xóa dấu vết thế chấp cũ của xe này khỏi file collateral
                    // để Fleeca không đồng bộ lại thành xe đỏ nữa.
                    RemoveCollateralStateRecordForRecoveredVehicle(src);

                    lock (_destroyedVehicles)
                    {
                        var found = _destroyedVehicles.FirstOrDefault(x => x == src);
                        if (found != null)
                            _destroyedVehicles.Remove(found);
                    }

                    lock (_persistVehicles)
                    {
                        EnforceActiveVehicleCapForOwner(ownerHash);

                        _persistVehicles.Add(restored);
                        _vehiclesDirty = true;
                    }

                    Vector3 safePos;
                    float safeHeading;

                    if (TryFindSafeInsuranceSpawnPoint(src.Position, out safePos, out safeHeading))
                    {
                        restored.Position = safePos;
                        restored.Heading = safeHeading;
                    }
                    else
                    {
                        // fallback tối thiểu: vẫn cố đưa sang node đường gần nhất
                        Vector3 street = World.GetNextPositionOnStreet(src.Position);
                        if (street != Vector3.Zero)
                            restored.Position = street;
                    }

                    SpawnPersistentVehicle(restored);
                    restoredCount++;
                }
                catch (Exception ex)
                {
                    Log("ConfirmDestroyedVehicleRecovery item failed: " + ex.ToString());
                }
            }

            _destroyedVehiclesDirty = true;
            SaveVehiclesFileInternal();
            SaveDestroyedVehiclesFileInternal();

            ShowFeedMessage(
                PM_FeedInsuranceSender,
                PM_FeedInsuranceSubject,
                string.Format(CultureInfo.InvariantCulture, PM_FeedInsuranceBody, restoredCount, totalFee));

            CloseAllMmiInsuranceMenus();
        }
        catch (Exception ex)
        {
            Log("ConfirmDestroyedVehicleRecovery failed: " + ex.ToString());
            GTA.UI.Notification.Show(PM_InsuranceRecoveryFailed);
        }
    }

    private void EnforceActiveVehicleCapForOwner(int ownerHash)
    {
        try
        {
            while (_persistVehicles.Count(x => x.OwnerModelHash == ownerHash) >= MAX_VEHICLES_PER_CHAR)
            {
                var oldestUnlocked = _persistVehicles.FirstOrDefault(x => x.OwnerModelHash == ownerHash && !x.IsCollateralLocked);
                if (oldestUnlocked == null)
                    break;

                RemovePersistentVehicleSafe(oldestUnlocked);
            }
        }
        catch (Exception ex)
        {
            Log("EnforceActiveVehicleCapForOwner failed: " + ex.ToString());
        }
    }

    private void EnsurePersistentForCurrentPlayer() { }

    private void EnforceF2OnlyAfterLoad()
    {
        try
        {
            bool anyChange = false;
            lock (_persistVehicles)
            {
                foreach (var pv in _persistVehicles)
                {
                    try
                    {
                        if (pv.MapBlip != null)
                        {
                            try { if (pv.MapBlip.Exists()) pv.MapBlip.Delete(); } catch (Exception ex) { Log("EnforceF2AfterLoad remove blip: " + ex.ToString()); }
                            pv.MapBlip = null;
                        }

                        if (pv.AutoSpawnEnabled)
                        {
                            pv.AutoSpawnEnabled = false;
                            anyChange = true;
                        }
                    }
                    catch (Exception ex) { Log("EnforceF2AfterLoad inner: " + ex.ToString()); }
                }
            }

            if (anyChange)
            {
                _vehiclesDirty = true;
                try { SaveVehiclesFileInternal(); }
                catch (Exception ex) { Log("EnforceF2AfterLoad SaveVehiclesFileInternal failed: " + ex.ToString()); }
            }
        }
        catch (Exception ex)
        {
            Log("EnforceF2OnlyAfterLoad failed: " + ex.ToString());
        }
    }

    private int GetCurrentPedHandleSafe() => Game.Player.Character?.Handle ?? 0;
    private static void SafeRemoveBlip(Blip b) { try { if (b != null && b.Exists()) b.Delete(); } catch (Exception ex) { Log("SafeRemoveBlip: " + ex.ToString()); } }
    private static string SafeGetPlate(Vehicle v) { try { return v.Mods.LicensePlate ?? ""; } catch (Exception ex) { Log("SafeGetPlate: " + ex.ToString()); return ""; } }

    // Xóa an toàn 1 PersistentVehicle khỏi danh sách (không cố gắng xóa runtime vehicle trong world).
    private static float ClampStat(float value)
    {
        if (value < 0f) return 0f;
        if (value > 100f) return 100f;
        return value;
    }

    private static float NormalizeStat(float raw, float cap)
    {
        if (cap <= 0f) return 0f;
        return ClampStat((raw / cap) * 100f);
    }

    private static float NormalizeCustomBlipScale(float scale)
    {
        if (float.IsNaN(scale) || float.IsInfinity(scale))
            return DEFAULT_CUSTOM_BLIP_SCALE;

        if (scale < MIN_CUSTOM_BLIP_SCALE) scale = MIN_CUSTOM_BLIP_SCALE;
        if (scale > MAX_CUSTOM_BLIP_SCALE) scale = MAX_CUSTOM_BLIP_SCALE;

        return (float)Math.Round(scale, 2, MidpointRounding.AwayFromZero);
    }

    private static float GetDefaultVehicleBlipScale(PersistentVehicle pv)
    {
        return DEFAULT_CUSTOM_BLIP_SCALE;
    }

    private static float GetEffectiveVehicleBlipScale(PersistentVehicle pv)
    {
        try
        {
            if (pv != null)
            {
                if (pv.PreviewBlipScale.HasValue)
                    return NormalizeCustomBlipScale(pv.PreviewBlipScale.Value);

                if (pv.CustomBlipScale.HasValue)
                    return NormalizeCustomBlipScale(pv.CustomBlipScale.Value);
            }
        }
        catch { }

        return GetDefaultVehicleBlipScale(pv);
    }

    private static float ResolveVehicleBlipScale(PersistentVehicle pv)
    {
        return GetEffectiveVehicleBlipScale(pv);
    }

    private static void ShowFeedMessage(string sender, string subject, string body)
    {
        try
        {
            Notification.Show(NotificationIcon.MpMorsMutual, sender, subject, body);
        }
        catch
        {
            GTA.UI.Notification.Show(body);
        }
    }

    private static void RemovePersistentVehicleSafe(PersistentVehicle pv)
    {
        if (pv == null) return;
        try
        {
            lock (_persistVehicles)
            {
                try
                {
                    // Remove map blip if any
                    try { SafeRemoveBlip(pv.MapBlip); pv.MapBlip = null; } catch { }

                    // Do NOT forcibly Delete() the runtime vehicle to avoid interfering with gameplay.
                    // Just drop the runtime reference so Restore/F2 won't treat it as persistent anymore.
                    try { pv.RuntimeVehicle = null; } catch { }

                    // Remove from the list if present
                    var found = _persistVehicles.FirstOrDefault(x => x == pv);
                    if (found != null)
                    {
                        _persistVehicles.Remove(found);
                        _vehiclesDirty = true;
                        Log($"RemovePersistentVehicleSafe: removed entry model 0x{pv.ModelHash:X} owner=0x{pv.OwnerModelHash:X}");
                    }
                }
                catch (Exception ex) { Log("RemovePersistentVehicleSafe inner: " + ex.ToString()); }
            }
        }
        catch (Exception ex) { Log("RemovePersistentVehicleSafe failed: " + ex.ToString()); }
    }
    private static void Log(string msg)
    {
        if (!ENABLE_LOG) return;
        try
        {
            EnsureDataFolderExists();
            var fp = Path.Combine(DataFolder, "persistent_log.txt");
            File.AppendAllText(fp, DateTime.Now.ToString("s") + "  " + msg + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* swallow */ }
    }

    private class PersistentWeapon { public uint Hash; public int Ammo; public int Tint; public List<uint> Components; }

    public class PersistentVehicle
    {
        public uint ModelHash;
        public Vector3 Position;
        public float Heading;
        public string Plate;
        public Vehicle RuntimeVehicle;
        public Blip MapBlip;
        public int StopCounter;
        public int OwnerModelHash;
        public bool AutoSpawnEnabled;
        public Dictionary<int, int> Mods;
        public bool TurboOn;
        public int PurchasePrice;
        public bool IsCollateralLocked;

        // Hao upgrade state
        public int? HaoUpgradeBranch;
        public bool HaoUpgradeApplied;

        // NEW: icon state
        public int? CustomIconId;   // icon đã lưu vĩnh viễn
        public int? PreviewIconId;  // icon preview tạm thời trong menu, không lưu file

        public float? CustomBlipScale;
        public float? PreviewBlipScale;

        public bool SuppressBlipWhileOccupied;

        public int? SavedLivery;
        public int? PrimaryColor;
        public int? SecondaryColor;
        public int? PearlColor;
        public int? WheelColor;
        public int? WindowTint;
        public int[] TyreSmokeColor;
        public List<int> ExtrasExist;
        public int[] NeonColor;
        public int? DashboardColor;

        public PersistentVehicle()
        {
            Mods = new Dictionary<int, int>();
            TyreSmokeColor = new int[3] { 0, 0, 0 };
            ExtrasExist = new List<int>();
            NeonColor = null;
            PurchasePrice = 0;
            IsCollateralLocked = false;

            HaoUpgradeBranch = null;
            HaoUpgradeApplied = false;

            CustomIconId = null;
            PreviewIconId = null;
            CustomBlipScale = null;
            PreviewBlipScale = null;

            SuppressBlipWhileOccupied = false;
        }
    }

    // optional helper container for explicit meta passing
    // --- Helper IO an toàn --- (thêm vào class)
    private static void EnsureDataFolderExists()
    {
        try { Directory.CreateDirectory(DataFolder); }
        catch { /* swallow - không ném ra */ }
    }

    private static bool HasTemporaryPreview(PersistentVehicle pv)
    {
        try
        {
            return pv != null && (pv.PreviewIconId.HasValue || pv.PreviewBlipScale.HasValue);
        }
        catch
        {
            return false;
        }
    }

    private static string GetBackupName(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(dir, $"{name}.backup.{ts}{ext}");
    }

    private static void RotateBackups(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);

            var all = Directory.EnumerateFiles(dir, $"{name}.backup.*{ext}")
                .OrderByDescending(f => f) // newest first (timestamp in name)
                .ToList();

            for (int i = MAX_BACKUPS; i < all.Count; i++)
            {
                try { File.Delete(all[i]); } catch { }
            }
        }
        catch { }
    }

    private static void WriteLinesAtomic(string path, string[] lines)
    {
        try
        {
            EnsureDataFolderExists();

            string tmp = path + ".tmp";

            // Write with FileStream to avoid partial writes and flush to disk
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var sw = new StreamWriter(fs, Encoding.UTF8))
            {
                foreach (var line in lines)
                {
                    sw.WriteLine(line);
                }
                sw.Flush();
                fs.Flush(true);
            }

            // Backup current file (if exists)
            if (File.Exists(path))
            {
                try
                {
                    var backup = GetBackupName(path);
                    File.Move(path, backup);
                    RotateBackups(path);
                }
                catch (Exception ex)
                {
                    // nếu backup fail thì vẫn tiếp tục attempt replace để không mất tmp
                    Log("WriteLinesAtomic backup failed: " + ex.Message);
                }
            }

            // Move tmp -> final (atomic rename on same volume)
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            Log("WriteLinesAtomic failed: " + ex.ToString());
            // cleanup tmp if exists
            try { var tmp = path + ".tmp"; if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static string[] ReadAllLinesSafeTryBackups(string path)
    {
        try
        {
            EnsureDataFolderExists();
            if (File.Exists(path))
            {
                return File.ReadAllLines(path, Encoding.UTF8);
            }
            // nếu file chính không tồn tại -> return empty
            return new string[0];
        }
        catch (Exception ex)
        {
            Log("ReadAllLinesSafeTryBackups failed for " + path + " : " + ex.ToString());
            // thử lấy backup gần nhất
            try
            {
                var dir = Path.GetDirectoryName(path);
                var name = Path.GetFileNameWithoutExtension(path);
                var ext = Path.GetExtension(path);
                var all = Directory.EnumerateFiles(dir, $"{name}.backup.*{ext}")
                    .OrderByDescending(f => f)
                    .ToList();
                if (all.Count > 0)
                {
                    try { return File.ReadAllLines(all[0], Encoding.UTF8); }
                    catch (Exception ex2) { Log("ReadAllLinesSafeTryBackups fallback failed: " + ex2.Message); }
                }
            }
            catch (Exception ex2) { Log("ReadAllLinesSafeTryBackups enumerate backups failed: " + ex2.Message); }

            return new string[0];
        }
    }

    public class VehicleMeta
    {
        public Dictionary<int, int> Mods;
        public bool TurboOn;
        public int PurchasePrice;

        public int? HaoUpgradeBranch;
        public bool HaoUpgradeApplied;

        // removed: SavedTopKmh, SavedPowerMul
    }
}