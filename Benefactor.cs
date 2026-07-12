using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Media;
using System.Threading.Tasks;

public class BenefactorScript : Script
{
    private static string L(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    private static string GetScriptsDirectory()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string scriptsDir = Path.Combine(baseDir, "scripts");

            if (Directory.Exists(scriptsDir))
            {
                return scriptsDir;
            }

            return baseDir;
        }
        catch
        {
            return ".";
        }
    }

    private static string BenefactorContactName => L("Benefactor_ContactName", "Benefactor");
    private static string BenefactorMenuTitle => L("Benefactor_MenuTitle", "Benefactor");
    private static string BenefactorMenuSubtitle => L("Benefactor_MenuSubtitle", "CHI TIẾT DỊCH VỤ THUÊ XE");
    private static string BenefactorConfirmTitle => L("Benefactor_ConfirmTitle", "Xác nhận thuê phương tiện");
    private static string BenefactorCancelTitle => L("Benefactor_CancelTitle", "Hủy bỏ dịch vụ thuê");
    private static string BenefactorFeeTitle => L("Benefactor_FeeTitle", "Số tiền thuê phương tiện: {0}");
    private static string BenefactorFeeNaText => L("Benefactor_FeeNaText", "N/A");
    private static string BenefactorNoVehiclesText => L("Benefactor_NoVehiclesText", "Không thể tải danh sách phương tiện.");
    private static string BenefactorChooseOneText => L("Benefactor_ChooseOneText", "Hãy chọn một phương tiện trước.");
    private static string BenefactorAlreadyActiveText => L("Benefactor_AlreadyActiveText", "Bạn đang có ~HUD_COLOUR_DEGEN_YELLOW~một chiếc thuê đang hoạt động~s~ rồi.");
    private static string BenefactorNotEnoughMoneyText => L("Benefactor_NotEnoughMoneyText", "Bạn không đủ tiền thuê phương tiện này.");
    private static string BenefactorSpawnFailedText => L("Benefactor_SpawnFailedText", "Không thể giao xe thuê.");
    private static string BenefactorRentalSpawnedText => L("Benefactor_RentalSpawnedText", "Chiếc phương tiện bạn thuê đã được đưa tới rồi nhé!");
    private static string BenefactorRentalExpiredText => L("Benefactor_RentalExpiredText", "Thời gian thuê xe đã kết thúc.");
    private static string BenefactorRentalLostNoticeText => L("Benefactor_RentalLostNoticeText",
         "Chiếc phương tiện mà bạn đã thuê từ công ty đâu rồi, sao không thấy trả lại? Nếu không trả sẽ phải đền bù một khoảng tiền ~HUD_COLOUR_DEGEN_YELLOW~{0}~s~ đấy!");
    private static string BenefactorRentalPenaltySeizureText = L(
        "Benefactor_RentalPenaltySeizureText",
        "Vì bạn không đủ tiền đền nên công ty chúng tôi buộc phải tịch thu chút đỉnh từ số tiền hiện tại của bạn. Mong lần sau bạn thuê phương tiện sẽ cẩn thận hơn!");

    private const int CONTACT_DIAL_TIMEOUT_MS = 2000;
    private const int RENT_CHECK_INTERVAL_MS = 30000;
    private const int RENT_DURATION_HOURS = 3;
    private const double RENT_FEE_PERCENT = 0.0167;
    private const int RENTAL_SPAWN_BLIP_DURATION_MS = 60000;
    private const int RENT_DECAY_STOP_DELETE_DELAY_MS = 60000;

    private const int DAILY_RENTAL_POOL_SIZE = 12;
    private const int RENT_DECAY_DURATION_MS = 5000;

    private const int RENT_LOST_NOTICE_DELAY_MS = 5000;
    private const int RENT_LOST_PENALTY_DELAY_MS = 15000;
    private const int RENT_LOST_WANTED_STAR_GAIN = 2;
    private const int RENT_LOST_WANTED_STAR_MAX = 5;

    private const int RENTAL_STREAM_KEEPALIVE_INTERVAL_MS = 10000;
    private const int RENTAL_STREAM_PRELOAD_WAIT_MS = 150;
    private const float RENTAL_STREAM_PRELOAD_RADIUS = 120f;
    private const float RENTAL_STREAM_KEEPALIVE_DISTANCE = 300f;

    private const int BENEFCTOR_INTRO_DELAY_MS = 500;

    private readonly string BenefactorAudioRoot = Path.Combine(
        GetScriptsDirectory(),
        "Audio"
    );

    private bool _benefactorIntroPlaying = false; 
    private int _benefactorIntroStartedAt = 0;

    // Trạng thái giữ vùng spawn luôn được load
    private Vector3 _rentalStreamAnchor = Vector3.Zero;
    private bool _rentalStreamActive = false;
    private int _rentalStreamLastPulseGameTime = -1;

    private int _rentalLossCompensation = -1;

    private const float AIR_WANTED_ZONE_RADIUS = 200f;
    private const int AIR_WANTED_FREEZE_DURATION_MS = 70000;

    private bool _rentedVehicleIsAir = false;
    private bool _airWantedZoneTriggered = false;
    private int _airWantedFreezeUntilGameTime = -1;
    private int _airWantedFrozenLevel = -1;

    private int _rentedBasePrice = 0;
    private bool _rentalLossSequenceActive = false;
    private bool _rentalLossNoticeSent = false;
    private bool _rentalLossPenaltyApplied = false;
    private int _rentalLossNoticeDueGameTime = -1;
    private int _rentalLossPenaltyDueGameTime = -1;

    private const string BenefactorMessageSoundName = "Text_Arrive_Tone";
    private const string BenefactorMessageSoundSet = "Phone_SoundSet_Default";

    private sealed class SpawnPoint
    {
        public Vector3 Position;
        public float Heading;

        public SpawnPoint(float x, float y, float z, float heading)
        {
            Position = new Vector3(x, y, z);
            Heading = heading;
        }
    }

    // Plane / Helicopter
    private static readonly SpawnPoint[] AIR_RENT_SPAWN_POINTS =
    {
        new SpawnPoint(-1432.883f, -2666.713f, 22.7252f, 239.5946f),
        new SpawnPoint(-980.8219f, -2996.609f, 15.16564f, 61.21362f),
        new SpawnPoint(-1082.069f, -2908.835f, 15.67712f, 60.14975f),
    };

    // Boats / Class 14
    private static readonly SpawnPoint[] BOAT_RENT_SPAWN_POINTS =
    {
    new SpawnPoint(-846.8261f, -1362.792f, -0.08653483f, 105.8724f),
    new SpawnPoint(-843.0363f, -1372.011f, -0.08653766f, 111.5746f),
    new SpawnPoint(-836.4541f, -1388.954f, -0.08653696f, 108.9818f),
    new SpawnPoint(-839.4794f, -1380.607f, -0.08653706f, 109.0283f),
    new SpawnPoint(-849.943f, -1354.28f, -0.08640999f, 108.0519f),
};

    // All other vehicle classes
    private static readonly SpawnPoint[] GROUND_RENT_SPAWN_POINTS =
    {
        new SpawnPoint(-167.0612f, -625.1726f, 31.92324f, 69.95976f),
        new SpawnPoint(-311.5956f, -771.7511f, 47.5835f, 342.8295f),
        new SpawnPoint(-357.8903f, -756.9161f, 43.12815f, 88.4092f),
        new SpawnPoint(-156.6779f, -637.7577f, 31.67542f, 250.8911f),
    };

    private readonly ObjectPool _menuPool = new ObjectPool();
    private NativeMenu _mainMenu;
    private bool _menuReady = false;

    private NativeCheckboxItem[] _vehicleItems = new NativeCheckboxItem[0];
    private NativeItem _feeItem;
    private NativeItem _confirmItem;
    private NativeItem _cancelItem;

    private bool _contactAdded = false;
    private bool _callPending = false;
    private int _callDueTime = 0;

    private bool _menuSync = false;

    private readonly Random _rng = new Random();

    private List<RentalOption> _currentOptions = new List<RentalOption>();
    private RentalOption _selectedOption = null;
    private int _selectedBasePrice = 0;
    private int _selectedFee = 0;

    private int _benefactorPoolDaySerial = -1;
    private bool _benefactorPoolBuiltForDay = false;
    private readonly HashSet<uint> _benefactorRentedHashesToday = new HashSet<uint>();

    private iFruitContact _benefactorContact = null;

    private Vector3 _rentalDecayStartVelocity = Vector3.Zero;
    private int _rentalDecayStartedAt = -1;

    private Vehicle _rentedVehicle = null;
    private DateTime? _rentalStartedAt = null;
    private DateTime? _rentalEndsAt = null;
    private bool _rentalExpiryTriggered = false;
    private int _lastRentalTimeCheckGameTime = -RENT_CHECK_INTERVAL_MS;
    private int _rentalCleanupStartGameTime = -1;

    private Blip _rentalSpawnBlip = null;
    private int _rentalSpawnBlipExpireAt = -1;

    private sealed class RentalOption
    {
        public InstantRefill.VehicleEntry Source;
        public int RolledBasePrice;
        public int RentalFee;

        public string DisplayName
        {
            get { return Source != null ? Source.Name : string.Empty; }
        }

        public uint Hash
        {
            get { return Source != null ? Source.Hash : 0u; }
        }
    }

    public BenefactorScript()
    {
        Interval = 1000;
        Tick += OnTick;
        KeyDown += OnKeyDown;
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            SyncDailyRentalPoolState();
            UpdateBenefactorContactAvailability();

            EnsureContactRegistered();
            ProcessPendingCall();
            UpdateRentalLifecycle();
            UpdateRentalSpawnBlipLifecycle();
            PulseRentalStreamAnchor();

            // GIỮ DÒNG NÀY
            UpdateAirVehicleWantedEffect();

            bool menuVisible = _mainMenu != null && _mainMenu.Visible;
            bool needsFastTick =
                menuVisible ||
                _callPending ||
                _rentalExpiryTriggered ||
                IsAirWantedFreezeActive();

            if (menuVisible)
                _menuPool.Process();

            Interval = needsFastTick ? 0 : 1000;
        }
        catch (Exception ex)
        {
            Log("OnTick failed: " + ex);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            if (_mainMenu != null && _mainMenu.Visible)
            {
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                {
                    CloseMenu();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log("OnKeyDown failed: " + ex);
        }
    }

    private void StartRentalStreamAnchor(Vector3 position)
    {
        try
        {
            _rentalStreamAnchor = position;
            _rentalStreamActive = true;
            _rentalStreamLastPulseGameTime = -RENTAL_STREAM_KEEPALIVE_INTERVAL_MS;

            // Nạp ngay một nhịp đầu tiên
            PulseRentalStreamAnchor(true);
        }
        catch (Exception ex)
        {
            Log("StartRentalStreamAnchor failed: " + ex);
        }
    }

    private void StopRentalStreamAnchor()
    {
        try
        {
            _rentalStreamActive = false;
            _rentalStreamAnchor = Vector3.Zero;
            _rentalStreamLastPulseGameTime = -1;

            try
            {
                Function.Call(Hash.NEW_LOAD_SCENE_STOP);
            }
            catch { }
        }
        catch (Exception ex)
        {
            Log("StopRentalStreamAnchor failed: " + ex);
        }
    }

    private void PrewarmSpawnArea(Vector3 position)
    {
        try
        {
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, position.X, position.Y, position.Z);

            Function.Call(Hash.NEW_LOAD_SCENE_START_SPHERE,
                position.X, position.Y, position.Z,
                RENTAL_STREAM_PRELOAD_RADIUS, 0);

            Script.Wait(RENTAL_STREAM_PRELOAD_WAIT_MS);

            Function.Call(Hash.NEW_LOAD_SCENE_STOP);
        }
        catch (Exception ex)
        {
            Log("PrewarmSpawnArea failed: " + ex);
        }
    }

    private void PulseRentalStreamAnchor(bool force = false)
    {
        try
        {
            if (!_rentalStreamActive || _rentalStreamAnchor == Vector3.Zero)
                return;

            if (!force && Game.GameTime - _rentalStreamLastPulseGameTime < RENTAL_STREAM_KEEPALIVE_INTERVAL_MS)
                return;

            Ped player = Game.Player.Character;
            if (!force && player != null && player.Exists() && !player.IsDead)
            {
                float dist2 = player.Position.DistanceToSquared(_rentalStreamAnchor);
                if (dist2 <= RENTAL_STREAM_KEEPALIVE_DISTANCE * RENTAL_STREAM_KEEPALIVE_DISTANCE)
                    return;
            }

            _rentalStreamLastPulseGameTime = Game.GameTime;
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, _rentalStreamAnchor.X, _rentalStreamAnchor.Y, _rentalStreamAnchor.Z);
        }
        catch (Exception ex)
        {
            Log("PulseRentalStreamAnchor failed: " + ex);
        }
    }

    private void EnsureContactRegistered()
    {
        try
        {
            SyncDailyRentalPoolState();
            UpdateBenefactorContactAvailability();
        }
        catch (Exception ex)
        {
            Log("EnsureContactRegistered failed: " + ex);
        }
    }

    private void OnBenefactorAnswered(iFruitContact sender)
    {
        try
        {
            SyncDailyRentalPoolState();

            if (!HasAvailableRentalOptions())
            {
                UpdateBenefactorContactAvailability();
                return;
            }

            int remainingToday = _currentOptions != null ? _currentOptions.Count : 0;

            if (remainingToday < 0)
            {
                remainingToday = 0;
            }
            else if (remainingToday > 12)
            {
                remainingToday = 12;
            }

            _callPending = true;
            _callDueTime = Game.GameTime + BENEFCTOR_INTRO_DELAY_MS;

            StartBenefactorIntroSequence();
            PlayBenefactorIntroAudio(remainingToday);
        }
        catch (Exception ex)
        {
            Log("OnBenefactorAnswered failed: " + ex);
        }
        // Gộp khối try-catch trống trong finally bằng toán tử điều kiện và loại bỏ catch thừa
        finally
        {
            CustomiFruit.GetCurrentInstance()?.Close(0);
        }
    }

    private void ProcessPendingCall()
    {
        try
        {
            if (!_callPending) return;
            if (Game.GameTime < _callDueTime) return;

            _callPending = false;
            _benefactorIntroPlaying = false;

            SyncDailyRentalPoolState();

            if (!HasAvailableRentalOptions())
            {
                UpdateBenefactorContactAvailability();
                return;
            }

            OpenMenu();
        }
        catch (Exception ex)
        {
            Log("ProcessPendingCall failed: " + ex);
        }
    }

    private static int ComputeRentalCompensation(int basePrice)
    {
        try
        {
            if (basePrice <= 0)
                return 0;

            decimal amount = decimal.Round(
                (decimal)Math.Max(0, basePrice) * 0.75m,
                0,
                MidpointRounding.AwayFromZero);

            return (int)Math.Max(0m, amount);
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsAirVehicleClassName(string cls)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cls))
                return false;

            string s = cls.Trim().ToLowerInvariant();

            return s.Contains("aircraft")
                || s.Contains("helicopter")
                || s.Contains("seaplane")
                || s.Contains("flying")
                || s.Contains("jet")
                || s.Contains("bomber");
        }
        catch
        {
            return false;
        }
    }

    private bool IsAirWantedFreezeActive()
    {
        try
        {
            return _airWantedFrozenLevel >= 0
                && _airWantedFreezeUntilGameTime >= 0
                && Game.GameTime < _airWantedFreezeUntilGameTime;
        }
        catch
        {
            return false;
        }
    }

    private void StartAirWantedFreeze(int wantedLevel)
    {
        try
        {
            _airWantedFrozenLevel = Math.Max(0, Math.Min(5, wantedLevel));
            _airWantedFreezeUntilGameTime = Game.GameTime + AIR_WANTED_FREEZE_DURATION_MS;
        }
        catch { }
    }

    private void ClearAirWantedFreeze()
    {
        _airWantedFrozenLevel = -1;
        _airWantedFreezeUntilGameTime = -1;
        ActivateAirWantedImmunity(false);
    }

    private void UpdateAirVehicleWantedEffect()
    {
        try
        {
            if (!IsRentalActive())
                return;

            if (!_rentedVehicleIsAir)
                return;

            if (_rentedVehicle == null || !_rentedVehicle.Exists())
                return;

            // Đang trong thời gian miễn truy nã
            if (IsAirWantedFreezeActive())
            {
                ForceWantedLevel(0);
                ActivateAirWantedImmunity(true);

                if (Game.GameTime >= _airWantedFreezeUntilGameTime)
                    ClearAirWantedFreeze();

                return;
            }

            // Đã kích hoạt rồi thì thôi
            if (_airWantedZoneTriggered)
                return;

            // Trong phạm vi 800m của phương tiện thuê
            if (!IsPlayerWithinAirWantedZone())
                return;

            ApplyAirVehicleWantedReduction();
        }
        catch (Exception ex)
        {
            Log("UpdateAirVehicleWantedEffect failed: " + ex);
        }
    }

    private bool IsPlayerWithinAirWantedZone()
    {
        try
        {
            Ped player = Game.Player.Character;

            if (player == null || !player.Exists() || player.IsDead)
                return false;

            if (_rentedVehicle == null || !_rentedVehicle.Exists())
                return false;

            return player.Position.DistanceToSquared(_rentedVehicle.Position)
                <= AIR_WANTED_ZONE_RADIUS * AIR_WANTED_ZONE_RADIUS;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyAirVehicleWantedReduction()
    {
        try
        {
            // Xóa toàn bộ sao truy nã
            ForceWantedLevel(0);

            try
            {
                int playerId = Function.Call<int>(Hash.PLAYER_ID);

                Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, playerId);
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, playerId, 0, false);
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, playerId, false);
            }
            catch { }

            // Never Wanted 70 giây
            StartAirWantedFreeze(0);
            ActivateAirWantedImmunity(true);

            // Chỉ kích hoạt 1 lần
            _airWantedZoneTriggered = true;
        }
        catch (Exception ex)
        {
            Log("ApplyAirVehicleWantedReduction failed: " + ex);
        }
    }

    private static int SafeGetWantedLevel()
    {
        try
        {
            return Math.Max(0, Game.Player.WantedLevel);
        }
        catch
        {
            return 0;
        }
    }

    private static void ForceWantedLevel(int wantedLevel)
    {
        try
        {
            wantedLevel = Math.Max(0, Math.Min(5, wantedLevel));

            try { Game.Player.WantedLevel = wantedLevel; } catch { }

            try
            {
                if (wantedLevel <= 0)
                {
                    Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player);
                    Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
                    return;
                }

                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, wantedLevel, false);
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
            }
            catch { }
        }
        catch { }
    }

    private void ActivateAirWantedImmunity(bool enabled)
    {
        try
        {
            int playerId = Function.Call<int>(Hash.PLAYER_ID);

            Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, playerId, enabled);
            Function.Call(Hash.SET_DISPATCH_COPS_FOR_PLAYER, playerId, !enabled);

            if (enabled)
            {
                Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, playerId);
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, playerId, 0, false);
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, playerId, false);

                // Ghì liên tục để vùng cấm như sân bay không tự bật sao lại ngay
                try { Game.Player.WantedLevel = 0; } catch { }
            }
        }
        catch { }
    }

    private bool IsRentalVehicleDestroyedOrMissing()
    {
        try
        {
            if (_rentedVehicle == null)
                return true;

            bool exists = false;
            try { exists = _rentedVehicle.Exists(); } catch { exists = false; }
            if (!exists)
                return true;

            try
            {
                if (_rentedVehicle.IsDead)
                    return true;
            }
            catch { }

            try
            {
                if (_rentedVehicle.EngineHealth <= 0f || _rentedVehicle.BodyHealth <= 0f)
                    return true;
            }
            catch { }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private void BeginLostRentalSequence()
    {
        try
        {
            _rentalExpiryTriggered = true;
            _rentalLossSequenceActive = true;
            _rentalLossNoticeSent = false;
            _rentalLossPenaltyApplied = false;
            _rentalLossCompensation = ComputeRentalCompensation(_rentedBasePrice);
            _rentalLossNoticeDueGameTime = Game.GameTime + RENT_LOST_NOTICE_DELAY_MS;
            _rentalLossPenaltyDueGameTime = -1;
            _rentalCleanupStartGameTime = -1;
        }
        catch (Exception ex)
        {
            Log("BeginLostRentalSequence failed: " + ex);
        }
    }

    private void UpdateLostRentalSequence()
    {
        try
        {
            if (!_rentalLossSequenceActive)
                return;

            if (_rentalLossPenaltyApplied)
                return;

            int compensation = _rentalLossCompensation > 0
                ? _rentalLossCompensation
                : ComputeRentalCompensation(_rentedBasePrice);

            if (compensation <= 0)
                compensation = 0;

            if (!_rentalLossNoticeSent)
            {
                if (Game.GameTime < _rentalLossNoticeDueGameTime)
                    return;

                ShowBenefactorNotification(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        BenefactorRentalLostNoticeText,
                        FormatMoney(compensation)));

                _rentalLossNoticeSent = true;
                _rentalLossPenaltyDueGameTime = Game.GameTime + RENT_LOST_PENALTY_DELAY_MS;
                return;
            }

            if (Game.GameTime < _rentalLossPenaltyDueGameTime)
                return;

            ApplyLostRentalPenalty();
            _rentalLossPenaltyApplied = true;

            ClearRentalState();
        }
        catch (Exception ex)
        {
            Log("UpdateLostRentalSequence failed: " + ex);
        }
    }

    private void ApplyLostRentalPenalty()
    {
        try
        {
            int compensation = _rentalLossCompensation > 0
                ? _rentalLossCompensation
                : ComputeRentalCompensation(_rentedBasePrice);

            if (compensation <= 0)
                return;

            if (Game.Player.Money >= compensation)
            {
                Game.Player.Money -= compensation;
                return;
            }

            int currentMoney = 0;
            try { currentMoney = Math.Max(0, Game.Player.Money); } catch { currentMoney = 0; }

            int moneyToRemove = currentMoney / 2;
            int remainingMoney = currentMoney - moneyToRemove;

            Game.Player.Money = remainingMoney;

            int currentWanted = 0;
            try { currentWanted = Game.Player.WantedLevel; } catch { }

            int newWanted = Math.Min(RENT_LOST_WANTED_STAR_MAX, currentWanted + RENT_LOST_WANTED_STAR_GAIN);

            try { Game.Player.WantedLevel = newWanted; } catch { }

            try
            {
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, newWanted, false);
                Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
            }
            catch { }

            ShowBenefactorNotification(BenefactorRentalPenaltySeizureText);
        }
        catch (Exception ex)
        {
            Log("ApplyLostRentalPenalty failed: " + ex);
        }
    }

    private void OpenMenu()
    {
        try
        {
            SyncDailyRentalPoolState();

            if (!HasAvailableRentalOptions())
            {
                UpdateBenefactorContactAvailability();
                GTA.UI.Screen.ShowSubtitle(BenefactorNoVehiclesText, 2500);
                return;
            }

            EnsureMainMenuCreated();
            BuildMenu();

            if (_mainMenu != null)
            {
                _mainMenu.Visible = true;
                Interval = 0;
            }
        }
        catch (Exception ex)
        {
            Log("OpenMenu failed: " + ex);
        }
    }

    private void CloseMenu()
    {
        try
        {
            if (_mainMenu != null)
                _mainMenu.Visible = false;
        }
        catch { }
    }

    private void EnsureMainMenuCreated()
    {
        try
        {
            if (_menuReady)
                return;

            _mainMenu = new NativeMenu(BenefactorMenuTitle, BenefactorMenuSubtitle);
            _menuPool.Add(_mainMenu);
            ConfigureKeyboardOnlyMenu(_mainMenu);
            _menuReady = true;
        }
        catch (Exception ex)
        {
            Log("EnsureMainMenuCreated failed: " + ex);
        }
    }

    private void ConfigureKeyboardOnlyMenu(NativeMenu menu)
    {
        if (menu == null)
            return;

        menu.MouseBehavior = MenuMouseBehavior.Disabled;
        menu.ResetCursorWhenOpened = false;
        menu.CloseOnInvalidClick = false;
        menu.RotateCamera = true;
    }

    private static string FormatMoney(int value)
    {
        if (value < 0)
            value = 0;

        return string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private string GetBenefactorIntroWavPath(int remainingToday)
    {
        try
        {
            Directory.CreateDirectory(BenefactorAudioRoot);
        }
        catch
        {
            // Tránh nuốt ngoại lệ âm thầm trừ khi thực sự cần thiết
        }

        int suffix = 12 - Math.Max(0, Math.Min(12, remainingToday));

        if (suffix <= 0)
        {
            return Path.Combine(BenefactorAudioRoot, "Benefactor.wav");
        }

        return Path.Combine(BenefactorAudioRoot, $"Benefactor_-{suffix}.wav");
    }

    private string GetBenefactorIntroSubtitle(int remainingToday)
    {
        switch (Math.Max(0, Math.Min(12, remainingToday)))
        {
            case 12:
                return "Hi, I'm with Benefactor Rentals. Which vehicle are you looking to rent?";
            case 11:
                return "Benefactor has only 11 vehicles left for today.";
            case 10:
                return "Only 10 Benefactor vehicles remain available today.";
            case 9:
                return "We are down to our last 9 vehicles for the day.";
            case 8:
                return "Just 8 Benefactor vehicles left on the lot today.";
            case 7:
                return "Benefactor’s daily availability is now down to 7 vehicles.";
            case 6:
                return "Securing one of our last 6 vehicles for today is highly recommended.";
            case 5:
                return "Only 5 spots left for today’s Benefactor fleet.";
            case 4:
                return "Hurry, Benefactor has just 4 vehicles remaining today.";
            case 3:
                return "We have only 3 vehicles left to offer for the rest of the day.";
            case 2:
                return "Just 2 Benefactor vehicles left available.";
            case 1:
                return "There is only 1 last Benefactor vehicle up for grabs today.";
            default:
                return "Benefactor has no remaining rentals for the rest of the day. Kindly return tomorrow, and thank you for utilizing our services!";
        }
    }

    private void PlayBenefactorIntroAudio(int remainingToday)
    {
        try
        {
            string path = GetBenefactorIntroWavPath(remainingToday);
            if (!File.Exists(path))
            {
                return;
            }

            string subtitle = GetBenefactorIntroSubtitle(remainingToday);
            GTA.UI.Screen.ShowSubtitle(subtitle, 4000);

            Task.Run(() =>
            {
                try
                {
                    using (var sp = new SoundPlayer(path))
                    {
                        sp.Load();
                        sp.PlaySync();
                    }
                }
                catch
                {
                    // Xử lý lỗi phát âm thanh nếu cần
                }
            });
        }
        catch
        {
            // Xử lý lỗi chung nếu cần
        }
    }

    private void StartBenefactorIntroSequence()
    {
        try
        {
            _benefactorIntroPlaying = true;
            _benefactorIntroStartedAt = Game.GameTime;
        }
        catch
        {
            _benefactorIntroPlaying = false;
        }
    }

    private static int ComputeRentalFee(int basePrice)
    {
        try
        {
            if (basePrice <= 0)
                return 0;

            decimal fee = (decimal)basePrice * (decimal)RENT_FEE_PERCENT;
            return (int)Math.Round(fee, MidpointRounding.AwayFromZero);
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsAirVehicleClass(int vehicleClass)
    {
        return vehicleClass == 15 || vehicleClass == 16; // Helicopters / Planes
    }

    private bool IsExpiredRentalAirVehicle()
    {
        try
        {
            return _rentedVehicleIsAir
                && _rentedVehicle != null
                && _rentedVehicle.Exists();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEntityOnGroundSafe(Vehicle veh)
    {
        try
        {
            if (veh == null || !veh.Exists())
                return false;

            return !veh.IsInAir;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBoatVehicleClass(int vehicleClass)
    {
        return vehicleClass == 14; // Boats
    }

    private static int GetVehicleClassFromModel(Model model)
    {
        try
        {
            if (model == null || !model.IsValid || !model.IsInCdImage)
                return -1;

            return SafeCallInt(
                () => Function.Call<int>(Hash.GET_VEHICLE_CLASS_FROM_NAME, model.Hash),
                -1);
        }
        catch
        {
            return -1;
        }
    }

    private bool IsPlaneOrHeliModel(Model model)
    {
        try
        {
            int vehicleClass = GetVehicleClassFromModel(model);
            return IsAirVehicleClass(vehicleClass);
        }
        catch
        {
            return false;
        }
    }

    private SpawnPoint GetRandomRentalSpawnPoint(Model model)
    {
        try
        {
            int vehicleClass = GetVehicleClassFromModel(model);

            SpawnPoint[] pool;
            if (IsBoatVehicleClass(vehicleClass))
            {
                pool = BOAT_RENT_SPAWN_POINTS;
            }
            else if (IsAirVehicleClass(vehicleClass))
            {
                pool = AIR_RENT_SPAWN_POINTS;
            }
            else
            {
                pool = GROUND_RENT_SPAWN_POINTS;
            }

            if (pool == null || pool.Length == 0)
                return new SpawnPoint(-167.0612f, -625.1726f, 31.92324f, 69.95976f);

            return pool[_rng.Next(pool.Length)];
        }
        catch
        {
            return new SpawnPoint(-167.0612f, -625.1726f, 31.92324f, 69.95976f);
        }
    }

    private static void ShuffleList<T>(IList<T> list, Random rng)
    {
        if (list == null || rng == null)
            return;

        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            if (j == i)
                continue;

            T temp = list[i];
            list[i] = list[j];
            list[j] = temp;
        }
    }

    private int GetCurrentInGameDaySerial()
    {
        try
        {
            DateTime dt = GetCurrentInGameDateTime();
            return dt.Year * 10000 + dt.Month * 100 + dt.Day;
        }
        catch
        {
            return -1;
        }
    }

    private void SyncDailyRentalPoolState()
    {
        try
        {
            int daySerial = GetCurrentInGameDaySerial();
            if (daySerial < 0)
                return;

            if (_benefactorPoolBuiltForDay && _benefactorPoolDaySerial == daySerial)
                return;

            _benefactorPoolDaySerial = daySerial;
            _benefactorPoolBuiltForDay = true;

            _benefactorRentedHashesToday.Clear();
            _currentOptions = BuildDailyRentalOptions(daySerial);

            _selectedOption = null;
            _selectedBasePrice = 0;
            _selectedFee = 0;

            UpdateFeeItem();
        }
        catch (Exception ex)
        {
            Log("SyncDailyRentalPoolState failed: " + ex);
        }
    }

    private List<RentalOption> BuildDailyRentalOptions(int daySerial)
    {
        try
        {
            var source = GetVehicleSourceList();
            if (source == null || source.Count == 0)
                return new List<RentalOption>();

            var pool = source.Where(v => v != null).ToList();

            int seed = unchecked(daySerial * 73856093 ^ 0x6B1D2D47);
            var dailyRng = new Random(seed);

            ShuffleList(pool, dailyRng);

            int count = Math.Min(DAILY_RENTAL_POOL_SIZE, pool.Count);
            var result = new List<RentalOption>(count);

            for (int i = 0; i < count; i++)
            {
                var entry = pool[i];
                if (entry == null)
                    continue;

                result.Add(new RentalOption
                {
                    Source = entry,
                    RolledBasePrice = 0,
                    RentalFee = 0
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            Log("BuildDailyRentalOptions failed: " + ex);
            return new List<RentalOption>();
        }
    }

    private void UpdateBenefactorContactAvailability()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            bool hasOptions = _currentOptions != null && _currentOptions.Count > 0;
            string contactName = BenefactorContactName;

            var existing = phone.Contacts.FirstOrDefault(c =>
                string.Equals(c.Name, contactName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                _benefactorContact = existing;
                existing.Active = hasOptions;
                _contactAdded = true;
                return;
            }

            if (!hasOptions)
                return;

            var contact = new iFruitContact(contactName)
            {
                Active = true,
                DialTimeout = CONTACT_DIAL_TIMEOUT_MS,
                Bold = false,
                Icon = ContactIcon.Bikesite
            };

            contact.Answered += OnBenefactorAnswered;
            phone.Contacts.Add(contact);

            _benefactorContact = contact;
            _contactAdded = true;
        }
        catch (Exception ex)
        {
            Log("UpdateBenefactorContactAvailability failed: " + ex);
        }
    }

    private void ConsumeDailyRentalOption(uint hash)
    {
        try
        {
            if (hash == 0)
                return;

            _benefactorRentedHashesToday.Add(hash);

            if (_currentOptions != null && _currentOptions.Count > 0)
                _currentOptions.RemoveAll(o => o != null && o.Hash == hash);

            if (_selectedOption != null && _selectedOption.Hash == hash)
                ClearSelection();

            UpdateBenefactorContactAvailability();
        }
        catch (Exception ex)
        {
            Log("ConsumeDailyRentalOption failed: " + ex);
        }
    }

    private bool HasAvailableRentalOptions()
    {
        SyncDailyRentalPoolState();
        return _currentOptions != null && _currentOptions.Count > 0;
    }

    private List<InstantRefill.VehicleEntry> GetVehicleSourceList()
    {
        try
        {
            var inst = InstantRefill.Instance;
            if (inst == null)
                return new List<InstantRefill.VehicleEntry>();

            FieldInfo field = typeof(InstantRefill).GetField("_vehicles", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                return new List<InstantRefill.VehicleEntry>();

            object value = field.GetValue(inst);
            var list = value as List<InstantRefill.VehicleEntry>;
            if (list == null)
                return new List<InstantRefill.VehicleEntry>();

            return list.Where(v => v != null).ToList();
        }
        catch (Exception ex)
        {
            Log("GetVehicleSourceList failed: " + ex);
            return new List<InstantRefill.VehicleEntry>();
        }
    }

    private void BuildMenu()
    {
        try
        {
            if (_mainMenu == null)
                return;

            SyncDailyRentalPoolState();

            _mainMenu.Clear();
            _currentOptions = _currentOptions ?? new List<RentalOption>();
            _selectedOption = null;
            _selectedBasePrice = 0;
            _selectedFee = 0;

            if (_currentOptions.Count == 0)
            {
                _mainMenu.Add(new NativeItem(BenefactorNoVehiclesText));
                return;
            }

            _vehicleItems = new NativeCheckboxItem[_currentOptions.Count];

            for (int i = 0; i < _currentOptions.Count; i++)
            {
                int index = i;
                var option = _currentOptions[i];
                string title = string.Format(CultureInfo.InvariantCulture, "{0}. {1}", i + 1, option.DisplayName);
                var item = new NativeCheckboxItem(title, false);
                item.Description = option.Source != null
                    ? string.Format(CultureInfo.InvariantCulture, "{0} | {1}", option.Source.Class ?? string.Empty, option.Source.Label ?? string.Empty)
                    : string.Empty;

                item.CheckboxChanged += (s, e) =>
                {
                    HandleSelectionChanged(index, item);
                };

                _vehicleItems[i] = item;
                _mainMenu.Add(item);
            }

            _feeItem = new NativeItem(string.Format(CultureInfo.InvariantCulture, BenefactorFeeTitle, BenefactorFeeNaText));
            _confirmItem = new NativeItem(BenefactorConfirmTitle);
            _cancelItem = new NativeItem(BenefactorCancelTitle);

            _confirmItem.Activated += (s, e) =>
            {
                ConfirmRental();
            };

            _cancelItem.Activated += (s, e) =>
            {
                CloseMenu();
            };

            _mainMenu.Add(_feeItem);
            _mainMenu.Add(_confirmItem);
            _mainMenu.Add(_cancelItem);
        }
        catch (Exception ex)
        {
            Log("BuildMenu failed: " + ex);
        }
    }

    private void HandleSelectionChanged(int index, NativeCheckboxItem item)
    {
        try
        {
            if (_menuSync)
                return;

            if (_currentOptions == null || index < 0 || index >= _currentOptions.Count)
                return;

            _menuSync = true;

            if (item != null && item.Checked)
            {
                for (int i = 0; i < _vehicleItems.Length; i++)
                {
                    if (i == index)
                        continue;

                    if (_vehicleItems[i] != null && _vehicleItems[i].Checked)
                        _vehicleItems[i].Checked = false;
                }

                var selected = _currentOptions[index];
                if (selected != null && selected.Source != null)
                {
                    _selectedOption = selected;
                    _selectedBasePrice = selected.Source.GetRandomPrice(_rng, false, 0);
                    _selectedFee = ComputeRentalFee(_selectedBasePrice);
                    selected.RolledBasePrice = _selectedBasePrice;
                    selected.RentalFee = _selectedFee;
                    UpdateFeeItem();
                }
                else
                {
                    ClearSelection();
                }
            }
            else
            {
                var selected = _selectedOption;
                if (selected != null && selected.Source != null && selected.Hash == _currentOptions[index].Hash)
                {
                    ClearSelection();
                }
            }
        }
        catch (Exception ex)
        {
            Log("HandleSelectionChanged failed: " + ex);
        }
        finally
        {
            _menuSync = false;
        }
    }

    private void ClearSelection()
    {
        _selectedOption = null;
        _selectedBasePrice = 0;
        _selectedFee = 0;
        UpdateFeeItem();
    }

    private void UpdateFeeItem()
    {
        try
        {
            if (_feeItem == null)
                return;

            if (_selectedOption == null || _selectedFee <= 0)
            {
                _feeItem.Title = string.Format(CultureInfo.InvariantCulture, BenefactorFeeTitle, BenefactorFeeNaText);
                return;
            }

            _feeItem.Title = string.Format(CultureInfo.InvariantCulture, BenefactorFeeTitle, FormatMoney(_selectedFee));
        }
        catch (Exception ex)
        {
            Log("UpdateFeeItem failed: " + ex);
        }
    }

    private void ConfirmRental()
    {
        try
        {
            if (_selectedOption == null || _selectedOption.Source == null)
            {
                GTA.UI.Screen.ShowSubtitle(BenefactorChooseOneText, 2500);
                return;
            }

            if (IsRentalActive())
            {
                GTA.UI.Screen.ShowSubtitle(BenefactorAlreadyActiveText, 2500);
                return;
            }

            if (_selectedFee <= 0)
            {
                GTA.UI.Screen.ShowSubtitle(BenefactorChooseOneText, 2500);
                return;
            }

            if (Game.Player.Money < _selectedFee)
            {
                GTA.UI.Screen.ShowSubtitle(BenefactorNotEnoughMoneyText, 2500);
                return;
            }

            uint rentedHash = _selectedOption.Hash;

            if (!TrySpawnRentalVehicle(_selectedOption.Source, out Vehicle vehicle))
            {
                GTA.UI.Screen.ShowSubtitle(BenefactorSpawnFailedText, 2500);
                return;
            }

            Game.Player.Money -= _selectedFee;
            _rentedVehicle = vehicle;
            _rentedBasePrice = Math.Max(0, _selectedBasePrice);
            _rentalLossCompensation = ComputeRentalCompensation(_rentedBasePrice);

            _rentalLossSequenceActive = false;
            _rentalLossNoticeSent = false;
            _rentalLossPenaltyApplied = false;
            _rentalLossNoticeDueGameTime = -1;
            _rentalLossPenaltyDueGameTime = -1;

            ConsumeDailyRentalOption(rentedHash);

            _rentalStartedAt = GetCurrentInGameDateTime();
            _rentalEndsAt = _rentalStartedAt.Value.AddHours(RENT_DURATION_HOURS);
            _rentalExpiryTriggered = false;
            _rentalCleanupStartGameTime = -1;
            _lastRentalTimeCheckGameTime = Game.GameTime;

            CloseMenu();
            ShowBenefactorNotification(BenefactorRentalSpawnedText);
            Interval = 0;
        }
        catch (Exception ex)
        {
            Log("ConfirmRental failed: " + ex);
        }
    }

    private bool TrySpawnRentalVehicle(InstantRefill.VehicleEntry entry, out Vehicle spawned)
    {
        spawned = null;

        try
        {
            if (entry == null)
                return false;

            Model model = new Model((int)entry.Hash);
            if (!model.IsValid || !model.IsInCdImage)
                return false;

            if (!model.IsLoaded)
            {
                model.Request(1000);
                int waited = 0;
                while (!model.IsLoaded && waited < 3000)
                {
                    Script.Wait(50);
                    waited += 50;
                }
            }

            if (!model.IsLoaded)
                return false;

            SpawnPoint spawnPoint = GetRandomRentalSpawnPoint(model);

            // Nạp map/collision trước khi tạo xe
            PrewarmSpawnArea(spawnPoint.Position);

            Vehicle veh = World.CreateVehicle(model, spawnPoint.Position, spawnPoint.Heading);
            if (veh == null || !veh.Exists())
                return false;

            try
            {
                veh.PlaceOnGround();
                veh.Repair();
                veh.DirtLevel = 0f;
                veh.IsEngineRunning = true;
                veh.IsPersistent = true;
                veh.LockStatus = VehicleLockStatus.Unlocked;

                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, veh.Handle, true, true);
                Function.Call(Hash.SET_VEHICLE_HAS_BEEN_OWNED_BY_PLAYER, veh.Handle, true);
            }
            catch { }

            _rentedVehicleIsAir = IsAirVehicleClassName(entry?.Class) || IsPlaneOrHeliModel(model);
            _airWantedZoneTriggered = false;

            // Giữ vùng spawn này được streamed trong suốt thời gian thuê
            StartRentalStreamAnchor(spawnPoint.Position);

            CreateRentalSpawnBlip(veh);

            spawned = veh;
            return true;
        }
        catch (Exception ex)
        {
            Log("TrySpawnRentalVehicle failed: " + ex);
            spawned = null;
            _rentedVehicleIsAir = false;
            _airWantedZoneTriggered = false;
            StopRentalStreamAnchor();
            return false;
        }
    }

    private void KeepRentedVehicleAlive()
    {
        try
        {
            if (_rentedVehicle == null || !_rentedVehicle.Exists())
                return;

            _rentedVehicle.IsPersistent = true;

            try
            {
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _rentedVehicle.Handle, true, true);
            }
            catch { }

            try
            {
                Function.Call(Hash.SET_VEHICLE_HAS_BEEN_OWNED_BY_PLAYER, _rentedVehicle.Handle, true);
            }
            catch { }
        }
        catch { }
    }

    private void CreateRentalSpawnBlip(Vehicle veh)
    {
        try
        {
            ClearRentalSpawnBlip();

            if (veh == null || !veh.Exists())
                return;

            Blip blip = veh.AddBlip();
            if (blip == null || !blip.Exists())
                return;

            blip.Sprite = BlipSprite.Standard;
            blip.Color = BlipColor.Yellow;
            blip.IsShortRange = false;
            blip.Name = L("Benefactor_RentalBlipName", "Xe thuê");

            _rentalSpawnBlip = blip;
            _rentalSpawnBlipExpireAt = Game.GameTime + RENTAL_SPAWN_BLIP_DURATION_MS;
        }
        catch
        {
            ClearRentalSpawnBlip();
        }
    }

    private void UpdateRentalSpawnBlipLifecycle()
    {
        try
        {
            if (_rentalSpawnBlip == null)
                return;

            if (Game.GameTime < _rentalSpawnBlipExpireAt)
            {
                if (_rentedVehicle == null || !_rentedVehicle.Exists())
                    ClearRentalSpawnBlip();

                return;
            }

            ClearRentalSpawnBlip();
        }
        catch
        {
            ClearRentalSpawnBlip();
        }
    }

    private void ClearRentalSpawnBlip()
    {
        try
        {
            if (_rentalSpawnBlip != null)
            {
                try
                {
                    if (_rentalSpawnBlip.Exists())
                        _rentalSpawnBlip.Delete();
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            _rentalSpawnBlip = null;
            _rentalSpawnBlipExpireAt = -1;
        }
    }

    private DateTime GetCurrentInGameDateTime()
    {
        try
        {
            int year = SafeCallInt(() => Function.Call<int>(Hash.GET_CLOCK_YEAR), 2000);
            int month = SafeCallInt(() => Function.Call<int>(Hash.GET_CLOCK_MONTH), 1);
            int day = SafeCallInt(() => Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH), 1);
            int hour = SafeCallInt(() => Function.Call<int>(Hash.GET_CLOCK_HOURS), 0);
            int minute = SafeCallInt(() => Function.Call<int>(Hash.GET_CLOCK_MINUTES), 0);

            if (month < 1 || month > 12)
                month += 1;

            if (year < 1) year = 2000;
            if (month < 1) month = 1;
            if (month > 12) month = 12;

            int maxDay = DateTime.DaysInMonth(year, month);
            if (day < 1) day = 1;
            if (day > maxDay) day = maxDay;
            if (hour < 0) hour = 0;
            if (hour > 23) hour = 23;
            if (minute < 0) minute = 0;
            if (minute > 59) minute = 59;

            return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        }
        catch
        {
            return new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        }
    }

    private static int SafeCallInt(Func<int> fn, int fallback)
    {
        try
        {
            if (fn == null)
                return fallback;

            return fn();
        }
        catch
        {
            return fallback;
        }
    }

    private bool IsRentalActive()
    {
        try
        {
            return _rentalStartedAt.HasValue && _rentalEndsAt.HasValue;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateRentalLifecycle()
    {
        try
        {
            if (!IsRentalActive())
            {
                _rentalExpiryTriggered = false;
                _rentalLossSequenceActive = false;
                _rentalLossNoticeSent = false;
                _rentalLossPenaltyApplied = false;
                _rentalLossNoticeDueGameTime = -1;
                _rentalLossPenaltyDueGameTime = -1;
                _rentalCleanupStartGameTime = -1;

                _airWantedZoneTriggered = false;
                _rentedVehicleIsAir = false;
                ClearAirWantedFreeze();

                StopRentalStreamAnchor();
                return;
            }

            KeepRentedVehicleAlive();

            // Giữ khu vực spawn được nạp theo nhịp thưa
            PulseRentalStreamAnchor();

            if (!_rentalEndsAt.HasValue)
                return;

            if (!_rentalExpiryTriggered)
            {
                if (Game.GameTime - _lastRentalTimeCheckGameTime < RENT_CHECK_INTERVAL_MS)
                    return;

                _lastRentalTimeCheckGameTime = Game.GameTime;

                DateTime now = GetCurrentInGameDateTime();
                if (now >= _rentalEndsAt.Value)
                    TriggerRentalExpiry();
            }

            if (_rentalLossSequenceActive)
            {
                UpdateLostRentalSequence();
                return;
            }

            if (_rentalExpiryTriggered)
            {
                UpdateExpiredVehicleDecay();
            }
        }
        catch (Exception ex)
        {
            Log("UpdateRentalLifecycle failed: " + ex);
        }
    }

    private void TriggerRentalExpiry()
    {
        try
        {
            if (_rentedVehicle == null)
            {
                ClearRentalState();
                return;
            }

            if (IsRentalVehicleDestroyedOrMissing())
            {
                BeginLostRentalSequence();
                return;
            }

            _rentalExpiryTriggered = true;
            _rentalCleanupStartGameTime = -1;

            _rentalDecayStartedAt = Game.GameTime;
            _rentalDecayStartVelocity = _rentedVehicle.Velocity;

            try
            {
                Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, _rentedVehicle.Handle, true);
            }
            catch { }

            try
            {
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, _rentedVehicle.Handle, false, true, true);
            }
            catch { }

            // Thêm đoạn mã xử lý vật lý cho máy bay sau khi tắt engine / undriveable
            if (_rentedVehicleIsAir)
            {
                try
                {
                    Function.Call(Hash.SET_ENTITY_HAS_GRAVITY, _rentedVehicle.Handle, true);
                }
                catch { }

                try
                {
                    Function.Call(Hash.SET_ENTITY_DYNAMIC, _rentedVehicle.Handle, true);
                }
                catch { }

                try
                {
                    Function.Call(Hash.ACTIVATE_PHYSICS, _rentedVehicle.Handle);
                }
                catch { }
            }

            try
            {
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _rentedVehicle.Handle, 2);
            }
            catch { }

            GTA.UI.Screen.ShowSubtitle(BenefactorRentalExpiredText, 3000);
        }
        catch (Exception ex)
        {
            Log("TriggerRentalExpiry failed: " + ex);
        }
    }

    private void UpdateExpiredVehicleDecay()
    {
        try
        {
            if (_rentedVehicle == null)
            {
                ClearRentalState();
                return;
            }

            if (IsRentalVehicleDestroyedOrMissing())
            {
                BeginLostRentalSequence();
                return;
            }

            try
            {
                Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, _rentedVehicle.Handle, true);
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, _rentedVehicle.Handle, false, true, true);
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _rentedVehicle.Handle, 2);
            }
            catch { }

            if (_rentalDecayStartedAt < 0)
            {
                _rentalDecayStartedAt = Game.GameTime;
                _rentalDecayStartVelocity = _rentedVehicle.Velocity;
            }

            float elapsed = Game.GameTime - _rentalDecayStartedAt;
            float t = Math.Max(0f, Math.Min(1f, elapsed / (float)RENT_DECAY_DURATION_MS));

            bool isAirVehicle = IsExpiredRentalAirVehicle();

            if (isAirVehicle)
            {
                try
                {
                    Function.Call(Hash.SET_ENTITY_HAS_GRAVITY, _rentedVehicle.Handle, true);
                }
                catch { }

                try
                {
                    Function.Call(Hash.SET_ENTITY_DYNAMIC, _rentedVehicle.Handle, true);
                }
                catch { }

                try
                {
                    Function.Call(Hash.ACTIVATE_PHYSICS, _rentedVehicle.Handle);
                }
                catch { }

                Vector3 currentVelocity = Vector3.Zero;
                try
                {
                    currentVelocity = _rentedVehicle.Velocity;
                }
                catch { }

                // Giảm dần lực ngang để máy bay/trực thăng không còn bay tiếp,
                // nhưng vẫn giữ trọng lực để hạ xuống mượt.
                float horizontalFactor = 1f - (0.85f * t);
                if (horizontalFactor < 0f)
                    horizontalFactor = 0f;

                // Tạo tốc độ rơi xuống tăng dần theo thời gian, nhưng vẫn mềm.
                float targetDownSpeed = -(0.25f + (3.5f * t));
                if (targetDownSpeed < -4.5f)
                    targetDownSpeed = -4.5f;

                Vector3 newVelocity = new Vector3(
                    currentVelocity.X * horizontalFactor,
                    currentVelocity.Y * horizontalFactor,
                    currentVelocity.Z);

                // Ép Z xuống thấp dần để mất lift và hạ từ từ.
                if (newVelocity.Z > targetDownSpeed)
                    newVelocity.Z = targetDownSpeed;

                try
                {
                    if (!IsEntityOnGroundSafe(_rentedVehicle))
                        _rentedVehicle.Velocity = newVelocity;
                    else
                        _rentedVehicle.Velocity = Vector3.Zero;
                }
                catch { }
            }
            else
            {
                // Xe thường: giữ nguyên logic cũ
                float factor = 1f - t;
                factor = factor * factor;

                try
                {
                    _rentedVehicle.Velocity = _rentalDecayStartVelocity * factor;
                }
                catch { }

                if (t >= 1f || _rentedVehicle.Velocity.Length() <= 0.03f)
                {
                    try
                    {
                        _rentedVehicle.Velocity = Vector3.Zero;
                    }
                    catch { }
                }
            }

            bool playerInsideRental = false;
            try
            {
                Ped player = Game.Player.Character;
                if (player != null && player.Exists() && player.CurrentVehicle != null && player.CurrentVehicle.Exists() && player.CurrentVehicle.Handle == _rentedVehicle.Handle)
                    playerInsideRental = true;
            }
            catch { }

            if (!playerInsideRental)
            {
                if (_rentalCleanupStartGameTime < 0)
                    _rentalCleanupStartGameTime = Game.GameTime;

                if (Game.GameTime - _rentalCleanupStartGameTime >= RENT_DECAY_STOP_DELETE_DELAY_MS)
                {
                    CleanupRentedVehicle();
                }
            }
            else
            {
                _rentalCleanupStartGameTime = Game.GameTime;
            }
        }
        catch (Exception ex)
        {
            Log("UpdateExpiredVehicleDecay failed: " + ex);
        }
    }

    private void CleanupRentedVehicle()
    {
        try
        {
            ClearRentalSpawnBlip();

            if (_rentedVehicle != null)
            {
                try
                {
                    if (_rentedVehicle.Exists())
                    {
                        try { _rentedVehicle.MarkAsNoLongerNeeded(); } catch { }
                        try { _rentedVehicle.Delete(); } catch { }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log("CleanupRentedVehicle failed: " + ex);
        }
        finally
        {
            StopRentalStreamAnchor();
            ClearRentalState();
        }
    }

    private void ClearRentalState()
    {
        ClearRentalSpawnBlip();
        StopRentalStreamAnchor();

        _rentedVehicle = null;
        _rentalStartedAt = null;
        _rentalEndsAt = null;
        _rentalExpiryTriggered = false;
        _rentalCleanupStartGameTime = -1;
        _rentalDecayStartedAt = -1;
        _rentalDecayStartVelocity = Vector3.Zero;

        _rentedVehicleIsAir = false;
        _airWantedZoneTriggered = false;
        ClearAirWantedFreeze();

        _rentedBasePrice = 0;
        _rentalLossSequenceActive = false;
        _rentalLossNoticeSent = false;
        _rentalLossPenaltyApplied = false;
        _rentalLossNoticeDueGameTime = -1;
        _rentalLossPenaltyDueGameTime = -1;

        _rentalLossCompensation = -1;
    }

    private void ShowBenefactorNotification(string message)
    {
        PlayFrontendSound(BenefactorMessageSoundName, BenefactorMessageSoundSet);

        try
        {
            Notification.Show(NotificationIcon.Bikesite, BenefactorContactName, L("Benefactor_NotificationTitle", "Dịch vụ thuê xe"), message);
        }
        catch
        {
            try { GTA.UI.Screen.ShowSubtitle(message, 3000); } catch { }
        }
    }

    private void PlayFrontendSound(string soundName, string soundSet)
    {
        try
        {
            Audio.PlaySoundFrontend(soundName, soundSet);
        }
        catch
        {
            try
            {
                Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, soundName, soundSet, true);
            }
            catch
            {
            }
        }
    }

    private static void Log(string message)
    {
        try
        {
            // Keep silent by default.
            // File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Benefactor.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch { }
    }
}