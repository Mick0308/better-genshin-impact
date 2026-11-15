using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Service.AccountRecords;
using BetterGenshinImpact.Service.AccountRecords.Model;
using Microsoft.ClearScript;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace BetterGenshinImpact.Core.Script.Dependence;

/// <summary>每个 JS 引擎独立的库存入口；业务校验由库存服务处理。</summary>
public sealed class ScriptInventory
{
    /// <summary>共享库存服务。</summary>
    private readonly InventoryStore _store;
    /// <summary>宿主确定的来源及批次。</summary>
    private readonly ScriptInventoryContext _context;
    /// <summary>传统入口首次访问时读取 UID，不扫描背包。</summary>
    private readonly Func<string?> _readUid;
    /// <summary>任务退出后不再接收调用。</summary>
    private readonly CancellationToken _ct;
    /// <summary>串行化 JS 调用，保证记录编号及提交顺序。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>本次执行绑定的游戏 UID。</summary>
    private string? _uid;
    /// <summary>本次执行跨提交和分组递增的记录编号，切换分组不复用编号。</summary>
    private long _recordNumber;
    /// <summary>输出原生 JS 对象所用的字段命名。</summary>
    private static readonly JsonSerializerSettings OutputSettings = new()
    { ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() } };

    /// <summary>注入存储和身份解析，不在宿主对象内查找全局服务。</summary>
    public ScriptInventory(InventoryStore store, ScriptInventoryContext context, Func<string?> readUid, CancellationToken ct)
    {
        _store = store;
        _context = context;
        _readUid = readUid;
        _ct = ct;
        _uid = context.VerifiedUid;
    }

    /// <summary>注册两个原生 JS 入口，数组和返回值不暴露 CLR 集合。</summary>
    internal static void Register(IScriptEngine engine, ScriptInventory inventory)
    {
        engine.AddHostObject("__bgiInventory", inventory);
        engine.Execute("""
            (() => {
                const host = __bgiInventory;
                globalThis.inventory = Object.freeze({
                    report: async (records, options = {}) => {
                        if (!Array.isArray(records)) throw new TypeError('inventory.report 需要记录数组');
                        if (options === null || typeof options !== 'object' || Array.isArray(options))
                            throw new TypeError('inventory.report 的 options 需要对象');
                        return JSON.parse(await host.Report(JSON.stringify(records), JSON.stringify(options)));
                    },
                    getQuantities: async names => {
                        if (typeof names !== 'string' && !Array.isArray(names))
                            throw new TypeError('inventory.getQuantities 需要名称或名称数组');
                        return JSON.parse(await host.GetQuantities(JSON.stringify(names)));
                    }
                });
            })();
            """);
    }

    /// <summary>接收数组；结构先整体校验，业务失败按记录返回并保留追踪。</summary>
    public async Task<string> Report(string json, string optionsJson = "{}")
    {
        using var operation = ScriptHostOperations.Enter();
        _ct.ThrowIfCancellationRequested();
        var input = Parse(json) as JArray ?? throw new ArgumentException("库存上报必须是数组。");
        var reports = input.Select(ParseRecord).ToArray();
        // 分组参数在任何写入前校验；不把拼错的参数静默归入默认批次。
        if (Parse(optionsJson) is not JObject options || options.Properties().Any(p => p.Name != "batch") ||
            options["batch"] is { Type: not JTokenType.String })
            throw new ArgumentException("options 仅支持可选的字符串 batch 分组名。");
        var batchId = _context.GetBatchId((string?)options["batch"]);
        await _gate.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            _ct.ThrowIfCancellationRequested();
            var uid = ResolveUid();
            var result = new InventoryReportResult { BatchId = batchId };
            var source = new AccountRecordSource { Reference = _context.Source, DisplayName = _context.DisplayName };
            // 按数组顺序处理，不按类型分组，也不按名称去重。
            foreach (var (report, _) in reports)
                report.RecordId = checked(++_recordNumber).ToString(CultureInfo.InvariantCulture);
            for (var index = 0; index < reports.Length; index++)
            {
                _ct.ThrowIfCancellationRequested();
                var (report, delta) = reports[index];
                // 不同物品的连续实测一次保存；同名实测必须分开，避免触发底层单次重复名称校验。
                var snapshots = new List<InventoryReport> { report };
                var names = new HashSet<string>(StringComparer.Ordinal) { report.Name.Trim() };
                if (!delta)
                    while (index + 1 < reports.Length && !reports[index + 1].Delta &&
                        names.Add(reports[index + 1].Report.Name.Trim()))
                        snapshots.Add(reports[++index].Report);
                var part = delta
                    ? await _store.ReportDeltaAsync(uid, source, [new InventoryDeltaReport
                    { RecordId = report.RecordId, Name = report.Name, Delta = report.Quantity,
                        RecordedAt = report.RecordedAt!, Expirations = report.Expirations }], batchId).ConfigureAwait(false)
                    : await _store.ReportAsync(uid, source, snapshots, batchId: batchId).ConfigureAwait(false);
                result.Records.AddRange(part.Records);
                result.Accepted.AddRange(part.Accepted);
                foreach (var pair in part.Rejected) result.Rejected[pair.Key] = pair.Value;
                foreach (var pair in part.Warnings) result.Warnings[pair.Key] = pair.Value;
                result.DuplicateCount += part.DuplicateCount;
                result.DuplicateErrorCount += part.DuplicateErrorCount;
            }
            return JsonConvert.SerializeObject(result, OutputSettings);
        }
        finally { _gate.Release(); }
    }

    /// <summary>读取最后确认数量；不存在返回 null，不累计待核实增量。</summary>
    public async Task<string> GetQuantities(string json)
    {
        using var operation = ScriptHostOperations.Enter();
        _ct.ThrowIfCancellationRequested();
        var input = Parse(json);
        var values = input is JArray array ? array.ToArray() : new[] { input };
        if (values.Any(value => value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)value)))
            throw new ArgumentException("查询名称必须是非空字符串。");
        var names = values.Select(value => ((string)value!).Trim()).Distinct(StringComparer.Ordinal).ToArray();
        await _gate.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            _ct.ThrowIfCancellationRequested();
            var workspace = await _store.ReadAsync(ResolveUid(), includeHistory: false).ConfigureAwait(false);
            var result = names.ToDictionary(name => name,
                name => workspace.Inventory.Items.GetValueOrDefault(name)?.FirstOrDefault()?.Quantity, StringComparer.Ordinal);
            // 物品名称是字典键，必须原样输出。
            return JsonConvert.SerializeObject(result);
        }
        finally { _gate.Release(); }
    }

    /// <summary>只有得到有效 UID 后才绑定会话，失败不能创建错误账号文件。</summary>
    private string ResolveUid()
    {
        var uid = _uid ?? _readUid();
        if (uid == null || !InventoryStore.IsValidUid(uid)) throw new InvalidOperationException("无法确定当前游戏 UID，库存操作未执行。");
        return _uid = uid!;
    }

    /// <summary>保留原始时间文本，由库存规则判断时区和业务时间。</summary>
    private static JToken Parse(string json) => JsonConvert.DeserializeObject<JToken>(json,
        new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })
        ?? throw new ArgumentException("库存参数不能为空。");

    /// <summary>拒绝未知字段和错误类型，避免拼错类型或漏填数量被静默忽略。</summary>
    private static (InventoryReport Report, bool Delta) ParseRecord(JToken token)
    {
        if (token is not JObject item) throw new ArgumentException("每条库存记录必须是对象。");
        var allowed = new[] { "name", "quantity", "type", "recordedAt", "category", "comment", "expirations" };
        if (item.Properties().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)))
            throw new ArgumentException("库存记录包含不支持的字段。");
        var type = item["type"];
        if (type != null && (type.Type != JTokenType.String || (string?)type is not ("snapshot" or "delta")))
            throw new ArgumentException("库存记录 type 仅支持 snapshot 或 delta。");
        if (item["name"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)item["name"]) ||
            item["quantity"]?.Type != JTokenType.Integer)
            throw new ArgumentException("库存记录必须包含非空 name 和整数 quantity。");
        // JS Number 超出安全整数范围会丢失精度，进入库存模型前拒绝。
        var quantity = item["quantity"]!.Value<decimal>();
        if (quantity < -9007199254740991m || quantity > 9007199254740991m)
            throw new ArgumentException("quantity 超出 JS 安全整数范围。");
        foreach (var field in new[] { "recordedAt", "category", "comment" })
            if (item[field] is { Type: not (JTokenType.String or JTokenType.Null) })
                throw new ArgumentException($"{field} 必须是字符串。");
        var delta = (string?)type == "delta";
        if (delta && (item.ContainsKey("category") || item.ContainsKey("comment")))
            throw new ArgumentException("增量记录不支持 category 和 comment。");
        if (item["expirations"] is { Type: not JTokenType.Null } expirations)
        {
            if (expirations is not JArray groups) throw new ArgumentException("expirations 必须是数组。");
            foreach (var group in groups)
                if (group is not JObject entry || entry.Properties().Any(p => p.Name is not ("quantity" or "expiresAt")) ||
                    entry["quantity"]?.Type != JTokenType.Integer || entry["expiresAt"]?.Type != JTokenType.String)
                    throw new ArgumentException("到期分组仅支持整数 quantity 和字符串 expiresAt。");
        }
        return (item.ToObject<InventoryReport>()!, delta);
    }
}
