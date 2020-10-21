#pragma warning disable CS1591

using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Entities
{
    /// <summary>
    /// This is just a marker interface to denote top level folders.
    /// </summary>
    public interface ICollectionFolder : IHasCollectionType
    {
        string Path { get; }

        string Name { get; }

        Guid Id { get; }

        IReadOnlyCollection<string> PhysicalLocations { get; }
    }

    public interface ISupportsUserSpecificView
    {
        bool EnableUserSpecificView { get; }
    }

    public interface IHasCollectionType
    {
        string CollectionType { get; }
    }
}
