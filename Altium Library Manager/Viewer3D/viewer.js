import * as THREE from 'three';
import { OrbitControls } from 'three/addons/OrbitControls.js';

// Altium coordinates: X/Y is the board plane, Z points up; units are millimeters
THREE.Object3D.DEFAULT_UP.set(0, 0, 1);

const canvas = document.getElementById('view');
const statusEl = document.getElementById('status');

const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
renderer.setPixelRatio(window.devicePixelRatio);

const scene = new THREE.Scene();
scene.background = new THREE.Color(0xf4f5f7);

const camera = new THREE.PerspectiveCamera(35, 1, 0.01, 1000);
const controls = new OrbitControls(camera, canvas);
controls.enableDamping = true;
controls.addEventListener('change', requestRender);

scene.add(new THREE.HemisphereLight(0xffffff, 0x8a8f99, 1.6));
const keyLight = new THREE.DirectionalLight(0xffffff, 1.8);
camera.add(keyLight);
keyLight.position.set(1, 2, 3);
scene.add(camera);

let content = null;   // group of the footprint currently shown
const contentBounds = new THREE.Sphere();
let requestId = 0;

// --- STEP colors ---

// Used when neither the face, the solid nor the part has a color
const DEFAULT_MODEL_COLOR = new THREE.Color().setRGB(0.75, 0.75, 0.75, THREE.SRGBColorSpace);

const SOLID_TYPES = /^(MANIFOLD_SOLID_BREP|BREP_WITH_VOIDS|FACETED_BREP|SHELL_BASED_SURFACE_MODEL)$/;
const STYLE_TYPES = /^(PRESENTATION_STYLE_ASSIGNMENT|PRESENTATION_STYLE_BY_CONTEXT|SURFACE_STYLE_USAGE|SURFACE_SIDE_STYLE|SURFACE_STYLE_FILL_AREA|FILL_AREA_STYLE|FILL_AREA_STYLE_COLOUR|COLOUR_RGB|DRAUGHTING_PRE_DEFINED_COLOUR)$/;
const PREDEFINED_COLORS = {
    black: [0, 0, 0], white: [1, 1, 1], red: [1, 0, 0], green: [0, 1, 0], blue: [0, 0, 1],
    yellow: [1, 1, 0], magenta: [1, 0, 1], cyan: [0, 1, 1]
};

/**
 * STEP assigns colors at three levels: face, solid and the whole part (shape representation); the most
 * specific one wins. occt-import-js loses some of them: faces that inherit the solid color come back
 * uncolored, and in some files (e.g. TE connectors) it drops all face colors. So the styles are also read
 * from the STEP text here:
 *  - fallback: the solid (or part) color for faces without their own color;
 *  - shells: per shell, the face colors in shell order, which is the order OpenCascade explores faces in,
 *    so they can be matched to occt's brep_faces when occt reported none.
 */
