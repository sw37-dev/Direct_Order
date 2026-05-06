using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

public class MoneyTruckEvent : Script
{
    private const int RollIntervalMs = 480000;   //  8 phút roll 1 lần
    private const int SpawnChancePercent = 15;   // 15% xuất hiện
    private const long RewardPointsMin = 1000000;   // số điểm thưởng ghi file
    private const long RewardPointsMax = 1300000;   // số điểm thưởng ghi file
    private const int CashRewardAmount = 10000;   // số tiền mặt
    private const int WantedStars = 1;

    private const int DiscountTicketDropChancePercent = 21;   // 21% rơi thẻ

    private readonly string _vehicleDiscountTicketFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PersistentManager", "MoneyTruckEvent_VehicleDiscountTicket.dat");

    private int _vehicleDiscountTicketCount = 0;

    private const int EventExpireMs = 200000;   // 200 giây ~ 3 phút 20 giây
    private const int ThinkIntervalMs = 1000;
    private const int GameReadyDelayMs = 3000;

    private const float LootTouchRadius = 1.5f;

    // Thêm gần các const hiện tại
    private const float CashLootMinSeparation = 0.95f;

    private const string GuardRelationshipGroupName = "MTX_MONEYTRUCK_GUARDS";
    private const string PlayerRelationshipGroupName = "PLAYER";

    private const int LootSpawnAttempts = 24;
    private const float LootGroundProbeHeight = 120.0f;
    private const float LootSpawnZOffset = 0.05f;

    private const float ConvoySpacing = 14.0f;
    private const float LeaderDriveSpeed = 18.0f;
    private const float FollowerDriveSpeed = 16.0f;

    private static readonly uint[] TruckModels =
    {
        0x6827CF72u,
        0xF337AB36u
    };

    private static readonly uint[] GuardWeapons =
    {
        0xBD248B55u,   // Mini SMG
        0xDBBD7280u,   // Combat MG Mk2
        0x61012683u,   // Gusenberg Sweeper
        0xAF3696A1u    // Up-n-Atomizer
    };

    private static readonly string[] CashPropModels =
    {
        "prop_cash_pile_01",
        "prop_cash_pile_02",
        "prop_money_bag_01",
        "prop_ld_banknotes_01"
    };

    private static string TruckBlipName => Language.Get("MoneyTruckEvent_TruckBlipName", "Xe chở tiền");
    private static string CrateBlipName => Language.Get("MoneyTruckEvent_CrateBlipName", "Cặp tiền thưởng");
    private static string CashCrateBlipName => Language.Get("MoneyTruckEvent_CashCrateBlipName", "Cặp tiền mặt");
    private static string GuardBlipName => Language.Get("MoneyTruckEvent_GuardBlipName", "Nhân viên bảo vệ");

    private readonly Random _rng = new Random();
    private int _nextRollTime = 0;
    private int _lastThinkTime = 0;

    private bool _modReady = false;
    private int _gameReadySince = -1;

    private bool _eventActive = false;
    private bool _alerted = false;
    private int _eventStartTime = 0;

    private int _guardRelationshipGroupHash = 0;
    private int _playerRelationshipGroupHash = 0;
    private bool _relationshipGroupsReady = false;

    private Vector3 _convoyDriveTarget = Vector3.Zero;

    private readonly List<ConvoyUnit> _convoyUnits = new List<ConvoyUnit>();

    private readonly string _rewardFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PersistentManager", "DirectOrder_spend.dat");

    // Thêm ngay trong MoneyTruckEvent, phía trên ConvoyUnit
    private sealed class CashLootItem
    {
        public Prop Prop = null;
        public int Amount = 0;
    }

    private sealed class ConvoyUnit
    {
        public long RewardPoints = 0;

        public Vehicle Truck = null;
        public Ped Driver = null;
        public Ped Passenger = null;

        // Trong ConvoyUnit, đổi/ thêm các field này
        public Prop DiscountTicket = null;
        public bool DiscountTicketSpawned = false;
        public Blip DiscountTicketBlip = null;

        public readonly List<CashLootItem> CashProps = new List<CashLootItem>();

        public Prop Crate = null;
        public Prop CashCrate = null;

        public Blip TruckBlip = null;
        public Blip CrateBlip = null;
        public Blip CashCrateBlip = null;
        public Blip DriverBlip = null;
        public Blip PassengerBlip = null;

        public Vector3 TruckPos = Vector3.Zero;
        public float TruckHeading = 0f;

        public int LastTruckHealth = -1;
        public int LastDriverHealth = -1;
        public int LastPassengerHealth = -1;

        public uint DriverWeapon = 0;
        public uint PassengerWeapon = 0;

        public bool LootSpawned = false;
        public float DriveSpeed = LeaderDriveSpeed;
    }

