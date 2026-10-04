using System;
using System.IO;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 截图自动落盘：存到「设置 → 常用工具」里那个目录（**默认桌面**），文件名 `截图_yyyyMMdd_HHmmss.png`。
/// 撞名自动加序号，不覆盖旧图；闯了什么错都只写日志，绝不往外抛（截图本身不能因为存盘失败就没了）。
///
/// ⚠️ Win7 移植说明：原版用 WinRT 的 <c>Windows.Graphics.Imaging.BitmapDecoder</c> /
///    <c>BitmapEncoder</c> 把 BMP 字节转 PNG，**Win7 上没有 WinRT**，改成 GDI+（System.Drawing.Common，
///    csproj 已引用）：<c>Image.FromStream</c> 解 BMP → <c>ImageFormat.Png</c> 存回内存流。语义等价。
/// </summary>
public static class ShotSaver
{
    /// <summary>设置里那个目录；没设 / 不在了 → 系统桌面。</summary>
    public static string Dir()
    {
        try
        {
            var d = App.Settings.Current.ShotSaveDir;
            if (!string.IsNullOrWhiteSpace(d) && Directory.Exists(d)) return d;
        }
        catch { /* 读设置失败就走桌面 */ }

        try { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        catch { return ""; }
    }

    /// <summary>显示用：设置里的目录（不管存不存在）。</summary>
    public static string DirSetting()
    {
        try
        {
            var d = App.Settings.Current.ShotSaveDir;
            if (!string.IsNullOrWhiteSpace(d)) return d;
        }
        catch { }
        return Dir();
    }

    /// <summary>存 PNG（自动防撞名）。返回落盘路径；失败 null。</summary>
    public static string? Save(byte[] png, DateTime? at = null)
    {
        try
        {
            var dir = Dir();
            if (string.IsNullOrWhiteSpace(dir)) return null;
            Directory.CreateDirectory(dir);

            var stamp = (at ?? DateTime.Now).ToString("yyyyMMdd_HHmmss");
            var path = Path.Combine(dir, $"截图_{stamp}.png");
            for (var i = 2; i < 200 && File.Exists(path); i++)
                path = Path.Combine(dir, $"截图_{stamp}_{i}.png");

            File.WriteAllBytes(path, png);
            ScreenCapture.Log("截图已自动保存：" + path);
            return path;
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("截图自动保存失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>把 ScreenCapture 给的 BMP 字节转成 PNG 字节（落盘的一般是 PNG）。</summary>
    public static Task<byte[]?> BmpToPngAsync(byte[] bmp)
    {
        try
        {
            // ⚠️ GDI+ 的解码/编码走的是原生 GDI+，放后台线程做，别卡 UI 线程。
            return Task.Run<byte[]?>(() =>
            {
                try
                {
                    using var src = new MemoryStream(bmp, writable: false);
                    using var image = System.Drawing.Image.FromStream(src);

                    using var outMs = new MemoryStream();
                    image.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
                    return outMs.ToArray();
                }
                catch (Exception ex)
                {
                    ScreenCapture.Log("截图转 PNG 失败: " + ex.Message);
                    return null;
                }
            });
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("截图转 PNG 失败: " + ex.Message);
            return Task.FromResult<byte[]?>(null);
        }
    }

    /// <summary>截一块（ScreenFrame + 裁剪区）直接存成 PNG。返回落盘路径；失败 null。</summary>
    public static async Task<string?> SaveFrameAsync(ScreenFrame frame, int cx, int cy, int cw, int ch)
    {
        try
        {
            var bmp = ScreenCapture.ToBmp(frame, cx, cy, cw, ch);
            if (bmp is null) return null;
            var png = await BmpToPngAsync(bmp).ConfigureAwait(false);
            if (png is null) return null;
            return Save(png);
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("截图自动保存失败: " + ex.Message);
            return null;
        }
    }
}
