// Preview contract: self-contained glTF 2.0 GLB with indexed, uncompressed triangle meshes.
// Node extras.part_id links a mesh to the profile's persistent semantic part ID.
const vertexCode = `#version 300 es
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
uniform mat4 uCamera;
uniform mat4 uModel;
out vec3 normal;
void main() {
  gl_Position = uCamera * uModel * vec4(aPosition, 1.0);
  normal = transpose(inverse(mat3(uModel))) * aNormal;
}`;

const fragmentCode = `#version 300 es
precision highp float;
in vec3 normal;
uniform vec3 uColor;
uniform bool uPicking;
out vec4 pixel;
void main() {
  if (uPicking) {
    pixel = vec4(uColor, 1.0);
    return;
  }
  vec3 n = normalize(normal);
  float key = max(dot(n, normalize(vec3(-0.3, 1.0, 0.65))), 0.0);
  float fill = max(dot(n, normalize(vec3(0.4, 0.25, -1.0))), 0.0);
  vec3 linearColor = uColor * (0.42 + 0.64 * key + 0.18 * fill);
  pixel = vec4(pow(max(linearColor, 0.0), vec3(1.0 / 2.2)), 1.0);
}`;

const identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
const jsonChunk = 0x4e4f534a;
const binaryChunk = 0x004e4942;
const floatComponent = 5126;

function multiply(left, right) {
  const result = new Float32Array(16);
  for (let column = 0; column < 4; column++) {
    for (let row = 0; row < 4; row++) {
      for (let i = 0; i < 4; i++) {
        result[column * 4 + row] += left[i * 4 + row] * right[column * 4 + i];
      }
    }
  }
  return result;
}

function transformNode(node) {
  if (node.matrix) return node.matrix;
  const [x, y, z, w] = node.rotation || [0, 0, 0, 1];
  const [sx, sy, sz] = node.scale || [1, 1, 1];
  const [tx, ty, tz] = node.translation || [0, 0, 0];
  return [
    (1 - 2 * (y * y + z * z)) * sx, 2 * (x * y + w * z) * sx, 2 * (x * z - w * y) * sx, 0,
    2 * (x * y - w * z) * sy, (1 - 2 * (x * x + z * z)) * sy, 2 * (y * z + w * x) * sy, 0,
    2 * (x * z + w * y) * sz, 2 * (y * z - w * x) * sz, (1 - 2 * (x * x + y * y)) * sz, 0,
    tx, ty, tz, 1,
  ];
}

function cross(a, b) {
  return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
}

function dot(a, b) {
  return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
}

function unit(vector) {
  const magnitude = Math.hypot(...vector);
  return vector.map(value => value / magnitude);
}

function cameraMatrix(center, radius, yaw, pitch, zoom, aspect) {
  const halfY = radius * 1.16 * Math.max(1, 1 / aspect) / zoom;
  const halfX = halfY * aspect;
  const far = radius * 8;
  const projection = [1 / halfX, 0, 0, 0, 0, 1 / halfY, 0, 0, 0, 0, -2 / far, 0, 0, 0, -1, 1];
  const forward = [Math.cos(pitch) * Math.sin(yaw), Math.sin(pitch), Math.cos(pitch) * Math.cos(yaw)];
  const eye = center.map((component, index) => component + forward[index] * radius * 3);
  const right = unit(cross([0, 1, 0], forward));
  const up = cross(forward, right);
  const view = [
    right[0], up[0], forward[0], 0, right[1], up[1], forward[1], 0, right[2], up[2], forward[2], 0,
    -dot(right, eye), -dot(up, eye), -dot(forward, eye), 1,
  ];
  return multiply(projection, view);
}

function program(gl) {
  function compile(type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      gl.deleteShader(shader);
      throw new Error("3D 렌더러의 셰이더를 실행할 수 없습니다.");
    }
    return shader;
  }
  const vertex = compile(gl.VERTEX_SHADER, vertexCode);
  const fragment = compile(gl.FRAGMENT_SHADER, fragmentCode);
  const shaderProgram = gl.createProgram();
  gl.attachShader(shaderProgram, vertex);
  gl.attachShader(shaderProgram, fragment);
  gl.linkProgram(shaderProgram);
  gl.deleteShader(vertex);
  gl.deleteShader(fragment);
  if (!gl.getProgramParameter(shaderProgram, gl.LINK_STATUS)) {
    gl.deleteProgram(shaderProgram);
    throw new Error("3D 렌더러를 실행할 수 없습니다.");
  }
  return shaderProgram;
}

