# CshSymbols 图标字体对照表

把 **Segoe MDL2 Assets** 的码位重新映射到 MIT 许可的 **Fluent System Icons** 字形上，
使原 WinUI 工程 XAML/C# 里的 `Glyph="&#xXXXX;"`、`"\uXXXX"` 一个字都不用改即可正确显示。

> ⚠️ **未映射的码位不会报错，而是落到源字体在该 PUA 码位上的任意 Fluent 字形**。
> 2026-10-29 的复选框“勾选变向上箭头”就是这么来的（源字体 U+E10B = `arrow_step_out`）。
> 新增图标码位后，**必须**照第 4 节补一条映射并重新生成字体，否则形状不可控。

## 1. 来源与许可

- **源字体**：`FluentSystemIcons-Regular.ttf`，来自 GitHub 仓库 `microsoft/fluentui-system-icons`（**MIT License**）。
- **许可**：MIT，可自由使用 / 修改 / 再分发（保留版权声明）。字体内部已写入 nameID 13/14 的 MIT 许可说明与链接。
- **生成方式**：`fonttools` 读取源字体，按下表把选定图标的字形名绑定到 MDL2 码位上（源字体原有 Fluent 码位保留）。
- 原 Segoe MDL2 Assets / Segoe Fluent Icons 字体**未使用、未再分发**（均为微软专有字体，不可再分发）。

## 2. 字体信息

- 文件：`src/Assets/Fonts/CshSymbols.ttf`（约 2.7 MB）
- 字体族名 family name（nameID 1/4/6/16）：`CshSymbols`；子族 `Regular`
- 本表收录 **118** 个应用码位 + **26** 个 FluentAvalonia 内置控件码位，全部已映射

## 3. 在 Avalonia 中引用

程序集名 `ClassSoftwareHub`（`ClassSoftwareHub.Win7.csproj` 的 `<AssemblyName>`），字体随 `<AvaloniaResource Include="Assets\**" ...>` 自动打包：

```xml
avares://ClassSoftwareHub/Assets/Fonts/CshSymbols.ttf#CshSymbols
```

在 `App.axaml` 中把符号字体重定向到它，并用全局样式强制所有 FontIcon 使用（FluentAvalonia 2.4.1 的 FontIcon 不会自动读 `SymbolThemeFontFamily`）：

```xml
<FontFamily x:Key="SymbolThemeFontFamily">avares://ClassSoftwareHub/Assets/Fonts/CshSymbols.ttf#CshSymbols</FontFamily>

<Style Selector="fa|FontIcon">
  <Setter Property="FontFamily" Value="{StaticResource SymbolThemeFontFamily}" />
</Style>
```

## 4. 如何新增 / 修改映射

```python
from fontTools.ttLib import TTFont
P = r"src/Assets/Fonts/CshSymbols.ttf"
f = TTFont(P)
assert "ic_fluent_checkmark_24_regular" in f.getGlyphOrder()
for t in f["cmap"].tables:
    if t.isUnicode():
        t.cmap[0xE10B] = "ic_fluent_checkmark_24_regular"
f.save(P)
```

可用的字形名从字体自身的 glyph order 里挑（`f.getGlyphOrder()`），不要凭记忆写。
改完在 `Assets/Fonts/icon-map.md` 补表，并把字体大小变化一起提交。

## 5. 应用码位对照表

