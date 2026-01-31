using System.Numerics;
using System.Runtime.InteropServices;

using FF16Framework.Faith.Hooks;
using FF16Framework.Faith.Structs;
using FF16Framework.Interfaces.Actor;

using NenTools.ImGui.Interfaces.Shell;

using Reloaded.Hooks.Definitions;
using Reloaded.Mod.Interfaces;

using RyoTune.Reloaded;

namespace FF16Framework.Services.Magic;

/// <summary>
/// Casting engine for magic spells.
/// Handles SetupMagic, CastMagic, and FireMagicProjectile hooks.
/// </summary>
public unsafe class MagicCaster : HookGroupBase
{
    // ========================================
    // DELEGATES
    // ========================================
    
    public delegate long SetupMagicDelegate(
        long battleMagicPtr, int magicId, long casterActorRef, 
        long positionStruct, int commandId, int actionId, byte flag);
    
    public delegate char CastMagicDelegate(long a1, long unkMagicStructPtr);
    
    public delegate char FireMagicProjectileDelegate(long magicManagerPtr, long projectileDataPtr);
    
    public delegate long* UnkTargetStruct_CreateDelegate(long manager, long* outResult);
    
    // ========================================
    // HOOKS
    // ========================================
    
    private IHook<SetupMagicDelegate>? _setupMagicHook;
    private IHook<CastMagicDelegate>? _castMagicHook;
    private IHook<FireMagicProjectileDelegate>? _fireMagicProjectileHook;
    private IHook<UnkTargetStruct_CreateDelegate>? _unkTargetStructCreateHook;
    private CastMagicDelegate? _insertNewMagicWrapper;  // Direct wrapper, not through hook
    
    // ========================================
    // CACHED CONTEXT
    // ========================================
    
    private long _cachedCasterActorRef = 0;
    private long _cachedPositionStruct = 0;
    private nint _cachedTargetVTable = 0;
    private int _cachedCommandId = 0;
    private int _cachedActionId = 0;
    private byte _cachedFlag = 0;
    private long _cachedExecutorClient = 0;
    private nint _executorClientGlobalAddress = 0;  // Address of the global ExecutorClient singleton
    private readonly nint _baseAddress;
    private bool _hasMagicContext = false;
    
    // Buffer pool for allocations
    private readonly List<IntPtr> _allocatedMagicBuffers = new();
    private readonly List<IntPtr> _allocatedTargetBuffers = new();
    private const int MAX_BUFFER_POOL_SIZE = 32;
    private const int TARGET_STRUCT_SIZE = 0x7C;
    private const int MAGIC_STRUCT_SIZE = 0x108;
    
    private const int DEFAULT_COMMAND_ID = 101;
    private const int DEFAULT_ACTION_ID = 218;
    private const byte DEFAULT_FLAG = 1;
    
    // StaticActorInfo offsets
    private const int ACTOR_REF_OFFSET = 0x58;  // ActorRef within StaticActorInfo
    
    // Hardcoded global offset from TrulyEikonicSpells (fallback if dynamic extraction fails)
    private const int GLOBAL_OFFSET_BATTLE_MAGIC_EXECUTOR = 0x18168E8;
    
    // ========================================
    // DEPENDENCIES
    // ========================================
    
    private readonly IImGuiShell _shell;
    private readonly IActorService _actorService;
    private readonly UnkList35Hooks _list35Hooks;
    private readonly MagicHooks _magicHooks;
    
    // ========================================
    // CALLBACKS
    // ========================================
    
    public Func<int>? GetActiveEikon { get; set; }
    public Func<int, bool>? OnChargedShotDetected { get; set; }
    
    // ========================================
    // PROPERTIES
    // ========================================
    
    public bool IsReady => _setupMagicHook != null && _insertNewMagicWrapper != null;
    
    public MagicCaster(Config config, IModConfig modConfig, ILogger logger, 
        IImGuiShell shell, IActorService actorService, UnkList35Hooks list35Hooks, MagicHooks magicHooks)
        : base(config, modConfig, logger)
    {
        _shell = shell;
        _actorService = actorService;
        _list35Hooks = list35Hooks;
        _magicHooks = magicHooks;
        _baseAddress = System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress;
    }
    
    // ========================================
    // SETUP
    // ========================================
    
