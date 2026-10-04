# Windows 7 移植版 · 移植契约（所有 agent 必读）

> 本文件是 `src/` 移植工作的**唯一技术依据**。动手前先从头读完。  
> 契约里写死的 API 名称都是**实测核验过**的（用反射探针 dump 过 Avalonia 11.2.8 与  
> FluentAvaloniaUI 2.4.1 的真实导出成员），不是猜的。凡本文件没提到的 API，  
> 动手前先自己核验，**不要凭印象写 WinUI 的成员名**。

---

## 1. 任务本质

把 `C:\csh-win7\` 根目录下的 **WinUI 3 原版**（net10 + WindowsAppSDK 2.5）**逐文件等价移植**到  
`C:\csh-win7\src\`（**net6.0-windows7.0 + Avalonia 11.2.8 + FluentAvaloniaUI 2.4.1**）。

**两条铁律：**

1. **语义等价，不是重新设计。** 你是在**翻译**已有代码，不是重写功能。  
   原文件的方法、字段、注释（中文长注释尤其）、边界处理、时序、容错分支，**尽量原样保留**。  
   原代码里那些「为什么这么写」的注释是宝贵的，**不要删、不要精简**——只把其中的  
   WinUI/WinRT 专有名词改成对应的 Avalonia 说法。
2. **UI 一模一样。** 布局结构、控件层级、间距、尺寸、圆角、颜色、图标码位、文案，全部照搬。  
   见到 `Glyph="&#xE719;"` 就**原样抄**（图标字体已把 MDL2 码位重映射好了，不用改码位）。

**允许的偏离**：只有当 WinUI API 在 Avalonia 里**确实不存在**时，才按第 5 节的替代方案改写。

---

## 2. 目录与归属

```
C:\csh-win7\
├── (WinUI 原版权威参照，只读！)
│   ├── App.xaml / App.xaml.cs
│   ├── MainWindow.xaml / .cs
│   ├── Core/ Data/ Services/ Pages/ Views/ Web/ Assets/
│   └── ClassSoftwareHub.Desktop.csproj
└── src/                     ← 你唯一可以写的地方
    ├── ClassSoftwareHub.Win7.csproj
    ├── App.axaml(.cs)  Program.cs  MainWindow.axaml(.cs)
    ├── app.manifest
    ├── Assets/Fonts/CshSymbols.ttf      ← 图标字体（勿动）
    ├── Platform/                        ← 平台抽象层（已就绪，勿改）
    ├── Core/  Data/  Services/  Pages/  Views/  Web/
```

> ⛔ **绝对不要修改 `src\` 以外的任何文件。** 根目录的 WinUI 原版是 1:1 对照基准。  
> ⛔ **不要改 `Platform/` 下已有文件**（`NativeMethods.cs` / `OsInfo.cs` / `Backdrop.cs` /  
> `ThemeCompat.cs` / `PageBase.cs`）。缺什么就在 `Platform/` 下**新增**文件，并在总结里说明。  
> ⛔ **只写你被分配的文件**，不要动别的 agent 负责的文件（见各自的任务说明）。

命名空间统一用 **`ClassSoftwareHub.Desktop.*`**（与 WinUI 原版一致），  
即 `src/Core/ShellConfig.cs` 里写 `namespace ClassSoftwareHub.Desktop.Core;`。  
**程序集名是 `ClassSoftwareHub`**（`avares://ClassSoftwareHub/...` 里的就是它）。

---

## 3. 编译与自检

```bash
export DOTNET_ROOT="C:\\Users\\Administrator\\.dotnet6"
DOTNET=/c/Users/Administrator/.dotnet6/dotnet.exe
cd /c/csh-win7/src
$DOTNET build ClassSoftwareHub.Win7.csproj -c Debug
```

- **你每写完一批文件就必须跑一次编译**，修到自己的文件零错误再收工。
- 别人的文件可能还没写完导致报错 —— **只修你自己文件报出来的错**，别去改别人的。
- 如果你的文件因为「别人还没写」而报「找不到类型」，说明你依赖的契约没就位：  
  那就按本文件约定的签名**照常写**，在总结里标注你依赖了谁。

---

## 4. 核心 API 替换表（**已实测核验**）

### 4.1 命名空间与根类型

