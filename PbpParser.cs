using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.PbpViewer {
    public class PbpInfo {
        public string Title { get; set; } 
        public string TitleId { get; set; } 
        public string AppVer { get; set; }
        public string PspSystemVer { get; set; } 
        public string Category { get; set; } 
        public string DiscId { get; set; } 
        public string DiscVersion { get; set; } 
        public string ParentalLevel { get; set; } 
        public string Region { get; set; } 
        public string Bootable { get; set; } 
        public string Attribute { get; set; } 
        public string FileSize { get; set; } 

        public BitmapImage Icon0 { get; set; }   // может быть null → default
        public BitmapImage Pic0 { get; set; }    // null = нет, без default
        public BitmapImage Pic1 { get; set; }    // null = нет, без default
        public BitmapImage Boot { get; set; }    // BOOT.PNG

        public bool HasIcon1 { get; set; }
        public string Icon1Size { get; set; } 
        public bool HasSnd0 { get; set; }
        public string Snd0Size { get; set; } 
        public string PbootTitle { get; set; }
        public string UpdaterVer { get; set; }
        public string DiscNumber { get; set; }
        public string DiscTotal { get; set; }
        public string IsDemo { get; set; }      // "Yes" / "No" / null
        public string IsFakeNp { get; set; }    // "Yes / Probably" / "No" / null
        public byte[] Snd0Data { get; set; }
    }

    public static class PbpParser {
        public static PbpInfo Parse(string path) {
            var info = new PbpInfo();

            var fi = new FileInfo(path);
            info.FileSize = FormatSize(fi.Length);

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(fs)) {
                if (fs.Length < 0x28)
                    throw new Exception("Файл слишком маленький");

                // Magic: 00 50 42 50
                byte[] magic = br.ReadBytes(4);
                if (magic.Length != 4 ||
                    magic[0] != 0x00 ||
                    magic[1] != (byte) 'P' ||
                    magic[2] != (byte) 'B' ||
                    magic[3] != (byte) 'P') {throw new Exception("Неверный magic PBP");}

                br.ReadUInt32(); // version

                // 8 offsets
                var offsets = new uint[8];
                for (int i = 0; i < 8; i++)
                    offsets[i] = br.ReadUInt32();

                // offsets:
                // 0 PARAM.SFO, 1 ICON0, 2 ICON1, 3 PIC0, 4 PIC1, 5 SND0, 6 DATA.PSP, 7 DATA.PSAR

                TryParseSfo(fs, br, offsets[0], GetEntryEnd(offsets, 0, (uint) fs.Length), info);

                TryLoadPng(
                    fs, br,
                    offsets[1],
                    GetEntryEnd(offsets, 1, (uint) fs.Length),
                    img => info.Icon0 = img);

                TryLoadPng(
                    fs, br,
                    offsets[3],
                    GetEntryEnd(offsets, 3, (uint) fs.Length),
                    img => info.Pic0 = img);

                TryLoadPng(
                    fs, br,
                    offsets[4],
                    GetEntryEnd(offsets, 4, (uint) fs.Length),
                    img => info.Pic1 = img);

                TryLoadBootPng(
                    fs,
                    br,
                    offsets[7],
                    GetEntryEnd(offsets, 7, (uint) fs.Length),
                    img => info.Boot = img);

                // ICON1 — реально существующая секция
                SetEntrySize(
                    offsets[2],
                    GetEntryEnd(offsets, 2, (uint) fs.Length),
                    size => {
                        info.HasIcon1 = size > 0;
                        info.Icon1Size = size > 0 ? FormatSize(size) : null;
                    });

                
                // SND0
                info.HasSnd0 = false;
                info.Snd0Size = null;
                info.Snd0Data = null;

                uint sndStart = offsets[5];
                uint sndEnd = GetEntryEnd(offsets, 5, (uint) fs.Length);

                if (sndStart > 0 && sndEnd > sndStart && sndEnd <= (uint) fs.Length) {
                    int size = (int) (sndEnd - sndStart);
                    if (size >= 16 && size <= 8 * 1024 * 1024) {
                        fs.Position = sndStart;
                        byte[] data = br.ReadBytes(size);
                        if (data != null && data.Length >= 16) {
                            info.Snd0Data = data;
                            info.HasSnd0 = true;
                            info.Snd0Size = FormatSize(size);
                        }
                    }
                }

                DetectDemoAndFakeNp(info, fi.Length);

            }

            return info;
        }
        static void DetectDemoAndFakeNp(PbpInfo info, long fileLength) {
            string id = info.DiscId?.Trim();
            if (string.IsNullOrEmpty(id)) {
                // иногда TITLE_ID вместо DISC_ID
                id = info.TitleId?.Trim();
            }
            if (string.IsNullOrEmpty(id)) {
                info.IsDemo = null;
                info.IsFakeNp = null;
                return;
            }

            bool inTable = DemoNpIsoSizes.ContainsKey(id);
            bool looksLikeDemoId = LooksLikeDemoProductCode(id);

            if (inTable || looksLikeDemoId)
                info.IsDemo = "Yes";
            else
                info.IsDemo = "No";

            // FakeNP: известное демо с ожидаемым NP_ISO, а файл заметно меньше
            info.IsFakeNp = null;
            if (inTable) {
                long expected = DemoNpIsoSizes[id];
                if (expected > 0) {
                    // порог: меньше ~98% ожидаемого NP_ISO
                    if (fileLength < (long) (expected * 0.98))
                        info.IsFakeNp = "Yes (probably)";
                    else
                        info.IsFakeNp = "No";
                }
            }
        }

        static bool LooksLikeDemoProductCode(string id) {
            // NPJH90xxx, NPUH90xxx, NPEH90xxx, NPJG90xxx, NPUG70xxx, NPHG00xxx...
            if (id.Length >= 9 && id.StartsWith("NP", StringComparison.OrdinalIgnoreCase)) {
                // 5-й символ часто '9' у демо (NPJH9....)
                if (id.Length >= 5 && id[4] == '9')
                    return true;
            }
            // ULED90xxx, UCED90xxx, ULUD90xxx, ULET00xxx...
            if (id.StartsWith("ULED", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("UCED", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("ULUD", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("ULET", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        static uint FindEnd(uint start, List<uint> sorted) {
            if (start == 0) return 0;
            foreach (var o in sorted)
                if (o > start) return o;
            return 0;
        }
        static uint GetEntryEnd(uint[] offsets, int index, uint fileLength) {
            if (index < 0 || index >= offsets.Length)
                return 0;

            uint start = offsets[index];

            if (start == 0 || start >= fileLength)
                return 0;

            // Следующий слот таблицы является границей текущего.
            // Если offset одинаковый — текущая секция отсутствует.
            if (index + 1 < offsets.Length) {
                uint next = offsets[index + 1];

                if (next <= start)
                    return 0;

                if (next > fileLength)
                    return 0;

                return next;
            }

            return (uint) fileLength;
        }
        static void TryLoadBootPng(
    FileStream fs,
    BinaryReader br,
    uint start,
    uint end,
    Action<BitmapImage> set) {
            if (start == 0 || end <= start || end > fs.Length)
                return;

            try {
                // Читаем только маленький заголовок (достаточно 0x30 байт)
                const int headerRead = 0x30;
                if (end - start < headerRead)
                    return;

                fs.Position = start;
                byte[] hdr = br.ReadBytes(headerRead);

                // Ищем PSISOIMG0000 (PS1 Classics) — самый частый случай с открытым STARTDAT
                // Magic: "PSISOIMG0000" (12 байт) + 4 байта = размер первой части = offset до STARTDAT
                if (hdr.Length >= 16 &&
                    hdr[0] == (byte) 'P' && hdr[1] == (byte) 'S' && hdr[2] == (byte) 'I' &&
                    hdr[3] == (byte) 'S' && hdr[4] == (byte) 'O' && hdr[5] == (byte) 'I' &&
                    hdr[6] == (byte) 'M' && hdr[7] == (byte) 'G' &&
                    hdr[8] == (byte) '0' && hdr[9] == (byte) '0' && hdr[10] == (byte) '0' && hdr[11] == (byte) '0') {
                    // Offset до второй части (STARTDAT) — последние 4 байта 16-байтового заголовка
                    uint secondPartOffset = BitConverter.ToUInt32(hdr, 12);

                    // Альтернативное место (некоторые дампы) — offset 0x1B
                    if (secondPartOffset == 0 || secondPartOffset >= (end - start)) {
                        if (hdr.Length >= 0x1F)
                            secondPartOffset = BitConverter.ToUInt32(hdr, 0x1B);
                    }

                    if (secondPartOffset > 0 && secondPartOffset < (end - start)) {
                        long special = start + secondPartOffset;

                        // Проверяем, что там действительно STARTDAT
                        fs.Position = special;
                        byte[] marker = br.ReadBytes(8);
                        if (marker.Length == 8 &&
                            marker[0] == (byte) 'S' && marker[1] == (byte) 'T' &&
                            marker[2] == (byte) 'A' && marker[3] == (byte) 'R' &&
                            marker[4] == (byte) 'T' && marker[5] == (byte) 'D' &&
                            marker[6] == (byte) 'A' && marker[7] == (byte) 'T') {
                            fs.Position = special + 0x10;
                            uint headerSize = br.ReadUInt32();
                            uint bootSize = br.ReadUInt32();

                            // Разумные проверки
                            if (headerSize >= 0x20 && headerSize <= 0x200 &&
                                bootSize >= 16 && bootSize <= 4 * 1024 * 1024) {
                                long bootStart = special + headerSize;
                                if (bootStart >= start && bootStart + bootSize <= end) {
                                    fs.Position = bootStart;
                                    byte[] png = br.ReadBytes((int) bootSize);

                                    if (png.Length >= 8 &&
                                        png[0] == 0x89 && png[1] == 0x50 &&
                                        png[2] == 0x4E && png[3] == 0x47) {
                                        var img = new BitmapImage();
                                        using (var ms = new MemoryStream(png)) {
                                            img.BeginInit();
                                            img.CacheOption = BitmapCacheOption.OnLoad;
                                            img.StreamSource = ms;
                                            img.EndInit();
                                            img.Freeze();
                                        }
                                        set(img);
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }

                // Fallback: очень ограниченный поиск только в первых ~2 МБ
                // (на случай нестандартных/кастомных PBP, где STARTDAT всё-таки открыт)
                const int maxScan = 2 * 1024 * 1024;
                long scanEnd = Math.Min(end, start + maxScan);
                long remaining = scanEnd - start;
                const int chunkSize = 64 * 1024; // маленький чанк
                byte[] buffer = new byte[chunkSize + 16];

                long absolute = start;
                while (remaining > 0) {
                    int toRead = (int) Math.Min(buffer.Length, remaining);
                    fs.Position = absolute;
                    int read = fs.Read(buffer, 0, toRead);
                    if (read < 8) break;

                    for (int i = 0; i <= read - 8; i++) {
                        if (buffer[i] == (byte) 'S' && buffer[i + 1] == (byte) 'T' &&
                            buffer[i + 2] == (byte) 'A' && buffer[i + 3] == (byte) 'R' &&
                            buffer[i + 4] == (byte) 'T' && buffer[i + 5] == (byte) 'D' &&
                            buffer[i + 6] == (byte) 'A' && buffer[i + 7] == (byte) 'T') {
                            long special = absolute + i;
                            fs.Position = special + 0x10;

                            uint headerSize = br.ReadUInt32();
                            uint bootSize = br.ReadUInt32();

                            if (headerSize >= 0x20 && headerSize <= 0x200 &&
                                bootSize >= 16 && bootSize <= 4 * 1024 * 1024) {
                                long bootStart = special + headerSize;
                                if (bootStart >= start && bootStart + bootSize <= end) {
                                    fs.Position = bootStart;
                                    byte[] png = br.ReadBytes((int) bootSize);

                                    if (png.Length >= 8 &&
                                        png[0] == 0x89 && png[1] == 0x50 &&
                                        png[2] == 0x4E && png[3] == 0x47) {
                                        var img = new BitmapImage();
                                        using (var ms = new MemoryStream(png)) {
                                            img.BeginInit();
                                            img.CacheOption = BitmapCacheOption.OnLoad;
                                            img.StreamSource = ms;
                                            img.EndInit();
                                            img.Freeze();
                                        }
                                        set(img);
                                        return;
                                    }
                                }
                            }
                        }
                    }

                    long advance = Math.Max(1, read - 16);
                    absolute += advance;
                    remaining -= advance;
                }
            }
            catch {
                // BOOT.PNG отсутствует или повреждён — просто выходим
            }
        }
        static void TryLoadPng(FileStream fs, BinaryReader br, uint start, uint end, Action<BitmapImage> set) {
            if (start == 0 || end <= start || end > fs.Length) return;
            try {
                int size = (int) (end - start);
                if (size < 16 || size > 4 * 1024 * 1024) return;

                fs.Position = start;
                byte[] data = br.ReadBytes(size);
                if (data.Length < 8 || data[0] != 0x89 || data[1] != 0x50 || data[2] != 0x4E || data[3] != 0x47)
                    return;

                var img = new BitmapImage();
                using (var ms = new MemoryStream(data)) {
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                }
                set(img);
            }
            catch { }
        }

        static void SetEntrySize(uint start, uint end, Action<long> set) {
            if (start == 0 || end <= start) { set(0); return; }
            set(end - start);
        }

        static void TryParseSfo(FileStream fs, BinaryReader br, uint start, uint end, PbpInfo info) {
            if (start == 0 || end <= start || end > fs.Length) return;

            try {
                fs.Position = start;
                int size = (int) (end - start);
                if (size < 20 || size > 1024 * 1024) return;

                byte[] data = br.ReadBytes(size);
                if (data.Length < 20) return;

                // Magic PSF
                if (data[0] != 0x00 || data[1] != 0x50 || data[2] != 0x53 || data[3] != 0x46)
                    return;

                uint keyTable = BitConverter.ToUInt32(data, 8);
                uint dataTable = BitConverter.ToUInt32(data, 12);
                uint count = BitConverter.ToUInt32(data, 16);

                if (count > 256 || keyTable >= data.Length || dataTable >= data.Length)
                    return;

                for (int i = 0; i < count; i++) {
                    int idx = 20 + i * 16;
                    if (idx + 16 > data.Length) break;

                    ushort keyOff = BitConverter.ToUInt16(data, idx);
                    ushort fmt = BitConverter.ToUInt16(data, idx + 2);
                    uint len = BitConverter.ToUInt32(data, idx + 4);
                    uint dataOff = BitConverter.ToUInt32(data, idx + 12);

                    int keyPos = (int) (keyTable + keyOff);
                    if (keyPos < 0 || keyPos >= data.Length) continue;

                    string key = ReadCString(data, keyPos);
                    if (string.IsNullOrEmpty(key)) continue;


                    // Только UTF-8 строки (0x0204)
                    //if (fmt != 0x0204) continue;

                    int valPos = (int) (dataTable + dataOff);
                    if (valPos < 0 || valPos + len > data.Length) continue;

                    if (fmt == 0x0404) // uint32
{
                        if (valPos + 4 > data.Length) continue;
                        uint num = BitConverter.ToUInt32(data, valPos);
                        switch (key) {
                            case "PARENTAL_LEVEL":
                                info.ParentalLevel = DecodeParental(num);
                                break;
                            case "REGION":
                                info.Region = DecodeRegion(num);
                                break;
                            case "BOOTABLE":
                                info.Bootable = num != 0 ? "Yes" : "No";
                                break;
                            case "ATTRIBUTE":
                                info.Attribute = "0x" + num.ToString("X8");
                                break;
                            case "DISC_NUMBER":
                                info.DiscNumber = num.ToString();
                                break;
                            case "DISC_TOTAL":
                                info.DiscTotal = num.ToString();
                                break;
                        }
                        continue;
                    }
                    if (fmt != 0x0204 && fmt != 0x0004) continue;
                    string value = Encoding.UTF8.GetString(data, valPos, (int) len).TrimEnd('\0').Trim();
                    if (string.IsNullOrEmpty(value)) continue;

                    switch (key) {
                        case "TITLE": info.Title = value; break;
                        case "TITLE_ID": info.TitleId = value; break;
                        case "APP_VER": info.AppVer = value; break;
                        case "PSP_SYSTEM_VER": info.PspSystemVer = value; break;
                        case "CATEGORY":
                            info.Category = DecodeCategory(value);  // ← было просто value
                            break;
                        case "DISC_ID": info.DiscId = value; break;
                        case "DISC_VERSION": info.DiscVersion = value; break;
                        case "PARENTAL_LEVEL":  // часто int — см. ниже
                            break;
                        case "REGION":
                            break;
                        case "BOOTABLE":
                            break;
                        case "ATTRIBUTE":
                            break;
                      
                        case "PBOOT_TITLE":
                            info.PbootTitle = value;
                            break;
                        case "UPDATER_VER":
                            info.UpdaterVer = value;
                            break;
                        
                    }

                }

            }

            catch {
                // игнорируем битый SFO
            }
        }
        static string DecodeCategory(string cat) {
            if (string.IsNullOrWhiteSpace(cat)) return null;
            cat = cat.Trim();
            switch (cat.ToUpperInvariant()) {
                case "MG": return "MG — Memory Stick Game / App / Update data";
                case "MS": return "MS — Memory Stick Save";
                case "UG": return "UG — UMD Disc Game";
                case "EG": return "EG — PSP Minis / Remaster / Episode";
                case "ME": return "ME — PS1 Classic";
                case "MA": return "MA — Application";
                case "PG": return "PG — Game Update";
                case "MN": return "MN — PSP Minis / Extended / Episode";
                case "PE": return "PE — PSP Remaster";
                case "PP": return "PP — Not bootable on PS3 OR Game update";
                case "1P": return "1P — PS1 Classic";
                default: return cat;
            }
        }

        static string DecodeParental(uint level) {
            // 1–11; типичные: 1=все, 5≈12+, 7≈15+, 9≈18+
            string age;
            if (level <= 1) age = "general audience";
            else if (level <= 4) age = "older children";
            else if (level <= 5) age = "~12 years";
            else if (level <= 7) age = "~15 years";
            else if (level <= 9) age = "~18 years";
            else age = "restricted";

            return $"{level} ({age})";
        }

        static string DecodeRegion(uint region) {
            // Часто 0x8000 и подобные битовые маски — показываем hex + подсказку
            if (region == 0) return "0 (unspecified)";
            return $"0x{region:X8}";
        }
        // DISC_ID → ожидаемый размер NP_ISO в байтах (0 = в таблице пусто, только факт "это демо")
        static readonly Dictionary<string, long> DemoNpIsoSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
{
    // --- с известным NP_ISO ---
    { "NPEG90005", 93880320L },
    { "NPJG90069", 126746624L },
    { "NPJH90100", 131563520L },
    { "NPJG90070", 132874240L },
    { "NPJG90059", 137461760L },      // Hot Shots Tennis; Misshitsu тоже NPJG90059 — один ID
    { "NPJH90126", 139984896L },
    { "NPEG90015", 142671872L },
    { "NPJH90072", 142704640L },
    { "NPJH90090", 146407424L },
    { "NPEH90049", 150306816L },
    { "NPJH90231", 152305664L },
    { "NPJH90267", 153419776L },
    { "NPJH90113", 154566656L },
    { "NPJH90083", 164069376L },
    { "NPUW00003", 170754048L },
    { "NPJH90182", 173015040L },
    { "NPEH90038", 183992320L },
    { "NPJH90130", 190480384L },
    { "NPEH90042", 192380928L },
    { "NPJH90205", 199229440L },
    { "NPJH90232", 200507392L },
    { "NPJH90110", 205717504L },
    { "NPJH90131", 206274560L },
    { "NPJH90122", 208732160L },
    { "NPJH90286", 209616896L },
    { "NPJH90076", 211156992L },
    { "NPJH90115", 216432640L },
    { "NPJH90134", 227508224L },
    { "NPJH90196", 228753408L },
    { "NPJH00094", 229539840L },
    { "NPJH90084", 234455040L },
    { "NPJH90068", 240582656L },
    { "NPJH90226", 241238016L },
    { "UCUS98734", 242515968L },
    { "NPUG70125", 248840192L },
    { "NPJH90135", 254148608L },
    { "NPUH90066", 266043392L },
    { "NPJH90082", 266665984L },
    { "NPJH90096", 274530304L },
    { "NPJH90063", 276660224L },
    { "NPJH90156", 280559616L },
    { "NPJH90273", 281772032L },
    { "NPJH90164", 283705344L },
    { "NPJH90324", 306249728L },
    { "NPJH90154", 306446336L },
    { "NPJG90100", 317784064L },
    { "NPJG90088", 319619072L },
    { "NPJH90206", 339836928L },
    { "NPJH90311", 344096768L },
    { "NPJH90097", 349569024L },
    { "NPJH90098", 349569024L },
    { "NPJH90069", 350224384L },
    { "NPJH90216", 369590272L },
    { "NPJH90274", 376897536L },
    { "NPEG90030", 376995840L },
    { "NPJH90123", 378732544L },
    { "NPJH90180", 380207104L },
    { "NPJH90200", 393707520L },
    { "NPJH90119", 398819328L },
    { "NPJH90155", 410943488L },
    { "NPJH90167", 415334400L },
    { "NPJG90107", 423821312L },
    { "NPJH90099", 432734208L },
    { "NPJH90280", 479330304L },
    { "NPJH90062", 512458752L },
    { "NPJH90209", 517046272L },
    { "NPJH90132", 540639232L },
    { "NPJH90157", 763527168L },
    { "NPJH90217", 1005322240L },
    { "NPJH90252", 1197965312L },

    // --- демо без NP_ISO (только факт "это демо", FakeNP по размеру не считаем) ---
    { "ULJM05500", 0L },
    { "UCJS10036", 0L },
    { "UCJS10041", 0L },
    { "UCKS45020", 0L },
    { "UCAS40063", 0L },
    { "UCUS98662", 0L },
    { "UCES00304", 0L },
    { "ULJS00021", 0L },
    { "ULJM05161", 0L },
    { "ULED90018", 0L },
    { "UCJS10043", 0L },
    { "UCES00373", 0L },
    { "UCES00206", 0L },
    { "UCUS98631", 0L },
    { "UCES00302", 0L },
    { "ULJS00068", 0L },
    { "UCES00422", 0L },
    { "UCJS10032", 0L },
    { "UCUS98646", 0L },
    { "UCES00279", 0L },
    { "UCED90007", 0L },
    { "ULED90008", 0L },
    { "UCUS98644", 0L },
    { "UCUS98641", 0L },
    { "ULED90009", 0L },
    { "ULKS46116", 0L },
    { "ULJM05232", 0L },
    { "UCUS98667", 0L },
    { "ULJM05171", 0L },
    { "ULJM05128", 0L },
    { "ULJM05126", 0L },
    { "ULJM05135", 0L },
    { "ULJM05189", 0L },
    { "UCJS10039", 0L },
    { "NPUH90001", 0L },
    { "ULJM05123", 0L },
    { "ULJM05184", 0L },
    { "ULJS00087", 0L },
    { "ULJM05201", 0L },
    { "ULED90040", 0L },
    { "ULJM05168", 0L },
    { "UCED90036", 0L },
    { "ULJM05170", 0L },
    { "UCJS10060", 0L },
    { "ULED90025", 0L },
    { "UCUS98645", 0L },
    { "UCED90042", 0L },
    { "ULED90043", 0L },
    { "ULJM05164", 0L },
    { "ULJS00105", 0L },
    { "ULET00870", 0L },
    { "NPEH90001", 0L },
    { "ULED90045", 0L },
    { "ULUS10260", 0L },
    { "UCUS98699", 0L },
    { "ULUD90004", 0L },
    { "NPJG90009", 0L },
    { "UCJS10075", 0L },
    { "ULJM05315", 0L },
    { "ULKS46142", 0L },
    { "NPJG90020", 0L },
    { "ULUS10289", 0L },
    { "NPJG90019", 0L },
    { "NPHG00005", 0L },
    { "ULJS00137", 0L },
    { "ULJM05352", 0L },
    { "ULJS00141", 0L },
    { "NPJH90004", 0L },
    { "ULJM91014", 0L },
    { "ULJS00138", 0L },
    { "ULJM05393", 0L },
    { "NPJH90007", 0L },
    { "ULJS00159", 0L },
    { "NPJH90010", 0L },
    { "NPJG90041", 0L },
    { "ULJM05409", 0L },
    { "NPJH90024", 0L },
    { "ULJM91017", 0L },
    { "ULJM05469", 0L },
    { "NPJH90034", 0L },
    { "NPJH90026", 0L },
    { "NPJH90025", 0L },
    { "NPJH90027", 0L },
    { "NPJG90047", 0L },
    { "NPJH90061", 0L },
    { "ULJS00233", 0L },
    { "NPJH90081", 0L },
    { "NPJH90170", 0L },
};

        static void TryLoadIcon(FileStream fs, BinaryReader br, uint start, uint end, PbpInfo info) {
            if (start == 0 || end <= start || end > fs.Length) return;

            try {
                int size = (int) (end - start);
                if (size < 16 || size > 2 * 1024 * 1024) return; // разумные пределы

                fs.Position = start;
                byte[] png = br.ReadBytes(size);

                // Проверка PNG magic
                if (png.Length < 8 ||
                    png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
                    return;

                var img = new BitmapImage();
                using (var ms = new MemoryStream(png)) {
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                }
                info.Icon0 = img;
            }
            catch {
                // битая иконка — просто оставляем null
            }
        }

        static string ReadCString(byte[] data, int offset) {
            int end = offset;
            while (end < data.Length && data[end] != 0)
                end++;
            if (end == offset) return null;
            return Encoding.ASCII.GetString(data, offset, end - offset);
        }

        static string FormatSize(long bytes) {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.00") + " MB";
        }

    }
}