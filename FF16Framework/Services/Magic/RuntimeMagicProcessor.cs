using System.Numerics;
using System.Runtime.InteropServices;

using FF16Framework.Faith.Structs;
using FF16Framework.Interfaces.Magic;

using NenTools.ImGui.Interfaces.Shell;

using Reloaded.Hooks.Definitions;
using Reloaded.Mod.Interfaces;

using RyoTune.Reloaded;

namespace FF16Framework.Services.Magic;

/// <summary>
/// Hook-based runtime property interception for RuntimeMode.
/// Intercepts magic file processing and applies modifications per-execution.
/// </summary>
public unsafe class RuntimeMagicProcessor : HookGroupBase
{
    // ========================================
    // DELEGATES
    // ========================================
    
    public delegate void MagicFileInstance_CreateOperationAndApplyPropertiesDelegate(
        long magicFileInstance, int opType, int propertyId, long dataPtr);
    
    public delegate long MagicFile_ProcessDelegate(long a1, long a2, long a3, long a4);
    public delegate long MagicFile_HandleSubEntryDelegate(long a1, long a2, long a3, long a4);
    
    // ========================================
    // HOOKS
    // ========================================
    
    private IHook<MagicFileInstance_CreateOperationAndApplyPropertiesDelegate>? _createOpHook;
    private IHook<MagicFile_ProcessDelegate>? _processHook;
    private IHook<MagicFile_HandleSubEntryDelegate>? _handleSubEntryHook;
    
    // ========================================
    // STATE
    // ========================================
    
    private readonly Dictionary<(int magicId, int groupId), Queue<List<MagicModification>>> _groupedQueues = new();
    private List<MagicModification>? _activeInstanceEntries = null;
    private int _activeInstanceMagicId = 0;
    
    private readonly Dictionary<int, int> _opInstanceTracker = new();
    private readonly Dictionary<long, int> _propInstanceTracker = new();
    private int _lastOpType = -1;
    private readonly List<MagicModification> _pendingInjections = new();
    private bool _isProcessingInjections = false;
    
    /// <summary>
    /// When false, hooks pass through without processing modifications.
    /// </summary>
    public bool IsEnabled { get; set; } = false;
    
    // ========================================
    // DEPENDENCIES
    // ========================================
    
    private readonly IImGuiShell _shell;
    
    // ========================================
    // EVENTS
    // ========================================
    
    /// <summary>
    /// Raised when a magic file instance is processed, providing the factory for VFX.
    /// </summary>
    public event Action<long>? OnMagicFactoryUpdated;
    
    public RuntimeMagicProcessor(Config config, IModConfig modConfig, ILogger logger, IImGuiShell shell)
        : base(config, modConfig, logger)
    {
        _shell = shell;
    }
    
    // ========================================
    // SETUP
    // ========================================
    
    public override void SetupHooks()
    {
        Project.Scans.AddScanHook("MagicFileInstance_CreateOperationAndApplyProperties", (result, hooks) =>
        {
            _createOpHook = hooks.CreateHook<MagicFileInstance_CreateOperationAndApplyPropertiesDelegate>(
                CreateOperationAndApplyPropertiesImpl, result).Activate();
            _logger.WriteLine($"[RuntimeMagicProcessor] Hooked CreateOperationAndApplyProperties at 0x{result:X}");
        });
        
        Project.Scans.AddScanHook("MagicFile_Process", (result, hooks) =>
        {
            _processHook = hooks.CreateHook<MagicFile_ProcessDelegate>(
                MagicFileProcessImpl, result).Activate();
            _logger.WriteLine($"[RuntimeMagicProcessor] Hooked MagicFile_Process at 0x{result:X}");
        });
        
        Project.Scans.AddScanHook("MagicFile_HandleSubEntry", (result, hooks) =>
        {
            _handleSubEntryHook = hooks.CreateHook<MagicFile_HandleSubEntryDelegate>(
                MagicFileHandleSubEntryImpl, result).Activate();
            _logger.WriteLine($"[RuntimeMagicProcessor] Hooked MagicFile_HandleSubEntry at 0x{result:X}");
        });
    }
    
    // ========================================
    // PUBLIC API
    // ========================================
    
    /// <summary>
    /// Enqueues modifications to be applied when a magic spell is processed.
    /// </summary>
    public void EnqueueModifications(int magicId, IEnumerable<MagicModification> modifications)
    {
        var grouped = modifications.GroupBy(m => m.OperationGroupId);
        
        foreach (var group in grouped)
        {
            var key = (magicId, group.Key);
            if (!_groupedQueues.TryGetValue(key, out var queue))
            {
                queue = new Queue<List<MagicModification>>();
                _groupedQueues[key] = queue;
            }
            queue.Enqueue(group.ToList());
            _shell.LogWriteLine("MagicProcessor", $"Enqueued {group.Count()} entries for Magic {magicId} Group {group.Key}", color: Color.Yellow);
        }
    }
    
