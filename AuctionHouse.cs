using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using System;
using System.Globalization;
using System.Linq;

public partial class InstantRefill : Script
{
    private static string AuctionContactName => L("AuctionContactName", "Velocity Auctions");

    private const int AuctionRollIntervalMs = 105000;
    private const int AuctionDurationMs = 180000;
    private const int AuctionPriceTickMs = 20000;
    private const int AuctionBuyerCheckMs = 10000;
    private const float AuctionMinPriceFloor = 1000f;
    private const int AuctionCycleMinDays = 3;
    private const int AuctionCycleMaxDays = 4;
    private const int AUCTIONS_CALL_DURATION_MS = 3000;

    private const int AuctionSoldDeliveryDelayMs = 15000;
    private const double AuctionSoldDeliveryChance = 0.75d;

    private bool _auctionSoldDeliveryPending = false;
    private int _auctionSoldDeliveryDueAt = -1;

    private uint _auctionSoldVehicleHash = 0;
    private string _auctionSoldVehicleName = string.Empty;
    private int _auctionSoldVehicleClassId = -1;

    private sealed class AuctionSpawnPoint
    {
        public Vector3 Position;
        public float Heading;

        public AuctionSpawnPoint(float x, float y, float z, float heading)
        {
            Position = new Vector3(x, y, z);
            Heading = heading;
        }
    }

    private static readonly AuctionSpawnPoint[] AuctionPurchaseSpawnPoints = new[]
    {
        new AuctionSpawnPoint(-59.13711f, -1117.107f, 25.93202f, 359.8048f),
        new AuctionSpawnPoint(-50.64890f, -1116.615f, 25.81576f,   2.891143f),
        new AuctionSpawnPoint(-53.51005f, -1117.277f, 25.91357f,   3.144088f),
        new AuctionSpawnPoint(-61.93560f, -1116.743f, 25.95147f, 358.9644f),
        new AuctionSpawnPoint(-64.33164f, -1836.009f, 26.27896f, 339.4374f),
        new AuctionSpawnPoint(-57.32535f, -1844.958f, 25.59167f, 320.1644f),
        new AuctionSpawnPoint(-59.95718f, -1842.515f, 26.00023f, 317.7365f),
    };

    private enum AuctionPhase
    {
        None,
        WaitingForPriceUpdate,
        WaitingForBuyerCheck
    }

    private bool _auctionInitialized = false;
    private int _auctionDayCounter = 0;
    private int _auctionLastClockHour = -1;
    private int _auctionNextRollCheckGameTime = 0;
    private int _auctionNextEligibleDay = 0;

    private bool _auctionActive = false;
    private VehicleEntry _auctionVehicle = null;
    private int _auctionStartGameTime = 0;
    private int _auctionEndGameTime = 0;
    private int _auctionPhaseDueGameTime = 0;
    private int _auctionCurrentPrice = 0;
    private int _auctionStartingPrice = 0;
    private AuctionPhase _auctionPhase = AuctionPhase.None;

    private CustomiFruit _auctionPhoneInstance = null;
    private bool _auctionContactAdded = false;

    private sealed class AuctionBidder
    {
        public string Name;
        public int RollAtMs;
        public float Chance;
        public bool HasRolledThisCycle;

        public AuctionBidder(string name, int rollAtMs, float chance)
        {
            Name = name;
            RollAtMs = rollAtMs;
            Chance = chance;
            HasRolledThisCycle = false;
        }
    }

    private readonly AuctionBidder[] _auctionBidders = new[]
    {
        new AuctionBidder("Deal Hunter",  7000, 0.06f),
        new AuctionBidder("Anonymous", 10000, 0.13f),
        new AuctionBidder("Auction Expert", 15000, 0.09f),
        new AuctionBidder("The Tycoon", 18000, 0.11f),
    };

    private int _auctionCycleStartGameTime = 0;

    private void UpdateAuctionHouse()
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            EnsureAuctionTimeBase();
            EnsureAuctionHouseContactRegistered();

            UpdatePendingAuctionSoldDelivery();

            if (_auctionActive)
            {
                ProcessActiveAuction();
                return;
            }

            if (Game.GameTime < _auctionNextRollCheckGameTime)
                return;

            _auctionNextRollCheckGameTime = Game.GameTime + AuctionRollIntervalMs;

            if (!IsAuctionWindowOpen())
                return;

            if (_auctionDayCounter < _auctionNextEligibleDay)
                return;

