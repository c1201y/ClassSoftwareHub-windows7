using System.Net.Sockets;
using System.Text;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 教室喊话收件端的内置局域网发送页（自上游 1.8 移植）。
/// <para>
/// 手机浏览器直接访问 <c>http://本机IP:47821/</c> 就能投递一条喊话。由收件端直接返回页面，
/// 页面与 <c>/announce</c> 同源，不受 file:// 不透明来源限制，能读到真实发送结果。
/// </para>
/// </summary>
public static partial class AnnounceLanService
{
    /// <summary>返回一段 HTML（保持 Connection: close，与其它路由一致）。</summary>
    private static async Task WriteHtmlAsync(NetworkStream stream, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        var head = "HTTP/1.1 200 OK\r\n"
                   + "Content-Type: text/html; charset=utf-8\r\n"
                   + $"Content-Length: {bytes.Length}\r\n"
                   + "Cache-Control: no-store\r\n"
                   + "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    /// <summary>
    /// 手机浏览器直接访问 <c>http://本机IP:47821/</c> 就能用的发送页。
    /// ⛔ 配对码**故意不预填**：这个页面同网段谁都能打开，预填等于把码公开。
    /// </summary>
    private const string TestPageHtml = @"<!doctype html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1,viewport-fit=cover"">
<meta name=""color-scheme"" content=""light dark"">
<title>局域网喊话</title>
<style>
:root{--bg:#f6f7f9;--card:#fff;--text:#16181d;--sub:#6b7280;--line:#e3e6eb;--accent:#2b6df6;--ok:#137a4b;--err:#b91c1c}
@media (prefers-color-scheme:dark){:root{--bg:#14161a;--card:#1c1f25;--text:#f2f4f7;--sub:#9aa3af;--line:#2b3037;--accent:#2b6df6;--ok:#4ade80;--err:#f87171}}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
body{margin:0;padding:20px 16px 40px;background:var(--bg);color:var(--text);
font:15px/1.6 -apple-system,""Segoe UI"",""Microsoft YaHei"",system-ui,sans-serif}
h1{margin:4px 0 2px;font-size:20px;font-weight:650}
.sub{margin:0 0 18px;font-size:13px;color:var(--sub)}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:16px;margin-bottom:14px}
label{display:block;font-size:13px;color:var(--sub);margin:0 0 6px}
input,textarea,select{width:100%;padding:11px 12px;font:inherit;color:var(--text);background:var(--bg);
border:1px solid var(--line);border-radius:10px;outline:none;-webkit-appearance:none}
input:focus,textarea:focus,select:focus{border-color:var(--accent)}
textarea{min-height:88px;resize:vertical}
.f+.f{margin-top:14px}
.row{display:flex;gap:10px}.row>*{flex:1;min-width:0}
button{width:100%;padding:15px;font:inherit;font-size:16px;font-weight:600;color:#fff;
background:var(--accent);border:0;border-radius:12px}
button:disabled{opacity:.55}
#s{margin-top:14px;padding:12px;border-radius:10px;font-size:14px;display:none}
#s.on{display:block}
#s.ok{background:rgba(19,122,75,.12);color:var(--ok)}
#s.err{background:rgba(185,28,28,.12);color:var(--err)}
</style>
</head>
<body>
<h1>局域网喊话</h1>
<p class=""sub"">同一局域网内直连本机，不经过服务器。</p>
<div class=""card"">
<div class=""f""><label>配对码（6 位）</label>
<input id=""code"" inputmode=""numeric"" maxlength=""6"" placeholder=""见接收端界面"" autocomplete=""off""></div>
<div class=""f""><label>内容（不超过 100 字）</label>
<textarea id=""text"" maxlength=""100"">手机直连测试：这条消息从手机发到了这台电脑</textarea></div>
<div class=""f row"">
<div><label>署名</label><input id=""from"" maxlength=""20"" value=""手机""></div>
<div><label>展示时长</label><select id=""dur"">
<option value=""15"">15 秒</option><option value=""30"" selected>30 秒</option><option value=""60"">60 秒</option>
</select></div>
</div>
</div>
<button id=""b"">发送喊话</button>
<div id=""s""></div>
<script>
(function(){
var b=document.getElementById('b'),s=document.getElementById('s');
function show(k,t){s.className='on '+k;s.textContent=t;}
b.onclick=function(){
  var code=document.getElementById('code').value.trim();
  var text=document.getElementById('text').value.trim();
  var from=document.getElementById('from').value.trim();
  var dur=parseInt(document.getElementById('dur').value,10)||30;
  if(!/^\d{6}$/.test(code)){show('err','配对码必须是 6 位数字');return;}
  if(!text){show('err','内容不能为空');return;}
  b.disabled=true;show('ok','正在发送……');
  fetch('/announce',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({pairCode:code,text:text,from:from,duration:dur,
      id:'web-'+Math.random().toString(16).slice(2,14)})})
  .then(function(r){return r.text().then(function(t){return {c:r.status,b:t};});})
  .then(function(res){
    if(res.c===200){show('ok','已送达。电脑上应当已经弹出这条喊话。');}
    else if(res.c===403){show('err','配对码不正确。');}
    else if(res.c===429){show('err','发送过于频繁 —— 同一个配对码 10 秒内只接受一条。');}
    else{show('err','服务端返回 '+res.c+'：'+res.b);}
  })
  .catch(function(e){show('err','发送失败：'+e.message);})
  .then(function(){b.disabled=false;});
};
})();
</script>
</body>
</html>
";
}
