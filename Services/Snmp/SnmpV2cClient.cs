using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RuijieNetworkAssistant.Services.Snmp;

/// <summary>一条 SNMP 取值结果。</summary>
/// <param name="Bytes">
/// OCTET STRING 的**原始字节**。必须保留：某些 MAC 地址字节组合恰好是合法 UTF-8，
/// 只按文本解码会得到 "Xil3…" 这种再也还原不回去的乱码（现场 P0-4）。
/// </param>
public sealed record SnmpValue(string Oid, string Type, string? Text, long? Number, byte[]? Bytes = null)
{
    /// <summary>
    /// 设备用哨兵值/占位符明确表示"我没有这个数"：
    ///   - **数值 -255**：设备可能使用的"无读数"哨兵（不能假设所有设备含义相同）；
    ///   - 字符串 `"-"` / `"N/A"` / 空串、空 Hex-STRING：占位符。
    /// 这些**绝不能当数据**（尤其 -255 不能被当成 0 或负值参与计算）。
    /// </summary>
    // 注意：这里**不看 Bytes**。设备在空槽位里也可能返回字符串 `N/A`，
    // 只认"没有字节"的话这种占位就会原样显示成 N/A，跟 -255/`-` 的显示口径不一致。
    public bool IsNoReading =>
        !IsMissingInstance
        && (Number == NoReadingSentinel
            || (Number is null && IsPlaceholderText(Text)));

    /// <summary>
    /// 设备回了 noSuchObject(0x80) / noSuchInstance(0x81) / endOfMibView(0x82)：
    /// 这是"**这个 OID 在设备上不存在**"，不是"这个位置没有读数"——
    /// 前者按老约定显示 `N/A`，后者才是"未提供"。
    /// </summary>
    public bool IsMissingInstance => Type is "0x80" or "0x81" or "0x82";

    /// <summary>设备表示"无读数"的哨兵值（实测：-255）。</summary>
    public const long NoReadingSentinel = -255;

