using System.Numerics;
using System.Text.Json;

using FF16Framework.Interfaces.Actor;
using FF16Framework.Interfaces.Magic;
using FF16Framework.Services.ResourceManager;

using FF16Tools.Files.Magic;
using FF16Tools.Files.Magic.Factories;
using FF16Tools.Files.Magic.Operations;

using Reloaded.Mod.Interfaces;

namespace FF16Framework.Services.Magic;

/// <summary>
/// Main magic service implementing IMagicService.
/// Provides spell building, casting, and dual-mode modification support.
/// </summary>
public class MagicService : IMagicService
{
    private readonly ILogger _logger;
    private readonly MagicCaster _caster;
    private readonly RuntimeMagicProcessor _processor;
    private readonly IActorService _actorService;
    private readonly ResourceManagerService _resourceManager;
    
    public MagicService(
        ILogger logger, 
        MagicCaster caster, 
        RuntimeMagicProcessor processor,
        IActorService actorService,
        ResourceManagerService resourceManager)
    {
        _logger = logger;
        _caster = caster;
        _processor = processor;
        _actorService = actorService;
        _resourceManager = resourceManager;
        
        // Default to MemoryMode
        Mode = MagicModificationMode.MemoryMode;
    }
    
    // ========================================
    // STATUS
    // ========================================
    
    /// <inheritdoc/>
    public bool IsReady => _caster.IsReady;
    
    /// <inheritdoc/>
    public MagicModificationMode Mode 
    { 
        get => _processor.IsEnabled ? MagicModificationMode.RuntimeMode : MagicModificationMode.MemoryMode;
        set => _processor.IsEnabled = value == MagicModificationMode.RuntimeMode;
    }
    
    // ========================================
    // SPELL BUILDING
    // ========================================
    
    /// <inheritdoc/>
    public IMagicBuilder CreateSpell(int magicId)
    {
        return new MagicBuilder(magicId, this);
    }
    
