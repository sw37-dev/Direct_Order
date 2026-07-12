using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using iFruitAddon2;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

public class Violet : Script
{
    private static Violet _singleton;
    private bool _isPrimaryInstance = false;

    private static string T(string key, string fallback)
        => Language.Get(key, fallback);

    private static string VioletContactName = "Violet";
    private static string VioletNotificationBrand => T("Violet_NotificationBrand", "Violet");
    private static string VioletTrialStartTitle => T("Violet_TrialStartTitle", "Violet");
    private static string VioletTrialStartNotice => T("Violet_TrialStartNotice", "Bạn chỉ có ~g~200 giây~s~ để tìm và sử dụng phương tiện tạm thời!");
    private static string VioletTrialEndTitle => T("Violet_TrialEndTitle", "Violet");
    private static string VioletTrialEndNotice => T("Violet_TrialEndNotice", "Hiện tại đã ~HUD_COLOUR_REDLIGHT~kết thúc thử thách~s~ tìm phương tiện rồi!");
    private static string VioletTrialWarningNotice => T("Violet_TrialWarningNotice", "Bạn chỉ còn khoảng ~y~25 giây~s~ để tìm kiếm, hãy cố lên!");
    private static string VioletNoVehiclesLeftNotice => T("Violet_NoVehiclesLeftNotice", "Tôi không tìm thấy định vị nào nữa cả!");
    private static string VioletNoBorrowedVehiclesNotice => T("Violet_NoBorrowedVehiclesNotice", "Tôi chưa tìm thấy phương tiện nào bị bỏ rơi cả!");
    private static string VioletStartFailedNotice => T("Violet_StartFailedNotice", "Không thể bắt đầu thử thách lúc này.");
    private static string VioletNoSearchCenterNotice => T("Violet_NoSearchCenterNotice", "Không thể tìm được khu vực thử thách phù hợp.");
    private static string VioletSpawnFailedNotice => T("Violet_SpawnFailedNotice", "Không thể tạo đủ phương tiện cho thử thách.");
    private static string VioletVioletSearchAreaName => T("Violet_SearchAreaName", "Violet Search Area");
    private static string VioletDefaultVehicleBlipName => T("Violet_DefaultVehicleBlipName", "Phương tiện Violet");
    private static string VioletCooldownFallbackNotice => T("Violet_CooldownFallbackNotice", "Tôi chưa tìm thấy phương tiện nào bị bỏ rơi cả!");
    private const int VioletNotificationTimeout = 2500;

    private const int TrialDurationMs = 200000;      // 200 giây
    private const float SearchCenterDistance = 550f; // tâm vùng cách player 550m
    private const float SearchRadius = 300f;         // vùng tìm kiếm chỉ 300m
    private const int MinSpawnVehicles = 2;
    private const int MaxSpawnVehicles = 3;

    private const int ContactAnswerDelayMs = 750;
    private const int ModelLoadTimeoutMs = 3000;
    private const int SpawnAttemptsPerVehicle = 24;
    private const float MinVehicleSeparation = 70f;

    private const int VioletTrialWarningRemainingMs = 25000;
    private const float VioletTrialTimerX = 0.865f;
    private const float VioletTrialTimerY = 0.850f;
    private const float VioletTrialTimerScale = 1.25f;
    private const float VioletTrialBarX = 0.865f;
    private const float VioletTrialBarY = 0.800f;
    private const float VioletTrialBarWidth = 0.140f;
    private const float VioletTrialBarHeight = 0.030f;
    private const int VioletDoubleWarningSoundDelayMs = 140;
    private const double VioletSpawnIconChance = 0.05;
    private const int VioletDailyMaxUses = 3;

    private const int FranklinHash = -1692214353;
    private const int MichaelHash = 225514697;
    private const int TrevorHash = -1686040670;
    private const int SearchAreaBlipAlpha = 98;

    private int _violetDailyOwnerHash = 0;
    private int _violetDailyDayKey = -1;
    private int _violetDailyUsesLeft = VioletDailyMaxUses;

    private bool _violet25SecWarningSent = false;
    private bool _violetTrialIconsEnabled = false;
    private int _violetLastWarningSoundSecond = -1;
    private int _violetPendingWarningSoundAt = -1;

    private readonly Random _rng = new Random();

    private CustomiFruit _phoneInstance = null;
    private iFruitContact _violetContact = null;
    private bool _violetContactBound = false;

    private bool _trialActive = false;
    private int _trialStartGameTime = 0;
    private int _trialEndGameTime = 0;
    private Vector3 _searchCenter = Vector3.Zero;
    private Blip _searchAreaBlip = null;

    private readonly List<TrialVehicleRecord> _trialVehicles = new List<TrialVehicleRecord>();