    /// <summary>
    /// Clears all queued modifications.
    /// </summary>
    public void ClearQueue()
    {
        _groupedQueues.Clear();
        _activeInstanceEntries = null;
        _activeInstanceMagicId = 0;
    }
    
    // ========================================
    // HOOK IMPLEMENTATIONS
    // ========================================
    
    private long MagicFileProcessImpl(long a1, long a2, long a3, long a4)
    {
        // Reset trackers
        _opInstanceTracker.Clear();
        _propInstanceTracker.Clear();
        _lastOpType = -1;
        _pendingInjections.Clear();
        _activeInstanceEntries = null;
        _activeInstanceMagicId = 0;
        
        try
        {
            long result = _processHook!.OriginalFunction(a1, a2, a3, a4);
            
            if (!IsEnabled) return result;
            
            // Process remaining injections at end of group
            if (_pendingInjections.Count > 0)
            {
                _isProcessingInjections = true;
                try
                {
                    foreach (var entry in _pendingInjections)
                    {
                        _shell.LogWriteLine("MagicProcessor", $"[INJECTOR] Injecting Op {entry.OperationId} Prop {entry.PropertyId} (End of Group)", color: Color.Green);
                        PerformInjection(a1, entry);
                    }
                    _pendingInjections.Clear();
                }
                finally
                {
                    _isProcessingInjections = false;
                }
            }
            
            // Inject end-of-group properties (InjectAfterOp == -1)
            if (_activeInstanceEntries != null)
            {
                foreach (var entry in _activeInstanceEntries)
                {
                    if (entry.Type == MagicModificationType.AddProperty && entry.InjectAfterOp == -1)
                    {
                        PerformInjection(a1, entry);
                    }
                }
            }
            
            return result;
        }
        finally
        {
            _activeInstanceEntries = null;
            _activeInstanceMagicId = 0;
        }
    }
    
    private long MagicFileHandleSubEntryImpl(long a1, long a2, long a3, long a4)
    {
        if (IsEnabled)
        {
            int opType = (int)a2;
            CheckOpChange(a1, opType);
        }
        return _handleSubEntryHook!.OriginalFunction(a1, a2, a3, a4);
    }
    
    private void CreateOperationAndApplyPropertiesImpl(long magicFileInstance, int opType, int propertyId, long dataPtr)
    {
        // Notify listeners of the magic factory
        OnMagicFactoryUpdated?.Invoke(magicFileInstance);
        
        if (!IsEnabled)
        {
            _createOpHook!.OriginalFunction(magicFileInstance, opType, propertyId, dataPtr);
            return;
        }
        
        var (magicId, groupId) = ResolveIds(magicFileInstance);
        
        // Activate queued entries
        if (_activeInstanceEntries == null && (magicId != 0 || groupId != 0))
        {
            var key = (magicId, groupId);
            if (_groupedQueues.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                _activeInstanceEntries = queue.Dequeue();
                _activeInstanceMagicId = magicId;
                _shell.LogWriteLine("MagicProcessor", $"[ACTIVATE] Linked {_activeInstanceEntries.Count} mods to Magic {magicId} Group {groupId}", color: Color.Green);
            }
        }
        
        CheckOpChange(magicFileInstance, opType);
        
        // Track occurrences
        long propKey = ((long)opType << 32) | (uint)propertyId;
        int propOccurrence = _propInstanceTracker.GetValueOrDefault(propKey, 0);
        _propInstanceTracker[propKey] = propOccurrence + 1;
        
        int opOccurrence = _opInstanceTracker.GetValueOrDefault(opType, 0) - 1;
        if (opOccurrence < 0) opOccurrence = 0;
        
        // Check for RemoveOperation
        if (_activeInstanceEntries != null)
        {
            foreach (var entry in _activeInstanceEntries)
            {
                if (entry.Type == MagicModificationType.RemoveOperation && 
                    entry.OperationId == opType &&
                    (entry.Occurrence == 0 || entry.Occurrence == opOccurrence))
                {
                    _shell.LogWriteLine("MagicProcessor", $"[REMOVE] Op {opType} Prop {propertyId} DISABLED", color: Color.Red);
                    return;
                }
            }
        }
        
        // Check for property modifications
        long valuePtr = *(long*)(dataPtr + 8);
        bool modified = false;
        object? originalValue = null;
        MagicModification? activeEntry = null;
        
        if (_activeInstanceEntries != null)
        {
            foreach (var entry in _activeInstanceEntries)
            {
                if (entry.Type == MagicModificationType.SetProperty &&
                    entry.OperationId == opType &&
                    entry.PropertyId == propertyId &&
                    (entry.Occurrence == 0 || entry.Occurrence == propOccurrence))
                {
                    originalValue = ReadValue(valuePtr, entry.Value);
                    WriteValue(valuePtr, entry.Value);
                    modified = true;
                    activeEntry = entry;
                    _shell.LogWriteLine("MagicProcessor", $"[SET] Magic {magicId} Op {opType} Prop {propertyId} = {entry.Value}", color: Color.Cyan);
                    break;
                }
                
                if (entry.Type == MagicModificationType.RemoveProperty &&
                    entry.OperationId == opType &&
                    entry.PropertyId == propertyId &&
                    (entry.Occurrence == 0 || entry.Occurrence == propOccurrence))
                {
                    _shell.LogWriteLine("MagicProcessor", $"[REMOVE] Magic {magicId} Op {opType} Prop {propertyId}", color: Color.Red);
                    return;
                }
            }
        }
        
        // Call original
        _createOpHook!.OriginalFunction(magicFileInstance, opType, propertyId, dataPtr);
        
        // Restore original value
        if (modified && originalValue != null)
        {
            WriteValue(valuePtr, originalValue);
        }
    }
    
