using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CodexProxyBridge;

internal static class Program
{
    // AppName 保持旧名以兼容历史 %LOCALAPPDATA%\CodexGreenHubBridge 下已保存的
    // 识别路径、日志与会话恢复文件；对外展示名已改为通用代理桥。
    private const string AppName = "CodexGreenHubBridge";
    internal const string BuildVersion = "2.6.0-universal-upstream";
    private static readonly string AppDataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    private static readonly string LogPath = Path.Combine(AppDataDirectory, "bridge.log");
    private static readonly string CliShimMarkerPath =
        Path.Combine(AppDataDirectory, "cli-shim-active.txt");
    private static readonly object LogLock = new();
    private static CodexModelCatalogConfigGuard? _codexConfigGuard;
    private static UserProxyEnvironmentSession? _storeProxyEnvironmentSession;
    private static bool _fullShutdownRequested;
    private static int _latestUpstreamPort;
    private static int _httpOnlyConfigActive;
    private static int _codexProxyEnvironmentInjected;
    private static int _storeActivationMode;
    private static string _upstreamSource = "等待发现本地代理…";
    private const string CliShimModeVariable = "CODEX_GREENHUB_CLI_SHIM";
    private const string RealCodexCliVariable = "CODEX_GREENHUB_REAL_CLI";
    private const string HttpModelCatalogVariable = "CODEX_GREENHUB_HTTP_MODEL_CATALOG";
    internal static string BridgeLogPath => LogPath;
    internal static bool HttpOnlyConfigActive => Volatile.Read(ref _httpOnlyConfigActive) != 0;
    internal static bool CodexProxyEnvironmentInjected =>
        Volatile.Read(ref _codexProxyEnvironmentInjected) != 0;
    internal static bool StoreActivationMode =>
        Volatile.Read(ref _storeActivationMode) != 0;
    internal static int LatestUpstreamPort => Volatile.Read(ref _latestUpstreamPort);
    internal static string UpstreamSourceDescription => Volatile.Read(ref _upstreamSource);
    internal static void WriteLog(string message) => Log(message);
    internal static void MarkFullShutdownRequested() => _fullShutdownRequested = true;