    /// <inheritdoc/>
    public IMagicBuilder? ImportFromJson(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<MagicModificationJson>(json);
            if (data == null) return null;
            
            var builder = new MagicBuilder(data.MagicId, this);
            builder.ImportModifications(data.Modifications);
            return builder;
        }
        catch (Exception ex)
        {
            _logger.WriteLine($"[MagicService] Failed to import JSON: {ex.Message}");
            return null;
        }
    }
    
    /// <inheritdoc/>
    public IMagicBuilder? ImportFromFile(string filePath)
    {
        try
        {
            string json = File.ReadAllText(filePath);
            return ImportFromJson(json);
        }
        catch (Exception ex)
        {
            _logger.WriteLine($"[MagicService] Failed to load file: {ex.Message}");
            return null;
        }
    }
    
    // ========================================
    // DIRECT CASTING
    // ========================================
    
    /// <inheritdoc/>
    public bool Cast(int magicId, nint? sourceActor = null, nint? targetActor = null)
    {
        return _caster.Cast(magicId, sourceActor, targetActor);
    }
    
    /// <inheritdoc/>
    public bool CastWithGameTarget(int magicId, nint? sourceActor = null)
    {
        return _caster.CastWithGameTarget(magicId, sourceActor);
    }
    
    // ========================================
    // MODIFICATION QUEUE
    // ========================================
    
    /// <inheritdoc/>
    public void EnqueueModifications(int magicId, IEnumerable<MagicModification> modifications)
    {
        if (Mode == MagicModificationMode.RuntimeMode)
        {
            _processor.EnqueueModifications(magicId, modifications);
        }
        else
        {
            // In MemoryMode, apply to the resource handle if loaded
            ApplyModificationsToResource(magicId, modifications);
        }
    }
    
    // ========================================
    // TARGETING
    // ========================================
    
    /// <inheritdoc/>
    public nint GetLockedTarget() => _actorService.GetLockedTarget();
    
    /// <inheritdoc/>
    public nint GetPlayerActor() => _actorService.GetPlayerActor();
    
    // ========================================
    // CALLBACKS
    // ========================================
    
    /// <inheritdoc/>
    public void RegisterChargedShotHandler(Func<int, bool> handler)
    {
        _caster.RegisterChargedShotHandler(handler);
    }
    
    // ========================================
    // MEMORY MODE HELPERS
    // ========================================
    
    /// <summary>
    /// Applies modifications to a loaded magic resource in MemoryMode.
    /// </summary>
    private unsafe void ApplyModificationsToResource(int magicId, IEnumerable<MagicModification> modifications)
    {
        // Find the magic resource by magic ID
        // Magic files are typically named like "magic/0214.magic" for magic ID 214
        string magicFileName = $"magic/{magicId:D4}.magic";
        
        if (!_resourceManager.SortedHandles.TryGetValue(".magic", out var magicHandles))
        {
            _logger.WriteLine($"[MagicService] No .magic resources loaded");
            return;
        }
        
        ResourceHandle? handle = null;
        foreach (var kvp in magicHandles)
        {
            if (kvp.Key.Contains($"{magicId:D4}") || kvp.Key.EndsWith($"/{magicId}.magic"))
            {
                handle = kvp.Value;
                break;
            }
        }
        
        if (handle == null || !handle.IsLoaded())
        {
            _logger.WriteLine($"[MagicService] Magic {magicId} resource not found or not loaded");
            return;
        }
        
        try
        {
            // Read current buffer
            var currentBuffer = new Span<byte>((void*)handle.BufferAddress, (int)handle.FileSize);
            
            // Parse with FF16Tools
            var magicFile = MagicFile.Open(handle.BufferAddress, handle.FileSize);
            
            // Apply modifications to the magic entry for this ID
            if (magicFile.MagicEntries.TryGetValue((uint)magicId, out var entry))
            {
                ApplyModificationsToMagicEntry(entry, modifications);
            }
            else
            {
                _logger.WriteLine($"[MagicService] Magic entry {magicId} not found in file");
                return;
            }
            
            // Write back
            using var outputStream = new MemoryStream();
            magicFile.Write(outputStream);
            
            // Replace buffer
            handle.ReplaceBuffer(outputStream.ToArray());
            
            _logger.WriteLine($"[MagicService] Applied {modifications.Count()} modifications to magic {magicId} (MemoryMode)");
        }
        catch (Exception ex)
        {
            _logger.WriteLine($"[MagicService] Failed to apply modifications: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Applies modifications to a MagicEntry.
    /// </summary>
    private void ApplyModificationsToMagicEntry(MagicEntry entry, IEnumerable<MagicModification> modifications)
    {
        foreach (var mod in modifications)
        {
            try
            {
                switch (mod.Type)
                {
                    case MagicModificationType.SetProperty:
                        SetPropertyInEntry(entry, mod);
                        break;
                    case MagicModificationType.RemoveProperty:
                        RemovePropertyFromEntry(entry, mod);
                        break;
                    case MagicModificationType.AddProperty:
                        AddPropertyToEntry(entry, mod);
                        break;
                    case MagicModificationType.AddOperation:
                        AddOperationToEntry(entry, mod);
                        break;
                    case MagicModificationType.RemoveOperation:
                        RemoveOperationFromEntry(entry, mod);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[MagicService] Failed to apply mod {mod.Type} Group {mod.OperationGroupId} Op {mod.OperationId}: {ex.Message}");
            }
        }
    }
    
    private void SetPropertyInEntry(MagicEntry entry, MagicModification mod)
    {
        // Find the operation group
        var group = entry.OperationGroupList?.OperationGroups.FirstOrDefault(g => g.Id == mod.OperationGroupId);
        if (group == null) return;
        
        // Find the operation
        var operation = group.OperationList?.Operations.FirstOrDefault(o => (int)o.Type == mod.OperationId);
        if (operation == null) return;
        
        // Find and update the property
        var property = operation.Properties.FirstOrDefault(p => (int)p.Type == mod.PropertyId);
        if (property?.Value == null) return;
        
        SetPropertyValue(property.Value, mod.Value);
    }
    
    private void RemovePropertyFromEntry(MagicEntry entry, MagicModification mod)
    {
        var group = entry.OperationGroupList?.OperationGroups.FirstOrDefault(g => g.Id == mod.OperationGroupId);
        if (group == null) return;
        
        var operation = group.OperationList?.Operations.FirstOrDefault(o => (int)o.Type == mod.OperationId);
        if (operation == null) return;
        
        var property = operation.Properties.FirstOrDefault(p => (int)p.Type == mod.PropertyId);
        if (property != null)
        {
            operation.Properties.Remove(property);
        }
    }
    
    private void AddPropertyToEntry(MagicEntry entry, MagicModification mod)
    {
        var group = entry.OperationGroupList?.OperationGroups.FirstOrDefault(g => g.Id == mod.OperationGroupId);
        if (group == null) return;
        
        var operation = group.OperationList?.Operations.FirstOrDefault(o => (int)o.Type == mod.OperationId);
        if (operation == null) return;
        
        // Check if property already exists
        var existingProp = operation.Properties.FirstOrDefault(p => (int)p.Type == mod.PropertyId);
        if (existingProp?.Value != null)
        {
            SetPropertyValue(existingProp.Value, mod.Value);
            return;
        }
        
        // Create and add new property with value
        var newProp = new MagicOperationProperty((MagicPropertyType)mod.PropertyId);
        // Value will be set when Write() is called from the Data
        operation.Properties.Add(newProp);
    }
    
    private void AddOperationToEntry(MagicEntry entry, MagicModification mod)
    {
        var group = entry.OperationGroupList?.OperationGroups.FirstOrDefault(g => g.Id == mod.OperationGroupId);
        if (group == null) return;
        
        // Create new operation
        var newOp = MagicOperationFactory.Create((MagicOperationType)mod.OperationId);
        group.OperationList?.Operations.Add(newOp);
    }
    
    private void RemoveOperationFromEntry(MagicEntry entry, MagicModification mod)
    {
        var group = entry.OperationGroupList?.OperationGroups.FirstOrDefault(g => g.Id == mod.OperationGroupId);
        if (group == null) return;
        
        var operation = group.OperationList?.Operations.FirstOrDefault(o => (int)o.Type == mod.OperationId);
        if (operation != null)
        {
            group.OperationList?.Operations.Remove(operation);
        }
    }
    
    private void SetPropertyValue(MagicPropertyValueBase property, object? value)
    {
        if (value == null) return;
        
        switch (value)
        {
            case float f:
                if (property is MagicPropertyFloatValue floatProp)
                    floatProp.Value = f;
                break;
            case int i:
                if (property is MagicPropertyIntValue intProp)
                    intProp.Value = i;
                else if (property is MagicPropertyBoolValue boolProp)
                    boolProp.Value = i != 0;
                break;
            case Vector3 v:
                if (property is MagicPropertyVec3Value vecProp)
                    vecProp.Value = v;
                break;
        }
    }
}