| WinUI 原版                                   | Avalonia 移植版                                                                                                                                                                |
| ------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Microsoft.UI.Xaml.Controls.Page`          | `Platform.PageBase`（继承 `Avalonia.Controls.UserControl`，重写 `OnNavigatedTo(object?)` / `OnNavigatedFrom()`）                                                                   |
| `Microsoft.UI.Xaml.Window`                 | `Avalonia.Controls.Window`                                                                                                                                                  |
| `Microsoft.UI.Xaml.Controls.Frame`         | `FluentAvalonia.UI.Controls.Frame`（**同签名**：`Navigate(Type)` / `Navigate(Type, object)` / `BackStack` / `CanGoBack` / `GoBack()` / `Navigated` / `Navigating` / `CacheSize`） |
| `Microsoft.UI.Xaml.Application`            | `Avalonia.Application`                                                                                                                                                      |
| `Microsoft.UI.Xaml.Controls.UserControl`   | `Avalonia.Controls.UserControl`                                                                                                                                             |
| `Microsoft.UI.Xaml.Controls.ContentDialog` | `FluentAvalonia.UI.Controls.ContentDialog`（`await dialog.ShowAsync()`）                                                                                                      |

### 4.2 控件

| WinUI                                                                                      | Avalonia / FluentAvalonia                                                                                                                                              | 备注                                                                                                                                                                                                                       |
| ------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `NavigationView`                                                                           | `FluentAvalonia.UI.Controls.NavigationView`                                                                                                                            | 属性同名：`MenuItems` `FooterMenuItems` `OpenPaneLength` `PaneDisplayMode` `IsBackButtonVisible` `IsSettingsVisible` `CompactModeThresholdWidth` `ExpandedModeThresholdWidth` `SelectedItem`，事件 `ItemInvoked` `BackRequested` |
| `NavigationViewItem`                                                                       | `FluentAvalonia.UI.Controls.NavigationViewItem`                                                                                                                        | ⚠️ **图标属性叫 `IconSource`，不叫 `Icon`**（见 4.5）                                                                                                                                                                               |
| `FontIcon`                                                                                 | `FluentAvalonia.UI.Controls.FontIcon`                                                                                                                                  | 成员 `Glyph` `FontSize` `FontFamily` `Foreground` —— 与 WinUI 同名                                                                                                                                                            |
| `SymbolIcon`                                                                               | `FluentAvalonia.UI.Controls.SymbolIcon`                                                                                                                                | 成员 `Symbol` `FontSize`                                                                                                                                                                                                   |
| `InfoBar`                                                                                  | `FluentAvalonia.UI.Controls.InfoBar`                                                                                                                                   | 成员 `Title` `Message` `Severity` `IsOpen` `IsClosable` `ActionButton` `IconSource`                                                                                                                                        |
| `NumberBox`                                                                                | `FluentAvalonia.UI.Controls.NumberBox`                                                                                                                                 | 成员 `Value` `Minimum` `Maximum` `SmallChange` `LargeChange` `Header` `PlaceholderText` `SpinButtonPlacementMode` `ValidationMode` `AcceptsExpression`，事件 `ValueChanged`                                                   |
| `ProgressRing`                                                                             | `FluentAvalonia.UI.Controls.ProgressRing`                                                                                                                              | 成员 `IsActive` `IsIndeterminate` `Value`                                                                                                                                                                                  |
| `ProgressBar`                                                                              | `Avalonia.Controls.ProgressBar`                                                                                                                                        |                                                                                                                                                                                                                          |
| `InfoBadge`                                                                                | `FluentAvalonia.UI.Controls.InfoBadge`                                                                                                                                 | 成员 `Value` `IconSource`；通过 `NavigationViewItem.InfoBadge` 挂载                                                                                                                                                             |
| `Expander`                                                                                 | `Avalonia.Controls.Expander`                                                                                                                                           |                                                                                                                                                                                                                          |
| `SettingsCard`（CommunityToolkit）                                                           | ⚠️ **FA 没有**（只有 `SettingsExpander`）。写一个 `src/Views/SettingsCard.axaml(.cs)` 自建（`HeaderedContentControl` 风格：Header/Description/Icon + 右侧内容），**全体共用同一个**，不要各写各的     |                                                                                                                                                                                                                          |
| `SettingsExpander`                                                                         | `FluentAvalonia.UI.Controls.SettingsExpander`                                                                                                                          | 成员 `Header` `Description` `IconSource` `IsExpanded` `Items` `Footer` `ActionIconSource`                                                                                                                                  |
| `VariableSizedWrapGrid` / `ItemsWrapGrid`                                                  | `Avalonia.Controls.WrapPanel`                                                                                                                                          | Avalonia **没有** VariableSizedWrapGrid                                                                                                                                                                                    |
| `RelativePanel`                                                                            | `Avalonia.Controls.Grid`                                                                                                                                               | Avalonia **没有** RelativePanel，用 Grid 行列还原                                                                                                                                                                                |
| `ItemsRepeater`                                                                            | `Avalonia.Controls.ItemsControl` + `ItemsPanel`                                                                                                                        | Avalonia 未引用 ItemsRepeater 包                                                                                                                                                                                             |
| `GridView` / `ListView`                                                                    | `Avalonia.Controls.ListBox`（配 `ItemsPanel` 改横向/换行）                                                                                                                     |                                                                                                                                                                                                                          |
| `MenuFlyout` / `MenuFlyoutItem`                                                            | `FluentAvalonia.UI.Controls.FAMenuFlyout` / `Avalonia.Controls.MenuFlyout`                                                                                             |                                                                                                                                                                                                                          |
| `TeachingTip`                                                                              | `FluentAvalonia.UI.Controls.TeachingTip`                                                                                                                               |                                                                                                                                                                                                                          |
| `SplitView`                                                                                | `Avalonia.Controls.SplitView`                                                                                                                                          |                                                                                                                                                                                                                          |
| `ToggleSwitch` / `CheckBox` / `RadioButton` / `Slider` / `TextBox` / `ComboBox` / `Button` | `Avalonia.Controls.*` **同名**                                                                                                                                           |                                                                                                                                                                                                                          |
| `ScrollViewer`                                                                             | `Avalonia.Controls.ScrollViewer`                                                                                                                                       | 同 `HorizontalScrollBarVisibility` 等                                                                                                                                                                                      |
| `ToolTipService.ToolTip`                                                                   | `ToolTip.Tip`（附加属性）                                                                                                                                                    |                                                                                                                                                                                                                          |
| `FontIconSource` / `SymbolIconSource`                                                      | `FluentAvalonia.UI.Controls.FontIconSource` / `SymbolIconSource`                                                                                                       | 用于 `IconSource` 类属性                                                                                                                                                                                                      |
| `Window.SystemBackdrop`                                                                    | `Platform.Backdrop.Apply(win, root, prefer)`                                                                                                                           | 见 6.2                                                                                                                                                                                                                    |
| `Window.ExtendsContentIntoTitleBar` + `SetTitleBar`                                        | ⚠️ Avalonia 用 `ExtendClientAreaToDecorationsHint` + `ExtendClientAreaChromeHints` / `ExtendClientAreaTitleBarHeightHint`。**Win7 上更稳的做法是保留系统边框**，具体见 `MainWindow` 任务的说明 |                                                                                                                                                                                                                          |

### 4.3 类型 / 基础设施

| WinUI / WinRT                                                                | Avalonia 移植版                                                                                                                                                                       |
| ---------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Microsoft.UI.Dispatching.DispatcherQueue`                                   | `Avalonia.Threading.Dispatcher.UIThread`                                                                                                                                           |
| `queue.TryEnqueue(() => …)`                                                  | `Dispatcher.UIThread.Post(() => …)`                                                                                                                                                |
| `queue.TryEnqueue(DispatcherPriority.X, …)`                                  | `Dispatcher.UIThread.Post(action, DispatcherPriority.X)`                                                                                                                           |
| `DispatcherQueue.GetForCurrentThread()`                                      | `Dispatcher.UIThread`                                                                                                                                                              |
| `queue.HasThreadAccess`                                                      | `Dispatcher.UIThread.CheckAccess()`                                                                                                                                                |
| `Windows.UI.Color.FromArgb(a,r,g,b)`                                         | `Avalonia.Media.Color.FromArgb(a,r,g,b)`                                                                                                                                           |
| `Windows.UI.Colors.X`                                                        | `Avalonia.Media.Colors.X`                                                                                                                                                          |
| `Microsoft.UI.Xaml.Media.SolidColorBrush`                                    | `Avalonia.Media.SolidColorBrush`                                                                                                                                                   |
| `Microsoft.UI.Xaml.Media.Imaging.BitmapImage`                                | `Avalonia.Media.Imaging.Bitmap`（`new Bitmap(stream)`）                                                                                                                              |
| `Windows.Foundation.Rect` / `Size` / `Point`                                 | `Avalonia.Rect` / `Avalonia.Size` / `Avalonia.Point`                                                                                                                               |
| `Windows.Storage.Streams.IRandomAccessStream`                                | `System.IO.Stream`                                                                                                                                                                 |
| `Windows.Storage.ApplicationData.Current.LocalFolder`                        | `Core.AppPaths.DataDir`（已在 `Core/ShellConfig.cs`）                                                                                                                                  |
| `Windows.Storage.Pickers.FileOpenPicker` / `FileSavePicker` / `FolderPicker` | `Avalonia.Platform.Storage.IStorageProvider`（从 `TopLevel.GetTopLevel(control).StorageProvider` 取）。原版 `PickSingleFileAsync` → `OpenFilePickerAsync(new FilePickerOpenOptions{...})` |
| `Windows.ApplicationModel.DataTransfer.Clipboard` / `DataPackage`            | `TopLevel.GetTopLevel(control).Clipboard`（`IClipboard`：`SetTextAsync` / `GetTextAsync`）                                                                                            |
| `Microsoft.UI.Xaml.Window.AppWindow`                                         | Avalonia `Window` 本身（`Position`/`ClientSize`/`WindowState`）+ `Platform.NativeMethods` 里的 Win32 调用                                                                                  |
| `Windows.System.Launcher.LaunchUriAsync`                                     | `TopLevel.GetTopLevel(control).Launcher.LaunchUriAsync(uri)`                                                                                                                       |
| `ThemeResource`（XAML）                                                        | `{DynamicResource ...}`                                                                                                                                                            |
| `StaticResource`（XAML）                                                       | `{StaticResource ...}`（同名，可直接用）                                                                                                                                                    |
| `x:Bind`                                                                     | `{Binding}`（本项目**未启用**编译期绑定，反射绑定即可）                                                                                                                                                |
| `ElementTheme`                                                               | `Avalonia.Styling.ThemeVariant`（`Light`/`Dark`/`Default`）                                                                                                                          |
| `FrameworkElement.RequestedTheme`                                            | `StyledElement.RequestedThemeVariant`                                                                                                                                              |
| `Grid.ColumnSpacing` / `RowSpacing`                                          | ⛔ **Avalonia 的 Grid 没有这两个属性** → 用子元素 `Margin` 模拟，间距值照搬原版                                                                                                                           |

