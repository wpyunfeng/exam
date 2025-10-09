using DTcms.Core.Common.Emums;
using DTcms.Core.Common.Extensions;
using Google.OrTools.Sat;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DTcms.Core.Common.Helpers
{
    /// <summary>
    /// 自动安排班级考场
    /// </summary>
    public static class AIExamClassRoomHelper
    {

        /// <summary>
        /// 自动安排班级考场
        /// </summary>
        public static List<AIExamClassRoomResult> AutoAssignRooms(AIExamClassRoomModel model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            var classes = model.ModelClassList?.ToList() ?? throw new ArgumentException("ModelClassList is required", nameof(model));
            var rooms = model.ModelRoomList?.ToList() ?? throw new ArgumentException("ModelRoomList is required", nameof(model));

            if (!classes.Any())
            {
                return new List<AIExamClassRoomResult>();
            }

            if (!rooms.Any())
            {
                throw new InvalidOperationException("No rooms available for allocation.");
            }

            // Maintain deterministic ordering based on provided list order for classes.
            var orderedClasses = classes.ToList();

            // Group rooms by building and order by room number (fallback to room id if missing).
            var roomsByBuilding = rooms
                .GroupBy(r => r.BuildingId)
                .ToDictionary(g => g.Key, g => g
                    .OrderBy(r => r.RoomNo ?? r.ModelRoomId)
                    .ThenBy(r => r.ModelRoomId)
                    .ToList());

            // Prepare mapping from room id to room metadata for quick access.
            var roomById = rooms.ToDictionary(r => r.ModelRoomId);

            // Build contiguous segment options for each class.
            var classSegments = new List<List<SegmentOption>>();

            for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
            {
                var examClass = orderedClasses[classIndex];
                var segmentsForClass = new List<SegmentOption>();

                foreach (var building in roomsByBuilding)
                {
                    var buildingRooms = building.Value;
                    for (int start = 0; start < buildingRooms.Count; start++)
                    {
                        int capacitySum = 0;
                        var roomsInSegment = new List<AIExamClassRoomModelRoom>();

                        for (int end = start; end < buildingRooms.Count; end++)
                        {
                            var room = buildingRooms[end];
                            roomsInSegment.Add(room);
                            capacitySum += room.SeatCount;

                            int roomCount = roomsInSegment.Count;
                            if (capacitySum < examClass.StudentCount)
                            {
                                continue;
                            }

                            if (examClass.StudentCount < roomCount)
                            {
                                // Avoid segments where some rooms would receive zero students.
                                continue;
                            }

                            var distribution = DistributeStudentsEvenly(examClass.StudentCount, roomsInSegment);
                            if (distribution == null)
                            {
                                continue;
                            }

                            bool fitsAllRooms = true;
                            for (int idx = 0; idx < roomsInSegment.Count; idx++)
                            {
                                if (distribution[idx] > roomsInSegment[idx].SeatCount)
                                {
                                    fitsAllRooms = false;
                                    break;
                                }
                            }

                            if (!fitsAllRooms)
                            {
                                continue;
                            }

                            var assignments = new Dictionary<int, int>();
                            for (int idx = 0; idx < roomsInSegment.Count; idx++)
                            {
                                assignments[roomsInSegment[idx].ModelRoomId] = distribution[idx];
                            }

                            var segmentOption = new SegmentOption
                            {
                                BuildingId = building.Key,
                                Rooms = roomsInSegment.Select(r => r.ModelRoomId).ToList(),
                                RoomAssignments = assignments,
                                TotalCapacity = capacitySum
                            };

                            segmentsForClass.Add(segmentOption);
                        }
                    }
                }

                if (!segmentsForClass.Any())
                {
                    throw new InvalidOperationException($"No feasible room assignment found for class {examClass.ModelClassId}.");
                }

                classSegments.Add(segmentsForClass);
            }

            var cpModel = new CpModel();

            // Decision variables: whether a class chooses a specific segment.
            var segmentVariables = new BoolVar[orderedClasses.Count][];

            for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
            {
                var segmentsForClass = classSegments[classIndex];
                segmentVariables[classIndex] = new BoolVar[segmentsForClass.Count];
                var segmentSelection = new List<BoolVar>();

                for (int segmentIndex = 0; segmentIndex < segmentsForClass.Count; segmentIndex++)
                {
                    var boolVar = cpModel.NewBoolVar($"class_{classIndex}_segment_{segmentIndex}");
                    segmentVariables[classIndex][segmentIndex] = boolVar;
                    segmentSelection.Add(boolVar);
                }

                cpModel.Add(LinearExpr.Sum(segmentSelection) == 1);
            }

            // Indicator variables for class-room usage.
            var classRoomUsage = new Dictionary<(int classIndex, int roomId), BoolVar>();

            for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
            {
                var segmentsForClass = classSegments[classIndex];
                var roomToSegments = new Dictionary<int, List<BoolVar>>();

                for (int segmentIndex = 0; segmentIndex < segmentsForClass.Count; segmentIndex++)
                {
                    var segment = segmentsForClass[segmentIndex];
                    var segmentVar = segmentVariables[classIndex][segmentIndex];

                    foreach (var roomAssignment in segment.RoomAssignments)
                    {
                        int roomId = roomAssignment.Key;
                        if (roomAssignment.Value <= 0)
                        {
                            continue;
                        }

                        if (!roomToSegments.TryGetValue(roomId, out var list))
                        {
                            list = new List<BoolVar>();
                            roomToSegments[roomId] = list;
                        }

                        list.Add(segmentVar);
                    }
                }

                foreach (var kvp in roomToSegments)
                {
                    var usageVar = cpModel.NewBoolVar($"class_{classIndex}_room_{kvp.Key}");
                    classRoomUsage[(classIndex, kvp.Key)] = usageVar;

                    foreach (var segVar in kvp.Value)
                    {
                        cpModel.AddImplication(segVar, usageVar);
                    }

                    cpModel.Add(usageVar <= LinearExpr.Sum(kvp.Value));
                }
            }

            // Seat capacity constraints per room.
            foreach (var room in rooms)
            {
                var capacityExpr = LinearExpr.Sum(orderedClasses.Select((cls, classIndex) =>
                {
                    var segmentsForClass = classSegments[classIndex];
                    var terms = new List<LinearExpr>();
                    for (int segmentIndex = 0; segmentIndex < segmentsForClass.Count; segmentIndex++)
                    {
                        var segment = segmentsForClass[segmentIndex];
                        if (segment.RoomAssignments.TryGetValue(room.ModelRoomId, out int assigned))
                        {
                            terms.Add(segmentVariables[classIndex][segmentIndex] * assigned);
                        }
                    }

                    return terms.Any() ? LinearExpr.Sum(terms) : LinearExpr.Constant(0);
                }));

                cpModel.Add(capacityExpr <= room.SeatCount);
            }

            // Grade constraints: at most one class per grade per room and at most two grades per room.
            var grades = orderedClasses.Select(c => c.Grade).Distinct().ToList();
            var gradeUsageVars = new Dictionary<(int roomId, int grade), BoolVar>();

            foreach (var room in rooms)
            {
                var gradeVarsForRoom = new List<BoolVar>();

                foreach (var grade in grades)
                {
                    var relevantClasses = orderedClasses
                        .Select((cls, idx) => new { cls, idx })
                        .Where(x => x.cls.Grade == grade)
                        .Select(x => x.idx)
                        .ToList();

                    if (!relevantClasses.Any())
                    {
                        continue;
                    }

                    var classUsageVars = new List<BoolVar>();
                    foreach (var classIdx in relevantClasses)
                    {
                        if (classRoomUsage.TryGetValue((classIdx, room.ModelRoomId), out var usage))
                        {
                            classUsageVars.Add(usage);
                        }
                    }

                    if (!classUsageVars.Any())
                    {
                        continue;
                    }

                    var gradeVar = cpModel.NewBoolVar($"room_{room.ModelRoomId}_grade_{grade}");
                    gradeUsageVars[(room.ModelRoomId, grade)] = gradeVar;
                    gradeVarsForRoom.Add(gradeVar);

                    foreach (var usageVar in classUsageVars)
                    {
                        cpModel.AddImplication(usageVar, gradeVar);
                    }

                    cpModel.Add(gradeVar <= LinearExpr.Sum(classUsageVars));
                    cpModel.Add(LinearExpr.Sum(classUsageVars) <= 1);
                }

                if (gradeVarsForRoom.Any())
                {
                    cpModel.Add(LinearExpr.Sum(gradeVarsForRoom) <= 2);
                }
            }

            // Objective: minimize overall unused seats to prefer tighter fits and smaller rooms.
            var objectiveTerms = new List<LinearExpr>();
            for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
            {
                var examClass = orderedClasses[classIndex];
                for (int segmentIndex = 0; segmentIndex < classSegments[classIndex].Count; segmentIndex++)
                {
                    var segment = classSegments[classIndex][segmentIndex];
                    int waste = Math.Max(0, segment.TotalCapacity - examClass.StudentCount);
                    if (waste > 0)
                    {
                        objectiveTerms.Add(segmentVariables[classIndex][segmentIndex] * waste);
                    }
                }
            }

            if (objectiveTerms.Any())
            {
                cpModel.Minimize(LinearExpr.Sum(objectiveTerms));
            }

            var solver = new CpSolver
            {
                StringParameters = "max_time_in_seconds:30"
            };

            var status = solver.Solve(cpModel);

            if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
            {
                throw new InvalidOperationException("Unable to find a feasible room assignment satisfying all constraints.");
            }

            var results = new List<AIExamClassRoomResult>();

            for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
            {
                int chosenSegmentIndex = -1;
                for (int segmentIndex = 0; segmentIndex < classSegments[classIndex].Count; segmentIndex++)
                {
                    if (solver.BooleanValue(segmentVariables[classIndex][segmentIndex]))
                    {
                        chosenSegmentIndex = segmentIndex;
                        break;
                    }
                }

                if (chosenSegmentIndex < 0)
                {
                    throw new InvalidOperationException($"Solver failed to assign a segment for class {orderedClasses[classIndex].ModelClassId}.");
                }

                var chosenSegment = classSegments[classIndex][chosenSegmentIndex];
                var orderedRooms = chosenSegment.Rooms
                    .Select(roomId => roomById[roomId])
                    .OrderBy(r => r.RoomNo ?? r.ModelRoomId)
                    .ThenBy(r => r.ModelRoomId)
                    .ToList();

                foreach (var room in orderedRooms)
                {
                    int assigned = chosenSegment.RoomAssignments[room.ModelRoomId];
                    if (assigned <= 0)
                    {
                        continue;
                    }

                    results.Add(new AIExamClassRoomResult
                    {
                        ModelClassId = orderedClasses[classIndex].ModelClassId,
                        ModelRoomId = room.ModelRoomId,
                        ExamStudentCount = assigned
                    });
                }
            }

            return results;
        }

        private static int[]? DistributeStudentsEvenly(int studentCount, List<AIExamClassRoomModelRoom> rooms)
        {
            if (rooms == null || rooms.Count == 0)
            {
                return null;
            }

            int roomCount = rooms.Count;
            if (roomCount == 0)
            {
                return null;
            }

            int baseCount = studentCount / roomCount;
            int remainder = studentCount % roomCount;

            var distribution = new int[roomCount];
            for (int i = 0; i < roomCount; i++)
            {
                distribution[i] = baseCount + (i < remainder ? 1 : 0);
                if (distribution[i] <= 0)
                {
                    return null;
                }
            }

            return distribution;
        }

        private class SegmentOption
        {
            public int BuildingId { get; set; }

            public List<int> Rooms { get; set; } = new List<int>();

            public Dictionary<int, int> RoomAssignments { get; set; } = new Dictionary<int, int>();

            public int TotalCapacity { get; set; }
        }

    }
    public class AIExamClassRoomModel
    {

        /// <summary>
        /// 班级列表
        /// </summary>
        public List<AIExamClassRoomModelClass>? ModelClassList { get; set; }
        /// <summary>
        /// 考场列表
        /// </summary>
        public List<AIExamClassRoomModelRoom>? ModelRoomList { get; set; }

    }

    public class AIExamClassRoomModelClass
    {
        /// <summary>
        /// 班级年级
        /// </summary>
        public int Grade { get; set; }
        /// <summary>
        /// 班级id
        /// </summary>
        public int ModelClassId { get; set; }
        /// <summary>
        /// 班级名称
        /// </summary>
        public string? ModelClassName { get; set; }
        /// <summary>
        /// 班级人数
        /// </summary>
        public int StudentCount { get; set; }
    }

    public class AIExamClassRoomModelRoom
    {
        /// <summary>
        /// 考场id
        /// </summary>
        public int ModelRoomId { get; set; }
        /// <summary>
        /// 考场名称
        /// </summary>
        public string? ModelRoomName { get; set; }
        /// <summary>
        /// 教学楼id
        /// </summary>
        public int BuildingId { get; set; }
        /// <summary>
        /// 考场编号
        /// </summary>
        public int? RoomNo { get; set; }
        /// <summary>
        /// 座位数
        /// </summary>
        public int SeatCount { get; set; }
    }

    public class AIExamClassRoomResult
    {
        /// <summary>
        /// 班级id
        /// </summary>
        public int ModelClassId { get; set; }

        /// <summary>
        /// 考场id
        /// </summary>
        public int ModelRoomId { get; set; }

        /// <summary>
        /// 考场分配的考生人数
        /// </summary>
        public int ExamStudentCount { get; set; }

    }
}