    internal static void SetUpstreamSource(string description) =>
        Volatile.Write(ref _upstreamSource, description);

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(CliShimModeVariable),
                "1",
                StringComparison.Ordinal))
        {
            return await RunCodexCliShimAsync(args);
        }

        Directory.CreateDirectory(AppDataDirectory);
        var options = LaunchOptions.Parse(args);

        if (options.ShowHelp)
        {
            ShowMessage(LaunchOptions.HelpText, "Codex 固定代理桥");
            return 0;
        }

        var mutexName = options.AllowMultiple
            ? $@"Local\CodexGreenHubBridge.Test.{Environment.ProcessId}"
            : @"Local\CodexGreenHubBridge.Singleton";
        using var mutex = new Mutex(true, mutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            ShowMessage("固定端口中转已经在运行。", AppName);
            return 0;
        }

        if (options.SelfTestConfig)
            return CodexConfigGuard.RunSelfTest(Log);

        CodexConfigGuard.RecoverStale(Log);
        CodexModelCatalogConfigGuard.RecoverStale(Log);
        BridgeSettings settings;
        try
        {
            settings = BridgeSettings.LoadAndResolve();
        }
        catch (Exception ex)
        {
            Log($"程序位置解析失败：{ex}");
            ShowMessage(
                $"无法确定 Codex 的程序位置：\n\n{ex.Message}\n\n" +
                $"请重新运行并在弹出的窗口中选择程序。\n日志：{LogPath}",
                AppName);
            return 1;
        }
        using var shutdown = new CancellationTokenSource();
        StatusForm? statusForm = null;
        Thread? uiThread = null;

        if (!options.Headless)
        {
            using var uiReady = new ManualResetEventSlim(false);
            uiThread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                statusForm = new StatusForm(settings, shutdown);
                uiReady.Set();
                Application.Run(statusForm);
            })
            {
                IsBackground = true,
                Name = "CodexGreenHubBridge.UI"
            };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            uiReady.Wait();
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreBestEffort(settings);

        try
        {
            Log(
                $"启动版本={BuildVersion}。固定端口={settings.FixedPort}，" +
                $"上游端口={(settings.UpstreamPort is > 0 ? settings.UpstreamPort.Value.ToString() : "自动发现")}");

            if (options.Diagnose)
            {
                return Diagnose(settings);
            }

            var relay = new RawTcpRelay(
                IPAddress.Loopback,
                settings.FixedPort,
                () => Volatile.Read(ref _latestUpstreamPort),
                Log);
            await relay.StartAsync(shutdown.Token);
            Log($"Codex 专用纯 TCP 中转已监听 127.0.0.1:{settings.FixedPort}");

            var initialPort = await DiscoverUpstreamAsync(settings, shutdown.Token);
            Volatile.Write(ref _latestUpstreamPort, initialPort);
            Log($"上游 HTTP 代理：127.0.0.1:{initialPort}（{UpstreamSourceDescription}）");

            // 等待固定中转 → 上游链路就绪（超时后仍继续启动，中转会自动等待上游）
            try
            {
                var readyDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
                while (DateTimeOffset.UtcNow < readyDeadline)
                {
                    shutdown.Token.ThrowIfCancellationRequested();
                    if (await ProxyHealthProbe.CheckAsync(
                            settings.FixedPort,
                            TimeSpan.FromSeconds(6),
                            shutdown.Token))
                    {
                        Log($"固定代理 127.0.0.1:{settings.FixedPort} 已通过 ChatGPT 全链路检查。");
                        break;
                    }
                    await Task.Delay(500, shutdown.Token);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                Log("固定代理全链路检查超时，Codex 启动后中转会持续等待上游恢复。");
            }

            if (!options.RelayOnly)
            {
                StartCodexWithProxy(settings);
            }

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            if (options.ExitAfterSeconds is > 0)
            {
                lifetime.CancelAfter(TimeSpan.FromSeconds(options.ExitAfterSeconds.Value));
            }

            await MonitorAsync(settings, options, lifetime.Token);
            shutdown.Cancel();
            await Task.WhenAll(
                relay.Completion);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Log("收到退出信号。");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"致命错误：{ex}");
            ShowMessage($"启动失败：{ex.Message}\n\n日志：{LogPath}", AppName);
            return 1;
        }
        finally
        {
            RestoreBestEffort(settings);
            statusForm?.RequestClose();
            uiThread?.Join(2000);
        }
    }

    private static int Diagnose(BridgeSettings settings)
    {
        var issues = new List<string>();
        if (!File.Exists(settings.CodexExe))
            issues.Add($"找不到 Codex：{settings.CodexExe}");
        if (IsTcpPortListening(settings.FixedPort))
            issues.Add($"固定端口 {settings.FixedPort} 已被占用");

        string upstreamDescription;
        if (settings.UpstreamPort is > 0)
        {
            var configured = settings.UpstreamPort.Value;
            var ok = Task.Run(async () => await ProxyHealthProbe.CheckAsync(
                    configured,
                    TimeSpan.FromSeconds(6),
                    CancellationToken.None))
                .GetAwaiter().GetResult();
            upstreamDescription = ok
                ? $"127.0.0.1:{configured}（可用）"
                : $"127.0.0.1:{configured}（不可用）";
            if (!ok)
                issues.Add($"配置的上游端口 {configured} 未通过 ChatGPT 全链路检查");
        }
        else
        {
            var detected = Task.Run(async () => await UpstreamDetector.DiscoverAsync(
                    settings,
                    Log,
                    CancellationToken.None))
                .GetAwaiter().GetResult();
            upstreamDescription = detected is int port
                ? $"127.0.0.1:{port}（自动发现 · {UpstreamSourceDescription}）"
                : "未发现可用本地 HTTP 代理";
            if (detected is null)
                issues.Add("未发现可用的本地 HTTP 代理，请先启动代理软件或配置 UpstreamPort");
        }

        var report =
            $"Codex：{settings.CodexExe}\n" +
            $"Codex 固定代理：127.0.0.1:{settings.FixedPort}（纯 TCP）\n" +
            $"上游代理：{upstreamDescription}\n\n" +
            (issues.Count == 0 ? "检查通过。" : "发现问题：\n- " + string.Join("\n- ", issues));

        Log(report.ReplaceLineEndings(" | "));
        ShowMessage(report, "Codex 固定代理桥诊断");
        return issues.Count == 0 ? 0 : 2;
    }

    private static async Task MonitorAsync(BridgeSettings settings, LaunchOptions options, CancellationToken cancellationToken)
    {
        var codexWasSeen = options.RelayOnly;
        DateTimeOffset? codexGoneSince = null;
        DateTimeOffset? upstreamUnhealthySince = null;
        var nextUpstreamHealthCheck = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        var lastUpstreamSource = "";

        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var activePort = Volatile.Read(ref _latestUpstreamPort);
            var upstreamListening = activePort > 0 && IsTcpPortListening(activePort);

            if (!upstreamListening)
            {
                upstreamUnhealthySince ??= now;
                var currentSource = UpstreamSourceDescription;
                if (lastUpstreamSource != currentSource)
                {
                    lastUpstreamSource = currentSource;
                    Log($"上游 127.0.0.1:{activePort} 已断开，正在重新发现可用代理…");
                }
            }
            else if (now >= nextUpstreamHealthCheck)
            {
                nextUpstreamHealthCheck = now + TimeSpan.FromSeconds(15);
                var healthy = await ProxyHealthProbe.CheckAsync(
                    activePort,
                    TimeSpan.FromSeconds(6),
                    cancellationToken);
                if (healthy)
                {
                    upstreamUnhealthySince = null;
                }
                else
                {
                    upstreamUnhealthySince ??= now;
                    if (now - upstreamUnhealthySince.Value >= TimeSpan.FromSeconds(30))
                    {
                        Log($"上游 127.0.0.1:{activePort} 连续全链路失败，开始重新发现上游。");
                        var discovered = await UpstreamDetector.DiscoverAsync(
                            settings,
                            Log,
                            cancellationToken);
                        if (discovered is int newPort && newPort != activePort)
                        {
                            Volatile.Write(ref _latestUpstreamPort, newPort);
                            upstreamUnhealthySince = null;
                            lastUpstreamSource = UpstreamSourceDescription;
                            Log($"上游已切换为 127.0.0.1:{newPort}（{UpstreamSourceDescription}）。");
                        }
                        else if (discovered is null)
                        {
                            SetUpstreamSource("上游不可用，等待代理软件恢复…");
                            Log("未发现可用上游，保持等待；新的 Codex 连接会等待可用端口。");
                        }
                    }
                }
            }

            if (!options.RelayOnly)
            {
                var codexRunning = IsCodexRunning(settings.CodexExe);
                codexWasSeen |= codexRunning;
                if (codexWasSeen && !codexRunning)
                {
                    codexGoneSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - codexGoneSince > TimeSpan.FromSeconds(8))
                    {
                        Log("Codex 已退出，中转程序退出。");
                        return;
                    }
                }
                else
                {
                    codexGoneSince = null;
                }
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    /// <summary>
    /// 在超时时间内发现可用的本地 HTTP 代理端口；仍失败则请求用户手动输入。
    /// </summary>
    private static async Task<int> DiscoverUpstreamAsync(
        BridgeSettings settings,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(settings.UpstreamReadyTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var port = await UpstreamDetector.DiscoverAsync(settings, Log, cancellationToken);
            if (port is int found && found > 0)
                return found;
            Log("尚未发现可用的本地 HTTP 代理，3 秒后重试。");
            await Task.Delay(3000, cancellationToken);
        }

        var userPort = PromptForPort(
            "未能在超时时间内自动发现可用代理。\n\n" +
            "请确认代理软件已启动，并输入它的本地 HTTP 代理端口：\n" +
            "Clash Verge / Mihomo = 7897，Clash for Windows = 7890，V2rayN = 10809，Netch = 2802");
        if (userPort is > 0)
        {
            SetUpstreamSource($"用户手动指定端口 {userPort.Value}");
            return userPort.Value;
        }
        throw new TimeoutException(
            "未能确定上游 HTTP 代理端口。请先启动代理软件并开启系统代理，" +
            "或在配置文件中填写 UpstreamPort 后重新启动。");
    }

    private static int? PromptForPort(string message)
    {
        using var dialog = new Form
        {
            Text = "指定上游代理端口",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(460, 150),
            TopMost = true
        };
        var label = new Label
        {
            Text = message,
            Location = new Point(18, 14),
            Size = new Size(424, 62),
            AutoEllipsis = true
        };
        var input = new TextBox
        {
            Location = new Point(18, 84),
            Size = new Size(424, 26),
            BorderStyle = BorderStyle.FixedSingle
        };
        var confirm = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Location = new Point(316, 116),
            Size = new Size(126, 28)
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(228, 116),
            Size = new Size(80, 28)
        };
        dialog.Controls.Add(label);
        dialog.Controls.Add(input);
        dialog.Controls.Add(confirm);
        dialog.Controls.Add(cancel);
        dialog.AcceptButton = confirm;
        dialog.CancelButton = cancel;
        input.Focus();

        return dialog.ShowDialog() == DialogResult.OK &&
               int.TryParse(input.Text.Trim(), out var port) &&
               port is > 0 and <= 65535
            ? port
            : null;
    }

    internal static void StartCodexWithProxy(BridgeSettings settings)
    {
        if (IsCodexRunning(settings.CodexExe))
        {
            Log("Codex 已经在运行；当前进程不会继承中转代理环境，请下次由中转启动。");
            return;
        }

        var microsoftStoreInstall = IsMicrosoftStoreCodex(settings.CodexExe);
        if (!microsoftStoreInstall && !File.Exists(settings.CodexExe))
            throw new FileNotFoundException("找不到 Codex 桌面主程序", settings.CodexExe);
        if (microsoftStoreInstall)
            LogStorePackageAccess(settings.CodexExe);
        var startInfo = new ProcessStartInfo(settings.CodexExe)
        {
            WorkingDirectory = Path.GetDirectoryName(settings.CodexExe)!,
            UseShellExecute = false
        };
        // Owl/Chromium can otherwise relaunch itself through an unelevated broker on
        // Windows. That replacement process loses the custom proxy environment.
        startInfo.ArgumentList.Add("--do-not-de-elevate");
        CodexProxyEnvironment.Apply(startInfo, settings.FixedPort);
        var modelCatalogConfigured = false;
        if (microsoftStoreInstall)
        {
            var modelCatalog = PrepareStoreHttpModelCatalog();
            if (modelCatalog is not null)
            {
                try
                {
                    _codexConfigGuard?.Restore(Log);
                    _codexConfigGuard =
                        CodexModelCatalogConfigGuard.Apply(modelCatalog, Log);
                    modelCatalogConfigured = true;
                    Log(
                        "已按当前 Microsoft Store Codex 的 models_cache.json " +
                        "临时注入模型目录；现有桌面、宠物、Provider、插件和项目配置保持不变。");
                }
                catch (Exception ex)
                {
                    Log(
                        $"当前桌面端模型目录注入失败，本次仅使用代理环境继续启动：" +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        else
        {
            // 桌面版：通过 CLI 中转在启动参数中注入 HTTPS-only 模型目录，
            // 不修改 config.toml、不切换 Provider，Codex 直接使用 HTTPS/SSE 跳过 WebSocket。
            var realCli = Path.Combine(
                Path.GetDirectoryName(settings.CodexExe) ?? "",
                "resources",
                "codex.exe");
            if (File.Exists(realCli))
            {
                try
                {
                    var shim = PrepareCodexCliShim();
                    var shimDirectory = Path.GetDirectoryName(shim)!;
                    var modelCatalog = PrepareHttpModelCatalog(realCli);
                    if (modelCatalog is not null)
                    {
                        startInfo.Environment[CliShimModeVariable] = "1";
                        startInfo.Environment[RealCodexCliVariable] = realCli;
                        startInfo.Environment[HttpModelCatalogVariable] = modelCatalog;
                        var currentPath = startInfo.Environment.TryGetValue("PATH", out var existingPath)
                            ? existingPath
                            : null;
                        startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
                            ? shimDirectory
                            : shimDirectory + Path.PathSeparator + currentPath;
                        startInfo.Environment["CODEX_CLI_PATH"] = "codex-greenhub-cli-shim";
                        modelCatalogConfigured = true;
                        Log(
                            "已注入 Codex CLI 中转：HTTPS-only 模型目录，" +
                            "WebSocket 偏好已关闭（零写入 config.toml）。");
                        _ = VerifyCliShimActivationAsync();
                    }
                    else
                    {
                        Log("HTTPS-only 模型目录生成失败，本次仅注入代理环境。");
                    }
                }
                catch (Exception ex)
                {
                    Log(
                        $"CLI 中转注入失败，本次仅使用代理环境：{ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                Log("未找到 resources\\codex.exe，跳过 CLI 中转注入，仅使用代理环境。");
            }
        }
        int? launchedProcessId;
        if (microsoftStoreInstall)
        {
            var applicationUserModelId = ResolveStoreApplicationUserModelId(settings.CodexExe)
                ?? throw new InvalidOperationException(
                    "无法从 Microsoft Store 包注册信息解析 Codex AUMID。");
            try
            {
                _storeProxyEnvironmentSession?.Restore(Log);
                _storeProxyEnvironmentSession =
                    UserProxyEnvironmentSession.Apply(settings.FixedPort, Log);
                launchedProcessId = ActivateStoreApplication(applicationUserModelId);
            }
            catch
            {
                var environmentSession =
                    Interlocked.Exchange(ref _storeProxyEnvironmentSession, null);
                environmentSession?.Restore(Log);
                _codexConfigGuard?.Restore(Log);
                _codexConfigGuard = null;
                throw;
            }
            Log(
                $"已通过 Windows 官方包激活接口启动 Codex：" +
                $"{applicationUserModelId}；PID={launchedProcessId}");
            _ = RestoreStoreProxyEnvironmentAfterActivationAsync(launchedProcessId.Value);
        }
        else
        {
            var launched = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法创建 Codex 进程。");
            launchedProcessId = launched.Id;
        }
        Volatile.Write(ref _codexProxyEnvironmentInjected, 1);
        Volatile.Write(ref _storeActivationMode, microsoftStoreInstall ? 1 : 0);
        Volatile.Write(
            ref _httpOnlyConfigActive,
            microsoftStoreInstall && modelCatalogConfigured ? 1 : 0);
        Log(
            (microsoftStoreInstall
                ? $"已启动 Store Codex；进程继承临时 HTTP/HTTPS 代理环境：" +
                  $"http://127.0.0.1:{settings.FixedPort}；"
                : $"已启动 Codex 并注入 HTTP/HTTPS 代理环境（WSS 跟随 HTTPS_PROXY）：" +
                  $"http://127.0.0.1:{settings.FixedPort}；启动 PID={launchedProcessId}。") +
            (modelCatalogConfigured
                ? "WebSocket 偏好已关闭（HTTPS/SSE）。"
                : "未注入模型目录。"));
    }

    internal static bool IsMicrosoftStoreCodex(string executablePath)
    {
        var normalized = executablePath.Replace('/', '\\');
        return normalized.Contains(@"\Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase);
    }

    private static string? PrepareStoreHttpModelCatalog()
    {
        var codexHome = Path.GetDirectoryName(CodexConfigGuard.ConfigPath)
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        var cachePath = Path.Combine(codexHome, "models_cache.json");
        if (!File.Exists(cachePath))
        {
            Log(
                $"Microsoft Store 模型缓存尚不存在：{cachePath}。" +
                "请先正常启动一次 Codex 完成登录和模型同步；本次仍会继续启动。");
            return null;
        }

        try
        {
            var cachedRoot = JsonNode.Parse(File.ReadAllText(cachePath)) as JsonObject
                ?? throw new InvalidDataException("Store 模型缓存不是有效 JSON 对象。");
            var cachedModels = cachedRoot["models"] as JsonArray;
            if (cachedModels is null || cachedModels.Count == 0)
                throw new InvalidDataException("Store 模型缓存为空。");
            var root = cachedRoot.DeepClone() as JsonObject
                ?? throw new InvalidDataException("无法复制 Store 模型缓存。");
            var models = DisableWebSockets(root);
            var outputPath = Path.Combine(AppDataDirectory, "model-catalog-https.json");
            File.WriteAllText(
                outputPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Log(
                $"已按当前用户模型缓存生成 Store HTTPS-only 目录：{models} 个模型；" +
                $"保留缓存根字段：{string.Join(", ", root.Select(item => item.Key))}；" +
                "模型 slug、能力与排序字段均保持不变。");
            return outputPath;
        }
        catch (Exception ex) when (
            ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log($"读取 Microsoft Store 用户模型缓存失败，本次仅启用固定代理：{ex.Message}");
            return null;
        }
    }

    private static string? ResolveStoreApplicationUserModelId(string executablePath)
    {
        var normalized = Path.GetFullPath(executablePath).Replace('/', '\\');
        const string marker = @"\WindowsApps\";
        var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;
        var packageStart = markerIndex + marker.Length;
        var packageEnd = normalized.IndexOf('\\', packageStart);
        if (packageEnd <= packageStart)
            return null;
        var packageFullName = normalized[packageStart..packageEnd];
        var match = Regex.Match(
            packageFullName,
            @"^(?<name>.+?)_\d+\.\d+\.\d+\.\d+_[^_]+_[^_]*_(?<publisher>[^_]+)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;

        const string packagesKey =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\" +
            @"\CurrentVersion\AppModel\Repository\Packages";
        using var packageKey = Registry.CurrentUser.OpenSubKey(
            $@"{packagesKey}\{packageFullName}");
        var applicationId = packageKey?.GetSubKeyNames()
            .FirstOrDefault(name =>
                !name.Equals("Capabilities", StringComparison.OrdinalIgnoreCase));
        applicationId ??= "App";
        return
            $"{match.Groups["name"].Value}_{match.Groups["publisher"].Value}!{applicationId}";
    }

    private static int ActivateStoreApplication(string applicationUserModelId)
    {
        object? activationManagerObject = null;
        try
        {
            var activationManagerType = Type.GetTypeFromCLSID(
                new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"),
                throwOnError: true)
                ?? throw new InvalidOperationException(
                    "系统未提供 ApplicationActivationManager。");
            activationManagerObject = Activator.CreateInstance(activationManagerType)
                ?? throw new InvalidOperationException(
                    "无法创建 Windows 包激活管理器。");
            var activationManager =
                (IApplicationActivationManager)activationManagerObject;
            var result = activationManager.ActivateApplication(
                applicationUserModelId,
                null,
                ActivateOptions.None,
                out var processId);
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);
            if (processId == 0 || processId > int.MaxValue)
                throw new InvalidOperationException(
                    $"Windows 包激活接口返回了无效 PID：{processId}。");
            return (int)processId;
        }
        catch (COMException ex)
        {
            var hresult = unchecked((uint)ex.ErrorCode);
            throw new InvalidOperationException(
                $"Windows 包激活 Codex 失败（HRESULT=0x{hresult:X8}）：{ex.Message} " +
                "若 Codex 可从“开始”菜单正常启动，请把此 HRESULT 和中转日志反馈给开发者；" +
                "若从“开始”菜单也无法启动，则属于应用包注册、系统策略或安装问题，" +
                "请尝试“重置”或卸载后从 Microsoft Store 重新安装。中转不会修改 WindowsApps ACL。",
                ex);
        }
        finally
        {
            if (activationManagerObject is not null &&
                Marshal.IsComObject(activationManagerObject))
            {
                Marshal.FinalReleaseComObject(activationManagerObject);
            }
        }
    }

    private static void LogStorePackageAccess(string executablePath)
    {
        var cliPath = Path.Combine(
            Path.GetDirectoryName(executablePath) ?? "",
            "resources",
            "codex.exe");
        var mainReadable = CanOpenForRead(executablePath, out var mainError);
        var cliReadable = CanOpenForRead(cliPath, out var cliError);
        var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        Log(
            $"Microsoft Store 包权限检查：用户={currentUser}；" +
            $"主程序可读取={mainReadable}；CLI 可读取={cliReadable}。" +
            "WindowsApps 由 TrustedInstaller/AppX 保护，将使用 AUMID 激活，不修改目录 ACL。");
        if (!mainReadable && !string.IsNullOrWhiteSpace(mainError))
            Log($"Store 主程序读取限制：{mainError}");
        if (!cliReadable && !string.IsNullOrWhiteSpace(cliError))
            Log($"Store CLI 读取限制：{cliError}");
    }

    private static bool CanOpenForRead(string path, out string? error)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            error = null;
            return true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        {
            error = ex.Message;
            return false;
        }
    }

    private static async Task RestoreStoreProxyEnvironmentAfterActivationAsync(
        int activatedProcessId)
    {
        try
        {
            try
            {
                using var process = Process.GetProcessById(activatedProcessId);
                Log(
                    $"已确认 Windows 包激活返回进程：" +
                    $"PID={activatedProcessId}，名称={process.ProcessName}");
            }
            catch (Exception ex)
            {
                Log(
                    $"包激活已成功，但读取 PID={activatedProcessId} 状态失败：" +
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            // Give the desktop process time to spawn app-server so both inherit
            // the proxy environment captured during package activation.
            await Task.Delay(8_000);
        }
        catch
        {
            // Restoration below is mandatory even if process inspection fails.
        }
        finally
        {
            var session = Interlocked.Exchange(ref _storeProxyEnvironmentSession, null);
            session?.Restore(Log);
        }
    }

    private static async Task VerifyCliShimActivationAsync()
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (File.Exists(CliShimMarkerPath))
            {
                Volatile.Write(ref _httpOnlyConfigActive, 1);
                Log("已确认 Codex app-server 经过 CLI 中转，WebSocket 偏好已关闭。");
                return;
            }
            await Task.Delay(500);
        }

        Volatile.Write(ref _httpOnlyConfigActive, 0);
        Log(
            "警告：30 秒内未检测到 CLI 中转接管；当前 Codex 可能未继承启动环境，" +
            "HTTPS-only 状态不会显示为已生效。");
    }

    private static string PrepareCodexCliShim()
    {
        var source = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定中转程序路径。");
        var destination = Path.Combine(AppDataDirectory, "codex-greenhub-cli-shim.exe");
        File.Copy(source, destination, overwrite: true);
        if (File.Exists(CliShimMarkerPath))
            File.Delete(CliShimMarkerPath);
        Log(
            $"已准备 Codex CLI 中转命令：{Path.GetFileName(destination)}；" +
            "桌面版将通过 PATH 解析该命令。");
        return destination;
    }

    private static string? PrepareHttpModelCatalog(string realCodexCli)
    {
        var path = Path.Combine(AppDataDirectory, "model-catalog-https.json");
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var output = ReadCodexModelCatalog(realCodexCli);
                var root = ParseModelCatalog(output);
                var models = DisableWebSockets(root);
                File.WriteAllText(
                    path,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Log(
                    $"已生成 HTTPS-only 模型目录：{models} 个模型，Provider 与模型 ID 保持不变。" +
                    (attempt > 1 ? $"（第 {attempt} 次读取成功）" : ""));
                return path;
            }
            catch (Exception ex) when (
                ex is JsonException or InvalidDataException or InvalidOperationException or TimeoutException)
            {
                lastError = ex;
                Log($"Codex 模型目录第 {attempt} 次读取无效：{ex.Message}");
                if (attempt < 3)
                    Thread.Sleep(250);
            }
        }

        try
        {
            if (File.Exists(path))
            {
                var cached = ParseModelCatalog(File.ReadAllText(path));
                var models = DisableWebSockets(cached);
                File.WriteAllText(
                    path,
                    cached.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Log($"实时模型目录读取失败，已复用上次有效缓存：{models} 个模型。");
                return path;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            lastError = ex;
        }

        Log(
            $"警告：无法取得有效 Codex 模型目录，本次跳过 HTTPS-only 注入但继续启动。" +
            $"原因：{lastError?.Message ?? "未知错误"}");
        return null;
    }

    private static string ReadCodexModelCatalog(string realCodexCli)
    {
        var startInfo = new ProcessStartInfo(realCodexCli)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("debug");
        startInfo.ArgumentList.Add("models");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法读取 Codex 模型目录。");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("读取 Codex 模型目录超时。");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"读取 Codex 模型目录失败：{error.Trim()}");
        return output;
    }

    private static JsonObject ParseModelCatalog(string output)
    {
        var start = output.IndexOf("{\"models\"", StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidDataException("输出中未找到 models JSON 对象。");

        var bytes = Encoding.UTF8.GetBytes(output[start..]);
        var reader = new Utf8JsonReader(
            bytes,
            new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        return JsonNode.Parse(ref reader) as JsonObject
            ?? throw new InvalidDataException("Codex 模型目录不是有效 JSON 对象。");
    }

    private static int DisableWebSockets(JsonObject root)
    {
        var models = root["models"] as JsonArray;
        if (models is null || models.Count == 0)
            throw new InvalidDataException("Codex 模型目录为空。");
        foreach (var model in models.OfType<JsonObject>())
            model["prefer_websockets"] = false;
        return models.Count;
    }

    private static async Task<int> RunCodexCliShimAsync(string[] args)
    {
        var realCli = Environment.GetEnvironmentVariable(RealCodexCliVariable);
        var modelCatalog = Environment.GetEnvironmentVariable(HttpModelCatalogVariable);
        if (string.IsNullOrWhiteSpace(realCli) || !File.Exists(realCli) ||
            string.IsNullOrWhiteSpace(modelCatalog) || !File.Exists(modelCatalog))
            return 127;

        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            File.WriteAllText(
                CliShimMarkerPath,
                $"接管时间：{DateTimeOffset.Now:O}{Environment.NewLine}" +
                $"参数：{string.Join(' ', args)}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Log("Codex CLI 中转已实际接管 app-server；HTTPS-only 模型目录已注入。");
        }
        catch
        {
            // 标记仅用于诊断，不能影响 app-server 启动。
        }

        var startInfo = new ProcessStartInfo(realCli)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"model_catalog_json='{modelCatalog}'");
        foreach (var argument in args)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["CODEX_CLI_PATH"] = realCli;
        startInfo.Environment.Remove(CliShimModeVariable);

        using var child = Process.Start(startInfo);
        if (child is null)
            return 127;

        var standardInput = Console.OpenStandardInput();
        var standardOutput = Console.OpenStandardOutput();
        var standardError = Console.OpenStandardError();
        _ = ForwardStandardInputAsync(standardInput, child.StandardInput.BaseStream);
        var stdout = child.StandardOutput.BaseStream.CopyToAsync(standardOutput);
        var stderr = child.StandardError.BaseStream.CopyToAsync(standardError);
        await child.WaitForExitAsync();
        try { child.StandardInput.Close(); } catch { }
        await Task.WhenAll(stdout, stderr);
        return child.ExitCode;
    }

    private static async Task ForwardStandardInputAsync(Stream source, Stream destination)
    {
        try
        {
            await source.CopyToAsync(destination);
            await destination.FlushAsync();
            destination.Close();
        }
        catch
        {
            // The real CLI may exit before the desktop closes its stdin pipe.
        }
    }

    private static bool IsCodexRunning(string codexExe)
    {
        var expected = Path.GetFullPath(codexExe);
        var processName = Path.GetFileNameWithoutExtension(expected);
        var storeInstall = IsMicrosoftStoreCodex(expected);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (storeInstall ||
                    string.Equals(
                        process.MainModule?.FileName,
                        expected,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                if (storeInstall)
                    return true;
                // Some process metadata can be temporarily inaccessible during startup.
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }

    private static void RestoreBestEffort(BridgeSettings settings)
    {
        try
        {
            _codexConfigGuard?.Restore(Log);
            _codexConfigGuard = null;
            var environmentSession =
                Interlocked.Exchange(ref _storeProxyEnvironmentSession, null);
            environmentSession?.Restore(Log);
            Volatile.Write(ref _httpOnlyConfigActive, 0);
            Volatile.Write(ref _storeActivationMode, 0);
        }
        catch (Exception ex)
        {
            Log($"恢复 Codex 配置失败：{ex.Message}");
        }
    }

    internal static bool TryGetLoopbackProxyPort(string? server, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(server))
            return false;
        var match = Regex.Match(
            server,
            @"(?:^|[=;])(?:https?://)?(?:127\.0\.0\.1|localhost):(?<port>\d{1,5})(?:$|;)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success &&
               int.TryParse(match.Groups["port"].Value, out port) &&
               port is > 0 and <= 65535;
    }

    private static bool IsTcpPortListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);

    private static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never stop the bridge.
        }
    }

    private static void ShowMessage(string text, string caption) =>
        MessageBox(
            IntPtr.Zero,
            text,
            caption,
            0x00000040 | 0x00010000 | 0x00040000);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}

[Flags]
internal enum ActivateOptions
{
    None = 0,
    DesignMode = 0x1,
    NoErrorUi = 0x2,
    NoSplashScreen = 0x4
}

[ComImport]
[Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication(
        [MarshalAs(UnmanagedType.LPWStr)] string applicationUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
        ActivateOptions options,
        out uint processId);
}

internal static class ProxyHealthProbe
{
    private const string TargetHost = "chatgpt.com";
    private const int TargetPort = 443;

    public static async Task<bool> CheckAsync(
        int proxyPort,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var token = timeoutCts.Token;

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, token);
            using var stream = client.GetStream();

            var connectRequest = Encoding.ASCII.GetBytes(
                $"CONNECT {TargetHost}:{TargetPort} HTTP/1.1\r\n" +
                $"Host: {TargetHost}:{TargetPort}\r\n" +
                "Proxy-Connection: Keep-Alive\r\n\r\n");
            await stream.WriteAsync(connectRequest, token);

            var responseHeader = await ReadHeaderAsync(stream, token);
            if (!responseHeader.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
                !responseHeader.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
                return false;

            using var tls = new SslStream(stream, leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = TargetHost,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                },
                token);

            var headRequest = Encoding.ASCII.GetBytes(
                $"HEAD / HTTP/1.1\r\nHost: {TargetHost}\r\nConnection: close\r\n\r\n");
            await tls.WriteAsync(headRequest, token);
            var firstByte = new byte[1];
            return await tls.ReadAsync(firstByte, token) > 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (AuthenticationException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(512);
        var single = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(single, cancellationToken) == 0)
                break;
            bytes.Add(single[0]);
            var count = bytes.Count;
            if (count >= 4 &&
                bytes[count - 4] == '\r' &&
                bytes[count - 3] == '\n' &&
                bytes[count - 2] == '\r' &&
                bytes[count - 1] == '\n')
                break;
        }
        return Encoding.ASCII.GetString([.. bytes]);
    }
}

/// <summary>
/// 通用上游发现：按 用户配置 → 系统代理嗅探 → 常见端口探测 的顺序
/// 找到电脑上任意代理软件提供的本地 HTTP 代理端口。
/// </summary>
internal static class UpstreamDetector
{
    // 常见桌面代理软件本地 HTTP 端口（并行探测，命中即用）
    private static readonly (int Port, string Name)[] KnownProxies =
    [
        (7897, "Clash Verge / Mihomo"),
        (7890, "Clash for Windows"),
        (10809, "V2rayN HTTP"),
        (2802, "Netch HTTP"),
        (7891, "Clash 备用混合端口"),
        (2080, "通用 HTTP 代理"),
        (8888, "通用 HTTP 代理"),
        (8080, "通用 HTTP 代理"),
        (1080, "Shadowsocks 类（若支持 HTTP）")
    ];

    public static async Task<int?> DiscoverAsync(
        BridgeSettings settings,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        // 1) 用户显式指定端口
        if (settings.UpstreamPort is > 0)
        {
            var configured = settings.UpstreamPort.Value;
            if (await ProxyHealthProbe.CheckAsync(
                    configured,
                    TimeSpan.FromSeconds(6),
                    cancellationToken))
            {
                Program.SetUpstreamSource($"手动配置端口 {configured}");
                log($"使用配置指定上游：127.0.0.1:{configured}");
                return configured;
            }
            log($"配置指定端口 {configured} 未通过全链路检查，继续自动发现。");
        }

        // 2) 嗅探 Windows 系统代理（大多数 GUI 代理软件开启“系统代理”后会指向本机端口）
        var proxy = WindowsProxy.Capture();
        if (Program.TryGetLoopbackProxyPort(proxy.Server, out var systemPort) &&
            systemPort != settings.FixedPort)
        {
            if (await ProxyHealthProbe.CheckAsync(
                    systemPort,
                    TimeSpan.FromSeconds(6),
                    cancellationToken))
            {
                Program.SetUpstreamSource($"系统代理端口 {systemPort}");
                log($"检测到可用系统代理：127.0.0.1:{systemPort}（{proxy.Server}）");
                return systemPort;
            }
        }

        // 3) 常见端口并行全链路探测（把名称随结果一起返回，避免与过滤后的索引错位）
        var results = await Task.WhenAll(
            KnownProxies
                .Where(item => item.Port != settings.FixedPort)
                .Select(async item =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return (Port: 0, Name: item.Name);
                        var ok = await ProxyHealthProbe.CheckAsync(
                            item.Port,
                            TimeSpan.FromSeconds(4),
                            cancellationToken);
                        return ok ? (Port: item.Port, Name: item.Name) : (Port: 0, Name: item.Name);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return (Port: 0, Name: item.Name);
                    }
                }));
        foreach (var result in results)
        {
            if (result.Port > 0)
            {
                Program.SetUpstreamSource($"自动发现端口 {result.Port}（{result.Name}）");
                log($"自动发现上游代理：127.0.0.1:{result.Port}（{result.Name}）");
                return result.Port;
            }
        }

        Program.SetUpstreamSource("未发现可用本地 HTTP 代理");
        return null;
    }
}

internal static class TcpLatencyProbe
{
    public static async Task<long?> MeasureAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            using var client = new TcpClient();
            var stopwatch = Stopwatch.StartNew();
            await client.ConnectAsync(host, port, timeoutCts.Token);
            stopwatch.Stop();
            return stopwatch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}

internal static class UpstreamConnector
{
    public static async Task<(TcpClient Client, int Port)?> ConnectAsync(
        Func<int> upstreamPort,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + waitTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var port = upstreamPort();
            if (port > 0)
            {
                var client = new TcpClient();
                try
                {
                    using var attemptCts =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attemptCts.CancelAfter(TimeSpan.FromSeconds(1));
                    await client.ConnectAsync(
                        IPAddress.Loopback,
                        port,
                        attemptCts.Token);
                    return (client, port);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    client.Dispose();
                }
                catch (SocketException)
                {
                    client.Dispose();
                }
            }
            await Task.Delay(150, cancellationToken);
        }
        return null;
    }
}

internal sealed class RawTcpRelay
{
    private readonly TcpListener _listener;
    private readonly Func<int> _upstreamPort;
    private readonly Action<string> _log;
    private readonly List<Task> _connections = [];
    private readonly object _connectionsLock = new();
    private Task _completion = Task.CompletedTask;

    public RawTcpRelay(IPAddress address, int port, Func<int> upstreamPort, Action<string> log)
    {
        _listener = new TcpListener(address, port);
        _upstreamPort = upstreamPort;
        _log = log;
    }

    public Task Completion => _completion;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        _completion = AcceptLoopAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                var task = HandleConnectionAsync(client, cancellationToken);
                lock (_connectionsLock)
                {
                    _connections.RemoveAll(item => item.IsCompleted);
                    _connections.Add(task);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _listener.Stop();
            Task[] pending;
            lock (_connectionsLock)
                pending = [.. _connections];
            await Task.WhenAll(pending.Select(IgnoreFailureAsync));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var connection = await UpstreamConnector.ConnectAsync(
                    _upstreamPort,
                    TimeSpan.FromSeconds(12),
                    cancellationToken);
                if (connection is null)
                {
                    _log("Codex 中转等待上游恢复 12 秒后仍无可用代理。");
                    return;
                }
                using var upstream = connection.Value.Client;
                using var source = client.GetStream();
                using var destination = upstream.GetStream();
                var forward = source.CopyToAsync(destination, cancellationToken);
                var backward = destination.CopyToAsync(source, cancellationToken);
                await Task.WhenAny(forward, backward);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log($"Codex 纯 TCP 中转失败：{ex.Message}");
            }
        }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try { await task; } catch { }
    }
}

