using System;
using System.Collections.Generic;
using System.Numerics;

namespace FF16Framework.Interfaces.Magic;

/// <summary>
/// Public interface for the Magic API.
/// Provides spell casting, modification, and targeting functionality.
/// </summary>
public interface IMagicService
{
    // ========================================
    // STATUS
    // ========================================
    
    /// <summary>
    /// Returns true if the magic system has captured the necessary game context to cast spells.
    /// The context is captured automatically when any magic spell is cast in-game.
    /// </summary>
    bool IsReady { get; }
    
    /// <summary>
    /// Gets or sets the modification mode for magic spells.
    /// Default is MemoryMode.
    /// </summary>
    MagicModificationMode Mode { get; set; }
    
    // ========================================
    // SPELL BUILDING
    // ========================================
    
    /// <summary>
    /// Creates a new magic spell builder for the specified magic ID.
    /// </summary>
    /// <param name="magicId">The ID of the magic spell.</param>
    /// <returns>A builder for configuring and casting the spell.</returns>
    IMagicBuilder CreateSpell(int magicId);
    
    /// <summary>
    /// Creates a spell builder from a JSON configuration.
    /// </summary>
    /// <param name="json">JSON string containing spell modifications.</param>
    /// <returns>A builder with the imported modifications, or null if parsing failed.</returns>
    IMagicBuilder? ImportFromJson(string json);
    
    /// <summary>
    /// Creates a spell builder from a JSON file.
    /// </summary>
    /// <param name="filePath">Path to the JSON file.</param>
    /// <returns>A builder with the imported modifications, or null if loading failed.</returns>
    IMagicBuilder? ImportFromFile(string filePath);
    
    // ========================================
    // DIRECT CASTING
    // ========================================
    
    /// <summary>
    /// Casts a magic spell with optional source and target actors.
    /// </summary>
    /// <param name="magicId">The ID of the magic spell to cast.</param>
    /// <param name="sourceActor">
    /// The actor casting the spell. If null, defaults to the player (Clive).
    /// Use nint.Zero to explicitly cast without a source.
    /// </param>
    /// <param name="targetActor">
    /// The target actor for the spell. If null, defaults to the camera's soft/hard locked target.
    /// Use nint.Zero to explicitly cast without a target.
    /// </param>
    /// <returns>True if the spell was cast successfully.</returns>
    bool Cast(int magicId, nint? sourceActor = null, nint? targetActor = null);
    
    /// <summary>
    /// Casts a magic spell using the game's own TargetStruct for proper body targeting.
    /// This is the correct way to get accurate targeting (Y=1.23 vs Y=0.26).
    /// </summary>
    /// <param name="magicId">The ID of the magic spell to cast.</param>
    /// <param name="sourceActor">The actor casting the spell. If null, defaults to player.</param>
    /// <returns>True if the spell was cast successfully.</returns>
    bool CastWithGameTarget(int magicId, nint? sourceActor = null);
    
    // ========================================
    // MODIFICATION QUEUE (RuntimeMode)
    // ========================================
    
    /// <summary>
    /// Enqueues modifications to be applied when a magic spell is processed.
    /// Only used in RuntimeMode.
    /// </summary>
    /// <param name="magicId">The magic ID to apply modifications to.</param>
    /// <param name="modifications">The modifications to apply.</param>
    void EnqueueModifications(int magicId, IEnumerable<MagicModification> modifications);
    
    // ========================================
    // TARGETING
    // ========================================
    
    /// <summary>
    /// Gets the currently soft-locked or hard-locked target actor from the camera system.
    /// Returns nint.Zero if no target is locked.
    /// </summary>
    nint GetLockedTarget();
    
    /// <summary>
    /// Gets the player's (Clive's) actor pointer.
    /// </summary>
    nint GetPlayerActor();
    
    // ========================================
    // CALLBACKS
    // ========================================
    
    /// <summary>
    /// Registers a handler that can intercept and modify charged shots.
    /// The handler receives the active Eikon ID and returns true to suppress the shot.
    /// </summary>
    void RegisterChargedShotHandler(Func<int, bool> handler);
}
