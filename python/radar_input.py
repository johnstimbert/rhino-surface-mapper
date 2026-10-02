"""Passive observation of configured SRV primary-fire controls.

This module belongs to the input-observation layer. It reads the Elite Dangerous
bindings preset and observes keyboard, mouse-button, and WinMM joystick-button
state so callers can tell whether the player is pressing the SRV primary-fire
control. It never injects input and never claims that a fire pulse occurred in
the game; it only observes whether the configured controls are currently down.

Safety contract: unsupported bindings are reported rather than guessed, mouse
wheel bindings are not mapped because GetAsyncKeyState cannot represent wheel
deltas as a held button, and focus-sensitive checks require the foreground
process to be EliteDangerous64.exe.
"""
import ctypes as C
from ctypes import wintypes as W
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET


def read_bindings(folder, selected=None):
    """Read SRV primary-fire bindings from an Elite Dangerous bindings folder.

    Parameters:
        folder: Directory containing Elite Dangerous ``.binds`` files and
            ``StartPreset.4.start``.
        selected: Optional explicit ``.binds`` file path that bypasses preset
            resolution.

    Returns:
        ``(path, bindings)`` where bindings contains the Primary/Secondary
        ``BuggyPrimaryFireButton`` entries with their modifiers.

    Side effects:
        Reads files from disk only; it never observes devices or injects input.

    Raises:
        OSError: If required files cannot be read.
        ET.ParseError: If the selected XML cannot be parsed.
        ValueError: If the preset/action has no usable primary-fire binding.
        IndexError: If ``StartPreset.4.start`` is empty.
    """
    folder = Path(folder)
    if selected:
        path = Path(selected)
    else:
        # Elite Dangerous records the active preset under LOCALAPPDATA in
        # Frontier Developments\Elite Dangerous\Options\Bindings.
        preset_file = folder / 'StartPreset.4.start'
        lines = preset_file.read_text(encoding='utf-8-sig').splitlines()
        preset = (lines[2] if len(lines) >= 3 else lines[0]).strip()
        candidates = []
        for candidate in folder.glob('*.binds'):
            # Choose the matching preset with the highest MajorVersion, then
            # MinorVersion, then file mtime, matching the game's upgrade model.
            try:
                root = ET.parse(candidate).getroot()
                if root.get('PresetName') == preset:
                    candidates.append((int(root.get('MajorVersion', 0)),
                                       int(root.get('MinorVersion', 0)),
                                       candidate.stat().st_mtime_ns, candidate))
            except (ET.ParseError, ValueError):
                continue
        if not candidates:
            raise ValueError(f'Perfil SRV {preset!r} não encontrado. Escolha o .binds nas opções.')
        path = max(candidates)[-1]
    root = ET.parse(path).getroot()
    action = root.find('BuggyPrimaryFireButton')
    if action is None:
        raise ValueError('O perfil não contém disparo primário do SRV.')
    bindings = []
    for slot in ('Primary', 'Secondary'):
        # Only Primary/Secondary BuggyPrimaryFireButton entries and their
        # modifiers are observed; {NoDevice} means the slot is intentionally off.
        item = action.find(slot)
        if item is not None and item.get('Device') not in (None, '{NoDevice}'):
            binding = [(item.get('Device'), item.get('Key', ''))]
            binding += [(m.get('Device'), m.get('Key', '')) for m in item.findall('Modifier')]
            bindings.append(binding)
    if not bindings:
        raise ValueError('Disparo primário SRV sem controlos associados.')
    return path, bindings


def virtual_key(device, key):
    """Map supported keyboard and mouse binding names to Windows virtual keys.

    Parameters:
        device: Elite Dangerous device name such as ``Keyboard`` or ``Mouse``.
        key: Binding key name such as ``Key_F8`` or ``Mouse_1``.

    Returns:
        A Windows virtual-key integer, or None for unsupported devices/keys.

    Side effects:
        None.

    Raises:
        No exceptions are raised directly.
    """
    # Mouse wheel is unsupported because wheel movement is a delta event, not a
    # stable pressed state available through GetAsyncKeyState.
    if device == 'Mouse':
        # Win32 virtual keys: Mouse_1->VK 1, Mouse_2->2, Mouse_3->4,
        # Mouse_4->5, and Mouse_5->6.
        return {'Mouse_1':1, 'Mouse_2':2, 'Mouse_3':4, 'Mouse_4':5, 'Mouse_5':6}.get(key)
    if device != 'Keyboard':
        return None
    name = key.removeprefix('Key_')
    if len(name) == 1 and name.isascii() and name.isalnum():
        # Letter and digit virtual keys use their uppercase ASCII code.
        return ord(name.upper())
    if re.fullmatch(r'F(?:[1-9]|1[0-9]|2[0-4])', name):
        # Function keys follow VK_Fn == 111 + n for F1 through F24.
        return 111 + int(name[1:])
    return {'Space':32, 'Enter':13, 'Tab':9, 'Backspace':8, 'Escape':27,
            'LeftShift':160, 'RightShift':161, 'LeftControl':162, 'RightControl':163,
            'LeftAlt':164, 'RightAlt':165, 'UpArrow':38, 'DownArrow':40,
            'LeftArrow':37, 'RightArrow':39, 'Home':36, 'End':35,
            'PageUp':33, 'PageDown':34, 'Insert':45, 'Delete':46}.get(name)


