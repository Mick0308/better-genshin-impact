using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.Service.AccountRecords;
using Microsoft.ClearScript.V8;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ScriptTests;

/// <summary>隔离存储与真实 V8 验证库存桥接，不启动主程序或游戏。</summary>
[Collection("ScriptExecution")]
public sealed class ScriptInventoryTests : IDisposable
{
    /// <summary>测试独占目录。</summary>
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BetterGI.ScriptInventory", Guid.NewGuid().ToString("N"));
    /// <summary>测试 UID。</summary>
    private const string Uid = "100000001";
    /// <summary>独立库存存储。</summary>
    private readonly InventoryStore _store;
    /// <summary>绑定的节点执行。</summary>
    private readonly ScriptInventoryContext _context = new("test/script", "测试脚本", Uid, "run", "/group/#1/js");
    /// <summary>库存宿主。</summary>
    private readonly ScriptInventory _host;
    /// <summary>避免业务时间处于未来。</summary>
    private readonly DateTimeOffset _time = DateTimeOffset.UtcNow.AddHours(-1);

    /// <summary>建立最小 CSV 和库存服务。</summary>
    public ScriptInventoryTests()
    {
        Directory.CreateDirectory(_root);
        var csv = Path.Combine(_root, "items.csv");
        File.WriteAllText(csv, "item_name,material_type\n摩拉,货币\n须臾树脂,消耗品\n");
        _store = new InventoryStore(new InventoryCatalog(csv), Path.Combine(_root, "records"));
        _host = new ScriptInventory(_store, _context, () => throw new Exception("已核验账号不应再次识别"), default);
    }

    /// <summary>仅删除测试自己的数据。</summary>
    public void Dispose() => Directory.Delete(_root, true);

    /// <summary>构造 JS 风格库存输入。</summary>
    private string Records(long quantity, string? type = null, int minute = 0)
    {
        var item = new JObject { ["name"] = "摩拉", ["quantity"] = quantity,
            ["recordedAt"] = _time.AddMinutes(minute).ToString("O") };
        if (type != null) item["type"] = type;
        return new JArray(item).ToString();
    }

    /// <summary>重复调用共用批次，增量不改变确认数，后续实测可以核实。</summary>
    [Fact]
    public async Task RepeatedCalls_ShareBatchAndKeepPendingDeltaOutOfQuery()
    {
        var first = JObject.Parse(await _host.Report(Records(10)));
        var second = JObject.Parse(await _host.Report(Records(5, "delta", 1)));
        Assert.Equal((string?)first["batchId"], (string?)second["batchId"]);
        Assert.Equal("1", (string?)first["records"]![0]!["recordId"]);
        Assert.Equal("2", (string?)second["records"]![0]!["recordId"]);
        Assert.Equal(10, (long)JObject.Parse(await _host.GetQuantities("\"摩拉\""))["摩拉"]!);
        await _host.Report(Records(15, minute: 2));
        var data = (await _store.ReadAsync(Uid)).Inventory;
        Assert.Equal(15, data.Items["摩拉"][0].Quantity);
        Assert.True(Assert.Single(data.Events.Where(e => e.Kind == "Delta")).VerifiedCorrect);
        Assert.Equal(3, (await _store.GetBatchAsync(Uid, _context.BatchId))!.OriginalCount);
    }

    /// <summary>同数组实测、增量、核实保持原顺序，而不是先汇总实测。</summary>
    [Fact]
    public async Task MixedArray_PreservesOrder()
    {
        var input = new JArray(JArray.Parse(Records(10))[0], JArray.Parse(Records(5, "delta", 1))[0],
            JArray.Parse(Records(15, minute: 2))[0]);
        await _host.Report(input.ToString());
        var data = (await _store.ReadAsync(Uid)).Inventory;
        Assert.True(Assert.Single(data.Events.Where(e => e.Kind == "Delta")).VerifiedCorrect);
    }

