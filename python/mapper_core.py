"""Qt-free domain core for Rhino Surface Mapper.

This module owns telemetry ingestion, map state, circular search navigation,
overlay guidance data, and validation of persisted maps. It deliberately has
no PySide6 dependency: UI code supplies status dictionaries and consumes plain
Python values. Coordinates are kept in a local metre projection anchored to a
map centre; persisted state contains durable exploration data only.
"""

from __future__ import annotations

import math
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from map_persistence import (
    is_map_file_protected,
    read_file_timestamps,
    read_map_json,
    update_map_file_flags,
    write_map_json,
)

# The game exposes status flags as a bit field; this bit means the commander is
# currently driving the SRV/Rhino, which gates mapping and overlay behaviour.
SRV_FLAG = 0x04000000
DEFAULT_RADIUS_M = 6_371_000.0
SEARCH_RADIUS_M = 3_500.0
SEARCH_SPACING_M = 1_800.0


@dataclass(frozen=True)
class StatusUpdate:
    """Result of processing one telemetry status."""

    accepted: bool
    location_changed: bool = False
    system: str = ""
    body: str = ""
    latitude: float | None = None
    longitude: float | None = None

    def __bool__(self) -> bool:
        """Preserve the historical boolean contract for existing callers."""
        return self.accepted


