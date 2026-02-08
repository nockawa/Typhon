using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// Temporary buffs and debuffs on an entity.
/// AllowMultiple: an entity can have many concurrent effects (poison + burn + haste + ...).
/// </summary>
[Component("ARPG.StatusEffects", 1, true)]
[StructLayout(LayoutKind.Sequential)]
public struct StatusEffects
{
    [Field] [Index(AllowMultiple = true)] public int EffectTypeId;

    [Field] public long TargetEntityId;
    [Field] public long SourceEntityId;

    [Field] public int StackCount;
    [Field] public long ExpirationTick;

    // Effect parameters
    [Field] public int DamagePerTick;
    [Field] public int StatModifier;
    [Field] public int TickIntervalMs;
}
