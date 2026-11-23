# Codex & Windows Network Repair Skill

> **诊断并修复 Windows 代理异常（如 V2rayN 节点有延迟却无法访问外网、关闭代理后断网）及 Codex 无法加载工作空间、反复重连 5/5 的 AI Agent Skill。**
>
> *An AI Agent Skill for diagnosing and fixing Windows proxy issues (e.g., V2rayN latency OK but unreachable, broken network after turning off proxy) and Codex workspace load / reconnect `5/5` failures.*

---

## 解决什么问题 / Problems Solved

| 常见故障现象 | 对应排查分支 | 核心能力 |
|---|---|---|
| 节点测速 `-1`，或 V2rayN 节点有延迟但网页打不开 | [references/paths.md](references/paths.md) | 拆分检测入口 TCP、节点真连接与显式代理 HTTPS 请求，定位首个失败环节 |
| 显式指定本地代理可通，普通 Chrome / Edge 超时 | [references/windows.md](references/windows.md) | 核对 WinINet / WinHTTP 当前用户接口、活动拨号连接及具体网址选路 |
| 关闭系统代理或退出客户端后，原本可直连的网站也断网 | [references/windows.md](references/windows.md) / [references/ppp-sync.md](references/ppp-sync.md) | 排查残留代理与 Windows 宽带拨号（PPP）错名/脱钩问题，验证启停全生命周期 |
| Codex 提示无法加载工作空间设置或无法新建聊天 | [references/codex.md](references/codex.md) | 区分工作空间加载、创建聊天、首轮握手与流式中途断开 |
| Codex 显示“正在重新连接 1/5～5/5”或频繁回退 HTTP | [references/codex.md](references/codex.md) | 自动化对照 `respect_system_proxy` 握手结果，获准后备份并通过原生命令修复 |

---

## 快速开始 / Quick Start

### 方式一：下载完整发布包（推荐普通用户 / 开箱即用）

