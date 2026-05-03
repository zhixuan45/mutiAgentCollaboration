using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RepoManager
{
    class Program
    {
        // filepath → agentId 的文件锁字典
        private static readonly ConcurrentDictionary<string, string> LockedFiles = new();
        // sessionId → 消息通道，用于从 POST 请求向 SSE 连接推送数据
        private static readonly ConcurrentDictionary<string, Channel<string>> Sessions = new();

        static async Task Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--stdio-bridge")
            {
                await RunStdioBridgeAsync("http://localhost:5000");
                return;
            }

            var builder = WebApplication.CreateBuilder(args);

            // 允许所有跨域请求，方便其他节点或 WebUI 直接连入 MCP
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
            });

            var app = builder.Build();
            app.UseCors();

            // 1. 处理 MCP 握手与长连接端点 (SSE)
            app.MapGet("/sse", async (HttpContext context) =>
            {
                var sessionId = Guid.NewGuid().ToString();
                var channel = Channel.CreateUnbounded<string>();
                Sessions.TryAdd(sessionId, channel);

                context.Response.Headers.Append("Content-Type", "text/event-stream");
                context.Response.Headers.Append("Cache-Control", "no-cache");
                context.Response.Headers.Append("Connection", "keep-alive");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[SSE Connect] 新会话已连接: {sessionId}");
                Console.ResetColor();

                // 发送 endpoint 事件，告诉客户端通过哪个 URL POST 消息
                var endpointUrl = $"/message?sessionId={sessionId}";
                await context.Response.WriteAsync($"event: endpoint\ndata: {endpointUrl}\n\n");
                await context.Response.Body.FlushAsync();

                try
                {
                    await foreach (var msg in channel.Reader.ReadAllAsync(context.RequestAborted))
                    {
                        await context.Response.WriteAsync($"event: message\ndata: {msg}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    Sessions.TryRemove(sessionId, out _);
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[SSE Disconnect] 会话已断开: {sessionId}");
                    Console.ResetColor();
                }
            });

            // 2. 处理大模型客户端实际请求的接收端点 (POST)
            app.MapPost("/message", async (HttpContext context, string sessionId) =>
            {
                if (!Sessions.TryGetValue(sessionId, out var channel))
                    return Results.NotFound("Session not found or expired.");

                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[POST In] sessionId: {sessionId}");
                Console.WriteLine(body);
                Console.ResetColor();

                try
                {
                    var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("method", out var methodElement))
                    {
                        var method = methodElement.GetString();
                        var id = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : (JsonElement?)null;
                        await HandleRequest(method, root, id, channel);
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Error] 解析或处理请求失败: {ex.Message}");
                    Console.ResetColor();
                }

                return Results.Accepted();
            });

            PrintBanner();

            // 启动控制台交互循环（后台线程，不阻塞 Web 服务）
            _ = Task.Run(RunConsoleLoopAsync);

            app.Urls.Add("http://localhost:5000");
            await app.RunAsync();
        }

        // ---------- 控制台交互循环 ----------

        static async Task RunConsoleLoopAsync()
        {
            // 等待服务器完全启动
            await Task.Delay(1000);
            Console.WriteLine("输入 /help 查看可用指令，直接回车可刷新状态。");

            while (true)
            {
                var input = Console.ReadLine();
                if (input == null) break;
                if (string.IsNullOrWhiteSpace(input)) continue;

                // 交给 TaskScheduler 处理控制台指令
                if (!TaskScheduler.HandleConsoleCommand(input, LockedFiles))
                    Console.WriteLine($"未知指令: {input}，输入 /help 查看帮助");
            }
        }

        // ---------- MCP 请求路由 ----------

        static async Task HandleRequest(string? method, JsonElement root, JsonElement? id, Channel<string> channel)
        {
            object? responseResult = null;
            object? errorResult = null;

            try
            {
                switch (method)
                {
                    case "initialize":
                        responseResult = new
                        {
                            protocolVersion = "2024-11-05",
                            capabilities = new { tools = new { } },
                            serverInfo = new { name = "RepoManager", version = "2.0.0" }
                        };
                        break;

                    case "notifications/initialized":
                        return; // Notification，无需回复

                    case "tools/list":
                        // 合并文件锁工具 + 任务调度工具
                        var allTools = new List<object>();
                        allTools.AddRange(GetLockToolDefinitions());
                        allTools.AddRange(TaskScheduler.GetToolDefinitions());
                        responseResult = new { tools = allTools };
                        break;

                    case "tools/call":
                        var paramsEl = root.GetProperty("params");
                        var toolName = paramsEl.GetProperty("name").GetString()!;
                        var argsEl   = paramsEl.GetProperty("arguments");

                        // 优先尝试任务调度工具，未命中再尝试文件锁工具
                        var taskResult = TaskScheduler.HandleToolCall(toolName, argsEl, LockedFiles);
                        if (taskResult != null)
                        {
                            responseResult = new { content = new[] { new { type = "text", text = taskResult.ToString() } } };
                        }
                        else
                        {
                            responseResult = HandleLockTool(toolName, argsEl);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                errorResult = new { code = -32603, message = ex.Message };
            }

            if (id.HasValue && (responseResult != null || errorResult != null))
            {
                var response = new Dictionary<string, object>
                {
                    { "jsonrpc", "2.0" },
                    { "id", id.Value.ValueKind == JsonValueKind.Number
                        ? id.Value.GetInt32()
                        : (object)id.Value.GetString()! }
                };
                if (errorResult != null) response["error"] = errorResult;
                else response["result"] = responseResult!;

                var jsonStr = JsonSerializer.Serialize(response,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[SSE Out] 发送至通道:\n{jsonStr}");
                Console.ResetColor();

                await channel.Writer.WriteAsync(jsonStr);
            }
        }

        // ---------- 文件锁工具定义 ----------

        static object[] GetLockToolDefinitions() => new object[]
        {
            new {
                name = "acquire_lock",
                description = "获取工作区文件或目录的独占修改锁。在你修改文件之前必须调用此工具。",
                inputSchema = new {
                    type = "object",
                    properties = new {
                        filepath = new { type = "string", description = "要锁定的目标文件路径" },
                        agent_id = new { type = "string", description = "【重要提示】你必须为自己生成一个全局唯一且不重复的标识符（例如UUID或带有时间戳的随机哈希），绝对禁止使用类似'agent1'、'test'等通用名称。在你的整个操作周期内必须一致使用此唯一ID。" }
                    },
                    required = new[] { "filepath", "agent_id" }
                }
            },
            new {
                name = "release_lock",
                description = "释放你占用过的文件锁。提交代码后必须调用。",
                inputSchema = new {
                    type = "object",
                    properties = new {
                        filepath = new { type = "string", description = "要释放的文件路径" },
                        agent_id = new { type = "string", description = "与 acquire_lock 时完全一致的唯一标识符。" }
                    },
                    required = new[] { "filepath", "agent_id" }
                }
            },
            new {
                name = "check_status",
                description = "查看当前项目被锁定的文件状态列表",
                inputSchema = new {
                    type = "object",
                    properties = new {
                        filepath = new { type = "string", description = "（可选）查询特定文件" }
                    }
                }
            }
        };

        // ---------- 文件锁工具执行 ----------

        static object HandleLockTool(string toolName, JsonElement argsEl)
        {
            switch (toolName)
            {
                case "acquire_lock":
                {
                    var filepath = argsEl.GetProperty("filepath").GetString()!;
                    var agentId  = argsEl.GetProperty("agent_id").GetString()!;
                    return new { content = new[] { new { type = "text", text = AcquireLock(filepath, agentId) } } };
                }
                case "release_lock":
                {
                    var filepath = argsEl.GetProperty("filepath").GetString()!;
                    var agentId  = argsEl.GetProperty("agent_id").GetString()!;
                    return new { content = new[] { new { type = "text", text = ReleaseLock(filepath, agentId) } } };
                }
                case "check_status":
                    return new { content = new[] { new { type = "text", text = CheckStatus() } } };

                default:
                    throw new Exception($"Unknown tool: {toolName}");
            }
        }

        static string AcquireLock(string filepath, string agentId)
        {
            if (LockedFiles.TryAdd(filepath, agentId))
                return $"Success: [{filepath}] 已被 [{agentId}] 锁定，可以开始编辑。";

            LockedFiles.TryGetValue(filepath, out var owner);
            if (owner == agentId) return $"Success: 你 ([{agentId}]) 已持有 [{filepath}] 的锁。";
            return $"Failed: [{filepath}] 当前被其他 Agent ([{owner}]) 占用，请等待或处理其他任务。";
        }

        static string ReleaseLock(string filepath, string agentId)
        {
            if (LockedFiles.TryGetValue(filepath, out var owner) && owner == agentId)
            {
                LockedFiles.TryRemove(filepath, out _);
                return $"Success: [{filepath}] 的锁已释放。";
            }
            return $"Failed: 你无法释放 [{filepath}]，因为你不是持有者。（当前持有者: {owner ?? "无"}）";
        }

        static string CheckStatus()
        {
            if (LockedFiles.IsEmpty) return "当前没有文件被锁定。";
            var lines = new List<string> { "=== 文件锁状态 ===" };
            foreach (var kv in LockedFiles)
                lines.Add($"🔒 {kv.Key} → {kv.Value}");
            return string.Join("\n", lines);
        }

        // ---------- Stdio Bridge 模式 ----------

        static async Task RunStdioBridgeAsync(string serverBaseUrl)
        {
            using var client = new System.Net.Http.HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            string? endpointUrl = null;

            try
            {
                using var request = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get, $"{serverBaseUrl}/sse");
                request.Headers.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                using var response = await client.SendAsync(
                    request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync();
                using var sseReader = new StreamReader(stream);

                // 后台监听 SSE 并转发到 stdout
                var sseTask = Task.Run(async () =>
                {
                    string? line;
                    while ((line = await sseReader.ReadLineAsync()) != null)
                    {
                        if (line.StartsWith("event: endpoint"))
                        {
                            var dataLine = await sseReader.ReadLineAsync();
                            endpointUrl = dataLine!.Substring(6).Trim();
                        }
                        else if (line.StartsWith("event: message"))
                        {
                            var dataLine = await sseReader.ReadLineAsync();
                            var json = dataLine!.Substring(6).Trim();
                            Console.WriteLine(json);
                            Console.Out.Flush();
                        }
                    }
                });

                // 前台读取 stdin 并 POST 给 Server
                using var stdinReader = new StreamReader(Console.OpenStandardInput());
                string? inputLine;
                while ((inputLine = await stdinReader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(inputLine)) continue;
                    while (endpointUrl == null) await Task.Delay(50); // 等待握手完成
                    var content = new System.Net.Http.StringContent(
                        inputLine, System.Text.Encoding.UTF8, "application/json");
                    await client.PostAsync($"{serverBaseUrl}{endpointUrl}", content);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Bridge Error: " + ex.Message);
            }
        }

        // ---------- 启动横幅 ----------

        static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=================================================");
            Console.WriteLine("🚀 RepoManager v2.0 (HTTP/SSE + 任务调度)");
            Console.WriteLine("=================================================");
            Console.WriteLine("监听地址  : http://localhost:5000");
            Console.WriteLine("SSE 端点  : http://localhost:5000/sse");
            Console.WriteLine("-------------------------------------------------");
            Console.WriteLine("🔧 文件锁工具 : acquire_lock / release_lock / check_status");
            Console.WriteLine("📋 任务调度   : create_task / poll_my_task / update_task");
            Console.WriteLine("              review_task / list_all_tasks / spawn_worker");
            Console.WriteLine("-------------------------------------------------");
            Console.WriteLine("💻 控制台指令 : /help /tasks /locks /workers /save /load");
            Console.WriteLine("=================================================");
            Console.ResetColor();
        }
    }
}