class JoyCaps(C.Structure):
    """WinMM JOYCAPS subset used to identify devices and axis limits.

    Instances are filled by joyGetDevCapsW. The name is normalised by callers
    for stable matching, with VID/PID fallbacks for known T.16000M devices.
    """

    _fields_ = [('mid', W.WORD), ('pid', W.WORD), ('name', W.WCHAR*32)] + [
        (n, W.UINT) for n in ('xmin','xmax','ymin','ymax','zmin','zmax','buttons',
        'pmin','pmax','rmin','rmax','umin','umax','vmin','vmax','caps','maxaxes','axes','maxbuttons')
    ] + [('regkey', W.WCHAR*32), ('oem', W.WCHAR*260)]


class JoyInfo(C.Structure):
    """WinMM JOYINFOEX-compatible state used for buttons and axes.

    Callers set ``size`` and ``flags`` before joyGetPosEx fills the structure.
    Button bits are later tested with ``buttons & (1 << (n - 1))``. The full
    all-fields flag is JOY_RETURNALL = 0x00ff; button-only polling below uses
    the smaller button-state flag because only Joy_1 through Joy_32 are read.
    """

    _fields_ = [(n, W.DWORD) for n in ('size','flags','x','y','z','r','u','v',
                                              'buttons','button','pov','reserved1','reserved2')]


