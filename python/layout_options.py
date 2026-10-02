"""Qt layout and preferences panel for the Mapper main window.

This module is the presentation-layer owner for the collapsible options panel,
top control bars and theme styling. Preferences are persisted through
``settings_persistence`` to ``options.json``; business rules stay in the
Qt-free core modules and persistence details stay out of the widgets.
"""
from pathlib import Path
from PySide6.QtCore import Qt, QByteArray
from PySide6.QtGui import QColor, QFont
from PySide6.QtWidgets import (QWidget, QVBoxLayout, QHBoxLayout, QFormLayout,
    QPushButton, QToolButton, QScrollArea, QMenu, QComboBox, QSpinBox,
    QLineEdit, QFileDialog, QLabel, QColorDialog, QMessageBox, QSizePolicy, QFontComboBox, QStyleFactory, QAbstractSpinBox)
from settings_persistence import (read_exported_settings, save_preferences,
                                  write_exported_settings)
from i18n import SUPPORTED_LANGUAGES, normalize_language, translate


OP_MAPS = 'maps'
OP_MARK = 'mark'
OP_MARK_DEPOSIT = 'mark_deposit'
OP_MARK_RIG = 'mark_rig'
OP_EXIT = 'exit'

SECTION_SEARCH = 'search_navigation'
SECTION_RADAR = 'radar'
SECTION_RHINO = 'rhino'
SECTION_ASSIST = 'steering_assistance'
SECTION_ED_PARAMETERS = 'ed_parameters'
SECTION_MAP = 'map'
SECTION_OVERLAY = 'overlay'
SECTION_LAYOUT = 'layout'

THEME_SYSTEM = 'system'
THEME_DARK = 'dark'
THEME_LIGHT = 'light'
THEME_LABELS = {
    THEME_SYSTEM: 'Windows system',
    THEME_DARK: 'Dark',
    THEME_LIGHT: 'Light',
}
THEME_LEGACY_VALUES = {label: value for value, label in THEME_LABELS.items()}
THEME_LEGACY_VALUES['Como o Windows'] = THEME_SYSTEM


def normalize_theme_value(value):
    """Return the stable theme ID for a current or legacy persisted value.

    Args:
        value: Persisted theme value or translated legacy label.

    Returns:
        ``system``, ``dark`` or ``light`` when recognized; otherwise ``None``.

    Notes:
        Legacy labels such as ``Como o Windows`` are normalized to ``system``.
    """
    if not isinstance(value, str):
        return None
    if value in THEME_LABELS:
        return value
    return THEME_LEGACY_VALUES.get(value)


def _theme_is_dark(theme, system_scheme):
    """Resolve whether a theme selection should render as dark.

    Args:
        theme: Stable or legacy theme value.
        system_scheme: Current Qt platform colour scheme.

    Returns:
        ``True`` for explicit dark mode or system mode while the platform is
        dark; ``False`` for light and unrecognized values.
    """
    theme = normalize_theme_value(theme)
    return theme == THEME_DARK or (theme == THEME_SYSTEM and system_scheme == Qt.ColorScheme.Dark)


def _widget_theme_colors(dark):
    """Return base colours used by the main window stylesheet.

    Args:
        dark: Whether the dark palette should be returned.

    Returns:
        Dictionary with background, foreground, panel and border colours.
    """
    if dark:
        return {'background': '#202429', 'foreground': '#edf1f5',
                'panel': '#2c3239', 'border': '#46515d'}
    return {'background': '#edf1f4', 'foreground': '#233448',
            'panel': '#ffffff', 'border': '#cbd5dd'}


