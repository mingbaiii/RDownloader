# RDownloader

> 本项目由 AI 辅助编写。

**RDown** 是一个多线程下载工具。它利用 CDN 的多个节点进行并行下载——先将文件切分为多个分片，再通过不同 CDN 节点同时拉取，最后合并成完整文件。支持智能节点选择、速度自适应负载均衡，以及在服务器不支持断点续传时自动退化为单节点下载。

## 功能特性

- **CDN 多节点并行下载** — 自动解析域名的所有 CDN 节点 IP，将文件分段后从多个节点同时下载，充分利用带宽。**实测可大幅提高国内下载 GitHub 文件的速度**
- **自定义请求头与认证** — Basic Auth、Bearer Token、Cookie、Referer、User-Agent 及任意自定义头
- **可配置的重试机制** — 失败重试，可分别配置重连、重新获取节点、重新获取 URL 的次数
- **桌面 GUI** — 内置 aria2 兼容的 JSON-RPC 接口，支持可视化下载管理

## 安装

### 从源码构建

```bash
# 前置条件：安装 Rust 工具链（https://rustup.rs）
git clone https://github.com/user/rdownloader.git
cd rdownloader

# Release 构建（优化体积）
cargo build --release
# 产物：target/release/rdown[.exe]
```

### GUI（仅 Windows）

```bash
# 先构建 CLI
.\build-release.ps1

# 在 Visual Studio 中打开 GUI/GUI.slnx 并构建
```

## 快速上手

### 交互模式

```bash
rdown

rdown> download https://example.com/file.zip
rdown> resume abc12345
rdown> monitor abc12345
```

### JSON 模式（脚本调用）

```bash
# 直接执行模式 — 单条命令，stdout 输出 JSON 结果
rdown --json download https://example.com/file.zip
# → {"status": "ok", "id": "abc12345", "output": "file.zip"}

rdown --json resume abc12345
# → {"status": "ok", "message": "Download resumed"}

rdown --json list
# → {"status": "ok", "downloads": [...]}
```

```bash
# stdin 模式 — 持久进程，逐行 JSON
rdown --json
{"command": "download", "args": ["https://example.com/file.zip"]}
{"command": "resume", "args": ["abc12345"]}
{"command": "exit"}
```

## CLI 命令

| 命令 | 说明 |
|---|---|
| `download <url> [-o <路径>]` | 创建下载任务（探测节点，不立即开始下载） |
| `list` | 列出当前目录下所有下载任务 |
| `info <id>` | 查看下载详情（节点、速度、进度） |
| `info all <id>` | 查看完整详情（含分片及每个分片的速度） |
| `monitor <id>` | 实时 TUI 进度监控 |
| `pause <id>` | 暂停下载 |
| `resume <id>` | 开始/恢复下载 |
| `cancel <id>` | 取消并删除下载任务 |
| `loop` | 批量启动所有待处理任务并等待完成 |
| `add-endpoint <id> <ip> [--proxy <url>]` | 手动添加节点 |
| `disable-endpoint <id> <ip>` | 禁用某个节点 |
| `enable-endpoint <id> <ip>` | 重新启用被禁用的节点 |
| `set-config <key> <value>` | 修改全局配置 |
| `help` | 显示帮助 |
| `exit` | 退出（仅 JSON stdin 模式） |

## 配置

通过 `set-config` 命令管理配置，并持久化到配置文件。常用配置项：