    private static readonly uint[] _fallbackVehiclePool =
    {
        0x46699F47u, 0x20314B42u, 0x81BD2ED0u, 0xB779A091u, 0xED552C74u, 0x9A474B5Eu, 0xFE5F0722u, 0x31F0B376u,
        0xF7004C86u, 0x2189D250u, 0x64DE07A1u, 0xD8A914D3u, 0x4BFCF28Bu, 0xA1355F67u, 0x25C5AF13u, 0xFD231729u,
        0x43779C54u, 0xF9300CC5u, 0xCADD5D2Du, 0x05283265u, 0x55365079u, 0x09E478B3u, 0xB1D95DA0u, 0xC972A155u,
        0x52FF9437u, 0x00ABB0C0u, 0x53174EEFu, 0xC1AE4D16u, 0x991EFC04u, 0xE384DD25u, 0x067BC037u, 0x98F65A5Eu,
        0x0796B7A5u,
        0x79C12D73u,
        0x5B531351u,
        0x586765FBu,
        0xF1B44F44u,
        0x107F392Cu,
        0xD876DBE2u,
        0x9C669788u,
        0xE882E5F6u,
        0x5EE005DAu,
        0xE5B3ACA1u,
        0x4EE74355u,
        0x8198AEDCu,
        0x6838FC1Du,
        0x42D623C7u,
        0xE4C8C4Du,
        0x25676EAFu,
        0x9DC66994u,
        0x9229E4EBu,
        0x5502626Cu,
        0xC3F57329u,
        0x92EF6E04u,
        0x432EA949u,
        0xCE23D3BFu,
        0x93F09558u,
        0x8911B9F5u,
        0xFD707EDEu,
        0x843B73DEu,
        0x5BEB3CE0u,
        0x817AFAADu,
        0xDCBC1C3Bu,
        0x2C2C2324u,
        0xF0C2A91Fu,
        0x39D6E83Fu,
        0xB44F0582u,
        0xC3F25753u,
        0x4B6C568Au,
        0xFE141DA6u,
        0xA9EC907Bu,
        0xCA7C4AE9u,
        0x5649FF41u,
        0xBB78956Au,
        0x85E8E76Bu,
        0xE33A477Bu,
        0x1B8165D3u,
        0xB2A716A3u,
        0x5882160Fu,
        0x4FAF0D70u,
        0xD86A0247u,
        0x206D1B68u,
        0xEF813606u,
        0xC07107EEu,
        0xFF5968CDu,
        0x1BF8D381u,
        0x26321E67u,
        0xCD93A7DBu,
        0xC8163646u,
        0x9A9EB7DEu,
        0xC7E55211u,
        0xB79F589Eu,
        0x6EF89CCCu,
        0xC1CE1183u,
        0xB53C6C52u,
        0xDA5819A3u,
        0xD35698EFu,
        0x79DD18AEu,
        0x70241EEAu,
        0x9F6ED5A2u,
        0x91CA96EEu,
        0xDA288376u,
        0xAE12C99Cu,
        0x3DA47243u,
        0xA8E38B01u,
        0x4131F378u,
        0xA0438767u,
        0x34B82784u,
        0x7B54A9D3u,
        0x185E2FF3u,
        0x767164D6u,
        0xB39B0AE6u,
        0x9734F3EAu,
        0xE2E7D4ABu,
        0x33B98FE2u,
        0x3DC92356u,
        0x9DAE1398u,
        0xD80F4A44u,
        0xF2AE3F81u,
        0x75599EA7u,
        0xFDEFAEC3u,
        0xE644E480u,
        0x2C33B46Eu,
        0xA46462F7u,
        0xAD5E30D7u,
        0x71FA16EAu,
        0xAD6065C0u,
        0x8B213907u,
        0xEEF345ECu,
        0xD7C56D39u,
        0x2EA68690u,
        0x04F48FC4u,
        0xCEB28249u,
        0xED62BFA9u,
        0xE00BADABu,
        0x0DF381E5u,
        0x3AF76F4Au,
        0xC5DD6967u,
        0xCABD11E8u,
        0x76D7C404u,
        0xFE0A508Cu,
        0xEA313705u,
        0x381E10BDu,
        0x58E316C7u,
        0xD4AE63D9u,
        0x97398A4Bu,
        0x2E3967B0u,
        0x400F5147u,
        0x2A54C47Du,
        0x9C5E5644u,
        0x2EF89E46u,
        0xA960B13Eu,
        0xE5BA6858u,
        0xEF2295C9u,
        0x28FC5B78u,
        0xD9F0503Du,
        0x4019CB4Cu,
        0xE8983F9Fu,
        0x9C32EB57u,
        0x3AF8C345u,
        0x3E48BF23u,
        0x2C509634u,
        0xF4E1AA15u,
        0x50A6FB9Cu,
        0xE7D2A16Eu,
        0x34DBA661u,
        0x42BC5E19u,
        0xEE6024BCu,
        0x1FD824AFu,
        0xEBC24DF2u,
        0x0D17099Du,
        0x11F58A5Au,
        0x6322B39Au,
        0x58CDAF30u,
        0x56C8A5EFu,
        0x1044926Fu,
        0x3E3D1F59u,
        0x761E2AD3u,
        0x3329757Eu,
        0xF8AB457Bu,
        0x7B406EFBu,
        0x3E2E4F8Au,
        0x185484E1u,
        0xE99011C2u,
        0x10635A0Eu,
        0xA31CB573u,
        0xBC5DC07Eu,
        0x4662BCBBu,
        0x3D7C6410u,
        0x6D6F8F43u,
        0xAA6F980Au,
        0x96E24857u,
        0x8A63C7B9u,
        0x7397224Cu,
        0xB5EF4C33u,
        0xF79A00F7u,
        0x11CBC051u,
        0xAF599F01u,
        0xC4810400u,
        0x142E0DC3u,
        0x5BFA5C4Bu,
        0x1AAD0DEDu,
        0xAE2CC02Au,
        0xC58DA34Au,
        0xB7D9F7F1u,
        0x7E8F677Fu,
        0x36B4A8A9u,
        0x95F6A2C9u,
        0x2D3BD401u,
        0xAC5DF515u,
        0xDE05FB87u,
        0x2714AA93u,
        0xD757D97Du,
        0x4C8DBA51u,
    };

    private sealed class TrialVehicleRecord
    {
        public uint ModelHash;
        public string DisplayName;
        public Vector3 Position;
        public float Heading;
        public string Plate;
        public int OwnerHash;
        public int SpawnGameTime;
        public Vehicle RuntimeVehicle;
        public Blip MapBlip;
    }

