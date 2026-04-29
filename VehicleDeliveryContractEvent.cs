using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Globalization;

public class VehicleDeliveryContractEvent : Script
{
    private enum EventState
    {
        Idle,
        MarkerAvailable,
        FadingOut,
        MissionActive
    }

    private sealed class VehicleSpec
    {
        public uint ModelHash;
        public string DisplayName;

        public VehicleSpec(uint modelHash, string displayName)
        {
            ModelHash = modelHash;
            DisplayName = displayName;
        }
    }

    private const int RollIntervalMs = 420000;           // 7 phút roll 1 lần
    private const int SpawnChancePercent = 13;          // 13% kích hoạt sự kiện
    private const int GameReadyDelayMs = 3000;
    private const int FadeOutMs = 800;                   // hiệu ứng tắt màn hình
    private const int PostFadeDelayMs = 900;            // hiệu ứng mở màn hình
    private const int MarkerAutoCancelMs = 200000;       // 200 giây kết thúc
    private const int MissionTimeoutMs = 330000;         // sự kiện 5 phút 30 giây
    private const int RewardMoneyMin = 500000;
    private const int RewardMoneyMax = 530000;
    private const int RewardMoneyReference = 529704; // mốc gốc để scale các phép tính khác
    private const int RewardPenaltyPerDamagePercentReference = 27196; // mốc gốc theo RewardMoneyReference

    private const int WantedStarsOnTimeout = 1;          // số sao truy nã
    private const int WantedDeliveryWindowMs = 120000;   // thời gian ân hạn 2 phút
    private const int WantedWarningBeforeMs = 20000;     // cảnh báo trước truy nã 20 giây
    private const int MissionVehicleForcedDeleteDelayMs = 60000; // 60 giây xóa xe
    private const int ContractFailPenaltyDelayMs = 5000;
    private const int ContractFailDamageThresholdPercent = 30;   // 30% hư sẽ kết thúc
    private const int ContractFailPenaltyMaxMoney = 5000000;   // số tiền đền bù tối đa

    private static readonly Vector3 MarkerPosition = new Vector3(-28.426650f, -1085.516000f, 26.566130f);
    private static readonly Vector3 PostCutscenePlayerPosition = new Vector3(-25.922160f, -1081.612000f, 26.630220f);
    private static readonly Vector3 MissionVehicleSpawnPosition = new Vector3(-8.652727f, -1082.264000f, 26.191780f);
    private const float MissionVehicleSpawnHeading = 120.0132f;

    private const float PickupInteractRadius = 2.0f;
    private const float DestinationCompleteRadius = 4.0f;

    private static readonly Vector3[] DestinationPoints =
    {
        new Vector3(-58.428600f, 6533.193000f, 31.490840f),
        new Vector3(-1431.920000f, -199.432900f, 47.403380f),
        new Vector3(612.141100f, 2724.557000f, 41.868520f),
        new Vector3(2575.316000f, 313.474800f, 108.457900f),
        new Vector3(856.914400f, -2120.468000f, 30.663230f),
        new Vector3(-1150.903000f, 2676.246000f, 18.093890f),
        new Vector3(2153.489000f, 4796.473000f, 41.183230f),
        new Vector3(912.767900f, 53.009880f, 80.764800f),
        new Vector3(-2194.572000f, 4268.384000f, 48.558200f),
        new Vector3(905.037100f, -10.634650f, 78.764640f),
        new Vector3(895.386700f, -4.536160f, 78.764860f),
        new Vector3(872.964100f, -46.079950f, 78.764370f),
        new Vector3(24.272250f, 3685.475000f, 39.619430f),
        new Vector3(3321.321000f, 5159.794000f, 18.420570f),
        new Vector3(3694.442000f, 4563.420000f, 25.205290f),
        new Vector3(-1138.135000f, -1992.937000f, 13.166810f),
        new Vector3(-3050.916000f, 596.416400f, 7.477588f),
        new Vector3(1471.977000f, 6559.794000f, 13.249540f),
        new Vector3(-1891.012000f, 2034.456000f, 140.739400f),
        new Vector3(-1921.965000f, 2048.662000f, 140.734900f),
        new Vector3(-1887.382000f, 2033.546000f, 140.736700f),
        new Vector3(-324.660100f, -2595.340000f, 6.000295f),
        new Vector3(-2326.185000f, 289.130700f, 169.467000f),
        new Vector3(-2331.420000f, 300.360800f, 169.467000f),
        new Vector3(-2339.705000f, 293.093400f, 169.467000f),
        new Vector3(-2349.194000f, 285.607700f, 169.467200f),
        new Vector3(-54.332120f, 1904.546000f, 195.361300f),
        new Vector3(1244.062000f, -578.194700f, 69.328290f),
        new Vector3(706.467300f, 609.733300f, 128.911100f),
        new Vector3(642.296600f, 587.920200f, 128.910700f),
        new Vector3(699.276400f, 664.022500f, 128.911100f),
        new Vector3(849.619600f, 512.768800f, 125.919300f),
        new Vector3(791.091100f, 201.218300f, 81.437260f),
        new Vector3(743.233300f, 135.632000f, 80.344670f),
        new Vector3(732.257800f, 204.027100f, 86.103880f),
        new Vector3(625.889600f, 265.608100f, 103.089400f),
        new Vector3(456.236000f, 257.197800f, 103.209100f),
        new Vector3(389.156300f, 182.338600f, 103.116900f),
        new Vector3(308.856900f, 175.239700f, 103.964000f),
        new Vector3(117.369700f, 39.996100f, 73.520240f),
        new Vector3(168.411800f, -63.042810f, 68.358260f),
        new Vector3(135.017100f, -204.997400f, 54.458930f),
        new Vector3(-284.431500f, 2534.790000f, 74.669070f),
        new Vector3(-1890.739000f, 2046.195000f, 140.861000f),
        new Vector3(734.192100f, 4192.590000f, 40.718360f),
        new Vector3(1152.296000f, 2097.106000f, 55.896760f),
        new Vector3(231.480000f, 341.509700f, 105.535900f),
        new Vector3(1535.238000f, -2176.706000f, 77.378970f),
        new Vector3(-1175.097000f, -1766.813000f, 3.921362f),
        new Vector3(1765.996000f, -1652.418000f, 112.660300f),
        new Vector3(-61.176950f, -2023.459000f, 18.016970f),
        new Vector3(-49.088180f, -2004.528000f, 18.016960f),
        new Vector3(-465.946700f, -767.494400f, 35.381560f),
        new Vector3(-446.314600f, -774.989400f, 40.200140f),
        new Vector3(-458.053700f, -777.734600f, 45.018650f),
        new Vector3(-477.675700f, -754.546400f, 45.019470f),
        new Vector3(-484.367500f, -732.042200f, 33.211950f),
        new Vector3(808.169600f, 1272.247000f, 360.483500f),
        new Vector3(2748.692000f, 1446.760000f, 24.489020f),
        new Vector3(2408.281000f, 4996.048000f, 46.605310f),
        new Vector3(-1773.153000f, 1878.643000f, 151.339300f),
        new Vector3(-291.695300f, 1454.894000f, 337.297800f),
        new Vector3(183.739700f, -2227.176000f, 5.951474f),
        new Vector3(773.897400f, -1318.276000f, 26.225350f),
        new Vector3(403.056800f, -204.732000f, 59.096870f),
        new Vector3(318.833300f, -274.080700f, 53.940210f),
        new Vector3(-924.856300f, 403.887100f, 79.126520f),
        new Vector3(-928.853900f, 402.928400f, 79.126500f),
        new Vector3(-966.685300f, 449.929200f, 79.809010f),
        new Vector3(-975.344400f, 525.952600f, 81.471280f),
        new Vector3(-1096.121000f, 439.940300f, 75.286210f),
        new Vector3(-1202.352000f, 320.094400f, 70.839780f),
        new Vector3(-1204.849000f, 265.515700f, 69.533760f),
        new Vector3(-1301.163000f, 251.791800f, 62.590130f),
        new Vector3(-1316.367000f, 249.031900f, 61.851050f),
        new Vector3(-1285.919000f, 295.604800f, 64.879340f),
        new Vector3(-2033.155000f, -357.536600f, 48.106300f),
        new Vector3(-1972.912000f, -298.262000f, 48.106140f),
        new Vector3(-2020.688000f, -363.800400f, 44.106040f),
        new Vector3(-1320.148000f, 280.896700f, 63.830970f),
    };

