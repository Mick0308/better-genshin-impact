using BetterGenshinImpact.Service.AccountRecords;
using BetterGenshinImpact.Service.AccountRecords.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

/// <summary>批次身份、核实引用及跨文件恢复验证；所有数据使用隔离临时目录。</summary>
public sealed class InventoryBatchTests : IDisposable
{
    /// <summary>独立测试目录。</summary>
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BetterGI.BatchTests", Guid.NewGuid().ToString("N"));
    /// <summary>测试账号。</summary>
    private const string Uid = "100000001";
    /// <summary>实际业务时间早于当前时间。</summary>
    private readonly DateTimeOffset _time = DateTimeOffset.UtcNow.AddHours(-1);
    /// <summary>不依赖资源包的物品目录。</summary>
    private readonly InventoryCatalog _catalog;
    /// <summary>测试存储。</summary>
    private readonly InventoryStore _store;
    /// <summary>统一测试来源。</summary>
    private readonly AccountRecordSource _source = new() { Reference = "test", DisplayName = "测试" };
    /// <summary>创建隔离字典和存储。</summary>
    public InventoryBatchTests()
    {
        Directory.CreateDirectory(_root);
        var csv = Path.Combine(_root, "items.csv");
        File.WriteAllText(csv, "item_name,material_type\n摩拉,货币\n经验书,材料\n");
        _catalog = new(csv);
        _store = new(_catalog, Path.Combine(_root, "records"));
    }
    /// <summary>只删除本测试创建的目录。</summary>
    public void Dispose() => Directory.Delete(_root, true);
    /// <summary>构造有明确身份的总量输入。</summary>
    private InventoryReport Report(string name, long quantity, int minute = 0, string id = "1") => new()
    { Name = name, Quantity = quantity, RecordId = id, RecordedAt = _time.AddMinutes(minute).ToString("O") };
    /// <summary>主状态路径。</summary>
    private string InventoryPath => Path.Combine(_root, "records", Uid, "inventory.json");

