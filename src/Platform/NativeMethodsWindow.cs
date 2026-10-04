using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// <see cref="NativeMethods"/> 的**第四批补充**：独立窗口（Views/*Window）需要的零散 user32 导出。
///
/// ⚠️ 为什么另开文件：<see cref="NativeMethods"/> 不是 partial 且移植期要求不修改已就绪的 Platform 文件，
///    没法就地扩展，所以按伴生类的惯例新开一个同命名空间的类。调用方一律走 <c>Platform.NativeMethods*</c>。
/// 每个导出仍按 <see cref="NativeMethods"/> 的惯例，在注释里标**最低可用系统**。
/// </summary>
internal static class NativeMethodsWindow
{
    /// <summary>最低 Win2000。取窗口客户区矩形（WebView 子窗宿主要铺满客户区，见 Views/WebSheetHost）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(IntPtr hWnd, out NativeMethods.RECT lpRect);

    /// <summary>
    /// 最低 Win8。Win32 POINTER_INFO（字段顺序/对齐照 WinUser.h 来，不能少不能换）。
    /// ⚠️ 只在「触摸/笔拖动侧边栏」时用到 <c>ptPixelLocation</c>（拿指针自己的绝对屏幕坐标）；
    ///    Win7 上这个导出**不存在**（GetPointerInfo 是 Win8 才引入的），调用会抛
    ///    EntryPointNotFoundException —— 调用方必须接住并退回「增量累加」那条路
    ///    （见 <c>ToolSidebarWindow.TryGetPointerScreenPoint</c> 的注释）。
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO pointerInfo);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public NativeMethodsShell.POINT ptPixelLocation;
        public NativeMethodsShell.POINT ptHimetricLocation;
        public NativeMethodsShell.POINT ptPixelLocationRaw;
        public NativeMethodsShell.POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }
}
