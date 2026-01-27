using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

using FF16Framework.Interfaces.Magic;

namespace FF16Framework.Services.Magic;

/// <summary>
/// Implementation of IMagicBuilder.
/// Provides fluent API for configuring magic spell modifications.
/// </summary>
public class MagicBuilder : IMagicBuilder
{
    private readonly MagicService _service;
    
    // Dictionary keyed by (Type, GroupId, OpId, PropId) to ensure uniqueness
    private readonly Dictionary<(MagicModificationType Type, int GroupId, int OpId, int PropId), MagicModification> _modifications = new();
    
    public int MagicId { get; }
    
    internal MagicBuilder(int magicId, MagicService service)
    {
        MagicId = magicId;
        _service = service;
    }
    
    // ========================================
    // PROPERTY MODIFICATIONS
    // ========================================
    
    public IMagicBuilder SetProperty(int operationGroupId, int operationId, int propertyId, object value)
    {
        // Remove any conflicting RemoveProperty
        var removeKey = (MagicModificationType.RemoveProperty, operationGroupId, operationId, propertyId);
        _modifications.Remove(removeKey);
        
        // Check if there's an existing AddProperty - update instead
        var addKey = (MagicModificationType.AddProperty, operationGroupId, operationId, propertyId);
        if (_modifications.TryGetValue(addKey, out var existingAdd))
        {
            _modifications[addKey] = existingAdd with { Value = NormalizeValue(value) };
            return this;
        }
        
        var key = (MagicModificationType.SetProperty, operationGroupId, operationId, propertyId);
        _modifications[key] = new MagicModification
        {
            Type = MagicModificationType.SetProperty,
            OperationGroupId = operationGroupId,
            OperationId = operationId,
            PropertyId = propertyId,
            Value = NormalizeValue(value)
        };
        
        return this;
    }
    
    public IMagicBuilder RemoveProperty(int operationGroupId, int operationId, int propertyId)
    {
        // Remove any conflicting SetProperty or AddProperty
        var setKey = (MagicModificationType.SetProperty, operationGroupId, operationId, propertyId);
        var addKey = (MagicModificationType.AddProperty, operationGroupId, operationId, propertyId);
        _modifications.Remove(setKey);
        _modifications.Remove(addKey);
        
        var key = (MagicModificationType.RemoveProperty, operationGroupId, operationId, propertyId);
        _modifications[key] = new MagicModification
        {
            Type = MagicModificationType.RemoveProperty,
            OperationGroupId = operationGroupId,
            OperationId = operationId,
            PropertyId = propertyId
        };
        
        return this;
    }
    
    public IMagicBuilder AddProperty(int operationGroupId, int operationId, int propertyId, object value)
    {
        // Check if there's already a SetProperty - use SetProperty instead
        var setKey = (MagicModificationType.SetProperty, operationGroupId, operationId, propertyId);
        if (_modifications.ContainsKey(setKey))
        {
            return SetProperty(operationGroupId, operationId, propertyId, value);
        }
        
        // Remove conflicting RemoveProperty
        var removeKey = (MagicModificationType.RemoveProperty, operationGroupId, operationId, propertyId);
        _modifications.Remove(removeKey);
        
        var key = (MagicModificationType.AddProperty, operationGroupId, operationId, propertyId);
        _modifications[key] = new MagicModification
        {
            Type = MagicModificationType.AddProperty,
            OperationGroupId = operationGroupId,
            OperationId = operationId,
            PropertyId = propertyId,
            Value = NormalizeValue(value)
        };
        
        return this;
    }
    
    // ========================================
    // OPERATION MODIFICATIONS
    // ========================================
    
    public IMagicBuilder AddOperation(int operationGroupId, int operationId)
    {
        var key = (MagicModificationType.AddOperation, operationGroupId, operationId, -1);
        
        // Remove conflicting RemoveOperation
        var removeKey = (MagicModificationType.RemoveOperation, operationGroupId, operationId, -1);
        _modifications.Remove(removeKey);
        
        _modifications[key] = new MagicModification
        {
            Type = MagicModificationType.AddOperation,
            OperationGroupId = operationGroupId,
            OperationId = operationId,
            PropertyId = -1
        };
        
        return this;
    }
    
    public IMagicBuilder AddOperation(int operationGroupId, int operationId, IList<int> propertyIds, IList<object> values)
    {
        if (propertyIds.Count != values.Count)
        {
            throw new ArgumentException($"propertyIds ({propertyIds.Count}) and values ({values.Count}) must have the same length");
        }
        
        // First add the operation
        AddOperation(operationGroupId, operationId);
        
        // Then add each property
        for (int i = 0; i < propertyIds.Count; i++)
        {
            AddProperty(operationGroupId, operationId, propertyIds[i], values[i]);
        }
        
        return this;
    }
    
