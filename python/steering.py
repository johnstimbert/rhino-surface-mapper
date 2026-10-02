"""Pure steering-assistance decisions for SRV heading correction.

This module is the domain layer for steering assistance: it observes map-space
telemetry supplied by callers and decides whether a short left/right steering
pulse would reduce heading error. It never sends input, never calls native APIs,
and never controls throttle or brake. Positive angular error means the desired
bearing is to the right of the current heading.

Safety contract: each decision consumes at most one fresh sample, suppresses
commands while telemetry is stale or unstable, and treats all pulse durations as
empirical trial values rather than a vehicle-dynamics model.
"""
import math
from i18n import translate


STATUS_STOPPED = 'stopped'
STATUS_WAITING = 'waiting'
STATUS_SLOWING = 'slowing'
STATUS_CORRECTING = 'correcting'
STATUS_ON_COURSE = 'on_course'
STATUS_EASING = 'easing'
WAIT_UNSTABLE_HEADING = 'unstable_heading'


def angle_delta(a, b):
    """Return the signed shortest angular delta from ``a`` to ``b`` in degrees.

    Parameters:
        a: Source heading in degrees.
        b: Target heading in degrees.

    Returns:
        A value in the range [-180, 180), where positive means turn right and
        negative means turn left, including wraparound through north.

    Side effects:
        None.

    Raises:
        TypeError: If the values do not support numeric arithmetic.
    """
    return (b-a+180) % 360-180