function readGlb(buffer) {
  if (buffer.byteLength < 28) throw new Error("모델 파일이 비어 있거나 손상됐습니다.");
  const header = new DataView(buffer);
  if (header.getUint32(0, true) !== 0x46546c67 || header.getUint32(4, true) !== 2 ||
      header.getUint32(8, true) !== buffer.byteLength) {
    throw new Error("지원되는 GLB 2.0 파일 형식이 아닙니다.");
  }
  let spec;
  let binary;
  for (let offset = 12; offset + 8 <= buffer.byteLength;) {
    const size = header.getUint32(offset, true);
    const type = header.getUint32(offset + 4, true);
    if (offset + 8 + size > buffer.byteLength) throw new Error("모델 파일이 손상됐습니다.");
    const bytes = new Uint8Array(buffer, offset + 8, size);
    if (type === jsonChunk) spec = JSON.parse(new TextDecoder().decode(bytes));
    if (type === binaryChunk) binary = bytes;
    offset += 8 + size;
  }
  if (!spec?.scenes?.length || !binary || spec.extensionsRequired?.length) {
    throw new Error("이 모델은 현재 뷰어에서 지원하는 메시 형식과 다릅니다.");
  }
  return { spec, binary };
}

function accessor(spec, index, binaryLength) {
  const item = spec.accessors?.[index];
  const view = spec.bufferViews?.[item?.bufferView];
  if (!view || view.buffer !== 0 || item.sparse || item.normalized) {
    throw new Error("이 모델에서 지원되는 메시 속성을 찾지 못했습니다.");
  }
  const offset = (view.byteOffset || 0) + (item.byteOffset || 0);
  const components = item.type === "VEC3" ? 3 : item.type === "SCALAR" ? 1 : 0;
  const componentBytes = { 5121: 1, 5123: 2, 5125: 4, 5126: 4 }[item.componentType];
  const width = components * componentBytes;
  const stride = view.byteStride || 0;
  if (!width || !Number.isInteger(item.count) || item.count < 1 || offset < 0 ||
      offset + (item.count - 1) * (stride || width) + width > binaryLength) {
    throw new Error("모델 메시 크기가 올바르지 않습니다.");
  }
  return { ...item, offset, stride };
}

function meshList(spec, binaryLength) {
  const meshes = [];
  const min = [Infinity, Infinity, Infinity];
  const max = [-Infinity, -Infinity, -Infinity];
  const visit = (index, parent, inheritedPart, depth = 0) => {
    const node = spec.nodes?.[index];
    if (!node || depth > 80) throw new Error("모델 장면 구조를 불러올 수 없습니다.");
    const transform = multiply(parent, transformNode(node));
    const partId = node.extras?.part_id || inheritedPart;
    for (const primitive of spec.meshes?.[node.mesh]?.primitives || []) {
      if ((primitive.mode ?? 4) !== 4) throw new Error("삼각형 메시만 표시할 수 있습니다.");
      const position = accessor(spec, primitive.attributes?.POSITION, binaryLength);
      const normal = accessor(spec, primitive.attributes?.NORMAL, binaryLength);
      const indices = accessor(spec, primitive.indices, binaryLength);
      if (position.type !== "VEC3" || position.componentType !== floatComponent ||
          normal.type !== "VEC3" || normal.componentType !== floatComponent || normal.count !== position.count ||
          indices.type !== "SCALAR" || indices.componentType === floatComponent || indices.stride ||
          !position.min?.every(Number.isFinite) || !position.max?.every(Number.isFinite)) {
        throw new Error("모델의 좌표, 법선 또는 삼각형 인덱스 형식을 읽을 수 없습니다.");
      }
      for (let mask = 0; mask < 8; mask++) {
        const [x, y, z] = min.map((_, axis) => (mask & (1 << axis) ? position.max : position.min)[axis]);
        for (let axis = 0; axis < 3; axis++) {
          const value = transform[axis] * x + transform[axis + 4] * y + transform[axis + 8] * z + transform[axis + 12];
          min[axis] = Math.min(min[axis], value);
          max[axis] = Math.max(max[axis], value);
        }
      }
      const material = spec.materials?.[primitive.material]?.pbrMetallicRoughness;
      const color = material?.baseColorFactor?.slice(0, 3) || [.31, .45, .74];
      meshes.push({ position, normal, indices, color, transform, partId });
    }
    for (const child of node.children || []) visit(child, transform, partId, depth + 1);
  };
  for (const node of spec.scenes[spec.scene || 0].nodes || []) visit(node, identity, null);
  if (!meshes.length || !min.every(Number.isFinite) || !max.every(Number.isFinite)) {
    throw new Error("모델에 렌더링할 기하가 없습니다.");
  }
  return { meshes, min, max };
}

