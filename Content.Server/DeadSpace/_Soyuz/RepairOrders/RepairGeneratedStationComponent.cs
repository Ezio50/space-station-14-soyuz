// Мёртвый Космос, Союз-1, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-soyuz/master/LICENSES/LICENSE.TXT

using Content.Shared.DeadSpace._Soyuz.RepairOrders;
using Robust.Shared.Prototypes;

namespace Content.Server.DeadSpace._Soyuz.RepairOrders;

[RegisterComponent]
public sealed partial class RepairGeneratedStationComponent : Component
{
    [DataField] public ProtoId<RepairStationPrototype> Configuration;
    [DataField] public int Seed;
    [DataField] public HashSet<Vector2i> ProtectedCells = new();
}