internal static class CodexProxyEnvironment
{
    public static void Apply(ProcessStartInfo startInfo, int fixedPort)
    {
        var proxy = $"http://127.0.0.1:{fixedPort}";
        startInfo.Environment["HTTP_PROXY"] = proxy;
        startInfo.Environment["HTTPS_PROXY"] = proxy;
        startInfo.Environment["http_proxy"] = proxy;
        startInfo.Environment["https_proxy"] = proxy;
        startInfo.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
        startInfo.Environment["no_proxy"] = "localhost,127.0.0.1,::1";

        // Codex's WSS implementation follows HTTPS_PROXY. Remove competing
        // websocket/general proxy variables so all libraries choose one path.
        foreach (var name in new[]
                 {
                     "WS_PROXY", "WSS_PROXY", "ws_proxy", "wss_proxy",
                     "ALL_PROXY", "all_proxy"
                 })
        {
            startInfo.Environment.Remove(name);
        }
    }
}

internal static class CodexUserConfigRepair
{
    private const string LegacyShimName = "codex-greenhub-cli-shim.exe";
    private static readonly Regex LegacyShimSettingRegex = new(
        @"(?mi)^[ \t]*CODEX_CLI_PATH[ \t]*=[ \t]*(['""])" +
        @"(?:[^'""\r\n]*[\\/])?codex-greenhub-cli-shim\.exe\1[ \t]*(?:\r?\n)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void RepairLegacyBridgeChanges(Action<string> log)
    {
        var configPath = CodexConfigGuard.ConfigPath;
        if (!File.Exists(configPath))
        {
            CleanupLegacyRuntimeFiles(log);
            return;
        }

        try
        {
            var current = File.ReadAllText(configPath);
            if (!LegacyShimSettingRegex.IsMatch(current))
            {
                CleanupLegacyRuntimeFiles(log);
                return;
            }

            var backupPath = configPath + ".backup";
            var canRestoreKnownGoodBackup = File.Exists(backupPath);
            string? backup = null;
            if (canRestoreKnownGoodBackup)
            {
                backup = File.ReadAllText(backupPath);
                canRestoreKnownGoodBackup =
                    backup.Length > current.Length &&
                    backup.Contains("selected-avatar-id", StringComparison.Ordinal) &&
                    !current.Contains("selected-avatar-id", StringComparison.Ordinal) &&
                    !LegacyShimSettingRegex.IsMatch(backup) &&
                    !backup.Contains(
                        "# BEGIN CodexGreenHubBridge",
                        StringComparison.Ordinal);
            }

            if (canRestoreKnownGoodBackup && backup is not null)
            {
                var recoveryCopy = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "CodexGreenHubBridge",
                    "codex-config-before-v2.7-repair.toml");
                Directory.CreateDirectory(Path.GetDirectoryName(recoveryCopy)!);
                File.Copy(configPath, recoveryCopy, overwrite: true);
                AtomicWrite(
                    configPath,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                        .GetBytes(backup));
                log(
                    "检测到旧版中转导致 Codex 配置被重建；已保存当前残缺配置并恢复 " +
                    $"config.toml.backup（包含宠物形象与原 GPT/Provider 设置）。恢复前副本：{recoveryCopy}");
            }
            else
            {
                var cleaned = LegacyShimSettingRegex.Replace(current, "");
                AtomicWrite(
                    configPath,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                        .GetBytes(cleaned));
                log(
                    "已从 Codex 用户配置中移除旧版中转遗留的 " +
                    "CODEX_CLI_PATH；其他设置保持不变。");
            }
        }
        catch (Exception ex)
        {
            log(
                $"清理旧版 Codex 配置副作用失败，将继续以零写入模式启动：" +
                $"{ex.GetType().Name}: {ex.Message}");
        }

        CleanupLegacyRuntimeFiles(log);
    }