    public override void SetupHooks()
    {
        Project.Scans.AddScanHook("SetupMagic", (result, hooks) =>
        {
            _setupMagicHook = hooks.CreateHook<SetupMagicDelegate>(SetupMagicImpl, result).Activate();
            _logger.WriteLine($"[MagicCaster] Hooked SetupMagic at 0x{result:X}");
        });
        
        Project.Scans.AddScanHook("CastMagic", (result, hooks) =>
        {
            _castMagicHook = hooks.CreateHook<CastMagicDelegate>(CastMagicImpl, result).Activate();
            
            // Extract ExecutorClient global address from the instruction at end of signature
            // The signature ends at "48 8B 0D" which is "mov rcx, [rip+offset]"
            // The next 4 bytes are the RIP-relative offset to the ExecutorClient singleton
            try
            {
                // Count bytes in CastMagic signature to find "48 8B 0D":
                // 48 89 5C 24 10 (5) + 48 89 74 24 18 (5) + 57 (1) + 48 83 EC 20 (4) + 48 8B 41 10 (4) + 48 8B F2 (3) = 0x16
                nint instructionAddress = result + 0x16; // Offset to "48 8B 0D" in the signature
                int ripOffset = *(int*)(instructionAddress + 3); // Read the 4-byte offset after 48 8B 0D
                _executorClientGlobalAddress = instructionAddress + 7 + ripOffset; // RIP + instruction size (7) + offset
                _logger.WriteLine($"[MagicCaster] ExecutorClient global at 0x{_executorClientGlobalAddress:X}");
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[MagicCaster] Failed to extract ExecutorClient global: {ex.Message}");
            }
            
            _logger.WriteLine($"[MagicCaster] Hooked CastMagic at 0x{result:X}");
        });
        
        Project.Scans.AddScanHook("FireMagicProjectile", (result, hooks) =>
        {
            _fireMagicProjectileHook = hooks.CreateHook<FireMagicProjectileDelegate>(FireMagicProjectileImpl, result).Activate();
            _logger.WriteLine($"[MagicCaster] Hooked FireMagicProjectile at 0x{result:X}");
        });
        
        Project.Scans.AddScanHook("UnkTargetStruct_Create", (result, hooks) =>
        {
            _unkTargetStructCreateHook = hooks.CreateHook<UnkTargetStruct_CreateDelegate>(UnkTargetStructCreateImpl, result).Activate();
            _logger.WriteLine($"[MagicCaster] Hooked UnkTargetStruct_Create at 0x{result:X}");
        });
        
        // Create DIRECT wrapper for InsertNewMagic (not through hook - just call the function)
        Project.Scans.AddScanHook("InsertNewMagic", (result, hooks) =>
        {
            _insertNewMagicWrapper = hooks.CreateWrapper<CastMagicDelegate>(result, out _);
            _logger.WriteLine($"[MagicCaster] Created InsertNewMagic wrapper at 0x{result:X}");
        });
    }
    
    // ========================================
    // PUBLIC API
    // ========================================
    
    /// <summary>
    /// Casts a magic spell with optional source and target.
    /// </summary>
    public bool Cast(int magicId, nint? sourceActor = null, nint? targetActor = null)
    {
        if (!IsReady)
        {
            _shell.LogWriteLine("MagicCaster", "Not ready - waiting for magic context", color: Color.Red);
            return false;
        }
        
        // Get source actor - need ActorRef, not StaticActorInfo pointer!
        long casterRef = _cachedCasterActorRef;
        if (sourceActor.HasValue && sourceActor.Value != 0)
        {
            casterRef = GetActorRef(sourceActor.Value);
        }
        else if (casterRef == 0)
        {
            var playerInfo = _actorService.GetPlayerActor();
            if (playerInfo != 0)
                casterRef = GetActorRef(playerInfo);
        }
        
        if (casterRef == 0)
        {
            _shell.LogWriteLine("MagicCaster", "No caster ActorRef available", color: Color.Red);
            return false;
        }
        
        // Build target position
        Vector3 targetPosition;
        if (targetActor.HasValue)
        {
            if (targetActor.Value == nint.Zero)
            {
                // No target - use player forward
                var playerPos = _actorService.GetPlayerPosition();
                var playerFwd = _actorService.GetPlayerForward();
                targetPosition = playerPos + playerFwd * 10f;
            }
            else
            {
                targetPosition = _actorService.GetActorPosition(targetActor.Value);
            }
        }
        else
        {
            // Use locked target
            var lockedTarget = _actorService.GetLockedTarget();
            if (lockedTarget != nint.Zero)
            {
                targetPosition = _actorService.GetActorPosition(lockedTarget);
            }
            else
            {
                var playerPos = _actorService.GetPlayerPosition();
                var playerFwd = _actorService.GetPlayerForward();
                targetPosition = playerPos + playerFwd * 10f;
            }
        }
        
        return CastAtPosition(magicId, casterRef, targetPosition);
    }
    
