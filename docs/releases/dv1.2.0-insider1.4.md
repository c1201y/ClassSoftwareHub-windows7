![ClassSoftwareHub dv1.2.0-insider1.4 预览版](https://github.com/c1201y/ClassSoftwareHub-windows7/releases/download/dv1.2.0-insider1.4/dv1.2.png)

# ClassSoftwareHub 桌面版（Windows 7）dv1.2.0-insider1.4 预览版发布

前言
--

* 本版为 **dv1.2.0 预览线第三个版本**：对齐上游桌面版 dv1.2.0-insider1.4，并修复本移植版在导航与触摸滚动上暴露的问题。

* 预览版面向 insider 通道用户；若你正在使用 dv1.2.0-insider1.3 或更早版本，建议升级。

更新日志
----

* 【新增】**首页更新横幅**（自上游 1.4）：有待安装的更新时，「查看更新内容」卡片整体变为绿色成功色横幅，图标换为对勾，与普通入口卡一眼区分。

* 【更新】**法律条款升级至 v1.2**（自上游 1.4）：用户协议、隐私政策、免责声明三份文本全面更新，覆盖客户端破坏性操作（卸载 / 清理 / 关机）、下载加速节点防盗链与限速条款、投稿与内容规范等。

* 【优化】设置页「检查更新」的更新说明改为 **Markdown 渲染**，不再把 `#`、`![..](..)` 等符号原样显示（自上游 1.4）；同时修正该卡片描述过长导致标题竖排的问题。

* 【优化】侧边布局页「展开面板高度」滑块不再把存档写成 45%（XAML 解析期的事件回写问题，自上游 1.4）。

* 【修复】点击导航栏项目导致**程序崩溃**（0xc0000005）：原因为实验性功能分组高亮时清空 `SelectedItem` 触发 FluentAvalonia 选中逻辑异常，现已改为仅重置选中态并加入重入保护。

* 【修复】**触摸滚动**：Avalonia 内置滚动手势与滚动位置双向绑定在部分状态下不收敛，导致进程栈溢出（0xC00000FD）。改为由本程序自行处理触摸平移，带位置夹取与惯性滑动；滚轮、滚动条、键盘翻页不受影响。

* 【优化】触摸滑动的滚动条现在与内容同步移动，不再等抬手后才跳变。

## 其他内容

* 网站链接：[电教委员常用软件下载站](https://classsoftwarehub.cn/#/home)

* 投喂作者：[爱发电 · 连接创作者与粉丝的会员制平台](https://afdian.com/a/cyan1201)

* Github 主页：[c1201y/ClassSoftwareHub-windows7](https://github.com/c1201y/ClassSoftwareHub-windows7)

* Github 更新文章：[Releases · c1201y/ClassSoftwareHub-windows7](https://github.com/c1201y/ClassSoftwareHub-windows7/releases)

* 此项目由人类构建。

* 感谢热心群友@凭舟吟通过服务器及自建下载节点！
