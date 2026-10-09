// Мёртвый Космос, Союз-1, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-soyuz/master/LICENSES/LICENSE.TXT

using System.IO;
using System.Linq;
using Content.Shared.Decals;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.DeadSpace._Soyuz.RepairOrders;

[Prototype]
public sealed partial class RepairStationPrototype : IPrototype, ISerializationHooks
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int MinColumns = 3;
    [DataField] public int MaxColumns = 4;
    [DataField] public int MinBranchColumns;
    [DataField] public int MaxBranchColumns;
    [DataField] public int MinRoomWidth = 6;
    [DataField] public int MaxRoomWidth = 8;
    [DataField] public int MinRoomDepth = 6;
    [DataField] public int MaxRoomDepth = 9;
    [DataField] public string CorridorTile = "FloorSteel";
    [DataField] public string CorridorTrim = "FloorSteelCheckerDark";
    [DataField] public EntProtoId Wall = "WallSolid";
    [DataField] public EntProtoId ReinforcedWall = "WallReinforced";
    [DataField] public EntProtoId ExteriorCorner = "WallMiningDiagonal";
    [DataField] public EntProtoId ExteriorWindow = "MiningWindow";
    [DataField] public EntProtoId Window = "ReinforcedWindow";
    [DataField] public EntProtoId Grille = "Grille";
    [DataField] public EntProtoId Dock = "AirlockGlassShuttle";
    [DataField] public EntProtoId Light = "PoweredlightSoyuz";
    [DataField] public EntProtoId Apc = "APCBasic";
    [DataField] public EntProtoId Smes = "SMESBasic";
    [DataField] public EntProtoId Substation = "SubstationBasic";
    [DataField] public EntProtoId LowVoltageCable = "CableApcExtension";
    [DataField] public EntProtoId MediumVoltageCable = "CableMV";
    [DataField] public EntProtoId HighVoltageCable = "CableHV";
    [DataField] public Color CorridorColor = Color.FromHex("#91A3B3");
    [DataField] public Dictionary<Direction, ProtoId<DecalPrototype>> BorderDecals = new();
    [DataField] public Dictionary<Direction, ProtoId<DecalPrototype>> CornerDecals = new();
    [DataField] public Dictionary<Direction, ProtoId<DecalPrototype>> InnerCornerDecals = new();
    [DataField] public ProtoId<DecalPrototype> ArrowDecal = "ArrowsGreyscale";
    [DataField] public ProtoId<DecalPrototype> DockDecal = "StandClear";
    [DataField] public List<EntProtoId> CorridorDecoration = new();
    [DataField] public ProtoId<RepairStationRoomPrototype>? CommonArea;
    [DataField] public List<ProtoId<RepairStationRoomPrototype>> CommonAreas = new();
    [DataField] public int CommonAreaWidth = 8;
    [DataField] public int CommonAreaDepth = 6;
    [DataField] public List<ProtoId<RepairStationRoomPrototype>> RequiredRooms = new();
    [DataField(required: true)] public List<ProtoId<RepairStationRoomPrototype>> Rooms = new();

    void ISerializationHooks.AfterDeserialization()
    {
        if (MinColumns < 2 || MaxColumns < MinColumns || MaxColumns > 6 ||
            MinBranchColumns < 0 || MaxBranchColumns < MinBranchColumns || MaxBranchColumns > 3 ||
            MaxBranchColumns > 0 && CommonArea == null && CommonAreas.Count == 0 ||
            MinRoomWidth < 6 || MaxRoomWidth < MinRoomWidth || MaxRoomWidth > 10 ||
            MinRoomDepth < 6 || MaxRoomDepth < MinRoomDepth || MaxRoomDepth > 12 || Rooms.Count == 0 ||
            CommonAreaWidth is < 6 or > 10 || CommonAreaDepth is < 6 or > 12 ||
            RequiredRooms.Count > MinColumns * 2 || RequiredRooms.Distinct().Count() != RequiredRooms.Count)
            throw new InvalidDataException($"Invalid procedural repair station dimensions or rooms: {ID}.");
    }
}

