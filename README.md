# Multi-Agent Collaboration Framework 🤖🤝🤖

本项目是一个**即开即用、分布式**的多智能体（Agent）协作模板。通过结合全局准则（Rules）、预定义能力流（Skills）以及本地化锁服务（MCP Server），你可以将这个模板直接平移或应用到任何项目中，实现多个 Agent 在单一分支或项目下的安全协同开发。

## 架构说明 (三位一体)

1.  **规则层 (Rules)**: 提供 `GEMINI.md` 与 `CLAUDE.md`，用于告诉 IDE 里的 Agent：“如何在这里规矩地干活”。
2.  **技能层 (Skills)**: 位于 `skills/` 下。包含了诸如 `collaborative_git` 等原子化工作流，约束 Agent 代码同步动作。
3.  **协议层 (MCP)**: 位于 `mcp/RepoManager/`。这是一个基于 **C# 编写的独立单体服务**。负责维持本地文件的锁状态，防止两个 Agent 同时修改同一个代码文件导致灾难性覆盖。

## 🚀 为什么选择这套结构？

*   **分布式/解耦**: 我们不强绑定任何特定的 `src/` 代码结构。只要把这三个模块塞进你的项目，启动 MCP，任何 Agent 就能自动遵守。
*   **低依赖 (即开即用)**: MCP Server 采用 C# (`.NET`) 编写，由于启用了 AOT (Ahead-of-Time) 编译配置，它可以被打包成极小且无运行时依赖的单一 `.exe` 文件。

## 🔧 使用方法

### 1. 配置规则 (Rules)
根据你主力使用的 Agent 模型，将 `GEMINI.md` 或 `CLAUDE.md` 的内容配置到你的全局 Prompt、`.cursorrules` 或框架对应的项目级约束文件中。

### 2. 引入技能 (Skills)
将 `skills/` 文件夹复制到你当前 IDE/智能体框架支持的技能存放路径。遇到冲突或同步动作时，指导 Agent 调用 `Collaborative Git Workflow` 技能。

### 3. 运行 MCP (RepoManager)
本项目已经为您生成了基于 **SSE（Server-Sent Events）网络直连**的便携部署配置。

#### 第一步：启动 MCP 服务
为了实现真正的“即开即用”，我已经为您预先编译了独立运行的程序包（**内置了完整的 .NET 运行环境，不需要您安装任何依赖**）。

您只需要在项目根目录找到并**双击运行 `RepoManager.exe`**，即可开启独立的 HTTP/SSE Web 服务器（监听 http://localhost:5000 ）。
*启动后，一个黑色的终端窗口会保持开启，里面会清晰地展示出带有颜色高亮的每一次 Agent 操作日志和锁申请情况。*

#### 第二步：让 Agent 连接
在大多数支持项目级 MCP 配置的 IDE 中（我们已经在 `.gemini/mcp.json` 和 `.claude/mcp.json` 中配置好了），IDE 启动时会自动尝试通过 HTTP 网络连接到 `http://localhost:5000/sse`。
由于采用了标准的网络协议，你甚至可以让局域网内其他的机器连接到这台主机的 MCP 进行跨机器协作同步！

*   `acquire_lock(filepath)`: 获取文件的修改权限。
*   `release_lock(filepath)`: 释放文件的修改权限。

---

## 🧠 任务调度系统（v2.0 新增）

除了文件锁，RepoManager 现在还内置了一个**队长-队员任务调度总线**。

### 工作流程

```
用户 → 队长AI(Gemini) → create_task × N → 任务池
                      → spawn_worker(qwen/gemini) → 队员进程
队员AI ← poll_my_task ← 任务池
队员AI → 执行 + acquire_lock → 提交
队员AI → update_task(done)
队长AI → review_task → 通过 or 打回重做
```

### 任务调度 MCP 工具

| 工具 | 调用方 | 说明 |
|------|--------|------|
| `create_task` | 队长 | 创建子任务，指定角色 |
| `poll_my_task` | 队员 | 按角色名领取任务 |
| `update_task` | 队员 | 上报完成/失败 |
| `review_task` | 队长 | 审查通过或打回 |
| `list_all_tasks` | 任何 | 查看任务看板 |
| `spawn_worker` | 队长 | 自动启动 qwen/gemini 队员进程 |

### 控制台指令（在 RepoManager 黑窗口中输入）

| 指令 | 说明 |
|------|------|
| `/help` | 查看所有指令 |
| `/tasks` | 打印任务看板 |
| `/locks` | 查看所有文件锁 |
| `/workers` | 查看运行中的队员进程 |
| `/save [file]` | 保存任务+锁状态到 JSON |
| `/load [file]` | 从 JSON 恢复状态 |
| `/clear-locks` | 强制清除所有锁（紧急用）|
| `/kill <pid>` | 终止指定队员进程 |

### 配套 Skill 文件

- `skills/orchestrator/SKILL.md`：队长操作规程（加载给 Gemini CLI）
- `skills/worker/SKILL.md`：队员操作规程（加载给 Qwen/Gemini CLI）

---
*设计初衷详见根目录下的 [doc.md](doc.md)*