    /// <summary>占位字符串判定（`-`、`N/A`、`N\A`、空串）。</summary>
    public static bool IsPlaceholderText(string? text)
    {
        var value = text?.Trim();
        return string.IsNullOrEmpty(value)
               || value == "-"
               || value == "--"
               || value.Equals("N/A", StringComparison.OrdinalIgnoreCase)
               || value.Equals("NA", StringComparison.OrdinalIgnoreCase)
               || value.Equals("N\\A", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 界面显示文本：字符串直接显示，数值转文本；
    /// **哨兵值 / 占位符显示成"未提供"**（并且不再参与任何计算）。
    /// </summary>
    public string Display => IsMissingInstance
        ? "N/A"
        : IsNoReading
            ? "未提供"
            : Text ?? (Number?.ToString() ?? "N/A");
}

/// <summary>
/// 一次 GET 的结果：既要数值，也要"设备到底有没有回"。
/// 只返回字典时，超时 / Community 错误 与"设备没有这个 OID"无法区分，界面上全都是 N/A。
/// </summary>
public sealed record SnmpGetResponse(
    IReadOnlyDictionary<string, SnmpValue> Values,
    bool Responded,
    string? Error)
{
    public static SnmpGetResponse Empty { get; } =
        new(new Dictionary<string, SnmpValue>(StringComparer.Ordinal), false, null);

    public static SnmpGetResponse Ok(IReadOnlyDictionary<string, SnmpValue> values) =>
        new(values, true, null);
}

/// <summary>一次 WALK（GETNEXT 连续取值）的结果。</summary>
public sealed record SnmpWalkResponse(
    IReadOnlyList<SnmpValue> Rows,
    bool Responded,
    bool Truncated,
    string? Error,
    // 本次走到哪了（最后一个取到的 OID）。截断时调用方把它存下来，
    // 下次用 startAfterOid 从**断点继续**（增量续拉），不用把整张表重走一遍。
    string? LastOid = null);

/// <summary>
/// 最小 SNMP v2c 客户端（GET / GETNEXT-WALK），自己实现 BER 编解码，不引入第三方库。
/// 只做"读取设备信息"，绝不用于修改配置（符合项目"SNMP 只读"的定位）。
/// 与 CLI 连接完全无关：只依赖 IP + Community + UDP 端口。
/// </summary>
public sealed class SnmpV2cClient
{
    private const byte SequenceTag = 0x30;
    private const byte IntegerTag = 0x02;
    private const byte OctetStringTag = 0x04;
    private const byte NullTag = 0x05;
    private const byte OidTag = 0x06;
    private const byte IpAddressTag = 0x40;
    private const byte Counter32Tag = 0x41;
    private const byte Gauge32Tag = 0x42;
    private const byte TimeTicksTag = 0x43;
    private const byte Counter64Tag = 0x46;
    private const byte GetRequestTag = 0xA0;
    private const byte GetNextRequestTag = 0xA1;
    private const byte GetResponseTag = 0xA2;
    private const byte NoSuchObjectTag = 0x80;
    private const byte NoSuchInstanceTag = 0x81;
    private const byte EndOfMibViewTag = 0x82;

    /// <summary>
    /// 请求号计数器。**必须走 <see cref="Interlocked"/> 自增**（2026-09-22 改）：
    /// 批量巡检/定位会 4~8 路并发查询不同设备，而每个 WALK 各建一个 UdpSession、共用这一个静态计数器 ——
    /// `++` 在多线程下会重号。虽然回包是按各自的 socket 收的（不会互相认领），
    /// 但只要设备或隧道按 request-id 关联状态就会出问题，原子自增没有代价。
    /// </summary>
    private int _requestId = Environment.TickCount & 0x7FFF;

    /// <summary>
    /// 一次查询（一个 GET 批次 / 一次 WALK）共用的 UDP 会话。
    ///
    /// **为什么要复用**：过 UU + SnmpTunnel 隧道时，"新的本地 UDP 源端口"在隧道里就是一个**新会话**——
    /// 服务端要为它新建一个 UDP socket、重建到交换机的映射。旧实现每个请求都 `new UdpClient()`，
    /// 于是成百上千个请求就是成百上千个会话：实测每次请求 1.8~3.1 秒、还经常整包超时，
    /// 隧道客户端日志里刷满 `本地 UDP 接收出错：ConnectionReset`。复用一条会话即可根治这类开销。
    ///
    /// 复用时必须**按 request-id 认领回包**：上一次请求迟到的响应可能后到，
    /// 直接取第一个到达的报文会张冠李戴（这就是旧实现"每请求一个 socket"能侥幸正确的原因）。
    /// </summary>
    private sealed class UdpSession : IDisposable
    {
        private readonly UdpClient _udp;

        public UdpSession(string host, int port)
        {
            _udp = new UdpClient();
            _udp.Connect(host, port);
        }

        /// <summary>发一条请求并等回包；request-id 不匹配的（迟到的旧回包）直接丢掉继续等。</summary>
        public async Task<byte[]> RequestAsync(
            byte[] request,
            int requestId,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            await _udp.SendAsync(request, request.Length).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Math.Max(300, timeoutMs));
            while (true)
            {
                var response = await _udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                if (!TryReadRequestId(response.Buffer, out var id) || id == requestId)
                {
                    // 解析不出 request-id 的（少数代理不回填）也放行，保持与旧行为兼容
                    return response.Buffer;
                }
            }
        }

        public void Dispose()
        {
            try
            {
                _udp.Dispose();
            }
            catch
            {
                // 关闭失败不影响结果
            }
        }
    }

    /// <summary>从响应报文里取出 request-id（取不到返回 false）。</summary>
    private static bool TryReadRequestId(byte[] buffer, out long requestId)
    {
        requestId = 0;
        var offset = 0;
        if (!TryReadTlv(buffer, ref offset, out var rootTag, out var rootBody) || rootTag != SequenceTag)
        {
            return false;
        }

        var inner = 0;
        if (!TryReadTlv(rootBody, ref inner, out _, out _))          // version
        {
            return false;
        }

        if (!TryReadTlv(rootBody, ref inner, out _, out _))          // community
        {
            return false;
        }

        if (!TryReadTlv(rootBody, ref inner, out _, out var pduBody))
        {
            return false;
        }

        var pduOffset = 0;
        if (!TryReadTlv(pduBody, ref pduOffset, out _, out var idBody))
        {
            return false;
        }

        requestId = DecodeInteger(idBody);
        return true;
    }

    /// <summary>
    /// 逐项 GET。**单个 OID 失败不影响其它 OID**（返回结果里缺这一项，由界面显示 N/A）。
    /// Community 不写日志。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SnmpValue>> GetAsync(
        string host,
        int port,
        string community,
        IEnumerable<string> oids,
        int timeoutMs = 1500,
        int retries = 1,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, SnmpValue>(StringComparer.Ordinal);
        var targets = oids.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();
        if (targets.Count == 0 || string.IsNullOrWhiteSpace(host))
        {
            return result;
        }

        // 一次请求最多带 12 个 OID，避免 UDP 报文过大被设备丢弃
        // 整段共用一个 UDP 会话（隧道里"一个新源端口 = 一个新会话"，见 UdpSession 注释）
        using var session = new UdpSession(host, port);
        foreach (var batch in targets.Chunk(12))
        {
            var values = await TryGetBatchAsync(session, community, batch, timeoutMs, retries, cancellationToken)
                .ConfigureAwait(false);
            foreach (var pair in values)
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }

    /// <summary>
    /// 逐项 GET，同时告诉调用方"设备有没有回"。
    /// 用于区分：Community 错误 / 超时（Responded=false）与"设备确实没有这些 OID"（Responded=true 但值缺失）。
    /// </summary>
    public async Task<SnmpGetResponse> GetWithStatusAsync(
        string host,
        int port,
        string community,
        IEnumerable<string> oids,
        int timeoutMs = 1500,
        int retries = 1,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, SnmpValue>(StringComparer.Ordinal);
        var targets = oids.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();
        if (targets.Count == 0 || string.IsNullOrWhiteSpace(host))
        {
            return SnmpGetResponse.Empty;
        }

        var responded = false;
        string? error = null;

        // 一次请求最多带 12 个 OID，避免 UDP 报文过大被设备丢弃
        using var session = new UdpSession(host, port);
        foreach (var batch in targets.Chunk(12))
        {
            var response = await TryGetBatchWithStatusAsync(session, community, batch, timeoutMs, retries, cancellationToken)
                .ConfigureAwait(false);
            responded |= response.Responded;
            error ??= response.Error;
            foreach (var pair in response.Values)
            {
                result[pair.Key] = pair.Value;
            }
        }

        return new SnmpGetResponse(result, responded, responded ? null : error);
    }

    /// <summary>
    /// 从 <paramref name="rootOid"/> 开始连续 GETNEXT，直到走出该子树（标准 SNMP Walk）。
    /// 表类信息（接口 / VLAN / MAC / 温度 / 风扇 / 电源 / 资源）都靠它取；
    /// 单次超时即停止本次 WALK，由调用方决定显示 N/A 还是重试。
    /// </summary>
    public async Task<SnmpWalkResponse> WalkAsync(
        string host,
        int port,
        string community,
        string rootOid,
        int timeoutMs = 1500,
        int retries = 1,
        int maxRows = 2000,
        CancellationToken cancellationToken = default,
        int timeBudgetMs = 0,
        IProgress<int>? progress = null,
        string? startAfterOid = null)
    {
        var rows = new List<SnmpValue>();
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(rootOid))
        {
            return new SnmpWalkResponse(rows, false, false, null);
        }

        var prefix = rootOid.Trim('.');
        // 增量续拉：从上次的断点接着走（断点必须是本子树里的 OID，否则当没给）
        var resumeFrom = !string.IsNullOrWhiteSpace(startAfterOid)
                         && startAfterOid.Trim('.').StartsWith(prefix + ".", StringComparison.Ordinal)
            ? startAfterOid.Trim('.')
            : null;
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        bool BudgetExceeded() =>
            timeBudgetMs > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds > timeBudgetMs;

        // 断点游标：最后一个取到的 OID（截断时交给调用方，下次从这里续拉）
        static string? LastOidOf(List<SnmpValue> list) => list.Count > 0 ? list[^1].Oid : null;

        // 整次 WALK 共用一个 UDP 会话（隧道里"一个新源端口 = 一个新会话"，见 UdpSession 注释）
        using var session = new UdpSession(host, port);

        // ---------- 优先 GETBULK（一次 N 个节点）：采整棵树时把请求数从几千降到几十 ----------
        // 老代理不认 0xA5 的表现是"完全不回应"→ TryBulkStep 返回 null，这里自动退回 GETNEXT。
        var bulkKey = $"{host}:{port}";
        var bulkCursor = resumeFrom ?? prefix;
        var bulkResponded = false;
        var recoveries = 0;
        var requestsSincePace = 0;
        while (BulkEnabled && !BulkUnsupported.ContainsKey(bulkKey) && rows.Count < maxRows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (BudgetExceeded())
            {
                // 大型设备的 MAC 表可能包含大量记录，整表需要较多请求，
                // 到时间预算就带着已取到的行收工，由界面标注"已截断"。见 MaxThrottleRecoveries 注释。
                return new SnmpWalkResponse(
                    rows,
                    rows.Count > 0,
                    true,
                    $"达到时间预算（{timeBudgetMs / 1000} 秒），已取 {rows.Count} 条", LastOidOf(rows));
            }

            var step = await TryBulkStepAsync(
                    session,
                    community,
                    bulkCursor,
                    DefaultMaxRepetitions,
                    timeoutMs,
                    cancellationToken)
                .ConfigureAwait(false);

            if (step is null)
            {
                if (rows.Count == 0)
                {
                    // 一次都没成功：老代理不认 GETBULK 的典型表现（干脆不回）→ 记下来走 GETNEXT。
                    BulkUnsupported[bulkKey] = true;
                    bulkResponded = false;
                    break;
                }

                // 中途失败 = 设备限速，不是"不支持 GETBULK"。
                // 设备验证发现：连续大量 GETBULK 后可能临时不回应，
                // 停几秒再单发立刻又通。旧实现这里直接整表退回 GETNEXT 重走（一次请求一个节点），
                // 上万行的表等于永远跑不完 —— 用户看到的就是"查询卡住 / 超时"。
                // 现在改成：停一下，从**断点**（bulkCursor 没动）继续走。
                if (recoveries >= MaxThrottleRecoveries)
                {
                    return new SnmpWalkResponse(
                        rows,
                        true,
                        true,
                        $"设备中途停止响应（疑似 SNMP 限速，已暂停重试 {recoveries} 次）；已取 {rows.Count} 条", LastOidOf(rows));
                }

                recoveries++;
                await Task.Delay(ThrottlePauseMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            bulkResponded = true;
            Interlocked.Increment(ref _bulkStepsSucceeded);
            var (batch, endOfMib) = step.Value;
            if (batch.Count == 0)
            {
                // 第一步就空批次 / endOfMibView：说明这台代理**不是真的支持 GETBULK**
                // （合规代理会返回接下来的 N 个节点；不认 0xA5 的代理常常把它当普通 GET 回 noSuchObject）。
                // 这种情况当作"不支持"，记下来并退回 GETNEXT —— 否则会把"整棵子树"误判成空。
                // 注意：子树真的是空的时候，GETNEXT 也会立刻到底，结果一样是空，不会额外出错。
                BulkUnsupported[bulkKey] = true;
                bulkResponded = false;      // 关键：不能算"GETBULK 成功"，否则不会退回 GETNEXT
                break;
            }

            var last = bulkCursor;
            var sawNonAdvancingBatch = false;
            foreach (var value in batch)
            {
                if (!value.Oid.StartsWith(prefix + ".", StringComparison.Ordinal))
                {
                    return new SnmpWalkResponse(rows, true, false, null, LastOidOf(rows));   // 走出子树：正常结束
                }

                if (CompareOid(value.Oid, last) <= 0)
                {
                    // 单调前进保护（附录 A#12）：设备回了"不前进"的批量 → 这是病态响应，
                    // 不能拿来当结果（否则表格会被静默截断），丢掉这批并退回 GETNEXT。
                    sawNonAdvancingBatch = true;
                    break;
                }

                rows.Add(value);
                last = value.Oid;
                if (rows.Count >= maxRows)
                {
                    return new SnmpWalkResponse(rows, true, true, null, LastOidOf(rows));
                }
            }

            if (sawNonAdvancingBatch)
            {
                BulkUnsupported[bulkKey] = true;
                bulkResponded = false;
                break;
            }

            if (endOfMib)
            {
                return new SnmpWalkResponse(rows, true, false, null, LastOidOf(rows));
            }

            bulkCursor = last;
            progress?.Report(rows.Count);

            // 主动降速：每 PaceEveryRequests 次请求歇一下，尽量避免触发设备的限速阈值。
            requestsSincePace++;
            if (requestsSincePace >= PaceEveryRequests)
            {
                requestsSincePace = 0;
                await Task.Delay(PaceDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        if (bulkResponded)
        {
            if (rows.Count > 0)
            {
                return new SnmpWalkResponse(rows, true, false, null, LastOidOf(rows));
            }

            // 走完 GETBULK 一行都没拿到：不可信（不认 0xA5 的代理常回一个 noSuchObject/NULL 就算完），
            // 记下来并用 GETNEXT 重走一遍——真正的空子树用 GETNEXT 也是立刻到底，不会多花请求。
            BulkUnsupported[bulkKey] = true;
        }

        // ---------- 退化路径：GETNEXT（老代理） ----------
        rows.Clear();
        var current = resumeFrom ?? prefix;
        var respondedAny = false;

        while (rows.Count < maxRows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (BudgetExceeded())
            {
                return new SnmpWalkResponse(rows, rows.Count > 0, true, $"达到时间预算（{timeBudgetMs / 1000} 秒），已取 {rows.Count} 条", LastOidOf(rows));
            }

            var step = await GetNextOnceAsync(session, community, current, timeoutMs, retries, cancellationToken)
                .ConfigureAwait(false);
            if (!step.Responded || step.Value is null)
            {
                return new SnmpWalkResponse(rows, respondedAny || step.Responded, false, step.Error, LastOidOf(rows));
            }

            respondedAny = true;
            var value = step.Value;

            // 走出子树 / 越界：正常结束
            if (!value.Oid.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return new SnmpWalkResponse(rows, true, false, null, LastOidOf(rows));
            }

            rows.Add(value);
            current = value.Oid;
            if (rows.Count % 50 == 0)
            {
                progress?.Report(rows.Count);
            }
        }

        return new SnmpWalkResponse(rows, respondedAny, true, null, LastOidOf(rows));
    }

    private async Task<(bool Responded, SnmpValue? Value, string? Error)> GetNextOnceAsync(
        UdpSession session,
        string community,
        string oid,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        string? lastError = null;
        // ⚠️ 这里必须真的用上 retries（2026-09-22 修复）。
        // 旧写法是 `for (var attempt = 0; attempt < 1; attempt++)` 外加一句
        // `_ = retries; // 退化路径自带重试` —— 但那个注释是错的：循环只跑一次，
        // 也就是说 **GETNEXT 路径把调用方传的 retries 整个丢掉了**。
        // 后果：任何一次 UDP 丢包/超时都会让**整棵子树**变成空，
        // 上层看到的是"设备没有返回这一列"，而不是"超时"——
        // 现场复现：真机巡检时偶发 `资源表未取到（内存）`，CPU/内存两个最关键的指标直接变 N/A；
        // 经隧道（UU/SnmpTunnel）这种一来一回 2~3 秒的链路更容易撞上。
        for (var attempt = 0; attempt <= Math.Max(0, retries); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var requestId = Interlocked.Increment(ref _requestId);
                var request = BuildGetNextRequest(requestId, community, oid);
                var buffer = await session
                    .RequestAsync(request, requestId, timeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                return ParseGetNextResponse(buffer);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = "SNMP 请求超时";
            }
            catch (SocketException ex)
            {
                lastError = $"网络错误：{ex.SocketErrorCode}";
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        return (false, null, lastError ?? "SNMP 请求失败");
    }

    /// <summary>组装 SNMP v2c GetNextRequest 报文（WALK 用，比 GET 只差一个 PDU tag）。</summary>
    internal static byte[] BuildGetNextRequest(int requestId, string community, string oid) =>
        BuildRequest(requestId, community, new[] { oid }, GetNextRequestTag);

    /// <summary>GETBULK 的 PDU tag（RFC 3416）：一次拿 N 个节点，整棵树才采得动。</summary>
    private const byte GetBulkRequestTag = 0xA5;

    /// <summary>GETBULK 默认每次请求的 max-repetitions（25~50 是实测较稳的区间）。</summary>
    private const int DefaultMaxRepetitions = 25;

    /// <summary>
    /// 周期性降低请求速率，避免设备在长时间 WALK 中触发临时限速。
    /// </summary>
    private const int PaceEveryRequests = 100;

    private const int PaceDelayMs = 120;

    /// <summary>中途被限速时最多"暂停 + 从断点续走"几次。</summary>
    private const int MaxThrottleRecoveries = 6;

    private const int ThrottlePauseMs = 1500;

    /// <summary>
    /// 记住"哪些目标不支持 GETBULK"（键 = host:port）。
    /// 老代理对 0xA5 的表现是**完全不回应**，如果每次都试一次，每个 WALK 都要白等一个超时
    /// （SNMP 页一次查询有十来次 WALK，就白等十来秒）——所以失败一次后本次运行内直接用 GETNEXT。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> BulkUnsupported =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 诊断/自检用：该目标是否已被记住"不支持 GETBULK"。
    ///
    /// 为什么需要它：这份记忆是**按 host:port** 存的，而自检里大量模拟代理都跑在
    /// `127.0.0.1:161` 上 —— 只要有个模拟代理不认 0xA5，同进程后面**真机联测**查同一个
    /// `127.0.0.1:161` 就会被静默降级成 GETNEXT（现象是"真机 GETBULK 成功 0 次"）。
    /// 有了它就能区分"设备真不支持"和"被前面的场景污染了"。产品逻辑不调用它。
    /// </summary>
    internal static bool IsBulkKnownUnsupported(string host, int port) =>
        BulkUnsupported.ContainsKey($"{host}:{port}");

    /// <summary>诊断/自检用：清掉某个目标的 GETBULK 记忆（让真机联测不受前面 mock 场景影响）。</summary>
    internal static void ForgetBulkSupport(string host, int port) =>
        BulkUnsupported.TryRemove($"{host}:{port}", out _);

    /// <summary>
    /// GETBULK 总开关。**2026-09-21 起默认打开**。
    ///
    /// 原因：大型设备的部分表可能包含大量记录。GETNEXT 是一行一个来回，逐条请求耗时较长；
    /// GETBULK 可在一次请求中获取多条记录，减少往返次数。
    ///
    /// 不支持的代理不会被误判成"空表"，因为有三层保护（都已在代码里）：
    ///   ① 不认 0xA5 的代理要么完全不回应、要么按普通 GET 回 noSuchObject → 识别为不可用，记入 BulkUnsupported；
    ///   ② 首个批次为空 → 同样判为不可用并**退回 GETNEXT 重走整棵子树**（不会静默截断）；
    ///   ③ 单调前进保护：返回的 OID 不前进就终止，不会死循环。
    /// 自检里 `BulkEnabled=true/false` 两条路径的 walk 结果必须逐行一致，且必须真的走到 GETBULK 分支。
    /// </summary>
    internal static bool BulkEnabled { get; set; } = true;

    /// <summary>
    /// 真正成功用上 GETBULK 的次数（解析成功才算）。自检靠它证明"这条路径确实被走到了"，
    /// 而不是悄悄退化成 GETNEXT 却仍然测过。
    /// </summary>
    internal static int BulkStepsSucceeded => Volatile.Read(ref _bulkStepsSucceeded);

    private static int _bulkStepsSucceeded;

    /// <summary>
    /// 组装 GETBULK 报文：SEQUENCE { version, community, [0xA5] { request-id, non-repeaters=0,
    /// max-repetitions=N, varbind-list } }。
    /// </summary>
    internal static byte[] BuildGetBulkRequest(int requestId, string community, string oid, int maxRepetitions)
    {
        var varbind = Tlv(SequenceTag, Concat(EncodeOid(oid), Tlv(NullTag, Array.Empty<byte>())));
        var varbindList = Tlv(SequenceTag, varbind);
        var pdu = Tlv(
            GetBulkRequestTag,
            Concat(
                EncodeInteger(requestId),
                EncodeInteger(0),                                  // non-repeaters：不重复的 varbind 个数
                EncodeInteger(Math.Clamp(maxRepetitions, 1, 64)),  // max-repetitions
                varbindList));
        return Tlv(SequenceTag, Concat(EncodeInteger(1), Tlv(OctetStringTag, Encoding.ASCII.GetBytes(community)), pdu));
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }

    /// <summary>
    /// 解析 GETBULK 响应：一个报文里可能带多个 varbind（还可能混着 endOfMibView）。
    /// 返回 <c>Responded=false</c> 表示"没有回应"（超时/格式错），调用方据此决定是否退回 GETNEXT。
    /// </summary>
    internal static (bool Responded, List<SnmpValue> Values, bool EndOfMibView, string? Error) ParseGetBulkResponse(byte[] buffer)
    {
        var values = new List<SnmpValue>();
        try
        {
            var offset = 0;
            if (!TryReadTlv(buffer, ref offset, out var rootTag, out var rootBody) || rootTag != SequenceTag)
            {
                return (false, values, false, "响应格式无法解析");
            }

            var inner = 0;
            TryReadTlv(rootBody, ref inner, out _, out _);                  // version
            TryReadTlv(rootBody, ref inner, out _, out _);                  // community
            if (!TryReadTlv(rootBody, ref inner, out var pduTag, out var pduBody) || pduTag != GetResponseTag)
            {
                return (false, values, false, "设备返回的不是 GetResponse");
            }

            var pduOffset = 0;
            TryReadTlv(pduBody, ref pduOffset, out _, out _);               // request-id
            TryReadTlv(pduBody, ref pduOffset, out _, out var errorBody);   // error-status
            var errorStatus = DecodeInteger(errorBody);
            TryReadTlv(pduBody, ref pduOffset, out _, out _);               // error-index
            if (errorStatus != 0)
            {
                // 老代理不支持 GETBULK 时会回 errorStatus（如 tooBig / genErr）→ 交给调用方退化
                return (false, values, false, $"设备返回错误状态 {errorStatus}（将退回 GETNEXT）");
            }

            if (!TryReadTlv(pduBody, ref pduOffset, out _, out var varbindList))
            {
                return (false, values, false, "响应里没有 varbind");
            }

            var endOfMib = false;
            var vbOffset = 0;
            while (TryReadTlv(varbindList, ref vbOffset, out _, out var varbind))
            {
                var itemOffset = 0;
                if (!TryReadTlv(varbind, ref itemOffset, out var oidTag, out var oidBody) || oidTag != OidTag)
                {
                    continue;
                }

                if (!TryReadTlv(varbind, ref itemOffset, out var valueTag, out var valueBody))
                {
                    continue;
                }

                if (valueTag is EndOfMibViewTag or NoSuchObjectTag or NoSuchInstanceTag)
                {
                    endOfMib = true;      // 子树到头
                    break;
                }

                values.Add(DecodeValue(DecodeOid(oidBody), valueTag, valueBody));
            }

            return (true, values, endOfMib, null);
        }
        catch (Exception ex)
        {
            return (false, values, false, ex.Message);
        }
    }

    /// <summary>
    /// GETBULK 单步：发一次请求拿一批；不支持（超时/错误状态）返回 null 让调用方退化。
    ///
    /// 这里**故意不重试**：GETBULK 超时/断连的已知原因是"老代理不认 0xA5 就干脆不回包"，
    /// 重试只会把这个超时等两三遍（大表上就是几十秒白等），退化到 GETNEXT 才是正确出路。
    /// 所以签名里不再接受 retries —— 旧参数虽然收下了却永远用不到（编译器 CS0162 早就指着这段死循环），
    /// 留着只会让人以为"这里会自动重试若干次"。
    /// </summary>
    private async Task<(List<SnmpValue> Values, bool EndOfMibView)?> TryBulkStepAsync(
        UdpSession session,
        string community,
        string oid,
        int maxRepetitions,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var requestId = Interlocked.Increment(ref _requestId);
            var request = BuildGetBulkRequest(requestId, community, oid, maxRepetitions);
            var buffer = await session
                .RequestAsync(request, requestId, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
            var parsed = ParseGetBulkResponse(buffer);
            if (!parsed.Responded)
            {
                return null;
            }

            return (parsed.Values, parsed.EndOfMibView);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 超时：可能只是老代理不认 0xA5（真机行为是干脆不回）→ 退化
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>解析 GetNextResponse：返回 (OID, 值)；endOfMibView / noSuchObject 视为"到头了"。</summary>
    internal static (bool Responded, SnmpValue? Value, string? Error) ParseGetNextResponse(byte[] buffer)
    {
        try
        {
            var offset = 0;
            if (!TryReadTlv(buffer, ref offset, out var rootTag, out var rootBody) || rootTag != SequenceTag)
            {
                return (false, null, "响应格式无法解析");
            }

            var inner = 0;
            TryReadTlv(rootBody, ref inner, out _, out _);                 // version
            TryReadTlv(rootBody, ref inner, out _, out _);                 // community
            if (!TryReadTlv(rootBody, ref inner, out var pduTag, out var pduBody) || pduTag != GetResponseTag)
            {
                return (false, null, "设备返回的不是 GetResponse");
            }

            var pduOffset = 0;
            TryReadTlv(pduBody, ref pduOffset, out _, out _);              // request-id
            TryReadTlv(pduBody, ref pduOffset, out _, out var errorBody);  // error-status
            var errorStatus = DecodeInteger(errorBody);
            TryReadTlv(pduBody, ref pduOffset, out _, out var errorIndexBody);
            var errorIndex = DecodeInteger(errorIndexBody);
            if (errorStatus != 0)
            {
                return (true, null, errorStatus == 1
                    ? "设备无更多数据（noSuchName）"
                    : $"设备返回错误状态 {errorStatus}（index {errorIndex}）");
            }

            if (!TryReadTlv(pduBody, ref pduOffset, out _, out var varbindList))
            {
                return (false, null, "响应里没有 varbind");
            }

            var vbOffset = 0;
            if (!TryReadTlv(varbindList, ref vbOffset, out _, out var varbind))
            {
                return (false, null, "varbind 为空");
            }

            var itemOffset = 0;
            if (!TryReadTlv(varbind, ref itemOffset, out var oidTag, out var oidBody) || oidTag != OidTag)
            {
                return (false, null, "varbind 里没有 OID");
            }

            var oid = DecodeOid(oidBody);
            if (!TryReadTlv(varbind, ref itemOffset, out var valueTag, out var valueBody))
            {
                return (false, null, "varbind 里没有值");
            }

            // endOfMibView / noSuchObject / noSuchInstance 都是"没有更多数据"
            if (valueTag is EndOfMibViewTag or NoSuchObjectTag or NoSuchInstanceTag)
            {
                return (true, null, null);
            }

            return (true, DecodeValue(oid, valueTag, valueBody), null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private async Task<IReadOnlyDictionary<string, SnmpValue>> TryGetBatchAsync(
        UdpSession session,
        string community,
        IReadOnlyList<string> oids,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        var empty = new Dictionary<string, SnmpValue>(StringComparer.Ordinal);

        for (var attempt = 0; attempt <= Math.Max(0, retries); attempt++)
        {
            try
            {
                var requestId = Interlocked.Increment(ref _requestId);
                var request = BuildGetRequest(requestId, community, oids);
                var buffer = await session
                    .RequestAsync(request, requestId, timeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                return ParseResponse(buffer);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 超时：重试
            }
            catch (SocketException)
            {
                // 网络错误：重试
            }
            catch (Exception)
            {
                return empty;
            }
        }

        return empty;
    }

    private async Task<SnmpGetResponse> TryGetBatchWithStatusAsync(
        UdpSession session,
        string community,
        IReadOnlyList<string> oids,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        string? lastError = null;
        for (var attempt = 0; attempt <= Math.Max(0, retries); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var requestId = Interlocked.Increment(ref _requestId);
                var request = BuildGetRequest(requestId, community, oids);
                var buffer = await session
                    .RequestAsync(request, requestId, timeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                var parsed = ParseResponseDetailed(buffer);
                if (parsed.ErrorStatus == 0)
                {
                    return SnmpGetResponse.Ok(parsed.Values);
                }

                // 设备对整批请求回错误（最常见的 noSuchName=2：批里有一个 OID 设备不认识）。
                // 这时**绝不能**把整批当失败：逐个 OID 再问一次，能取到的就留下。
                if (oids.Count > 1 && parsed.ErrorStatus == 2)
                {
                    var salvaged = new Dictionary<string, SnmpValue>(StringComparer.Ordinal);
                    foreach (var single in oids)
                    {
                        var one = await TryGetBatchWithStatusAsync(
                            session, community, new[] { single }, timeoutMs, 0, cancellationToken).ConfigureAwait(false);
                        foreach (var pair in one.Values)
                        {
                            salvaged[pair.Key] = pair.Value;
                        }
                    }

                    return new SnmpGetResponse(salvaged, true, null);
                }

                if (oids.Count == 1)
                {
                    // 单个 OID 不认识：属于"设备没有这项"，不算失败
                    return new SnmpGetResponse(
                        new Dictionary<string, SnmpValue>(StringComparer.Ordinal), true, null);
                }

                return new SnmpGetResponse(
                    new Dictionary<string, SnmpValue>(StringComparer.Ordinal),
                    true,
                    $"设备返回错误状态 {parsed.ErrorStatus}（index {parsed.ErrorIndex}）");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = "SNMP 请求超时（设备未响应或被防火墙拦截）";
            }
            catch (SocketException ex)
            {
                lastError = $"网络错误：{ex.SocketErrorCode}";
            }
            catch (Exception ex)
            {
                return new SnmpGetResponse(
                    new Dictionary<string, SnmpValue>(StringComparer.Ordinal),
                    false,
                    ex.Message);
            }
        }

        return new SnmpGetResponse(new Dictionary<string, SnmpValue>(StringComparer.Ordinal), false, lastError);
    }

    /// <summary>组装 SNMP v2c 请求（GET / GETNEXT 共用，只有 PDU tag 不同）。</summary>
    internal static byte[] BuildRequest(int requestId, string community, IReadOnlyList<string> oids, byte pduTag)
    {
        var varbinds = new List<byte>();
        foreach (var oid in oids)
        {
            var item = new List<byte>();
            item.AddRange(EncodeOid(oid));
            item.AddRange(Tlv(NullTag, Array.Empty<byte>()));
            varbinds.AddRange(Tlv(SequenceTag, item.ToArray()));
        }

        var pduBody = new List<byte>();
        pduBody.AddRange(EncodeInteger(requestId));
        pduBody.AddRange(EncodeInteger(0)); // error-status
        pduBody.AddRange(EncodeInteger(0)); // error-index
        pduBody.AddRange(Tlv(SequenceTag, varbinds.ToArray()));

        var message = new List<byte>();
        message.AddRange(EncodeInteger(1)); // version = v2c
        message.AddRange(Tlv(OctetStringTag, Encoding.ASCII.GetBytes(community ?? string.Empty)));
        message.AddRange(Tlv(pduTag, pduBody.ToArray()));

        return Tlv(SequenceTag, message.ToArray());
    }

    /// <summary>组装 SNMP v2c GetRequest 报文。</summary>
    internal static byte[] BuildGetRequest(int requestId, string community, IReadOnlyList<string> oids)
    {
        var varbinds = new List<byte>();
        foreach (var oid in oids)
        {
            var item = new List<byte>();
            item.AddRange(EncodeOid(oid));
            item.AddRange(Tlv(NullTag, Array.Empty<byte>()));
            varbinds.AddRange(Tlv(SequenceTag, item.ToArray()));
        }

        var pduBody = new List<byte>();
        pduBody.AddRange(EncodeInteger(requestId));
        pduBody.AddRange(EncodeInteger(0)); // error-status
        pduBody.AddRange(EncodeInteger(0)); // error-index
        pduBody.AddRange(Tlv(SequenceTag, varbinds.ToArray()));

        var message = new List<byte>();
        message.AddRange(EncodeInteger(1)); // version = v2c
        message.AddRange(Tlv(OctetStringTag, Encoding.ASCII.GetBytes(community ?? string.Empty)));
        message.AddRange(Tlv(GetRequestTag, pduBody.ToArray()));

        return Tlv(SequenceTag, message.ToArray());
    }

    /// <summary>解析 GetResponse：返回 OID → 值；解析不了的部分直接跳过。</summary>
    internal static Dictionary<string, SnmpValue> ParseResponse(byte[] buffer)
        => ParseResponseDetailed(buffer).Values;

    /// <summary>
    /// 解析 GetResponse，并把 error-status / error-index 一起返回。
    /// 之前只看"有没有值"，导致设备回 noSuchName 时无法区分"整批失败"和"其中一项没有"。
    /// </summary>
    internal static (Dictionary<string, SnmpValue> Values, long ErrorStatus, long ErrorIndex) ParseResponseDetailed(byte[] buffer)
    {
        var result = new Dictionary<string, SnmpValue>(StringComparer.Ordinal);
        long errorStatus = 0;
        long errorIndex = 0;
        try
        {
            var offset = 0;
            if (!TryReadTlv(buffer, ref offset, out var rootTag, out var rootBody) || rootTag != SequenceTag)
            {
                return (result, -1, -1);
            }

            var inner = 0;
            TryReadTlv(rootBody, ref inner, out _, out _);                 // version
            TryReadTlv(rootBody, ref inner, out _, out _);                 // community
            if (!TryReadTlv(rootBody, ref inner, out var pduTag, out var pduBody) || pduTag != GetResponseTag)
            {
                return (result, -1, -1);
            }

            var pduOffset = 0;
            TryReadTlv(pduBody, ref pduOffset, out _, out _);              // request-id
            TryReadTlv(pduBody, ref pduOffset, out _, out var errorBody);  // error-status
            errorStatus = DecodeInteger(errorBody);
            TryReadTlv(pduBody, ref pduOffset, out _, out var errorIndexBody);   // error-index
            errorIndex = DecodeInteger(errorIndexBody);
            if (errorStatus != 0)
            {
                return (result, errorStatus, errorIndex);
            }

            if (!TryReadTlv(pduBody, ref pduOffset, out _, out var varbindList))
            {
                return (result, 0, 0);
            }

            var vbOffset = 0;
            while (vbOffset < varbindList.Length)
            {
                if (!TryReadTlv(varbindList, ref vbOffset, out _, out var varbind))
                {
                    break;
                }

                var itemOffset = 0;
                if (!TryReadTlv(varbind, ref itemOffset, out var oidTag, out var oidBody) || oidTag != OidTag)
                {
                    continue;
                }

                var oid = DecodeOid(oidBody);
                if (!TryReadTlv(varbind, ref itemOffset, out var valueTag, out var valueBody))
                {
                    continue;
                }

                result[oid] = DecodeValue(oid, valueTag, valueBody);
            }
        }
        catch (Exception)
        {
            // 解析失败就当没取到，界面显示 N/A
        }

        return (result, errorStatus, errorIndex);
    }

    private static SnmpValue DecodeValue(string oid, byte tag, ReadOnlySpan<byte> body) => tag switch
    {
        IntegerTag => new SnmpValue(oid, "INTEGER", null, DecodeInteger(body)),
        Counter32Tag => new SnmpValue(oid, "Counter32", null, (long)DecodeUnsigned(body)),
        Gauge32Tag => new SnmpValue(oid, "Gauge32", null, (long)DecodeUnsigned(body)),
        TimeTicksTag => new SnmpValue(oid, "TimeTicks", null, (long)DecodeUnsigned(body)),
        Counter64Tag => new SnmpValue(oid, "Counter64", null, (long)DecodeUnsigned(body)),
        IpAddressTag => new SnmpValue(
            oid,
            "IpAddress",
            body.Length == 4 ? $"{body[0]}.{body[1]}.{body[2]}.{body[3]}" : "N/A",
            null),
        // 原始字节要带走（MAC 等二进制字段靠它还原），因此复制一份，避免缓冲区被复用后窜改。
        OctetStringTag => new SnmpValue(oid, "STRING", DecodeText(body), null, body.ToArray()),
        _ => new SnmpValue(oid, $"0x{tag:X2}", DecodeText(body), null),
    };

    private static string DecodeText(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
        {
            return string.Empty;
        }

        // 纯 ASCII 直接当文本（最常见：接口名、型号、英文 sysName）。
        // 注意：不能对 Span 用 LINQ（它不是 IEnumerable），这里手写循环（也在热路径上）
        var printable = true;
        foreach (var b in body)
        {
            if (b is >= 0x20 and < 0x7F || b is 0x09 or 0x0A or 0x0D)
            {
                continue;
            }

            printable = false;
            break;
        }

        if (printable)
        {
            return Encoding.ASCII.GetString(body);
        }

        // 非 ASCII：按 UTF-8 → GB18030 依次尝试（中文 sysName / ifAlias / VLAN 名在锐捷设备上
        // 常见 GB2312/GBK 编码，旧代码一律转 hex，用户看到的是 "C4 E3 BA C3" 这种"错误信息"）。
        if (TryDecode(body, new UTF8Encoding(false, throwOnInvalidBytes: true), out var utf8Text))
        {
            return utf8Text;
        }

        if (TryDecode(body, TryGetChineseEncoding(), out var gbText))
        {
            return gbText;
        }

        // 两种编码都不是合法文本 → 退化成 hex，至少不显示乱码。
        return Convert.ToHexString(body).Chunk(2).Aggregate(string.Empty, (acc, c) => acc + string.Concat(c) + " ");
    }

    /// <summary>按指定编码严格解码；解不出来（非法字节序列）返回 false。</summary>
    private static bool TryDecode(ReadOnlySpan<byte> body, Encoding? encoding, out string text)
    {
        text = string.Empty;
        if (encoding is null)
        {
            return false;
        }

        try
        {
            var decoder = encoding.GetDecoder();
            var chars = new char[encoding.GetMaxCharCount(body.Length)];
            var count = decoder.GetChars(body, chars, flush: true);
            if (count == 0)
            {
                return false;
            }

            text = new string(chars, 0, count);
            return true;
        }
        catch (Exception)
        {
            // DecoderFallbackException（非法序列）/ ArgumentException（编码不可用）
            return false;
        }
    }

    /// <summary>
    /// 取中文编码（GB18030，兼容 GB2312/GBK）。
    /// .NET Core 默认不带代码页编码，需要注册 CodePagesEncodingProvider；
    /// 注册不了就返回 null，此时只保留 UTF-8 / hex 两条路（不猜、不显示乱码）。
    /// </summary>
    private static Encoding? TryGetChineseEncoding()
    {
        if (_chineseEncodingResolved)
        {
            return _chineseEncoding;
        }

        _chineseEncodingResolved = true;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _chineseEncoding = Encoding.GetEncoding(54936);   // GB18030（兼容 GB2312 / GBK）
        }
        catch (Exception)
        {
            // 运行环境没有代码页编码（正常情况下 Windows 桌面版内置）→ 只走 UTF-8 / hex。
            _chineseEncoding = null;
        }

        return _chineseEncoding;
    }

    private static Encoding? _chineseEncoding;
    private static bool _chineseEncodingResolved;

    private static byte[] Tlv(byte tag, byte[] body)
    {
        var length = EncodeLength(body.Length);
        var result = new byte[1 + length.Length + body.Length];
        result[0] = tag;
        Array.Copy(length, 0, result, 1, length.Length);
        Array.Copy(body, 0, result, 1 + length.Length, body.Length);
        return result;
    }

    private static byte[] EncodeLength(int length)
    {
        if (length < 0x80)
        {
            return new[] { (byte)length };
        }

        var bytes = new List<byte>();
        var value = length;
        while (value > 0)
        {
            bytes.Insert(0, (byte)(value & 0xFF));
            value >>= 8;
        }

        bytes.Insert(0, (byte)(0x80 | bytes.Count));
        return bytes.ToArray();
    }

    private static byte[] EncodeInteger(int value)
    {
        var bytes = new List<byte>();
        var v = value;
        do
        {
            bytes.Insert(0, (byte)(v & 0xFF));
            v >>= 8;
        }
        while (v != 0 && v != -1);

        if ((bytes[0] & 0x80) != 0)
        {
            bytes.Insert(0, 0);
        }

        return Tlv(IntegerTag, bytes.ToArray());
    }

    private static byte[] EncodeOid(string oid)
    {
        var parts = oid.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var v) ? v : 0)
            .ToArray();

        var body = new List<byte>();
        if (parts.Length >= 2)
        {
            body.Add((byte)(parts[0] * 40 + parts[1]));
        }
        else if (parts.Length == 1)
        {
            body.Add((byte)(parts[0] * 40));
        }

        for (var i = 2; i < parts.Length; i++)
        {
            var value = parts[i];
            var stack = new Stack<byte>();
            stack.Push((byte)(value & 0x7F));
            value >>= 7;
            while (value > 0)
            {
                stack.Push((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }

            foreach (var b in stack)
            {
                body.Add(b);
            }
        }

        return Tlv(OidTag, body.ToArray());
    }

    private static string DecodeOid(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
        {
            return string.Empty;
        }

        var parts = new List<int> { body[0] / 40, body[0] % 40 };
        var value = 0;
        for (var i = 1; i < body.Length; i++)
        {
            value = (value << 7) | (body[i] & 0x7F);
            if ((body[i] & 0x80) == 0)
            {
                parts.Add(value);
                value = 0;
            }
        }

        return string.Join('.', parts);
    }

    /// <summary>
    /// 按 OID 语义比较：**逐段按数值**比，绝不能按字符串比。
    ///
    /// 字符串比较是 GETBULK 单调前进保护踩过的坑：`...1.1.1.1.9` 与 `...1.1.1.1.10`
    /// 按字符比会认为 `.10 &lt; .9`（'1' &lt; '9'），于是"9 → 10"这一步被误判成
    /// "没有前进"，整个 WALK 当场终止 —— 26 个接口的 ifName 表会在第 9 行被**静默截断**
    /// （表格少了一半，但状态仍显示"查询成功"）。
    /// OID 的字典序是按段的数值顺序，不是字节序。
    /// </summary>
    internal static int CompareOid(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        var count = Math.Max(a.Length, b.Length);
        for (var i = 0; i < count; i++)
        {
            var leftPart = i < a.Length ? a[i] : string.Empty;
            var rightPart = i < b.Length ? b[i] : string.Empty;
            if (!long.TryParse(leftPart, out var x) || !long.TryParse(rightPart, out var y))
            {
                // 正常 OID 全是数字；真出现非数字段就退回字节比较，至少不丢可比性
                var fallback = string.CompareOrdinal(leftPart, rightPart);
                if (fallback != 0)
                {
                    return fallback;
                }

                continue;
            }

            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    private static long DecodeInteger(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
        {
            return 0;
        }

        long value = (body[0] & 0x80) != 0 ? -1 : 0;
        foreach (var b in body)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private static ulong DecodeUnsigned(ReadOnlySpan<byte> body)
    {
        ulong value = 0;
        foreach (var b in body)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    /// <summary>
    /// 读一个 TLV。**body 是原缓冲区上的切片（零拷贝）**——旧实现 `out byte[] body` 每次都
    /// `new byte[] + Array.Copy`，而解析一个 varbind 要读多个嵌套 TLV；大表场景会造成大量小数组分配。
    /// 需要真正把字节留下来的地方（例如 MAC 的原始字节）由调用方自己 `.ToArray()`。
    /// </summary>
    private static bool TryReadTlv(ReadOnlySpan<byte> buffer, ref int offset, out byte tag, out ReadOnlySpan<byte> body)
    {
        tag = 0;
        body = Array.Empty<byte>();
        if (offset >= buffer.Length)
        {
            return false;
        }

        tag = buffer[offset++];
        if (offset >= buffer.Length)
        {
            return false;
        }

        var length = (int)buffer[offset++];
        if ((length & 0x80) != 0)
        {
            var count = length & 0x7F;
            if (count == 0 || count > 4 || offset + count > buffer.Length)
            {
                return false;
            }

            length = 0;
            for (var i = 0; i < count; i++)
            {
                length = (length << 8) | buffer[offset++];
            }
        }

        if (length < 0 || offset + length > buffer.Length)
        {
            return false;
        }

        body = buffer.Slice(offset, length);
        offset += length;
        return true;
    }
}
