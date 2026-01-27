using System;
using System.Collections.Generic;
using System.Numerics;

namespace FF16Framework.Interfaces.Magic;

/// <summary>
/// Modification mode for magic spells.
/// </summary>
public enum MagicModificationMode
{
    /// <summary>
    /// Modifies the .magic file buffer in memory via ResourceHandle.ReplaceBuffer().
    /// Changes persist until the resource is unloaded or restored.
    /// Less CPU intensive - modifications applied once when the spell is loaded.
    /// </summary>
    MemoryMode,
    
    /// <summary>
    /// Intercepts property values at runtime during spell execution.
    /// Changes apply per-execution and don't modify the underlying resource.
    /// More flexible but slightly higher CPU cost per cast.
    /// </summary>
    RuntimeMode
}

/// <summary>
/// Type of modification to apply to a magic spell.
/// </summary>
public enum MagicModificationType
{
    /// <summary>
    /// Modify an existing property value.
    /// </summary>
    SetProperty,
    
    /// <summary>
    /// Remove an existing property from an operation.
    /// </summary>
    RemoveProperty,
    
    /// <summary>
    /// Add a new property to an existing operation.
    /// </summary>
    AddProperty,
    
    /// <summary>
    /// Add a new operation to an operation group.
    /// </summary>
    AddOperation,
    
    /// <summary>
    /// Remove an operation from an operation group.
    /// </summary>
    RemoveOperation
}

/// <summary>
/// Represents a single modification to a magic spell.
/// </summary>
public record MagicModification
{
    /// <summary>
    /// The type of modification.
    /// </summary>
    public required MagicModificationType Type { get; init; }
    
    /// <summary>
    /// The operation group ID containing the target operation.
    /// </summary>
    public required int OperationGroupId { get; init; }
    
    /// <summary>
    /// The operation ID (type) within the group.
    /// </summary>
    public required int OperationId { get; init; }
    
    /// <summary>
    /// The property ID to modify. Use -1 for operation-level modifications.
    /// </summary>
    public int PropertyId { get; init; } = -1;
    
    /// <summary>
    /// The new value for the property. Can be int, float, bool, or Vector3.
    /// </summary>
    public object? Value { get; init; }
    
    /// <summary>
    /// For injections, specifies after which operation to inject.
    /// Use -1 to inject at the end of the group.
    /// Use 0 (default) to inject immediately after the current operation.
    /// </summary>
    public int InjectAfterOp { get; init; } = 0;
    
    /// <summary>
    /// Optional occurrence index when targeting a specific instance of repeated operations.
    /// </summary>
    public int Occurrence { get; init; } = 0;
}

/// <summary>
/// JSON-serializable format for magic modifications.
/// Used for import/export functionality.
/// </summary>
public class MagicModificationJson
{
    public int MagicId { get; set; }
    public List<ModificationEntry> Modifications { get; set; } = new();
    
    public class ModificationEntry
    {
        public string Type { get; set; } = "SetProperty";
        public int GroupId { get; set; }
        public int OpId { get; set; }
        public int PropId { get; set; } = -1;
        public object? Value { get; set; }
        public int InjectAfterOp { get; set; } = 0;
        public int Occurrence { get; set; } = 0;
    }
}
