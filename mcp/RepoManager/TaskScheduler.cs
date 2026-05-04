using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoManager
{
    // 任务状态枚举
    public enum TaskStatus { Pending, InProgress, Done, Failed, Rejected }

    // 单个任务的完整数据
    public class AgentTask
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string AssigneeRole { get; set; } = "";   // 指定给哪个角色
        public string? AssignedAgentId { get; set; }     // 实际接手的队员 ID
        public TaskStatus Status { get; set; } = TaskStatus.Pending;
        public string? Result { get; set; }              // 队员上报的结果摘要
        public string? ReviewComment { get; set; }       // 队长审查意见
        public string Priority { get; set; } = "normal"; // high / normal / low
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }

    // 派生子进程记录
    public class WorkerProcess
    {
        public int Pid { get; set; }
        public string RoleName { get; set; } = "";
        public string CliType { get; set; } = "gemini";
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    }

    // 任务调度器：管理任务池与子进程
    public static class TaskScheduler
    {
        // 任务池：taskId → AgentTask
        private static readonly ConcurrentDictionary<string, AgentTask> Tasks = new();
        // 子进程记录：pid → WorkerProcess
        private static readonly ConcurrentDictionary<int, WorkerProcess> Workers = new();

        // ---------- 任务 CRUD ----------

        /// <summary>队长创建任务</summary>
        public static AgentTask CreateTask(string title, string description, string assigneeRole, string priority = "normal")
        {
            var task = new AgentTask
            {
                Title = title,
                Description = description,
                AssigneeRole = assigneeRole.ToLower(),
                Priority = priority
            };
            Tasks[task.Id] = task;
            LogTask($"[CREATE] #{task.Id} [{task.AssigneeRole}] {task.Title}", ConsoleColor.Green);
            return task;
        }

        /// <summary>队员轮询属于自己角色且处于 Pending 状态的任务</summary>
        public static AgentTask? PollTask(string roleName, string agentId)
        {
            // 按优先级排序：high > normal > low
            var pending = Tasks.Values
                .Where(t => t.AssigneeRole == roleName.ToLower() && t.Status == TaskStatus.Pending)
                .OrderBy(t => t.Priority == "high" ? 0 : t.Priority == "normal" ? 1 : 2)
                .ThenBy(t => t.CreatedAt)
                .FirstOrDefault();

            if (pending == null) return null;

            // 原子状态变更：Pending → InProgress
            pending.Status = TaskStatus.InProgress;
            pending.AssignedAgentId = agentId;
            pending.UpdatedAt = DateTime.UtcNow;
            LogTask($"[ASSIGN] #{pending.Id} → 队员 {agentId}", ConsoleColor.Cyan);
            return pending;
        }

        /// <summary>队员上报任务结果</summary>
        public static string UpdateTask(string taskId, string status, string result, string agentId)
        {
            if (!Tasks.TryGetValue(taskId, out var task))
                return $"Error: 任务 #{taskId} 不存在";
            if (task.AssignedAgentId != agentId)
                return $"Error: 任务 #{taskId} 不属于你（当前执行者: {task.AssignedAgentId}）";

            task.Status = status.ToLower() switch
            {
                "done" => TaskStatus.Done,
                "failed" => TaskStatus.Failed,
                _ => task.Status
            };
            task.Result = result;
            task.UpdatedAt = DateTime.UtcNow;
            LogTask($"[UPDATE] #{task.Id} → {task.Status} by {agentId}", ConsoleColor.Yellow);
            return $"Success: 任务 #{taskId} 已更新为 {task.Status}";
        }

        /// <summary>队长审查任务，批准或打回</summary>
        public static string ReviewTask(string taskId, bool approved, string? comment = null)
        {
            if (!Tasks.TryGetValue(taskId, out var task))
                return $"Error: 任务 #{taskId} 不存在";
            if (task.Status != TaskStatus.Done && task.Status != TaskStatus.Failed)
                return $"Error: 任务 #{taskId} 当前状态为 {task.Status}，还未完成，无法审查";

            task.ReviewComment = comment;
            task.UpdatedAt = DateTime.UtcNow;

            if (approved)
            {
                // 保持 Done 状态，审查通过
                LogTask($"[REVIEW ✅] #{task.Id} 通过", ConsoleColor.Green);
                return $"Success: 任务 #{taskId} 审查通过";
            }
            else
            {
                // 打回：重置为 Pending，清空执行者，队员重新领取
                task.Status = TaskStatus.Pending;
                task.AssignedAgentId = null;
                task.Result = null;
                LogTask($"[REVIEW ❌] #{task.Id} 打回重做: {comment}", ConsoleColor.Red);
                return $"Success: 任务 #{taskId} 已打回，附加意见: {comment ?? "无"}";
            }
        }

        /// <summary>列出所有任务的看板视图</summary>
        public static string ListAllTasks()
        {
            if (Tasks.IsEmpty) return "任务池为空";
            var lines = new List<string> { "=== 任务看板 ===" };
            foreach (var t in Tasks.Values.OrderBy(x => x.CreatedAt))
            {
                var icon = t.Status switch
                {
                    TaskStatus.Pending => "⏳",
                    TaskStatus.InProgress => "🔄",
                    TaskStatus.Done => "✅",
                    TaskStatus.Failed => "❌",
                    TaskStatus.Rejected => "🔁",
                    _ => "?"
                };
                lines.Add($"{icon} #{t.Id} [{t.Priority}] [{t.AssigneeRole}] {t.Title} → {t.Status}");
                if (t.Result != null) lines.Add($"   └ 结果: {t.Result}");
                if (t.ReviewComment != null) lines.Add($"   └ 审查意见: {t.ReviewComment}");
            }
            return string.Join("\n", lines);
        }

        /// <summary>获取极简任务进度摘要（节省 Token）</summary>
        public static string GetTaskSummary()
        {
            if (Tasks.IsEmpty) return "任务池为空";
            int total = Tasks.Count;
            int pending = Tasks.Values.Count(t => t.Status == TaskStatus.Pending);
            int inProgress = Tasks.Values.Count(t => t.Status == TaskStatus.InProgress);
            int done = Tasks.Values.Count(t => t.Status == TaskStatus.Done);
            int failed = Tasks.Values.Count(t => t.Status == TaskStatus.Failed);
            
            return $"进度概览: 总计 {total} | ⏳ 待处理 {pending} | 🔄 进行中 {inProgress} | ✅ 已完成 {done} | ❌ 失败 {failed}";
        }

        // ---------- 子进程管理 ----------

        /// <summary>派生队员子进程（默认交互模式），工作目录跟随队长</summary>
        public static string SpawnWorker(string roleName, string taskId, string mcpUrl, string workingDir)
        {
            if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
            {
                var fallback = Directory.GetCurrentDirectory();
                LogTask($"[SPAWN ⚠️] 指定工作目录不存在，回退到: {fallback}", ConsoleColor.Yellow);
                workingDir = fallback;
            }

            var systemPrompt =
                $"You are a worker agent with role [{roleName}] in a multi-agent system. " +
                $"Your task ID to work on is [{taskId}]. " +
                $"MCP Server is at [{mcpUrl}]. " +
                $"Working directory is [{workingDir}]. " +
                $"Follow the worker skill: poll_my_task with role_name={roleName}, execute the task, then update_task with your result. " +
                $"Acquire file locks before editing files. Release locks after committing.";

            try
            {
                // 以默认交互模式启动 gemini，传入 systemPrompt 作为第一句话
                var psi = new ProcessStartInfo("gemini", $"\"{systemPrompt}\"")
                {
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WorkingDirectory = workingDir
                };

                var process = Process.Start(psi)
                    ?? throw new Exception("Process.Start 返回 null，进程启动失败");

                var worker = new WorkerProcess
                {
                    Pid = process.Id,
                    RoleName = roleName,
                    CliType = "gemini"
                };
                Workers[process.Id] = worker;

                LogTask($"[SPAWN ✅] gemini 队员已启动 PID={process.Id} role={roleName} dir={workingDir}", ConsoleColor.Green);
                return $"Success: gemini 队员进程已在默认模式下启动，PID={process.Id}，角色={roleName}，工作目录={workingDir}";
            }
            catch (Exception ex)
            {
                LogTask($"[SPAWN ❌] 启动 gemini 失败: {ex.Message}", ConsoleColor.Red);
                return $"Error: 启动 gemini 队员失败 → {ex.Message}。请确认 gemini CLI 已安装并在 PATH 中。";
            }
        }

        /// <summary>通过 VBScript 模拟键盘输入向运行中的队员追加提示词</summary>
        public static string SendWorkerPrompt(string roleName, string prompt)
        {
            var worker = Workers.Values.FirstOrDefault(w => w.RoleName == roleName);
            if (worker == null) return $"Error: 未找到角色为 {roleName} 的运行中队员";

            try
            {
                // VBScript SendKeys 转义
                var specialChars = new[] { '+', '^', '%', '~', '(', ')', '[', ']', '{', '}' };
                var escaped = new System.Text.StringBuilder();
                foreach (var c in prompt)
                {
                    if (specialChars.Contains(c)) escaped.Append("{").Append(c).Append("}");
                    else if (c == '"') escaped.Append("\"\""); // 转义双引号
                    else escaped.Append(c);
                }

                var vbsPath = Path.Combine(Path.GetTempPath(), $"poke_{Guid.NewGuid():N}.vbs");
                var vbsCode = $@"
Set WshShell = WScript.CreateObject(""WScript.Shell"")
success = WshShell.AppActivate({worker.Pid})
If success Then
    WScript.Sleep 500
    WshShell.SendKeys ""{escaped}""
    WScript.Sleep 100
    WshShell.SendKeys ""~""
End If
";
                File.WriteAllText(vbsPath, vbsCode);
                var psi = new ProcessStartInfo("cscript", $"//nologo \"{vbsPath}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit();
                File.Delete(vbsPath);

                LogTask($"[PROMPT] 已向 {roleName} 注入提示词", ConsoleColor.Cyan);
                return $"Success: 已成功唤醒窗口并向 {roleName} (PID={worker.Pid}) 发送了提示词";
            }
            catch (Exception ex)
            {
                return $"Error: 发送提示词失败 → {ex.Message}";
            }
        }

        /// <summary>终止指定 PID 的队员进程</summary>
        public static string KillWorker(int pid)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                proc.Kill();
                Workers.TryRemove(pid, out _);
                LogTask($"[KILL] PID={pid} 已终止", ConsoleColor.Red);
                return $"Success: 进程 PID={pid} 已终止";
            }
            catch (Exception ex)
            {
                return $"Error: 终止 PID={pid} 失败 → {ex.Message}";
            }
        }

        // ---------- 持久化 ----------

        /// <summary>保存任务池到 JSON 文件</summary>
        public static string Save(string filename)
        {
            try
            {
                var data = new { tasks = Tasks.Values.ToList() };
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filename, json);
                return $"Success: 已保存 {Tasks.Count} 个任务到 {filename}";
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        /// <summary>从 JSON 文件恢复任务池</summary>
        public static string Load(string filename)
        {
            try
            {
                if (!File.Exists(filename)) return $"Error: 文件 {filename} 不存在";
                var json = File.ReadAllText(filename);
                var doc = JsonDocument.Parse(json);
                var tasksArr = doc.RootElement.GetProperty("tasks");
                var loaded = JsonSerializer.Deserialize<List<AgentTask>>(tasksArr.GetRawText())!;
                Tasks.Clear();
                foreach (var t in loaded) Tasks[t.Id] = t;
                return $"Success: 已从 {filename} 恢复 {loaded.Count} 个任务";
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        // ---------- 控制台指令 ----------

        /// <summary>处理 /xxx 控制台指令，返回 true 表示已处理</summary>
        public static bool HandleConsoleCommand(string input, ConcurrentDictionary<string, string> lockedFiles)
        {
            var parts = input.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLower();
            var arg = parts.Length > 1 ? parts[1] : "";

            switch (cmd)
            {
                case "/tasks":
                    Console.WriteLine(ListAllTasks());
                    return true;

                case "/locks":
                    // 查询所有文件锁
                    if (lockedFiles.IsEmpty) Console.WriteLine("当前没有文件被锁定");
                    else foreach (var kv in lockedFiles)
                        Console.WriteLine($"🔒 {kv.Key} → {kv.Value}");
                    return true;

                case "/save":
                    var saveFile = string.IsNullOrEmpty(arg) ? "repo_state.json" : arg;
                    Console.WriteLine(Save(saveFile));
                    return true;

                case "/load":
                    var loadFile = string.IsNullOrEmpty(arg) ? "repo_state.json" : arg;
                    Console.WriteLine(Load(loadFile));
                    return true;

                case "/clear-locks":
                    lockedFiles.Clear();
                    Console.WriteLine("⚠️ 所有文件锁已强制清除");
                    return true;

                case "/kill":
                    if (int.TryParse(arg, out var pid))
                        Console.WriteLine(KillWorker(pid));
                    else
                        Console.WriteLine("用法: /kill <PID>");
                    return true;

                case "/workers":
                    if (Workers.IsEmpty) Console.WriteLine("没有运行中的队员进程");
                    else foreach (var w in Workers.Values)
                        Console.WriteLine($"⚙️ PID={w.Pid} role={w.RoleName} 启动于={w.StartedAt:HH:mm:ss}");
                    return true;

                case "/help":
                    Console.WriteLine(
                        "可用指令:\n" +
                        "  /tasks          - 查看任务看板\n" +
                        "  /locks          - 查看所有文件锁\n" +
                        "  /workers        - 查看运行中的队员进程\n" +
                        "  /save [file]    - 保存状态到 JSON\n" +
                        "  /load [file]    - 从 JSON 恢复状态\n" +
                        "  /clear-locks    - 强制清除所有文件锁\n" +
                        "  /kill <pid>     - 终止队员进程");
                    return true;
            }
            return false;
        }

        // ---------- 工具定义（供 MCP tools/list 使用）----------

        /// <summary>生成任务调度相关的 MCP 工具定义列表</summary>
        public static object[] GetToolDefinitions()
        {
            return new object[]
            {
                new {
                    name = "create_task",
                    description = "【队长专用】创建一个子任务并放入任务池。指定执行角色后，目标队员会通过 poll_my_task 领取。",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            title       = new { type = "string", description = "任务标题（简洁概括）" },
                            description = new { type = "string", description = "任务详细描述，包括要实现的功能、验收标准等" },
                            assignee_role = new { type = "string", description = "指定给哪类队员，例如：backend_dev, frontend_dev, qa_tester, devops" },
                            priority    = new { type = "string", description = "优先级: high / normal / low，默认 normal", @enum = new[]{"high","normal","low"} }
                        },
                        required = new[] { "title", "description", "assignee_role" }
                    }
                },
                new {
                    name = "poll_my_task",
                    description = "【队员专用】按角色名轮询任务池中待执行的任务。成功领取后任务状态变为 in_progress。",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            role_name = new { type = "string", description = "你的角色名，必须与队长 create_task 时指定的 assignee_role 一致" },
                            agent_id  = new { type = "string", description = "你的唯一标识符（UUID），整个会话保持一致" }
                        },
                        required = new[] { "role_name", "agent_id" }
                    }
                },
                new {
                    name = "update_task",
                    description = "【队员专用】上报任务执行结果，状态可为 done 或 failed。",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            task_id  = new { type = "string", description = "任务 ID" },
                            status   = new { type = "string", description = "done / failed", @enum = new[]{"done","failed"} },
                            result   = new { type = "string", description = "结果摘要：完成了什么、修改了哪些文件" },
                            agent_id = new { type = "string", description = "你的唯一标识符，必须与 poll_my_task 时一致" }
                        },
                        required = new[] { "task_id", "status", "result", "agent_id" }
                    }
                },
                new {
                    name = "review_task",
                    description = "【队长专用】审查已完成的任务。批准则关闭任务，打回则任务重置为 pending 供队员重做。",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            task_id  = new { type = "string", description = "要审查的任务 ID" },
                            approved = new { type = "boolean", description = "true=批准，false=打回重做" },
                            comment  = new { type = "string", description = "审查意见（打回时必填，说明需要修改的地方）" }
                        },
                        required = new[] { "task_id", "approved" }
                    }
                },
                new {
                    name = "list_all_tasks",
                    description = "查看所有任务的看板概览，包含状态、角色、结果摘要。",
                    inputSchema = new {
                        type = "object",
                        properties = new { },
                    }
                },
                new {
                    name = "spawn_worker",
                    description = "【队长专用】自动派生一个 gemini CLI 队员进程。队员将在默认交互模式下启动（有完整的 MCP 工具）。",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            role_name    = new { type = "string", description = "队员角色名，例如 backend_dev" },
                            working_dir  = new { type = "string", description = "【必填】你（队长）当前的工作目录绝对路径，队员进程将在该目录下启动" },
                            task_id      = new { type = "string", description = "要告知队员优先处理的任务 ID（可选）" },
                            mcp_url      = new { type = "string", description = "MCP 服务器地址，默认 http://localhost:5000" }
                        },
                        required = new[] { "role_name", "working_dir" }
                    }
                },
                new {
                    name = "send_worker_prompt",
                    description = "【队长专用】向已经启动的队员控制台注入一条提示词。常用于在任务打回或分配新任务后唤醒队员。",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            role_name    = new { type = "string", description = "你要唤醒的队员角色名" },
                            prompt       = new { type = "string", description = "提示词内容。强烈建议只使用纯英文（如 'Task rejected, please poll again' 或 'Please continue'），以防止中文输入法导致的乱码。" }
                        },
                        required = new[] { "role_name", "prompt" }
                    }
                },
                new {
                    name = "get_task_status_summary",
                    description = "【队长专用】获取极简的任务进度摘要，用于节省 Token 消耗。",
                    inputSchema = new {
                        type = "object",
                        properties = new { },
                    }
                }
            };
        }

        /// <summary>处理任务相关的 MCP tools/call</summary>
        public static object? HandleToolCall(string toolName, JsonElement argsEl, ConcurrentDictionary<string, string> lockedFiles)
        {
            switch (toolName)
            {
                case "create_task":
                {
                    var title       = argsEl.GetProperty("title").GetString()!;
                    var description = argsEl.GetProperty("description").GetString()!;
                    var role        = argsEl.GetProperty("assignee_role").GetString()!;
                    var priority    = argsEl.TryGetProperty("priority", out var p) ? p.GetString()! : "normal";
                    var task        = CreateTask(title, description, role, priority);
                    return $"Success: 任务已创建 #{task.Id} [{role}] {title}";
                }
                case "poll_my_task":
                {
                    var role    = argsEl.GetProperty("role_name").GetString()!;
                    var agentId = argsEl.GetProperty("agent_id").GetString()!;
                    var task    = PollTask(role, agentId);
                    if (task == null) return $"NoTask: 当前没有分配给 [{role}] 的待执行任务，请稍后再试";
                    return $"TaskAssigned:\n  ID: {task.Id}\n  Title: {task.Title}\n  Description: {task.Description}\n  Priority: {task.Priority}";
                }
                case "update_task":
                {
                    var taskId  = argsEl.GetProperty("task_id").GetString()!;
                    var status  = argsEl.GetProperty("status").GetString()!;
                    var result  = argsEl.GetProperty("result").GetString()!;
                    var agentId = argsEl.GetProperty("agent_id").GetString()!;
                    return UpdateTask(taskId, status, result, agentId);
                }
                case "review_task":
                {
                    var taskId   = argsEl.GetProperty("task_id").GetString()!;
                    var approved = argsEl.GetProperty("approved").GetBoolean();
                    var comment  = argsEl.TryGetProperty("comment", out var c) ? c.GetString() : null;
                    return ReviewTask(taskId, approved, comment);
                }
                case "list_all_tasks":
                    return ListAllTasks();

                case "spawn_worker":
                {
                    var roleName   = argsEl.GetProperty("role_name").GetString()!;
                    var workingDir = argsEl.GetProperty("working_dir").GetString()!;
                    var taskId     = argsEl.TryGetProperty("task_id", out var tid) ? tid.GetString()! : "";
                    var mcpUrl     = argsEl.TryGetProperty("mcp_url", out var mu) ? mu.GetString()! : "http://localhost:5000";
                    return SpawnWorker(roleName, taskId, mcpUrl, workingDir);
                }

                case "send_worker_prompt":
                {
                    var roleName = argsEl.GetProperty("role_name").GetString()!;
                    var prompt   = argsEl.GetProperty("prompt").GetString()!;
                    return SendWorkerPrompt(roleName, prompt);
                }

                case "get_task_status_summary":
                    return GetTaskSummary();
            }
            return null; // 不是任务相关工具
        }

        // 彩色控制台日志
        private static void LogTask(string msg, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(msg);
            Console.ResetColor();
        }
    }
}