export class ModelViewer {
  constructor(canvas, { onPart, onZoom, onLost }) {
    this.canvas = canvas;
    this.gl = canvas.getContext("webgl2", { alpha: true, antialias: true });
    if (!this.gl) throw new Error("WebGL2를 사용할 수 없습니다. 다른 브라우저에서 모델을 열어 주세요.");
    this.program = program(this.gl);
    this.uniforms = Object.fromEntries(["uCamera", "uModel", "uColor", "uPicking"]
      .map(name => [name, this.gl.getUniformLocation(this.program, name)]));
    this.meshes = [];
    this.parts = [];
    this.selection = new Set();
    this.onZoom = onZoom;
    this.reset();
    this.pickTarget = null;
    this.canvas.addEventListener("webglcontextlost", onLost);
    new ResizeObserver(() => this.schedule()).observe(canvas);
    this.bindInput(onPart);
  }

  bindInput(onPart) {
    const canvas = this.canvas;
    let gesture;
    canvas.addEventListener("pointerdown", event => {
      if (event.button !== 0 || gesture) return;
      gesture = { x: event.clientX, y: event.clientY, yaw: this.yaw, pitch: this.pitch, moved: false, id: event.pointerId };
      canvas.setPointerCapture(event.pointerId);
    });
    canvas.addEventListener("pointermove", event => {
      if (gesture?.id !== event.pointerId) return;
      const dx = event.clientX - gesture.x;
      const dy = event.clientY - gesture.y;
      if (Math.hypot(dx, dy) >= 4) gesture.moved = true;
      if (!gesture.moved) return;
      this.yaw = gesture.yaw - dx * .009;
      this.pitch = Math.min(1.53, Math.max(-1.53, gesture.pitch + dy * .009));
      this.schedule();
    });
    canvas.addEventListener("pointerup", event => {
      if (gesture?.id !== event.pointerId) return;
      if (!gesture.moved) onPart(this.partAt(event.clientX, event.clientY));
      gesture = null;
    });
    canvas.addEventListener("lostpointercapture", () => { gesture = null; });
    canvas.addEventListener("wheel", event => {
      event.preventDefault();
      this.zoomBy(Math.exp(-event.deltaY * .002));
    }, { passive: false });
    canvas.addEventListener("keydown", event => {
      switch (event.key) {
        case "ArrowLeft": this.yaw -= .15; break;
        case "ArrowRight": this.yaw += .15; break;
        case "ArrowUp": this.pitch = Math.min(1.53, this.pitch + .15); break;
        case "ArrowDown": this.pitch = Math.max(-1.53, this.pitch - .15); break;
        case "+": case "=": this.zoomBy(1.2); break;
        case "-": this.zoomBy(1 / 1.2); break;
        case "Home": this.reset(); break;
        default: return;
      }
      event.preventDefault();
      this.schedule();
    });
  }

  reset() {
    this.yaw = .65;
    this.pitch = .43;
    this.zoom = 1;
    this.onZoom(1);
    this.schedule();
  }

  zoomBy(factor) {
    this.zoom = Math.min(5, Math.max(.45, this.zoom * factor));
    this.onZoom(this.zoom);
    this.schedule();
  }