| 配置键 | 说明 | 默认值 |
|---|---|---|
| `connections` | 并行连接数 | `64` |
| `chunk-size` | 初始分片大小（如 `64KB`、`1MB`） | `64KB` |
| `retry` | 失败重试次数 | `3` |
| `download-timeout` | 下载超时（如 `30s`、`5m`、`1h`） | `30s` |
| `speed-threshold` | 速度比例阈值，用于加权负载均衡 | `2.0` |
| `ip-protocol` | IP 协议偏好：`ipv4`、`ipv6`、`both` | `ipv4` |
| `dns-servers` | DNS 服务器列表（UDP IP、DoH URL 或 `system`） | `system, 114.114.114.114, https://223.5.5.5/dns-query` |
| `header` | 自定义 HTTP 请求头（JSON 或 `Name: Value` 格式） | — |
| `proxy` | 代理规则：`*=<url>`、`<ip>=<url>`、`dns:*=<url>` | — |
| `endpoint-failure-threshold` | 单节点连续失败多少次后自动禁用 | `3` |

示例：

```bash
rdown set-config connections 16
rdown set-config chunk-size 1MB
rdown set-config dns-servers "system,8.8.8.8,https://1.1.1.1/dns-query"
rdown set-config proxy "*=http://127.0.0.1:10808"
rdown set-config header '{"Authorization":"Bearer my-token"}'
```

## 项目结构

```
.
├── src/
│   ├── main.rs           # 入口：交互 REPL、JSON stdin/直接执行模式
│   ├── config.rs         # 全局配置管理
│   ├── remote_file.rs    # 远程文件元数据（HEAD 探测、Accept-Ranges）
│   ├── utils.rs          # 工具函数
│   ├── cli/              # CLI：命令解析、执行、Tab 补全
│   ├── dns/              # DNS 解析（UDP、DoH、系统 DNS）
│   ├── downloader/       # 核心下载引擎
│   │   ├── handle.rs     # 下载编排
│   │   ├── connection.rs # HTTP Range 请求连接
│   │   ├── allocator.rs  # 分片在节点间的分配
│   │   ├── scheduler.rs  # 节点调度与速度跟踪
│   │   ├── state.rs      # 下载状态机
│   │   └── retry.rs      # 重试管理器
│   └── endpoint_test/    # 节点连通性及速度探测
├── GUI/                  # 桌面应用
│   ├── MainWindow.xaml   # 主窗口：下载列表与操作
│   ├── DownloadPage.xaml # 下载详情与进度视图
│   ├── SettingsPage.xaml # 设置与配置界面
│   ├── Aria2RpcServer.cs # aria2 兼容 JSON-RPC 服务
│   └── RDownloaderManager.cs # GUI 核心下载编排
├── tests/                # 测试服务器与集成测试
│   └── test_server.py    # 测试服务器，覆盖多种下载场景
├── Cargo.toml
```

## 架构

### 下载流程

```
URL → DNS 解析 → 获取所有 CDN 节点 IP
              ↓
      节点探测（连通性 + 速度测试）
              ↓
      分片分配（文件切分为多个分片，分配给不同节点）
              ↓
      多节点多连接并行下载
              ↓
      分片合并 → 完成 / 重试 / 退化
```

### 退化场景

| 情况 | 行为 |
|---|---|
| 无 `Content-Length` | 单连接，chunked 传输 |
| `Accept-Ranges: none` | 单节点单连接，不发送 Range 请求 |
| 无 `Accept-Ranges` 头 | 先建立多连接 → 服务器返回 200 → 自动退化为单连接 |
| `Content-Length: 0` | 最多重试 `content-length-zero-retry` 次 |

## JSON 模式 API

为脚本和自动化提供完整的程序化控制。

```python
import subprocess, json

def rdown(cmd, *args):
    result = subprocess.run(
        ["rdown", "--json", cmd] + list(args),
        capture_output=True, text=True, timeout=60
    )
    return json.loads(result.stdout)

# 创建并开始下载
resp = rdown("download", "https://example.com/file.zip")
rdown("resume", resp["id"])

# 等待所有下载完成
rdown("loop")
```

## GUI 功能

- 多标签页界面：下载列表、详情视图、设置
- 实时进度条、速度图表、分片可视化
- aria2 兼容的 JSON-RPC 服务端，可对接现有工具
- 节点管理（启用、禁用）
- 自定义请求头和代理配置界面