def _theme_stylesheet(colors, arrow_root):
    """Build the application stylesheet for controls and spin buttons.

    Args:
        colors: Palette from ``_widget_theme_colors``.
        arrow_root: Asset directory URL/path for spin-button arrow SVGs.

    Returns:
        Qt stylesheet string applied to the main window.
    """
    background = colors['background']
    foreground = colors['foreground']
    panel = colors['panel']
    border = colors['border']
    return (f'QMainWindow, QWidget {{ background: {background}; color: {foreground}; }} '
            f'QPushButton, QToolButton, QComboBox, QSpinBox, QLineEdit {{ '
            f'background: {panel}; border: 1px solid {border}; border-radius: 4px; padding: 4px; }} '
            'QPushButton:disabled { color: #7e8790; } '
            'QPushButton:checked { border: 2px solid #448b68; } '
            f'QMenu {{ background: {panel}; }} '
            'QMenu::item:selected { background: #448b68; color: white; }' +
            f"""
            QAbstractSpinBox {{ padding: 4px 24px 4px 4px; min-height: 24px; }}
            QAbstractSpinBox::up-button {{ subcontrol-origin: border; subcontrol-position: top right; width: 22px; height: 16px; border-left: 1px solid {border}; background: {panel}; }}
            QAbstractSpinBox::down-button {{ subcontrol-origin: border; subcontrol-position: bottom right; width: 22px; height: 16px; border-left: 1px solid {border}; background: {panel}; }}
            QAbstractSpinBox::up-arrow {{ image: url("{arrow_root}/spin-up.svg"); width: 10px; height: 7px; }}
            QAbstractSpinBox::down-arrow {{ image: url("{arrow_root}/spin-down.svg"); width: 10px; height: 7px; }}
            QAbstractSpinBox::up-button:hover, QAbstractSpinBox::down-button:hover {{ background: #698779; }}
        """)