            TryStartAuctionSession();
        }
        catch
        {
        }
    }

    private void ResetAuctionBidderCycle()
    {
        try
        {
            _auctionCycleStartGameTime = Game.GameTime;

            if (_auctionBidders == null)
                return;

            foreach (var bidder in _auctionBidders)
            {
                if (bidder != null)
                    bidder.HasRolledThisCycle = false;
            }
        }
        catch { }
    }

    private void ScheduleNextAuctionSession()
    {
        _auctionNextEligibleDay = _auctionDayCounter + _rng.Next(AuctionCycleMinDays, AuctionCycleMaxDays + 1);
        _auctionNextRollCheckGameTime = Game.GameTime + AuctionRollIntervalMs;
    }

    private void EnsureAuctionTimeBase()
    {
        try
        {
            int hour = SafeCall(() => Function.Call<int>(Hash.GET_CLOCK_HOURS), 0);

            if (!_auctionInitialized)
            {
                _auctionInitialized = true;
                _auctionLastClockHour = hour;
                _auctionDayCounter = 0;
                _auctionNextEligibleDay = _auctionDayCounter + _rng.Next(AuctionCycleMinDays, AuctionCycleMaxDays + 1);
                _auctionNextRollCheckGameTime = Game.GameTime + AuctionRollIntervalMs;
                return;
            }

            if (_auctionLastClockHour >= 0 && hour < _auctionLastClockHour)
            {
                _auctionDayCounter++;
            }

            _auctionLastClockHour = hour;
        }
        catch
        {
        }
    }

    private bool IsAuctionWindowOpen()
    {
        try
        {
            int hour = SafeCall(() => Function.Call<int>(Hash.GET_CLOCK_HOURS), 0);
            int minute = SafeCall(() => Function.Call<int>(Hash.GET_CLOCK_MINUTES), 0);

            if (hour < 17)
                return false;

            if (hour > 20)
                return false;

            if (hour == 20 && minute > 0)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ProcessAuctionBidderRolls()
    {
        try
        {
            if (!_auctionActive || _auctionVehicle == null)
                return;

            if (_auctionBidders == null || _auctionBidders.Length == 0)
                return;

            int elapsed = Game.GameTime - _auctionCycleStartGameTime;

            foreach (var bidder in _auctionBidders)
            {
                if (bidder == null || bidder.HasRolledThisCycle)
                    continue;

                if (elapsed < bidder.RollAtMs)
                    continue;

                bidder.HasRolledThisCycle = true;

                if (_rng.NextDouble() < bidder.Chance)
                {
                    EndAuctionSession(
                        false,
                        true,
                        string.Format(
                            L("Auction_EndByNpc", "Phiên đấu giá đã ~HUD_COLOUR_REDLIGHT~kết thúc~s~ vì {0} đã mua chiếc phương tiện này."),
                            bidder.Name
                        )
                    );
                    return;
                }
            }
        }
        catch
        {
        }
    }

    private void TryStartAuctionSession()
    {
        try
        {
            var list = GetActiveVehicleList();
            if (list == null || list.Count == 0)
                return;

            var chosen = list[_rng.Next(list.Count)];
            if (chosen == null)
                return;

            int startPrice = chosen.GetRandomPrice(_rng, false, 0);
            if (startPrice < (int)AuctionMinPriceFloor)
                startPrice = (int)AuctionMinPriceFloor;

            _auctionVehicle = chosen;
            _auctionStartingPrice = startPrice;
            _auctionCurrentPrice = startPrice;

            _auctionActive = true;
            _auctionPhase = AuctionPhase.WaitingForPriceUpdate;
            _auctionStartGameTime = Game.GameTime;
            _auctionEndGameTime = Game.GameTime + AuctionDurationMs;
            _auctionPhaseDueGameTime = Game.GameTime + AuctionPriceTickMs;

            ResetAuctionBidderCycle();
            PlayFrontendSound("LOCAL_PLYR_CASH_COUNTER_COMPLETE", "DLC_HEISTS_GENERAL_FRONTEND_SOUNDS");

            ShowAuctionBroadcast(
                 L("Auction_Subject", "Phiên đấu giá"),
                 string.Format(
                     L("Auction_StartBody",
                          "Hiện tại đang có một phiên đấu giá cho chiếc ~HUD_COLOUR_DEGEN_YELLOW~{0}~s~ với giá khởi điểm đang là ~HUD_COLOUR_DEGEN_GREEN~${1}~s~. Bạn đã được mời để tham gia chương trình này, hãy chốt giá theo số tiền bạn đang xem nhé!"),
                     chosen.Name ?? string.Empty,
                     _auctionCurrentPrice.ToString("N0")
                 )
            );
        }
        catch
        {
            EndAuctionSession(false, "Phiên đấu giá không thể khởi tạo.");
        }
    }

    private void ProcessActiveAuction()
    {
        try
        {
            if (!_auctionActive || _auctionVehicle == null)
                return;

            if (Game.GameTime >= _auctionEndGameTime)
            {
                EndAuctionSession(
                     false,
                     L("Auction_EndByTimeout", "Phiên đấu giá đã kết thúc do hết thời gian hiệu lực.")
                );
                return;
            }

            // Roll các NPC theo mốc trong vòng hiện tại
            ProcessAuctionBidderRolls();

            if (!_auctionActive || _auctionVehicle == null)
                return;

            // Chỉ khi hết 20 giây của vòng hiện tại mới đổi giá
            if (Game.GameTime < _auctionPhaseDueGameTime)
                return;

            int oldPrice = _auctionCurrentPrice;
            int deltaPct = _rng.Next(-25, 26);
            double factor = 1.0 + (deltaPct / 100.0);
            int newPrice = (int)Math.Round(oldPrice * factor, MidpointRounding.AwayFromZero);

            if (newPrice < (int)AuctionMinPriceFloor)
                newPrice = (int)AuctionMinPriceFloor;

            _auctionCurrentPrice = newPrice;

            ShowAuctionBroadcast(
                L("Auction_Subject", "Phiên đấu giá"),
                string.Format(
                    L("Auction_PriceUpdateBody",
                    "Giá mới của chiếc ~HUD_COLOUR_DEGEN_YELLOW~{0}~s~ hiện đang là ~HUD_COLOUR_DEGEN_GREEN~${1}~s~ ({2}% so với lần trước)."),
                    _auctionVehicle.Name ?? string.Empty,
                    _auctionCurrentPrice.ToString("N0"),
                    (deltaPct >= 0 ? "+" : "") + deltaPct.ToString(CultureInfo.InvariantCulture)
                )
            );

            _auctionPhase = AuctionPhase.WaitingForPriceUpdate;
            _auctionPhaseDueGameTime = Game.GameTime + AuctionPriceTickMs;

            // Bắt đầu vòng 20 giây mới
            ResetAuctionBidderCycle();
        }
        catch
        {
        }
    }

    private void EndAuctionSession(bool boughtByPlayer, string message)
    {
        EndAuctionSession(boughtByPlayer, false, message);
    }

    private void EndAuctionSession(bool boughtByPlayer, bool scheduleSoldVehicleDelivery, string message)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                ShowAuctionBroadcast("Phiên đấu giá", message);
            }
        }
        catch
        {
        }

        if (scheduleSoldVehicleDelivery && _auctionVehicle != null)
        {
            QueueAuctionSoldDelivery(_auctionVehicle);
        }

        _auctionActive = false;
        _auctionPhase = AuctionPhase.None;
        _auctionVehicle = null;

        _auctionStartGameTime = 0;
        _auctionEndGameTime = 0;
        _auctionPhaseDueGameTime = 0;

        _auctionStartingPrice = 0;
        _auctionCurrentPrice = 0;

        _auctionCycleStartGameTime = 0;

        // Quan trọng: không reset về 0 nữa, mà hẹn phiên tiếp theo sau 3-4 ngày
        ScheduleNextAuctionSession();
    }

    private void QueueAuctionSoldDelivery(VehicleEntry soldVehicle)
    {
        try
        {
            if (soldVehicle == null)
                return;

            _auctionSoldVehicleHash = soldVehicle.Hash;
            _auctionSoldVehicleName = soldVehicle.Name ?? string.Empty;
            _auctionSoldVehicleClassId = SafeCall(
                () => Function.Call<int>(Hash.GET_VEHICLE_CLASS_FROM_NAME, unchecked((int)soldVehicle.Hash)),
                -1
            );

            if (_auctionSoldVehicleClassId < 0)
                return;

            _auctionSoldDeliveryPending = true;
            _auctionSoldDeliveryDueAt = Game.GameTime + AuctionSoldDeliveryDelayMs;
        }
        catch
        {
        }
    }

    private void UpdatePendingAuctionSoldDelivery()
    {
        try
        {
            if (!_auctionSoldDeliveryPending || _auctionSoldDeliveryDueAt < 0)
                return;

            if (Game.GameTime < _auctionSoldDeliveryDueAt)
                return;

            if (DeliveryContractBridge.CurrentKind != DeliveryContractMissionKind.None)
                return;

            _auctionSoldDeliveryPending = false;
            _auctionSoldDeliveryDueAt = -1;

            // 50% mới kích hoạt
            if (_rng.NextDouble() >= AuctionSoldDeliveryChance)
                return;

            DeliveryContractMissionKind routeKind = ResolveAuctionDeliveryKind(_auctionSoldVehicleClassId);
            if (routeKind == DeliveryContractMissionKind.None)
                return;

            DeliveryContractBridge.RequestForcedMission(
                routeKind,
                _auctionSoldVehicleHash,
                _auctionSoldVehicleName,
                _auctionSoldVehicleClassId
            );

            // set đúng cờ khởi động cho từng script
            if (routeKind == DeliveryContractMissionKind.Vehicle)
            {
                DeliveryContractBridge.VehiclePendingStart = true;
            }
            else if (routeKind == DeliveryContractMissionKind.Plane)
            {
                PlaneDeliveryContractEvent.RequestSpawn();
            }
            else if (routeKind == DeliveryContractMissionKind.Submarine)
            {
                SubmarineDeliveryContractEvent.RequestSpawn();
            }

            ShowAuctionBroadcast(
                L("Auction_DeliverySubject", "Giao hàng"),
                string.Format(
                    L("Auction_DeliveryBody",
                    "Người vừa nãy trong phiên đấu giá đã mua chiếc ~HUD_COLOUR_DEGEN_YELLOW~{0}~s~. Họ muốn giao đến nhưng shop đang bận. Bạn muốn nhận đơn hàng này không?"),
                    _auctionSoldVehicleName ?? string.Empty
                )
            );
        }
        catch { }
    }

    private DeliveryContractMissionKind ResolveAuctionDeliveryKind(int vehicleClassId)
    {
        // class chuẩn của game:
        // 14 = Boats, 15 = Helicopters, 16 = Planes
        // còn lại -> giao xe
        switch (vehicleClassId)
        {
            case 14:
                return DeliveryContractMissionKind.Submarine;

            case 15:
            case 16:
                return DeliveryContractMissionKind.Plane;

            default:
                return DeliveryContractMissionKind.Vehicle;
        }
    }

    private void EnsureAuctionHouseContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_auctionPhoneInstance, phone))
            {
                _auctionPhoneInstance = phone;
                _auctionContactAdded = false;
            }

            if (_auctionContactAdded)
                return;

            if (phone.Contacts.Any(c => string.Equals(c.Name, AuctionContactName, StringComparison.OrdinalIgnoreCase)))
            {
                _auctionContactAdded = true;
                return;
            }

            var contact = new iFruitContact(AuctionContactName)
            {
                Active = true,
                DialTimeout = AUCTIONS_CALL_DURATION_MS,
                Bold = false,
                Icon = ContactIcon.SSASuperAutos
            };

            contact.Answered += OnAuctionHouseContactAnswered;
            phone.Contacts.Add(contact);

            _auctionContactAdded = true;
        }
        catch
        {
        }
    }

    private void OnAuctionHouseContactAnswered(iFruitContact sender)
    {
        try
        {
            if (_auctionActive)
            {
                TryBuyCurrentAuctionVehicle();
            }
            else
            {
                ShowAuctionBroadcast(
                    L("Auction_Title", "Phiên đấu giá"),
                    L("Auction_NoActiveBody", "Hiện chưa có phiên đấu giá nào đang diễn ra cả. Vui lòng quay lại sau vài ngày!")
                );
            }

            try
            {
                CustomiFruit.GetCurrentInstance()?.Close(0);
            }
            catch { }
        }
        catch { }
    }

    private bool TryBuyCurrentAuctionVehicle()
    {
        try
        {
            if (!_auctionActive || _auctionVehicle == null)
                return false;

            int price = Math.Max(0, _auctionCurrentPrice);
            if (price <= 0)
                return false;

            if (Game.Player.Money < price)
            {
                ShowAuctionBroadcast(
                    L("AuctTit","Phiên đấu giá"),
                    string.Format(
                        L("Auction_NotEnoughMoneyBody", "Bạn không đủ tiền để mua chiếc phương tiện này. Giá hiện tại là ~HUD_COLOUR_REDLIGHT~${0}."),
                        price.ToString("N0")
                    )
                );
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return false;
            }

            Vehicle spawned = SpawnAuctionVehicle(_auctionVehicle, price);
            if (spawned == null || !spawned.Exists())
            {
                ShowAuctionBroadcast(
                    L("AT", "Phiên đấu giá"),
                    L("Auction_SpawnFailedBody", "Không thể tạo phương tiện đấu giá ở vị trí giao phương tiện.")
                );
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return false;
            }

            Game.Player.Money -= price;
            AddToSpendingAccumulator(price);

            try
            {
                var meta = new PersistentManager.VehicleMeta
                {
                    PurchasePrice = price
                };
                PersistentManager.RegisterVehicle(spawned, meta);
            }
            catch
            {
                try { PersistentManager.RegisterVehicle(spawned); } catch { }
            }

            if (_auctionVehicle != null)
                _auctionVehicle.TimesPurchased = Math.Max(0, _auctionVehicle.TimesPurchased + 1);

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");

            ShowAuctionBroadcast(
                L("AuctionHouse_Title", "Phiên đấu giá"),
                string.Format(
                    L("Auction_PurchasedBody",
                    "Bạn đã mua thành công chiếc ~HUD_COLOUR_DEGEN_YELLOW~{0}~s~ với giá ~HUD_COLOUR_DEGEN_GREEN~${1}~s~. Phương tiện đã được giao tới bãi đỗ xe của bạn."),
                    _auctionVehicle.Name ?? string.Empty,
                    price.ToString("N0")
                )
            );

            EndAuctionSession(true, string.Empty);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private Vehicle SpawnAuctionVehicle(VehicleEntry chosen, int price)
    {
        try
        {
            if (chosen == null)
                return null;

            Model model = new Model((int)chosen.Hash);
            if (!model.IsValid || !model.IsInCdImage)
                return null;

            if (!model.IsLoaded)
            {
                model.Request(1500);
                int waited = 0;
                while (!model.IsLoaded && waited < 3000)
                {
                    Script.Wait(50);
                    waited += 50;
                }
            }

            if (!model.IsLoaded)
                return null;

            if (AuctionPurchaseSpawnPoints == null || AuctionPurchaseSpawnPoints.Length == 0)
                return null;

            AuctionSpawnPoint spawn = AuctionPurchaseSpawnPoints[_rng.Next(AuctionPurchaseSpawnPoints.Length)];
            if (spawn == null)
                return null;

            try
            {
                Function.Call(Hash.CLEAR_AREA,
                    spawn.Position.X, spawn.Position.Y, spawn.Position.Z,
                    4.0f, true, false, false, false);
            }
            catch
            {
            }

            Vehicle veh = null;
            try
            {
                veh = World.CreateVehicle(model, spawn.Position, spawn.Heading);
            }
            catch
            {
                veh = null;
            }

            if (veh == null || !veh.Exists())
                return null;

            try { veh.PlaceOnGround(); } catch { }
            try { veh.Heading = spawn.Heading; } catch { }
            try { veh.IsEngineRunning = false; } catch { }
            try { veh.DirtLevel = 0f; } catch { }
            try { veh.IsPersistent = true; } catch { }

            try
            {
                string plate = Helper.GenerateRandomPlate(_rng);
                if (!string.IsNullOrWhiteSpace(plate))
                    veh.Mods.LicensePlate = plate;
            }
            catch { }

            model.MarkAsNoLongerNeeded();
            return veh;
        }
        catch
        {
            return null;
        }
    }

    private void ShowAuctionBroadcast(string subject, string body)
    {
        try
        {
            if (!TryShowNotificationWithIcon(subject, body, "CHAR_CARSITE2", "Carsite2", "Carsite"))
            {
                Notification.Show(body);
            }
        }
        catch
        {
            try { Notification.Show(body); } catch { }
        }
    }

    private bool TryShowNotificationWithIcon(string subject, string body, params string[] preferredIcons)
    {
        try
        {
            var iconType = typeof(NotificationIcon);
            string[] names = Enum.GetNames(iconType);

            foreach (var preferred in preferredIcons)
            {
                if (string.IsNullOrWhiteSpace(preferred))
                    continue;

                string exact = names.FirstOrDefault(n => string.Equals(n, preferred, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(exact))
                {
                    var icon = (NotificationIcon)Enum.Parse(iconType, exact, true);
                    Notification.Show(icon, "SSA Super Autos", subject, body);
                    return true;
                }
            }

            foreach (var preferred in preferredIcons)
            {
                if (string.IsNullOrWhiteSpace(preferred))
                    continue;

                string partial = names.FirstOrDefault(n => n.IndexOf(preferred, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrWhiteSpace(partial))
                {
                    var icon = (NotificationIcon)Enum.Parse(iconType, partial, true);
                    Notification.Show(icon, "SSA Super Autos", subject, body);
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private bool TryAssignContactIcon(iFruitContact contact, params string[] preferredIcons)
    {
        try
        {
            if (contact == null) return false;

            // ContactIcon là class, không phải enum. Gán trực tiếp icon chuẩn của iFruitAddon2.
            contact.Icon = ContactIcon.SSASuperAutos;
            return true;
        }
        catch { }

        return false;
    }
}