    private static readonly VehicleSpec[] MissionVehicles =
    {
        new VehicleSpec(0xD8A914D3u, "Banshee GTS"),
        new VehicleSpec(0x4BFCF28Bu, "Bestia GTS"),
        new VehicleSpec(0x25C5AF13u, "Banshee 900R"),
        new VehicleSpec(0x58F77553u, "BR8"),
        new VehicleSpec(0xF9300CC5u, "Bati 801"),
        new VehicleSpec(0xCADD5D2Du, "Bati 801RR"),
        new VehicleSpec(0xB1D95DA0u, "Cheetah"),
        new VehicleSpec(0xC972A155u, "Champion"),
        new VehicleSpec(0x52FF9437u, "Cyclone"),
        new VehicleSpec(0x00ABB0C0u, "Carbon RS"),
        new VehicleSpec(0x79C12D73u, "Coquette D10 Pursuit"),
        new VehicleSpec(0x1ABA13B5u, "Cruiser"),
        new VehicleSpec(0xF1B44F44u, "Diabolus"),
        new VehicleSpec(0x4EE74355u, "Emerus"),
        new VehicleSpec(0xC3F57329u, "FMJ MK V"),
        new VehicleSpec(0x5502626Cu, "FMJ"),
        new VehicleSpec(0x93F09558u, "Future Shock Deathbike"),
        new VehicleSpec(0x817AFAADu, "Gauntlet"),
        new VehicleSpec(0x2C2C2324u, "Gargoyle"),
        new VehicleSpec(0x4992196Cu, "GP1"),
        new VehicleSpec(0x4B6C568Au, "Hakuchou"),
        new VehicleSpec(0xF0C2A91Fu, "Hakuchou Drag"),
        new VehicleSpec(0xA9EC907Bu, "Ignus"),
        new VehicleSpec(0x18F25AC7u, "Infernus"),
        new VehicleSpec(0x85E8E76Bu, "Itali GTB"),
        new VehicleSpec(0xE33A477Bu, "Itali GTB2"),
        new VehicleSpec(0xD86A0247u, "Krieger"),
        new VehicleSpec(0xFF5968CDu, "LM87"),
        new VehicleSpec(0xB6846A55u, "LE-7B"),
        new VehicleSpec(0x26321E67u, "Lectro"),
        new VehicleSpec(0x1CBDC10Bu, "Lynx"),
        new VehicleSpec(0xC8163646u, "Luiva"),
        new VehicleSpec(0xC7E55211u, "Locust"),
        new VehicleSpec(0xDA5819A3u, "Massacro"),
        new VehicleSpec(0x70241EEAu, "Niobe"),
        new VehicleSpec(0x9F6ED5A2u, "Neo"),
        new VehicleSpec(0x91CA96EEu, "Neon"),
        new VehicleSpec(0xDA288376u, "Nemesis"),
        new VehicleSpec(0x3DA47243u, "Nero"),
        new VehicleSpec(0x4131F378u, "Nero Custom"),
        new VehicleSpec(0xA0438767u, "Nightblade"),
        new VehicleSpec(0x34B82784u, "Oppressor"),
        new VehicleSpec(0x767164D6u, "Osiris"),
        new VehicleSpec(0xE550775Bu, "Paragon R"),
        new VehicleSpec(0x546D8EEEu, "Paragon R (Armored)"),
        new VehicleSpec(0x1446590Au, "PR4"),
        new VehicleSpec(0x10635A0Eu, "10F Widebody"),
        new VehicleSpec(0x33B98FE2u, "Pariah"),
        new VehicleSpec(0xF2AE3F81u, "Pipistrello"),
        new VehicleSpec(0xFDEFAEC3u, "Police Bike"),
        new VehicleSpec(0xAD5E30D7u, "Powersurge"),
        new VehicleSpec(0xA4D99B7Du, "Raiden"),
        new VehicleSpec(0x679450AFu, "Rapid GT"),
        new VehicleSpec(0x68FB5379u, "Rapid GT X"),
        new VehicleSpec(0xD7C56D39u, "Raptor"),
        new VehicleSpec(0x0DF381E5u, "Reaper"),
        new VehicleSpec(0x76D7C404u, "Reever"),
        new VehicleSpec(0xCABD11E8u, "Ruffian"),
        new VehicleSpec(0x5097F589u, "SC1"),
        new VehicleSpec(0x2EF89E46u, "Sanchez"),
        new VehicleSpec(0xA960B13Eu, "Sanchez"),
        new VehicleSpec(0x58E316C7u, "Sanctus"),
        new VehicleSpec(0xD37B7976u, "Schwartzer"),
        new VehicleSpec(0xD9F0503Du, "Scramjet"),
        new VehicleSpec(0x97398A4Bu, "Seven-70"),
        new VehicleSpec(0x50A6FB9Cu, "Shinobi"),
        new VehicleSpec(0xE7D2A16Eu, "Shotaro"),
        new VehicleSpec(0x2E3967B0u, "SM722"),
        new VehicleSpec(0x2C509634u, "Sovereign"),
        new VehicleSpec(0x400F5147u, "Specter"),
        new VehicleSpec(0x34DBA661u, "Stromberg"),
        new VehicleSpec(0x11F58A5Au, "Stryder"),
        new VehicleSpec(0xEE6024BCu, "Sultan RS"),
        new VehicleSpec(0x28FC5B78u, "Suzume"),
        new VehicleSpec(0xBC5DC07Eu, "Taipan"),
        new VehicleSpec(0x6322B39Au, "T20"),
        new VehicleSpec(0x1044926Fu, "Tempesta"),
        new VehicleSpec(0x3D7C6410u, "Tezeract"),
        new VehicleSpec(0x3E3D1F59u, "Thrax"),
        new VehicleSpec(0x6D6F8F43u, "Thrust"),
        new VehicleSpec(0xAF0B8D48u, "Tigon"),
        new VehicleSpec(0xA31CB573u, "Tornado RR"),
        new VehicleSpec(0x56C8A5EFu, "Toreador"),
        new VehicleSpec(0xF62446BAu, "Torero XO"),
        new VehicleSpec(0xF8AB457Bu, "Turismo Omaggio"),
        new VehicleSpec(0x185484E1u, "Turismo R"),
        new VehicleSpec(0x7B406EFBu, "Tyrus"),
        new VehicleSpec(0xE99011C2u, "Tyrant"),
        new VehicleSpec(0x142E0DC3u, "Vacca"),
        new VehicleSpec(0xF79A00F7u, "Vader"),
        new VehicleSpec(0x7397224Cu, "Vagner"),
        new VehicleSpec(0xCCE5C8FAu, "Veto Classic"),
        new VehicleSpec(0xA703E4A9u, "Veto Modern"),
        new VehicleSpec(0xAF599F01u, "Vindicator"),
        new VehicleSpec(0xC4810400u, "Visione"),
        new VehicleSpec(0x7E8F677Fu, "X80 Proto"),
        new VehicleSpec(0x36B4A8A9u, "XA-21"),
        new VehicleSpec(0x95F6A2C9u, "X-treme"),
        new VehicleSpec(0x2714AA93u, "Zeno"),
        new VehicleSpec(0xAC5DF515u, "Zentorno"),
        new VehicleSpec(0xDE05FB87u, "Zombie Chopper"),
        new VehicleSpec(0xD757D97Du, "Zorrusso"),
    };

