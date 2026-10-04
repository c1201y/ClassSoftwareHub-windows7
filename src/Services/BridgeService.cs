using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 网页 &lt;-&gt; 原生 桥接。
/// 网页用 window.chrome.webview.postMessage(JSON) 发指令；原生用 PostWebMessageAsJson 回事件。
/// 所有消息都是 {"type": "...", ...} 形状。
///
/// ⚠️ 移植说明：原版 <see cref="Attach"/> 接的是 WinUI 的 <c>Microsoft.UI.Xaml.Controls.WebView2</c> 控件，
///    但 Avalonia **没有** WebView2 控件类型（本工程只引了 <c>Microsoft.Web.WebView2</c> 的 Core）。
///    这里改成直接挂 <c>CoreWebView2</c>（<c>WebView2.CoreWebView2</c>），语义完全一致 ——
///    消息收发本来就只走 Core 的 <c>PostWebMessageAsJson</c>，不碰控件本身。
/// </summary>
public sealed class BridgeService
{
    private readonly MainWindow _window;
    private readonly SettingsStore _settings;
    private CoreWebView2? _core;

    public BridgeService(MainWindow window, SettingsStore settings)
    {
        _window = window;
        _settings = settings;
    }

    /// <summary>挂上当前网页浮层（WebSheet）的 Core。网页还没就绪时可以是 null，之后收到消息会再挂。</summary>
    public void Attach(CoreWebView2 core) => _core = core;

    // ---------- 原生 -> 网页 ----------

    public void Send(string type, object? payload = null)
    {
        var core = _core;
        if (core is null) return;

        JsonObject node;
        try
        {
            node = JsonSerializer.SerializeToNode(payload) as JsonObject ?? new JsonObject();
            node["type"] = type;
        }
        catch
        {
            node = new JsonObject { ["type"] = type };
        }

        try { core.PostWebMessageAsJson(node.ToJsonString()); } catch { /* 网页还没就绪 */ }
    }

    // ---------- 网页 -> 原生 ----------

    public void HandleWebMessage(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); } catch { return; }
        if (node is not JsonObject obj) return;

        var type = Str(obj["type"]);
        switch (type)
        {
            case "shell.ready":
            case "shell.getInfo":
                Send("shell.info", _window.BuildInfoPayload());
                break;

            case "shell.getState":
                Send("shell.state", _window.BuildStatePayload());
                break;

            // ---- 标题栏 / 拖动 ----
            case "shell.titlebar":
                _window.ApplyTitleBarRegions(obj);
                break;

            case "shell.titlebarUpdate":
                // 网页要求「重新计算」——它自己会再发一条 shell.titlebar 回来
                break;

            case "shell.beginDrag":
            case "start-window-drag":
                _window.BeginWindowDrag();
                break;

            case "shell.toggleMaximize":
            case "toggle-window-maximize":
                _window.ToggleWindowMaximize();
                break;

            // ---- 外观 ----
            case "shell.setBackdrop":
                _window.SetBackdrop(Str(obj["value"], "acrylic"));
                break;

            case "shell.setTheme":
                _window.SetTheme(Str(obj["value"], "system"));
                break;

            case "shell.webTheme":
                _window.OnWebThemeReported(Str(obj["value"]));
                break;

            case "shell.setAlwaysOnTop":
                _window.SetAlwaysOnTop(Bool(obj["value"]));
                break;

            case "shell.setWebTransparent":
                _window.SetWebTransparent(Bool(obj["value"], true));
                break;

            case "shell.setTitleBarColor":
                _window.SetTitleBarColor(Str(obj["value"]));
                break;

            // ---- 窗口 / 系统 ----
            case "shell.window":
                _window.HandleWindowCommand(Str(obj["action"]));
                break;

            case "shell.openExternal":
                _window.OpenExternal(Str(obj["url"]));
                break;

            case "shell.setTelemetry":
                _window.SetTelemetryEnabled(Bool(obj["value"]));
                break;

            case "shell.setAutoStart":
                _window.SetAutoStart(Bool(obj["value"]));
                break;

            case "shell.setZoom":
                _window.SetWebZoom(Double(obj["value"], 1.0));
                break;

            case "shell.log":
                System.Diagnostics.Debug.WriteLine("[web] " + Str(obj["message"]));
                break;

            default:
                System.Diagnostics.Debug.WriteLine("[bridge] unknown message: " + type);
                break;
        }
    }

    // ---------- 小工具 ----------

    private static string Str(JsonNode? n, string dflt = "")
    {
        try { return n is null ? dflt : (n.GetValue<string>() ?? dflt); }
        catch { return dflt; }
    }

    private static bool Bool(JsonNode? n, bool dflt = false)
    {
        try { return n is null ? dflt : n.GetValue<bool>(); }
        catch { return dflt; }
    }

    private static double Double(JsonNode? n, double dflt)
    {
        try { return n is null ? dflt : n.GetValue<double>(); }
        catch { return dflt; }
    }
}