### 4.4 资源字典键（FluentAvalonia 提供）

`SymbolThemeFontFamily`（图标字体，已在 `App.axaml` 指向内嵌的 `CshSymbols`）、  
`SolidBackgroundFillColorBaseBrush`、`CardStrokeColorDefaultBrush`、  
`SystemFillColorCautionBrush`、`AccentButtonStyle`、`SubtitleTextBlockStyle` 等  
WinUI 同名的主题资源 **FluentAvalonia 大多有**。用到时先核验键名是否存在  
（可 grep `C:\Users\Administrator\.nuget\packages\fluentavaloniaui\2.4.1\lib\net6.0\FluentAvalonia.xml`），  
不存在就改用最接近的键，并在注释里写明替换原因。

### 4.5 图标（重要）

原版 XAML/C# 里的 `Glyph="&#xE719;"` 与 `"\uE708"` 这类 **Segoe MDL2 Assets 码位，  
原样照抄，一个字符都不要改**。图标字体已把 91 个用到的码位重映射到内嵌字体  
`Assets/Fonts/CshSymbols.ttf`（族名 `CshSymbols`，已挂在 `SymbolThemeFontFamily`）。

- `FontIcon` 不用指定 `FontFamily`，它会自动走 `SymbolThemeFontFamily`。
- ⚠️ 若某个码位渲染出来是空白/方块，**不要自己换码位**，在总结里列出来即可（已知有 3 个  
  低置信度映射：`U+E840`、`U+EEA1`、`U+EEA3`）。