    private static readonly string[] ContactNames =
    {
        Language.Get("ContactName_LesterDeadthwish", "Lester Deadthwish"),
        Language.Get("ContactName_Athur", "Athur"),
        Language.Get("ContactName_Josh", "Josh"),
        Language.Get("ContactName_MpFmContact", "MpFmContact")
    };

    private static readonly NotificationIcon[] FeedIcons =
    {
        NotificationIcon.LesterDeathwish,
        NotificationIcon.MpFmContact,
        NotificationIcon.Arthur,
        NotificationIcon.Josh
    };

    private readonly Random _rng = new Random();

    private bool _modReady;
    private int _gameReadySince = -1;

    private int _missionBaseReward;

    private bool _contractFailPenaltyPending;
    private int _contractFailPenaltyAt = -1;

    private int _missionVehicleCleanupAt = -1;

    private int _nextRollTime;
    private int _lastProximityCheckTime;
    private int _stateSince;

    private EventState _state = EventState.Idle;
    private bool _playerNearMarkerCached;
    private bool _missionAcceptedNotificationShown;
    private bool _wantedPhaseActive;
    private bool _wantedWarningShown;   // thêm cờ để chỉ hiện subtitle 1 lần

    private int _wantedDeliveryDeadlineTime = -1;

    private const double ContractFailPenaltyNormalRate = 0.18d;
    private const double ContractFailPenaltyWithInsuranceRate = 0.05d;

    private VehicleSpec _selectedVehicleSpec;
    private Vector3 _selectedDestination = Vector3.Zero;
    private string _selectedContactName = string.Empty;

    private Vehicle _missionVehicle;
    private Blip _pickupBlip;
    private Blip _destinationBlip;

    public VehicleDeliveryContractEvent()
    {
        Tick += OnTick;
        Interval = 16;   // 16ms ~ 60FPS
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (Game.IsLoading)
        {
            ResetRuntimeState();
            return;
        }

        if (!_modReady)
        {
            if (_gameReadySince < 0)
                _gameReadySince = Game.GameTime;

            if (Game.GameTime - _gameReadySince < GameReadyDelayMs)
                return;

            _modReady = true;
            _nextRollTime = Game.GameTime + RollIntervalMs;
        }

        UpdateDeferredMissionVehicleCleanup();
        UpdatePendingContractFailurePenalty();

        switch (_state)
        {
            case EventState.Idle:
                UpdateIdleState();
                break;

            case EventState.MarkerAvailable:
                UpdateMarkerState();
                break;

            case EventState.FadingOut:
                UpdateFadingOutState();
                break;

            case EventState.MissionActive:
                UpdateMissionState();
                break;
        }
    }

    private static string LT(string key, string fallback, params string[] tokensAndValues)
    {
        return Language.ReplaceTokens(Language.Get(key, fallback), tokensAndValues);
    }

    private void UpdateIdleState()
    {
        if (Game.GameTime < _nextRollTime)
            return;

        _nextRollTime = Game.GameTime + RollIntervalMs;

        // Có event khác đang chiếm slot thì không roll thêm
        if (DeliveryContractBridge.CurrentKind != DeliveryContractMissionKind.None)
            return;

        // Nếu trước đó đã roll ra máy bay nhưng plane script chưa kịp nhận thì không roll lại
        if (DeliveryContractBridge.PlanePendingStart || DeliveryContractBridge.SubmarinePendingStart)
            return;

        if (_rng.Next(100) >= SpawnChancePercent)
            return;

        // Roll tầng 2: 70% xe, 20% máy bay, 10% tàu thuyền
        int kindRoll = _rng.Next(100);
        if (kindRoll < 70)
        {
            ShowMarkerEvent();
            return;
        }

        if (kindRoll < 90)
        {
            PlaneDeliveryContractEvent.RequestSpawn();
            return;
        }

        SubmarineDeliveryContractEvent.RequestSpawn();
    }

