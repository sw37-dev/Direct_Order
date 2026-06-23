using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

internal static class PlaneDeliveryHelpBoxOverlay
{
    private static readonly object SyncRoot = new object();
    private static string _currentText;

    public static void SetText(string text)
    {
        lock (SyncRoot)
        {
            _currentText = string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }

    public static void Clear()
    {
        lock (SyncRoot)
        {
            _currentText = null;
        }
    }

    public static void DrawThisFrame()
    {
        string text;

        lock (SyncRoot)
        {
            text = _currentText;
        }

        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            GTA.UI.Screen.ShowHelpTextThisFrame(text);
        }
        catch
        {
            // Không fallback sang subtitle vì bạn muốn dùng help-box gốc.
        }
    }
}

public class PlaneDeliveryContractHelpBoxRenderer : Script
{
    public PlaneDeliveryContractHelpBoxRenderer()
    {
        Tick += OnTick;
        Interval = 0; // Chỉ script UI này chạy mỗi frame
    }

    private void OnTick(object sender, EventArgs e)
    {
        PlaneDeliveryHelpBoxOverlay.DrawThisFrame();
    }
}

public class PlaneDeliveryContractEvent : Script
{
    private enum EventState
    {
        Idle,
        MarkerAvailable,
        FadingOut,
        MissionActive
    }

    private sealed class PlaneSpec
    {
        public uint ModelHash;
        public string DisplayName;

        public PlaneSpec(uint modelHash, string displayName)
        {
            ModelHash = modelHash;
            DisplayName = displayName;
        }
    }

    private const int GameReadyDelayMs = 3000;
    private const int FadeOutMs = 800;
    private const int PostFadeDelayMs = 900;

    private const int RewardMoneyMin = 250000;
    private const int RewardMoneyMax = 280000;

    private const int MissionVehicleForcedDeleteDelayMs = 80000;
    private const int WantedImmunityAfterSuccessMs = 100000;

    private const float PickupInteractRadius = 2.0f;
    private const float DestinationCompleteRadius = 6.0f;
    private const float DeliveryAltitudeTolerance = 3.0f;

    // Thêm hằng số mới
    private const float WantedImmunityActivationRadius = 300.0f;

    private static readonly Vector3 MarkerPosition = new Vector3(1697.248000f, 3291.199000f, 48.920480f);
    private static readonly Vector3 PostCutscenePlayerPosition = new Vector3(1697.248000f, 3291.199000f, 48.920480f);
    private static readonly Vector3 MissionVehicleSpawnPosition = new Vector3(1698.675000f, 3248.991000f, 40.465130f);
    private const float MissionVehicleSpawnHeading = 105.3598f;

    private static readonly Vector3[] DestinationPoints =
    {
        new Vector3(-971.439800f, -3088.593000f, 13.944370f),
        new Vector3(-1279.593000f, -3146.625000f, 13.945170f),
        new Vector3(-1599.893000f, -2735.104000f, 13.944700f),
        new Vector3(-1409.283000f, -2450.432000f, 13.948400f),
        new Vector3(-1255.059000f, -2578.894000f, 13.945150f),
        new Vector3(-1522.380000f, -2936.811000f, 13.944440f),
        new Vector3(-1466.653000f, -2961.940000f, 13.968570f),
        new Vector3(-1005.645000f, -3402.934000f, 13.835090f),
        new Vector3(-1107.906000f, -3018.113000f, 13.944580f),
        new Vector3(-1587.507000f, -3096.899000f, 13.944750f),
        new Vector3(-1680.086000f, -2871.052000f, 13.944450f),
        new Vector3(-1316.738000f, -3123.635000f, 14.307090f),
        new Vector3(-1421.096000f, -2642.741000f, 13.944940f),
        new Vector3(-1127.807000f, -2405.883000f, 13.945160f),
        new Vector3(-991.696400f, -3096.851000f, 13.944420f),
        new Vector3(-998.743700f, -3264.091000f, 14.313570f),
        new Vector3(-1583.820000f, -2703.204000f, 14.783270f),
    };

