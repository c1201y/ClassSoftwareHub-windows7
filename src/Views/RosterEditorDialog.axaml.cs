using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Data;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 名单编辑弹窗里的一行。
///
/// 为什么要有这么个东西：名单最多 2000 人，列表必须走 ListBox 的**虚拟化**，
/// 那就只能是"数据驱动"的（每行不能我们自己 new 控件）。
/// </summary>
public sealed class RosterEntry : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    private int _no;

    public RosterEntry(int no, string name)
    {
        _no = no;
        _name = name;
    }

    /// <summary>行号（从 1 起）。⚠️ 可写：删行 / 加行之后要整排重排。</summary>
    public int No
    {
        get => _no;
        set
        {
            if (_no == value) return;
            _no = value;
            Raise(nameof(No));
            Raise(nameof(NoText));
            Raise(nameof(RowLabel));
        }
    }

    public string NoText => _no.ToString();

    /// <summary>读屏用："第 N 行姓名"。不含名字本身，所以改名后不用重发通知。</summary>
    public string RowLabel => $"第 {_no} 行姓名";

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            // Name 一起发通知是有意为之：提交时会把首尾空白去掉，
            // 通知一发出，输入框里那串没去空白的原文会被刷成规范后的值 —— 用户能看见自己写的东西被"整"过。
            Raise(nameof(Name));
            Raise(nameof(DeleteLabel));
        }
    }

    /// <summary>删除按钮的读屏名 —— 必须带上是谁，否则十行读出来一模一样。</summary>
    public string DeleteLabel => _name.Length == 0 ? "移除这一行（空行）" : $"把「{_name}」从名单中移除";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 「查看 / 编辑名单」弹窗（2026-09-29 Nick 提）：
/// 把导进来的名字列出来，逐个改名、逐个删除、随时补人，改完还能导出回 Excel / txt。
/// 一条"导入 → 核对 → 修正 → 带走"的闭环。
///
/// 语义：**改动当场生效**（改名、删行、加行都直接反映到 <see cref="Result"/>），
/// 所以底部只有一个「关闭」，不做"确定 / 取消"—— 免得用户改了五十个名字点取消全丢。
/// 调用方在ShowAsync 之后读 <see cref="Result"/> 即可。
///
/// ⚠️ 移植说明：
///   · 原版 WinUI 的 ListView → Avalonia 的 ListBox（虚拟化语义一致；容器清零那条
///     ItemContainerStyle 没有直接对应物，改由行模板收紧内边距）。
///   · 文件选择器从 WinRT 的 FileSavePicker 换成 <c>IStorageProvider.SaveFilePickerAsync</c>
///     （对话框浮在 TopLevel 上，直接取自身所属 TopLevel；取不到再退主窗口）。
///   · 可视树遍历用 Avalonia 的 <c>GetVisualDescendants()</c>（等价原版手写的 FindDescendant）。
/// </summary>
public sealed partial class RosterEditorDialog : ContentDialog
{
    /// <summary>
    /// ⛔ 千万别删（2026-10-07，Nick 实机报「抽号 → 查看 / 编辑名单」点了没反应、
    /// 而且**整页变空白**）：
    /// Avalonia 的样式查找用的键是 <see cref="IStyleable.StyleKey"/>，它默认 = **本类的类型**。
    /// 我们继承自 <c>ContentDialog</c>，默认键就成了 <c>RosterEditorDialog</c> ——
    /// FluentAvalonia 主题里根本没有这个键的样式，于是**模板不会被应用**。
    ///
    /// 后果是连锁的：模板没应用 → <c>ContentDialog.OnApplyTemplate</c> 里那句
    /// <c>NameScope.Get("PrimaryButton")</c>（FA 2.4.1 是**无条件**取的，找不到就抛）
    /// 抛 <see cref="System.Collections.Generic.KeyNotFoundException"/> → 异常发生在
    /// <c>MeasureCore</c> 里 → Avalonia 只能中断整棵视觉树的布局 → **页面一片空白**。
    /// 紧接着 Loaded 又调 <c>SetupDialog()</c>，再抛一条
    /// "Attempted to setup ContentDialog but the template has not been applied yet"。
    /// 两条都在 %LOCALAPPDATA%\ClassSoftwareHub\logs\crash.log 里（page=pick-number）。
    ///
    /// 修法就是这里：把 StyleKey 显式指回 <see cref="ContentDialog"/>，
    /// 让主题能按 ContentDialog 找到模板（FA 上游 issue #24 记的就是这个坑）。
    ///
    /// ⚠️ 2026-10-07 改法二选一，这里用的是 Avalonia 11 推荐的 <c>StyleKeyOverride</c>
    ///    （旧的 <c>IStyleable.StyleKey</c> 已过时：编译器 CS0618 提示"12.0 可能移除"）。
    ///    语义完全相同，只是换了个不用显式实现接口的写法。
    /// </summary>
    protected override Type StyleKeyOverride => typeof(ContentDialog);