    private bool IsMissionVehicleFailed(Vehicle vehicle)
    {
        if (vehicle == null || !vehicle.Exists())
            return true;

        if (vehicle.IsDead || vehicle.Health <= 0)
            return true;

        return GetVehicleDamagePercent(vehicle) >= ContractFailDamageThresholdPercent;
    }

    private void TriggerContractFailurePenaltyAndEndMission()
    {
        if (_state != EventState.MissionActive)
            return;

        ShowHelpBox(LT(
               "ContractFailMessage",
               "~HUD_COLOUR_ORANGELIGHT~Khách hàng không còn nhu cầu nhận xe nữa và yêu cầu bạn phải đền bù cho chiếc xe bạn đã làm hỏng đó. Lần sau nhớ chú ý nhé!"
        ), 3000);

        _contractFailPenaltyPending = true;
        _contractFailPenaltyAt = Game.GameTime + ContractFailPenaltyDelayMs;

        CleanupAllAndReturnToIdle(true, true);
    }

    private void UpdatePendingContractFailurePenalty()
    {
        if (!_contractFailPenaltyPending || _contractFailPenaltyAt < 0)
            return;

        if (Game.GameTime < _contractFailPenaltyAt)
            return;

        _contractFailPenaltyPending = false;
        _contractFailPenaltyAt = -1;

        bool useInsurance = false;
        try
        {
            useInsurance = MissionInsuranceTicketStore.TryConsumeTicket();
        }
        catch
        {
            useInsurance = false;
        }

        double rate = useInsurance
            ? ContractFailPenaltyWithInsuranceRate   // 0.05d
            : ContractFailPenaltyNormalRate;         // 0.18d

        try
        {
            int currentMoney = Math.Max(0, Game.Player.Money);

            int penalty = (int)Math.Round(currentMoney * rate, MidpointRounding.AwayFromZero);
            penalty = Math.Min(penalty, ContractFailPenaltyMaxMoney); // 5,000,000
            penalty = Math.Min(penalty, currentMoney);

            if (penalty > 0)
                Game.Player.Money = currentMoney - penalty;
        }
        catch
        {
        }
    }

    private void ShowMarkerEvent()
    {
        CleanupPickupObjects();
        ResetDeliveryFlowFlags();

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.Vehicle;

        _state = EventState.MarkerAvailable;
        _stateSince = Game.GameTime;
        _lastProximityCheckTime = 0;
        _playerNearMarkerCached = false;
        _missionAcceptedNotificationShown = false;

        CreatePickupBlip();

        Notification.Show(LT(
            "MarkerEventNotification",
            "~y~~h~Đang có yêu cầu giao hàng này! Bạn có muốn kiếm thêm thu nhập không?"
        ));
    }

    private void UpdateMarkerState()
    {
        if (Game.GameTime - _stateSince >= MarkerAutoCancelMs)
        {
            CancelMarkerEvent();
            return;
        }

        DrawPickupMarker();

        if (Game.GameTime - _lastProximityCheckTime >= 500)
        {
            _lastProximityCheckTime = Game.GameTime;
            _playerNearMarkerCached = IsPlayerNearPickupMarker();
        }

        if (_playerNearMarkerCached)
        {
            ShowHelpBox(LT(
                "MarkerAcceptPrompt",
                "~HUD_COLOUR_YOGA~Bạn có muốn nhận nhiệm vụ giao xe này không?"
            ));
            HandleMarkerInput();
        }
        else
        {
            HelpBoxBridge.ClearPersistent();
        }
    }

    private void HandleMarkerInput()
    {
        if (!IsPlayerNearPickupMarker())
            return;

        if (Game.IsKeyPressed(Keys.Enter))
        {
            StartMissionCutscene();
            return;
        }

        if (Game.IsKeyPressed(Keys.Back))
        {
            CancelMarkerEvent();
            return;
        }
    }

    private void StartMissionCutscene()
    {
        if (_state != EventState.MarkerAvailable)
            return;

        _missionBaseReward = RollMissionBaseReward(); // NEW

        ResetDeliveryFlowFlags();

        _state = EventState.FadingOut;
        _stateSince = Game.GameTime;

        DeletePickupBlip();
        GTA.UI.Screen.FadeOut(FadeOutMs);
    }

    private int RollMissionBaseReward()
    {
        return _rng.Next(RewardMoneyMin, RewardMoneyMax + 1); // max inclusive
    }

    private int GetScaledPenaltyPerDamagePercent(int baseReward)
    {
        if (baseReward <= 0)
            baseReward = RewardMoneyReference;

        return (int)Math.Round(
            RewardPenaltyPerDamagePercentReference * (baseReward / (double)RewardMoneyReference),
            MidpointRounding.AwayFromZero
        );
    }

    private void UpdateFadingOutState()
    {
        if (Game.GameTime - _stateSince < FadeOutMs + PostFadeDelayMs)
            return;

        PositionPlayerAfterCutscene();

        if (!SpawnMission())
        {
            GTA.UI.Screen.FadeIn(FadeOutMs);
            CleanupAllAndReturnToIdle(true, false);
            return;
        }

        GTA.UI.Screen.FadeIn(FadeOutMs);

        _state = EventState.MissionActive;
        _stateSince = Game.GameTime;
        _wantedPhaseActive = false;
        _wantedWarningShown = false;  // reset mỗi lần vào mission
        _wantedDeliveryDeadlineTime = -1;
        _missionAcceptedNotificationShown = false;

        ShowMissionAcceptedMessage();
    }

