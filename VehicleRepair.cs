using GTA;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using System;
using System.Linq;
using System.Windows.Forms;

public class AutoRepairCtrlZ : Script
{
    private static string ContactName(string key, string fallback)
    {
        return Language.Get(key, fallback);
    }

    // cấu hình
    private readonly int DefaultInterval = 1700; // ms khi không hiển thị help-box
    private readonly int HelpDurationMs = 15000; // 15 giây

    private const int REPAIR_CALL_DURATION_MS = 2000;

    // --- thêm hằng phí tối thiểu cho thay đèn ---
    private readonly int MinLightPrice = 1000;

    // --- phí đánh bóng ---
    private readonly int PolishPrice = 300;

    // --- cooldown cho phím Ctrl+Z (8 giây) ---
    private readonly int HelpBoxCooldownMs = 8000;
    private int _helpBoxCooldownExpiry = 0;

    private const string ES = "~INPUT_FRONTEND_CANCEL~";   // Esc
    private const string E = "~INPUT_FRONTEND_ACCEPT~";   // Enter
    private const string L = "~INPUT_FRONTEND_LEFT~";   // ICON_LEFT

    // Phone contact replacement for Ctrl+Z
    private CustomiFruit _losSantosPhoneInstance = null;
    private bool _losSantosContactAdded = false;

    // trạng thái
    // 0 = none, 1 = sửa xe, 2 = thay đèn neon, 3 = thay đèn dashboard, 4 = thay đèn xenon, 5 = rửa / đánh bóng
    private int helpStage = 0;
    private int helpEndGameTime = 0;
    private Vehicle targetVehicle = null;
    private int dmgprc = 0;   // damagePercent
    private int rePr = 0;     // repairPrice
    private int liPr = 0;    // lightPrice

    // snapshot % hư hại để state 2 không bị phụ thuộc vào việc state 1 có bị bỏ qua hay không
    private int damageSnapshotPrc = 0;

    private Random _rng = new Random();

