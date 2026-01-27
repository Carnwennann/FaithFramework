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
    private CastMagicDelegate? _castMagicWrapper;
    
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
    
    // ========================================
    // DEPENDENCIES
    // ========================================
    
    private readonly IImGuiShell _shell;
    private readonly IActorService _actorService;
    private readonly UnkList35Hooks _list35Hooks;
    
    // ========================================
    // CALLBACKS
    // ========================================
    
    public Func<int>? GetActiveEikon { get; set; }
    public Func<int, bool>? OnChargedShotDetected { get; set; }
    
    // ========================================
    // PROPERTIES
    // ========================================
    
    public bool IsReady => _setupMagicHook != null && _castMagicWrapper != null && _hasMagicContext;
    
    public MagicCaster(Config config, IModConfig modConfig, ILogger logger, 
        IImGuiShell shell, IActorService actorService, UnkList35Hooks list35Hooks)
        : base(config, modConfig, logger)
    {
        _shell = shell;
        _actorService = actorService;
        _list35Hooks = list35Hooks;
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
            _castMagicWrapper = hooks.CreateWrapper<CastMagicDelegate>(result, out _);
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
        
        // Get source actor
        long casterRef = sourceActor.HasValue ? (long)sourceActor.Value : _cachedCasterActorRef;
        if (casterRef == 0)
        {
            casterRef = (long)_actorService.GetPlayerActor();
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
        
        long casterRef = sourceActor.HasValue ? (long)sourceActor.Value : _cachedCasterActorRef;
        if (casterRef == 0)
        {
            casterRef = (long)_actorService.GetPlayerActor();
        }
        
        // Allocate magic struct
        nint magicBuffer = AllocateMagicBuffer();
        NativeMemory.Clear((void*)magicBuffer, MAGIC_STRUCT_SIZE);
        
        try
        {
            _shell.LogWriteLine("MagicCaster", $"Casting magic {magicId} with game target (Y={targetStruct->Position.Y:F2})", color: Color.Green);
            
            long result = _setupMagicHook!.OriginalFunction(
                magicBuffer, magicId, casterRef, (long)targetStruct,
                _cachedCommandId != 0 ? _cachedCommandId : DEFAULT_COMMAND_ID,
                _cachedActionId != 0 ? _cachedActionId : DEFAULT_ACTION_ID,
                _cachedFlag != 0 ? _cachedFlag : DEFAULT_FLAG);
            
            if (result != 0 && _cachedExecutorClient != 0 && _castMagicWrapper != null)
            {
                _castMagicWrapper(_cachedExecutorClient, result);
                return true;
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
        
        // Cache target VTable
        if (positionStruct != 0)
        {
            var targetVTable = *(nint*)positionStruct;
            if (targetVTable != 0)
            {
                _cachedTargetVTable = targetVTable;
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
            var vtable = *(nint*)(*result);
            if (vtable != 0)
            {
                _cachedTargetVTable = vtable;
            }
        }
        
        return result;
    }
    
    // ========================================
    // INTERNAL HELPERS
    // ========================================
    
    private bool CastAtPosition(int magicId, long casterRef, Vector3 position)
    {
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
        
        try
        {
            _shell.LogWriteLine("MagicCaster", $"Casting magic {magicId} at {position:F2}", color: Color.Green);
            
            long result = _setupMagicHook!.OriginalFunction(
                magicBuffer, magicId, casterRef, targetBuffer,
                _cachedCommandId != 0 ? _cachedCommandId : DEFAULT_COMMAND_ID,
                _cachedActionId != 0 ? _cachedActionId : DEFAULT_ACTION_ID,
                _cachedFlag != 0 ? _cachedFlag : DEFAULT_FLAG);
            
            if (result != 0 && _cachedExecutorClient != 0 && _castMagicWrapper != null)
            {
                _castMagicWrapper(_cachedExecutorClient, result);
                return true;
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
