"""Qt integration layer for SRV steering assistance.

This module connects UI state, telemetry validity, the pure SteeringAssist
decision engine, and the Windows SteeringInput bridge. It owns the 16 ms timer
loop that checks focus, context, suspension conditions, manual intervention,
arrival braking, and direction-test mode in a deterministic order.

Safety contract: suspension keeps assistance enabled but releases keys and waits
for safe conditions; full stop disables assistance for manual steering or ended
navigation. All injected-input errors and unsafe UI/game states release held
keys before displaying status.
"""
import time
import math
from pathlib import Path
from datetime import datetime
from PySide6.QtCore import QTimer
from PySide6.QtGui import QShortcut, QKeySequence
from PySide6.QtWidgets import QPushButton, QLabel, QMessageBox
from steering import SteeringAssist, STATUS_SLOWING, STATUS_WAITING
from steering_input import SteeringInput
from turn_trial import TurnTrial
from i18n import translate


class SteeringUI:
    """Mixin-style owner of steering assistance widgets and timer lifecycle.

    The host window supplies radar_input, overlay, state, live_status, and
    status_valid. This class owns the assist engine, input bridge, F8 shortcut,
    pending-start state, arrival-braking timer, and direction-test recorder. The
    QTimer ticks every 16 ms on the Qt thread; no worker thread is created here.
    """

    def setup_steering(self, layout, options):
        """Create steering assistance state, widgets, shortcuts, and timer.

        Parameters:
            layout: Qt layout that receives the button and status label.
            options: Mapping with assistance speed, pulse, and tolerance values.

        Returns:
            None.

        Side effects:
            Instantiates SteeringAssist and SteeringInput, loads bindings,
            connects the F8 shortcut/button, and starts a 16 ms QTimer.

        Raises:
            Any Qt construction errors from widget/timer creation may propagate.
        """
        self.assist = SteeringAssist()
        # Direction-test mode is user-requested and bypasses the adaptive
        # steering controller and its speed/heading limits.
        self.direction_test = False
        self.direction_test_started = 0
        self.turn_trial = TurnTrial()
        self.turn_results_directory = Path(__file__).resolve().parent/'logs'
        self.turn_index = None
        self.turn_tick = None
        for key, attr, low, high, factor in (
                ('assist_speed', 'max_speed',15,40,1), ('assist_pulse_ms','max_pulse',200,1000,.001)):
            value = options.get(key)
            if type(value) in (int,float) and low<=value<=high:
                setattr(self.assist,attr,value*factor)
        tolerance = options.get('assist_tolerance_deg', 3)
        if type(tolerance) is int and 0 <= tolerance <= 180:
            self.assist.tolerance = tolerance
        self.steering_input = SteeringInput(self.radar_input)
        self.steering_input.load()
        self.assist_pending = 0
        self.assist_context = None
        self.f8_down = False
        self.braking_until = 0
        self.navigation_arrivals = 0
        self.assist_button = QPushButton(translate('SteeringUI', 'Steering assistance [F8]'))
        self.assist_button.clicked.connect(self.toggle_assistance)
        layout.addWidget(self.assist_button)
        self.assist_info = QLabel(self.steering_input.message)
        layout.addWidget(self.assist_info)
        self.assist_shortcut = QShortcut(QKeySequence('F8'),self)
        self.assist_shortcut.activated.connect(self.toggle_assistance)
        self.assist_timer = QTimer(self)
        self.assist_timer.timeout.connect(self.update_assistance)
        self.assist_timer.start(16)

    def steering_target(self):
        """Return the current map-space navigation target for assistance.

        Parameters:
            None.

        Returns:
            ``(x, y)`` for active navigation, return-to-pause, or search target;
            otherwise None.

        Side effects:
            None.

        Raises:
            AttributeError: If the host window lacks expected state fields.
        """
        s = self.state
        if s.active_nav_target is not None:
            return (s.active_nav_target['x'],s.active_nav_target['y'])
        if s.return_to_pause:
            return s.search_pause_point
        if s.search_started and not s.search_paused:
            return s.next_target_xy
        return None

    def pause_assistance(self, reason):
        """Suspend assistance without disabling the user's enabled state.

        Parameters:
            reason: Human-readable reason or translation key for the wait state.

        Returns:
            None.

        Side effects:
            Releases any held key, resets unsafe samples, puts the decision
            engine into waiting status, updates the label, and posts overlay
            notice.

        Raises:
            Any release or UI update exception may propagate to the caller.
        """
        self.steering_input.release()
        self.assist.reset_samples()
        self.assist.wait(reason)
        notice = translate('SteeringUI', 'Assistance paused: {reason}').format(
            reason=self.assist.wait_reason)
        self.assist_info.setText(notice)
        self.overlay.set_assistance_notice(notice)

    def stop_assistance(self, reason='Assistance off'):
        """Fully disable assistance and any direction-test or brake lifecycle.

        Parameters:
            reason: Translation key/message for the final stopped status.

        Returns:
            None.

        Side effects:
            Clears pending start and braking state, stops the decision engine,
            releases injected keys, and finishes any direction-test row as
            incomplete. Used for manual steering and completed navigation.

        Raises:
            Any release or trial-finalization exception may propagate.
        """
        self.assist_pending = 0
        self.braking_until = 0
        self.assist.stop(translate('SteeringUI', reason))
        self.steering_input.release()
        self.turn_trial.finish(time.monotonic(),complete=False)
        self.turn_index = None

    def toggle_assistance(self):
        """Toggle steering assistance from the UI button or F8 shortcut.

        Parameters:
            None.

        Returns:
            None.

        Side effects:
            Stops active assistance/braking, or reloads bindings, validates SRV
            and target context, captures map/body/generation, and opens a
            two-stage pending start for 10 s so the user can press the button
            and then refocus the game.

        Raises:
            Qt message-box errors may propagate.
        """
        if self.assist.enabled or self.assist_pending or self.braking_until:
            self.stop_assistance()
            return
        self.steering_input.load()
        if not self.steering_input.keys:
            self.assist_info.setText(self.steering_input.message)
            QMessageBox.information(
                self, translate('SteeringUI', 'Steering assistance'),
                self.steering_input.message)
            return
        if not self.status_valid or not self.state.in_srv or (not self.direction_test and self.steering_target() is None):
            self.assist_info.setText(translate(
                'SteeringUI', 'Enter the SRV first; normal assistance also requires a target.'))
            return
        self.assist_context = (id(self.state),self.state.body_key,self.state.map_generation)
        self.assist_pending = time.monotonic()+10
        self.assist.wait('Waiting for game focus')
        self.update_assistance()

    def update_assistance(self):
        """Run one guarded 16 ms timer tick for assistance state.

        Parameters:
            None.

        Returns:
            None.

        Side effects:
            Delegates to _update_assistance and converts expected transient
            failures into a suspension that releases keys and reports the cause.

        Raises:
            No OSError, ValueError, TypeError, or KeyError escapes.
        """
        try:
            self._update_assistance()
        except (OSError, ValueError, TypeError, KeyError) as exc:
            self.pause_assistance(translate(
                'SteeringUI', 'Temporary failure: {error}').format(error=exc))

    def _update_assistance(self):
        """Execute the ordered assistance state machine for one timer tick.

        Parameters:
            None.

        Returns:
            None.

        Side effects:
            Checks F8 edge/focus, navigation arrival and braking, context
            changes, manual input, telemetry validity, GUI panels/turret,
            joystick failures, injection errors, pending activation, steering
            decisions, key release, and direction-test updates.
            Suspension/release conditions are focus loss, invalid SRV telemetry,
            map/body/generation changes, joystick polling failure,
            ``GuiFocus != 0`` panels, turret flag ``Flags & (1 << 13)``, and
            injection errors. Full-stop conditions are manual keyboard/joystick
            steering, navigation/search completion, and arrival.

        Raises:
            OSError: From input APIs.
            ValueError: From invalid state/data conversions.
            TypeError: From unexpected state values.
            KeyError: From missing target keys.
        """
        # Tick order is safety-critical: read focus/F8 first, then arrival,
        # braking, context, suspension checks, activation, decisions, and tests.
        now = time.monotonic()
        focused = self.radar_input.game_focused()
        down = bool(self.radar_input.user.GetAsyncKeyState(0x77)&0x8000) if self.radar_input.available else False
        rising = down and not self.f8_down
        self.f8_down = down
        if rising and focused:
            self.toggle_assistance()
            return
        s = self.state
        context = (id(s),s.body_key,s.map_generation)
        arrivals = getattr(s, 'navigation_arrivals', 0)
        arrived = arrivals != self.navigation_arrivals and context == self.assist_context
        self.navigation_arrivals = arrivals
        if arrived and self.assist.enabled:
            # Arrival is a full stop of steering assistance, followed by the
            # explicit S-key brake only while focus and SRV telemetry are valid.
            self.stop_assistance('Destination reached')
            if focused and self.status_valid and s.in_srv and self.steering_input.brake():
                self.braking_until = now+3.0
        if self.braking_until:
            # Arrival braking is interrupted by timeout, focus loss, invalid SRV
            # telemetry, leaving the SRV, or manual keyboard/joystick input.
            if (now >= self.braking_until or not focused or not self.status_valid
                    or not s.in_srv or self.steering_input.manual() is True):
                self.stop_assistance('Assistance off: completed/interrupted arrival')
            else:
                self.assist_info.setText(translate(
                    'SteeringUI', 'Destination reached: braking (S for 3 s)'))
                self.overlay.set_assistance_notice(translate(
                    'SteeringUI', 'Destination reached: braking'))
                return
        target = self.steering_target()
        if context != self.assist_context:
            # Map/body/generation changes invalidate previous telemetry samples
            # but keep assistance enabled so it can resume after new samples.
            self.pause_assistance('Waiting for map position')
            self.assist.reset_samples()
            self.assist_context = context
        if self.assist.enabled or self.assist_pending:
            manual = self.steering_input.manual()
            # Suspension releases input but keeps assistance enabled; full stop
            # is reserved for manual steering or navigation completion/end.
            if not self.direction_test and target is None:
                self.stop_assistance('Assistance off: navigation/search ended')
            elif manual is True:
                self.stop_assistance('Assistance off: manual steering')
            elif not focused:
                self.pause_assistance('Waiting for game focus')
                return
            elif not self.status_valid or not s.in_srv:
                self.pause_assistance('Waiting for a valid SRV position')
                return
            elif self.live_status.get('GuiFocus',0)!=0 or int(self.live_status.get('Flags',0)) & (1<<13):
                self.pause_assistance('Waiting for the panel/turret to close')
                return
            elif manual is None:
                self.pause_assistance('Waiting for joystick input')
                return
            elif self.steering_input.error:
                self.pause_assistance(self.steering_input.error)
                self.steering_input.error = ''
                return
            elif self.assist_pending:
                # Pending start completes only after the game has focus and all
                # suspension gates are clear within the 10 s activation window.
                self.assist_pending = 0
                self.assist.start()
                self.direction_test_started = now
                if self.direction_test:
                    # Direction-test results use 7 s cycles: 2 s steering then
                    # 5 s forward, alternating left/right, written under logs.
                    path=self.turn_results_directory/('direcao-2s-'+datetime.now().strftime('%Y%m%d-%H%M%S-%f')+'.csv')
                    self.turn_trial=TurnTrial(path)
                    self.turn_index=None
                    self.turn_tick=now
            elif not self.direction_test:
                command = self.assist.decide(now,target)
                if command:
                    if self.steering_input.pulse(*command) is False:
                        if self.steering_input.error:
                            self.pause_assistance(self.steering_input.error)
                        else:
                            self.assist.wait('Command not sent')
                elif self.assist.status in (STATUS_SLOWING, STATUS_WAITING):
                    self.steering_input.release()
        if self.direction_test and self.assist.enabled and focused:
            elapsed = max(0,now-self.direction_test_started)
            if (self.turn_tick is not None and now-self.turn_tick>.15
                    and (self.turn_tick-self.direction_test_started)%7<2
                    and self.turn_trial.current is not None):
                self.turn_trial.current['gap']=True
            self.turn_tick=now
            index = int(elapsed//7)
            offset = elapsed%7
            turn_direction = -1 if index%2==0 else 1
            if index != self.turn_index:
                # Only complete turns with enough telemetry are included in the average.
                self.turn_trial.finish(now,complete=(self.turn_index is not None and index==self.turn_index+1))
                self.turn_index=index
                self.turn_trial.begin(turn_direction,now,self.assist.sample)
            motion=self.assist.motion
            self.turn_trial.observe(self.assist.sample,motion[2] if motion else None)
            remaining = 2-offset if offset<2 else 7-offset
            direction = turn_direction if offset<2 else 0
            label = translate(
                'SteeringUI',
                'LEFT' if turn_direction < 0 else 'RIGHT') if direction else translate('SteeringUI', 'FORWARD')
            if direction:
                if self.steering_input.hold_test(direction,remaining) is False:
                    self.stop_assistance(self.steering_input.error or 'Test interrupted: command not sent')
            else:
                self.steering_input.release()
            if self.assist.enabled:
                self.assist.message = f'{label} {math.ceil(remaining)} s · {self.turn_trial.summary()}'
        active = self.assist.enabled or bool(self.assist_pending)
        mode = 'teste' if self.direction_test else 'assistência'
        self.assist_button.setText(translate('SteeringUI', 'Steering assistance [F8]'))
        self.assist_button.setCheckable(True)
        self.assist_button.setChecked(active)
        detail = self.assist.message
        if self.direction_test and self.turn_trial.rows:
            def average(side):
                """Format one direction-test mean angle for the status label.

                Parameters:
                    side: Trial side key understood by TurnTrial.mean().

                Returns:
                    A localized decimal angle string, or an em dash when absent.

                Side effects:
                    None.

                Raises:
                    No exceptions are raised directly.
                """
                value=self.turn_trial.mean(side)
                return '—' if value is None else f'{value:.1f}º'.replace('.',',')
            detail += translate('SteeringUI', ' | {summary} | Left {left} | Right {right}').format(
                summary=self.turn_trial.summary(), left=average('esquerda'), right=average('direita'))
            self.assist_info.setToolTip(str(self.turn_trial.path or ''))
        if active and not self.direction_test:
            speed = '—' if self.assist.speed is None else f'{self.assist.speed:.1f}'.replace('.',',')
            limit = f'{self.assist.speed_limit:.1f}'.replace('.',',')
            detail += translate(
                'SteeringUI', ' | Estimated speed: {speed} m/s | Limit: {limit} m/s | Commands sent: {count}').format(
                    speed=speed, limit=limit, count=self.steering_input.sent_pulses)
        self.assist_info.setText(detail if self.steering_input.keys else self.steering_input.message)
        notice = ''
        if active:
            notice = (translate('SteeringUI', 'Return to the game to activate')
                      if self.assist_pending else
                      (self.assist.message if self.direction_test else self.assist.overlay_text()))
        self.overlay.set_assistance_notice(
            notice, warning=self.assist.status == STATUS_SLOWING)

    def direction_test_active(self):
        """Return whether direction-test mode is active or pending activation.

        Parameters:
            None.

        Returns:
            True when direction_test is selected and assistance is enabled or
            waiting for game focus; otherwise False.

        Side effects:
            None.

        Raises:
            No exceptions are raised directly.
        """
        return self.direction_test and (self.assist.enabled or bool(self.assist_pending))

    def observe_steering(self):
        """Forward accepted telemetry samples into the steering decision engine.

        Parameters:
            None.

        Returns:
            None.

        Side effects:
            Suspends and resets samples when map/body/generation changes. When
            heading is available, converts latitude/longitude to map metres,
            records the sample, and feeds direction-test observation data.

        Raises:
            AttributeError: If expected state or assist attributes are missing.
        """
        s = self.state
        context = (id(s),s.body_key,s.map_generation)
        if context != self.assist_context:
            self.pause_assistance('Waiting for map position')
            self.assist.reset_samples()
            self.assist_context = context
        if s.rhino_heading is not None:
            x,y = s.llxy(s.rhino_lat,s.rhino_lon)
            self.assist.observe(time.monotonic(),x,y,s.rhino_heading)
            motion=self.assist.motion
            self.turn_trial.observe(self.assist.sample,motion[2] if motion else None)