    private void PositionPlayerAfterCutscene()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player != null && player.Exists())
            {
                player.Task.ClearAllImmediately();
                player.Position = PostCutscenePlayerPosition;
                player.Heading = MissionVehicleSpawnHeading;
            }
        }
        catch
        {
        }
    }

    private bool SpawnMission()
    {
        _missionVehicleCleanupAt = -1;

        _selectedVehicleSpec = GetRandomVehicleSpec();
        if (_selectedVehicleSpec == null)
            return false;

        _selectedDestination = DestinationPoints[_rng.Next(DestinationPoints.Length)];
        _selectedContactName = ContactNames[_rng.Next(ContactNames.Length)];

        Model vehicleModel = new Model((int)_selectedVehicleSpec.ModelHash);
        if (!vehicleModel.IsValid || !vehicleModel.IsInCdImage)
            return false;

        RequestModel(vehicleModel);
        if (!vehicleModel.IsLoaded)
            return false;

        try
        {
            _missionVehicle = World.CreateVehicle(vehicleModel, MissionVehicleSpawnPosition, MissionVehicleSpawnHeading);
        }
        catch
        {
            _missionVehicle = null;
        }

        if (_missionVehicle == null || !_missionVehicle.Exists())
        {
            _missionVehicle = null;
            return false;
        }

        try
        {
            _missionVehicle.IsPersistent = true;
            _missionVehicle.IsInvincible = false;
            _missionVehicle.CanBeVisiblyDamaged = true;
            _missionVehicle.DirtLevel = 0f;
            _missionVehicle.NeedsToBeHotwired = false;
            _missionVehicle.PlaceOnGround();

            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _missionVehicle.Handle, true, true);
            Function.Call(Hash.SET_VEHICLE_ENGINE_ON, _missionVehicle.Handle, true, true, false);
            Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _missionVehicle.Handle, 1);
            Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED_FOR_ALL_PLAYERS, _missionVehicle.Handle, false);
            Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, _missionVehicle.Handle);

            _missionVehicle.LockStatus = VehicleLockStatus.Unlocked;
        }
        catch
        {
        }

        CreateDestinationBlip();
        return true;
    }

    private void ShowMissionAcceptedMessage()
    {
        if (_missionAcceptedNotificationShown)
            return;

        _missionAcceptedNotificationShown = true;

        string sender = GetSelectedContactName();
        string subject = LT("MissionAcceptedSubject", "Giao xe");
        string body = BuildMissionBodyMessage();

        ShowFeedMessage(sender, subject, body);
    }

    private string BuildMissionBodyMessage()
    {
        string vehicleName = GetSelectedVehicleDisplayName();

        return LT(
            "MissionBodyMessage",
            "Tôi đã mua chiếc ~HUD_COLOUR_DEGEN_CYAN~{VEHICLE}~s~ này rồi. Hiện tại tôi đang cần gấp nhưng không đến cửa hàng được. Bạn có thể giao đến địa điểm tôi hẹn không? Nhưng tôi báo trước, do xe đã thanh toán rồi nên nếu bạn làm hư nó thì phải đền bù cho tôi đấy. Chúc may mắn!!",
            "{VEHICLE}", vehicleName
        );
    }

    private string GetSelectedVehicleDisplayName()
    {
        if (_selectedVehicleSpec == null || string.IsNullOrWhiteSpace(_selectedVehicleSpec.DisplayName))
            return Language.Get("DefaultVehicleName", "Phương tiện");

        return _selectedVehicleSpec.DisplayName.Trim();
    }

    private string GetSelectedContactName()
    {
        if (string.IsNullOrWhiteSpace(_selectedContactName))
            return Language.Get("DefaultContactName", "CHAR_MP_FM_CONTACT");

        return _selectedContactName;
    }

    private void UpdateMissionState()
    {
        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
        {
            CleanupAllAndReturnToIdle(true, true);
            return;
        }

        if (_missionVehicle == null || !_missionVehicle.Exists())
        {
            CleanupAllAndReturnToIdle(true, true);
            return;
        }

        if (IsMissionVehicleFailed(_missionVehicle))
        {
            TriggerContractFailurePenaltyAndEndMission();
            return;
        }

        DrawDestinationMarker();

        bool nearDestination = IsVehicleNearDestination(_missionVehicle, _selectedDestination);
        int damagePercent = GetVehicleDamagePercent(_missionVehicle);

        if (nearDestination)
        {
            if (damagePercent == 0)
            {
                int payout = CalculateDeliveryReward(0);
                CompleteMission(payout);
                return;
            }

            if (damagePercent <= 10)
            {
                int payout = CalculateDeliveryReward(damagePercent);
                ShowDeliveryConfirmHelpBox(payout);

                if (Game.IsKeyPressed(Keys.Enter))
                {
                    CompleteMission(payout);
                    return;
                }

                if (Game.IsKeyPressed(Keys.Back))
                {
                    CompleteMissionWithoutReward();
                    return;
                }

                return;
            }

            ShowDeliveryConditionWarning();
            return;
        }
        else
        {
            HelpBoxBridge.ClearPersistent();
        }

        int elapsedMs = Game.GameTime - _stateSince;
        int remainingMsBeforeWanted = MissionTimeoutMs - elapsedMs;

        if (!_wantedPhaseActive)
        {
            // hiện subtitle đúng 20 giây trước khi vào truy nã
            if (!_wantedWarningShown && remainingMsBeforeWanted <= WantedWarningBeforeMs && remainingMsBeforeWanted > 0)
            {
                _wantedWarningShown = true;
                ShowWantedWarningSubtitle();
            }

            if (elapsedMs >= MissionTimeoutMs)
            {
                StartWantedPhase();
            }
        }

        if (_wantedPhaseActive &&
            _wantedDeliveryDeadlineTime > 0 &&
            Game.GameTime >= _wantedDeliveryDeadlineTime)
        {
            CleanupAllAndReturnToIdle(true, true);
            return;
        }

        if (_missionVehicle.IsDead || _missionVehicle.Health <= 0)
        {
            CleanupAllAndReturnToIdle(true, true);
            return;
        }
    }

    private void ShowWantedWarningSubtitle()
    {
        GTA.UI.Screen.ShowSubtitle(
            LT("WantedWarningSubtitle", "Chiếc xe này hiện đang là ~HUD_COLOUR_REDLIGHT~xe lậu~s~, hãy cẩn thận cảnh sát!!!"),
            5000
        );
    }

    private void ShowDeliveryConditionWarning()
    {
        ShowHelpBox(LT(
            "DeliveryConditionWarning",
            "~h~~y~Chưa đủ điều kiện giao xe, hãy chú ý!!!"
        ));
    }

    private void ShowDeliveryConfirmHelpBox(int payout)
    {
        ShowHelpBox(LT(
            "DeliveryConfirmHelpBox",
            "Đơn giao xe này sẽ nhận được số tiền là ~HUD_COLOUR_DEGEN_YELLOW~${AMOUNT}~s~ này thôi nhé!!?",
            "{AMOUNT}", payout.ToString("N0", CultureInfo.InvariantCulture)
        ));
    }

    private void CompleteMissionWithoutReward()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player != null && player.Exists() && _missionVehicle != null && _missionVehicle.Exists())
            {
                if (player.CurrentVehicle != null &&
                    player.CurrentVehicle.Exists() &&
                    player.CurrentVehicle.Handle == _missionVehicle.Handle)
                {
                    Function.Call(Hash.TASK_LEAVE_VEHICLE, player.Handle, _missionVehicle.Handle, 0);
                }
            }
        }
        catch
        {
        }

        ReleaseMissionVehicleWithDeferredCleanup();
        CleanupDestinationObjects();
        ResetDeliveryFlowFlags();

        _selectedVehicleSpec = null;
        _selectedDestination = Vector3.Zero;
        _selectedContactName = string.Empty;

        _state = EventState.Idle;
        _playerNearMarkerCached = false;
        _stateSince = 0;
        _missionAcceptedNotificationShown = false;
        _wantedWarningShown = false;
        _missionBaseReward = 0;

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;
        DeliveryContractBridge.PlanePendingStart = false;
        DeliveryContractBridge.SubmarinePendingStart = false;
        HelpBoxBridge.ClearPersistent();

        if (_modReady)
            _nextRollTime = Game.GameTime + RollIntervalMs;
    }

    private void StartWantedPhase()
    {
        if (_wantedPhaseActive)
            return;

        _wantedPhaseActive = true;
        _wantedDeliveryDeadlineTime = Game.GameTime + WantedDeliveryWindowMs;

        try
        {
            SetWantedLevel(WantedStarsOnTimeout);
        }
        catch
        {
        }
    }

    private bool IsVehicleNearDestination(Vehicle vehicle, Vector3 destination)
    {
        if (vehicle == null || !vehicle.Exists())
            return false;

        return vehicle.Position.DistanceToSquared(destination) <= DestinationCompleteRadius * DestinationCompleteRadius;
    }

    private int GetVehicleDamagePercent(Vehicle targetVehicle)
    {
        if (targetVehicle == null || !targetVehicle.Exists())
            return 100;

        // tính % hư hại tổng quát:
        float engine = ClampFloat(targetVehicle.EngineHealth, 0f, 1000f);
        float body = ClampFloat(targetVehicle.BodyHealth, 0f, 1000f);

        float deficitEngine = 1000f - engine;
        float deficitBody = 1000f - body;

        float percent = (deficitEngine + deficitBody) / 20.0f;

        if (percent < 0f)
            percent = 0f;
        if (percent > 100f)
            percent = 100f;

        return (int)Math.Round(percent, MidpointRounding.AwayFromZero);
    }

    private float ClampFloat(float value, float min, float max)
    {
        if (value < min)
            return min;
        if (value > max)
            return max;
        return value;
    }

    private int CalculateDeliveryReward(int damagePercent)
    {
        int baseReward = _missionBaseReward > 0 ? _missionBaseReward : RewardMoneyReference;

        if (damagePercent <= 0)
            return baseReward;

        int clampedDamage = damagePercent;
        if (clampedDamage < 0)
            clampedDamage = 0;
        if (clampedDamage > 10)
            clampedDamage = 10;

        int penaltyPerDamagePercent = GetScaledPenaltyPerDamagePercent(baseReward);
        int reward = baseReward - (clampedDamage * penaltyPerDamagePercent);

        return Math.Max(0, reward);
    }

    private void ArmMissionVehicleCleanup()
    {
        _missionVehicleCleanupAt = Game.GameTime + MissionVehicleForcedDeleteDelayMs;
    }

    private void ReleaseMissionVehicleWithDeferredCleanup()
    {
        if (_missionVehicle == null)
            return;

        try
        {
            if (_missionVehicle.Exists())
            {
                ReleaseMissionVehicleToGame(true);
                ArmMissionVehicleCleanup();
            }
            else
            {
                _missionVehicle = null;
                _missionVehicleCleanupAt = -1;
            }
        }
        catch
        {
            _missionVehicle = null;
            _missionVehicleCleanupAt = -1;
        }
    }

    private void UpdateDeferredMissionVehicleCleanup()
    {
        if (_missionVehicleCleanupAt < 0)
            return;

        if (Game.GameTime < _missionVehicleCleanupAt)
            return;

        try
        {
            if (_missionVehicle != null && _missionVehicle.Exists())
                _missionVehicle.Delete();
        }
        catch
        {
        }

        _missionVehicle = null;
        _missionVehicleCleanupAt = -1;
    }

    private void CompleteMission(int payout)
    {
        try
        {
            AddCashReward(payout);
            PlaySoundFrontend("PICK_UP", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch
        {
        }

        try
        {
            Ped player = Game.Player.Character;
            if (player != null && player.Exists() && _missionVehicle != null && _missionVehicle.Exists())
            {
                if (player.CurrentVehicle != null &&
                    player.CurrentVehicle.Exists() &&
                    player.CurrentVehicle.Handle == _missionVehicle.Handle)
                {
                    Function.Call(Hash.TASK_LEAVE_VEHICLE, player.Handle, _missionVehicle.Handle, 0);
                }
            }
        }
        catch
        {
        }

        ReleaseMissionVehicleWithDeferredCleanup();

        CleanupDestinationObjects();
        ResetDeliveryFlowFlags();

        _selectedVehicleSpec = null;
        _selectedDestination = Vector3.Zero;
        _selectedContactName = string.Empty;

        _state = EventState.Idle;
        _playerNearMarkerCached = false;
        _stateSince = 0;
        _missionAcceptedNotificationShown = false;
        _wantedWarningShown = false;
        _missionBaseReward = 0;

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;
        DeliveryContractBridge.PlanePendingStart = false;
        DeliveryContractBridge.SubmarinePendingStart = false;
        HelpBoxBridge.ClearPersistent();

        if (_modReady)
            _nextRollTime = Game.GameTime + RollIntervalMs;
    }

    private void CancelMarkerEvent()
    {
        CleanupPickupObjects();
        ResetDeliveryFlowFlags();

        _state = EventState.Idle;
        _playerNearMarkerCached = false;
        _stateSince = 0;
        _missionAcceptedNotificationShown = false;
        _wantedWarningShown = false;
        _missionBaseReward = 0;

        _selectedVehicleSpec = null;
        _selectedDestination = Vector3.Zero;
        _selectedContactName = string.Empty;

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;
        DeliveryContractBridge.PlanePendingStart = false;
        DeliveryContractBridge.SubmarinePendingStart = false;
        HelpBoxBridge.ClearPersistent();

        _nextRollTime = Game.GameTime + RollIntervalMs;
    }

    private void CleanupAllAndReturnToIdle(bool scheduleNextRoll, bool keepMissionVehicle = false)
    {
        CleanupPickupObjects();
        CleanupDestinationObjects();

        try
        {
            ReleaseMissionVehicleWithDeferredCleanup();
        }
        catch { }

        _selectedVehicleSpec = null;
        _selectedDestination = Vector3.Zero;
        _selectedContactName = string.Empty;

        ResetDeliveryFlowFlags();

        _state = EventState.Idle;
        _playerNearMarkerCached = false;
        _stateSince = 0;
        _missionAcceptedNotificationShown = false;
        _wantedWarningShown = false;
        _missionBaseReward = 0;

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;
        DeliveryContractBridge.PlanePendingStart = false;
        DeliveryContractBridge.SubmarinePendingStart = false;
        HelpBoxBridge.ClearPersistent();

        if (scheduleNextRoll && _modReady)
        {
            _nextRollTime = Game.GameTime + RollIntervalMs;
        }
    }

    private void ReleaseMissionVehicleToGame(bool lockVehicle)
    {
        if (_missionVehicle == null || !_missionVehicle.Exists())
            return;

        try
        {
            Ped player = Game.Player.Character;
            if (player != null && player.Exists() && player.CurrentVehicle != null && player.CurrentVehicle.Exists())
            {
                if (player.CurrentVehicle.Handle == _missionVehicle.Handle)
                {
                    Function.Call(Hash.TASK_LEAVE_VEHICLE, player.Handle, _missionVehicle.Handle, 0);
                }
            }

            if (lockVehicle)
            {
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, _missionVehicle.Handle, false, true, true);
                Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, _missionVehicle.Handle, true);
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _missionVehicle.Handle, 2);
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED_FOR_ALL_PLAYERS, _missionVehicle.Handle, true);

                _missionVehicle.LockStatus = VehicleLockStatus.Locked;
                _missionVehicle.IsPersistent = true;
            }
            else
            {
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _missionVehicle.Handle, 1);
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED_FOR_ALL_PLAYERS, _missionVehicle.Handle, false);
                _missionVehicle.LockStatus = VehicleLockStatus.Unlocked;
            }
        }
        catch
        {
        }
    }

    private void ResetDeliveryFlowFlags()
    {
        _wantedPhaseActive = false;
        _wantedDeliveryDeadlineTime = -1;
        _wantedWarningShown = false;   // reset cùng lúc
    }

    private void ResetRuntimeState()
    {
        CleanupPickupObjects();
        CleanupDestinationObjects();

        try
        {
            if (_missionVehicle != null && _missionVehicle.Exists())
            {
                _missionVehicle.Delete();
            }
        }
        catch { }

        _missionVehicle = null;
        _missionVehicleCleanupAt = -1;

        _selectedVehicleSpec = null;
        _selectedDestination = Vector3.Zero;
        _selectedContactName = string.Empty;

        ResetDeliveryFlowFlags();

        _modReady = false;
        _gameReadySince = -1;
        _nextRollTime = 0;
        _lastProximityCheckTime = 0;
        _stateSince = 0;
        _state = EventState.Idle;
        _playerNearMarkerCached = false;
        _missionAcceptedNotificationShown = false;
        _wantedWarningShown = false;
        _missionBaseReward = 0;

        _contractFailPenaltyPending = false;
        _contractFailPenaltyAt = -1;

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;
        DeliveryContractBridge.PlanePendingStart = false;
        DeliveryContractBridge.SubmarinePendingStart = false;
        HelpBoxBridge.ClearAll();
    }

    private void CreatePickupBlip()
    {
        DeletePickupBlip();

        try
        {
            _pickupBlip = World.CreateBlip(MarkerPosition);
            if (_pickupBlip != null)
            {
                _pickupBlip.Sprite = BlipSprite.Standard;
                _pickupBlip.Color = BlipColor.Yellow;
                _pickupBlip.Scale = 0.75f;
                _pickupBlip.IsShortRange = false;
                _pickupBlip.Name = LT("PickupBlipName", "Điểm nhận nhiệm vụ");
                _pickupBlip.ShowRoute = false;
            }
        }
        catch
        {
            _pickupBlip = null;
        }
    }

    private void CreateDestinationBlip()
    {
        DeleteDestinationBlip();

        try
        {
            _destinationBlip = World.CreateBlip(_selectedDestination);
            if (_destinationBlip != null)
            {
                _destinationBlip.Sprite = BlipSprite.Standard;
                _destinationBlip.Color = BlipColor.Blue;
                _destinationBlip.Scale = 0.7f;
                _destinationBlip.IsShortRange = false;
                _destinationBlip.Name = LT("DestinationBlipName", "Điểm giao xe");
                _destinationBlip.ShowRoute = false;
            }
        }
        catch
        {
            _destinationBlip = null;
        }
    }

    private void CleanupPickupObjects()
    {
        DeletePickupBlip();
    }

    private void CleanupDestinationObjects()
    {
        DeleteDestinationBlip();
    }

    private void DeletePickupBlip()
    {
        try
        {
            if (_pickupBlip != null && _pickupBlip.Exists())
                _pickupBlip.Delete();
        }
        catch
        {
        }

        _pickupBlip = null;
        DeliveryMarkerBridge.ClearPickup();
    }

    private void DeleteDestinationBlip()
    {
        try
        {
            if (_destinationBlip != null && _destinationBlip.Exists())
                _destinationBlip.Delete();
        }
        catch
        {
        }

        _destinationBlip = null;
        DeliveryMarkerBridge.ClearDestination();
    }

    private void DrawPickupMarker()
    {
        try
        {
            DeliveryMarkerBridge.SetPickup(
                MarkerType.VerticalCylinder,
                MarkerPosition + new Vector3(0.0f, 0.0f, -1.42f),
                new Vector3(0.85f, 0.85f, 0.85f),
                Color.FromArgb(200, 180, 0, 255));
        }
        catch
        {
        }
    }

    private void DrawDestinationMarker()
    {
        try
        {
            DeliveryMarkerBridge.SetDestination(
                MarkerType.VerticalCylinder,
                _selectedDestination + new Vector3(0.0f, 0.0f, -1.42f),
                new Vector3(0.85f, 0.85f, 0.85f),
                Color.FromArgb(185, 30, 144, 255));
        }
        catch
        {
        }
    }

    private bool IsPlayerNearPickupMarker()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return false;

            return player.Position.DistanceToSquared(MarkerPosition) <= PickupInteractRadius * PickupInteractRadius;
        }
        catch
        {
            return false;
        }
    }

    private void ShowHelpBox(string text)
    {
        HelpBoxBridge.SetPersistent(text);
    }

    private void ShowHelpBox(string text, int durationMs)
    {
        HelpBoxBridge.SetTimed(text, durationMs);
    }

    private VehicleSpec GetRandomVehicleSpec()
    {
        if (MissionVehicles == null || MissionVehicles.Length == 0)
            return null;

        return MissionVehicles[_rng.Next(MissionVehicles.Length)];
    }

    private void AddCashReward(int amount)
    {
        if (amount <= 0)
            return;

        try
        {
            Game.Player.Money += amount;
        }
        catch
        {
        }
    }

    private void SetWantedLevel(int stars)
    {
        try
        {
            int playerId = Function.Call<int>(Hash.PLAYER_ID);
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, playerId, stars, false);
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, playerId, true);
        }
        catch
        {
        }
    }

    private void PlaySoundFrontend(string soundName, string soundSet)
    {
        try
        {
            Audio.PlaySoundFrontend(soundName, soundSet);
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, soundName, soundSet, true);
        }
        catch
        {
        }
    }

    private void RequestModel(Model model)
    {
        try
        {
            if (model == null || !model.IsValid)
                return;

            if (!model.IsLoaded)
            {
                model.Request(500);

                int waited = 0;
                while (!model.IsLoaded && waited < 2500)
                {
                    Script.Wait(50);
                    waited += 50;
                }
            }
        }
        catch
        {
        }
    }

    private void ShowFeedMessage(string sender, string subject, string body)
    {
        try
        {
            Notification.Show(GetRandomFeedIcon(), sender, subject, body);
        }
        catch
        {
            try
            {
                GTA.UI.Notification.Show(body);
            }
            catch
            {
            }
        }
    }

    private NotificationIcon GetRandomFeedIcon()
    {
        try
        {
            if (FeedIcons == null || FeedIcons.Length == 0)
                return NotificationIcon.SocialClub;

            return FeedIcons[_rng.Next(FeedIcons.Length)];
        }
        catch
        {
            return NotificationIcon.SocialClub;
        }
    }
}

