using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Typhon.Engine")]
[assembly: InternalsVisibleTo("Typhon.Engine.Tests")]
[assembly: InternalsVisibleTo("Typhon.Benchmark")]

namespace Typhon.Schema.Definition;

[AttributeUsage(AttributeTargets.Struct)]
[PublicAPI]
public sealed class ComponentAttribute : Attribute
{
    public string Name { get; }
    public int Revision { get; }
    public bool AllowMultiple { get; }

    public ComponentAttribute(string name, int revision, bool allowMultiple = false)
    {
        Name = name;
        Revision = revision;
        AllowMultiple = allowMultiple;
    }
}

[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class FieldAttribute : Attribute
{
    public int? FieldId { get; set; }
    public string Name { get; set; }
}

[AttributeUsage(AttributeTargets.Field)]
[PublicAPI]
public sealed class IndexAttribute : Attribute
{
    public bool AllowMultiple { get; set; }
}
