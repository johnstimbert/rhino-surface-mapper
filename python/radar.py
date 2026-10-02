"""Qt-free radar pulse state machine for Rhino Surface Mapper.

The module models radar coverage expansion using plain Python state. UI and
input code provide time, button state, and context; drawing code consumes the
coverage records stored on ``MapperState``. No PySide6 dependency belongs here.
"""
class RadarPulse:
    """Manage one or more expanding radar waves for the current map context.

    ``waves`` stores tuples of the persisted pulse dictionary, start time, and
    maximum radius. ``armed``/``was_down`` implement press-release semantics so
    holding the trigger cannot repeatedly create pulses. A context change
    invalidates in-flight waves because coverage belongs to a specific map/UI
    context.
    """

    # The game radar pulse expands at 2000 m over 3 seconds.
    SPEED_M_S = 2000.0 / 3.0
    def __init__(self):
        """Initialize an idle radar pulse controller."""
        self.waves = []
        self.was_down = True  # Require a release after entering a new context.
        self.context = None
        self.armed = False

    @property
    def active(self):
        """Return the latest active wave tuple, or ``None`` when idle."""
        return self.waves[-1] if self.waves else None

    def tick(self, state, now, enabled, down, context):
        """Advance radar waves and optionally fire a new pulse.

        Args:
            state: Mapper-like object providing read-only status, Rhino
                coordinates, projection, scanner range, and radar coverage.
            now: Current monotonic time in seconds.
            enabled: Whether radar input should be interpreted in the current
                focus/state.
            down: Trigger state; ``False`` means released, truthy means pressed,
                and ``None`` means unknown because the game lacks focus.
            context: Identity for the active map/input context.

        Returns:
            True when visible radar coverage changed.
        """
        if getattr(state, 'read_only', False):
            changed = bool(self.waves)
            self.waves = []
            self.armed = False
            self.was_down = True
            return changed
        changed = False
        if self.context != context:
            # Context changes invalidate pending waves and require a fresh
            # release before the next press can fire in the new context.
            self.context = context
            self.was_down = True
            self.armed = False
            self.waves = []
        finished = False
        latest = self.active
        remaining = []
        for wave in self.waves:
            pulse, start, limit = wave
            pulse['radius'] = min(limit, max(0, (now-start)*self.SPEED_M_S))
            if now-start >= limit/self.SPEED_M_S - 1e-9:
                pulse['radius'] = limit
            changed = True
            if pulse['radius'] >= limit:
                finished = finished or wave is latest
            else:
                remaining.append(wave)
        self.waves = remaining
        if not enabled:
            # ``None`` means the game does not have focus: do not infer a
            # release. With focus, observe release even while telemetry arrives.
            self.was_down = down is not False
            self.armed = down is False
            return changed
        if not down:
            self.armed = True
        if self.active is None and self.armed and down and (not self.was_down or finished):
            x, y = state.llxy(state.rhino_lat, state.rhino_lon)
            pulse = dict(x=x, y=y, radius=0.0)
            state.radar_coverage.append(pulse)
            self.waves.append((pulse, now, state.scanner_range_m))
            changed = True
        self.was_down = down
        return changed