    // --- danh sách tên màu (neon / Menyoo names) ---
    private string[] HudNeonNames = new string[] {
        "PURPLELIGHT", "YELLOWLIGHT", "ORANGELIGHT", "GREENLIGHT", "PLATINUM", "SAME_CREW", "YOGA",
        "FRANKLIN", "CONTROLLER_MICHAEL", "DEGEN_CYAN", "REDLIGHT", "SIMPLEBLIP_DEFAULT", "FRIENDLY",
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

    public AutoRepairCtrlZ()
    {
        Language.Initialize();
        Interval = DefaultInterval;
        KeyDown += OnKeyDown;
        Tick += OnTick;
        Aborted += OnAbort;
    }

    private static string T(string key, string fallback = "")
    {
        return Language.Get(key, fallback);
    }

    private static string TF(string key, string fallback, params object[] args)
    {
        return string.Format(Language.Get(key, fallback), args);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            // Ctrl+Z has been replaced by the Los Santos Customs phone contact.

            // Enter = xác nhận, Back = bỏ qua state hiện tại
            if (helpStage != 0 && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Back))
            {
                bool confirm = (e.KeyCode == Keys.Enter);
                HandleCurrentHelpStage(confirm);
                return;
            }
        }
        catch (Exception ex)
        {
            GTA.UI.Screen.ShowSubtitle("~r~Lỗi: AutoRepairCtrlZ: lỗi xử lý phím (" + ex.Message + ")", 3000);
            CloseHelpBox();
        }
    }

    private void HandleCurrentHelpStage(bool confirm)
    {
        switch (helpStage)
        {
            case 1:
                TryPerformRepair(confirm);
                break;

            case 2:
                TryPerformChangeNeon(confirm);
                break;

            case 3:
                TryPerformChangeDashboard(confirm);
                break;

            case 4:
                TryPerformChangeXenon(confirm);
                break;

            case 5:
                TryPerformPolish(confirm);
                break;

            default:
                CloseHelpBox();
                break;
        }
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            EnsureLosSantosCustomsContactRegistered();

            if (helpStage == 0) return;

            if (targetVehicle == null || !targetVehicle.Exists() || targetVehicle.IsDead)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("AutoRepair_InvalidVehicle", "~r~Lỗi: Phương tiện đã hỏng hoặc không tồn tại."),
                    3000
                );
                CloseHelpBox();
                return;
            }

            int now = Game.GameTime;
            if (now >= helpEndGameTime)
            {
                CloseHelpBox();
                return;
            }

            string text = "";

            if (helpStage == 1)
            {
                text =
                     T("AutoRepair_RepairTitle", "~b~~h~SỬA PHƯƠNG TIỆN~s~") + "\n" +
                     TF("AutoRepair_DamageLine", "Mức hư: {0}%", dmgprc) + "\n" +
                     TF("AutoRepair_RepairPriceLine", "Giá: ${0:N0}", rePr) + "\n" +
                     TF("AutoRepair_RepairHint", "{0} để sửa", E) + "\n" +
                     TF("AutoRepair_CancelHint", "{0} để hủy", L);
            }
            else if (helpStage == 2)
            {
                int lightDamagePrc = damageSnapshotPrc > 0 ? damageSnapshotPrc : dmgprc;
                liPr = Math.Max(lightDamagePrc * 60, MinLightPrice);

                text =
                   T("AutoRepair_NeonTitle", "~b~~h~THAY ĐÈN NEON~s~") + "\n" +
                   TF("AutoRepair_NeonPrompt",
                   "Bạn có muốn thay đèn neon với giá ${0:N0} không?",
                   liPr) + "\n" +
                   TF("AutoRepair_PayHint", "{0} để tiếp tục", E) + "\n" +
                   TF("AutoRepair_CancelHint", "{0} để bỏ qua", L);
            }
            else if (helpStage == 3)
            {
                text =
                   T("AutoRepair_DashboardTitle", "~b~~h~THAY ĐÈN DASHBOARD~s~") + "\n" +
                   T("AutoRepair_DashboardPrompt", "Bạn có muốn thay đèn dashboard cho xe không?") + "\n" +
                   TF("AutoRepair_PayHint", "{0} để tiếp tục", E) + "\n" +
                   TF("AutoRepair_CancelHint", "{0} để bỏ qua", L);
            }
            else if (helpStage == 4)
            {
                text =
                   T("AutoRepair_XenonTitle", "~b~~h~THAY ĐÈN XENON~s~") + "\n" +
                   T("AutoRepair_XenonPrompt", "Bạn có muốn lắp đèn Xenon cho xe không?") + "\n" +
                   TF("AutoRepair_PayHint", "{0} để tiếp tục", E) + "\n" +
                   TF("AutoRepair_CancelHint", "{0} để bỏ qua", L);
            }
            else if (helpStage == 5)
            {
                text =
                   T("AutoRepair_PolishTitle", "~b~~h~RỬA & ĐÁNH BÓNG~s~") + "\n" +
                   TF("AutoRepair_PolishPrompt",
                   "~h~Bạn có muốn đánh bóng phương tiện với giá ${0:N0} không?",
                   PolishPrice) + "\n" +
                   TF("AutoRepair_WashHint", "{0} để rửa", E) + "\n" +
                   TF("AutoRepair_CancelHint", "{0} để hủy bỏ", L);
            }

            GTA.UI.Screen.ShowHelpTextThisFrame(text);
        }
        catch (Exception ex)
        {
            ShowFeedMessage("LS Customs", "Lỗi", "AutoRepairCtrlZ Tick error: " + ex.Message);
            CloseHelpBox();
        }
    }

    private void TryPerformRepair(bool confirm)
    {
        try
        {
            if (targetVehicle == null || !targetVehicle.Exists())
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("AutoRepair_RepairVehicleMissing", "~r~Phương tiện đã hỏng."),
                    3000
                );
                CloseHelpBox();
                return;
            }

            // Back ở state 1: không sửa xe, không trừ tiền, chuyển sang state 2
            if (!confirm)
            {
                StartHelpStage(2);
                return;
            }

            int playerMoney = Game.Player.Money;

            if (playerMoney < rePr)
            {
                GTA.UI.Screen.ShowSubtitle(
                    TF("AutoRepair_NotEnoughMoneyRepair",
                    "~r~Không đủ tiền để sửa phương tiện. Cần: ~s~${0:N0}",
                    rePr),
                    3000
                );
                CloseHelpBox();
                return;
            }

            Game.Player.Money -= rePr;

            try
            {
                targetVehicle.EngineHealth = 1000f;
                targetVehicle.BodyHealth = 1000f;
                targetVehicle.Repair();
            }
            catch
            {
                targetVehicle.EngineHealth = 1000f;
                targetVehicle.BodyHealth = 1000f;
            }

            targetVehicle.IsDriveable = true;

            if (confirm)
            {
                ShowFeedMessage(
                    T("AutoRepair_FeedSender", "LS Customs"),
                    T("AutoRepair_RepairSubject", "Sửa phương tiện"),
                    T("AutoRepair_RepairBody", "Xưởng đã sửa hoàn tất rồi. Nếu có vấn đề gì thì cứ gọi đến nhé! Xưởng sẽ phục vụ quý khách hết mình!")
                );
            }

            StartHelpStage(2);
        }
        catch (Exception ex)
        {
            GTA.UI.Screen.ShowSubtitle("Lỗi khi sửa: " + ex.Message);
            CloseHelpBox();
        }
    }

    private void TryPerformChangeNeon(bool confirm)
    {
        try
        {
            if (targetVehicle == null || !targetVehicle.Exists())
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("AutoRepair_LightVehicleMissing", "Lỗi: Phương tiện không tồn tại."),
                    3000
                );
                CloseHelpBox();
                return;
            }

            // Back ở state 2: không thay neon, chuyển sang state 3
            if (!confirm)
            {
                StartHelpStage(3);
                return;
            }

            int playerMoney = Game.Player.Money;
            if (playerMoney < liPr)
            {
                GTA.UI.Screen.ShowSubtitle(
                    TF("AutoRepair_NotEnoughMoneyLight",
                    "~r~Không đủ tiền để thay đèn. Cần: ~s~${0:N0}",
                    liPr),
                    3000
                );
                CloseHelpBox();
                return;
            }

            Game.Player.Money -= liPr;

            ApplyNeonLight(targetVehicle);

            ShowFeedMessage(
                T("AutoRepair_FeedSender", "LS Customs"),
                T("AutoRepair_NeonSubject", "Thay đèn neon"),
                T("AutoRepair_NeonBody", "Đèn neon đã được thay xong. Màu này có hợp với bạn không? Không hợp thì mua tiếp để đổi màu thôi hehe!!")
            );

            StartHelpStage(3);
        }
        catch (Exception ex)
        {
            ShowFeedMessage("LS Customs", "Lỗi", "Lỗi khi thay đèn neon: " + ex.Message);
            CloseHelpBox();
        }
    }

    private void TryPerformChangeDashboard(bool confirm)
    {
        try
        {
            if (targetVehicle == null || !targetVehicle.Exists())
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("AutoRepair_LightVehicleMissing", "Lỗi: Phương tiện không tồn tại."),
                    3000
                );
                CloseHelpBox();
                return;
            }

            // Back ở state 3: không thay dashboard, chuyển sang state 4
            if (!confirm)
            {
                StartHelpStage(4);
                return;
            }

            ApplyDashboardLight(targetVehicle);

            ShowFeedMessage(
                T("AutoRepair_FeedSender", "LS Customs"),
                T("AutoRepair_DashboardSubject", "Thay đèn dashboard"),
                T("AutoRepair_DashboardBody", "Đèn dashboard đã được thay xong. Xem màu có ưng ý không, nếu không cứ đổi tiếp nha, hihi!")
            );

            StartHelpStage(4);
        }
        catch (Exception ex)
        {
            ShowFeedMessage("LS Customs", "Lỗi", "Lỗi khi thay đèn dashboard: " + ex.Message);
            CloseHelpBox();
        }
    }

    private void TryPerformChangeXenon(bool confirm)
    {
        try
        {
            if (targetVehicle == null || !targetVehicle.Exists())
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("AutoRepair_LightVehicleMissing", "Lỗi: Phương tiện không tồn tại."),
                    3000
                );
                CloseHelpBox();
                return;
            }

            // Back ở state 4: không lắp xenon, chuyển sang state 5
            if (!confirm)
            {
                StartHelpStage(5);
                return;
            }

            ApplyXenonUpgrade(targetVehicle);

            ShowFeedMessage(
                T("AutoRepair_FeedSender", "LS Customs"),
                T("AutoRepair_XenonSubject", "Thay đèn Xenon"),
                T("AutoRepair_XenonBody", "Đèn Xenon đã được lắp xong. Có tới 13 mẫu màu đèn khác nhau nên cứ chọn thoải mái nhé!")
            );

            StartHelpStage(5);
        }
        catch (Exception ex)
        {
            ShowFeedMessage("LS Customs", "Lỗi", "Lỗi khi thay đèn xenon: " + ex.Message);
            CloseHelpBox();
        }
    }

    private void TryPerformPolish(bool confirm)
    {
        try
        {
            if (targetVehicle == null || !targetVehicle.Exists())
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("AutoRepair_PolishVehicleMissing", "Lỗi: Phương tiện không tồn tại."),
                    3000
                );
                CloseHelpBox();
                return;
            }

            // Back ở state 3: không đánh bóng, không trừ tiền, đóng menu
            if (!confirm)
            {
                CloseHelpBox();
                return;
            }

            int playerMoney = Game.Player.Money;
            if (playerMoney < PolishPrice)
            {
                GTA.UI.Screen.ShowSubtitle(
                    TF("AutoRepair_NotEnoughMoneyPolish",
                    "~r~Không đủ tiền để đánh bóng. Cần: ~s~${0:N0}",
                    PolishPrice),
                    3000
                );
                CloseHelpBox();
                return;
            }

            Game.Player.Money -= PolishPrice;

            try
            {
                Function.Call(Hash.SET_VEHICLE_DIRT_LEVEL, targetVehicle.Handle, 0f);
                targetVehicle.Wash();
            }
            catch
            {
                try
                {
                    Function.Call(Hash.SET_VEHICLE_DIRT_LEVEL, targetVehicle.Handle, 0f);
                }
                catch { }
            }

            if (confirm)
            {
                ShowFeedMessage(
                    T("AutoRepair_FeedSender", "LS Customs"),
                    T("AutoRepair_PolishSubject", "Đánh bóng phương tiện"),
                    T("AutoRepair_PolishBody", "Phương tiện đã được đánh bóng và làm sạch. Nếu sử dụng bẩn quá thì cứ gọi dịch vụ nha!!")
                );
            }

            CloseHelpBox();
        }
        catch (Exception ex)
        {
            ShowFeedMessage("LS Customs", "Lỗi", "Lỗi khi đánh bóng: " + ex.Message);
            CloseHelpBox();
        }
    }

    private void ApplyNeonLight(Vehicle v)
    {
        if (v == null || !v.Exists()) return;

        try
        {
            for (int i = 0; i <= 3; i++)
            {
                SafeCall(() => { Function.Call(Hash.SET_VEHICLE_NEON_ENABLED, v.Handle, i, false); });
            }

            string chosenName = HudNeonNames[_rng.Next(HudNeonNames.Length)];
            var rgb = NameToRgb(chosenName);

            for (int i = 0; i <= 3; i++)
            {
                SafeCall(() => { Function.Call(Hash.SET_VEHICLE_NEON_ENABLED, v.Handle, i, true); });
            }

            SafeCall(() => { Function.Call(Hash.SET_VEHICLE_NEON_COLOUR, v.Handle, rgb.Item1, rgb.Item2, rgb.Item3); });
        }
        catch { }
    }

    private void ApplyDashboardLight(Vehicle v)
    {
        if (v == null || !v.Exists()) return;

        try
        {
            int paintIndex = _rng.Next(0, 178);
            SafeCall(() => { Function.Call(Hash.SET_VEHICLE_EXTRA_COLOUR_6, v.Handle, paintIndex); });
        }
        catch { }
    }

    private void ApplyXenonUpgrade(Vehicle v)
    {
        if (v == null || !v.Exists()) return;

        try
        {
            Function.Call(Hash.TOGGLE_VEHICLE_MOD, v.Handle, 22, true);

            int xenonColorIndex = _rng.Next(0, 13);
            Function.Call(Hash.SET_VEHICLE_XENON_LIGHT_COLOR_INDEX, v.Handle, xenonColorIndex);
        }
        catch { }
    }

    private void StartHelpStage(int stage)
    {
        helpStage = stage;
        helpEndGameTime = Game.GameTime + HelpDurationMs;
        Interval = 0;
    }

    private void CloseHelpBox()
    {
        helpStage = 0;
        targetVehicle = null;
        dmgprc = 0;
        rePr = 0;
        liPr = 0;
        damageSnapshotPrc = 0;
        helpEndGameTime = 0;
        Interval = DefaultInterval;

        try
        {
            _helpBoxCooldownExpiry = Game.GameTime + HelpBoxCooldownMs;
        }
        catch
        {
            _helpBoxCooldownExpiry = 0;
        }
    }

    private void OnAbort(object sender, EventArgs e)
    {
        CloseHelpBox();
    }

    // ----------------------- Phone contact replacement -----------------------
    private void EnsureLosSantosCustomsContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_losSantosPhoneInstance, phone))
            {
                _losSantosPhoneInstance = phone;
                _losSantosContactAdded = false;
            }

            if (_losSantosContactAdded)
                return;

            string lscName = ContactName("Contact_LosSantosCustom", "Los Santos Custom");

            if (phone.Contacts.Any(c =>
                string.Equals(c.Name, lscName, StringComparison.OrdinalIgnoreCase)))
            {
                _losSantosContactAdded = true;
                return;
            }

            var lsc = new iFruitContact(lscName)
            {
                Active = true,
                DialTimeout = 2000,
                Bold = false,
                Icon = ContactIcon.LSCustoms
            };

            lsc.Answered += OnLosSantosCustomsContactAnswered;
            phone.Contacts.Add(lsc);

            _losSantosContactAdded = true;
        }
        catch
        {
        }
    }

    private void OnLosSantosCustomsContactAnswered(iFruitContact sender)
    {
        try
        {
            TriggerLosSantosCustomsRepairOffer();

            try
            {
                CustomiFruit.GetCurrentInstance()?.Close(0);
            }
            catch { }
        }
        catch (Exception ex)
        {
            GTA.UI.Screen.ShowSubtitle("~r~Lỗi: Los Santos Customs contact failed: " + ex.Message, 3000);
        }
    }

    private void TriggerLosSantosCustomsRepairOffer()
    {
        try
        {
            if (helpStage != 0)
                return;

            int remainMs = Math.Max(0, _helpBoxCooldownExpiry - Game.GameTime);
            if (remainMs > 0)
            {
                int sec = (int)Math.Ceiling(remainMs / 1000.0);
                GTA.UI.Screen.ShowSubtitle(
                    TF("AutoRepair_CooldownMessage",
                    "Đợi ~HUD_COLOUR_DEGEN_GREEN~{0}s~s~ trước khi mở menu.",
                    sec),
                    3000
                );
                return;
            }

            var ped = Game.Player.Character;
            Vehicle v = null;

            if (ped.IsInVehicle())
            {
                v = ped.CurrentVehicle;
            }

            if (v == null)
            {
                GTA.UI.Screen.ShowSubtitle(
                    T("AutoRepair_NoVehicle", "~r~Không có phương tiện để kiểm tra."),
                    3000
                );
                return;
            }

            targetVehicle = v;

            // tính % hư hại tổng quát:
            float engine = ClampFloat(targetVehicle.EngineHealth, 0f, 1000f);
            float body = ClampFloat(targetVehicle.BodyHealth, 0f, 1000f);

            float deficitEngine = 1000f - engine;
            float deficitBody = 1000f - body;

            float percent = (deficitEngine + deficitBody) / 20.0f;
            percent = Math.Min(percent, 99f);
            if (percent < 0f) percent = 0f;

            dmgprc = (int)Math.Round(percent);
            damageSnapshotPrc = dmgprc;

            // giá sửa
            double rawPrice = dmgprc * 1543.2;
            rePr = (int)Math.Round(rawPrice);

            StartHelpStage(1);
        }
        catch (Exception ex)
        {
            GTA.UI.Screen.ShowSubtitle("~r~Lỗi: AutoRepairCtrlZ contact failed (" + ex.Message + ")", 3000);
            CloseHelpBox();
        }
    }

    private float ClampFloat(float v, float min, float max)
    {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }

    private void ShowFeedMessage(string sender, string subject, string body)
    {
        try
        {
            Notification.Show(NotificationIcon.LsCustoms, sender, subject, body);
        }
        catch
        {
            GTA.UI.Notification.Show(body);
        }
    }

    private void SafeCall(Action action)
    {
        try { action(); } catch { }
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
}