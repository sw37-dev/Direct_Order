using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using System;
using System.Linq;

public class SimeonsShowroomFix : Script
{
    public static bool PdmEnabledForCurrentSession => PdmShowroomBridge.IsActive;

    private static readonly Vector3 PdmShowroomPos = new Vector3(-45.8048f, -1095.530f, 26.3272f);

    // NPC Simeon: tọa độ đúng theo yêu cầu
    // NPC Simeon: tọa độ đúng theo yêu cầu
    private static readonly Vector3 PdmNpcPos = new Vector3(-42.214610f, -1094.355000f, 26.422350f);
    private const float PdmNpcHeading = 125.4078f;
    private const float PdmNpcMenuRadius = 4.0f;

    private int _simeonNpcSpawnRequestedAt = -1;
    private const int PdmNpcSpawnWarmupMs = 800;

    private const float PdmActivationRadius = 12.0f;
    private const int PdmSessionLifetimeMs = 200000; // 200 giây

    // NEW: Simeon có thể từ chối nếu đang cúp điện
    private const int PdmBlackoutRejectChancePercent = 70;
    private const int PdmBlackoutRejectNoticeDelayMs = 3000;
    private const string PdmBlackoutRejectMessage =
        "Do hiện tại không có điện để sử dụng cho hoạt động kinh doanh nên cửa hàng sẽ tạm đóng cửa! Hãy quay lại sau nhé!";

    // PATCH: thêm 20 giây cảnh báo trước khi tự đóng
    private const int PdmCloseGraceDelayMs = 20000;

    // PATCH: thông báo khi sắp đóng cửa sau 200 giây
    private const string PdmClosingSoonNotice =
        "Premium Deluxe Motorsprot sắp đóng cửa rồi, nếu bạn đang bên trong thì hãy ra ngoài ngay!";

    public static readonly Vector3[] PdmDeliverySpawnPoints =
    {
        new Vector3(-53.630660f, -1117.313000f, 26.433740f),
        new Vector3(-50.500340f, -1117.416000f, 26.433410f),
        new Vector3(-56.282270f, -1117.515000f, 26.433600f),
    };

    private static readonly string[] PdmOpenIpls =
    {
        "hei_showroom_open",
        "hei_showroom_open_props",
        "shr_int",
        "csr_inMission",
        "shutter_open",
        "v_15_shrm_mesh_woodboard"
    };

    private static readonly string[] PdmClosedIpls =
    {
        "shutter_closed",
        "fakeint",
        "v_carshowroom"
    };

    private static readonly string[] PdmBaseIpls =
    {
        "v_carshowroom",
        "shr_int"
    };

    private const string PdmCloseFeedTitle = "Thông báo";
    private const string PdmCloseFeedMessage = "Premium Deluxe Motorsport đã đóng cửa hàng lại rồi!";

    private bool _pdmIplAppliedOpen = false;
    private bool _contactAdded = false;

    // Theo dõi trạng thái để chỉ bắn thông báo đúng 1 lần khi PDM chuyển từ mở sang đóng
    private bool _wasPdmActive = false;
    private int _trackedSessionStartedAt = -1;
    private bool _closeNoticeShownForSession = false;

    // NEW: thông báo từ chối do cúp điện hiển thị trễ 3 giây
    private int _pendingSimeonRejectNoticeAt = -1;
    private bool _pendingSimeonRejectNotice = false;

    // NPC Simeon đang đứng tại showroom
    private Ped _simeonNpc;

    private readonly Random _rng = new Random();

