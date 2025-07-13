// unset

using System.Threading.Tasks;

namespace Typhon.Engine;

public interface IInitializable
{
    void Initialize();
    bool IsInitialized { get; }
    bool IsDisposed { get; }
    int ReferenceCounter { get; }
}

public interface IAsyncInitializable
{
    Task InitializeAsync();
    bool IsInitialized { get; }
    bool IsDisposed { get; }
    int ReferenceCounter { get; }
}