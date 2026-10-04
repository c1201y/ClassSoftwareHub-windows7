using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// <see cref="NativeMethods"/> 的**补充**：内存工作集回收（<c>Services.MemoryTrimmer</c> 专用）。
///
/// ⚠️ 为什么另开文件：<see cref="NativeMethods"/> 不是 partial 且移植期要求不修改已就绪的
///    Platform 文件，所以按伴生类的惯例新开一个同命名空间的类。调用方一律走 <c>Platform.NativeMethods*</c>。
///
/// 每个导出仍按 <see cref="NativeMethods"/> 的惯例，在注释里标**最低可用系统**。
/// </summary>
internal static class NativeMethodsMemory
{
    /// <summary>最低 Win2000。取当前进程的伪句柄（不用 CloseHandle）。</summary>
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// 最低 Win2000（Win7 完整可用）。把进程工作集里闲置的页丢进系统 standby list，
    /// 别的程序要用内存时系统优先回收 —— 教学机 8G 内存防涨靠它。
    /// </summary>
    [DllImport("psapi.dll", SetLastError = true)]
    internal static extern bool EmptyWorkingSet(IntPtr hProcess);
}
