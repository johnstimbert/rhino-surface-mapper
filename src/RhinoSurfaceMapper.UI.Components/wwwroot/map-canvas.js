// map-canvas.js — JS-owned half of the map canvas, per DESIGN_DOTNET_PORT.md's "Map canvas"
// section: "JS owns pan/zoom gestures and the requestAnimationFrame draw loop, calling back
// into .NET only for semantic events". Phase 3 (read-only map display) draws background + trail
// only; coverage discs, markers and rig icons are later phases (the .NET MapScene DTO already
// carries always-empty arrays for them so this file does not need to change shape later, only
// add drawing for those arrays).
//
// The world<->screen transform below is the exact JS twin of
// RhinoSurfaceMapper.UI.Components.Map.MapTransform: screenX = width/2 + (worldX-centerX)*scale,
// screenY = height/2 - (worldY-centerY)*scale, initial scale 0.08, zoom clamp [0.001, 10], wheel
// factor 1.15^(delta/120) anchored under the cursor. The .NET copy remains the authority used for
// hit-testing and automated tests; this copy exists only so panning/zooming feels immediate
// without a .NET round-trip on every mouse-move.

const MIN_SCALE = 0.001;
const MAX_SCALE = 10.0;
const INITIAL_SCALE = 0.08;
const WHEEL_ZOOM_BASE = 1.15;

const TRAIL_COLOR = '#58d0ff';
const BACKGROUND_COLOR = '#10151a';
const GRID_COLOR = '#1f2b33';
const GRID_SPACING_METRES = 500;
const RHINO_COLOR = '#ffb020';

/**
 * Creates one map canvas instance bound to a <canvas> element. Returns an object exposing
 * `pushScene`/`dispose`, invoked from .NET through MapCanvasInterop.
 * @param {HTMLCanvasElement} canvasElement
 */
