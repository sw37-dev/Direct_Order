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
    private const float PdmActivationRadius = 12.0f;
    private const int PdmSessionLifetimeMs = 200000; // 200 giây

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
    private const string PdmCloseFeedMessage = "PDM đã đóng cửa lại rồi đấy nhá!";

    private bool _pdmIplAppliedOpen = false;
    private bool _contactAdded = false;

    // Theo dõi trạng thái để chỉ bắn thông báo đúng 1 lần khi PDM chuyển từ mở sang đóng
    private bool _wasPdmActive = false;
    private int _trackedSessionStartedAt = -1;
    private bool _closeNoticeShownForSession = false;

    public SimeonsShowroomFix()
    {
        Tick += OnTick;
        Interval = 1000; // tối ưu nhẹ tài nguyên hơn: không cần tick quá dày
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            EnsurePdmContactRegistered();

            bool isActive = PdmShowroomBridge.IsActive;

            if (isActive)
            {
                // Ghi nhận session hiện tại để phục vụ việc tránh thông báo lặp
                if (!_wasPdmActive || _trackedSessionStartedAt != PdmShowroomBridge.ActivatedAtGameTime)
                {
                    _trackedSessionStartedAt = PdmShowroomBridge.ActivatedAtGameTime;
                    _closeNoticeShownForSession = false;
                }

                if (!_pdmIplAppliedOpen)
                    ApplyPdmOpenState();

                _wasPdmActive = true;
                return;
            }

            // Nếu vừa chuyển từ mở sang đóng thì báo đúng 1 lần
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
        catch { }
    }

    private void EnsurePdmContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (_contactAdded)
                return;

            if (phone.Contacts.Any(c => string.Equals(c.Name, "PDM", StringComparison.OrdinalIgnoreCase)))
            {
                _contactAdded = true;
                return;
            }

            var contact = new iFruitContact("PDM")
            {
                Active = true,
                DialTimeout = 2500,
                Bold = false,
                Icon = new ContactIcon("WEB_PREMIUMDELUXEMOTORSPORT")
            };

            contact.Answered += OnPdmAnswered;
            phone.Contacts.Add(contact);
            _contactAdded = true;
        }
        catch
        {
        }
    }

    // Thông báo dạng tin nhắn của PDM
    private void ShowPdmNotification(string title, string message, int timeout = 3000)
    {
        try
        {
            Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, message);

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_MESSAGETEXT,
                "WEB_PREMIUMDELUXEMOTORSPORT",
                "WEB_PREMIUMDELUXEMOTORSPORT",
                false,
                0,
                "PDM",
                title);

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_TICKER, false, true);
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
            ActivatePdmShowroom();

            if (IsPlayerNearPdmShowroom())
            {
                PdmShowroomBridge.RequestOpenMenu();
                ShowPdmNotification("Mua xe", "PDM đang mở cửa! Bạn hãy lựa phương tiện đi!");
            }
            else
            {
                ShowPdmNotification("Đến showroom", "Cửa hàng đã mở cửa rồi, bạn đi đến cửa hàng nha!");
            }
        }
        finally
        {
            try { CustomiFruit.GetCurrentInstance()?.Close(0); } catch { }
        }
    }

    private void ActivatePdmShowroom()
    {
        if (!PdmShowroomBridge.IsActive)
            PdmShowroomBridge.Activate(PdmSessionLifetimeMs);

        ApplyPdmOpenState();
    }

    private void DeactivatePdmShowroom()
    {
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

    private bool IsPlayerNearPdmShowroom()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists() || p.IsDead)
                return false;

            return p.Position.DistanceTo(PdmShowroomPos) <= PdmActivationRadius;
        }
        catch
        {
            return false;
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
            if (IsActive && Game.GameTime < ExpiresAtGameTime)
                return;

            IsActive = true;
            ActivatedAtGameTime = Game.GameTime;
            ExpiresAtGameTime = Game.GameTime + Math.Max(1, durationMs);
            _openMenuRequested = false;

            // Chốt chính sách ngay lúc PDM được kích hoạt
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

    public static void RequestOpenMenu()
    {
        lock (SyncRoot)
        {
            if (IsActive)
                _openMenuRequested = true;
        }
    }

    public static bool HasOpenMenuRequest()
    {
        lock (SyncRoot)
        {
            return _openMenuRequested;
        }
    }

    public static void ClearOpenMenuRequest()
    {
        lock (SyncRoot)
        {
            _openMenuRequested = false;
        }
    }
}