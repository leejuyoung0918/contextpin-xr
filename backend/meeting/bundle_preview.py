"""A small orthographic view of the exact base STL for a multimodal edit request."""
import math
import struct
import zlib

from .mesh_geometry import triangle_normal
from .openscad_geometry import _stl_faces


def render_base(stl, hit_scad_mm):
    # Camera is above the front-right corner of the model, in SCAD's Z-up frame.
    right = (.894427, .447214, 0.)
    up = (-.218218, .436436, .872872)
    eye = (.390360, -.780720, .487950)
    def project(point):
        return tuple(sum(a*b for a, b in zip(point, axis)) for axis in (right, up, eye))

    faces = _stl_faces(stl)
    vertices = {p: project(p) for face in faces for p in face}
    xmin, ymin = (min(v[i] for v in vertices.values()) for i in (0, 1))
    xmax, ymax = (max(v[i] for v in vertices.values()) for i in (0, 1))
    width, height = 384, 256
    scale = min((width - 24) / max(1e-9, xmax - xmin), (height - 24) / max(1e-9, ymax - ymin))

    def pixel(v):
        return (width/2 + (v[0] - (xmin+xmax)/2)*scale, height/2 - (v[1] - (ymin+ymax)/2)*scale)

    rows = [bytearray((245, 247, 249) * width) for _ in range(height)]
    for face in sorted(faces, key=lambda tri: sum(vertices[v][2] for v in tri)):
        normal = triangle_normal(*face)
        if sum(n*d for n, d in zip(normal, eye)) <= 0:
            continue
        shade = .45 + .55 * max(0., sum(n*d for n, d in zip(normal, (.24, -.42, .87))))
        color = bytes(round(c * shade) for c in (83, 164, 189))
        points = [pixel(vertices[v]) for v in face]
        edges = list(zip(points, points[1:] + points[:1]))
        for y in range(max(0, math.ceil(min(v[1] for v in points) - .5)),
                       min(height, math.ceil(max(v[1] for v in points) - .5))):
            line = y + .5
            hits = [x0 + (line-y0)*(x1-x0)/(y1-y0) for (x0,y0),(x1,y1) in edges if min(y0,y1) <= line < max(y0,y1)]
            if len(hits) == 2:
                left, right_edge = max(0, math.ceil(min(hits)-.5)), min(width, math.ceil(max(hits)-.5))
                if left < right_edge:
                    rows[y][left*3:right_edge*3] = color * (right_edge-left)

    x, y = map(round, pixel(project(hit_scad_mm)))
    for j in range(max(0, y-5), min(height, y+6)):
        for i in range(max(0, x-5), min(width, x+6)):
            if 7 <= (i-x)**2 + (j-y)**2 <= 25:
                rows[j][3*i:3*i+3] = b'\xfa\x56\x1a'
    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data))

    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>2I5B', width, height, 8, 2, 0, 0, 0)) +
            chunk(b'IDAT', zlib.compress(b''.join(b'\0' + row for row in rows))) + chunk(b'IEND', b''))
