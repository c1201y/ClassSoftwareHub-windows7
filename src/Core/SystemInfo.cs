using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ClassSoftwareHub.Desktop.Platform;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 首页「硬件信息 / 系统信息」用：抓一份**本机**快照。
/// 全是本机只读查询（注册表 + SMBIOS + Win32），不联网、不落盘、不轮询。
///
/// ⚠️ Win7 移植说明：原版散在本文件里的 P/Invoke 已按契约收进 <c>Platform.NativeMethods</c> /
///    <c>Platform.NativeMethodsEx</c>；每个 API 的最低可用系统都标在 Platform 那边。
///    这里用到的全部是 Win7 就有的老 API（GetLogicalProcessorInformation / GetSystemFirmwareTable /
///    GlobalMemoryStatusEx / EnumDisplayDevices / GetPerformanceInfo / IsProcessorFeaturePresent），
///    没有一处依赖 Win10/Win11 专有能力。
/// </summary>
public static class SystemInfo
{
    /// <summary>一行信息：左标签 + 右值。</summary>
    public sealed record InfoLine(string Label, string Value);

    /// <summary>硬件信息 + 系统信息两个板块。</summary>
    public sealed record QuickInfo(IReadOnlyList<InfoLine> Hardware, IReadOnlyList<InfoLine> System);

    private const string Dash = "—";

    public static QuickInfo Gather()
    {
        var hw = new List<InfoLine>
        {
            new("处理器", CpuText()),
            new("内存", MemoryText()),
            new("硬盘", DiskText()),
            new("触摸", TouchText()),
            new("显卡", GpuText()),
            new("显存", VramText()),
            new("显示器", MonitorText()),
        };

        var sys = new List<InfoLine>
        {
            new("操作系统", OsNameText()),
            new("系统版本", OsVersionText()),
            new("安装日期", InstallDateText()),
            new("虚拟内存", PageFileText()),
            new("虚拟化", VirtualizationText()),
            new("DirectX", DirectXText()),
        };

        return new QuickInfo(Clean(hw), Clean(sys));
    }

    private static IReadOnlyList<InfoLine> Clean(List<InfoLine> list) =>
        list.Select(x => new InfoLine(x.Label, Blank(x.Value) ? Dash : x.Value.Trim())).ToList();

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    // ══════════════════════════ 硬件信息 ══════════════════════════

