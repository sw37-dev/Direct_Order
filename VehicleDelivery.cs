using GTA;
using GTA.Native;
using GTA.Math;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using iFruitAddon2;
using LemonUI;
using LemonUI.Menus;

public class VehicleDelivery : Script
{
    private readonly ObjectPool _luiPool = new ObjectPool();

    private static string ContactName(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    private enum SpawnStage
    {
        NotStarted,
        RequestedModel,
        ModelLoaded,
        Spawned,
        Failed
    }

    // Behavior states for per-task state machine
    private enum BehaviorState
    {
        Driving,
        Avoiding,
        Recovering,
        FinalApproach
    }

    private class DeliveryTask
    {
        public uint ModelHash;
        public Vector3 TargetPos;            // normalized to nearest street when enqueued
        public int StartTimeMs;
        public int MaxTimeMs;                // <=0 means infinite (no timeout)
        public int Distance;                 // spawn distance
        public Vehicle SpawnedVehicle;
        public Ped DriverPed;
        public Action<Vehicle> OnDelivered;
        public bool IsActive;
        public bool HasSpawned;              // becomes true when vehicle + ped created

        // NEW: whether this model is considered "registered for delivery" (set when processing task)
        public bool IsRegistered = true;

        // non-blocking load state:
        public SpawnStage Stage = SpawnStage.NotStarted;
        public int ModelRequestStartMs = 0;
        public Model? RequestedModel = null;

        // stuck detection / recovery
        public float LastDistance = float.MaxValue;
        public int LastMoveTimeMs = 0;
        public int RecoveryAttempts = 0;
        public bool IsBraking = false;       // currently in forced brake state
        public int BrakeStartMs = 0;

        // Nudge guard - to avoid spamming nudges
        public int LastNudgeMs = 0;

        // --- NEW (insert into DeliveryTask) ---
        // LastDesiredSpeed used for smoothing cruise speed changes
        public float LastDesiredSpeed = 0f;

        // Steering smoothing / last detour waypoint (for debugging / stability)
        public Vector3 LastDetourWaypoint = Vector3.Zero;
        public int LastDetourTimeMs = 0;

        // spawn retry tracking
        public int SpawnAttempts = 0;
        public bool TeleportFallbackUsed = false;

        // FINALIZATION guard - prevents repeated leave/enter jitter
        public bool IsFinalizing = false;

        // NEW: behavior state & metadata
        public BehaviorState State = BehaviorState.Driving;
        public int StateStartMs = 0;
        public Vector3 AvoidTarget = Vector3.Zero;
        public float LastObstacleDistance = float.MaxValue;

        // --- PATCH 1: DeliveryTask AI metadata (INSERT into DeliveryTask) ---
        // --- Advanced AI metadata (non-invasive) ---
        // pursuit / interdiction metadata
        public Vector3 LastPredictedPlayerPos = Vector3.Zero;
        public int LastPursuitEvalMs = 0;
        public float PursuitConfidence = 0f;     // 0..1 heuristic for "will chase / intercept"
        public int LastRoadblockMs = 0;          // cooldown for spawning roadblocks per task
        public bool RoadblockActive = false;
        public int LastPITAttemptMs = 0;
        public int PITCooldownMs = 10000;        // default 10s cooldown between PIT attempts
        public bool HasAttemptedPIT = false;

        // flanking/cover metadata
        public bool IsFlanking = false;
        public Vector3 FlankTarget = Vector3.Zero;
        public int FlankStartMs = 0;

        // repath / nav heuristics
        public int LastRepathMs = 0;
        public int RepathCooldownMs = 800;       // small cooldown to prevent spamming repath
        public int MaxRepathAttempts = 6;
        public int RepathAttempts = 0;

        // behavior-tree small state
        public string BehaviorTag = "delivery";  // "delivery","interdict","pursuit","evade","blocker"
    }

    private readonly List<DeliveryTask> _tasks = new List<DeliveryTask>();
    private readonly string _iniPath = Path.Combine("scripts", "VehicleDelivery.ini");
    private readonly HashSet<uint> _allowedVehicles = new HashSet<uint>();

    // 1) Thêm 1 field dùng chung vào trong class VehicleDelivery
    private static readonly string[] _sharedColorNames = new string[] {
    "PURPLELIGHT", "YELLOWLIGHT", "ORANGELIGHT", "GREENLIGHT", "PLATINUM", "SAME_CREW", "YOGA",
    "FRANKLIN", "CONTROLLER_MICHAEL", "DEGEN_CYAN", "REDLIGHT", "SIMPLEBLIP_DEFAULT", "FREINDLY",
    "LOCATION", "PICKUP", "DARTS", "WAYPOINT", "TREVOR", "WAYPOINTLIGHT", "CONTROLLER_FRANKLIN",
    "VIDEO_EDITOR_AMBIENT", "GB", "G", "Ice White", "RADAR_DAMAGE", "RADAR_ARMOUR", "NET_PLAYER3",
    "GANG4", "PINKLIGHT", "PM_MITEM_HIGHLIGHT", "TENNIS", "NORTH_BLUE", "SOCIAL CLUB",
    "PLATFORM_GREEN", "B", "G5", "Blue", "Yellow", "Schafter Purple", "NET_PLAYER4", "NET_PLAYER6",
    "NET_PLAYER8", "NET_PLAYER10", "NET_PLAYER11", "NET_PLAYER12", "NET_PLAYER13", "NET_PLAYER18",
    "NET_PLAYER19", "NET_PLAYER21", "NET_PLAYER26", "NET_PLAYER27", "NET_PLAYER28", "NET_PLAYER29",
    "NET_PLAYER30", "NET_PLAYER31", "NET_PLAYER32", "Bronze", "Silver", "ENEMY", "INACTIVE_MISSION",
    "GWC and Golfing Society", "GOLF_P2", "GOLF_P3", "PURE_WHITE", "PM_WEAPONS_LOCKED",
    "VIDEO_EDITOR_AUDIO", "VIDEO_EDITOR_TEXT", "HB_YELLOW", "LOW_FLOW", "GREYLIGHT", "G1", "G2",
    "G3", "G4", "G6", "G7", "G14", "DEGEN_RED", "DEGEN_YELLOW", "DEGEN_GREEN", "DEGEN_MAGENTA",
    "VIDEO_EDITOR_VIDEO", "HB_BLUE", "VIDEO_EDITOR_SCORE", "VIDEO_EDITOR_TEXT_FADEOUT",
    "HEIST_BACKGROUND", "VIDEO_EDITOR_AMBIENT_FADEOUT", "LOW_FLOW_DARK", "ADVERSARY", "DEGEN_BLUE",
    "STUNT_1", "STUNT_2", "Gray", "Red", "GREENDARK", "RADAR_HEALTH", "SHOOTING_RANGE", "FLIGHT_SCHOOL",
    "WAYPOINTDARK", "Electric Blue", "Mint Green", "Lime Green"
};

    // 2) Thêm helper này vào trong class VehicleDelivery
    private void ApplyRandomTyreSmokeColor(Vehicle v)
    {
        if (!SafeExists(v)) return;

        try
        {
            if (_sharedColorNames == null || _sharedColorNames.Length == 0)
                return;

            string chosenName = _sharedColorNames[_rng.Next(_sharedColorNames.Length)];
            var rgb = NameToRgb(chosenName);

            Function.Call(Hash.SET_VEHICLE_TYRE_SMOKE_COLOR, v.Handle, rgb.Item1, rgb.Item2, rgb.Item3);
        }
        catch
        {
            try { Function.Call(Hash.SET_VEHICLE_TYRE_SMOKE_COLOR, v.Handle, 200, 50, 255); } catch { }
        }
    }

    // NEW: allow developer to hardcode some allowed model hashes in source (if you want)
    private readonly HashSet<uint> _hardcodedAllowed = new HashSet<uint>()
    {
0x20314B42,    // Apocalypse ZR380
0xB779A091,    // Adder
0xED552C74,   // Autarch
0xFE5F0722,   // Apocalypse Deathbike
0x2189D250,    // APC
0xD8A914D3,    // Banshee GTS
0xA1355F67,    // Blazer Aqua
0x25C5AF13,    // Banshee 900R
0xFD231729,    // Blazer Lifeguard
0x43779C54,    // BMX
0xF9300CC5,    // Bati 801
0xCADD5D2D,    // Bati 801RR
0x05283265,    // BF400
0x55365079,    // Brioso 300
0x09E478B3,    // Buffalo EVX
0xB1D95DA0,    // Cheetah
0xC972A155,    // Champion
0x52FF9437,    // Cyclone
0x00ABB0C0,    // Carbon RS
0x79C12D73,    // Coquette D10 Pursuit
0x5B531351,    // Deity
0x586765FB,    // Deluxo
0xD876DBE2,    // Desert Raid
0xF1B44F44,    // Diabolus
0x9C669788,    // Double-T
0xE882E5F6,    // Dubsta
0x5EE005DA,    // Deveste Eight
0xE5B3ACA1,    // Dominator GT
0x4EE74355,    // Emerus
0x8198AEDC,    // Entity XXR
0x6838FC1D,    // Entity MT
0x42D623C7,    // Envisage
0x25676EAF,    // FCR 1000
0x9DC66994,    // FIB (Buffalo)
0x9229E4EB,    // Faggio Sport
0x5502626C,    // FMJ
0xC3F57329,    // FMJ MK V
0x92EF6E04,    // 811
0x432EA949,    // FIB (Granger)
0xCE23D3BF,    // Fixter
0x93F09558,    // Future Shock Deathbike
0x843B73DE,    // Fieldmaster
0x5BEB3CE0,    // Future Shock Scarab
0x817AFAAD,    // Gauntlet
0xDCBC1C3B,    // Go Go Monkey Blista
0x2C2C2324,    // Gargoyle
0xF0C2A91F,    // Hakuchou Drag
0xB44F0582,    // Hot Rod Blazer
0x4B6C568A,    // Hakuchou
0xFE141DA6,    // Half-track
0xA9EC907B,    // Ignus
0xCA7C4AE9,    // Inductor
0x5649FF41,    // Itali GTO Stinger TT
0xBB78956A,    // Itali RSX
0x85E8E76B,    // Itali GTB
0xE33A477B,    // Itali GTB Custom
0x1B8165D3,    // Jubilee
0xD86A0247,    // Krieger
0xFF5968CD,    // LM87
0x1BF8D381,    // Lifeguard
0x26321E67,    // Lectro
0xCD93A7DB,    // Liberator
0xC8163646,    // Luiva
0xC7E55211,    // Locust
0x79DD18AE,    // Menacer
0x70241EEA,    // Niobe
0xDA288376,    // Nemesis
0xAE12C99C,    // Nightmare Deathbike
0x3DA47243,    // Nero
0x4131F378,    // Nero Custom
0xA0438767,    // Nightblade
0x34B82784,    // Oppressor
0x185E2FF3,    // Outlaw
0x9734F3EA,    // Penetrator
0x33B98FE2,    // Pariah
0x9DAE1398,    // Phantom Wedge
0xD80F4A44,    // Patriot Mil-Spec
0xF2AE3F81,    // Pipistrello
0x75599EA7,    // Pizza Boy
0xFDEFAEC3,    // Police Bike
0xE644E480,    // Panto
0x2C33B46E,    // Park Ranger
0xA46462F7,    // Police Rancher
0xAD5E30D7,    // Powersurge
0x71FA16EA,    // Police Cruiser
0x8B213907,    // R88
0x04F48FC4,    // Rebla GTS
0xCEB28249,    // Ramp Buggy (DUNE4)
0xED62BFA9,    // Ramp Buggy (DUNE5)
0xE00BADAB,    // Ratel
0x0DF381E5,    // Reaper
0x3AF76F4A,    // Rocket Voltic
0xCABD11E8,    // Ruffian
0x76D7C404,    // Reever
0x381E10BD,    // Ruiner 2000
0x58E316C7,    // Sanctus
0x2EF89E46,    // Sanchez
0xA960B13E,    // Sanchez (Sport)
0xE5BA6858,    // Street Blazer
0x28FC5B78,    // Suzume
0xD9F0503D,    // Scramjet
0x9C32EB57,    // Stanier LE Cruiser
0x3AF8C345,    // Sandking SWB
0x2C509634,    // Sovereign
0xF4E1AA15,    // Scorcher
0x50A6FB9C,    // Shinobi
0xE7D2A16E,    // Shotaro
0x34DBA661,    // Stromberg
0x42BC5E19,    // Slamvan Custom
0xEE6024BC,    // Sultan RS
0x1FD824AF,    // Space Docker
0x0D17099D,    // Speedo
0x11F58A5A,    // Stryder
0x6322B39A,    // T20
0x56C8A5EF,    // Toreador
0x1044926F,    // Tempesta
0x3E3D1F59,    // Thrax
0xAF0B8D48,    // Tigon
0xF8AB457B,    // Turismo Omaggio
0x7B406EFB,    // Tyrus
0x185484E1,    // Turismo R
0xE99011C2,    // Tyrant
0xA31CB573,    // Tornado Rat Rod
0xBC5DC07E,    // Taipan
0x4662BCBB,    // Technical Aqua
0x3D7C6410,    // Tezeract
0x6D6F8F43,    // Thrust
0xAA6F980A,    // TM-02 Khanjali
0x8A63C7B9,    // Unmarked Cruiser
0x7397224C,    // Vagner
0xB5EF4C33,    // Vigilante
0xF79A00F7,    // Vader
0x11CBC051,    // Verus
0xAF599F01,    // Vindicator
0xC4810400,    // Visione
0xAE2CC02A,    // Vivanite
0xB7D9F7F1,    // Weaponized Tampa
0x7E8F677F,    // X80 Proto
0x36B4A8A9,    // XA-21
0x95F6A2C9,    // X-treme
0x2D3BD401,    // Z-Type
0xAC5DF515,    // Zentorno
0xDE05FB87,    // Zombie Chopper
0x2714AA93,    // Zeno
0xD757D97D,    // Zorrusso
0x4C8DBA51,    // Zhaba
    };

    private readonly Random _rng = new Random();
    // --- PATCH 4a: field for AI module (INSERT near existing fields) ---
    private AdvancedAIModule _advancedAIModule = null;

    // --- CHỈNH: sử dụng driving-style "Ignore Lights" để tránh stop ở đèn giao thông ---
    private const int AGGRESSIVE_DRIVING_STYLE = 2883621;

    // --- NEW: smooth driving style fallback (keeps aggressive intent but smoother approach) ---
    public const int SMOOTH_AGGRESSIVE_DRIVING_STYLE = 786603; // (made public for AdvancedAIModule reference)

    // Tunables (made more aggressive/robust)
    private int MODEL_LOAD_TIMEOUT_MS = 6000; // increased
    private int PED_MODEL_LOAD_TIMEOUT_MS = 3000; // increased

    // More aggressive detection / recovery parameters
    private float ARRIVE_DISTANCE = 1.5f;         // meters from normalized street target (user requested)
    private int STUCK_TIMEOUT_MS = 3000;         // shorter: detect stuck sooner
    private int MAX_RECOVERY_ATTEMPTS = 12;        // allow more recovery attempts
    private int BRAKE_CONFIRM_TIMEOUT_MS = 5000;  // how long we wait for speed->0 after forcing brake
    private float BRAKE_SPEED_THRESHOLD = 0.5f;   // considered "stopped" if speed below this

    // FINAL approach tuning (new): when within this distance, ramp down speed smoothly
    private float FINAL_APPROACH_DISTANCE = 20.0f;    // start smooth approach inside this radius
    private float APPROACH_MIN_SPEED = 2.0f;         // m/s minimal approach cruise (very slow)
    private float APPROACH_MAX_SPEED = 24.0f;        // cap approach top speed
    private float APPROACH_SPEED_FACTOR = 0.9f;      // multiplier applied to distance->speed mapping

    // --- PATCH 2b: new AI tunables ---
    private bool _enableAdvancedAI = true;          // master switch
    private int ROADBLOCK_COOLDOWN_MS = 15000;      // how often roadblocks may spawn per task
    private int PIT_CHANCE_PERCENT = 18;            // chance% to attempt PIT when conditions met
    private int PIT_COOLDOWN_MS = 12000;            // global per-task PIT cooldown
    private float PURSUIT_PREDICT_SEC = 1.1f;       // seconds forward to extrapolate player's motion
    private float PIT_MIN_SPEED = 8.0f;             // m/s minimal speed for considering PIT
    private float PIT_MIN_REL_SPEED = 3.0f;         // relative speed advantage required
    private float ROADBLOCK_SPAWN_RADIUS = 70f;     // spawn roadblock this far ahead of player

    // Nudge parameters (slightly stronger)
    private float NUDGE_FORCE = 1.2f;             // multiplier for velocity nudge (m/s)
    private int NUDGE_DURATION_MS = 300;          // how long nudge velocity is applied
    private int NUDGE_COOLDOWN_MS = 900;         // cooldown between nudges per task (reduced from very long)
    private float OBSTACLE_CHECK_DISTANCE = 8.0f; // raycast forward to detect obstacles
    private float SIDE_NUDGE_OFFSET = 6.0f;       // meters to offset intermediate waypoint when dodging

    // Spawn retry parameters
    private int SPAWN_ATTEMPTS_MAX = 6;           // how many spawn attempts before giving up (per task)
    private int SPAWN_RADIUS_MIN = 30;            // min radius for spawn circle
    private int SPAWN_RADIUS_MAX = 150;           // max radius for spawn circle

    // INI-configurable toggles (defaults)
    private bool _enableHighSmooth = true;              // NEW: enable high-smooth approach by default
    private bool _debugMode = false;
    private int _spawnRetryCount = 4;                  // default spawn retries
    private bool _teleportOnFinalFail = false;         // final fallback: teleport vehicle to final stop (disabled by default)

    // --- PATCH: spawn-mode toggle fields (INSERT near other fields / tunables) ---
    private bool _forceSpawnInFrontMode = false; // false = mặc định dùng chế độ giao xe (NPC). true = mọi xe spawn trước mặt player.

    // --- Pegasus Concierge phone/menu ---
    private CustomiFruit _pegasusPhoneInstance = null;
    private bool _pegasusContactAdded = false;
    private NativeMenu _pegasusConciergeMenu = null;
    private NativeCheckboxItem _pegasusCurrentLocationItem = null;
    private NativeCheckboxItem _pegasusExpressItem = null;
    private bool _pegasusMenuSyncLock = false;

    // Optional: expose default via INI (được xử lý trong LoadSettings patch)
    // singleton guard
    private static VehicleDelivery _singleton = null;
    private bool _isPrimaryInstance = false;

    public VehicleDelivery()
    {
        if (_singleton != null)
        {
            _isPrimaryInstance = false;
            return;
        }

        _singleton = this;
        _isPrimaryInstance = true;

        Interval = 250; // tighter interval to improve approach responsiveness
        Tick += OnTick;
        LoadAllowedVehicles();
        LoadSettings();
        // --- PATCH 4b: instantiate AI module (INSERT into constructor after LoadSettings()) ---
        if (_enableAdvancedAI) _advancedAIModule = new AdvancedAIModule(this);
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_iniPath))
            {
                // Tạo file INI mặc định với các khóa bổ sung
                var defaultLines = new List<string> {
                "[Vehicles]",
                "# Put allowed model hashes here (hex), one per line, e.g:",
                "# 0x20314B42   // Apocalypse ZR380",
                "0x20314B42",
                "",
                "[Settings]",
                "SmoothMode = 1",
                "DebugMode = 0",
                "ArrivalDistance = 1.5",
                "ModelLoadTimeoutMs = 6000",
                "PedModelLoadTimeoutMs = 3000",
                "SpawnRetryCount = 4",
                "TeleportOnFinalFail = 0",
                "ForceSpawnInFront = 0", // Khóa mới bổ sung vào file mặc định
                "",
                "# --- Advanced AI Settings ---",
                "EnableAdvancedAI = 1",
                "RoadblockCooldownMs = 15000",
                "PITChancePercent = 18",
                "PITCooldownMs = 12000",
                "PursuitPredictSec = 1.1",
                "RoadblockSpawnRadius = 70.0"
            };
                try { File.WriteAllLines(_iniPath, defaultLines.ToArray()); } catch { }
            }

