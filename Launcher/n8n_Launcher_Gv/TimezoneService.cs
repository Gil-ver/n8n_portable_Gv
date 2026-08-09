using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace n8n_launcher_Gv;

/// <summary>
/// 时区服务：
///   1. 从 app/node_modules/n8n/dist/timezones.json 加载 n8n 官方支持的 IANA 时区清单。
///   2. 调用便携运行时 runtime/node/node.exe 检测系统时区（Intl.DateTimeFormat().resolvedOptions().timeZone）。
///   3. 根据用户设置（"auto" 或具体 IANA）解析出最终注入 n8n 进程的 GENERIC_TIMEZONE / TZ 环境变量值。
///
/// 设计原则：
///   · 静默 fallback：出错永远回落到 UTC，绝不阻止 n8n 启动。
///   · 便携优先：只使用便携包内自带的 node.exe / timezones.json，不依赖用户系统安装。
///   · 结果缓存：加载/检测结果按 portableRoot 缓存，避免每次启动重复 IO 与进程调用。
/// </summary>
internal sealed class TimezoneService
{
    private const string AutoTimezoneToken = "auto";
    private const string FallbackTimezone = "UTC";
    private const int DetectionTimeoutMs = 3000;

    private readonly object _lock = new();
    private IReadOnlyList<string>? _cachedSupportedTimezones;
    private string? _cachedSupportedTimezonesRoot;
    private string? _cachedDetectedSystemTimezone;
    private string? _cachedDetectedRoot;

    public static string AutoToken => AutoTimezoneToken;
    public static string Fallback => FallbackTimezone;

    /// <summary>
    /// 加载 n8n 支持的 IANA 时区列表。加载失败返回空列表（UI 会自行处理）。
    /// </summary>
    public IReadOnlyList<string> LoadSupportedTimezones(string portableRoot)
    {
        if (string.IsNullOrWhiteSpace(portableRoot))
            return Array.Empty<string>();

        lock (_lock)
        {
            if (_cachedSupportedTimezones != null &&
                string.Equals(_cachedSupportedTimezonesRoot, portableRoot, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedSupportedTimezones;
            }

            try
            {
                string jsonPath = Path.Combine(portableRoot, "app", "node_modules", "n8n", "dist", "timezones.json");
                if (!File.Exists(jsonPath))
                {
                    Debug.WriteLine($"[Timezone] timezones.json not found: {jsonPath}");
                    _cachedSupportedTimezones = Array.Empty<string>();
                    _cachedSupportedTimezonesRoot = portableRoot;
                    return _cachedSupportedTimezones;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var list = new List<string>(400);

                // n8n timezones.json 里可能是数组，也可能是对象（{ "Africa/Abidjan": "...", ... }）。
                // 两种形态都兼容。
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in doc.RootElement.EnumerateArray())
                    {
                        string? name = null;
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            name = item.GetString();
                        }
                        else if (item.ValueKind == JsonValueKind.Object)
                        {
                            if (item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                                name = nameElement.GetString();
                            else if (item.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String)
                                name = valueElement.GetString();
                        }

                        if (!string.IsNullOrWhiteSpace(name))
                            list.Add(name!.Trim());
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    // n8n 官方结构：{ "data": { "Africa/Abidjan": "Africa/Abidjan", ... } }
                    // 如果根对象包含 data 且为 Object，则下潜一层再遍历。
                    JsonElement target = doc.RootElement;
                    if (target.TryGetProperty("data", out var dataElement)
                        && dataElement.ValueKind == JsonValueKind.Object)
                    {
                        target = dataElement;
                    }

                    foreach (JsonProperty prop in target.EnumerateObject())
                    {
                        string name = prop.Name?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(name))
                            list.Add(name);
                    }
                }

                // 去重 + 稳定字母序
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sorted = list
                    .Where(name => seen.Add(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _cachedSupportedTimezones = sorted;
                _cachedSupportedTimezonesRoot = portableRoot;
                Debug.WriteLine($"[Timezone] Loaded {sorted.Count} IANA zones from {jsonPath}");
                return _cachedSupportedTimezones;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Timezone] Load supported list error: {ex.Message}");
                _cachedSupportedTimezones = Array.Empty<string>();
                _cachedSupportedTimezonesRoot = portableRoot;
                return _cachedSupportedTimezones;
            }
        }
    }

    /// <summary>
    /// 使用便携运行时 node.exe 检测当前系统时区。3 秒超时；任何异常返回 null（调用方按 UTC 回落）。
    /// </summary>
    public string? DetectSystemTimezone(string portableRoot)
    {
        if (string.IsNullOrWhiteSpace(portableRoot))
            return null;

        lock (_lock)
        {
            if (_cachedDetectedSystemTimezone != null &&
                string.Equals(_cachedDetectedRoot, portableRoot, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedDetectedSystemTimezone;
            }
        }

        string detected;
        try
        {
            string nodeExe = Path.Combine(portableRoot, "runtime", "node", "node.exe");
            if (!File.Exists(nodeExe))
            {
                Debug.WriteLine($"[Timezone] node.exe not found: {nodeExe}");
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = nodeExe,
                Arguments = "-e \"process.stdout.write(Intl.DateTimeFormat().resolvedOptions().timeZone || '')\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = portableRoot
            };

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            string stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(DetectionTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                Debug.WriteLine("[Timezone] node.exe timezone detection timed out.");
                return null;
            }

            detected = stdout?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Timezone] Detect error: {ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(detected))
            return null;

        lock (_lock)
        {
            _cachedDetectedSystemTimezone = detected;
            _cachedDetectedRoot = portableRoot;
        }

        Debug.WriteLine($"[Timezone] System timezone detected: {detected}");
        return detected;
    }

    /// <summary>
    /// 决策出真正要注入 n8n 的时区。
    ///   · userChoice 为空 / "auto" → 调 node.exe 检测；检测失败 → UTC。
    ///   · userChoice 为具体 IANA → 与支持清单校验；不在清单里 → UTC。
    /// </summary>
    public string ResolveEffectiveTimezone(string portableRoot, string? userChoice)
    {
        var supported = LoadSupportedTimezones(portableRoot);

        string trimmed = userChoice?.Trim() ?? string.Empty;
        bool isAuto = trimmed.Length == 0
            || string.Equals(trimmed, AutoTimezoneToken, StringComparison.OrdinalIgnoreCase);

        if (isAuto)
        {
            string? detected = DetectSystemTimezone(portableRoot);
            if (!string.IsNullOrWhiteSpace(detected))
            {
                // 支持清单为空时仍认为检测有效（不阻拦）。
                if (supported.Count == 0 ||
                    supported.Contains(detected, StringComparer.OrdinalIgnoreCase))
                {
                    return detected!;
                }
                Debug.WriteLine($"[Timezone] Detected '{detected}' not in n8n supported list, falling back to UTC.");
            }
            return FallbackTimezone;
        }

        if (supported.Count == 0 ||
            supported.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        Debug.WriteLine($"[Timezone] User choice '{trimmed}' not in n8n supported list, falling back to UTC.");
        return FallbackTimezone;
    }

    /// <summary>
    /// 归一化用户存储值：空 → "auto"；非空原样返回（大小写保持）。
    /// </summary>
    public static string NormalizeUserChoice(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || string.Equals(trimmed, AutoTimezoneToken, StringComparison.OrdinalIgnoreCase))
            return AutoTimezoneToken;
        return trimmed;
    }
}