    /// <summary>同批逐条接受或拒绝，重传不产生新的状态事件，身份冲突整批拒绝。</summary>
    [Fact]
    public async Task MixedBatch_TracksEachRecordAndRejectsIdentityCollision()
    {
        var reports = new[] { Report("摩拉", 10), Report("经验书", -1, id: "2") };
        var result = await _store.ReportAsync(Uid, _source, reports, batchId: "a");
        Assert.Equal(new[] { "Accepted", "Rejected" }, result.Records.Select(item => item.Outcome));
        var data = (await _store.ReadAsync(Uid)).Inventory;
        Assert.Equal(new InventoryRecordReference { BatchId = "a", RecordId = "1" }, data.Items["摩拉"][0].RecordReference);
        Assert.Equal("2", data.Errors["经验书"].RecordReference!.RecordId);
        Assert.Equal(2, (await _store.GetBatchAsync(Uid, "a"))!.OriginalCount);
        Assert.Empty(data.PendingBatches);
        var repeated = await _store.ReportAsync(Uid, _source, reports, batchId: "a");
        Assert.Equal(2, repeated.DuplicateCount);
        Assert.Equal(data.Events.Count, (await _store.ReadAsync(Uid)).Inventory.Events.Count);
        reports[0].Quantity = 99;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.ReportAsync(Uid, _source, reports, batchId: "a"));
        Assert.Equal(10, (await _store.ReadAsync(Uid)).Inventory.Items["摩拉"][0].Quantity);
    }

    /// <summary>增量依旧待核实，后续总量通过完整引用精确关联，不把接受当作核实。</summary>
    [Fact]
    public async Task Delta_VerificationKeepsIdentityAndExistingStateRules()
    {
        await _store.ReportAsync(Uid, _source, [Report("摩拉", 10)], batchId: "baseline");
        var delta = new InventoryDeltaReport { RecordId = "change", Name = "摩拉", Delta = 5, RecordedAt = _time.AddMinutes(1).ToString("O") };
        await _store.ReportDeltaAsync(Uid, _source, [delta], "delta");
        var before = (await _store.ReadAsync(Uid)).Inventory;
        var pending = Assert.Single(before.Events.Where(item => item.Kind == "Delta"));
        Assert.Equal("Pending", pending.State);
        Assert.Equal(10, before.Items["摩拉"][0].Quantity);
        Assert.Equal(1, (await _store.ReportDeltaAsync(Uid, _source, [delta], "delta")).DuplicateCount);
        await _store.ReportAsync(Uid, _source, [Report("摩拉", 15, 2)], batchId: "verify");
        var verified = Assert.Single((await _store.ReadAsync(Uid)).Inventory.Events.Where(item => item.Kind == "Delta"));
        Assert.Equal(pending.Id, verified.Id);
        Assert.Equal(pending.RecordReference, verified.RecordReference);
        Assert.Equal("Completed", verified.State);
        Assert.True(verified.VerifiedCorrect);
        Assert.Equal(new InventoryRecordReference { BatchId = "verify", RecordId = "1" }, verified.VerificationReference);
    }

    /// <summary>失败增量相同内容重传可在补充字典后成功，成功后不再次累计。</summary>
    [Fact]
    public async Task FailedDelta_CanBeRevalidatedWithSameIdentity()
    {
        var delta = new InventoryDeltaReport { Name = "新材料", Delta = 2, RecordedAt = _time.ToString("O") };
        Assert.Single((await _store.ReportDeltaAsync(Uid, _source, [delta], "retry")).Rejected);
        await _store.DefineAsync("新材料", "材料");
        Assert.Single((await _store.ReportDeltaAsync(Uid, _source, [delta], "retry")).Accepted);
        Assert.Equal(1, (await _store.ReportDeltaAsync(Uid, _source, [delta], "retry")).DuplicateCount);
        Assert.Single((await _store.ReadAsync(Uid)).Inventory.Events.Where(item => item.State == "Pending"));
    }

    /// <summary>人工确认是独立批次，原增量终止且记录替代依据；重试不更新时间。</summary>
    [Fact]
    public async Task Manual_IsOneBatchAndRetryDoesNotReapply()
    {
        await _store.ReportDeltaAsync(Uid, _source, [new() { Name = "摩拉", Delta = 2, RecordedAt = _time.ToString("O") }], "delta");
        await _store.SetLocalQuantityAsync(Uid, "摩拉", 7, batchId: "manual");
        var data = (await _store.ReadAsync(Uid)).Inventory;
        var record = data.Items["摩拉"][0];
        Assert.Equal("manual", record.RecordReference!.BatchId);
        var aborted = Assert.Single(data.Events.Where(item => item.Kind == "Delta"));
        Assert.Equal("Aborted", aborted.State);
        Assert.Equal(record.RecordReference, aborted.VerificationReference);
        await _store.SetLocalQuantityAsync(Uid, "摩拉", 7, batchId: "manual");
        Assert.Equal(record.RecordedAt, (await _store.ReadAsync(Uid)).Inventory.Items["摩拉"][0].RecordedAt);
        var manual = Assert.Single((await _store.GetBatchAsync(Uid, "manual"))!.Records);
        Assert.Equal(record.RecordedAt, manual.ConfirmedAt);
        Assert.Equal("Accepted", manual.Outcome);
    }

    /// <summary>批次文件写失败后状态和恢复依据已提交，新实例可补齐且不会重复应用。</summary>
    [Fact]
    public async Task PublicationFailure_RecoversFromCommittedState()
    {
        var directory = Path.Combine(_root, "records", Uid);
        Directory.CreateDirectory(directory);
        var blocked = Path.Combine(directory, "batches");
        File.WriteAllText(blocked, "block directory creation");
        await Assert.ThrowsAnyAsync<IOException>(() => _store.ReportAsync(Uid, _source, [Report("摩拉", 10)], batchId: "recover"));
        var saved = JsonConvert.DeserializeObject<InventoryDocument>(File.ReadAllText(InventoryPath))!;
        Assert.Equal(10, saved.Items["摩拉"][0].Quantity);
        Assert.NotEmpty(saved.PendingBatches);
        File.Delete(blocked);
        var reopened = new InventoryStore(_catalog, Path.Combine(_root, "records"));
        Assert.NotNull(await reopened.GetBatchAsync(Uid, "recover"));
        Assert.Empty(JsonConvert.DeserializeObject<InventoryDocument>(File.ReadAllText(InventoryPath))!.PendingBatches);
        Assert.Equal(1, (await reopened.ReportAsync(Uid, _source, [Report("摩拉", 10)], batchId: "recover")).DuplicateCount);
    }

    /// <summary>索引丢失可重建，按物品查询只返回该物品，并支持分页。</summary>
    [Fact]
    public async Task ItemIndex_RebuildsFromBatches()
    {
        await _store.ReportAsync(Uid, _source, [Report("摩拉", 10), Report("经验书", 2, id: "2")], batchId: "index");
        File.Delete(Path.Combine(_root, "records", Uid, "batch-index.json"));
        var entry = Assert.Single(await _store.GetItemHistorySnapshotAsync(Uid, "经验书"));
        Assert.Equal("2", entry.Reference.RecordId);
        var page = await _store.GetItemHistoryPageAsync(Uid,
            [entry.Reference, new() { BatchId = "index", RecordId = "missing" }, new() { BatchId = "gone", RecordId = "1" },
                new() { BatchId = "index", RecordId = "1" }]);
        Assert.Equal("经验书", page[0]!.Name);
        Assert.Null(page[1]);
        Assert.Null(page[2]);
        Assert.Equal("摩拉", page[3]!.Name);
        Assert.Empty(await _store.GetItemHistoryPageAsync(Uid, []));
    }

    /// <summary>不同来源的导入记录仍属于同一批次，旧交换格式无需提供 ID。</summary>
    [Fact]
    public async Task Import_UsesOneBatchAcrossSources()
    {
        await _store.ReportAsync(Uid, _source, [Report("摩拉", 10)]);
        await _store.ReportAsync(Uid, new() { Reference = "other", DisplayName = "other" }, [Report("经验书", 2)]);
        var package = await _store.ExportInventoryAsync(Uid);
        package["batchId"] = "import";
        var result = await _store.ImportInventoryAsync(package, "100000002", false);
        Assert.Equal("import", result.BatchId);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal(2, (await _store.GetBatchAsync("100000002", "import"))!.Records.Count);
    }

    /// <summary>开发期只接受当前版本，旧格式明确拒绝且不改写文件。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OldSchema_IsRejectedWithoutMigration(int schemaVersion)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InventoryPath)!);
        var text = JsonConvert.SerializeObject(new InventoryDocument { SchemaVersion = schemaVersion, Uid = Uid });
        File.WriteAllText(InventoryPath, text);
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.ReadAsync(Uid));
        Assert.Equal(text, File.ReadAllText(InventoryPath));
    }

    /// <summary>错误保留修订数量和关联引用，完整输入只在批次中保存。</summary>
    [Fact]
    public async Task Error_ReferencesBatchWithoutDuplicatingInput()
    {
        var input = Report("摩拉", -1);
        await _store.ReportAsync(Uid, _source, [input], batchId: "invalid");
        var data = (await _store.ReadAsync(Uid)).Inventory;
        var error = data.Errors["摩拉"];
        Assert.Equal(-1, error.IncomingQuantity);
        Assert.Equal("invalid", error.RecordReference!.BatchId);
        var document = JObject.Parse(File.ReadAllText(InventoryPath));
        Assert.Null(document["Errors"]!["摩拉"]!["Incoming"]);
        Assert.DoesNotContain(Assert.Single(data.Events).History, line => line.Contains("Incoming="));
        var batch = (await _store.GetBatchAsync(Uid, "invalid"))!;
        Assert.Equal(-1, (long)Assert.Single(batch.Records).Input["Quantity"]!);
    }

    /// <summary>归档目录写入失败时，已提交的库存与待发布事件仍能在新实例中恢复。</summary>
    [Fact]
    public async Task ArchivePublicationFailure_DoesNotLoseAcceptedEvent()
    {
        var directory = Path.Combine(_root, "records", Uid);
        Directory.CreateDirectory(directory);
        var blocked = Path.Combine(directory, "events");
        File.WriteAllText(blocked, "block directory creation");
        await Assert.ThrowsAnyAsync<IOException>(() => _store.ReportAsync(Uid, _source, [Report("摩拉", 10)], batchId: "archive"));
        var saved = JsonConvert.DeserializeObject<InventoryDocument>(File.ReadAllText(InventoryPath))!;
        Assert.NotEmpty(saved.PendingEventArchives);
        File.Delete(blocked);
        var reopened = new InventoryStore(_catalog, Path.Combine(_root, "records"));
        Assert.NotNull(await reopened.GetBatchAsync(Uid, "archive"));
        var data = (await reopened.ReadAsync(Uid)).Inventory;
        Assert.Equal("archive", Assert.Single(data.Events).RecordReference!.BatchId);
        Assert.Empty(data.PendingEventArchives);
    }

    /// <summary>开发期保留全部批次记录，重复提交不新增证据，修改内容仍拒绝。</summary>
    [Fact]
    public async Task History_KeepsAllRecordsAndDeduplicatesRetries()
    {
        var inputs = Enumerable.Range(1, 1002).Select(index => Report("摩拉", 10, id: index.ToString())).ToArray();
        await _store.ReportAsync(Uid, _source, inputs, batchId: "large");
        var batch = (await _store.GetBatchAsync(Uid, "large"))!;
        Assert.Equal(1002, batch.OriginalCount);
        Assert.Equal(0, batch.RemovedCount);
        Assert.Equal(1002, batch.Records.Count);
        Assert.Contains(batch.Records, item => item.RecordId == "1");
        await _store.ReportAsync(Uid, _source, inputs, batchId: "large");
        batch = (await _store.GetBatchAsync(Uid, "large"))!;
        Assert.Equal(1002, batch.Records.Count);
        Assert.Equal(0, batch.RemovedCount);
        inputs[1].Quantity = 12;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.ReportAsync(Uid, _source, inputs, batchId: "large"));
    }

    /// <summary>批次证据保留来源原始时间文本，不因 JSON 自动解析改变时区表示。</summary>
    [Fact]
    public async Task History_PreservesRawTimestampText()
    {
        var report = Report("摩拉", 10);
        report.RecordedAt = _time.ToOffset(TimeSpan.FromHours(8)).ToString("O");
        await _store.ReportAsync(Uid, _source, [report], batchId: "timestamp");
        var batch = (await _store.GetBatchAsync(Uid, "timestamp"))!;
        Assert.Equal(JTokenType.String, batch.Records[0].Input["RecordedAt"]!.Type);
        Assert.Equal(report.RecordedAt, (string?)batch.Records[0].Input["RecordedAt"]);
    }

    /// <summary>明确删除物品重置接收状态，其他物品仍去重，身份内容冲突仍拒绝。</summary>
    [Fact]
    public async Task DeleteItem_ResetsOnlyItsAcceptance()
    {
        var inputs = new[] { Report("摩拉", 10), Report("经验书", 2, id: "2") };
        await _store.ReportAsync(Uid, _source, inputs, batchId: "delete");
        await _store.DeleteItemAsync(Uid, "摩拉");
        var repeated = await _store.ReportAsync(Uid, _source, inputs, batchId: "delete");
        Assert.Equal("摩拉", Assert.Single(repeated.Accepted));
        Assert.Equal(1, repeated.DuplicateCount);
        inputs[0].Quantity = 20;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.ReportAsync(Uid, _source, inputs, batchId: "delete"));
    }
}
