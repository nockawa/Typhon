// unset

using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// By default, all internal type are documented, but you can decorate one with this attribute to exclude it from the documentation
/// </summary>
[PublicAPI]
public class ExcludeDocFxAttribute : Attribute { }