| 码位 | 当前 Fluent 字形 | 出现位置（示例） |
| --- | --- | --- |
| U+E700 | `ic_fluent_navigation_24_regular` | Pages/HubAiPage.axaml, Views/VirtualKeyboardWindow.axaml |
| U+E706 | `ic_fluent_weather_sunny_24_regular` | Data/SidebarModule.cs, Pages/ShellPage.axaml.cs, Views/VolumeWindow.axaml.cs |
| U+E708 | `ic_fluent_weather_moon_24_regular` | Pages/ShellPage.axaml, Pages/ShellPage.axaml.cs |
| U+E70D | `ic_fluent_chevron_down_24_regular` | Pages/HubAiPage.axaml.cs, Pages/SidebarLayoutPage.axaml.cs, Views/Palette/SpinField.axaml … |
| U+E70E | `ic_fluent_chevron_up_24_regular` | Pages/SidebarLayoutPage.axaml.cs, Views/Palette/SpinField.axaml, Views/ToolSidebarWindow.axaml.cs |
| U+E710 | `ic_fluent_add_24_regular` | Pages/AutoShutdownPage.axaml.cs, Pages/FunctionPlotPage.axaml, Pages/SidebarLayoutPage.axaml.cs … |
| U+E711 | `ic_fluent_dismiss_24_regular` | MainWindow.axaml, Pages/SidebarLayoutPage.axaml.cs, Pages/SoftwarePage.axaml … |
| U+E712 | `ic_fluent_more_horizontal_24_regular` | Pages/AppDataPage.axaml.cs, Pages/UninstallPage.axaml.cs |
| U+E713 | `ic_fluent_settings_24_regular` | Controls/AppCategoryLook.cs, Data/SearchIndex.cs, Pages/ShellPage.axaml … |
| U+E714 | `ic_fluent_video_24_regular` | Pages/WelcomePage.axaml.cs |
| U+E716 | `ic_fluent_people_24_regular` | Data/SidebarModule.cs, Data/ToolDef.cs, Pages/ShellPage.axaml |
| U+E718 | `ic_fluent_pin_24_regular` | Pages/SettingsPage.axaml, Pages/SidebarLayoutPage.axaml, Views/SnipEditorWindow.axaml … |
| U+E719 | `ic_fluent_shopping_bag_24_regular` | App.axaml, Controls/AppCategoryLook.cs, Controls/AppDataLook.cs … |
| U+E71D | `ic_fluent_apps_list_24_regular` | Controls/AppCategoryLook.cs, Data/SearchIndex.cs, Pages/HubAiSettingsPage.axaml … |
| U+E71E | `ic_fluent_search_24_regular` | Data/SidebarModule.cs |
| U+E71F | `ic_fluent_zoom_out_24_regular` | Pages/FunctionPlotPage.axaml |
| U+E720 | `ic_fluent_megaphone_loud_24_regular` | Data/SearchIndex.cs, Data/ToolDef.cs, Pages/ShellPage.axaml … |
| U+E721 | `ic_fluent_search_24_regular` | Controls/LevelChipBar.cs, Pages/LogViewerPage.axaml, Pages/ShellPage.axaml … |
| U+E722 | `ic_fluent_camera_24_regular` | Data/SidebarModule.cs, Pages/SidebarLayoutPage.axaml, Views/SnipEditorWindow.axaml |
| U+E724 | `ic_fluent_send_24_regular` | Pages/FeedbackFormPage.axaml, Pages/SoftwarePage.axaml |
| U+E72A | `ic_fluent_arrow_right_24_regular` | Views/SnipEditorWindow.axaml |
| U+E72C | `ic_fluent_arrow_clockwise_24_regular` | Core/QuickLinks.cs, Pages/ChangelogPage.axaml, Pages/FunctionPlotPage.axaml … |
| U+E72E | `ic_fluent_lock_closed_24_regular` | Pages/HubAiSettingsPage.axaml, Views/ToolSidebarWindow.axaml, Views/ToolSidebarWindow.axaml.cs |
| U+E734 | `ic_fluent_star_24_regular` | Data/Feedback.cs |
| U+E739 | `ic_fluent_square_24_regular` | Views/SnipEditorWindow.axaml |
| U+E73E | `ic_fluent_checkmark_24_regular` | App.axaml, Pages/DetailPage.axaml, Pages/SidebarLayoutPage.axaml.cs … |
| U+E740 | `ic_fluent_full_screen_maximize_24_regular` | Data/SidebarModule.cs, Data/ToolDef.cs, Pages/ShellPage.axaml … |
| U+E748 | `ic_fluent_person_swap_24_regular` | Views/ToolSidebarWindow.axaml.cs |
| U+E74D | `ic_fluent_delete_24_regular` | Controls/AppDataLook.cs, Data/SearchIndex.cs, Data/SidebarModule.cs … |
| U+E74E | `ic_fluent_save_24_regular` | Views/SnipEditorWindow.axaml |
| U+E74F | `ic_fluent_speaker_mute_24_regular` | Views/VolumeWindow.axaml, Views/VolumeWindow.axaml.cs |
| U+E750 | `ic_fluent_backspace_24_regular` | Pages/Tools/EquationEditor.cs, Pages/Tools/FormulaEditor.cs |
| U+E765 | `ic_fluent_keyboard_24_regular` | Data/SidebarModule.cs, Pages/ExperimentalPage.axaml.cs, Pages/ShellPage.axaml … |
| U+E767 | `ic_fluent_speaker_2_24_regular` | Data/SidebarModule.cs, Views/VolumeMixerWindow.axaml, Views/VolumeMixerWindow.axaml.cs … |
| U+E76B | `ic_fluent_chevron_left_24_regular` | Pages/FunctionPlotPage.axaml, Pages/HubAiPage.axaml.cs, Views/ToolSidebarWindow.axaml … |
| U+E76C | `ic_fluent_chevron_right_24_regular` | Pages/FeedbackPage.axaml, Pages/FunctionPlotPage.axaml, Pages/HubAiPage.axaml.cs … |
| U+E770 | `ic_fluent_laptop_24_regular` | Pages/WelcomePage.axaml.cs |
| U+E772 | `ic_fluent_desktop_tower_24_regular` | Pages/SettingsPage.axaml |
| U+E774 | `ic_fluent_globe_24_regular` | Controls/AppDataLook.cs, MainWindow.axaml, Pages/LegalPage.axaml … |
| U+E777 | `ic_fluent_arrow_counterclockwise_24_regular` | Pages/SidebarLayoutPage.axaml, Views/ToolSidebarWindow.axaml, Views/ToolSidebarWindow.axaml.cs |
| U+E77B | `ic_fluent_person_24_regular` | Core/QuickLinks.cs, Pages/HubAiSettingsPage.axaml |
| U+E783 | `ic_fluent_error_circle_24_regular` | Controls/LevelChipBar.cs, Services/LogReader.cs |
| U+E787 | `ic_fluent_calendar_ltr_24_regular` | Data/ToolDef.cs, Pages/ExperimentalPage.axaml.cs, Pages/ShellPage.axaml … |
| U+E78C | `ic_fluent_document_arrow_down_24_regular` | Pages/SidebarLayoutPage.axaml |
| U+E790 | `ic_fluent_color_24_regular` | Data/SidebarModule.cs, Data/ToolDef.cs, Pages/SettingsPage.axaml … |
| U+E7A6 | `ic_fluent_arrow_redo_24_regular` | Views/SnipEditorWindow.axaml |
| U+E7A7 | `ic_fluent_arrow_undo_24_regular` | Views/SnipEditorWindow.axaml |
| U+E7A8 | `ic_fluent_crop_24_regular` | Views/SnipOverlayWindow.axaml |
| U+E7B8 | `ic_fluent_archive_24_regular` | Pages/DetailPage.axaml, Services/SystemRepair.cs |
| U+E7BA | `ic_fluent_warning_24_regular` | Controls/LevelChipBar.cs, Data/Feedback.cs, MainWindow.axaml … |
| U+E7C2 | `ic_fluent_arrow_move_24_regular` | Pages/VirtualKeyboardPage.axaml |
| U+E7C3 | `ic_fluent_document_24_regular` | Pages/SettingsPage.axaml |
| U+E7C4 | `ic_fluent_window_multiple_24_regular` | Data/SidebarModule.cs, Pages/SidebarLayoutPage.axaml |
| U+E7E7 | `ic_fluent_chat_24_regular` | Pages/SettingsPage.axaml |
| U+E7E8 | `ic_fluent_power_24_regular` | Data/SearchIndex.cs, Data/SidebarModule.cs, Pages/ExperimentalPage.axaml.cs … |
| U+E7EF | `ic_fluent_window_shield_24_regular` | Data/SearchIndex.cs, Services/SystemRepair.cs |
| U+E7F1 | `ic_fluent_hard_drive_24_regular` | Data/ToolDef.cs, Pages/ShellPage.axaml |
| U+E7F3 | `ic_fluent_desktop_speaker_24_regular` | Pages/WelcomePage.axaml.cs |
| U+E7F4 | `ic_fluent_desktop_24_regular` | Controls/AppDataLook.cs, Data/SearchIndex.cs, Data/SidebarModule.cs … |
| U+E7FC | `ic_fluent_games_24_regular` | Pages/WelcomePage.axaml.cs |
| U+E80A | `ic_fluent_grid_24_regular` | Data/SidebarModule.cs, Data/ToolDef.cs, Pages/ShellPage.axaml … |
| U+E80F | `ic_fluent_home_24_regular` | Data/SearchIndex.cs, Pages/ShellPage.axaml, Pages/SidebarLayoutPage.axaml … |
| U+E81C | `ic_fluent_history_24_regular` | Data/SearchIndex.cs, Data/SidebarModule.cs, Pages/ChangelogPage.axaml … |
| U+E81E | `ic_fluent_layer_24_regular` | Pages/SettingsPage.axaml |
| U+E840 | `ic_fluent_pin_24_regular` | Views/ToolSidebarWindow.axaml.cs |
| U+E894 | `ic_fluent_dismiss_24_regular` | Views/SnipEditorWindow.axaml |
| U+E895 | `ic_fluent_arrow_sync_24_regular` | Pages/SettingsPage.axaml, Services/DownloadManager.cs, Services/SystemRepair.cs |
| U+E896 | `ic_fluent_arrow_download_24_regular` | Data/SearchIndex.cs, Data/SidebarModule.cs, Data/ToolDef.cs … |
| U+E898 | `ic_fluent_arrow_upload_24_regular` | Data/SearchIndex.cs, Pages/ShellPage.axaml, Pages/WelcomePage.axaml |
| U+E8A0 | `ic_fluent_panel_left_contract_24_regular` | Data/SearchIndex.cs, Pages/ShellPage.axaml, Pages/SidebarLayoutPage.axaml … |
| U+E8A1 | `ic_fluent_text_bullet_list_square_24_regular` | Pages/SettingsPage.axaml |
| U+E8A3 | `ic_fluent_zoom_in_24_regular` | Pages/FunctionPlotPage.axaml |
| U+E8A7 | `ic_fluent_open_24_regular` | Pages/ChangelogPage.axaml, Pages/ExperimentalPage.axaml, Pages/FeedbackFormPage.axaml … |
| U+E8AB | `ic_fluent_arrow_swap_24_regular` | Pages/VirtualKeyboardPage.axaml |
| U+E8B7 | `ic_fluent_folder_24_regular` | Data/SearchIndex.cs, Pages/AppDataPage.axaml, Pages/AppDataPage.axaml.cs … |
| U+E8BB | `ic_fluent_dismiss_24_regular` | Data/SidebarModule.cs |
| U+E8BD | `ic_fluent_chat_24_regular` | Core/QuickLinks.cs, Data/SidebarModule.cs, Data/ToolDef.cs … |
| U+E8C8 | `ic_fluent_copy_24_regular` | Data/SidebarModule.cs, Pages/AutoShutdownPage.axaml.cs, Pages/HubAiPage.axaml.cs … |
| U+E8CB | `ic_fluent_arrow_sort_24_regular` | Pages/SidebarLayoutPage.axaml |
| U+E8D2 | `ic_fluent_text_font_24_regular` | Pages/VirtualKeyboardPage.axaml, Views/SnipEditorWindow.axaml |
| U+E8E5 | `ic_fluent_document_arrow_right_24_regular` | Pages/Tools/EncodingToolPage.axaml |
| U+E8EF | `ic_fluent_math_symbols_24_regular` | Data/SidebarModule.cs, Data/ToolDef.cs, Pages/ShellPage.axaml … |
| U+E8F1 | `ic_fluent_library_24_regular` | Controls/AppCategoryLook.cs, Controls/AppDataLook.cs |
| U+E8F2 | `ic_fluent_bot_24_regular` | Pages/HubAiPage.axaml, Pages/HubAiPage.axaml.cs, Pages/HubAiSettingsPage.axaml … |
| U+E8F3 | `ic_fluent_arrow_sync_24_regular` | Services/SystemRepair.cs |
| U+E8F4 | `ic_fluent_folder_add_24_regular` | Pages/SidebarLayoutPage.axaml |
| U+E90F | `ic_fluent_wrench_24_regular` | Pages/HubAiPage.axaml.cs, Services/SystemRepair.cs |
| U+E916 | `ic_fluent_timer_24_regular` | Data/SidebarModule.cs, Data/ToolDef.cs, Pages/ShellPage.axaml |
| U+E921 | `ic_fluent_subtract_24_regular` | Pages/SettingsPage.axaml |
| U+E92D | `ic_fluent_arrow_maximize_24_regular` | Views/VolumeWindow.axaml |
| U+E930 | `ic_fluent_checkmark_circle_24_regular` | Pages/HubAiPage.axaml.cs, Pages/WelcomePage.axaml.cs |
| U+E939 | `ic_fluent_person_feedback_24_regular` | Data/SearchIndex.cs, Pages/ShellPage.axaml, Pages/WelcomePage.axaml |
| U+E943 | `ic_fluent_code_24_regular` | Core/QuickLinks.cs, Data/SidebarModule.cs, Data/ToolDef.cs … |
| U+E945 | `ic_fluent_flash_24_regular` | Data/SearchIndex.cs, Pages/DetailPage.axaml, Pages/ExperimentalPage.axaml.cs … |
| U+E946 | `ic_fluent_info_24_regular` | Controls/LevelChipBar.cs, Pages/AppDataPage.axaml, Pages/HubAiPage.axaml.cs … |
| U+E9A6 | `ic_fluent_full_screen_maximize_24_regular` | Pages/SidebarLayoutPage.axaml |
| U+E9D2 | `ic_fluent_math_formula_24_regular` | Data/SidebarModule.cs, Data/ToolDef.cs, Pages/ShellPage.axaml … |
| U+E9D5 | `ic_fluent_task_list_ltr_24_regular` | Pages/SettingsPage.axaml |
| U+E9D9 | `ic_fluent_pulse_square_24_regular` | Data/SidebarModule.cs, Services/SystemRepair.cs, Views/LogDetailWindow.axaml |
| U+E9E9 | `ic_fluent_options_24_regular` | Pages/VirtualKeyboardPage.axaml |
| U+E9F3 | `ic_fluent_arrow_clockwise_dashes_24_regular` | Pages/ExperimentalPage.axaml.cs, Pages/ShellPage.axaml, Pages/SystemRepairPage.axaml … |
| U+EA18 | `ic_fluent_shield_24_regular` | Data/SidebarModule.cs, Pages/SettingsPage.axaml |
| U+EA3A | `ic_fluent_circle_24_regular` | Views/SnipEditorWindow.axaml |
| U+EA80 | `ic_fluent_lightbulb_24_regular` | Data/SearchIndex.cs, Pages/AppDataPage.axaml, Pages/EasiNoteGuardPage.axaml … |
| U+EA99 | `ic_fluent_broom_24_regular` | Data/SearchIndex.cs, Data/SidebarModule.cs, Pages/ExperimentalPage.axaml.cs … |
| U+EB51 | `ic_fluent_heart_24_regular` | Core/QuickLinks.cs |
| U+EB5E | `ic_fluent_wifi_off_24_regular` | MainWindow.axaml |
| U+EB9F | `ic_fluent_image_24_regular` | Pages/Tools/ImageColorToolPage.axaml |
| U+ECA5 | `ic_fluent_dashboard_20_regular` | Views/SnipEditorWindow.axaml |
| U+ED1A | `ic_fluent_eye_off_24_regular` | Pages/SidebarLayoutPage.axaml, Views/ToolSidebarWindow.axaml, Views/ToolSidebarWindow.axaml.cs |
| U+ED63 | `ic_fluent_pen_24_regular` | Views/SnipEditorWindow.axaml |
| U+ED64 | `ic_fluent_highlight_24_regular` | Views/SnipEditorWindow.axaml |
| U+EDA2 | `ic_fluent_hard_drive_24_regular` | Pages/WelcomePage.axaml.cs |
| U+EDA4 | `ic_fluent_hand_point_24_regular` | Pages/WelcomePage.axaml.cs |
| U+EDA8 | `ic_fluent_line_thickness_24_regular` | Views/SnipEditorWindow.axaml |
| U+EEA0 | `ic_fluent_ram_20_regular` | Pages/WelcomePage.axaml.cs |
| U+EEA1 | `ic_fluent_memory_16_regular` | Pages/WelcomePage.axaml.cs |
| U+EEA3 | `ic_fluent_square_multiple_24_regular` | Pages/WelcomePage.axaml.cs |

