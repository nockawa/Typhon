namespace Typhon.Engine;

internal interface IView
{
    int ViewId { get; }
    int[] FieldDependencies { get; }
    bool IsDisposed { get; }
    ViewDeltaRingBuffer DeltaBuffer { get; }
}