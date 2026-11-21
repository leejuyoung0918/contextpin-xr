"""Canonical millimetre mesh and its two verified file representations."""

from collections import Counter, defaultdict
from dataclasses import dataclass
import json
import math
import struct


class GeometryError(ValueError):
    pass


Point = tuple[float, float, float]
Triangle = tuple[int, int, int]


@dataclass
class MeshGeometry:
    """Right-handed Y-up millimetres, grouped by stable semantic part ID."""

    vertices: list[Point]
    parts: dict[str, list[Triangle]]

    def triangles(self):
        for faces in self.parts.values():
            yield from faces


def subtract(a, b):
    return tuple(x - y for x, y in zip(a, b))


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def triangle_normal(a, b, c):
    vector = cross(subtract(b, a), subtract(c, a))
    length = math.hypot(*vector)
    if length <= 1e-10:
        raise GeometryError("생성된 모델에 면적이 0인 면이 있습니다.")
    return tuple(value / length for value in vector)


def bounds(points):
    axes = tuple(zip(*points))
    low = [min(axis) for axis in axes]
    high = [max(axis) for axis in axes]
    return {"min": low, "max": high, "size": list(subtract(high, low))}


def inspect_mesh(mesh):
    faces = list(mesh.triangles())
    if not faces or not mesh.vertices or not all(math.isfinite(v) for p in mesh.vertices for v in p):
        raise GeometryError("생성된 모델이 비어 있거나 치수가 유효하지 않습니다.")
    edge_uses = defaultdict(list)
    orientation = Counter()
    used_vertices = set()
    volume = 0.0
    for face_id, face in enumerate(faces):
        if any(i < 0 or i >= len(mesh.vertices) for i in face):
            raise GeometryError("면이 잘못된 꼭짓점을 참조합니다.")
        a, b, c = (mesh.vertices[i] for i in face)
        used_vertices.update(face)
        triangle_normal(a, b, c)
        volume += sum(x * y for x, y in zip(a, cross(b, c))) / 6
        for start, end in zip(face, (*face[1:], face[0])):
            edge = tuple(sorted((start, end)))
            edge_uses[edge].append(face_id)
            orientation[edge] += 1 if start < end else -1
    if any(len(uses) != 2 for uses in edge_uses.values()) or any(orientation.values()):
        raise GeometryError("모델 표면이 닫혀 있지 않거나 바깥쪽 방향이 일관되지 않습니다.")
    if len(used_vertices) != len(mesh.vertices):
        raise GeometryError("삼각형에 연결되지 않은 꼭짓점이 포함되어 있습니다.")
    adjacency = defaultdict(list)
    for first, second in edge_uses.values():
        adjacency[first].append(second)
        adjacency[second].append(first)
    remaining = set(range(len(faces)))
    components = 0
    while remaining:
        components += 1
        stack = [remaining.pop()]
        while stack:
            for neighbor in adjacency[stack.pop()]:
                if neighbor in remaining:
                    remaining.remove(neighbor)
                    stack.append(neighbor)
    if components != 1 or volume <= 0:
        raise GeometryError("모델은 양의 부피를 가진 하나의 연결된 형상이어야 합니다.")
    return {
        "geometry_valid": True,
        "solid_count": components,
        "volume_mm3": volume,
        "triangle_count": len(faces),
        "bounds_mm": bounds(mesh.vertices),
        "mesh_checks_performed": ["finite_coordinates", "positive_triangle_area", "closed_edge_manifold",
                                  "oriented_surface", "connected_surface", "positive_signed_volume", "referenced_vertices"],
    }


def to_stl(point):
    """A proper rotation from design Y-up to build-plate Z-up."""
    x, y, z = point
    return x, -z, y


def export_stl(mesh):
    faces = list(mesh.triangles())
    output = bytearray(struct.pack("<80sI", b"ContextPin - millimetres, Z up", len(faces)))
    for face in faces:
        vertices = [to_stl(mesh.vertices[i]) for i in face]
        output += struct.pack("<12fH", *triangle_normal(*vertices), *(v for p in vertices for v in p), 0)
    return bytes(output)