    /// <summary>
    /// 界面上的行 —— **唯一真相**。
    ///
    /// ⚠️ 为什么它（而不是 <see cref="Result"/>）才是真相：用户随时能「添加姓名」多出一行，
    /// 那一行在填上名字之前不属于名单。两个集合各管一段（界面 / 有效姓名）就不会互相打脸。
    /// </summary>
    private readonly ObservableCollection<RosterEntry> _rows = new();

    private readonly string _source;

    /// <summary>当前名单（只含填了名字的行）。关掉弹窗后由调用方读走。</summary>
    public List<string> Result { get; } = new();

    public RosterEditorDialog(IEnumerable<string> names, string source)
    {
        InitializeComponent();

        _source = source ?? "";
        RosterList.ItemsSource = _rows;

        foreach (var name in names) _rows.Add(new RosterEntry(_rows.Count + 1, (name ?? "").Trim()));

        SyncResult();

        // 挂上窗口之后按可用高度收一次列表高度（见「高度自适应」一节）
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(FitListHeight, DispatcherPriority.Background);
        Loaded += (_, _) => Dispatcher.UIThread.Post(FitListHeight, DispatcherPriority.Background);
    }

    // ══════════ 高度自适应 ══════════
    //
    // ⚠️ 为什么要有这一段（2026-10-07 实测发现；Nick 也反馈过"编辑名单窗口有问题"）：
    //    上游 WinUI 版把名单高度写死 380 DIP（一屏核对十几个人）。在 WinUI 里 ContentDialog
    //    自己带一层兜底，超出窗口的部分会被压回去、按钮始终可见；
    //    **FluentAvalonia 的 ContentDialog 没有这层兜底** —— 内容多高就铺多高，
    //    整块溢到窗口外面（弹窗浮在覆盖层上，不会被窗口裁掉）。
    //    实测：窗口只有 ~567 DIP 高时，底部的「关闭」被挤到窗口下方、鼠标点不到
    //    （UIA 里能查到它，但 rect.bottom 已经超出窗口 rect.bottom）。
    //    修法：量出"列表以外"的固定开销，把列表高度收进可用高度里（上限仍是上游的 380）。
    //    ⚠️ 量不准（Bounds 还没算出来 / 数值离谱）就退回上游的固定 380 —— 兜底逻辑绝不能反过来把界面搞坏。

    /// <summary>上游写死的列表高度，同时也是我们的上限。</summary>
    private const double MaxListHeight = 380;

    /// <summary>列表至少留这么高；再挤就让它自己滚（否则连名字都看不见）。</summary>
    private const double MinListHeight = 140;

    /// <summary>列表以外（标题 / 页头 / 页脚 / 按钮 / 留白）的总高。量到一次就记住。</summary>
    private double _chromeHeight;

    private bool _chromeMeasured;

    private void FitListHeight()
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;