    /// <summary>
    /// Casts using the game's own TargetStruct for proper body targeting.
    /// </summary>
    public bool CastWithGameTarget(int magicId, nint? sourceActor = null)
    {
        if (!IsReady)
        {
            _shell.LogWriteLine("MagicCaster", "Not ready - waiting for magic context", color: Color.Red);
            return false;
        }
        
        var targetStruct = _list35Hooks.GetTargettedEnemy();
        if (targetStruct == null)
        {
            _shell.LogWriteLine("MagicCaster", "No locked target - falling back to standard cast", color: Color.Yellow);
            return Cast(magicId, sourceActor, null);
        }
        
        // Get source actor - need ActorRef, not StaticActorInfo pointer!
        long casterRef = _cachedCasterActorRef;
        if (sourceActor.HasValue && sourceActor.Value != 0)
        {
            casterRef = GetActorRef(sourceActor.Value);
        }
        else if (casterRef == 0)
        {
            var playerInfo = _actorService.GetPlayerActor();
            if (playerInfo != 0)
                casterRef = GetActorRef(playerInfo);
        }
        
        if (casterRef == 0)
        {
            _shell.LogWriteLine("MagicCaster", "No caster ActorRef available", color: Color.Red);
            return false;
        }
        
        // Allocate magic struct
        nint magicBuffer = AllocateMagicBuffer();
        NativeMemory.Clear((void*)magicBuffer, MAGIC_STRUCT_SIZE);
        
        _logger.WriteLine($"[MagicCaster] CastWithGameTarget: MagicId={magicId}, CasterRef=0x{casterRef:X}, TargetVTable=0x{(nint)targetStruct->vtable:X}");
        
        try
        {
            _shell.LogWriteLine("MagicCaster", $"Casting magic {magicId} with game target (Y={targetStruct->Position.Y:F2})", color: Color.Green);
            
            _logger.WriteLine($"[MagicCaster] Calling SetupMagic: magicBuffer=0x{magicBuffer:X}, targetStruct=0x{(nint)targetStruct:X}");
            
            // SetupMagic fills the magicBuffer with the spell data
            // Use explicit (long) casts like TrulyEikonicSpells does
            _setupMagicHook!.OriginalFunction(
                (long)magicBuffer, magicId, casterRef, (long)targetStruct,
                _cachedCommandId != 0 ? _cachedCommandId : DEFAULT_COMMAND_ID,
                _cachedActionId != 0 ? _cachedActionId : DEFAULT_ACTION_ID,
                _cachedFlag != 0 ? _cachedFlag : DEFAULT_FLAG);
            
            _logger.WriteLine($"[MagicCaster] SetupMagic completed successfully");
            
            // Get ExecutorClient and insert the magic DIRECTLY (not through hook)
            var executorClient = GetExecutorClient();
            if (executorClient != 0 && _insertNewMagicWrapper != null)
            {
                _logger.WriteLine($"[MagicCaster] Calling InsertNewMagic: executor=0x{executorClient:X}, magic=0x{magicBuffer:X}");
                _insertNewMagicWrapper((long)executorClient, (long)magicBuffer);
                _logger.WriteLine($"[MagicCaster] InsertNewMagic completed successfully");
                return true;
            }
            else
            {
                _shell.LogWriteLine("MagicCaster", "No executor client available", color: Color.Yellow);
            }
        }
        catch (Exception ex)
        {
            _shell.LogWriteLine("MagicCaster", $"Cast failed: {ex.Message}", color: Color.Red);
        }
        
        return false;
    }
    
    /// <summary>
    /// Registers a charged shot handler.
    /// </summary>
    public void RegisterChargedShotHandler(Func<int, bool> handler)
    {
        OnChargedShotDetected = handler;
    }
    
    /// <summary>
    /// Gets the ActorRef from a StaticActorInfo pointer.
    /// The ActorRef is the internal game reference used by SetupMagic.
    /// </summary>
    private long GetActorRef(nint staticActorInfo)
    {
        if (staticActorInfo == 0)
            return 0;
            
        try
        {
            // ActorRef is at offset 0x58 within StaticActorInfo
            long actorRef = *(long*)(staticActorInfo + ACTOR_REF_OFFSET);
            _logger.WriteLine($"[MagicCaster] GetActorRef: StaticActorInfo=0x{staticActorInfo:X} -> ActorRef=0x{actorRef:X}");
            return actorRef;
        }
        catch
        {
            _logger.WriteLine($"[MagicCaster] Failed to read ActorRef from 0x{staticActorInfo:X}");
            return 0;
        }
    }
    
