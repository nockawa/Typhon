# Introduction to Typhon design & core concepts

## General concepts

Refer to the index in the left pane of this documentation window to access other pages.

### Basic data layout

For many types of data their storage/access work this way:
- An array or a list of pages that stores all the elements.
- A bitmap which specifies for each element if it's free (`0`) or allocated (`1`).
- Each element is identified by its index into its owning structure.
  - If it's owned by a single array, the access is very simple.
  - If it's owned by a list of pages, all the pages have the same size, so finding the element is a [DivRem](https://learn.microsoft.com/fr-fr/dotnet/api/system.math.divrem) operation to identify which page and the index in this page.

### Waiting and burning

Or more accurately "burning, then waiting". Data is often shared among many threads, the lapse of time we need to acquire exclusive access on it must be very short (nanosecond order of magnitude).

Typhon tries to deal with concurrency with the following mechanisms, in order of preference:
1. Using the [Interlocked](https://learn.microsoft.com/en-us/dotnet/api/system.threading.interlocked) class, when possible, for lock-free (or [non-blocking](https://en.wikipedia.org/wiki/Non-blocking_algorithm)) programming.
2. Relying on home-made types such as [AccessControl](<xref:Typhon.Engine.AccessControl>) or [AdaptiveWaiter](<xref:Typhon.Engine.AdaptiveWaiter>) that relies on the `Interlocked` type to :
   - Acquire the control on a resource, or... 
   - ...wait to get it, first by burning some CPU cycles, then yielding the thread's execution, and eventually sleep/async wait.
   - Release the control when the resource access is no longer needed.
3. Using the C# `lock` keyword/pattern.

Nothing is free and everything has pros/cons, here the key thing to understand is **what latency are we willing to deal with**.

A C# lock (the [monitor](https://learn.microsoft.com/en-us/dotnet/api/system.threading.monitor) class) is dealing with OS level resources for the synchronization, it's great, but the latency is usually in the order of the millisecond (which is at least thousands time longer than what we need).

So if we attempt to acquire a resource but can't because it's currently being used by another thread, if we don't want to "give up our execution to another thread for at least 1ms", we need to rely on something else than a Thread level operation. The alternative is "looping doing nothing", aka "burning the CPU".

By design, Typhon doesn't acquire exclusive access to data for a long time (here long is `> 1ms`).

If the user needs to acquire for an undetermined time, there's a `TryEnter()` variant method with a timeout that allows not to block, if possible.

All in all, there's no miracle: very low latency and efficient use of the CPU don't match well and one has to find the pros/cons that suit the most its usage.