internal enum DeliveryContractMissionKind
{
    None = 0,
    Vehicle = 1,
    Plane = 2,
    Submarine = 3
}

internal static class DeliveryContractBridge
{
    public static DeliveryContractMissionKind CurrentKind = DeliveryContractMissionKind.None;

    // true = đã có roll ra máy bay, chờ PlaneDeliveryContractEvent nhận và dựng marker
    public static bool PlanePendingStart = false;

    // true = đã có roll ra tàu thuyền, chờ SubmarineDeliveryContractEvent nhận và dựng marker
    public static bool SubmarinePendingStart = false;
}

internal static class HelpBoxBridge
{
    private static readonly object SyncRoot = new object();

    private static string _persistentText = string.Empty;
    private static string _timedText = string.Empty;
    private static int _timedUntil = -1;

    public static void SetPersistent(string text)
    {
        lock (SyncRoot)
        {
            _persistentText = text ?? string.Empty;
        }
    }

    public static void ClearPersistent()
    {
        lock (SyncRoot)
        {
            _persistentText = string.Empty;
        }
    }

    public static void SetTimed(string text, int durationMs)
    {
        if (durationMs <= 0)
            durationMs = 1;

        lock (SyncRoot)
        {
            _timedText = text ?? string.Empty;
            _timedUntil = Game.GameTime + durationMs;
        }
    }

