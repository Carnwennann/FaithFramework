using System.Numerics;

using FF16Framework.Faith.Hooks;
using FF16Framework.Faith.Structs;
using FF16Framework.Interfaces.Actor;

namespace FF16Framework.Services.Actor;

/// <summary>
/// Actor service implementation providing player info, targeting, and actor lookups.
/// Facade over EntityManagerHooks and UnkList35Hooks.
/// </summary>
public unsafe class ActorService : IActorService
{
    private readonly EntityManagerHooks _entityHooks;
    private readonly UnkList35Hooks _list35Hooks;

    public ActorService(EntityManagerHooks entityHooks, UnkList35Hooks list35Hooks)
    {
        _entityHooks = entityHooks;
        _list35Hooks = list35Hooks;
    }

    /// <inheritdoc/>
    public bool IsInitialized =>
        _entityHooks.UnkSingletonPlayerOrCameraRelated != 0 &&
        _entityHooks.StaticActorManager != 0 &&
        _entityHooks.ActorManager != null;

    /// <inheritdoc/>
    public bool HasTargetingFunctions =>
        _list35Hooks.UnkSingletonPlayer_GetList35EntryFunction != null &&
        _list35Hooks.UnkList35Entry_GetCurrentTargettedEnemyFunction != null;

    // ========================================
    // PLAYER
    // ========================================

    /// <inheritdoc/>
    public nint GetPlayerActor()
    {
        if (!IsInitialized) return nint.Zero;
        
        // Get player actor ID from the singleton at offset 0xC8
        uint playerId = *(uint*)(_entityHooks.UnkSingletonPlayerOrCameraRelated + 0xC8);
        if (playerId == 0) return nint.Zero;
        
        var actorRef = _entityHooks.ActorManager_GetActorByKeyFunction(_entityHooks.ActorManager, playerId);
        return actorRef != null ? (nint)actorRef : nint.Zero;
    }

    /// <inheritdoc/>
    public nint GetPlayerStaticActorInfo()
    {
        if (!IsInitialized) return nint.Zero;
        
        uint playerId = *(uint*)(_entityHooks.UnkSingletonPlayerOrCameraRelated + 0xC8);
        if (playerId == 0) return nint.Zero;
        
        return GetStaticActorInfo(playerId);
    }

    /// <inheritdoc/>
    public Vector3 GetPlayerPosition()
    {
        nint playerInfo = GetPlayerStaticActorInfo();
        if (playerInfo == nint.Zero) return Vector3.Zero;
        return GetActorPosition(playerInfo);
    }

    /// <inheritdoc/>
    public Vector3 GetPlayerForward()
    {
        nint playerInfo = GetPlayerStaticActorInfo();
        if (playerInfo == nint.Zero) return new Vector3(0, 0, 1);
        return GetActorForward(playerInfo);
    }

    // ========================================
    // TARGETING
    // ========================================

    /// <inheritdoc/>
    public nint GetLockedTarget()
    {
        var targetStruct = _list35Hooks.GetTargettedEnemy();
        if (targetStruct == null) return nint.Zero;
        
        int actorId = targetStruct->ActorId;
        if (actorId == 0) return nint.Zero;
        
        return GetStaticActorInfo((uint)actorId);
    }

    /// <inheritdoc/>
    public nint GetLockedTargetStruct()
    {
        var targetStruct = _list35Hooks.GetTargettedEnemy();
        return (nint)targetStruct;
    }

    // ========================================
    // ACTOR QUERIES
    // ========================================

    /// <inheritdoc/>
    public nint GetStaticActorInfo(uint actorId)
    {
        if (_entityHooks.StaticActorManager == 0) return nint.Zero;
        if (_entityHooks.StaticActorManager_GetOrCreateHook == null) return nint.Zero;
        
        nint* outInfo = null;
        nint result = _entityHooks.StaticActorManager_GetOrCreateHook.OriginalFunction(
            _entityHooks.StaticActorManager, &outInfo, actorId);
        
        return outInfo != null ? (nint)outInfo : nint.Zero;
    }

    /// <inheritdoc/>
    public Vector3 GetActorPosition(nint staticActorInfo)
    {
        if (staticActorInfo == nint.Zero) return Vector3.Zero;
        if (_entityHooks.GetPositionFunction == null) return Vector3.Zero;
        
        NodePositionPair result = default;
        _entityHooks.GetPositionFunction(staticActorInfo, &result);
        return result.Position;
    }

    /// <inheritdoc/>
    public Vector3 GetActorRotation(nint staticActorInfo)
    {
        if (staticActorInfo == nint.Zero) return Vector3.Zero;
        if (_entityHooks.GetRotationFunction == null) return Vector3.Zero;
        
        Vector3 result = default;
        _entityHooks.GetRotationFunction(staticActorInfo, &result);
        return result;
    }

    /// <inheritdoc/>
    public Vector3 GetActorForward(nint staticActorInfo)
    {
        if (staticActorInfo == nint.Zero) return new Vector3(0, 0, 1);
        if (_entityHooks.GetForwardVectorFunction == null) return new Vector3(0, 0, 1);
        
        Vector3 result = default;
        _entityHooks.GetForwardVectorFunction(staticActorInfo, &result);
        return result;
    }

    /// <inheritdoc/>
    public bool IsActorValid(nint staticActorInfo)
    {
        if (staticActorInfo == nint.Zero) return false;
        if (_entityHooks.HasEntityDataFunction == null) return false;
        
        return _entityHooks.HasEntityDataFunction(staticActorInfo) != 0;
    }
}
