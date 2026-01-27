using System;
using System.Collections.Generic;
using System.Numerics;

namespace FF16Framework.Interfaces.Magic;

/// <summary>
/// Builder interface for configuring magic spell modifications.
/// Supports fluent API pattern for easy chaining.
/// 
/// Magic Structure in FF16:
/// - Magic → OperationGroups → Operations → Properties
/// - Each Magic has multiple OperationGroups (blocks of operations executed together)
/// - Each OperationGroup has multiple Operations (individual behaviors)
/// - Each Operation has multiple Properties (configuration values)
/// </summary>
public interface IMagicBuilder
{
    /// <summary>
    /// Gets the magic ID this builder is configured for.
    /// </summary>
    int MagicId { get; }
    
    // ========================================
    // PROPERTY MODIFICATIONS
    // ========================================
    
    /// <summary>
    /// Sets (modifies) an existing property value within a specific operation.
    /// </summary>
    /// <param name="operationGroupId">The operation group containing the operation.</param>
    /// <param name="operationId">The specific operation containing the property.</param>
    /// <param name="propertyId">The property ID to modify.</param>
    /// <param name="value">The new value (int, float, bool, or Vector3).</param>
    /// <returns>This builder for chaining.</returns>
    IMagicBuilder SetProperty(int operationGroupId, int operationId, int propertyId, object value);
    
    /// <summary>
    /// Removes a property from a specific operation.
    /// </summary>
    IMagicBuilder RemoveProperty(int operationGroupId, int operationId, int propertyId);
    
    /// <summary>
    /// Adds a new property to an existing operation.
    /// </summary>
    IMagicBuilder AddProperty(int operationGroupId, int operationId, int propertyId, object value);
    
    // ========================================
    // OPERATION MODIFICATIONS
    // ========================================
    
    /// <summary>
    /// Adds a new operation to an operation group with no properties.
    /// </summary>
    IMagicBuilder AddOperation(int operationGroupId, int operationId);
    
    /// <summary>
    /// Adds a new operation to an operation group with multiple properties.
    /// </summary>
    IMagicBuilder AddOperation(int operationGroupId, int operationId, IList<int> propertyIds, IList<object> values);
    
    /// <summary>
    /// Removes an operation from an operation group.
    /// </summary>
    IMagicBuilder RemoveOperation(int operationGroupId, int operationId);
    
    // ========================================
    // VALIDATION
    // ========================================
    
    /// <summary>
    /// Checks if an operation group exists in the current magic definition.
    /// </summary>
    bool HasOperationGroup(int operationGroupId);
    
    /// <summary>
    /// Checks if an operation exists within an operation group.
    /// </summary>
    bool HasOperation(int operationGroupId, int operationId);
    
    /// <summary>
    /// Checks if a property exists within an operation.
    /// </summary>
    bool HasProperty(int operationGroupId, int operationId, int propertyId);
    
    /// <summary>
    /// Gets all operation group IDs in the current magic definition.
    /// </summary>
    IReadOnlyList<int> GetOperationGroupIds();
    
    /// <summary>
    /// Gets all operation IDs in a specific operation group.
    /// </summary>
    IReadOnlyList<int> GetOperationIds(int operationGroupId);
    
    // ========================================
    // BUILD & EXECUTE
    // ========================================
    
    /// <summary>
    /// Gets the list of modifications configured in this builder.
    /// </summary>
    IReadOnlyList<MagicModification> GetModifications();
    
    /// <summary>
    /// Exports the current modifications to a JSON string.
    /// </summary>
    string ExportToJson();
    
    /// <summary>
    /// Clears all modifications from this builder.
    /// </summary>
    IMagicBuilder Clear();
    
    /// <summary>
    /// Casts the spell with the configured modifications.
    /// </summary>
    /// <param name="sourceActor">The actor casting the spell. Null = player.</param>
    /// <param name="targetActor">The target actor. Null = locked target, nint.Zero = no target.</param>
    /// <returns>True if the spell was cast successfully.</returns>
    bool Cast(nint? sourceActor = null, nint? targetActor = null);
    
    /// <summary>
    /// Casts the spell using the game's targeting system for proper body targeting.
    /// </summary>
    /// <param name="sourceActor">The actor casting the spell. Null = player.</param>
    /// <returns>True if the spell was cast successfully.</returns>
    bool CastWithGameTarget(nint? sourceActor = null);
}
