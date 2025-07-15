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




