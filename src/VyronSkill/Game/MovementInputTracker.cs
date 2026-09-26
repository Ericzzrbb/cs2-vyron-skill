using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using VyronSkill.Core;

namespace VyronSkill.Game;

/// <summary>
/// Remembers the order in which the movement keys were pressed.
/// <para>
/// This is what makes "仅4个正方向" (four directions only) feel right: instead of guessing from the
/// current button bits, the most recently pressed key always wins, so a player holding W and then
/// tapping D dashes right, exactly like Delta Force does with the 4-way operator preference.
/// </para>
/// </summary>
internal sealed class MovementInputTracker
{
    private readonly Dictionary<int, List<DirectionKey>> _heldKeys = new();

    public void OnButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        if (player is null || !player.IsValid || player.Slot < 0)
        {
            return;
        }

        Update(player.Slot, pressed, true);
        Update(player.Slot, released, false);
    }

    /// <summary>The movement key the player pressed most recently and is still holding.</summary>
    public DirectionKey ActiveKey(int slot)
        => _heldKeys.TryGetValue(slot, out var keys) && keys.Count > 0 ? keys[^1] : DirectionKey.None;

    public void Clear(int slot) => _heldKeys.Remove(slot);

    public void ClearAll() => _heldKeys.Clear();

    private void Update(int slot, PlayerButtons buttons, bool pressedNow)
    {
        if (buttons == 0)
        {
            return;
        }

        Track(slot, buttons, PlayerButtons.Forward, DirectionKey.Forward, pressedNow);
        Track(slot, buttons, PlayerButtons.Back, DirectionKey.Back, pressedNow);
        Track(slot, buttons, PlayerButtons.Moveleft, DirectionKey.Left, pressedNow);
        Track(slot, buttons, PlayerButtons.Moveright, DirectionKey.Right, pressedNow);
    }

    private void Track(int slot, PlayerButtons buttons, PlayerButtons flag, DirectionKey key, bool pressedNow)
    {
        if ((buttons & flag) == 0)
        {
            return;
        }

        if (!_heldKeys.TryGetValue(slot, out var keys))
        {
            if (!pressedNow)
            {
                return;
            }

            keys = new List<DirectionKey>(4);
            _heldKeys[slot] = keys;
        }

        keys.Remove(key);

        if (pressedNow)
        {
            keys.Add(key);
        }
        else if (keys.Count == 0)
        {
            _heldKeys.Remove(slot);
        }
    }
}
