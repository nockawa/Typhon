using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Workbench.Fixtures;

// Archetype IDs 800+ picked to avoid colliding with unit-test archetypes (200-series) or engine-internal
// ranges. The Workbench fixture project owns this ID block.

[Archetype(800)]
public class CompAArch : Archetype<CompAArch>
{
    public static readonly Comp<CompA> A = Register<CompA>();
}

[Archetype(801)]
public class CompABArch : Archetype<CompABArch>
{
    public static readonly Comp<CompA> A = Register<CompA>();
    public static readonly Comp<CompB> B = Register<CompB>();
}

[Archetype(802)]
public class CompABCArch : Archetype<CompABCArch>
{
    public static readonly Comp<CompA> A = Register<CompA>();
    public static readonly Comp<CompB> B = Register<CompB>();
    public static readonly Comp<CompC> C = Register<CompC>();
}

[Archetype(803)]
public class CompDArch : Archetype<CompDArch>
{
    public static readonly Comp<CompD> D = Register<CompD>();
}

[Archetype(804)]
public class GuildArch : Archetype<GuildArch>
{
    public static readonly Comp<CompGuild> Guild = Register<CompGuild>();
}

[Archetype(805)]
public class PlayerArch : Archetype<PlayerArch>
{
    public static readonly Comp<CompPlayer> Player = Register<CompPlayer>();
}