    // ========================================
    // HELPERS
    // ========================================
    
    private void CheckOpChange(long magicFileInstance, int opType)
    {
        if (opType == _lastOpType) return;
        
        // Track new operation
        int occurrence = _opInstanceTracker.GetValueOrDefault(opType, 0);
        _opInstanceTracker[opType] = occurrence + 1;
        
        // Process pending injections for the previous op
        if (_pendingInjections.Count > 0 && !_isProcessingInjections)
        {
            _isProcessingInjections = true;
            try
            {
                foreach (var entry in _pendingInjections.ToList())
                {
                    PerformInjection(magicFileInstance, entry);
                }
                _pendingInjections.Clear();
            }
            finally
            {
                _isProcessingInjections = false;
            }
        }
        
        // Queue injections for after this op
        if (_activeInstanceEntries != null)
        {
            foreach (var entry in _activeInstanceEntries)
            {
                if (entry.Type == MagicModificationType.AddProperty && 
                    entry.InjectAfterOp == _lastOpType && 
                    entry.InjectAfterOp > 0)
                {
                    _pendingInjections.Add(entry);
                }
                else if (entry.Type == MagicModificationType.AddOperation && 
                         entry.InjectAfterOp == _lastOpType &&
                         entry.InjectAfterOp > 0)
                {
                    _pendingInjections.Add(entry);
                }
            }
        }
        
        _lastOpType = opType;
    }
    
    private void PerformInjection(long magicFileInstance, MagicModification entry)
    {
        byte* buffer = stackalloc byte[16];
        long* fakeData = stackalloc long[2];
        fakeData[0] = 0;
        fakeData[1] = (long)buffer;
        
        WriteValue((long)buffer, entry.Value);
        
        _shell.LogWriteLine("MagicProcessor", $"[INJECT] Op {entry.OperationId} Prop {entry.PropertyId} = {entry.Value}", color: Color.Green);
        _createOpHook!.OriginalFunction(magicFileInstance, entry.OperationId, entry.PropertyId, (long)fakeData);
    }
    
    private (int magicId, int groupId) ResolveIds(long magicFileInstance)
    {
        if (!PointerValidation.IsValidPointer(magicFileInstance)) return (0, 0);
        
        var instance = (MagicFileInstance*)magicFileInstance;
        if (!instance->IsValid) return (0, 0);
        
        return (instance->MagicId, instance->GroupId);
    }
    
    private static object? ReadValue(long valuePtr, object? newValue)
    {
        if (valuePtr == 0) return null;
        
        return newValue switch
        {
            float => *(float*)valuePtr,
            int => *(int*)valuePtr,
            Vector3 => *(Vector3*)valuePtr,
            _ => *(int*)valuePtr
        };
    }
    
    private static void WriteValue(long valuePtr, object? value)
    {
        if (valuePtr == 0 || value == null) return;
        
        switch (value)
        {
            case float f:
                *(float*)valuePtr = f;
                break;
            case int i:
                *(int*)valuePtr = i;
                break;
            case Vector3 v:
                *(Vector3*)valuePtr = v;
                break;
        }
    }
}
