using GTA;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

public class PlayerIcon : Script
{
    private const int FRANKLIN_HASH = -1692214353;
    private const int MICHAEL_HASH = 225514697;
    private const int TREVOR_HASH = -1686040670;

    private const int TICK_INTERVAL_MS = 0;
    private const int WANTED_CHECK_INTERVAL_MS = 1000;
    private const int RANDOM_COLOR_REFRESH_MS = 80000;

    private const int COLOR_RED = 1;
    private const int COLOR_BEIGE = 36;
    private const int COLOR_LIGHT_ORANGE = 9;
    private const int COLOR_LIGHT_YELLOW = 33;
    private const int COLOR_LIGHT_RED = 35;

    private const int MIN_COLOR_ID = 0;
    private const int MAX_COLOR_ID = 85;

    private static readonly int[] DISALLOWED_RANDOM_COLOR_IDS = new int[]
    {
        1, 75, 79
    };

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GTA V Mods", "PlayerIcon");

    private static string StateFilePrefix = "playericon_state_";

    private readonly Random _rng = new Random();
    private readonly Dictionary<int, PlayerIconState> _states = new Dictionary<int, PlayerIconState>();

    private int _currentOwnerHash = 0;

    private sealed class PlayerIconState
    {
        public int OwnerHash;
        public int StableColorId = -1;

        // Chỉ dùng cho màu ngẫu nhiên ổn định.
        public int NextRandomRefreshAt = 0;
        public bool WasWantedLastCheck = false;
        public int NextWantedCheckAt = 0;
        public int CurrentDesiredColorId = -1;
        public bool Dirty = false;
    }

    public PlayerIcon()
    {
        Interval = TICK_INTERVAL_MS;
        Tick += OnTick;
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading || Game.IsCutsceneActive)
                return;

            int ownerHash = GetCurrentCharacterHash();
            if (ownerHash == 0)
                return;

            if (_currentOwnerHash != ownerHash)
            {
                _currentOwnerHash = ownerHash;
                EnsureStateLoaded(ownerHash);
            }

            PlayerIconState state = GetOrCreateState(ownerHash);
            if (state == null)
                return;

            // Nếu màu ổn định chưa hợp lệ thì khởi tạo ngay và lưu lại.
            if (state.StableColorId < MIN_COLOR_ID ||
                state.StableColorId > MAX_COLOR_ID ||
                IsDisallowedRandomColorId(state.StableColorId))
            {
                state.StableColorId = RollRandomAllowedColorId();
                state.NextRandomRefreshAt = Game.GameTime + RANDOM_COLOR_REFRESH_MS;
                state.Dirty = true;
            }

            if (Game.GameTime >= state.NextWantedCheckAt)
            {
                state.NextWantedCheckAt = Game.GameTime + WANTED_CHECK_INTERVAL_MS;

                int wantedLevel = SafeGetWantedLevel();
                bool isWanted = wantedLevel >= 1;
                bool cameOutOfWanted = state.WasWantedLastCheck && !isWanted;

                state.WasWantedLastCheck = isWanted;

                if (isWanted)
                {
                    // Khi bị truy nã, luôn ép màu theo sao truy nã.
                    state.CurrentDesiredColorId = GetWantedColorId(wantedLevel);
                }
                else
                {
                    // Vừa thoát truy nã: bắt đầu lại chu kỳ 80 giây.
                    if (cameOutOfWanted)
                    {
                        state.NextRandomRefreshAt = Game.GameTime + RANDOM_COLOR_REFRESH_MS;
                        state.Dirty = true;
                    }

                    // Nếu chưa có timer thì khởi tạo timer cho màu ổn định.
                    if (state.NextRandomRefreshAt <= 0)
                    {
                        state.NextRandomRefreshAt = Game.GameTime + RANDOM_COLOR_REFRESH_MS;
                        state.Dirty = true;
                    }

                    // Chỉ khi KHÔNG có truy nã và timer hết hạn thì mới đổi sang màu ngẫu nhiên mới.
                    if (Game.GameTime >= state.NextRandomRefreshAt)
                    {
                        state.StableColorId = RollRandomAllowedColorId();
                        state.NextRandomRefreshAt = Game.GameTime + RANDOM_COLOR_REFRESH_MS;
                        state.Dirty = true;
                    }

                    state.CurrentDesiredColorId = state.StableColorId;
                }
            }

            // Ép màu mỗi frame để game không kéo về trắng mặc định.
            ApplyColorToCurrentPlayerIcon(state.CurrentDesiredColorId);