export function createMapCanvas(canvasElement) {
    const ctx = canvasElement.getContext('2d');

    const state = {
        scale: INITIAL_SCALE,
        centerX: 0,
        centerY: 0,
        dragging: false,
        lastPointerX: 0,
        lastPointerY: 0,
    };

    // Latest scene pushed from .NET. Kept as flat arrays (not objects per point) to avoid
    // allocating thousands of small objects per frame for large trails.
    let scene = {
        generation: 0,
        rhinoX: null,
        rhinoY: null,
        rhinoHeadingDegrees: null,
        trailX: new Float64Array(0),
        trailY: new Float64Array(0),
        trailBreak: new Uint8Array(0),
    };

    let disposed = false;
    let resizeObserver = null;

    function resizeToElement() {
        const rect = canvasElement.getBoundingClientRect();
        const dpr = window.devicePixelRatio || 1;
        const width = Math.max(1, Math.round(rect.width * dpr));
        const height = Math.max(1, Math.round(rect.height * dpr));
        if (canvasElement.width !== width || canvasElement.height !== height) {
            canvasElement.width = width;
            canvasElement.height = height;
        }
    }

    function toScreenX(worldX) {
        return (canvasElement.width / 2) + (worldX - state.centerX) * state.scale;
    }

    function toScreenY(worldY) {
        return (canvasElement.height / 2) - (worldY - state.centerY) * state.scale;
    }

    function toWorldX(screenX) {
        return state.centerX + (screenX - canvasElement.width / 2) / state.scale;
    }

    function toWorldY(screenY) {
        return state.centerY - (screenY - canvasElement.height / 2) / state.scale;
    }

    function clampScale(scale) {
        return Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale));
    }

    function drawGrid() {
        const spacingPx = GRID_SPACING_METRES * state.scale;
        if (spacingPx < 4) {
            return;
        }

        ctx.strokeStyle = GRID_COLOR;
        ctx.lineWidth = 1;
        ctx.beginPath();

        const originScreenX = toScreenX(0);
        for (let x = originScreenX % spacingPx; x < canvasElement.width; x += spacingPx) {
            ctx.moveTo(x, 0);
            ctx.lineTo(x, canvasElement.height);
        }

        const originScreenY = toScreenY(0);
        for (let y = originScreenY % spacingPx; y < canvasElement.height; y += spacingPx) {
            ctx.moveTo(0, y);
            ctx.lineTo(canvasElement.width, y);
        }

        ctx.stroke();
    }

    function drawTrail() {
        const count = scene.trailX.length;
        if (count === 0) {
            return;
        }

        ctx.strokeStyle = TRAIL_COLOR;
        ctx.lineWidth = Math.max(1, 2 * (window.devicePixelRatio || 1));
        ctx.beginPath();

        let penDown = false;
        for (let i = 0; i < count; i++) {
            const sx = toScreenX(scene.trailX[i]);
            const sy = toScreenY(scene.trailY[i]);
            if (!penDown || scene.trailBreak[i]) {
                ctx.moveTo(sx, sy);
                penDown = true;
            } else {
                ctx.lineTo(sx, sy);
            }
        }

        ctx.stroke();
    }

    function drawRhino() {
        if (scene.rhinoX === null || scene.rhinoY === null) {
            return;
        }

        const sx = toScreenX(scene.rhinoX);
        const sy = toScreenY(scene.rhinoY);
        const radius = Math.max(3, 5 * (window.devicePixelRatio || 1));

        ctx.fillStyle = RHINO_COLOR;
        ctx.beginPath();
        ctx.arc(sx, sy, radius, 0, Math.PI * 2);
        ctx.fill();

        if (scene.rhinoHeadingDegrees !== null) {
            const headingRad = (scene.rhinoHeadingDegrees * Math.PI) / 180;
            const length = radius * 3;
            ctx.strokeStyle = RHINO_COLOR;
            ctx.lineWidth = 2;
            ctx.beginPath();
            ctx.moveTo(sx, sy);
            ctx.lineTo(sx + Math.sin(headingRad) * length, sy - Math.cos(headingRad) * length);
            ctx.stroke();
        }
    }

    function draw() {
        if (disposed) {
            return;
        }

        resizeToElement();
        ctx.fillStyle = BACKGROUND_COLOR;
        ctx.fillRect(0, 0, canvasElement.width, canvasElement.height);

        drawGrid();
        drawTrail();
        drawRhino();

        requestAnimationFrame(draw);
    }

    function onWheel(event) {
        event.preventDefault();
        const rect = canvasElement.getBoundingClientRect();
        const dpr = window.devicePixelRatio || 1;
        const cursorScreenX = (event.clientX - rect.left) * dpr;
        const cursorScreenY = (event.clientY - rect.top) * dpr;

        const worldXUnderCursor = toWorldX(cursorScreenX);
        const worldYUnderCursor = toWorldY(cursorScreenY);

        const newScale = clampScale(state.scale * Math.pow(WHEEL_ZOOM_BASE, event.deltaY / -120));
        state.scale = newScale;

        state.centerX = worldXUnderCursor - (cursorScreenX - canvasElement.width / 2) / newScale;
        state.centerY = worldYUnderCursor + (cursorScreenY - canvasElement.height / 2) / newScale;
    }

    function onPointerDown(event) {
        state.dragging = true;
        state.lastPointerX = event.clientX;
        state.lastPointerY = event.clientY;
        canvasElement.setPointerCapture(event.pointerId);
    }

    function onPointerMove(event) {
        if (!state.dragging) {
            return;
        }

        const dpr = window.devicePixelRatio || 1;
        const dx = (event.clientX - state.lastPointerX) * dpr;
        const dy = (event.clientY - state.lastPointerY) * dpr;
        state.lastPointerX = event.clientX;
        state.lastPointerY = event.clientY;

        state.centerX -= dx / state.scale;
        state.centerY += dy / state.scale;
    }

    function onPointerUp(event) {
        state.dragging = false;
        canvasElement.releasePointerCapture(event.pointerId);
    }

    canvasElement.addEventListener('wheel', onWheel, { passive: false });
    canvasElement.addEventListener('pointerdown', onPointerDown);
    canvasElement.addEventListener('pointermove', onPointerMove);
    canvasElement.addEventListener('pointerup', onPointerUp);

    if (typeof ResizeObserver !== 'undefined') {
        resizeObserver = new ResizeObserver(() => resizeToElement());
        resizeObserver.observe(canvasElement);
    }

    requestAnimationFrame(draw);

    return {
        /**
         * Receives one scene tick from .NET. Called at ~20 Hz (50 ms) by MapScenePresenter;
         * never on every raw telemetry sample.
         * @param {{generation:number, rhinoX:?number, rhinoY:?number, rhinoHeadingDegrees:?number}} meta
         * @param {number[]} trailX
         * @param {number[]} trailY
         * @param {boolean[]} trailBreak
         */
        pushScene(meta, trailX, trailY, trailBreak) {
            scene = {
                generation: meta.generation,
                rhinoX: meta.rhinoX ?? null,
                rhinoY: meta.rhinoY ?? null,
                rhinoHeadingDegrees: meta.rhinoHeadingDegrees ?? null,
                trailX: Float64Array.from(trailX ?? []),
                trailY: Float64Array.from(trailY ?? []),
                trailBreak: Uint8Array.from((trailBreak ?? []).map((b) => (b ? 1 : 0))),
            };
        },

        /** Stops the draw loop and detaches all listeners. Idempotent. */
        dispose() {
            disposed = true;
            canvasElement.removeEventListener('wheel', onWheel);
            canvasElement.removeEventListener('pointerdown', onPointerDown);
            canvasElement.removeEventListener('pointermove', onPointerMove);
            canvasElement.removeEventListener('pointerup', onPointerUp);
            if (resizeObserver !== null) {
                resizeObserver.disconnect();
            }
        },
    };
}
