using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Xml.Linq;
using LemonUI;
using LemonUI.Menus;

public partial class InstantRefill : Script
{
    private static string LT(string key, string fallback, params string[] tokensAndValues)
        => Language.ReplaceTokens(Language.Get(key, fallback), tokensAndValues);

    // Thêm gần nhóm field LemonUI của class
    private string DiscountTicketHelpText
    => L("VehicleDetail_TicketHelp",
        "Nhấn Enter để áp dụng thẻ giảm giá cho loại phương tiện này, bạn có thể từ chối nếu bạn muốn?");

    private void UpdateDiscountTicketMenuItem(NativeCheckboxItem item)
    {
        if (item == null) return;

        item.Title = LT("VehicleDetail_TicketToggle", "Xác nhận dùng thẻ giảm giá");
        item.Description = DiscountTicketHelpText;
        item.Checked = _luiUseDiscountTicket;
    }

    private bool _luiDetailForceFixedSalePrice = false;

    // Thời lượng riêng cho menu chọn phương tiện (99 giây)
    private const int VehicleSelectionDurationMs = 99000;
    private int _pendingExpiryGameTime = 0;

    // Vehicle pending
    private uint _pendingVehicleHash = 0;
    private int _pendingVehiclePrice = 0;
    private VehicleEntry _pendingVehicleEntry = null;

    private int _vehSeIn = 0;   // _vehicleSelectionIndex
    private int _lastVehicleSelectionChangeTime = -99999;
    private const int VehicleSelectionDebounceMs = 100;

    // ADD INSIDE CLASS InstantRefill, near other UI fields
    private readonly ObjectPool _luiPool = new ObjectPool();

    private NativeMenu _luiVehicleListMenu;
    private NativeMenu _luiVehicleDetailMenu;

    // 1) THÊM FIELD MỚI (đặt gần nhóm LemonUI fields)
    private Vehicle _luiPreviewVehicle = null;
    private uint _luiPreviewVehicleHash = 0;

    // 2) Thêm vào vùng fields UI
    private NativeStatsPanel _luiVehicleStatsPanel;
    private NativeStatsInfo _luiVehicleStatTopSpeed;
    private NativeStatsInfo _luiVehicleStatAcceleration;
    private NativeStatsInfo _luiVehicleStatBraking;
    private NativeStatsInfo _luiVehicleStatTraction;

    private VehicleBrowseMode _luiBrowseMode = VehicleBrowseMode.None;
    private bool _luiMenuAutoCloseEnabled = false;
    private int _luiMenuAutoCloseExpiry = 0;

    private bool _luiUseDiscountTicket = false;
    private int _luiBaseVehiclePrice = 0;

    private VehicleEntry _luiDetailVehicle = null;
    private int _luiDetailPrice = 0;
    private string _luiDetailPlate = "";
    private NativeMenu _luiReturnMenu = null;

    private bool _luiPlateEdited = false;

    // <<< ADDED: back-cancel counters and locks >>>
    private int _statsBackCancelCount = 0;
    private int _weaponBackCancelCount = 0;
    private int _vehicleBackCancelCount = 0;

    private bool _statsLocked = false;
    private bool _weaponLocked = false;
    private bool _vehicleLocked = false;

    private readonly List<VehicleEntry> _vehicles = new List<VehicleEntry>();
    // view list used when filtering; if null -> use full _vehicles
    private List<VehicleEntry> _vehicleViewList = null;
    private readonly Dictionary<uint, VehicleEntry> _vehicleMap = new Dictionary<uint, VehicleEntry>();

    // -------------------- NEW: vehicle pool management --------------------
    internal class VehicleEntry
    {
        public string Name;
        public uint Hash;
        public string Label;
        public string Class;
        public int PriceMin;
        public int PriceMax;
        public int TimesPurchased;

        public VehicleEntry()
        {
            TimesPurchased = 0;
            PriceMin = 1000;
            PriceMax = 2000;
        }

        public int GetRandomPrice(Random rng, bool onSale, int salePrice)
        {
            if (onSale) return salePrice;
            if (PriceMax < PriceMin) return PriceMin;
            return rng.Next(PriceMin, PriceMax + 1);
        }
    }

    // <<< PATCH: Add helpers for resolving vehicle tokens from .ini and parsing AddVehicle lines >>>
    private bool TryResolveVehicleHash(string tokenRaw, out uint resolvedHash)
    => Helper.TryResolveVehicleHash(tokenRaw, out resolvedHash);

    // <<< PATCH: Load custom AddVehicle(...) entries from the ini file >>>
    private static int ReadIntAttr(XElement node, string attrName, int fallback)
    => Helper.ReadIntAttr(node, attrName, fallback);

    private static string ReadStringAttr(XElement node, string attrName, string fallback = "")
    => Helper.ReadStringAttr(node, attrName, fallback);

