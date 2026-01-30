using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine;

/// <summary>
/// Extension methods for tree navigation on <see cref="IResource"/> instances.
/// </summary>
[PublicAPI]
public static class ResourceExtensions
{
    /// <summary>
    /// Gets all ancestors of this resource, starting from the immediate parent up to the root.
    /// </summary>
    /// <param name="resource">The resource to get ancestors for.</param>
    /// <returns>Ancestors from parent to root (nearest first).</returns>
    public static IEnumerable<IResource> GetAncestors(this IResource resource)
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));

        var current = resource.Parent;
        while (current != null)
        {
            yield return current;
            current = current.Parent;
        }
    }

    /// <summary>
    /// Gets all descendants of this resource using depth-first traversal.
    /// </summary>
    /// <param name="resource">The resource to get descendants for.</param>
    /// <returns>All descendants (children, grandchildren, etc.).</returns>
    public static IEnumerable<IResource> GetDescendants(this IResource resource)
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));

        foreach (var child in resource.Children)
        {
            yield return child;
            foreach (var descendant in child.GetDescendants())
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Gets the full path from root to this resource.
    /// </summary>
    /// <param name="resource">The resource to get the path for.</param>
    /// <param name="separator">Path separator (default: "/").</param>
    /// <returns>Full path string (e.g., "Root/DataEngine/DatabaseEngine_abc123").</returns>
    public static string GetPath(this IResource resource, string separator = "/")
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));

        var ancestors = resource.GetAncestors().Reverse().ToList();
        ancestors.Add(resource);
        return string.Join(separator, ancestors.Select(r => r.Id));
    }

    /// <summary>
    /// Finds a descendant resource by relative path.
    /// </summary>
    /// <param name="resource">The starting resource.</param>
    /// <param name="path">Relative path (e.g., "DatabaseEngine_abc/ComponentTable_Player").</param>
    /// <param name="separator">Path separator (default: "/").</param>
    /// <returns>The resource at the path, or null if not found.</returns>
    public static IResource FindByPath(this IResource resource, string path, string separator = "/")
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));
        if (string.IsNullOrEmpty(path)) return resource;

        var parts = path.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries);
        var current = resource;

        foreach (var part in parts)
        {
            var child = current.Children.FirstOrDefault(c => c.Id == part);
            if (child == null) return null;
            current = child;
        }

        return current;
    }

    /// <summary>
    /// Finds all descendants matching a predicate.
    /// </summary>
    /// <param name="resource">The starting resource.</param>
    /// <param name="predicate">Filter predicate.</param>
    /// <returns>All matching descendants.</returns>
    public static IEnumerable<IResource> FindAll(this IResource resource, Func<IResource, bool> predicate)
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));

        return resource.GetDescendants().Where(predicate);
    }

    /// <summary>
    /// Finds the first descendant matching a predicate.
    /// </summary>
    /// <param name="resource">The starting resource.</param>
    /// <param name="predicate">Filter predicate.</param>
    /// <returns>First matching descendant, or null if none found.</returns>
    public static IResource FindFirst(this IResource resource, Func<IResource, bool> predicate)
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));

        return resource.GetDescendants().FirstOrDefault(predicate);
    }

    /// <summary>
    /// Gets the depth of this resource in the tree (root = 0).
    /// </summary>
    /// <param name="resource">The resource to measure.</param>
    /// <returns>Depth from root.</returns>
    public static int GetDepth(this IResource resource)
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));

        int depth = 0;
        var current = resource.Parent;
        while (current != null)
        {
            depth++;
            current = current.Parent;
        }
        return depth;
    }

    /// <summary>
    /// Checks if this resource is an ancestor of another resource.
    /// </summary>
    /// <param name="resource">The potential ancestor.</param>
    /// <param name="other">The potential descendant.</param>
    /// <returns>True if this resource is an ancestor of other.</returns>
    public static bool IsAncestorOf(this IResource resource, IResource other)
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));
        if (other == null) throw new ArgumentNullException(nameof(other));

        return other.GetAncestors().Any(a => ReferenceEquals(a, resource));
    }

    /// <summary>
    /// Checks if this resource is a descendant of another resource.
    /// </summary>
    /// <param name="resource">The potential descendant.</param>
    /// <param name="other">The potential ancestor.</param>
    /// <returns>True if this resource is a descendant of other.</returns>
    public static bool IsDescendantOf(this IResource resource, IResource other)
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));
        if (other == null) throw new ArgumentNullException(nameof(other));

        return resource.GetAncestors().Any(a => ReferenceEquals(a, other));
    }

    /// <summary>
    /// Gets the total count of descendants.
    /// </summary>
    /// <param name="resource">The resource to count from.</param>
    /// <returns>Total number of descendants.</returns>
    public static int GetDescendantCount(this IResource resource)
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));

        return resource.GetDescendants().Count();
    }
}