    public static void ClearAll()
    {
        lock (SyncRoot)
        {
            _persistentText = string.Empty;
            _timedText = string.Empty;
            _timedUntil = -1;
        }
    }

    public static void RenderThisFrame()
    {
        string timedText;
        int timedUntil;
        string persistentText;

        lock (SyncRoot)
        {
            timedText = _timedText;
            timedUntil = _timedUntil;
            persistentText = _persistentText;

            if (!string.IsNullOrEmpty(timedText) && Game.GameTime > timedUntil)
            {
                _timedText = string.Empty;
                _timedUntil = -1;
                timedText = string.Empty;
            }
        }

        try
        {
            if (!string.IsNullOrEmpty(timedText))
            {
                GTA.UI.Screen.ShowHelpTextThisFrame(timedText);
                return;
            }

            if (!string.IsNullOrEmpty(persistentText))
            {
                GTA.UI.Screen.ShowHelpTextThisFrame(persistentText);
            }
        }
        catch
        {
        }
    }
}

internal static class DeliveryMarkerBridge
{
    private struct MarkerRenderState
    {
        public bool Visible;
        public MarkerType Type;
        public Vector3 Position;
        public Vector3 Scale;
        public Color Color;
    }

    private static readonly object SyncRoot = new object();
    private static MarkerRenderState _pickup;
    private static MarkerRenderState _destination;