    /// <summary>结构错误预检整次调用；业务错误则返回逐条结果。</summary>
    [Fact]
    public async Task InvalidStructure_DoesNotPartiallyWrite_BusinessFailureIsRecorded()
    {
        var input = JArray.Parse(Records(10));
        input.Add(new JObject { ["name"] = "摩拉", ["quantity"] = 1, ["type"] = "dleta" });
        await Assert.ThrowsAsync<ArgumentException>(() => _host.Report(input.ToString()));
        Assert.False(await _store.HasUidDataAsync(Uid));
        input = JArray.Parse(Records(10));
        input.Add(JArray.Parse(Records(-1, minute: 1))[0]);
        var result = JObject.Parse(await _host.Report(input.ToString()));
        Assert.Equal(new[] { "Accepted", "Rejected" }, result["records"]!.Select(r => (string?)r["outcome"]));
    }

    /// <summary>数量零和未知不同，到期信息复用现有模型且允许部分未知。</summary>
    [Fact]
    public async Task QueryAndExpirations_KeepExistingSemantics()
    {
        await _host.Report(Records(0));
        var record = new JObject { ["name"] = "须臾树脂", ["quantity"] = 2,
            ["recordedAt"] = _time.ToString("O"), ["expirations"] = new JArray(new JObject
            { ["quantity"] = 1, ["expiresAt"] = DateTimeOffset.UtcNow.AddDays(2).ToString("O") }) };
        await _host.Report(new JArray(record).ToString());
        var quantities = JObject.Parse(await _host.GetQuantities("[\"摩拉\",\"须臾树脂\",\"未知物品\"]"));
        Assert.Equal(0, (long)quantities["摩拉"]!);
        Assert.Equal(2, (long)quantities["须臾树脂"]!);
        Assert.Equal(JTokenType.Null, quantities["未知物品"]!.Type);
        Assert.Single((await _store.ReadAsync(Uid)).Inventory.Items["须臾树脂"][0].Expirations!);
    }