class SteeringAssist:
    """Stateful SRV steering-decision engine.

    The instance owns recent telemetry samples, derived motion estimates,
    command cooldown timestamps, status text, and configurable speed/tolerance
    limits. It has no thread of its own and assumes callers serialize access
    from the UI/timer thread. Lifecycle is explicit: start() enables decisions,
    stop() disables them, and reset_samples() discards unsafe telemetry history.
    """

    def __init__(self, max_speed=15.0, max_pulse=.8):
        """Initialize the decision engine and its conservative default limits.

        Parameters:
            max_speed: Maximum allowed SRV speed in metres per second before
                steering pulses are suppressed.
            max_pulse: Maximum single steering-pulse duration in seconds.

        Returns:
            None.

        Side effects:
            Initializes status strings through the translation layer.

        Raises:
            No exceptions are raised directly.
        """
        self.tolerance = 3
        self.max_speed = max_speed
        self.max_pulse = max_pulse
        self.enabled = False
        self.status = STATUS_STOPPED
        self.wait_code = None
        self.correction_direction = 0
        self.message = translate('SteeringAssist', 'Assistance off')
        self.previous = None
        self.sample = None
        self.consumed = None
        self.wait_until = 0
        self.command_end = 0
        self.position = None
        self.motion = None
        self.speed = None
        self.speed_limit = max_speed
        self.error_degrees = None
        self.interval = None
        self.wait_reason = translate('SteeringAssist', 'Waiting for fresh data')

    def stop(self, reason=None):
        """Disable assistance and clear any active correction state.

        Parameters:
            reason: Optional status message to expose to the UI; when omitted,
                the generic assistance-off message is used.

        Returns:
            None.

        Side effects:
            Changes enabled/status fields and clears correction direction.

        Raises:
            No exceptions are raised directly.
        """
        self.enabled = False
        self.status = STATUS_STOPPED
        self.wait_code = None
        self.correction_direction = 0
        self.message = reason if reason is not None else translate('SteeringAssist', 'Assistance off')

    def reset_samples(self):
        """Discard telemetry history and timing gates after an unsafe context.

        Parameters:
            None.

        Returns:
            None.

        Side effects:
            Clears samples, motion estimates, speed, intervals, and cooldowns.

        Raises:
            No exceptions are raised directly.
        """
        self.previous = self.sample = self.consumed = None
        self.position = self.motion = self.speed = self.interval = None
        self.error_degrees = None
        self.wait_until = 0
        self.command_end = 0

    def observe(self, now, x, y, heading):
        """Record fresh telemetry and estimate planar motion when position changes.

        Parameters:
            now: Monotonic timestamp in seconds for the telemetry sample.
            x: Map-space east/west position in metres.
            y: Map-space north/south position in metres.
            heading: Current SRV heading in degrees.

        Returns:
            None.

        Side effects:
            Updates previous/current samples, position history, motion estimate,
            or resets samples when any input is non-finite. Identical telemetry
            is ignored so one physical sample cannot drive repeated pulses.

        Raises:
            TypeError: If a value cannot be checked with math.isfinite().
        """
        if not all(math.isfinite(v) for v in (now, x, y, heading)):
            self.reset_samples()
            return
        sample = (now, x, y, heading)
        # Heading and position can arrive in different telemetry updates. A
        # heading-only change must not restart the clock used for speed.
        if self.position is None:
            self.position = (now,x,y)
        elif (x,y) != self.position[1:]:
            pt,px,py = self.position
            dt = now-pt
            if dt>0:
                distance = math.hypot(x-px,y-py)
                self.motion = (now,dt,distance/dt,
                               math.degrees(math.atan2(x-px,y-py)) % 360,distance)
            self.position = (now,x,y)
        if self.sample is not None and sample[1:] == self.sample[1:]:
            return
        self.previous, self.sample = self.sample, sample

    def start(self):
        """Enable assistance while requiring telemetry newer than activation.

        Parameters:
            None.

        Returns:
            None.

        Side effects:
            Marks the current sample as consumed, resets timing gates, and sets
            the visible status to waiting for fresh data.

        Raises:
            No exceptions are raised directly.
        """
        self.enabled = True
        self.wait_until = self.command_end = 0
        self.consumed = self.sample  # Wait for data captured after activation.
        self.message = translate('SteeringAssist', 'Assistance: waiting for fresh data')
        self.wait_reason = translate('SteeringAssist', 'Waiting for fresh data')
        self.status = STATUS_WAITING
        self.wait_code = None
        self.correction_direction = 0

    def wait(self, reason, wait_code=None):
        """Enter a non-commanding wait state with a translated explanation.

        Parameters:
            reason: Translation key/message describing why commands are unsafe.
            wait_code: Optional machine-readable reason for overlay variations.

        Returns:
            Always None, matching decide() when no command should be sent.

        Side effects:
            Updates wait reason, status, correction direction, and UI message.

        Raises:
            No exceptions are raised directly.
        """
        self.wait_reason = translate('SteeringAssist', reason)
        self.wait_code = wait_code
        self.status = STATUS_WAITING
        self.correction_direction = 0
        self.message = translate('SteeringAssist', 'Assistance: {reason}').format(
            reason=self.wait_reason.lower())
        return None

    def overlay_text(self):
        """Return concise overlay text that preserves the current safety reason.

        Parameters:
            None.

        Returns:
            A translated string describing waiting, slowing, correcting,
            on-course, or easing state.

        Side effects:
            None.

        Raises:
            No exceptions are raised directly.
        """
        if self.status == STATUS_SLOWING:
            if self.wait_code == WAIT_UNSTABLE_HEADING:
                return translate('SteeringAssist', 'Reduce speed · unstable heading')
            return translate('SteeringAssist', 'Reduce speed · up to {speed:.1f} m/s').format(
                speed=self.speed_limit)
        if self.status == STATUS_CORRECTING:
            return translate('SteeringAssist', 'Correcting right' if self.correction_direction > 0 else 'Correcting left')
        if self.status == STATUS_ON_COURSE:
            return translate('SteeringAssist', 'Assistance: on course')
        if self.status == STATUS_EASING:
            return translate('SteeringAssist', 'Easing steering')
        return self.wait_reason

    def decide(self, now, target):
        """Decide whether one empirical steering pulse is currently safe.

        Parameters:
            now: Current monotonic timestamp in seconds.
            target: Map-space ``(x, y)`` target point in metres.

        Returns:
            ``(direction, duration_seconds)`` when a pulse should be sent, where
            ``+1`` is right and ``-1`` is left; otherwise None.

        Side effects:
            Consumes fresh samples, updates status/message fields, and sets
            command and settling cooldown timestamps. No input is sent here.

        Raises:
            TypeError: If target coordinates are not numeric.
            IndexError: If target does not contain two coordinates.
        """
        if not self.enabled:
            return None
        # Suppression order is deliberate: stale telemetry older than 2.5 s
        # cannot safely describe the SRV's current heading or position.
        if self.sample is None or now-self.sample[0] > 2.5:
            return self.wait('Waiting for fresh telemetry')
        # Do not overlap commands; another layer owns the actual key release.
        if now < self.command_end:
            return None
        # After a pulse, wait 0.25 s for the telemetry response to settle.
        if now < self.wait_until or self.sample[0] < self.wait_until:
            return self.wait('Waiting for steering response')
        # A consumed sample has already had one chance to produce a command.
        if self.sample is self.consumed:
            return None
        self.consumed = self.sample
        if self.previous is None:
            return self.wait('Waiting for a second sample')
        t, x, y, heading = self.sample
        pt, px, py, ph = self.previous
        dt = t-pt
        # Samples closer than 0.25 s are noisy; gaps beyond 30 s are stale.
        if dt < .25 or dt > 30:
            return self.wait('Waiting for regular samples')
        if self.motion is None:
            return self.wait('Waiting for a new position')
        mt,position_dt,speed,travel,distance = self.motion
        self.speed, self.interval = speed, position_dt
        # Motion estimates must come from a position update within 30 s and
        # remain no more than 2.5 s old relative to the current decision.
        if position_dt>30 or now-mt>2.5:
            return self.wait('Waiting for a new position')
        rate = angle_delta(ph, heading)/dt
        bearing = math.degrees(math.atan2(target[0]-x, target[1]-y)) % 360
        error = angle_delta(heading, bearing)
        self.error_degrees = error
        self.speed_limit = self.max_speed
        # The configured speed limit is a safety gate, not throttle control.
        if speed > self.speed_limit:
            self.wait_reason = translate('SteeringAssist', 'Speed above the limit')
            self.wait_code = None
            self.status = STATUS_SLOWING
            self.correction_direction = 0
            self.message = translate('SteeringAssist', 'Reduce speed')
            return None
        # Suppress commands when heading changes faster than 20 deg/s or the
        # latest position jump exceeds 100 m, both signs of unstable telemetry.
        if abs(rate)>20 or distance>100:
            return self.wait('Waiting for heading/position stability', WAIT_UNSTABLE_HEADING)
        # Below 0.3 m/s, steering response is unreliable and may be stationary.
        if speed < .3:
            return self.wait('Waiting for movement')
        # If travel differs from heading by more than 60 degrees, the SRV may
        # be sliding/reversing, so steering corrections are not predictable.
        if abs(angle_delta(heading,travel))>60:
            return self.wait('Waiting for steady forward motion')
        # Ease before crossing the target bearing using a short, bounded
        # predicted error; this is not obstacle detection or path planning.
        predicted = error-rate*min(dt, .5)
        # The configured tolerance and predicted-error crossing suppress pulses
        # when the current or near-future heading is already acceptable.
        if abs(error)<=self.tolerance or predicted*error<=0 or abs(predicted)<=self.tolerance:
            self.status = STATUS_ON_COURSE if abs(error)<=self.tolerance else STATUS_EASING
            self.wait_code = None
            self.correction_direction = 0
            self.message = translate(
                'SteeringAssist',
                'Assistance: on course' if self.status == STATUS_ON_COURSE else 'Easing steering')
            return None
        # Empirical pulse math, not a vehicle dynamics model: start from
        # min(max_pulse, abs(predicted) / 46.5 * 0.7), then reduce duration
        # above 10 m/s while keeping a 0.08 s floor. Observe one response before
        # another pulse rather than assuming exact proportional behaviour.
        base_duration = min(self.max_pulse, abs(predicted)/46.5*.7)
        duration = max(.08, base_duration/(1+max(0,speed-10)/20))
        # Empirical aggression bands based on current error: 1, 1.5, 2, or 3.
        deviation = abs(error)
        factor = 1 if deviation <= 30 else 1.5 if deviation <= 90 else 2 if deviation <= 120 else 3
        duration *= factor
        self.command_end = now+duration
        self.wait_until = self.command_end+.25
        self.status = STATUS_CORRECTING
        self.wait_code = None
        self.correction_direction = 1 if error > 0 else -1
        self.message = translate(
            'SteeringAssist',
            'Assistance: correcting right' if self.correction_direction > 0 else
            'Assistance: correcting left')
        return (1 if error>0 else -1), duration