    /// <summary>
    /// Gets the ExecutorClient from the global singleton or cache.
    /// </summary>
    private nint GetExecutorClient()
    {
        // Try to read from dynamically extracted global address first
        if (_executorClientGlobalAddress != 0)
        {
            try
            {
                nint executorFromGlobal = *(nint*)_executorClientGlobalAddress;
                if (executorFromGlobal != 0)
                {
                    _logger.WriteLine($"[MagicCaster] ExecutorClient from dynamic global: 0x{executorFromGlobal:X}");
                    return executorFromGlobal;
                }
            }
            catch { /* Ignore read errors */ }
        }
        
        // Try hardcoded global offset (TrulyEikonicSpells approach)
        try
        {
            nint executorFromHardcoded = *(nint*)(_baseAddress + GLOBAL_OFFSET_BATTLE_MAGIC_EXECUTOR);
            if (executorFromHardcoded != 0)
            {
                _logger.WriteLine($"[MagicCaster] ExecutorClient from hardcoded global: 0x{executorFromHardcoded:X}");
                return executorFromHardcoded;
            }
        }
        catch { /* Ignore read errors */ }
        
        // Fall back to cached values from MagicHooks
        if (_magicHooks.CachedExecutorClient != 0)
        {
            _logger.WriteLine($"[MagicCaster] ExecutorClient from MagicHooks cache: 0x{_magicHooks.CachedExecutorClient:X}");
            return _magicHooks.CachedExecutorClient;
        }
        
        // Fall back to local cache
        if (_cachedExecutorClient != 0)
        {
            _logger.WriteLine($"[MagicCaster] ExecutorClient from local cache: 0x{_cachedExecutorClient:X}");
            return (nint)_cachedExecutorClient;
        }
        
        _logger.WriteLine($"[MagicCaster] No ExecutorClient available!");
        return 0;
    }
    
    // ========================================
    // HOOK IMPLEMENTATIONS
    // ========================================
    
    private long SetupMagicImpl(long battleMagicPtr, int magicId, long casterActorRef, 
        long positionStruct, int commandId, int actionId, byte flag)
    {
        // Cache context
        _cachedCasterActorRef = casterActorRef;
        _cachedPositionStruct = positionStruct;
        _cachedCommandId = commandId;
        _cachedActionId = actionId;
        _cachedFlag = flag;
        
        // Cache target VTable (only log on first capture)
        if (positionStruct != 0)
        {
            var targetVTable = *(nint*)positionStruct;
            if (targetVTable != 0 && _cachedTargetVTable == 0)
            {
                _cachedTargetVTable = targetVTable;
                _logger.WriteLine($"[MagicCaster] Captured VTable from SetupMagic: 0x{_cachedTargetVTable:X}");
            }
        }
        
        _hasMagicContext = true;
        
        return _setupMagicHook!.OriginalFunction(battleMagicPtr, magicId, casterActorRef, positionStruct, commandId, actionId, flag);
    }
    
    private char CastMagicImpl(long a1, long unkMagicStructPtr)
    {
        _cachedExecutorClient = a1;
        return _castMagicHook!.OriginalFunction(a1, unkMagicStructPtr);
    }
    
    private char FireMagicProjectileImpl(long magicManagerPtr, long projectileDataPtr)
    {
        // Check for charged shot interception
        if (OnChargedShotDetected != null && GetActiveEikon != null)
        {
            int shotType = MagicManagerHelper.GetShotTypeFromManager(magicManagerPtr);
            if (shotType == (int)MagicShotType.Charged)
            {
                int activeEikon = GetActiveEikon();
                if (OnChargedShotDetected(activeEikon))
                {
                    _shell.LogWriteLine("MagicCaster", $"Charged shot intercepted for Eikon {activeEikon}", color: Color.Yellow);
                    return (char)0;
                }
            }
        }
        
        return _fireMagicProjectileHook!.OriginalFunction(magicManagerPtr, projectileDataPtr);
    }
    
