using Typhon.Engine;
using Typhon.Schema.Definition;

namespace AntHill.ECS;

[Archetype(100)]
partial class Ant : Archetype<Ant>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Movement> Movement = Register<Movement>();
}
