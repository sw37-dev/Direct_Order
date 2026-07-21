using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

public class Distance : Script
{
    private const int FRANKLIN_HASH = -1692214353;
    private const int MICHAEL_HASH = 225514697;
    private const int TREVOR_HASH = -1686040670;

    private const float MINIMAP_SPAWN_RADIUS = 200.0f;
    private const int SCAN_INTERVAL_MS = 2000;

    private const float MINIMAP_BLINK_ALPHA_175_RADIUS = 350.0f;
    private const float MINIMAP_BLINK_ALPHA_100_RADIUS = 500.0f;
    private const int BLINK_PERIOD_MS = 800;

    private enum DistancePresentationMode
    {
        Far = 0,
        Near = 1,
        BlinkAlpha175 = 2,
        BlinkAlpha100 = 3
    }

    private readonly Dictionary<string, DistancePresentationMode> _presentationModes
        = new Dictionary<string, DistancePresentationMode>(StringComparer.Ordinal);

    // FiveM native docs: 2 = shows on both main map and minimap, 3 = shows on main map only.
    private const int BLIP_DISPLAY_MAP_AND_MINIMAP = 2;
    private const int BLIP_DISPLAY_MAP_ONLY = 3;

    private const int MIN_ICON_ID = 0;
    private const int MAX_ICON_ID = 957;
    private const int MIN_COLOR_ID = 0;
    private const int MAX_COLOR_ID = 85;

    private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private int _nextScanAt;
    private bool _busy;
    private static volatile bool _mmiDistanceEnabled = false;

    public Distance()
    {
        Interval = 1000;
        Tick += OnTick;
        _nextScanAt = Game.GameTime + 1000;
    }

    public static void NotifyMmiActivity()
    {
        try
        {
            _mmiDistanceEnabled = true;
        }
        catch
        {
        }
    }