    // ── 处理器：型号 + 核心数 / 线程数 + 频率 ───────────────────
    private static string CpuText()
    {
        try
        {
            string name = "";
            int mhz = 0;
            using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            {
                name = (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
                if (k?.GetValue("~MHz") is int i && i > 0) mhz = i;
            }

            name = name.Replace("(R)", "").Replace("(TM)", "").Replace("CPU", "").Replace("  ", " ").Trim();
            // 型号串里常自带 "@ 2.60GHz"；我们自己算频率，去掉它免得重复
            name = System.Text.RegularExpressions.Regex.Replace(name, @"@\s*[\d.]+\s*GHz", "").Trim();

            var (cores, threads) = CpuCoreCounts();
            if (threads <= 0) threads = Environment.ProcessorCount;

            var parts = new List<string>();
            if (name.Length > 0) parts.Add(name);
            if (cores > 0) parts.Add($"{cores} 核 / {threads} 线程");
            else if (threads > 0) parts.Add($"{threads} 线程");
            if (mhz > 0) parts.Add((mhz / 1000d).ToString("0.00") + " GHz");
            return string.Join(" · ", parts);
        }
        catch { return ""; }
    }

    /// <summary>用 GetLogicalProcessorInformation 数物理核（Relationship 0 = ProcessorCore）。</summary>
    private static (int cores, int threads) CpuCoreCounts()
    {
        try
        {
            uint len = 0;
            NativeMethodsEx.GetLogicalProcessorInformation(IntPtr.Zero, ref len);
            if (len == 0) return (0, 0);

            var ptr = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!NativeMethodsEx.GetLogicalProcessorInformation(ptr, ref len)) return (0, 0);
                var size = Marshal.SizeOf<NativeMethodsEx.SLPI_ENTRY>();
                if (size <= 0) return (0, 0);
                var count = (int)(len / (uint)size);
                var cores = 0;
                for (var i = 0; i < count; i++)
                {
                    var e = Marshal.PtrToStructure<NativeMethodsEx.SLPI_ENTRY>(ptr + i * size);
                    if (e.Relationship == 0) cores++;   // RelationProcessorCore
                }
                return (cores, Environment.ProcessorCount);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { return (0, 0); }
    }

    // ── 内存：厂家 + 型号 + 类型 + 频率 + 插槽（SMBIOS Type 17） ──
    private static string MemoryText()
    {
        try
        {
            var modules = MemoryModules();
            var used = modules.Where(m => m.SizeMb > 0).ToList();

            if (used.Count > 0)
            {
                var totalMb = used.Sum(m => m.SizeMb);
                var distinct = used.Select(m => m.SizeMb).Distinct().OrderBy(x => x).ToList();
                var head = distinct.Count == 1 && used.Count > 1
                    ? $"{Mb(distinct[0])} × {used.Count}"
                    : Mb(totalMb);

                var parts = new List<string> { head };

                var type = used.Select(m => m.TypeName).FirstOrDefault(t => t.Length > 0) ?? "";
                var speed = used.Select(m => m.Speed).FirstOrDefault(s => s > 0);
                var ts = new List<string>();
                if (type.Length > 0) ts.Add(type);
                if (speed > 0) ts.Add(speed + " MT/s");
                if (ts.Count > 0) parts.Add(string.Join(" ", ts));

                var withBrand = used.FirstOrDefault(m => m.Manufacturer.Length > 0 || m.PartNumber.Length > 0);
                if (withBrand is not null)
                {
                    var brand = (VendorZh(withBrand.Manufacturer) + " " + withBrand.PartNumber).Trim();
                    if (brand.Length > 0) parts.Add(brand);
                }

                if (modules.Count > 0) parts.Add($"插槽 {used.Count}/{modules.Count}");

                return string.Join(" · ", parts);
            }

            // SMBIOS 解不出来：退回总量（至少容量是对的）
            var mstat = new NativeMethodsEx.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<NativeMethodsEx.MEMORYSTATUSEX>()
            };
            if (NativeMethodsEx.GlobalMemoryStatusEx(ref mstat) && mstat.ullTotalPhys > 0)
                return Size((long)mstat.ullTotalPhys);
            return "";
        }
        catch { return ""; }
    }

    private sealed record MemoryModule(long SizeMb, int Speed, string TypeName, string Manufacturer, string PartNumber);

