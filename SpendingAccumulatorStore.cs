using GTA;
using System;
using System.Globalization;
using System.IO;

public sealed class SpendingAccumulatorStore
{
    private readonly string _filePath;
    private long _total;

    public SpendingAccumulatorStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public long Total => _total;

    public void LoadSpendingAccumulator()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _total = 0L;
                return;
            }

            var txt = File.ReadAllText(_filePath).Trim();
            if (long.TryParse(txt, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                _total = Math.Max(0L, v);
            else
                _total = 0L;
        }
        catch
        {
            _total = 0L;
        }
    }

    public void ReloadSpendingAccumulatorFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var txt = File.ReadAllText(_filePath).Trim();
            if (long.TryParse(txt, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                _total = Math.Max(0L, v);
        }
        catch
        {
            // giữ nguyên giá trị hiện tại
        }
    }

    public void SaveSpendingAccumulator()
    {
        try
        {
            string tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, _total.ToString(CultureInfo.InvariantCulture));

            if (File.Exists(_filePath))
                File.Delete(_filePath);

            File.Move(tmp, _filePath);
        }
        catch
        {
            // không crash trong game
        }
    }

    public void AddToSpendingAccumulator(long amount)
    {
        try
        {
            if (amount <= 0) return;
            _total = Math.Max(0L, _total + amount);
            SaveSpendingAccumulator();
        }
        catch
        {
        }
    }

    public void SubtractFromSpendingAccumulator(long amount)
    {
        try
        {
            if (amount <= 0) return;
            _total = Math.Max(0L, _total - amount);
            SaveSpendingAccumulator();
        }
        catch
        {
        }
    }

    public string FormatML(long v)
    {
        try
        {
            return v.ToString("N0", CultureInfo.InvariantCulture);
        }
        catch
        {
            return v.ToString();
        }
    }
}