  load(buffer, referenceRadius = 0) {
    const { spec, binary } = readGlb(buffer);
    const { meshes, min, max } = meshList(spec, binary.byteLength);
    this.clear();
    const gl = this.gl;
    this.vertexBuffer = gl.createBuffer();
    this.indexBuffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, this.vertexBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, binary, gl.STATIC_DRAW);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.indexBuffer);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, binary, gl.STATIC_DRAW);
    const parts = new Map();
    for (const mesh of meshes) {
      const vao = gl.createVertexArray();
      gl.bindVertexArray(vao);
      for (const [location, item] of [[0, mesh.position], [1, mesh.normal]]) {
        gl.enableVertexAttribArray(location);
        gl.vertexAttribPointer(location, 3, gl.FLOAT, false, item.stride, item.offset);
      }
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.indexBuffer);
      if (mesh.partId && !parts.has(mesh.partId)) {
        parts.set(mesh.partId, this.parts.push(mesh.partId));
      }
      this.meshes.push({ ...mesh, vao, pickId: parts.get(mesh.partId) || 0 });
    }
    gl.bindVertexArray(null);
    this.center = min.map((value, index) => (value + max[index]) / 2);
    this.radius = Math.max(.0001, referenceRadius || Math.hypot(...max.map((value, index) => value - min[index])) / 2);
    this.schedule();
  }

  clear() {
    const gl = this.gl;
    for (const mesh of this.meshes) gl.deleteVertexArray(mesh.vao);
    if (this.vertexBuffer) gl.deleteBuffer(this.vertexBuffer);
    if (this.indexBuffer) gl.deleteBuffer(this.indexBuffer);
    this.meshes = [];
    this.parts = [];
    this.vertexBuffer = null;
    this.indexBuffer = null;
    this.schedule();
  }

  highlight(partIds) {
    this.selection = new Set(partIds);
    this.schedule();
  }

  schedule() {
    if (this.animation) return;
    this.animation = requestAnimationFrame(() => {
      this.animation = null;
      this.draw();
    });
  }

  dimensions() {
    const density = Math.min(devicePixelRatio || 1, 2);
    return [Math.max(1, Math.round(this.canvas.clientWidth * density)),
      Math.max(1, Math.round(this.canvas.clientHeight * density))];
  }

  draw(target = null) {
    const gl = this.gl;
    const [width, height] = target ? [target.width, target.height] : this.dimensions();
    if (!target) {
      if (this.canvas.width !== width) this.canvas.width = width;
      if (this.canvas.height !== height) this.canvas.height = height;
    }
    gl.bindFramebuffer(gl.FRAMEBUFFER, target?.framebuffer || null);
    gl.viewport(0, 0, width, height);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    if (!this.meshes.length) return;
    gl.enable(gl.DEPTH_TEST);
    gl.disable(gl.DITHER);
    gl.useProgram(this.program);
    gl.uniformMatrix4fv(this.uniforms.uCamera, false, cameraMatrix(this.center, this.radius, this.yaw, this.pitch, this.zoom, width / height));
    gl.uniform1i(this.uniforms.uPicking, target ? 1 : 0);
    for (const mesh of this.meshes) {
      const color = target
        ? [(mesh.pickId & 255) / 255, ((mesh.pickId >> 8) & 255) / 255, 0]
        : this.selection.has(mesh.partId) ? [1, .62, .16] : mesh.color;
      gl.uniformMatrix4fv(this.uniforms.uModel, false, mesh.transform);
      gl.uniform3fv(this.uniforms.uColor, color);
      gl.bindVertexArray(mesh.vao);
      gl.drawElements(gl.TRIANGLES, mesh.indices.count, mesh.indices.componentType, mesh.indices.offset);
    }
    gl.bindVertexArray(null);
  }

  ensurePickTarget() {
    const gl = this.gl;
    const [width, height] = this.dimensions();
    if (this.pickTarget?.width === width && this.pickTarget.height === height) return this.pickTarget;
    if (this.pickTarget) {
      gl.deleteTexture(this.pickTarget.texture);
      gl.deleteRenderbuffer(this.pickTarget.depth);
      gl.deleteFramebuffer(this.pickTarget.framebuffer);
    }
    const framebuffer = gl.createFramebuffer();
    const texture = gl.createTexture();
    const depth = gl.createRenderbuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texStorage2D(gl.TEXTURE_2D, 1, gl.RGBA8, width, height);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
    gl.bindRenderbuffer(gl.RENDERBUFFER, depth);
    gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT16, width, height);
    gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth);
    return (this.pickTarget = { framebuffer, texture, depth, width, height });
  }

  partAt(clientX, clientY) {
    if (!this.meshes.length) return null;
    const rect = this.canvas.getBoundingClientRect();
    const target = this.ensurePickTarget();
    const x = Math.floor(target.width * (clientX - rect.left) / rect.width);
    const y = target.height - 1 - Math.floor(target.height * (clientY - rect.top) / rect.height);
    if (x < 0 || x >= target.width || y < 0 || y >= target.height) return null;
    this.draw(target);
    const pixel = new Uint8Array(4);
    this.gl.readPixels(x, y, 1, 1, this.gl.RGBA, this.gl.UNSIGNED_BYTE, pixel);
    this.gl.bindFramebuffer(this.gl.FRAMEBUFFER, null);
    return this.parts[pixel[0] + 256 * pixel[1] - 1] || null;
  }
}