    private void EnsureCustomVehicleXmlExists()
    {
        try
        {
            if (File.Exists(customVehicleXmlPath))
                return;

            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement("DirectOrderVehicles",
                    new XComment("Mỗi node Vehicle/AddVehicle là 1 xe ngoài danh sách"),
                    new XElement("Vehicle",
                        new XAttribute("token", "adder"),
                        new XAttribute("name", "Adder"),
                        new XAttribute("label", "ADDER"),
                        new XAttribute("class", "Siêu Xe"),
                        new XAttribute("priceMin", "1930500"),
                        new XAttribute("priceMax", "2359500")
                    )
                )
            );

            doc.Save(customVehicleXmlPath);
        }
        catch { }
    }

    private void LoadCustomVehicleEntriesFromXml()
    {
        try
        {
            EnsureCustomVehicleXmlExists();
            if (!File.Exists(customVehicleXmlPath))
                return;

            var doc = XDocument.Load(customVehicleXmlPath);
            if (doc.Root == null)
                return;

            int added = 0;

            foreach (var node in doc.Root.Elements())
            {
                string nodeName = node.Name.LocalName;
                if (!nodeName.Equals("Vehicle", StringComparison.OrdinalIgnoreCase) &&
                    !nodeName.Equals("AddVehicle", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string tokenArg = ReadStringAttr(node, "token");
                string displayName = ReadStringAttr(node, "name");
                string modelLabel = ReadStringAttr(node, "label");
                string cls = ReadStringAttr(node, "class");

                int priceMin = ReadIntAttr(node, "priceMin", 800);
                int priceMax = ReadIntAttr(node, "priceMax", Math.Max(priceMin, priceMin + 1));

                if (string.IsNullOrWhiteSpace(tokenArg) ||
                    string.IsNullOrWhiteSpace(displayName) ||
                    string.IsNullOrWhiteSpace(modelLabel) ||
                    string.IsNullOrWhiteSpace(cls))
                {
                    continue;
                }

                if (priceMin < 800) priceMin = 800;
                if (priceMax < priceMin) priceMax = priceMin;

                uint hash;
                bool ok = TryResolveVehicleHash(tokenArg, out hash);
                if (!ok)
                {
                    Notification.Show(LT(
                        "Vehicle_Custom_NotFound",
                        "~h~Không tìm thấy phương tiện '{token}' (hash: {hash}). Bỏ qua!",
                        "{token}", tokenArg ?? string.Empty,
                        "{hash}", $"0x{hash:X8}"
                    ));
                    continue;
                }

                if (_vehicleMap.ContainsKey(hash))
                    continue;

                AddVehicle(hash, displayName, modelLabel, cls, priceMin, priceMax);
                added++;
            }

            if (added > 0)
                Notification.Show(LT(
                    "Vehicle_Custom_AddedCount",
                    "~h~Đã thêm mới thành công {count} phương tiện.~h~",
                    "{count}", added.ToString()
                ));
        }
        catch
        {
            try { GTA.UI.Screen.ShowSubtitle("~HUD_COLOUR_DEGEN_RED~Lỗi khi đọc file XML phương tiện.", 3000); } catch { }
        }
    }

    // -------------------------- Initialize vehicles (NEW) --------------------------
    private void InitializeVehicleList()
    {
        _vehicles.Clear();
        _vehicleMap.Clear();

        // ------ DANH SÁCH XE (1-276) ------

        // 1. Akula
        AddVehicle(0x46699F47u, "Akula", "AKULA", "Combat Helicopter", 7507500, 9652500);

        // 2. Apocalypse ZR380
        AddVehicle(0x20314B42u, "Apocalypse ZR380", "ZR380", "Arena Combat Racer", 4075500, 4933500);

        // 3. Avenger
        AddVehicle(0x81BD2ED0u, "Avenger", "AVENGER", "V-22 Flying Fortress", 7078500, 9652500);

        // 140. Adder
        AddVehicle(0xB779A091u, "Adder", "ADDER", "Super", 1930500, 2359500);

        // 141. Autarch
        AddVehicle(0xED552C74u, "Autarch", "AUTARCH", "Super", 3753750, 4719000);

        // 4. Avisa
        AddVehicle(0x9A474B5Eu, "Avisa", "AVISA", "Mini Submarine", 2145000, 4290000);

        // 113. Apocalypse Deathbike
        AddVehicle(0xFE5F0722u, "Apocalypse Deathbike", "DEATHBIKE", "Arena Combat Racer", 2359500, 3003000);

        // 189. Annihilator
        AddVehicle(0x31F0B376u, "Annihilator", "ANNIHL", "Combat Helicopter", 3861000, 4290000);

        // 114. Atomic Blimp
        AddVehicle(0xF7004C86u, "Atomic Blimp", "BLIMP", "Blimp", 2145000, 2895750);

        // 217. Apocalypse Sasquatch
        AddVehicle(0x669EB40Au, "Apocalypse Sasquatch", "MONSTER3", "Off-Road", 3003000, 3432000);

        // 74. APC
        AddVehicle(0x2189D250u, "APC", "APC", "Amphibious Armored", 6006000, 7507500);

        // 5. B-11 Strikeforce
        AddVehicle(0x64DE07A1u, "B-11 Strikeforce", "STRIKEFORCE", "Bomber Aircraft", 7507500, 9652500);

        // 6. Banshee GTS
        AddVehicle(0xD8A914D3u, "Banshee GTS", "BANSHEE3", "Sports", 2466750, 3003000);

        // 190. Bestia GTS
        AddVehicle(0x4BFCF28Bu, "Bestia GTS", "BESTIAGTS", "Sports", 1222650, 1394250);

        // 7. Blazer Aqua
        AddVehicle(0xA1355F67u, "Blazer Aqua", "BLAZER5", "Amphibious ATV", 3646500, 4290000);

        // 142. Banshee 900R
        AddVehicle(0x25C5AF13u, "Banshee 900R", "BANSHEE2", "Super", 1072500, 1287000);

        // 143. Blazer Lifeguard
        AddVehicle(0xFD231729u, "Blazer Lifeguard", "BLAZER2", "Off-Road", 117975, 139425);

        // 218. BR8
        AddVehicle(0x58F77553u, "BR8", "OPENWHEEL1", "Racing", 7078500, 7507500);

        // 8. BMX
        AddVehicle(0x43779C54u, "BMX", "BMX", "Bicycle", 1502, 1931);

        // 115. Bati 801
        AddVehicle(0xF9300CC5u, "Bati 801", "BATI", "Motorcycle", 21450, 42900);

        // 116. Bati 801RR
        AddVehicle(0xCADD5D2Du, "Bati 801RR", "BAT12", "Motorbike", 25740, 47190);

        // 117. BF400
        AddVehicle(0x5283265u, "BF400", "BF400", "Motorcycle", 193050, 214500);

        // 118. Brioso 300
        AddVehicle(0x55365079u, "Brioso 300", "BRIOSO2", "City", 1244100, 1394250);

        // 9. Buffalo EVX
        AddVehicle(0x09E478B3u, "Buffalo EVX", "BUFFALO5", "Electric", 3861000, 5362500);

        // 10. Cheetah
        AddVehicle(0xB1D95DA0u, "Cheetah", "CHEETAH", "Super", 1287000, 2145000);

        // 106. Champion
        AddVehicle(0xC972A155u, "Champion", "CHAMPION", "Super", 7185750, 8580000);

        // 144. Cyclone
        AddVehicle(0x52FF9437u, "Cyclone", "CYCLONE", "Super", 3646500, 4290000);

        // 120. Cargobob
        AddVehicle(0x53174EEFu, "Cargobob", "CARGOBOB", "Military Aircraft", 3217500, 4826250);

        // 191. Carbon RS
        AddVehicle(0xABB0C0u, "Carbon RS", "CARBON", "Motorcycle", 75075, 96525);

        // 192. Comet
        AddVehicle(0xC1AE4D16u, "Comet", "COMET2", "Sports", 171600, 257400);

        // 193. Comet S2
        AddVehicle(0x991EFC04u, "Comet S2", "COMET6", "Sports", 3432000, 4719000);

        // 194. Conada
        AddVehicle(0xE384DD25u, "Conada", "Conada", "Helicopter", 4719000, 5577000);

        // 219. Carbonizzare
        AddVehicle(0x7B8AB45Fu, "Carbonizzare", "CARBONIZ", "Sports", 386100, 429000);

        // 220. Chernobog
        AddVehicle(0xD6BC7523u, "Chernobog", "CHERNOBOG", "Military", 2788500, 3432000);

        // 221. Comet Retro Custom
        AddVehicle(0x877358ADu, "Comet Retro Custom", "COMET3", "Sports", 1351350, 1415700);

        // 222. Comet S2 Cabrio
        AddVehicle(0x440851D8u, "Comet S2 Cabrio", "COMET7", "Sports", 3432000, 4075500);

        // 223. Comet SR
        AddVehicle(0x276D98A3u, "Comet SR", "COMET5", "Sports", 2145000, 2574000);

        // 224. Coquette D10 Pursuit
        AddVehicle(0x79C12D73u, "Coquette D10 Pursuit", "POLCOQUTT4", "Police", 11797500, 12870000);

        // 225. Cruiser
        AddVehicle(0x1ABA13B5u, "Cruiser", "CRUISER", "Bicycle", 1287, 2145);

        // 195. Coquette
        AddVehicle(0x67BC037u, "Coquette", "COQUETTE", "Sports", 214500, 321750);

        // 196. Coquette D10
        AddVehicle(0x98F65A5Eu, "Coquette D10", "COQUETTE4", "Sports", 3003000, 3646500);

        // 197. Coquette D5
        AddVehicle(0x796B7A5u, "Coquette D5", "coquette6", "Sports", 4290, 6435);

        // 11. Deity
        AddVehicle(0x5B531351u, "Deity", "DEITY", "Sedan", 3217500, 4290000);

        // 12. Deluxo
        AddVehicle(0x586765FBu, "Deluxo", "DELUXO", "Flying", 11368500, 13084500);

        // 198. Diabolus
        AddVehicle(0xF1B44F44u, "Diabolus", "DIABLOUS", "Motorcycle", 332475, 386100);

        // 122. Dinghy
        AddVehicle(0x107F392Cu, "Dinghy", "DINGHY", "Boat", 321750, 429000);

        // 145. Desert Raid
        AddVehicle(0xD876DBE2u, "Desert Raid", "TROPHY2", "Off-Road", 1394250, 1608750);

        // 147. Double-T
        AddVehicle(0x9C669788u, "Double-T", "DOUBLE", "Motorcycle", 21450, 27885);

        // 95. Dubsta
        AddVehicle(0xE882E5F6u, "Dubsta", "DUBSTA", "SUV", 117975, 171600);

        // 13. Deveste Eight
        AddVehicle(0x5EE005DAu, "Deveste Eight", "DEVESTE", "Super", 3217500, 4290000);

        // 226. DR1
        AddVehicle(0x4669D038u, "DR1", "OPENWHEEL2", "F1 Racing", 6220500, 6435000);

        // 227. Dune Buggy
        AddVehicle(0x9CF21E0Fu, "Dune Buggy", "DUNE", "Off-Road", 38610, 47190);

        // 97. Dominator GT
        AddVehicle(0xE5B3ACA1u, "Dominator GT", "DOMINATOR9", "Muscle", 4182750, 5362500);

        // 14. Emerus
        AddVehicle(0x4EE74355u, "Emerus", "EMERUS", "Super", 5362500, 6435000);

        // 148. Entity XXR
        AddVehicle(0x8198AEDCu, "Entity XXR", "ENTITY2", "Super", 4611750, 5148000);

        // 15. Entity MT
        AddVehicle(0x6838FC1Du, "Entity MT", "ENTITY3", "Super", 4719000, 6006000);

        // 228. Elegy RH8
        AddVehicle(0xDE3D9D22u, "Elegy RH8", "ELEGY2", "Sports", 193050, 214500);

        // 229. Endurex Race Bike
        AddVehicle(0xB67597ECu, "Endurex Race Bike", "TRIBIKE2", "Racing Bicycle", 19305, 23595);

        // 230. Entity XF
        AddVehicle(0xB2FE5CF9u, "Entity XF", "ENTITYXF", "Super", 1673100, 1716000);

        // 16. Envisage
        AddVehicle(0x42D623C7u, "Envisage", "ENVISAGE", "Sports", 4290000, 6435000);

        // 17. F-160 Raiju
        AddVehicle(0xE4C8C4Du, "F-160 Raiju", "RAIJU", "Jet Aircraft", 13942500, 17160000);

        // 237. Future Shock Slamvan
        AddVehicle(0x163F8520u, "Future Shock Slamvan", "SLAMVAN5", "Muscle", 2788500, 3003000);

        // 103. FCR 1000
        AddVehicle(0x25676EAFu, "FCR 1000", "FCR", "Motorbike", 214500, 321750);

        // 123. FIB (Buffalo)
        AddVehicle(0x9DC66994u, "FIB", "FBI2", "Agent", 3217500, 4290000);

        // 238. Future Shock ZR380
        AddVehicle(0xBE11EFC6u, "Future Shock ZR380", "ZR3802", "Sports", 4290000, 4719000);

        // 150. FMJ
        AddVehicle(0x5502626Cu, "FMJ", "FMJ", "Super", 3432000, 4075500);

        // 151. FMJ MK V
        AddVehicle(0xC3F57329u, "FMJ MK V", "fmj2", "Super", 6006000, 6649500);

        // 231. Flash GT
        AddVehicle(0xB4F32118u, "Flash GT", "FLASHGT", "Sports", 3217500, 3646500);

        // 234. Future Shock Dominator
        AddVehicle(0xAE0A3D4Fu, "Future Shock Dominator", "DOMINATOR5", "Muscle", 2359500, 2681250);

        // 232. Furore GT
        AddVehicle(0xBF1691E0u, "Furore GT", "FURORE", "Sports", 922350, 986700);

        // 233. Furia
        AddVehicle(0x3944D5A0u, "Furia", "FURIA", "Super", 5791500, 6006000);

        // 235. Future Shock Impaler
        AddVehicle(0x8D45DF49u, "Future Shock Impaler", "IMPALER3", "Muscle", 2466750, 2788500);

        // 124. FIB (Granger)
        AddVehicle(0x432EA949u, "FIB", "FBI", "Agent", 3217500, 4290000);

        // 139. 811
        AddVehicle(0x92EF6E04u, "811", "PFISTER811", "Super", 2145000, 2788500);

        // 236. Future Shock Sasquatch
        AddVehicle(0x32174AFCu, "Future Shock Sasquatch", "MONSTER4", "Off-Road", 3217500, 3432000);

        // 125. Fixter
        AddVehicle(0xCE23D3BFu, "Fixter", "FIXTER", "Bicycle", 1716, 2145);

        // 149. Faggio Sport
        AddVehicle(0x9229E4EBu, "Faggio Sport", "FAGGION", "Motorcycle", 96525, 107250);

        // 18. Future Shock Deathbike
        AddVehicle(0x93F09558u, "Future Shock Deathbike", "DEATHBIKE2", "Arena War", 2145000, 4290000);

        // 199. Feltzer
        AddVehicle(0x8911B9F5u, "Feltzer", "FELTZER", "Sports", 278850, 321750);

        // 200. FH-1 Hunter
        AddVehicle(0xFD707EDEu, "FH-1 Hunter", "HUNTER", "Military Helicopter", 8580000, 9652500);

        // 92. Fieldmaster
        AddVehicle(0x843B73DEu, "Fieldmaster", "TRACTOR2", "Tractor", 4290, 6435);

        // 19. Future Shock Scarab
        AddVehicle(0x5BEB3CE0u, "Future Shock Scarab", "SCARAB2", "Armored", 6435000, 8580000);

        // 20. Gauntlet
        AddVehicle(0x817AFAADu, "Gauntlet", "GAUNTLET5", "Sports", 1287000, 1716000);

        // 94. Go Go Monkey Blista
        AddVehicle(0xDCBC1C3Bu, "Go Go Monkey Blista", "BLISTA3", "Sports", 4290, 6435);

        // 239. Gauntlet Hellfire
        AddVehicle(0x734C5E50u, "Gauntlet Hellfire", "GAUNTLET4", "Muscle", 1501500, 1716000);

        // 240. GP1
        AddVehicle(0x4992196Cu, "GP1", "GP1", "Super", 2509650, 2788500);

        // 241. Growler
        AddVehicle(0x4DC079D7u, "Growler", "GROWLER", "Sports", 3217500, 3646500);

        // 126. Gargoyle
        AddVehicle(0x2C2C2324u, "Gargoyle", "GARGOYLE", "Motorcycle", 235950, 278850);

        // 21. Hakuchou Drag
        AddVehicle(0xF0C2A91Fu, "Hakuchou Drag", "HAKUCHOU2", "High-End Motorcycle", 1716000, 2145000);

        // 22. Hydra
        AddVehicle(0x39D6E83Fu, "Hydra", "HYDRA", "Jet Aircraft", 7507500, 10725000);

        // 152. Hot Rod Blazer
        AddVehicle(0xB44F0582u, "Hot Rod Blazer", "BLAZER03", "ATV", 139425, 171600);

        // 153. Howard NX-25
        AddVehicle(0xC3F25753u, "Howard NX-25", "HOWARD", "Aircraft", 2359500, 3003000);

        // 242. Hermes
        AddVehicle(0x00E83C17u, "Hermes", "HERMES", "Muscle", 1072500, 1287000);

        // 75. Hakuchou
        AddVehicle(0x4B6C568Au, "Hakuchou", "HAKUCHOU", "Motorcycle", 150150, 193050);

        // 76. Half-track
        AddVehicle(0xFE141DA6u, "Half-track", "HALFTRACK", "Armored", 4290000, 5362500);

        // 23. Ignus
        AddVehicle(0xA9EC907Bu, "Ignus", "IGNUS", "Super", 5362500, 6435000);

        // 24. Inductor
        AddVehicle(0xCA7C4AE9u, "Inductor", "INDUCTOR", "Electric Bicycle", 85800, 128700);

        // 25. Itali GTO Stinger TT
        AddVehicle(0x5649FF41u, "Itali GTOStinger", "stingertt", "Sports", 4719000, 6006000);

        // 26. Itali RSX
        AddVehicle(0xBB78956Au, "Itali RSX", "ITALIRSX", "Sports", 6435000, 8580000);

        // 154. Itali GTB
        AddVehicle(0x85E8E76Bu, "Itali GTB", "ITALIGTB", "Super", 2145000, 2788500);

        // 155. Itali GTB Custom
        AddVehicle(0xE33A477Bu, "Itali GTB2", "ITALIGTB2", "Super", 858000, 1287000);

        // 107. Jubilee
        AddVehicle(0x1B8165D3u, "Jubilee", "JUBILEE", "SUV", 3110250, 3646500);

        // 243. Infernus
        AddVehicle(0x18F25AC7u, "Infernus", "INFERNUS", "Super", 858000, 1072500);

        // 244. Jester (Racecar)
        AddVehicle(0xBE0E6126u, "Jester (Racecar)", "JESTER2", "Sports", 697125, 804375);

        // 245. Jester RR
        AddVehicle(0xA1B3A871u, "Jester RR", "JESTER4", "Sports", 3861000, 4290000);

        // 247. Jet
        AddVehicle(0x3F119114u, "Jet", "JET", "Aircraft", 2145000, 3217500);

        // 248. Junk Energy Inductor
        AddVehicle(0x89C45478u, "Junk Energy Inductor", "INDUCTOR2", "Bicycle", 96525, 117975);

        // 201. Jester
        AddVehicle(0xB2A716A3u, "Jester", "JESTER", "Sports", 471900, 600600);

        // 202. Jester RR Widebody
        AddVehicle(0x5882160Fu, "Jester R2W", "JESTER5", "Sports", 4290000, 5362500);

        // 27. Kosatka
        AddVehicle(0x4FAF0D70u, "Kosatka", "KOSATKA", "Nuclear Submarine (Sea)", 4290000, 6435000);

        // 28. Krieger
        AddVehicle(0xD86A0247u, "Krieger", "krieger", "Super", 5362500, 6435000);

        // 249. Komoda
        AddVehicle(0xCE44C4B9u, "Komoda", "KOMODA", "Sports", 3539250, 3861000);

        // 250. Kuruma (Armored)
        AddVehicle(0x187D938Du, "Kuruma (Armored)", "KURUMA2", "Sports", 1394250, 1501500);

        // 203. Khamelion
        AddVehicle(0x206D1B68u, "Khamelion", "KHAMEL", "Sports (Electric)", 171600, 257400);

        // 29. Kurtz 31 Patrol
        AddVehicle(0xEF813606u, "Kurtz 31 Patrol", "PATROLBOAT", "Patrol Warship", 5362500, 8151000);

        // 127. Kraken
        AddVehicle(0xC07107EEu, "Kraken", "SUBMERS2", "Mini Submarine", 2574000, 3217500);

        // 30. LM87
        AddVehicle(0xFF5968CDu, "LM87", "LM87", "Super", 6006000, 7507500);

        // 128. Lifeguard
        AddVehicle(0x1BF8D381u, "Lifeguard", "LGUARD", "Rescue (Sea)", 1716000, 2145000);

        // 262. LE-7B
        AddVehicle(0xB6846A55u, "LE-7B", "LE7B", "Super", 5148000, 5362500);

        // 156. Lectro
        AddVehicle(0x26321E67u, "Lectro", "LECTRO", "Motorcycle", 2037750, 2574000);

        // 251. Lynx
        AddVehicle(0x1CBDC10Bu, "Lynx", "LYNX", "Sports", 3646500, 3861000);

        // 157. Liberator
        AddVehicle(0xCD93A7DBu, "Liberator", "MONSTER", "Off-Road", 1501500, 1930500);

        // 158. Luiva
        AddVehicle(0xC8163646u, "Luiva", "LUIVA", "Super", 7936500, 8794500);

        // 110. LF-22 Starling
        AddVehicle(0x9A9EB7DEu, "LF-22 Starling", "STARLING", "Aircraft", 7078500, 8151000);

        // 31. Locust
        AddVehicle(0xC7E55211u, "Locust", "LOCUST", "Sports", 2145000, 4290000);

        // 89. Luxor Deluxe
        AddVehicle(0xB79F589Eu, "Luxor Deluxe", "LUXOR2", "Aircraft", 18232500, 22522500);

        // 32. Longfin
        AddVehicle(0x6EF89CCCu, "Longfin", "LONGFIN", "Boat", 4182750, 4933500);

        // 33. Marquis
        AddVehicle(0xC1CE1183u, "Marquis", "MARQUIS", "Boat", 750750, 1072500);

        // 252. Mammatus
        AddVehicle(0x97E55D11u, "Mammatus", "MAMMATUS", "Aircraft", 536250, 750750);

        // 34. Mini Tank
        AddVehicle(0xB53C6C52u, "Mini Tank", "MINITANK", "Remote-Control Tank", 4290000, 6435000);

        // 204. Massacro (Racecar)
        AddVehicle(0xDA5819A3u, "Massacro", "MASSACRO2", "Sports", 793650, 858000);

        // 35. Mogul
        AddVehicle(0xD35698EFu, "Mogul", "MOGUL", "Military Aircraft", 5898750, 7507500);

        // 77. Menacer
        AddVehicle(0x79DD18AEu, "Menacer", "MENACER", "Armored", 3217500, 4290000);

        // 36. Niobe
        AddVehicle(0x70241EEAu, "Niobe", "NIOBE", "Sports", 3217500, 4290000);

        // 254. Nightmare Dominator
        AddVehicle(0xB2E046FBu, "Nightmare Dominator", "DOMINATOR6", "Muscle", 2359500, 2574000);

        // 205. Neo
        AddVehicle(0x9F6ED5A2u, "Neo", "NEO", "Sports", 3646500, 4290000);

        // 206. Neon
        AddVehicle(0x91CA96EEu, "Neon", "NEON", "Sports", 3003000, 3432000);

        // 159. Nemesis
        AddVehicle(0xDA288376u, "Nemesis", "NEMESIS", "Motorbike", 21450, 27885);

        // 160. Nightmare Deathbike
        AddVehicle(0xAE12C99Cu, "Nightmare Deathbike", "DEATHBIKE3", "Arena War", 2466750, 3003000);

        // 161. Nero
        AddVehicle(0x3DA47243u, "Nero", "NERO", "Super", 3003000, 3432000);

        // 188. 9F Cabrio
        AddVehicle(0xA8E38B01u, "9F Cabrio", "NINEF2", "Convertible", 214500, 321750);

        // 162. Nero Custom
        AddVehicle(0x4131F378u, "Nero Custom", "NERO2", "Super", 1287000, 1394250);

        // 129. Nightblade
        AddVehicle(0xA0438767u, "Nightblade", "NIGHTBLADE", "Motorcycle", 203775, 235950);

        // 37. Oppressor
        AddVehicle(0x34B82784u, "Oppressor", "OPPRESSOR", "Jet Motorcycle", 6435000, 8580000);

        // 38. Oppressor Mk 2
        AddVehicle(0x7B54A9D3u, "Oppressor MK2", "OPPRESSOR2", "Jet Flying", 13942500, 17160000);

        // 78. Outlaw
        AddVehicle(0x185E2FF3u, "Outlaw", "OUTLAW", "Off-Road", 2145000, 3003000);

        // 207. Osiris
        AddVehicle(0x767164D6u, "Osiris", "OSIRIS", "Super", 3861000, 4504500);

        // 39. P-996 LAZER
        AddVehicle(0xB39B0AE6u, "P-996 LAZER", "LAZER", "Jet Aircraft", 11797500, 15015000);

        // 255. Paragon R
        AddVehicle(0xE550775Bu, "Paragon R", "PARAGON", "Sports", 1930500, 2145000);

        // 256. Paragon R (Armored)
        AddVehicle(0x546D8EEEu, "Paragon R (Armored)", "PARAGON2", "Sports", 32175, 53625);

        // 257. Phoenix
        AddVehicle(0x831A21D5u, "Phoenix", "PHOENIX", "Muscle", 64350, 75075);

        // 258. PR4
        AddVehicle(0x1446590Au, "PR4", "FORMULA", "Racing", 7507500, 7722000);

        // 163. Penetrator
        AddVehicle(0x9734F3EAu, "Penetrator", "penetrator", "Super", 1716000, 2037750);

        // 164. Police Predator
        AddVehicle(0xE2E7D4ABu, "Police Predator", "PREDATOR", "Police Boat", 7507500, 8580000);

        // 40. Pariah
        AddVehicle(0x33B98FE2u, "Pariah", "PARIAH", "Sports", 2574000, 3432000);

        // 98. P-45 Nokota
        AddVehicle(0x3DC92356u, "P-45 Nokota", "NOKOTA", "Bomber Aircraft", 5148000, 6435000);

        // 41. Phantom Wedge
        AddVehicle(0x9DAE1398u, "Phantom Wedge", "PHANTOM2", "Truck", 4290000, 6435000);

        // 108. Patriot Mil-Spec
        AddVehicle(0xD80F4A44u, "Patriot Mil-Spec", "PATRIOT3", "Military", 3217500, 3861000);

        // 42. Pipistrello
        AddVehicle(0xF2AE3F81u, "Pipistrello", "PIPISTRELLO", "Super", 6006000, 7507500);

        // 105. Pizza Boy
        AddVehicle(0x75599EA7u, "Pizza Boy", "PIZZABOY", "Delivery", 386100, 461175);

        // 101. Police Bike
        AddVehicle(0xFDEFAEC3u, "Police Bike", "POLICEB", "Police Motorcycle", 429000, 643500);

        // 130. Panto
        AddVehicle(0xE644E480u, "Panto", "PANTO", "City", 150150, 193050);

        // 131. Park Ranger
        AddVehicle(0x2C33B46Eu, "Park Ranger", "PRANGER", "Ranger", 5577000, 7078500);

        // 132. Police Rancher
        AddVehicle(0xA46462F7u, "Police Rancher", "POLICEO1", "Patrol", 75075, 107250);

        // 79. Powersurge
        AddVehicle(0xAD5E30D7u, "Powersurge", "POWERSURGE", "Motorcycle", 3003000, 3861000);

        // 112. Police Cruiser
        AddVehicle(0x71FA16EAu, "Police Cruiser", "POLICE", "Police", 9438000, 10725000);

        // 43. Pyro
        AddVehicle(0xAD6065C0u, "Pyro", "PYRO", "Aircraft", 8580000, 10725000);

        // 44. R88
        AddVehicle(0x8B213907u, "R88", "FORMULA2", "Racing", 6006000, 7507500);

        // 259. Raiden
        AddVehicle(0xA4D99B7Du, "Raiden", "RAIDEN", "Sports", 2788500, 3003000);

        // 260. Rapid GT
        AddVehicle(0x679450AFu, "Rapid GT", "RAPIDGT", "Sports", 257400, 321750);

        // 261. Rapid GT X
        AddVehicle(0x68FB5379u, "Rapid GT X", "RAPIDGT4", "Sports", 5791500, 5898750);

        // 263. Ruiner ZZ-8
        AddVehicle(0x65BDEBFCu, "Ruiner ZZ-8", "RUINER4", "Muscle", 2788500, 2895750);

        // 264. Ruston
        AddVehicle(0x2AE524A8u, "Ruston", "RUSTON", "Sports", 858000, 1072500);

        // 45. RC Bandito
        AddVehicle(0xEEF345ECu, "RC Bandito", "RCBANDITO", "Remote-Control", 2145000, 4290000);

        // 208. Raptor
        AddVehicle(0xD7C56D39u, "Raptor", "RAPTOR", "Sports", 1287000, 1501500);

        // 209. Rhino Tank
        AddVehicle(0x2EA68690u, "Rhino Tank", "RHINO", "Tank", 3003000, 3753750);

        // 133. Rebla GTS
        AddVehicle(0x4F48FC4u, "Rebla GTS", "REBLA", "SUV", 2145000, 2788500);

        // 165. Ramp Buggy (DUNE4)
        AddVehicle(0xCEB28249u, "Ramp Buggy", "DUNE4", "Off-Road", 6542250, 7078500);

        // 166. Ramp Buggy (DUNE5)
        AddVehicle(0xED62BFA9u, "Ramp Buggy", "DUNE5", "Off-Road", 4290, 6435);

        // 167. Ratel
        AddVehicle(0xE00BADABu, "Ratel", "RATEL", "Off-Road", 3861000, 4290000);

        // 168. Reaper
        AddVehicle(0x0DF381E5u, "Reaper", "REAPER", "Super", 3110250, 3646500);

        // 169. Rocket Voltic
        AddVehicle(0x3AF76F4Au, "Rocket Voltic", "VOLTIC2", "Super", 7936500, 8580000);

        // 170. Rogue
        AddVehicle(0xC5DD6967u, "Rogue", "ROGUE", "Aircraft", 2788500, 3646500);

        // 171. Ruffian
        AddVehicle(0xCABD11E8u, "Ruffian", "RUFFIAN", "Motorcycle", 17160, 25740);

        // 46. Reever
        AddVehicle(0x76D7C404u, "Reever", "REEVER", "Motorcycle", 3217500, 4290000);

        // 47. RM-10 Bombushka
        AddVehicle(0xFE0A508Cu, "RM-10 Bombushka", "BOMBUSHKA", "Bomber Aircraft", 11797500, 13942500);

        // 48. RO-86 Alkonost
        AddVehicle(0xEA313705u, "RO-86 Alkonost", "ALKONOST", "Bomber Aircraft", 8580000, 10725000);

        // 49. Ruiner 2000
        AddVehicle(0x381E10BDu, "Ruiner 2000", "RUINER2", "Weaponized", 10725000, 12870000);

        // 50. Sanctus
        AddVehicle(0x58E316C7u, "Sanctus", "SANCTUS", "Halloween", 3861000, 4504500);

        // 210. Sea Sparrow
        AddVehicle(0xD4AE63D9u, "SeaSparrow", "SPARROW", "Amphibious Helicopter", 3432000, 4075500);

        // 265. SC1
        AddVehicle(0x5097F589u, "SC1", "SC1", "Super", 3324750, 3539250);

        // 266. Schwartzer
        AddVehicle(0xD37B7976u, "Schwartzer", "SCHWARZE", "Sports", 160875, 182325);

        // 267. Seashark
        AddVehicle(0xDB4388E4u, "Seashark", "SEASHARK", "Watercraft", 32175, 38610);

        // 211. Seven-70
        AddVehicle(0x97398A4Bu, "Seven-70", "SEVEN70", "Sports", 1394250, 1608750);

        // 212. SM722
        AddVehicle(0x2E3967B0u, "SM722", "SM722", "Sports", 4290000, 4826250);

        // 213. Specter Custom
        AddVehicle(0x400F5147u, "Specter", "SPECTER2", "Sports", 1716000, 1930500);

        // 214. SuperVolito
        AddVehicle(0x2A54C47Du, "SuperVolito", "SVOLITO", "Helicopter", 3539250, 4182750);

        // 215. SuperVolito Carbon
        AddVehicle(0x9C5E5644u, "SuperVolito Carbon", "SVOLITO2", "Helicopter", 4290000, 4826250);

        // 172. Sanchez (SANCHEZ01)
        AddVehicle(0x2EF89E46u, "Sanchez", "SANCHEZ01", "Motorcycle", 15015, 19305);

        // 173. Sanchez (SANCHEZ02)
        AddVehicle(0xA960B13Eu, "Sanchez", "SANCHEZ02", "Motorcycle", 12870, 17160);

        // 174. Street Blazer
        AddVehicle(0xE5BA6858u, "Street Blazer", "BLAZER4", "ATV", 160875, 193050);

        // 175. Suntrap
        AddVehicle(0xEF2295C9u, "Suntrap", "SUNTRAP", "Boat", 49335, 55770);

        // 176. Suzume
        AddVehicle(0x28FC5B78u, "Suzume", "SUZUME", "Super", 4290000, 5362500);

        // 51. Scramjet
        AddVehicle(0xD9F0503Du, "Scramjet", "SCRAMJET", "Jet Super", 7507500, 10725000);

        // 99. Swift Deluxe
        AddVehicle(0x4019CB4Cu, "Swift Deluxe", "SWIFT2", "Helicopter", 10296000, 11368500);

        // 109. Seabreeze
        AddVehicle(0xE8983F9Fu, "Seabreeze", "SEABREEZE", "Seaplane", 2145000, 3217500);

        // 100. Stanier LE Cruiser
        AddVehicle(0x9C32EB57u, "Stanier LE Cruiser", "POLICE5", "Patrol", 9009000, 10296000);

        // 134. Sandking SWB
        AddVehicle(0x3AF8C345u, "Sandking SWB", "SANDKIN2", "Off-Road", 68640, 85800);

        // 135. Skylift
        AddVehicle(0x3E48BF23u, "Skylift", "SKYLIF", "Helicopter", 85800, 107250);

        // 136. Sovereign
        AddVehicle(0x2C509634u, "Sovereign", "SOVEREIGN", "Police Motorcycle", 235950, 268125);

        // 104. Scorcher
        AddVehicle(0xF4E1AA15u, "Scorcher", "SCORCHER", "Bicycle", 3218, 5363);

        // 53. Shinobi
        AddVehicle(0x50A6FB9Cu, "Shinobi", "SHINOBI", "Motorcycle", 4933500, 6006000);

        // 54. Shotaro
        AddVehicle(0xE7D2A16Eu, "Shotaro", "SHOTARO", "Motorcycle", 5362500, 7507500);

        // 55. Stromberg
        AddVehicle(0x34DBA661u, "Stromberg", "STROMBERG", "Amphibious Super", 5898750, 7507500);

        // 90. Slamvan Custom
        AddVehicle(0x42BC5E19u, "Slamvan Custom", "SLAMVAN3", "Custom", 750750, 965250);

        // 56. Sultan RS
        AddVehicle(0xEE6024BCu, "Sultan RS", "SULTANRS", "Super", 1501500, 2145000);

        // 91. Space Docker
        AddVehicle(0x1FD824AFu, "Space Docker", "DUNE2", "Space", 42900, 107250);

        // 57. Swift
        AddVehicle(0xEBC24DF2u, "Swift", "SWIFT", "Helicopter", 2681250, 3861000);

        // 80. Speedo
        AddVehicle(0x0D17099Du, "Speedo", "SPEEDO4", "Van", 1730000, 2145000);

        // 81. Stryder
        AddVehicle(0x11F58A5Au, "Stryder", "STRYDER", "3-Wheel Motorcycle", 1287000, 1608750);

        // 58. T20
        AddVehicle(0x6322B39Au, "T20", "T20", "Super", 3861000, 5362500);

        // 59. Thruster
        AddVehicle(0x58CDAF30u, "Thruster", "THRUSTER", "Jetpack Device", 6435000, 8580000);

        // 60. Toreador
        AddVehicle(0x56C8A5EFu, "Toreador", "TOREADOR", "Amphibious Super", 8580000, 9652500);

        // 177. Tempesta
        AddVehicle(0x1044926Fu, "Tempesta", "TEMPESTA", "Super", 2145000, 3217500);

        // 268. Toro
        AddVehicle(0x362CAC6Du, "Toro", "TORO", "Watercraft", 3646500, 3861000);

        // 269. Torero XO
        AddVehicle(0xF62446BAu, "Torero XO", "TORERO2", "Super", 6006000, 6435000);

        // 270. Tri-Cycles Race Bike
        AddVehicle(0xE823FB48u, "Tri-Cycles Race Bike", "TRIBIKE3", "Bicycle", 17160, 25740);

        // 178. Thrax
        AddVehicle(0x3E3D1F59u, "Thrax", "THRAX", "Super", 4719000, 5148000);

        // 216. Titan
        AddVehicle(0x761E2AD3u, "Titan", "TITAN", "Aircraft", 4075500, 4504500);

        // 179. Tigon
        AddVehicle(0xAF0B8D48u, "Tigon", "TIGON", "Super", 4826250, 5255250);

        // 180. Titan 250 D
        AddVehicle(0x3329757Eu, "Titan 250 D", "TITAN2", "Aircraft", 10081500, 11154000);

        // 181. Turismo Omaggio
        AddVehicle(0xF8AB457Bu, "Turismo Omaggio", "TURISMO3", "Super", 5791500, 6435000);

        // 182. Tyrus
        AddVehicle(0x7B406EFBu, "Tyrus", "TYRUS", "Super", 5148000, 5577000);

        // 61. Tula
        AddVehicle(0x3E2E4F8Au, "Tula", "TULA", "Seaplane", 10725000, 13942500);

        // 62. Turismo R
        AddVehicle(0x185484E1u, "Turismo R", "TURISMOR", "Super", 1072500, 1716000);

        // 63. Tyrant
        AddVehicle(0xE99011C2u, "Tyrant", "TYRANT", "Super", 4290000, 6435000);

        // 187. 10F Widebody
        AddVehicle(0x10635A0Eu, "10F Widebody", "TENF2", "Sports", 4504500, 5148000);

        // 93. Tornado Rat Rod
        AddVehicle(0xA31CB573u, "Tornado RR", "TORNADO6", "Sports", 750750, 965250);

        // 82. Taipan
        AddVehicle(0xBC5DC07Eu, "Taipan", "TAIPAN", "Super", 3753750, 4719000);

        // 83. Technical Aqua
        AddVehicle(0x4662BCBBu, "Technical Aqua", "TECHNICAL2", "Amphibious Pickup", 2681250, 3432000);

        // 84. Tezeract
        AddVehicle(0x3D7C6410u, "Tezeract", "TEZERACT", "Super (Electric)", 5577000, 7078500);

        // 85. Thrust
        AddVehicle(0x6D6F8F43u, "Thrust", "THRUST", "Motorcycle", 139425, 171600);

        // 86. TM-02 Khanjali
        AddVehicle(0xAA6F980Au, "TM-02 Khanjali", "KHANJALI", "Tank", 7507500, 9652500);

        // 65. Ultralight
        AddVehicle(0x96E24857u, "Ultralight", "microlight", "Light Aircraft", 1072500, 1501500);

        // 137. Unmarked Cruiser
        AddVehicle(0x8A63C7B9u, "Unmarked Cruiser", "POLICE4", "Undercover Police", 7936500, 9009000);

        // 66. Vagner
        AddVehicle(0x7397224Cu, "Vagner", "VAGNER", "Super", 2145000, 4290000);

        // 67. Vigilante
        AddVehicle(0xB5EF4C33u, "Vigilante", "VIGILANTE", "Super", 7507500, 8580000);

        // 183. Vader
        AddVehicle(0xF79A00F7u, "Vader", "VADER", "Motorcycle", 17160, 21450);

        // 184. Verus
        AddVehicle(0x11CBC051u, "Verus", "VERUS", "Off-Road", 407550, 450450);

        // 185. Vindicator
        AddVehicle(0xAF599F01u, "Vindicator", "VINDICATOR", "Motorcycle", 1287000, 1501500);

        // 271. Veto Classic
        AddVehicle(0xCCE5C8FAu, "Veto Classic", "VETO", "Sports", 1823250, 2037750);

        // 272. Veto Modern
        AddVehicle(0xA703E4A9u, "Veto Modern", "VETO2", "Sports", 2037750, 2359500);

        // 273. Vigero ZX
        AddVehicle(0x973141FCu, "Vigero ZX", "VIGERO2", "Muscle", 4075500, 4290000);

        // 274. Vigero ZX Convertible
        AddVehicle(0x1635C007u, "Vigero ZX Convertible", "VIGERO3", "Muscle", 4719000, 5148000);

        // 68. Visione
        AddVehicle(0xC4810400u, "Visione", "VISIONE", "Super", 4290000, 5362500);

        // 217. Vacca
        AddVehicle(0x142E0DC3u, "Vacca", "VACCA", "Super", 429000, 643500);

        // 218. Valkyrie MOD.0
        AddVehicle(0x5BFA5C4Bu, "Valkyrie MOD.0", "VALKYRI2", "Helicopter", 6435000, 7507500);

        // 111. Volatol
        AddVehicle(0x1AAD0DEDu, "Volatol", "VOLATOL", "Bomber Aircraft", 7507500, 8580000);

        // 69. Vivanite
        AddVehicle(0xAE2CC02Au, "Vivanite", "VIVANITE", "SUV", 3003000, 4290000);

        // 70. Weaponized Dinghy
        AddVehicle(0xC58DA34Au, "Weaponized Dinghy", "DINGHY5", "Weaponized Boat", 3217500, 4290000);

        // 275. Whippet Race Bike
        AddVehicle(0x4339CD69u, "Whippet Race Bike", "TRIBIKE", "Bicycle", 17160, 23595);

        // 87. Weaponized Tampa
        AddVehicle(0xB7D9F7F1u, "Weaponized Tampa", "TAMPA3", "Weaponized", 4075500, 4933500);

        // 71. X80 Proto
        AddVehicle(0x7E8F677Fu, "X80 Proto", "PROTOTIPO", "Super", 5362500, 7507500);

        // 186. XA-21
        AddVehicle(0x36B4A8A9u, "XA-21", "XA21", "Super", 4504500, 5255250);

        // 187. X-treme
        AddVehicle(0x95F6A2C9u, "X-treme", "XTREME", "Super", 6435000, 7078500);

        // 276. Xero Blimp
        AddVehicle(0xDB6B4924u, "Xero Blimp", "BLIMP2", "Aircraft", 2359500, 2681250);

        // 72. Z-Type
        AddVehicle(0x2D3BD401u, "Z-Type", "ZTYPE", "Sports", 1287000, 2145000);

        // 73. Zentorno
        AddVehicle(0xAC5DF515u, "Zentorno", "ZENTORNO", "Super", 1287000, 2145000);

        // 138. Zombie Chopper
        AddVehicle(0xDE05FB87u, "Zombie Chopper", "ZOMBIEB", "Motorcycle", 257400, 268125);

        // 188. Zeno
        AddVehicle(0x2714AA93u, "Zeno", "ZENO", "Super", 5791500, 6435000);

        // 189. Zorrusso
        AddVehicle(0xD757D97Du, "Zorrusso", "ZORRUSSO", "Super", 3861000, 4504500);

        // 88. Zhaba
        AddVehicle(0x4C8DBA51u, "Zhaba", "ZHABA", "Off-Road", 4611750, 5791500);

        // at the end of InitializeVehicleList(), after building built-in list:
        LoadCustomVehicleEntriesFromXml();
    }

    private void AddVehicle(uint hash, string name, string label, string cls, int priceMin, int priceMax)
    {
        try
        {
            // enforce min price >= 800
            if (priceMin < 800) priceMin = 800;
            if (priceMax < priceMin) priceMax = priceMin;

            // ignore duplicates by hash to avoid overriding built-in entries
            if (_vehicleMap.ContainsKey(hash))
                return;

            var v = new VehicleEntry
            {
                Name = name,
                Hash = hash,
                Label = label,
                Class = cls,
                PriceMin = priceMin,
                PriceMax = priceMax,
                TimesPurchased = 0
            };
            _vehicles.Add(v);
            _vehicleMap[hash] = v;
        }
        catch
        {
            // swallow to avoid in-game crash on bad data
        }
    }

    private List<VehicleEntry> GetActiveVehicleList()
    {
        return _vehicleViewList ?? _vehicles;
    }

    // ------------------- NEW HELPER: HandleBackCancel -------------------
    // (sale logic đã được chuyển sang Sale.cs)

    // 1) thêm helper lấy heading ngẫu nhiên
    private float GetRandomPreviewHeading()
    => Helper.GetRandomPreviewHeading(_rng);

    // 2) THÊM HELPER PREVIEW
    private bool CanPreviewVehicle(VehicleEntry v)
    => Helper.CanPreviewVehicle(v, _luiPreviewSkipTokens);

    // 2) sửa TryGetVehiclePreviewSpawnPoint: đổi heading sang ngẫu nhiên
    private bool TryGetVehiclePreviewSpawnPoint(Ped player, out Vector3 spawnPos, out float heading)
    => Helper.TryGetVehiclePreviewSpawnPoint(player, _rng, out spawnPos, out heading);

    private void ClearVehiclePreview()
    {
        try
        {
            if (_luiPreviewVehicle != null && _luiPreviewVehicle.Exists())
                _luiPreviewVehicle.Delete();
        }
        catch { }

        _luiPreviewVehicle = null;
        _luiPreviewVehicleHash = 0;
    }

    private void SpawnVehiclePreview(VehicleEntry chosen)
    {
        try
        {
            if (chosen == null) { ClearVehiclePreview(); return; }
            if (!CanPreviewVehicle(chosen)) { ClearVehiclePreview(); return; }

            // cùng xe thì giữ nguyên, khỏi spawn lại
            if (_luiPreviewVehicle != null && _luiPreviewVehicle.Exists() && _luiPreviewVehicleHash == chosen.Hash)
                return;

            ClearVehiclePreview();

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return;

            Model model = new Model((int)chosen.Hash);
            if (!model.IsValid || !model.IsInCdImage)
                return;

            if (!model.IsLoaded)
            {
                model.Request(500);
                int waited = 0;
                while (!model.IsLoaded && waited < 2000)
                {
                    Script.Wait(50);
                    waited += 50;
                }
            }

            if (!model.IsLoaded)
                return;

            if (!TryGetVehiclePreviewSpawnPoint(player, out Vector3 spawnPos, out float heading))
                return;

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
                return;

            _luiPreviewVehicle = veh;
            _luiPreviewVehicleHash = chosen.Hash;

            // Khóa an toàn: không cho lái, không di chuyển, không bị phá
            try { Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, veh.Handle, true, true); } catch { }
            try { Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, veh.Handle); } catch { }
            try { Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, veh.Handle, true); } catch { }
            try { Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, veh.Handle, 2); } catch { }
            try { Function.Call(Hash.SET_VEHICLE_ENGINE_ON, veh.Handle, false, true, true); } catch { }
            try { Function.Call(Hash.SET_ENTITY_DYNAMIC, veh.Handle, false); } catch { }
            try { Function.Call(Hash.FREEZE_ENTITY_POSITION, veh.Handle, true); } catch { }
            try { Function.Call(Hash.SET_ENTITY_INVINCIBLE, veh.Handle, true); } catch { }
            try { Function.Call(Hash.SET_ENTITY_CAN_BE_DAMAGED, veh.Handle, false); } catch { }
            try { Function.Call(Hash.SET_VEHICLE_HAS_BEEN_OWNED_BY_PLAYER, veh.Handle, false); } catch { }
            try { veh.IsInvincible = true; } catch { }
            try { veh.IsPersistent = true; } catch { }
        }
        catch
        {
            ClearVehiclePreview();
        }
    }

    // ADD INSIDE CLASS
    private void InitializeLemonVehicleMenus()
    {
        try
        {
            string brandTitle = L("VehicleMenu_BrandTitle", "Legendary Motorsport");

            _luiVehicleListMenu = new NativeMenu(
                brandTitle,
                L("VehicleMenu_ListTitle", "Danh sách phương tiện"));

            _luiVehicleDetailMenu = new NativeMenu(
                brandTitle,
                L("VehicleMenu_DetailTitle", "Mua phương tiện"));

            _luiPool.Add(_luiVehicleListMenu);
            _luiPool.Add(_luiVehicleDetailMenu);

            ConfigureKeyboardOnlyVehicleMenu(_luiVehicleListMenu);
            ConfigureKeyboardOnlyVehicleMenu(_luiVehicleDetailMenu);

            _luiVehicleListMenu.Visible = false;
            _luiVehicleDetailMenu.Visible = false;

            _luiBrowseMode = VehicleBrowseMode.None;
            _luiMenuAutoCloseEnabled = false;
            _luiMenuAutoCloseExpiry = 0;
        }
        catch
        {
            // swallow to avoid crashing the game
        }
    }

    // 3) SỬA HÀM AddReadOnlyMenuLine
    private void AddReadOnlyMenuLine(NativeMenu menu, string text, NativePanel panel = null, Action onSelected = null)
    {
        if (menu == null) return;

        var item = new NativeItem(text);
        item.Enabled = true;
        item.Panel = panel;

        if (onSelected != null)
        {
            item.Selected += (s, e) =>
            {
                try { onSelected(); } catch { }
            };
        }

        item.Activated += (s, e) =>
        {
            // intentionally empty
        };

        menu.Add(item);
    }

    // --- PATCH: LemonUI keyboard-only vehicle menus ---
    private string SanitizePlateText(string raw)
    => Helper.SanitizePlateText(raw);

    private string PromptPlateText(string currentPlate)
    {
        try
        {
            // Hộp thoại nhập liệu chuẩn của game
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", currentPlate ?? "", "", "", "", 8);

            while (true)
            {
                int state = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);

                if (state == 0)
                {
                    Script.Wait(0);
                    continue;
                }

                if (state == 2) // cancel
                    return null;

                return Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT);
            }
        }
        catch
        {
            return null;
        }
    }

    private void ApplyPlateItemDescription(NativeItem item)
    {
        if (item == null) return;
        item.Description = _luiPlateEdited ? _plateHintAfterEdit : _plateHintBeforeEdit;
    }

    // 3) Thêm các helper này vào trong class InstantRefill (đặt gần nhóm LemonUI helpers)
    private void EnsureVehicleStatsPanel()
    {
        if (_luiVehicleStatsPanel != null)
            return;

        _luiVehicleStatTopSpeed = new NativeStatsInfo(L("VehicleStat_TopSpeed", "Tốc độ cao nhất"), 0);
        _luiVehicleStatAcceleration = new NativeStatsInfo(L("VehicleStat_Acceleration", "Gia tốc"), 0);
        _luiVehicleStatBraking = new NativeStatsInfo(L("VehicleStat_Braking", "Phanh"), 0);
        _luiVehicleStatTraction = new NativeStatsInfo(L("VehicleStat_Traction", "Độ bám đường"), 0);

        _luiVehicleStatsPanel = new NativeStatsPanel(
            _luiVehicleStatTopSpeed,
            _luiVehicleStatAcceleration,
            _luiVehicleStatBraking,
            _luiVehicleStatTraction
        )
        {
            BackgroundColor = Color.FromArgb(180, 32, 32, 32),
            ForegroundColor = Color.FromArgb(255, 255, 255, 255),
            Visible = true
        };
    }

    private static float ClampStat(float value)
    => Helper.ClampStat(value);

    // cap là ngưỡng hiển thị, chỉ phục vụ normalize cho bar 0..100
    private static float NormalizeStat(float raw, float cap)
    => Helper.NormalizeStat(raw, cap);

    private void ReadVehicleModelStats(uint modelHash, out float topSpeed, out float acceleration, out float braking, out float traction)
    {
        // Các native này là model-stat, không cần spawn xe ra ngoài.
        topSpeed = SafeCall(() => Function.Call<float>(Hash.GET_VEHICLE_MODEL_ESTIMATED_MAX_SPEED, modelHash), 0f);
        acceleration = SafeCall(() => Function.Call<float>(Hash.GET_VEHICLE_MODEL_ACCELERATION, modelHash), 0f);
        braking = SafeCall(() => Function.Call<float>(Hash.GET_VEHICLE_MODEL_MAX_BRAKING, modelHash), 0f);
        traction = SafeCall(() => Function.Call<float>(Hash.GET_VEHICLE_MODEL_MAX_TRACTION, modelHash), 0f);
    }

    private void UpdateVehicleStatsPanel(float topSpeed, float acceleration, float braking, float traction)
    {
        EnsureVehicleStatsPanel();

        // Chỉ giữ tên + thanh, không hiển thị số liệu.
        _luiVehicleStatTopSpeed.Name = L("VehicleStat_TopSpeed", "Tốc độ cao nhất");
        _luiVehicleStatAcceleration.Name = L("VehicleStat_Acceleration", "Gia tốc");
        _luiVehicleStatBraking.Name = L("VehicleStat_Braking", "Phanh");
        _luiVehicleStatTraction.Name = L("VehicleStat_Traction", "Độ bám đường");

        // Normalize chỉ để hiển thị bar. Các cap này là display-only.
        _luiVehicleStatTopSpeed.Value = NormalizeStat(topSpeed, 60f);
        _luiVehicleStatAcceleration.Value = NormalizeStat(acceleration, 1.20f);
        _luiVehicleStatBraking.Value = NormalizeStat(braking, 1.50f);
        _luiVehicleStatTraction.Value = NormalizeStat(traction, 3.50f);
    }

    private void ConfigureKeyboardOnlyVehicleMenu(NativeMenu menu)
    {
        if (menu == null) return;

        // Tắt hoàn toàn điều hướng bằng chuột trong menu
        menu.MouseBehavior = MenuMouseBehavior.Disabled;

        // Không reset / lôi con trỏ ra khi mở menu
        menu.ResetCursorWhenOpened = false;

        // Không đóng menu vì click lệch chuột
        menu.CloseOnInvalidClick = false;

        // Cho phép camera/mouse của game hoạt động khi menu mở
        menu.RotateCamera = true;
    }

    private string GenerateRandomPlate()
    => Helper.GenerateRandomPlate(_rng);

    // 4) SỬA CloseLemonVehicleMenus
    private void CloseLemonVehicleMenus(bool setCooldown)
    {
        try
        {
            ClearVehiclePreview();

            if (_luiVehicleListMenu != null) _luiVehicleListMenu.Visible = false;
            if (_luiVehicleDetailMenu != null) _luiVehicleDetailMenu.Visible = false;
        }
        catch { }

        _luiMenuAutoCloseEnabled = false;
        _luiMenuAutoCloseExpiry = 0;
        _luiBrowseMode = VehicleBrowseMode.None;
        _luiDetailVehicle = null;
        _luiDetailPrice = 0;
        _luiDetailPlate = "";
        _luiReturnMenu = null;
        _luiPlateEdited = false;   // <-- thêm dòng này

        if (setCooldown)
            EnsureHelpBoxCooldownSet();
    }

    private void BuildVehicleBrowseMenu(List<VehicleEntry> source, bool includeRandomItem)
    {
        if (_luiVehicleListMenu == null) return;

        _luiVehicleListMenu.Clear();

        if (source == null || source.Count == 0)
            return;

        int index = 1;
        foreach (var vehicle in source)
        {
            var captured = vehicle;

            var item = new NativeItem($"{index}. {captured.Name}");
            item.AltTitle = "~HUD_COLOUR_YELLOWLIGHT~>~s~";
            item.Description = LT("VehicleMenu_OpenDetailHint", "Chọn một chiếc phương tiện để xem chi tiết phương tiện đó.");

            item.Activated += (s, e) => OpenVehicleDetailMenu(captured, _luiVehicleListMenu);
            _luiVehicleListMenu.Add(item);

            index++;
        }

        if (includeRandomItem)
        {
            var randomItem = new NativeItem(L("VehicleMenu_RandomItem", "Chọn ngẫu nhiên trong tổng danh sách."));
            randomItem.AltTitle = "~c~>~s~";
            randomItem.Activated += (s, e) =>
            {
                if (_vehicles == null || _vehicles.Count == 0) return;
                var randomVehicle = _vehicles[_rng.Next(_vehicles.Count)];
                OpenVehicleDetailMenu(randomVehicle, _luiVehicleListMenu, true);
            };
            _luiVehicleListMenu.Add(randomItem);
        }
    }

    private bool OpenFilteredVehicleMenu(char filterChar)
    {
        if (_vehicles == null || _vehicles.Count == 0)
        {
            GTA.UI.Screen.ShowSubtitle(L("Vehicle_Empty", "~HUD_COLOUR_DEGEN_RED~Phương tiện trống."), 3000);
            return false;
        }

        char wanted = char.ToUpperInvariant(filterChar);
        var filtered = new List<VehicleEntry>();

        foreach (var v in _vehicles)
        {
            if (string.IsNullOrEmpty(v?.Name)) continue;
            if (char.ToUpperInvariant(v.Name[0]) == wanted)
                filtered.Add(v);
        }

        if (filtered.Count == 0)
        {
            GTA.UI.Screen.ShowSubtitle(L("Vehicle_NotFound", "~HUD_COLOUR_DEGEN_RED~Không tìm thấy phương tiện phù hợp."), 3000);
            return false;
        }

        CloseLemonVehicleMenus(false);

        BuildVehicleBrowseMenu(filtered, true);
        _luiBrowseMode = VehicleBrowseMode.Filtered;
        _luiMenuAutoCloseEnabled = false;
        _luiMenuAutoCloseExpiry = 0;

        ConfigureKeyboardOnlyVehicleMenu(_luiVehicleListMenu);
        ConfigureKeyboardOnlyVehicleMenu(_luiVehicleDetailMenu);

        _luiVehicleListMenu.Visible = true;
        _luiVehicleDetailMenu.Visible = false;
        Interval = 0;

        return true;
    }

    private bool OpenAllVehiclesMenu()
    {
        if (_vehicles == null || _vehicles.Count == 0)
        {
            GTA.UI.Screen.ShowSubtitle(L("Vehicle_Empty", "~HUD_COLOUR_DEGEN_RED~Phương tiện trống."), 3000);
            return false;
        }

        CloseLemonVehicleMenus(false);

        BuildVehicleBrowseMenu(_vehicles, false);
        _luiBrowseMode = VehicleBrowseMode.Full;
        _luiMenuAutoCloseEnabled = true;
        _luiMenuAutoCloseExpiry = Game.GameTime + 99000;

        ConfigureKeyboardOnlyVehicleMenu(_luiVehicleListMenu);
        ConfigureKeyboardOnlyVehicleMenu(_luiVehicleDetailMenu);

        _luiVehicleListMenu.Visible = true;
        _luiVehicleDetailMenu.Visible = false;
        Interval = 0;

        return true;
    }

    // 5) SỬA OpenVehicleDetailMenu
    private void OpenVehicleDetailMenu(VehicleEntry chosen, NativeMenu returnMenu, bool forceFixedSalePrice = false)
    {
        if (chosen == null) return;

        EnsureVehicleStatsPanel();
        ClearVehiclePreview();

        _luiDetailVehicle = chosen;
        _luiDetailForceFixedSalePrice = forceFixedSalePrice;

        _luiBaseVehiclePrice = chosen.GetRandomPrice(_rng, false, 0);
        _luiUseDiscountTicket = false;

        _luiDetailPrice = ComputeVehicleMenuPrice(chosen, _luiDetailForceFixedSalePrice, false);

        _luiDetailPlate = GenerateRandomPlate();
        _luiReturnMenu = returnMenu ?? _luiVehicleListMenu;
        _luiPlateEdited = false;

        ReadVehicleModelStats(
            chosen.Hash,
            out float topSpeed,
            out float acceleration,
            out float braking,
            out float traction
        );

        UpdateVehicleStatsPanel(topSpeed, acceleration, braking, traction);

        BuildVehicleDetailMenu(chosen, _luiReturnMenu, _luiDetailPrice, _luiDetailPlate);

        if (_luiVehicleListMenu != null)
            _luiVehicleListMenu.Visible = false;

        ConfigureKeyboardOnlyVehicleMenu(_luiVehicleListMenu);
        ConfigureKeyboardOnlyVehicleMenu(_luiVehicleDetailMenu);

        _luiVehicleDetailMenu.Visible = true;
        SpawnVehiclePreview(chosen);

        Interval = 0;
    }

    // 3) THÊM HÀM BUILD MENU CHI TIẾT
    private void BuildVehicleDetailMenu(VehicleEntry chosen, NativeMenu returnMenu, int price, string plate)
    {
        if (_luiVehicleDetailMenu == null || chosen == null)
            return;

        _luiVehicleDetailMenu.Clear();

        // item 1: tên xe + stats panel
        AddReadOnlyMenuLine(
            _luiVehicleDetailMenu,
            LT("VehicleDetail_Name", "Tên phương tiện: {name}", "{name}", chosen.Name ?? string.Empty),
            _luiVehicleStatsPanel,
            () => SpawnVehiclePreview(chosen)
        );

        // item 2: loại xe
        AddReadOnlyMenuLine(
            _luiVehicleDetailMenu,
            LT("VehicleDetail_Class", "Loại: {class}", "{class}", chosen.Class ?? string.Empty),
            null,
            () => SpawnVehiclePreview(chosen)
        );

        // item 3: giá
        AddReadOnlyMenuLine(
            _luiVehicleDetailMenu,
            LT("VehicleDetail_Price", "Giá mua: ${price}", "{price}", price.ToString("N0")),
            null,
            () => SpawnVehiclePreview(chosen)
        );

        // item 4: biển số xe (có chú thích + Enter để sửa)
        var plateItem = new NativeItem(LT("VehicleDetail_Plate", "Biển số xe: {plate}", "{plate}", plate ?? string.Empty));
        ApplyPlateItemDescription(plateItem);

        plateItem.Selected += (s, e) =>
        {
            SpawnVehiclePreview(chosen);
            ApplyPlateItemDescription(plateItem);
        };

        plateItem.Activated += (s, e) =>
        {
            string currentPlate = string.IsNullOrWhiteSpace(_luiDetailPlate)
                ? GenerateRandomPlate()
                : _luiDetailPlate;

            string typed = PromptPlateText(currentPlate);
            string normalized = SanitizePlateText(typed);

            if (string.IsNullOrWhiteSpace(normalized))
            {
                GTA.UI.Screen.ShowSubtitle(L("Vehicle_InvalidPlate", "~y~Biển số không hợp lệ."), 2000);
                return;
            }

            _luiPlateEdited = true;
            _luiDetailPlate = normalized;

            // rebuild ngay để title + chú thích cập nhật liền
            BuildVehicleDetailMenu(chosen, returnMenu, price, _luiDetailPlate);
            ConfigureKeyboardOnlyVehicleMenu(_luiVehicleDetailMenu);
            _luiVehicleDetailMenu.Visible = true;
            SpawnVehiclePreview(chosen);
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        };

        _luiVehicleDetailMenu.Add(plateItem);

        // item thẻ giảm giá
        LoadVehicleDiscountTicketState();

        var ticketItem = new NativeCheckboxItem(
            LT("VehicleDetail_TicketToggle", "Xác nhận dùng thẻ giảm giá"),
            false
        );

        ticketItem.Description = DiscountTicketHelpText;
        ticketItem.Checked = _luiUseDiscountTicket;

        ticketItem.Selected += (s, e) =>
        {
            SpawnVehiclePreview(chosen);
            ticketItem.Description = DiscountTicketHelpText;
        };

        // nếu bản LemonUI của bạn có event đổi trạng thái checkbox,
        // dùng event đó để cập nhật logic
        ticketItem.CheckboxChanged += (s, e) =>
        {
            LoadVehicleDiscountTicketState();

            if (_vehicleDiscountTicketCount <= 0)
            {
                _luiUseDiscountTicket = false;
                ticketItem.Checked = false;

                GTA.UI.Screen.ShowSubtitle(
                    L("Vehicle_NoDiscountTicket", "~HUD_COLOUR_DEGEN_RED~Bạn không còn thẻ giảm giá nào cả."),
                    2500);

                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            _luiUseDiscountTicket = ticketItem.Checked;
            _luiDetailPrice = ComputeVehicleMenuPrice(chosen, _luiDetailForceFixedSalePrice, _luiUseDiscountTicket);

            BuildVehicleDetailMenu(chosen, returnMenu, _luiDetailPrice, _luiDetailPlate);
            ConfigureKeyboardOnlyVehicleMenu(_luiVehicleDetailMenu);
            _luiVehicleDetailMenu.Visible = true;

            SpawnVehiclePreview(chosen);
            PlayFrontendSound("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
        };

        _luiVehicleDetailMenu.Add(ticketItem);

        // item 5: xác nhận mua
        var confirm = new NativeItem(L("VehicleDetail_ConfirmBuy", "Xác nhận mua phương tiện"));
        confirm.Selected += (s, e) =>
        {
            SpawnVehiclePreview(chosen);
        };
        confirm.Activated += (s, e) =>
        {
            PurchaseVehicleFromMenu(chosen, price, _luiDetailPlate);
        };
        _luiVehicleDetailMenu.Add(confirm);

        // item 6: quay lại
        var back = new NativeItem(L("VehicleDetail_Back", "Quay lại danh sách phương tiện"));
        back.Selected += (s, e) =>
        {
            // giữ preview như hiện tại
        };
        back.Activated += (s, e) =>
        {
            ClearVehiclePreview();

            _luiVehicleDetailMenu.Visible = false;
            if (returnMenu != null)
                returnMenu.Visible = true;

            Interval = 0;
        };
        _luiVehicleDetailMenu.Add(back);
    }

    // 6) SỬA PurchaseVehicleFromMenu
    private void PurchaseVehicleFromMenu(VehicleEntry chosen, int totalCost, string plateText)
    {
        try
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
            {
                CloseLemonVehicleMenus(false);
                return;
            }

            if (chosen == null)
            {
                CloseLemonVehicleMenus(false);
                return;
            }

            int finalCost = totalCost;

            if (_luiUseDiscountTicket)
            {
                LoadVehicleDiscountTicketState();

                if (_vehicleDiscountTicketCount <= 0)
                {
                    Notification.Show(LT("Vehicle_NoDiscountTicket", "~HUD_COLOUR_DEGEN_RED~Bạn không còn thẻ giảm giá nào."));
                    PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                    return;
                }
            }

            if (Game.Player.Money < finalCost)
            {
                Notification.Show(LT(
                    "Vehicle_NoMoney",
                    "Bạn không đủ tiền để mua phương tiện này. Giá: ${price}",
                    "{price}", finalCost.ToString("N0")
                ));
                PlayFrontendSound("ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET");
                return;
            }

            Game.Player.Money -= finalCost;
            AddToSpendingAccumulator(finalCost);

            if (_luiUseDiscountTicket)
                TryConsumeVehicleDiscountTicket();

            ClearVehiclePreview();

            int purchasePriceForRegister = finalCost;
            string plateSnapshot = plateText;

            VehicleDelivery.RequestDelivery(chosen.Hash, player.Position, (veh) =>
            {
                try
                {
                    if (veh != null && veh.Exists())
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(plateSnapshot))
                                Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, veh.Handle, plateSnapshot);
                        }
                        catch { }

                        try
                        {
                            var meta = new PersistentManager.VehicleMeta
                            {
                                PurchasePrice = purchasePriceForRegister
                            };
                            PersistentManager.RegisterVehicle(veh, meta);
                        }
                        catch
                        {
                            try { PersistentManager.RegisterVehicle(veh); } catch { }
                        }
                    }
                }
                catch { }
            }, 150, 30000);

            chosen.TimesPurchased = Math.Max(0, chosen.TimesPurchased + 1);

            PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            ShowFeedMessage(
                L("VehicleFeed_Title", "Online Shop"),
                L("VehicleFeed_Subtitle", "Mua hàng"),
                LT("VehicleFeed_SuccessMessage", "{prefix} Dù gì thì cũng cảm ơn bạn đã mua hàng nhé! Nhớ quay lại mua tiếp nha!",
                "{prefix}", _msgSuccess[_rng.Next(_msgSuccess.Length)] ?? string.Empty)
            );

            CloseLemonVehicleMenus(true);
        }
        catch
        {
            CloseLemonVehicleMenus(false);
        }
    }

    private void StartVehicleSelection()
    {
        OpenAllVehiclesMenu();
    }

    private void UpdateVehicleSelectionMessage()
    {
        return;
    }

    // --- NEW: Mouse wheel support for vehicle selection (keeps existing left/right keys) ---
    private void ProcessVehicleSelectionWheelInput()
    {
        if (Game.IsLoading || Game.IsPaused || Game.IsCutsceneActive)
            return;

        var list = GetActiveVehicleList();
        if (list == null || list.Count == 0)
            return;

        // Controls cần disable khi đọc input (thêm NextCamera / LookBehind / VehicleLookBehind)
        GTA.Control[] controlsToDisable =
        {
            GTA.Control.SelectNextWeapon,
            GTA.Control.SelectPrevWeapon,
            GTA.Control.NextWeapon,
            GTA.Control.PrevWeapon,
            GTA.Control.CursorScrollDown,
            GTA.Control.CursorScrollUp,
        // Prevent camera toggle / pause interference while browsing selection
            GTA.Control.NextCamera,
            GTA.Control.LookBehind,
            GTA.Control.VehicleLookBehind,
            GTA.Control.FrontendPause,
            GTA.Control.FrontendPauseAlternate,

        // Also block M and F actions while browsing selection
            GTA.Control.InteractionMenu, // M
            GTA.Control.Enter,           // Context / Enter
            GTA.Control.VehicleExit      // F (enter/exit vehicle)
        };

        foreach (var ctrl in controlsToDisable)
        {
            SafeCall(() =>
            {
                Function.Call(
                    Hash.DISABLE_CONTROL_ACTION,
                    0,
                    (int)ctrl,
                    true
                );
            });
        }

        int now = Game.GameTime;
        if (now - _lastVehicleSelectionChangeTime < VehicleSelectionDebounceMs)
            return;

        bool nextPressed =
            SafeCall(() => Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.SelectNextWeapon), false) ||
            SafeCall(() => Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.NextWeapon), false) ||
            SafeCall(() => Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.CursorScrollDown), false);

        bool prevPressed =
            SafeCall(() => Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.SelectPrevWeapon), false) ||
            SafeCall(() => Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.PrevWeapon), false) ||
            SafeCall(() => Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.CursorScrollUp), false);

        bool moved = false;

        if (nextPressed)
        {
            _vehSeIn = (_vehSeIn + 1) % list.Count;
            moved = true;
        }
        else if (prevPressed)
        {
            _vehSeIn--;
            if (_vehSeIn < 0)
                _vehSeIn = list.Count - 1;

            moved = true;
        }

        if (!moved)
            return;

        _lastVehicleSelectionChangeTime = now;

        SafeCall(UpdateVehicleSelectionMessage);
        SafeCall(() => PlayFrontendSound("NAV_UP_DOWN", "HUD_FRONTEND_DEFAULT_SOUNDSET"));
    }

    private void AcceptVehiclePending()
    {
        Ped player = Game.Player.Character;

        // 1. Kiểm tra điều kiện cơ bản
        if (player == null || !player.Exists() || player.IsDead)
        {
            ClearPending(true, false);
            return;
        }

        if (_pendingType != PendingType.VehicleOffer || _pendingVehicleEntry == null)
        {
            ClearPending(true, false);
            return;
        }

        int totalCost = _pendingVehiclePrice;

        // 2. Kiểm tra tài chính
        if (Game.Player.Money < totalCost)
        {
            ClearPending(true, false);
            // Có thể thêm thông báo "Không đủ tiền" ở đây nếu muốn
            return;
        }

        Game.Player.Money -= totalCost;
        AddToSpendingAccumulator(totalCost); // <<< ADDED: tích lũy chi tiêu
        var chosen = _pendingVehicleEntry;

        // 4. GỌI DỊCH VỤ GIAO XE (Thay thế toàn bộ đoạn spawn cũ)
        int purchasePriceForRegister = totalCost;
        VehicleDelivery.RequestDelivery(chosen.Hash, player.Position, (veh) =>
        {
            try
            {
                if (veh != null && veh.Exists())
                {
                    try
                    {
                        var meta = new PersistentManager.VehicleMeta
                        {
                            PurchasePrice = purchasePriceForRegister
                        };
                        PersistentManager.RegisterVehicle(veh, meta);
                    }
                    catch
                    {
                        // Fallback: nếu vì lý do nào đó type/reflect không tồn tại, gọi overload cũ
                        try { PersistentManager.RegisterVehicle(veh); } catch { }
                    }
                }
            }
            catch { /* Tránh crash nếu manager lỗi */ }
        }, 150, 30000);

        // 5. PHẢN HỒI THÀNH CÔNG (Giao diện & Logic mua hàng)
        // Lưu ý: VehicleDelivery sẽ tự hiển thị thông báo "Xe đang được giao" 
        // nên ở đây ta chỉ xử lý logic hoàn tất giao dịch.
        chosen.TimesPurchased = Math.Max(0, chosen.TimesPurchased + 1);
        SafeCall(() => PlayFrontendSound("PURCHASE", "HUD_FRONTEND_DEFAULT_SOUNDSET"));
        _lastInteractionGameTime = Game.GameTime;

        // Dọn dẹp trạng thái chờ
        ClearPending(false, true);
    }
}