## 6. FluentAvalonia 内置控件码位

这些码位不由本工程代码直接书写，而是 FA 的控件主题在用（复选框、InfoBar、滚动条、NumberBox 等）。
大小写与形状必须与 FA 主题里的字形语义一致，改错会直接改变控件外观。

| 码位 | 当前 Fluent 字形 |
| --- | --- |
| U+E108 | `ic_fluent_subtract_24_regular` |
| U+E10A | `ic_fluent_dismiss_24_regular` |
| U+E10B | `ic_fluent_checkmark_24_regular` |
| U+E10C | `ic_fluent_more_horizontal_24_regular` |
| U+E110 | `ic_fluent_arrow_up_24_regular` |
| U+E11A | `ic_fluent_search_24_regular` |
| U+E18B | `ic_fluent_eye_24_regular` |
| U+E72B | `ic_fluent_arrow_left_24_regular` |
| U+E738 | `ic_fluent_subtract_24_regular` |
| U+E74B | `ic_fluent_arrow_down_24_regular` |
| U+E76D | `ic_fluent_filter_24_regular` |
| U+E923 | `ic_fluent_square_multiple_24_regular` |
| U+E974 | `ic_fluent_chevron_right_24_regular` |
| U+EA38 | `ic_fluent_sparkle_24_regular` |
| U+EC8F | `ic_fluent_chevron_up_down_24_regular` |
| U+EDD9 | `ic_fluent_triangle_left_20_regular` |
| U+EDDA | `ic_fluent_triangle_right_20_regular` |
| U+EDDB | `ic_fluent_triangle_up_20_regular` |
| U+EDDC | `ic_fluent_triangle_down_20_regular` |
| U+F29A | `ic_fluent_checkmark_circle_24_regular` |
| U+F3F1 | `ic_fluent_error_circle_20_regular` |
| U+F3F2 | `ic_fluent_error_circle_24_regular` |
| U+F4A5 | `ic_fluent_info_24_regular` |
| U+F4AC | `ic_fluent_person_24_regular` |
| U+F86A | `ic_fluent_warning_24_regular` |
| U+F882 | `ic_fluent_weather_partly_cloudy_day_48_regular` |

## 7. 统计

- 应用码位：**118 个**，全部有 `ic_fluent_` 映射
- FluentAvalonia 内置码位：**26 个**，全部有 `ic_fluent_` 映射
- 未映射图标码位：**0 个**（2026-10-29 全量审计）