def export_glb(mesh):
    binary = bytearray()
    document = {
        "asset": {"version": "2.0", "generator": "ContextPin parametric mesh"},
        "scene": 0,
        "scenes": [{"nodes": []}],
        "nodes": [], "meshes": [], "materials": [], "accessors": [], "bufferViews": [],
    }

    def accessor(data, count, component_type, value_type, target, limits=None):
        view_id = len(document["bufferViews"])
        document["bufferViews"].append({"buffer": 0, "byteOffset": len(binary), "byteLength": len(data), "target": target})
        binary.extend(data)
        value = {"bufferView": view_id, "componentType": component_type, "count": count, "type": value_type}
        if limits:
            value.update(min=limits["min"], max=limits["max"])
        document["accessors"].append(value)
        return len(document["accessors"]) - 1

    palette = [(0.12, 0.43, 0.64, 1), (0.18, 0.64, 0.66, 1), (0.34, 0.76, 0.65, 1)]
    part_map = []
    for index, (part_id, faces) in enumerate(mesh.parts.items()):
        vertices_m, normals, indices = [], [], []
        # Weld smooth faces in preview while separating caps from side normals.
        vertex_indices = {}
        for face in faces:
            normals_face = triangle_normal(*(mesh.vertices[i] for i in face))
            cap = math.isclose(abs(normals_face[1]), 1, abs_tol=1e-8)
            normal_group = int(normals_face[1]) if cap else 0
            for vertex_id in face:
                key = (vertex_id, normal_group)
                if key not in vertex_indices:
                    vertex_indices[key] = len(vertices_m)
                    vertices_m.append(tuple(value / 1000 for value in mesh.vertices[vertex_id]))
                    normals.append([0.0, 0.0, 0.0])
                vertex_index = vertex_indices[key]
                indices.append(vertex_index)
                for axis in range(3):
                    normals[vertex_index][axis] += normals_face[axis]
        normal_values = [tuple(value / math.hypot(*vector) for value in vector) for vector in normals]
        # POSITION accessor bounds are measured from the actual FLOAT representation.
        positions = b"".join(struct.pack("<3f", *point) for point in vertices_m)
        float_positions = list(struct.iter_unpack("<3f", positions))
        position_id = accessor(positions, len(vertices_m), 5126, "VEC3", 34962, bounds(float_positions))
        normal_id = accessor(b"".join(struct.pack("<3f", *n) for n in normal_values), len(normals), 5126, "VEC3", 34962)
        index_id = accessor(struct.pack(f"<{len(indices)}I", *indices), len(indices), 5125, "SCALAR", 34963)
        document["meshes"].append({"name": part_id, "primitives": [{"attributes": {"POSITION": position_id,
            "NORMAL": normal_id}, "indices": index_id, "material": index, "mode": 4}]})
        document["materials"].append({"pbrMetallicRoughness": {"baseColorFactor": palette[index % len(palette)],
            "metallicFactor": 0, "roughnessFactor": 0.72}})
        document["nodes"].append({"name": part_id, "mesh": index, "extras": {"part_id": part_id}})
        document["scenes"][0]["nodes"].append(index)
        part_map.append({"part_id": part_id, "node_name": part_id, "node_index": index})
    document["buffers"] = [{"byteLength": len(binary)}]
    scene = json.dumps(document, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    scene += b" " * (-len(scene) % 4)
    total_size = 12 + 8 + len(scene) + 8 + len(binary)
    return struct.pack("<4sIII4s", b"glTF", 2, total_size, len(scene), b"JSON") + scene + \
        struct.pack("<I4s", len(binary), b"BIN\x00") + binary, {"parts": part_map}


def validate_exports(mesh, glb, stl):
    """Read back both payloads and compare all emitted triangles to the source."""
    count = sum(len(faces) for faces in mesh.parts.values())
    if struct.unpack_from("<I", stl, 80)[0] != count or len(stl) != 84 + 50 * count:
        raise GeometryError("내보낸 STL의 삼각형 개수가 원본과 다릅니다.")
    stl_points = []
    for index, face in enumerate(mesh.triangles()):
        values = struct.unpack_from("<9f", stl, 96 + index * 50)
        actual = tuple(zip(values[::3], values[1::3], values[2::3]))
        expected = [to_stl(mesh.vertices[i]) for i in face]
        _check_triangle(actual, expected)
        stl_points.extend(actual)

    magic, version, length, json_length, chunk_type = struct.unpack_from("<4sIII4s", glb)
    if (magic, version, length, chunk_type) != (b"glTF", 2, len(glb), b"JSON"):
        raise GeometryError("내보낸 GLB 헤더가 잘못되었습니다.")
    document = json.loads(glb[20:20 + json_length])
    binary_start = 20 + json_length
    bin_length, bin_type = struct.unpack_from("<I4s", glb, binary_start)
    if (bin_type, bin_length) != (b"BIN\x00", len(glb) - binary_start - 8):
        raise GeometryError("내보낸 GLB의 이진 데이터가 잘못되었습니다.")

    def read_accessor(index, fmt):
        access = document["accessors"][index]
        view = document["bufferViews"][access["bufferView"]]
        start = binary_start + 8 + view["byteOffset"]
        data = list(struct.iter_unpack(fmt, glb[start:start + view["byteLength"]]))
        if len(data) != access["count"]:
            raise GeometryError("내보낸 GLB 데이터의 개수가 원본과 다릅니다.")
        return data

    glb_points = []
    if len(document["nodes"]) != len(mesh.parts):
        raise GeometryError("내보낸 GLB의 의미적 부위 개수가 원본과 다릅니다.")
    for part_id, node in zip(mesh.parts, document["nodes"]):
        if node["extras"]["part_id"] != part_id:
            raise GeometryError("GLB 부위 ID가 원본과 다릅니다.")
        primitive, = document["meshes"][node["mesh"]]["primitives"]
        positions = read_accessor(primitive["attributes"]["POSITION"], "<3f")
        indices = [i for (i,) in read_accessor(primitive["indices"], "<I")]
        if len(indices) != 3 * len(mesh.parts[part_id]) or any(i >= len(positions) for i in indices):
            raise GeometryError("GLB 삼각형 데이터가 원본과 다릅니다.")
        for offset, face in enumerate(mesh.parts[part_id]):
            points = [positions[i] for i in indices[3 * offset:3 * offset + 3]]
            _check_triangle([tuple(v * 1000 for v in point) for point in points], [mesh.vertices[i] for i in face])
        glb_points.extend(positions)
    return {"bounds_m": bounds(glb_points), "stl_bounds_mm": bounds(stl_points)}


def _check_triangle(actual, expected):
    if any(not math.isclose(x, y, rel_tol=1e-7, abs_tol=1e-6)
           for a, b in zip(actual, expected) for x, y in zip(a, b)):
        raise GeometryError("미리보기와 출력용 면의 위치가 원본 삼각형과 일치하지 않습니다.")
