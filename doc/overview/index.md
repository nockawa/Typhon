# Overview: 🐍 a High-Performance Component-Based Database for Game Development

Typhon Engine is a specialized database engine that combines the performance of an in-memory database with the reliability of persistent storage. It's designed specifically for game development scenarios that require high-throughput data operations with transactional safety.

## Core Features

### Component-Based Architecture
Typhon uses a component-based architecture where data is organized into typed components rather than traditional tables. This approach aligns perfectly with Entity Component System ([ECS](https://en.wikipedia.org/wiki/Entity_component_system)) patterns commonly used in game development.

```csharp
[Component(SchemaName)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerPosition
{
    public const string SchemaName = "Game.Components.PlayerPosition";
    public float X;
    public float Y;
    public float Z;
}
```

### Powerful Transaction System
All operations are performed through transactions, providing ACID guarantees even in high-throughput scenarios:

- **Create and read components** within the same transaction or across different ones
- **Update and delete** with transactional safety
- **Rollback support** to handle errors or rejected changes
- **Transaction isolation** to prevent data corruption

### Concurrency Control Options
Choose the concurrency model that fits your needs:

- **Exclusive mode** for critical operations that need to lock resources
- **Optimistic concurrency** for higher throughput when conflicts are rare

### High-Performance Memory Management
The engine is built with performance as a top priority:

- **Memory-mapped files** for efficient data persistence
- **Chunked storage system** for optimal memory usage
- **Advanced page caching** to minimize disk I/O
- **Optimized memory access patterns** using unsafe code where needed

### Indexing Support
Automatically create and maintain indexes for faster lookups:

```csharp
[Component(SchemaName)]
public struct Enemy
{
    [Index(AllowMultiple = true)]  // Support multiple enemies at same position
    public float PositionX;
    
    [Index]  // Only one enemy per ID
    public int EnemyId;
}
```

### Memory-Efficient Storage
The storage layer is designed to minimize memory overhead:

- **Fixed-size chunks** for predictable memory allocation
- **Efficient bitmap tracking** of allocated/free chunks
- **Pooled accessors** to reduce GC pressure

## Use Cases

Typhon Engine is particularly well-suited for:

- **Game state persistence** with minimal performance impact
- **High-frequency data updates** common in simulation games
- **Complex game worlds** with many interrelated entities
- **Networked games** requiring transaction safety

The database engine provides a robust foundation for building complex, data-driven game systems while maintaining the performance needed for smooth gameplay.

## Technical Foundation

Built on modern C# with careful optimization, Typhon Engine takes advantage of:

- Low-level memory operations for performance
- Thread safety for multi-core utilization
- Modern .NET features for developer ergonomics
- Dependency injection for flexible configuration

This combination delivers both the raw performance needed for games and the developer-friendly API needed for rapid development.

<!--
# MMO DB Runtime design document

## Overview

Spatially organized real-time database, stored and processed in a distributed system.

Database is made of DBObject (Database Object), each DBObject has a unique PK (long) and reference a set of DBComponents (that can be referenced by zero to many DBOject).
Each DBComponent defines a list of data field, static (defined at declaration level) or not (storing data for each instance).

Data type are:
 - `boolean`
 - `byte`, `short`, `int`, `long` (signed integers), `ubyte`, `ushort`, `uint`, `ulong` (unsigned version)
 - `float`, `double`: float precision numbers
 - `char`: 16 bits unicode character
 - `string`: null terminated UTF8 string of variable size, `string64` : 64 bytes UTF8 string, `string1024` : 1024 bytes UTF8 string.
 - `point(2/3)(d/f)`, `quaternion`, all computation are done with 3d double internally but data can be stored on a less precise type (e.g. `point2f`)
 - `array<T>` : an array of T values.

##### DBObject component definition
```
 BEGIN DECL COMPONENT DBCObject
	TypeName : static string
	ID       : long
 END DECL
```

##### DBSpatial3d component definition
```
 BEGIN DECL COMPONENT DBCSpatial3d
	Parent	 : long
	Children : array<long>
	Position : point3d
	BSphere  : double
 END DECL
```

#### Example of DBObject type

##### Player data definition

###### Component

```
BEGIN DECL COMPONENT DBCPlayer

	Online	: boolean
	Name	: string

END DECL
```

###### Object

```
BEGIN DECL OBJECT Player

	DBCObject("Player")
	DBCSpatial3d
	DBCPlayer
	DBCContainer(128)               ; Player's inventory

END DECL
```

##### Inventory types

###### Components

```
BEGIN DECL COMPONENT DBCItem

	QuantityType : static byte      ; 0 = unit, 1 = liter, 2 = cube meter
	Name         : string64
	StoredIn     : int              ; PK of the container storing the item
	Quantity     : double

END DECL
```

```
BEGIN DECL COMPONENT DBCContainer

	Capacity     : static double    ; capacity in liters
	Name         : string64
	Owner        : int              ; PK of the player that owns the container

END DECL
```

###### Objects

```
BEGIN DECL OBJECT Player

	DBCObject("Player")
	DBCSpatial3d
	DBCPlayer

END DECL
```



DBObj are instanciated through the Database then user can execute queries or create and update views.

-->