    private void ShowVioletNotification(string title, string message, int timeout = VioletNotificationTimeout)
    {
        try
        {
            Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, message);

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_MESSAGETEXT,
                "DIA_VIOLET",
                "DIA_VIOLET",
                false,
                0,
                VioletNotificationBrand,
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
                Screen.ShowSubtitle($"{title}: {message}", timeout);
            }
        }
    }

    private void UnlockSpawnedVehicle(Vehicle veh)
    {
        try
        {
            if (veh == null || !veh.Exists())
                return;

            veh.IsInvincible = false;
            veh.CanBeVisiblyDamaged = true;
            veh.LockStatus = VehicleLockStatus.Unlocked;

            try { veh.IsDriveable = true; } catch { }

            try
            {
                Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED_FOR_ALL_PLAYERS, veh.Handle, false);
            }
            catch { }

            try
            {
                Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, veh.Handle, false);
            }
            catch { }

            try
            {
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, veh.Handle, false, true, true);
            }
            catch { }
        }
        catch
        {
        }
    }

    private static BlipSprite GetVehicleBlipSprite(Vehicle v)
    {
        try
        {
            if (v != null && v.Exists())
            {
                if (v.IsBlimp) return BlipSprite.Blimp;
                if (v.IsSubmarine) return BlipSprite.Sub;
                if (v.IsBoat) return BlipSprite.Boat;
                if (v.IsHelicopter) return BlipSprite.Helicopter;
                if (v.IsPlane) return BlipSprite.Plane;
                if (v.IsBike || v.IsBicycle || v.IsMotorcycle) return BlipSprite.PersonalVehicleBike;
                if (v.IsTrain) return BlipSprite.PersonalVehicleCar;
                return BlipSprite.PersonalVehicleCar;
            }
        }
        catch { }

        return BlipSprite.PersonalVehicleCar;
    }

    private void AttachTrialVehicleBlip(TrialVehicleRecord record)
    {
        try
        {
            if (record == null || record.RuntimeVehicle == null || !record.RuntimeVehicle.Exists())
                return;

            try
            {
                if (record.MapBlip != null && record.MapBlip.Exists())
                    record.MapBlip.Delete();
            }
            catch { }

            Blip blip = record.RuntimeVehicle.AddBlip();
            if (blip == null || !blip.Exists())
                return;

            blip.IsShortRange = false;
            blip.Sprite = GetVehicleBlipSprite(record.RuntimeVehicle);
            blip.Color = BlipColor.Yellow;
            blip.Name = string.IsNullOrWhiteSpace(record.DisplayName)
                ? VioletDefaultVehicleBlipName
                : $"{record.DisplayName}";

            record.MapBlip = blip;
        }
        catch
        {
        }
    }

    private void RemoveTrialVehicleBlip(TrialVehicleRecord record)
    {
        try
        {
            if (record == null)
                return;

            if (record.MapBlip != null && record.MapBlip.Exists())
                record.MapBlip.Delete();

            record.MapBlip = null;
        }
        catch
        {
        }
    }

    public Violet()
    {
        if (_singleton != null)
        {
            _isPrimaryInstance = false;
            return;
        }

        _singleton = this;
        _isPrimaryInstance = true;

        Interval = 0;
        Tick += OnTick;

        EnsureVioletContactRegistered();
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (!_isPrimaryInstance)
            return;

        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            EnsureVioletContactRegistered();
            SyncVioletDailyState();
            SyncVioletCooldownState();

            if (_trialActive)
            {
                int remainingMs = _trialEndGameTime - Game.GameTime;

                if (!_violet25SecWarningSent &&
                    remainingMs > 0 &&
                    remainingMs <= VioletTrialWarningRemainingMs)
                {
                    _violet25SecWarningSent = true;
                    ShowVioletNotification(
                        VioletNotificationBrand,
                        VioletTrialWarningNotice
                    );
                }

                UpdateVioletWarningCountdownSound(remainingMs);

                if (Game.GameTime >= _trialEndGameTime)
                    EndTrial();
                else
                {
                    KeepContactDisabledWhileActive();
                    DrawVioletTrialCountdownTimer(remainingMs);
                    DrawVioletTrialCountdownBar(remainingMs);
                }
            }
            else
            {
                KeepContactEnabledWhenIdle();
            }
        }
        catch
        {
        }
    }

    private void EnsureVioletContactRegistered()
    {
        try
        {
            var phone = CustomiFruit.GetCurrentInstance();
            if (phone == null || phone.Contacts == null)
                return;

            if (!ReferenceEquals(_phoneInstance, phone))
            {
                _phoneInstance = phone;
                _violetContact = null;
                _violetContactBound = false;
            }

            if (_violetContact == null)
            {
                _violetContact = phone.Contacts.FirstOrDefault(c =>
                    c != null &&
                    string.Equals(c.Name, VioletContactName, StringComparison.OrdinalIgnoreCase));
            }

            if (_violetContact == null)
            {
                _violetContact = new iFruitContact(VioletContactName)
                {
                    Active = !_trialActive,
                    DialTimeout = 2000,
                    Bold = false,
                    Icon = new ContactIcon("DIA_VIOLET")
                };

                _violetContact.Answered += OnVioletAnswered;
                phone.Contacts.Add(_violetContact);
                _violetContactBound = true;
                return;
            }

            if (!_violetContactBound)
            {
                _violetContact.Answered += OnVioletAnswered;
                _violetContactBound = true;
            }

            _violetContact.Active = !_trialActive;
        }
        catch
        {
        }
    }

    private void KeepContactDisabledWhileActive()
    {
        try
        {
            if (_violetContact != null)
                _violetContact.Active = false;

            var phone = CustomiFruit.GetCurrentInstance();
            if (phone != null && phone.Contacts != null)
            {
                var c = phone.Contacts.FirstOrDefault(x =>
                    x != null && string.Equals(x.Name, VioletContactName, StringComparison.OrdinalIgnoreCase));
                if (c != null)
                    c.Active = false;
            }
        }
        catch
        {
        }
    }

    private void KeepContactEnabledWhenIdle()
    {
        try
        {
            if (_violetContact != null)
                _violetContact.Active = true;

            var phone = CustomiFruit.GetCurrentInstance();
            if (phone != null && phone.Contacts != null)
            {
                var c = phone.Contacts.FirstOrDefault(x =>
                    x != null && string.Equals(x.Name, VioletContactName, StringComparison.OrdinalIgnoreCase));
                if (c != null)
                    c.Active = true;
            }
        }
        catch { }
    }

    private void DrawVioletTrialCountdownTimer(int remainingMs)
    {
        try
        {
            if (!_trialActive || remainingMs <= 0)
                return;

            int totalSeconds = (remainingMs + 999) / 1000;
            if (totalSeconds < 0)
                totalSeconds = 0;

            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;

            string timerText = remainingMs <= VioletTrialWarningRemainingMs
                ? string.Format(CultureInfo.InvariantCulture, "~w~{0:00} : ~r~{1:00}~s~", minutes, seconds)
                : string.Format(CultureInfo.InvariantCulture, "~w~{0:00} : {1:00}~s~", minutes, seconds);

            DrawCenteredHudText(timerText, VioletTrialTimerX, VioletTrialTimerY, VioletTrialTimerScale);
        }
        catch
        {
        }
    }

    private static void DrawCenteredHudText(string text, float x, float y, float scale)
    {
        try
        {
            Function.Call(Hash.SET_TEXT_FONT, 0);
            Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
            Function.Call(Hash.SET_TEXT_COLOUR, 255, 255, 255, 255);
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            Function.Call(Hash.SET_TEXT_PROPORTIONAL, true);
            Function.Call(Hash.SET_TEXT_OUTLINE);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x, y);
        }
        catch { }
    }

    private void PlayFrontendSound(string soundName, string soundSet)
    {
        try
        {
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, soundName, soundSet, true);
        }
        catch { }
    }

    private void UpdateVioletWarningCountdownSound(int remainingMs)
    {
        try
        {
            if (!_trialActive || remainingMs <= 0)
            {
                _violetLastWarningSoundSecond = -1;
                _violetPendingWarningSoundAt = -1;
                return;
            }

            if (_violetPendingWarningSoundAt >= 0 && Game.GameTime >= _violetPendingWarningSoundAt)
            {
                _violetPendingWarningSoundAt = -1;
                PlayFrontendSound("5_SEC_WARNING", "HUD_MINI_GAME_SOUNDSET");
            }

            int totalSeconds = (remainingMs + 999) / 1000;

            if (totalSeconds > 25)
            {
                _violetLastWarningSoundSecond = -1;
                _violetPendingWarningSoundAt = -1;
                return;
            }

            if (totalSeconds != _violetLastWarningSoundSecond)
            {
                _violetLastWarningSoundSecond = totalSeconds;
                PlayFrontendSound("5_SEC_WARNING", "HUD_MINI_GAME_SOUNDSET");

                if (totalSeconds <= 10)
                    _violetPendingWarningSoundAt = Game.GameTime + VioletDoubleWarningSoundDelayMs;
                else
                    _violetPendingWarningSoundAt = -1;
            }
        }
        catch { }
    }

    private void DrawVioletTrialCountdownBar(int remainingMs)
    {
        try
        {
            if (!_trialActive || remainingMs <= 0)
                return;

            float progress = remainingMs / (float)TrialDurationMs;
            if (progress < 0f) progress = 0f;
            if (progress > 1f) progress = 1f;

            int r, g, b;
            if (remainingMs <= 10000)
            {
                r = 220; g = 45; b = 45;      // đỏ
            }
            else if (remainingMs <= VioletTrialWarningRemainingMs)
            {
                r = 245; g = 195; b = 35;     // vàng
            }
            else
            {
                r = 65; g = 210; b = 85;      // xanh lá
            }

            float left = VioletTrialBarX - (VioletTrialBarWidth * 0.5f);
            float fillWidth = VioletTrialBarWidth * progress;
            if (fillWidth < 0.001f)
                fillWidth = 0.001f;

            // nền
            DrawHudRect(VioletTrialBarX, VioletTrialBarY, VioletTrialBarWidth + 0.003f, VioletTrialBarHeight + 0.003f, 0, 0, 0, 175);
            DrawHudRect(VioletTrialBarX, VioletTrialBarY, VioletTrialBarWidth, VioletTrialBarHeight, 18, 18, 18, 180);

            // phần đã còn lại (shrink từ phải sang trái)
            float fillX = left + (fillWidth * 0.5f);
            DrawHudRect(fillX, VioletTrialBarY, fillWidth, VioletTrialBarHeight, r, g, b, 220);
        }
        catch { }
    }

    private static void DrawHudRect(float x, float y, float w, float h, int r, int g, int b, int a)
    {
        try
        {
            Function.Call(Hash.DRAW_RECT, x, y, w, h, r, g, b, a);
        }
        catch { }
    }

    private void OnVioletAnswered(iFruitContact sender)
    {
        try
        {
            if (_trialActive)
                return;

            var phone = CustomiFruit.GetCurrentInstance();
            if (phone != null)
            {
                try { phone.Close(0); } catch { }
            }

            SyncVioletCooldownState();
            if (IsVioletOnCooldown())
            {
                ShowVioletNotification(
                    VioletNotificationBrand,
                    VioletNoBorrowedVehiclesNotice
                );
                return;
            }

            Script.Wait(ContactAnswerDelayMs);

            StartTrial();
        }
        catch
        {
            try
            {
                if (_violetContact != null)
                    _violetContact.Active = true;
            }
            catch { }
        }
    }

    private void StartTrial()
    {
        try
        {
            if (_trialActive)
                return;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
            {
                Notification.Show(VioletStartFailedNotice);
                return;
            }

            if (!TryFindSearchCenter(player.Position, out Vector3 center))
            {
                Notification.Show(VioletNoSearchCenterNotice);
                return;
            }

            SyncVioletDailyState();

            if (!TryConsumeVioletDailyUse())
            {
                ShowVioletNotification(
                    VioletNotificationBrand,
                    VioletNoVehiclesLeftNotice
                );
                return;
            }

            _trialActive = true;
            _trialStartGameTime = Game.GameTime;
            _trialEndGameTime = Game.GameTime + TrialDurationMs;
            _searchCenter = center;
            _violet25SecWarningSent = false;
            _violetLastWarningSoundSecond = -1;
            _violetPendingWarningSoundAt = -1;

            _violetTrialIconsEnabled = (_rng.NextDouble() < VioletSpawnIconChance);

            SetContactActive(false);

            ShowVioletNotification(
                VioletTrialStartTitle,
                VioletTrialStartNotice
            );

            CleanupSpawnedTrialVehicles(deleteVehicles: false);
            ClearSearchAreaBlip();
            ClearTrialStorageFile();

            CreateSearchAreaBlip(center, GetCurrentCharacterHashSafe());

            if (!SpawnTrialVehicles(center))
            {
                CleanupSpawnedTrialVehicles(deleteVehicles: true);
                ClearSearchAreaBlip();
                ClearTrialStorageFile();
                _trialActive = false;
                SetContactActive(true);
                Notification.Show(VioletSpawnFailedNotice);
                return;
            }

            WriteTrialVehicleStorage();

            if (_violetContact != null)
                _violetContact.Active = false;
        }
        catch
        {
            CleanupSpawnedTrialVehicles(deleteVehicles: true);
            ClearSearchAreaBlip();
            ClearTrialStorageFile();
            _trialActive = false;
            SetContactActive(true);
        }
    }

    private void EndTrial()
    {
        try
        {
            _trialActive = false;
            _trialStartGameTime = 0;
            _trialEndGameTime = 0;
            _violet25SecWarningSent = false;
            _violetTrialIconsEnabled = false;
            _violetLastWarningSoundSecond = -1;
            _violetPendingWarningSoundAt = -1;

            CleanupSpawnedTrialVehicles(deleteVehicles: false);
            ClearSearchAreaBlip();
            ClearTrialStorageFile();

            SaveVioletCooldownState(GetCurrentInGameDateTime().AddHours(3));

            SetContactActive(true);

            ShowVioletNotification(
                VioletTrialEndTitle,
                VioletTrialEndNotice
            );

            _trialVehicles.Clear();
        }
        catch
        {
        }
    }

    private void SetContactActive(bool active)
    {
        try
        {
            if (_violetContact != null)
                _violetContact.Active = active;

            var phone = CustomiFruit.GetCurrentInstance();
            if (phone != null && phone.Contacts != null)
            {
                var c = phone.Contacts.FirstOrDefault(x =>
                    x != null && string.Equals(x.Name, VioletContactName, StringComparison.OrdinalIgnoreCase));
                if (c != null)
                    c.Active = active;
            }
        }
        catch
        {
        }
    }

    private bool SpawnTrialVehicles(Vector3 center)
    {
        try
        {
            var pool = GetSpawnableVehiclePool();
            if (pool.Count == 0)
                return false;

            Shuffle(pool);

            int targetCount = _rng.Next(MinSpawnVehicles, MaxSpawnVehicles + 1);
            int spawnCount = 0;

            var occupied = new List<Vector3>();
            var selectedVehicles = new List<TrialVehicleRecord>();

            foreach (uint modelHash in pool)
            {
                if (spawnCount >= targetCount)
                    break;

                if (!IsSpawnableRoadVehicle(modelHash))
                    continue;

                if (TrySpawnSingleTrialVehicle(modelHash, center, occupied, out TrialVehicleRecord record))
                {
                    selectedVehicles.Add(record);
                    occupied.Add(record.Position);
                    spawnCount++;
                }
            }

            if (spawnCount < MinSpawnVehicles)
            {
                for (int i = selectedVehicles.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (selectedVehicles[i].RuntimeVehicle != null && selectedVehicles[i].RuntimeVehicle.Exists())
                            selectedVehicles[i].RuntimeVehicle.Delete();
                    }
                    catch { }
                }

                return false;
            }

            _trialVehicles.Clear();
            _trialVehicles.AddRange(selectedVehicles);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TrySpawnSingleTrialVehicle(uint modelHash, Vector3 center, List<Vector3> occupied, out TrialVehicleRecord record)
    {
        record = null;

        try
        {
            Model model = new Model((int)modelHash);
            if (!model.IsValid || !model.IsInCdImage)
                return false;

            if (!model.IsLoaded)
            {
                model.Request(ModelLoadTimeoutMs);
                int waited = 0;
                while (!model.IsLoaded && waited < ModelLoadTimeoutMs)
                {
                    Script.Wait(50);
                    waited += 50;
                }
            }

            if (!model.IsLoaded)
                return false;

            if (!TryFindSpawnPointInSearchArea(center, occupied, out Vector3 spawnPos, out float heading))
                return false;

            Vehicle veh = null;
            try
            {
                veh = World.CreateVehicle(model, spawnPos, heading);
            }
            catch
            {
                veh = null;
            }

            if (veh == null || !veh.Exists())
                return false;

            try
            {
                veh.PlaceOnGround();
                veh.Repair();
                veh.DirtLevel = 0f;
                UnlockSpawnedVehicle(veh);
            }
            catch
            {
            }

            string plate = GenerateRandomPlate();
            try
            {
                veh.Mods.LicensePlate = plate;
            }
            catch
            {
            }

            try
            {
                Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, veh.Handle, plate);
            }
            catch
            {
            }

            record = new TrialVehicleRecord
            {
                ModelHash = modelHash,
                DisplayName = SafeGetVehicleDisplayName(modelHash),
                Position = spawnPos,
                Heading = heading,
                Plate = plate,
                OwnerHash = GetCurrentCharacterHashSafe(),
                SpawnGameTime = Game.GameTime,
                RuntimeVehicle = veh
            };

            if (_violetTrialIconsEnabled)
                AttachTrialVehicleBlip(record);

            model.MarkAsNoLongerNeeded();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryFindSearchCenter(Vector3 playerPos, out Vector3 center)
    {
        center = Vector3.Zero;

        try
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                Vector3 dir = RandomUnit2D();
                Vector3 guess = playerPos + dir * SearchCenterDistance;

                if (!TrySnapToRoadNode(guess, out Vector3 snapped, out _))
                    continue;

                float d = snapped.DistanceTo2D(playerPos);
                if (d < 520f || d > 580f)
                    continue;

                if (!IsSearchAreaLikelyLand(snapped, SearchRadius))
                    continue;

                center = snapped;
                return true;
            }

            Vector3 fallback = World.GetNextPositionOnStreet(playerPos + RandomUnit2D() * SearchCenterDistance);
            if (fallback != Vector3.Zero && !IsPointInWater(fallback))
            {
                center = fallback;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private bool IsSearchAreaLikelyLand(Vector3 center, float radius)
    {
        try
        {
            if (IsPointInWater(center))
                return false;

            float sampleRadius = Math.Max(120f, radius * 0.90f);
            for (int i = 0; i < 8; i++)
            {
                double angle = (Math.PI * 2.0 * i) / 8.0;
                Vector3 p = center + new Vector3(
                    (float)Math.Cos(angle) * sampleRadius,
                    (float)Math.Sin(angle) * sampleRadius,
                    0f);

                if (IsPointInWater(p))
                    return false;
            }
        }
        catch
        {
        }

        return true;
    }

    private bool TryFindSpawnPointInSearchArea(Vector3 center, List<Vector3> occupied, out Vector3 spawnPos, out float heading)
    {
        spawnPos = Vector3.Zero;
        heading = 0f;

        try
        {
            for (int attempt = 0; attempt < SpawnAttemptsPerVehicle; attempt++)
            {
                float dist = 20f + (float)_rng.NextDouble() * (SearchRadius - 20f);
                float angle = (float)(_rng.NextDouble() * Math.PI * 2.0);

                Vector3 guess = center + new Vector3(
                    (float)Math.Cos(angle) * dist,
                    (float)Math.Sin(angle) * dist,
                    0f);

                if (!TrySnapToRoadNode(guess, out Vector3 nodePos, out float nodeHeading))
                    continue;

                if (nodePos.DistanceTo2D(center) > SearchRadius)
                    continue;

                if (nodePos.DistanceTo2D(center) < 18f)
                    continue;

                if (IsPointInWater(nodePos))
                    continue;

                if (occupied != null)
                {
                    bool tooClose = false;
                    for (int i = 0; i < occupied.Count; i++)
                    {
                        if (occupied[i].DistanceTo2D(nodePos) < MinVehicleSeparation)
                        {
                            tooClose = true;
                            break;
                        }
                    }

                    if (tooClose)
                        continue;
                }

                spawnPos = nodePos;
                heading = nodeHeading;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private bool TrySnapToRoadNode(Vector3 guess, out Vector3 spawnPos, out float heading)
    {
        spawnPos = Vector3.Zero;
        heading = 0f;

        try
        {
            OutputArgument nodePos = new OutputArgument();
            OutputArgument nodeHeading = new OutputArgument();

            bool found = Function.Call<bool>(
                Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                guess.X, guess.Y, guess.Z + 50f,
                nodePos, nodeHeading,
                1, 3.0f, 0);

            if (!found)
                return false;

            Vector3 p = nodePos.GetResult<Vector3>();
            float h = nodeHeading.GetResult<float>();

            float groundZ = p.Z;
            try
            {
                OutputArgument zArg = new OutputArgument();
                if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, p.X, p.Y, p.Z + 100f, zArg, false))
                    groundZ = zArg.GetResult<float>();
            }
            catch
            {
            }

            spawnPos = new Vector3(p.X, p.Y, groundZ + 0.5f);
            heading = h;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsPointInWater(Vector3 pos)
    {
        try
        {
            var waterHeight = new OutputArgument();

            bool hasWater = Function.Call<bool>(
                Hash.GET_WATER_HEIGHT,
                pos.X, pos.Y, pos.Z + 1.0f,
                waterHeight);

            return hasWater;
        }
        catch
        {
            return false;
        }
    }

    private void CreateSearchAreaBlip(Vector3 center, int ownerHash)
    {
        try
        {
            ClearSearchAreaBlip();

            Blip blip = null;

            try
            {
                blip = Function.Call<Blip>(
                    Hash.ADD_BLIP_FOR_RADIUS,
                    center.X,
                    center.Y,
                    center.Z,
                    SearchRadius);
            }
            catch
            {
                blip = null;
            }

            if (blip == null)
                blip = World.CreateBlip(center);

            if (blip != null)
            {
                blip.Color = GetSearchAreaBlipColorByCharacter(ownerHash);
                blip.IsShortRange = false;
                blip.Name = VioletVioletSearchAreaName;
                try { blip.ShowRoute = false; } catch { }
                try { Function.Call(Hash.SET_BLIP_ALPHA, blip.Handle, SearchAreaBlipAlpha); } catch { }
                _searchAreaBlip = blip;
            }
        }
        catch
        {
        }
    }

    private BlipColor GetSearchAreaBlipColorByCharacter(int ownerHash)
    {
        try
        {
            if (ownerHash == FranklinHash)
                return BlipColor.Green;

            if (ownerHash == MichaelHash)
                return BlipColor.Blue;

            if (ownerHash == TrevorHash)
                return BlipColor.Orange;
        }
        catch { }

        return BlipColor.Green;
    }

    private void ClearSearchAreaBlip()
    {
        try
        {
            if (_searchAreaBlip != null && _searchAreaBlip.Exists())
                _searchAreaBlip.Delete();
        }
        catch
        {
        }

        _searchAreaBlip = null;
    }

    private void WriteTrialVehicleStorage()
    {
        try
        {
            EnsureDataFolderExists();

            string[] lines = _trialVehicles.Select(v =>
            {
                string modelName = (v.DisplayName ?? SafeGetVehicleDisplayName(v.ModelHash) ?? $"0x{v.ModelHash:X8}").Replace("|", "");
                string plate = (v.Plate ?? "").Replace("|", "");

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|0x{1:X8}|{2}|{3}|{4}|{5}|{6}|{7}|{8}",
                    modelName,
                    v.ModelHash,
                    v.Position.X,
                    v.Position.Y,
                    v.Position.Z,
                    v.Heading,
                    plate,
                    v.OwnerHash,
                    v.SpawnGameTime
                );
            }).ToArray();

            WriteLinesAtomic(VioletStorageFilePath, lines);
        }
        catch
        {
        }
    }

    private void ClearTrialStorageFile()
    {
        try
        {
            EnsureDataFolderExists();
            WriteLinesAtomic(VioletStorageFilePath, Array.Empty<string>());
        }
        catch
        {
            try
            {
                if (File.Exists(VioletStorageFilePath))
                    File.WriteAllText(VioletStorageFilePath, string.Empty, Encoding.UTF8);
            }
            catch { }
        }
    }

    private void CleanupSpawnedTrialVehicles(bool deleteVehicles)
    {
        try
        {
            for (int i = _trialVehicles.Count - 1; i >= 0; i--)
            {
                try
                {
                    RemoveTrialVehicleBlip(_trialVehicles[i]);
                }
                catch { }
            }

            if (deleteVehicles)
            {
                for (int i = _trialVehicles.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        var veh = _trialVehicles[i].RuntimeVehicle;
                        if (veh != null && veh.Exists())
                            veh.Delete();
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        _trialVehicles.Clear();
    }

    private static void EnsureDataFolderExists()
    {
        try
        {
            Directory.CreateDirectory(VioletDataFolder);
        }
        catch
        {
        }
    }

    private static string VioletDailyStateFilePath(int ownerHash)
        => Path.Combine(VioletDataFolder, $"violet_daily_state_{ownerHash}.dat");

    private static int GetCurrentGameDayKey()
    {
        try
        {
            int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);
            int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
            int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);

            if (month < 1 || month > 12) month += 1;
            if (year < 1) year = 1;
            month = Math.Max(1, Math.Min(12, month));

            int maxDay = DateTime.DaysInMonth(year, month);
            day = Math.Max(1, Math.Min(maxDay, day));

            return (year * 10000) + (month * 100) + day;
        }
        catch
        {
            return -1;
        }
    }

    private static DateTime GetCurrentInGameDateTime()
    {
        try
        {
            int year = Function.Call<int>(Hash.GET_CLOCK_YEAR);
            int month = Function.Call<int>(Hash.GET_CLOCK_MONTH);
            int day = Function.Call<int>(Hash.GET_CLOCK_DAY_OF_MONTH);
            int hour = Function.Call<int>(Hash.GET_CLOCK_HOURS);
            int minute = Function.Call<int>(Hash.GET_CLOCK_MINUTES);

            if (month < 1 || month > 12) month += 1;
            if (year < 1) year = 1;
            month = Math.Max(1, Math.Min(12, month));

            int maxDay = DateTime.DaysInMonth(year, month);
            day = Math.Max(1, Math.Min(maxDay, day));

            hour = Math.Max(0, Math.Min(23, hour));
            minute = Math.Max(0, Math.Min(59, minute));

            return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        }
        catch
        {
            return new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        }
    }

    private void SyncVioletDailyState()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHashSafe();
            int dayKey = GetCurrentGameDayKey();

            if (ownerHash == 0 || dayKey == -1)
                return;

            if (_violetDailyOwnerHash == ownerHash && _violetDailyDayKey == dayKey)
                return;

            _violetDailyOwnerHash = ownerHash;
            _violetDailyDayKey = dayKey;
            _violetDailyUsesLeft = VioletDailyMaxUses;

            try
            {
                string file = VioletDailyStateFilePath(ownerHash);
                if (File.Exists(file))
                {
                    string text = File.ReadAllText(file, Encoding.UTF8).Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string[] p = text.Split('|');
                        if (p.Length >= 2)
                        {
                            int storedDayKey;
                            int storedUsesLeft;

                            if (int.TryParse(p[0], out storedDayKey) &&
                                int.TryParse(p[1], out storedUsesLeft) &&
                                storedDayKey == dayKey)
                            {
                                _violetDailyUsesLeft = Math.Max(0, Math.Min(VioletDailyMaxUses, storedUsesLeft));
                            }
                        }
                    }
                }
            }
            catch
            {
                _violetDailyUsesLeft = VioletDailyMaxUses;
            }
        }
        catch
        {
        }
    }

    private void SaveVioletDailyState()
    {
        try
        {
            if (_violetDailyOwnerHash == 0 || _violetDailyDayKey == -1)
                return;

            EnsureDataFolderExists();
            WriteLinesAtomic(
                VioletDailyStateFilePath(_violetDailyOwnerHash),
                new[] { string.Format(CultureInfo.InvariantCulture, "{0}|{1}", _violetDailyDayKey, _violetDailyUsesLeft) }
            );
        }
        catch
        {
        }
    }

    private bool TryConsumeVioletDailyUse()
    {
        try
        {
            SyncVioletDailyState();

            if (_violetDailyUsesLeft <= 0)
                return false;

            _violetDailyUsesLeft--;
            SaveVioletDailyState();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string VioletDataFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GTA V Mods", "Violet");

    private static string VioletStorageFilePath
        => Path.Combine(VioletDataFolder, "violet_trial_vehicles.txt");

    private static string VioletCooldownStateFilePath(int ownerHash)
        => Path.Combine(VioletDataFolder, $"violet_cooldown_state_{ownerHash}.dat");

    private bool TryReadVioletCooldownState(out int storedDayKey, out DateTime cooldownUntil)
    {
        storedDayKey = -1;
        cooldownUntil = DateTime.MinValue;

        try
        {
            int ownerHash = GetCurrentCharacterHashSafe();
            if (ownerHash == 0)
                return false;

            string file = VioletCooldownStateFilePath(ownerHash);
            if (!File.Exists(file))
                return false;

            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                int idx = raw.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = raw.Substring(0, idx).Trim().ToLowerInvariant();
                string val = raw.Substring(idx + 1).Trim();

                if (key == "daykey")
                {
                    int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out storedDayKey);
                }
                else if (key == "cooldownuntil")
                {
                    DateTime parsed;
                    if (DateTime.TryParseExact(val, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
                        cooldownUntil = parsed;
                }
            }

            return storedDayKey != -1 && cooldownUntil != DateTime.MinValue;
        }
        catch
        {
            return false;
        }
    }

    private void SaveVioletCooldownState(DateTime cooldownUntil)
    {
        try
        {
            int ownerHash = GetCurrentCharacterHashSafe();
            int dayKey = GetCurrentGameDayKey();

            if (ownerHash == 0 || dayKey == -1)
                return;

            EnsureDataFolderExists();

            WriteLinesAtomic(
                VioletCooldownStateFilePath(ownerHash),
                new[]
                {
                    "version=1",
                    "dayKey=" + dayKey.ToString(CultureInfo.InvariantCulture),
                    "cooldownUntil=" + cooldownUntil.ToString("o", CultureInfo.InvariantCulture)
                }
            );
        }
        catch
        {
        }
    }

    private void ClearVioletCooldownState()
    {
        try
        {
            int ownerHash = GetCurrentCharacterHashSafe();
            if (ownerHash == 0)
                return;

            string file = VioletCooldownStateFilePath(ownerHash);
            if (File.Exists(file))
                File.Delete(file);
        }
        catch
        {
        }
    }

    private bool IsVioletOnCooldown()
    {
        try
        {
            int currentDayKey = GetCurrentGameDayKey();
            if (currentDayKey == -1)
                return false;

            if (!TryReadVioletCooldownState(out int storedDayKey, out DateTime cooldownUntil))
                return false;

            DateTime now = GetCurrentInGameDateTime();

            if (storedDayKey != currentDayKey)
            {
                ClearVioletCooldownState();
                return false;
            }

            if (now >= cooldownUntil)
            {
                ClearVioletCooldownState();
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SyncVioletCooldownState()
    {
        try
        {
            IsVioletOnCooldown();
        }
        catch
        {
        }
    }

    private static void WriteLinesAtomic(string path, string[] lines)
    {
        try
        {
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var sw = new StreamWriter(fs, Encoding.UTF8))
            {
                foreach (var line in lines)
                    sw.WriteLine(line);

                sw.Flush();
                fs.Flush(true);
            }

            if (File.Exists(path))
                File.Delete(path);

            File.Move(tmp, path);
        }
        catch
        {
            try
            {
                var tmp = path + ".tmp";
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch { }
        }
    }

    private static string SafeGetVehicleDisplayName(uint modelHash)
    {
        try
        {
            string name = PersistentManager.GetVehicleDisplayNamePublic(modelHash);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch
        {
        }

        return $"0x{modelHash:X8}";
    }

    private int GetCurrentCharacterHashSafe()
    {
        try
        {
            return Game.Player?.Character?.Model.Hash ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private Vector3 RandomUnit2D()
    {
        double angle = _rng.NextDouble() * Math.PI * 2.0;
        return new Vector3((float)Math.Cos(angle), (float)Math.Sin(angle), 0f);
    }

    private bool IsSpawnableRoadVehicle(uint modelHash)
    {
        try
        {
            Model model = new Model((int)modelHash);
            if (!model.IsValid || !model.IsInCdImage)
                return false;

            if (model.IsBoat || model.IsPlane || model.IsHelicopter || model.IsSubmarine || model.IsBlimp || model.IsTrain)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GenerateRandomPlate()
    {
        try
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";
            char[] plate = new char[8];

            for (int i = 0; i < plate.Length; i++)
                plate[i] = chars[_rng.Next(chars.Length)];

            return new string(plate);
        }
        catch
        {
            return "VIOLET";
        }
    }

    private List<uint> GetSpawnableVehiclePool()
    {
        var pool = new HashSet<uint>();

        try
        {
            Type t = typeof(VehicleDelivery);

            FieldInfo singletonField = t.GetField("_singleton", BindingFlags.Static | BindingFlags.NonPublic);
            object singleton = singletonField?.GetValue(null);

            if (singleton != null)
            {
                FieldInfo allowedField = t.GetField("_allowedVehicles", BindingFlags.Instance | BindingFlags.NonPublic);
                if (allowedField != null)
                {
                    object val = allowedField.GetValue(singleton);
                    if (val is IEnumerable enumerable)
                    {
                        foreach (object item in enumerable)
                        {
                            try
                            {
                                if (item == null)
                                    continue;

                                pool.Add(Convert.ToUInt32(item, CultureInfo.InvariantCulture));
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        if (pool.Count == 0)
        {
            foreach (uint h in _fallbackVehiclePool)
                pool.Add(h);
        }

        return pool.Where(IsSpawnableRoadVehicle).Distinct().ToList();
    }

    private void Shuffle<T>(IList<T> list)
    {
        if (list == null || list.Count <= 1)
            return;

        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            if (j == i)
                continue;

            T tmp = list[i];
            list[i] = list[j];
            list[j] = tmp;
        }
    }
}