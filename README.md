# ClassSoftwareHub · Windows 7 移植版

「电教委员常用软件下载站」的桌面版 —— **Windows 7 移植版**（代号 `csh-win7`）。

- 网站：[classsoftwarehub.us.ci](https://classsoftwarehub.us.ci)
- 原版（WinUI 3 / Windows 10+）：[c1201y/ClassSoftwareHub-Desktop](https://github.com/c1201y/ClassSoftwareHub-Desktop)

## 为什么有这个仓库

原版用 WinUI 3，系统底线是 **Windows 10 1809** —— 教室里的老机器（Windows 7 SP1）装不上。
本仓库用 **Avalonia 11.2.8 + .NET 6** 重做了 UI 层，把系统底线降到 **Windows 7 SP1**，
布局、间距、圆角、颜色、图标、文案都与原版 1:1 对齐（对齐要求见 `src-win7/PORTING.md`）。

两版**各自独立发布、各自独立更新** —— 本仓库就是 Win7 版的更新源（客户端只读这里的 Release）。

## 技术选型（锁死，别升）

| 组件 | 版本 | 为什么不能动 |
| --- | --- | --- |
| .NET | **6.0** | 最后一个官方支持 Windows 7 的 .NET 版本 |
| Avalonia | **11.2.8** | 支持 net6.0；12.x 的 baseline 更高，Win7/D3D11 上跑不了 |
| FluentAvaloniaUI | **2.4.1** | 支持 net6.0；2.5+ 依赖 net10 |
| WebView2 SDK | **≤ 1.0.1587** | Win7 上 WebView2 运行时止于 109.0.1518.x |

## 目录

```
src-win7\            移植版全部源码（Avalonia 工程）
  Launcher\          便携版启动器（就是产物根目录那一个 exe）
Assets\              构建需要的图片 / 图标资源
Web\                 站点外壳脚本
installer\           Inno Setup 安装脚本
tools\               打包与发版脚本
.github\workflows\   构建 / 发版工作流
```

## 编译 / 打包 / 发版

见 [BUILDING.md](BUILDING.md)。

## 许可

与原版一致：GPL-3.0。