    private static readonly PlaneSpec[] MissionPlanes =
    {
        // Bay thật: máy bay / trực thăng / blimp
        new PlaneSpec(0x46699F47u, "Akula"),
        new PlaneSpec(0x81BD2ED0u, "Avenger"),
        new PlaneSpec(0x31F0B376u, "Annihilator"),
        new PlaneSpec(0x64DE07A1u, "B-11 Strikeforce"),
        new PlaneSpec(0x53174EEFu, "Cargobob"),
        new PlaneSpec(0xE384DD25u, "Conada"),
        new PlaneSpec(0xE4C8C4Du, "F-160 Raiju"),
        new PlaneSpec(0xFD707EDEu, "FH-1 Hunter"),
        new PlaneSpec(0x39D6E83Fu, "Hydra"),
        new PlaneSpec(0xC3F25753u, "Howard NX-25"),
        new PlaneSpec(0x9A9EB7DEu, "LF-22 Starling"),
        new PlaneSpec(0xB79F589Eu, "Luxor Deluxe"),
        new PlaneSpec(0x97E55D11u, "Mammatus"),
        new PlaneSpec(0xD35698EFu, "Mogul"),
        new PlaneSpec(0xB39B0AE6u, "P-996 LAZER"),
        new PlaneSpec(0xAD6065C0u, "Pyro"),
        new PlaneSpec(0xC5DD6967u, "Rogue"),
        new PlaneSpec(0xD4AE63D9u, "SeaSparrow"),
        new PlaneSpec(0xE8983F9Fu, "Seabreeze"),
        new PlaneSpec(0x3E48BF23u, "Skylift"),
        new PlaneSpec(0x2A54C47Du, "SuperVolito"),
        new PlaneSpec(0x9C5E5644u, "SuperVolito Carbon"),
        new PlaneSpec(0x4019CB4Cu, "Swift Deluxe"),
        new PlaneSpec(0xEBC24DF2u, "Swift"),
        new PlaneSpec(0x761E2AD3u, "Titan"),
        new PlaneSpec(0x3329757Eu, "Titan 250 D"),
        new PlaneSpec(0x96E24857u, "Ultralight"),

        // Bay đặc biệt: xe bay / hover / nhảy bay
        new PlaneSpec(0x586765FBu, "Deluxo"),
        new PlaneSpec(0x7B54A9D3u, "Oppressor MK2"),
        new PlaneSpec(0xD9F0503Du, "Scramjet"),
        new PlaneSpec(0x58CDAF30u, "Thruster"),
    };

    private readonly Random _rng = new Random();

    private bool _modReady;
    private int _gameReadySince = -1;

    private EventState _state = EventState.Idle;
    private int _stateSince;

    private int _nextCleanupAt = -1;
    private int _wantedImmunityDisableAt = -1;
    private bool _wantedImmunityActive;

    private int _missionBaseReward;
    private bool _missionAcceptedNotificationShown;
    private bool _playerNearMarkerCached;
    private int _lastProximityCheckTime;

    private PlaneSpec _selectedPlaneSpec;
    private Vector3 _selectedDestination = Vector3.Zero;
    private string _selectedContactName = string.Empty;

    private Vehicle _missionVehicle;
    private Blip _pickupBlip;
    private Blip _destinationBlip;

    public PlaneDeliveryContractEvent()
    {
        Tick += OnTick;
        Interval = 16;
    }

