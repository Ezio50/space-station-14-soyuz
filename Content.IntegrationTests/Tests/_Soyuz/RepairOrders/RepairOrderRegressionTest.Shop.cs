// Мёртвый Космос, Союз-1, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-soyuz/master/LICENSE-SOYUZ.TXT

#nullable enable
using System.Collections.Generic;
using System.Linq;
using Content.Client.DeadSpace._Soyuz.RepairOrders;
using Content.Server.DeadSpace._Soyuz.RepairOrders;
using Content.Server.Station.Systems;
using Content.Shared.Access.Components;
using Content.Shared.DeadSpace._Soyuz.RepairOrders;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Soyuz.RepairOrders;

public sealed partial class RepairOrderRegressionTest
{
    [Test]
    public async Task ShopReceiptAndStatusWorkWithConsoleClosed()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var (server, client) = pair;
        var map = await pair.CreateTestMap();
        var session = server.PlayerMan.GetSessionById(client.Session!.UserId);
        var requestId = Guid.NewGuid().ToString("N");
        var unsubmittedId = Guid.NewGuid().ToString("N");
        const string reward = "RepairOrderSteelReward";
        EntityUid console = default;
        EntityUid actor = default;
        var station = default(Entity<RepairOrderStationComponent>);

        await server.WaitPost(() =>
        {
            station = CreateStation(server.EntMan);
            station.Comp.RepairPoints = 100;
            server.System<StationSystem>().AddGridToStation(station.Owner, map.Grid);
            console = server.EntMan.SpawnEntity("RepairOrdersConsole", map.GridCoords);
            server.EntMan.RemoveComponent<AccessReaderComponent>(console);
            actor = server.EntMan.SpawnEntity("MobHuman", map.GridCoords);
            server.PlayerMan.SetAttachedEntity(session, actor);
        });

        await pair.ReallyBeIdle(5);
        var shop = client.System<RepairOrderShopSystem>();
        RepairOrderShopDraft draft = default!;
        var clientConsole = pair.ToClientUid(console);
        await client.WaitAssertion(() =>
        {
            draft = shop.GetDraft(clientConsole, pair.ToClientUid(actor));
            draft.Cart.Add(reward, 1);
            draft.PendingRequestId = requestId;
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(server.System<SharedUserInterfaceSystem>().IsUiOpen(console, RepairOrderUiKey.Key, actor), Is.False);
            var lines = new List<RepairOrderRewardBuiEntry> { new(reward, 1) };
            for (var i = 0; i < 2; i++)
            {
                // The purchase reached its handler, but there is no window to receive a BUI reply.
                var request = new RepairOrderShopPurchaseMessage(requestId, lines) { Actor = actor, UiKey = RepairOrderUiKey.Key };
                server.EntMan.EventBus.RaiseLocalEvent(console, request);
            }

            Assert.That(station.Comp.RepairPoints, Is.EqualTo(98));
            Assert.That(server.EntMan.EntityQuery<MetaDataComponent>().Count(meta =>
                meta.EntityPrototype?.ID == "RepairOrderRewardCrate"), Is.EqualTo(1));
        });

        await pair.ReallyBeIdle(5);
        await client.WaitAssertion(() =>
        {
            Assert.That(draft.PendingRequestId, Is.Null);
            Assert.That(draft.Cart, Is.Empty);
            Assert.That(draft.Result?.Success, Is.True);

            // Simulate a client that did not process the original receipt.
            draft.PendingRequestId = requestId;
            draft.Cart.Add(reward, 1);
            shop.RequestStatus(clientConsole, requestId);
        });

        await pair.ReallyBeIdle(5);
        await client.WaitAssertion(() =>
        {
            Assert.That(draft.PendingRequestId, Is.Null);
            Assert.That(draft.Cart, Is.Empty);
            Assert.That(draft.Result?.Success, Is.True);

            draft.PendingRequestId = unsubmittedId;
            draft.Cart.Add(reward, 1);
            shop.RequestStatus(clientConsole, unsubmittedId);
        });

        await pair.ReallyBeIdle(5);
        await client.WaitAssertion(() =>
        {
            Assert.That(draft.PendingRequestId, Is.Null);
            Assert.That(draft.Cart[reward], Is.EqualTo(1));
            Assert.That(draft.Result?.Success, Is.False);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(station.Comp.RepairPoints, Is.EqualTo(98));
            server.EntMan.DeleteEntity(station.Owner);
            server.EntMan.DeleteEntity(map.MapUid);
        });
        await pair.CleanReturnAsync();
    }
}