class LayoutOptions:
    """Mixin that owns the main-window options panel and persisted preferences.

    The host window supplies operational controls, map view, overlay and input
    services. This mixin owns option widgets, validation callbacks, reset
    actions and theme application, while preserving unrelated preference keys
    that may be used by other parts of the application.
    """

    def build_layout_options(self, layout, operations, controls, options):
        """Build control bars and the collapsible persisted options panel.

        Args:
            layout: Main window layout receiving the map view and options panel.
            operations: Existing operation-button layout to repopulate.
            controls: Existing navigation/control layout to repopulate.
            options: Previously loaded preference dictionary.

        Side effects:
            Reparents existing widgets into the new layout, creates option
            controls, applies validated saved values, wires persistence through
            ``settings_persistence`` and connects system ``colorSchemeChanged``
            so ``system`` theme follows the platform.
        """
        self.setting_fields = {}
        self.loading_settings = False
        self.preferences = dict(options) if isinstance(options, dict) else {}
        if 'theme' in self.preferences:
            theme = normalize_theme_value(self.preferences['theme'])
            if theme is not None:
                self.preferences['theme'] = theme
        self.option_resets = {}
        self.option_sections = {}
        self.fixed_buttons = []
        self.color_buttons = {}
        # Reuse existing controls to preserve their signals and enablement rules.
        for box in (operations, controls):
            while box.count():
                item = box.takeAt(0)
                if item.widget():
                    item.widget().hide()
        maps = QPushButton(translate('LayoutOptions', 'Maps'))
        menu = QMenu(maps)
        # This is the bar actually shown after the layout is applied.
        # The order follows the map menu used during normal operation.
        for title, callback in [('Open', self.load_map), ('Save', self.save_map),
                                ('New', self.new_map), ('View and manage', self.show_map_library)]:
            menu.addAction(translate('LayoutOptions', title), callback)
        maps.setObjectName("maps_menu")
        maps.setMenu(menu)
        operations.addWidget(maps)
        self.op_buttons[OP_MAPS] = maps
        self.fixed_buttons.append((maps, 90))
        for key, title in [(OP_MARK,'Marker'), (OP_MARK_DEPOSIT,'Deposit'), (OP_MARK_RIG,'Rig')]:
            button = self.op_buttons[key]
            button.setText(translate('LayoutOptions', title))
            button.show()
            operations.addWidget(button)
            self.fixed_buttons.append((button, 90))
        self.options_button = QPushButton(translate('LayoutOptions', 'Options'))
        self.options_button.setCheckable(True)
        operations.addWidget(self.options_button)
        operations.addWidget(self.op_buttons[OP_EXIT])
        self.op_buttons[OP_EXIT].show()
        self.fixed_buttons.extend([(self.options_button,125), (self.op_buttons[OP_EXIT],90)])
        operations.addStretch()
        for button, width in [(self.search_button,140),(self.skip_button,150),(self.overlay_button,90)]:
            controls.addWidget(button)
            button.show()
            self.fixed_buttons.append((button,width))
        controls.addStretch()
        controls.addWidget(self.assist_button)
        self.fixed_buttons.append((self.assist_button,180))
        controls.addWidget(self.follow)
        self.follow.show()
        controls.addSpacing(18)
        controls.addWidget(self.info_right)
        self.info_right.setFixedWidth(195)
        self.info_left.hide()
        self.radar_info.hide()
        self.assist_info.hide()
        layout.removeWidget(self.view)
        body = QHBoxLayout()
        body.addWidget(self.view, 1)
        self.options_panel = QScrollArea()
        self.options_panel.setWidgetResizable(True)
        self.options_panel.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        panel = QWidget()
        panel_layout = QVBoxLayout(panel)
        close = QPushButton(translate('LayoutOptions', 'Close options ×'))
        close.clicked.connect(lambda: self.options_button.setChecked(False))
        panel_layout.addWidget(close)
        self.options_panel.setWidget(panel)
        body.addWidget(self.options_panel)
        layout.insertLayout(3, body, 1)
        self.options_panel.hide()
        self.options_button.toggled.connect(self.toggle_options_panel)

        def section(section_id, title):
            """Create one collapsible settings group in the options panel.

            Args:
                section_id: Stable section identifier used for reset callbacks.
                title: Translatable section title.

            Returns:
                QFormLayout that receives rows for the section.
            """
            title = translate('LayoutOptions', title)
            header = QToolButton()
            header.setText(title)
            header.setToolButtonStyle(Qt.ToolButtonStyle.ToolButtonTextBesideIcon)
            header.setArrowType(Qt.ArrowType.RightArrow)
            header.setCheckable(True)
            header.setMinimumHeight(34)
            header.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Fixed)
            content = QWidget()
            form = QFormLayout(content)
            form.setRowWrapPolicy(QFormLayout.RowWrapPolicy.WrapAllRows)
            content.hide()
            header.toggled.connect(content.setVisible)
            header.toggled.connect(lambda opened: header.setArrowType(Qt.ArrowType.DownArrow if opened else Qt.ArrowType.RightArrow))
            panel_layout.addWidget(header)
            panel_layout.addWidget(content)
            self.option_sections[section_id] = (header, content)
            self.option_resets[section_id] = []
            return form

        def number(form, theme, label, key, default, low, high, callback, suffix=''):
            """Create a persisted integer spin option with validation.

            Args:
                form: Section form receiving the row.
                theme: Section identifier used to group reset callbacks.
                label: Translatable row label.
                key: Preference key stored in ``options.json``.
                default: Default integer value.
                low: Inclusive minimum accepted value.
                high: Inclusive maximum accepted value.
                callback: Runtime effect applied whenever the value changes.
                suffix: Unit suffix displayed by Qt.

            Returns:
                The configured QSpinBox.
            """
            spin = QSpinBox()
            spin.setObjectName(key)
            spin.setRange(low, high)
            spin.setSuffix(suffix)
            value = self.preferences.get(key, default)
            spin.setValue(value if type(value) is int and low <= value <= high else default)
            self.setting_fields[key] = (spin.value, spin.setValue, lambda v: type(v) is int and low <= v <= high)
            callback(spin.value())
            def changed(value):
                """Apply, persist and repaint after the user changes a number."""
                callback(value)
                self.save_preference(key, value)
                self.view.update()
            spin.valueChanged.connect(changed)
            form.addRow(translate('LayoutOptions', label), spin)
            self.option_resets[theme].append(lambda: spin.setValue(default))
            return spin

        def color(form, theme, label, key, default):
            """Create a persisted colour option with validation and preview.

            Args:
                form: Section form receiving the row.
                theme: Section identifier used to group reset callbacks.
                label: Translatable row label.
                key: Preference key stored in ``options.json``.
                default: Default QColor-compatible string.
            """
            button = QPushButton()
            button.setObjectName(key)
            self.color_buttons[key] = button
            value = self.preferences.get(key, default)
            if not isinstance(value, str) or not QColor(value).isValid():
                value = default
            def apply(value, save=True):
                """Apply a colour to the runtime view and optionally persist it."""
                self.view.colors[key] = value
                if key == "rhino_color":
                    svg = (Path(__file__).resolve().parent / "assets" / "Rhino.svg").read_text(encoding="utf-8-sig")
                    self.view.rhino_renderer.load(QByteArray(svg.replace("#e6ebf0", value).encode("utf-8")))
                button.setText(value)
                button.setStyleSheet(f'border-left: 18px solid {value}; padding: 5px;')
                self.view.update()
                if save:
                    self.save_preference(key, value)
            self.setting_fields[key] = (lambda: self.view.colors[key], apply, lambda v: isinstance(v,str) and QColor(v).isValid())
            apply(value, False)
            def choose():
                """Open the QColorDialog and save the selected valid colour."""
                value = QColorDialog.getColor(QColor(self.view.colors[key]), self, label)
                if value.isValid():
                    apply(value.name())
            button.clicked.connect(choose)
            form.addRow(translate('LayoutOptions', label), button)
            self.option_resets[theme].append(lambda: apply(self.view.default_map_color(key) if key in ('map_background','grid_color') else default))

        # Registered option keys, defaults and visible ranges:
        # coverage_width_m=2000 m, scanner_range_m=2000 m, search_azimuth=000º,
        # radar_speed=667 m/s in 100..3000, wave_color=#69b574,
        # rhino_size=56 px in 20..160, rhino_color=#e6ebf0,
        # assist_speed=15 m/s in 15..40, assist_pulse_ms=800 ms in 200..1000,
        # assist_tolerance_deg=3° in 0..180, scanner_group=A in 0..25,
        # bindings_path='', map colours below, map_text_scale=100% in 75..175,
        # overlay_width=360 px in 240..900, overlay_opacity=100% in 25..100,
        # language=normalized supported language, theme=system, font_family=Segoe UI,
        # font_size=9 pt in 8..11, panel_width=320 px in 280..440,
        # button_height=34 px in 28..44.
        # Search/navigation options: coverage_width_m 0..99,999 m default 2000;
        # search_azimuth 000º..359º default 0. Radar range shares the metre UI.
        for field, default in [('coverage_width_m',2000),('scanner_range_m',2000),('search_azimuth',0)]:
            spin = self.parameter_spins[field]
            self.setting_fields[field] = (spin.value, spin.setValue, lambda v, spin=spin: type(v) is int and spin.minimum() <= v <= spin.maximum())
            value = self.preferences.get(field, default)
            if type(value) is int and spin.minimum() <= value <= spin.maximum():
                spin.setValue(value)
            spin.valueChanged.connect(lambda value, field=field:self.save_preference(field,value))
        form = section(SECTION_SEARCH, 'Search and navigation')
        for field, label in [('coverage_width_m','Coverage'), ('search_azimuth','Search bearing')]:
            form.addRow(translate('LayoutOptions', label), self.parameter_spins[field])
            self.parameter_spins[field].show()
        self.option_resets[SECTION_SEARCH].extend([lambda:self.parameter_spins['coverage_width_m'].setValue(2000),lambda:self.search_azimuth_spin.setValue(0)])
        form = section(SECTION_RADAR, 'Radar')
        self.option_resets[SECTION_RADAR].append(lambda:self.parameter_spins['scanner_range_m'].setValue(2000))
        form.addRow(translate('LayoutOptions', 'Scanner range'), self.parameter_spins['scanner_range_m'])
        self.parameter_spins['scanner_range_m'].show()
        # Radar options affect animated wave timing and colour immediately.
        number(form,SECTION_RADAR,'Wave speed','radar_speed',667,100,3000,lambda v: setattr(self.view.radar,'SPEED_M_S',v),' m/s')
        color(form,SECTION_RADAR,'Wave colour','wave_color','#69b574')
        form.addRow(self.radar_info)
        self.radar_info.show()
        self.radar_info.setWordWrap(True)
        form = section(SECTION_RHINO, 'Rhino')
        # Rhino options control marker size and SVG hull recolouring.
        number(form,SECTION_RHINO,'Size','rhino_size',56,20,160,lambda v: setattr(self.view,'rhino_height',v),' px')
        color(form,SECTION_RHINO,'Hull colour','rhino_color','#e6ebf0')
        form = section(SECTION_ASSIST, 'Steering assistance')
        # Assistance options tune pulse timing, speed estimate and tolerance.
        number(form,SECTION_ASSIST,'Estimated maximum speed','assist_speed',15,15,40,lambda v:setattr(self.assist,'max_speed',v),' m/s')
        number(form,SECTION_ASSIST,'Maximum base duration','assist_pulse_ms',800,200,1000,lambda v:setattr(self.assist,'max_pulse',v/1000),' ms')
        number(form,SECTION_ASSIST,'Bearing tolerance','assist_tolerance_deg',3,0,180,lambda v:setattr(self.assist,'tolerance',v),'°')
        self.assist_info.setWordWrap(True)
        form.addRow(self.assist_info)
        self.assist_info.show()
        form = section(SECTION_ED_PARAMETERS, 'Elite Dangerous parameters')
        # ED parameters point input readers at the selected fire group/bindings.
        group = QComboBox()
        group.addItems([chr(65+i) for i in range(26)])
        group.setCurrentIndex(self.scanner_group)
        group.currentIndexChanged.connect(lambda v: (setattr(self,'scanner_group',v),self.save_preference('scanner_group',v)))
        form.addRow(translate('LayoutOptions', 'Mineral Scanner fire group'), group)
        self.setting_fields['scanner_group'] = (group.currentIndex, group.setCurrentIndex, lambda v: type(v) is int and 0 <= v <= 25)
        path = QLineEdit(self.bindings_override)
        path.setPlaceholderText(translate('LayoutOptions', 'Automatic SRV profile'))
        form.addRow(translate('LayoutOptions', 'Bindings file'), path)
        def apply_bindings():
            """Reload input bindings from the override path and persist it."""
            self.bindings_override = path.text().strip()
            self.radar_input.load(self.bindings_override or None)
            self.steering_input.load()
            self.save_preference('bindings_path', self.bindings_override)
        path.editingFinished.connect(apply_bindings)
        self.setting_fields['bindings_path'] = (path.text, lambda v:(path.setText(v),apply_bindings()), lambda v:isinstance(v,str))
        choose = QPushButton(translate('LayoutOptions', 'Choose .binds'))
        def browse():
            """Let the user pick a .binds file and apply it immediately."""
            filename, _ = QFileDialog.getOpenFileName(self,translate('LayoutOptions', 'SRV controls'),path.text(),'Bindings (*.binds)')
            if filename:
                path.setText(filename)
                apply_bindings()
        choose.clicked.connect(browse)
        form.addRow(choose)
        status = QPushButton(translate('LayoutOptions', 'Choose Status.json'))
        status.clicked.connect(self.choose_status)
        form.addRow(status)
        self.option_resets[SECTION_ED_PARAMETERS].extend([lambda:group.setCurrentIndex(0),lambda:(path.clear(),apply_bindings())])
        form = section(SECTION_MAP, 'Map')
        # Map options affect canvas colours and label scale without touching data.
        for label,key,default in [('Background','map_background','#ffffff'),('Grid','grid_color','#eeeeee'),('Trail','trail_color','#2f7d32'),('Coverage','coverage_color','#8cbd8c')]:
            color(form,SECTION_MAP,label,key,default)
        number(form,SECTION_MAP,'Text scale','map_text_scale',100,75,175,lambda v:setattr(self.view,'text_scale',v/100),' %')
        form = section(SECTION_OVERLAY, 'Overlay')
        # Overlay options resize the interactive overlay and change opacity.
        number(form,SECTION_OVERLAY,'Width','overlay_width',360,240,900,lambda v:self.overlay.resize(v,round(v/self.overlay.aspect_ratio)),' px')
        number(form,SECTION_OVERLAY,'Opacity','overlay_opacity',100,25,100,lambda v:self.overlay.setWindowOpacity(v/100),' %')
        form = section(SECTION_LAYOUT, 'Layout')
        # Layout options cover language, theme, font and panel/control sizing.
        language = QComboBox()
        for code, label in SUPPORTED_LANGUAGES.items():
            language.addItem(translate('LayoutOptions', label), code)
        language.setObjectName('language_selector')
        language_value = normalize_language(self.preferences.get('language'))
        language.setCurrentIndex(max(0, language.findData(language_value)))
        language.currentIndexChanged.connect(
            lambda _: self.save_preference('language', language.currentData()))
        form.addRow(translate('LayoutOptions', 'Language'), language)
        restart = QLabel(translate(
            'LayoutOptions', 'Restart the application to apply the language change.'))
        restart.setWordWrap(True)
        form.addRow(restart)
        self.setting_fields['language'] = (
            language.currentData,
            lambda value: language.setCurrentIndex(
                max(0, language.findData(normalize_language(value)))),
            lambda value: isinstance(value, str) and value in SUPPORTED_LANGUAGES)
        theme = QComboBox()
        # Theme IDs are stable persisted values. Legacy translated labels are
        # accepted on read, and "system" follows Qt colorSchemeChanged events.
        for value, label in THEME_LABELS.items():
            theme.addItem(translate('LayoutOptions', label), value)
        theme_value = normalize_theme_value(self.preferences.get('theme', THEME_SYSTEM)) or THEME_SYSTEM
        theme.setCurrentIndex(theme.findData(theme_value))
        form.addRow(translate('LayoutOptions', 'Theme'),theme)
        self.setting_fields['theme'] = (theme.currentData, lambda v: theme.setCurrentIndex(max(0, theme.findData(normalize_theme_value(v) or v))), lambda v: normalize_theme_value(v) is not None)
        theme.currentIndexChanged.connect(lambda _: (self.apply_theme(theme.currentData()),self.save_preference('theme',theme.currentData())))
        self.option_resets[SECTION_LAYOUT].append(lambda: theme.setCurrentIndex(0))
        family = QFontComboBox()
        family.setCurrentFont(QFont(self.preferences.get('font_family','Segoe UI')))
        form.addRow(translate('LayoutOptions', 'Font family'), family)
        def set_font(value):
            """Apply and persist the selected font family."""
            font = QFont(self.font())
            font.setFamily(value.family())
            self.setFont(font)
            self.save_preference('font_family',value.family())
        family.currentFontChanged.connect(set_font)
        self.setting_fields['font_family'] = (lambda:family.currentFont().family(), lambda v:family.setCurrentFont(QFont(v)), lambda v:isinstance(v,str) and bool(v.strip()))
        initial_font = QFont(self.font())
        initial_font.setFamily(family.currentFont().family())
        self.setFont(initial_font)
        def font_size(value):
            """Apply the selected point size to the host window font."""
            font = QFont(self.font())
            font.setPointSize(value)
            self.setFont(font)
        number(form,SECTION_LAYOUT,'Font size','font_size',9,8,11,font_size,' pt')
        self.option_resets[SECTION_LAYOUT].append(lambda:family.setCurrentFont(QFont('Segoe UI')))
        number(form,SECTION_LAYOUT,'Panel width','panel_width',320,280,440,lambda v:self.options_panel.setFixedWidth(v),' px')
        number(form,SECTION_LAYOUT,'Button height','button_height',34,28,44,self.resize_buttons,' px')
        for section_id,(header,content) in self.option_sections.items():
            if self.option_resets[section_id]:
                reset = QPushButton(translate('LayoutOptions', 'Reset to defaults'))
                reset.clicked.connect(lambda checked=False,section_id=section_id:[callback() for callback in self.option_resets[section_id]])
                content.layout().addRow(reset)
        actions = QHBoxLayout()
        for title, callback in [('Save',self.export_settings),('Load',self.import_settings),('Reset',self.reset_settings)]:
            button = QPushButton(translate('LayoutOptions', title))
            button.setFixedSize(78,34)
            button.clicked.connect(callback)
            actions.addWidget(button)
        actions.addStretch()
        panel_layout.addLayout(actions)
        panel_layout.addStretch()
        # The Windows style may use horizontal arrows for derived fields; force
        # one style and sizing policy for every spin box in the options panel.
        for spin in self.options_panel.findChildren(QAbstractSpinBox):
            numeric_style = QStyleFactory.create('Fusion')
            numeric_style.setParent(spin)
            spin.setStyle(numeric_style)
            spin.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Fixed)
        self.theme_selector = theme
        self.apply_theme(theme.currentData())
        self.setMinimumSize(1150,560)
        QApplication.styleHints().colorSchemeChanged.connect(self.system_theme_changed)

    def export_settings(self):
        """Export currently registered preferences to a user-selected JSON file.

        Side effects:
            Opens a save dialog and writes only known option keys through
            ``settings_persistence.write_exported_settings``.

        Error handling:
            ``OSError`` is handled locally and shown as a warning message.
        """
        filename, _ = QFileDialog.getSaveFileName(
            self, translate('LayoutOptions', 'Save settings'),
            'Rhino-configuracoes.json', translate('LayoutOptions', 'Settings (*.json)'))
        if not filename:
            return
        data = {key:getter() for key,(getter,_,_) in self.setting_fields.items()}
        try:
            write_exported_settings(filename, data)
        except OSError as exc:
            QMessageBox.warning(
                self, translate('LayoutOptions', 'Settings'),
                translate('LayoutOptions', 'Could not save settings: {error}').format(error=exc))

    def import_settings(self):
        """Import and validate a settings JSON file before applying it.

        Side effects:
            Opens a file dialog, validates every imported key against the
            registered field validators, applies the theme first so imported
            colours can override theme defaults, updates preferences and saves
            through the normal preference path.

        Error handling:
            ``OSError``, ``ValueError`` and ``TypeError`` from reading or
            validation are handled locally and shown as a warning.
        """
        filename, _ = QFileDialog.getOpenFileName(
            self, translate('LayoutOptions', 'Load settings'), '',
            translate('LayoutOptions', 'Settings (*.json)'))
        if not filename:
            return
        try:
            data = read_exported_settings(filename)
            for key,value in data.items():
                if key not in self.setting_fields or not self.setting_fields[key][2](value):
                    raise ValueError(f'Configuração inválida: {key}')
        except (OSError,ValueError,TypeError) as exc:
            QMessageBox.warning(
                self, translate('LayoutOptions', 'Settings'),
                translate('LayoutOptions', 'Could not load settings: {error}').format(error=exc))
            return
        self.loading_settings = True
        try:
            # Apply theme first; colour values from the imported file win after.
            for key in sorted(data,key=lambda key:key != 'theme'):
                self.setting_fields[key][1](data[key])
            self.preferences.update({key:getter() for key,(getter,_,_) in self.setting_fields.items()})
        finally:
            self.loading_settings = False
        self.save_preference('theme',self.theme_selector.currentData())
        self.view.update()

    def reset_settings(self):
        """Reset all registered options to their section defaults.

        Side effects:
            Applies layout defaults before other sections, updates the
            in-memory preference dictionary, persists the current theme and
            repaints the map view.
        """
        self.loading_settings = True
        try:
            for callback in self.option_resets[SECTION_LAYOUT]:
                callback()
            for section_id,callbacks in self.option_resets.items():
                if section_id != SECTION_LAYOUT:
                    for callback in callbacks:
                        callback()
            self.preferences.update({key:getter() for key,(getter,_,_) in self.setting_fields.items()})
        finally:
            self.loading_settings = False
        self.save_preference('theme',self.theme_selector.currentData())
        self.view.update()

    def system_theme_changed(self, scheme):
        """React to platform colour-scheme changes in ``system`` theme mode.

        Args:
            scheme: New Qt platform colour scheme supplied by
                ``colorSchemeChanged``.

        Side effects:
            Reapplies the active theme only when the stored theme is ``system``.
        """
        if self.preferences.get('theme', THEME_SYSTEM) == THEME_SYSTEM:
            self.apply_theme(THEME_SYSTEM)

    def resize_buttons(self, height):
        """Resize fixed-width command buttons to a shared height.

        Args:
            height: Button height in pixels, normally from the ``button_height``
                option whose visible range is 28..44 px and default is 34 px.
        """
        for button,width in self.fixed_buttons:
            button.setFixedSize(width,height)

    def toggle_options_panel(self, visible):
        """Show or hide the options panel.

        Args:
            visible: Whether the panel should be visible.

        Side effects:
            Opening the panel pauses steering assistance because changing
            controls or bindings while assistance is active would be unsafe and
            distracting.
        """
        if visible:
            self.pause_assistance('Configurações abertas')
        self.options_panel.setVisible(visible)

    def save_preference(self, key, value):
        """Persist one preference while preserving unrelated existing keys.

        Args:
            key: Preference key to update.
            value: New value. Theme values are normalized to stable IDs.

        Side effects:
            Updates ``self.preferences`` and writes the whole dictionary to
            ``options.json`` via ``settings_persistence.save_preferences``.
            Keeping the dictionary intact preserves keys owned by other
            features instead of dropping them during a single-option save.
        """
        if key == 'theme':
            value = normalize_theme_value(value)
            if value is None:
                return
        self.preferences[key] = value
        if self.loading_settings:
            return
        try:
            save_preferences(self.options_path, self.preferences)
        except OSError as exc:
            self.statusBar().showMessage(translate(
                'LayoutOptions', 'Could not save settings: {error}').format(error=exc))

    def apply_theme(self, theme):
        """Apply the selected theme to the window, map view and library.

        Args:
            theme: Stable or legacy theme value. Supported stable IDs are
                ``system``, ``dark`` and ``light``.

        Side effects:
            Rebuilds the stylesheet, updates the map view theme flag, restores
            default map colours for unset keys, forwards the theme to the map
            library when it exists, and repaints the view.
        """
        dark = _theme_is_dark(theme, QApplication.styleHints().colorScheme())
        colors = _widget_theme_colors(dark)
        arrow_root = (Path(__file__).resolve().parent / 'assets').as_posix()
        self.setStyleSheet(_theme_stylesheet(colors, arrow_root))
        self.view.dark_theme = dark
        for key in ('map_background','grid_color'):
            if key not in self.preferences:
                value = self.view.default_map_color(key)
                self.view.colors[key] = value
                self.color_buttons[key].setText(value)
                self.color_buttons[key].setStyleSheet(f'border-left: 18px solid {value}; padding: 5px;')
        map_library = getattr(self, 'map_library', None)
        if map_library is not None:
            map_library.set_theme(dark)
        self.view.update()


from PySide6.QtWidgets import QApplication
