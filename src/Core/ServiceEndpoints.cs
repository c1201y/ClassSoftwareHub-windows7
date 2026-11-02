namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 自建后端（Cloudflare Worker <c>classhub</c>）的入口清单 —— **唯一的定义处**。
///
/// <para>
/// 为什么单独一个文件：这份清单被两处消费 —— 喊话 / 投稿 / 回声洞 / 反馈走
/// <see cref="ShellConfig.OrderedSubmitEndpoints"/>，投稿页与 OSS 直传走
/// <see cref="Services.SubmitEndpoint.Ordered"/>。两边**各抄一份**的时候就出现过「改一处忘一处」
/// 的隐患。现在两个入口都指向这里，就不会再漂。
/// </para>
/// <para>
/// 顺序即优先级：按顺序试，第一个拿到业务 JSON 响应的就停手，并把成功的那个记进本机文件
/// （<see cref="ShellConfig.SubmitEndpointFile"/>），下次直接先试它。
/// </para>
/// </summary>
public static class ServiceEndpoints
{
    /// <summary>
    /// 入口按顺序试。
    ///
    /// <para>
    /// 排序依据（自上游 1.8 移植时的实测核验）：
    /// <c>api.classsoftwarehub.cn</c> 是三条里**唯一确认能通的**，必须排第一。
    /// </para>
    ///
    /// <para>
    /// 另两条：<c>submit.132614.xyz</c> 直连 Cloudflare，部分网络环境 TLS 直接被重置；
    /// <c>cshapi.132614.xyz</c> 走第三方中转，中转订单到期时整条域名返回 512。
    /// 两条都是**快速失败**（0.2~0.4 秒），排在后面代价很小，换地区时它们仍是主力。
    /// </para>
    ///
    /// <para>
    /// 顺序不是唯一防线：每轮都会按「上次成功优先」重排（见 <c>ShellConfig.OrderedSubmitEndpoints</c>），
    /// 一条入口只要成功过一次，之后的轮询就直接先试它。
    /// </para>
    /// </summary>
    public static readonly string[] All =
    {
        "https://api.classsoftwarehub.cn",
        "https://submit.132614.xyz",
        "https://cshapi.132614.xyz",
    };
}
