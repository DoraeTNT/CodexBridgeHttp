using System.Diagnostics;
using System.Drawing;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace CodexProxyBridge;

internal sealed class StatusForm : Form
{
    private readonly BridgeSettings _settings;
    private readonly CancellationTokenSource _shutdown;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly NotifyIcon _trayIcon;
    private readonly StatusRow _upstreamRow;
    private readonly StatusRow _relayRow;
    private readonly StatusRow _transportRow;
    private readonly StatusRow _codexRow;
    private readonly StatusRow _latencyRow;
    private readonly Label _summary;
    private readonly TextBox _logBox;
    private readonly ToolTip _toolTip;
    private bool _allowClose;
    private bool _exitInProgress;
    private bool _latencyProbeRunning;
    private DateTimeOffset _nextLatencyProbe = DateTimeOffset.MinValue;
    private string _lastLogText = "";

    public StatusForm(BridgeSettings settings, CancellationTokenSource shutdown)
    {
        _settings = settings;
        _shutdown = shutdown;

        Text = $"Codex 固定代理桥 · {Program.BuildVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 720);
        Size = new Size(780, 780);
        BackColor = Color.FromArgb(244, 247, 251);
        Font = new Font("Microsoft YaHei UI", 10F);
        Icon = SystemIcons.Shield;

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 92,
            BackColor = Color.FromArgb(24, 35, 56),
            Padding = new Padding(24, 16, 24, 12)
        };
        var title = new Label
        {
            AutoSize = true,
            Text = "Codex · 固定代理桥",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            Location = new Point(22, 14)
        };
        _summary = new Label
        {
            AutoSize = true,
            Text = "正在初始化中转服务…",
            ForeColor = Color.FromArgb(190, 205, 225),
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            Location = new Point(24, 55)
        };
        header.Controls.Add(title);
        header.Controls.Add(_summary);