            if (state.Dirty)
                SaveState(state);
        }
        catch
        {
        }
    }

    private static int GetWantedColorId(int wantedLevel)
    {
        if (wantedLevel >= 5)
            return COLOR_RED;

        if (wantedLevel == 4)
            return COLOR_LIGHT_RED;

        if (wantedLevel == 3)
            return COLOR_LIGHT_ORANGE;

        if (wantedLevel == 2)
            return COLOR_LIGHT_YELLOW;

        if (wantedLevel >= 1)
            return COLOR_BEIGE;

        return COLOR_BEIGE;
    }

    private PlayerIconState GetOrCreateState(int ownerHash)
    {
        try
        {
            if (_states.TryGetValue(ownerHash, out PlayerIconState state))
                return state;

            state = new PlayerIconState
            {
                OwnerHash = ownerHash
            };

            LoadState(state);

            if (state.StableColorId < MIN_COLOR_ID ||
                state.StableColorId > MAX_COLOR_ID ||
                IsDisallowedRandomColorId(state.StableColorId))
            {
                state.StableColorId = RollRandomAllowedColorId();
                state.NextRandomRefreshAt = Game.GameTime + RANDOM_COLOR_REFRESH_MS;
                state.Dirty = true;
            }

            state.CurrentDesiredColorId = state.StableColorId;
            _states[ownerHash] = state;
            return state;
        }
        catch
        {
            return null;
        }
    }

    private void EnsureStateLoaded(int ownerHash)
    {
        try
        {
            GetOrCreateState(ownerHash);
        }
        catch
        {
        }
    }

    private void ApplyColorToCurrentPlayerIcon(int colorId)
    {
        try
        {
            colorId = NormalizeColorId(colorId);
            Function.Call(Hash.SET_PLAYER_ICON_COLOUR, colorId);
        }
        catch
        {
        }
    }

    private int RollRandomAllowedColorId()
    {
        try
        {
            int colorId;
            do
            {
                colorId = _rng.Next(MIN_COLOR_ID, MAX_COLOR_ID + 1);
            }
            while (IsDisallowedRandomColorId(colorId));

            return NormalizeColorId(colorId);
        }
        catch
        {
            return COLOR_BEIGE;
        }
    }

    private static bool IsDisallowedRandomColorId(int colorId)
    {
        if (colorId < MIN_COLOR_ID || colorId > MAX_COLOR_ID)
            return false;

        for (int i = 0; i < DISALLOWED_RANDOM_COLOR_IDS.Length; i++)
        {
            if (colorId == DISALLOWED_RANDOM_COLOR_IDS[i])
                return true;
        }

        return false;
    }

    private static int NormalizeColorId(int colorId)
    {
        if (colorId < MIN_COLOR_ID)
            return MIN_COLOR_ID;

        if (colorId > MAX_COLOR_ID)
            return MAX_COLOR_ID;

        return colorId;
    }

    private static int SafeGetWantedLevel()
    {
        try
        {
            int wanted = Game.Player.WantedLevel;
            if (wanted < 0)
                wanted = 0;
            if (wanted > 5)
                wanted = 5;
            return wanted;
        }
        catch
        {
            return 0;
        }
    }

    private int GetCurrentCharacterHash()
    {
        try
        {
            Ped p = Game.Player.Character;
            if (p == null || !p.Exists())
                return 0;

            int hash = p.Model.Hash;
            if (hash == FRANKLIN_HASH || hash == MICHAEL_HASH || hash == TREVOR_HASH)
                return hash;
        }
        catch
        {
        }

        return 0;
    }

    private static string GetStateFilePath(int ownerHash)
    {
        return Path.Combine(DataFolder, string.Format(CultureInfo.InvariantCulture,
            "{0}{1}.dat", StateFilePrefix, ownerHash));
    }

    private void LoadState(PlayerIconState state)
    {
        try
        {
            if (state == null || state.OwnerHash == 0)
                return;

            Directory.CreateDirectory(DataFolder);

            string file = GetStateFilePath(state.OwnerHash);
            if (!File.Exists(file))
                return;

            foreach (string raw in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                int idx = raw.IndexOf('=');
                if (idx <= 0)
                    continue;

                string key = raw.Substring(0, idx).Trim().ToLowerInvariant();
                string val = raw.Substring(idx + 1).Trim();

                if (key == "stablecolorid")
                {
                    int parsed;
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        state.StableColorId = NormalizeColorId(parsed);
                }
            }
        }
        catch
        {
        }
    }

    private void SaveState(PlayerIconState state)
    {
        try
        {
            if (state == null || state.OwnerHash == 0)
                return;

            Directory.CreateDirectory(DataFolder);

            string file = GetStateFilePath(state.OwnerHash);
            string text =
                "version=1" + Environment.NewLine +
                "stableColorId=" + NormalizeColorId(state.StableColorId).ToString(CultureInfo.InvariantCulture) + Environment.NewLine;

            File.WriteAllText(file, text);
            state.Dirty = false;
        }
        catch { }
    }
}