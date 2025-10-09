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

                            var segmentOption = new SegmentOption
                            {
                                BuildingId = building.Key,
                                Rooms = roomsInSegment.Select(r => r.ModelRoomId).ToList(),
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

                segmentsForClass = segmentsForClass
                    .OrderBy(s => s.Rooms.Count)
                    .ThenBy(s => s.TotalCapacity)
                    .ThenBy(s => string.Join("_", s.Rooms.OrderBy(id => id)))
                    .ToList();

                classSegments.Add(segmentsForClass);
            }

            var cpModel = new CpModel();

            // Decision variables: whether a class chooses a specific segment.
            var segmentVariables = new BoolVar[orderedClasses.Count][];
            var segmentRoomAssignments = new Dictionary<(int classIndex, int segmentIndex, int roomId), IntVar>();

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

                    var assignVars = new List<IntVar>();

                    foreach (var roomId in segment.Rooms)
                    {
                        var room = roomById[roomId];
                        var assignVar = cpModel.NewIntVar(0, room.SeatCount, $"class_{classIndex}_segment_{segmentIndex}_room_{roomId}_students");
                        segmentRoomAssignments[(classIndex, segmentIndex, roomId)] = assignVar;

                        cpModel.Add(assignVar == 0).OnlyEnforceIf(segmentVar.Not());
                        cpModel.Add(assignVar >= 1).OnlyEnforceIf(segmentVar);

                        assignVars.Add(assignVar);
                    }

                    cpModel.Add(LinearExpr.Sum(assignVars) == orderedClasses[classIndex].StudentCount).OnlyEnforceIf(segmentVar);

                    if (assignVars.Count > 0)
                    {
                        cpModel.Add(LinearExpr.Sum(assignVars) == 0).OnlyEnforceIf(segmentVar.Not());
                    }

                    for (int i = 0; i < segment.Rooms.Count; i++)
                    {
                        for (int j = i + 1; j < segment.Rooms.Count; j++)
                        {
                            var roomI = segment.Rooms[i];
                            var roomJ = segment.Rooms[j];
                            var assignI = segmentRoomAssignments[(classIndex, segmentIndex, roomI)];
                            var assignJ = segmentRoomAssignments[(classIndex, segmentIndex, roomJ)];

                            cpModel.Add(assignI - assignJ <= 1).OnlyEnforceIf(segmentVar);
                            cpModel.Add(assignJ - assignI <= 1).OnlyEnforceIf(segmentVar);
                        }
                    }

                    foreach (var roomId in segment.Rooms)
                    {
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

                    if (kvp.Value.Count > 0)
                    {
                        cpModel.Add(usageVar <= LinearExpr.Sum(kvp.Value));
                    }
                }
            }

            // Aggregate per-class room assignments and enforce usage links.
            var classRoomAssignments = new Dictionary<(int classIndex, int roomId), IntVar>();

            for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
            {
                foreach (var room in rooms)
                {
                    var relatedAssignments = new List<IntVar>();

                    for (int segmentIndex = 0; segmentIndex < classSegments[classIndex].Count; segmentIndex++)
                    {
                        if (!classSegments[classIndex][segmentIndex].Rooms.Contains(room.ModelRoomId))
                        {
                            continue;
                        }

                        if (segmentRoomAssignments.TryGetValue((classIndex, segmentIndex, room.ModelRoomId), out var assignVar))
                        {
                            relatedAssignments.Add(assignVar);
                        }
                    }

                    if (!relatedAssignments.Any())
                    {
                        continue;
                    }

                    var totalVar = cpModel.NewIntVar(0, room.SeatCount, $"class_{classIndex}_room_{room.ModelRoomId}_total");
                    cpModel.Add(totalVar == LinearExpr.Sum(relatedAssignments));
                    classRoomAssignments[(classIndex, room.ModelRoomId)] = totalVar;

                    var usageVar = classRoomUsage[(classIndex, room.ModelRoomId)];
                    cpModel.Add(totalVar >= 1).OnlyEnforceIf(usageVar);
                    cpModel.Add(totalVar == 0).OnlyEnforceIf(usageVar.Not());
                }
            }

            // Seat capacity constraints per room.
            var roomAssignedExpressions = new Dictionary<int, IntVar>();

            foreach (var room in rooms)
            {
                var perClassAssignments = new List<IntVar>();

                for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
                {
                    if (classRoomAssignments.TryGetValue((classIndex, room.ModelRoomId), out var assignment))
                    {
                        perClassAssignments.Add(assignment);
                    }
                }

                var totalAssigned = cpModel.NewIntVar(0, room.SeatCount, $"room_{room.ModelRoomId}_assigned_total");
                roomAssignedExpressions[room.ModelRoomId] = totalAssigned;

                if (perClassAssignments.Any())
                {
                    cpModel.Add(totalAssigned == LinearExpr.Sum(perClassAssignments));
                }
                else
                {
                    cpModel.Add(totalAssigned == 0);
                }

                cpModel.Add(totalAssigned <= room.SeatCount);
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

            foreach (var room in rooms)
            {
                if (!roomAssignedExpressions.TryGetValue(room.ModelRoomId, out var assignedVar))
                {
                    continue;
                }

                var slackVar = cpModel.NewIntVar(0, room.SeatCount, $"room_{room.ModelRoomId}_slack");

                cpModel.Add(assignedVar + slackVar == room.SeatCount);

                objectiveTerms.Add(slackVar * 1000);
            }

            for (int classIndex = 0; classIndex < orderedClasses.Count; classIndex++)
            {
                var examClass = orderedClasses[classIndex];
                for (int segmentIndex = 0; segmentIndex < classSegments[classIndex].Count; segmentIndex++)
                {
                    var segment = classSegments[classIndex][segmentIndex];
                    int segmentSizePenalty = segment.TotalCapacity;
                    int roomUsagePenalty = segment.Rooms.Count * 10;

                    objectiveTerms.Add(segmentVariables[classIndex][segmentIndex] * (segmentSizePenalty + roomUsagePenalty));
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
                    var assignVar = segmentRoomAssignments[(classIndex, chosenSegmentIndex, room.ModelRoomId)];
                    int assigned = (int)solver.Value(assignVar);
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

        private class SegmentOption
        {
            public int BuildingId { get; set; }

            public List<int> Rooms { get; set; } = new List<int>();

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
