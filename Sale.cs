using GTA;
using GTA.Native;
using GTA.UI;
using System;
using System.Globalization;
using System.IO;

public partial class InstantRefill
{
    // Sale multiplier (price * saleMultiplier). Default = 0.3
    private double _saleMultiplier = 0.3;

    // vehicle sale flag
    private bool _vehOS = false;
    private const int _vehiclesSalePrice = 390000;

    private const double VehicleDiscountTicketMultiplier = 0.77;   // thẻ giảm giá ~23%

    private readonly string _vehicleDiscountTicketFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PersistentManager", "MoneyTruckEvent_VehicleDiscountTicket.dat");

    private int _vehicleDiscountTicketCount = 0;

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

    private int GetVehicleDiscountTicketCount()
    {
        LoadVehicleDiscountTicketState();
        return Math.Max(0, _vehicleDiscountTicketCount);
    }

    private bool TryConsumeVehicleDiscountTicket()
    {
        LoadVehicleDiscountTicketState();

        if (_vehicleDiscountTicketCount <= 0)
            return false;

        _vehicleDiscountTicketCount--;
        if (_vehicleDiscountTicketCount < 0)
            _vehicleDiscountTicketCount = 0;

        SaveVehicleDiscountTicketState();
        return true;
    }

    private int ApplyVehicleDiscountTicket(int price)
    {
        if (price <= 0)
            return 0;

        return Math.Max(0, (int)Math.Ceiling(price * VehicleDiscountTicketMultiplier));
    }

    // --- NEW: time-gated sale roll state ---
    // Mỗi lần roll xong sẽ đẩy lịch sang 2 hoặc 3 ngày in-game tiếp theo.
    private bool _vehSaleInitialized = false;
    private int _vehSaleLastDayOfWeek = -1;
    private int _vehSaleWeekSerial = 0;
    private int _vehSaleNextEligibleDaySerial = -1;       // Ngày serial in-game mà lần roll tiếp theo được phép diễn ra.
    private int _vehSaleLastAttemptDaySerial = -1;        // Chống roll lặp nhiều lần trong cùng ngày đủ điều kiện.
    private int _vehSaleExpiryGameTime = 0;
    private const int VehicleSaleDurationMs = 600000;     // Sale kéo dài 600 giây
    private const int VehicleSaleStartHour = 17;          // 17h bắt đầu
    private const int VehicleSaleEndHour = 21;            // 21h kết thúc
    private const int VehicleSaleChancePercent = 30;      // 30% kích hoạt khuyến mãi

    // 50/50 chọn 2 ngày hoặc 3 ngày cho lần thử tiếp theo
    private const int VehicleSaleGapChancePercent = 50;

    // 1) THÊM FIELD MỚI
    private int _lastVehicleSaleAutoCheckTime = -90000;

    // ----------------------- Sale helpers -----------------------
    private void ShowVehicleSaleMessage(bool isSale)
    => Helper.ShowVehicleSaleMessage(isSale);

    private int GetCurrentInGameHour()
    {
        return SafeCall(() => Function.Call<int>(Hash.GET_CLOCK_HOURS), 0);
    }

    private int GetCurrentInGameDayKey()
    {
        return SafeCall(() => Function.Call<int>(Hash.GET_CLOCK_DAY_OF_WEEK), -1);
    }

    private bool IsVehicleSaleWindowNow()
    {
        int hour = GetCurrentInGameHour();
        return hour >= VehicleSaleStartHour && hour < VehicleSaleEndHour;
    }

    private void RefreshVehicleSaleDailyLock()
    {
        EnsureVehicleSaleScheduleInitialized();
        GetCurrentInGameDaySerial();
    }

    private void StopVehicleSaleIfExpired()
    {
        if (_vehOS && _vehSaleExpiryGameTime > 0 && Game.GameTime >= _vehSaleExpiryGameTime)
        {
            _vehOS = false;
            _vehSaleExpiryGameTime = 0;
        }
    }

    private int GetCurrentInGameDaySerial()
    {
        int dow = SafeCall(() => Function.Call<int>(Hash.GET_CLOCK_DAY_OF_WEEK), -1);
        if (dow < 0)
            return -1;

        if (_vehSaleLastDayOfWeek < 0)
        {
            _vehSaleLastDayOfWeek = dow;
            _vehSaleWeekSerial = 0;
            return dow;
        }

        if (dow != _vehSaleLastDayOfWeek)
        {
            if (dow < _vehSaleLastDayOfWeek)
                _vehSaleWeekSerial++;

            _vehSaleLastDayOfWeek = dow;
        }

        return _vehSaleWeekSerial * 7 + dow;
    }

    private void EnsureVehicleSaleScheduleInitialized()
    {
        if (_vehSaleInitialized)
            return;

        int daySerial = GetCurrentInGameDaySerial();
        if (daySerial < 0)
            return;

        _vehSaleInitialized = true;
        _vehSaleNextEligibleDaySerial = daySerial + ((_rng.Next(100) < VehicleSaleGapChancePercent) ? 2 : 3);
        _vehSaleLastAttemptDaySerial = -1;
    }