    public SimeonsShowroomFix()
    {
        Tick += OnTick;
        Interval = 1000; // giữ nhẹ tài nguyên
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            EnsurePdmContactRegistered();
            ProcessPendingSimeonRejectNotice();

            bool canOpenNow = IsPdmOpenNow(out _);

            // Nếu phiên đang mở và đã tới ngưỡng hết hạn thì bắn cảnh báo 1 lần
            // rồi gia hạn thêm 20 giây trước khi đóng thật.
            if (PdmShowroomBridge.IsActive &&
                Game.GameTime >= PdmShowroomBridge.ExpiresAtGameTime &&
                !PdmShowroomBridge.IsCloseGracePeriodArmed)
            {
                ShowPdmNotification(PdmCloseFeedTitle, PdmClosingSoonNotice);
                PdmShowroomBridge.StartCloseGracePeriod(PdmCloseGraceDelayMs);
            }

            // Hết thời gian gia hạn thì đóng thật
            if (PdmShowroomBridge.IsActive &&
                PdmShowroomBridge.IsCloseGracePeriodArmed &&
                Game.GameTime >= PdmShowroomBridge.CloseGraceUntilGameTime)
            {
                DeactivatePdmShowroom();
            }

            // Giữ logic cũ: ngoài giờ thì PDM không cho hoạt động
            // nhưng không ép đóng ngay trong 20 giây cảnh báo cuối phiên
            if (PdmShowroomBridge.IsActive && !canOpenNow && !PdmShowroomBridge.IsCloseGracePeriodArmed)
            {
                DeactivatePdmShowroom();
            }

            bool isActive = PdmShowroomBridge.IsActive;

            if (isActive)
            {
                if (!_wasPdmActive || _trackedSessionStartedAt != PdmShowroomBridge.ActivatedAtGameTime)
                {
                    _trackedSessionStartedAt = PdmShowroomBridge.ActivatedAtGameTime;
                    _closeNoticeShownForSession = false;
                }

                if (!_pdmIplAppliedOpen)
                    ApplyPdmOpenState();

                EnsureSimeonNpcSpawned();
                UpdateSimeonMenuState();

                _wasPdmActive = true;
                return;
            }

            // Phiên đã tắt: dọn NPC và menu request
            PdmShowroomBridge.ClearOpenMenuRequest();
            DeleteSimeonNpc();

            if (_wasPdmActive && !_closeNoticeShownForSession)
            {
                ShowPdmCloseNotification();
                _closeNoticeShownForSession = true;
            }

            if (_pdmIplAppliedOpen)
                ApplyPdmClosedState();

            _wasPdmActive = false;
            _trackedSessionStartedAt = -1;
        }
        catch
        {
        }
    }

    private bool IsPdmOpenNow(out string reason)
    {
        reason = "";

        try
        {
            int dow = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_WEEK); // 0 = CN, 1 = T2 ... 6 = T7
            int hour = Function.Call<int>(Hash.GET_CLOCK_HOURS);
            int minute = Function.Call<int>(Hash.GET_CLOCK_MINUTES);

            int timeMinutes = (hour * 60) + minute;

            bool open = false;

            // Thứ 2 -> Thứ 6
            if (dow >= 1 && dow <= 5)
            {
                open =
                    (timeMinutes >= (9 * 60) && timeMinutes <= (11 * 60 + 59)) ||
                    (timeMinutes >= (13 * 60) && timeMinutes < (18 * 60));
            }
            // Thứ 7
            else if (dow == 6)
            {
                open =
                    (timeMinutes >= (9 * 60) && timeMinutes <= (11 * 60 + 59)) ||
                    (timeMinutes >= (13 * 60) && timeMinutes < (15 * 60));
            }
            // Chủ Nhật
            else if (dow == 0)
            {
                open =
                    (timeMinutes >= (9 * 60) && timeMinutes <= (11 * 60 + 59));
            }

            return open;
        }
        catch
        {
            reason = "";
            return false;
        }
    }

    private void EnsurePdmContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            bool canOpenNow = IsPdmOpenNow(out _);

            var existing = phone.Contacts.FirstOrDefault(c =>
                string.Equals(c.Name, "Simeon", StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Active = canOpenNow;
                _contactAdded = true;
                return;
            }

            if (_contactAdded)
                return;

            var contact = new iFruitContact("Simeon")
            {
                Active = canOpenNow,
                DialTimeout = 2500,
                Bold = false,
                Icon = ContactIcon.Simeon
            };

            contact.Answered += OnPdmAnswered;
            phone.Contacts.Add(contact);
            _contactAdded = true;
        }
        catch
        {
        }
    }

    // Thay toàn bộ thông báo sang Notification.Show theo kiểu mẫu bạn đưa
    private void ShowPdmNotification(string title, string message, int timeout = 3000)
    {
        try
        {
            Notification.Show(NotificationIcon.Simeon, "Premium Deluxe Motorsport", title, message);
        }
        catch
        {
            try
            {
                Notification.Show($"{title}: {message}");
            }
            catch
            {
                GTA.UI.Screen.ShowSubtitle($"{title}: {message}", timeout);
            }
        }
    }

    private void ShowPdmCloseNotification()
    {
        ShowPdmNotification(PdmCloseFeedTitle, PdmCloseFeedMessage);
    }

    private void OnPdmAnswered(iFruitContact sender)
    {
        try
        {
            if (!IsPdmOpenNow(out _))
            {
                try
                {
                    sender.Active = false;
                }
                catch
                {
                }

                return;
            }

            // NEW: nếu đang cúp điện thì 70% từ chối, dù vẫn đang trong khung giờ hoạt động
            if (IsCityBlackoutActive() && _rng.Next(100) < PdmBlackoutRejectChancePercent)
            {
                try
                {
                    sender.Active = false;
                }
                catch
                {
                }

                ScheduleSimeonRejectNotification();
                return;
            }

            ActivatePdmShowroom();
            UpdateSimeonMenuState();

            // Nếu chưa đứng gần Simeon thì vẫn nhắc đi tới showroom,
            // còn nếu đã gần rồi thì không hiện thông báo này nữa.
            if (!IsPlayerNearSimeonNpc())
            {
                ShowPdmNotification("Đến showroom", "Cửa hàng đã mở rồi, bạn đi đến cửa hàng nha!");
            }
        }
        finally
        {
            try
            {
                CustomiFruit.GetCurrentInstance()?.Close(0);
            }
            catch
            {
            }
        }
    }

    private bool IsCityBlackoutActive()
    {
        try
        {
            return CityBlackoutHackerState.GetWorldPriceStateForCurrentCharacter()
                   != CityBlackoutHackerState.WorldPriceState.Normal;
        }
        catch
        {
            return false;
        }
    }

    private void ScheduleSimeonRejectNotification()
    {
        try
        {
            _pendingSimeonRejectNoticeAt = Game.GameTime + PdmBlackoutRejectNoticeDelayMs;
            _pendingSimeonRejectNotice = true;
        }
        catch
        {
        }
    }

    private void ProcessPendingSimeonRejectNotice()
    {
        try
        {
            if (!_pendingSimeonRejectNotice)
                return;

            if (Game.GameTime < _pendingSimeonRejectNoticeAt)
                return;

            _pendingSimeonRejectNotice = false;
            _pendingSimeonRejectNoticeAt = -1;

            ShowPdmNotification(PdmCloseFeedTitle, PdmBlackoutRejectMessage);
        }
        catch
        {
        }
    }

    private void ActivatePdmShowroom()
    {
        if (!IsPdmOpenNow(out _))
        {
            DeactivatePdmShowroom();
            return;
        }

        if (!PdmShowroomBridge.IsActive)
            PdmShowroomBridge.Activate(PdmSessionLifetimeMs);

        _simeonNpcSpawnRequestedAt = -1;
        ApplyPdmOpenState();
        EnsureSimeonNpcSpawned();
        UpdateSimeonMenuState();
    }

    private void DeactivatePdmShowroom()
    {
        PdmShowroomBridge.ClearOpenMenuRequest();
        DeleteSimeonNpc();
        ApplyPdmClosedState();
        PdmShowroomBridge.Deactivate();
    }

    private void ApplyPdmOpenState()
    {
        try
        {
            foreach (string ipl in PdmBaseIpls)
                Function.Call(Hash.REQUEST_IPL, ipl);

            foreach (string ipl in PdmOpenIpls)
                Function.Call(Hash.REQUEST_IPL, ipl);

            foreach (string ipl in PdmClosedIpls)
                Function.Call(Hash.REMOVE_IPL, ipl);

            RequestCollision();
            RefreshInterior();

            _pdmIplAppliedOpen = true;
        }
        catch
        {
        }
    }

    private void ApplyPdmClosedState()
    {
        try
        {
            foreach (string ipl in PdmBaseIpls)
                Function.Call(Hash.REQUEST_IPL, ipl);

            foreach (string ipl in PdmOpenIpls)
                Function.Call(Hash.REMOVE_IPL, ipl);

            foreach (string ipl in PdmClosedIpls)
                Function.Call(Hash.REQUEST_IPL, ipl);

            RequestCollision();
            RefreshInterior();

            _pdmIplAppliedOpen = false;
        }
        catch
        {
        }
    }

    private static void RequestCollision()
    {
        Function.Call(Hash.REQUEST_COLLISION_AT_COORD, PdmShowroomPos.X, PdmShowroomPos.Y, PdmShowroomPos.Z);
        Function.Call(Hash.REQUEST_ADDITIONAL_COLLISION_AT_COORD, PdmShowroomPos.X, PdmShowroomPos.Y, PdmShowroomPos.Z);

        // Chỉ request collision tại đúng tọa độ NPC, không chỉnh Z
        Function.Call(Hash.REQUEST_COLLISION_AT_COORD, PdmNpcPos.X, PdmNpcPos.Y, PdmNpcPos.Z);
        Function.Call(Hash.REQUEST_ADDITIONAL_COLLISION_AT_COORD, PdmNpcPos.X, PdmNpcPos.Y, PdmNpcPos.Z);
    }

    private static void RefreshInterior()
    {
        try
        {
            int interiorId = Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS, PdmShowroomPos.X, PdmShowroomPos.Y, PdmShowroomPos.Z);
            if (interiorId != 0 && Function.Call<bool>(Hash.IS_VALID_INTERIOR, interiorId))
            {
                Function.Call(Hash.PIN_INTERIOR_IN_MEMORY, interiorId);
                Function.Call(Hash.REFRESH_INTERIOR, interiorId);
            }
        }
        catch
        {
        }
    }

    private bool IsPlayerNearSimeonNpc()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists() || p.IsDead)
                return false;

            return p.Position.DistanceTo(PdmNpcPos) <= PdmNpcMenuRadius;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureSimeonNpcSpawned()
    {
        try
        {
            if (!PdmShowroomBridge.IsActive)
                return;

            if (_simeonNpc != null && _simeonNpc.Exists())
                return;

            // Lần đầu thấy cần spawn thì chỉ request collision trước
            if (_simeonNpcSpawnRequestedAt < 0)
            {
                _simeonNpcSpawnRequestedAt = Game.GameTime;
                RequestCollision();
                return;
            }

            // Chờ một chút để collision/interior ổn định rồi mới spawn
            if (Game.GameTime - _simeonNpcSpawnRequestedAt < PdmNpcSpawnWarmupMs)
            {
                RequestCollision();
                return;
            }

            var model = new Model("ig_siemonyetarian");
            if (!model.IsLoaded)
                model.Request(250);

            if (!model.IsLoaded)
                return;

            Ped ped = World.CreatePed(model, PdmNpcPos, PdmNpcHeading);
            if (ped == null || !ped.Exists())
                return;

            Function.Call(
                Hash.SET_ENTITY_COORDS_NO_OFFSET,
                ped.Handle,
                PdmNpcPos.X, PdmNpcPos.Y, PdmNpcPos.Z,
                false, false, false
            );

            Function.Call(Hash.SET_ENTITY_HEADING, ped.Handle, PdmNpcHeading);

            ped.IsPersistent = true;
            ped.BlockPermanentEvents = true;
            ped.IsInvincible = true;
            ped.CanRagdoll = false;

            Function.Call(Hash.FREEZE_ENTITY_POSITION, ped.Handle, true);

            try
            {
                ped.Task.StandStill(-1);
            }
            catch
            {
            }

            _simeonNpc = ped;
            _simeonNpcSpawnRequestedAt = -1;
        }
        catch
        {
        }
    }

    private void DeleteSimeonNpc()
    {
        try
        {
            PdmShowroomBridge.ClearOpenMenuRequest();

            if (_simeonNpc != null && _simeonNpc.Exists())
            {
                try
                {
                    _simeonNpc.MarkAsNoLongerNeeded();
                }
                catch
                {
                }

                try
                {
                    _simeonNpc.Delete();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        finally
        {
            _simeonNpc = null;
            _simeonNpcSpawnRequestedAt = -1;
        }
    }

    private void UpdateSimeonMenuState()
    {
        try
        {
            if (!PdmShowroomBridge.IsActive)
            {
                PdmShowroomBridge.ClearOpenMenuRequest();
                return;
            }

            if (IsPlayerNearSimeonNpc())
                PdmShowroomBridge.RequestOpenMenu();
            else
                PdmShowroomBridge.ClearOpenMenuRequest();
        }
        catch
        {
        }
    }
}

internal static class PdmShowroomBridge
{
    private static readonly object SyncRoot = new object();
    private static readonly Random _rng = new Random();

    public static bool IsActive { get; private set; } = false;
    public static int ActivatedAtGameTime { get; private set; } = -1;
    public static int ExpiresAtGameTime { get; private set; } = -1;

    private static bool _openMenuRequested = false;

    public static readonly Vector3 ShowroomPreviewPos = new Vector3(-45.8048f, -1095.530f, 26.3272f);
    public const float ShowroomPreviewHeading = 106.3412f;
    public const float ShowroomActivationRadius = 12.0f;

    public static int CloseGraceUntilGameTime { get; private set; } = -1;

    public static bool IsCloseGracePeriodArmed
    {
        get { return CloseGraceUntilGameTime > 0; }
    }

    public static void StartCloseGracePeriod(int delayMs)
    {
        lock (SyncRoot)
        {
            if (!IsActive)
                return;

            int safeDelay = Math.Max(1, delayMs);
            ExpiresAtGameTime = Game.GameTime + safeDelay;
            CloseGraceUntilGameTime = ExpiresAtGameTime;
        }
    }

    public static bool CanActivateNow(out string reason)
    {
        reason = "";

        try
        {
            int dow = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_WEEK);
            int hour = Function.Call<int>(Hash.GET_CLOCK_HOURS);
            int minute = Function.Call<int>(Hash.GET_CLOCK_MINUTES);

            int timeMinutes = (hour * 60) + minute;
            bool open = false;

            if (dow >= 1 && dow <= 5)
            {
                open =
                    (timeMinutes >= (9 * 60) && timeMinutes <= (11 * 60 + 59)) ||
                    (timeMinutes >= (13 * 60) && timeMinutes < (18 * 60));
            }
            else if (dow == 6)
            {
                open =
                    (timeMinutes >= (9 * 60) && timeMinutes <= (11 * 60 + 59)) ||
                    (timeMinutes >= (13 * 60) && timeMinutes < (15 * 60));
            }
            else if (dow == 0)
            {
                open = (timeMinutes >= (9 * 60) && timeMinutes <= (11 * 60 + 59));
            }

            if (!open)
                reason = "Premium Deluxe Motorsport hiện không hoạt động trong khung giờ này.";

            return open;
        }
        catch
        {
            reason = "Không thể xác định giờ hoạt động của Premium Deluxe Motorsport.";
            return false;
        }
    }

    public enum PdmOfferKind
    {
        None,
        Discount,
        PriceIncrease
    }

    public struct PdmOfferProfile
    {
        public PdmOfferKind Kind;
        public int ChancePercent;
        public double Multiplier;
        public string ItemTitle;
        public string ItemDescription;
    }

    private static PdmOfferProfile _currentOfferProfile = new PdmOfferProfile
    {
        Kind = PdmOfferKind.None,
        ChancePercent = 0,
        Multiplier = 1.0,
        ItemTitle = "",
        ItemDescription = ""
    };

    public static PdmOfferProfile CurrentOfferProfile
    {
        get
        {
            lock (SyncRoot)
                return _currentOfferProfile;
        }
    }

    public static bool TryRollVehicleOffer(Random rng, out PdmOfferProfile profile)
    {
        lock (SyncRoot)
        {
            profile = _currentOfferProfile;

            if (profile.Kind == PdmOfferKind.None || profile.ChancePercent <= 0)
                return false;

            if (rng == null)
                rng = new Random();

            return rng.Next(100) < profile.ChancePercent;
        }
    }

    private static int GetCurrentInGameDayOfWeek()
    {
        try
        {
            return Function.Call<int>(Hash.GET_CLOCK_DAY_OF_WEEK);
        }
        catch
        {
            return -1;
        }
    }

    private static PdmOfferProfile BuildProfileForDay(int dow)
    {
        // GTA day index: 0 = Chủ Nhật, 1 = Thứ 2, ..., 6 = Thứ 7
        switch (dow)
        {
            case 1: // Thứ 2
                return new PdmOfferProfile
                {
                    Kind = PdmOfferKind.Discount,
                    ChancePercent = 2,
                    Multiplier = 0.80,
                    ItemTitle = "Nhận sự ưu đãi",
                    ItemDescription = "Bạn nhận được sự ưu đãi hiếm từ Premium Deluxe Motorsport có thể giảm 20% giá trị phương tiện!!!!"
                };

            case 3: // Thứ 4
                return new PdmOfferProfile
                {
                    Kind = PdmOfferKind.Discount,
                    ChancePercent = 1,
                    Multiplier = 0.70,
                    ItemTitle = "Nhận sự ưu đãi",
                    ItemDescription = "Bạn đã nhận được sự ưu đãi cực hiếm từ Premium Deluxe Motorsport, giảm mạnh 30% giá trị của phương tiện!!!!"
                };

            case 5: // Thứ 6
                return new PdmOfferProfile
                {
                    Kind = PdmOfferKind.Discount,
                    ChancePercent = 10,
                    Multiplier = 0.875,
                    ItemTitle = "Nhận sự ưu đãi",
                    ItemDescription = "Premium Deluxe Motorsport có ưu đãi 12.5% dành cho bạn!!!"
                };

            case 0: // Chủ Nhật
            case 6: // Thứ 7
                return new PdmOfferProfile
                {
                    Kind = PdmOfferKind.Discount,
                    ChancePercent = 13,
                    Multiplier = 0.87,
                    ItemTitle = "Nhận sự ưu đãi",
                    ItemDescription = "Đây là sự ưu đãi của Premium Deluxe Motorsport dành cho bạn cho dịp cuối tuần, Premium Deluxe Motorsport giảm đến 13% giá trị đấy nhé!!!"
                };

            case 2: // Thứ 3
            case 4: // Thứ 5
                {
                    int chance = 33 + _rng.Next(23); // 33..55
                    return new PdmOfferProfile
                    {
                        Kind = PdmOfferKind.PriceIncrease,
                        ChancePercent = chance,
                        Multiplier = 1.25,
                        ItemTitle = "Chính sách tăng giá",
                        ItemDescription = string.Format(
                            "Do Premium Deluxe Motorsport nhập hàng khan hiếm nên hiện tại Premium Deluxe Motorsport tăng nhẹ giá trị của chiếc phương tiện này. Mong quý khách thông cảm!!!",
                            chance)
                    };
                }

            default:
                return new PdmOfferProfile
                {
                    Kind = PdmOfferKind.None,
                    ChancePercent = 0,
                    Multiplier = 1.0,
                    ItemTitle = "",
                    ItemDescription = ""
                };
        }
    }

    private static void RollPricingPolicyForCurrentDay()
    {
        lock (SyncRoot)
        {
            int dow = GetCurrentInGameDayOfWeek();
            _currentOfferProfile = BuildProfileForDay(dow);
        }
    }

    public static void Activate(int durationMs)
    {
        lock (SyncRoot)
        {
            if (!CanActivateNow(out _))
                return;

            if (IsActive && Game.GameTime < ExpiresAtGameTime)
                return;

            IsActive = true;
            ActivatedAtGameTime = Game.GameTime;
            ExpiresAtGameTime = Game.GameTime + Math.Max(1, durationMs);
            CloseGraceUntilGameTime = -1;
            _openMenuRequested = false;

            RollPricingPolicyForCurrentDay();
        }
    }

    public static void Deactivate()
    {
        lock (SyncRoot)
        {
            IsActive = false;
            ActivatedAtGameTime = -1;
            ExpiresAtGameTime = -1;
            CloseGraceUntilGameTime = -1;
            _openMenuRequested = false;

            _currentOfferProfile = new PdmOfferProfile
            {
                Kind = PdmOfferKind.None,
                ChancePercent = 0,
                Multiplier = 1.0,
                ItemTitle = "",
                ItemDescription = ""
            };
        }
    }

    // Nếu bên xử lý mua xe muốn gọi rõ ràng sau khi mua xong
    public static void NotifyVehiclePurchased()
    {
        Deactivate();
    }

    public static void RequestOpenMenu()
    {
        lock (SyncRoot)
        {
            if (IsActive)
                _openMenuRequested = true;
        }
    }

    public static void ClearOpenMenuRequest()
    {
        lock (SyncRoot)
        {
            _openMenuRequested = false;
        }
    }

    public static bool HasOpenMenuRequest()
    {
        lock (SyncRoot)
        {
            return _openMenuRequested;
        }
    }
}