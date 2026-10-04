# CshSymbols 图标字体对照表

把 **Segoe MDL2 Assets** 的码位重新映射到 MIT 许可的 **Fluent System Icons** 字形上，
使原 WinUI 工程 XAML/C# 里的 `Glyph="&#xXXXX;"`、`"\uXXXX"` 一个字都不用改即可正确显示。

## 1. 来源与许可

- **源字体**：`FluentSystemIcons-Regular.ttf`，来自 GitHub 仓库 `microsoft/fluentui-system-icons`（**MIT License**）。
- **许可**：MIT，可自由使用 / 修改 / 再分发（保留版权声明）。字体内部已写入 nameID 13/14 的 MIT 许可说明与链接。
- **生成方式**：`fonttools` 读取源字体，按下表把选定图标的字形绑定到 MDL2 码位上（原有 Fluent 码位保留，仅覆盖 E700–EF00 区间）。
- 原 Segoe MDL2 Assets / Segoe Fluent Icons 字体**未使用、未再分发**（均为微软专有字体，不可再分发）。

### 下载入口（按顺序尝试）

| 入口 | 结果 |
| --- | --- |
| `raw.githubusercontent.com` | 失败（schannel 吊销检查不可用） |
| `cdn.jsdelivr.net` | 失败（HTTP 502） |
| `fastly.jsdelivr.net` | 失败（HTTP 502） |
| **`gh-proxy.com`**（转发到 raw.githubusercontent.com） | **成功（实际使用）** |

## 2. 字体信息

- 文件：`src-win7/Assets/Fonts/CshSymbols.ttf`（约 2.7 MB）
- 字体族名 family name（nameID 1/4/6/16）：`CshSymbols`；子族 `Regular`
- 码表内含 91 个本工程用到的 MDL2 码位（另有源字体自带的近万个 Fluent 码位）

## 3. 在 Avalonia 中引用

程序集名 `ClassSoftwareHub`（`ClassSoftwareHub.Win7.csproj` 的 `<AssemblyName>`），字体随 `<AvaloniaResource Include="Assets\**" ...>` 自动打包：

```xml
avares://ClassSoftwareHub/Assets/Fonts/CshSymbols.ttf#CshSymbols
```

在 `App.axaml` 中把符号字体重定向到它（FluentAvalonia 的 FontIcon / SymbolIcon 会读取该资源键）：

```xml
<Application.Resources>
  <FontFamily x:Key="SymbolThemeFontFamily">avares://ClassSoftwareHub/Assets/Fonts/CshSymbols.ttf#CshSymbols</FontFamily>
</Application.Resources>
```

## 4. 对照表

