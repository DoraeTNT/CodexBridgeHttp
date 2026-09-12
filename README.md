# CodexProxyBridge（v2.6 通用代理桥）

单文件 Windows 启动器，为 Codex 桌面版提供固定的本地 TCP 代理端口 `127.0.0.1:7890`，
并让 Codex 跳过 WebSocket，直接使用 HTTPS/SSE 传输。

> v2.6 起已完全移除 GreenHub 依赖：上游改为自动发现电脑上任意一款代理软件
> （Clash Verge / Clash for Windows / V2rayN / Netch / Shadowsocks 等）的本地
> HTTP 代理端口。全程不修改 Codex 的 `config.toml`，不切换 Provider，不接管
> Windows 系统代理。

默认显示状态面板，可直观看到：

- 上游代理：当前使用的本地 HTTP 代理端口、来源（手动配置 / 系统代理 / 自动发现）与可用状态。
- 代理延迟：上游代理端口的 TCP 连接延迟（每 8 秒刷新）。
- 固定中转：`127.0.0.1:7890` 纯 TCP 中转是否正在监听。
- Codex 传输：HTTPS/SSE（WebSocket 已跳过）是否生效。
- Codex 是否运行。
- 最近的中转运行日志。

关闭窗口时程序会缩小到系统托盘；需要彻底停止时，点击面板或托盘菜单中的“退出中转”，
会同时关闭 Codex 和中转服务。

## 工作方式

1. 在 `127.0.0.1:7890` 启动纯 TCP 中转。
2. 自动发现电脑上代理软件的本地 HTTP 代理端口（发现顺序见下）。
3. 启动 Codex，注入 `HTTP_PROXY` / `HTTPS_PROXY=http://127.0.0.1:7890`。
4. 通过 CLI 中转注入 HTTPS-only 模型目录（`prefer_websockets=false`），
   使 Codex 直接使用 HTTPS/SSE，跳过 WebSocket。
5. 持续健康检查上游，失效时自动重新发现并切换。
6. Codex 退出后，中转程序随之退出。

## 上游发现顺序

1. **用户显式指定**：配置文件中的 `UpstreamPort`（如 `7890`）。
2. **系统代理嗅探**：读取 Windows 系统代理地址（多数代理软件开启“系统代理”后生效）。
3. **常见端口探测**：并行探测下列软件的本地 HTTP 端口，通过 `chatgpt.com` 全链路检查者即采用。

| 软件 | HTTP 端口 |
|---|---|
| Clash Verge / Mihomo | 7897 |
| Clash for Windows | 7890 |
| V2rayN | 10809 |
| Netch | 2802 |
| Shadowsocks 类（若支持 HTTP） | 1080 |

> 纯 SOCKS5 代理（如 Shadowsocks 默认）无法作为 HTTP 上游。请在代理软件中开启
> “系统代理”或“HTTP 端口”，让桥通过策略 2 捕获；或直接填写其 HTTP 端口。

## 不修改 Codex 配置

- 不写入、不修改 `%USERPROFILE%\.codex\config.toml`。
- 不切换 `model_provider`，保留内置 Provider、原登录状态、历史项目和历史会话。
- Windows 系统代理、用户永久环境变量均不会被修改。
- Microsoft Store 版 Codex：通过 AUMID 激活，临时注入模型目录并在退出后精确恢复。

## 参数

```text
--diagnose                只检查配置，不启动程序
--self-test-config        在隔离环境中测试 Codex 配置保护
--relay-only              仅运行中转，不启动 Codex
--headless                不显示状态窗口
--allow-multiple          允许测试实例与正式实例同时运行
--exit-after-seconds N    N 秒后退出（用于测试）
--help                    显示帮助
```

程序日志位于：

```text
%LOCALAPPDATA%\CodexProxyBridge\bridge.log
```

把 `CodexProxyBridge.json` 与 EXE 放在同一目录即可覆盖默认端口或指定上游：

```json
{
  "FixedPort": 7890,
  "UpstreamPort": 0,
  "CodexExe": "",
  "UpstreamReadyTimeoutSeconds": 30,
  "ForceCodexHttpTransport": false
}
```

- `UpstreamPort`：`0` 表示自动发现；填写具体端口则优先使用该端口。
- `UpstreamReadyTimeoutSeconds`：自动发现超时时间，超时后弹出窗口请求手动输入。

## 在其他电脑使用

发布的单文件 EXE 包含 .NET 运行时，可以直接复制到其他 Windows x64 电脑。
启动时会从正在运行的进程、桥接程序附近、App Paths/卸载注册表、常用安装目录、
桌面和下载目录自动寻找 Codex。若仍无法识别，会显示置顶提示并要求选择
`Codex.exe`（或旧版 `app` 目录中的 `ChatGPT.exe`）。识别结果保存在
`%LOCALAPPDATA%\CodexProxyBridge\CodexProxyBridge.json`。

Microsoft Store 版 Codex 位于受保护的 `WindowsApps` 中时，中转不会向应用包
复制、执行或注入 CLI 包装程序，而是通过包 AUMID 和 `shell:AppsFolder` 激活，
退出时精确恢复启动前内容。