    public IMagicBuilder RemoveOperation(int operationGroupId, int operationId)
    {
        // Remove all modifications for this operation
        var keysToRemove = _modifications.Keys
            .Where(k => k.GroupId == operationGroupId && k.OpId == operationId)
            .ToList();
        foreach (var keyToRemove in keysToRemove)
        {
            _modifications.Remove(keyToRemove);
        }
        
        var key = (MagicModificationType.RemoveOperation, operationGroupId, operationId, -1);
        _modifications[key] = new MagicModification
        {
            Type = MagicModificationType.RemoveOperation,
            OperationGroupId = operationGroupId,
            OperationId = operationId,
            PropertyId = -1
        };
        
        return this;
    }
    
    // ========================================
    // VALIDATION (Stubs - validation at runtime)
    // ========================================
    
    public bool HasOperationGroup(int operationGroupId) => true;
    public bool HasOperation(int operationGroupId, int operationId) => true;
    public bool HasProperty(int operationGroupId, int operationId, int propertyId) => true;
    public IReadOnlyList<int> GetOperationGroupIds() => Array.Empty<int>();
    public IReadOnlyList<int> GetOperationIds(int operationGroupId) => Array.Empty<int>();
    
    // ========================================
    // BUILD & EXECUTE
    // ========================================
    
    public IReadOnlyList<MagicModification> GetModifications() => _modifications.Values.ToList();
    
    public IMagicBuilder Clear()
    {
        _modifications.Clear();
        return this;
    }
    
    public string ExportToJson()
    {
        var json = new MagicModificationJson
        {
            MagicId = MagicId,
            Modifications = _modifications.Values.Select(m => new MagicModificationJson.ModificationEntry
            {
                Type = m.Type.ToString(),
                GroupId = m.OperationGroupId,
                OpId = m.OperationId,
                PropId = m.PropertyId,
                Value = SerializeValue(m.Value),
                InjectAfterOp = m.InjectAfterOp,
                Occurrence = m.Occurrence
            }).ToList()
        };
        
        return JsonSerializer.Serialize(json, new JsonSerializerOptions 
        { 
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }
    
    public bool Cast(nint? sourceActor = null, nint? targetActor = null)
    {
        // Enqueue modifications if any
        if (_modifications.Count > 0)
        {
            _service.EnqueueModifications(MagicId, _modifications.Values);
        }
        
        return _service.Cast(MagicId, sourceActor, targetActor);
    }
    
    public bool CastWithGameTarget(nint? sourceActor = null)
    {
        // Enqueue modifications if any
        if (_modifications.Count > 0)
        {
            _service.EnqueueModifications(MagicId, _modifications.Values);
        }
        
        return _service.CastWithGameTarget(MagicId, sourceActor);
    }
    
    // ========================================
    // INTERNAL HELPERS
    // ========================================
    
    private static object NormalizeValue(object value)
    {
        return value switch
        {
            bool b => b ? 1 : 0,
            float f => f,
            double d => (float)d,
            int i => i,
            Vector3 v => v,
            _ => value
        };
    }
    
    private static object? SerializeValue(object? value)
    {
        if (value is Vector3 v)
        {
            return new { x = v.X, y = v.Y, z = v.Z };
        }
        return value;
    }
    
    // ========================================
    // IMPORT HELPERS
    // ========================================
    
    internal void ImportModifications(IEnumerable<MagicModificationJson.ModificationEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!Enum.TryParse<MagicModificationType>(entry.Type, out var modType))
                continue;
            
            var value = DeserializeValue(entry.Value);
            
            var key = (modType, entry.GroupId, entry.OpId, entry.PropId);
            _modifications[key] = new MagicModification
            {
                Type = modType,
                OperationGroupId = entry.GroupId,
                OperationId = entry.OpId,
                PropertyId = entry.PropId,
                Value = value,
                InjectAfterOp = entry.InjectAfterOp,
                Occurrence = entry.Occurrence
            };
        }
    }
    
    private static object? DeserializeValue(object? value)
    {
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object && 
                element.TryGetProperty("x", out var x) &&
                element.TryGetProperty("y", out var y) &&
                element.TryGetProperty("z", out var z))
            {
                return new Vector3(x.GetSingle(), y.GetSingle(), z.GetSingle());
            }
            
            return element.ValueKind switch
            {
                JsonValueKind.Number when element.TryGetInt32(out int i) => i,
                JsonValueKind.Number => element.GetSingle(),
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                _ => null
            };
        }
        return value;
    }
}