    private static void CleanupLegacyRuntimeFiles(Action<string> log)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexGreenHubBridge");
        foreach (var name in new[]
                 {
                     "cli-shim-active.txt",
                     LegacyShimName,
                     "model-catalog-https.json",
                     "http-model-catalog.json"
                 })
        {
            try
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                log($"清理旧版运行文件 {name} 失败：{ex.Message}");
            }
        }
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temporaryPath =
            path + $".tmp.bridge-repair.{Environment.ProcessId}.{Guid.NewGuid():N}";
        File.WriteAllBytes(temporaryPath, bytes);
        try
        {
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

internal sealed class UserProxyEnvironmentSession
{
    private const string EnvironmentKey = "Environment";
    private static readonly string[] ManagedNames =
    [
        "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy",
        "NO_PROXY", "no_proxy",
        "WS_PROXY", "WSS_PROXY", "ws_proxy", "wss_proxy",
        "ALL_PROXY", "all_proxy"
    ];
    private readonly Dictionary<string, RegistryValueSnapshot> _original;
    private int _restored;

    private UserProxyEnvironmentSession(
        Dictionary<string, RegistryValueSnapshot> original)
    {
        _original = original;
    }

    public static UserProxyEnvironmentSession Apply(
        int fixedPort,
        Action<string> log)
    {
        var original = new Dictionary<string, RegistryValueSnapshot>(
            StringComparer.OrdinalIgnoreCase);
        using var key = Registry.CurrentUser.CreateSubKey(EnvironmentKey, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户环境变量注册表。");
        foreach (var name in ManagedNames)
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            original[name] = value is null
                ? new RegistryValueSnapshot(false, null, RegistryValueKind.String)
                : new RegistryValueSnapshot(true, value, key.GetValueKind(name));
        }

        var session = new UserProxyEnvironmentSession(original);
        try
        {
            var proxy = $"http://127.0.0.1:{fixedPort}";
            foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
                key.SetValue(name, proxy, RegistryValueKind.String);
            foreach (var name in new[] { "NO_PROXY", "no_proxy" })
                key.SetValue(name, "localhost,127.0.0.1,::1", RegistryValueKind.String);
            foreach (var name in new[]
                     {
                         "WS_PROXY", "WSS_PROXY", "ws_proxy", "wss_proxy",
                         "ALL_PROXY", "all_proxy"
                     })
                key.DeleteValue(name, throwOnMissingValue: false);
            NotifyEnvironmentChanged();
            log(
                $"已为 Microsoft Store 包激活临时设置用户代理环境：" +
                $"HTTPS_PROXY={proxy}；应用与 app-server 启动后将立即恢复注册表原值。");
            return session;
        }
        catch
        {
            session.Restore(log);
            throw;
        }
    }

    public void Restore(Action<string> log)
    {
        if (Interlocked.Exchange(ref _restored, 1) != 0)
            return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(EnvironmentKey, writable: true)
                ?? throw new InvalidOperationException("无法打开当前用户环境变量注册表。");
            foreach (var (name, snapshot) in _original)
            {
                if (snapshot.Existed)
                    key.SetValue(name, snapshot.Value!, snapshot.Kind);
                else
                    key.DeleteValue(name, throwOnMissingValue: false);
            }
            NotifyEnvironmentChanged();
            log("Microsoft Store Codex 已继承代理环境；用户环境变量已恢复原值。");
        }
        catch (Exception ex)
        {
            log($"恢复 Microsoft Store 临时代理环境失败：{ex.Message}");
        }
    }

    private static void NotifyEnvironmentChanged()
    {
        _ = SendMessageTimeout(
            new IntPtr(0xffff),
            0x001A,
            UIntPtr.Zero,
            "Environment",
            0x0002,
            2_000,
            out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);

    private sealed record RegistryValueSnapshot(
        bool Existed,
        object? Value,
        RegistryValueKind Kind);
}

internal static class WindowsProxy
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    public static ProxySnapshot Capture()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettings, writable: false);
        var enabled = Convert.ToInt32(key?.GetValue("ProxyEnable", 0)) != 0;
        return new ProxySnapshot(
            enabled,
            key?.GetValue("ProxyServer") as string,
            key?.GetValue("ProxyOverride") as string,
            key?.GetValue("AutoConfigURL") as string);
    }

    public static void Set(string server)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettings, writable: true);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", server, RegistryValueKind.String);
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        NotifyChanged();
    }

    public static void SetPac(string url)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettings, writable: true);
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        key.SetValue("AutoConfigURL", url, RegistryValueKind.String);
        NotifyChanged();
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettings, writable: true);
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        NotifyChanged();
    }

    public static void Restore(ProxySnapshot snapshot)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettings, writable: true);
        key.SetValue("ProxyEnable", snapshot.Enabled ? 1 : 0, RegistryValueKind.DWord);
        if (snapshot.Server is null)
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        else
            key.SetValue("ProxyServer", snapshot.Server, RegistryValueKind.String);
        if (snapshot.Override is null)
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
        else
            key.SetValue("ProxyOverride", snapshot.Override, RegistryValueKind.String);
        if (snapshot.AutoConfigUrl is null)
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        else
            key.SetValue("AutoConfigURL", snapshot.AutoConfigUrl, RegistryValueKind.String);
        NotifyChanged();
    }

    private static void NotifyChanged()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int bufferLength);
}