    private static List<MemoryModule> MemoryModules()
    {
        var list = new List<MemoryModule>();
        var table = RawSmbios();
        if (table is null) return list;

        var pos = 8;   // 头 8 字节：4 字节（含主版本）+ 4 字节长度
        while (pos + 4 <= table.Length)
        {
            var type = table[pos];
            var length = table[pos + 1];
            if (length < 4) break;
            if (type == 127) break;   // End-of-table

            if (type == 17 && length >= 0x15 && pos + length <= table.Length)
            {
                var sizeRaw = (ushort)(table[pos + 0x0C] | (table[pos + 0x0D] << 8));
                long sizeMb;
                if ((sizeRaw & 0x8000) != 0) sizeMb = (sizeRaw & 0x7FFF) / 1024;          // 最高位 = KB
                else if (sizeRaw == 0 && length >= 0x20)                                   // Extended Size
                    sizeMb = BitConverter.ToUInt32(table, pos + 0x1C) & 0x7FFFFFFF;
                else sizeMb = sizeRaw;

                var memType = table[pos + 0x12];

                int speed = length >= 0x17 ? (table[pos + 0x15] | (table[pos + 0x16] << 8)) : 0;
                if (speed == 0 && length >= 0x58)
                    speed = (int)(BitConverter.ToUInt32(table, pos + 0x54) & 0x7FFFFFFF);   // Extended Speed

                var manufacturer = length > 0x17 ? SmbiosString(table, pos, length, table[pos + 0x17]) : "";
                var partNumber = length > 0x1A ? SmbiosString(table, pos, length, table[pos + 0x1A]) : "";

                list.Add(new MemoryModule(sizeMb, speed, MemoryTypeName(memType), Clean(manufacturer), Clean(partNumber)));
            }

            // 跳过本结构：先跳格式化区，再跳字符串区（以 00 00 结束）
            pos += length;
            while (pos + 1 < table.Length && !(table[pos] == 0 && table[pos + 1] == 0)) pos++;
            pos += 2;
        }
        return list;
    }

    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (c >= 0x20 && c < 0x7F) sb.Append(c);
        return sb.ToString().Trim();
    }

    /// <summary>取 SMBIOS 结构字符串区里的第 index 个字符串（1 起）。</summary>
    private static string SmbiosString(byte[] table, int formattedStart, int formattedLen, int index)
    {
        if (index <= 0) return "";
        var pos = formattedStart + formattedLen;
        var current = 1;
        while (pos < table.Length && table[pos] != 0)
        {
            var end = pos;
            while (end < table.Length && table[end] != 0) end++;
            if (current == index) return Encoding.ASCII.GetString(table, pos, end - pos);
            current++;
            pos = end + 1;
        }
        return "";
    }

    /// <summary>SMBIOS 规范 Table 7.18.2（Memory Device）的 Memory Type 取值。</summary>
    private static string MemoryTypeName(byte code) => code switch
    {
        0x03 => "DRAM",
        0x0F => "SDRAM",
        0x11 => "RDRAM",
        0x12 => "DDR",
        0x13 => "DDR2",
        0x14 => "DDR2 FB-DIMM",
        0x18 => "DDR3",
        0x19 => "FBD2",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x1F => "逻辑非易失内存",
        0x20 => "HBM",
        0x21 => "HBM2",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        0x24 => "HBM3",
        0x25 => "LPDDR5X",
        _ => "",
    };

    private static string VendorZh(string v) => v switch
    {
        "Samsung" => "三星",
        "SK Hynix" => "SK 海力士",
        "Hynix" => "海力士",
        "Hynix Semiconductor" => "海力士",
        "Micron" => "美光",
        "Micron Technology" => "美光",
        "Crucial" => "英睿达",
        "Kingston" => "金士顿",
        "Corsair" => "海盗船",
        "ADATA" => "威刚",
        "Nanya" => "南亚",
        "Ramaxel" => "记忆科技",
        "Transcend" => "创见",
        "G.Skill" => "芝奇",
        "Kingbank" => "金百达",
        _ => v,
    };

    private static byte[]? RawSmbios()
    {
        try
        {
            const uint RSMB = 0x52534D42;   // 'RSMB'
            var size = NativeMethodsEx.GetSystemFirmwareTable(RSMB, 0, IntPtr.Zero, 0);
            if (size == 0 || size > 8 * 1024 * 1024) return null;
            var buf = new byte[size];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                if (NativeMethodsEx.GetSystemFirmwareTable(RSMB, 0, handle.AddrOfPinnedObject(), size) == 0) return null;
            }
            finally { handle.Free(); }
            return buf;
        }
        catch { return null; }
    }

    // ── 硬盘：型号（注册表 disk Enum，无需管理员）+ 已用 / 总容量 ──
    private static string DiskText()
    {
        try
        {
            var models = PhysicalDiskModels();

            long total = 0, free = 0;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    total += d.TotalSize;
                    free += d.TotalFreeSpace;
                }
                catch { }
            }
            if (total <= 0) return models.Count > 0 ? string.Join(" + ", models) : "";

            var modelPart = models.Count > 0 ? string.Join(" + ", models.Take(2)) + " · " : "";
            return $"{modelPart}已用 {Size(total - free)} / 共 {Size(total)}";
        }
        catch { return ""; }
    }

    /// <summary>
    /// HKLM\SYSTEM\CurrentControlSet\Services\disk\Enum 里的值形如
    /// <c>SCSI\Disk&amp;Ven_NVMe&amp;Prod_Samsung_SSD_980_PRO_1TB&amp;Rev_...</c>；拆成厂家 + 型号。
    /// </summary>
    private static List<string> PhysicalDiskModels()
    {
        var result = new List<string>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\disk\Enum");
            if (k is null) return result;
            foreach (var name in k.GetValueNames().Where(n => n.All(char.IsDigit)).OrderBy(n => n.Length).ThenBy(n => n))
            {
                var raw = k.GetValue(name) as string;
                if (Blank(raw)) continue;

                var ven = Token(raw!, "Ven_");
                var prod = Token(raw!, "Prod_");
                var bus = new[] { "NVMe", "ATA", "SCSI", "USB", "SATA", "RAID", "Virtual", "Msft" };
                string model;
                if (bus.Contains(ven, StringComparer.OrdinalIgnoreCase) || ven.Length == 0)
                    model = prod;
                else
                    model = (ven + " " + prod).Trim();

                if (model.Length > 0 && !result.Contains(model)) result.Add(model);
            }
        }
        catch { }
        return result;
    }

    private static string Token(string s, string marker)
    {
        var i = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        i += marker.Length;
        var e = s.IndexOfAny(new[] { '&', '\\' }, i);
        var v = e < 0 ? s.Substring(i) : s.Substring(i, e - i);
        return v.Replace('_', ' ').Trim();
    }

    // ── 触摸：最大触控点数 ────────────────────────────────────
    private static string TouchText()
    {
        try
        {
            var n = NativeMethods.GetSystemMetrics(NativeMethodsEx.SM_MAXIMUMTOUCHES);   // SM_MAXIMUMTOUCHES
            return n > 0 ? $"{n} 点触控" : "不支持触控";
        }
        catch { return ""; }
    }

    // ── 显卡：适配器名（多显卡用 ｜ 分开） ─────────────────────
    private static string GpuText()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return "";
            var names = new List<string>();
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)).OrderBy(n => n))
            {
                using var k = cls.OpenSubKey(sub);
                var d = (k?.GetValue("DriverDesc") as string)?.Trim();
                if (!Blank(d) && !names.Contains(d!)) names.Add(d!);
            }
            var real = names.Where(n => !IsVirtualAdapter(n)).ToList();
            return string.Join(" ｜ ", (real.Count > 0 ? real : names).Take(2));
        }
        catch { return ""; }
    }

    private static bool IsVirtualAdapter(string name) =>
        name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Mirror", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Remote", StringComparison.OrdinalIgnoreCase);

    // ── 显存：显示适配器子键里的 qwMemorySize / MemorySize ─────
    private static string VramText()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return "";
            long best = 0;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k is null) continue;

                long v = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    int i => (uint)i,
                    _ => 0,
                };
                if (v <= 0 && k.GetValue("HardwareInformation.MemorySize") is byte[] b && b.Length >= 4)
                    v = BitConverter.ToUInt32(b, 0);

                if (v > best) best = v;
            }
            return best > 0 ? Size(best) : "";
        }
        catch { return ""; }
    }

    // ── 显示器：厂家 + 型号 + 分辨率 + 刷新率 ──────────────────
    private static string MonitorText()
    {
        try
        {
            var name = MonitorModel();

            var devName = PrimaryDisplayName();
            var dm = new NativeMethodsEx.DEVMODE { dmSize = (ushort)Marshal.SizeOf<NativeMethodsEx.DEVMODE>() };
            if (devName.Length == 0 || !NativeMethodsEx.EnumDisplaySettings(devName, -1, ref dm))
                return name;

            var parts = new List<string>();
            if (name.Length > 0) parts.Add(name);
            if (dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0) parts.Add($"{dm.dmPelsWidth} × {dm.dmPelsHeight}");
            if (dm.dmDisplayFrequency > 1) parts.Add(dm.dmDisplayFrequency + " Hz");
            return string.Join(" · ", parts);
        }
        catch { return ""; }
    }

    private static string PrimaryDisplayName()
    {
        var dd = new NativeMethodsEx.DISPLAY_DEVICE { cb = Marshal.SizeOf<NativeMethodsEx.DISPLAY_DEVICE>() };
        for (uint i = 0; NativeMethodsEx.EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            const int attachedToDesktop = 0x00000001, primaryDevice = 0x00000004;
            if ((dd.StateFlags & attachedToDesktop) != 0 && (dd.StateFlags & primaryDevice) != 0)
                return dd.DeviceName ?? "";
            dd = new NativeMethodsEx.DISPLAY_DEVICE { cb = Marshal.SizeOf<NativeMethodsEx.DISPLAY_DEVICE>() };
        }
        return @"\\.\DISPLAY1";
    }

    /// <summary>从注册表里第一个有效 EDID 拿「厂家简称 + 型号」。</summary>
    private static string MonitorModel()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
            if (root is null) return "";
            foreach (var mfg in root.GetSubKeyNames())
            {
                using var mk = root.OpenSubKey(mfg);
                if (mk is null) continue;
                foreach (var inst in mk.GetSubKeyNames())
                {
                    using var ik = mk.OpenSubKey(inst + @"\Device Parameters");
                    if (ik?.GetValue("EDID") is not byte[] edid || edid.Length < 128) continue;

                    var vendor = EdidVendor(edid);
                    var model = EdidModel(edid);
                    if (model.Length == 0) model = mfg;   // 退回注册表键名（如 DEL4098）

                    var text = (vendor + " " + model).Trim();
                    if (text.Length > 0) return text;
                }
            }
        }
        catch { }
        return "";
    }

    private static readonly Dictionary<string, string> MonitorVendorZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DEL"] = "Dell", ["AUS"] = "ASUS", ["SAM"] = "Samsung", ["ACR"] = "Acer",
        ["LEN"] = "Lenovo", ["HPN"] = "HP", ["HWP"] = "HP", ["GSM"] = "LG",
        ["BNQ"] = "BenQ", ["MSI"] = "MSI", ["PHL"] = "Philips", ["VSC"] = "ViewSonic",
        ["HKC"] = "HKC", ["AOC"] = "AOC", ["CMN"] = "京东方", ["BOE"] = "京东方",
        ["AUO"] = "友达光电", ["INL"] = "InnoLux", ["SDC"] = "三星显示", ["LGD"] = "LG Display",
        ["CSO"] = "华星光电", ["HEC"] = "HEC", ["SHP"] = "夏普",
    };

    private static string EdidVendor(byte[] edid)
    {
        var v = (edid[8] << 8) | edid[9];
        var c1 = (char)(((v >> 10) & 0x1F) + 'A' - 1);
        var c2 = (char)(((v >> 5) & 0x1F) + 'A' - 1);
        var c3 = (char)((v & 0x1F) + 'A' - 1);
        var code = new string(new[] { c1, c2, c3 });
        return MonitorVendorZh.TryGetValue(code, out var zh) ? zh : code;
    }

    private static string EdidModel(byte[] edid)
    {
        foreach (var off in new[] { 54, 72, 90, 108 })
        {
            if (off + 18 > edid.Length) continue;
            if (edid[off] != 0 || edid[off + 1] != 0 || edid[off + 3] != 0xFC) continue;
            var sb = new StringBuilder();
            for (var i = off + 5; i < off + 18; i++)
            {
                if (edid[i] == 0x0A) break;
                if (edid[i] >= 0x20 && edid[i] < 0x7F) sb.Append((char)edid[i]);
            }
            var s = sb.ToString().Trim();
            if (s.Length > 0) return s;
        }
        return "";
    }

    // ══════════════════════════ 系统信息 ══════════════════════════

    // ── 反馈中心「本机信息」要的三样（2026-09-30 Nick 指定）：型号 / 版本名 / 具体内部版本号 ──

    /// <summary>
    /// 处理器**型号**：只要型号串，不带核数、不带频率（如「13th Gen Intel(R) Core(TM) i7-13700H」）。
    /// 与首页那张「处理器」行刻意不同 —— 反馈正文里维护者要看的是"什么型号"，不是"几核几线程"。
    /// </summary>
    public static string CpuModel()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
            // 型号串常自带 "@ 2.60GHz"，删掉 —— 那是当前频率，跟型号无关
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*@\s*[\d.]+\s*GHz", "").Trim();
            // Intel 的串尾巴上多半还挂一个 " CPU"（"…i7-9750H CPU"）—— 反馈正文里是噪声，去掉。
            // ⚠️ 只删**结尾**那个，别全局替换：Xeon 的正式型号是 "Xeon(R) CPU E5-2670 v2"，
            //    全局替换会把型号本身改掉。
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+CPU$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            return name;
        }
        catch { return ""; }
    }

    /// <summary>操作系统的完整显示名（如「Windows 11 专业版 25H2」）—— 反馈正文里用。</summary>
    public static string OsDisplay() => OsNameText();

    /// <summary>
    /// **具体**的内部版本号：<c>CurrentBuildNumber</c> + <c>UBR</c>（如 <c>26220.1234</c>）。
    /// ⚠️ 只给 <c>CurrentBuildNumber</c>（26220）是不够的 —— 同一个 build 上装没装某个累积更新
    ///    就靠 UBR 区分，排查"打了某补丁之后才出问题"时缺了它根本对不上。
    /// </summary>
    public static string OsBuildEx()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = (k?.GetValue("CurrentBuildNumber") as string)?.Trim() ?? "";
            if (build.Length == 0) return "";

            // UBR 是 REG_DWORD；个别阉割系统上可能读不到，那就只给 build
            var ubr = (k?.GetValue("UBR"))?.ToString()?.Trim() ?? "";
            return ubr.Length > 0 ? $"{build}.{ubr}" : build;
        }
        catch { return ""; }
    }

    // ── 操作系统：名称 + 大版本号（如 Windows 11 专业版 24H2） ──
    private static string OsNameText()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = (k?.GetValue("ProductName") as string)?.Trim() ?? "Windows";
            var build = k?.GetValue("CurrentBuildNumber") as string ?? "";
            var display = (k?.GetValue("DisplayVersion") as string)?.Trim()
                          ?? (k?.GetValue("ReleaseId") as string)?.Trim() ?? "";
            var editionId = (k?.GetValue("EditionID") as string)?.Trim() ?? "";

            // ⚠️ 2026-10-02 实机反馈（Win7 机器上显示「Windows 10 Windows 7 Ultimate」）：
            //    以前只认 Win10/11 —— build < 22000 一律叫 "Windows 10"，认不出的 EditionID
            //    又直接把 ProductName（Win7 上是英文 "Windows 7 Ultimate"）整段拼上去。
            //    现在按**真实内核版本**（OsInfo.Raw 走 RtlGetVersion，与 OsInfo 同一数据源，
            //    不受 manifest 兼容垫片影响）判全家桶：7 / 8 / 8.1 / 10 / 11。
            var win = FamilyName();

            var edition = EditionZh(editionId);
            if (edition.Length == 0)
            {
                // 认不出 EditionID：把 ProductName 开头的 "Windows xx" 剥掉只留版本词
                //（Win7 的 ProductName 是英文 "Windows 7 Ultimate" → 剥出 "Ultimate"）
                edition = System.Text.RegularExpressions.Regex
                    .Replace(product, @"^Windows\s*(\d+(\.\d+)?)?\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    .Trim();
                if (edition.Length == 0) edition = product;
            }

            return $"{win} {edition} {display}".Trim();
        }
        catch { return ""; }
    }

    /// <summary>
    /// Windows 家族名。数据源用 <see cref="OsInfo.Raw"/>（RtlGetVersion）——
    /// Environment.OSVersion 在 Win7 上会被 exe 清单的 supportedOS 垫片统一报成 6.2，不能用（见 OsInfo 头注释）。
    /// </summary>
    private static string FamilyName()
    {
        var v = OsInfo.SimulateWin7 ? new Version(6, 1, 7601) : OsInfo.Version;
        if (v.Major is 6 or >= 10)
        {
            if (v.Major == 6) return v.Minor switch { 0 => "Windows Vista", 1 => "Windows 7", 2 => "Windows 8", _ => "Windows 8.1" };
            return v.Build >= 22000 ? "Windows 11" : "Windows 10";
        }
        return "Windows";
    }

    /// <summary>ProductName 在本机可能是英文，按 EditionID 换成中文版名（含 Win7 系的 ID）。</summary>
    private static string EditionZh(string id) => id switch
    {
        "Professional" => "专业版",
        "ProfessionalN" => "专业版 N",
        "Core" => "家庭版",
        "CoreN" => "家庭版 N",
        "CoreSingleLanguage" => "家庭单语言版",
        "CoreCountrySpecific" => "家庭中文版",
        "Enterprise" => "企业版",
        "EnterpriseN" => "企业版 N",
        "EnterpriseS" => "企业版 LTSC",
        "EnterpriseSN" => "企业版 LTSC N",
        "Education" => "教育版",
        "EducationN" => "教育版 N",
        "ProfessionalEducation" => "专业教育版",
        "ProfessionalWorkstation" => "专业工作站版",
        "ProfessionalWorkstationN" => "专业工作站版 N",
        "IoTEnterprise" => "IoT 企业版",
        "IoTEnterpriseS" => "IoT 企业版 LTSC",
        "Starter" => "入门版",
        // ── Win7 / Win8 系的 EditionID（注册表里就有，不需要额外数据源）──
        "Ultimate" => "旗舰版",
        "HomeBasic" => "家庭普通版",
        "HomePremium" => "家庭高级版",
        "HomeBasicN" => "家庭普通版 N",
        "HomePremiumN" => "家庭高级版 N",
        "Prerelease" => "预览版",
        _ => "",
    };

    // ── 操作系统版本：10.0.26100.4188（64 位） ─────────────────
    private static string OsVersionText()
    {
        try
        {
            // ⚠️ 2026-10-02 实机反馈（Win7 上显示 10.0.7601.28117）：
            //    CurrentMajorVersionNumber / CurrentMinorVersionNumber 是 **Win10 才有的注册表键**，
            //    Win7 上读不到，原来回退硬编码 "10.0" —— 版本号直接错了。
            //    主/次版本改用 OsInfo.Raw（RtlGetVersion，真实内核 6.1.7601），build/UBR 仍走注册表。
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var raw = OsInfo.SimulateWin7 ? new Version(6, 1, 7601) : OsInfo.Version;
            var build = k?.GetValue("CurrentBuildNumber") as string ?? "";
            var ubr = k?.GetValue("UBR")?.ToString() ?? "";
            var version = $"{raw.Major}.{raw.Minor}.{build}";
            if (ubr.Length > 0) version += "." + ubr;
            var bits = Environment.Is64BitOperatingSystem ? "64 位" : "32 位";
            return $"{version}（{bits}）";
        }
        catch { return ""; }
    }

    // ── 安装日期 ─────────────────────────────────────────────
    private static string InstallDateText()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k?.GetValue("InstallDate") is int secs && secs > 0)
                return DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime().ToString("yyyy-MM-dd");
            if (k?.GetValue("InstallTime") is long ft && ft > 0)
                return DateTime.FromFileTime(ft).ToString("yyyy-MM-dd");
        }
        catch { }
        return "";
    }

    // ── 虚拟内存 / 分页文件 ───────────────────────────────────
    // Windows 口径：虚拟内存总量 = 物理内存 + 分页文件（＝提交上限 CommitLimit）。
    private static string PageFileText()
    {
        try
        {
            if (!NativeMethodsEx.GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<NativeMethodsEx.PERFORMANCE_INFORMATION>())) return "";
            var page = (long)pi.PageSize;
            if (page <= 0) return "";

            var limitBytes = (long)pi.CommitLimit * page;                  // 虚拟内存总量
            var pageFileBytes = ((long)pi.CommitLimit - (long)pi.PhysicalTotal) * page;   // 分页文件大小

            var parts = new List<string> { $"共 {Size(limitBytes)}" };
            parts.Add(pageFileBytes > 0 ? $"分页文件 {Size(pageFileBytes)}" : "分页文件 未启用");
            return string.Join(" · ", parts);
        }
        catch { return ""; }
    }

    // ── 虚拟化是否开启 ────────────────────────────────────────
    // 注意：Hyper-V / VBS 跑起来后，固件层的虚拟化位会被监控程序挡住
    // （此时 IsProcessorFeaturePresent(PF_VIRT_FIRMWARE_ENABLED) 与 WMI 的
    //  VirtualizationFirmwareEnabled 都会报 false，但机器实际是开着的，
    //  任务管理器也会显示「已启用」）→ 所以要再把「监控程序在跑」当作已启用。
    private static string VirtualizationText()
    {
        try
        {
            if (NativeMethodsEx.IsProcessorFeaturePresent(21)) return "已启用";                     // PF_VIRT_FIRMWARE_ENABLED
            if (VbsEnabled() || HypervisorServicePresent()) return "已启用（Hyper-V 运行中）";
            return "未启用";
        }
        catch { return ""; }
    }

    private static bool VbsEnabled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard");
            if (k is null) return false;
            if (k.GetValue("EnableVirtualizationBasedSecurity") is int v && v == 1) return true;

            using var s = k.OpenSubKey(@"Scenarios\HypervisorEnforcedCodeIntegrity");
            return s?.GetValue("Enabled") is int e && e == 1;
        }
        catch { return false; }
    }

    /// <summary>Hyper-V 监控程序在跑时，VMBus / 虚拟化平台驱动是存在的。</summary>
    private static bool HypervisorServicePresent()
    {
        try
        {
            foreach (var name in new[] { "VMBus", "vmbusr", "HvHost" })
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
                if (k is not null) return true;
            }
        }
        catch { }
        return false;
    }

    // ── DirectX 版本 ─────────────────────────────────────────
    private static string DirectXText()
    {
        try
        {
            var dir = Environment.SystemDirectory;
            if (File.Exists(Path.Combine(dir, "d3d12.dll")) || File.Exists(Path.Combine(dir, "d3d12core.dll")))
                return "DirectX 12";
            if (File.Exists(Path.Combine(dir, "d3d11.dll")))
                return "DirectX 11";

            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX");
            var v = (k?.GetValue("Version") as string)?.Trim();
            return Blank(v) ? "" : "DirectX " + v;
        }
        catch { return ""; }
    }

    // ── 数值格式化 ────────────────────────────────────────────
    private static string Mb(long mb) => mb >= 1024 ? (mb / 1024d).ToString("0.#") + " GB" : mb + " MB";

    private static string Size(long bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        var g = bytes / gb;
        return g >= 1000 ? (g / 1024d).ToString("0.##") + " TB" : g.ToString("0.#") + " GB";
    }
}