[Prototype]
public sealed partial class RepairStationRoomPrototype : IPrototype, ISerializationHooks
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId Name;
    [DataField] public string Section = string.Empty;
    [DataField] public string Tile = "FloorSteel";
    [DataField] public string Trim = "FloorSteelCheckerLight";
    [DataField] public EntProtoId Door = "AirlockGlass";
    [DataField] public EntProtoId Light = "PoweredlightSoyuz";
    [DataField] public List<EntProtoId> Workstations = new();
    [DataField] public List<EntProtoId> Storage = new();
    [DataField] public List<EntProtoId> Furniture = new();
    [DataField] public List<EntProtoId> Decoration = new();
    [DataField] public Color AccentColor = Color.FromHex("#91A3B3");
    [DataField] public EntProtoId? Sign;
    [DataField] public List<EntProtoId> Posters = new();
    [DataField] public List<EntProtoId> WallFixtures = new();
    [DataField] public ProtoId<DecalPrototype> EntranceDecal = "ArrowsGreyscale";
    [DataField] public string? FeatureTile;
    [DataField] public float PartitionChance;
    [DataField] public EntProtoId PartitionDoor = "Windoor";
    [DataField] public int WindowWidth = 3;
    [DataField] public int MinDepth = 6;
    [DataField] public int FrontWindows;
    [DataField] public Color BorderColor = Color.White;
    [DataField] public Dictionary<Direction, ProtoId<DecalPrototype>> BorderDecals = new();
    [DataField] public Dictionary<Direction, ProtoId<DecalPrototype>> CornerDecals = new();
    [DataField] public Dictionary<Direction, ProtoId<DecalPrototype>> InnerCornerDecals = new();
    [DataField] public List<RepairStationRoomModule> Modules = new();
    [DataField] public List<RepairStationFurnishing> Furnishings = new();

    void ISerializationHooks.AfterDeserialization()
    {
        if (Furnishings.Count == 0 && (Workstations.Count == 0 || Storage.Count == 0 || Furniture.Count == 0))
            throw new InvalidDataException($"Procedural repair room {ID} needs workstations, storage and furniture.");
        if (!float.IsFinite(PartitionChance) || PartitionChance is < 0 or > 1)
            throw new InvalidDataException($"Invalid partition chance for repair room {ID}.");
        if (WindowWidth is < 0 or > 7 || WindowWidth > 0 && WindowWidth % 2 == 0 || MinDepth is < 6 or > 12 ||
            FrontWindows is < 0 or > 4 ||
            Modules.Count > 0 && PartitionChance > 0)
            throw new InvalidDataException($"Invalid windows or overlapping module partition for repair room {ID}.");
    }
}

public enum RepairStationModuleAnchor : byte { BackLeft, BackRight }

[DataDefinition]
public sealed partial class RepairStationRoomModule : ISerializationHooks
{
    [DataField] public RepairStationModuleAnchor Anchor;
    [DataField(required: true)] public Vector2i Size;
    [DataField] public string? Tile;
    [DataField] public ProtoId<DecalPrototype>? Marking;
    [DataField] public List<Vector2i> HighVoltageCables = new();
    [DataField(required: true)] public List<RepairStationModuleEntity> Entities = new();

    void ISerializationHooks.AfterDeserialization()
    {
        if (!Enum.IsDefined(Anchor) || Size.X is < 1 or > 3 || Size.Y is < 1 or > 3 || Entities.Count == 0 ||
            Entities.Select(e => e.Position).Concat(HighVoltageCables)
                .Any(p => p.X < 0 || p.X >= Size.X || p.Y < 0 || p.Y >= Size.Y))
            throw new InvalidDataException("Invalid procedural station room module.");
    }
}

[DataDefinition]
public sealed partial class RepairStationModuleEntity : ISerializationHooks
{
    [DataField(required: true)] public EntProtoId Prototype;
    [DataField(required: true)] public Vector2i Position;
    [DataField] public float Rotation;
    [DataField] public bool Wall;
    [DataField] public bool HighVoltageConnection;
    [DataField] public bool MediumVoltageConnection;

    void ISerializationHooks.AfterDeserialization()
    {
        if (!float.IsFinite(Rotation) || Rotation % 90 != 0)
            throw new InvalidDataException("Room module entities require a finite cardinal rotation.");
    }
}

public enum RepairStationFurnishingArea : byte { Left, Right, Back, Island, Front }

[DataDefinition]
public sealed partial class RepairStationFurnishing : ISerializationHooks
{
    [DataField(required: true)] public RepairStationFurnishingArea Area;
    [DataField(required: true)] public List<EntProtoId> Prototypes = new();
    [DataField] public EntProtoId? Seat;
    [DataField] public List<EntProtoId> Props = new();
    [DataField] public int MinCount = 1;
    [DataField] public int MaxCount = 4;
    [DataField] public int Spacing = 2;
    [DataField] public int Inset;
    [DataField] public bool Unique;
    [DataField] public bool Continuous;
    [DataField] public string? Tile;
    [DataField] public ProtoId<DecalPrototype>? Marking;

    void ISerializationHooks.AfterDeserialization()
    {
        if (!Enum.IsDefined(Area) || Prototypes.Count == 0 || MinCount < 1 || MaxCount < MinCount ||
            MaxCount > 8 || Spacing is < 1 or > 4 || Inset is < 0 or > 1)
            throw new InvalidDataException("Invalid procedural station furnishing group.");
    }
}