    private static bool IsMmiDistanceActive()
    {
        try
        {
            return _mmiDistanceEnabled;
        }
        catch
        {
            return false;
        }
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            if (Game.IsLoading)
                return;

            // Chưa có xác nhận restore MMI thì Distance hoàn toàn ngủ.
            if (!IsMmiDistanceActive())
                return;

            if (_busy)
                return;

            _busy = true;
            try
            {
                // Cập nhật nhấp nháy
                UpdateBlinkingPresentations();

                // Chỉ quét khoảng cách
                if (Game.GameTime >= _nextScanAt)
                {
                    _nextScanAt = Game.GameTime + SCAN_INTERVAL_MS;
                    UpdateDistanceManagement();
                    UpdateBlinkingPresentations();
                }
            }
            finally
            {
                _busy = false;
            }
        }
        catch
        {
            _busy = false;
        }
    }

    private void UpdateDistanceManagement()
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            int currentCharHash = GetCurrentCharacterHash(player);
            if (!IsSupportedCharacter(currentCharHash))
                return;

            Vector3 playerPos = player.Position;
            bool blinkOn = IsBlinkPhaseOn();

            foreach (var pv in EnumeratePersistentVehiclesSnapshot())
            {
                try
                {
                    if (pv == null)
                        continue;

                    int ownerHash = ReadInt(pv, "OwnerModelHash", 0);
                    if (ownerHash != currentCharHash)
                        continue;

                    // Symbolix has priority:
                    // if any preview field is active, Distance must not fight it.
                    if (HasAnyPreviewActive(pv))
                    {
                        KeepPreviewFriendlyVisibility(pv);
                        continue;
                    }

                    string key = BuildVehicleKey(pv);
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    Vector3 vehiclePos = ReadVector3(pv, "Position", Vector3.Zero);
                    if (vehiclePos == Vector3.Zero)
                        continue;

                    float distance = playerPos.DistanceTo2D(vehiclePos);

                    if (distance <= MINIMAP_SPAWN_RADIUS)
                    {
                        CachePresentationMode(key, DistancePresentationMode.Near);
                        ActivateVehicleForNearbyMinimap(pv, playerPos);
                    }
                    else if (distance <= MINIMAP_BLINK_ALPHA_175_RADIUS)
                    {
                        CachePresentationMode(key, DistancePresentationMode.BlinkAlpha175);
                        ApplyBlinkMinimapPresentation(pv, 175, blinkOn);
                    }
                    else if (distance <= MINIMAP_BLINK_ALPHA_100_RADIUS)
                    {
                        CachePresentationMode(key, DistancePresentationMode.BlinkAlpha100);
                        ApplyBlinkMinimapPresentation(pv, 100, blinkOn);
                    }
                    else
                    {
                        CachePresentationMode(key, DistancePresentationMode.Far);
                        DeactivateVehicleForFarMinimap(pv);
                    }
                }
                catch
                {
                    // best-effort only
                }
            }
        }
        catch
        {
        }
    }

    private void UpdateBlinkingPresentations()
    {
        try
        {
            bool blinkOn = IsBlinkPhaseOn();

            foreach (var pv in EnumeratePersistentVehiclesSnapshot())
            {
                try
                {
                    if (pv == null)
                        continue;

                    int ownerHash = ReadInt(pv, "OwnerModelHash", 0);
                    int currentCharHash = GetCurrentCharacterHash(Game.Player.Character);
                    if (ownerHash != currentCharHash)
                        continue;

                    if (HasAnyPreviewActive(pv))
                    {
                        KeepPreviewFriendlyVisibility(pv);
                        continue;
                    }

                    string key = BuildVehicleKey(pv);
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    if (!_presentationModes.TryGetValue(key, out DistancePresentationMode mode))
                        continue;

                    if (mode == DistancePresentationMode.Near)
                    {
                        ApplyNearMinimapPresentation(pv);
                    }
                    else if (mode == DistancePresentationMode.BlinkAlpha175)
                    {
                        ApplyBlinkMinimapPresentation(pv, 175, blinkOn);
                    }
                    else if (mode == DistancePresentationMode.BlinkAlpha100)
                    {
                        ApplyBlinkMinimapPresentation(pv, 100, blinkOn);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static bool IsBlinkPhaseOn()
    {
        try
        {
            return ((Game.GameTime / BLINK_PERIOD_MS) % 2) == 0;
        }
        catch
        {
            return true;
        }
    }

    private void CachePresentationMode(string key, DistancePresentationMode mode)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            if (mode == DistancePresentationMode.Far)
                _presentationModes.Remove(key);
            else
                _presentationModes[key] = mode;
        }
        catch
        {
        }
    }

    private void ApplyNearMinimapPresentation(object pv)
    {
        try
        {
            EnsureBlipExists(pv);
            ApplyPresentationFromPersistentState(pv, forceMapAndMinimap: true);

            Blip blip = ReadBlip(pv, "MapBlip", null);
            if (blip == null || !SafeExists(blip))
                return;

            SetBlipAlphaSafe(blip, 255);
        }
        catch
        {
        }
    }

    private void ApplyBlinkMinimapPresentation(object pv, int baseAlpha, bool blinkOn)
    {
        try
        {
            EnsureBlipExists(pv);
            ApplyPresentationFromPersistentState(pv, forceMapAndMinimap: true);

            Blip blip = ReadBlip(pv, "MapBlip", null);
            if (blip == null || !SafeExists(blip))
                return;

            SetBlipAlphaSafe(blip, blinkOn ? baseAlpha : 0);
        }
        catch
        {
        }
    }

    private static void SetBlipAlphaSafe(Blip blip, int alpha)
    {
        try
        {
            if (blip == null || !blip.Exists())
                return;

            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;

            Function.Call(Hash.SET_BLIP_ALPHA, blip.Handle, alpha);
        }
        catch
        {
        }
    }

    private void ActivateVehicleForNearbyMinimap(object pv, Vector3 playerPos)
    {
        try
        {
            string key = BuildVehicleKey(pv);
            if (string.IsNullOrWhiteSpace(key))
                return;

            // Keep the persistent vehicle spawn-enabled while the player is near.
            SetBoolField(pv, "AutoSpawnEnabled", true);

            Vehicle runtime = ReadVehicle(pv, "RuntimeVehicle", null);
            bool runtimeExists = runtime != null && SafeExists(runtime);

            if (!runtimeExists)
            {
                TryInvokeSpawnPersistentVehicle(pv);
                runtime = ReadVehicle(pv, "RuntimeVehicle", null);
                runtimeExists = runtime != null && SafeExists(runtime);
            }

            EnsureBlipExists(pv);
            ApplyPresentationFromPersistentState(pv, forceMapAndMinimap: true);

            Blip blip = ReadBlip(pv, "MapBlip", null);
            if (blip != null && SafeExists(blip))
            {
                SetBlipAlphaSafe(blip, 255);
            }

            // If the vehicle exists, keep it synced to the live position.
            if (runtimeExists)
            {
                try
                {
                    WriteVector3Field(pv, "Position", runtime.Position);
                    WriteFloatField(pv, "Heading", runtime.Heading);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private void DeactivateVehicleForFarMinimap(object pv)
    {
        try
        {
            // Ngoài phạm vi 250m: không hiện minimap, chỉ giữ bản đồ lớn để định vị.
            Vehicle runtime = ReadVehicle(pv, "RuntimeVehicle", null);
            if (runtime != null && SafeExists(runtime))
            {
                if (!IsPlayerUsingVehicle(runtime))
                {
                    try
                    {
                        runtime.Delete();
                    }
                    catch
                    {
                    }
                }
            }

            // Giữ blip trên bản đồ lớn, nhưng không cho hiện trên minimap.
            // Nếu runtime đã bị game xóa thì blip vẫn chỉ là marker định vị.
            EnsureBlipExists(pv);
            ApplyPresentationFromPersistentState(pv, forceMapAndMinimap: false);

            Blip blip = ReadBlip(pv, "MapBlip", null);
            if (blip != null && SafeExists(blip))
            {
                SetBlipAlphaSafe(blip, 255);
            }

            WriteField(pv, "RuntimeVehicle", null);
        }
        catch
        {
        }
    }

    private void KeepPreviewFriendlyVisibility(object pv)
    {
        try
        {
            // Do not delete or respawn anything while Symbolix preview is active.
            // Only make sure the blip stays visible on the full map and not lost.
            EnsureBlipExists(pv);
            ApplyPresentationFromPersistentState(pv, forceMapAndMinimap: true);
        }
        catch
        {
        }
    }

    private void ApplyPresentationFromPersistentState(object pv, bool forceMapAndMinimap)
    {
        try
        {
            Blip blip = ReadBlip(pv, "MapBlip", null);
            if (blip == null || !SafeExists(blip))
            {
                EnsureBlipExists(pv);
                blip = ReadBlip(pv, "MapBlip", null);
            }

            if (blip == null || !SafeExists(blip))
                return;

            string key = BuildVehicleKey(pv);
            uint modelHash = (uint)ReadInt(pv, "ModelHash", 0);

            int iconId = -1;
            float scale = 1.0f;
            int colorId = -1;

            try
            {
                iconId = PersistentManager.GetVehicleEffectiveIconIdByKey(key);
            }
            catch
            {
                iconId = -1;
            }

            try
            {
                scale = PersistentManager.GetVehicleEffectiveScaleByKey(key);
            }
            catch
            {
                scale = 1.0f;
            }

            try
            {
                colorId = PersistentManager.GetVehicleEffectiveColorByKey(key);
            }
            catch
            {
                colorId = -1;
            }

            try
            {
                blip.IsShortRange = false;
            }
            catch
            {
            }

            try
            {
                if (iconId >= MIN_ICON_ID && iconId <= MAX_ICON_ID)
                    blip.Sprite = (BlipSprite)iconId;
                else
                    blip.Sprite = GetDefaultVehicleSprite(modelHash);
            }
            catch
            {
            }

            try
            {
                if (!float.IsNaN(scale) && !float.IsInfinity(scale))
                    blip.Scale = ClampScale(scale);
                else
                    blip.Scale = 1.0f;
            }
            catch
            {
            }

            try
            {
                if (colorId >= MIN_COLOR_ID && colorId <= MAX_COLOR_ID)
                    blip.Color = (BlipColor)colorId;
                else
                    blip.Color = GetDefaultVehicleColorForOwner(ReadInt(pv, "OwnerModelHash", 0), ReadBool(pv, "IsCollateralLocked", false));
            }
            catch
            {
            }

            try
            {
                Vector3 pos = ReadVector3(pv, "Position", Vector3.Zero);
                Vehicle runtime = ReadVehicle(pv, "RuntimeVehicle", null);

                if (runtime != null && SafeExists(runtime))
                    blip.Position = runtime.Position;
                else if (pos != Vector3.Zero)
                    blip.Position = pos;
            }
            catch
            {
            }

            try
            {
                if (forceMapAndMinimap)
                    SetBlipDisplaySafe(blip, BLIP_DISPLAY_MAP_AND_MINIMAP);
                else
                    SetBlipDisplaySafe(blip, BLIP_DISPLAY_MAP_ONLY);
            }
            catch
            {
            }

            try
            {
                string name = GetVehicleDisplayName(modelHash);
                if (ReadBool(pv, "IsCollateralLocked", false))
                    name = "Phương tiện bị niêm phong";

                blip.Name = string.IsNullOrWhiteSpace(name) ? "Vehicle" : name;
            }
            catch
            {
            }
        }
        catch
        {
        }
    }

    private static void SetBlipDisplaySafe(Blip blip, int display)
    {
        try
        {
            if (blip == null || !blip.Exists())
                return;

            Function.Call(Hash.SET_BLIP_DISPLAY, blip.Handle, display);
        }
        catch
        {
        }
    }

    private void EnsureBlipExists(object pv)
    {
        try
        {
            Blip blip = ReadBlip(pv, "MapBlip", null);
            if (blip != null && SafeExists(blip))
                return;

            Vehicle runtime = ReadVehicle(pv, "RuntimeVehicle", null);
            Vector3 pos = ReadVector3(pv, "Position", Vector3.Zero);

            Blip created = null;

            try
            {
                if (runtime != null && SafeExists(runtime))
                    created = runtime.AddBlip();
            }
            catch
            {
            }

            if (created == null && pos != Vector3.Zero)
            {
                try
                {
                    created = World.CreateBlip(pos);
                }
                catch
                {
                    created = null;
                }
            }

            if (created == null)
                return;

            try
            {
                created.IsShortRange = false;
            }
            catch
            {
            }

            WriteField(pv, "MapBlip", created);
        }
        catch
        {
        }
    }

    private void TryInvokeSpawnPersistentVehicle(object pv)
    {
        try
        {
            Type pmType = typeof(PersistentManager);
            MethodInfo spawnMethod = pmType.GetMethod("SpawnPersistentVehicle", AnyInstance, null, new[] { typeof(PersistentManager.PersistentVehicle) }, null);
            if (spawnMethod == null)
            {
                // fallback: search by name only, in case compiler binding differs
                spawnMethod = pmType.GetMethods(AnyInstance)
                    .FirstOrDefault(m => string.Equals(
                        m.Name, "SpawnPersistentVehicle", StringComparison.OrdinalIgnoreCase)
                    && m.GetParameters().Length == 1);
            }

            if (spawnMethod == null)
                return;

            object typedPv = pv;
            if (typedPv == null)
                return;

            spawnMethod.Invoke(this.GetPersistentManagerInstanceForMethod(spawnMethod), new[] { typedPv });
        }
        catch
        {
        }
    }

    private object GetPersistentManagerInstanceForMethod(MethodInfo spawnMethod)
    {
        try
        {
            if (spawnMethod == null)
                return null;

            if (spawnMethod.IsStatic)
                return null;

            // The script instance itself is not PersistentManager; we need a live instance.
            // In ScriptHookVDotNet the script class instances are separate, so we try to locate
            // any loaded PersistentManager script instance via reflection. If not found, we skip.
            foreach (var scriptObj in FindAllLoadedScriptInstances())
            {
                if (scriptObj != null && scriptObj.GetType().Name == "PersistentManager")
                    return scriptObj;
            }
        }
        catch
        {
        }

        return null;
    }

    private IEnumerable<object> FindAllLoadedScriptInstances()
    {
        try
        {
            var scriptsField = typeof(Script).Assembly
                .GetTypes()
                .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(f => typeof(Script).IsAssignableFrom(f.FieldType))
                    .Select(f => f.GetValue(null)))
                .Where(x => x != null);

            return scriptsField.ToList();
        }
        catch
        {
            return Enumerable.Empty<object>();
        }
    }

    private static bool IsPlayerUsingVehicle(Vehicle veh)
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || veh == null || !veh.Exists())
                return false;

            return player.IsInVehicle(veh);
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeExists(Entity e)
    {
        try
        {
            return e != null && e.Exists();
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeExists(Blip b)
    {
        try
        {
            return b != null && b.Exists();
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeExists(Vehicle v)
    {
        try
        {
            return v != null && v.Exists();
        }
        catch
        {
            return false;
        }
    }

    private static int GetCurrentCharacterHash(Ped player)
    {
        try
        {
            if (player == null || !player.Exists())
                return 0;

            int hash = player.Model.Hash;
            if (hash == FRANKLIN_HASH || hash == MICHAEL_HASH || hash == TREVOR_HASH)
                return hash;
        }
        catch
        {
        }

        return 0;
    }

    private static bool IsSupportedCharacter(int hash)
    {
        return hash == FRANKLIN_HASH || hash == MICHAEL_HASH || hash == TREVOR_HASH;
    }

    private static BlipSprite GetDefaultVehicleSprite(uint modelHash)
    {
        try
        {
            int vehicleClass = Function.Call<int>(Hash.GET_VEHICLE_CLASS_FROM_NAME, modelHash);

            switch (vehicleClass)
            {
                case 8:
                case 13:
                    return BlipSprite.PersonalVehicleBike;
                case 14:
                    return BlipSprite.Boat;
                case 15:
                    return BlipSprite.Helicopter;
                case 16:
                    return BlipSprite.Plane;
            }

            try
            {
                Model m = new Model((int)modelHash);
                if (m.IsBlimp) return BlipSprite.Blimp;
                if (m.IsSubmarine) return BlipSprite.Sub;
                if (m.IsMotorcycle) return BlipSprite.PersonalVehicleBike;
            }
            catch
            {
            }

            return BlipSprite.PersonalVehicleCar;
        }
        catch
        {
            return BlipSprite.PersonalVehicleCar;
        }
    }

    private static BlipColor GetDefaultVehicleColorForOwner(int ownerHash, bool collateralLocked)
    {
        try
        {
            if (collateralLocked)
                return BlipColor.Red;

            if (ownerHash == FRANKLIN_HASH) return BlipColor.Green;
            if (ownerHash == MICHAEL_HASH) return BlipColor.Blue;
            if (ownerHash == TREVOR_HASH) return BlipColor.Orange;
        }
        catch
        {
        }

        return BlipColor.White;
    }

    private static float ClampScale(float scale)
    {
        if (float.IsNaN(scale) || float.IsInfinity(scale))
            return 1.0f;

        if (scale < 0.50f) scale = 0.50f;
        if (scale > 2.00f) scale = 2.00f;

        return (float)Math.Round(scale, 2, MidpointRounding.AwayFromZero);
    }

    private static bool HasAnyPreviewActive(object pv)
    {
        try
        {
            if (pv == null)
                return false;

            int? previewIcon = ReadNullableInt(pv, "PreviewIconId");
            int? previewColor = ReadNullableInt(pv, "PreviewBlipColorId");
            float? previewScale = ReadNullableFloat(pv, "PreviewBlipScale");

            return previewIcon.HasValue || previewColor.HasValue || previewScale.HasValue;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildVehicleKey(object pv)
    {
        try
        {
            if (pv == null)
                return string.Empty;

            int ownerHash = ReadInt(pv, "OwnerModelHash", 0);
            uint modelHash = (uint)ReadInt(pv, "ModelHash", 0);
            string plate = (ReadString(pv, "Plate", string.Empty) ?? string.Empty).Trim();
            Vector3 pos = ReadVector3(pv, "Position", Vector3.Zero);

            string normalizedPlate = NormalizePlate(plate);
            if (!string.IsNullOrWhiteSpace(normalizedPlate))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|{1:X}|PLATE|{2}",
                    ownerHash,
                    modelHash,
                    normalizedPlate);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1:X}|POS|{2:0.0}|{3:0.0}|{4:0.0}",
                ownerHash,
                modelHash,
                pos.X,
                pos.Y,
                pos.Z);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizePlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
            return string.Empty;

        return new string(plate.Trim().Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
    }

    private static string GetVehicleDisplayName(uint modelHash)
    {
        try
        {
            Type pmType = typeof(PersistentManager);
            MethodInfo mi = pmType.GetMethod("GetVehicleDisplayNamePublic", AnyStatic, null, new[] { typeof(uint) }, null);
            if (mi != null)
            {
                object result = mi.Invoke(null, new object[] { modelHash });
                if (result is string s && !string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }
        catch
        {
        }

        return $"0x{modelHash:X}";
    }

    private static IEnumerable<object> EnumeratePersistentVehiclesSnapshot()
    {
        try
        {
            Type pmType = typeof(PersistentManager);
            FieldInfo field = pmType.GetField("_persistVehicles", AnyStatic);
            if (field == null)
                return Enumerable.Empty<object>();

            object listObj = field.GetValue(null);
            if (listObj is System.Collections.IEnumerable enumerable)
            {
                var snapshot = new List<object>();
                foreach (var item in enumerable)
                    snapshot.Add(item);
                return snapshot;
            }
        }
        catch
        {
        }

        return Enumerable.Empty<object>();
    }

    private static int ReadInt(object obj, string fieldName, int fallback = 0)
    {
        try
        {
            if (obj == null)
                return fallback;

            FieldInfo f = obj.GetType().GetField(fieldName, AnyInstance);
            if (f == null)
                return fallback;

            object value = f.GetValue(obj);
            if (value == null)
                return fallback;

            if (value is int i)
                return i;

            if (value is uint u)
                return unchecked((int)u);

            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ReadBool(object obj, string fieldName, bool fallback = false)
    {
        try
        {
            if (obj == null)
                return fallback;

            FieldInfo f = obj.GetType().GetField(fieldName, AnyInstance);
            if (f == null)
                return fallback;

            object value = f.GetValue(obj);
            if (value == null)
                return fallback;

            if (value is bool b)
                return b;

            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static string ReadString(object obj, string fieldName, string fallback = "")
    {
        try
        {
            if (obj == null)
                return fallback;

            FieldInfo f = obj.GetType().GetField(fieldName, AnyInstance);
            if (f == null)
                return fallback;

            object value = f.GetValue(obj);
            if (value == null)
                return fallback;

            return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static Vector3 ReadVector3(object obj, string fieldName, Vector3 fallback)
    {
        try
        {
            if (obj == null)
                return fallback;

            FieldInfo f = obj.GetType().GetField(fieldName, AnyInstance);
            if (f == null)
                return fallback;

            object value = f.GetValue(obj);
            if (value is Vector3 v)
                return v;

            return fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static Vehicle ReadVehicle(object obj, string fieldName, Vehicle fallback)
    {
        try
        {
            if (obj == null)
                return fallback;

            FieldInfo f = obj.GetType().GetField(fieldName, AnyInstance);
            if (f == null)
                return fallback;

            object value = f.GetValue(obj);
            if (value is Vehicle v)
                return v;
        }
        catch
        {
        }

        return fallback;
    }

    private static Blip ReadBlip(object obj, string fieldName, Blip fallback)
    {
        try
        {
            if (obj == null)
                return fallback;

            FieldInfo f = obj.GetType().GetField(fieldName, AnyInstance);
            if (f == null)
                return fallback;

            object value = f.GetValue(obj);
            if (value is Blip b)
                return b;
        }
        catch
        {
        }

        return fallback;
    }

    private static int? ReadNullableInt(object obj, string fieldName)
    {
        try
        {
            if (obj == null)
                return null;

            FieldInfo f = obj.GetType().GetField(fieldName, AnyInstance);
            if (f == null)
                return null;

            object value = f.GetValue(obj);
            if (value == null)
                return null;

            if (value is int i)
                return i;

            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static float? ReadNullableFloat(object obj, string fieldName)
    {
        try
        {
            if (obj == null)
                return null;

            FieldInfo f = obj.GetType().GetField(fieldName, AnyInstance);
            if (f == null)
                return null;

            object value = f.GetValue(obj);
            if (value == null)
                return null;

            if (value is float fval)
                return fval;

            return Convert.ToSingle(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteField(object obj, string fieldName, object value)
    {
        try
        {
            if (obj == null)
                return;

            FieldInfo f = obj.GetType().GetField(fieldName, AnyInstance);
            if (f == null)
                return;

            f.SetValue(obj, value);
        }
        catch
        {
        }
    }

    private static void WriteVector3Field(object obj, string fieldName, Vector3 value)
    {
        WriteField(obj, fieldName, value);
    }

    private static void WriteFloatField(object obj, string fieldName, float value)
    {
        WriteField(obj, fieldName, value);
    }

    private static void SetBoolField(object obj, string fieldName, bool value)
    {
        WriteField(obj, fieldName, value);
    }
}