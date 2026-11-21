"""A deliberately generic solid used to exercise interchangeable model editing."""

import math

from ..geometry import ModelAdapter
from ..mesh_geometry import GeometryError, MeshGeometry, bounds


class RoundedFixtureAdapter(ModelAdapter):
    def build(self, params):
        width = params["width_mm"]
        depth = params["depth_mm"]
        height = params["height_mm"]
        radius = params["corner_radius_mm"]
        waist = params["waist_inset_mm"]
        bevel = params["rim_bevel_mm"]
        if radius + waist >= min(width, depth) / 2:
            raise GeometryError("모서리 반경과 몸체 윤곽 깊이의 합이 현재 단면에 비해 큽니다.")
        if radius - bevel * 0.4 >= min(width, depth) / 2 - bevel:
            raise GeometryError("모서리 반경과 상단 경사가 현재 윗면 폭에 비해 큽니다.")

        vertices = []
        parts = {"base": [], "body": [], "rim": []}
        lower_rim = height - bevel

        def add_ring(y, inset=0, corner_reduction=0):
            offset = len(vertices)
            points = _outline(width / 2 - inset, depth / 2 - inset, max(0.5, radius - corner_reduction), y)
            vertices.extend(points)
            return range(offset, offset + len(points))

        bottom = add_ring(0)
        bottom_center = len(vertices)
        vertices.append((0, 0, 0))
        parts["base"].extend((bottom_center, vertex, bottom[(i + 1) % len(bottom)]) for i, vertex in enumerate(bottom))
        previous = add_ring(4)
        _join(parts["base"], bottom, previous)
        for index in range(1, 17):
            portion = index / 16
            inset = waist * math.sin(math.pi * portion) ** 2
            ring = add_ring(4 + (lower_rim - 4) * portion, inset)
            _join(parts["body"], previous, ring)
            previous = ring
        for index in range(1, 9):
            angle = index * math.pi / 16
            inset = bevel * (1 - math.cos(angle))
            ring = add_ring(lower_rim + bevel * math.sin(angle), inset, corner_reduction=inset * 0.4)
            _join(parts["rim"], previous, ring)
            previous = ring
        top_center = len(vertices)
        vertices.append((0, height, 0))
        parts["rim"].extend((top_center, previous[(i + 1) % len(previous)], vertex) for i, vertex in enumerate(previous))
        return MeshGeometry(vertices, parts)

    def measure(self, geometry):
        width, height, depth = bounds(geometry.vertices)["size"]
        base_points = [geometry.vertices[vertex] for triangle in geometry.parts["base"] for vertex in triangle]
        return {"width_mm": width, "depth_mm": depth, "height_mm": height, "base_width_mm": bounds(base_points)["size"][0]}


def _outline(half_width, half_depth, radius, y):
    points = []
    corners = [(1, 1), (-1, 1), (-1, -1), (1, -1)]
    for corner, (sign_x, sign_z) in enumerate(corners):
        center_x = sign_x * (half_width - radius)
        center_z = sign_z * (half_depth - radius)
        for segment in range(9):
            angle = (corner + segment / 8) * math.pi / 2
            points.append((center_x + radius * math.cos(angle), y, center_z + radius * math.sin(angle)))
    return points


def _join(faces, lower, upper):
    for index in range(len(lower)):
        following = (index + 1) % len(lower)
        faces.extend(((lower[index], upper[index], upper[following]), (lower[index], upper[following], lower[following])))