---

## 5. 平台抽象层（`Platform/`，已就绪，直接用）

```csharp
using ClassSoftwareHub.Desktop.Platform;
```

| 类型                        | 用途                                                                                                                                                                                                                       |
| ------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `OsInfo`                  | 版本判定。`IsWindows7` / `IsWindows10OrLater` / `IsWindows11` / `SupportsMica` / `SupportsAcrylicBlur` / `SupportsDarkTitleBar` / `SupportsWindowRounding` / `IsDwmCompositionEnabled` / `Describe()`。**所有「Win10 才有」的判定都走这里** |
| `NativeMethods`           | 全部 P/Invoke（user32 / gdi32 / dwmapi / shell32 / ntdll）。**禁止在业务代码里另写 DllImport**，需要新 API 就加到 `NativeMethods.cs`（先读它，很多已存在）                                                                                                |
| `Backdrop`                | `Backdrop.Apply(win, root, prefer)` → 返回实际生效的 `"mica"/"acrylic"/"blur"/"solid"`。等价于原版 `Services/BackdropHost.cs`                                                                                                         |
| `ThemeCompat`             | `ThemeCompat.Apply(root)` / `ThemeCompat.Notify()` / `ThemeCompat.Map(str)`。等价于原版 `Services/ThemeHost.cs`                                                                                                                |
| `PageBase` / `Navigation` | 见 4.1；`Navigation.Attach(frame)` 必须在首次 `Navigate` 前调用                                                                                                                                                                    |