            var avail = top.Bounds.Height;
            if (avail <= 0) return;

            if (!_chromeMeasured)
            {
                var need = Bounds.Height;                 // 整张弹窗此刻的高度（列表 = 380）
                var listH = RosterList.Bounds.Height;
                if (need <= 0 || listH <= 0) return;      // 还没布局完，等下一次回调

                var chrome = need - listH;
                // 合理性检查：列表以外的部分应在 100~460 之间，离谱就当量失败（维持上游固定值）
                if (chrome is not (> 100 and < 460)) return;

                _chromeHeight = chrome;
                _chromeMeasured = true;
            }

            var target = Math.Clamp(avail - _chromeHeight - 16, MinListHeight, MaxListHeight);
            if (Math.Abs(RosterList.Height - target) > 1) RosterList.Height = target;
        }
        catch
        {
            // 纯兜底：出任何意外都保持上游的固定高度
        }
    }

    // ══════════ 增 / 删 / 改 ══════════

    /// <summary>
    /// 原版 ListView 用 <c>SelectionMode="None"</c> 关掉选择；<b>Avalonia 没有这一档</b>
    /// （<c>SelectionMode</c> 是 [Flags] 枚举，<c>Single = 0</c> 就是默认值，"None" 编译不过）。
    /// 名字那一格本身就是输入框，点它只为改名，不该在背后再选出一行强调色高亮 ——
    /// 于是选中就当场清掉。⚠️ 在同一次输入处理里同步清掉，渲染还没轮到，看不到闪烁。
    /// </summary>
    private void RosterList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RosterList.SelectedIndex >= 0) RosterList.SelectedIndex = -1;
    }

    /// <summary>
    /// 在末尾加一行，并把键盘焦点直接送进新行的输入框 —— 点完就能打字，不用再找一次该点哪儿。
    /// （2026-09-29 Nick：「明明有删除，却不点增加是何意味，只删不增是吧？」）
    /// </summary>
    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        var entry = new RosterEntry(_rows.Count + 1, "");
        _rows.Add(entry);
        SyncResult();                 // 空行不算人数，但"另有 N 行未填"的提示要跟着变

        // ⚠️ 虚拟化列表：容器是滚进可视区才生成的，所以必须先布局、再滚动、再取容器。
        RosterList.UpdateLayout();
        RosterList.ScrollIntoView(entry);
        RosterList.UpdateLayout();
        FocusName(entry);

        ToastText.Text = "已添加一行，填好后按回车。空着不填的行，关闭后会自动丢弃。";
    }

    private void FocusName(RosterEntry entry)
    {
        // ⚠️ Avalonia 的 ItemContainerGenerator 只有 ContainerFromIndex（没有 ContainerFromItem），
        //    先用 Items.IndexOf 换下标再取容器。
        var index = RosterList.Items.IndexOf(entry);
        if (index < 0) return;
        if (RosterList.ItemContainerGenerator.ContainerFromIndex(index) is not Visual container) return;

        var box = FindDescendant<TextBox>(container);
        if (box is null) return;

        box.Focus();
        box.SelectAll();
    }

    private static T? FindDescendant<T>(Visual node) where T : Visual
        => node.GetVisualDescendants().OfType<T>().FirstOrDefault();

    /// <summary>
    /// 按界面上的当前内容重建 <see cref="Result"/>。
    ///
    /// ⚠️ 为什么是"整份重建"而不是"按下标改一格"：用户可以加出空行，空行随时会被丢掉 ——
    /// 只要有任何一行可能被跳过，下标就不再对齐，逐格维护迟早会改错人。整份重建永远对。
    /// </summary>
    private void SyncResult()
    {
        Result.Clear();
        foreach (var row in _rows)
        {
            var name = (row.Name ?? "").Trim();
            if (name.Length > 0) Result.Add(name);
        }

        RefreshChrome();
    }

    /// <summary>人数 / 空状态 / 导出可用性 —— 三处都跟着名单走，集中在一处改。</summary>
    private void RefreshChrome()
    {
        var blanks = _rows.Count - Result.Count;

        CountText.Text = _rows.Count == 0
            ? "共 0 人"
            : $"共 {Result.Count} 人"
              + (blanks > 0 ? $"（另有 {blanks} 行未填，关闭后不保存）" : "")
              + (string.IsNullOrWhiteSpace(_source) ? "" : $"（{_source}）");

        RosterList.IsVisible = _rows.Count > 0;
        EmptyHint.IsVisible = _rows.Count == 0;

        ExportExcelButton.IsEnabled = Result.Count > 0;
        ExportTxtButton.IsEnabled = Result.Count > 0;
    }

    // 交互取舍：名字那一格**就是输入框**（不是"先点编辑再变输入框"），少一步、也不用猜哪儿能点；
    // 提交时机 = 回车 或 焦点离开；名字被清空**不提交**，还原成原值 —— 免得手一滑抽出个空白。
    // 真要删人就点右边那个删除键，两个动作分得开。
    private void Name_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitName(sender as TextBox);
    }

    private void Name_LostFocus(object? sender, RoutedEventArgs e) => CommitName(sender as TextBox);

    private void CommitName(TextBox? box)
    {
        if (box?.DataContext is not RosterEntry entry) return;

        var typed = (box.Text ?? "").Trim();

        if (typed.Length == 0)
        {
            // 清空一律还原成"这一行上次提交过的值"：老名字不会被手滑误删，
            // 刚加的空行也保持空着（不弹提示 —— 用户接着打字时弹提示更烦）。
            box.Text = entry.Name;
            return;
        }

        if (entry.Name == typed) return;

        entry.Name = typed;
        SyncResult();
        ToastText.Text = $"已改名为「{typed}」";
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not RosterEntry entry) return;

        var removed = entry.Name.Trim();
        _rows.Remove(entry);

        for (var i = 0; i < _rows.Count; i++) _rows[i].No = i + 1;   // 行号重排
        SyncResult();

        ToastText.Text = removed.Length == 0
            ? "已移除该空行。"
            : $"已移除「{removed}」；关闭本窗口后生效。";
    }

    // ══════════ 导出 ══════════
    private async void ExportExcel_Click(object? sender, RoutedEventArgs e) => await ExportAsync(excel: true);

    private async void ExportTxt_Click(object? sender, RoutedEventArgs e) => await ExportAsync(excel: false);

    /// <summary>导的是**当前这一份**（含刚才的手改），不是原来那个文件 —— 这样"改完导出"才有意义。</summary>
    private async Task ExportAsync(bool excel)
    {
        if (Result.Count == 0)
        {
            ToastText.Text = "名单是空的，没有可导出的内容。";
            return;
        }

        try
        {
            // 对话框浮在 TopLevel 的浮层里，优先取自己所属的 TopLevel（取不到退主窗口）
            var top = TopLevel.GetTopLevel(this) ?? (TopLevel?)App.MainWindow;
            if (top is null) return;

            var options = new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                SuggestedFileName = "班级名单",
                DefaultExtension = excel ? "xlsx" : "txt",
                FileTypeChoices = new List<Avalonia.Platform.Storage.FilePickerFileType>
                {
                    new(excel ? "Excel 工作簿" : "文本文件")
                    {
                        Patterns = new[] { excel ? "*.xlsx" : "*.txt" }
                    },
                },
            };

            var file = await top.StorageProvider.SaveFilePickerAsync(options);
            if (file is null) return;

            var path = file.Path.LocalPath;
            if (excel) NameRoster.WriteXlsx(path, Result);
            else NameRoster.WriteText(path, Result);

            ToastText.Text = $"已导出 {Result.Count} 个姓名到「{file.Name}」。";
        }
        catch (Exception ex)
        {
            ToastText.Text = "导出失败：" + ex.Message;
        }
    }
}