| MDL2 码位 | MDL2 名称 | 选用的 Fluent 图标 | 置信度 | 备注 |
| --- | --- | --- | :-: | --- |
| U+E700 | GlobalNavigationButton | `ic_fluent_navigation_24_regular` | 高 | hamburger 菜单 |
| U+E706 | Brightness | `ic_fluent_brightness_high_24_regular` | 高 | 太阳/亮度 |
| U+E708 | QuietHours | `ic_fluent_weather_moon_24_regular` | 高 | 月亮/深色 |
| U+E70D | ChevronDown | `ic_fluent_chevron_down_24_regular` | 高 |  |
| U+E70E | ChevronUp | `ic_fluent_chevron_up_24_regular` | 高 |  |
| U+E710 | Add | `ic_fluent_add_24_regular` | 高 |  |
| U+E711 | Cancel | `ic_fluent_dismiss_24_regular` | 高 |  |
| U+E713 | Settings | `ic_fluent_settings_24_regular` | 高 |  |
| U+E714 | Video | `ic_fluent_video_24_regular` | 高 |  |
| U+E716 | People | `ic_fluent_people_24_regular` | 高 |  |
| U+E718 | Pin | `ic_fluent_pin_24_regular` | 高 |  |
| U+E719 | Shop | `ic_fluent_shopping_bag_24_regular` | 高 |  |
| U+E71D | AllApps | `ic_fluent_apps_list_24_regular` | 中 | MDL2 为项目符号列表，Fluent apps_list 亦为列表，形近 |
| U+E71E | Zoom | `ic_fluent_zoom_in_24_regular` | 高 |  |
| U+E722 | Camera | `ic_fluent_camera_24_regular` | 高 |  |
| U+E72A | Forward | `ic_fluent_arrow_forward_24_regular` | 高 |  |
| U+E72C | Refresh | `ic_fluent_arrow_clockwise_24_regular` | 高 |  |
| U+E734 | FavoriteStar | `ic_fluent_star_24_regular` | 高 |  |
| U+E739 | Checkbox | `ic_fluent_checkbox_unchecked_24_regular` | 高 |  |
| U+E73E | CheckMark | `ic_fluent_checkmark_24_regular` | 高 |  |
| U+E740 | FullScreen | `ic_fluent_full_screen_maximize_24_regular` | 高 |  |
| U+E74D | Delete | `ic_fluent_delete_24_regular` | 高 |  |
| U+E74E | Save | `ic_fluent_save_24_regular` | 高 |  |
| U+E74F | Mute | `ic_fluent_speaker_mute_24_regular` | 高 |  |
| U+E765 | KeyboardClassic | `ic_fluent_keyboard_24_regular` | 高 |  |
| U+E767 | Volume | `ic_fluent_speaker_2_24_regular` | 高 |  |
| U+E76B | ChevronLeft | `ic_fluent_chevron_left_24_regular` | 高 |  |
| U+E76C | ChevronRight | `ic_fluent_chevron_right_24_regular` | 高 |  |
| U+E770 | System | `ic_fluent_laptop_24_regular` | 高 | 系统(笔记本) |
| U+E772 | Devices | `ic_fluent_desktop_tower_24_regular` | 中 | 显示器+主机(近似) |
| U+E774 | Globe | `ic_fluent_globe_24_regular` | 高 |  |
| U+E777 | UpdateRestore | `ic_fluent_arrow_counterclockwise_24_regular` | 中 | MDL2 为逆时针回环箭头，Fluent 取逆时针箭头 |
| U+E77B | Contact | `ic_fluent_person_24_regular` | 中 | MDL2 为人像卡片，Fluent 用人像 |
| U+E783 | Error | `ic_fluent_error_circle_24_regular` | 高 |  |
| U+E787 | Calendar | `ic_fluent_calendar_24_regular` | 高 |  |
| U+E790 | Color | `ic_fluent_color_24_regular` | 高 |  |
| U+E7A6 | Redo | `ic_fluent_arrow_redo_24_regular` | 高 |  |
| U+E7A7 | Undo | `ic_fluent_arrow_undo_24_regular` | 高 |  |
| U+E7A8 | Crop | `ic_fluent_crop_24_regular` | 高 |  |
| U+E7B8 | Package | `ic_fluent_box_24_regular` | 高 |  |
| U+E7BA | Warning | `ic_fluent_warning_24_regular` | 高 |  |
| U+E7C2 | Move | `ic_fluent_arrow_move_24_regular` | 高 |  |
| U+E7C4 | TaskView | `ic_fluent_window_multiple_24_regular` | 中 | MDL2 为窗口+圆点（Win+Tab），Fluent 用多窗口 |
| U+E7E7 | ActionCenterNotification | `ic_fluent_alert_24_regular` | 中 | 通知(铃铛) |
| U+E7E8 | PowerButton | `ic_fluent_power_24_regular` | 高 |  |
| U+E7F3 | SettingsDisplaySound | `ic_fluent_desktop_speaker_24_regular` | 中 | 显示器+扬声器 |
| U+E7F4 | TVMonitor | `ic_fluent_desktop_24_regular` | 高 | 显示器 |
| U+E7FC | Game | `ic_fluent_games_24_regular` | 高 |  |
| U+E80A | AllApps/TiltDown(网格) | `ic_fluent_grid_24_regular` | 高 | 9宫格(布局) |
| U+E80F | Home | `ic_fluent_home_24_regular` | 高 |  |
| U+E81C | History | `ic_fluent_history_24_regular` | 高 |  |
| U+E81E | MapLayers | `ic_fluent_layer_24_regular` | 中 | MDL2 为层叠层，Fluent 用 layer |
| U+E840 | Pinned | `ic_fluent_pin_24_regular` | 低 | 复用 Pin |
| U+E894 | Clear | `ic_fluent_dismiss_24_regular` | 中 | X(清除) |
| U+E895 | Sync | `ic_fluent_arrow_sync_24_regular` | 高 |  |
| U+E896 | Download | `ic_fluent_arrow_download_24_regular` | 高 |  |
| U+E898 | Upload | `ic_fluent_arrow_upload_24_regular` | 高 |  |
| U+E8A0 | OpenPane | `ic_fluent_panel_left_expand_24_regular` | 中 | 侧栏展开(近似) |
| U+E8A7 | OpenInNewWindow | `ic_fluent_open_24_regular` | 高 |  |
| U+E8AB | Switch | `ic_fluent_arrow_swap_24_regular` | 高 |  |
| U+E8B7 | Folder | `ic_fluent_folder_24_regular` | 高 |  |
| U+E8BB | ChromeClose | `ic_fluent_dismiss_24_regular` | 高 | 关闭 |
| U+E8BD | Message | `ic_fluent_chat_24_regular` | 高 |  |
| U+E8C8 | Copy | `ic_fluent_copy_24_regular` | 高 |  |
| U+E8D2 | Font | `ic_fluent_text_font_24_regular` | 高 |  |
| U+E8E5 | OpenFile | `ic_fluent_folder_open_24_regular` | 中 | 打开文件(近似) |
| U+E8F4 | NewFolder | `ic_fluent_folder_add_24_regular` | 高 |  |
| U+E916 | Stopwatch | `ic_fluent_timer_24_regular` | 高 |  |
| U+E921 | ChromeMinimize | `ic_fluent_subtract_24_regular` | 中 | 最小化(横线) |
| U+E92D | ChromeFullScreen | `ic_fluent_arrow_maximize_24_regular` | 中 | MDL2 为对角双向箭头（放大），Fluent 用 maximize 箭头 |
| U+E939 | FeedbackApp | `ic_fluent_person_feedback_24_regular` | 中 | MDL2 为人+对话气泡，Fluent person_feedback 同形 |
| U+E943 | Code | `ic_fluent_code_24_regular` | 高 |  |
| U+E945 | LightningBolt | `ic_fluent_flash_24_regular` | 中 | MDL2 为闪电，Fluent flash 同形 |
| U+E946 | Info | `ic_fluent_info_24_regular` | 高 |  |
| U+E9E9 | Equalizer | `ic_fluent_options_24_regular` | 中 | 滑块 |
| U+EA3A | CircleRing | `ic_fluent_circle_24_regular` | 高 |  |
| U+EA80 | Lightbulb | `ic_fluent_lightbulb_24_regular` | 高 |  |
| U+EA99 | Broom | `ic_fluent_broom_24_regular` | 高 |  |
| U+EB51 | Heart | `ic_fluent_heart_24_regular` | 高 |  |
| U+EB5E | WifiError4 | `ic_fluent_wifi_off_24_regular` | 中 | 网络错误 |
| U+EB9F | Photo2 | `ic_fluent_image_24_regular` | 高 |  |
| U+ECA5 | Tiles | `ic_fluent_dashboard_20_regular` | 中 | 马赛克(近似) |
| U+ED1A | Hide | `ic_fluent_eye_off_24_regular` | 中 | 隐藏 |
| U+ED63 | Pencil | `ic_fluent_pen_24_regular` | 高 |  |
| U+ED64 | Marker | `ic_fluent_highlight_24_regular` | 高 | 荧光笔 |
| U+EDA2 | HardDrive | `ic_fluent_hard_drive_24_regular` | 高 |  |
| U+EDA4 | Touchscreen | `ic_fluent_hand_point_24_regular` | 中 | 触摸 |
| U+EDA8 | BrushSize | `ic_fluent_line_thickness_24_regular` | 高 | 画笔粗细 |
| U+EEA0 | RAM | `ic_fluent_ram_20_regular` | 中 | 内存(20号) |
| U+EEA1 | CPU | `ic_fluent_memory_16_regular` | 低 | 处理器(近似) |
| U+EEA3 | VirtualMachineGroup | `ic_fluent_square_multiple_24_regular` | 低 | 虚拟化(近似) |

