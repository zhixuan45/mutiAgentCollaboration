---
name: Collaborative Git Workflow
description: 多智能体协同开发时的标准代码同步与防冲突流。所有 Agent 在准备提交代码前必须自动触发此技能。
---

# Collaborative Git Workflow (协作 Git 工作流)

## 🎯 目的
在多智能体共享同一分支（单分支协作）开发时，防止因并发提交导致代码覆盖。此技能将常规的 `git push` 包装为一套安全的自动化流程。

## ⚙️ 触发条件
*   当 Agent 准备将本地修改推送到远端仓库时。
*   触发词包括：`commit`, `push`, `提交代码`, `推送`, `同步代码`。

## 📋 执行动作流

严格按照以下顺序执行。如果某一步骤失败，不要强制跳过，而是将错误原因返回给用户（或触发专门的 Bug 解决技能）。

### 1. 状态检查 (Check)
*   检查工作区是否干净。
*   确认通过 MCP (`RepoManager`) 获得了正在修改的文件的锁。如果没有锁，严禁提交。

### 2. 拉取与变基 (Pull & Rebase)
*   执行 `git fetch origin`
*   执行 `git pull --rebase origin main` (或当前工作分支)
*   **冲突处理准则**：如果在 rebase 过程中发生合并冲突（Merge Conflict）：
    *   **不要**盲目覆盖。
    *   检查冲突内容，如果是不同的功能，尝试合并。
    *   如果是同一段逻辑的修改，优先保留 `origin` (主干) 的逻辑，并将你新开发的逻辑作为补充或插件重新注入。
    *   如果冲突超出处理能力，执行 `git rebase --abort`，然后提示用户介入。

### 3. 验证 (Verify)
*   如果项目包含测试脚本（如 `npm test` 或 `dotnet test`），必须运行并确保全部通过。

### 4. 提交与推送 (Commit & Push)
*   使用符合规范的 Commit Message：`feat/fix/chore(模块名): 操作描述 (中文)`
*   执行 `git push origin HEAD`
*   **严禁**使用 `--force` 或 `-f`。

### 5. 清理 (Cleanup)
*   推送成功后，立即调用 MCP Server 的 `release_lock` 接口，释放你占用过的文件锁。
