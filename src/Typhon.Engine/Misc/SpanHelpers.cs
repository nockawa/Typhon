using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

internal static class SpanHelpers
{
    public static Span<TTo> Cast<TFRom, TTo>(this Span<TFRom> span) where TFRom : struct where TTo : struct => MemoryMarshal.Cast<TFRom, TTo>(span);
    public static ReadOnlySpan<TTo> Cast<TFRom, TTo>(this ReadOnlySpan<TFRom> span) where TFRom : struct where TTo : struct => MemoryMarshal.Cast<TFRom, TTo>(span);
}