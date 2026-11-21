using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace ContextPin
{
    [Serializable]
    public struct BoardVector
    {
        public float x, y, z;

        public BoardVector(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public BoardVector(Vector3 value) : this(value.X, value.Y, value.Z) { }
        public Vector3 Vector => new Vector3(x, y, z);
        internal static string CoordinateKey(float value) =>
            Math.Round((double)value * 1000000, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        internal string Key => string.Join(",", CoordinateKey(x), CoordinateKey(y), CoordinateKey(z));
    }

    [Serializable]
    public sealed class BoardTerminal
    {
        public string id, address, netId, region, geometrySource, netSource;
        public int row, column;
        public BoardVector boardLocal;

        internal BoardTerminal Copy() => (BoardTerminal)MemberwiseClone();
        internal string Key => string.Join("|", id, address, netId, region, geometrySource, netSource,
            row.ToString(CultureInfo.InvariantCulture), column.ToString(CultureInfo.InvariantCulture), boardLocal.Key);
    }

    [Serializable]
    public sealed class BoardReference
    {
        public string address, purpose;
        public BoardVector boardLocal;

        internal BoardReference Copy() => (BoardReference)MemberwiseClone();
    }

    [Serializable]
    public sealed class BoardComponent
    {
        public string id;
        public string[] pinRoles;

        internal BoardComponent Copy() => new BoardComponent { id = id, pinRoles = (string[])pinRoles.Clone() };
    }

    [Serializable]
    public sealed class BoardMarker
    {
        public int id;
        public BoardVector[] clockwiseCorners;

        internal BoardMarker Copy() => new BoardMarker { id = id, clockwiseCorners = (BoardVector[])clockwiseCorners.Clone() };
    }

    [Serializable]
    public sealed class BoardConnectionTarget
    {
        public string kind, fromNet, toNet;
        public bool polarized;

        internal BoardConnectionTarget Copy() => (BoardConnectionTarget)MemberwiseClone();
    }

    [Serializable]
    public sealed class LabBoardDefinition
    {
        public string id, kind, displayName, units, axes, contentHash;
        public string geometrySource, netSource, placementPolicy, internalConnections;
        public string markerLayoutId, markerDictionary;
        public int revision;
        public float widthMeters, heightMeters;
        public GuidePoint[] points;
        public BoardTerminal[] terminals;
        public BoardReference[] references;
        public BoardMarker[] markers;
        public BoardComponent[] knownComponents;
        public BoardConnectionTarget[] targets;

        public GuidePoint Find(string terminalId) => points?.FirstOrDefault(point => point.id == terminalId || point.code == terminalId);
        public BoardTerminal FindTerminal(string terminalId) => terminals?.FirstOrDefault(terminal =>
            terminal.id == terminalId || terminal.address == terminalId);

        public bool SameNet(string left, string right)
        {
            string net = FindTerminal(left)?.netId;
            return net != null && net == FindTerminal(right)?.netId;
        }

        public string PlacementComparison(string actual, string target)
        {
            var actualTerminal = FindTerminal(actual);
            var targetTerminal = FindTerminal(target);
            if (actualTerminal == null || targetTerminal == null) return "unknown";
            if (actualTerminal.id == targetTerminal.id) return "exact";
            if (actualTerminal.netId != targetTerminal.netId) return "different_net";
            return placementPolicy == "equivalent_net" ? "equivalent" : "different_terminal";
        }

        public bool IsExpectedConnection(string kind, string fromPoint, string toPoint)
        {
            var target = targets?.FirstOrDefault(item => item.kind == kind);
            if (target == null) return false;
            string from = FindTerminal(fromPoint)?.netId;
            string to = FindTerminal(toPoint)?.netId;
            if (from == null || to == null || from == to) return false;
            return (from == target.fromNet && to == target.toNet) ||
                (!target.polarized && from == target.toNet && to == target.fromNet);
        }

        public bool IsValidVersion
        {
            get
            {
                var expected = LabBoards.KnownVersion(id);
                return expected != null && revision == expected.revision &&
                    contentHash == expected.contentHash && contentHash == ComputeHash();
            }
        }

        internal string ComputeHash()
        {
            if (points == null || terminals == null || references == null || markers == null || knownComponents == null || targets == null ||
                points.Any(point => point == null) || terminals.Any(terminal => terminal == null) ||
                references.Any(reference => reference == null) || markers.Any(marker => marker?.clockwiseCorners == null) ||
                knownComponents.Any(component => component?.pinRoles == null) ||
                targets.Any(target => target == null))
                return null;
            var fields = new List<string> { id, kind, displayName, units, axes, geometrySource, netSource, markerLayoutId, markerDictionary,
                placementPolicy, internalConnections, revision.ToString(CultureInfo.InvariantCulture),
                BoardVector.CoordinateKey(widthMeters), BoardVector.CoordinateKey(heightMeters) };
            fields.AddRange(points.Select(point => string.Join("|", point.id, point.label, point.code, point.node,
                BoardVector.CoordinateKey(point.x), BoardVector.CoordinateKey(point.y), point.canonical)));
            fields.AddRange(terminals.Select(terminal => terminal.Key));
            fields.AddRange(references.Select(reference => string.Join("|", reference.address, reference.purpose, reference.boardLocal.Key)));
            fields.AddRange(markers.Select(marker => marker.id.ToString(CultureInfo.InvariantCulture) + "|" +
                string.Join("|", marker.clockwiseCorners.Select(corner => corner.Key))));
            fields.AddRange(knownComponents.Select(component => component.id + "|" + string.Join(",", component.pinRoles)));
            fields.AddRange(targets.Select(target => string.Join("|", target.kind, target.fromNet, target.toNet, target.polarized)));
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", fields)))).Replace("-", "").ToLowerInvariant();
        }

        public LabBoardDefinition Copy()
        {
            var result = (LabBoardDefinition)MemberwiseClone();
            result.points = points?.Select(point => new GuidePoint
            {
                id = point.id, label = point.label, code = point.code, node = point.node,
                x = point.x, y = point.y, canonical = point.canonical
            }).ToArray();
            result.terminals = terminals?.Select(terminal => terminal.Copy()).ToArray();
            result.references = references?.Select(reference => reference.Copy()).ToArray();
            result.markers = markers?.Select(marker => marker.Copy()).ToArray();
            result.knownComponents = knownComponents?.Select(component => component.Copy()).ToArray();
            result.targets = targets?.Select(target => target.Copy()).ToArray();
            return result;
        }
    }

    public static class LabBoards
    {
        public const string PhysicalId = "led-breadboard-v1";
        public const int PhysicalRevision = 1;
        const float Pitch = .00254f;

        static readonly LabBoardDefinition simulation = BuildSimulation();
        static readonly LabBoardDefinition physical = BuildPhysical();

        public static LabBoardDefinition Resolve(string id) => KnownVersion(id)?.Copy();

        internal static LabBoardDefinition KnownVersion(string id)
        {
            if (id == PhysicalId) return physical;
            if (id == GuideBoard.Id) return simulation;
            return null;
        }

        static BoardComponent[] Components() => new[]
        {
            new BoardComponent { id = "resistor", pinRoles = new[] { "lead_1", "lead_2" } },
            new BoardComponent { id = "led", pinRoles = new[] { "anode", "cathode" } },
            new BoardComponent { id = "jumper", pinRoles = new[] { "end_1", "end_2" } },
            new BoardComponent { id = "power", pinRoles = new[] { "positive", "ground" } }
        };

        static BoardConnectionTarget[] Targets(string firstNet, string secondNet, string plusNet, string groundNet) => new[]
        {
            new BoardConnectionTarget { kind = "resistor", fromNet = plusNet, toNet = firstNet },
            new BoardConnectionTarget { kind = "led", fromNet = firstNet, toNet = secondNet, polarized = true },
            new BoardConnectionTarget { kind = "ground", fromNet = secondNet, toNet = groundNet },
        };

        static LabBoardDefinition BuildSimulation()
        {
            var definition = new LabBoardDefinition
            {
                id = GuideBoard.Id, revision = 1, kind = "simulation", displayName = "LED 실습 가상 보드",
                units = "normalized_2d", axes = "2D panel coordinates", placementPolicy = "equivalent_net",
                geometrySource = "simulation", netSource = "simulation",
                internalConnections = "A, B, C are independent simulation strips; no physical coordinates.",
                points = GuideBoard.Points.ToArray(),
                terminals = GuideBoard.Points.Select(point => new BoardTerminal
                {
                    id = point.id, address = point.code, netId = point.node, region = point.node,
                    geometrySource = "simulation", netSource = "simulation"
                }).ToArray(),
                references = Array.Empty<BoardReference>(),
                markers = Array.Empty<BoardMarker>(),
                knownComponents = Components(), targets = Targets("a", "b", "plus", "gnd"),
            };
            definition.contentHash = definition.ComputeHash();
            return definition.Copy();
        }

        static BoardVector Hole(int column, int row)
        {
            // E to F spans three pitches across the central trench. Logical rows
            // follow the printed numbers, beginning at the near edge of the board.
            int x = column < 5 ? column : column + 2;
            return new BoardVector(x * Pitch, 0, (row - 1) * Pitch);
        }

        static LabBoardDefinition BuildPhysical()
        {
            const string geometry = "nominal_teaching_fixture_verify_pitch_and_rail_positions";
            const string nets = "teaching_fixture_instructor_verify_unpowered_continuity";
            var points = new[]
            {
                Point("plus-1", "+1", "P+", "positive-near", .12f, .31f),
                Point("plus-2", "+2", "P+", "positive-near", .12f, .60f),
                Point("a-1", "A1", "1번 스트립", "left-01", .39f, .31f),
                Point("a-2", "C1", "1번 스트립", "left-01", .60f, .31f),
                Point("b-1", "A6", "6번 스트립", "left-06", .39f, .54f),
                Point("b-2", "C6", "6번 스트립", "left-06", .60f, .54f),
                Point("c-1", "A11", "11번 스트립", "left-11", .39f, .77f),
                Point("c-2", "C11", "11번 스트립", "left-11", .60f, .77f),
                Point("gnd-1", "−1", "P−", "ground-near", .87f, .31f),
                Point("gnd-2", "−2", "P−", "ground-near", .87f, .60f)
            };
            var aliases = points.ToDictionary(point => point.code, point => point.id, StringComparer.Ordinal);
            var terminals = new List<BoardTerminal>();
            for (int row = 1; row <= 30; row++)
            {
                for (int column = 0; column < 10; column++)
                {
                    string address = ((char)('A' + column)).ToString() + row.ToString(CultureInfo.InvariantCulture);
                    string side = column < 5 ? "left" : "right";
                    terminals.Add(new BoardTerminal
                    {
                        id = aliases.TryGetValue(address, out string alias) ? alias : address,
                        address = address, row = row, column = column, region = side + "-strip",
                        netId = side + "-" + row.ToString("00", CultureInfo.InvariantCulture),
                        boardLocal = Hole(column, row), geometrySource = geometry, netSource = nets,
                    });
                }
            }
            foreach (var point in points.Where(point => point.node.EndsWith("-near", StringComparison.Ordinal)))
            {
                bool positive = point.id.StartsWith("plus-", StringComparison.Ordinal);
                int column = positive ? -3 : 14;
                int index = point.canonical ? 0 : 1;
                terminals.Add(new BoardTerminal
                {
                    id = point.id, address = point.code, netId = point.node, region = point.node,
                    row = index + 1, column = column,
                    boardLocal = new BoardVector(column * Pitch, 0, index * Pitch),
                    geometrySource = "nominal_near_rail_fixed_by_instructor", netSource = nets,
                });
            }
            var definition = new LabBoardDefinition
            {
                id = PhysicalId, revision = PhysicalRevision, kind = "real_breadboard",
                displayName = "30줄 실물 LED 실습 보드",
                units = "meters", axes = "A1 origin on contact surface; x toward J1; y upward; z toward A30",
                widthMeters = 17 * Pitch, heightMeters = 29 * Pitch,
                geometrySource = geometry, netSource = nets, placementPolicy = "equivalent_net",
                internalConnections = "A–E share each numbered row. F–J share a separate net per row. " +
                    "Only designated near-end pair per power rail is used. The middle/distal rail has no modeled continuity.",
                points = points, terminals = terminals.ToArray(),
                references = new[]
                {
                    Reference("A1", 0, 1, "origin"), Reference("J1", 9, 1, "x"),
                    Reference("A30", 0, 30, "z"), Reference("J30", 9, 30, "corner_check"),
                    Reference("F15", 5, 15, "middle_check"),
                },
                knownComponents = Components(), targets = Targets("left-01", "left-06", "positive-near", "ground-near"),
                markerLayoutId = "led-board-26mm-v1", markerDictionary = "DICT_4X4_50",
                markers = new[] { Marker(11, -.047f, .012f), Marker(29, .050f, .012f) },
            };
            definition.contentHash = definition.ComputeHash();
            return definition;
        }

        static BoardReference Reference(string address, int column, int row, string purpose) =>
            new BoardReference { address = address, purpose = purpose, boardLocal = Hole(column, row) };

        static GuidePoint Point(string id, string address, string region, string node, float x, float y) =>
            new GuidePoint { id = id, code = address, label = region, node = node, x = x, y = y, canonical = id.EndsWith("-1", StringComparison.Ordinal) };

        static BoardMarker Marker(int id, float x, float z)
        {
            const float side = .026f;
            return new BoardMarker { id = id, clockwiseCorners = new[]
            {
                new BoardVector(x, 0, z + side), new BoardVector(x + side, 0, z + side),
                new BoardVector(x + side, 0, z), new BoardVector(x, 0, z),
            } };
        }
    }
}
