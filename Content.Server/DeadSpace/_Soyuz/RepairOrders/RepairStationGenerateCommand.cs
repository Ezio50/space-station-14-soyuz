// Мёртвый Космос, Союз-1, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-soyuz/master/LICENSES/LICENSE.TXT

using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.DeadSpace._Soyuz.RepairOrders;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.DeadSpace._Soyuz.RepairOrders;

[AdminCommand(AdminFlags.Mapping)]
public sealed class RepairStationGenerateCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public string Command => "repairgen";
    public string Description => "Generate an intact procedural repair station on an existing map.";
    public string Help => "repairgen <mapId> <repairStation prototype> <seed> <x> <y>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 5 || !int.TryParse(args[0], out var mapNumber) ||
            !int.TryParse(args[2], out var seed) || !int.TryParse(args[3], out var x) || !int.TryParse(args[4], out var y))
        {
            shell.WriteError(Help);
            return;
        }
        var configuration = new ProtoId<RepairStationPrototype>(args[1]);
        var maps = _entities.System<SharedMapSystem>();
        var mapId = new MapId(mapNumber);
        if (!maps.MapExists(mapId) || !_prototypes.HasIndex(configuration))
        {
            shell.WriteError("Unknown map or repairStation prototype.");
            return;
        }

        EntityUid grid = EntityUid.Invalid;
        try
        {
            grid = _entities.System<RepairStationGenerationSystem>().Generate(mapId, configuration, seed).Owner;
            _entities.System<SharedTransformSystem>().SetWorldPosition(grid, new System.Numerics.Vector2(x, y));
            _entities.System<MetaDataSystem>().SetEntityName(grid, Loc.GetString("repair-station-generated-name"));
            shell.WriteLine($"Generated station {_entities.GetNetEntity(grid)}: {configuration}, seed {seed}.");
        }
        catch (Exception exception)
        {
            if (grid.IsValid())
                _entities.DeleteEntity(grid);
            shell.WriteError($"Station generation failed: {exception.Message}");
        }
    }
}