internal sealed record ProxySnapshot(
    bool Enabled,
    string? Server,
    string? Override,
    string? AutoConfigUrl);

internal sealed class CodexModelCatalogConfigGuard
{
    private const string BeginMarker = "# BEGIN CodexGreenHubBridge model catalog";
    private const string EndMarker = "# END CodexGreenHubBridge model catalog";
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexGreenHubBridge");
    private static readonly string StatePath =
        Path.Combine(StateDirectory, "codex-model-catalog-session.json");
    private static readonly string BackupPath =
        Path.Combine(StateDirectory, "codex-model-catalog-original.toml");
    private static readonly Regex ManagedBlockRegex = new(
        $@"(?ms)^[ \t]*{Regex.Escape(BeginMarker)}\r?\n.*?^[ \t]*{Regex.Escape(EndMarker)}(?:\r?\n)?",
        RegexOptions.Compiled);
    private static readonly Regex SectionRegex = new(
        @"(?m)^[ \t]*\[[^\r\n]+",
        RegexOptions.Compiled);
    private static readonly Regex TopLevelCatalogRegex = new(
        @"(?m)^[ \t]*model_catalog_json[ \t]*=[^\r\n]*(?:\r?\n)?",
        RegexOptions.Compiled);

    private readonly ConfigSessionState _state;
    private bool _restored;

    private CodexModelCatalogConfigGuard(ConfigSessionState state)
    {
        _state = state;
    }