    /// <summary>并发提交不复用记录编号。</summary>
    [Fact]
    public async Task ConcurrentCalls_AllocateDistinctRecordIds()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => _host.Report(Records(i, minute: i))));
        Assert.Equal(5, results.Select(json => (string?)JObject.Parse(json)["records"]![0]!["recordId"]).Distinct().Count());
    }

    /// <summary>轮次、节点和失败重试区分批次，直接运行也独立分配。</summary>
    [Fact]
    public void ExecutionIdentity_DistinguishesNodesIterationsAndRetries()
    {
        Assert.Equal(_context.BatchId, new ScriptInventoryContext("test/script", "测试", Uid, "run", "/group/#1/js").BatchId);
        Assert.NotEqual(_context.BatchId, new ScriptInventoryContext("test/script", "测试", Uid, "run", "/group/#2/js").BatchId);
        Assert.NotEqual(_context.BatchId, new ScriptInventoryContext("test/script", "测试", Uid, "run", "/group/#1/other").BatchId);
        Assert.NotEqual(_context.BatchId, new ScriptInventoryContext("test/script", "测试", Uid, "run", "/group/#1/js", 2).BatchId);
        Assert.NotEqual(new ScriptInventoryContext("x", "x").BatchId, new ScriptInventoryContext("x", "x").BatchId);
    }

    /// <summary>无账号不写入，取消后不接收新调用。</summary>
    [Fact]
    public async Task MissingUidAndCancellation_DoNotWrite()
    {
        var unknown = new ScriptInventory(_store, new ScriptInventoryContext("x", "x"), () => null, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unknown.Report(Records(1)));
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        var stopped = new ScriptInventory(_store, _context, () => Uid, ct.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.Report(Records(1)));
        Assert.False(await _store.HasUidDataAsync(Uid));
    }

    /// <summary>前后扫描分批，回到同名分组继续追加；默认批次与所有命名分组独立。</summary>
    [Fact]
    public async Task NamedGroups_SeparateBatchesAndAllowInterleavedAppend()
    {
        var before = JObject.Parse(await _host.Report(Records(10), "{\"batch\":\"before\"}"));
        var after = JObject.Parse(await _host.Report(Records(20, minute: 1), "{\"batch\":\"after\"}"));
        var appended = JObject.Parse(await _host.Report(Records(5, "delta", 2), "{\"batch\":\"before\"}"));
        var normal = JObject.Parse(await _host.Report(Records(25, minute: 3)));
        Assert.Equal((string?)before["batchId"], (string?)appended["batchId"]);
        Assert.NotEqual((string?)before["batchId"], (string?)after["batchId"]);
        Assert.Equal(_context.BatchId, (string?)normal["batchId"]);
        Assert.NotEqual((string?)normal["batchId"], (string?)before["batchId"]);
        Assert.NotEqual((string?)normal["batchId"], (string?)after["batchId"]);
        Assert.Equal(2, (await _store.GetBatchAsync(Uid, (string)before["batchId"]!))!.OriginalCount);
        Assert.Equal(1, (await _store.GetBatchAsync(Uid, (string)after["batchId"]!))!.OriginalCount);
        Assert.NotEqual((string?)before["records"]![0]!["recordId"], (string?)appended["records"]![0]!["recordId"]);
        Assert.Equal(_context.GetBatchId("before"), new ScriptInventoryContext("test/script", "测试", Uid,
            "run", "/group/#1/js").GetBatchId("before"));
        Assert.NotEqual(_context.GetBatchId("before"), new ScriptInventoryContext("test/script", "测试", Uid,
            "run", "/group/#2/js").GetBatchId("before"));
        Assert.NotEqual(_context.GetBatchId("before"), new ScriptInventoryContext("test/script", "测试", Uid,
            "run", "/group/#1/js", 2).GetBatchId("before"));
    }

    /// <summary>错误分组参数不能悄悄写入默认批次。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"batch\":null}")]
    [InlineData("{\"batch\":1}")]
    [InlineData("{\"batch\":\"\"}")]
    [InlineData("{\"batch\":\"   \"}")]
    [InlineData("{\"batc\":\"before\"}")]
    public async Task InvalidGroupOptions_DoNotWrite(string options)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.Report(Records(10), options));
        Assert.False(await _store.HasUidDataAsync(Uid));
    }

    /// <summary>真实 JS 数组和 await 桥接返回原生对象及数组，且不要求 JSON 字符串参数。</summary>
    [Fact]
    public async Task NativeJavaScriptArrays_WorkThroughBothPublicEntrypoints()
    {
        string? result = null;
        await ScriptExecution.ExecuteAsync(ct =>
        {
            var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding | V8ScriptEngineFlags.EnableTaskPromiseConversion);
            ScriptInventory.Register(engine, new ScriptInventory(_store, _context, () => Uid, ct));
            engine.AddHostObject("capture", new Action<string>(value => result = value));
            return engine;
        }, engine => engine.Evaluate("""
            (async () => {
                const report = await inventory.report([{ name: '摩拉', quantity: 0, recordedAt: new Date(Date.now() - 60000).toISOString() }]);
                if (!Array.isArray(report.records) || report.records[0].outcome !== 'Accepted') throw new Error('invalid result');
                const one = await inventory.getQuantities('摩拉');
                const many = await inventory.getQuantities(['摩拉', '未知']);
                const first = await inventory.report([{ name: '摩拉', quantity: 1, recordedAt: new Date(Date.now() - 40000).toISOString() }], { batch: '扫描前' });
                const second = await inventory.report([{ name: '摩拉', quantity: 2, recordedAt: new Date(Date.now() - 30000).toISOString() }], { batch: '扫描后' });
                const third = await inventory.report([{ name: '摩拉', quantity: 3, recordedAt: new Date(Date.now() - 20000).toISOString() }], { batch: '扫描前' });
                if (first.batchId === second.batchId || first.batchId !== third.batchId || report.batchId === first.batchId)
                    throw new Error('invalid group identity');
                let rejected = false;
                try { await inventory.report([], 'before'); } catch { rejected = true; }
                if (!rejected) throw new Error('invalid options accepted');
                capture(JSON.stringify({ one, many, keys: Object.keys(inventory) }));
            })()
            """), default);
        var data = JObject.Parse(result!);
        Assert.Equal(0, (long)data["one"]!["摩拉"]!);
        Assert.Equal(JTokenType.Null, data["many"]!["未知"]!.Type);
        Assert.Equal(new[] { "report", "getQuantities" }, data["keys"]!.Values<string>());
    }
}
