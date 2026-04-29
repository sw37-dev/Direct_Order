using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using LemonUI.Menus;
using System;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;

internal static class Helper
{
    private static string L(string key, string fallback = "")
    {
        return Language.Get(key, fallback);
    }

    public static T SafeCall<T>(Func<T> fn, T fallback = default)
    {
        try
        {
            if (fn == null) return fallback;
            return fn();
        }
        catch
        {
            return fallback;
        }
    }

    public static void SafeCall(Action act)
    {
        try
        {
            act?.Invoke();
        }
        catch { }
    }

    public static string TruncateHelpText(string text, int maxChars = 400)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Length <= maxChars) return text;
        return text.Substring(0, maxChars - 3) + "...";
    }

    public static bool TryResolveVehicleHash(string tokenRaw, out uint resolvedHash)
    {
        resolvedHash = 0;
        if (string.IsNullOrWhiteSpace(tokenRaw)) return false;

        string token = tokenRaw.Trim();

        if ((token.StartsWith("\"") && token.EndsWith("\"")) || (token.StartsWith("'") && token.EndsWith("'")))
        {
            token = token.Substring(1, token.Length - 2).Trim();
        }

        try
        {
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                string hex = token.Substring(2);
                uint h = 0;
                if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out h))
                {
                    var m = new Model((int)h);
                    resolvedHash = h;
                    return m.IsValid && m.IsInCdImage;
                }
                return false;
            }

            int hInt = Function.Call<int>(Hash.GET_HASH_KEY, token);
            uint hUint = unchecked((uint)hInt);

            var model = new Model((int)hUint);
            resolvedHash = hUint;
            return model.IsValid && model.IsInCdImage;
        }
        catch
        {
            return false;
        }
    }

    public static int ReadIntAttr(XElement node, string attrName, int fallback)
    {
        try
        {
            string raw = (string)node.Attribute(attrName);
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return v;
        }
        catch { }
        return fallback;
    }

    public static string ReadStringAttr(XElement node, string attrName, string fallback = "")
    {
        try
        {
            string raw = (string)node.Attribute(attrName);
            if (!string.IsNullOrWhiteSpace(raw))
                return raw.Trim();
        }
        catch { }
        return fallback;
    }

    public static void ShowVehicleSaleMessage(bool isSale)
    {
        try
        {
            if (isSale)
            {
                Notification.Show(
                    NotificationIcon.Carsite,
                    "Legendary Motorsport",
                    L("VehicleSale_Title", "Khuyến mãi"),
                    L("VehicleSale_OnSale", "Hiện tại đang trong thời gian giảm giá phương tiện, mau mau chốt đơn nào!!!")
                );
            }
            else
            {
                Notification.Show(
                    NotificationIcon.Carsite,
                    "Legendary Motorsport",
                    L("VehicleSale_Title", "Khuyến mãi"),
                    L("VehicleSale_OffSale", "Hôm nay không có chương trình khuyến mãi rồi! ^^")
                );
            }
        }
        catch
        {
            Notification.Show(isSale
                ? L("VehicleSale_OnSale", "Hiện tại đang trong thời gian giảm giá phương tiện, mau mau chốt đơn nào!!!")
                : L("VehicleSale_OffSale", "Hôm nay không có chương trình khuyến mãi rồi! ^^"));
        }
    }

    public static float GetRandomPreviewHeading(Random rng)
    {
        try
        {
            if (rng == null) rng = new Random();
            return (float)(rng.NextDouble() * 360.0);
        }
        catch
        {
            return 0f;
        }
    }

    public static bool CanPreviewVehicle(InstantRefill.VehicleEntry v, string[] skipTokens)
    {
        try
        {
            if (v == null) return false;
            if (string.IsNullOrWhiteSpace(v.Class)) return true;

            string cls = v.Class.Trim().ToLowerInvariant();
            if (skipTokens == null) return true;

            foreach (string token in skipTokens)
            {
                if (!string.IsNullOrWhiteSpace(token) && cls.Contains(token))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetVehiclePreviewSpawnPoint(Ped player, Random rng, out Vector3 spawnPos, out float heading)
    {
        spawnPos = Vector3.Zero;
        heading = 0f;

        try
        {
            if (player == null || !player.Exists() || player.IsDead)
                return false;

            heading = GetRandomPreviewHeading(rng);

            Vector3 basePos = SafeCall(
                () => Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_IN_WORLD_COORDS, player.Handle, 0.0f, 7.0f, 0.0f),
                player.Position
            );

            float groundZ = basePos.Z;
            try
            {
                var zOut = new OutputArgument();
                bool found = Function.Call<bool>(
                    Hash.GET_GROUND_Z_FOR_3D_COORD,
                    basePos.X, basePos.Y, basePos.Z + 80.0f,
                    zOut,
                    false
                );

                if (found)
                    groundZ = zOut.GetResult<float>();
            }
            catch { }

            spawnPos = new Vector3(basePos.X, basePos.Y, groundZ + 0.03f);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void ShowFeedMessage(string sender, string subject, string body)
    {
        try
        {
            Notification.Show(NotificationIcon.SocialClub, sender, subject, body);
        }
        catch
        {
            GTA.UI.Notification.Show(body);
        }
    }

    public static char GetCharFromKey(Keys k)
    {
        if (k >= Keys.A && k <= Keys.Z)
            return (char)k;

        if (k >= Keys.D0 && k <= Keys.D9)
            return (char)('0' + (k - Keys.D0));

        if (k >= Keys.NumPad0 && k <= Keys.NumPad9)
            return (char)('0' + (k - Keys.NumPad0));

        return '\0';
    }

    public static void HandleBackCancel(
    ref int counter,
    ref bool locked,
    int backCancelThreshold,
    Action<string> showNotification,
    Action<string, int> showSubtitle,
    Action playErrorSound)
    {
        try
        {
            if (locked) return;

            counter++;

            if (counter >= backCancelThreshold)
            {
                counter = 0;
                locked = true;
                showNotification?.Invoke(
                    Language.ReplaceTokens(
                        L("BackCancel_Locked", "~h~Tính năng đã bị khóa do hủy liên tiếp ~o~{count}~s~ lần!"),
                        "{count}", backCancelThreshold.ToString()
                        )
                    );
                playErrorSound?.Invoke();
                return;
            }

            int remaining = backCancelThreshold - counter;

            if (backCancelThreshold == 8)
            {
                switch (remaining)
                {
                    case 4:
                        showNotification?.Invoke(L("BackCancel_Remain4", "~y~~h~Bạn còn 4 lần để hủy đơn!"));
                        break;
                    case 3:
                        showNotification?.Invoke(L("BackCancel_Remain3", "~y~~h~Bạn còn 3 lần!!"));
                        break;
                    case 2:
                        showNotification?.Invoke(L("BackCancel_Remain2", "~y~~h~Chú ý!! Bạn chỉ còn 2 lần trước khi bị khóa tính năng!"));
                        break;
                    case 1:
                        showSubtitle?.Invoke(L("BackCancel_Remain1_Subtitle", "~HUD_COLOUR_DEGEN_RED~~h~Bạn chỉ còn 1 lần cuối thôi!?"), 3000);
                        playErrorSound?.Invoke();
                        break;
                }
            }
            else
            {
                switch (remaining)
                {
                    case 3:
                        showNotification?.Invoke(L("BackCancel_GenericRemain3", "~y~~h~Bạn còn 3 lần để hủy trước khi bị khóa tính năng!!!"));
                        break;
                    case 2:
                        showNotification?.Invoke(L("BackCancel_GenericRemain2", "~y~~h~Bạn chỉ còn 2 lần hủy. Hãy chú ý!!!"));
                        break;
                    case 1:
                        showSubtitle?.Invoke(L("BackCancel_GenericRemain1_Subtitle", "~HUD_COLOUR_DEGEN_RED~~h~Còn 1 lần cuối. Hãy quyết định kỹ trước khi thực hiện!!!"), 3000);
                        playErrorSound?.Invoke();
                        break;
                }
            }
        }
        catch { }
    }

    public static float ClampStat(float value)
    {
        if (value < 0f) return 0f;
        if (value > 100f) return 100f;
        return value;
    }

    public static float NormalizeStat(float raw, float cap)
    {
        if (cap <= 0f) return 0f;
        return ClampStat((raw / cap) * 100f);
    }

    public static string SanitizePlateText(string raw)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var sb = new StringBuilder(8);

            foreach (char ch in raw.Trim().ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(ch) || ch == ' ')
                {
                    sb.Append(ch);
                    if (sb.Length >= 8)
                        break;
                }
            }

            string plate = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(plate) ? null : plate;
        }
        catch
        {
            return null;
        }
    }

    public static string GenerateRandomPlate(Random rng)
    {
        try
        {
            if (rng == null) rng = new Random();

            char L1 = (char)('A' + rng.Next(26));
            char L2 = (char)('A' + rng.Next(26));
            char L3 = (char)('A' + rng.Next(26));

            int d1 = rng.Next(10);
            int d2 = rng.Next(10);
            int d3 = rng.Next(10);
            int d4 = rng.Next(10);
            int d5 = rng.Next(10);

            return $"{d1}{d2}{L1}{L2}{L3}{d3}{d4}{d5}";
        }
        catch
        {
            return "00000000";
        }
    }
}