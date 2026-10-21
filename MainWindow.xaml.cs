using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using WeLinkMessageExporter.Export;
using WeLinkMessageExporter.Models;
using WeLinkMessageExporter.Services;

namespace WeLinkMessageExporter;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private readonly CliService _cli = new();
    private readonly DiagnosticLog _diagnostics = new();
    private readonly ConversationService _conversationService;
    private readonly MessageService _messageService;
    private readonly ContactService _contacts;
    private readonly ObservableCollection<Conversation> _conversations = new();
    private readonly ICollectionView _view;
    private string _filter = "all";
    private string _sortMode = "latest";
    private long _currentRecentBatch;
    private CancellationTokenSource? _activityCts;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _isClosing;
    private bool _startupScheduled;

    public MainWindow()
    {
        CrashReporter.Trace("MainWindow constructor entered");
        InitializeComponent();
        CrashReporter.Trace("MainWindow XAML initialized");
        _conversationService = new ConversationService(_cli);
        _messageService = new MessageService(_cli);
        _contacts = new ContactService(_cli);
        _view = CollectionViewSource.GetDefaultView(_conversations);
        _view.Filter = FilterConversation;
        if (_view is ListCollectionView sortedView) sortedView.CustomSort = new ConversationComparer(this);
        ConversationList.ItemsSource = _view;
        DateTime beijingToday = DateTime.UtcNow.AddHours(8).Date;
        StartPicker.SelectedDate = beijingToday.AddDays(-30);
        EndPicker.SelectedDate = beijingToday;
        OutputBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WeLinkExports");
        try
        {
            foreach (var item in ConversationStore.Load().Where(x => x is not null)) AddOrUpdate(item, save: false);
        }
        catch (Exception ex)
        {
            CrashReporter.Exception("Loading local conversation index", ex);
            // Cache errors must not block access to the main window.
        }
        _currentRecentBatch = _conversations.Count == 0 ? 0 : _conversations.Max(x => x.RecentBatchTicks);
        ApplySortMode("latest");
        UpdateCounts();
        CrashReporter.Trace("MainWindow initialized; waiting for visual tree");
        SourceInitialized += (_, _) =>
        {
            try
            {
                int dark = 1;
                var hwnd = new WindowInteropHelper(this).Handle;
                if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
            }
            catch { /* legacy Windows versions can keep their normal caption */ }
        };
        Loaded += Window_Loaded;
        Closing += (_, _) =>
        {
            _isClosing = true;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _activityCts?.Cancel(); } catch (ObjectDisposedException) { }
            try { ConversationStore.Save(_conversations); } catch (Exception ex) { CrashReporter.Exception("Closing: saving index", ex); }
            try { _diagnostics.Dispose(); } catch (Exception ex) { CrashReporter.Exception("Closing: diagnostic shutdown", ex); }
            CrashReporter.Trace("MainWindow closed");
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupScheduled) return;
        _startupScheduled = true;
        CrashReporter.Trace("MainWindow Loaded event");
        // Post to idle after the first window layout/paint. Never block window creation
        // on WeLink authentication or conversation retrieval.
        Dispatcher.BeginInvoke(new Action(() => _ = SafeStartupAsync()), DispatcherPriority.ApplicationIdle);
    }

    private async Task SafeStartupAsync()
    {
        try
        {
            if (_isClosing) return;
            CrashReporter.Trace("Automatic CLI status check begin");
            var cliAvailable = await CheckConnectionAsync();
            if (_isClosing) return;
            if (cliAvailable)
            {
                CrashReporter.Trace("Automatic recent-conversation retrieval begin");
                await LoadRecentAsync();
            }
            else
            {
                Log("CLI 尚未就绪。窗口保持打开，你可先登录或点击“检查连接”。");
            }
            CrashReporter.Trace("Automatic startup operations finished");
        }
        catch (OperationCanceledException)
        {
            CrashReporter.Trace("Automatic startup cancelled");
        }
        catch (Exception ex)
        {
            CrashReporter.Exception("SafeStartupAsync", ex);
            TryShowRecoverableError("启动初始化失败", ex);
        }
    }

    private void TryShowRecoverableError(string title, Exception ex)
    {
        try
        {
            if (!_isClosing)
            {
                Log($"{title}：{ex.Message}");
                MessageBox.Show(this, $"{ex.Message}\n\n窗口不会自动关闭。详细日志：\n{CrashReporter.FilePath}", title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception reportError)
        {
            CrashReporter.Exception("Displaying error", reportError);
        }
    }

    private bool FilterConversation(object obj)
    {
        if (obj is not Conversation conv) return false;
        if (_filter != "all" && _filter != conv.Type) return false;
        string text = SearchBox?.Text?.Trim() ?? "";
        if (text.Length == 0) return true;
        return (conv.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
               || (conv.Id?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
               || (conv.Detail?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void Log(string message)
    {
        if (_isClosing) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(message)); return; }
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        LogBox.AppendText(line);
        try { _diagnostics.Write(message); } catch (Exception ex) { CrashReporter.Exception("Writing UI log", ex); }
        if (LogBox.Text.Length > 24000) LogBox.Text = LogBox.Text[^18000..];
        LogBox.ScrollToEnd();
        FooterText.Text = message.Length > 110 ? message[..110] + "…" : message;
    }

    private void SetBusy(bool busy, bool cancellable = false)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        LoginButton.IsEnabled = !busy;
        RemoteSearchButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy && cancellable;
    }

    private bool EnterOperation(bool cancellable = false)
    {
        if (_busy) return false;
        _cts = new CancellationTokenSource();
        SetBusy(true, cancellable);
        return true;
    }
    private void FinishOperation()
    {
        if (_isClosing)
        {
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            return;
        }
        try
        {
            SetBusy(false);
            ExportProgress.IsIndeterminate = false;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task<bool> CheckConnectionAsync()
    {
        if (!EnterOperation()) return false;
        try
        {
            LoginStatusText.Text = "正在检测…";
            string version = await _cli.ExecuteAsync(new[] { "--version" }, _cts!.Token);
            string status = await _cli.ExecuteAsync(new[] { "auth", "status" }, _cts.Token);
            bool unauth = status.Contains("not logged", StringComparison.OrdinalIgnoreCase)
                      || status.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
                      || status.Contains("未登录", StringComparison.OrdinalIgnoreCase)
                      || status.Contains("未授权", StringComparison.OrdinalIgnoreCase)
                      || status.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase)
                      || status.Contains("not login", StringComparison.OrdinalIgnoreCase)
                      || status.Contains("login required", StringComparison.OrdinalIgnoreCase)
                      || System.Text.RegularExpressions.Regex.IsMatch(status,
                           "\"(?:authenticated|isAuthenticated|isLoggedIn|loggedIn|success)\"\\s*:\\s*false",
                           System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            LoginStatusText.Text = unauth ? "需要登录" : "CLI 已连接";
            LoginStatusText.Foreground = (Brush)FindResource(unauth ? "MutedBrush" : "AccentBrush");
            Log("检测到 WeLink CLI " + version.Split('\n')[0].Trim() + (unauth ? "，请先授权登录" : "，认证查询已返回"));
            // Never write auth output (which may contain scopes or account information) to the log.
            return !unauth;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            CrashReporter.Exception("CLI status check", ex);
            LoginStatusText.Text = "连接失败";
            LoginStatusText.Foreground = (Brush)FindResource("MutedBrush");
            Log("CLI 检测失败：" + ex.Message);
            return false;
        }
        finally { FinishOperation(); }
    }

    private async Task LoadRecentAsync(bool userInitiated = false)
    {
        if (!EnterOperation(true)) return;
        try
        {
            // Stop any previous probe. A new refresh must reflect its own latest snapshot.
            _activityCts?.Cancel();
            Log("正在快速刷新会话列表，历史会话及本地置顶设置均会保留。");
            List<Conversation>? rows = null;
            Exception? last = null;
            foreach (int count in new[] { 100, 50, 20, 10 })
            {
                _cts!.Token.ThrowIfCancellationRequested();
                try { rows = await _conversationService.RecentAsync(count, _cts.Token); break; }
                catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
            }
            if (rows is null) throw last ?? new InvalidOperationException("未取得最近会话列表。");

            var existingKeys = new HashSet<string>(_conversations.Select(x => x.Key));
            long batch = DateTime.UtcNow.Ticks;
            _currentRecentBatch = batch;
            int rank = 0;
            foreach (var row in rows)
            {
                row.RecentBatchTicks = batch;
                row.RecentRank = rank++;
                AddOrUpdate(row, save: false);
            }
            int added = _conversations.Count(x => !existingKeys.Contains(x.Key));
            ConversationStore.Save(_conversations);
            if (userInitiated)
            {
                // A stale search/type filter or scroll offset must not hide newly discovered chats.
                if (!string.IsNullOrWhiteSpace(SearchBox.Text)) SearchBox.Clear();
                SetFilter("all");
            }
            _view.Refresh();
            UpdateCounts();
            ScrollConversationListToTop();
            Log($"刷新完成：本次取得 {rows.Count} 条（新增 {added} 条），已立即重排；本地索引共 {_conversations.Count} 条。");
            ProgressText.Text = "已按 WeLink 最近会话顺序排列；最新消息时间仅用于展示，不会使列表跳位。";
            // The sample WeLink API does not supply per-conversation timestamps.
            // Resolve them separately without delaying the visible refresh.
            // Limit timestamp probes to the visible recent chats, rather than firing 100
            // concurrent CLI calls and hitting the server's rate/error limits.
            BeginActivityLookup(rows.Take(12).Select(r => r.Key).Distinct().ToList(), batch);
        }
        catch (OperationCanceledException) { Log("取消刷新会话。"); }
        catch (Exception ex) { CrashReporter.Exception("Recent conversation refresh", ex); Log("刷新失败：" + ex.Message); }
        finally { FinishOperation(); }
    }

    private void ScrollConversationListToTop()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosing) return;
            var first = _view.Cast<Conversation>().FirstOrDefault();
            if (first != null) ConversationList.ScrollIntoView(first);
        }), DispatcherPriority.Loaded);
    }

    private void BeginActivityLookup(IReadOnlyList<string> keys, long batch)
    {
        _activityCts?.Cancel();
        if (_isClosing || keys.Count == 0) return;
        var cts = new CancellationTokenSource();
        _activityCts = cts;
        _ = SafeActivityLookupAsync(keys, batch, cts);
    }

    private async Task SafeActivityLookupAsync(IReadOnlyList<string> keys, long batch, CancellationTokenSource cts)
    {
        int succeeded = 0, failed = 0;
        using var slots = new SemaphoreSlim(3, 3);
        try
        {
            // Capture immutable identities; avoid touching ObservableCollection off the UI thread.
            var targets = keys.Select(key => _conversations.FirstOrDefault(x => x.Key == key))
                .Where(x => x is not null).Cast<Conversation>()
                .Select(x => new Conversation { Type = x.Type, Id = x.Id, Name = x.Name }).ToList();
            var tasks = targets.Select(async conv =>
            {
                await slots.WaitAsync(cts.Token);
                try
                {
                    // Probe timeouts must not hold up the refresh or block later exports.
                    using var perChatTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    perChatTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                    long latest = await _messageService.GetLatestMessageTimeAsync(conv, perChatTimeout.Token);
                    if (latest > 0)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_isClosing || cts.IsCancellationRequested || _currentRecentBatch != batch) return;
                            var known = _conversations.FirstOrDefault(x => x.Key == conv.Key);
                            if (known == null) return;
                            // Timestamp is display-only for the active recent batch.
                            // Resorting after each response used to reshuffle rows while scrolling.
                            known.LastMessageUnixMs = Math.Max(known.LastMessageUnixMs, latest);
                        }, DispatcherPriority.Background);
                        Interlocked.Increment(ref succeeded);
                    }
                    else Interlocked.Increment(ref failed);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
                catch (OperationCanceledException) { Interlocked.Increment(ref failed); } // per-chat timeout
                catch (Exception) { Interlocked.Increment(ref failed); }
                finally { slots.Release(); }
            }).ToList();
            await Task.WhenAll(tasks);
            if (!cts.IsCancellationRequested && !_isClosing)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_currentRecentBatch != batch) return;
                    ConversationStore.Save(_conversations);
                    Log($"最近会话前 {targets.Count} 项时间抽样核对结束：成功 {succeeded}，未确认 {failed}。排序不会被抽样结果打乱。");
                    ProgressText.Text = "排序已稳定：最近会话按返回顺序，置顶模式按本地置顶分组。";
                }, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { CrashReporter.Exception("Activity timestamp refresh", ex); }
        finally
        {
            if (ReferenceEquals(_activityCts, cts)) _activityCts = null;
            cts.Dispose();
        }
    }

    private void AddOrUpdate(Conversation conv, bool save = true)
    {
        if (conv is null || string.IsNullOrWhiteSpace(conv.Id) || (conv.Type != "group" && conv.Type != "private")) return;
        conv.Name ??= conv.Id;
        conv.Detail ??= "";
        Conversation? known = _conversations.FirstOrDefault(x => x.Key == conv.Key);
        if (known == null)
        {
            conv.PropertyChanged += Item_PropertyChanged;
            _conversations.Add(conv);
        }
        else
        {
            // Prefer an existing custom label, but replace raw-ID placeholders with discovered names.
            if ((known.Name == known.Id || string.IsNullOrWhiteSpace(known.Name)) && !string.IsNullOrWhiteSpace(conv.Name)) known.Name = conv.Name;
            if (!string.IsNullOrWhiteSpace(conv.Detail)) known.Detail = conv.Detail;
            if (conv.RecentBatchTicks > 0)
            {
                known.RecentBatchTicks = conv.RecentBatchTicks;
                known.RecentRank = conv.RecentRank;
            }
            if (conv.LastMessageUnixMs > 0) known.LastMessageUnixMs = Math.Max(known.LastMessageUnixMs, conv.LastMessageUnixMs);
            // Preserve known.IsPinned and known.Selected. Search/recent results cannot overwrite local preferences.
        }
        if (save) { ConversationStore.Save(_conversations); _view.Refresh(); UpdateCounts(); }
    }
    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Conversation.Selected)) UpdateCounts();
    }
    private void UpdateCounts()
    {
        int visible = _view.Cast<Conversation>().Count();
        CountText.Text = visible == _conversations.Count ? $"{visible} 个会话" : $"显示 {visible} / {_conversations.Count}";
        SelectedText.Text = $"已选择 {_conversations.Count(c => c.Selected)} 个";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
        UpdateCounts();
    }
    private void SetFilter(string filter)
    {
        _filter = filter;
        AllFilter.Background = (Brush)FindResource(filter == "all" ? "AccentBrush" : "PanelRaisedBrush");
        PrivateFilter.Background = (Brush)FindResource(filter == "private" ? "AccentBrush" : "PanelRaisedBrush");
        GroupFilter.Background = (Brush)FindResource(filter == "group" ? "AccentBrush" : "PanelRaisedBrush");
        AllFilter.Foreground = (Brush)FindResource(filter == "all" ? "BackgroundBrush" : "TextBrush");
        PrivateFilter.Foreground = (Brush)FindResource(filter == "private" ? "BackgroundBrush" : "TextBrush");
        GroupFilter.Foreground = (Brush)FindResource(filter == "group" ? "BackgroundBrush" : "TextBrush");
        _view.Refresh();
        UpdateCounts();
    }
    private void ApplySortMode(string mode)
    {
        // Resolve controls from the XAML namescope instead of relying on generated
        // MainWindow.g.cs fields. Stale WPF intermediate files can otherwise cause
        // CS0103 when the buttons are introduced in a newer XAML layout.
        _sortMode = mode;
        UpdateSortButtonVisual("LatestSortButton", mode == "latest");
        UpdateSortButtonVisual("PinnedSortButton", mode == "pinned");
        _view.Refresh();
    }

    private void UpdateSortButtonVisual(string name, bool active)
    {
        if (FindName(name) is not Button button)
        {
            // Keep sorting functional even if a customized layout omits this button.
            CrashReporter.Trace($"Optional sorting control not found: {name}");
            return;
        }
        button.Background = (Brush)FindResource(active ? "AccentBrush" : "PanelRaisedBrush");
        button.Foreground = (Brush)FindResource(active ? "BackgroundBrush" : "TextBrush");
    }
    private void LatestSort_Click(object sender, RoutedEventArgs e) { ApplySortMode("latest"); ScrollConversationListToTop(); }
    private void PinnedSort_Click(object sender, RoutedEventArgs e) { ApplySortMode("pinned"); ScrollConversationListToTop(); }
    private void PinConversation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Conversation conv })
        {
            conv.IsPinned = !conv.IsPinned;
            _view.Refresh();
            ConversationStore.Save(_conversations);
            if (_sortMode == "pinned") ScrollConversationListToTop();
            Log((conv.IsPinned ? "本地置顶：" : "取消本地置顶：") + conv.Name);
            e.Handled = true;
        }
    }

    private sealed class ConversationComparer : IComparer
    {
        private readonly MainWindow _owner;
        public ConversationComparer(MainWindow owner) => _owner = owner;
        public int Compare(object? a, object? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a is not Conversation x) return -1;
            if (b is not Conversation y) return 1;
            if (_owner._sortMode == "pinned" && x.IsPinned != y.IsPinned) return x.IsPinned ? -1 : 1;
            return CompareActivity(x, y, _owner._currentRecentBatch);
        }
        private static int CompareActivity(Conversation x, Conversation y, long batch)
        {
            // The CLI's recent-conversation response does not contain a timestamp.
            // Its returned order is the most reliable common ranking for the entire
            // CURRENT snapshot. Do not mix timestamps for only 14/99 rows with the
            // other 85: doing so makes known/unknown rows leapfrog unexpectedly.
            bool xRecent = batch > 0 && x.RecentBatchTicks == batch;
            bool yRecent = batch > 0 && y.RecentBatchTicks == batch;
            if (xRecent != yRecent) return xRecent ? -1 : 1;
            if (xRecent && yRecent)
            {
                int byRank = x.RecentRank.CompareTo(y.RecentRank);
                if (byRank != 0) return byRank;
            }
            else
            {
                // Historical/search-only chats are not ordered by the current
                // recent response. Use their validated cached timestamps when available.
                bool xHasTime = x.LastMessageUnixMs > 946684800000L;
                bool yHasTime = y.LastMessageUnixMs > 946684800000L;
                if (xHasTime != yHasTime) return xHasTime ? -1 : 1;
                if (xHasTime)
                {
                    int byTime = y.LastMessageUnixMs.CompareTo(x.LastMessageUnixMs);
                    if (byTime != 0) return byTime;
                }
                int bySnapshot = y.RecentBatchTicks.CompareTo(x.RecentBatchTicks);
                if (bySnapshot != 0) return bySnapshot;
                int byRank = x.RecentRank.CompareTo(y.RecentRank);
                if (byRank != 0) return byRank;
            }
            int byName = StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name);
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(x.Key, y.Key);
        }
    }

    private void AllFilter_Click(object sender, RoutedEventArgs e) => SetFilter("all");
    private void PrivateFilter_Click(object sender, RoutedEventArgs e) => SetFilter("private");
    private void GroupFilter_Click(object sender, RoutedEventArgs e) => SetFilter("group");
    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var conv in _view.Cast<Conversation>()) conv.Selected = true;
        ConversationStore.Save(_conversations);
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var conv in _conversations) conv.Selected = false;
        ConversationStore.Save(_conversations);
    }
    private void ManualAdd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var id = CliService.ValidateId(ManualIdBox.Text);
            var type = ManualGroupRadio.IsChecked == true ? "group" : "private";
            var name = string.IsNullOrWhiteSpace(ManualNameBox.Text) ? id : ManualNameBox.Text.Trim();
            AddOrUpdate(new Conversation { Type = type, Id = id, Name = name, Detail = type == "group" ? "群 ID: " + id : "账号: " + id });
            var added = _conversations.First(x => x.Key == type + ":" + id);
            added.Selected = true;
            Log("已添加并选中：" + added.Name);
            ManualIdBox.Clear(); ManualNameBox.Clear();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法添加", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void RemoteSearch_Click(object sender, RoutedEventArgs e)
    {
        if (!EnterOperation(true)) return;
        try
        {
            string keyword = CliService.ValidateSearch(SearchBox.Text);
            Log("正在在线搜索联系人与群聊：" + keyword);
            var rows = await _conversationService.SearchAsync(keyword, _cts!.Token);
            foreach (var row in rows) AddOrUpdate(row, save: false);
            ConversationStore.Save(_conversations);
            _view.Refresh(); UpdateCounts();
            Log($"在线搜索得到 {rows.Count} 条，已合并到本地会话列表。");
            if (rows.Count is > 0 and <= 12)
            {
                Log("搜索结果较少，后台核对这些会话的最新消息时间，以便正确插入排序位置。");
                BeginActivityLookup(rows.Select(x => x.Key).Distinct().ToList(), _currentRecentBatch);
            }
        }
        catch (OperationCanceledException) { Log("已取消在线搜索。"); }
        catch (Exception ex) { Log("在线搜索失败：" + ex.Message); }
        finally { FinishOperation(); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadRecentAsync(userInitiated: true);
    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckConnectionAsync();
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (!EnterOperation(true)) return;
        bool completed = false;
        try
        {
            LoginStatusText.Text = "等待扫码授权…";
            LoginStatusText.Foreground = (Brush)FindResource("BlueBrush");
            ExportProgress.IsIndeterminate = true;
            ProgressText.Text = "扫码登录进行中，请查看弹出的终端二维码，并在手机 WeLink 确认授权。";
            Log("已启动 WeLink CLI 原生扫码登录；程序会持续等待手机扫码及确认，不会提前视为登录成功。可按“取消”中止。");
            int exitCode = await _cli.LoginInteractiveAsync(_cts!.Token);
            _cts.Token.ThrowIfCancellationRequested();
            if (exitCode != 0)
            {
                LoginStatusText.Text = "登录未完成";
                Log($"CLI 登录命令退出码为 {exitCode}；未视为登录成功，请根据扫码终端的错误信息重试。");
            }
            else
            {
                Log("CLI 扫码登录命令已正常结束，正在二次验证授权状态。");
                completed = true;
            }
        }
        catch (OperationCanceledException)
        {
            LoginStatusText.Text = "已取消登录";
            Log("用户取消了本次扫码登录；未更改 WeLink CLI 已保存的认证状态。");
        }
        catch (Exception ex)
        {
            CrashReporter.Exception("Interactive WeLink login", ex);
            LoginStatusText.Text = "登录失败";
            Log("无法完成扫码登录：" + ex.Message);
            TryShowRecoverableError("WeLink 扫码登录失败", ex);
        }
        finally
        {
            FinishOperation();
            if (!_isClosing && !completed) ProgressText.Text = "登录未完成；可以再次点击“扫码登录”。";
        }
        if (!completed || _isClosing) return;
        bool confirmed = await CheckConnectionAsync();
        if (_isClosing) return;
        if (confirmed)
        {
            Log("登录状态复核通过，自动更新会话列表。");
            ProgressText.Text = "已完成扫码授权和登录状态校验。";
            await LoadRecentAsync();
        }
        else
        {
            Log("扫码命令已结束，但后续认证状态未通过；请确认手机授权及终端反馈。");
            ProgressText.Text = "登录状态未确认，请查看日志并尝试“检查连接”。";
        }
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择聊天记录导出文件夹", Multiselect = false };
        if (dlg.ShowDialog(this) == true) OutputBox.Text = dlg.FolderName;
    }
    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DiagnosticLog.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "\"" + DiagnosticLog.LogDirectory + "\"", UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开诊断日志", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(OutputBox.Text);
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "\"" + OutputBox.Text + "\"", UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开文件夹", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("正在取消当前操作…");
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _conversations.Where(x => x.Selected).ToList();
        if (selected.Count == 0) { MessageBox.Show("请先勾选至少一个群聊或私聊。", "未选择会话", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!StartPicker.TryCommit() || !EndPicker.TryCommit() ||
            StartPicker.SelectedDate is not DateTime from || EndPicker.SelectedDate is not DateTime to || from.Date > to.Date)
        { MessageBox.Show("请选择有效的日期范围（格式 YYYY-MM-DD），且开始日期不能晚于结束日期。", "日期错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        string path = OutputBox.Text.Trim();
        if (path.Length == 0) { MessageBox.Show("请选择导出目录。", "路径错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!EnterOperation(true)) return;
        _activityCts?.Cancel(); // Prevent timestamp probes competing with large history exports.
        var succeeded = new List<string>();
        var failed = new List<string>();
        int pageSize = Page20.IsChecked == true ? 20 : Page50.IsChecked == true ? 50 : 100;
        try
        {
            Directory.CreateDirectory(path);
            ExportProgress.Value = 0;
            Log($"开始导出：{selected.Count} 个会话，{from:yyyy-MM-dd} 至 {to:yyyy-MM-dd}，每页 {pageSize} 条。");
            for (int i = 0; i < selected.Count; i++)
            {
                var conv = selected[i];
                _cts!.Token.ThrowIfCancellationRequested();
                ProgressText.Text = $"{i + 1}/{selected.Count} · 正在读取 {conv.Name} 的消息…";
                ExportProgress.Value = (double)i * 100 / selected.Count;
                try
                {
                    var messages = await _messageService.GetRangeAsync(conv, from, to, pageSize, Log, _cts.Token);
                    _cts.Token.ThrowIfCancellationRequested();
                    if (messages.Count == 0)
                        Log($"{conv.Name}：接口在选定日期范围内未返回消息，TXT 会标注 0 条；如果 WeLink 客户端存在对应消息，请核对会话账号及所选日期。");
                    ProgressText.Text = $"正在补全发送人：{conv.Name}（{messages.Count} 条消息）";
                    var names = await _contacts.ResolveAsync(messages.Select(x => x.Sender), Log, _cts.Token);
                    int missing = names.Values.Count(v => !v.Resolved);
                    if (missing > 0) Log($"{conv.Name}：{missing} 个发送人未解析成功，将保留原始账号。");
                    ProgressText.Text = $"正在保存：{conv.Name}";
                    string written = await TxtExporter.SaveAsync(conv, messages, names, from, to, path, _cts.Token);
                    succeeded.Add(written);
                    Log($"导出完成：{Path.GetFileName(written)} · {messages.Count} 条");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed.Add(conv.Name + "：" + ex.Message);
                    Log("导出失败：" + conv.Name + " · " + ex.Message);
                }
                ExportProgress.Value = (double)(i + 1) * 100 / selected.Count;
            }
            ProgressText.Text = $"完成：成功 {succeeded.Count} 个，失败 {failed.Count} 个。";
            Log(ProgressText.Text);
            if (failed.Count > 0) MessageBox.Show("部分会话未成功导出，失败原因见运行日志。\n\n" + string.Join("\n", failed.Take(6)), "导出完成（部分失败）", MessageBoxButton.OK, MessageBoxImage.Warning);
            else MessageBox.Show($"成功导出 {succeeded.Count} 个会话。\n\n保存位置：{path}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { ProgressText.Text = "操作已取消；先前完成的文件保留，未写完的临时文件已删除。"; Log(ProgressText.Text); }
        catch (Exception ex) { ProgressText.Text = "导出中止"; Log("导出中止：" + ex.Message); MessageBox.Show(ex.Message, "导出错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { FinishOperation(); ConversationStore.Save(_conversations); }
    }
}