1. 前往本仓库的 [Releases 页面](https://github.com/li-tao-yao/codex-windows-network-skill/releases)，下载最新的完整压缩包（如 `codex-windows-network-skill-v1.0.0.zip`）。
   - 该压缩包已包含完整的 Skill 文档以及预编译好的 `scripts/Check-WindowsProxy.exe` 和 `scripts/Repair-CodexReconnect.exe`。
2. 解压到本地目录（路径尽量不含特殊字符）。
3. **本机 AI Agent（Codex / Antigravity / Claude Code 等）**：
   - 将整个解压后的文件夹放入对应工具的 Skills 目录，或直接让 AI 打开该目录并先阅读 [SKILL.md](SKILL.md)。
   - 直接发送以下提示词即可开始排查：

```text
请用这份技能，在我这台 Windows 电脑定位实际失败环节并修复。我的症状是 ____，原网络和代理模式是 ____。请先做只读对照，涉及持久写入时说明具体影响与回滚方法。若我已安装 Codex，主动检查重连和工作空间加载问题；若单独指定范围，按该范围执行。最后从普通图标验证浏览器和真实聊天。如果更改了系统代理，请分别测试只关系统代理、完全退出客户端、重新开启；如果配置了登录启动，再记下重新登录后的结果。请分开报告已恢复和仍待验证的部分。
```

4. **网页版 AI（ChatGPT / Gemini 等）**：
   - 将 [SKILL.md](SKILL.md) 及对应症状分支文档（`references/*.md`）发送给 AI。
   - 在本机运行辅助工具，仅把脱敏后的状态码、耗时与摘要回传给 AI。

### 方式二：Git 克隆并本地从源码编译（推荐开发者）

为保持源码仓库干净与可审计，Git 仓库中**不直接提交 `.exe` 二进制文件**。克隆仓库后，可使用 Windows 系统自带的 `.NET Framework 4.x` 编译器一键构建（无需安装 Visual Studio 或 .NET SDK）：

```powershell
git clone https://github.com/li-tao-yao/codex-windows-network-skill.git
cd codex-windows-network-skill

# 使用 Windows 内置 csc.exe 从源码编译两个辅助工具到 scripts\ 目录
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Tools.ps1
```

也可以直接从 [Releases 页面](https://github.com/li-tao-yao/codex-windows-network-skill/releases) 单独下载两个 `.exe` 放到 `scripts/` 目录，并对照 `SHA256SUMS.txt` 校验文件完整性。

---

## 辅助工具与源码说明 / Helper Tools & Transparency

本仓库包含两个用 C# 编写的轻量化 Windows 辅助工具（源码均已完整公开，可直接审计）：

| 工具 | 源码 | 权限与行为 | 常用命令行参数 |
|---|---|---|---|
| `scripts/Check-WindowsProxy.exe` | [Check-WindowsProxy.cs](scripts/Check-WindowsProxy.cs) | **纯只读检查**，不修改任何系统设置，不输出私人地址/PAC/凭据 | `--port 10808 --no-pause`<br>`--expect on/off` |
| `scripts/Repair-CodexReconnect.exe` | [Repair-CodexReconnect.cs](scripts/Repair-CodexReconnect.cs) | 先做只读握手对照；仅在临时启用 `respect_system_proxy` 有效且获机主确认后，才备份并通过原生命令保存该项 | `--check --no-pause`（只读）<br>`--apply --no-pause`（获准修复）<br>`--port 10808` |

如需手动编译，可在 Windows PowerShell 中直接调用系统自带的 `csc.exe`：

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /optimize+ /target:exe /utf8output /out:scripts\Check-WindowsProxy.exe scripts\Check-WindowsProxy.cs
& $csc /nologo /optimize+ /target:exe /utf8output /r:System.Web.Extensions.dll /out:scripts\Repair-CodexReconnect.exe scripts\Repair-CodexReconnect.cs
```

---

## 目录结构 / Repository Structure

```text
codex-windows-network-skill/
├── SKILL.md                          # Agent Skill 主入口与排查路由表（name: windows-network-repair）
├── README.md                         # 项目说明、安装与编译文档
├── 先读我.txt                         # 面向离线解压用户的简明使用说明
├── LICENSE                           # MIT 开源许可证
├── agents/
│   └── openai.yaml                   # OpenAI / Codex Skill 元数据与默认提示词
├── references/
│   ├── paths.md                      # 节点入口 TCP、真连接与显式代理请求排查
│   ├── windows.md                    # Windows 系统代理、活动拨号错名与启停验收
│   ├── ppp-sync.md                   # Windows 宽带拨号代理启停同步与回滚规范
│   ├── codex.md                      # Codex 工作空间加载与 5/5 重连诊断修复
│   └── cases.md                      # 脱敏案例与结论迁移边界
└── scripts/
    ├── Check-WindowsProxy.cs         # Windows 当前用户代理只读检查器源码
    ├── Repair-CodexReconnect.cs      # Codex 重连自动对照与单项修复工具源码
    └── Build-Tools.ps1               # Windows 内置 csc.exe 一键编译脚本
```

---

## 安全与隐私原则 / Safety & Privacy

- **只读优先**：所有分支均先完成只读基线与对照，不盲改系统或应用配置。
- **最小改动与可回滚**：任何持久化修改前必须明确说明对象、影响和回滚步骤，修改前自动备份原配置。
- **隐私脱敏**：排查过程无需向 AI 或他人提供私人服务器 IP、域名、节点订阅链接、账号凭据或完整日志；内置工具输出已默认将本地代理端点脱敏为 `loopback:端口`。
- **真实应用验收**：命令行探针通过后，仍要求从平时使用的桌面图标启动浏览器与 Codex 验证实际网页和真实新聊天。

---

## License

本项目采用 [MIT License](LICENSE) 开源。