    private void ScheduleNextVehicleSaleAttempt(int fromDaySerial)
    {
        int gapDays = (_rng.Next(100) < VehicleSaleGapChancePercent) ? 2 : 3;
        _vehSaleNextEligibleDaySerial = fromDaySerial + gapDays;
        _vehSaleLastAttemptDaySerial = fromDaySerial;
    }

    private void TryRollVehicleSale()
    {
        EnsureVehicleSaleScheduleInitialized();
        StopVehicleSaleIfExpired();

        if (_vehOS)
            return;

        int daySerial = GetCurrentInGameDaySerial();
        if (daySerial < 0)
            return;

        // Chưa tới ngày đủ điều kiện thì thôi
        if (daySerial < _vehSaleNextEligibleDaySerial)
            return;

        // Chỉ roll 1 lần trong đúng ngày đủ điều kiện đó
        if (_vehSaleLastAttemptDaySerial == daySerial)
            return;

        // Chỉ roll trong khung giờ sale
        if (!IsVehicleSaleWindowNow())
            return;

        _vehSaleLastAttemptDaySerial = daySerial;

        if (_rng.Next(100) < VehicleSaleChancePercent)
        {
            _vehOS = true;
            _vehSaleExpiryGameTime = Game.GameTime + VehicleSaleDurationMs;
            ShowVehicleSaleMessage(true);
        }
        else
        {
            ShowVehicleSaleMessage(false);
        }

        // Sau mỗi lần đến hạn roll, đẩy sang 2 hoặc 3 ngày tiếp theo
        ScheduleNextVehicleSaleAttempt(daySerial);
    }

    private void TryAutoRollVehicleSale()
    {
        try
        {
            if (!_modReady) return;
            if (Game.IsLoading || Game.IsCutsceneActive) return;
            if (_softDisabled) return;

            if (Game.GameTime - _lastVehicleSaleAutoCheckTime < VehicleSaleAutoCheckIntervalMs)
                return;

            _lastVehicleSaleAutoCheckTime = Game.GameTime;

            EnsureVehicleSaleScheduleInitialized();
            StopVehicleSaleIfExpired();

            if (_vehOS)
                return;

            TryRollVehicleSale();
        }
        catch { }
    }

    private int ComputeVehicleMenuPrice(
    VehicleEntry chosen,
    bool forceFixedSalePrice = false,
    bool applyTicketDiscount = false,
    bool applyPdmSpecialOffer = false)
    {
        if (chosen == null) return 0;

        LoadVehicleDiscountTicketState();

        int price = chosen.GetRandomPrice(_rng, false, 0);

        if (_vehOS)
        {
            if (forceFixedSalePrice)
                price = _vehiclesSalePrice;
            else
                price = (int)Math.Ceiling(price * _saleMultiplier);
        }

        if (applyPdmSpecialOffer)
            price = (int)Math.Ceiling(price * PdmShowroomBridge.CurrentOfferProfile.Multiplier);

        if (applyTicketDiscount)
            price = ApplyVehicleDiscountTicket(price);

        if (chosen.Label == "OPPRESSOR2" && chosen.TimesPurchased > 0 && !_vehOS)
            price = (int)Math.Round(price * 1.3);

        price = ApplyCurrentWorldPriceMultiplier(price);
        return Math.Max(0, price);
    }

    // ----------------------- Vehicle Offer Logic -----------------------
    private void HandleVehicleOffer()
    {
        if (_vehicles.Count == 0)
        {
            GTA.UI.Screen.ShowSubtitle(
                L("VehicleEmptyMessage", "~g~Phương tiện trống."),
                3000
        );
            return;
        }

        var chosen = _vehicles[_rng.Next(_vehicles.Count)];
        ShowVehicleOffer(chosen, false);
    }

    private void ShowVehicleOffer(VehicleEntry chosen, bool selectionMode)
    {
        int price;

        LoadVehicleDiscountTicketState();

        if (selectionMode)
        {
            price = chosen.GetRandomPrice(_rng, false, 0);

            if (_vehOS)
                price = (int)Math.Ceiling(price * _saleMultiplier);
        }
        else
        {
            price = chosen.GetRandomPrice(_rng, _vehOS, _vehiclesSalePrice);
        }

        // KHÔNG tự áp thẻ ở đây nữa
        // price = ApplyVehicleDiscountTicket(price);

        if (chosen.Label == "OPPRESSOR2" && chosen.TimesPurchased > 0 && !_vehOS)
        {
            double increased = Math.Round(price * 1.3);
            price = (int)increased;
        }

        // Chèn thêm đúng một dòng này ngay trước khi dùng price để hiển thị / set pending
        price = ApplyCurrentWorldPriceMultiplier(price);

        bool modelAvailable = SafeCall(() =>
        {
            Model m = new Model((int)chosen.Hash);
            return m.IsInCdImage && m.IsValid;
        }, false);

        if (!modelAvailable)
        {
            GTA.UI.Screen.ShowSubtitle(
                L("VehicleNotFoundMessage", "~HUD_COLOUR_DEGEN_RED~Lỗi: Không tìm thấy phương tiện!"),
                3000
            );
            return;
        }

        return;
    }
}