    public static void RequestSpawn()
    {
        if (!PrimeAutoHandoverBridge.CanTriggerDeliveryContractsForCurrentCharacter())
            return;

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.Plane;
        DeliveryContractBridge.PlanePendingStart = true;
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (Game.IsLoading)
        {
            ResetRuntimeState();
            return;
        }

        if (!PrimeAutoHandoverBridge.CanTriggerDeliveryContractsForCurrentCharacter())
        {
            DeliveryContractBridge.PlanePendingStart = false;

            if (_state != EventState.Idle)
                CleanupAllAndReturnToIdle(true);

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

        UpdateWantedImmunity();
        UpdateDeferredCleanup();

        if (DeliveryContractBridge.PlanePendingStart &&
            _state == EventState.Idle &&
            DeliveryContractBridge.CurrentKind == DeliveryContractMissionKind.Plane)
        {
            DeliveryContractBridge.PlanePendingStart = false;
            StartMarkerEvent();
        }

        switch (_state)
        {
            case EventState.Idle:
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

    private void StartMarkerEvent()
    {
        ClearHelpBox();
        CleanupPickupObjects();
        ResetMissionFlags();

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.Plane;

        _state = EventState.MarkerAvailable;
        _stateSince = Game.GameTime;
        _lastProximityCheckTime = 0;
        _playerNearMarkerCached = false;
        _missionAcceptedNotificationShown = false;

        CreatePickupBlip();

        ShowPlaneOfferMessage();
    }

    private void ShowPlaneOfferMessage()
    {
        try
        {
            Notification.Show(
                NotificationIcon.Planesite,
                LT("PlaneOffer_Title", "Plane Site"),
                LT("PlaneOffer_Subject", "Giao máy bay"),
                LT("PlaneOffer_Body", "Đang có yêu cầu giao máy bay! Bạn có muốn kiếm thêm thu nhập không?")
            );
        }
        catch
        {
            try
            {
                GTA.UI.Notification.Show("Đang có yêu cầu giao máy bay! Bạn có muốn kiếm thêm thu nhập không?");
            }
            catch { }
        }
    }

    private void UpdateMarkerState()
    {
        DrawPickupMarker();

        if (Game.GameTime - _lastProximityCheckTime >= 300)
        {
            _lastProximityCheckTime = Game.GameTime;
            _playerNearMarkerCached = IsPlayerNearPickupMarker();
        }

        if (_playerNearMarkerCached)
        {
            ShowHelpBox(LT(
                "PlaneMarkerAcceptPrompt",
                "~HUD_COLOUR_FRANKLIN~Bạn có muốn nhận nhiệm vụ máy bay này không? Nhiệm vụ máy bay sẽ nguy hiểm hơn giao xe đấy?"
            ));
            HandleMarkerInput();
        }
        else
        {
            ClearHelpBox();
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

        ClearHelpBox();
        _missionBaseReward = RollMissionBaseReward();

        _state = EventState.FadingOut;
        _stateSince = Game.GameTime;

        DeletePickupBlip();
        GTA.UI.Screen.FadeOut(FadeOutMs);
    }

    private int RollMissionBaseReward()
    {
        return _rng.Next(RewardMoneyMin, RewardMoneyMax + 1);
    }

    // Trong UpdateFadingOutState(), bỏ dòng bật miễn sao truy nã ngay khi vào nhiệm vụ
    private void UpdateFadingOutState()
    {
        ClearHelpBox();

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
        catch { }
    }

    private bool SpawnMission()
    {
        if (!TryConsumeAuctionForcedPlane(out _selectedPlaneSpec))
            _selectedPlaneSpec = GetRandomPlaneSpec();

        if (_selectedPlaneSpec == null)
            return false;

        _selectedDestination = DestinationPoints[_rng.Next(DestinationPoints.Length)];
        _selectedContactName = Language.Get("DefaultContactName", "CHAR_MP_FM_CONTACT");

        Model model = new Model((int)_selectedPlaneSpec.ModelHash);
        if (!model.IsValid || !model.IsInCdImage)
            return false;

        RequestModel(model);
        if (!model.IsLoaded)
            return false;

        try
        {
            _missionVehicle = World.CreateVehicle(model, MissionVehicleSpawnPosition, MissionVehicleSpawnHeading);
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
            Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, _missionVehicle.Handle);

            _missionVehicle.LockStatus = VehicleLockStatus.Unlocked;
        }
        catch { }

        CreateDestinationBlip();
        return true;
    }

    private bool TryConsumeAuctionForcedPlane(out PlaneSpec spec)
    {
        spec = null;

        uint hash;
        string displayName;
        int classId;

        if (!DeliveryContractBridge.TryConsumeForcedMission(
                DeliveryContractMissionKind.Plane,
                out hash,
                out displayName,
                out classId))
        {
            return false;
        }

        spec = new PlaneSpec(hash, string.IsNullOrWhiteSpace(displayName) ? "Máy bay" : displayName);
        return true;
    }

    private void ShowMissionAcceptedMessage()
    {
        if (_missionAcceptedNotificationShown)
            return;

        _missionAcceptedNotificationShown = true;

        string sender = _selectedContactName;
        string subject = LT("PlaneMissionAcceptedSubject", "Giao máy bay");
        string body = BuildMissionBodyMessage();

        ShowFeedMessage(sender, subject, body);
    }

    private string BuildMissionBodyMessage()
    {
        string vehicleName = GetSelectedPlaneDisplayName();

        return LT(
            "PlaneMissionBodyMessage",
            "Tôi cần giao chiếc ~HUD_COLOUR_DEGEN_CYAN~{VEHICLE}~s~ này đến nơi an toàn. Hãy lái nó thật cẩn thận. Nếu làm hư quá nhiều thì tiền công sẽ bị cắt hoặc mất sạch.",
            "{VEHICLE}", vehicleName
        );
    }

    private string GetSelectedPlaneDisplayName()
    {
        if (_selectedPlaneSpec == null || string.IsNullOrWhiteSpace(_selectedPlaneSpec.DisplayName))
            return Language.Get("DefaultVehicleName", "Phương tiện");

        return _selectedPlaneSpec.DisplayName.Trim();
    }

    // Trong UpdateMissionState(), gọi kiểm tra vùng miễn sao truy nã trước khi xử lý giao hàng
    private void UpdateMissionState()
    {
        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
        {
            AbortMissionImmediate();
            return;
        }

        if (_missionVehicle == null || !_missionVehicle.Exists())
        {
            AbortMissionImmediate();
            return;
        }

        if (_missionVehicle.IsDead || _missionVehicle.Health <= 0)
        {
            AbortMissionImmediate();
            return;
        }

        UpdateWantedImmunityZone();

        DrawDestinationMarker();

        bool nearDestination = IsVehicleNearDestination(_missionVehicle, _selectedDestination);
        bool lowEnoughForDelivery = IsVehicleWithinDeliveryAltitude(_missionVehicle, _selectedDestination, DeliveryAltitudeTolerance);
        int damagePercent = GetVehicleDamagePercent(_missionVehicle);

        if (nearDestination)
        {
            if (!lowEnoughForDelivery)
            {
                ShowDeliveryConditionWarning();
                return;
            }

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

        ClearHelpBox();
    }

    // Thêm 2 hàm mới này
    private void UpdateWantedImmunityZone()
    {
        if (_state != EventState.MissionActive)
            return;

        if (_wantedImmunityActive)
        {
            ActivateWantedImmunity(true);
            return;
        }

        if (!IsWithinWantedImmunityZone())
            return;

        ClearPlayerWantedLevelNow();
        _wantedImmunityActive = true;
        _wantedImmunityDisableAt = -1;
        ActivateWantedImmunity(true);
    }

    private bool IsWithinWantedImmunityZone()
    {
        try
        {
            Vector3 currentPosition = GetMissionProgressPosition();
            return currentPosition.DistanceToSquared(_selectedDestination) <= WantedImmunityActivationRadius * WantedImmunityActivationRadius;
        }
        catch
        {
            return false;
        }
    }

    private Vector3 GetMissionProgressPosition()
    {
        try
        {
            if (_missionVehicle != null && _missionVehicle.Exists())
                return _missionVehicle.Position;
        }
        catch { }

        try
        {
            Ped player = Game.Player.Character;
            if (player != null && player.Exists())
                return player.Position;
        }
        catch { }

        return Vector3.Zero;
    }

    private bool IsVehicleWithinDeliveryAltitude(Vehicle vehicle, Vector3 destination, float toleranceMeters)
    {
        if (vehicle == null || !vehicle.Exists())
            return false;

        if (vehicle.IsDead || vehicle.Health <= 0)
            return false;

        try
        {
            float deltaZ = Math.Abs(vehicle.Position.Z - destination.Z);
            return deltaZ <= toleranceMeters;
        }
        catch
        {
            return false;
        }
    }

    private void ShowDeliveryConditionWarning()
    {
        ShowHelpBox(LT(
            "PlaneDeliveryConditionWarning",
            "~h~Máy bay phải hạ thấp xuống một chút rồi mới giao được."
        ));
    }

    private void ShowDeliveryConfirmHelpBox(int payout)
    {
        ShowHelpBox(LT(
            "PlaneDeliveryConfirmHelpBox",
            "Đơn hàng bay này sẽ được giao thành công và sẽ nhận được số tiền là ~HUD_COLOUR_DEGEN_GREEN~${AMOUNT}~h~~s~ này thôi nhé!!?",
            "{AMOUNT}", payout.ToString("N0", CultureInfo.InvariantCulture)
        ));
    }

    private int CalculateDeliveryReward(int damagePercent)
    {
        if (damagePercent > 50)
            return 0;

        int baseReward = _missionBaseReward > 0 ? _missionBaseReward : RewardMoneyMin;

        if (damagePercent <= 0)
            return baseReward;

        int reward = baseReward - (damagePercent * 5000);
        return Math.Max(0, reward);
    }

    private void CompleteMission(int payout)
    {
        ClearHelpBox();

        try
        {
            AddCashReward(payout);
            PlaySoundFrontend("PICK_UP", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        }
        catch { }

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
        catch { }

        ReleaseMissionVehicleWithDeferredCleanup();
        CleanupDestinationObjects();
        ResetMissionFlags();

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;

        _state = EventState.Idle;
        _stateSince = 0;
        _missionAcceptedNotificationShown = false;

        StartWantedImmunityCountdown();
    }

    private void CompleteMissionWithoutReward()
    {
        ClearHelpBox();

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
        catch { }

        ReleaseMissionVehicleWithDeferredCleanup();
        CleanupDestinationObjects();
        ResetMissionFlags();

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;

        _state = EventState.Idle;
        _stateSince = 0;
        _missionAcceptedNotificationShown = false;

        StartWantedImmunityCountdown();
    }

    private void AbortMissionImmediate()
    {
        ClearHelpBox();

        CleanupDestinationObjects();
        DeleteMissionVehicleNow();
        CleanupPickupObjects();
        ResetMissionFlags();

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;
        DeliveryContractBridge.PlanePendingStart = false;

        _state = EventState.Idle;
        _stateSince = 0;
        _missionAcceptedNotificationShown = false;

        DisableWantedImmunityNow();
    }

    // Sửa StartWantedImmunityCountdown() để vừa clear sao truy nã vừa bật miễn sao trong 100 giây
    private void StartWantedImmunityCountdown()
    {
        _wantedImmunityActive = true;
        _wantedImmunityDisableAt = Game.GameTime + WantedImmunityAfterSuccessMs;

        ClearPlayerWantedLevelNow();
        ActivateWantedImmunity(true);
    }

    // Thêm hàm xóa toàn bộ sao truy nã ngay lập tức
    private void ClearPlayerWantedLevelNow()
    {
        try
        {
            int playerId = Function.Call<int>(Hash.PLAYER_ID);
            Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, playerId);
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, playerId, 0, false);
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, playerId, false);
        }
        catch { }
    }

    private void UpdateWantedImmunity()
    {
        if (!_wantedImmunityActive)
            return;

        if (_wantedImmunityDisableAt > 0 && Game.GameTime >= _wantedImmunityDisableAt)
        {
            DisableWantedImmunityNow();
            return;
        }

        ActivateWantedImmunity(true);
    }

    private void ActivateWantedImmunity(bool enabled)
    {
        try
        {
            int playerId = Function.Call<int>(Hash.PLAYER_ID);
            Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, playerId, enabled);
            Function.Call(Hash.SET_DISPATCH_COPS_FOR_PLAYER, playerId, !enabled);

            if (enabled)
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, playerId, 0, false);
        }
        catch { }
    }

