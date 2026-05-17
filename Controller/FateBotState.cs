namespace FateWalker.Controller;

public enum FateBotState
{
    Stopped,        // /fwalk stop, or never started
    Selecting,      // looking for the next FATE
    Teleporting,    // Lifestream cross-zone teleport in flight; wait for territory change
    Mounting,       // calling Mount Roulette, waiting for mounted condition
    Traveling,      // vnavmesh moving us toward target position (FATE center or start NPC)
    Interacting,    // FATE is Preparing — dismount + talk to MotivationNpc to start it
    Engaging,       // inside the FATE, BossMod preset active, fighting
    Dying,          // player is Unconscious — wait for raise, else click Return after grace
    PreparingPause, // safety prep before Paused: flee combat → teleport to nearest aetheryte → Pause
    Paused,         // auto-pause (chat-stop pause or session-cap macro-break) — resumes after timer
    Repairing,      // gear durability low — teleport to in-zone Mender, interact, click Repair All
    Trading,        // gem cap nearing — teleport to vendor, buy items from shopping list
    Recovering,     // FATE ended; cooldown before re-selecting
}
