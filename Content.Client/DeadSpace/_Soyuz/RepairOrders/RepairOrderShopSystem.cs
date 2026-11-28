// Мёртвый Космос, Союз-1, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-soyuz/master/LICENSE-SOYUZ.TXT

using Content.Shared.DeadSpace._Soyuz.RepairOrders;
using Content.Shared.GameTicking;
using Robust.Shared.Timing;

namespace Content.Client.DeadSpace._Soyuz.RepairOrders;

public sealed class RepairOrderShopSystem : EntitySystem
{
    private readonly Dictionary<(NetEntity Console, NetEntity Actor), RepairOrderShopDraft> _drafts = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(_ => _drafts.Clear());
        SubscribeNetworkEvent<RepairOrderShopResultEvent>(OnShopResult);
    }

    public RepairOrderShopDraft GetDraft(EntityUid console, EntityUid actor)
    {
        var key = (GetNetEntity(console), GetNetEntity(actor));
        if (!_drafts.TryGetValue(key, out var draft))
            _drafts.Add(key, draft = new RepairOrderShopDraft());
        return draft;
    }

    public void RequestStatus(EntityUid console, string requestId)
        => RaiseNetworkEvent(new RepairOrderShopStatusRequestEvent(GetNetEntity(console), requestId));

    private void OnShopResult(RepairOrderShopResultEvent result)
    {
        if (_drafts.TryGetValue((result.Console, result.Actor), out var draft))
            draft.ApplyResult(result);
    }

    public override void Shutdown()
    {
        _drafts.Clear();
        base.Shutdown();
    }
}

public sealed class RepairOrderShopDraft
{
    public readonly Dictionary<string, int> Cart = new();
    public string? RewardPoolId;
    public string Search = string.Empty;
    public int Tab;
    public bool Compact = true;
    public string? PendingRequestId;
    public List<RepairOrderRewardBuiEntry>? PendingLines;
    public RepairOrderShopResultEvent? Result;
    public TimeSpan RetryAt;
    public GameTick PurchaseTick;
    public bool StatusRequested;

    public event Action? ResultReceived;

    public void ApplyResult(RepairOrderShopResultEvent result)
    {
        if (PendingRequestId != result.RequestId)
            return;

        PendingRequestId = null;
        Result = result;
        if (result.Success)
            Cart.Clear();
        ResultReceived?.Invoke();
    }
}