    private long* UnkTargetStructCreateImpl(long manager, long* outResult)
    {
        var result = _unkTargetStructCreateHook!.OriginalFunction(manager, outResult);
        
        // Cache VTable from created struct
        if (result != null && *result != 0)
        {
            long structAddr = *result;
            nint vtable = *(nint*)structAddr;
            if (vtable != 0 && _cachedTargetVTable == 0)
            {
                _cachedTargetVTable = vtable;
                _logger.WriteLine($"[MagicCaster] Captured VTable from UnkTargetStruct::Create: 0x{_cachedTargetVTable:X}");
            }
        }
        
        return result;
    }
    
    // ========================================
    // INTERNAL HELPERS
    // ========================================
    
    private bool CastAtPosition(int magicId, long casterRef, Vector3 position)
    {
        // CRITICAL: VTable is required for TargetStruct to work
        // It must be captured from the game casting a spell first
        if (_cachedTargetVTable == 0)
        {
            _shell.LogWriteLine("MagicCaster", "ERROR: No VTable available. Cast a spell normally first to capture VTable.", color: Color.Red);
            return false;
        }
        
        // Allocate buffers
        nint magicBuffer = AllocateMagicBuffer();
        nint targetBuffer = AllocateTargetBuffer();
        
        NativeMemory.Clear((void*)magicBuffer, MAGIC_STRUCT_SIZE);
        NativeMemory.Clear((void*)targetBuffer, TARGET_STRUCT_SIZE);
        
        // Setup target struct
        var target = (TargetStruct*)targetBuffer;
        target->VTable = _cachedTargetVTable;
        target->Position = position;
        target->Direction = new Vector3(0, 0, 1);
        target->Type = 0;
        
        _logger.WriteLine($"[MagicCaster] CastAtPosition: MagicId={magicId}, CasterRef=0x{casterRef:X}, VTable=0x{_cachedTargetVTable:X}, Pos={position}");
        
        try
        {
            _shell.LogWriteLine("MagicCaster", $"Casting magic {magicId} at {position:F2}", color: Color.Green);
            
            _logger.WriteLine($"[MagicCaster] Calling SetupMagic: magicBuffer=0x{magicBuffer:X}, targetBuffer=0x{targetBuffer:X}");
            
            // SetupMagic fills the magicBuffer with the spell data
            // Use explicit (long) casts like TrulyEikonicSpells does
            _setupMagicHook!.OriginalFunction(
                (long)magicBuffer, magicId, casterRef, (long)targetBuffer,
                _cachedCommandId != 0 ? _cachedCommandId : DEFAULT_COMMAND_ID,
                _cachedActionId != 0 ? _cachedActionId : DEFAULT_ACTION_ID,
                _cachedFlag != 0 ? _cachedFlag : DEFAULT_FLAG);
            
            _logger.WriteLine($"[MagicCaster] SetupMagic completed successfully");
            
            // Get ExecutorClient and insert the magic DIRECTLY (not through hook)
            var executorClient = GetExecutorClient();
            if (executorClient != 0 && _insertNewMagicWrapper != null)
            {
                _logger.WriteLine($"[MagicCaster] Calling InsertNewMagic: executor=0x{executorClient:X}, magic=0x{magicBuffer:X}");
                _insertNewMagicWrapper((long)executorClient, (long)magicBuffer);
                _logger.WriteLine($"[MagicCaster] InsertNewMagic completed successfully");
                return true;
            }
            else
            {
                _shell.LogWriteLine("MagicCaster", "No executor client available", color: Color.Yellow);
            }
        }
        catch (Exception ex)
        {
            _shell.LogWriteLine("MagicCaster", $"Cast failed: {ex.Message}", color: Color.Red);
        }
        
        return false;
    }
    
    private nint AllocateMagicBuffer()
    {
        // Prune old buffers if pool is too large
        while (_allocatedMagicBuffers.Count >= MAX_BUFFER_POOL_SIZE)
        {
            var old = _allocatedMagicBuffers[0];
            _allocatedMagicBuffers.RemoveAt(0);
            Marshal.FreeHGlobal(old);
        }
        
        var buffer = Marshal.AllocHGlobal(MAGIC_STRUCT_SIZE);
        _allocatedMagicBuffers.Add(buffer);
        return buffer;
    }
    
    private nint AllocateTargetBuffer()
    {
        while (_allocatedTargetBuffers.Count >= MAX_BUFFER_POOL_SIZE)
        {
            var old = _allocatedTargetBuffers[0];
            _allocatedTargetBuffers.RemoveAt(0);
            Marshal.FreeHGlobal(old);
        }
        
        var buffer = Marshal.AllocHGlobal(TARGET_STRUCT_SIZE);
        _allocatedTargetBuffers.Add(buffer);
        return buffer;
    }
}
