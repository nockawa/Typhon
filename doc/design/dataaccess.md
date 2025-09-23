# Data access pattern 

## Page access

Typhon was designed with multi-cores, concurrent execution of the code, how to access the data is a key concept to understand in order to "do things right".

In this engine, the core data is the **Page** and there's only one method to request access to a given one :

[bool PagedMMF.**RequestPage**(int filePageIndex, bool exclusive, out PageAccessor result, long timeout, CancellationToken cancellationToken)](<xref:Typhon.Engine.PagedMMF.RequestPage(System.Int32,System.Boolean,Typhon.Engine.PageAccessor@,System.Int64,System.Threading.CancellationToken)>)

Accessing a resource, if successfully acquired, will almost always return a `accessor`: an object which lifetime is matching the one of the access the user wants, dedicated to the requested resource and exposing the APIs to access the data.

The method returns:
- `true` if the resource access was acquired, the `accessor` instance is then valid.
- `false` if the user specified conditions that can't be met to acquire the access.

There are many rules that dictate if the resource access can be acquired or not, most of the time they make sense:
- You can get a shared or exclusive access if the resource is 'idle'.
- Many can access a given resource as long as it's in share mode (`exclusive=false`)
- You can't get an exclusive access if there's already one acquired by 'someone else' (being most of the time another thread).
- Re-entrance may be more subtle, refer to the API documentation.

What has to be understood is:
- Most of the time, the access request won't be blocking and will be very fast. But...
- ... we have to account for the rare cases when the resource access can't be acquired (e.g. long blocking execution on another thread, resource starvation preventing to allocate the resource before requesting access, etc.). 

### Understanding shared/exclusive access

There are many levels of granularity when it's about data access. The lowest level one will be the `Page`, middle could be a `Chunk` inside a [ChunkBasedSegment](<xref:Typhon.Engine.ChunkBasedSegment>), high level being a `Component Data` or an `Index`. 

Getting shared or exclusive access at a given level may imply different rules than another one.

⚠️ **Thinking "shared is read-only, exclusive is read-write" is wrong !** ⚠️

It all depends on own things are designed and implemented.

💡 Things to consider (non-exhaustive list of examples/rules): 
- When modifying the content of a component, only a shared access can be requested on the page that hosts its data. 
- We can modify the content of multiple chunks that are hosted in the same page as long as we don't change the **structure** of how the data is stored. 
- There might be multiple levels of access control, at different granularities.
- A general rule (but not always true) is:
  - Content can be read/modified in shared access.
  - Structure can be read in shared access.
  - Structure can be modified in exclusive access.

