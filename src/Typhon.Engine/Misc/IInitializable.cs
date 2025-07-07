// unset

namespace Typhon.Engine;

public interface IInitializable
{
    void Initialize();
    bool IsInitialized { get; }
    bool IsDisposed { get; }
    int ReferenceCounter { get; }
}