    private void DisableWantedImmunityNow()
    {
        _wantedImmunityActive = false;
        _wantedImmunityDisableAt = -1;
        ActivateWantedImmunity(false);
    }

    // Sửa IsVehicleNearDestination() để nhận radius linh hoạt
    private bool IsVehicleNearDestination(Vehicle vehicle, Vector3 destination, float radius = DestinationCompleteRadius)
    {
        if (vehicle == null || !vehicle.Exists())
            return false;

        return vehicle.Position.DistanceToSquared(destination) <= radius * radius;
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

        if (percent < 0f) percent = 0f;
        if (percent > 100f) percent = 100f;

        return (int)Math.Round(percent, MidpointRounding.AwayFromZero);
    }

    private float ClampFloat(float value, float min, float max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
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
                _nextCleanupAt = Game.GameTime + MissionVehicleForcedDeleteDelayMs;
            }
            else
            {
                _missionVehicle = null;
                _nextCleanupAt = -1;
            }
        }
        catch
        {
            _missionVehicle = null;
            _nextCleanupAt = -1;
        }
    }

    private void UpdateDeferredCleanup()
    {
        if (_nextCleanupAt < 0)
            return;

        if (Game.GameTime < _nextCleanupAt)
            return;

        DeleteMissionVehicleNow();
        _nextCleanupAt = -1;
    }

    private void DeleteMissionVehicleNow()
    {
        try
        {
            if (_missionVehicle != null && _missionVehicle.Exists())
                _missionVehicle.Delete();
        }
        catch { }

        _missionVehicle = null;
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
        catch { }
    }

    private void ResetMissionFlags()
    {
        _missionBaseReward = 0;
        _selectedPlaneSpec = null;
        _selectedDestination = Vector3.Zero;
        _selectedContactName = string.Empty;
        _playerNearMarkerCached = false;
        _lastProximityCheckTime = 0;
    }

    private void CleanupPickupObjects()
    {
        DeletePickupBlip();
    }

    private void CleanupDestinationObjects()
    {
        DeleteDestinationBlip();
    }

    private void CancelMarkerEvent()
    {
        ClearHelpBox();

        CleanupPickupObjects();
        ResetMissionFlags();

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;

        _state = EventState.Idle;
        _stateSince = 0;
        _missionAcceptedNotificationShown = false;

        DeliveryContractBridge.PlanePendingStart = false;
    }

    private void CleanupAllAndReturnToIdle(bool scheduleNextRoll)
    {
        ClearHelpBox();

        CleanupPickupObjects();
        CleanupDestinationObjects();
        DeleteMissionVehicleNow();
        ResetMissionFlags();

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;
        DeliveryContractBridge.PlanePendingStart = false;

        _state = EventState.Idle;
        _stateSince = 0;
        _missionAcceptedNotificationShown = false;

        if (scheduleNextRoll)
        {
            // plane file không tự roll, nên không cần làm gì ở đây
        }
    }

    private void ResetRuntimeState()
    {
        ClearHelpBox();

        CleanupPickupObjects();
        CleanupDestinationObjects();
        DeleteMissionVehicleNow();

        _nextCleanupAt = -1;
        _wantedImmunityActive = false;
        _wantedImmunityDisableAt = -1;

        ResetMissionFlags();

        DeliveryContractBridge.CurrentKind = DeliveryContractMissionKind.None;
        DeliveryContractBridge.PlanePendingStart = false;

        _modReady = false;
        _gameReadySince = -1;
        _state = EventState.Idle;
        _stateSince = 0;
        _missionAcceptedNotificationShown = false;
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
                _pickupBlip.Color = BlipColor.Red;
                _pickupBlip.Scale = 1.0f;
                _pickupBlip.IsShortRange = false;
                _pickupBlip.Name = LT("PlanePickupBlipName", "Điểm nhận nhiệm vụ");
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
                _destinationBlip.Scale = 1.0f;
                _destinationBlip.IsShortRange = false;
                _destinationBlip.Name = LT("PlaneDestinationBlipName", "Điểm giao máy bay");
                _destinationBlip.ShowRoute = false;
            }
        }
        catch
        {
            _destinationBlip = null;
        }
    }

    private void DeletePickupBlip()
    {
        try
        {
            if (_pickupBlip != null && _pickupBlip.Exists())
                _pickupBlip.Delete();
        }
        catch { }

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
        catch { }

        _destinationBlip = null;
        DeliveryMarkerBridge.ClearDestination();
    }

    private void DrawPickupMarker()
    {
        try
        {
            DeliveryMarkerBridge.SetPickup(
                MarkerType.VerticalCylinder,
                MarkerPosition + new Vector3(0.0f, 0.0f, -1.0f),
                new Vector3(1.0f, 1.0f, 1.0f),
                Color.FromArgb(220, 255, 0, 0));
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
                _selectedDestination + new Vector3(0.0f, 0.0f, -1.25f),
                new Vector3(1.0f, 1.0f, 1.0f),
                Color.FromArgb(200, 255, 255, 0));
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
        PlaneDeliveryHelpBoxOverlay.SetText(text);
    }

    private void ClearHelpBox()
    {
        PlaneDeliveryHelpBoxOverlay.Clear();
    }

    private PlaneSpec GetRandomPlaneSpec()
    {
        if (MissionPlanes == null || MissionPlanes.Length == 0)
            return null;

        return MissionPlanes[_rng.Next(MissionPlanes.Length)];
    }

    private void AddCashReward(int amount)
    {
        if (amount <= 0)
            return;

        try
        {
            Game.Player.Money += amount;
        }
        catch { }
    }

    private void PlaySoundFrontend(string soundName, string soundSet)
    {
        try
        {
            Audio.PlaySoundFrontend(soundName, soundSet);
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, soundName, soundSet, true);
        }
        catch { }
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
        catch { }
    }

    private void ShowFeedMessage(string sender, string subject, string body)
    {
        try
        {
            Notification.Show(NotificationIcon.SocialClub, sender, subject, body);
        }
        catch
        {
            try
            {
                GTA.UI.Notification.Show(body);
            }
            catch { }
        }
    }
}