    public static void SetPickup(MarkerType type, Vector3 position, Vector3 scale, Color color)
    {
        lock (SyncRoot)
        {
            _pickup.Visible = true;
            _pickup.Type = type;
            _pickup.Position = position;
            _pickup.Scale = scale;
            _pickup.Color = color;
        }
    }

    public static void SetDestination(MarkerType type, Vector3 position, Vector3 scale, Color color)
    {
        lock (SyncRoot)
        {
            _destination.Visible = true;
            _destination.Type = type;
            _destination.Position = position;
            _destination.Scale = scale;
            _destination.Color = color;
        }
    }

    public static void ClearPickup()
    {
        lock (SyncRoot)
        {
            _pickup.Visible = false;
        }
    }

    public static void ClearDestination()
    {
        lock (SyncRoot)
        {
            _destination.Visible = false;
        }
    }

    public static void ClearAll()
    {
        lock (SyncRoot)
        {
            _pickup.Visible = false;
            _destination.Visible = false;
        }
    }

    public static void RenderThisFrame()
    {
        MarkerRenderState pickup;
        MarkerRenderState destination;

        lock (SyncRoot)
        {
            pickup = _pickup;
            destination = _destination;
        }

        try
        {
            if (pickup.Visible)
            {
                World.DrawMarker(
                    pickup.Type,
                    pickup.Position,
                    Vector3.Zero,
                    Vector3.Zero,
                    pickup.Scale,
                    pickup.Color);
            }
        }
        catch
        {
        }

        try
        {
            if (destination.Visible)
            {
                World.DrawMarker(
                    destination.Type,
                    destination.Position,
                    Vector3.Zero,
                    Vector3.Zero,
                    destination.Scale,
                    destination.Color);
            }
        }
        catch
        {
        }
    }
}

internal sealed class DeliveryMarkerRenderer : Script
{
    public DeliveryMarkerRenderer()
    {
        Tick += OnTick;
        Interval = 0;
    }

    private void OnTick(object sender, EventArgs e)
    {
        DeliveryMarkerBridge.RenderThisFrame();
    }
}

internal sealed class VehicleDeliveryContractHelpBoxRenderer : Script
{
    public VehicleDeliveryContractHelpBoxRenderer()
    {
        Tick += OnTick;
        Interval = 0;
    }

    private void OnTick(object sender, EventArgs e)
    {
        HelpBoxBridge.RenderThisFrame();
    }
}