### 5.1 原版服务的等价物

| 原版                                          | 移植版                                                               |
| ------------------------------------------- | ----------------------------------------------------------------- |
| `Services/BackdropHost.cs`                  | `Platform.Backdrop`（**已实现，不要再写一份**）                               |
| `Services/ThemeHost.cs`                     | `Platform.ThemeCompat`（**已实现**）                                   |
| `Services/ScreenCapture.cs` 的 `Log(string)` | 由「服务层」agent 移植，全局日志入口，**不要另起炉灶**                                  |
| `Core/WindowChrome.cs`                      | 移植成 `src/Core/WindowChrome.cs`，内部改用 `Platform.NativeMethods` |



---

## 6. 已知硬约束（Win7 特有，必须遵守）


1. **Win7 没有 Mica/Acrylic** → 一律走 `Platform.Backdrop`，它会自动分级降级到 Aero 毛玻璃/纯色。
2. **Win7 没有 Segoe MDL2 Assets / Segoe Fluent Icons** → 已用内嵌字体解决，勿动。
3. **Win7 没有 `Windows.Graphics.Capture`** → 截屏必须走 GDI `BitBlt`  
   （`NativeMethods` 里 `CreateCompatibleDC`/`CreateCompatibleBitmap`/`BitBlt` 已备好）。
4. **Win7 上 WebView2 运行时止于 109.0.1518.x** → csproj 已锁 SDK `1.0.1587.40`。  
   用到 WebView2 的地方（仅「应用内网页浮层 WebSheet」）要**保留**，并在初始化失败时  
   提供「用浏览器打开」的降级路径（原版已有这套逻辑，照搬）。
5. **不要用 `Environment.OSVersion` 判版本** → 用 `OsInfo`。
6. **C# 语言版本锁 10**（.NET 6 SDK 上限）：不能用 C# 11/12 的 `required`、原始字符串、列表模式、  
   集合表达式、主构造函数。原版没有用这些，移植时也别引入。
7. **`Microsoft.Win32.Registry`** 在 net6.0-windows 上可用，注册表相关代码可直译。

---

## 7. 交付要求

每个 agent 收工时用**中文**总结，必须包含：

1. 你移植了哪些文件（原路径 → 新路径），各多少行；
2. 编译结果（你**自己文件**的错误是否清零）；
3. **所有偏离原版的地方**，逐条说明「原写法 → 新写法 → 为什么」；
4. **没做完 / 不敢确定 / 需要主控决策**的事项，直说，**不要粉饰**；
5. 你依赖了哪些别人负责的文件（如果它们还没就绪）。

> 不确定的地方宁可**保留原语义 + 加 `// TODO(win7):` 注释**，也要把结构写完整，  
> 不要为了「能编译」把功能阉割掉。