class MapperState:
    """Mutable map and navigation state independent of any graphical toolkit.

    The instance owns one loaded or in-progress map plus live telemetry that is
    never serialized. Lists contain plain dictionaries because map files are
    JSON documents. ``protected`` and ``mining_only`` make the state read-only;
    ``mining_only`` is session state and must not be written to disk.
    """

    def __init__(self) -> None:
        """Initialize telemetry, map, and search state without UI ownership.

        ``None`` means that a value is not known yet. Empty lists mean that the
        map has no stored records of that type.
        """
        self.status_path: Path | None = None
        self.points: list[dict[str, Any]] = []
        self.radar_coverage = []
        self.map_generation = 0
        self.deposits: list[dict[str, Any]] = []
        self.rigs: list[dict[str, Any]] = []
        self.marks: list[dict[str, Any]] = []
        self.system = ""
        self.body = ""
        self.body_key: str | None = None
        # Optional metadata: old maps do not have these fields and remain valid.
        self.created_at: str | None = None
        self.last_saved_at: str | None = None
        self.favorite = False
        self.protected = False
        self.mining_only = False  # Session-only mode; it is never serialized.
        # PML identifies the exploration area on a body. The map centre remains
        # a technical projection anchor; the PML centre is the geographic point
        # used to rediscover the same map on return.
        self.pml_id = ""
        self.pml_center_lat: float | None = None
        self.pml_center_lon: float | None = None
        self.center_lat: float | None = None
        self.center_lon: float | None = None
        self.radius = DEFAULT_RADIUS_M
        self.last_xy: tuple[float, float] | None = None
        self.rhino_lat: float | None = None
        self.rhino_lon: float | None = None
        self.rhino_heading: float | None = None
        self.fuel_reservoir: float | None = None
        self.fuel_percent: float | None = None
        self.fuel_low = False
        self.coverage_width_m = 2_000.0
        self.scanner_range_m = 2_000.0
        self.center_enabled = False
        self.search_azimuth = 0
        self.search_started = False
        self.datum_lat: float | None = None
        self.datum_lon: float | None = None
        self.route_index = 0
        self.next_target_xy: tuple[float, float] | None = None
        self.route_history: list[dict[str, Any]] = []
        self.overlay_blink_on = True
        self.overlay_next_blink = 0.0
        self.active_nav_target: dict[str, Any] | None = None
        self.search_paused = False
        self.search_pause_point: tuple[float, float] | None = None
        self.return_to_pause = False
        self.in_srv = False

    def overlay_allowed(self) -> bool:
        """Return whether the navigation overlay is meaningful for this state."""
        if not self.in_srv:
            return False
        return bool(self.search_started or self.active_nav_target is not None or self.return_to_pause)

    @staticmethod
    def heading_error(current: float, target: float) -> float:
        """Return signed angular error; negative values mean turn left."""
        # Modulo arithmetic crosses 359°/000° and chooses an error in [-180, 180).
        return (target - current + 540.0) % 360.0 - 180.0

    def llxy(self, lat: float, lon: float) -> tuple[float, float]:
        """Convert latitude/longitude to local metres relative to the map centre.

        Raises:
            RuntimeError: if no projection centre has been established yet.
        """
        if self.center_lat is None or self.center_lon is None:
            raise RuntimeError("O centro do mapa ainda não foi definido.")
        # Local equirectangular projection: latitude metres use the planet
        # radius directly; longitude metres shrink by cos(latitude) as the
        # centre moves away from the equator.
        cosine = math.cos(math.radians(self.center_lat))
        return (
            math.radians(lon - self.center_lon) * self.radius * cosine,
            math.radians(lat - self.center_lat) * self.radius,
        )

    def xyll(self, x: float, y: float) -> tuple[float, float]:
        """Convert local metres relative to the map centre to latitude/longitude.

        Raises:
            RuntimeError: if no projection centre has been established yet.
        """
        if self.center_lat is None or self.center_lon is None:
            raise RuntimeError("O centro do mapa ainda não foi definido.")
        # Inverse of ``llxy``: undo the local metre scaling before converting
        # radians back to degrees.
        cosine = math.cos(math.radians(self.center_lat))
        return (
            self.center_lat + math.degrees(y / self.radius),
            self.center_lon + math.degrees(x / (self.radius * cosine)),
        )

    def new_map(self, keep_pml: bool = False) -> None:
        """Clear exploration data and search state while keeping live telemetry.

        ``keep_pml`` is used by the New action to start another recording in
        the same PML. A real body change uses the default and clears PML data.
        """
        self.favorite = False
        self.protected = False
        self.mining_only = False
        self.points.clear()
        self.radar_coverage.clear()
        self.map_generation += 1
        self.deposits.clear()
        self.rigs.clear()
        self.marks.clear()
        self.created_at = None
        self.last_saved_at = None
        self.last_xy = None
        self.search_started = False
        self.search_paused = False
        self.search_pause_point = None
        self.return_to_pause = False
        self.active_nav_target = None
        self.datum_lat = None
        self.datum_lon = None
        if not keep_pml:
            self.pml_id = ""
            self.pml_center_lat = None
            self.pml_center_lon = None
        self.route_index = 0
        self.next_target_xy = None
        self.route_history.clear()

    def process_status(self, status: dict[str, Any], *, record_position: bool = True) -> StatusUpdate:
        """Update state from one Elite Dangerous ``Status.json`` document.

        The update is accepted only while the commander is in the SRV/Rhino.
        Fuel, heading, current body identity, local projection centre, trail
        samples, and the current search target may be updated.

        Args:
            status: Parsed status document supplied by the UI/polling layer.
            record_position: When false, validate identity and coordinates
                without moving live Rhino position or appending trail points.

        Returns:
            A ``StatusUpdate`` describing whether the sample was accepted and
            whether the caller should handle a body/location change.
        """
        flags = int(status.get("Flags", 0))
        self.in_srv = bool(flags & SRV_FLAG)
        if not self.in_srv:
            return StatusUpdate(False)

        fuel = status.get("Fuel") or {}
        reservoir = fuel.get("FuelReservoir")
        if reservoir is not None:
            self.fuel_reservoir = float(reservoir)
            # Elite reports the SRV/Rhino reservoir as a 0.0-0.80 value; in
            # game UI terms 0.80 represents a full 100% tank.
            self.fuel_percent = max(0.0, min(100.0, self.fuel_reservoir / 0.80 * 100.0))
            self.fuel_low = bool(int(status.get("Flags", 0)) & 0x00080000)

        heading = status.get("Heading")
        if heading is not None:
            try:
                self.rhino_heading = float(heading) % 360.0
            except (TypeError, ValueError):
                self.rhino_heading = None

        lat, lon = status.get("Latitude"), status.get("Longitude")
        if lat is None or lon is None:
            return StatusUpdate(False)
        latitude, longitude = float(lat), float(lon)

        system, body = status.get("StarSystem", ""), status.get("BodyName", "")
        # Status.json may omit StarSystem for several seconds. If the body is
        # unchanged, preserving the last valid system avoids treating each
        # update as a new body and clearing the map.
        if not system and self.system and body == self.body:
            system = self.system
        body_key = f"{system}|{body}"
        if system and not self.system and self.body and body == self.body:
            self.system = system
            self.body_key = body_key
        if self.body_key and self.body_key != body_key:
            return StatusUpdate(True, True, system, body, latitude, longitude)
        if record_position:
            self.rhino_lat, self.rhino_lon = latitude, longitude
        if self.read_only and self.body_key != body_key:
            # Inspecting another body must not clear a protected/read-only map.
            return StatusUpdate(True, True, system, body, latitude, longitude)
        # A new body defines a new coordinate frame; trails from different
        # bodies cannot share the same local metre coordinates.
        if self.body_key != body_key:
            self.body_key, self.system, self.body = body_key, system, body
            self.center_lat, self.center_lon = self.rhino_lat, self.rhino_lon
            self.radius = float(status.get("PlanetRadius") or DEFAULT_RADIUS_M)
            self.new_map()

        if not record_position:
            return StatusUpdate(True, False, system, body, latitude, longitude)

        x, y = self.llxy(self.rhino_lat, self.rhino_lon)
        # Trail samples are kept at least 10 m apart to limit file size and
        # visual noise. Jumps over 100 m mark ``break_before`` so drawing code
        # does not invent a continuous line across teleports or stale samples.
        if not self.read_only and (self.last_xy is None or math.hypot(x - self.last_xy[0], y - self.last_xy[1]) >= 10.0):
            point = {"x": x, "y": y, "lat": self.rhino_lat, "lon": self.rhino_lon,
                     "t": status.get("timestamp", time.time())}
            if self.last_xy is not None and math.hypot(x - self.last_xy[0], y - self.last_xy[1]) > 100.0:
                point["break_before"] = True
            self.points.append(point)
            self.last_xy = (x, y)

        self.update_next()
        return StatusUpdate(True, False, system, body, latitude, longitude)

    def start_search(self, azimuth: int = 0) -> bool:
        """Set the Datum at the current Rhino position and prepare the route.

        Args:
            azimuth: Initial search bearing in degrees, where 0 is north and
                values increase clockwise.

        Returns:
            True when the search could be started; false for read-only or
            incomplete telemetry states.

        Raises:
            ValueError: if ``azimuth`` is not an integer in the inclusive
                000-359 degree range.
        """
        if self.read_only:
            return False
        if type(azimuth) is not int or not 0 <= azimuth <= 359:
            raise ValueError("AZ Busca deve ser um inteiro entre 000 e 359.")
        if self.rhino_lat is None or self.rhino_lon is None or self.center_lat is None:
            return False
        self.search_azimuth = azimuth
        self.datum_lat, self.datum_lon = self.rhino_lat, self.rhino_lon
        self.search_started = True
        self.search_paused = False
        self.search_pause_point = None
        self.return_to_pause = False
        self.route_index = 0
        self.next_target_xy = None
        self.route_history.clear()
        self.update_next()
        return True

    @property
    def search_total_points(self) -> int:
        """Return how many targets cover the circular search route.

        ``ceil`` rounds up so the arc spacing never exceeds the configured
        spacing. With the current 3.5 km radius and 1.8 km spacing this yields
        the intended 13-point route.
        """
        return max(1, math.ceil((2.0 * math.pi * SEARCH_RADIUS_M) / SEARCH_SPACING_M))

    def skip_next(self) -> bool:
        """Mark the current search target as skipped and advance to the next."""
        if self.read_only:
            return False
        if not self.search_started or self.next_target_xy is None:
            return False
        tx, ty = self.next_target_xy
        self.route_history.append({"number": self.route_index + 1, "x": tx, "y": ty, "status": "skipped"})
        self.route_index += 1
        if self.route_index >= self.search_total_points:
            self.next_target_xy = None
        else:
            self.update_next()
        return True

    def update_next(self) -> None:
        """Refresh the circular search target and auto-advance on arrival.

        The route is a ring around the Datum. The first point starts at the
        configured azimuth, with 0° pointing north and subsequent points moving
        clockwise. A target is considered reached inside 100 m, matching the
        overlay arrival rule.
        """
        if self.read_only:
            self.next_target_xy = None
            return
        if (not self.search_started or self.datum_lat is None or self.datum_lon is None
                or self.route_index >= self.search_total_points):
            self.next_target_xy = None
            return

        datum_x, datum_y = self.llxy(self.datum_lat, self.datum_lon)
        # The route is generated as X=sin(angle), Y=cos(angle): angle zero is
        # north, and increasing angles move clockwise around the Datum.
        step_angle = (2.0 * math.pi) / self.search_total_points
        angle = math.radians(self.search_azimuth) + self.route_index * step_angle
        target_x = datum_x + SEARCH_RADIUS_M * math.sin(angle)
        target_y = datum_y + SEARCH_RADIUS_M * math.cos(angle)
        self.next_target_xy = (target_x, target_y)

        if self.rhino_lat is None or self.rhino_lon is None:
            return
        rhino_x, rhino_y = self.llxy(self.rhino_lat, self.rhino_lon)
        if math.hypot(target_x - rhino_x, target_y - rhino_y) > 100.0:
            return

        self.route_history.append({"number": self.route_index + 1, "x": target_x, "y": target_y, "status": "reached"})
        self.route_index += 1
        if self.route_index >= self.search_total_points:
            self.next_target_xy = None
            return
        angle = math.radians(self.search_azimuth) + self.route_index * step_angle
        self.next_target_xy = (
            datum_x + SEARCH_RADIUS_M * math.sin(angle),
            datum_y + SEARCH_RADIUS_M * math.cos(angle),
        )

    def overlay_navigation(self, now: float | None = None) -> tuple[str, str, str, str, str]:
        """Return overlay heading text, distance text, colors, and target name.

        The method chooses the active target in priority order: explicit marker
        navigation, return-to-pause, then circular search. It computes bearing
        and signed heading error from live Rhino telemetry, maps the error to
        guidance colors, blinks close distances, and applies the 100 m arrival
        side effects for marker and pause navigation.

        Args:
            now: Optional monotonic timestamp used by tests to control blinking.

        Returns:
            ``(heading, distance, heading_color, distance_color, target_name)``.
            Placeholder dashes are returned when navigation is unavailable.
        """
        target_name = ""
        tx, ty = None, None

        if self.active_nav_target is not None:
            tx = self.active_nav_target.get("x")
            ty = self.active_nav_target.get("y")
            target_name = self.active_nav_target.get("name", "")
        elif self.return_to_pause and self.search_pause_point is not None:
            tx, ty = self.search_pause_point
            target_name = "Ponto de Pausa ⏸"
        elif self.search_started and not self.search_paused and self.next_target_xy is not None:
            tx, ty = self.next_target_xy
            target_name = f"Busca: Ponto {self.route_index + 1}"

        if (not self.in_srv or tx is None or ty is None or self.rhino_lat is None
                or self.rhino_lon is None or self.rhino_heading is None):
            return "—", "—", "#888888", "white", target_name

        rx, ry = self.llxy(self.rhino_lat, self.rhino_lon)
        distance = math.hypot(tx - rx, ty - ry)
        bearing = (math.degrees(math.atan2(tx - rx, ty - ry)) + 360.0) % 360.0
        error = MapperState.heading_error(self.rhino_heading, bearing)
        # Tight ±2° alignment is green, the wider ±8° correction band is yellow,
        # and larger errors are red so the overlay communicates urgency.
        if abs(error) <= 2.0:
            color = "#00cc44"
        elif abs(error) <= 8.0:
            color = "#ffd21c"
        else:
            color = "#ff3030"

        # Three arrows are enough inside the correction band; five arrows make
        # large turns more visible without changing the target bearing text.
        arrows = 3 if abs(error) <= 8 else 5
        if error < -2.0:
            heading = f"{'<' * arrows} {bearing:03.0f}°"
        elif error > 2.0:
            heading = f"{bearing:03.0f}° {'»' * arrows}"
        else:
            heading = f"{bearing:03.0f}°"

        # ``monotonic`` measures intervals without depending on system clock
        # adjustments. The 0.45 s toggle blinks inside 300 m without sleeping,
        # so the UI remains responsive.
        if distance <= 300.0:
            now = time.monotonic() if now is None else now
            if now >= self.overlay_next_blink:
                self.overlay_blink_on = not self.overlay_blink_on
                self.overlay_next_blink = now + 0.45
            distance_color = "white" if self.overlay_blink_on else "#202020"
        else:
            self.overlay_blink_on, self.overlay_next_blink = True, 0.0
            distance_color = "white"

        # Arrival at 100 m ends direct navigation. If search was paused for a
        # marker, the next leg returns to the saved pause point before resuming.
        if distance <= 100.0:
            if self.active_nav_target is not None:
                self.navigation_arrivals = getattr(self, 'navigation_arrivals', 0)+1
                self.active_nav_target = None
                if self.search_paused and self.search_pause_point is not None:
                    self.return_to_pause = True
                else:
                    self.search_paused = False
            elif self.return_to_pause:
                self.return_to_pause = False
                self.search_paused = False
                self.search_pause_point = None

        return heading, f"{distance:.0f} m", color, distance_color, target_name

    def to_dict(self) -> dict[str, Any]:
        """Serialize durable map state using the established JSON shape.

        Live telemetry, ``mining_only``, and ``map_generation`` are omitted on
        purpose: they are session/UI state, not map data to reload later.
        """
        return {
            "system": self.system, "body": self.body,
            "created_at": self.created_at, "last_saved_at": self.last_saved_at,
            "favorite": self.favorite, "protected": self.protected,
            "pml_id": self.pml_id, "pml_center_lat": self.pml_center_lat,
            "pml_center_lon": self.pml_center_lon, "center_lat": self.center_lat,
            "center_lon": self.center_lon, "planet_radius": self.radius,
            "coverage_width_m": self.coverage_width_m, "scanner_range_m": self.scanner_range_m,
            "search_started": self.search_started, "datum_lat": self.datum_lat,
            "search_azimuth": self.search_azimuth,
            "datum_lon": self.datum_lon, "route_index": self.route_index,
            "route_history": self.route_history, "points": self.points,
            "radar_coverage": self.radar_coverage,
            "deposits": self.deposits, "rigs": self.rigs, "marks": self.marks,
        }

    def save(self, path: Path, update_saved_at: bool = True) -> None:
        """Write the current map as UTF-8 JSON.

        ``ensure_ascii=False`` is handled by the persistence layer so names stay
        readable. I/O and permission errors intentionally propagate to the UI
        dialog that initiated the save.

        Raises:
            PermissionError: if the session is mining-only or the target file is
                protected on disk.
        """
        path = Path(path)
        if self.mining_only:
            raise PermissionError('Só minerar não permite gravar alterações. Abre uma nova versão para explorar.')
        if is_map_file_protected(path):
            raise PermissionError('Este mapa está protegido. Cria uma nova versão para continuar a exploração.')
        # Only maps that already carry metadata receive a new saved timestamp;
        # we do not invent a creation date for old maps during a normal save.
        if self.created_at is not None and update_saved_at:
            self.last_saved_at = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
        write_map_json(path, self.to_dict())

    @property
    def read_only(self):
        """Return whether exploration changes are currently forbidden."""
        return getattr(self, 'protected', False) or getattr(self, 'mining_only', False)

    def enter_mining_mode(self):
        """Suspend exploration/search while keeping existing records viewable."""
        self.mining_only = True
        self.search_started = False
        self.search_paused = False
        self.search_pause_point = None
        self.return_to_pause = False
        self.active_nav_target = None
        self.next_target_xy = None

    @staticmethod
    def set_file_flags(path, *, favorite, protected):
        """Change only management flags while preserving map content and dates.

        This is the explicit operation that can remove protection. It reads the
        current JSON so an old library copy cannot overwrite newer map content.
        """
        update_map_file_flags(path, favorite=favorite, protected=protected)

    def populate_missing_timestamps(self, path: Path) -> bool:
        """Fill absent map dates from legacy file metadata.

        On Windows, ``st_ctime`` is file creation time and ``st_mtime`` is last
        modification time. Returns true only when new JSON data should be saved,
        preserving historical dates instead of the migration date.
        """
        created_at, last_saved_at = read_file_timestamps(path)
        changed = False
        if self.created_at is None:
            self.created_at = created_at
            changed = True
        if self.last_saved_at is None:
            self.last_saved_at = last_saved_at
            changed = True
        return changed

    def load(self, path: Path) -> None:
        """Load, validate, and install a map state from disk.

        A candidate state is populated and validated before replacing
        ``self.__dict__``. If parsing or validation fails, the current in-memory
        state remains intact instead of becoming partially loaded.
        """
        candidate = MapperState()
        candidate._load_file(path)
        candidate.validate_map()
        candidate.update_next()
        # We only reach this point after validation; any earlier error leaves
        # the existing state untouched.
        self.__dict__.update(candidate.__dict__)

    def validate_map(self) -> None:
        """Validate and normalize a loaded map.

        The method enforces expected types, coordinate bounds, search invariants,
        marker/deposit schemas, and radar pulse limits. It also backfills
        latitude/longitude for legacy marker-like records from stored local
        metre coordinates.

        Raises:
            ValueError: for structurally invalid or out-of-range map data.
            TypeError: when numeric coercion receives an incompatible value.
            KeyError: when required fields are missing from item dictionaries.
        """
        def number(value):
            """Convert a finite numeric value for geometry and rendering."""
            result = float(value)
            if not math.isfinite(result):
                raise ValueError("O mapa contém um número não finito.")
            return result

        if not isinstance(self.system, str) or not isinstance(self.body, str):
            raise ValueError("Sistema e corpo devem ser texto.")
        for value in (self.created_at, self.last_saved_at):
            if value is not None and not isinstance(value, str):
                raise ValueError("Data do mapa inválida.")
        if not isinstance(self.pml_id, str):
            raise ValueError("Identificador do PML inválido.")
        if (self.pml_center_lat is None) != (self.pml_center_lon is None):
            raise ValueError("Centro do PML incompleto.")
        if self.pml_center_lat is not None:
            self.pml_center_lat, self.pml_center_lon = (
                number(self.pml_center_lat), number(self.pml_center_lon))
            if not (-90 <= self.pml_center_lat <= 90 and -180 <= self.pml_center_lon <= 180):
                raise ValueError("Centro do PML fora dos limites.")
        self.center_lat, self.center_lon = number(self.center_lat), number(self.center_lon)
        if not (-90 < self.center_lat < 90 and -180 <= self.center_lon <= 180):
            raise ValueError("Centro do mapa fora dos limites suportados.")
        if number(self.radius) <= 0:
            raise ValueError("Raio do planeta inválido.")
        if not (100 <= number(self.coverage_width_m) <= 5000 and 500 <= number(self.scanner_range_m) <= 5000):
            raise ValueError("Cobertura ou scanner fora dos limites.")
        if type(self.search_azimuth) is not int or not 0 <= self.search_azimuth <= 359:
            raise ValueError("AZ Busca deve ser um inteiro entre 000 e 359.")
        if not 0 <= self.route_index <= self.search_total_points:
            raise ValueError("Índice da busca inválido.")
        if self.search_started and (self.datum_lat is None or self.datum_lon is None):
            raise ValueError("Busca ativa sem Datum.")
        if (self.datum_lat is None) != (self.datum_lon is None):
            raise ValueError("Datum incompleto.")
        if self.datum_lat is not None:
            if not (-90 <= number(self.datum_lat) <= 90 and -180 <= number(self.datum_lon) <= 180):
                raise ValueError("Datum inválido.")
        for items in (self.points, self.deposits, self.rigs, self.marks, self.route_history):
            if not isinstance(items, list):
                raise ValueError("A coleção de marcadores deve ser uma lista.")
            for item in items:
                if not isinstance(item, dict):
                    raise ValueError("Marcador inválido.")
                item['x'], item['y'] = number(item['x']), number(item['y'])
        for item in self.points + self.deposits + self.rigs + self.marks:
            if 'lat' not in item or 'lon' not in item:
                item['lat'], item['lon'] = self.xyll(item['x'], item['y'])
            item['lat'], item['lon'] = number(item['lat']), number(item['lon'])
            if not (-90 <= item['lat'] <= 90 and -180 <= item['lon'] <= 180):
                raise ValueError("Coordenadas do marcador inválidas.")
        for mark in self.marks:
            if not isinstance(mark.get("name"), str) or not mark["name"].strip():
                raise ValueError("Nome da marca inválido.")
        for deposit in self.deposits:
            if not isinstance(deposit.get('name', ''), str):
                raise ValueError("Nome do depósito inválido.")
            if deposit['size'] not in ('Pequeno', 'Médio', 'Grande', 'Enorme'):
                raise ValueError("Tamanho do depósito inválido.")
            count = number(deposit['rigs'])
            if not count.is_integer() or not 1 <= count <= 6:
                raise ValueError("Número de rigs inválido.")
            deposit['rigs'] = int(count)
        for item in self.route_history:
            if item.get('status') not in ('reached', 'skipped'):
                raise ValueError("Estado do destino inválido.")
            value = number(item['number'])
            if not value.is_integer() or not 1 <= value <= self.search_total_points:
                raise ValueError("Número do destino inválido.")
            item['number'] = int(value)
        self.last_xy = ((self.points[-1]['x'], self.points[-1]['y']) if self.points else None)
        if not isinstance(self.radar_coverage, list):
            raise ValueError('Cobertura do radar inválida.')
        for pulse in self.radar_coverage:
            if not isinstance(pulse, dict):
                raise ValueError('Pulso de radar inválido.')
            for key in ('x', 'y', 'radius'):
                pulse[key] = number(pulse[key])
            if not 0 <= pulse['radius'] <= 5000:
                raise ValueError('Raio de cobertura inválido.')

    def _load_file(self, path: Path) -> None:
        """Deserialize JSON fields into this candidate state.

        This internal method intentionally performs only direct loading and
        light type coercion. Call ``load`` externally so the result is validated
        before it can replace an existing state.
        """
        data = read_map_json(path)
        self.system, self.body = data.get("system", ""), data.get("body", "")
        self.created_at = data.get("created_at")
        self.last_saved_at = data.get("last_saved_at")
        self.favorite = data.get('favorite', False)
        self.protected = data.get('protected', False)
        if type(self.favorite) is not bool or type(self.protected) is not bool:
            raise ValueError('Favorito e proteção inválidos no mapa.')
        self.body_key = f"{self.system}|{self.body}"
        self.pml_id = data.get("pml_id", "")
        self.pml_center_lat = data.get("pml_center_lat")
        self.pml_center_lon = data.get("pml_center_lon")
        if self.pml_center_lat is not None:
            self.pml_center_lat = float(self.pml_center_lat)
        if self.pml_center_lon is not None:
            self.pml_center_lon = float(self.pml_center_lon)
        self.center_lat, self.center_lon = float(data["center_lat"]), float(data["center_lon"])
        self.radius = float(data.get("planet_radius", DEFAULT_RADIUS_M))
        self.coverage_width_m = float(data.get("coverage_width_m", 500.0))
        self.scanner_range_m = float(data.get("scanner_range_m", 2_000.0))
        self.search_started = bool(data.get("search_started", False))
        self.search_azimuth = data.get("search_azimuth", 0)
        self.datum_lat, self.datum_lon = data.get("datum_lat"), data.get("datum_lon")
        if self.datum_lat is not None:
            self.datum_lat = float(self.datum_lat)
        if self.datum_lon is not None:
            self.datum_lon = float(self.datum_lon)
        self.route_index = int(data.get("route_index", 0))
        self.marks = data.get("marks", [])
        self.radar_coverage = data.get('radar_coverage', [])
        self.route_history = data.get("route_history", [])
        self.points, self.deposits, self.rigs = data.get("points", []), data.get("deposits", []), data.get("rigs", [])
        for deposit in self.deposits:
            deposit.setdefault("size", "Pequeno")
            deposit.setdefault("rigs", 1)
        self.last_xy = ((self.points[-1]["x"], self.points[-1]["y"]) if self.points else None)
