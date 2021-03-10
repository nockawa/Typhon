# Memory Management

The whole project is written in C# / .net 5 and doesn't rely on native code at all. The .net 5 version has many feature to work with memory in a safe, unsafe, managed, unmanaged ways.
One key aspect of the project is to be able to run in real-time, meaning Video Gaming real-time: so a ver low-lantency, but this is a server side project, we are not shooting at 60 FPS here. :)

So in order to be fast and low-lantency we try to follow these rules:
 - Stay away from GC when it's possible, don't be anal about it, but be mindful of it. Which mean relying on `struct` data type when possible.
 - Staying away from `System.Array` when possible, prefer the new `MemoryPool`/`Memory<T>` paradigm.
 - The Database Engine relies on the Virtual Disk Manager (VDM) service to access the Disk Pages from memory. As it is a major memory footprint, we try to be efficient here: a single memory buffer is allocated for the whole cached size.
 - Extensive use of `Span<T>` related APIs for data processing, many code parts are relying on unsafe code, with care.
 - Another consideration is being thread-safe and multi-thread friendly, while there's a dedicated documentation for this, the implication on memory management is the following:
   - We try to stay away from linked-list and any structured memory management that would make our life hard regarding concurrency, and we need to be mindful of the low-latency as well.
   - One repeated pattern is to split a memory zone into uniform size chunk of data and to rely on a bitmap to indicate which chunk is free (0) or allocated (1). There are several implementations, for instance 
@"Typhon.Engine.BitmapL3" or @"Typhon.Engine.ConcurrentBitmapL3", these types using several levels of bitmap to accelerate the different operations. So we don't have to care about fragmentation, allocation is pretty fast/cheap, freeing is very fast. The downside is we have to work with fixed-size blocks and deal with the consequences: wasting memory, harder code that may have to iterate through meaning block to access simple data.