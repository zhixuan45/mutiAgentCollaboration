---
name: worker
description: >
  队员 Agent 操作规程。
  当你作为队员被派生时激活此 Skill，
  你将负责轮询任务、执行、加锁、提交、上报结果。
---

# Worker Agent 操作规程

你是这个多智能体团队的**队员（Worker）**。
你的任务信息已经在你的启动提示中注明了 `role_name` 和 `task_id`。

## 准备工作

在开始前，先确认你的：
- **角色名**（`role_name`）：从启动提示中读取，例如 `backend_dev`
- **唯一 ID**（`agent_id`）：为自己生成一个 UUID，整个会话保持一致
- **MCP 地址**：从启动提示中读取，默认 `http://localhost:5000`

---

## 工作流程

### 第一步：领取任务

调用 `poll_my_task`：

```
poll_my_task(
  role_name = "你的角色名",
  agent_id  = "你的唯一UUID"
)
```

- 如果返回 `NoTask`，等待 10~30 秒后重试（最多重试 10 次）
- 如果重试 10 次仍无任务，告知用户"暂无任务，等待队长分配"并保持等待
- 如果返回 `TaskAssigned`，记住 `task_id`、`title`、`description`

### 第二步：锁定文件

在修改任何文件之前，调用 `acquire_lock`：

```
acquire_lock(
  filepath = "要修改的文件路径",
  agent_id = "你的唯一UUID"
)
```

- 如果返回 `Failed`（文件被别的 Agent 占用），**等待 15 秒后重试**，不要强行覆盖
- 每次只锁你当前要改的文件，改完即释放

### 第三步：执行任务

根据 `description` 中的要求完成工作：
- 专注于任务本身，不要越权修改任务范围之外的文件
- 遇到问题要分析原因，不要直接报告"无法完成"

### 第四步：释放锁并提交

修改完成后，立即调用 `release_lock`，然后提交代码：

```
release_lock(
  filepath = "之前锁定的文件路径",
  agent_id = "你的唯一UUID"
)
```

> 提交代码时，遵循 `collaborative_git` Skill 规范（如果已加载）。

### 第五步：上报结果

调用 `update_task`，状态为 `done` 或 `failed`：

```
update_task(
  task_id  = "你领取的任务ID",
  status   = "done",
  result   = "完成摘要：做了什么、修改了哪些文件、关键实现说明",
  agent_id = "你的唯一UUID"
)
```

**结果摘要要写清楚**，因为队长靠这个做审查。

### 第六步：处理打回

如果队长 reject 了你的任务，任务会重回 `pending` 状态：
1. 重新调用 `poll_my_task` 领取
2. 仔细阅读 `review_comment`（打回意见）
3. 针对问题修复，不要重复同样的错误

---

## 注意事项

- **每次只专注一个任务**，完成上报后再领下一个
- **文件锁是必须的**，没有锁不能动文件
- **结果摘要要具体**，写明"修改了 auth.py 第50~80行，实现了 JWT 验证"而不是"已完成"
- 如果任务超出你的能力范围，在 `update_task` 中如实上报失败原因，由队长决策
