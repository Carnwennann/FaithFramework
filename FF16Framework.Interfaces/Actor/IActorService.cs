using System.Numerics;

namespace FF16Framework.Interfaces.Actor;

/// <summary>
/// Actor service interface providing player info, targeting, and actor lookups.
/// Facade over EntityManagerHooks for easier consumption by other services.
/// </summary>
public interface IActorService
{
    /// <summary>
    /// Returns true if the actor system is initialized and ready to use.
    /// </summary>
    bool IsInitialized { get; }
    
    /// <summary>
    /// Returns true if targeting functions are available.
    /// </summary>
    bool HasTargetingFunctions { get; }
    
    // ========================================
    // PLAYER
    // ========================================
    
    /// <summary>
    /// Gets the player's (Clive's) actor pointer.
    /// Returns nint.Zero if not available.
    /// </summary>
    nint GetPlayerActor();
    
    /// <summary>
    /// Gets the player's StaticActorInfo pointer.
    /// Returns nint.Zero if not available.
    /// </summary>
    nint GetPlayerStaticActorInfo();
    
    /// <summary>
    /// Gets the player's current world position.
    /// </summary>
    Vector3 GetPlayerPosition();
    
    /// <summary>
    /// Gets the player's current forward direction vector.
    /// </summary>
    Vector3 GetPlayerForward();
    
    // ========================================
    // TARGETING
    // ========================================
    
    /// <summary>
    /// Gets the currently soft-locked or hard-locked target actor.
    /// Returns nint.Zero if no target is locked.
    /// </summary>
    nint GetLockedTarget();
    
    /// <summary>
    /// Gets the game's internal TargetStruct for the currently locked enemy.
    /// This contains properly calculated body position for accurate targeting.
    /// Returns null if no target is locked.
    /// </summary>
    unsafe nint GetLockedTargetStruct();
    
    // ========================================
    // ACTOR QUERIES
    // ========================================
    
    /// <summary>
    /// Gets a StaticActorInfo by actor ID.
    /// </summary>
    /// <param name="actorId">The actor ID to look up.</param>
    /// <returns>StaticActorInfo pointer, or nint.Zero if not found.</returns>
    nint GetStaticActorInfo(uint actorId);
    
    /// <summary>
    /// Gets the world position of an actor.
    /// </summary>
    /// <param name="staticActorInfo">The StaticActorInfo pointer.</param>
    /// <returns>The actor's world position.</returns>
    Vector3 GetActorPosition(nint staticActorInfo);
    
    /// <summary>
    /// Gets the rotation of an actor as Euler angles.
    /// </summary>
    /// <param name="staticActorInfo">The StaticActorInfo pointer.</param>
    /// <returns>The actor's rotation as Euler angles.</returns>
    Vector3 GetActorRotation(nint staticActorInfo);
    
    /// <summary>
    /// Gets the forward direction vector of an actor.
    /// </summary>
    /// <param name="staticActorInfo">The StaticActorInfo pointer.</param>
    /// <returns>The actor's forward direction vector.</returns>
    Vector3 GetActorForward(nint staticActorInfo);
    
    /// <summary>
    /// Checks if an actor is valid.
    /// </summary>
    /// <param name="staticActorInfo">The StaticActorInfo pointer.</param>
    /// <returns>True if the actor is valid.</returns>
    bool IsActorValid(nint staticActorInfo);
}