    public static void RecoverStale(Action<string> log)
    {
        if (!File.Exists(StatePath))
            return;
        try
        {
            var state = JsonSerializer.Deserialize<ConfigSessionState>(
                File.ReadAllText(StatePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (state is null || string.IsNullOrWhiteSpace(state.ConfigPath))
                throw new InvalidDataException("Microsoft Store 模型配置恢复记录无效。");
            RestoreState(state, log, staleRecovery: true);
        }
        catch (Exception ex)
        {
            log($"检测到未完成的 Store 模型配置会话，但自动恢复失败：{ex.Message}");
        }
    }

    public static CodexModelCatalogConfigGuard Apply(
        string modelCatalogPath,
        Action<string> log)
    {
        if (!File.Exists(modelCatalogPath))
            throw new FileNotFoundException("找不到 HTTPS-only 模型目录。", modelCatalogPath);

        Directory.CreateDirectory(StateDirectory);
        var configPath = CodexConfigGuard.ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var originalExisted = File.Exists(configPath);
        var originalBytes = originalExisted
            ? File.ReadAllBytes(configPath)
            : Array.Empty<byte>();
        var originalText = originalExisted ? DecodeUtf8(originalBytes) : "";
        var patchedText = Patch(originalText, modelCatalogPath);
        var patchedBytes =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(patchedText);

        if (originalExisted)
            AtomicWrite(BackupPath, originalBytes);
        else
            File.Delete(BackupPath);

        var state = new ConfigSessionState(
            configPath,
            originalExisted,
            Convert.ToHexString(SHA256.HashData(patchedBytes)),
            DateTimeOffset.UtcNow);
        AtomicWrite(
            StatePath,
            JsonSerializer.SerializeToUtf8Bytes(
                state,
                new JsonSerializerOptions { WriteIndented = true }));

        try
        {
            AtomicWrite(configPath, patchedBytes);
        }
        catch
        {
            CleanupStateFiles();
            throw;
        }

        log(
            $"已为 Microsoft Store 版临时设置 model_catalog_json：" +
            $"{configPath}；未修改 Provider、账号或历史数据库。");
        return new CodexModelCatalogConfigGuard(state);
    }

    public void Restore(Action<string> log)
    {
        lock (this)
        {
            if (_restored)
                return;
            RestoreState(_state, log, staleRecovery: false);
            _restored = true;
        }
    }

    private static void RestoreState(
        ConfigSessionState state,
        Action<string> log,
        bool staleRecovery)
    {
        var currentExists = File.Exists(state.ConfigPath);
        var currentBytes = currentExists
            ? File.ReadAllBytes(state.ConfigPath)
            : Array.Empty<byte>();
        var currentHash = Convert.ToHexString(SHA256.HashData(currentBytes));
        if (currentExists &&
            string.Equals(currentHash, state.PatchedHash, StringComparison.OrdinalIgnoreCase))
        {
            RestoreOriginalExactly(state);
        }
        else if (!currentExists && state.OriginalExisted)
        {
            RestoreOriginalExactly(state);
        }
        else if (currentExists)
        {
            var originalText = state.OriginalExisted && File.Exists(BackupPath)
                ? DecodeUtf8(File.ReadAllBytes(BackupPath))
                : "";
            var cleaned = Unpatch(DecodeUtf8(currentBytes), originalText);
            AtomicWrite(
                state.ConfigPath,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(cleaned));
            log(
                "Codex 用户配置在运行期间发生变化；已仅移除中转的模型目录设置，" +
                "并保留其他修改。");
        }

        CleanupStateFiles();
        log(staleRecovery
            ? "检测到上次异常退出，已恢复 Microsoft Store 版 Codex 用户配置。"
            : "已恢复 Microsoft Store 版 Codex 启动前用户配置。");
    }

    private static void RestoreOriginalExactly(ConfigSessionState state)
    {
        if (state.OriginalExisted)
        {
            if (!File.Exists(BackupPath))
                throw new FileNotFoundException("找不到 Store 模型配置备份。", BackupPath);
            AtomicWrite(state.ConfigPath, File.ReadAllBytes(BackupPath));
        }
        else if (File.Exists(state.ConfigPath))
        {
            File.Delete(state.ConfigPath);
        }
    }

    private static string Patch(string original, string modelCatalogPath)
    {
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = ManagedBlockRegex.Replace(original, "");
        var sectionIndex = SectionRegex.Match(text) is { Success: true } section
            ? section.Index
            : text.Length;
        var prefix = text[..sectionIndex];
        var suffix = text[sectionIndex..];
        prefix = TopLevelCatalogRegex.Replace(prefix, "");

        var bom = prefix.StartsWith('\uFEFF') ? "\uFEFF" : "";
        if (bom.Length > 0)
            prefix = prefix[1..];
        var catalogValue = JsonSerializer.Serialize(Path.GetFullPath(modelCatalogPath));
        var block =
            BeginMarker + newline +
            $"model_catalog_json = {catalogValue}" + newline +
            EndMarker + newline;
        return bom + block + prefix + suffix;
    }

    private static string Unpatch(string current, string original)
    {
        var text = ManagedBlockRegex.Replace(current, "");
        var currentSectionIndex = SectionRegex.Match(text) is { Success: true } currentSection
            ? currentSection.Index
            : text.Length;
        var currentPrefix = text[..currentSectionIndex];
        if (TopLevelCatalogRegex.IsMatch(currentPrefix))
            return text;

        var originalSectionIndex = SectionRegex.Match(original) is { Success: true } originalSection
            ? originalSection.Index
            : original.Length;
        var originalCatalog = TopLevelCatalogRegex.Match(original[..originalSectionIndex]);
        if (!originalCatalog.Success)
            return text;

        var bom = text.StartsWith('\uFEFF') ? "\uFEFF" : "";
        if (bom.Length > 0)
            text = text[1..];
        return bom + originalCatalog.Value + text;
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        var offset = bytes.Length >= 3 &&
                     bytes[0] == 0xEF &&
                     bytes[1] == 0xBB &&
                     bytes[2] == 0xBF
            ? 3
            : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
        File.WriteAllBytes(temporaryPath, bytes);
        try
        {
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void CleanupStateFiles()
    {
        File.Delete(StatePath);
        File.Delete(BackupPath);
    }

    private sealed record ConfigSessionState(
        string ConfigPath,
        bool OriginalExisted,
        string PatchedHash,
        DateTimeOffset AppliedAt);
}

internal sealed class CodexConfigGuard
{
    private const string ProviderId = "codex-greenhub-http";
    private const string BeginMarker = "# BEGIN CodexGreenHubBridge HTTP-only";
    private const string EndMarker = "# END CodexGreenHubBridge HTTP-only";
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexGreenHubBridge");
    private static readonly string StatePath = Path.Combine(StateDirectory, "codex-config-session.json");
    private static readonly string BackupPath = Path.Combine(StateDirectory, "codex-config-original.toml");
    private static readonly Regex ManagedBlockRegex = new(
        $@"(?ms)^[ \t]*{Regex.Escape(BeginMarker)}\r?\n.*?^[ \t]*{Regex.Escape(EndMarker)}(?:\r?\n)?",
        RegexOptions.Compiled);
    private static readonly Regex SectionRegex = new(
        @"(?m)^[ \t]*\[[^\r\n]+",
        RegexOptions.Compiled);
    private static readonly Regex TopLevelProviderRegex = new(
        @"(?m)^[ \t]*model_provider[ \t]*=[^\r\n]*(?:\r?\n)?",
        RegexOptions.Compiled);

    private readonly ConfigSessionState _state;
    private bool _restored;

    private CodexConfigGuard(ConfigSessionState state)
    {
        _state = state;
    }

    public static string ConfigPath
    {
        get
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome))
            {
                codexHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex");
            }
            return Path.Combine(Path.GetFullPath(codexHome), "config.toml");
        }
    }

    public static void RecoverStale(Action<string> log)
    {
        if (!File.Exists(StatePath))
            return;

        try
        {
            var state = JsonSerializer.Deserialize<ConfigSessionState>(
                File.ReadAllText(StatePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (state is null || string.IsNullOrWhiteSpace(state.ConfigPath))
                throw new InvalidDataException("恢复记录无效。");

            RestoreState(state, log, staleRecovery: true);
        }
        catch (Exception ex)
        {
            log($"检测到未完成的 Codex 配置会话，但自动恢复失败：{ex.Message}");
        }
    }

    public static CodexConfigGuard Apply(Action<string> log)
    {
        Directory.CreateDirectory(StateDirectory);
        var configPath = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        var originalExisted = File.Exists(configPath);
        var originalBytes = originalExisted
            ? File.ReadAllBytes(configPath)
            : Array.Empty<byte>();
        var originalText = originalExisted
            ? DecodeUtf8(originalBytes)
            : "";
        var patchedText = Patch(originalText);
        var patchedBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(patchedText);

        if (originalExisted)
            AtomicWrite(BackupPath, originalBytes);
        else
            File.Delete(BackupPath);

        var state = new ConfigSessionState(
            configPath,
            originalExisted,
            Convert.ToHexString(SHA256.HashData(patchedBytes)),
            DateTimeOffset.UtcNow);
        AtomicWrite(
            StatePath,
            JsonSerializer.SerializeToUtf8Bytes(state, new JsonSerializerOptions { WriteIndented = true }));

        try
        {
            AtomicWrite(configPath, patchedBytes);
        }
        catch
        {
            CleanupStateFiles();
            throw;
        }

        log($"已临时启用 Codex HTTP/SSE 传输；退出中转后将自动还原：{configPath}");
        return new CodexConfigGuard(state);
    }

    public static int RunSelfTest(Action<string> log)
    {
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"CodexGreenHubBridge-SelfTest-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var normalizedTemporaryRoot = Path.GetFullPath(temporaryRoot);
        var normalizedSystemTemp = Path.GetFullPath(Path.GetTempPath());
        if (!normalizedTemporaryRoot.StartsWith(
                normalizedSystemTemp,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("自检临时目录不在系统临时目录内。");
        }

        Directory.CreateDirectory(normalizedTemporaryRoot);
        Environment.SetEnvironmentVariable("CODEX_HOME", normalizedTemporaryRoot);
        try
        {
            return RunSelfTestCore(log);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
            if (Directory.Exists(normalizedTemporaryRoot))
                Directory.Delete(normalizedTemporaryRoot, recursive: true);
        }
    }

    private static int RunSelfTestCore(Action<string> log)
    {
        var configPath = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var original = Encoding.UTF8.GetBytes(
            "model = \"gpt-test\"\r\n" +
            "model_provider = \"original-provider\"\r\n\r\n" +
            "[features]\r\nsample = true\r\n");
        File.WriteAllBytes(configPath, original);

        var exactRestoreGuard = Apply(log);
        var patched = File.ReadAllText(configPath);
        if (!patched.Contains($"model_provider = \"{ProviderId}\"", StringComparison.Ordinal) ||
            !patched.Contains("supports_websockets = false", StringComparison.Ordinal))
            throw new InvalidDataException("HTTP-only 配置未正确注入。");
        exactRestoreGuard.Restore(log);
        if (!File.ReadAllBytes(configPath).SequenceEqual(original))
            throw new InvalidDataException("Codex 配置未精确恢复。");

        var mergeRestoreGuard = Apply(log);
        File.AppendAllText(configPath, "\r\n[user_change]\r\nkept = true\r\n");
        mergeRestoreGuard.Restore(log);
        var merged = File.ReadAllText(configPath);
        if (merged.Contains(BeginMarker, StringComparison.Ordinal) ||
            merged.Contains($"model_provider = \"{ProviderId}\"", StringComparison.Ordinal) ||
            !merged.Contains("model_provider = \"original-provider\"", StringComparison.Ordinal) ||
            !merged.Contains("[user_change]", StringComparison.Ordinal))
            throw new InvalidDataException("保留用户并发修改的恢复测试失败。");

        _ = Apply(log);
        RecoverStale(log);
        if (!File.ReadAllText(configPath).Contains(
                "model_provider = \"original-provider\"",
                StringComparison.Ordinal))
            throw new InvalidDataException("异常退出恢复测试失败。");

        File.Delete(configPath);
        var missingFileGuard = Apply(log);
        if (!File.Exists(configPath))
            throw new InvalidDataException("缺少原配置时未能创建临时配置。");
        missingFileGuard.Restore(log);
        if (File.Exists(configPath))
            throw new InvalidDataException("原配置不存在时，退出后未删除临时配置。");

        log("Codex 配置保护自检通过：注入、精确恢复、合并恢复、异常恢复、无原文件恢复均正常。");
        return 0;
    }

    public void Restore(Action<string> log)
    {
        lock (this)
        {
            if (_restored)
                return;
            RestoreState(_state, log, staleRecovery: false);
            _restored = true;
        }
    }

    private static void RestoreState(ConfigSessionState state, Action<string> log, bool staleRecovery)
    {
        var currentExists = File.Exists(state.ConfigPath);
        var currentBytes = currentExists ? File.ReadAllBytes(state.ConfigPath) : Array.Empty<byte>();
        var currentHash = Convert.ToHexString(SHA256.HashData(currentBytes));

        if (currentExists &&
            string.Equals(currentHash, state.PatchedHash, StringComparison.OrdinalIgnoreCase))
        {
            RestoreOriginalExactly(state);
        }
        else if (!currentExists && state.OriginalExisted)
        {
            RestoreOriginalExactly(state);
        }
        else if (currentExists)
        {
            var originalText = state.OriginalExisted && File.Exists(BackupPath)
                ? DecodeUtf8(File.ReadAllBytes(BackupPath))
                : "";
            var cleaned = Unpatch(DecodeUtf8(currentBytes), originalText);
            AtomicWrite(
                state.ConfigPath,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(cleaned));
            log("Codex 配置在运行期间发生了其他变化；已仅移除中转注入项并保留其余修改。");
        }

        CleanupStateFiles();
        log(staleRecovery
            ? "检测到上次异常退出，已自动恢复 Codex 配置。"
            : "已恢复 Codex 启动前配置。");
    }

    private static void RestoreOriginalExactly(ConfigSessionState state)
    {
        if (state.OriginalExisted)
        {
            if (!File.Exists(BackupPath))
                throw new FileNotFoundException("找不到 Codex 配置备份。", BackupPath);
            AtomicWrite(state.ConfigPath, File.ReadAllBytes(BackupPath));
        }
        else if (File.Exists(state.ConfigPath))
        {
            File.Delete(state.ConfigPath);
        }
    }

    private static string Patch(string original)
    {
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = ManagedBlockRegex.Replace(original, "");
        var sectionIndex = SectionRegex.Match(text) is { Success: true } section
            ? section.Index
            : text.Length;
        var prefix = text[..sectionIndex];
        var suffix = text[sectionIndex..];
        var providerMatches = TopLevelProviderRegex.Matches(prefix);
        if (providerMatches.Count > 0)
        {
            prefix = TopLevelProviderRegex.Replace(
                prefix,
                $"model_provider = \"{ProviderId}\"{newline}");
        }
        else
        {
            var bom = prefix.StartsWith('\uFEFF') ? "\uFEFF" : "";
            if (bom.Length > 0)
                prefix = prefix[1..];
            prefix = $"{bom}model_provider = \"{ProviderId}\"{newline}{prefix}";
        }

        text = prefix + suffix;
        if (text.Length > 0 && !text.EndsWith('\n'))
            text += newline;
        if (text.Length > 0 && !text.EndsWith(newline + newline, StringComparison.Ordinal))
            text += newline;

        return text +
               BeginMarker + newline +
               $"[model_providers.{ProviderId}]" + newline +
               "name = \"ChatGPT HTTP via GreenHub\"" + newline +
               "base_url = \"https://chatgpt.com/backend-api/codex\"" + newline +
               "wire_api = \"responses\"" + newline +
               "requires_openai_auth = true" + newline +
               "supports_websockets = false" + newline +
               EndMarker + newline;
    }

    private static string Unpatch(string current, string original)
    {
        var text = ManagedBlockRegex.Replace(current, "");
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var currentSectionIndex = SectionRegex.Match(text) is { Success: true } currentSection
            ? currentSection.Index
            : text.Length;
        var currentPrefix = text[..currentSectionIndex];
        var currentSuffix = text[currentSectionIndex..];
        var currentProvider = TopLevelProviderRegex.Match(currentPrefix);

        if (currentProvider.Success &&
            currentProvider.Value.Contains($"\"{ProviderId}\"", StringComparison.Ordinal))
        {
            var originalSectionIndex = SectionRegex.Match(original) is { Success: true } originalSection
                ? originalSection.Index
                : original.Length;
            var originalProvider = TopLevelProviderRegex.Match(original[..originalSectionIndex]);
            currentPrefix = originalProvider.Success
                ? TopLevelProviderRegex.Replace(currentPrefix, originalProvider.Value, 1)
                : TopLevelProviderRegex.Replace(currentPrefix, "", 1);
        }

        text = currentPrefix + currentSuffix;
        return text.Replace(newline + newline + newline, newline + newline, StringComparison.Ordinal);
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        var offset = bytes.Length >= 3 &&
                     bytes[0] == 0xEF &&
                     bytes[1] == 0xBB &&
                     bytes[2] == 0xBF
            ? 3
            : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
        File.WriteAllBytes(temporaryPath, bytes);
        try
        {
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void CleanupStateFiles()
    {
        File.Delete(StatePath);
        File.Delete(BackupPath);
    }

    private sealed record ConfigSessionState(
        string ConfigPath,
        bool OriginalExisted,
        string PatchedHash,
        DateTimeOffset AppliedAt);
}

internal sealed record BridgeSettings(
    int FixedPort,
    int? UpstreamPort,
    string CodexExe,
    int UpstreamReadyTimeoutSeconds,
    bool? ForceCodexHttpTransport)
{
    private static readonly string UserConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexGreenHubBridge",
        "CodexProxyBridge.json");

    public static BridgeSettings LoadAndResolve()
    {
        var defaults = new BridgeSettings(
            7890,
            null,
            "",
            30,
            false);

        var portableConfigPath = Path.Combine(AppContext.BaseDirectory, "CodexProxyBridge.json");
        var loaded = TryLoad(UserConfigPath) ?? TryLoad(portableConfigPath) ?? defaults;

        var codex = ExecutableLocator.FindCodex(loaded.CodexExe);
        if (codex is null)
        {
            codex = ExecutableLocator.AskUserForCodex();
        }

        if (codex is null)
            throw new FileNotFoundException("未选择 Codex 的 ChatGPT.exe，无法启动中转。");

        var resolved = loaded with
        {
            FixedPort = loaded.FixedPort is > 0 and <= 65535 ? loaded.FixedPort : defaults.FixedPort,
            UpstreamPort = loaded.UpstreamPort is > 0 and <= 65535 ? loaded.UpstreamPort : null,
            CodexExe = codex,
            UpstreamReadyTimeoutSeconds = loaded.UpstreamReadyTimeoutSeconds is > 0 and <= 600
                ? loaded.UpstreamReadyTimeoutSeconds
                : defaults.UpstreamReadyTimeoutSeconds,
            // 保持 HTTP-only Provider 注入停用；桌面版改用 CLI 中转（零写入 config.toml）
            ForceCodexHttpTransport = false
        };
        resolved.SaveUserConfig();
        return resolved;
    }

    private static BridgeSettings? TryLoad(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<BridgeSettings>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private void SaveUserConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserConfigPath)!);
            File.WriteAllText(
                UserConfigPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A read-only environment should not prevent the bridge from running.
        }
    }
}

internal static class ExecutableLocator
{
    public static string? FindCodex(string? savedPath)
    {
        var cliPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        string? fromCli = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(cliPath) &&
                Path.IsPathFullyQualified(cliPath) &&
                Path.GetDirectoryName(cliPath) is { } cliDirectory)
            {
                fromCli = Path.GetFullPath(Path.Combine(cliDirectory, "..", "ChatGPT.exe"));
            }
        }
        catch
        {
            // A bare CODEX_CLI_PATH such as "codex.exe" is valid but cannot
            // identify the desktop application directory.
        }

        var result = FirstValidCodex(
            ExpandSavedPath(savedPath),
            FindRunningProcessPath("ChatGPT"),
            FindRunningProcessPath("Codex"),
            fromCli,
            CandidateNearBridge("ChatGPT.exe"),
            CandidateNearBridge("Codex.exe"),
            CandidateNearBridge("app", "ChatGPT.exe"),
            FindFromAppPathsRegistry("ChatGPT.exe"),
            FindFromAppPathsRegistry("Codex.exe"),
            FindCodexFromShortcuts(),
            FindFromUninstallRegistry("Codex", "ChatGPT.exe"),
            FindFromUninstallRegistry("Codex", "Codex.exe"),
            CandidateInSpecialFolder(Environment.SpecialFolder.LocalApplicationData, "Programs", "Codex", "ChatGPT.exe"),
            CandidateInSpecialFolder(Environment.SpecialFolder.LocalApplicationData, "Programs", "Codex", "Codex.exe"),
            CandidateInSpecialFolder(Environment.SpecialFolder.LocalApplicationData, "Codex", "ChatGPT.exe"),
            CandidateInSpecialFolder(Environment.SpecialFolder.ProgramFiles, "Codex", "ChatGPT.exe"),
            FindPortableCodexInUserFolder("Downloads"),
            FindPortableCodexInUserFolder("Desktop"));
        result ??= FindCodexByBoundedScan();
        Program.WriteLog(result is null
            ? "Codex 自动定位失败，将请求用户选择 Codex.exe 或 ChatGPT.exe。"
            : $"Codex 自动定位成功：{result}");
        return result;
    }

    public static string? AskUserForCodex()
    {
        const string title =
            "未能自动识别 Codex。请选择桌面上或安装目录中的 Codex.exe；" +
            "也可以选择 app 目录中的 ChatGPT.exe。";
        using var owner = CreateDialogOwner();
        MessageBox.Show(
            owner,
            title,
            "需要选择 Codex 程序位置",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        using var dialog = new OpenFileDialog
        {
            Title = "选择 Codex.exe 或 ChatGPT.exe",
            Filter =
                "Codex 程序或快捷方式|Codex.exe;ChatGPT.exe;*.lnk|" +
                "可执行文件 (*.exe)|*.exe|Windows 快捷方式 (*.lnk)|*.lnk",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            RestoreDirectory = true,
            DereferenceLinks = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        while (dialog.ShowDialog(owner) == DialogResult.OK)
        {
            var resolved = NormalizeCodexCandidate(dialog.FileName);
            if (resolved is not null)
            {
                Program.WriteLog($"用户选择并确认 Codex 程序：{resolved}");
                return resolved;
            }

            MessageBox.Show(
                owner,
                "所选文件旁边没有找到 Codex 所需的 resources\\codex.exe。\n\n" +
                "请尝试选择桌面上的 Codex 快捷方式、安装目录中的 Codex.exe，" +
                "或 app 目录中的 ChatGPT.exe。",
                "不是有效的 Codex 桌面程序",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        return null;
    }

    private static Form CreateDialogOwner()
    {
        var owner = new Form
        {
            Text = "Codex 固定代理桥",
            TopMost = true,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(1, 1),
            Opacity = 0
        };
        owner.Show();
        owner.Activate();
        return owner;
    }

    private static string? ExpandSavedPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path);

    private static string? FirstExisting(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch { }
        }
        return null;
    }

    private static string? FirstValidCodex(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var resolved = NormalizeCodexCandidate(candidate);
            if (resolved is not null)
                return resolved;
        }
        return null;
    }

    private static string? NormalizeCodexCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
            if (File.Exists(expanded) &&
                Path.GetExtension(expanded).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                expanded = ResolveShortcutTarget(expanded) ?? expanded;
            }
            var directory = Directory.Exists(expanded)
                ? Path.GetFullPath(expanded)
                : Path.GetDirectoryName(Path.GetFullPath(expanded));
            if (string.IsNullOrWhiteSpace(directory))
                return null;

            var fileName = Directory.Exists(expanded) ? "" : Path.GetFileName(expanded);
            var possibleDesktopExecutables = new List<string>();
            if (fileName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(directory).Equals("resources", StringComparison.OrdinalIgnoreCase) &&
                Directory.GetParent(directory) is { } appDirectory)
            {
                possibleDesktopExecutables.AddRange(
                [
                    Path.Combine(appDirectory.FullName, "ChatGPT.exe"),
                    Path.Combine(appDirectory.FullName, "Codex.exe")
                ]);
            }
            else if (fileName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
                possibleDesktopExecutables.Add(expanded);
            else if (fileName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase))
                possibleDesktopExecutables.AddRange(
                [
                    Path.Combine(directory, "ChatGPT.exe"),
                    expanded,
                    Path.Combine(directory, "app", "ChatGPT.exe"),
                    Path.Combine(directory, "app", "Codex.exe")
                ]);

            possibleDesktopExecutables.AddRange(
            [
                Path.Combine(directory, "ChatGPT.exe"),
                Path.Combine(directory, "Codex.exe"),
                Path.Combine(directory, "app", "ChatGPT.exe"),
                Path.Combine(directory, "app", "Codex.exe")
            ]);

            foreach (var desktopExecutable in possibleDesktopExecutables.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var fullDesktopPath = Path.GetFullPath(desktopExecutable);
                var desktopDirectory = Path.GetDirectoryName(fullDesktopPath);
                if (File.Exists(fullDesktopPath) &&
                    desktopDirectory is not null &&
                    File.Exists(Path.Combine(desktopDirectory, "resources", "codex.exe")))
                    return fullDesktopPath;
            }
        }
        catch { }
        return null;
    }

