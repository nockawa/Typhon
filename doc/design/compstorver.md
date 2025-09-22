# Component Storage and Versioning

## Entity/Component relation
So far, it's very simple, an entity is created by giving a list of components. What happens underneath is a unique ID (across the whole database) will be allocated and each component will be created and stored in its own table, identified by this id (acting as the primary key), with an index maintained.

Later on the user will be able to read all or part of these components by giving the Entity ID.

## Storing components - high level view
There are two main storage component involved to store a component:
1. Component segment: a [Chunk Based Segment](datalayers.md#chunkbasedsegment) used to store each revision of a component.
2. Row Version segment: a Chunk Based segment that list all the revision of a given component.

### Component Segment

We compute for each component type:
1. The size of the components data (which is the size of the C# `struct` that defines or wraps it).
2. The size taken to store the ElementID of each secondary index that allow multiples value (ElementID being the index of the element in the buffer storing all elements of the same key).

### Row Version Segment

Typhon allows full isolation through Transactions. Each CRUD operation is associated to a transaction and we use the time of creation of this transaction as a reference point to interact with components.
Which means each mutation of a component's data (including its deletion) leads to the creation of a new revision (following the principle of [MVCC](https://en.wikipedia.org/wiki/Multiversion_concurrency_control)).

A revision is kept as long as there's a transaction that can access it. 

The Row Version Segment stores chunks that each have the following structure
 - A header: [RowVersionStorageHeader](<xref:Typhon.Engine.RowVersionStorageHeader>)
 - Multiple (currently 8) entries of [RowVersionStorageElement](<xref:Typhon.Engine.RowVersionStorageElement>).

To store the multiple versions of a given component, there is one or many chunks, organised as a chained list, that store all the revisions for that component.

The first chunk always exists and is the entry point, it header is used to operate the chain. Subsequent chunks, if any, have their header ignored, only the elements are used.

As we store new revisions of a components and progressively destroying old ones, the storage is acting as a circular buffer. The first item is given by the [FirstItemRevision](<xref:Typhon.Engine.RowVersionStorageHeader.FirstItemRevision>) field.

Chunks are added/removed to the chain when needed.

## Versioning components

Any access or mutation is made from a Transaction where the changes are isolated from the rest of the database until they are committed.

The transaction also stores useful information about each component involved.

### Creation

A component is created from a transaction, the tick of the component is the one retrieved at creation time (so it's different from the transaction's Tick).

A Row Version chunk is created as well as a Component chunk for the first revision. A ComponentInfo.RowInfo is created to cache useful data for this revision. The revision is flagged as 'isolated' to be excluded from queries (even though there's no way to retrieve it).
There's no index yet on the PK or indexed fields, the RowInfo stores the equivalent in the RowInfo.

### Read

Reading a component will create and setup a corresponding RowInfo.

### Update

An update can be made after a read or as a first operation on a Component, in any cases a RowInfo is built if not already.

Then a new revision is created and stores the updated data, subsequent updates can be made, which will made on the same revision, overwritting the previous change.

### Delete

Same as update but the RowChunkId field of RowVersionElement is 0, indicating a new revision that is a delete. 


