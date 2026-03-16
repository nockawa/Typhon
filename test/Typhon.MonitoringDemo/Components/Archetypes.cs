using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.MonitoringDemo;

// ============================================================================
// ECS Archetypes for Monitoring Demo
// ============================================================================
// Each archetype wraps a single component type, matching the old CRUD API's
// one-component-per-entity model. IDs start at 600 to avoid collision with
// Engine.Tests (200+) and other test projects.
// ============================================================================

// --- Factory Archetypes ---

[Archetype(600)]
class FactoryBuildingArch : Archetype<FactoryBuildingArch>
{
    public static readonly Comp<FactoryBuilding> Building = Register<FactoryBuilding>();
}

[Archetype(601)]
class ConveyorBeltArch : Archetype<ConveyorBeltArch>
{
    public static readonly Comp<ConveyorBelt> Belt = Register<ConveyorBelt>();
}

[Archetype(602)]
class ItemStackArch : Archetype<ItemStackArch>
{
    public static readonly Comp<ItemStack> Stack = Register<ItemStack>();
}

[Archetype(603)]
class RecipeArch : Archetype<RecipeArch>
{
    public static readonly Comp<Recipe> Recipe = Register<Recipe>();
}

[Archetype(604)]
class ResourceNodeArch : Archetype<ResourceNodeArch>
{
    public static readonly Comp<ResourceNode> Node = Register<ResourceNode>();
}

[Archetype(605)]
class PowerGridArch : Archetype<PowerGridArch>
{
    public static readonly Comp<PowerGrid> Grid = Register<PowerGrid>();
}

// --- RPG Archetypes ---

[Archetype(606)]
class CharacterArch : Archetype<CharacterArch>
{
    public static readonly Comp<Character> Character = Register<Character>();
}

[Archetype(607)]
class WorldPositionArch : Archetype<WorldPositionArch>
{
    public static readonly Comp<WorldPosition> Position = Register<WorldPosition>();
}

[Archetype(608)]
class CombatStatsArch : Archetype<CombatStatsArch>
{
    public static readonly Comp<CombatStats> Stats = Register<CombatStats>();
}

[Archetype(609)]
class SkillArch : Archetype<SkillArch>
{
    public static readonly Comp<Skill> Skill = Register<Skill>();
}

[Archetype(610)]
class QuestArch : Archetype<QuestArch>
{
    public static readonly Comp<Quest> Quest = Register<Quest>();
}

[Archetype(611)]
class InventoryArch : Archetype<InventoryArch>
{
    public static readonly Comp<Inventory> Item = Register<Inventory>();
}