    private static string? ResolveShortcutTarget(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return null;
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return null;
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);
            return shortcut?.GetType().InvokeMember(
                "TargetPath",
                System.Reflection.BindingFlags.GetProperty,
                binder: null,
                target: shortcut,
                args: null) as string;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string? FindRunningProcessPath(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return path;
            }
            catch { }
            finally { process.Dispose(); }
        }
        return null;
    }

    private static string? FindCodexFromShortcuts()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var shortcut in Directory.EnumerateFiles(
                             root,
                             "*.lnk",
                             SearchOption.AllDirectories)
                         .Where(path => ContainsCodexName(Path.GetFileNameWithoutExtension(path))))
                {
                    if (NormalizeCodexCandidate(shortcut) is { } resolved)
                        return resolved;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return null;
    }

    private static string? FindFromUninstallRegistry(string displayNamePart, string executableName)
    {
        var roots = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32)
        };

        foreach (var (hive, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                    continue;
                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var item = uninstall.OpenSubKey(subKeyName);
                    var displayName = item?.GetValue("DisplayName") as string;
                    if (displayName?.Contains(displayNamePart, StringComparison.OrdinalIgnoreCase) != true)
                        continue;

                    var installLocation = item?.GetValue("InstallLocation") as string;
                    var candidate = FirstExisting(
                        string.IsNullOrWhiteSpace(installLocation)
                            ? null
                            : Path.Combine(installLocation, executableName),
                        NormalizeDisplayIcon(item?.GetValue("DisplayIcon") as string, executableName));
                    if (candidate is not null)
                        return candidate;
                }
            }
            catch { }
        }
        return null;
    }

    private static string? FindFromAppPathsRegistry(string executableName)
    {
        var roots = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32)
        };
        foreach (var (hive, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(
                    $@"Software\Microsoft\Windows\CurrentVersion\App Paths\{executableName}");
                var value = key?.GetValue(null) as string;
                var candidate = value?.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch { }
        }
        return null;
    }

    private static string? NormalizeDisplayIcon(string? displayIcon, string executableName)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
            return null;
        var path = displayIcon.Trim().Trim('"').Split(',')[0];
        if (File.Exists(path) &&
            string.Equals(Path.GetFileName(path), executableName, StringComparison.OrdinalIgnoreCase))
            return path;
        return null;
    }

    private static string? CandidateInSpecialFolder(Environment.SpecialFolder folder, params string[] parts)
    {
        var root = Environment.GetFolderPath(folder);
        return string.IsNullOrWhiteSpace(root) ? null : Path.Combine([root, .. parts]);
    }

    private static string? CandidateInUserFolder(string folderName, params string[] parts)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? null
            : Path.Combine([profile, folderName, .. parts]);
    }

    private static string? CandidateNearBridge(params string[] parts)
    {
        var direct = Path.Combine([AppContext.BaseDirectory, .. parts]);
        if (File.Exists(direct))
            return direct;
        var parent = Directory.GetParent(AppContext.BaseDirectory)?.FullName;
        return parent is null ? null : Path.Combine([parent, .. parts]);
    }

    private static string? FindCodexByBoundedScan()
    {
        Program.WriteLog("正在常用目录中限深搜索 Codex 桌面程序。");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(localAppData, "Programs"),
            Path.Combine(localAppData, "OpenAI"),
            Path.Combine(localAppData, "Codex"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        var inspectedDirectories = 0;
        foreach (var root in roots
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = FindCodexInDirectoryTree(root, maxDepth: 5, ref inspectedDirectories);
            if (found is not null)
                return found;
            if (inspectedDirectories >= 8_000)
                break;
        }
        return null;
    }

    private static string? FindCodexInDirectoryTree(
        string root,
        int maxDepth,
        ref int inspectedDirectories)
    {
        if (!Directory.Exists(root))
            return null;

        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0 && inspectedDirectories < 8_000)
        {
            var (directory, depth) = pending.Dequeue();
            inspectedDirectories++;
            var candidate = NormalizeCodexCandidate(directory);
            if (candidate is not null)
                return candidate;
            if (depth >= maxDepth)
                continue;

            try
            {
                var children = Directory.EnumerateDirectories(directory)
                    .Select(path => new DirectoryInfo(path))
                    .Where(info => (info.Attributes & FileAttributes.ReparsePoint) == 0)
                    .OrderByDescending(info =>
                        ContainsCodexName(info.Name))
                    .ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var child in children)
                    pending.Enqueue((child.FullName, depth + 1));
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return null;
    }

    private static bool ContainsCodexName(string name) =>
        name.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("openai", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("chatgpt", StringComparison.OrdinalIgnoreCase);

    private static string? FindPortableCodexInUserFolder(string folderName)
    {
        var searchRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            folderName);
        if (!Directory.Exists(searchRoot))
            return null;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(searchRoot, "*Codex*", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                var candidate = FirstExisting(
                    Path.Combine(directory, "app", "ChatGPT.exe"),
                    Path.Combine(directory, "ChatGPT.exe"));
                if (candidate is not null)
                    return candidate;
            }
        }
        catch { }
        return null;
    }
}