function readStepColors(text) {
    const entities = new Map();
    for (const m of text.matchAll(/#(\d+)\s*=\s*([A-Z_0-9]+)\s*\(([^;]*)\)\s*;/g))
        entities.set(m[1], { type: m[2], args: m[3] });

    const colorOf = (id, depth = 0) => {
        const e = entities.get(id);
        if (!e || depth > 10 || !STYLE_TYPES.test(e.type)) return null;
        if (e.type === 'COLOUR_RGB') {
            const v = e.args.split(',').slice(1).map(Number);
            return v.length === 3 && v.every(Number.isFinite) ? v : null;
        }
        if (e.type === 'DRAUGHTING_PRE_DEFINED_COLOUR')
            return PREDEFINED_COLORS[(e.args.match(/'([^']*)'/)?.[1] || '').toLowerCase()] || null;
        for (const ref of e.args.matchAll(/#(\d+)/g)) {
            const c = colorOf(ref[1], depth + 1);
            if (c) return c;
        }
        return null;
    };

    const toColor = c => new THREE.Color().setRGB(c[0], c[1], c[2], THREE.SRGBColorSpace);

    const solidColors = [];
    const faceColors = new Map();   // face entity id → THREE.Color
    let partColor = null;
    for (const e of entities.values()) {
        if (e.type !== 'STYLED_ITEM' && e.type !== 'OVER_RIDING_STYLED_ITEM') continue;
        const refs = [...e.args.matchAll(/#(\d+)/g)].map(r => r[1]);
        const targetId = refs[refs.length - 1];
        const target = entities.get(targetId);
        if (!target) continue;
        const color = refs.slice(0, -1).map(id => colorOf(id)).find(c => c);
        if (!color) continue;
        if (/^(ADVANCED_FACE|FACE_SURFACE)$/.test(target.type)) faceColors.set(targetId, toColor(color));
        else if (SOLID_TYPES.test(target.type)) solidColors.push(color);
        else if (/SHAPE_REPRESENTATION$/.test(target.type)) partColor ??= color;
    }

    const shells = [];
    if (faceColors.size)
        for (const e of entities.values())
            if (e.type === 'CLOSED_SHELL' || e.type === 'OPEN_SHELL')
                shells.push([...e.args.matchAll(/#(\d+)/g)].map(r => faceColors.get(r[1]) || null));

    // Several solids with different colors can't be matched to meshes reliably; take the most common one
    const counts = new Map();
    for (const c of solidColors) {
        const key = c.join(',');
        counts.set(key, (counts.get(key) || 0) + 1);
    }
    const solid = [...counts].sort((a, b) => b[1] - a[1])[0]?.[0].split(',').map(Number);
    const fallback = solid || partColor;
    return { fallback: fallback ? toColor(fallback) : null, shells };
}

// --- STEP → meshes (OpenCascade compiled to WebAssembly) ---

const occtReady = occtimportjs({ locateFile: name => 'lib/' + name });

// Served from memory by the WPF host (PreviewPanel.LockDown); also allowed in the page's connect-src
const MODELS_URL = 'https://models.viewer3d.local/';

// Tessellating STEP is expensive, and the same package often appears in many rows. Bounded by count and by
// total vertices: a few detailed connector models can hold millions of them (~24 bytes each with normals).
const stepCache = new Map();
const STEP_CACHE_LIMIT = 30;
const STEP_CACHE_MAX_VERTICES = 4_000_000;
const vertexCount = meshes => meshes.reduce((n, m) => n + m.geometry.attributes.position.count, 0);

// Set by the app (Options → Preview) through an "options" message
const options = { showBoard: true, showPads: true, linearDeflection: 0.002 };
let lastFootprint = null;

/**
 * Returns the tessellated model for key, downloading the STEP file from the host only if it isn't cached.
 * The host serves models from memory at MODELS_URL/{key}.step, so the (often multi-megabyte) STEP text never goes
 * through a JSON message, and a model already seen is neither transferred nor parsed again.
 */
async function loadStep(key) {
    if (stepCache.has(key)) {
        const cached = stepCache.get(key);
        stepCache.delete(key);
        stepCache.set(key, cached);   // move to the most recently used position
        return cached;
    }

    const response = await fetch(MODELS_URL + encodeURIComponent(key) + '.step');
    if (!response.ok)
        throw new Error(`STEP model unavailable (${response.status})`);
    const stepBytes = new Uint8Array(await response.arrayBuffer());
    // STEP is ASCII, so the bytes go to OpenCascade as is and the text is only needed for reading colors
    const stepText = new TextDecoder('latin1').decode(stepBytes);

    const occt = await occtReady;
    const result = occt.ReadStepFile(stepBytes, {
        linearUnit: 'millimeter',
        linearDeflectionType: 'bounding_box_ratio',
        linearDeflection: options.linearDeflection,
        angularDeflection: 0.5
    });
    if (!result.success)
        throw new Error('failed to read STEP');

    const stepColors = readStepColors(stepText);
    const fallback = stepColors.fallback;
    const usedShells = new Set();

    const meshes = result.meshes.map(m => {
        const geometry = new THREE.BufferGeometry();
        geometry.setAttribute('position', new THREE.Float32BufferAttribute(m.attributes.position.array, 3));
        if (m.attributes.normal)
            geometry.setAttribute('normal', new THREE.Float32BufferAttribute(m.attributes.normal.array, 3));
        geometry.setIndex(new THREE.Uint32BufferAttribute(Uint32Array.from(m.index.array), 1));
        if (!m.attributes.normal)
            geometry.computeVertexNormals();

        // occt-import-js returns colors in linear space, which is three.js' working color space
        const baseColor = m.color ? new THREE.Color(m.color[0], m.color[1], m.color[2]) : (fallback || DEFAULT_MODEL_COLOR);

        // If occt reported no face colors, take them from the STEP shell with the same number of faces: an unused
        // one first; then any matching one again, since an assembly places the same part several times (e.g. a
        // header made of 8 instances of one segment) and each instance comes back as its own mesh. Without the
        // reuse, only the first instance got its colors, and the differently colored neighbors also showed their
        // coincident faces at the joints flickering through.
        const brepFaces = m.brep_faces || [];
        let shellColors = null;
        if (!brepFaces.some(f => f.color)) {
            let index = stepColors.shells.findIndex((s, i) => !usedShells.has(i) && s.length === brepFaces.length);
            if (index >= 0)
                usedShells.add(index);
            else
                index = stepColors.shells.findIndex(s => s.length === brepFaces.length);
            if (index >= 0)
                shellColors = stepColors.shells[index];
        }
        const faceColor = (face, i) =>
            face.color ? new THREE.Color(face.color[0], face.color[1], face.color[2]) : shellColors?.[i] || null;

        // One material per distinct face color (index 0 is the base color for uncolored faces)
        const colors = [baseColor];
        const indexByKey = new Map();
        const materialOf = color => {
            if (!color) return 0;
            const key = color.getHexString();
            if (!indexByKey.has(key)) {
                indexByKey.set(key, colors.length);
                colors.push(color);
            }
            return indexByKey.get(key);
        };

        // Triangle ranges per material; adjacent faces with the same material are merged into one draw group,
        // triangles not covered by any face fall back to the base color
        const triangleCount = m.index.array.length / 3;
        const ranges = [];
        let next = 0;
        const push = (first, last, material) => {
            const prev = ranges[ranges.length - 1];
            if (prev && prev.material === material && prev.last + 1 === first) prev.last = last;
            else ranges.push({ first, last, material });
        };
        const faces = brepFaces.map((face, i) => ({ first: face.first, last: face.last, material: materialOf(faceColor(face, i)) }));
        for (const face of faces.sort((a, b) => a.first - b.first)) {
            if (face.first > next) push(next, face.first - 1, 0);
            push(face.first, face.last, face.material);
            next = face.last + 1;
        }
        if (next < triangleCount) push(next, triangleCount - 1, 0);
        for (const r of ranges)
            geometry.addGroup(r.first * 3, (r.last - r.first + 1) * 3, r.material);

        return { geometry, colors };
    });

    stepCache.set(key, meshes);
    // Drop the least recently used models (Map keeps insertion order), but never the one just added
    let vertices = [...stepCache.values()].reduce((n, cached) => n + vertexCount(cached), 0);
    while (stepCache.size > 1 && (stepCache.size > STEP_CACHE_LIMIT || vertices > STEP_CACHE_MAX_VERTICES)) {
        const oldest = stepCache.keys().next().value;
        const dropped = stepCache.get(oldest);
        vertices -= vertexCount(dropped);
        dropped.forEach(m => m.geometry.dispose());
        stepCache.delete(oldest);
    }
    return meshes;
}

// --- Scene construction ---

const deg = THREE.MathUtils.degToRad;
// The model's seating plane (z = 0) is the top of the copper: the pads lie below it and the board below them, so the
// pads don't cover the bottom of small parts' terminals. The model's bottom and the pads' top still coincide, and
// coplanar surfaces flicker (z-fighting), so the pads and the board are pushed back in depth: the model wins.
const padMaterial = new THREE.MeshStandardMaterial({
    color: 0xd4a73c, metalness: 0.6, roughness: 0.35,
    polygonOffset: true, polygonOffsetFactor: 1, polygonOffsetUnits: 1
});
const boardMaterial = new THREE.MeshStandardMaterial({
    color: 0x2f6b3a, roughness: 0.8, transparent: true, opacity: 0.85,
    polygonOffset: true, polygonOffsetFactor: 2, polygonOffsetUnits: 2
});
// The board is only a backdrop, and the view is fitted to each part: so its thickness and margin are shares of the
// part's size, and every part (01005 or QFN-80) sits on a board that looks the same. A real 0.8 mm board would be a
// block under an 01005 and a sheet under a large connector.
const BOARD_THICKNESS_SHARE = 0.08;
const BOARD_MARGIN_SHARE = 0.25;
const COPPER = 0.035;
// The board top lies slightly below the pads so their bottoms never coincide with it
const PAD_LIFT = 0.002;
// (Without pads the model stands on the board itself)
const boardTop = () => options.showPads ? -(COPPER + PAD_LIFT) : 0;

function createPad(p) {
    let shape;
    if (p.Round && Math.abs(p.Width - p.Height) < 1e-6) {
        shape = new THREE.Shape();
        shape.absarc(0, 0, p.Width / 2, 0, Math.PI * 2);
    } else {
        // Oval pads get rounded ends
        const w = p.Width, h = p.Height, r = p.Round ? Math.min(w, h) / 2 : 0;
        shape = roundedRect(w, h, r);
    }
    if (p.Hole > 0) {
        const hole = new THREE.Path();
        hole.absarc(0, 0, p.Hole / 2, 0, Math.PI * 2, true);
        shape.holes.push(hole);
    }
    const mesh = new THREE.Mesh(new THREE.ExtrudeGeometry(shape, { depth: COPPER, bevelEnabled: false }), padMaterial);
    mesh.position.set(p.X, p.Y, -COPPER);
    mesh.rotation.z = deg(p.Rotation);
    return mesh;
}

function roundedRect(w, h, r) {
    const s = new THREE.Shape(), x = -w / 2, y = -h / 2;
    s.moveTo(x + r, y);
    s.lineTo(x + w - r, y);
    if (r) s.absarc(x + w - r, y + r, r, -Math.PI / 2, 0);
    s.lineTo(x + w, y + h - r);
    if (r) s.absarc(x + w - r, y + h - r, r, 0, Math.PI / 2);
    s.lineTo(x + r, y + h);
    if (r) s.absarc(x + r, y + h - r, r, Math.PI / 2, Math.PI);
    s.lineTo(x, y + r);
    if (r) s.absarc(x + r, y + r, r, Math.PI, Math.PI * 1.5);
    return s;
}

async function createBody(b) {
    const group = new THREE.Group();

    if (b.ModelKey) {
        for (const m of await loadStep(b.ModelKey)) {
            const materials = m.colors.map(color =>
                new THREE.MeshStandardMaterial({ color, roughness: 0.55, metalness: 0.1, side: THREE.DoubleSide }));
            group.add(new THREE.Mesh(m.geometry, materials));
        }
        // Altium rotates the model about X, then Y, then Z (board axes), then moves it into place
        group.rotation.set(deg(b.RotX), deg(b.RotY), deg(b.RotZ) + deg(b.Rotation2D), 'ZYX');
        group.position.set(b.X, b.Y, b.Z);
    } else {
        // Simple Altium body: an extruded outline
        const shape = new THREE.Shape(b.Outline.map(([x, y]) => new THREE.Vector2(x, y)));
        const depth = Math.max(b.Height - b.StandOff, 0.01);
        const material = new THREE.MeshStandardMaterial({
            color: b.Color, roughness: 0.6, transparent: b.Opacity < 1, opacity: b.Opacity
        });
        const mesh = new THREE.Mesh(new THREE.ExtrudeGeometry(shape, { depth, bevelEnabled: false }), material);
        mesh.position.z = b.StandOff;
        group.add(mesh);
    }

    if (b.Bottom) {
        // Body on the bottom side: mirror it; it is moved below the board once the board's thickness is known
        group.scale.z *= -1;
        group.userData.bottom = true;
    }
    return group;
}

/** The footprint's size on the board plane. */
function extentOf(box) {
    const size = new THREE.Vector3();
    box.getSize(size);
    return Math.max(size.x, size.y);
}

function boardThickness(box) {
    return Math.max(extentOf(box) * BOARD_THICKNESS_SHARE, 0.02);
}

function createBoard(box, thickness) {
    const size = new THREE.Vector3();
    box.getSize(size);
    const margin = extentOf(box) * BOARD_MARGIN_SHARE;
    const geometry = new THREE.BoxGeometry(size.x + margin * 2, size.y + margin * 2, thickness);
    const board = new THREE.Mesh(geometry, boardMaterial);
    const center = new THREE.Vector3();
    box.getCenter(center);
    board.position.set(center.x, center.y, boardTop() - thickness / 2);
    return board;
}

async function show(footprint) {
    lastFootprint = footprint;
    const id = ++requestId;
    setStatus('Loading 3D…');

    try {
        const group = new THREE.Group();
        if (options.showPads)
            footprint.Pads.forEach(p => group.add(createPad(p)));
        for (const b of footprint.Bodies) {
            // Let the page paint the status before the long tessellation
            await new Promise(r => setTimeout(r, 0));
            if (id !== requestId) return;
            group.add(await createBody(b));
        }
        if (id !== requestId) return;

        const box = new THREE.Box3().setFromObject(group);
        if (box.isEmpty()) {
            setContent(null);
            setStatus('Nothing to show');
            return;
        }
        const thickness = boardThickness(box);
        for (const child of group.children)
            if (child.userData.bottom) child.position.z += boardTop() - thickness;
        if (options.showBoard)
            group.add(createBoard(box, thickness));

        setContent(group);
        fit();
        setStatus(footprint.Bodies.length ? '' : 'This footprint has no 3D model');
    } catch (e) {
        if (id !== requestId) return;
        setContent(null);
        setStatus('3D error: ' + e.message);
    }
}

function setContent(group) {
    if (content) {
        scene.remove(content);
        content.traverse(o => {
            if (!o.isMesh) return;
            // STEP geometries live in the cache; dispose only what belongs to this scene
            for (const material of [o.material].flat())
                if (material !== padMaterial && material !== boardMaterial) material.dispose();
            if (o.geometry.type !== 'BufferGeometry') o.geometry.dispose();
        });
    }
    content = group;
    if (group) {
        scene.add(group);
        new THREE.Box3().setFromObject(group).getBoundingSphere(contentBounds);
    }
    requestRender();
}

function setStatus(text) {
    statusEl.textContent = text;
    statusEl.style.display = text ? 'flex' : 'none';
}

function fit() {
    if (!content) return;
    const box = new THREE.Box3().setFromObject(content);
    const center = box.getCenter(new THREE.Vector3());
    const radius = box.getBoundingSphere(new THREE.Sphere()).radius || 1;
    const distance = radius / Math.sin(deg(camera.fov) / 2) * 1.05;
    const direction = new THREE.Vector3(0.9, -1.3, 1.1).normalize();
    camera.position.copy(center).addScaledVector(direction, distance);
    controls.target.copy(center);
    controls.update();
    requestRender();
}

function resize() {
    // The pixel ratio changes when the window moves to a monitor with different scaling
    if (renderer.getPixelRatio() !== window.devicePixelRatio)
        renderer.setPixelRatio(window.devicePixelRatio);
    const w = canvas.clientWidth, h = canvas.clientHeight;
    if (canvas.width !== Math.floor(w * renderer.getPixelRatio()) || canvas.height !== Math.floor(h * renderer.getPixelRatio())) {
        renderer.setSize(w, h, false);
        camera.aspect = w / Math.max(h, 1);
        camera.updateProjectionMatrix();
    }
}

// Depth precision depends on the far/near ratio, so the clipping planes are fitted tightly around the model
// for the current camera distance instead of a fixed wide range. This removes most of the flicker inside STEP models.
function updateClipping() {
    const r = Math.max(contentBounds.radius, 0.1);
    const d = camera.position.distanceTo(content ? contentBounds.center : controls.target);
    camera.near = Math.max(d - r * 1.5, r / 200);
    camera.far = d + r * 1.5;
    camera.updateProjectionMatrix();
}

// Render on demand: nothing is drawn while the view is idle, so the page uses no CPU/GPU between interactions
let renderRequested = false;

function requestRender() {
    if (renderRequested) return;
    renderRequested = true;
    requestAnimationFrame(renderFrame);
}

function renderFrame() {
    renderRequested = false;
    resize();
    // With damping, update() keeps firing 'change' until the motion settles, which schedules the next frame
    controls.update();
    updateClipping();
    renderer.render(scene, camera);
}

new ResizeObserver(requestRender).observe(canvas);
window.addEventListener('resize', requestRender);

canvas.addEventListener('dblclick', fit);

// For debugging the page in a regular browser (without WebView2)
window.viewer = { show, fit, camera, controls };

// --- Messages from WPF ---

window.chrome?.webview?.addEventListener('message', e => {
    const msg = e.data;
    if (msg.type === 'show') {
        show(msg.footprint);
    } else if (msg.type === 'clear') {
        requestId++;
        lastFootprint = null;
        setContent(null);
        setStatus(msg.text || '');
    } else if (msg.type === 'theme') {
        applyTheme(msg.dark);
    } else if (msg.type === 'options') {
        applyOptions(msg);
    }
});

/** Board / pads visibility and STEP detail; a different detail re-tessellates the models. */
function applyOptions(msg) {
    const detailChanged = msg.linearDeflection !== options.linearDeflection;
    options.showBoard = msg.showBoard;
    options.showPads = msg.showPads;
    options.linearDeflection = msg.linearDeflection;

    if (detailChanged) {
        stepCache.forEach(meshes => meshes.forEach(m => m.geometry.dispose()));
        stepCache.clear();
    }
    if (lastFootprint)
        show(lastFootprint);
}

/** Matches the app's light / dark theme (the dark one is a soft gray, not black). */
function applyTheme(dark) {
    scene.background.set(dark ? 0x252629 : 0xf4f5f7);
    document.body.classList.toggle('dark', dark);
    requestRender();
}

occtReady.then(
    () => { setStatus(''); window.chrome?.webview?.postMessage('ready'); },
    e => setStatus('Failed to load OpenCascade: ' + e));
