using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// Randomly-rolled item modifier (prefix or suffix).
/// AllowMultiple: a Rare item typically has 4-6 affixes, a Legendary may have special ones.
/// </summary>
[Component("ARPG.ItemAffixes", 1, true)]
[StructLayout(LayoutKind.Sequential)]
public struct ItemAffixes
{
    [Field] /*[Index(AllowMultiple = true)]*/ public int AffixTypeId;

    [Field] public int MinValue;
    [Field] public int MaxValue;
    [Field] public int RolledValue;
}