internal sealed record LaunchOptions(
    bool RelayOnly,
    bool NoSystemProxy,
    bool Diagnose,
    bool SelfTestConfig,
    bool ShowHelp,
    bool Headless,
    bool AllowMultiple,
    int? ExitAfterSeconds)
{
    public const string HelpText =
        "默认运行：发现本地 HTTP 代理 → 建立 127.0.0.1:7890 固定中转 → 启动 Codex（HTTPS/SSE）。\n\n" +
        "--diagnose                只检查配置，不启动程序\n" +
        "--self-test-config        在隔离环境中测试 Codex 配置保护\n" +
        "--relay-only              仅运行中转，不启动 Codex\n" +
        "--headless                不显示状态窗口\n" +
        "--allow-multiple          允许测试实例与正式实例同时运行\n" +
        "--exit-after-seconds N    N 秒后退出（用于测试）\n" +
        "--help                    显示帮助";

    public static LaunchOptions Parse(string[] args)
    {
        var relayOnly = args.Contains("--relay-only", StringComparer.OrdinalIgnoreCase);
        var noSystemProxy = args.Contains("--no-system-proxy", StringComparer.OrdinalIgnoreCase);
        var diagnose = args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase);
        var selfTestConfig = args.Contains("--self-test-config", StringComparer.OrdinalIgnoreCase);
        var showHelp = args.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
                       args.Contains("-h", StringComparer.OrdinalIgnoreCase);
        int? exitAfter = null;
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--exit-after-seconds", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[index + 1], out var seconds) && seconds > 0)
            {
                exitAfter = seconds;
            }
        }
        var headless = args.Contains("--headless", StringComparer.OrdinalIgnoreCase) ||
                       diagnose || selfTestConfig || exitAfter is > 0;
        var allowMultiple = args.Contains("--allow-multiple", StringComparer.OrdinalIgnoreCase);
        return new LaunchOptions(
            relayOnly,
            noSystemProxy,
            diagnose,
            selfTestConfig,
            showHelp,
            headless,
            allowMultiple,
            exitAfter);
    }
}
