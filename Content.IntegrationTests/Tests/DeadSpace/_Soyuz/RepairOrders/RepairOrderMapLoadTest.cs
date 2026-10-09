// Мёртвый Космос, Союз-1, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-soyuz/master/LICENSES/LICENSE.TXT

using System.Linq;
using Content.Server.DeadSpace._Soyuz.RepairOrders;
using Content.Shared.DeadSpace._Soyuz.RepairOrders;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.DeadSpace._Soyuz.RepairOrders;

[TestFixture]
public sealed class RepairOrderMapLoadTest
{
    [Test]
    public async Task RepairOrderGridsLoad()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entManager = server.ResolveDependency<IEntityManager>();
        var mapLoader = entManager.System<RepairStationGenerationSystem>();
        var mapSystem = entManager.System<SharedMapSystem>();
        var prototypeManager = server.ResolveDependency<IPrototypeManager>();

        await server.WaitPost(() =>
        {
            var repairOrders = prototypeManager.EnumeratePrototypes<RepairOrderPrototype>()
                .OrderBy(order => order.ID)
                .ToArray();

            Assert.That(repairOrders, Is.Not.Empty, "No RepairOrderPrototype instances were loaded.");

            var sources = repairOrders.GroupBy(order => order.ProceduralStation is { } station
                ? $"procedural:{station.Id}" : $"map:{order.TargetGridPath}");
            foreach (var group in sources)
            {
                var order = group.First();
                var diagnostic = $"Repair Order grid '{group.Key}' used by: {string.Join(", ", group.Select(o => o.ID))}.";
                var failingLogCount = pair.ServerLogHandler.FailingLogs.Count;

                mapSystem.CreateMap(out var mapId);
                try
                {
                    Entity<MapGridComponent>? grid;
                    bool loaded;

                    try
                    {
                        loaded = mapLoader.TryCreateTarget(mapId, order, 0, out grid);
                    }
                    catch (Exception exception)
                    {
                        throw new Exception($"Failed to load {diagnostic}", exception);
                    }

                    Assert.That(loaded, Is.True, $"Failed to load {diagnostic}");
                    Assert.That(grid!.Value.Comp.LocalAABB.Size.X, Is.GreaterThan(0f),
                        $"Loaded grid has no width. {diagnostic}");
                    Assert.That(grid.Value.Comp.LocalAABB.Size.Y, Is.GreaterThan(0f),
                        $"Loaded grid has no height. {diagnostic}");
                }
                finally
                {
                    mapSystem.DeleteMap(mapId);
                }

                var mapErrorLogs = pair.ServerLogHandler.FailingLogs
                    .Skip(failingLogCount)
                    .ToArray();
                Assert.That(mapErrorLogs, Is.Empty,
                    $"Error logs were emitted while loading {diagnostic}\n{string.Join('\n', mapErrorLogs)}");
            }
        });

        await pair.CleanReturnAsync();
    }
}
