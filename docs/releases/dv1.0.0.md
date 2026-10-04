![ClassSoftwareHub dv1.0.0 正式版](https://github.com/c1201y/ClassSoftwareHub-windows7/releases/download/dv1.0.0/dv1.1.png)

# ClassSoftwareHub 桌面版（Windows 7）dv1.0.0 正式版发布

前言
--

* 这是 Windows 7 移植版的**首个正式版**：把桌面版带进了教室里的老机器（Windows 7 SP1），界面与交互继续与原版 1:1 对齐。
* 从这一版起，Win7 版**独立成库**：更新检查与更新日志只读本仓库的 Release，不再和 WinUI 版（ClassSoftwareHub-Desktop）的发布混在一起。

更新日志
----

* 【新增】全新的 **Windows 7 移植版外壳**（Avalonia 11.2.8 + .NET 6），把系统底线从 Windows 10 1809 降到 **Windows 7 SP1**；布局、间距、圆角、颜色、图标、文案与原版逐一对齐。

* 【新增】**工具侧边栏**：可以把常用工具自己拼进侧边栏、排顺序、拖出去删掉；四条边都能贴，点击展开 / 收起，鼠标和手指都拖得动。

* 【新增】**截屏贴图**：拖一块屏幕区域就出编辑窗 —— 画笔 / 荧光笔 / 箭头 / 矩形 / 椭圆 / 文字 / 马赛克，能撤销重做，然后复制到剪贴板、存到本地，或者钉在屏幕上。

* 【新增】**常用工具集**：随机摇号、课表计时、秒表计时、全屏时钟、编码 / 哈希、图片取色、系统镜像下载等，开箱即用。

* 【新增】**外观分体**：主界面和外部小窗（侧边栏 / 常用工具浮窗 / 截屏编辑窗）可以各用一套浅色或深色。

* 【新增】**双通道自动更新**与**站内更新日志**：正式版 / 预览版两条通道，更新器负责核对版本、下载并校验安装包。

* 【优化】首页下载改为**镜像直链优先，直链失效自动回退 GitHub Release**，下载更稳。

* 【优化】精简应用图标体积，明显加快首屏与列表加载；补齐部分页面的过渡与交互动画。

* 【修复】主窗口关闭 / 最小化 / 最大化按钮位置冒出的**系统原生小按钮**（命中测试未截断导致）。

* 【修复】常用工具窗口**偶发黑边**、拖动窗口时的**残影**、更新日志页加载时的**布局错乱**。

* 【变更】更新通道与更新日志改指向**本独立仓库**（`ClassSoftwareHub-windows7`），tag 前缀随之清空（`dv1.0.0` 形状）。

## 其他内容

* 网站链接：[电教委员常用软件下载站](https://classsoftwarehub.us.ci/#/home)

* 投喂作者：[爱发电 · 连接创作者与粉丝的会员制平台](https://ifdian.net/a/TinyNickCSHub)

* Github 主页：[c1201y/ClassSoftwareHub-windows7](https://github.com/c1201y/ClassSoftwareHub-windows7)

* Github 更新文章：[Releases · c1201y/ClassSoftwareHub-windows7](https://github.com/c1201y/ClassSoftwareHub-windows7/releases)

* 此项目由人类构建。
