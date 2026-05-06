using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

public class SubmarineDeliveryContractEvent : Script
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

    private const int GameReadyDelayMs = 3000;
    private const int FadeOutMs = 800;
    private const int PostFadeDelayMs = 900;

    private const int MissionVehicleForcedDeleteDelayMs = 80000;

    private const int SpecialVehicleUseDurationMs = 150000;
    private const int SpecialVehicleUnusedCleanupDelayMs = 45000;

    private static readonly Vector3 SpecialVehicleDestinationPosition = new Vector3(2721.440000f, -1598.636000f, -0.128290f);
    private static readonly Vector3 SpecialVehicleSpawnPosition = new Vector3(2734.299000f, -1615.712000f, 3.896975f);
    private const float SpecialVehicleDestinationMatchRadius = 0.75f;
    private const float SpecialVehicleSpawnHeading = 0f;
    private const uint SpecialVehicleModelHash = 0xA1355F67u;   // Blazer Aqua

    private const int RewardMoneyMin = 180000;
    private const int RewardMoneyMax = 250000;

    private const int DamagePenaltyPerPercent = 6000;
    private const int DamageZeroRewardThresholdPercent = 30;

    private const float PickupInteractRadius = 2.0f;
    private const float DestinationCompleteRadius = 2.5f;
    private const float DestinationDepthTolerance = 2.0f;

    private static readonly Vector3 MarkerPosition = new Vector3(-768.996200f, -1327.138000f, 9.599990f);
    private static readonly Vector3 PostCutscenePlayerPosition = new Vector3(-764.503200f, -1375.198000f, 1.595213f);
    private static readonly Vector3 MissionVehicleSpawnPosition = new Vector3(-761.6564f, -1371.609f, 0.3017412f);
    private const float MissionVehicleSpawnHeading = 232.3191f;

    private static readonly VehicleSpec[] MissionVehicles =
    {
        new VehicleSpec(0x9A474B5Eu, "Avisa"),
        new VehicleSpec(0x2189D250u, "APC"),
        new VehicleSpec(0xA1355F67u, "Blazer Aqua"),
        new VehicleSpec(0x107F392Cu, "Dinghy"),
        new VehicleSpec(0xEF813606u, "Kurtz 31 Patrol"),
        new VehicleSpec(0xC07107EEu, "Kraken"),
        new VehicleSpec(0x6EF89CCCu, "Longfin"),
        new VehicleSpec(0xC1CE1183u, "Marquis"),
        new VehicleSpec(0xE2E7D4ABu, "Police Predator"),
        new VehicleSpec(0xDB4388E4u, "Seashark"),
        new VehicleSpec(0x34DBA661u, "Stromberg"),
        new VehicleSpec(0xEF2295C9u, "Suntrap"),
        new VehicleSpec(0x56C8A5EFu, "Toreador"),
        new VehicleSpec(0x362CAC6Du, "Toro"),
        new VehicleSpec(0xC58DA34Au, "Weaponized Dinghy"),
    };

    private static readonly Vector3[] DestinationPoints =
    {
        new Vector3(2721.440000f, -1598.636000f, -0.128290f),
        new Vector3(3843.525000f, 4466.951000f, -0.474536f),
        new Vector3(-893.881500f, 6217.255000f, 0.055056f),
        new Vector3(-3360.179000f, 980.086200f, -0.447586f),
        new Vector3(-1620.646000f, 5247.949000f, -1.083967f),
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
    private int _stateSince;
    private int _lastProximityCheckTime;

    private Vehicle _specialVehicle;
    private int _specialVehicleUseUntil = -1;
    private int _specialVehicleCleanupAt = -1;
    private bool _specialVehicleWasUsed;

    private EventState _state = EventState.Idle;
    private bool _playerNearMarkerCached;
    private bool _missionAcceptedNotificationShown;

    private int _missionBaseReward;
    private int _missionVehicleCleanupAt = -1;

    private VehicleSpec _selectedVehicleSpec;
    private Vector3 _selectedDestination = Vector3.Zero;
    private string _selectedContactName = string.Empty;

    private Vehicle _missionVehicle;
    private Blip _pickupBlip;
    private Blip _destinationBlip;

    public SubmarineDeliveryContractEvent()
    {
        Tick += OnTick;
        Interval = 16;
    }

    public static void RequestSpawn()
    {
        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.Submarine;
        DeliveryContractBridge.SubmarinePendingStart = true;
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
        }

        UpdateDeferredMissionVehicleCleanup();
        UpdateSpecialVehicleState();

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
        if (!DeliveryContractBridge.SubmarinePendingStart)
            return;

        if (DeliveryContractBridge.CurrentKind != DeliveryContractMissionKind.Submarine)
            return;

        ShowMarkerEvent();
    }

    private void ShowMarkerEvent()
    {
        CleanupPickupObjects();
        ResetDeliveryFlowFlags();

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.Submarine;

        _state = EventState.MarkerAvailable;
        _stateSince = Game.GameTime;
        _playerNearMarkerCached = false;
        _missionAcceptedNotificationShown = false;

        CreatePickupBlip();

        Notification.Show(LT(
            "SubmarineMarkerEventNotification",
            "~y~~h~Đang có yêu cầu giao tàu thuyền! Bạn có muốn kiếm thêm thu nhập không?"
        ));
    }

    private void UpdateMarkerState()
    {
        DrawPickupMarker();

        if (Game.GameTime - _stateSince >= 200000)
        {
            CancelMarkerEvent();
            return;
        }

        if (Game.GameTime - _lastProximityCheckTime >= 500)
        {
            _lastProximityCheckTime = Game.GameTime;
            _playerNearMarkerCached = IsPlayerNearPickupMarker();
        }

        if (_playerNearMarkerCached)
        {
            ShowHelpBox(LT(
                "SubmarineMarkerAcceptPrompt",
                "~HUD_COLOUR_CONTROLLER_FRANKLIN~Bạn có muốn nhận nhiệm vụ tàu thuyền này không? Nhiệm vụ này sẽ đơn giản hơn giao xe hoặc giao máy bay đó?"
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

        _missionBaseReward = RollMissionBaseReward();
        ResetDeliveryFlowFlags();

        _state = EventState.FadingOut;
        _stateSince = Game.GameTime;

        DeletePickupBlip();
        GTA.UI.Screen.FadeOut(FadeOutMs);
    }

    private int RollMissionBaseReward()
    {
        return _rng.Next(RewardMoneyMin, RewardMoneyMax + 1);
    }

    private void UpdateFadingOutState()
    {
        if (Game.GameTime - _stateSince < FadeOutMs + PostFadeDelayMs)
            return;

        PositionPlayerAfterCutscene();

        if (!SpawnMission())
        {
            GTA.UI.Screen.FadeIn(FadeOutMs);
            CleanupAllAndReturnToIdle(true);
            return;
        }

        GTA.UI.Screen.FadeIn(FadeOutMs);

        _state = EventState.MissionActive;
        _stateSince = Game.GameTime;
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
        if (!TryConsumeAuctionForcedSubmarine(out _selectedVehicleSpec))
            _selectedVehicleSpec = GetRandomVehicleSpec();

        if (_selectedVehicleSpec == null)
            return false;

        _selectedDestination = DestinationPoints[_rng.Next(DestinationPoints.Length)];
        _selectedContactName = ContactNames[_rng.Next(ContactNames.Length)];

        if (IsSpecialVehicleDestination(_selectedDestination))
            SpawnSpecialVehicle();

        Model vehicleModel = new Model(unchecked((int)_selectedVehicleSpec.ModelHash));
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

            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _missionVehicle.Handle, true, true);
            Function.Call(Hash.SET_VEHICLE_ENGINE_ON, _missionVehicle.Handle, true, true, false);
            Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _missionVehicle.Handle, 1);
            Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED_FOR_ALL_PLAYERS, _missionVehicle.Handle, false);

            _missionVehicle.LockStatus = VehicleLockStatus.Unlocked;
        }
        catch
        {
        }

        CreateDestinationBlip();
        return true;
    }

    private bool TryConsumeAuctionForcedSubmarine(out VehicleSpec spec)
    {
        spec = null;

        uint hash;
        string displayName;
        int classId;

        if (!DeliveryContractBridge.TryConsumeForcedMission(
                DeliveryContractMissionKind.Submarine,
                out hash,
                out displayName,
                out classId))
        {
            return false;
        }

        spec = new VehicleSpec(hash, string.IsNullOrWhiteSpace(displayName) ? "Tàu thuyền" : displayName);
        return true;
    }

    private bool IsSpecialVehicleDestination(Vector3 destination)
    {
        float dx = destination.X - SpecialVehicleDestinationPosition.X;
        float dy = destination.Y - SpecialVehicleDestinationPosition.Y;
        float dz = destination.Z - SpecialVehicleDestinationPosition.Z;

        return (dx * dx + dy * dy + dz * dz) <= (SpecialVehicleDestinationMatchRadius * SpecialVehicleDestinationMatchRadius);
    }

    private void SpawnSpecialVehicle()
    {
        if (_specialVehicle != null && _specialVehicle.Exists())
            return;

        Model vehicleModel = new Model(unchecked((int)SpecialVehicleModelHash));
        if (!vehicleModel.IsValid || !vehicleModel.IsInCdImage)
            return;

        RequestModel(vehicleModel);
        if (!vehicleModel.IsLoaded)
            return;

        try
        {
            _specialVehicle = World.CreateVehicle(vehicleModel, SpecialVehicleSpawnPosition, SpecialVehicleSpawnHeading);
        }
        catch
        {
            _specialVehicle = null;
        }

        if (_specialVehicle == null || !_specialVehicle.Exists())
        {
            _specialVehicle = null;
            return;
        }

        try
        {
            _specialVehicle.IsPersistent = true;
            _specialVehicle.IsInvincible = false;
            _specialVehicle.CanBeVisiblyDamaged = true;
            _specialVehicle.DirtLevel = 0f;
            _specialVehicle.NeedsToBeHotwired = false;

            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _specialVehicle.Handle, true, true);
            Function.Call(Hash.SET_VEHICLE_ENGINE_ON, _specialVehicle.Handle, false, true, false);
            Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _specialVehicle.Handle, 1);
            Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED_FOR_ALL_PLAYERS, _specialVehicle.Handle, false);
            Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, _specialVehicle.Handle, false);
            Function.Call(Hash.FREEZE_ENTITY_POSITION, _specialVehicle.Handle, false);

            _specialVehicle.LockStatus = VehicleLockStatus.Unlocked;
        }
        catch
        {
        }

        _specialVehicleWasUsed = false;
        _specialVehicleUseUntil = -1;
        _specialVehicleCleanupAt = -1;
    }

    private void UpdateSpecialVehicleState()
    {
        if (_specialVehicle == null)
            return;

        if (!_specialVehicle.Exists())
        {
            CleanupSpecialVehicleNow();
            return;
        }

        Ped player = Game.Player.Character;
        bool playerInSpecialVehicle =
            player != null &&
            player.Exists() &&
            !player.IsDead &&
            player.CurrentVehicle != null &&
            player.CurrentVehicle.Exists() &&
            player.CurrentVehicle.Handle == _specialVehicle.Handle;

        if (playerInSpecialVehicle)
        {
            try
            {
                _specialVehicle.IsPersistent = true;
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _specialVehicle.Handle, true, true);
            }
            catch
            {
            }

            _specialVehicleWasUsed = true;

            if (_specialVehicleUseUntil < 0)
                _specialVehicleUseUntil = Game.GameTime + SpecialVehicleUseDurationMs;

            if (_specialVehicleCleanupAt >= 0)
                _specialVehicleCleanupAt = -1;

            if (Game.GameTime >= _specialVehicleUseUntil)
            {
                ForcePlayerOutOfSpecialVehicle();
                ReleaseSpecialVehicleToGame(true);
                ArmSpecialVehicleCleanup();

                _specialVehicleUseUntil = -1;
            }

            return;
        }

        if (_specialVehicleWasUsed && _specialVehicleUseUntil >= 0 && Game.GameTime >= _specialVehicleUseUntil)
        {
            ReleaseSpecialVehicleToGame(true);
            ArmSpecialVehicleCleanup();
            _specialVehicleUseUntil = -1;
        }

        if (_specialVehicleCleanupAt >= 0 && Game.GameTime >= _specialVehicleCleanupAt)
        {
            CleanupSpecialVehicleNow();
        }
    }

    private void ForcePlayerOutOfSpecialVehicle()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player != null && player.Exists() && _specialVehicle != null && _specialVehicle.Exists())
            {
                player.Task.ClearAllImmediately();
                Function.Call(Hash.TASK_LEAVE_VEHICLE, player.Handle, _specialVehicle.Handle, 0);
            }
        }
        catch
        {
        }
    }

    private void QueueSpecialVehicleCleanupAfterMission()
    {
        if (_specialVehicle == null || !_specialVehicle.Exists())
            return;

        if (!_specialVehicleWasUsed)
        {
            ReleaseSpecialVehicleToGame(false);
            ArmSpecialVehicleCleanup();
        }
    }

    private void ArmSpecialVehicleCleanup()
    {
        if (_specialVehicle == null || !_specialVehicle.Exists())
            return;

        _specialVehicleCleanupAt = Game.GameTime + SpecialVehicleUnusedCleanupDelayMs;
    }

    private void ReleaseSpecialVehicleToGame(bool lockVehicle)
    {
        if (_specialVehicle == null || !_specialVehicle.Exists())
            return;

        try
        {
            if (lockVehicle)
            {
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, _specialVehicle.Handle, false, true, true);
                Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, _specialVehicle.Handle, true);
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _specialVehicle.Handle, 2);
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED_FOR_ALL_PLAYERS, _specialVehicle.Handle, true);
                Function.Call(Hash.FREEZE_ENTITY_POSITION, _specialVehicle.Handle, true);

                _specialVehicle.LockStatus = VehicleLockStatus.Locked;
            }
            else
            {
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, _specialVehicle.Handle, false, true, false);
                Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, _specialVehicle.Handle, false);
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _specialVehicle.Handle, 1);
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED_FOR_ALL_PLAYERS, _specialVehicle.Handle, false);
                Function.Call(Hash.FREEZE_ENTITY_POSITION, _specialVehicle.Handle, false);

                _specialVehicle.LockStatus = VehicleLockStatus.Unlocked;
            }

            _specialVehicle.IsPersistent = false;
        }
        catch
        {
        }
    }

    private void CleanupSpecialVehicleNow()
    {
        try
        {
            if (_specialVehicle != null && _specialVehicle.Exists())
                _specialVehicle.Delete();
        }
        catch
        {
        }

        _specialVehicle = null;
        _specialVehicleUseUntil = -1;
        _specialVehicleCleanupAt = -1;
        _specialVehicleWasUsed = false;
    }

    private void UpdateMissionState()
    {
        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
        {
            CleanupAllAndReturnToIdle(false);
            return;
        }

        if (_missionVehicle == null || !_missionVehicle.Exists() || _missionVehicle.IsDead || _missionVehicle.Health <= 0)
        {
            CleanupAllAndReturnToIdle(false);
            return;
        }

        DrawDestinationMarker();

        bool nearDestination = IsVehicleNearDestination(_missionVehicle, _selectedDestination);
        int damagePercent = GetVehicleDamagePercent(_missionVehicle);

        if (!nearDestination)
        {
            HelpBoxBridge.ClearPersistent();
            return;
        }

        if (damagePercent == 0)
        {
            int payout = CalculateDeliveryReward(0);
            ShowDeliveryConfirmHelpBox(payout);

            if (Game.IsKeyPressed(Keys.Enter))
            {
                CompleteMission(payout);
                return;
            }

            if (Game.IsKeyPressed(Keys.Back))
                return;

            return;
        }

        if (damagePercent <= DamageZeroRewardThresholdPercent)
        {
            int payout = CalculateDeliveryReward(damagePercent);
            ShowDeliveryConfirmHelpBox(payout);

            if (Game.IsKeyPressed(Keys.Enter))
            {
                CompleteMission(payout);
                return;
            }

            if (Game.IsKeyPressed(Keys.Back))
                return;

            return;
        }

        ShowOverDamageHelpBox();

        if (Game.IsKeyPressed(Keys.Enter))
        {
            CompleteMission(0);
            return;
        }

        if (Game.IsKeyPressed(Keys.Back))
            return;
    }

    private void ShowOverDamageHelpBox()
    {
        ShowHelpBox(LT(
            "SubmarineOverDamageWarning",
            "~h~Tàu thuyền đã hư quá rồi. Không còn tiền công đâu nhé!!!"
        ));
    }

    private void ShowMissionAcceptedMessage()
    {
        if (_missionAcceptedNotificationShown)
            return;

        _missionAcceptedNotificationShown = true;

        string sender = GetSelectedContactName();
        string subject = LT("MissionAcceptedSubject_Submarine", "Giao tàu thuyền");
        string body = BuildMissionBodyMessage();

        ShowFeedMessage(sender, subject, body);
    }

    private string BuildMissionBodyMessage()
    {
        string vehicleName = GetSelectedVehicleDisplayName();

        return LT(
            "SubmarineMissionBodyMessage",
            "Tôi đã mua chiếc ~HUD_COLOUR_DEGEN_CYAN~{VEHICLE}~s~ này rồi. Hiện tại tôi đang cần gấp nhưng không đến nơi nhận được. Nhưng tôi báo trước, nếu bạn làm hư nó thì tôi sẽ không trả tiền đâu. Chúc may mắn!!",
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
            return Language.Get("DefaultContactName", "MpFmContact");

        return _selectedContactName;
    }

    private void ShowDeliveryConfirmHelpBox(int payout)
    {
        ShowHelpBox(LT(
            "SubmarineDeliveryConfirmHelpBox",
            "Đơn này sẽ nhận được số tiền là ~HUD_COLOUR_DEGEN_YELLOW~${AMOUNT}~s~ này thôi nhé!!?",
            "{AMOUNT}", payout.ToString("N0", CultureInfo.InvariantCulture)
        ));
    }

    private void CompleteMission(int payout)
    {
        try
        {
            AddCashReward(payout);
            if (payout > 0)
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

        ReleaseMissionVehicleWithDeferredCleanup(true);
        QueueSpecialVehicleCleanupAfterMission();

        CleanupDestinationObjects();
        ResetDeliveryFlowFlags();

        _selectedVehicleSpec = null;
        _selectedDestination = Vector3.Zero;
        _selectedContactName = string.Empty;

        _state = EventState.Idle;
        _playerNearMarkerCached = false;
        _stateSince = 0;
        _missionAcceptedNotificationShown = false;
        _missionBaseReward = 0;

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.Submarine;
        DeliveryContractBridge.SubmarinePendingStart = false;
        HelpBoxBridge.ClearPersistent();
    }

    private void CancelMarkerEvent()
    {
        CleanupPickupObjects();
        ResetDeliveryFlowFlags();

        _state = EventState.Idle;
        _playerNearMarkerCached = false;
        _stateSince = 0;
        _missionAcceptedNotificationShown = false;
        _missionBaseReward = 0;

        _selectedVehicleSpec = null;
        _selectedDestination = Vector3.Zero;
        _selectedContactName = string.Empty;

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;
        DeliveryContractBridge.SubmarinePendingStart = false;
        HelpBoxBridge.ClearPersistent();
    }

    private void CleanupAllAndReturnToIdle(bool deleteVehicle)
    {
        CleanupPickupObjects();
        CleanupDestinationObjects();

        try
        {
            if (deleteVehicle)
                CleanupMissionVehicleNow();
            else
                ReleaseMissionVehicleWithDeferredCleanup(true);
        }
        catch
        {
        }

        QueueSpecialVehicleCleanupAfterMission();

        _selectedVehicleSpec = null;
        _selectedDestination = Vector3.Zero;
        _selectedContactName = string.Empty;

        ResetDeliveryFlowFlags();

        _state = EventState.Idle;
        _playerNearMarkerCached = false;
        _stateSince = 0;
        _missionAcceptedNotificationShown = false;
        _missionBaseReward = 0;

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;
        DeliveryContractBridge.SubmarinePendingStart = false;
        HelpBoxBridge.ClearPersistent();
    }

    private void CleanupMissionVehicleNow()
    {
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

    private void ArmMissionVehicleCleanup()
    {
        _missionVehicleCleanupAt = Game.GameTime + MissionVehicleForcedDeleteDelayMs;
    }

    private void ReleaseMissionVehicleWithDeferredCleanup(bool lockVehicle)
    {
        if (_missionVehicle == null)
            return;

        try
        {
            if (_missionVehicle.Exists())
            {
                ReleaseMissionVehicleToGame(lockVehicle);
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

        CleanupMissionVehicleNow();
    }

    private void ReleaseMissionVehicleToGame(bool lockVehicle)
    {
        if (_missionVehicle == null || !_missionVehicle.Exists())
            return;

        try
        {
            if (lockVehicle)
            {
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, _missionVehicle.Handle, false, true, true);
                Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, _missionVehicle.Handle, true);
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _missionVehicle.Handle, 2);
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED_FOR_ALL_PLAYERS, _missionVehicle.Handle, true);
                Function.Call(Hash.FREEZE_ENTITY_POSITION, _missionVehicle.Handle, true);

                _missionVehicle.LockStatus = VehicleLockStatus.Locked;
            }
            else
            {
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _missionVehicle.Handle, 1);
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED_FOR_ALL_PLAYERS, _missionVehicle.Handle, false);
                _missionVehicle.LockStatus = VehicleLockStatus.Unlocked;
            }

            _missionVehicle.IsPersistent = false;
        }
        catch
        {
        }
    }

    private int CalculateDeliveryReward(int damagePercent)
    {
        int baseReward = _missionBaseReward > 0 ? _missionBaseReward : RewardMoneyMin;

        if (damagePercent <= 0)
            return baseReward;

        if (damagePercent > DamageZeroRewardThresholdPercent)
            return 0;

        int reward = baseReward - (damagePercent * DamagePenaltyPerPercent);
        return Math.Max(0, reward);
    }

    private int GetVehicleDamagePercent(Vehicle targetVehicle)
    {
        if (targetVehicle == null || !targetVehicle.Exists())
            return 100;

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

    private bool IsVehicleNearDestination(Vehicle vehicle, Vector3 destination)
    {
        if (vehicle == null || !vehicle.Exists())
            return false;

        float dx = vehicle.Position.X - destination.X;
        float dy = vehicle.Position.Y - destination.Y;
        float horizontalDistanceSquared = dx * dx + dy * dy;
        float depthDelta = Math.Abs(vehicle.Position.Z - destination.Z);

        return horizontalDistanceSquared <= (DestinationCompleteRadius * DestinationCompleteRadius) &&
               depthDelta <= DestinationDepthTolerance;
    }

    private VehicleSpec GetRandomVehicleSpec()
    {
        if (MissionVehicles == null || MissionVehicles.Length == 0)
            return null;

        return MissionVehicles[_rng.Next(MissionVehicles.Length)];
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
                _pickupBlip.Name = LT("SubmarinePickupBlipName", "Điểm nhận nhiệm vụ");
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
                _destinationBlip.Color = BlipColor.Yellow;
                _destinationBlip.Scale = 0.7f;
                _destinationBlip.IsShortRange = false;
                _destinationBlip.Name = LT("SubmarineDestinationBlipName", "Điểm giao tàu thuyền");
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
                Color.FromArgb(220, 255, 165, 0));
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
                MarkerType.Ring,
                _selectedDestination + new Vector3(0.0f, 0.0f, 0.15f),
                new Vector3(2.8f, 2.8f, 2.8f),
                Color.FromArgb(220, 255, 215, 0));
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

    private void ResetDeliveryFlowFlags()
    {
        // File tàu thuyền không dùng wanted phase
    }

    private void ResetRuntimeState()
    {
        CleanupPickupObjects();
        CleanupDestinationObjects();

        try
        {
            if (_missionVehicle != null && _missionVehicle.Exists())
                _missionVehicle.Delete();
        }
        catch
        {
        }

        CleanupSpecialVehicleNow();

        _missionVehicle = null;
        _missionVehicleCleanupAt = -1;

        _selectedVehicleSpec = null;
        _selectedDestination = Vector3.Zero;
        _selectedContactName = string.Empty;

        _modReady = false;
        _gameReadySince = -1;
        _stateSince = 0;
        _state = EventState.Idle;
        _playerNearMarkerCached = false;
        _missionAcceptedNotificationShown = false;
        _missionBaseReward = 0;
        _lastProximityCheckTime = 0;

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;
        DeliveryContractBridge.SubmarinePendingStart = false;
        DeliveryMarkerBridge.ClearAll();
        HelpBoxBridge.ClearAll();
    }

    private void ShowHelpBox(string text)
    {
        HelpBoxBridge.SetPersistent(text);
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