## 5. 需要人工确认

下列码位语义不能 100% 确定，或 Fluent System Icons 中没有完全等价图标、只能近似匹配。
字体中它们都有有效字形；若与原版观感有出入，请优先复核这些。

### 5.1 低置信度 —— 仅近似，务必复核

| 码位 | MDL2 名称 | 选用图标 | 原因 |
| --- | --- | --- | --- |
| U+E840 | Pinned | `pin` | 复用 Pin |
| U+EEA1 | CPU | `memory` | 处理器(近似) |
| U+EEA3 | VirtualMachineGroup | `square_multiple` | 虚拟化(近似) |

### 5.2 中置信度 —— 语义/观感存在偏差，建议抽查

| 码位 | MDL2 名称 | 选用图标 | 说明 |
| --- | --- | --- | --- |
| U+E71D | AllApps | `apps_list` | MDL2 为项目符号列表，Fluent apps_list 亦为列表，形近 |
| U+E772 | Devices | `desktop_tower` | 显示器+主机(近似) |
| U+E777 | UpdateRestore | `arrow_counterclockwise` | MDL2 为逆时针回环箭头，Fluent 取逆时针箭头 |
| U+E77B | Contact | `person` | MDL2 为人像卡片，Fluent 用人像 |
| U+E7C4 | TaskView | `window_multiple` | MDL2 为窗口+圆点（Win+Tab），Fluent 用多窗口 |
| U+E7E7 | ActionCenterNotification | `alert` | 通知(铃铛) |
| U+E7F3 | SettingsDisplaySound | `desktop_speaker` | 显示器+扬声器 |
| U+E81E | MapLayers | `layer` | MDL2 为层叠层，Fluent 用 layer |
| U+E894 | Clear | `dismiss` | X(清除) |
| U+E8A0 | OpenPane | `panel_left_expand` | 侧栏展开(近似) |
| U+E8E5 | OpenFile | `folder_open` | 打开文件(近似) |
| U+E921 | ChromeMinimize | `subtract` | 最小化(横线) |
| U+E92D | ChromeFullScreen | `arrow_maximize` | MDL2 为对角双向箭头（放大），Fluent 用 maximize 箭头 |
| U+E939 | FeedbackApp | `person_feedback` | MDL2 为人+对话气泡，Fluent person_feedback 同形 |
| U+E945 | LightningBolt | `flash` | MDL2 为闪电，Fluent flash 同形 |
| U+E9E9 | Equalizer | `options` | 滑块 |
| U+EB5E | WifiError4 | `wifi_off` | 网络错误 |
| U+ECA5 | Tiles | `dashboard` | 马赛克(近似) |
| U+ED1A | Hide | `eye_off` | 隐藏 |
| U+EDA4 | Touchscreen | `hand_point` | 触摸 |
| U+EEA0 | RAM | `ram` | 内存(20号) |

### 5.3 未被 MDL2 官方文档收录的码位（重点）

- `U+E7C2`（Move）、`U+ED1A`（Hide）：在 `segmdl2.ttf` 中存在，但微软 MDL2 文档未列名。
- `U+EEA0`（RAM）、`U+EEA1`（CPU）、`U+EEA3`（VirtualMachineGroup）：在 Windows 11 的 `segmdl2.ttf` 中**根本不存在**，仅存在于 Segoe Fluent Icons。
- 名称取自微软 Segoe Fluent Icons 官方文档。这 5 个码位没有官方 MDL2 图形可比对，映射按语义选择，属低/中置信度。

## 6. 统计

- 收集到本工程实际使用的 MDL2 码位：**91 个**
- 成功映射并写入字体：**91 个（100%）**
- 未映射：**0 个**
- 置信度：高 67 / 中 21 / 低 3