class RadarInput:
    """Passive Windows device observer for the configured fire-control binding.

    The instance owns the resolved bindings file path, supported binding list,
    joystick-name index, Windows DLL handles, and a cached foreground-window
    focus result. It has no background thread; callers poll load(), down(), and
    game_focused() from their own lifecycle.
    """

    def __init__(self):
        """Initialize passive input-observation state and Windows API prototypes.

        Parameters:
            None.

        Returns:
            None.

        Side effects:
            On Windows, loads user32, kernel32, and winmm function prototypes for
            GetAsyncKeyState, foreground-process checks, joyGetNumDevs,
            joyGetDevCapsW, and joyGetPosEx.

        Raises:
            OSError: Propagated by ctypes if Windows DLL loading fails.
        """
        self.bindings = []
        self.message = 'Controlos por carregar'
        self.path = None
        self.last_window = None
        self.last_focus_result = False
        self.joysticks = {}
        self.available = os.name == 'nt'
        if not self.available:
            self.message = 'Deteção de disparo disponível no Windows'
            return
        self.user = C.WinDLL('user32', use_last_error=True)
        self.kernel = C.WinDLL('kernel32', use_last_error=True)
        self.winmm = C.WinDLL('winmm')
        self.user.GetForegroundWindow.restype = W.HWND
        self.user.GetWindowThreadProcessId.argtypes = [W.HWND, C.POINTER(W.DWORD)]
        self.user.GetAsyncKeyState.argtypes = [C.c_int]
        self.user.GetAsyncKeyState.restype = C.c_short
        self.kernel.OpenProcess.argtypes = [W.DWORD, W.BOOL, W.DWORD]
        self.kernel.OpenProcess.restype = W.HANDLE
        self.kernel.QueryFullProcessImageNameW.argtypes = [W.HANDLE, W.DWORD, W.LPWSTR, C.POINTER(W.DWORD)]
        self.kernel.CloseHandle.argtypes = [W.HANDLE]
        self.winmm.joyGetDevCapsW.argtypes = [C.c_size_t, C.POINTER(JoyCaps), W.UINT]
        self.winmm.joyGetPosEx.argtypes = [W.UINT, C.POINTER(JoyInfo)]

    def load(self, selected=None):
        """Resolve bindings and keep only controls this observer can poll.

        Parameters:
            selected: Optional explicit ``.binds`` file path.

        Returns:
            None.

        Side effects:
            Reads the Elite Dangerous bindings under ``%LOCALAPPDATA%``, scans
            up to 16 WinMM joystick devices, normalises names, updates message
            text, and records unsupported bindings for display.

        Raises:
            No exceptions escape; expected I/O, XML, and lookup failures are
            converted into the user-visible message.
        """
        self.bindings = []
        if not self.available:
            return
        try:
            folder = Path(os.environ['LOCALAPPDATA'])/'Frontier Developments'/'Elite Dangerous'/'Options'/'Bindings'
            self.path, bindings = read_bindings(folder, selected)
            self.joysticks = {}
            for index in range(min(16, self.winmm.joyGetNumDevs())):
                # WinMM exposes legacy joystick slots; this observer deliberately
                # scans at most 16 devices to match the API-era limit used here.
                caps = JoyCaps()
                if self.winmm.joyGetDevCapsW(index, C.byref(caps), C.sizeof(caps)) == 0:
                    name = re.sub('[^a-z0-9]', '', caps.name.lower())
                    self.joysticks.setdefault(name, []).append(index)
                    # Some drivers return a generic name. The T.16000M VID/PID
                    # special case avoids choosing a different joystick.
                    if (caps.mid, caps.pid) == (0x044f, 0xb10a) and name != 't16000m':
                        self.joysticks.setdefault('t16000m', []).append(index)
            unsupported = []
            for binding in bindings:
                if all(self.supports(device, key) for device, key in binding):
                    self.bindings.append(binding)
                else:
                    unsupported.append(' + '.join(f'{d}/{k}' for d,k in binding))
            self.message = self.path.name + ': ' + ' ou '.join(
                ' + '.join(f'{d}/{k}' for d,k in binding) for binding in self.bindings)
            if unsupported:
                self.message += ' | Não disponível: ' + ', '.join(unsupported)
        except (OSError, ValueError, IndexError, KeyError, ET.ParseError) as exc:
            self.message = f'Controlos: {exc}'

    def joystick_id(self, device):
        """Return the unique WinMM device index matching an Elite device name.

        Parameters:
            device: Elite Dangerous device name from a binding entry.

        Returns:
            The sole matching joystick index, or None when absent or ambiguous.

        Side effects:
            None.

        Raises:
            No exceptions are raised directly.
        """
        name = re.sub('[^a-z0-9]', '', device.lower())
        matches = self.joysticks.get(name, [])
        return matches[0] if len(matches) == 1 else None

    def supports(self, device, key):
        """Report whether a binding entry can be passively observed.

        Parameters:
            device: Elite Dangerous device name.
            key: Elite Dangerous key/button name.

        Returns:
            True for supported virtual keys or joystick buttons 1-32 on a unique
            device; otherwise False.

        Side effects:
            None.

        Raises:
            No exceptions are raised directly.
        """
        if virtual_key(device, key) is not None:
            return True
        return bool(self.joystick_id(device) is not None and
                    re.fullmatch(r'Joy_(?:[1-9]|[12][0-9]|3[0-2])', key))

    def game_focused(self):
        """Return whether the foreground window belongs to EliteDangerous64.exe.

        Parameters:
            None.

        Returns:
            True when the current foreground process basename matches
            ``EliteDangerous64.exe`` case-insensitively; otherwise False.

        Side effects:
            Caches the result per foreground HWND to avoid reopening the same
            process handle on every timer tick.

        Raises:
            No exceptions are raised directly.
        """
        if not self.available:
            return False
        pid = W.DWORD()
        window = self.user.GetForegroundWindow()
        if window == self.last_window:
            return self.last_focus_result
        self.last_window = window
        self.last_focus_result = False
        # Focus check chain: GetForegroundWindow -> GetWindowThreadProcessId ->
        # OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION = 0x1000) ->
        # QueryFullProcessImageNameW -> basename comparison.
        self.user.GetWindowThreadProcessId(window, C.byref(pid))
        handle = self.kernel.OpenProcess(0x1000, False, pid.value)
        if not handle:
            return False
        try:
            buffer = C.create_unicode_buffer(32768)
            size = W.DWORD(len(buffer))
            self.last_focus_result = bool(self.kernel.QueryFullProcessImageNameW(handle, 0, buffer, C.byref(size))
                        and Path(buffer.value).name.lower() == 'elitedangerous64.exe')
            return self.last_focus_result
        finally:
            self.kernel.CloseHandle(handle)

    def down(self):
        """Return whether any configured fire-control binding is fully pressed.

        Parameters:
            None.

        Returns:
            True when any binding is down. Each binding requires all modifiers
            and the main control (AND), while alternative bindings are accepted
            independently (OR).

        Side effects:
            Polls keyboard/mouse state and caches each joystick button word for
            this call only. It never injects input or proves the game fired.

        Raises:
            No exceptions are raised directly.
        """
        states = {}
        def pressed(device, key):
            """Poll one supported binding component as a current pressed state.

            Parameters:
                device: Elite Dangerous device name.
                key: Elite Dangerous key/button name.

            Returns:
                True when the component is currently pressed, otherwise False.

            Side effects:
                Uses GetAsyncKeyState(vk) & 0x8000 for virtual keys. For
                joysticks, calls joyGetPosEx once per device in this down() call
                and tests buttons with ``buttons & (1 << (n - 1))``.

            Raises:
                No exceptions are raised directly.
            """
            vk = virtual_key(device, key)
            if vk is not None:
                return bool(self.user.GetAsyncKeyState(vk) & 0x8000)
            index = self.joystick_id(device)
            if index is None:
                return False
            if index not in states:
                info = JoyInfo()
                # 0x80 requests button state for the 32-button WinMM mask used
                # by the Joy_1..Joy_32 binding names supported here.
                info.size, info.flags = C.sizeof(info), 0x80
                states[index] = info.buttons if self.winmm.joyGetPosEx(index, C.byref(info)) == 0 else 0
            return bool(states[index] & (1 << (int(key[4:])-1)))
        return any(all(pressed(d,k) for d,k in b) for b in self.bindings)