        var statusCard = new Panel
        {
            Dock = DockStyle.Top,
            Height = 285,
            BackColor = Color.White,
            Padding = new Padding(18, 12, 18, 12),
            Margin = new Padding(16)
        };
        statusCard.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(190, 205, 225));
            var rect = new Rectangle(0, 0, statusCard.ClientSize.Width - 1, statusCard.ClientSize.Height - 1);
            e.Graphics.DrawRectangle(pen, rect);
        };
        _upstreamRow = new StatusRow("上游代理", "检查中…");
        _latencyRow = new StatusRow("代理延迟", "等待检测…");
        _relayRow = new StatusRow("固定中转", "检查中…");
        _transportRow = new StatusRow("Codex 传输", "检查中…");
        _codexRow = new StatusRow("Codex", "检查中…");
        foreach (var row in new[] { _codexRow, _transportRow, _relayRow, _latencyRow, _upstreamRow })
        {
            row.Dock = DockStyle.Top;
            statusCard.Controls.Add(row);
        }

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(16, 10, 16, 6),
            BackColor = Color.FromArgb(244, 247, 251),
            WrapContents = false
        };
        actions.Controls.Add(CreateButton("启动 Codex", (_, _) => Program.StartCodexWithProxy(_settings)));
        actions.Controls.Add(CreateButton("立即刷新", (_, _) => RefreshStatus()));
        actions.Controls.Add(CreateButton("打开日志", (_, _) => OpenFile(Program.BridgeLogPath)));
        var exitButton = CreateButton("退出中转", (_, _) => ExitBridge(), danger: true);
        actions.Controls.Add(exitButton);
        _toolTip = new ToolTip
        {
            AutoPopDelay = 8000,
            InitialDelay = 350,
            ReshowDelay = 100,
            ShowAlways = true
        };
        _toolTip.SetToolTip(exitButton, "退出中转，并同时关闭 Codex");

        var logTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(18, 5, 0, 0),
            Text = "最近运行日志",
            ForeColor = Color.FromArgb(45, 55, 72),
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            BackColor = Color.White
        };
        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(250, 251, 253),
            ForeColor = Color.FromArgb(45, 55, 72),
            Font = new Font("Consolas", 9.5F),
            WordWrap = false
        };

        var logPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 0, 18, 16),
            BackColor = Color.White
        };
        logPanel.Controls.Add(_logBox);

        Controls.Add(logPanel);
        Controls.Add(logTitle);
        Controls.Add(actions);
        Controls.Add(statusCard);
        Controls.Add(header);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "Codex 固定代理桥",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();

        FormClosing += HandleFormClosing;
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
                HideToTray();
        };

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();
        Shown += (_, _) => RefreshStatus();
    }

    public void RequestClose()
    {
        if (IsDisposed)
            return;
        void CloseNow()
        {
            _allowClose = true;
            _refreshTimer.Stop();
            _trayIcon.Visible = false;
            Close();
        }
        if (InvokeRequired)
            BeginInvoke(CloseNow);
        else
            CloseNow();
    }

    private void RefreshStatus()
    {
        try
        {
            var codexRunning = IsProcessRunningByPath(
                Path.GetFileNameWithoutExtension(_settings.CodexExe),
                _settings.CodexExe);
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            var fixedListening = listeners.Any(endpoint =>
                endpoint.Address.Equals(System.Net.IPAddress.Loopback) && endpoint.Port == _settings.FixedPort);
            var upstreamPort = Program.LatestUpstreamPort;
            var upstreamListening = upstreamPort > 0 &&
                                    listeners.Any(endpoint => endpoint.Port == upstreamPort);

            _upstreamRow.Set(
                upstreamListening,
                upstreamPort > 0
                    ? $"127.0.0.1:{upstreamPort}" + (upstreamListening ? " · 可用" : " · 未监听") + " · " + Program.UpstreamSourceDescription
                    : "等待发现本地代理…");
            _relayRow.Set(fixedListening,
                $"127.0.0.1:{_settings.FixedPort} · Codex 纯 TCP" + (fixedListening ? " · 正在监听" : " · 未启动"));
            var proxyEnvironmentInjected = Program.CodexProxyEnvironmentInjected;
            var modelCatalogInjected = Program.HttpOnlyConfigActive;
            var codexProxyPort = _settings.FixedPort;
            _transportRow.Set(proxyEnvironmentInjected && modelCatalogInjected,
                proxyEnvironmentInjected && modelCatalogInjected
                    ? $"HTTPS/SSE → 127.0.0.1:{codexProxyPort} · WebSocket 已跳过"
                    : codexRunning
                        ? "当前 Codex 未完成模型目录注入，请由中转重新启动"
                        : "等待中转启动 Codex");
            _codexRow.Set(codexRunning, codexRunning ? "运行中" : "未运行");

            if (!_latencyProbeRunning && DateTimeOffset.UtcNow >= _nextLatencyProbe)
                _ = RefreshNodeLatencyAsync();

            var allReady = upstreamListening && fixedListening &&
                           proxyEnvironmentInjected && modelCatalogInjected && codexRunning;
            _summary.Text = allReady
                ? "全部服务正常，Codex 正通过固定代理连接（HTTPS/SSE）。"
                : "部分服务尚未就绪，请查看下方状态。";
            _summary.ForeColor = allReady
                ? Color.FromArgb(116, 214, 151)
                : Color.FromArgb(241, 196, 102);

            RefreshLog();
        }
        catch (Exception ex)
        {
            _summary.Text = $"状态刷新失败：{ex.Message}";
            _summary.ForeColor = Color.FromArgb(255, 150, 150);
        }
    }

    private async Task RefreshNodeLatencyAsync()
    {
        _latencyProbeRunning = true;
        _nextLatencyProbe = DateTimeOffset.UtcNow.AddSeconds(8);
        try
        {
            var upstreamPort = Program.LatestUpstreamPort;
            if (upstreamPort <= 0)
            {
                _latencyRow.Set(false, "等待上游就绪");
                return;
            }

            _latencyRow.Set(false, $"127.0.0.1:{upstreamPort} · 检测中…");
            var latency = await TcpLatencyProbe.MeasureAsync(
                "127.0.0.1",
                upstreamPort,
                TimeSpan.FromSeconds(4),
                _shutdown.Token);
            if (IsDisposed)
                return;

            if (latency is long milliseconds)
            {
                var quality = milliseconds switch
                {
                    < 20 => "极快",
                    < 50 => "优秀",
                    < 120 => "良好",
                    < 250 => "较慢",
                    _ => "延迟很高"
                };
                _latencyRow.Set(
                    milliseconds < 250,
                    $"{milliseconds} ms · {quality}");
            }
            else
            {
                _latencyRow.Set(false, "连接超时");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed)
                _latencyRow.Set(false, $"检测失败：{ex.Message}");
        }
        finally
        {
            _latencyProbeRunning = false;
        }
    }

    private void RefreshLog()
    {
        try
        {
            if (!File.Exists(Program.BridgeLogPath))
                return;
            var lines = ReadLastLines(Program.BridgeLogPath, 14);
            var text = string.Join(Environment.NewLine, lines);
            if (text == _lastLogText)
                return;
            _lastLogText = text;
            _logBox.Text = text;
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
        catch
        {
            // The bridge may be appending while the UI refreshes.
        }
    }

    private static IEnumerable<string> ReadLastLines(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var queue = new Queue<string>(count);
        while (reader.ReadLine() is { } line)
        {
            if (queue.Count == count)
                queue.Dequeue();
            queue.Enqueue(line);
        }
        return queue;
    }

    private static bool IsProcessRunningByPath(string processName, string expectedPath)
    {
        var storeInstall = Program.IsMicrosoftStoreCodex(expectedPath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (storeInstall ||
                    string.Equals(
                        process.MainModule?.FileName,
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                if (storeInstall)
                    return true;
            }
            finally { process.Dispose(); }
        }
        return false;
    }

    private static Button CreateButton(string text, EventHandler handler, bool danger = false)
    {
        var button = new Button
        {
            AutoSize = false,
            Width = 96,
            Height = 34,
            Margin = new Padding(0, 0, 8, 0),
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = danger ? Color.FromArgb(255, 241, 242) : Color.White,
            ForeColor = danger ? Color.FromArgb(190, 55, 65) : Color.FromArgb(45, 75, 125),
            Font = new Font("Microsoft YaHei UI", 10F),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = danger
            ? Color.FromArgb(245, 170, 175)
            : Color.FromArgb(140, 160, 190);
        button.Click += handler;
        return button;
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示状态", null, (_, _) => RestoreWindow());
        menu.Items.Add("启动 Codex", null, (_, _) => Program.StartCodexWithProxy(_settings));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出中转", null, (_, _) => ExitBridge());
        return menu;
    }

    private void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose)
        {
            _trayIcon.Visible = false;
            return;
        }
        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        _trayIcon.ShowBalloonTip(
            1500,
            "固定代理仍在运行",
            "双击托盘图标可重新打开状态窗口。",
            ToolTipIcon.Info);
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private async void ExitBridge()
    {
        if (_exitInProgress)
            return;

        var confirm = MessageBox.Show(
            "退出中转将同时关闭 Codex。\n\n确定继续吗？",
            "确认完整退出",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
            return;

        _exitInProgress = true;
        _refreshTimer.Stop();
        _summary.Text = "正在关闭 Codex 和中转服务…";
        _summary.ForeColor = Color.FromArgb(241, 196, 102);
        UseWaitCursor = true;

        try
        {
            Program.MarkFullShutdownRequested();
            await ManagedApplicationShutdown.CloseAsync(_settings, Program.WriteLog);
        }
        finally
        {
            _allowClose = true;
            _trayIcon.Visible = false;
            _shutdown.Cancel();
            Close();
        }
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal static class ManagedApplicationShutdown
{
    public static async Task CloseAsync(BridgeSettings settings, Action<string> log)
    {
        await CloseMatchingProcessesAsync(
            Path.GetFileNameWithoutExtension(settings.CodexExe),
            settings.CodexExe,
            "Codex",
            log);
    }

    private static async Task CloseMatchingProcessesAsync(
        string processName,
        string expectedExecutable,
        string displayName,
        Action<string> log)
    {
        var matches = new List<Process>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (Program.IsMicrosoftStoreCodex(expectedExecutable) ||
                    string.Equals(
                        process.MainModule?.FileName,
                        expectedExecutable,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        if (matches.Count == 0)
        {
            log($"{displayName} 未运行，无需关闭。");
            return;
        }

        // Ask window-owning processes to close first so they can flush state.
        foreach (var process in matches.OrderByDescending(item => item.MainWindowHandle != IntPtr.Zero))
        {
            try
            {
                if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
                    process.CloseMainWindow();
            }
            catch { }
        }

        var gracefulDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < gracefulDeadline && matches.Any(IsStillRunning))
            await Task.Delay(200);

        foreach (var process in matches)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        }
        log($"已关闭 {displayName}。");
    }

    private static bool IsStillRunning(Process process)
    {
        try { return !process.HasExited; }
        catch { return false; }
    }
}

internal sealed class StatusRow : Panel
{
    private readonly Label _dot;
    private readonly Label _detail;

    public StatusRow(string name, string initialDetail)
    {
        Height = 53;
        BackColor = Color.White;
        Padding = new Padding(4, 4, 4, 4);

        _dot = new Label
        {
            Text = "●",
            AutoSize = true,
            ForeColor = Color.FromArgb(165, 175, 190),
            Font = new Font("Segoe UI Symbol", 12F),
            Location = new Point(4, 15)
        };
        var nameLabel = new Label
        {
            Text = name,
            AutoSize = false,
            Width = 125,
            Height = 34,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(35, 45, 60),
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            Location = new Point(29, 8)
        };
        _detail = new Label
        {
            Text = initialDetail,
            AutoEllipsis = true,
            Height = 34,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(60, 72, 90),
            Font = new Font("Microsoft YaHei UI", 10F),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Location = new Point(160, 8),
            Width = 520
        };
        Controls.Add(_dot);
        Controls.Add(nameLabel);
        Controls.Add(_detail);
    }

    public void Set(bool healthy, string detail)
    {
        _dot.ForeColor = healthy
            ? Color.FromArgb(34, 156, 92)
            : Color.FromArgb(220, 130, 40);
        _detail.Text = detail;
    }
}
