// Мёртвый Космос, Союз-1, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-soyuz/master/LICENSES/LICENSE.TXT

using System.Linq;
using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Decals;
using Content.Server.Shuttles.Components;
using Content.Shared.DeadSpace._Soyuz.RepairOrders;
using Content.Shared.Decals;
using Content.Shared.Maps;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server.DeadSpace._Soyuz.RepairOrders;

public sealed class RepairStationGenerationSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly ITileDefinitionManager _tiles = default!;
    [Dependency] private readonly TileSystem _tile = default!;
    [Dependency] private readonly MetaDataSystem _metadata = default!;
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly MapLoaderSystem _loader = default!;
    [Dependency] private readonly DecalSystem _decals = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private sealed class Layout
    {
        public readonly Dictionary<Vector2i, string> Tiles = new();
        public readonly Dictionary<Vector2i, EntProtoId> Walls = new();
        public readonly Dictionary<Vector2i, Angle> WallRotations = new();
        public readonly List<Placement> Entities = new();
        public readonly HashSet<Vector2i> ProtectedCells = new();
        public readonly HashSet<Vector2i> LowVoltage = new();
        public readonly HashSet<Vector2i> MediumVoltage = new();
        public readonly HashSet<Vector2i> HighVoltage = new();
        public readonly HashSet<Vector2i> Occupied = new();
        public readonly HashSet<Vector2i> Walkways = new();
        public readonly HashSet<Vector2i> MountedWalls = new();
        public readonly HashSet<int> DoorColumns = new();
        public readonly List<FloorMarking> Decals = new();
        public readonly Dictionary<Vector2i, BorderStyle> Borders = new();
        public readonly HashSet<Vector2i> RoomEntries = new();
    }

    private readonly record struct Placement(EntProtoId Prototype, Vector2i Cell, Angle Rotation,
        LocId? Name = null, Vector2 Offset = default, bool Anchor = false);
    private readonly record struct FloorMarking(ProtoId<DecalPrototype> Prototype, Vector2i Cell, Color? Color, Angle Rotation);
    private readonly record struct BorderStyle(Dictionary<Direction, ProtoId<DecalPrototype>> Lines,
        Dictionary<Direction, ProtoId<DecalPrototype>> Corners, Dictionary<Direction, ProtoId<DecalPrototype>> InnerCorners,
        Color Color, Angle Rotation, string? FeatureTile = null);
    private readonly record struct RoomArea(int Left, int Right, int Side, int Depth, int DoorX, bool Mirrored)
    {
        public int Center => (Left + Right) / 2;
        public Vector2i Cell(int x, int depth) => new(x, (2 + depth) * Side);
    }

    public bool TryCreateTarget(MapId mapId, RepairOrderPrototype order, int seed, out Entity<MapGridComponent>? grid)
    {
        if (order.ProceduralStation is not { } station)
            return _loader.TryLoadGrid(mapId, order.TargetGridPath, out grid);

        grid = Generate(mapId, station, seed);
        return true;
    }

    public Entity<MapGridComponent> Generate(MapId mapId, ProtoId<RepairStationPrototype> configuration, int seed)
    {
        var config = _prototypes.Index(configuration);
        var layout = BuildLayout(config, seed);
        ValidateLayout(layout, config);
        var grid = _mapManager.CreateGridEntity(mapId);
        try
        {
            var generated = AddComp<RepairGeneratedStationComponent>(grid);
            generated.Configuration = configuration;
            generated.Seed = seed;
            generated.ProtectedCells.UnionWith(layout.ProtectedCells);
            var tileRandom = new Random(unchecked(seed ^ 0x51ed270b));
            _map.SetTiles(grid.Owner, grid.Comp, layout.Tiles.OrderBy(p => p.Key.X).ThenBy(p => p.Key.Y)
                .Select(p => (p.Key, _tile.GetVariantTile((ContentTileDefinition) _tiles[p.Value], tileRandom))).ToList());
            EnsureComp<ShuttleComponent>(grid);

            foreach (var (cell, prototype) in layout.Walls.OrderBy(p => p.Key.X).ThenBy(p => p.Key.Y))
                Place(grid, new Placement(prototype, cell, layout.WallRotations.GetValueOrDefault(cell)));
            foreach (var cell in layout.LowVoltage.OrderBy(c => c.X).ThenBy(c => c.Y))
                Place(grid, new Placement(config.LowVoltageCable, cell, Angle.Zero));
            foreach (var cell in layout.MediumVoltage.OrderBy(c => c.X).ThenBy(c => c.Y))
                Place(grid, new Placement(config.MediumVoltageCable, cell, Angle.Zero));
            foreach (var cell in layout.HighVoltage.OrderBy(c => c.X).ThenBy(c => c.Y))
                Place(grid, new Placement(config.HighVoltageCable, cell, Angle.Zero));
            foreach (var entity in layout.Entities)
                Place(grid, entity);

            EnsureComp<DecalGridComponent>(grid);
            foreach (var marking in layout.Decals)
            {
                var coordinates = new EntityCoordinates(grid.Owner, new Vector2(marking.Cell.X, marking.Cell.Y) * grid.Comp.TileSize);
                if (!_decals.TryAddDecal(marking.Prototype.Id, coordinates, out _, color: marking.Color, rotation: marking.Rotation))
                    throw new InvalidOperationException($"Cannot place station decal {marking.Prototype} at {marking.Cell}.");
            }

            if (_map.IsInitialized(mapId))
                EntityManager.RunMapInit(grid.Owner, MetaData(grid.Owner));
            var atmosphere = EnsureComp<GridAtmosphereComponent>(grid);
            _atmos.RebuildGridAtmosphere((grid.Owner, atmosphere, grid.Comp));
            return grid;
        }
        catch
        {
            Del(grid.Owner);
            throw;
        }
    }

    private void Place(Entity<MapGridComponent> grid, Placement placement)
    {
        var uid = SpawnAttachedTo(placement.Prototype.Id,
            new EntityCoordinates(grid.Owner, (new Vector2(placement.Cell.X, placement.Cell.Y) + new Vector2(0.5f) + placement.Offset) * grid.Comp.TileSize),
            rotation: placement.Rotation);
        if (placement.Anchor && !Transform(uid).Anchored && !_transform.AnchorEntity(uid))
            throw new InvalidOperationException($"Cannot anchor generated station object {placement.Prototype} at {placement.Cell}.");
        if (placement.Name is { } name)
            _metadata.SetEntityName(uid, Loc.GetString(name));
    }

    private Layout BuildLayout(RepairStationPrototype config, int seed)
    {
        var random = new Random(seed);
        var layout = new Layout();
        var columns = random.Next(config.MinColumns, config.MaxColumns + 1);
        var widths = Enumerable.Range(0, columns)
            .Select(_ => random.Next(config.MinRoomWidth, config.MaxRoomWidth + 1)).ToArray();
        var junction = columns / 2;
        var commonLeft = widths.Take(junction).Sum();
        var commonArea = config.CommonAreas.Count > 0 ? config.CommonAreas[random.Next(config.CommonAreas.Count)] : config.CommonArea;
        var commonRooms = new Dictionary<int, ProtoId<RepairStationRoomPrototype>>();
        if (commonArea is { } firstCommon)
        {
            commonRooms[1] = firstCommon;
            var alternatives = config.CommonAreas.Where(room => room != firstCommon).ToArray();
            commonRooms[-1] = alternatives.Length > 0 ? alternatives[random.Next(alternatives.Length)] : firstCommon;
        }
        var commonWidth = commonArea == null ? 0 : config.CommonAreaWidth;
        var branchColumns = commonArea == null ? 0 : random.Next(config.MinBranchColumns, config.MaxBranchColumns + 1);
        var length = widths.Sum() + commonWidth;
        var roomPool = config.RequiredRooms.ToList();
        var optionalRooms = config.Rooms.Except(config.RequiredRooms).Distinct().ToList();
        optionalRooms.RemoveAll(room => commonRooms.Values.Contains(room));
        Shuffle(optionalRooms, random);
        while (roomPool.Count < (columns + branchColumns) * 2)
        {
            if (optionalRooms.Count == 0)
                throw new InvalidOperationException($"Repair station {config.ID} has too few room choices.");
            roomPool.Add(optionalRooms[(roomPool.Count - config.RequiredRooms.Count) % optionalRooms.Count]);
        }
        Shuffle(roomPool, random);
        var sections = roomPool.GroupBy(room => _prototypes.Index(room).Section).Select(group => group.ToList()).ToList();
        Shuffle(sections, random);
        roomPool = sections.SelectMany(section => section).ToList();

        Rectangle(layout, -5, length + 5, -2, 2, config.CorridorTile, config.ReinforcedWall);
        for (var x = -4; x < length + 5; x++)
        {
            layout.Tiles[new Vector2i(x, -1)] = config.CorridorTrim;
            layout.Tiles[new Vector2i(x, 1)] = config.CorridorTrim;
            layout.LowVoltage.Add(new Vector2i(x, 0));
            layout.MediumVoltage.Add(new Vector2i(x, 0));
            layout.HighVoltage.Add(new Vector2i(x, 0));
        }

        var start = 0;
        var roomNumber = 0;
        var wingDepths = new Dictionary<int, int> { [1] = 0, [-1] = 0 };
        var commonDepths = new Dictionary<int, int>();
        foreach (var side in new[] { 1, -1 })
        {
            var wingWidths = side > 0 ? widths :
                PartitionWidths(config, junction, commonLeft, random)
                    .Concat(PartitionWidths(config, columns - junction, widths.Sum() - commonLeft, random)).ToArray();
            start = 0;
            for (var i = 0; i < wingWidths.Length; i++)
            {
                if (i == junction)
                    start += commonWidth;
                var width = wingWidths[i];
                var room = _prototypes.Index(roomPool[roomNumber++ % roomPool.Count]);
                var depth = Math.Max(room.MinDepth, random.Next(config.MinRoomDepth, config.MaxRoomDepth + 1));
                wingDepths[side] = Math.Max(wingDepths[side], depth);
                BuildRoom(layout, config, room, start, start + width, side, depth, random);
                start += width;
            }
        }
        if (commonArea != null)
        {
            foreach (var side in new[] { 1, -1 })
            {
                var room = _prototypes.Index(commonRooms[side]);
                var depth = Math.Max(room.MinDepth, Math.Clamp(config.CommonAreaDepth + random.Next(-1, 2), 6, 12));
                commonDepths[side] = depth;
                wingDepths[side] = Math.Max(wingDepths[side], depth);
                BuildRoom(layout, config, room, commonLeft, commonLeft + commonWidth, side, depth, random, openEntrance: true);
            }
        }

        if (branchColumns > 0)
        {
            var side = random.Next(2) == 0 ? 1 : -1;
            BuildBranch(layout, config, roomPool.Skip(roomNumber).ToArray(), branchColumns,
                (commonLeft + commonLeft + commonWidth) / 2, side, commonDepths[side], wingDepths[side], random);
        }

        foreach (var end in new[] { -5, length + 5 })
        {
            var cell = new Vector2i(end, 0);
            layout.Walls.Remove(cell);
            layout.Entities.Add(new Placement(config.Dock, cell, Angle.FromDegrees(end < 0 ? 270 : 90)));
            for (var x = end < 0 ? -5 : length + 2; x <= (end < 0 ? -2 : length + 5); x++)
            for (var y = -2; y <= 2; y++)
                layout.ProtectedCells.Add(new Vector2i(x, y));
            layout.LowVoltage.Add(cell);
            Mark(layout, config.DockDecal, new Vector2i(end < 0 ? end + 1 : end - 1, 0), null, Angle.FromDegrees(end < 0 ? 90 : 270));
        }

        foreach (var x in new[] { -3, length + 3 })
        {
            var lampX = x < 0 ? x - 1 : x + 1;
            MountLight(layout, config.Light, new Vector2i(lampX, 1), new Vector2i(lampX, 2));
            MountLight(layout, config.Light, new Vector2i(lampX, -1), new Vector2i(lampX, -2));
            layout.Entities.Add(new Placement(config.Apc, new Vector2i(x + 1, 2), Angle.Zero));
            for (var y = 0; y <= 2; y++)
            {
                layout.LowVoltage.Add(new Vector2i(x + 1, y));
                layout.MediumVoltage.Add(new Vector2i(x + 1, y));
            }
        }

        foreach (var x in new[] { -1, length + 1 })
        foreach (var y in new[] { -2, 2 })
        {
            var cell = new Vector2i(x, y);
            layout.Walls.Remove(cell);
            layout.Entities.Add(new Placement(config.Grille, cell, Angle.Zero));
            layout.Entities.Add(new Placement(config.ExteriorWindow, cell, Angle.Zero));
        }

        layout.Entities.Add(new Placement(config.Smes, new Vector2i(-3, 1), Angle.Zero));
        layout.Entities.Add(new Placement(config.Substation, new Vector2i(-3, -1), Angle.Zero));
        for (var y = -1; y <= 1; y++)
        {
            layout.HighVoltage.Add(new Vector2i(-3, y));
            layout.MediumVoltage.Add(new Vector2i(-3, y));
        }
        FinishHull(layout, config);
        DressCorridor(layout, config, length, random);
        FrameFloors(layout);
        return layout;
    }

    private void BuildBranch(Layout layout, RepairStationPrototype config, ProtoId<RepairStationRoomPrototype>[] rooms,
        int columns, int center, int side, int commonDepth, int wingDepth, Random random)
    {
        var branch = new Layout();
        var widths = Enumerable.Range(0, columns).Select(_ => random.Next(config.MinRoomWidth, config.MaxRoomWidth + 1)).ToArray();
        var length = widths.Sum();
        Rectangle(branch, -5, length + 5, -2, 2, config.CorridorTile, config.ReinforcedWall);
        for (var x = -4; x < length + 5; x++)
        {
            foreach (var y in new[] { -1, 1 })
                branch.Tiles[new Vector2i(x, y)] = config.CorridorTrim;
            branch.LowVoltage.Add(new Vector2i(x, 0));
            branch.MediumVoltage.Add(new Vector2i(x, 0));
            branch.HighVoltage.Add(new Vector2i(x, 0));
        }
        var number = 0;
        foreach (var roomSide in new[] { 1, -1 })
        {
            var start = 0;
            foreach (var width in widths)
            {
                var room = _prototypes.Index(rooms[number++]);
                BuildRoom(branch, config, room, start, start + width, roomSide,
                    Math.Max(room.MinDepth, random.Next(config.MinRoomDepth, config.MaxRoomDepth + 1)), random);
                start += width;
            }
        }
        var dock = new Vector2i(length + 5, 0);
        branch.Walls.Remove(dock);
        branch.Entities.Add(new Placement(config.Dock, dock, Angle.FromDegrees(90)));
        branch.LowVoltage.Add(dock);
        for (var x = length + 2; x <= length + 5; x++)
        for (var y = -2; y <= 2; y++)
            branch.ProtectedCells.Add(new Vector2i(x, y));
        Mark(branch, config.DockDecal, dock - new Vector2i(1, 0), null, Angle.FromDegrees(270));
        foreach (var end in new[] { -4, length + 4 })
        foreach (var y in new[] { -1, 1 })
            MountLight(branch, config.Light, new Vector2i(end, y), new Vector2i(end, 2 * y));
        DressCorridor(branch, config, length, random);

        var origin = new Vector2i(center, (wingDepth + 9) * side);
        var rotation = Angle.FromDegrees(side > 0 ? 90 : 270);
        MergeLayout(layout, branch, origin, rotation);
        var entrance = (commonDepth + 2) * side;
        var exit = origin.Y - 5 * side;
        layout.Entities.RemoveAll(p => (p.Cell.Y == entrance || p.Cell.Y == exit) && Math.Abs(p.Cell.X - center) <= 2);
        Rectangle(layout, center - 2, center + 2, Math.Min(entrance, exit), Math.Max(entrance, exit),
            config.CorridorTile, config.ReinforcedWall);
        foreach (var y in new[] { entrance, exit })
        for (var x = center - 1; x <= center + 1; x++)
        {
            var cell = new Vector2i(x, y);
            layout.Walls.Remove(cell);
            layout.WallRotations.Remove(cell);
            layout.Entities.RemoveAll(p => p.Cell == cell);
            layout.Occupied.Remove(cell);
            layout.MountedWalls.Remove(cell);
        }
        for (var y = Math.Min(entrance, exit); y <= Math.Max(entrance, exit); y++)
        {
            var cell = new Vector2i(center, y);
            layout.Walkways.Add(cell);
            layout.LowVoltage.Add(cell);
            layout.MediumVoltage.Add(cell);
            layout.HighVoltage.Add(cell);
            foreach (var x in new[] { center - 1, center + 1 })
            {
                var floor = new Vector2i(x, y);
                layout.Tiles[floor] = config.CorridorTrim;
                layout.Borders[floor] = CorridorBorder(config);
            }
            if (y != entrance && y != exit && y % 4 == 0)
                MountLight(layout, config.Light, cell - new Vector2i(1, 0), cell - new Vector2i(2, 0));
        }
        for (var y = 0; y * side <= Math.Abs(entrance); y += side)
        {
            layout.LowVoltage.Add(new Vector2i(center, y));
            layout.MediumVoltage.Add(new Vector2i(center, y));
            layout.HighVoltage.Add(new Vector2i(center, y));
        }
    }

    private static Vector2i RotateCell(Vector2i cell, Angle rotation)
    {
        var rotated = rotation.RotateVec(new Vector2(cell.X, cell.Y));
        return new Vector2i((int) Math.Round(rotated.X), (int) Math.Round(rotated.Y));
    }

    private static void MergeLayout(Layout target, Layout source, Vector2i origin, Angle rotation)
    {
        Vector2i Cell(Vector2i cell) => origin + RotateCell(cell, rotation);
        foreach (var (cell, tile) in source.Tiles)
        {
            if (!target.Tiles.TryAdd(Cell(cell), tile))
                throw new InvalidOperationException($"Procedural station wings overlap at {Cell(cell)}.");
        }
        foreach (var (cell, prototype) in source.Walls)
            target.Walls.Add(Cell(cell), prototype);
        foreach (var (cell, angle) in source.WallRotations)
            target.WallRotations.Add(Cell(cell), angle + rotation);
        foreach (var placement in source.Entities)
            target.Entities.Add(placement with { Cell = Cell(placement.Cell), Rotation = placement.Rotation + rotation,
                Offset = rotation.RotateVec(placement.Offset) });
        foreach (var marking in source.Decals)
            target.Decals.Add(marking with { Cell = Cell(marking.Cell), Rotation = marking.Rotation + rotation });
        foreach (var (cell, border) in source.Borders)
            target.Borders.Add(Cell(cell), border with { Rotation = border.Rotation + rotation });
        target.ProtectedCells.UnionWith(source.ProtectedCells.Select(Cell));
        target.Occupied.UnionWith(source.Occupied.Select(Cell));
        target.Walkways.UnionWith(source.Walkways.Select(Cell));
        target.MountedWalls.UnionWith(source.MountedWalls.Select(Cell));
        target.RoomEntries.UnionWith(source.RoomEntries.Select(Cell));
        target.LowVoltage.UnionWith(source.LowVoltage.Select(Cell));
        target.MediumVoltage.UnionWith(source.MediumVoltage.Select(Cell));
        target.HighVoltage.UnionWith(source.HighVoltage.Select(Cell));
    }

    private void BuildRoom(Layout layout, RepairStationPrototype config, RepairStationRoomPrototype room,
        int left, int right, int side, int depth, Random random, bool openEntrance = false)
    {
        var near = 2 * side;
        var far = (2 + depth) * side;
        var center = (left + right) / 2;
        var doorX = openEntrance ? center : Math.Clamp(center + random.Next(-1, 2), left + 3, right - 3);
        var area = new RoomArea(left, right, side, depth, doorX, random.Next(2) == 0);
        Rectangle(layout, left, right, Math.Min(near, far), Math.Max(near, far), room.Tile, config.Wall);

        for (var x = left + 1; x < right; x++)
        for (var y = 3; y < 2 + depth; y++)
        {
            if (x == left + 1 || x == right - 1 || y == 3 || y == 1 + depth)
                layout.Tiles[new Vector2i(x, y * side)] = room.Trim;
        }
        if (room.FeatureTile is { } featureTile)
        {
            for (var x = left + 2; x <= right - 2; x++)
            for (var v = 2; v <= depth - 2; v++)
                layout.Tiles[area.Cell(x, v)] = featureTile;
        }

        for (var v = 1; v < depth; v++)
            layout.Walkways.Add(area.Cell(center, v));
        layout.Walkways.Add(area.Cell(doorX, 2));
        for (var x = left + 1; x < right; x++)
        {
            layout.Walkways.Add(area.Cell(x, 1));
            layout.Walkways.Add(area.Cell(x, depth / 2));
        }
        var door = new Vector2i(doorX, near);
        layout.RoomEntries.Add(door);
        if (openEntrance)
        {
            for (var x = left + 1; x < right; x++)
            {
                layout.Walls.Remove(new Vector2i(x, near));
                layout.DoorColumns.Add(x);
            }
            MountPanel(layout, config.Apc, area.Cell(left, 1), Angle.FromDegrees(90));
        }
        else
        {
            layout.Walls.Remove(door);
            layout.DoorColumns.Add(doorX);
            layout.Entities.Add(new Placement(room.Door, door, Angle.Zero, room.Name));
            MountPanel(layout, config.Apc, area.Cell(doorX + 1, 0), Angle.FromDegrees(side > 0 ? 0 : 180));
            if (room.Sign is { } sign)
                MountPanel(layout, sign, area.Cell(doorX - 1, 0), Angle.Zero, room.Name);
            var frontWindows = Enumerable.Range(left + 1, right - left - 1)
                .Where(x => Math.Abs(x - doorX) > 1 && !layout.MountedWalls.Contains(new Vector2i(x, near))).ToList();
            Shuffle(frontWindows, random);
            foreach (var x in frontWindows.Take(room.FrontWindows))
            {
                var window = new Vector2i(x, near);
                layout.Walls.Remove(window);
                layout.Entities.Add(new Placement(config.Grille, window, Angle.Zero));
                layout.Entities.Add(new Placement(config.Window, window, Angle.Zero));
            }
        }

        var windowRadius = Math.Min(room.WindowWidth / 2, (right - left - 2) / 2);
        var windowCenter = Math.Clamp(center + random.Next(-1, 2), left + 1 + windowRadius, right - 1 - windowRadius);
        for (var x = windowCenter - windowRadius; room.WindowWidth > 0 && x <= windowCenter + windowRadius; x++)
        {
            var window = new Vector2i(x, far);
            layout.Walls.Remove(window);
            layout.Entities.Add(new Placement(config.Grille, window, Angle.Zero));
            layout.Entities.Add(new Placement(config.ExteriorWindow, window, Angle.Zero));
        }

        if (depth >= 8 && random.NextDouble() < room.PartitionChance)
        {
            for (var x = left + 1; x < right; x++)
            {
                var cell = area.Cell(x, depth - 3);
                layout.Occupied.Add(cell);
                if (x == center)
                    layout.Entities.Add(new Placement(room.PartitionDoor, cell, Angle.Zero));
                else
                {
                    layout.Entities.Add(new Placement(config.Grille, cell, Angle.Zero));
                    layout.Entities.Add(new Placement(config.Window, cell, Angle.Zero));
                }
            }
        }

        MountLight(layout, room.Light, area.Cell(left + 1, depth - 1), area.Cell(left + 1, depth));
        MountLight(layout, room.Light, area.Cell(right - 1, depth - 1), area.Cell(right - 1, depth));
        MountLight(layout, room.Light, area.Cell(left + 1, depth / 2), area.Cell(left, depth / 2));
        MountLight(layout, room.Light, area.Cell(right - 1, depth / 2), area.Cell(right, depth / 2));
        MountLight(layout, room.Light, area.Cell(left + 2, 1), area.Cell(left + 2, 0));
        MountLight(layout, room.Light, area.Cell(right - 2, 1), area.Cell(right - 2, 0));
        foreach (var wall in new[] { area.Cell(left, 2), area.Cell(right, depth - 2) })
        {
            if (room.Posters.Count > 0)
                MountPanel(layout, Pick(room.Posters, random), wall, Angle.Zero);
        }
        for (var i = 0; i < room.WallFixtures.Count; i++)
            MountPanel(layout, room.WallFixtures[i], area.Cell(i % 2 == 0 ? left : right, i / 2 + 3),
                Angle.FromDegrees(i % 2 == 0 ? 90 : 270));

        foreach (var module in room.Modules)
            PlaceModule(layout, room, area, module);

        var groups = room.Furnishings;
        if (groups.Count == 0)
        {
            groups = new List<RepairStationFurnishing>
            {
                new() { Area = RepairStationFurnishingArea.Left, Prototypes = room.Workstations },
                new() { Area = RepairStationFurnishingArea.Right, Prototypes = room.Furniture },
                new() { Area = RepairStationFurnishingArea.Back, Prototypes = room.Storage, MaxCount = 2 },
            };
        }
        foreach (var group in groups)
            Furnish(layout, area, group, random);
        foreach (var x in new[] { left + 1, right - 1 })
        {
            if (room.Decoration.Count > 0)
                TryPlaceFurniture(layout, new Placement(Pick(room.Decoration, random), area.Cell(x, depth - 1), Angle.Zero));
        }

        var cables = layout.Entities.Where(p => p.Cell.Y * side > 2 && p.Cell.Y * side <= 2 + depth &&
            p.Cell.X >= left && p.Cell.X <= right).ToArray();
        for (var y = 0; y <= 2 + depth; y++)
            layout.LowVoltage.Add(new Vector2i(center, y * side));
        for (var x = Math.Min(center, doorX); x <= doorX + 1; x++)
        {
            layout.LowVoltage.Add(new Vector2i(x, near));
            layout.MediumVoltage.Add(new Vector2i(x, near));
        }
        for (var y = 0; y <= 2; y++)
            layout.MediumVoltage.Add(new Vector2i(doorX, y * side));
        if (openEntrance)
        {
            layout.MediumVoltage.Add(new Vector2i(center, near));
            for (var x = left; x <= center; x++)
                layout.MediumVoltage.Add(area.Cell(x, 1));
        }
        foreach (var placement in cables)
        {
            for (var x = Math.Min(center, placement.Cell.X); x <= Math.Max(center, placement.Cell.X); x++)
                layout.LowVoltage.Add(new Vector2i(x, placement.Cell.Y));
        }

        BorderRoom(layout, config, room, area);
        if (!openEntrance)
        {
            Mark(layout, room.EntranceDecal, area.Cell(doorX, 1), room.AccentColor, Angle.FromDegrees(side > 0 ? 0 : 180));
            Mark(layout, config.ArrowDecal, new Vector2i(doorX, side), room.AccentColor, Angle.FromDegrees(side > 0 ? 0 : 180));
        }
    }

    private static void PlaceModule(Layout layout, RepairStationRoomPrototype room,
        RoomArea area, RepairStationRoomModule module)
    {
        var left = module.Anchor == RepairStationModuleAnchor.BackLeft ? area.Left + 1 : area.Right - module.Size.X;
        var depth = area.Depth - module.Size.Y;
        Vector2i Cell(Vector2i position)
        {
            var x = left + position.X;
            return area.Cell(area.Side > 0 ? x : area.Left + area.Right - x, depth + position.Y);
        }

        for (var x = 0; x < module.Size.X; x++)
        for (var y = 0; y < module.Size.Y; y++)
        {
            var cell = Cell(new Vector2i(x, y));
            if (!CanFurnish(layout, cell))
                throw new InvalidOperationException($"Room module overlaps a wall, passage or another module: {room.ID}, {cell}.");
            layout.Occupied.Add(cell);
            if (module.Tile is { } tile)
                layout.Tiles[cell] = tile;
        }
        foreach (var position in module.HighVoltageCables)
            layout.HighVoltage.Add(Cell(position));
        foreach (var entity in module.Entities)
        {
            var cell = Cell(entity.Position);
            var rotation = Angle.FromDegrees(entity.Rotation + (area.Side > 0 ? 0 : 180));
            if (entity.Wall)
            {
                layout.Walls[cell] = entity.Prototype;
                layout.WallRotations[cell] = rotation;
            }
            else
                layout.Entities.Add(new Placement(entity.Prototype, cell, rotation));
            if (entity.HighVoltageConnection)
            {
                for (var y = 0; y <= cell.Y * area.Side; y++)
                    layout.HighVoltage.Add(new Vector2i(cell.X, y * area.Side));
            }
            if (entity.MediumVoltageConnection)
                ConnectRoomCable(layout.MediumVoltage, area, cell);
        }
        if (module.Marking is { } marking)
            Mark(layout, marking, Cell(Vector2i.Zero), room.AccentColor, Angle.FromDegrees(area.Side > 0 ? 0 : 180));
    }

    private static void ConnectRoomCable(HashSet<Vector2i> cable, RoomArea area, Vector2i cell)
    {
        for (var y = 0; y <= cell.Y * area.Side; y++)
            cable.Add(new Vector2i(area.Center, y * area.Side));
        for (var x = Math.Min(area.Center, cell.X); x <= Math.Max(area.Center, cell.X); x++)
            cable.Add(new Vector2i(x, cell.Y));
    }

    private static void FinishHull(Layout layout, RepairStationPrototype config)
    {
        foreach (var cell in layout.Walls.Keys.ToArray())
        {
            var north = !layout.Tiles.ContainsKey(cell + new Vector2i(0, 1));
            var east = !layout.Tiles.ContainsKey(cell + new Vector2i(1, 0));
            var south = !layout.Tiles.ContainsKey(cell + new Vector2i(0, -1));
            var west = !layout.Tiles.ContainsKey(cell + new Vector2i(-1, 0));
            if (!north && !east && !south && !west)
                continue;
            layout.Walls[cell] = config.ReinforcedWall;
            if ((north && south) || (east && west) || layout.MountedWalls.Contains(cell))
                continue;
            int? angle = north && west ? 0 : north && east ? 270 : south && east ? 180 : south && west ? 90 : null;
            if (angle is not { } rotation)
                continue;
            layout.Walls[cell] = config.ExteriorCorner;
            layout.WallRotations[cell] = Angle.FromDegrees(rotation);
        }
    }

    private static int[] PartitionWidths(RepairStationPrototype config, int count, int length, Random random)
    {
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            var remaining = count - i - 1;
            var min = Math.Max(config.MinRoomWidth, length - remaining * config.MaxRoomWidth);
            var max = Math.Min(config.MaxRoomWidth, length - remaining * config.MinRoomWidth);
            result[i] = random.Next(min, max + 1);
            length -= result[i];
        }
        return result;
    }

    private void Furnish(Layout layout, RoomArea area, RepairStationFurnishing group, Random random)
    {
        var slots = new List<(Vector2i Cell, Vector2i Seat, Angle Facing)>();
        var region = area.Mirrored ? group.Area switch
        {
            RepairStationFurnishingArea.Left => RepairStationFurnishingArea.Right,
            RepairStationFurnishingArea.Right => RepairStationFurnishingArea.Left,
            _ => group.Area,
        } : group.Area;
        if (region is RepairStationFurnishingArea.Left or RepairStationFurnishingArea.Right)
        {
            var left = region == RepairStationFurnishingArea.Left;
            var x = left ? area.Left + 1 + group.Inset : area.Right - 1 - group.Inset;
            for (var v = 2; v < area.Depth - 1; v += group.Spacing)
                slots.Add((area.Cell(x, v), area.Cell(x + (left ? 1 : -1), v), Angle.FromDegrees(left ? 270 : 90)));
        }
        else if (region is RepairStationFurnishingArea.Back or RepairStationFurnishingArea.Front)
        {
            var back = region == RepairStationFurnishingArea.Back;
            for (var x = area.Left + 1; x < area.Right; x += group.Spacing)
                slots.Add((area.Cell(x, back ? area.Depth - 1 : 2), area.Cell(x, back ? area.Depth - 2 : 3),
                    Angle.FromDegrees((area.Side > 0) == back ? 180 : 0)));
        }
        else if (area.Right - area.Left >= 8)
        {
            var x = area.Center + (area.Mirrored ? -2 : 2);
            for (var v = 3; v < area.Depth - 2; v += group.Spacing)
                slots.Add((area.Cell(x, v), area.Cell(x + (area.Mirrored ? 1 : -1), v),
                    Angle.FromDegrees(area.Mirrored ? 270 : 90)));
        }
        slots.RemoveAll(slot => !CanFurnish(layout, slot.Cell) || group.Seat != null && !CanFurnish(layout, slot.Seat));
        var count = random.Next(group.MinCount, group.MaxCount + 1);
        if (group.Continuous && slots.Count > 0)
        {
            var runs = new List<List<(Vector2i Cell, Vector2i Seat, Angle Facing)>>();
            foreach (var slot in slots)
            {
                if (runs.Count == 0 || Math.Abs(runs[^1][^1].Cell.X - slot.Cell.X) +
                    Math.Abs(runs[^1][^1].Cell.Y - slot.Cell.Y) > group.Spacing)
                    runs.Add(new List<(Vector2i Cell, Vector2i Seat, Angle Facing)>());
                runs[^1].Add(slot);
            }
            var available = Math.Min(count, runs.Max(run => run.Count));
            var choices = runs.Where(run => run.Count >= available).ToList();
            var selectedRun = choices[random.Next(choices.Count)];
            slots = selectedRun.Skip(random.Next(selectedRun.Count - available + 1)).Take(available).ToList();
        }
        else
            Shuffle(slots, random);
        var prototypes = group.Prototypes.ToList();
        Shuffle(prototypes, random);
        var placed = 0;
        foreach (var slot in slots)
        {
            if (count <= 0 || group.Unique && placed >= prototypes.Count)
                break;
            if (!CanFurnish(layout, slot.Cell) || group.Seat != null && !CanFurnish(layout, slot.Seat))
                continue;
            var prototype = prototypes[placed % prototypes.Count];
            var rotation = region == RepairStationFurnishingArea.Left ? 90 :
                region == RepairStationFurnishingArea.Right ? 270 :
                region == RepairStationFurnishingArea.Island ? area.Mirrored ? 90 : 270 :
                region == RepairStationFurnishingArea.Front ? area.Side > 0 ? 180 : 0 : area.Side > 0 ? 0 : 180;
            TryPlaceFurniture(layout, new Placement(prototype, slot.Cell, Angle.FromDegrees(rotation)));
            if (group.Seat is { } seat)
                TryPlaceFurniture(layout, new Placement(seat, slot.Seat, slot.Facing, Anchor: true));
            if (group.Tile is { } tile)
            {
                layout.Tiles[slot.Cell] = tile;
                if (group.Seat != null)
                    layout.Tiles[slot.Seat] = tile;
            }
            if (group.Props.Count > 0)
            {
                var props = group.Props.ToList();
                Shuffle(props, random);
                var propCount = random.Next(1, Math.Min(props.Count, 2) + 1);
                for (var i = 0; i < propCount; i++)
                {
                    var offset = new Vector2(i == 0 ? -0.18f : 0.18f, (float) random.NextDouble() * 0.2f);
                    layout.Entities.Add(new Placement(props[i], slot.Cell,
                        Angle.FromDegrees(random.Next(-20, 21)), Offset: offset));
                }
            }
            count--;
            placed++;
        }
        if (placed > 0 && group.Marking is { } marking)
        {
            var cell = slots.First(slot => layout.Occupied.Contains(slot.Cell)).Cell;
            Mark(layout, marking, cell, null, Angle.Zero);
        }
    }

    private static bool CanFurnish(Layout layout, Vector2i cell) => layout.Tiles.ContainsKey(cell) &&
        !layout.Walls.ContainsKey(cell) && !layout.Walkways.Contains(cell) && !layout.Occupied.Contains(cell);

    private static bool TryPlaceFurniture(Layout layout, Placement placement)
    {
        if (!CanFurnish(layout, placement.Cell))
            return false;
        layout.Occupied.Add(placement.Cell);
        layout.Entities.Add(placement);
        return true;
    }

    private static void MountLight(Layout layout, EntProtoId prototype, Vector2i floor, Vector2i wall)
    {
        if (!layout.Tiles.ContainsKey(floor) || layout.Walls.ContainsKey(floor) || !layout.Walls.ContainsKey(wall) ||
            !layout.MountedWalls.Add(wall))
            return;
        var direction = wall - floor;
        var angle = direction.Y > 0 ? 0 : direction.Y < 0 ? 180 : direction.X > 0 ? 270 : 90;
        layout.Entities.Add(new Placement(prototype, floor, Angle.FromDegrees(angle)));
        layout.LowVoltage.Add(floor);
    }

    private static void MountPanel(Layout layout, EntProtoId prototype, Vector2i wall, Angle rotation, LocId? name = null)
    {
        if (!layout.Walls.ContainsKey(wall) || !layout.MountedWalls.Add(wall))
            return;
        layout.Entities.Add(new Placement(prototype, wall, rotation, name, Anchor: true));
        layout.ProtectedCells.Add(wall);
        layout.LowVoltage.Add(wall);
    }

    private static void BorderRoom(Layout layout, RepairStationPrototype config, RepairStationRoomPrototype room, RoomArea area)
    {
        var style = new BorderStyle(room.BorderDecals.Count > 0 ? room.BorderDecals : config.BorderDecals,
            room.CornerDecals.Count > 0 ? room.CornerDecals : config.CornerDecals,
            room.InnerCornerDecals.Count > 0 ? room.InnerCornerDecals : config.InnerCornerDecals,
            room.BorderColor, Angle.Zero, room.FeatureTile);
        for (var x = area.Left + 1; x < area.Right; x++)
        for (var v = 1; v < area.Depth; v++)
        {
            var cell = area.Cell(x, v);
            layout.Borders[cell] = style;
        }
    }

    private static BorderStyle CorridorBorder(RepairStationPrototype config) =>
        new(config.BorderDecals, config.CornerDecals, config.InnerCornerDecals, config.CorridorColor, Angle.Zero);

    private static void FrameFloors(Layout layout)
    {
        foreach (var (cell, style) in layout.Borders.OrderBy(p => p.Key.X).ThenBy(p => p.Key.Y))
        {
            if (layout.Walls.ContainsKey(cell))
                continue;
            bool Edge(Direction direction)
            {
                var neighbor = cell + RotateCell(direction.ToIntVec(), style.Rotation);
                return layout.Walls.ContainsKey(neighbor) || style.FeatureTile is { } tile &&
                    layout.Tiles[cell] == tile && layout.Tiles.GetValueOrDefault(neighbor) != tile;
            }
            var covered = new HashSet<Direction>();
            foreach (var (corner, first, second) in new[]
            {
                (Direction.NorthEast, Direction.North, Direction.East),
                (Direction.SouthEast, Direction.South, Direction.East),
                (Direction.SouthWest, Direction.South, Direction.West),
                (Direction.NorthWest, Direction.North, Direction.West),
            })
            {
                if (Edge(first) && Edge(second) && style.Corners.TryGetValue(corner, out var outer))
                {
                    Mark(layout, outer, cell, style.Color, style.Rotation);
                    covered.Add(first);
                    covered.Add(second);
                }
                else if (!Edge(first) && !Edge(second) && Edge(corner) && style.InnerCorners.TryGetValue(corner, out var inner))
                    Mark(layout, inner, cell, style.Color, style.Rotation);
            }
            foreach (var (direction, decal) in style.Lines)
            {
                if (!covered.Contains(direction) && Edge(direction))
                    Mark(layout, decal, cell, style.Color, style.Rotation);
            }
        }
    }

    private static void Mark(Layout layout, ProtoId<DecalPrototype> prototype, Vector2i cell, Color? color, Angle angle) =>
        layout.Decals.Add(new FloorMarking(prototype, cell, color, angle));

    private static void Shuffle<T>(List<T> pool, Random random)
    {
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var other = random.Next(i + 1);
            (pool[i], pool[other]) = (pool[other], pool[i]);
        }
    }

    private static void DressCorridor(Layout layout, RepairStationPrototype config, int length, Random random)
    {
        for (var x = -4; x < length + 5; x++)
        {
            layout.Walkways.Add(new Vector2i(x, 0));
            foreach (var side in new[] { -1, 1 })
            {
                var floor = new Vector2i(x, side);
                layout.Borders[floor] = CorridorBorder(config);
                if (layout.DoorColumns.Contains(x))
                    continue;
                if (x % 5 == 2)
                    MountLight(layout, config.Light, floor, new Vector2i(x, 2 * side));
                if (x > 1 && x < length - 1 && x % 5 == 0 && config.CorridorDecoration.Count > 0 &&
                    !layout.DoorColumns.Any(door => Math.Abs(door - x) <= 1))
                    TryPlaceFurniture(layout, new Placement(Pick(config.CorridorDecoration, random), floor, Angle.FromDegrees(side > 0 ? 0 : 180)));
            }
        }
    }

    private static EntProtoId Pick(List<EntProtoId> pool, Random random) => pool[random.Next(pool.Count)];

    private static void Rectangle(Layout layout, int left, int right, int bottom, int top, string tile, EntProtoId wall)
    {
        for (var x = left; x <= right; x++)
        for (var y = bottom; y <= top; y++)
        {
            var cell = new Vector2i(x, y);
            layout.Tiles[cell] = tile;
            if (x == left || x == right || y == bottom || y == top)
                layout.Walls[cell] = wall;
        }
    }

    private void ValidateLayout(Layout layout, RepairStationPrototype config)
    {
        var catalog = RepairValueCatalog.Build(_prototypes);
        if (catalog.Errors.Count != 0)
            throw new InvalidOperationException(string.Join("\n", catalog.Errors));
        foreach (var tile in layout.Tiles.Values.Distinct())
            _ = _tiles[tile];
        var prototypes = layout.Entities.Select(p => p.Prototype).Concat(layout.Walls.Values)
            .Append(config.LowVoltageCable).Append(config.MediumVoltageCable).Append(config.HighVoltageCable).Distinct();
        foreach (var prototype in prototypes)
        {
            if (!_prototypes.TryIndex(prototype, out var entity) || entity.Abstract ||
                RepairValueCatalog.IsCandidate(entity) && !catalog.IsExcluded(prototype.Id) && !catalog.TryResolve(prototype.Id, out _))
                throw new InvalidOperationException($"Invalid or unclassified procedural station entity: {prototype}.");
        }
        foreach (var placement in layout.Entities)
        {
            if (!layout.Tiles.ContainsKey(placement.Cell))
                throw new InvalidOperationException($"Procedural station placement outside its grid: {placement}.");
        }
        foreach (var cell in layout.LowVoltage.Concat(layout.MediumVoltage).Concat(layout.HighVoltage))
        {
            if (!layout.Tiles.ContainsKey(cell))
                throw new InvalidOperationException($"Procedural station cable outside its grid: {cell}.");
        }
        foreach (var decal in layout.Decals)
        {
            if (!_prototypes.HasIndex(decal.Prototype) || !layout.Tiles.ContainsKey(decal.Cell) || layout.Walls.ContainsKey(decal.Cell))
                throw new InvalidOperationException($"Invalid procedural station decal: {decal}.");
        }
        if (!_prototypes.Index(config.Dock).Components.ContainsKey("Docking"))
            throw new InvalidOperationException($"Procedural station dock {config.Dock} lacks Docking.");
        var sealedCells = layout.Walls.Keys.Concat(layout.Entities
            .Where(p => _prototypes.Index(p.Prototype).Components.ContainsKey("Airtight")).Select(p => p.Cell)).ToHashSet();
        foreach (var cell in layout.Tiles.Keys)
        {
            if (!sealedCells.Contains(cell) && new[] { new Vector2i(0, 1), new Vector2i(1, 0), new Vector2i(0, -1), new Vector2i(-1, 0) }
                .Any(direction => !layout.Tiles.ContainsKey(cell + direction)))
                throw new InvalidOperationException($"Procedural station has an unsealed hull at {cell}.");
        }
        var blocked = layout.Walls.Keys.Concat(layout.Entities.Where(p =>
        {
            var prototype = _prototypes.Index(p.Prototype);
            return prototype.Components.ContainsKey("Airtight") && !prototype.Components.ContainsKey("Door");
        }).Select(p => p.Cell)).ToHashSet();
        var reachable = new HashSet<Vector2i> { Vector2i.Zero };
        var pending = new Queue<Vector2i>();
        pending.Enqueue(Vector2i.Zero);
        while (pending.TryDequeue(out var cell))
        {
            foreach (var direction in new[] { new Vector2i(0, 1), new Vector2i(1, 0), new Vector2i(0, -1), new Vector2i(-1, 0) })
            {
                var neighbor = cell + direction;
                if (layout.Tiles.ContainsKey(neighbor) && !blocked.Contains(neighbor) && reachable.Add(neighbor))
                    pending.Enqueue(neighbor);
            }
        }
        foreach (var entry in layout.RoomEntries.Concat(layout.Entities.Where(p => p.Prototype == config.Dock).Select(p => p.Cell)))
        {
            if (!reachable.Contains(entry))
                throw new InvalidOperationException($"Procedural station has an inaccessible room or dock at {entry}.");
        }
    }
}