    public MoneyTruckEvent()
    {
        Tick += OnTick;
        Interval = ThinkIntervalMs;
        LoadVehicleDiscountTicketState();
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (Game.IsLoading || Game.IsCutsceneActive)
        {
            _modReady = false;
            _gameReadySince = -1;
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

        if (Game.GameTime - _lastThinkTime < ThinkIntervalMs)
            return;
        _lastThinkTime = Game.GameTime;

        if (!_eventActive)
        {
            if (Game.GameTime >= _nextRollTime)
            {
                _nextRollTime = Game.GameTime + RollIntervalMs;
                TryRollSpawn();
            }
            return;
        }

        UpdateActiveEvent();
    }

    private void LoadVehicleDiscountTicketState()
    {
        try
        {
            if (!File.Exists(_vehicleDiscountTicketFilePath))
            {
                _vehicleDiscountTicketCount = 0;
                return;
            }

            string raw = File.ReadAllText(_vehicleDiscountTicketFilePath).Trim();

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _vehicleDiscountTicketCount))
                _vehicleDiscountTicketCount = 0;

            if (_vehicleDiscountTicketCount < 0)
                _vehicleDiscountTicketCount = 0;
        }
        catch
        {
            _vehicleDiscountTicketCount = 0;
        }
    }

    private void SaveVehicleDiscountTicketState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_vehicleDiscountTicketFilePath));

            if (_vehicleDiscountTicketCount < 0)
                _vehicleDiscountTicketCount = 0;

            string tmp = _vehicleDiscountTicketFilePath + ".tmp";
            File.WriteAllText(tmp, _vehicleDiscountTicketCount.ToString(CultureInfo.InvariantCulture));

            if (File.Exists(_vehicleDiscountTicketFilePath))
                File.Delete(_vehicleDiscountTicketFilePath);

            File.Move(tmp, _vehicleDiscountTicketFilePath);
        }
        catch
        {
        }
    }

    // Sửa CleanupUnitDiscountTicket thành như sau
    private void CleanupUnitDiscountTicket(ConvoyUnit unit)
    {
        try
        {
            if (unit != null && unit.DiscountTicketBlip != null && unit.DiscountTicketBlip.Exists())
                unit.DiscountTicketBlip.Delete();
        }
        catch { }

        try
        {
            if (unit != null && unit.DiscountTicket != null && unit.DiscountTicket.Exists())
                unit.DiscountTicket.Delete();
        }
        catch { }

        if (unit != null)
        {
            unit.DiscountTicket = null;
            unit.DiscountTicketBlip = null;
            unit.DiscountTicketSpawned = false;
        }
    }

    // Thêm helper này
    private void CreateDiscountTicketBlip(ConvoyUnit unit)
    {
        try
        {
            if (unit == null || unit.DiscountTicket == null || !unit.DiscountTicket.Exists())
                return;

            unit.DiscountTicketBlip = unit.DiscountTicket.AddBlip();
            if (unit.DiscountTicketBlip != null)
            {
                unit.DiscountTicketBlip.Name = Language.Get(
                    "MoneyTruckEvent_DiscountTicketBlipName",
                    "Thẻ giảm giá");

                unit.DiscountTicketBlip.IsShortRange = false;
                unit.DiscountTicketBlip.Scale = 0.85f;
                unit.DiscountTicketBlip.Color = BlipColor.Purple;
                unit.DiscountTicketBlip.Sprite = BlipSprite.Standard;
            }
        }
        catch { }
    }

    // Trong SpawnDiscountTicket, sau khi FinalizeLootProp(unit.DiscountTicket) thành công,
    // thêm CreateDiscountTicketBlip(unit);

    private void SpawnDiscountTicket(ConvoyUnit unit, Vector3 center)
    {
        if (unit == null || unit.DiscountTicketSpawned)
            return;

        if (_rng.Next(100) >= DiscountTicketDropChancePercent)
            return;

        if (!TryFindSafeLootSpawnPoint(center, 2.2f, 5.0f, out Vector3 ticketSpawn))
            return;

        try
        {
            Model ticketModel = new Model("prop_cs_documents_01");
            if (!ticketModel.IsValid || !ticketModel.IsInCdImage)
                ticketModel = new Model("prop_ld_case_01");

            RequestModel(ticketModel);
            if (!ticketModel.IsLoaded)
                return;

            unit.DiscountTicket = World.CreateProp(ticketModel, ticketSpawn, true, false);
            if (unit.DiscountTicket == null || !unit.DiscountTicket.Exists())
            {
                unit.DiscountTicket = null;
                return;
            }

            if (!FinalizeLootProp(unit.DiscountTicket))
            {
                CleanupUnitDiscountTicket(unit);
                return;
            }

            CreateDiscountTicketBlip(unit);
            unit.DiscountTicketSpawned = true;
        }
        catch
        {
            CleanupUnitDiscountTicket(unit);
        }
    }

    // Thêm helper rải loot không chồng nhau
    private bool TryFindDistinctSafeLootSpawnPoint(
        Vector3 center,
        float minDistance,
        float maxDistance,
        List<Vector3> occupied,
        float minSeparation,
        out Vector3 spawnPos)
    {
        spawnPos = Vector3.Zero;

        for (int attempt = 0; attempt < LootSpawnAttempts; attempt++)
        {
            if (!TryFindSafeLootSpawnPoint(center, minDistance, maxDistance, out spawnPos))
                return false;

            bool overlaps = false;

            if (occupied != null)
            {
                float minSepSq = minSeparation * minSeparation;
                for (int i = 0; i < occupied.Count; i++)
                {
                    if (occupied[i].DistanceToSquared(spawnPos) < minSepSq)
                    {
                        overlaps = true;
                        break;
                    }
                }
            }

            if (!overlaps)
                return true;
        }

        return false;
    }

    // Thêm helper số tiền cho từng loại prop
    private int GetCashLootAmount(string modelName)
    {
        switch (modelName)
        {
            case "prop_cash_pile_01":
                return 250;

            case "prop_cash_pile_02":
                return 500;

            case "prop_money_bag_01":
                return 1000;

            case "prop_ld_banknotes_01":
                return 150;

            default:
                return 250;
        }
    }

    private void CollectUnitDiscountTicket(ConvoyUnit unit)
    {
        if (unit == null || unit.DiscountTicket == null || !unit.DiscountTicket.Exists())
            return;

        _vehicleDiscountTicketCount++;
        SaveVehicleDiscountTicketState();

        try { unit.DiscountTicket.Delete(); } catch { }
        unit.DiscountTicket = null;
        unit.DiscountTicketSpawned = false;

        PlaySoundFrontend("PICK_UP", "HUD_FRONTEND_DEFAULT_SOUNDSET");

        string msg = Language.ReplaceTokens(
            Language.Get(
                "MoneyTruckEvent_DiscountTicketMessage",
                "~HUD_COLOUR_DEGEN_GREEN~Bạn đã nhặt được thẻ giảm giá. Hiện đang có {count} thẻ."),
            "{count}", _vehicleDiscountTicketCount.ToString("N0", CultureInfo.InvariantCulture));

        Notification.Show(msg);
    }

    private void TryRollSpawn()
    {
        if (_eventActive)
            return;

        if (_rng.Next(100) >= SpawnChancePercent)
            return;

        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
            return;

        if (TryFindSpawnPointNearPlayer(player.Position, out Vector3 spawnPos, out float heading))
        {
            SpawnEvent(spawnPos, heading);
        }
    }

    private bool TryFindSpawnPointNearPlayer(Vector3 origin, out Vector3 spawnPos, out float heading)
    {
        spawnPos = Vector3.Zero;
        heading = 0f;

        try
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                float dist = (float)(260.0 + _rng.NextDouble() * 260.0);
                float angle = (float)(_rng.NextDouble() * Math.PI * 2.0);

                Vector3 guess = origin + new Vector3(
                    (float)Math.Cos(angle) * dist,
                    (float)Math.Sin(angle) * dist,
                    0f);

                OutputArgument nodePos = new OutputArgument();
                OutputArgument nodeHeading = new OutputArgument();

                bool found = Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
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

                if (!IsPointTooCloseToPlayer(spawnPos))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private bool IsPointTooCloseToPlayer(Vector3 pos)
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return true;
            return player.Position.DistanceToSquared(pos) < 80f * 80f;
        }
        catch
        {
            return true;
        }
    }

    private long GetRandomRewardPoints()
    {
        return _rng.Next((int)RewardPointsMin, (int)RewardPointsMax + 1);
    }

    private void EnsureRelationshipGroups()
    {
        if (_relationshipGroupsReady)
            return;

        try
        {
            _guardRelationshipGroupHash = Function.Call<int>(Hash.GET_HASH_KEY, GuardRelationshipGroupName);
            _playerRelationshipGroupHash = Function.Call<int>(Hash.GET_HASH_KEY, PlayerRelationshipGroupName);

            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 0, _guardRelationshipGroupHash, _guardRelationshipGroupHash);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 0, _playerRelationshipGroupHash, _playerRelationshipGroupHash);

            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, _guardRelationshipGroupHash, _playerRelationshipGroupHash);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, _playerRelationshipGroupHash, _guardRelationshipGroupHash);

            _relationshipGroupsReady = true;
        }
        catch { }
    }

    private void SpawnEvent(Vector3 spawnPos, float heading)
    {
        CleanupEvent(true, false, false);

        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
            return;

        EnsureRelationshipGroups();

        try
        {
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, player.Handle, _playerRelationshipGroupHash);
        }
        catch { }

        if (!TryFindDriveTarget(spawnPos, out _convoyDriveTarget))
            return;

        Vector3 forward = GetForwardVector(heading);
        Vector3 secondSpawnPos = spawnPos - (forward * ConvoySpacing);
        secondSpawnPos.Z = spawnPos.Z;

        ConvoyUnit leader = CreateConvoyUnit(spawnPos, heading, false);
        if (leader == null)
        {
            CleanupEvent(true, false, false);
            return;
        }

        ConvoyUnit follower = CreateConvoyUnit(secondSpawnPos, heading, true);
        if (follower == null)
        {
            CleanupEvent(true, false, false);
            return;
        }

        _convoyUnits.Add(leader);
        _convoyUnits.Add(follower);

        _eventActive = true;
        _alerted = false;
        _eventStartTime = Game.GameTime;

        StartConvoyMovement();

        ShowIntroNotification();

        UpdateTruckBlips();
        UpdateGuardBlips();

        UpdateLastHealthSnapshot();
    }

    private ConvoyUnit CreateConvoyUnit(Vector3 spawnPos, float heading, bool isFollower)
    {
        uint modelHash = TruckModels[_rng.Next(TruckModels.Length)];
        Model truckModel = new Model((int)modelHash);
        if (!truckModel.IsValid || !truckModel.IsInCdImage)
            return null;

        RequestModel(truckModel);
        if (!truckModel.IsLoaded)
            return null;

        Vehicle truck = null;
        try
        {
            truck = World.CreateVehicle(truckModel, spawnPos, heading);
        }
        catch
        {
            truck = null;
        }

        if (truck == null || !truck.Exists())
            return null;

        ConvoyUnit unit = new ConvoyUnit();
        unit.Truck = truck;
        unit.TruckPos = spawnPos;
        unit.TruckHeading = heading;
        unit.DriveSpeed = isFollower ? FollowerDriveSpeed : LeaderDriveSpeed;
        unit.RewardPoints = GetRandomRewardPoints();

        try { Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, unit.Truck.Handle, true, true); } catch { }
        try { unit.Truck.IsPersistent = true; } catch { }
        try { unit.Truck.IsInvincible = false; } catch { }
        try { unit.Truck.DirtLevel = 0f; } catch { }
        try { unit.Truck.PlaceOnGround(); } catch { }
        try { unit.Truck.IsEngineRunning = true; } catch { }
        try { unit.Truck.NeedsToBeHotwired = false; } catch { }
        try { unit.Truck.CanBeVisiblyDamaged = true; } catch { }

        if (!CreateGuards(unit))
        {
            try { unit.Truck.Delete(); } catch { }
            return null;
        }

        CreateTruckBlip(unit);

        unit.LastTruckHealth = SafeHealth(unit.Truck);
        unit.LastDriverHealth = SafeHealth(unit.Driver);
        unit.LastPassengerHealth = SafeHealth(unit.Passenger);

        return unit;
    }

    private bool CreateGuards(ConvoyUnit unit)
    {
        if (unit == null || unit.Truck == null || !unit.Truck.Exists())
            return false;

        Ped player = Game.Player.Character;
        if (player == null || !player.Exists())
            return false;

        Model guardModel = new Model("s_m_m_highsec_01");
        if (!guardModel.IsValid || !guardModel.IsInCdImage)
            guardModel = new Model("s_m_m_security_01");
        if (!guardModel.IsValid || !guardModel.IsInCdImage)
            guardModel = new Model("a_m_y_business_01");

        RequestModel(guardModel);
        if (!guardModel.IsLoaded)
            return false;

        PickTwoDifferentWeapons(out unit.DriverWeapon, out unit.PassengerWeapon);

        try
        {
            unit.Driver = World.CreatePed(guardModel, unit.Truck.GetOffsetPosition(new Vector3(-2.0f, -3.5f, 0.2f)));
            unit.Passenger = World.CreatePed(guardModel, unit.Truck.GetOffsetPosition(new Vector3(2.0f, -3.5f, 0.2f)));
        }
        catch
        {
            unit.Driver = null;
            unit.Passenger = null;
            return false;
        }

        if (unit.Driver == null || !unit.Driver.Exists() || unit.Passenger == null || !unit.Passenger.Exists())
            return false;

        SetupGuard(unit.Driver, unit.DriverWeapon);
        SetupGuard(unit.Passenger, unit.PassengerWeapon);

        try { unit.Driver.Task.WarpIntoVehicle(unit.Truck, VehicleSeat.Driver); } catch { }
        try { unit.Passenger.Task.WarpIntoVehicle(unit.Truck, VehicleSeat.Passenger); } catch { }

        return true;
    }

    private void PickTwoDifferentWeapons(out uint weaponA, out uint weaponB)
    {
        int first = _rng.Next(GuardWeapons.Length);
        int second = _rng.Next(GuardWeapons.Length - 1);
        if (second >= first)
            second++;

        weaponA = GuardWeapons[first];
        weaponB = GuardWeapons[second];
    }

    private void SetupGuard(Ped ped, uint weaponHash)
    {
        if (ped == null || !ped.Exists())
            return;

        EnsureRelationshipGroups();

        try { Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, ped.Handle, true, true); } catch { }
        try { ped.IsPersistent = true; } catch { }
        try { ped.BlockPermanentEvents = true; } catch { }
        try { ped.CanRagdoll = true; } catch { }
        try { ped.CanSufferCriticalHits = false; } catch { }
        try { ped.Armor = 100; } catch { }
        try { ped.Health = 200; } catch { }

        try { Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, ped.Handle, _guardRelationshipGroupHash); } catch { }
        ApplyGuardAntiFriendlyFire(ped);

        try { Function.Call(Hash.SET_PED_DROPS_WEAPONS_WHEN_DEAD, ped.Handle, false); } catch { }
        try { Function.Call(Hash.SET_PED_ACCURACY, ped.Handle, 55); } catch { }

        try { ped.Weapons.Give((WeaponHash)weaponHash, 9999, true, true); } catch { }
        try { Function.Call(Hash.SET_CURRENT_PED_WEAPON, ped.Handle, weaponHash, true); } catch { }
    }

    private void StripDeadGuardWeapons(ConvoyUnit unit)
    {
        if (unit == null)
            return;

        try
        {
            if (unit.Driver != null && unit.Driver.Exists() && unit.Driver.IsDead)
                Function.Call(Hash.REMOVE_ALL_PED_WEAPONS, unit.Driver.Handle, true);
        }
        catch { }

        try
        {
            if (unit.Passenger != null && unit.Passenger.Exists() && unit.Passenger.IsDead)
                Function.Call(Hash.REMOVE_ALL_PED_WEAPONS, unit.Passenger.Handle, true);
        }
        catch { }
    }

    private void StartConvoyMovement()
    {
        if (_convoyUnits.Count == 0)
            return;

        for (int i = 0; i < _convoyUnits.Count; i++)
        {
            ConvoyUnit unit = _convoyUnits[i];
            if (unit == null || unit.Truck == null || !unit.Truck.Exists() || unit.Driver == null || !unit.Driver.Exists())
                continue;

            try
            {
                Function.Call(Hash.SET_PED_KEEP_TASK, unit.Driver.Handle, true);
            }
            catch { }

            try
            {
                Function.Call(Hash.SET_DRIVER_ABILITY, unit.Driver.Handle, 1.0f);
            }
            catch { }

            try
            {
                Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, unit.Driver.Handle, 0.0f);
            }
            catch { }

            try
            {
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE,
                    unit.Driver.Handle,
                    unit.Truck.Handle,
                    _convoyDriveTarget.X, _convoyDriveTarget.Y, _convoyDriveTarget.Z,
                    unit.DriveSpeed,
                    786603,
                    5.0f);
            }
            catch
            {
                try
                {
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, unit.Driver.Handle, unit.Truck.Handle, unit.DriveSpeed, 786603);
                }
                catch { }
            }

            try { unit.Truck.IsEngineRunning = true; } catch { }
        }
    }

    private bool TryFindDriveTarget(Vector3 origin, out Vector3 target)
    {
        target = Vector3.Zero;

        try
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                float dist = (float)(350.0 + _rng.NextDouble() * 250.0);
                float angle = (float)(_rng.NextDouble() * Math.PI * 2.0);

                Vector3 guess = origin + new Vector3(
                    (float)Math.Cos(angle) * dist,
                    (float)Math.Sin(angle) * dist,
                    0f);

                OutputArgument nodePos = new OutputArgument();
                OutputArgument nodeHeading = new OutputArgument();

                bool found = Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                    guess.X, guess.Y, guess.Z + 50f,
                    nodePos, nodeHeading,
                    1, 3.0f, 0);

                if (!found)
                    continue;

                Vector3 p = nodePos.GetResult<Vector3>();

                float groundZ = p.Z;
                try
                {
                    OutputArgument zArg = new OutputArgument();
                    if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, p.X, p.Y, p.Z + 80f, zArg, false))
                        groundZ = zArg.GetResult<float>();
                }
                catch { }

                target = new Vector3(p.X, p.Y, groundZ + 0.5f);
                return true;
            }
        }
        catch { }

        return false;
    }

    private void UpdateActiveEvent()
    {
        if (Game.GameTime - _eventStartTime >= EventExpireMs)
        {
            CleanupEvent(true, true, true);
            return;
        }

        Ped player = Game.Player.Character;
        if (player == null || !player.Exists() || player.IsDead)
        {
            CleanupEvent(true, true, true);
            return;
        }

        if (!_alerted)
        {
            if (IsPlayerAimingAtAnyGuard() || AnyUnitUnderAttack())
            {
                TriggerAlert();
            }
        }

        if (_alerted)
            UpdateEnemyBehavior(player);

        UpdateGuardBlips();
        UpdateTruckBlips();

        for (int i = 0; i < _convoyUnits.Count; i++)
        {
            ConvoyUnit unit = _convoyUnits[i];
            if (unit == null)
                continue;

            StripDeadGuardWeapons(unit);

            if (IsUnitTruckDestroyed(unit))
            {
                if (!unit.LootSpawned)
                    SpawnUnitLoot(unit);
            }
        }

        AutoCollectDroppedLootIfTouching(player);

        if (AllUnitsResolved())
        {
            CleanupEvent(true, true, true);
            return;
        }
    }

    private void UpdateLastHealthSnapshot()
    {
        for (int i = 0; i < _convoyUnits.Count; i++)
        {
            ConvoyUnit unit = _convoyUnits[i];
            if (unit == null)
                continue;

            unit.LastTruckHealth = SafeHealth(unit.Truck);
            unit.LastDriverHealth = SafeHealth(unit.Driver);
            unit.LastPassengerHealth = SafeHealth(unit.Passenger);
        }
    }

    private bool AnyUnitUnderAttack()
    {
        bool changed = false;

        for (int i = 0; i < _convoyUnits.Count; i++)
        {
            ConvoyUnit unit = _convoyUnits[i];
            if (unit == null)
                continue;

            int currentTruckHealth = SafeHealth(unit.Truck);
            int currentDriverHealth = SafeHealth(unit.Driver);
            int currentPassengerHealth = SafeHealth(unit.Passenger);

            if (unit.LastTruckHealth >= 0 && currentTruckHealth >= 0 && currentTruckHealth < unit.LastTruckHealth)
                changed = true;
            if (unit.LastDriverHealth >= 0 && currentDriverHealth >= 0 && currentDriverHealth < unit.LastDriverHealth)
                changed = true;
            if (unit.LastPassengerHealth >= 0 && currentPassengerHealth >= 0 && currentPassengerHealth < unit.LastPassengerHealth)
                changed = true;

            unit.LastTruckHealth = currentTruckHealth;
            unit.LastDriverHealth = currentDriverHealth;
            unit.LastPassengerHealth = currentPassengerHealth;
        }

        return changed;
    }

    private void TriggerAlert()
    {
        if (_alerted)
            return;

        _alerted = true;
        SetWantedLevel(WantedStars);
        PlaySoundFrontend("5_SEC_WARNING", "HUD_MINI_GAME_SOUNDSET");

        UpdateGuardBlips();
    }

    private void UpdateEnemyBehavior(Ped player)
    {
        for (int i = 0; i < _convoyUnits.Count; i++)
        {
            ConvoyUnit unit = _convoyUnits[i];
            if (unit == null)
                continue;

            UpdateUnitGuardBehavior(unit, player);
        }
    }

    private void UpdateUnitGuardBehavior(ConvoyUnit unit, Ped player)
    {
        try
        {
            if (unit.Driver != null && unit.Driver.Exists() && !unit.Driver.IsDead)
            {
                if (unit.Truck != null && unit.Truck.Exists() && unit.Driver.IsInVehicle(unit.Truck))
                {
                    try { Function.Call(Hash.TASK_LEAVE_VEHICLE, unit.Driver.Handle, unit.Truck.Handle, 0); } catch { }
                }
                else
                {
                    try { Function.Call(Hash.TASK_COMBAT_PED, unit.Driver.Handle, player.Handle, 0, 16); } catch { }
                }

                try { Function.Call(Hash.SET_CURRENT_PED_WEAPON, unit.Driver.Handle, unit.DriverWeapon, true); } catch { }
            }

            if (unit.Passenger != null && unit.Passenger.Exists() && !unit.Passenger.IsDead)
            {
                if (unit.Truck != null && unit.Truck.Exists() && unit.Passenger.IsInVehicle(unit.Truck))
                {
                    try { Function.Call(Hash.TASK_LEAVE_VEHICLE, unit.Passenger.Handle, unit.Truck.Handle, 0); } catch { }
                }
                else
                {
                    try { Function.Call(Hash.TASK_COMBAT_PED, unit.Passenger.Handle, player.Handle, 0, 16); } catch { }
                }

                try { Function.Call(Hash.SET_CURRENT_PED_WEAPON, unit.Passenger.Handle, unit.PassengerWeapon, true); } catch { }
            }
        }
        catch { }
    }

    private bool IsUnitTruckDestroyed(ConvoyUnit unit)
    {
        try
        {
            if (unit == null || unit.Truck == null || !unit.Truck.Exists())
                return true;

            if (unit.Truck.IsDead)
                return true;

            if (unit.Truck.Health <= 0)
                return true;

            return false;
        }
        catch
        {
            return true;
        }
    }

    private void SpawnUnitLoot(ConvoyUnit unit)
    {
        if (unit == null || unit.LootSpawned)
            return;

        Vector3 center = unit.Truck != null && unit.Truck.Exists() ? unit.Truck.Position : unit.TruckPos;

        if (!TryFindSafeLootSpawnPoint(center, 8.5f, 10.0f, out Vector3 rewardSpawn))
            return;

        if (!TryFindSafeLootSpawnPoint(center, 10.0f, 12.0f, out Vector3 cashSpawn))
            return;

        try
        {
            Model rewardModel = new Model("prop_security_case_01");
            if (!rewardModel.IsValid || !rewardModel.IsInCdImage)
                rewardModel = new Model("prop_ld_case_01");

            RequestModel(rewardModel);
            if (!rewardModel.IsLoaded)
                return;

            unit.Crate = World.CreateProp(rewardModel, rewardSpawn, true, false);
            if (unit.Crate == null || !unit.Crate.Exists())
                return;

            if (!FinalizeLootProp(unit.Crate))
            {
                CleanupUnitLoot(unit);
                return;
            }
        }
        catch
        {
            CleanupUnitLoot(unit);
            return;
        }

        try
        {
            Model cashModel = new Model("prop_security_case_01");
            if (!cashModel.IsValid || !cashModel.IsInCdImage)
                cashModel = new Model("prop_ld_case_01");

            RequestModel(cashModel);
            if (!cashModel.IsLoaded)
            {
                CleanupUnitLoot(unit);
                return;
            }

            unit.CashCrate = World.CreateProp(cashModel, cashSpawn, true, false);
            if (unit.CashCrate == null || !unit.CashCrate.Exists())
            {
                CleanupUnitLoot(unit);
                return;
            }

            if (!FinalizeLootProp(unit.CashCrate))
            {
                CleanupUnitLoot(unit);
                return;
            }
        }
        catch
        {
            CleanupUnitLoot(unit);
            return;
        }

        unit.LootSpawned = true;

        DeleteTruckBlip(unit);
        CreateCrateBlip(unit);
        CreateCashCrateBlip(unit);

        SpawnCashProps(unit, center);
        SpawnDiscountTicket(unit, center);
    }

    private void CleanupUnitLoot(ConvoyUnit unit)
    {
        try
        {
            if (unit.Crate != null && unit.Crate.Exists())
                unit.Crate.Delete();
        }
        catch { }
        unit.Crate = null;

        try
        {
            if (unit.CashCrate != null && unit.CashCrate.Exists())
                unit.CashCrate.Delete();
        }
        catch { }
        unit.CashCrate = null;

        DeleteCashProps(unit);
        CleanupUnitDiscountTicket(unit);
    }

    // Sửa SpawnCashProps thành dạng rải tách nhau và có tiền thật
    private void SpawnCashProps(ConvoyUnit unit, Vector3 center)
    {
        DeleteCashProps(unit);

        int count = _rng.Next(10, 18);
        List<Vector3> occupied = new List<Vector3>();

        for (int i = 0; i < count; i++)
        {
            try
            {
                string modelName = CashPropModels[_rng.Next(CashPropModels.Length)];
                Model moneyModel = new Model(modelName);
                if (!moneyModel.IsValid || !moneyModel.IsInCdImage)
                    continue;

                RequestModel(moneyModel);
                if (!moneyModel.IsLoaded)
                    continue;

                if (!TryFindDistinctSafeLootSpawnPoint(center, 1.6f, 6.2f, occupied, CashLootMinSeparation, out Vector3 spawn))
                    continue;

                Prop money = World.CreateProp(moneyModel, spawn, true, false);
                if (money == null || !money.Exists())
                    continue;

                if (!FinalizeLootProp(money))
                {
                    try { money.Delete(); } catch { }
                    continue;
                }

                unit.CashProps.Add(new CashLootItem
                {
                    Prop = money,
                    Amount = GetCashLootAmount(modelName)
                });

                occupied.Add(spawn);
            }
            catch
            {
            }
        }
    }

    private void AutoCollectDroppedLootIfTouching(Ped player)
    {
        if (player == null || !player.Exists())
            return;

        float touchRadiusSq = LootTouchRadius * LootTouchRadius;

        for (int i = 0; i < _convoyUnits.Count; i++)
        {
            ConvoyUnit unit = _convoyUnits[i];
            if (unit == null)
                continue;

            if (unit.Crate != null && unit.Crate.Exists())
            {
                if (player.Position.DistanceToSquared(unit.Crate.Position) <= touchRadiusSq)
                {
                    CollectUnitCrate(unit);
                    if (AllUnitsResolved())
                        CleanupEvent(true, true, true);
                    return;
                }
            }

            if (unit.CashCrate != null && unit.CashCrate.Exists())
            {
                if (player.Position.DistanceToSquared(unit.CashCrate.Position) <= touchRadiusSq)
                {
                    CollectUnitCashCrate(unit);
                    if (AllUnitsResolved())
                        CleanupEvent(true, true, true);
                    return;
                }
            }

            if (unit.DiscountTicket != null && unit.DiscountTicket.Exists())
            {
                if (player.Position.DistanceToSquared(unit.DiscountTicket.Position) <= touchRadiusSq)
                {
                    CollectUnitDiscountTicket(unit);
                    if (AllUnitsResolved())
                        CleanupEvent(true, true, true);
                    return;
                }
            }

            // Sửa đoạn nhặt cash props trong AutoCollectDroppedLootIfTouching
            for (int j = unit.CashProps.Count - 1; j >= 0; j--)
            {
                CashLootItem item = unit.CashProps[j];
                if (item == null || item.Prop == null || !item.Prop.Exists())
                {
                    unit.CashProps.RemoveAt(j);
                    continue;
                }

                if (player.Position.DistanceToSquared(item.Prop.Position) <= touchRadiusSq)
                {
                    AddCashReward(item.Amount);

                    try { item.Prop.Delete(); } catch { }
                    unit.CashProps.RemoveAt(j);

                    PlaySoundFrontend("PICK_UP", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                }
            }
        }
    }

    private bool IsPlayerAimingAtAnyGuard()
    {
        try
        {
            int playerId = Function.Call<int>(Hash.PLAYER_ID);

            for (int i = 0; i < _convoyUnits.Count; i++)
            {
                ConvoyUnit unit = _convoyUnits[i];
                if (unit == null)
                    continue;

                if (IsPlayerAimingAtPed(playerId, unit.Driver) ||
                    IsPlayerAimingAtPed(playerId, unit.Passenger))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private bool IsPlayerAimingAtPed(int playerId, Ped ped)
    {
        try
        {
            if (ped == null || !ped.Exists() || ped.IsDead)
                return false;

            if (Function.Call<bool>(Hash.IS_PLAYER_FREE_AIMING_AT_ENTITY, playerId, ped.Handle))
                return true;

            if (Function.Call<bool>(Hash.IS_PLAYER_TARGETTING_ENTITY, playerId, ped.Handle))
                return true;
        }
        catch
        {
        }

        return false;
    }

    private bool TryFindSafeLootSpawnPoint(Vector3 center, float minDistance, float maxDistance, out Vector3 spawnPos)
    {
        spawnPos = Vector3.Zero;

        try
        {
            for (int attempt = 0; attempt < LootSpawnAttempts; attempt++)
            {
                float dist = minDistance + (float)_rng.NextDouble() * (maxDistance - minDistance);
                float angle = (float)(_rng.NextDouble() * Math.PI * 2.0);

                Vector3 guess = center + new Vector3(
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

                float groundZ = p.Z;
                try
                {
                    OutputArgument zArg = new OutputArgument();
                    bool ok = Function.Call<bool>(
                        Hash.GET_GROUND_Z_FOR_3D_COORD,
                        p.X, p.Y, p.Z + LootGroundProbeHeight,
                        zArg, false);

                    if (!ok)
                        continue;

                    groundZ = zArg.GetResult<float>();
                }
                catch
                {
                    continue;
                }

                spawnPos = new Vector3(p.X, p.Y, groundZ + LootSpawnZOffset);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private bool FinalizeLootProp(Prop prop)
    {
        try
        {
            if (prop == null || !prop.Exists())
                return false;

            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, prop.Handle, true, true);
            Function.Call(Hash.SET_ENTITY_VISIBLE, prop.Handle, true, 0);
            Function.Call(Hash.SET_ENTITY_COLLISION, prop.Handle, true, true);
            Function.Call(Hash.SET_ENTITY_ALPHA, prop.Handle, 255, false);
            Function.Call(Hash.PLACE_OBJECT_ON_GROUND_PROPERLY, prop.Handle);

            prop.IsPersistent = true;
            prop.IsPositionFrozen = true;

            return true;
        }
        catch
        {
            return false;
        }
    }

    // Sửa DeleteCashProps thành
    private void DeleteCashProps(ConvoyUnit unit)
    {
        try
        {
            for (int i = unit.CashProps.Count - 1; i >= 0; i--)
            {
                try
                {
                    CashLootItem item = unit.CashProps[i];
                    if (item != null && item.Prop != null && item.Prop.Exists())
                        item.Prop.Delete();
                }
                catch { }
            }
        }
        catch { }

        unit.CashProps.Clear();
    }

    private Vector3 GetForwardVector(float heading)
    {
        float rad = heading * (float)Math.PI / 180f;
        return new Vector3(-(float)Math.Sin(rad), (float)Math.Cos(rad), 0f);
    }

    private void CollectUnitCrate(ConvoyUnit unit)
    {
        if (unit == null || unit.Crate == null || !unit.Crate.Exists())
            return;

        AddRewardPoints(unit.RewardPoints);
        ShowRewardNotification(unit.RewardPoints);
        PlaySoundFrontend("PICK_UP", "HUD_FRONTEND_DEFAULT_SOUNDSET");

        try { unit.Crate.Delete(); } catch { }
        unit.Crate = null;
        DeleteCrateBlip(unit);
    }

    private void CollectUnitCashCrate(ConvoyUnit unit)
    {
        if (unit == null || unit.CashCrate == null || !unit.CashCrate.Exists())
            return;

        AddCashReward(CashRewardAmount);
        PlaySoundFrontend("PICK_UP", "HUD_FRONTEND_DEFAULT_SOUNDSET");

        try { unit.CashCrate.Delete(); } catch { }
        unit.CashCrate = null;
        DeleteCashCrateBlip(unit);
    }

    private bool AllUnitsResolved()
    {
        if (_convoyUnits.Count == 0)
            return false;

        for (int i = 0; i < _convoyUnits.Count; i++)
        {
            ConvoyUnit unit = _convoyUnits[i];
            if (unit == null)
                continue;

            // ✔️ thêm check ticket vào đây
            if (unit.DiscountTicket != null && unit.DiscountTicket.Exists())
                return false;

            if (unit.Truck != null && unit.Truck.Exists() && !unit.Truck.IsDead && unit.Truck.Health > 0)
                return false;

            if (!unit.LootSpawned)
                return false;

            if (unit.Crate != null && unit.Crate.Exists())
                return false;

            if (unit.CashCrate != null && unit.CashCrate.Exists())
                return false;

            if (unit.CashProps.Count > 0)
                return false;
        }

        return true;
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

    private void AddRewardPoints(long amount)
    {
        if (amount <= 0)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_rewardFilePath));
            long current = 0;
            if (File.Exists(_rewardFilePath))
            {
                string raw = File.ReadAllText(_rewardFilePath).Trim();
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out current);
            }

            current += amount;
            string tmp = _rewardFilePath + ".tmp";
            File.WriteAllText(tmp, current.ToString(CultureInfo.InvariantCulture));
            if (File.Exists(_rewardFilePath)) File.Delete(_rewardFilePath);
            File.Move(tmp, _rewardFilePath);
        }
        catch
        {
        }
    }

    private int SafeHealth(Entity e)
    {
        try
        {
            if (e == null || !e.Exists())
                return -1;
            return e.Health;
        }
        catch
        {
            return -1;
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

    private void ShowIntroNotification()
    {
        string message = Language.Get(
            "MoneyTruckEvent_IntroMessage",
            "~HUD_COLOUR_CONTROLLER_MICHAEL~Bạn có muốn kiếm chút đỉnh không? Có thấy 2 chiếc xe trên bản đồ không? Hãy đến chỗ của chúng!");

        Notification.Show(
            NotificationIcon.Lester,
            "Annonymous",
            "",
            message);
    }

    private void ShowRewardNotification(long points)
    {
        string message = Language.Get(
            "MoneyTruckEvent_RewardMessage",
            "~HUD_COLOUR_DEGEN_GREEN~Bạn đã nhận được {points} điểm thưởng.");

        message = Language.ReplaceTokens(
            message,
            "{points}", points.ToString("N0", CultureInfo.InvariantCulture));

        Notification.Show(message);
    }

    private void ApplyGuardAntiFriendlyFire(Ped ped)
    {
        if (ped == null || !ped.Exists())
            return;

        try
        {
            Function.Call(Hash.SET_CAN_ATTACK_FRIENDLY, ped.Handle, false, false);
        }
        catch { }

        try
        {
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, ped.Handle, _guardRelationshipGroupHash);
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
                while (!model.IsLoaded && waited < 2000)
                {
                    Script.Wait(50);
                    waited += 50;
                }
            }
        }
        catch { }
    }

    private void UpdateTruckBlips()
    {
        for (int i = 0; i < _convoyUnits.Count; i++)
        {
            ConvoyUnit unit = _convoyUnits[i];
            if (unit == null)
                continue;

            if (unit.LootSpawned)
                continue;

            if (unit.Truck == null || !unit.Truck.Exists())
            {
                DeleteTruckBlip(unit);
            }
            else
            {
                if (unit.TruckBlip == null || !unit.TruckBlip.Exists())
                    CreateTruckBlip(unit);
            }
        }
    }

    private void UpdateGuardBlips()
    {
        if (!_alerted)
            return;

        for (int i = 0; i < _convoyUnits.Count; i++)
        {
            ConvoyUnit unit = _convoyUnits[i];
            if (unit == null)
                continue;

            SyncGuardBlip(unit, true);
            SyncGuardBlip(unit, false);
        }
    }

    private void SyncGuardBlip(ConvoyUnit unit, bool isDriver)
    {
        try
        {
            Ped ped = isDriver ? unit.Driver : unit.Passenger;
            Blip blip = isDriver ? unit.DriverBlip : unit.PassengerBlip;

            if (ped == null || !ped.Exists() || ped.IsDead)
            {
                if (blip != null && blip.Exists())
                    blip.Delete();

                if (isDriver)
                    unit.DriverBlip = null;
                else
                    unit.PassengerBlip = null;

                return;
            }

            if (blip == null || !blip.Exists())
                CreateGuardBlip(unit, isDriver);
        }
        catch { }
    }

    private void CreateTruckBlip(ConvoyUnit unit)
    {
        try
        {
            if (unit == null || unit.Truck == null || !unit.Truck.Exists())
                return;

            unit.TruckBlip = unit.Truck.AddBlip();
            if (unit.TruckBlip != null)
            {
                unit.TruckBlip.Name = TruckBlipName;
                unit.TruckBlip.IsShortRange = false;
                unit.TruckBlip.Scale = 0.9f;
                unit.TruckBlip.ShowRoute = false;
                unit.TruckBlip.Color = BlipColor.Blue;
            }
        }
        catch { }
    }

    private void DeleteTruckBlip(ConvoyUnit unit)
    {
        try
        {
            if (unit != null && unit.TruckBlip != null && unit.TruckBlip.Exists())
                unit.TruckBlip.Delete();
        }
        catch { }

        if (unit != null)
            unit.TruckBlip = null;
    }

    private void CreateCrateBlip(ConvoyUnit unit)
    {
        try
        {
            if (unit == null || unit.Crate == null || !unit.Crate.Exists())
                return;

            unit.CrateBlip = unit.Crate.AddBlip();
            if (unit.CrateBlip != null)
            {
                unit.CrateBlip.Name = CrateBlipName;
                unit.CrateBlip.IsShortRange = false;
                unit.CrateBlip.Scale = 0.8f;
                unit.CrateBlip.Color = BlipColor.Green;
            }
        }
        catch { }
    }

    private void DeleteCrateBlip(ConvoyUnit unit)
    {
        try
        {
            if (unit != null && unit.CrateBlip != null && unit.CrateBlip.Exists())
                unit.CrateBlip.Delete();
        }
        catch { }

        if (unit != null)
            unit.CrateBlip = null;
    }

    private void CreateCashCrateBlip(ConvoyUnit unit)
    {
        try
        {
            if (unit == null || unit.CashCrate == null || !unit.CashCrate.Exists())
                return;

            unit.CashCrateBlip = unit.CashCrate.AddBlip();
            if (unit.CashCrateBlip != null)
            {
                unit.CashCrateBlip.Name = CashCrateBlipName;
                unit.CashCrateBlip.IsShortRange = false;
                unit.CashCrateBlip.Scale = 0.8f;
                unit.CashCrateBlip.Color = BlipColor.Blue;
            }
        }
        catch { }
    }

    private void DeleteCashCrateBlip(ConvoyUnit unit)
    {
        try
        {
            if (unit != null && unit.CashCrateBlip != null && unit.CashCrateBlip.Exists())
                unit.CashCrateBlip.Delete();
        }
        catch { }

        if (unit != null)
            unit.CashCrateBlip = null;
    }

    private void CreateGuardBlip(ConvoyUnit unit, bool isDriver)
    {
        try
        {
            if (unit == null)
                return;

            Ped ped = isDriver ? unit.Driver : unit.Passenger;
            if (ped == null || !ped.Exists() || ped.IsDead)
                return;

            Blip blip = isDriver ? unit.DriverBlip : unit.PassengerBlip;
            if (blip != null && blip.Exists())
                return;

            blip = ped.AddBlip();
            if (blip != null)
            {
                blip.Name = GuardBlipName;
                blip.IsShortRange = false;
                blip.Scale = 0.75f;
                blip.Color = BlipColor.Red;
                blip.ShowRoute = false;
            }

            if (isDriver)
                unit.DriverBlip = blip;
            else
                unit.PassengerBlip = blip;
        }
        catch { }
    }

    private void CleanupEvent(bool deleteLoot, bool keepDestroyedTruck, bool scheduleNextRoll)
    {
        for (int i = 0; i < _convoyUnits.Count; i++)
        {
            ConvoyUnit unit = _convoyUnits[i];
            if (unit == null)
                continue;

            try
            {
                if (unit.TruckBlip != null && unit.TruckBlip.Exists())
                    unit.TruckBlip.Delete();
            }
            catch { }
            unit.TruckBlip = null;

            try
            {
                if (unit.CrateBlip != null && unit.CrateBlip.Exists())
                    unit.CrateBlip.Delete();
            }
            catch { }
            unit.CrateBlip = null;

            try
            {
                if (unit.CashCrateBlip != null && unit.CashCrateBlip.Exists())
                    unit.CashCrateBlip.Delete();
            }
            catch { }
            unit.CashCrateBlip = null;

            try
            {
                if (unit.DriverBlip != null && unit.DriverBlip.Exists())
                    unit.DriverBlip.Delete();
            }
            catch { }
            unit.DriverBlip = null;

            try
            {
                if (unit.PassengerBlip != null && unit.PassengerBlip.Exists())
                    unit.PassengerBlip.Delete();
            }
            catch { }
            unit.PassengerBlip = null;

            // Sửa CleanupEvent để xóa luôn blip của thẻ giảm giá
            if (deleteLoot)
            {
                try
                {
                    if (unit.Crate != null && unit.Crate.Exists())
                        unit.Crate.Delete();
                }
                catch { }
                unit.Crate = null;

                try
                {
                    if (unit.CashCrate != null && unit.CashCrate.Exists())
                        unit.CashCrate.Delete();
                }
                catch { }
                unit.CashCrate = null;

                DeleteCashProps(unit);
                CleanupUnitDiscountTicket(unit);
            }
            else
            {
                CleanupUnitDiscountTicket(unit);
            }

            try
            {
                if (unit.Driver != null && unit.Driver.Exists())
                    unit.Driver.Delete();
            }
            catch { }
            unit.Driver = null;

            try
            {
                if (unit.Passenger != null && unit.Passenger.Exists())
                    unit.Passenger.Delete();
            }
            catch { }
            unit.Passenger = null;

            try
            {
                if (unit.Truck != null && unit.Truck.Exists())
                {
                    bool truckDestroyed = IsUnitTruckDestroyed(unit);

                    if (!keepDestroyedTruck || !truckDestroyed)
                    {
                        unit.Truck.Delete();
                    }
                    else
                    {
                        try { unit.Truck.IsPersistent = false; } catch { }
                    }
                }
            }
            catch { }
            unit.Truck = null;
        }

        _convoyUnits.Clear();

        _eventActive = false;
        _alerted = false;
        _eventStartTime = 0;
        _convoyDriveTarget = Vector3.Zero;

        if (scheduleNextRoll && _modReady)
            _nextRollTime = Game.GameTime + RollIntervalMs;
    }
}