            var lines = File.ReadAllLines(_iniPath);
            foreach (var raw in lines)
            {
                var ln = raw.Trim();
                if (string.IsNullOrEmpty(ln) || ln.StartsWith("#") || (ln.StartsWith("[") && ln.EndsWith("]")))
                    continue;

                var parts = ln.Split(new char[] { '=' }, 2);
                if (parts.Length == 2)
                {
                    var k = parts[0].Trim();
                    var v = parts[1].Trim();

                    // --- Parsing chính ---
                    if (k.Equals("SmoothMode", StringComparison.InvariantCultureIgnoreCase))
                    {
                        _enableHighSmooth = (v == "1" || v.Equals("true", StringComparison.InvariantCultureIgnoreCase));
                    }
                    else if (k.Equals("DebugMode", StringComparison.InvariantCultureIgnoreCase))
                    {
                        _debugMode = (v == "1" || v.Equals("true", StringComparison.InvariantCultureIgnoreCase));
                    }
                    else if (k.Equals("ArrivalDistance", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                            ARRIVE_DISTANCE = Math.Max(0.5f, Math.Min(6.0f, parsed));
                    }
                    else if (k.Equals("ModelLoadTimeoutMs", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (int.TryParse(v, out int parsed)) MODEL_LOAD_TIMEOUT_MS = Math.Max(1000, parsed);
                    }
                    else if (k.Equals("PedModelLoadTimeoutMs", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (int.TryParse(v, out int parsed)) PED_MODEL_LOAD_TIMEOUT_MS = Math.Max(500, parsed);
                    }
                    else if (k.Equals("SpawnRetryCount", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (int.TryParse(v, out int parsed)) _spawnRetryCount = Math.Max(0, Math.Min(10, parsed));
                    }
                    else if (k.Equals("TeleportOnFinalFail", StringComparison.InvariantCultureIgnoreCase))
                    {
                        _teleportOnFinalFail = (v == "1" || v.Equals("true", StringComparison.InvariantCultureIgnoreCase));
                    }
                    // --- PATCH: parse ForceSpawnInFront ---
                    else if (k.Equals("ForceSpawnInFront", StringComparison.InvariantCultureIgnoreCase))
                    {
                        _forceSpawnInFrontMode = (v == "1" || v.Equals("true", StringComparison.InvariantCultureIgnoreCase));
                    }

                    // --- PATCH 2: Advanced AI Keys ---
                    else if (k.Equals("EnableAdvancedAI", StringComparison.InvariantCultureIgnoreCase))
                    {
                        _enableAdvancedAI = (v == "1" || v.Equals("true", StringComparison.InvariantCultureIgnoreCase));
                    }
                    else if (k.Equals("RoadblockCooldownMs", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (int.TryParse(v, out int parsed)) ROADBLOCK_COOLDOWN_MS = Math.Max(2000, parsed);
                    }
                    else if (k.Equals("PITChancePercent", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (int.TryParse(v, out int parsed)) PIT_CHANCE_PERCENT = Math.Max(0, Math.Min(100, parsed));
                    }
                    else if (k.Equals("PITCooldownMs", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (int.TryParse(v, out int parsed)) PIT_COOLDOWN_MS = Math.Max(2000, parsed);
                    }
                    else if (k.Equals("PursuitPredictSec", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                            PURSUIT_PREDICT_SEC = Math.Max(0.1f, Math.Min(5.0f, parsed));
                    }
                    else if (k.Equals("RoadblockSpawnRadius", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                            ROADBLOCK_SPAWN_RADIUS = Math.Max(20f, Math.Min(200f, parsed));
                    }
                }
            }
        }
        catch { /* Ngậm lỗi để tránh crash game khi file INI hỏng */ }
    }

    private void LoadAllowedVehicles()
    {
        try
        {
            _allowedVehicles.Clear();

            // Luôn nạp mặc định từ code trước
            foreach (var h in _hardcodedAllowed)
                _allowedVehicles.Add(h);

            if (!File.Exists(_iniPath))
            {
                var defaultLines = new[] {
                "[VehiclesCustom]",
                "# Add your custom vehicle hashes here, one per line:",
                "# 0x20314B42   // Example"
            };
                try { File.WriteAllLines(_iniPath, defaultLines); } catch { }
                return;
            }

            var lines = File.ReadAllLines(_iniPath);
            bool inCustomSection = false;

            foreach (var raw in lines)
            {
                var ln = raw.Trim();
                if (string.IsNullOrEmpty(ln) || ln.StartsWith("#"))
                    continue;

                if (ln.StartsWith("[") && ln.EndsWith("]"))
                {
                    inCustomSection = ln.Equals("[VehiclesCustom]", StringComparison.InvariantCultureIgnoreCase);
                    continue;
                }

                if (!inCustomSection)
                    continue;

                uint val = 0;
                try
                {
                    var field = ln.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    if (field.StartsWith("0x", StringComparison.InvariantCultureIgnoreCase))
                        uint.TryParse(field.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out val);
                    else
                        uint.TryParse(field, out val);
                }
                catch { val = 0; }

                if (val != 0)
                    _allowedVehicles.Add(val);
            }
        }
        catch
        {
            // swallow
        }
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (!_isPrimaryInstance) return;

        EnsurePegasusConciergeContactRegistered();

        try
        {
            _luiPool?.Process();
        }
        catch { }

        if (_tasks.Count == 0) return;

        var copy = _tasks.ToList();
        int now = Game.GameTime;
        foreach (var t in copy)
        {
            try
            {
                if (!t.IsActive) { lock (_tasks) { _tasks.Remove(t); } continue; }

                int elapsed = now - t.StartTimeMs;

                if (!t.HasSpawned)
                {
                    HandleTaskSpawnState(t, now);
                    continue;
                }

                // --- PATCH 4c: call advanced AI per task (INSERT right before obstacle detection block) ---
                // call advanced AI module (non-destructive)
                if (_enableAdvancedAI && _advancedAIModule != null)
                {
                    try { _advancedAIModule.UpdateForTask(t, now); } catch { }
                }

                // ---------- runtime safety checks ----------
                if (t.SpawnedVehicle == null || !SafeExists(t.SpawnedVehicle))
                {
                    // if the vehicle unexpectedly despawned or was deleted early, attempt to respawn (within retry limit)
                    if (t.SpawnAttempts < _spawnRetryCount)
                    {
                        t.SpawnAttempts++;
                        try { if (SafeExists(t.SpawnedVehicle)) t.SpawnedVehicle.Delete(); } catch { }
                        try { if (SafeExists(t.DriverPed)) t.DriverPed.Delete(); } catch { }

                        // reset spawn state and let HandleTaskSpawnState restart attempt
                        t.HasSpawned = false;
                        t.Stage = SpawnStage.NotStarted;
                        t.RequestedModel = null;
                        t.ModelRequestStartMs = 0;
                        t.LastMoveTimeMs = now;
                        continue;
                    }
                    else
                    {
                        CleanupTask(t, false);
                        continue;
                    }
                }

                // compute distance safely
                float dist = float.MaxValue;
                try
                {
                    if (SafeExists(t.SpawnedVehicle))
                        dist = t.SpawnedVehicle.Position.DistanceTo(t.TargetPos);
                    else
                        dist = float.MaxValue;
                }
                catch { dist = float.MaxValue; }

                // if vehicle somehow teleported far away (unexpected huge distance), treat as failure and respawn
                if (dist > 2000f)
                {
                    if (t.SpawnAttempts < _spawnRetryCount)
                    {
                        t.SpawnAttempts++;
                        try { if (t.DriverPed != null && SafeExists(t.DriverPed)) t.DriverPed.Delete(); } catch { }
                        try { if (t.SpawnedVehicle != null && SafeExists(t.SpawnedVehicle)) t.SpawnedVehicle.Delete(); } catch { }

                        t.HasSpawned = false;
                        t.Stage = SpawnStage.NotStarted;
                        t.RequestedModel = null;
                        t.ModelRequestStartMs = now;
                        continue;
                    }
                }

                // update "last movement" tracking
                if (dist + 0.5f < t.LastDistance) // moved significantly closer
                {
                    t.LastDistance = dist;
                    t.LastMoveTimeMs = now;
                }

                // ---------- Precompute speed & near flag ----------
                float speed = 0f;
                try { if (SafeExists(t.SpawnedVehicle)) speed = Math.Abs(t.SpawnedVehicle.Speed); } catch { speed = 0f; }

                bool near = dist <= ARRIVE_DISTANCE;

                // compute timeout - if MaxTimeMs <= 0 then infinite (no timeout)
                bool timedOut = (t.MaxTimeMs > 0) && (elapsed >= t.MaxTimeMs);

                // If the task is already finalizing (we started leave/flee sequence) avoid re-triggering any movement/drive logic
                if (t.IsFinalizing)
                {
                    // minimal monitoring: if driver ped and vehicle both gone, cleanup
                    if ((t.DriverPed == null || !SafeExists(t.DriverPed)) && (t.SpawnedVehicle == null || !SafeExists(t.SpawnedVehicle)))
                    {
                        CleanupTask(t, true);
                        continue;
                    }
                    else
                    {
                        // otherwise skip most behavior until cleanup deletes ped/vehicle
                        continue;
                    }
                }

                // ---------- NEW: Obstacle detection + avoidance state machine ----------
                if (SafeExists(t.SpawnedVehicle))
                {
                    float closestHit;
                    Vector3 hitPos;
                    Entity hitEntity;
                    bool obstacle = GetObstacleInfo(t.SpawnedVehicle, OBSTACLE_CHECK_DISTANCE, out closestHit, out hitPos, out hitEntity);
                    t.LastObstacleDistance = obstacle ? closestHit : float.MaxValue;

                    // If we detect an obstacle within a reasonable distance, plan avoidance
                    if (obstacle && closestHit <= OBSTACLE_CHECK_DISTANCE)
                    {
                        // enter Avoiding state if not already or if closer than previous
                        if (t.State != BehaviorState.Avoiding || (t.State == BehaviorState.Avoiding && closestHit < t.LastObstacleDistance * 0.98f))
                        {
                            t.State = BehaviorState.Avoiding;
                            t.StateStartMs = now;
                            // try to compute a detour waypoint
                            Vector3 detour = FindDetourWaypoint(t, forwardLook: Math.Max(6.0f, closestHit + 2.0f), initialSide: SIDE_NUDGE_OFFSET, sideSteps: 6, forwardSteps: 4);
                            if (detour != Vector3.Zero)
                            {
                                t.AvoidTarget = detour;
                                t.LastDetourWaypoint = detour;
                                t.LastDetourTimeMs = now;

                                // desired speed depends on obstacle distance; closer => slower
                                float desired = Math.Max(APPROACH_MIN_SPEED, Math.Min(28.0f, (closestHit / OBSTACLE_CHECK_DISTANCE) * 20.0f + 8.0f));
                                // gently ramp down to desired to avoid instant stops
                                SmoothSetCruiseSpeed(t, desired, 0.12f);

                                // issue a controlled drive to detour point
                                try
                                {
                                    Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, t.DriverPed.Handle);
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, detour.X, detour.Y, detour.Z, desired, 0, (int)t.ModelHash, SMOOTH_AGGRESSIVE_DRIVING_STYLE, 2.0f);
                                    Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, t.DriverPed.Handle, SMOOTH_AGGRESSIVE_DRIVING_STYLE);
                                }
                                catch
                                {
                                    try { t.DriverPed.Task.CruiseWithVehicle(t.SpawnedVehicle, desired); } catch { }
                                }

                                // small lateral nudge to help physics free up
                                if (now - t.LastNudgeMs > NUDGE_COOLDOWN_MS)
                                {
                                    // nudge toward side of detour (left/right)
                                    Vector3 lateral = Normalize2D(detour - t.SpawnedVehicle.Position);
                                    lateral = new Vector3(-lateral.Y, lateral.X, 0f); // perp
                                    NudgeVehicle(t, lateral, Math.Min(NUDGE_FORCE * 1.0f, 1.8f), 160);
                                    t.LastNudgeMs = now;
                                }
                            }
                            else
                            {
                                // If no detour found: attempt lane change / edge slip to left/right
                                Vector3 laneCandidate = TryFindLateralClearSpace(t, closestHit, SIDE_NUDGE_OFFSET);
                                if (laneCandidate != Vector3.Zero)
                                {
                                    t.AvoidTarget = laneCandidate;
                                    t.LastDetourWaypoint = laneCandidate;
                                    t.LastDetourTimeMs = now;

                                    float desired = Math.Max(APPROACH_MIN_SPEED, Math.Min(30.0f, (closestHit / OBSTACLE_CHECK_DISTANCE) * 22.0f + 6.0f));
                                    SmoothSetCruiseSpeed(t, desired, 0.12f);

                                    try
                                    {
                                        Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, t.DriverPed.Handle);
                                        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, laneCandidate.X, laneCandidate.Y, laneCandidate.Z, desired, 0, (int)t.ModelHash, SMOOTH_AGGRESSIVE_DRIVING_STYLE, 2.0f);
                                        Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, t.DriverPed.Handle, SMOOTH_AGGRESSIVE_DRIVING_STYLE);
                                    }
                                    catch
                                    {
                                        try { t.DriverPed.Task.CruiseWithVehicle(t.SpawnedVehicle, desired); } catch { }
                                    }

                                    if (now - t.LastNudgeMs > NUDGE_COOLDOWN_MS)
                                    {
                                        NudgeVehicle(t, t.SpawnedVehicle.ForwardVector + Normalize2D(laneCandidate - t.SpawnedVehicle.Position) * 0.6f, NUDGE_FORCE * 0.9f, 160);
                                        t.LastNudgeMs = now;
                                    }
                                }
                                else
                                {
                                    // Last resort: slow down smoothly and hope other traffic moves; try small reverse nudge then attempt forward
                                    SmoothSetCruiseSpeed(t, Math.Max(3.0f, speed * 0.45f), 0.08f);
                                    if (now - t.LastNudgeMs > NUDGE_COOLDOWN_MS)
                                    {
                                        NudgeVehicle(t, t.SpawnedVehicle.ForwardVector * -1f, NUDGE_FORCE * 0.7f, 160);
                                        t.LastNudgeMs = now;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // no obstacle right now: if we were avoiding and passed avoid target, resume Driving
                        if (t.State == BehaviorState.Avoiding)
                        {
                            // if we have an avoid target and are near it, switch back to Driving
                            if (t.AvoidTarget != Vector3.Zero && t.SpawnedVehicle.Position.DistanceTo(t.AvoidTarget) < 4.5f)
                            {
                                t.State = BehaviorState.Driving;
                                t.StateStartMs = now;
                                t.AvoidTarget = Vector3.Zero;
                                // reissue drive-to-final with smooth approach
                                try
                                {
                                    float ds = Math.Min(35.0f, Math.Max(16.0f, dist * 0.6f + 10.0f));
                                    Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, t.DriverPed.Handle);
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, t.TargetPos.X, t.TargetPos.Y, t.TargetPos.Z, ds, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 3.0f);
                                    SmoothSetCruiseSpeed(t, ds, 0.18f);
                                    Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, t.DriverPed.Handle, AGGRESSIVE_DRIVING_STYLE);
                                }
                                catch { }
                            }
                            else
                            {
                                // if not yet near avoid target, keep current drive task (it was set when entering avoiding)
                            }
                        }
                    }
                }

                // ---------- NEW: Controlled final approach ----------
                // If we're within FINAL_APPROACH_DISTANCE, perform smooth ramp-down of cruise speed toward ARRIVE_DISTANCE.
                if (_enableHighSmooth && dist <= FINAL_APPROACH_DISTANCE && !near)
                {
                    // if in Avoiding/Recovering, let those behaviors handle speed; else treat as FinalApproach state
                    if (t.State != BehaviorState.Avoiding && t.State != BehaviorState.Recovering)
                    {
                        t.State = BehaviorState.FinalApproach;
                        t.StateStartMs = now;
                    }

                    if (t.State == BehaviorState.FinalApproach)
                    {
                        try
                        {
                            if (t.DriverPed != null && SafeExists(t.DriverPed) && SafeExists(t.SpawnedVehicle))
                            {
                                // Desired speed scales with distance: closer => slower
                                float desiredSpeed = (dist / FINAL_APPROACH_DISTANCE) * APPROACH_MAX_SPEED * APPROACH_SPEED_FACTOR;
                                desiredSpeed = Math.Max(APPROACH_MIN_SPEED, Math.Min(APPROACH_MAX_SPEED, desiredSpeed));

                                // gently change cruise speed
                                SmoothSetCruiseSpeed(t, desiredSpeed, 0.10f);

                                // Use a smoother driving style in the final approach while still preserving aggressive intent
                                try { Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, t.DriverPed.Handle, SMOOTH_AGGRESSIVE_DRIVING_STYLE); } catch { }

                                // Reissue a targeted drive-to a computed final stop position (so AI aims to stop exactly ARRIVE_DISTANCE away)
                                Vector3 approachDir = Normalize2D(t.TargetPos - t.SpawnedVehicle.Position);
                                if (approachDir.Length() < 0.001f) approachDir = t.SpawnedVehicle.ForwardVector;
                                Vector3 finalStop = t.TargetPos - approachDir * ARRIVE_DISTANCE;
                                // Snap final stop to street if possible to help pathing
                                var fsOnStreet = World.GetNextPositionOnStreet(finalStop);
                                if (fsOnStreet != Vector3.Zero) finalStop = fsOnStreet;

                                // Reissue longrange drive to finalStop with controlled desiredSpeed to avoid sudden brakes
                                Function.Call(Hash.CLEAR_PED_TASKS, t.DriverPed.Handle);
                                try
                                {
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, finalStop.X, finalStop.Y, finalStop.Z, desiredSpeed, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 2.0f);
                                }
                                catch
                                {
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD, t.DriverPed.Handle, t.SpawnedVehicle.Handle, finalStop.X, finalStop.Y, finalStop.Z, desiredSpeed, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 2.0f);
                                }
                                // small velocity nudge in forward direction to keep physics engaged if near tiny obstacles
                                if (now - t.LastNudgeMs > NUDGE_COOLDOWN_MS)
                                {
                                    NudgeVehicle(t, t.SpawnedVehicle.ForwardVector, NUDGE_FORCE * 0.4f, 120);
                                    t.LastNudgeMs = now;
                                }
                            }
                        }
                        catch { }
                        // then continue main loop (do not trigger recovery while we are actively approaching)
                        continue; // skip later recovery logic while final approach is being actively handled this tick
                    }
                }

                // ---------- if near target, start/confirm braking ----------
                if (near || timedOut)
                {
                    // If not already braking, request controlled brake action and start timer
                    if (!t.IsBraking)
                    {
                        t.IsBraking = true;
                        t.BrakeStartMs = now;
                        try
                        {
                            if (t.DriverPed != null && SafeExists(t.DriverPed) && SafeExists(t.SpawnedVehicle))
                            {
                                // Instead of hard immediate full brake, reduce cruise speed to a low value and request a short brake tap.
                                SmoothSetCruiseSpeed(t, 1.2f, 0.08f);
                                try { Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, t.DriverPed.Handle, t.SpawnedVehicle.Handle, 27, 800); } catch { }
                            }
                        }
                        catch { }
                    }

                    // Wait until speed is near zero OR brake confirm timeout expired
                    if (t.IsBraking)
                    {
                        bool stopped = speed <= BRAKE_SPEED_THRESHOLD;
                        bool brakeTimeout = (now - t.BrakeStartMs) >= BRAKE_CONFIRM_TIMEOUT_MS;

                        if (stopped || brakeTimeout)
                        {
                            // Prevent re-entry into finalization sequence
                            if (t.IsFinalizing)
                            {
                                // already in finalization; wait for cleanup
                                continue;
                            }

                            t.IsFinalizing = true; // <-- guard to prevent jitter loops

                            // finalize delivery: force ped leave, ped flee, visual finish, register persistent, notify
                            try
                            {
                                if (t.DriverPed != null && SafeExists(t.DriverPed) && SafeExists(t.SpawnedVehicle))
                                {
                                    // ensure ped leaves vehicle
                                    Function.Call(Hash.TASK_LEAVE_VEHICLE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, 0);
                                }
                            }
                            catch
                            {
                                try
                                {
                                    if (t.DriverPed != null && SafeExists(t.DriverPed) && SafeExists(t.SpawnedVehicle))
                                        t.DriverPed.Task.LeaveVehicle(t.SpawnedVehicle, LeaveVehicleFlags.None);
                                }
                                catch { }
                            }

                            // after small delay make ped flee and keep task
                            DelayThen(() =>
                            {
                                try
                                {
                                    if (t.DriverPed != null && SafeExists(t.DriverPed))
                                    {
                                        Vector3 fleeFrom = SafeExists(t.SpawnedVehicle) ? t.SpawnedVehicle.Position : t.TargetPos;
                                        Function.Call(Hash.SET_PED_KEEP_TASK, t.DriverPed.Handle, 1);
                                        Function.Call(Hash.TASK_SMART_FLEE_COORD, t.DriverPed.Handle, fleeFrom.X, fleeFrom.Y, fleeFrom.Z, 50.0f, 5000, false, false);
                                    }
                                }
                                catch { }
                            }, 800);

                            // finish touches
                            try { ApplyFinalVehicleFinish(t.SpawnedVehicle); } catch { }
                            try { ApplyOnlineLikeUpgrades(t.SpawnedVehicle); } catch { }
                            try { PersistentManager.RegisterVehicle(t.SpawnedVehicle); } catch { }

                            // final notification only when brake confirmed and near target
                            if (near)
                            {
                                ShowFeedMessage(
                                    Tr("VehicleDelivery_FeedSender", "Legendary Motorsport"),
                                    Tr("VehicleDelivery_FeedDeliveredSubject", "Giao xe"),
                                    Tr("VehicleDelivery_FeedDeliveredBody", "Xe của bạn đã được giao đến và đã được chuẩn bị kỹ càng. Cảm ơn quý khách đã mua hàng!")
                                );
                            }
                            else
                            {
                                ShowFeedMessage(
                                    Tr("VehicleDelivery_FeedSender", "Legendary Motorsport"),
                                    Tr("VehicleDelivery_FeedFailedSubject", "Giao xe"),
                                    Tr("VehicleDelivery_FeedFailedBody", "Tôi không thể đến đó được. Hãy kiểm tra vị trí trên bản đồ và bạn đến lấy nhé?.")
                                );
                            }

                            try { t.OnDelivered?.Invoke(t.SpawnedVehicle); } catch { }

                            CleanupTask(t, true);
                            continue;
                        }
                        else
                        {
                            // still braking — wait next tick
                            continue;
                        }
                    }
                }

                // ---------- CHỈNH: nếu xe dừng mà chưa gần đích => ưu tiên tiếp tục tiếp cận chính xác
                if (speed <= BRAKE_SPEED_THRESHOLD && !near)
                {
                    // If within final approach zone, reissue very low-speed drive to exact final stop position
                    if (dist <= FINAL_APPROACH_DISTANCE && t.RecoveryAttempts < MAX_RECOVERY_ATTEMPTS)
                    {
                        try
                        {
                            if (t.DriverPed != null && SafeExists(t.DriverPed) && SafeExists(t.SpawnedVehicle))
                            {
                                t.RecoveryAttempts++; // small penalty but not considered full failure

                                Vector3 approachDir = Normalize2D(t.TargetPos - t.SpawnedVehicle.Position);
                                if (approachDir.Length() < 0.001f) approachDir = t.SpawnedVehicle.ForwardVector;
                                Vector3 finalStop = t.TargetPos - approachDir * ARRIVE_DISTANCE;
                                var fsOnStreet = World.GetNextPositionOnStreet(finalStop);
                                if (fsOnStreet != Vector3.Zero) finalStop = fsOnStreet;

                                float desiredSpeed = Math.Max(APPROACH_MIN_SPEED, Math.Min(APPROACH_MAX_SPEED, dist * 0.4f));
                                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, t.DriverPed.Handle);
                                try
                                {
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, finalStop.X, finalStop.Y, finalStop.Z, desiredSpeed, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 2.0f);
                                }
                                catch
                                {
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD, t.DriverPed.Handle, t.SpawnedVehicle.Handle, finalStop.X, finalStop.Y, finalStop.Z, desiredSpeed, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 2.0f);
                                }
                                SmoothSetCruiseSpeed(t, desiredSpeed, 0.12f);
                                try { Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, t.DriverPed.Handle, SMOOTH_AGGRESSIVE_DRIVING_STYLE); } catch { }

                                // gentle nudge if necessary
                                if (now - t.LastNudgeMs > NUDGE_COOLDOWN_MS)
                                {
                                    NudgeVehicle(t, t.SpawnedVehicle.ForwardVector, NUDGE_FORCE * 0.6f, 160);
                                    t.LastNudgeMs = now;
                                }
                            }
                        }
                        catch { }
                        t.LastMoveTimeMs = now;
                        continue;
                    }

                    // fallback: original recovery logic (force longrange drive)
                    if (t.RecoveryAttempts < MAX_RECOVERY_ATTEMPTS)
                    {
                        t.RecoveryAttempts++;
                        t.LastNudgeMs = now;

                        try
                        {
                            if (t.DriverPed != null && SafeExists(t.DriverPed) && SafeExists(t.SpawnedVehicle))
                            {
                                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, t.DriverPed.Handle);
                                try
                                {
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, t.TargetPos.X, t.TargetPos.Y, t.TargetPos.Z, 55.0f, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 2.0f);
                                }
                                catch
                                {
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD, t.DriverPed.Handle, t.SpawnedVehicle.Handle, t.TargetPos.X, t.TargetPos.Y, t.TargetPos.Z, 55.0f, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 2.0f);
                                }
                                SmoothSetCruiseSpeed(t, 55.0f, 0.16f);

                                NudgeVehicle(t, t.SpawnedVehicle.ForwardVector, NUDGE_FORCE * 1.4f, 220);
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        // exhausted attempts: final urgent drive
                        try
                        {
                            if (t.DriverPed != null && SafeExists(t.DriverPed) && SafeExists(t.SpawnedVehicle))
                            {
                                Function.Call(Hash.CLEAR_PED_TASKS, t.DriverPed.Handle);
                                try
                                {
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, t.TargetPos.X, t.TargetPos.Y, t.TargetPos.Z, 80.0f, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 1.0f);
                                }
                                catch
                                {
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD, t.DriverPed.Handle, t.SpawnedVehicle.Handle, t.TargetPos.X, t.TargetPos.Y, t.TargetPos.Z, 80.0f, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 1.0f);
                                }
                                SmoothSetCruiseSpeed(t, 80.0f, 0.18f);

                                // if everything else failed and teleport fallback is enabled, teleport vehicle to final stop (disabled by default)
                                if (_teleportOnFinalFail && !t.TeleportFallbackUsed)
                                {
                                    try
                                    {
                                        Vector3 approachDir = Normalize2D(t.TargetPos - t.SpawnedVehicle.Position);
                                        if (approachDir.Length() < 0.001f) approachDir = t.SpawnedVehicle.ForwardVector;
                                        Vector3 teleportPos = t.TargetPos - approachDir * ARRIVE_DISTANCE * 0.7f;
                                        var onStreet = World.GetNextPositionOnStreet(teleportPos);
                                        if (onStreet != Vector3.Zero) teleportPos = onStreet;
                                        SafeCall(() => { Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, t.SpawnedVehicle.Handle, teleportPos.X, teleportPos.Y, teleportPos.Z, false, false, true); });
                                        t.TeleportFallbackUsed = true;
                                        t.LastMoveTimeMs = now;
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                        t.LastMoveTimeMs = now;
                    }

                    continue;
                }

                // ---------- not arrived: check for stuck condition and recovery (original logic kept, enhanced) ----------
                if (now - t.LastMoveTimeMs > STUCK_TIMEOUT_MS)
                {
                    if (t.RecoveryAttempts < MAX_RECOVERY_ATTEMPTS)
                    {
                        t.RecoveryAttempts++;

                        float closestHit;
                        Vector3 hitPos;
                        Entity hitEntity;
                        bool obstacle = GetObstacleInfo(t.SpawnedVehicle, OBSTACLE_CHECK_DISTANCE, out closestHit, out hitPos, out hitEntity);

                        if (obstacle)
                        {
                            try
                            {
                                // First, try to find a multi-step detour waypoint to steer around obstacle
                                Vector3 detour = FindDetourWaypoint(t, forwardLook: 8.0f, initialSide: SIDE_NUDGE_OFFSET, sideSteps: 6, forwardSteps: 3);

                                if (detour != Vector3.Zero)
                                {
                                    // Issue a smooth, controlled drive-to the detour point using smoother driving style.
                                    float newSpeed = Math.Max(12.0f, Math.Min(36.0f, 18.0f + t.RecoveryAttempts * 2.0f)); // moderated speed for controlled avoidance
                                    if (t.DriverPed != null && SafeExists(t.DriverPed) && SafeExists(t.SpawnedVehicle))
                                    {
                                        t.State = BehaviorState.Avoiding;
                                        t.StateStartMs = now;
                                        t.AvoidTarget = detour;

                                        Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, t.DriverPed.Handle);
                                        try
                                        {
                                            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, detour.X, detour.Y, detour.Z, newSpeed, 0, (int)t.ModelHash, SMOOTH_AGGRESSIVE_DRIVING_STYLE, 2.5f);
                                        }
                                        catch
                                        {
                                            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD, t.DriverPed.Handle, t.SpawnedVehicle.Handle, detour.X, detour.Y, detour.Z, newSpeed, 0, (int)t.ModelHash, SMOOTH_AGGRESSIVE_DRIVING_STYLE, 2.5f);
                                        }

                                        SmoothSetCruiseSpeed(t, newSpeed, 0.18f);
                                        try { Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, t.DriverPed.Handle, SMOOTH_AGGRESSIVE_DRIVING_STYLE); } catch { }
                                    }

                                    // small forward nudge to keep physics engaged if necessary but smaller than previous
                                    if (Game.GameTime - t.LastNudgeMs > NUDGE_COOLDOWN_MS)
                                    {
                                        NudgeVehicle(t, t.SpawnedVehicle.ForwardVector, NUDGE_FORCE * 0.8f, 160);
                                        t.LastNudgeMs = Game.GameTime;
                                    }
                                }
                                else
                                {
                                    // If no detour waypoint found, fallback to earlier behavior but improved: use CLEAR_PED_TASKS_IMMEDIATELY and smoothing
                                    t.LastNudgeMs = Game.GameTime;
                                    float newSpeed = 28.0f + t.RecoveryAttempts * 4.0f;
                                    if (t.DriverPed != null && SafeExists(t.DriverPed) && SafeExists(t.SpawnedVehicle))
                                    {
                                        Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, t.DriverPed.Handle);
                                        try
                                        {
                                            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, t.TargetPos.X, t.TargetPos.Y, t.TargetPos.Z, newSpeed, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 4.0f);
                                        }
                                        catch
                                        {
                                            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD, t.DriverPed.Handle, t.SpawnedVehicle.Handle, t.TargetPos.X, t.TargetPos.Y, t.TargetPos.Z, newSpeed, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 4.0f);
                                        }
                                        SmoothSetCruiseSpeed(t, newSpeed, 0.22f);
                                        NudgeVehicle(t, t.SpawnedVehicle.ForwardVector * -1f, NUDGE_FORCE * 0.95f, NUDGE_DURATION_MS);
                                    }
                                }
                            }
                            catch { /* swallow */ }
                        }
                        else
                        {
                            // not obvious obstacle — reissue direct longrange drive to target with small velocity nudge
                            try
                            {
                                NudgeVehicle(t, t.SpawnedVehicle.ForwardVector * -1f, NUDGE_FORCE * 0.9f, NUDGE_DURATION_MS);

                                float newSpeed = 26.0f + t.RecoveryAttempts * 4.0f;
                                if (t.DriverPed != null && SafeExists(t.DriverPed) && SafeExists(t.SpawnedVehicle))
                                {
                                    Function.Call(Hash.CLEAR_PED_TASKS, t.DriverPed.Handle);
                                    try
                                    {
                                        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, t.TargetPos.X, t.TargetPos.Y, t.TargetPos.Z, newSpeed, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 4.0f);
                                    }
                                    catch
                                    {
                                        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD, t.DriverPed.Handle, t.SpawnedVehicle.Handle, t.TargetPos.X, t.TargetPos.Y, t.TargetPos.Z, newSpeed, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 4.0f);
                                    }
                                    SmoothSetCruiseSpeed(t, newSpeed, 0.18f);
                                }
                            }
                            catch { /* swallow recovery errors */ }
                        }

                        // reset last-move timer to allow this recovery to take effect
                        t.LastMoveTimeMs = now;
                        t.LastDistance = dist;
                    }
                    else
                    {
                        // exhausted recovery attempts: final forced drive (high priority)
                        try
                        {
                            if (t.DriverPed != null && SafeExists(t.DriverPed) && SafeExists(t.SpawnedVehicle))
                            {
                                Function.Call(Hash.CLEAR_PED_TASKS, t.DriverPed.Handle);
                                try
                                {
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, t.TargetPos.X, t.TargetPos.Y, t.TargetPos.Z, 60.0f, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 2.0f);
                                }
                                catch
                                {
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD, t.DriverPed.Handle, t.SpawnedVehicle.Handle, t.TargetPos.X, t.TargetPos.Y, t.TargetPos.Z, 60.0f, 0, (int)t.ModelHash, AGGRESSIVE_DRIVING_STYLE, 2.0f);
                                }
                                SmoothSetCruiseSpeed(t, 60.0f, 0.22f);

                                // optionally teleport if final fallback enabled
                                if (_teleportOnFinalFail && !t.TeleportFallbackUsed)
                                {
                                    try
                                    {
                                        Vector3 approachDir = Normalize2D(t.TargetPos - t.SpawnedVehicle.Position);
                                        if (approachDir.Length() < 0.001f) approachDir = t.SpawnedVehicle.ForwardVector;
                                        Vector3 teleportPos = t.TargetPos - approachDir * ARRIVE_DISTANCE * 0.7f;
                                        var onStreet = World.GetNextPositionOnStreet(teleportPos);
                                        if (onStreet != Vector3.Zero) teleportPos = onStreet;
                                        SafeCall(() => { Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, t.SpawnedVehicle.Handle, teleportPos.X, teleportPos.Y, teleportPos.Z, false, false, true); });
                                        t.TeleportFallbackUsed = true;
                                        t.LastMoveTimeMs = now;
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                        t.LastMoveTimeMs = now;
                    }
                }

                // ---------- normal enforcement: keep cruise speed and continue ----------
                try
                {
                    if (t.DriverPed != null && SafeExists(t.DriverPed))
                    {
                        // keep baseline cruise; but if in final approach and smooth enabled we already set in that block
                        if (!(_enableHighSmooth && dist <= FINAL_APPROACH_DISTANCE))
                        {
                            // if currently avoiding/recovering, don't stomp on their desired speed
                            if (t.State == BehaviorState.Driving || t.State == BehaviorState.FinalApproach)
                            {
                                SmoothSetCruiseSpeed(t, 35.0f, 0.18f);
                                try { Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, t.DriverPed.Handle, AGGRESSIVE_DRIVING_STYLE); } catch { }
                            }
                        }
                    }
                }
                catch { }

            }
            catch (Exception)
            {
                // swallow per-task errors to avoid mod crash
            }
        }
    }

    // Non-blocking spawn handler (same structure as before, with improved destination normalization)
    private void HandleTaskSpawnState(DeliveryTask t, int now)
    {
        try
        {
            if (t.Stage == SpawnStage.NotStarted)
            {
                // --- NEW: xác định task này có "registered" (được phép giao) hay không.
                bool anyRestriction = (_allowedVehicles.Count > 0) || (_hardcodedAllowed.Count > 0);
                t.IsRegistered = !anyRestriction || _allowedVehicles.Contains(t.ModelHash) || _hardcodedAllowed.Contains(t.ModelHash);

                // --- PATCH: force-spawn override ---
                if (_forceSpawnInFrontMode)
                {
                    // Khi chế độ force bật, coi như mọi task đều KHÔNG registered 
                    // => sẽ kích hoạt nhánh "spawn in front of player" ở Stage RequestedModel.
                    t.IsRegistered = false;
                }

                // request/validate model
                var mLocal = new Model((int)t.ModelHash);
                if (!mLocal.IsValid || !mLocal.IsInCdImage)
                {
                    t.Stage = SpawnStage.Failed;
                    CleanupTask(t, false);
                    return;
                }

                try { if (!mLocal.IsLoaded) mLocal.Request(MODEL_LOAD_TIMEOUT_MS); } catch { }

                t.RequestedModel = mLocal;
                t.ModelRequestStartMs = now;

                // normalize target pos to nearest street
                try
                {
                    var street = FindBestApproachPoint(t.TargetPos);
                    if (street != Vector3.Zero) t.TargetPos = street;
                }
                catch { }

                t.Stage = SpawnStage.RequestedModel;
                return;
            }

            if (t.Stage == SpawnStage.RequestedModel)
            {
                Model m = t.RequestedModel ?? new Model((int)t.ModelHash);

                if (m.IsLoaded)
                {
                    // ---------- PHẦN ĐIỀU CHỈNH THEO HƯỚNG DẪN ----------
                    // Nếu model KHÔNG registered để giao bằng NPC, thì chuyển sang luồng "spawn/teleport trước mặt người chơi"
                    // Đây là hành vi mong muốn cả khi: (a) _forceSpawnInFrontMode == true (chế độ giao tắt) và (b) _forceSpawnInFrontMode == false (NPC-on nhưng model không trong INI)
                    if (!t.IsRegistered)
                    {
                        try
                        {
                            Ped player = Game.Player.Character;
                            if (player == null || !player.Exists())
                            {
                                t.Stage = SpawnStage.Failed;
                                try { t.RequestedModel?.MarkAsNoLongerNeeded(); } catch { }
                                CleanupTask(t, false);
                                return;
                            }

                            Vehicle spawned = null;

                            // ưu tiên spawn trên node đường lộ phía trước player
                            Vector3 roadSpawnPos = Vector3.Zero;
                            float roadSpawnHeading = 0f;
                            Vector3 roadOrigin = player.Position + player.ForwardVector * 12.0f;

                            bool roadSpawnOk = TryFindRoadSpawnPointNear(roadOrigin, 4.0f, 18.0f, 8, out roadSpawnPos, out roadSpawnHeading);

                            if (roadSpawnOk)
                            {
                                try
                                {
                                    spawned = World.CreateVehicle(m, roadSpawnPos, roadSpawnHeading);
                                }
                                catch
                                {
                                    spawned = null;
                                }
                            }

                            // fallback về logic cũ nếu spawn trên node không ra
                            if (spawned == null || !SafeExists(spawned))
                            {
                                Vector3 basePos = player.Position + player.ForwardVector * 3.2f + new Vector3(0f, 0f, 0.5f);

                                for (int attempt = 0; attempt < 6 && (spawned == null || !SafeExists(spawned)); attempt++)
                                {
                                    float jitter = 1.8f + attempt * 0.6f;
                                    Vector3 tryPos = basePos + new Vector3((float)(_rng.NextDouble() - 0.5f) * jitter, (float)(_rng.NextDouble() - 0.5f) * jitter, 0f);
                                    Vector3 ground = World.GetNextPositionOnStreet(tryPos);
                                    if (ground != Vector3.Zero) tryPos = ground;

                                    try
                                    {
                                        spawned = World.CreateVehicle(m, tryPos);
                                    }
                                    catch
                                    {
                                        spawned = null;
                                    }
                                }
                            }

                            if (spawned == null || !SafeExists(spawned))
                            {
                                t.Stage = SpawnStage.Failed;
                                try { t.RequestedModel?.MarkAsNoLongerNeeded(); } catch { }
                                CleanupTask(t, false);
                                return;
                            }

                            try
                            {
                                spawned.PlaceOnGround();
                                spawned.Repair();
                                spawned.DirtLevel = 0f;
                                spawned.IsEngineRunning = true;
                                Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, spawned.Handle, 1);
                            }
                            catch { }

                            try { ApplyOnlineLikeUpgrades(spawned); } catch { }
                            try { ApplyFinalVehicleFinish(spawned); } catch { }

                            try
                            {
                                try { Game.Player.Character.SetIntoVehicle(spawned, VehicleSeat.Driver); }
                                catch
                                {
                                    SafeCall(() =>
                                    {
                                        Function.Call(Hash.SET_PED_INTO_VEHICLE, Game.Player.Character.Handle, spawned.Handle, -1);
                                    });
                                }
                            }
                            catch { }

                            try { PersistentManager.RegisterVehicle(spawned); } catch { }
                            try { t.OnDelivered?.Invoke(spawned); } catch { }
                            try { t.RequestedModel?.MarkAsNoLongerNeeded(); } catch { }

                            t.IsActive = false;
                            lock (_tasks)
                            {
                                if (_tasks.Contains(t)) _tasks.Remove(t);
                            }
                            return;
                        }
                        catch
                        {
                            try { t.RequestedModel?.MarkAsNoLongerNeeded(); } catch { }
                            t.Stage = SpawnStage.Failed;
                            CleanupTask(t, false);
                            return;
                        }
                    }

                    // ---------- ELSE: registered => ưu tiên node đường lộ, fail thì fallback logic cũ ----------
                    bool spawnedOk = false;
                    Vehicle veh = null;
                    Ped driver = null;

                    int maxAttempts = Math.Max(1, Math.Min(SPAWN_ATTEMPTS_MAX, _spawnRetryCount));

                    for (int attempt = 0; attempt < maxAttempts && !spawnedOk; attempt++)
                    {
                        t.SpawnAttempts = attempt + 1;

                        Vector3 spawnPos = Vector3.Zero;
                        float spawnHeading = 0f;
                        bool roadSpawnOk = false;

                        try
                        {
                            float minDist = Math.Max(30f, t.Distance - 20f);
                            float maxDist = Math.Min(220f, t.Distance + 40f);

                            roadSpawnOk = TryFindRoadSpawnPointNear(t.TargetPos, minDist, maxDist, 10, out spawnPos, out spawnHeading);
                        }
                        catch
                        {
                            roadSpawnOk = false;
                        }

                        if (roadSpawnOk)
                        {
                            try
                            {
                                veh = World.CreateVehicle(m, spawnPos, spawnHeading);
                            }
                            catch
                            {
                                veh = null;
                            }
                        }

                        // fallback về logic cũ nếu node spawn không thành công
                        if (veh == null || !SafeExists(veh))
                        {
                            double angle = (_rng.NextDouble() * 2.0 * Math.PI);
                            float attemptRadius = t.Distance;
                            if (attempt == 0) attemptRadius = Math.Max(t.Distance, SPAWN_RADIUS_MIN);
                            else attemptRadius = Math.Min(SPAWN_RADIUS_MAX, t.Distance + attempt * 12);

                            float dx = (float)Math.Cos(angle);
                            float dy = (float)Math.Sin(angle);
                            Vector3 oldSpawnPos = t.TargetPos + new Vector3(dx * attemptRadius, dy * attemptRadius, 3.0f);
                            Vector3 groundPos = World.GetNextPositionOnStreet(oldSpawnPos);
                            if (groundPos != Vector3.Zero) oldSpawnPos = groundPos;

                            try { veh = World.CreateVehicle(m, oldSpawnPos); } catch { veh = null; }
                        }

                        if (veh == null || !SafeExists(veh))
                        {
                            try { t.RequestedModel?.MarkAsNoLongerNeeded(); } catch { }
                            continue;
                        }

                        try
                        {
                            veh.PlaceOnGround();
                            veh.Repair();
                            veh.DirtLevel = 0f;
                            veh.IsEngineRunning = true;
                            Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, veh.Handle, 1);
                        }
                        catch { }

                        driver = null;
                        var pedCandidates = new uint[] { 0x9CE6AEB5u, 0x705E61F2u, 0xC7C21664u, 0xA10B9F45u };
                        Model pedModel = null;
                        foreach (var pm in pedCandidates)
                        {
                            try
                            {
                                var mm = new Model((int)pm);
                                if (mm.IsValid)
                                {
                                    pedModel = mm;
                                    break;
                                }
                            }
                            catch { }
                        }

                        if (pedModel == null)
                        {
                            try { if (veh != null && veh.Exists()) veh.Delete(); } catch { }
                            try { t.RequestedModel?.MarkAsNoLongerNeeded(); } catch { }
                            try { if (pedModel != null) pedModel.MarkAsNoLongerNeeded(); } catch { }
                            continue;
                        }

                        try { if (!pedModel.IsLoaded) pedModel.Request(PED_MODEL_LOAD_TIMEOUT_MS); } catch { }
                        try { driver = World.CreatePed(pedModel, veh.Position + veh.UpVector * 0.5f); } catch { driver = null; }

                        if (driver == null || !SafeExists(driver))
                        {
                            try { if (veh.Exists()) veh.Delete(); } catch { }
                            try { t.RequestedModel?.MarkAsNoLongerNeeded(); } catch { }
                            try { if (pedModel != null) pedModel.MarkAsNoLongerNeeded(); } catch { }
                            continue;
                        }

                        try
                        {
                            driver.SetIntoVehicle(veh, VehicleSeat.Driver);
                            driver.BlockPermanentEvents = true;
                            driver.IsPersistent = true;
                            driver.CanBeTargetted = false;
                        }
                        catch { }

                        try { ApplyOnlineLikeUpgrades(veh); } catch { }

                        float postSpawnDist = veh.Position.DistanceTo(t.TargetPos);
                        if (postSpawnDist > SPAWN_RADIUS_MAX + 50f)
                        {
                            try { if (driver.Exists()) driver.Delete(); } catch { }
                            try { if (veh.Exists()) veh.Delete(); } catch { }
                            continue;
                        }

                        try
                        {
                            try
                            {
                                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                                    driver.Handle, veh.Handle,
                                    t.TargetPos.X, t.TargetPos.Y, t.TargetPos.Z,
                                    35.0f, 0, (int)t.ModelHash,
                                    AGGRESSIVE_DRIVING_STYLE, 4.0f);
                            }
                            catch
                            {
                                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD,
                                    driver.Handle, veh.Handle,
                                    t.TargetPos.X, t.TargetPos.Y, t.TargetPos.Z,
                                    35.0f, 0, (int)t.ModelHash,
                                    AGGRESSIVE_DRIVING_STYLE, 4.0f);
                            }

                            Function.Call(Hash.SET_DRIVE_TASK_CRUISE_SPEED, driver.Handle, 35.0f);
                            try { Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, driver.Handle, AGGRESSIVE_DRIVING_STYLE); } catch { }
                        }
                        catch
                        {
                            try { driver.Task.CruiseWithVehicle(veh, 35.0f); } catch { }
                        }

                        t.SpawnedVehicle = veh;
                        t.DriverPed = driver;
                        t.HasSpawned = true;
                        t.Stage = SpawnStage.Spawned;

                        t.LastDistance = Vector3.Distance(veh.Position, t.TargetPos);
                        t.LastMoveTimeMs = Game.GameTime;
                        t.RecoveryAttempts = 0;
                        t.IsBraking = false;
                        t.LastNudgeMs = 0;
                        t.State = BehaviorState.Driving;
                        t.StateStartMs = Game.GameTime;
                        t.AvoidTarget = Vector3.Zero;

                        try { t.RequestedModel?.MarkAsNoLongerNeeded(); } catch { }
                        try { if (pedModel != null) pedModel.MarkAsNoLongerNeeded(); } catch { }

                        spawnedOk = true;
                        break;
                    }

                    if (!spawnedOk)
                    {
                        try { t.RequestedModel?.MarkAsNoLongerNeeded(); } catch { }
                        t.Stage = SpawnStage.Failed;
                        CleanupTask(t, false);
                        return;
                    }
                    return;
                }
                else
                {
                    // Model chưa load xong
                    if (now - t.ModelRequestStartMs > MODEL_LOAD_TIMEOUT_MS)
                    {
                        try { t.RequestedModel?.MarkAsNoLongerNeeded(); } catch { }
                        t.Stage = SpawnStage.Failed;
                        CleanupTask(t, false);
                        return;
                    }
                    return;
                }
            }
        }
        catch (Exception)
        {
            try { t.RequestedModel?.MarkAsNoLongerNeeded(); } catch { }
            t.Stage = SpawnStage.Failed;
            CleanupTask(t, false);
            return;
        }
    }

    private void ApplyFinalVehicleFinish(Vehicle v)
    {
        if (!SafeExists(v)) return;
        try
        {
            v.Repair();
            v.DirtLevel = 0f;
            v.IsEngineRunning = false;
            v.IsEngineRunning = true;
            try { ApplyRandomTyreSmokeColor(v); } catch { }
            for (int ex = 1; ex <= 20; ex++)
            {
                try
                {
                    if (Function.Call<bool>(Hash.DOES_EXTRA_EXIST, v.Handle, ex))
                        Function.Call(Hash.SET_VEHICLE_EXTRA, v.Handle, ex, 0);
                }
                catch { }
            }
            try { ApplyRandomVehicleColours(v); } catch { }
            try { Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v.Handle, 1); } catch { }
        }
        catch { }
    }

    // --- REPLACE the whole ApplyOnlineLikeUpgrades method with this one ---
    private void ApplyOnlineLikeUpgrades(Vehicle v)
    {
        if (!SafeExists(v)) return;
        try
        {
            SafeCall(() => { Function.Call(Hash.SET_VEHICLE_MOD_KIT, v.Handle, 0); return 0; });

            // --- NEW: decide per-vehicle whether to apply "full" (60%) or "random" (40%) ---
            bool chooseFullVehicle = _rng.NextDouble() < 0.60; // tune 0.60 to change probability

            for (int modType = 0; modType <= 49; modType++)
            {
                if (!SafeExists(v)) break;
                if (modType == 18) continue; // turbo handled later

                int count = SafeCall(() => Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, v.Handle, modType), 0);
                if (count > 0)
                {
                    int chosenIndex = chooseFullVehicle ? (count - 1) : _rng.Next(0, count); // 0..count-1
                    SafeCall(() => { Function.Call(Hash.SET_VEHICLE_MOD, v.Handle, modType, chosenIndex, false); return 0; });
                }
            }

            int[] perfMods = new int[] { 11, 12, 13, 16 };
            foreach (int pm in perfMods)
            {
                if (!SafeExists(v)) break;
                int count = SafeCall(() => Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, v.Handle, pm), 0);
                if (count > 0)
                {
                    int chosenIndex = chooseFullVehicle ? (count - 1) : _rng.Next(0, count);
                    SafeCall(() => { Function.Call(Hash.SET_VEHICLE_MOD, v.Handle, pm, chosenIndex, false); return 0; });
                }
            }

            // Turbo (modType 18) - keep toggle, but choose index similarly
            SafeCall(() => { Function.Call(Hash.TOGGLE_VEHICLE_MOD, v.Handle, 18, true); return 0; });
            int turboCount = SafeCall(() => Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, v.Handle, 18), 0);
            if (turboCount > 0)
            {
                int chosenIndex = chooseFullVehicle ? (turboCount - 1) : _rng.Next(0, turboCount);
                SafeCall(() => { Function.Call(Hash.SET_VEHICLE_MOD, v.Handle, 18, chosenIndex, false); return 0; });
            }

            // Livery: choose last or random
            SafeCall(() =>
            {
                int liveryCount = SafeCall(() => Function.Call<int>(Hash.GET_VEHICLE_LIVERY_COUNT, v.Handle), 0);
                if (liveryCount > 0)
                {
                    int chosenLivery = chooseFullVehicle ? Math.Max(0, liveryCount - 1) : _rng.Next(0, liveryCount);
                    Function.Call(Hash.SET_VEHICLE_LIVERY, v.Handle, chosenLivery);
                }
                return 0;
            });

            // The rest unchanged
            // Random primary/secondary colors instead of forcing black
            SafeCall(() => { ApplyRandomVehicleColours(v); return 0; });
            SafeCall(() => { Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, v.Handle, 0, 0); return 0; });
            SafeCall(() => { Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v.Handle, 1); return 0; });
            SafeCall(() => { ApplyRandomTyreSmokeColor(v); return 0; });

            for (int ex = 1; ex <= 20; ex++)
            {
                if (!SafeExists(v)) break;
                bool exists = SafeCall(() => Function.Call<bool>(Hash.DOES_EXTRA_EXIST, v.Handle, ex), false);
                if (exists) SafeCall(() => { Function.Call(Hash.SET_VEHICLE_EXTRA, v.Handle, ex, 0); return 0; });
            }

            SafeCall(() => { v.IsEngineRunning = true; return 0; });
            SafeCall(() => { v.DirtLevel = 0f; return 0; });
            SafeCall(() => { v.Repair(); return 0; });

            // pick a Menyoo color name randomly
            string chosenName = _sharedColorNames[_rng.Next(_sharedColorNames.Length)];

            // map the chosen name deterministically to RGB (game native requires RGB)
            var rgb = NameToRgb(chosenName);

            // Enable all neon positions (0=Left,1=Right,2=Front,3=Back) if present and set color
            try
            {
                for (int i = 0; i <= 3; i++)
                {
                    if (!SafeExists(v)) break;
                    SafeCall(() => { Function.Call(Hash.SET_VEHICLE_NEON_ENABLED, v.Handle, i, true); return 0; });
                }

                // set neon colour (native expects RGB)
                SafeCall(() => { Function.Call(Hash.SET_VEHICLE_NEON_COLOUR, v.Handle, rgb.Item1, rgb.Item2, rgb.Item3); return 0; });
            }
            catch { /* swallow neon errors to avoid crash on unsupported vehicles */ }

            // Dashboard Colour (Paint Index 0..177) — choose randomly and apply
            try
            {
                int paintIndex = _rng.Next(0, 178); // inclusive 0 .. 177
                SafeCall(() => { Function.Call(Hash.SET_VEHICLE_EXTRA_COLOUR_6, v.Handle, paintIndex); return 0; });
            }
            catch { /* ignore if native not available */ }
        }
        catch { }
    }

    private Tuple<int, int, int> NameToRgb(string name)
    {
        if (string.IsNullOrEmpty(name)) return Tuple.Create(255, 255, 255);

        int r = 0, g = 0, b = 0;
        for (int i = 0; i < name.Length; i++)
        {
            int ch = (int)name[i];
            r = (r * 31 + ch + 7) % 256;
            g = (g * 37 + ch + 13) % 256;
            b = (b * 41 + ch + 97) % 256;
        }

        if (r < 30 && g < 30 && b < 30)
        {
            r = (r + 120) % 256;
            g = (g + 120) % 256;
            b = (b + 120) % 256;
        }

        int max = Math.Max(r, Math.Max(g, b));
        if (max > 0 && max < 200)
        {
            float factor = 200f / Math.Max(1, max);
            r = Math.Min(255, (int)(r * factor));
            g = Math.Min(255, (int)(g * factor));
            b = Math.Min(255, (int)(b * factor));
        }

        return Tuple.Create(r, g, b);
    }

    // --- ADDITIONAL HELPER: deterministic name->RGB mapping (place this helper inside the same class) ---
    private void ApplyRandomVehicleColours(Vehicle v)
    {
        if (!SafeExists(v)) return;

        try
        {
            // Menyoo verified range: 0..218
            int primary = _rng.Next(0, 219);
            int secondary = _rng.Next(0, 219);

            // Apply random primary/secondary colors
            Function.Call(Hash.SET_VEHICLE_COLOURS, v.Handle, primary, secondary);

            // Optional: keep these as-is or randomize separately if you want.
            // Here we leave them neutral to avoid unwanted overrides.
            // Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, v.Handle, 0, 0);
        }
        catch
        {
            // fallback to original behavior if native call fails
            try { Function.Call(Hash.SET_VEHICLE_COLOURS, v.Handle, 0, 0); } catch { }
        }
    }

    private void ShowFeedMessage(string sender, string subject, string body)
    {
        try
        {
            Notification.Show(NotificationIcon.Carsite, sender, subject, body);
        }
        catch
        {
            GTA.UI.Notification.Show(body);
        }
    }

    private string Tr(string key, string fallback = "")
    {
        return Language.Get(key, fallback);
    }

    // Thêm vào trong class VehicleDelivery (ví dụ trước phần Helpers)
    [Flags]
    private enum IntersectOptions
    {
        Peds = 4,
        Vehicles = 8,
        MapObjects = 16,
        Everything = 65535
    }

    // Pegasus Concierge phone contact + LemonUI menu
    private void EnsurePegasusConciergeContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_pegasusPhoneInstance, phone))
            {
                _pegasusPhoneInstance = phone;
                _pegasusContactAdded = false;
            }

            if (_pegasusContactAdded)
                return;

            string pegasusName = ContactName("Contact_PegasusConcierge", "Pegasus Concierge");

            if (phone.Contacts.Any(c =>
                string.Equals(c.Name, pegasusName, StringComparison.OrdinalIgnoreCase)))
            {
                _pegasusContactAdded = true;
                return;
            }

            var pegasusContact = new iFruitContact(pegasusName)
            {
                Active = true,
                DialTimeout = 2000,
                Bold = false,
                Icon = ContactIcon.Pegasus
            };

            pegasusContact.Answered += OnPegasusConciergeAnswered;
            phone.Contacts.Add(pegasusContact);
            _pegasusContactAdded = true;
        }
        catch (Exception ex)
        { }
    }

    private void OnPegasusConciergeAnswered(iFruitContact sender)
    {
        try
        {
            OpenPegasusConciergeMenu();

            try
            {
                CustomiFruit.GetCurrentInstance()?.Close(0);
            }
            catch { }
        }
        catch (Exception ex)
        { }
    }

    private void OpenPegasusConciergeMenu()
    {
        try
        {
            EnsurePegasusConciergeMenu();
            SyncPegasusConciergeMenuState();

            if (_pegasusConciergeMenu != null)
            {
                _pegasusConciergeMenu.Visible = true;
                Interval = 0;
            }
        }
        catch (Exception ex)
        { }
    }

    private void EnsurePegasusConciergeMenu()
    {
        if (_pegasusConciergeMenu != null)
            return;

        _pegasusConciergeMenu = new NativeMenu(
            Tr("Pegasus_MenuTitle", "Vehicle Delivery"),
            Tr("Pegasus_MenuSubtitle", "VẬN CHUYỂN PHƯƠNG TIỆN"));

        _luiPool.Add(_pegasusConciergeMenu);

        _pegasusCurrentLocationItem = new NativeCheckboxItem(
            Tr("Pegasus_DeliverToLocation", "Giao đến vị trí"),
            true);

        _pegasusExpressItem = new NativeCheckboxItem(
            Tr("Pegasus_ExpressDelivery", "Chuyển phát cấp tốc"),
            false);

        _pegasusCurrentLocationItem.CheckboxChanged += PegasusCurrentLocationItem_CheckboxChanged;
        _pegasusExpressItem.CheckboxChanged += PegasusExpressItem_CheckboxChanged;

        var cancel = new NativeItem(
            Tr("Pegasus_CancelService", "Hủy dịch vụ"));

        cancel.Activated += (s, e) => ClosePegasusConciergeMenu(false);

        _pegasusConciergeMenu.Add(_pegasusCurrentLocationItem);
        _pegasusConciergeMenu.Add(_pegasusExpressItem);
        _pegasusConciergeMenu.Add(cancel);
    }

    private void SyncPegasusConciergeMenuState()
    {
        if (_pegasusCurrentLocationItem == null || _pegasusExpressItem == null)
            return;

        _pegasusMenuSyncLock = true;
        try
        {
            _pegasusCurrentLocationItem.Checked = !_forceSpawnInFrontMode;
            _pegasusExpressItem.Checked = _forceSpawnInFrontMode;
        }
        finally
        {
            _pegasusMenuSyncLock = false;
        }
    }

    private void PegasusCurrentLocationItem_CheckboxChanged(object sender, EventArgs e)
    {
        if (_pegasusMenuSyncLock || _pegasusCurrentLocationItem == null || _pegasusExpressItem == null)
            return;

        _pegasusMenuSyncLock = true;
        try
        {
            if (_pegasusCurrentLocationItem.Checked)
            {
                _forceSpawnInFrontMode = false;
                if (_pegasusExpressItem.Checked)
                    _pegasusExpressItem.Checked = false;

                ClosePegasusConciergeMenu(false);
            }
            else
            {
                // Không cho phép cả hai cùng tắt.
                if (!_pegasusExpressItem.Checked)
                    _pegasusCurrentLocationItem.Checked = true;
            }
        }
        finally
        {
            _pegasusMenuSyncLock = false;
        }
    }

    private void PegasusExpressItem_CheckboxChanged(object sender, EventArgs e)
    {
        if (_pegasusMenuSyncLock || _pegasusCurrentLocationItem == null || _pegasusExpressItem == null)
            return;

        _pegasusMenuSyncLock = true;
        try
        {
            if (_pegasusExpressItem.Checked)
            {
                _forceSpawnInFrontMode = true;
                if (_pegasusCurrentLocationItem.Checked)
                    _pegasusCurrentLocationItem.Checked = false;

                ClosePegasusConciergeMenu(false);
            }
            else
            {
                // Không cho phép cả hai cùng tắt.
                if (!_pegasusCurrentLocationItem.Checked)
                    _pegasusExpressItem.Checked = true;
            }
        }
        finally
        {
            _pegasusMenuSyncLock = false;
        }
    }

    private void ClosePegasusConciergeMenu(bool countCancel)
    {
        try
        {
            if (_pegasusConciergeMenu != null)
                _pegasusConciergeMenu.Visible = false;
        }
        catch { }

        Interval = 1000;
    }

    private bool SafeExists(Entity e) { try { return e != null && e.Exists(); } catch { return false; } }

    private T SafeCall<T>(Func<T> fn, T fallback = default)
    {
        try { if (fn == null) return fallback; return fn(); }
        catch { return fallback; }
    }

    private void SafeCall(Action act)
    {
        try { act?.Invoke(); }
        catch { }
    }

    private void DelayThen(Action action, int delayMs)
    {
        int start = Game.GameTime;
        Tick += LocalDelayedTick;
        void LocalDelayedTick(object s, EventArgs e)
        {
            try
            {
                if (Game.GameTime - start >= delayMs)
                {
                    try { action?.Invoke(); } catch { }
                    Tick -= LocalDelayedTick;
                }
            }
            catch { Tick -= LocalDelayedTick; }
        }
    }

    private void CleanupTask(DeliveryTask t, bool success)
    {
        try
        {
            // mark finalizing to avoid loops
            t.IsFinalizing = true;

            if (t.DriverPed != null && SafeExists(t.DriverPed))
            {
                try
                {
                    t.DriverPed.BlockPermanentEvents = false;
                    t.DriverPed.IsPersistent = false;
                    DelayThen(() =>
                    {
                        try { if (t.DriverPed.Exists()) t.DriverPed.Delete(); } catch { }
                    }, 10000);
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            t.IsActive = false;
            lock (_tasks) { if (_tasks.Contains(t)) _tasks.Remove(t); }
        }
    }

    // Public API: default maxTimeMs = -1 means infinite (no timeout)
    // IMPORTANT: targetPosition will be normalized to nearest street to ensure reachability.
    public static void RequestDelivery(uint modelHash, Vector3 targetPosition, Action<Vehicle> onDelivered = null, int distance = 80, int maxTimeMs = -1)
    {
        try
        {
            if (_singleton == null) _singleton = new VehicleDelivery();
            var inst = _singleton;
            if (inst == null) return;

            // normalize now to nearest street (best-effort), so the driver aims for a reachable coordinate near player
            Vector3 normalized = targetPosition;
            try
            {
                var s = World.GetNextPositionOnStreet(targetPosition);
                if (s != Vector3.Zero) normalized = s;
            }
            catch { }

            var task = new DeliveryTask
            {
                ModelHash = modelHash,
                TargetPos = normalized,
                StartTimeMs = Game.GameTime,
                MaxTimeMs = maxTimeMs,
                Distance = distance,
                OnDelivered = onDelivered,
                IsActive = true,
                HasSpawned = false,
                Stage = SpawnStage.NotStarted,
                SpawnAttempts = 0,
                TeleportFallbackUsed = false
            };

            lock (inst._tasks) inst._tasks.Add(task);
        }
        catch { }
    }

    // ---------- Additional helper utilities ----------

    // Attempt to pick a "best" approach point on street around the desired goal.
    // Strategy: sample points on a small ring around target, snap to street, prefer points that are reachable and not in water.
    private Vector3 FindBestApproachPoint(Vector3 target)
    {
        try
        {
            Vector3 best = Vector3.Zero;
            float bestScore = float.MaxValue;

            // sample radii and angles (more resolution close-in)
            float[] radii = new float[] { 0.8f, 2f, 4f, 6f, 8f };
            int samplesPerRadius = 12;

            foreach (var r in radii)
            {
                for (int i = 0; i < samplesPerRadius; i++)
                {
                    double angle = (i / (double)samplesPerRadius) * Math.PI * 2.0;
                    float dx = (float)Math.Cos(angle);
                    float dy = (float)Math.Sin(angle);
                    Vector3 candidate = target + new Vector3(dx * r, dy * r, 0f);
                    Vector3 onStreet = World.GetNextPositionOnStreet(candidate);
                    if (onStreet == Vector3.Zero) continue;

                    // prefer ones near the target and slightly more 'open' (low surrounding height variance)
                    float score = Vector3.Distance(onStreet, target);

                    // penalize water or underground (z far from target.z)
                    if (Math.Abs(onStreet.Z - target.Z) > 2.5f) score += 5.0f;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = onStreet;
                    }
                }
                if (best != Vector3.Zero) break; // early out - prefer closer radii results
            }

            // fallback to direct street snap
            if (best == Vector3.Zero)
            {
                var s = World.GetNextPositionOnStreet(target);
                if (s != Vector3.Zero) best = s;
            }

            return best;
        }
        catch { return Vector3.Zero; }
    }

    private bool TryFindRoadSpawnPointNear(Vector3 origin, float minDistance, float maxDistance, int attempts, out Vector3 spawnPos, out float heading)
    {
        spawnPos = Vector3.Zero;
        heading = 0f;

        try
        {
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                float dist = minDistance + (float)_rng.NextDouble() * (maxDistance - minDistance);
                float angle = (float)(_rng.NextDouble() * Math.PI * 2.0);

                Vector3 guess = origin + new Vector3(
                    (float)Math.Cos(angle) * dist,
                    (float)Math.Sin(angle) * dist,
                    0f);

                OutputArgument nodePos = new OutputArgument();
                OutputArgument nodeHeading = new OutputArgument();

                bool found = Function.Call<bool>(
                    Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                    guess.X, guess.Y, guess.Z + 50f,
                    nodePos, nodeHeading,
                    1, 3.0f, 0);

                if (!found)
                    continue;

                Vector3 p = nodePos.GetResult<Vector3>();
                float h = nodeHeading.GetResult<float>();

                float groundZ = p.Z;
                try
                {
                    OutputArgument zArg = new OutputArgument();
                    if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, p.X, p.Y, p.Z + 80f, zArg, false))
                        groundZ = zArg.GetResult<float>();
                }
                catch { }

                spawnPos = new Vector3(p.X, p.Y, groundZ + 0.5f);
                heading = h;
                return true;
            }
        }
        catch { }

        return false;
    }

    // ---------- NEW: Get detailed obstacle info ----------
    // returns true if any hit; out closestHit distance, out hit position & hit entity.
    private bool GetObstacleInfo(Vehicle v, float checkDistance, out float closestHit, out Vector3 hitPos, out Entity hitEntity)
    {
        closestHit = float.MaxValue;
        hitPos = Vector3.Zero;
        hitEntity = null;
        try
        {
            if (!SafeExists(v)) return false;

            Vector3 start = v.Position + new Vector3(0f, 0f, 0.6f);
            Vector3 forward = v.ForwardVector;
            Vector3 perp = new Vector3(-forward.Y, forward.X, 0f);
            float sideAngle = 0.28f;

            var tests = new List<Tuple<Vector3, float>>();
            tests.Add(Tuple.Create(Normalize2D(forward), 1.0f));
            tests.Add(Tuple.Create(Normalize2D(forward + perp * 0.25f), 1.0f));
            tests.Add(Tuple.Create(Normalize2D(forward - perp * 0.25f), 1.0f));
            tests.Add(Tuple.Create(Normalize2D(new Vector3(forward.X, forward.Y, 0.15f)), 1.0f));
            tests.Add(Tuple.Create(Normalize2D(new Vector3((float)(forward.X * Math.Cos(sideAngle) - forward.Y * Math.Sin(sideAngle)), (float)(forward.X * Math.Sin(sideAngle) + forward.Y * Math.Cos(sideAngle)), 0f)), 1.0f));
            tests.Add(Tuple.Create(Normalize2D(new Vector3((float)(forward.X * Math.Cos(-sideAngle) - forward.Y * Math.Sin(-sideAngle)), (float)(forward.X * Math.Sin(-sideAngle) + forward.Y * Math.Cos(-sideAngle)), 0f)), 1.0f));

            bool anyHit = false;

            foreach (var t in tests)
            {
                Vector3 dir = t.Item1;
                if (dir == Vector3.Zero) continue;

                Vector3 end = start + dir * checkDistance;
                var res = World.Raycast(start, end, GTA.IntersectFlags.Everything, v);
                if (!res.DidHit) continue;

                float hitDist = Vector3.Distance(start, res.HitPosition);
                if (hitDist < 1.0f) continue; // ignore near self-collision

                // attempt to ignore very small decorative props by using the hit entity type heuristics
                anyHit = true;
                if (hitDist < closestHit)
                {
                    closestHit = hitDist;
                    hitPos = res.HitPosition;
                    hitEntity = res.HitEntity;
                }

                if (_debugMode) Notification.Show($"~b~Obstacle ray hit @ {hitDist:F1}m");
            }

            return anyHit;
        }
        catch
        {
            return false;
        }
    }

    // compute a perpendicular direction vector (XZ plane)
    private Vector3 GetPerpendicularDir(Vector3 forward)
    {
        return new Vector3(-forward.Y, forward.X, 0f);
    }

    // Try find lateral clear space (lane change / slip) near vehicle when no detour found.
    private Vector3 TryFindLateralClearSpace(DeliveryTask t, float closestHit, float sideOffset)
    {
        try
        {
            if (t == null || t.SpawnedVehicle == null || !SafeExists(t.SpawnedVehicle)) return Vector3.Zero;
            var v = t.SpawnedVehicle;
            Vector3 pos = v.Position;
            Vector3 forward = Normalize2D(v.ForwardVector);
            if (forward == Vector3.Zero) forward = new Vector3(1f, 0f, 0f);
            Vector3 perp = GetPerpendicularDir(forward);

            // try multiples of offset to left/right
            float[] offsets = new float[] { sideOffset, sideOffset * 1.5f, sideOffset * 0.75f, sideOffset * 2.0f };
            var sides = new[] { 1f, -1f }; // right first then left
            if (_rng.NextDouble() > 0.5) sides = new[] { -1f, 1f };

            foreach (var s in sides)
            {
                foreach (var off in offsets)
                {
                    Vector3 candidate = pos + perp * (off * s) + forward * Math.Max(4.0f, closestHit * 0.6f);
                    Vector3 onStreet = World.GetNextPositionOnStreet(candidate);
                    if (onStreet == Vector3.Zero) continue;

                    // quick ray to candidate to ensure it's not blocked
                    var ray = World.Raycast(pos + new Vector3(0f, 0f, 0.6f), onStreet + new Vector3(0f, 0f, 0.6f), GTA.IntersectFlags.Everything, t.SpawnedVehicle);
                    if (ray.DidHit)
                    {
                        float hitDist = Vector3.Distance(pos, ray.HitPosition);
                        if (hitDist < 1.2f) continue;
                        if (hitDist < Vector3.Distance(pos, onStreet) - 0.6f) continue;
                    }

                    if (Math.Abs(onStreet.Z - pos.Z) > 2.5f) continue;

                    return onStreet;
                }
            }

            return Vector3.Zero;
        }
        catch { return Vector3.Zero; }
    }

    // Apply a short velocity "nudge" to the vehicle along 'dir' (dir not necessarily normalized)
    private void NudgeVehicle(DeliveryTask t, Vector3 dir, float force, int durationMs)
    {
        try
        {
            if (t == null || !SafeExists(t.SpawnedVehicle)) return;
            var v = t.SpawnedVehicle;
            Vector3 baseVel = Vector3.Zero;
            try { baseVel = v.Velocity; } catch { baseVel = Vector3.Zero; }
            Vector3 d = dir;
            if (d.Length() <= 0.001f) d = v.ForwardVector;
            // normalize horizontally
            d = new Vector3(d.X, d.Y, 0f);
            float len = (float)Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (len <= 0.001f) d = new Vector3(v.ForwardVector.X, v.ForwardVector.Y, 0f);
            else d = new Vector3(d.X / len, d.Y / len, 0f);

            Vector3 applied = baseVel + d * force;
            // keep existing z velocity small
            applied = new Vector3(applied.X, applied.Y, baseVel.Z);

            SafeCall(() => { Function.Call(Hash.SET_ENTITY_VELOCITY, v.Handle, applied.X, applied.Y, applied.Z); });

            // stop the nudge after duration
            DelayThen(() =>
            {
                try
                {
                    if (!SafeExists(v)) return;
                    // reduce velocity so AI can re-take control - don't zero everything abruptly if moving
                    SafeCall(() => { Function.Call(Hash.SET_ENTITY_VELOCITY, v.Handle, 0f, 0f, 0f); });
                }
                catch { }
            }, durationMs);
        }
        catch { }
    }

    // --- NEW HELPER: FindDetourWaypoint ---
    // Try to find a reachable detour waypoint to the left or right of vehicle that leads around an obstacle.
    // Returns Vector3.Zero if none found.
    private Vector3 FindDetourWaypoint(DeliveryTask t, float forwardLook = 6.0f, float initialSide = 4.0f, int sideSteps = 5, int forwardSteps = 3)
    {
        try
        {
            if (t == null || t.SpawnedVehicle == null || !SafeExists(t.SpawnedVehicle)) return Vector3.Zero;

            var v = t.SpawnedVehicle;
            Vector3 vehPos = v.Position;
            Vector3 forward = Normalize2D(v.ForwardVector);
            if (forward == Vector3.Zero) forward = new Vector3(1f, 0f, 0f);
            Vector3 perp = GetPerpendicularDir(forward);

            // try both sides, prefer whichever gives a clear ray to the candidate
            var sides = new[] { 1f, -1f }; // 1 = right, -1 = left (we'll prefer right first randomly sometimes)
                                           // small randomness to pick side preference to reduce deterministic jams
            if (_rng.NextDouble() > 0.5) sides = new[] { -1f, 1f };

            for (int fStep = forwardSteps; fStep >= 1; fStep--)
            {
                float fDist = forwardLook * fStep / forwardSteps; // nearer forward first
                foreach (var sideMult in sides)
                {
                    for (int s = 1; s <= sideSteps; s++)
                    {
                        float sideOffset = initialSide * s / (float)sideSteps * Math.Abs(sideMult);
                        Vector3 candidate = vehPos + forward * fDist + perp * (sideOffset * sideMult) + new Vector3(0f, 0f, 0.8f);
                        Vector3 onStreet = World.GetNextPositionOnStreet(candidate);
                        if (onStreet == Vector3.Zero) continue;

                        // quick ray from vehicle to candidate to ensure path is free (no mid obstacles)
                        var ray = World.Raycast(vehPos + new Vector3(0f, 0f, 0.6f), onStreet + new Vector3(0f, 0f, 0.6f), GTA.IntersectFlags.Everything, v);
                        if (ray.DidHit)
                        {
                            // if we hit something very near candidate point (like the street edge), we may still accept if hit is far
                            float hitDist = Vector3.Distance(vehPos, ray.HitPosition);
                            if (hitDist < 1.5f) continue; // immediate block
                                                          // otherwise consider candidate only if blockage is after candidate (rare)
                            if (hitDist < Vector3.Distance(vehPos, onStreet) - 0.4f) continue;
                        }

                        // finally check candidate isn't inside water / bad Z difference
                        if (Math.Abs(onStreet.Z - vehPos.Z) > 3.5f) continue;

                        // Good candidate found
                        t.LastDetourWaypoint = onStreet;
                        t.LastDetourTimeMs = Game.GameTime;
                        if (_debugMode) Notification.Show($"~g~Detour waypoint found L{(sideMult < 0 ? -1 : 1)} s{sideOffset:F1} f{fDist:F1}");
                        return onStreet;
                    }
                }
            }

            return Vector3.Zero;
        }
        catch { return Vector3.Zero; }
    }

    // --- NEW HELPER: SmoothSetCruiseSpeed ---
    // Gradually change cruise speed to avoid abrupt jumps (also updates t.LastDesiredSpeed)
    private void SmoothSetCruiseSpeed(DeliveryTask t, float targetSpeed, float smoothingFactor = 0.22f)
    {
        try
        {
            if (t == null || t.DriverPed == null || !SafeExists(t.DriverPed)) return;

            // initialize last speed if zero (first-time)
            if (t.LastDesiredSpeed <= 0.01f) t.LastDesiredSpeed = targetSpeed;

            // simple exponential smoothing
            float next = t.LastDesiredSpeed + (targetSpeed - t.LastDesiredSpeed) * smoothingFactor;

            // clamp small steps to avoid being stuck very low or small jitter
            if (Math.Abs(next - t.LastDesiredSpeed) < 0.05f) next = targetSpeed;

            // apply
            try { Function.Call(Hash.SET_DRIVE_TASK_CRUISE_SPEED, t.DriverPed.Handle, next); } catch { }

            t.LastDesiredSpeed = next;
        }
        catch { }
    }

    private class AdvancedAIModule
    {
        private VehicleDelivery _owner;
        private Random _rng = new Random();

        public AdvancedAIModule(VehicleDelivery owner)
        {
            _owner = owner;
        }

        // Entry point per-task (safe: do light-weight checks only)
        public void UpdateForTask(DeliveryTask t, int now)
        {
            try
            {
                if (t == null || !t.IsActive || t.SpawnedVehicle == null || !_owner.SafeExists(t.SpawnedVehicle)) return;

                // 1) Pursuit prediction / interdiction detection
                EvaluatePursuitPrediction(t, now);

                // 2) If player is being intercepted (pursuit confidence high), attempt PIT or roadblock
                if (t.PursuitConfidence > 0.65f)
                {
                    // PIT attempt logic - opportunistic
                    if (now - t.LastPITAttemptMs > t.PITCooldownMs)
                    {
                        if (TryAttemptPIT(t, now)) t.LastPITAttemptMs = now;
                    }
                }

                // 3) If stuck or obstacle frequently, do proactive replan (repath)
                if (now - t.LastRepathMs > t.RepathCooldownMs)
                {
                    // if obstacle close & we have low progress -> attempt to recompute detour
                    float closestHit; Vector3 hitPos; Entity hitEntity;
                    bool obstacle = _owner.GetObstacleInfo(t.SpawnedVehicle, _owner.OBSTACLE_CHECK_DISTANCE, out closestHit, out hitPos, out hitEntity);
                    if (obstacle || t.RecoveryAttempts > 0)
                    {
                        if (t.RepathAttempts < t.MaxRepathAttempts)
                        {
                            t.RepathAttempts++;
                            t.LastRepathMs = now;
                            // compute alternate waypoint and issue drive-to (non-destructive)
                            Vector3 alt = _owner.FindDetourWaypoint(t, forwardLook: Math.Max(6.0f, closestHit + 2.0f), initialSide: _owner.SIDE_NUDGE_OFFSET, sideSteps: 6, forwardSteps: 4);
                            if (alt != Vector3.Zero)
                            {
                                SafeIssueDetourDrive(t, alt);
                            }
                            else
                            {
                                // fallback: try lateral clear space (lane change)
                                var lateral = _owner.TryFindLateralClearSpace(t, closestHit, _owner.SIDE_NUDGE_OFFSET);
                                if (lateral != Vector3.Zero) SafeIssueDetourDrive(t, lateral);
                            }
                        }
                    }
                }

                // 4) Flanking behavior: if we detect slow moving target ahead and we can overtake via side route, schedule flank
                if (!t.IsFlanking && now - t.FlankStartMs > 6000 && t.State == BehaviorState.Driving)
                {
                    var flank = ComputeFlankOpportunity(t);
                    if (flank != Vector3.Zero)
                    {
                        t.IsFlanking = true;
                        t.FlankTarget = flank;
                        t.FlankStartMs = now;
                        SafeIssueDetourDrive(t, flank, drivingStyle: VehicleDelivery.SMOOTH_AGGRESSIVE_DRIVING_STYLE);
                    }
                }

                // 5) Small housekeeping: if we completed flank or detour, clear flags
                if (t.IsFlanking && _owner.SafeExists(t.SpawnedVehicle) && Vector3.Distance(t.SpawnedVehicle.Position, t.FlankTarget) < 4.5f)
                {
                    t.IsFlanking = false;
                    t.FlankTarget = Vector3.Zero;
                }
            }
            catch { /* swallow to avoid mod crash */ }
        }

        private void EvaluatePursuitPrediction(DeliveryTask t, int now)
        {
            try
            {
                // sample player velocity & position
                var player = Game.Player.Character;
                if (player == null || !player.Exists()) { t.PursuitConfidence = 0f; return; }

                Vector3 playerPos = player.Position;
                Vector3 playerVel = player.Velocity;

                // simple linear prediction
                Vector3 predicted = playerPos + playerVel * _owner.PURSUIT_PREDICT_SEC;
                t.LastPredictedPlayerPos = predicted;
                t.LastPursuitEvalMs = now;

                float distToPred = Vector3.Distance(t.SpawnedVehicle.Position, predicted);
                float distToPlayer = Vector3.Distance(t.SpawnedVehicle.Position, playerPos);

                // basic heuristics: if vehicle near predicted intercept path and heading roughly towards player -> raise confidence
                float headingDot = Vector3.Dot(_owner.Normalize2D(t.SpawnedVehicle.ForwardVector), _owner.Normalize2D(playerPos - t.SpawnedVehicle.Position));
                float speedDiff = Math.Abs(t.SpawnedVehicle.Speed - playerVel.Length());

                // compute confidence (0..1)
                float conf = 0f;
                if (distToPred < _owner.ROADBLOCK_SPAWN_RADIUS) conf += 0.4f;
                if (distToPlayer < _owner.ROADBLOCK_SPAWN_RADIUS * 1.2f) conf += 0.2f;
                if (headingDot > 0.6f) conf += 0.2f;
                if (speedDiff < 12f) conf += 0.2f;

                t.PursuitConfidence = Math.Max(0f, Math.Min(1f, conf));
            }
            catch { t.PursuitConfidence = 0f; }
        }

        private bool TryAttemptPIT(DeliveryTask t, int now)
        {
            try
            {
                // conditions for PIT: moderate speed, side approach possible, random chance
                if (t == null || t.DriverPed == null || !_owner.SafeExists(t.DriverPed)) return false;
                var player = Game.Player.Character;
                if (player == null || !player.Exists()) return false;

                float playerSpeed = player.Speed;
                float ourSpeed = t.SpawnedVehicle.Speed;

                if (playerSpeed < _owner.PIT_MIN_SPEED) return false;
                if (ourSpeed < _owner.PIT_MIN_SPEED) return false;
                if (ourSpeed - playerSpeed < _owner.PIT_MIN_REL_SPEED) return false;
                if (_rng.Next(100) >= _owner.PIT_CHANCE_PERCENT) return false; // probability guard

                // attempt: issue short lateral intercept run + task temp action to bump
                Vector3 interceptPoint = ComputeSideInterceptPoint(t, player);
                if (interceptPoint == Vector3.Zero) return false;

                // issue high-priority drive to intercept point and then small ram nudge
                try
                {
                    Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, t.DriverPed.Handle);
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, interceptPoint.X, interceptPoint.Y, interceptPoint.Z, Math.Max(18.0f, t.SpawnedVehicle.Speed + 3f), 0, (int)t.ModelHash, VehicleDelivery.AGGRESSIVE_DRIVING_STYLE, 0.6f);
                    // schedule a nudge shortly after arrival attempt
                    DelayThenLocal(() =>
                    {
                        try
                        {
                            if (!_owner.SafeExists(t.SpawnedVehicle)) return;
                            // small lateral set-velocity to simulate PIT contact
                            Vector3 lateral = new Vector3(-t.SpawnedVehicle.ForwardVector.Y, t.SpawnedVehicle.ForwardVector.X, 0f);
                            _owner.NudgeVehicle(t, lateral, 2.4f, 160);
                        }
                        catch { }
                    }, 700);
                }
                catch { }

                t.HasAttemptedPIT = true;
                t.LastPITAttemptMs = now;
                return true;
            }
            catch { return false; }
        }

        // Compute a side intercept point near player's future position
        private Vector3 ComputeSideInterceptPoint(DeliveryTask t, Ped player)
        {
            try
            {
                Vector3 pred = player.Position + player.Velocity * _owner.PURSUIT_PREDICT_SEC;
                // pick a lateral offset to hit side of player's vehicle path
                Vector3 toPred = _owner.Normalize2D(pred - t.SpawnedVehicle.Position);
                if (toPred == Vector3.Zero) toPred = _owner.Normalize2D(player.Position - t.SpawnedVehicle.Position);
                Vector3 perp = new Vector3(-toPred.Y, toPred.X, 0f);

                // try right side then left side
                float[] offsets = new float[] { 4.2f, -4.2f };
                foreach (var o in offsets)
                {
                    Vector3 cand = pred + perp * o;
                    var onStreet = World.GetNextPositionOnStreet(cand);
                    if (onStreet != Vector3.Zero && Math.Abs(onStreet.Z - t.SpawnedVehicle.Position.Z) < 2.5f)
                        return onStreet;
                }
                return Vector3.Zero;
            }
            catch { return Vector3.Zero; }
        }

        // Safe small wrapper to call owner.DelayThen without exposing internal delegate scoping confusion
        private void DelayThenLocal(Action action, int delayMs)
        {
            _owner.DelayThen(action, delayMs);
        }

        // Issue a detour drive in a safe manner
        private void SafeIssueDetourDrive(DeliveryTask t, Vector3 waypoint, int drivingStyle = -1)
        {
            try
            {
                if (t.DriverPed == null || !_owner.SafeExists(t.DriverPed)) return;
                if (!_owner.SafeExists(t.SpawnedVehicle)) return;

                float desired = Math.Max(12.0f, Math.Min(40.0f, Vector3.Distance(t.SpawnedVehicle.Position, waypoint) * 0.6f + 10f));
                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, t.DriverPed.Handle);
                if (drivingStyle < 0) drivingStyle = VehicleDelivery.SMOOTH_AGGRESSIVE_DRIVING_STYLE;
                try
                {
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, t.DriverPed.Handle, t.SpawnedVehicle.Handle, waypoint.X, waypoint.Y, waypoint.Z, desired, 0, (int)t.ModelHash, drivingStyle, 2.2f);
                }
                catch
                {
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD, t.DriverPed.Handle, t.SpawnedVehicle.Handle, waypoint.X, waypoint.Y, waypoint.Z, desired, 0, (int)t.ModelHash, drivingStyle, 2.2f);
                }

                _owner.SmoothSetCruiseSpeed(t, desired, 0.16f);
            }
            catch { }
        }

        // Compute simple flank opportunity: find a nearby street point slightly ahead but offset laterally
        private Vector3 ComputeFlankOpportunity(DeliveryTask t)
        {
            try
            {
                if (t == null || !_owner.SafeExists(t.SpawnedVehicle)) return Vector3.Zero;
                var v = t.SpawnedVehicle;
                Vector3 forward = _owner.Normalize2D(v.ForwardVector);
                if (forward == Vector3.Zero) return Vector3.Zero;
                Vector3 perp = new Vector3(-forward.Y, forward.X, 0f);
                float look = 12f + (float)_rng.NextDouble() * 8f;
                float side = 5f + (float)_rng.NextDouble() * 6f;
                Vector3 cand = v.Position + forward * look + perp * (_rng.Next(2) == 0 ? side : -side);
                Vector3 onStreet = World.GetNextPositionOnStreet(cand);
                if (onStreet == Vector3.Zero) return Vector3.Zero;
                return onStreet;
            }
            catch { return Vector3.Zero; }
        }
    }

    // --- small helper: normalize 2D vector safely
    private Vector3 Normalize2D(Vector3 v)
    {
        var d = new Vector3(v.X, v.Y, 0f);
        float len = (float)Math.Sqrt(d.X * d.X + d.Y * d.Y);
        if (len <= 0.0001f) return Vector3.Zero;
        return new Vector3(d.X / len, d.Y / len, 0f);
    }
}